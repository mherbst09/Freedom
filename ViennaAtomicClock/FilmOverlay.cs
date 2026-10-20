using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ViennaAtomicClock;

/// <summary>
/// Click-through layer that makes the window look like an old projected war film:
/// moving grain, dust specks and hairs, vertical scratches, gentle flicker and a dark vignette.
/// Runs at 24 frames per second like a film projector.
/// </summary>
internal sealed class FilmOverlay : FrameworkElement
{
    private const int GrainSize = 200;

    private sealed class Scratch
    {
        public double X, Top, Bottom, Thickness, Alpha, Drift;
        public int FramesLeft;
        public bool Light;
    }

    private readonly ImageBrush[] _grain;
    private readonly List<Scratch> _scratches = new();
    private readonly List<(Point At, double Size, bool Light)> _specks = new();
    private readonly List<Geometry> _hairs = new();
    private readonly Random _random = new();
    private readonly DispatcherTimer _timer;
    private readonly Brush _vignette;
    private int _grainIndex;
    private Point _grainOffset;
    private double _flicker;
    private bool _bright;

    public FilmOverlay()
    {
        IsHitTestVisible = false;
        _grain = Enumerable.Range(0, 6).Select(_ => CreateGrain()).ToArray();

        var vignette = new RadialGradientBrush
        {
            RadiusX = 0.72, RadiusY = 0.72,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.5),
                new GradientStop(Color.FromArgb(70, 0, 0, 0), 0.85),
                new GradientStop(Color.FromArgb(150, 0, 0, 0), 1.05)
            }
        };
        vignette.Freeze();
        _vignette = vignette;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000 / 24.0) };
        _timer.Tick += (_, _) => NextFrame();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _timer.Start();
            else _timer.Stop();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Corner radius of the window shell, so the effect does not spill past the rounded corners.</summary>
    public double Radius { get; set; } = 9;

    private ImageBrush CreateGrain()
    {
        var pixels = new byte[GrainSize * GrainSize * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Roughly gaussian grain, light and dark, stored premultiplied.
            var v = _random.NextDouble() + _random.NextDouble() + _random.NextDouble() - 1.5;
            var alpha = (byte)Math.Min(255, Math.Abs(v) * 120);
            var shade = v > 0 ? alpha : (byte)0;
            pixels[i] = shade;
            pixels[i + 1] = shade;
            pixels[i + 2] = shade;
            pixels[i + 3] = alpha;
        }

        var bitmap = BitmapSource.Create(GrainSize, GrainSize, 96, 96, PixelFormats.Pbgra32, null, pixels, GrainSize * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, GrainSize, GrainSize),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }

    private void NextFrame()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        _grainIndex = _random.Next(_grain.Length);
        _grainOffset = new Point(_random.NextDouble() * GrainSize, _random.NextDouble() * GrainSize);

        // Kept below a 10 % brightness swing so the flicker never counts as flashing.
        _flicker = 0.015 + _random.NextDouble() * 0.045;
        _bright = _random.NextDouble() < 0.04;

        foreach (var scratch in _scratches)
        {
            scratch.FramesLeft--;
            scratch.X += scratch.Drift + (_random.NextDouble() - 0.5) * 1.6;
        }
        _scratches.RemoveAll(s => s.FramesLeft <= 0 || s.X < 0 || s.X > width);
        if (_scratches.Count < 3 && _random.NextDouble() < 0.05)
        {
            var full = _random.NextDouble() < 0.6;
            var top = full ? 0 : _random.NextDouble() * height * 0.6;
            _scratches.Add(new Scratch
            {
                X = _random.NextDouble() * width,
                Top = top,
                Bottom = full ? height : top + height * (0.2 + _random.NextDouble() * 0.4),
                Thickness = 0.6 + _random.NextDouble() * 0.9,
                Alpha = 0.15 + _random.NextDouble() * 0.3,
                Drift = (_random.NextDouble() - 0.5) * 2,
                FramesLeft = 6 + _random.Next(40),
                Light = _random.NextDouble() < 0.55
            });
        }

        // Dust and hairs only live for a single frame, like dirt on a real print.
        _specks.Clear();
        var specks = _random.Next(0, 5);
        for (var i = 0; i < specks; i++)
            _specks.Add((new Point(_random.NextDouble() * width, _random.NextDouble() * height),
                0.6 + Math.Pow(_random.NextDouble(), 3) * 3.5, _random.NextDouble() < 0.3));

        _hairs.Clear();
        if (_random.NextDouble() < 0.06)
        {
            var start = new Point(_random.NextDouble() * width, _random.NextDouble() * height);
            var length = 20 + _random.NextDouble() * 60;
            var hair = new StreamGeometry();
            using (var ctx = hair.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.BezierTo(
                    new Point(start.X + (_random.NextDouble() - 0.5) * length, start.Y + _random.NextDouble() * length),
                    new Point(start.X + (_random.NextDouble() - 0.5) * length, start.Y + _random.NextDouble() * length),
                    new Point(start.X + (_random.NextDouble() - 0.5) * length, start.Y + length), true, true);
            }
            hair.Freeze();
            _hairs.Add(hair);
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        dc.PushClip(new RectangleGeometry(bounds, Radius, Radius));

        dc.PushOpacity(0.2);
        dc.PushTransform(new TranslateTransform(-_grainOffset.X, -_grainOffset.Y));
        dc.DrawRectangle(_grain[_grainIndex], null, new Rect(_grainOffset, bounds.Size));
        dc.Pop();
        dc.Pop();

        dc.DrawRectangle(_bright ? WhiteVeil : null, null, bounds);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(_flicker * 255), 0, 0, 0)), null, bounds);

        foreach (var scratch in _scratches)
        {
            var color = scratch.Light ? Color.FromArgb((byte)(scratch.Alpha * 255), 235, 230, 220) : Color.FromArgb((byte)(scratch.Alpha * 255), 10, 10, 10);
            dc.DrawLine(new Pen(new SolidColorBrush(color), scratch.Thickness), new Point(scratch.X, scratch.Top), new Point(scratch.X, scratch.Bottom));
        }

        foreach (var (at, size, light) in _specks)
            dc.DrawEllipse(light ? DustLight : DustDark, null, at, size, size * (0.7 + (at.X % 3) * 0.15));

        foreach (var hair in _hairs)
            dc.DrawGeometry(null, HairPen, hair);

        dc.DrawRectangle(_vignette, null, bounds);
        dc.Pop();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static readonly Brush WhiteVeil = Frozen(Color.FromArgb(10, 255, 250, 235));
    private static readonly Brush DustDark = Frozen(Color.FromArgb(150, 8, 8, 8));
    private static readonly Brush DustLight = Frozen(Color.FromArgb(120, 240, 235, 225));
    private static readonly Pen HairPen = CreateHairPen();

    private static Pen CreateHairPen()
    {
        var pen = new Pen(Frozen(Color.FromArgb(120, 10, 10, 10)), 0.8);
        pen.Freeze();
        return pen;
    }
}
