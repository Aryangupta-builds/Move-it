using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MoveIt;

public record Prog(long Done, long Total, double Speed);

public static class Net
{
    public const int Port = 50505, DiscPort = 50506, Chunk = 1 << 20;

    public static async Task SendMsg(Stream s, object o, CancellationToken ct)
    {
        var d = JsonSerializer.SerializeToUtf8Bytes(o);
        var h = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(h, d.Length);
        await s.WriteAsync(h, ct); await s.WriteAsync(d, ct);
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
}

public class Receiver
{
    readonly string code, dest; readonly Action<string> log; readonly IProgress<Prog> prog;
    readonly CancellationTokenSource cts = new(); TcpListener? l;

    public Receiver(string code, string dest, Action<string> log, IProgress<Prog> prog)
    { this.code = code; this.dest = dest; this.log = log; this.prog = prog; }

    public void Start() { _ = Task.Run(Discovery); _ = Task.Run(Serve); }
    public void Stop() { cts.Cancel(); l?.Stop(); }

    async Task Discovery()
    {
        try
        {
            using var u = new UdpClient(new IPEndPoint(IPAddress.Any, Net.DiscPort));
            while (!cts.IsCancellationRequested)
            {
                var r = await u.ReceiveAsync(cts.Token);
                if (Encoding.ASCII.GetString(r.Buffer) == "MOVEIT?")
                { var b = Encoding.UTF8.GetBytes(Environment.MachineName); await u.SendAsync(b, b.Length, r.RemoteEndPoint); }
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
        catch (Exception e) when (!cts.IsCancellationRequested) { log("Cannot listen: " + e.Message); }
        catch { }
    }

    string Safe(string rel)
    {
        var bad = Path.GetInvalidFileNameChars();
        var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..").Select(p => new string(p.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()));
        var b = Path.GetFullPath(dest);
        var p = Path.GetFullPath(Path.Combine(new[] { b }.Concat(parts).ToArray()));
        if (!p.StartsWith(b, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Bad path");
        var root = Path.Combine(Path.GetDirectoryName(p)!, Path.GetFileNameWithoutExtension(p)); var ext = Path.GetExtension(p);
        for (int i = 1; File.Exists(p); i++) p = $"{root} ({i}){ext}";
        return p;
    }

    async Task Handle(TcpClient c)
    {
        try
        {
            using (c)
            {
                var s = c.GetStream(); var ct = cts.Token;
                var h = await Net.RecvMsg(s, ct);
                var given = Encoding.UTF8.GetBytes(h.GetProperty("code").GetString() ?? "");
                if (!CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(code)))
                { await Net.SendMsg(s, new { ok = false }, ct); log("Rejected: wrong code"); return; }
                await Net.SendMsg(s, new { ok = true }, ct);
                long total = h.GetProperty("total").GetInt64(), done = 0, last = 0; int n = 0;
                var sw = Stopwatch.StartNew(); var buf = new byte[Net.Chunk];
                log($"Connected: {h.GetProperty("count").GetInt32()} files incoming");
                while (true)
                {
                    var m = await Net.RecvMsg(s, ct);
                    if (m.GetProperty("t").GetString() == "end") break;
                    var path = Safe(m.GetProperty("path").GetString()!);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    long left = m.GetProperty("size").GetInt64();
                    await using var f = File.Create(path);
                    while (left > 0)
                    {
                        int r = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ct);
                        if (r == 0) throw new IOException("Sender disconnected");
                        await f.WriteAsync(buf.AsMemory(0, r), ct); left -= r; done += r;
                        if (sw.ElapsedMilliseconds - last > 200) { last = sw.ElapsedMilliseconds; prog.Report(new(done, total, done / Math.Max(sw.Elapsed.TotalSeconds, .1))); }
                    }
                    n++;
                }
                await Net.SendMsg(s, new { ok = true }, ct);
                prog.Report(new(total, total, 0)); log($"Done! {n} files saved to {dest}");
            }
        }
        catch (Exception e) { log("Error: " + e.Message); }
    }
}

public static class Sender
{
    public static IEnumerable<(string, string)> Walk(string folder, string prefix) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))
            .Select(f => (f, prefix + "/" + Path.GetRelativePath(folder, f).Replace('\\', '/')));

    public static async Task<List<string>> Scan()
    {
        var found = new List<string>();
        using var u = new UdpClient { EnableBroadcast = true };
        var q = Encoding.ASCII.GetBytes("MOVEIT?");
        await u.SendAsync(q, q.Length, new IPEndPoint(IPAddress.Broadcast, Net.DiscPort));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { while (true) { var r = await u.ReceiveAsync(cts.Token); var ip = r.RemoteEndPoint.Address.ToString(); if (!found.Contains(ip)) found.Add(ip); } }
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

    public static async Task Send(string host, string code, List<(string p, string rel)> files, IProgress<Prog> prog, CancellationToken ct)
    {
        var sized = new List<(string p, string rel, long len)>();
        foreach (var (p, rel) in files) { try { sized.Add((p, rel, new FileInfo(p).Length)); } catch { } }
        long total = sized.Sum(x => x.len), done = 0, last = 0;
        using var c = new TcpClient(); await c.ConnectAsync(host, Net.Port, ct);
        var s = c.GetStream();
        await Net.SendMsg(s, new { code, total, count = sized.Count }, ct);
        if (!(await Net.RecvMsg(s, ct)).GetProperty("ok").GetBoolean()) throw new Exception("Wrong code");
        var sw = Stopwatch.StartNew(); var buf = new byte[Net.Chunk];
        foreach (var (p, rel, len) in sized)
        {
            ct.ThrowIfCancellationRequested();
            FileStream? f; try { f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); } catch { continue; }
            await Net.SendMsg(s, new { t = "file", path = rel, size = len }, ct);
            await using (f)
            {
                long sent = 0;
                while (sent < len)
                {
                    int want = (int)Math.Min(buf.Length, len - sent);
                    int r = await f.ReadAsync(buf.AsMemory(0, want), ct);
                    if (r == 0) { Array.Clear(buf, 0, want); r = want; } // file shrank: pad to keep protocol in sync
                    await s.WriteAsync(buf.AsMemory(0, r), ct); sent += r; done += r;
                    if (sw.ElapsedMilliseconds - last > 200) { last = sw.ElapsedMilliseconds; prog.Report(new(done, total, done / Math.Max(sw.Elapsed.TotalSeconds, .1))); }
                }
            }
        }
        await Net.SendMsg(s, new { t = "end" }, ct);
        await Net.RecvMsg(s, ct);
        prog.Report(new(total, total, 0));
    }
}
