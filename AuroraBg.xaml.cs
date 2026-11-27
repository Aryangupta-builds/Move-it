using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using WColor = Windows.UI.Color;

namespace MoveIt;

// App background: a few very soft, slowly drifting colour clouds (real radial gradients, no hard edges).
public sealed partial class AuroraBg : UserControl
{
    readonly Compositor comp;
    readonly ContainerVisual root;
    readonly List<(SpriteVisual V, double Ax, double Ay, double Size, float Dx, float Dy, double Secs)> blobs = new();
    bool dark = true; double w = 1, h = 1;

    public AuroraBg()
    {
        InitializeComponent();
        comp = ElementCompositionPreview.GetElementVisual(this).Compositor;
        root = comp.CreateContainerVisual();
        ElementCompositionPreview.SetElementChildVisual(Host, root);
        Build();
    }

    public void SetDark(bool d) { if (d == dark) return; dark = d; Build(); }

    void Build()
    {
        root.Children.RemoveAll(); blobs.Clear();
        WColor C(byte r, byte g, byte b) => WColor.FromArgb(255, r, g, b);
        if (dark)
        {   // deep navy base (from BgBrush) + calm blue / violet / cyan light
            Add(C(30, 100, 255), 0.45, 0.10, 0.10, 1.00, 70, 40, 18);
            Add(C(110, 70, 255), 0.40, 0.55, 1.05, 0.95, -60, 50, 22);
            Add(C(0, 170, 255), 0.28, 0.98, 0.45, 0.85, 40, -50, 26);
        }
        else
        {   // pastel
            Add(C(120, 170, 255), 0.60, 0.10, 0.10, 1.00, 70, 40, 18);
            Add(C(190, 160, 255), 0.50, 0.55, 1.05, 0.95, -60, 50, 22);
            Add(C(130, 220, 255), 0.50, 0.98, 0.45, 0.85, 40, -50, 26);
        }
        Layout();
    }

    void Add(WColor col, double peak, double ax, double ay, double size, float dx, float dy, double secs)
    {
        var v = Fx.Blob(comp, col, peak); root.Children.InsertAtTop(v);
        blobs.Add((v, ax, ay, size, dx, dy, secs));
    }

    void Host_SizeChanged(object s, SizeChangedEventArgs e) { w = Math.Max(1, e.NewSize.Width); h = Math.Max(1, e.NewSize.Height); Layout(); }

    void Layout()
    {
        double m = Math.Max(w, h);
        foreach (var b in blobs)
        {
            float sz = (float)(b.Size * m * 1.1);
            var p0 = new Vector3((float)(b.Ax * w - sz / 2), (float)(b.Ay * h - sz / 2), 0);
            b.V.Size = new Vector2(sz, sz); b.V.Offset = p0;
            b.V.StopAnimation("Offset");
            var an = comp.CreateVector3KeyFrameAnimation();
            var ease = comp.CreateCubicBezierEasingFunction(new Vector2(.45f, 0), new Vector2(.55f, 1));
            an.InsertKeyFrame(0, p0); an.InsertKeyFrame(.5f, p0 + new Vector3(b.Dx, b.Dy, 0), ease); an.InsertKeyFrame(1, p0, ease);
            an.Duration = TimeSpan.FromSeconds(b.Secs); an.IterationBehavior = AnimationIterationBehavior.Forever;
            b.V.StartAnimation("Offset", an);
        }
    }
}
