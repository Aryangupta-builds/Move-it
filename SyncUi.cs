using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoveIt;

// Spec 20 (first step): manual, opt-in sync between two folders this PC can reach, e.g. a Windows shared folder on the other laptop.
public sealed partial class MainWindow
{
    static bool BadSyncFolder(string p)
    {
        static string N(string x) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(x)) + Path.DirectorySeparatorChar;
        var f = N(p);
        if (string.Equals(f, N(Path.GetPathRoot(p)!), StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(f, N(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), StringComparison.OrdinalIgnoreCase)) return true;
        return new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData }
            .Any(k => f.StartsWith(N(Environment.GetFolderPath(k)), StringComparison.OrdinalIgnoreCase));
    }

    async void Sync_Click(object s, RoutedEventArgs e) => await RunTool(async () =>
    {
        if (!await Confirm("Set up Folder Sync?", "Folder Sync copies new and changed files between two folders, only when you run it. Newer files can replace older versions. Share the folder on the other laptop in Windows first, then choose it as one side.\n\nNot required for normal migration.", "Yes, set up", "No")) return;
        await Msg("Step 1", "Choose the SOURCE folder."); var a = await PickDir(); if (a == null) return;
        await Msg("Step 2", "Choose the DESTINATION folder."); var b = await PickDir(); if (b == null) return;
        string na = Path.TrimEndingDirectorySeparator(a) + "\\", nb = Path.TrimEndingDirectorySeparator(b) + "\\";
        if (BadSyncFolder(a) || BadSyncFolder(b) || na.StartsWith(nb, StringComparison.OrdinalIgnoreCase) || nb.StartsWith(na, StringComparison.OrdinalIgnoreCase))
        { await Msg("Folder not allowed", "Do not choose a drive, your whole profile, system or app folders, or one folder inside the other."); return; }
        bool two = await Confirm("Sync mode", $"Source: {a}\nDestination: {b}\n\nOne-way copies source changes to the destination. Two-way also copies destination changes back.", "Two-way", "One-way");
        bool ok = await RunSync(a, b, two);
        if (ok) await OfferSavePair(a, b, two);
    });

    async Task<bool> RunSync(string a, string b, bool two)
    {
        void CheckBoth() { if (!Directory.Exists(a) || !Directory.Exists(b)) throw new IOException("A folder is not reachable. Check that the other laptop is on and the folder is shared."); }
        CheckBoth();
        var last = FolderSync.LoadState(a, b);
        var plan = await WithStatus("Comparing both folders…", () => FolderSync.Plan(a, b, last, two));
        CheckBoth();
        long freeA = Net.FreeSpace(a), freeB = Net.FreeSpace(b);
        if ((freeB >= 0 && plan.CopyBytes > freeB) || (two && freeA >= 0 && plan.CopyBytes > freeA)) { await Msg("Not enough space", $"{Fmt(plan.CopyBytes)} may be needed."); return false; }
        if (!await Confirm("Preview", $"{(two ? "Two-way" : "One-way")}\nTo copy: {plan.Copies.Count} ({Fmt(plan.CopyBytes)})\nUnchanged: {plan.Unchanged}\nConflicts (left untouched): {plan.Conflicts.Count}\nDeletions to review: {plan.Deletions.Count}\n\n{Some(plan.Copies.Select(c => $"{c.Kind} {(c.AToB ? "to destination" : "to source")}: {c.Rel}"))}", "Sync now", "Cancel")) return false;
        var approved = await PickDeletions(plan.Deletions);   // per-file choice, nothing ticked by default
        CheckBoth();
        var replaced = plan.Copies.Where(c => c.Kind == ChangeKind.Modified).Select(c => Path.GetFullPath(Path.Combine(c.AToB ? b : a, c.Rel))).Where(File.Exists).ToList();
        if (replaced.Count > 0 && await Confirm("Back up first?", $"{replaced.Count} existing file(s) will be replaced ({Fmt(BackupService.RequiredBytes(replaced))}). Back them up? Restore later from Settings > Restore a backup.", "Back up", "Continue without backup"))
        {
            var root = await PickDir(); if (root == null) return false;   // cancelled: nothing is changed
            var bad = await WithStatus("Backing up files that will be replaced…", () => BackupService.Verify(Path.Combine(root, "MoveIt_Backup_" + BackupService.Create(root, replaced, CancellationToken.None).Id)));
            if (bad.Count > 0) { await Msg("Backup failed", "Sync was not started.\n" + Some(bad)); return false; }
        }
        var (state, errors) = await WithStatus("Syncing files…", () => FolderSync.Apply(a, b, plan, last, approved, CancellationToken.None));
        int resolved = 0; bool allC = false; string allChoice = "";
        using (var la = new LocalSide(a)) using (var lb = new LocalSide(b))
            foreach (var c in plan.Conflicts)
            {
                string choice = allC ? allChoice : await ConflictChoice(la, lb, c, plan.Conflicts.Count > 1);
                if (choice.EndsWith("*")) { allC = true; allChoice = choice.TrimEnd('*'); choice = allChoice; }
                if (choice == "" || choice == "leave") continue;
                try { var sig = await FolderSync.ResolveAsync(la, lb, c, choice, CancellationToken.None); if (sig != null) state.Files[c.Rel] = sig; resolved++; }
                catch (Exception ex) { errors.Add($"{c.Rel}: {ex.Message}"); }
            }
        FolderSync.SaveState(a, b, state);   // after conflicts, so resolved files are remembered
        try { Directory.CreateDirectory(ReportStore.Folder); File.AppendAllText(Path.Combine(ReportStore.Folder, "folder-sync-history.txt"), $"{DateTime.Now:g} | {a} -> {b} | {(two ? "two-way" : "one-way")} | planned copies {plan.Copies.Count}, approved deletions {approved.Count}, conflicts {plan.Conflicts.Count} (resolved {resolved}), errors {errors.Count}\n"); } catch { }
        await Msg(errors.Count == 0 && plan.Conflicts.Count == 0 ? "Sync finished" : "Sync finished with items to review",
            $"Errors: {errors.Count}\n{Some(errors)}\nConflicts left: {plan.Conflicts.Count - resolved}");
        SyncPairStore.Touch(a, b, two, plan.Conflicts.Count - resolved == 0 && errors.Count == 0 ? "Finished" : "Finished with items to review", errors.Count);
        return true;
    }
}
