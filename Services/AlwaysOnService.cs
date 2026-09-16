using Dapper;
using Microsoft.Extensions.Options;
using SqlMonitor.Infrastructure;
using SqlMonitor.Models;
using SqlMonitor.Options;
using SqlMonitor.Sql;

namespace SqlMonitor.Services;

/// <summary>
/// "Always On" sekmesi: AG grubu, replika ve veritabanı bazında senkron
/// sağlığı.
///
/// Index Analizi/Stored Procedure'ün KARDEŞİ, aynı disiplin: saniyelik
/// snapshot döngüsünün DIŞINDA, sekme açıldığında ya
/// da "yenile"ye basıldığında çalışır. Sebebi burada da aynı - bu sekmenin
/// bir önceki hâli (rol bilgisi) bir zamanlar snapshot'ın içindeydi ve
/// ~500ms'lik maliyeti yüzünden çıkarılmıştı (bkz. DmvSql.BackupStatus
/// üstündeki not). Bu servis o notun önerdiği "ayrı, seyrek sorgu" - artık
/// çok daha zengin bir içerikle.
///
/// AG yapılandırılmamış bir sunucuda TÜM sorgular hata vermeden boş küme
/// döner (bkz. DmvSql.AgReplicas üstündeki not, aynı ilke burada da
/// geçerli) - HasAvailabilityGroup bunu ayırt eder.
/// </summary>
public sealed class AlwaysOnService
{
    private readonly SqlConnectionFactory _factory;
    private readonly MonitorOptions _options;

    public AlwaysOnService(SqlConnectionFactory factory, IOptions<MonitorOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    public async Task<AlwaysOnResult> GetAlwaysOnStatusAsync(InstanceOptions instance, CancellationToken ct)
    {
        var sql = string.Join("\n", new[]
        {
            DmvSql.AgGroupHealth,
            DmvSql.AgReplicaHealth,
            DmvSql.AgDatabaseReplicaHealth,
            DmvSql.AgClusterHealth,
            DmvSql.AgClusterMembers
        });

        await using var conn = _factory.CreateTargetConnection(instance);
        await conn.OpenAsync(ct);

        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
            sql, commandTimeout: Math.Max(_options.QueryTimeoutSeconds, 15), cancellationToken: ct));

        var groups = (await multi.ReadAsync<AgGroupInfo>()).ToList();
        var replicas = (await multi.ReadAsync<AgReplicaInfo>()).ToList();
        var databases = (await multi.ReadAsync<AgDatabaseReplicaInfo>()).ToList();
        var cluster = (await multi.ReadAsync<AgClusterInfo>()).FirstOrDefault();
        var clusterMembers = (await multi.ReadAsync<AgClusterMemberInfo>()).ToList();

        return new AlwaysOnResult
        {
            HasAvailabilityGroup = groups.Count > 0,
            Groups = groups,
            Replicas = replicas,
            Databases = databases,
            Cluster = cluster,
            ClusterMembers = clusterMembers
        };
    }
}
