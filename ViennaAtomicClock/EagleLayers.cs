using System.Windows;
using System.Windows.Media;

namespace ViennaAtomicClock;

/// <summary>
/// Eagle theme backdrop in the colours of the Freedom logo: soft pink and cyan light orbs drift slowly,
/// a gradient orbit ring turns with a yellow planet on it, and a few sparkles twinkle.
/// </summary>
internal sealed class EagleBackdrop : AnimatedLayer
{
    private static readonly Color Pink = Color.FromRgb(255, 46, 136);
    private static readonly Color Cyan = Color.FromRgb(50, 200, 240);
    private static readonly Color Yellow = Color.FromRgb(255, 212, 59);

    private sealed record Orb(Color Color, double X, double Y, double Radius, double Speed, double Phase);

    private readonly Orb[] _orbs =
    {
        new(Pink, 0.18, 0.25, 0.42, 0.09, 0),
        new(Cyan, 0.82, 0.7, 0.46, 0.07, 2),
        new(Pink, 0.7, 0.15, 0.28, 0.11, 4),
        new(Cyan, 0.25, 0.85, 0.3, 0.08, 1),
        new(Yellow, 0.55, 0.5, 0.16, 0.13, 3)
    };

    private readonly (double X, double Y, double Phase, double Size)[] _sparkles;
    private double _time;

    public EagleBackdrop() : base(30)
    {
        var random = new Random(11);
        _sparkles = Enumerable.Range(0, 14).Select(_ => (random.NextDouble(), random.NextDouble(), random.NextDouble() * 6, 4 + random.NextDouble() * 6)).ToArray();
    }

    protected override void Step(double dt) => _time += dt;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0) return;
        var dark = App.IsDark;
        PushShellClip(dc);

        foreach (var orb in _orbs)
        {
            var x = (orb.X + Math.Sin(_time * orb.Speed + orb.Phase) * 0.08) * w;
            var y = (orb.Y + Math.Cos(_time * orb.Speed * 1.3 + orb.Phase) * 0.08) * h;
            var r = orb.Radius * Math.Min(w, h) * (1 + Math.Sin(_time * 0.4 + orb.Phase) * 0.08);
            var alpha = (byte)(dark ? 70 : 55);
            var glow = new RadialGradientBrush(Color.FromArgb(alpha, orb.Color.R, orb.Color.G, orb.Color.B), Color.FromArgb(0, orb.Color.R, orb.Color.G, orb.Color.B));
            glow.Freeze();
            dc.DrawEllipse(glow, null, new Point(x, y), r, r);
        }

        // Orbit ring from the logo, slowly turning, with the yellow planet travelling along it.
        var center = new Point(w * 0.78, h * 0.32);
        var rx = Math.Min(w, h) * 0.42;
        var ry = rx * 0.38;
        var tilt = -18 + Math.Sin(_time * 0.2) * 4;
        var ring = new LinearGradientBrush(Color.FromArgb(150, Pink.R, Pink.G, Pink.B), Color.FromArgb(150, Cyan.R, Cyan.G, Cyan.B), 0);
        ring.Freeze();
        dc.PushTransform(new RotateTransform(tilt, center.X, center.Y));
        dc.DrawEllipse(null, new Pen(ring, 6), center, rx, ry);
        var a = _time * 0.35;
        var planet = new Point(center.X + Math.Cos(a) * rx, center.Y + Math.Sin(a) * ry);
        var halo = new RadialGradientBrush(Color.FromArgb(120, Yellow.R, Yellow.G, Yellow.B), Color.FromArgb(0, Yellow.R, Yellow.G, Yellow.B));
        halo.Freeze();
        dc.DrawEllipse(halo, null, planet, 34, 34);
        dc.DrawEllipse(Fill(Yellow, 0.95), null, planet, 11, 11);
        dc.Pop();

        foreach (var (sx, sy, phase, size) in _sparkles)
        {
            var twinkle = Math.Max(0, Math.Sin(_time * 1.6 + phase));
            if (twinkle < 0.05) continue;
            dc.PushTransform(new TranslateTransform(sx * w, sy * h));
            dc.PushTransform(new ScaleTransform(size * twinkle, size * twinkle));
            dc.DrawGeometry(Fill(phase > 3 ? Cyan : Colors.White, 0.8 * twinkle), null, Sparkle);
            dc.Pop();
            dc.Pop();
        }
        dc.Pop();
    }

    private static readonly Geometry Sparkle = CreateSparkle();

    private static Geometry CreateSparkle()
    {
        var g = Geometry.Parse("M 0,-1 Q 0.12,-0.12 1,0 Q 0.12,0.12 0,1 Q -0.12,0.12 -1,0 Q -0.12,-0.12 0,-1 Z");
        g.Freeze();
        return g;
    }
}

/// <summary>Small picture of a theme for the settings list: window, cards, accent and the theme's motif.</summary>
internal static class ThemePreview
{
    public static Brush Create(string theme, bool dark)
    {
        var (window, surface, accent, text, border) = Themes.PreviewColors(theme, dark);
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(window), null, new Rect(0, 0, 160, 90), 8, 8);
            Motif(dc, theme, accent);
            var card = new SolidColorBrush(Color.FromArgb(225, surface.R, surface.G, surface.B));
            var edge = new Pen(new SolidColorBrush(border), 1);
            dc.DrawRoundedRectangle(card, edge, new Rect(10, 22, 92, 58), 6, 6);
            dc.DrawRoundedRectangle(card, edge, new Rect(108, 22, 42, 58), 6, 6);
            dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, new Rect(10, 8, 16, 9), 3, 3);
            dc.DrawRoundedRectangle(new SolidColorBrush(text), null, new Rect(22, 40, 54, 12), 3, 3);
            dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, new Rect(22, 64, 40, 5), 2, 2);
            for (var i = 0; i < 4; i++)
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(150, text.R, text.G, text.B)), null, new Rect(114, 30 + i * 11, 28, 4), 2, 2);
        }
        group.Freeze();
        var brush = new DrawingBrush(group) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }

    private static void Motif(DrawingContext dc, string theme, Color accent)
    {
        var random = new Random(theme.Length * 7);
        switch (theme)
        {
            case Themes.Hacker:
                for (var x = 4; x < 160; x += 9)
                    for (var y = random.Next(-20, 40); y < 90; y += 7)
                        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)random.Next(40, 140), 57, 255, 106)), null, new Rect(x, y, 3, 4));
                break;
            case Themes.Anime:
                foreach (var (x, y) in new[] { (130.0, 10.0), (60.0, 84.0), (150.0, 70.0), (40.0, 12.0) })
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(160, 255, 120, 180)), null, new Point(x, y), 5, 5);
                break;
            case Themes.Tetris:
                Color[] blocks = { Color.FromRgb(0, 240, 240), Color.FromRgb(240, 240, 0), Color.FromRgb(160, 0, 240), Color.FromRgb(240, 0, 0) };
                for (var i = 0; i < 12; i++)
                    dc.DrawRectangle(new SolidColorBrush(blocks[i % 4]), null, new Rect(random.Next(0, 15) * 10, 80 - random.Next(0, 3) * 10, 9, 9));
                break;
            case Themes.America:
                for (var i = 0; i < 7; i++)
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(110, 178, 34, 52)), null, new Rect(0, i * 13, 160, 6));
                break;
            case Themes.Minecraft:
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(106, 170, 64)), null, new Rect(0, 82, 160, 4));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(134, 96, 67)), null, new Rect(0, 86, 160, 4));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(255, 240, 120)), null, new Rect(120, 4, 12, 12));
                break;
            case Themes.Eagle:
                var ring = new LinearGradientBrush(Color.FromRgb(255, 46, 136), Color.FromRgb(50, 200, 240), 0);
                dc.DrawEllipse(null, new Pen(ring, 3), new Point(118, 16), 34, 12);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 212, 59)), null, new Point(150, 14), 4, 4);
                break;
            case Themes.Mystic:
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(230, 255, 240, 226)), null, new Point(132, 14), 9, 9);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 170, 40, 60)), null, new Rect(112, 4, 2, 16));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 170, 40, 60)), null, new Rect(150, 4, 2, 16));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 170, 40, 60)), null, new Rect(108, 3, 48, 3));
                foreach (var (x, y, r) in new[] { (14.0, 10.0, 7.0), (26.0, 6.0, 6.0), (38.0, 14.0, 6.0), (6.0, 20.0, 5.0), (60.0, 86.0, 3.0), (90.0, 12.0, 3.0), (150.0, 84.0, 3.0) })
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 255, 170, 200)), null, new Point(x, y), r, r);
                dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 255, 255, 255), Color.FromArgb(110, 255, 255, 255), 90), null, new Rect(0, 60, 160, 30));
                break;
            case Themes.War:
                for (var i = 0; i < 40; i++)
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, new Rect(random.NextDouble() * 160, random.NextDouble() * 90, 1.5, 1.5));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null, new Rect(64, 0, 1, 90));
                break;
        }
    }
}
