namespace SqlMonitor.Sql;

/// <summary>
/// İzlenen sunucuya gönderilen bütün sorgular burada.
///
/// Tek dosyada toplu olmalarının sebebi: bir izleme aracının en büyük
/// riski, izlediği sunucuyu yormasıdır. Sorgular koda dağılmış olursa
/// hangi DMV'ye ne sıklıkla dokunduğunu kimse takip edemez. Buraya
/// yeni bir sorgu eklerken kendine şunu sor: bu sorgu 30 saniyede bir,
/// sonsuza kadar çalışacak - maliyeti buna değer mi?
///
/// Ortak kurallar:
///   - Her sorgu OPTION (RECOMPILE) ile biter. DMV sorgularının plan
///     cache'i kirletmesini istemeyiz ve parametre sniffing burada
///     bize hiçbir fayda sağlamaz.
///   - WITH (NOLOCK) kullanılıyor. DMV'ler zaten latch tabanlıdır ama
///     sys.databases / master_files gibi katalog görünümlerinde metadata
///     lock beklememek için gerekli.
///   - Plan XML'i HİÇBİR toplu sorguda çekilmiyor. Plan çekmek pahalıdır;
///     yalnızca kullanıcı tek bir satıra tıkladığında, ayrı uçtan.
/// </summary>
public static class DmvSql
{
    /// <summary>Sunucu künyesi. Örnekleme başına bir kez.</summary>
    public const string ServerInfo = """
        SELECT
            ServerName        = CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')),
            ProductVersion    = CONVERT(nvarchar(50),  SERVERPROPERTY('ProductVersion')),
            ProductLevel      = CONVERT(nvarchar(50),  SERVERPROPERTY('ProductLevel')),
            Edition           = CONVERT(nvarchar(128), SERVERPROPERTY('Edition')),
            VersionLine       = LEFT(@@VERSION, CHARINDEX(CHAR(10), @@VERSION + CHAR(10)) - 1),
            CpuCount          = si.cpu_count,
            SchedulerCount    = si.scheduler_count,
            PhysicalMemoryGb  = CONVERT(int, si.physical_memory_kb / 1048576.0),
            IsVirtual         = CONVERT(bit, CASE WHEN si.virtual_machine_type <> 0 THEN 1 ELSE 0 END),
            VirtualType       = si.virtual_machine_type_desc,
            /* sqlserver_start_time sunucunun YEREL saatidir. C# tarafı UTC ile
               çalıştığı için burada çeviriyoruz; aksi hâlde sunucu ile uygulama
               farklı saat diliminde olduğunda uptime saçmalar. */
            StartTimeUtc      = CONVERT(datetime2(0),
                                    DATEADD(MINUTE,
                                        DATEDIFF(MINUTE, GETDATE(), GETUTCDATE()),
                                        si.sqlserver_start_time)),
            DatabaseCount     = (SELECT COUNT(*) FROM sys.databases WITH (NOLOCK)),
            OnlineDatabaseCount = (SELECT COUNT(*) FROM sys.databases WITH (NOLOCK) WHERE state = 0)
        FROM sys.dm_os_sys_info AS si WITH (NOLOCK)
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Performans sayaçları. cntr_type 272696576 = kümülatif "per second"
    /// sayaç; ham değeri anlamsızdır, iki okuma arasındaki farkı saniyeye
    /// bölmek gerekir. Bu farkı C# tarafında CounterDeltaTracker yapıyor.
    ///
    /// object_name sabit 256 karakter dolgulu gelir ve named instance'ta
    /// 'MSSQL$INSTANCE:' önekiyle başlar. O yüzden = değil LIKE kullanıyoruz.
    /// </summary>
    public const string PerformanceCounters = """
        SELECT
            CounterKey   = RTRIM(counter_name),
            InstanceName = RTRIM(instance_name),
            CounterValue = cntr_value,
            CounterType  = cntr_type
        FROM sys.dm_os_performance_counters WITH (NOLOCK)
        WHERE (RTRIM(counter_name) = N'Batch Requests/sec'      AND object_name LIKE N'%SQL Statistics%')
           OR (RTRIM(counter_name) = N'SQL Compilations/sec'    AND object_name LIKE N'%SQL Statistics%')
           OR (RTRIM(counter_name) = N'SQL Re-Compilations/sec' AND object_name LIKE N'%SQL Statistics%')
           OR (RTRIM(counter_name) = N'Transactions/sec'        AND object_name LIKE N'%Databases%'      AND RTRIM(instance_name) = N'_Total')
           OR (RTRIM(counter_name) = N'Logins/sec'              AND object_name LIKE N'%General Statistics%')
           OR (RTRIM(counter_name) = N'User Connections'        AND object_name LIKE N'%General Statistics%')
           OR (RTRIM(counter_name) = N'Number of Deadlocks/sec' AND object_name LIKE N'%Locks%'          AND RTRIM(instance_name) = N'_Total')
           OR (RTRIM(counter_name) = N'Page life expectancy'    AND object_name LIKE N'%Buffer Manager%')
           OR (RTRIM(counter_name) LIKE N'Buffer cache hit ratio%' AND object_name LIKE N'%Buffer Manager%')
           OR (RTRIM(counter_name) = N'Memory Grants Pending'   AND object_name LIKE N'%Memory Manager%')
           OR (RTRIM(counter_name) = N'Target Server Memory (KB)' AND object_name LIKE N'%Memory Manager%')
           OR (RTRIM(counter_name) = N'Total Server Memory (KB)'  AND object_name LIKE N'%Memory Manager%')
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// CPU - SQL DIŞI süreçler dahil TOPLAM makine görüntüsü. SQL Server
    /// kendi CPU yüzdesini bu ring buffer'a yazar; her dakika BİR kayıt
    /// atar (canlı sunucuda ölçüldü: art arda kayıtların zaman damgaları
    /// 60,08-60,13 sn arayla). En yeni kaydı alıyoruz.
    ///
    /// SystemIdle boşta geçen yüzde; SQL + diğer + idle = 100. "Diğer"
    /// yüksekse sorun SQL'de değil, aynı makinedeki başka bir süreçte
    /// demektir - bu ayrım bir DBA için çok kıymetli.
    ///
    /// NEDEN DAKİKADA BİR VE NEDEN DÜZELTİLEMEZ: bu, ne kadar sık
    /// sorgularsak sorgulayalım değişmeyen bir SQL Server motor sınırı -
    /// SQLOS bu XML kaydını dakikada bir üretiyor, DAHA SIK sorgulamak
    /// aynı değeri tekrar tekrar döndürür. "SQL dışı süreçler" (OtherCpuPercent)
    /// ve "toplam sistem" (SystemCpuPercent, LiveMonitorService'te
    /// hesaplanıyor) için SQL Server'ın T-SQL'den erişilebilir BAŞKA hiçbir
    /// kaynağı yok - o veri yalnızca burada var, OS'a ayrı bir ajanla
    /// erişmeden daha sık ölçülemez. SQL Server'ın KENDİ CPU'su için ise
    /// aşağıdaki ResourceGovernorCpu çok daha taze bir kaynak - bkz. orası.
    /// </summary>
    public const string CpuUtilization = """
        SELECT TOP (1)
            SqlCpuPercent    = SQLProcessUtilization,
            SystemIdle       = SystemIdle,
            OtherCpuPercent  = 100 - SystemIdle - SQLProcessUtilization
        FROM
        (
            SELECT
                record_id             = CONVERT(xml, record).value('(./Record/@id)[1]', 'int'),
                SystemIdle            = CONVERT(xml, record).value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int'),
                SQLProcessUtilization = CONVERT(xml, record).value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int')
            FROM sys.dm_os_ring_buffers WITH (NOLOCK)
            WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
              AND record LIKE N'%<SystemHealth>%'
        ) AS x
        ORDER BY record_id DESC
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Scheduler doygunluğu. runnable_tasks, CPU için sıraya girmiş ama
    /// henüz çalışamayan görev sayısıdır. CPU yüzdesi düşük görünüp
    /// runnable_tasks yüksekse gerçek bir CPU baskısı var demektir -
    /// yalnız yüzdeye bakmak yanıltır.
    /// </summary>
    public const string SchedulerPressure = """
        SELECT
            SchedulerCount = COUNT(*),
            RunnableTasks  = SUM(runnable_tasks_count),
            PendingDiskIo  = SUM(pending_disk_io_count)
        FROM sys.dm_os_schedulers WITH (NOLOCK)
        WHERE status = N'VISIBLE ONLINE'
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// SQL Server'ın KENDİ CPU kullanımı, KÜMÜLATİF milisaniye (sunucu
    /// başlangıcından beri) - CpuUtilization'daki ring buffer'ın (dakikada
    /// bir güncellenen 60 sn ortalaması) AKSİNE bu, Resource Governor'ın
    /// kendi muhasebesi ve her an güncel. İki ardışık okuma arasındaki
    /// farkı CounterDeltaTracker ile alıp gerçek, O PENCEREYE ait bir CPU%
    /// hesaplıyoruz (bkz. LiveMonitorService.ReadCpuAsync) - "app 1 sn'de
    /// bir mi soruyor" sorusunun cevabı artık gerçekten "evet, ve SQL
    /// Server da o kadar sık yeni veri veriyor".
    ///
    /// Canlıda doğrulandı: art arda iki okuma arasında (birkaç saniye)
    /// hem total_cpu_usage_ms hem statistics_start_time'a göre delta
    /// gerçekten değişiyor - ring buffer'ın aksine 60 sn'de bir donmuyor.
    ///
    /// TÜM resource pool'lar toplanıyor (varsayılan kurulumda yalnızca
    /// "internal" + "default" var, ama Resource Governor'da özel pool
    /// tanımlanmışsa onlar da SQL'in kendi CPU'sudur, dahil edilmeli).
    ///
    /// SINIR: bu yalnızca SQL Server'ın KENDİ CPU'sunu verir. "CPU (sistem)"
    /// (SQL dışı süreçler dahil toplam) için hâlâ CpuUtilization'daki ring
    /// buffer'a muhtacız - Resource Governor'ın SQL dışı süreçlere dair
    /// HİÇBİR görünürlüğü yok, o OS-seviyesi bir bilgi.
    /// </summary>
    public const string ResourceGovernorCpu = """
        SELECT TotalCpuUsageMs = SUM(total_cpu_usage_ms)
        FROM sys.dm_resource_governor_resource_pools WITH (NOLOCK)
        OPTION (RECOMPILE);
        """;

    public const string MemoryUsage = """
        SELECT
            CommittedGb = CONVERT(decimal(19,2), physical_memory_in_use_kb / 1048576.0),
            PhysicalGb  = CONVERT(decimal(19,2),
                            (SELECT physical_memory_kb / 1048576.0 FROM sys.dm_os_sys_info WITH (NOLOCK)))
        FROM sys.dm_os_process_memory WITH (NOLOCK)
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// tempdb doluluk ve içerik dağılımı.
    ///
    /// Dağılım kritik: doluluk kullanıcı nesnelerinden geliyorsa geçici
    /// tablo kullanan bir sorgu; version store'dan geliyorsa açık kalmış
    /// uzun bir transaction (snapshot izolasyonu); iç nesnelerden geliyorsa
    /// spill yapan bir sıralama/hash. Üçü üç ayrı problem.
    /// </summary>
    public const string TempDbUsage = """
        /*  tempdb doluluk ve icerik dagilimi.

            DIKKAT - burada bir tuzak var:
            sys.dm_db_file_space_usage VERITABANI KAPSAMLI bir DMV'dir ve
            uc parcali adlandirma (tempdb.sys.dm_db_file_space_usage) ile
            CAGRILAMAZ - "Invalid object name" hatasi verir. Katalog
            gorunumlerinde (sys.database_files) uc parcali ad calisir,
            veritabani kapsamli DMV'lerde calismaz. Ikisi farkli seylerdir.

            Cozum: sp_executesql'i hedef veritabaninin adiyla nitelemek.
            EXEC tempdb.sys.sp_executesql, gonderilen batch'i tempdb
            baglaminda calistirir ve isi bitince baglantinin kendi
            veritabani baglamini DEGISTIRMEDEN birakir. "USE tempdb;"
            yazsaydik ayni baglantiyi paylasan diger paneller yanlis
            veritabaninda calisirdi.

            ISNULL sart: bos sonuc kumesinde SUM() NULL doner; NULL degeri
            decimal alana yazmaya calisan istemci hata firlatir.  */

        DECLARE @usage TABLE
        (
            UsedMb            decimal(19,2),
            VersionStoreMb    decimal(19,2),
            UserObjectsMb     decimal(19,2),
            InternalObjectsMb decimal(19,2)
        );

        INSERT INTO @usage
        EXEC tempdb.sys.sp_executesql N'
            SELECT
                UsedMb            = CONVERT(decimal(19,2), ISNULL(SUM(allocated_extent_page_count),        0) * 8.0 / 1024),
                VersionStoreMb    = CONVERT(decimal(19,2), ISNULL(SUM(version_store_reserved_page_count),  0) * 8.0 / 1024),
                UserObjectsMb     = CONVERT(decimal(19,2), ISNULL(SUM(user_object_reserved_page_count),    0) * 8.0 / 1024),
                InternalObjectsMb = CONVERT(decimal(19,2), ISNULL(SUM(internal_object_reserved_page_count), 0) * 8.0 / 1024)
            FROM sys.dm_db_file_space_usage WITH (NOLOCK);';

        SELECT
            TotalMb           = CONVERT(decimal(19,2),
                                    ISNULL((SELECT SUM(CONVERT(bigint, size)) * 8.0 / 1024
                                            FROM tempdb.sys.database_files WITH (NOLOCK)
                                            WHERE type = 0), 0)),
            UsedMb            = ISNULL(MAX(UsedMb), 0),
            VersionStoreMb    = ISNULL(MAX(VersionStoreMb), 0),
            UserObjectsMb     = ISNULL(MAX(UserObjectsMb), 0),
            InternalObjectsMb = ISNULL(MAX(InternalObjectsMb), 0)
        FROM @usage;
        """;

    /// <summary>
    /// tempdb ayırma sayfası çekişmesi (GAM/SGAM/PFS latch bekleyenler).
    /// Sıfırdan büyükse tempdb dosya sayısını artırma zamanı gelmiştir.
    /// </summary>
    public const string TempDbContention = """
        SELECT
            Contention    = ISNULL(COUNT(*), 0),
            DataFileCount = ISNULL((SELECT COUNT(*)
                                    FROM tempdb.sys.database_files WITH (NOLOCK)
                                    WHERE type = 0), 0)
        FROM sys.dm_os_waiting_tasks AS wt WITH (NOLOCK)
        WHERE wt.wait_type LIKE N'PAGELATCH%'
          AND wt.resource_description LIKE N'2:%'
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Volume doluluğu. dm_os_volume_stats yalnızca SQL Server'ın dosyası
    /// olan volume'leri görür - E:\ üzerinde hiç veri dosyası yoksa
    /// listede çıkmaz. Bu bir eksiklik değil, kapsam sınırıdır.
    /// </summary>
    public const string VolumeSpace = """
        SELECT DISTINCT
            Mount   = vs.volume_mount_point,
            TotalGb = CONVERT(decimal(19,2), vs.total_bytes     / 1073741824.0),
            FreeGb  = CONVERT(decimal(19,2), vs.available_bytes / 1073741824.0)
        FROM sys.master_files AS mf WITH (NOLOCK)
        CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) AS vs
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Dosya bazlı I/O gecikmesi.
    ///
    /// DİKKAT: ReadLatencyMs/WriteLatencyMs KÜMÜLATİFTİR, SQL Server
    /// açıldığından beri. 45 gün uptime'ı olan bir sunucuda "446 ms yazma
    /// gecikmesi" görürsen bu ŞU AN diskin yavaş olduğu anlamına GELMEZ;
    /// 45 günün ortalamasıdır ve büyük ihtimalle geçmişte yaşanmış bir
    /// olayın kalıntısıdır. Bu iki alan artık YALNIZCA bilgi amaçlı -
    /// LiveMonitorService.ReadIoAsync bunlardan alarm ÜRETMİYOR.
    ///
    /// IoStallReadMs/NumOfReads/IoStallWriteMs/NumOfWrites HAM kümülatif
    /// sayaçlardır - bölünmemiş hâlde döndürülüyorlar ki ReadIoAsync iki
    /// örnekleme arasındaki FARKI alıp gerçek, o pencereye ait bir
    /// ortalama hesaplayabilsin (CounterDeltaTracker'ın /sn sayaçlar için
    /// yaptığının aynısı - bkz. oradaki yorum).
    ///
    /// TOP (12) DEĞİL TOP (50): kümülatif yazma gecikmesine göre sıralayıp
    /// 12'yle kesersek, ömür boyu ortalaması düşük ama SON birkaç dakikada
    /// yeni bir sorun yaşamaya başlamış bir dosya listeye hiç girmeyebilir -
    /// tam da bu sorguyu var eden problemin ta kendisi. 50, pratikte
    /// hiçbir sunucunun aşmayacağı cömert bir üst sınır, maliyeti önemsiz
    /// (bu DMV zaten hafif, CROSS APPLY yok).
    /// </summary>
    public const string FileIoStats = """
        SELECT TOP (50)
            DatabaseName   = DB_NAME(vfs.database_id),
            PhysicalName   = mf.physical_name,
            FileType       = mf.type_desc,
            ReadLatencyMs  = CONVERT(decimal(19,1),
                               CASE WHEN vfs.num_of_reads  = 0 THEN 0
                                    ELSE vfs.io_stall_read_ms  * 1.0 / vfs.num_of_reads  END),
            WriteLatencyMs = CONVERT(decimal(19,1),
                               CASE WHEN vfs.num_of_writes = 0 THEN 0
                                    ELSE vfs.io_stall_write_ms * 1.0 / vfs.num_of_writes END),
            SizeMb         = CONVERT(decimal(19,1), vfs.size_on_disk_bytes / 1048576.0),
            IoStallReadMs  = vfs.io_stall_read_ms,
            NumOfReads     = vfs.num_of_reads,
            IoStallWriteMs = vfs.io_stall_write_ms,
            NumOfWrites    = vfs.num_of_writes
        FROM sys.dm_io_virtual_file_stats(NULL, NULL) AS vfs
        JOIN sys.master_files AS mf WITH (NOLOCK)
          ON mf.database_id = vfs.database_id
         AND mf.file_id     = vfs.file_id
        ORDER BY
            CASE WHEN vfs.num_of_writes = 0 THEN 0
                 ELSE vfs.io_stall_write_ms * 1.0 / vfs.num_of_writes END DESC
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Anlık aktivite. Ekranın en çok bakılan paneli.
    ///
    /// sql_text için OUTER APPLY kullanıyoruz, CROSS APPLY değil: plan
    /// cache'ten düşmüş bir sorgunun metni gelmez ve CROSS APPLY o satırı
    /// tamamen yutar. Sorgu metnini kaybetmek, isteği kaybetmekten iyidir.
    ///
    /// SUBSTRING hesabı, çok ifadeli bir batch içinde ŞU AN çalışan tek
    /// ifadeyi kesip alır. Aksi hâlde 400 satırlık stored procedure'ün
    /// tamamını görürsün ve hangi satırda takıldığını anlamazsın.
    /// </summary>
    public const string ActiveRequests = """
        SELECT
            SessionId            = s.session_id,
            LoginName            = ISNULL(s.login_name, N''),
            HostName             = ISNULL(s.host_name, N''),
            ProgramName          = ISNULL(s.program_name, N''),
            DatabaseName         = ISNULL(DB_NAME(r.database_id), N''),
            Status               = ISNULL(r.status, s.status),
            Command              = ISNULL(r.command, N''),
            WaitType             = ISNULL(r.wait_type, N''),
            WaitTimeMs           = CONVERT(bigint, ISNULL(r.wait_time, 0)),
            ElapsedMs            = CONVERT(bigint, ISNULL(r.total_elapsed_time, 0)),
            CpuMs                = CONVERT(bigint, ISNULL(r.cpu_time, 0)),
            LogicalReads         = CONVERT(bigint, ISNULL(r.logical_reads, 0)),
            Writes               = CONVERT(bigint, ISNULL(r.writes, 0)),
            BlockedBy            = CONVERT(int,    ISNULL(r.blocking_session_id, 0)),
            OpenTransactionCount = CONVERT(int,    ISNULL(r.open_transaction_count, 0)),
            SqlText              = CONVERT(nvarchar(4000), ISNULL(
                                     SUBSTRING(t.text,
                                       (r.statement_start_offset / 2) + 1,
                                       ((CASE r.statement_end_offset
                                             WHEN -1 THEN DATALENGTH(t.text)
                                             ELSE r.statement_end_offset
                                         END - r.statement_start_offset) / 2) + 1),
                                     N'')),

            /*  Çalışan ifade bir prosedür/fonksiyon içindeyse ADI.
                SqlText yukarıda BİLEREK tek ifadeye kırpıldığı için
                "hangi prosedür" bilgisi oradan okunamaz - ayrıca lazım.

                Ad-hoc bir batch'te objectid NULL gelir, o zaman '' döner
                ve ekran yalnızca sorgu metnini gösterir. OBJECT_NAME'in
                db bağlamı dışından çalışabilmesi için dbid'yi de
                veriyoruz; yetki yoksa yine NULL, yine '' - bu yüzden
                sorgu hata vermez, sadece adı boş kalır. */
            ObjectName           = ISNULL(
                                     OBJECT_SCHEMA_NAME(t.objectid, t.dbid) + N'.' +
                                     OBJECT_NAME(t.objectid, t.dbid), N'')
        FROM sys.dm_exec_sessions AS s WITH (NOLOCK)
        JOIN sys.dm_exec_requests AS r WITH (NOLOCK)
          ON r.session_id = s.session_id
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) AS t
        WHERE s.is_user_process = 1
          AND s.session_id <> @@SPID

          /*  is_user_process = 1 yeterli bir filtre DEGILDIR.

              Sunucunun kendi ic isleri icin actigi bazi oturumlar
              NT AUTHORITY\SYSTEM altinda "kullanici oturumu" gibi
              gorunur. En bilineni sp_server_diagnostics: Always On /
              FCI saglik izleme oturumu, instance acildigi anda baslar
              ve kapanana kadar durur. Onu "uzun suren sorgu" saymak,
              her ekran yenilemesinde sahte bir kritik alarm demektir.

              Ayirt edici isaret komut adi degil, BEKLEME TIPIDIR:
              bu oturumlar is beklerken parked bir wait type'ta durur.
              Asagidakiler "bos bekliyorum" anlamina gelir, "calisiyorum"
              degil - hicbiri bir kullanici sorgusunun yavasligini
              gostermez.  */
          AND ISNULL(r.wait_type, N'') NOT IN (
                N'SP_SERVER_DIAGNOSTICS_SLEEP',        -- AG/FCI saglik oturumu
                N'BROKER_RECEIVE_WAITFOR',             -- Service Broker kuyruk okuyucusu
                N'BROKER_TRANSMITTER',
                N'BROKER_TO_FLUSH',
                N'FT_IFTS_SCHEDULER_IDLE_WAIT',        -- full-text zamanlayici
                N'HADR_FILESTREAM_IOMGR_IOCOMPLETION',
                N'DIRTY_PAGE_POLL',
                N'XE_LIVE_TARGET_TVF'
              )

          /*  total_elapsed_time int'tir ve 2147483647 ms'de (yaklasik
              24.8 gun) DOYAR, artmaz. Bu degeri gorduysek elimizdeki
              sey bir sure degil, tasmis bir sayactir - "2147483 sn"
              diye ekrana yazmak kullaniciyi yaniltir. Boyle bir satiri
              olcmek yerine disarida birakiyoruz.  */
          AND r.total_elapsed_time < 2147483647

          /*  Izleme aracinin kendisi de olcume dahil olmamali. @@SPID
              yalnizca su anki baglantiyi eler; toplayici ile arayuz ayni
              anda calisirken birbirlerini "calisan istek" olarak
              gorurlerdi.  */
          AND ISNULL(s.program_name, N'') NOT LIKE N'SqlMonitor.%'
        ORDER BY r.total_elapsed_time DESC
        OPTION (RECOMPILE);
        """;

    /// <summary>Oturum sayıları. Ayrı sorgu, çünkü çalışmayan oturumlar da sayılmalı.</summary>
    public const string SessionCounts = """
        SELECT
            TotalConnections = COUNT(*),
            SleepingSessions = SUM(CASE WHEN s.status = N'sleeping' THEN 1 ELSE 0 END),
            OpenTransactions = (SELECT COUNT(DISTINCT session_id)
                                FROM sys.dm_tran_session_transactions WITH (NOLOCK))
        FROM sys.dm_exec_sessions AS s WITH (NOLOCK)
        WHERE s.is_user_process = 1
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Bloklama zinciri.
    ///
    /// Baş engelleyiciyi (head blocker) ayırt etmek önemlidir: A, B'yi
    /// bloklar, B de C'yi bloklarsa üçünü öldürmenin anlamı yok, A'yı
    /// bulman gerekir. Burada zinciri düz getiriyoruz, baş engelleyiciyi
    /// C# tarafında hesaplıyoruz.
    /// </summary>
    public const string BlockingChains = """
        SELECT
            BlockedSessionId  = r.session_id,
            BlockingSessionId = r.blocking_session_id,
            WaitTimeMs        = CONVERT(bigint, ISNULL(r.wait_time, 0)),
            WaitType          = ISNULL(r.wait_type, N''),
            WaitResource      = ISNULL(r.wait_resource, N''),
            DatabaseName      = ISNULL(DB_NAME(r.database_id), N''),
            /*  Bekleyen taraf için ŞU AN takılı olan tek ifadeyi kesiyoruz
                (ActiveRequests ile aynı hesap): kilide hangi satırın
                takıldığını görmek, 400 satırlık prosedür gövdesini
                okumaktan çok daha işe yarar.

                Bekleten tarafta bu mümkün DEĞİL: onun metni
                most_recent_sql_handle'dan geliyor ve o handle'ın ifade
                offset'i yok - bekleten çoğu zaman hiç sorgu çalıştırmıyor,
                açık bir transaction'la öylece oturuyor. Orada tam metin
                kalıyor; hangi prosedür olduğunu BlockerObjectName söylüyor. */
            BlockedSql        = CONVERT(nvarchar(2000), ISNULL(
                                  SUBSTRING(bt.text,
                                    (r.statement_start_offset / 2) + 1,
                                    ((CASE r.statement_end_offset
                                          WHEN -1 THEN DATALENGTH(bt.text)
                                          ELSE r.statement_end_offset
                                      END - r.statement_start_offset) / 2) + 1),
                                  N'')),
            BlockerSql        = CONVERT(nvarchar(2000), ISNULL(kt.text, N'')),

            /*  "Hangi prosedür blokluyor" sorusunun cevabı. Metinden
                okunamaz: bir prosedür çağrısında sql_text prosedürün
                GÖVDESİNİ döndürür, adını değil. Ad-hoc bir batch'te
                objectid NULL gelir ve '' döneriz.  */
            BlockedObjectName = ISNULL(
                                  OBJECT_SCHEMA_NAME(bt.objectid, bt.dbid) + N'.' +
                                  OBJECT_NAME(bt.objectid, bt.dbid), N''),
            BlockerObjectName = ISNULL(
                                  OBJECT_SCHEMA_NAME(kt.objectid, kt.dbid) + N'.' +
                                  OBJECT_NAME(kt.objectid, kt.dbid), N'')
        FROM sys.dm_exec_requests AS r WITH (NOLOCK)
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) AS bt
        LEFT JOIN sys.dm_exec_connections AS c WITH (NOLOCK)
          ON c.session_id = r.blocking_session_id
        OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) AS kt
        WHERE r.blocking_session_id <> 0
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Bekleme istatistikleri.
    ///
    /// WHERE listesi Paul Randal'ın "görmezden gelinecek beklemeler"
    /// listesinin kısaltılmış hâli. Bunlar sunucu boştayken de sürekli
    /// birikir (arka plan görevlerinin uykusu). Filtrelemezsen listenin
    /// tepesini hep onlar kapar ve gerçek darboğazı hiç göremezsin.
    ///
    /// Canlı sunucuda doğrulandı (2026-09-14): filtresiz listenin tepesi
    /// SOS_WORK_DISPATCHER'dı (150+ milyar ms kümülatif) - orijinal
    /// listede bu EKSİKTİ, buraya eklendi. Bu tek satır olmadan Genel
    /// Bakış'taki "Bekleme" kartı hep aynı anlamsız satırı gösterirdi.
    ///
    /// Değerler kümülatiftir; anlamlı hâle gelmesi için İKİ tüketici de
    /// iki örneğin farkını alıyor: collector (mon.WaitSample'a kümülatif
    /// yazıp geçmişte fark hesaplanabilsin diye) VE LiveMonitorService.
    /// ReadWaitsAsync (canlı "Bekleme" kartı için, CounterDeltaTracker
    /// üzerinden - CPU/I-O düzeltmelerindeki AYNI desen).
    /// </summary>
    public const string WaitStats = """
        SELECT
            WaitType     = wait_type,
            WaitTimeMs   = CONVERT(bigint, wait_time_ms),
            SignalTimeMs = CONVERT(bigint, signal_wait_time_ms),
            WaitingTasks = CONVERT(bigint, waiting_tasks_count)
        FROM sys.dm_os_wait_stats WITH (NOLOCK)
        WHERE waiting_tasks_count > 0
          AND wait_type NOT IN (
                N'BROKER_EVENTHANDLER', N'BROKER_RECEIVE_WAITFOR', N'BROKER_TASK_STOP',
                N'BROKER_TO_FLUSH', N'BROKER_TRANSMITTER', N'CHECKPOINT_QUEUE',
                N'CHKPT', N'CLR_AUTO_EVENT', N'CLR_MANUAL_EVENT', N'CLR_SEMAPHORE',
                N'DBMIRROR_DBM_EVENT', N'DBMIRROR_EVENTS_QUEUE', N'DBMIRROR_WORKER_QUEUE',
                N'DBMIRRORING_CMD', N'DIRTY_PAGE_POLL', N'DISPATCHER_QUEUE_SEMAPHORE',
                N'EXECSYNC', N'FSAGENT', N'FT_IFTS_SCHEDULER_IDLE_WAIT', N'FT_IFTSHC_MUTEX',
                N'HADR_CLUSAPI_CALL', N'HADR_FILESTREAM_IOMGR_IOCOMPLETION',
                N'HADR_LOGCAPTURE_WAIT', N'HADR_NOTIFICATION_DEQUEUE',
                N'HADR_TIMER_TASK', N'HADR_WORK_QUEUE',
                N'KSOURCE_WAKEUP', N'LAZYWRITER_SLEEP', N'LOGMGR_QUEUE',
                N'MEMORY_ALLOCATION_EXT', N'ONDEMAND_TASK_QUEUE',
                N'PARALLEL_REDO_DRAIN_WORKER', N'PARALLEL_REDO_LOG_CACHE',
                N'PARALLEL_REDO_TRAN_LIST', N'PARALLEL_REDO_WORKER_SYNC',
                N'PARALLEL_REDO_WORKER_WAIT_WORK',
                N'PREEMPTIVE_XE_GETTARGETSTATE', N'PWAIT_ALL_COMPONENTS_INITIALIZED',
                N'PWAIT_DIRECTLOGCONSUMER_GETNEXT', N'QDS_PERSIST_TASK_MAIN_LOOP_SLEEP',
                N'QDS_ASYNC_QUEUE', N'QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP',
                N'QDS_SHUTDOWN_QUEUE', N'REDO_THREAD_PENDING_WORK',
                N'REQUEST_FOR_DEADLOCK_SEARCH', N'RESOURCE_QUEUE',
                N'SERVER_IDLE_CHECK', N'SLEEP_BPOOL_FLUSH', N'SLEEP_DBSTARTUP',
                N'SLEEP_DCOMSTARTUP', N'SLEEP_MASTERDBREADY', N'SLEEP_MASTERMDREADY',
                N'SLEEP_MASTERUPGRADED', N'SLEEP_MSDBSTARTUP', N'SLEEP_SYSTEMTASK',
                N'SLEEP_TASK', N'SLEEP_TEMPDBSTARTUP', N'SNI_HTTP_ACCEPT',
                N'SOS_WORK_DISPATCHER', N'SP_SERVER_DIAGNOSTICS_SLEEP', N'SQLTRACE_BUFFER_FLUSH',
                N'SQLTRACE_INCREMENTAL_FLUSH_SLEEP', N'SQLTRACE_WAIT_ENTRIES',
                N'WAIT_FOR_RESULTS', N'WAITFOR', N'WAITFOR_TASKSHUTDOWN',
                N'WAIT_XTP_RECOVERY', N'WAIT_XTP_HOST_WAIT',
                N'WAIT_XTP_OFFLINE_CKPT_NEW_LOG', N'WAIT_XTP_CKPT_CLOSE',
                N'XE_DISPATCHER_JOIN', N'XE_DISPATCHER_WAIT', N'XE_TIMER_EVENT',
                N'XE_LIVE_TARGET_TVF', N'XE_BUFFERMGR_ALLPROCESSED_EVENT'
              )
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Yedek durumu. msdb.dbo.backupset okur; izleme hesabının msdb'de
    /// db_datareader olması gerekir (02_monitoring_login.sql bunu verir).
    ///
    /// Hiç FULL yedeği olmayan bir DB, günlerdir eski yedeği olan bir
    /// DB'den çok daha kötüdür - o yüzden NULL'ı ayrı bir durum olarak
    /// işaretliyoruz, büyük bir sayıya çevirmiyoruz.
    ///
    /// DIKKAT - Always On: backupset HER REPLIKADA YERELDIR, AG bunu
    /// replikalar arasında senkronize ETMEZ. Yedek başka bir makinede
    /// alınıyorsa bu sorgu onu göremez ve "hiç yedeği yok" der - ki bu
    /// yanlış alarmın en kötü türüdür, insanı gereksiz yere korkutur.
    /// Eksik parçayı BackupHistory sorgusu diğer replikalardan getirir,
    /// HealthEvaluator ikisini birleştirir.
    ///
    /// FULL'de is_copy_only filtresi bilerek YOK: secondary replikada
    /// SQL Server yalnızca COPY_ONLY full yedeğe izin verir. Filtreyi
    /// bıraksaydık tam da aradığımız yedeği elemiş olurduk. Kurtarma
    /// açısından copy-only bir full, geçerli bir restore tabanıdır.
    /// LOG'da ise filtre duruyor: copy-only log yedeği zinciri
    /// ilerletmez, onu zincirin parçası saymak yanıltıcı olur.
    ///
    /// Burada bir sure sys.dm_hadr_* join'leri vardi (AG rolunu
    /// gostermek icin). OLCULDU: bu iki join sorguyu ~110 ms'den
    /// ~500 ms'ye cikariyordu ve donen sutun hicbir kontrolde
    /// kullanilmiyordu. Panel her saniye yenilendigine gore bu, hicbir
    /// sey karsiliginda saniyede yarim saniyelik sunucu isi demekti.
    /// Cikarildi. AG rolu bir gun gerekirse AYRI ve seyrek calisan bir
    /// sorgu olmali, her snapshot'a bindirilmemeli.
    /// </summary>
    public const string BackupStatus = """
        SELECT
            DatabaseName    = d.name,
            RecoveryModel   = d.recovery_model_desc,
            LastFullBackup  = MAX(CASE WHEN b.type = 'D' THEN b.backup_finish_date END),
            LastLogBackup   = MAX(CASE WHEN b.type = 'L' AND b.is_copy_only = 0
                                       THEN b.backup_finish_date END),
            LogReuseWait    = d.log_reuse_wait_desc,
            StateDesc       = d.state_desc
        FROM sys.databases AS d WITH (NOLOCK)
        LEFT JOIN msdb.dbo.backupset AS b WITH (NOLOCK)
          ON b.database_name = d.name
        WHERE d.database_id <> 2          -- tempdb yedeklenmez
        GROUP BY d.name, d.recovery_model_desc, d.log_reuse_wait_desc, d.state_desc
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// AG replikalarını keşfeder.
    ///
    /// Amaç, "yedek hangi makinede alınıyor" sorusunu kullanıcıya
    /// sordurmadan cevaplamak: replica_server_name'i alıp izlenen
    /// instance'ın bağlantı dizesindeki Server'ı onunla değiştiriyoruz,
    /// kimlik bilgileri aynı kalıyor.
    ///
    /// AG yoksa veya HADR kapalıysa sorgu hata vermez, boş küme döner -
    /// bu yüzden AG olmayan sunucularda da güvenle çalıştırılabilir.
    /// </summary>
    public const string AgReplicas = """
        SELECT
            ReplicaServerName = ar.replica_server_name,
            RoleDesc          = ISNULL(ars.role_desc, N'UNKNOWN'),
            IsLocal           = CONVERT(bit, ISNULL(ars.is_local, 0)),
            BackupPriority    = ar.backup_priority,
            GroupName         = ag.name
        FROM sys.availability_replicas AS ar WITH (NOLOCK)
        JOIN sys.availability_groups AS ag WITH (NOLOCK)
          ON ag.group_id = ar.group_id
        LEFT JOIN sys.dm_hadr_availability_replica_states AS ars WITH (NOLOCK)
          ON ars.replica_id = ar.replica_id
        OPTION (RECOMPILE);
        """;

    // ------------------------------------------------------------------
    // Always On sekmesi.
    //
    // AgReplicas'tan (yukarısı) FARKLI amaç: o yalnızca yedek kaynağı
    // keşfi için var, saniyelik döngüde koşan BackupHistoryReader'a
    // bağlı - burada ELİMİZE geçen ROL bilgisi bile bir süre KpiSnapshot'a
    // dahildi ve ~500ms'lik maliyeti yüzünden çıkarıldı (bkz. yukarıdaki
    // BackupStatus üstündeki not). Always On sekmesi TAM DA o notun
    // önerdiği şey: AYRI, SEYREK çalışan, kendi ucundan gelen bir sorgu -
    // Index Analizi/Stored Procedure ile aynı disiplin.
    //
    // Canlı sunucuda ölçüldü: 5 sorgu birden ~0.5 sn (3 düğüm, 3 veritabanı).
    //
    // AG yoksa (ya da HADR kapalıysa) sorgular hata vermez, boş küme
    // döner - AgReplicas'taki gibi. Cluster DMV'leri (en alttaki ikisi)
    // AYRICA TRY/CATCH içine alındı: bunlar WSFC cluster'ına, AG'nin
    // kendisinden FARKLI bir yetkiye ihtiyaç duyabilir - başarısız olursa
    // sekmenin geri kalanını çökertmemesi için boş küme dönüyorlar.
    // ------------------------------------------------------------------

    /// <summary>AG'nin rollup sağlığı - "genel olarak durum ne" sorusunun tek satırlık cevabı.</summary>
    public const string AgGroupHealth = """
        SELECT
            GroupName               = ag.name,
            PrimaryReplica          = ags.primary_replica,
            PrimaryRecoveryHealth   = ags.primary_recovery_health_desc,
            SecondaryRecoveryHealth = ags.secondary_recovery_health_desc,
            SynchronizationHealth   = ags.synchronization_health_desc
        FROM sys.availability_groups AS ag WITH (NOLOCK)
        LEFT JOIN sys.dm_hadr_availability_group_states AS ags WITH (NOLOCK)
          ON ags.group_id = ag.group_id
        OPTION (RECOMPILE);
        """;

    /// <summary>Replika bazında rol, bağlantı durumu, senkron sağlığı, commit modu.</summary>
    public const string AgReplicaHealth = """
        SELECT
            GroupName             = ag.name,
            ReplicaServerName     = ar.replica_server_name,
            RoleDesc              = ars.role_desc,
            ConnectedState        = ars.connected_state_desc,
            SynchronizationHealth = ars.synchronization_health_desc,
            OperationalState      = ars.operational_state_desc,
            RecoveryHealth        = ars.recovery_health_desc,
            AvailabilityMode      = ar.availability_mode_desc,
            FailoverMode          = ar.failover_mode_desc,
            BackupPriority        = ar.backup_priority,
            -- Mirroring uç noktası: "TCP://SUNUCU.alanadi.com:5022" gibi.
            -- Replikanın KAYITLI kısa adı (replica_server_name) bazı ağlarda
            -- DNS'ten çözülmüyor ama buradaki tam alan adı (FQDN) çözülebilir -
            -- otomatik keşif için ikinci bir aday adres, bkz. BackupHistoryReader.
            EndpointUrl           = ar.endpoint_url,
            IsLocal               = CONVERT(bit, ISNULL(ars.is_local, 0)),
            LastConnectErrorDescription = ars.last_connect_error_description,
            LastConnectErrorTime  = ars.last_connect_error_timestamp
        FROM sys.availability_replicas AS ar WITH (NOLOCK)
        JOIN sys.availability_groups AS ag WITH (NOLOCK)
          ON ag.group_id = ar.group_id
        LEFT JOIN sys.dm_hadr_availability_replica_states AS ars WITH (NOLOCK)
          ON ars.replica_id = ar.replica_id
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Veritabanı × replika bazında senkron durumu - GERÇEK lag burada
    /// görünür (log_send_queue/redo_queue). sys.databases'e KASITLI
    /// bakmıyoruz (database_id yalnızca YEREL replikada anlamlıdır);
    /// sys.availability_databases_cluster.database_name, group_database_id
    /// üzerinden TÜM replikalarda aynı veritabanını doğru eşler.
    /// </summary>
    public const string AgDatabaseReplicaHealth = """
        SELECT
            GroupName             = ag.name,
            ReplicaServerName     = ar.replica_server_name,
            DatabaseName          = adc.database_name,
            SynchronizationState  = drs.synchronization_state_desc,
            SynchronizationHealth = drs.synchronization_health_desc,
            IsSuspended           = drs.is_suspended,
            SuspendReason         = drs.suspend_reason_desc,
            LogSendQueueKb        = drs.log_send_queue_size,
            LogSendRateKb         = drs.log_send_rate,
            RedoQueueKb           = drs.redo_queue_size,
            RedoRateKb            = drs.redo_rate,
            LastCommitTime        = drs.last_commit_time,
            LastHardenedTime      = drs.last_hardened_time,
            LastRedoneTime        = drs.last_redone_time
        FROM sys.dm_hadr_database_replica_states AS drs WITH (NOLOCK)
        JOIN sys.availability_replicas AS ar WITH (NOLOCK) ON ar.replica_id = drs.replica_id
        JOIN sys.availability_groups AS ag WITH (NOLOCK) ON ag.group_id = ar.group_id
        JOIN sys.availability_databases_cluster AS adc WITH (NOLOCK)
          ON adc.group_id = ar.group_id AND adc.group_database_id = drs.group_database_id
        ORDER BY adc.database_name, ar.replica_server_name
        OPTION (RECOMPILE);
        """;

    /// <summary>WSFC cluster'ı ve quorum - AG sağlıklı görünse bile cluster oy çoğunluğunu kaybetmiş olabilir.</summary>
    public const string AgClusterHealth = """
        BEGIN TRY
            SELECT
                ClusterName = cluster_name,
                QuorumType  = quorum_type_desc,
                QuorumState = quorum_state_desc
            FROM sys.dm_hadr_cluster WITH (NOLOCK);
        END TRY
        BEGIN CATCH
            SELECT ClusterName = CAST(NULL AS nvarchar(128)),
                   QuorumType  = CAST(NULL AS nvarchar(60)),
                   QuorumState = CAST(NULL AS nvarchar(60))
            WHERE 1 = 0;
        END CATCH
        """;

    /// <summary>Cluster üyesi düğümler - hangisi UP/DOWN, oy sayısı.</summary>
    public const string AgClusterMembers = """
        BEGIN TRY
            SELECT
                MemberName           = member_name,
                MemberType           = member_type_desc,
                MemberState          = member_state_desc,
                NumberOfQuorumVotes  = number_of_quorum_votes
            FROM sys.dm_hadr_cluster_members WITH (NOLOCK);
        END TRY
        BEGIN CATCH
            SELECT MemberName = CAST(NULL AS nvarchar(128)),
                   MemberType = CAST(NULL AS nvarchar(60)),
                   MemberState = CAST(NULL AS nvarchar(60)),
                   NumberOfQuorumVotes = CAST(0 AS int)
            WHERE 1 = 0;
        END CATCH
        """;

    /// <summary>
    /// Diğer replikanın SADECE yedek geçmişi.
    ///
    /// sys.databases'e bilerek bakmıyoruz: oradaki recovery model,
    /// log_reuse_wait ve state yerel gerçeklerdir ve primary'nin durumu
    /// hakkında hüküm vermek için kullanılamaz. Secondary'de log_reuse_wait
    /// okumak, primary'nin log'u truncate edip edemediği hakkında hiçbir
    /// şey söylemez. Bu sorgudan tek istediğimiz şu: "bu DB'nin yedeği en
    /// son ne zaman alındı?"
    ///
    /// Tarih filtresi msdb büyüdüğünde taramayı sınırlamak için var;
    /// bir yıldan eski bir yedek zaten her eşiğin ötesinde kalır, sonucu
    /// değiştirmez.
    /// </summary>
    public const string BackupHistory = """
        SELECT
            DatabaseName   = b.database_name,
            LastFullBackup = MAX(CASE WHEN b.type = 'D' THEN b.backup_finish_date END),
            LastLogBackup  = MAX(CASE WHEN b.type = 'L' AND b.is_copy_only = 0
                                      THEN b.backup_finish_date END)
        FROM msdb.dbo.backupset AS b WITH (NOLOCK)
        WHERE b.backup_finish_date IS NOT NULL
          AND b.backup_finish_date > DATEADD(day, -400, GETDATE())
        GROUP BY b.database_name
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Hata günlüğü. sp_readerrorlog securityadmin ister; bu yüzden
    /// varsayılan olarak kapalı (Monitor:EnableErrorLogPanel).
    /// </summary>
    public const string ErrorLog = """
        CREATE TABLE #errlog
        (
            LogDate     datetime,
            ProcessInfo nvarchar(100),
            Text        nvarchar(max)
        );

        INSERT INTO #errlog
        EXEC sys.sp_readerrorlog 0, 1;

        SELECT TOP (40)
            LogDate,
            ProcessInfo = ISNULL(ProcessInfo, N''),
            Text        = CONVERT(nvarchar(1000), ISNULL(Text, N''))
        FROM #errlog
        ORDER BY LogDate DESC;

        DROP TABLE #errlog;
        """;

    /// <summary>
    /// Bozuk (suspect) sayfalar - msdb.dbo.suspect_pages. SQL Server bir
    /// sayfayı okurken 823/824 hatası (checksum uyuşmazlığı, yırtık sayfa,
    /// OS-seviyesi I/O hatası) alırsa sayfayı buraya kaydeder - genelde
    /// disk/donanım sorununun ilk görünür belirtisi.
    ///
    /// event_type 1/2/3 AKTİF/çözülmemiş bir sorunu gösterir; 4 (restore
    /// ile onarıldı), 5 (DBCC ile onarıldı) ve 7 (DBCC tarafından ayrıldı)
    /// zaten ÇÖZÜLMÜŞ demektir - onları saymıyoruz, aksi hâlde geçmişte
    /// bir kere onarılmış bir sayfa sonsuza kadar kritik görünürdü.
    ///
    /// Küçük, indeksli bir sistem tablosu - maliyeti önemsiz, bu yüzden
    /// diğer "sekmeler dışı" taramaların aksine doğrudan HealthEvaluator'ın
    /// her turunda (saniyelik döngüde) çalıştırılabiliyor.
    /// </summary>
    public const string SuspectPages = """
        SELECT
            DatabaseName   = DB_NAME(database_id),
            FileId         = file_id,
            PageId         = page_id,
            EventType      = event_type,
            ErrorCount     = error_count,
            LastUpdateDate = last_update_date
        FROM msdb.dbo.suspect_pages WITH (NOLOCK)
        WHERE event_type IN (1, 2, 3)
        ORDER BY last_update_date DESC
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Sınıra yaklaşan identity kolonları - sunucu genelinde tarama.
    ///
    /// Bir identity kolonu veri tipinin üst sınırına ulaşınca o tabloya
    /// yapılan TÜM INSERT'ler aniden "arithmetic overflow" hatasıyla
    /// durur - genelde hiç uyarı vermeden, üretimde. sys.identity_columns
    /// VERİTABANI BAZLI bir katalog görünümü (dm_exec_procedure_stats gibi
    /// sunucu geneli tek sorguyla gelmiyor), o yüzden UnusedProceduresScan
    /// ile AYNI cursor + dinamik SQL deseni kullanılıyor.
    ///
    /// Yalnızca tinyint/smallint/int/bigint için üst sınır biliniyor -
    /// decimal/numeric tabanlı identity'ler (nadir) kapsam dışı, üst
    /// sınırları scale/precision'a bağlı ve ayrı bir hesap gerektirir.
    /// last_value negatifse (bazı DBA'ler taşmayı geciktirmek için seed'i
    /// bilerek negatife çeker) atlanıyor - o zaten bir geçici tedbirdir,
    /// "taşmaya yaklaşıyor" demek yanlış olur.
    ///
    /// %70 eşiği SQL tarafında filtreleniyor (yalnızca yaklaşanları
    /// döndür) - IdentityColumnScanner sonucu önbelleğe alıyor, bu yüzden
    /// canlı HealthEvaluator döngüsünde her seferinde tekrar taranmıyor.
    /// </summary>
    public const string IdentityColumnUsage = """
        SET NOCOUNT ON;

        IF OBJECT_ID('tempdb..#IdentityUsage') IS NOT NULL DROP TABLE #IdentityUsage;
        CREATE TABLE #IdentityUsage
        (
            DatabaseName sysname, SchemaName sysname, TableName sysname, ColumnName sysname,
            TypeName sysname, LastValue DECIMAL(38,0), MaxValue DECIMAL(38,0)
        );

        DECLARE @DbName sysname, @Sql NVARCHAR(MAX);
        DECLARE dbcur CURSOR LOCAL FAST_FORWARD FOR
            SELECT name FROM sys.databases
            WHERE database_id > 4 AND state = 0 AND is_read_only = 0 AND HAS_DBACCESS(name) = 1;
        OPEN dbcur; FETCH NEXT FROM dbcur INTO @DbName;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @Sql = N'
                INSERT INTO #IdentityUsage
                SELECT
                    ''' + REPLACE(@DbName,'''','''''') + N''', s.name, t.name, c.name, ty.name,
                    CAST(ic.last_value AS DECIMAL(38,0)),
                    CASE ty.name
                        WHEN ''tinyint''  THEN 255
                        WHEN ''smallint'' THEN 32767
                        WHEN ''int''      THEN 2147483647
                        WHEN ''bigint''   THEN 9223372036854775807
                    END
                FROM ' + QUOTENAME(@DbName) + N'.sys.identity_columns ic
                JOIN ' + QUOTENAME(@DbName) + N'.sys.tables t  ON t.object_id = ic.object_id
                JOIN ' + QUOTENAME(@DbName) + N'.sys.schemas s ON s.schema_id = t.schema_id
                JOIN ' + QUOTENAME(@DbName) + N'.sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                JOIN ' + QUOTENAME(@DbName) + N'.sys.types ty  ON ty.user_type_id = ic.system_type_id
                WHERE ic.last_value IS NOT NULL
                  AND ic.last_value >= 0
                  AND ty.name IN (''tinyint'',''smallint'',''int'',''bigint'');';
            EXEC sp_executesql @Sql;

            FETCH NEXT FROM dbcur INTO @DbName;
        END
        CLOSE dbcur; DEALLOCATE dbcur;

        SELECT
            DatabaseName, SchemaName, TableName, ColumnName, TypeName,
            LastValue  = CONVERT(bigint, LastValue),
            MaxValue   = CONVERT(bigint, MaxValue),
            UsedPercent = CONVERT(decimal(5,2), LastValue * 100.0 / MaxValue)
        FROM #IdentityUsage
        WHERE LastValue >= MaxValue * 0.7
        ORDER BY LastValue * 1.0 / MaxValue DESC;

        DROP TABLE #IdentityUsage;
        """;

    /// <summary>
    /// Tek bir plan cache satırının tam metni ve yürütme planı.
    ///
    /// Bilerek TOP listesinden AYRI: query_plan XML'ini CONVERT etmek
    /// pahalıdır (bazı planlarda yüzlerce KB). Listede 50-100 satır için
    /// bunu yapmak sunucuyu gereksiz yorar; yalnızca kullanıcı "İncele"ye
    /// bastığında, TEK satır için çalışır. Aynı disiplin canlı oturum
    /// planında da var (/live/session/{id}/plan) - burası onun plan
    /// cache karşılığı.
    ///
    /// Plan hâlâ cache'te değilse (tahliye olmuş, recompile geçirmiş) NULL
    /// döner - hata değil, sadece "artık orada değil" demektir.
    /// </summary>
    public const string QueryPlanByHandle = """
        DECLARE @sh varbinary(64) = CONVERT(varbinary(64), @SqlHandle, 1);
        DECLARE @ph varbinary(64) = CONVERT(varbinary(64), @PlanHandle, 1);

        SELECT
            SqlText = CONVERT(nvarchar(max), ISNULL(t.text, N'')),
            PlanXml = CONVERT(nvarchar(max), ISNULL(CONVERT(nvarchar(max), p.query_plan), N''))
        FROM sys.dm_exec_sql_text(@sh) AS t
        OUTER APPLY sys.dm_exec_query_plan(@ph) AS p
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Eksik index önerileri - sunucu genelinde.
    ///
    /// Buradaki üç DMV, tek bir sorgunun plan XML'inden çıkarılan bir
    /// öneriden TAMAMEN FARKLI bir kaynak: bu üçü optimizer'ın derleme
    /// sırasında biriktirdiği, sunucudaki HER veritabanına ait öneri
    /// havuzudur - tek bir plana bakmaz. Canlı sunucuda doğrulandı: master
    /// bağlamından bağlanıp USE yapmadan bir kullanıcı veritabanına VE msdb'ye ait öneriler
    /// birlikte döndü - yani "dm_db_" öneki yanıltıcı, bu görünüm gerçekte
    /// sunucu genelidir.
    ///
    /// ETKİ FORMÜLÜ Microsoft'un kendi belgelediği formül -
    /// avg_total_user_cost * avg_user_impact * (user_seeks + user_scans) -
    /// uydurma değil, resmi. Canlı veriyle doğrulandı: sıralama gerçek
    /// dünyadaki ağırlıkla eşleşiyor (en çok seek alan VE en pahalı
    /// sorguları etkileyen öneriler tepede çıkıyor).
    ///
    /// equality_columns/inequality_columns/included_columns SQL Server'ın
    /// KENDİSİ tarafından "[Kolon1], [Kolon2]" biçiminde, CREATE INDEX'e
    /// doğrudan yapıştırılabilir şekilde döndürülüyor - ayrıca parantez
    /// temizlemeye gerek yok, C# tarafında yalnızca isim üretimi için
    /// ayrıştırılıyor.
    ///
    /// PARSENAME kullanıldı çünkü [statement] üç parçalı köşeli parantezli
    /// bir ad ("[db].[schema].[tablo]") - dize kesmek yerine SQL Server'ın
    /// kendi ayrıştırıcısını kullanmak, şema/tablo adında nokta geçen nadir
    /// bir durumda bile doğru sonuç verir.
    ///
    /// Maliyet: canlı sunucuda ölçüldü, ~0.3 sn - CROSS APPLY yok, üç
    /// küçük DMV'nin basit bir JOIN'i. Yine de saniyelik döngüye SOKULMADI;
    /// bu öneriler dakikalar içinde önemli ölçüde değişmez.
    /// </summary>
    public const string MissingIndexSuggestions = """
        SELECT TOP (@Top)
            DatabaseName      = DB_NAME(mid.database_id),
            SchemaName        = PARSENAME(mid.[statement], 2),
            TableName         = PARSENAME(mid.[statement], 1),
            EqualityColumns   = mid.equality_columns,
            InequalityColumns = mid.inequality_columns,
            IncludeColumns    = mid.included_columns,
            Seeks             = migs.user_seeks,
            Scans             = migs.user_scans,
            AvgTotalUserCost  = migs.avg_total_user_cost,
            AvgUserImpact     = migs.avg_user_impact,
            Impact            = migs.avg_total_user_cost * migs.avg_user_impact
                                 * (migs.user_seeks + migs.user_scans),
            LastUserSeek      = migs.last_user_seek,
            LastUserScan      = migs.last_user_scan,
            UniqueCompiles    = migs.unique_compiles
        FROM sys.dm_db_missing_index_groups AS mig WITH (NOLOCK)
        JOIN sys.dm_db_missing_index_group_stats AS migs WITH (NOLOCK)
          ON migs.group_handle = mig.index_group_handle
        JOIN sys.dm_db_missing_index_details AS mid WITH (NOLOCK)
          ON mig.index_handle = mid.index_handle
        WHERE mid.database_id <> 32767          -- resource DB, gösterilecek bir şey değil
        ORDER BY Impact DESC
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// En ağır stored procedure'lar - sys.dm_exec_procedure_stats.
    ///
    /// sys.dm_exec_query_stats'ten (ifade bazında ölçer - bir prosedürün
    /// içindeki 12 ayrı SELECT ayrı ayrı satır olarak görünür) FARKI: bu
    /// DMV PROSEDÜR bazında toplar - "bu prosedür bir bütün olarak ne
    /// kadar CPU yedi" sorusuna cevap verir. İkisi birbirinin yerine
    /// geçmez, tamamlar.
    ///
    /// sql_handle/plan_handle SUNUCU-ÇAPINDA (server-scoped) bir mekanizma -
    /// DmvSql.QueryPlanByHandle burada da çalışır, prosedürün hangi
    /// veritabanında olduğu önemli değil, ekstra cross-database dinamik
    /// SQL'e gerek yok.
    ///
    /// GÜRÜLTÜ FİLTRESİ: tempdb'de yaşayan prosedürler gerçek iş mantığı
    /// değildir - kullanıcı uygulamaları neredeyse hiçbir zaman tempdb'ye
    /// stored procedure yazmaz, orası izleme araçlarının (kendimiz dahil,
    /// ama biz zaten ad-hoc SQL kullanıyoruz, bu DMV'ye hiç girmiyoruz)
    /// geçici toplama prosedürlerinin yaşadığı yerdir. Kural EN YOĞUN
    /// SORGULAR'daki "sys.dm_* okuyan her şey elenir" kuralıyla aynı ruhta:
    /// belirli bir aracın adına değil, YAŞADIĞI YERE bakıyoruz - hangi
    /// izleme aracı olursa olsun aynı şekilde elenir.
    ///
    /// Maliyet: canlı sunucuda ölçüldü, ~0.5 sn (336 prosedür üzerinde).
    /// Index Analizi'yle aynı disiplin: saniyelik döngüye SOKULMADI.
    ///
    /// {DIRECTION} - ASC/DESC - tablo başlıklarına tıklayarak büyükten
    /// küçüğe/küçükten büyüğe geçiş yapılabilsin diye eklendi. {ORDER_BY}
    /// gibi bu da C# tarafında SABİT bir whitelist'ten geliyor (yalnızca
    /// "ASC"/"DESC" string'i), istekten doğrudan gelmiyor.
    /// </summary>
    public const string TopProceduresTemplate = """
        SELECT TOP (@Top)
            SqlHandle        = CONVERT(varchar(300), ps.sql_handle, 1),
            PlanHandle       = CONVERT(varchar(300), ps.plan_handle, 1),
            DatabaseName     = DB_NAME(ps.database_id),
            SchemaName       = OBJECT_SCHEMA_NAME(ps.object_id, ps.database_id),
            ProcName         = OBJECT_NAME(ps.object_id, ps.database_id),
            Calls            = ps.execution_count,
            TotalCpuMs       = ps.total_worker_time / 1000.0,
            TotalDurationMs  = ps.total_elapsed_time / 1000.0,
            LogicalReads     = ps.total_logical_reads,
            LogicalWrites    = ps.total_logical_writes,
            CachedTime       = ps.cached_time,
            LastExecutionTime = ps.last_execution_time
        FROM sys.dm_exec_procedure_stats AS ps WITH (NOLOCK)
        WHERE ps.database_id IS NOT NULL
          AND DB_NAME(ps.database_id) IS NOT NULL
          AND DB_NAME(ps.database_id) <> N'tempdb'
          AND OBJECT_NAME(ps.object_id, ps.database_id) IS NOT NULL
        ORDER BY {ORDER_BY} {DIRECTION}
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Bütün prosedür cache'inin özeti - TOP listesiyle KARIŞTIRILMASIN.
    /// Ayrı bir sorgu, çünkü listelenen N prosedürün toplamı ile
    /// sunucudaki TÜM prosedürlerin toplamı genelde çok farklıdır.
    /// </summary>
    public const string ProcedureCacheTotals = """
        SELECT
            ProcCount    = COUNT(*),
            TotalCalls   = ISNULL(SUM(execution_count), 0),
            TotalCpuMs   = ISNULL(SUM(total_worker_time), 0) / 1000.0,
            TotalDurationMs = ISNULL(SUM(total_elapsed_time), 0) / 1000.0
        FROM sys.dm_exec_procedure_stats WITH (NOLOCK)
        WHERE database_id IS NOT NULL AND DB_NAME(database_id) <> N'tempdb'
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// Kullanılmayan prosedür taraması - TopProceduresTemplate'in tam tersi
    /// sorusu: plan cache'te HİÇ görünmeyen prosedürler hangileri?
    ///
    /// BU SORU SANILDIĞINDAN ÇOK DAHA TEHLİKELİ. sys.dm_exec_procedure_stats
    /// yalnızca "şu an cache'te ne var"ı gösterir - orada olmayan bir
    /// prosedür iki şeyden biri demektir: (a) gerçekten hiç çağrılmadı,
    /// ya da (b) çağrıldı ama planı tahliye oldu (recompile, bellek
    /// baskısı, sunucu yeniden başlatıldı). Yalnızca plan cache'e bakıp
    /// "kullanılmıyor" damgası vurmak YANLIŞ POZİTİF üretir.
    ///
    /// İKİNCİ, DAHA GÜVENİLİR bir tanık var: Query Store (açıksa).
    /// Plan cache'in aksine Query Store diske yazar ve
    /// stale_query_threshold_days (burada 30) gün boyunca sorgu geçmişini
    /// SAKLAR - bir yeniden başlatmadan etkilenmez. Bu yüzden her
    /// veritabanı için İKİ tanığa da bakıyoruz: sys.dm_exec_procedure_stats
    /// (sunucu geneli, tek DMV) VE o veritabanının sys.query_store_query'si
    /// (açıksa). Bir prosedür ikisinde de hiç görünmüyorsa öneri güçlenir;
    /// yalnızca plan cache'e bakabiliyorsak (Query Store kapalıysa) bunu
    /// SONUCA damgalıyoruz (QueryStoreEnabled=0) ki arayüz "zayıf kanıt"
    /// uyarısı gösterebilsin - sessizce aynı kesinlikte sunmuyoruz.
    ///
    /// sys.procedures VERİTABANI BAZLI bir katalog görünümü - dm_exec_
    /// procedure_stats gibi sunucu geneli tek sorguyla gelmiyor. Bu yüzden
    /// sys.databases üzerinde gezip her veritabanı için üç parçalı isimle
    /// (DB.sys.procedures, DB.sys.query_store_query) dinamik SQL çalıştırıp
    /// sonuçları geçici tablolarda birleştiriyoruz - EN YOĞUN SORGULAR'daki
    /// tek-DMV yaklaşımından FARKLI bir şekil, çünkü soru farklı: "hangi
    /// veritabanının hangi objesi hiç görünmedi" sorusu doğası gereği
    /// veritabanı bazlı bir tarama gerektiriyor.
    ///
    /// GÜRÜLTÜ FİLTRESİ: is_ms_shipped=0 (sistem prosedürleri değil) VE
    /// SSMS'in "Database Diagrams" özelliğinin otomatik oluşturduğu 7
    /// sabit isimli yardımcı prosedür (sp_creatediagram vb.) hariç -
    /// bunlar uygulama kodu değil, SSMS aracının kendi altyapısı; hemen
    /// hemen hiçbir üretim veritabanında gerçekten çağrılmazlar ve
    /// "kullanılmayan kod" listesinde anlamsız gürültü yaratırlar. İsim
    /// seti sabit ve Microsoft tarafından belgeli - EN YOĞUN SORGULAR'daki
    /// tempdb kuralıyla aynı ruhta: markaya değil, YAPIYA/kaynağa bakıyoruz.
    ///
    /// Canlı sunucuda ölçüldü: 3 veritabanı, ~1900 prosedür, ~0.5 sn.
    /// Index Analizi'yle aynı disiplin: saniyelik döngüye SOKULMADI.
    /// </summary>
    public const string UnusedProceduresScan = """
        SET NOCOUNT ON;

        IF OBJECT_ID('tempdb..#AllProcs') IS NOT NULL DROP TABLE #AllProcs;
        CREATE TABLE #AllProcs (
            DatabaseName sysname, SchemaName sysname, ProcName sysname,
            ObjectId INT, CreateDate DATETIME, ModifyDate DATETIME
        );

        IF OBJECT_ID('tempdb..#SeenPlanCache') IS NOT NULL DROP TABLE #SeenPlanCache;
        CREATE TABLE #SeenPlanCache (DatabaseName sysname, ObjectId INT);

        IF OBJECT_ID('tempdb..#SeenQueryStore') IS NOT NULL DROP TABLE #SeenQueryStore;
        CREATE TABLE #SeenQueryStore (DatabaseName sysname, ObjectId INT);

        IF OBJECT_ID('tempdb..#DbStatus') IS NOT NULL DROP TABLE #DbStatus;
        CREATE TABLE #DbStatus (
            DatabaseName sysname, QueryStoreEnabled BIT,
            QsEarliest DATETIME2, QsLatest DATETIME2, QsStaleThresholdDays INT
        );

        DECLARE @DbName sysname, @Sql NVARCHAR(MAX);
        DECLARE dbcur CURSOR LOCAL FAST_FORWARD FOR
            SELECT name FROM sys.databases
            WHERE database_id > 4 AND state = 0 AND is_read_only = 0 AND HAS_DBACCESS(name) = 1;
        OPEN dbcur; FETCH NEXT FROM dbcur INTO @DbName;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @Sql = N'INSERT INTO #AllProcs (DatabaseName, SchemaName, ProcName, ObjectId, CreateDate, ModifyDate)
                SELECT ''' + REPLACE(@DbName,'''','''''') + N''', s.name, p.name, p.object_id, p.create_date, p.modify_date
                FROM ' + QUOTENAME(@DbName) + N'.sys.procedures p
                JOIN ' + QUOTENAME(@DbName) + N'.sys.schemas s ON s.schema_id = p.schema_id
                WHERE p.is_ms_shipped = 0
                  AND p.name NOT IN (N''sp_upgraddiagrams'', N''sp_helpdiagrams'', N''sp_helpdiagramdefinition'',
                                      N''sp_creatediagram'', N''sp_renamediagram'', N''sp_alterdiagram'', N''sp_dropdiagram'');';
            EXEC sp_executesql @Sql;

            SET @Sql = N'IF EXISTS (SELECT 1 FROM ' + QUOTENAME(@DbName) + N'.sys.database_query_store_options WHERE actual_state <> 0)
                BEGIN
                    INSERT INTO #SeenQueryStore (DatabaseName, ObjectId)
                    SELECT DISTINCT ''' + REPLACE(@DbName,'''','''''') + N''', object_id
                    FROM ' + QUOTENAME(@DbName) + N'.sys.query_store_query
                    WHERE object_id IS NOT NULL AND object_id <> 0;

                    INSERT INTO #DbStatus (DatabaseName, QueryStoreEnabled, QsEarliest, QsLatest, QsStaleThresholdDays)
                    SELECT ''' + REPLACE(@DbName,'''','''''') + N''', 1,
                        (SELECT MIN(start_time) FROM ' + QUOTENAME(@DbName) + N'.sys.query_store_runtime_stats_interval),
                        (SELECT MAX(end_time) FROM ' + QUOTENAME(@DbName) + N'.sys.query_store_runtime_stats_interval),
                        stale_query_threshold_days
                    FROM ' + QUOTENAME(@DbName) + N'.sys.database_query_store_options;
                END
                ELSE
                BEGIN
                    INSERT INTO #DbStatus (DatabaseName, QueryStoreEnabled) VALUES (''' + REPLACE(@DbName,'''','''''') + N''', 0);
                END';
            EXEC sp_executesql @Sql;

            FETCH NEXT FROM dbcur INTO @DbName;
        END
        CLOSE dbcur; DEALLOCATE dbcur;

        -- Plan cache: hangi (db, object) şu an cache'te görülüyor - sunucu
        -- geneli TEK DMV, DB başına ayrı sorguya gerek yok. DB_NAME(...)
        -- NULL dönebilir (silinmiş/yetim bir veritabanına ait eski kayıt) -
        -- filtrelenmezse INSERT tamamen başarısız olur ve TÜM prosedürler
        -- yanlışlıkla "görülmedi" çıkar.
        INSERT INTO #SeenPlanCache (DatabaseName, ObjectId)
        SELECT DISTINCT DB_NAME(ps.database_id), ps.object_id
        FROM sys.dm_exec_procedure_stats ps WITH (NOLOCK)
        WHERE ps.database_id IS NOT NULL AND ps.object_id IS NOT NULL
          AND DB_NAME(ps.database_id) IS NOT NULL;

        SELECT TOP (@Top)
            ap.DatabaseName, ap.SchemaName, ap.ProcName, ap.CreateDate, ap.ModifyDate,
            QueryStoreEnabled = ISNULL(ds.QueryStoreEnabled, 0),
            -- Kanıt gücü ÜÇ seviyeli - yalnızca "Query Store açık mı" tek
            -- başına yeterli değil. QS az önce açıldıysa (canlıda görüldü:
            -- büyük bir veritabanında QS açıldıktan SONRA bile pencere
            -- yalnızca 1 saatlik veriye sahipti) penceresi henüz saatler
            -- derinliğinde - o pencerede "görülmedi" demek neredeyse hiçbir
            -- şey kanıtlamaz. Asıl güçlü kanıt İKİ şartı BİRDEN ister:
            -- prosedür QS'in izlemeye BAŞLADIĞI tarihten (QsEarliest) ÖNCE
            -- oluşturulmuş VE yine de hiç görülmemiş OLSA BİLE, pencerenin
            -- kendisi de en az 7 gün derinliğinde olmalı - yoksa "1 saattir
            -- izliyoruz, hiç çalışmadı" gibi anlamsız bir güven verir.
            --   2 = güçlü  (QS açık, proc pencereden önce vardı, pencere 7+ gün derin)
            --   1 = zayıf-yeni (QS açık ama proc pencereden yeni YA DA
            --       pencerenin kendisi henüz 7 günden kısa)
            --   0 = zayıf (QS hiç açık değil - yalnızca plan cache'e bakılabildi)
            EvidenceTier = CASE
                WHEN ISNULL(ds.QueryStoreEnabled, 0) = 1 AND ds.QsEarliest IS NOT NULL
                     AND ap.CreateDate < ds.QsEarliest
                     AND DATEDIFF(DAY, ds.QsEarliest, ds.QsLatest) >= 7 THEN 2
                WHEN ISNULL(ds.QueryStoreEnabled, 0) = 1 THEN 1
                ELSE 0
            END
        FROM #AllProcs ap
        LEFT JOIN #SeenPlanCache pc  ON pc.DatabaseName = ap.DatabaseName AND pc.ObjectId = ap.ObjectId
        LEFT JOIN #SeenQueryStore qs ON qs.DatabaseName = ap.DatabaseName AND qs.ObjectId = ap.ObjectId
        LEFT JOIN #DbStatus ds       ON ds.DatabaseName = ap.DatabaseName
        WHERE pc.ObjectId IS NULL AND qs.ObjectId IS NULL
        -- En güçlü kanıtlı satırlar ÖNCE - aksi halde Query Store kapalı
        -- (ya da yeni açılmış) büyük bir DB'nin yüzlerce zayıf-kanıtlı satırı
        -- TOP(@Top) içinde güvenilir adayları boğar. Canlıda doğrulandı:
        -- QS'i kapalı olan büyük bir veritabanı (1534 prosedürün 1255'i
        -- "görülmedi") yanında, QS'i 31+ gündür açık olan veritabanlarının
        -- güvenilir adayları varsayılan 150 satırlık görünümde eziliyordu.
        ORDER BY
            CASE
                WHEN ISNULL(ds.QueryStoreEnabled, 0) = 1 AND ds.QsEarliest IS NOT NULL
                     AND ap.CreateDate < ds.QsEarliest
                     AND DATEDIFF(DAY, ds.QsEarliest, ds.QsLatest) >= 7 THEN 2
                WHEN ISNULL(ds.QueryStoreEnabled, 0) = 1 THEN 1
                ELSE 0
            END DESC,
            ap.ModifyDate ASC;

        SELECT
            ap.DatabaseName,
            TotalProcs = COUNT(*),
            UnseenProcs = SUM(CASE WHEN pc.ObjectId IS NULL AND qs.ObjectId IS NULL THEN 1 ELSE 0 END),
            QueryStoreEnabled = MAX(CAST(ISNULL(ds.QueryStoreEnabled,0) AS INT)),
            QsEarliest = MAX(ds.QsEarliest),
            QsLatest = MAX(ds.QsLatest),
            QsStaleThresholdDays = MAX(ds.QsStaleThresholdDays)
        FROM #AllProcs ap
        LEFT JOIN #SeenPlanCache pc  ON pc.DatabaseName = ap.DatabaseName AND pc.ObjectId = ap.ObjectId
        LEFT JOIN #SeenQueryStore qs ON qs.DatabaseName = ap.DatabaseName AND qs.ObjectId = ap.ObjectId
        LEFT JOIN #DbStatus ds       ON ds.DatabaseName = ap.DatabaseName
        GROUP BY ap.DatabaseName;

        DROP TABLE #AllProcs;
        DROP TABLE #SeenPlanCache;
        DROP TABLE #SeenQueryStore;
        DROP TABLE #DbStatus;
        """;

    /// <summary>
    /// Kullanılmayan bir prosedürün tanımı - TAM DA çünkü sql_handle/
    /// plan_handle'ı YOK (hiç cache'e girmediği için). QueryPlanByHandle
    /// burada işe yaramaz; bunun yerine hedef veritabanının kendi
    /// sys.sql_modules'una üç parçalı isimle doğrudan bakıyoruz.
    ///
    /// @Database dinamik SQL'e QUOTENAME ile gömülüyor (üç parçalı isimde
    /// veritabanı adı parametre OLAMAZ) - servis katmanında ayrıca sıkı
    /// bir tanımlayıcı doğrulaması var. @Schema/@Proc gerçek sp_executesql
    /// parametresi, güvenli.
    /// </summary>
    public const string ProcedureSourceByName = """
        DECLARE @Sql NVARCHAR(MAX) = N'
            SELECT Definition = sm.definition
            FROM ' + QUOTENAME(@Database) + N'.sys.sql_modules sm
            JOIN ' + QUOTENAME(@Database) + N'.sys.procedures p ON p.object_id = sm.object_id
            JOIN ' + QUOTENAME(@Database) + N'.sys.schemas s ON s.schema_id = p.schema_id
            WHERE s.name = @Schema AND p.name = @Proc;';
        EXEC sp_executesql @Sql, N'@Schema sysname, @Proc sysname', @Schema=@Schema, @Proc=@Proc;
        """;

    /// <summary>
    /// SQL Agent job'ları - son çalışma sonucu, süresi ve şu an çalışıyor mu.
    ///
    /// msdb.dbo.* ÜÇ PARÇALI isimle okunuyor - BackupStatus'un yaptığı gibi,
    /// bağlantı bağlamını (Database=master) değiştirmeye gerek yok.
    /// İzleme hesabının msdb'de db_datareader olması yeterli - 02_monitoring_
    /// login.sql bunu zaten yedek geçmişi için veriyordu, ek yetki gerekmiyor.
    ///
    /// step_id = 0 ŞART: sysjobhistory her ADIM için ayrı satır tutar (adım 1,
    /// adım 2, ...) VE ayrıca step_id=0 ile "job'un kendisi" için bir özet
    /// satırı ekler. Bunu filtrelemezsen 5 adımlı bir job'un son çalışması
    /// 5 farklı (çoğu zaman çelişkili) satır olarak görünür.
    ///
    /// run_date/run_time SQL Agent'ın kendi tuhaf biçimi: run_date YYYYMMDD
    /// tamsayı, run_time HHMMSS tamsayı ama BAŞINDAKİ SIFIRLAR YOK (örn.
    /// saat 00:17:00 için run_time = 1700, 100'lük basamak saat değil).
    /// RIGHT('000000' + ..., 6) bu sıfırları geri koyup doğru CONVERT'i
    /// mümkün kılıyor - canlı sunucuda birebir doğrulandı.
    ///
    /// run_duration da AYNI mantıkla HHMMSS - saniyeye çevirmek için
    /// saat*3600 + dakika*60 + saniye gerekiyor, doğrudan sayı değil.
    ///
    /// DİKKAT: run_date/run_time SUNUCUNUN YEREL saatidir, UTC DEĞİL -
    /// msdb.dbo.backupset ile aynı durum (bkz. BackupStatus üstündeki not),
    /// aynı ilkeyle burada da dönüştürülmeden, olduğu gibi bırakılıyor.
    ///
    /// sysjobactivity'den "şu an çalışıyor mu" - start_execution_date dolu
    /// AMA stop_execution_date boşsa hâlâ sürüyor demektir.
    /// </summary>
    public const string AgentJobStatus = """
        ;WITH LastRun AS (
            SELECT
                jh.job_id, jh.run_status, jh.run_date, jh.run_time, jh.run_duration, jh.message,
                rn = ROW_NUMBER() OVER (PARTITION BY jh.job_id ORDER BY jh.run_date DESC, jh.run_time DESC)
            FROM msdb.dbo.sysjobhistory AS jh WITH (NOLOCK)
            WHERE jh.step_id = 0
        ),
        LastActivity AS (
            SELECT
                ja.job_id, ja.start_execution_date, ja.stop_execution_date,
                rn = ROW_NUMBER() OVER (PARTITION BY ja.job_id ORDER BY ja.start_execution_date DESC)
            FROM msdb.dbo.sysjobactivity AS ja WITH (NOLOCK)
            WHERE ja.start_execution_date IS NOT NULL
        )
        SELECT
            JobName        = j.name,
            IsEnabled      = j.enabled,
            CategoryName   = ISNULL(c.name, N''),
            LastRunOutcome = lr.run_status,
            LastRunAt      = CASE WHEN lr.run_date IS NULL THEN NULL ELSE
                                CONVERT(datetime,
                                    STUFF(STUFF(CONVERT(varchar(8), lr.run_date), 5, 0, '-'), 8, 0, '-') + ' ' +
                                    STUFF(STUFF(RIGHT('000000' + CONVERT(varchar(6), lr.run_time), 6), 3, 0, ':'), 6, 0, ':'))
                              END,
            LastRunDurationSeconds = CASE WHEN lr.run_duration IS NULL THEN NULL ELSE
                                (lr.run_duration / 10000) * 3600 + ((lr.run_duration / 100) % 100) * 60 + (lr.run_duration % 100)
                              END,
            LastRunMessage = lr.message,
            IsCurrentlyRunning = CONVERT(bit, CASE
                WHEN la.start_execution_date IS NOT NULL AND la.stop_execution_date IS NULL THEN 1
                ELSE 0 END)
        FROM msdb.dbo.sysjobs AS j WITH (NOLOCK)
        LEFT JOIN msdb.dbo.syscategories AS c WITH (NOLOCK) ON c.category_id = j.category_id
        LEFT JOIN LastRun AS lr WITH (NOLOCK) ON lr.job_id = j.job_id AND lr.rn = 1
        LEFT JOIN LastActivity AS la WITH (NOLOCK) ON la.job_id = j.job_id AND la.rn = 1
        ORDER BY
            CASE WHEN la.start_execution_date IS NOT NULL AND la.stop_execution_date IS NULL THEN 0 ELSE 1 END,
            CASE WHEN lr.run_status = 0 THEN 0 ELSE 1 END,
            j.enabled DESC,
            lr.run_date DESC, lr.run_time DESC
        OPTION (RECOMPILE);
        """;
}
