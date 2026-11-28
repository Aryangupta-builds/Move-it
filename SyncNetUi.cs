using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoveIt;

// Spec 20: Folder Sync between the two laptops over the MoveIt connection (same code + encryption as a normal transfer).
// Receiving laptop: Settings > "Allow Folder Sync on this laptop" (one chosen folder, this app session only) AND the Receive screen open with its code.
// Sending laptop: Settings > "Folder Sync with the other laptop". Manual only; nothing runs by itself; deletions only for files the user ticks.
public sealed partial class MainWindow
{
    async void AllowSync_Click(object s, RoutedEventArgs e) => await RunTool(async () =>
    {
        if (SyncHost.Root != null)
        {
            if (await Confirm("Folder Sync is allowed", $"The other laptop may sync with:\n{SyncHost.Root}\n\nStop allowing it now?", "Stop allowing", "Keep allowing")) { SyncHost.Root = null; await Msg("Stopped", "Folder Sync is off on this laptop."); }
            return;
        }
        if (!await Confirm("Allow Folder Sync on this laptop?", "You choose ONE folder. While MoveIt is open and on the Receive screen, a laptop that knows your pairing code can add or update files in that folder and read them. Files are only moved to .moveit-sync-trash, never erased, and only deletions you approve on the other laptop are applied. This switches off when MoveIt closes.", "Choose folder", "Cancel")) return;
        var f = await PickDir(); if (f == null) return;
        if (BadSyncFolder(f)) { await Msg("Folder not allowed", "Do not choose a drive, your whole profile, or system/app folders."); return; }
        SyncHost.Root = f;
        await Msg("Folder Sync allowed", $"Folder: {f}\nNow open the Receive screen and give the code to the other laptop.");
    });

    async void SyncRemote_Click(object s, RoutedEventArgs e) => await RunTool(async () =>
    {
        if (!await Confirm("Sync with the other laptop?", "Folder Sync copies new and changed files between a folder here and the allowed folder on the other laptop, only when you run it. The other laptop must be on its Receive screen and have allowed Folder Sync. Not required for normal migration.", "Continue", "Cancel")) return;
        var found = await Sender.Scan();
        if (found.Count == 0) { await Msg("No laptop found", "Open the Receive screen on the other laptop and try again."); return; }
        var pick = new ComboBox { ItemsSource = found.Select(f => $"{f.Name} ({f.Ip})").ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Other laptop" };
        var code = new TextBox { Header = "Pairing code (shown on the other laptop)", MaxLength = 6 };
        var two = new CheckBox { Content = "Two-way (also copy changes from the other laptop back). Default: one-way" };
        var panel = new StackPanel { Spacing = 10 }; panel.Children.Add(pick); panel.Children.Add(code); panel.Children.Add(two);
        if (dialogOpen) return; dialogOpen = true; ContentDialogResult r;
        try { r = await new ContentDialog { Title = "Connect", Content = panel, PrimaryButtonText = "Choose folder", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme }.ShowAsync(); } finally { dialogOpen = false; }
        if (r != ContentDialogResult.Primary || pick.SelectedIndex < 0 || code.Text.Length != 6) return;
        var host = found[pick.SelectedIndex].Ip; bool twoWay = two.IsChecked == true;
        await Msg("Local folder", "Choose the folder on THIS laptop."); var a = await PickDir(); if (a == null) return;
        if (BadSyncFolder(a)) { await Msg("Folder not allowed", "Do not choose a drive, your whole profile, or system/app folders."); return; }
        RemoteSide? remote = null;
        try
        {
            remote = await RemoteSide.Connect(host, code.Text, CancellationToken.None);
            using var local = new LocalSide(a); using var rm = remote;
            string key = "moveit://" + remote.PeerName + "/" + remote.RootName;
            await RunSyncSides(local, rm, a, key, twoWay, remote.Encrypted);
            if (!SyncPairStore.Exists(a, key) && await Confirm("Add to the Folder Sync dashboard?", "Saves the two folders and the other laptop's name (never the pairing code). You can run Sync now from the dashboard later; it never syncs by itself.", "Save pair", "Don't save")) SyncPairStore.Add(a, key, twoWay, remote.PeerName, remote.RootName);
        }
        catch (Exception ex) { await Msg("Folder Sync stopped", ex.Message + "\n\nNothing was deleted. Finished files are kept; run Sync again to continue."); }
    });

    // Preview -> per-file deletion choice -> apply (old versions archived) -> conflict choices -> state saved after everything.
    async Task RunSyncSides(ISyncSide a, ISyncSide b, string keyA, string keyB, bool two, bool encrypted)
    {
        var last = FolderSync.LoadState(keyA, keyB);
        var plan = await FolderSync.PlanAsync(a, b, last, two, CancellationToken.None);
        long freeB = b.FreeSpace(), freeA = a.FreeSpace();
        if ((freeB >= 0 && plan.CopyBytes > freeB) || (two && freeA >= 0 && plan.CopyBytes > freeA)) { await Msg("Not enough space", $"{Fmt(plan.CopyBytes)} may be needed."); return; }
        if (!await Confirm("Preview", $"{(two ? "Two-way" : "One-way")} · {(encrypted ? "encrypted" : "NOT encrypted")}\nTo copy: {plan.Copies.Count} ({Fmt(plan.CopyBytes)})\nUnchanged: {plan.Unchanged}\nConflicts (you choose next): {plan.Conflicts.Count}\nDeletions to review: {plan.Deletions.Count}\n\n{Some(plan.Copies.Select(c => $"{c.Kind} {(c.AToB ? "to " + b.Label : "to " + a.Label)}: {c.Rel}"))}\n\nReplaced files are first kept in .moveit-sync-trash.", "Sync now", "Cancel")) return;
        var approved = await PickDeletions(plan.Deletions);
        long lastTick = 0, bytesDone = 0; int doneFiles = 0; string curRel = "";
        void Prog(string rel, long n)
        {
            if (rel != curRel) { curRel = rel; doneFiles++; }
            bytesDone += n; var now = Environment.TickCount64; if (now - lastTick < 250) return; lastTick = now;
            ShowToolStatus($"Syncing {doneFiles}/{plan.Copies.Count}: {Path.GetFileName(rel)}", plan.CopyBytes > 0 ? bytesDone * 100.0 / plan.CopyBytes : null);
        }
        ShowToolStatus("Syncing…");
        (SyncState state, List<string> errors, int copied) res;
        try { res = await FolderSync.ApplyAsync(a, b, plan, last, approved, true, Prog, CancellationToken.None); } finally { HideToolStatus(); }
        var (state, errors, copied) = res;
        int resolved = 0;
        bool all = false; string allChoice = "";
        foreach (var c in plan.Conflicts)
        {
            string choice = all ? allChoice : await ConflictChoice(a, b, c, plan.Conflicts.Count > 1);
            if (choice.EndsWith("*")) { all = true; allChoice = choice.TrimEnd('*'); choice = allChoice; }
            if (choice == "" || choice == "leave") continue;
            try { var sig = await FolderSync.ResolveAsync(a, b, c, choice, CancellationToken.None); if (sig != null) state.Files[c.Rel] = sig; resolved++; }
            catch (Exception ex) { errors.Add($"{c.Rel}: {ex.Message}"); }
        }
        FolderSync.SaveState(keyA, keyB, state);   // saved AFTER conflicts so resolved files are remembered
        try { Directory.CreateDirectory(ReportStore.Folder); File.AppendAllText(Path.Combine(ReportStore.Folder, "folder-sync-history.txt"), $"{DateTime.Now:g} | {a.Label} <-> {b.Label} | {(two ? "two-way" : "one-way")} | copied {copied}, deletions approved {approved.Count}, conflicts {plan.Conflicts.Count} (resolved {resolved}), errors {errors.Count}\n"); } catch { }
        await Msg(errors.Count == 0 ? "Sync finished" : "Sync finished with errors", $"Copied: {copied}\nConflicts left: {plan.Conflicts.Count - resolved}\nErrors: {errors.Count}\n{Some(errors)}");
    }

    // Per-file deletion choice (spec 20E). Nothing is ticked by default; unticked files stay on both sides.
    async Task<HashSet<string>> PickDeletions(List<SyncChange> dels)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (dels.Count == 0 || dialogOpen) return res;
        var list = new StackPanel { Spacing = 4 }; var boxes = new List<(CheckBox b, string rel)>();
        foreach (var d in dels.Take(500)) { var cb = new CheckBox { Content = d.Rel }; boxes.Add((cb, d.Rel)); list.Children.Add(cb); }
        var all = new Button { Content = "Tick all" }; all.Click += (_, _) => { foreach (var (b, _) in boxes) b.IsChecked = true; };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = $"{dels.Count} file(s) were deleted on one side. Tick the ones whose remaining copy may be moved to .moveit-sync-trash (not erased). Unticked files are kept.{(dels.Count > 500 ? " Only the first 500 are listed; the rest are kept." : "")}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(all); panel.Children.Add(new ScrollViewer { MaxHeight = 280, Content = list });
        dialogOpen = true; ContentDialogResult r;
        try { r = await new ContentDialog { Title = "Review deletions", Content = panel, PrimaryButtonText = "Move ticked files to trash", CloseButtonText = "Keep all files", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme }.ShowAsync(); } finally { dialogOpen = false; }
        if (r == ContentDialogResult.Primary) foreach (var (b, rel) in boxes) if (b.IsChecked == true) res.Add(rel);
        return res;
    }

    // Keep Source / Keep Destination / Keep Both / Review Manually (spec 20F). A trailing "*" means "apply to the remaining conflicts".
    async Task<string> ConflictChoice(ISyncSide a, ISyncSide b, SyncChange c, bool offerAll)
    {
        string Info(FileSig? g) => g == null ? "not available" : $"{Fmt(g.Size)}, changed {new DateTime(g.MTimeTicks, DateTimeKind.Utc).ToLocalTime():g}";
        var sa = await a.Stat(c.Rel, CancellationToken.None); var sb = await b.Stat(c.Rel, CancellationToken.None);
        string pick = "";
        var all = new CheckBox { Content = "Use this choice for the remaining conflicts", Visibility = offerAll ? Visibility.Visible : Visibility.Collapsed };
        ContentDialog? dlg = null; var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button Btn(string text, string key) { var x = new Button { Content = text }; x.Click += (_, _) => { pick = key; dlg!.Hide(); }; return x; }
        row.Children.Add(Btn("Keep source", "source")); row.Children.Add(Btn("Keep destination", "destination")); row.Children.Add(Btn("Keep both", "both")); row.Children.Add(Btn("Review manually", "leave"));
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = $"Source ({a.Label}): {Info(sa)}\nDestination ({b.Label}): {Info(sb)}\n\nKeep source/destination replaces the other version. Keep both saves the destination version as a \"conflict copy\". Review manually changes nothing.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(row); panel.Children.Add(all);
        while (dialogOpen) await Task.Delay(100);
        dlg = new ContentDialog { Title = "Conflict: " + c.Rel, Content = panel, CloseButtonText = "Leave", XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
        dialogOpen = true; try { await dlg.ShowAsync(); } finally { dialogOpen = false; }
        return pick == "" ? "leave" : pick + (all.IsChecked == true && pick != "leave" ? "*" : "");
    }

    // Saved remote pair: find the laptop by name, ask for the code (never stored), run the same safe flow.
    async Task RunRemotePair(SyncPair p)
    {
        var found = await Sender.Scan();
        var hit = found.FirstOrDefault(f => string.Equals(f.Name, p.RemoteName, StringComparison.OrdinalIgnoreCase));
        if (hit.Ip == null) { await Msg("Laptop not found", $"{p.RemoteName} did not answer. Open its Receive screen, allow Folder Sync there, and try again."); return; }
        if (!Directory.Exists(p.A)) { await Msg("Folder missing", "The local folder is not available: " + p.A); return; }
        var code = new TextBox { Header = $"Pairing code shown on {p.RemoteName}", MaxLength = 6 };
        if (dialogOpen) return; dialogOpen = true; ContentDialogResult r;
        try { r = await new ContentDialog { Title = "Sync now", Content = code, PrimaryButtonText = "Connect", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme }.ShowAsync(); } finally { dialogOpen = false; }
        if (r != ContentDialogResult.Primary || code.Text.Length != 6) return;
        try
        {
            using var rm = await RemoteSide.Connect(hit.Ip, code.Text, CancellationToken.None); using var local = new LocalSide(p.A);
            if (!string.Equals(rm.RootName, p.RemoteRoot, StringComparison.OrdinalIgnoreCase)) { await Msg("Different folder", $"The other laptop now allows \"{rm.RootName}\" instead of \"{p.RemoteRoot}\". Nothing was synced. Stop this pair and set up a new one if that is intended."); return; }
            await RunSyncSides(local, rm, p.A, p.B, p.TwoWay, rm.Encrypted);
            SyncPairStore.Touch(p.A, p.B, p.TwoWay, "Finished", 0);
        }
        catch (Exception ex) { await Msg("Folder Sync stopped", ex.Message + "\n\nNothing was deleted. Run Sync again to continue."); }
    }
}
