using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using WColor = Windows.UI.Color;

namespace MoveIt;

// iOS 17 AirDrop / NameDrop style effect (replaces the old falling particles; same public API).
//   - a soft light aura hangs from the top edge (the point where the two devices "touch")
//   - liquid ripples keep rolling out of that point and fade as they spread over the screen
//   - Connect() = bright flash + 3 quick ripples, then a steady rhythm while receiving
//   - Complete() = one more big burst, light turns green, the check stays
//   - Dim() = cancelled / failed: ripples stop, light fades out
// The frame loop only runs while something is moving.
public sealed partial class ParticleFlow : UserControl
{
    enum Mode { Off, Idle, Active, Complete, Dimmed }

    sealed class Ripple
    {
        public Grid El = null!; public CompositeTransform T = null!;
        public SolidColorBrush Fill = null!, Soft = null!, Mid = null!, Core = null!;
        public double Age, Life, Strength, Wobble; public bool Alive;
    }

    // colours (R,G,B): ripple palette cycles slowly through these (white-blue to soft violet, like the iOS glow)
    static readonly (byte R, byte G, byte B)[] Pal = { (125, 211, 252), (96, 165, 250), (167, 139, 250), (103, 232, 249) };
    static readonly (byte R, byte G, byte B)[] PalGood = { (52, 211, 153), (110, 231, 183), (125, 211, 252), (167, 243, 208) };
    static readonly byte[] AuraA = { 10, 12, 16, 22, 30, 42, 60 };
    static readonly (byte R, byte G, byte B)[] AuraCool = { (56, 189, 248), (66, 170, 250), (96, 165, 250), (125, 190, 252), (150, 210, 253), (186, 230, 253), (240, 249, 255) };
    static readonly (byte R, byte G, byte B)[] AuraGood = { (16, 185, 129), (34, 197, 142), (52, 211, 153), (80, 222, 170), (110, 231, 183), (167, 243, 208), (236, 253, 245) };

    readonly List<Ripple> pool = new();
    readonly Ellipse[] auraEl = new Ellipse[7]; readonly SolidColorBrush[] auraB = new SolidColorBrush[7];
    readonly Random rnd = new(); readonly Stopwatch clock = Stopwatch.StartNew();
    Mode mode = Mode.Off;
    double modeT, last, w = 1, h = 1, cx, cy;
    double glow, glowTarget, dim = 1, dimTarget = 1, flash, hue, interval = 2, spawnIn, burstIn; int burst;
    bool hooked, good; Storyboard? badgeSb;

    // where the connect badge sits, as a fraction of the control's height
    double centerFraction = 0.42;
    public double CenterFraction { get => centerFraction; set { centerFraction = value; UpdateCenter(); } }

    void UpdateCenter()
    {
        cx = w / 2; cy = h * centerFraction;
        Glow.Margin = new Thickness(0, cy - 170, 0, 0); Badge.Margin = new Thickness(0, cy - 46, 0, 0);
    }

    public ParticleFlow()
    {
        InitializeComponent();
        for (int i = 0; i < auraEl.Length; i++)
        {
            auraB[i] = new SolidColorBrush();
            auraEl[i] = new Ellipse { Fill = auraB[i], IsHitTestVisible = false };
            Aura.Children.Add(auraEl[i]);
        }
        TintAura(false);
        for (int i = 0; i < 8; i++) pool.Add(NewRipple());
        Unloaded += (_, _) => Unhook();
    }

    Ripple NewRipple()
    {
        var r = new Ripple { Fill = new SolidColorBrush(), Soft = new SolidColorBrush(), Mid = new SolidColorBrush(), Core = new SolidColorBrush(), T = new CompositeTransform() };
        // three strokes of different width stacked = feathered edge (no blur effect needed)
        Ellipse E(double thick, SolidColorBrush stroke, SolidColorBrush? fill) => new Ellipse { Width = 400, Height = 400, StrokeThickness = thick, Stroke = stroke, Fill = fill, IsHitTestVisible = false };
        r.El = new Grid
        {
            Width = 400, Height = 400, Opacity = 0, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            RenderTransform = r.T, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5)
        };
        r.El.Children.Add(E(26, r.Soft, r.Fill));
        r.El.Children.Add(E(9, r.Mid, null));
        r.El.Children.Add(E(1.6, r.Core, null));
        Field.Children.Add(r.El);
        return r;
    }

    void TintAura(bool good)
    {
        this.good = good;
        var set = good ? AuraGood : AuraCool;
        for (int i = 0; i < auraB.Length; i++) auraB[i].Color = WColor.FromArgb(AuraA[i], set[i].R, set[i].G, set[i].B);
    }

    // ---- public states ----
    public void Start()
    {
        HideBadge(); KillRipples(); TintAura(false);
        SetMode(Mode.Idle); interval = 2.0; spawnIn = 0.3; burst = 0; flash = 0;
        glowTarget = 0.2; dimTarget = 1; Hook();
    }
    public void Connect()
    {
        TintAura(false); SetMode(Mode.Active); interval = 0.85; spawnIn = 0.9;
        burst = 3; burstIn = 0; flash = 1; glowTarget = 0.6; dimTarget = 1; PlayBadge(false); Hook();
    }
    public void Complete()
    {
        TintAura(true); SetMode(Mode.Complete);
        burst = 3; burstIn = 0; flash = 1; glowTarget = 0.9; dimTarget = 1; PlayBadge(true); Hook();
    }
    public void Dim()
    {
        HideBadge(); if (mode == Mode.Off) return;
        SetMode(Mode.Dimmed); burst = 0; glowTarget = 0; dimTarget = 0.3; Hook();
    }
    public void Stop()
    {
        SetMode(Mode.Off); HideBadge(); Unhook(); KillRipples();
        Glow.Opacity = 0; Aura.Opacity = 0; glow = glowTarget = 0; burst = 0; flash = 0;
    }

    void SetMode(Mode m) { mode = m; modeT = 0; }

    void KillRipples() { foreach (var r in pool) { r.Alive = false; r.El.Visibility = Visibility.Collapsed; r.El.Opacity = 0; } }

    // ---- layout ----
    void Host_SizeChanged(object s, SizeChangedEventArgs e)
    {
        w = Math.Max(1, e.NewSize.Width); h = Math.Max(1, e.NewSize.Height);
        Host.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, w, h) };
        UpdateCenter();
        Aura.Width = w; Aura.Height = h;
        for (int i = 0; i < auraEl.Length; i++)
        {
            double k = 1 - i * 0.13, W = w * 1.5 * k, H = h * 0.95 * k;
            auraEl[i].Width = W; auraEl[i].Height = H;
            Canvas.SetLeft(auraEl[i], (w - W) / 2); Canvas.SetTop(auraEl[i], -h * 0.03 - H / 2);   // centre sits on the top edge, only the lower half is visible
        }
    }

    // ---- ripples ----
    static (byte R, byte G, byte B) Mix((byte R, byte G, byte B)[] p, double u)
    {
        u -= Math.Floor(u); double f = u * p.Length; int i = (int)f; var a = p[i % p.Length]; var b = p[(i + 1) % p.Length]; double k = f - i;
        return ((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
    }

    void Spawn(double strength)
    {
        Ripple? r = null; foreach (var x in pool) if (!x.Alive) { r = x; break; }
        if (r == null) return;
        var c = Mix(good ? PalGood : Pal, hue + rnd.NextDouble() * 0.08);
        r.Fill.Color = WColor.FromArgb(14, c.R, c.G, c.B);
        r.Soft.Color = WColor.FromArgb(70, c.R, c.G, c.B);
        r.Mid.Color = WColor.FromArgb(130, c.R, c.G, c.B);
        r.Core.Color = WColor.FromArgb(235, 235, 248, 255);
        r.Age = 0; r.Life = 2.6 + rnd.NextDouble() * 0.7; r.Strength = strength; r.Wobble = rnd.NextDouble() * 6.28; r.Alive = true;
        r.El.Visibility = Visibility.Visible; r.El.Opacity = 0;
    }

    // ---- frame loop ----
    void Hook() { if (hooked) return; last = clock.Elapsed.TotalSeconds; CompositionTarget.Rendering += OnFrame; hooked = true; }
    void Unhook() { if (!hooked) return; CompositionTarget.Rendering -= OnFrame; hooked = false; }

    void OnFrame(object? sender, object e)
    {
        double now = clock.Elapsed.TotalSeconds, dt = Math.Min(now - last, 0.05); last = now;
        if (dt <= 0) return;
        modeT += dt; hue += dt * 0.06;
        glow += (glowTarget - glow) * Math.Min(1, dt * 2.5);
        dim += (dimTarget - dim) * Math.Min(1, dt * 3);
        flash = Math.Max(0, flash - dt * 1.1);

        // spawn: quick burst on connect / complete, then a steady rhythm
        if (burst > 0) { burstIn -= dt; if (burstIn <= 0) { Spawn(1.0); burst--; burstIn = 0.22; } }
        else if (mode == Mode.Idle || mode == Mode.Active)
        {
            spawnIn -= dt;
            if (spawnIn <= 0) { Spawn(mode == Mode.Active ? 0.8 + rnd.NextDouble() * 0.2 : 0.5); spawnIn = interval * (0.9 + rnd.NextDouble() * 0.2); }
        }

        // ripples: expand from the top-centre like a pressure wave (fast start, slows down) and fade
        double maxR = Math.Max(h * 1.05, w * 0.6), oy = -h * 0.02; bool anyAlive = false;
        foreach (var r in pool)
        {
            if (!r.Alive) continue;
            r.Age += dt; double t = r.Age / r.Life;
            if (t >= 1) { r.Alive = false; r.El.Visibility = Visibility.Collapsed; r.El.Opacity = 0; continue; }
            anyAlive = true;
            double ease = 1 - Math.Pow(1 - t, 2.2), s = (30 + (maxR - 30) * ease) / 200, wob = 1 + 0.025 * Math.Sin(now * 3 + r.Wobble);
            r.T.ScaleX = s * 1.08 * wob; r.T.ScaleY = s * 0.92 / wob;   // slightly wider than tall = liquid look
            Canvas.SetLeft(r.El, cx - 200); Canvas.SetTop(r.El, oy - 200);
            r.El.Opacity = Math.Clamp(Math.Pow(1 - t, 1.6) * Math.Min(1, t * 14) * r.Strength * dim, 0, 1);
        }

        // aura + badge glow
        double breathe = 0.06 * Math.Sin(now * 1.8);
        double auraO = mode == Mode.Dimmed ? 0.3 : 0.3 + 0.5 * glow + 0.5 * flash + breathe;
        Aura.Opacity = Math.Clamp(auraO * dim, 0, 1);
        AuraT.ScaleX = 1 + 0.04 * flash + 0.015 * Math.Sin(now * 1.8); AuraT.ScaleY = 1 + 0.08 * flash;
        Glow.Opacity = Math.Clamp((glow + (mode == Mode.Active ? 0.08 * Math.Sin(now * 2.2) : 0)) * dim, 0, 1);
        GlowT.ScaleX = GlowT.ScaleY = 0.7 + 0.3 * glow;

        // settled: stop using CPU (check / dimmed light stays)
        if (!anyAlive && burst == 0 && flash <= 0)
        {
            if (mode == Mode.Complete && modeT > 3.5) Unhook();
            else if (mode == Mode.Dimmed && Math.Abs(dim - dimTarget) < 0.01) Unhook();
        }
    }

    // ---- indicator ----
    void PlayBadge(bool complete)
    {
        badgeSb?.Stop();
        BadgeFill.Fill = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = complete ? WColor.FromArgb(255, 16, 185, 129) : WColor.FromArgb(255, 47, 128, 237), Offset = 0 },
                new GradientStop { Color = complete ? WColor.FromArgb(255, 52, 211, 153) : WColor.FromArgb(255, 34, 211, 238), Offset = 1 }
            }
        };
        Badge.Visibility = Visibility.Visible;
        var sb = new Storyboard();
        double b0 = complete ? 0.5 : 0.3;
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var a = new DoubleAnimation { From = 0.4, To = 1, Duration = TimeSpan.FromSeconds(0.5), BeginTime = TimeSpan.FromSeconds(b0), EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, BadgeT); Storyboard.SetTargetProperty(a, prop); sb.Children.Add(a);
        }
        var o = new DoubleAnimationUsingKeyFrames();
        o.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
        o.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(b0), Value = 0 });
        o.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(b0 + 0.3), Value = 1 });
        if (!complete)
        {   // connection indicator: shown briefly, then hands over to the transfer view
            o.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(b0 + 1.2), Value = 1 });
            o.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(b0 + 1.7), Value = 0 });
        }
        Storyboard.SetTarget(o, Badge); Storyboard.SetTargetProperty(o, "Opacity"); sb.Children.Add(o);
        badgeSb = sb; sb.Begin();
    }

    void HideBadge() { badgeSb?.Stop(); badgeSb = null; Badge.Opacity = 0; Badge.Visibility = Visibility.Collapsed; }
}
