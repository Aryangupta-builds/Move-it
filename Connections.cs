using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MoveIt;

public record LinkInfo(string Kind, string Name, bool Up, long LinkSpeedBps, string Ip, bool CanCarryTransfer);

// Reports what Windows really shows. MoveIt sends data over TCP/IP, so a "wired" connection only works when Windows exposes a
// network adapter for the cable (USB network/bridge cable, USB Ethernet, Thunderbolt/USB4 networking). An ordinary charging cable
// creates no adapter and is therefore never listed as a transfer link. Bluetooth is detected but NOT used to move files.
public static class Connections
{
    public static List<LinkInfo> Detect()
    {
        var l = new List<LinkInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var d = (ni.Description ?? "") + " " + ni.Name;
                string ip = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
                bool up = ni.OperationalStatus == OperationalStatus.Up;
                bool bt = d.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
                bool usb = d.Contains("USB", StringComparison.OrdinalIgnoreCase) || d.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) || d.Contains("Remote NDIS", StringComparison.OrdinalIgnoreCase) || d.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase);
                string kind = bt ? "Bluetooth" : ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : usb ? "Wired (USB network)" : "Ethernet";
                if (d.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || d.Contains("VMware", StringComparison.OrdinalIgnoreCase) || d.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) || d.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)) continue;
                l.Add(new LinkInfo(kind, ni.Name, up, ni.Speed, ip, up && ip != "" && !bt));
            }
        }
        catch { }
        return l;
    }

    // Wired USB network first (when it really exists and is up), then Ethernet, then Wi-Fi. Never silently switches during a transfer.
    public static LinkInfo? Recommend(List<LinkInfo> links)
    {
        var ok = links.Where(x => x.CanCarryTransfer).ToList();
        return ok.FirstOrDefault(x => x.Kind == "Wired (USB network)") ?? ok.FirstOrDefault(x => x.Kind == "Ethernet") ?? ok.FirstOrDefault(x => x.Kind == "Wi-Fi");
    }

    public static string Describe(List<LinkInfo> links)
    {
        if (links.Count == 0) return "No network adapters found.";
        var sb = new System.Text.StringBuilder();
        foreach (var x in links)
            sb.AppendLine($"{x.Kind}: {(x.Up ? "connected" : "not connected")}{(x.Up && x.LinkSpeedBps > 0 ? $", link speed {AppInfo.Fmt(x.LinkSpeedBps / 8.0)}/s (negotiated, not measured)" : "")}" +
                          (x.Kind == "Bluetooth" ? " (detected only, MoveIt does not send files over Bluetooth)" : ""));
        if (!links.Any(x => x.Kind == "Wired (USB network)")) sb.AppendLine("Wired USB: no USB network adapter found. An ordinary USB charging cable between two laptops does not create one.");
        return sb.ToString().TrimEnd();
    }
}

public enum LinkQuality { Excellent, Good, Unstable, Poor, Disconnected, NotAvailable }

public static class Diagnostics
{
    // Based only on values that were measured. Thresholds are simple rules of thumb, not a standard.
    public static LinkQuality Classify(SpeedResult? r)
    {
        if (r == null) return LinkQuality.NotAvailable;
        if (r.LatencyMs < 0) return LinkQuality.Disconnected;
        if (r.LostPings > 0 || r.JitterMs > 50) return LinkQuality.Unstable;
        double mbs = Math.Min(r.UploadBps, r.DownloadBps) / (1024 * 1024);
        if (r.LatencyMs < 20 && mbs >= 10) return LinkQuality.Excellent;
        if (r.LatencyMs < 60 && mbs >= 3) return LinkQuality.Good;
        return LinkQuality.Poor;
    }

    public static string Tips(LinkQuality q) => q switch
    {
        LinkQuality.Disconnected => "Check that both laptops are on the same WiFi, the other laptop is on the Receive screen, and Windows Firewall allows MoveIt (Private network).",
        LinkQuality.Unstable => "Move closer to the router, close other heavy downloads, or use a cable (Ethernet or a USB network cable).",
        LinkQuality.Poor => "The link is slow. Use 5 GHz WiFi or a cable. Large transfers will take a long time.",
        _ => ""
    };

    public static string Estimate(long bytes, double bps) => bps < 1 ? "Not available" : "about " + AppInfo.Eta(bytes, bps) + " (estimate from the measured speed)";
}
