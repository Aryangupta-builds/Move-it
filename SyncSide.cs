using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace MoveIt;

// Spec 20: a "side" of a Folder Sync pair. It is either a folder this PC can reach (LocalSide) or a folder on the other laptop reached
// over the MoveIt connection (RemoteSide, same pairing code and encryption as a normal transfer). The planner and the applier only
// talk to this interface, so the safety rules (no silent deletes, no overwrite without a plan) are the same for both.
public sealed class SyncRead : IDisposable
{
    public Stream Stream = Stream.Null; public long Size; public long MTimeTicks;
    public Func<Task<string?>> Sha = () => Task.FromResult<string?>(null);   // SHA-256 of the bytes the source sent; valid only after the stream was read to the end
    public Action? Release;
    public void Dispose() { try { Stream.Dispose(); } catch { } var r = Release; Release = null; r?.Invoke(); }
}

public interface ISyncSide : IDisposable
{
    string Label { get; }
    bool Reachable();
    long FreeSpace();   // -1 = unknown
    Task<Dictionary<string, FileSig>> Snapshot(CancellationToken ct);
    Task<string?> Hash(string rel, CancellationToken ct);
    Task<FileSig?> Stat(string rel, CancellationToken ct);
    Task<SyncRead> OpenRead(string rel, CancellationToken ct);
    // Writes to a temporary name, verifies against the source checksum, then replaces the target. Nothing is left half-written.
    Task Write(string rel, Stream src, long size, long mtimeTicks, Func<Task<string?>> sourceSha, Action<long>? onBytes, CancellationToken ct);
    Task<bool> Rename(string from, string to, CancellationToken ct);   // false = not possible (missing source or the target exists)
    Task Archive(string rel, string stamp, CancellationToken ct);      // copy the current file into .moveit-sync-trash (keeps the old version before it is replaced)
    Task Trash(string rel, string stamp, CancellationToken ct);        // move into .moveit-sync-trash, never erase
}

// Hashes everything that is read through it.
public sealed class HashingStream : Stream
{
    readonly Stream inner; readonly IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); string? final;
    public HashingStream(Stream s) { inner = s; }
    public string Result() => final ??= Convert.ToHexString(h.GetHashAndReset());
    public override int Read(byte[] b, int o, int c) { int r = inner.Read(b, o, c); if (r > 0) h.AppendData(b, o, r); return r; }
    public override async ValueTask<int> ReadAsync(Memory<byte> m, CancellationToken ct = default) { int r = await inner.ReadAsync(m, ct); if (r > 0) h.AppendData(m.Span[..r]); return r; }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    protected override void Dispose(bool d) { if (d) { inner.Dispose(); h.Dispose(); } base.Dispose(d); }
}

// Reads exactly `left` bytes of a connection and then reports end-of-stream; the connection itself stays open for the next message.
sealed class LimitedReadStream : Stream
{
    readonly Stream inner; long left;
    public LimitedReadStream(Stream s, long n) { inner = s; left = n; }
    public override async ValueTask<int> ReadAsync(Memory<byte> m, CancellationToken ct = default)
    {
        if (left <= 0) return 0;
        int r = await inner.ReadAsync(m[..(int)Math.Min(m.Length, left)], ct);
        if (r == 0) throw new IOException("Connection lost"); left -= r; return r;
    }
    public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    protected override void Dispose(bool d) { base.Dispose(d); }   // never closes the connection
}

// What a folder looks like for sync. Shared by the local side and by the server that answers the other laptop, so both apply the same filters.
public static class SyncScan
{
    public const string Trash = ".moveit-sync-trash";

    public static Dictionary<string, FileSig> Snapshot(string root, CancellationToken ct)
    {
        var d = new Dictionary<string, FileSig>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return d;
        foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rel.StartsWith(Trash, StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".moveit-tmp", StringComparison.OrdinalIgnoreCase) || rel.Contains(".moveit-part-", StringComparison.OrdinalIgnoreCase) || Sensitive.IsBlockedFile(f) || rel.Split('/').Any(x => Sensitive.IsBlockedDir(x))) continue;
            try { var fi = new FileInfo(f); d[rel] = new FileSig(fi.Length, fi.LastWriteTimeUtc.Ticks); } catch { }
        }
        return d;
    }

    // Maps a relative path from the other side to a full path inside root. Anything that could leave the folder or hit a sensitive file is refused.
    public static string Resolve(string root, string rel, bool allowTrash = false)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.Contains(':') || rel.StartsWith('/') || rel.StartsWith('\\')) throw new InvalidOperationException("Bad path");
        var parts = rel.Replace('\\', '/').Split('/');
        foreach (var p in parts)
            if (p.Length == 0 || p == "." || p == ".." || Net.CleanSegment(p) != p || (!allowTrash && p.Equals(Trash, StringComparison.OrdinalIgnoreCase)) || Sensitive.IsBlockedDir(p)) throw new InvalidOperationException("Bad path");
        if (Sensitive.IsBlockedFile(rel)) throw new InvalidOperationException("Not allowed: sensitive file");
        var b = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(b, string.Join('\\', parts)));
        if (!full.StartsWith(b, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Bad path");
        return full;
    }
}

public sealed class LocalSide : ISyncSide
{
    readonly string root; public LocalSide(string root) { this.root = root; }
    public string Label => root;
    public bool Reachable() => Directory.Exists(root);
    public long FreeSpace() => Net.FreeSpace(root);
    public Task<Dictionary<string, FileSig>> Snapshot(CancellationToken ct) => Task.Run(() => SyncScan.Snapshot(root, ct), ct);
    string P(string rel) => SyncScan.Resolve(root, rel);

    public Task<string?> Hash(string rel, CancellationToken ct) => Task.Run(() => Duplicates.Hash(P(rel)), ct);
    public Task<FileSig?> Stat(string rel, CancellationToken ct)
    { try { var fi = new FileInfo(P(rel)); return Task.FromResult(fi.Exists ? new FileSig(fi.Length, fi.LastWriteTimeUtc.Ticks) : null); } catch { return Task.FromResult<FileSig?>(null); } }

    public Task<SyncRead> OpenRead(string rel, CancellationToken ct)
    {
        var fi = new FileInfo(P(rel));
        var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var hs = new HashingStream(fs);
        return Task.FromResult(new SyncRead { Stream = hs, Size = fs.Length, MTimeTicks = fi.LastWriteTimeUtc.Ticks, Sha = () => Task.FromResult<string?>(hs.Result()) });
    }

    public async Task Write(string rel, Stream src, long size, long mtimeTicks, Func<Task<string?>> sourceSha, Action<long>? onBytes, CancellationToken ct)
    {
        var dst = P(rel); var tmp = dst + ".moveit-tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        try
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long got = 0;
            await using (var o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
                try
                {
                    int r;
                    while ((r = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0) { await o.WriteAsync(buf.AsMemory(0, r), ct); h.AppendData(buf, 0, r); got += r; onBytes?.Invoke(r); }
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
            }
            if (got != size) throw new IOException("The file changed while it was being copied. It will be tried again at the next sync.");
            var expected = await sourceSha();
            if (expected != null && !string.Equals(expected, Convert.ToHexString(h.GetHashAndReset()), StringComparison.OrdinalIgnoreCase)) throw new IOException("Checksum mismatch: the copy was discarded.");
            File.SetLastWriteTimeUtc(tmp, new DateTime(mtimeTicks, DateTimeKind.Utc));
            File.Move(tmp, dst, true);
        }
        catch { Net.TryDelete(tmp); throw; }
    }

    public Task<bool> Rename(string from, string to, CancellationToken ct)
    {
        try
        {
            var a = P(from); var b = P(to);
            if (!File.Exists(a) || File.Exists(b)) return Task.FromResult(false);
            Directory.CreateDirectory(Path.GetDirectoryName(b)!); File.Move(a, b); return Task.FromResult(true);
        }
        catch { return Task.FromResult(false); }
    }

    string TrashPath(string rel, string stamp)
        => Path.Combine(root, SyncScan.Trash, stamp, rel.Replace('/', Path.DirectorySeparatorChar));
    public Task Archive(string rel, string stamp, CancellationToken ct)
    {
        var p = P(rel); if (!File.Exists(p)) return Task.CompletedTask;
        var t = TrashPath(rel, stamp); Directory.CreateDirectory(Path.GetDirectoryName(t)!); File.Copy(p, t, true); return Task.CompletedTask;
    }
    public Task Trash(string rel, string stamp, CancellationToken ct)
    {
        var p = P(rel); if (!File.Exists(p)) return Task.CompletedTask;
        var t = TrashPath(rel, stamp); Directory.CreateDirectory(Path.GetDirectoryName(t)!); File.Move(p, t, true); return Task.CompletedTask;
    }
    public void Dispose() { }
}

// A folder on the other laptop. One connection, one request at a time. The other laptop must be on its Receive screen with the same code
// AND must have allowed Folder Sync for one chosen folder (Settings > "Allow Folder Sync on this laptop").
public sealed class RemoteSide : ISyncSide
{
    readonly TcpClient c; readonly Stream s; readonly SemaphoreSlim gate = new(1, 1);
    public string PeerName { get; private set; } = ""; public string RootName { get; private set; } = ""; public bool Encrypted { get; private set; }
    long free = -1; bool broken;

    RemoteSide(TcpClient c, Stream s) { this.c = c; this.s = s; }
    public string Label => $"{PeerName}: {RootName}";
    public bool Reachable() => !broken && c.Connected;
    public long FreeSpace() => free;

    public static async Task<RemoteSide> Connect(string host, string code, CancellationToken ct)
    {
        var (c, s, secure) = await Net.OpenAsync(host, code, ct);
        try
        {
            await Net.SendMsg(s, new { code, mode = "sync", v = 2, name = Environment.MachineName }, ct);
            var r = await Net.RecvFirst(s, secure, ct);
            if (!r.GetProperty("ok").GetBoolean())
            {
                var why = r.TryGetProperty("reason", out var w) ? w.GetString() : "";
                throw new Exception(why == "sync-off" ? "The other laptop has not allowed Folder Sync. There, open Settings > \"Allow Folder Sync on this laptop\" first."
                    : why == "busy" ? "The other laptop is already syncing." : why == "code" ? "Wrong pairing code" : "The other laptop refused Folder Sync (older MoveIt?).");
            }
            var side = new RemoteSide(c, s) { Encrypted = secure };
            side.PeerName = r.TryGetProperty("name", out var n) ? n.GetString() ?? "Laptop" : "Laptop";
            side.RootName = r.TryGetProperty("root", out var rn) ? rn.GetString() ?? "folder" : "folder";
            side.free = r.TryGetProperty("free", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt64() : -1;
            return side;
        }
        catch { c.Dispose(); throw; }
    }

    async Task<JsonElement> Call(object msg, CancellationToken ct)
    {
        try { await Net.SendMsg(s, msg, ct); var r = await Net.RecvMsg(s, ct); return r; }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException) { broken = true; throw new IOException("Connection to the other laptop was lost."); }
    }
    static string Err(JsonElement r) => r.TryGetProperty("reason", out var w) ? w.GetString() ?? "failed" : "failed";

    public async Task<Dictionary<string, FileSig>> Snapshot(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var d = new Dictionary<string, FileSig>(StringComparer.OrdinalIgnoreCase); int skip = 0;
            while (true)
            {
                var r = await Call(new { t = "list", skip, take = 2000 }, ct);
                if (!r.GetProperty("ok").GetBoolean()) throw new IOException(Err(r));
                foreach (var e in r.GetProperty("files").EnumerateArray()) { d[e.GetProperty("p").GetString()!] = new FileSig(e.GetProperty("s").GetInt64(), e.GetProperty("m").GetInt64()); skip++; }
                if (!r.GetProperty("more").GetBoolean()) break;
            }
            return d;
        }
        finally { gate.Release(); }
    }

    public async Task<string?> Hash(string rel, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { var r = await Call(new { t = "hash", path = rel }, ct); return r.GetProperty("ok").GetBoolean() ? r.GetProperty("sha").GetString() : null; }
        finally { gate.Release(); }
    }

    public async Task<FileSig?> Stat(string rel, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { var r = await Call(new { t = "stat", path = rel }, ct); return r.GetProperty("ok").GetBoolean() ? new FileSig(r.GetProperty("s").GetInt64(), r.GetProperty("m").GetInt64()) : null; }
        finally { gate.Release(); }
    }

    // The request stays locked until the caller disposes the SyncRead (the bytes and the checksum trailer are part of the same exchange).
    public async Task<SyncRead> OpenRead(string rel, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var r = await Call(new { t = "get", path = rel }, ct);
            if (!r.GetProperty("ok").GetBoolean()) throw new IOException(Err(r));
            long size = r.GetProperty("s").GetInt64(), m = r.GetProperty("m").GetInt64();
            var lim = new LimitedReadStream(s, size); string? trailer = null; bool read = false;
            return new SyncRead
            {
                Stream = lim, Size = size, MTimeTicks = m,
                Sha = async () => { if (!read) { read = true; try { var t = await Net.RecvMsg(s, ct); trailer = t.TryGetProperty("sha", out var x) ? x.GetString() : null; } catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException) { broken = true; throw; } } return trailer; },
                Release = () => { if (!read) broken = true; gate.Release(); }   // stopped before the trailer: the connection is out of step and must not be reused
            };
        }
        catch { gate.Release(); throw; }
    }

    public async Task Write(string rel, Stream src, long size, long mtimeTicks, Func<Task<string?>> sourceSha, Action<long>? onBytes, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            bool wantRs = size >= Net.ResumeMin;   // big files may continue from a partial copy on the other laptop (it answers "off")
            var ready = await Call(wantRs ? (object)new { t = "put", path = rel, s = size, m = mtimeTicks, rs = 1 } : new { t = "put", path = rel, s = size, m = mtimeTicks }, ct);
            if (!ready.GetProperty("ok").GetBoolean()) throw new IOException(Err(ready) == "space" ? "Not enough space on the other laptop" : Err(ready));
            long off = wantRs && ready.TryGetProperty("off", out var ov) && ov.ValueKind == JsonValueKind.Number ? Math.Clamp(ov.GetInt64(), 0, size) : 0;
            try
            {
                var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20); long sent = 0;
                try
                {
                    while (sent < off)   // bytes the other laptop already holds: read through the (hashing) stream, do not send again
                    {
                        int r0 = await src.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, off - sent)), ct);
                        if (r0 == 0) throw new IOException("The file changed while it was being copied. It will be tried again at the next sync.");
                        sent += r0;
                    }
                    if (off > 0) onBytes?.Invoke(off);
                    while (sent < size)
                    {
                        int r = await src.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, size - sent)), ct);
                        if (r == 0) throw new IOException("The file changed while it was being copied. It will be tried again at the next sync.");
                        await s.WriteAsync(buf.AsMemory(0, r), ct); sent += r; onBytes?.Invoke(r);
                    }
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
                await s.FlushAsync(ct);
                var sha = await sourceSha();
                var done = await Call(new { t = "end", sha }, ct);
                if (!done.GetProperty("ok").GetBoolean()) throw new IOException(Err(done) == "checksum" ? "Checksum mismatch: the other laptop discarded the copy." : Err(done));
            }
            catch (Exception ex) when (ex is not IOException io || io.Message.StartsWith("Connection")) { broken = true; throw; }
            catch (IOException) { broken = true; throw; }   // bytes were already on the wire: the exchange is out of step, so this connection is finished
        }
        finally { gate.Release(); }
    }

    async Task<bool> Simple(object msg, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { var r = await Call(msg, ct); return r.GetProperty("ok").GetBoolean(); }
        finally { gate.Release(); }
    }
    public Task<bool> Rename(string from, string to, CancellationToken ct) => Simple(new { t = "rename", from, to }, ct);
    public async Task Archive(string rel, string stamp, CancellationToken ct) { if (!await Simple(new { t = "archive", path = rel, stamp }, ct)) throw new IOException("Could not keep the old version on the other laptop"); }
    public async Task Trash(string rel, string stamp, CancellationToken ct) { if (!await Simple(new { t = "trash", path = rel, stamp }, ct)) throw new IOException("Could not move the file to the sync trash on the other laptop"); }

    public void Dispose()
    {
        try { if (!broken) Net.SendMsg(s, new { t = "bye" }, CancellationToken.None).Wait(500); } catch { }
        try { c.Dispose(); } catch { }
    }
}

// Receiver side of Folder Sync: answers the other laptop, but ONLY inside the one folder the user allowed on this laptop.
// Nothing is deleted (trash only), nothing outside the folder is touched, sensitive files are refused.
public static class SyncHost
{
    public static string? Root;   // set only while the user has allowed Folder Sync on this laptop (Settings); null = refuse everything
    static int busy;
    public static event Action<string>? Activity;   // short text for the UI ("Folder Sync: 12 files received")

    public static async Task Serve(Stream s, string deviceName, string peer, CancellationToken ct)
    {
        var root = Root;
        if (root == null || !Directory.Exists(root)) { await Net.SendMsg(s, new { ok = false, reason = "sync-off" }, ct); return; }
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { await Net.SendMsg(s, new { ok = false, reason = "busy" }, ct); return; }
        try
        {
            await Net.SendMsg(s, new { ok = true, v = 2, sync = 1, name = deviceName, root = Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } n ? n : root, free = Net.FreeSpace(root) }, ct);
            Activity?.Invoke($"Folder Sync with {peer}: connected");
            List<(string p, FileSig sig)>? snap = null; int copied = 0;
            while (!ct.IsCancellationRequested)
            {
                if (Root == null) { await Net.SendMsg(s, new { ok = false, reason = "sync-off" }, ct); return; }   // the user switched it off meanwhile
                var m = await Net.RecvMsg(s, ct); var t = m.GetProperty("t").GetString();
                string Path_() => m.GetProperty("path").GetString() ?? "";
                try
                {
                    switch (t)
                    {
                        case "bye": Activity?.Invoke($"Folder Sync with {peer}: finished ({copied} file(s) received)"); return;
                        case "list":
                            {
                                int skip = m.GetProperty("skip").GetInt32(), take = Math.Clamp(m.GetProperty("take").GetInt32(), 1, 2000);
                                if (skip == 0 || snap == null) snap = await Task.Run(() => SyncScan.Snapshot(root, ct).Select(kv => (kv.Key, kv.Value)).ToList(), ct);
                                var page = snap.Skip(skip).Take(take).Select(x => new { p = x.p, s = x.sig.Size, m = x.sig.MTimeTicks }).ToList();
                                await Net.SendMsg(s, new { ok = true, files = page, more = skip + page.Count < snap.Count }, ct); break;
                            }
                        case "stat":
                            {
                                var fi = new FileInfo(SyncScan.Resolve(root, Path_()));
                                await Net.SendMsg(s, fi.Exists ? new { ok = true, s = fi.Length, m = fi.LastWriteTimeUtc.Ticks } : new { ok = false, s = 0L, m = 0L }, ct); break;
                            }
                        case "hash":
                            {
                                var h = await Task.Run(() => Duplicates.Hash(SyncScan.Resolve(root, Path_())), ct);
                                await Net.SendMsg(s, h != null ? new { ok = true, sha = h } : new { ok = false, sha = "" }, ct); break;
                            }
                        case "get": await Get(s, SyncScan.Resolve(root, Path_()), ct); break;
                        case "put":
                            {
                                bool rsWanted = m.TryGetProperty("rs", out var rsx) && rsx.ValueKind == JsonValueKind.Number && rsx.GetInt32() == 1;
                                if (await Put(s, root, SyncScan.Resolve(root, Path_()), m.GetProperty("s").GetInt64(), m.GetProperty("m").GetInt64(), rsWanted, ct)) copied++;
                                break;
                            }
                        case "rename":
                            {
                                var a = SyncScan.Resolve(root, m.GetProperty("from").GetString() ?? ""); var b = SyncScan.Resolve(root, m.GetProperty("to").GetString() ?? "");
                                bool ok = File.Exists(a) && !File.Exists(b); if (ok) { Directory.CreateDirectory(Path.GetDirectoryName(b)!); File.Move(a, b); }
                                await Net.SendMsg(s, new { ok }, ct); break;
                            }
                        case "archive":
                        case "trash":
                            {
                                var rel = Path_(); var p = SyncScan.Resolve(root, rel); var stamp = SafeStamp(m.GetProperty("stamp").GetString());
                                if (File.Exists(p))
                                {
                                    var tp = Path.Combine(root, SyncScan.Trash, stamp, rel.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(tp)!);
                                    if (t == "archive") File.Copy(p, tp, true); else File.Move(p, tp, true);
                                }
                                await Net.SendMsg(s, new { ok = true }, ct); break;
                            }
                        default: await Net.SendMsg(s, new { ok = false, reason = "unknown" }, ct); break;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or DirectoryNotFoundException or FileNotFoundException)
                { await Net.SendMsg(s, new { ok = false, reason = "refused" }, ct); }
            }
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    static string SafeStamp(string? x) { var v = new string((x ?? "").Where(ch => char.IsAsciiLetterOrDigit(ch)).Take(20).ToArray()); return v.Length == 0 ? DateTime.Now.ToString("yyyyMMdd_HHmmss") : v; }

    static async Task Get(Stream s, string path, CancellationToken ct)
    {
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Net.SendMsg(s, new { ok = false, reason = "unreadable", s = 0L, m = 0L }, ct); return; }
        await using (fs)
        {
            long size = fs.Length; await Net.SendMsg(s, new { ok = true, s = size, m = File.GetLastWriteTimeUtc(path).Ticks }, ct);
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                long sent = 0;
                while (sent < size)
                {
                    int r = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, size - sent)), ct);
                    if (r == 0) throw new IOException("File shrank while sending");   // the stream is now out of step: dropping the connection is the only safe answer
                    await s.WriteAsync(buf.AsMemory(0, r), ct); h.AppendData(buf, 0, r); sent += r;
                }
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
            await s.FlushAsync(ct);
            await Net.SendMsg(s, new { sha = Convert.ToHexString(h.GetHashAndReset()) }, ct);
        }
    }

    static async Task<bool> Put(Stream s, string root, string path, long size, long mtimeTicks, bool wantRs, CancellationToken ct)
    {
        long free = Net.FreeSpace(root);
        if (size < 0 || (free >= 0 && size + (64L << 20) > free)) { await Net.SendMsg(s, new { ok = false, reason = "space" }, ct); return false; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bool resumable = wantRs && size >= Net.ResumeMin;
        // A partial copy is named after the exact source version (mtime + size): if the file changed on the other side, the old partial is never reused.
        var tmp = resumable ? $"{path}.moveit-part-{mtimeTicks}-{size}" : path + ".moveit-tmp";
        long off = 0;
        if (resumable)
        {
            try
            {
                var dir = Path.GetDirectoryName(path)!; var name = Path.GetFileName(path);
                foreach (var old in Directory.EnumerateFiles(dir, name + ".moveit-part-*"))   // partials of other versions of this file are obsolete
                    if (!string.Equals(old, tmp, StringComparison.OrdinalIgnoreCase)) Net.TryDelete(old);
                var fi = new FileInfo(tmp);
                if (fi.Exists) { if (fi.Length <= size) off = fi.Length; else fi.Delete(); }
            }
            catch { off = 0; }
        }
        await Net.SendMsg(s, resumable ? (object)new { ok = true, off } : new { ok = true }, ct);
        bool ok = false, keep = false;
        try
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (off > 0) await Net.HashPrefix(tmp, off, h, ct);   // the final checksum must cover the whole file, not only the new part
            await using (var o = new FileStream(tmp, off > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (off > 0) o.Seek(off, SeekOrigin.Begin);
                var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
                try
                {
                    long left = size - off;
                    while (left > 0) { int r = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ct); if (r == 0) throw new IOException("Connection lost"); await o.WriteAsync(buf.AsMemory(0, r), ct); h.AppendData(buf, 0, r); left -= r; }
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
            }
            var trailer = await Net.RecvMsg(s, ct);
            var want = trailer.TryGetProperty("sha", out var x) ? x.GetString() : null;
            if (want != null && !string.Equals(want, Convert.ToHexString(h.GetHashAndReset()), StringComparison.OrdinalIgnoreCase)) { await Net.SendMsg(s, new { ok = false, reason = "checksum" }, ct); return false; }   // bad partial: discarded, next try starts from 0
            File.SetLastWriteTimeUtc(tmp, new DateTime(mtimeTicks, DateTimeKind.Utc));
            File.Move(tmp, path, true); ok = true;
            await Net.SendMsg(s, new { ok = true }, ct);
            return true;
        }
        catch (IOException) { keep = resumable; throw; }   // connection dropped: keep the partial so the next sync continues
        finally { if (!ok && !keep) Net.TryDelete(tmp); }
    }
}
