using System.Text.Json;
using System.Text.Json.Serialization;
using SqlMonitor.Api;
using SqlMonitor.Infrastructure;
using SqlMonitor.Options;
using SqlMonitor.Services;

var builder = WebApplication.CreateBuilder(args);

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

app.Run();
