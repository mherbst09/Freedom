using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ViennaAtomicClock;

/// <summary>Look of one chibi catgirl (all original characters).</summary>
internal sealed record ChibiStyle(string Name, Color Hair, Color HairShade, Color Eye, Color EyeLight, Color Ribbon, bool TwinTails, bool Bun);

/// <summary>Everything that moves on a chibi for one frame.</summary>
internal struct ChibiPose
{
    public double Blink;      // 0 open … 1 closed
    public bool Happy;        // ^ ^ eyes
    public bool Talking;
    public double WaveArm;    // 0 down … 1 fully raised and waving
    public double WavePhase;
    public double Tail;       // swish angle in degrees
    public double EarTwitch;  // degrees
    public double Legs;       // swing angle in degrees (walking or dangling)
    public bool Sitting;
    public double Bob;        // vertical bounce in units
}

/// <summary>
/// Draws a small, school-friendly chibi catgirl in sailor uniform from vector shapes.
/// Units: the head is about 2 units wide; the origin is the centre of the face.
/// </summary>
internal static class ChibiPainter
{
    public static readonly ChibiStyle[] Styles =
    {
        new("Sakura", C(255, 158, 199), C(224, 111, 160), C(62, 123, 255), C(156, 195, 255), C(255, 79, 123), false, false),
        new("Yuki", C(242, 244, 255), C(185, 190, 224), C(224, 70, 90), C(255, 154, 166), C(106, 91, 255), true, false),
        new("Mochi", C(180, 140, 255), C(138, 98, 217), C(242, 178, 51), C(255, 224, 138), C(47, 191, 138), false, true)
    };

    private static readonly Color Skin = C(255, 231, 218);
    private static readonly Color SkinShade = C(247, 200, 186);
    private static readonly Color Line = C(90, 35, 70);
    private static readonly Color Navy = C(44, 52, 110);
    private static readonly Color Blush = Color.FromArgb(150, 255, 120, 160);

    /// <param name="at">Screen position of the face centre.</param>
    /// <param name="scale">Pixels per unit.</param>
    /// <param name="facing">1 = looking right, -1 = mirrored.</param>
    /// <param name="headOnly">Only head and shoulders (for peeking in from the side).</param>
    public static void Draw(DrawingContext dc, Point at, double scale, ChibiStyle style, ChibiPose pose, double facing = 1, bool headOnly = false, double tilt = 0)
    {
        dc.PushTransform(new TranslateTransform(at.X, at.Y + pose.Bob * scale));
        dc.PushTransform(new RotateTransform(tilt));
        dc.PushTransform(new ScaleTransform(scale * facing, scale));
        var line = Pen(Line, 0.055);

        if (!headOnly)
        {
            DrawTail(dc, style, pose, line);
            if (style.TwinTails) DrawTwinTails(dc, style, line);
            DrawLegs(dc, pose, line);
            DrawBody(dc, style, line);
            DrawArms(dc, pose, line);
        }
        else if (style.TwinTails) DrawTwinTails(dc, style, line);

        DrawHairBack(dc, style, line);
        if (style.Bun) dc.DrawEllipse(Brush(style.Hair), line, new Point(0, -1.12), 0.42, 0.34);
        DrawEars(dc, style, pose, line);
        dc.DrawEllipse(Brush(Skin), line, new Point(0, 0.12), 0.95, 0.85);
        DrawFace(dc, style, pose, line);
        DrawBangs(dc, style, line);
        if (!headOnly && pose.WaveArm > 0)
            DrawArm(dc, new Point(0.46, 0.98), -18 - pose.WaveArm * (100 + Math.Sin(pose.WavePhase) * 25), line);

        // Ribbon in the hair.
        var ribbon = Brush(style.Ribbon);
        dc.PushTransform(new TranslateTransform(0.62, -0.72));
        dc.DrawGeometry(ribbon, Pen(Line, 0.04), Geo("M 0,0 L -0.28,-0.18 L -0.26,0.18 Z M 0,0 L 0.28,-0.18 L 0.26,0.18 Z"));
        dc.DrawEllipse(ribbon, Pen(Line, 0.04), new Point(0, 0), 0.08, 0.08);
        dc.Pop();

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private static void DrawTail(DrawingContext dc, ChibiStyle style, ChibiPose pose, Pen line)
    {
        dc.PushTransform(new RotateTransform(pose.Tail, 0.35, 1.75));
        dc.DrawGeometry(null, Pen(Line, 0.24, round: true), Geo("M 0.35,1.75 C 1.0,1.9 1.35,1.4 1.15,0.95 C 1.05,0.75 1.25,0.6 1.35,0.62"));
        dc.DrawGeometry(null, Pen(style.Hair, 0.16, round: true), Geo("M 0.35,1.75 C 1.0,1.9 1.35,1.4 1.15,0.95 C 1.05,0.75 1.25,0.6 1.35,0.62"));
        dc.Pop();
    }

    private static void DrawTwinTails(DrawingContext dc, ChibiStyle style, Pen line)
    {
        var hair = Brush(style.Hair);
        dc.DrawGeometry(hair, line, Geo("M -0.9,-0.55 C -1.7,-0.4 -1.7,0.9 -1.25,1.55 C -1.2,1.1 -1.0,0.4 -0.75,-0.1 Z"));
        dc.DrawGeometry(hair, line, Geo("M 0.9,-0.55 C 1.7,-0.4 1.7,0.9 1.25,1.55 C 1.2,1.1 1.0,0.4 0.75,-0.1 Z"));
        dc.DrawEllipse(Brush(style.Ribbon), Pen(Line, 0.04), new Point(-0.92, -0.5), 0.12, 0.12);
        dc.DrawEllipse(Brush(style.Ribbon), Pen(Line, 0.04), new Point(0.92, -0.5), 0.12, 0.12);
    }

    private static void DrawLegs(DrawingContext dc, ChibiPose pose, Pen line)
    {
        foreach (var side in new[] { -1, 1 })
        {
            var hip = new Point(side * 0.22, 1.85);
            var angle = pose.Sitting ? pose.Legs * side : pose.Legs * side;
            dc.PushTransform(new RotateTransform(angle, hip.X, hip.Y));
            dc.DrawRoundedRectangle(Brush(Skin), line, new Rect(hip.X - 0.11, hip.Y, 0.22, 0.42), 0.1, 0.1);
            dc.DrawRoundedRectangle(Brush(Colors.White), line, new Rect(hip.X - 0.12, hip.Y + 0.28, 0.24, 0.22), 0.06, 0.06);
            dc.DrawEllipse(Brush(C(120, 60, 70)), line, new Point(hip.X + 0.04, hip.Y + 0.55), 0.17, 0.09);
            dc.Pop();
        }
    }

    private static void DrawBody(DrawingContext dc, ChibiStyle style, Pen line)
    {
        // Pleated skirt, sailor blouse, collar and bow.
        dc.DrawGeometry(Brush(Navy), line, Geo("M -0.52,1.45 L 0.52,1.45 L 0.74,1.95 L -0.74,1.95 Z"));
        var pleat = Pen(Color.FromArgb(120, 255, 255, 255), 0.03);
        foreach (var x in new[] { -0.4, -0.13, 0.13, 0.4 }) dc.DrawLine(pleat, new Point(x * 0.8, 1.5), new Point(x, 1.93));
        dc.DrawGeometry(Brush(Colors.White), line, Geo("M -0.42,0.86 L 0.42,0.86 L 0.55,1.5 L -0.55,1.5 Z"));
        dc.DrawGeometry(Brush(Navy), line, Geo("M -0.45,0.86 L 0.45,0.86 L 0.5,1.0 L 0,1.22 L -0.5,1.0 Z"));
        dc.DrawLine(Pen(Colors.White, 0.03), new Point(-0.44, 0.94), new Point(0, 1.14));
        dc.DrawLine(Pen(Colors.White, 0.03), new Point(0.44, 0.94), new Point(0, 1.14));
        var bow = Brush(style.Ribbon);
        dc.DrawGeometry(bow, Pen(Line, 0.035), Geo("M 0,1.18 L -0.24,1.06 L -0.2,1.34 Z M 0,1.18 L 0.24,1.06 L 0.2,1.34 Z"));
        dc.DrawEllipse(bow, Pen(Line, 0.035), new Point(0, 1.19), 0.06, 0.06);
    }

    private static void DrawArms(DrawingContext dc, ChibiPose pose, Pen line)
    {
        // Left arm hangs down, right arm waves.
        DrawArm(dc, new Point(-0.46, 0.98), 18, line);
        if (pose.WaveArm <= 0) DrawArm(dc, new Point(0.46, 0.98), -18, line);
    }

    private static void DrawArm(DrawingContext dc, Point shoulder, double angle, Pen line)
    {
        dc.PushTransform(new RotateTransform(angle, shoulder.X, shoulder.Y));
        var side = shoulder.X < 0 ? -1 : 1;
        dc.DrawRoundedRectangle(Brush(Colors.White), line, new Rect(shoulder.X - 0.11 + side * 0.04, shoulder.Y, 0.22, 0.42), 0.1, 0.1);
        dc.DrawEllipse(Brush(Skin), line, new Point(shoulder.X + side * 0.04, shoulder.Y + 0.5), 0.11, 0.11);
        dc.Pop();
    }

    private static void DrawHairBack(DrawingContext dc, ChibiStyle style, Pen line)
    {
        var hair = Brush(style.Hair);
        dc.DrawGeometry(hair, line, Geo("M -1.12,0.2 C -1.25,-1.2 1.25,-1.2 1.12,0.2 C 1.15,0.6 1.1,0.85 0.95,1.0 L 0.7,0.55 L -0.7,0.55 L -0.95,1.0 C -1.1,0.85 -1.15,0.6 -1.12,0.2 Z"));
    }

    private static void DrawEars(DrawingContext dc, ChibiStyle style, ChibiPose pose, Pen line)
    {
        foreach (var side in new[] { -1, 1 })
        {
            var twitch = side == 1 ? pose.EarTwitch : 0;
            dc.PushTransform(new RotateTransform(twitch, side * 0.62, -0.75));
            dc.DrawGeometry(Brush(style.Hair), line,
                Geo(string.Format(CultureInfo.InvariantCulture, "M {0},-0.62 Q {1},-1.55 {2},-1.45 Q {3},-1.1 {4},-0.86 Z", side * 0.95, side * 0.95, side * 0.78, side * 0.5, side * 0.35)));
            dc.DrawGeometry(Brush(C(255, 196, 216)), null,
                Geo(string.Format(CultureInfo.InvariantCulture, "M {0},-0.72 Q {1},-1.32 {2},-1.26 Q {3},-1.05 {4},-0.86 Z", side * 0.86, side * 0.86, side * 0.76, side * 0.58, side * 0.48)));
            dc.Pop();
        }
    }

    private static void DrawBangs(DrawingContext dc, ChibiStyle style, Pen line)
    {
        var bangs = Geo("M -1.02,-0.05 C -1.1,-1.05 1.1,-1.05 1.02,-0.05 L 0.84,-0.42 L 0.64,-0.2 L 0.45,-0.46 L 0.22,-0.24 L 0.04,-0.48 L -0.16,-0.25 L -0.36,-0.46 L -0.56,-0.2 L -0.78,-0.42 Z");
        dc.DrawGeometry(Brush(style.Hair), line, bangs);
        dc.DrawGeometry(null, Pen(Color.FromArgb(110, 255, 255, 255), 0.08, round: true), Geo("M -0.55,-0.7 Q -0.1,-0.88 0.35,-0.74"));
        dc.DrawGeometry(null, Pen(style.HairShade, 0.04), Geo("M 0.04,-0.48 L 0.12,-0.8 M -0.36,-0.46 L -0.32,-0.76"));
    }

    private static void DrawFace(DrawingContext dc, ChibiStyle style, ChibiPose pose, Pen line)
    {
        foreach (var side in new[] { -1, 1 })
        {
            var c = new Point(side * 0.4, 0.2);
            if (pose.Happy)
            {
                dc.DrawGeometry(null, Pen(Line, 0.07, round: true),
                    Geo(string.Format(CultureInfo.InvariantCulture, "M {0},{1} Q {2},{3} {4},{1}", c.X - 0.2, c.Y + 0.06, c.X, c.Y - 0.2, c.X + 0.2)));
                continue;
            }

            var open = 1 - Math.Clamp(pose.Blink, 0, 1);
            if (open < 0.15)
            {
                dc.DrawGeometry(null, Pen(Line, 0.07, round: true),
                    Geo(string.Format(CultureInfo.InvariantCulture, "M {0},{1} Q {2},{3} {4},{1}", c.X - 0.2, c.Y + 0.02, c.X, c.Y + 0.14, c.X + 0.2)));
                continue;
            }

            dc.PushTransform(new ScaleTransform(1, open, c.X, c.Y));
            var iris = new LinearGradientBrush(Shade(style.Eye, -0.45), style.EyeLight, 90);
            dc.DrawEllipse(Brush(Colors.White), null, c, 0.27, 0.33);
            dc.DrawEllipse(iris, Pen(Line, 0.04), c, 0.24, 0.31);
            dc.DrawEllipse(Brush(Shade(style.Eye, -0.6)), null, new Point(c.X, c.Y + 0.02), 0.1, 0.14);
            dc.DrawEllipse(Brush(Colors.White), null, new Point(c.X - 0.08 * side, c.Y - 0.11), 0.09, 0.09);
            dc.DrawEllipse(Brush(Colors.White), null, new Point(c.X + 0.07 * side, c.Y + 0.12), 0.04, 0.04);
            dc.Pop();
            // Soft raised brows and an upper lash with a little flick at the outer corner.
            dc.DrawGeometry(null, Pen(style.HairShade, 0.05, round: true),
                Geo(string.Format(CultureInfo.InvariantCulture, "M {0},-0.33 Q {1},-0.39 {2},-0.27", c.X - 0.16 * side, c.X, c.X + 0.16 * side)));
            dc.DrawGeometry(null, Pen(Line, 0.06, round: true),
                Geo(string.Format(CultureInfo.InvariantCulture, "M {0},{1} Q {2},{3} {4},{5} L {6},{7}",
                    c.X - 0.25 * side, c.Y - 0.25 * open, c.X, c.Y - 0.4 * open, c.X + 0.26 * side, c.Y - 0.16 * open, c.X + 0.32 * side, c.Y - 0.1 * open)));
        }

        dc.DrawEllipse(Brush(Blush), null, new Point(-0.62, 0.52), 0.17, 0.09);
        dc.DrawEllipse(Brush(Blush), null, new Point(0.62, 0.52), 0.17, 0.09);

        if (pose.Talking)
            dc.DrawGeometry(Brush(C(200, 70, 100)), Pen(Line, 0.04), Geo("M -0.1,0.56 Q 0,0.72 0.1,0.56 Z"));
        else
            dc.DrawGeometry(null, Pen(Line, 0.045, round: true), Geo("M -0.14,0.56 Q -0.07,0.64 0,0.56 Q 0.07,0.64 0.14,0.56"));
    }

    /// <summary>White speech bubble with a small tail pointing down to the speaker.</summary>
    public static void Bubble(DrawingContext dc, Point tip, string text, double pop, double pixelsPerDip)
    {
        if (pop <= 0) return;
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("de-AT"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 13, Brush(Line), pixelsPerDip);
        var w = formatted.Width + 22;
        var h = formatted.Height + 12;
        var box = new Rect(tip.X - w / 2, tip.Y - h - 10, w, h);
        dc.PushTransform(new ScaleTransform(pop, pop, tip.X, tip.Y));
        dc.DrawRoundedRectangle(Brush(Color.FromArgb(245, 255, 255, 255)), Pen(C(255, 150, 200), 2), box, h / 2, h / 2);
        dc.DrawGeometry(Brush(Color.FromArgb(245, 255, 255, 255)), null, Geo(string.Format(CultureInfo.InvariantCulture,
            "M {0},{1} L {2},{3} L {4},{1} Z", tip.X - 7, box.Bottom - 1, tip.X, tip.Y, tip.X + 7)));
        dc.DrawText(formatted, new Point(box.X + 11, box.Y + 6));
        dc.Pop();
    }

    private static readonly Dictionary<string, Geometry> GeometryCache = new();

    private static Geometry Geo(string data)
    {
        if (GeometryCache.TryGetValue(data, out var cached)) return cached;
        var g = Geometry.Parse(data);
        g.Freeze();
        GeometryCache[data] = g;
        return g;
    }

    private static readonly Dictionary<Color, Brush> BrushCache = new();

    private static Brush Brush(Color color)
    {
        if (BrushCache.TryGetValue(color, out var cached)) return cached;
        var b = new SolidColorBrush(color);
        b.Freeze();
        BrushCache[color] = b;
        return b;
    }

    private static readonly Dictionary<(Color, double, bool), Pen> PenCache = new();

    private static Pen Pen(Color color, double thickness, bool round = false)
    {
        if (PenCache.TryGetValue((color, thickness, round), out var cached)) return cached;
        var pen = new Pen(Brush(color), thickness) { LineJoin = PenLineJoin.Round };
        if (round) pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
        pen.Freeze();
        PenCache[(color, thickness, round)] = pen;
        return pen;
    }

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color Shade(Color c, double amount)
    {
        byte F(byte v) => (byte)Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromRgb(F(c.R), F(c.G), F(c.B));
    }
}

/// <summary>
/// Anime theme overlay with three chibi catgirls: Sakura sits on the clock card swinging her legs,
/// Mochi now and then walks along the bottom and waves, Yuki peeks in from the side. They blink,
/// twitch their ears, swish their tails and say little things in speech bubbles.
/// </summary>
internal sealed class AnimeOverlay : AnimatedLayer
{
    private static readonly string[] Phrases = { "nya~ ♡", "Ganbatte! ✧", "Du schaffst das!", "Kawaii~", "Trink was! ☕", "Fast Pause! ♪", "(=^･ω･^=)" };

    private sealed class Heart { public double X, Y, Vy, Age, Size; }

    private readonly List<Heart> _hearts = new();
    private double _time;

    // Sakura (sitting)
    private double _sitBlinkAt = 2, _sitWaveUntil = -1, _sitNextWave = 6, _sitTwitchUntil = -1, _sitNextTwitch = 3;
    private string _sitText = "";

    // Mochi (walking)
    private double _walkStart = 12, _walkX, _walkDir = 1, _walkPauseUntil = -1;
    private bool _walking, _walkPaused;
    private string _walkText = "";

    // Yuki (peeking)
    private double _peekStart = 24;
    private string _peekText = "";
    private double _peekBlinkAt = 1;

    public AnimeOverlay() : base(30) { }

    /// <summary>Card the sitting chibi sits on (in this layer's coordinates), or null to hide her.</summary>
    public Func<Rect?>? SeatAnchor { get; set; }

    /// <summary>Optional remark such as "Noch 12 min ♡" that the chibis sometimes say.</summary>
    public Func<string?>? Remark { get; set; }

    private string Say()
    {
        var remark = Remark?.Invoke();
        return remark is not null && Random.NextDouble() < 0.4 ? remark : Phrases[Random.Next(Phrases.Length)];
    }

    protected override void Step(double dt)
    {
        _time += dt;
        var w = ActualWidth;

        if (_time > _sitNextWave)
        {
            _sitWaveUntil = _time + 2.4;
            _sitNextWave = _time + 10 + Random.NextDouble() * 10;
            _sitText = Say();
            for (var i = 0; i < 4; i++) _hearts.Add(new Heart { X = 0, Y = 0, Vy = -(30 + Random.NextDouble() * 30), Size = 6 + Random.NextDouble() * 6, Age = -i * 0.2 });
        }
        if (_time > _sitNextTwitch)
        {
            _sitTwitchUntil = _time + 0.25;
            _sitNextTwitch = _time + 2 + Random.NextDouble() * 5;
        }
        if (_time > _sitBlinkAt + 0.16) _sitBlinkAt = _time + 2.5 + Random.NextDouble() * 3;

        // Walker: starts every 25-40 s, crosses the window, pauses in the middle to wave.
        if (!_walking && _time > _walkStart)
        {
            _walking = true;
            _walkDir = Random.Next(2) == 0 ? 1 : -1;
            _walkX = _walkDir > 0 ? -60 : w + 60;
            _walkPaused = false;
        }
        if (_walking)
        {
            if (_walkPaused)
            {
                if (_time > _walkPauseUntil) { _walkPaused = false; _walkText = ""; }
            }
            else
            {
                var before = _walkX;
                _walkX += _walkDir * 70 * dt;
                var middle = w * (0.35 + 0.3 * ((int)_walkStart % 2));
                if ((before - middle) * (_walkX - middle) <= 0 && _walkPauseUntil < _walkStart)
                {
                    _walkPaused = true;
                    _walkPauseUntil = _time + 2.8;
                    _walkText = Say();
                }
                if (_walkX < -80 || _walkX > w + 80)
                {
                    _walking = false;
                    _walkStart = _time + 25 + Random.NextDouble() * 15;
                }
            }
        }

        // Peeker: every 35-50 s slides in from the right for about 4 seconds.
        var peek = _time - _peekStart;
        if (peek > 0 && _peekText.Length == 0) _peekText = Say();
        if (peek > 4.5)
        {
            _peekStart = _time + 35 + Random.NextDouble() * 15;
            _peekText = "";
        }
        if (_time > _peekBlinkAt + 0.16) _peekBlinkAt = _time + 1.5 + Random.NextDouble() * 2;

        foreach (var h in _hearts)
        {
            h.Age += dt;
            if (h.Age > 0) h.Y += h.Vy * dt;
        }
        _hearts.RemoveAll(h => h.Age > 2.2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        PushShellClip(dc);

        // Sakura sits on the top edge of the clock card with her legs dangling in front of it.
        if (SeatAnchor?.Invoke() is { } card && card.Width > 200)
        {
            const double s = 22;
            var seat = new Point(card.X + card.Width * 0.5, card.Top + 2);
            var face = new Point(seat.X, seat.Y - 1.85 * s);
            var waving = _time < _sitWaveUntil;
            var pose = new ChibiPose
            {
                Sitting = true,
                Legs = Math.Sin(_time * 2.2) * 22,
                Tail = Math.Sin(_time * 1.6) * 14,
                Blink = BlinkAmount(_time, _sitBlinkAt),
                Happy = waving,
                Talking = waving && ((int)(_time * 6) & 1) == 0,
                WaveArm = waving ? Math.Min(1, (_sitWaveUntil - _time) * 3) : 0,
                WavePhase = _time * 12,
                EarTwitch = _time < _sitTwitchUntil ? -18 : 0,
                Bob = Math.Sin(_time * 1.6) * 0.05
            };
            ChibiPainter.Draw(dc, face, s, ChibiPainter.Styles[0], pose);

            foreach (var heart in _hearts.Where(x => x.Age > 0))
            {
                var hx = face.X + 30 + Math.Sin(heart.Age * 3 + heart.Size) * 8;
                var hy = face.Y - 20 + heart.Y;
                var alpha = Math.Clamp(1 - heart.Age / 2.2, 0, 1);
                dc.PushTransform(new TranslateTransform(hx, hy));
                dc.PushTransform(new ScaleTransform(heart.Size, heart.Size));
                dc.DrawGeometry(Fill(Color.FromRgb(255, 95, 168), alpha), null, HeartShape);
                dc.Pop();
                dc.Pop();
            }

            if (waving)
                ChibiPainter.Bubble(dc, new Point(face.X - 2.2 * s, face.Y - 1.2 * s), _sitText, Pop(_time - (_sitWaveUntil - 2.4)), dpi);
        }

        // Mochi walks along the bottom edge.
        if (_walking)
        {
            const double s = 22;
            var face = new Point(_walkX, h - 2.5 * s - 6);
            var pose = new ChibiPose
            {
                Legs = _walkPaused ? 0 : Math.Sin(_time * 9) * 24,
                Bob = _walkPaused ? 0 : -Math.Abs(Math.Sin(_time * 9)) * 0.12,
                Tail = Math.Sin(_time * 3) * 18,
                Blink = BlinkAmount(_time, _sitBlinkAt + 1.3),
                Happy = _walkPaused,
                Talking = _walkPaused && ((int)(_time * 6) & 1) == 0,
                WaveArm = _walkPaused ? 1 : 0,
                WavePhase = _time * 12
            };
            ChibiPainter.Draw(dc, face, s, ChibiPainter.Styles[2], pose, _walkDir);
            if (_walkPaused)
                ChibiPainter.Bubble(dc, new Point(face.X, face.Y - 1.6 * s), _walkText, Pop(_time - (_walkPauseUntil - 2.8)), dpi);
        }

        // Yuki peeks in from the right edge.
        var peek = _time - _peekStart;
        if (peek is > 0 and < 4.5)
        {
            const double s = 28;
            var slide = peek < 0.6 ? Smooth(peek / 0.6) : peek > 3.9 ? 1 - Smooth((peek - 3.9) / 0.6) : 1;
            var face = new Point(w + 10 - slide * 1.4 * s, h * 0.55);
            var pose = new ChibiPose { Blink = BlinkAmount(_time, _peekBlinkAt), EarTwitch = Math.Sin(_time * 7) * 8, Talking = peek is > 0.8 and < 3.5 && ((int)(_time * 6) & 1) == 0 };
            ChibiPainter.Draw(dc, face, s, ChibiPainter.Styles[1], pose, -1, headOnly: true, tilt: -18);
            if (peek is > 0.8 and < 3.7)
                ChibiPainter.Bubble(dc, new Point(face.X - 1.6 * s, face.Y - 1.5 * s), _peekText, Pop(peek - 0.8), dpi);
        }

        dc.Pop();
    }

    private static double BlinkAmount(double time, double blinkAt)
    {
        var t = time - blinkAt;
        return t is < 0 or > 0.16 ? 0 : 1 - Math.Abs(t - 0.08) / 0.08;
    }

    private static double Pop(double t) => t < 0 ? 0 : t < 0.25 ? 1.15 * Smooth(t / 0.25) : t < 0.35 ? 1.15 - 0.15 * (t - 0.25) / 0.1 : 1;

    private static double Smooth(double x) => Math.Clamp(x, 0, 1) is var c ? c * c * (3 - 2 * c) : 0;

    private static readonly Geometry HeartShape = CreateHeart();

    private static Geometry CreateHeart()
    {
        var g = Geometry.Parse("M 0,0.9 C -1.4,0 -1.1,-1 -0.5,-1 C -0.2,-1 0,-0.8 0,-0.6 C 0,-0.8 0.2,-1 0.5,-1 C 1.1,-1 1.4,0 0,0.9 Z");
        g.Freeze();
        return g;
    }
}
