using System.Collections.Concurrent;
using System.Text;

namespace SqlMonitor.Infrastructure;

/// <summary>
/// Basit, bağımlılıksız dosya günlüğü.
///
/// Neden var: uygulama pencereli (WinExe) çalıştığında konsol YOKTUR -
/// varsayılan konsol günlüğü hiçbir yere yazmaz. O hâlde "bağlanamadı",
/// "port kullanımda", "giriş başarısız" gibi mesajlar tamamen kaybolur ve
/// kullanıcı çift tıklayıp hiçbir şey olmamasının sebebini göremez.
/// Burası o boşluğu dolduruyor: her şey exe'nin yanındaki logs/ klasörüne,
/// günlük bir dosyaya yazılır.
///
/// Neden Serilog/NLog değil: tek ihtiyaç "satırı dosyaya ekle". Bir izleme
/// aracının kurulumu mümkün olduğunca az parçadan oluşmalı - bu dosya
/// (~90 satır) bir NuGet bağımlılığından daha ucuz.
///
/// Günlük yazımı ASLA uygulamayı düşürmez: her hata yutulur. Bir izleme
/// aracının kendi günlüğü yüzünden çökmesi kabul edilemez.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string? _directory;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    /// <summary>Bu kadar günden eski günlükler silinir - disk izleyen bir araç kendi diskini doldurmamalı.</summary>
    private const int RetentionDays = 14;

    /// <summary>
    /// BOM YAZMAYAN UTF-8. StreamWriter'ın varsayılanı BOM yazar; dosyanın
    /// başında görünen "" karakteri hem gereksiz hem de grep/Select-String
    /// ile arayan birinin ilk satırı kaçırmasına yol açar.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Temizliğin en son hangi gün yapıldığı. Yalnızca açılışta temizlemek
    /// YETMEZ: bu araç 7/24 açık kalmak üzere tasarlandı, aylarca yeniden
    /// başlatılmayan bir kurulumda saklama süresi hiç uygulanmazdı.
    /// </summary>
    private DateOnly _lastPurgeDay = DateOnly.FromDateTime(DateTime.Now);

    public FileLoggerProvider(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _directory = directory;
            PurgeOldFiles(directory);
        }
        catch
        {
            // Yazılamayan bir klasör (örn. Program Files, yetki yok) günlüğü
            // devre dışı bırakır - uygulamanın kendisini değil.
            _directory = null;
        }
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose() => _loggers.Clear();

    internal void Write(string line)
    {
        if (_directory is null) return;

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var path = Path.Combine(_directory, $"sqlmonitor-{today:yyyyMMdd}.log");

            // FileShare.ReadWrite ŞART: uygulamanın ikinci bir kopyası
            // açılmaya çalıştığında (en tipik hata durumu - port meşgul)
            // iki süreç AYNI dosyaya yazmak ister. File.AppendAllText'in
            // varsayılan paylaşımıyla ikincisi IOException alıyor ve
            // hatası sessizce kayboluyordu - tam da okumak istediğimiz satır.
            lock (_gate)
            {
                // Gün değiştiyse (gece yarısını geçen uzun süreli çalışma)
                // eski dosyaları da burada temizliyoruz - yeniden başlatma
                // beklemeden.
                if (today != _lastPurgeDay)
                {
                    _lastPurgeDay = today;
                    PurgeOldFiles(_directory);
                }

                using var stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Utf8NoBom);
                writer.WriteLine(line);
            }
        }
        catch
        {
            // Disk dolu, dosya kilitli, ağ sürücüsü gitti... hiçbiri
            // uygulamayı durdurmaya değmez.
        }
    }

    private static void PurgeOldFiles(string directory)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(directory, "sqlmonitor-*.log"))
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
        }
        catch { /* temizlik başarısız olabilir, önemli değil */ }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            // "SqlMonitor.Services.CollectorService" yerine "CollectorService" -
            // günlük satırı okunabilir kalsın.
            _category = category.Split('.')[^1];
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var level = logLevel switch
            {
                LogLevel.Critical => "KRİTİK",
                LogLevel.Error => "HATA  ",
                LogLevel.Warning => "UYARI ",
                _ => "BİLGİ "
            };

            var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} [{_category}] {formatter(state, exception)}";
            if (exception is not null) text += Environment.NewLine + exception;

            _provider.Write(text);
        }
    }
}
