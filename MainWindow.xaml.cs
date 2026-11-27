using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MoveIt;

public sealed partial class MainWindow : Window
{
    readonly List<(string path, bool folder)> items = new();
    readonly ObservableCollection<string> itemView = new(), log = new();
    Receiver? rx; CancellationTokenSource? txCts;

    public MainWindow()
    {
        InitializeComponent();
        Title = "MoveIt";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 760));
        DestBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MoveIt_Received");
        LogList.ItemsSource = log; ItemList.ItemsSource = itemView;
        Nav.SelectedItem = Nav.MenuItems[0];
        Closed += (_, _) => rx?.Stop();
    }

    void Nav_SelectionChanged(NavigationView s, NavigationViewSelectionChangedEventArgs a)
    {
        var tag = (a.SelectedItem as NavigationViewItem)?.Tag as string;
        RxPanel.Visibility = tag == "rx" ? Visibility.Visible : Visibility.Collapsed;
        TxPanel.Visibility = tag == "tx" ? Visibility.Visible : Visibility.Collapsed;
    }

    T Init<T>(T picker) { InitializeWithWindow.Initialize(picker!, WindowNative.GetWindowHandle(this)); return picker; }

    static string Fmt(double n)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
        while (n >= 1024 && i < 4) { n /= 1024; i++; }
        return $"{n:0.#} {u[i]}";
    }

    void Show(ProgressBar bar, TextBlock txt, Prog p)
    {
        bar.Value = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
        txt.Text = $"{Fmt(p.Done)} / {Fmt(p.Total)}  •  {Fmt(p.Speed)}/s";
    }

    // ---- receive ----
    async void BrowseDest_Click(object s, RoutedEventArgs e)
    {
        var p = Init(new FolderPicker()); p.FileTypeFilter.Add("*");
        var f = await p.PickSingleFolderAsync(); if (f != null) DestBox.Text = f.Path;
    }

    void StartRx_Click(object s, RoutedEventArgs e)
    {
        var code = RandomNumberGenerator.GetInt32(1000000).ToString("D6");
        InfoText.Text = $"IP: {Net.LocalIp()}\nCode: {code}";
        rx = new Receiver(code, DestBox.Text,
            m => DispatcherQueue.TryEnqueue(() => log.Insert(0, m)),
            new Progress<Prog>(p => Show(RxBar, RxStat, p)));
        rx.Start(); StartRxBtn.IsEnabled = false;
        log.Insert(0, "Ready. Now open Send on the old laptop and click Scan WiFi.");
    }

    // ---- send ----
    async void Scan_Click(object s, RoutedEventArgs e)
    {
        TxStat.Text = "Scanning...";
        var found = await Sender.Scan();
        if (found.Count > 0) HostBox.Text = found[0];
        TxStat.Text = found.Count > 0 ? $"Found: {string.Join(", ", found)}" : "Nothing found - type the IP shown on the new laptop";
    }

    async void AddFiles_Click(object s, RoutedEventArgs e)
    {
        var p = Init(new FileOpenPicker()); p.FileTypeFilter.Add("*");
        var fs = await p.PickMultipleFilesAsync();
        if (fs != null) foreach (var f in fs) { items.Add((f.Path, false)); itemView.Add(f.Path); }
    }

    async void AddFolder_Click(object s, RoutedEventArgs e)
    {
        var p = Init(new FolderPicker()); p.FileTypeFilter.Add("*");
        var f = await p.PickSingleFolderAsync();
        if (f != null) { items.Add((f.Path, true)); itemView.Add(f.Path + "  [folder]"); }
    }

    void Clear_Click(object s, RoutedEventArgs e) { items.Clear(); itemView.Clear(); }
    void Cancel_Click(object s, RoutedEventArgs e) => txCts?.Cancel();

    async void Send_Click(object s, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim(); var code = CodeBox.Text.Trim();
        var dirs = new List<(string name, string path)>();
        string prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (CbDesktop.IsChecked == true) dirs.Add(("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
        if (CbDocuments.IsChecked == true) dirs.Add(("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        if (CbDownloads.IsChecked == true) dirs.Add(("Downloads", Path.Combine(prof, "Downloads")));
        if (CbPictures.IsChecked == true) dirs.Add(("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));
        if (CbVideos.IsChecked == true) dirs.Add(("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)));
        if (CbMusic.IsChecked == true) dirs.Add(("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)));
        bool apps = CbApps.IsChecked == true;
        if (host == "" || code == "" || (items.Count == 0 && dirs.Count == 0 && !apps))
        { TxStat.Text = "Enter IP, code and select something to send."; return; }

        SendBtn.IsEnabled = false; txCts = new(); var snapshot = items.ToList();
        var prog = new Progress<Prog>(p => Show(TxBar, TxStat, p));
        try
        {
            TxStat.Text = "Collecting files...";
            var files = await Task.Run(async () =>
            {
                var l = new List<(string, string)>();
                foreach (var (path, folder) in snapshot)
                    if (!folder) l.Add((path, Path.GetFileName(path)));
                    else l.AddRange(Sender.Walk(path, Path.GetFileName(path.TrimEnd('\\'))));
                foreach (var (name, path) in dirs)
                    if (Directory.Exists(path)) l.AddRange(Sender.Walk(path, "UserData/" + name));
                if (apps)
                {
                    DispatcherQueue.TryEnqueue(() => TxStat.Text = "Exporting app list (may take a minute)...");
                    foreach (var f in await Sender.ExportApps()) l.Add((f, "AppList/" + Path.GetFileName(f)));
                }
                return l;
            });
            TxStat.Text = "Connecting...";
            await Sender.Send(host, code, files, prog, txCts.Token);
            TxStat.Text = $"Done! {files.Count} files sent.";
        }
        catch (OperationCanceledException) { TxStat.Text = "Cancelled"; }
        catch (Exception ex) { TxStat.Text = "Error: " + ex.Message; }
        finally { SendBtn.IsEnabled = true; }
    }
}
