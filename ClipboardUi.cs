using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace MoveIt;

// Spec 18 (first step): manual text sharing. The clipboard is read only when the user clicks, never monitored.
public sealed partial class MainWindow
{
    bool clipBusy;

    async void ClipSend_Click(object s, RoutedEventArgs e)
    {
        if (clipBusy) return;
        if (!ClipSwitch.IsOn) { await Msg("Sharing is off", "Turn on clipboard sharing in Settings first."); return; }
        if (targetIp == "") { await Msg("Not connected", "Connect to the other laptop first."); return; }
        clipBusy = true; string? tmp = null;
        string host = targetIp, code = targetCode, name = targetName;
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) { await Msg("No text", "The clipboard does not contain text."); return; }
            var text = await view.GetTextAsync();
            if (text.Length == 0 || text.Length > 200_000) { await Msg("Cannot send", "Text must be between 1 and 200,000 characters."); return; }
            var preview = text.Length > 200 ? text[..200] + "…" : text;
            if (!await Confirm("Send clipboard text?", $"To {name}:\n\n{preview}\n\nIt is encrypted if the other laptop also runs this MoveIt version (an older version would receive it unencrypted). It is saved there as a text file. Do not send passwords or private data.", "Send", "Cancel")) return;
            if (!ClipSwitch.IsOn) return;
            tmp = Path.Combine(Path.GetTempPath(), $"MoveIt-Clipboard-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(tmp, text);
            using var to = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var rep = await Sender.Send(host, code, DeviceName, new List<(string, string)> { (tmp, Path.GetFileName(tmp)) }, new Progress<Prog>(_ => { }), to.Token);
            await Msg(rep.Clean && rep.Sent == 1 ? "Text sent" : "Text not sent", rep.Clean && rep.Sent == 1 ? (rep.Encrypted ? "Sent encrypted. " : "Sent WITHOUT encryption (older MoveIt on the other laptop). ") + "If clipboard sharing is on there, a Copy prompt appears; otherwise open Settings, choose \"Copy received text\" and select the file." : Some(rep.Problems));
        }
        catch (Exception ex) { await Msg("Text not sent", ex.Message); }
        finally
        {
            clipBusy = false;
            if (tmp != null) { try { File.Delete(tmp); } catch { } }
        }
    }

    async void ClipReceive_Click(object s, RoutedEventArgs e) => await RunTool(async () =>
    {
        var p = Init(new Windows.Storage.Pickers.FileOpenPicker()); p.FileTypeFilter.Add(".txt");
        var f = await p.PickSingleFileAsync(); if (f == null) return;
        if (new FileInfo(f.Path).Length > 1_000_000) { await Msg("File too large", "Choose a MoveIt-Clipboard text file."); return; }
        var d = new DataPackage(); d.SetText(await File.ReadAllTextAsync(f.Path));
        Clipboard.SetContent(d);
        await Msg("Copied", "The text is on your clipboard. Paste it where you need it.");
    });

    // Spec 18: if clipboard sharing is ON here and the received file is a MoveIt clipboard text, offer to copy it now (preview shown, nothing is pasted automatically).
    async Task OfferClipboardThen(IReadOnlyList<ReceivedFile> files, string stageDir, string peer, Func<Task> next)
    {
        try
        {
            var cf = files.Count == 1 && files[0].Name.StartsWith("MoveIt-Clipboard") && files[0].Name.EndsWith(".txt") ? files[0] : null;
            var path = cf == null || stageDir == "" ? null : Path.Combine(stageDir, cf.Name);
            if (path != null && ClipSwitch.IsOn && File.Exists(path) && new FileInfo(path).Length <= 1_000_000)
            {
                var text = await File.ReadAllTextAsync(path);
                var preview = text.Length > 300 ? text[..300] + "…" : text;
                if (await Confirm("Clipboard text from " + (peer == "" ? "the other laptop" : peer), $"{preview}\n\nCopy it to this laptop's clipboard now? Turn off clipboard sharing in Settings to stop receiving.", "Copy", "Not now"))
                { var d = new DataPackage(); d.SetText(text); Clipboard.SetContent(d); }
            }
        }
        catch { }
        await next();
    }
}
