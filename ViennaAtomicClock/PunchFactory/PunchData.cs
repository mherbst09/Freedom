using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace ViennaAtomicClock.PunchFactory;

public enum ProducerKind { Human, Machine, Venue }

/// <summary>What an upgrade level changes.</summary>
public enum Effect
{
    ClickFlat, ClickMult, ClickPpsShare, CritChance, CritMult, ComboDecay, MaxCombo, AbilityPower, AbilityCooldown,
    PowerPunch, GoldenRewards, FuryBonus, ProducerMult, KindMult, AllMult, TrainerSynergy, TrainingSpeed, OfflineHours,
    TypeSynergy, CostReduction, EventFrequency, StartCash, ComboFloor
}

public sealed class ProducerDef
{
    public string Id { get; set; } = "";
    public ProducerKind Kind { get; set; }
    public double BaseCost { get; set; }
    public double CostGrowth { get; set; } = 1.15;
    /// <summary>Punch power per second of one unit.</summary>
    public double BaseRate { get; set; }
}

public sealed class UpgradeDef
{
    public string Id { get; set; } = "";
    /// <summary>"click" or "auto".</summary>
    public string Group { get; set; } = "click";
    public Effect Effect { get; set; }
    public double Value { get; set; }
    /// <summary>Producer id or kind name the effect applies to (if any).</summary>
    public string? Target { get; set; }
    public double BaseCost { get; set; }
    public double CostGrowth { get; set; } = 10;
    public int MaxLevel { get; set; } = 5;
    /// <summary>Shown once this producer is owned <see cref="UnlockCount"/> times …</summary>
    public string? UnlockProducer { get; set; }
    public int UnlockCount { get; set; }
    /// <summary>… or once the career earned this much.</summary>
    public double UnlockEarned { get; set; }
}

public sealed class AbilityDef
{
    public string Id { get; set; } = "";
    public double Cooldown { get; set; }
    public double Duration { get; set; }
    public double Power { get; set; }
    public double UnlockEarned { get; set; }
}

public sealed class EventDef
{
    public string Id { get; set; } = "";
    public double Weight { get; set; } = 1;
    /// <summary>Seconds the target stays catchable.</summary>
    public double Lifetime { get; set; } = 8;
    /// <summary>Clicks needed to catch it (challenger needs several).</summary>
    public int Hits { get; set; } = 1;
}

public sealed class EnvironmentDef
{
    public string Id { get; set; } = "";
    /// <summary>Lifetime punch power that unlocks it.</summary>
    public double UnlockEarned { get; set; }
    /// <summary>Production bonus while it is the active studio (0.1 = +10 %).</summary>
    public double Bonus { get; set; }
}

public sealed class PrestigeUpgradeDef
{
    public string Id { get; set; } = "";
    public Effect Effect { get; set; }
    public double Value { get; set; }
    public double Cost { get; set; }
    public double CostGrowth { get; set; } = 2;
    public int MaxLevel { get; set; } = 10;
    /// <summary>Cosmetic unlocked by the first level (optional).</summary>
    public string? Cosmetic { get; set; }
}

/// <summary>All balancing values (the counterpart of ScriptableObjects), editable as JSON.</summary>
public sealed class BalanceConfig
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public double ComboWindow { get; set; } = 0.9;
    public double ComboGrace { get; set; } = 1.2;
    public double ComboDecayPerSecond { get; set; } = 8;
    public int BaseMaxCombo { get; set; } = 300;
    public double BaseCritChance { get; set; } = 0.02;
    public double BaseCritMult { get; set; } = 5;
    public double FuryDuration { get; set; } = 10;
    public double FuryClickMult { get; set; } = 5;
    public double FuryProductionMult { get; set; } = 2;
    public double PrestigeRequirement { get; set; } = 1e9;
    public double FameFactor { get; set; } = 5;
    public double FamePower { get; set; } = 1.0 / 3;
    public double FameBonusPerPoint { get; set; } = 0.02;
    public double MasteryBonusPerPoint { get; set; } = 0.01;
    public double BaseOfflineHours { get; set; } = 8;
    public double OfflineEfficiency { get; set; } = 1;
    public double EventMinSeconds { get; set; } = 75;
    public double EventMaxSeconds { get; set; } = 180;
    public double AutosaveSeconds { get; set; } = 30;
    public int[] Milestones { get; set; } = { 10, 25, 50, 100, 250 };
    public double MilestoneMult { get; set; } = 2;

    public List<ProducerDef> Producers { get; set; } = new();
    public List<UpgradeDef> Upgrades { get; set; } = new();
    public List<AbilityDef> Abilities { get; set; } = new();
    public List<EventDef> Events { get; set; } = new();
    public List<EnvironmentDef> Environments { get; set; } = new();
    public List<PrestigeUpgradeDef> PrestigeUpgrades { get; set; } = new();

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock", "punchfactory-balance.json");

    public static BalanceConfig Default()
    {
        var c = new BalanceConfig();
        void P(string id, ProducerKind kind, double cost, double rate) => c.Producers.Add(new ProducerDef { Id = id, Kind = kind, BaseCost = cost, BaseRate = rate });
        P("rookie", ProducerKind.Human, 15, 0.1);
        P("sparring", ProducerKind.Human, 100, 1);
        P("trainer", ProducerKind.Human, 1_100, 8);
        P("doubleend", ProducerKind.Machine, 12_000, 47);
        P("machine", ProducerKind.Machine, 130_000, 260);
        P("robot", ProducerKind.Machine, 1.4e6, 1_400);
        P("team", ProducerKind.Human, 2e7, 7_800);
        P("studio", ProducerKind.Venue, 3.3e8, 44_000);
        P("arena", ProducerKind.Venue, 5.1e9, 260_000);
        P("holo", ProducerKind.Venue, 7.5e10, 1.6e6);
        P("orbital", ProducerKind.Venue, 1e12, 1e7);

        void U(string id, string group, Effect effect, double value, double cost, double growth, int max, string? target = null, string? unlock = null, int count = 0, double earned = 0) =>
            c.Upgrades.Add(new UpgradeDef { Id = id, Group = group, Effect = effect, Value = value, BaseCost = cost, CostGrowth = growth, MaxLevel = max, Target = target, UnlockProducer = unlock, UnlockCount = count, UnlockEarned = earned });
        // Click upgrades.
        U("wraps", "click", Effect.ClickFlat, 1, 50, 3.5, 10);
        U("gloves", "click", Effect.ClickMult, 1.5, 500, 12, 8, earned: 200);
        U("precision", "click", Effect.CritChance, 0.01, 1_000, 6, 10, earned: 400);
        U("combos", "click", Effect.ComboDecay, 0.1, 2_000, 7, 5, earned: 800);
        U("strength", "click", Effect.ClickPpsShare, 0.01, 10_000, 10, 10, earned: 4_000);
        U("footwork", "click", Effect.MaxCombo, 50, 25_000, 9, 4, earned: 10_000);
        U("adrenaline", "click", Effect.AbilityPower, 0.25, 100_000, 10, 5, earned: 40_000);
        U("crits", "click", Effect.CritMult, 1, 250_000, 8, 10, earned: 100_000);
        U("explosive", "click", Effect.PowerPunch, 1, 1e6, 12, 5, earned: 400_000);
        U("mastery", "click", Effect.ClickMult, 2, 1e7, 25, 5, earned: 4e6);
        U("golden", "click", Effect.GoldenRewards, 0.5, 1e9, 20, 5, earned: 4e8);
        U("energy", "click", Effect.FuryBonus, 2, 5e10, 30, 5, earned: 2e10);
        // Production upgrades.
        U("faster", "auto", Effect.KindMult, 1.5, 1_000, 8, 10, "Human", "rookie", 5);
        U("plans", "auto", Effect.ProducerMult, 2, 5_000, 15, 5, "rookie,sparring", "sparring", 5);
        U("equipment", "auto", Effect.KindMult, 1.5, 50_000, 10, 10, "Machine", "doubleend", 1);
        U("coach", "auto", Effect.AllMult, 1.25, 200_000, 12, 10, earned: 80_000);
        U("video", "auto", Effect.TrainerSynergy, 0.01, 2e6, 12, 5, null, "trainer", 10);
        U("science", "auto", Effect.TrainingSpeed, 0.1, 2e7, 10, 10, earned: 8e6);
        U("maintenance", "auto", Effect.ProducerMult, 2, 2e8, 15, 5, "machine,robot", "robot", 1);
        U("regen", "auto", Effect.OfflineHours, 2, 1e9, 10, 8, earned: 4e8);
        U("synergy", "auto", Effect.TypeSynergy, 0.02, 1e10, 12, 5, earned: 4e9);
        U("global", "auto", Effect.AllMult, 2, 1e12, 50, 10, earned: 4e11);

        void A(string id, double cooldown, double duration, double power, double unlock) =>
            c.Abilities.Add(new AbilityDef { Id = id, Cooldown = cooldown, Duration = duration, Power = power, UnlockEarned = unlock });
        A("power", 30, 0, 50, 100);          // one hit worth 50 clicks
        A("flurry", 45, 2, 20, 1_000);       // 20 automatic hits in 2 s
        A("adrenalin", 120, 20, 2, 10_000);  // production ×2
        A("focus", 90, 15, 0.5, 100_000);    // +50 % crit chance
        A("coach", 150, 15, 3, 1e6);         // boxers ×3
        A("golden", 180, 15, 0.05, 1e7);     // each click + 5 % of PPS × 60

        void E(string id, double weight, double lifetime, int hits = 1) => c.Events.Add(new EventDef { Id = id, Weight = weight, Lifetime = lifetime, Hits = hits });
        E("glove", 3, 8);
        E("drone", 2, 6);
        E("challenger", 1.5, 9, 12);
        E("bonusbag", 2, 8);
        E("motivation", 2, 7);
        E("sponsor", 1.5, 8);

        void V(string id, double unlock, double bonus) => c.Environments.Add(new EnvironmentDef { Id = id, UnlockEarned = unlock, Bonus = bonus });
        V("garage", 0, 0);
        V("smallgym", 5_000, 0.05);
        V("fitness", 500_000, 0.1);
        V("localarena", 5e7, 0.15);
        V("worldarena", 5e9, 0.25);
        V("lab", 5e11, 0.35);
        V("megaarena", 5e13, 0.5);
        V("orbit", 5e15, 0.75);

        void F(string id, Effect effect, double value, double cost, double growth, int max, string? cosmetic = null) =>
            c.PrestigeUpgrades.Add(new PrestigeUpgradeDef { Id = id, Effect = effect, Value = value, Cost = cost, CostGrowth = growth, MaxLevel = max, Cosmetic = cosmetic });
        F("f_click", Effect.ClickMult, 1.5, 1, 2, 15);
        F("f_passive", Effect.AllMult, 1.25, 1, 2, 15);
        F("f_offline", Effect.OfflineHours, 2, 2, 2, 8);
        F("f_crit", Effect.CritChance, 0.01, 2, 2, 10);
        F("f_combo", Effect.ComboDecay, 0.1, 2, 2, 5);
        F("f_cooldown", Effect.AbilityCooldown, 0.05, 3, 2, 8);
        F("f_cheaper", Effect.CostReduction, 0.03, 3, 2.5, 10);
        F("f_start", Effect.StartCash, 1000, 5, 3, 5);
        F("f_floor", Effect.ComboFloor, 10, 10, 3, 3);
        F("f_chrome", Effect.ClickFlat, 0, 15, 1, 1, "glove_chrome");
        F("f_holobag", Effect.ClickFlat, 0, 25, 1, 1, "bag_holo");
        F("f_events", Effect.EventFrequency, 0.15, 8, 2.5, 5);
        return c;
    }

    /// <summary>Loads the JSON (writes the defaults once); broken or missing values fall back to the defaults.</summary>
    public static BalanceConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        BalanceConfig? config = null;
        try
        {
            if (File.Exists(path)) config = JsonSerializer.Deserialize<BalanceConfig>(File.ReadAllText(path), Json);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            config = null;
        }
        if (config is null)
        {
            config = Default();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path)) File.WriteAllText(path, JsonSerializer.Serialize(config, Json));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Read-only profile: the defaults work.
            }
        }
        return config.Normalize();
    }

    /// <summary>Repairs values from an edited file: lists that are empty use the defaults, numbers are clamped.</summary>
    public BalanceConfig Normalize()
    {
        var d = Default();
        static double C(double v, double min, double max, double fallback) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
        if (Producers is not { Count: > 0 }) Producers = d.Producers;
        if (Upgrades is not { Count: > 0 }) Upgrades = d.Upgrades;
        if (Abilities is not { Count: > 0 }) Abilities = d.Abilities;
        if (Events is not { Count: > 0 }) Events = d.Events;
        if (Environments is not { Count: > 0 }) Environments = d.Environments;
        if (PrestigeUpgrades is not { Count: > 0 }) PrestigeUpgrades = d.PrestigeUpgrades;
        ComboWindow = C(ComboWindow, 0.1, 5, d.ComboWindow);
        ComboGrace = C(ComboGrace, 0, 10, d.ComboGrace);
        ComboDecayPerSecond = C(ComboDecayPerSecond, 0.1, 1000, d.ComboDecayPerSecond);
        BaseMaxCombo = Math.Clamp(BaseMaxCombo, 260, 10_000);
        BaseCritChance = C(BaseCritChance, 0, 1, d.BaseCritChance);
        BaseCritMult = C(BaseCritMult, 1, 1000, d.BaseCritMult);
        FuryDuration = C(FuryDuration, 1, 120, d.FuryDuration);
        PrestigeRequirement = C(PrestigeRequirement, 1, 1e300, d.PrestigeRequirement);
        BaseOfflineHours = C(BaseOfflineHours, 0, 72, d.BaseOfflineHours);
        OfflineEfficiency = C(OfflineEfficiency, 0, 1, d.OfflineEfficiency);
        EventMinSeconds = C(EventMinSeconds, 5, 3600, d.EventMinSeconds);
        EventMaxSeconds = C(EventMaxSeconds, EventMinSeconds, 7200, d.EventMaxSeconds);
        AutosaveSeconds = C(AutosaveSeconds, 5, 600, d.AutosaveSeconds);
        if (Milestones is not { Length: > 0 }) Milestones = d.Milestones;
        foreach (var p in Producers)
        {
            p.BaseCost = C(p.BaseCost, 1, 1e300, 10);
            p.CostGrowth = C(p.CostGrowth, 1.01, 3, 1.15);
            p.BaseRate = C(p.BaseRate, 0, 1e300, 0.1);
        }
        foreach (var u in Upgrades)
        {
            u.BaseCost = C(u.BaseCost, 1, 1e300, 100);
            u.CostGrowth = C(u.CostGrowth, 1.01, 1000, 10);
            u.MaxLevel = Math.Clamp(u.MaxLevel, 1, 1000);
        }
        return this;
    }
}

/// <summary>Achievement condition types.</summary>
public enum Goal { Clicks, Earned, Producer, TotalProducers, Combo, Crits, Fury, Prestiges, Environments, Abilities, Events, Upgrades, PlayHours, Pps }

public sealed record AchievementDef(string Id, Goal Goal, double Threshold, string? Target = null, string? Cosmetic = null);

public enum CosmeticSlot { Glove, Bag, Particles, Marks, Outfit, Decor }

public sealed record CosmeticDef(string Id, CosmeticSlot Slot, Color Primary, Color Secondary, string? Unlock = null);

/// <summary>Achievements and cosmetics (fixed content; every achievement gives one mastery point).</summary>
public static class PunchContent
{
    public static readonly IReadOnlyList<AchievementDef> Achievements = Build();

    private static List<AchievementDef> Build()
    {
        var list = new List<AchievementDef>
        {
            new("first_punch", Goal.Clicks, 1),
            new("first_crit", Goal.Crits, 1),
            new("first_boxer", Goal.TotalProducers, 1),
            new("first_prestige", Goal.Prestiges, 1, Cosmetic: "glove_gold"),
            new("all_envs", Goal.Environments, 8, Cosmetic: "glove_galaxy")
        };
        foreach (var n in new[] { 100, 1_000, 10_000, 50_000, 250_000 }) list.Add(new($"clicks_{n}", Goal.Clicks, n, Cosmetic: n == 10_000 ? "marks_dents" : null));
        foreach (var e in new[] { 3, 6, 9, 12, 15, 18, 24, 30, 40 }) list.Add(new($"earned_{e}", Goal.Earned, Math.Pow(10, e), Cosmetic: e == 6 ? "bag_classic" : e == 12 ? "particles_gold" : null));
        foreach (var n in new[] { 10, 50, 100, 250 }) list.Add(new($"combo_{n}", Goal.Combo, n, Cosmetic: n == 250 ? "particles_fire" : null));
        foreach (var n in new[] { 10, 100, 1_000 }) list.Add(new($"crits_{n}", Goal.Crits, n));
        foreach (var n in new[] { 1, 10, 50 }) list.Add(new($"fury_{n}", Goal.Fury, n, Cosmetic: n == 10 ? "glove_flame" : null));
        foreach (var n in new[] { 3, 10 }) list.Add(new($"prestige_{n}", Goal.Prestiges, n, Cosmetic: n == 3 ? "outfit_pro" : null));
        foreach (var n in new[] { 2, 4, 6 }) list.Add(new($"envs_{n}", Goal.Environments, n, Cosmetic: n == 4 ? "decor_trophies" : n == 6 ? "glove_neon" : null));
        foreach (var n in new[] { 10, 100, 500 }) list.Add(new($"abilities_{n}", Goal.Abilities, n, Cosmetic: n == 100 ? "marks_stars" : null));
        foreach (var n in new[] { 1, 10, 50 }) list.Add(new($"events_{n}", Goal.Events, n, Cosmetic: n == 10 ? "particles_ice" : null));
        foreach (var n in new[] { 10, 50, 120 }) list.Add(new($"upgrades_{n}", Goal.Upgrades, n, Cosmetic: n == 50 ? "bag_camo" : null));
        foreach (var n in new[] { 1, 10 }) list.Add(new($"hours_{n}", Goal.PlayHours, n, Cosmetic: n == 1 ? "decor_plants" : null));
        foreach (var n in new[] { 100, 500 }) list.Add(new($"producers_{n}", Goal.TotalProducers, n, Cosmetic: n == 100 ? "outfit_retro" : null));
        foreach (var e in new[] { 3, 9, 15 }) list.Add(new($"pps_{e}", Goal.Pps, Math.Pow(10, e), Cosmetic: e == 9 ? "decor_neon" : null));
        foreach (var (id, n) in new[] { ("rookie", 50), ("sparring", 50), ("trainer", 25), ("doubleend", 25), ("machine", 25), ("robot", 25), ("team", 10), ("studio", 10), ("arena", 5), ("holo", 1), ("orbital", 1) })
            list.Add(new($"own_{id}", Goal.Producer, n, id, id == "orbital" ? "bag_gold" : id == "robot" ? "outfit_neon" : null));
        return list;
    }

    public static readonly IReadOnlyList<CosmeticDef> Cosmetics = new CosmeticDef[]
    {
        new("glove_red", CosmeticSlot.Glove, C("#D8313A"), C("#7A1018")),
        new("glove_blue", CosmeticSlot.Glove, C("#2F6FE0"), C("#123A80"), "earned_6"),
        new("glove_black", CosmeticSlot.Glove, C("#2A2A30"), C("#0E0E12"), "clicks_1000"),
        new("glove_gold", CosmeticSlot.Glove, C("#F2C232"), C("#9C7414")),
        new("glove_flame", CosmeticSlot.Glove, C("#FF7A1A"), C("#C2200E")),
        new("glove_neon", CosmeticSlot.Glove, C("#3DF2C8"), C("#0E8A70")),
        new("glove_chrome", CosmeticSlot.Glove, C("#DCE4EE"), C("#7C8796")),
        new("glove_galaxy", CosmeticSlot.Glove, C("#6A3DE8"), C("#1A0E48")),
        new("bag_red", CosmeticSlot.Bag, C("#B8262E"), C("#5E1216")),
        new("bag_classic", CosmeticSlot.Bag, C("#7A4E2A"), C("#3A2412")),
        new("bag_bw", CosmeticSlot.Bag, C("#E8E8E8"), C("#1E1E1E"), "combo_100"),
        new("bag_camo", CosmeticSlot.Bag, C("#5A6A3A"), C("#2E3A1E")),
        new("bag_gold", CosmeticSlot.Bag, C("#E8B830"), C("#7A5A10")),
        new("bag_holo", CosmeticSlot.Bag, C("#7FE8FF"), C("#C27BFF")),
        new("particles_white", CosmeticSlot.Particles, C("#FFFFFF"), C("#C8D4E0")),
        new("particles_gold", CosmeticSlot.Particles, C("#FFD54A"), C("#FF9F1A")),
        new("particles_fire", CosmeticSlot.Particles, C("#FF5A1A"), C("#FFD21A")),
        new("particles_ice", CosmeticSlot.Particles, C("#8FE8FF"), C("#2F8FE0")),
        new("particles_rainbow", CosmeticSlot.Particles, C("#FF4FA0"), C("#4FD8FF"), "earned_18"),
        new("marks_none", CosmeticSlot.Marks, C("#000000"), C("#000000")),
        new("marks_dents", CosmeticSlot.Marks, C("#3A0A0E"), C("#000000")),
        new("marks_stars", CosmeticSlot.Marks, C("#FFE36A"), C("#FFFFFF")),
        new("marks_bolts", CosmeticSlot.Marks, C("#6AE8FF"), C("#FFFFFF"), "fury_50"),
        new("outfit_track", CosmeticSlot.Outfit, C("#2F6FE0"), C("#F4F4F4")),
        new("outfit_pro", CosmeticSlot.Outfit, C("#1A1A1A"), C("#E8B830")),
        new("outfit_retro", CosmeticSlot.Outfit, C("#E85A2A"), C("#F2D24A")),
        new("outfit_neon", CosmeticSlot.Outfit, C("#2AE8A0"), C("#E82A9A")),
        new("decor_posters", CosmeticSlot.Decor, C("#E8D8B0"), C("#C83A3A")),
        new("decor_trophies", CosmeticSlot.Decor, C("#F2C232"), C("#7A5A10")),
        new("decor_neon", CosmeticSlot.Decor, C("#FF3AA0"), C("#3AE8FF")),
        new("decor_plants", CosmeticSlot.Decor, C("#3A9A4A"), C("#7A4E2A"))
    };

    public static CosmeticDef Cosmetic(string id) => Cosmetics.FirstOrDefault(c => c.Id == id) ?? Cosmetics[0];

    /// <summary>The first cosmetic of each slot is always owned.</summary>
    public static IEnumerable<string> DefaultCosmetics => Enum.GetValues<CosmeticSlot>().Select(s => Cosmetics.First(c => c.Slot == s).Id);

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
