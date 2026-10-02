using System.Collections.Concurrent;

namespace SqlMonitor.Services;

/// <summary>
/// "PLE düşük ama TOPARLIYOR mu, yoksa TAKILI mı?"
///
/// Page life expectancy bir YAŞ değil, bir ORANDIR: havuzdan saniyede
/// kaç sayfa atıldığına göre hesaplanır. Bunun ölçülebilir bir sonucu
/// var: hiç sayfa atılmıyorsa PLE saniyede TAM 1 artar. Gerçek PROD
/// verisinde birebir böyle görüldü - 160, 171, 202, 232, 262, 291 ...
/// 30 saniyede +30.
///
/// Yani düşük bir PLE iki ÇOK FARKLI durumu anlatabiliyor:
///
///   saniyede +1 artıyor -> havuzdan hiçbir şey atılmıyor, baskı YOK.
///                          Düşük sayı geçmiş bir olayın kalıntısı;
///                          sistem şu anda sapasağlam.
///   düz/düşüyor         -> sayfalar atılmaya devam ediyor, baskı GERÇEK.
///
/// Bu ayrımı yapmadan alarm üretmek, tek seferlik bir rapor sorgusundan
/// sonra sistem çoktan iyileşmişken 4-5 dakika boyunca "KRİTİK" diye
/// bağırmak demek - kullanıcının Slack'te yaşadığı tam olarak buydu.
/// Eşiği yükseltmek ya da streak'i uzatmak bunu çözmez: ikisi de
/// "ne kadar süredir düşük" sorar, oysa doğru soru "hâlâ kötüleşiyor mu".
///
/// CounterDeltaTracker/StreakTracker gibi Singleton olmalı - onu tüketen
/// LiveMonitorService Scoped, önceki okuma orada yaşayamaz.
/// </summary>
public sealed class PleTrendTracker
{
    private readonly ConcurrentDictionary<string, (DateTime At, long Ple)> _last = new();

    /// <summary>
    /// Hiç sayfa atılmıyorsa PLE saniyede 1 artar. %80'lik pay ölçüm
    /// gürültüsü ve yuvarlama içindir - toplayıcı tam 30,000 ms'de bir
    /// çalışmıyor, sayaç da tam sayı.
    /// </summary>
    private const double RecoveryRatio = 0.8;

    /// <summary>
    /// Bu okumayı kaydeder ve "havuz toparlıyor mu" sorusuna cevap verir.
    ///
    /// İLK okumada her zaman false döner: karşılaştıracak bir şey yok,
    /// "toparlıyor" demek tahmin olurdu. Bu, ilk turda sahte bir
    /// "sağlıklı" üretmemek için bilinçli - emin olmadığımızda alarmı
    /// susturmuyoruz.
    /// </summary>
    public bool ObserveAndIsRecovering(string scope, string instanceName, long ple, DateTime nowUtc)
    {
        var key = $"{scope}:{instanceName}";
        var vardi = _last.TryGetValue(key, out var onceki);
        _last[key] = (nowUtc, ple);

        if (!vardi) return false;

        var saniye = (nowUtc - onceki.At).TotalSeconds;

        // Çok kısa aralıkta PLE sayacı değişmemiş olabilir (ekran saniyede
        // bir yeniliyor, sayaç saniyede bir artıyor) - ölçüm anlamsız.
        // Çok uzun aralıkta ise arada ne olduğunu bilmiyoruz; toplayıcı
        // durmuş olabilir, "toparlıyor" demek yanlış olur.
        if (saniye < 5 || saniye > 300) return false;

        return (ple - onceki.Ple) >= saniye * RecoveryRatio;
    }
}
