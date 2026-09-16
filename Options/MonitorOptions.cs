namespace SqlMonitor.Options;

/// <summary>
/// appsettings.json içindeki "Monitor" bölümünün karşılığı.
/// </summary>
public sealed class MonitorOptions
{
    public const string SectionName = "Monitor";

    /// <summary>İzleme verisinin yazıldığı SqlMonitorDb bağlantısı.</summary>
    public string StoreConnectionString { get; set; } = string.Empty;

    /// <summary>Arka plan toplayıcının örnekleme aralığı.</summary>
    public int CollectIntervalSeconds { get; set; } = 30;

    /// <summary>Metrik saklama süresi (gün).</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// İzlenen sunucuya açılan her komutun üst sınırı. Kısa tutuyoruz:
    /// izleme sorgusu asla izlediği sunucuyu bekletmemeli. Zaman aşımına
    /// uğrayan panel boş görünür, sunucu yavaşlamaz.
    /// </summary>
    public int QueryTimeoutSeconds { get; set; } = 8;

    /// <summary>
    /// sp_readerrorlog securityadmin ister. Hesabına o yetkiyi
    /// vermediysen false bırak, error log paneli devre dışı kalır.
    /// </summary>
    public bool EnableErrorLogPanel { get; set; }

    /// <summary>
    /// Diğer AG replikalarından okunan yedek geçmişinin önbellek süresi.
    ///
    /// Yedek geçmişi saatlik değişen bir veridir; toplayıcı ise 30
    /// saniyede bir çalışır. Önbelleksiz olsaydı her turda 2 ayrı
    /// makineye bağlanıp msdb tarardık - izlediğimiz sistemi yormak
    /// izleme aracının en son yapması gereken şey.
    /// </summary>
    public int BackupHistoryCacheSeconds { get; set; } = 120;

    /// <summary>
    /// Slack Incoming Webhook URL'i. Boşsa bildirim hiç gönderilmez
    /// (Services/SlackNotifier.cs sessizce no-op) - kurulum sonrası,
    /// paylaşılan appsettings.json'da varsayılan olarak boş kalır.
    /// </summary>
    public string SlackWebhookUrl { get; set; } = string.Empty;

    public List<InstanceOptions> Instances { get; set; } = new();

    public ThresholdOptions Thresholds { get; set; } = new();

    public InstanceOptions? FindInstance(string? name)
    {
        if (Instances.Count == 0) return null;

        if (string.IsNullOrWhiteSpace(name))
            return Instances.FirstOrDefault(i => i.IsDefault) ?? Instances[0];

        return Instances.FirstOrDefault(
            i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class InstanceOptions
{
    /// <summary>Kısa anahtar. API'de ?instance= ile bu isim geçilir.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Ekranda görünen ad.</summary>
    public string? DisplayName { get; set; }

    public string ConnectionString { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    /// <summary>
    /// true ise bu instance'ın ConnectionString'inde kullanıcı adı/şifre
    /// YOKTUR - ekrandan çalışma zamanında girilir (bkz. ProdCredentialStore,
    /// SqlConnectionFactory.VerifyCredentialsAsync, Api/AuthEndpoints).
    /// Canlı (Prod) sunucular için: appsettings.json'da gerçek şifre
    /// tutulmasın, girişi SQL Server'ın kendisi doğrulasın istendi.
    /// </summary>
    public bool RequiresLogin { get; set; }

    /// <summary>
    /// AG replikalarını sys.availability_replicas üzerinden kendiliğinden
    /// bul ve yedek geçmişini onlardan da oku (msdb.dbo.backupset her
    /// replikada yereldir, AG onu senkronize etmez - yedek 3. makinede
    /// alınıyorsa primary'ye bakan izleme tek başına "hiç yedek yok" derdi).
    ///
    /// Keşfedilen replikaya bağlanırken bu instance'ın bağlantı dizesi
    /// kullanılır, sadece Server kısmı değişir - yani aynı kullanıcı adı
    /// ve parolanın diğer replikalarda da geçerli olduğu varsayılır. AG
    /// kurulumlarında bu genellikle doğrudur. AG yoksa bu ayarın hiçbir
    /// etkisi olmaz - sorgular boş küme döner.
    /// </summary>
    public bool AutoDiscoverAgReplicas { get; set; } = true;

    /// <summary>
    /// sys.availability_replicas.replica_server_name → gerçek adres eşlemesi.
    /// Bazı ağlarda replikanın kayıtlı adı DNS'ten çözülmüyor (canlıda
    /// doğrulandı - nslookup/ping "Non-existent domain" veriyor, ama IP'ye
    /// doğrudan bağlanmak çalışıyor); otomatik keşif o zaman o replikaya
    /// HİÇ ulaşamaz. Eşleme varsa SqlConnectionFactory.RebindServer adın
    /// yerine burayı kullanır. Anahtar karşılaştırması büyük/küçük harf
    /// duyarsız - DMV'nin döndürdüğü ad hangi harfle gelirse gelsin eşleşsin diye.
    /// </summary>
    public Dictionary<string, string> ReplicaHostOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bu instance'ın Critical geçişleri Slack'e gitsin mi - bkz. CollectorService/SlackNotifier.</summary>
    public bool SlackAlertsEnabled { get; set; }
}

/// <summary>
/// Eşikler. Bunlar uygulamanın "hüküm" katmanının tamamı: ham sayıyı
/// sağlıklı / uyarı / kritik hükmüne çeviren tek yer burasıdır.
/// Sunucunun karakterine göre ayarlanmalı, sabit doğru değildir.
/// </summary>
public sealed class ThresholdOptions
{
    public decimal CpuWarn { get; set; } = 70;
    public decimal CpuCritical { get; set; } = 85;

    /// <summary>PLE'de düşük kötüdür, yön ters.</summary>
    public decimal PleWarn { get; set; } = 1000;
    public decimal PleCritical { get; set; } = 300;

    public decimal TempDbWarn { get; set; } = 70;
    public decimal TempDbCritical { get; set; } = 90;

    public decimal DiskWarn { get; set; } = 80;
    public decimal DiskCritical { get; set; } = 90;

    /// <summary>Buffer cache hit'te de düşük kötüdür.</summary>
    public decimal BufferCacheHitWarn { get; set; } = 97;
    public decimal BufferCacheHitCritical { get; set; } = 90;

    public decimal WriteLatencyWarnMs { get; set; } = 20;
    public decimal WriteLatencyCriticalMs { get; set; } = 100;
    public decimal ReadLatencyWarnMs { get; set; } = 20;
    public decimal ReadLatencyCriticalMs { get; set; } = 100;

    public int BlockedSessionsWarn { get; set; } = 1;
    public int BlockedSessionsCritical { get; set; } = 5;

    public int LongRunningQueryWarnSeconds { get; set; } = 30;
    public int LongRunningQueryCriticalSeconds { get; set; } = 300;

    public int BackupAgeWarnDays { get; set; } = 1;
    public int BackupAgeCriticalDays { get; set; } = 3;

    /// <summary>
    /// Log yedeğinin "gecikmiş" sayılacağı yaş (dakika).
    ///
    /// log_reuse_wait_desc = LOG_BACKUP tek başına bir sorun DEĞİLDİR;
    /// FULL recovery'de iki log yedeği arasındaki normal durumdur. Sorun,
    /// bu durumun geçmemesidir. O yüzden hüküm bu eşikle veriliyor:
    /// log yedek aralığının birkaç katı olacak şekilde ayarla.
    /// 15 dakikada bir yedek alan bir sunucuda 60 rahat bir değerdir.
    /// </summary>
    public int LogBackupAgeWarnMinutes { get; set; } = 60;
}

/// <summary>
/// Otomatik keşifle bulunan bir AG replikası - yedek geçmişi okumak için.
/// Yalnızca msdb.dbo.backupset okunur; bu sunucunun sağlık panelleri
/// hesaplanmaz - burada tek merak ettiğimiz yedeğin ne zaman alındığı.
/// Bkz. BackupHistoryReader.ResolveSourcesAsync.
/// </summary>
public sealed class BackupSourceOptions
{
    /// <summary>Bulguda görünecek ad. Boşsa bağlantıdaki sunucu adı kullanılır.</summary>
    public string? Name { get; set; }

    public string ConnectionString { get; set; } = string.Empty;
}
