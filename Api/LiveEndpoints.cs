using Dapper;
using Microsoft.Extensions.Options;
using SqlMonitor.Infrastructure;
using SqlMonitor.Models;
using SqlMonitor.Options;
using SqlMonitor.Services;
using SqlMonitor.Sql;

namespace SqlMonitor.Api;

public static class LiveEndpoints
{
    /// <summary>
    /// RequiresLogin=true olan bir instance için kilit henüz açılmadıysa
    /// true. Her uç, izlenen sunucuya dokunmadan ÖNCE bunu kontrol eder -
    /// SqlConnectionFactory içindeki ProdLoginRequiredException son bir
    /// güvenlik ağı, asıl kapı burası: kilitliyken hiçbir DMV sorgusu
    /// denenmez, kullanıcıya doğrudan "giriş gerekli" cevabı döner.
    /// </summary>
    private static bool LoginRequired(InstanceOptions target, ProdCredentialStore store)
        => target.RequiresLogin && !store.IsUnlocked(target.Name);

    private static IResult LoginRequiredResult(InstanceOptions target)
        => Results.Json(new { error = "login_required", instance = target.Name }, statusCode: 401);

    public static void MapLiveEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // -------------------------------------------------------------
        // Ekranın tamamı tek uçtan.
        //
        // Frontend'in 12 ayrı istek atması yerine tek istek atmasını
        // tercih ettik: paneller arasında zaman kayması olmuyor ve
        // izlenen sunucuya tek bağlantı açılıyor.
        // -------------------------------------------------------------
        api.MapGet("/live/snapshot", async (
            string? instance,
            LiveMonitorService live,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null)
                return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });

            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            try
            {
                var snapshot = await live.GetSnapshotAsync(target, ct);
                return Results.Ok(snapshot);
            }
            catch (ProdLoginRequiredException)
            {
                // Yukarıdaki kontrol ile bağlantının açılması arasında kilit
                // kapanmış olabilir (başka bir sekmede "Çıkış yap") - bu bir
                // sunucu hatası değil, giriş gerektiren bir durum.
                return LoginRequiredResult(target);
            }
            catch (Exception ex)
            {
                // Panel BAZINDA hatalar zaten SafeAsync ile yutuluyor
                // (snapshot.Errors'a düşüyor); buraya ancak bağlantının
                // KENDİSİ açılamadığında geliniyor: yanlış sunucu adı, yanlış
                // şifre, sunucu kapalı... Eskiden bu çıplak bir HTTP 500'dü,
                // gövdesi boştu - kullanıcı ekranda yalnızca "HTTP 500"
                // görüyordu. Kurulumdaki EN OLASI ilk hata bu olduğu için
                // sebebi olduğu gibi göstermek önemli.
                return Results.Problem(
                    title: $"'{target.Name}' sunucusuna bağlanılamadı",
                    detail: ex.Message, statusCode: 502);
            }
        });

        // İzlenen instance listesi - üstteki seçici için.
        api.MapGet("/instances", (InstanceRegistry registry) =>
            Results.Ok(registry.Instances.Select(i => new
            {
                i.Name,
                DisplayName = i.DisplayName ?? i.Name,
                i.IsDefault,
                i.RequiresLogin
            })));

        // Olay zaman çizelgesi.
        api.MapGet("/live/timeline", async (
            string? instance,
            int? hours,
            MetricStore store,
            InstanceRegistry registry,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound();

            var entries = await store.GetTimelineAsync(
                target.Name, Math.Clamp(hours ?? 24, 1, 168), ct);

            return Results.Ok(entries);
        });

        // -------------------------------------------------------------
        // Bir olayın kanıtı: "uzun süren sorgu var" diyen olayın HANGİ
        // sorgu olduğu.
        //
        // AYRI UÇ OLMASI KASITLI, /live/session/{spid}/plan ile aynı
        // gerekçeyle: 100 olaylık çizelge her yenilemede megabaytlarca
        // SQL metni taşımasın, yalnızca tıklanan olayınki gelsin.
        //
        // Canlı DMV'ye BAKMIYOR - olay anında yazılmış kaydı okuyor.
        // Kullanıcı olaya saatler sonra tıklıyor; o oturum çoktan bitmiş
        // olur, DMV'de aramanın anlamı yok.
        // -------------------------------------------------------------
        api.MapGet("/live/event/{eventId:long}/detail", async (
            long eventId,
            string? instance,
            MetricStore store,
            InstanceRegistry registry,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound();

            var evidence = await store.GetEventEvidenceAsync(target.Name, eventId, ct);
            return Results.Ok(evidence);
        });

        // -------------------------------------------------------------
        // "Bu uyarı neden çıktı?" - sağlık kartına tıklanınca.
        //
        // Yukarıdaki olay ucuyla KARIŞTIRILMAMALI: orası geçmiş bir
        // olayın kaydedilmiş fotoğrafı, burası şu anki durumun canlı
        // sorgusu. Kart canlı bir şeyi gösteriyor, cevabı da canlı
        // olmalı - 3 saat önceki kanıtı göstermek yanlış olurdu.
        // -------------------------------------------------------------
        api.MapGet("/live/check/{key}/evidence", async (
            string key,
            string? instance,
            CheckEvidenceService evidenceService,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });
            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            if (!CheckEvidenceService.Supports(key))
                return Results.NotFound(new { error = $"'{key}' kontrolü için sorgu düzeyinde kanıt yok." });

            try
            {
                return Results.Ok(await evidenceService.GetAsync(target, key, ct));
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Kanıt okunamadı",
                    detail: ex.Message,
                    statusCode: 502);
            }
        });

        // -------------------------------------------------------------
        // Tek bir oturumun sorgu metni ve planı.
        //
        // AYRI UÇ OLMASI KASITLI: plan XML'i çekmek pahalıdır. Toplu
        // listede asla çekilmez, yalnızca kullanıcı bir satıra tıkladığında
        // ve yalnızca o oturum için çekilir.
        //
        // Parametre olarak session_id alıyoruz, ham SQL metni değil.
        // -------------------------------------------------------------
        api.MapGet("/live/session/{sessionId:int}/plan", async (
            int sessionId,
            string? instance,
            SqlConnectionFactory factory,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound();
            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            const string sql = """
                SELECT
                    SqlText  = CONVERT(nvarchar(max), ISNULL(t.text, N'')),
                    PlanXml  = CONVERT(nvarchar(max), ISNULL(CONVERT(nvarchar(max), p.query_plan), N''))
                FROM sys.dm_exec_requests AS r WITH (NOLOCK)
                OUTER APPLY sys.dm_exec_sql_text(r.sql_handle)     AS t
                OUTER APPLY sys.dm_exec_query_plan(r.plan_handle)  AS p
                WHERE r.session_id = @SessionId
                OPTION (RECOMPILE);
                """;

            await using var conn = factory.CreateTargetConnection(target);
            await conn.OpenAsync(ct);

            var row = await conn.QueryFirstOrDefaultAsync<PlanResult>(
                new CommandDefinition(sql, new { SessionId = sessionId },
                    commandTimeout: factory.QueryTimeoutSeconds, cancellationToken: ct));

            return row is null
                ? Results.NotFound(new { error = "Oturum artık çalışmıyor." })
                : Results.Ok(row);
        });

        // -------------------------------------------------------------
        // Bir oturumu sonlandır - Aktivite sekmesindeki bloklama zincirinde
        // "Sonlandır" butonu için. KILL bir DDL komutudur, parametre kabul
        // ETMEZ - SessionId'nin gerçekten bir tamsayı olduğu (route/body
        // model binding'i tarafından, aşağıdaki >0 kontrolüyle) doğrulanmadan
        // sorguya gömülmüyor; enjeksiyon riski bu yüzden yok. Onay
        // istemcide (window.confirm) alınıyor - bu geri alınamaz bir eylem.
        // -------------------------------------------------------------
        api.MapPost("/live/kill-session", async (
            KillSessionRequest body,
            SqlConnectionFactory factory,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            var target = registry.Find(body.Instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });
            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            if (body.SessionId <= 0)
                return Results.BadRequest(new { error = "Geçersiz SPID." });

            try
            {
                await using var conn = factory.CreateTargetConnection(target);
                await conn.OpenAsync(ct);

                await conn.ExecuteAsync(new CommandDefinition(
                    $"KILL {body.SessionId};", commandTimeout: 10, cancellationToken: ct));

                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                // "Oturum yok" da (zaten bitmiş) buradan geçer - kullanıcıya
                // olduğu gibi göster, çoğu zaman zaten aradığı sonuç budur.
                return Results.Problem(
                    title: "Oturum sonlandırılamadı", detail: ex.Message, statusCode: 502);
            }
        });

        // -------------------------------------------------------------
        // Index Analizi - eksik index önerileri.
        //
        // BİLEREK snapshot'ın dışında - kullanıcı sekmeyi açtığında ya da
        // "yenile"ye bastığında çalışır. Saniyede bir çalışan snapshot'a
        // bu taramayı eklemek ekranın geri kalanını da yavaşlatırdı.
        // -------------------------------------------------------------
        api.MapGet("/live/missing-indexes", async (
            string? instance,
            int? top,
            IndexAnalysisService indexAnalysis,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });
            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            try
            {
                var result = await indexAnalysis.GetMissingIndexesAsync(target, top ?? 100, ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Eksik index önerileri okunamadı",
                    detail: ex.Message,
                    statusCode: 502);
            }
        });

        // -------------------------------------------------------------
        // Stored Procedure - en ağır prosedürler (sys.dm_exec_procedure_stats).
        // Index Analizi gibi BİLEREK snapshot'ın dışında.
        // -------------------------------------------------------------
        api.MapGet("/live/top-procedures", async (
            string? instance,
            string? sort,
            string? dir,
            int? top,
            StoredProcedureService procedures,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });
            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            var sortKey = sort ?? "cpu";
            if (!StoredProcedureService.IsValidSortKey(sortKey))
                return Results.BadRequest(new { error = $"Geçersiz sort değeri: {sortKey}" });

            // "asc" DIŞINDA her şey (boş, "desc", saçma bir değer) DESC -
            // mevcut davranışı bozmayan, güvenli bir varsayılan.
            var descending = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);

            try
            {
                var result = await procedures.GetTopProceduresAsync(target, sortKey, descending, top ?? 50, ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Stored procedure istatistikleri okunamadı",
                    detail: ex.Message,
                    statusCode: 502);
            }
        });

        // Tek bir prosedürün tam tanımı. Yalnızca "İncele" tıklandığında.
        api.MapGet("/live/procedure-definition", async (
            string? instance,
            string sqlHandle,
            string planHandle,
            StoredProcedureService procedures,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });
            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            try
            {
                var detail = await procedures.GetDefinitionAsync(target, sqlHandle, planHandle, ct);
                return detail is null
                    ? Results.NotFound(new { error = "Tanım artık cache'te değil (tahliye olmuş olabilir)." })
                    : Results.Ok(detail);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // -------------------------------------------------------------
        // Kullanılmayan prosedürler - plan cache'te (ve varsa Query
        // Store'da) hiç görünmeyen prosedürler. BİLEREK ayrı uç: taban
        // "top procedures" sorgusundan daha pahalı (çok-veritabanlı
        // dinamik SQL), sekme açıldığında yalnızca bir kez çekilir.
        // -------------------------------------------------------------
        api.MapGet("/live/unused-procedures", async (
            string? instance,
            int? top,
            StoredProcedureService procedures,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });
            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            try
            {
                var result = await procedures.GetUnusedProceduresAsync(target, top ?? 150, ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Kullanılmayan prosedür taraması başarısız",
                    detail: ex.Message,
                    statusCode: 502);
            }
        });

        // Kullanılmayan bir prosedürün tam tanımı - sql_handle'ı olmadığı
        // için query-plan/procedure-definition uçlarından FARKLI bir yol
        // izler (bkz. StoredProcedureService.GetProcedureSourceAsync).
        api.MapGet("/live/procedure-source", async (
            string? instance,
            string database,
            string schema,
            string proc,
            StoredProcedureService procedures,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });
            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            try
            {
                var detail = await procedures.GetProcedureSourceAsync(target, database, schema, proc, ct);
                return detail is null
                    ? Results.NotFound(new { error = "Prosedür artık bulunamıyor (tarama ile aradaki sürede silinmiş olabilir)." })
                    : Results.Ok(detail);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // -------------------------------------------------------------
        // Always On - AG grubu, replika ve veritabanı bazında senkron
        // sağlığı. BİLEREK ayrı uç, saniyelik döngünün dışında (bkz.
        // AlwaysOnService üstündeki not).
        // -------------------------------------------------------------
        api.MapGet("/live/alwayson", async (
            string? instance,
            AlwaysOnService alwaysOn,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });
            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            try
            {
                var result = await alwaysOn.GetAlwaysOnStatusAsync(target, ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Always On durumu okunamadı",
                    detail: ex.Message,
                    statusCode: 502);
            }
        });

        // -------------------------------------------------------------
        // Agent Jobs - SQL Server Agent job'larının son çalışma durumu.
        // BİLEREK ayrı uç, saniyelik döngünün dışında (bkz. AgentJobService
        // üstündeki not).
        // -------------------------------------------------------------
        api.MapGet("/live/agent-jobs", async (
            string? instance,
            AgentJobService agentJobs,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });
            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            try
            {
                var result = await agentJobs.GetJobsAsync(target, ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Agent job durumu okunamadı",
                    detail: ex.Message,
                    statusCode: 502);
            }
        });

        // -------------------------------------------------------------
        // Bekleme türlerinde zaman içi trend - mon.WaitSample'daki
        // geçmişten, izlenen sunucuya HİÇ dokunmadan (yalnızca izleme
        // veritabanını okur). BİLEREK ayrı uç: KPI trend'leriyle aynı
        // disiplin, snapshot'a bindirilmemiş.
        // -------------------------------------------------------------
        api.MapGet("/live/wait-trend", async (
            string? instance,
            int? hours,
            int? top,
            MetricStore store,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            var target = registry.Find(instance);
            if (target is null) return Results.NotFound(new { error = "Tanımlı instance bulunamadı." });
            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            try
            {
                var result = await store.GetWaitTrendAsync(
                    target.Name, Math.Clamp(hours ?? 6, 1, 48), Math.Clamp(top ?? 5, 1, 10), ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Bekleme trendi okunamadı", detail: ex.Message, statusCode: 502);
            }
        });

        // Hata günlüğü - yetki gerektirdiği için varsayılan kapalı.
        api.MapGet("/live/errorlog", async (
            string? instance,
            SqlConnectionFactory factory,
            IOptions<MonitorOptions> options,
            InstanceRegistry registry,
            ProdCredentialStore credStore,
            CancellationToken ct) =>
        {
            if (!options.Value.EnableErrorLogPanel)
                return Results.Ok(new
                {
                    enabled = false,
                    reason = "Hata günlüğü paneli kapalı. sp_readerrorlog securityadmin " +
                             "yetkisi ister; açmak için Monitor:EnableErrorLogPanel ayarını true yap."
                });

            var target = registry.Find(instance);
            if (target is null) return Results.NotFound();
            if (LoginRequired(target, credStore)) return LoginRequiredResult(target);

            await using var conn = factory.CreateTargetConnection(target);
            await conn.OpenAsync(ct);

            var rows = await conn.QueryAsync<ErrorLogEntry>(new CommandDefinition(
                DmvSql.ErrorLog, commandTimeout: 20, cancellationToken: ct));

            return Results.Ok(new { enabled = true, entries = rows });
        });

        // Uygulamanın kendi sağlığı. Toplayıcı sessizce öldüyse burada görünür.
        api.MapGet("/health", async (
            SqlConnectionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                await using var conn = factory.CreateStoreConnection();
                await conn.OpenAsync(ct);

                var lastRun = await conn.QueryFirstOrDefaultAsync<DateTime?>(
                    new CommandDefinition(
                        "SELECT MAX(StartedAt) FROM mon.CollectorRun;",
                        cancellationToken: ct));

                return Results.Ok(new
                {
                    store = "ok",
                    lastCollectorRunUtc = lastRun,
                    staleSeconds = lastRun is null
                        ? (double?)null
                        : (DateTime.UtcNow - lastRun.Value).TotalSeconds
                });
            }
            catch (Exception ex)
            {
                return Results.Problem($"İzleme veritabanına ulaşılamıyor: {ex.Message}");
            }
        });
    }

    private sealed class PlanResult
    {
        public string SqlText { get; set; } = "";
        public string PlanXml { get; set; } = "";
    }

    private sealed record KillSessionRequest(string Instance, int SessionId);
}
