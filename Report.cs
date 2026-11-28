using System.Text;

namespace MoveIt;

// Plain-text transfer report. Contains counts, device names and the list of problems only: no file contents, no passwords or tokens.
public static class ReportStore
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "Reports");

    public static string Build(string kind, string from, string to, DateTime start, DateTime end, long bytes, SendReport? r, string finalStatus, string method, IEnumerable<string>? categories = null, IEnumerable<string>? excluded = null, int blocked = 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"MoveIt {AppInfo.Version} - {kind} report");
        sb.AppendLine($"From: {from}\nTo: {to}\nMethod: {method}\nStarted: {start:yyyy-MM-dd HH:mm:ss}\nFinished: {end:yyyy-MM-dd HH:mm:ss}\nFinal status: {finalStatus}");
        sb.AppendLine($"Data: {AppInfo.Fmt(bytes)}");
        if (r != null)
        {
            sb.AppendLine($"Files sent and confirmed: {r.Sent}\nNot sent: {r.NotSent} (unreadable {r.Unreadable}, changed while sending {r.Changed}, not saved by receiver {r.PeerSkipped}, failed integrity check {r.PeerFailed})");
            sb.AppendLine($"Empty folders created: {r.EmptyFolders}\nIntegrity check (SHA-256 per file): {(r.Checked ? "on" : "not available - the other laptop runs an older MoveIt")}");
        }
        if (blocked > 0) sb.AppendLine($"Items left out for safety (passwords, keys, tokens): {blocked}");
        if (categories != null) sb.AppendLine("Included: " + string.Join(", ", categories));
        if (excluded != null && excluded.Any()) sb.AppendLine("Not selected: " + string.Join(", ", excluded));
        if (r != null && r.Problems.Count > 0) { sb.AppendLine("\nProblems:"); foreach (var p in r.Problems.Take(200)) sb.AppendLine(" - " + p); }
        return sb.ToString();
    }

    public static string? Save(string text)
    {
        try { Directory.CreateDirectory(Folder); var p = Path.Combine(Folder, "report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"); File.WriteAllText(p, text); return p; }
        catch { return null; }
    }
}
