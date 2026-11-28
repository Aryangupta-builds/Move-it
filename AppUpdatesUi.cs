using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoveIt;

// Spec 9: app updates through winget's official source only. Nothing runs without a confirmation; agreements are never auto-accepted.
public sealed partial class MainWindow
{
    async void AppUpdates_Click(object s, RoutedEventArgs e) => await RunTool(async () =>
    {
        if (dialogOpen) return;
        var psi = new ProcessStartInfo("winget") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        psi.ArgumentList.Add("upgrade"); psi.ArgumentList.Add("--source"); psi.ArgumentList.Add("winget");
        string output;
        using (var p = Process.Start(psi)!)
        {
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            using var to = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await p.WaitForExitAsync(to.Token); } catch (OperationCanceledException) { p.Kill(true); throw new TimeoutException("winget did not answer. Run 'winget upgrade' once in a terminal, then try again."); }
            output = await read;
        }
        var id = new TextBox { PlaceholderText = "Package Id to update (copy from the list)" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new ScrollViewer { MaxHeight = 280, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = output.Trim(), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), IsTextSelectionEnabled = true } });
        panel.Children.Add(id);
        bool update;
        dialogOpen = true;
        try { update = await new ContentDialog { Title = "App updates on this laptop", Content = panel, PrimaryButtonText = "Update this app", CloseButtonText = "Close", XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme }.ShowAsync() == ContentDialogResult.Primary; }
        finally { dialogOpen = false; }
        var package = id.Text.Trim();
        if (!update) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(package, @"^[A-Za-z0-9][A-Za-z0-9._+\-]*$")) { await Msg("Invalid Id", "Copy the package Id exactly as shown."); return; }
        if (!await Confirm("Update app?", $"winget will update {package} from its official source. Follow any prompts in the window that opens.", "Update", "Cancel")) return;
        var run = new ProcessStartInfo("winget") { UseShellExecute = false };
        foreach (var a in new[] { "upgrade", "--id", package, "--exact", "--source", "winget" }) run.ArgumentList.Add(a);
        using var u = Process.Start(run)!; await u.WaitForExitAsync();
        await Msg(u.ExitCode == 0 ? "Update finished" : "Update not completed", $"{package}: winget exit code {u.ExitCode}.");
    });
}
