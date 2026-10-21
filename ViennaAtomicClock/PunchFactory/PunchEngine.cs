namespace ViennaAtomicClock.PunchFactory;

public enum BuyMode { One = 1, Ten = 10, TwentyFive = 25, Hundred = 100, Max = 0 }

public enum PunchSource { Click, Flurry, PowerPunch, Controller }

/// <summary>What one punch produced (for numbers, sounds, particles and vibration).</summary>
public readonly record struct PunchResult(BigNum Amount, bool Critical, int Combo, bool Fury, PunchSource Source, double Strength);

/// <summary>Offline production on start.</summary>
public readonly record struct OfflineReport(TimeSpan Away, TimeSpan Counted, BigNum Gain, bool Capped, bool ClockProblem);

/// <summary>A temporary bonus (ability or event).</summary>
public sealed class Buff
{
    public required string Id { get; init; }
    public double ProductionMult { get; init; } = 1;
    public double ClickMult { get; init; } = 1;
    public double CritChance { get; init; }
    public double HumanMult { get; init; } = 1;
    public double GoldenShare { get; init; }
    public double Remaining { get; set; }
    public double Duration { get; init; }
}

/// <summary>A random target in the studio that can be caught.</summary>
public sealed class ActiveEvent
{
    public required EventDef Def { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public double Remaining { get; set; }
    public int HitsLeft { get; set; }
}

/// <summary>Values that only change with purchases (recomputed on demand, not every frame).</summary>
public readonly record struct Stats(BigNum ClickBase, double ClickPpsShare, BigNum Pps, IReadOnlyDictionary<string, BigNum> UnitRate,
    double CritChance, double CritMult, int MaxCombo, double ComboDecay, double TrainingSpeed, double OfflineHours, double GlobalMult,
    double CostFactor, double CooldownFactor, double AbilityPower, double PowerPunchMult, double GoldenMult, double FuryExtraSeconds, int ComboFloor, double EventFrequency);

/// <summary>
/// Punch Factory's rules – no drawing, no sound, no files. Clicks, combo, crits, fury, producers with milestones,
/// upgrades, abilities, events, environments, prestige, offline time and achievements. The view and UI only call
/// these methods and listen to the events.
/// </summary>
public sealed class PunchEngine
{
    private readonly Random _random;
    private Stats _stats;
    private bool _dirty = true;
    private double _achievementTimer;
    private double _flurryLeft;
    private double _flurryTimer;
    private double _nextEvent;
    private double _sinceHit = 99;

    public PunchEngine(BalanceConfig config, PunchSave save, Random? random = null)
    {
        Config = config;
        Save = save;
        _random = random ?? new Random();
        _nextEvent = NextEventDelay();
        UpdateEnvironments(notify: false);
    }

    public BalanceConfig Config { get; }
    public PunchSave Save { get; private set; }
    public double Combo { get; private set; }
    public double FuryLeft { get; private set; }
    public bool Fury => FuryLeft > 0;
    public List<Buff> Buffs { get; } = new();
    public ActiveEvent? Event { get; private set; }

    public event Action<PunchResult>? Punched;
    public event Action? FuryStarted;
    public event Action? FuryEnded;
    public event Action<AchievementDef>? AchievementUnlocked;
    public event Action<EnvironmentDef>? EnvironmentUnlocked;
    public event Action<string>? AbilityUsed;
    public event Action<ActiveEvent>? EventSpawned;
    public event Action<ActiveEvent, BigNum>? EventCaught;
    public event Action? Changed;

    public Stats Stats
    {
        get
        {
            if (_dirty)
            {
                _stats = Compute(Save.Count, Save.Level);
                _dirty = false;
                if (_stats.Pps > Save.BestPps) Save.BestPps = _stats.Pps;
            }
            return _stats;
        }
    }

    public void MarkDirty() => _dirty = true;

    // ───────────────────────── derived values ─────────────────────────

    /// <summary>Sum (additive effects) or product (multiplicative effects) of all levels of matching upgrades.</summary>
    private double Sum(Effect effect, Func<string, int> level, string? target = null) =>
        Config.Upgrades.Where(u => u.Effect == effect && (target is null || Targets(u, target))).Sum(u => u.Value * level(u.Id))
        + Config.PrestigeUpgrades.Where(p => p.Effect == effect).Sum(p => p.Value * Save.PrestigeLevel(p.Id));

    private double Product(Effect effect, Func<string, int> level, string? target = null)
    {
        var result = 1.0;
        foreach (var u in Config.Upgrades.Where(u => u.Effect == effect && (target is null || Targets(u, target))))
            result *= Math.Pow(u.Value, level(u.Id));
        foreach (var p in Config.PrestigeUpgrades.Where(p => p.Effect == effect && p.Value > 0))
            result *= Math.Pow(p.Value, Save.PrestigeLevel(p.Id));
        return result;
    }

    private static bool Targets(UpgradeDef upgrade, string target) =>
        upgrade.Target is null || upgrade.Target.Split(',').Any(t => string.Equals(t.Trim(), target, StringComparison.OrdinalIgnoreCase));

    public int MasteryPoints => Save.Achievements.Count + Save.HighestEnvironment;

    public EnvironmentDef ActiveEnvironment => Config.Environments.FirstOrDefault(e => e.Id == Save.Environment) ?? Config.Environments[0];

    /// <summary>All values for the given producer counts and upgrade levels (also used for "what if I buy this" previews).</summary>
    public Stats Compute(Func<string, int> count, Func<string, int> level)
    {
        var c = Config;
        var global = (1 + Save.FameEarned * c.FameBonusPerPoint) * (1 + MasteryPoints * c.MasteryBonusPerPoint) * (1 + ActiveEnvironment.Bonus);
        global *= Product(Effect.AllMult, level);
        var training = 1 + Sum(Effect.TrainingSpeed, level);
        var owned = c.Producers.Count(p => count(p.Id) > 0);
        global *= 1 + Sum(Effect.TypeSynergy, level) * owned;
        global *= training;

        var trainers = c.Producers.Where(p => p.Id == "trainer").Sum(p => count(p.Id));
        var trainerBoost = 1 + trainers * (0.01 + Sum(Effect.TrainerSynergy, level));
        var studios = c.Producers.Where(p => p.Id == "studio").Sum(p => count(p.Id));
        var unit = new Dictionary<string, BigNum>();
        BigNum pps = BigNum.Zero;
        foreach (var p in c.Producers)
        {
            var n = count(p.Id);
            var milestones = c.Milestones.Count(m => n >= m);
            var rate = p.BaseRate * Math.Pow(c.MilestoneMult, milestones) * Product(Effect.ProducerMult, level, p.Id) * Product(Effect.KindMult, level, p.Kind.ToString()) * global;
            if (p.Id != "trainer") rate *= trainerBoost;
            if (p.Kind == ProducerKind.Human) rate *= 1 + 0.02 * studios;
            BigNum perUnit = rate;
            unit[p.Id] = perUnit;
            if (n > 0) pps += perUnit * n;
        }

        var clickBase = (BigNum)((1 + Sum(Effect.ClickFlat, level)) * Product(Effect.ClickMult, level) * (1 + Save.FameEarned * c.FameBonusPerPoint));
        var offline = c.BaseOfflineHours + Sum(Effect.OfflineHours, level);
        var cost = Math.Max(0.2, 1 - Sum(Effect.CostReduction, level));
        var cooldown = Math.Max(0.3, 1 - Sum(Effect.AbilityCooldown, level));
        return new Stats(clickBase, Sum(Effect.ClickPpsShare, level), pps, unit,
            Math.Min(1, c.BaseCritChance + Sum(Effect.CritChance, level)), c.BaseCritMult + Sum(Effect.CritMult, level),
            c.BaseMaxCombo + (int)Sum(Effect.MaxCombo, level), Math.Max(0.1, 1 - Sum(Effect.ComboDecay, level)), training, offline, global,
            cost, cooldown, 1 + Sum(Effect.AbilityPower, level), 1 + Sum(Effect.PowerPunch, level), 1 + Sum(Effect.GoldenRewards, level),
            Sum(Effect.FuryBonus, level), (int)Sum(Effect.ComboFloor, level), 1 + Sum(Effect.EventFrequency, level));
    }

    /// <summary>Production per second right now (with abilities, events and fury).</summary>
    public BigNum PpsNow => Stats.Pps * BuffProductionMult() + HumanBoost();

    private double BuffProductionMult()
    {
        var mult = Fury ? Config.FuryProductionMult : 1;
        foreach (var buff in Buffs) mult *= buff.ProductionMult;
        return mult;
    }

    /// <summary>Coach's Call: human and machine boxers work faster (venues stay the same).</summary>
    private BigNum HumanBoost()
    {
        var factor = Buffs.Where(b => b.HumanMult > 1).Aggregate(1.0, (m, b) => m * b.HumanMult);
        if (factor <= 1) return BigNum.Zero;
        var stats = Stats;
        BigNum extra = BigNum.Zero;
        foreach (var p in Config.Producers.Where(p => p.Kind != ProducerKind.Venue))
        {
            var n = Save.Count(p.Id);
            if (n > 0) extra += stats.UnitRate[p.Id] * n * (factor - 1);
        }
        return extra * BuffProductionMult();
    }

    public double ComboMultiplier
    {
        get
        {
            var combo = Combo;
            if (combo >= 400) return 4;
            if (combo >= 100) return 3;
            if (combo >= 50) return 2;
            if (combo >= 25) return 1.5;
            if (combo >= 10) return 1.2;
            return 1;
        }
    }

    public double CritChanceNow => Math.Min(1, Stats.CritChance + Buffs.Sum(b => b.CritChance));

    /// <summary>Punch power of one normal hit right now (before crit).</summary>
    public BigNum ClickValue
    {
        get
        {
            var stats = Stats;
            var value = stats.ClickBase + PpsNow * stats.ClickPpsShare;
            var mult = ComboMultiplier * (Fury ? Config.FuryClickMult : 1);
            foreach (var buff in Buffs) mult *= buff.ClickMult;
            return value * mult;
        }
    }

    // ───────────────────────── punching ─────────────────────────

    public PunchResult Punch(PunchSource source = PunchSource.Click)
    {
        Combo = Math.Min(Stats.MaxCombo, Combo + 1);
        _sinceHit = 0;
        if (Combo > Save.MaxCombo) Save.MaxCombo = (int)Combo;
        if (!Fury && Combo >= 250)
        {
            FuryLeft = Config.FuryDuration + Stats.FuryExtraSeconds;
            Save.FuryCount++;
            FuryStarted?.Invoke();
        }
        var amount = ClickValue;
        var strength = 1.0;
        if (source == PunchSource.PowerPunch)
        {
            var power = Config.Abilities.First(a => a.Id == "power");
            amount *= power.Power * Stats.PowerPunchMult;
            strength = 3;
        }
        var golden = Buffs.Sum(b => b.GoldenShare);
        if (golden > 0) amount += Stats.Pps * (golden * 60 * Stats.GoldenMult);
        var critical = _random.NextDouble() < CritChanceNow;
        if (critical)
        {
            amount *= Stats.CritMult;
            Save.Crits++;
            strength *= 2;
        }
        if (source is PunchSource.Click or PunchSource.Controller or PunchSource.PowerPunch) Save.Clicks++;
        Earn(amount);
        var result = new PunchResult(amount, critical, (int)Combo, Fury, source, strength * (Fury ? 1.5 : 1));
        Punched?.Invoke(result);
        return result;
    }

    private void Earn(BigNum amount)
    {
        if (amount.IsZero || amount.IsNegative) return;
        Save.Punch += amount;
        Save.EarnedRun += amount;
        Save.EarnedAll += amount;
    }

    // ───────────────────────── time ─────────────────────────

    public void Tick(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        seconds = Math.Min(seconds, 1); // a hiccup never produces more than one second
        Save.PlaySeconds += seconds;
        var production = PpsNow * seconds;
        Earn(production);

        // Combo decays slowly once the hits stop.
        _sinceHit += seconds;
        if (_sinceHit > Config.ComboGrace && Combo > Stats.ComboFloor)
            Combo = Math.Max(Stats.ComboFloor, Combo - Config.ComboDecayPerSecond * Stats.ComboDecay * seconds);
        if (Fury)
        {
            FuryLeft -= seconds;
            if (FuryLeft <= 0)
            {
                FuryLeft = 0;
                Combo = Math.Min(Combo, 100);
                FuryEnded?.Invoke();
            }
        }

        // Buffs and cooldowns.
        for (var i = Buffs.Count - 1; i >= 0; i--)
        {
            Buffs[i].Remaining -= seconds;
            if (Buffs[i].Remaining <= 0) Buffs.RemoveAt(i);
        }
        foreach (var key in Save.Cooldowns.Keys.ToList())
        {
            var left = Save.Cooldowns[key] - seconds;
            if (left <= 0) Save.Cooldowns.Remove(key);
            else Save.Cooldowns[key] = left;
        }
        if (_flurryLeft > 0)
        {
            _flurryTimer -= seconds;
            while (_flurryTimer <= 0 && _flurryLeft > 0)
            {
                _flurryLeft--;
                _flurryTimer += 0.1;
                Punch(PunchSource.Flurry);
            }
        }

        // Random events.
        if (Event is { } active)
        {
            active.Remaining -= seconds;
            if (active.Remaining <= 0) Event = null;
        }
        else
        {
            _nextEvent -= seconds;
            if (_nextEvent <= 0 && Save.EarnedRun >= 50) SpawnEvent();
        }

        _achievementTimer -= seconds;
        if (_achievementTimer <= 0)
        {
            _achievementTimer = 0.5;
            UpdateEnvironments(notify: true);
            CheckAchievements();
            if (Stats.Pps > Save.BestPps) Save.BestPps = Stats.Pps;
        }
    }

    // ───────────────────────── producers ─────────────────────────

    public BigNum ProducerCost(ProducerDef def, int owned, int amount)
    {
        var g = def.CostGrowth;
        var first = BigNum.FromDouble(def.BaseCost * Stats.CostFactor) * BigNum.Pow(g, owned);
        return first * ((Math.Pow(g, amount) - 1) / (g - 1));
    }

    /// <summary>How many units the mode buys and what they cost. "Max" buys as many as affordable (at least the price of one is shown).</summary>
    public (int Amount, BigNum Cost) Quote(string id, BuyMode mode)
    {
        var def = Config.Producers.First(p => p.Id == id);
        var owned = Save.Count(id);
        var amount = (int)mode;
        if (mode == BuyMode.Max)
        {
            var first = BigNum.FromDouble(def.BaseCost * Stats.CostFactor) * BigNum.Pow(def.CostGrowth, owned);
            var ratio = (Save.Punch * (def.CostGrowth - 1) / first).ToDouble() + 1;
            amount = ratio <= 1 || double.IsNaN(ratio) ? 0 : (int)Math.Min(100_000, Math.Floor(Math.Log(ratio) / Math.Log(def.CostGrowth)));
            if (amount < 1) amount = 1;
            // Rounding can be off by one at the edge.
            while (amount > 1 && ProducerCost(def, owned, amount) > Save.Punch) amount--;
        }
        return (amount, ProducerCost(def, owned, amount));
    }

    /// <summary>Extra punch power per second if <paramref name="amount"/> more were owned.</summary>
    public BigNum ProducerGain(string id, int amount)
    {
        var after = Compute(p => Save.Count(p) + (p == id ? amount : 0), Save.Level);
        return after.Pps - Stats.Pps;
    }

    public bool BuyProducer(string id, BuyMode mode)
    {
        var (amount, cost) = Quote(id, mode);
        if (amount <= 0 || cost > Save.Punch) return false;
        Save.Punch -= cost;
        Save.Producers[id] = Save.Count(id) + amount;
        MarkDirty();
        CheckAchievements();
        Changed?.Invoke();
        return true;
    }

    public int NextMilestone(string id) => Config.Milestones.FirstOrDefault(m => m > Save.Count(id));

    // ───────────────────────── upgrades ─────────────────────────

    public bool IsUnlocked(UpgradeDef def)
    {
        if (Save.Level(def.Id) > 0) return true;
        if (def.UnlockProducer is not null && Save.Count(def.UnlockProducer) < def.UnlockCount) return false;
        return Save.EarnedRun >= def.UnlockEarned || def.UnlockEarned <= 0;
    }

    public BigNum UpgradeCost(UpgradeDef def) => BigNum.FromDouble(def.BaseCost) * BigNum.Pow(def.CostGrowth, Save.Level(def.Id));

    public bool IsMaxed(UpgradeDef def) => Save.Level(def.Id) >= def.MaxLevel;

    /// <summary>The effect of the next level (production and click value before → after).</summary>
    public (Stats Before, Stats After) UpgradePreview(UpgradeDef def) => (Stats, Compute(Save.Count, u => Save.Level(u) + (u == def.Id ? 1 : 0)));

    public bool BuyUpgrade(string id)
    {
        var def = Config.Upgrades.First(u => u.Id == id);
        if (!IsUnlocked(def) || IsMaxed(def)) return false;
        var cost = UpgradeCost(def);
        if (cost > Save.Punch) return false;
        Save.Punch -= cost;
        Save.Upgrades[id] = Save.Level(id) + 1;
        Save.UpgradesBought++;
        MarkDirty();
        CheckAchievements();
        Changed?.Invoke();
        return true;
    }

    // ───────────────────────── abilities ─────────────────────────

    public bool IsAbilityUnlocked(AbilityDef def) => Save.EarnedRun >= def.UnlockEarned || Save.EarnedAll >= def.UnlockEarned * 10;

    public double CooldownLeft(string id) => Save.Cooldowns.TryGetValue(id, out var left) ? left : 0;

    public double CooldownOf(AbilityDef def) => def.Cooldown * Stats.CooldownFactor;

    public bool UseAbility(string id)
    {
        var def = Config.Abilities.FirstOrDefault(a => a.Id == id);
        if (def is null || !IsAbilityUnlocked(def) || CooldownLeft(id) > 0) return false;
        var power = Stats.AbilityPower;
        var duration = def.Duration * power;
        switch (id)
        {
            case "power":
                Punch(PunchSource.PowerPunch);
                break;
            case "flurry":
                _flurryLeft += Math.Round(def.Power * power);
                _flurryTimer = 0;
                break;
            case "adrenalin":
                Buffs.Add(new Buff { Id = id, ProductionMult = def.Power, Remaining = duration, Duration = duration });
                break;
            case "focus":
                Buffs.Add(new Buff { Id = id, CritChance = def.Power, Remaining = duration, Duration = duration });
                break;
            case "coach":
                Buffs.Add(new Buff { Id = id, HumanMult = def.Power, Remaining = duration, Duration = duration });
                break;
            case "golden":
                Buffs.Add(new Buff { Id = id, GoldenShare = def.Power, Remaining = duration, Duration = duration });
                break;
        }
        Save.Cooldowns[id] = CooldownOf(def);
        Save.AbilitiesUsed++;
        AbilityUsed?.Invoke(id);
        Changed?.Invoke();
        return true;
    }

    // ───────────────────────── events ─────────────────────────

    private double NextEventDelay() => (Config.EventMinSeconds + _random.NextDouble() * (Config.EventMaxSeconds - Config.EventMinSeconds)) / Math.Max(1, _dirty ? 1 : Stats.EventFrequency);

    /// <summary>Spawns a random event now (also used by the tests).</summary>
    public ActiveEvent SpawnEvent(string? id = null)
    {
        var defs = Config.Events;
        var def = id is null ? Pick(defs) : defs.First(e => e.Id == id);
        Event = new ActiveEvent { Def = def, X = 0.15 + _random.NextDouble() * 0.7, Y = 0.18 + _random.NextDouble() * 0.45, Remaining = def.Lifetime, HitsLeft = Math.Max(1, def.Hits) };
        _nextEvent = NextEventDelay();
        EventSpawned?.Invoke(Event);
        return Event;
    }

    private EventDef Pick(IReadOnlyList<EventDef> defs)
    {
        var roll = _random.NextDouble() * defs.Sum(d => d.Weight);
        foreach (var def in defs)
        {
            roll -= def.Weight;
            if (roll < 0) return def;
        }
        return defs[^1];
    }

    /// <summary>Clicking the event target. Returns the reward once it is caught (zero for buffs and unfinished challengers).</summary>
    public BigNum? HitEvent()
    {
        if (Event is not { } active) return null;
        active.HitsLeft--;
        if (active.HitsLeft > 0) return BigNum.Zero;
        Event = null;
        Save.EventsCaught++;
        var stats = Stats;
        var golden = stats.GoldenMult;
        var floor = stats.ClickBase * 25;
        BigNum reward = BigNum.Zero;
        switch (active.Def.Id)
        {
            case "glove": reward = BigNum.Max(PpsNow * 600, floor) * golden; break;
            case "challenger": reward = BigNum.Max(PpsNow * 1200, floor * 3) * golden; break;
            case "sponsor": reward = BigNum.Max(BigNum.Min(Save.Punch * 0.15, PpsNow * 900), floor) * golden; break;
            case "drone": Buffs.Add(new Buff { Id = "drone", ClickMult = 7, Remaining = 15, Duration = 15 }); break;
            case "bonusbag": Buffs.Add(new Buff { Id = "bonusbag", ProductionMult = 2, Remaining = 30, Duration = 30 }); break;
            case "motivation":
                Combo = Math.Min(stats.MaxCombo, Combo + 60);
                Buffs.Add(new Buff { Id = "motivation", CritChance = 0.2, Remaining = 15, Duration = 15 });
                break;
        }
        Earn(reward);
        EventCaught?.Invoke(active, reward);
        Changed?.Invoke();
        return reward;
    }

    // ───────────────────────── environments ─────────────────────────

    public bool IsEnvironmentUnlocked(EnvironmentDef def) => Config.Environments.IndexOf(def) <= Save.HighestEnvironment;

    private void UpdateEnvironments(bool notify)
    {
        var highest = 0;
        for (var i = 0; i < Config.Environments.Count; i++)
            if (Save.EarnedAll >= Config.Environments[i].UnlockEarned) highest = i;
        if (highest <= Save.HighestEnvironment) return;
        var previous = Save.HighestEnvironment;
        Save.HighestEnvironment = highest;
        // Move into the new studio automatically (it has the bigger bonus).
        if (Config.Environments.FindIndex(e => e.Id == Save.Environment) == previous) Save.Environment = Config.Environments[highest].Id;
        MarkDirty();
        if (notify) EnvironmentUnlocked?.Invoke(Config.Environments[highest]);
    }

    public void SelectEnvironment(string id)
    {
        var def = Config.Environments.FirstOrDefault(e => e.Id == id);
        if (def is null || !IsEnvironmentUnlocked(def)) return;
        Save.Environment = id;
        MarkDirty();
        Changed?.Invoke();
    }

    // ───────────────────────── prestige ─────────────────────────

    public bool CanPrestige => Save.EarnedRun >= Config.PrestigeRequirement && FameGain >= 1;

    /// <summary>Fame a reset would give now (total fame grows with the cube root of lifetime punch power).</summary>
    public double FameGain
    {
        get
        {
            var ratio = (Save.EarnedAll / Config.PrestigeRequirement).Log10;
            if (double.IsNegativeInfinity(ratio) || ratio < 0) return 0;
            var potential = Math.Floor(Config.FameFactor * Math.Pow(10, ratio * Config.FamePower));
            return Math.Max(0, potential - Save.FameEarned);
        }
    }

    /// <summary>Starts a new career: producers, upgrades and punch power reset; fame, achievements, cosmetics, environments and statistics stay.</summary>
    public bool DoPrestige()
    {
        if (!CanPrestige) return false;
        Save.FameEarned += FameGain;
        Save.Prestiges++;
        Save.Punch = Sum(Effect.StartCash, _ => 0);
        Save.EarnedRun = BigNum.Zero;
        Save.Producers.Clear();
        Save.Upgrades.Clear();
        Save.Cooldowns.Clear();
        Buffs.Clear();
        Combo = 0;
        FuryLeft = 0;
        Event = null;
        _flurryLeft = 0;
        MarkDirty();
        CheckAchievements();
        Changed?.Invoke();
        return true;
    }

    public double PrestigeCost(PrestigeUpgradeDef def) => Math.Ceiling(def.Cost * Math.Pow(def.CostGrowth, Save.PrestigeLevel(def.Id)));

    public bool BuyPrestigeUpgrade(string id)
    {
        var def = Config.PrestigeUpgrades.FirstOrDefault(p => p.Id == id);
        if (def is null || Save.PrestigeLevel(id) >= def.MaxLevel) return false;
        var cost = PrestigeCost(def);
        if (cost > Save.FameAvailable) return false;
        Save.FameSpent += cost;
        Save.PrestigeLevels[id] = Save.PrestigeLevel(id) + 1;
        if (def.Cosmetic is not null) Save.Cosmetics.Add(def.Cosmetic);
        MarkDirty();
        Changed?.Invoke();
        return true;
    }

    // ───────────────────────── offline and achievements ─────────────────────────

    /// <summary>Production while the game was closed: capped (8 h + upgrades), never for negative or absurd clock jumps.</summary>
    public OfflineReport ApplyOffline(DateTime nowUtc)
    {
        var away = nowUtc - Save.LastSaveUtc;
        if (away < TimeSpan.Zero) return new OfflineReport(TimeSpan.Zero, TimeSpan.Zero, BigNum.Zero, false, true);
        var suspicious = away > TimeSpan.FromDays(400);
        var cap = TimeSpan.FromHours(Stats.OfflineHours);
        var counted = suspicious ? TimeSpan.Zero : (away > cap ? cap : away);
        if (counted < TimeSpan.FromSeconds(10)) return new OfflineReport(away, TimeSpan.Zero, BigNum.Zero, false, suspicious);
        var gain = Stats.Pps * (counted.TotalSeconds * Config.OfflineEfficiency);
        Earn(gain);
        foreach (var key in Save.Cooldowns.Keys.ToList())
        {
            var left = Save.Cooldowns[key] - away.TotalSeconds;
            if (left <= 0) Save.Cooldowns.Remove(key);
            else Save.Cooldowns[key] = left;
        }
        UpdateEnvironments(notify: false);
        return new OfflineReport(away, counted, gain, away > cap, suspicious);
    }

    public double Progress(AchievementDef def) => def.Goal switch
    {
        Goal.Clicks => Save.Clicks,
        Goal.Earned => Save.EarnedAll.Log10 >= 300 ? double.MaxValue : Save.EarnedAll.ToDouble(),
        Goal.Producer => Save.Count(def.Target ?? ""),
        Goal.TotalProducers => Save.Producers.Values.Sum(),
        Goal.Combo => Save.MaxCombo,
        Goal.Crits => Save.Crits,
        Goal.Fury => Save.FuryCount,
        Goal.Prestiges => Save.Prestiges,
        Goal.Environments => Save.HighestEnvironment + 1,
        Goal.Abilities => Save.AbilitiesUsed,
        Goal.Events => Save.EventsCaught,
        Goal.Upgrades => Save.UpgradesBought,
        Goal.PlayHours => Save.PlaySeconds / 3600,
        _ => Save.BestPps.Log10 >= 300 ? double.MaxValue : Save.BestPps.ToDouble()
    };

    public void CheckAchievements()
    {
        foreach (var def in PunchContent.Achievements)
        {
            if (Save.Achievements.Contains(def.Id) || Progress(def) < def.Threshold) continue;
            Save.Achievements.Add(def.Id);
            if (def.Cosmetic is not null) Save.Cosmetics.Add(def.Cosmetic);
            foreach (var cosmetic in PunchContent.Cosmetics.Where(c => c.Unlock == def.Id)) Save.Cosmetics.Add(cosmetic.Id);
            MarkDirty(); // mastery point
            AchievementUnlocked?.Invoke(def);
        }
    }

    public void Equip(string cosmeticId)
    {
        if (!Save.Cosmetics.Contains(cosmeticId)) return;
        var def = PunchContent.Cosmetic(cosmeticId);
        Save.Equipped[def.Slot] = cosmeticId;
        Changed?.Invoke();
    }

    /// <summary>Starts from a new save (full reset or import).</summary>
    public void Replace(PunchSave save)
    {
        Save = save;
        Buffs.Clear();
        Combo = 0;
        FuryLeft = 0;
        Event = null;
        _flurryLeft = 0;
        MarkDirty();
        UpdateEnvironments(notify: false);
        Changed?.Invoke();
    }
}
