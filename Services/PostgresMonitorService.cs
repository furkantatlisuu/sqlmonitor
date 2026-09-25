using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Options;
using SqlMonitor.Infrastructure;
using SqlMonitor.Models;
using SqlMonitor.Options;
using SqlMonitor.Sql;

namespace SqlMonitor.Services;

/// <summary>
/// PostgreSQL için anlık görüntü ve sağlık kontrolleri.
///
/// LiveMonitorService + HealthEvaluator ikilisinin PostgreSQL karşılığı,
/// ama onların AYNADAKİ GÖRÜNTÜSÜ DEĞİL - bilerek. SQL Server'ın 16
/// kontrolünü PostgreSQL'e zorla çevirmeye çalışmak, olmayan ölçüleri
/// uydurmak demekti (PostgreSQL kendi CPU'sunu ölçmez, PLE diye bir
/// kavramı yoktur, yedek katalogu tutmaz). Onun yerine PostgreSQL'in
/// GERÇEKTEN ölçtüğü şeylerden kendi kontrol setini kuruyoruz:
/// bloklama, uzun sorgu, açık transaction, önbellek isabeti, bağlantı
/// doluluğu, sequence taşması, checksum, deadlock, ölü satır (vacuum),
/// geçici dosya, replikasyon slotu ve WAL arşivi.
///
/// Ekran, karşılığı olmayan kartları capability listesine bakarak
/// tamamen gizliyor (kullanıcı tercihi) - boş kart göstermiyoruz.
/// </summary>
public sealed class PostgresMonitorService
{
    private readonly SqlConnectionFactory _factory;
    private readonly MonitorOptions _options;
    private readonly ILogger<PostgresMonitorService> _log;

    public PostgresMonitorService(
        SqlConnectionFactory factory,
        IOptions<MonitorOptions> options,
        ILogger<PostgresMonitorService> log)
    {
        _factory = factory;
        _options = options.Value;
        _log = log;
    }

    public async Task<LiveSnapshot> GetSnapshotAsync(
        InstanceOptions instance, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = new LiveSnapshot
        {
            InstanceName = instance.Name,
            CapturedAtUtc = DateTime.UtcNow,
            Engine = DbEngine.Postgres
        };

        await using var conn = _factory.CreateTargetDbConnection(instance);
        await conn.OpenAsync(ct);

        var hasStatements = await SafeAsync(s, "topqueries",
            () => HasStatStatementsAsync(conn, ct));

        s.Capabilities = BuildCapabilities(hasStatements);

        s.Server = await SafeAsync(s, "server", () => ReadServerAsync(conn, ct)) ?? new ServerInfo();

        var dbs = await SafeAsync(s, "databases", () => ReadDatabasesAsync(conn, ct))
                  ?? new List<PgDatabaseRow>();

        s.Activity = await SafeAsync(s, "activity", () => ReadActivityAsync(conn, ct)) ?? new ActivityPanel();
        s.Blocking = await SafeAsync(s, "blocking", () => ReadBlockingAsync(conn, ct)) ?? new BlockingPanel();

        var seqs   = await SafeAsync(s, "sequences", () => Query<PgSequenceRow>(conn, PgSql.SequenceUsage, ct))
                     ?? new List<PgSequenceRow>();
        var vacuum = await SafeAsync(s, "vacuum", () => Query<PgVacuumRow>(conn, PgSql.VacuumStatus, ct))
                     ?? new List<PgVacuumRow>();
        var repl   = await SafeAsync(s, "replication", () => QueryOne<PgReplicationRow>(conn, PgSql.ReplicationAndWal, ct));

        // Ekranın üst şeridi bu üçünü gösteriyor. Çekirdek sayısı ve RAM
        // PostgreSQL'den okunamaz (motor kendi donanımını bilmez) -
        // onlar 0 kalıyor ve ekran 0 olanları hiç çizmiyor.
        s.Server.DatabaseCount = dbs.Count;
        s.Server.OnlineDatabaseCount = dbs.Count(d => d.AllowConnections);
        if (s.Server.StartTimeUtc != default)
            s.Server.UptimeHours = (DateTime.UtcNow - s.Server.StartTimeUtc).TotalHours;

        s.Health = BuildHealth(instance, dbs, s.Activity, s.Blocking, seqs, vacuum, repl);
        s.Kpis = BuildKpis(dbs, s.Activity, s.Blocking);

        s.ElapsedMs = (int)sw.ElapsedMilliseconds;
        return s;
    }

    /// <summary>
    /// PostgreSQL'in ölçebildikleri. SQL Server listesiyle kıyaslanınca
    /// eksik görünenler (cpu, memory, disk, backups, alwayson,
    /// agentjobs, tempdb, io, waits) motorun gerçekten ölçmediği
    /// şeyler - bkz. sınıf başındaki not.
    /// </summary>
    private static List<string> BuildCapabilities(bool hasStatStatements)
    {
        var caps = new List<string>
        {
            Capability.UnusedIndexes,
            Capability.Vacuum,
            Capability.TableBloat,
            Capability.KillSession
        };

        // pg_stat_statements olmadan "en yoğun sorgular" ve "bu uyarı
        // neden çıktı" kanıtı üretilemez. Eklenti yoksa ekran o
        // bölümleri hiç göstermiyor - boş bir liste gösterip kullanıcıyı
        // "demek ki hiç sorgu yok" diye yanıltmaktansa.
        if (hasStatStatements) caps.Add(Capability.TopQueries);

        return caps;
    }

    private static async Task<bool> HasStatStatementsAsync(DbConnection conn, CancellationToken ct)
        => await conn.ExecuteScalarAsync<bool?>(
               new CommandDefinition(PgSql.HasStatStatements, cancellationToken: ct)) ?? false;

    private async Task<ServerInfo> ReadServerAsync(DbConnection conn, CancellationToken ct)
    {
        var row = await QueryOne<PgServerRow>(conn, PgSql.ServerInfo, ct);
        if (row is null) return new ServerInfo();

        return new ServerInfo
        {
            ServerName = row.ServerName,
            ProductVersion = row.ProductVersion,
            Edition = row.Edition,
            StartTimeUtc = row.StartTime.ToUniversalTime()
        };
    }

    private async Task<List<PgDatabaseRow>> ReadDatabasesAsync(DbConnection conn, CancellationToken ct)
        => await Query<PgDatabaseRow>(conn, PgSql.DatabaseStats, ct);

    private async Task<ActivityPanel> ReadActivityAsync(DbConnection conn, CancellationToken ct)
    {
        var requests = await Query<RequestRow>(conn, PgSql.ActiveRequests, ct);
        var counts = await QueryOne<SessionCountRow>(conn, PgSql.SessionCounts, ct) ?? new SessionCountRow();

        // "Çalışan" sayılmayan (idle) oturumların süresi yanıltıcıdır:
        // pg_stat_activity idle bir bağlantıda son sorgunun query_start'ını
        // tutmaya devam eder, yani saatlerce "açık" görünür. Uzunluk
        // hesabına yalnızca gerçekten çalışanlar giriyor.
        var running = requests.Where(r => r.Status == "active").ToList();

        return new ActivityPanel
        {
            RunningRequests = counts.RunningRequests,
            SleepingSessions = counts.SleepingSessions,
            TotalConnections = counts.TotalConnections,
            OpenTransactions = counts.OpenTransactions,
            LongestRunningSeconds = running.Count == 0 ? 0 : running.Max(r => r.ElapsedMs) / 1000,
            Requests = requests,
            MaxConnections = counts.MaxConnections
        };
    }

    private async Task<BlockingPanel> ReadBlockingAsync(DbConnection conn, CancellationToken ct)
    {
        var rows = await Query<BlockRow>(conn, PgSql.BlockingChains, ct);
        var t = _options.Thresholds;

        var blocked = rows.Select(r => r.BlockedSessionId).ToHashSet();
        var head = rows.Select(r => r.BlockingSessionId).FirstOrDefault(id => !blocked.Contains(id));

        var panel = new BlockingPanel
        {
            BlockedCount = rows.Select(r => r.BlockedSessionId).Distinct().Count(),
            BlockerCount = rows.Select(r => r.BlockingSessionId).Distinct().Count(),
            HeadBlockerSessionId = head == 0 ? null : head,
            LongestBlockMs = rows.Count == 0 ? 0 : rows.Max(r => r.WaitTimeMs),
            Chains = rows
        };

        panel.Severity = panel.BlockedCount >= t.BlockedSessionsCritical ? Severity.Critical
                       : panel.BlockedCount >= t.BlockedSessionsWarn ? Severity.Warning
                       : Severity.Healthy;
        return panel;
    }

    // ------------------------------------------------------------------
    // Sağlık kontrolleri - PostgreSQL'in kendi ölçtükleri üzerinden
    // ------------------------------------------------------------------

    private const string Performance = "performans";
    private const string Reliability = "güvenilirlik";
    private const string Capacity = "kapasite";

    private HealthPanel BuildHealth(
        InstanceOptions instance,
        List<PgDatabaseRow> dbs,
        ActivityPanel activity,
        BlockingPanel blocking,
        List<PgSequenceRow> sequences,
        List<PgVacuumRow> vacuum,
        PgReplicationRow? repl)
    {
        var t = _options.Thresholds;
        var checks = new List<HealthCheck>();

        // --- Bloklama ---------------------------------------------------
        checks.Add(new HealthCheck
        {
            Key = "blocking",
            Question = "Lock / bloklama var mı?",
            Category = Performance,
            Severity = blocking.Severity,
            Finding = blocking.BlockedCount == 0
                ? "şu an bloklanan istek yok"
                : $"{blocking.BlockedCount} bloklanan istek, baş engelleyici " +
                  $"PID {blocking.HeadBlockerSessionId?.ToString() ?? "?"}, " +
                  $"en uzunu {blocking.LongestBlockMs / 1000} sn",
            Remedy = blocking.BlockedCount == 0 ? null
                : "Baş engelleyiciyi incele. pg_terminate_backend ile sonlandırmadan önce " +
                  "ne yaptığına bak - uzun bir rapor ise sonlandırmak geri alma maliyeti doğurur.",
            Evidence = blocking.Severity == Severity.Healthy
                ? null
                : HealthEvaluator.BuildBlockingEvidence(blocking.Chains)
        });

        // --- Uzun süren sorgu -------------------------------------------
        var longest = activity.LongestRunningSeconds;
        var lrSeverity = longest >= t.LongRunningQueryCriticalSeconds ? Severity.Critical
                       : longest >= t.LongRunningQueryWarnSeconds ? Severity.Warning
                       : Severity.Healthy;

        checks.Add(new HealthCheck
        {
            Key = "long_running",
            Question = "Uzun süren sorgu var mı?",
            Category = Performance,
            Severity = lrSeverity,
            Finding = activity.RunningRequests == 0
                ? "çalışan kullanıcı isteği yok"
                : $"{activity.RunningRequests} çalışan istek, en uzunu {longest} sn",
            Evidence = lrSeverity == Severity.Healthy
                ? null
                : HealthEvaluator.BuildRequestEvidence(activity.Requests.Where(r => r.Status == "active"))
        });

        // --- Açık kalmış transaction ------------------------------------
        //
        // PostgreSQL'de bu SQL Server'dakinden DAHA kritik: "idle in
        // transaction" bir bağlantı, VACUUM'un ölü satırları
        // temizlemesini de engeller (xmin horizon ilerlemez). Yani tek
        // bir unutulmuş transaction bütün veritabanını şişirebilir.
        checks.Add(new HealthCheck
        {
            Key = "open_tran",
            Question = "Unutulmuş transaction var mı?",
            Category = Reliability,
            Severity = activity.OpenTransactions >= 3 ? Severity.Warning : Severity.Healthy,
            Finding = $"{activity.OpenTransactions} adet 'idle in transaction' bağlantı",
            Remedy = activity.OpenTransactions > 0
                ? "Açık kalmış transaction kilitleri tutar VE autovacuum'un ölü satırları " +
                  "temizlemesini engeller - tablo şişmesinin en sık sebebi budur."
                : null
        });

        // --- Bağlantı doluluğu ------------------------------------------
        //
        // PostgreSQL'de bağlantı pahalıdır (her biri ayrı süreç) ve
        // max_connections'a dayanmak "sunucu artık hiç kimseyi kabul
        // etmiyor" demektir - SQL Server'da bunun bu kadar sert bir
        // karşılığı yok, o yüzden bu kontrol PostgreSQL'e özel.
        // max_connections sunucudan okunuyor (bkz. PgSql.SessionCounts) -
        // ayara yazmak yerine: kullanici onu degistirdiginde uygulamayi
        // yeniden baslatmak gerekmesin.
        var limit = activity.MaxConnections > 0 ? activity.MaxConnections : 100;
        var connPct = limit == 0 ? 0 : activity.TotalConnections * 100.0 / limit;
        checks.Add(new HealthCheck
        {
            Key = "connections",
            Question = "Bağlantı limiti doluyor mu?",
            Category = Capacity,
            Severity = connPct >= 90 ? Severity.Critical : connPct >= 75 ? Severity.Warning : Severity.Healthy,
            Finding = $"{activity.TotalConnections} / {limit} bağlantı (%{connPct:0.#})",
            Remedy = connPct >= 75
                ? "PostgreSQL'de her bağlantı ayrı bir süreçtir. Limit dolmadan önce " +
                  "PgBouncer gibi bir havuzlayıcı düşünülmeli."
                : null
        });

        // --- Önbellek isabeti -------------------------------------------
        //
        // PLE'nin karşılığı DEĞİL ama PostgreSQL'in bellek sağlığı için
        // verdiği en doğrudan sinyal bu. shared_buffers yetersizse
        // burası düşer.
        var mainDb = dbs.FirstOrDefault(d =>
                         string.Equals(d.DatabaseName, CurrentDatabase(instance), StringComparison.OrdinalIgnoreCase))
                     ?? dbs.FirstOrDefault();

        if (mainDb is not null)
        {
            var hit = mainDb.CacheHitPercent ?? 100m;
            checks.Add(new HealthCheck
            {
                Key = "cache_hit",
                Question = "Önbellek isabet oranı yeterli mi?",
                Category = Performance,
                Severity = hit < t.BufferCacheHitCritical ? Severity.Critical
                         : hit < t.BufferCacheHitWarn ? Severity.Warning
                         : Severity.Healthy,
                Finding = $"{mainDb.DatabaseName}: %{hit:0.##} isabet " +
                          $"({mainDb.BlocksRead:N0} blok diskten okundu)",
                Remedy = hit < t.BufferCacheHitWarn
                    ? "shared_buffers küçük olabilir ya da çok tarama yapan sorgular var. " +
                      "Sıralı tarama yiyen tablolara bak."
                    : null
            });

            // --- Deadlock ------------------------------------------------
            checks.Add(new HealthCheck
            {
                Key = "deadlocks",
                Question = "Deadlock oluyor mu?",
                Category = Reliability,
                Severity = mainDb.Deadlocks > 0 ? Severity.Warning : Severity.Healthy,
                Finding = mainDb.Deadlocks == 0
                    ? "sayaç sıfırlandığından beri deadlock yok"
                    : $"{mainDb.Deadlocks:N0} deadlock" +
                      (mainDb.StatsReset.HasValue
                          ? $" ({mainDb.StatsReset:dd.MM.yyyy HH:mm}'den beri)"
                          : ""),
                Remedy = mainDb.Deadlocks > 0
                    ? "log_lock_waits açıkken PostgreSQL günlüğü deadlock'un iki tarafını da yazar."
                    : null
            });

            // --- Checksum (bozulma) --------------------------------------
            checks.Add(new HealthCheck
            {
                Key = "corruption",
                Question = "Veri bozulması var mı?",
                Category = Reliability,
                Severity = mainDb.ChecksumFailures > 0 ? Severity.Critical : Severity.Healthy,
                Finding = mainDb.ChecksumFailures == 0
                    ? "checksum hatası yok"
                    : $"{mainDb.ChecksumFailures:N0} checksum hatası",
                Remedy = mainDb.ChecksumFailures > 0
                    ? "Checksum hatası diskte bozulmuş sayfa demektir. Donanımı kontrol et, " +
                      "yedekten geri dönmeyi planla."
                    : null
            });

            // --- Geçici dosya (tempdb'nin uzaktan akrabası) ---------------
            checks.Add(new HealthCheck
            {
                Key = "temp_files",
                Question = "Geçici dosya taşması var mı?",
                Category = Capacity,
                Severity = mainDb.TempBytes > 10L * 1024 * 1024 * 1024 ? Severity.Warning : Severity.Healthy,
                Finding = mainDb.TempFiles == 0
                    ? "geçici dosya kullanılmamış"
                    : $"{mainDb.TempFiles:N0} geçici dosya, {Bytes(mainDb.TempBytes)}",
                Remedy = mainDb.TempFiles > 0
                    ? "Sıralama/hash işlemleri work_mem'e sığmadığında diske taşar. " +
                      "work_mem artırılabilir ya da sorgular gözden geçirilebilir."
                    : null
            });
        }

        // --- Veritabanı erişilebilirliği --------------------------------
        var kapali = dbs.Where(d => !d.AllowConnections).Select(d => d.DatabaseName).ToList();
        checks.Add(new HealthCheck
        {
            Key = "db_online",
            Question = "Tüm veritabanları erişilebilir mi?",
            Category = Reliability,
            Severity = kapali.Count > 0 ? Severity.Warning : Severity.Healthy,
            Finding = kapali.Count == 0
                ? $"{dbs.Count} veritabanı, hepsi bağlantıya açık"
                : $"{kapali.Count} veritabanı bağlantıya kapalı: {string.Join(", ", kapali)}"
        });

        // --- Sequence taşması -------------------------------------------
        var enDolu = sequences.OrderByDescending(x => x.UsedPercent ?? 0).FirstOrDefault();
        if (enDolu is not null)
        {
            var pct = enDolu.UsedPercent ?? 0;
            checks.Add(new HealthCheck
            {
                Key = "identity_overflow",
                Question = "Sequence taşmaya yaklaşan var mı?",
                Category = Capacity,
                Severity = pct >= 90 ? Severity.Critical : pct >= 70 ? Severity.Warning : Severity.Healthy,
                Finding = $"en dolu: {enDolu.SchemaName}.{enDolu.ObjectName} " +
                          $"%{pct:0.####} ({enDolu.LastValue:N0} / {enDolu.MaxValue:N0})",
                Remedy = pct >= 70
                    ? "Sequence üst sınıra dayanınca INSERT'ler hata vermeye başlar. " +
                      "Sütun tipi int ise bigint'e çıkarmak gerekir."
                    : null
            });
        }

        // --- Ölü satır / vacuum -----------------------------------------
        //
        // SQL Server'da karşılığı olmayan, MVCC'ye özgü bir dert.
        // Sadece anlamlı büyüklükteki tablolara bakıyoruz: 10 satırlık
        // bir tabloda %60 ölü olması hiçbir şey ifade etmez.
        var sisen = vacuum
            .Where(v => v.LiveTuples + v.DeadTuples > 10_000 && (v.DeadPercent ?? 0) >= 20)
            .OrderByDescending(v => v.DeadTuples)
            .ToList();

        checks.Add(new HealthCheck
        {
            Key = "vacuum",
            Question = "Tablolar şişmiş mi (vacuum yetişiyor mu)?",
            Category = Capacity,
            Severity = sisen.Any(v => (v.DeadPercent ?? 0) >= 40) ? Severity.Warning : Severity.Healthy,
            Finding = sisen.Count == 0
                ? "kayda değer ölü satır birikmesi yok"
                : $"{sisen.Count} tabloda %20+ ölü satır, en kötüsü " +
                  $"{sisen[0].SchemaName}.{sisen[0].TableName} (%{sisen[0].DeadPercent:0.#}, " +
                  $"{sisen[0].DeadTuples:N0} ölü satır)",
            Remedy = sisen.Count > 0
                ? "Autovacuum yetişemiyor. Açık kalmış transaction var mı bak (ölü satırları " +
                  "o da tutabilir), gerekirse autovacuum eşiklerini sıkılaştır."
                : null
        });

        // --- Replikasyon slotu ve WAL arşivi ----------------------------
        //
        // Tek makine kurulumunda bile önemli: kullanılmayan bir
        // replikasyon slotu WAL'ı sonsuza kadar biriktirir ve diski
        // doldurur - PostgreSQL'in en bilinen "sunucu bir sabah durmuş"
        // sebeplerinden biri.
        if (repl is not null)
        {
            checks.Add(new HealthCheck
            {
                Key = "wal",
                Question = "WAL birikmesi riski var mı?",
                Category = Reliability,
                Severity = repl.InactiveSlots > 0 || repl.FailedCount > 0 ? Severity.Warning : Severity.Healthy,
                Finding = repl.InactiveSlots == 0 && repl.FailedCount == 0
                    ? $"{repl.SlotCount} replikasyon slotu, arşiv hatası yok"
                    : $"{repl.InactiveSlots} pasif slot, {repl.FailedCount:N0} arşiv hatası",
                Remedy = repl.InactiveSlots > 0
                    ? "Pasif bir replikasyon slotu WAL'ı sonsuza kadar biriktirir ve diski doldurur. " +
                      "Kullanılmıyorsa pg_drop_replication_slot ile silinmeli."
                    : repl.FailedCount > 0
                        ? "WAL arşivlenemiyor - archive_command'i kontrol et. Arşiv tıkanırsa pg_wal şişer."
                        : null
            });
        }

        return BuildPanel(checks);
    }

    /// <summary>
    /// Puanlama SQL Server tarafıyla AYNI (kritik 13, uyarı 4) ama
    /// bölen farklı: yalnızca PostgreSQL'in gerçekten ölçebildiği
    /// kontroller sayılıyor. Ölçülemeyen 5 kontrolü "sağlıklı" sayıp
    /// 100 vermek yalan olurdu.
    /// </summary>
    private static HealthPanel BuildPanel(List<HealthCheck> checks)
    {
        const int CriticalPenalty = 13;
        const int WarningPenalty = 4;

        foreach (var c in checks)
        {
            c.ScoreImpact = c.Severity switch
            {
                Severity.Critical => -CriticalPenalty,
                Severity.Warning => -WarningPenalty,
                _ => 0
            };

            c.CanExplain = c.Severity is Severity.Warning or Severity.Critical
                        && c.Key is "blocking" or "long_running";
        }

        int Score(string? category)
        {
            var list = category is null ? checks : checks.Where(c => c.Category == category).ToList();
            return Math.Max(0, 100 + list.Sum(c => c.ScoreImpact));
        }

        return new HealthPanel
        {
            Score = Score(null),
            PerformanceScore = Score(Performance),
            ReliabilityScore = Score(Reliability),
            CapacityScore = Score(Capacity),
            Checks = checks
                .OrderBy(c => c.ScoreImpact)
                .ThenBy(c => c.Question)
                .ToList()
        };
    }

    private static List<KpiItem> BuildKpis(
        List<PgDatabaseRow> dbs, ActivityPanel activity, BlockingPanel blocking)
    {
        var main = dbs.FirstOrDefault();
        return new List<KpiItem>
        {
            new() { Key = "connections", Label = "Bağlantı", Value = activity.TotalConnections,
                    Unit = "", Severity = Severity.Healthy },
            new() { Key = "running", Label = "Çalışan sorgu", Value = activity.RunningRequests,
                    Unit = "", Severity = Severity.Healthy },
            new() { Key = "blocked", Label = "Bloklanan", Value = blocking.BlockedCount,
                    Unit = "", Severity = blocking.Severity },
            new() { Key = "cache", Label = "Önbellek isabeti", Value = main?.CacheHitPercent ?? 0,
                    Unit = "%", Severity = Severity.Healthy }
        };
    }

    private static string CurrentDatabase(InstanceOptions instance)
    {
        try
        {
            return new Npgsql.NpgsqlConnectionStringBuilder(instance.ConnectionString).Database ?? "";
        }
        catch { return ""; }
    }

    private static string Bytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    // ------------------------------------------------------------------

    private async Task<List<T>> Query<T>(DbConnection conn, string sql, CancellationToken ct)
        => (await conn.QueryAsync<T>(new CommandDefinition(
                sql, commandTimeout: _options.QueryTimeoutSeconds, cancellationToken: ct))).ToList();

    private async Task<T?> QueryOne<T>(DbConnection conn, string sql, CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<T>(new CommandDefinition(
               sql, commandTimeout: _options.QueryTimeoutSeconds, cancellationToken: ct));

    /// <summary>
    /// Tek bir panelin patlaması ekranın tamamını götürmesin - SQL
    /// Server tarafındaki SafeAsync ile aynı disiplin.
    /// </summary>
    private async Task<T?> SafeAsync<T>(LiveSnapshot s, string panel, Func<Task<T>> read)
    {
        try { return await read(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PostgreSQL paneli okunamadı: {Panel}", panel);
            s.Errors.Add(new PanelError(panel, ex.Message));
            return default;
        }
    }

    // --- Dapper satır tipleri -----------------------------------------

    private sealed class PgServerRow
    {
        public string ServerName { get; set; } = "";
        public string ProductVersion { get; set; } = "";
        public string Edition { get; set; } = "";
        public DateTime StartTime { get; set; }
        public string DatabaseName { get; set; } = "";
    }

    public sealed class PgDatabaseRow
    {
        public string DatabaseName { get; set; } = "";
        public decimal? CacheHitPercent { get; set; }
        public long BlocksRead { get; set; }
        public long BlocksHit { get; set; }
        public long Commits { get; set; }
        public long Rollbacks { get; set; }
        public long Deadlocks { get; set; }
        public long TempFiles { get; set; }
        public long TempBytes { get; set; }
        public long ChecksumFailures { get; set; }
        public DateTime? StatsReset { get; set; }
        public bool AllowConnections { get; set; }
    }

    public sealed class PgSequenceRow
    {
        public string SchemaName { get; set; } = "";
        public string ObjectName { get; set; } = "";
        public long LastValue { get; set; }
        public long MaxValue { get; set; }
        public decimal? UsedPercent { get; set; }
    }

    public sealed class PgVacuumRow
    {
        public string SchemaName { get; set; } = "";
        public string TableName { get; set; } = "";
        public long LiveTuples { get; set; }
        public long DeadTuples { get; set; }
        public decimal? DeadPercent { get; set; }
        public DateTime? LastVacuum { get; set; }
        public DateTime? LastAnalyze { get; set; }
        public long SizeBytes { get; set; }
    }

    public sealed class PgReplicationRow
    {
        public int ReplicaCount { get; set; }
        public int SlotCount { get; set; }
        public int InactiveSlots { get; set; }
        public long ArchivedCount { get; set; }
        public long FailedCount { get; set; }
        public DateTime? LastFailedTime { get; set; }
    }

    private sealed class SessionCountRow
    {
        public int RunningRequests { get; set; }
        public int SleepingSessions { get; set; }
        public int TotalConnections { get; set; }
        public int OpenTransactions { get; set; }
        public int MaxConnections { get; set; }
    }
}
