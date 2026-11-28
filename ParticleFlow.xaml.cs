using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace MoveIt;

// Light particles that flow top to bottom. Driven by real connection / transfer states:
//   Start()    waiting for a device  -> calm downward flow
//   Connect()  device connected      -> particles converge on the connection point, success badge, then faster flow
//   Complete() all files received    -> particles converge, glowing check stays
//   Dim()      cancelled / failed    -> flow slows and fades
// Only runs a frame loop while something is moving, so it costs nothing when idle.
public sealed partial class ParticleFlow : UserControl
{
    enum Mode { Off, Idle, Active, Converge, Complete }
    sealed class P { public Rectangle El = null!; public CompositeTransform T = null!; public double X, Y, V, Len, W, Phase, Xr, Base; }

    readonly List<P> ps = new(); readonly Random rnd = new(); readonly Stopwatch clock = Stopwatch.StartNew();
    readonly LinearGradientBrush[] brushes;
    Mode mode = Mode.Off; double modeT, last, w = 1, h = 1, cx, cy, speed = 1, speedTarget = 1, glow, glowTarget, dim = 1, dimTarget = 1;
    bool hooked; Storyboard? badgeSb;

    // where particles gather, as a fraction of the control's height
    public double CenterFraction { get; set; } = 0.42;

    public ParticleFlow()
    {
        InitializeComponent();
        brushes = new[] { Trail(34, 211, 238), Trail(59, 130, 246), Trail(186, 230, 253) }; // cyan, electric blue, ice blue
        Unloaded += (_, _) => Unhook();
    }

    static LinearGradientBrush Trail(byte r, byte g, byte b)
    {
        var br = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        br.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, r, g, b), Offset = 0 });
        br.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(150, r, g, b), Offset = 0.7 });
        br.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 232, 247, 255), Offset = 1 });
        return br;
    }

    // ---- public states ----
    public void Start() { HideBadge(); foreach (var p in ps) Place(p, true); SetMode(Mode.Idle); speedTarget = 1; glowTarget = 0; dimTarget = 1; Hook(); }
    public void Connect()
    {
        SetMode(Mode.Converge); glowTarget = 1; dimTarget = 1; PlayBadge(false); Hook();
    }
    public void Complete() { SetMode(Mode.Complete); glowTarget = 1; dimTarget = 1; PlayBadge(true); Hook(); }
    public void Dim() { HideBadge(); if (mode == Mode.Off) return; SetMode(Mode.Idle); speedTarget = 0.5; glowTarget = 0; dimTarget = 0.35; Hook(); }
    public void Stop() { SetMode(Mode.Off); HideBadge(); Unhook(); foreach (var p in ps) p.El.Opacity = 0; Glow.Opacity = 0; glow = glowTarget = 0; }

    void SetMode(Mode m) { mode = m; modeT = 0; }

    // ---- layout / particles ----
    void Host_SizeChanged(object s, SizeChangedEventArgs e)
    {
        w = Math.Max(1, e.NewSize.Width); h = Math.Max(1, e.NewSize.Height); cx = w / 2; cy = h * CenterFraction;
        Host.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, w, h) };
        Glow.Margin = new Thickness(0, cy - 170, 0, 0); Badge.Margin = new Thickness(0, cy - 46, 0, 0);
        int n = (int)Math.Clamp(w / 22, 22, 54);
        while (ps.Count > n) { Field.Children.Remove(ps[^1].El); ps.RemoveAt(ps.Count - 1); }
        while (ps.Count < n) Add();
        if (!hooked && mode != Mode.Off) { foreach (var p in ps) Place(p, true); }
    }

    void Add()
    {
        double depth = rnd.NextDouble();                       // 0 = far/slow/thin, 1 = near/fast/bright
        var p = new P { W = 1.4 + depth * 2.2, Len = 26 + depth * 54, V = 70 + depth * 130, Phase = rnd.NextDouble() * 6.28, Base = 0.35 + depth * 0.65, Xr = rnd.NextDouble() };
        p.T = new CompositeTransform { CenterY = p.Len };
        p.El = new Rectangle
        {
            Width = p.W, Height = p.Len, RadiusX = p.W / 2, RadiusY = p.W / 2, Opacity = 0, IsHitTestVisible = false,
            Fill = brushes[rnd.Next(brushes.Length)], RenderTransform = p.T
        };
        Field.Children.Add(p.El); ps.Add(p); Place(p, true);
    }

    // put a particle back above the top edge (spread = random depth above so streams look continuous)
    void Place(P p, bool spread)
    {
        p.Xr = rnd.NextDouble(); p.X = p.Xr * w;
        p.Y = spread ? -rnd.NextDouble() * h * 0.6 : -p.Len - rnd.NextDouble() * h * 0.25;
    }

    // ---- frame loop ----
    void Hook() { if (hooked) return; last = clock.Elapsed.TotalSeconds; CompositionTarget.Rendering += OnFrame; hooked = true; }
    void Unhook() { if (!hooked) return; CompositionTarget.Rendering -= OnFrame; hooked = false; }

    static double Sm(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    void OnFrame(object? sender, object e)
    {
        double now = clock.Elapsed.TotalSeconds, dt = Math.Min(now - last, 0.05); last = now;
        if (dt <= 0) return;
        modeT += dt;
        double ease = Math.Min(1, dt * 3);
        speed += (speedTarget - speed) * ease; glow += (glowTarget - glow) * Math.Min(1, dt * 2.5); dim += (dimTarget - dim) * ease;

        if (mode == Mode.Converge && modeT > 1.2)
        {   // connection animation finished: continue as a livelier downward stream
            mode = Mode.Active; modeT = 0; speedTarget = 1.7; speed = 0.5; glowTarget = 0.35;
            foreach (var p in ps) Place(p, true);
        }

        double scale = Math.Clamp(h / 700, 0.6, 1.6);
        bool converging = mode == Mode.Converge || mode == Mode.Complete;
        foreach (var p in ps)
        {
            double a, sy = 1;
            if (converging)
            {
                double k = (mode == Mode.Complete ? 2.0 : 3.2) * (0.75 + 0.5 * (p.Phase / 6.28));
                double f = 1 - Math.Exp(-k * dt);
                p.X += (cx - p.X) * f; p.Y += (cy - p.Y) * f;
                double dist = Math.Sqrt((cx - p.X) * (cx - p.X) + (cy - p.Y) * (cy - p.Y));
                sy = Math.Clamp(dist / (h * 0.45), 0.1, 1);
                a = p.Base * Math.Min(1, dist / 70) * (p.Y < -p.Len ? 0 : 1);
            }
            else
            {
                p.Y += p.V * speed * scale * dt;
                p.X = p.Xr * w + Math.Sin(now * 0.9 + p.Phase) * 4;   // tiny sway, direction is always downward
                if (p.Y - p.Len > h) Place(p, false);
                double fadeIn = Sm((p.Y + p.Len * 0.2) / Math.Max(60, h * 0.12)), fadeOut = 1 - Sm((p.Y - h * 0.82) / Math.Max(1, h * 0.18));
                a = p.Base * fadeIn * fadeOut * (mode == Mode.Active ? 1 : 0.8);
            }
            p.El.Opacity = Math.Clamp(a * dim, 0, 1);
            p.T.ScaleY = sy; p.T.TranslateX = p.X - p.W / 2; p.T.TranslateY = p.Y - p.Len;
        }

        double breathe = mode == Mode.Active ? 0.08 * Math.Sin(now * 2.2) : 0;
        Glow.Opacity = Math.Clamp(glow + breathe, 0, 1); GlowT.ScaleX = GlowT.ScaleY = 0.7 + 0.3 * glow;

        if (mode == Mode.Complete && modeT > 3) Unhook();   // settled: stop using CPU, the check stays
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
                new GradientStop { Color = complete ? Windows.UI.Color.FromArgb(255, 16, 185, 129) : Windows.UI.Color.FromArgb(255, 47, 128, 237), Offset = 0 },
                new GradientStop { Color = complete ? Windows.UI.Color.FromArgb(255, 52, 211, 153) : Windows.UI.Color.FromArgb(255, 34, 211, 238), Offset = 1 }
            }
        };
        Badge.Visibility = Visibility.Visible;
        var sb = new Storyboard();
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var a = new DoubleAnimation { From = 0.4, To = 1, Duration = TimeSpan.FromSeconds(0.5), BeginTime = TimeSpan.FromSeconds(complete ? 0.8 : 0.5), EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, BadgeT); Storyboard.SetTargetProperty(a, prop); sb.Children.Add(a);
        }
        var o = new DoubleAnimationUsingKeyFrames();
        double b0 = complete ? 0.8 : 0.5;
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
