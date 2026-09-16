using System.Data;
using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlMonitor.Infrastructure;
using SqlMonitor.Models;
using SqlMonitor.Options;
using SqlMonitor.Sql;

namespace SqlMonitor.Services;

/// <summary>
/// İzlenen sunucudan okuyup ekranın modelini kuran servis.
///
/// Tasarım kararı: her panel kendi try/catch'i içinde çalışır. Bir DMV
/// yetki hatası verdiğinde ya da zaman aşımına uğradığında ekranın
/// tamamı değil, yalnızca o panel kaybolur. İzleme aracının kendisi
/// kırılgan olursa kimse ona güvenmez.
/// </summary>
public sealed class LiveMonitorService
{
    private readonly SqlConnectionFactory _factory;
    private readonly CounterDeltaTracker _counters;
    private readonly StreakTracker _streaks;
    private readonly MetricStore _store;
    private readonly HealthEvaluator _health;
    private readonly MonitorOptions _options;
    private readonly ILogger<LiveMonitorService> _log;

    public LiveMonitorService(
        SqlConnectionFactory factory,
        CounterDeltaTracker counters,
        StreakTracker streaks,
        MetricStore store,
        HealthEvaluator health,
        IOptions<MonitorOptions> options,
        ILogger<LiveMonitorService> log)
    {
        _factory = factory;
        _counters = counters;
        _streaks = streaks;
        _store = store;
        _health = health;
        _options = options.Value;
        _log = log;
    }

    /// <param name="scope">
    /// Sayac farki icin tuketici kimligi. Arayuz "ui", toplayici "collector"
    /// gecer; boylece iki tuketici birbirinin olcum penceresini bozmaz.
    /// </param>
    public async Task<LiveSnapshot> GetSnapshotAsync(
        InstanceOptions instance, CancellationToken ct, string scope = "ui")
    {
        var sw = Stopwatch.StartNew();
        var snapshot = new LiveSnapshot
        {
            InstanceName = instance.Name,
            CapturedAtUtc = DateTime.UtcNow
        };

        await using var conn = _factory.CreateTargetConnection(instance);
        await conn.OpenAsync(ct);

        // Paneller sırayla okunuyor, paralel değil. Tek bir bağlantı
        // üzerinden sıralı okumak, izlenen sunucuya aynı anda 10 bağlantı
        // açmaktan çok daha nazik bir davranış.
        var counters = await SafeAsync(snapshot, "counters",
            () => ReadCountersAsync(conn, instance.Name, ct));

        snapshot.Server = await SafeAsync(snapshot, "server",
            () => ReadServerInfoAsync(conn, ct)) ?? new ServerInfo();

        snapshot.Cpu = await SafeAsync(snapshot, "cpu",
            () => ReadCpuAsync(conn, instance.Name, scope, ct)) ?? new CpuPanel();

        snapshot.Memory = await SafeAsync(snapshot, "memory",
            () => ReadMemoryAsync(conn, counters, ct)) ?? new MemoryPanel();

        snapshot.TempDb = await SafeAsync(snapshot, "tempdb",
            () => ReadTempDbAsync(conn, ct)) ?? new TempDbPanel();

        snapshot.Disk = await SafeAsync(snapshot, "disk",
            () => ReadDiskAsync(conn, instance.Name, scope, ct)) ?? new DiskPanel();

        snapshot.Io = await SafeAsync(snapshot, "io",
            () => ReadIoAsync(conn, instance.Name, scope, ct)) ?? new IoPanel();

        snapshot.Activity = await SafeAsync(snapshot, "activity",
            () => ReadActivityAsync(conn, ct)) ?? new ActivityPanel();

        snapshot.Blocking = await SafeAsync(snapshot, "blocking",
            () => ReadBlockingAsync(conn, ct)) ?? new BlockingPanel();

        // Bekleme istatistikleri GERİ EKLENDİ (2026-09-14) - Genel Bakış'taki
        // "Bekleme" kartını besliyor. sys.dm_os_wait_stats tek başına ucuz
        // bir DMV (CROSS APPLY yok) - CPU/I-O düzeltmelerindeki delta deseni
        // burada da geçerli, saniyelik döngüye eklemek sorun değil.
        //
        // YALNIZCA scope=="ui" İÇİN ÇALIŞIYOR: toplayıcı (scope="collector")
        // snapshot.Waits'i HİÇ OKUMUYOR - ExtractMetrics ona hiç dokunmuyor,
        // kendi kalıcı bekleme kaydını ayrıca ReadRawWaitsAsync'ten alıyor
        // (bkz. altındaki not). Toplayıcı için de hesaplamak, kullanılmayan
        // bir sonuç için gereksiz bir DMV turu + CounterDeltaTracker
        // defteri tutmak olurdu - "izlediğin sunucuyu boşuna yorma"
        // ilkesine aykırı, bu yüzden BİLEREK atlanıyor.
        //
        // Toplayıcının ham bekleme kaydı (ReadRawWaitsAsync -> mon.WaitSample)
        // AYRI ve DEĞİŞMEDİ; bu ikisi aynı DmvSql.WaitStats sorgusunu
        // paylaşıyor ama farklı amaçlarla (biri kalıcı geçmiş, biri anlık kart).
        snapshot.Waits = scope == "ui"
            ? await SafeAsync(snapshot, "waits", () => ReadWaitsAsync(conn, instance.Name, scope, ct)) ?? new WaitStatsPanel()
            : new WaitStatsPanel();

        snapshot.Kpis = BuildKpis(snapshot, counters, scope);

        // Sağlık hükmü en son: diğer bütün panellerin sonucuna bakar.
        snapshot.Health = await SafeAsync(snapshot, "health",
            () => _health.EvaluateAsync(conn, instance, snapshot, ct)) ?? new HealthPanel();

        // Sparkline verisi izleme DB'sinden. Burası patlarsa grafikler
        // düz çizgi olur, geri kalan ekran çalışmaya devam eder.
        await SafeAsync(snapshot, "trend", async () =>
        {
            await AttachTrendsAsync(instance.Name, snapshot, ct);
            return true;
        });

        sw.Stop();
        snapshot.ElapsedMs = (int)sw.ElapsedMilliseconds;
        return snapshot;
    }

    /// <summary>
    /// Ham (kümülatif) bekleme istatistiklerini okur.
    ///
    /// Collector bunu kullanır: fark hesabı yapabilmek için HAM değerlerin
    /// saklanması gerekir. Ekrandaki panel farkı gösterir, ama saklanan
    /// hep ham değerdir - fark saklarsan geçmişi yeniden hesaplayamazsın.
    /// </summary>
    public async Task<List<WaitSampleRow>> ReadRawWaitsAsync(
        InstanceOptions instance, CancellationToken ct)
    {
        await using var conn = _factory.CreateTargetConnection(instance);
        await conn.OpenAsync(ct);

        return (await QueryAsync<WaitSampleRow>(conn, DmvSql.WaitStats, ct)).ToList();
    }

    // ------------------------------------------------------------------
    // Panel okuyucuları
    // ------------------------------------------------------------------

    /// <summary>
    /// "En çok bekleme" kartı - DmvSql.WaitStats'i (ReadRawWaitsAsync'in
    /// kullandığı AYNI, zaten gürültüsü filtrelenmiş sorgu) okuyup her
    /// wait_type için CounterDeltaTracker'dan bir "ms/sn" oranı istiyor.
    /// I/O'daki gibi bir bölme YOK burada - wait_time_ms zaten doğrudan
    /// bir süre, stall/ops gibi ayrı bir sayaca bölünmesi gerekmiyor.
    ///
    /// İlk örneklemede (veya sayaç geri gittiyse - restart) tüm oranlar
    /// null döner, Top boş kalır - "0 ms/sn" göstermek "bekleme yok"
    /// demek olurdu, oysa doğrusu "henüz ölçemedik".
    /// </summary>
    private async Task<WaitStatsPanel> ReadWaitsAsync(
        SqlConnection conn, string instanceName, string scope, CancellationToken ct)
    {
        var rows = await QueryAsync<WaitSampleRow>(conn, DmvSql.WaitStats, ct);
        var deltas = new List<WaitTypeDelta>();

        foreach (var r in rows)
        {
            var rate = _counters.GetRatePerSecond(scope, instanceName, $"wait:{r.WaitType}", r.WaitTimeMs);
            if (rate is decimal ms && ms > 0)
                deltas.Add(new WaitTypeDelta { WaitType = r.WaitType, MsPerSecond = Math.Round(ms, 1) });
        }

        var top = deltas.OrderByDescending(d => d.MsPerSecond).Take(8).ToList();
        var totalMs = top.Sum(d => d.MsPerSecond);

        foreach (var d in top)
            d.SharePercent = totalMs > 0 ? Math.Round(d.MsPerSecond * 100m / totalMs, 1) : 0;

        return new WaitStatsPanel { Top = top };
    }

    private async Task<Dictionary<string, long>> ReadCountersAsync(
        SqlConnection conn, string instanceName, CancellationToken ct)
    {
        var rows = await QueryAsync<CounterRow>(conn, DmvSql.PerformanceCounters, ct);

        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            result[row.CounterKey] = row.CounterValue;

        return result;
    }

    private async Task<ServerInfo> ReadServerInfoAsync(SqlConnection conn, CancellationToken ct)
    {
        var info = await QuerySingleAsync<ServerInfo>(conn, DmvSql.ServerInfo, ct)
                   ?? new ServerInfo();

        info.UptimeHours = Math.Round((DateTime.UtcNow - info.StartTimeUtc).TotalHours, 1);
        return info;
    }

    private async Task<CpuPanel> ReadCpuAsync(
        SqlConnection conn, string instanceName, string scope, CancellationToken ct)
    {
        var cpu = await QuerySingleAsync<CpuRow>(conn, DmvSql.CpuUtilization, ct);
        var sched = await QuerySingleAsync<SchedulerRow>(conn, DmvSql.SchedulerPressure, ct);
        var rg = await QuerySingleAsync<ResourceGovernorCpuRow>(conn, DmvSql.ResourceGovernorCpu, ct);

        var t = _options.Thresholds;
        var schedulerCount = sched?.SchedulerCount ?? 0;

        // Ring buffer'daki değer - dakikada bir tazeleniyor (bkz.
        // DmvSql.CpuUtilization). İLK okumada (ya da Resource Governor
        // sayacı henüz bir taban oluşturmadıysa) buna düşüyoruz; aksi
        // halde ekranda boşluk/"-" görünür.
        var ringBufferSqlCpu = cpu?.SqlCpuPercent ?? 0;

        // Resource Governor'ın KÜMÜLATİF ms sayacı - iki ardışık okuma
        // arasındaki farkı alıp gerçek zamanlı bir CPU% üretiyoruz.
        // Ring buffer'ın aksine bu, uygulamanın kendi örnekleme aralığı
        // kadar (bugün ~1 sn) taze - bkz. DmvSql.ResourceGovernorCpu.
        var sqlCpu = ringBufferSqlCpu;
        if (rg?.TotalCpuUsageMs is long totalMs && schedulerCount > 0)
        {
            var msPerSecond = _counters.GetRatePerSecond(scope, instanceName, "sql_cpu_ms", totalMs);
            if (msPerSecond is decimal rate)
                sqlCpu = (int)Math.Round(Math.Clamp(rate / (schedulerCount * 10m), 0, 100));
        }

        var panel = new CpuPanel
        {
            SqlCpuPercent = sqlCpu,
            OtherCpuPercent = cpu?.OtherCpuPercent ?? 0,
            SystemCpuPercent = 100 - (cpu?.SystemIdle ?? 100),
            RunnableTasks = sched?.RunnableTasks ?? 0,
            SchedulerCount = schedulerCount
        };

        panel.Severity = Grade(sqlCpu, t.CpuWarn, t.CpuCritical, higherIsWorse: true);

        // CPU yüzdesi düşük ama sıra birikmişse yine de baskı vardır.
        // Yalnızca yüzdeye bakan bir izleme aracı bu durumu kaçırır.
        if (panel.SchedulerCount > 0 &&
            panel.RunnableTasks > panel.SchedulerCount &&
            panel.Severity == Severity.Healthy)
        {
            panel.Severity = Severity.Warning;
        }

        return panel;
    }

    private async Task<MemoryPanel> ReadMemoryAsync(
        SqlConnection conn, Dictionary<string, long>? counters, CancellationToken ct)
    {
        var mem = await QuerySingleAsync<MemoryRow>(conn, DmvSql.MemoryUsage, ct);
        var t = _options.Thresholds;

        var ple = GetCounter(counters, "Page life expectancy");
        var hit = ComputeBufferCacheHit(counters);

        var panel = new MemoryPanel
        {
            PageLifeExpectancy = ple ?? 0,
            BufferCacheHitRatio = hit,
            PendingGrants = (int)(GetCounter(counters, "Memory Grants Pending") ?? 0),
            CommittedGb = Round2((GetCounter(counters, "Total Server Memory (KB)") ?? 0) / 1048576m),
            TargetGb = Round2((GetCounter(counters, "Target Server Memory (KB)") ?? 0) / 1048576m),
            PhysicalGb = mem?.PhysicalGb ?? 0
        };

        // PLE'de yön ters: düşük olan kötü.
        panel.Severity = Grade(panel.PageLifeExpectancy, t.PleWarn, t.PleCritical, higherIsWorse: false);

        if (panel.PendingGrants > 0)
            panel.Severity = Worst(panel.Severity, Severity.Warning);

        return panel;
    }

    private async Task<TempDbPanel> ReadTempDbAsync(SqlConnection conn, CancellationToken ct)
    {
        var usage = await QuerySingleAsync<TempDbRow>(conn, DmvSql.TempDbUsage, ct);
        var cont = await QuerySingleAsync<TempDbContentionRow>(conn, DmvSql.TempDbContention, ct);
        var t = _options.Thresholds;

        var total = usage?.TotalMb ?? 0;
        var used = usage?.UsedMb ?? 0;
        var pct = total > 0 ? Round2(used / total * 100m) : 0;

        var panel = new TempDbPanel
        {
            TotalMb = total,
            UsedMb = used,
            UsedPercent = pct,
            VersionStoreMb = usage?.VersionStoreMb ?? 0,
            UserObjectsMb = usage?.UserObjectsMb ?? 0,
            InternalObjectsMb = usage?.InternalObjectsMb ?? 0,
            AllocationContention = cont?.Contention ?? 0,
            DataFileCount = cont?.DataFileCount ?? 0
        };

        panel.Severity = Grade(pct, t.TempDbWarn, t.TempDbCritical, higherIsWorse: true);

        if (panel.AllocationContention > 0)
            panel.Severity = Worst(panel.Severity, Severity.Warning);

        return panel;
    }

    private async Task<DiskPanel> ReadDiskAsync(
        SqlConnection conn, string instanceName, string scope, CancellationToken ct)
    {
        var rows = await QueryAsync<VolumeRow>(conn, DmvSql.VolumeSpace, ct);
        var t = _options.Thresholds;
        var panel = new DiskPanel();

        foreach (var row in rows)
        {
            var usedPct = row.TotalGb > 0
                ? Round2((row.TotalGb - row.FreeGb) / row.TotalGb * 100m)
                : 0;

            var volume = new VolumeInfo
            {
                Mount = row.Mount,
                TotalGb = row.TotalGb,
                FreeGb = row.FreeGb,
                UsedPercent = usedPct,
                Severity = Grade(usedPct, t.DiskWarn, t.DiskCritical, higherIsWorse: true)
            };

            panel.Volumes.Add(volume);
            panel.Severity = Worst(panel.Severity, volume.Severity);
        }

        panel.Volumes = panel.Volumes.OrderByDescending(v => v.UsedPercent).ToList();

        // Toplayıcı (scope=="collector") bu tahmine hiç ihtiyaç duymuyor -
        // ExtractMetrics yalnızca ham disk_used_pct'i okuyor. Waits
        // panelindeki AYNI ilke: kullanılmayan bir scope için izleme
        // veritabanına (yerel olsa da) boşuna bir tur atmayalım.
        if (scope == "ui")
        {
            try
            {
                panel.Forecast = await _store.GetDiskForecastAsync(instanceName, ct);
            }
            catch
            {
                // İzleme DB'sine ulaşılamıyorsa tahmin sessizce boş kalır -
                // Disk paneli zaten gösterilecek asıl veriyi (hacimler)
                // taşıyor, tahmin bir ek, gerekli değil.
            }
        }

        return panel;
    }

    /// <summary>
    /// En az 3 örneklem gerektiren "üst üste" kuralı BURADA, HER dosya için
    /// ayrı ayrı DEĞİL - genel okuma/yazma sonucunun kendisi için uygulanıyor.
    /// Sebebi: "en kötü dosya" örneklemeden örneklemeye değişebilir (bugün
    /// data dosyası, yarın log dosyası) - dosya bazlı bir streak, dosya
    /// değiştiği anda anlamsızca sıfırlanırdı. Bunun yerine "şu an okuma/
    /// yazma genel olarak eşiğin üzerinde mi" sorusuna üst üste cevap
    /// arıyoruz; HANGİ dosyanın suçlu olduğu değişebilir, sorun sürüyor mu
    /// sorusu değişmez.
    /// </summary>
    private const int RequiredBreachStreak = 3;

    private async Task<IoPanel> ReadIoAsync(
        SqlConnection conn, string instanceName, string scope, CancellationToken ct)
    {
        var rows = (await QueryAsync<FileIoRow>(conn, DmvSql.FileIoStats, ct)).ToList();
        var t = _options.Thresholds;

        var panel = new IoPanel { Files = rows };

        if (rows.Count == 0)
        {
            panel.Severity = Severity.Healthy;
            return panel;
        }

        // Kümülatif (açılıştan beri) - YALNIZCA bilgi amaçlı, aşağıdaki
        // Severity hesabına hiç girmiyor.
        panel.WorstReadMs = rows.Max(f => f.ReadLatencyMs);
        panel.WorstWriteMs = rows.Max(f => f.WriteLatencyMs);
        var slowestCumulative = rows.OrderByDescending(f => f.WriteLatencyMs).First();
        panel.SlowestFile = $"{slowestCumulative.DatabaseName} · {slowestCumulative.PhysicalName}";

        // Delta: her dosya için stall-ms/sn ve ops/sn oranlarını ayrı ayrı
        // CounterDeltaTracker'dan alıp bölüyoruz - ikisinin de ortak "/sn"
        // kısmı sadeleşir, geriye "bu pencerede işlem başına ortalama ms"
        // kalır (CounterDeltaTracker'ın kendisi ilk okumada null, sayaç
        // geri gittiyse (restart) null döner - burada tekrar yazmaya
        // gerek yok). Bölen (ops/sn) sıfırsa gecikme HESAPLANAMAZ - "0 ms"
        // "harika" demek değil, "bu pencerede o dosyaya hiç dokunulmadı"
        // demektir, null bırakıyoruz.
        decimal? bestReadMs = null; string? bestReadFile = null;
        decimal? bestWriteMs = null; string? bestWriteFile = null;

        foreach (var f in rows)
        {
            var fileKey = $"{f.DatabaseName}|{f.PhysicalName}";
            var label = $"{f.DatabaseName} · {f.PhysicalName}";

            var readStallRate = _counters.GetRatePerSecond(scope, instanceName, $"io_read_stall:{fileKey}", f.IoStallReadMs);
            var readOpsRate   = _counters.GetRatePerSecond(scope, instanceName, $"io_read_ops:{fileKey}",   f.NumOfReads);
            var writeStallRate = _counters.GetRatePerSecond(scope, instanceName, $"io_write_stall:{fileKey}", f.IoStallWriteMs);
            var writeOpsRate   = _counters.GetRatePerSecond(scope, instanceName, $"io_write_ops:{fileKey}",   f.NumOfWrites);

            var deltaRead = (readStallRate is decimal rs && readOpsRate is decimal ro && ro > 0)
                ? rs / ro : (decimal?)null;
            var deltaWrite = (writeStallRate is decimal ws && writeOpsRate is decimal wo && wo > 0)
                ? ws / wo : (decimal?)null;

            if (deltaRead is decimal dr && (bestReadMs is null || dr > bestReadMs))
            { bestReadMs = dr; bestReadFile = label; }

            if (deltaWrite is decimal dw && (bestWriteMs is null || dw > bestWriteMs))
            { bestWriteMs = dw; bestWriteFile = label; }
        }

        panel.DeltaReadLatencyMs = bestReadMs;
        panel.DeltaWriteLatencyMs = bestWriteMs;
        panel.SlowestDeltaReadFile = bestReadFile;
        panel.SlowestDeltaWriteFile = bestWriteFile;

        // "Üst üste en az 3 periyot eşiğin üzerinde" - tek bir anlık
        // sıçrama alarm üretmesin, SÜREGELEN bir sorun olsun istiyoruz.
        // Eşiğin altına inince StreakTracker sayacı kendiliğinden sıfırlar.
        var readOverWarn = bestReadMs is decimal r2 && r2 >= t.ReadLatencyWarnMs;
        var writeOverWarn = bestWriteMs is decimal w2 && w2 >= t.WriteLatencyWarnMs;

        panel.ReadBreachStreak = _streaks.Bump($"{scope}::{instanceName}::io_read", readOverWarn);
        panel.WriteBreachStreak = _streaks.Bump($"{scope}::{instanceName}::io_write", writeOverWarn);

        panel.ReadSeverity = panel.ReadBreachStreak >= RequiredBreachStreak
            ? Grade(bestReadMs ?? 0, t.ReadLatencyWarnMs, t.ReadLatencyCriticalMs, true)
            : Severity.Healthy;

        panel.WriteSeverity = panel.WriteBreachStreak >= RequiredBreachStreak
            ? Grade(bestWriteMs ?? 0, t.WriteLatencyWarnMs, t.WriteLatencyCriticalMs, true)
            : Severity.Healthy;

        panel.Severity = Worst(panel.ReadSeverity, panel.WriteSeverity);

        return panel;
    }

    private async Task<ActivityPanel> ReadActivityAsync(SqlConnection conn, CancellationToken ct)
    {
        var requests = (await QueryAsync<RequestRow>(conn, DmvSql.ActiveRequests, ct)).ToList();
        var counts = await QuerySingleAsync<SessionCountRow>(conn, DmvSql.SessionCounts, ct);

        foreach (var r in requests)
            r.SqlText = Collapse(r.SqlText, 600);

        return new ActivityPanel
        {
            Requests = requests.Take(50).ToList(),
            RunningRequests = requests.Count,
            SleepingSessions = counts?.SleepingSessions ?? 0,
            TotalConnections = counts?.TotalConnections ?? 0,
            OpenTransactions = counts?.OpenTransactions ?? 0,
            LongestRunningSeconds = requests.Count == 0 ? 0 : requests.Max(r => r.ElapsedMs) / 1000
        };
    }

    private async Task<BlockingPanel> ReadBlockingAsync(SqlConnection conn, CancellationToken ct)
    {
        var rows = (await QueryAsync<BlockRow>(conn, DmvSql.BlockingChains, ct)).ToList();
        var t = _options.Thresholds;

        foreach (var r in rows)
        {
            r.BlockedSql = Collapse(r.BlockedSql, 400);
            r.BlockerSql = Collapse(r.BlockerSql, 400);
        }

        var panel = new BlockingPanel
        {
            Chains = rows,
            BlockedCount = rows.Count,
            BlockerCount = rows.Select(r => r.BlockingSessionId).Distinct().Count(),
            LongestBlockMs = rows.Count == 0 ? 0 : rows.Max(r => r.WaitTimeMs)
        };

        // Baş engelleyici: başkasını bloklayan ama kendisi bloklanmayan
        // oturum. Zincirin tepesi burasıdır; müdahale edilecek yer de.
        var blocked = rows.Select(r => r.BlockedSessionId).ToHashSet();
        panel.HeadBlockerSessionId = rows
            .Select(r => r.BlockingSessionId)
            .FirstOrDefault(id => !blocked.Contains(id));

        if (panel.HeadBlockerSessionId == 0) panel.HeadBlockerSessionId = null;

        panel.Severity = Grade(panel.BlockedCount, t.BlockedSessionsWarn, t.BlockedSessionsCritical, true);
        return panel;
    }

    // ------------------------------------------------------------------
    // KPI şeridi
    // ------------------------------------------------------------------

    private List<KpiItem> BuildKpis(LiveSnapshot s, Dictionary<string, long>? counters, string scope)
    {
        var t = _options.Thresholds;
        var name = s.InstanceName;

        decimal? Rate(string key)
        {
            var raw = GetCounter(counters, key);
            return raw is null ? null : _counters.GetRatePerSecond(scope, name, key, raw.Value);
        }

        return new List<KpiItem>
        {
            new()
            {
                Key = "batch_req_sec", Label = "İstek/sn", Unit = "",
                Value = Rate("Batch Requests/sec"),
                Severity = Severity.Healthy,
                Note = "SQL Server'a saniyede gelen batch sayısı"
            },
            new()
            {
                Key = "tran_sec", Label = "İşlem/sn", Unit = "",
                Value = Rate("Transactions/sec"),
                Severity = Severity.Healthy
            },
            new()
            {
                Key = "cpu_sql", Label = "CPU (SQL)", Unit = "%",
                Value = s.Cpu.SqlCpuPercent,
                Severity = Grade(s.Cpu.SqlCpuPercent, t.CpuWarn, t.CpuCritical, true)
            },
            new()
            {
                Key = "cpu_system", Label = "CPU (sistem)", Unit = "%",
                Value = s.Cpu.SystemCpuPercent,
                Severity = Grade(s.Cpu.SystemCpuPercent, t.CpuWarn, t.CpuCritical, true),
                // CPU (SQL)'in aksine bu değer SQL Server'ın kendi ring
                // buffer'ına bağlı ve dakikada bir tazeleniyor (bkz.
                // DmvSql.CpuUtilization) - OS-geneli veri, daha sık
                // ölçülebileceği başka bir T-SQL kaynağı yok. Kullanıcı
                // iki kartın neden farklı davrandığını (biri saniyelik
                // hareket ederken diğeri basamaklı) merak etmesin diye
                // bunu her zaman gösteriyoruz, yalnızca "diğer süreç"
                // uyarısı olduğunda değil.
                Note = s.Cpu.OtherCpuPercent > 20
                    ? $"CPU'nun %{s.Cpu.OtherCpuPercent}'i SQL dışı süreçlerde · ~60 sn'de bir tazelenir"
                    : "~60 sn'de bir tazelenir"
            },
            new()
            {
                Key = "buffer_hit", Label = "Buffer cache hit", Unit = "%",
                Value = s.Memory.BufferCacheHitRatio,
                Severity = Grade(s.Memory.BufferCacheHitRatio,
                                 t.BufferCacheHitWarn, t.BufferCacheHitCritical, false)
            },
            new()
            {
                Key = "ple", Label = "Page life expectancy", Unit = "sn",
                Value = s.Memory.PageLifeExpectancy,
                Severity = Grade(s.Memory.PageLifeExpectancy, t.PleWarn, t.PleCritical, false),
                Note = "Bir sayfanın bellekte kalma süresi; düşük olan kötü"
            },
            new()
            {
                Key = "connections", Label = "Bağlantı", Unit = "",
                Value = s.Activity.TotalConnections,
                Severity = Severity.Healthy,
                Note = $"{s.Activity.SleepingSessions} tanesi uykuda"
            },
            new()
            {
                Key = "blocked", Label = "Bloklanan", Unit = "",
                Value = s.Blocking.BlockedCount,
                Severity = s.Blocking.Severity
            },
            new()
            {
                Key = "deadlock_sec", Label = "Deadlock/sn", Unit = "",
                Value = Rate("Number of Deadlocks/sec"),
                Severity = Severity.Healthy
            }
        };
    }

    private async Task AttachTrendsAsync(
        string instanceName, LiveSnapshot snapshot, CancellationToken ct)
    {
        const int Points = 40;

        var kpis = snapshot.Kpis;
        var keys = kpis.Select(k => k.Key).ToArray();

        var trends = await _store.GetRecentTrendsAsync(instanceName, keys, Points, ct);

        foreach (var kpi in kpis)
            if (trends.TryGetValue(kpi.Key, out var values))
                kpi.Trend = values;

        // Grafiğin kapsadığı aralık ekranda yazacak. Sabit bir metin yerine
        // ölçülen değeri gönderiyoruz: toplayıcı aksarsa aynı 40 örnek çok
        // daha geniş bir zamana yayılır ve sabit etiket yalan olur.
        snapshot.Trend = await _store.GetTrendWindowAsync(instanceName, keys, Points, ct);
    }

    // ------------------------------------------------------------------
    // Yardımcılar
    // ------------------------------------------------------------------

    /// <summary>
    /// Panel bazlı hata yalıtımı. Patlarsa loglar, snapshot'a not düşer,
    /// null döner - ama ekranın kalanı ayakta kalır.
    /// </summary>
    private async Task<T?> SafeAsync<T>(LiveSnapshot snapshot, string panel, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Panel} paneli okunamadı", panel);
            snapshot.Errors.Add(new PanelError(panel, ex.Message));
            return default;
        }
    }

    private async Task<IEnumerable<T>> QueryAsync<T>(SqlConnection conn, string sql, CancellationToken ct)
        => await conn.QueryAsync<T>(new CommandDefinition(
            sql, commandTimeout: _factory.QueryTimeoutSeconds, cancellationToken: ct));

    private async Task<T?> QuerySingleAsync<T>(SqlConnection conn, string sql, CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<T>(new CommandDefinition(
            sql, commandTimeout: _factory.QueryTimeoutSeconds, cancellationToken: ct));

    private static long? GetCounter(Dictionary<string, long>? counters, string key)
        => counters is not null && counters.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// Buffer cache hit ratio, SQL Server'da iki sayacın oranıdır:
    /// "Buffer cache hit ratio" ve "Buffer cache hit ratio base".
    /// Ham sayacı tek başına okumak yanlış sonuç verir.
    /// </summary>
    private static decimal ComputeBufferCacheHit(Dictionary<string, long>? counters)
    {
        var value = GetCounter(counters, "Buffer cache hit ratio");
        var baseValue = GetCounter(counters, "Buffer cache hit ratio base");

        if (value is null || baseValue is null or 0) return 0;

        return Round2(value.Value * 100m / baseValue.Value);
    }

    private static decimal Round2(decimal value) => Math.Round(value, 2);

    private static Severity Grade(decimal value, decimal warn, decimal critical, bool higherIsWorse)
    {
        if (higherIsWorse)
        {
            if (value >= critical) return Severity.Critical;
            if (value >= warn) return Severity.Warning;
        }
        else
        {
            if (value <= critical) return Severity.Critical;
            if (value <= warn) return Severity.Warning;
        }

        return Severity.Healthy;
    }

    private static Severity Worst(Severity a, Severity b)
    {
        if (a == Severity.Unknown) return b;
        if (b == Severity.Unknown) return a;
        return (Severity)Math.Max((int)a, (int)b);
    }

    /// <summary>SQL metnini tek satıra indirger; tablo hizası bozulmasın.</summary>
    private static string Collapse(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var single = string.Join(' ',
            text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .Trim();

        while (single.Contains("  ")) single = single.Replace("  ", " ");

        return single.Length <= maxLength ? single : single[..maxLength] + "…";
    }

    // Dapper'ın eşleyeceği ham satır tipleri
    private sealed class CounterRow
    {
        public string CounterKey { get; set; } = "";
        public string InstanceName { get; set; } = "";
        public long CounterValue { get; set; }
        public int CounterType { get; set; }
    }

    private sealed class CpuRow
    {
        public int SqlCpuPercent { get; set; }
        public int SystemIdle { get; set; }
        public int OtherCpuPercent { get; set; }
    }

    private sealed class ResourceGovernorCpuRow
    {
        public long? TotalCpuUsageMs { get; set; }
    }

    private sealed class SchedulerRow
    {
        public int SchedulerCount { get; set; }
        public int RunnableTasks { get; set; }
        public int PendingDiskIo { get; set; }
    }

    private sealed class MemoryRow
    {
        public decimal CommittedGb { get; set; }
        public decimal PhysicalGb { get; set; }
    }

    private sealed class TempDbRow
    {
        public decimal TotalMb { get; set; }
        public decimal UsedMb { get; set; }
        public decimal VersionStoreMb { get; set; }
        public decimal UserObjectsMb { get; set; }
        public decimal InternalObjectsMb { get; set; }
    }

    private sealed class TempDbContentionRow
    {
        public int Contention { get; set; }
        public int DataFileCount { get; set; }
    }

    private sealed class VolumeRow
    {
        public string Mount { get; set; } = "";
        public decimal TotalGb { get; set; }
        public decimal FreeGb { get; set; }
    }

    private sealed class SessionCountRow
    {
        public int TotalConnections { get; set; }
        public int SleepingSessions { get; set; }
        public int OpenTransactions { get; set; }
    }
}

public sealed class WaitSampleRow
{
    public string WaitType { get; set; } = "";
    public long WaitTimeMs { get; set; }
    public long SignalTimeMs { get; set; }
    public long WaitingTasks { get; set; }
}
