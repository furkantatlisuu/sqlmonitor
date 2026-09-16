using System.Collections.Concurrent;
using SqlMonitor.Models;
using SqlMonitor.Options;

namespace SqlMonitor.Services;

/// <summary>
/// HealthEvaluator'ın "alwayson" kontrolü için AlwaysOnService'in TTL'li
/// önbelleğe alınmış hâli - AgentJobHealthCache ile aynı disiplin, aynı
/// sebep.
///
/// AlwaysOnService'in kendisi BİLEREK önbderleksiz kalıyor: Always On
/// sekmesindeki "Yenile" her zaman taze veri getirmeli. Ama sağlık
/// kontrolü AYRI bir yoldan geliyor ve ÇOK daha sık: HealthEvaluator her
/// snapshot'ta çalışıyor, snapshot da ekran açıkken 5 saniyede bir (hatta
/// kullanıcı isterse saniyede bir) yenileniyor. Önbelleksiz hâlde bu,
/// izlenen sunucuya her turda AYRI bir bağlantı + 5 sorgu demekti;
/// canlıda ölçüldü, snapshot süresine ~145 ms ekliyordu (toplayıcı
/// ortalaması 950 ms'den 1200 ms'ye çıkmıştı).
///
/// AG topolojisi ve replika sağlığı saniyeler içinde değişen bir şey
/// değil - 30 saniyelik bayatlık, bir sağlık hükmü için tamamen zararsız,
/// üstelik toplayıcının kendi aralığıyla (varsayılan 30 sn) aynı.
/// </summary>
public sealed class AlwaysOnHealthCache
{
    private readonly AlwaysOnService _alwaysOn;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public AlwaysOnHealthCache(AlwaysOnService alwaysOn)
    {
        _alwaysOn = alwaysOn;
    }

    public async Task<AlwaysOnResult> GetAsync(InstanceOptions instance, CancellationToken ct)
    {
        if (_cache.TryGetValue(instance.Name, out var cached) &&
            DateTime.UtcNow - cached.ReadAtUtc < CacheTtl)
        {
            return cached.Result;
        }

        var result = await _alwaysOn.GetAlwaysOnStatusAsync(instance, ct);
        _cache[instance.Name] = new CacheEntry(DateTime.UtcNow, result);
        return result;
    }

    private sealed record CacheEntry(DateTime ReadAtUtc, AlwaysOnResult Result);
}
