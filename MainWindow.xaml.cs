using System.Collections.ObjectModel;
using System.Numerics;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MoveIt;

public record DeviceItem(string Ip, string Name);

public class AppSettings
{
    public string Theme { get; set; } = "Default";
    public string DeviceName { get; set; } = "";
    public string SaveFolder { get; set; } = "";
    public bool OpenWhenDone { get; set; } = true;
}

public sealed partial class MainWindow : Window
{
    readonly List<(string path, bool folder)> items = new();
    readonly ObservableCollection<string> itemView = new();
    readonly ObservableCollection<DeviceItem> devices = new();
    string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MoveIt_Received");
    string targetIp = "", targetCode = "";
    Receiver? rx; CancellationTokenSource? scanCts, txCts;

    public MainWindow()
    {
        InitializeComponent();
        Title = "MoveIt";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800));
        DeviceGrid.ItemsSource = devices;
        ItemList.ItemsSource = itemView;
        HomeInfo.Text = $"This laptop: {DeviceName}  •  {Net.LocalIp()}";
        Closed += (_, _) => StopAll();
        LoadSettings();
    }

    // ---------- helpers ----------
    void ShowPage(Grid page)
    {
        foreach (var g in new[] { HomePage, ScanPage, SendPage, RxPage, SettingsPage })
            g.Visibility = g == page ? Visibility.Visible : Visibility.Collapsed;
    }

    void StopAll()
    {
        scanCts?.Cancel(); txCts?.Cancel();
        ScanRadar.Stop(); RxRadar.Stop();
        rx?.Stop(); rx = null;
    }

    T Init<T>(T picker) { InitializeWithWindow.Initialize(picker!, WindowNative.GetWindowHandle(this)); return picker; }

    static string Fmt(double n)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
        while (n >= 1024 && i < 4) { n /= 1024; i++; }
        return $"{n:0.#} {u[i]}";
    }

    async Task Msg(string title, string text)
    {
        await new ContentDialog { Title = title, Content = text, CloseButtonText = "OK", XamlRoot = Content.XamlRoot }.ShowAsync();
    }

    // ---------- settings ----------
    AppSettings cfg = new();
    bool loadingSettings;
    static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "settings.json");
    string DeviceName => string.IsNullOrWhiteSpace(cfg.DeviceName) ? Environment.MachineName : cfg.DeviceName.Trim();

    void LoadSettings()
    {
        try { if (File.Exists(SettingsFile)) cfg = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new(); }
        catch { cfg = new(); }
        if (cfg.SaveFolder != "") dest = cfg.SaveFolder;
        ApplyTheme(cfg.Theme);
        RefreshSettingsUi();
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, System.Text.Json.JsonSerializer.Serialize(cfg));
        }
        catch { }
    }

    void RefreshSettingsUi()
    {
        loadingSettings = true;
        ThemeRadio.SelectedIndex = cfg.Theme == "Light" ? 1 : cfg.Theme == "Dark" ? 2 : 0;
        NameBox.Text = cfg.DeviceName; NameBox.PlaceholderText = Environment.MachineName;
        FolderText.Text = dest; OpenSwitch.IsOn = cfg.OpenWhenDone;
        HomeInfo.Text = $"This laptop: {DeviceName}  •  {Net.LocalIp()}";
        loadingSettings = false;
    }

    void ApplyTheme(string t)
    {
        if (Content is FrameworkElement fe)
            fe.RequestedTheme = t == "Light" ? ElementTheme.Light : t == "Dark" ? ElementTheme.Dark : ElementTheme.Default;
    }

    void OpenSettings_Click(object s, RoutedEventArgs e) { RefreshSettingsUi(); ShowPage(SettingsPage); }

    void Theme_Changed(object s, SelectionChangedEventArgs e)
    {
        if (loadingSettings) return;
        cfg.Theme = ThemeRadio.SelectedIndex == 1 ? "Light" : ThemeRadio.SelectedIndex == 2 ? "Dark" : "Default";
        ApplyTheme(cfg.Theme); SaveSettings();
    }

    void Name_LostFocus(object s, RoutedEventArgs e) { cfg.DeviceName = NameBox.Text.Trim(); SaveSettings(); RefreshSettingsUi(); }

    async void SettingsFolder_Click(object s, RoutedEventArgs e)
    {
        var p = Init(new FolderPicker()); p.FileTypeFilter.Add("*");
        var f = await p.PickSingleFolderAsync();
        if (f != null) { dest = f.Path; cfg.SaveFolder = dest; SaveSettings(); RefreshSettingsUi(); }
    }

    void Open_Toggled(object s, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        cfg.OpenWhenDone = OpenSwitch.IsOn; SaveSettings();
    }

    void Reset_Click(object s, RoutedEventArgs e)
    {
        cfg = new();
        dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MoveIt_Received");
        SaveSettings(); ApplyTheme("Default"); RefreshSettingsUi();
    }

    // ---------- home ----------
    void Card_Enter(object s, PointerRoutedEventArgs e)
    {
        if (s is Border b) { b.CenterPoint = new Vector3((float)b.ActualWidth / 2, (float)b.ActualHeight / 2, 0); b.Scale = new Vector3(1.05f, 1.05f, 1); }
    }
    void Card_Exit(object s, PointerRoutedEventArgs e) { if (s is Border b) b.Scale = Vector3.One; }

    void Back_Click(object s, RoutedEventArgs e) { StopAll(); ShowPage(HomePage); }

    void SendCard_Tapped(object s, TappedRoutedEventArgs e) { ShowPage(ScanPage); StartScan(); }

    void RxCard_Tapped(object s, TappedRoutedEventArgs e) => StartReceiving();

    // ---------- scan ----------
    async void StartScan()
    {
        scanCts?.Cancel(); scanCts = new(); var ct = scanCts.Token;
        devices.Clear(); ScanRadar.Start();
        ScanTitle.Text = "Looking for nearby laptops…";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var found = await Sender.Scan();
                if (ct.IsCancellationRequested) break;
                foreach (var (ip, name) in found)
                    if (!devices.Any(d => d.Ip == ip)) devices.Add(new DeviceItem(ip, name));
                ScanTitle.Text = devices.Count == 0 ? "Looking for nearby laptops…" : $"{devices.Count} laptop(s) found. Tap one";
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(1500); }
        }
    }

    void Device_Click(object s, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DeviceItem d) ChooseTarget(d.Ip, d.Name);
    }

    async void Manual_Click(object s, RoutedEventArgs e)
    {
        var box = new TextBox { Header = "Receiver IP address", PlaceholderText = "e.g. 192.168.1.5" };
        var dlg = new ContentDialog
        {
            Title = "Enter IP manually", Content = box, PrimaryButtonText = "Continue", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim() != "")
            ChooseTarget(box.Text.Trim(), box.Text.Trim());
    }

    async void ChooseTarget(string ip, string name)
    {
        var box = new TextBox { Header = "6-digit code shown on the receiving laptop", MaxLength = 6, PlaceholderText = "000000" };
        var dlg = new ContentDialog
        {
            Title = $"Connect to {name}", Content = box, PrimaryButtonText = "Connect", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim() == "") return;
        targetIp = ip; targetCode = box.Text.Trim();
        scanCts?.Cancel(); ScanRadar.Stop();
        SendTitle.Text = $"Send to {name}";
        PickPanel.Visibility = Visibility.Visible; SendingPanel.Visibility = Visibility.Collapsed;
        ShowPage(SendPage);
    }

    // ---------- send ----------
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

    void Done_Click(object s, RoutedEventArgs e)
    {
        StopAll(); items.Clear(); itemView.Clear();
        PickPanel.Visibility = Visibility.Visible; SendingPanel.Visibility = Visibility.Collapsed;
        ShowPage(HomePage);
    }

    void ShowTx(Prog p)
    {
        double pct = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
        TxRing.Value = pct; TxPercent.Text = $"{pct:0}%";
        TxStat.Text = $"{Fmt(p.Done)} of {Fmt(p.Total)}  •  {Fmt(p.Speed)}/s";
    }

    async void Send_Click(object s, RoutedEventArgs e)
    {
        var dirs = new List<(string name, string path)>();
        string prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (CbDesktop.IsChecked == true) dirs.Add(("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
        if (CbDocuments.IsChecked == true) dirs.Add(("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        if (CbDownloads.IsChecked == true) dirs.Add(("Downloads", Path.Combine(prof, "Downloads")));
        if (CbPictures.IsChecked == true) dirs.Add(("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));
        if (CbVideos.IsChecked == true) dirs.Add(("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)));
        if (CbMusic.IsChecked == true) dirs.Add(("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)));
        bool apps = CbApps.IsChecked == true;
        if (items.Count == 0 && dirs.Count == 0 && !apps) { await Msg("Nothing selected", "Add files or folders, or tick an option under Full migration."); return; }

        PickPanel.Visibility = Visibility.Collapsed; SendingPanel.Visibility = Visibility.Visible;
        CancelBtn.Visibility = Visibility.Visible; DoneBtn.Visibility = Visibility.Collapsed;
        TxRing.Value = 0; TxPercent.Text = "0%";
        txCts = new(); var snapshot = items.ToList();
        var prog = new Progress<Prog>(ShowTx);
        try
        {
            TxStat.Text = "Collecting files…";
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
                    DispatcherQueue.TryEnqueue(() => TxStat.Text = "Exporting app list (may take a minute)…");
                    foreach (var f in await Sender.ExportApps()) l.Add((f, "AppList/" + Path.GetFileName(f)));
                }
                return l;
            });
            TxStat.Text = "Connecting…";
            await Sender.Send(targetIp, targetCode, files, prog, txCts.Token);
            TxRing.Value = 100; TxPercent.Text = "✓"; TxStat.Text = $"Done! {files.Count} files sent.";
        }
        catch (OperationCanceledException) { TxStat.Text = "Cancelled"; }
        catch (Exception ex) { TxStat.Text = "Error: " + ex.Message; }
        finally { CancelBtn.Visibility = Visibility.Collapsed; DoneBtn.Visibility = Visibility.Visible; }
    }

    // ---------- receive ----------
    void StartReceiving()
    {
        rx?.Stop();
        var code = RandomNumberGenerator.GetInt32(1000000).ToString("D6");
        CodeText.Text = code; DestText.Text = dest;
        RxName.Text = $"Visible as {DeviceName}  •  {Net.LocalIp()}";
        RxStatus.Text = "Waiting for the other laptop…"; RxProgPanel.Visibility = Visibility.Collapsed; RxBar.Value = 0;
        ShowPage(RxPage); RxRadar.Start();
        rx = new Receiver(code, dest, m => DispatcherQueue.TryEnqueue(() => OnRxLog(m)),
            new Progress<Prog>(p =>
            {
                RxProgPanel.Visibility = Visibility.Visible;
                RxBar.Value = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
                RxStat.Text = $"{Fmt(p.Done)} of {Fmt(p.Total)}  •  {Fmt(p.Speed)}/s";
            }), DeviceName);
        rx.Start();
    }

    void OnRxLog(string m)
    {
        RxStatus.Text = m;
        if (m.StartsWith("Connected")) RxProgPanel.Visibility = Visibility.Visible;
        if (m.StartsWith("Done") && cfg.OpenWhenDone)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dest}\"") { UseShellExecute = true }); } catch { }
        }
    }

    async void ChangeDest_Click(object s, RoutedEventArgs e)
    {
        var p = Init(new FolderPicker()); p.FileTypeFilter.Add("*");
        var f = await p.PickSingleFolderAsync();
        if (f != null) { dest = f.Path; cfg.SaveFolder = dest; SaveSettings(); StartReceiving(); }
    }
}
