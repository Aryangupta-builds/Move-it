using System.Collections.ObjectModel;
using System.Numerics;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using QRCoder;
using WinRT.Interop;

namespace MoveIt;

public record DeviceItem(string Ip, string Name);

public class AppSettings
{
    public string Theme { get; set; } = "Default";
    public string DeviceName { get; set; } = "";
    public string SaveFolder { get; set; } = "";
    public bool OpenWhenDone { get; set; } = true;
    public bool SetupDone { get; set; } = false;
    public bool MobileShare { get; set; } = true;
}

public sealed partial class MainWindow : Window
{
    readonly List<(string path, bool folder)> items = new();
    readonly ObservableCollection<string> itemView = new();
    readonly ObservableCollection<DeviceItem> devices = new();
    string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MoveIt_Received");
    string targetIp = "", targetCode = "";
    WebShare? web; Receiver? rx; CancellationTokenSource? scanCts, txCts;

    public MainWindow()
    {
        InitializeComponent();
        Title = "MoveIt";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800));
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico")); } catch { }
        Drift(B1, 60, 40, 11); Drift(B2, -50, 60, 14); Drift(B3, 70, -50, 17);
        DeviceGrid.ItemsSource = devices;
        ItemList.ItemsSource = itemView;
        HomeInfo.Text = $"This laptop: {DeviceName}  •  {Net.LocalIp()}";
        Closed += (_, _) => StopAll();
        LoadSettings();
        ExtendsContentIntoTitleBar = true; SetTitleBar(HeaderBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        ModeRadio.SelectedIndex = 1;
        if (!cfg.SetupDone) { SetupName.Text = cfg.DeviceName; SetupName.PlaceholderText = Environment.MachineName; }
        BuildSetup();
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
        if (current != null && current != page) previous = current;
        current = page;
        foreach (var g in new[] { SetupPage, HomePage, ScanPage, SendPage, RxPage, SettingsPage })
        {
            if (g == page) { g.Opacity = 0; g.Visibility = Visibility.Visible; DispatcherQueue.TryEnqueue(() => g.Opacity = 1); }
            else g.Visibility = Visibility.Collapsed;
        }
        BackBtn.Visibility = page == HomePage || page == SetupPage ? Visibility.Collapsed : Visibility.Visible;
        SettingsBtn.Visibility = page == SettingsPage || page == SetupPage ? Visibility.Collapsed : Visibility.Visible;
    }

    // Back works from every page, independent of theme/state
    void GoBack()
    {
        if (current == SetupPage) return;
        if (current == SettingsPage) { ShowPage(previous ?? HomePage); return; }
        StopAll(); ShowPage(HomePage);
    }
    void Back_Accel(KeyboardAccelerator a, KeyboardAcceleratorInvokedEventArgs e) { if (current != HomePage) { GoBack(); e.Handled = true; } }
    void Root_PointerPressed(object s, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(RootGrid).Properties.IsXButton1Pressed && current != HomePage) GoBack();
    }

    void StopAll()
    {
        scanCts?.Cancel(); txCts?.Cancel();
        ScanRadar.Stop(); RxRadar.Stop(); SendFx.Stop();
        rx?.Stop(); rx = null; web?.Stop(); web = null;
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
        FolderText.Text = dest; OpenSwitch.IsOn = cfg.OpenWhenDone; MobileSwitch.IsOn = cfg.MobileShare;
        HomeInfo.Text = $"This laptop: {DeviceName}  •  {Net.LocalIp()}";
        loadingSettings = false;
    }

    void ApplyTheme(string t)
    {
        RootGrid.RequestedTheme = t == "Light" ? ElementTheme.Light : t == "Dark" ? ElementTheme.Dark : ElementTheme.Default;
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

    void Back_Click(object s, RoutedEventArgs e) => GoBack();

    void SendCard_Tapped(object s, TappedRoutedEventArgs e) { ShowPage(ScanPage); StartScan(); }

    void RxCard_Tapped(object s, TappedRoutedEventArgs e) => StartReceiving();

    // ---------- scan ----------
    async void StartScan()
    {
        scanCts?.Cancel(); scanCts = new(); var ct = scanCts.Token;
        devices.Clear(); ScanRadar.DeviceLabel = DeviceName; ScanRadar.Start();
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
        var code = new TextBox { Header = "Pairing code (shown on the receiving laptop)", MaxLength = 6, PlaceholderText = "000000" };
        var ip = new TextBox { Header = "IP address (optional, leave empty to find automatically)", PlaceholderText = "e.g. 192.168.1.5" };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(code); panel.Children.Add(ip);
        var dlg = new ContentDialog
        {
            Title = "Connect manually", Content = panel, PrimaryButtonText = "Connect", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot
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

    void GoSend(string ip, string name, string code)
    {
        targetIp = ip; targetCode = code;
        scanCts?.Cancel(); ScanRadar.Stop();
        SendTitle.Text = $"Send to {name}";
        PickPanel.Visibility = Visibility.Visible; SendingPanel.Visibility = Visibility.Collapsed;
        ShowPage(SendPage);
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
        GoSend(ip, name, box.Text.Trim());
    }

    // ---------- send ----------
    async void AddFiles_Click(object s, RoutedEventArgs e)
    {
        var p = Init(new FileOpenPicker()); p.FileTypeFilter.Add("*");
        if (ModeRadio.SelectedIndex == 0)
        {
            var one = await p.PickSingleFileAsync();
            if (one != null) { items.Clear(); itemView.Clear(); items.Add((one.Path, false)); itemView.Add(one.Path); }
            return;
        }
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
        TxBar.Value = pct; TxPercent.Text = $"{pct:0}%";
        TxStat.Text = $"{Fmt(p.Done)} of {Fmt(p.Total)}  •  {Fmt(p.Speed)}/s";
    }

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
        if ((full ? false : items.Count == 0) || (full && dirs.Count == 0 && !apps)) { await Msg("Nothing selected", full ? "Tick at least one option." : "Add a file first."); return; }

        PickPanel.Visibility = Visibility.Collapsed; SendingPanel.Visibility = Visibility.Visible;
        CancelBtn.Visibility = Visibility.Visible; DoneBtn.Visibility = Visibility.Collapsed;
        TxBar.Value = 0; TxPercent.Text = "0%"; SendFx.DeviceLabel = DeviceName; SendFx.Transfer(true);
        txCts = new(); var snapshot = full ? new List<(string path, bool folder)>() : items.ToList();
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
            TxBar.Value = 100; TxPercent.Text = "Sent!"; SendFx.Done(); TxStat.Text = $"Done! {files.Count} files sent.";
        }
        catch (OperationCanceledException) { TxStat.Text = "Cancelled"; SendFx.Stop(); }
        catch (Exception ex) { TxStat.Text = "Error: " + ex.Message; SendFx.Stop(); }
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
        InstallBtn.Visibility = Visibility.Collapsed; RxRadar.DeviceLabel = DeviceName; ShowPage(RxPage); RxRadar.Start();
        rx = new Receiver(code, dest, m => DispatcherQueue.TryEnqueue(() => OnRxLog(m)),
            new Progress<Prog>(p =>
            {
                RxProgPanel.Visibility = Visibility.Visible;
                RxBar.Value = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
                RxStat.Text = $"{Fmt(p.Done)} of {Fmt(p.Total)}  •  {Fmt(p.Speed)}/s";
            }), DeviceName);
        rx.Start();
        web?.Stop(); web = null; PhoneCard.Visibility = Visibility.Collapsed;
        if (cfg.MobileShare)
        {
            web = new WebShare(code, dest, n => DispatcherQueue.TryEnqueue(() => RxStatus.Text = $"Received from phone: {n}"));
            try { web.Start(); ShowQr($"http://{Net.LocalIp()}:{WebShare.Port}/?c={code}"); } catch { web = null; }
        }
    }

    async void ShowQr(string url)
    {
        var png = new PngByteQRCode(new QRCodeGenerator().CreateQrCode(url, QRCodeGenerator.ECCLevel.Q)).GetGraphic(8);
        var ms = new InMemoryRandomAccessStream();
        await ms.WriteAsync(png.AsBuffer()); ms.Seek(0);
        var bi = new BitmapImage(); await bi.SetSourceAsync(ms);
        QrImg.Source = bi; PhoneUrl.Text = url.Split('?')[0]; PhoneCard.Visibility = Visibility.Visible;
    }

    void OnRxLog(string m)
    {
        RxStatus.Text = m;
        if (m.StartsWith("Connected")) { RxProgPanel.Visibility = Visibility.Visible; RxRadar.Transfer(false); }
        if (m.StartsWith("Done")) { RxRadar.Done(); if (File.Exists(Path.Combine(dest, "AppList", "apps.json"))) InstallBtn.Visibility = Visibility.Visible; }
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

    void Mobile_Toggled(object s, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        cfg.MobileShare = MobileSwitch.IsOn; SaveSettings();
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

    // ---------- send modes ----------
    void Mode_Changed(object s, SelectionChangedEventArgs e)
    {
        int m = ModeRadio.SelectedIndex;
        FilesCard.Visibility = m == 2 ? Visibility.Collapsed : Visibility.Visible;
        MigCard.Visibility = m == 2 ? Visibility.Visible : Visibility.Collapsed;
        AddFolderBtn.Visibility = m == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (m == 0 && items.Count > 1) { var first = items[0]; items.Clear(); itemView.Clear(); items.Add(first); itemView.Add(first.path); }
    }

    void SelectAll_Click(object s, RoutedEventArgs e)
    {
        foreach (var c in new[] { CbDesktop, CbDocuments, CbDownloads, CbPictures, CbVideos, CbMusic, CbApps }) c.IsChecked = true;
    }

    // ---------- install apps on the new laptop ----------
    void InstallApps_Click(object s, RoutedEventArgs e)
    {
        var json = Path.Combine(dest, "AppList", "apps.json");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c winget import -i \"{json}\" --accept-package-agreements --accept-source-agreements --ignore-unavailable & pause") { UseShellExecute = true });
        }
        catch { }
    }
}
