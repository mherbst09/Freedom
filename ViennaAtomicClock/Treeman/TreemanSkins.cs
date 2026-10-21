using System.Windows;
using System.Windows.Media;

namespace ViennaAtomicClock.Treeman;

public enum SkinRarity { Common, Rare, Epic, Legendary, Gold }

public enum HairStyle { None, Short, Swoop, Spiky, Long, Dreads, Buzz, Mohawk, Curly }

public enum Headwear { None, HardHat, Beanie, Cap, Crown, Santa, Helmet, VikingHelmet, Bandana, TopHat, Cowboy, Tricorne, Chef, SpaceHelmet, Kabuto }

public enum FacialHair { None, Beard, Mustache, BigMustache, Goatee, Stubble }

public enum Eyewear { None, Round, Shades }

public enum Outfit { Plaid, Plain, Suit, Hoodie, Stripes, Belted, Armor, Robe, Torn }

/// <summary>How a lumberjack looks. Everything is drawn from these values – no image files.</summary>
public sealed class SkinLook
{
    public Color Skin { get; init; } = Hex("#F2C195");
    public Outfit Outfit { get; init; } = Outfit.Plaid;
    public Color Shirt { get; init; } = Hex("#C83737");
    public Color Accent { get; init; } = Hex("#8E1F24");
    public Color? Tie { get; init; }
    public Color Pants { get; init; } = Hex("#2F4A7A");
    public Color Boots { get; init; } = Hex("#3B2A1E");
    public HairStyle Hair { get; init; } = HairStyle.None;
    public Color HairColor { get; init; } = Hex("#5A3A1E");
    public Headwear Hat { get; init; } = Headwear.HardHat;
    public Color HatColor { get; init; } = Hex("#F2B72C");
    public FacialHair Facial { get; init; } = FacialHair.Beard;
    public Color FacialColor { get; init; } = Hex("#8A4B24");
    public Eyewear Eyewear { get; init; }
    public bool BigChin { get; init; }
    public Color? Chain { get; init; }
    public Color Blade { get; init; } = Hex("#C9D2DC");
    public Color Handle { get; init; } = Hex("#8F6235");
    public Color? Aura { get; init; }

    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

/// <summary>One collectible skin. Names of real people and characters stay as they are; the others are translated.</summary>
public sealed class SkinDefinition
{
    private SkinPaint? _paint;

    public SkinDefinition(string id, SkinRarity rarity, SkinLook look, string? fixedName = null)
    {
        Id = id;
        Rarity = rarity;
        Look = look;
        FixedName = fixedName;
    }

    public string Id { get; }
    public SkinRarity Rarity { get; }
    public SkinLook Look { get; }
    /// <summary>Proper name (not translated), or null when the name comes from the string table.</summary>
    public string? FixedName { get; }
    public string NameKey => "tm.skin." + Id;
    public string Name => FixedName ?? Loc.T(NameKey);
    internal SkinPaint Paint => _paint ??= new SkinPaint(Look);
}

/// <summary>All 30 skins of the boxes.</summary>
public static class SkinCatalog
{
    public const string DefaultId = "classic";

    private static Color H(string hex) => SkinLook.Hex(hex);

    public static readonly IReadOnlyList<SkinDefinition> All = new SkinDefinition[]
    {
        // ── Common ──
        new("classic", SkinRarity.Common, new SkinLook()),
        new("forest", SkinRarity.Common, new SkinLook { Shirt = H("#3C8D4A"), Accent = H("#1F5A2C"), Pants = H("#4A3B2A"), Hat = Headwear.Beanie, HatColor = H("#2F4A7A"), FacialColor = H("#5A3A1E") }),
        new("bluecheck", SkinRarity.Common, new SkinLook { Shirt = H("#3A6FC4"), Accent = H("#1F3F7A"), Pants = H("#4A4A52"), Hat = Headwear.Cap, HatColor = H("#C83737"), Facial = FacialHair.Stubble, FacialColor = H("#5A3A1E") }),
        new("ranger", SkinRarity.Common, new SkinLook { Outfit = Outfit.Plain, Shirt = H("#6B7A3A"), Accent = H("#4E5A2A"), Pants = H("#4E4532"), Hat = Headwear.Cap, HatColor = H("#4E5E2A"), Facial = FacialHair.Mustache, FacialColor = H("#8A8A8A") }),
        new("builder", SkinRarity.Common, new SkinLook { Outfit = Outfit.Stripes, Shirt = H("#F07A1E"), Accent = H("#F2F2E8"), Pants = H("#3A4A6A"), Hat = Headwear.HardHat, HatColor = H("#F4F4F4"), Facial = FacialHair.Stubble, FacialColor = H("#4A3020") }),
        new("climber", SkinRarity.Common, new SkinLook { Outfit = Outfit.Plain, Shirt = H("#1F9A9A"), Accent = H("#147070"), Pants = H("#33363D"), Hat = Headwear.Helmet, HatColor = H("#F28C28"), Facial = FacialHair.Goatee, FacialColor = H("#6A4020") }),
        new("fisher", SkinRarity.Common, new SkinLook { Outfit = Outfit.Plain, Shirt = H("#F2C531"), Accent = H("#C89A1A"), Pants = H("#2E3A4A"), Hat = Headwear.Beanie, HatColor = H("#22305A"), FacialColor = H("#9A9A9A") }),
        new("baker", SkinRarity.Common, new SkinLook { Outfit = Outfit.Plain, Shirt = H("#F4F1EA"), Accent = H("#D8D2C4"), Pants = H("#5A5A5A"), Hat = Headwear.Chef, HatColor = H("#FFFFFF"), Facial = FacialHair.Mustache, FacialColor = H("#2A2A2A") }),
        new("punk", SkinRarity.Common, new SkinLook { Outfit = Outfit.Plain, Shirt = H("#222226"), Accent = H("#3A3A40"), Pants = H("#2A2A30"), Hat = Headwear.None, Hair = HairStyle.Mohawk, HairColor = H("#FF3FA4"), Facial = FacialHair.None, Eyewear = Eyewear.Shades, Chain = H("#C8CCD4") }),
        new("sailor", SkinRarity.Common, new SkinLook { Outfit = Outfit.Stripes, Shirt = H("#F4F4F4"), Accent = H("#1E2C55"), Pants = H("#1E2C55"), Hat = Headwear.Cap, HatColor = H("#1E2C55"), Facial = FacialHair.None, Hair = HairStyle.Short, HairColor = H("#3A2A1A") }),
        new("hiker", SkinRarity.Common, new SkinLook { Outfit = Outfit.Hoodie, Shirt = H("#4F8A3C"), Accent = H("#36652A"), Pants = H("#5A4A3A"), Hat = Headwear.None, Hair = HairStyle.Curly, HairColor = H("#C8642A"), Facial = FacialHair.None }),
        new("gardener", SkinRarity.Common, new SkinLook { Outfit = Outfit.Belted, Shirt = H("#4A6FA5"), Accent = H("#7A5232"), Pants = H("#4A6FA5"), Hat = Headwear.Cap, HatColor = H("#D8C08A"), Facial = FacialHair.Stubble, FacialColor = H("#6A5040") }),

        // ── Rare ──
        new("randy", SkinRarity.Rare, new SkinLook { Skin = H("#F5D0B0"), Outfit = Outfit.Plain, Shirt = H("#6E9BE0"), Accent = H("#4E78B8"), Pants = H("#2C3E66"), Boots = H("#2A2A2A"), Hat = Headwear.None, Hair = HairStyle.Short, HairColor = H("#5A3A22"), Facial = FacialHair.BigMustache, FacialColor = H("#1A1A1A") }, "Randy Marsh"),
        new("peter", SkinRarity.Rare, new SkinLook { Skin = H("#F6D3B3"), Outfit = Outfit.Plain, Shirt = H("#F4F4F4"), Accent = H("#D4D4D4"), Pants = H("#3F7A3A"), Boots = H("#1E1E1E"), Hat = Headwear.None, Hair = HairStyle.Short, HairColor = H("#4A2E1A"), Facial = FacialHair.None, Eyewear = Eyewear.Round, BigChin = true }, "Peter Griffin"),
        new("ninja", SkinRarity.Rare, new SkinLook { Outfit = Outfit.Belted, Shirt = H("#1E1E24"), Accent = H("#B02020"), Pants = H("#1E1E24"), Boots = H("#111114"), Hat = Headwear.Bandana, HatColor = H("#1E1E24"), Facial = FacialHair.None, Blade = H("#7A808A"), Handle = H("#2A2A2A") }),
        new("pirate", SkinRarity.Rare, new SkinLook { Outfit = Outfit.Stripes, Shirt = H("#F4F0E6"), Accent = H("#B82A2A"), Pants = H("#3B3B3B"), Hat = Headwear.Tricorne, HatColor = H("#1E1E1E"), FacialColor = H("#1A1A1A") }),
        new("cowboy", SkinRarity.Rare, new SkinLook { Shirt = H("#8A5A2B"), Accent = H("#5A3818"), Pants = H("#3A4A6A"), Hat = Headwear.Cowboy, HatColor = H("#7A4E26"), Facial = FacialHair.Mustache, FacialColor = H("#5A3A1E") }),
        new("zombie", SkinRarity.Rare, new SkinLook { Skin = H("#8DB36A"), Outfit = Outfit.Torn, Shirt = H("#5E3E7A"), Accent = H("#3E2856"), Pants = H("#3C4A5A"), Hat = Headwear.None, Hair = HairStyle.Spiky, HairColor = H("#3A3A2A"), Facial = FacialHair.None }),
        new("astronaut", SkinRarity.Rare, new SkinLook { Outfit = Outfit.Belted, Shirt = H("#EDEDED"), Accent = H("#9AA6B2"), Pants = H("#E0E0E0"), Boots = H("#7A8088"), Hat = Headwear.SpaceHelmet, HatColor = H("#BFE4FF"), Hair = HairStyle.Short, Facial = FacialHair.None }),
        new("viking", SkinRarity.Rare, new SkinLook { Outfit = Outfit.Armor, Shirt = H("#7A5232"), Accent = H("#4A3020"), Pants = H("#5A4A3A"), Hat = Headwear.VikingHelmet, HatColor = H("#9AA0A8"), FacialColor = H("#D2691E") }),

        // ── Epic ──
        new("obama", SkinRarity.Epic, new SkinLook { Skin = H("#8D5A3B"), Outfit = Outfit.Suit, Shirt = H("#3A3F47"), Accent = H("#2A2E35"), Tie = H("#2F5DA8"), Pants = H("#3A3F47"), Boots = H("#151515"), Hat = Headwear.None, Hair = HairStyle.Buzz, HairColor = H("#1A1A1A"), Facial = FacialHair.None }, "Barack Obama"),
        new("nettspend", SkinRarity.Epic, new SkinLook { Skin = H("#F2C9A8"), Outfit = Outfit.Hoodie, Shirt = H("#2A2A2E"), Accent = H("#E33B6A"), Pants = H("#6A6A72"), Boots = H("#E8E8E8"), Hat = Headwear.None, Hair = HairStyle.Buzz, HairColor = H("#F0E6C0"), Facial = FacialHair.None, Chain = H("#D8DCE4") }, "Nettspend"),
        new("osamason", SkinRarity.Epic, new SkinLook { Skin = H("#7A4A2E"), Outfit = Outfit.Hoodie, Shirt = H("#18181C"), Accent = H("#C02030"), Pants = H("#18181C"), Boots = H("#2A2A2A"), Hat = Headwear.None, Hair = HairStyle.Dreads, HairColor = H("#1A1410"), Facial = FacialHair.None, Eyewear = Eyewear.Shades, Chain = H("#E4AE39") }, "OsamaSon"),
        new("bleood", SkinRarity.Epic, new SkinLook { Skin = H("#F0C8A8"), Outfit = Outfit.Plain, Shirt = H("#1A1A1A"), Accent = H("#C8102E"), Pants = H("#7A1020"), Boots = H("#1A1A1A"), Hat = Headwear.None, Hair = HairStyle.Spiky, HairColor = H("#C8102E"), Facial = FacialHair.None, Chain = H("#D8DCE4") }, "bleood"),
        new("samurai", SkinRarity.Epic, new SkinLook { Outfit = Outfit.Armor, Shirt = H("#8A1E1E"), Accent = H("#2A1A1A"), Pants = H("#2A2A2A"), Boots = H("#1A1A1A"), Hat = Headwear.Kabuto, HatColor = H("#2A2A2A"), Facial = FacialHair.Mustache, FacialColor = H("#1A1A1A"), Blade = H("#EEF2F6") }),

        // ── Legendary ──
        new("trump", SkinRarity.Legendary, new SkinLook { Skin = H("#F2B183"), Outfit = Outfit.Suit, Shirt = H("#1F2A44"), Accent = H("#151D30"), Tie = H("#C8102E"), Pants = H("#1F2A44"), Boots = H("#111111"), Hat = Headwear.None, Hair = HairStyle.Swoop, HairColor = H("#F2C94C"), Facial = FacialHair.None }, "Donald Trump"),
        new("santa", SkinRarity.Legendary, new SkinLook { Outfit = Outfit.Belted, Shirt = H("#C8202A"), Accent = H("#1A1A1A"), Pants = H("#C8202A"), Boots = H("#1A1A1A"), Hat = Headwear.Santa, HatColor = H("#C8202A"), FacialColor = H("#F4F4F4") }),
        new("king", SkinRarity.Legendary, new SkinLook { Outfit = Outfit.Robe, Shirt = H("#5A2A8A"), Accent = H("#E4AE39"), Pants = H("#3A1A5A"), Hat = Headwear.Crown, HatColor = H("#F2C232"), FacialColor = H("#D8D8D8") }),

        // ── Gold ──
        new("golden", SkinRarity.Gold, new SkinLook { Shirt = H("#E4AE39"), Accent = H("#B8862A"), Pants = H("#6A5020"), HatColor = H("#FFD700"), Blade = H("#FFD54A"), Handle = H("#4A3418"), Aura = H("#FFD54A") }),
        new("diamond", SkinRarity.Gold, new SkinLook { Shirt = H("#6FE3F2"), Accent = H("#2AA9C8"), Pants = H("#1E4A6A"), HatColor = H("#BFF6FF"), FacialColor = H("#3A6A8A"), Blade = H("#E0FFFF"), Handle = H("#2A4A6A"), Aura = H("#7FF0FF") })
    };

    private static readonly Dictionary<string, SkinDefinition> ById = All.ToDictionary(s => s.Id);

    public static bool Exists(string? id) => id is not null && ById.ContainsKey(id);
    public static SkinDefinition Get(string? id) => id is not null && ById.TryGetValue(id, out var skin) ? skin : ById[DefaultId];
    public static IEnumerable<SkinDefinition> OfRarity(SkinRarity rarity) => All.Where(s => s.Rarity == rarity);

    /// <summary>The usual colour coding of item rarities (blue, purple, pink, red, gold).</summary>
    public static Color RarityColor(SkinRarity rarity) => rarity switch
    {
        SkinRarity.Common => H("#4B69FF"),
        SkinRarity.Rare => H("#8847FF"),
        SkinRarity.Epic => H("#D32CE6"),
        SkinRarity.Legendary => H("#EB4B4B"),
        _ => H("#E4AE39")
    };
}

/// <summary>Frozen brushes of one skin, created once.</summary>
internal sealed class SkinPaint
{
    public SkinPaint(SkinLook look)
    {
        Skin = Solid(look.Skin);
        Shirt = Solid(look.Shirt);
        Accent = Solid(look.Accent);
        Tie = look.Tie is { } tie ? Solid(tie) : null;
        Pants = Solid(look.Pants);
        Boots = Solid(look.Boots);
        Hair = Solid(look.HairColor);
        Hat = Solid(look.HatColor);
        Facial = Solid(look.FacialColor);
        Stubble = Solid(Color.FromArgb(110, look.FacialColor.R, look.FacialColor.G, look.FacialColor.B));
        Chain = look.Chain is { } chain ? Pen(chain, 3.5) : null;
        Blade = Solid(look.Blade);
        Handle = Solid(look.Handle);
        DreadPen = Pen(look.HairColor, 8);
        if (look.Aura is { } aura)
        {
            var glow = new RadialGradientBrush(Color.FromArgb(150, aura.R, aura.G, aura.B), Color.FromArgb(0, aura.R, aura.G, aura.B));
            glow.Freeze();
            Aura = glow;
        }
    }

    public Brush Skin, Shirt, Accent, Pants, Boots, Hair, Hat, Facial, Stubble, Blade, Handle;
    public Brush? Tie, Aura;
    public Pen? Chain;
    public Pen DreadPen;

    private static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(Color color, double thickness)
    {
        var pen = new Pen(Solid(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}

/// <summary>
/// Draws a lumberjack in any skin around (0,0) = between the feet, facing left (towards the trunk when he stands on
/// the right). Each painter owns its transforms, so one instance per drawing surface.
/// </summary>
internal sealed class SkinPainter
{
    private static readonly Pen Outline = MakePen(Color.FromRgb(0x3A, 0x26, 0x18), 3);
    private static readonly Pen Thin = MakePen(Color.FromRgb(0x3A, 0x26, 0x18), 2);
    private static readonly Pen EyePen = MakePen(Color.FromRgb(0x25, 0x25, 0x2A), 3);
    private static readonly Pen GlassPen = MakePen(Color.FromRgb(0x25, 0x25, 0x2A), 2.5);
    private static readonly Pen WhitePen = MakePen(Colors.White, 2);
    private static readonly Brush Eye = Solid(0x25, 0x25, 0x2A);
    private static readonly Brush White = Solid(0xFF, 0xFF, 0xFF);
    private static readonly Brush ShadesBrush = Solid(0x15, 0x15, 0x18);
    private static readonly Brush Fur = Solid(0xF6, 0xF4, 0xEE);
    private static readonly Brush Gold = Solid(0xE4, 0xAE, 0x39);
    private static readonly Brush Gem = Solid(0xD0, 0x20, 0x40);
    private static readonly Brush Glass = Solid(0xBF, 0xE4, 0xFF, 80);
    private static readonly Brush Bone = Solid(0xF0, 0xE6, 0xCC);
    private static readonly Brush DarkSpot = Solid(0x1A, 0x1A, 0x1A);

    private static readonly Geometry BeardGeometry = G("M -26,2 Q 0,52 26,2 Q 0,18 -26,2 Z");
    private static readonly Geometry StubbleGeometry = G("M -26,4 Q 0,34 26,4 Q 0,24 -26,4 Z");
    private static readonly Geometry MustacheGeometry = G("M -27,10 Q -16,3 -5,9 Q -16,15 -27,10 Z");
    private static readonly Geometry BigMustacheGeometry = G("M -32,13 Q -27,1 -15,4 Q -8,1 -1,6 Q 1,15 -8,14 Q -19,11 -32,13 Z");
    private static readonly Geometry GoateeGeometry = G("M -20,16 Q -12,32 -4,16 Q -12,21 -20,16 Z");
    private static readonly Geometry HairShort = G("M -28,-4 Q -30,-30 0,-31 Q 29,-30 28,-2 Q 22,-16 6,-18 Q -14,-20 -28,-4 Z");
    private static readonly Geometry HairSwoop = G("M -38,-12 Q -36,-38 0,-38 Q 32,-35 31,0 L 25,4 Q 26,-12 14,-17 Q -6,-24 -38,-12 Z");
    private static readonly Geometry HairSpiky = G("M -28,-6 L -32,-28 L -18,-24 L -14,-42 L -3,-28 L 8,-45 L 14,-27 L 27,-38 L 26,-18 L 33,-12 L 28,-2 Q 10,-18 -28,-6 Z");
    private static readonly Geometry HairBuzz = G("M -27,-8 Q -27,-29 0,-29 Q 27,-29 27,-4 Q 12,-17 -27,-8 Z");
    private static readonly Geometry HairMohawk = G("M -12,-25 L -9,-50 L -1,-31 L 5,-54 L 11,-31 L 19,-48 L 21,-23 Z");
    private static readonly Geometry HairCurly = Group(
        new EllipseGeometry(new Point(-22, -16), 11, 11), new EllipseGeometry(new Point(-10, -27), 12, 11), new EllipseGeometry(new Point(5, -30), 12, 11),
        new EllipseGeometry(new Point(18, -24), 12, 11), new EllipseGeometry(new Point(27, -11), 10, 11));
    private static readonly Geometry HairLongBack = G("M -24,-18 Q 0,-42 28,-18 L 36,42 L 8,46 Q 2,20 -8,8 Z");
    private static readonly Geometry Chin = new EllipseGeometry(new Point(-8, 24), 19, 12);
    private static readonly Geometry BladeGeometry = G("M 4,66 L 34,58 Q 42,76 34,94 L 4,86 Z");

    private static readonly Geometry BeanieTop = G("M -29,-8 Q -30,-40 0,-40 Q 30,-40 29,-8 Z");
    private static readonly Geometry CapDome = G("M -27,-10 Q -27,-35 0,-35 Q 27,-35 27,-10 Z");
    private static readonly Geometry CapVisor = G("M -26,-13 L -50,-9 Q -48,-3 -26,-6 Z");
    private static readonly Geometry CrownGeometry = G("M -24,-22 L -27,-50 L -13,-37 L 0,-54 L 13,-37 L 27,-50 L 24,-22 Z");
    private static readonly Geometry SantaGeometry = G("M -30,-14 Q -10,-60 22,-42 Q 38,-32 38,-6 Q 30,-28 26,-14 Z");
    private static readonly Geometry HelmetGeometry = G("M -31,-6 Q -31,-39 0,-39 Q 31,-39 31,-6 Z");
    private static readonly Geometry HornLeft = G("M -24,-20 Q -46,-26 -44,-52 Q -36,-34 -20,-30 Z");
    private static readonly Geometry HornRight = G("M 24,-20 Q 46,-26 44,-52 Q 36,-34 20,-30 Z");
    private static readonly Geometry MaskGeometry = G("M -29,2 Q -28,28 0,29 Q 28,28 29,2 Z");
    private static readonly Geometry BandTails = G("M 26,-16 L 46,-8 L 42,-1 L 26,-8 Z");
    private static readonly Geometry CowboyCrown = G("M -20,-24 Q -22,-50 0,-47 Q 22,-50 20,-24 Z");
    private static readonly Geometry CowboyBrim = G("M -48,-20 Q 0,-36 48,-20 Q 0,-10 -48,-20 Z");
    private static readonly Geometry TricorneGeometry = G("M -44,-18 Q -30,-52 0,-45 Q 30,-52 44,-18 Q 0,-30 -44,-18 Z");
    private static readonly Geometry ChefGeometry = G("M -22,-20 L -24,-40 Q -38,-58 -16,-61 Q -6,-76 9,-63 Q 31,-67 26,-44 L 22,-20 Z");
    private static readonly Geometry KabutoCrest = G("M -4,-34 L -32,-62 L -10,-36 Z M 4,-34 L 32,-62 L 10,-36 Z");
    private static readonly Geometry TornHem = G("M -34,74 L -26,82 L -18,74 L -10,84 L -2,75 L 6,84 L 14,74 L 22,83 L 34,74 L 34,66 L -34,66 Z");
    private static readonly Geometry SuitShirt = G("M -13,1 L 0,34 L 13,1 Z");
    private static readonly Geometry ShortTie = G("M -4,4 L 4,4 L 7,44 L 0,52 L -7,44 Z");
    private static readonly Geometry LongTie = G("M -4,4 L 4,4 L 7,60 L 0,68 L -7,60 Z");

    private readonly TranslateTransform _head = new();
    private readonly TranslateTransform _torso = new();
    private readonly RotateTransform _axeRotation = new();
    private readonly TranslateTransform _axePosition = new();
    private readonly RotateTransform _knockedOver = new(-80);

    /// <summary>Draws the figure. <paramref name="axeAngle"/> in degrees (-75 = resting on the shoulder).</summary>
    public void Draw(DrawingContext dc, SkinDefinition skin, double bob, double axeAngle, bool dead, double time)
    {
        var look = skin.Look;
        var p = skin.Paint;
        if (p.Aura is not null)
        {
            dc.PushOpacity(0.65 + 0.35 * Math.Sin(time * 2.6));
            dc.DrawEllipse(p.Aura, null, new Point(0, -90), 95, 120);
            dc.Pop();
        }
        if (dead) dc.PushTransform(_knockedOver);

        dc.DrawRoundedRectangle(p.Pants, Thin, new Rect(-24, -62, 20, 56), 5, 5);
        dc.DrawRoundedRectangle(p.Pants, Thin, new Rect(4, -62, 20, 56), 5, 5);
        dc.DrawRoundedRectangle(p.Boots, null, new Rect(-30, -12, 28, 14), 5, 5);
        dc.DrawRoundedRectangle(p.Boots, null, new Rect(2, -12, 28, 14), 5, 5);

        var top = -134 + bob;
        var headY = top - 26;
        _head.Y = headY;
        _torso.Y = top;

        // Things behind the body.
        dc.PushTransform(_head);
        if (look.Hair == HairStyle.Long) dc.DrawGeometry(p.Hair, Thin, HairLongBack);
        if (look.Hair == HairStyle.Dreads)
            for (var i = 0; i < 6; i++)
            {
                var x = -20 + i * 10;
                dc.DrawLine(p.DreadPen, new Point(x, -16), new Point(x + 6 + i, 34 + (i % 2) * 8));
            }
        if (look.Outfit == Outfit.Hoodie) dc.DrawRoundedRectangle(p.Accent, Thin, new Rect(-30, 18, 60, 20), 10, 10);
        dc.Pop();

        DrawTorso(dc, look, p, top);

        dc.PushTransform(_head);
        dc.DrawEllipse(p.Skin, Outline, new Point(0, 0), 28, 28);
        if (look.BigChin)
        {
            dc.DrawGeometry(p.Skin, Outline, Chin);
            dc.DrawLine(Thin, new Point(-12, 28), new Point(-10, 34));
        }
        switch (look.Facial)
        {
            case FacialHair.Beard: dc.DrawGeometry(p.Facial, Thin, BeardGeometry); break;
            case FacialHair.Stubble: dc.DrawGeometry(p.Stubble, null, StubbleGeometry); break;
            case FacialHair.Mustache: dc.DrawGeometry(p.Facial, null, MustacheGeometry); break;
            case FacialHair.BigMustache: dc.DrawGeometry(p.Facial, Thin, BigMustacheGeometry); break;
            case FacialHair.Goatee: dc.DrawGeometry(p.Facial, null, GoateeGeometry); break;
        }
        if (look.Hat == Headwear.Bandana) dc.DrawGeometry(p.Hat, Thin, MaskGeometry);
        DrawEyes(dc, look, dead);
        DrawHair(dc, look, p);
        DrawHat(dc, look, p);
        dc.Pop();

        // Axe: raised behind the head, then swung into the trunk.
        _axeRotation.Angle = axeAngle;
        _axeRotation.CenterX = -10;
        _axeRotation.CenterY = top + 24;
        dc.PushTransform(_axeRotation);
        _axePosition.X = -10;
        _axePosition.Y = top + 24;
        dc.PushTransform(_axePosition);
        dc.DrawRoundedRectangle(p.Handle, Thin, new Rect(-4, -6, 10, 92), 4, 4);
        dc.DrawGeometry(p.Blade, Thin, BladeGeometry);
        dc.DrawEllipse(p.Skin, Thin, new Point(0, 58), 9, 9);
        dc.Pop();
        dc.Pop();
        if (dead) dc.Pop();
    }

    private void DrawTorso(DrawingContext dc, SkinLook look, SkinPaint p, double top)
    {
        var body = new Rect(-34, top, 68, 80);
        dc.DrawRoundedRectangle(p.Shirt, Outline, body, 14, 14);
        dc.PushTransform(_torso); // local: y = 0 is the top of the body
        switch (look.Outfit)
        {
            case Outfit.Plaid:
                dc.DrawRectangle(p.Accent, null, new Rect(-34, 26, 68, 7));
                dc.DrawRectangle(p.Accent, null, new Rect(-34, 52, 68, 7));
                dc.DrawRectangle(p.Accent, null, new Rect(-6, 2, 7, 76));
                break;
            case Outfit.Plain:
                dc.DrawRectangle(p.Accent, null, new Rect(-2, 4, 3, 72));
                dc.DrawRoundedRectangle(p.Accent, null, new Rect(-24, 18, 14, 10), 3, 3);
                break;
            case Outfit.Suit:
                dc.DrawGeometry(White, Thin, SuitShirt);
                dc.DrawGeometry(p.Tie ?? p.Accent, Thin, look.Tie is { R: > 150 } ? LongTie : ShortTie); // a red tie hangs long
                dc.DrawLine(Thin, new Point(-13, 1), new Point(-6, 40));
                dc.DrawLine(Thin, new Point(13, 1), new Point(6, 40));
                dc.DrawEllipse(p.Accent, null, new Point(-10, 56), 2.5, 2.5);
                dc.DrawEllipse(p.Accent, null, new Point(-10, 68), 2.5, 2.5);
                break;
            case Outfit.Hoodie:
                dc.DrawRoundedRectangle(p.Accent, Thin, new Rect(-22, 46, 44, 22), 6, 6);
                dc.DrawLine(Thin, new Point(-8, 4), new Point(-9, 26));
                dc.DrawLine(Thin, new Point(6, 4), new Point(7, 26));
                break;
            case Outfit.Stripes:
                for (var y = 10; y < 78; y += 16) dc.DrawRectangle(p.Accent, null, new Rect(-33, y, 66, 7));
                break;
            case Outfit.Belted:
                dc.DrawRectangle(p.Accent, null, new Rect(-34, 52, 68, 10));
                dc.DrawRoundedRectangle(Gold, Thin, new Rect(-10, 50, 18, 14), 2, 2);
                if (look.Hat == Headwear.Santa)
                {
                    dc.DrawRectangle(Fur, null, new Rect(-5, 2, 10, 48));
                    dc.DrawRoundedRectangle(Fur, null, new Rect(-34, 70, 68, 10), 5, 5);
                }
                break;
            case Outfit.Armor:
                dc.DrawRectangle(p.Accent, null, new Rect(-34, 20, 68, 6));
                dc.DrawRectangle(p.Accent, null, new Rect(-34, 42, 68, 6));
                dc.DrawRectangle(p.Accent, null, new Rect(-34, 64, 68, 6));
                for (var x = -24; x <= 24; x += 16) dc.DrawEllipse(Gold, null, new Point(x, 10), 2.5, 2.5);
                break;
            case Outfit.Robe:
                dc.DrawRoundedRectangle(Fur, Thin, new Rect(-34, 0, 68, 16), 8, 8);
                for (var x = -24; x <= 24; x += 12) dc.DrawEllipse(DarkSpot, null, new Point(x, 8), 2, 3);
                dc.DrawRectangle(p.Accent, null, new Rect(-4, 16, 8, 62));
                break;
            case Outfit.Torn:
                dc.DrawGeometry(p.Pants, null, TornHem);
                dc.DrawRectangle(p.Accent, null, new Rect(-24, 20, 14, 12));
                dc.DrawRectangle(p.Accent, null, new Rect(8, 40, 16, 10));
                break;
        }
        if (look.Chain is not null)
        {
            dc.DrawGeometry(null, p.Chain, ChainGeometry);
            dc.DrawEllipse(p.Chain!.Brush, null, new Point(-2, 34), 6, 6);
        }
        dc.Pop();
    }

    private static readonly Geometry ChainGeometry = G("M -20,2 Q -6,34 -2,34 Q 8,34 20,2");

    private static void DrawEyes(DrawingContext dc, SkinLook look, bool dead)
    {
        if (dead)
        {
            foreach (var ex in new[] { -16.0, -2.0 })
            {
                dc.DrawLine(EyePen, new Point(ex - 4, -6), new Point(ex + 4, 2));
                dc.DrawLine(EyePen, new Point(ex + 4, -6), new Point(ex - 4, 2));
            }
            return;
        }
        if (look.Eyewear == Eyewear.Shades)
        {
            dc.DrawRoundedRectangle(ShadesBrush, null, new Rect(-26, -9, 30, 11), 4, 4);
            dc.DrawLine(Thin, new Point(4, -6), new Point(22, -8));
            return;
        }
        dc.DrawEllipse(Eye, null, new Point(-16, -3), 3.5, 4);
        dc.DrawEllipse(Eye, null, new Point(-2, -3), 3.5, 4);
        if (look.Eyewear == Eyewear.Round)
        {
            dc.DrawEllipse(null, GlassPen, new Point(-17, -3), 8, 8);
            dc.DrawEllipse(null, GlassPen, new Point(1, -3), 8, 8);
            dc.DrawLine(GlassPen, new Point(9, -4), new Point(24, -8));
        }
    }

    private static void DrawHair(DrawingContext dc, SkinLook look, SkinPaint p)
    {
        var geometry = look.Hair switch
        {
            HairStyle.Short or HairStyle.Long or HairStyle.Dreads => HairShort,
            HairStyle.Swoop => HairSwoop,
            HairStyle.Spiky => HairSpiky,
            HairStyle.Buzz => HairBuzz,
            HairStyle.Mohawk => HairMohawk,
            HairStyle.Curly => HairCurly,
            _ => null
        };
        // Hats cover the hair; only helmets with a visor-less dome show nothing of it.
        if (geometry is null || look.Hat is Headwear.HardHat or Headwear.Beanie or Headwear.Helmet or Headwear.VikingHelmet or Headwear.Kabuto) return;
        dc.DrawGeometry(p.Hair, Thin, geometry);
    }

    private static void DrawHat(DrawingContext dc, SkinLook look, SkinPaint p)
    {
        switch (look.Hat)
        {
            case Headwear.HardHat:
                dc.DrawRoundedRectangle(p.Hat, Outline, new Rect(-30, -34, 60, 22), 10, 10);
                dc.DrawRectangle(p.Hat, Outline, new Rect(-40, -16, 80, 8));
                break;
            case Headwear.Beanie:
                dc.DrawGeometry(p.Hat, Outline, BeanieTop);
                dc.DrawRoundedRectangle(p.Hat, Outline, new Rect(-31, -15, 62, 10), 4, 4);
                dc.DrawEllipse(p.Hat, Thin, new Point(0, -42), 7, 7);
                break;
            case Headwear.Cap:
                dc.DrawGeometry(p.Hat, Outline, CapDome);
                dc.DrawGeometry(p.Hat, Outline, CapVisor);
                break;
            case Headwear.Crown:
                dc.DrawGeometry(p.Hat, Outline, CrownGeometry);
                dc.DrawEllipse(Gem, null, new Point(0, -32), 4, 4);
                dc.DrawEllipse(Gem, null, new Point(-15, -28), 3, 3);
                dc.DrawEllipse(Gem, null, new Point(15, -28), 3, 3);
                break;
            case Headwear.Santa:
                dc.DrawGeometry(p.Hat, Outline, SantaGeometry);
                dc.DrawRoundedRectangle(Fur, Thin, new Rect(-33, -20, 66, 11), 5, 5);
                dc.DrawEllipse(Fur, Thin, new Point(38, -6), 7, 7);
                break;
            case Headwear.Helmet:
                dc.DrawGeometry(p.Hat, Outline, HelmetGeometry);
                dc.DrawLine(Thin, new Point(-26, -8), new Point(-18, 16));
                break;
            case Headwear.VikingHelmet:
                dc.DrawGeometry(Bone, Thin, HornLeft);
                dc.DrawGeometry(Bone, Thin, HornRight);
                dc.DrawGeometry(p.Hat, Outline, HelmetGeometry);
                dc.DrawRectangle(Gold, Thin, new Rect(-31, -12, 62, 6));
                break;
            case Headwear.Bandana:
                dc.DrawGeometry(p.Hat, Thin, CapDome);
                dc.DrawRectangle(p.Hat, null, new Rect(-28, -14, 56, 6));
                dc.DrawGeometry(p.Hat, Thin, BandTails);
                break;
            case Headwear.TopHat:
                dc.DrawRectangle(p.Hat, Outline, new Rect(-22, -68, 44, 46));
                dc.DrawRectangle(Gem, null, new Rect(-22, -34, 44, 7));
                dc.DrawRectangle(p.Hat, Outline, new Rect(-34, -26, 68, 7));
                break;
            case Headwear.Cowboy:
                dc.DrawGeometry(p.Hat, Outline, CowboyCrown);
                dc.DrawGeometry(p.Hat, Outline, CowboyBrim);
                break;
            case Headwear.Tricorne:
                dc.DrawGeometry(p.Hat, Outline, TricorneGeometry);
                dc.DrawEllipse(White, null, new Point(0, -34), 5, 5);
                break;
            case Headwear.Chef:
                dc.DrawGeometry(p.Hat, Outline, ChefGeometry);
                dc.DrawRectangle(p.Hat, Outline, new Rect(-24, -26, 48, 10));
                break;
            case Headwear.SpaceHelmet:
                dc.DrawEllipse(Glass, WhitePen, new Point(0, 0), 40, 40);
                dc.DrawEllipse(null, Outline, new Point(0, 0), 42, 42);
                dc.DrawLine(WhitePen, new Point(-26, -22), new Point(-16, -30));
                break;
            case Headwear.Kabuto:
                dc.DrawGeometry(Gold, Thin, KabutoCrest);
                dc.DrawGeometry(p.Hat, Outline, HelmetGeometry);
                dc.DrawRectangle(p.Hat, Outline, new Rect(-34, -10, 68, 8));
                break;
        }
    }

    private static Geometry G(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Group(params Geometry[] parts)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var part in parts) group.Children.Add(part);
        var flat = Geometry.Combine(group, Geometry.Empty, GeometryCombineMode.Union, null);
        flat.Freeze();
        return flat;
    }

    private static Brush Solid(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }
}

/// <summary>Small standing picture of a skin (box reel, collection). Skins not found yet show a question mark.</summary>
internal sealed class SkinPreview : FrameworkElement
{
    private static readonly Brush QuestionBrush = Freeze(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)));
    private readonly SkinPainter _painter = new();
    private readonly MatrixTransform _fit = new();
    private readonly SkinDefinition _skin;
    private readonly bool _locked;

    public SkinPreview(SkinDefinition skin, bool locked = false)
    {
        _skin = skin;
        _locked = locked;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        // The figure spans about x -60..60 and y -215..5 (hats included).
        if (_locked)
        {
            var question = new FormattedText("?", Loc.Culture, FlowDirection.LeftToRight, new Typeface("Segoe UI Black"), Math.Min(ActualWidth, ActualHeight) * 0.5, QuestionBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(question, new Point((ActualWidth - question.Width) / 2, (ActualHeight - question.Height) / 2));
            return;
        }
        var scale = Math.Min(ActualWidth / 130, ActualHeight / 225);
        _fit.Matrix = new Matrix(scale, 0, 0, scale, ActualWidth / 2, ActualHeight - 8 * scale);
        dc.PushTransform(_fit);
        _painter.Draw(dc, _skin, 0, -75, false, 0);
        dc.Pop();
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
