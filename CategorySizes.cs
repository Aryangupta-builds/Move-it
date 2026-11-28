using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MoveIt;

// Smart-Switch-style "Select data to transfer": every category row shows its real size, found by a read-only scan on a background
// thread. The running total follows the ticked rows. Nothing is copied here and nothing on disk is changed.
public sealed partial class MainWindow
{
    readonly Dictionary<CheckBox, long> catBytes = new();
    bool catScanned, catScanning;

    void Cat_Changed(object s, RoutedEventArgs e) => UpdateSelectedTotal();

    void UpdateSelectedTotal()
    {
        long sum = 0;
        foreach (var kv in catBytes) if (kv.Key.IsChecked == true) sum += kv.Value;
        SelTotal.Text = AppInfo.Fmt(sum) + " selected" + (catScanning ? " (still scanning...)" : "");
    }

    async void RefreshCategorySizes()
    {
        if (catScanned || catScanning) return;
        catScanning = true;
        try
        {
            var jobs = new (CheckBox cb, TextBlock tb, List<ScanRoot> roots)[]
            {
                (CbDesktop, SzDesktop, MigrationCatalog.Personal(true, false, false, false, false, false)),
                (CbDocuments, SzDocuments, MigrationCatalog.Personal(false, true, false, false, false, false)),
                (CbDownloads, SzDownloads, MigrationCatalog.Personal(false, false, true, false, false, false)),
                (CbPictures, SzPictures, MigrationCatalog.Personal(false, false, false, true, false, false)),
                (CbVideos, SzVideos, MigrationCatalog.Personal(false, false, false, false, true, false)),
                (CbMusic, SzMusic, MigrationCatalog.Personal(false, false, false, false, false, true)),
                (CbBrowser, SzBrowser, MigrationCatalog.Browser()),
                (CbGames, SzGames, MigrationCatalog.Games()),
                (CbDev, SzDev, MigrationCatalog.DevSettings()),
            };
            foreach (var j in jobs) j.tb.Text = "Scanning...";
            SzApps.Text = "Scanning...";
            foreach (var j in jobs)
            {
                var res = await Task.Run(() => j.roots.Select(r => Scanner.Scan(r, CancellationToken.None)).ToList());
                long bytes = res.Sum(x => x.Bytes); int files = res.Sum(x => x.Files.Count);
                catBytes[j.cb] = bytes;
                j.tb.Text = files == 0 ? "Nothing found" : $"{AppInfo.Fmt(bytes)}  ·  {files:N0} file(s)";
                UpdateSelectedTotal();
            }
            int apps = await Task.Run(() => SoftwareInventory.ReadLocal().Count);
            SzApps.Text = $"{apps} app(s) found";
            catScanned = true;
        }
        catch (Exception ex) { SelTotal.Text = "Scan failed: " + ex.Message; }
        finally { catScanning = false; UpdateSelectedTotal(); }
    }
}
