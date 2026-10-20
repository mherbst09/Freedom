using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ViennaAtomicClock;

/// <summary>
/// The sad counterpart to the fireworks: when a lesson starts, a click-through overlay plays a gloomy show
/// (storm clouds, rain, lightning, crows, a gravestone for the break, candles, falling leaves) with a
/// synthesized soundtrack (rain, wind, thunder, church bell, funeral march, crows and a sad trombone).
/// </summary>
internal sealed class GloomWindow : Window
{
    public const double ShowSeconds = 15.0;

    private readonly GloomCanvas _canvas;
    private readonly Stopwatch _stopwatch = new();
    private SoundPlayer? _sound;
    private MemoryStream? _soundStream;
    private double _lastFrame;
    private bool _running, _closed;

    public GloomWindow(Rect focusArea)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "Gloom";
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var focus = new Rect(focusArea.X - Left, focusArea.Y - Top, focusArea.Width, focusArea.Height);
        focus.Intersect(new Rect(0, 0, Width, Height));
        if (focus.IsEmpty || focus.Width < 200 || focus.Height < 200) focus = new Rect(0, 0, Width, Height);

        var plan = GloomPlan.Create(focus, Random.Shared);
        _canvas = new GloomCanvas(plan, Width, Height);
        Content = _canvas;

        var soundTask = Task.Run(() =>
        {
            try { return GloomSound.Render(plan); }
            catch { return null; }
        });

        SourceInitialized += (_, _) => MakeClickThrough();
        Loaded += async (_, _) =>
        {
            _soundStream = await soundTask;
            if (_closed) { _soundStream?.Dispose(); return; }
            try
            {
                if (_soundStream is not null)
                {
                    _sound = new SoundPlayer(_soundStream);
                    _sound.Load();
                }
            }
            catch { _sound = null; }
            _running = true;
            _stopwatch.Start();
            try { _sound?.Play(); } catch { /* No audio device. */ }
            CompositionTarget.Rendering += OnRendering;
        };
        Closed += (_, _) =>
        {
            _closed = true;
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnRendering;
            try { _sound?.Stop(); } catch { }
            _sound?.Dispose();
            _soundStream?.Dispose();
        };
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _stopwatch.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastFrame, 0, 0.05);
        _lastFrame = now;
        _canvas.Advance(now, dt);
        if (now >= ShowSeconds + 0.6) Close();
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x20 | 0x80 | 0x80000 | 0x08000000);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

/// <summary>Timeline of the show, shared by picture and sound so thunder follows its lightning.</summary>
internal sealed record GloomPlan(Rect Focus, double[] Lightning, double[] Crows, double[] Bells, double TromboneAt)
{
    public static GloomPlan Create(Rect focus, Random random) => new(
        focus,
        new[] { 2.4 + random.NextDouble() * 0.6, 6.8 + random.NextDouble() * 0.8, 11.0 + random.NextDouble() * 0.6 },
        new[] { 1.2, 5.6, 9.6 },
        new[] { 0.2, 4.2, 8.2 },
        12.4);
}

internal sealed class GloomCanvas : FrameworkElement
{
    private sealed class RainDrop { public double X, Y, Speed, Length; }
    private sealed class Leaf { public double X, Y, Vy, Sway, Phase, Angle, Spin, Size; public Color Color; }
    private sealed class Splash { public double X, Y, Age; }
    private sealed class Drip { public double X, Y, Vy; }

    private readonly GloomPlan _plan;
    private readonly double _width, _height;
    private readonly Rect _focus;
    private readonly Random _random = new();
    private readonly List<RainDrop> _rain = new();
    private readonly List<Leaf> _leaves = new();
    private readonly List<Splash> _splashes = new();
    private readonly List<Drip> _drips = new();
    private readonly (double X, double Y, double R, double Speed)[] _clouds;
    private readonly Dictionary<(Color, int), Brush> _brushes = new();
    private Geometry? _title, _subtitle;
    private Point[][]? _bolt;
    private double _boltAt = -10, _lastWash = -10, _washAt = -10;
    private int _nextStrike;
    private double _time;

    public GloomCanvas(GloomPlan plan, double width, double height)
    {
        _plan = plan;
        _width = width;
        _height = height;
        _focus = plan.Focus;
        IsHitTestVisible = false;
        _clouds = Enumerable.Range(0, 9).Select(i => (
            _focus.X + _focus.Width * (i / 8.0) + _random.NextDouble() * 80,
            _focus.Y + _focus.Height * (0.02 + _random.NextDouble() * 0.12),
            _focus.Width * (0.12 + _random.NextDouble() * 0.1),
            6 + _random.NextDouble() * 10)).ToArray();
        for (var i = 0; i < 420; i++) _rain.Add(NewDrop(anywhere: true));
        for (var i = 0; i < 26; i++) _leaves.Add(NewLeaf(anywhere: true));
    }

    private double Ground => _focus.Bottom - _focus.Height * 0.06;

    private RainDrop NewDrop(bool anywhere) => new()
    {
        X = _random.NextDouble() * (_width + 300) - 150,
        Y = anywhere ? _random.NextDouble() * _height : -40 - _random.NextDouble() * 200,
        Speed = 900 + _random.NextDouble() * 600,
        Length = 14 + _random.NextDouble() * 22
    };

    private Leaf NewLeaf(bool anywhere)
    {
        Color[] colors = { Color.FromRgb(92, 70, 50), Color.FromRgb(70, 64, 58), Color.FromRgb(110, 84, 60), Color.FromRgb(60, 56, 50) };
        return new Leaf
        {
            X = _focus.X + _random.NextDouble() * _focus.Width, Y = anywhere ? _focus.Y + _random.NextDouble() * _focus.Height : _focus.Y - 30,
            Vy = 40 + _random.NextDouble() * 50, Sway = 30 + _random.NextDouble() * 40, Phase = _random.NextDouble() * 6,
            Angle = _random.NextDouble() * 360, Spin = (_random.NextDouble() - 0.5) * 160, Size = 7 + _random.NextDouble() * 7,
            Color = colors[_random.Next(colors.Length)]
        };
    }

    public void Advance(double time, double dt)
    {
        _time = time;
        var raining = time < GloomWindow.ShowSeconds - 0.8;
        for (var i = _rain.Count - 1; i >= 0; i--)
        {
            var d = _rain[i];
            d.Y += d.Speed * dt;
            d.X -= d.Speed * 0.18 * dt;
            if (d.Y > Ground + _random.NextDouble() * 20 && d.X > _focus.X && d.X < _focus.Right)
            {
                if (_random.NextDouble() < 0.25) _splashes.Add(new Splash { X = d.X, Y = d.Y });
                if (raining) _rain[i] = NewDrop(false); else _rain.RemoveAt(i);
            }
            else if (d.Y > _height + 40) { if (raining) _rain[i] = NewDrop(false); else _rain.RemoveAt(i); }
        }
        foreach (var s in _splashes) s.Age += dt;
        _splashes.RemoveAll(s => s.Age > 0.35);

        foreach (var l in _leaves)
        {
            l.Y += l.Vy * dt;
            l.X += Math.Sin(time * 1.4 + l.Phase) * l.Sway * dt - 20 * dt;
            l.Angle += l.Spin * dt;
        }
        for (var i = 0; i < _leaves.Count; i++)
            if (_leaves[i].Y > Ground) _leaves[i] = NewLeaf(false);

        // Black ink drips from the headline.
        if (time > 1.5 && time < GloomWindow.ShowSeconds - 1 && _random.NextDouble() < dt * 6 && _title is not null)
        {
            var b = _title.Bounds;
            _drips.Add(new Drip { X = b.X + _random.NextDouble() * b.Width, Y = b.Bottom - 4, Vy = 10 });
        }
        foreach (var d in _drips) { d.Vy += 260 * dt; d.Y += d.Vy * dt; }
        _drips.RemoveAll(d => d.Y > _height);

        if (_nextStrike < _plan.Lightning.Length && time >= _plan.Lightning[_nextStrike])
        {
            _nextStrike++;
            _boltAt = time;
            _bolt = MakeBolt();
            // Only one full-screen flash per strike (well below 3 per second).
            if (time - _lastWash >= 0.34) { _lastWash = time; _washAt = time; }
        }

        InvalidateVisual();
    }

    private Point[][] MakeBolt()
    {
        var x = _focus.X + _focus.Width * (0.15 + _random.NextDouble() * 0.7);
        var y = _focus.Y + _focus.Height * 0.08;
        var main = new List<Point> { new(x, y) };
        var branches = new List<Point[]>();
        while (y < _focus.Y + _focus.Height * 0.7)
        {
            x += (_random.NextDouble() - 0.5) * 70;
            y += 25 + _random.NextDouble() * 35;
            main.Add(new Point(x, y));
            if (_random.NextDouble() < 0.25)
            {
                var bx = x; var by = y;
                var branch = new List<Point> { new(bx, by) };
                for (var k = 0; k < 4; k++) { bx += (_random.NextDouble() - 0.3) * 60; by += 20 + _random.NextDouble() * 20; branch.Add(new Point(bx, by)); }
                branches.Add(branch.ToArray());
            }
        }
        return branches.Prepend(main.ToArray()).ToArray();
    }

    private Brush Fill(Color c, double alpha)
    {
        var level = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 30);
        if (_brushes.TryGetValue((c, level), out var b)) return b;
        var brush = new SolidColorBrush(Color.FromArgb((byte)(level * 255 / 30), c.R, c.G, c.B));
        brush.Freeze();
        _brushes[(c, level)] = brush;
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var fadeIn = Math.Clamp(_time / 1.2, 0, 1);
        var fadeOut = Math.Clamp((GloomWindow.ShowSeconds + 0.4 - _time) / 1.4, 0, 1);
        var show = Math.Min(fadeIn, fadeOut);
        var all = new Rect(0, 0, _width, _height);

        dc.DrawRectangle(Fill(Color.FromRgb(8, 12, 22), 0.72 * show), null, all);
        var vignette = new RadialGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb((byte)(200 * show), 0, 0, 0)) { RadiusX = 0.7, RadiusY = 0.7 };
        vignette.Freeze();
        dc.DrawRectangle(vignette, null, _focus);

        // Lightning flash and bolt.
        var sinceWash = _time - _washAt;
        if (sinceWash is >= 0 and < 0.3) dc.DrawRectangle(Fill(Color.FromRgb(200, 210, 255), 0.22 * (1 - sinceWash / 0.3)), null, all);

        // Storm clouds.
        foreach (var (cx, cy, r, speed) in _clouds)
        {
            var x = cx + _time * speed;
            var g = new RadialGradientBrush(Color.FromArgb((byte)(200 * show), 30, 32, 40), Color.FromArgb(0, 30, 32, 40));
            g.Freeze();
            dc.DrawEllipse(g, null, new Point(x, cy), r * 1.4, r * 0.7);
        }

        var sinceBolt = _time - _boltAt;
        if (_bolt is not null && sinceBolt is >= 0 and < 0.35)
        {
            var alpha = 1 - sinceBolt / 0.35;
            foreach (var line in _bolt)
            {
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    c.BeginFigure(line[0], false, false);
                    c.PolyLineTo(line.Skip(1).ToList(), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, new Pen(Fill(Color.FromRgb(160, 180, 255), 0.35 * alpha), line == _bolt[0] ? 14 : 7), geo);
                dc.DrawGeometry(null, new Pen(Fill(Colors.White, alpha), line == _bolt[0] ? 3 : 1.5), geo);
            }
        }

        // Rain.
        var rainPen = new Pen(Fill(Color.FromRgb(170, 185, 210), 0.38 * show), 1.2);
        foreach (var d in _rain)
            dc.DrawLine(rainPen, new Point(d.X, d.Y), new Point(d.X + d.Length * 0.18, d.Y - d.Length));
        foreach (var s in _splashes)
        {
            var p = s.Age / 0.35;
            dc.DrawEllipse(null, new Pen(Fill(Color.FromRgb(170, 185, 210), 0.5 * (1 - p) * show), 1), new Point(s.X, s.Y), 3 + p * 10, 1 + p * 3);
        }

        DrawGrave(dc, show);

        foreach (var l in _leaves)
        {
            dc.PushTransform(new TranslateTransform(l.X, l.Y));
            dc.PushTransform(new RotateTransform(l.Angle));
            dc.DrawEllipse(Fill(l.Color, 0.85 * show), null, new Point(0, 0), l.Size, l.Size * 0.45);
            dc.DrawLine(new Pen(Fill(Color.FromRgb(40, 30, 20), 0.8 * show), 1), new Point(-l.Size, 0), new Point(l.Size, 0));
            dc.Pop();
            dc.Pop();
        }

        foreach (var crowAt in _plan.Crows) DrawCrow(dc, _time - crowAt, show);

        DrawTitle(dc, show);
        foreach (var d in _drips)
            dc.DrawEllipse(Fill(Color.FromRgb(15, 15, 18), 0.9 * show), null, new Point(d.X, d.Y), 2.2, 3.6);
    }

    private void DrawGrave(DrawingContext dc, double show)
    {
        var size = Math.Min(_focus.Width, _focus.Height);
        var w = size * 0.16;
        var h = w * 1.25;
        var x = _focus.X + _focus.Width / 2 - w / 2;
        var y = Ground - h;
        var stone = new StreamGeometry();
        using (var c = stone.Open())
        {
            c.BeginFigure(new Point(x, Ground), true, true);
            c.LineTo(new Point(x, y + w / 2), true, false);
            c.ArcTo(new Point(x + w, y + w / 2), new Size(w / 2, w / 2), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x + w, Ground), true, false);
        }
        stone.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb((byte)(255 * show), 110, 112, 118), Color.FromArgb((byte)(255 * show), 60, 62, 68), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, new Pen(Fill(Color.FromRgb(30, 30, 34), show), 2), stone);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Carve(string text, double fontSize, double ty)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Georgia, Times New Roman"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), fontSize, Fill(Color.FromRgb(35, 36, 40), show), dpi);
            dc.DrawText(ft, new Point(x + w / 2 - ft.Width / 2, ty));
        }
        Carve("R.I.P.", w * 0.2, y + w * 0.32);
        Carve(Loc.T("gloom.rip"), w * 0.13, y + w * 0.62);
        Carve("✝", w * 0.16, y + w * 0.86);

        // Ground and two flickering candles.
        dc.DrawRectangle(Fill(Color.FromRgb(14, 16, 14), 0.95 * show), null, new Rect(_focus.X, Ground, _focus.Width, _focus.Bottom - Ground));
        foreach (var side in new[] { -1, 1 })
        {
            var cx = _focus.X + _focus.Width / 2 + side * w * 0.95;
            var flicker = 0.75 + 0.25 * Math.Sin(_time * 17 + side * 3) * Math.Sin(_time * 7.3 + side);
            var glow = new RadialGradientBrush(Color.FromArgb((byte)(90 * flicker * show), 255, 170, 80), Color.FromArgb(0, 255, 170, 80));
            glow.Freeze();
            dc.DrawEllipse(glow, null, new Point(cx, Ground - w * 0.42), w * 0.45, w * 0.45);
            dc.DrawRectangle(Fill(Color.FromRgb(225, 220, 200), show), null, new Rect(cx - w * 0.05, Ground - w * 0.32, w * 0.1, w * 0.32));
            dc.DrawEllipse(Fill(Color.FromRgb(255, 190, 90), flicker * show), null, new Point(cx, Ground - w * 0.38), w * 0.035, w * 0.07 * flicker);
        }
    }

    private void DrawCrow(DrawingContext dc, double t, double show)
    {
        const double duration = 4.5;
        if (t is < 0 or > duration) return;
        var p = t / duration;
        var size = Math.Min(_focus.Width, _focus.Height) * 0.05;
        var x = _focus.Right + size - p * (_focus.Width + size * 2);
        var y = _focus.Y + _focus.Height * (0.22 + 0.05 * Math.Sin(p * 6));
        var flap = Math.Sin(t * 9);
        var wing = new StreamGeometry();
        using (var c = wing.Open())
        {
            c.BeginFigure(new Point(x - size, y - flap * size * 0.6), false, false);
            c.QuadraticBezierTo(new Point(x - size * 0.4, y - size * 0.1), new Point(x, y), true, true);
            c.QuadraticBezierTo(new Point(x + size * 0.4, y - size * 0.1), new Point(x + size, y - flap * size * 0.6), true, true);
        }
        wing.Freeze();
        dc.DrawGeometry(null, new Pen(Fill(Color.FromRgb(10, 10, 12), show), size * 0.18) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, wing);
        dc.DrawEllipse(Fill(Color.FromRgb(10, 10, 12), show), null, new Point(x, y + size * 0.05), size * 0.22, size * 0.14);
    }

    private void DrawTitle(DrawingContext dc, double show)
    {
        var alpha = Math.Clamp((_time - 1) / 1.5, 0, 1) * show;
        if (alpha <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var titleSize = _focus.Width * 0.07;
        var center = new Point(_focus.X + _focus.Width / 2, _focus.Y + _focus.Height * 0.38);
        _title ??= Text(Loc.T("gloom.title"), titleSize, center, dpi, FontStyles.Normal);
        _subtitle ??= Text(Loc.T("gloom.sub"), titleSize * 0.36, new Point(center.X, center.Y + titleSize * 0.95), dpi, FontStyles.Italic);

        // Slow, heavy sinking motion.
        var sink = Math.Sin(_time * 0.8) * 4 + (_time - 1) * 2;
        dc.PushOpacity(alpha);
        dc.PushTransform(new TranslateTransform(0, sink));
        dc.PushTransform(new TranslateTransform(4, 6));
        dc.DrawGeometry(Fill(Colors.Black, 0.7), null, _title);
        dc.Pop();
        var fill = new LinearGradientBrush(Color.FromRgb(200, 204, 214), Color.FromRgb(90, 94, 104), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, new Pen(Fill(Color.FromRgb(20, 22, 28), 1), titleSize * 0.03), _title);
        dc.DrawGeometry(Fill(Color.FromRgb(150, 156, 170), 1), null, _subtitle);
        dc.Pop();
        dc.Pop();
    }

    private static Geometry Text(string text, double size, Point center, double dpi, FontStyle style)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Georgia, Times New Roman, serif"), style, FontWeights.Bold, FontStretches.Normal), size, Brushes.White, dpi);
        var geo = ft.BuildGeometry(new Point(0, 0));
        var b = geo.Bounds;
        geo.Transform = new TranslateTransform(center.X - b.X - b.Width / 2, center.Y - b.Y - b.Height / 2);
        geo.Freeze();
        return geo;
    }
}

/// <summary>Synthesized sad soundtrack as an in-memory stereo WAV (no recordings).</summary>
internal static class GloomSound
{
    private const int Rate = 22050;
    private const double TwoPi = Math.PI * 2;

    public static MemoryStream Render(GloomPlan plan)
    {
        var length = (int)((GloomWindow.ShowSeconds + 1) * Rate);
        var l = new float[length];
        var r = new float[length];
        var random = new Random();
        double N() => random.NextDouble() * 2 - 1;
        void Add(int i, double v, double pan)
        {
            if ((uint)i >= (uint)length) return;
            l[i] += (float)(v * Math.Sqrt(1 - pan));
            r[i] += (float)(v * Math.Sqrt(pan));
        }

        // Rain bed and howling wind across the whole show.
        double lpL = 0, lpR = 0, hp = 0, wind = 0;
        for (var i = 0; i < length; i++)
        {
            var t = i / (double)Rate;
            var env = Math.Clamp(t / 1.5, 0, 1) * Math.Clamp((GloomWindow.ShowSeconds + 0.6 - t) / 1.5, 0, 1);
            lpL += 0.35 * (N() - lpL);
            lpR += 0.35 * (N() - lpR);
            hp = 0.97 * hp + N() * 0.03;
            var cutoff = 0.004 + 0.003 * Math.Sin(t * 0.7) + 0.002 * Math.Sin(t * 1.9);
            wind += cutoff * (N() - wind);
            Add(i, (lpL - hp) * 0.16 * env + wind * 2.2 * env, 0.25);
            Add(i, (lpR - hp) * 0.16 * env + wind * 2.2 * env, 0.75);
        }

        // Single raindrop ticks.
        for (var k = 0; k < 600; k++)
        {
            var at = (int)(random.NextDouble() * GloomWindow.ShowSeconds * Rate);
            var pan = random.NextDouble();
            for (var i = 0; i < 60; i++) Add(at + i, N() * 0.08 * Math.Exp(-i / 10.0), pan);
        }

        foreach (var at in plan.Bells) Bell(Add, at);
        foreach (var at in plan.Lightning) Thunder(Add, N, at + 0.4, random.NextDouble());
        foreach (var at in plan.Crows) for (var c = 0; c < 3; c++) Caw(Add, N, at + 0.3 + c * 0.45, 0.85 - c * 0.2);
        FuneralMarch(Add, 1.2);
        SadTrombone(Add, plan.TromboneAt);

        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var bytes = length * 4;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16);
            w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(bytes);
            for (var i = 0; i < length; i++)
            {
                w.Write((short)(Math.Tanh(l[i] * 0.9) * 30000));
                w.Write((short)(Math.Tanh(r[i] * 0.9) * 30000));
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static void Bell(Action<int, double, double> add, double at)
    {
        // Deep church bell: inharmonic partials with long decay.
        double[] ratios = { 0.5, 1, 1.19, 1.5, 2, 2.74, 3.76 };
        double[] gains = { 0.5, 0.45, 0.3, 0.25, 0.2, 0.12, 0.08 };
        var start = (int)(at * Rate);
        for (var i = 0; i < (int)(4.5 * Rate); i++)
        {
            var t = i / (double)Rate;
            double v = 0;
            for (var k = 0; k < ratios.Length; k++) v += Math.Sin(TwoPi * 196 * ratios[k] * t) * gains[k] * Math.Exp(-t / (1.6 - k * 0.15));
            add(start + i, v * 0.35 * Math.Min(1, t / 0.005), 0.5);
        }
    }

    private static void Thunder(Action<int, double, double> add, Func<double> noise, double at, double pan)
    {
        var start = (int)(at * Rate);
        double lp = 0, sub = 0;
        for (var i = 0; i < (int)(4.0 * Rate); i++)
        {
            var t = i / (double)Rate;
            var crack = noise() * Math.Exp(-t / 0.03) * 0.8;
            lp += (0.02 + 0.1 * Math.Exp(-t / 0.3)) * (noise() - lp);
            var rumble = lp * (Math.Exp(-t / 1.4) * (1 + 0.6 * Math.Sin(t * 9))) * 3.2;
            sub += 0.005 * (noise() - sub);
            add(start + i, crack + rumble + sub * 14 * Math.Exp(-t / 1.8), pan);
        }
    }

    private static void Caw(Action<int, double, double> add, Func<double> noise, double at, double pan)
    {
        // Harsh crow call: a buzzy tone falling in pitch, mixed with rasp.
        var start = (int)(at * Rate);
        double phase = 0, band = 0;
        var duration = 0.32;
        for (var i = 0; i < (int)(duration * Rate); i++)
        {
            var t = i / (double)Rate;
            var f = 820 - 260 * t / duration;
            phase += TwoPi * f / Rate;
            var buzz = Math.Sign(Math.Sin(phase)) * 0.5 + Math.Sin(phase * 2) * 0.3;
            band += 0.4 * (noise() - band);
            var env = Math.Min(1, t / 0.02) * Math.Clamp((duration - t) / 0.08, 0, 1);
            add(start + i, (buzz * (0.6 + 0.4 * Math.Sin(TwoPi * 60 * t)) + band * 0.6) * env * 0.16, pan);
        }
    }

    // Opening of Chopin's funeral march (public domain), MIDI note and beats.
    private static readonly (int Note, double Beats)[] March =
    {
        (58, 1.5), (58, 1), (58, 0.5), (58, 1.5),
        (61, 1), (60, 0.5), (60, 1), (58, 0.5), (58, 1), (57, 0.5), (58, 2)
    };

    private static void FuneralMarch(Action<int, double, double> add, double at)
    {
        const double beat = 0.55;
        var t0 = at;
        for (var repeat = 0; repeat < 2; repeat++)
        {
            foreach (var (note, beats) in March)
            {
                var duration = beats * beat;
                Organ(add, t0, duration * 0.95, Midi(note), 0.2);
                Organ(add, t0, duration * 0.95, Midi(note - 12), 0.14);
                t0 += duration;
            }
        }
        // Sustained low B-flat minor chord underneath.
        foreach (var n in new[] { 34, 41, 46 }) Organ(add, at, t0 - at, Midi(n), 0.09);
    }

    private static void Organ(Action<int, double, double> add, double at, double duration, double freq, double amp)
    {
        var start = (int)(at * Rate);
        for (var i = 0; i < (int)((duration + 0.25) * Rate); i++)
        {
            var t = i / (double)Rate;
            var env = Math.Min(1, t / 0.08) * (t < duration ? 1 : Math.Exp(-(t - duration) / 0.12));
            var trem = 1 + 0.03 * Math.Sin(TwoPi * 5 * t);
            var v = Math.Sin(TwoPi * freq * t) + 0.5 * Math.Sin(TwoPi * freq * 2 * t) + 0.25 * Math.Sin(TwoPi * freq * 3 * t) + 0.12 * Math.Sin(TwoPi * freq * 4 * t);
            add(start + i, v * env * amp * trem, 0.5);
        }
    }

    private static void SadTrombone(Action<int, double, double> add, double at)
    {
        // "Wah wah wah waaah": four falling notes with a muted wah, the last one long and wobbling.
        int[] notes = { 62, 61, 60, 59 };
        double[] lengths = { 0.45, 0.45, 0.45, 1.6 };
        var t0 = at;
        for (var n = 0; n < notes.Length; n++)
        {
            var start = (int)(t0 * Rate);
            var duration = lengths[n];
            var phase = 0.0;
            for (var i = 0; i < (int)(duration * Rate); i++)
            {
                var t = i / (double)Rate;
                var vibrato = n == 3 ? 1 + 0.02 * Math.Sin(TwoPi * 6 * t) * Math.Min(1, t / 0.4) : 1;
                phase += TwoPi * Midi(notes[n]) * vibrato / Rate;
                var wah = 0.5 + 0.5 * Math.Sin(Math.PI * Math.Min(1, t / Math.Min(duration, 0.4)));
                double v = 0;
                for (var k = 1; k <= 6; k++) v += Math.Sin(k * phase) / k * Math.Pow(wah, k * 0.6);
                var env = Math.Min(1, t / 0.03) * Math.Clamp((duration - t) / 0.12, 0, 1);
                add(start + i, v * env * 0.3, 0.5);
            }
            t0 += duration + 0.05;
        }
    }

    private static double Midi(int note) => 440 * Math.Pow(2, (note - 69) / 12.0);
}
