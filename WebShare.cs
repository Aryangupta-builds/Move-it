using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MoveIt;

// Tiny web server so a phone/tablet (Android or iPhone) can scan the QR and send one or many files.
public class WebShare
{
    public const int Port = 50507;
    readonly string code, dest, devName;
    readonly Action<string, long, long> onProgress;
    readonly Action<string> onFile;
    readonly CancellationTokenSource cts = new();
    TcpListener? l;

    public WebShare(string code, string dest, string devName, Action<string, long, long> onProgress, Action<string> onFile)
    { this.code = code; this.dest = dest; this.devName = devName; this.onProgress = onProgress; this.onFile = onFile; }

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

            if (first[0] == "GET")
            {
                var route = first[1].Split('?')[0];
                if (route == "/logo.png")
                {
                    var lp = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
                    if (File.Exists(lp)) { await Reply(s, 200, "image/png", File.ReadAllBytes(lp)); return; }
                    await Reply(s, 404, "text/plain", "no logo"); return;
                }
                await Reply(s, 200, "text/html; charset=utf-8", Page.Replace("%%NAME%%", System.Net.WebUtility.HtmlEncode(devName))); return;
            }
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
                    int body = len - (end + 4); long got = 0; onProgress(Path.GetFileName(path), 0, total);
                    if (body > 0) { await fs.WriteAsync(buf.AsMemory(end + 4, (int)Math.Min(body, total)), cts.Token); got = Math.Min(body, total); onProgress(Path.GetFileName(path), got, total); }
                    var b2 = new byte[1 << 17];
                    while (got < total)
                    {
                        int n = await s.ReadAsync(b2.AsMemory(0, (int)Math.Min(b2.Length, total - got)), cts.Token);
                        if (n == 0) break; await fs.WriteAsync(b2.AsMemory(0, n), cts.Token); got += n; onProgress(Path.GetFileName(path), got, total);
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

    static Task Reply(Stream s, int code, string type, string body) => Reply(s, code, type, Encoding.UTF8.GetBytes(body));
    static async Task Reply(Stream s, int code, string type, byte[] b)
    {
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} OK\r\nContent-Type: {type}\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head); await s.WriteAsync(b); await s.FlushAsync();
    }

    const string Page = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="theme-color" content="#050816"><title>MoveIt</title><style>
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
html,body{height:100%;margin:0}
body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif;color:#fff;background:#050816;display:flex;align-items:center;justify-content:center;padding:20px;overflow-x:hidden}
.bg{position:fixed;inset:0;overflow:hidden;z-index:0}
.bg i{position:absolute;border-radius:50%;filter:blur(70px);opacity:.55;animation:drift 14s ease-in-out infinite alternate}
.b1{width:340px;height:340px;background:#1e6bff;top:-90px;left:-90px}.b2{width:300px;height:300px;background:#00d4ff;bottom:5%;right:-110px;animation-duration:18s}.b3{width:320px;height:320px;background:#7a4dff;bottom:-110px;left:12%;animation-duration:22s}
@keyframes drift{to{transform:translate(40px,30px) scale(1.1)}}
.card{position:relative;z-index:1;width:100%;max-width:440px;padding:26px 22px 22px;border-radius:32px;background:linear-gradient(145deg,rgba(255,255,255,.14),rgba(255,255,255,.05));border:1px solid rgba(255,255,255,.22);box-shadow:0 20px 60px rgba(0,0,0,.4),inset 0 1px 0 rgba(255,255,255,.3);backdrop-filter:blur(28px) saturate(160%);-webkit-backdrop-filter:blur(28px) saturate(160%);text-align:center}
.logo{width:64px;height:64px;border-radius:18px;box-shadow:0 8px 28px rgba(47,155,255,.5)}
h1{margin:10px 0 2px;font-size:26px;letter-spacing:.3px}.sub{margin:0;opacity:.75;font-size:15px}.sub b{opacity:1}
.stage{position:relative;width:220px;height:220px;margin:14px auto 6px;display:flex;align-items:center;justify-content:center}
.ring{position:absolute;inset:0;border-radius:50%;border:2px solid #38bdf8;background:rgba(47,128,237,.1);opacity:0;transform:scale(.45)}
.busy .ring{animation:ripple 2s ease-out infinite}.busy .r2{animation-delay:.66s}.busy .r3{animation-delay:1.33s}
@keyframes ripple{0%{transform:scale(.45);opacity:.85}100%{transform:scale(1.05);opacity:0}}
.prog{position:absolute;width:150px;height:150px;transform:rotate(-90deg);opacity:0;transition:opacity .3s}.busy .prog,.done .prog{opacity:1}
.trk{fill:none;stroke:rgba(255,255,255,.15);stroke-width:7}.arc{fill:none;stroke:url(#g);stroke-width:7;stroke-linecap:round;transition:stroke-dashoffset .25s}
.core{position:relative;width:108px;height:108px;border-radius:50%;display:flex;align-items:center;justify-content:center;background:linear-gradient(135deg,#2f80ed,#56ccf2);box-shadow:0 10px 34px rgba(47,128,237,.55);transition:background .4s,transform .4s}
.core svg{width:46px;height:46px;stroke:#fff;fill:none;stroke-width:2.4;stroke-linecap:round;stroke-linejoin:round}
.core .ck{display:none}.done .core{background:linear-gradient(135deg,#11998e,#38ef7d);animation:pop .5s cubic-bezier(.3,1.6,.5,1)}.done .core .up{display:none}.done .core .ck{display:block}
@keyframes pop{0%{transform:scale(.6)}100%{transform:scale(1)}}
.pct{position:absolute;font-size:26px;font-weight:700;display:none}.busy .pct{display:block}.busy .core svg.up{display:none}.busy .core{background:rgba(5,8,22,.55);box-shadow:none}
.chip{position:absolute;width:30px;height:38px;border-radius:8px;background:rgba(255,255,255,.92);opacity:0;left:calc(50% - 15px);top:calc(50% - 19px)}
.busy .chip{animation:fly 1.8s ease-in-out infinite}.busy .c2{animation-delay:.6s}.busy .c3{animation-delay:1.2s}
@keyframes fly{0%{transform:translateY(0) scale(1);opacity:0}20%{opacity:1}75%{opacity:1}100%{transform:translateY(-118px) scale(.4);opacity:0}}
.stat{min-height:40px;font-size:14px;opacity:.85;line-height:1.5;margin-bottom:12px}
.btn{display:block;width:100%;padding:17px;border:0;border-radius:20px;font-size:17px;font-weight:600;color:#fff;background:linear-gradient(135deg,#2f80ed,#56ccf2);box-shadow:0 8px 26px rgba(47,128,237,.45);cursor:pointer;transition:transform .15s}.btn:active{transform:scale(.97)}
input[type=file]{display:none}
input[type=text]{width:100%;padding:14px;border-radius:16px;border:1px solid rgba(255,255,255,.3);background:rgba(255,255,255,.1);color:#fff;font-size:20px;text-align:center;margin-bottom:12px;letter-spacing:6px}
.list{margin-top:14px;max-height:200px;overflow:auto;text-align:left}.f{padding:10px 12px;border-radius:14px;background:rgba(255,255,255,.07);margin-bottom:8px;font-size:14px}
.f span{display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.bar{height:5px;border-radius:3px;background:rgba(255,255,255,.15);margin-top:6px}.bar i{display:block;height:100%;width:0;border-radius:3px;background:linear-gradient(90deg,#2f80ed,#38ef7d);transition:width .2s}
</style></head><body>
<div class="bg"><i class="b1"></i><i class="b2"></i><i class="b3"></i></div>
<main class="card">
<img class="logo" src="/logo.png" alt="" onerror="this.style.display='none'">
<h1>MoveIt</h1><p class="sub">Send files to <b>%%NAME%%</b></p>
<div class="stage" id="stage">
<i class="ring"></i><i class="ring r2"></i><i class="ring r3"></i>
<svg class="prog" viewBox="0 0 120 120"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#2f80ed"/><stop offset="1" stop-color="#38ef7d"/></linearGradient></defs><circle class="trk" cx="60" cy="60" r="52"/><circle id="arc" class="arc" cx="60" cy="60" r="52" stroke-dasharray="326.7" stroke-dashoffset="326.7"/></svg>
<div class="core"><svg class="up" viewBox="0 0 24 24"><path d="M12 19V5M5 12l7-7 7 7"/></svg><svg class="ck" viewBox="0 0 24 24"><path d="M5 13l4 4 10-10"/></svg></div>
<span class="pct" id="pct">0%</span>
<i class="chip"></i><i class="chip c2"></i><i class="chip c3"></i>
</div>
<div class="stat" id="stat">Pick one or many files to send</div>
<input id="code" type="text" inputmode="numeric" maxlength="6" placeholder="Pairing code" style="display:none">
<label class="btn" for="pick" id="btn">Choose files</label><input id="pick" type="file" multiple>
<div class="list" id="list"></div>
</main>
<script>
const $=id=>document.getElementById(id);const q=new URLSearchParams(location.search);let code=q.get('c')||'';
if(!code){$('code').style.display='block';$('code').oninput=e=>code=e.target.value}
const fmt=n=>{const u=['B','KB','MB','GB'];let i=0;while(n>=1024&&i<3){n/=1024;i++}return n.toFixed(i?1:0)+' '+u[i]};
function up(f,row,onp){return new Promise(r=>{const x=new XMLHttpRequest();x.open('POST','/up');x.setRequestHeader('X-Code',code);x.setRequestHeader('X-Name',encodeURIComponent(f.name));
x.upload.onprogress=p=>{row.querySelector('i').style.width=(100*p.loaded/Math.max(p.total,1))+'%';onp(p.loaded)};
x.onload=()=>{const ok=x.status==200;row.querySelector('span').textContent=(ok?'✓ ':'✗ ')+f.name;if(ok)row.querySelector('i').style.width='100%';r(ok)};x.onerror=()=>{row.querySelector('span').textContent='✗ '+f.name;r(false)};x.send(f)})}
$('pick').onchange=async e=>{const fs=[...e.target.files];if(!fs.length)return;
 const st=$('stage');st.className='stage busy';$('list').innerHTML='';
 const total=fs.reduce((a,f)=>a+f.size,0)||1;let sent=0,okc=0;const t0=Date.now();
 const rows=fs.map(f=>{const d=document.createElement('div');d.className='f';d.innerHTML='<span></span><div class="bar"><i></i></div>';d.firstChild.textContent=f.name;$('list').appendChild(d);return d});
 const set=(done,i)=>{const p=Math.min(100,100*done/total);$('arc').style.strokeDashoffset=326.7*(1-p/100);$('pct').textContent=Math.round(p)+'%';
  const sp=done/Math.max((Date.now()-t0)/1000,.3);$('stat').textContent='File '+(i+1)+' of '+fs.length+' • '+fmt(done)+' of '+fmt(total)+' • '+fmt(sp)+'/s'};
 set(0,0);
 for(let i=0;i<fs.length;i++){if(await up(fs[i],rows[i],l=>set(sent+l,i)))okc++;sent+=fs[i].size;set(sent,Math.min(i,fs.length-1))}
 st.className='stage done';$('arc').style.strokeDashoffset=0;
 $('stat').textContent=okc==fs.length?'✓ Sent '+fs.length+(fs.length>1?' files':' file')+' • '+fmt(total):'Sent '+okc+' of '+fs.length+'. Check the pairing code and try again.';
 $('btn').textContent='Send more files';e.target.value=''};
</script></body></html>
""";
}
