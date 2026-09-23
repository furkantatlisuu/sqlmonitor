using System.Diagnostics;
using Microsoft.Extensions.Options;
using SqlMonitor.Models;
using SqlMonitor.Options;

namespace SqlMonitor.Services;

/// <summary>
/// Arka plan toplayıcı.
///
/// Bu servis olmadan uygulama yalnızca bir "DMV görüntüleyici" olurdu:
/// o an ne olduğunu gösterir, ama "dün gece 03:00'te ne oldu" sorusuna
/// cevap veremez. Trend grafikleri, olay zaman çizelgesi ve bekleme
/// farkı hesabının üçü de buradaki periyodik yazmadan besleniyor.
///
/// Tarayıcı açık olmasa bile çalışır - izlemenin sürekliliği kullanıcının
/// sekmeyi açık tutmasına bağlı olmamalı.
/// </summary>
public sealed class CollectorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MonitorOptions _options;
    private readonly InstanceRegistry _instances;
    private readonly ProdCredentialStore _credentials;
    private readonly SlackNotifier _slack;
    private readonly SlackDebouncer _slackDebounce;
    private readonly ILogger<CollectorService> _log;

    private DateTime _lastPurgeUtc = DateTime.MinValue;

    public CollectorService(
        IServiceScopeFactory scopeFactory,
        IOptions<MonitorOptions> options,
        InstanceRegistry instances,
        ProdCredentialStore credentials,
        SlackNotifier slack,
        SlackDebouncer slackDebounce,
        ILogger<CollectorService> log)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _instances = instances;
        _credentials = credentials;
        _slack = slack;
        _slackDebounce = slackDebounce;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CollectLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // NORMAL KAPANIŞ - hata değil.
            //
            // Kapanış sinyali geldiğinde Task.Delay ve
            // PeriodicTimer.WaitForNextTickAsync iptal istisnası fırlatır.
            // Yakalanmazsa .NET bunu "BackgroundService failed" diye ERROR,
            // ardından "IHost instance is stopping" diye KRİTİK olarak
            // günlüğe yazıyor - uygulamayı her kapattığında günlükte iki
            // korkutucu satır. Günlük dosyaya yazılmaya başlandıktan sonra
            // bu gürültü doğrudan kullanıcının karşısına çıkıyor, o yüzden
            // kaynağında susturuluyor.
            _log.LogInformation("Toplayıcı durduruldu.");
        }
    }

    private async Task CollectLoopAsync(CancellationToken stoppingToken)
    {
        // Uygulama daha ayağa kalkarken izlenen sunucuya yüklenmeyelim.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.CollectIntervalSeconds));
        _log.LogInformation("Toplayıcı başladı, aralık {Seconds} sn", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        do
        {
            // _instances.Instances CANLI bir özellik - InstanceRegistry
            // her ekle/düzenle/sil sonrası kendi önbelleğini günceller,
            // bu yüzden ekrandan yapılan bir değişiklik uygulamayı
            // yeniden başlatmadan bir SONRAKİ turda kendiliğinden yansır.
            foreach (var instance in _instances.Instances)
            {
                if (stoppingToken.IsCancellationRequested) break;

                // RequiresLogin=true ve kimse henüz ekrandan doğru kullanıcı
                // adı/şifreyi girmediyse bu instance'a dokunacak hiçbir
                // kimlik bilgimiz yok - denemek zaten başarısız olur.
                // Sessizce atlıyoruz; her turda "toplama başarısız" hatası
                // basmak (kilit açılana kadar 30 saniyede bir) gürültüden
                // başka bir şey katmaz. Kilit açılınca bir sonraki turda
                // kendiliğinden toplamaya başlar.
                if (instance.RequiresLogin && !_credentials.IsUnlocked(instance.Name))
                    continue;

                try
                {
                    await CollectOnceAsync(instance, stoppingToken);
                }
                catch (Exception ex)
                {
                    // Bir instance patlarsa diğerlerini toplamaya devam et.
                    // Toplayıcının tamamen durması, izlemenin kör kalması demek.
                    _log.LogError(ex, "{Instance} için toplama başarısız", instance.Name);
                }
            }

            await MaybePurgeAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CollectOnceAsync(InstanceOptions instance, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        using var scope = _scopeFactory.CreateScope();
        var live = scope.ServiceProvider.GetRequiredService<LiveMonitorService>();
        var store = scope.ServiceProvider.GetRequiredService<MetricStore>();

        var instanceId = await store.EnsureInstanceAsync(instance.Name, instance.DisplayName, ct);

        try
        {
            var snapshot = await live.GetSnapshotAsync(instance, ct, scope: "collector");

            await store.SaveMetricsAsync(instanceId, startedAt, ExtractMetrics(snapshot), ct);
            await SaveWaitsAsync(live, store, instanceId, startedAt, instance, ct);
            await RecordEventsAsync(store, instance, instanceId, snapshot, ct);

            sw.Stop();
            await store.LogCollectorRunAsync(
                instanceId, startedAt, (int)sw.ElapsedMilliseconds, true, null, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await store.LogCollectorRunAsync(
                instanceId, startedAt, (int)sw.ElapsedMilliseconds, false, ex.ToString(), ct);
            throw;
        }
    }

    /// <summary>
    /// Snapshot'tan zaman serisine yazılacak sayıları çıkarır.
    ///
    /// MetricKey'ler KPI'ların Key değerleriyle bilerek aynı: frontend
    /// sparkline'ı çekerken ekstra eşleme tablosuna ihtiyaç duymuyor.
    /// </summary>
    private static Dictionary<string, decimal> ExtractMetrics(LiveSnapshot s)
    {
        var metrics = new Dictionary<string, decimal>();

        foreach (var kpi in s.Kpis)
            if (kpi.Value.HasValue)
                metrics[kpi.Key] = kpi.Value.Value;

        metrics["tempdb_used_pct"] = s.TempDb.UsedPercent;
        metrics["tempdb_version_store_mb"] = s.TempDb.VersionStoreMb;
        metrics["runnable_tasks"] = s.Cpu.RunnableTasks;
        metrics["running_requests"] = s.Activity.RunningRequests;
        metrics["open_transactions"] = s.Activity.OpenTransactions;
        metrics["worst_write_ms"] = s.Io.WorstWriteMs;
        metrics["worst_read_ms"] = s.Io.WorstReadMs;
        metrics["health_score"] = s.Health.Score;

        var fullest = s.Disk.Volumes.FirstOrDefault();
        if (fullest is not null)
            metrics["disk_used_pct"] = fullest.UsedPercent;

        return metrics;
    }

    /// <summary>
    /// Bekleme istatistiklerinin HAM (kümülatif) hâlini saklar.
    ///
    /// Snapshot'taki değerler zaten fark alınmış durumda; onları saklamak
    /// işe yaramaz. Fark hesabının bir sonraki turda da yapılabilmesi için
    /// ham değerlere ihtiyacımız var - o yüzden ayrı bir okuma yapıyoruz.
    /// dm_os_wait_stats ucuz bir DMV, ikinci okuma sorun değil.
    /// </summary>
    private static async Task SaveWaitsAsync(
        LiveMonitorService live, MetricStore store, int instanceId,
        DateTime capturedAt, InstanceOptions instance, CancellationToken ct)
    {
        var raw = await live.ReadRawWaitsAsync(instance, ct);
        await store.SaveWaitSampleAsync(instanceId, capturedAt, raw, ct);
    }

    /// <summary>
    /// Kural durumları değiştiyse olay yazar (zaman çizelgesi buradan
    /// doluyor, HER turda anında - gecikme yok) ve Critical'e giren/çıkan
    /// geçişleri Slack'e bildirir.
    ///
    /// Slack kararı BİLEREK RecordEventIfChangedAsync'in dönüşünden
    /// (mon.HealthEvent'e yeni satır yazılıp yazılmadığından) ayrı:
    /// SlackDebouncer kendi "üst üste kaç tur Critical" sayacını tutuyor
    /// (bkz. kendi notu) - bir kontrol 2 turdur Critical'deyse mon.HealthEvent
    /// artık "değişmedi" der (ilk turda zaten yazılmıştı) ama Slack'in asıl
    /// karar anı TAM O ikinci tur. İkisini aynı sinyale bağlasaydık,
    /// debounce eşiği aşıldığı anda bildirim hiç gitmeyebilirdi.
    /// </summary>
    private async Task RecordEventsAsync(
        MetricStore store, InstanceOptions instance, int instanceId,
        LiveSnapshot snapshot, CancellationToken ct)
    {
        foreach (var check in snapshot.Health.Checks)
        {
            if (check.Severity == Severity.Unknown) continue;

            var previous = await store.RecordEventIfChangedAsync(
                instanceId, check.Key, check.Severity,
                check.Question, check.Finding,
                MetricStore.SerializeEvidence(check.Evidence), ct);

            // Slack, instance başına AÇIK/KAPALI - global SlackWebhookUrl
            // tek başına yeterli değil, kullanıcı hangi sunucunun Critical'inin
            // oraya gideceğini "Sunucuları yönet"ten ayrıca seçiyor (bkz.
            // InstanceOptions.SlackAlertsEnabled). Kapalıyken debounce
            // sayacını da işletmiyoruz - hiç alarma dönüşmeyecek bir sayaç
            // tutmanın anlamı yok.
            if (!instance.SlackAlertsEnabled) continue;

            var (becameCritical, resolved) = _slackDebounce.Evaluate(
                $"{instance.Name}::{check.Key}", check.Severity);

            if (becameCritical)
                await _slack.NotifyTransitionAsync(instance, check, previous ?? Severity.Healthy, ct);
            else if (resolved)
                await _slack.NotifyTransitionAsync(instance, check, previous ?? Severity.Critical, ct);
        }
    }

    /// <summary>Günde bir kez eski veriyi temizle.</summary>
    private async Task MaybePurgeAsync(CancellationToken ct)
    {
        if ((DateTime.UtcNow - _lastPurgeUtc).TotalHours < 24) return;

        _lastPurgeUtc = DateTime.UtcNow;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<MetricStore>();
            await store.PurgeAsync(ct);
            _log.LogInformation("Eski izleme verisi temizlendi");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Temizlik başarısız");
        }
    }
}
