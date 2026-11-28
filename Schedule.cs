using System.Text.Json;

namespace MoveIt;

public sealed class ScheduledJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime When { get; set; }
    public string DeviceName { get; set; } = "";
    public List<string> Paths { get; set; } = new();   // files and folders chosen
    public string Status { get; set; } = "Scheduled";  // Scheduled, Due, Done, Cancelled
}

// A schedule only fires while MoveIt is running. It never wakes a sleeping PC. The pairing code changes for every Receive session,
// so a due job asks the user to start it (and type the code); it never connects by itself.
public static class ScheduleStore
{
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "schedule.json");
    static readonly object gate = new();

    public static List<ScheduledJob> Load()
    { lock (gate) { try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<ScheduledJob>>(File.ReadAllText(FilePath)) ?? new(); } catch { } return new(); } }

    public static void Save(List<ScheduledJob> l)
    {
        lock (gate)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); var tmp = FilePath + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(l)); File.Move(tmp, FilePath, true); } catch { }
        }
    }

    public static ScheduledJob Add(DateTime when, string device, IEnumerable<string> paths)
    { var l = Load(); var j = new ScheduledJob { When = when, DeviceName = device, Paths = paths.ToList() }; l.Add(j); Save(l); return j; }
    public static void Cancel(Guid id) { var l = Load(); var j = l.FirstOrDefault(x => x.Id == id); if (j != null) { j.Status = "Cancelled"; Save(l); } }
    public static void Reschedule(Guid id, DateTime when) { var l = Load(); var j = l.FirstOrDefault(x => x.Id == id); if (j != null) { j.When = when; j.Status = "Scheduled"; Save(l); } }
    public static List<ScheduledJob> Due(DateTime now) => Load().Where(j => j.Status == "Scheduled" && j.When <= now).ToList();

    // Checks before a job may start. Returns a list of problems (empty = ready).
    public static List<string> Preflight(ScheduledJob j, PeerInfo? peer)
    {
        var p = new List<string>();
        foreach (var x in j.Paths) if (!File.Exists(x) && !Directory.Exists(x)) p.Add("No longer exists: " + x);
        if (peer == null) { p.Add("The other laptop is not reachable (it must be on the Receive screen)."); return p; }
        if (peer.Busy) p.Add("The other laptop is busy with another transfer.");
        long need = 0; foreach (var x in j.Paths)
        {
            try
            {
                if (File.Exists(x)) { using var s = File.OpenRead(x); need += s.Length; }
                else if (Directory.Exists(x)) need += new DirectoryInfo(x).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length);
            }
            catch { p.Add("Cannot read: " + x); }
        }
        if (peer.FreeBytes >= 0 && need > peer.FreeBytes) p.Add("Not enough free space on the other laptop.");
        return p;
    }
}
