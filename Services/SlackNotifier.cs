using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SqlMonitor.Models;
using SqlMonitor.Options;

namespace SqlMonitor.Services;

/// <summary>
/// Slack Incoming Webhook'una kritik durum bildirimi gönderir.
///
/// SADECE Critical'e giren/çıkan geçişlerde çağrılır (bkz.
/// CollectorService.RecordEventsAsync) - her tur, her kontrol için değil.
/// Bu sınıf kendisi hiçbir eşik/geçiş mantığı taşımıyor, yalnızca "şunu
/// Slack'e yaz" der; ne zaman çağrılacağına çağıran karar verir.
///
/// SlackWebhookUrl boşsa (varsayılan, appsettings.json paylaşılırken
/// boş kalır) sessizce hiçbir şey yapmaz - Slack kurulmamış bir
/// ortamda bildirim özelliğinin "kapalı" hali budur, ayrı bir
/// enable/disable ayarına gerek yok.
///
/// Webhook isteği ASLA toplayıcıyı düşürmemeli - her hata burada
/// yutulup loglanır, CollectorService'in geri kalanı çalışmaya devam eder.
/// </summary>
public sealed class SlackNotifier
{
    private readonly HttpClient _http;
    private readonly MonitorOptions _options;
    private readonly AppSettingStore _settings;
    private readonly ILogger<SlackNotifier> _log;

    public SlackNotifier(
        HttpClient http, IOptions<MonitorOptions> options,
        AppSettingStore settings, ILogger<SlackNotifier> log)
    {
        _http = http;
        _options = options.Value;
        _settings = settings;
        _log = log;
    }

    /// <summary>
    /// Webhook adresi ÖNCE ekrandan girilip veritabanına kaydedilmiş
    /// olandan okunur; yoksa appsettings.json/ortam değişkenine düşülür.
    ///
    /// Bu sıra bilinçli: ekrandan girilen değer hiçbir dosyada görünmez
    /// (depoya ve paylaşılan zip'e sızmaz), dosya yolu ise sunucuya
    /// kurulumlarda ortam değişkeniyle vermek isteyenler için duruyor.
    /// </summary>
    private string? WebhookUrl
    {
        get
        {
            var fromDb = _settings.Get(AppSettingStore.SlackWebhookUrl);
            return !string.IsNullOrWhiteSpace(fromDb) ? fromDb : _options.SlackWebhookUrl;
        }
    }

    /// <summary>Arayüzün "Slack yapılandırılmış mı" sorusuna cevabı.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(WebhookUrl);

    // Kırmızı/yeşil - Slack'in kendi renk paletiyle uyumlu (Slack'in resmi
    // marka renkleri #E01E5A/#2EB67D değil, klasik alarm kırmızısı/yeşili
    // tercih edildi - PagerDuty/Datadog gibi izleme araçlarının alışılmış
    // renkleri, sağlıklı/kritik ayrımı ilk bakışta tanınsın).
    private const string CriticalColor = "#DC3545";
    private const string ResolvedColor = "#2EB67D";

    public async Task NotifyTransitionAsync(
        InstanceOptions instance, HealthCheck check, Severity previous, CancellationToken ct)
    {
        var webhook = WebhookUrl;
        if (string.IsNullOrWhiteSpace(webhook)) return;

        var becameCritical = check.Severity == Severity.Critical;
        var displayName = instance.DisplayName ?? instance.Name;

        // DİKKAT: Slack'in webhook'u "blocks" alanını attachments İÇİNDE
        // KABUL ETMİYOR - canlı denendi, HTTP 400 "invalid_attachments"
        // döndü. Bu yüzden Block Kit değil, her Incoming Webhook'ta
        // çalışan KLASİK attachment alanları (title/text/fields) kullanılıyor -
        // daha az esnek ama evrensel olarak destekleniyor.
        var fields = new List<object>
        {
            new { title = "Sunucu", value = Esc(displayName), @short = true }
        };

        if (becameCritical)
            fields.Add(new { title = "Kategori", value = Esc(check.Category), @short = true });
        else
            fields.Add(new { title = "Durum değişimi", value = $"{SeverityLabel(previous)} → {SeverityLabel(check.Severity)}", @short = true });

        if (becameCritical && check.Remedy is not null)
            fields.Add(new { title = "Öneri", value = Esc(check.Remedy), @short = false });

        var payload = new
        {
            attachments = new object[]
            {
                new
                {
                    color = becameCritical ? CriticalColor : ResolvedColor,
                    title = becameCritical
                        ? $":red_circle: KRİTİK — {Esc(check.Question)}"
                        : $":white_check_mark: Düzeldi — {Esc(check.Question)}",
                    text = Esc(check.Finding),
                    fields,
                    footer = "SqlMonitor",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    mrkdwn_in = new[] { "text", "fields" }
                }
            }
        };

        try
        {
            var res = await _http.PostAsJsonAsync(webhook, payload, ct);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                _log.LogWarning(
                    "Slack bildirimi reddedildi: {Status} {Body} ({Instance}/{Key})",
                    res.StatusCode, body, instance.Name, check.Key);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Slack bildirimi gönderilemedi ({Instance}/{Key})", instance.Name, check.Key);
        }
    }

    /// <summary>
    /// Ayar ekranındaki "Test mesajı gönder" düğmesi. Normal bildirimlerin
    /// aksine hatayı YUTMAZ - kullanıcı adresi yeni girdi, çalışmadıysa
    /// sebebini görmesi gerekiyor.
    /// </summary>
    public async Task<(bool Ok, string? Error)> SendTestAsync(CancellationToken ct)
    {
        var webhook = WebhookUrl;
        if (string.IsNullOrWhiteSpace(webhook)) return (false, "Webhook adresi tanımlı değil.");

        var payload = new
        {
            attachments = new object[]
            {
                new
                {
                    color = ResolvedColor,
                    title = ":white_check_mark: SQL İzleme — test mesajı",
                    text = "Slack bağlantısı çalışıyor. Bu mesaj ayar ekranındaki " +
                           "\"Test mesajı gönder\" düğmesiyle gönderildi.",
                    footer = "SqlMonitor",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }
            }
        };

        try
        {
            var res = await _http.PostAsJsonAsync(webhook, payload, ct);
            if (res.IsSuccessStatusCode) return (true, null);

            var body = (await res.Content.ReadAsStringAsync(ct)).Trim();
            return (false, $"Slack reddetti: {(int)res.StatusCode} {body}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string SeverityLabel(Severity s) => s switch
    {
        Severity.Healthy => "sağlıklı",
        Severity.Warning => "uyarı",
        Severity.Critical => "kritik",
        _ => "bilinmiyor"
    };

    // Slack mrkdwn'de *, _, ~, `, &, <, > özel anlam taşıyor - en azından
    // &/</> kaçırılmazsa bulgu metnindeki bir "<" karakteri (örn. bir SQL
    // ifadesi) mesajı bozabilir.
    private static string Esc(string? text) => (text ?? "")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
