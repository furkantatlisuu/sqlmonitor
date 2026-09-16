namespace SqlMonitor.Models;

// ------------------------------------------------------------------
// Identity kolonu taşma - bkz. Services/IdentityColumnScanner.cs,
// DmvSql.IdentityColumnUsage.
// ------------------------------------------------------------------

public sealed class IdentityColumnRow
{
    public string DatabaseName { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public string TableName { get; set; } = "";
    public string ColumnName { get; set; } = "";
    public string TypeName { get; set; } = "";
    public long LastValue { get; set; }
    public long MaxValue { get; set; }
    public decimal UsedPercent { get; set; }
}

// ------------------------------------------------------------------
// Bekleme türlerinde zaman içi trend - bkz. Services/MetricStore.cs
// GetWaitTrendAsync, mon.WaitSample.
// ------------------------------------------------------------------

public sealed class WaitTrendResult
{
    public TrendWindow Trend { get; set; } = new();
    public List<WaitTrendSeries> Series { get; set; } = new();
}

public sealed class WaitTrendSeries
{
    public string WaitType { get; set; } = "";

    /// <summary>Örnek başına ms/sn - KPI trend dizileriyle AYNI şekil, aynı sparkline() JS fonksiyonu çiziyor.</summary>
    public List<decimal> Values { get; set; } = new();
}

// ------------------------------------------------------------------
// Disk doluluk tahmini - bkz. Services/MetricStore.cs GetDiskForecastAsync.
// ------------------------------------------------------------------

/// <summary>
/// Geçmiş disk_used_pct örneklerinden basit doğrusal (OLS) projeksiyon.
/// HasEnoughHistory=false ise (3 günden az geçmiş) diğer alanlar
/// anlamsızdır - kullanıcıya tahmin yerine "henüz yeterli geçmiş yok" gösterilmeli.
/// </summary>
public sealed class DiskForecast
{
    public bool HasEnoughHistory { get; set; }
    public int HistoryDays { get; set; }
    public decimal? CurrentPercent { get; set; }

    /// <summary>Günde yüzde kaç puan değişiyor - pozitif = doluyor, negatif/sıfır = büyüme yok.</summary>
    public decimal? SlopePerDayPercent { get; set; }

    /// <summary>Bu hızla giderse kaç güne %100'e ulaşır - büyüme yoksa ya da zaten çok yavaşsa null.</summary>
    public int? DaysToFull { get; set; }
}
