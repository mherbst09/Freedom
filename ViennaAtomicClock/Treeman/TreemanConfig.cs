using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViennaAtomicClock.Treeman;

/// <summary>Where a branch grows, or which side of the trunk the lumberjack stands on.</summary>
public enum Side { None, Left, Right }

public enum GameState { MainMenu, Countdown, Playing, Paused, GameOver }

public enum GameOverReason { None, Branch, TimeUp }

public static class SideExtensions
{
    public static Side Opposite(this Side side) => side switch { Side.Left => Side.Right, Side.Right => Side.Left, _ => Side.None };
}

/// <summary>Time energy: how fast it drains, how much a chop gives back and how the drain grows with the score.</summary>
public sealed class TimeSettings
{
    [Description("Maximum time energy (the full bar).")]
    public double MaxEnergy { get; set; } = 100;

    [Description("Energy lost per second at the start of a round.")]
    public double StartDrainPerSecond { get; set; } = 8;

    [Description("Energy given back by every successful chop (never above the maximum).")]
    public double RefillPerChop { get; set; } = 4;

    [Description("Extra drain per second for every difficulty level.")]
    public double DrainIncreasePerLevel { get; set; } = 1.6;

    [Description("Points needed for the next difficulty level.")]
    public int PointsPerLevel { get; set; } = 25;

    [Description("Upper limit for the drain, so the game never becomes impossible.")]
    public double MaxDrainPerSecond { get; set; } = 26;

    [Description("Seconds over which a new level's drain is blended in (no abrupt jumps).")]
    public double LevelBlendSeconds { get; set; } = 1.5;

    [Description("Below this share of the maximum the bar warns (colour and pulse).")]
    public double LowWarningShare { get; set; } = 0.25;
}

/// <summary>Rules for a fair, readable tree.</summary>
public sealed class GenerationRules
{
    [Description("Segments without branches at the start of every round.")]
    public int SafeStartSegments { get; set; } = 4;

    [Description("Chance that a segment gets a branch at the start.")]
    public double StartBranchChance { get; set; } = 0.42;

    [Description("Extra branch chance per difficulty level.")]
    public double BranchChanceIncreasePerLevel { get; set; } = 0.035;

    [Description("Highest branch chance.")]
    public double MaxBranchChance { get; set; } = 0.7;

    [Description("Most branches directly on top of each other on the same side.")]
    public int MaxSameSideInRow { get; set; } = 3;

    [Description("Forbid a left branch directly next to a right one (there is always a free segment in between).")]
    public bool RequireGapBetweenSides { get; set; } = true;

    [Description("Most empty segments in a row (keeps the game busy).")]
    public int MaxEmptyInRow { get; set; } = 3;
}

/// <summary>Player, input and animation timing.</summary>
public sealed class PlayerSettings
{
    [Description("Minimum time between two chops in seconds; faster presses are buffered, not lost.")]
    public double InputCooldownSeconds { get; set; } = 0.08;

    [Description("How many early presses are remembered.")]
    public int InputBufferSize { get; set; } = 3;

    [Description("Length of the axe swing animation in seconds.")]
    public double SwingSeconds { get; set; } = 0.11;

    [Description("Countdown before a round in seconds (0 = none).")]
    public double CountdownSeconds { get; set; } = 3;

    [Description("Tree segments that are visible above the stump.")]
    public int VisibleSegments { get; set; } = 9;

    [Description("Every multiple of this score makes the score display pop.")]
    public int MilestoneEvery { get; set; } = 25;
}

/// <summary>Price and drop chances of the skin boxes.</summary>
public sealed class LootSettings
{
    [Description("Coins one box costs.")]
    public int BoxPrice { get; set; } = 1000;

    [Description("Relative drop weights per rarity: Common, Rare, Epic, Legendary, Gold.")]
    public double[] RarityWeights { get; set; } = { 55, 28, 12, 4, 1 };

    [Description("Coins given back for a skin you already own, per rarity: Common, Rare, Epic, Legendary, Gold.")]
    public int[] DuplicateRefund { get; set; } = { 100, 200, 400, 700, 1000 };

    [Description("Seconds the box reel spins before it stops.")]
    public double SpinSeconds { get; set; } = 6;
}

/// <summary>
/// All balancing values in one place (the counterpart of Unity ScriptableObjects). Stored as editable JSON in
/// %AppData%\ViennaAtomicClock\treeman-config.json; missing or broken values fall back to the defaults.
/// </summary>
public sealed class TreemanConfig
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public TimeSettings Time { get; set; } = new();
    public GenerationRules Generation { get; set; } = new();
    public PlayerSettings Player { get; set; } = new();
    public LootSettings Loot { get; set; } = new();

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock", "treeman-config.json");

    public static TreemanConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        TreemanConfig config;
        try
        {
            config = File.Exists(path) ? JsonSerializer.Deserialize<TreemanConfig>(File.ReadAllText(path), JsonOptions) ?? new TreemanConfig() : new TreemanConfig();
        }
        catch
        {
            config = new TreemanConfig();
        }
        config.Normalize();
        if (!File.Exists(path))
        {
            // Written once so the values can be balanced without touching the code.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
            }
            catch
            {
                // Read-only profile: the defaults still work.
            }
        }
        return config;
    }

    /// <summary>Clamps every value into a sensible range.</summary>
    public TreemanConfig Normalize()
    {
        Time ??= new TimeSettings();
        Generation ??= new GenerationRules();
        Player ??= new PlayerSettings();
        static double C(double v, double min, double max, double fallback) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
        Time.MaxEnergy = C(Time.MaxEnergy, 10, 1000, 100);
        Time.StartDrainPerSecond = C(Time.StartDrainPerSecond, 0, 500, 8);
        Time.RefillPerChop = C(Time.RefillPerChop, 0, Time.MaxEnergy, 4);
        Time.DrainIncreasePerLevel = C(Time.DrainIncreasePerLevel, 0, 100, 1.6);
        Time.PointsPerLevel = Math.Clamp(Time.PointsPerLevel, 1, 10_000);
        Time.MaxDrainPerSecond = C(Time.MaxDrainPerSecond, Time.StartDrainPerSecond, 1000, 26);
        Time.LevelBlendSeconds = C(Time.LevelBlendSeconds, 0, 30, 1.5);
        Time.LowWarningShare = C(Time.LowWarningShare, 0, 1, 0.25);
        Generation.SafeStartSegments = Math.Clamp(Generation.SafeStartSegments, 1, 50);
        Generation.StartBranchChance = C(Generation.StartBranchChance, 0, 1, 0.42);
        Generation.BranchChanceIncreasePerLevel = C(Generation.BranchChanceIncreasePerLevel, 0, 1, 0.035);
        Generation.MaxBranchChance = C(Generation.MaxBranchChance, Generation.StartBranchChance, 0.95, 0.7);
        Generation.MaxSameSideInRow = Math.Clamp(Generation.MaxSameSideInRow, 1, 20);
        Generation.MaxEmptyInRow = Math.Clamp(Generation.MaxEmptyInRow, 1, 50);
        Player.InputCooldownSeconds = C(Player.InputCooldownSeconds, 0, 1, 0.08);
        Player.InputBufferSize = Math.Clamp(Player.InputBufferSize, 0, 10);
        Player.SwingSeconds = C(Player.SwingSeconds, 0.02, 1, 0.11);
        Player.CountdownSeconds = C(Player.CountdownSeconds, 0, 10, 3);
        Player.VisibleSegments = Math.Clamp(Player.VisibleSegments, 4, 20);
        Player.MilestoneEvery = Math.Clamp(Player.MilestoneEvery, 1, 10_000);
        Loot ??= new LootSettings();
        Loot.BoxPrice = Math.Clamp(Loot.BoxPrice, 0, 1_000_000);
        var defaults = new LootSettings();
        var weights = Loot.RarityWeights is { Length: 5 } w && w.All(x => double.IsFinite(x) && x >= 0) && w.Sum() > 0 ? w : defaults.RarityWeights;
        Loot.RarityWeights = weights.ToArray();
        Loot.DuplicateRefund = Loot.DuplicateRefund is { Length: 5 } r ? r.Select(x => Math.Clamp(x, 0, 1_000_000)).ToArray() : defaults.DuplicateRefund;
        Loot.SpinSeconds = C(Loot.SpinSeconds, 0.5, 20, 6);
        return this;
    }
}

/// <summary>Small, fast, seedable random generator (SplitMix64) – identical sequences for identical seeds on every PC.</summary>
public sealed class SeededRandom
{
    private ulong _state;

    public SeededRandom(ulong seed) => _state = seed;

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform value in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
}
