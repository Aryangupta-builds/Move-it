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
public record Prog(long Done, long Total, double Speed, string File = "", int Index = 0, int Count = 0);

public enum RxState { Listening, Connected, Receiving, Complete, Cancelled, Failed, Disconnected }
public record ReceivedFile(string Name, long Size);
// One event type shared by the laptop receiver and the phone/tablet web receiver, so the UI has a single code path.
public record RxEvent(RxState State, string Message = "", string Device = "", string DeviceType = "", int Count = 0, long Total = 0, IReadOnlyList<ReceivedFile>? Files = null);

public class RemoteCancelledException : Exception { public RemoteCancelledException() : base("Cancelled by the other device") { } }

public static class Net
{
    public const int Port = 50505, DiscPort = 50506, Chunk = 1 << 20;

    public static async Task SendMsg(Stream s, object o, CancellationToken ct)
    {
        var d = JsonSerializer.SerializeToUtf8Bytes(o);
        var h = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(h, d.Length);
        await s.WriteAsync(h, ct); await s.WriteAsync(d, ct); await s.FlushAsync(ct);
    }

    public static async Task<JsonElement> RecvMsg(Stream s, CancellationToken ct)
    {
        var h = new byte[4]; await s.ReadExactlyAsync(h, ct);
        int n = BinaryPrimitives.ReadInt32BigEndian(h);
        if (n < 0 || n > (1 << 20)) throw new IOException("Bad message");
        var d = new byte[n]; await s.ReadExactlyAsync(d, ct);
        return JsonDocument.Parse(d).RootElement.Clone();
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

    public static void TryDelete(string? path) { try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { } }
}

public class Receiver
{
    readonly string code, dest, deviceName;
    readonly Action<RxEvent> raise; readonly Action<Prog> prog;
    readonly CancellationTokenSource cts = new();
    readonly object gate = new();
    TcpListener? l; TcpClient? active; CancellationTokenSource? session; bool userCancelled;

    public Receiver(string code, string dest, Action<RxEvent> raise, Action<Prog> prog, string? name = null)
    { this.code = code; this.dest = dest; this.raise = raise; this.prog = prog; deviceName = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name; }

    public void Start() { _ = Task.Run(Discovery); _ = Task.Run(Serve); }
    public void Stop() { cts.Cancel(); try { l?.Stop(); } catch { } }

    // Receiver-side cancel: tells the sender first (so it shows "Cancelled by receiver"), then aborts and cleans up.
    public void CancelTransfer()
    {
        TcpClient? c; CancellationTokenSource? sc;
        lock (gate) { c = active; sc = session; if (c != null) userCancelled = true; }
        if (c == null) return;
        _ = Task.Run(async () =>
        {
            try { await Net.SendMsg(c.GetStream(), new { t = "cancel" }, CancellationToken.None); await Task.Delay(250); } catch { }
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

    string Safe(string rel)
    {
        var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..").Select(Net.CleanSegment).ToArray();
        if (parts.Length == 0) parts = new[] { "file" };
        var b = Path.GetFullPath(dest);
        var p = Path.GetFullPath(Path.Combine(new[] { b }.Concat(parts).ToArray()));
        if (!p.StartsWith(b, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Bad path");
        return Net.UniquePath(p);
    }

    // The .part file is still open when a transfer aborts: close it first, otherwise Windows refuses to delete it.
    static void CloseAndDelete(FileStream? f, string? part) { try { f?.Dispose(); } catch { } Net.TryDelete(part); }

    async Task Handle(TcpClient c)
    {
        bool mine = false; string? part = null; FileStream? f = null; var files = new List<ReceivedFile>();
        string peer = "Laptop"; int skipped = 0;
        using (c)
        {
            try
            {
                Net.KeepAlive(c.Client);
                var s = c.GetStream();
                var h = await Net.RecvMsg(s, cts.Token);
                var given = Encoding.UTF8.GetBytes(h.GetProperty("code").GetString() ?? "");
                if (!CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(code)))
                { await Net.SendMsg(s, new { ok = false, reason = "code" }, cts.Token); raise(new(RxState.Listening, "Rejected a connection: wrong pairing code")); return; }
                lock (gate)
                {
                    if (active == null) { active = c; mine = true; userCancelled = false; session = CancellationTokenSource.CreateLinkedTokenSource(cts.Token); }
                }
                if (!mine) { await Net.SendMsg(s, new { ok = false, reason = "busy" }, cts.Token); return; }
                await Net.SendMsg(s, new { ok = true }, cts.Token);
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
                    string rel = m.GetProperty("path").GetString() ?? "file", fin = "";
                    long left = m.GetProperty("size").GetInt64(), size = left; idx++;
                    f = null; bool skip = false;
                    try
                    {
                        fin = Safe(rel); part = fin + ".part";
                        Directory.CreateDirectory(Path.GetDirectoryName(fin)!);
                        f = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                    }
                    catch { skip = true; part = null; }
                    var shown = Path.GetFileName(rel);
                    var fh = new byte[4];
                    while (left > 0)
                    {
                        // file data arrives as chunks: [4-byte length][bytes]. Length -1 means "sender cancelled".
                        await s.ReadExactlyAsync(fh, ct);
                        int r = BinaryPrimitives.ReadInt32BigEndian(fh);
                        if (r == -1) throw new RemoteCancelledException();
                        if (r <= 0 || r > buf.Length || r > left) throw new IOException("Bad data from the other laptop");
                        await s.ReadExactlyAsync(buf.AsMemory(0, r), ct);
                        if (!skip && f != null)
                        {
                            try { await f.WriteAsync(buf.AsMemory(0, r), ct); }
                            catch (OperationCanceledException) { throw; }
                            catch { skip = true; await f.DisposeAsync(); f = null; Net.TryDelete(part); part = null; } // disk full etc: keep protocol in sync, skip file
                        }
                        left -= r; done += r;
                        if (sw.ElapsedMilliseconds - last > 200)
                        { last = sw.ElapsedMilliseconds; prog(new(done, total, done / Math.Max(sw.Elapsed.TotalSeconds, .1), shown, idx, count)); }
                    }
                    if (f != null) { await f.DisposeAsync(); File.Move(part!, fin); files.Add(new(Path.GetFileName(fin), size)); part = null; }
                    else skipped++;
                }
                await Net.SendMsg(s, new { ok = true }, ct);
                prog(new(total, total, 0, "", count, count));
                raise(new(RxState.Complete, skipped == 0 ? $"{files.Count} file(s) saved" : $"{files.Count} saved, {skipped} could not be saved", peer, "Laptop", files.Count, total, files));
            }
            catch (RemoteCancelledException) { CloseAndDelete(f, part); raise(new(RxState.Cancelled, "Cancelled by the sender", peer)); }
            catch (OperationCanceledException)
            {
                CloseAndDelete(f, part);
                if (userCancelled) raise(new(RxState.Cancelled, "Transfer cancelled", peer));
            }
            catch (Exception e)
            {
                CloseAndDelete(f, part);
                if (mine) raise(new(RxState.Failed, e is IOException ? "Connection lost. The transfer was interrupted." : "Error: " + e.Message, peer));
            }
            finally
            {
                if (mine) lock (gate) { active = null; try { session?.Dispose(); } catch { } session = null; }
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
    public static async Task Send(string host, string code, string deviceName, List<(string p, string rel)> files, IProgress<Prog> prog, CancellationToken ct)
    {
        var sized = new List<(string p, string rel, long len)>();
        foreach (var (p, rel) in files) { try { sized.Add((p, rel, new FileInfo(p).Length)); } catch { } }
        long total = sized.Sum(x => x.len), done = 0, last = 0;
        using var c = new TcpClient(); await c.ConnectAsync(host, Net.Port, ct);
        Net.KeepAlive(c.Client);
        var s = c.GetStream();
        await Net.SendMsg(s, new { code, total, count = sized.Count, name = deviceName }, ct);
        var reply = await Net.RecvMsg(s, ct);
        if (!reply.GetProperty("ok").GetBoolean())
            throw new Exception(reply.TryGetProperty("reason", out var rs) && rs.GetString() == "busy" ? "The receiver is busy with another transfer" : "Wrong pairing code");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool remoteCancel = false, inBody = false; var fh = new byte[4];
        // One background reader owns all incoming messages: a "cancel" from the receiver, or the final ok.
        _ = Task.Run(async () =>
        {
            try
            {
                var m = await Net.RecvMsg(s, linked.Token);
                if (m.TryGetProperty("t", out var t) && t.GetString() == "cancel") { remoteCancel = true; linked.Cancel(); }
                else ack.TrySetResult(true);
            }
            catch { ack.TrySetResult(false); if (!linked.IsCancellationRequested) try { linked.Cancel(); } catch { } }
        });

        try
        {
            var sw = Stopwatch.StartNew(); var buf = new byte[Net.Chunk]; int idx = 0;
            foreach (var (p, rel, len) in sized)
            {
                linked.Token.ThrowIfCancellationRequested(); idx++;
                FileStream? f; try { f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); } catch { continue; }
                await Net.SendMsg(s, new { t = "file", path = rel, size = len }, CancellationToken.None);
                await using (f)
                {
                    long sent = 0; inBody = true;
                    while (sent < len)
                    {
                        linked.Token.ThrowIfCancellationRequested();   // cancel is only checked between chunks, so a chunk is never half-written
                        int want = (int)Math.Min(buf.Length, len - sent);
                        int r = await f.ReadAsync(buf.AsMemory(0, want), linked.Token);
                        if (r == 0) { Array.Clear(buf, 0, want); r = want; } // file shrank: pad to keep protocol in sync
                        BinaryPrimitives.WriteInt32BigEndian(fh, r);
                        await s.WriteAsync(fh, CancellationToken.None); await s.WriteAsync(buf.AsMemory(0, r), CancellationToken.None);
                        sent += r; done += r;
                        if (sw.ElapsedMilliseconds - last > 200) { last = sw.ElapsedMilliseconds; prog.Report(new(done, total, done / Math.Max(sw.Elapsed.TotalSeconds, .1), Path.GetFileName(rel), idx, sized.Count)); }
                    }
                    inBody = false;
                }
            }
            await Net.SendMsg(s, new { t = "end" }, linked.Token);
            if (!await ack.Task.WaitAsync(TimeSpan.FromSeconds(60), linked.Token)) throw new IOException("The receiver did not confirm");
            prog.Report(new(total, total, 0, "", sized.Count, sized.Count));
        }
        catch (Exception) when (remoteCancel) { throw new RemoteCancelledException(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try
            {
                if (inBody) { BinaryPrimitives.WriteInt32BigEndian(fh, -1); await s.WriteAsync(fh, CancellationToken.None); }   // inside file data: chunk length -1
                else await Net.SendMsg(s, new { t = "cancel" }, CancellationToken.None);
            }
            catch { }
            throw;
        }
        catch (OperationCanceledException) { throw new IOException("Connection to the receiver was lost"); }
    }
}
