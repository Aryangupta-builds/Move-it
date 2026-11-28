using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoveIt;

// Spec 5 (dashboard) and 21 (manual connection selection).
public sealed partial class MainWindow
{
    async void Link_Changed(object s, SelectionChangedEventArgs e)
    {
        var box = (ComboBox)s;
        if (box.SelectedIndex <= 0) { Net.BindIp = null; return; }
        var want = box.SelectedItem as string;
        var link = Connections.Detect().FirstOrDefault(x => x.Kind == want && x.CanCarryTransfer);
        if (link == null || Sending)
        {
            box.SelectedIndex = 0;
            await Msg("Connection not changed", link == null ? $"No usable {want} connection was found. Using Automatic." : "A transfer is running. Using Automatic for new connections.");
            return;
        }
        Net.BindIp = link.Ip;
    }

    // Spec 5: dedicated Migration Center page (replaces the old dialog).
    void Dashboard_Click(object s, RoutedEventArgs e)
    {
        if (targetIp == "") { _ = Msg("Not connected", "Connect to the other laptop first."); return; }
        ShowPage(MigrationPage); _ = LoadMigrationCenter();
    }

    void MigRefresh_Click(object s, RoutedEventArgs e) => _ = LoadMigrationCenter();
    void MigToSend_Click(object s, RoutedEventArgs e) => ShowPage(SendPage);

    async Task LoadMigrationCenter()
    {
        MigRefreshBtn.IsEnabled = false; MigReady.Text = "Checking the other laptop and scanning folders…"; MigData.Text = "";
        try
        {
            using var to = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var peer = await Sender.Probe(targetIp, targetCode, to.Token);
            var scans = await Task.Run(() => MigrationCatalog.Personal(true, true, true, true, true, true).Concat(MigrationCatalog.Browser()).Concat(MigrationCatalog.Games()).Concat(MigrationCatalog.DevSettings()).Select(r => Scanner.Scan(r, CancellationToken.None)).ToList());
            int appCount = await Task.Run(() => SoftwareInventory.ReadLocal().Count);
            long size = scans.Sum(x => x.Bytes), mine = Net.FreeSpace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var battery = Battery.Read(); var gate = MigrationGate.Check(battery, peer, targetName);
            string Space(long v) => v < 0 ? "Not available" : AppInfo.Fmt(v);
            var ready = peer.Busy ? "Not ready: the other laptop is busy" : gate.Result == GateResult.Blocked ? "Not ready: " + gate.Message :
                peer.FreeBytes >= 0 && size > peer.FreeBytes ? "Not enough space for all personal folders" : gate.Result == GateResult.NeedConfirm ? gate.Message : "Ready";
            MigReady.Text = ready; var links = await Task.Run(() => Connections.Detect());
            MigLink.Text = "Active connection: " + ActiveLink(targetIp) + "\n\nAvailable on this laptop:\n" + Connections.Describe(links) + "\n\nWired: " + WiredStatus.Evaluate(false, null).Text;
            MigMine.Text = $"{DeviceName}\n{Environment.OSVersion.VersionString}\nFree: {Space(mine)}\n{Battery.Describe(battery)}";
            MigOther.Text = $"{targetName}\n{(peer.Os == "" ? "OS not available" : peer.Os)}\nFree: {Space(peer.FreeBytes)}\nBattery: {(!peer.HasBattery ? "no battery" : peer.Battery < 0 ? "not available" : peer.Battery + "%")}";
            string Line(ScanResult x) => $"{x.Root.Name}: {x.Files.Count} file(s), {AppInfo.Fmt(x.Bytes)}{(x.EmptyDirs.Count > 0 ? $", {x.EmptyDirs.Count} empty folder(s)" : "")}";
            var sb = new System.Text.StringBuilder();
            foreach (var g in scans.Where(x => x.Exists).GroupBy(x => x.Root.Category))
                sb.AppendLine($"{g.Key} ({g.Sum(x => x.Files.Count)} file(s), {AppInfo.Fmt(g.Sum(x => x.Bytes))})").AppendLine(string.Join("\n", g.Select(x => "   " + Line(x))));
            sb.AppendLine($"Installed software: {appCount} app(s) detected (the list is migrated, program files are not copied)");
            long sel = 0; int selFiles = 0;
            bool On(CheckBox c) => c.IsChecked == true;
            var chosen = new HashSet<string>();
            if (On(CbDesktop)) chosen.Add("Desktop"); if (On(CbDocuments)) chosen.Add("Documents"); if (On(CbDownloads)) chosen.Add("Downloads");
            if (On(CbPictures)) chosen.Add("Pictures"); if (On(CbVideos)) chosen.Add("Videos"); if (On(CbMusic)) chosen.Add("Music");
            foreach (var x in scans.Where(x => x.Exists && ((x.Root.Category == "Personal files" && chosen.Contains(x.Root.Name)) || (x.Root.Category == "Browser data" && On(CbBrowser)) || (x.Root.Category == "Games" && On(CbGames)) || (x.Root.Category == "Development data" && On(CbDev)))))
            { sel += x.Bytes; selFiles += x.Files.Count; }
            sb.AppendLine().Append($"Total detected: {scans.Sum(x => x.Files.Count)} file(s), {AppInfo.Fmt(size)}\nEstimated size of your current selection on the Send page: {selFiles} file(s), {AppInfo.Fmt(sel)}");
            if (peer.FreeBytes >= 0) sb.Append(sel > peer.FreeBytes ? "\nThis selection does NOT fit on the other laptop." : "\nThis selection fits on the other laptop.");
            sb.Append("\nSelection sizes are estimates from a read-only scan; excluded duplicates and empty folders are not subtracted.");
            MigData.Text = sb.ToString();
        }
        catch (Exception ex) { MigReady.Text = "Dashboard unavailable: " + ex.Message; }
        finally { MigRefreshBtn.IsEnabled = true; }
    }
}
