using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ViennaAtomicClock;

/// <summary>Pixel textures and shared sizes for the Minecraft theme (all hand-made, no game assets).</summary>
internal static class Voxel
{
    public const double Block = 32;

    // Colours first: static fields are initialised in order and the textures need them.
    public static readonly Color[] DirtColors = { Color.FromRgb(134, 96, 67), Color.FromRgb(121, 85, 58), Color.FromRgb(150, 108, 74), Color.FromRgb(104, 74, 50) };
    public static readonly Color[] GrassColors = { Color.FromRgb(106, 170, 64), Color.FromRgb(93, 153, 54), Color.FromRgb(121, 186, 74), Color.FromRgb(84, 140, 48) };

    public static readonly BitmapSource Grass = Texture(1, grass: true);
    public static readonly BitmapSource Dirt = Texture(2, grass: false);
    public static readonly BitmapSource Stone = StoneTexture();

    private static BitmapSource Texture(int seed, bool grass)
    {
        var random = new Random(seed);
        var pixels = new byte[16 * 16 * 4];
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                // Grass hangs a little unevenly over the dirt.
                var grassDepth = 3 + ((x * 7 + seed) % 3 == 0 ? 1 : 0);
                var c = grass && y < grassDepth ? GrassColors[random.Next(4)] : DirtColors[random.Next(4)];
                var i = (y * 16 + x) * 4;
                pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = 255;
            }
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 64);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource StoneTexture()
    {
        var random = new Random(3);
        var pixels = new byte[16 * 16 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var v = (byte)(112 + random.Next(-18, 19));
            pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v; pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 64);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>
/// Minecraft theme backdrop: sky with a square sun (or moon and stars at night), drifting blocky clouds,
/// a strip of grass, dirt and stone at the bottom where now and then a block cracks, breaks and grows back.
/// </summary>
internal sealed class MinecraftBackdrop : AnimatedLayer
{
    private sealed class Particle { public double X, Y, Vx, Vy, Age; public Color Color; }

    private readonly List<(double X, double Y, int W, int H, double Speed)> _clouds = new();
    private readonly List<Point> _stars = new();
    private readonly List<Particle> _particles = new();
    private double _time;
    private int _mineIndex = -1;
    private double _mineStart, _nextMine = 4, _missingUntil = -1;

    public MinecraftBackdrop() : base(20)
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    // Only the top of the ground strip shows below the cards; the animals walk on it.
    private double GroundTop => ActualHeight - 22;

    protected override void Step(double dt)
    {
        _time += dt;
        var w = ActualWidth;
        if (_clouds.Count == 0)
        {
            for (var i = 0; i < 6; i++)
                _clouds.Add((Random.NextDouble() * w, 8 + Random.NextDouble() * 50, 3 + Random.Next(5), 1 + Random.Next(2), 5 + Random.NextDouble() * 9));
            for (var i = 0; i < 50; i++)
                _stars.Add(new Point(Random.NextDouble(), Random.NextDouble() * 0.7));
        }
        for (var i = 0; i < _clouds.Count; i++)
        {
            var c = _clouds[i];
            var x = c.X + c.Speed * dt;
            if (x > w + 200) x = -c.W * 16 - 50;
            _clouds[i] = (x, c.Y, c.W, c.H, c.Speed);
        }

        // Mining: crack for 1.4 s, break into particles, grow back after a few seconds.
        var blocks = (int)(w / Voxel.Block) + 1;
        if (_mineIndex < 0 && _time > _nextMine)
        {
            _mineIndex = Random.Next(blocks);
            _mineStart = _time;
        }
        if (_mineIndex >= 0 && _missingUntil < 0 && _time - _mineStart > 1.4)
        {
            _missingUntil = _time + 4;
            var cx = _mineIndex * Voxel.Block + Voxel.Block / 2;
            for (var i = 0; i < 16; i++)
                _particles.Add(new Particle
                {
                    X = cx + (Random.NextDouble() - 0.5) * Voxel.Block, Y = GroundTop + Random.NextDouble() * Voxel.Block * 0.5,
                    Vx = (Random.NextDouble() - 0.5) * 140, Vy = -60 - Random.NextDouble() * 120,
                    Color = Random.NextDouble() < 0.35 ? Voxel.GrassColors[Random.Next(4)] : Voxel.DirtColors[Random.Next(4)]
                });
        }
        if (_missingUntil > 0 && _time > _missingUntil)
        {
            _mineIndex = -1;
            _missingUntil = -1;
            _nextMine = _time + 6 + Random.NextDouble() * 6;
        }

        foreach (var p in _particles)
        {
            p.Age += dt;
            p.Vy += 420 * dt;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
        }
        _particles.RemoveAll(p => p.Age > 1.3);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0) return;
        var dark = App.IsDark;

        PushShellClip(dc);
        var cx = w / 2 + Math.Sin(_time * 0.05) * 60;
        if (dark)
        {
            foreach (var s in _stars)
            {
                var twinkle = 0.4 + 0.6 * Math.Abs(Math.Sin(_time * 1.5 + s.X * 40));
                dc.DrawRectangle(Fill(Colors.White, 0.6 * twinkle), null, new Rect(s.X * w, s.Y * h, 3, 3));
            }
            // Moon in the empty middle of the title bar.
            dc.DrawRectangle(Fill(Color.FromRgb(230, 230, 210), 0.9), null, new Rect(cx - 24, 12, 48, 48));
            dc.DrawRectangle(Fill(Color.FromRgb(190, 190, 170), 0.9), null, new Rect(cx - 12, 24, 12, 12));
            dc.DrawRectangle(Fill(Color.FromRgb(190, 190, 170), 0.9), null, new Rect(cx + 6, 42, 8, 8));
        }
        else
        {
            dc.DrawRectangle(Fill(Color.FromRgb(255, 240, 120), 0.95), null, new Rect(cx - 28, 8, 56, 56));
            dc.DrawRectangle(Fill(Color.FromRgb(255, 255, 200), 0.95), null, new Rect(cx - 18, 18, 36, 36));
        }

        foreach (var (x, y, cw, ch, _) in _clouds)
        {
            const double px = 16;
            dc.DrawRectangle(Fill(Colors.White, dark ? 0.12 : 0.6), null, new Rect(x, y, cw * px, ch * px));
            dc.DrawRectangle(Fill(Colors.White, dark ? 0.12 : 0.6), null, new Rect(x + px, y - px / 2, Math.Max(1, cw - 2) * px, px / 2));
        }

        var top = GroundTop;
        for (var i = 0; i * Voxel.Block < w; i++)
        {
            var x = i * Voxel.Block;
            var missing = i == _mineIndex && _missingUntil > 0;
            if (!missing) dc.DrawImage(Voxel.Grass, new Rect(x, top, Voxel.Block, Voxel.Block));
            else dc.DrawImage(Voxel.Stone, new Rect(x, top + Voxel.Block * 0.6, Voxel.Block, Voxel.Block));
        }

        // Cracks on the block that is being mined.
        if (_mineIndex >= 0 && _missingUntil < 0)
        {
            var stage = (int)Math.Clamp((_time - _mineStart) / 1.4 * 5, 0, 4);
            var x0 = _mineIndex * Voxel.Block;
            var crack = Fill(Colors.Black, 0.55);
            var u = Voxel.Block / 16;
            int[][] lines = { new[] { 7, 7, 3 }, new[] { 4, 9, 5 }, new[] { 10, 3, 4 }, new[] { 2, 12, 6 }, new[] { 12, 11, 4 } };
            for (var k = 0; k <= stage; k++)
            {
                var (lx, ly, len) = (lines[k][0], lines[k][1], lines[k][2]);
                dc.DrawRectangle(crack, null, new Rect(x0 + lx * u, top + ly * u, len * u, u));
                dc.DrawRectangle(crack, null, new Rect(x0 + (lx + len / 2) * u, top + (ly - 2) * u, u, 3 * u));
            }
        }

        foreach (var p in _particles)
            dc.DrawRectangle(Fill(p.Color, Math.Clamp(1.3 - p.Age, 0, 1)), null, new Rect(p.X, p.Y, 5, 5));

        if (dark) dc.DrawRectangle(Fill(Colors.Black, 0.4), null, new Rect(0, top, w, Voxel.Block));
        dc.Pop();
    }
}

/// <summary>
/// Minecraft theme foreground: blocky sheep, pigs and chickens wander along the grass at the bottom,
/// graze, hop and turn around; flowers sway, and at night torches flicker.
/// </summary>
internal sealed class MinecraftOverlay : AnimatedLayer
{
    private enum Kind { Sheep, Pig, Chicken }

    private sealed class Animal
    {
        public Kind Kind;
        public double X, Dir = 1, Speed, StateUntil, Hop;
        public bool Idle, Grazing;
        public Color Wool;
    }

    private readonly List<Animal> _animals = new();
    private readonly List<(double X, int Kind)> _flowers = new();
    private double _time;

    public MinecraftOverlay() : base(20)
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    private double Ground => ActualHeight - 22;

    protected override void Step(double dt)
    {
        _time += dt;
        var w = ActualWidth;
        if (_animals.Count == 0)
        {
            Kind[] kinds = { Kind.Sheep, Kind.Pig, Kind.Chicken, Kind.Sheep, Kind.Chicken };
            Color[] wool = { Colors.White, Color.FromRgb(240, 175, 200), Color.FromRgb(90, 90, 90) };
            foreach (var kind in kinds)
                _animals.Add(new Animal
                {
                    Kind = kind, X = Random.NextDouble() * w, Dir = Random.Next(2) == 0 ? -1 : 1,
                    Speed = kind == Kind.Chicken ? 26 : 16 + Random.NextDouble() * 8, Wool = wool[Random.Next(wool.Length)]
                });
            for (var x = 60.0; x < w; x += 90 + Random.NextDouble() * 140)
                _flowers.Add((x, Random.Next(3)));
        }

        foreach (var a in _animals)
        {
            if (_time > a.StateUntil)
            {
                a.Idle = Random.NextDouble() < 0.4;
                a.Grazing = a.Idle && a.Kind != Kind.Chicken && Random.NextDouble() < 0.7;
                if (!a.Idle && Random.NextDouble() < 0.3) a.Dir = -a.Dir;
                a.StateUntil = _time + 2 + Random.NextDouble() * 5;
            }
            if (!a.Idle)
            {
                a.X += a.Dir * a.Speed * dt;
                if (a.X < 10) { a.X = 10; a.Dir = 1; }
                if (a.X > w - 50) { a.X = w - 50; a.Dir = -1; }
            }
            if (a.Hop <= 0 && Random.NextDouble() < dt * 0.15) a.Hop = 0.001;
            if (a.Hop > 0) a.Hop = a.Hop > 0.5 ? 0 : a.Hop + dt;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        if (w <= 0) return;
        var ground = Ground;
        PushShellClip(dc);

        foreach (var (x, kind) in _flowers)
        {
            var sway = Math.Round(Math.Sin(_time * 2 + x) * 1);
            dc.DrawRectangle(Fill(Color.FromRgb(70, 140, 40), 1), null, new Rect(x + 3, ground - 10, 2, 10));
            var petal = kind switch { 0 => Color.FromRgb(220, 40, 40), 1 => Color.FromRgb(250, 220, 40), _ => Color.FromRgb(80, 120, 230) };
            dc.DrawRectangle(Fill(petal, 1), null, new Rect(x + sway, ground - 16, 8, 6));
            dc.DrawRectangle(Fill(Color.FromRgb(250, 230, 120), 1), null, new Rect(x + 3 + sway, ground - 14, 2, 2));
        }

        if (App.IsDark)
            for (var x = 140.0; x < w; x += 320)
            {
                var flicker = 0.75 + 0.25 * Math.Sin(_time * 13 + x);
                var glow = new RadialGradientBrush(Color.FromArgb((byte)(110 * flicker), 255, 200, 90), Color.FromArgb(0, 255, 200, 90));
                glow.Freeze();
                dc.DrawEllipse(glow, null, new Point(x + 3, ground - 22), 46, 46);
                dc.DrawRectangle(Fill(Color.FromRgb(110, 80, 40), 1), null, new Rect(x, ground - 18, 6, 18));
                dc.DrawRectangle(Fill(Color.FromRgb(255, 210, 80), 1), null, new Rect(x, ground - 24, 6, 6));
                dc.DrawRectangle(Fill(Color.FromRgb(255, 140, 40), flicker), null, new Rect(x + 1, ground - 27, 4, 4));
            }

        foreach (var a in _animals) DrawAnimal(dc, a, ground);
        dc.Pop();
    }

    private void DrawAnimal(DrawingContext dc, Animal a, double ground)
    {
        const double p = 2; // one "pixel" of the voxel art
        var hop = a.Hop > 0 ? Math.Sin(a.Hop / 0.5 * Math.PI) * 10 : 0;
        var step = !a.Idle && ((int)(_time * 6) & 1) == 0;
        dc.PushTransform(new TranslateTransform(Math.Round(a.X), Math.Round(ground - hop)));
        if (a.Dir < 0) dc.PushTransform(new ScaleTransform(-1, 1, 10 * p, 0));

        void Box(double x, double y, double bw, double bh, Color c) => dc.DrawRectangle(Fill(c, 1), null, new Rect(x * p, y * p, bw * p, bh * p));

        switch (a.Kind)
        {
            case Kind.Sheep:
            {
                var legs = Color.FromRgb(200, 170, 150);
                Box(2, -6, 2, 6 - (step ? 1 : 0), legs); Box(6, -6, 2, 6 - (step ? 0 : 1), legs);
                Box(12, -6, 2, 6 - (step ? 0 : 1), legs); Box(16, -6, 2, 6 - (step ? 1 : 0), legs);
                Box(0, -16, 20, 11, a.Wool);
                Box(1, -15, 18, 1, Color.FromArgb(255, (byte)(a.Wool.R * 0.9), (byte)(a.Wool.G * 0.9), (byte)(a.Wool.B * 0.9)));
                var headY = a.Grazing ? -9 + Math.Abs(Math.Sin(_time * 4)) : -19;
                Box(18, headY, 8, 8, Color.FromRgb(205, 175, 150));
                Box(17, headY - 1, 9, 3, a.Wool);
                Box(23, headY + 3, 2, 2, Colors.Black);
                break;
            }
            case Kind.Pig:
            {
                var pink = Color.FromRgb(240, 160, 160);
                Box(2, -5, 3, 5 - (step ? 1 : 0), pink); Box(14, -5, 3, 5 - (step ? 0 : 1), pink);
                Box(0, -14, 19, 10, pink);
                Box(0, -14, 19, 1, Color.FromRgb(250, 185, 185));
                var headY = a.Grazing ? -9 : -16;
                Box(17, headY, 9, 9, pink);
                Box(24, headY + 4, 3, 3, Color.FromRgb(225, 120, 130));
                Box(21, headY + 2, 2, 2, Colors.Black);
                break;
            }
            default:
            {
                var white = Color.FromRgb(245, 245, 245);
                Box(4, -4, 1, 4, Color.FromRgb(240, 170, 40)); Box(7, -4 + (step ? 1 : 0), 1, 4, Color.FromRgb(240, 170, 40));
                Box(1, -11, 10, 7, white);
                Box(0, -10, 2, 4, Color.FromRgb(225, 225, 225));
                Box(8, -16, 5, 7, white);
                Box(13, -14, 2, 2, Color.FromRgb(240, 170, 40));
                Box(12, -12, 2, 2, Color.FromRgb(220, 40, 40));
                Box(10, -15, 1, 1, Colors.Black);
                break;
            }
        }

        if (a.Dir < 0) dc.Pop();
        dc.Pop();
    }
}
