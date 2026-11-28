using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace MoveIt;

// Spec 9: for apps from the old laptop that are not installed here, ask winget's official source whether an installer exists.
// Found => "Reinstall required (official installer available)". Not found => "Manual setup required". winget unreachable => "Not available", never a guess.
public sealed partial class MainWindow
{
    async void AppSource_Click(object s, RoutedEventArgs e) => await RunTool(async () =>
    {
        var folder = Path.Combine(savedTo != "" ? savedTo : dest, "AppList");
        var oldApps = await Task.Run(() => SoftwareInventory.ParseExport(Path.Combine(folder, "installed_apps.txt")));
        if (oldApps.Count == 0) { await Msg("No app list", "No installed_apps.txt from the old laptop was found in the received folder. Run a complete migration first."); return; }
        var rows = await Task.Run(() => SoftwareInventory.Compare(oldApps, SoftwareInventory.ReadLocal()));
        var todo = rows.Where(r => r.Status == AppStatus.ReinstallRequired).Take(20).ToList();
        if (todo.Count == 0) { await Msg("Apps", SoftwareInventory.Summary(rows)); return; }
        if (!await Confirm("Check official source?", $"MoveIt will ask winget (official source only) about {todo.Count} app(s) that are not installed here. Nothing is installed. This can take about a minute.", "Check", "Cancel")) return;
        var found = new List<string>(); var manual = new List<string>(); var unknown = new List<string>();
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        int n = 0;
        try
        {
            foreach (var r in todo)
            {
                ShowToolStatus($"Asking winget {n + 1}/{todo.Count}: {r.App.Name}", n * 100.0 / todo.Count); n++;
                bool? ok = await Task.Run(() => WingetHas(r.App.Name, total.Token));
                (ok == true ? found : ok == false ? manual : unknown).Add($"{r.App.Name} {r.App.Version}".Trim());
            }
        }
        finally { HideToolStatus(); }
        string L(List<string> l) => l.Count == 0 ? "none" : string.Join(", ", l.Take(12)) + (l.Count > 12 ? $" (+{l.Count - 12} more)" : "");
        await Msg("Apps and the official source",
            $"Reinstall required, official installer available: {L(found)}\n\nManual setup required (not in winget; use the publisher's website): {L(manual)}\n\nNot available (winget did not answer): {L(unknown)}\n\n" +
            (rows.Count(r => r.Status == AppStatus.ReinstallRequired) > todo.Count ? "Only the first 20 apps were checked. " : "") + "Licences and sign-ins are never transferred.");
    });

    static bool? WingetHas(string name, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("winget") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            foreach (var a in new[] { "search", "--name", name, "--exact", "--source", "winget" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi); if (p == null) return null;
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(15000) || ct.IsCancellationRequested) { try { p.Kill(true); } catch { } return null; }
            var o = read.GetAwaiter().GetResult();
            if (o.Contains("No package found", StringComparison.OrdinalIgnoreCase)) return false;
            return p.ExitCode == 0 ? true : p.ExitCode == unchecked((int)0x8A150014) ? false : null;   // APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND
        }
        catch { return null; }
    }
}
