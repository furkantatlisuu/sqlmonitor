using Microsoft.Data.SqlClient;
using SqlMonitor.Sql;

namespace SqlMonitor.Infrastructure;

/// <summary>
/// Açılışta izleme veritabanını (SqlMonitorDb) kendiliğinden kurar.
///
/// Kullanıcı isteği: "projeyi çalıştırınca kendisi oluştursun, varsa
/// bişey yapmasın" - db/01_create_monitordb.sql'i elle çalıştırma
/// zorunluluğu ortadan kalksın. StoreSchemaSql.CreateMonitorDb TAMAMEN
/// idempotent (her CREATE "yoksa oluştur" koruması taşıyor), bu sınıf
/// yalnızca onu her açılışta çalıştırır - veritabanı zaten kuruluysa
/// hiçbir şey değişmez, yalnızca birkaç ms'lik ucuz bir kontrol turu olur.
/// </summary>
public static class StoreSchemaInitializer
{
    /// <summary>
    /// Hata durumunda uygulamanın AÇILIŞINI DURDURMAZ - yalnızca loglar.
    /// Sebep: izleme veritabanına yazamamak (örn. yetki yok, sunucu
    /// erişilemez) canlı izleme ekranının çalışmasını engellememeli; o
    /// ekran izlenen sunucuya bağlanır, SqlMonitorDb'ye değil. Yalnızca
    /// trend/geçmiş özellikleri (zaten kendi try/catch'leriyle korunuyor)
    /// etkilenir.
    /// </summary>
    public static async Task EnsureCreatedAsync(
        string storeConnectionString, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storeConnectionString))
            return;   // Program.cs zaten bunun için ayrı bir uyarı basıyor.

        string databaseName;
        try
        {
            databaseName = new SqlConnectionStringBuilder(storeConnectionString).InitialCatalog;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Monitor:StoreConnectionString çözümlenemedi - izleme veritabanı kurulamıyor.");
            return;
        }

        if (string.IsNullOrWhiteSpace(databaseName))
            databaseName = "SqlMonitorDb";

        // Ad, betiğe hem string sabiti hem tanımlayıcı olarak gömülüyor;
        // köşeli parantez/tırnak taşıyan bir ad betiği bozar (ve enjeksiyon
        // kapısı olur). Bu isim kendi yapılandırma dosyamızdan geliyor,
        // yine de gömmeden önce doğruluyoruz - ucuz ve kesin.
        if (!IsSafeDatabaseName(databaseName))
        {
            logger.LogError(
                "Monitor:StoreConnectionString içindeki veritabanı adı ('{Db}') güvenli değil - " +
                "yalnızca harf, rakam, _ ve - kullan. İzleme veritabanı kurulmadı.", databaseName);
            return;
        }

        var script = StoreSchemaSql.CreateMonitorDb
            .Replace("{{DBNAME}}", databaseName)
            .Replace("{{DBQ}}", "[" + databaseName + "]");

        var batches = SplitIntoBatches(script);

        try
        {
            var bootstrapConnectionString = SqlConnectionFactory.WithMasterCatalog(storeConnectionString);
            await using var conn = new SqlConnection(bootstrapConnectionString);
            await conn.OpenAsync(ct);

            foreach (var batch in batches)
            {
                await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 30 };
                await cmd.ExecuteNonQueryAsync(ct);
            }

            logger.LogInformation("İzleme veritabanı {Db} hazır (zaten kuruluysa dokunulmadı).", databaseName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "İzleme veritabanı {Db} otomatik kurulamadı - trend/geçmiş, sunucu yönetimi ve " +
                "hatırlanan girişler çalışmayacak. Kullanıcının CREATE DATABASE yetkisi var mı?",
                databaseName);
            return;
        }

        // Kurulum "başarılı" görünse bile ASIL soru şu: uygulamanın kendi
        // bağlantı dizesiyle o veritabanı gerçekten açılabiliyor mu?
        // Eskiden bu doğrulanmıyordu ve ad uyuşmazlığı sessiz kalıyordu:
        // log "hazır" diyor, arkasından her şey "Cannot open database" ile
        // patlıyordu. Artık sorun buradan, tek ve anlaşılır bir satırla çıkıyor.
        try
        {
            await using var verify = new SqlConnection(storeConnectionString);
            await verify.OpenAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "İzleme veritabanı {Db} kuruldu ama uygulamanın bağlantı dizesiyle AÇILAMIYOR. " +
                "Monitor:StoreConnectionString içindeki Database/yetki ayarlarını kontrol et.",
                databaseName);
        }
    }

    /// <summary>Betiğe gömülecek veritabanı adı için katı (ve fazlasıyla yeterli) bir alt küme.</summary>
    private static bool IsSafeDatabaseName(string name) =>
        name.Length <= 128 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    /// <summary>
    /// GO satırlarından böler - ADO.NET'in SqlCommand'ı GO'yu tanımıyor,
    /// her biri kendi başına bir T-SQL batch'i olarak çalıştırılmalı.
    /// Satırın TAMAMI (baştaki/sondaki boşluklar hariç) "GO" olmalı -
    /// bir yorum ya da string içindeki "GO" kelimesi yanlışlıkla bölmesin.
    /// </summary>
    private static List<string> SplitIntoBatches(string script)
    {
        var batches = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                var batch = current.ToString().Trim();
                if (batch.Length > 0) batches.Add(batch);
                current.Clear();
            }
            else
            {
                current.AppendLine(line);
            }
        }

        var last = current.ToString().Trim();
        if (last.Length > 0) batches.Add(last);

        return batches;
    }
}
