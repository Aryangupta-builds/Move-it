using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
namespace MoveIt;

public partial class App : Application
{
    Window? w;
    static Mutex? single;   // held for the whole run: a second launch finds it and just brings the first window forward
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? cls, string title);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);

    static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "crash.log");

    // Writes the error to %LOCALAPPDATA%\MoveIt\crash.log (a WinUI app that fails at start-up otherwise closes silently)
    static void Log(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}\r\n{ex}\r\n\r\n");
        }
        catch { }
    }

    static void Fail(string where, Exception ex)
    {
        Log(where, ex);
        try { MessageBoxW(IntPtr.Zero, $"MoveIt could not start.\n\n{where}\n{ex.GetType().Name}: {ex.Message}\n\nDetails: {LogPath}", "MoveIt", 0x10); } catch { }
    }

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Log("UnobservedTaskException", e.Exception);
        UnhandledException += (_, e) => Log("Application.UnhandledException", e.Exception);
        try { InitializeComponent(); }
        catch (Exception ex) { Fail("App.xaml failed to load", ex); throw; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        single = new Mutex(true, @"Local\MoveIt.SingleInstance", out bool first);
        if (!first)
        {
            var h = FindWindowW(null, "MoveIt");
            if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); }   // 9 = SW_RESTORE
            Environment.Exit(0);
        }
        try { w = new MainWindow(); if (!(Environment.GetCommandLineArgs().Contains("--startup") && w.StartHiddenOk)) w.Activate(); }   // Windows startup with the tray on: stay in the tray, no window
        catch (Exception ex) { Fail("Main window failed to open", ex); Environment.Exit(1); }
    }
}
