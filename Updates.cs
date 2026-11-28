using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
namespace MoveIt;

// Update manifest (JSON) published at the official update source:
// { "version": "1.3.1", "notes": "what changed", "installerUrl": "https://.../MOVE-IT-Setup.exe", "sha256": "64 hex chars" }
public sealed record UpdateInfo(string Version, string Notes, string InstallerUrl, string Sha256);

public static class Updater
{
    static readonly HttpClient http = CreateClient();
    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MoveIt-UpdateCheck");   // GitHub's API rejects requests without a User-Agent
        return c;
    }

    public static bool IsNewer(string latest, string current) =>
        Version.TryParse(latest, out var a) && Version.TryParse(current, out var b) && a > b;

    // Returns null when the source is fine but there is nothing newer. Throws with a readable message on any problem.
    public static async Task<UpdateInfo?> CheckAsync(string manifestUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The update source must be a valid https:// address.");
        using var resp = await http.GetAsync(u, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("No release has been published at the update source yet.");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        UpdateInfo m;
        using (var doc = JsonDocument.Parse(json))
            m = doc.RootElement.TryGetProperty("tag_name", out _) ? await FromGitHubRelease(doc.RootElement, ct)
                : JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidOperationException("The update source returned no data.");
        if (!Uri.TryCreate(m.InstallerUrl, UriKind.Absolute, out var iu) || iu.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The update source gave an installer address that is not https.");
        return IsNewer(m.Version, AppInfo.Version) ? m : null;
    }

    // GitHub "latest release" JSON: needs assets MOVE-IT-Setup.exe and MOVE-IT-Setup.exe.sha256 (build.yml already makes both).
    static async Task<UpdateInfo> FromGitHubRelease(JsonElement r, CancellationToken ct)
    {
        string tag = (r.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        string notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        string exe = "", sha = "";
        if (r.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString(); var url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name == "MOVE-IT-Setup.exe") exe = url;
                else if (name == "MOVE-IT-Setup.exe.sha256") sha = url;
            }
        if (exe == "" || sha == "") throw new InvalidOperationException("The release has no MOVE-IT-Setup.exe with its .sha256 file, so it cannot be verified.");
        var hash = (await http.GetStringAsync(sha, ct)).Trim().Split(' ', '\t', '\r', '\n')[0];
        return new UpdateInfo(tag, notes, exe, hash);
    }

    // Downloads to %TEMP%, checks SHA-256 against the manifest. A file that does not match is deleted and never returned.
    public static async Task<string> DownloadVerifiedAsync(UpdateInfo info, IProgress<double>? prog, CancellationToken ct = default)
    {
        if (info.Sha256.Length != 64) throw new InvalidOperationException("The update has no valid SHA-256 checksum, so it will not be installed.");
        var path = Path.Combine(Path.GetTempPath(), $"MOVE-IT-Setup-{info.Version}.exe");
        try
        {
            using var r = await http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            r.EnsureSuccessStatusCode();
            long total = r.Content.Headers.ContentLength ?? 0, got = 0;
            using (var src = await r.Content.ReadAsStreamAsync(ct))
            using (var dst = File.Create(path))
            {
                var buf = new byte[81920]; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n), ct); got += n; if (total > 0) prog?.Report(got * 100.0 / total); }
            }
            string hash;
            using (var fs = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The downloaded file does not match its checksum. It was deleted and not installed.");
            return path;
        }
        catch { try { File.Delete(path); } catch { } throw; }
    }
}
