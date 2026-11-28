using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;

namespace MoveIt;

public record DeviceItem(string Ip, string Name);

public class AppSettings
{
    public string Theme { get; set; } = "Default";
    public string DeviceName { get; set; } = "";
    public string SaveFolder { get; set; } = "";
    public bool OpenWhenDone { get; set; } = true;
    public bool SetupDone { get; set; } = false;
    public bool MobileShare { get; set; } = true;
    public bool AllowPlain { get; set; } = true;     // spec 26: allow unencrypted TCP with an older MoveIt that cannot encrypt
    public bool TrayEnabled { get; set; } = false;   // off by default: closing the window exits the app
    public string UpdateUrl { get; set; } = "https://api.github.com/repos/Aryangupta-builds/Move-it/releases/latest";   // only contacted when the user presses "Check for updates"; clear it to disable
}

// One row in the Send file list
public sealed class SendItem
{
    public string FullPath { get; init; } = "";
    public bool Folder { get; init; }
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public long Size { get; init; }
    public string Glyph => Folder ? "\uE8B7" : "\uE8A5";
}

public record HistoryEntry(DateTime Time, string Direction, string Device, int Files, long Size, string Status);

// Display model for the History page
public sealed class HistoryRow
{
    public string Glyph { get; init; } = "";
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Status { get; init; } = "";
    public Brush StatusBrush { get; init; } = null!;

    public static HistoryRow From(HistoryEntry e)
    {
        var col = e.Status == "Completed" ? Windows.UI.Color.FromArgb(255, 16, 185, 129)
                : e.Status == "Cancelled" || e.Status == "Partial" ? Windows.UI.Color.FromArgb(255, 245, 158, 11)
                : Windows.UI.Color.FromArgb(255, 239, 68, 68);
        bool sent = e.Direction == "Sent";
        return new HistoryRow
        {
            Glyph = sent ? "\uE898" : "\uE896",
            Title = sent ? $"Sent to {e.Device}" : $"Received from {e.Device}",
            Sub = $"{e.Time:d MMM yyyy, h:mm tt}  •  {e.Files} file{(e.Files == 1 ? "" : "s")}  •  {AppInfo.Fmt(e.Size)}",
            Status = e.Status, StatusBrush = new SolidColorBrush(col)
        };
    }
}

public static class AppInfo
{
    public static string Version
    {
        get { var v = Assembly.GetExecutingAssembly().GetName().Version; return v == null ? "1.4.0" : $"{v.Major}.{v.Minor}.{v.Build}"; }
    }

    public static string Fmt(double n)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
        while (n >= 1024 && i < 4) { n /= 1024; i++; }
        return $"{n:0.#} {u[i]}";
    }

    public static string Eta(long left, double speed)
    {
        if (speed < 1 || left <= 0) return "—";
        int s = (int)Math.Min(left / speed, 359999);
        if (s < 60) return $"{Math.Max(s, 1)} s";
        if (s < 3600) return $"{s / 60} min {s % 60} s";
        return $"{s / 3600} h {(s % 3600) / 60} min";
    }

    public static string FormatOf(string name)
    {
        var e = Path.GetExtension(name).TrimStart('.');
        return e == "" ? "File" : e.ToUpperInvariant();
    }
}

// Speed from real byte counts, smoothed (exponential moving average) so speed/ETA don't jump around.
public sealed class RateMeter
{
    long lastBytes, lastMs; double ema; bool init;
    public double Speed => ema;
    public void Reset() { init = false; ema = 0; }
    public double Update(long bytes, long nowMs)
    {
        if (!init) { init = true; lastBytes = bytes; lastMs = nowMs; return 0; }
        long dt = nowMs - lastMs; if (dt < 300) return ema;
        double inst = Math.Max(0, bytes - lastBytes) * 1000.0 / dt;
        ema = ema <= 0 ? inst : ema * 0.7 + inst * 0.3;
        lastBytes = bytes; lastMs = nowMs; return ema;
    }
}

public static class HistoryStore
{
    static readonly object gate = new();
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "history.json");

    public static List<HistoryEntry> Load()
    {
        lock (gate)
        {
            try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? new(); }
            catch { }
            return new();
        }
    }

    static void Save(List<HistoryEntry> l)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(l));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    public static void Add(HistoryEntry e)
    {
        lock (gate)
        {
            var l = Load(); l.Insert(0, e);
            if (l.Count > 200) l.RemoveRange(200, l.Count - 200);
            Save(l);
        }
    }

    public static void Clear() { lock (gate) Save(new()); }
}
