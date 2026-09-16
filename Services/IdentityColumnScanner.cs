using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Options;
using SqlMonitor.Infrastructure;
using SqlMonitor.Models;
using SqlMonitor.Options;
using SqlMonitor.Sql;

namespace SqlMonitor.Services;

/// <summary>
/// Sınıra yaklaşan identity kolonlarını tarar ve TTL'li önbelleğe alır.
///
/// DmvSql.IdentityColumnUsage sunucu genelinde bir cursor + dinamik SQL
/// taraması (UnusedProceduresScan ile aynı disiplin) - Index Analizi'nin
/// aksine bunu HealthEvaluator'ın HER turunda (saniyelik döngüde)
/// çalıştırmak izlenen sunucuyu boşuna yorardı. Bunun yerine BackupHistoryReader
/// ile AYNI desen: önbellek süresi dolmadıkça taze bir tarama yapılmaz,
/// HealthEvaluator her seferinde bu servise sorar ama arkadaki gerçek
/// SQL sorgusu yalnızca birkaç dakikada bir çalışır.
///
/// Identity kolonu sayısı/doluluk oranı saatler içinde anlamlı değişmez -
/// 10 dakikalık bir bayatlık burada tamamen zararsız.
/// </summary>
public sealed class IdentityColumnScanner
{
    private readonly SqlConnectionFactory _factory;
    private readonly MonitorOptions _options;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public IdentityColumnScanner(SqlConnectionFactory factory, IOptions<MonitorOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    public async Task<List<IdentityColumnRow>> GetNearLimitAsync(
        InstanceOptions instance, CancellationToken ct)
    {
        if (_cache.TryGetValue(instance.Name, out var cached) &&
            DateTime.UtcNow - cached.ReadAtUtc < CacheTtl)
        {
            return cached.Rows;
        }

        await using var conn = _factory.CreateTargetConnection(instance);
        await conn.OpenAsync(ct);

        var rows = (await conn.QueryAsync<IdentityColumnRow>(new CommandDefinition(
            DmvSql.IdentityColumnUsage,
            commandTimeout: Math.Max(_options.QueryTimeoutSeconds, 30),
            cancellationToken: ct))).ToList();

        _cache[instance.Name] = new CacheEntry(DateTime.UtcNow, rows);
        return rows;
    }

    private sealed record CacheEntry(DateTime ReadAtUtc, List<IdentityColumnRow> Rows);
}
