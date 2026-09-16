using System.Collections.Concurrent;

namespace SqlMonitor.Services;

/// <summary>
/// "Üst üste kaç örneklemedir eşiğin üzerinde" sayacı.
///
/// Var olma sebebi: I/O gecikmesi gibi gürültülü metriklerde TEK bir anlık
/// sıçrama alarm üretmemeli - disk bir tek yazma isteğinde 300 ms sürebilir
/// ve bu tamamen normaldir, hiçbir şeyi göstermez. Alarmı SÜREGELEN bir
/// duruma bağlamak (üst üste N örneklem eşiğin üzerinde) gürültüyü elemenin
/// en basit yolu - CounterDeltaTracker'ın "önceki okuma" mantığıyla aynı
/// ruhta, ama o oran hesaplar, bu yalnızca "kaç kez üst üste" sayar.
///
/// CounterDeltaTracker gibi Singleton olmalı - HealthEvaluator/LiveMonitorService
/// Scoped, her istekte yeniden kurulur, sayaç orada YAŞAYAMAZ.
/// </summary>
public sealed class StreakTracker
{
    private readonly ConcurrentDictionary<string, int> _streaks = new();

    /// <summary>
    /// Eşik bu turda aşıldıysa (<paramref name="exceeded"/>=true) sayaç bir
    /// artar, aksi hâlde SIFIRLANIR - "üst üste" kelimesinin anlamı budur,
    /// aradaki bir iyi örneklem zinciri bozar. Güncel (artırılmış/sıfırlanmış)
    /// değeri döner.
    /// </summary>
    public int Bump(string key, bool exceeded)
        => _streaks.AddOrUpdate(key, exceeded ? 1 : 0, (_, prev) => exceeded ? prev + 1 : 0);
}
