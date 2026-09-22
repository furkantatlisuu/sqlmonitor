using System.Runtime.InteropServices;

namespace SqlMonitor.Infrastructure;

/// <summary>
/// Açılış başarısız olduğunda kullanıcıya görünür bir pencere gösterir.
///
/// Neden gerekli: uygulama pencereli (WinExe) çalıştığı için konsol yok.
/// Konsol olsaydı hata ekranda yazardı; olmayınca çift tıklayan kişi için
/// "hiçbir şey olmadı" ile "port kullanımda, başlayamadım" aynı görünür.
/// Sessiz başarısızlık, izleme aracının yapabileceği en kötü şeydir -
/// kullanıcı izlendiğini sanır, oysa hiçbir şey çalışmıyordur.
///
/// user32.dll'deki MessageBox doğrudan çağrılıyor: WinForms/WPF bağımlılığı
/// eklemeden (ve hedef çatıyı net8.0-windows'a çevirmeden) bir pencere
/// göstermenin en ucuz yolu. Windows dışında sessizce atlanır.
/// </summary>
public static class StartupFailureDialog
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_TOPMOST = 0x40000;

    public static void Show(Exception ex, string logDirectory)
    {
        if (!OperatingSystem.IsWindows()) return;

        // En sık karşılaşılacak hataya özel, anlaşılır bir açıklama -
        // ham .NET istisnası çoğu kullanıcıya hiçbir şey anlatmaz.
        var hint = ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("Failed to bind", StringComparison.OrdinalIgnoreCase)
            ? "Bu adres başka bir uygulama tarafından kullanılıyor.\n\n" +
              "Muhtemel sebep: uygulama zaten açık ya da Visual Studio'dan " +
              "çalışıyor. Önce onu kapat, sonra tekrar dene.\n\n"
            : "";

        var text =
            "SQL İzleme başlatılamadı.\n\n" +
            hint +
            $"Hata: {ex.Message}\n\n" +
            $"Ayrıntılar için günlük dosyasına bak:\n{logDirectory}";

        try
        {
            MessageBoxW(IntPtr.Zero, text, "SQL İzleme", MB_OK | MB_ICONERROR | MB_TOPMOST);
        }
        catch
        {
            // Pencere gösterilemediyse yapacak bir şey kalmadı - günlük zaten yazıldı.
        }
    }
}
