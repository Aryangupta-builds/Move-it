using System.Security.Cryptography;
using System.Text.Json;

namespace MoveIt;

public record BackupEntry(string OriginalPath, string StoredName, long Size, string Sha256);
public sealed class BackupManifest
{
    public string Id { get; set; } = "";
    public DateTime Created { get; set; }
    public string Note { get; set; } = "Only the files listed here were backed up. This is not a full system backup.";
    public List<BackupEntry> Entries { get; set; } = new();
}
public record RestoreReport(int Restored, List<string> Failed);

// Copies chosen destination files to a backup folder with a manifest, verifies the copies, and can put them back.
// It never touches the source laptop and never restores anything that is not in the manifest.
public static class BackupService
{
    public static long RequiredBytes(IEnumerable<string> paths) { long n = 0; foreach (var p in paths) { try { n += new FileInfo(p).Length; } catch { } } return n; }

    static string Sha(string p) { using var f = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(f)); }

    public static BackupManifest Create(string backupRoot, IEnumerable<string> existingFiles, CancellationToken ct)
    {
        var files = existingFiles.Where(File.Exists).ToList();
        long need = RequiredBytes(files), free = Net.FreeSpace(backupRoot);
        if (free >= 0 && need > free) throw new IOException($"Not enough space for the backup: needs {AppInfo.Fmt(need)}, {AppInfo.Fmt(free)} free.");
        var m = new BackupManifest { Id = DateTime.Now.ToString("yyyyMMdd_HHmmss"), Created = DateTime.Now };
        var dir = Path.Combine(backupRoot, "MoveIt_Backup_" + m.Id); var store = Path.Combine(dir, "files");
        Directory.CreateDirectory(store); int n = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var name = $"{++n:D6}_{Net.CleanSegment(Path.GetFileName(f))}"; var to = Path.Combine(store, name);
            File.Copy(f, to, false);
            var h = Sha(to);
            if (h != Sha(f)) { File.Delete(to); throw new IOException("Backup copy did not match the original: " + Path.GetFileName(f)); }
            m.Entries.Add(new BackupEntry(f, name, new FileInfo(to).Length, h));
        }
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
        return m;
    }

    public static BackupManifest? Load(string backupDir)
    {
        try { return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(backupDir, "manifest.json"))); } catch { return null; }
    }

    public static List<string> Verify(string backupDir)
    {
        var bad = new List<string>(); var m = Load(backupDir);
        if (m == null) { bad.Add("manifest.json missing or unreadable"); return bad; }
        foreach (var e in m.Entries)
        {
            var p = Path.Combine(backupDir, "files", e.StoredName);
            try { if (!File.Exists(p) || Sha(p) != e.Sha256) bad.Add(e.OriginalPath); } catch { bad.Add(e.OriginalPath); }
        }
        return bad;
    }

    // Replaces the files at their original paths with the backed-up versions. The caller must have asked the user first.
    public static RestoreReport Restore(string backupDir, CancellationToken ct)
    {
        var failed = new List<string>(); int ok = 0; var m = Load(backupDir);
        if (m == null) return new(0, new() { "manifest.json missing or unreadable" });
        foreach (var e in m.Entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var src = Path.Combine(backupDir, "files", e.StoredName);
                if (!File.Exists(src) || Sha(src) != e.Sha256) { failed.Add(e.OriginalPath + " (backup copy damaged)"); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(e.OriginalPath)!);
                var tmp = e.OriginalPath + ".restore";
                File.Copy(src, tmp, true);
                if (Sha(tmp) != e.Sha256) { File.Delete(tmp); failed.Add(e.OriginalPath + " (verification failed)"); continue; }
                File.Move(tmp, e.OriginalPath, true); ok++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { failed.Add(e.OriginalPath + " (" + ex.Message + ")"); }
        }
        return new(ok, failed);
    }
}
