using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoveIt;

// Spec 14: files that already exist at the destination. Default keeps both; Replace makes a verified backup first.
public sealed partial class MainWindow
{
    async Task<(string Mode, string Note)> ChooseConflictMode(string stageDir, string target)
    {
        if (stageDir == "" || !Directory.Exists(stageDir) || dialogOpen) return ("both", "");
        var existing = await Task.Run(() => Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.Combine(target, Path.GetRelativePath(stageDir, f))).Where(p => File.Exists(p)).ToList());
        if (existing.Count == 0) return ("both", "");
        ContentDialogResult r;
        dialogOpen = true;
        try
        {
            r = await new ContentDialog
            {
                Title = $"{existing.Count} file(s) already exist",
                Content = new ScrollViewer { MaxHeight = 300, Content = new TextBlock { Text = $"Your choice applies to all of them.\nSkip identical keeps both when contents differ.\n\n{Some(existing)}", TextWrapping = TextWrapping.Wrap } },
                PrimaryButtonText = "Replace (with backup)", SecondaryButtonText = "Skip identical", CloseButtonText = "Keep both",
                DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme
            }.ShowAsync();
        }
        finally { dialogOpen = false; }
        if (r == ContentDialogResult.Secondary) return ("skip", "\nIdentical files were skipped.");
        if (r != ContentDialogResult.Primary) return ("both", "");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "Backups");
        var dir = await Task.Run(() =>
        {
            Directory.CreateDirectory(root);
            var d = Path.Combine(root, "MoveIt_Backup_" + BackupService.Create(root, existing, CancellationToken.None).Id);
            if (BackupService.Verify(d).Count > 0) throw new IOException("Backup could not be verified. Nothing was replaced.");
            return d;
        });
        return ("replace", "\nReplaced files were backed up to: " + dir);
    }
}
