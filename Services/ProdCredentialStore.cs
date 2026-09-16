using System.Collections.Concurrent;

namespace SqlMonitor.Services;

/// <summary>
/// Çalışma zamanında girilen "canlı" (RequiresLogin=true) instance kimlik
/// bilgilerini bellekte tutar.
///
/// appsettings.json'da SAKLANMAZ - kullanıcı ekrandan girer, gerçek bir
/// bağlantı denemesiyle doğrulanır (bkz. SqlConnectionFactory.
/// VerifyCredentialsAsync), başarılıysa burada tutulur. Uygulama tek bir
/// süreç/dahili bir araç olarak çalıştığından bu depoyu tarayıcı oturumu
/// başına değil, SÜREÇ başına tek bir "kilit açık/kapalı" durumu olarak
/// tasarladık - kim açtıysa aynı makinedeki aynı süreci kullanan herkes
/// görür, tıpkı fiziksel bir anahtarın kilidi açması gibi. Uygulama
/// yeniden başlayınca ya da "çıkış yap" ile temizlenir.
/// </summary>
public sealed class ProdCredentialStore
{
    private sealed record Entry(string UserId, string Password);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public bool IsUnlocked(string instanceName) => _entries.ContainsKey(instanceName);

    public void Set(string instanceName, string userId, string password)
        => _entries[instanceName] = new Entry(userId, password);

    public void Clear(string instanceName) => _entries.TryRemove(instanceName, out _);

    public bool TryGet(string instanceName, out string userId, out string password)
    {
        if (_entries.TryGetValue(instanceName, out var e))
        {
            userId = e.UserId;
            password = e.Password;
            return true;
        }

        userId = "";
        password = "";
        return false;
    }
}
