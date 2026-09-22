using SqlMonitor.Models;
using SqlMonitor.Services;

namespace SqlMonitor.Api;

/// <summary>
/// "Sunucuları yönet" ekranı - izlenen sunucu listesini appsettings.json'a
/// hiç dokunmadan ekle/düzenle/sil. Tek doğruluk kaynağı InstanceRegistry
/// (ve onun arkasındaki mon.MonitoredInstance tablosu) - bkz. oradaki
/// üstteki not.
///
/// InstanceKey (anahtar) BİLEREK değiştirilemez bir alan: mon.Instance
/// geçmiş tablosu ve ?instance= sorgu parametresi hep bu anahtara göre
/// çalışıyor - yeniden adlandırma izin verilseydi eski geçmiş sessizce
/// öksüz kalırdı. Farklı bir isim istiyorsan sil, yeni adla yeniden ekle -
/// kullanıcının zaten istediği akış bu ("silip kendi istediğimi vereyim").
/// </summary>
public static class InstanceManagementEndpoints
{
    public static void MapInstanceManagementEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/settings/instances");

        api.MapGet("/", (InstanceRegistry registry) =>
            Results.Ok(new
            {
                managed = registry.IsManaged,
                items = registry.Records.Select(ToSummary)
            }));

        api.MapPost("/", async (NewInstanceRequest body, InstanceRegistry registry, CancellationToken ct) =>
        {
            var record = new InstanceRecord
            {
                InstanceKey = (body.InstanceKey ?? "").Trim(),
                DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim(),
                Server = (body.Server ?? "").Trim(),
                DatabaseName = string.IsNullOrWhiteSpace(body.DatabaseName) ? "master" : body.DatabaseName.Trim(),
                UserId = body.UserId,
                Password = body.Password,
                RequiresLogin = body.RequiresLogin,
                IsDefault = body.IsDefault,
                AutoDiscoverAgReplicas = body.AutoDiscoverAgReplicas,
                ReplicaHostOverrides = body.ReplicaHostOverrides,
                SlackAlertsEnabled = body.SlackAlertsEnabled
            };

            var (ok, error) = await registry.AddOrUpdateAsync(record, isNew: true, ct);
            return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error });
        });

        api.MapPut("/{key}", async (string key, UpdateInstanceRequest body, InstanceRegistry registry, CancellationToken ct) =>
        {
            if (registry.Find(key) is null)
                return Results.NotFound(new { error = $"'{key}' anahtarıyla bir sunucu bulunamadı." });

            var record = new InstanceRecord
            {
                InstanceKey = key,
                DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim(),
                Server = (body.Server ?? "").Trim(),
                DatabaseName = string.IsNullOrWhiteSpace(body.DatabaseName) ? "master" : body.DatabaseName.Trim(),
                UserId = body.UserId,
                Password = body.Password,
                RequiresLogin = body.RequiresLogin,
                IsDefault = body.IsDefault,
                AutoDiscoverAgReplicas = body.AutoDiscoverAgReplicas,
                ReplicaHostOverrides = body.ReplicaHostOverrides,
                SlackAlertsEnabled = body.SlackAlertsEnabled
            };

            var (ok, error) = await registry.AddOrUpdateAsync(record, isNew: false, ct);
            return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error });
        });

        // -------------------------------------------------------------
        // Slack webhook adresi - DOSYADA DEĞİL, izleme veritabanında.
        //
        // Adres hiçbir zaman geri OKUNMAZ (GET yalnızca "kayıtlı mı"
        // der): ekranda göstermenin faydası yok, riski var. Aynı
        // disiplin "Sunucuları yönet"teki şifre alanında da var.
        //
        // Sunucu listesinin ALTINDA değil, YANINDA bir ayar - o yüzden
        // kendi grubunda (/api/settings/slack), instances grubunda değil.
        // -------------------------------------------------------------
        var settingsApi = app.MapGroup("/api/settings");

        settingsApi.MapGet("/slack", (AppSettingStore settings, SlackNotifier slack) =>
            Results.Ok(new
            {
                configured = slack.IsConfigured,
                storable = settings.IsAvailable,
                fromDatabase = settings.Has(AppSettingStore.SlackWebhookUrl)
            }));

        settingsApi.MapPost("/slack", async (
            SlackWebhookRequest body, AppSettingStore settings, CancellationToken ct) =>
        {
            var url = (body.WebhookUrl ?? "").Trim();

            if (url.Length == 0)
            {
                await settings.ClearAsync(AppSettingStore.SlackWebhookUrl, ct);
                return Results.Ok(new { ok = true, configured = false });
            }

            if (!url.StartsWith("https://hooks.slack.com/", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new
                {
                    error = "Adres https://hooks.slack.com/ ile başlamalı. " +
                            "Slack > Apps > Incoming Webhooks ekranından alabilirsin."
                });

            await settings.SetAsync(AppSettingStore.SlackWebhookUrl, url, ct);
            return Results.Ok(new { ok = true, configured = true });
        });

        // Girilen adresin gerçekten çalıştığını kullanıcı ANINDA görsün -
        // "kaydettim ama acaba doğru mu" belirsizliği kalmasın.
        settingsApi.MapPost("/slack/test", async (SlackNotifier slack, CancellationToken ct) =>
        {
            if (!slack.IsConfigured)
                return Results.BadRequest(new { error = "Önce webhook adresini kaydet." });

            var (ok, error) = await slack.SendTestAsync(ct);
            return ok
                ? Results.Ok(new { ok = true })
                : Results.BadRequest(new { error = error ?? "Mesaj gönderilemedi." });
        });

        api.MapDelete("/{key}", async (string key, InstanceRegistry registry, CancellationToken ct) =>
        {
            if (registry.Find(key) is null)
                return Results.NotFound(new { error = $"'{key}' anahtarıyla bir sunucu bulunamadı." });

            await registry.DeleteAsync(key, ct);
            return Results.Ok(new { ok = true });
        });
    }

    private static InstanceSummary ToSummary(InstanceRecord r) => new(
        r.InstanceKey, r.DisplayName, r.Server, r.DatabaseName, r.UserId,
        HasPassword: !string.IsNullOrEmpty(r.Password),
        r.RequiresLogin, r.IsDefault, r.AutoDiscoverAgReplicas, r.ReplicaHostOverrides, r.SlackAlertsEnabled);

    private sealed record NewInstanceRequest(
        string InstanceKey, string? DisplayName, string Server, string? DatabaseName,
        string? UserId, string? Password, bool RequiresLogin, bool IsDefault, bool AutoDiscoverAgReplicas,
        string? ReplicaHostOverrides, bool SlackAlertsEnabled);

    private sealed record SlackWebhookRequest(string? WebhookUrl);

    private sealed record UpdateInstanceRequest(
        string? DisplayName, string Server, string? DatabaseName,
        string? UserId, string? Password, bool RequiresLogin, bool IsDefault, bool AutoDiscoverAgReplicas,
        string? ReplicaHostOverrides, bool SlackAlertsEnabled);
}
