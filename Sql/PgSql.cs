namespace SqlMonitor.Sql;

/// <summary>
/// PostgreSQL sorguları - DmvSql.cs'in karşılığı.
///
/// Hepsi PostgreSQL 18.6'da ÇALIŞTIRILARAK doğrulandı; tahmin yok.
///
/// Takma ad sözdizimi SQL Server'dan FARKLI: burada <c>ifade AS "Ad"</c>
/// yazılır, <c>"Ad" = ifade</c> DEĞİL - ikincisi PostgreSQL'de bir
/// karşılaştırma olarak ayrıştırılır ve sessizce boolean döner.
/// Çift tırnak şart: tırnaksız adlar küçük harfe düşer ve Dapper'ın
/// eşlemesi bozulmaz ama okunaksız olur.
///
/// SQL Server'dan İKİ TEMEL FARK var, bu dosyadaki her şeyi etkiliyor:
///
/// 1) BAZI GÖRÜNÜMLER VERİTABANI BAŞINADIR. pg_stat_user_tables,
///    pg_stat_user_indexes, pg_sequences, pg_stat_statements yalnızca
///    BAĞLANILAN veritabanını görür. SQL Server'da master'a bağlanıp
///    sunucunun tamamını okuyabiliyorduk; burada olmaz. Bu yüzden
///    PostgreSQL instance'ında "veritabanı" alanı zorunlu.
///
/// 2) SAYAÇLAR SIFIRLANABİLİR. pg_stat_* değerleri pg_stat_reset() ile
///    ya da sunucu yeniden başlatılınca sıfırlanır; stats_reset sütunu
///    ne zamandan beri saydığını söyler. Birikimli sayılar hep onunla
///    birlikte okunmalı.
/// </summary>
public static class PgSql
{
    /// <summary>Sunucu kimliği.</summary>
    public const string ServerInfo = """
        SELECT
            coalesce(inet_server_addr()::text, 'localhost') AS "ServerName",
            current_setting('server_version')               AS "ProductVersion",
            'PostgreSQL'                                    AS "Edition",
            pg_postmaster_start_time()                      AS "StartTime",
            current_database()                              AS "DatabaseName";
        """;

    /// <summary>
    /// Anlık aktivite - ActiveRequests'in karşılığı.
    ///
    /// backend_type = 'client backend' filtresi ŞART: autovacuum,
    /// walwriter, checkpointer gibi arka plan süreçleri de bu görünümde
    /// durur ve onları "uzun süren kullanıcı sorgusu" saymak SQL
    /// Server'daki sp_server_diagnostics tuzağının aynısıdır - sürekli
    /// sahte alarm üretirdi.
    ///
    /// ObjectName: PostgreSQL'de "hangi prosedür" sorusunun DOĞRUDAN
    /// cevabı yok - pg_stat_activity yalnızca gönderilen metni tutar.
    /// Metin bir fonksiyon çağrısıysa adı çıkarmaya çalışıyoruz;
    /// çıkmazsa boş bırakıyoruz. Uydurmaktansa boş bırakmak doğru.
    /// </summary>
    public const string ActiveRequests = """
        SELECT
            a.pid                                      AS "SessionId",
            coalesce(a.usename, '')                    AS "LoginName",
            coalesce(host(a.client_addr), coalesce(a.client_hostname, '')) AS "HostName",
            coalesce(a.application_name, '')           AS "ProgramName",
            coalesce(a.datname, '')                    AS "DatabaseName",
            coalesce(a.state, '')                      AS "Status",
            coalesce(a.backend_type, '')               AS "Command",
            coalesce(a.wait_event_type || ':' || a.wait_event, '') AS "WaitType",
            0::bigint                                  AS "WaitTimeMs",
            coalesce((extract(epoch FROM (now() - a.query_start)) * 1000)::bigint, 0) AS "ElapsedMs",
            0::bigint                                  AS "CpuMs",
            0::bigint                                  AS "LogicalReads",
            0::bigint                                  AS "Writes",
            coalesce((SELECT b FROM unnest(pg_blocking_pids(a.pid)) AS b LIMIT 1), 0) AS "BlockedBy",
            CASE WHEN a.xact_start IS NOT NULL THEN 1 ELSE 0 END AS "OpenTransactionCount",
            coalesce(a.query, '')                      AS "SqlText",
            coalesce((regexp_match(a.query,
                '(?i)(?:call|select|perform)\s+([a-z_][a-z0-9_$]*(?:\.[a-z_][a-z0-9_$]*)?)\s*\('))[1], '')
                                                       AS "ObjectName"
        FROM pg_stat_activity a
        WHERE a.backend_type = 'client backend'
          AND a.pid <> pg_backend_pid()
          AND a.state IS NOT NULL
        ORDER BY a.query_start NULLS LAST;
        """;

    /// <summary>Oturum sayıları - SessionCounts karşılığı.</summary>
    public const string SessionCounts = """
        SELECT
            count(*) FILTER (WHERE state = 'active' AND pid <> pg_backend_pid())::int AS "RunningRequests",
            count(*) FILTER (WHERE state = 'idle')::int                               AS "SleepingSessions",
            count(*)::int                                                             AS "TotalConnections",
            count(*) FILTER (WHERE state IN ('idle in transaction',
                                             'idle in transaction (aborted)'))::int   AS "OpenTransactions",
            current_setting('max_connections')::int                                   AS "MaxConnections"
        FROM pg_stat_activity
        WHERE backend_type = 'client backend';
        """;

    /// <summary>
    /// Bloklama zinciri - BlockingChains karşılığı.
    ///
    /// pg_blocking_pids() SQL Server'ın blocking_session_id'sinden DAHA
    /// iyi: gerçek kilit grafiğini çözer ve bekletenlerin TAMAMINI
    /// döndürür, tek bir değer değil. Bekletenin sorgusu da aynı
    /// görünümden okunuyor - SQL Server'da most_recent_sql_handle'a
    /// gitmek zorundaydık, burada gerekmiyor.
    /// </summary>
    public const string BlockingChains = """
        SELECT
            bekleyen.pid                                AS "BlockedSessionId",
            bekleten.pid                                AS "BlockingSessionId",
            coalesce((extract(epoch FROM (now() - bekleyen.query_start)) * 1000)::bigint, 0) AS "WaitTimeMs",
            coalesce(bekleyen.wait_event_type || ':' || bekleyen.wait_event, '') AS "WaitType",
            coalesce(bekleyen.wait_event, '')           AS "WaitResource",
            coalesce(bekleyen.datname, '')              AS "DatabaseName",
            coalesce(bekleyen.query, '')                AS "BlockedSql",
            coalesce(bekleten.query, '')                AS "BlockerSql",
            ''                                          AS "BlockedObjectName",
            ''                                          AS "BlockerObjectName"
        FROM pg_stat_activity bekleyen
        CROSS JOIN LATERAL unnest(pg_blocking_pids(bekleyen.pid)) AS engelleyen(pid)
        JOIN pg_stat_activity bekleten ON bekleten.pid = engelleyen.pid
        WHERE bekleyen.backend_type = 'client backend';
        """;

    /// <summary>
    /// Veritabanı düzeyi sayaçlar: önbellek isabeti, deadlock, geçici
    /// dosya, checksum hatası, boyut.
    ///
    /// SQL Server'da bunlar üç ayrı yerden (Buffer Manager sayaçları,
    /// tempdb DMV'leri, suspect_pages) geliyordu; PostgreSQL hepsini
    /// tek görünümde topluyor.
    /// </summary>
    public const string DatabaseStats = """
        SELECT
            d.datname                                   AS "DatabaseName",
            round(100.0 * d.blks_hit / nullif(d.blks_hit + d.blks_read, 0), 2) AS "CacheHitPercent",
            d.blks_read                                 AS "BlocksRead",
            d.blks_hit                                  AS "BlocksHit",
            d.xact_commit                               AS "Commits",
            d.xact_rollback                             AS "Rollbacks",
            d.deadlocks                                 AS "Deadlocks",
            d.temp_files                                AS "TempFiles",
            d.temp_bytes                                AS "TempBytes",
            coalesce(d.checksum_failures, 0)            AS "ChecksumFailures",
            d.stats_reset                               AS "StatsReset",
            db.datallowconn                             AS "AllowConnections"
        FROM pg_stat_database d
        JOIN pg_database db ON db.datname = d.datname
        WHERE d.datname IS NOT NULL
          AND db.datistemplate = false
        ORDER BY d.datname;
        """;
        /*  pg_database_size() BİLEREK YOK.
            Buradaydı ve pahalıydı: veritabanı dizinini dolaşıyor, bu
            sorguyu çağrı başına ~394 ms'ye çıkarıyordu - kendi
            pg_stat_statements listemizde izlenen sunucunun EN AĞIR
            sorgusu olarak çıktı (toplam sürenin %84'ü). Hem SELECT'te
            hem ORDER BY'da, yani veritabanı başına iki kez
            çağrılıyordu. Döndürdüğü değer ise hiçbir kontrolde
            kullanılmıyordu. "İzlediğin sunucuyu boşuna yorma" ilkesi
            en çok izleme aracının kendisi için geçerli. */

    /// <summary>
    /// Sequence doluluğu - SQL Server'daki IDENTITY taşma kontrolünün
    /// karşılığı, üstelik daha kolay: pg_sequences son değeri ve üst
    /// sınırı hazır veriyor. VERİTABANI BAŞINA çalışır.
    /// </summary>
    public const string SequenceUsage = """
        SELECT
            schemaname                                  AS "SchemaName",
            sequencename                                AS "ObjectName",
            last_value                                  AS "LastValue",
            max_value                                   AS "MaxValue",
            round(100.0 * last_value / nullif(max_value, 0), 4) AS "UsedPercent"
        FROM pg_sequences
        WHERE last_value IS NOT NULL
        ORDER BY 100.0 * last_value / nullif(max_value, 0) DESC NULLS LAST
        LIMIT 200;
        """;

    /// <summary>
    /// Hiç kullanılmayan index'ler. SQL Server'da bunun hazır bir
    /// karşılığı yok; PostgreSQL bedavaya veriyor.
    ///
    /// idx_scan sayacı sıfırlanabilir - stats_reset ile birlikte
    /// okunmalı, yoksa dün sıfırlanmış bir sayaç "bu index hiç
    /// kullanılmıyor" gibi görünür.
    ///
    /// Primary key'ler listeden çıkarılıyor: kullanılmıyor görünseler
    /// bile silinmezler, listede durmaları yalnızca gürültü olur.
    /// </summary>
    public const string UnusedIndexes = """
        SELECT
            s.schemaname                                AS "SchemaName",
            s.relname                                   AS "TableName",
            s.indexrelname                              AS "IndexName",
            s.idx_scan                                  AS "Scans",
            pg_relation_size(s.indexrelid)              AS "SizeBytes",
            i.indisunique                               AS "IsUnique"
        FROM pg_stat_user_indexes s
        JOIN pg_index i ON i.indexrelid = s.indexrelid
        WHERE i.indisprimary = false
        ORDER BY s.idx_scan, pg_relation_size(s.indexrelid) DESC
        LIMIT 100;
        """;

    /// <summary>
    /// Çok taranan tablolar - "eksik index" sinyali.
    ///
    /// PostgreSQL, SQL Server'ın dm_db_missing_index_details'ine denk
    /// bir ÖNERİ üretmez. En yakın dürüst sinyal bu: index yerine
    /// sıralı tarama yiyen ve bu sırada çok satır okuyan tablolar.
    /// "Şu index'i oluştur" demiyor, "buraya bak" diyor - ekranda da
    /// böyle anlatılıyor.
    /// </summary>
    public const string SeqScanHeavyTables = """
        SELECT
            schemaname                                  AS "SchemaName",
            relname                                     AS "TableName",
            seq_scan                                    AS "SeqScans",
            seq_tup_read                                AS "SeqTupleRead",
            coalesce(idx_scan, 0)                       AS "IndexScans",
            n_live_tup                                  AS "LiveTuples",
            pg_relation_size(relid)                     AS "SizeBytes"
        FROM pg_stat_user_tables
        WHERE seq_scan > 0
        ORDER BY seq_tup_read DESC
        LIMIT 50;
        """;

    /// <summary>
    /// Ölü satır (bloat) ve vacuum durumu. SQL Server'da karşılığı yok -
    /// MVCC'ye özgü bir dert. Autovacuum yetişemezse tablo şişer,
    /// sorgular yavaşlar.
    /// </summary>
    public const string VacuumStatus = """
        SELECT
            schemaname                                  AS "SchemaName",
            relname                                     AS "TableName",
            n_live_tup                                  AS "LiveTuples",
            n_dead_tup                                  AS "DeadTuples",
            round(100.0 * n_dead_tup / nullif(n_live_tup + n_dead_tup, 0), 1) AS "DeadPercent",
            greatest(last_vacuum, last_autovacuum)      AS "LastVacuum",
            greatest(last_analyze, last_autoanalyze)    AS "LastAnalyze",
            pg_relation_size(relid)                     AS "SizeBytes"
        FROM pg_stat_user_tables
        WHERE n_dead_tup > 0
        ORDER BY n_dead_tup DESC
        LIMIT 50;
        """;

    /// <summary>
    /// Replikasyon ve WAL arşivi. Kullanıcının kurulumu tek makine, ama
    /// arşiv hatası tek makinede de anlamlı: WAL arşivlenemiyorsa disk
    /// dolar ve sunucu durur.
    /// </summary>
    public const string ReplicationAndWal = """
        SELECT
            (SELECT count(*) FROM pg_stat_replication)::int  AS "ReplicaCount",
            (SELECT count(*) FROM pg_replication_slots)::int AS "SlotCount",
            (SELECT count(*) FROM pg_replication_slots WHERE active = false)::int AS "InactiveSlots",
            a.archived_count                                 AS "ArchivedCount",
            a.failed_count                                   AS "FailedCount",
            a.last_failed_time                               AS "LastFailedTime"
        FROM pg_stat_archiver a;
        """;

    /// <summary>
    /// pg_stat_statements KURULU ve sorgulanabilir mi.
    ///
    /// pg_extension'a bakmak tek başına yetmez: eklenti CREATE EXTENSION
    /// ile kurulmuş ama shared_preload_libraries'e eklenmemişse görünüm
    /// hiç oluşmaz. to_regclass ile gerçekten var olduğunu doğruluyoruz.
    /// </summary>
    public const string HasStatStatements = """
        SELECT to_regclass('pg_stat_statements') IS NOT NULL;
        """;

    /// <summary>
    /// En ağır sorgular - dm_exec_query_stats karşılığı.
    ///
    /// {ORDER_BY} çağıran taraftaki SABİT listeden gelir, kullanıcıdan
    /// değil (DmvSql.TopQueriesTemplate ile aynı disiplin).
    ///
    /// Sütun adları PostgreSQL 13+ biçiminde (total_exec_time,
    /// shared_blks_*); daha eskisinde total_time idi. Kullanıcının
    /// sunucusu 18.
    /// </summary>
    /// <summary>
    /// "En Yoğun Sorgular" listesi - dm_exec_procedure_stats'in
    /// karşılığı, ama PROSEDÜR değil SORGU bazında.
    ///
    /// PostgreSQL'de prosedür/fonksiyon istatistiği ayrı bir yerdedir
    /// (pg_stat_user_functions) ve varsayılan olarak KAPALIDIR
    /// (track_functions = none). Kullanıcının sunucusunda da kapalı.
    /// pg_stat_statements ise her SQL ifadesini tutar - "hangi sorgu
    /// ağır" sorusunun burada gerçek cevabı budur.
    ///
    /// ProcName sütununa sorgunun ilk satırını koyuyoruz: ekran bir ad
    /// bekliyor, PostgreSQL'de sorgunun adı yok. Uydurma bir ad
    /// üretmektense sorgunun kendisinden okunabilir bir parça veriyoruz.
    ///
    /// queryid metin olarak SqlHandle'a konuyor - "İncele" düğmesi tam
    /// metni onunla geri istiyor.
    /// </summary>
    public const string TopStatementsTemplate = """
        SELECT
            s.queryid::text                             AS "SqlHandle",
            ''                                          AS "PlanHandle",
            coalesce(d.datname, current_database())     AS "DatabaseName",
            ''                                          AS "SchemaName",
            left(regexp_replace(s.query, '\s+', ' ', 'g'), 120) AS "ProcName",
            s.calls                                     AS "Calls",
            s.total_exec_time::numeric                  AS "TotalCpuMs",
            s.total_exec_time::numeric                  AS "TotalDurationMs",
            (s.shared_blks_hit + s.shared_blks_read)::bigint AS "LogicalReads",
            s.shared_blks_written::bigint               AS "LogicalWrites",
            now()                                       AS "CachedTime",
            now()                                       AS "LastExecutionTime"
        FROM pg_stat_statements s
        LEFT JOIN pg_database d ON d.oid = s.dbid
        WHERE s.calls > 0
        ORDER BY {ORDER_BY} {DIRECTION}
        LIMIT @Top;
        """;

    /// <summary>Listenin altındaki "tüm cache" özeti.</summary>
    public const string StatementTotals = """
        SELECT
            count(*)::int                               AS "ProcCount",
            coalesce(sum(calls), 0)::bigint             AS "TotalCalls",
            coalesce(sum(total_exec_time), 0)::numeric  AS "TotalCpuMs",
            coalesce(sum(total_exec_time), 0)::numeric  AS "TotalDurationMs"
        FROM pg_stat_statements;
        """;

    /// <summary>"İncele" - queryid'ye göre sorgunun tam metni.</summary>
    public const string StatementTextByQueryId = """
        SELECT left(query, 100000)
        FROM pg_stat_statements
        WHERE queryid::text = @QueryId
        LIMIT 1;
        """;

    public const string TopQueriesTemplate = """
        SELECT
            current_database()                          AS "DatabaseName",
            ''                                          AS "ObjectName",
            s.calls                                     AS "Calls",
            (s.shared_blks_hit + s.shared_blks_read)::bigint AS "TotalLogicalReads",
            ((s.shared_blks_hit + s.shared_blks_read) / greatest(s.calls, 1))::bigint AS "AvgLogicalReads",
            s.shared_blks_read::bigint                  AS "TotalPhysicalReads",
            s.total_exec_time::numeric                  AS "TotalCpuMs",
            (s.total_exec_time / greatest(s.calls, 1))::numeric AS "AvgCpuMs",
            now()                                       AS "LastExecution",
            left(s.query, 2000)                         AS "SqlText"
        FROM pg_stat_statements s
        WHERE s.calls > 0
        ORDER BY {ORDER_BY} DESC
        LIMIT @Top;
        """;
}
