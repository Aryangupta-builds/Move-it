using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoveIt;

// Spec 9: dedicated Software migration page. Compares the old laptop's list (AppList/installed_apps.txt in the received folder) with this laptop.
// Statuses: Already installed, Reinstall required, Update available, Incompatible, Manual setup required, System component (ignored).
// "Unsupported" is not a separate state: an app MoveIt cannot judge stays "Reinstall required" until the official source (winget) was checked.
public sealed partial class MainWindow
{
    List<AppRow> swRows = new();
    readonly Dictionary<string, string> swNotes = new(StringComparer.OrdinalIgnoreCase);   // name -> "official installer available" / "not in winget" / "winget did not answer"

    static string SwLabel(AppStatus s) => s switch
    {
        AppStatus.AlreadyInstalled => "Already installed",
        AppStatus.ReinstallRequired => "Reinstall required",
        AppStatus.OlderVersionHere => "Update available",
        AppStatus.Incompatible => "Incompatible",
        AppStatus.ManualSetup => "Manual setup required",
        _ => "System component (ignored)"
    };

    void Software_Click(object s, RoutedEventArgs e) { ShowPage(SoftwarePage); _ = LoadSoftware(); }
    void SwReload_Click(object s, RoutedEventArgs e) => _ = LoadSoftware();
    void SwFilter_Changed(object s, SelectionChangedEventArgs e) { if (SwList != null) RenderSoftware(); }
    void SwSearch_Changed(object s, TextChangedEventArgs e) { if (SwList != null) RenderSoftware(); }

    async Task LoadSoftware()
    {
        SwSummary.Text = "Reading the app lists…"; SwList.ItemsSource = null; swNotes.Clear();
        var file = Path.Combine(savedTo != "" ? savedTo : dest, "AppList", "installed_apps.txt");
        var oldApps = await Task.Run(() => SoftwareInventory.ParseExport(file));
        if (oldApps.Count == 0) { swRows = new(); SwSummary.Text = "No app list from the old laptop was found in the received folder. Run a complete migration with \"Installed apps list\" ticked first."; return; }
        swRows = await Task.Run(() => SoftwareInventory.Compare(oldApps, SoftwareInventory.ReadLocal()));
        RenderSoftware();
    }

    void RenderSoftware()
    {
        var f = SwFilter.SelectedItem as string ?? "All"; var q = (SwSearch.Text ?? "").Trim();
        var shown = swRows.Where(r => (f == "All" || SwLabel(r.Status) == f) && (q == "" || r.App.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.App.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)));
        SwList.ItemsSource = shown.Select(SwLine).ToList();
        SwSummary.Text = swRows.Count == 0 ? SwSummary.Text : SoftwareInventory.Summary(swRows) + $"  Showing {((System.Collections.IList)SwList.ItemsSource).Count} of {swRows.Count}.";
    }

    string SwLine(AppRow r)
    {
        var a = r.App; var note = swNotes.TryGetValue(a.Name, out var n) ? "  [" + n + "]" : "";
        return $"{SwLabel(r.Status)}  •  {a.Name} {a.Version}{(a.Publisher != "" ? "  (" + a.Publisher + ")" : "")}{(r.Here != "" ? "  — here: " + r.Here : "")}{note}";
    }

    async void SwCheck_Click(object s, RoutedEventArgs e)
    {
        var todo = swRows.Where(r => r.Status == AppStatus.ReinstallRequired).Take(20).ToList();
        if (todo.Count == 0) { await Msg("Nothing to check", "No app is marked \"Reinstall required\"."); return; }
        if (!await Confirm("Check the official source?", $"MoveIt asks winget (official source only) about {todo.Count} app(s). Nothing is installed. This can take about a minute.", "Check", "Cancel")) return;
        SwCheckBtn.IsEnabled = false; SwSummary.Text = "Asking winget…";
        try
        {
            using var total = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            int n = 0;
            foreach (var r in todo)
            {
                ShowToolStatus($"Asking winget {n + 1}/{todo.Count}: {r.App.Name}", n * 100.0 / todo.Count); n++;
                bool? ok = await Task.Run(() => WingetHas(r.App.Name, total.Token));
                swNotes[r.App.Name] = ok == true ? "official installer available" : ok == false ? "not in winget: use the publisher's website" : "winget did not answer";
                if (ok == false) { int i = swRows.FindIndex(x => x.App.Name == r.App.Name); if (i >= 0) swRows[i] = swRows[i] with { Status = AppStatus.ManualSetup }; }
            }
        }
        finally { HideToolStatus(); SwCheckBtn.IsEnabled = true; RenderSoftware(); }
    }

    async void SwSave_Click(object s, RoutedEventArgs e)
    {
        if (swRows.Count == 0) { await Msg("Nothing to save", "No app list is loaded."); return; }
        try
        {
            Directory.CreateDirectory(ReportStore.Folder);
            var f = Path.Combine(ReportStore.Folder, $"software-migration-{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllLinesAsync(f, new[] { "MOVE IT software migration list", SoftwareInventory.Summary(swRows), "" }.Concat(swRows.Select(SwLine)));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f) { UseShellExecute = true });
        }
        catch (Exception ex) { await Msg("Could not save", ex.Message); }
    }
}
