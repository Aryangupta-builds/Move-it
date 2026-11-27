using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MoveIt;

// Tiny web server so a phone/tablet (Android or iPhone) can scan the QR and send one or many files.
public class WebShare
{
    public const int Port = 50507;
    readonly string code, dest;
    readonly Action<string> onFile;
    readonly CancellationTokenSource cts = new();
    TcpListener? l;

    public WebShare(string code, string dest, Action<string> onFile) { this.code = code; this.dest = dest; this.onFile = onFile; }

    public void Start() { l = new TcpListener(IPAddress.Any, Port); l.Start(); _ = Task.Run(Loop); }
    public void Stop() { cts.Cancel(); try { l?.Stop(); } catch { } }

    async Task Loop()
    {
        while (!cts.IsCancellationRequested)
        {
            try { var c = await l!.AcceptTcpClientAsync(cts.Token); _ = Task.Run(() => Handle(c)); }
            catch { break; }
        }
    }

    async Task Handle(TcpClient c)
    {
        using (c)
        try
        {
            var s = c.GetStream();
            var buf = new byte[16384]; int len = 0, end = -1;
            while (end < 0 && len < buf.Length)
            {
                int n = await s.ReadAsync(buf.AsMemory(len), cts.Token); if (n == 0) return; len += n;
                end = Find(buf, len);
            }
            if (end < 0) return;
            var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var first = lines[0].Split(' ');
            var h = new Dictionary<string, string>();
            foreach (var ln in lines.Skip(1)) { int i = ln.IndexOf(':'); if (i > 0) h[ln[..i].Trim().ToLower()] = ln[(i + 1)..].Trim(); }

            if (first[0] == "GET") { await Reply(s, 200, "text/html; charset=utf-8", Page); return; }
            if (first[0] == "POST" && h.TryGetValue("x-code", out var cd) && cd == code && h.TryGetValue("x-name", out var nm))
            {
                long total = h.TryGetValue("content-length", out var cl) ? long.Parse(cl) : 0;
                Directory.CreateDirectory(Path.Combine(dest, "FromPhone"));
                var name = Path.GetFileName(Uri.UnescapeDataString(nm)); if (name == "") name = "file";
                var path = Path.Combine(dest, "FromPhone", name);
                for (int k = 1; File.Exists(path); k++)
                    path = Path.Combine(dest, "FromPhone", Path.GetFileNameWithoutExtension(name) + $" ({k})" + Path.GetExtension(name));
                using (var fs = File.Create(path))
                {
                    int body = len - (end + 4); long got = 0;
                    if (body > 0) { await fs.WriteAsync(buf.AsMemory(end + 4, (int)Math.Min(body, total)), cts.Token); got = Math.Min(body, total); }
                    var b2 = new byte[1 << 17];
                    while (got < total)
                    {
                        int n = await s.ReadAsync(b2.AsMemory(0, (int)Math.Min(b2.Length, total - got)), cts.Token);
                        if (n == 0) break; await fs.WriteAsync(b2.AsMemory(0, n), cts.Token); got += n;
                    }
                }
                onFile(Path.GetFileName(path));
                await Reply(s, 200, "text/plain", "ok");
                return;
            }
            await Reply(s, 403, "text/plain", "Wrong code");
        }
        catch { }
    }

    static int Find(byte[] b, int len)
    {
        for (int i = 0; i + 3 < len; i++) if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
        return -1;
    }

    static async Task Reply(Stream s, int code, string type, string body)
    {
        var b = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} OK\r\nContent-Type: {type}\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head); await s.WriteAsync(b); await s.FlushAsync();
    }

    const string Page = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>MoveIt</title><style>
*{box-sizing:border-box}body{margin:0;min-height:100vh;font-family:system-ui,sans-serif;color:#fff;background:linear-gradient(135deg,#050816,#0a1a4a);display:flex;align-items:center;justify-content:center;padding:20px}
.c{width:100%;max-width:440px;padding:24px;border-radius:28px;background:rgba(255,255,255,.08);border:1px solid rgba(255,255,255,.2);backdrop-filter:blur(24px);text-align:center}
h1{margin:0 0 4px}p{opacity:.7;margin:4px 0 18px}
label.b,button{display:block;width:100%;padding:16px;border:0;border-radius:18px;font-size:17px;font-weight:600;color:#fff;background:linear-gradient(135deg,#2f80ed,#56ccf2);cursor:pointer}
input[type=file]{display:none}input[type=text]{width:100%;padding:14px;border-radius:14px;border:1px solid rgba(255,255,255,.3);background:rgba(255,255,255,.1);color:#fff;font-size:18px;text-align:center;margin-bottom:12px}
.f{margin-top:12px;text-align:left;font-size:14px}.bar{height:6px;border-radius:3px;background:rgba(255,255,255,.15);margin:4px 0 10px}.bar i{display:block;height:100%;width:0;border-radius:3px;background:#38ef7d}
</style></head><body><div class="c"><h1>MoveIt</h1><p>Send files to this laptop</p>
<input id="code" type="text" inputmode="numeric" maxlength="6" placeholder="6-digit code" style="display:none">
<label class="b" for="pick">Choose files</label><input id="pick" type="file" multiple><div id="list"></div></div>
<script>
const q=new URLSearchParams(location.search);let code=q.get('c')||'';const ci=document.getElementById('code');
if(!code){ci.style.display='block';ci.oninput=()=>code=ci.value}
document.getElementById('pick').onchange=async e=>{for(const f of e.target.files){
 const d=document.createElement('div');d.className='f';d.innerHTML='<span></span><div class="bar"><i></i></div>';d.firstChild.textContent=f.name;document.getElementById('list').appendChild(d);
 await new Promise(r=>{const x=new XMLHttpRequest();x.open('POST','/up');x.setRequestHeader('X-Code',code);x.setRequestHeader('X-Name',encodeURIComponent(f.name));
 x.upload.onprogress=p=>d.querySelector('i').style.width=(100*p.loaded/p.total)+'%';
 x.onload=()=>{d.firstChild.textContent=(x.status==200?'✓ ':'✗ ')+f.name;if(x.status==200)d.querySelector('i').style.width='100%';r()};x.onerror=()=>{d.firstChild.textContent='✗ '+f.name;r()};x.send(f)})}};
</script></body></html>
""";
}
