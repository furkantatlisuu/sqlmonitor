namespace SqlMonitor.Sql;

/// <summary>
/// İzleme veritabanının (SqlMonitorDb) kendi kurulum betiği - `db/01_create_
/// monitordb.sql` ile AYNI içerik, C# tarafında GÖMÜLÜ. Dosyayı diskten
/// okumak yerine gömmenin sebebi: uygulama nereye taşınırsa taşınsın
/// (yayına alma, farklı bir makine, `db/` klasörü kopyalanmamış olsa bile)
/// açılışta kendi veritabanını kurabilsin - harici bir dosyaya bağımlı
/// olmasın.
///
/// TAMAMEN İDEMPOTENT: her CREATE, "yoksa oluştur" koruması taşıyor.
/// Uygulama her açılışta bunu çalıştırır (bkz. Infrastructure.
/// StoreSchemaInitializer) - veritabanı zaten varsa PRINT mesajları
/// dışında hiçbir şey değişmez.
///
/// Veritabanının adı ARTIK SABİT DEĞİL: aşağıdaki {{DBNAME}}/{{DBQ}} yer
/// tutucularını StoreSchemaInitializer, StoreConnectionString'deki
/// Database= (InitialCatalog) değeriyle dolduruyor.
///
/// Eskiden ad koda gömülüydü ("SqlMonitorDb") ve bağlantı dizesinde
/// BAŞKA bir ad yazan herkes sessizce bozuk bir kuruluma düşüyordu:
/// betik "SqlMonitorDb"yi oluşturup "kuruldu" diye logluyor, uygulama ise
/// bağlantı dizesindeki asıl veritabanına bağlanmaya çalışıp "Cannot open
/// database" alıyordu. Sonuç: sunucu yönetimi ekranı kapalı, geçmiş yok,
/// hatırlanan giriş yok - üstelik açılış logu "hazır" diyordu. Projeyi
/// başka birine verirken veritabanına farklı bir ad vermek son derece
/// doğal olduğu için bu gerçek bir kurulum hatasıydı.
/// </summary>
public static class StoreSchemaSql
{
    /// <summary>
    /// GO ile ayrılmış batch'ler - ADO.NET'in SqlCommand'ı GO'yu ANLAMAZ
    /// (o yalnızca sqlcmd/SSMS'in istemci-taraflı bir kısaltması), bu
    /// yüzden StoreSchemaInitializer bunu GO satırlarından bölüp her
    /// batch'i AYRI bir ExecuteNonQueryAsync çağrısıyla çalıştırıyor.
    /// CREATE DATABASE ve USE'un neden kendi batch'inde olması GEREKTİĞİ
    /// de bundandır - aynı batch içinde olamazlar.
    /// </summary>
    public const string CreateMonitorDb = """
        IF DB_ID(N'{{DBNAME}}') IS NULL
        BEGIN
            PRINT '{{DBNAME}} oluşturuluyor...';
            EXEC(N'CREATE DATABASE {{DBQ}}');
        END
        GO

        ALTER DATABASE {{DBQ}} SET RECOVERY SIMPLE;
        GO

        USE {{DBQ}};
        GO

        IF SCHEMA_ID(N'mon') IS NULL EXEC(N'CREATE SCHEMA mon');
        GO

        IF OBJECT_ID(N'mon.Instance') IS NULL
        CREATE TABLE mon.Instance
        (
            InstanceId   INT           IDENTITY(1,1) NOT NULL,
            Name         NVARCHAR(128) NOT NULL,
            DisplayName  NVARCHAR(200) NULL,
            IsEnabled    BIT           NOT NULL CONSTRAINT DF_Instance_IsEnabled DEFAULT (1),
            CreatedAt    DATETIME2(0)  NOT NULL CONSTRAINT DF_Instance_CreatedAt DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT PK_Instance PRIMARY KEY CLUSTERED (InstanceId),
            CONSTRAINT UQ_Instance_Name UNIQUE (Name)
        );
        GO

        IF OBJECT_ID(N'mon.MetricSample') IS NULL
        CREATE TABLE mon.MetricSample
        (
            InstanceId  INT            NOT NULL,
            MetricKey   VARCHAR(64)    NOT NULL,
            CapturedAt  DATETIME2(0)   NOT NULL,
            Value       DECIMAL(19,4)  NOT NULL,
            CONSTRAINT PK_MetricSample PRIMARY KEY CLUSTERED (InstanceId, MetricKey, CapturedAt),
            CONSTRAINT FK_MetricSample_Instance FOREIGN KEY (InstanceId) REFERENCES mon.Instance(InstanceId)
        );
        GO

        IF OBJECT_ID(N'mon.WaitSample') IS NULL
        CREATE TABLE mon.WaitSample
        (
            InstanceId    INT           NOT NULL,
            CapturedAt    DATETIME2(0)  NOT NULL,
            WaitType      NVARCHAR(60)  NOT NULL,
            WaitTimeMs    BIGINT        NOT NULL,
            SignalTimeMs  BIGINT        NOT NULL,
            WaitingTasks  BIGINT        NOT NULL,
            CONSTRAINT PK_WaitSample PRIMARY KEY CLUSTERED (InstanceId, CapturedAt, WaitType),
            CONSTRAINT FK_WaitSample_Instance FOREIGN KEY (InstanceId) REFERENCES mon.Instance(InstanceId)
        );
        GO

        IF OBJECT_ID(N'mon.HealthEvent') IS NULL
        CREATE TABLE mon.HealthEvent
        (
            EventId      BIGINT        IDENTITY(1,1) NOT NULL,
            InstanceId   INT           NOT NULL,
            OccurredAt   DATETIME2(0)  NOT NULL,
            RuleKey      VARCHAR(64)   NOT NULL,
            Severity     TINYINT       NOT NULL,
            PrevSeverity TINYINT       NOT NULL,
            Title        NVARCHAR(200) NOT NULL,
            Detail       NVARCHAR(800) NULL,
            CONSTRAINT PK_HealthEvent PRIMARY KEY CLUSTERED (EventId),
            CONSTRAINT FK_HealthEvent_Instance FOREIGN KEY (InstanceId) REFERENCES mon.Instance(InstanceId)
        );
        GO

        -- Olayın YAŞANDIĞI ANDAKİ kanıtı (JSON): "uzun süren sorgu var"
        -- diyen bir olayın, hangi sorgu/prosedür olduğunu da söylemesi
        -- için. Canlı DMV'den sonradan okunamaz - kullanıcı olaya
        -- tıkladığında o oturum çoktan bitmiş olur; bu yüzden olay
        -- yazılırken saklanıyor.
        --
        -- NVARCHAR(MAX) ama pratikte küçük: yalnızca sağlıksız olaylara,
        -- en uzun 5 isteğe ve istek başına kırpılmış SQL metnine yazılıyor
        -- (bkz. HealthEvaluator.TakeEvidence). Eski satırlar zaten
        -- mevcut olay temizliğiyle birlikte siliniyor.
        IF COL_LENGTH(N'mon.HealthEvent', N'Evidence') IS NULL
            ALTER TABLE mon.HealthEvent ADD Evidence NVARCHAR(MAX) NULL;
        GO

        -- Evidence INCLUDE'a BİLEREK girmiyor: NVARCHAR(MAX) bir
        -- nonclustered index'i şişirir ve zaman çizelgesi listesi zaten
        -- içeriğini değil, yalnızca "var mı yok mu" bilgisini okuyor.
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_HealthEvent_Instance_Time')
        CREATE NONCLUSTERED INDEX IX_HealthEvent_Instance_Time
            ON mon.HealthEvent (InstanceId, OccurredAt DESC)
            INCLUDE (RuleKey, Severity, PrevSeverity, Title, Detail);
        GO

        IF OBJECT_ID(N'mon.CollectorRun') IS NULL
        CREATE TABLE mon.CollectorRun
        (
            RunId       BIGINT        IDENTITY(1,1) NOT NULL,
            InstanceId  INT           NOT NULL,
            StartedAt   DATETIME2(0)  NOT NULL,
            DurationMs  INT           NOT NULL,
            Succeeded   BIT           NOT NULL,
            ErrorText   NVARCHAR(2000) NULL,
            CONSTRAINT PK_CollectorRun PRIMARY KEY CLUSTERED (RunId),
            CONSTRAINT FK_CollectorRun_Instance FOREIGN KEY (InstanceId) REFERENCES mon.Instance(InstanceId)
        );
        GO

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CollectorRun_Instance_Time')
        CREATE NONCLUSTERED INDEX IX_CollectorRun_Instance_Time
            ON mon.CollectorRun (InstanceId, StartedAt DESC);
        GO

        -- Kullanıcının ekrandan ekleyip/düzenleyip/sildiği izlenen sunucu
        -- listesi. appsettings.json'daki Monitor:Instances SADECE ilk
        -- açılışta bu tablo BOŞSA tohum olarak kullanılır (bkz.
        -- Services.InstanceRegistry.InitializeAsync) - ondan sonra tek
        -- doğruluk kaynağı burasıdır, appsettings.json bir daha okunmaz.
        -- mon.Instance'tan (yukarısı) BİLEREK ayrı: o toplayıcının kendi
        -- ürettiği bir geçmiş-anahtarı tablosu (IDENTITY, otomatik dolar);
        -- bu ise kullanıcının doğrudan CRUD yaptığı bir yapılandırma
        -- tablosu - ikisini birleştirmek "geçmiş" ile "ayar"ı karıştırırdı.
        IF OBJECT_ID(N'mon.MonitoredInstance') IS NULL
        CREATE TABLE mon.MonitoredInstance
        (
            InstanceKey    NVARCHAR(128) NOT NULL,
            DisplayName    NVARCHAR(200) NULL,
            Server         NVARCHAR(256) NOT NULL,
            DatabaseName   NVARCHAR(128) NOT NULL CONSTRAINT DF_MonitoredInstance_Database DEFAULT (N'master'),
            UserId         NVARCHAR(128) NULL,
            Password       NVARCHAR(256) NULL,
            RequiresLogin  BIT           NOT NULL CONSTRAINT DF_MonitoredInstance_RequiresLogin DEFAULT (0),
            IsDefault      BIT           NOT NULL CONSTRAINT DF_MonitoredInstance_IsDefault DEFAULT (0),
            AutoDiscoverAgReplicas BIT   NOT NULL CONSTRAINT DF_MonitoredInstance_AutoDiscover DEFAULT (1),
            SortOrder      INT           NOT NULL CONSTRAINT DF_MonitoredInstance_SortOrder DEFAULT (0),
            CreatedAt      DATETIME2(0)  NOT NULL CONSTRAINT DF_MonitoredInstance_CreatedAt DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT PK_MonitoredInstance PRIMARY KEY CLUSTERED (InstanceKey)
        );
        GO

        -- Sonradan eklendi (idempotent ALTER) - AG replikalarının kayıtlı
        -- adı (sys.availability_replicas.replica_server_name) bazı ağlarda
        -- DNS'ten çözülmüyor; canlıda doğrulandı (nslookup/ping "Non-existent
        -- domain", ama IP'ye doğrudan bağlanmak çalışıyor). "host=ip;host=ip"
        -- biçiminde, boş bırakılabilir - bkz. SqlConnectionFactory.RebindServer.
        IF COL_LENGTH(N'mon.MonitoredInstance', N'ReplicaHostOverrides') IS NULL
            ALTER TABLE mon.MonitoredInstance ADD ReplicaHostOverrides NVARCHAR(1000) NULL;
        GO

        -- Sonradan eklendi (idempotent ALTER) - Slack bildirimi TEK bir
        -- global SlackWebhookUrl'e bağlı ama HANGİ instance'ların Critical
        -- geçişlerinin oraya gideceği burada, instance başına seçilir.
        -- Varsayılan 0 (kapalı): yeni bir sunucu eklendiğinde ya da bu
        -- kolon ilk kez eklendiğinde hiçbir şey sessizce Slack'e gitmeye
        -- başlamasın - kullanıcı bilerek açmalı. Bkz. CollectorService.
        IF COL_LENGTH(N'mon.MonitoredInstance', N'SlackAlertsEnabled') IS NULL
            ALTER TABLE mon.MonitoredInstance ADD SlackAlertsEnabled BIT NOT NULL CONSTRAINT DF_MonitoredInstance_SlackAlerts DEFAULT (0);
        GO

        -- RequiresLogin=true bir instance'a ekrandan girilen kimlik bilgisi
        -- BAŞARILI olduktan sonra burada hatırlanır - kullanıcı "her F5'te/
        -- yeniden başlatmada yeniden gir" istemedi. mon.MonitoredInstance'taki
        -- UserId/Password'tan BİLEREK ayrı: o tablo "Sunucuları yönet"
        -- ekranında görünür/düzenlenir, bu ise görünmez - RequiresLogin
        -- bayrağının "ekrandan sor" anlamı ekranda hâlâ doğru kalsın diye.
        -- Yalnızca Services.RememberedCredentialStore okur/yazar; giriş
        -- ekranından (Api/AuthEndpoints.cs) başarılı girişte doldurulur,
        -- "Çıkış yap"ta ya da instance silinince/düzenlenince temizlenir.
        IF OBJECT_ID(N'mon.RememberedCredential') IS NULL
        CREATE TABLE mon.RememberedCredential
        (
            InstanceKey NVARCHAR(128) NOT NULL,
            UserId      NVARCHAR(128) NOT NULL,
            Password    NVARCHAR(256) NOT NULL,
            SavedAt     DATETIME2(0)  NOT NULL CONSTRAINT DF_RememberedCredential_SavedAt DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT PK_RememberedCredential PRIMARY KEY CLUSTERED (InstanceKey)
        );
        GO

        -- Ekrandan girilen, dosyada TUTULMAYAN uygulama ayarları.
        --
        -- Şu an tek kullanıcısı Slack webhook adresi. appsettings.json'da
        -- durmasının iki sakıncası vardı: dosya hem depoya hem paylaşılan
        -- zip'e giriyor (webhook'u eline geçiren herkes kanala mesaj
        -- atabilir), hem de yayın çıktısını her aldığımızda yeniden
        -- yazılması gerekiyordu. Burada durduğunda hiçbir dosyada
        -- görünmüyor, yedeklenen tek yer izleme veritabanı oluyor.
        --
        -- Yalnızca Services.AppSettingStore okur/yazar.
        IF OBJECT_ID(N'mon.AppSetting') IS NULL
        CREATE TABLE mon.AppSetting
        (
            Name      NVARCHAR(100) NOT NULL,
            Value     NVARCHAR(1000) NULL,
            UpdatedAt DATETIME2(0)  NOT NULL CONSTRAINT DF_AppSetting_UpdatedAt DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT PK_AppSetting PRIMARY KEY CLUSTERED (Name)
        );
        GO

        CREATE OR ALTER PROCEDURE mon.usp_EnsureInstance
            @Name        NVARCHAR(128),
            @DisplayName NVARCHAR(200) = NULL,
            @InstanceId  INT OUTPUT
        AS
        BEGIN
            SET NOCOUNT ON;

            SELECT @InstanceId = InstanceId FROM mon.Instance WHERE Name = @Name;

            IF @InstanceId IS NULL
            BEGIN
                INSERT INTO mon.Instance (Name, DisplayName)
                VALUES (@Name, @DisplayName);

                SET @InstanceId = SCOPE_IDENTITY();
            END
            ELSE IF @DisplayName IS NOT NULL
            BEGIN
                UPDATE mon.Instance
                   SET DisplayName = @DisplayName
                 WHERE InstanceId = @InstanceId
                   AND ISNULL(DisplayName, N'') <> @DisplayName;
            END
        END
        GO

        CREATE OR ALTER PROCEDURE mon.usp_PurgeOldData
            @RetentionDays INT = 30
        AS
        BEGIN
            SET NOCOUNT ON;

            DECLARE @cutoff DATETIME2(0) = DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME());
            DECLARE @rows INT = 1;

            WHILE @rows > 0
            BEGIN
                DELETE TOP (5000) FROM mon.MetricSample WHERE CapturedAt < @cutoff;
                SET @rows = @@ROWCOUNT;
            END

            SET @rows = 1;
            WHILE @rows > 0
            BEGIN
                DELETE TOP (5000) FROM mon.WaitSample WHERE CapturedAt < @cutoff;
                SET @rows = @@ROWCOUNT;
            END

            SET @rows = 1;
            WHILE @rows > 0
            BEGIN
                DELETE TOP (5000) FROM mon.CollectorRun WHERE StartedAt < @cutoff;
                SET @rows = @@ROWCOUNT;
            END

            DECLARE @eventCutoff DATETIME2(0) = DATEADD(DAY, -(@RetentionDays * 3), SYSUTCDATETIME());
            SET @rows = 1;
            WHILE @rows > 0
            BEGIN
                DELETE TOP (5000) FROM mon.HealthEvent WHERE OccurredAt < @eventCutoff;
                SET @rows = @@ROWCOUNT;
            END
        END
        GO
        """;
}
