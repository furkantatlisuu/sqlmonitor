using Dapper;
using Microsoft.Extensions.Options;
using SqlMonitor.Infrastructure;
using SqlMonitor.Models;
using SqlMonitor.Options;
using SqlMonitor.Sql;

namespace SqlMonitor.Services;

/// <summary>
/// "Index Analizi" sekmesi.
///
/// Şimdilik yalnızca eksik index önerileri (sys.dm_db_missing_index_*).
/// Diğer alt sekmeler (kullanılmayan index, bayat istatistik,
/// fragmantasyon) ayrı turlarda buraya eklenecek - dördü de aynı
/// "sunucudaki index sağlığı" hikâyesinin parçası, tek bir servis
/// altında toplanmaları mantıklı.
///
/// BİLEREK snapshot'a (saniyelik döngüye) dahil değil: kullanıcı sekmeyi
/// açtığında ya da "yenile"ye bastığında çalışır. Sorgu zaten ucuz olsa
/// da (~0.3 sn, CROSS APPLY yok), eksik index önerileri dakikalar
/// içinde önemli ölçüde değişmez - saniyelik tazelik burada hiçbir şey
/// kazandırmaz, yalnızca izlenen sunucuyu boşuna yorar.
/// </summary>
public sealed class IndexAnalysisService
{
    private readonly SqlConnectionFactory _factory;
    private readonly MonitorOptions _options;

    public IndexAnalysisService(SqlConnectionFactory factory, IOptions<MonitorOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    public async Task<MissingIndexResult> GetMissingIndexesAsync(
        InstanceOptions instance, int top, CancellationToken ct)
    {
        top = Math.Clamp(top, 1, 500);

        await using var conn = _factory.CreateTargetConnection(instance);
        await conn.OpenAsync(ct);

        var rows = (await conn.QueryAsync<DbRow>(new CommandDefinition(
            DmvSql.MissingIndexSuggestions, new { Top = top },
            commandTimeout: _options.QueryTimeoutSeconds, cancellationToken: ct))).ToList();

        return new MissingIndexResult
        {
            Rows = rows.Select(ToMissingIndexRow).ToList()
        };
    }

    /// <summary>
    /// DMV'nin döndürdüğü "[Kolon1], [Kolon2]" dizesini CREATE INDEX'e
    /// çevirir. equality_columns/inequality_columns/included_columns zaten
    /// SQL Server'ın kendi ürettiği, sözdizimsel olarak geçerli bir liste -
    /// burada yapılan tek şey bunları birleştirip bir CREATE INDEX
    /// gövdesine yerleştirmek. İsim üretimi için parantezleri tek tek
    /// temizlemek gerekiyor (SplitBracketColumns), gövde için gerekmiyor.
    /// </summary>
    private static MissingIndexRow ToMissingIndexRow(DbRow r)
    {
        var row = new MissingIndexRow
        {
            DatabaseName = r.DatabaseName ?? "",
            SchemaName = r.SchemaName ?? "",
            TableName = r.TableName ?? "",
            EqualityColumns = r.EqualityColumns ?? "",
            InequalityColumns = r.InequalityColumns ?? "",
            IncludeColumns = r.IncludeColumns ?? "",
            Seeks = r.Seeks,
            Scans = r.Scans,
            AvgTotalUserCost = r.AvgTotalUserCost,
            AvgUserImpact = r.AvgUserImpact,
            Impact = r.Impact,
            LastUserSeek = r.LastUserSeek,
            LastUserScan = r.LastUserScan,
            UniqueCompiles = r.UniqueCompiles
        };

        // Gövde için DMV'nin kendi bracketed dizesini AYNEN kullanıyoruz -
        // "[TaxNumber], [InvoiceType]" zaten geçerli bir CREATE INDEX
        // kolon listesi, yeniden üretmenin bir faydası yok. Parantezleri
        // yalnızca İSİM üretirken (SplitBracketColumns) temizliyoruz,
        // çünkü "IX_Tablo_[Kolon]" geçersiz bir tanımlayıcı olurdu.
        var keyColumnsRaw = string.Join(", ", new[] { row.EqualityColumns, row.InequalityColumns }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        if (keyColumnsRaw.Length > 0)
        {
            var nameParts = SplitBracketColumns(row.EqualityColumns)
                .Concat(SplitBracketColumns(row.InequalityColumns));
            var name = $"IX_{row.TableName}_{string.Join('_', nameParts)}";

            var script = $"CREATE INDEX {name} ON {row.SchemaName}.{row.TableName} ({keyColumnsRaw})";
            if (!string.IsNullOrWhiteSpace(row.IncludeColumns))
                script += $" INCLUDE ({row.IncludeColumns})";
            script += ";";

            row.SuggestedCreateIndex = script;
        }

        return row;
    }

    /// <summary>"[A], [B]" -> ["A", "B"]. Parantezsiz/boşsa boş liste.</summary>
    private static List<string> SplitBracketColumns(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new List<string>()
            : raw.Split(", ", StringSplitOptions.RemoveEmptyEntries)
                 .Select(c => c.Trim('[', ']'))
                 .ToList();

    private sealed class DbRow
    {
        public string? DatabaseName { get; set; }
        public string? SchemaName { get; set; }
        public string? TableName { get; set; }
        public string? EqualityColumns { get; set; }
        public string? InequalityColumns { get; set; }
        public string? IncludeColumns { get; set; }
        public long Seeks { get; set; }
        public long Scans { get; set; }
        public decimal AvgTotalUserCost { get; set; }
        public decimal AvgUserImpact { get; set; }
        public decimal Impact { get; set; }
        public DateTime? LastUserSeek { get; set; }
        public DateTime? LastUserScan { get; set; }
        public int UniqueCompiles { get; set; }
    }
}
