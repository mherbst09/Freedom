using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ViennaAtomicClock.Weather;

/// <summary>
/// Self-drawn weather symbols on a 64×64 canvas. Every drawing is built once per condition and frozen,
/// so lists with hundreds of icons cost nothing extra.
/// </summary>
internal static class WeatherIcons
{
    private static readonly Dictionary<(WeatherCondition, bool), DrawingImage> Images = new();
    private static readonly Dictionary<(WeatherCondition, bool), (Drawing? Back, Drawing? Front)> LayerCache = new();

    private static readonly Brush SunFill = Frozen(Color.FromRgb(0xFF, 0xC8, 0x3A));
    private static readonly Pen SunPen = FrozenPen(Color.FromRgb(0xF2, 0x9E, 0x0C), 2.6);
    private static readonly Brush MoonFill = Frozen(Color.FromRgb(0xEC, 0xEA, 0xF8));
    private static readonly Pen MoonPen = FrozenPen(Color.FromRgb(0x8E, 0x8C, 0xB0), 1.6);
    private static readonly Brush CloudFill = Frozen(Color.FromRgb(0xF1, 0xF4, 0xF8));
    private static readonly Pen CloudPen = FrozenPen(Color.FromRgb(0x86, 0x93, 0xA5), 1.8);
    private static readonly Brush DarkCloudFill = Frozen(Color.FromRgb(0xB8, 0xC1, 0xCD));
    private static readonly Pen DarkCloudPen = FrozenPen(Color.FromRgb(0x5F, 0x6B, 0x7B), 1.8);
    private static readonly Pen RainPen = FrozenPen(Color.FromRgb(0x2F, 0x84, 0xF5), 3, PenLineCap.Round);
    private static readonly Pen DrizzlePen = FrozenPen(Color.FromRgb(0x4A, 0x9B, 0xF7), 2.2, PenLineCap.Round);
    private static readonly Pen IcePen = FrozenPen(Color.FromRgb(0x63, 0xC7, 0xE8), 3, PenLineCap.Round);
    private static readonly Brush SnowFill = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Pen SnowPen = FrozenPen(Color.FromRgb(0x5E, 0x9E, 0xE0), 1.4);
    private static readonly Brush BoltFill = Frozen(Color.FromRgb(0xFF, 0xB8, 0x1C));
    private static readonly Pen BoltPen = FrozenPen(Color.FromRgb(0xC9, 0x7A, 0x00), 1.2);
    private static readonly Pen FogPen = FrozenPen(Color.FromRgb(0x93, 0xA0, 0xB0), 3.2, PenLineCap.Round);

    private static readonly Geometry CloudShape = BuildCloud();

    public static DrawingImage Image(WeatherCondition condition, bool isDay)
    {
        if (Images.TryGetValue((condition, isDay), out var image)) return image;
        var (back, front) = Layers(condition, isDay);
        var group = new DrawingGroup();
        // A transparent frame keeps every icon the same size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));
        if (back is not null) group.Children.Add(back);
        if (front is not null) group.Children.Add(front);
        group.Freeze();
        image = new DrawingImage(group);
        image.Freeze();
        Images[(condition, isDay)] = image;
        return image;
    }

    /// <summary>Sun/moon (back) and clouds with precipitation (front), drawn separately for the animated header icon.</summary>
    public static (Drawing? Back, Drawing? Front) Layers(WeatherCondition condition, bool isDay)
    {
        if (LayerCache.TryGetValue((condition, isDay), out var cached)) return cached;
        Drawing? back = null;
        var front = new DrawingGroup();

        switch (condition)
        {
            case WeatherCondition.Clear:
                back = isDay ? Sun(32, 32, 13) : Moon(32, 32, 16);
                break;
            case WeatherCondition.MostlyClear:
                back = isDay ? Sun(30, 28, 12) : Moon(30, 28, 15);
                AddCloud(front, 34, 40, 0.55, false);
                break;
            case WeatherCondition.PartlyCloudy:
                back = isDay ? Sun(24, 22, 10) : Moon(24, 22, 12);
                AddCloud(front, 34, 38, 0.8, false);
                break;
            case WeatherCondition.Cloudy:
                AddCloud(front, 24, 28, 0.6, true);
                AddCloud(front, 34, 36, 0.9, false);
                break;
            case WeatherCondition.Fog:
                AddCloud(front, 32, 26, 0.75, false);
                for (var i = 0; i < 3; i++)
                    front.Children.Add(Line(FogPen, 12 + i * 3, 44 + i * 7, 52 - i * 2, 44 + i * 7));
                break;
            case WeatherCondition.Drizzle:
                AddCloud(front, 32, 28, 0.85, false);
                AddDrops(front, DrizzlePen, 3, 4.5);
                break;
            case WeatherCondition.Rain:
            case WeatherCondition.Showers:
                if (condition == WeatherCondition.Showers) back = isDay ? Sun(22, 18, 9) : Moon(22, 18, 11);
                AddCloud(front, 32, 28, 0.9, condition == WeatherCondition.Rain);
                AddDrops(front, RainPen, 3, 7);
                break;
            case WeatherCondition.HeavyRain:
                AddCloud(front, 32, 26, 0.95, true);
                AddDrops(front, RainPen, 4, 9);
                break;
            case WeatherCondition.FreezingRain:
                AddCloud(front, 32, 28, 0.9, true);
                AddDrops(front, IcePen, 2, 7);
                AddFlakes(front, 1, 40);
                break;
            case WeatherCondition.Snow:
            case WeatherCondition.SnowShowers:
                if (condition == WeatherCondition.SnowShowers) back = isDay ? Sun(22, 18, 9) : Moon(22, 18, 11);
                AddCloud(front, 32, 28, 0.9, false);
                AddFlakes(front, 3, 32);
                break;
            case WeatherCondition.HeavySnow:
                AddCloud(front, 32, 26, 0.95, true);
                AddFlakes(front, 5, 30);
                break;
            case WeatherCondition.Thunderstorm:
            case WeatherCondition.Hail:
                AddCloud(front, 32, 26, 0.95, true);
                front.Children.Add(new GeometryDrawing(BoltFill, BoltPen, Geometry.Parse("M 34,38 L 26,50 L 32,50 L 28,61 L 40,46 L 34,46 L 38,38 Z")));
                if (condition == WeatherCondition.Hail)
                    foreach (var x in new[] { 18.0, 46 })
                        front.Children.Add(new GeometryDrawing(SnowFill, SnowPen, new EllipseGeometry(new Point(x, 52), 3.2, 3.2)));
                else
                    AddDrops(front, RainPen, 2, 7, 14);
                break;
        }

        if (back is not null && back.CanFreeze) back.Freeze();
        Drawing? frontDrawing = front.Children.Count > 0 ? front : null;
        frontDrawing?.Freeze();
        var result = (back, frontDrawing);
        LayerCache[(condition, isDay)] = result;
        return result;
    }

    private static Drawing Sun(double x, double y, double r)
    {
        var group = new DrawingGroup();
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            group.Children.Add(Line(SunPen, x + Math.Cos(a) * (r + 4), y + Math.Sin(a) * (r + 4), x + Math.Cos(a) * (r + 9), y + Math.Sin(a) * (r + 9)));
        }
        group.Children.Add(new GeometryDrawing(SunFill, SunPen, new EllipseGeometry(new Point(x, y), r, r)));
        return group;
    }

    private static Drawing Moon(double x, double y, double r)
    {
        var disc = new EllipseGeometry(new Point(x, y), r, r);
        var cut = new EllipseGeometry(new Point(x + r * 0.55, y - r * 0.35), r * 0.85, r * 0.85);
        return new GeometryDrawing(MoonFill, MoonPen, new CombinedGeometry(GeometryCombineMode.Exclude, disc, cut));
    }

    private static void AddCloud(DrawingGroup group, double x, double y, double scale, bool dark)
    {
        var shape = CloudShape.Clone();
        shape.Transform = new MatrixTransform(scale, 0, 0, scale, x - 32 * scale, y - 30 * scale);
        group.Children.Add(new GeometryDrawing(dark ? DarkCloudFill : CloudFill, dark ? DarkCloudPen : CloudPen, shape));
    }

    private static void AddDrops(DrawingGroup group, Pen pen, int count, double length, double startX = 20)
    {
        var spacing = (44 - startX) / Math.Max(1, count - 1);
        for (var i = 0; i < count; i++)
        {
            var x = startX + i * spacing + (count == 1 ? 10 : 0);
            var y = 46 + (i % 2) * 4;
            group.Children.Add(Line(pen, x, y, x - length * 0.35, y + length));
        }
    }

    private static void AddFlakes(DrawingGroup group, int count, double startX)
    {
        var positions = new (double X, double Y)[] { (20, 50), (32, 56), (44, 50), (26, 60), (38, 61) };
        foreach (var (x, y) in positions.Take(count))
        {
            var flake = new GeometryGroup();
            for (var i = 0; i < 3; i++)
            {
                var a = i * Math.PI / 3;
                flake.Children.Add(new LineGeometry(new Point(x - Math.Cos(a) * 4, y - Math.Sin(a) * 4), new Point(x + Math.Cos(a) * 4, y + Math.Sin(a) * 4)));
            }
            group.Children.Add(new GeometryDrawing(null, SnowPen, flake));
            group.Children.Add(new GeometryDrawing(SnowFill, null, new EllipseGeometry(new Point(x, y), 1.4, 1.4)));
        }
    }

    private static Drawing Line(Pen pen, double x1, double y1, double x2, double y2) =>
        new GeometryDrawing(null, pen, new LineGeometry(new Point(x1, y1), new Point(x2, y2)));

    /// <summary>A soft cumulus outline centred at (32, 30) roughly 52×28 large.</summary>
    private static Geometry BuildCloud()
    {
        Geometry shape = new RectangleGeometry(new Rect(10, 28, 44, 16), 8, 8);
        foreach (var (cx, cy, r) in new[] { (22.0, 30.0, 10.0), (34.0, 24.0, 13.0), (45.0, 31.0, 9.0) })
            shape = new CombinedGeometry(GeometryCombineMode.Union, shape, new EllipseGeometry(new Point(cx, cy), r, r));
        var flat = shape.GetFlattenedPathGeometry(0.2, ToleranceType.Absolute);
        flat.Freeze();
        return flat;
    }

    public static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness, PenLineCap cap = PenLineCap.Flat)
    {
        var pen = new Pen(Frozen(color), thickness) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    /// <summary>Small static icon element.</summary>
    public static Image Small(WeatherCondition condition, bool isDay, double size = 30) => new()
    {
        Source = Image(condition, isDay),
        Width = size,
        Height = size,
        ToolTip = WeatherText.Condition(condition, isDay)
    };
}

/// <summary>Large header icon: the sun slowly turns and the clouds float (both via composition animations, no per-frame code).</summary>
internal sealed class AnimatedWeatherIcon : Grid
{
    private readonly Image _back = new() { Stretch = Stretch.Uniform };
    private readonly Image _front = new() { Stretch = Stretch.Uniform };
    private readonly RotateTransform _turn = new();
    private readonly TranslateTransform _float = new();
    private (WeatherCondition, bool)? _shown;

    public AnimatedWeatherIcon()
    {
        _back.RenderTransformOrigin = new Point(0.5, 0.5);
        _back.RenderTransform = _turn;
        _front.RenderTransform = _float;
        Children.Add(_back);
        Children.Add(_front);
        IsHitTestVisible = false;
    }

    public void Show(WeatherCondition condition, bool isDay, bool animate)
    {
        if (_shown != (condition, isDay))
        {
            _shown = (condition, isDay);
            var (back, front) = WeatherIcons.Layers(condition, isDay);
            _back.Source = back is null ? null : Wrap(back);
            _front.Source = front is null ? null : Wrap(front);
        }

        if (animate)
        {
            // Only a sun turns (a moon would look odd); the rotation centre is the sun, which sits in the middle when it is alone.
            var sunAlone = isDay && condition == WeatherCondition.Clear;
            if (sunAlone) _turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(90)) { RepeatBehavior = RepeatBehavior.Forever });
            else _turn.BeginAnimation(RotateTransform.AngleProperty, null);
            _float.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-2.5, 2.5, TimeSpan.FromSeconds(4.5))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
        else
        {
            _turn.BeginAnimation(RotateTransform.AngleProperty, null);
            _float.BeginAnimation(TranslateTransform.XProperty, null);
            _turn.Angle = 0;
            _float.X = 0;
        }
    }

    private static DrawingImage Wrap(Drawing drawing)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));
        group.Children.Add(drawing);
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
