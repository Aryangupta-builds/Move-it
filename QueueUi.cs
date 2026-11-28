using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoveIt;

// Spec 13: queue window over TransferQueue. Runs on the UI thread; pause/skip/reorder apply between items.
public sealed partial class MainWindow
{
    bool queueRunning;

    async void Queue_Click(object s, RoutedEventArgs e)
    {
        if (dialogOpen) return;
        if (targetIp == "" || items.Count == 0) { await Msg("Queue is empty", "Connect to the other laptop and add files or folders first."); return; }
        var q = new TransferQueue(); foreach (var i in items) q.Add(i.FullPath, i.Folder);
        string host = targetIp, code = targetCode, peerLabel = targetName;
        using var cts = new CancellationTokenSource(); Task run = Task.CompletedTask; bool started = false; var startedAt = DateTime.Now; int page = 0; const int PageSize = 50;
        var rows = new StackPanel { Spacing = 8 }; var labels = new Dictionary<Guid, TextBlock>();
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Refresh()
        {
            foreach (var x in q.Items)
                if (labels.TryGetValue(x.Id, out var t))
                    t.Text = $"{x.Name} ({x.Type}{", " + Fmt(x.Size)})\n{x.Status} {x.Progress}% {x.Error}";
            summary.Text = $"{q.Completed} done, {q.Pending} pending, {q.Items.Count} total" + (q.IsPaused ? "  •  Paused after the current item" : "");
        }
        Button Btn(string text, Action act) { var b = new Button { Content = text }; b.Click += (_, _) => { act(); Refresh(); }; return b; }
        void Build()
        {
            rows.Children.Clear(); labels.Clear();
            page = Math.Clamp(page, 0, Math.Max(0, (q.Items.Count - 1) / PageSize));
            foreach (var x in q.Items.Skip(page * PageSize).Take(PageSize))   // paged so large queues stay responsive
            {
                var id = x.Id; var t = new TextBlock { TextWrapping = TextWrapping.Wrap }; labels[id] = t;
                var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                bar.Children.Add(Btn("Up", () => { q.Move(id, -1); Build(); }));
                bar.Children.Add(Btn("Down", () => { q.Move(id, 1); Build(); }));
                bar.Children.Add(Btn("Skip", () => q.Skip(id)));
                bar.Children.Add(Btn("Remove", () => { if (q.Remove(id)) Build(); }));
                rows.Children.Add(t); rows.Children.Add(bar);
            }
            Refresh();
        }
        var start = new Button { Content = "Start queue" };
        start.Click += async (_, _) =>
        {
            start.IsEnabled = false; started = true; queueRunning = true; startedAt = DateTime.Now;
            try { run = q.RunAsync(host, code, DeviceName, Refresh, cts.Token); await run; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { summary.Text = ex.Message; return; }
            finally { queueRunning = false; }
            Refresh();
        };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        controls.Children.Add(start); controls.Children.Add(Btn("Pause", q.Pause)); controls.Children.Add(Btn("Resume", q.Resume));
        controls.Children.Add(Btn("Previous", () => { page--; Build(); })); controls.Children.Add(Btn("Next", () => { page++; Build(); }));
        async void Measure(QueueItem x)
        {
            try { x.Size = await Task.Run(() => new DirectoryInfo(x.Path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length)); Refresh(); }
            catch { }   // size stays 0 if the folder cannot be read
        }
        foreach (var x in q.Items.Where(i => i.Folder)) Measure(x);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(controls); panel.Children.Add(summary); panel.Children.Add(rows);
        Build();
        dialogOpen = true; SendBtn.IsEnabled = false;
        try
        {
            await new ContentDialog { Title = "Transfer queue", Content = new ScrollViewer { Content = panel, MaxHeight = 420 }, CloseButtonText = "Close (cancels unfinished items; needed before exit)", XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme }.ShowAsync();
            cts.Cancel();
            if (started)
            {
                try { await run; } catch (OperationCanceledException) { }
                var ok = q.Items.Where(i => i.Status == QStatus.Done).ToList();
                var result = ok.Count == q.Items.Count ? "Completed" : ok.Count > 0 ? "Partial" : q.Items.Any(i => i.Status == QStatus.Failed) ? "Failed" : "Cancelled";
                HistoryStore.Add(new HistoryEntry(DateTime.Now, "Sent", peerLabel, ok.Count, ok.Sum(i => i.Size), result));   // counts queue items
                SaveSimpleReport("Queue send", DeviceName, peerLabel, startedAt, q.Items.Count, ok.Count, q.Items.Count(i => i.Status == QStatus.Failed), q.Items.Count(i => i.Status == QStatus.Skipped), q.Items.Sum(i => i.Size), result, q.Items.Where(i => i.Error != "").Select(i => i.Name + ": " + i.Error));
                MarkScheduledDone(ok.Select(i => i.Path));
            }
        }
        finally { dialogOpen = false; SendBtn.IsEnabled = true; }
    }
}
