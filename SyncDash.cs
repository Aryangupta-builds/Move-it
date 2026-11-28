using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoveIt;

// Spec 20: saved folder pairs. A pair is only stored after the user finished the consent/setup flow and agreed to save it. Nothing here syncs by itself.
public sealed class SyncPair
{
    public string A { get; set; } = ""; public string B { get; set; } = "";
    public bool TwoWay { get; set; }
    public bool Paused { get; set; }
    public bool Remote { get; set; }             // B is a folder on the other laptop (reached over MoveIt). The pairing code is NEVER stored: it is asked at every sync.
    public string RemoteName { get; set; } = "";  // other laptop name + its allowed folder name, e.g. "LAPTOP-B"
    public string RemoteRoot { get; set; } = "";
    public bool AutoApply { get; set; }          // explicit consent given: copies NEW/MODIFIED files only (old version kept in .moveit-sync-trash); never deletes, never touches conflicts
    public bool AutoCheck { get; set; }          // periodic CHECK only (shows pending changes); it never copies or deletes
    public DateTime? LastSync { get; set; }
    public string LastResult { get; set; } = "";
    public int LastErrors { get; set; }
    public int LastPending { get; set; } = -1;   // -1 = not checked
    public DateTime? LastChecked { get; set; }
}

public static class SyncPairStore
{
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "sync_pairs.json");
    static bool Same(SyncPair p, string a, string b) => string.Equals(p.A, a, StringComparison.OrdinalIgnoreCase) && string.Equals(p.B, b, StringComparison.OrdinalIgnoreCase);

    public static List<SyncPair> Load() { try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<SyncPair>>(File.ReadAllText(FilePath)) ?? new() : new(); } catch { return new(); } }
    public static void Save(List<SyncPair> l)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); var tmp = FilePath + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(l)); File.Move(tmp, FilePath, true); } catch { }
    }
    public static string RemoteKey(string name, string root) => "moveit://" + name + "/" + root;
    public static void Add(string a, string b, bool two, string? remoteName = null, string? remoteRoot = null)
    { var l = Load(); if (!l.Any(p => Same(p, a, b))) { l.Add(new SyncPair { A = a, B = b, TwoWay = two, Remote = remoteName != null, RemoteName = remoteName ?? "", RemoteRoot = remoteRoot ?? "" }); Save(l); } }
    public static bool Exists(string a, string b) => Load().Any(p => Same(p, a, b));
    public static void Touch(string a, string b, bool two, string result, int errors)
    {
        var l = Load(); var p = l.FirstOrDefault(x => Same(x, a, b)); if (p == null) return;
        p.LastSync = DateTime.Now; p.LastResult = result; p.LastErrors = errors; p.TwoWay = two; Save(l);
    }
    public static void Remove(SyncPair x) { var l = Load(); l.RemoveAll(p => Same(p, x.A, x.B)); Save(l); }
}

public sealed partial class MainWindow
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? syncTimer; bool syncChecking;

    // Periodic check only, and only for pairs the user switched on. Never copies, replaces or deletes. Not real-time.
    void InitSyncWatch()
    {
        syncTimer = DispatcherQueue.CreateTimer(); syncTimer.Interval = TimeSpan.FromMinutes(15);
        syncTimer.Tick += async (_, _) =>
        {
            if (syncChecking || Sending || rxReceiving) return;
            var list = SyncPairStore.Load(); if (!list.Any(p => p.AutoCheck && !p.Paused && !p.Remote)) return;
            syncChecking = true;
            try
            {
                foreach (var p in list.Where(x => x.AutoCheck && !x.Paused && !x.Remote))
                {
                    int n = await Task.Run(async () =>
                    {
                        try
                        {
                            if (!Directory.Exists(p.A) || !Directory.Exists(p.B)) return -1;
                            var last = FolderSync.LoadState(p.A, p.B); using var la = new LocalSide(p.A); using var lb = new LocalSide(p.B);
                            var plan = await FolderSync.PlanAsync(la, lb, last, p.TwoWay, CancellationToken.None);
                            if (p.AutoApply && plan.Copies.Count > 0)
                            {
                                long fb = Net.FreeSpace(p.B), fa = Net.FreeSpace(p.A);
                                if ((fb < 0 || plan.CopyBytes <= fb) && (!p.TwoWay || fa < 0 || plan.CopyBytes <= fa))
                                {
                                    plan.Deletions.Clear();   // deletions are never automatic
                                    var (st, errs, copied) = await FolderSync.ApplyAsync(la, lb, plan, last, null, true, null, CancellationToken.None);
                                    FolderSync.SaveState(p.A, p.B, st);
                                    try { Directory.CreateDirectory(ReportStore.Folder); File.AppendAllText(Path.Combine(ReportStore.Folder, "folder-sync-history.txt"), $"{DateTime.Now:g} | AUTO | {p.A} -> {p.B} | copied {copied}, errors {errs.Count}\n"); } catch { }
                                    p.LastSync = DateTime.Now; p.LastResult = errs.Count == 0 ? "Auto sync finished" : "Auto sync with errors"; p.LastErrors = errs.Count;
                                    return plan.Conflicts.Count;
                                }
                            }
                            return plan.Pending;
                        }
                        catch { return -1; }
                    });
                    p.LastPending = n; p.LastChecked = DateTime.Now;
                }
                var fresh = SyncPairStore.Load();   // keep changes the user made meanwhile
                foreach (var p in list) { var f = fresh.FirstOrDefault(x => x.A == p.A && x.B == p.B); if (f != null) { f.LastPending = p.LastPending; f.LastChecked = p.LastChecked; f.LastSync = p.LastSync ?? f.LastSync; if (p.LastResult != "") f.LastResult = p.LastResult; f.LastErrors = p.LastErrors; } }
                SyncPairStore.Save(fresh);
            }
            finally { syncChecking = false; }
        };
        syncTimer.Start();
    }

    async Task OfferSavePair(string a, string b, bool two)
    {
        if (SyncPairStore.Exists(a, b)) return;
        if (await Confirm("Add to the Folder Sync dashboard?", "The dashboard lets you run Sync now, pause, resume or stop this pair and see its history. A saved pair never syncs by itself. You can stop it any time.", "Save pair", "Don't save"))
            SyncPairStore.Add(a, b, two);
    }

    static async Task<string> PairStatus(SyncPair p)
    {
        if (p.Paused) return "Status: Paused";
        if (p.Remote) return $"Status: Remote pair with {p.RemoteName}. It is checked only when you press Sync now (the other laptop must be on its Receive screen, have allowed Folder Sync, and you enter its code).";
        return await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(p.A) || !Directory.Exists(p.B)) return "Status: Disconnected (a folder is not reachable)";
                var plan = FolderSync.Plan(p.A, p.B, FolderSync.LoadState(p.A, p.B), p.TwoWay);
                int Count(ChangeKind k) => plan.Copies.Count(c => c.Kind == k);
                return $"Status: Ready\nPending changes: {plan.Pending}\nNew files: {Count(ChangeKind.New)}\nModified files: {Count(ChangeKind.Modified)}\nRenamed: {Count(ChangeKind.Renamed)}\nConflicts: {plan.Conflicts.Count}\nDeletions to review: {plan.Deletions.Count}";
            }
            catch (Exception ex) { return "Status: Error: " + ex.Message; }
        });
    }

    async void SyncDash_Click(object s, RoutedEventArgs e) => await RunTool(async () =>
    {
        if (dialogOpen) return;
        int idx = 0;
        while (true)
        {
            var pairs = SyncPairStore.Load();
            if (pairs.Count == 0) { await Msg("No saved folder pairs", "Folder Sync is off. Use \"Folder Sync (optional)\" to set up a pair; it is never enabled automatically."); return; }
            idx = Math.Clamp(idx, 0, pairs.Count - 1);
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = pairs.Select(p => $"{p.A}  {(p.TwoWay ? "↔" : "→")}  {p.B}").ToList(), SelectedIndex = idx };
            var info = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Checking…", IsTextSelectionEnabled = true };
            var auto = new CheckBox { Content = "Check for changes every 15 min while MoveIt is open (only shows pending changes, never copies)" };
            var autoApply = new CheckBox { Content = "Apply new/changed files automatically every 15 min while MoveIt is open (never deletes, never touches conflicts)" };
            string? act = null; ContentDialog? dlg = null;
            Button Btn(string text, string key) { var b = new Button { Content = text }; b.Click += (_, _) => { act = key; dlg!.Hide(); }; return b; }
            async Task Refresh()
            {
                var p = pairs[Math.Max(combo.SelectedIndex, 0)]; idx = Math.Max(combo.SelectedIndex, 0);
                auto.IsChecked = p.AutoCheck; autoApply.IsChecked = p.AutoApply; info.Text = "Checking…";
                var st = await PairStatus(p);
                info.Text = $"Source: {p.A}\nDestination: {p.B}\nMode: {(p.TwoWay ? "Two-way" : "One-way")}\nLast sync: {(p.LastSync?.ToString("g") ?? "never")}{(p.LastResult != "" ? " (" + p.LastResult + ")" : "")}\nLast errors: {p.LastErrors}\n"
                    + st + (p.AutoCheck && p.LastPending >= 0 ? $"\nLast automatic check: {p.LastPending} pending ({p.LastChecked:g})" : "");
            }
            combo.SelectionChanged += async (_, _) => await Refresh();
            auto.Click += (_, _) => { var l = SyncPairStore.Load(); var q = l.FirstOrDefault(x => x.A == pairs[idx].A && x.B == pairs[idx].B); if (q != null) { q.AutoCheck = auto.IsChecked == true; SyncPairStore.Save(l); pairs[idx].AutoCheck = q.AutoCheck; } };
            autoApply.Click += (_, _) =>
            {
                bool want = autoApply.IsChecked == true;
                if (want) { autoApply.IsChecked = false; act = "autoapply"; dlg!.Hide(); return; }   // consent is asked after this dialog closes
                var l = SyncPairStore.Load(); var q = l.FirstOrDefault(x => x.A == pairs[idx].A && x.B == pairs[idx].B); if (q != null) { q.AutoApply = false; SyncPairStore.Save(l); }
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(Btn("Sync now", "sync")); row.Children.Add(Btn(pairs[idx].Paused ? "Resume" : "Pause", "pause")); row.Children.Add(Btn("Change folders", "change")); row.Children.Add(Btn("Stop sync", "stop")); row.Children.Add(Btn("History", "history"));
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(combo); panel.Children.Add(info); panel.Children.Add(auto); panel.Children.Add(autoApply); panel.Children.Add(row);
            dlg = new ContentDialog { Title = "Folder Sync dashboard", Content = panel, CloseButtonText = "Close", XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
            _ = Refresh();
            dialogOpen = true;
            try { await dlg.ShowAsync(); } finally { dialogOpen = false; }
            if (act == null) return;
            var cur = pairs[idx];
            if (act == "autoapply")
            {
                if (cur.Remote) { await Msg("Not available", "Automatic apply is only for folders this PC can reach. Remote pairs need the pairing code each time."); continue; }
                if (await Confirm("Turn on automatic apply?", "While MoveIt is open, every 15 minutes this pair copies NEW and CHANGED files by itself. A replaced file's old version is kept in .moveit-sync-trash. Deletions and conflicts are never automatic: they wait for you. It is not real-time, and it stops when MoveIt closes. Pause or Stop sync any time.", "Turn on", "Keep manual"))
                { var l = SyncPairStore.Load(); var q = l.FirstOrDefault(x => x.A == cur.A && x.B == cur.B); if (q != null) { q.AutoApply = true; q.AutoCheck = true; SyncPairStore.Save(l); } }
            }
            else if (act == "pause") { var l = SyncPairStore.Load(); var q = l.FirstOrDefault(x => x.A == cur.A && x.B == cur.B); if (q != null) { q.Paused = !q.Paused; SyncPairStore.Save(l); } }
            else if (act == "change")
            {
                if (cur.Remote) { await Msg("Remote pair", "To use other folders, press Stop sync and set up a new pair."); continue; }
                if (!await Confirm("Change the folders of this pair?", "You will choose a new source and destination. The old pair is replaced and nothing is copied, replaced or deleted now. The new pair starts with no history: files that exist on both sides but differ are shown as conflicts for you to decide, and nothing is overwritten without your confirmation.", "Change folders", "Cancel")) continue;
                await Msg("Step 1", "Choose the new SOURCE folder."); var na = await PickDir(); if (na == null) continue;
                await Msg("Step 2", "Choose the new DESTINATION folder."); var nb = await PickDir(); if (nb == null) continue;
                string xa = Path.TrimEndingDirectorySeparator(na) + "\\", xb = Path.TrimEndingDirectorySeparator(nb) + "\\";
                if (BadSyncFolder(na) || BadSyncFolder(nb) || xa.StartsWith(xb, StringComparison.OrdinalIgnoreCase) || xb.StartsWith(xa, StringComparison.OrdinalIgnoreCase))
                { await Msg("Folder not allowed", "Do not choose a drive, your whole profile, system or app folders, or one folder inside the other."); continue; }
                if (SyncPairStore.Exists(na, nb)) { await Msg("Already saved", "This pair is already on the dashboard."); continue; }
                var l = SyncPairStore.Load(); var q = l.FirstOrDefault(x => x.A == cur.A && x.B == cur.B);
                if (q != null) { q.A = na; q.B = nb; q.LastSync = null; q.LastResult = ""; q.LastErrors = 0; q.LastPending = -1; q.LastChecked = null; SyncPairStore.Save(l); }
            }
            else if (act == "stop")
            {
                if (await Confirm("Stop Folder Sync for this pair?", "The pair is removed from the dashboard and will not be checked again. Files on both laptops stay exactly as they are. Nothing is deleted.", "Stop sync", "Cancel")) SyncPairStore.Remove(cur);
            }
            else if (act == "history")
            {
                var f = Path.Combine(ReportStore.Folder, "folder-sync-history.txt");
                if (!File.Exists(f)) await Msg("No history yet", "Sync history is written after the first sync.");
                else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f) { UseShellExecute = true });
            }
            else if (act == "sync")
            {
                if (cur.Paused) { await Msg("Sync is paused", "Press Resume first."); continue; }
                if (cur.Remote) await RunRemotePair(cur); else await RunSync(cur.A, cur.B, cur.TwoWay);
            }
        }
    });
}
