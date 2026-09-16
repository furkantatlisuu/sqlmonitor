using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlMonitor.Models;
using SqlMonitor.Options;
using SqlMonitor.Sql;

namespace SqlMonitor.Services;

/// <summary>
/// Kural motoru.
///
/// Bu sınıf uygulamanın en değerli parçası. Metrik göstermek kolaydır;
/// "PLE 648" yazan bir ekran kimseye yardım etmez. Değerli olan
/// "RAM ihtiyacı var mı? → evet, çünkü ..." diyebilmektir.
///
/// Her kural şu üçlüyü üretir: soru, bulgu, hüküm. Soru sabit;
/// bulgu sunucudan gelir; hüküm eşiklerden çıkar. Ayrıca her kural
/// skordan kaç puan götürdüğünü söyler, böylece "önce neyi düzelteyim"
/// sorusunun cevabı listeyi sıralamakla verilir.
/// </summary>
public sealed class HealthEvaluator
{
    private readonly MonitorOptions _options;
    private readonly BackupHistoryReader _backupHistory;
    private readonly IdentityColumnScanner _identityScanner;
    private readonly AlwaysOnHealthCache _alwaysOn;
    private readonly AgentJobHealthCache _agentJobs;

    public HealthEvaluator(
        IOptions<MonitorOptions> options, BackupHistoryReader backupHistory,
        IdentityColumnScanner identityScanner, AlwaysOnHealthCache alwaysOn,
        AgentJobHealthCache agentJobs)
    {
        _options = options.Value;
        _backupHistory = backupHistory;
        _identityScanner = identityScanner;
        _alwaysOn = alwaysOn;
        _agentJobs = agentJobs;
    }

    // Kategoriler skorun üç alt bileşenine karşılık gelir.
    private const string Performance = "performans";
    private const string Reliability = "güvenilirlik";
    private const string Capacity = "kapasite";

    public async Task<HealthPanel> EvaluateAsync(
        SqlConnection conn, InstanceOptions instance, LiveSnapshot s, CancellationToken ct)
    {
        var t = _options.Thresholds;
        var checks = new List<HealthCheck>();

        // --- Bellek ---------------------------------------------------
        checks.Add(new HealthCheck
        {
            Key = "memory",
            Question = "RAM ihtiyacı var mı?",
            Category = Performance,
            Severity = s.Memory.Severity,
            Finding = $"PLE {s.Memory.PageLifeExpectancy} sn, bekleyen grant {s.Memory.PendingGrants}, " +
                      $"bellek {s.Memory.CommittedGb:0.#}/{s.Memory.TargetGb:0.#} GB",
            Remedy = s.Memory.Severity == Severity.Healthy
                ? null
                : "Max server memory ayarını ve en çok okuma yapan sorguları gözden geçir. " +
                  "PLE düşüklüğü çoğu zaman RAM eksikliği değil, gereksiz tablo taramasıdır."
        });

        // --- Bloklama -------------------------------------------------
        checks.Add(new HealthCheck
        {
            Key = "blocking",
            Question = "Lock / bloklama var mı?",
            Category = Performance,
            Severity = s.Blocking.Severity,
            Finding = s.Blocking.BlockedCount == 0
                ? "şu an bloklanan istek yok"
                : $"{s.Blocking.BlockedCount} bloklanan istek, baş engelleyici " +
                  $"SPID {s.Blocking.HeadBlockerSessionId?.ToString() ?? "?"}, " +
                  $"en uzunu {s.Blocking.LongestBlockMs / 1000} sn",
            Remedy = s.Blocking.BlockedCount == 0
                ? null
                : "Baş engelleyiciyi incele. Öldürmeden önce ne yaptığına bak: " +
                  "uzun süren bir raporsa öldürmek rollback maliyeti doğurur."
        });

        // --- CPU ------------------------------------------------------
        checks.Add(new HealthCheck
        {
            Key = "cpu",
            Question = "CPU baskısı var mı?",
            Category = Performance,
            Severity = s.Cpu.Severity,
            Finding = $"SQL CPU %{s.Cpu.SqlCpuPercent}, runnable task {s.Cpu.RunnableTasks} / " +
                      $"{s.Cpu.SchedulerCount} scheduler" +
                      (s.Cpu.OtherCpuPercent > 20
                          ? $", SQL dışı süreçler %{s.Cpu.OtherCpuPercent}"
                          : ""),
            Remedy = s.Cpu.Severity == Severity.Healthy
                ? null
                : "En çok CPU yakan sorgulara bak. runnable task sayısı scheduler sayısını " +
                  "aşıyorsa CPU yüzdesi düşük olsa bile gerçek bir kuyruk var."
        });

        // --- tempdb ---------------------------------------------------
        checks.Add(new HealthCheck
        {
            Key = "tempdb",
            Question = "tempdb sağlıklı mı?",
            Category = Capacity,
            Severity = s.TempDb.Severity,
            Finding = $"%{100 - s.TempDb.UsedPercent:0.#} boş " +
                      $"({s.TempDb.UsedMb:0}/{s.TempDb.TotalMb:0} MB), " +
                      $"version store {s.TempDb.VersionStoreMb:0} MB, " +
                      $"{s.TempDb.DataFileCount} veri dosyası",
            Remedy = s.TempDb.AllocationContention > 0
                ? "Ayırma sayfası çekişmesi var. tempdb veri dosyası sayısını çekirdek " +
                  "sayısına kadar (en fazla 8) artırmayı değerlendir."
                : null
        });

        // --- Disk -----------------------------------------------------
        var fullestVolume = s.Disk.Volumes.FirstOrDefault();
        checks.Add(new HealthCheck
        {
            Key = "disk",
            Question = "Disklerin durumu temiz mi?",
            Category = Capacity,
            Severity = s.Disk.Severity,
            Finding = fullestVolume is null
                ? "volume bilgisi okunamadı"
                : $"en dolu volume {fullestVolume.Mount} %{fullestVolume.UsedPercent:0.#} dolu " +
                  $"({fullestVolume.FreeGb:0.#} GB boş)",
            Remedy = s.Disk.Severity == Severity.Healthy ? null : "Yer aç veya volume büyüt."
        });

        // --- I/O gecikmesi: OKUMA ve YAZMA ayrı puanlanır --------------
        //
        // ESKİDEN tek bir "io" kuralı, sunucu açılışından beri kümülatif
        // ortalamayı doğrudan alarma çeviriyordu - 45 gün uptime'ı olan bir
        // sunucuda "446 ms yazma gecikmesi" büyük ihtimalle geçmişte
        // yaşanmış, artık bitmiş bir olayın kalıntısıydı ama ekranda kalıcı
        // bir kırmızı rozet olarak asılı duruyordu.
        //
        // ŞİMDİ iki değişiklik var:
        //   1) Kümülatif ortalama (WorstReadMs/WorstWriteMs) SADECE bilgi -
        //      Severity'ye hiç girmiyor. Alarmı DELTA (iki örnekleme
        //      arası ortalama) sürüyor, üst üste en az
        //      LiveMonitorService.RequiredBreachStreak (3) örneklem eşiğin
        //      üzerinde kalmadan hiçbir şey tetiklenmiyor - bkz.
        //      LiveMonitorService.ReadIoAsync.
        //   2) Okuma ve yazma ARTIK AYRI kural, ayrı puan. Sebebi: aynı
        //      dosyada yazma yüksek ama okuma düşükse bu "disk yavaş"
        //      değil, daha spesifik bir işarettir (log flush/yazma
        //      tıkanıklığı) - tek bir "disk" kuralı bu ayrımı kaybederdi.
        checks.Add(new HealthCheck
        {
            Key = "io_read",
            Question = "Okuma gecikmesi kabul edilebilir mi?",
            Category = Performance,
            Severity = s.Io.ReadSeverity,
            Finding = s.Io.DeltaReadLatencyMs is decimal dr
                ? $"son pencerede {dr:0.#} ms ({s.Io.SlowestDeltaReadFile}) · " +
                  $"{s.Io.ReadBreachStreak} örneklemedir eşiğin üzerinde " +
                  $"· açılıştan beri ortalama {s.Io.WorstReadMs:0.#} ms"
                : $"henüz yeterli örnek yok · açılıştan beri ortalama {s.Io.WorstReadMs:0.#} ms ({s.Io.SlowestFile ?? "-"})",
            Remedy = s.Io.ReadSeverity == Severity.Healthy
                ? null
                : "Okuma gecikmesi birden fazla örneklemde sürüyor - disk/SAN katmanında " +
                  "gerçek, güncel bir sorun olabilir."
        });

        checks.Add(new HealthCheck
        {
            Key = "io_write",
            Question = "Yazma gecikmesi kabul edilebilir mi?",
            Category = Performance,
            Severity = s.Io.WriteSeverity,
            Finding = s.Io.DeltaWriteLatencyMs is decimal dw
                ? $"son pencerede {dw:0.#} ms ({s.Io.SlowestDeltaWriteFile}) · " +
                  $"{s.Io.WriteBreachStreak} örneklemedir eşiğin üzerinde " +
                  $"· açılıştan beri ortalama {s.Io.WorstWriteMs:0.#} ms"
                : $"henüz yeterli örnek yok · açılıştan beri ortalama {s.Io.WorstWriteMs:0.#} ms ({s.Io.SlowestFile ?? "-"})",
            Remedy = s.Io.WriteSeverity == Severity.Healthy
                ? null
                : (s.Io.DeltaReadLatencyMs is decimal rr && rr < t.ReadLatencyWarnMs
                    ? "Aynı pencerede okuma gecikmesi düşük - bu genel bir disk yavaşlığından çok, " +
                      "yazma tarafında bir tıkanıklık (log flush, yazma kuyruğu) işareti."
                    : "Okuma da etkileniyor - disk/SAN katmanında genel bir yavaşlama olabilir.")
        });

        // --- Yedekler ve log truncate ---------------------------------
        // Ikisi de ayni sorgudan besleniyor. Ayri ayri calistirsaydik
        // her 30 saniyede msdb.dbo.backupset'i iki kez taramis olurduk;
        // buyuk bir msdb'de bu ihmal edilebilir bir maliyet degil.
        try
        {
            var backupRows = (await conn.QueryAsync<BackupRow>(new CommandDefinition(
                DmvSql.BackupStatus, commandTimeout: _options.QueryTimeoutSeconds,
                cancellationToken: ct))).ToList();

            // Always On: yedek bu makinede degil, baska bir replikada
            // aliniyor olabilir. msdb replikalar arasi senkronize
            // edilmedigi icin yerel gecmis tek basina yaniltir - uzak
            // replikalarin gecmisini de katip en yeni tarihi aliyoruz.
            var remote = await _backupHistory.ReadAsync(instance, conn, ct);
            MergeRemoteHistory(backupRows, remote);

            checks.Add(EvaluateBackups(backupRows, remote));
            checks.Add(EvaluateLogReuse(backupRows, remote));
        }
        catch (Exception ex)
        {
            checks.Add(Unknown("backup", "Yedekler temiz mi?", Reliability, ex.Message));
            checks.Add(Unknown("log_reuse", "Log'lar truncate olabiliyor mu?", Reliability, ex.Message));
        }

        // --- Uzun süren sorgu -----------------------------------------
        var longest = s.Activity.LongestRunningSeconds;
        checks.Add(new HealthCheck
        {
            Key = "long_running",
            Question = "Uzun süren sorgu var mı?",
            Category = Performance,
            Severity = longest >= t.LongRunningQueryCriticalSeconds ? Severity.Critical
                     : longest >= t.LongRunningQueryWarnSeconds ? Severity.Warning
                     : Severity.Healthy,
            Finding = s.Activity.RunningRequests == 0
                ? "çalışan kullanıcı isteği yok"
                : $"{s.Activity.RunningRequests} çalışan istek, en uzunu {longest} sn"
        });

        // --- Unutulmuş transaction ------------------------------------
        checks.Add(new HealthCheck
        {
            Key = "open_tran",
            Question = "Unutulmuş transaction var mı?",
            Category = Reliability,
            Severity = s.Activity.OpenTransactions > 0 && s.Activity.LongestRunningSeconds > 600
                ? Severity.Warning
                : Severity.Healthy,
            Finding = $"{s.Activity.OpenTransactions} açık transaction",
            Remedy = s.Activity.OpenTransactions > 0
                ? "Açık kalmış transaction log'un truncate olmasını engeller ve kilitleri tutar."
                : null
        });

        // --- Veritabanı durumu ----------------------------------------
        checks.Add(new HealthCheck
        {
            Key = "db_online",
            Question = "Tüm veritabanları online mı?",
            Category = Reliability,
            Severity = s.Server.DatabaseCount == s.Server.OnlineDatabaseCount
                ? Severity.Healthy
                : Severity.Critical,
            Finding = $"{s.Server.DatabaseCount} DB, {s.Server.OnlineDatabaseCount} tanesi ONLINE"
        });

        // --- Bozulma (suspect pages) ------------------------------------
        // Küçük, indeksli bir sistem tablosu - maliyeti önemsiz, diğer
        // "sekmeler dışı" taramaların aksine doğrudan burada, her turda.
        try
        {
            var suspectRows = (await conn.QueryAsync<SuspectPageRow>(new CommandDefinition(
                DmvSql.SuspectPages, commandTimeout: _options.QueryTimeoutSeconds,
                cancellationToken: ct))).ToList();

            checks.Add(EvaluateCorruption(suspectRows));
        }
        catch (Exception ex)
        {
            checks.Add(Unknown("corruption", "Veritabanı bozulması var mı?", Reliability, ex.Message));
        }

        // --- Identity kolonu taşma ---------------------------------------
        // IdentityColumnScanner kendi TTL önbelleğini taşıyor - buradaki
        // çağrı saniyelik döngüde tekrarlansa da arkadaki gerçek tarama
        // yalnızca birkaç dakikada bir çalışır (bkz. IdentityColumnScanner).
        try
        {
            var idRows = await _identityScanner.GetNearLimitAsync(instance, ct);
            checks.Add(EvaluateIdentityColumns(idRows));
        }
        catch (Exception ex)
        {
            checks.Add(Unknown("identity_overflow", "Identity kolonu taşmaya yakın mı?", Capacity, ex.Message));
        }

        // --- Always On senkron sağlığı ---------------------------------
        // AlwaysOnHealthCache 30 saniyelik TTL taşıyor (bkz. kendi notu) -
        // bu kontrol her snapshot'ta çalışıyor, snapshot ise ekran açıkken
        // saniyeler içinde yenileniyor; önbelleksiz hâli izlenen sunucuya
        // her turda ayrı bir bağlantı + 5 sorgu demekti.
        try
        {
            var ag = await _alwaysOn.GetAsync(instance, ct);
            checks.Add(EvaluateAlwaysOn(ag));
        }
        catch (Exception ex)
        {
            checks.Add(Unknown("alwayson", "Always On senkron sağlığı yerinde mi?", Reliability, ex.Message));
        }

        // --- Agent Job başarısızlığı ------------------------------------
        // AgentJobHealthCache 2 dakikalık TTL taşıyor (bkz. kendi notu) -
        // sysjobhistory taraması Agent Jobs sekmesinin manuel tıklamasına
        // göre tasarlandı, 30 saniyede bir sürekli çağrılmaya değil.
        try
        {
            var jobs = await _agentJobs.GetAsync(instance, ct);
            checks.Add(EvaluateAgentJobs(jobs));
        }
        catch (Exception ex)
        {
            checks.Add(Unknown("agent_jobs", "Agent job'lar başarılı mı?", Reliability, ex.Message));
        }

        return BuildPanel(checks);
    }

    /// <summary>
    /// event_type 1/2/3 AKTİF/çözülmemiş bir bozulmayı gösterir (bkz.
    /// DmvSql.SuspectPages üstündeki not - sorgu zaten yalnızca bunları
    /// döndürüyor, 4/5/7 gibi çözülmüş durumlar hiç gelmiyor).
    /// </summary>
    private static HealthCheck EvaluateCorruption(List<SuspectPageRow> rows)
    {
        if (rows.Count == 0)
        {
            return new HealthCheck
            {
                Key = "corruption",
                Question = "Veritabanı bozulması var mı?",
                Category = Reliability,
                Severity = Severity.Healthy,
                Finding = "Bozuk (suspect) sayfa yok"
            };
        }

        // error_count > 1: aynı sayfa tekrar tekrar okunamıyor - tek
        // seferlik bir I/O blip'inden çok, kötüleşen bir donanım arızasına işaret eder.
        var worsening = rows.Count(r => r.ErrorCount > 1);
        var databases = string.Join(", ", rows.Select(r => r.DatabaseName).Distinct(StringComparer.OrdinalIgnoreCase));

        return new HealthCheck
        {
            Key = "corruption",
            Question = "Veritabanı bozulması var mı?",
            Category = Reliability,
            Severity = Severity.Critical,
            Finding = $"{rows.Count} bozuk sayfa - {databases}" +
                      (worsening > 0
                          ? $" ({worsening} tanesi tekrar tekrar okunamıyor - donanım arızası olabilir)"
                          : ""),
            Remedy = "Önce DBCC CHECKDB ile kapsamı doğrula. Temiz bir yedeğin varsa en hızlı kurtarma genelde " +
                     "sayfa/dosya/tam geri yükleme; yedek yoksa DBCC CHECKDB REPAIR_ALLOW_DATA_LOSS son çare - " +
                     "önce disk/donanımı kontrol et, tek bir bozuk sayfa nadiren yalnız gelir."
        };
    }

    /// <summary>
    /// rows zaten IdentityColumnUsage sorgusunda UsedPercent DESC sıralı
    /// geliyor (yalnızca %70 üstündekiler) - rows[0] her zaman en kötüsü.
    /// </summary>
    private static HealthCheck EvaluateIdentityColumns(List<IdentityColumnRow> rows)
    {
        if (rows.Count == 0)
        {
            return new HealthCheck
            {
                Key = "identity_overflow",
                Question = "Identity kolonu taşmaya yakın mı?",
                Category = Capacity,
                Severity = Severity.Healthy,
                Finding = "Sınırına yaklaşan identity kolonu yok"
            };
        }

        var critical = rows.Count(r => r.UsedPercent >= 90);
        var worst = rows[0];

        return new HealthCheck
        {
            Key = "identity_overflow",
            Question = "Identity kolonu taşmaya yakın mı?",
            Category = Capacity,
            Severity = critical > 0 ? Severity.Critical : Severity.Warning,
            Finding = $"{rows.Count} identity kolonu %70'in üzerinde dolu - en kötüsü " +
                      $"{worst.DatabaseName}.{worst.SchemaName}.{worst.TableName}.{worst.ColumnName} " +
                      $"(%{worst.UsedPercent:0.#}, {worst.TypeName})",
            Remedy = "Sınıra ulaşınca o tabloya yapılan TÜM INSERT'ler aniden \"arithmetic overflow\" hatasıyla " +
                     "durur. Kolonu daha büyük bir tipe (örn. int'ten bigint'e) ALTER etmeyi ya da acil " +
                     "durumda seed'i negatife çekip zaman kazanmayı değerlendir."
        };
    }

    /// <summary>
    /// Replika bağlantısı ve senkron sağlığı - frontend'deki AlwaysOn
    /// sekmesinin statusPill eşikleriyle AYNI (HEALTHY/CONNECTED iyi,
    /// PARTIALLY_HEALTHY uyarı, geri kalan her şey kritik). AG hiç
    /// yapılandırılmamışsa (ag.HasAvailabilityGroup=false) bu sunucu
    /// için anlamsız - sessizce Healthy.
    /// </summary>
    private static HealthCheck EvaluateAlwaysOn(AlwaysOnResult ag)
    {
        if (!ag.HasAvailabilityGroup)
        {
            return new HealthCheck
            {
                Key = "alwayson",
                Question = "Always On senkron sağlığı yerinde mi?",
                Category = Reliability,
                Severity = Severity.Healthy,
                Finding = "AG yapılandırılmamış"
            };
        }

        var disconnected = ag.Replicas.Where(r => r.ConnectedState != "CONNECTED").ToList();
        var unhealthyReplicas = ag.Replicas.Where(r => r.SynchronizationHealth == "NOT_HEALTHY").ToList();
        var partialReplicas = ag.Replicas.Where(r => r.SynchronizationHealth == "PARTIALLY_HEALTHY").ToList();
        var suspendedDbs = ag.Databases.Where(d => d.IsSuspended).ToList();
        var unhealthyDbs = ag.Databases.Where(d => d.SynchronizationHealth == "NOT_HEALTHY").ToList();
        var partialDbs = ag.Databases.Where(d => d.SynchronizationHealth == "PARTIALLY_HEALTHY").ToList();

        string Finding()
        {
            if (disconnected.Count > 0)
                return $"{disconnected.Count} replika bağlı değil: " +
                       string.Join(", ", disconnected.Select(r => r.ReplicaServerName));
            if (unhealthyReplicas.Count > 0)
                return $"{unhealthyReplicas.Count} replika NOT_HEALTHY: " +
                       string.Join(", ", unhealthyReplicas.Select(r => r.ReplicaServerName));
            if (suspendedDbs.Count > 0)
                return $"{suspendedDbs.Count} veritabanı SUSPENDED: " +
                       string.Join(", ", suspendedDbs.Select(d => $"{d.DatabaseName}@{d.ReplicaServerName}"));
            if (unhealthyDbs.Count > 0)
                return $"{unhealthyDbs.Count} veritabanı NOT_HEALTHY: " +
                       string.Join(", ", unhealthyDbs.Select(d => $"{d.DatabaseName}@{d.ReplicaServerName}"));
            if (partialReplicas.Count > 0 || partialDbs.Count > 0)
                return $"{partialReplicas.Count} replika, {partialDbs.Count} veritabanı PARTIALLY_HEALTHY";
            return $"{ag.Replicas.Count} replika, hepsi bağlı ve senkron sağlığı yerinde";
        }

        var severity =
            disconnected.Count > 0 || unhealthyReplicas.Count > 0 ||
            suspendedDbs.Count > 0 || unhealthyDbs.Count > 0
                ? Severity.Critical
                : partialReplicas.Count > 0 || partialDbs.Count > 0
                    ? Severity.Warning
                    : Severity.Healthy;

        return new HealthCheck
        {
            Key = "alwayson",
            Question = "Always On senkron sağlığı yerinde mi?",
            Category = Reliability,
            Severity = severity,
            Finding = Finding(),
            Remedy = severity == Severity.Healthy
                ? null
                : "Always On sekmesinden ilgili replika/veritabanına bak. Bağlantı kopmuşsa ağ/replika " +
                  "sunucusunu kontrol et; SUSPENDED ise kasıtlı değilse ALTER DATABASE ... SET HADR RESUME " +
                  "ile devam ettirmeyi değerlendir."
        };
    }

    /// <summary>Disabled job'lar da FailedCount'a dahil - Agent Jobs sekmesiyle aynı sayım.</summary>
    private static HealthCheck EvaluateAgentJobs(AgentJobsResult jobs)
    {
        if (jobs.FailedCount == 0)
        {
            return new HealthCheck
            {
                Key = "agent_jobs",
                Question = "Agent job'lar başarılı mı?",
                Category = Reliability,
                Severity = Severity.Healthy,
                Finding = jobs.Jobs.Count == 0
                    ? "tanımlı job yok"
                    : $"{jobs.Jobs.Count} job, hepsi son çalışmasında başarılı"
            };
        }

        var failedNames = jobs.Jobs
            .Where(j => j.LastRunOutcome == 0)
            .Select(j => j.JobName)
            .Take(3)
            .ToList();
        var suffix = jobs.FailedCount > failedNames.Count ? ", ..." : "";

        return new HealthCheck
        {
            Key = "agent_jobs",
            Question = "Agent job'lar başarılı mı?",
            Category = Reliability,
            Severity = Severity.Critical,
            Finding = $"{jobs.FailedCount} job son çalışmasında başarısız: " +
                      string.Join(", ", failedNames) + suffix,
            Remedy = "Agent Jobs sekmesinden başarısız job'ın son çalışma mesajını incele."
        };
    }

    private sealed class SuspectPageRow
    {
        public string DatabaseName { get; set; } = "";
        public int FileId { get; set; }
        public long PageId { get; set; }
        public int EventType { get; set; }
        public int ErrorCount { get; set; }
        public DateTime LastUpdateDate { get; set; }
    }

    /// <summary>
    /// Uzak replikalardan gelen tarihleri yerel satırlara işler.
    ///
    /// SADECE tarihler taşınır. RecoveryModel, LogReuseWait ve StateDesc
    /// yerel kalmak zorunda: onlar bu replikanın kendi gerçekleridir.
    /// Secondary'deki log_reuse_wait değerini primary'nin satırına
    /// yazsaydık, "log truncate olabiliyor mu" hükmünü tamamen alakasız
    /// bir makinenin durumuna göre vermiş olurduk.
    /// </summary>
    private static void MergeRemoteHistory(List<BackupRow> rows, RemoteBackupHistory remote)
    {
        if (remote.ByDatabase.Count == 0) return;

        foreach (var row in rows)
        {
            if (!remote.ByDatabase.TryGetValue(row.DatabaseName, out var entry)) continue;

            if (entry.LastFullBackup is not null &&
                (row.LastFullBackup is null || entry.LastFullBackup > row.LastFullBackup))
            {
                row.LastFullBackup = entry.LastFullBackup;
                row.FullBackupSource = entry.FullBackupSource;
            }

            if (entry.LastLogBackup is not null &&
                (row.LastLogBackup is null || entry.LastLogBackup > row.LastLogBackup))
            {
                row.LastLogBackup = entry.LastLogBackup;
                row.LogBackupSource = entry.LogBackupSource;
            }
        }
    }

    private HealthCheck EvaluateBackups(List<BackupRow> rows, RemoteBackupHistory remote)
    {
        var t = _options.Thresholds;

        {
            // Hiç FULL yedeği olmayan DB, eski yedeği olandan çok daha kötüdür.
            var never = rows.Count(r => r.LastFullBackup is null);
            var stale = rows.Count(r => r.LastFullBackup is not null &&
                                        (DateTime.Now - r.LastFullBackup.Value).TotalDays > t.BackupAgeCriticalDays);
            var aging = rows.Count(r => r.LastFullBackup is not null &&
                                        (DateTime.Now - r.LastFullBackup.Value).TotalDays > t.BackupAgeWarnDays &&
                                        (DateTime.Now - r.LastFullBackup.Value).TotalDays <= t.BackupAgeCriticalDays);

            var severity = never > 0 || stale > 0 ? Severity.Critical
                         : aging > 0 ? Severity.Warning
                         : Severity.Healthy;

            // "Yedek yok" ile "yedeğe bakamadım" ayrı şeylerdir.
            //
            // Yedek başka bir replikada alınırken oraya ulaşamadıysak,
            // elimizdeki veri "yedek yok" demeye yetmez. Kritik alarm
            // basmak burada zarar verir: insanlar var olmayan bir sorun
            // için koşturur, sonra alarma güvenmemeyi öğrenir. Bilmediğimizi
            // bilmediğimiz gibi söylüyoruz - Unknown skordan puan götürmez.
            //
            // Yerelde BAYAT yedek bulduysak o gerçek bir bulgudur,
            // eksik replikaya rağmen kritik kalır.
            if (remote.Failures.Count > 0 && never > 0 && stale == 0)
                severity = Severity.Unknown;

            var finding = never > 0
                ? $"{never} DB'nin hiç FULL yedeği yok, {stale} tanesi {t.BackupAgeCriticalDays}+ gün eski"
                : stale > 0
                    ? $"{stale} DB'nin yedeği {t.BackupAgeCriticalDays}+ gün eski"
                    : aging > 0
                        ? $"{aging} DB'nin yedeği 1 günden eski"
                        : $"{rows.Count} DB'nin yedeği güncel";

            // Yedeğin nerede bulunduğunu yazmak önemli: "bu DB'nin yedeği
            // var" demek yetmez, kullanıcı yedeğin hangi makinede
            // durduğunu bilmeden kurtarma planı yapamaz.
            var remoteBacked = rows
                .Where(r => r.FullBackupSource is not null)
                .ToList();

            if (remoteBacked.Count > 0)
            {
                var where = string.Join(", ", remoteBacked
                    .Select(r => r.FullBackupSource!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

                finding += $" · {remoteBacked.Count} DB'nin yedeği {where} üzerinde bulundu";
            }
            else if (remote.HasAnySource)
            {
                finding += $" · {remote.SourcesRead.Count} replika da tarandı";
            }

            if (remote.Failures.Count > 0)
                finding += $" · {remote.Failures.Count} replika okunamadı";

            // Ulaşılamayan bir replika varken "yedek yok" demek, yanlış
            // alarm riskini geri getirir. Hükmü değiştirmiyoruz ama
            // kullanıcının bu belirsizliği görmesi gerekiyor.
            var incomplete = remote.Failures.Count > 0
                ? "Not: " + string.Join("; ", remote.Failures) +
                  " — bu replikalar okunamadığı için orada alınmış yedekler bu bulguya yansımamış olabilir."
                : null;

            // Hüküm "doğrulanamadı" ise bulgu da öyle konuşmalı; yoksa
            // ekranda "yedeği yok" yazıp yanında gri rozet durur.
            if (severity == Severity.Unknown)
            {
                finding = $"{never} DB'nin yedeği bu makinede görünmüyor ve " +
                          $"{remote.Failures.Count} replika okunamadı — yedek durumu doğrulanamadı";
            }

            var advice = severity == Severity.Healthy || severity == Severity.Unknown
                ? null
                : "Hiç FULL yedeği olmayan bir veritabanı, kurtarılamaz bir veritabanıdır. " +
                  "Önce tam yedeği al, sonra log yedek zincirini kur.";

            return new HealthCheck
            {
                Key = "backup",
                Question = "Yedekler temiz mi?",
                Category = Reliability,
                Severity = severity,
                Finding = finding,
                Remedy = advice is null
                    ? incomplete
                    : incomplete is null
                        ? advice
                        : advice + " " + incomplete
            };
        }
    }

    /// <summary>
    /// Log truncate edilebiliyor mu?
    ///
    /// DIKKAT - burada eskiden bir yanlış alarm vardı:
    /// log_reuse_wait_desc = LOG_BACKUP, FULL recovery modelinde NORMAL
    /// ve GEÇİCİ bir durumdur. Log yedeği alındıktan saniyeler sonra
    /// herhangi bir işlem yazar yazmaz veritabanı yine bu duruma döner.
    /// 15 dakikada bir log yedeği alan sağlıklı bir sunucuda bunu görmek
    /// beklenen şeydir; tek başına uyarı sebebi değildir. Öyle saymak,
    /// düzgün çalışan her sunucuda kalıcı bir sarı rozet demekti.
    ///
    /// Gerçek sorun LOG_BACKUP'ın GEÇMEMESİDİR: yedek job'ı durmuşsa log
    /// dosyası sınırsız büyür. O yüzden hüküm artık durumun kendisine
    /// değil, son log yedeğinin YAŞINA bakıyor.
    ///
    /// LastLogBackup burada AG replikalarından gelen tarihlerle
    /// birleştirilmiş hâldedir - yedek başka makinede alınıyor olsa da
    /// doğru cevabı verir.
    ///
    /// remote AYRICA "ulaşılamayan replika var mı" sorusuna cevap vermek
    /// için lazım - bkz. aşağıdaki "overdue" dalındaki not. EvaluateBackups
    /// (yukarısı) bu korumayı zaten taşıyordu, EvaluateLogReuse'ta EKSİKTİ -
    /// remote parametresi hiç alınmıyordu. Canlıda AG replika ana bilgisayar
    /// adları (SQLNODE3 vb.) bazı ağlarda DNS'ten çözülmüyor (yalnızca
    /// IP'ler çözülüyor, bkz. [[ag-topology]] belleği) - otomatik keşif
    /// bu replikalara ulaşamayınca remote.Failures dolar ve düzeltilmezse
    /// bu metot yereldeki (eksik) geçmişe bakıp yanlış alarm basardı.
    /// </summary>
    private HealthCheck EvaluateLogReuse(List<BackupRow> rows, RemoteBackupHistory remote)
    {
        var t = _options.Thresholds;

        var waiting = rows
            .Where(r => string.Equals(r.LogReuseWait, "LOG_BACKUP", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Yedeği gerçekten gecikmiş olanlar: hiç log yedeği yok ya da
        // sonuncusu eşikten eski. Sarı rozeti hak eden küme budur.
        var overdue = waiting
            .Where(r => r.LastLogBackup is null ||
                        (DateTime.Now - r.LastLogBackup.Value).TotalMinutes > t.LogBackupAgeWarnMinutes)
            .ToList();

        // "LOG_BACKUP dışı sebep" tek bir kova DEĞİL - burada eskiden bir
        // yanlış alarm daha vardı. OLDEST_PAGE, CHECKPOINT ve
        // DATABASE_SNAPSHOT_CREATION, motorun kendi iç mekaniğidir:
        // indirect checkpoint hedefi henüz yetişmemiş ya da bir snapshot
        // oluşturuluyor - ikisi de bir sonraki checkpoint'te KENDİLİĞİNDEN
        // geçer, LOG_BACKUP'ın "yedek alınınca geçer" durumuyla aynı ruhtadır.
        // Canlı sunucuda doğrulandı: DATABASE_SNAPSHOT_CREATION görünürken
        // sys.databases'te aktif TEK BİR snapshot bile yoktu - yani bu bir
        // gecikmiş/askıda kalmış rozet, gerçek bir engelleyici değil.
        //
        // Gerçekten kendiliğinden geçmeyen sebepler - açık kalmış bir
        // transaction, duran replikasyon, gerilemiş bir AG replikası -
        // hâlâ kritik. Fark, "motor meşgul" ile "bir şey sıkışmış" arasında.
        var transientReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OLDEST_PAGE", "CHECKPOINT", "DATABASE_SNAPSHOT_CREATION"
        };

        bool IsOtherReason(BackupRow r) =>
            !string.IsNullOrEmpty(r.LogReuseWait) &&
            !string.Equals(r.LogReuseWait, "NOTHING", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r.LogReuseWait, "LOG_BACKUP", StringComparison.OrdinalIgnoreCase);

        var trueBlockers = rows.Where(r => IsOtherReason(r) && !transientReasons.Contains(r.LogReuseWait!)).ToList();
        var engineBusy = rows.Where(r => IsOtherReason(r) && transientReasons.Contains(r.LogReuseWait!)).ToList();

        // engineBusy ARTIK skoru düşürmüyor ve "bekleyen iş" listesinde
        // görünmüyor (Severity.Healthy). Kullanıcı isteği: bu satır zaten
        // kendiliğinden geçen, aksiyon gerektirmeyen bir durum olduğu için
        // sürekli sarı bir rozet olarak asılı kalması yanlış sinyal
        // veriyordu - aşağıdaki Finding metni yine de nedeni açıklıyor,
        // yalnızca dikkat/puan kaybına sebep olmuyor.
        var severity = trueBlockers.Count > 0 ? Severity.Critical
                     : overdue.Count > 0 ? Severity.Warning
                     : Severity.Healthy;

        // Ulaşılamayan bir AG replikası varken "log yedeği eski" demek
        // yanlış alarm riski taşır - EvaluateBackups'taki AYNI ilke: o DB'nin
        // gerçek/taze log yedeği tam da okuyamadığımız replikada olabilir.
        // trueBlockers varsa bu düşürme uygulanmaz - Critical zaten Unknown'dan
        // daha kesin bir bulgu, ondan vazgeçmemeli.
        var overdueUnverified = overdue.Count > 0 && remote.Failures.Count > 0 && trueBlockers.Count == 0;
        if (overdueUnverified) severity = Severity.Unknown;

        string finding;
        string? remedy;

        // Hangi DB, hangi sebep - "1 DB, LOG_BACKUP dışı sebep" gibi
        // içi boş bir cümle yerine doğrudan söylüyoruz. Panele bakan kişi
        // "ne olduğunu anlayamadım" diye sormak zorunda kalmasın.
        static string NameReasons(IEnumerable<BackupRow> rows) =>
            string.Join(", ", rows.Select(r => $"{r.DatabaseName} ({r.LogReuseWait})"));

        if (trueBlockers.Count > 0)
        {
            finding = $"{NameReasons(trueBlockers)} log'unu truncate edemiyor";
            remedy = "ACTIVE_TRANSACTION, REPLICATION veya AVAILABILITY_REPLICA gibi bir sebep " +
                     "kendiliğinden geçmez; kökünü bul, yoksa log dosyası büyümeye devam eder.";
        }
        else if (engineBusy.Count > 0)
        {
            finding = $"{NameReasons(engineBusy)} — motorun kendi iç işlemi, genelde bir sonraki checkpoint'te geçer";
            remedy = "Aksiyon almana gerek yok. Bu satır birkaç dakikadan uzun süre aynı DB'de " +
                     "takılı kalırsa (ekranı biraz sonra tekrar aç, hâlâ duruyor mu bak) o zaman " +
                     "bir yedek/snapshot işleminin gerçekten sıkışmış olabileceğini düşün.";
        }
        else if (overdueUnverified)
        {
            finding = $"{overdue.Count} DB'nin log yedeği yerel geçmişte {t.LogBackupAgeWarnMinutes} dk'dan eski " +
                      $"görünüyor, ama {remote.Failures.Count} AG replikası okunamadığı için doğrulanamadı — " +
                      "gerçek/taze yedek o replikalardan birinde olabilir.";
            remedy = "Not: " + string.Join("; ", remote.Failures) +
                     " — bu replikalara ulaşılamadığı için orada alınmış bir log yedeği bu bulguya yansımamış " +
                     "olabilir. Gerçekten durduğundan şüpheleniyorsan SQL Agent job geçmişine bak.";
        }
        else if (overdue.Count > 0)
        {
            finding = $"{overdue.Count} DB'nin log yedeği {t.LogBackupAgeWarnMinutes} dk'dan eski";
            remedy = "Log yedek job'ı çalışmıyor olabilir. SQL Agent job geçmişine bak; " +
                     "log yedeği durduğu sürece log dosyası büyümeye devam eder.";
        }
        else if (waiting.Count > 0)
        {
            // Beklenen durum. Yine de sayıyı gösteriyoruz ki kullanıcı
            // panelin bu DB'leri görmezden gelmediğini bilsin.
            var newest = waiting.Max(r => r.LastLogBackup!.Value);
            var minutes = (int)Math.Max(0, (DateTime.Now - newest).TotalMinutes);

            finding = $"{waiting.Count} DB log yedeği bekliyor — normal, " +
                      $"en son log yedeği {minutes} dk önce";
            remedy = null;
        }
        else
        {
            finding = "log truncate'i engelleyen bir şey yok";
            remedy = null;
        }

        return new HealthCheck
        {
            Key = "log_reuse",
            Question = "Log'lar truncate olabiliyor mu?",
            Category = Reliability,
            Severity = severity,
            Finding = finding,
            Remedy = remedy
        };
    }

    /// <summary>
    /// Kontrolleri 0-100 skora çevirir.
    ///
    /// Puanlama basit tutuldu: her kritik 13, her uyarı 4 puan götürür.
    /// Bu sayılar bilimsel değil, kalibrasyon meselesi - kendi sunucunun
    /// karakterine göre değiştirmen normal. Önemli olan, listeyi
    /// "önce neyi düzelteyim" sırasına sokması.
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
        }

        int ScoreFor(string category)
        {
            var subset = checks.Where(c => c.Category == category).ToList();
            if (subset.Count == 0) return 100;

            var penalty = subset.Sum(c => -c.ScoreImpact);
            return Math.Max(0, 100 - penalty);
        }

        var totalPenalty = checks.Sum(c => -c.ScoreImpact);

        return new HealthPanel
        {
            Score = Math.Max(0, 100 - totalPenalty),
            PerformanceScore = ScoreFor(Performance),
            ReliabilityScore = ScoreFor(Reliability),
            CapacityScore = ScoreFor(Capacity),

            // Ekranda çözüm sırasına göre görünsün: en çok puan götüren üstte.
            Checks = checks
                .OrderBy(c => c.ScoreImpact)
                .ThenBy(c => c.Question)
                .ToList()
        };
    }

    private static HealthCheck Unknown(string key, string question, string category, string reason)
        => new()
        {
            Key = key,
            Question = question,
            Category = category,
            Severity = Severity.Unknown,
            Finding = $"değerlendirilemedi — {reason}"
        };

    private sealed class BackupRow
    {
        public string DatabaseName { get; set; } = "";
        public string RecoveryModel { get; set; } = "";
        public DateTime? LastFullBackup { get; set; }
        public DateTime? LastLogBackup { get; set; }
        public string? LogReuseWait { get; set; }
        public string StateDesc { get; set; } = "";

        /// <summary>
        /// Yedek başka bir replikada bulunduysa o makinenin adı; yedek
        /// yerelde bulunduysa null. Bulguda "nerede" demek için.
        /// </summary>
        public string? FullBackupSource { get; set; }

        public string? LogBackupSource { get; set; }
    }
}
