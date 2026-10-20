using System.Windows;
using System.Windows.Media;

namespace ViennaAtomicClock;

/// <summary>
/// The selectable looks. Each has a light and a dark palette plus its own fonts and corner style.
/// In the coloured mode the palette is generated from one colour the user picks.
/// </summary>
internal static class Themes
{
    public const string War = "war";
    public const string Hacker = "hacker";
    public const string Anime = "anime";
    public const string Tetris = "tetris";
    public const string America = "america";
    public const string Minecraft = "minecraft";

    public static readonly (string Id, string Label, string Glyph, string Hint)[] All =
    {
        (War, "Kriegsfilm", "✪", "Grau, Filmkorn, Schablonenschrift"),
        (Hacker, "Hacker", ">_", "Grünes Terminal, Code-Regen"),
        (Anime, "Anime", "🐾", "Pink, Katzenohren, Pfoten"),
        (Tetris, "Tetris", "▦", "Fallende Steine, Arcade-Look"),
        (America, "America", "★", "Stars and Stripes, wehende Fahne"),
        (Minecraft, "Minecraft", "⛏", "Blöcke, Wolken, Pixel-Look")
    };

    public static string Normalize(string? id) => All.Any(t => t.Id == id) ? id! : War;

    /// <param name="custom">Picked colour (with alpha) for the coloured mode, or null for the theme's own palette.</param>
    public static void Apply(ResourceDictionary resources, string theme, bool dark, Color? custom)
    {
        var p = custom is { } c ? ColoredPalette(theme, c) : Palette(theme, dark).Select(Parse).ToArray();
        resources["WindowBackground"] = Brush(p[0]);
        resources["SurfaceBrush"] = Brush(p[1]);
        resources["SurfaceAlt"] = Brush(p[2]);
        resources["CardBorder"] = Brush(p[3]);
        resources["TextPrimary"] = Brush(p[4]);
        resources["TextSecondary"] = Brush(p[5]);
        resources["TextMuted"] = Brush(p[6]);
        resources["HoverBrush"] = Brush(p[7]);
        resources["TrackBrush"] = Brush(p[8]);
        resources["AccentBrush"] = Brush(p[9]);
        resources["AccentForeground"] = Brush(p[10]);
        resources["AccentSoft"] = Brush(p[11]);
        resources["GreenBrush"] = Brush(p[12]);
        resources["GreenSoft"] = Brush(p[13]);
        resources["OrangeBrush"] = Brush(p[14]);
        // Popups sit over other windows, so they get the surface colour without transparency.
        resources["PopupBrush"] = Brush(Color.FromRgb(p[1].R, p[1].G, p[1].B));

        var (ui, display, title, radius) = theme switch
        {
            Hacker => ("Consolas, Cascadia Mono, Courier New", "Consolas, Cascadia Mono, Courier New", "OCR A Extended, Consolas", 2.0),
            Anime => ("Segoe UI Variable Text, Segoe UI", "Segoe UI Variable Display, Segoe UI", "Ink Free, Segoe Print, Segoe UI", 18.0),
            Tetris => ("Bahnschrift, Segoe UI", "Lucida Console, Consolas", "OCR A Extended, Consolas", 0.0),
            America => ("Bahnschrift, Segoe UI", "Bahnschrift, Segoe UI", "Impact, Arial Black", 6.0),
            Minecraft => ("Lucida Console, Consolas", "Lucida Console, Consolas", "Lucida Console, Consolas", 0.0),
            _ => ("Courier New, Consolas", "Courier New, Consolas", "Stencil, Impact, Arial Black", 4.0)
        };
        resources["UiFont"] = new FontFamily(ui);
        resources["DisplayFont"] = new FontFamily(display);
        resources["StencilFont"] = new FontFamily(title);
        resources["CardRadius"] = new CornerRadius(radius);
    }

    /// <summary>Corner radius of the whole window for a theme.</summary>
    public static double WindowRadius(string theme) => theme switch
    {
        Hacker => 4, Anime => 26, Tetris => 4, America => 12, Minecraft => 2, _ => 10
    };

    /// <summary>How see-through the cards are, so animated backdrops can shine through.</summary>
    private static double SurfaceOpacity(string theme) => theme switch
    {
        Hacker => 0.85, Anime => 0.88, Tetris => 0.86, America => 0.88, Minecraft => 0.93, _ => 1
    };

    /// <summary>Dark palette tinted with the picked colour; the colour itself becomes the accent.</summary>
    private static Color[] ColoredPalette(string theme, Color pick)
    {
        var (h, s, v) = ColorMath.ToHsv(pick);
        var tint = Math.Clamp(s, 0.15, 0.9);
        var alpha = Math.Clamp(pick.A / 255.0, 0.4, 1) * SurfaceOpacity(theme);
        Color Hsv(double sat, double val, double a = 1) => ColorMath.FromHsv(h, sat, val, a);

        var accent = Hsv(Math.Max(s, 0.35), Math.Max(v, 0.82));
        var accentLuma = 0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B;
        return new[]
        {
            Hsv(tint * 0.65, 0.12),                 // window
            Hsv(tint * 0.6, 0.17, alpha),           // surface
            Hsv(tint * 0.6, 0.22, Math.Min(1, alpha + 0.05)), // surface alt
            Hsv(tint * 0.55, 0.34),                 // border
            Hsv(0.06, 0.97),                        // text
            Hsv(tint * 0.35, 0.78),                 // text 2
            Hsv(tint * 0.35, 0.56),                 // muted
            Hsv(tint * 0.6, 0.27),                  // hover
            Hsv(tint * 0.6, 0.26),                  // track
            accent,
            accentLuma > 150 ? Hsv(tint, 0.12) : Colors.White,
            Hsv(tint * 0.75, 0.32, 0.85),           // accent soft
            Color.FromRgb(126, 224, 160),
            Color.FromArgb(200, 22, 60, 40),
            Color.FromRgb(255, 196, 92)
        };
    }

    // Order: window, surface, surfaceAlt, border, text, text2, muted, hover, track,
    //        accent, accentForeground, accentSoft, green, greenSoft, orange.
    private static string[] Palette(string theme, bool dark) => (theme, dark) switch
    {
        (Hacker, true) => new[]
        {
            "#020604", "#D9050E08", "#E00A170E", "#1F6B34", "#8CFFA8", "#45C866", "#2B8044", "#0F2A17", "#0E2715",
            "#39FF6A", "#021006", "#B30E2E18", "#39FF6A", "#0E2E18", "#FFD23F"
        },
        (Hacker, false) => new[]
        {
            "#C9DCCB", "#E6EEF6EE", "#E6E2EEE3", "#7FB08A", "#0B3D1A", "#2F6B40", "#5C8A68", "#D2E6D5", "#C3D9C7",
            "#0E7A33", "#EAFBEF", "#C9E6D0", "#0E7A33", "#CDE8D4", "#9A6A00"
        },
        (Anime, true) => new[]
        {
            "#24112B", "#E0381B41", "#E6472350", "#7A3C88", "#FFE8F4", "#F5B3D6", "#B97FA6", "#4A2453", "#4D2757",
            "#FF7AC0", "#2A0C1D", "#C05A2552", "#8EF0C8", "#1F4A3E", "#FFB86B"
        },
        (Anime, false) => new[]
        {
            "#FFDDEE", "#E6FFF6FB", "#E6FFEAF4", "#FFB8DA", "#5A2346", "#9A5582", "#C089AB", "#FFE3F0", "#FFD3E8",
            "#FF5FA8", "#FFFFFF", "#FFD3E7", "#2FBF8A", "#D5F5E9", "#E07A12"
        },
        (Tetris, true) => new[]
        {
            "#07071A", "#D90F1030", "#E0181A45", "#3A3D8C", "#F4F4FF", "#A9ACE0", "#6D70A8", "#22245A", "#1E2050",
            "#00E5FF", "#04121A", "#B3143A5C", "#4CFF6A", "#123A22", "#FFB020"
        },
        (Tetris, false) => new[]
        {
            "#DDE1F7", "#E6F7F8FF", "#E6E9ECFF", "#9EA6E0", "#141638", "#3E4380", "#7177B0", "#D6DAF7", "#C9CEF2",
            "#7B2CFF", "#FFFFFF", "#E0D3FF", "#1FAA4A", "#CFEFD9", "#E07800"
        },
        (America, true) => new[]
        {
            "#06122B", "#E00B1F44", "#E6122B5A", "#2C4A86", "#FFFFFF", "#C6D2EA", "#8496BC", "#183466", "#1A3266",
            "#E8283C", "#FFFFFF", "#C0441726", "#7BE0A0", "#123A2A", "#FFD24A"
        },
        (America, false) => new[]
        {
            "#E9EDF5", "#E6FFFFFF", "#E6F1F3F8", "#B7C3DA", "#0A1F44", "#33476E", "#6E7FA0", "#E3E8F2", "#D5DCEA",
            "#B22234", "#FFFFFF", "#F6D5D9", "#1F7A4A", "#D3EEDC", "#C77A00"
        },
        (Minecraft, true) => new[]
        {
            "#1B1B1B", "#EB2E2E2E", "#F03C3C3C", "#0E0E0E", "#FFFFFF", "#C8C8C8", "#8B8B8B", "#4A4A4A", "#262626",
            "#6CC349", "#0E2207", "#C02F4A22", "#6CC349", "#253A1C", "#F2B233"
        },
        (Minecraft, false) => new[]
        {
            "#8FB8FF", "#F0C6C6C6", "#F0B5B5B5", "#373737", "#3F3F3F", "#555555", "#6E6E6E", "#D0D0D0", "#8B8B8B",
            "#3C8527", "#FFFFFF", "#A9C99B", "#3C8527", "#BFD9B3", "#B8720F"
        },
        (_, true) => new[]
        {
            "#121110", "#1C1B19", "#262421", "#3A3732", "#E6E1D5", "#A39D90", "#77726A", "#2E2C28", "#2F2D29",
            "#D6CFBE", "#161513", "#2D2B27", "#A9B59A", "#262A21", "#D9B26A"
        },
        _ => new[]
        {
            "#BDB7AB", "#DCD6C9", "#CEC8BA", "#A49E91", "#1B1A17", "#4B4840", "#6E6A60", "#C9C2B3", "#C2BBAC",
            "#36332E", "#EDE8DC", "#C6BFAF", "#4E5A3E", "#C3C5AF", "#7A5A1E"
        }
    };

    private static Color Parse(string color) => (Color)ColorConverter.ConvertFromString(color);

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

internal static class ColorMath
{
    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        var h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v, double a = 1)
    {
        h = ((h % 360) + 360) % 360 / 60;
        var c = v * s;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x),
            3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
