using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace MoveIt;

// Radar sweep (searching) + AirDrop-style ripples and flying file chips (sending / receiving)
public sealed partial class Radar : UserControl
{
    Storyboard idle = new(), tx = new(), chipSb = new();
    bool idleOn, txOn;

    public Radar()
    {
        InitializeComponent();
        // idle: slow rings + sweep
        for (int i = 0; i < 3; i++) Ring(idle, 3.0, i * 1.0);
        var sw = new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(3), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(sw, SweepRot); Storyboard.SetTargetProperty(sw, "Angle"); idle.Children.Add(sw);
        // transfer: faster rings + chips
        for (int i = 0; i < 4; i++) Ring(tx, 1.6, i * 0.4);
        for (int i = 0; i < 3; i++) Chip(i * 0.6);
    }

    void Logo_Failed(object s, ExceptionRoutedEventArgs e) { }

    void Ring(Storyboard sb, double dur, double begin)
    {
        var t = new ScaleTransform { ScaleX = 0.3, ScaleY = 0.3 };
        var r = new Ellipse
        {
            Width = 290, Height = 290, StrokeThickness = 2, Opacity = 0, IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 56, 189, 248)),
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 47, 128, 237)),
            RenderTransform = t, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5)
        };
        Root.Children.Insert(0, r);
        Key(sb, t, "ScaleX", dur, begin, (0, 0.3), (1, 1));
        Key(sb, t, "ScaleY", dur, begin, (0, 0.3), (1, 1));
        Key(sb, r, "Opacity", dur, begin, (0, 0.8), (1, 0));
    }

    // Flying file chip: moves vertically; direction decided in Transfer(send)
    readonly List<(CompositeTransform t, Border b, double begin)> chips = new();
    void Chip(double begin)
    {
        var t = new CompositeTransform();
        var b = new Border
        {
            Width = 40, Height = 50, CornerRadius = new CornerRadius(10), Opacity = 0, IsHitTestVisible = false,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(235, 255, 255, 255)),
            RenderTransform = t, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            Child = new FontIcon { Glyph = "\uE8A5", FontSize = 22, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 47, 128, 237)) }
        };
        Root.Children.Add(b);
        chips.Add((t, b, begin));
    }

    static void Key(Storyboard sb, DependencyObject target, string prop, double dur, double begin, params (double at, double v)[] k)
    {
        var a = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromSeconds(begin), RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(dur) };
        foreach (var (at, v) in k)
            a.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(at * dur), Value = v, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        Storyboard.SetTarget(a, target); Storyboard.SetTargetProperty(a, prop); sb.Children.Add(a);
    }

    public void Start() { Reset(); StopTx(); if (!idleOn) { idle.Begin(); idleOn = true; } Sweep.Visibility = Visibility.Visible; }
    public void Stop() { if (idleOn) { idle.Stop(); idleOn = false; } StopTx(); }

    void StopTx() { if (txOn) { tx.Stop(); chipSb.Stop(); txOn = false; } }

    void Reset() { Check.Visibility = Visibility.Collapsed; CheckT.ScaleX = CheckT.ScaleY = 0; }

    // send = file flies out from this laptop, receive = file drops into this laptop (AirDrop style)
    public void Transfer(bool send)
    {
        Reset(); Stop(); Sweep.Visibility = Visibility.Collapsed;
        chipSb = new Storyboard();
        foreach (var (t, b, begin) in chips)
        {
            double from = send ? 0 : -140, to = send ? -140 : 0;
            Key(chipSb, t, "TranslateY", 1.8, begin, (0, from), (1, to));
            Key(chipSb, t, "ScaleX", 1.8, begin, (0, send ? 1 : 0.4), (1, send ? 0.4 : 1));
            Key(chipSb, t, "ScaleY", 1.8, begin, (0, send ? 1 : 0.4), (1, send ? 0.4 : 1));
            Key(chipSb, b, "Opacity", 1.8, begin, (0, 0), (0.25, 1), (0.75, 1), (1, 0));
        }
        tx.Begin(); chipSb.Begin(); txOn = true;
    }

    public void Done()
    {
        Stop(); Sweep.Visibility = Visibility.Collapsed;
        Check.Visibility = Visibility.Visible;
        var sb = new Storyboard();
        foreach (var p in new[] { "ScaleX", "ScaleY" })
        {
            var a = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(0.5), EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, CheckT); Storyboard.SetTargetProperty(a, p); sb.Children.Add(a);
        }
        sb.Begin();
    }
}
