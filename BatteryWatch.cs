using Microsoft.UI.Xaml;

namespace MoveIt;

// Spec 10: warns on this laptop while a transfer runs. Inline text only, so it never collides with a dialog and never stops the transfer.
public sealed partial class MainWindow
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? batteryTimer;

    string? wiredLocalIp;   // set only when the send runs over a wired (USB network) adapter

    void SetWiredWatch(string host)
    {
        wiredLocalIp = null;
        try
        {
            using var u = new System.Net.Sockets.UdpClient(); u.Connect(host, Net.Port);
            var ip = Net.BindIp ?? ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Address.ToString();
            if (Connections.Detect().FirstOrDefault(x => x.Ip == ip)?.Kind == "Wired (USB network)") wiredLocalIp = ip;
        }
        catch { }
    }

    void InitBatteryWatch()
    {
        batteryTimer = DispatcherQueue.CreateTimer(); batteryTimer.Interval = TimeSpan.FromSeconds(20);
        batteryTimer.Tick += (_, _) =>
        {
            var b = Battery.Read();
            bool low = (Sending || rxReceiving) && b.HasBattery && !b.Charging && b.Percent >= 0 && b.Percent < MigrationGate.Minimum;
            bool wiredLost = Sending && WiredStatus.WiredLost(wiredLocalIp);
            var text = wiredLost ? "Wired connection lost. Reconnect the cable. The transfer cannot continue until the link is back; finished files are kept." : low ? $"Battery low: {b.Percent}%. Connect the charger now. The transfer is still running." : "";
            foreach (var t in new[] { TxBatteryWarn, RxBatteryWarn }) { t.Text = text; t.Visibility = text != "" ? Visibility.Visible : Visibility.Collapsed; }
        };
        batteryTimer.Start();
    }
}
