using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlMonitor.Options;
using SqlMonitor.Services;

namespace SqlMonitor.Infrastructure;

/// <summary>
/// Bağlantı üretiminin tek noktası.
///
/// Neden fabrika? Çünkü izleme uygulamasının iki farklı hedefi var ve
/// bunların karışması tehlikeli: izlenen PROD sunucusu (sadece okuma,
/// kısa timeout) ve kendi SqlMonitorDb'si (yazma). Tek yerden geçirince
/// timeout ve uygulama adı gibi güvenlik ayarlarını unutmak zorlaşır.
///
/// RequiresLogin=true olan instance'lar (bkz. InstanceOptions.RequiresLogin)
/// için ConnectionString'te kullanıcı adı/şifre YOKTUR - her bağlantı
/// isteğinde ProdCredentialStore'dan TAZE okunup eklenir. Kilit açık
/// değilse (henüz kimse doğru girişi yapmadıysa) ProdLoginRequiredException
/// fırlatılır; çağıran uçlar bunu 401 "giriş gerekli" cevabına çevirir.
/// </summary>
public sealed class SqlConnectionFactory
{
    private readonly MonitorOptions _options;
    private readonly ProdCredentialStore _credentials;

    public SqlConnectionFactory(IOptions<MonitorOptions> options, ProdCredentialStore credentials)
    {
        _options = options.Value;
        _credentials = credentials;
    }

    /// <summary>İzleme verisinin tutulduğu SqlMonitorDb.</summary>
    public SqlConnection CreateStoreConnection()
    {
        if (string.IsNullOrWhiteSpace(_options.StoreConnectionString))
            throw new InvalidOperationException(
                "Monitor:StoreConnectionString ayarlanmamış. appsettings.json'a bak.");

        return new SqlConnection(_options.StoreConnectionString);
    }

    /// <summary>
    /// İzlenen SQL Server instance'ı.
    ///
    /// instance.RequiresLogin=true ve henüz giriş yapılmamışsa
    /// ProdLoginRequiredException fırlatır - bağlantı hiç açılmaya
    /// çalışılmaz.
    /// </summary>
    public SqlConnection CreateTargetConnection(InstanceOptions instance)
    {
        var baseConnectionString = ResolveConnectionString(instance);

        if (string.IsNullOrWhiteSpace(baseConnectionString))
            throw new InvalidOperationException(
                $"'{instance.Name}' instance'ının bağlantı dizesi boş.");

        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            // İzlenen sunucuya bağlanırken uzun beklemeyiz. Sunucu zorda
            // olduğunda izleme aracının onu daha da bekletmesi kabul edilemez.
            ConnectTimeout = Math.Min(
                new SqlConnectionStringBuilder(baseConnectionString).ConnectTimeout,
                5),

            // sys.dm_exec_sessions'ta kim olduğumuz belli olsun. Bir gün
            // "bu bağlantılar kimin" diye baktığında kendini tanıyacaksın.
            ApplicationName = "SqlMonitor.Live"
        };

        return new SqlConnection(builder.ConnectionString);
    }

    public int QueryTimeoutSeconds => _options.QueryTimeoutSeconds;

    /// <summary>
    /// Yedek geçmişi okunacak diğer replika.
    ///
    /// Ayrı bir metot olmasının sebebi ApplicationName: sys.dm_exec_sessions'a
    /// bakan biri, canlı izleme bağlantısı ile yedek geçmişi için açılmış
    /// kısa ömürlü bağlantıyı ayırt edebilmeli.
    ///
    /// Bağlantı zaman aşımı burada daha da kısa (3 sn): bu bağlantı
    /// isteğe bağlı bir zenginleştirmedir. Ulaşılamayan bir replika,
    /// asıl panelin gecikmesine sebep olmamalı.
    ///
    /// owningInstance.RequiresLogin=true ise source.ConnectionString'e
    /// (kimlik bilgisi TAŞIMAZ) ProdCredentialStore'daki TAZE kimlik
    /// bilgisi eklenir - ana bağlantıyla aynı disiplin, aynı çalışma
    /// zamanında girilen kullanıcı adı/şifre replika okumaları için de
    /// geçerli sayılır (AutoDiscoverAgReplicas'ın zaten varsaydığı "aynı
    /// kimlik bilgisi her replikada geçerli" ilkesiyle tutarlı).
    /// </summary>
    public SqlConnection CreateBackupSourceConnection(InstanceOptions owningInstance, BackupSourceOptions source)
    {
        if (string.IsNullOrWhiteSpace(source.ConnectionString))
            throw new InvalidOperationException("Yedek kaynağının bağlantı dizesi boş.");

        var baseConnectionString = ApplyProdCredentialsIfNeeded(source.ConnectionString, owningInstance);

        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            ConnectTimeout = Math.Min(
                new SqlConnectionStringBuilder(baseConnectionString).ConnectTimeout,
                3),
            ApplicationName = "SqlMonitor.BackupHistory"
        };

        return new SqlConnection(builder.ConnectionString);
    }

    /// <summary>
    /// Bir bağlantı dizesindeki sunucuyu değiştirir, gerisini korur.
    ///
    /// AG replikası keşfedildiğinde kullanılır: kullanıcı adı, parola ve
    /// şifreleme ayarları aynı kalsın, sadece hedef makine değişsin
    /// istiyoruz. Veritabanını master'a sabitliyoruz, çünkü secondary
    /// replikadaki bir kullanıcı veritabanı okunabilir olmayabilir ve
    /// oraya bağlanmayı denemek boş yere hata verir.
    ///
    /// BİLEREK kimlik bilgisi EKLEMİYOR - instance.ConnectionString
    /// RequiresLogin=true için zaten kimliksiz. Kimlik bilgisi ancak
    /// bağlantı gerçekten açılırken (CreateBackupSourceConnection'da)
    /// ProdCredentialStore'dan TAZE okunup eklenir; burada eklemiş
    /// olsaydık, rebind anı ile bağlanma anı arasında kilit değişirse
    /// (örn. çıkış yapılırsa) bayat bir kimlik taşınmış olurdu.
    ///
    /// serverName önce instance.ReplicaHostOverrides'a bakılarak
    /// çözülüyor - DMV'nin döndürdüğü ad bazı ağlarda DNS'ten
    /// çözülmüyor (canlıda doğrulandı), eşleme varsa gerçek adres
    /// (genelde bir IP) onun yerine kullanılıyor.
    /// </summary>
    public string RebindServer(InstanceOptions instance, string serverName)
    {
        var effectiveHost = instance.ReplicaHostOverrides.TryGetValue(serverName, out var mapped)
            ? mapped
            : serverName;

        var builder = new SqlConnectionStringBuilder(instance.ConnectionString)
        {
            DataSource = effectiveHost,
            InitialCatalog = "master"
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Aynı bağlantı dizesi, veritabanı yalnızca "master"a sabitlenmiş.
    ///
    /// StoreConnectionString genelde Database=SqlMonitorDb taşır - ama
    /// SqlMonitorDb HENÜZ YOKSA (ilk açılış) o veritabanına doğrudan
    /// bağlanmaya çalışmak bağlantının kendisini patlatır. StoreSchemaInitializer
    /// bunun yerine buradan açılan bağlantıyla CREATE DATABASE'i çalıştırır,
    /// sonra script'in kendi içindeki USE SqlMonitorDb ile AYNI bağlantı
    /// üzerinde bağlamı değiştirir.
    /// </summary>
    public static string WithMasterCatalog(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        return builder.ConnectionString;
    }

    /// <summary>Bağlantı dizesindeki sunucu adı - bulguda göstermek için.</summary>
    public static string ServerNameOf(string connectionString)
    {
        try
        {
            var name = new SqlConnectionStringBuilder(connectionString).DataSource;
            return string.IsNullOrWhiteSpace(name) ? "?" : name;
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    /// Giriş ekranından gelen kullanıcı adı/şifreyi GERÇEKTEN doğrular -
    /// SQL Server'a bağlanmayı dener. Başarılıysa sessizce döner;
    /// başarısızsa (yanlış şifre, sunucuya ulaşılamadı, ...) istisna
    /// fırlatır - hüküm SQL Server'ın kendisine ait, burada ayrıca bir
    /// "doğru şifre" bilgisi TUTULMUYOR/karşılaştırılmıyor.
    /// </summary>
    public async Task VerifyCredentialsAsync(
        InstanceOptions instance, string userId, string password, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(instance.ConnectionString)
        {
            UserID = userId,
            Password = password,
            IntegratedSecurity = false,
            ConnectTimeout = 5,
            ApplicationName = "SqlMonitor.Live"
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
    }

    private string ResolveConnectionString(InstanceOptions instance)
        => ApplyProdCredentialsIfNeeded(instance.ConnectionString, instance);

    /// <summary>
    /// instance.RequiresLogin=false ise baseConnectionString'i olduğu gibi
    /// döndürür (appsettings.json'daki kimlik bilgisi zaten yeterli).
    /// true ise ProdCredentialStore'dan TAZE kullanıcı adı/şifreyi okuyup
    /// ekler - kilit açık değilse ProdLoginRequiredException fırlatır.
    /// </summary>
    private string ApplyProdCredentialsIfNeeded(string baseConnectionString, InstanceOptions instance)
    {
        if (!instance.RequiresLogin) return baseConnectionString;

        if (!_credentials.TryGet(instance.Name, out var userId, out var password))
            throw new ProdLoginRequiredException(instance.Name);

        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            UserID = userId,
            Password = password,
            IntegratedSecurity = false
        };

        return builder.ConnectionString;
    }
}

/// <summary>
/// RequiresLogin=true olan bir instance'a, kilit henüz açılmadan (kimse
/// doğru kullanıcı adı/şifreyi girmeden) bağlanmaya çalışıldığında
/// fırlatılır. Api/LiveEndpoints.cs bunu 401 "login_required" cevabına
/// çevirir - normal bir DMV/bağlantı hatasıyla (502) KARIŞTIRILMAMALI,
/// frontend ikisini ayrı ayrı ele alıyor (biri giriş formu gösterir,
/// diğeri panel hatası gösterir).
/// </summary>
public sealed class ProdLoginRequiredException : Exception
{
    public string InstanceName { get; }

    public ProdLoginRequiredException(string instanceName)
        : base($"'{instanceName}' için giriş yapılmamış.")
        => InstanceName = instanceName;
}
