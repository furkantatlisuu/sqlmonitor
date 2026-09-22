using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlMonitor.Infrastructure;
using SqlMonitor.Models;
using SqlMonitor.Options;

namespace SqlMonitor.Services;

/// <summary>
/// İzlenen sunucu listesinin TEK doğruluk kaynağı.
///
/// appsettings.json'daki Monitor:Instances artık yalnızca İLK açılışta,
/// mon.MonitoredInstance tablosu BOŞSA bir "tohum" olarak kullanılır -
/// ondan sonra kullanıcı ekrandan ("Sunucuları yönet") ekleyip/silip/
/// düzenlediği sürece appsettings.json bir daha hiç okunmaz. Bu, kullanıcının
/// "Test/Prod'u kendim silip istediğim sunucuyu/IP'yi verebilmem lazım"
/// isteğinin karşılığı - appsettings.json'u elle düzenleyip uygulamayı
/// yeniden başlatmaya gerek kalmadan.
///
/// StoreConnectionString boşsa (izleme veritabanı hiç yapılandırılmamışsa)
/// DB'ye hiç dokunmadan doğrudan appsettings.json'daki statik listeye
/// düşer - "izleme geçmişi çalışmaz ama canlı ekran çalışır" ilkesiyle
/// tutarlı, bkz. StoreSchemaInitializer'daki aynı yaklaşım.
/// </summary>
public sealed class InstanceRegistry
{
    private readonly SqlConnectionFactory _factory;
    private readonly MonitorOptions _options;
    private readonly ProdCredentialStore _credentials;
    private readonly RememberedCredentialStore _rememberedCredentials;
    private readonly ILogger<InstanceRegistry> _log;

    private readonly SemaphoreSlim _lock = new(1, 1);

    private volatile IReadOnlyList<InstanceOptions> _resolved = Array.Empty<InstanceOptions>();
    private volatile IReadOnlyList<InstanceRecord> _records = Array.Empty<InstanceRecord>();

    /// <summary>DB'ye hiç ulaşılamıyorsa (StoreConnectionString boş/erişilemez) yönetim ekranı kapatılır - CRUD burada bozuk bir DB'ye yazamaz.</summary>
    public bool IsManaged { get; private set; }

    public InstanceRegistry(
        SqlConnectionFactory factory,
        IOptions<MonitorOptions> options,
        ProdCredentialStore credentials,
        RememberedCredentialStore rememberedCredentials,
        ILogger<InstanceRegistry> log)
    {
        _factory = factory;
        _options = options.Value;
        _credentials = credentials;
        _rememberedCredentials = rememberedCredentials;
        _log = log;
    }

    /// <summary>Bağlantı açarken kullanılacak, hazır ConnectionString'li hâl - snapshot/DMV tarafının tükettiği şey budur.</summary>
    public IReadOnlyList<InstanceOptions> Instances => _resolved;

    /// <summary>Yönetim ekranının listelediği ham alanlar (şifre hariç, bkz. InstanceSummary).</summary>
    public IReadOnlyList<InstanceRecord> Records => _records;

    public InstanceOptions? Find(string? name)
    {
        var list = _resolved;
        if (list.Count == 0) return null;

        if (string.IsNullOrWhiteSpace(name))
            return list.FirstOrDefault(i => i.IsDefault) ?? list[0];

        return list.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.StoreConnectionString))
        {
            // İzleme DB'si yok - yönetilebilir bir liste sunamayız,
            // appsettings.json'daki statik listeyle idare ediyoruz.
            _resolved = _options.Instances;
            IsManaged = false;
            return;
        }

        try
        {
            await using var conn = _factory.CreateStoreConnection();
            await conn.OpenAsync(ct);

            var count = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT COUNT(*) FROM mon.MonitoredInstance", cancellationToken: ct));

            if (count == 0 && _options.Instances.Count > 0)
            {
                _log.LogInformation(
                    "mon.MonitoredInstance boş - appsettings.json'daki {Count} sunucuyla tohumlanıyor. " +
                    "Bundan sonra sunucu listesi yönetimi \"Sunucuları yönet\" ekranından yapılır, " +
                    "appsettings.json'daki Instances bir daha okunmaz.",
                    _options.Instances.Count);

                foreach (var seed in _options.Instances)
                    await InsertSeedAsync(conn, seed, ct);
            }

            IsManaged = true;
            await ReloadCoreAsync(conn, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "İzlenen sunucu listesi mon.MonitoredInstance'tan okunamadı - " +
                "appsettings.json'daki statik listeye düşülüyor, yönetim ekranı devre dışı.");
            _resolved = _options.Instances;
            IsManaged = false;
        }
    }

    private static async Task InsertSeedAsync(SqlConnection conn, InstanceOptions seed, CancellationToken ct)
    {
        var b = new SqlConnectionStringBuilder(seed.ConnectionString);

        const string sql = """
            INSERT INTO mon.MonitoredInstance
                (InstanceKey, DisplayName, Server, DatabaseName, UserId, Password, RequiresLogin, IsDefault, AutoDiscoverAgReplicas, SlackAlertsEnabled)
            VALUES
                (@Key, @DisplayName, @Server, @Database, @UserId, @Password, @RequiresLogin, @IsDefault, @AutoDiscover, @SlackAlertsEnabled);
            """;

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Key = seed.Name,
            seed.DisplayName,
            Server = b.DataSource,
            Database = string.IsNullOrWhiteSpace(b.InitialCatalog) ? "master" : b.InitialCatalog,
            UserId = seed.RequiresLogin || string.IsNullOrWhiteSpace(b.UserID) ? null : b.UserID,
            Password = seed.RequiresLogin || string.IsNullOrWhiteSpace(b.Password) ? null : b.Password,
            seed.RequiresLogin,
            seed.IsDefault,
            AutoDiscover = seed.AutoDiscoverAgReplicas,
            seed.SlackAlertsEnabled
        }, cancellationToken: ct));
    }

    public async Task ReloadAsync(CancellationToken ct)
    {
        if (!IsManaged) return;

        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);
        await ReloadCoreAsync(conn, ct);
    }

    private async Task ReloadCoreAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT InstanceKey, DisplayName, Server, DatabaseName, UserId, Password,
                   RequiresLogin, IsDefault, AutoDiscoverAgReplicas, SortOrder, ReplicaHostOverrides,
                   SlackAlertsEnabled
            FROM mon.MonitoredInstance
            ORDER BY SortOrder, InstanceKey;
            """;

        var rows = (await conn.QueryAsync<InstanceRecord>(
            new CommandDefinition(sql, cancellationToken: ct))).ToList();

        _records = rows;
        _resolved = rows.Select(ToInstanceOptions).ToList();
    }

    private static InstanceOptions ToInstanceOptions(InstanceRecord r)
    {
        var replicaHosts = ParseReplicaHosts(r.ReplicaHostOverrides);

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = r.Server,
            InitialCatalog = string.IsNullOrWhiteSpace(r.DatabaseName) ? "master" : r.DatabaseName,
            TrustServerCertificate = true
        };

        // RequiresLogin=false VE bir kullanıcı adı kayıtlıysa doğrudan
        // bağlantı dizesine göm - appsettings.json'daki TEST'in davranışıyla
        // birebir aynı. RequiresLogin=true ise burada HİÇBİR kimlik bilgisi
        // yok; SqlConnectionFactory bunu ProdCredentialStore'dan tazeler.
        if (!r.RequiresLogin && !string.IsNullOrWhiteSpace(r.UserId))
        {
            builder.UserID = r.UserId;
            builder.Password = r.Password ?? "";
            builder.IntegratedSecurity = false;
        }
        else if (!r.RequiresLogin)
        {
            // Kullanıcı adı YOK ve ekrandan da sorulmayacak - geriye tek
            // anlamlı seçenek Windows kimlik doğrulaması kalıyor. Eskiden
            // burası boş geçiliyordu ve bağlantı dizesi ne kullanıcı adı ne
            // de Integrated Security taşıyordu: böyle eklenen bir sunucu
            // ASLA bağlanamıyor, "Login failed for user ''" (18456) veriyordu.
            // İzleme veritabanının kendi varsayılan bağlantısı da (bkz.
            // appsettings.json) zaten Windows kimliğiyle çalışıyor - izlenen
            // sunucu için de aynısının mümkün olmaması bir eksiklikti.
            builder.IntegratedSecurity = true;
        }

        return new InstanceOptions
        {
            Name = r.InstanceKey,
            DisplayName = r.DisplayName,
            ConnectionString = builder.ConnectionString,
            IsDefault = r.IsDefault,
            RequiresLogin = r.RequiresLogin,
            AutoDiscoverAgReplicas = r.AutoDiscoverAgReplicas,
            ReplicaHostOverrides = replicaHosts.Map,
            ReplicaAddresses = replicaHosts.Addresses,
            SlackAlertsEnabled = r.SlackAlertsEnabled
        };
    }

    /// <summary>
    /// Replika adres alanını ayrıştırır. İKİ biçim de kabul edilir:
    ///
    ///   "10.0.0.16;10.0.0.17"                    -> düz adres listesi (basit yol)
    ///   "SQLNODE2=10.0.0.16;SQLNODE3=10.0.0.17"  -> ad -> adres eşlemesi
    ///
    /// Düz liste kullanıcı için çok daha kolay: hangi adın hangi makine
    /// olduğunu bilmek zorunda değil, çünkü bağlandığımızda sunucu zaten
    /// kendi adını söylüyor. Ad eşlemesi biçimi, daha önce böyle
    /// kaydetmiş kurulumlar bozulmasın diye korunuyor.
    ///
    /// Bozuk/boş parçalar sessizce atlanır - tek bir yazım hatası tüm
    /// listeyi geçersiz kılmamalı.
    /// </summary>
    private static (Dictionary<string, string> Map, List<string> Addresses) ParseReplicaHosts(string? raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new List<string>();

        if (string.IsNullOrWhiteSpace(raw)) return (map, addresses);

        foreach (var piece in raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = piece.Trim();
            if (entry.Length == 0) continue;

            if (entry.Contains('='))
            {
                var parts = entry.Split('=', 2);
                var host = parts[0].Trim();
                var address = parts[1].Trim();
                if (host.Length == 0 || address.Length == 0) continue;

                map[host] = address;
                addresses.Add(address);          // eşlemedeki adres de doğrudan kullanılabilir
            }
            else
            {
                addresses.Add(entry);
            }
        }

        return (map, addresses);
    }

    /// <summary>
    /// Ekle/güncelle. isNew=false ve Password boş bırakılmışsa mevcut
    /// şifre KORUNUR (kullanıcı her düzenlemede şifreyi yeniden yazmak
    /// zorunda kalmasın) - RequiresLogin=true'ya geçilirse bu kural
    /// bilerek BYPASS edilir, kayıtlı şifre varsa temizlenir (bkz. aşağı).
    /// </summary>
    public async Task<(bool Ok, string? Error)> AddOrUpdateAsync(InstanceRecord r, bool isNew, CancellationToken ct)
    {
        if (!IsManaged)
            return (false, "Sunucu listesi bu ortamda yönetilemiyor (izleme veritabanına ulaşılamıyor).");

        if (!IsValidKey(r.InstanceKey))
            return (false, "Anahtar yalnızca harf, rakam, _ ve - içerebilir (en fazla 64 karakter).");

        if (string.IsNullOrWhiteSpace(r.Server))
            return (false, "Sunucu adresi gerekli.");

        await _lock.WaitAsync(ct);
        try
        {
            // Kayıttan ÖNCEKİ hâli - aşağıda "kimlik bilgisini silmek
            // gerekiyor mu" kararı için lazım (bkz. CredentialsAffectedBy).
            var existing = _records.FirstOrDefault(x =>
                string.Equals(x.InstanceKey, r.InstanceKey, StringComparison.OrdinalIgnoreCase));

            await using var conn = _factory.CreateStoreConnection();
            await conn.OpenAsync(ct);

            if (isNew)
            {
                var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM mon.MonitoredInstance WHERE InstanceKey = @Key",
                    new { Key = r.InstanceKey }, cancellationToken: ct));

                if (exists > 0) return (false, $"'{r.InstanceKey}' anahtarıyla bir sunucu zaten var.");
            }

            if (r.IsDefault)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE mon.MonitoredInstance SET IsDefault = 0 WHERE InstanceKey <> @Key",
                    new { Key = r.InstanceKey }, cancellationToken: ct));
            }

            // RequiresLogin=true ise kimlik bilgisi hiç saklanmaz, varsa
            // temizlenir. false ise: yeni bir şifre girilmişse onu yaz,
            // boş bırakılmışsa (düzenlemede) mevcut şifreyi koru.
            var userId = r.RequiresLogin ? null : NullIfBlank(r.UserId);
            var passwordProvided = r.RequiresLogin || !string.IsNullOrWhiteSpace(r.Password);
            var password = r.RequiresLogin ? null : NullIfBlank(r.Password);

            const string upsert = """
                MERGE mon.MonitoredInstance AS target
                USING (SELECT @Key AS InstanceKey) AS src
                  ON target.InstanceKey = src.InstanceKey
                WHEN MATCHED THEN UPDATE SET
                    DisplayName  = @DisplayName,
                    Server       = @Server,
                    DatabaseName = @Database,
                    UserId       = @UserId,
                    Password     = CASE WHEN @PasswordProvided = 1 THEN @Password ELSE target.Password END,
                    RequiresLogin = @RequiresLogin,
                    IsDefault    = @IsDefault,
                    AutoDiscoverAgReplicas = @AutoDiscover,
                    ReplicaHostOverrides = @ReplicaHostOverrides,
                    SlackAlertsEnabled = @SlackAlertsEnabled
                WHEN NOT MATCHED THEN INSERT
                    (InstanceKey, DisplayName, Server, DatabaseName, UserId, Password, RequiresLogin, IsDefault, AutoDiscoverAgReplicas, ReplicaHostOverrides, SlackAlertsEnabled)
                    VALUES (@Key, @DisplayName, @Server, @Database, @UserId, @Password, @RequiresLogin, @IsDefault, @AutoDiscover, @ReplicaHostOverrides, @SlackAlertsEnabled);
                """;

            await conn.ExecuteAsync(new CommandDefinition(upsert, new
            {
                Key = r.InstanceKey,
                r.DisplayName,
                r.Server,
                Database = string.IsNullOrWhiteSpace(r.DatabaseName) ? "master" : r.DatabaseName,
                UserId = userId,
                Password = password,
                PasswordProvided = passwordProvided,
                r.RequiresLogin,
                r.IsDefault,
                AutoDiscover = r.AutoDiscoverAgReplicas,
                ReplicaHostOverrides = NullIfBlank(r.ReplicaHostOverrides),
                r.SlackAlertsEnabled
            }, cancellationToken: ct));

            // Sunucu bilgisi ya da giriş kuralı değiştiyse eski çalışma
            // zamanı kimlik bilgisi artık geçersiz/yanlış hedefe ait
            // olabilir - kilidi geri alıyoruz (hem bellek-içi hem kalıcı
            // hatırlanmış olanı), gerekiyorsa yeniden girer.
            //
            // AMA yalnızca GERÇEKTEN bağlantıyı etkileyen bir alan
            // değiştiyse. Eskiden her kayıt bunu koşulsuz yapıyordu:
            // görünen adı düzeltmek ya da "Slack'e bildir" kutucuğunu
            // işaretlemek bile kullanıcıyı o sunucuya yeniden giriş
            // yapmaya zorluyordu - kimlik bilgisiyle hiçbir ilgisi olmayan
            // bir değişiklik için gereksiz bir ceza.
            if (existing is null || CredentialsAffectedBy(existing, r, passwordProvided))
            {
                _credentials.Clear(r.InstanceKey);
                await _rememberedCredentials.ForgetAsync(r.InstanceKey, ct);
            }

            await ReloadCoreAsync(conn, ct);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string instanceKey, CancellationToken ct)
    {
        if (!IsManaged) return;

        await _lock.WaitAsync(ct);
        try
        {
            await using var conn = _factory.CreateStoreConnection();
            await conn.OpenAsync(ct);

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM mon.MonitoredInstance WHERE InstanceKey = @Key",
                new { Key = instanceKey }, cancellationToken: ct));

            _credentials.Clear(instanceKey);
            await _rememberedCredentials.ForgetAsync(instanceKey, ct);
            await ReloadCoreAsync(conn, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Kayıtlı (hatırlanan) girişi geçersiz kılan bir değişiklik mi?
    ///
    /// Yalnızca bağlantının KİME/NEREYE açıldığını ya da kimliğin nasıl
    /// verildiğini değiştiren alanlar sayılır. Görünen ad, varsayılan
    /// işareti, AG keşfi, replika eşlemesi ve Slack anahtarı bu listede
    /// BİLEREK yok - hiçbiri mevcut kullanıcı adı/şifreyi geçersiz kılmaz.
    /// </summary>
    private static bool CredentialsAffectedBy(InstanceRecord before, InstanceRecord after, bool passwordProvided)
        => !string.Equals(before.Server, after.Server, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(before.DatabaseName, after.DatabaseName, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(before.UserId ?? "", after.UserId ?? "", StringComparison.OrdinalIgnoreCase)
        || before.RequiresLogin != after.RequiresLogin
        || (passwordProvided && !after.RequiresLogin);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Anahtar aynı zamanda ?instance= sorgu parametresi ve mon.Instance.Name
    /// olarak da kullanılıyor - StoredProcedureService'teki LooksLikeIdentifier
    /// ile aynı sıkı disiplin, sürpriz karakterlerin sorunlu bir yere sızmasını önler.
    /// </summary>
    private static bool IsValidKey(string s) =>
        !string.IsNullOrWhiteSpace(s) && s.Length <= 64 &&
        s.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
}
