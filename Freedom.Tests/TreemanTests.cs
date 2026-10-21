using System.IO;
using System.Text.RegularExpressions;
using ViennaAtomicClock;
using ViennaAtomicClock.Treeman;
using Xunit;

namespace Freedom.Tests;

/// <summary>Save system in memory (no files).</summary>
internal sealed class MemorySave : ISaveSystem
{
    public TreemanSaveData Data { get; } = new();
    public int Saves;

    public bool SubmitScore(int score)
    {
        if (score <= Data.Highscore) return false;
        Data.Highscore = score;
        Saves++;
        return true;
    }

    public void Save() => Saves++;
}

public sealed class TreemanGameTests
{
    private static TreemanConfig Config(Action<TreemanConfig>? change = null)
    {
        var config = new TreemanConfig();
        config.Player.CountdownSeconds = 0; // straight into Playing
        change?.Invoke(config);
        return config.Normalize();
    }

    private static GameManager Playing(TreemanConfig? config = null, MemorySave? save = null, ulong seed = 42)
    {
        var game = new GameManager(config ?? Config(), save ?? new MemorySave(), () => seed);
        game.StartRound(seed);
        Assert.Equal(GameState.Playing, game.State);
        return game;
    }

    /// <summary>The side whose next segment (index 1, about to slide down) has no branch.</summary>
    private static Side SafeSide(GameManager game) => game.Tree[1] == Side.Left ? Side.Right : Side.Left;

    [Fact]
    public void A_chop_raises_the_score_exactly_once()
    {
        var game = Playing();
        var chops = 0;
        game.Chopped += _ => chops++;
        Assert.True(game.Chop(SafeSide(game)));
        Assert.Equal(1, game.Score);
        Assert.Equal(1, chops);
    }

    [Fact]
    public void A_chop_refills_time_but_never_above_the_maximum()
    {
        var game = Playing();
        game.Chop(SafeSide(game));
        Assert.Equal(100, game.Time.Current); // already full: capped
        game.Tick(2); // 8 per second → 84
        Assert.Equal(84, game.Time.Current, 6);
        game.Chop(SafeSide(game));
        Assert.Equal(88, game.Time.Current, 6);
        for (var i = 0; i < 10; i++) game.Chop(SafeSide(game));
        Assert.Equal(100, game.Time.Current, 6);
    }

    [Fact]
    public void A_branch_on_the_player_side_ends_the_round()
    {
        var game = Playing();
        // Chop safely until a branch is about to slide down, then step under it.
        while (game.Tree[1] == Side.None) game.Chop(Side.Left);
        var danger = game.Tree[1];
        RoundResult? ended = null;
        game.RoundEnded += r => ended = r;
        game.Chop(danger);
        Assert.Equal(GameState.GameOver, game.State);
        Assert.Equal(GameOverReason.Branch, game.LastReason);
        Assert.NotNull(ended);
        Assert.False(game.Chop(Side.Left)); // no points after the end
        Assert.Equal(ended!.Value.Score, game.Score);
    }

    [Fact]
    public void A_branch_on_the_other_side_is_safe()
    {
        var game = Playing();
        while (game.Tree[1] == Side.None) game.Chop(Side.Left);
        game.Chop(game.Tree[1].Opposite());
        Assert.Equal(GameState.Playing, game.State);
    }

    [Fact]
    public void An_empty_time_bar_ends_the_round_once()
    {
        var game = Playing();
        var ends = 0;
        game.RoundEnded += _ => ends++;
        game.Tick(20); // 8/s × 20 s > 100
        Assert.Equal(GameState.GameOver, game.State);
        Assert.Equal(GameOverReason.TimeUp, game.LastReason);
        game.Tick(5);
        game.EndRound(GameOverReason.Branch);
        Assert.Equal(1, ends); // never a second game over
        Assert.Equal(0, game.Time.Current);
    }

    [Fact]
    public void Time_stands_still_while_paused()
    {
        var game = Playing();
        game.Tick(1);
        game.Pause();
        var before = game.Time.Current;
        game.Tick(5);
        Assert.Equal(before, game.Time.Current);
        Assert.False(game.Chop(Side.Left));
        game.Resume();
        Assert.Equal(GameState.Playing, game.State);
        game.Tick(1);
        Assert.True(game.Time.Current < before);
    }

    [Fact]
    public void Restart_resets_score_time_and_tree()
    {
        var game = Playing(seed: 7);
        var startTree = Enumerable.Range(0, game.Tree.Count).Select(i => game.Tree[i]).ToArray();
        for (var i = 0; i < 12 && game.State == GameState.Playing; i++) game.Chop(SafeSide(game));
        game.Tick(3);
        game.Pause();
        game.Restart(7);
        Assert.Equal(GameState.Playing, game.State);
        Assert.Equal(0, game.Score);
        Assert.Equal(0, game.Level);
        Assert.Equal(100, game.Time.Current);
        Assert.Equal(Side.Left, game.Player.Side);
        Assert.Equal(startTree, Enumerable.Range(0, game.Tree.Count).Select(i => game.Tree[i]).ToArray());
    }

    [Fact]
    public void Highscore_only_grows()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "treeman.json");
        var save = new JsonSaveSystem(path);
        Assert.True(save.SubmitScore(30));
        Assert.False(save.SubmitScore(12));
        Assert.False(save.SubmitScore(30));
        Assert.Equal(30, new JsonSaveSystem(path).Data.Highscore); // survives an app restart
        Assert.True(save.SubmitScore(31));
        File.WriteAllText(path, "{ broken");
        Assert.Equal(0, new JsonSaveSystem(path).Data.Highscore); // broken file → defaults, no crash

        var memory = new MemorySave();
        memory.Data.Highscore = 50;
        var game = Playing(save: memory);
        game.Tick(30);
        Assert.Equal(50, game.Highscore);
        Assert.False(game.LastResult!.Value.NewHighscore);
    }

    [Fact]
    public void A_new_record_is_reported_with_the_previous_best()
    {
        var save = new MemorySave();
        save.Data.Highscore = 2;
        var game = Playing(save: save);
        for (var i = 0; i < 5; i++) game.Chop(SafeSide(game));
        game.Tick(30);
        var result = game.LastResult!.Value;
        Assert.True(result.NewHighscore);
        Assert.Equal(2, result.PreviousHighscore);
        Assert.Equal(5, result.Highscore);
    }

    [Fact]
    public void Difficulty_rises_in_smooth_levels()
    {
        var game = Playing();
        var levels = new List<int>();
        game.LevelChanged += levels.Add;
        for (var i = 0; i < 50 && game.State == GameState.Playing; i++)
        {
            game.Chop(SafeSide(game));
            game.Tick(0.05);
        }
        Assert.Equal(new[] { 1, 2 }, levels);
        Assert.Equal(8 + 2 * 1.6, game.Time.TargetDrain, 6);
        Assert.True(game.Time.Drain < game.Time.TargetDrain); // still blending, no jump
        game.Tick(3);
        Assert.Equal(game.Time.TargetDrain, game.Time.Drain, 6);
    }

    [Fact]
    public void Invalid_state_changes_are_ignored()
    {
        var game = new GameManager(Config(c => c.Player.CountdownSeconds = 3), new MemorySave(), () => 1);
        Assert.Equal(GameState.MainMenu, game.State);
        Assert.False(game.Chop(Side.Left));
        game.Resume();
        Assert.Equal(GameState.MainMenu, game.State);
        game.StartRound();
        Assert.Equal(GameState.Countdown, game.State);
        Assert.False(game.Chop(Side.Left)); // no chopping during the countdown
        game.Pause();
        game.Tick(10);
        Assert.Equal(GameState.Paused, game.State); // the countdown waits too
        game.Resume();
        Assert.Equal(GameState.Countdown, game.State);
        game.Tick(3.01);
        Assert.Equal(GameState.Playing, game.State);
        game.ShowMainMenu();
        Assert.Equal(GameState.MainMenu, game.State);
        Assert.Equal(0, game.Score);
    }
}

public sealed class TreeGeneratorTests
{
    private static List<Side> Generate(GenerationRules rules, ulong seed, int count, int level = 0)
    {
        var generator = new TreeGenerator(rules, seed);
        generator.Reset(seed);
        return Enumerable.Range(0, count).Select(_ => generator.Next(level)).ToList();
    }

    [Fact]
    public void Safe_start_segments_have_no_branches()
    {
        var rules = new GenerationRules { SafeStartSegments = 5 };
        for (ulong seed = 0; seed < 200; seed++)
            Assert.All(Generate(rules, seed, 5), s => Assert.Equal(Side.None, s));
    }

    [Fact]
    public void Generated_trees_never_contain_forbidden_patterns()
    {
        var rules = new GenerationRules();
        for (ulong seed = 0; seed < 300; seed++)
            foreach (var level in new[] { 0, 5, 40 })
            {
                var sequence = Generate(rules, seed, 400, level);
                Assert.True(TreeGenerator.IsFair(sequence, rules, out var problem), $"seed {seed}, level {level}: {problem}");
                Assert.Contains(sequence, s => s != Side.None);
            }
    }

    [Fact]
    public void The_validator_rejects_unfair_sequences()
    {
        var rules = new GenerationRules { SafeStartSegments = 1, MaxSameSideInRow = 2 };
        Assert.False(TreeGenerator.IsFair(new[] { Side.Left }, rules, out _));
        Assert.False(TreeGenerator.IsFair(new[] { Side.None, Side.Left, Side.Right }, rules, out _)); // no gap
        Assert.False(TreeGenerator.IsFair(new[] { Side.None, Side.Left, Side.Left, Side.Left }, rules, out _));
        Assert.True(TreeGenerator.IsFair(new[] { Side.None, Side.Left, Side.None, Side.Right, Side.Right }, rules, out _));
    }

    [Fact]
    public void The_same_seed_gives_the_same_tree()
    {
        var rules = new GenerationRules();
        Assert.Equal(Generate(rules, 1234, 200), Generate(rules, 1234, 200));
        Assert.NotEqual(Generate(rules, 1234, 200), Generate(rules, 4321, 200));
    }

    [Fact]
    public void Branches_get_more_frequent_with_the_level()
    {
        var rules = new GenerationRules();
        double Share(int level) => Enumerable.Range(0, 40).SelectMany(s => Generate(rules, (ulong)s, 300, level)).Count(x => x != Side.None) / (40 * 300.0);
        Assert.True(Share(10) > Share(0) + 0.05, $"{Share(0):F2} → {Share(10):F2}");
        Assert.True(new TreeGenerator(rules, 1).BranchChance(1000) <= rules.MaxBranchChance);
    }

    [Fact]
    public void There_is_always_a_way_out()
    {
        // Only the segment sliding down is dangerous, and it has a branch on at most one side.
        var game = new GameManager(new TreemanConfig { Player = { CountdownSeconds = 0 } }, new MemorySave(), () => 99);
        game.StartRound(99);
        for (var i = 0; i < 2000; i++)
        {
            var next = game.Tree[1];
            Assert.True(game.Chop(next == Side.Left ? Side.Right : Side.Left));
            game.Tick(0.01);
            Assert.Equal(GameState.Playing, game.State);
        }
        Assert.Equal(2000, game.Score);
    }
}

public sealed class TreemanInputTests
{
    [Fact]
    public void Fast_presses_are_buffered_and_none_are_lost()
    {
        var config = new TreemanConfig { Player = { CountdownSeconds = 0, InputCooldownSeconds = 0.08, InputBufferSize = 3 } }.Normalize();
        var game = new GameManager(config, new MemorySave(), () => 5);
        var input = new InputController(config.Player);
        game.StartRound(5);
        game.Time.Refill(100);
        // Four presses within one frame: one now, three buffered.
        for (var i = 0; i < 4; i++) input.Press(InputAction.Left, game);
        Assert.Equal(1, game.Score);
        Assert.Equal(3, input.Pending);
        for (var frame = 0; frame < 30 && input.Pending > 0; frame++) input.Update(1 / 60.0, game);
        Assert.True(game.Score == 4 || game.State == GameState.GameOver);
        // A full buffer drops extra presses instead of growing without limit.
        input.Clear();
        if (game.State == GameState.Playing)
        {
            for (var i = 0; i < 20; i++) input.Press(InputAction.Right, game);
            Assert.True(input.Pending <= 4);
        }
    }

    [Fact]
    public void Keys_map_to_actions_and_only_count_while_playing()
    {
        Assert.Equal(InputAction.Left, InputController.Map(System.Windows.Input.Key.A));
        Assert.Equal(InputAction.Left, InputController.Map(System.Windows.Input.Key.Left));
        Assert.Equal(InputAction.Right, InputController.Map(System.Windows.Input.Key.D));
        Assert.Equal(InputAction.Right, InputController.Map(System.Windows.Input.Key.Right));
        Assert.Equal(InputAction.Confirm, InputController.Map(System.Windows.Input.Key.Space));
        Assert.Equal(InputAction.Confirm, InputController.Map(System.Windows.Input.Key.Enter));
        Assert.Equal(InputAction.Pause, InputController.Map(System.Windows.Input.Key.Escape));
        Assert.Null(InputController.Map(System.Windows.Input.Key.Q));

        var config = new TreemanConfig { Player = { CountdownSeconds = 0 } }.Normalize();
        var game = new GameManager(config, new MemorySave(), () => 3);
        var input = new InputController(config.Player);
        input.Press(InputAction.Left, game);
        Assert.Equal(GameState.MainMenu, game.State);
        input.Press(InputAction.Confirm, game);
        Assert.Equal(GameState.Playing, game.State);
        input.Press(InputAction.Pause, game);
        Assert.Equal(GameState.Paused, game.State);
        input.Press(InputAction.Left, game);
        Assert.Equal(0, game.Score);
        input.Press(InputAction.Pause, game);
        Assert.Equal(GameState.Playing, game.State);
    }

    [Fact]
    public void Config_values_are_repaired()
    {
        var config = new TreemanConfig { Time = { MaxEnergy = double.NaN, RefillPerChop = -5 }, Player = { InputBufferSize = 99 } }.Normalize();
        Assert.Equal(100, config.Time.MaxEnergy);
        Assert.Equal(0, config.Time.RefillPerChop);
        Assert.Equal(10, config.Player.InputBufferSize);
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "cfg.json");
        var loaded = TreemanConfig.Load(path);
        Assert.True(File.Exists(path)); // written once for balancing
        Assert.Equal(8, loaded.Time.StartDrainPerSecond);
    }
}

public sealed class TreemanRenderTests
{
    [Fact]
    public void The_page_renders_every_state_in_every_theme_and_language()
    {
        UiTestHost.Run(() =>
        {
            var save = new MemorySave();
            var page = new TreemanPage(new TreemanConfig { Player = { CountdownSeconds = 3 } }.Normalize(), save);
            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 720), "treeman-menu");
            Assert.Equal("menu", page.Ui.ShownPanel);

            page.Game.StartRound(17);
            page.Step(0.5);
            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 720), "treeman-countdown");
            page.Step(3);
            Assert.Equal(GameState.Playing, page.Game.State);
            for (var i = 0; i < 30 && page.Game.State == GameState.Playing; i++)
            {
                page.Game.Chop(page.Game.Tree[1] == Side.Left ? Side.Right : Side.Left);
                page.Step(1 / 60.0);
            }
            page.Step(0.03);
            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1920, 1080), "treeman-playing");
            page.Game.Pause();
            UiTestHost.Render(page, 1280, 720);
            Assert.Equal("pause", page.Ui.ShownPanel);
            page.Game.Resume();
            while (page.Game.Tree[1] == Side.None) { page.Game.Chop(Side.Left); page.Step(0.01); }
            page.Game.Chop(page.Game.Tree[1]);
            page.Step(0.1);
            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 720), "treeman-gameover");
            Assert.Equal("gameover", page.Ui.ShownPanel);
            page.Ui.OpenOptions();
            UiTestHost.Render(page, 1280, 720);
            page.Ui.CloseOptions();

            // Other sizes and aspect ratios keep the game proportional.
            foreach (var (w, h) in new[] { (1366.0, 768.0), (900.0, 900.0), (2560.0, 1080.0) }) UiTestHost.Render(page, w, h);
            try
            {
                foreach (var (code, _, _) in Loc.Languages)
                {
                    Loc.Apply(code);
                    page.Game.ShowMainMenu();
                    UiTestHost.Render(page, 1280, 720);
                }
                foreach (var (theme, _, _, _) in Themes.All)
                {
                    App.ApplyTheme(theme, true);
                    UiTestHost.Render(new TreemanPage(new TreemanConfig().Normalize(), new MemorySave()), 1280, 720);
                }
            }
            finally
            {
                Loc.Apply("de");
                App.ApplyTheme(Themes.Standard, true);
            }
            page.Shutdown();
        });
    }

    [Fact]
    public void Keys_chop_even_when_the_focus_is_outside_the_game()
    {
        UiTestHost.Run(() =>
        {
            var page = new TreemanPage(new TreemanConfig { Player = { CountdownSeconds = 0 } }.Normalize(), new MemorySave());
            var outside = new System.Windows.Controls.Button { Content = "Apps" };
            var root = new System.Windows.Controls.DockPanel();
            System.Windows.Controls.DockPanel.SetDock(outside, System.Windows.Controls.Dock.Top);
            root.Children.Add(outside);
            root.Children.Add(page);
            var window = new System.Windows.Window { Content = root, Width = 900, Height = 600, ShowActivated = false, Left = -3000 };
            window.PreviewKeyDown += (_, e) => page.HandleKey(e); // like MainWindow
            window.Show();
            try
            {
                void Press(System.Windows.Input.Key key)
                {
                    outside.Focus();
                    var source = System.Windows.PresentationSource.FromVisual(window)!;
                    var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key)
                    {
                        RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
                    };
                    outside.RaiseEvent(args);
                }

                Press(System.Windows.Input.Key.Enter);
                Assert.Equal(GameState.Playing, page.Game.State);
                var expected = 0;
                foreach (var key in new[] { System.Windows.Input.Key.A, System.Windows.Input.Key.D, System.Windows.Input.Key.Left, System.Windows.Input.Key.Right })
                {
                    if (page.Game.State != GameState.Playing) break;
                    page.Step(0.2); // past the input cooldown
                    Press(key);
                    expected++;
                    Assert.True(page.Game.Score == expected || page.Game.State == GameState.GameOver, $"{key}: score {page.Game.Score}");
                }
                Assert.True(expected >= 1);
            }
            finally
            {
                window.Close();
                page.Shutdown();
            }
        });
    }

    [Fact]
    public void Every_treeman_text_exists_in_all_languages()
    {
        var placeholder = new Regex(@"\{\d\}");
        foreach (var (key, texts) in TreemanStrings.Table)
        {
            Assert.Equal(Loc.Languages.Length, texts.Length);
            var expected = placeholder.Matches(texts[0]).Select(m => m.Value).OrderBy(v => v).ToList();
            foreach (var text in texts)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), key);
                Assert.True(expected.SequenceEqual(placeholder.Matches(text).Select(m => m.Value).OrderBy(v => v)), $"{key}: {text}");
            }
        }
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ViennaAtomicClock", "Treeman"));
        Assert.True(Directory.Exists(source), source);
        var keys = Directory.GetFiles(source, "*.cs").SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"(tm\\.[A-Za-z0-9_.]+[A-Za-z0-9])\"").Select(m => m.Groups[1].Value)).Distinct().ToList();
        var missing = keys.Where(k => !TreemanStrings.Table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing: " + string.Join(", ", missing));
    }
}
