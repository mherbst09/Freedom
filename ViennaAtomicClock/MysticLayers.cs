using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ViennaAtomicClock;

/// <summary>
/// Cached mystic scenery: a moonlit Japanese night (or a misty pink dawn in light mode) with distant
/// mountains, a torii gate and a cherry tree in full bloom. It is deliberately static so the clock
/// is not repeatedly composited over a full-window redraw.
/// </summary>
internal sealed class MysticBackdrop : FrameworkElement
{
    private readonly DispatcherTimer _resizeDebounce;
    private Size _builtFor;
    private bool _builtDark;
    private ImageSource? _scenery;
    private Size _pendingSize;

    public MysticBackdrop()
    {
        IsHitTestVisible = false;
        _resizeDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            BuildSnapshot(_pendingSize);
        };
        Unloaded += (_, _) => _resizeDebounce.Stop();
    }

    /// <summary>The expensive scenery is static; only <see cref="MysticOverlay"/> animates.</summary>
    internal double TargetFramesPerSecond => 0;

    public double Radius { get; set; }

    public void RefreshTheme()
    {
        if (_builtDark == App.IsDark && _scenery is not null) return;
        _scenery = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var area = new Size(w, h);
        _pendingSize = area;
        if (_scenery is null || _builtDark != App.IsDark)
            BuildSnapshot(area);
        else if (_builtFor != area)
        {
            // Stretch the old bitmap while resizing and rebuild once the user stops dragging.
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        }

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), Radius, Radius));
        dc.DrawImage(_scenery, new Rect(0, 0, w, h));
        dc.Pop();
    }

    private void BuildSnapshot(Size area)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        _scenery = Snapshot(Scenery.Build(area.Width, area.Height, App.IsDark), area.Width, area.Height);
        _builtFor = area;
        _builtDark = App.IsDark;
        InvalidateVisual();
    }

    private ImageSource Snapshot(Drawing drawing, double w, double h)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawDrawing(drawing);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(w * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(h * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The still parts of the picture, drawn once per size: sky, moon, mountains, torii and the cherry tree.</summary>
    private static class Scenery
    {
        public static Drawing Build(double w, double h, bool dark)
        {
            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                var sky = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                    GradientStops = dark
                        ? new GradientStopCollection { new(Color.FromRgb(14, 10, 32), 0), new(Color.FromRgb(42, 24, 62), 0.55), new(Color.FromRgb(74, 40, 84), 1) }
                        : new GradientStopCollection { new(Color.FromRgb(236, 226, 246), 0), new(Color.FromRgb(250, 226, 236), 0.6), new(Color.FromRgb(255, 240, 236), 1) }
                };
                dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));

                // Moon (or a pale morning sun) with a wide halo.
                var moon = new Point(w * 0.8, h * 0.2);
                var r = Math.Min(w, h) * 0.075;
                var halo = new RadialGradientBrush(dark ? Color.FromArgb(90, 255, 236, 214) : Color.FromArgb(120, 255, 210, 220), Colors.Transparent);
                dc.DrawEllipse(halo, null, moon, r * 4.5, r * 4.5);
                dc.DrawEllipse(new SolidColorBrush(dark ? Color.FromRgb(255, 244, 226) : Color.FromRgb(255, 226, 232)), null, moon, r, r);
                if (dark) dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(28, 120, 90, 120)), null, new Point(moon.X - r * 0.3, moon.Y + r * 0.2), r * 0.28, r * 0.22);

                // Two layers of mountains fading into the mist.
                Mountains(dc, w, h, 0.58, 0.16, 5, dark ? Color.FromArgb(150, 70, 52, 104) : Color.FromArgb(120, 198, 170, 210));
                Mountains(dc, w, h, 0.7, 0.12, 11, dark ? Color.FromArgb(200, 46, 30, 70) : Color.FromArgb(150, 214, 168, 196));

                Torii(dc, new Point(w * 0.7, h * 0.74), Math.Min(w, h) * 0.17, dark ? Color.FromArgb(230, 150, 34, 52) : Color.FromArgb(220, 196, 52, 72));

                // Ground mist line.
                var ground = new LinearGradientBrush(Colors.Transparent, dark ? Color.FromArgb(200, 30, 18, 46) : Color.FromArgb(160, 246, 214, 226), 90);
                dc.DrawRectangle(ground, null, new Rect(0, h * 0.78, w, h * 0.22));

                Tree(dc, w, h, dark);
            }
            group.Freeze();
            return group;
        }

        private static void Mountains(DrawingContext dc, double w, double h, double baseline, double height, int seed, Color color)
        {
            var random = new Random(seed);
            var figure = new PathFigure { StartPoint = new Point(0, h), IsClosed = true };
            var peaks = 7;
            figure.Segments.Add(new LineSegment(new Point(0, h * baseline), true));
            for (var i = 0; i <= peaks; i++)
            {
                var x = w * i / peaks;
                var peak = h * (baseline - height * (0.35 + random.NextDouble() * 0.65));
                var valley = h * (baseline - height * random.NextDouble() * 0.25);
                figure.Segments.Add(new QuadraticBezierSegment(new Point(x - w / peaks * 0.5, peak), new Point(x, valley), true));
            }
            figure.Segments.Add(new LineSegment(new Point(w, h), true));
            var fill = new LinearGradientBrush(color, Color.FromArgb((byte)(color.A / 3), color.R, color.G, color.B), 90);
            dc.DrawGeometry(fill, null, new PathGeometry(new[] { figure }));
        }

        private static void Torii(DrawingContext dc, Point foot, double size, Color color)
        {
            var brush = new SolidColorBrush(color);
            var dark = new SolidColorBrush(Color.FromArgb(color.A, (byte)(color.R * 0.35), (byte)(color.G * 0.3), (byte)(color.B * 0.35)));
            var half = size * 0.42;
            var pillar = size * 0.07;
            // Pillars.
            dc.DrawRectangle(brush, null, new Rect(foot.X - half, foot.Y - size, pillar, size));
            dc.DrawRectangle(brush, null, new Rect(foot.X + half - pillar, foot.Y - size, pillar, size));
            // Lower beam (nuki).
            dc.DrawRectangle(brush, null, new Rect(foot.X - half - size * 0.08, foot.Y - size * 0.78, half * 2 + size * 0.16, size * 0.06));
            // Curved top beam (kasagi) with the dark cap.
            var top = new StreamGeometry();
            using (var g = top.Open())
            {
                var y = foot.Y - size * 0.98;
                g.BeginFigure(new Point(foot.X - half - size * 0.2, y - size * 0.1), true, true);
                g.QuadraticBezierTo(new Point(foot.X, y + size * 0.04), new Point(foot.X + half + size * 0.2, y - size * 0.1), true, true);
                g.LineTo(new Point(foot.X + half + size * 0.16, y + size * 0.02), true, true);
                g.QuadraticBezierTo(new Point(foot.X, y + size * 0.14), new Point(foot.X - half - size * 0.16, y + size * 0.02), true, true);
            }
            dc.DrawGeometry(brush, null, top);
            dc.DrawRectangle(dark, null, new Rect(foot.X - size * 0.035, foot.Y - size * 0.94, size * 0.07, size * 0.16));
        }

        /// <summary>A cherry tree grown from one seed, so it looks the same every start.</summary>
        private static void Tree(DrawingContext dc, double w, double h, bool dark)
        {
            var random = new Random(7);
            var scale = Math.Min(w, h);
            var bark = new SolidColorBrush(dark ? Color.FromRgb(38, 22, 40) : Color.FromRgb(98, 64, 78));
            var blossoms = new List<(Point P, double R, Color C)>();
            Color[] pinks = dark
                ? new[] { Color.FromArgb(200, 255, 168, 200), Color.FromArgb(180, 240, 130, 176), Color.FromArgb(210, 255, 206, 224) }
                : new[] { Color.FromArgb(220, 255, 182, 206), Color.FromArgb(200, 246, 150, 186), Color.FromArgb(230, 255, 214, 228) };

            void Branch(Point start, double angle, double length, double width, int depth)
            {
                var bend = (random.NextDouble() - 0.5) * 0.5;
                var end = new Point(start.X + Math.Cos(angle) * length, start.Y + Math.Sin(angle) * length);
                var mid = new Point((start.X + end.X) / 2 + Math.Cos(angle + Math.PI / 2) * length * bend * 0.3,
                                    (start.Y + end.Y) / 2 + Math.Sin(angle + Math.PI / 2) * length * bend * 0.3);
                var nx = Math.Cos(angle + Math.PI / 2);
                var ny = Math.Sin(angle + Math.PI / 2);
                var tip = width * 0.62;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(start.X + nx * width / 2, start.Y + ny * width / 2), true, true);
                    g.QuadraticBezierTo(new Point(mid.X + nx * width * 0.4, mid.Y + ny * width * 0.4), new Point(end.X + nx * tip / 2, end.Y + ny * tip / 2), true, true);
                    g.LineTo(new Point(end.X - nx * tip / 2, end.Y - ny * tip / 2), true, true);
                    g.QuadraticBezierTo(new Point(mid.X - nx * width * 0.4, mid.Y - ny * width * 0.4), new Point(start.X - nx * width / 2, start.Y - ny * width / 2), true, true);
                }
                dc.DrawGeometry(bark, null, geometry);

                if (depth <= 2)
                    for (var i = 0; i < 3 + (2 - depth) * 2; i++)
                    {
                        var t = 0.3 + random.NextDouble() * 0.7;
                        var at = new Point(start.X + (end.X - start.X) * t + (random.NextDouble() - 0.5) * scale * 0.05,
                                           start.Y + (end.Y - start.Y) * t + (random.NextDouble() - 0.5) * scale * 0.05);
                        blossoms.Add((at, scale * (0.016 + random.NextDouble() * 0.022), pinks[random.Next(pinks.Length)]));
                    }

                if (depth == 0 || length < scale * 0.03) return;
                var children = depth > 3 ? 2 : 2 + random.Next(2);
                for (var i = 0; i < children; i++)
                {
                    var spread = (i - (children - 1) / 2.0) * (0.42 + random.NextDouble() * 0.25);
                    Branch(end, angle + spread + (random.NextDouble() - 0.5) * 0.25, length * (0.68 + random.NextDouble() * 0.12), width * 0.64, depth - 1);
                }
            }

            Branch(new Point(w * 0.12, h * 1.02), -Math.PI / 2 + 0.22, scale * 0.3, scale * 0.06, 6);
            // Soft glow behind the crown, then the blossom clusters on top.
            foreach (var (p, r, c) in blossoms)
                dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Colors.Transparent), null, p, r * 2.4, r * 2.4);
            foreach (var (p, r, c) in blossoms)
                dc.DrawEllipse(new SolidColorBrush(c), null, p, r, r);
        }
    }
}

/// <summary>Mystic theme foreground: a few large petals glide smoothly over the cached scenery.</summary>
internal sealed class MysticOverlay : AnimatedLayer
{
    private readonly List<(double X, double Y, double Phase, double Speed, double Size, double Rot)> _petals = new();
    private double _time;

    public MysticOverlay() : base(60) { }

    protected override void Step(double dt)
    {
        _time += dt;
        var w = ActualWidth;
        var h = ActualHeight;
        while (_petals.Count < 9)
            _petals.Add((Random.NextDouble() * w, _petals.Count == 0 && _time < 0.2 ? Random.NextDouble() * h : -30 - Random.NextDouble() * h, Random.NextDouble() * 6, 26 + Random.NextDouble() * 22, 7 + Random.NextDouble() * 5, Random.NextDouble() * 360));
        for (var i = 0; i < _petals.Count; i++)
        {
            var p = _petals[i];
            p.Y += p.Speed * dt;
            p.X += (20 + Math.Sin(_time * 0.9 + p.Phase) * 26) * dt;
            p.Rot += 50 * dt;
            _petals[i] = p.Y > h + 30 || p.X > w + 40 ? (Random.NextDouble() * w * 0.8 - 40, -30, Random.NextDouble() * 6, 26 + Random.NextDouble() * 22, 7 + Random.NextDouble() * 5, Random.NextDouble() * 360) : p;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0) return;
        PushShellClip(dc);
        foreach (var (x, y, phase, _, size, rot) in _petals)
        {
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new RotateTransform(rot));
            dc.PushTransform(new ScaleTransform(size * (0.3 + Math.Abs(Math.Cos(_time * 2 + phase))), size));
            dc.DrawGeometry(Fill(Color.FromRgb(255, 186, 208), 0.7), null, Shapes.Petal);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }
        dc.Pop();
    }
}

internal static class Shapes
{
    /// <summary>Cherry petal with the little notch at its tip, about 2 units tall.</summary>
    public static readonly Geometry Petal = CreatePetal();

    private static Geometry CreatePetal()
    {
        var g = Geometry.Parse("M 0,-0.78 L 0.18,-1 C 0.75,-0.9 0.85,0.25 0,1 C -0.85,0.25 -0.75,-0.9 -0.18,-1 Z");
        g.Freeze();
        return g;
    }
}
