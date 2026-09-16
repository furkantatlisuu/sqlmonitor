using System.Collections.Concurrent;
using SqlMonitor.Models;
using SqlMonitor.Options;

namespace SqlMonitor.Services;

/// <summary>
/// HealthEvaluator'ın "agent_jobs" kontrolü için AgentJobService'in TTL'li
/// önbelleğe alınmış hâli.
///
/// AgentJobService'in kendisi BİLEREK önbelleksiz bırakıldı (Agent Jobs
/// sekmesindeki "Yenile" her zaman taze veri getirmeli) - ama
/// DmvSql.AgentJobStatus, msdb.dbo.sysjobhistory üzerinde job başına
/// pencere fonksiyonu çalıştırıyor (bkz. AgentJobService'in kendi notu:
/// büyük bir sunucuda yüz binlerce satır olabilir). Toplayıcı artık bunu
/// HER instance için 30 saniyede bir çağıracağından (CollectorService →
/// HealthEvaluator), sekmedeki tek seferlik manuel tıklamadan çok daha
/// sürekli bir yük demek - bu yüzden yalnızca sağlık kontrolü tarafı
/// IdentityColumnScanner ile AYNI disiplinle ayrıca önbelleğe alınıyor.
/// Job başarısızlığını birkaç dakika geç görmek, izlenen sunucuyu sürekli
/// yormaktan daha iyi bir takas.
/// </summary>
public sealed class AgentJobHealthCache
{
    private readonly AgentJobService _jobs;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public AgentJobHealthCache(AgentJobService jobs)
    {
        _jobs = jobs;
    }

    public async Task<AgentJobsResult> GetAsync(InstanceOptions instance, CancellationToken ct)
    {
        if (_cache.TryGetValue(instance.Name, out var cached) &&
            DateTime.UtcNow - cached.ReadAtUtc < CacheTtl)
        {
            return cached.Result;
        }

        var result = await _jobs.GetJobsAsync(instance, ct);
        _cache[instance.Name] = new CacheEntry(DateTime.UtcNow, result);
        return result;
    }

    private sealed record CacheEntry(DateTime ReadAtUtc, AgentJobsResult Result);
}
