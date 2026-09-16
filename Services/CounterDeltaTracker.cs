using System.Collections.Concurrent;

namespace SqlMonitor.Services;

/// <summary>
/// SQL Server'ın "/sec" sayaçları aslında saniyelik değildir; açılıştan
/// beri artan sayaçlardır. 838 milyon gibi bir sayı görürsün ve bu sana
/// hiçbir şey söylemez.
///
/// Anlamlı olan, iki okuma arasındaki artışın geçen süreye bölünmesidir.
/// Bu sınıf her sayacın son okunan değerini ve zamanını hafızada tutar,
/// bir sonraki okumada farkı verir.
///
/// İlk okumada null döner - bu doğrudur, henüz oran hesaplanamaz.
/// Ekranda "-" göstermek, uydurma bir 0 göstermekten iyidir.
///
/// Bellekte tutuluyor, dolayısıyla uygulama yeniden başlayınca sıfırlanır.
/// Bu kabul edilebilir: bir örnekleme aralığı sonra tekrar dolar.
/// </summary>
public sealed class CounterDeltaTracker
{
    private readonly record struct Reading(long Value, DateTime AtUtc);

    private readonly ConcurrentDictionary<string, Reading> _last = new();

    /// <summary>
    /// Sayacın saniyelik oranını döndürür. İlk çağrıda veya sayaç
    /// geri gittiyse null.
    /// </summary>
    /// <param name="scope">
    /// Tuketici adi ("ui" veya "collector").
    ///
    /// Bu parametre olmadan gercek bir hata olusuyordu: arayuz 10 saniyede,
    /// toplayici 30 saniyede bir ayni sayaci okuyor ve ayni taban degeri
    /// eziyorlardi. Toplayici arayuzden hemen once calistiginda arayuzun
    /// olcum penceresi 0.1 saniyeye dusuyor ve deger null donuyordu -
    /// ekranda ara sira "-" gorunmesinin sebebi buydu.
    /// Her tuketicinin kendi tabani olmali.
    /// </param>
    public decimal? GetRatePerSecond(
        string scope, string instanceName, string counterKey, long cumulativeValue)
    {
        var key = $"{scope}::{instanceName}::{counterKey}";
        var now = DateTime.UtcNow;
        var current = new Reading(cumulativeValue, now);

        if (!_last.TryGetValue(key, out var previous))
        {
            _last[key] = current;
            return null;
        }

        _last[key] = current;

        var seconds = (decimal)(now - previous.AtUtc).TotalSeconds;

        // Çok kısa aralıkta bölme yapmak saçma sonuç üretir.
        if (seconds < 0.5m) return null;

        var delta = cumulativeValue - previous.Value;

        // Negatif fark = SQL Server yeniden başlamış, sayaç sıfırlanmış.
        // Bu turu atlıyoruz; bir sonraki okumada temiz bir taban olacak.
        if (delta < 0) return null;

        return Math.Round(delta / seconds, 2);
    }

    /// <summary>Kümülatif olmayan sayaçlar için (PLE gibi) - doğrudan değer.</summary>
    public void Remember(string scope, string instanceName, string counterKey, long value)
        => _last[$"{scope}::{instanceName}::{counterKey}"] = new Reading(value, DateTime.UtcNow);
}
