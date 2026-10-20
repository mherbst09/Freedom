using System.Windows;
using System.Windows.Media;

namespace ViennaAtomicClock;

/// <summary>
/// Small vector pictures of the drinks (cans, cups, glasses, bottles) on a transparent background.
/// Drawn in code in the brands' typical colours, without logos.
/// </summary>
internal static class DrinkArt
{
    private const double S = 64;
    private static readonly Dictionary<string, ImageSource> Cache = new();

    public static ImageSource For(string brand, string name, string category)
    {
        var key = $"{brand}|{name}|{category}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var image = new DrawingImage(Draw(brand.ToUpperInvariant(), name.ToLowerInvariant(), category));
        image.Freeze();
        Cache[key] = image;
        return image;
    }

    private static Drawing Draw(string brand, string name, string category)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            // Invisible frame keeps every picture at the same 64x64 size.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, S, S));

            switch (category)
            {
                case "Energy":
                    var (body, band, mark) = EnergyColors(brand, name);
                    Can(dc, tall: brand is "MONSTER" or "ROCKSTAR" || name.Contains("xl") || name.Contains("groß"), body, band, mark);
                    break;
                case "Kaffee":
                    Coffee(dc, name);
                    break;
                case "Eiskaffee":
                    if (brand == "NESCAFÉ") Can(dc, false, C("#7A4A2A"), C("#E9D7BF"), C("#C8102E"));
                    else if (name.Contains("doubleshot")) Can(dc, false, C("#1F2A44"), C("#D9C29A"), C("#FFFFFF"));
                    else if (brand == "STARBUCKS") ToGo(dc, C("#F4EDE2"), C("#00704A"), C("#B48A60"));
                    else Carton(dc, name.Contains("macchiato") ? C("#E8D5BC") : C("#FFFFFF"), name.Contains("double") ? C("#2C2C2C") : C("#7A4A2A"));
                    break;
                case "Tee":
                    Tea(dc, name.Contains("grün") ? C("#9CC46B") : C("#B5652B"));
                    break;
                case "Softdrink":
                    Bottle(dc, name.Contains("mate") ? C("#E3A33B") : C("#3B1E12"), name.Contains("mate") ? C("#2E6B3A") : C("#D7282F"));
                    break;
                default:
                    Glass(dc);
                    break;
            }
        }
        group.Freeze();
        return group;
    }

    private static (Color Body, Color Band, Color Mark) EnergyColors(string brand, string name)
    {
        var zero = name.Contains("zero") || name.Contains("sugarfree");
        return brand switch
        {
            "RED BULL" => zero ? (C("#D9DEE6"), C("#1E3A8A"), C("#D7282F")) : (C("#1E3A8A"), C("#C9CED6"), C("#D7282F")),
            "MONSTER" => name.Contains("ultra") ? (C("#F2F4F5"), C("#B9C2C8"), C("#7FA8C9"))
                : name.Contains("mango") ? (C("#2B3F8F"), C("#F6A21A"), C("#FFD24A"))
                : name.Contains("doctor") ? (C("#1C1C1C"), C("#F2C230"), C("#FFFFFF"))
                : name.Contains("pipeline") ? (C("#1A9AA8"), C("#F05A8A"), C("#FFFFFF"))
                : zero ? (C("#1C1C1C"), C("#3A3A3A"), C("#BFEFFF")) : (C("#151515"), C("#2A2A2A"), C("#7CFF4F")),
            "ROCKSTAR" => (C("#141414"), C("#D9A521"), C("#F5D25A")),
            "EFFECT" => (C("#141414"), C("#D7282F"), C("#FFFFFF")),
            "HELL" => (C("#C8102E"), C("#1A1A1A"), C("#FFD000")),
            "28 BLACK" => (C("#4B1F5C"), C("#1A1A1A"), C("#B57EDC")),
            "POWER HORSE" => (C("#7A1020"), C("#D9B44A"), C("#FFFFFF")),
            "BURN" => (C("#151515"), C("#F05A1A"), C("#FFC23A")),
            "KONG STRONG" => name.Contains("wild") ? (C("#5B2A86"), C("#F3C200"), C("#FFFFFF"))
                : name.Contains("cola") ? (C("#5A1A12"), C("#D7282F"), C("#FFFFFF"))
                : zero ? (C("#E6E6E6"), C("#D7282F"), C("#F3C200")) : (C("#D7282F"), C("#F3C200"), C("#1A1A1A")),
            "FLYING POWER" => zero ? (C("#DDE6F2"), C("#1F5FBF"), C("#F28C1A")) : (C("#1F5FBF"), C("#F28C1A"), C("#FFFFFF")),
            "S-BUDGET" => zero ? (C("#F1F1F1"), C("#F4C400"), C("#1E4FA0")) : (C("#F4C400"), C("#1E4FA0"), C("#FFFFFF")),
            "CLEVER" => name.Contains("kiwi") ? (C("#7DBA2F"), C("#1F5FBF"), C("#FFFFFF"))
                : zero ? (C("#E8EEF5"), C("#1F5FBF"), C("#D7282F")) : (C("#1F5FBF"), C("#E8EEF5"), C("#D7282F")),
            _ => (C("#3A3F47"), C("#9AA3AE"), C("#FFD000"))
        };
    }

    private static void Can(DrawingContext dc, bool tall, Color body, Color band, Color mark)
    {
        var w = tall ? 36.0 : 30.0;
        var top = tall ? 4.0 : 10.0;
        var bottom = 60.0;
        var x = (S - w) / 2;

        // Metallic body with a bright highlight stripe.
        var metal = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(Shade(body, -0.35), 0), new GradientStop(body, 0.25),
                new GradientStop(Shade(body, 0.45), 0.42), new GradientStop(body, 0.6), new GradientStop(Shade(body, -0.4), 1)
            }
        };
        var bodyRect = new RectangleGeometry(new Rect(x, top + 3, w, bottom - top - 5), 3, 3);
        dc.DrawGeometry(metal, new Pen(new SolidColorBrush(Shade(body, -0.6)), 0.8), bodyRect);

        // Lid and bottom rim.
        var lid = new LinearGradientBrush(C("#E8ECEF"), C("#8E969E"), 0);
        dc.DrawGeometry(lid, new Pen(new SolidColorBrush(C("#6B737B")), 0.6), new RectangleGeometry(new Rect(x + 1.5, top, w - 3, 4), 1.5, 1.5));
        dc.DrawGeometry(lid, null, new RectangleGeometry(new Rect(x + 1.5, bottom - 3, w - 3, 3), 1.5, 1.5));

        // Brand-coloured band and a generic energy mark.
        var bandTop = top + (bottom - top) * 0.42;
        dc.PushClip(bodyRect);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(235, band.R, band.G, band.B)), null, new Rect(x, bandTop, w, (bottom - top) * 0.2));
        dc.Pop();

        var cx = S / 2;
        var my = top + (bottom - top) * 0.28;
        var bolt = Poly(new Point(cx + 2, my - 7), new Point(cx - 4, my + 1), new Point(cx, my + 1), new Point(cx - 2, my + 8),
            new Point(cx + 5, my - 1), new Point(cx + 1, my - 1));
        dc.DrawGeometry(new SolidColorBrush(mark), null, bolt);

        // Glossy highlight.
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null, new Rect(x + w * 0.3, top + 5, 2.2, bottom - top - 10));
    }

    private static void Coffee(DrawingContext dc, string name)
    {
        var coffee = C("#5A3218");
        var crema = C("#C58A4E");
        var foam = C("#F4E7D3");
        if (name.Contains("doppelt"))
            Cup(dc, 16, 30, 32, 18, coffee, crema, steam: true, saucer: true);
        else if (name.Contains("espresso"))
            Cup(dc, 20, 34, 24, 15, coffee, crema, steam: true, saucer: true);
        else if (name.Contains("latte"))
            Glass(dc, layers: new[] { (C("#F4E7D3"), 0.25), (C("#B07A4A"), 0.35), (C("#EEDCC2"), 0.4) });
        else if (name.Contains("cold"))
            Glass(dc, layers: new[] { (C("#3B200E"), 1.0) }, ice: true);
        else if (name.Contains("melange") || name.Contains("cappuccino") || name.Contains("flat"))
            Cup(dc, 13, 26, 38, 22, foam, Shade(foam, -0.08), steam: true, saucer: true, heart: name.Contains("flat") || name.Contains("cappuccino"));
        else
            Mug(dc, name.Contains("entkoffein") ? C("#2F6B3A") : name.Contains("instant") ? C("#B5652B") : C("#E9E4DA"), coffee);
    }

    private static void Cup(DrawingContext dc, double x, double top, double w, double h, Color drink, Color surface,
        bool steam, bool saucer, bool heart = false)
    {
        var porcelain = new LinearGradientBrush(C("#FFFFFF"), C("#D5D2CC"), 0);
        var edge = new Pen(new SolidColorBrush(C("#8C8880")), 0.9);
        if (saucer)
            dc.DrawEllipse(porcelain, edge, new Point(S / 2, top + h + 2), w * 0.75, 3.6);

        var cup = new StreamGeometry();
        using (var c = cup.Open())
        {
            c.BeginFigure(new Point(x, top), true, true);
            c.LineTo(new Point(x + w, top), true, false);
            c.BezierTo(new Point(x + w, top + h * 0.8), new Point(x + w * 0.8, top + h), new Point(x + w * 0.55, top + h), true, false);
            c.LineTo(new Point(x + w * 0.45, top + h), true, false);
            c.BezierTo(new Point(x + w * 0.2, top + h), new Point(x, top + h * 0.8), new Point(x, top), true, false);
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(C("#D5D2CC")), 2.6), new EllipseGeometry(new Point(x + w + 2.5, top + h * 0.38), 4, h * 0.22));
        dc.DrawGeometry(porcelain, edge, cup);
        dc.DrawEllipse(new SolidColorBrush(drink), edge, new Point(x + w / 2, top + 0.5), w / 2 - 0.8, 2.6);
        dc.DrawEllipse(new SolidColorBrush(surface), null, new Point(x + w / 2, top + 0.5), w / 2 - 4, 1.6);
        if (heart)
            dc.DrawGeometry(new SolidColorBrush(C("#B07A4A")), null, HeartGeometry(new Point(x + w / 2, top + 0.3), 2.6));
        if (steam) Steam(dc, x + w / 2, top - 3);
    }

    private static void Mug(DrawingContext dc, Color mug, Color drink)
    {
        const double x = 15, top = 20, w = 30, h = 34;
        var body = new LinearGradientBrush(Shade(mug, 0.15), Shade(mug, -0.2), 0);
        var edge = new Pen(new SolidColorBrush(Shade(mug, -0.5)), 0.9);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Shade(mug, -0.15)), 3.2), new EllipseGeometry(new Point(x + w + 3, top + h * 0.45), 5.5, 8));
        dc.DrawGeometry(body, edge, new RectangleGeometry(new Rect(x, top, w, h), 4, 4));
        dc.DrawEllipse(new SolidColorBrush(drink), edge, new Point(x + w / 2, top + 1), w / 2 - 1, 2.6);
        Steam(dc, x + w / 2, top - 3);
    }

    private static void Glass(DrawingContext dc, (Color Color, double Part)[]? layers = null, bool ice = false)
    {
        const double top = 8, bottom = 58;
        var glass = Poly(new Point(19, top), new Point(45, top), new Point(42, bottom), new Point(22, bottom));
        layers ??= new[] { (C("#8FC6E8"), 0.7) };
        dc.PushClip(glass);
        var y = bottom;
        var fill = 0.82;
        foreach (var (color, part) in layers.Reverse())
        {
            var height = (bottom - top) * fill * part;
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(10, y - height, 44, height));
            y -= height;
        }
        if (ice)
        {
            var cube = new SolidColorBrush(Color.FromArgb(150, 230, 245, 255));
            dc.DrawRectangle(cube, null, new Rect(24, y + 2, 8, 8));
            dc.DrawRectangle(cube, null, new Rect(33, y + 6, 7, 7));
        }
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null, new Rect(23, top, 3, bottom - top));
        dc.Pop();
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(40, 200, 230, 255)), new Pen(new SolidColorBrush(C("#7D8A96")), 1.1), glass);
    }

    private static void ToGo(DrawingContext dc, Color cup, Color sleeve, Color lid)
    {
        var body = Poly(new Point(19, 14), new Point(45, 14), new Point(41, 60), new Point(23, 60));
        dc.DrawGeometry(new LinearGradientBrush(cup, Shade(cup, -0.15), 0), new Pen(new SolidColorBrush(Shade(cup, -0.4)), 0.8), body);
        dc.PushClip(body);
        dc.DrawRectangle(new SolidColorBrush(sleeve), null, new Rect(10, 30, 44, 14));
        dc.Pop();
        dc.DrawEllipse(new SolidColorBrush(C("#FFFFFF")), null, new Point(32, 37), 4.5, 4.5);
        dc.DrawGeometry(new SolidColorBrush(lid), new Pen(new SolidColorBrush(Shade(lid, -0.4)), 0.8), new RectangleGeometry(new Rect(17, 9, 30, 6), 2.5, 2.5));
        dc.DrawLine(new Pen(new SolidColorBrush(C("#2E7D4F")), 2.4), new Point(36, 10), new Point(41, 1));
    }

    private static void Carton(DrawingContext dc, Color cup, Color stripe)
    {
        var body = Poly(new Point(18, 16), new Point(46, 16), new Point(42, 60), new Point(22, 60));
        dc.DrawGeometry(new LinearGradientBrush(cup, Shade(cup, -0.18), 0), new Pen(new SolidColorBrush(C("#8C8880")), 0.8), body);
        dc.PushClip(body);
        dc.DrawRectangle(new SolidColorBrush(stripe), null, new Rect(10, 34, 44, 9));
        dc.Pop();
        dc.DrawGeometry(new LinearGradientBrush(C("#F2F2F2"), C("#A9A9A9"), 0), new Pen(new SolidColorBrush(C("#7A7A7A")), 0.8),
            new RectangleGeometry(new Rect(16, 12, 32, 5), 2, 2));
        dc.DrawLine(new Pen(new SolidColorBrush(C("#E04B4B")), 2.2), new Point(38, 13), new Point(44, 3));
    }

    private static void Tea(DrawingContext dc, Color tea)
    {
        Cup(dc, 14, 28, 34, 22, tea, Shade(tea, 0.15), steam: true, saucer: true);
        dc.DrawLine(new Pen(new SolidColorBrush(C("#DDD6C8")), 0.8), new Point(36, 28), new Point(44, 38));
        dc.DrawRectangle(new SolidColorBrush(C("#F2D06B")), new Pen(new SolidColorBrush(C("#B99A3C")), 0.6), new Rect(41, 38, 7, 8));
    }

    private static void Bottle(DrawingContext dc, Color drink, Color label)
    {
        var bottle = new StreamGeometry();
        using (var c = bottle.Open())
        {
            c.BeginFigure(new Point(28, 4), true, true);
            c.LineTo(new Point(36, 4), true, false);
            c.LineTo(new Point(36, 16), true, false);
            c.BezierTo(new Point(43, 22), new Point(44, 26), new Point(44, 32), true, false);
            c.LineTo(new Point(44, 58), true, false);
            c.BezierTo(new Point(44, 61), new Point(42, 62), new Point(40, 62), true, false);
            c.LineTo(new Point(24, 62), true, false);
            c.BezierTo(new Point(22, 62), new Point(20, 61), new Point(20, 58), true, false);
            c.LineTo(new Point(20, 32), true, false);
            c.BezierTo(new Point(20, 26), new Point(21, 22), new Point(28, 16), true, false);
        }
        dc.DrawGeometry(new LinearGradientBrush(Shade(drink, 0.25), Shade(drink, -0.25), 0), new Pen(new SolidColorBrush(Shade(drink, -0.6)), 0.8), bottle);
        dc.PushClip(bottle);
        dc.DrawRectangle(new SolidColorBrush(label), null, new Rect(10, 36, 44, 11));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), null, new Rect(24, 6, 2.5, 54));
        dc.Pop();
        dc.DrawRectangle(new SolidColorBrush(C("#C9CED6")), null, new Rect(27, 2, 10, 4));
    }

    private static void Steam(DrawingContext dc, double cx, double y)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, 170, 170, 170)), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        foreach (var dx in new[] { -6.0, 0, 6 })
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(cx + dx, y), false, false);
                c.BezierTo(new Point(cx + dx - 3, y - 4), new Point(cx + dx + 3, y - 7), new Point(cx + dx, y - 11), true, false);
            }
            dc.DrawGeometry(null, pen, g);
        }
    }

    private static Geometry HeartGeometry(Point c, double r)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y + r), true, true);
            ctx.BezierTo(new Point(c.X - r * 2, c.Y - r * 0.2), new Point(c.X - r, c.Y - r * 1.4), new Point(c.X, c.Y - r * 0.4), true, false);
            ctx.BezierTo(new Point(c.X + r, c.Y - r * 1.4), new Point(c.X + r * 2, c.Y - r * 0.2), new Point(c.X, c.Y + r), true, false);
        }
        return g;
    }

    private static Geometry Poly(params Point[] points)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(points[0], true, true);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, false);
        }
        return g;
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Shade(Color c, double amount)
    {
        byte F(byte v) => (byte)Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromRgb(F(c.R), F(c.G), F(c.B));
    }
}
