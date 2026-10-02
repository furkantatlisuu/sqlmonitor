using System.Collections.Concurrent;
using SqlMonitor.Models;

namespace SqlMonitor.Services;

/// <summary>
/// Slack'e giden Critical bildirimlerini flapping'e karşı korur.
///
/// Ekrandaki rozet (ve mon.HealthEvent tarihçesi) HER turda anında
/// güncellenir - biri o an bakıyorsa güncel durumu görmeli, tarihçe de
/// gerçek olanı kaydetmeli. Ama Slack'e mesaj SADECE bir kontrol arka
/// arkaya RequiredCriticalStreak tur (30 sn'lik toplayıcı aralığında
/// varsayılan 2 tur = ~1 dk) Critical KALIRSA gider - CPU/disk/bloklama
/// gibi eşiğin tam sınırında salınabilen metrikler her 30 saniyede bir
/// Critical↔Warning arası gidip gelirse, io_read/io_write'ın kendi
/// StreakTracker korumasının AKSİNE bu ikisi dışındaki 14 kontrolün hiçbiri
/// böyle bir korumaya sahip değildi - burası o boşluğu Slack'e özel,
/// tüm kontrollere EŞİT şekilde kapatıyor.
///
/// "Düzeldi" mesajı yalnızca GERÇEKTEN alarm verilmiş (streak eşiği
/// aşılmış) bir kritik dönem için gönderilir - eşiğe hiç ulaşmadan kendi
/// kendine geçen bir sıçrama için "düzeldi" demek, hiç haber verilmemiş
/// bir şeyin "bittiğini" söylemek olur, kafa karıştırır.
///
/// StreakTracker'daki genel "üst üste kaç kez" sayacından BİLEREK ayrı:
/// burada ayrıca "bu bölüm için gerçekten alarm verildi mi" bilgisi de
/// tutulmalı (resolved kararı için), StreakTracker'ın tek sorumluluğu
/// (yalnızca sayaç) bunu karşılamıyor.
/// </summary>
public sealed class SlackDebouncer
{
    private const int DefaultCriticalStreak = 2;

    /// <summary>
    /// Bazı kontroller için 2 tur (~1 dk) YETERSİZ. Bellek bunun örneği:
    ///
    /// PLE bir yaş değil, bir orandır. Tek bir ağır rapor sorgusu
    /// havuzdan birkaç GB geçirdiğinde PLE anında dibe vurur, iş bitince
    /// de saniyede 1 artarak kendiliğinden geri tırmanır. Gerçek PROD
    /// olayında PLE 4121'den 160'a düştü, 2 dakika orada kaldı, sonra
    /// tırmanıp 5. dakikada eşiğin üstüne çıktı. Bu bir arıza değil, bir
    /// raporun maliyeti - ama 2 turluk eşik Slack'e "KRİTİK" yolladı.
    ///
    /// 10 tur (~5 dk) bu ayrımı yapıyor: kendi kendine geçen bir çukur
    /// sessiz kalır, GERÇEK bellek baskısı (dakikalarca düşük kalan PLE)
    /// yine bildirilir. Eşiği yükseltmek ya da kontrolü kapatmak yerine
    /// bunu seçtik - ikisi de gerçek baskıyı da görünmez yapardı.
    /// </summary>
    private static readonly Dictionary<string, int> StreakByCheck = new(StringComparer.OrdinalIgnoreCase)
    {
        ["memory"] = 10
    };

    private readonly ConcurrentDictionary<string, int> _streaks = new();
    private readonly ConcurrentDictionary<string, bool> _alerted = new();

    /// <summary>Anahtar "INSTANCE::kontrol" biçiminde geliyor (bkz. CollectorService).</summary>
    private static int RequiredStreak(string key)
    {
        var i = key.LastIndexOf("::", StringComparison.Ordinal);
        var check = i >= 0 ? key[(i + 2)..] : key;
        return StreakByCheck.TryGetValue(check, out var n) ? n : DefaultCriticalStreak;
    }

    public (bool BecameCritical, bool Resolved) Evaluate(string key, Severity current)
    {
        var isCritical = current == Severity.Critical;
        var streak = _streaks.AddOrUpdate(key, isCritical ? 1 : 0, (_, prev) => isCritical ? prev + 1 : 0);

        if (isCritical)
        {
            // "==" bilerek ">=" değil: eşik bir kez aşıldıktan sonra streak
            // büyümeye devam ederken her turda tekrar tetiklenmesin diye.
            if (streak == RequiredStreak(key))
            {
                _alerted[key] = true;
                return (true, false);
            }
            return (false, false);
        }

        if (_alerted.TryRemove(key, out var wasAlerted) && wasAlerted)
            return (false, true);

        return (false, false);
    }
}
