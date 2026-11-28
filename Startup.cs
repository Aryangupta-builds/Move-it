using Microsoft.Win32;
namespace MoveIt;

// Windows startup entry. One fixed value name ("MoveIt") under HKCU\...\Run, so it can never be duplicated.
// Task Manager / Settings > Apps > Startup show it and write their own on/off flag to StartupApproved\Run.
public static class StartupReg
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string Name = "MoveIt";

    static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MoveIt.exe");

    // True only if the entry exists AND the user has not switched it off in Task Manager / Windows Settings.
    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) is not string) return false;
            using var ap = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (ap?.GetValue(Name) is byte[] b && b.Length > 0 && (b[0] & 1) == 1) return false;   // 03 = disabled by user
            return true;
        }
        catch { return false; }
    }

    // Turning it on in MoveIt is explicit consent, so a flag set earlier in Task Manager is cleared.
    public static bool Set(bool on)
    {
        try
        {
            if (on)
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKey);
                run.SetValue(Name, $"\"{ExePath}\" --startup", RegistryValueKind.String);
                using var ap = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
                ap?.DeleteValue(Name, false);
            }
            else
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, true);
                run?.DeleteValue(Name, false);
                using var ap = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
                ap?.DeleteValue(Name, false);
            }
            return IsEnabled() == on;
        }
        catch { return false; }
    }
}
