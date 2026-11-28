using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
namespace MoveIt;

public partial class App : Application
{
    Window? w;

    public App()
    {
        // Log every kind of crash to %LocalAppData%\MoveIt\crash.log so a silent exit never happens again
        UnhandledException += (_, e) => Log("XAML unhandled", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("AppDomain unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Log("Unobserved task", e.Exception);
        try { InitializeComponent(); }
        catch (Exception ex) { Fatal("Could not load the app resources", ex); throw; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { w = new MainWindow(); w.Activate(); }
        catch (Exception ex) { Fatal("MoveIt could not open its window", ex); }
    }

    static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "crash.log");

    public static void Log(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}\r\n{ex}\r\n\r\n");
        }
        catch { }
    }

    static void Fatal(string title, Exception ex)
    {
        Log(title, ex);
        try { MessageBoxW(IntPtr.Zero, $"{title}.\n\n{ex.Message}\n\nDetails were saved to:\n{LogPath}", "MoveIt", 0x10); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
