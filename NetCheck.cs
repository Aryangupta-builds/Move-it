using System.Net.Sockets;
using Microsoft.UI.Xaml;

namespace MoveIt;

// Spec 16/17/24: connection test (measured values only) and report folder access.
public sealed partial class MainWindow
{
    async void TestLink_Click(object s, RoutedEventArgs e)
    {
        if (targetIp == "") { await Msg("No laptop selected", "Connect to the other laptop first."); return; }
        string host = targetIp, code = targetCode;
        TestLinkBtn.IsEnabled = false; SendBtn.IsEnabled = false; TestLinkBtn.Content = "Testing… (up to 60 seconds)";
        try
        {
            using var to = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            ShowToolStatus("Measuring the connection (up to 60 seconds)…");
            SpeedResult r; try { r = await Task.Run(() => Sender.SpeedTest(host, code, 32, to.Token)); } finally { HideToolStatus(); }
            var q = Diagnostics.Classify(r);
            string Ms(double v) => v < 0 ? "Not available" : $"{v:0} ms";
            string Rate(double v) => v < 1 ? "Not available" : AppInfo.Fmt(v) + "/s";
            await Msg("Connection: " + q,
                $"Local network: {(System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() ? "available" : "not available")}\nOther laptop: reachable\nPacket loss: Not available\nInterface: {ActiveLink(host)}\nLatency: {Ms(r.LatencyMs)}\nJitter: {Ms(r.JitterMs)}\nTimed-out pings: {r.LostPings}\n" +
                $"Upload: {Rate(r.UploadBps)}\nDownload: {Rate(r.DownloadBps)}\nTest duration: {r.Duration.TotalSeconds:0.0} s\n" +
                $"Time for 1 GB: {Diagnostics.Estimate(1L << 30, r.UploadBps)}\n\n{Diagnostics.Tips(q)}");
        }
        catch (Exception ex)
        {
            await Msg("Connection test failed", (ex is OperationCanceledException ? "The test timed out." : ex.Message) + "\n\n" + Diagnostics.Tips(LinkQuality.Disconnected));
        }
        finally { TestLinkBtn.IsEnabled = true; SendBtn.IsEnabled = true; TestLinkBtn.Content = "Test connection speed"; }
    }

    // The adapter Windows would use to reach this host (UDP connect sends no data).
    static string ActiveLink(string host)
    {
        try
        {
            using var u = new UdpClient(); u.Connect(host, Net.Port);
            var ip = Net.BindIp ?? ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Address.ToString();
            var l = Connections.Detect().FirstOrDefault(x => x.Ip == ip);
            return l == null ? "Not available" : $"{l.Kind} ({l.Name})";
        }
        catch { return "Not available"; }
    }

    async void OpenReports_Click(object s, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ReportStore.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReportStore.Folder) { UseShellExecute = true });
        }
        catch (Exception ex) { await Msg("Cannot open reports", ex.Message); }
    }
}
