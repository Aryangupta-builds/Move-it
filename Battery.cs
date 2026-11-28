using System.Runtime.InteropServices;

namespace MoveIt;

// Percent is -1 when Windows does not report it. HasBattery=false means a desktop PC (or no battery detected).
public record BatteryInfo(int Percent, bool Charging, bool HasBattery);

public static class Battery
{
    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    public static BatteryInfo Read()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return new(-1, false, true);   // unknown: treated as "status unavailable", not as a number
            bool charging = s.ACLineStatus == 1;
            if ((s.BatteryFlag & 128) != 0) return new(-1, charging, false);       // flag 128 = no system battery
            return new(s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent, charging, true);
        }
        catch { return new(-1, false, true); }
    }

    public static string Describe(BatteryInfo b) =>
        !b.HasBattery ? "No battery (mains powered)" :
        b.Percent < 0 ? "Battery level not available" :
        $"{b.Percent}%{(b.Charging ? " (charging)" : "")}";
}

public enum GateResult { Ok, Blocked, NeedConfirm }
public record GateDecision(GateResult Result, string Message);

// Complete migration must not start when either laptop is below 20 %. Unknown battery on a laptop is never guessed: the user has to confirm.
public static class MigrationGate
{
    public const int Minimum = 20;

    public static GateDecision Check(BatteryInfo local, PeerInfo? peer, string peerName)
    {
        var low = new List<string>(); var unknown = new List<string>();
        void One(string who, int pct, bool has)
        {
            if (!has) return;                      // mains powered: nothing to protect
            if (pct < 0) unknown.Add(who);
            else if (pct < Minimum) low.Add($"{who} is at {pct}%");
        }
        One("This laptop", local.Percent, local.HasBattery);
        if (peer != null) One(peerName, peer.Battery, peer.HasBattery);
        if (low.Count > 0)
            return new(GateResult.Blocked, string.Join(" and ", low) + $". A complete migration needs at least {Minimum}% on both laptops. Connect the charger, wait a moment and try again.");
        if (unknown.Count > 0)
            return new(GateResult.NeedConfirm, "Battery level of " + string.Join(" and ", unknown) + " could not be read. Make sure both laptops are charged or plugged in before you continue.");
        return new(GateResult.Ok, "");
    }
}
