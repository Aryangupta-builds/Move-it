using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;

namespace MoveIt;

// Spec 29: automatic self-check. These tests need only this laptop (loopback + a temporary folder), so they can be run before the
// two-laptop tests in QA_CHECKLIST.md. Nothing outside a temporary folder is touched; the folder is deleted at the end.
// A PASS here does NOT replace the real two-laptop, installer and UI tests.
public static class SelfTest
{
    public sealed record Result(string Name, bool Ok, string Detail);

    public static async Task<List<Result>> RunAll(CancellationToken ct)
    {
        var list = new List<Result>();
        var tmp = Path.Combine(Path.GetTempPath(), "MoveIt-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        async Task Run(string name, Func<Task<string?>> body)
        {
            try { var fail = await body(); list.Add(new Result(name, fail == null, fail ?? "OK")); }
            catch (Exception ex) { list.Add(new Result(name, false, ex.GetType().Name + ": " + ex.Message)); }
        }
        try
        {
            await Run("Credential files and folders are blocked (spec 5, 8, 26)", () => Task.FromResult(SensitiveCheck()));
            await Run("Folder Sync refuses unsafe paths (spec 20 I)", () => Task.FromResult(PathCheck(tmp)));
            await Run("SHA-256 helper matches the real hash (spec 22)", () => Task.FromResult(HashCheck(tmp)));
            await Run("Empty folders detected, sensitive items skipped, source untouched (spec 6)", () => Task.FromResult(ScanCheck(tmp, ct)));
            await Run("Verified write keeps good copy, discards bad checksum (spec 22)", () => WriteCheck(tmp, ct));
            await Run("Sync trash and old-version archive never erase (spec 20 E)", () => TrashCheck(tmp, ct));
            await Run("Sync scan ignores temporary and partial files (spec 20)", () => SnapshotCheck(tmp, ct));
            await Run("Small-file packing keeps the bytes identical (spec 27)", () => PackCheck());
            await Run("Encrypted channel round trip on this PC (spec 26)", () => SecureCheck(ct));
            await Run("Wrong pairing code cannot read the data (spec 26)", () => WrongCodeCheck(ct));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
        return list;
    }

    static string? SensitiveCheck()
    {
        foreach (var n in new[] { "Login Data", "Cookies", "id_rsa", "a.pem", "vault.kdbx", ".env", ".env.local" })
            if (!Sensitive.IsBlockedFile(n)) return n + " is not blocked";
        if (Sensitive.IsBlockedFile("notes.txt")) return "notes.txt is blocked by mistake";
        if (!Sensitive.IsBlockedDir(@"C:\Users\x\.ssh")) return ".ssh folder is not blocked";
        return null;
    }

    static string? PathCheck(string tmp)
    {
        foreach (var bad in new[] { @"..\x", "a/../../x", "C:/x", "/x", @"\x", ".moveit-sync-trash/a.txt", ".env", "a/.ssh/id_rsa", "a//b", "" })
        {
            try { SyncScan.Resolve(tmp, bad); return "accepted: " + bad; }
            catch (InvalidOperationException) { }
        }
        var ok = SyncScan.Resolve(tmp, "a/b.txt");
        return ok.StartsWith(Path.GetFullPath(tmp), StringComparison.OrdinalIgnoreCase) ? null : "good path resolved outside the folder";
    }

    static string? HashCheck(string tmp)
    {
        var data = RandomNumberGenerator.GetBytes(300_000); var p = Path.Combine(tmp, "h.bin"); File.WriteAllBytes(p, data);
        return Duplicates.Hash(p) == Convert.ToHexString(SHA256.HashData(data)) ? null : "hash differs";
    }

    static string? ScanCheck(string tmp, CancellationToken ct)
    {
        var root = Path.Combine(tmp, "scan"); Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "empty")); Directory.CreateDirectory(Path.Combine(root, "sub")); Directory.CreateDirectory(Path.Combine(root, ".ssh"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello"); File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "world!");
        File.WriteAllText(Path.Combine(root, ".env"), "SECRET=1"); File.WriteAllText(Path.Combine(root, ".ssh", "id_rsa"), "key");
        var r = Scanner.Scan(new ScanRoot { Category = "Test", Name = "T", Path = root, DestPrefix = "X" }, ct);
        if (r.Files.Count != 2) return $"expected 2 files, found {r.Files.Count}";
        if (r.Bytes != 11) return $"expected 11 bytes, found {r.Bytes}";
        if (!r.EmptyDirs.Contains("X/empty")) return "empty folder not detected";
        if (r.Blocked < 2) return $"sensitive items not skipped ({r.Blocked})";
        if (!Directory.Exists(Path.Combine(root, "empty")) || !File.Exists(Path.Combine(root, ".env"))) return "the scan changed the source";
        return null;
    }

    static async Task<string?> WriteCheck(string tmp, CancellationToken ct)
    {
        var side = new LocalSide(Path.Combine(tmp, "w")); var data = RandomNumberGenerator.GetBytes(200_000);
        var mt = DateTime.UtcNow.AddDays(-3).Ticks; var sha = Convert.ToHexString(SHA256.HashData(data));
        await side.Write("d/a.bin", new MemoryStream(data), data.Length, mt, () => Task.FromResult<string?>(sha), null, ct);
        var path = Path.Combine(tmp, "w", "d", "a.bin");
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return "good copy not written correctly";
        if (Math.Abs(File.GetLastWriteTimeUtc(path).Ticks - mt) > TimeSpan.TicksPerSecond * 2) return "modified time not kept";
        try
        {
            await side.Write("d/a.bin", new MemoryStream(RandomNumberGenerator.GetBytes(1000)), 1000, mt, () => Task.FromResult<string?>(new string('0', 64)), null, ct);
            return "bad checksum was accepted";
        }
        catch (IOException) { }
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return "the good file was replaced by a bad copy";
        if (File.Exists(path + ".moveit-tmp")) return "temporary file left behind";
        try { await side.Write("d/short.bin", new MemoryStream(new byte[10]), 50, mt, () => Task.FromResult<string?>(null), null, ct); return "short copy was accepted"; }
        catch (IOException) { }
        return File.Exists(Path.Combine(tmp, "w", "d", "short.bin")) ? "short copy was saved" : null;
    }

    static async Task<string?> TrashCheck(string tmp, CancellationToken ct)
    {
        var root = Path.Combine(tmp, "t"); var side = new LocalSide(root); Directory.CreateDirectory(Path.Combine(root, "d"));
        var f = Path.Combine(root, "d", "x.txt"); File.WriteAllText(f, "keep me");
        await side.Archive("d/x.txt", "s1", ct);
        if (!File.Exists(f) || !File.Exists(Path.Combine(root, ".moveit-sync-trash", "s1", "d", "x.txt"))) return "archive did not keep both versions";
        await side.Trash("d/x.txt", "s2", ct);
        if (File.Exists(f)) return "trash left the file in place";
        return File.ReadAllText(Path.Combine(root, ".moveit-sync-trash", "s2", "d", "x.txt")) == "keep me" ? null : "trashed file content is wrong (file erased?)";
    }

    static async Task<string?> SnapshotCheck(string tmp, CancellationToken ct)
    {
        var root = Path.Combine(tmp, "snap"); Directory.CreateDirectory(Path.Combine(root, "d")); Directory.CreateDirectory(Path.Combine(root, ".moveit-sync-trash"));
        File.WriteAllText(Path.Combine(root, "d", "z.txt"), "z"); File.WriteAllText(Path.Combine(root, "d", "x.moveit-tmp"), "t");
        File.WriteAllText(Path.Combine(root, "d", "y.moveit-part-1-2"), "p"); File.WriteAllText(Path.Combine(root, ".moveit-sync-trash", "o.txt"), "o");
        var s = await new LocalSide(root).Snapshot(ct);
        return s.Count == 1 && s.ContainsKey("d/z.txt") ? null : "snapshot listed: " + string.Join(", ", s.Keys);
    }

    static async Task<string?> PackCheck()
    {
        var sink = new MemoryStream(); var want = new MemoryStream(); var co = new CoalescingStream(sink) { Hold = true }; var rnd = new Random(7);
        for (int i = 0; i < 2000; i++) { var b = new byte[rnd.Next(1, 50)]; rnd.NextBytes(b); await co.WriteAsync(b); want.Write(b); }
        var big = RandomNumberGenerator.GetBytes(200_000); await co.WriteAsync(big); want.Write(big);
        co.Hold = false; await co.FlushNow();
        return sink.ToArray().AsSpan().SequenceEqual(want.ToArray()) ? null : "bytes differ after packing";
    }

    static async Task<string?> SecureCheck(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(15));
        var lis = new TcpListener(IPAddress.Loopback, 0); lis.Start();
        try
        {
            int port = ((IPEndPoint)lis.LocalEndpoint).Port; var payload = RandomNumberGenerator.GetBytes(300_000);
            var server = Task.Run(async () =>
            {
                using var sc = await lis.AcceptTcpClientAsync(cts.Token);
                var (ss, secure) = await SecureStream.Accept(sc.GetStream(), "123456", cts.Token);
                if (!secure) return "server did not get the secure handshake";
                var got = new byte[payload.Length]; await ss.ReadExactlyAsync(got, cts.Token);
                await ss.WriteAsync(got, cts.Token); await ss.FlushAsync(cts.Token); await Task.Delay(300, cts.Token); return (string?)null;
            }, cts.Token);
            using var cc = new TcpClient(); await cc.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var cs = await SecureStream.Client(cc.GetStream(), "123456", cts.Token);
            await cs.WriteAsync(payload, cts.Token); await cs.FlushAsync(cts.Token);
            var back = new byte[payload.Length]; await cs.ReadExactlyAsync(back, cts.Token);
            var sres = await server;
            if (sres != null) return sres;
            return back.AsSpan().SequenceEqual(payload) ? null : "data came back different";
        }
        finally { lis.Stop(); }
    }

    static async Task<string?> WrongCodeCheck(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(15));
        var lis = new TcpListener(IPAddress.Loopback, 0); lis.Start();
        try
        {
            int port = ((IPEndPoint)lis.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var sc = await lis.AcceptTcpClientAsync(cts.Token);
                var (ss, _) = await SecureStream.Accept(sc.GetStream(), "111111", cts.Token);
                try { var b = new byte[16]; await ss.ReadExactlyAsync(b, cts.Token); return "server could read data sent with a different code"; }
                catch (IOException) { return (string?)null; }
            }, cts.Token);
            using var cc = new TcpClient(); await cc.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var cs = await SecureStream.Client(cc.GetStream(), "222222", cts.Token);
            await cs.WriteAsync(new byte[16], cts.Token); await cs.FlushAsync(cts.Token);
            return await server;
        }
        finally { lis.Stop(); }
    }

    public static string Format(List<Result> r)
    {
        var sb = new StringBuilder();
        foreach (var x in r) sb.AppendLine((x.Ok ? "PASS  " : "FAIL  ") + x.Name + (x.Ok ? "" : "\n      " + x.Detail));
        sb.AppendLine().AppendLine($"{r.Count(x => x.Ok)} of {r.Count} passed. This only covers checks that need no second laptop; the other tests stay in QA_CHECKLIST.md.");
        return sb.ToString();
    }
}

public sealed partial class MainWindow
{
    async void SelfTest_Click(object s, RoutedEventArgs e) => await RunTool(async () =>
    {
        ShowToolStatus("Running self-check…");
        List<SelfTest.Result> res;
        try { res = await Task.Run(() => SelfTest.RunAll(CancellationToken.None)); } finally { HideToolStatus(); }
        var text = SelfTest.Format(res);
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoveIt", "Reports"); Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, $"self-check-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), text);
        }
        catch { }
        await Msg(res.All(x => x.Ok) ? "Self-check passed" : "Self-check found problems", text.Length > 1800 ? text[..1800] + "…" : text);
    });
}
