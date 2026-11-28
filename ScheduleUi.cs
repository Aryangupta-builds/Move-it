using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoveIt;

// Spec 19: scheduled transfers. A job only reminds and prepares; the user connects and presses Send.
public sealed partial class MainWindow
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? scheduleTimer;

    void InitSchedule()
    {
        scheduleTimer = DispatcherQueue.CreateTimer(); scheduleTimer.Interval = TimeSpan.FromSeconds(30);
        scheduleTimer.Tick += async (_, _) =>
        {
            if (dialogOpen || Content?.XamlRoot == null) return;
            var due = ScheduleStore.Due(DateTime.Now);
            if (due.Count == 0) return;
            foreach (var j in due) SetJob(j.Id, "Due");
            await Msg("Scheduled transfer is due", $"{due.Count} job(s) are due. Connect to the other laptop, then open Scheduled transfers on the Send page.");
        };
        scheduleTimer.Start();
        InitBatteryWatch();
        InitSyncWatch();
    }

    static void SetJob(Guid id, string status)
    {
        var l = ScheduleStore.Load(); var j = l.FirstOrDefault(x => x.Id == id);
        if (j != null) { j.Status = status; ScheduleStore.Save(l); }
    }

    // A job is Done only after a clean send that included every one of its paths.
    static void MarkScheduledDone(IEnumerable<string> sent)
    {
        var set = new HashSet<string>(sent, StringComparer.OrdinalIgnoreCase);
        var l = ScheduleStore.Load();
        var done = l.Where(j => j.Status == "Loaded to Send list" && j.Paths.All(p => set.Contains(p))).ToList();
        foreach (var j in done) j.Status = "Done";
        if (done.Count > 0) ScheduleStore.Save(l);
    }

    async void Schedule_Click(object s, RoutedEventArgs e)
    {
        if (dialogOpen) return;
        var date = new DatePicker { Date = DateTimeOffset.Now };
        var time = new TimePicker { Time = DateTime.Now.AddHours(1).TimeOfDay };
        DateTime When() => date.Date.Date + time.Time;
        ScheduledJob? run = null;
        var list = new StackPanel { Spacing = 8 };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Jobs run once. MoveIt must be open; it never wakes a sleeping laptop. You connect and press Send yourself." };
        var dlg = new ContentDialog { Title = "Scheduled transfers", CloseButtonText = "Close", XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
        void Build()
        {
            list.Children.Clear();
            foreach (var j in ScheduleStore.Load().OrderBy(x => x.When))
            {
                list.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"{j.When:g}  •  {j.Status}\nTo {j.DeviceName}: {Some(j.Paths)}" });
                var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var load = new Button { Content = "Run now", IsEnabled = j.Status is not ("Cancelled" or "Done") }; load.Click += (_, _) => { run = j; dlg.Hide(); };
                var move = new Button { Content = "Reschedule" }; move.Click += (_, _) => { if (When() > DateTime.Now) { ScheduleStore.Reschedule(j.Id, When()); Build(); } else note.Text = "Choose a future time."; };
                var stop = new Button { Content = "Cancel job" }; stop.Click += (_, _) => { ScheduleStore.Cancel(j.Id); Build(); };
                bar.Children.Add(load); bar.Children.Add(move); bar.Children.Add(stop); list.Children.Add(bar);
            }
        }
        var label = $"Schedule the {items.Count} selected item(s)";
        var add = new Button { Content = label, IsEnabled = items.Count > 0 && targetName != "" };
        add.Click += (_, _) =>
        {
            if (When() <= DateTime.Now) { note.Text = "Choose a future time."; return; }
            if (add.Content as string != "Confirm schedule")
            { note.Text = $"Review: from {DeviceName} to {targetName} at {When():g}\n{Some(items.Select(i => i.Name))}"; add.Content = "Confirm schedule"; return; }
            add.Content = label;
            ScheduleStore.Add(When(), targetName, items.Select(i => i.FullPath));
            note.Text = "Scheduled: " + Some(items.Select(i => i.Name)); Build();
        };
        date.DateChanged += (_, _) => add.Content = label; time.TimeChanged += (_, _) => add.Content = label;
        var panel = new StackPanel { Spacing = 10 };
        foreach (UIElement c in new UIElement[] { note, date, time, add, list }) panel.Children.Add(c);
        Build(); dlg.Content = new ScrollViewer { Content = panel, MaxHeight = 460 };
        dialogOpen = true;
        try { await dlg.ShowAsync(); } finally { dialogOpen = false; }
        if (run != null) await LoadJob(run);
    }

    async Task LoadJob(ScheduledJob j)
    {
        if (targetIp == "") { await Msg("Not connected", "Connect to the other laptop first."); return; }
        PeerInfo? peer = null;
        try { using var to = new CancellationTokenSource(TimeSpan.FromSeconds(8)); peer = await Sender.Probe(targetIp, targetCode, to.Token); } catch { }
        var problems = await WithStatus("Checking the scheduled job…", () => ScheduleStore.Preflight(j, peer));
        if (problems.Count > 0) { await Msg("Job cannot start", Some(problems) + "\n\nFix this and run it again, or reschedule."); return; }
        if (j.DeviceName != targetName && !await Confirm("Different laptop", $"This job was made for {j.DeviceName}, but you are connected to {targetName}.", "Use this laptop", "Cancel")) return;
        ModeRadio.SelectedIndex = 1; items.Clear();
        foreach (var p in j.Paths) AddItem(p, Directory.Exists(p));
        SetJob(j.Id, "Loaded to Send list");
        await Msg("Ready to review", "The scheduled items are in the Send list. Check them and press Send.");
    }
}
