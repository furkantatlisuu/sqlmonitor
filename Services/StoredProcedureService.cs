using Dapper;
using Microsoft.Extensions.Options;
using SqlMonitor.Infrastructure;
using SqlMonitor.Models;
using SqlMonitor.Options;
using SqlMonitor.Sql;

namespace SqlMonitor.Services;

/// <summary>
/// "Stored Procedure" sekmesi: sys.dm_exec_procedure_stats üzerinden en
/// ağır prosedürleri listeler.
///
/// Index Analizi'yle AYNI disiplin: snapshot'ın (saniyelik döngü)
/// DIŞINDA, sekme açıldığında ya da "yenile"ye basıldığında çalışır. Bu
/// DMV ifade değil PROSEDÜR bazında toplar, ve prosedür tanımını
/// TAMAMEN sql_handle üzerinden çeker - sunucu-çapında (server-scoped)
/// bir mekanizma, DmvSql.QueryPlanByHandle paylaşılıyor.
/// </summary>
public sealed class StoredProcedureService
{
    private readonly SqlConnectionFactory _factory;
    private readonly MonitorOptions _options;

    /// <summary>
    /// GÜVENLİK: SQL'e giden ORDER BY metni HER ZAMAN bu sabit sözlükten
    /// gelir, asla istekten gelen sortKey'in kendisi değil - En Yoğun
    /// Sorgular'daki aynı yapısal koruma.
    /// </summary>
    private static readonly Dictionary<string, string> SortExpressions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"]           = "ps.total_worker_time",
        ["avgcpu"]        = "(ps.total_worker_time / ps.execution_count)",
        ["avgduration"]   = "(ps.total_elapsed_time / ps.execution_count)",
        ["duration"]      = "ps.total_elapsed_time",
        ["calls"]         = "ps.execution_count",
        ["reads"]         = "ps.total_logical_reads",
        ["writes"]        = "ps.total_logical_writes",
        ["avgreads"]      = "(ps.total_logical_reads / ps.execution_count)",
        ["avgwrites"]     = "(ps.total_logical_writes / ps.execution_count)",
        ["lastexecution"] = "ps.last_execution_time"
    };

    /// <summary>
    /// Aynı sıralama anahtarlarının PostgreSQL karşılığı. Ekran her iki
    /// motorda da aynı sütun başlıklarını gösteriyor, arkadaki ifade
    /// değişiyor.
    ///
    /// PostgreSQL'de "süre" ve "CPU" AYNI sütundur (total_exec_time):
    /// motor sorgu başına CPU'yu ayrı ölçmez, yalnızca geçen süreyi
    /// tutar. İkisini farklıymış gibi göstermek yanlış olurdu; ikisi de
    /// aynı gerçeği gösteriyor.
    /// </summary>
    private static readonly Dictionary<string, string> PgSortExpressions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"]           = "s.total_exec_time",
        ["avgcpu"]        = "(s.total_exec_time / greatest(s.calls, 1))",
        ["avgduration"]   = "(s.total_exec_time / greatest(s.calls, 1))",
        ["duration"]      = "s.total_exec_time",
        ["calls"]         = "s.calls",
        ["reads"]         = "(s.shared_blks_hit + s.shared_blks_read)",
        ["writes"]        = "s.shared_blks_written",
        ["avgreads"]      = "((s.shared_blks_hit + s.shared_blks_read) / greatest(s.calls, 1))",
        ["avgwrites"]     = "(s.shared_blks_written / greatest(s.calls, 1))",
        ["lastexecution"] = "s.total_exec_time"   // PostgreSQL son çalışma zamanını tutmaz
    };

    public static bool IsValidSortKey(string? key) => key is not null && SortExpressions.ContainsKey(key);

    public StoredProcedureService(SqlConnectionFactory factory, IOptions<MonitorOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    public async Task<TopProceduresResult> GetTopProceduresAsync(
        InstanceOptions instance, string sortKey, bool descending, int top, CancellationToken ct)
    {
        top = Math.Clamp(top, 1, 200);

        if (DbEngine.IsPostgres(instance.Engine))
            return await GetPostgresTopAsync(instance, sortKey, descending, top, ct);

        if (!SortExpressions.TryGetValue(sortKey, out var orderBy))
            throw new ArgumentException($"Geçersiz sıralama anahtarı: {sortKey}");

        var sql = DmvSql.TopProceduresTemplate
                   .Replace("{ORDER_BY}", orderBy)
                   .Replace("{DIRECTION}", descending ? "DESC" : "ASC")
                   + "\n" + DmvSql.ProcedureCacheTotals;

        await using var conn = _factory.CreateTargetConnection(instance);
        await conn.OpenAsync(ct);

        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
            sql, new { Top = top },
            commandTimeout: Math.Max(_options.QueryTimeoutSeconds, 15),
            cancellationToken: ct));

        var rows = (await multi.ReadAsync<TopProcedureRow>()).ToList();
        var totals = await multi.ReadSingleAsync<TotalsRow>();

        var listedCpu = rows.Sum(r => r.TotalCpuMs);
        var listedDuration = rows.Sum(r => r.TotalDurationMs);

        foreach (var r in rows)
            r.DurationSharePercent = listedDuration > 0
                ? Math.Round(r.TotalDurationMs * 100m / listedDuration, 1)
                : 0;

        return new TopProceduresResult
        {
            SortKey = sortKey,
            SortDescending = descending,
            CachedProcCount = totals.ProcCount,
            TotalCallsAllCache = totals.TotalCalls,
            TotalCpuMsAllCache = totals.TotalCpuMs,
            TotalDurationMsAllCache = totals.TotalDurationMs,
            ListedTotalCpuMs = listedCpu,
            ListedTotalDurationMs = listedDuration,
            Rows = rows
        };
    }

    /// <summary>
    /// PostgreSQL'in "en yoğun sorgular"ı - pg_stat_statements'tan.
    ///
    /// Sonuç SQL Server'la AYNI şekilde dönüyor ki ekran tarafında
    /// ikinci bir tablo yazmak gerekmesin. İki gerçek fark var ve
    /// ikisi de uydurulmuyor, olduğu gibi aktarılıyor:
    ///
    /// - CPU ve süre aynı sayı (bkz. PgSortExpressions): PostgreSQL
    ///   sorgu başına CPU'yu ayrı ölçmez.
    /// - "Son çalışma" bilgisi yok; pg_stat_statements böyle bir sütun
    ///   tutmuyor. Ekranda o sütun boş kalır.
    /// </summary>
    private async Task<TopProceduresResult> GetPostgresTopAsync(
        InstanceOptions instance, string sortKey, bool descending, int top, CancellationToken ct)
    {
        if (!PgSortExpressions.TryGetValue(sortKey, out var orderBy))
            throw new ArgumentException($"Geçersiz sıralama anahtarı: {sortKey}");

        var sql = PgSql.TopStatementsTemplate
                   .Replace("{ORDER_BY}", orderBy)
                   .Replace("{DIRECTION}", descending ? "DESC" : "ASC");

        await using var conn = _factory.CreateTargetDbConnection(instance);
        await conn.OpenAsync(ct);

        var rows = (await conn.QueryAsync<TopProcedureRow>(new CommandDefinition(
            sql, new { Top = top },
            commandTimeout: Math.Max(_options.QueryTimeoutSeconds, 15),
            cancellationToken: ct))).ToList();

        var totals = await conn.QuerySingleAsync<TotalsRow>(new CommandDefinition(
            PgSql.StatementTotals,
            commandTimeout: Math.Max(_options.QueryTimeoutSeconds, 15),
            cancellationToken: ct));

        var listedCpu = rows.Sum(r => r.TotalCpuMs);
        var listedDuration = rows.Sum(r => r.TotalDurationMs);

        foreach (var r in rows)
            r.DurationSharePercent = listedDuration > 0
                ? Math.Round(r.TotalDurationMs * 100m / listedDuration, 1)
                : 0;

        return new TopProceduresResult
        {
            SortKey = sortKey,
            SortDescending = descending,
            CachedProcCount = totals.ProcCount,
            TotalCallsAllCache = totals.TotalCalls,
            TotalCpuMsAllCache = totals.TotalCpuMs,
            TotalDurationMsAllCache = totals.TotalDurationMs,
            ListedTotalCpuMs = listedCpu,
            ListedTotalDurationMs = listedDuration,
            Rows = rows
        };
    }

    /// <summary>
    /// Bir prosedürün tam CREATE PROCEDURE metni. sql_handle server-scoped
    /// olduğu için prosedürün hangi veritabanında olduğu önemli değil -
    /// aynı sorgu (DmvSql.QueryPlanByHandle) canlı oturum planında da
    /// kullanılıyor, burada yalnızca plan XML'i görmezden geliyoruz -
    /// bir prosedürün "planı" tek bir şey değildir (içindeki her ifadenin
    /// kendi planı vardır), tanımın kendisi asıl değerli olan.
    /// </summary>
    public async Task<ProcedureDefinitionDetail?> GetDefinitionAsync(
        InstanceOptions instance, string sqlHandleHex, string planHandleHex, CancellationToken ct)
    {
        if (!LooksLikeHandleHex(sqlHandleHex) || !LooksLikeHandleHex(planHandleHex))
            throw new ArgumentException("Geçersiz handle biçimi.");

        await using var conn = _factory.CreateTargetConnection(instance);
        await conn.OpenAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync<PlanTextRow>(new CommandDefinition(
            DmvSql.QueryPlanByHandle,
            new { SqlHandle = sqlHandleHex, PlanHandle = planHandleHex },
            commandTimeout: Math.Max(_options.QueryTimeoutSeconds, 15),
            cancellationToken: ct));

        if (row is null || string.IsNullOrEmpty(row.SqlText))
            return null;   // Plan artık cache'te değil - tahliye olmuş.

        return new ProcedureDefinitionDetail { Definition = row.SqlText };
    }

    /// <summary>
    /// Plan cache'te (VE varsa Query Store'da) hiç görünmeyen prosedürler.
    ///
    /// DİKKAT: "listede olmak" = "kullanılmıyor" değil, "kullanıldığına
    /// dair kanıt bulamadık" demektir - DmvSql.UnusedProceduresScan'deki
    /// uzun açıklamaya bak. UnusedProcedureDbStatus.QueryStoreEnabled=false
    /// olan bir veritabanının satırları zayıf kanıta dayanır, arayüz bunu
    /// mutlaka göstermeli.
    /// </summary>
    public async Task<UnusedProceduresResult> GetUnusedProceduresAsync(
        InstanceOptions instance, int top, CancellationToken ct)
    {
        top = Math.Clamp(top, 1, 500);

        await using var conn = _factory.CreateTargetConnection(instance);
        await conn.OpenAsync(ct);

        // Dinamik SQL + geçici tablolar bir dize üzerinde döngü içeriyor -
        // taban DMV sorgularından daha pahalı, ama canlıda ölçüldü: 3
        // veritabanı / ~1900 prosedür için ~0.5 sn. Cömert bir zaman aşımı
        // yine de mantıklı, çok daha büyük bir sunucuda çalışabilir.
        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
            DmvSql.UnusedProceduresScan, new { Top = top },
            commandTimeout: Math.Max(_options.QueryTimeoutSeconds, 30),
            cancellationToken: ct));

        var rows = (await multi.ReadAsync<UnusedProcedureRow>()).ToList();
        var dbStatuses = (await multi.ReadAsync<UnusedProcedureDbStatus>()).ToList();

        return new UnusedProceduresResult { Databases = dbStatuses, Rows = rows };
    }

    /// <summary>
    /// Kullanılmayan bir prosedürün tanımı. GetDefinitionAsync'ten (sql_handle
    /// ile) FARKLI bir yol izler çünkü bu prosedürlerin, tanımı gereği,
    /// bir handle'ı YOK - hiç cache'e girmediler. Bunun yerine hedef
    /// veritabanının kendi sys.sql_modules'una doğrudan bakıyoruz.
    ///
    /// GÜVENLİK: database/schema/proc üçü de tarayıcıdan gelen ham metin -
    /// database dinamik SQL'e QUOTENAME ile gömülüyor (üç parçalı isimde
    /// parametre olamaz), bu yüzden burada SIKI bir tanımlayıcı doğrulaması
    /// şart - aksi halde dinamik SQL enjeksiyonuna açık kapı olur.
    /// </summary>
    public async Task<ProcedureDefinitionDetail?> GetProcedureSourceAsync(
        InstanceOptions instance, string database, string schema, string procName, CancellationToken ct)
    {
        if (!LooksLikeIdentifier(database) || !LooksLikeIdentifier(schema) || !LooksLikeIdentifier(procName))
            throw new ArgumentException("Geçersiz veritabanı/şema/prosedür adı.");

        await using var conn = _factory.CreateTargetConnection(instance);
        await conn.OpenAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync<DefinitionRow>(new CommandDefinition(
            DmvSql.ProcedureSourceByName,
            new { Database = database, Schema = schema, Proc = procName },
            commandTimeout: Math.Max(_options.QueryTimeoutSeconds, 15),
            cancellationToken: ct));

        if (row is null || string.IsNullOrEmpty(row.Definition))
            return null;   // Bu arada silinmiş olabilir - tarama ile tıklama arasında zaman geçer.

        return new ProcedureDefinitionDetail { Definition = row.Definition };
    }

    /// <summary>
    /// SQL Server tanımlayıcı kurallarının basitleştirilmiş, KATI bir alt
    /// kümesi - süslü parantez/özel karakter isimlere ("weird db") bilerek
    /// izin vermiyoruz. Reddedilen bir isim varsa kullanıcı olağan dışı bir
    /// veritabanı adıyla karşılaşmış demektir, bu metod onu "kullanılmayan
    /// prosedür" akışında görmez - kabul edilebilir bir bedel, çünkü asıl
    /// risk (dinamik SQL enjeksiyonu) çok daha ağır.
    /// </summary>
    private static bool LooksLikeIdentifier(string s) =>
        !string.IsNullOrWhiteSpace(s) && s.Length <= 128 &&
        s.All(c => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@');

    private sealed class DefinitionRow
    {
        public string Definition { get; set; } = "";
    }

    private static bool LooksLikeHandleHex(string s) =>
        s.Length is > 2 and < 700 && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        s.Skip(2).All(Uri.IsHexDigit);

    private sealed class TotalsRow
    {
        public int ProcCount { get; set; }
        public long TotalCalls { get; set; }
        public decimal TotalCpuMs { get; set; }
        public decimal TotalDurationMs { get; set; }
    }

    private sealed class PlanTextRow
    {
        public string SqlText { get; set; } = "";
        public string PlanXml { get; set; } = "";
    }
}
