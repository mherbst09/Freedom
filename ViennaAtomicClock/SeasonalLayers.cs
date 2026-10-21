using System.Windows;
using System.Windows.Media;

namespace ViennaAtomicClock;

/// <summary>Christmas theme: gently falling snow, fir silhouettes and a glowing garland.</summary>
internal sealed class ChristmasBackdrop : AnimatedLayer
{
    private sealed class Flake
    {
        public double X, Y, Speed, Size, Drift, Phase;
    }

    private readonly List<Flake> _flakes = new();
    private double _width;
    private double _height;
    private double _time;

    public ChristmasBackdrop() : base(24) { }

    protected override void Step(double dt)
    {
        EnsureScene();
        _time += dt;
        foreach (var flake in _flakes)
        {
            flake.Y += flake.Speed * dt;
            flake.X += Math.Sin(_time * 0.8 + flake.Phase) * flake.Drift * dt;
            if (flake.Y > _height + 12)
            {
                flake.Y = -12;
                flake.X = Random.NextDouble() * Math.Max(1, _width);
            }
            if (flake.X < -12) flake.X = _width + 12;
            else if (flake.X > _width + 12) flake.X = -12;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        EnsureScene();
        var red = ResourceColor("AccentBrush", Color.FromRgb(214, 50, 70));
        var green = ResourceColor("GreenBrush", Color.FromRgb(62, 171, 92));
        var snow = ResourceColor("TextPrimary", Colors.White);
        var gold = ResourceColor("OrangeBrush", Color.FromRgb(248, 198, 76));

        PushShellClip(dc);
        DrawSnowBank(dc, snow);
        DrawPine(dc, 54, ActualHeight - 8, 1.05, green, snow);
        DrawPine(dc, ActualWidth - 42, ActualHeight - 12, 0.82, green, snow);
        DrawGarland(dc, red, green, gold);

        foreach (var flake in _flakes)
        {
            var alpha = 0.22 + Math.Min(0.48, flake.Size / 14);
            dc.DrawEllipse(Fill(snow, alpha), null, new Point(flake.X, flake.Y), flake.Size, flake.Size);
            if (flake.Size < 2.4) continue;
            var pen = new Pen(Fill(snow, alpha * 0.8), Math.Max(0.6, flake.Size * 0.24));
            pen.Freeze();
            dc.DrawLine(pen, new Point(flake.X - flake.Size * 1.7, flake.Y), new Point(flake.X + flake.Size * 1.7, flake.Y));
            dc.DrawLine(pen, new Point(flake.X, flake.Y - flake.Size * 1.7), new Point(flake.X, flake.Y + flake.Size * 1.7));
        }
        dc.Pop();
    }

    private void EnsureScene()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var count = Math.Clamp((int)(ActualWidth * ActualHeight / 15000), 38, 110);
        if (_flakes.Count == count && Math.Abs(_width - ActualWidth) < 80 && Math.Abs(_height - ActualHeight) < 80) return;
        _width = ActualWidth;
        _height = ActualHeight;
        _flakes.Clear();
        for (var i = 0; i < count; i++)
            _flakes.Add(new Flake
            {
                X = Random.NextDouble() * _width,
                Y = Random.NextDouble() * _height,
                Speed = 13 + Random.NextDouble() * 28,
                Size = 0.8 + Random.NextDouble() * 2.8,
                Drift = 3 + Random.NextDouble() * 8,
                Phase = Random.NextDouble() * Math.PI * 2
            });
    }

    private void DrawGarland(DrawingContext dc, Color red, Color green, Color gold)
    {
        var path = new StreamGeometry();
        using (var g = path.Open())
        {
            g.BeginFigure(new Point(-20, 23), false, false);
            g.BezierTo(new Point(ActualWidth * 0.27, 68), new Point(ActualWidth * 0.72, 67), new Point(ActualWidth + 20, 22), true, false);
        }
        path.Freeze();
        var garland = new Pen(Fill(green, 0.52), 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        garland.Freeze();
        dc.DrawGeometry(null, garland, path);

        var lights = Math.Clamp((int)(ActualWidth / 64), 8, 26);
        for (var i = 0; i <= lights; i++)
        {
            var share = i / (double)lights;
            var x = share * ActualWidth;
            var y = 23 + Math.Sin(share * Math.PI) * 34;
            var color = (i % 3) switch { 0 => red, 1 => gold, _ => Color.FromRgb(112, 205, 255) };
            var pulse = 0.72 + Math.Sin(_time * 2.2 + i * 0.9) * 0.18;
            dc.DrawEllipse(Fill(color, 0.12 * pulse), null, new Point(x, y + 8), 10, 10);
            dc.DrawEllipse(Fill(color, 0.78 * pulse), null, new Point(x, y + 8), 3.4, 4.6);
        }
    }

    private void DrawSnowBank(DrawingContext dc, Color snow)
    {
        var bank = new StreamGeometry();
        using (var g = bank.Open())
        {
            g.BeginFigure(new Point(0, ActualHeight - 42), true, true);
            g.BezierTo(new Point(ActualWidth * 0.22, ActualHeight - 76), new Point(ActualWidth * 0.36, ActualHeight - 18), new Point(ActualWidth * 0.57, ActualHeight - 48), true, false);
            g.BezierTo(new Point(ActualWidth * 0.76, ActualHeight - 75), new Point(ActualWidth * 0.9, ActualHeight - 26), new Point(ActualWidth, ActualHeight - 52), true, false);
            g.LineTo(new Point(ActualWidth, ActualHeight), true, false);
            g.LineTo(new Point(0, ActualHeight), true, false);
        }
        bank.Freeze();
        dc.DrawGeometry(Fill(snow, 0.1), null, bank);
    }

    private void DrawPine(DrawingContext dc, double x, double bottom, double scale, Color green, Color snow)
    {
        var trunk = new Rect(x - 5 * scale, bottom - 88 * scale, 10 * scale, 88 * scale);
        dc.DrawRectangle(Fill(Color.FromRgb(98, 64, 43), 0.35), null, trunk);
        for (var i = 0; i < 3; i++)
        {
            var y = bottom - (42 + i * 42) * scale;
            var half = (48 - i * 7) * scale;
            var tree = new StreamGeometry();
            using (var g = tree.Open())
            {
                g.BeginFigure(new Point(x, y - 58 * scale), true, true);
                g.LineTo(new Point(x - half, y + 18 * scale), true, false);
                g.LineTo(new Point(x + half, y + 18 * scale), true, false);
            }
            tree.Freeze();
            dc.DrawGeometry(Fill(green, 0.2 + i * 0.05), null, tree);
            dc.DrawLine(new Pen(Fill(snow, 0.18), 2.2 * scale), new Point(x - half * 0.7, y + 4 * scale), new Point(x + half * 0.72, y + 8 * scale));
        }
    }
}

/// <summary>Halloween theme: moonlit fog, fluttering bats, crooked branches and small pumpkins.</summary>
internal sealed class HalloweenBackdrop : AnimatedLayer
{
    private sealed class Bat
    {
        public double X, Y, Speed, Scale, Phase;
    }

    private readonly List<Bat> _bats = new();
    private double _width;
    private double _height;
    private double _time;

    public HalloweenBackdrop() : base(24) { }

    protected override void Step(double dt)
    {
        EnsureScene();
        _time += dt;
        foreach (var bat in _bats)
        {
            bat.X += bat.Speed * dt;
            if (bat.X > _width + 45)
            {
                bat.X = -45;
                bat.Y = 65 + Random.NextDouble() * Math.Max(80, _height * 0.45);
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        EnsureScene();
        var orange = ResourceColor("AccentBrush", Color.FromRgb(255, 122, 26));
        var cream = ResourceColor("TextPrimary", Color.FromRgb(255, 239, 212));
        var purple = ResourceColor("TextSecondary", Color.FromRgb(182, 143, 210));
        var background = ResourceColor("WindowBackground", Colors.Black);
        var batColor = 0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B < 110
            ? Color.FromRgb(153, 101, 181)
            : Color.FromRgb(70, 35, 83);
        var branch = Color.FromRgb(45, 29, 38);

        PushShellClip(dc);
        var moon = new Point(ActualWidth * 0.82, Math.Max(92, ActualHeight * 0.17));
        dc.DrawEllipse(Fill(orange, 0.06), null, moon, 86, 86);
        dc.DrawEllipse(Fill(cream, 0.18), null, moon, 54, 54);
        dc.DrawEllipse(Fill(background, 0.78), null, new Point(moon.X + 18, moon.Y - 12), 50, 50);

        DrawBranches(dc, branch);
        DrawFog(dc, cream, purple);
        foreach (var bat in _bats) DrawBat(dc, bat, batColor);
        DrawPumpkin(dc, 58, ActualHeight - 34, 0.9, orange, branch);
        DrawPumpkin(dc, ActualWidth - 64, ActualHeight - 31, 0.72, orange, branch);
        dc.Pop();
    }

    private void EnsureScene()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var count = Math.Clamp((int)(ActualWidth / 70), 9, 24);
        if (_bats.Count == count && Math.Abs(_width - ActualWidth) < 80 && Math.Abs(_height - ActualHeight) < 80) return;
        _width = ActualWidth;
        _height = ActualHeight;
        _bats.Clear();
        for (var i = 0; i < count; i++)
            _bats.Add(new Bat
            {
                X = Random.NextDouble() * _width,
                Y = 58 + Random.NextDouble() * Math.Max(80, _height * 0.46),
                Speed = 10 + Random.NextDouble() * 24,
                Scale = 0.55 + Random.NextDouble() * 0.9,
                Phase = Random.NextDouble() * Math.PI * 2
            });
    }

    private void DrawBat(DrawingContext dc, Bat bat, Color color)
    {
        var flap = 4 + Math.Sin(_time * 6 + bat.Phase) * 3.5;
        dc.PushTransform(new TranslateTransform(bat.X, bat.Y + Math.Sin(_time * 1.7 + bat.Phase) * 5));
        dc.PushTransform(new ScaleTransform(bat.Scale, bat.Scale));
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(0, 2), true, true);
            g.LineTo(new Point(-7, -2), true, false);
            g.LineTo(new Point(-16, -flap), true, false);
            g.LineTo(new Point(-13, 4), true, false);
            g.LineTo(new Point(-5, 7), true, false);
            g.LineTo(new Point(0, 4), true, false);
            g.LineTo(new Point(5, 7), true, false);
            g.LineTo(new Point(13, 4), true, false);
            g.LineTo(new Point(16, -flap), true, false);
            g.LineTo(new Point(7, -2), true, false);
        }
        dc.DrawGeometry(Fill(color, 0.34), null, shape);
        dc.Pop();
        dc.Pop();
    }

    private void DrawFog(DrawingContext dc, Color cream, Color purple)
    {
        for (var i = 0; i < 5; i++)
        {
            var direction = i % 2 == 0 ? 1 : -1;
            var x = (i * ActualWidth / 4 + _time * (7 + i * 2) * direction) % (ActualWidth + 420) - 210;
            if (x < -210) x += ActualWidth + 420;
            var y = ActualHeight * (0.58 + i * 0.08);
            dc.DrawEllipse(Fill(i % 2 == 0 ? cream : purple, 0.035), null, new Point(x, y), 230, 38 + i * 4);
            dc.DrawEllipse(Fill(cream, 0.025), null, new Point(x + 145, y + 15), 180, 28);
        }
    }

    private void DrawBranches(DrawingContext dc, Color color)
    {
        var pen = new Pen(Fill(color, 0.44), 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var branches = new StreamGeometry();
        using (var g = branches.Open())
        {
            g.BeginFigure(new Point(-12, ActualHeight * 0.45), false, false);
            g.BezierTo(new Point(25, ActualHeight * 0.32), new Point(45, ActualHeight * 0.24), new Point(86, ActualHeight * 0.2), true, false);
            g.BeginFigure(new Point(45, ActualHeight * 0.29), false, false);
            g.BezierTo(new Point(58, ActualHeight * 0.2), new Point(52, ActualHeight * 0.14), new Point(76, ActualHeight * 0.08), true, false);
            g.BeginFigure(new Point(65, ActualHeight * 0.235), false, false);
            g.BezierTo(new Point(86, ActualHeight * 0.27), new Point(100, ActualHeight * 0.28), new Point(124, ActualHeight * 0.27), true, false);
            g.BeginFigure(new Point(ActualWidth + 10, ActualHeight * 0.7), false, false);
            g.BezierTo(new Point(ActualWidth - 44, ActualHeight * 0.64), new Point(ActualWidth - 58, ActualHeight * 0.52), new Point(ActualWidth - 108, ActualHeight * 0.48), true, false);
        }
        branches.Freeze();
        dc.DrawGeometry(null, pen, branches);
    }

    private void DrawPumpkin(DrawingContext dc, double x, double y, double scale, Color orange, Color dark)
    {
        dc.DrawRectangle(Fill(dark, 0.5), null, new Rect(x - 2 * scale, y - 23 * scale, 4 * scale, 10 * scale));
        dc.DrawEllipse(Fill(orange, 0.26), null, new Point(x, y), 24 * scale, 19 * scale);
        var groove = new Pen(Fill(dark, 0.34), 1.5 * scale);
        groove.Freeze();
        dc.DrawEllipse(null, groove, new Point(x, y), 10 * scale, 18 * scale);
        dc.DrawGeometry(Fill(dark, 0.5), null, Triangle(x - 9 * scale, y - 4 * scale, 6 * scale));
        dc.DrawGeometry(Fill(dark, 0.5), null, Triangle(x + 9 * scale, y - 4 * scale, 6 * scale));
    }

    private static Geometry Triangle(double x, double y, double size)
    {
        var shape = new StreamGeometry();
        using var g = shape.Open();
        g.BeginFigure(new Point(x, y - size / 2), true, true);
        g.LineTo(new Point(x - size / 2, y + size / 2), true, false);
        g.LineTo(new Point(x + size / 2, y + size / 2), true, false);
        return shape;
    }
}

/// <summary>Jungle theme: layered leaves and vines with slowly drifting fireflies.</summary>
internal sealed class JungleBackdrop : AnimatedLayer
{
    private sealed class Firefly
    {
        public double X, Y, BaseX, BaseY, Phase, Speed, Radius;
    }

    private readonly List<Firefly> _fireflies = new();
    private double _width;
    private double _height;
    private double _time;

    public JungleBackdrop() : base(24) { }

    protected override void Step(double dt)
    {
        EnsureScene();
        _time += dt;
        foreach (var firefly in _fireflies)
        {
            firefly.X = firefly.BaseX + Math.Sin(_time * firefly.Speed + firefly.Phase) * 22;
            firefly.Y = firefly.BaseY + Math.Cos(_time * firefly.Speed * 0.72 + firefly.Phase) * 15;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        EnsureScene();
        var leaf = ResourceColor("GreenBrush", Color.FromRgb(47, 194, 105));
        var accent = ResourceColor("AccentBrush", Color.FromRgb(53, 208, 127));
        var deep = ResourceColor("WindowBackground", Color.FromRgb(5, 30, 19));
        var gold = ResourceColor("OrangeBrush", Color.FromRgb(241, 184, 70));

        PushShellClip(dc);
        DrawVines(dc, leaf, deep);
        DrawCanopy(dc, leaf, accent);
        foreach (var firefly in _fireflies)
        {
            var pulse = 0.58 + Math.Sin(_time * 2.3 + firefly.Phase) * 0.28;
            dc.DrawEllipse(Fill(gold, 0.08 * pulse), null, new Point(firefly.X, firefly.Y), firefly.Radius * 4.5, firefly.Radius * 4.5);
            dc.DrawEllipse(Fill(gold, 0.68 * pulse), null, new Point(firefly.X, firefly.Y), firefly.Radius, firefly.Radius);
        }
        dc.Pop();
    }

    private void EnsureScene()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var count = Math.Clamp((int)(ActualWidth * ActualHeight / 36000), 16, 48);
        if (_fireflies.Count == count && Math.Abs(_width - ActualWidth) < 80 && Math.Abs(_height - ActualHeight) < 80) return;
        _width = ActualWidth;
        _height = ActualHeight;
        _fireflies.Clear();
        for (var i = 0; i < count; i++)
        {
            var x = 32 + Random.NextDouble() * Math.Max(1, _width - 64);
            var y = 72 + Random.NextDouble() * Math.Max(1, _height - 130);
            _fireflies.Add(new Firefly
            {
                X = x, BaseX = x, Y = y, BaseY = y,
                Phase = Random.NextDouble() * Math.PI * 2,
                Speed = 0.35 + Random.NextDouble() * 0.75,
                Radius = 1.2 + Random.NextDouble() * 1.5
            });
        }
    }

    private void DrawVines(DrawingContext dc, Color leaf, Color deep)
    {
        var vinePen = new Pen(Fill(leaf, 0.23), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        vinePen.Freeze();
        for (var i = 0; i < 6; i++)
        {
            var x = 32 + i * Math.Max(80, ActualWidth / 5.5);
            var length = 105 + (i % 3) * 42;
            var path = new StreamGeometry();
            using (var g = path.Open())
            {
                g.BeginFigure(new Point(x, -10), false, false);
                g.BezierTo(new Point(x - 30, length * 0.34), new Point(x + 34, length * 0.63), new Point(x + Math.Sin(_time * 0.45 + i) * 10, length), true, false);
            }
            path.Freeze();
            dc.DrawGeometry(null, vinePen, path);
            for (var j = 1; j <= 3; j++)
                DrawLeaf(dc, x + (j % 2 == 0 ? 12 : -12), length * j / 4, (j % 2 == 0 ? 28 : -28) + Math.Sin(_time * 0.4 + i) * 3, 18, 7, j % 2 == 0 ? leaf : deep, 0.3);
        }
    }

    private void DrawCanopy(DrawingContext dc, Color leaf, Color accent)
    {
        var positions = new[]
        {
            (24d, 24d, 30d, 1.3), (75d, 9d, 58d, 1.0), (132d, 20d, 112d, 1.15),
            (ActualWidth - 26, 22d, -32d, 1.3), (ActualWidth - 82, 7d, -62d, 1.0), (ActualWidth - 140, 25d, -112d, 1.1),
            (18d, ActualHeight - 38, -24d, 1.05), (ActualWidth - 18, ActualHeight - 42, 204d, 1.05)
        };
        for (var i = 0; i < positions.Length; i++)
        {
            var (x, y, angle, scale) = positions[i];
            DrawLeaf(dc, x, y, angle + Math.Sin(_time * 0.35 + i) * 2.5, 50 * scale, 17 * scale, i % 2 == 0 ? leaf : accent, 0.22 + i % 3 * 0.035);
        }
    }

    private void DrawLeaf(DrawingContext dc, double x, double y, double angle, double width, double height, Color color, double alpha)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new RotateTransform(angle));
        dc.DrawEllipse(Fill(color, alpha), null, new Point(0, 0), width / 2, height / 2);
        var vein = new Pen(Fill(ResourceColor("TextPrimary", Colors.White), alpha * 0.45), Math.Max(0.6, height / 18));
        vein.Freeze();
        dc.DrawLine(vein, new Point(-width * 0.38, 0), new Point(width * 0.38, 0));
        dc.Pop();
        dc.Pop();
    }
}
