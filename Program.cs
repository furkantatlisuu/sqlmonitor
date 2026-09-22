using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlMonitor.Api;
using SqlMonitor.Infrastructure;
using SqlMonitor.Options;
using SqlMonitor.Services;

// ---------------------------------------------------------------------
// TEK KOPYA KORUMASI
//
// Çift tıklanan bir uygulamada en sık yapılan hata, zaten açıkken bir kez
// daha çift tıklamaktır. Konsol olmadığı için ikinci kopya kullanıcıya
// hiçbir şey söylemeden ölürdü; üstelik ölmeseydi DAHA kötü olurdu -
// iki toplayıcı aynı izleme veritabanına yazardı.
//
// Doğru davranış: "zaten açık" bir hata değil. Çalışan kopyayı tarayıcıda
// aç ve sessizce çık. Kullanıcı açısından ikinci çift tıklama da
// "uygulamayı aç" demektir, sonuç aynı olmalı.
//
// Mutex adı dinlenen adresi içeriyor: farklı portlara ayarlanmış iki
// kurulum (örn. test) birbirini engellemesin.
// ---------------------------------------------------------------------
// Adres, konağın (host) kendi kurallarıyla AYNI sırada çözülmeli - yoksa
// mutex adı ve tarayıcıda açılacak adres, uygulamanın gerçekte dinlediği
// adresten farklı olabilir: ASPNETCORE_URLS ile başka bir porta alınmış
// bir kurulumda ikinci kopya yanlış adrese tarayıcı açardı.
var startupConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .Build();

// Dinlenecek adres TEK bir yerde çözülüyor ve hem Kestrel'e hem de
// aşağıdaki mutex/tarayıcı mantığına aynı değer veriliyor - ikisinin
// ayrışması, "tarayıcı yanlış adresi açtı" gibi anlaşılmaz hatalara
// yol açardı.
//
// Öncelik sırası bilerek STANDART yolları üstte tutuyor:
//   1) --urls http://...        (komut satırı)
//   2) ASPNETCORE_URLS          (ortam değişkeni, yaygın dağıtım yolu)
//   3) Monitor:ListenUrl        (appsettings.json - bizim varsayılanımız)
//
// NOT: bu ayar eskiden appsettings.json'da "Urls" adıyla duruyordu ve
// canlıda şu tuzak görüldü: ASP.NET Core'un kendi "Urls" anahtarı
// appsettings'ten okununca ASPNETCORE_URLS'i EZİYOR, yani portu ortam
// değişkeniyle değiştirmek sessizce çalışmıyordu. Kendi anahtarımıza
// taşıyıp önceliği burada açıkça kurunca sorun ortadan kalkıyor.
static string? ArgValue(string[] argv, string name)
{
    var i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

var listenUrls =
    ArgValue(args, "--urls")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? startupConfig["Monitor:ListenUrl"]
    ?? "http://localhost:51900";

// Birden fazla adres ";" ile verilebilir; tarayıcı/mutex için ilki yeterli.
var configuredUrl = listenUrls.Split(';', StringSplitOptions.RemoveEmptyEntries)
    .FirstOrDefault() ?? "http://localhost:51900";

var noBrowser = args.Contains("--no-browser");

var mutexName = "Local\\SqlMonitor_" +
    string.Concat(configuredUrl.Where(char.IsLetterOrDigit));

using var singleInstance = new Mutex(initiallyOwned: true, mutexName, out var isFirstInstance);

if (!isFirstInstance)
{
    // Zaten çalışan kopyayı göster. Tarayıcı açılamazsa bile sessizce
    // çıkıyoruz - ikinci bir kopya başlatmak her hâlükârda yanlış olurdu.
    // --no-browser burada da geçerli: "tarayıcı açma" dediyse, ikinci
    // çift tıklamada da açmıyoruz.
    if (!noBrowser)
    {
        try
        {
            Process.Start(new ProcessStartInfo(configuredUrl) { UseShellExecute = true });
        }
        catch { /* tarayıcı açılamadı, yapacak bir şey yok */ }
    }

    return 0;
}

var builder = WebApplication.CreateBuilder(args);

// Yukarıda çözülen adresi Kestrel'e veriyoruz - böylece uygulamanın
// GERÇEKTEN dinlediği adres ile mutex/tarayıcı için kullandığımız adres
// aynı olmak ZORUNDA, ayrışamaz.
builder.WebHost.UseUrls(listenUrls);

// Bağlantı dizeleri appsettings.json'da düz metin duruyor. Geliştirme
// için sorun değil ama sunucuya kurarken bunları user-secrets'a veya
// ortam değişkenine taşı. Aşağıdaki satır ortam değişkenlerini otomatik
// okur: Monitor__Instances__0__ConnectionString gibi.
// Yerel sırlar (Slack webhook'u, varsa özel bağlantı dizeleri) buraya:
// appsettings.Local.json .gitignore'da - depoya da, patronuna göndereceğin
// zip'e de girmez, ama senin makinende appsettings.json'ı EZER. Böylece
// paylaşılan appsettings.json tertemiz kalırken kurulumun çalışmaya
// devam eder. Dosya yoksa hiçbir şey olmaz (optional: true).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Configuration.AddEnvironmentVariables();

// Günlük sağlayıcıları BİLEREK sıfırdan kuruluyor.
//
// Varsayılanlar arasında Windows Event Log da var ve canlıda şu hatayı
// verdiği görüldü: açılış başarısız olduğunda (örn. port meşgul) hata
// yazılmaya çalışılırken "Cannot access a disposed object: EventLogInternal"
// fırlatıyor, bu da aşağıdaki try/catch'e HİÇ SIRA GELMEDEN süreci
// öldürüyordu - yani asıl hatayı gösterecek mekanizma, hatanın kendisi
// yüzünden çalışamıyordu. Event Log zaten bu uygulama için gereksiz
// (kaynak oluşturmak yönetici yetkisi ister).
//
// Geriye ihtiyacımız olan ikisi kalıyor: Console (pencereli çalışırken
// hiçbir yere yazmaz, zararsız) ve exe'nin yanındaki logs/ klasörüne
// yazan kendi dosya günlüğümüz - konsol olmayınca tek teşhis kaynağı o.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();   // Visual Studio'da F5 ile çalışırken Çıktı penceresi
builder.Logging.AddProvider(new FileLoggerProvider(
    Path.Combine(AppContext.BaseDirectory, "logs")));

builder.Services.Configure<MonitorOptions>(
    builder.Configuration.GetSection(MonitorOptions.SectionName));

// RequiresLogin=true instance'lar (Prod) için çalışma zamanı kimlik
// bilgisi deposu - SqlConnectionFactory bundan önce kurulmalı, ona
// enjekte ediliyor.
builder.Services.AddSingleton<ProdCredentialStore>();
builder.Services.AddSingleton<SqlConnectionFactory>();

// ProdCredentialStore'un KALICI hâli (mon.RememberedCredential) -
// kullanıcı "bir kere girdiysem bir daha girmemi istemesin" dedi.
// SqlConnectionFactory'ye bağımlı, ProdCredentialStore'a değil - döngüsel
// bağımlılık olmasın diye kimlik bilgisini ProdCredentialStore'a
// aktarmayı Program.cs (açılışta) ve AuthEndpoints (girişte) yapıyor.
builder.Services.AddSingleton<RememberedCredentialStore>();

// İzlenen sunucu listesinin tek doğruluk kaynağı - appsettings.json'daki
// Monitor:Instances yalnızca ilk açılışta bir tohum. SqlConnectionFactory
// ve ProdCredentialStore'dan SONRA kurulmalı, ikisine de bağımlı.
builder.Services.AddSingleton<InstanceRegistry>();

// Sayaç farkları uygulama ömrü boyunca hafızada tutulmalı - singleton.
builder.Services.AddSingleton<CounterDeltaTracker>();
builder.Services.AddSingleton<StreakTracker>();
builder.Services.AddSingleton<BackupHistoryReader>();
builder.Services.AddSingleton<IndexAnalysisService>();
builder.Services.AddSingleton<StoredProcedureService>();
builder.Services.AddSingleton<AlwaysOnService>();
builder.Services.AddSingleton<AgentJobService>();
builder.Services.AddSingleton<IdentityColumnScanner>();
builder.Services.AddSingleton<AgentJobHealthCache>();
builder.Services.AddSingleton<AlwaysOnHealthCache>();
builder.Services.AddSingleton<SlackDebouncer>();

// SlackNotifier kendi HttpClient'ını AddHttpClient'tan alıyor - webhook
// isteği yavaş/asılı kalırsa toplayıcıyı sonsuza dek bloklamasın diye
// kısa bir zaman aşımı.
builder.Services.AddHttpClient<SlackNotifier>(c => c.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddScoped<MetricStore>();
builder.Services.AddScoped<HealthEvaluator>();
builder.Services.AddScoped<LiveMonitorService>();

builder.Services.AddHostedService<CollectorService>();

// JSON: enum'lar sayı yerine metin olarak gitsin, frontend'de
// Severity === 2 yerine severity === "Critical" yazabilelim.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapLiveEndpoints();
app.MapAuthEndpoints();
app.MapInstanceManagementEndpoints();

// Açılışta konfigürasyonu doğrula. Yanlış ayarla sessizce çalışan bir
// izleme aracı, hiç çalışmayandan daha tehlikelidir.
var options = app.Services.GetRequiredService<
    Microsoft.Extensions.Options.IOptions<MonitorOptions>>().Value;

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

if (string.IsNullOrWhiteSpace(options.StoreConnectionString))
    logger.LogWarning("Monitor:StoreConnectionString boş — trend ve olay geçmişi çalışmayacak, sunucu listesi yönetilemeyecek.");
else
    // İzleme veritabanını (SqlMonitorDb) kendiliğinden kurar - db/01_create_
    // monitordb.sql'i elle çalıştırma zorunluluğunu kaldırır. Tamamen
    // idempotent; veritabanı zaten varsa hiçbir şey değişmez. Hata olursa
    // yalnızca loglar, açılışı DURDURMAZ (bkz. StoreSchemaInitializer).
    await StoreSchemaInitializer.EnsureCreatedAsync(options.StoreConnectionString, logger, default);

// İzlenen sunucu listesini mon.MonitoredInstance'tan yükler (boşsa
// appsettings.json'daki Instances ile tohumlar) - bkz. InstanceRegistry
// üstündeki not. Şema kurulumundan SONRA çalışmalı, tablo henüz yoksa
// bu adım hiçbir şey okuyamaz.
var registry = app.Services.GetRequiredService<InstanceRegistry>();
await registry.InitializeAsync(default);

// Önceden başarıyla girilmiş RequiresLogin kimlik bilgilerini
// (mon.RememberedCredential) belleğe önceden yükler - kullanıcı her
// yeniden başlatmada (F5) yeniden girmek zorunda kalmasın diye.
// StoreConnectionString boşsa (izleme DB'si yok) bu tablo da yok -
// sessizce atlanır, ilk açılıştaki uyarı zaten bunu açıklıyor.
if (!string.IsNullOrWhiteSpace(options.StoreConnectionString))
{
    try
    {
        var rememberedStore = app.Services.GetRequiredService<RememberedCredentialStore>();
        var credentialStore = app.Services.GetRequiredService<ProdCredentialStore>();
        var remembered = await rememberedStore.LoadAllAsync(default);

        foreach (var (key, userId, password) in remembered)
            credentialStore.Set(key, userId, password);

        if (remembered.Count > 0)
            logger.LogInformation("{Count} sunucu için hatırlanmış giriş bilgisi yüklendi.", remembered.Count);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Hatırlanmış giriş bilgileri yüklenemedi - RequiresLogin sunucular yine ekrandan giriş isteyecek.");
    }
}

if (registry.Instances.Count == 0)
    logger.LogWarning("İzlenecek sunucu tanımlanmamış — \"Sunucuları yönet\" ekranından ekleyebilirsin.");

foreach (var instance in registry.Instances)
    logger.LogInformation("İzlenen instance: {Name}", instance.Name);

// EXE olarak çift tıklanınca tarayıcıyı kendimiz açıyoruz - "çalıştır,
// sonra adresi elle yaz" adımı kullanıcıya bırakılmasın.
//
// İki durumda AÇMIYORUZ:
//   --no-browser   : elle kapatmak isteyen için
//   UserInteractive=false : Windows Service / arka plan olarak
//     çalışıyorsak oturum yok, tarayıcı açmak anlamsız (ve hata verir).
if (Environment.UserInteractive && !noBrowser)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        // Gerçekten dinlenen adresi kullanıyoruz - yapılandırmadan
        // tahmin etmek yerine (Urls/ASPNETCORE_URLS/komut satırı hepsi
        // burayı etkiler, sonuç yalnızca burada kesinleşir).
        var url = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?.Addresses.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            logger.LogInformation("Tarayıcı açıldı: {Url}", url);
        }
        catch (Exception ex)
        {
            // Tarayıcı açılamazsa uygulama yine çalışıyor - adresi yaz, yeter.
            logger.LogWarning(ex, "Tarayıcı açılamadı. Adresi elle aç: {Url}", url);
        }
    });
}

// Konsol olmadığı için başarısız bir açılış EKRANDA HİÇBİR İZ BIRAKMAZ -
// kullanıcı çift tıklar, hiçbir şey olmaz. En sık sebep portun meşgul
// olmasıdır (uygulama zaten açık ya da Visual Studio çalışıyor). Hatayı
// hem günlüğe yazıp hem pencerede gösteriyoruz.
try
{
    app.Run();
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Uygulama başlatılamadı.");
    StartupFailureDialog.Show(ex, Path.Combine(AppContext.BaseDirectory, "logs"));
    return 1;
}

return 0;
