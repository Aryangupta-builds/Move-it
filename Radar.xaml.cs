using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace MoveIt;

public sealed partial class Radar : UserControl
{
    readonly Storyboard story = new() { RepeatBehavior = RepeatBehavior.Forever };

    public Radar()
    {
        InitializeComponent();
        for (int i = 0; i < 3; i++)
        {
            var scale = new ScaleTransform { ScaleX = 0.3, ScaleY = 0.3 };
            var ring = new Ellipse
            {
                Width = 260, Height = 260, StrokeThickness = 2, Opacity = 0,
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 10, 132, 255)),
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 10, 132, 255)),
                RenderTransform = scale,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5)
            };
            Root.Children.Insert(i, ring);
            double begin = i * 0.8;
            Anim(scale, "ScaleX", 0.3, 1, begin);
            Anim(scale, "ScaleY", 0.3, 1, begin);
            Anim(ring, "Opacity", 0.8, 0, begin);
        }
    }

    void Anim(DependencyObject target, string prop, double from, double to, double begin)
    {
        var a = new DoubleAnimation
        {
            From = from, To = to,
            Duration = new Duration(TimeSpan.FromSeconds(2.4)),
            BeginTime = TimeSpan.FromSeconds(begin),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, prop);
        story.Children.Add(a);
    }

    public void Start() => story.Begin();
    public void Stop() => story.Stop();
}
