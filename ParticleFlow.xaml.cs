using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WColor = Windows.UI.Color;

namespace MoveIt;

// Immersive transfer light, drawn with Composition radial gradients (smooth, GPU):
//   - a multi-colour aura hangs from the top edge and drifts slowly
//   - soft glowing particles fly in from the whole top edge (left / centre / right) towards the device icon and vanish into it
//   Connect()  device connected -> flash + a quick burst of particles, then a gentle steady stream
//   Complete() all received     -> green aura, check badge
//   Error()    cancelled/failed -> RED wash over the stage + red badge (also used by Dim())
public sealed partial class ParticleFlow : UserControl
{
    enum Mode { Off, Idle, Active, Complete, Error }
    enum Pal { Blue = 0, Green = 1, Red = 2 }

    sealed class Dot { public SpriteVisual V = null!; public double Age, Life, X0, Y0, Tx, Ty, Size, Wob, Ph, Strength; public bool Alive; }
    sealed class AuraSet { public ContainerVisual Root = null!; public SpriteVisual[] Blobs = null!; public double Weight; }

    // aura layout: peak, anchor x/y (fractions of w/h), size x/y (fractions)
    static readonly (double Peak, double Ax, double Ay, double Sx, double Sy)[] Defs =
    {
        (0.90, 0.25, -0.02, 1.30, 0.95), (0.70, 0.75, -0.02, 1.25, 0.85), (0.80, 0.50, 0.00, 1.00, 0.60), (0.45, 0.90, 0.06, 0.80, 0.50),
        (0.85, 0.50, -0.03, 0.70, 0.22)   // white hot-spot on the top edge
    };
    static readonly (byte R, byte G, byte B)[][] AuraCols =
    {
        new (byte, byte, byte)[] { (59, 130, 246), (139, 92, 246), (34, 211, 238), (236, 72, 153), (235, 245, 255) },
        new (byte, byte, byte)[] { (16, 185, 129), (45, 212, 191), (52, 211, 153), (134, 239, 172), (236, 253, 245) },
        new (byte, byte, byte)[] { (239, 68, 68), (249, 115, 22), (244, 63, 94), (251, 146, 60), (255, 236, 236) }
    };
    static readonly (byte R, byte G, byte B)[][] DotCols =
    {
        new (byte, byte, byte)[] { (125, 211, 252), (96, 165, 250), (167, 139, 250), (103, 232, 249) },
        new (byte, byte, byte)[] { (52, 211, 153), (110, 231, 183), (125, 211, 252), (167, 243, 208) },
        new (byte, byte, byte)[] { (248, 113, 113), (251, 146, 60), (244, 63, 94), (252, 165, 165) }
    };

    readonly Compositor comp;
    readonly ContainerVisual root;
    readonly CompositionRoundedRectangleGeometry clipGeo;
    readonly SpriteVisual wash, badgeGlow;
    readonly List<Dot> pool = new();
    AuraSet? cur, prev;
    Pal pal = Pal.Blue;

    readonly Random rnd = new(); readonly Stopwatch clock = Stopwatch.StartNew();
    Mode mode = Mode.Off;
    double modeT, last, w = 1, h = 1, cx, cy;
    double glow, glowTarget, dim = 1, dimTarget = 1, flash, hue, interval = 0.14, spawnIn, burstIn, washT, washTarget, kick; int burst;
    bool hooked; Storyboard? badgeSb;

    // where the badge sits, as a fraction of the control's height
    double centerFraction = 0.42;
    public double CenterFraction { get => centerFraction; set { centerFraction = value; UpdateCenter(); } }

    void UpdateCenter()
    {
        cx = w / 2; cy = h * centerFraction;
        DevIcon.Margin = new Thickness(0, cy - 76, 0, 0);
        Badge.Margin = new Thickness(cx + 54 - 22, cy + 54 - 22, 0, 0);   // lower-right of the icon
    }

    // ---- device icon ----
    public event Action<bool>? IconHover;   // true = pointer over the icon
    public event Action? IconTapped;        // touch / click on the icon
    void Dev_PointerEntered(object s, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => IconHover?.Invoke(true);
    void Dev_PointerExited(object s, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => IconHover?.Invoke(false);
    void Dev_Tapped(object s, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => IconTapped?.Invoke();

    // "Phone" / "Tablet" / "Laptop" ... -> matching icon. Anything unknown = phone.
    public void SetDevice(string type)
    {
        var t = (type ?? "").ToLowerInvariant();
        DevGlyph.Glyph = t.Contains("tablet") && !t.Contains("phone") ? "\uE70A"
                       : t.Contains("laptop") || t.Contains("pc") || t.Contains("computer") ? "\uE7F8"
                       : "\uE8EA";
    }

    public void SetProgress(double pct) => DevRing.Value = Math.Clamp(pct, 0, 100);

    void ShowIcon()
    {
        if (DevIcon.Visibility == Visibility.Visible && DevIcon.Opacity > 0.5) return;
        DevIcon.Visibility = Visibility.Visible; DevIcon.Opacity = 1;   // plain fade-in (Fx.Fade), no bounce
    }

    void HideIcon() { DevIcon.Opacity = 0; DevIcon.Visibility = Visibility.Collapsed; DevRing.Value = 0; }

    // ---- aura ----
    AuraSet BuildAura(Pal p, double weight)
    {
        var set = new AuraSet { Root = comp.CreateContainerVisual(), Weight = weight, Blobs = new SpriteVisual[Defs.Length] };
        for (int i = 0; i < Defs.Length; i++)
        {
            var c = AuraCols[(int)p][i];
            set.Blobs[i] = Fx.Blob(comp, WColor.FromArgb(255, c.R, c.G, c.B), Defs[i].Peak);
            set.Root.Children.InsertAtTop(set.Blobs[i]);
        }
        root.Children.InsertAbove(set.Root, wash);
        set.Root.Opacity = (float)weight;
        PlaceAura(set, clock.Elapsed.TotalSeconds);
        return set;
    }

    void PlaceAura(AuraSet s, double now)
    {
        for (int i = 0; i < s.Blobs.Length; i++)
        {
            var d = Defs[i]; var v = s.Blobs[i];
            float sw = (float)(d.Sx * w), sh = (float)(d.Sy * h);
            double ph = i * 1.7;
            float x = (float)(d.Ax * w + Math.Sin(now * 0.45 + ph) * w * 0.06 - sw / 2);
            float y = (float)(d.Ay * h + Math.Cos(now * 0.38 + ph) * h * 0.02 - sh / 2);
            v.Size = new Vector2(sw, sh); v.Offset = new Vector3(x, y, 0);
        }
    }

    void SetPal(Pal p, bool instant)
    {
        if (p == pal && cur != null) return;
        pal = p;
        var c = AuraCols[(int)p][2];
        Fx.Recolor(badgeGlow, WColor.FromArgb(255, c.R, c.G, c.B), 0.55);
        DevRing.Foreground = new SolidColorBrush(WColor.FromArgb(255, AuraCols[(int)p][2].R, AuraCols[(int)p][2].G, AuraCols[(int)p][2].B));
        if (prev != null) { root.Children.Remove(prev.Root); prev = null; }
        if (cur != null) { if (instant) root.Children.Remove(cur.Root); else prev = cur; }
        cur = BuildAura(p, instant ? 1 : 0);
    }

    // ---- public states ----
    public void Start()
    {
        HideBadge(); HideIcon(); KillDots(); SetPal(Pal.Blue, true);
        SetMode(Mode.Idle); interval = 0.14; spawnIn = 0.3; burst = 0; flash = 0; washT = washTarget = 0; wash.Opacity = 0;
        glowTarget = 0.2; dimTarget = 1; Hook();
    }
    public void Connect()
    {
        ShowIcon(); SetPal(Pal.Blue, false); SetMode(Mode.Active); interval = 0.14; spawnIn = 0.7;
        burst = 14; burstIn = 0; flash = 1; glowTarget = 0.6; dimTarget = 1; washTarget = 0; PlayBadge(0); Hook();
    }
    public void Complete()
    {
        ShowIcon(); DevRing.Value = 100; SetPal(Pal.Green, false); SetMode(Mode.Complete);
        burst = 0; burstIn = 0; flash = 1; glowTarget = 0.9; dimTarget = 1; washTarget = 0; PlayBadge(1); Hook();
    }
    public void Error()
    {
        ShowIcon(); SetPal(Pal.Red, false); SetMode(Mode.Error);
        burst = 0; burstIn = 0; flash = 1; glowTarget = 0.6; dimTarget = 1; washTarget = 1; PlayBadge(2); Hook();
    }
    public void Dim() => Error();   // old name: cancelled / failed
    public void Stop()
    {
        SetMode(Mode.Off); HideBadge(); HideIcon(); Unhook(); KillDots();
        badgeGlow.Opacity = 0; wash.Opacity = 0; if (cur != null) cur.Root.Opacity = 0;
        glow = glowTarget = 0; burst = 0; flash = 0; washT = washTarget = 0;
    }

    void SetMode(Mode m) { mode = m; modeT = 0; }
    void KillDots() { foreach (var d in pool) { d.Alive = false; d.V.IsVisible = false; } }

    // ---- layout ----
    void Host_SizeChanged(object s, SizeChangedEventArgs e)
    {
        w = Math.Max(1, e.NewSize.Width); h = Math.Max(1, e.NewSize.Height);
        clipGeo.Size = new Vector2((float)w, (float)h);
        wash.Size = new Vector2((float)(w * 1.8), (float)(h * 1.6)); wash.Offset = new Vector3((float)(-w * 0.4), (float)(-h * 0.3), 0);
        UpdateCenter();
        var now = clock.Elapsed.TotalSeconds;
        if (cur != null) PlaceAura(cur, now); if (prev != null) PlaceAura(prev, now);
    }

    // ---- particles ----
    static (byte R, byte G, byte B) Mix((byte R, byte G, byte B)[] p, double u)
    {
        u -= Math.Floor(u); double f = u * p.Length; int i = (int)f; var a = p[i % p.Length]; var b = p[(i + 1) % p.Length]; double k = f - i;
        return ((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
    }

    // one particle enters from somewhere along the top edge (left / centre / right) and flies into the device icon
    void Spawn(double strength)
    {
        Dot? d = null; foreach (var x in pool) if (!x.Alive) { d = x; break; }
        if (d == null) return;
        double zone = rnd.NextDouble();
        double fx = zone < 0.3 ? rnd.NextDouble() * 0.28 : zone < 0.6 ? 0.72 + rnd.NextDouble() * 0.28 : 0.3 + rnd.NextDouble() * 0.4;
        d.X0 = fx * w; d.Y0 = -6 + rnd.NextDouble() * 22;
        double ang = rnd.NextDouble() * Math.PI * 2, rad = rnd.NextDouble() * 22;
        d.Tx = cx + Math.Cos(ang) * rad; d.Ty = cy + Math.Sin(ang) * rad;
        d.Size = 14 + rnd.NextDouble() * 18; d.Wob = 8 + rnd.NextDouble() * 20; d.Ph = rnd.NextDouble() * 6.28;
        d.Age = 0; d.Life = 2.0 + rnd.NextDouble() * 1.2; d.Strength = strength; d.Alive = true;
        var c = Mix(DotCols[(int)pal], hue + rnd.NextDouble() * 0.12);
        Fx.Recolor(d.V, WColor.FromArgb(255, c.R, c.G, c.B), 0.95);
        d.V.Opacity = 0; d.V.IsVisible = true;
    }

    // ---- frame loop ----
    void Hook() { if (hooked) return; last = clock.Elapsed.TotalSeconds; CompositionTarget.Rendering += OnFrame; hooked = true; }
    void Unhook() { if (!hooked) return; CompositionTarget.Rendering -= OnFrame; hooked = false; }

    void OnFrame(object? sender, object e)
    {
        double now = clock.Elapsed.TotalSeconds, dt = Math.Min(now - last, 0.05); last = now;
        if (dt <= 0) return;
        modeT += dt; hue += dt * 0.05;
        glow += (glowTarget - glow) * Math.Min(1, dt * 2.5);
        dim += (dimTarget - dim) * Math.Min(1, dt * 3);
        flash = Math.Max(0, flash - dt * 1.0);
        washT += (washTarget - washT) * Math.Min(1, dt * 2.2);

        if (burst > 0) { burstIn -= dt; if (burstIn <= 0) { Spawn(1.0); burst--; burstIn = 0.05; } }
        else if (mode == Mode.Active)
        {
            spawnIn -= dt;
            if (spawnIn <= 0) { Spawn(0.7 + rnd.NextDouble() * 0.3); spawnIn = interval * (0.7 + rnd.NextDouble() * 0.6); }
        }

        // particles: accelerate towards the icon on a slightly curved path, shrink, and fade out as they are absorbed
        bool anyAlive = false; kick = Math.Max(0, kick - dt * 1.5);
        foreach (var d in pool)
        {
            if (!d.Alive) continue;
            d.Age += dt; double t = d.Age / d.Life;
            if (t >= 1) { d.Alive = false; d.V.IsVisible = false; kick = Math.Min(0.25, kick + 0.03); continue; }
            anyAlive = true;
            double ease = Math.Pow(t, 1.7), dx = d.Tx - d.X0, dy = d.Ty - d.Y0, len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            double wob = Math.Sin(d.Ph + t * 5) * d.Wob * (1 - t);
            double px = d.X0 + dx * ease - dy / len * wob, py = d.Y0 + dy * ease + dx / len * wob;
            double sz = d.Size * (1 - 0.6 * t);
            d.V.Size = new Vector2((float)sz, (float)sz); d.V.Offset = new Vector3((float)(px - sz / 2), (float)(py - sz / 2), 0);
            double fade = Math.Min(1, t / 0.12) * (t > 0.82 ? (1 - t) / 0.18 : 1);
            d.V.Opacity = (float)Math.Clamp(fade * d.Strength * dim, 0, 1);
        }

        // aura (cross-fades when the colour set changes)
        double breathe = 0.06 * Math.Sin(now * 1.8);
        double auraO = (mode == Mode.Error ? 0.6 : 0.3 + 0.5 * glow + breathe) + 0.5 * flash;
        if (cur != null)
        {
            cur.Weight = Math.Min(1, cur.Weight + dt * 2.2); PlaceAura(cur, now);
            cur.Root.Opacity = (float)Math.Clamp(auraO * cur.Weight * dim, 0, 1);
        }
        if (prev != null)
        {
            prev.Weight -= dt * 2.2; PlaceAura(prev, now);
            if (prev.Weight <= 0) { root.Children.Remove(prev.Root); prev = null; }
            else prev.Root.Opacity = (float)Math.Clamp(auraO * prev.Weight * dim, 0, 1);
        }
        wash.Opacity = (float)Math.Clamp(washT * (0.8 + 0.2 * Math.Sin(now * 2.0)), 0, 1);
        badgeGlow.Offset = new Vector3((float)(cx - 170), (float)(cy - 170), 0);
        badgeGlow.Opacity = (float)Math.Clamp((glow + kick + (mode == Mode.Active ? 0.06 * Math.Sin(now * 2.2) : 0)) * dim, 0, 1);

        // settled: stop using CPU (aura, check / red X stay)
        if (!anyAlive && burst == 0 && flash <= 0 && prev == null && modeT > 3.5 && (mode == Mode.Complete || mode == Mode.Error)) Unhook();
    }

    // ---- indicator: kind 0 = connected (fades), 1 = complete (stays), 2 = error / cancelled (stays) ----
    void PlayBadge(int kind)
    {
        badgeSb?.Stop();
        (byte, byte, byte) a = kind == 2 ? ((byte)220, (byte)38, (byte)38) : kind == 1 ? ((byte)16, (byte)185, (byte)129) : ((byte)47, (byte)128, (byte)237);
        (byte, byte, byte) b = kind == 2 ? ((byte)248, (byte)113, (byte)113) : kind == 1 ? ((byte)52, (byte)211, (byte)153) : ((byte)34, (byte)211, (byte)238);
        BadgeFill.Fill = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = WColor.FromArgb(255, a.Item1, a.Item2, a.Item3), Offset = 0 },
                new GradientStop { Color = WColor.FromArgb(255, b.Item1, b.Item2, b.Item3), Offset = 1 }
            }
        };
        BadgeIcon.Glyph = kind == 2 ? "\uE711" : "\uE73E";
        Badge.Visibility = Visibility.Visible;
        var sb = new Storyboard();
        double b0 = kind == 0 ? 0.3 : 0.5;
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var an = new DoubleAnimation { From = 0.4, To = 1, Duration = TimeSpan.FromSeconds(0.55), BeginTime = TimeSpan.FromSeconds(b0), EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(an, BadgeT); Storyboard.SetTargetProperty(an, prop); sb.Children.Add(an);
        }
        var o = new DoubleAnimationUsingKeyFrames();
        o.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
        o.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(b0), Value = 0 });
        o.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(b0 + 0.3), Value = 1 });
        if (kind == 0)
        {   // connection indicator: shown briefly, then hands over to the transfer view
            o.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(b0 + 1.2), Value = 1 });
            o.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(b0 + 1.7), Value = 0 });
        }
        Storyboard.SetTarget(o, Badge); Storyboard.SetTargetProperty(o, "Opacity"); sb.Children.Add(o);
        badgeSb = sb; sb.Begin();
    }

    void HideBadge() { badgeSb?.Stop(); badgeSb = null; Badge.Opacity = 0; Badge.Visibility = Visibility.Collapsed; }
}
