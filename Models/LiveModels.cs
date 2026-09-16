namespace SqlMonitor.Models;

public enum Severity
{
    Healthy = 0,
    Warning = 1,
    Critical = 2,
    Unknown = 3
}

/// <summary>
/// Ekranın tek seferde ihtiyaç duyduğu her şey. Frontend tek uçtan
/// bunu çeker. Tarayıcıdan 12 ayrı istek atmak yerine tek istek:
/// hem daha az round-trip, hem de tüm paneller aynı ana ait olur
/// (paneller arası zaman kayması olmaz).
/// </summary>
public sealed class LiveSnapshot
{
    public string InstanceName { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; }
    public int ElapsedMs { get; set; }

    public ServerInfo Server { get; set; } = new();
    public List<KpiItem> Kpis { get; set; } = new();
    public CpuPanel Cpu { get; set; } = new();
    public MemoryPanel Memory { get; set; } = new();
    public TempDbPanel TempDb { get; set; } = new();
    public DiskPanel Disk { get; set; } = new();
    public IoPanel Io { get; set; } = new();
    public WaitStatsPanel Waits { get; set; } = new();
    public ActivityPanel Activity { get; set; } = new();
    public BlockingPanel Blocking { get; set; } = new();
    public HealthPanel Health { get; set; } = new();

    /// <summary>KPI grafiklerinin kapsadığı zaman aralığı.</summary>
    public TrendWindow Trend { get; set; } = new();

    /// <summary>Paneller tek tek patlayabilir. Hangileri patladı, burada.</summary>
    public List<PanelError> Errors { get; set; } = new();
}

/// <summary>
/// KPI'ların altındaki mini grafiklerin gerçekte ne kadarlık bir süreyi
/// gösterdiği.
///
/// Neden ölçülüp gönderiliyor da sabit yazılmıyor? Çünkü pencere sabit
/// değil: "son 40 örnek" isteniyor ve örnekleri toplayıcı üretiyor. Normal
/// şartlarda 40 x 30 sn = 20 dakika eder, ama toplayıcı bir süre durmuşsa
/// aynı 40 örnek çok daha geniş bir aralığa yayılır. Ekrana sabit bir
/// "son 20 dk" yazsaydık, tam da toplayıcının aksadığı anda yalan söylerdi.
/// Bu yüzden aralık, dönen örneklerin gerçek zaman damgalarından
/// hesaplanıyor.
/// </summary>
public sealed class TrendWindow
{
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }

    /// <summary>Seri başına örnek sayısı (en çok olanı).</summary>
    public int Points { get; set; }

    /// <summary>FromUtc ile ToUtc arası, dakika.</summary>
    public int SpanMinutes { get; set; }
}

public sealed record PanelError(string Panel, string Message);

public sealed class ServerInfo
{
    public string ServerName { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public string ProductLevel { get; set; } = "";
    public string Edition { get; set; } = "";
    public string VersionLine { get; set; } = "";
    public int CpuCount { get; set; }
    public int SchedulerCount { get; set; }
    public int PhysicalMemoryGb { get; set; }
    public bool IsVirtual { get; set; }
    public string VirtualType { get; set; } = "";
    public DateTime StartTimeUtc { get; set; }
    public double UptimeHours { get; set; }
    public int DatabaseCount { get; set; }
    public int OnlineDatabaseCount { get; set; }
}

/// <summary>Üst şeritteki kutulardan biri.</summary>
public sealed class KpiItem
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public decimal? Value { get; set; }
    public string Unit { get; set; } = "";
    public Severity Severity { get; set; } = Severity.Unknown;
    public string? Note { get; set; }

    /// <summary>Son N örneklemenin değerleri; sparkline için.</summary>
    public List<decimal> Trend { get; set; } = new();
}

public sealed class CpuPanel
{
    public int SqlCpuPercent { get; set; }
    public int SystemCpuPercent { get; set; }
    public int OtherCpuPercent { get; set; }
    public int RunnableTasks { get; set; }
    public int SchedulerCount { get; set; }
    public Severity Severity { get; set; }
}

public sealed class MemoryPanel
{
    public long PageLifeExpectancy { get; set; }
    public decimal BufferCacheHitRatio { get; set; }
    public int PendingGrants { get; set; }
    public decimal CommittedGb { get; set; }
    public decimal TargetGb { get; set; }
    public decimal PhysicalGb { get; set; }
    public Severity Severity { get; set; }
}

public sealed class TempDbPanel
{
    public decimal UsedMb { get; set; }
    public decimal TotalMb { get; set; }
    public decimal UsedPercent { get; set; }
    public decimal VersionStoreMb { get; set; }
    public decimal UserObjectsMb { get; set; }
    public decimal InternalObjectsMb { get; set; }
    public int AllocationContention { get; set; }
    public int DataFileCount { get; set; }
    public Severity Severity { get; set; }
}

public sealed class DiskPanel
{
    public List<VolumeInfo> Volumes { get; set; } = new();
    public Severity Severity { get; set; }

    /// <summary>
    /// En dolu hacmin geçmiş trendinden doğrusal projeksiyon - null olabilir
    /// (izleme veritabanı yoksa/erişilemezse). Bkz. Models.DiskForecast.
    /// </summary>
    public DiskForecast? Forecast { get; set; }
}

public sealed class VolumeInfo
{
    public string Mount { get; set; } = "";
    public decimal TotalGb { get; set; }
    public decimal FreeGb { get; set; }
    public decimal UsedPercent { get; set; }
    public Severity Severity { get; set; }
}

public sealed class IoPanel
{
    public List<FileIoRow> Files { get; set; } = new();

    /// <summary>Kümülatif (sunucu açılışından beri) - YALNIZCA bilgi amaçlı, ALARM ÜRETMEZ.</summary>
    public decimal WorstReadMs { get; set; }
    public decimal WorstWriteMs { get; set; }
    public string? SlowestFile { get; set; }

    /// <summary>
    /// Delta (iki örnekleme arası ortalama) - okuma/yazma AYRI puanlanır,
    /// alarmı bunlar sürüyor. İlk örneklemede (henüz taban yokken) null -
    /// "0 ms" göstermek "harika" demek olur, oysa doğrusu "henüz bilmiyoruz".
    /// </summary>
    public decimal? DeltaReadLatencyMs { get; set; }
    public decimal? DeltaWriteLatencyMs { get; set; }
    public string? SlowestDeltaReadFile { get; set; }
    public string? SlowestDeltaWriteFile { get; set; }

    /// <summary>Üst üste kaç örneklemedir ilgili eşiğin üzerinde - bkz. StreakTracker.</summary>
    public int ReadBreachStreak { get; set; }
    public int WriteBreachStreak { get; set; }

    public Severity ReadSeverity { get; set; }
    public Severity WriteSeverity { get; set; }

    /// <summary>İkisinin kötüsü - yalnızca genel "Disk" kartının rengi için.</summary>
    public Severity Severity { get; set; }
}

public sealed class FileIoRow
{
    public string DatabaseName { get; set; } = "";
    public string PhysicalName { get; set; } = "";
    public string FileType { get; set; } = "";

    /// <summary>Kümülatif ortalama - bilgi amaçlı.</summary>
    public decimal ReadLatencyMs { get; set; }
    public decimal WriteLatencyMs { get; set; }
    public decimal SizeMb { get; set; }

    /// <summary>Ham kümülatif sayaçlar - delta hesabı için, bkz. LiveMonitorService.ReadIoAsync.</summary>
    public long IoStallReadMs { get; set; }
    public long NumOfReads { get; set; }
    public long IoStallWriteMs { get; set; }
    public long NumOfWrites { get; set; }
}

/// <summary>
/// "En çok bekleme" kartı - sys.dm_os_wait_stats'in gürültüsüz (bkz.
/// DmvSql.WaitStats'teki filtre) ve DELTA'lanmış (iki örnekleme arası,
/// CounterDeltaTracker ile) hâli. İlk örneklemede Top boş olur - henüz
/// taban yok, "0 ms/sn" göstermek yanlış olurdu.
/// </summary>
public sealed class WaitStatsPanel
{
    public List<WaitTypeDelta> Top { get; set; } = new();
}

public sealed class WaitTypeDelta
{
    public string WaitType { get; set; } = "";
    public decimal MsPerSecond { get; set; }

    /// <summary>Listelenen (gürültüsüz) toplam içindeki payı - 0 ile 100 arası.</summary>
    public decimal SharePercent { get; set; }
}

public sealed class ActivityPanel
{
    public int RunningRequests { get; set; }
    public int SleepingSessions { get; set; }
    public int TotalConnections { get; set; }
    public int OpenTransactions { get; set; }
    public long LongestRunningSeconds { get; set; }
    public List<RequestRow> Requests { get; set; } = new();
}

public sealed class RequestRow
{
    public int SessionId { get; set; }
    public string LoginName { get; set; } = "";
    public string HostName { get; set; } = "";
    public string ProgramName { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Command { get; set; } = "";
    public string WaitType { get; set; } = "";
    public long WaitTimeMs { get; set; }
    public long ElapsedMs { get; set; }
    public long CpuMs { get; set; }
    public long LogicalReads { get; set; }
    public long Writes { get; set; }
    public int BlockedBy { get; set; }
    public int OpenTransactionCount { get; set; }
    public string SqlText { get; set; } = "";
}

public sealed class BlockingPanel
{
    public int BlockedCount { get; set; }
    public int BlockerCount { get; set; }
    public int? HeadBlockerSessionId { get; set; }
    public long LongestBlockMs { get; set; }
    public List<BlockRow> Chains { get; set; } = new();
    public Severity Severity { get; set; }
}

public sealed class BlockRow
{
    public int BlockedSessionId { get; set; }
    public int BlockingSessionId { get; set; }
    public long WaitTimeMs { get; set; }
    public string WaitType { get; set; } = "";
    public string WaitResource { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string BlockedSql { get; set; } = "";
    public string BlockerSql { get; set; } = "";
}

public sealed class HealthPanel
{
    public int Score { get; set; }
    public int PerformanceScore { get; set; }
    public int ReliabilityScore { get; set; }
    public int CapacityScore { get; set; }
    public List<HealthCheck> Checks { get; set; } = new();
}

public sealed class HealthCheck
{
    public string Key { get; set; } = "";
    public string Question { get; set; } = "";
    public string Finding { get; set; } = "";
    public Severity Severity { get; set; }
    public int ScoreImpact { get; set; }
    public string Category { get; set; } = "";
    public string? Remedy { get; set; }
}

public sealed class TimelineEntry
{
    public long EventId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string RuleKey { get; set; } = "";
    public Severity Severity { get; set; }
    public Severity PrevSeverity { get; set; }
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
}

public sealed class ErrorLogEntry
{
    public DateTime LogDate { get; set; }
    public string ProcessInfo { get; set; } = "";
    public string Text { get; set; } = "";
    public string Severity { get; set; } = "INFO";
}

// ------------------------------------------------------------------
// Index Analizi
// ------------------------------------------------------------------

/// <summary>
/// "Index Analizi" sekmesinin tamamı - şu an yalnızca eksik index önerileri.
///
/// Sunucunun açık kalma süresi burada YOK - snapshot'ta zaten var
/// (server.uptimeHours) ve arayüz "bu öneriler ne kadarlık bir pencereyi
/// kapsıyor" notunu oradan okuyor. Aynı bilgiyi ikinci bir DMV
/// sorgusuyla tekrar çekmek gereksiz bir gidiş-dönüş olurdu.
/// </summary>
public sealed class MissingIndexResult
{
    public int ElapsedMs { get; set; }
    public List<MissingIndexRow> Rows { get; set; } = new();
}

/// <summary>
/// Tek bir eksik index önerisi. sys.dm_db_missing_index_* satırından
/// doğrudan geliyor - bir plan XML'inden ayrıştırma gerekmiyor; kolon
/// listeleri SQL Server'ın kendisi "[Kolon1], [Kolon2]" biçiminde üretiyor.
/// </summary>
public sealed class MissingIndexRow
{
    public string DatabaseName { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public string TableName { get; set; } = "";

    public string EqualityColumns { get; set; } = "";
    public string InequalityColumns { get; set; } = "";
    public string IncludeColumns { get; set; } = "";

    public long Seeks { get; set; }
    public long Scans { get; set; }
    public decimal AvgTotalUserCost { get; set; }
    public decimal AvgUserImpact { get; set; }
    public decimal Impact { get; set; }
    public DateTime? LastUserSeek { get; set; }
    public DateTime? LastUserScan { get; set; }
    public int UniqueCompiles { get; set; }

    /// <summary>Kopyala-yapıştır çalıştırılabilir CREATE INDEX önerisi.</summary>
    public string SuggestedCreateIndex { get; set; } = "";
}

// ------------------------------------------------------------------
// Stored Procedure
// ------------------------------------------------------------------

/// <summary>
/// "Stored Procedure" sekmesinin tamamı - sys.dm_exec_procedure_stats'ten
/// (prosedür bazında toplam) gelir, ifade bazında değil.
/// </summary>
public sealed class TopProceduresResult
{
    public string SortKey { get; set; } = "";

    /// <summary>Tablo başlığına tıklayınca hangi yönde sıralandığını yansıtmak için.</summary>
    public bool SortDescending { get; set; } = true;
    public int CachedProcCount { get; set; }
    public long TotalCallsAllCache { get; set; }
    public decimal TotalCpuMsAllCache { get; set; }
    public decimal TotalDurationMsAllCache { get; set; }

    public decimal ListedTotalCpuMs { get; set; }
    public decimal ListedTotalDurationMs { get; set; }

    public List<TopProcedureRow> Rows { get; set; } = new();
}

public sealed class TopProcedureRow
{
    /// <summary>Hex string - "İncele" tıklandığında tam tanım için geri yollanır.</summary>
    public string SqlHandle { get; set; } = "";
    public string PlanHandle { get; set; } = "";

    public string DatabaseName { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public string ProcName { get; set; } = "";

    public long Calls { get; set; }
    public decimal TotalCpuMs { get; set; }
    public decimal TotalDurationMs { get; set; }
    public long LogicalReads { get; set; }
    public long LogicalWrites { get; set; }

    public DateTime CachedTime { get; set; }
    public DateTime LastExecutionTime { get; set; }

    /// <summary>Listelenen prosedürler arasında bu satırın toplam süreden aldığı pay.</summary>
    public decimal DurationSharePercent { get; set; }

    public decimal AvgCpuMs => Calls == 0 ? 0 : Math.Round(TotalCpuMs / Calls, 2);
    public decimal AvgDurationMs => Calls == 0 ? 0 : Math.Round(TotalDurationMs / Calls, 2);
    public decimal AvgLogicalReads => Calls == 0 ? 0 : Math.Round((decimal)LogicalReads / Calls, 1);
    public decimal AvgLogicalWrites => Calls == 0 ? 0 : Math.Round((decimal)LogicalWrites / Calls, 1);
}

/// <summary>Bir prosedürün tam tanımı - "İncele" tıklandığında yalnızca o satır için çekilir.</summary>
public sealed class ProcedureDefinitionDetail
{
    public string Definition { get; set; } = "";
}

/// <summary>
/// Kullanılmayan prosedür taraması sonucu. Rows, TOP ile sınırlı liste -
/// Databases'teki UnseenProcs ise SINIRSIZ gerçek toplam (Rows.Count ile
/// karıştırılmasın, tıpkı TopProceduresResult'taki Listed/AllCache ayrımı
/// gibi).
/// </summary>
public sealed class UnusedProceduresResult
{
    public List<UnusedProcedureDbStatus> Databases { get; set; } = new();
    public List<UnusedProcedureRow> Rows { get; set; } = new();
}

/// <summary>
/// Bir veritabanının tarama özeti VE kanıt gücü. QueryStoreEnabled=false
/// olduğunda arayüz bu veritabanının satırlarını "zayıf kanıt" diye
/// işaretlemeli - yalnızca plan cache'e bakılabildi demektir.
/// </summary>
public sealed class UnusedProcedureDbStatus
{
    public string DatabaseName { get; set; } = "";
    public int TotalProcs { get; set; }
    public int UnseenProcs { get; set; }
    public bool QueryStoreEnabled { get; set; }
    public DateTime? QsEarliest { get; set; }
    public DateTime? QsLatest { get; set; }
    public int? QsStaleThresholdDays { get; set; }
}

public sealed class UnusedProcedureRow
{
    public string DatabaseName { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public string ProcName { get; set; } = "";
    public DateTime CreateDate { get; set; }
    public DateTime ModifyDate { get; set; }

    /// <summary>Bu satırın veritabanı için Query Store da kontrol edildi mi
    /// (true) yoksa yalnızca plan cache'e mi bakılabildi (false, zayıf kanıt).</summary>
    public bool QueryStoreEnabled { get; set; }

    /// <summary>
    /// 2=güçlü (QS açık VE proc, QS'in izlemeye başladığı tarihten önce
    /// oluşturulmuş - gözlem penceresinin TAMAMI boyunca hiç görülmedi),
    /// 1=zayıf-yeni (QS açık ama proc pencere içinde/sonrasında oluşturulmuş,
    /// henüz yeterince gözlemlenmedi), 0=zayıf (QS hiç açık değil).
    /// Bkz. DmvSql.UnusedProceduresScan üstündeki not.
    /// </summary>
    public int EvidenceTier { get; set; }
}

/// <summary>
/// Always On sekmesinin tamamı. HasAvailabilityGroup=false ise bu sunucuda
/// AG yapılandırılmamış demektir - geri kalan listeler boş olacaktır,
/// bu bir hata değildir.
/// </summary>
public sealed class AlwaysOnResult
{
    public bool HasAvailabilityGroup { get; set; }
    public List<AgGroupInfo> Groups { get; set; } = new();
    public List<AgReplicaInfo> Replicas { get; set; } = new();
    public List<AgDatabaseReplicaInfo> Databases { get; set; } = new();
    public AgClusterInfo? Cluster { get; set; }
    public List<AgClusterMemberInfo> ClusterMembers { get; set; } = new();
}

public sealed class AgGroupInfo
{
    public string GroupName { get; set; } = "";
    public string? PrimaryReplica { get; set; }
    public string? PrimaryRecoveryHealth { get; set; }
    public string? SecondaryRecoveryHealth { get; set; }
    public string? SynchronizationHealth { get; set; }
}

public sealed class AgReplicaInfo
{
    public string GroupName { get; set; } = "";
    public string ReplicaServerName { get; set; } = "";
    public string? RoleDesc { get; set; }
    public string? ConnectedState { get; set; }
    public string? SynchronizationHealth { get; set; }
    public string? OperationalState { get; set; }
    public string? RecoveryHealth { get; set; }
    public string AvailabilityMode { get; set; } = "";
    public string FailoverMode { get; set; } = "";
    public int BackupPriority { get; set; }
    public bool IsLocal { get; set; }
    public string? LastConnectErrorDescription { get; set; }
    public DateTime? LastConnectErrorTime { get; set; }
}

public sealed class AgDatabaseReplicaInfo
{
    public string GroupName { get; set; } = "";
    public string ReplicaServerName { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string? SynchronizationState { get; set; }
    public string? SynchronizationHealth { get; set; }
    public bool IsSuspended { get; set; }
    public string? SuspendReason { get; set; }
    public long? LogSendQueueKb { get; set; }
    public long? LogSendRateKb { get; set; }
    public long? RedoQueueKb { get; set; }
    public long? RedoRateKb { get; set; }
    public DateTime? LastCommitTime { get; set; }
    public DateTime? LastHardenedTime { get; set; }
    public DateTime? LastRedoneTime { get; set; }
}

public sealed class AgClusterInfo
{
    public string? ClusterName { get; set; }
    public string? QuorumType { get; set; }
    public string? QuorumState { get; set; }
}

public sealed class AgClusterMemberInfo
{
    public string MemberName { get; set; } = "";
    public string MemberType { get; set; } = "";
    public string MemberState { get; set; } = "";
    public int NumberOfQuorumVotes { get; set; }
}

/// <summary>SQL Agent job listesinin tamamı + basit bir özet.</summary>
public sealed class AgentJobsResult
{
    public List<AgentJobRow> Jobs { get; set; } = new();
    public int FailedCount { get; set; }
    public int RunningCount { get; set; }
    public int DisabledCount { get; set; }
}

/// <summary>
/// Tek bir job'un son çalışması. LastRunOutcome SQL Agent'ın kendi kodu:
/// 0 Failed, 1 Succeeded, 2 Retry, 3 Canceled - null ise hiç çalışmamış
/// (job yeni eklenmiş ya da geçmişi temizlenmiş olabilir).
/// </summary>
public sealed class AgentJobRow
{
    public string JobName { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string CategoryName { get; set; } = "";
    public int? LastRunOutcome { get; set; }
    public DateTime? LastRunAt { get; set; }
    public int? LastRunDurationSeconds { get; set; }
    public string? LastRunMessage { get; set; }
    public bool IsCurrentlyRunning { get; set; }
}
