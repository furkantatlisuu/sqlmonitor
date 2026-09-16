using SqlMonitor.Infrastructure;
using SqlMonitor.Services;

namespace SqlMonitor.Api;

/// <summary>
/// RequiresLogin=true olan instance'lar (bkz. InstanceOptions.RequiresLogin)
/// için çalışma zamanı girişi. appsettings.json'da hiçbir kimlik bilgisi
/// TUTULMAZ - kullanıcı adı/şifre yalnızca burada, ekrandan girilir ve
/// SQL Server'a gerçek bir bağlantı denemesiyle doğrulanır.
///
/// Başarılı giriş hem ProdCredentialStore'a (bellek-içi, bu turun
/// bağlantılarını açar) hem RememberedCredentialStore'a (mon.
/// RememberedCredential, kalıcı) yazılır - kullanıcı isteği "bir kere
/// girdiysem bir daha girmemi istemesin", uygulama yeniden başlasa bile.
/// "Çıkış yap" ikisini de temizler - kalıcı hafızayı silmezsek çıkış
/// yapmanın hiçbir anlamı kalmazdı (bir sonraki açılışta yine otomatik
/// girmiş olurdu).
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/auth");

        api.MapGet("/status", (
            string? instance,
            InstanceRegistry registry,
            ProdCredentialStore store) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });

            return Results.Ok(new
            {
                instance = target.Name,
                requiresLogin = target.RequiresLogin,
                unlocked = !target.RequiresLogin || store.IsUnlocked(target.Name)
            });
        });

        api.MapPost("/login", async (
            LoginRequest body,
            SqlConnectionFactory factory,
            InstanceRegistry registry,
            ProdCredentialStore store,
            RememberedCredentialStore remembered,
            CancellationToken ct) =>
        {
            var target = registry.Find(body.Instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });

            if (!target.RequiresLogin)
                return Results.BadRequest(new { error = "Bu sunucu için giriş gerekmiyor." });

            if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
                return Results.BadRequest(new { error = "Kullanıcı adı ve şifre gerekli." });

            try
            {
                // Doğru/yanlış hükmünü biz vermiyoruz - SQL Server veriyor.
                // Bağlantı açılırsa girilen bilgiler doğrudur.
                await factory.VerifyCredentialsAsync(target, body.Username, body.Password, ct);
            }
            catch (Exception ex)
            {
                // Yanlış şifre de, sunucuya ulaşılamaması da aynı yoldan
                // (bağlantı açılamadı) geçtiği için burada ikisini ayırmaya
                // çalışmıyoruz - SQL Server'ın kendi hata mesajı zaten
                // hangisi olduğunu söylüyor, kullanıcıya olduğu gibi gösteriyoruz.
                return Results.Json(new { error = $"Giriş başarısız: {ex.Message}" }, statusCode: 401);
            }

            store.Set(target.Name, body.Username, body.Password);

            try
            {
                await remembered.SaveAsync(target.Name, body.Username, body.Password, ct);
            }
            catch (Exception ex)
            {
                // Kalıcı kayıt başarısız olsa bile giriş GEÇERLİ - bu turun
                // bağlantıları çalışır, yalnızca bir sonraki açılışta
                // yeniden girmesi gerekebilir. Girişi burada reddetmek
                // yanlış olur.
                app.Logger.LogWarning(ex, "Giriş bilgisi kalıcı olarak hatırlanamadı: {Instance}", target.Name);
            }

            return Results.Ok(new { ok = true });
        });

        api.MapPost("/logout", async (
            LogoutRequest body,
            InstanceRegistry registry,
            ProdCredentialStore store,
            RememberedCredentialStore remembered,
            CancellationToken ct) =>
        {
            var target = registry.Find(body.Instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });

            store.Clear(target.Name);
            await remembered.ForgetAsync(target.Name, ct);

            return Results.Ok(new { ok = true });
        });
    }

    private sealed record LoginRequest(string Instance, string Username, string Password);
    private sealed record LogoutRequest(string Instance);
}
