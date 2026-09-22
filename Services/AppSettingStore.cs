using System.Collections.Concurrent;
using Dapper;
using SqlMonitor.Infrastructure;

namespace SqlMonitor.Services;

/// <summary>
/// Ekrandan girilen, DOSYADA tutulmayan ayarlar (mon.AppSetting).
///
/// Bugünkü tek kullanıcısı Slack webhook adresi. appsettings.json'da
/// durduğunda iki sorun vardı: dosya hem depoya hem patrona gönderilen
/// zip'e giriyordu (webhook'u gören herkes o kanala mesaj atabilir),
/// hem de her yayın çıktısında yeniden yazılması gerekiyordu. Burada
/// hiçbir dosyada görünmüyor.
///
/// Değerler bellekte önbelleklenir: Slack bildirimi gönderilirken her
/// seferinde veritabanına gitmek gereksiz - ayar yılda bir değişir.
/// Kayıt/silme işlemleri önbelleği anında günceller, yeniden başlatmaya
/// gerek yok.
///
/// İzleme veritabanı yoksa/erişilemezse sessizce "ayar yok" gibi
/// davranır - uygulamanın geri kalanı çalışmaya devam etmeli.
/// </summary>
public sealed class AppSettingStore
{
    public const string SlackWebhookUrl = "SlackWebhookUrl";

    private readonly SqlConnectionFactory _factory;
    private readonly ILogger<AppSettingStore> _log;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _loaded;

    public AppSettingStore(SqlConnectionFactory factory, ILogger<AppSettingStore> log)
    {
        _factory = factory;
        _log = log;
    }

    /// <summary>Açılışta bir kez çağrılır; başarısız olursa uygulama yine çalışır.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = _factory.CreateStoreConnection();
            await conn.OpenAsync(ct);

            var rows = await conn.QueryAsync<(string Name, string? Value)>(new CommandDefinition(
                "SELECT Name, Value FROM mon.AppSetting", cancellationToken: ct));

            foreach (var (name, value) in rows)
                if (!string.IsNullOrWhiteSpace(value))
                    _cache[name] = value;

            _loaded = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Uygulama ayarları okunamadı - ekrandan girilen ayarlar bu turda geçersiz.");
        }
    }

    public string? Get(string name) => _cache.TryGetValue(name, out var v) ? v : null;

    public bool Has(string name) => !string.IsNullOrWhiteSpace(Get(name));

    public async Task SetAsync(string name, string? value, CancellationToken ct)
    {
        const string sql = """
            MERGE mon.AppSetting AS target
            USING (SELECT @Name AS Name) AS src ON target.Name = src.Name
            WHEN MATCHED THEN UPDATE SET Value = @Value, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Name, Value) VALUES (@Name, @Value);
            """;

        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { Name = name, Value = value }, cancellationToken: ct));

        if (string.IsNullOrWhiteSpace(value)) _cache.TryRemove(name, out _);
        else _cache[name] = value;
    }

    public async Task ClearAsync(string name, CancellationToken ct)
    {
        await using var conn = _factory.CreateStoreConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mon.AppSetting WHERE Name = @Name", new { Name = name }, cancellationToken: ct));

        _cache.TryRemove(name, out _);
    }

    /// <summary>İzleme veritabanından okuma yapılabildi mi - arayüzde "ayar kaydedilemez" uyarısı için.</summary>
    public bool IsAvailable => _loaded;
}
