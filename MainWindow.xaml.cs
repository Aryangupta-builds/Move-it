using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using QRCoder;
using WinRT.Interop;

namespace MoveIt;

public sealed partial class MainWindow : Window
{
    readonly ObservableCollection<SendItem> items = new();
    readonly ObservableCollection<DeviceItem> devices = new();
    readonly Dictionary<string, int> misses = new();
    string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MoveIt_Received");
    string targetIp = "", targetCode = "", targetName = "";
    WebShare? web; Receiver? rx; CancellationTokenSource? scanCts, txCts;
    int scanRounds;
    readonly DispatcherQueueTimer rxTimer, rxPanelTimer;
    bool rxHover, rxPanelOk;   // rxHover: pointer over icon/panel; rxPanelOk: details exist (a transfer started or ended), so hover may show them

    public MainWindow()
    {
        InitializeComponent();
        Title = "MoveIt";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800));
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico")); } catch { }
        DeviceGrid.ItemsSource = devices;
        ItemList.ItemsSource = items;
        items.CollectionChanged += (_, _) => RefreshItemsUi();
        VersionText.Text = "v" + AppInfo.Version; AboutVersion.Text = "Version " + AppInfo.Version;
        rxTimer = DispatcherQueue.CreateTimer(); rxTimer.Interval = TimeSpan.FromMilliseconds(120); rxTimer.Tick += (_, _) => RxTick();
        rxPanelTimer = DispatcherQueue.CreateTimer(); rxPanelTimer.Interval = TimeSpan.FromSeconds(4);
        rxPanelTimer.Tick += (_, _) => { rxPanelTimer.Stop(); if (!rxHover && rxReceiving) ShowRxPanel(false); };
        Particle.IconHover += inside => { rxHover = inside; if (inside) { rxPanelTimer.Stop(); if (rxPanelOk) ShowRxPanel(true); } else if (rxReceiving) { rxPanelTimer.Interval = TimeSpan.FromSeconds(1.5); rxPanelTimer.Start(); } };
        Particle.IconTapped += () => { if (!rxPanelOk || !rxReceiving) return; if (RxBottom.Opacity > 0.5) ShowRxPanel(false); else PulseRxPanel(); };   // touch: tap toggles
        Closed += (_, _) => StopAll(true);
        LoadSettings();
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        RootGrid.Loaded += (_, _) => UpdateCaptionButtons();
        RecoverIncoming();
        ExtendsContentIntoTitleBar = true; SetTitleBar(HeaderBar);
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        UpdateCaptionButtons();
        ModeRadio.SelectedIndex = 1;
        if (!cfg.SetupDone) { SetupName.Text = cfg.DeviceName; SetupName.PlaceholderText = Environment.MachineName; }
        BuildSetup();
        RefreshItemsUi();
        ShowPage(cfg.SetupDone ? HomePage : SetupPage);
    }

    // ---------- helpers ----------
    Grid? current, previous;

    void Drift(CompositeTransform t, double dx, double dy, double secs)
    {
        var sb = new Storyboard();
        foreach (var (p, v) in new[] { ("TranslateX", dx), ("TranslateY", dy) })
        {
            var an = new DoubleAnimation { From = 0, To = v, Duration = TimeSpan.FromSeconds(secs), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Storyboard.SetTarget(an, t); Storyboard.SetTargetProperty(an, p); sb.Children.Add(an);
        }
        sb.Begin();
    }

    void ShowPage(Grid page)
    {
        // Settings / History are side pages: remember the real page we came from, not each other
        if (current != null && current != page && current != SettingsPage && current != HistoryPage) previous = current;
        current = page;
        foreach (var g in new[] { SetupPage, HomePage, ScanPage, SendPage, RxChoosePage, RxPage, HistoryPage, SettingsPage })
        {
            if (g == page) { g.Opacity = 0; g.Visibility = Visibility.Visible; DispatcherQueue.TryEnqueue(() => g.Opacity = 1); }
            else g.Visibility = Visibility.Collapsed;
        }
        BackBtn.Visibility = page == HomePage || page == SetupPage ? Visibility.Collapsed : Visibility.Visible;
        SettingsBtn.Visibility = page == SettingsPage || page == SetupPage ? Visibility.Collapsed : Visibility.Visible;
        HistoryBtn.Visibility = page == HistoryPage || page == SetupPage ? Visibility.Collapsed : Visibility.Visible;
    }

    bool dialogOpen;
    async Task<bool> Confirm(string title, string text, string yes, string no)
    {
        if (dialogOpen) return false;
        dialogOpen = true;
        try
        {
            var r = await new ContentDialog
            {
                Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = yes, CloseButtonText = no, DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme
            }.ShowAsync();
            return r == ContentDialogResult.Primary;
        }
        finally { dialogOpen = false; }
    }

    async Task Msg(string title, string text)
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try { await new ContentDialog { Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK", XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme }.ShowAsync(); }
        finally { dialogOpen = false; }
    }

    bool Sending => SendingPanel.Visibility == Visibility.Visible && TxLive.Visibility == Visibility.Visible && txCts != null;

    // Back works from every page. If a transfer is running, ask first, tell the other device, then leave.
    async Task GoBackAsync()
    {
        if (current == SetupPage) return;
        if (current == SettingsPage || current == HistoryPage) { ShowPage(previous ?? HomePage); return; }
        if (current == RxPage && rxReceiving)
        {
            if (!await Confirm("Cancel this transfer?", "A transfer is in progress. If you leave now it will be cancelled.", "Cancel transfer", "Keep receiving")) return;
            if (rxReceiving) { CancelRx(); await Task.Delay(450); } // give the cancel message time to reach the sender
        }
        else if (current == SendPage && Sending)
        {
            if (!await Confirm("Cancel this transfer?", "A transfer is in progress. If you leave now it will be cancelled.", "Cancel transfer", "Keep sending")) return;
            txCts?.Cancel(); await Task.Delay(450);
        }
        if (current == RxPage && cfg.MobileShare) { StopAll(); ShowPage(RxChoosePage); return; }
        StopAll(); ShowPage(HomePage);
    }
    async void Back_Accel(KeyboardAccelerator a, KeyboardAcceleratorInvokedEventArgs e)
    {
        // Esc / Alt+Left must not also fire when a dialog or dropdown is open (Esc is meant to close that first)
        if (current == HomePage || VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Count > 0) return;
        e.Handled = true; await GoBackAsync();
    }
    // Narrow (< 760 px): smaller side padding and a single column of mode choices. Cards are single-column everywhere.
    bool? narrowLayout;
    void Root_SizeChanged(object s, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width);

    void ApplyLayout(double width)
    {
        bool n = width < 760;
        if (narrowLayout == n) return;
        narrowLayout = n;
        PagesHost.Padding = n ? new Thickness(14, 0, 14, 0) : new Thickness(24, 0, 24, 0);
        ModeRadio.MaxColumns = n ? 1 : 3;
    }

    async void Root_PointerPressed(object s, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(RootGrid).Properties.IsXButton1Pressed && current != HomePage) await GoBackAsync();
    }
    async void Back_Click(object s, RoutedEventArgs e) => await GoBackAsync();

    void StopAll(bool sync = false)
    {
        rxGen++; rxTimer.Stop(); rxPanelTimer.Stop(); rxReceiving = false; rxConnected = false;
        scanCts?.Cancel(); txCts?.Cancel();
        ScanRadar.Stop(); Particle.Stop(); SendFx.Stop();
        rx?.Stop(); rx = null; web?.Stop(); web = null;
        // files that finished but were never moved out of the staging folder go to the default save folder
        var st = stage; stage = "";
        if (st != "")
        {
            var target = dest;
            void Work() { try { MoveStaged(st, target); Directory.Delete(st, true); } catch { } }
            if (sync) Work(); else _ = Task.Run(Work);
        }
    }

    T Init<T>(T picker) { InitializeWithWindow.Initialize(picker!, WindowNative.GetWindowHandle(this)); return picker; }
    static string Fmt(double n) => AppInfo.Fmt(n);

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
        FolderText.Text = dest; OpenSwitch.IsOn = cfg.OpenWhenDone; MobileSwitch.IsOn = cfg.MobileShare;
        HomeInfo.Text = $"This laptop: {DeviceName}  •  {Net.LocalIp()}";
        loadingSettings = false;
    }

    void ApplyTheme(string t)
    {
        RootGrid.RequestedTheme = t == "Light" ? ElementTheme.Light : t == "Dark" ? ElementTheme.Dark : ElementTheme.Default;
        UpdateCaptionButtons();
    }

    // The system minimize / maximize / close buttons follow the SYSTEM theme, not the app theme. If the app is dark
    // but Windows is light (or the other way round) they become invisible, so we colour them ourselves.
    void UpdateCaptionButtons()
    {
        try
        {
            bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
            Aurora.SetDark(dark);
            Windows.UI.Color Argb(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);
            var fg = dark ? Argb(255, 255, 255, 255) : Argb(255, 20, 20, 20);
            var tb = AppWindow.TitleBar;
            tb.ButtonForegroundColor = fg; tb.ButtonHoverForegroundColor = fg; tb.ButtonPressedForegroundColor = fg;
            tb.ButtonInactiveForegroundColor = dark ? Argb(255, 150, 155, 170) : Argb(255, 110, 115, 130);
            tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.ButtonHoverBackgroundColor = dark ? Argb(40, 255, 255, 255) : Argb(30, 0, 0, 0);
            tb.ButtonPressedBackgroundColor = dark ? Argb(70, 255, 255, 255) : Argb(55, 0, 0, 0);
        }
        catch { }
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

    void Open_Toggled(object s, RoutedEventArgs e) { if (loadingSettings) return; cfg.OpenWhenDone = OpenSwitch.IsOn; SaveSettings(); }
    void Mobile_Toggled(object s, RoutedEventArgs e) { if (loadingSettings) return; cfg.MobileShare = MobileSwitch.IsOn; SaveSettings(); }

    void Reset_Click(object s, RoutedEventArgs e)
    {
        cfg = new();
        dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MoveIt_Received");
        SaveSettings(); ApplyTheme("Default"); RefreshSettingsUi();
    }

    // ---------- history ----------
    void OpenHistory_Click(object s, RoutedEventArgs e) { RefreshHistory(); ShowPage(HistoryPage); }

    void RefreshHistory()
    {
        var rows = HistoryStore.Load().Select(HistoryRow.From).ToList();
        HistList.ItemsSource = rows;
        HistEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HistClearBtn.IsEnabled = rows.Count > 0;
    }

    async void HistClear_Click(object s, RoutedEventArgs e)
    {
        if (await Confirm("Clear history?", "This removes the list of past transfers. Your files are not touched.", "Clear", "Keep")) { HistoryStore.Clear(); RefreshHistory(); }
    }

    // ---------- home ----------
    void SendCard_Click(object s, RoutedEventArgs e) { ShowPage(ScanPage); StartScan(); }
    void RxCard_Click(object s, RoutedEventArgs e) { if (cfg.MobileShare) ShowPage(RxChoosePage); else StartReceiving(false); }
    void RxFromLaptop_Click(object s, RoutedEventArgs e) => StartReceiving(false);
    void RxFromPhone_Click(object s, RoutedEventArgs e) => StartReceiving(true);

    // ---------- scan ----------
    async void StartScan()
    {
        scanCts?.Cancel(); scanCts = new(); var ct = scanCts.Token;
        devices.Clear(); misses.Clear(); scanRounds = 0; ScanRadar.DeviceLabel = DeviceName; ScanRadar.Start();
        UpdateScanUi();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var found = await Sender.Scan();
                if (ct.IsCancellationRequested) break;
                scanRounds++;
                foreach (var (ip, name) in found)
                {
                    misses[ip] = 0;
                    if (!devices.Any(d => d.Ip == ip)) devices.Add(new DeviceItem(ip, name));
                }
                foreach (var d in devices.ToList())   // drop devices that were not seen 3 searches in a row
                    if (!found.Any(f => f.Ip == d.Ip)) { misses[d.Ip] = misses.GetValueOrDefault(d.Ip) + 1; if (misses[d.Ip] >= 3) devices.Remove(d); }
                UpdateScanUi();
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { scanRounds++; UpdateScanUi(); await Task.Delay(1500); }
        }
    }

    void UpdateScanUi()
    {
        bool any = devices.Count > 0;
        ScanTitle.Text = any ? $"{devices.Count} laptop{(devices.Count == 1 ? "" : "s")} found. Tap one" : scanRounds >= 3 ? "No laptops found yet" : "Looking for nearby laptops…";
        ScanLoading.Visibility = Visibility.Visible;
        ScanEmpty.Visibility = !any && scanRounds >= 3 ? Visibility.Visible : Visibility.Collapsed;
        DeviceGrid.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    void Device_Click(object s, ItemClickEventArgs e) { if (e.ClickedItem is DeviceItem d) ChooseTarget(d.Ip, d.Name); }

    async void Manual_Click(object s, RoutedEventArgs e)
    {
        var code = new TextBox { Header = "Pairing code (shown on the receiving laptop)", MaxLength = 6, PlaceholderText = "000000" };
        var ip = new TextBox { Header = "IP address (optional, leave empty to find automatically)", PlaceholderText = "e.g. 192.168.1.5" };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(code); panel.Children.Add(ip);
        var dlg = new ContentDialog
        {
            Title = "Connect manually", Content = panel, PrimaryButtonText = "Connect", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || code.Text.Trim() == "") return;
        string host = ip.Text.Trim(), name = host;
        if (host == "")
        {
            var found = await Sender.Scan();
            if (found.Count == 0) { await Msg("Not found", "No laptop found on this WiFi. Type its IP address too."); return; }
            host = found[0].Ip; name = found[0].Name;
        }
        GoSend(host, name, code.Text.Trim());
    }

    async void ChooseTarget(string ip, string name)
    {
        var box = new TextBox { Header = "6-digit code shown on the receiving laptop", MaxLength = 6, PlaceholderText = "000000" };
        var dlg = new ContentDialog
        {
            Title = $"Connect to {name}", Content = box, PrimaryButtonText = "Connect", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim() == "") return;
        GoSend(ip, name, box.Text.Trim());
    }

    void GoSend(string ip, string name, string code)
    {
        targetIp = ip; targetCode = code; targetName = name;
        scanCts?.Cancel(); ScanRadar.Stop();
        SendTitle.Text = $"Send to {name}";
        ResetSendView();
        ShowPage(SendPage);
    }

    // ---------- send ----------
    void RefreshItemsUi()
    {
        ItemsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        long total = items.Where(i => !i.Folder).Sum(i => i.Size);
        SendSummary.Text = items.Count == 0 ? "" : $"{items.Count} item{(items.Count == 1 ? "" : "s")}" + (total > 0 ? $"  •  {Fmt(total)}" : "");
    }

    static SendItem MakeItem(string path, bool folder)
    {
        long size = 0;
        if (!folder) { try { size = new FileInfo(path).Length; } catch { } }
        return new SendItem
        {
            FullPath = path, Folder = folder, Size = size,
            Name = Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path,
            Detail = folder ? "Folder  •  " + path : Fmt(size) + "  •  " + (Path.GetDirectoryName(path) ?? "")
        };
    }

    void AddItem(string path, bool folder) { if (!items.Any(i => i.FullPath == path)) items.Add(MakeItem(path, folder)); }

    async void AddFiles_Click(object s, RoutedEventArgs e)
    {
        var p = Init(new FileOpenPicker()); p.FileTypeFilter.Add("*");
        if (ModeRadio.SelectedIndex == 0)
        {
            var one = await p.PickSingleFileAsync();
            if (one != null) { items.Clear(); AddItem(one.Path, false); }
            return;
        }
        var fs = await p.PickMultipleFilesAsync();
        if (fs != null) foreach (var f in fs) AddItem(f.Path, false);
    }

    async void AddFolder_Click(object s, RoutedEventArgs e)
    {
        var p = Init(new FolderPicker()); p.FileTypeFilter.Add("*");
        var f = await p.PickSingleFolderAsync();
        if (f != null) AddItem(f.Path, true);
    }

    void RemoveItem_Click(object s, RoutedEventArgs e)
    {
        if (s is Button b && b.Tag is string path) { var it = items.FirstOrDefault(i => i.FullPath == path); if (it != null) items.Remove(it); }
    }

    void Clear_Click(object s, RoutedEventArgs e) => items.Clear();

    void Mode_Changed(object s, SelectionChangedEventArgs e)
    {
        int m = ModeRadio.SelectedIndex;
        FilesCard.Visibility = m == 2 ? Visibility.Collapsed : Visibility.Visible;
        MigCard.Visibility = m == 2 ? Visibility.Visible : Visibility.Collapsed;
        AddFolderBtn.Visibility = m == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (m == 0 && items.Count > 1) { var first = items[0]; items.Clear(); items.Add(first); }
    }

    void SelectAll_Click(object s, RoutedEventArgs e)
    {
        foreach (var c in new[] { CbDesktop, CbDocuments, CbDownloads, CbPictures, CbVideos, CbMusic, CbApps }) c.IsChecked = true;
    }

    void ResetSendView()
    {
        PickPanel.Visibility = Visibility.Visible; SendingPanel.Visibility = Visibility.Collapsed;
        TxResult.Visibility = Visibility.Collapsed; TxLive.Visibility = Visibility.Visible;
        SendFx.Stop();
    }

    readonly RateMeter txRate = new(); long txTotal; int txFiles;

    void ShowTx(Prog p)
    {
        double pct = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
        txTotal = p.Total;
        double speed = txRate.Update(p.Done, Environment.TickCount64);
        TxBar.Value = pct; TxPercent.Text = $"{pct:0}%";
        if (!string.IsNullOrEmpty(p.File)) TxFile.Text = p.Count > 1 ? $"{p.File}  ({p.Index} of {p.Count})" : p.File;
        TxStat.Text = $"{Fmt(p.Done)} of {Fmt(p.Total)}  •  {(speed > 0 ? Fmt(speed) + "/s" : "—")}  •  ETA {AppInfo.Eta(p.Total - p.Done, speed)}";
    }

    async void Cancel_Click(object s, RoutedEventArgs e)
    {
        if (txCts == null || CancelBtn.IsEnabled == false) return;
        if (!await Confirm("Cancel this transfer?", "The receiver keeps files that already finished. The file in progress is discarded.", "Cancel transfer", "Keep sending")) return;
        CancelBtn.IsEnabled = false; TxStat.Text = "Cancelling…";
        txCts?.Cancel();
    }

    void ShowTxResult(string glyph, Brush dot, string title, string msg, string details, bool failed)
    {
        TxLive.Visibility = Visibility.Collapsed;
        TxResIcon.Glyph = glyph; TxResDot.Fill = dot; TxResTitle.Text = title; TxResMsg.Text = msg; TxResDetails.Text = details;
        TxAgainBtn.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        TxDeviceBtn.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        DoneBtn.Content = failed ? "Back to home" : "Done";
        TxResult.Visibility = Visibility.Visible;
    }

    Brush Res(string key) => (Brush)Application.Current.Resources[key];

    void TxAgain_Click(object s, RoutedEventArgs e) => ResetSendView();   // back to the file picker, selection is kept
    void TxChangeDevice_Click(object s, RoutedEventArgs e) { ResetSendView(); ShowPage(ScanPage); StartScan(); }
    void Done_Click(object s, RoutedEventArgs e) { StopAll(); items.Clear(); ResetSendView(); ShowPage(HomePage); }

    async void Send_Click(object s, RoutedEventArgs e)
    {
        bool full = ModeRadio.SelectedIndex == 2;
        var dirs = new List<(string name, string path)>();
        string prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (full && CbDesktop.IsChecked == true) dirs.Add(("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
        if (full && CbDocuments.IsChecked == true) dirs.Add(("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        if (full && CbDownloads.IsChecked == true) dirs.Add(("Downloads", Path.Combine(prof, "Downloads")));
        if (full && CbPictures.IsChecked == true) dirs.Add(("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));
        if (full && CbVideos.IsChecked == true) dirs.Add(("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)));
        if (full && CbMusic.IsChecked == true) dirs.Add(("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)));
        bool apps = full && CbApps.IsChecked == true;
        if ((!full && items.Count == 0) || (full && dirs.Count == 0 && !apps)) { await Msg("Nothing selected", full ? "Tick at least one option." : "Add a file first."); return; }

        PickPanel.Visibility = Visibility.Collapsed; SendingPanel.Visibility = Visibility.Visible;
        TxResult.Visibility = Visibility.Collapsed; TxLive.Visibility = Visibility.Visible;
        CancelBtn.IsEnabled = true;
        TxBar.Value = 0; TxPercent.Text = "0%"; TxFile.Text = ""; txRate.Reset(); txTotal = 0; txFiles = 0;
        SendFx.DeviceLabel = DeviceName; SendFx.Transfer(true);
        txCts = new(); var token = txCts.Token;
        var snapshot = full ? new List<(string path, bool folder)>() : items.Select(i => (i.FullPath, i.Folder)).ToList();
        var prog = new Progress<Prog>(ShowTx);   // created on the UI thread, so reports arrive on the UI thread (the sender reports every ~200 ms)
        string status = "Failed"; string device = targetName;
        try
        {
            TxStat.Text = "Collecting files…";
            var files = await Task.Run(async () =>
            {
                var l = new List<(string, string)>();
                foreach (var (path, folder) in snapshot)
                    if (!folder) l.Add((path, Path.GetFileName(path)));
                    else l.AddRange(Sender.Walk(path, Path.GetFileName(path.TrimEnd('\\', '/'))));
                foreach (var (name, path) in dirs)
                    if (Directory.Exists(path)) l.AddRange(Sender.Walk(path, "UserData/" + name));
                if (apps)
                {
                    DispatcherQueue.TryEnqueue(() => TxStat.Text = "Exporting app list (may take a minute)…");
                    foreach (var f in await Sender.ExportApps()) l.Add((f, "AppList/" + Path.GetFileName(f)));
                }
                return l;
            });
            token.ThrowIfCancellationRequested();
            txFiles = files.Count;
            TxStat.Text = "Connecting…";
            await Sender.Send(targetIp, targetCode, DeviceName, files, prog, token);
            TxBar.Value = 100; TxPercent.Text = "100%"; SendFx.Done(); status = "Completed";
            ShowTxResult("\uE73E", Res("OkBrush"), "Transfer Complete", $"Sent to {targetName}",
                $"Files: {txFiles}\nTotal size: {Fmt(txTotal)}", false);
        }
        catch (RemoteCancelledException)
        {
            status = "Cancelled"; SendFx.Stop();
            ShowTxResult("\uE711", Res("WarnBrush"), "Cancelled", "Cancelled by receiver", "The receiver stopped the transfer. Files that already finished are kept on the receiving laptop.", true);
        }
        catch (OperationCanceledException)
        {
            status = "Cancelled"; SendFx.Stop();
            ShowTxResult("\uE711", Res("WarnBrush"), "Cancelled", "You cancelled the transfer", "Files that already finished are kept on the receiving laptop. The file in progress was discarded.", true);
        }
        catch (Exception ex)
        {
            SendFx.Stop();
            ShowTxResult("\uE7BA", Res("ErrBrush"), "Transfer failed", ex.Message, ex.Message.Contains("pairing code") ? "Go back and enter the code shown on the receiving laptop." : "Check that both laptops are on the same WiFi and the receiving laptop is still on the Receive screen.", true);
        }
        finally
        {
            CancelBtn.IsEnabled = true; txCts?.Dispose(); txCts = null;
            HistoryStore.Add(new HistoryEntry(DateTime.Now, "Sent", device, txFiles, txTotal, status));
        }
    }

    // ---------- receive ----------
    int rxGen; bool phoneMode, rxConnected, rxReceiving, rxFatal;
    string peerName = "", peerType = ""; int rxCount; long rxTotal; Prog? rxLatest; long rxStartMs, terminalAt;
    readonly RateMeter rxRate = new();
    string stage = "";    // staging folder of the current receive session (files land here first, then move to the folder the user picks)
    string savedTo = "";  // folder where the finished files really ended up

    static string IncomingRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "Incoming");

    static string NewStage()
    {
        var d = Path.Combine(IncomingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // Moves finished files from the staging folder into target (keeps sub folders, renames duplicates). Unfinished .part files stay. Returns how many files moved.
    static int MoveStaged(string from, string target)
    {
        int n = 0;
        if (!Directory.Exists(from)) return 0;
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList())
        {
            if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var to = Net.UniquePath(Path.Combine(target, Path.GetRelativePath(from, f)));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(f, to); n++;
            }
            catch { }
        }
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length).ToList())
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        return n;
    }

    // If the app was closed or crashed while files were still in staging, put them in <save folder>\Recovered at the next start.
    void RecoverIncoming()
    {
        try
        {
            var root = IncomingRoot;
            var old = Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>();
            if (old.Length == 0) return;
            var target = Path.Combine(dest, "Recovered");
            _ = Task.Run(() =>
            {
                foreach (var d in old) { try { MoveStaged(d, target); Directory.Delete(d, true); } catch { } }
            });
        }
        catch { }
    }

    void SetRxState(string text) => RxStateText.Text = text;

    // waiting view (only code / QR) <-> full-screen live view (particles + transfer data)
    void ShowRxLive(bool live)
    {
        RxWaitView.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        RxLiveView.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        if (live) { RxLiveView.UpdateLayout(); UpdateStageCenter(); }
    }

    void ShowRxResult(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        RxResultHead.Visibility = v; RxResult.Visibility = v; RxResDetails.Visibility = v;
    }

    // details panel (file, format, size, speed...) sits at the bottom. While receiving it shows for a few seconds and hides by itself;
    // hover / tap on the device icon (or on the panel) shows it again. After the transfer ends it stays (result + buttons).
    void ShowRxPanel(bool show) { RxBottom.Opacity = show ? 1 : 0; RxBottom.IsHitTestVisible = show; }
    void PulseRxPanel() { ShowRxPanel(true); rxPanelTimer.Stop(); rxPanelTimer.Interval = TimeSpan.FromSeconds(4); rxPanelTimer.Start(); }
    void RxBottom_PointerEntered(object s, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { rxHover = true; rxPanelTimer.Stop(); }
    void RxBottom_PointerExited(object s, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { rxHover = false; if (rxReceiving) { rxPanelTimer.Interval = TimeSpan.FromSeconds(1.5); rxPanelTimer.Start(); } }

    // the device icon keeps ONE fixed place: in the free space above the (maximum height) details panel, so it never jumps when the panel changes
    void RxLive_SizeChanged(object s, SizeChangedEventArgs e) => UpdateStageCenter();
    void RxBottom_SizeChanged(object s, SizeChangedEventArgs e) { }
    void UpdateStageCenter()
    {
        double h = RxLiveView.ActualHeight; if (h <= 0) return;
        double top = 64, bottom = h - 300;                    // ~300 px reserved for the details panel
        double center = bottom > top + 160 ? (top + bottom) / 2 : Math.Max(120, h * 0.3);
        Particle.CenterFraction = Math.Clamp(center / h, 0.15, 0.5);
    }

    void StartReceiving(bool phone)
    {
        StopAll(); int gen = ++rxGen;
        phoneMode = phone; rxFatal = false; savedTo = "";
        try { stage = NewStage(); } catch { stage = ""; }
        string recvDir = stage != "" ? stage : dest;   // fallback: save straight into the default folder
        var code = RandomNumberGenerator.GetInt32(1000000).ToString("D6");
        CodeText.Text = code;
        CodeCard.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        PhoneCard.Visibility = Visibility.Collapsed;
        RxTitle.Text = phone ? "Scan to send files" : "Ready to receive";
        RxName.Text = $"Visible as {DeviceName}  •  {Net.LocalIp()}";
        RxName.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        ResetRxView(phone ? "" : "Waiting for the other laptop…");
        ShowPage(RxPage);
        Action<RxEvent> raise = ev => DispatcherQueue.TryEnqueue(() => { if (gen == rxGen) OnRxEvent(ev); });
        Action<Prog> prog = p => OnRxProg(p, gen);
        if (phone)
        {
            web = new WebShare(code, recvDir, DeviceName, raise, prog);
            try { web.Start(); ShowQr($"http://{Net.LocalIp()}:{WebShare.Port}/?c={code}"); }
            catch (Exception ex) { web = null; RxStatus.Text = "Could not start phone sharing: " + ex.Message; }
            return;
        }
        rx = new Receiver(code, recvDir, raise, prog, DeviceName);
        rx.Start();
    }

    void ResetRxView(string status)
    {
        rxReceiving = false; rxConnected = false; rxTimer.Stop();
        Particle.Stop(); ShowRxLive(false); rxPanelTimer.Stop(); rxPanelOk = false; ShowRxPanel(false);
        ShowRxResult(false); RxProgCard.Visibility = Visibility.Collapsed; RxCancelBtn.Visibility = Visibility.Collapsed;
        InstallBtn.Visibility = Visibility.Collapsed; RxNote.Visibility = Visibility.Collapsed;
        RxDevice.Text = RxFile.Text = RxFormat.Text = RxSize.Text = RxFiles.Text = "—";
        RxBar.Value = 0; RxPercent.Text = "0%"; RxCancelBtn.IsEnabled = true;
        RxStatus.Text = status; SetRxState("Waiting");
    }

    // called from background threads: only store the latest snapshot, the UI timer applies it every ~120 ms
    void OnRxProg(Prog p, int gen)
    {
        if (gen != rxGen) return;
        if (Environment.TickCount64 - terminalAt < 400 && p.Total > 0 && p.Done >= p.Total && !rxReceiving) return; // late final report after Complete
        Volatile.Write(ref rxLatest, p);
        DispatcherQueue.TryEnqueue(() => { if (gen == rxGen && !rxTimer.IsRunning) rxTimer.Start(); });
    }

    void RxTick()
    {
        var p = Interlocked.Exchange(ref rxLatest, null);
        if (p == null) return;
        if (!rxReceiving) EnterReceiving();
        double pct = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
        double speed = rxRate.Update(p.Done, Environment.TickCount64);
        RxBar.Value = pct; RxPercent.Text = $"{pct:0}%"; Particle.SetProgress(pct);
        RxStat.Text = $"{Fmt(p.Done)} of {Fmt(p.Total)}";
        RxSpeed.Text = speed > 0 ? Fmt(speed) + "/s" : "—";
        RxEta.Text = "ETA " + AppInfo.Eta(p.Total - p.Done, speed);
        RxSize.Text = Fmt(p.Total);
        if (!string.IsNullOrEmpty(p.File)) { RxFile.Text = p.File; RxFormat.Text = AppInfo.FormatOf(p.File); }
        if (p.Count > 0) RxFiles.Text = $"{p.Index} of {p.Count}";
    }

    void EnterReceiving()
    {
        if (!rxConnected) { rxConnected = true; ShowRxLive(true); Particle.Start(); Particle.SetDevice(peerType); Particle.Connect(); }   // e.g. 2nd phone batch without a new Connected event
        rxReceiving = true; rxPanelOk = true; Particle.SetProgress(0); PulseRxPanel(); rxRate.Reset(); rxStartMs = Environment.TickCount64;
        ShowRxResult(false); InstallBtn.Visibility = Visibility.Collapsed;
        RxProgCard.Visibility = Visibility.Visible; RxCancelBtn.Visibility = Visibility.Visible; RxCancelBtn.IsEnabled = true;
        RxStatus.Text = $"Receiving from {(peerName == "" ? "device" : peerName)}…"; SetRxState("Receiving");
    }

    void OnRxEvent(RxEvent ev)
    {
        switch (ev.State)
        {
            case RxState.Listening:
                if (ev.Message != "") { RxNote.Text = ev.Message; RxNote.Visibility = Visibility.Visible; }
                break;
            case RxState.Connected:
                peerName = ev.Device; peerType = ev.DeviceType; rxCount = ev.Count; rxTotal = ev.Total;
                RxNote.Visibility = Visibility.Collapsed; ShowRxResult(false); InstallBtn.Visibility = Visibility.Collapsed;
                RxDevice.Text = $"{peerName} • {peerType}";
                if (rxTotal > 0) RxSize.Text = Fmt(rxTotal);
                if (rxCount > 0) RxFiles.Text = $"0 of {rxCount}";
                rxConnected = true;
                ShowRxLive(true); rxPanelOk = false; ShowRxPanel(false); Particle.Start(); Particle.SetDevice(peerType); Particle.Connect();   // only the device icon + ripples; details appear when the transfer starts
                RxStatus.Text = ev.Message; SetRxState("Connected");
                break;
            case RxState.Receiving:
                if (!rxReceiving) EnterReceiving();
                break;
            case RxState.Complete: case RxState.Cancelled: case RxState.Failed: case RxState.Disconnected:
                FinishRx(ev); break;
        }
    }

    void FinishRx(RxEvent ev)
    {
        bool was = rxReceiving;
        if (ev.Device != "") peerName = ev.Device;
        rxReceiving = false; rxConnected = false; rxTimer.Stop(); terminalAt = Environment.TickCount64;
        Interlocked.Exchange(ref rxLatest, null);
        RxProgCard.Visibility = Visibility.Collapsed; RxCancelBtn.Visibility = Visibility.Collapsed;
        rxFatal = ev.State == RxState.Failed && ev.Message.StartsWith("Cannot listen");

        if (ev.State == RxState.Disconnected && !was)   // phone page closed before sending anything: just go back to waiting
        { ResetRxView(phoneMode ? "" : ev.Message + ". Waiting…"); return; }

        bool wasLive = RxLiveView.Visibility == Visibility.Visible;
        ShowRxLive(true); rxPanelTimer.Stop(); rxPanelOk = true; ShowRxPanel(true); if (!wasLive) { Particle.Start(); Particle.SetDevice(peerType); }
        int gen = rxGen; string st = stage;

        string title, msg, details, glyph; Brush dot; string hist;
        RxOpenBtn.Visibility = Visibility.Collapsed; InstallBtn.Visibility = Visibility.Collapsed;
        if (ev.State == RxState.Complete)
        {
            Particle.Complete(); hist = "Completed"; glyph = "\uE73E"; dot = Res("OkBrush");
            var files = ev.Files ?? new List<ReceivedFile>();
            long size = ev.Total > 0 ? ev.Total : files.Sum(f => f.Size);
            string name = files.Count == 1 ? files[0].Name : $"{files.Count} files";
            var exts = files.Select(f => AppInfo.FormatOf(f.Name)).Distinct().ToList();
            string fmt = files.Count == 1 ? AppInfo.FormatOf(files[0].Name) : exts.Count == 0 ? "—" : string.Join(", ", exts.Take(3)) + (exts.Count > 3 ? "…" : "");
            title = "Transfer Complete"; msg = ev.Message;
            details = "Choose where to save the files…";
            RxSize.Text = Fmt(size); RxFile.Text = name; RxFormat.Text = fmt;
            HistoryStore.Add(new HistoryEntry(DateTime.Now, "Received", peerName == "" ? "Device" : peerName, files.Count, size, hist));
            SetRxState("Complete"); RxStatus.Text = "Complete";
            RxAgainBtn.Content = "Receive more";
            _ = SaveReceivedAsync(files.Count, st, gen);   // asks where to save, then moves the files there
        }
        else
        {
            Particle.Error();   // cancelled / failed / disconnected: red stage + red X
            if (ev.State == RxState.Cancelled) { hist = "Cancelled"; title = "Transfer cancelled"; glyph = "\uE711"; dot = Res("ErrBrush"); details = "Files that finished before the cancel are kept. The file in progress was deleted."; SetRxState("Cancelled"); }
            else if (ev.State == RxState.Disconnected) { hist = "Disconnected"; title = "Device disconnected"; glyph = "\uE7BA"; dot = Res("ErrBrush"); details = "The connection dropped (WiFi off or out of range). Files that finished are kept."; SetRxState("Disconnected"); }
            else { hist = "Failed"; title = "Transfer failed"; glyph = "\uE7BA"; dot = Res("ErrBrush"); details = "Check that both devices are on the same WiFi, then try again."; SetRxState("Failed"); }
            msg = ev.Message; RxStatus.Text = title;
            RxAgainBtn.Content = "Try again";
            if (was || ev.State != RxState.Failed || !rxFatal)
                HistoryStore.Add(new HistoryEntry(DateTime.Now, "Received", peerName == "" ? "Device" : peerName, 0, rxTotal, hist));
            _ = FlushAfterStopAsync(st, gen);   // finished files go to the default save folder
        }
        RxResIcon.Glyph = glyph; RxResDot.Fill = dot; RxResTitle.Text = title; RxResMsg.Text = msg; RxResDetails.Text = details;
        ShowRxResult(true);
    }

    // After a COMPLETE transfer: ask where to save, then move the files from the staging folder.
    async Task SaveReceivedAsync(int count, string stageDir, int gen)
    {
        string target = dest;
        try
        {
            if (stageDir != "" && count > 0 && !dialogOpen)
            {
                dialogOpen = true;
                try
                {
                    var dlg = new ContentDialog
                    {
                        Title = "Where should we save the files?",
                        Content = new TextBlock { Text = $"{count} file{(count == 1 ? "" : "s")} received.\nDefault folder:\n{dest}", TextWrapping = TextWrapping.Wrap },
                        PrimaryButtonText = "Save to default folder", SecondaryButtonText = "Choose another folder…",
                        DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme
                    };
                    if (await dlg.ShowAsync() == ContentDialogResult.Secondary)
                    {
                        var picker = Init(new FolderPicker()); picker.FileTypeFilter.Add("*");
                        var f = await picker.PickSingleFolderAsync();
                        if (f != null) target = f.Path;
                    }
                }
                finally { dialogOpen = false; }
            }
            if (stageDir != "") await Task.Run(() => MoveStaged(stageDir, target));
            savedTo = target;
            if (gen != rxGen) return;
            RxResDetails.Text = $"Saved to: {target}";
            RxOpenBtn.Visibility = Visibility.Visible;
            InstallBtn.Visibility = File.Exists(Path.Combine(target, "AppList", "apps.json")) ? Visibility.Visible : Visibility.Collapsed;
            if (cfg.OpenWhenDone) OpenDest();
        }
        catch (Exception ex)
        {
            if (gen == rxGen) RxResDetails.Text = "Could not save the files: " + ex.Message + (stageDir != "" ? "\nThey are still in: " + stageDir : "");
        }
    }

    // After cancelled / failed / disconnected: finished files are kept in the default save folder.
    async Task FlushAfterStopAsync(string stageDir, int gen)
    {
        if (stageDir == "") return;
        var target = dest; int n = 0;
        try { n = await Task.Run(() => MoveStaged(stageDir, target)); } catch { }
        if (n > 0)
        {
            savedTo = target;
            if (gen == rxGen) { RxResDetails.Text += $"\nFinished files saved to: {target}"; RxOpenBtn.Visibility = Visibility.Visible; }
        }
    }

    void OpenDest() { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{(savedTo != "" ? savedTo : dest)}\"") { UseShellExecute = true }); } catch { } }

    void RxOpen_Click(object s, RoutedEventArgs e) => OpenDest();
    void RxAgain_Click(object s, RoutedEventArgs e) { savedTo = ""; if (rxFatal) StartReceiving(phoneMode); else ResetRxView(phoneMode ? "" : "Waiting for the other laptop…"); }
    async void RxDone_Click(object s, RoutedEventArgs e) { rxReceiving = false; await GoBackAsync(); }

    void CancelRx() { RxCancelBtn.IsEnabled = false; RxStatus.Text = "Cancelling…"; SetRxState("Cancelling…"); if (phoneMode) web?.CancelTransfer(); else rx?.CancelTransfer(); }

    async void RxCancel_Click(object s, RoutedEventArgs e)
    {
        if (!rxReceiving) return;
        if (!await Confirm("Cancel this transfer?", "Files that already finished are kept. The file in progress is deleted and the sender is told.", "Cancel transfer", "Keep receiving")) return;
        if (rxReceiving) CancelRx();
    }

    async void ShowQr(string url)
    {
        var png = new PngByteQRCode(new QRCodeGenerator().CreateQrCode(url, QRCodeGenerator.ECCLevel.Q)).GetGraphic(8);
        var ms = new InMemoryRandomAccessStream();
        await ms.WriteAsync(png.AsBuffer()); ms.Seek(0);
        var bi = new BitmapImage(); await bi.SetSourceAsync(ms);
        QrImg.Source = bi; PhoneCard.Visibility = Visibility.Visible;
        shareUrl = url; RxLink.Text = url.Replace("http://", "");
    }

    string shareUrl = "";
    async void CopyLink_Click(object s, RoutedEventArgs e)
    {
        if (shareUrl == "") return;
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText(shareUrl);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        CopyLinkText.Text = "Copied ✓"; await Task.Delay(1800); CopyLinkText.Text = "Copy link";
    }

    // ---------- setup (first run) ----------
    async void Firewall_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"MoveIt\" dir=in action=allow program=\"{Environment.ProcessPath}\" profile=private,public enable=yes")
            { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            using var p = System.Diagnostics.Process.Start(psi)!;
            await p.WaitForExitAsync();
            FwStatus.Text = p.ExitCode == 0 ? "✓ Allowed. You're all set, tap Continue." : "Could not set it. You can also tap Allow when Windows asks.";
        }
        catch { FwStatus.Text = "Cancelled. You can also tap Allow when Windows asks the first time."; }
    }


    // ---------- onboarding wizard ----------
    int step; const int LastStep = 4;
    StackPanel[] steps = Array.Empty<StackPanel>();

    Border Row(string glyph, string title, string desc, string c1 = "#FF2F80ED", string c2 = "#FF56CCF2")
    {
        Windows.UI.Color C(string h) => Windows.UI.Color.FromArgb(Convert.ToByte(h.Substring(1, 2), 16), Convert.ToByte(h.Substring(3, 2), 16), Convert.ToByte(h.Substring(5, 2), 16), Convert.ToByte(h.Substring(7, 2), 16));
        var g = new Grid { Width = 46, Height = 46, VerticalAlignment = VerticalAlignment.Top };
        g.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops = { new GradientStop { Color = C(c1), Offset = 0 }, new GradientStop { Color = C(c2), Offset = 1 } }
            }
        });
        g.Children.Add(new FontIcon { Glyph = glyph, FontSize = 20, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) });
        var t = new StackPanel { Spacing = 2 };
        t.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        t.Children.Add(new TextBlock { Text = desc, Opacity = 0.75, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(g); Grid.SetColumn(t, 1); row.Children.Add(t);
        return new Border { Style = (Style)Application.Current.Resources["GlassCard"], Padding = new Thickness(16), Child = row };
    }

    void BuildSetup()
    {
        steps = new[] { Step0, Step1, Step2, Step3, Step4 };
        S0Host.Children.Add(Row("\uE72E", "Private by design", "Files travel directly between your laptops on your WiFi. Nothing is uploaded anywhere."));
        S0Host.Children.Add(Row("\uE945", "Fast and simple", "No cables, no pen drives, no accounts. Just pair with a 6-digit code.", "#FF11998E", "#FF38EF7D"));
        S0Host.Children.Add(Row("\uE8B7", "Move everything", "Single files, whole folders, or your complete setup including your app list.", "#FF7A4DFF", "#FFB28DFF"));
        S1Host.Children.Add(Row("\uE896", "1. Open Receive on the new laptop", "It shows a 6-digit pairing code and waits for the other laptop."));
        S1Host.Children.Add(Row("\uE898", "2. Open Send on the old laptop", "The new laptop appears on the radar. Tap it and type the code. Not showing up? Use 'Enter pairing code or IP address' on that screen.", "#FF11998E", "#FF38EF7D"));
        S1Host.Children.Add(Row("\uE8E5", "3. Choose what to send", "Pick your files and tap Send. Watch the transfer animate on both laptops.", "#FF7A4DFF", "#FFB28DFF"));
        S2Host.Children.Add(Row("\uE8A5", "Single file", "Send just one file quickly."));
        S2Host.Children.Add(Row("\uE8B7", "Files and folders", "Pick any number of files and whole folders.", "#FF11998E", "#FF38EF7D"));
        S2Host.Children.Add(Row("\uE71D", "Complete migration", "Desktop, Documents, Downloads, Pictures and more, plus your installed apps list. On the new laptop tap 'Install all apps' to reinstall them in one go.", "#FF7A4DFF", "#FFB28DFF"));
        S2Host.Children.Add(Row("\uE8EA", "From your phone or tablet", "Scan the QR code on the Receive screen with your phone camera, then send one or many files. Works on Android and iPhone.", "#FFF59E0B", "#FFFCD34D"));
        ShowStep(0);
    }

    void ShowStep(int i)
    {
        step = i;
        for (int k = 0; k < steps.Length; k++) steps[k].Visibility = k == i ? Visibility.Visible : Visibility.Collapsed;
        StepBack.Opacity = i == 0 ? 0 : 1; StepBack.IsHitTestVisible = i != 0;
        StepSkip.Visibility = i < 3 ? Visibility.Visible : Visibility.Collapsed;
        StepNext.Content = i == LastStep ? "Get started" : i == 3 ? "Continue" : "Next";
        Dots.Children.Clear();
        for (int k = 0; k <= LastStep; k++)
            Dots.Children.Add(new Border
            {
                Width = k == i ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4),
                Background = k == i ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 47, 128, 237)) : new SolidColorBrush(Windows.UI.Color.FromArgb(70, 160, 170, 190))
            });
    }

    void StepBack_Click(object s, RoutedEventArgs e) { if (step > 0) ShowStep(step - 1); }
    void StepSkip_Click(object s, RoutedEventArgs e) => ShowStep(3);
    void StepNext_Click(object s, RoutedEventArgs e)
    {
        if (step < LastStep) { ShowStep(step + 1); return; }
        cfg.DeviceName = SetupName.Text.Trim(); cfg.MobileShare = SetupMobile.IsOn; cfg.SetupDone = true; SaveSettings();
        RefreshSettingsUi(); ShowPage(HomePage);
    }

    // ---------- install apps on the new laptop ----------
    void InstallApps_Click(object s, RoutedEventArgs e)
    {
        var json = Path.Combine(savedTo != "" ? savedTo : dest, "AppList", "apps.json");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c winget import -i \"{json}\" --accept-package-agreements --accept-source-agreements --ignore-unavailable & pause") { UseShellExecute = true });
        }
        catch { }
    }
}
