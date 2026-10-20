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
/// Transparent, click-through overlay across all monitors that plays a loud casino-style "big win" show
/// (fireworks, bombs, tracers, a tank, an eagle, coin rain, a synthesized soundtrack) and closes itself afterwards.
/// </summary>
internal sealed class FireworksWindow : Window
{
    public const double ShowSeconds = 15.0;

    private readonly FireworksCanvas _canvas;
    private readonly Stopwatch _stopwatch = new();
    private SoundPlayer? _sound;
    private MemoryStream? _soundStream;
    private double _lastFrameSeconds;
    private bool _running;
    private bool _closed;

    /// <param name="focusArea">Screen area (WPF units) where the headline, eagle and bombs are centred.</param>
    public FireworksWindow(Rect focusArea)
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
        Title = "Feuerwerk";

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var focus = new Rect(focusArea.X - Left, focusArea.Y - Top, focusArea.Width, focusArea.Height);
        focus.Intersect(new Rect(0, 0, Width, Height));
        if (focus.IsEmpty || focus.Width < 200 || focus.Height < 200) focus = new Rect(0, 0, Width, Height);

        var show = FireworkShow.Create(Width, Height, focus, Random.Shared);
        _canvas = new FireworksCanvas(show, Width, Height);
        Content = _canvas;

        // The soundtrack takes about a second to synthesize, so it is built off the UI thread and the show starts once it is ready.
        var screenWidth = Width;
        var soundTask = Task.Run(() =>
        {
            try { return FireworkSound.Render(show, screenWidth); }
            catch { return null; }
        });

        SourceInitialized += (_, _) => MakeClickThrough();
        Loaded += async (_, _) =>
        {
            _soundStream = await soundTask;
            if (_closed)
            {
                _soundStream?.Dispose();
                return;
            }

            try
            {
                if (_soundStream is not null)
                {
                    _sound = new SoundPlayer(_soundStream);
                    _sound.Load();
                }
            }
            catch
            {
                // Without audio the show still runs.
                _sound = null;
            }

            Start();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            Stop();
        };
    }

    /// <summary>Screen area (WPF units) of the monitor the given window is on, e.g. the beamer.</summary>
    public static Rect MonitorArea(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                return new Rect(
                    info.Monitor.Left / dpi.DpiScaleX, info.Monitor.Top / dpi.DpiScaleY,
                    (info.Monitor.Right - info.Monitor.Left) / dpi.DpiScaleX,
                    (info.Monitor.Bottom - info.Monitor.Top) / dpi.DpiScaleY);
            }
        }
        catch
        {
            // Fall back to the primary screen below.
        }

        return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }

    private void Start()
    {
        _running = true;
        _stopwatch.Start();
        try { _sound?.Play(); } catch { /* Audio device may be unavailable. */ }
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        try { _sound?.Stop(); } catch { }
        _sound?.Dispose();
        _soundStream?.Dispose();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _stopwatch.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastFrameSeconds, 0, 0.05);
        _lastFrameSeconds = now;

        _canvas.Advance(now, dt);
        if (now >= ShowSeconds && (_canvas.IsIdle || now >= ShowSeconds + 1.5))
            Close();
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate);
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

internal enum ShellKind { Peony, Ring, Willow, Glitter, Crossette, Heart, Palm, Rainbow, Strobe, Mega, Star }

internal sealed record Shell(
    double LaunchAt,
    double FlightSeconds,
    double StartX,
    double TargetX,
    double TargetY,
    ShellKind Kind,
    Color Primary,
    Color Secondary,
    double Scale)
{
    public double BurstAt => LaunchAt + FlightSeconds;
}

internal enum CueKind { Jackpot, Eagle, Gun, Cannon, Bomb }

/// <summary>A non-firework moment of the show. For eagles X is the flight direction (+1 = to the right).</summary>
internal sealed record Cue(double At, CueKind Kind, double X, double Y, double Duration = 0)
{
    public const double BombFallSeconds = 1.2;
    public double ActiveFrom => Kind == CueKind.Bomb ? At - BombFallSeconds : At;
}

internal sealed record ShowPlan(List<Shell> Shells, List<Cue> Cues, Rect Focus);

internal static class FireworkShow
{
    public const double FinaleJackpotAt = 11.9;

    public static readonly Color[] Palette =
    {
        Color.FromRgb(255, 59, 59),   // Rot
        Color.FromRgb(255, 138, 0),   // Orange
        Color.FromRgb(255, 214, 64),  // Gold
        Color.FromRgb(124, 255, 79),  // Grün
        Color.FromRgb(0, 229, 255),   // Cyan
        Color.FromRgb(61, 123, 255),  // Blau
        Color.FromRgb(166, 77, 255),  // Violett
        Color.FromRgb(255, 79, 216),  // Pink
        Color.FromRgb(255, 255, 255)  // Weiß
    };

    public static readonly Color Gold = Color.FromRgb(255, 196, 92);

    private static readonly (ShellKind Kind, double Weight)[] KindWeights =
    {
        (ShellKind.Peony, 2), (ShellKind.Ring, 1), (ShellKind.Willow, 1), (ShellKind.Glitter, 1),
        (ShellKind.Crossette, 1), (ShellKind.Heart, 0.4), (ShellKind.Palm, 1), (ShellKind.Rainbow, 2.6),
        (ShellKind.Strobe, 1), (ShellKind.Mega, 1.1), (ShellKind.Star, 1)
    };

    public static ShowPlan Create(double width, double height, Rect focus, Random random)
    {
        var shells = new List<Shell>();
        var cues = new List<Cue>();
        double Jitter(double range) => (random.NextDouble() - 0.5) * range;
        var center = new Point(focus.X + focus.Width / 2, focus.Y + focus.Height * 0.42);

        // Jackpot at the start and before the finale: coin fountain, confetti, slot-machine jingle.
        cues.Add(new Cue(0.05, CueKind.Jackpot, center.X, center.Y));
        cues.Add(new Cue(FinaleJackpotAt, CueKind.Jackpot, center.X, center.Y));

        foreach (var at in new[] { 0.45, 5.4, 10.4 })
            cues.Add(new Cue(at + Jitter(0.3), CueKind.Eagle, random.Next(2) == 0 ? -1 : 1,
                focus.Y + focus.Height * (0.16 + random.NextDouble() * 0.16), 2.8));

        for (var i = 0; i < 7; i++)
            cues.Add(new Cue(1.4 + i * 1.5 + Jitter(0.5), CueKind.Gun, i % 2 == 0 ? focus.Left : focus.Right, focus.Bottom,
                0.9 + random.NextDouble() * 0.5));

        // Tank cannon: muzzle flash at the bottom edge and a fast heavy shell straight up.
        foreach (var at in new[] { 2.6, 6.3, 9.1 })
        {
            var x = focus.Left + focus.Width * (0.2 + random.NextDouble() * 0.6);
            cues.Add(new Cue(at, CueKind.Cannon, x, focus.Bottom));
            shells.Add(new Shell(at, 0.42, x, x + Jitter(80), focus.Top + focus.Height * (0.15 + random.NextDouble() * 0.2),
                ShellKind.Mega, Palette[random.Next(Palette.Length)], Palette[random.Next(Palette.Length)], 1.35));
        }

        foreach (var at in new[] { 3.7, 7.7, 10.1, 12.7 })
            cues.Add(new Cue(at + Jitter(0.2), CueKind.Bomb, focus.Left + focus.Width * (0.15 + random.NextDouble() * 0.7),
                focus.Top + focus.Height * (0.45 + random.NextDouble() * 0.3)));

        // Non-stop barrage, then a dense finale; the last bursts land well before ShowSeconds.
        var t = 0.2;
        while (t < 11.2)
        {
            shells.Add(RandomShell(t, width, height, random));
            var extra = random.NextDouble();
            if (extra < 0.55) shells.Add(RandomShell(t + 0.04 + random.NextDouble() * 0.1, width, height, random));
            if (extra < 0.2) shells.Add(RandomShell(t + 0.1 + random.NextDouble() * 0.12, width, height, random));
            t += 0.16 + random.NextDouble() * 0.24;
        }

        for (var i = 0; i < 40; i++)
            shells.Add(RandomShell(11.4 + random.NextDouble() * 1.4, width, height, random, finale: true));

        return new ShowPlan(shells.OrderBy(s => s.LaunchAt).ToList(), cues.OrderBy(c => c.ActiveFrom).ToList(), focus);
    }

    private static Shell RandomShell(double launchAt, double width, double height, Random random, bool finale = false)
    {
        var roll = random.NextDouble() * KindWeights.Sum(k => k.Weight);
        var kind = KindWeights[^1].Kind;
        foreach (var (k, weight) in KindWeights)
        {
            if (roll < weight) { kind = k; break; }
            roll -= weight;
        }

        var primary = Palette[random.Next(Palette.Length)];
        var secondary = Palette[random.Next(Palette.Length)];
        if (kind is ShellKind.Willow or ShellKind.Palm) primary = Gold;

        var targetX = width * (0.06 + random.NextDouble() * 0.88);
        return new Shell(
            launchAt,
            0.75 + random.NextDouble() * 0.55,
            targetX + (random.NextDouble() - 0.5) * width * 0.12,
            targetX,
            height * (0.1 + random.NextDouble() * 0.42),
            kind,
            primary,
            secondary,
            (finale ? 1.15 : 0.85) + random.NextDouble() * 0.5);
    }
}

internal sealed class Spark
{
    public double X, Y, Vx, Vy;
    public double Age, Life;
    public double Drag, Gravity, Size, Streak;
    public Color Color;
    public bool Flicker, Strobe, Rainbow;
    public double Hue, Seed;
    public double SplitAt = double.NaN;
}

internal sealed class Flash
{
    public double X, Y, Age, Radius, Duration = 0.35;
    public Color Color;
}

internal sealed class Shockwave
{
    public double X, Y, Age, MaxRadius;
}

internal sealed class Wash
{
    public double Age, Strength;
    public Color Color;
}

internal sealed class Coin
{
    public double X, Y, Vx, Vy, Phase, Spin, Size, Gravity;
}

internal sealed class Confetti
{
    public double X, Y, Vx, Vy, Angle, Spin, Flip, FlipSpeed, Size, Age, Life, Sway;
    public Color Color;
}

internal sealed class FireworksCanvas : FrameworkElement
{
    private const int AlphaLevels = 32;
    private const int MaxSparks = 6500;
    private const double WashSeconds = 0.3;
    // Full-screen colour washes stay below 3 per second (photosensitivity guideline).
    private const double MinWashGap = 0.34;

    private readonly List<Shell> _pending;
    private readonly List<Cue> _pendingCues;
    private readonly List<Shell> _rising = new();
    private readonly List<Cue> _activeCues = new();
    private readonly Dictionary<Cue, double> _nextShot = new();
    private readonly List<Spark> _sparks = new();
    private readonly List<Flash> _flashes = new();
    private readonly List<Shockwave> _shocks = new();
    private readonly List<Wash> _washes = new();
    private readonly List<Coin> _coins = new();
    private readonly List<Confetti> _confetti = new();
    private readonly List<Point> _bulbs = new();
    private readonly Dictionary<(Color, int), SolidColorBrush> _brushes = new();
    private readonly Dictionary<(Color, int, int, bool), Pen> _pens = new();
    private readonly double _width;
    private readonly double _height;
    private readonly Rect _focus;
    private readonly Point _center;
    private readonly Random _random = new();
    private Geometry? _title;
    private Geometry? _subtitle;
    private double _time;
    private double _lastWash = -10;
    private double _coinRainCarry;

    public FireworksCanvas(ShowPlan show, double width, double height)
    {
        _pending = new List<Shell>(show.Shells);
        _pendingCues = new List<Cue>(show.Cues);
        _width = width;
        _height = height;
        _focus = show.Focus;
        _center = new Point(_focus.X + _focus.Width / 2, _focus.Y + _focus.Height * 0.42);
        IsHitTestVisible = false;

        // Casino marquee bulbs around the focus screen.
        const double inset = 16, spacing = 46;
        var r = new Rect(_focus.X + inset, _focus.Y + inset, _focus.Width - 2 * inset, _focus.Height - 2 * inset);
        var nx = Math.Max(2, (int)(r.Width / spacing));
        var ny = Math.Max(2, (int)(r.Height / spacing));
        for (var i = 0; i < nx; i++) _bulbs.Add(new Point(r.Left + r.Width * i / nx, r.Top));
        for (var i = 0; i < ny; i++) _bulbs.Add(new Point(r.Right, r.Top + r.Height * i / ny));
        for (var i = 0; i < nx; i++) _bulbs.Add(new Point(r.Right - r.Width * i / nx, r.Bottom));
        for (var i = 0; i < ny; i++) _bulbs.Add(new Point(r.Left, r.Bottom - r.Height * i / ny));
    }

    public bool IsIdle => _pending.Count == 0 && _rising.Count == 0 && _sparks.Count == 0 && _flashes.Count == 0
                          && _pendingCues.Count == 0 && _activeCues.Count == 0 && _coins.Count == 0 && _confetti.Count == 0;

    private double MinFocus => Math.Min(_focus.Width, _focus.Height);

    public void Advance(double time, double dt)
    {
        _time = time;

        while (_pending.Count > 0 && _pending[0].LaunchAt <= time)
        {
            _rising.Add(_pending[0]);
            _pending.RemoveAt(0);
        }

        while (_pendingCues.Count > 0 && _pendingCues[0].ActiveFrom <= time)
        {
            var cue = _pendingCues[0];
            _pendingCues.RemoveAt(0);
            StartCue(cue);
        }

        AdvanceCues(time);

        for (var i = _rising.Count - 1; i >= 0; i--)
        {
            var shell = _rising[i];
            if (time >= shell.BurstAt)
            {
                _rising.RemoveAt(i);
                Burst(shell);
                continue;
            }

            // Rocket tail: short-lived golden sparks behind the rising head.
            var (x, y) = RocketPosition(shell, time);
            for (var k = 0; k < 3; k++)
            {
                AddSpark(new Spark
                {
                    X = x, Y = y,
                    Vx = (_random.NextDouble() - 0.5) * 40, Vy = 30 + _random.NextDouble() * 60,
                    Life = 0.25 + _random.NextDouble() * 0.35, Drag = 2.5, Gravity = 60,
                    Size = 1.6, Streak = 0.03, Color = FireworkShow.Gold, Flicker = true
                });
            }
        }

        // Steady coin rain from the top while the show runs.
        if (time > 0.3 && time < 12.8)
        {
            _coinRainCarry += dt * 22;
            while (_coinRainCarry >= 1)
            {
                _coinRainCarry--;
                _coins.Add(new Coin
                {
                    X = _focus.Left + _random.NextDouble() * _focus.Width, Y = _focus.Top - 30,
                    Vx = (_random.NextDouble() - 0.5) * 60, Vy = 180 + _random.NextDouble() * 220,
                    Phase = _random.NextDouble() * Math.PI * 2, Spin = 6 + _random.NextDouble() * 8,
                    Size = MinFocus * (0.012 + _random.NextDouble() * 0.01), Gravity = 260
                });
            }
        }

        var splits = new List<Spark>();
        foreach (var spark in _sparks)
        {
            spark.Age += dt;
            var damping = Math.Exp(-spark.Drag * dt);
            spark.Vx *= damping;
            spark.Vy = spark.Vy * damping + spark.Gravity * dt;
            spark.X += spark.Vx * dt;
            spark.Y += spark.Vy * dt;

            if (!double.IsNaN(spark.SplitAt) && spark.Age >= spark.SplitAt)
            {
                spark.SplitAt = double.NaN;
                spark.Life = spark.Age; // the comet itself ends here
                splits.Add(spark);
            }
        }

        foreach (var parent in splits)
            SplitCrossette(parent);

        _sparks.RemoveAll(s => s.Age >= s.Life || s.Y > _height + 40);

        foreach (var coin in _coins)
        {
            coin.Vy += coin.Gravity * dt;
            coin.X += coin.Vx * dt;
            coin.Y += coin.Vy * dt;
            coin.Phase += coin.Spin * dt;
        }
        _coins.RemoveAll(c => c.Y > _height + 60);

        foreach (var piece in _confetti)
        {
            piece.Age += dt;
            var damping = Math.Exp(-1.8 * dt);
            piece.Vx = piece.Vx * damping + Math.Sin(piece.Age * 3 + piece.Sway) * 90 * dt;
            piece.Vy = piece.Vy * damping + 260 * dt;
            piece.X += piece.Vx * dt;
            piece.Y += piece.Vy * dt;
            piece.Angle += piece.Spin * dt;
            piece.Flip += piece.FlipSpeed * dt;
        }
        _confetti.RemoveAll(c => c.Age >= c.Life || c.Y > _height + 40);

        foreach (var flash in _flashes) flash.Age += dt;
        _flashes.RemoveAll(f => f.Age >= f.Duration);
        foreach (var shock in _shocks) shock.Age += dt;
        _shocks.RemoveAll(s => s.Age >= 0.7);
        foreach (var wash in _washes) wash.Age += dt;
        _washes.RemoveAll(w => w.Age >= WashSeconds);

        InvalidateVisual();
    }

    private void StartCue(Cue cue)
    {
        switch (cue.Kind)
        {
            case CueKind.Jackpot:
                Jackpot(cue);
                break;
            case CueKind.Cannon:
                _flashes.Add(new Flash { X = cue.X, Y = cue.Y, Radius = MinFocus * 0.28, Color = Color.FromRgb(255, 170, 60), Duration = 0.3 });
                for (var i = 0; i < 70; i++)
                {
                    var angle = -Math.PI / 2 + (_random.NextDouble() - 0.5) * 1.4;
                    var speed = 300 + _random.NextDouble() * 500;
                    AddSpark(new Spark
                    {
                        X = cue.X, Y = cue.Y, Vx = Math.Cos(angle) * speed, Vy = Math.Sin(angle) * speed,
                        Life = 0.5 + _random.NextDouble() * 0.5, Drag = 3, Gravity = 300, Size = 2.4, Streak = 0.05,
                        Color = Vary(FireworkShow.Gold), Flicker = true
                    });
                }
                break;
            default:
                _activeCues.Add(cue);
                if (cue.Kind == CueKind.Gun) _nextShot[cue] = cue.At;
                break;
        }
    }

    private void AdvanceCues(double time)
    {
        for (var i = _activeCues.Count - 1; i >= 0; i--)
        {
            var cue = _activeCues[i];
            switch (cue.Kind)
            {
                case CueKind.Bomb when time >= cue.At:
                    _activeCues.RemoveAt(i);
                    ExplodeBomb(cue);
                    break;

                case CueKind.Bomb:
                {
                    var (x, y) = BombPosition(cue, time);
                    AddSpark(new Spark
                    {
                        X = x, Y = y, Vx = (_random.NextDouble() - 0.5) * 30, Vy = -60,
                        Life = 0.35, Drag = 2, Gravity = 0, Size = 2.4, Streak = 0.02,
                        Color = Color.FromRgb(255, 90, 40), Flicker = true
                    });
                    break;
                }

                case CueKind.Gun:
                {
                    var next = _nextShot[cue];
                    while (next <= time && next < cue.At + cue.Duration)
                    {
                        FireTracer(cue);
                        next += 1 / 14.0;
                    }
                    _nextShot[cue] = next;
                    if (next >= cue.At + cue.Duration)
                    {
                        _activeCues.RemoveAt(i);
                        _nextShot.Remove(cue);
                    }
                    break;
                }

                case CueKind.Eagle:
                {
                    if (time >= cue.At + cue.Duration)
                    {
                        _activeCues.RemoveAt(i);
                        break;
                    }
                    var (x, y, size, _) = EaglePose(cue, time);
                    for (var k = 0; k < 3; k++)
                    {
                        AddSpark(new Spark
                        {
                            X = x - cue.X * size * 0.5, Y = y + (_random.NextDouble() - 0.5) * size * 0.3,
                            Vx = -cue.X * 60 + (_random.NextDouble() - 0.5) * 60, Vy = (_random.NextDouble() - 0.5) * 60,
                            Life = 0.6 + _random.NextDouble() * 0.6, Drag = 1.5, Gravity = 40, Size = 2.2, Streak = 0.03,
                            Rainbow = true, Hue = _time * 200 + _random.NextDouble() * 60, Flicker = true
                        });
                    }
                    break;
                }
            }
        }
    }

    private (double X, double Y) BombPosition(Cue cue, double time)
    {
        var p = Math.Clamp((time - cue.ActiveFrom) / Cue.BombFallSeconds, 0, 1);
        return (cue.X, -30 + (cue.Y + 30) * p * p);
    }

    private (double X, double Y, double Size, double Flap) EaglePose(Cue cue, double time)
    {
        var p = Math.Clamp((time - cue.At) / cue.Duration, 0, 1);
        var size = MinFocus * 0.2;
        var from = cue.X > 0 ? _focus.Left - size : _focus.Right + size;
        var to = cue.X > 0 ? _focus.Right + size : _focus.Left - size;
        var x = from + (to - from) * p;
        var y = cue.Y + Math.Sin(p * Math.PI * 2) * _focus.Height * 0.05;
        return (x, y, size, Math.Sin(time * 9));
    }

    private void FireTracer(Cue cue)
    {
        var fromLeft = cue.X <= _focus.X + _focus.Width / 2;
        var angle = -(0.6 + _random.NextDouble() * 0.8);
        var speed = _focus.Height * (1.5 + _random.NextDouble() * 0.5);
        var vx = Math.Cos(angle) * speed * (fromLeft ? 1 : -1);
        AddSpark(new Spark
        {
            X = cue.X, Y = cue.Y, Vx = vx, Vy = Math.Sin(angle) * speed,
            Life = 0.9, Drag = 0.25, Gravity = 120, Size = 2.6, Streak = 0.06,
            Color = Color.FromRgb(255, 236, 140)
        });
        _flashes.Add(new Flash { X = cue.X, Y = cue.Y, Radius = 70, Color = Color.FromRgb(255, 200, 90), Duration = 0.07 });
    }

    private void Jackpot(Cue cue)
    {
        AddWash(Color.FromRgb(255, 200, 40), 0.28);
        _flashes.Add(new Flash { X = cue.X, Y = cue.Y, Radius = MinFocus * 0.6, Color = FireworkShow.Gold, Duration = 0.5 });

        for (var i = 0; i < 160; i++)
        {
            var angle = -Math.PI / 2 + (_random.NextDouble() - 0.5) * 2.4;
            var speed = MinFocus * (0.6 + _random.NextDouble() * 0.9);
            _coins.Add(new Coin
            {
                X = cue.X, Y = cue.Y, Vx = Math.Cos(angle) * speed, Vy = Math.Sin(angle) * speed,
                Phase = _random.NextDouble() * Math.PI * 2, Spin = 6 + _random.NextDouble() * 10,
                Size = MinFocus * (0.014 + _random.NextDouble() * 0.012), Gravity = MinFocus * 1.3
            });
        }

        for (var i = 0; i < 320; i++)
        {
            var angle = _random.NextDouble() * Math.PI * 2;
            var speed = MinFocus * (0.3 + _random.NextDouble() * 1.1);
            _confetti.Add(new Confetti
            {
                X = cue.X, Y = cue.Y, Vx = Math.Cos(angle) * speed, Vy = Math.Sin(angle) * speed - MinFocus * 0.4,
                Angle = _random.NextDouble() * Math.PI, Spin = (_random.NextDouble() - 0.5) * 14,
                FlipSpeed = 4 + _random.NextDouble() * 8, Size = 5 + _random.NextDouble() * 7,
                Life = 4 + _random.NextDouble() * 2.5, Sway = _random.NextDouble() * 6,
                Color = Hsv(_random.NextDouble() * 360)
            });
        }
    }

    private void ExplodeBomb(Cue cue)
    {
        var big = MinFocus;
        AddWash(Color.FromRgb(255, 110, 30), 0.3);
        _flashes.Add(new Flash { X = cue.X, Y = cue.Y, Radius = big * 0.6, Color = Color.FromRgb(255, 150, 50), Duration = 0.6 });
        _shocks.Add(new Shockwave { X = cue.X, Y = cue.Y, MaxRadius = big * 0.8 });

        Color[] fire = { Color.FromRgb(255, 60, 20), Color.FromRgb(255, 140, 0), Color.FromRgb(255, 220, 80), Colors.White };
        for (var i = 0; i < 300; i++)
        {
            var angle = _random.NextDouble() * Math.PI * 2;
            var speed = big * 0.32 * 2.2 * (0.3 + _random.NextDouble() * 0.8);
            AddSpark(new Spark
            {
                X = cue.X, Y = cue.Y, Vx = Math.Cos(angle) * speed, Vy = Math.Sin(angle) * speed,
                Life = 0.9 + _random.NextDouble() * 0.8, Drag = 2.2, Gravity = 140, Size = 3.4, Streak = 0.06,
                Color = fire[_random.Next(fire.Length)], Flicker = _random.NextDouble() < 0.3
            });
        }

        for (var i = 0; i < 50; i++)
        {
            var angle = -Math.PI / 2 + (_random.NextDouble() - 0.5) * 2.2;
            var speed = big * (0.8 + _random.NextDouble() * 0.9);
            AddSpark(new Spark
            {
                X = cue.X, Y = cue.Y, Vx = Math.Cos(angle) * speed, Vy = Math.Sin(angle) * speed,
                Life = 1.6 + _random.NextDouble() * 0.8, Drag = 0.9, Gravity = 520, Size = 2.8, Streak = 0.08,
                Color = Color.FromRgb(255, 120, 30), Flicker = true
            });
        }
    }

    private void AddWash(Color color, double strength)
    {
        if (_time - _lastWash < MinWashGap) return;
        _lastWash = _time;
        _washes.Add(new Wash { Color = color, Strength = strength });
    }

    private (double X, double Y) RocketPosition(Shell shell, double time)
    {
        var p = Math.Clamp((time - shell.LaunchAt) / shell.FlightSeconds, 0, 1);
        var eased = 1 - Math.Pow(1 - p, 2.2);
        var wobble = Math.Sin(p * 18 + shell.StartX) * 4 * (1 - p);
        var x = shell.StartX + (shell.TargetX - shell.StartX) * eased + wobble;
        var y = (_height + 20) + (shell.TargetY - (_height + 20)) * eased;
        return (x, y);
    }

    private void Burst(Shell shell)
    {
        var radius = Math.Min(_width, _height) * 0.2 * shell.Scale;
        _flashes.Add(new Flash { X = shell.TargetX, Y = shell.TargetY, Radius = radius * 0.9, Color = shell.Primary });

        switch (shell.Kind)
        {
            case ShellKind.Peony:
                Sphere(shell, radius, 170, drag: 1.7, gravity: 90, life: 1.6, size: 2.6);
                Sphere(shell with { Primary = shell.Secondary }, radius * 0.45, 60, drag: 1.7, gravity: 90, life: 1.3, size: 2.2);
                break;
            case ShellKind.Ring:
                Ring(shell, radius, 100);
                Sphere(shell with { Primary = Colors.White }, radius * 0.25, 30, drag: 2, gravity: 80, life: 1.0, size: 2);
                break;
            case ShellKind.Willow:
                Sphere(shell, radius * 0.9, 140, drag: 1.3, gravity: 45, life: 2.8, size: 2.0, streak: 0.12, flicker: true);
                break;
            case ShellKind.Glitter:
                Sphere(shell, radius, 190, drag: 1.8, gravity: 85, life: 1.9, size: 2.3, flicker: true);
                break;
            case ShellKind.Crossette:
                Crossette(shell, radius);
                break;
            case ShellKind.Heart:
                Heart(shell, radius);
                break;
            case ShellKind.Palm:
                Palm(shell, radius);
                break;
            case ShellKind.Rainbow:
                Sphere(shell, radius * 1.1, 240, drag: 1.7, gravity: 80, life: 2.0, size: 2.7, rainbow: true);
                break;
            case ShellKind.Strobe:
                Sphere(shell with { Primary = Colors.White }, radius, 200, drag: 1.6, gravity: 50, life: 2.4, size: 2.0, strobe: true);
                Sphere(shell, radius * 0.4, 40, drag: 2, gravity: 80, life: 1.2, size: 2.4);
                break;
            case ShellKind.Mega:
                _flashes.Add(new Flash { X = shell.TargetX, Y = shell.TargetY, Radius = radius * 1.8, Color = Colors.White, Duration = 0.45 });
                Sphere(shell, radius * 1.4, 280, drag: 1.6, gravity: 70, life: 2.3, size: 3.0, rainbow: _random.NextDouble() < 0.5);
                Sphere(shell with { Primary = shell.Secondary }, radius * 0.8, 120, drag: 1.7, gravity: 80, life: 1.8, size: 2.6);
                Ring(shell with { Primary = Colors.White, Secondary = FireworkShow.Gold }, radius * 1.1, 80);
                break;
            case ShellKind.Star:
                Star(shell, radius);
                break;
        }
    }

    private void Sphere(Shell shell, double radius, int count, double drag, double gravity, double life, double size,
        double streak = 0.05, bool flicker = false, bool rainbow = false, bool strobe = false)
    {
        var speed = radius * drag;
        var hueOffset = _random.NextDouble() * 360;
        for (var i = 0; i < count; i++)
        {
            // Projected 3D sphere: denser rim, looks round instead of like a flat disc.
            var angle = _random.NextDouble() * Math.PI * 2;
            var z = _random.NextDouble() * 2 - 1;
            var s = speed * Math.Sqrt(1 - z * z) * (0.9 + _random.NextDouble() * 0.15);
            AddSpark(new Spark
            {
                X = shell.TargetX, Y = shell.TargetY,
                Vx = Math.Cos(angle) * s, Vy = Math.Sin(angle) * s,
                Life = life * (0.75 + _random.NextDouble() * 0.4), Drag = drag, Gravity = gravity,
                Size = size, Streak = streak, Color = Vary(shell.Primary), Flicker = flicker,
                Rainbow = rainbow, Hue = hueOffset + angle * 180 / Math.PI, Strobe = strobe, Seed = _random.NextDouble()
            });
        }
    }

    private void Ring(Shell shell, double radius, int count)
    {
        var tilt = _random.NextDouble() * Math.PI;
        var squash = 0.35 + _random.NextDouble() * 0.5;
        const double drag = 1.8;
        for (var i = 0; i < count; i++)
        {
            var angle = i * Math.PI * 2 / count;
            var rx = Math.Cos(angle);
            var ry = Math.Sin(angle) * squash;
            var vx = rx * Math.Cos(tilt) - ry * Math.Sin(tilt);
            var vy = rx * Math.Sin(tilt) + ry * Math.Cos(tilt);
            AddSpark(new Spark
            {
                X = shell.TargetX, Y = shell.TargetY,
                Vx = vx * radius * drag, Vy = vy * radius * drag,
                Life = 1.5 + _random.NextDouble() * 0.3, Drag = drag, Gravity = 70,
                Size = 2.6, Streak = 0.05, Color = Vary(i % 2 == 0 ? shell.Primary : shell.Secondary)
            });
        }
    }

    private void Crossette(Shell shell, double radius)
    {
        const int comets = 12;
        const double drag = 1.4;
        for (var i = 0; i < comets; i++)
        {
            var angle = i * Math.PI * 2 / comets + _random.NextDouble() * 0.2;
            AddSpark(new Spark
            {
                X = shell.TargetX, Y = shell.TargetY,
                Vx = Math.Cos(angle) * radius * drag * 0.7, Vy = Math.Sin(angle) * radius * drag * 0.7,
                Life = 2, Drag = drag, Gravity = 60, Size = 3.4, Streak = 0.06,
                Color = shell.Primary, SplitAt = 0.55 + _random.NextDouble() * 0.15
            });
        }
    }

    private void SplitCrossette(Spark parent)
    {
        var baseAngle = Math.Atan2(parent.Vy, parent.Vx) + Math.PI / 4;
        var speed = Math.Min(_width, _height) * 0.18;
        for (var k = 0; k < 4; k++)
        {
            var angle = baseAngle + k * Math.PI / 2;
            AddSpark(new Spark
            {
                X = parent.X, Y = parent.Y,
                Vx = Math.Cos(angle) * speed + parent.Vx * 0.3, Vy = Math.Sin(angle) * speed + parent.Vy * 0.3,
                Life = 0.8 + _random.NextDouble() * 0.3, Drag = 2.2, Gravity = 90, Size = 2.4, Streak = 0.05,
                Color = Vary(parent.Color), Flicker = true
            });
        }
        _flashes.Add(new Flash { X = parent.X, Y = parent.Y, Radius = speed * 0.3, Color = parent.Color });
    }

    private void Heart(Shell shell, double radius)
    {
        const int count = 120;
        const double drag = 1.9;
        var scale = radius * drag / 17.0;
        for (var i = 0; i < count; i++)
        {
            var t = i * Math.PI * 2 / count;
            var hx = 16 * Math.Pow(Math.Sin(t), 3);
            var hy = -(13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t));
            AddSpark(new Spark
            {
                X = shell.TargetX, Y = shell.TargetY,
                Vx = hx * scale, Vy = hy * scale,
                Life = 1.7, Drag = drag, Gravity = 35, Size = 2.6, Streak = 0.04,
                Color = Vary(Color.FromRgb(255, 64, 128))
            });
        }
    }

    private void Star(Shell shell, double radius)
    {
        const int perEdge = 14;
        const double drag = 1.9;
        var spin = _random.NextDouble() * Math.PI;
        var corners = new Point[10];
        for (var c = 0; c < 10; c++)
        {
            var a = spin + c * Math.PI / 5 - Math.PI / 2;
            var r = c % 2 == 0 ? 1.0 : 0.42;
            corners[c] = new Point(Math.Cos(a) * r, Math.Sin(a) * r);
        }

        for (var c = 0; c < 10; c++)
        {
            var a = corners[c];
            var b = corners[(c + 1) % 10];
            for (var k = 0; k < perEdge; k++)
            {
                var t = k / (double)perEdge;
                AddSpark(new Spark
                {
                    X = shell.TargetX, Y = shell.TargetY,
                    Vx = (a.X + (b.X - a.X) * t) * radius * drag, Vy = (a.Y + (b.Y - a.Y) * t) * radius * drag,
                    Life = 1.8, Drag = drag, Gravity = 30, Size = 2.6, Streak = 0.04,
                    Color = Vary(c % 2 == 0 ? shell.Primary : FireworkShow.Gold), Rainbow = _random.NextDouble() < 0.3,
                    Hue = c * 36
                });
            }
        }
    }

    private void Palm(Shell shell, double radius)
    {
        const int fronds = 7;
        const double drag = 1.2;
        for (var f = 0; f < fronds; f++)
        {
            var angle = -Math.PI / 2 + (f - (fronds - 1) / 2.0) * 0.45 + (_random.NextDouble() - 0.5) * 0.15;
            for (var k = 0; k < 14; k++)
            {
                var s = radius * drag * (0.55 + k * 0.035);
                AddSpark(new Spark
                {
                    X = shell.TargetX, Y = shell.TargetY,
                    Vx = Math.Cos(angle) * s, Vy = Math.Sin(angle) * s,
                    Life = 2.0 + k * 0.03, Drag = drag, Gravity = 110, Size = 2.2, Streak = 0.09,
                    Color = Vary(shell.Primary), Flicker = k % 3 == 0
                });
            }
        }
        Sphere(shell with { Primary = shell.Secondary }, radius * 0.35, 40, drag: 2, gravity: 80, life: 1.1, size: 2);
    }

    private void AddSpark(Spark spark)
    {
        if (_sparks.Count < MaxSparks) _sparks.Add(spark);
    }

    private Color Vary(Color color)
    {
        int Shift(byte c) => Math.Clamp(c + _random.Next(-25, 26), 0, 255);
        return Color.FromRgb((byte)Shift(color.R), (byte)Shift(color.G), (byte)Shift(color.B));
    }

    private static Color Hsv(double hue, double saturation = 1, double value = 1)
    {
        hue = ((hue % 360) + 360) % 360 / 60;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue % 2 - 1));
        var m = value - c;
        var (r, g, b) = (int)hue switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x),
            3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Dimming so the colours pop on any desktop; fades in and out with the show.
        var dim = Math.Min(Math.Min(_time / 0.4, 1), Math.Max(0, (FireworksWindow.ShowSeconds + 0.5 - _time) / 1.2));
        if (dim > 0)
            dc.DrawRectangle(GetBrush(Colors.Black, dim * 0.5), null, new Rect(0, 0, _width, _height));

        foreach (var wash in _washes)
            dc.DrawRectangle(GetBrush(wash.Color, wash.Strength * (1 - wash.Age / WashSeconds)), null, new Rect(0, 0, _width, _height));

        DrawSunburst(dc, dim);

        foreach (var flash in _flashes)
        {
            var p = flash.Age / flash.Duration;
            var brush = new RadialGradientBrush(
                Color.FromArgb((byte)(170 * (1 - p)), flash.Color.R, flash.Color.G, flash.Color.B),
                Color.FromArgb(0, flash.Color.R, flash.Color.G, flash.Color.B));
            brush.Freeze();
            var r = flash.Radius * (0.4 + p * 0.8);
            dc.DrawEllipse(brush, null, new Point(flash.X, flash.Y), r, r);
        }

        foreach (var shock in _shocks)
        {
            var p = shock.Age / 0.7;
            var r = shock.MaxRadius * (1 - Math.Pow(1 - p, 3));
            dc.DrawEllipse(null, GetPen(Colors.White, (1 - p) * 0.8, 3 + 14 * (1 - p)), new Point(shock.X, shock.Y), r, r * 0.85);
        }

        foreach (var shell in _rising)
        {
            var (x, y) = RocketPosition(shell, _time);
            dc.DrawEllipse(GetBrush(Colors.White, 1), null, new Point(x, y), 2.4, 2.4);
            dc.DrawEllipse(GetBrush(FireworkShow.Gold, 0.35), null, new Point(x, y), 6, 6);
        }

        foreach (var cue in _activeCues)
        {
            if (cue.Kind == CueKind.Bomb)
            {
                var (x, y) = BombPosition(cue, _time);
                dc.DrawEllipse(GetBrush(Color.FromRgb(255, 60, 30), 0.35), null, new Point(x, y), 14, 14);
                dc.DrawEllipse(GetBrush(Color.FromRgb(40, 40, 40), 1), GetPen(Colors.White, 0.6, 1.5), new Point(x, y), 7, 10);
            }
        }

        var lots = _sparks.Count > 3500;
        foreach (var spark in _sparks)
        {
            var remaining = 1 - spark.Age / spark.Life;
            var alpha = Math.Clamp(remaining * 1.4, 0, 1);
            if (spark.Flicker && remaining < 0.6 && _random.NextDouble() < 0.45) alpha *= 0.15;
            if (spark.Strobe && remaining < 0.85 && ((int)(_time * 18 + spark.Seed * 7) & 1) == 0) alpha *= 0.05;
            if (alpha < 0.03) continue;

            var baseColor = spark.Rainbow ? Hsv(spark.Hue + spark.Age * 260) : spark.Color;
            // Freshly ejected sparks burn white-hot, then settle into their colour.
            var color = spark.Age < 0.12 ? Mix(baseColor, Colors.White, 1 - spark.Age / 0.12) : baseColor;
            var head = new Point(spark.X, spark.Y);
            var tail = new Point(spark.X - spark.Vx * spark.Streak, spark.Y - spark.Vy * spark.Streak);
            var size = spark.Size * (0.5 + 0.5 * remaining);

            dc.DrawLine(GetPen(color, alpha * 0.8, size), tail, head);
            if (!lots && spark.Size >= 2 && remaining > 0.3)
                dc.DrawEllipse(GetBrush(color, alpha * 0.18), null, head, size * 2.6, size * 2.6);
            dc.DrawEllipse(GetBrush(color, alpha), null, head, size * 0.75, size * 0.75);
        }

        foreach (var piece in _confetti)
        {
            var alpha = Math.Clamp((piece.Life - piece.Age) / 0.8, 0, 1);
            var length = piece.Size * (0.3 + 0.7 * Math.Abs(Math.Cos(piece.Flip)));
            var dx = Math.Cos(piece.Angle) * length;
            var dy = Math.Sin(piece.Angle) * length;
            dc.DrawLine(GetPen(piece.Color, alpha, piece.Size * 0.55, flat: true),
                new Point(piece.X - dx, piece.Y - dy), new Point(piece.X + dx, piece.Y + dy));
        }

        var coinFill = GetBrush(Color.FromRgb(255, 200, 40), 1);
        var coinRim = GetPen(Color.FromRgb(176, 112, 0), 1, 2);
        var coinShine = GetBrush(Color.FromRgb(255, 250, 200), 0.9);
        foreach (var coin in _coins)
        {
            var squash = Math.Abs(Math.Cos(coin.Phase));
            var rx = Math.Max(1.2, coin.Size * squash);
            dc.DrawEllipse(coinFill, coinRim, new Point(coin.X, coin.Y), rx, coin.Size);
            if (squash > 0.5)
                dc.DrawEllipse(coinShine, null, new Point(coin.X - rx * 0.3, coin.Y - coin.Size * 0.35), rx * 0.25, coin.Size * 0.2);
        }

        foreach (var cue in _activeCues)
            if (cue.Kind == CueKind.Eagle)
                DrawEagle(dc, cue);

        DrawTitle(dc);
        DrawMarquee(dc, dim);
    }

    private void DrawSunburst(DrawingContext dc, double dim)
    {
        var alpha = TitleAlpha() * dim;
        if (alpha <= 0) return;

        const int rays = 18;
        var length = Math.Sqrt(_focus.Width * _focus.Width + _focus.Height * _focus.Height);
        var spin = _time * 0.6;
        for (var i = 0; i < rays; i++)
        {
            var a = spin + i * Math.PI * 2 / rays;
            var w = Math.PI / rays * 0.55;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(_center, true, true);
                ctx.LineTo(new Point(_center.X + Math.Cos(a - w) * length, _center.Y + Math.Sin(a - w) * length), false, false);
                ctx.LineTo(new Point(_center.X + Math.Cos(a + w) * length, _center.Y + Math.Sin(a + w) * length), false, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(GetBrush(Hsv(i * 40 + _time * 90), alpha * 0.14), null, geometry);
        }
    }

    private void DrawEagle(DrawingContext dc, Cue cue)
    {
        var (x, y, size, flap) = EaglePose(cue, _time);
        var dir = cue.X > 0 ? 1.0 : -1.0;

        Point P(double px, double py) => new(x + px * size * dir, y + py * size);

        StreamGeometry Shape(params Point[] points)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(points[0], true, true);
                ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
            }
            g.Freeze();
            return g;
        }

        StreamGeometry Wing(double f) => Shape(
            P(0.12, -0.02), P(-0.02, -0.45 * f), P(-0.28, -0.88 * f), P(-0.42, -0.84 * f), P(-0.36, -0.66 * f),
            P(-0.5, -0.6 * f), P(-0.4, -0.46 * f), P(-0.54, -0.38 * f), P(-0.4, -0.26 * f), P(-0.5, -0.16 * f), P(-0.2, 0.04));

        var glow = GetPen(FireworkShow.Gold, 0.55, 5);
        var outline = GetPen(Color.FromRgb(255, 230, 150), 1, 1.6);
        var brown = GetBrush(Color.FromRgb(92, 56, 24), 1);
        var dark = GetBrush(Color.FromRgb(60, 36, 16), 1);

        dc.DrawGeometry(dark, outline, Wing(flap * 0.75 - 0.15));
        dc.DrawGeometry(brown, glow, Shape(P(-0.34, 0), P(-0.64, -0.1), P(-0.66, 0.12), P(-0.36, 0.08)));
        dc.DrawEllipse(brown, glow, P(0, 0.02), size * 0.38, size * 0.11);
        dc.DrawGeometry(brown, outline, Wing(flap));
        dc.DrawEllipse(GetBrush(Colors.White, 1), outline, P(0.4, -0.06), size * 0.1, size * 0.09);
        dc.DrawGeometry(GetBrush(Color.FromRgb(255, 200, 0), 1), null, Shape(P(0.47, -0.09), P(0.6, -0.03), P(0.47, -0.01)));
        dc.DrawEllipse(GetBrush(Colors.Black, 1), null, P(0.43, -0.08), size * 0.015, size * 0.015);
    }

    private double TitleAlpha()
    {
        var p = _time - 0.05;
        if (p < 0) return 0;
        return Math.Clamp(p / 0.12, 0, 1) * Math.Clamp((FireworksWindow.ShowSeconds - 0.6 - _time) / 1.0, 0, 1);
    }

    private void DrawTitle(DrawingContext dc)
    {
        var alpha = TitleAlpha();
        if (alpha <= 0) return;

        var titleSize = _focus.Width * 0.085;
        var subtitleSize = _focus.Width * 0.038;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _title ??= BuildText(Loc.T("fireworks.title"), titleSize, new Point(_center.X, _center.Y), pixelsPerDip);
        _subtitle ??= BuildText(Loc.T("fireworks.sub"), subtitleSize, new Point(_center.X, _center.Y + titleSize * 0.95), pixelsPerDip);

        // Punches in huge and wobbles into place; punches again at the finale jackpot.
        var p = _time - 0.05;
        var scale = 1 + 1.8 * Math.Exp(-p * 4.5) * Math.Cos(p * 13) + 0.035 * Math.Sin(_time * 11);
        var q = _time - FireworkShow.FinaleJackpotAt;
        if (q > 0) scale += 0.6 * Math.Exp(-q * 5) * Math.Cos(q * 13);
        scale = Math.Max(0.2, scale);

        dc.PushOpacity(alpha);
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, _center.X * (1 - scale), _center.Y * (1 - scale)));

        var hue = _time * 220;
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0.3) };
        for (var i = 0; i <= 6; i++)
            rainbow.GradientStops.Add(new GradientStop(Hsv(hue + i * 60, 0.85), i / 6.0));
        rainbow.Freeze();

        dc.PushTransform(new TranslateTransform(titleSize * 0.05, titleSize * 0.07));
        dc.DrawGeometry(GetBrush(Colors.Black, 0.55), null, _title);
        dc.Pop();
        dc.DrawGeometry(null, GetPen(Hsv(hue), 0.35, titleSize * 0.22), _title);
        dc.DrawGeometry(rainbow, GetPen(Colors.White, 1, titleSize * 0.035), _title);

        var blink = ((int)(_time * 6) & 1) == 0;
        dc.DrawGeometry(null, GetPen(Color.FromRgb(255, 40, 40), 0.5, subtitleSize * 0.25), _subtitle);
        dc.DrawGeometry(GetBrush(blink ? Color.FromRgb(255, 215, 0) : Colors.White, 1),
            GetPen(Color.FromRgb(140, 0, 0), 1, subtitleSize * 0.06), _subtitle);

        dc.Pop();
        dc.Pop();
    }

    private static Geometry BuildText(string text, double size, Point center, double pixelsPerDip)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI Black, Arial Black, Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("de-AT"), FlowDirection.LeftToRight, typeface, size, Brushes.White, pixelsPerDip);
        var geometry = formatted.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;
        geometry.Transform = new TranslateTransform(center.X - bounds.X - bounds.Width / 2, center.Y - bounds.Y - bounds.Height / 2);
        geometry.Freeze();
        return geometry;
    }

    private void DrawMarquee(DrawingContext dc, double dim)
    {
        if (dim <= 0) return;
        var step = (int)(_time * 10);
        for (var i = 0; i < _bulbs.Count; i++)
        {
            var lit = (i + step) % 3 == 0;
            var color = lit ? Hsv(i * 25 + _time * 120) : Color.FromRgb(255, 190, 80);
            if (lit) dc.DrawEllipse(GetBrush(color, 0.35 * dim), null, _bulbs[i], 15, 15);
            dc.DrawEllipse(GetBrush(color, (lit ? 1 : 0.3) * dim), null, _bulbs[i], 6, 6);
        }
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Color Quantize(Color c) => Color.FromRgb((byte)(c.R & 0xF0), (byte)(c.G & 0xF0), (byte)(c.B & 0xF0));

    private SolidColorBrush GetBrush(Color color, double alpha)
    {
        color = Quantize(color);
        var level = (int)Math.Round(Math.Clamp(alpha, 0, 1) * (AlphaLevels - 1));
        if (_brushes.TryGetValue((color, level), out var brush)) return brush;

        brush = new SolidColorBrush(Color.FromArgb((byte)(level * 255 / (AlphaLevels - 1)), color.R, color.G, color.B));
        brush.Freeze();
        _brushes[(color, level)] = brush;
        return brush;
    }

    private Pen GetPen(Color color, double alpha, double thickness, bool flat = false)
    {
        color = Quantize(color);
        var level = (int)Math.Round(Math.Clamp(alpha, 0, 1) * (AlphaLevels - 1));
        var width = (int)Math.Round(thickness * 2);
        if (_pens.TryGetValue((color, level, width, flat), out var pen)) return pen;

        var cap = flat ? PenLineCap.Flat : PenLineCap.Round;
        pen = new Pen(GetBrush(color, alpha), Math.Max(0.5, width / 2.0)) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        _pens[(color, level, width, flat)] = pen;
        return pen;
    }
}

/// <summary>
/// Synthesizes the whole soundtrack as one in-memory stereo WAV: firework bangs, falling bombs, a tank with
/// cannon, machine-gun bursts, eagle screams, a slot-machine jackpot and a brass rendition of the US anthem's
/// opening (public-domain melody). Everything is generated here; no recordings are bundled.
/// </summary>
internal static class FireworkSound
{
    private const int SampleRate = 22050;
    private const double TwoPi = Math.PI * 2;

    private sealed class Track
    {
        public readonly float[] L;
        public readonly float[] R;
        private readonly Random _random = new();

        public Track(int length)
        {
            L = new float[length];
            R = new float[length];
        }

        public double Noise() => _random.NextDouble() * 2 - 1;
        public double Next() => _random.NextDouble();

        public void Add(int i, double v, double pan)
        {
            if ((uint)i >= (uint)L.Length) return;
            pan = Math.Clamp(pan, 0, 1);
            L[i] += (float)(v * Math.Sqrt(1 - pan));
            R[i] += (float)(v * Math.Sqrt(pan));
        }
    }

    public static MemoryStream Render(ShowPlan show, double screenWidth)
    {
        var track = new Track((int)((FireworksWindow.ShowSeconds + 1.0) * SampleRate));
        double Pan(double x) => Math.Clamp(x / Math.Max(1, screenWidth), 0.05, 0.95);

        foreach (var shell in show.Shells)
            ShellSound(track, shell, Pan(shell.TargetX));

        Anthem(track, 1.2, 0.42);

        var cannons = show.Cues.Where(c => c.Kind == CueKind.Cannon).ToList();
        if (cannons.Count > 0)
            TankEngine(track, cannons.Min(c => c.At) - 0.8, cannons.Max(c => c.At) + 1.2);

        foreach (var cue in show.Cues)
        {
            switch (cue.Kind)
            {
                case CueKind.Jackpot: Jackpot(track, cue.At); break;
                case CueKind.Eagle: Eagle(track, cue.At, cue.X > 0 ? 0.25 : 0.75); break;
                case CueKind.Gun: Gun(track, cue.At, cue.Duration, Pan(cue.X)); break;
                case CueKind.Cannon: Cannon(track, cue.At, Pan(cue.X)); break;
                case CueKind.Bomb: Bomb(track, cue.At, Pan(cue.X)); break;
            }
        }

        return Encode(track);
    }

    private static void ShellSound(Track track, Shell shell, double pan)
    {
        // Quiet rising whoosh during the flight.
        var start = (int)(shell.LaunchAt * SampleRate);
        var flight = (int)(shell.FlightSeconds * SampleRate);
        var lp = 0.0;
        for (var i = 0; i < flight; i++)
        {
            var p = i / (double)flight;
            lp += (0.05 + p * 0.25) * (track.Noise() - lp);
            track.Add(start + i, lp * 0.07 * Math.Sin(Math.PI * p), pan);
        }

        // The bang: low-passed noise burst plus a sub-bass thump.
        var burst = (int)(shell.BurstAt * SampleRate);
        var loud = 0.45 + 0.3 * shell.Scale;
        lp = 0;
        for (var i = 0; i < (int)(1.6 * SampleRate); i++)
        {
            var t = i / (double)SampleRate;
            var envelope = Math.Min(1, t / 0.003) * Math.Exp(-t / 0.28);
            lp += (0.12 + 0.6 * Math.Exp(-t / 0.05)) * (track.Noise() - lp);
            var thump = Math.Sin(TwoPi * (55 - 20 * Math.Min(1, t / 0.2)) * t) * Math.Exp(-t / 0.12);
            track.Add(burst + i, (lp * 1.1 * envelope + thump * 0.9) * loud, pan);
        }

        // Crackle for glittering effects.
        if (shell.Kind is ShellKind.Glitter or ShellKind.Willow or ShellKind.Crossette or ShellKind.Palm or ShellKind.Strobe)
            Crackle(track, shell.BurstAt + 0.35, 1.4, 40 + (int)(track.Next() * 40), pan);
    }

    private static void Crackle(Track track, double from, double span, int pops, double pan)
    {
        for (var c = 0; c < pops; c++)
        {
            var at = (int)((from + track.Next() * span) * SampleRate);
            var amp = 0.12 + track.Next() * 0.22;
            var length = 40 + (int)(track.Next() * 120);
            for (var i = 0; i < length; i++)
                track.Add(at + i, track.Noise() * amp * Math.Exp(-i / 25.0), pan);
        }
    }

    private static void Bomb(Track track, double at, double pan)
    {
        // Falling whistle, then a huge blast with a long rumble and raining debris.
        var whistleStart = (int)((at - Cue.BombFallSeconds) * SampleRate);
        var phase = 0.0;
        var whistleLength = (int)(Cue.BombFallSeconds * SampleRate);
        for (var i = 0; i < whistleLength; i++)
        {
            var p = i / (double)whistleLength;
            phase += TwoPi * 1900 * Math.Pow(380.0 / 1900, p) / SampleRate;
            track.Add(whistleStart + i, Math.Sin(phase) * (0.04 + 0.2 * p), pan);
        }

        var start = (int)(at * SampleRate);
        double lp = 0, subPhase = 0;
        for (var i = 0; i < (int)(3.2 * SampleRate); i++)
        {
            var t = i / (double)SampleRate;
            var crack = track.Noise() * Math.Exp(-t / 0.01) * 1.2;
            lp += (0.03 + 0.5 * Math.Exp(-t / 0.1)) * (track.Noise() - lp);
            var body = lp * Math.Exp(-t / 0.9) * 1.8;
            subPhase += TwoPi * (48 - 22 * Math.Min(1, t / 0.5)) / SampleRate;
            var sub = Math.Sin(subPhase) * Math.Exp(-t / 0.7) * 1.6;
            track.Add(start + i, crack + body + sub, pan);
        }

        Crackle(track, at + 0.4, 2.2, 80, pan);
    }

    private static void Cannon(Track track, double at, double pan)
    {
        var start = (int)(at * SampleRate);
        double lp = 0, boomPhase = 0;
        for (var i = 0; i < (int)(2.2 * SampleRate); i++)
        {
            var t = i / (double)SampleRate;
            var crack = track.Noise() * Math.Exp(-t / 0.006) * 1.3;
            boomPhase += TwoPi * (75 - 35 * Math.Min(1, t / 0.3)) / SampleRate;
            var boom = Math.Sin(boomPhase) * Math.Exp(-t / 0.35) * 1.3;
            lp += (0.05 + 0.4 * Math.Exp(-t / 0.08)) * (track.Noise() - lp);
            var rumble = lp * Math.Exp(-t / 0.6) * 1.4;
            var ring = (Math.Sin(TwoPi * 190 * t) + 0.6 * Math.Sin(TwoPi * 433 * t)) * Math.Exp(-t / 0.35) * 0.12;
            track.Add(start + i, crack + boom + rumble + ring, pan);
        }
    }

    private static void TankEngine(Track track, double from, double to)
    {
        // Diesel rumble plus clanking tracks, driving from left to right.
        var start = (int)(from * SampleRate);
        var duration = to - from;
        double phase = 0, lp = 0;
        for (var i = 0; i < (int)(duration * SampleRate); i++)
        {
            var t = i / (double)SampleRate;
            var envelope = Math.Min(1, t / 0.8) * Math.Clamp((duration - t) / 0.8, 0, 1);
            phase += (32 + 4 * Math.Sin(TwoPi * 0.4 * t)) / SampleRate;
            var saw = 2 * (phase - Math.Floor(phase)) - 1;
            lp += 0.08 * (track.Noise() - lp);
            var tread = t * 7 - Math.Floor(t * 7);
            var clank = tread < 0.04 ? track.Noise() * 0.5 * (1 - tread / 0.04) : 0;
            track.Add(start + i, (saw * 0.35 + lp * 0.6 + clank) * envelope * 0.55, 0.15 + 0.7 * t / duration);
        }
    }

    private static void Gun(Track track, double at, double duration, double pan)
    {
        var shots = (int)(duration * 14);
        for (var s = 0; s < shots; s++)
        {
            var start = (int)((at + s / 14.0 + track.Next() * 0.006) * SampleRate);
            var lp = 0.0;
            for (var i = 0; i < (int)(0.09 * SampleRate); i++)
            {
                var t = i / (double)SampleRate;
                var crack = track.Noise() * Math.Exp(-t / 0.004);
                lp += 0.6 * (track.Noise() - lp);
                var thump = Math.Sin(TwoPi * 95 * t) * Math.Exp(-t / 0.03);
                track.Add(start + i, (crack * 0.9 + lp * 0.7 * Math.Exp(-t / 0.018) + thump * 0.6) * 0.75, pan);
            }
        }
    }

    private static void Eagle(Track track, double at, double pan)
    {
        // Short "kee", then the long descending scream; a quieter echo from the other side.
        for (var echo = 0; echo < 2; echo++)
        {
            var gain = echo == 0 ? 0.42 : 0.14;
            var offset = echo * 0.23;
            var side = echo == 0 ? pan : 1 - pan;
            Screech(track, at + offset, 0.22, 3300, 2900, gain * 0.8, side);
            Screech(track, at + offset + 0.28, 1.25, 3100, 1700, gain, side);
        }
    }

    private static void Screech(Track track, double at, double duration, double f0, double f1, double amp, double pan)
    {
        var start = (int)(at * SampleRate);
        double phase = 0, lp = 0;
        for (var i = 0; i < (int)(duration * SampleRate); i++)
        {
            var t = i / (double)SampleRate;
            var p = t / duration;
            var f = f0 * Math.Pow(f1 / f0, Math.Pow(p, 1.4)) * (1 + 0.04 * Math.Sin(TwoPi * 27 * t));
            phase += TwoPi * f / SampleRate;
            var envelope = Math.Min(1, t / 0.03) * Math.Clamp((duration - t) / 0.25, 0, 1);
            var rasp = 0.65 + 0.35 * Math.Sin(TwoPi * 95 * t);
            lp += 0.5 * (track.Noise() - lp);
            var v = (Math.Sin(phase) + 0.45 * Math.Sin(2 * phase + 0.3) + 0.2 * Math.Sin(3 * phase)) * rasp + lp * 0.25;
            track.Add(start + i, v * envelope * amp, pan);
        }
    }

    private static void Jackpot(Track track, double at)
    {
        // Rising slot-machine arpeggio, a big chord and a shower of coin dings.
        int[] arpeggio = { 72, 76, 79, 84 };
        for (var rep = 0; rep < 3; rep++)
            for (var k = 0; k < arpeggio.Length; k++)
                Chip(track, at + (rep * 4 + k) * 0.065, 0.09, Midi(arpeggio[k] + rep * 2), 0.13);

        foreach (var note in new[] { 76, 79, 84, 88 })
            Chip(track, at + 0.8, 0.55, Midi(note), 0.08);

        double[] bells = { 2093, 2349, 2637, 3136 };
        for (var i = 0; i < 22; i++)
            Bell(track, at + 0.1 + track.Next() * 2.4, bells[(int)(track.Next() * bells.Length)], 0.16, 0.15 + track.Next() * 0.7);
    }

    private static void Chip(Track track, double at, double duration, double freq, double amp)
    {
        var start = (int)(at * SampleRate);
        for (var i = 0; i < (int)(duration * SampleRate); i++)
        {
            var t = i / (double)SampleRate;
            var square = Math.Sin(TwoPi * freq * t) > 0 ? 1 : -1;
            track.Add(start + i, square * amp * Math.Exp(-t / (duration * 0.6)), 0.5);
        }
    }

    private static void Bell(Track track, double at, double freq, double amp, double pan)
    {
        var start = (int)(at * SampleRate);
        for (var i = 0; i < (int)(0.6 * SampleRate); i++)
        {
            var t = i / (double)SampleRate;
            var v = (Math.Sin(TwoPi * freq * t) + 0.5 * Math.Sin(TwoPi * freq * 2.76 * t)) * Math.Exp(-t / 0.18);
            track.Add(start + i, v * amp, pan);
        }
    }

    // Opening of "The Star-Spangled Banner" (public domain) as MIDI note / beats.
    private static readonly (int Note, double Beats)[] AnthemNotes =
    {
        (67, .75), (64, .25), (60, 1), (64, 1), (67, 1), (72, 2),
        (76, .75), (74, .25), (72, 1), (64, 1), (66, 1), (67, 2),
        (67, .5), (67, .5), (76, 1.5), (74, .5), (72, 1), (71, 2),
        (69, .75), (71, .25), (72, 1), (72, 1), (67, 1), (64, 1), (60, 2)
    };

    private static void Anthem(Track track, double at, double beatSeconds)
    {
        var t = at;
        foreach (var (note, beats) in AnthemNotes)
        {
            var duration = beats * beatSeconds;
            Brass(track, t, duration * 0.95, Midi(note), 0.3);
            Brass(track, t, duration * 0.95, Midi(note - 12), 0.16);
            t += duration;
        }
    }

    private static void Brass(Track track, double at, double duration, double freq, double amp)
    {
        var start = (int)(at * SampleRate);
        var phase = 0.0;
        for (var i = 0; i < (int)((duration + 0.12) * SampleRate); i++)
        {
            var t = i / (double)SampleRate;
            var envelope = Math.Min(1, t / 0.04) * (t < duration ? 1 : Math.Exp(-(t - duration) / 0.04));
            var vibrato = 1 + 0.006 * Math.Sin(TwoPi * 5.5 * t) * Math.Min(1, t / 0.25);
            phase += TwoPi * freq * vibrato / SampleRate;
            var bright = 0.55 + 0.4 * Math.Min(1, t / 0.06);
            double v = 0, weight = 1;
            for (var k = 1; k <= 7; k++)
            {
                v += Math.Sin(k * phase) * weight / k;
                weight *= bright;
            }
            track.Add(start + i, v * envelope * amp, 0.5);
        }
    }

    private static double Midi(int note) => 440 * Math.Pow(2, (note - 69) / 12.0);

    private static MemoryStream Encode(Track track)
    {
        var length = track.L.Length;
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var dataBytes = length * 2 * sizeof(short);
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write((short)2); // stereo
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2 * sizeof(short));
            writer.Write((short)(2 * sizeof(short)));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataBytes);

            for (var i = 0; i < length; i++)
            {
                // Soft clipping keeps the overlapping chaos loud without harsh digital distortion.
                writer.Write((short)(Math.Tanh(track.L[i] * 0.85) * 31000));
                writer.Write((short)(Math.Tanh(track.R[i] * 0.85) * 31000));
            }
        }

        stream.Position = 0;
        return stream;
    }
}
