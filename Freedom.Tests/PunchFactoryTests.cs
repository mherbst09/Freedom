using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ViennaAtomicClock;
using ViennaAtomicClock.PunchFactory;
using Xunit;

namespace Freedom.Tests;

/// <summary>Random with a fixed result (crits on or off).</summary>
internal sealed class FixedRandom : Random
{
    private readonly double _value;
    public FixedRandom(double value) => _value = value;
    public override double NextDouble() => _value;
    protected override double Sample() => _value;
    public override int Next(int maxValue) => 0;
}

public sealed class BigNumTests
{
    [Fact]
    public void Arithmetic_works_far_beyond_double()
    {
        var a = BigNum.FromLog10(400);
        var b = BigNum.FromLog10(399);
        Assert.Equal(400, (a + b).Log10, 1);
        Assert.Equal(799, (a * b).Log10, 6);
        Assert.Equal(1, (a / b).Log10, 6);
        Assert.True(a > b);
        Assert.Equal(a, a + 1); // far below precision
        Assert.Equal(1600, BigNum.Pow(a, 4).Log10, 6);
        Assert.Equal(double.PositiveInfinity, a.ToDouble());
        Assert.Equal(12.5, ((BigNum)10 + 2.5).ToDouble(), 9);
        Assert.Equal(-5, ((BigNum)5 - 10).ToDouble(), 9);
        Assert.True(BigNum.Zero < 1);
        Assert.True((BigNum)(-1) < BigNum.Zero);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1K")]
    [InlineData(1234, "1.23K")]
    [InlineData(12345, "12.3K")]
    [InlineData(1_000_000, "1M")]
    [InlineData(2_500_000_000, "2.5B")]
    [InlineData(1e15, "1Qa")]
    [InlineData(7.5, "7.5")]
    public void Large_numbers_are_abbreviated(double value, string expected) => Assert.Equal(expected, ((BigNum)value).Format(CultureInfo.InvariantCulture));

    [Fact]
    public void Very_large_numbers_switch_to_letters_and_exponents()
    {
        Assert.Equal("1Vg", BigNum.FromLog10(63).Format(CultureInfo.InvariantCulture));
        Assert.Equal("1aa", BigNum.FromLog10(66).Format(CultureInfo.InvariantCulture));
        Assert.EndsWith("e5000", BigNum.FromLog10(5000).Format(CultureInfo.InvariantCulture));
        Assert.Equal("1,23K", ((BigNum)1234).Format(CultureInfo.GetCultureInfo("de-DE")));
        Assert.True(BigNum.TryParse(BigNum.FromLog10(12345.678).Serialize(), out var back));
        Assert.Equal(12345.678, back.Log10, 6);
        Assert.False(BigNum.TryParse("NaN|5", out _));
    }
}

public sealed class PunchEngineTests
{
    private static PunchEngine Engine(double random = 0.99, Action<PunchSave>? setup = null)
    {
        var save = new PunchSave().Normalize();
        setup?.Invoke(save);
        return new PunchEngine(BalanceConfig.Default().Normalize(), save, new FixedRandom(random));
    }

    [Fact]
    public void A_punch_gives_one_punch_power_at_the_start()
    {
        var engine = Engine();
        var result = engine.Punch();
        Assert.Equal(1, result.Amount.ToDouble(), 9);
        Assert.Equal(1, engine.Save.Punch.ToDouble(), 9);
        Assert.Equal(1, engine.Save.Clicks);
        Assert.False(result.Critical);
    }

    [Fact]
    public void Critical_hits_multiply_the_punch()
    {
        var engine = Engine(random: 0.0);
        var result = engine.Punch();
        Assert.True(result.Critical);
        Assert.Equal(engine.Config.BaseCritMult, result.Amount.ToDouble(), 9);
        Assert.Equal(1, engine.Save.Crits);
    }

    [Fact]
    public void Combos_raise_the_multiplier_and_slowly_fall_off()
    {
        var engine = Engine();
        for (var i = 0; i < 9; i++) engine.Punch();
        Assert.Equal(1, engine.ComboMultiplier);
        engine.Punch();
        Assert.Equal(1.2, engine.ComboMultiplier);
        for (var i = 0; i < 15; i++) engine.Punch();
        Assert.Equal(1.5, engine.ComboMultiplier);
        Assert.Equal(1.5, engine.Punch().Amount.ToDouble(), 9);
        engine.Tick(0.5); // within the grace time
        Assert.Equal(26, engine.Combo);
        engine.Tick(1); // grace over: falls slowly
        Assert.InRange(engine.Combo, 15, 25.9);
        for (var i = 0; i < 10; i++) engine.Tick(1);
        Assert.Equal(0, engine.Combo);
    }

    [Fact]
    public void Fury_mode_starts_at_250_and_ends_after_its_time()
    {
        var engine = Engine();
        var started = 0;
        var ended = 0;
        engine.FuryStarted += () => started++;
        engine.FuryEnded += () => ended++;
        for (var i = 0; i < 250; i++) engine.Punch();
        Assert.True(engine.Fury);
        Assert.Equal(1, started);
        var furyPunch = engine.Punch();
        Assert.True(furyPunch.Fury);
        Assert.Equal(3 * engine.Config.FuryClickMult, furyPunch.Amount.ToDouble(), 6);
        for (var i = 0; i < 12; i++) engine.Tick(1);
        Assert.False(engine.Fury);
        Assert.Equal(1, ended);
        Assert.True(engine.Combo <= 100);
        Assert.Equal(1, engine.Save.FuryCount);
    }

    [Fact]
    public void Producer_prices_grow_and_max_buys_what_is_affordable()
    {
        var engine = Engine(setup: s => s.Punch = 1000);
        var rookie = engine.Config.Producers.First(p => p.Id == "rookie");
        Assert.Equal(15, engine.Quote("rookie", BuyMode.One).Cost.ToDouble(), 6);
        Assert.Equal(15 * (Math.Pow(1.15, 10) - 1) / 0.15, engine.Quote("rookie", BuyMode.Ten).Cost.ToDouble(), 4);
        var (max, cost) = engine.Quote("rookie", BuyMode.Max);
        Assert.True(cost <= engine.Save.Punch);
        Assert.True(engine.ProducerCost(rookie, 0, max + 1) > engine.Save.Punch);
        Assert.True(engine.BuyProducer("rookie", BuyMode.Max));
        Assert.Equal(max, engine.Save.Count("rookie"));
        Assert.Equal(1000 - cost.ToDouble(), engine.Save.Punch.ToDouble(), 4);
        Assert.False(engine.BuyProducer("orbital", BuyMode.One)); // far too expensive
    }

    [Fact]
    public void Producers_earn_per_second_and_milestones_double_them()
    {
        var engine = Engine(setup: s => s.Producers["sparring"] = 9);
        Assert.Equal(9, engine.Stats.Pps.ToDouble(), 6);
        engine.Tick(1);
        Assert.Equal(9, engine.Save.Punch.ToDouble(), 6);
        var mastery = 1 + engine.MasteryPoints * 0.01; // the first boxer already unlocked an achievement
        var gain = engine.ProducerGain("sparring", 1);
        Assert.Equal(11 * mastery, gain.ToDouble(), 6); // the 10th unit hits the milestone: 10 × 2 − 9
        engine.Save.Producers["sparring"] = 10;
        engine.MarkDirty();
        Assert.Equal(20 * mastery, engine.Stats.Pps.ToDouble(), 6);
        Assert.Equal(25, engine.NextMilestone("sparring"));
    }

    [Fact]
    public void Trainers_and_studios_boost_the_others()
    {
        var engine = Engine(setup: s => { s.Producers["sparring"] = 1; s.Producers["trainer"] = 5; });
        // sparring 1 × (1 + 5 × 1 %) + trainer 5 × 8
        Assert.Equal(1.05 + 40, engine.Stats.Pps.ToDouble(), 6);
    }

    [Fact]
    public void Upgrades_cost_more_each_level_and_show_a_real_preview()
    {
        var engine = Engine(setup: s => s.Punch = 1e9);
        var wraps = engine.Config.Upgrades.First(u => u.Id == "wraps");
        Assert.True(engine.IsUnlocked(wraps));
        var first = engine.UpgradeCost(wraps);
        var (before, after) = engine.UpgradePreview(wraps);
        Assert.Equal(1, (after.ClickBase - before.ClickBase).ToDouble(), 9);
        Assert.True(engine.BuyUpgrade("wraps"));
        Assert.True(engine.UpgradeCost(wraps) > first);
        Assert.Equal(2, engine.Punch().Amount.ToDouble(), 9);
        var faster = engine.Config.Upgrades.First(u => u.Id == "faster");
        Assert.False(engine.IsUnlocked(faster)); // needs 5 rookies
        engine.Save.Producers["rookie"] = 5;
        Assert.True(engine.IsUnlocked(faster));
        for (var i = 0; i < wraps.MaxLevel; i++) engine.BuyUpgrade("wraps");
        Assert.True(engine.IsMaxed(wraps));
        Assert.False(engine.BuyUpgrade("wraps"));
    }

    [Fact]
    public void Abilities_have_cooldowns_and_temporary_effects()
    {
        var engine = Engine(setup: s => { s.EarnedRun = 1e9; s.EarnedAll = 1e9; s.Producers["sparring"] = 10; });
        Assert.True(engine.UseAbility("adrenalin"));
        Assert.False(engine.UseAbility("adrenalin")); // cooling down
        Assert.Equal(engine.Stats.Pps.ToDouble() * 2, engine.PpsNow.ToDouble(), 6);
        for (var i = 0; i < 21; i++) engine.Tick(1);
        Assert.Equal(engine.Stats.Pps.ToDouble(), engine.PpsNow.ToDouble(), 6);
        var clicks = engine.Save.Clicks;
        Assert.True(engine.UseAbility("power"));
        Assert.Equal(clicks + 1, engine.Save.Clicks);
        Assert.True(engine.UseAbility("flurry"));
        var before = engine.Save.Punch;
        for (var i = 0; i < 30; i++) engine.Tick(0.1);
        Assert.True(engine.Save.Punch > before);
        Assert.Equal(engine.Config.Abilities.First(a => a.Id == "adrenalin").Cooldown, engine.CooldownOf(engine.Config.Abilities.First(a => a.Id == "adrenalin")));
        var locked = Engine();
        Assert.False(locked.UseAbility("golden")); // not unlocked yet
    }

    [Fact]
    public void Events_give_rewards_or_bonuses_when_caught()
    {
        var engine = Engine(setup: s => { s.Producers["sparring"] = 10; s.EarnedRun = 1000; });
        engine.SpawnEvent("glove");
        var reward = engine.HitEvent();
        Assert.NotNull(reward);
        Assert.True(reward!.Value >= engine.Stats.Pps * 600);
        Assert.Null(engine.Event);
        engine.SpawnEvent("challenger");
        for (var i = 0; i < 11; i++) Assert.Equal(BigNum.Zero, engine.HitEvent());
        Assert.True(engine.HitEvent() > BigNum.Zero);
        engine.SpawnEvent("bonusbag");
        engine.HitEvent();
        Assert.Equal(engine.Stats.Pps.ToDouble() * 2, engine.PpsNow.ToDouble(), 6);
        engine.SpawnEvent("drone");
        for (var i = 0; i < 10; i++) engine.Tick(1); // not caught in time
        Assert.Null(engine.Event);
        Assert.Equal(3, engine.Save.EventsCaught);
    }

    [Fact]
    public void Prestige_shows_fame_and_keeps_only_the_permanent_things()
    {
        var engine = Engine(setup: s => { s.EarnedRun = 1e12; s.EarnedAll = 1e12; s.Punch = 5e11; s.Producers["robot"] = 50; s.Upgrades["wraps"] = 3; s.Achievements.Add("first_punch"); });
        Assert.True(engine.CanPrestige);
        var gain = engine.FameGain;
        Assert.Equal(50, gain, 6); // 5 × ∛(1e12 / 1e9)
        Assert.True(engine.DoPrestige());
        var s = engine.Save;
        Assert.Equal(gain, s.FameEarned);
        Assert.Equal(1, s.Prestiges);
        Assert.Equal(BigNum.Zero, s.Punch);
        Assert.Empty(s.Producers);
        Assert.Empty(s.Upgrades);
        Assert.Equal(1e12, s.EarnedAll.ToDouble(), 0);
        Assert.Contains("first_punch", s.Achievements);
        Assert.Contains("first_prestige", s.Achievements);
        Assert.False(engine.CanPrestige);
        // Fame makes clicks stronger at once.
        Assert.Equal(1 + gain * 0.02, engine.Punch().Amount.ToDouble(), 6);
        Assert.True(engine.BuyPrestigeUpgrade("f_click"));
        Assert.Equal(gain - 1, s.FameAvailable);
    }

    [Fact]
    public void Offline_time_is_capped_and_clock_tricks_give_nothing()
    {
        var now = DateTime.UtcNow;
        var engine = Engine(setup: s => { s.Producers["sparring"] = 10; s.LastSaveUtc = now.AddHours(-20); });
        var report = engine.ApplyOffline(now);
        Assert.True(report.Capped);
        Assert.Equal(TimeSpan.FromHours(8), report.Counted);
        Assert.Equal(20 * 8 * 3600, report.Gain.ToDouble(), 0);

        var back = Engine(setup: s => { s.Producers["sparring"] = 10; s.LastSaveUtc = now.AddHours(3); });
        var backwards = back.ApplyOffline(now);
        Assert.True(backwards.ClockProblem);
        Assert.Equal(BigNum.Zero, backwards.Gain);
        Assert.Equal(BigNum.Zero, back.Save.Punch);

        var ancient = Engine(setup: s => { s.Producers["sparring"] = 10; s.LastSaveUtc = now.AddYears(-3); });
        Assert.True(ancient.ApplyOffline(now).ClockProblem);
        Assert.Equal(BigNum.Zero, ancient.Save.Punch);

        var upgraded = Engine(setup: s => { s.Producers["sparring"] = 10; s.Upgrades["regen"] = 2; s.LastSaveUtc = now.AddHours(-20); });
        Assert.Equal(TimeSpan.FromHours(12), upgraded.ApplyOffline(now).Counted);
    }

    [Fact]
    public void There_are_at_least_50_achievements_and_they_unlock()
    {
        Assert.True(PunchContent.Achievements.Count >= 50);
        Assert.Equal(PunchContent.Achievements.Count, PunchContent.Achievements.Select(a => a.Id).Distinct().Count());
        var engine = Engine();
        var unlocked = new List<string>();
        engine.AchievementUnlocked += def => unlocked.Add(def.Id);
        engine.Punch();
        engine.CheckAchievements();
        Assert.Contains("first_punch", unlocked);
        var pps = engine.Stats.Pps;
        Assert.Equal(1, engine.MasteryPoints);
        Assert.All(PunchContent.Cosmetics.Where(c => c.Unlock is not null), c => Assert.Contains(PunchContent.Achievements, a => a.Id == c.Unlock));
        Assert.All(PunchContent.Achievements.Where(a => a.Cosmetic is not null), a => Assert.Contains(PunchContent.Cosmetics, c => c.Id == a.Cosmetic));
    }

    [Fact]
    public void Environments_unlock_with_lifetime_power_and_give_their_bonus()
    {
        var engine = Engine(setup: s => s.Producers["sparring"] = 10);
        var base_ = engine.Stats.Pps.ToDouble();
        var unlocked = new List<string>();
        engine.EnvironmentUnlocked += e => unlocked.Add(e.Id);
        engine.Save.EarnedAll = 600_000;
        engine.Tick(0.6);
        Assert.Equal(new[] { "fitness" }, unlocked);
        Assert.Equal("fitness", engine.Save.Environment);
        Assert.Equal(base_ * 1.1 * (1 + engine.MasteryPoints * 0.01) / 1, engine.Stats.Pps.ToDouble() / (1 + 0) , 1);
        engine.SelectEnvironment("garage");
        Assert.Equal("garage", engine.Save.Environment);
        engine.SelectEnvironment("orbit"); // locked
        Assert.Equal("garage", engine.Save.Environment);
    }

    [Fact]
    public void Huge_production_stays_stable()
    {
        var engine = Engine(setup: s => { foreach (var p in BalanceConfig.Default().Producers) s.Producers[p.Id] = 100_000; s.FameEarned = 1e12; s.Upgrades["global"] = 10; });
        for (var i = 0; i < 600; i++) engine.Tick(1 / 60.0);
        Assert.True(engine.Save.Punch.Log10 > 30);
        Assert.True(double.IsFinite(engine.Save.Punch.Log10));
        Assert.False(string.IsNullOrEmpty(engine.Save.Punch.Format(CultureInfo.InvariantCulture)));
        engine.Tick(double.NaN);
        engine.Tick(-5);
        engine.Tick(1e9); // a giant hiccup counts as one second at most
        Assert.True(double.IsFinite(engine.Save.Punch.Log10));
    }
}

public sealed class PunchStorageTests
{
    [Fact]
    public void Saves_round_trip_and_tampering_is_detected()
    {
        using var folder = new TempFolder();
        var storage = new PunchStorage(folder.Path);
        var save = new PunchSave { Punch = BigNum.FromLog10(500), Clicks = 42 }.Normalize();
        save.Producers["robot"] = 7;
        storage.Save(save);
        var loaded = storage.Load();
        Assert.Equal(500, loaded.Punch.Log10, 6);
        Assert.Equal(42, loaded.Clicks);
        Assert.Equal(7, loaded.Count("robot"));

        storage.Save(loaded); // now a backup exists
        File.WriteAllText(storage.SavePath, File.ReadAllText(storage.SavePath).Replace("42", "99999999"));
        var restored = storage.Load();
        Assert.Equal("pf.save.restored", storage.LoadNote);
        Assert.Equal(42, restored.Clicks);

        File.WriteAllText(storage.SavePath, "{broken");
        File.WriteAllText(storage.BackupPath, "{broken");
        var fresh = storage.Load();
        Assert.Equal("pf.save.damaged", storage.LoadNote);
        Assert.Equal(0, fresh.Clicks);
    }

    [Fact]
    public void Export_and_import_use_a_checked_code()
    {
        var save = new PunchSave { Clicks = 7, FameEarned = 12 }.Normalize();
        var code = PunchStorage.Export(save);
        Assert.StartsWith("PUNCHFACTORY1:", code);
        Assert.True(PunchStorage.TryImport(code, out var back));
        Assert.Equal(7, back.Clicks);
        Assert.Equal(12, back.FameEarned);
        Assert.False(PunchStorage.TryImport(code[..^4] + "AAAA", out _));
        Assert.False(PunchStorage.TryImport("hello", out _));
    }

    [Fact]
    public void Edited_values_are_repaired()
    {
        var save = new PunchSave { Clicks = -5, FameEarned = double.NaN, MaxCombo = -1, Environment = "" }.Normalize();
        save.Producers["rookie"] = -3;
        save.Equipped[CosmeticSlot.Glove] = "glove_galaxy"; // not owned
        save.Normalize();
        Assert.Equal(0, save.Clicks);
        Assert.Equal(0, save.FameEarned);
        Assert.Equal("garage", save.Environment);
        Assert.False(save.Producers.ContainsKey("rookie"));
        Assert.Equal("glove_red", save.Equipped[CosmeticSlot.Glove]);
    }

    [Fact]
    public void Balance_data_is_written_once_and_repaired()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "balance.json");
        var config = BalanceConfig.Load(path);
        Assert.True(File.Exists(path));
        Assert.Equal(11, config.Producers.Count);
        Assert.Equal(22, config.Upgrades.Count);
        Assert.Equal(6, config.Abilities.Count);
        Assert.Equal(8, config.Environments.Count);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"BaseCost\": 15,", "\"BaseCost\": -15,"));
        Assert.Equal(1, BalanceConfig.Load(path).Producers[0].BaseCost);
        File.WriteAllText(path, "not json");
        Assert.Equal(11, BalanceConfig.Load(path).Producers.Count);
    }
}

public sealed class PunchFactoryPageTests
{
    private static PunchFactoryPage Page(TempFolder folder, Action<PunchSave>? setup = null)
    {
        var storage = new PunchStorage(folder.Path);
        if (setup is not null)
        {
            var save = new PunchSave().Normalize();
            setup(save);
            storage.Save(save);
        }
        return new PunchFactoryPage(storage, BalanceConfig.Default().Normalize(), new FixedRandom(0.99));
    }

    [Fact]
    public void The_game_page_renders_and_reacts()
    {
        using var folder = new TempFolder();
        UiTestHost.RunAsync(async () =>
        {
            var page = Page(folder, s => { s.Punch = 2e6; s.EarnedRun = 5e7; s.EarnedAll = 5e7; s.Producers["rookie"] = 12; s.Producers["sparring"] = 5; s.Producers["trainer"] = 2; s.Producers["doubleend"] = 1; });
            UiTestHost.Render(page, 1500, 900);
            for (var i = 0; i < 30; i++) { page.Engine.Punch(); page.Step(1 / 60.0); }
            page.Engine.SpawnEvent("glove");
            page.Step(0.2);
            await Task.Delay(300);
            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1500, 900), "punch-page-wide");
            Assert.Equal("wide", page.LayoutMode);
            Assert.False(string.IsNullOrWhiteSpace(page.StatusLine));

            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 900, 1300), "punch-page-narrow");
            Assert.Equal("narrow", page.LayoutMode);

            var before = page.Engine.Save.Count("rookie");
            Assert.True(page.Engine.BuyProducer("rookie", BuyMode.Ten));
            Assert.Equal(before + 10, page.Engine.Save.Count("rookie"));
            page.Shutdown();
            Assert.True(File.Exists(new PunchStorage(folder.Path).SavePath));
        }, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Offline_report_and_every_language_and_theme_render()
    {
        using var folder = new TempFolder();
        UiTestHost.Run(() =>
        {
            var page = Page(folder, s => { s.Producers["sparring"] = 10; s.LastSaveUtc = DateTime.UtcNow.AddHours(-2); });
            UiTestHost.Render(page, 1400, 900);
            try
            {
                foreach (var (code, _, _) in Loc.Languages)
                {
                    Loc.Apply(code);
                    page.Step(0.05);
                    UiTestHost.Render(page, 1400, 900);
                }
                foreach (var (theme, _, _, _) in Themes.All)
                    foreach (var dark in new[] { false, true })
                    {
                        App.ApplyTheme(theme, dark);
                        UiTestHost.Render(page, 1400, 900);
                    }
            }
            finally
            {
                Loc.Apply("de");
                App.ApplyTheme(Themes.Standard, true);
                page.Shutdown();
            }
        });
    }

    [Fact]
    public void Every_punch_factory_text_exists_in_all_languages()
    {
        var placeholder = new Regex(@"\{\d\}");
        foreach (var (key, texts) in PunchFactoryStrings.Table)
        {
            Assert.Equal(Loc.Languages.Length, texts.Length);
            var expected = placeholder.Matches(texts[0]).Select(m => m.Value).Distinct().OrderBy(v => v).ToList();
            foreach (var text in texts)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), key);
                Assert.True(expected.SequenceEqual(placeholder.Matches(text).Select(m => m.Value).Distinct().OrderBy(v => v)), $"{key}: {text}");
            }
        }
        var config = BalanceConfig.Default();
        var keys = new List<string>();
        keys.AddRange(config.Producers.SelectMany(p => new[] { "pf.prod." + p.Id, "pf.prod." + p.Id + ".desc" }));
        keys.AddRange(config.Upgrades.SelectMany(u => new[] { "pf.up." + u.Id, "pf.up." + u.Id + ".desc", "pf.eff." + u.Effect }));
        keys.AddRange(config.Abilities.SelectMany(a => new[] { "pf.ab." + a.Id, "pf.ab." + a.Id + ".desc" }));
        keys.AddRange(config.Abilities.Where(a => a.Duration > 0 && a.Id != "flurry").Select(a => "pf.buff." + a.Id));
        keys.AddRange(new[] { "pf.buff.drone", "pf.buff.bonusbag", "pf.buff.motivation" });
        keys.AddRange(config.Events.Select(e => "pf.ev." + e.Id));
        keys.AddRange(config.Environments.SelectMany(e => new[] { "pf.env." + e.Id, "pf.env." + e.Id + ".desc" }));
        keys.AddRange(config.PrestigeUpgrades.SelectMany(p => new[] { "pf.pr." + p.Id, "pf.pr." + p.Id + ".desc" }));
        keys.AddRange(PunchContent.Cosmetics.Select(c => "pf.cos." + c.Id));
        keys.AddRange(Enum.GetValues<CosmeticSlot>().Select(s => "pf.slot." + s));
        keys.AddRange(Enum.GetValues<Goal>().Select(g => "pf.goal." + g));
        keys.AddRange(Enum.GetValues<ProducerKind>().Select(k => "pf.kind." + k));
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ViennaAtomicClock", "PunchFactory"));
        Assert.True(Directory.Exists(source), source);
        keys.AddRange(Directory.GetFiles(source, "*.cs").SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"(pf\\.[A-Za-z0-9_.]+[A-Za-z0-9])\"").Select(m => m.Groups[1].Value)));
        var missing = keys.Distinct().Where(k => !PunchFactoryStrings.Table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing: " + string.Join(", ", missing));
    }
}
