using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ViennaAtomicClock.Weather;

/// <summary>Base for the small charts: theme colours and a short "draw in" reveal (Progress 0 → 1).</summary>
internal abstract class ChartBase : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ChartBase), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    protected ChartBase()
    {
        SnapsToDevicePixels = true;
        IsVisibleChanged += (_, _) => { if (IsVisible && RevealOnShow) Reveal(); };
    }

    /// <summary>Set to false when animations are switched off.</summary>
    public static bool AnimationsEnabled { get; set; } = true;

    public bool RevealOnShow { get; set; } = true;

    public void Reveal()
    {
        if (!AnimationsEnabled || !SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(ProgressProperty, null);
            Progress = 1;
            return;
        }
        BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    protected Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    protected Pen PenOf(string key, double thickness, bool dashed = false)
    {
        var pen = new Pen(Res(key), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dashed) pen.DashStyle = new DashStyle(new[] { 2.0, 3.0 }, 0);
        pen.Freeze();
        return pen;
    }

    protected FormattedText Label(string text, double size, string brush = "TextMuted") =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface((TryFindResource("UiFont") as FontFamily) ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, Res(brush), VisualTreeHelper.GetDpi(this).PixelsPerDip);
}

/// <summary>Line (optionally filled) with x labels, e.g. temperature, UV or pressure over hours.</summary>
internal sealed class LineChart : ChartBase
{
    private double[] _values = Array.Empty<double>();
    private string[] _labels = Array.Empty<string>();
    private string[] _valueLabels = Array.Empty<string>();

    public string LineBrush { get; set; } = "AccentBrush";
    public bool Fill { get; set; } = true;
    public int MarkIndex { get; set; } = -1;
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }

    public void SetData(IReadOnlyList<double> values, IReadOnlyList<string>? labels = null, IReadOnlyList<string>? valueLabels = null)
    {
        _values = values.ToArray();
        _labels = labels?.ToArray() ?? Array.Empty<string>();
        _valueLabels = valueLabels?.ToArray() ?? Array.Empty<string>();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_values.Length < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var labelSpace = _labels.Length > 0 ? 16 : 0;
        var top = _valueLabels.Length > 0 ? 16 : 4;
        var height = ActualHeight - labelSpace - top;
        var width = ActualWidth;
        var min = Minimum ?? _values.Min();
        var max = Maximum ?? _values.Max();
        if (max - min < 0.001) { max += 0.5; min -= 0.5; }
        Point P(int i) => new(i * width / (_values.Length - 1), top + height - (_values[i] - min) / (max - min) * height);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width * Progress, ActualHeight)));
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            for (var i = 1; i < _values.Length; i++) ctx.LineTo(P(i), true, true);
        }
        line.Freeze();

        if (Fill)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(0, top + height), true, true);
                for (var i = 0; i < _values.Length; i++) ctx.LineTo(P(i), false, true);
                ctx.LineTo(new Point(width, top + height), false, false);
            }
            area.Freeze();
            var color = (Res(LineBrush) as SolidColorBrush)?.Color ?? Colors.SteelBlue;
            var fill = new LinearGradientBrush(Color.FromArgb(90, color.R, color.G, color.B), Color.FromArgb(8, color.R, color.G, color.B), 90);
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);
        }
        dc.DrawGeometry(null, PenOf(LineBrush, 2.2), line);

        if (MarkIndex >= 0 && MarkIndex < _values.Length)
        {
            var mark = P(MarkIndex);
            dc.DrawEllipse(Res("SurfaceBrush"), PenOf(LineBrush, 2.2), mark, 4.5, 4.5);
        }

        var step = Math.Max(1, (int)Math.Ceiling(_labels.Length / Math.Max(1, width / 46)));
        for (var i = 0; i < _labels.Length && i < _values.Length; i += step)
        {
            var text = Label(_labels[i], 10.5);
            var x = Math.Clamp(P(i).X - text.Width / 2, 0, Math.Max(0, width - text.Width));
            dc.DrawText(text, new Point(x, ActualHeight - labelSpace + 2));
            if (i < _valueLabels.Length)
            {
                var value = Label(_valueLabels[i], 10.5, "TextSecondary");
                dc.DrawText(value, new Point(Math.Clamp(P(i).X - value.Width / 2, 0, Math.Max(0, width - value.Width)), Math.Max(0, P(i).Y - 16)));
            }
        }
        dc.Pop();
    }
}

/// <summary>Vertical bars, e.g. precipitation per 5 minutes or per hour.</summary>
internal sealed class BarChart : ChartBase
{
    private double[] _values = Array.Empty<double>();
    private string[] _labels = Array.Empty<string>();

    public string BarBrush { get; set; } = "AccentBrush";
    /// <summary>Values at or above this fill the full height.</summary>
    public double FullScale { get; set; } = 1;
    /// <summary>Optional guide lines (fractions of the height) with labels, e.g. light/moderate/heavy.</summary>
    public (double Fraction, string Text)[] Guides { get; set; } = Array.Empty<(double, string)>();

    public void SetData(IReadOnlyList<double> values, IReadOnlyList<string>? labels = null)
    {
        _values = values.ToArray();
        _labels = labels?.ToArray() ?? Array.Empty<string>();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_values.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var labelSpace = _labels.Length > 0 ? 16 : 0;
        var height = ActualHeight - labelSpace;
        var slot = ActualWidth / _values.Length;
        var guidePen = PenOf("CardBorder", 1, dashed: true);
        foreach (var (fraction, text) in Guides)
        {
            var y = height - fraction * height;
            dc.DrawLine(guidePen, new Point(0, y), new Point(ActualWidth, y));
            dc.DrawText(Label(text, 9.5), new Point(2, y - 13));
        }

        var brush = Res(BarBrush);
        var track = Res("TrackBrush");
        for (var i = 0; i < _values.Length; i++)
        {
            var share = Math.Clamp(_values[i] / FullScale, 0, 1) * Progress;
            var x = i * slot + slot * 0.18;
            var w = Math.Max(1, slot * 0.64);
            dc.DrawRoundedRectangle(track, null, new Rect(x, height - 3, w, 3), 1.5, 1.5);
            if (share > 0.001)
            {
                var h = Math.Max(3, share * height);
                dc.DrawRoundedRectangle(brush, null, new Rect(x, height - h, w, h), Math.Min(3, w / 2), Math.Min(3, w / 2));
            }
        }

        var step = Math.Max(1, (int)Math.Ceiling(_labels.Length / Math.Max(1, ActualWidth / 46)));
        for (var i = 0; i < _labels.Length; i += step)
        {
            if (_labels[i].Length == 0) continue;
            var text = Label(_labels[i], 10.5);
            dc.DrawText(text, new Point(Math.Clamp(i * slot + slot / 2 - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width)), ActualHeight - labelSpace + 2));
        }
    }
}

/// <summary>Coloured scale with a marker (air quality, UV index).</summary>
internal sealed class ScaleBar : ChartBase
{
    private readonly Brush _gradient;
    public double Position { get; set; }

    public ScaleBar(params Color[] colors)
    {
        var stops = new GradientStopCollection();
        for (var i = 0; i < colors.Length; i++) stops.Add(new GradientStop(colors[i], i / (double)Math.Max(1, colors.Length - 1)));
        var brush = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0));
        brush.Freeze();
        _gradient = brush;
        Height = 12;
    }

    public static ScaleBar AirQuality() => new(Color.FromRgb(0x50, 0xC8, 0x78), Color.FromRgb(0x50, 0xB4, 0xE6), Color.FromRgb(0xF0, 0xDC, 0x3C),
        Color.FromRgb(0xFF, 0x96, 0x32), Color.FromRgb(0xE6, 0x3C, 0x3C), Color.FromRgb(0x96, 0x32, 0x96));

    public static ScaleBar Uv() => new(Color.FromRgb(0x4C, 0xB8, 0x48), Color.FromRgb(0xF4, 0xD0, 0x30), Color.FromRgb(0xF5, 0x8B, 0x22),
        Color.FromRgb(0xE2, 0x3A, 0x33), Color.FromRgb(0x9B, 0x4D, 0xCA));

    protected override void OnRender(DrawingContext dc)
    {
        var h = 6.0;
        var y = (ActualHeight - h) / 2;
        dc.DrawRoundedRectangle(_gradient, null, new Rect(0, y, ActualWidth, h), 3, 3);
        var x = Math.Clamp(Position, 0, 1) * Progress * (ActualWidth - 10) + 5;
        dc.DrawEllipse(Brushes.White, PenOf("TextPrimary", 2), new Point(x, ActualHeight / 2), 5, 5);
    }
}

/// <summary>Half circle from sunrise to sunset with the current sun position.</summary>
internal sealed class SunArc : ChartBase
{
    public DateTimeOffset? Sunrise { get; set; }
    public DateTimeOffset? Sunset { get; set; }
    public DateTimeOffset Now { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var radius = Math.Min(ActualWidth / 2 - 8, ActualHeight - 12);
        var center = new Point(ActualWidth / 2, ActualHeight - 6);
        dc.DrawLine(PenOf("CardBorder", 1.2), new Point(0, center.Y), new Point(ActualWidth, center.Y));

        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(new Point(center.X - radius, center.Y), false, false);
            ctx.ArcTo(new Point(center.X + radius, center.Y), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, PenOf("TextMuted", 1.6, dashed: true), arc);

        if (Sunrise is not { } rise || Sunset is not { } set || set <= rise) return;
        var share = (Now - rise).TotalSeconds / (set - rise).TotalSeconds;
        var up = share is >= 0 and <= 1;
        var t = Math.Clamp(share, 0, 1) * Progress;
        var angle = Math.PI * (1 - t);
        var sun = new Point(center.X + Math.Cos(angle) * radius, center.Y - Math.Sin(angle) * radius);

        if (up)
        {
            var travelled = new StreamGeometry();
            using (var ctx = travelled.Open())
            {
                ctx.BeginFigure(new Point(center.X - radius, center.Y), false, false);
                ctx.ArcTo(sun, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            travelled.Freeze();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0xB0, 0x1C)), 2.4), travelled);
        }
        dc.DrawEllipse(new SolidColorBrush(up ? Color.FromRgb(0xFF, 0xC8, 0x3A) : Color.FromRgb(0x9A, 0xA4, 0xB2)), PenOf("SurfaceBrush", 2), up ? sun : new Point(center.X + (share < 0 ? -radius : radius), center.Y), 7, 7);
    }
}

/// <summary>Compass rose with a needle that turns smoothly to the wind direction.</summary>
internal sealed class WindCompass : ChartBase
{
    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(
        nameof(Angle), typeof(double), typeof(WindCompass), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Angle
    {
        get => (double)GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    public string CenterText { get; set; } = "";
    public string[] Cardinals { get; set; } = { "N", "E", "S", "W" };

    public void TurnTo(double degrees)
    {
        if (!AnimationsEnabled || !SystemParameters.ClientAreaAnimation)
        {
            Angle = degrees;
            return;
        }
        // Take the short way round.
        var from = Angle % 360;
        var delta = ((degrees - from + 540) % 360) - 180;
        BeginAnimation(AngleProperty, new DoubleAnimation(from, from + delta, TimeSpan.FromMilliseconds(340)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size / 2 - 2;
        dc.DrawEllipse(null, PenOf("CardBorder", 1.4), center, r, r);
        var tick = PenOf("TextMuted", 1);
        for (var i = 0; i < 36; i++)
        {
            var a = i * Math.PI / 18;
            var inner = i % 9 == 0 ? r - 8 : r - 4;
            dc.DrawLine(tick, new Point(center.X + Math.Sin(a) * inner, center.Y - Math.Cos(a) * inner), new Point(center.X + Math.Sin(a) * r, center.Y - Math.Cos(a) * r));
        }
        for (var i = 0; i < 4; i++)
        {
            var a = i * Math.PI / 2;
            var label = Label(Cardinals[i], 10, "TextSecondary");
            dc.DrawText(label, new Point(center.X + Math.Sin(a) * (r - 17) - label.Width / 2, center.Y - Math.Cos(a) * (r - 17) - label.Height / 2));
        }

        // Arrow points where the wind blows to (from the direction it comes from).
        var rad = (Angle + 180) * Math.PI / 180;
        var tip = new Point(center.X + Math.Sin(rad) * (r - 6), center.Y - Math.Cos(rad) * (r - 6));
        var tail = new Point(center.X - Math.Sin(rad) * (r - 6), center.Y + Math.Cos(rad) * (r - 6));
        var accent = PenOf("AccentBrush", 2.6);
        dc.DrawLine(accent, tail, tip);
        var left = new Point(tip.X - Math.Sin(rad - 0.45) * 10, tip.Y + Math.Cos(rad - 0.45) * 10);
        var right = new Point(tip.X - Math.Sin(rad + 0.45) * 10, tip.Y + Math.Cos(rad + 0.45) * 10);
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(left, true, true);
            ctx.LineTo(right, true, true);
        }
        head.Freeze();
        dc.DrawGeometry(Res("AccentBrush"), null, head);
        dc.DrawEllipse(Res("SurfaceBrush"), PenOf("AccentBrush", 2), center, 14, 14);
        if (CenterText.Length > 0)
        {
            var text = Label(CenterText, 10, "TextPrimary");
            dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        }
    }
}

/// <summary>Moon disc with the lit part for a phase (0 new, 0.5 full).</summary>
internal sealed class MoonPhaseGlyph : FrameworkElement
{
    private static readonly Brush Lit = WeatherIcons.Frozen(Color.FromRgb(0xF3, 0xF1, 0xE4));
    private static readonly Brush Dark = WeatherIcons.Frozen(Color.FromRgb(0x3A, 0x3F, 0x4D));
    private static readonly Pen Rim = MakeRim();
    private double _phase;

    public double Phase
    {
        get => _phase;
        set { _phase = value; InvalidateVisual(); }
    }

    private static Pen MakeRim()
    {
        var pen = new Pen(WeatherIcons.Frozen(Color.FromRgb(0x8C, 0x93, 0xA3)), 1);
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var r = Math.Min(ActualWidth, ActualHeight) / 2 - 1;
        if (r <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(Dark, Rim, c, r, r);
        // The terminator is an ellipse with a horizontal radius of |cos(2π·phase)|·r.
        var waxing = _phase < 0.5;
        var k = Math.Cos(2 * Math.PI * _phase);
        var figure = new StreamGeometry();
        using (var ctx = figure.Open())
        {
            var topPoint = new Point(c.X, c.Y - r);
            var bottom = new Point(c.X, c.Y + r);
            ctx.BeginFigure(topPoint, true, true);
            // Outer limb on the lit side.
            ctx.ArcTo(bottom, new Size(r, r), 0, false, waxing ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true);
            // Terminator back to the top.
            var rx = Math.Max(0.01, Math.Abs(k) * r);
            var bulgesOut = k < 0; // more than half lit
            ctx.ArcTo(topPoint, new Size(rx, r), 0, false,
                waxing == bulgesOut ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true);
        }
        figure.Freeze();
        dc.DrawGeometry(Lit, null, figure);
    }
}
