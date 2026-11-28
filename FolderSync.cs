using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MoveIt;

// Folder Sync ENGINE for two folders that this PC can reach (for example a shared or mapped folder).
// It is opt-in, has no background loop and no network transport yet: the planner only reads, and Apply only runs after the caller has the user's approval.
// Deletions are never executed unless listed in `approvedDeletes`, and even then the file is moved to ".moveit-sync-trash", not erased.
public record FileSig(long Size, long MTimeTicks);
public sealed class SyncState { public Dictionary<string, FileSig> Files { get; set; } = new(); public DateTime LastSync { get; set; } }
public enum ChangeKind { New, Modified, Renamed, Deletion, Conflict }
public record SyncChange(ChangeKind Kind, string Rel, bool AToB, string? From = null);

public sealed class SyncPlan
{
    public List<SyncChange> Copies = new();      // New / Modified / Renamed, safe to apply
    public List<SyncChange> Deletions = new();   // review queue: Rel was deleted on one side; the other side still has it
    public List<SyncChange> Conflicts = new();   // changed on both sides, or differing with no history: never overwritten automatically
    public int Unchanged;
    public Dictionary<string, FileSig> Same = new();   // identical on both sides: remembered so later deletions are detected
    public long CopyBytes;
    public int Pending => Copies.Count + Deletions.Count + Conflicts.Count;
}

public static class FolderSync
{
    const string Trash = ".moveit-sync-trash";
    static readonly TimeSpan Tol = TimeSpan.FromSeconds(2);   // FAT/exFAT store time with 2 s precision

    static Dictionary<string, FileSig> Snapshot(string root)
    {
        var d = new Dictionary<string, FileSig>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return d;
        foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rel.StartsWith(Trash, StringComparison.OrdinalIgnoreCase) || Sensitive.IsBlockedFile(f) || rel.Split('/').Any(s => Sensitive.IsBlockedDir(s))) continue;
            try { var fi = new FileInfo(f); d[rel] = new FileSig(fi.Length, fi.LastWriteTimeUtc.Ticks); } catch { }
        }
        return d;
    }

    static bool SameSig(FileSig x, FileSig y) => x.Size == y.Size && Math.Abs(x.MTimeTicks - y.MTimeTicks) <= Tol.Ticks;
    static bool SameContent(string pa, string pb) { var a = Duplicates.Hash(pa); return a != null && a == Duplicates.Hash(pb); }

    public static SyncPlan Plan(string a, string b, SyncState? last, bool twoWay)
    {
        var A = Snapshot(a); var B = Snapshot(b); var L = last?.Files; var plan = new SyncPlan();
        foreach (var rel in A.Keys.Union(B.Keys, StringComparer.OrdinalIgnoreCase).ToList())
        {
            bool inA = A.TryGetValue(rel, out var sa), inB = B.TryGetValue(rel, out var sb), had = L != null && L.TryGetValue(rel, out var _);
            FileSig? sl = had ? L![rel] : null;
            if (inA && inB)
            {
                if (SameSig(sa!, sb!)) { plan.Unchanged++; plan.Same[rel] = sa!; continue; }
                var pa = Path.Combine(a, rel); var pb = Path.Combine(b, rel);
                if (sa!.Size == sb!.Size && SameContent(pa, pb)) { plan.Unchanged++; plan.Same[rel] = sa; continue; }   // only the timestamp differs
                if (sl == null) { plan.Conflicts.Add(new(ChangeKind.Conflict, rel, true)); continue; }   // no history: cannot tell which is newer
                bool ca = !SameSig(sa, sl), cb = !SameSig(sb, sl);
                if (ca && cb) plan.Conflicts.Add(new(ChangeKind.Conflict, rel, true));
                else if (ca) { plan.Copies.Add(new(ChangeKind.Modified, rel, true)); plan.CopyBytes += sa.Size; }
                else if (cb && twoWay) { plan.Copies.Add(new(ChangeKind.Modified, rel, false)); plan.CopyBytes += sb.Size; }
                else plan.Unchanged++;   // one-way: edits made in the destination are left alone
            }
            else if (inA)
            {
                if (had && twoWay) plan.Deletions.Add(new(ChangeKind.Deletion, rel, true));       // B deleted it: review queue, A's copy is kept
                else { plan.Copies.Add(new(ChangeKind.New, rel, true)); plan.CopyBytes += sa!.Size; }
            }
            else   // only in B
            {
                if (had) plan.Deletions.Add(new(ChangeKind.Deletion, rel, true));                  // A deleted it: B's copy stays until the user approves
                else if (twoWay) { plan.Copies.Add(new(ChangeKind.New, rel, false)); plan.CopyBytes += sb!.Size; }
                // one-way: extra files in the destination are left alone
            }
        }
        // Rename detection: a deletion and a new file on the same side with identical size and timestamp.
        foreach (var del in plan.Deletions.ToList())
        {
            if (L == null || !L.TryGetValue(del.Rel, out var old)) continue;
            var match = plan.Copies.FirstOrDefault(c => c.Kind == ChangeKind.New && c.AToB && A.TryGetValue(c.Rel, out var s2) && SameSig(s2, old) && !B.ContainsKey(c.Rel) && B.ContainsKey(del.Rel));
            if (match == null) continue;
            plan.Deletions.Remove(del); plan.Copies.Remove(match);
            plan.Copies.Add(new(ChangeKind.Renamed, match.Rel, true, del.Rel));
        }
        return plan;
    }

    public static (SyncState state, List<string> errors) Apply(string a, string b, SyncPlan plan, SyncState? last, HashSet<string>? approvedDeletes, CancellationToken ct)
    {
        var st = new SyncState { Files = last?.Files != null ? new(last.Files, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase), LastSync = DateTime.Now };
        foreach (var x in plan.Same) st.Files[x.Key] = x.Value;
        var errors = new List<string>(); string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string Full(string root, string rel) => Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        bool Inside(string root, string p) => p.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        foreach (var c in plan.Copies)
        {
            ct.ThrowIfCancellationRequested();
            string from = c.AToB ? a : b, to = c.AToB ? b : a;
            try
            {
                var dst = Full(to, c.Rel);
                if (!Inside(to, dst)) throw new InvalidOperationException("Path outside the folder");
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (c.Kind == ChangeKind.Renamed && c.From != null)
                {
                    var old = Full(to, c.From);
                    if (File.Exists(old) && !File.Exists(dst) && Inside(to, old)) File.Move(old, dst);
                    else { File.Copy(Full(from, c.Rel), dst + ".tmp", true); File.Move(dst + ".tmp", dst, true); }
                    st.Files.Remove(c.From);
                }
                else { var src = Full(from, c.Rel); var tmp = dst + ".moveit-tmp"; File.Copy(src, tmp, true); File.Move(tmp, dst, true); }
                var fi = new FileInfo(dst); File.SetLastWriteTimeUtc(dst, new FileInfo(Full(from, c.Rel)).LastWriteTimeUtc);
                st.Files[c.Rel] = new FileSig(fi.Length, new FileInfo(dst).LastWriteTimeUtc.Ticks);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"{c.Rel}: {ex.Message}"); }
        }
        foreach (var d in plan.Deletions)
        {
            if (approvedDeletes == null || !approvedDeletes.Contains(d.Rel)) continue;   // never without the user's approval
            try
            {
                foreach (var root in new[] { a, b })
                {
                    var p = Full(root, d.Rel);
                    if (!File.Exists(p) || !Inside(root, p)) continue;
                    var trash = Path.Combine(root, Trash, stamp, d.Rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(trash)!); File.Move(p, trash, true);
                }
                st.Files.Remove(d.Rel);
            }
            catch (Exception ex) { errors.Add($"{d.Rel}: {ex.Message}"); }
        }
        return (st, errors);
    }

    // Conflict choices. KeepBoth leaves both versions: the destination's copy is saved next to it as "name (conflict copy).ext".
    public static void Resolve(string a, string b, SyncChange conflict, string choice /* source | destination | both */)
    {
        var pa = Path.Combine(a, conflict.Rel.Replace('/', '\\')); var pb = Path.Combine(b, conflict.Rel.Replace('/', '\\'));
        if (choice == "source") File.Copy(pa, pb, true);
        else if (choice == "destination") File.Copy(pb, pa, true);
        else if (choice == "both")
        {
            var alt = Path.Combine(Path.GetDirectoryName(pa)!, Path.GetFileNameWithoutExtension(pa) + " (conflict copy)" + Path.GetExtension(pa));
            File.Copy(pb, Net.UniquePath(alt), false);
        }
    }

    // ---- Generic versions (spec 20): same rules, but each side can be a local folder or the other laptop over MoveIt. ----
    public static async Task<SyncPlan> PlanAsync(ISyncSide a, ISyncSide b, SyncState? last, bool twoWay, CancellationToken ct)
    {
        var A = await a.Snapshot(ct); var B = await b.Snapshot(ct); var L = last?.Files; var plan = new SyncPlan();
        foreach (var rel in A.Keys.Union(B.Keys, StringComparer.OrdinalIgnoreCase).ToList())
        {
            ct.ThrowIfCancellationRequested();
            bool inA = A.TryGetValue(rel, out var sa), inB = B.TryGetValue(rel, out var sb), had = L != null && L.ContainsKey(rel);
            FileSig? sl = had ? L![rel] : null;
            if (inA && inB)
            {
                if (SameSig(sa!, sb!)) { plan.Unchanged++; plan.Same[rel] = sa!; continue; }
                if (sa!.Size == sb!.Size) { var ha = await a.Hash(rel, ct); if (ha != null && ha == await b.Hash(rel, ct)) { plan.Unchanged++; plan.Same[rel] = sa; continue; } }
                if (sl == null) { plan.Conflicts.Add(new(ChangeKind.Conflict, rel, true)); continue; }
                bool ca = !SameSig(sa, sl), cb = !SameSig(sb, sl);
                if (ca && cb) plan.Conflicts.Add(new(ChangeKind.Conflict, rel, true));
                else if (ca) { plan.Copies.Add(new(ChangeKind.Modified, rel, true)); plan.CopyBytes += sa.Size; }
                else if (cb && twoWay) { plan.Copies.Add(new(ChangeKind.Modified, rel, false)); plan.CopyBytes += sb.Size; }
                else plan.Unchanged++;
            }
            else if (inA)
            {
                if (had && twoWay) plan.Deletions.Add(new(ChangeKind.Deletion, rel, true));
                else { plan.Copies.Add(new(ChangeKind.New, rel, true)); plan.CopyBytes += sa!.Size; }
            }
            else
            {
                if (had) plan.Deletions.Add(new(ChangeKind.Deletion, rel, true));
                else if (twoWay) { plan.Copies.Add(new(ChangeKind.New, rel, false)); plan.CopyBytes += sb!.Size; }
            }
        }
        foreach (var del in plan.Deletions.ToList())
        {
            if (L == null || !L.TryGetValue(del.Rel, out var old)) continue;
            var match = plan.Copies.FirstOrDefault(c => c.Kind == ChangeKind.New && c.AToB && A.TryGetValue(c.Rel, out var s2) && SameSig(s2, old) && !B.ContainsKey(c.Rel) && B.ContainsKey(del.Rel));
            if (match == null) continue;
            plan.Deletions.Remove(del); plan.Copies.Remove(match);
            plan.Copies.Add(new(ChangeKind.Renamed, match.Rel, true, del.Rel));
        }
        return plan;
    }

    static async Task CopyOne(ISyncSide from, ISyncSide to, string rel, Action<long>? onBytes, CancellationToken ct)
    {
        using var r = await from.OpenRead(rel, ct);
        await to.Write(rel, r.Stream, r.Size, r.MTimeTicks, r.Sha, onBytes, ct);
    }

    // keepOldVersions: before a file is replaced, its old version is copied into .moveit-sync-trash (used by automatic sync). Deletions only for approvedDeletes (per file).
    public static async Task<(SyncState state, List<string> errors, int copied)> ApplyAsync(ISyncSide a, ISyncSide b, SyncPlan plan, SyncState? last, HashSet<string>? approvedDeletes, bool keepOldVersions, Action<string, long>? progress, CancellationToken ct)
    {
        var st = new SyncState { Files = last?.Files != null ? new(last.Files, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase), LastSync = DateTime.Now };
        foreach (var x in plan.Same) st.Files[x.Key] = x.Value;
        var errors = new List<string>(); string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss"); int copied = 0;
        foreach (var c in plan.Copies)
        {
            ct.ThrowIfCancellationRequested();
            ISyncSide from = c.AToB ? a : b, to = c.AToB ? b : a;
            try
            {
                progress?.Invoke(c.Rel, 0);
                bool done = false;
                if (c.Kind == ChangeKind.Renamed && c.From != null) { done = await to.Rename(c.From, c.Rel, ct); if (done) st.Files.Remove(c.From); }
                if (!done)
                {
                    if (keepOldVersions && c.Kind == ChangeKind.Modified) await to.Archive(c.Rel, stamp, ct);
                    await CopyOne(from, to, c.Rel, n => progress?.Invoke(c.Rel, n), ct);
                    if (c.Kind == ChangeKind.Renamed && c.From != null) st.Files.Remove(c.From);
                }
                var sig = await to.Stat(c.Rel, ct); if (sig != null) st.Files[c.Rel] = sig;
                copied++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"{c.Rel}: {ex.Message}"); if (!a.Reachable() || !b.Reachable()) { errors.Add("Connection lost. Run Sync again when the other laptop is back; finished files are kept."); break; } }
        }
        foreach (var d in plan.Deletions)
        {
            if (approvedDeletes == null || !approvedDeletes.Contains(d.Rel)) continue;
            try { await a.Trash(d.Rel, stamp, ct); await b.Trash(d.Rel, stamp, ct); st.Files.Remove(d.Rel); }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"{d.Rel}: {ex.Message}"); }
        }
        return (st, errors, copied);
    }

    // choice: source | destination | both. "both" keeps the destination's version next to the file as "name (conflict copy ...).ext", then copies the source version in.
    // Returns the new signature to remember, or null if nothing changed.
    public static async Task<FileSig?> ResolveAsync(ISyncSide a, ISyncSide b, SyncChange c, string choice, CancellationToken ct)
    {
        if (choice == "source") await CopyOne(a, b, c.Rel, null, ct);
        else if (choice == "destination") await CopyOne(b, a, c.Rel, null, ct);
        else if (choice == "both")
        {
            var dir = Path.GetDirectoryName(c.Rel.Replace('/', '\\'))?.Replace('\\', '/'); var name = Path.GetFileNameWithoutExtension(c.Rel); var ext = Path.GetExtension(c.Rel);
            var alt = (string.IsNullOrEmpty(dir) ? "" : dir + "/") + $"{name} (conflict copy {DateTime.Now:yyyyMMdd-HHmmss}){ext}";
            if (!await b.Rename(c.Rel, alt, ct)) throw new IOException("Could not keep both versions");
            await CopyOne(a, b, c.Rel, null, ct);
        }
        else return null;
        return await a.Stat(c.Rel, ct);
    }

    static string StatePath(string a, string b)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(a).ToLowerInvariant() + "|" + Path.GetFullPath(b).ToLowerInvariant())))[..16];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "sync", id + ".json");
    }
    public static SyncState? LoadState(string a, string b) { try { var p = StatePath(a, b); return File.Exists(p) ? JsonSerializer.Deserialize<SyncState>(File.ReadAllText(p)) : null; } catch { return null; } }
    public static void SaveState(string a, string b, SyncState s)
    { try { var p = StatePath(a, b); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(s)); File.Move(p + ".tmp", p, true); } catch { } }
}
