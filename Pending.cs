using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoveIt;

// Spec 22: transfer manifest. Only paths and sizes are stored (never file contents). Written when a send starts, removed after a clean finish,
// kept when the transfer was cancelled, failed or only partly sent, so it can be resumed.
public sealed class PendingTransfer
{
    public string Target { get; set; } = "";
    public DateTime Started { get; set; }
    public List<string[]> Files { get; set; } = new();   // [path, relative path]
    public List<string> EmptyDirs { get; set; } = new();
}

public static class PendingStore
{
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "pending_transfer.json");

    public static void Save(string target, List<(string p, string rel)> files, IReadOnlyList<string>? dirs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var m = new PendingTransfer { Target = target, Started = DateTime.Now, Files = files.Select(f => new[] { f.p, f.rel }).ToList(), EmptyDirs = dirs?.ToList() ?? new() };
            var tmp = FilePath + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(m)); File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    public static PendingTransfer? Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<PendingTransfer>(File.ReadAllText(FilePath)) : null; } catch { return null; }
    }

    public static void Clear() { try { File.Delete(FilePath); } catch { } }
}

public sealed partial class MainWindow
{
    List<(string p, string rel)>? resumeFiles; List<string>? resumeDirs;

    async void Resume_Click(object s, RoutedEventArgs e)
    {
        if (Sending || dialogOpen) return;
        if (targetIp == "") { await Msg("Not connected", "Connect to the other laptop first (it must be on the Receive screen)."); return; }
        var m = PendingStore.Load();
        if (m == null || m.Files.Count == 0) { await Msg("Nothing to resume", "There is no interrupted transfer saved."); return; }
        var button = (Button)s; button.IsEnabled = false;
        try
        {
            var all = m.Files.Where(f => f.Length == 2).Select(f => (p: f[0], rel: f[1])).ToList();
            var existing = all.Where(f => File.Exists(f.p)).ToList(); int gone = all.Count - existing.Count;
            button.Content = "Comparing with the other laptop…";
            HashSet<string> verified;
            using (var to = new CancellationTokenSource(TimeSpan.FromMinutes(10))) verified = await Sender.FindVerified(targetIp, targetCode, existing, to.Token);
            var rest = existing.Where(f => !verified.Contains(f.rel)).ToList();
            if (rest.Count == 0)
            {
                PendingStore.Clear();
                await Msg("Nothing left to send", $"All {verified.Count} file(s) are already on {targetName} and match (SHA-256 checked)." + (gone > 0 ? $"\n{gone} file(s) no longer exist on this laptop." : ""));
                return;
            }
            if (!await Confirm("Resume interrupted transfer?", $"Started: {m.Started:g} (to {m.Target})\nAlready on the other laptop and verified: {verified.Count}\nStill to send: {rest.Count}" + (gone > 0 ? $"\nNo longer on this laptop (skipped): {gone}" : "") + "\n\nVerified files are not sent again. Nothing on this laptop is deleted.", "Resume", "Cancel")) return;
            resumeFiles = rest; resumeDirs = m.EmptyDirs;
            Send_Click(s, e);
        }
        catch (Exception ex) { await Msg("Cannot resume", ex.Message); }
        finally { button.IsEnabled = true; button.Content = "Resume interrupted transfer"; }
    }
}
