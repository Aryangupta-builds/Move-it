using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MoveIt;

// Progress snapshot. File/Index/Count are optional so older call sites keep working.
public record Prog(long Done, long Total, double Speed, string File = "", int Index = 0, int Count = 0, long FileSize = 0);

public enum RxState { Listening, Connected, Receiving, Complete, Cancelled, Failed, Disconnected }
public record ReceivedFile(string Name, long Size);
// One event type shared by the laptop receiver and the phone/tablet web receiver, so the UI has a single code path.
public record RxEvent(RxState State, string Message = "", string Device = "", string DeviceType = "", int Count = 0, long Total = 0, IReadOnlyList<ReceivedFile>? Files = null, IReadOnlyList<string>? Problems = null);

public class RemoteCancelledException : Exception { public RemoteCancelledException() : base("Cancelled by the other device") { } }

public static class Net
{
    public static string? BindIp;   // null = Windows chooses; otherwise outgoing transfers use this adapter address
    public static TcpClient NewClient() => BindIp == null ? new TcpClient() : new TcpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(BindIp), 0));
    public const int Port = 50505, DiscPort = 50506, Chunk = 1 << 20;

    // Spec 26: encrypted connection when the other laptop supports it. An older MoveIt cannot, so we fall back to plain TCP and the caller is told (secure = false).
    public static bool AllowPlainFallback = true;

    // Spec 22: mid-file resume for big files. Only used when BOTH sides announce it ("rs" in the first reply); older MoveIt versions never see it.
    public const long ResumeMin = 64L << 20;
    public static string PartialRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "Partial");   // outside Incoming on purpose: RecoverIncoming must not touch it
    public static string PartialPath(string peer, string rel, long size, long mtime)
        => Path.Combine(PartialRoot, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{peer}|{rel}|{size}|{mtime}")))[..40] + ".part");
    public static async Task HashPrefix(string path, long n, System.Security.Cryptography.IncrementalHash h, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        var b = new byte[1 << 20]; long left = n;
        while (left > 0)
        {
            int r = await fs.ReadAsync(b.AsMemory(0, (int)Math.Min(b.Length, left)), ct);
            if (r == 0) throw new IOException("Partial file is shorter than expected");
            h.AppendData(b, 0, r); left -= r;
        }
    }
    public static void CleanPartials()   // partial files nobody resumed within 7 days
    {
        try { if (Directory.Exists(PartialRoot)) foreach (var f in Directory.GetFiles(PartialRoot, "*.part")) if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) TryDelete(f); } catch { }
    }
    public static async Task<(TcpClient c, Stream s, bool secure)> OpenAsync(string host, string code, CancellationToken ct)
    {
        var c = NewClient();
        try
        {
            await c.ConnectAsync(host, Port, ct); KeepAlive(c.Client);
            return (c, await SecureStream.Client(c.GetStream(), code, ct), true);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or SocketException)
        {
            c.Dispose();
            if (!AllowPlainFallback) throw new Exception("The other laptop does not support encrypted transfers. Update MoveIt on both laptops.");
            var p = NewClient(); await p.ConnectAsync(host, Port, ct); KeepAlive(p.Client);
            return (p, p.GetStream(), false);
        }
        catch { c.Dispose(); throw; }
    }

    // First reply on a secure connection: if it cannot be decrypted the pairing code differs (the code is part of the key).
    public static async Task<JsonElement> RecvFirst(Stream s, bool secure, CancellationToken ct)
    {
        try { return await RecvMsg(s, ct); }
        catch (Exception ex) when (secure && ex is IOException) { throw new Exception("Wrong pairing code"); }
    }

    public static async Task SendMsg(Stream s, object o, CancellationToken ct)
    {
        var d = JsonSerializer.SerializeToUtf8Bytes(o);
        var m = new byte[4 + d.Length]; BinaryPrimitives.WriteInt32BigEndian(m, d.Length); d.CopyTo(m, 4);   // one write = one encrypted record (was two): less overhead for many small files
        await s.WriteAsync(m, ct); await s.FlushAsync(ct);
    }

    public static async Task<JsonElement> RecvMsg(Stream s, CancellationToken ct)
    {
        var h = new byte[4]; await s.ReadExactlyAsync(h, ct);
        int n = BinaryPrimitives.ReadInt32BigEndian(h);
        if (n < 0 || n > (1 << 20)) throw new IOException("Bad message");
        var d = new byte[n]; await s.ReadExactlyAsync(d, ct);
        using var doc = JsonDocument.Parse(d); return doc.RootElement.Clone();   // Clone, then free the pooled parser memory (matters on weak PCs)
    }

    public static string LocalIp()
    {
        try { using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0); s.Connect("8.8.8.8", 80); return ((IPEndPoint)s.LocalEndPoint!).Address.ToString(); }
        catch { return "127.0.0.1"; }
    }

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    // Makes one path segment safe on Windows: bad chars, reserved names, trailing dots, absurd length.
    public static string CleanSegment(string p)
    {
        var bad = Path.GetInvalidFileNameChars();
        var t = new string(p.Select(ch => bad.Contains(ch) || ch < 32 ? '_' : ch).ToArray()).Trim().TrimEnd('.', ' ');
        if (t.Length == 0) t = "file";
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(t))) t = "_" + t;
        if (t.Length > 150)
        {
            var ext = Path.GetExtension(t); if (ext.Length > 20) ext = ext[..20];
            t = t[..(150 - ext.Length)] + ext;
        }
        return t;
    }

    // Duplicate file names become "name (1).ext", "name (2).ext" ... (also avoids clashing with a pending .part file)
    public static string UniquePath(string p)
    {
        var root = Path.Combine(Path.GetDirectoryName(p)!, Path.GetFileNameWithoutExtension(p)); var ext = Path.GetExtension(p);
        for (int i = 1; File.Exists(p) || File.Exists(p + ".part"); i++) p = $"{root} ({i}){ext}";
        return p;
    }

    // Detects a dead peer (WiFi off, cable out) in ~20 s instead of waiting forever on a silent socket.
    public static void KeepAlive(Socket sock)
    {
        try
        {
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 8);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch { }
    }

    public static long FreeSpace(string path)
    {
        try { var root = Path.GetPathRoot(Path.GetFullPath(path)); return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace; }
        catch { return -1; }   // -1 = unknown
    }

    public static void TryDelete(string? path) { try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { } }
}

public class Receiver
{
    readonly string code, dest, deviceName;
    readonly Action<RxEvent> raise; readonly Action<Prog> prog;
    readonly CancellationTokenSource cts = new();
    readonly object gate = new();
    TcpListener? l; TcpClient? active; Stream? activeStream; CancellationTokenSource? session; bool userCancelled;

    public Receiver(string code, string dest, Action<RxEvent> raise, Action<Prog> prog, string? name = null)
    { this.code = code; this.dest = dest; this.raise = raise; this.prog = prog; deviceName = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name; }

    public void Start() { _ = Task.Run(Discovery); _ = Task.Run(Serve); }
    public void Stop() { cts.Cancel(); try { l?.Stop(); } catch { } }

    // Receiver-side cancel: tells the sender first (so it shows "Cancelled by receiver"), then aborts and cleans up.
    public void CancelTransfer()
    {
        TcpClient? c; CancellationTokenSource? sc; Stream? cs;
        lock (gate) { c = active; cs = activeStream; sc = session; if (c != null) userCancelled = true; }
        if (c == null) return;
        _ = Task.Run(async () =>
        {
            try { await Net.SendMsg(cs ?? c.GetStream(), new { t = "cancel" }, CancellationToken.None); await Task.Delay(250); } catch { }
            try { sc?.Cancel(); } catch { }
        });
    }

    async Task Discovery()
    {
        try
        {
            using var u = new UdpClient(new IPEndPoint(IPAddress.Any, Net.DiscPort));
            while (!cts.IsCancellationRequested)
            {
                var r = await u.ReceiveAsync(cts.Token);
                if (Encoding.ASCII.GetString(r.Buffer) == "MOVEIT?")
                { var b = Encoding.UTF8.GetBytes(deviceName); await u.SendAsync(b, b.Length, r.RemoteEndPoint); }
            }
        }
        catch { }
    }

    async Task Serve()
    {
        try
        {
            l = new TcpListener(IPAddress.Any, Net.Port); l.Start();
            while (!cts.IsCancellationRequested) { var c = await l.AcceptTcpClientAsync(cts.Token); _ = Task.Run(() => Handle(c)); }
        }
        catch (Exception e) when (!cts.IsCancellationRequested) { raise(new(RxState.Failed, "Cannot listen: " + e.Message)); }
        catch { }
    }

    string Safe(string rel, bool exact = false)
    {
        var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..").Select(Net.CleanSegment).ToArray();
        if (parts.Length == 0) parts = new[] { "file" };
        var b = Path.GetFullPath(dest);
        var p = Path.GetFullPath(Path.Combine(new[] { b }.Concat(parts).ToArray()));
        if (!p.StartsWith(b, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Bad path");
        return exact ? p : Net.UniquePath(p);
    }

    string SafeDir(string rel)
    {
        var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..").Select(Net.CleanSegment).ToArray();
        if (parts.Length == 0) throw new InvalidOperationException("Bad path");
        var b = Path.GetFullPath(dest);
        var p = Path.GetFullPath(Path.Combine(new[] { b }.Concat(parts).ToArray()));
        if (!p.StartsWith(b, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Bad path");
        return p;
    }

    // Probe connection: gives the sender this laptop's battery / free space / OS, and answers ping + speed-test messages.
    // It never starts a transfer and never writes to the save folder.
    async Task Probe(Stream s, JsonElement h)
    {
        var ct = cts.Token; bool busy; lock (gate) busy = active != null;
        var b = Battery.Read();
        await Net.SendMsg(s, new { ok = true, v = 2, name = deviceName, os = Environment.OSVersion.VersionString, bat = b.Percent, charging = b.Charging, hasBattery = b.HasBattery, free = Net.FreeSpace(dest), busy }, ct);
        var buf = new byte[Net.Chunk];
        while (!ct.IsCancellationRequested)
        {
            var m = await Net.RecvMsg(s, ct); var t = m.GetProperty("t").GetString();
            if (t == "bye") return;
            if (t == "ping") { await Net.SendMsg(s, new { t = "pong" }, ct); continue; }
            if (busy) { await Net.SendMsg(s, new { ok = false, reason = "busy" }, ct); continue; }
            long n = Math.Clamp(m.GetProperty("bytes").GetInt64(), 0, 64L << 20);   // test payload is capped at 64 MB
            if (t == "up")
            {
                for (long left = n; left > 0;) { int r = (int)Math.Min(buf.Length, left); await s.ReadExactlyAsync(buf.AsMemory(0, r), ct); left -= r; }
                await Net.SendMsg(s, new { ok = true }, ct);
            }
            else if (t == "down")
            {
                for (long left = n; left > 0;) { int r = (int)Math.Min(buf.Length, left); await s.WriteAsync(buf.AsMemory(0, r), ct); left -= r; }
                await s.FlushAsync(ct);
            }
        }
    }

    // Resume support (spec 22): the sender lists files it wants to skip; for each one that already exists here with the same size we return its SHA-256.
    // Read-only: nothing is written or deleted. The sender compares the hash with its own copy before skipping anything.
    async Task Have(Stream s)
    {
        var ct = cts.Token;
        await Net.SendMsg(s, new { ok = true, v = 2 }, ct);
        while (!ct.IsCancellationRequested)
        {
            var m = await Net.RecvMsg(s, ct);
            if (m.GetProperty("t").GetString() != "have") return;
            var have = new List<object>();
            foreach (var e in m.GetProperty("files").EnumerateArray())
            {
                string rel = e.GetProperty("path").GetString() ?? ""; long size = e.GetProperty("size").GetInt64();
                try
                {
                    var p = Safe(rel, true); var fi = new FileInfo(p);
                    if (!fi.Exists || fi.Length != size) continue;
                    await using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
                    have.Add(new { path = rel, sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)) });
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            await Net.SendMsg(s, new { ok = true, have }, ct);
        }
    }

    // The .part file is still open when a transfer aborts: close it first, otherwise Windows refuses to delete it.
    static void CloseAndDelete(FileStream? f, string? part) { try { f?.Dispose(); } catch { } Net.TryDelete(part); }

    async Task Handle(TcpClient c)
    {
        bool mine = false, resumable = false; string? part = null; FileStream? f = null; var files = new List<ReceivedFile>();
        string peer = "Laptop"; int skipped = 0, failed = 0, dirsMade = 0; var problems = new List<string>();
        using (c)
        {
            try
            {
                var (s, secure) = await SecureStream.Accept(c.GetStream(), code, cts.Token);
                if (!secure && !Net.AllowPlainFallback) { raise(new(RxState.Listening, "Rejected an unencrypted connection (turned off in Settings)")); return; }
                JsonElement h;
                try { h = await Net.RecvMsg(s, cts.Token); }
                catch (IOException) when (secure) { raise(new(RxState.Listening, "Rejected a connection: wrong pairing code")); return; }
                var given = Encoding.UTF8.GetBytes(h.GetProperty("code").GetString() ?? "");
                if (!CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(code)))
                { await Net.SendMsg(s, new { ok = false, reason = "code" }, cts.Token); raise(new(RxState.Listening, "Rejected a connection: wrong pairing code")); return; }
                string? mode = h.TryGetProperty("mode", out var md) && md.ValueKind == JsonValueKind.String ? md.GetString() : null;
                if (mode == "probe") { await Probe(s, h); return; }
                if (mode == "have") { await Have(s); return; }
                if (mode == "sync") { await SyncHost.Serve(s, deviceName, (c.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "Laptop", cts.Token); return; }
                lock (gate)
                {
                    if (active == null) { active = c; activeStream = s; mine = true; userCancelled = false; session = CancellationTokenSource.CreateLinkedTokenSource(cts.Token); }
                }
                if (!mine) { await Net.SendMsg(s, new { ok = false, reason = "busy" }, cts.Token); return; }
                bool v2 = h.TryGetProperty("v", out var vv) && vv.ValueKind == JsonValueKind.Number && vv.GetInt32() >= 2;   // v2 peers: per-file SHA-256, empty folders, failure reports
                await Net.SendMsg(s, new { ok = true, v = 2, rs = 1, bat = Battery.Read().Percent, free = Net.FreeSpace(dest) }, cts.Token);
                var ct = session!.Token;

                if (h.TryGetProperty("name", out var nm) && !string.IsNullOrWhiteSpace(nm.GetString())) peer = nm.GetString()!;
                else peer = (c.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "Laptop";
                long total = h.GetProperty("total").GetInt64(), done = 0, last = 0; int count = h.GetProperty("count").GetInt32(), idx = 0;
                raise(new(RxState.Connected, $"Connected to {peer}", peer, "Laptop", count, total));
                var sw = Stopwatch.StartNew(); var buf = new byte[Net.Chunk];
                while (true)
                {
                    var m = await Net.RecvMsg(s, ct);
                    var t = m.GetProperty("t").GetString();
                    if (t == "end") break;
                    if (t == "cancel") throw new RemoteCancelledException();
                    if (t == "dir")   // empty folder (only sent when the user chose to include empty folders)
                    {
                        try { Directory.CreateDirectory(SafeDir(m.GetProperty("path").GetString() ?? "")); dirsMade++; } catch { }
                        continue;
                    }
                    string rel = m.GetProperty("path").GetString() ?? "file", fin = "";
                    long left = m.GetProperty("size").GetInt64(), size = left; idx++;
                    f = null; bool skip = false, abandoned = false;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long offset = 0; resumable = false;
                    bool wantRs = size >= Net.ResumeMin && m.TryGetProperty("rs", out var rsv) && rsv.ValueKind == JsonValueKind.Number && rsv.GetInt32() == 1;
                    try
                    {
                        fin = Safe(rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fin)!);
                        if (wantRs)
                        {
                            long mt = m.TryGetProperty("mt", out var mtv) && mtv.ValueKind == JsonValueKind.Number ? mtv.GetInt64() : 0;
                            part = Net.PartialPath(peer, rel, size, mt); Directory.CreateDirectory(Net.PartialRoot);
                            resumable = true;
                            if (File.Exists(part))
                            {
                                long have = new FileInfo(part).Length;
                                if (have > 0 && have < size) { await Net.HashPrefix(part, have, hash, ct); offset = have; }
                                else Net.TryDelete(part);
                            }
                            f = new FileStream(part, offset > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                            if (offset > 0) f.Seek(0, SeekOrigin.End);
                        }
                        else
                        {
                            part = fin + ".part";
                            f = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { try { f?.Dispose(); } catch { } f = null; skip = true; part = null; offset = 0; resumable = false; hash.GetHashAndReset(); }
                    if (wantRs) await Net.SendMsg(s, new { off = offset }, ct);   // the sender waits for this answer
                    left -= offset; done += offset;
                    var shown = Path.GetFileName(rel);
                    var fh = new byte[4];
                    while (left > 0)
                    {
                        // file data arrives as chunks: [4-byte length][bytes]. Length -1 means "sender cancelled".
                        await s.ReadExactlyAsync(fh, ct);
                        int r = BinaryPrimitives.ReadInt32BigEndian(fh);
                        if (r == -1) throw new RemoteCancelledException();
                        if (r == -2) { abandoned = true; done += left; left = 0; break; }   // sender could not finish this file (changed or unreadable): drop it, keep going
                        if (r <= 0 || r > buf.Length || r > left) throw new IOException("Bad data from the other laptop");
                        await s.ReadExactlyAsync(buf.AsMemory(0, r), ct);
                        if (!skip && f != null)
                        {
                            try { await f.WriteAsync(buf.AsMemory(0, r), ct); hash.AppendData(buf, 0, r); }
                            catch (OperationCanceledException) { throw; }
                            catch { skip = true; await f.DisposeAsync(); f = null; Net.TryDelete(part); part = null; } // disk full etc: keep protocol in sync, skip file
                        }
                        left -= r; done += r;
                        if (sw.ElapsedMilliseconds - last > 200)
                        { last = sw.ElapsedMilliseconds; prog(new(done, total, done / Math.Max(sw.Elapsed.TotalSeconds, .1), shown, idx, count, size)); }
                    }
                    string? sum = null;
                    if (v2 && !abandoned) { var sm = await Net.RecvMsg(s, ct); if (sm.TryGetProperty("sha256", out var shv)) sum = shv.GetString(); }
                    if (abandoned) { CloseAndDelete(f, part); f = null; part = null; failed++; problems.Add(shown + ": sender could not finish it"); }
                    else if (f != null)
                    {
                        await f.DisposeAsync();
                        if (v2 && sum != null && !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), sum, StringComparison.OrdinalIgnoreCase))
                        { Net.TryDelete(part); part = null; failed++; problems.Add(shown + ": checksum mismatch"); }   // checksum mismatch: never keep a damaged file
                        else { File.Move(part!, fin); files.Add(new(Path.GetFileName(fin), size)); part = null; }
                    }
                    else { skipped++; problems.Add(shown + ": could not be written"); }
                }
                await Net.SendMsg(s, new { ok = true, saved = files.Count, skipped, failed, dirs = dirsMade }, ct);
                prog(new(total, total, 0, "", count, count));
                int bad = skipped + failed;
                raise(new(RxState.Complete, bad == 0 ? $"{files.Count} file(s) saved" : $"{files.Count} saved, {bad} could not be saved", peer, "Laptop", files.Count, total, files, problems));
            }
            catch (RemoteCancelledException) { CloseAndDelete(f, part); raise(new(RxState.Cancelled, "Cancelled by the sender", peer)); }   // a deliberate cancel throws the partial file away
            catch (OperationCanceledException)
            {
                if (resumable && !userCancelled) { try { f?.Dispose(); } catch { } }   // interrupted, not cancelled: keep the partial big file so it can continue
                else CloseAndDelete(f, part);
                if (userCancelled) raise(new(RxState.Cancelled, "Transfer cancelled", peer));
            }
            catch (Exception e)
            {
                if (resumable && e is IOException) { try { f?.Dispose(); } catch { } }   // connection lost: keep the partial big file for a resume
                else CloseAndDelete(f, part);
                if (mine) raise(new(RxState.Failed, e is IOException ? "Connection lost. The transfer was interrupted." : "Error: " + e.Message, peer));
            }
            finally
            {
                if (mine) lock (gate) { active = null; activeStream = null; try { session?.Dispose(); } catch { } session = null; }
            }
        }
    }
}

public static class Sender
{
    public static IEnumerable<(string, string)> Walk(string folder, string prefix) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))
            .Select(f => (f, prefix + "/" + Path.GetRelativePath(folder, f).Replace('\\', '/')));

    public static async Task<List<(string Ip, string Name)>> Scan()
    {
        var found = new List<(string Ip, string Name)>();
        using var u = new UdpClient { EnableBroadcast = true };
        var q = Encoding.ASCII.GetBytes("MOVEIT?");
        await u.SendAsync(q, q.Length, new IPEndPoint(IPAddress.Broadcast, Net.DiscPort));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (true)
            {
                var r = await u.ReceiveAsync(cts.Token);
                var ip = r.RemoteEndPoint.Address.ToString();
                if (!found.Any(f => f.Ip == ip)) found.Add((ip, Encoding.UTF8.GetString(r.Buffer)));
            }
        }
        catch { }
        return found;
    }

    public static async Task<List<string>> ExportApps()
    {
        var tmp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "MoveIt_" + Guid.NewGuid().ToString("N"))).FullName;
        var res = new List<string>(); var rows = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path) in new[] {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall") })
        {
            using var k = hive.OpenSubKey(path); if (k == null) continue;
            foreach (var n in k.GetSubKeyNames())
            {
                using var sk = k.OpenSubKey(n);
                if (sk?.GetValue("DisplayName") is string name && name.Trim() != "")
                    rows.Add($"{name}  |  {sk.GetValue("DisplayVersion")}  |  {sk.GetValue("Publisher")}");
            }
        }
        var txt = Path.Combine(tmp, "installed_apps.txt"); File.WriteAllLines(txt, rows); res.Add(txt);
        var js = Path.Combine(tmp, "apps.json");
        try
        {
            var psi = new ProcessStartInfo("winget", $"export -o \"{js}\" --accept-source-agreements") { CreateNoWindow = true, UseShellExecute = false };
            using var pr = Process.Start(psi)!;
            await pr.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);
        }
        catch { }
        if (File.Exists(js))
        {
            res.Add(js);
            var bat = Path.Combine(tmp, "restore_apps.bat");
            File.WriteAllText(bat, "@echo off\r\nwinget import -i \"%~dp0apps.json\" --accept-package-agreements --accept-source-agreements --ignore-unavailable\r\npause\r\n");
            res.Add(bat);
        }
        return res;
    }

    // Throws OperationCanceledException (local cancel), RemoteCancelledException (receiver cancelled) or IOException/Exception (errors).
    // Returns an honest report: files that were unreadable, changed while sending, or rejected by the receiver are NOT counted as sent.
    public static async Task<SendReport> Send(string host, string code, string deviceName, List<(string p, string rel)> files, IProgress<Prog> prog, CancellationToken ct, IReadOnlyList<string>? emptyDirs = null)
    {
        var problems = new List<string>(); int unreadable = 0, changed = 0;
        var sized = new List<(string p, string rel, long len)>();
        foreach (var (p, rel) in files) { try { sized.Add((p, rel, new FileInfo(p).Length)); } catch { unreadable++; problems.Add("Cannot read: " + Path.GetFileName(p)); } }
        long total = sized.Sum(x => x.len), done = 0, last = 0;
        var (c, s0, secure) = await Net.OpenAsync(host, code, ct); using var cDispose = c;
        var co = new CoalescingStream(s0); Stream s = co;   // Hold stays false until the handshake is answered
        await Net.SendMsg(s, new { code, total, count = sized.Count, name = deviceName, v = 2 }, ct);
        var reply = await Net.RecvFirst(s, secure, ct);
        if (!reply.GetProperty("ok").GetBoolean())
            throw new Exception(reply.TryGetProperty("reason", out var rs) && rs.GetString() == "busy" ? "The receiver is busy with another transfer" : "Wrong pairing code");
        bool v2 = reply.TryGetProperty("v", out var rv) && rv.ValueKind == JsonValueKind.Number && rv.GetInt32() >= 2;
        int peerBat = reply.TryGetProperty("bat", out var pb) && pb.ValueKind == JsonValueKind.Number ? pb.GetInt32() : -1;
        long peerFree = reply.TryGetProperty("free", out var pf) && pf.ValueKind == JsonValueKind.Number ? pf.GetInt64() : -1;
        bool rsOk = reply.TryGetProperty("rs", out var rsx) && rsx.ValueKind == JsonValueKind.Number && rsx.GetInt32() == 1;   // receiver can continue a big file from a partial copy
        TaskCompletionSource<long>? offWait = null;
        co.Hold = true;   // from here on, small writes are packed into 64 KB records (flushed before every wait for the receiver)

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ack = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool remoteCancel = false, inBody = false; var fh = new byte[4];
        // One background reader owns all incoming messages: a "cancel" from the receiver, or the final summary.
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var m = await Net.RecvMsg(s, linked.Token);
                    if (m.TryGetProperty("t", out var t) && t.GetString() == "cancel") { remoteCancel = true; linked.Cancel(); return; }
                    if (m.TryGetProperty("off", out var o) && o.ValueKind == JsonValueKind.Number) { offWait?.TrySetResult(o.GetInt64()); continue; }   // answer to a big-file header
                    ack.TrySetResult(m); return;
                }
            }
            catch { ack.TrySetResult(null); offWait?.TrySetException(new IOException("Connection lost")); if (!linked.IsCancellationRequested) try { linked.Cancel(); } catch { } }
        });

        try
        {
            int dirs = 0;
            if (emptyDirs != null && emptyDirs.Count > 0)
            {
                if (v2) foreach (var d in emptyDirs) { await Net.SendMsg(s, new { t = "dir", path = d }, linked.Token); dirs++; }
                else problems.Add("The other laptop runs an older MoveIt: empty folders were not sent");
            }
            var sw = Stopwatch.StartNew(); var buf = new byte[Net.Chunk]; var wbuf = new byte[Net.Chunk + 4]; int idx = 0;
            foreach (var (p, rel, len) in sized)
            {
                linked.Token.ThrowIfCancellationRequested(); idx++;
                FileStream? f;
                try { f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan | FileOptions.Asynchronous); }   // we read 1 MB at a time ourselves, so no extra buffer
                catch { unreadable++; problems.Add("Cannot read: " + Path.GetFileName(p)); done += len; continue; }
                bool rsFile = rsOk && len >= Net.ResumeMin; long off = 0;
                if (rsFile)
                {
                    long mt = 0; try { mt = File.GetLastWriteTimeUtc(p).Ticks; } catch { }
                    offWait = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await Net.SendMsg(s, new { t = "file", path = rel, size = len, mt, rs = 1 }, CancellationToken.None);
                    await co.FlushNow(linked.Token);
                    off = await offWait.Task.WaitAsync(TimeSpan.FromMinutes(10), linked.Token);
                    if (off < 0 || off >= len) throw new IOException("Bad resume offset from the other laptop");
                }
                else await Net.SendMsg(s, new { t = "file", path = rel, size = len }, CancellationToken.None);
                bool abandoned = false;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (f)
                {
                    long sent = 0; inBody = true;
                    if (off > 0)   // the receiver already holds the first `off` bytes: hash them here too so the final checksum covers the whole file
                    {
                        long pl = off; bool tooShort = false;
                        while (pl > 0)
                        {
                            int rr = await f.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, pl)), linked.Token);
                            if (rr == 0) { tooShort = true; break; }
                            hash.AppendData(buf, 0, rr); pl -= rr;
                        }
                        if (tooShort)
                        {
                            BinaryPrimitives.WriteInt32BigEndian(fh, -2); await s.WriteAsync(fh, CancellationToken.None);
                            done += len; abandoned = true; changed++; problems.Add("Changed while sending (not copied): " + Path.GetFileName(rel));
                        }
                        else { sent = off; done += off; }
                    }
                    while (sent < len && !abandoned)
                    {
                        linked.Token.ThrowIfCancellationRequested();   // cancel is only checked between chunks, so a chunk is never half-written
                        int want = (int)Math.Min(buf.Length, len - sent);
                        int r = await f.ReadAsync(wbuf.AsMemory(4, want), linked.Token);   // data goes after a 4-byte gap so header + data leave in ONE write
                        if (r == 0)   // file shrank while sending
                        {
                            if (!v2) throw new IOException("A file changed while it was being sent: " + Path.GetFileName(rel));
                            BinaryPrimitives.WriteInt32BigEndian(fh, -2);   // tell the receiver to drop this file, then carry on with the next one
                            await s.WriteAsync(fh, CancellationToken.None);
                            done += len - sent; abandoned = true; changed++; problems.Add("Changed while sending (not copied): " + Path.GetFileName(rel));
                            break;
                        }
                        hash.AppendData(wbuf, 4, r);
                        BinaryPrimitives.WriteInt32BigEndian(wbuf, r);
                        await s.WriteAsync(wbuf.AsMemory(0, 4 + r), CancellationToken.None);
                        sent += r; done += r;
                        if (sw.ElapsedMilliseconds - last > 200) { last = sw.ElapsedMilliseconds; prog.Report(new(done, total, done / Math.Max(sw.Elapsed.TotalSeconds, .1), Path.GetFileName(rel), idx, sized.Count, len)); }
                    }
                    inBody = false;
                }
                if (v2 && !abandoned) await Net.SendMsg(s, new { t = "sum", sha256 = Convert.ToHexString(hash.GetHashAndReset()) }, CancellationToken.None);
            }
            co.Hold = false;
            await Net.SendMsg(s, new { t = "end" }, linked.Token);
            var a = await ack.Task.WaitAsync(TimeSpan.FromSeconds(60), linked.Token);
            if (a == null) throw new IOException("The receiver did not confirm");
            int rSkip = 0, rFail = 0;
            if (a.Value.TryGetProperty("skipped", out var k1) && k1.ValueKind == JsonValueKind.Number) rSkip = k1.GetInt32();
            if (a.Value.TryGetProperty("failed", out var k2) && k2.ValueKind == JsonValueKind.Number) rFail = k2.GetInt32();
            if (rSkip > 0) problems.Add($"{rSkip} file(s) could not be saved on the other laptop (disk full or no permission)");
            if (rFail > 0) problems.Add($"{rFail} file(s) failed the integrity check and were discarded");
            prog.Report(new(total, total, 0, "", sized.Count, sized.Count));
            return new SendReport(sized.Count - changed - rSkip - rFail, unreadable, changed, rSkip, rFail, dirs, v2, peerBat, peerFree, problems) { Encrypted = secure };
        }
        catch (Exception) when (remoteCancel) { throw new RemoteCancelledException(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try
            {
                co.Hold = false;   // send everything buffered, then the cancel marker
                if (inBody) { BinaryPrimitives.WriteInt32BigEndian(fh, -1); await s.WriteAsync(fh, CancellationToken.None); }   // inside file data: chunk length -1
                else await Net.SendMsg(s, new { t = "cancel" }, CancellationToken.None);
            }
            catch { }
            throw;
        }
        catch (OperationCanceledException) { throw new IOException("Connection to the receiver was lost"); }
    }

    // Spec 22: which of these files already exist on the other laptop with identical content (SHA-256 on both sides). Used to resume after an interruption.
    public static async Task<HashSet<string>> FindVerified(string host, string code, List<(string p, string rel)> files, CancellationToken ct)
    {
        var ok = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (c, s, secure) = await Net.OpenAsync(host, code, ct); using var cDispose = c;
        await Net.SendMsg(s, new { code, mode = "have", v = 2 }, ct);
        var r = await Net.RecvFirst(s, secure, ct);
        if (!r.GetProperty("ok").GetBoolean()) throw new Exception("Wrong pairing code");
        for (int i = 0; i < files.Count; i += 500)
        {
            var batch = files.Skip(i).Take(500).ToList(); var ask = new List<object>(); var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (p, rel) in batch) { try { ask.Add(new { path = rel, size = new FileInfo(p).Length }); local[rel] = p; } catch { } }
            await Net.SendMsg(s, new { t = "have", files = ask }, ct);
            var reply = await Net.RecvMsg(s, ct);
            if (!reply.TryGetProperty("have", out var arr)) throw new Exception("The other laptop does not support resume (older MoveIt).");
            foreach (var e in arr.EnumerateArray())
            {
                var rel = e.GetProperty("path").GetString() ?? ""; var sha = e.GetProperty("sha").GetString() ?? "";
                if (!local.TryGetValue(rel, out var lp)) continue;
                try
                {
                    await using var fs = new FileStream(lp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, true);
                    if (string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)), sha, StringComparison.OrdinalIgnoreCase)) ok.Add(rel);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }
        try { await Net.SendMsg(s, new { t = "bye" }, CancellationToken.None); } catch { }
        return ok;
    }

    // ---- Probe: battery / free space / OS of the other laptop, latency and a real speed test (no files are written) ----
    public static async Task<PeerInfo> Probe(string host, string code, CancellationToken ct)
    {
        var (c, s, secure) = await Net.OpenAsync(host, code, ct); using var cDispose = c;
        await Net.SendMsg(s, new { code, mode = "probe", v = 2 }, ct);
        var r = await Net.RecvFirst(s, secure, ct);
        if (!r.GetProperty("ok").GetBoolean()) throw new Exception("Wrong pairing code");
        int Int(string n, int d) => r.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : d;
        long Lng(string n) => r.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : -1;
        bool Bool(string n) => r.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.True;
        try { await Net.SendMsg(s, new { t = "bye" }, CancellationToken.None); } catch { }
        return new PeerInfo(r.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "", r.TryGetProperty("os", out var os) ? os.GetString() ?? "" : "",
            Int("bat", -1), Bool("charging"), !r.TryGetProperty("hasBattery", out _) || Bool("hasBattery"), Lng("free"), Bool("busy"));
    }

    // Measured, not theoretical: 5 pings, then up to `mb` MB each way. Receiver caps the payload at 64 MB. Nothing is stored on disk.
    public static async Task<SpeedResult> SpeedTest(string host, string code, int mb, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var (c, s, secure) = await Net.OpenAsync(host, code, ct); using var cDispose = c; c.NoDelay = true;
        await Net.SendMsg(s, new { code, mode = "probe", v = 2 }, ct);
        var hello = await Net.RecvFirst(s, secure, ct);
        if (!hello.GetProperty("ok").GetBoolean()) throw new Exception("Wrong pairing code");
        if (hello.TryGetProperty("busy", out var bz) && bz.ValueKind == JsonValueKind.True) throw new Exception("The other laptop is busy with a transfer");
        var lat = new List<double>(); int lost = 0;
        for (int i = 0; i < 5; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct); to.CancelAfter(2000);
            try { await Net.SendMsg(s, new { t = "ping" }, to.Token); await Net.RecvMsg(s, to.Token); lat.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)   // a timed-out ping leaves the stream out of step: stop and report what was measured
            {
                lost++;
                return new SpeedResult(lat.Count == 0 ? -1 : lat.Average(), lat.Count < 2 ? 0 : lat.Max() - lat.Min(), lost, 0, 0, started.Elapsed);
            }
        }
        long n = Math.Clamp(mb, 1, 64) * 1024L * 1024; var buf = new byte[Net.Chunk];
        double up = 0, down = 0;
        var w = Stopwatch.StartNew();
        await Net.SendMsg(s, new { t = "up", bytes = n }, ct);
        for (long left = n; left > 0;) { int r = (int)Math.Min(buf.Length, left); await s.WriteAsync(buf.AsMemory(0, r), ct); left -= r; }
        await s.FlushAsync(ct); await Net.RecvMsg(s, ct); w.Stop();
        up = n / Math.Max(w.Elapsed.TotalSeconds, 0.001);
        w.Restart();
        await Net.SendMsg(s, new { t = "down", bytes = n }, ct);
        for (long left = n; left > 0;) { int r = (int)Math.Min(buf.Length, left); await s.ReadExactlyAsync(buf.AsMemory(0, r), ct); left -= r; }
        w.Stop(); down = n / Math.Max(w.Elapsed.TotalSeconds, 0.001);
        try { await Net.SendMsg(s, new { t = "bye" }, CancellationToken.None); } catch { }
        return new SpeedResult(lat.Count == 0 ? -1 : lat.Average(), lat.Count == 0 ? -1 : lat.Max() - lat.Min(), lost, up, down, started.Elapsed);
    }
}

public record SendReport(int Sent, int Unreadable, int Changed, int PeerSkipped, int PeerFailed, int EmptyFolders, bool Checked, int PeerBattery, long PeerFree, List<string> Problems)
{
    public bool Encrypted { get; init; }
    public int NotSent => Unreadable + Changed + PeerSkipped + PeerFailed;
    public bool Clean => NotSent == 0;
}
public record PeerInfo(string Name, string Os, int Battery, bool Charging, bool HasBattery, long FreeBytes, bool Busy);
// Latency/jitter in ms (-1 = not available), speeds in bytes per second. LostPings is the number of pings that timed out (not a true packet-loss figure).
public record SpeedResult(double LatencyMs, double JitterMs, int LostPings, double UploadBps, double DownloadBps, TimeSpan Duration);
