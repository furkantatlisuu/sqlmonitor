using Dapper;
using SqlMonitor.Infrastructure;

namespace SqlMonitor.Services;

/// <summary>
/// RequiresLogin=true bir instance'a ekrandan başarıyla girilen kimlik
/// bilgisinin KALICI hâli - mon.RememberedCredential tablosunda.
///
/// ProdCredentialStore'dan (bellek-içi, süreç ömrüyle sınırlı) FARKI:
/// bu, uygulama yeniden başlasa (F5) bile hatırlar - kullanıcı isteği
/// "bir kere girdiysem bir daha girmemi istemesin". Program.cs açılışta
/// buradan okuyup ProdCredentialStore'u önceden dolduruyor; AuthEndpoints
/// başarılı girişte hem oraya hem buraya yazıyor; "Çıkış yap" ikisini de
/// temizliyor.
///
/// GÜVENLİK NOTU: appsettings.json'daki diğer sunucularla (Test) AYNI
/// disiplin - şifre düz metin saklanıyor. Bu, RequiresLogin'in "ekranda
/// görünsün/düzenlenebilsin" anlamını KORUYOR (mon.MonitoredInstance'taki
/// UserId/Password hâlâ NULL, "Sunucuları yönet" ekranı hâlâ "kayıtlı
/// şifre yok" gösteriyor) ama art arda her yeniden başlatmada yeniden
/// girmeyi GEREKSİZ kılıyor. SqlConnectionFactory bunu bilmiyor bile -
/// yalnızca ProdCredentialStore'a bakıyor, oraya nasıl girdiği (canlı
/// giriş mi, önyükleme mi) onun için fark etmiyor.
/// </summary>
public sealed class RememberedCredentialStore
{
    private readonly SqlConnectionFactory _factory;

    public RememberedCredentialStore(SqlConnectionFactory factory) => _factory = factory;

    public async Task<List<(string InstanceKey, string UserId, string Password)>> LoadAllAsync(CancellationToken ct)
    {
        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            "SELECT InstanceKey, UserId, Password FROM mon.RememberedCredential", cancellationToken: ct));

        return rows.Select(r => (r.InstanceKey, r.UserId, r.Password)).ToList();
    }

    public async Task SaveAsync(string instanceKey, string userId, string password, CancellationToken ct)
    {
        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            MERGE mon.RememberedCredential AS target
            USING (SELECT @Key AS InstanceKey) AS src
              ON target.InstanceKey = src.InstanceKey
            WHEN MATCHED THEN UPDATE SET UserId = @UserId, Password = @Password, SavedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (InstanceKey, UserId, Password) VALUES (@Key, @UserId, @Password);
            """;

        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { Key = instanceKey, UserId = userId, Password = password }, cancellationToken: ct));
    }

    public async Task ForgetAsync(string instanceKey, CancellationToken ct)
    {
        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mon.RememberedCredential WHERE InstanceKey = @Key",
            new { Key = instanceKey }, cancellationToken: ct));
    }

    private sealed class Row
    {
        public string InstanceKey { get; set; } = "";
        public string UserId { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
