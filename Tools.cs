using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MoveIt;

// Spec 8 (account setup), 15 (backup and rollback), 12/21 (connection overview).
public sealed partial class MainWindow
{
    bool toolBusy;

    async Task<string?> PickDir()
    {
        var p = Init(new FolderPicker()); p.FileTypeFilter.Add("*");
        return (await p.PickSingleFolderAsync())?.Path;
    }

    async Task RunTool(Func<Task> work)
    {
        if (toolBusy) return;
        toolBusy = true; ToolsCard.Opacity = 0.6;
        try { await work(); }
        catch (Exception ex) { await Msg("Could not finish", ex.Message); }
        finally { toolBusy = false; ToolsCard.Opacity = 1; }
    }

    static string Some(IEnumerable<string> l) => string.Join("\n", l.Take(8)) + (l.Count() > 8 ? $"\n…and {l.Count() - 8} more" : "");

    async void BackupCreate_Click(object s, RoutedEventArgs e) => await RunTool(async () =>
    {
        var p = Init(new FileOpenPicker()); p.FileTypeFilter.Add("*");
        var files = (await p.PickMultipleFilesAsync()).Select(f => f.Path).ToList();
        if (files.Count == 0) return;
        var root = await PickDir(); if (root == null) return;
        if (!await Confirm("Create backup", $"{files.Count} file(s), {Fmt(BackupService.RequiredBytes(files))} needed.\nSave to: {root}\n\nOnly these files are backed up. This is not a full system backup.", "Back up", "Cancel")) return;
        var (m, bad) = await WithStatus("Creating and verifying the backup…", () => { var x = BackupService.Create(root, files, CancellationToken.None); return (x, BackupService.Verify(Path.Combine(root, "MoveIt_Backup_" + x.Id))); });
        await Msg(bad.Count == 0 ? "Backup verified" : "Backup has problems", $"{m.Entries.Count} file(s) in MoveIt_Backup_{m.Id}.\n{Some(bad)}");
    });

    async void BackupRestore_Click(object s, RoutedEventArgs e) => await RunTool(async () =>
    {
        var dir = await PickDir(); if (dir == null) return;
        var m = BackupService.Load(dir);
        if (m == null || m.Entries.Count == 0) { await Msg("Not a backup folder", "Choose a MoveIt_Backup_… folder that contains manifest.json."); return; }
        var bad = await WithStatus("Checking the backup…", () => BackupService.Verify(dir));
        if (!await Confirm("Restore files?", $"{m.Entries.Count} file(s) will be replaced at their original paths. Later changes to these files will be lost.\nDamaged backup copies (will be skipped): {bad.Count}\n\n{Some(m.Entries.Select(x => x.OriginalPath))}", "Restore", "Cancel")) return;
        var r = await WithStatus("Restoring and verifying files…", () => BackupService.Restore(dir, CancellationToken.None));
        await Msg(r.Failed.Count == 0 ? "Restore complete" : "Restore completed with problems", $"Restored and verified: {r.Restored}\nFailed: {r.Failed.Count}\n{Some(r.Failed)}");
    });

    async void Links_Click(object s, RoutedEventArgs e) => await RunTool(async () =>
    {
        var l = await Task.Run(() => Connections.Detect());
        var wired = await Task.Run(() => WiredStatus.Evaluate(false, null));
        await Msg("Connections", $"{Connections.Describe(l)}\n\nWired status: {wired.Text}\n\nSuggested: {Connections.Recommend(l)?.Kind ?? "Not available"}\nWindows chooses the network route. MoveIt does not force an adapter.");
    });

    async void Accounts_Click(object s, RoutedEventArgs e) => await RunTool(async () =>
    {
        if (dialogOpen) return;
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "accounts.json");
        HashSet<string> done = new();
        try { done = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(file)) ?? new(); } catch { }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var list = new (string Name, string Url, string AppDir)[]
        {
            ("Google", "https://accounts.google.com", Path.Combine(local, "Google", "Chrome")),
            ("Microsoft", "https://account.microsoft.com", Path.Combine(local, "Microsoft", "Edge")),
            ("Steam", "https://store.steampowered.com/login", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")),
            ("Epic Games", "https://www.epicgames.com/id/login", Path.Combine(local, "EpicGamesLauncher"))
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Sign in yourself on this laptop. MoveIt never copies passwords, tokens or sessions.", TextWrapping = TextWrapping.Wrap });
        var boxes = new List<(string, CheckBox)>();
        foreach (var a in list)
        {
            var state = Directory.Exists(a.AppDir) ? "Ready to Sign In" : "Setup Required";
            var box = new CheckBox { Content = $"{a.Name}: {(done.Contains(a.Name) ? "Successfully Configured (marked by you)" : state + " (tick after you sign in)")}", IsChecked = done.Contains(a.Name) };
            panel.Children.Add(box); boxes.Add((a.Name, box));
            panel.Children.Add(new HyperlinkButton { Content = "Open official sign-in", NavigateUri = new Uri(a.Url) });
        }
        panel.Children.Add(new TextBlock { Text = "Saved passwords, cookies and login sessions: Unsupported (never transferred).", TextWrapping = TextWrapping.Wrap });
        dialogOpen = true;
        try { await new ContentDialog { Title = "Account setup", Content = new ScrollViewer { Content = panel }, CloseButtonText = "Save and close", XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme }.ShowAsync(); }
        finally { dialogOpen = false; }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(boxes.Where(b => b.Item2.IsChecked == true).Select(b => b.Item1)));
    });
}
