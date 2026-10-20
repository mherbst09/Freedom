using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ViennaAtomicClock.Weather;

public enum WeatherScene { ClearDay, ClearNight, CloudyDay, CloudyNight, Rain, Thunder, Snow, Fog }

/// <summary>
/// Animated sky behind the weather page, built in layers so each frame only redraws what moves:
/// the sky (gradient, sun glow or moon) is drawn once per weather change and crossfaded by the compositor;
/// sun rays sway by a composition animation; only the particle layer (clouds, haze, stars, rain, snow, fog,
/// lightning) is redrawn per frame. All brushes and geometries are created once and frozen. Animation is time
/// based, runs only while visible and not minimised, and lowers its particle density by itself when frames get slow.
/// </summary>
internal sealed class WeatherBackdrop : Grid
{
    private readonly SkyLayer _skyBack = new();
    private readonly SkyLayer _skyFront = new();
    private readonly RaysLayer _rays = new();
    private readonly ParticleLayer _particles = new();
    private readonly Border _scrim = new() { Background = SkyPaint.Scrim, IsHitTestVisible = false };
    private bool _running;
    private bool _animate = true;
    private TimeSpan _lastRender;
    private double _radius;
    private (WeatherScene, int)? _shown;

    public WeatherBackdrop()
    {
        IsHitTestVisible = false;
        Children.Add(_skyBack);
        Children.Add(_skyFront);
        Children.Add(_rays);
        Children.Add(_particles);
        Children.Add(_scrim);
        _rays.Visibility = Visibility.Collapsed;
        IsVisibleChanged += (_, _) => UpdateRunning();
        Loaded += (_, _) => UpdateRunning();
        Unloaded += (_, _) => Stop();
        SizeChanged += (_, _) => UpdateClip();
    }

    public double Radius
    {
        get => _radius;
        set { _radius = value; UpdateClip(); }
    }

    /// <summary>Particle density factor (0.35..1) after automatic adjustment.</summary>
    public double Density => _particles.Density;

    public WeatherScene Scene => _particles.Scene;

    public bool Animate
    {
        get => _animate;
        set
        {
            _animate = value;
            UpdateRunning();
            _particles.InvalidateVisual();
        }
    }

    private void UpdateClip()
    {
        if (ActualWidth <= 0) return;
        var clip = new RectangleGeometry(new Rect(RenderSize), _radius, _radius);
        clip.Freeze();
        Clip = clip;
    }

    /// <summary>Sets the sky. <paramref name="twilight"/> 0..1 blends in sunrise/sunset colours; the change crossfades.</summary>
    public void SetScene(WeatherScene scene, double twilight, double cloudCover, double intensity)
    {
        var level = (int)Math.Round(Math.Clamp(twilight, 0, 1) * 10);
        if (_shown != (scene, level))
        {
            var first = _shown is null;
            _shown = (scene, level);
            // The old sky stays underneath while the new one fades in on top (compositor animation, no redraw).
            _skyBack.Show(_skyFront.Scene, _skyFront.Twilight);
            _skyFront.Show(scene, level);
            if (_animate && !first && SystemParameters.ClientAreaAnimation)
                _skyFront.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.6)) { EasingFunction = new SineEase() });
            else
            {
                _skyFront.BeginAnimation(OpacityProperty, null);
                _skyFront.Opacity = 1;
            }
            _rays.Visibility = scene == WeatherScene.ClearDay ? Visibility.Visible : Visibility.Collapsed;
        }
        _particles.Configure(scene, cloudCover, intensity);
    }

    public static WeatherScene SceneFor(WeatherCondition condition, bool isDay) => condition switch
    {
        WeatherCondition.Clear or WeatherCondition.MostlyClear => isDay ? WeatherScene.ClearDay : WeatherScene.ClearNight,
        WeatherCondition.PartlyCloudy or WeatherCondition.Cloudy => isDay ? WeatherScene.CloudyDay : WeatherScene.CloudyNight,
        WeatherCondition.Fog => WeatherScene.Fog,
        WeatherCondition.Thunderstorm or WeatherCondition.Hail => WeatherScene.Thunder,
        WeatherCondition.Snow or WeatherCondition.HeavySnow or WeatherCondition.SnowShowers => WeatherScene.Snow,
        _ => WeatherScene.Rain
    };

    /// <summary>Frozen sky gradient for previews (place list).</summary>
    public static Brush SkyBrush(WeatherScene scene) => SkyPaint.Gradient(scene, 0);

    /// <summary>Moves the particles forward by <paramref name="dt"/> seconds (public for the performance test).</summary>
    public void Advance(double dt)
    {
        _particles.Advance(dt);
        _particles.InvalidateVisual();
    }

    /// <summary>The only layer that is redrawn per frame.</summary>
    internal FrameworkElement MovingLayer => _particles;

    private void UpdateRunning()
    {
        var shouldRun = _animate && IsVisible && SystemParameters.ClientAreaAnimation;
        _rays.Sway(shouldRun);
        if (shouldRun && !_running)
        {
            _running = true;
            _lastRender = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }
        else if (!shouldRun) Stop();
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args || args.RenderingTime == _lastRender) return;
        var dt = _lastRender == TimeSpan.Zero ? 1 / 60.0 : (args.RenderingTime - _lastRender).TotalSeconds;
        _lastRender = args.RenderingTime;
        if (Window.GetWindow(this) is { WindowState: WindowState.Minimized }) return;
        _particles.AdjustDensity(dt);
        Advance(Math.Min(dt, 0.1));
    }
}

/// <summary>Shared frozen paint for the sky.</summary>
internal static class SkyPaint
{
    private static readonly Dictionary<(WeatherScene, int), LinearGradientBrush> Gradients = new();
    /// <summary>Horizontal position of sun and moon (away from the big header icon on the right).</summary>
    public const double LightX = 0.6;
    public static readonly Brush Scrim = MakeScrim();
    public static readonly Brush SunGlow = Radial(Color.FromArgb(90, 0xFF, 0xF1, 0xC2));
    public static readonly Brush MoonGlow = Radial(Color.FromArgb(70, 0xD8, 0xDC, 0xFF));
    public static readonly Brush MoonDisc = WeatherIcons.Frozen(Color.FromRgb(0xF2, 0xF1, 0xFA));

    public static LinearGradientBrush Gradient(WeatherScene scene, int twilightLevel)
    {
        if (Gradients.TryGetValue((scene, twilightLevel), out var brush)) return brush;
        var (top, middle, bottom) = scene switch
        {
            WeatherScene.ClearDay => (C(0x2A6FD6), C(0x4F95E8), C(0x9CC8F2)),
            WeatherScene.ClearNight => (C(0x0B1230), C(0x1E2456), C(0x3B2F6B)),
            WeatherScene.CloudyDay => (C(0x5D7189), C(0x7D90A6), C(0xA8B5C4)),
            WeatherScene.CloudyNight => (C(0x161C2B), C(0x262F42), C(0x3A4458)),
            WeatherScene.Rain => (C(0x2E3846), C(0x46525F), C(0x5F6A76)),
            WeatherScene.Thunder => (C(0x1E222B), C(0x31363F), C(0x474C55)),
            WeatherScene.Snow => (C(0x8EA8C6), C(0xB9CBDD), C(0xE4ECF4)),
            _ => (C(0x8A9199), C(0xA9AFB5), C(0xC8CCD0))
        };
        // Sunrise/sunset: orange, pink and violet tones blended in by how close the sun is to the horizon.
        var t = twilightLevel / 10.0 * (scene is WeatherScene.Rain or WeatherScene.Thunder ? 0.35 : 0.8);
        top = Mix(top, C(0x3B3F8F), t);
        middle = Mix(middle, C(0xC8648E), t);
        bottom = Mix(bottom, C(0xF39A5B), t);
        brush = new LinearGradientBrush(new GradientStopCollection { new(top, 0), new(middle, 0.55), new(bottom, 1) }, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        Gradients[(scene, twilightLevel)] = brush;
        return brush;
    }

    public static Color C(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Brush Radial(Color center)
    {
        var brush = new RadialGradientBrush(center, Color.FromArgb(0, center.R, center.G, center.B));
        brush.Freeze();
        return brush;
    }

    public static Brush[] Levels(Color color, int count = 20)
    {
        var levels = new Brush[count + 1];
        for (var i = 0; i <= count; i++)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(i * 255 / count), color.R, color.G, color.B));
            brush.Freeze();
            levels[i] = brush;
        }
        return levels;
    }

    private static Brush MakeScrim()
    {
        // Soft darkening at the top keeps white header text readable on bright skies.
        var brush = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(70, 0, 0, 0), 0), new(Color.FromArgb(18, 0, 0, 0), 0.35), new(Color.FromArgb(0, 0, 0, 0), 0.6)
        }, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Gradient plus sun glow or moon; redrawn only when the situation changes.</summary>
internal sealed class SkyLayer : FrameworkElement
{
    public WeatherScene Scene { get; private set; } = WeatherScene.CloudyDay;
    public int Twilight { get; private set; }

    public SkyLayer() => IsHitTestVisible = false;

    public void Show(WeatherScene scene, int twilight)
    {
        Scene = scene;
        Twilight = twilight;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        dc.DrawRectangle(SkyPaint.Gradient(Scene, Twilight), null, new Rect(size));
        switch (Scene)
        {
            case WeatherScene.ClearDay:
                dc.DrawEllipse(SkyPaint.SunGlow, null, new Point(size.Width * SkyPaint.LightX, size.Height * 0.06), 260, 260);
                break;
            case WeatherScene.ClearNight:
            case WeatherScene.CloudyNight:
                var center = new Point(size.Width * SkyPaint.LightX, size.Height * 0.1);
                dc.PushOpacity(Scene == WeatherScene.CloudyNight ? 0.45 : 1);
                dc.DrawEllipse(SkyPaint.MoonGlow, null, center, 170, 170);
                dc.DrawEllipse(SkyPaint.MoonDisc, null, center, 26, 26);
                dc.Pop();
                break;
        }
    }
}

/// <summary>Light rays from the sun; they sway slowly through a composition animation, so they never cost a redraw.</summary>
internal sealed class RaysLayer : FrameworkElement
{
    private static readonly Brush RayBrush = WeatherIcons.Frozen(Color.FromArgb(26, 0xFF, 0xF4, 0xD6));
    private static readonly Geometry Rays = MakeRays();
    private readonly RotateTransform _turn = new();
    private bool _swaying;

    public RaysLayer()
    {
        IsHitTestVisible = false;
        RenderTransform = _turn;
        SizeChanged += (_, _) =>
        {
            _turn.CenterX = ActualWidth * SkyPaint.LightX;
            _turn.CenterY = ActualHeight * 0.06;
        };
    }

    public void Sway(bool on)
    {
        if (on == _swaying) return;
        _swaying = on;
        _turn.BeginAnimation(RotateTransform.AngleProperty, on
            ? new DoubleAnimation(-4, 4, TimeSpan.FromSeconds(26)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }
            : null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0) return;
        dc.PushTransform(new TranslateTransform(ActualWidth * SkyPaint.LightX, ActualHeight * 0.06));
        dc.DrawGeometry(RayBrush, null, Rays);
        dc.Pop();
    }

    private static Geometry MakeRays()
    {
        var group = new GeometryGroup();
        foreach (var angle in new[] { 100.0, 118, 134, 152, 170 })
        {
            var a1 = (angle - 3) * Math.PI / 180;
            var a2 = (angle + 3) * Math.PI / 180;
            group.Children.Add(new PathGeometry(new[]
            {
                new PathFigure(new Point(0, 0), new PathSegment[]
                {
                    new LineSegment(new Point(Math.Cos(a1) * 1400, Math.Sin(a1) * 1400), false),
                    new LineSegment(new Point(Math.Cos(a2) * 1400, Math.Sin(a2) * 1400), false)
                }, true)
            }));
        }
        group.Freeze();
        return group;
    }
}

/// <summary>Everything that moves: clouds, haze, stars, rain, snow, fog and the rare lightning.</summary>
internal sealed class ParticleLayer : FrameworkElement
{
    private const int LevelCount = 20;

    private sealed class Particle
    {
        public double X, Y, Speed, Size, Phase, Depth;
    }

    private static readonly Brush[] WhiteLevels = SkyPaint.Levels(Colors.White);
    private static readonly Brush[] CloudLevels = SkyPaint.Levels(Color.FromRgb(0xF4, 0xF6, 0xFA));
    private static readonly Brush[] DarkCloudLevels = SkyPaint.Levels(Color.FromRgb(0x55, 0x5E, 0x6C));
    private static readonly Pen RainPen = MakePen(Color.FromArgb(105, 0xD6, 0xE6, 0xFF), 1.2);
    private static readonly Pen RainPenFar = MakePen(Color.FromArgb(60, 0xD6, 0xE6, 0xFF), 0.9);
    private static readonly Pen BoltPen = MakePen(Color.FromArgb(230, 0xFF, 0xF6, 0xD8), 2.2);
    private static readonly Brush HazeBrush = SkyPaint.Radial(Color.FromArgb(40, 255, 255, 255));
    private static readonly Brush FogBrush = SkyPaint.Radial(Color.FromArgb(78, 0xEE, 0xF0, 0xF3));
    private static readonly Geometry CloudGeometry = MakeCloud();
    private static readonly Geometry[] Bolts =
    {
        Freeze(Geometry.Parse("M 0,0 L -18,70 L 6,74 L -14,150 L 30,60 L 6,56 L 22,0")),
        Freeze(Geometry.Parse("M 0,0 L 14,60 L -6,64 L 16,140 L -24,58 L -4,54 L -14,0"))
    };

    private readonly Random _random = new(7);
    private readonly List<Particle> _drops = new();
    private readonly List<Particle> _flakes = new();
    private readonly List<Particle> _stars = new();
    private readonly List<Particle> _haze = new();
    private readonly List<Particle> _clouds = new();
    private readonly List<Particle> _fog = new();
    private readonly List<MatrixTransform> _cloudTransforms = new();
    private readonly TranslateTransform _boltShift = new();

    private double _cloudCover = 0.5;
    private double _intensity = 0.5;
    private double _time;
    private double _slowSeconds;
    private double _fastSeconds;
    private double _flash;
    private double _nextFlash = 6;
    private int _bolt;
    private Size _builtFor;

    public ParticleLayer() => IsHitTestVisible = false;

    public WeatherScene Scene { get; private set; } = WeatherScene.CloudyDay;
    public double Density { get; private set; } = 1;

    public void Configure(WeatherScene scene, double cloudCover, double intensity)
    {
        cloudCover = Math.Clamp(cloudCover, 0, 1);
        intensity = Math.Clamp(intensity, 0, 1);
        if (scene == Scene && Math.Abs(cloudCover - _cloudCover) < 0.1 && Math.Abs(intensity - _intensity) < 0.1) return;
        Scene = scene;
        _cloudCover = cloudCover;
        _intensity = intensity;
        _builtFor = default;
        InvalidateVisual();
    }

    /// <summary>Fewer particles when frames take longer than ~22 ms for two seconds; slowly back up when it is smooth again.</summary>
    public void AdjustDensity(double dt)
    {
        if (dt > 0.5) return; // window was hidden or the PC was busy for a moment
        if (dt > 1 / 45.0) { _slowSeconds += dt; _fastSeconds = 0; }
        else { _fastSeconds += dt; _slowSeconds = Math.Max(0, _slowSeconds - dt / 2); }

        if (_slowSeconds > 2 && Density > 0.35)
        {
            Density = Math.Max(0.35, Density * 0.75);
            _slowSeconds = 0;
            _builtFor = default;
        }
        else if (_fastSeconds > 12 && Density < 1)
        {
            Density = Math.Min(1, Density * 1.12);
            _fastSeconds = 0;
            _builtFor = default;
        }
    }

    public void Advance(double dt)
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        EnsureParticles(size);
        _time += dt;

        foreach (var drop in _drops)
        {
            drop.Y += drop.Speed * dt;
            drop.X -= drop.Speed * 0.18 * dt;
            if (drop.Y > size.Height + 20)
            {
                drop.Y = -_random.NextDouble() * 60;
                drop.X = _random.NextDouble() * (size.Width + 80);
            }
        }

        foreach (var flake in _flakes)
        {
            flake.Y += flake.Speed * dt;
            flake.X += Math.Sin(_time * 0.8 + flake.Phase) * 12 * flake.Depth * dt;
            if (flake.Y > size.Height + 10)
            {
                flake.Y = -10;
                flake.X = _random.NextDouble() * size.Width;
            }
        }

        Drift(_clouds, dt, size);
        Drift(_fog, dt, size);
        Drift(_haze, dt, size);

        if (Scene == WeatherScene.Thunder)
        {
            _nextFlash -= dt;
            if (_nextFlash <= 0)
            {
                // Rare, soft flashes 9–22 s apart, never a bright full-screen strobe.
                _flash = 1;
                _bolt = _random.Next(Bolts.Length);
                _boltShift.X = size.Width * (0.2 + _random.NextDouble() * 0.6);
                _nextFlash = 9 + _random.NextDouble() * 13;
            }
            _flash = Math.Max(0, _flash - dt / 0.28);
        }
        else _flash = 0;
    }

    private void Drift(List<Particle> items, double dt, Size size)
    {
        foreach (var item in items)
        {
            item.X += item.Speed * dt;
            if (item.X > size.Width + 400) item.X = -400 - _random.NextDouble() * 200;
        }
    }

    private void EnsureParticles(Size size)
    {
        if (_builtFor == size) return;
        _builtFor = size;
        var budget = Density * Math.Clamp(size.Width * size.Height / 1_000_000.0, 0.2, 2.6);

        _drops.Clear();
        _flakes.Clear();
        _stars.Clear();
        _haze.Clear();
        _clouds.Clear();
        _fog.Clear();

        if (Scene is WeatherScene.Rain or WeatherScene.Thunder)
            for (var i = 0; i < (int)(150 * budget * (0.5 + _intensity)); i++)
            {
                var depth = _random.NextDouble();
                _drops.Add(new Particle
                {
                    X = _random.NextDouble() * (size.Width + 80), Y = _random.NextDouble() * size.Height,
                    Speed = 520 + depth * 520, Size = 10 + depth * 14, Depth = depth
                });
            }

        if (Scene == WeatherScene.Snow)
            for (var i = 0; i < (int)(120 * budget * (0.6 + _intensity)); i++)
            {
                var depth = _random.NextDouble();
                _flakes.Add(new Particle
                {
                    X = _random.NextDouble() * size.Width, Y = _random.NextDouble() * size.Height,
                    Speed = 18 + depth * 46, Size = 1.2 + depth * 3.2, Depth = depth, Phase = _random.NextDouble() * 6.28
                });
            }

        if (Scene is WeatherScene.ClearNight or WeatherScene.CloudyNight)
            for (var i = 0; i < (int)(120 * budget); i++)
                _stars.Add(new Particle
                {
                    X = _random.NextDouble() * size.Width, Y = _random.NextDouble() * size.Height * 0.7,
                    Size = 0.6 + _random.NextDouble() * 1.3, Phase = _random.NextDouble() * 6.28, Speed = 0.4 + _random.NextDouble() * 1.4
                });

        if (Scene == WeatherScene.ClearDay)
            for (var i = 0; i < (int)(6 * budget); i++)
                _haze.Add(new Particle
                {
                    X = _random.NextDouble() * size.Width, Y = _random.NextDouble() * size.Height,
                    Size = 50 + _random.NextDouble() * 80, Speed = 4 + _random.NextDouble() * 8
                });

        var cloudCount = Scene switch
        {
            WeatherScene.ClearDay or WeatherScene.ClearNight => (int)(4 * _cloudCover),
            WeatherScene.Fog => 3,
            _ => 4 + (int)(4 * _cloudCover)
        };
        for (var i = 0; i < cloudCount; i++)
        {
            var depth = _random.NextDouble();
            _clouds.Add(new Particle
            {
                X = _random.NextDouble() * (size.Width + 400) - 200, Y = size.Height * (0.02 + _random.NextDouble() * 0.38),
                Size = 1.6 + depth * 2.4, Speed = 5 + depth * 12, Depth = depth
            });
        }
        while (_cloudTransforms.Count < _clouds.Count) _cloudTransforms.Add(new MatrixTransform());

        if (Scene == WeatherScene.Fog)
            for (var i = 0; i < 5; i++)
                _fog.Add(new Particle
                {
                    X = _random.NextDouble() * size.Width - 200, Y = size.Height * (0.18 + i * 0.16),
                    Size = 0.7 + _random.NextDouble() * 0.5, Speed = 6 + _random.NextDouble() * 10
                });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        EnsureParticles(size);

        foreach (var mote in _haze)
            dc.DrawEllipse(HazeBrush, null, new Point(mote.X, mote.Y + Math.Sin(_time * 0.2 + mote.X * 0.01) * 10), mote.Size, mote.Size * 0.6);

        foreach (var star in _stars)
        {
            var twinkle = 0.55 + 0.45 * Math.Sin(_time * star.Speed + star.Phase);
            dc.DrawRectangle(WhiteLevels[(int)(twinkle * 14)], null, new Rect(star.X - star.Size, star.Y - star.Size, star.Size * 2, star.Size * 2));
        }

        var dark = Scene is WeatherScene.Rain or WeatherScene.Thunder or WeatherScene.CloudyNight;
        for (var i = 0; i < _clouds.Count; i++)
        {
            var cloud = _clouds[i];
            var s = cloud.Size * 60;
            _cloudTransforms[i].Matrix = new Matrix(s, 0, 0, s * 0.62, cloud.X, cloud.Y);
            var alpha = dark ? 0.25 + cloud.Depth * 0.25 : 0.18 + cloud.Depth * 0.3;
            if (Scene is WeatherScene.ClearDay or WeatherScene.ClearNight) alpha *= 0.7;
            dc.PushTransform(_cloudTransforms[i]);
            dc.DrawGeometry((dark ? DarkCloudLevels : CloudLevels)[(int)(alpha * LevelCount)], null, CloudGeometry);
            dc.Pop();
        }

        foreach (var band in _fog)
        {
            var width = size.Width * band.Size;
            dc.DrawEllipse(FogBrush, null, new Point(band.X + width / 2, band.Y), width / 2, 70);
        }

        foreach (var drop in _drops)
            dc.DrawLine(drop.Depth > 0.5 ? RainPen : RainPenFar, new Point(drop.X, drop.Y), new Point(drop.X + drop.Size * 0.18, drop.Y - drop.Size));

        foreach (var flake in _flakes)
            dc.DrawEllipse(WhiteLevels[(int)(8 + flake.Depth * 10)], null, new Point(flake.X, flake.Y), flake.Size, flake.Size);

        if (_flash > 0)
        {
            dc.DrawRectangle(WhiteLevels[Math.Clamp((int)(_flash * 4), 0, LevelCount)], null, new Rect(size));
            if (_flash > 0.55)
            {
                dc.PushTransform(_boltShift);
                dc.DrawGeometry(null, BoltPen, Bolts[_bolt]);
                dc.Pop();
            }
        }
    }

    private static Pen MakePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static Geometry MakeCloud()
    {
        Geometry shape = new RectangleGeometry(new Rect(-1.1, -0.1, 2.2, 0.55), 0.27, 0.27);
        foreach (var (cx, cy, r) in new[] { (-0.55, 0.0, 0.42), (0.0, -0.25, 0.6), (0.6, 0.02, 0.4) })
            shape = new CombinedGeometry(GeometryCombineMode.Union, shape, new EllipseGeometry(new Point(cx, cy), r, r));
        return Freeze(shape.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute));
    }

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}
