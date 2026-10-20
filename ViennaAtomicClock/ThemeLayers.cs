using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ViennaAtomicClock;

/// <summary>Click-through animated layer that only runs while it is visible (its theme is active).</summary>
internal abstract class AnimatedLayer : FrameworkElement
{
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<(Color, int), Brush> _brushes = new();
    protected readonly Random Random = new();

    protected AnimatedLayer(double framesPerSecond)
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / framesPerSecond) };
        _timer.Tick += (_, _) =>
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            Step(_timer.Interval.TotalSeconds);
            InvalidateVisual();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _timer.Start();
            else _timer.Stop();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Corner radius of the window shell, so nothing spills past rounded corners.</summary>
    public double Radius { get; set; }

    protected abstract void Step(double dt);

    protected Color ResourceColor(string key, Color fallback) =>
        TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

    /// <summary>Cached frozen brush with one of 20 alpha levels.</summary>
    protected Brush Fill(Color color, double alpha)
    {
        var level = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 20);
        if (_brushes.TryGetValue((color, level), out var brush)) return brush;
        var created = new SolidColorBrush(Color.FromArgb((byte)(level * 255 / 20), color.R, color.G, color.B));
        created.Freeze();
        _brushes[(color, level)] = created;
        return created;
    }

    protected void PushShellClip(DrawingContext dc) =>
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), Radius, Radius));
}

/// <summary>Hacker theme backdrop: falling columns of glowing glyphs behind the translucent cards.</summary>
internal sealed class HackerRain : AnimatedLayer
{
    private const double Cell = 17;
    private const string Glyphs = "ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ0123456789ABCDEF<>/{}[]=+*#$%&λΣΔ";

    private sealed class Column { public double Row, Speed; public int Length; }

    private Geometry[]? _shapes;
    private int[,] _cells = new int[0, 0];
    private Column[] _drops = Array.Empty<Column>();

    public HackerRain() : base(16) { }

    private void EnsureGrid()
    {
        var cols = Math.Max(1, (int)(ActualWidth / Cell) + 1);
        var rows = Math.Max(1, (int)(ActualHeight / Cell) + 1);
        if (_cells.GetLength(0) == cols && _cells.GetLength(1) == rows) return;

        _cells = new int[cols, rows];
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < rows; r++)
                _cells[c, r] = Random.Next(Glyphs.Length);

        _drops = Enumerable.Range(0, cols).Select(_ => NewDrop(rows, initial: true)).ToArray();
    }

    private Column NewDrop(int rows, bool initial) => new()
    {
        Row = initial ? Random.NextDouble() * rows * 1.5 - rows * 0.5 : -Random.NextDouble() * rows * 0.6,
        Speed = 0.35 + Random.NextDouble() * 0.9,
        Length = 6 + Random.Next(18)
    };

    protected override void Step(double dt)
    {
        EnsureGrid();
        var rows = _cells.GetLength(1);
        foreach (var drop in _drops)
        {
            drop.Row += drop.Speed;
            if (drop.Row - drop.Length > rows) { var fresh = NewDrop(rows, false); drop.Row = fresh.Row; drop.Speed = fresh.Speed; drop.Length = fresh.Length; }
        }

        // A few glyphs flicker to new characters every frame.
        for (var i = 0; i < _cells.Length / 30 + 1; i++)
            _cells[Random.Next(_cells.GetLength(0)), Random.Next(rows)] = Random.Next(Glyphs.Length);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || _drops.Length == 0) return;
        _shapes ??= BuildShapes(VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var accent = ResourceColor("AccentBrush", Color.FromRgb(57, 255, 106));
        var head = ResourceColor("TextPrimary", Colors.White);
        var rows = _cells.GetLength(1);

        PushShellClip(dc);
        for (var c = 0; c < _drops.Length; c++)
        {
            var drop = _drops[c];
            var headRow = (int)drop.Row;
            for (var k = 0; k < drop.Length; k++)
            {
                var r = headRow - k;
                if (r < 0 || r >= rows) continue;
                var alpha = k == 0 ? 0.75 : 0.5 * (1 - k / (double)drop.Length);
                dc.PushTransform(new TranslateTransform(c * Cell, r * Cell));
                dc.DrawGeometry(Fill(k == 0 ? head : accent, alpha), null, _shapes[_cells[c, r]]);
                dc.Pop();
            }
        }
        dc.Pop();
    }

    private static Geometry[] BuildShapes(double pixelsPerDip)
    {
        var typeface = new Typeface(new FontFamily("MS Gothic, Yu Gothic, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        return Glyphs.Select(ch =>
        {
            var text = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Cell * 0.82, Brushes.White, pixelsPerDip);
            var geometry = text.BuildGeometry(new Point((Cell - text.Width) / 2, 0));
            geometry.Freeze();
            return geometry;
        }).ToArray();
    }
}

/// <summary>Hacker theme overlay: CRT scanlines, a slow refresh band and a green vignette.</summary>
internal sealed class HackerScanlines : AnimatedLayer
{
    private readonly Brush _lines;
    private double _time;

    public HackerScanlines() : base(24)
    {
        var tile = new DrawingBrush(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(34, 0, 0, 0)), null, new RectangleGeometry(new Rect(0, 0, 4, 1))))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 4, 3),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 4, 3),
            ViewboxUnits = BrushMappingMode.Absolute
        };
        tile.Freeze();
        _lines = tile;
    }

    protected override void Step(double dt) => _time += dt;

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width <= 0) return;
        var accent = ResourceColor("AccentBrush", Color.FromRgb(57, 255, 106));

        PushShellClip(dc);
        dc.DrawRectangle(_lines, null, bounds);

        var bandY = (_time % 7) / 7 * (bounds.Height + 240) - 120;
        var band = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0),
                new GradientStop(Color.FromArgb(16, accent.R, accent.G, accent.B), 0.5),
                new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1)
            }
        };
        band.Freeze();
        dc.DrawRectangle(band, null, new Rect(0, bandY, bounds.Width, 120));

        var vignette = new RadialGradientBrush
        {
            RadiusX = 0.75, RadiusY = 0.75,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55),
                new GradientStop(Color.FromArgb(60, 0, 20, 5), 0.9),
                new GradientStop(Color.FromArgb(120, 0, 10, 2), 1.1)
            }
        };
        vignette.Freeze();
        dc.DrawRectangle(vignette, null, bounds);
        dc.Pop();
    }
}

/// <summary>
/// Anime theme backdrop: floating hearts, paws and sparkles, little paw-print trails and
/// cat ears peeking over the top of the main clock card.
/// </summary>
internal sealed class AnimeBackdrop : AnimatedLayer
{
    private enum Kind { Heart, Paw, Sparkle }

    private sealed class Floater
    {
        public double X, Y, Vy, Phase, Rotation, Spin, Size, Alpha, Flip;
        public Kind Kind;
    }

    private sealed class Print
    {
        public Point At;
        public double Angle, Age, Delay;
    }

    private readonly List<Floater> _floaters = new();
    private readonly List<Print> _prints = new();
    private readonly List<Floater> _petals = new();
    private double _time;
    private double _nextTrail = 1.5;

    public AnimeBackdrop() : base(30) { }

    protected override void Step(double dt)
    {
        _time += dt;
        var w = ActualWidth;
        var h = ActualHeight;

        while (_floaters.Count < 28)
            _floaters.Add(NewFloater(w, h, anywhere: _floaters.Count < 28 && _time < 0.2));

        foreach (var f in _floaters)
        {
            f.Y += f.Vy * dt;
            f.X += Math.Sin(_time * 0.8 + f.Phase) * 10 * dt;
            f.Rotation += f.Spin * dt;
        }
        _floaters.RemoveAll(f => f.Y < -40);

        if (_time >= _nextTrail)
        {
            _nextTrail = _time + 5 + Random.NextDouble() * 4;
            StartTrail(w, h);
        }
        // Cherry blossom petals tumbling down.
        while (_petals.Count < 22)
            _petals.Add(new Floater { X = Random.NextDouble() * w, Y = _time < 0.2 ? Random.NextDouble() * h : -20, Vy = 25 + Random.NextDouble() * 35,
                Phase = Random.NextDouble() * 10, Rotation = Random.NextDouble() * 360, Spin = (Random.NextDouble() - 0.5) * 120, Size = 5 + Random.NextDouble() * 4 });
        foreach (var p in _petals)
        {
            p.Y += p.Vy * dt;
            p.X += (Math.Sin(_time * 1.3 + p.Phase) * 30 + 14) * dt;
            p.Rotation += p.Spin * dt;
            p.Flip += dt * 3;
        }
        _petals.RemoveAll(p => p.Y > h + 20 || p.X > w + 30);

        foreach (var p in _prints) p.Age += dt;
        _prints.RemoveAll(p => p.Age - p.Delay > 3.2);
    }

    private Floater NewFloater(double w, double h, bool anywhere)
    {
        var roll = Random.NextDouble();
        return new Floater
        {
            X = Random.NextDouble() * w,
            Y = anywhere ? Random.NextDouble() * h : h + 30,
            Vy = -(10 + Random.NextDouble() * 22),
            Phase = Random.NextDouble() * 10,
            Rotation = Random.NextDouble() * 40 - 20,
            Spin = (Random.NextDouble() - 0.5) * 30,
            Size = 7 + Random.NextDouble() * 10,
            Alpha = 0.18 + Random.NextDouble() * 0.3,
            Kind = roll < 0.4 ? Kind.Heart : roll < 0.7 ? Kind.Paw : Kind.Sparkle
        };
    }

    private void StartTrail(double w, double h)
    {
        var start = new Point(Random.NextDouble() * w * 0.4, h * (0.4 + Random.NextDouble() * 0.5));
        var angle = -0.5 - Random.NextDouble() * 0.5;
        for (var i = 0; i < 10; i++)
        {
            var side = i % 2 == 0 ? -1 : 1;
            var along = i * 34;
            var x = start.X + Math.Cos(angle) * along - Math.Sin(angle) * side * 9;
            var y = start.Y + Math.Sin(angle) * along + Math.Cos(angle) * side * 9;
            _prints.Add(new Print { At = new Point(x, y), Angle = angle * 180 / Math.PI + 90, Delay = i * 0.35 });
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0) return;
        var accent = ResourceColor("AccentBrush", Color.FromRgb(255, 95, 168));
        var soft = ResourceColor("TextSecondary", Color.FromRgb(240, 170, 210));

        PushShellClip(dc);

        foreach (var p in _prints)
        {
            var t = p.Age - p.Delay;
            if (t < 0) continue;
            var alpha = 0.35 * Math.Clamp(1 - t / 3.2, 0, 1);
            DrawShape(dc, Kind.Paw, p.At, 9, p.Angle, Fill(soft, alpha));
        }

        foreach (var f in _floaters)
        {
            var color = f.Kind == Kind.Sparkle ? Colors.White : f.Kind == Kind.Heart ? accent : soft;
            var twinkle = f.Kind == Kind.Sparkle ? 0.6 + 0.4 * Math.Sin(_time * 4 + f.Phase) : 1;
            DrawShape(dc, f.Kind, new Point(f.X, f.Y), f.Size, f.Rotation, Fill(color, f.Alpha * twinkle));
        }

        foreach (var p in _petals)
        {
            dc.PushTransform(new TranslateTransform(p.X, p.Y));
            dc.PushTransform(new RotateTransform(p.Rotation));
            dc.PushTransform(new ScaleTransform(p.Size * Math.Abs(Math.Cos(p.Flip)) + 1, p.Size));
            dc.DrawGeometry(Fill(Color.FromRgb(255, 183, 213), 0.75), null, PetalShape);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        dc.Pop();
    }

    private static void DrawShape(DrawingContext dc, Kind kind, Point at, double size, double rotation, Brush brush)
    {
        dc.PushTransform(new TranslateTransform(at.X, at.Y));
        dc.PushTransform(new RotateTransform(rotation));
        dc.PushTransform(new ScaleTransform(size, size));
        dc.DrawGeometry(brush, null, kind switch { Kind.Heart => HeartShape, Kind.Paw => PawShape, _ => SparkleShape });
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    // Unit-size shapes centred on the origin.
    private static readonly Geometry HeartShape = Freeze(Geometry.Parse("M 0,0.9 C -1.4,0 -1.1,-1 -0.5,-1 C -0.2,-1 0,-0.8 0,-0.6 C 0,-0.8 0.2,-1 0.5,-1 C 1.1,-1 1.4,0 0,0.9 Z"));
    private static readonly Geometry PetalShape = Freeze(Geometry.Parse("M 0,-1 C 0.7,-0.6 0.6,0.6 0,1 C -0.6,0.6 -0.7,-0.6 0,-1 Z M 0,-1 L 0.15,-0.7 L 0,-0.8 L -0.15,-0.7 Z"));
    private static readonly Geometry SparkleShape = Freeze(Geometry.Parse("M 0,-1 Q 0.12,-0.12 1,0 Q 0.12,0.12 0,1 Q -0.12,0.12 -1,0 Q -0.12,-0.12 0,-1 Z"));
    private static readonly Geometry PawShape = Freeze(new GeometryGroup
    {
        Children =
        {
            new EllipseGeometry(new Point(0, 0.35), 0.55, 0.45),
            new EllipseGeometry(new Point(-0.62, -0.25), 0.2, 0.26),
            new EllipseGeometry(new Point(-0.22, -0.62), 0.2, 0.27),
            new EllipseGeometry(new Point(0.22, -0.62), 0.2, 0.27),
            new EllipseGeometry(new Point(0.62, -0.25), 0.2, 0.26)
        }
    });

    private static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }
}
