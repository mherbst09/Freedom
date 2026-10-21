using System.Windows;
using System.Windows.Media;

namespace ViennaAtomicClock.Treeman;

/// <summary>
/// Draws the game from the <see cref="GameManager"/> state. Pure presentation: it never changes the rules, it
/// only reacts to events (chop, round end, reset) with short effects that never delay the input. Everything is
/// drawn in a 1280×720 design space and scaled uniformly, so any window size and aspect ratio work.
/// </summary>
internal sealed class TreemanView : FrameworkElement
{
    public const double DesignWidth = 1280;
    public const double DesignHeight = 720;
    private const double GroundY = 610;
    private const double TrunkWidth = 118;
    private const double SegmentHeight = 82;
    private const double CenterX = DesignWidth / 2;
    private const double PlayerOffset = 150;
    private const double BranchLength = 170;

    private static readonly Brush ShadowBrush = Solid(0, 0, 0, 40);
    private static readonly Brush HitBrush = Solid(0xFF, 0x50, 0x40, 90);
    private static readonly Brush SnowBrush = Solid(0xFF, 0xFF, 0xFF, 220);
    private static readonly Brush BatBrush = Solid(0x1A, 0x14, 0x22);
    private static readonly Pen OutlinePen = MakePen(Color.FromRgb(0x3A, 0x26, 0x18), 3);
    private static readonly Pen ThinOutline = MakePen(Color.FromRgb(0x3A, 0x26, 0x18), 2);
    private static readonly Geometry BatGeometry = Frozen("M 0,0 Q -8,-9 -20,-5 Q -13,-1 -15,6 Q -7,1 0,6 Q 7,1 15,6 Q 13,-1 20,-5 Q 8,-9 0,0 Z");

    private static readonly Geometry BranchGeometry = MakeBranch();
    private static readonly Geometry LeavesGeometry = MakeLeaves();

    private readonly GameManager _game;

    // Effect state (all preallocated – no garbage per chop).
    private const int ChipCount = 64;
    private readonly Chip[] _chips = new Chip[ChipCount];
    private int _nextChip;
    private const int LogCount = 4;
    private readonly FlyingLog[] _logs = new FlyingLog[LogCount];
    private int _nextLog;
    private double _slide;         // 1 → 0: segments slide down after a chop
    private double _treeShake;     // seconds left
    private double _screenShake;   // seconds left
    private double _hitFlash;      // seconds left
    private double _time;
    private readonly Random _random = new(11);
    private readonly ScaleTransform _mirror = new(-1, 1, CenterX, 0);
    private readonly TranslateTransform _shake = new();
    private readonly MatrixTransform _layout = new();
    private readonly RotateTransform _logRotation = new();
    private readonly TranslateTransform _playerPosition = new();
    private readonly TranslateTransform[] _branchPositions = Enumerable.Range(0, 24).Select(_ => new TranslateTransform()).ToArray();
    private readonly TranslateTransform[] _batPositions = Enumerable.Range(0, 5).Select(_ => new TranslateTransform()).ToArray();
    private readonly SkinPainter _painter = new();

    private struct Chip { public double X, Y, Vx, Vy, Life, Size; }
    private struct FlyingLog { public double X, Y, Vx, Vy, Angle, Spin, Life; public Side Branch; }

    public TreemanView(GameManager game)
    {
        _game = game;
        IsHitTestVisible = false;
        game.Chopped += OnChopped;
        game.RoundEnded += _ => { _screenShake = 0.35; _hitFlash = _game.LastReason == GameOverReason.Branch ? 0.45 : 0; };
        game.RoundReset += ClearEffects;
    }

    public bool ReducedMotion { get; set; }

    /// <summary>Current map and time of day.</summary>
    public SceneStyle Scene { get; set; } = SceneStyle.Get(MapTheme.Normal, DayTime.Day);

    /// <summary>Skin of the lumberjack.</summary>
    public SkinDefinition Skin { get; set; } = SkinCatalog.Get(SkinCatalog.DefaultId);

    /// <summary>Falling snow and flying bats need continuous frames.</summary>
    public bool HasAmbientMotion => Scene.Particles != SceneParticles.None && !ReducedMotion;

    /// <summary>True while an effect still moves (the page keeps rendering until everything has settled).</summary>
    public bool IsAnimating
    {
        get
        {
            if (_slide > 0 || _treeShake > 0 || _screenShake > 0 || _hitFlash > 0 || _game.Player.IsSwinging) return true;
            for (var i = 0; i < ChipCount; i++) if (_chips[i].Life > 0) return true;
            for (var i = 0; i < LogCount; i++) if (_logs[i].Life > 0) return true;
            return false;
        }
    }

    private void ClearEffects()
    {
        for (var i = 0; i < ChipCount; i++) _chips[i].Life = 0;
        for (var i = 0; i < LogCount; i++) _logs[i].Life = 0;
        _slide = _treeShake = _screenShake = _hitFlash = 0;
    }

    private void OnChopped(ChopResult chop)
    {
        _slide = 1;
        _treeShake = 0.12;
        var direction = chop.PlayerSide == Side.Left ? 1 : -1; // the log flies away from the axe
        ref var log = ref _logs[_nextLog];
        _nextLog = (_nextLog + 1) % LogCount;
        log = new FlyingLog
        {
            X = CenterX, Y = GroundY - SegmentHeight / 2, Vx = direction * (520 + _random.NextDouble() * 160), Vy = -260,
            Angle = 0, Spin = direction * 540, Life = 0.6, Branch = chop.RemovedBranch
        };
        var hitX = CenterX - direction * TrunkWidth / 2;
        for (var i = 0; i < 7; i++)
        {
            ref var chip = ref _chips[_nextChip];
            _nextChip = (_nextChip + 1) % ChipCount;
            chip = new Chip
            {
                X = hitX, Y = GroundY - SegmentHeight * 0.55 + _random.NextDouble() * 20,
                Vx = -direction * (80 + _random.NextDouble() * 260), Vy = -150 - _random.NextDouble() * 260,
                Life = 0.45 + _random.NextDouble() * 0.25, Size = 4 + _random.NextDouble() * 6
            };
        }
    }

    /// <summary>Moves the effects forward (time based).</summary>
    public void Advance(double seconds)
    {
        _time += seconds;
        _slide = Math.Max(0, _slide - seconds / 0.075);
        _treeShake = Math.Max(0, _treeShake - seconds);
        _screenShake = Math.Max(0, _screenShake - seconds);
        _hitFlash = Math.Max(0, _hitFlash - seconds);
        for (var i = 0; i < ChipCount; i++)
        {
            ref var c = ref _chips[i];
            if (c.Life <= 0) continue;
            c.Life -= seconds;
            c.Vy += 1100 * seconds;
            c.X += c.Vx * seconds;
            c.Y += c.Vy * seconds;
        }
        for (var i = 0; i < LogCount; i++)
        {
            ref var l = ref _logs[i];
            if (l.Life <= 0) continue;
            l.Life -= seconds;
            l.Vy += 1300 * seconds;
            l.X += l.Vx * seconds;
            l.Y += l.Vy * seconds;
            l.Angle += l.Spin * seconds;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Letterbox: sky and ground colours fill any extra space, the game keeps its proportions.
        var scene = Scene;
        dc.DrawRectangle(scene.Sky, null, new Rect(0, 0, width, height));
        var scale = Math.Min(width / DesignWidth, height / DesignHeight);
        var offsetX = (width - DesignWidth * scale) / 2;
        var offsetY = (height - DesignHeight * scale) / 2;
        dc.DrawRectangle(scene.Ground, null, new Rect(0, offsetY + GroundY * scale, width, height - offsetY - GroundY * scale));

        var shakeX = 0.0;
        var shakeY = 0.0;
        if (_screenShake > 0 && !ReducedMotion)
        {
            var strength = _screenShake / 0.35 * 9;
            shakeX = Math.Sin(_time * 90) * strength;
            shakeY = Math.Cos(_time * 77) * strength * 0.6;
        }
        _layout.Matrix = new Matrix(scale, 0, 0, scale, offsetX + shakeX * scale, offsetY + shakeY * scale);
        dc.PushTransform(_layout);

        dc.DrawDrawing(scene.Background);
        DrawTree(dc, scene);
        DrawLogs(dc, scene);
        DrawPlayer(dc);
        DrawChips(dc, scene);
        DrawParticles(dc, scene);
        if (_hitFlash > 0) dc.DrawRectangle(HitBrush, null, new Rect(0, 0, DesignWidth, DesignHeight));
        dc.Pop();
        // Time of day over everything (letterbox included), then what glows.
        if (scene.Tint is not null) dc.DrawRectangle(scene.Tint, null, new Rect(0, 0, width, height));
        if (scene.Lights is not null)
        {
            dc.PushTransform(_layout);
            dc.DrawDrawing(scene.Lights);
            dc.Pop();
        }
    }

    private void DrawTree(DrawingContext dc, SceneStyle scene)
    {
        var tree = _game.Tree;
        var shake = _treeShake > 0 && !ReducedMotion ? Math.Sin(_treeShake * 120) * _treeShake / 0.12 * 5 : 0;
        // After a chop the segments start one step higher and slide down (logic is already done).
        var slideOffset = SegmentHeight * EaseOut(_slide);

        // Stump.
        dc.DrawRoundedRectangle(scene.BarkDark, OutlinePen, new Rect(CenterX - TrunkWidth / 2 - 16, GroundY - 22, TrunkWidth + 32, 30), 8, 8);
        dc.DrawEllipse(ShadowBrush, null, new Point(CenterX, GroundY + 8), 120, 14);

        for (var i = tree.Count - 1; i >= 0; i--)
        {
            var bottom = GroundY - 18 - i * SegmentHeight - slideOffset;
            var top = bottom - SegmentHeight;
            if (bottom < -SegmentHeight) continue;
            var x = CenterX - TrunkWidth / 2 + shake;
            var id = tree.IdAt(i);
            dc.DrawRectangle(id % 2 == 0 ? scene.BarkLight : scene.BarkDark, null, new Rect(x, top, TrunkWidth, SegmentHeight + 1));
            // Bark lines from the segment id, so the pattern travels with the segment.
            var lineX = x + 22 + (id * 37 % 60);
            dc.DrawRectangle(scene.BarkLine, null, new Rect(lineX, top + 14, 5, SegmentHeight - 30));
            dc.DrawRectangle(scene.BarkLine, null, new Rect(x + TrunkWidth - 30 - (id * 23 % 40), top + 26, 4, SegmentHeight - 44));
            dc.DrawLine(ThinOutline, new Point(x, top), new Point(x, bottom));
            dc.DrawLine(ThinOutline, new Point(x + TrunkWidth, top), new Point(x + TrunkWidth, bottom));

            var side = tree[i];
            if (side == Side.None) continue;
            var branchY = top + SegmentHeight * 0.32;
            if (side == Side.Left) dc.PushTransform(_mirror);
            // One transform object per slot: every pushed transform keeps its own position in this frame.
            var position = _branchPositions[i % _branchPositions.Length];
            position.X = CenterX + TrunkWidth / 2 - 6 + (side == Side.Left ? -shake : shake);
            position.Y = branchY;
            dc.PushTransform(position);
            dc.DrawGeometry(scene.Branch, OutlinePen, BranchGeometry);
            dc.DrawGeometry(scene.Leaf, OutlinePen, LeavesGeometry);
            dc.DrawEllipse(scene.LeafLight, null, new Point(BranchLength - 18, -14), 16, 10);
            scene.DrawLeafDecoration(dc, BranchLength, (int)(id % 1000));
            dc.Pop();
            if (side == Side.Left) dc.Pop();
        }
    }

    private void DrawLogs(DrawingContext dc, SceneStyle scene)
    {
        foreach (var log in _logs)
        {
            if (log.Life <= 0) continue;
            dc.PushOpacity(Math.Min(1, log.Life / 0.2));
            _logRotation.Angle = log.Angle;
            _logRotation.CenterX = log.X;
            _logRotation.CenterY = log.Y;
            dc.PushTransform(_logRotation);
            var rect = new Rect(log.X - TrunkWidth / 2, log.Y - SegmentHeight / 2, TrunkWidth, SegmentHeight);
            dc.DrawRectangle(scene.BarkLight, OutlinePen, rect);
            dc.DrawEllipse(scene.Ring, ThinOutline, new Point(rect.Right - 6, rect.Top + SegmentHeight / 2), 8, SegmentHeight / 2 - 6);
            dc.Pop();
            dc.Pop();
        }
    }

    private void DrawChips(DrawingContext dc, SceneStyle scene)
    {
        foreach (var chip in _chips)
        {
            if (chip.Life <= 0) continue;
            dc.DrawRectangle(scene.Chip, null, new Rect(chip.X, chip.Y, chip.Size, chip.Size * 0.6));
        }
    }

    private void DrawPlayer(DrawingContext dc)
    {
        var player = _game.Player;
        var dead = _game.State == GameState.GameOver && _game.LastReason == GameOverReason.Branch;
        // The skin is drawn as the right-hand lumberjack around (0,0) = between his feet; the left one is the mirror image.
        if (player.Side == Side.Left) dc.PushTransform(_mirror);
        _playerPosition.X = CenterX + PlayerOffset;
        _playerPosition.Y = GroundY + 6;
        dc.PushTransform(_playerPosition);
        dc.DrawEllipse(ShadowBrush, null, new Point(0, 2), 46, 9);
        var bob = ReducedMotion || dead ? 0 : Math.Sin(_time * 3) * 1.5;
        // Axe: raised behind the head, then swung into the trunk.
        var angle = player.IsSwinging && !dead ? Lerp(-110, 10, EaseOut(Math.Min(1, player.SwingProgress * 1.6))) : -75;
        _painter.Draw(dc, Skin, bob, angle, dead, _time);
        dc.Pop();
        if (player.Side == Side.Left) dc.Pop();
    }

    /// <summary>Snowfall or bats, computed from the clock (no state, no allocations).</summary>
    private void DrawParticles(DrawingContext dc, SceneStyle scene)
    {
        var t = ReducedMotion ? 0 : _time;
        if (scene.Particles == SceneParticles.Snow)
        {
            for (var i = 0; i < 70; i++)
            {
                var speed = 30 + (i * 37 % 40);
                var x = (i * 173.3 + Math.Sin(t * 0.8 + i) * 18 + t * 12) % (DesignWidth + 20) - 10;
                var y = (i * 97.7 + t * speed) % (GroundY + 30) - 20;
                var r = 1.6 + (i % 4) * 0.7;
                dc.DrawEllipse(SnowBrush, null, new Point(x, y), r, r);
            }
        }
        else if (scene.Particles == SceneParticles.Bats)
        {
            for (var i = 0; i < _batPositions.Length; i++)
            {
                var position = _batPositions[i];
                position.X = (i * 290 + t * (60 + i * 14)) % (DesignWidth + 200) - 100;
                position.Y = 90 + i * 46 + Math.Sin(t * 2.2 + i * 1.7) * 26;
                dc.PushTransform(position);
                dc.DrawGeometry(BatBrush, null, BatGeometry);
                dc.Pop();
            }
        }
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static Geometry MakeBranch()
    {
        var geometry = Geometry.Parse($"M 0,-6 L {BranchLength - 30},-12 Q {BranchLength},-12 {BranchLength},2 Q {BranchLength - 4},14 {BranchLength - 30},10 L 0,16 Z");
        geometry.Freeze();
        return geometry;
    }

    private static Geometry MakeLeaves()
    {
        Geometry leaves = new EllipseGeometry(new Point(BranchLength - 30, -6), 34, 24);
        leaves = new CombinedGeometry(GeometryCombineMode.Union, leaves, new EllipseGeometry(new Point(BranchLength - 2, -16), 26, 20));
        leaves = new CombinedGeometry(GeometryCombineMode.Union, leaves, new EllipseGeometry(new Point(BranchLength - 8, 10), 22, 16));
        var flat = leaves.GetFlattenedPathGeometry();
        flat.Freeze();
        return flat;
    }

    private static Geometry Frozen(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Brush Solid(byte r, byte g, byte b, byte a = 255) => Freeze(new SolidColorBrush(Color.FromArgb(a, r, g, b)));

    private static Pen MakePen(Color color, double thickness) =>
        Freeze(new Pen(new SolidColorBrush(color), thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
}
