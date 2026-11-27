using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using WColor = Windows.UI.Color;

namespace MoveIt;

// Soft light helpers built on the Composition layer (GPU, real radial gradients, no hard edges).
static class Fx
{
    // smooth fade whenever the element's Opacity changes (Composition implicit animation; replaces the XAML OpacityTransition that failed to load)
    public static void Fade(UIElement e, double secs = 0.25)
    {
        try
        {
            var v = ElementCompositionPreview.GetElementVisual(e); var c = v.Compositor;
            var a = c.CreateScalarKeyFrameAnimation(); a.Target = "Opacity";
            a.InsertExpressionKeyFrame(1f, "this.FinalValue"); a.Duration = TimeSpan.FromSeconds(secs);
            var col = c.CreateImplicitAnimationCollection(); col["Opacity"] = a; v.ImplicitAnimations = col;
        }
        catch { }
    }

    // gaussian-like fall-off: centre -> transparent edge
    static readonly float[] BlobOff = { 0f, .25f, .5f, .75f, 1f };
    static readonly double[] BlobA = { 1, .62, .30, .09, 0 };

    public static SpriteVisual Blob(Compositor c, WColor col, double peak)
    {
        var b = c.CreateRadialGradientBrush();
        b.EllipseCenter = new Vector2(.5f, .5f); b.EllipseRadius = new Vector2(.5f, .5f);
        for (int i = 0; i < BlobOff.Length; i++) b.ColorStops.Add(c.CreateColorGradientStop(BlobOff[i], WColor.FromArgb(0, col.R, col.G, col.B)));
        var v = c.CreateSpriteVisual(); v.Brush = b; Recolor(v, col, peak); return v;
    }

    public static void Recolor(SpriteVisual v, WColor col, double peak)
    {
        if (v.Brush is not CompositionRadialGradientBrush b) return;
        for (int i = 0; i < b.ColorStops.Count && i < BlobA.Length; i++)
            b.ColorStops[i].Color = WColor.FromArgb((byte)Math.Clamp(BlobA[i] * peak * 255, 0, 255), col.R, col.G, col.B);
    }

    // ripple ring: transparent centre, faint fill, bright thin crest, soft outer fade
    static readonly float[] RingOff = { 0f, .55f, .78f, .9f, .96f, 1f };
    static readonly double[] RingA = { 0, .05, .32, .9, .3, 0 };
    static readonly double[] RingWhite = { 0, 0, .2, .65, .2, 0 };

    public static (SpriteVisual V, CompositionColorGradientStop[] St) Ring(Compositor c, float size)
    {
        var b = c.CreateRadialGradientBrush();
        b.EllipseCenter = new Vector2(.5f, .5f); b.EllipseRadius = new Vector2(.5f, .5f);
        var st = new CompositionColorGradientStop[RingOff.Length];
        for (int i = 0; i < st.Length; i++) { st[i] = c.CreateColorGradientStop(RingOff[i], WColor.FromArgb(0, 255, 255, 255)); b.ColorStops.Add(st[i]); }
        var v = c.CreateSpriteVisual(); v.Brush = b; v.Size = new Vector2(size, size); v.CenterPoint = new Vector3(size / 2, size / 2, 0); v.IsVisible = false;
        return (v, st);
    }

    public static void ColorRing(CompositionColorGradientStop[] st, byte r, byte g, byte b)
    {
        for (int i = 0; i < st.Length; i++)
        {
            double w = RingWhite[i];
            st[i].Color = WColor.FromArgb((byte)(RingA[i] * 255), (byte)(r + (255 - r) * w), (byte)(g + (255 - g) * w), (byte)(b + (255 - b) * w));
        }
    }
}
