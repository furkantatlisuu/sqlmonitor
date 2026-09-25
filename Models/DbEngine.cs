namespace SqlMonitor.Models;

/// <summary>
/// İzlenen sunucunun motoru. Uygulama SQL Server için yazıldı;
/// PostgreSQL sonradan eklendi (kullanıcı isteği) ve ikisi AYNI ekranı
/// paylaşıyor - ama aynı verileri ÜRETEMİYORLAR.
///
/// Anahtar veritabanında metin olarak duruyor ("mssql"/"postgres"),
/// enum ordinal'i olarak değil: bir gün araya üçüncü bir motor girerse
/// ya da sıralama değişirse kayıtlı satırların anlamı kaymasın.
/// </summary>
public static class DbEngine
{
    public const string SqlServer = "mssql";
    public const string Postgres = "postgres";

    public static bool IsPostgres(string? engine)
        => string.Equals(engine, Postgres, StringComparison.OrdinalIgnoreCase);

    /// <summary>Bilinmeyen/boş değer SQL Server sayılır - eski kayıtlar
    /// motor bilgisi olmadan yazılmıştı, hepsi SQL Server'dı.</summary>
    public static string Normalize(string? engine)
        => IsPostgres(engine) ? Postgres : SqlServer;

    public static string Label(string? engine)
        => IsPostgres(engine) ? "PostgreSQL" : "SQL Server";
}

/// <summary>
/// Bir motorun NEYİ ölçebildiği. Ekran, karşılığı olmayan kartları ve
/// sekmeleri tamamen gizlemek için bunu kullanıyor (kullanıcı tercihi:
/// "hiç görünmesin").
///
/// Boş bir kart göstermektense hiç göstermemek, "bu neden boş?"
/// sorusunu baştan ortadan kaldırıyor. Sağlık skoru da yalnızca
/// ölçülebilen kontroller üzerinden hesaplanıyor - PostgreSQL'de
/// ölçülemeyen 5 kontrolü "sağlıklı" sayıp 100 vermek yalan olurdu,
/// "kritik" saymak ise daha büyük yalan.
/// </summary>
public static class Capability
{
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string TempDb = "tempdb";
    public const string Disk = "disk";
    public const string Io = "io";
    public const string Waits = "waits";
    public const string Backups = "backups";
    public const string AlwaysOn = "alwayson";
    public const string AgentJobs = "agentjobs";
    public const string MissingIndexes = "missingindexes";
    public const string UnusedIndexes = "unusedindexes";
    public const string TopQueries = "topqueries";
    public const string UnusedProcedures = "unusedprocedures";
    public const string ErrorLog = "errorlog";
    public const string KillSession = "killsession";
    public const string QueryPlan = "queryplan";
    public const string Vacuum = "vacuum";
    public const string TableBloat = "tablebloat";

    /// <summary>SQL Server: bugüne kadarki her şey.</summary>
    public static readonly IReadOnlyList<string> SqlServerAll = new[]
    {
        Cpu, Memory, TempDb, Disk, Io, Waits, Backups, AlwaysOn, AgentJobs,
        MissingIndexes, TopQueries, UnusedProcedures, ErrorLog, KillSession, QueryPlan
    };
}
