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

    private sealed record UpdateInstanceRequest(
        string? DisplayName, string Server, string? DatabaseName,
        string? UserId, string? Password, bool RequiresLogin, bool IsDefault, bool AutoDiscoverAgReplicas,
        string? ReplicaHostOverrides, bool SlackAlertsEnabled);
}
