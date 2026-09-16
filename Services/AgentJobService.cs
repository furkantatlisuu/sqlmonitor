using Dapper;
using Microsoft.Extensions.Options;
using SqlMonitor.Infrastructure;
using SqlMonitor.Models;
using SqlMonitor.Options;
using SqlMonitor.Sql;

namespace SqlMonitor.Services;

/// <summary>
/// "Agent Jobs" sekmesi: SQL Server Agent job'larının son çalışma durumu.
///
/// Index Analizi/Stored Procedure/Always On ile AYNI disiplin: saniyelik
/// snapshot döngüsünün DIŞINDA, sekme açıldığında ya da "yenile"ye
/// basıldığında çalışır - msdb.dbo.sysjobhistory büyük bir sunucuda
/// yüz binlerce satır olabilir, her saniye taramak gereksiz yük olurdu.
///
/// İzleme hesabının msdb'de db_datareader olması yeterli (02_monitoring_
/// login.sql zaten yedek geçmişi için bunu veriyordu) - ek yetki gerekmez.
/// </summary>
public sealed class AgentJobService
{
    private readonly SqlConnectionFactory _factory;
    private readonly MonitorOptions _options;

    public AgentJobService(SqlConnectionFactory factory, IOptions<MonitorOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    public async Task<AgentJobsResult> GetJobsAsync(InstanceOptions instance, CancellationToken ct)
    {
        await using var conn = _factory.CreateTargetConnection(instance);
        await conn.OpenAsync(ct);

        var jobs = (await conn.QueryAsync<AgentJobRow>(new CommandDefinition(
            DmvSql.AgentJobStatus,
            commandTimeout: Math.Max(_options.QueryTimeoutSeconds, 15),
            cancellationToken: ct))).ToList();

        return new AgentJobsResult
        {
            Jobs = jobs,
            FailedCount = jobs.Count(j => j.LastRunOutcome == 0),
            RunningCount = jobs.Count(j => j.IsCurrentlyRunning),
            DisabledCount = jobs.Count(j => !j.IsEnabled)
        };
    }
}
