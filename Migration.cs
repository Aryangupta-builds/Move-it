using System.Text;

namespace MoveIt;

// Things that must never be copied by a migration: passwords, tokens, keys, cookies, login sessions.
public static class Sensitive
{
    static readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase) { ".ssh", ".aws", ".azure", ".gnupg", ".kube", ".docker" };
    static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    { "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", ".git-credentials", ".npmrc", ".netrc", "credentials", "credentials.json", "secrets.json",
      "Login Data", "Cookies", "Web Data", "Local State", "key4.db", "key3.db", "logins.json", "cert9.db", ".env" };
    static readonly string[] Exts = { ".pem", ".pfx", ".p12", ".key", ".kdbx", ".ppk", ".keychain" };

    public static bool IsBlockedDir(string path) => Dirs.Contains(Path.GetFileName(path.TrimEnd('\\', '/')));
    public static bool IsBlockedFile(string path)
    {
        var n = Path.GetFileName(path);
        return Names.Contains(n) || n.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) || Exts.Any(e => n.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ScanRoot
{
    public string Category { get; init; } = "";   // shown in the review (Personal files, Browser data, ...)
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";       // folder or single file on this laptop
    public string DestPrefix { get; init; } = ""; // where it lands on the new laptop, relative to the save folder
}

public sealed class ScanResult
{
    public ScanRoot Root { get; init; } = null!;
    public bool Exists;
    public List<(string p, string rel)> Files = new();
    public List<string> EmptyDirs = new();         // relative paths of folders that contain nothing at all
    public long Bytes; public int Blocked, Unreadable;
}

public static class Scanner
{
    // Read-only: nothing on the source laptop is modified or deleted. Reparse points (links, OneDrive online-only files) are skipped, as in the normal sender.
    public static ScanResult Scan(ScanRoot root, CancellationToken ct)
    {
        var r = new ScanResult { Root = root };
        if (File.Exists(root.Path))
        {
            r.Exists = true;
            try { var fi = new FileInfo(root.Path); r.Files.Add((root.Path, root.DestPrefix + "/" + fi.Name)); r.Bytes += fi.Length; } catch { r.Unreadable++; r.Files.Clear(); }
            return r;
        }
        if (!Directory.Exists(root.Path)) return r;
        r.Exists = true;
        var stack = new Stack<string>(); stack.Push(root.Path);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var d = stack.Pop(); int n = 0; bool failed = false;
            // One enumeration gives name, attributes and size together (no extra GetAttributes/FileInfo call per file) and nothing is copied into a list.
            try
            {
                foreach (var fsi in new DirectoryInfo(d).EnumerateFileSystemInfos())
                {
                    n++; var e = fsi.FullName;
                    if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (fsi is DirectoryInfo) { if (Sensitive.IsBlockedDir(e)) r.Blocked++; else stack.Push(e); continue; }
                    if (Sensitive.IsBlockedFile(e)) { r.Blocked++; continue; }
                    if (e.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    long len; try { len = ((FileInfo)fsi).Length; } catch { r.Unreadable++; continue; }
                    r.Files.Add((e, Rel(root, e))); r.Bytes += len;
                }
            }
            catch { r.Unreadable++; failed = true; }
            if (!failed && n == 0 && !string.Equals(d, root.Path, StringComparison.OrdinalIgnoreCase)) r.EmptyDirs.Add(Rel(root, d));
        }
        return r;
    }

    static string Rel(ScanRoot root, string full) => root.DestPrefix + "/" + System.IO.Path.GetRelativePath(root.Path, full).Replace('\\', '/');
}

// What a migration may include. Only locations that are safe and well known: no profile-wide copy, no system folders, no credential stores.
public static class MigrationCatalog
{
    static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    public static List<ScanRoot> Personal(bool desktop, bool documents, bool downloads, bool pictures, bool videos, bool music)
    {
        var l = new List<ScanRoot>(); string prof = Env(Environment.SpecialFolder.UserProfile);
        void Add(bool on, string name, string path) { if (on) l.Add(new ScanRoot { Category = "Personal files", Name = name, Path = path, DestPrefix = "UserData/" + name }); }
        Add(desktop, "Desktop", Env(Environment.SpecialFolder.DesktopDirectory));
        Add(documents, "Documents", Env(Environment.SpecialFolder.MyDocuments));
        Add(downloads, "Downloads", System.IO.Path.Combine(prof, "Downloads"));
        Add(pictures, "Pictures", Env(Environment.SpecialFolder.MyPictures));
        Add(videos, "Videos", Env(Environment.SpecialFolder.MyVideos));
        Add(music, "Music", Env(Environment.SpecialFolder.MyMusic));
        return l;
    }

    // Bookmarks only. Passwords, cookies and sign-in sessions are never read.
    public static List<ScanRoot> Browser()
    {
        string la = Env(Environment.SpecialFolder.LocalApplicationData);
        return new()
        {
            new ScanRoot { Category = "Browser data", Name = "Chrome bookmarks", Path = System.IO.Path.Combine(la, "Google", "Chrome", "User Data", "Default", "Bookmarks"), DestPrefix = "Browser/Chrome" },
            new ScanRoot { Category = "Browser data", Name = "Edge bookmarks", Path = System.IO.Path.Combine(la, "Microsoft", "Edge", "User Data", "Default", "Bookmarks"), DestPrefix = "Browser/Edge" },
        };
    }

    // Save files in the two standard Windows places. Games are not installed or "moved": the folder is simply restored for you to copy back.
    public static List<ScanRoot> Games()
    {
        string prof = Env(Environment.SpecialFolder.UserProfile);
        return new()
        {
            new ScanRoot { Category = "Games", Name = "My Games (saves/config)", Path = System.IO.Path.Combine(Env(Environment.SpecialFolder.MyDocuments), "My Games"), DestPrefix = "Games/MyGames" },
            new ScanRoot { Category = "Games", Name = "Saved Games", Path = System.IO.Path.Combine(prof, "Saved Games"), DestPrefix = "Games/SavedGames" },
        };
    }

    // Editor/terminal preferences. Extensions, tokens and sign-in data are not included.
    public static List<ScanRoot> DevSettings()
    {
        string ad = Env(Environment.SpecialFolder.ApplicationData), la = Env(Environment.SpecialFolder.LocalApplicationData);
        return new()
        {
            new ScanRoot { Category = "Development data", Name = "VS Code settings", Path = System.IO.Path.Combine(ad, "Code", "User", "settings.json"), DestPrefix = "DevSettings/VSCode" },
            new ScanRoot { Category = "Development data", Name = "VS Code keybindings", Path = System.IO.Path.Combine(ad, "Code", "User", "keybindings.json"), DestPrefix = "DevSettings/VSCode" },
            new ScanRoot { Category = "Development data", Name = "VS Code snippets", Path = System.IO.Path.Combine(ad, "Code", "User", "snippets"), DestPrefix = "DevSettings/VSCode/snippets" },
            new ScanRoot { Category = "Development data", Name = "Windows Terminal settings", Path = System.IO.Path.Combine(la, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"), DestPrefix = "DevSettings/WindowsTerminal" },
        };
    }
}

// ---------- installed software: compare the old laptop's list with this laptop ----------
public record AppEntry(string Name, string Version, string Publisher);
public enum AppStatus { AlreadyInstalled, OlderVersionHere, ReinstallRequired, SystemComponent, Incompatible, ManualSetup }
public record AppRow(AppEntry App, AppStatus Status, string Here);

public static class SoftwareInventory
{
    public static List<AppEntry> ReadLocal()
    {
        var rows = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path) in new[] {
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall") })
        {
            try
            {
                using var k = hive.OpenSubKey(path); if (k == null) continue;
                foreach (var n in k.GetSubKeyNames())
                {
                    using var sk = k.OpenSubKey(n);
                    if (sk?.GetValue("DisplayName") is string name && name.Trim() != "" && !(sk.GetValue("SystemComponent") is int sc && sc == 1))
                        rows[name.Trim()] = new AppEntry(name.Trim(), sk.GetValue("DisplayVersion") as string ?? "", sk.GetValue("Publisher") as string ?? "");
                }
            }
            catch { }
        }
        return rows.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // installed_apps.txt written by the sender: "name  |  version  |  publisher"
    public static List<AppEntry> ParseExport(string file)
    {
        var l = new List<AppEntry>();
        try
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var p = line.Split('|').Select(x => x.Trim()).ToArray();
                if (p.Length >= 1 && p[0] != "") l.Add(new AppEntry(p[0], p.Length > 1 ? p[1] : "", p.Length > 2 ? p[2] : ""));
            }
        }
        catch { }
        return l;
    }

    static bool IsSystem(AppEntry a) =>
        a.Name.StartsWith("Update for", StringComparison.OrdinalIgnoreCase) || a.Name.StartsWith("Security Update", StringComparison.OrdinalIgnoreCase) ||
        a.Name.StartsWith("Hotfix", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) ||
        a.Name.Contains("Windows SDK", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("WebView2", StringComparison.OrdinalIgnoreCase);

    static Version? V(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s) { if (char.IsDigit(ch) || ch == '.') sb.Append(ch); else break; }
        return Version.TryParse(sb.ToString().Trim('.'), out var v) ? v : null;
    }

    // Statuses we can really decide from the two lists. "Incompatible" and "Manual setup" need an online lookup (winget), which is not done here.
    public static List<AppRow> Compare(List<AppEntry> old, List<AppEntry> here)
    {
        var map = here.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var res = new List<AppRow>();
        foreach (var a in old)
        {
            if (IsSystem(a)) { res.Add(new(a, AppStatus.SystemComponent, "")); continue; }
            if (!map.TryGetValue(a.Name, out var h)) { res.Add(new(a, IncompatibleHere(a) ? AppStatus.Incompatible : AppStatus.ReinstallRequired, "")); continue; }
            var vo = V(a.Version); var vh = V(h.Version);
            res.Add(new(a, vo != null && vh != null && vh < vo ? AppStatus.OlderVersionHere : AppStatus.AlreadyInstalled, h.Version));
        }
        return res;
    }

    // Only one rule we can really decide offline: an app marked x64 cannot run on a 32-bit Windows. Anything else stays "Reinstall required" until checked.
    static bool IncompatibleHere(AppEntry a) =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X86 &&
        (a.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("64-bit", StringComparison.OrdinalIgnoreCase));

    public static string Summary(List<AppRow> rows) =>
        $"{rows.Count(r => r.Status == AppStatus.ReinstallRequired)} not installed here, {rows.Count(r => r.Status == AppStatus.OlderVersionHere)} older than on the old laptop, " +
        $"{rows.Count(r => r.Status == AppStatus.AlreadyInstalled)} already installed, {rows.Count(r => r.Status == AppStatus.SystemComponent)} system components ignored" +
        (rows.Any(r => r.Status == AppStatus.Incompatible) ? $", {rows.Count(r => r.Status == AppStatus.Incompatible)} incompatible with this Windows (64-bit app, 32-bit Windows)." : ".");
}
