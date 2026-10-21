using System.IO;
using System.Windows.Controls;
using ViennaAtomicClock;
using ViennaAtomicClock.Treeman;
using Xunit;

namespace Freedom.Tests;

public sealed class TreemanCoinTests
{
    private static GameManager Playing(MemorySave save, ulong seed = 42)
    {
        var config = new TreemanConfig { Player = { CountdownSeconds = 0 } }.Normalize();
        var game = new GameManager(config, save, () => seed);
        game.StartRound(seed);
        return game;
    }

    [Fact]
    public void Every_point_becomes_one_coin_at_the_end_of_a_round()
    {
        var save = new MemorySave();
        save.Data.Coins = 10;
        var game = Playing(save);
        for (var i = 0; i < 17; i++) game.Chop(game.Tree[1] == Side.Left ? Side.Right : Side.Left);
        Assert.Equal(10, save.Data.Coins); // nothing during the round
        game.Tick(60);
        Assert.Equal(27, save.Data.Coins);
        Assert.Equal(17, game.LastResult!.Value.CoinsEarned);
        game.Tick(5);
        game.EndRound(GameOverReason.Branch);
        Assert.Equal(27, save.Data.Coins); // only once per round
    }

    [Fact]
    public void Coins_skins_and_the_equipped_skin_are_saved()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "treeman.json");
        var save = new JsonSaveSystem(path);
        Assert.Equal(new[] { SkinCatalog.DefaultId }, save.Data.OwnedSkins);
        save.Data.Coins = 1234;
        save.Data.OwnedSkins.Add("trump");
        save.Data.EquippedSkin = "trump";
        save.Save();
        var loaded = new JsonSaveSystem(path).Data;
        Assert.Equal(1234, loaded.Coins);
        Assert.Contains("trump", loaded.OwnedSkins);
        Assert.Equal("trump", loaded.EquippedSkin);

        // Old or edited files: unknown skins vanish, the default skin always stays, negative coins become 0.
        File.WriteAllText(path, """{ "Highscore": 5, "Coins": -9, "OwnedSkins": ["nope", "obama"], "EquippedSkin": "nope" }""");
        var repaired = new JsonSaveSystem(path).Data;
        Assert.Equal(0, repaired.Coins);
        Assert.Equal(new[] { "obama", SkinCatalog.DefaultId }, repaired.OwnedSkins);
        Assert.Equal(SkinCatalog.DefaultId, repaired.EquippedSkin);
        File.WriteAllText(path, """{ "Highscore": 5 }""");
        Assert.Equal(new[] { SkinCatalog.DefaultId }, new JsonSaveSystem(path).Data.OwnedSkins);
    }
}

public sealed class LootboxTests
{
    private static LootboxSystem Boxes(MemorySave save, int seed = 1, LootSettings? settings = null) =>
        new(settings ?? new TreemanConfig().Normalize().Loot, save, new Random(seed));

    [Fact]
    public void A_box_needs_enough_coins()
    {
        var save = new MemorySave();
        save.Data.Coins = 999;
        var boxes = Boxes(save);
        Assert.False(boxes.CanOpen);
        Assert.Equal(1, boxes.MissingCoins);
        Assert.Null(boxes.Open());
        Assert.Equal(999, save.Data.Coins);
        Assert.Single(save.Data.OwnedSkins);
    }

    [Fact]
    public void Opening_costs_1000_and_adds_the_skin()
    {
        var save = new MemorySave();
        save.Data.Coins = 1500;
        var result = Boxes(save, 3).Open()!;
        Assert.Equal(LootboxSystem.ReelLength, result.Reel.Count);
        Assert.Same(result.Skin, result.Reel[result.WinnerIndex]);
        if (result.Duplicate)
        {
            Assert.Equal(500 + result.Refund, save.Data.Coins);
        }
        else
        {
            Assert.Equal(500, save.Data.Coins);
            Assert.Contains(result.Skin.Id, save.Data.OwnedSkins);
        }
        Assert.True(save.Saves > 0);
    }

    [Fact]
    public void Duplicates_give_coins_back_instead()
    {
        var save = new MemorySave();
        foreach (var skin in SkinCatalog.All) if (!save.Data.OwnedSkins.Contains(skin.Id)) save.Data.OwnedSkins.Add(skin.Id);
        save.Data.Coins = 1000;
        var refunds = new TreemanConfig().Normalize().Loot.DuplicateRefund;
        var result = Boxes(save).Open()!;
        Assert.True(result.Duplicate);
        Assert.Equal(refunds[(int)result.Skin.Rarity], result.Refund);
        Assert.Equal(result.Refund, save.Data.Coins);
        Assert.Equal(SkinCatalog.All.Count, save.Data.OwnedSkins.Count);
    }

    [Fact]
    public void Rarities_follow_the_configured_weights()
    {
        var boxes = Boxes(new MemorySave(), 99);
        var counts = new int[5];
        const int rolls = 40_000;
        for (var i = 0; i < rolls; i++) counts[(int)boxes.RollRarity()]++;
        foreach (var rarity in Enum.GetValues<SkinRarity>())
            Assert.InRange(counts[(int)rarity] * 100.0 / rolls, boxes.ChanceOf(rarity) - 1.2, boxes.ChanceOf(rarity) + 1.2);
        Assert.Equal(100, Enum.GetValues<SkinRarity>().Sum(boxes.ChanceOf), 6);
    }

    [Fact]
    public void Every_skin_can_be_won_eventually()
    {
        var save = new MemorySave { Data = { Coins = 1_000_000_000 } };
        var boxes = Boxes(save, 5);
        for (var i = 0; i < 5000 && save.Data.OwnedSkins.Count < SkinCatalog.All.Count; i++) boxes.Open();
        Assert.Equal(SkinCatalog.All.Count, save.Data.OwnedSkins.Count);
    }

    [Fact]
    public void Only_owned_skins_can_be_equipped()
    {
        var save = new MemorySave();
        var boxes = Boxes(save);
        boxes.Equip("trump");
        Assert.Equal(SkinCatalog.DefaultId, save.Data.EquippedSkin);
        save.Data.OwnedSkins.Add("trump");
        boxes.Equip("trump");
        Assert.Equal("trump", save.Data.EquippedSkin);
    }

    [Fact]
    public void Broken_loot_settings_are_repaired()
    {
        var config = new TreemanConfig { Loot = { BoxPrice = -5, RarityWeights = new[] { 1.0, double.NaN }, DuplicateRefund = new[] { 1 }, SpinSeconds = 0 } }.Normalize();
        Assert.Equal(0, config.Loot.BoxPrice);
        Assert.Equal(new LootSettings().RarityWeights, config.Loot.RarityWeights);
        Assert.Equal(5, config.Loot.DuplicateRefund.Length);
        Assert.Equal(0.5, config.Loot.SpinSeconds);
    }
}

public sealed class SkinCatalogTests
{
    [Fact]
    public void There_are_30_distinct_skins_in_every_rarity()
    {
        Assert.Equal(30, SkinCatalog.All.Count);
        Assert.Equal(30, SkinCatalog.All.Select(s => s.Id).Distinct().Count());
        foreach (var rarity in Enum.GetValues<SkinRarity>()) Assert.NotEmpty(SkinCatalog.OfRarity(rarity));
        Assert.Equal(SkinCatalog.DefaultId, SkinCatalog.Get("unknown").Id);
    }

    [Fact]
    public void The_requested_skins_are_in_the_boxes()
    {
        var names = SkinCatalog.All.Select(s => s.FixedName).Where(n => n is not null).ToList();
        foreach (var wanted in new[] { "Donald Trump", "Barack Obama", "Randy Marsh", "Peter Griffin", "Nettspend", "OsamaSon", "bleood" }) Assert.Contains(wanted, names);
    }

    [Fact]
    public void Every_skin_has_a_name_in_every_language()
    {
        foreach (var skin in SkinCatalog.All.Where(s => s.FixedName is null))
            Assert.True(TreemanStrings.Table.ContainsKey(skin.NameKey), skin.NameKey);
        foreach (var key in Enum.GetValues<SkinRarity>().Select(r => "tm.rarity." + r.ToString().ToLowerInvariant())
                     .Concat(Enum.GetValues<MapTheme>().Select(t => "tm.map." + t.ToString().ToLowerInvariant()))
                     .Concat(Enum.GetValues<DayTime>().Select(t => "tm.time." + t.ToString().ToLowerInvariant())))
            Assert.True(TreemanStrings.Table.ContainsKey(key), key);
    }
}

public sealed class TreemanSceneTests
{
    [Fact]
    public void The_shuffle_never_repeats_the_map_and_reaches_all_twelve()
    {
        var random = new Random(8);
        SceneStyle? current = null;
        var seen = new HashSet<(MapTheme, DayTime)>();
        for (var i = 0; i < 400; i++)
        {
            var next = SceneStyle.Shuffle(random, current);
            Assert.NotSame(current, next);
            seen.Add((next.Theme, next.Time));
            current = next;
        }
        Assert.Equal(12, seen.Count);
    }

    [Fact]
    public void A_death_shuffles_the_map_for_the_next_round()
    {
        UiTestHost.Run(() =>
        {
            var page = new TreemanPage(new TreemanConfig { Player = { CountdownSeconds = 0 } }.Normalize(), new MemorySave()) { SceneRandom = new Random(2) };
            var first = page.View.Scene;
            page.Game.StartRound(4);
            Assert.Same(first, page.View.Scene);
            page.Game.Tick(60); // time runs out
            Assert.Same(first, page.View.Scene); // the game over screen keeps the map of the round
            page.Game.StartRound(5);
            Assert.NotSame(first, page.View.Scene);
            page.Shutdown();
        });
    }
}

public sealed class TreemanLootRenderTests
{
    [Fact]
    public void Every_map_and_time_of_day_renders()
    {
        UiTestHost.Run(() =>
        {
            var save = new MemorySave();
            var page = new TreemanPage(new TreemanConfig { Player = { CountdownSeconds = 0 } }.Normalize(), save);
            page.Game.StartRound(11);
            for (var i = 0; i < 6; i++) page.Game.Chop(page.Game.Tree[1] == Side.Left ? Side.Right : Side.Left);
            page.Step(0.5);
            foreach (var theme in Enum.GetValues<MapTheme>())
                foreach (var time in Enum.GetValues<DayTime>())
                {
                    page.View.Scene = SceneStyle.Get(theme, time);
                    page.Ui.SetScene(page.View.Scene);
                    page.Step(0.016);
                    UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 720), $"treeman-map-{theme}-{time}".ToLowerInvariant());
                }
            page.Shutdown();
        });
    }

    [Fact]
    public void Every_skin_renders_in_the_game_and_in_the_collection()
    {
        UiTestHost.RunAsync(async () =>
        {
            var save = new MemorySave();
            foreach (var skin in SkinCatalog.All) if (!save.Data.OwnedSkins.Contains(skin.Id)) save.Data.OwnedSkins.Add(skin.Id);
            var page = new TreemanPage(new TreemanConfig { Player = { CountdownSeconds = 0 } }.Normalize(), save);
            page.Game.StartRound(3);
            foreach (var skin in SkinCatalog.All)
            {
                page.View.Skin = skin;
                page.Game.Chop(page.Game.Tree[1] == Side.Left ? Side.Right : Side.Left);
                page.Step(0.03); // mid swing
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 640, 360), "treeman-skin-" + skin.Id);
            }
            page.Game.ShowMainMenu();
            page.Ui.OpenSkins();
            Assert.Equal("skins", page.Ui.ShownPanel);
            UiTestHost.Render(page, 1280, 900);
            await Task.Delay(400); // let the panel fade in
            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 900), "treeman-skins");
            page.Ui.CloseOptions();

            var locked = new TreemanPage(new TreemanConfig().Normalize(), new MemorySave());
            locked.Ui.OpenSkins();
            UiTestHost.Render(locked, 1280, 800);
            page.Shutdown();
            locked.Shutdown();
        }, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void The_box_reel_spins_stops_on_the_won_skin_and_can_be_skipped()
    {
        UiTestHost.RunAsync(async () =>
        {
            var save = new MemorySave { Data = { Coins = 2500 } };
            var page = new TreemanPage(new TreemanConfig().Normalize(), save);
            page.Ui.OpenShop();
            Assert.Equal("shop", page.Ui.ShownPanel);
            UiTestHost.Render(page, 1280, 720);
            await Task.Delay(400);
            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 720), "treeman-shop");
            var open = FindButton(page, b => b.IsEnabled && b is GameButton g && g.Text.Contains(1000.ToString("N0", Loc.Culture)));
            open.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(page.Ui.Loot.IsSpinning);
            page.Step(1.5);
            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 720), "treeman-shop-spinning");
            Assert.False(page.Ui.HandleSubPanelKey(System.Windows.Input.Key.A));
            Assert.True(page.Ui.HandleSubPanelKey(System.Windows.Input.Key.Escape)); // skips, does not close
            Assert.Equal("shop", page.Ui.ShownPanel);
            page.Step(0.05);
            Assert.False(page.Ui.Loot.IsSpinning);
            Assert.NotNull(page.Ui.Loot.LastResult);
            page.Step(0.3);
            UiTestHost.Render(page, 1280, 720);
            await Task.Delay(400);
            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 720), "treeman-shop-result");
            var won = page.Ui.Loot.LastResult!;
            Assert.Contains(won.Skin.Id, save.Data.OwnedSkins);
            Assert.Equal(1500 + won.Refund, save.Data.Coins); // price paid once, duplicates refunded
            Assert.True(page.Ui.HandleSubPanelKey(System.Windows.Input.Key.Escape));
            Assert.Equal("menu", page.Ui.ShownPanel);
            page.Shutdown();
        }, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void The_wooden_panels_render_in_every_state()
    {
        UiTestHost.RunAsync(async () =>
        {
            var save = new MemorySave { Data = { Coins = 4321, Highscore = 12 } };
            var page = new TreemanPage(new TreemanConfig { Player = { CountdownSeconds = 3 } }.Normalize(), save);
            async Task Snap(string name, double w = 1280, double h = 720)
            {
                UiTestHost.Render(page, w, h);
                await Task.Delay(350); // let the board pop in
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, w, h), name);
            }
            await Snap("treeman-ui-menu", 1280, 800);
            page.Game.StartRound(21);
            page.Step(0.2);
            await Snap("treeman-ui-countdown");
            page.Step(3);
            for (var i = 0; i < 20; i++)
            {
                page.Game.Chop(page.Game.Tree[1] == Side.Left ? Side.Right : Side.Left);
                page.Step(0.12);
            }
            page.Step(9); // time runs low
            await Snap("treeman-ui-playing");
            page.Game.Pause();
            await Snap("treeman-ui-pause");
            page.Ui.OpenOptions();
            await Snap("treeman-ui-options");
            page.Ui.CloseOptions();
            page.Game.Resume();
            page.Game.Tick(60);
            await Snap("treeman-ui-gameover", 1280, 800);
            await Snap("treeman-ui-gameover-small", 800, 520); // boards shrink in small windows
            page.Shutdown();
        }, TimeSpan.FromSeconds(180));
    }

    private static Button FindButton(System.Windows.DependencyObject root, Func<Button, bool> match)
    {
        var stack = new Stack<System.Windows.DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is Button button && match(button)) return button;
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++) stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(current, i));
        }
        throw new InvalidOperationException("button not found");
    }
}
