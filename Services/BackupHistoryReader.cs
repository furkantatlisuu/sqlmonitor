using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlMonitor.Infrastructure;
using SqlMonitor.Options;
using SqlMonitor.Sql;

namespace SqlMonitor.Services;

/// <summary>
/// Diğer Always On replikalarındaki yedek geçmişini okur.
///
/// Var olma sebebi tek bir SQL Server gerçeği: msdb.dbo.backupset
/// YEREL bir tablodur. Availability Group veritabanını replikalar
/// arasında senkronize eder ama msdb'yi ETMEZ. Yedek işi 3. makinede
/// koşuyorsa, primary'nin msdb'sinde o yedeğin hiçbir izi olmaz.
///
/// Sonuç, izleme açısından en kötü hata türüdür: yedekler pekâlâ
/// alınırken ekran "hiç FULL yedeği yok" der. Bu sınıf eksik yarıyı
/// diğer replikalardan getirir; birleştirmeyi HealthEvaluator yapar.
///
/// Singleton'dır, çünkü önbelleği tutar - aşağıya bak.
/// </summary>
public sealed class BackupHistoryReader
{
    private readonly SqlConnectionFactory _factory;
    private readonly MonitorOptions _options;
    private readonly ILogger<BackupHistoryReader> _log;

    // Instance adı -> son okunan sonuç.
    //
    // Önbellek şart: toplayıcı 30 saniyede bir çalışıyor ve arayüz de
    // ayrıca istek atıyor. Önbelleksiz her turda 2 uzak makineye bağlanıp
    // msdb tarardık. Yedek geçmişi ise saatlik değişen bir veri - 2
    // dakikalık bayatlık burada tamamen zararsız.
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public BackupHistoryReader(
        SqlConnectionFactory factory,
        IOptions<MonitorOptions> options,
        ILogger<BackupHistoryReader> log)
    {
        _factory = factory;
        _options = options.Value;
        _log = log;
    }

    /// <param name="localConn">
    /// İzlenen instance'a açık bağlantı. Replika keşfi bunun üzerinden
    /// yapılır; yeni bağlantı açmaya gerek yok.
    /// </param>
    public async Task<RemoteBackupHistory> ReadAsync(
        InstanceOptions instance, SqlConnection localConn, CancellationToken ct)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(0, _options.BackupHistoryCacheSeconds));

        if (_cache.TryGetValue(instance.Name, out var cached) &&
            DateTime.UtcNow - cached.ReadAtUtc < ttl)
        {
            return cached.Value;
        }

        var result = await ReadUncachedAsync(instance, localConn, ct);
        _cache[instance.Name] = new CacheEntry(DateTime.UtcNow, result);
        return result;
    }

    private async Task<RemoteBackupHistory> ReadUncachedAsync(
        InstanceOptions instance, SqlConnection localConn, CancellationToken ct)
    {
        var result = new RemoteBackupHistory();

        List<BackupSourceOptions> sources;
        try
        {
            sources = await ResolveSourcesAsync(instance, localConn, ct);
        }
        catch (Exception ex)
        {
            // Keşif başarısızsa panel yine de çalışsın; sadece yerel yedek
            // geçmişiyle hüküm verilir ve sebebi bulguya not düşülür.
            _log.LogWarning(ex, "AG replika keşfi başarısız: {Instance}", instance.Name);
            result.Failures.Add($"replika keşfi: {ex.Message}");
            return result;
        }

        // Yerel sunucunun adı - elle verilen adres listesinde kendisi de
        // varsa atlayabilmek için (aşağıya bak).
        string? localServerName = null;
        try
        {
            localServerName = await localConn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName'));",
                commandTimeout: _options.QueryTimeoutSeconds, cancellationToken: ct));
        }
        catch { /* okunamazsa yalnızca "kendini atlama" özelliği çalışmaz */ }

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            var label = string.IsNullOrWhiteSpace(source.Name)
                ? SqlConnectionFactory.ServerNameOf(source.ConnectionString)
                : source.Name;

            try
            {
                await using var conn = _factory.CreateBackupSourceConnection(instance, source);
                await conn.OpenAsync(ct);

                // Bağlandığımız makinenin KENDİ adını soruyoruz. Kullanıcı
                // yalnızca IP yazdıysa etiket "10.0.0.17" olarak kalırdı;
                // bulguda "yedek 10.0.0.17 üzerinde bulundu" yerine
                // "TestSQL3 üzerinde bulundu" demek çok daha anlaşılır.
                var remoteName = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                    "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName'));",
                    commandTimeout: _options.QueryTimeoutSeconds, cancellationToken: ct));

                if (!string.IsNullOrWhiteSpace(remoteName))
                {
                    // Elle verilen adreslerin arasında yerel sunucunun kendisi
                    // de olabilir (kullanıcı üç düğümün IP'sini birden
                    // yapıştırırsa). Onu atlıyoruz: yerel yedek geçmişi zaten
                    // ana sorgudan geliyor, ikinci kez okumak boşuna yük.
                    if (string.Equals(remoteName, localServerName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    label = remoteName;
                }

                var rows = await conn.QueryAsync<HistoryRow>(new CommandDefinition(
                    DmvSql.BackupHistory,
                    commandTimeout: _options.QueryTimeoutSeconds,
                    cancellationToken: ct));

                foreach (var row in rows)
                {
                    if (string.IsNullOrWhiteSpace(row.DatabaseName)) continue;
                    result.Merge(row.DatabaseName, row.LastFullBackup, row.LastLogBackup, label);
                }

                result.SourcesRead.Add(label);
            }
            catch (Exception ex)
            {
                // Ulaşılamayan bir replika kontrolü çökertmez. Ama sessizce
                // yutmak da olmaz: kullanıcı, "yedek yok" bulgusunun eksik
                // veriden mi geldiğini bilmek zorunda.
                _log.LogWarning(ex, "Yedek geçmişi okunamadı: {Source}", label);
                result.Failures.Add($"{label}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Hangi makinelere bakılacağını belirler - AutoDiscoverAgReplicas
    /// kapalıysa ya da AG yapılandırılmamışsa boş küme döner.
    /// </summary>
    private async Task<List<BackupSourceOptions>> ResolveSourcesAsync(
        InstanceOptions instance, SqlConnection localConn, CancellationToken ct)
    {
        if (!instance.AutoDiscoverAgReplicas)
            return new List<BackupSourceOptions>();

        // Kullanıcı replika adreslerini ELLE verdiyse keşfe hiç gitmiyoruz.
        //
        // Sebebi canlıda ölçüldü: bu ağda ne replikaların kısa adı
        // (TestSQL2) ne de SQL Server'ın bildiği tam alan adı
        // (TestSQL2.nilveratest.com) DNS'ten çözülüyor. Adresleri elle
        // veren bir kullanıcıya rağmen keşfedilen ADLARLA bağlanmayı
        // denemek yalnızca gecikme ve "okunamadı" hatası üretirdi.
        //
        // Hangi adresin hangi sunucu olduğunu bilmemize gerek yok:
        // bağlanınca sunucu kendi adını söylüyor (bkz. ReadUncachedAsync).
        if (instance.ReplicaAddresses.Count > 0)
        {
            return instance.ReplicaAddresses
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(address => new BackupSourceOptions
                {
                    Name = address,          // geçici etiket; gerçek ad bağlanınca okunuyor
                    ConnectionString = _factory.RebindServer(instance, address)
                })
                .ToList();
        }

        var replicas = (await localConn.QueryAsync<ReplicaRow>(new CommandDefinition(
            DmvSql.AgReplicas, cancellationToken: ct))).ToList();

        return replicas
            .Where(r => !r.IsLocal && !string.IsNullOrWhiteSpace(r.ReplicaServerName))
            .Select(r => new BackupSourceOptions
            {
                Name = r.ReplicaServerName,
                ConnectionString = _factory.RebindServer(instance, r.ReplicaServerName)
            })
            .ToList();
    }

    private sealed record CacheEntry(DateTime ReadAtUtc, RemoteBackupHistory Value);

    private sealed class HistoryRow
    {
        public string DatabaseName { get; set; } = "";
        public DateTime? LastFullBackup { get; set; }
        public DateTime? LastLogBackup { get; set; }
    }

    private sealed class ReplicaRow
    {
        public string ReplicaServerName { get; set; } = "";
        public string RoleDesc { get; set; } = "";
        public bool IsLocal { get; set; }
        public int BackupPriority { get; set; }
        public string GroupName { get; set; } = "";
    }
}

/// <summary>
/// Uzak replikalardan toplanan yedek geçmişi: veritabanı adı -> en yeni
/// tarihler ve o tarihi veren makinenin adı.
/// </summary>
public sealed class RemoteBackupHistory
{
    public Dictionary<string, RemoteBackupEntry> ByDatabase { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Başarıyla okunan kaynaklar - bulguda "nerede bulundu" demek için.</summary>
    public List<string> SourcesRead { get; } = new();

    /// <summary>Okunamayan kaynaklar ve sebepleri.</summary>
    public List<string> Failures { get; } = new();

    public bool HasAnySource => SourcesRead.Count > 0;

    /// <summary>
    /// Aynı veritabanı birden çok replikada görünebilir (dün primary'de,
    /// bugün secondary'de alınmış olabilir). Her zaman EN YENİ tarihi
    /// tutuyoruz: cevabını aradığımız soru "en son ne zaman yedeklendi",
    /// "nerede yedeklendi" değil.
    /// </summary>
    public void Merge(string database, DateTime? full, DateTime? log, string source)
    {
        if (!ByDatabase.TryGetValue(database, out var entry))
        {
            entry = new RemoteBackupEntry();
            ByDatabase[database] = entry;
        }

        if (full is not null && (entry.LastFullBackup is null || full > entry.LastFullBackup))
        {
            entry.LastFullBackup = full;
            entry.FullBackupSource = source;
        }

        if (log is not null && (entry.LastLogBackup is null || log > entry.LastLogBackup))
        {
            entry.LastLogBackup = log;
            entry.LogBackupSource = source;
        }
    }
}

public sealed class RemoteBackupEntry
{
    public DateTime? LastFullBackup { get; set; }
    public DateTime? LastLogBackup { get; set; }
    public string? FullBackupSource { get; set; }
    public string? LogBackupSource { get; set; }
}
