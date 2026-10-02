using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Options;
using SqlMonitor.Infrastructure;
using SqlMonitor.Models;
using SqlMonitor.Options;
using SqlMonitor.Sql;

namespace SqlMonitor.Services;

/// <summary>
/// "Bu uyarı neden çıktı, hangi sorgudan kaynaklanıyor?"
///
/// Sağlık kontrolü kartına tıklandığında ÇALIŞMA ANINDA sorulan kanıt.
/// mon.HealthEvent'teki kanıttan (bkz. HealthEvaluator.TakeEvidence)
/// farkı şu: orası GEÇMİŞ bir olayın donmuş fotoğrafı, burası ŞU AN.
/// Kart canlı bir durumu gösteriyor, kanıt da canlı olmalı.
///
/// BİLEREK snapshot'ın dışında: saniyede bir kimse "neden" diye
/// sormuyor. Kullanıcı tıkladığında, yalnızca o kontrol için çalışır.
///
/// Her kontrolün sorgu düzeyinde bir cevabı YOKTUR. Disk doluluğunun,
/// yedeklerin ya da Always On'un arkasında gösterilecek bir sorgu yok;
/// olmayan bir kanıt uydurmaktansa o kartları tıklanamaz bırakıyoruz.
/// </summary>
public sealed class CheckEvidenceService
{
    private readonly SqlConnectionFactory _factory;
    private readonly MonitorOptions _options;

    public CheckEvidenceService(SqlConnectionFactory factory, IOptions<MonitorOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    /// <summary>
    /// Kanıtı olan kontroller. Ekran bunu kullanarak hangi kartın
    /// tıklanabilir görüneceğine karar veriyor - kullanıcı tıklayıp
    /// "kanıt yok" yazısıyla karşılaşmasın.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "memory", "cpu", "long_running", "blocking"
        };

    public static bool Supports(string key) => SupportedKeys.Contains(key);

    /// <summary>
    /// Plan cache sayaçları birikimli olduğu için pencere şart
    /// (bkz. DmvSql.TopQueriesTemplate). 60 dakika: "bugün olan bitenin"
    /// içinde, ama tek bir ani sıçramayı da yutmayacak kadar dar.
    /// </summary>
    private const int LookbackMinutes = 60;

    /// <summary>
    /// 5 satır: suçluyu bulmaya fazlasıyla yeter ve panel okunabilir
    /// kalır. 10 denendi - her satır kendi SQL metnini taşıdığı için
    /// panel 2500 piksele çıkıyordu, kimsenin sonuna kadar ineceği bir
    /// liste değil.
    /// </summary>
    private const int TopQueries = 5;

    public async Task<EventEvidence?> GetAsync(
        InstanceOptions instance, string checkKey, CancellationToken ct)
    {
        await using var conn = _factory.CreateTargetDbConnection(instance);
        await conn.OpenAsync(ct);

        // PostgreSQL'in kendi sorguları. Kanıt üretebildiği iki kontrol
        // var: bloklama ve uzun süren sorgu - ikisinin de cevabı canlı
        // oturumlarda. "memory"/"cpu" burada YOK, çünkü PostgreSQL o
        // kontrolleri hiç üretmiyor (bkz. PostgresMonitorService).
        if (DbEngine.IsPostgres(instance.Engine))
        {
            return checkKey.ToLowerInvariant() switch
            {
                "long_running" => await PgRunningRequestsAsync(conn, ct),
                "blocking"     => await PgBlockingAsync(conn, ct),
                _ => null
            };
        }

        return checkKey.ToLowerInvariant() switch
        {
            // PLE = bir sayfanın buffer pool'da kalma süresi. Sayfa
            // ancak yerine YENİ bir sayfa okunduğunda atılır, o da
            // DİSKTEN okumadır. Önbellekten okuma (logical read) PLE'yi
            // düşürmez - milyarlarca logical read yapan bir sorgu
            // bellek açısından masum olabilir.
            //
            // Sıralama ÇAĞRI BAŞINA, toplama göre değil - iki kez
            // düzeltildi, ikisi de gerçek PROD olayından öğrenildi:
            //
            // 1) Önce total_logical_reads'ti: 8 TB okuyan ama diskten
            //    yalnızca 104 KB çeken bir kimlik sorgusu başa çıkıyordu.
            // 2) Sonra total_physical_reads oldu ve YİNE yanılttı. PLE
            //    4121'den 160'a düştüğünde liste SP_CreatePurchaseInvoice
            //    gibi prosedürleri "9,31 GB diskten" diye gösterdi - ama
            //    o 9 GB 142 BİN çağrıda, GÜNLER içinde birikmişti; çağrı
            //    başına 0,07 MB. Havuzu boşaltan asıl sorgu TEK çağrıda
            //    2,8 GB okuyan bir admin sayfalama prosedürüydü ve
            //    toplama göre sıralı listede 8. sıradaydı.
            //
            // Plan cache sayaçları plan oluştuğundan beri birikimli;
            // "son 60 dakika" filtresi yalnızca SON ÇALIŞMA zamanına
            // bakar, sayaçları o pencereye KISITLAYAMAZ. O yüzden
            // toplamlar penceresiz, çağrı başına değer ise gerçek:
            // havuzu bir çırpıda boşaltan şey, tek seferde çok sayfa
            // okuyan sorgudur.
            "memory" => await TopQueriesAsync(conn,
                "(qs.total_physical_reads / qs.execution_count)", "disk",
                $"Son {LookbackMinutes} dakikada çalışmış, ÇAĞRI BAŞINA en çok diskten " +
                "okuyan sorgular. PLE'yi çökerten budur: tek seferde çok sayfa okuyan bir " +
                "sorgu, önbellekteki her şeyin yerini alır. Toplam okuması büyük ama çağrı " +
                "başına küçük olan sorgular (günde yüz binlerce kez çalışan ucuz sorgular) " +
                "PLE'yi düşürmez - o yüzden listede altta kalırlar. Sayılar plan cache'e " +
                "girildiğinden beri birikimlidir.", ct),

            "cpu" => await TopQueriesAsync(conn, "qs.total_worker_time", "cpu",
                $"Son {LookbackMinutes} dakikada çalışmış, EN ÇOK CPU harcayan sorgular. " +
                "Sayılar sorgu plan cache'e girdiğinden beri birikimlidir, son çalışma " +
                "zamanıyla birlikte okuyun.", ct),

            // Bu ikisinde cevap plan cache'te değil, ŞU ANDA açık olan
            // oturumlarda. Zaten canlı bir durumu anlatıyorlar.
            "long_running" => await RunningRequestsAsync(conn, ct),
            "blocking"     => await BlockingAsync(conn, ct),

            _ => null
        };
    }

    private async Task<EventEvidence> TopQueriesAsync(
        DbConnection conn, string orderBy, string metric,
        string note, CancellationToken ct)
    {
        // orderBy çağıranın kendi sabit listesinden geliyor, kullanıcıdan
        // değil - yine de string birleştirme yaptığımız için burada
        // bırakılan bir not: bu iki değerin dışına çıkılmamalı.
        var sql = DmvSql.TopQueriesTemplate.Replace("{ORDER_BY}", orderBy);

        var rows = (await conn.QueryAsync<QueryEvidenceRow>(new CommandDefinition(
            sql, new { Top = TopQueries, Minutes = LookbackMinutes },
            commandTimeout: _options.QueryTimeoutSeconds, cancellationToken: ct))).ToList();

        return new EventEvidence
        {
            Kind = EventEvidence.KindQueries,
            Queries = rows,
            Metric = metric,
            Note = note
        };
    }

    private async Task<EventEvidence> PgRunningRequestsAsync(DbConnection conn, CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<RequestRow>(new CommandDefinition(
            PgSql.ActiveRequests, commandTimeout: _options.QueryTimeoutSeconds,
            cancellationToken: ct))).ToList();

        // Yalnızca GERÇEKTEN çalışanlar: pg_stat_activity idle bir
        // bağlantıda son sorgunun query_start'ını tutmaya devam eder,
        // onları da katsaydık saatlerdir "çalışıyor" görünürlerdi.
        var evidence = HealthEvaluator.BuildRequestEvidence(
            rows.Where(r => r.Status == "active"));
        evidence.Note = "Şu anda çalışan istekler, en uzun sürenden kısaya.";
        return evidence;
    }

    private async Task<EventEvidence> PgBlockingAsync(DbConnection conn, CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<BlockRow>(new CommandDefinition(
            PgSql.BlockingChains, commandTimeout: _options.QueryTimeoutSeconds,
            cancellationToken: ct))).ToList();

        var evidence = HealthEvaluator.BuildBlockingEvidence(rows);
        evidence.Note = "Şu anda süren bloklama, zincirin tepesinden aşağıya.";
        return evidence;
    }

    private async Task<EventEvidence> RunningRequestsAsync(
        DbConnection conn, CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<RequestRow>(new CommandDefinition(
            DmvSql.ActiveRequests, commandTimeout: _options.QueryTimeoutSeconds,
            cancellationToken: ct))).ToList();

        return new EventEvidence
        {
            Kind = EventEvidence.KindRequests,
            Requests = rows
                .OrderByDescending(r => r.ElapsedMs)
                .Take(TopQueries)
                .Select(r => new EvidenceRow
                {
                    SessionId = r.SessionId,
                    ObjectName = r.ObjectName,
                    SqlText = r.SqlText,
                    DatabaseName = r.DatabaseName,
                    LoginName = r.LoginName,
                    HostName = r.HostName,
                    ProgramName = r.ProgramName,
                    Status = r.Status,
                    WaitType = r.WaitType,
                    ElapsedSeconds = r.ElapsedMs / 1000,
                    CpuMs = r.CpuMs,
                    LogicalReads = r.LogicalReads,
                    BlockedBy = r.BlockedBy
                })
                .ToList(),
            Note = "Şu anda çalışan istekler, en uzun sürenden kısaya."
        };
    }

    private async Task<EventEvidence> BlockingAsync(
        DbConnection conn, CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<BlockRow>(new CommandDefinition(
            DmvSql.BlockingChains, commandTimeout: _options.QueryTimeoutSeconds,
            cancellationToken: ct))).ToList();

        var evidence = HealthEvaluator.BuildBlockingEvidence(rows);
        evidence.Note = "Şu anda süren bloklama, zincirin tepesinden aşağıya.";
        return evidence;
    }
}
