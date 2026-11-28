using System.Diagnostics;
using System.Net.NetworkInformation;

namespace MoveIt;

public enum WiredState { NoWiredLink, DeviceDetected, DriverRequired, InterfaceFound, Connected, Failed }

public record WiredInfo(WiredState State, string Text);

// Spec 12: wired status from what Windows really reports. "Connected" is only returned after a real probe of the other laptop succeeded.
// Windows cannot tell us whether a plain cable is plugged in, so "no adapter" is reported as "no supported wired link", never as a guess about the cable.
public static class WiredStatus
{
    static readonly string[] Words = { "USB", "RNDIS", "Remote NDIS", "Thunderbolt" };

    public static WiredInfo Evaluate(bool peerProbeOk, string? peerIp)
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                            && Words.Any(w => ((n.Description ?? "") + " " + n.Name).Contains(w, StringComparison.OrdinalIgnoreCase))
                            && !((n.Description ?? "").Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var up = adapters.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                && n.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
            if (up != null)
            {
                bool onThis = peerIp != null && up.GetIPProperties().UnicastAddresses.Any(u => Same24(u.Address.ToString(), peerIp));
                if (peerProbeOk && onThis) return new(WiredState.Connected, $"Connected over {up.Name}. Negotiated link speed {AppInfo.Fmt(up.Speed / 8.0)}/s is not a measured speed.");
                return new(WiredState.InterfaceFound, $"Compatible Transfer Interface Found ({up.Name}). Not connected to the other laptop yet.");
            }
            if (adapters.Count > 0)
                return new(WiredState.DeviceDetected, $"USB Device Detected ({adapters[0].Name}), but it has no network link yet. Check the other laptop and the cable software.");
            if (ProblemUsbDevice()) return new(WiredState.DriverRequired, "Driver Required: Windows reports a USB device with a problem. MoveIt never installs drivers automatically. Install the driver from the cable maker.");
            return new(WiredState.NoWiredLink, "No supported wired link. A plain USB charging cable between two laptops creates no connection. A USB bridge/network cable or USB Ethernet adapter is needed.");
        }
        catch (Exception ex) { return new(WiredState.Failed, "Connection Failed: " + ex.Message); }
    }

    // Reads Windows' own list of devices with problems (pnputil, Windows 10 2004+). Unavailable => false, so we never invent "driver required".
    static bool ProblemUsbDevice()
    {
        try
        {
            var psi = new ProcessStartInfo("pnputil", "/enum-devices /problem") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi); if (p == null) return false;
            var o = p.StandardOutput.ReadToEnd(); if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return false; }
            return o.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(b => b.Contains("USB", StringComparison.OrdinalIgnoreCase) && (b.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) || b.Contains("Network", StringComparison.OrdinalIgnoreCase) || b.Contains("Bridge", StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }

    static bool Same24(string a, string b)
    {
        var x = a.Split('.'); var y = b.Split('.');
        return x.Length == 4 && y.Length == 4 && x[0] == y[0] && x[1] == y[1] && x[2] == y[2];
    }

    // During a transfer over a wired adapter: true when that adapter is no longer up (cable pulled). Used for an inline warning only; never cancels.
    public static bool WiredLost(string? localIp)
    {
        if (string.IsNullOrEmpty(localIp)) return false;
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.ToString() == localIp));
            if (ni == null) return true;
            return ni.OperationalStatus != OperationalStatus.Up;
        }
        catch { return false; }
    }
}
