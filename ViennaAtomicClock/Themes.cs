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
    public const string Standard = "standard";
    public const string Eagle = "eagle";
    public const string Mystic = "mystic";
    public const string Christmas = "christmas";
    public const string Halloween = "halloween";
    public const string Jungle = "jungle";

    public static readonly (string Id, string Label, string Glyph, string Hint)[] All =
    {
        (Standard, "Standard", "◻", "Schlicht, nur deine Farben"),
        (Eagle, "Eagle", "🦅", "Logo-Farben, frisch animiert"),
        (War, "Kriegsfilm", "✪", "Grau, Filmkorn, Schablonenschrift"),
        (Hacker, "Hacker", ">_", "Grünes Terminal, Code-Regen"),
        (Anime, "Anime", "🐾", "Pink, Katzenohren, Pfoten"),
        (Tetris, "Tetris", "▦", "Fallende Steine, Arcade-Look"),
        (America, "America", "★", "Stars and Stripes, wehende Fahne"),
        (Minecraft, "Minecraft", "⛏", "Blöcke, Wolken, Pixel-Look"),
        (Mystic, "Mystisch", "🌸", "Kirschblüten, Nebel, Mondlicht"),
        (Christmas, "Weihnachten", "🎄", "Schnee, Tannengrün, Lichterglanz"),
        (Halloween, "Halloween", "🎃", "Mondlicht, Nebel, Fledermäuse"),
        (Jungle, "Jungle", "🌿", "Blätter, Lianen, Glühwürmchen")
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
        // Filled buttons: the Eagle theme uses its pink-to-cyan logo gradient, all others the accent colour.
        if (theme == Eagle && custom is null)
        {
            var fill = new LinearGradientBrush(Color.FromRgb(255, 46, 136), Color.FromRgb(50, 200, 240), new Point(0, 0), new Point(1, 1));
            fill.Freeze();
            resources["AccentFill"] = fill;
        }
        else resources["AccentFill"] = Brush(p[9]);

        var (ui, display, title, radius) = theme switch
        {
            // Corners stay crisp per theme, but every theme now has softly rounded ones.
            Hacker => ("Consolas, Cascadia Mono", "Consolas, Cascadia Mono", "OCR A Extended, Consolas", 8.0),
            Anime => ("Segoe UI Variable Text, Segoe UI", "Segoe UI Variable Display, Segoe UI", "Ink Free, Segoe Print, Segoe UI", 22.0),
            Tetris => ("Bahnschrift, Segoe UI", "Bahnschrift, Segoe UI", "OCR A Extended, Consolas", 8.0),
            America => ("Bahnschrift, Segoe UI", "Bahnschrift, Segoe UI", "Impact, Arial Black", 12.0),
            Minecraft => ("Lucida Console, Consolas", "Lucida Console, Consolas", "Lucida Console, Consolas", 6.0),
            Standard => ("Segoe UI Variable Text, Segoe UI", "Segoe UI Variable Display, Segoe UI", "Segoe UI Variable Display Semibold, Segoe UI Semibold", 14.0),
            Eagle => ("Segoe UI Variable Text, Segoe UI", "Segoe UI Variable Display, Segoe UI", "Segoe UI Variable Display Semibold, Segoe UI Semibold", 16.0),
            // Brush-like serif titles in the spirit of Japanese Mincho type.
            Mystic => ("Yu Gothic UI, Segoe UI Variable Text, Segoe UI", "Yu Mincho, Georgia, Segoe UI", "Yu Mincho Demibold, Yu Mincho, Georgia", 14.0),
            Christmas => ("Segoe UI Variable Text, Segoe UI", "Georgia, Segoe UI Variable Display, Segoe UI", "Georgia Bold, Georgia, Segoe UI Semibold", 18.0),
            Halloween => ("Segoe UI Variable Text, Segoe UI", "Georgia, Segoe UI Variable Display, Segoe UI", "Copperplate Gothic Bold, Georgia, Segoe UI Semibold", 15.0),
            Jungle => ("Segoe UI Variable Text, Segoe UI", "Trebuchet MS, Segoe UI Variable Display, Segoe UI", "Trebuchet MS Bold, Bahnschrift SemiBold, Segoe UI Semibold", 20.0),
            _ => ("Consolas, Courier New", "Consolas, Courier New", "Stencil, Impact, Arial Black", 10.0)
        };
        resources["UiFont"] = new FontFamily(ui);
        resources["DisplayFont"] = new FontFamily(display);
        resources["StencilFont"] = new FontFamily(title);
        resources["CardRadius"] = new CornerRadius(radius);
    }

    /// <summary>Corner radius of the whole window for a theme.</summary>
    public static double WindowRadius(string theme) => theme switch
    {
        Hacker => 14, Anime => 28, Tetris => 14, America => 18, Minecraft => 12, Standard => 20, Eagle => 22, Mystic => 20,
        Christmas => 22, Halloween => 18, Jungle => 24, _ => 16
    };

    /// <summary>How see-through the cards are, so animated backdrops can shine through.</summary>
    private static double SurfaceOpacity(string theme) => theme switch
    {
        Hacker => 0.85, Anime => 0.88, Tetris => 0.86, America => 0.88, Minecraft => 0.93, Eagle => 0.86, Mystic => 0.84,
        Christmas => 0.86, Halloween => 0.84, Jungle => 0.84, _ => 1
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
        (Standard, true) => new[]
        {
            "#0F1115", "#171A21", "#1E222B", "#2A2F3A", "#ECEEF2", "#A6ADBB", "#6F7686", "#232833", "#262B35",
            "#4F8BFF", "#FFFFFF", "#1D2C4D", "#3DD68C", "#143326", "#FFB547"
        },
        (Standard, false) => new[]
        {
            "#EEF1F6", "#FFFFFF", "#F3F5F9", "#DDE2EA", "#171A21", "#5A6272", "#8A92A2", "#E9EDF3", "#E3E7EE",
            "#2F6BFF", "#FFFFFF", "#E3ECFF", "#12A15F", "#DCF5E8", "#D9822B"
        },
        (Eagle, true) => new[]
        {
            "#0B0A14", "#E0151325", "#E61C1A30", "#2E2A4A", "#FFFFFF", "#C9C3E6", "#8C86AD", "#24203D", "#24203A",
            "#FF2E88", "#FFFFFF", "#C03A1530", "#32E0E8", "#0E3438", "#FFD43B"
        },
        (Eagle, false) => new[]
        {
            "#F6F4FF", "#E6FFFFFF", "#E6F4F1FF", "#E2DCF5", "#1A1530", "#5C5580", "#8E88AE", "#F0ECFF", "#ECE7FA",
            "#F01E7E", "#FFFFFF", "#FFE0EE", "#0FB8C9", "#D6F6F9", "#C99700"
        },
        (Mystic, true) => new[]
        {
            "#0E0B1A", "#D1181428", "#D8211B35", "#3B2F5A", "#F7EEF5", "#D3BFDB", "#9483A8", "#2C2445", "#2A2240",
            "#FF8FB8", "#2A0E1C", "#B5502846", "#9AE6C8", "#1E3A35", "#FFC37A"
        },
        (Mystic, false) => new[]
        {
            "#F4ECF2", "#D9FFFAFD", "#E0FBF1F6", "#E8D2DF", "#2A1B2E", "#6A4F6E", "#9C86A0", "#F6E2EC", "#EFDCE8",
            "#D9487E", "#FFFFFF", "#FADCE8", "#2F9C7A", "#D6F1E7", "#C77A1E"
        },
        (Christmas, true) => new[]
        {
            "#07150F", "#DB12281D", "#E3193425", "#3E7052", "#FFF6E7", "#D7C6B6", "#9A8C81", "#244633", "#1E3C2B",
            "#E43D52", "#FFFFFF", "#B33E1721", "#64D98A", "#163D28", "#F5C451"
        },
        (Christmas, false) => new[]
        {
            "#EAF3EC", "#E8FFFCF8", "#EAF6EF", "#B8D2C1", "#173324", "#486653", "#758C7D", "#DDEFE3", "#CEE5D6",
            "#C92F45", "#FFFFFF", "#F8DDE2", "#168447", "#D7EFE0", "#B67B08"
        },
        (Halloween, true) => new[]
        {
            "#100A18", "#D91D1228", "#E42A1838", "#5A366D", "#FFF0D8", "#D1B5D8", "#917A9D", "#382047", "#321C41",
            "#FF7A1A", "#21100A", "#B34A2418", "#9BE34D", "#253A18", "#FFC45C"
        },
        (Halloween, false) => new[]
        {
            "#F4E9D8", "#E8FFF9EF", "#EFE9D9F2", "#C9ACCF", "#302037", "#6B4E70", "#947E98", "#EADCF0", "#DDCDE4",
            "#D85B12", "#FFFFFF", "#F7D8C5", "#528B24", "#DDECCD", "#AD6700"
        },
        (Jungle, true) => new[]
        {
            "#06140F", "#D80D281C", "#E4143525", "#286346", "#ECFFF5", "#B6D8C6", "#789988", "#1B4831", "#173D2B",
            "#35D07F", "#062116", "#B31A5335", "#4BE08D", "#153E2A", "#F1B846"
        },
        (Jungle, false) => new[]
        {
            "#E8F5EC", "#E8FBFFF8", "#EBEDF8F0", "#A8CEB4", "#163624", "#456B53", "#78917F", "#D9EDDE", "#CBE5D3",
            "#168A4D", "#FFFFFF", "#CFEAD8", "#168A4D", "#D0ECD9", "#B97908"
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

    /// <summary>Window, surface, accent, text and border colour of a theme, for the little previews in the settings.</summary>
    public static (Color Window, Color Surface, Color Accent, Color Text, Color Border) PreviewColors(string theme, bool dark)
    {
        var p = Palette(theme, dark).Select(Parse).ToArray();
        return (p[0], Color.FromRgb(p[1].R, p[1].G, p[1].B), p[9], p[4], p[3]);
    }

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
