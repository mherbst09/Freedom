using System.Windows;
using System.Windows.Media;

namespace ViennaAtomicClock.Treeman;

public enum MapTheme { Normal, Christmas, Halloween, Easter }

public enum DayTime { Day, Sunset, Night }

public enum SceneParticles { None, Snow, Bats }

/// <summary>
/// Look of one map at one time of day: sky, hills, ground, tree colours, decorations and a light tint. All 12
/// combinations are built once (frozen) and reused, so switching costs nothing.
/// </summary>
internal sealed class SceneStyle
{
    public const double GroundY = 610;
    private const double W = TreemanView.DesignWidth;
    private const double H = TreemanView.DesignHeight;

    private static readonly Dictionary<(MapTheme, DayTime), SceneStyle> Cache = new();

    public static SceneStyle Get(MapTheme theme, DayTime time)
    {
        if (!Cache.TryGetValue((theme, time), out var style)) Cache[(theme, time)] = style = new SceneStyle(theme, time);
        return style;
    }

    /// <summary>A random map and time of day, never the same combination as <paramref name="current"/>.</summary>
    public static SceneStyle Shuffle(Random random, SceneStyle? current)
    {
        while (true)
        {
            var next = Get((MapTheme)random.Next(4), (DayTime)random.Next(3));
            if (!ReferenceEquals(next, current)) return next;
        }
    }

    public MapTheme Theme { get; }
    public DayTime Time { get; }
    public Brush Sky { get; }
    public Brush Ground { get; }
    public Brush BarkLight { get; }
    public Brush BarkDark { get; }
    public Brush BarkLine { get; }
    public Brush Branch { get; }
    public Brush Leaf { get; }
    public Brush LeafLight { get; }
    public Brush Chip { get; }
    public Brush Ring { get; }
    /// <summary>Everything behind the tree (sky, sun or moon, clouds, hills, ground, decorations).</summary>
    public Drawing Background { get; }
    /// <summary>Time-of-day colour laid over the whole scene (null at day on normal maps).</summary>
    public Brush? Tint { get; }
    /// <summary>Things that glow and stay bright above the tint (moon, stars, pumpkin faces, lights).</summary>
    public Drawing? Lights { get; }
    public SceneParticles Particles { get; }

    private SceneStyle(MapTheme theme, DayTime time)
    {
        Theme = theme;
        Time = time;
        Sky = time switch
        {
            DayTime.Sunset => Gradient(("#5B4A8C", 0), ("#E9806A", 0.5), ("#F9C77C", 0.85), ("#FBE3A6", 1)),
            DayTime.Night => Gradient(("#0E1430", 0), ("#22295A", 0.7), ("#353A70", 1)),
            _ => theme == MapTheme.Halloween ? Gradient(("#9AA3B8", 0), ("#C9C2D2", 0.75), ("#D9D0D6", 1)) : Gradient(("#8EC9E8", 0), ("#CFE9F0", 0.75), ("#E7F1E4", 1))
        };
        var (hillFar, hillNear, ground, soil) = theme switch
        {
            MapTheme.Christmas => ("#E3ECF4", "#CFDDEA", "#F2F6FA", "#6A5A4A"),
            MapTheme.Halloween => ("#5E4E6E", "#4A3E5A", "#45503A", "#3A2A22"),
            MapTheme.Easter => ("#BFE3B0", "#9ED38C", "#86C866", "#7A5638"),
            _ => ("#A9CBA6", "#86B580", "#6FA05C", "#7A5638")
        };
        Ground = Solid(ground);
        (BarkLight, BarkDark, BarkLine, Branch) = theme switch
        {
            MapTheme.Halloween => (Solid("#6E5E52"), Solid("#5E4F44"), Solid("#3E322A"), Solid("#4E3E32")),
            MapTheme.Easter => (Solid("#A8744A"), Solid("#94633C"), Solid("#74492A"), Solid("#86593A")),
            _ => (Solid("#9B673E"), Solid("#865632"), Solid("#6B4326"), Solid("#7C502E"))
        };
        (Leaf, LeafLight) = theme switch
        {
            MapTheme.Christmas => (Solid("#1F6B3A"), Solid("#2E8A4C")),
            MapTheme.Halloween => (Solid("#C8621E"), Solid("#E8913A")),
            MapTheme.Easter => (Solid("#72C452"), Solid("#A2DD80")),
            _ => (Solid("#3F9B4A"), Solid("#5CB95E"))
        };
        Chip = Solid(theme == MapTheme.Christmas ? "#F4F8FC" : "#D9A86C");
        Ring = Solid("#E3BC86");
        Tint = (time, theme) switch
        {
            (DayTime.Night, _) => Solid("#0A1238", 105),
            (DayTime.Sunset, _) => Solid("#FF7A3C", 42),
            (DayTime.Day, MapTheme.Halloween) => Solid("#3A2050", 28),
            _ => null
        };
        Particles = theme switch { MapTheme.Christmas => SceneParticles.Snow, MapTheme.Halloween => SceneParticles.Bats, _ => SceneParticles.None };

        var back = new DrawingGroup();
        var lights = new DrawingGroup();
        // Sun, sunset sun or moon with stars.
        switch (time)
        {
            case DayTime.Day:
                Add(back, theme == MapTheme.Halloween ? "#F4ECD8" : "#FFF1B8", new EllipseGeometry(new Point(1060, 120), 54, 54));
                break;
            case DayTime.Sunset:
                Add(back, "#FFD08A", 90, new EllipseGeometry(new Point(1010, 470), 110, 110));
                Add(back, "#FFB060", new EllipseGeometry(new Point(1010, 470), 72, 72));
                break;
            case DayTime.Night:
                Add(lights, "#F4F1DC", 60, new EllipseGeometry(new Point(1060, 120), 74, 74));
                Add(lights, "#F6F3E2", new EllipseGeometry(new Point(1060, 120), 46, 46));
                Add(lights, "#DAD5BC", new EllipseGeometry(new Point(1046, 110), 9, 9));
                Add(lights, "#DAD5BC", new EllipseGeometry(new Point(1072, 134), 6, 6));
                var stars = new Random(7);
                for (var i = 0; i < 46; i++)
                {
                    var x = stars.NextDouble() * W;
                    if (x is > 400 and < 880) x = x < 640 ? x - 260 : x + 260; // keep the tree area clear
                    var y = stars.NextDouble() * 380;
                    var r = 1 + stars.NextDouble() * 1.8;
                    Add(lights, "#FFFFFF", (byte)(150 + stars.Next(100)), new EllipseGeometry(new Point(x, y), r, r));
                }
                break;
        }
        var cloud = time switch { DayTime.Sunset => ("#FFD6C8", (byte)200), DayTime.Night => ("#4A5070", (byte)120), _ => ("#FFFFFF", (byte)210) };
        foreach (var (cx, cy, s) in new[] { (220.0, 110.0, 1.0), (520, 70, 0.7), (930, 150, 0.85) })
            foreach (var (dx, dy, r) in new[] { (0.0, 0.0, 34.0), (34, -10, 42), (72, 4, 30) })
                Add(back, cloud.Item1, cloud.Item2, new EllipseGeometry(new Point(cx + dx * s, cy + dy * s), r * s, r * s * 0.8));
        Add(back, hillFar, Geometry.Parse("M 0,520 Q 180,400 380,500 T 760,470 T 1280,480 L 1280,620 L 0,620 Z"));
        if (theme == MapTheme.Christmas)
            foreach (var (x, y, s) in new[] { (110.0, 470.0, 1.0), (170, 486, 0.8), (1060, 470, 1.1), (1140, 480, 0.9), (1210, 476, 0.75) })
            {
                Add(back, "#2C6B48", Geometry.Parse(FormattableString.Invariant($"M {x},{y - 70 * s} L {x + 26 * s},{y} L {x - 26 * s},{y} Z")));
                Add(back, "#FFFFFF", Geometry.Parse(FormattableString.Invariant($"M {x},{y - 70 * s} L {x + 9 * s},{y - 46 * s} L {x - 9 * s},{y - 46 * s} Z")));
            }
        Add(back, hillNear, Geometry.Parse("M 0,575 Q 250,500 520,560 T 1000,540 T 1280,560 L 1280,620 L 0,620 Z"));
        Add(back, ground, new RectangleGeometry(new Rect(-2000, GroundY, W + 4000, H)));
        Add(back, soil, new RectangleGeometry(new Rect(-2000, GroundY + 40, W + 4000, 400)));
        AddDecorations(theme, back, lights);
        back.Freeze();
        Background = back;
        if (lights.Children.Count > 0)
        {
            lights.Freeze();
            Lights = lights;
        }
    }

    /// <summary>Map props on the ground, kept away from the trunk and both lumberjack spots.</summary>
    private static void AddDecorations(MapTheme theme, DrawingGroup back, DrawingGroup lights)
    {
        var outline = Pen("#3A2618", 2.5);
        switch (theme)
        {
            case MapTheme.Christmas:
                // Snowman.
                Add(back, "#FFFFFF", outline, new EllipseGeometry(new Point(150, 584), 30, 28));
                Add(back, "#FFFFFF", outline, new EllipseGeometry(new Point(150, 538), 22, 20));
                Add(back, "#FFFFFF", outline, new EllipseGeometry(new Point(150, 504), 16, 15));
                Add(back, "#F28C28", null, Geometry.Parse("M 140,505 L 120,509 L 140,511 Z"));
                Add(back, "#222222", null, new EllipseGeometry(new Point(144, 499), 2.5, 2.5));
                Add(back, "#C8202A", null, new RectangleGeometry(new Rect(134, 518, 34, 7)));
                Add(back, "#222222", null, new RectangleGeometry(new Rect(136, 474, 28, 18)));
                Add(back, "#222222", null, new RectangleGeometry(new Rect(130, 490, 40, 4)));
                // Presents.
                foreach (var (x, w, h, color) in new[] { (1080.0, 46.0, 40.0, "#C8202A"), (1132, 38, 52, "#2E7D4A"), (1176, 42, 34, "#2F5DA8") })
                {
                    var rect = new Rect(x, GroundY - h + 4, w, h);
                    Add(back, color, outline, new RectangleGeometry(rect));
                    Add(back, "#F2C232", null, new RectangleGeometry(new Rect(x + w / 2 - 4, rect.Top, 8, h)));
                    Add(back, "#F2C232", null, new RectangleGeometry(new Rect(x, rect.Top + h / 2 - 4, w, 8)));
                }
                // Fairy lights along the hills (they glow at night).
                for (var i = 0; i < 18; i++)
                {
                    var x = 30 + i * 22.0;
                    var y = 600 - 18 * Math.Sin(i * 0.7);
                    Add(lights, (i % 3) switch { 0 => "#FF5A5A", 1 => "#FFD84A", _ => "#6AD0FF" }, new EllipseGeometry(new Point(x, y), 3.5, 3.5));
                }
                break;

            case MapTheme.Halloween:
                // Gravestones.
                foreach (var (x, h) in new[] { (70.0, 70.0), (130, 54), (1200, 66) })
                {
                    Add(back, "#8A8A94", outline, Geometry.Parse(FormattableString.Invariant($"M {x - 22},{GroundY + 4} L {x - 22},{GroundY - h + 22} Q {x - 22},{GroundY - h} {x},{GroundY - h} Q {x + 22},{GroundY - h} {x + 22},{GroundY - h + 22} L {x + 22},{GroundY + 4} Z")));
                    Add(back, "#5E5E68", null, new RectangleGeometry(new Rect(x - 10, GroundY - h + 24, 20, 4)));
                }
                // Jack-o'-lanterns: the faces glow above the night tint.
                foreach (var (x, s) in new[] { (230.0, 1.0), (1080, 1.15), (1140, 0.8) })
                {
                    var cy = GroundY - 22 * s;
                    Add(back, "#E8742A", outline, new EllipseGeometry(new Point(x, cy), 32 * s, 25 * s));
                    Add(back, "#C85A1A", null, new EllipseGeometry(new Point(x, cy), 12 * s, 24 * s));
                    Add(back, "#4A6A2A", null, new RectangleGeometry(new Rect(x - 3 * s, cy - 33 * s, 6 * s, 10 * s)));
                    var face = FormattableString.Invariant(
                        $"M {x - 18 * s},{cy - 4 * s} L {x - 10 * s},{cy - 14 * s} L {x - 4 * s},{cy - 4 * s} Z M {x + 4 * s},{cy - 4 * s} L {x + 10 * s},{cy - 14 * s} L {x + 18 * s},{cy - 4 * s} Z M {x - 18 * s},{cy + 6 * s} L {x - 10 * s},{cy + 12 * s} L {x - 4 * s},{cy + 7 * s} L {x + 2 * s},{cy + 13 * s} L {x + 8 * s},{cy + 7 * s} L {x + 18 * s},{cy + 6 * s} L {x + 10 * s},{cy + 16 * s} L {x - 10 * s},{cy + 16 * s} Z");
                    Add(lights, "#FFE36A", Geometry.Parse(face));
                }
                break;

            case MapTheme.Easter:
                // Flowers.
                var random = new Random(3);
                for (var i = 0; i < 26; i++)
                {
                    var x = random.NextDouble() * W;
                    if (x is > 420 and < 860) continue;
                    var y = GroundY + 6 + random.NextDouble() * 26;
                    var petal = (i % 3) switch { 0 => "#FFFFFF", 1 => "#FFD3E6", _ => "#FFF3A0" };
                    foreach (var (dx, dy) in new[] { (-4.0, 0.0), (4, 0), (0, -4), (0, 4) }) Add(back, petal, new EllipseGeometry(new Point(x + dx, y + dy), 3.5, 3.5));
                    Add(back, "#F2B72C", new EllipseGeometry(new Point(x, y), 2.5, 2.5));
                }
                // Painted eggs.
                foreach (var (x, color, band) in new[] { (90.0, "#FF8FB8", "#FFFFFF"), (165, "#7FD3FF", "#FFE36A"), (300, "#C59BFF", "#FFFFFF"), (985, "#FFE36A", "#FF8FB8"), (1090, "#8EE07A", "#FFFFFF"), (1225, "#FFB37A", "#7FD3FF") })
                {
                    var egg = new EllipseGeometry(new Point(x, GroundY - 14), 14, 19);
                    Add(back, color, outline, egg);
                    Add(back, band, null, new RectangleGeometry(new Rect(x - 13, GroundY - 17, 26, 6)));
                }
                // Bunny.
                Add(back, "#F4F0EA", outline, new EllipseGeometry(new Point(1160, GroundY - 26), 26, 24));
                Add(back, "#F4F0EA", outline, new EllipseGeometry(new Point(1140, GroundY - 56), 17, 15));
                Add(back, "#F4F0EA", outline, new EllipseGeometry(new Point(1134, GroundY - 86), 6, 18));
                Add(back, "#F4F0EA", outline, new EllipseGeometry(new Point(1148, GroundY - 88), 6, 18));
                Add(back, "#FFB6C8", null, new EllipseGeometry(new Point(1134, GroundY - 86), 3, 12));
                Add(back, "#FFB6C8", null, new EllipseGeometry(new Point(1148, GroundY - 88), 3, 12));
                Add(back, "#2A2A2A", null, new EllipseGeometry(new Point(1132, GroundY - 58), 2.5, 2.5));
                Add(back, "#FFFFFF", outline, new EllipseGeometry(new Point(1186, GroundY - 22), 8, 8));
                break;
        }
    }

    /// <summary>Decoration on every branch's leaves (branch-local coordinates, see TreemanView).</summary>
    public void DrawLeafDecoration(DrawingContext dc, double branchLength, int id)
    {
        switch (Theme)
        {
            case MapTheme.Christmas:
                dc.DrawEllipse(SnowCap, null, new Point(branchLength - 22, -26), 32, 8);
                dc.DrawEllipse(id % 2 == 0 ? Ornament1 : Ornament2, null, new Point(branchLength - 36, 10), 6, 6);
                dc.DrawEllipse(id % 2 == 0 ? Ornament2 : Ornament1, null, new Point(branchLength - 2, -2), 5, 5);
                break;
            case MapTheme.Easter:
                dc.DrawEllipse(Blossom, null, new Point(branchLength - 40, -12), 5, 5);
                dc.DrawEllipse(Blossom, null, new Point(branchLength - 12, -26), 5, 5);
                dc.DrawEllipse(BlossomWhite, null, new Point(branchLength - 22, 6), 5, 5);
                dc.DrawEllipse(Blossom, null, new Point(branchLength + 6, 2), 4, 4);
                break;
            case MapTheme.Halloween when id % 3 == 0:
                dc.DrawLine(WebPen, new Point(branchLength - 50, 4), new Point(branchLength - 50, 40));
                dc.DrawEllipse(Spider, null, new Point(branchLength - 50, 44), 6, 5);
                break;
        }
    }

    private static readonly Brush SnowCap = Solid("#FFFFFF", 235);
    private static readonly Brush Ornament1 = Solid("#E02B3A");
    private static readonly Brush Ornament2 = Solid("#F2C232");
    private static readonly Brush Blossom = Solid("#FF9CC6");
    private static readonly Brush BlossomWhite = Solid("#FFFFFF");
    private static readonly Brush Spider = Solid("#1A1A1A");
    private static readonly Pen WebPen = Pen("#E8E8E8", 1.2);

    private static void Add(DrawingGroup group, string color, Geometry geometry) => Add(group, color, 255, geometry);

    private static void Add(DrawingGroup group, string color, byte alpha, Geometry geometry) =>
        group.Children.Add(new GeometryDrawing(Solid(color, alpha), null, geometry));

    private static void Add(DrawingGroup group, string color, Pen? pen, Geometry geometry) =>
        group.Children.Add(new GeometryDrawing(Solid(color), pen, geometry));

    private static Brush Gradient(params (string Color, double Offset)[] stops)
    {
        var collection = new GradientStopCollection();
        foreach (var (color, offset) in stops) collection.Add(new GradientStop(SkinLook.Hex(color), offset));
        var brush = new LinearGradientBrush(collection, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static Brush Solid(string color, byte alpha = 255)
    {
        var c = SkinLook.Hex(color);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(string color, double thickness)
    {
        var pen = new Pen(Solid(color), thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
