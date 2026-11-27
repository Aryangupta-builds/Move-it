using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MoveIt;

// Small web server so a phone/tablet (Android or iPhone) can scan the QR code and send files.
// Handshake: the page calls /hello with the pairing code; /status doubles as keep-alive + cancel signal;
// /up uploads one file; /cancel and /done close a batch. All endpoints require the pairing code.
public class WebShare
{
    public const int Port = 50507;

    sealed class Batch { public bool Cancelled, CancelRaised; public List<ReceivedFile> Files = new(); }

    readonly string code, dest, devName;
    readonly Action<RxEvent> raise; readonly Action<Prog> prog;
    readonly CancellationTokenSource cts = new();
    readonly object gate = new();
    readonly Dictionary<string, Batch> batches = new();
    readonly HashSet<CancellationTokenSource> uploads = new();
    string? curBatch; bool connected; string peerName = "", peerType = ""; long lastSeen;
    TcpListener? l;

    public WebShare(string code, string dest, string devName, Action<RxEvent> raise, Action<Prog> prog)
    { this.code = code; this.dest = dest; this.devName = devName; this.raise = raise; this.prog = prog; }

    public void Start() { l = new TcpListener(IPAddress.Any, Port); l.Start(); _ = Task.Run(Loop); _ = Task.Run(Watch); }
    public void Stop() { cts.Cancel(); CancelUploads(); try { l?.Stop(); } catch { } }

    // PC-side cancel: marks the running batch cancelled; the phone learns about it from its next /status poll.
    public void CancelTransfer()
    {
        bool raiseIt = false;
        lock (gate)
        {
            if (curBatch != null && batches.TryGetValue(curBatch, out var b)) { b.Cancelled = true; raiseIt = !b.CancelRaised; b.CancelRaised = true; }
        }
        CancelUploads();
        if (raiseIt) raise(new(RxState.Cancelled, "Transfer cancelled", peerName, peerType));
    }

    void CancelUploads()
    {
        CancellationTokenSource[] arr; lock (gate) arr = uploads.ToArray();
        foreach (var u in arr) { try { u.Cancel(); } catch { } }
    }

    async Task Loop()
    {
        while (!cts.IsCancellationRequested)
        {
            try { var c = await l!.AcceptTcpClientAsync(cts.Token); c.NoDelay = true; _ = Task.Run(() => Handle(c)); }
            catch { break; }
        }
    }

    // Phone stopped talking to us (closed the tab, left the WiFi, locked the screen for long)
    async Task Watch()
    {
        while (!cts.IsCancellationRequested)
        {
            try { await Task.Delay(2000, cts.Token); } catch { break; }
            bool drop = false;
            lock (gate) { if (connected && Environment.TickCount64 - lastSeen > 12000) { connected = false; drop = true; } }
            if (drop) raise(new(RxState.Disconnected, "The phone disconnected", peerName, peerType));
        }
    }

    static (string name, string type) Describe(string ua)
    {
        if (ua.Contains("iPad")) return ("iPad", "Tablet");
        if (ua.Contains("iPhone")) return ("iPhone", "Phone");
        if (ua.Contains("Android"))
        {
            var m = Regex.Match(ua, @"Android [^;]+; ([^;)]+)");
            var model = m.Success ? Regex.Replace(m.Groups[1].Value, @"\s*Build/.*$", "").Trim() : "";
            if (model.Length <= 1) model = "Android device";
            return (model, ua.Contains("Mobile") ? "Phone" : "Tablet");
        }
        return ("Device", "Phone / tablet");
    }

    void Touch(string ua)
    {
        bool first;
        lock (gate)
        {
            lastSeen = Environment.TickCount64; first = !connected;
            if (first) { connected = true; (peerName, peerType) = Describe(ua); }
        }
        if (first) raise(new(RxState.Connected, $"Connected to {peerName}", peerName, peerType));
    }

    bool CodeOk(string? c) => c != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(c), Encoding.UTF8.GetBytes(code));

    Batch GetBatch(string id, bool create = true)
    {
        lock (gate)
        {
            if (!batches.TryGetValue(id, out var b))
            {
                if (!create) return new Batch();
                if (batches.Count > 50) batches.Clear();
                b = new Batch(); batches[id] = b;
            }
            if (create) curBatch = id;
            return b;
        }
    }

    static string Q(string pathAndQuery, string key)
    {
        int i = pathAndQuery.IndexOf('?'); if (i < 0) return "";
        foreach (var kv in pathAndQuery[(i + 1)..].Split('&'))
        {
            int e = kv.IndexOf('=');
            if (e > 0 && kv[..e] == key) return Uri.UnescapeDataString(kv[(e + 1)..]);
        }
        return "";
    }

    static long Num(Dictionary<string, string> h, string k) => h.TryGetValue(k, out var v) && long.TryParse(v, out var n) && n >= 0 ? n : 0;

    async Task Handle(TcpClient c)
    {
        using (c)
        {
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
                if (first.Length < 2) return;
                var h = new Dictionary<string, string>();
                foreach (var ln in lines.Skip(1)) { int i = ln.IndexOf(':'); if (i > 0) h[ln[..i].Trim().ToLower()] = ln[(i + 1)..].Trim(); }
                var ua = h.GetValueOrDefault("user-agent", "");
                var route = first[1].Split('?')[0];

                if (first[0] == "GET")
                {
                    if (route == "/logo.png")
                    {
                        var lp = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
                        if (File.Exists(lp)) { await Reply(s, 200, "image/png", File.ReadAllBytes(lp)); return; }
                        await Reply(s, 404, "text/plain", "no logo"); return;
                    }
                    if (route == "/hello")
                    {
                        if (!CodeOk(Q(first[1], "c"))) { await Reply(s, 403, "application/json", "{\"ok\":false}"); return; }
                        Touch(ua);
                        await Reply(s, 200, "application/json", "{\"ok\":true,\"name\":" + System.Text.Json.JsonSerializer.Serialize(devName) + "}"); return;
                    }
                    if (route == "/status")
                    {
                        if (!CodeOk(Q(first[1], "c"))) { await Reply(s, 403, "application/json", "{\"ok\":false}"); return; }
                        Touch(ua);
                        bool cancel; var bid = Q(first[1], "b");
                        lock (gate) cancel = bid != "" && batches.TryGetValue(bid, out var b) && b.Cancelled;
                        await Reply(s, 200, "application/json", cancel ? "{\"ok\":true,\"cancel\":true}" : "{\"ok\":true,\"cancel\":false}"); return;
                    }
                    await Reply(s, 200, "text/html; charset=utf-8", Page.Replace("%%NAME%%", WebUtility.HtmlEncode(devName))); return;
                }

                if (first[0] != "POST" || !h.TryGetValue("x-code", out var cd) || !CodeOk(cd))
                { await Reply(s, 403, "text/plain", "Wrong code"); return; }
                var batchId = h.GetValueOrDefault("x-batch", "default");

                if (route == "/cancel")
                {
                    var b = GetBatch(batchId); bool raiseIt;
                    lock (gate) { b.Cancelled = true; raiseIt = !b.CancelRaised; b.CancelRaised = true; }
                    CancelUploads();
                    if (raiseIt) raise(new(RxState.Cancelled, "Cancelled from the phone", peerName, peerType));
                    await Reply(s, 200, "application/json", "{\"ok\":true}"); return;
                }
                if (route == "/done")
                {
                    var b = GetBatch(batchId, false); List<ReceivedFile> files;
                    lock (gate) files = b.Files.ToList();
                    int failed = (int)Num(h, "x-failed");
                    if (!b.Cancelled && files.Count + failed > 0)
                        raise(new(RxState.Complete, failed == 0 ? $"{files.Count} file(s) saved" : $"{files.Count} saved, {failed} failed", peerName, peerType, files.Count, files.Sum(f => f.Size), files));
                    await Reply(s, 200, "application/json", b.Cancelled ? "{\"ok\":true,\"cancel\":true}" : "{\"ok\":true}"); return;
                }
                if (route == "/up" && h.TryGetValue("x-name", out var nm)) { await Upload(s, buf, len, end, h, batchId, nm, ua); return; }
                await Reply(s, 404, "text/plain", "Not found");
            }
            catch { }
        }
    }

    async Task Upload(NetworkStream s, byte[] buf, int len, int end, Dictionary<string, string> h, string batchId, string nm, string ua)
    {
        var b = GetBatch(batchId);
        bool already; lock (gate) already = b.Cancelled;
        if (already) { await Reply(s, 409, "application/json", "{\"cancel\":true}"); return; }
        if (!h.TryGetValue("content-length", out var cl) || !long.TryParse(cl, out var total) || total < 0) { await Reply(s, 411, "text/plain", "Length required"); return; }
        long offset = Num(h, "x-offset"), batchTotal = Math.Max(Num(h, "x-batchtotal"), total + offset); int idx = (int)Num(h, "x-index"), count = (int)Num(h, "x-count");
        Touch(ua);

        string name, fin, part;
        try
        {
            name = Net.CleanSegment(Path.GetFileName(Uri.UnescapeDataString(nm)));
            var dir = Path.Combine(dest, "FromPhone"); Directory.CreateDirectory(dir);
            fin = Net.UniquePath(Path.Combine(dir, name)); part = fin + ".part";
        }
        catch (Exception e) { await Reply(s, 500, "text/plain", "Cannot save: " + e.Message); raise(new(RxState.Failed, "Cannot save the file: " + e.Message, peerName, peerType)); return; }

        using var up = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        lock (gate) uploads.Add(up);
        bool ok = false;
        try
        {
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                long got = 0, lastRaise = 0; int body = len - (end + 4);
                prog(new(offset, batchTotal, 0, name, idx, count));
                if (body > 0) { int w = (int)Math.Min(body, total); await fs.WriteAsync(buf.AsMemory(end + 4, w), up.Token); got = w; }
                var b2 = new byte[1 << 17];
                while (got < total)
                {
                    int n = await s.ReadAsync(b2.AsMemory(0, (int)Math.Min(b2.Length, total - got)), up.Token);
                    if (n == 0) throw new IOException("Phone disconnected during upload");
                    await fs.WriteAsync(b2.AsMemory(0, n), up.Token); got += n;
                    long now = Environment.TickCount64; lastSeen = now;
                    if (now - lastRaise > 150) { lastRaise = now; prog(new(offset + got, batchTotal, 0, name, idx, count)); }
                }
            }
            File.Move(part, fin); ok = true;
            lock (gate) b.Files.Add(new(Path.GetFileName(fin), total));
            prog(new(offset + total, batchTotal, 0, name, idx, count));
            await Reply(s, 200, "application/json", "{\"ok\":true}");
        }
        catch (Exception e)
        {
            if (ok) return;
            Net.TryDelete(part);
            bool cancelled; lock (gate) cancelled = b.Cancelled;
            if (!cancelled && !cts.IsCancellationRequested)
                raise(new(RxState.Failed, e is IOException ? "Connection to the phone was lost. The transfer was interrupted." : "Error: " + e.Message, peerName, peerType));
            try { await Reply(s, 409, "application/json", "{\"cancel\":true}"); } catch { }
        }
        finally { lock (gate) uploads.Remove(up); }
    }

    static int Find(byte[] b, int len)
    {
        for (int i = 0; i + 3 < len; i++) if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
        return -1;
    }

    static string Reason(int c) => c switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 411 => "Length Required", _ => "Error" };
    static Task Reply(Stream s, int code, string type, string body) => Reply(s, code, type, Encoding.UTF8.GetBytes(body));
    static async Task Reply(Stream s, int code, string type, byte[] b)
    {
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {Reason(code)}\r\nContent-Type: {type}\r\nContent-Length: {b.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head); await s.WriteAsync(b); await s.FlushAsync();
    }

    const string Page = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="theme-color" content="#060a18"><title>MoveIt</title><style>
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
:root{--bg:#060a18;--card:rgba(255,255,255,.06);--line:rgba(255,255,255,.12);--txt:#f4f7ff;--dim:rgba(244,247,255,.62);--acc:#38bdf8;--acc2:#2f80ed;--ok:#34d399;--bad:#f87171}
html,body{margin:0;min-height:100%}
body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif;color:var(--txt);background:var(--bg);min-height:100dvh;display:flex;justify-content:center;padding:max(16px,env(safe-area-inset-top)) 16px max(16px,env(safe-area-inset-bottom))}
.bg{position:fixed;inset:0;z-index:0;overflow:hidden;pointer-events:none}
.bg i{position:absolute;border-radius:50%;filter:blur(90px);opacity:.35;transition:background 1s}
.bg .glow{left:50%;top:-34vh;width:150vw;height:70vh;transform:translateX(-50%);background:radial-gradient(closest-side,var(--g1),var(--g2) 55%,transparent 100%);filter:blur(24px);opacity:.55;transition:opacity .6s}
body{--g1:rgba(56,189,248,.65);--g2:rgba(99,102,241,.3)}
@keyframes breathe{50%{opacity:.95;transform:translateX(-50%) scale(1.07)}}
body.st-busy .glow{animation:breathe 2.4s ease-in-out infinite}
body.st-done{--g1:rgba(52,211,153,.7);--g2:rgba(16,185,129,.28)}
body.st-done .b1{background:#059669}body.st-done .b2{background:#14b8a6}
body.st-bad{--g1:rgba(248,113,113,.8);--g2:rgba(220,38,38,.38)}
body.st-bad .b1{background:#dc2626}body.st-bad .b2{background:#f97316}
body.st-bad .card{border-color:rgba(248,113,113,.45);box-shadow:0 0 70px rgba(220,38,38,.2)}
.card{transition:border-color .5s,box-shadow .5s}
.b1{width:340px;height:340px;background:#1e6bff;top:-120px;left:-100px}.b2{width:300px;height:300px;background:#06b6d4;bottom:-100px;right:-100px}
main{position:relative;z-index:1;width:100%;max-width:520px;display:flex;flex-direction:column;gap:14px}
header{display:flex;align-items:center;gap:12px;padding:4px 2px}
.logo{width:40px;height:40px;border-radius:12px}
header h1{margin:0;font-size:20px;font-weight:650;letter-spacing:.2px;flex:1}
.chip{display:flex;align-items:center;gap:7px;padding:7px 12px;border-radius:999px;background:var(--card);border:1px solid var(--line);font-size:13px;color:var(--dim);white-space:nowrap}
.chip i{width:8px;height:8px;border-radius:50%;background:#94a3b8}
.chip.on{color:var(--txt)}.chip.on i{background:var(--ok);box-shadow:0 0 8px var(--ok)}
.chip.err i{background:var(--bad)}.chip.wait i{background:#fbbf24;animation:pulse 1.2s infinite}
@keyframes pulse{50%{opacity:.35}}
.card{border-radius:24px;background:var(--card);border:1px solid var(--line);padding:20px;backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px)}
.to{font-size:14px;color:var(--dim);text-align:center}.to b{color:var(--txt)}
.dial{position:relative;width:176px;height:176px;margin:14px auto 10px;display:flex;align-items:center;justify-content:center}
.dial svg{position:absolute;inset:0;width:100%;height:100%;transform:rotate(-90deg)}
.trk{fill:none;stroke:rgba(255,255,255,.1);stroke-width:7}.arc{fill:none;stroke:url(#g);stroke-width:7;stroke-linecap:round;stroke-dasharray:326.7;stroke-dashoffset:326.7;transition:stroke-dashoffset .3s linear}
.core{width:124px;height:124px;border-radius:50%;display:flex;align-items:center;justify-content:center;flex-direction:column;background:linear-gradient(135deg,var(--acc2),var(--acc));box-shadow:0 10px 30px rgba(47,128,237,.4);transition:background .3s}
.core svg{position:static;width:44px;height:44px;transform:none;stroke:#fff;fill:none;stroke-width:2.4;stroke-linecap:round;stroke-linejoin:round}
.core .pct{font-size:30px;font-weight:700;display:none}
.busy .core{background:rgba(6,10,24,.7);box-shadow:none}.busy .core svg{display:none}.busy .core .pct{display:block}
.done .core{background:linear-gradient(135deg,#059669,var(--ok))}.bad .core{background:linear-gradient(135deg,#b91c1c,var(--bad))}
.file{text-align:center;min-height:50px}.file .n{font-weight:600;font-size:16px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.file .m{font-size:13px;color:var(--dim);margin-top:3px}
.badge{display:inline-block;padding:1px 7px;border-radius:6px;background:rgba(56,189,248,.18);color:var(--acc);font-size:11px;font-weight:700;margin-right:6px;vertical-align:1px}
.stats{display:grid;grid-template-columns:repeat(3,1fr);gap:8px;margin-top:12px}
.stats div{background:rgba(255,255,255,.05);border-radius:14px;padding:9px 6px;text-align:center}
.stats b{display:block;font-size:15px}.stats span{font-size:11px;color:var(--dim)}
.msg{text-align:center;font-size:14px;color:var(--dim);margin-top:12px;min-height:20px;line-height:1.4}.msg.ok{color:var(--ok)}.msg.bad{color:var(--bad)}
.btn{display:block;width:100%;padding:16px;border:0;border-radius:18px;font-size:16px;font-weight:650;color:#fff;background:linear-gradient(135deg,var(--acc2),var(--acc));box-shadow:0 8px 24px rgba(47,128,237,.35);cursor:pointer;text-align:center;transition:transform .12s,opacity .2s;margin-top:14px}
.btn:active{transform:scale(.98)}.btn[disabled],.btn.off{opacity:.4;pointer-events:none}
.btn.ghost{background:transparent;border:1px solid rgba(248,113,113,.55);color:var(--bad);box-shadow:none}
input[type=file]{display:none}
input[type=text]{width:100%;padding:14px;border-radius:16px;border:1px solid var(--line);background:rgba(255,255,255,.06);color:#fff;font-size:22px;text-align:center;letter-spacing:8px;margin-top:12px}
.list{max-height:220px;overflow:auto}.f{padding:10px 12px;border-radius:14px;background:rgba(255,255,255,.05);margin-top:8px;font-size:14px}
.f span{display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.f small{color:var(--dim)}
.bar{height:4px;border-radius:2px;background:rgba(255,255,255,.12);margin-top:7px}.bar i{display:block;height:100%;width:0;border-radius:2px;background:linear-gradient(90deg,var(--acc2),var(--ok));transition:width .2s}
.hide{display:none!important}
.sheet{position:fixed;inset:0;z-index:5;background:rgba(0,0,0,.55);display:flex;align-items:flex-end;justify-content:center;padding:16px}
.sheet>div{width:100%;max-width:520px;background:#101733;border:1px solid var(--line);border-radius:24px;padding:22px}
.sheet h3{margin:0 0 6px;font-size:18px}.sheet p{margin:0;color:var(--dim);font-size:14px;line-height:1.45}
.row{display:flex;gap:10px}.row .btn{margin-top:16px;padding:14px}.btn.dim{background:rgba(255,255,255,.1);box-shadow:none}.btn.red{background:#dc2626;box-shadow:none}
@media(min-width:700px){body{align-items:center}}
@media(prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}}
</style></head><body>
<div class="bg"><i class="glow"></i><i class="b1"></i><i class="b2"></i></div>
<main>
<header><img class="logo" src="/logo.png" alt="" onerror="this.style.display='none'"><h1>MoveIt</h1><div class="chip wait" id="chip"><i></i><span id="chipT">Connecting…</span></div></header>
<section class="card" id="card">
<div class="to">Send to <b id="pcn">%%NAME%%</b></div>
<div class="dial" id="dial">
<svg viewBox="0 0 120 120"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#2f80ed"/><stop offset="1" stop-color="#34d399"/></linearGradient></defs><circle class="trk" cx="60" cy="60" r="52"/><circle id="arc" class="arc" cx="60" cy="60" r="52"/></svg>
<div class="core"><svg id="ic" viewBox="0 0 24 24"><path d="M12 19V5M5 12l7-7 7 7"/></svg><span class="pct" id="pct">0%</span></div>
</div>
<div class="file"><div class="n" id="fn">Ready to send</div><div class="m" id="fm">Choose one or many files</div></div>
<div class="stats hide" id="stats"><div><b id="sSp">–</b><span>Speed</span></div><div><b id="sEta">–</b><span>Time left</span></div><div><b id="sCnt">–</b><span>Files</span></div></div>
<div class="msg" id="msg"></div>
<input id="code" type="text" inputmode="numeric" maxlength="6" placeholder="Pairing code" class="hide">
<label class="btn off" for="pick" id="btn">Choose files</label><input id="pick" type="file" multiple>
<button class="btn ghost hide" id="cancel">Cancel transfer</button>
</section>
<section class="list" id="list"></section>
</main>
<div class="sheet hide" id="sheet"><div><h3>Cancel transfer?</h3><p>The file being sent right now will be discarded. Files that already arrived stay on the PC.</p><div class="row"><button class="btn dim" id="keep">Keep sending</button><button class="btn red" id="yes">Cancel transfer</button></div></div></div>
<script>
const $=id=>document.getElementById(id);
const q=new URLSearchParams(location.search);let code=q.get('c')||'';
const C=326.7;let batch='',xhr=null,busy=false,connected=false,cancelled=false,remoteCancel=false,lost=false,fails=0,poll=null,pcName=document.getElementById('pcn').textContent;
const fmt=n=>{const u=['B','KB','MB','GB','TB'];let i=0;while(n>=1024&&i<4){n/=1024;i++}return n.toFixed(i?1:0)+' '+u[i]};
const fmtT=s=>!isFinite(s)||s<0?'–':s<60?Math.ceil(s)+'s':s<3600?Math.floor(s/60)+'m '+Math.ceil(s%60)+'s':Math.floor(s/3600)+'h '+Math.floor(s%3600/60)+'m';
const ext=n=>{const i=n.lastIndexOf('.');return i>0&&i<n.length-1?n.slice(i+1,i+6).toUpperCase():'FILE'};
function chip(t,cls){$('chip').className='chip '+cls;$('chipT').textContent=t}
function msg(t,cls){$('msg').textContent=t;$('msg').className='msg '+(cls||'')}
function dial(cls){$('dial').className='dial '+(cls||'');document.body.className=cls?'st-'+cls:'';
 $('ic').innerHTML=cls=='bad'?'<path d="M6 6l12 12M18 6L6 18"/>':cls=='done'?'<path d="M5 12.5l4.5 4.5L19 7.5"/>':'<path d="M12 19V5M5 12l7-7 7 7"/>'}
function ring(p){$('arc').style.strokeDashoffset=C*(1-Math.min(100,p)/100);$('pct').textContent=Math.round(p)+'%'}
function idle(){busy=false;$('cancel').classList.add('hide');$('btn').classList.toggle('off',!connected);$('btn').classList.remove('hide')}
async function hello(){
 try{const r=await fetch('/hello?c='+encodeURIComponent(code),{cache:'no-store'});
  if(r.ok){connected=true;fails=0;chip('Connected to '+pcName,'on');msg('');idle();$('code').classList.add('hide');start();return}
  connected=false;chip('Wrong code','err');msg('That pairing code does not match. Check the code shown on the PC.','bad');$('code').classList.remove('hide');
 }catch(e){connected=false;chip('Not reachable','err');msg('Cannot reach the PC. Make sure both devices are on the same WiFi.','bad');setTimeout(hello,3000)}}
function start(){clearInterval(poll);poll=setInterval(async()=>{
 try{const r=await fetch('/status?c='+encodeURIComponent(code)+'&b='+batch,{cache:'no-store'});const j=await r.json();
  if(!r.ok){throw 0}
  if(fails>=3){chip('Connected to '+pcName,'on');connected=true;if(!busy)idle()}fails=0;
  if(j.cancel&&busy&&!cancelled){remoteCancel=true;doCancel(true)}
 }catch(e){if(++fails>=3){connected=false;chip('Connection lost','err');if(busy){lost=true;if(xhr)xhr.abort();msg('Connection lost. The transfer was interrupted.','bad')}else{$('btn').classList.add('off')}}}},1500)}
if(!code){$('code').classList.remove('hide');chip('Enter code','wait');msg('Enter the 6-digit pairing code shown on the PC.');$('code').oninput=e=>{code=e.target.value.trim();if(code.length==6)hello()}}else hello();
function up(f,i,n,off,tot,row,onp){return new Promise(r=>{const x=new XMLHttpRequest();xhr=x;x.open('POST','/up');
 [['X-Code',code],['X-Name',encodeURIComponent(f.name)],['X-Batch',batch],['X-Index',i+1],['X-Count',n],['X-Offset',off],['X-BatchTotal',tot]].forEach(([k,v])=>x.setRequestHeader(k,v));
 x.upload.onprogress=p=>{row.querySelector('i').style.width=(100*p.loaded/Math.max(p.total,1))+'%';onp(p.loaded)};
 x.onload=()=>r(x.status==200?'ok':x.status==409?'cancel':'fail');x.onerror=()=>r('fail');x.onabort=()=>r('cancel');x.send(f)})}
async function doCancel(fromPc){
 cancelled=true;if(!fromPc){try{await fetch('/cancel',{method:'POST',headers:{'X-Code':code,'X-Batch':batch}})}catch(e){}}
 if(xhr)xhr.abort()}
$('cancel').onclick=()=>$('sheet').classList.remove('hide');
$('keep').onclick=()=>$('sheet').classList.add('hide');
$('yes').onclick=()=>{$('sheet').classList.add('hide');if(busy)doCancel(false)};
$('pick').onchange=async e=>{const fs=[...e.target.files];e.target.value='';if(!fs.length||!connected||busy)return;
 busy=true;cancelled=false;remoteCancel=false;lost=false;batch=Date.now().toString(36)+Math.random().toString(36).slice(2,6);
 $('btn').classList.add('hide');$('cancel').classList.remove('hide');$('stats').classList.remove('hide');$('list').innerHTML='';dial('busy');ring(0);msg('Sending…');
 const total=fs.reduce((a,f)=>a+f.size,0)||1;let sent=0,okc=0,bad=0,consec=0,sp=0,lt=Date.now(),ld=0;const t0=Date.now();
 const rows=fs.map(f=>{const d=document.createElement('div');d.className='f';d.innerHTML='<span></span><small></small><div class="bar"><i></i></div>';d.firstChild.textContent=f.name;d.querySelector('small').textContent=ext(f.name)+' • '+fmt(f.size);$('list').appendChild(d);return d});
 const set=(done,i)=>{const p=100*done/total;ring(p);const now=Date.now();if(now-lt>400){const inst=(done-ld)/((now-lt)/1000);sp=sp?sp*.7+inst*.3:inst;lt=now;ld=done}
  const f=fs[Math.min(i,fs.length-1)];$('fn').textContent=f.name;$('fm').innerHTML='<span class="badge">'+ext(f.name)+'</span>'+fmt(f.size);
  $('sSp').textContent=fmt(sp||done/Math.max((now-t0)/1000,.3))+'/s';$('sEta').textContent=fmtT((total-done)/(sp||done/Math.max((now-t0)/1000,.3)));$('sCnt').textContent=(i+1)+' / '+fs.length};
 set(0,0);
 for(let i=0;i<fs.length;i++){
  const res=await up(fs[i],i,fs.length,sent,total,rows[i],l=>set(sent+l,i));
  if(res=='cancel'&&!cancelled&&!lost){cancelled=true;remoteCancel=true}
  if(res=='cancel'||cancelled){rows[i].querySelector('span').textContent='✗ '+fs[i].name;break}
  if(res=='ok'){okc++;consec=0;rows[i].querySelector('span').textContent='✓ '+fs[i].name;rows[i].querySelector('i').style.width='100%'}
  else{bad++;consec++;rows[i].querySelector('span').textContent='✗ '+fs[i].name;if(consec>=2&&!connected)break}
  sent+=fs[i].size;set(sent,i)}
 xhr=null;$('stats').classList.add('hide');
 if(!cancelled&&connected&&!lost){try{const rr=await fetch('/done',{method:'POST',headers:{'X-Code':code,'X-Batch':batch,'X-Failed':bad}});const jj=await rr.json();if(jj.cancel){cancelled=true;remoteCancel=true}}catch(e){}}
 if(cancelled){try{navigator.vibrate&&navigator.vibrate([70,50,70])}catch(e){}dial('bad');ring(0);$('fn').textContent='Cancelled';$('fm').textContent=okc?okc+' file(s) arrived before cancelling':'Nothing was transferred';msg(remoteCancel?'Cancelled by the PC':'Transfer cancelled','bad')}
 else if(!connected){dial('bad');$('fn').textContent='Interrupted';$('fm').textContent=okc+' of '+fs.length+' files arrived';msg('Connection lost. Reconnect and send again.','bad')}
 else{
  if(bad==0){dial('done');ring(100);$('fn').textContent='Transfer complete';$('fm').textContent=fs.length+(fs.length>1?' files':' file')+' • '+fmt(total);msg('Sent to '+pcName,'ok')}
  else{dial('bad');$('fn').textContent='Finished with errors';$('fm').textContent=okc+' of '+fs.length+' sent';msg(bad+' file(s) failed. Please try those again.','bad')}}
 $('btn').textContent='Send more files';idle()};
</script>
</body></html>
""";
}
