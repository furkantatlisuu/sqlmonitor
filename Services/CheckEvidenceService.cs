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
        await using var conn = _factory.CreateTargetConnection(instance);
        await conn.OpenAsync(ct);

        return checkKey.ToLowerInvariant() switch
        {
            // PLE = bir sayfanın buffer pool'da kalma süresi. Sayfa
            // ancak yerine YENİ bir sayfa okunduğunda atılır, o da
            // DİSKTEN okumadır. Önbellekten okuma (logical read) PLE'yi
            // düşürmez - milyarlarca logical read yapan bir sorgu
            // bellek açısından masum olabilir.
            //
            // Bu sıralama bir kez total_logical_reads'ti ve YANILTTI:
            // gerçek PROD'da başa 8 TB okuyan ama diskten yalnızca
            // 104 KB çeken bir kimlik sorgusu çıkıyor, PLE'yi asıl
            // çökerten 155 milyon satırlık tablo taraması (2 çağrıda
            // 8,4 GB disk) listede hiç görünmüyordu.
            "memory" => await TopQueriesAsync(conn, "qs.total_physical_reads", "disk",
                $"Son {LookbackMinutes} dakikada çalışmış, EN ÇOK DİSKTEN OKUYAN sorgular. " +
                "PLE'yi düşüren budur: diskten gelen her sayfa, önbellekteki bir sayfanın " +
                "yerini alır. Önbellekten okuma (ikinci satırdaki sayı) çoktur ama PLE'ye " +
                "etkisi yoktur. Sayılar sorgu plan cache'e girdiğinden beri birikimlidir, " +
                "son çalışma zamanıyla birlikte okuyun.", ct),

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
        Microsoft.Data.SqlClient.SqlConnection conn, string orderBy, string metric,
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

    private async Task<EventEvidence> RunningRequestsAsync(
        Microsoft.Data.SqlClient.SqlConnection conn, CancellationToken ct)
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
        Microsoft.Data.SqlClient.SqlConnection conn, CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<BlockRow>(new CommandDefinition(
            DmvSql.BlockingChains, commandTimeout: _options.QueryTimeoutSeconds,
            cancellationToken: ct))).ToList();

        var evidence = HealthEvaluator.BuildBlockingEvidence(rows);
        evidence.Note = "Şu anda süren bloklama, zincirin tepesinden aşağıya.";
        return evidence;
    }
}
