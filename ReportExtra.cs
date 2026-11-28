namespace MoveIt;

// Spec 24: reports for receive, queue, and cancelled or failed sends. Names and counts only, never file contents.
public sealed partial class MainWindow
{
    static void SaveSimpleReport(string kind, string from, string to, DateTime start, int selected, int? ok, int? failed, int? skipped, long bytes, string status, IEnumerable<string>? problems = null)
    {
        static string N(int? v) => v?.ToString() ?? "Not available";
        var text = $"MoveIt {AppInfo.Version} - {kind} report\nFrom: {from}\nTo: {to}\nMethod: Local network (TCP)\nStarted: {start:yyyy-MM-dd HH:mm:ss}\nFinished: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                   $"Selected: {selected}\nSuccessful: {N(ok)}\nFailed: {N(failed)}\nSkipped: {N(skipped)}\nSelected data: {AppInfo.Fmt(bytes)}\nFinal status: {status}\n";
        var list = problems?.Take(200).ToList();
        if (list is { Count: > 0 }) text += "\nProblems:\n - " + string.Join("\n - ", list) + "\n";
        ReportStore.Save(text);
    }
}
