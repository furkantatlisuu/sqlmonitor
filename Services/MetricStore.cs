using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlMonitor.Infrastructure;
using SqlMonitor.Models;
using SqlMonitor.Options;

namespace SqlMonitor.Services;

/// <summary>
/// SqlMonitorDb ile konuşan tek katman.
///
/// Buradaki her şey izleme uygulamasının KENDİ veritabanına gider;
/// izlenen prod sunucusuna değil. Bu ayrımı korumak önemli: izleme
/// verisi büyüdükçe prod sunucusunun yükü artmamalı.
/// </summary>
public sealed class MetricStore
{
    private readonly SqlConnectionFactory _factory;
    private readonly MonitorOptions _options;
    private readonly ILogger<MetricStore> _log;

    // Instance adı -> InstanceId eşlemesi. Her yazmada tabloya sormamak için.
    private readonly Dictionary<string, int> _instanceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _instanceLock = new(1, 1);

    public MetricStore(
        SqlConnectionFactory factory,
        IOptions<MonitorOptions> options,
        ILogger<MetricStore> log)
    {
        _factory = factory;
        _options = options.Value;
        _log = log;
    }

    public async Task<int> EnsureInstanceAsync(string name, string? displayName, CancellationToken ct)
    {
        if (_instanceIds.TryGetValue(name, out var cached)) return cached;

        await _instanceLock.WaitAsync(ct);
        try
        {
            if (_instanceIds.TryGetValue(name, out cached)) return cached;

            await using var conn = _factory.CreateStoreConnection();
            await conn.OpenAsync(ct);

            var parameters = new DynamicParameters();
            parameters.Add("@Name", name);
            parameters.Add("@DisplayName", displayName);
            parameters.Add("@InstanceId", dbType: DbType.Int32, direction: ParameterDirection.Output);

            await conn.ExecuteAsync(new CommandDefinition(
                "mon.usp_EnsureInstance", parameters,
                commandType: CommandType.StoredProcedure, cancellationToken: ct));

            var id = parameters.Get<int>("@InstanceId");
            _instanceIds[name] = id;
            return id;
        }
        finally
        {
            _instanceLock.Release();
        }
    }

    /// <summary>
    /// Metrik örneklerini toplu yazar.
    ///
    /// SqlBulkCopy yerine tablo değerli parametre benzeri bir toplu INSERT
    /// kullanmıyoruz; örnek sayısı turda 10-15 civarı, Dapper'ın dizi
    /// desteği yeterli ve okunabilirliği yüksek. Örnek sayısı yüzlere
    /// çıkarsa SqlBulkCopy'ye geçmek gerekir.
    /// </summary>
    public async Task SaveMetricsAsync(
        int instanceId, DateTime capturedAtUtc,
        IReadOnlyDictionary<string, decimal> metrics, CancellationToken ct)
    {
        if (metrics.Count == 0) return;

        const string sql = """
            INSERT INTO mon.MetricSample (InstanceId, MetricKey, CapturedAt, Value)
            VALUES (@InstanceId, @MetricKey, @CapturedAt, @Value);
            """;

        var rows = metrics.Select(kv => new
        {
            InstanceId = instanceId,
            MetricKey = kv.Key,
            CapturedAt = capturedAtUtc,
            Value = kv.Value
        }).ToArray();

        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, rows, cancellationToken: ct));
    }

    public async Task SaveWaitSampleAsync(
        int instanceId, DateTime capturedAtUtc,
        IReadOnlyList<WaitSampleRow> waits, CancellationToken ct)
    {
        if (waits.Count == 0) return;

        const string sql = """
            INSERT INTO mon.WaitSample
                (InstanceId, CapturedAt, WaitType, WaitTimeMs, SignalTimeMs, WaitingTasks)
            VALUES
                (@InstanceId, @CapturedAt, @WaitType, @WaitTimeMs, @SignalTimeMs, @WaitingTasks);
            """;

        var rows = waits.Select(w => new
        {
            InstanceId = instanceId,
            CapturedAt = capturedAtUtc,
            w.WaitType,
            w.WaitTimeMs,
            w.SignalTimeMs,
            w.WaitingTasks
        }).ToArray();

        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, rows, cancellationToken: ct));
    }

    /// <summary>KPI kutularındaki mini grafikler için son N örnek.</summary>
    public async Task<Dictionary<string, List<decimal>>> GetRecentTrendsAsync(
        string instanceName, string[] metricKeys, int points, CancellationToken ct)
    {
        var result = new Dictionary<string, List<decimal>>(StringComparer.OrdinalIgnoreCase);
        if (metricKeys.Length == 0) return result;

        const string sql = """
            DECLARE @InstanceId int = (SELECT InstanceId FROM mon.Instance WHERE Name = @Name);

            SELECT MetricKey, CapturedAt, Value
            FROM
            (
                SELECT
                    MetricKey, CapturedAt, Value,
                    rn = ROW_NUMBER() OVER (PARTITION BY MetricKey ORDER BY CapturedAt DESC)
                FROM mon.MetricSample
                WHERE InstanceId = @InstanceId
                  AND MetricKey IN @Keys
                  AND CapturedAt >= DATEADD(HOUR, -6, SYSUTCDATETIME())
            ) AS x
            WHERE rn <= @Points
            ORDER BY MetricKey, CapturedAt;
            """;

        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<TrendRow>(
            new CommandDefinition(sql,
                new { Name = instanceName, Keys = metricKeys, Points = points },
                cancellationToken: ct));

        foreach (var group in rows.GroupBy(r => r.MetricKey))
            result[group.Key] = group.Select(r => r.Value).ToList();

        return result;
    }

    /// <summary>
    /// Yukarıdaki serilerin kapsadığı gerçek zaman aralığı.
    ///
    /// Ayrı bir metot çünkü grafik verisi ile onun etiketi iki farklı
    /// sorudur; seri sözlüğüne zaman damgası sıkıştırmak yerine aralığı
    /// ayrıca hesaplıyoruz. Sorgu aynı pencereyi kullanır, bu yüzden
    /// sonuç grafikle tutarlıdır.
    /// </summary>
    public async Task<TrendWindow> GetTrendWindowAsync(
        string instanceName, string[] metricKeys, int points, CancellationToken ct)
    {
        var window = new TrendWindow();
        if (metricKeys.Length == 0) return window;

        const string sql = """
            DECLARE @InstanceId int = (SELECT InstanceId FROM mon.Instance WHERE Name = @Name);

            SELECT
                FromUtc = MIN(x.CapturedAt),
                ToUtc   = MAX(x.CapturedAt),
                Points  = MAX(x.rn)
            FROM
            (
                SELECT
                    MetricKey, CapturedAt,
                    rn = ROW_NUMBER() OVER (PARTITION BY MetricKey ORDER BY CapturedAt DESC)
                FROM mon.MetricSample
                WHERE InstanceId = @InstanceId
                  AND MetricKey IN @Keys
                  AND CapturedAt >= DATEADD(HOUR, -6, SYSUTCDATETIME())
            ) AS x
            WHERE x.rn <= @Points;
            """;

        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);

        var row = await conn.QuerySingleOrDefaultAsync<TrendWindowRow>(
            new CommandDefinition(sql,
                new { Name = instanceName, Keys = metricKeys, Points = points },
                cancellationToken: ct));

        if (row?.FromUtc is null || row.ToUtc is null) return window;

        window.FromUtc = row.FromUtc;
        window.ToUtc = row.ToUtc;
        window.Points = row.Points;
        window.SpanMinutes = (int)Math.Round((row.ToUtc.Value - row.FromUtc.Value).TotalMinutes);

        return window;
    }

    private sealed class TrendWindowRow
    {
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }
        public int Points { get; set; }
    }

    /// <summary>
    /// En çok "yükte" olan bekleme türlerinin, seçilen pencerede zaman
    /// içindeki ms/sn trendini döndürür.
    ///
    /// mon.WaitSample KÜMÜLATİF wait_time_ms tutar (toplayıcı her 30 sn'de
    /// bir yazar) - burada LAG() ile art arda iki örneklemenin farkını
    /// alıp geçen saniyeye bölüyoruz, CounterDeltaTracker'ın canlı ekranda
    /// yaptığının AYNISI, yalnızca geçmiş üzerinde. Sayaç geri gittiyse
    /// (SQL Server yeniden başlamış) o noktayı NULL bırakıp atlıyoruz -
    /// aksi hâlde dev negatif bir sıçrama grafiği bozardı.
    /// </summary>
    public async Task<WaitTrendResult> GetWaitTrendAsync(
        string instanceName, int hours, int topN, CancellationToken ct)
    {
        const string sql = """
            DECLARE @InstanceId int = (SELECT InstanceId FROM mon.Instance WHERE Name = @Name);

            ;WITH Deltas AS (
                SELECT
                    WaitType, CapturedAt, WaitTimeMs,
                    PrevWaitTimeMs = LAG(WaitTimeMs) OVER (PARTITION BY WaitType ORDER BY CapturedAt),
                    PrevCapturedAt = LAG(CapturedAt) OVER (PARTITION BY WaitType ORDER BY CapturedAt)
                FROM mon.WaitSample
                WHERE InstanceId = @InstanceId
                  AND CapturedAt >= DATEADD(HOUR, -@Hours, SYSUTCDATETIME())
            ),
            Rates AS (
                SELECT
                    WaitType, CapturedAt,
                    MsPerSec = CASE
                        WHEN PrevCapturedAt IS NULL THEN NULL
                        WHEN WaitTimeMs < PrevWaitTimeMs THEN NULL
                        WHEN DATEDIFF(SECOND, PrevCapturedAt, CapturedAt) <= 0 THEN NULL
                        ELSE (WaitTimeMs - PrevWaitTimeMs) * 1.0 / DATEDIFF(SECOND, PrevCapturedAt, CapturedAt)
                    END
                FROM Deltas
            ),
            TopTypes AS (
                SELECT TOP (@Top) WaitType
                FROM Rates
                WHERE MsPerSec IS NOT NULL
                GROUP BY WaitType
                ORDER BY AVG(MsPerSec) DESC
            )
            SELECT r.WaitType, r.CapturedAt, MsPerSec = ROUND(r.MsPerSec, 2)
            FROM Rates r
            JOIN TopTypes t ON t.WaitType = r.WaitType
            WHERE r.MsPerSec IS NOT NULL
            ORDER BY r.WaitType, r.CapturedAt;
            """;

        var result = new WaitTrendResult();

        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);

        var rows = (await conn.QueryAsync<WaitRateRow>(new CommandDefinition(
            sql, new { Name = instanceName, Hours = hours, Top = topN },
            cancellationToken: ct))).ToList();

        if (rows.Count == 0) return result;

        foreach (var group in rows.GroupBy(r => r.WaitType))
        {
            result.Series.Add(new WaitTrendSeries
            {
                WaitType = group.Key,
                Values = group.Select(r => r.MsPerSec).ToList()
            });
        }

        result.Trend.FromUtc = rows.Min(r => r.CapturedAt);
        result.Trend.ToUtc = rows.Max(r => r.CapturedAt);
        result.Trend.Points = rows.GroupBy(r => r.WaitType).Max(g => g.Count());
        result.Trend.SpanMinutes = (int)Math.Round(
            (result.Trend.ToUtc!.Value - result.Trend.FromUtc!.Value).TotalMinutes);

        return result;
    }

    private sealed class WaitRateRow
    {
        public string WaitType { get; set; } = "";
        public DateTime CapturedAt { get; set; }
        public decimal MsPerSec { get; set; }
    }

    /// <summary>
    /// En dolu hacmin geçmiş disk_used_pct örneklerinden basit doğrusal
    /// (en küçük kareler / OLS) bir projeksiyon çıkarır.
    ///
    /// En az 3 günlük geçmiş şart - daha kısası tek günlük dalgalanmaları
    /// (bir yedek job'ının o gün ürettiği geçici dosyalar gibi) gerçek bir
    /// büyüme trendiyle karıştırabilir. disk_used_pct yalnızca "o anda en
    /// dolu hacim" değeridir (bkz. CollectorService.ExtractMetrics) - en
    /// dolu hacim zaman içinde değişirse eğim biraz gürültülü olabilir,
    /// ama sunucu tek bir ana veri hacmine sahipse (çoğu kurulum) bu sorun
    /// çıkarmaz.
    /// </summary>
    /// <summary>Tahminin baktığı geçmiş penceresi - saklama süresiyle uyumlu, günlük ortalamaya indirgendiği için ~30 satır.</summary>
    private const int ForecastWindowDays = 30;

    public async Task<DiskForecast> GetDiskForecastAsync(string instanceName, CancellationToken ct)
    {
        // GÜNLÜK ORTALAMAYA indirgiyoruz. Eskiden HER örneklem ham hâlde
        // çekiliyordu: 30 saniyelik toplayıcı + 30 günlük saklama = instance
        // başına ~86.000 satır, ÜSTELİK her canlı snapshot'ta yeniden (yani
        // ekran açıkken 5 saniyede bir). Regresyon için bu çözünürlük zaten
        // gereksiz - günlük ortalama hem ~30 satıra iniyor hem de gün içi
        // dalgalanmayı (yedek job'ının geçici dosyaları gibi) düzleştirdiği
        // için eğim DAHA sağlıklı çıkıyor.
        const string sql = """
            DECLARE @InstanceId int = (SELECT InstanceId FROM mon.Instance WHERE Name = @Name);

            SELECT
                CapturedAt = CAST(CAST(CapturedAt AS date) AS datetime2(0)),
                Value      = AVG(Value)
            FROM mon.MetricSample
            WHERE InstanceId = @InstanceId
              AND MetricKey = 'disk_used_pct'
              AND CapturedAt >= DATEADD(DAY, -@Days, SYSUTCDATETIME())
            GROUP BY CAST(CapturedAt AS date)
            ORDER BY 1;
            """;

        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);

        var rows = (await conn.QueryAsync<DiskSampleRow>(new CommandDefinition(
            sql, new { Name = instanceName, Days = ForecastWindowDays },
            cancellationToken: ct))).ToList();

        if (rows.Count < 2)
            return new DiskForecast { HasEnoughHistory = false };

        var firstAt = rows[0].CapturedAt;
        var historyDays = (rows[^1].CapturedAt - firstAt).TotalDays;
        var current = rows[^1].Value;

        if (historyDays < 3)
            return new DiskForecast
            {
                HasEnoughHistory = false,
                HistoryDays = (int)Math.Floor(historyDays),
                CurrentPercent = current
            };

        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        var n = rows.Count;
        foreach (var r in rows)
        {
            var x = (r.CapturedAt - firstAt).TotalDays;
            var y = (double)r.Value;
            sumX += x; sumY += y; sumXY += x * y; sumXX += x * x;
        }

        var denominator = n * sumXX - sumX * sumX;
        if (denominator == 0)
            return new DiskForecast
            {
                HasEnoughHistory = false,
                HistoryDays = (int)Math.Floor(historyDays),
                CurrentPercent = current
            };

        var slopePerDay = (n * sumXY - sumX * sumY) / denominator;

        int? daysToFull = null;
        if (slopePerDay > 0.01 && current < 100)
        {
            var days = (100 - (double)current) / slopePerDay;
            // 10 yıldan uzun bir "tahmin" göstermenin anlamı yok - pratikte hiç büyümüyor demektir.
            if (days is > 0 and < 3650)
                daysToFull = (int)Math.Ceiling(days);
        }

        return new DiskForecast
        {
            HasEnoughHistory = true,
            HistoryDays = (int)Math.Floor(historyDays),
            CurrentPercent = current,
            SlopePerDayPercent = Math.Round((decimal)slopePerDay, 3),
            DaysToFull = daysToFull
        };
    }

    private sealed class DiskSampleRow
    {
        public DateTime CapturedAt { get; set; }
        public decimal Value { get; set; }
    }

    /// <summary>Ekranın alt kısmındaki olay zaman çizelgesi.</summary>
    public async Task<List<TimelineEntry>> GetTimelineAsync(
        string instanceName, int hours, CancellationToken ct)
    {
        const string sql = """
            SELECT TOP (100)
                EventId, OccurredAtUtc = OccurredAt, RuleKey,
                Severity, PrevSeverity, Title, Detail,
                -- Kanıdın kendisi değil, yalnızca varlığı: ekran bununla
                -- satırı tıklanabilir yapıyor, içeriği tıklanınca çekiyor.
                HasDetail = CONVERT(bit, CASE WHEN e.Evidence IS NULL THEN 0 ELSE 1 END)
            FROM mon.HealthEvent AS e
            JOIN mon.Instance   AS i ON i.InstanceId = e.InstanceId
            WHERE i.Name = @Name
              AND e.OccurredAt >= DATEADD(HOUR, -@Hours, SYSUTCDATETIME())
            ORDER BY e.OccurredAt DESC;
            """;

        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<TimelineEntry>(
            new CommandDefinition(sql, new { Name = instanceName, Hours = hours },
                cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// Kural durumu değiştiyse olay yazar - değişmediyse hiçbir şey yazmaz.
    /// Zaman çizelgesinin değerli olması için yalnızca geçişleri içermesi
    /// gerekir; her örneklemeyi yazsaydık günde 2880 satırlık bir çöplük
    /// olurdu ve kimse bakmazdı.
    ///
    /// Durum değiştiyse (ya da ilk kez sağlıksız görüldüyse) mon.HealthEvent'e
    /// yazar ve önceki severity'yi döner - null dönerse hiçbir değişiklik
    /// yok demektir. CollectorService bu dönüş değerini Slack bildirimi
    /// gönderip göndermeyeceğine (ve "kritikleşti" mi "düzeldi" mi
    /// olduğuna) karar vermek için kullanıyor - ayrı bir "son bilinen
    /// severity" defteri tutmuyoruz, mon.HealthEvent zaten bunu içeriyor.
    /// </summary>
    public async Task<Severity?> RecordEventIfChangedAsync(
        int instanceId, string ruleKey, Severity current,
        string title, string? detail, string? evidenceJson, CancellationToken ct)
    {
        const string sql = """
            DECLARE @Prev tinyint =
            (
                SELECT TOP (1) Severity
                FROM mon.HealthEvent
                WHERE InstanceId = @InstanceId AND RuleKey = @RuleKey
                ORDER BY OccurredAt DESC, EventId DESC
            );

            IF @Prev IS NULL AND @Severity = 0
                RETURN;   -- ilk görüşte sağlıklıysa kayda değmez

            IF @Prev IS NOT NULL AND @Prev = @Severity
                RETURN;   -- durum değişmedi

            INSERT INTO mon.HealthEvent
                (InstanceId, OccurredAt, RuleKey, Severity, PrevSeverity, Title, Detail, Evidence)
            OUTPUT INSERTED.PrevSeverity
            VALUES
                (@InstanceId, SYSUTCDATETIME(), @RuleKey, @Severity, ISNULL(@Prev, 0), @Title, @Detail, @Evidence);
            """;

        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);

        var prev = await conn.ExecuteScalarAsync<byte?>(new CommandDefinition(sql, new
        {
            InstanceId = instanceId,
            RuleKey = ruleKey,
            Severity = (byte)current,
            Title = title,
            Detail = detail,
            Evidence = evidenceJson
        }, cancellationToken: ct));

        return prev is byte b ? (Severity)b : null;
    }

    /// <summary>
    /// Tek bir olayın kanıt satırları - kullanıcı zaman çizelgesinde o
    /// olaya tıkladığında.
    ///
    /// instanceName filtresi güvenlik değil, DOĞRULUK içindir: EventId
    /// global bir sayaç, ekran hangi sunucuya bakıyorsa o sunucunun
    /// olayını istiyor. Eşleşmezse boş dönüyoruz - başka bir sunucunun
    /// sorgu metnini yanlışlıkla göstermektense hiç göstermemek iyidir.
    /// </summary>
    public async Task<List<EvidenceRow>> GetEventEvidenceAsync(
        string instanceName, long eventId, CancellationToken ct)
    {
        const string sql = """
            SELECT e.Evidence
            FROM mon.HealthEvent AS e
            JOIN mon.Instance    AS i ON i.InstanceId = e.InstanceId
            WHERE e.EventId = @EventId AND i.Name = @Name;
            """;

        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);

        var json = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            sql, new { Name = instanceName, EventId = eventId }, cancellationToken: ct));

        if (string.IsNullOrWhiteSpace(json)) return new List<EvidenceRow>();

        try
        {
            return JsonSerializer.Deserialize<List<EvidenceRow>>(json, EvidenceJson)
                   ?? new List<EvidenceRow>();
        }
        catch (JsonException ex)
        {
            // Eski/bozuk bir satır yüzünden ekranın patlamasına değmez.
            _log.LogWarning(ex, "Olay {EventId} kanıtı okunamadı", eventId);
            return new List<EvidenceRow>();
        }
    }

    /// <summary>
    /// Kanıt JSON'u ekrana da aynı adlarla gidiyor (ASP.NET Core'un
    /// varsayılanı camelCase), depoda da öyle dursun - iki yerde iki
    /// farklı yazım, ileride birinin sessizce boş gelmesi demek olurdu.
    /// </summary>
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string? SerializeEvidence(List<EvidenceRow>? rows)
        => rows is null || rows.Count == 0
            ? null
            : JsonSerializer.Serialize(rows, EvidenceJson);

    public async Task LogCollectorRunAsync(
        int instanceId, DateTime startedAt, int durationMs,
        bool succeeded, string? error, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO mon.CollectorRun (InstanceId, StartedAt, DurationMs, Succeeded, ErrorText)
            VALUES (@InstanceId, @StartedAt, @DurationMs, @Succeeded, @ErrorText);
            """;

        try
        {
            await using var conn = _factory.CreateStoreConnection();
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                InstanceId = instanceId,
                StartedAt = startedAt,
                DurationMs = durationMs,
                Succeeded = succeeded,
                ErrorText = error?.Length > 1900 ? error[..1900] : error
            }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            // Günlük yazamamak, toplamayı durdurmak için sebep değil.
            _log.LogWarning(ex, "Toplayıcı çalışma günlüğü yazılamadı");
        }
    }

    private sealed class TrendRow
    {
        public string MetricKey { get; set; } = "";
        public DateTime CapturedAt { get; set; }
        public decimal Value { get; set; }
    }

    public async Task PurgeAsync(CancellationToken ct)
    {
        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "mon.usp_PurgeOldData",
            new { RetentionDays = _options.RetentionDays },
            commandType: CommandType.StoredProcedure,
            commandTimeout: 300,
            cancellationToken: ct));
    }
}
