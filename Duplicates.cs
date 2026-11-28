using System.Security.Cryptography;

namespace MoveIt;

public enum DupAction { SkipDuplicate, ReplaceExisting, KeepBoth, ReviewManually }
public sealed class DupGroup
{
    public long Size; public string Hash = "";
    public List<string> Paths = new();
    public long Wasted => Size * (Paths.Count - 1);
}

public static class Duplicates
{
    // Same size first (cheap), then SHA-256 of the content. Same name alone never counts as "identical".
    public static List<DupGroup> Find(IEnumerable<string> paths, CancellationToken ct)
    {
        var bySize = new Dictionary<long, List<string>>();
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            try { var len = new FileInfo(p).Length; if (len > 0) { if (!bySize.TryGetValue(len, out var l)) bySize[len] = l = new(); l.Add(p); } } catch { }
        }
        var groups = new List<DupGroup>();
        foreach (var (size, list) in bySize)
        {
            if (list.Count < 2) continue;
            // Cheap pre-check: first and last 64 KB. Files that differ there are never read in full (saves a lot of disk reading on big same-size files).
            var byQuick = new Dictionary<string, List<string>>();
            foreach (var p in list)
            {
                ct.ThrowIfCancellationRequested();
                var qk = Quick(p, size); if (qk == null) continue;
                if (!byQuick.TryGetValue(qk, out var ql)) byQuick[qk] = ql = new(); ql.Add(p);
            }
            var byHash = new Dictionary<string, List<string>>();
            var cand = byQuick.Values.Where(v => v.Count > 1).SelectMany(v => v).ToList();
            var hashes = new string?[cand.Count];
            Parallel.For(0, cand.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)), CancellationToken = ct }, i => hashes[i] = Hash(cand[i]));   // max 4 at once: low-end laptops stay responsive
            for (int i = 0; i < cand.Count; i++)
            {
                var h = hashes[i]; if (h == null) continue;
                if (!byHash.TryGetValue(h, out var l)) byHash[h] = l = new(); l.Add(cand[i]);
            }
            foreach (var (h, l) in byHash) if (l.Count > 1) groups.Add(new DupGroup { Size = size, Hash = h, Paths = l });
        }
        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    static string? Quick(string path, long size)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            var buf = new byte[(int)Math.Min(65536, size)]; f.ReadExactly(buf);
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); h.AppendData(buf);
            if (size > buf.Length) { f.Seek(-buf.Length, SeekOrigin.End); f.ReadExactly(buf); h.AppendData(buf); }
            return Convert.ToHexString(h.GetHashAndReset());
        }
        catch { return null; }
    }

    public static string? Hash(string path)
    {
        try { using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan); return Convert.ToHexString(SHA256.HashData(f)); }
        catch { return null; }
    }

    // Keeps the first file of every group (the one listed first) and returns the others, which can be left out of a transfer.
    public static HashSet<string> Extras(IEnumerable<DupGroup> groups)
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups) foreach (var p in g.Paths.Skip(1)) s.Add(p);
        return s;
    }
}
