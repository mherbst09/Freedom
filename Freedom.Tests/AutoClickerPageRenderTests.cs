using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using ViennaAtomicClock;
using ViennaAtomicClock.AutoClicker;
using Xunit;
using Xunit.Abstractions;

namespace Freedom.Tests;

public sealed class AutoClickerPageRenderTests
{
    private readonly ITestOutputHelper _output;

    public AutoClickerPageRenderTests(ITestOutputHelper output) => _output = output;

    private static AutoClickerPage CreatePage(TempFolder folder, FakeMouse mouse, Action<AutoClickerProfile>? change = null)
    {
        var storage = new AutoClickerStorage(folder.Path);
        var profile = new AutoClickerProfile { Name = "Test" };
        change?.Invoke(profile);
        storage.SaveProfile(profile);
        storage.SaveSettings(new AutoClickerSettings { LastProfile = "Test" });
        return new AutoClickerPage(storage, mouse);
    }

    [Fact]
    public void Page_renders_responsively_in_every_theme()
    {
        using var folder = new TempFolder();
        UiTestHost.Run(() =>
        {
            var page = CreatePage(folder, new FakeMouse());
            foreach (var (width, height, mode) in new[] { (1400.0, 1000.0, "wide"), (900.0, 1000.0, "medium"), (600.0, 1200.0, "narrow") })
            {
                var bitmap = UiTestHost.Render(page, width, height);
                Assert.Equal(mode, page.LayoutMode);
                UiTestHost.SaveIfRequested(bitmap, "clicker-" + mode);
            }
            try
            {
                foreach (var (theme, _, _, _) in Themes.All)
                    foreach (var dark in new[] { true, false })
                    {
                        App.ApplyTheme(theme, dark);
                        var bitmap = UiTestHost.Render(CreatePage(folder, new FakeMouse()), 1300, 950);
                        if (theme is Themes.Standard) UiTestHost.SaveIfRequested(bitmap, $"clicker-{theme}-{(dark ? "dark" : "light")}");
                    }
            }
            finally
            {
                App.ApplyTheme(Themes.Standard, true);
            }
        });
    }

    [Fact]
    public void Every_mode_and_card_renders_in_every_language()
    {
        using var folder = new TempFolder();
        UiTestHost.Run(() =>
        {
            try
            {
                foreach (var (code, _, _) in Loc.Languages)
                {
                    Loc.Apply(code);
                    // Every combination in German, one rich page per other language.
                    foreach (var position in code == "de" ? Enum.GetValues<PositionMode>() : new[] { PositionMode.Sequence })
                        foreach (var repeat in code == "de" ? Enum.GetValues<RepeatMode>() : new[] { RepeatMode.Count })
                        {
                            var page = CreatePage(folder, new FakeMouse(), p =>
                            {
                                p.Position = position;
                                p.Repeat = repeat;
                                p.ClickKind = ClickKind.Double;
                                p.RandomJitter = true;
                                p.IntervalInput = repeat == RepeatMode.Count ? IntervalInput.Time : IntervalInput.Cps;
                                p.Cps = 250;
                                p.Points = new List<ClickPoint> { new(10, 10), new(20, 20, 50) };
                                p.Area = new ScreenArea(0, 0, 100, 80);
                                p.MaxRuntimeEnabled = true;
                                p.MaxClicksEnabled = true;
                            });
                            var bitmap = UiTestHost.Render(page, 1300, 1400);
                            if (code == "de" && repeat == RepeatMode.Count) UiTestHost.SaveIfRequested(bitmap, "clicker-pos-" + position);
                        }
                }
            }
            finally
            {
                Loc.Apply("de");
            }
        });
    }

    [Fact]
    public void Test_run_from_the_page_shows_results_and_unlocks_again()
    {
        using var folder = new TempFolder();
        var mouse = new FakeMouse();
        AutoClickerPage? page = null;
        UiTestHost.Run(() =>
        {
            page = CreatePage(folder, mouse, p => { p.Cps = 500; p.Repeat = RepeatMode.Duration; p.DurationSeconds = 1; });
            UiTestHost.Render(page, 1300, 1000);
            page.StartRun(testMode: true);
        });
        // While the engine runs, the page keeps refreshing on the UI thread.
        var watch = Stopwatch.StartNew();
        while (UiTestHost.Run(() => page!.State) != AutoClickerState.Idle && watch.ElapsedMilliseconds < 5000)
        {
            UiTestHost.Run(() => { UiTestHost.Render(page!, 1300, 1000); });
            Thread.Sleep(30);
        }
        UiTestHost.Run(() =>
        {
            Assert.Equal(AutoClickerState.Idle, page!.State);
            var stats = page.Engine.Statistics.Snapshot(Stopwatch.GetTimestamp());
            _output.WriteLine($"500 CPS with live UI: {stats.AverageCps:F1} CPS, skipped {stats.Skipped}");
            Assert.InRange(stats.AverageCps, 450, 510); // the live display does not slow the engine down
            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1300, 1000), "clicker-after-test");
        });
        Assert.Empty(mouse.Actions); // test mode never clicks
    }

    [Fact]
    public void Starting_twice_or_with_invalid_settings_is_safe()
    {
        using var folder = new TempFolder();
        var mouse = new FakeMouse();
        UiTestHost.Run(() =>
        {
            var invalid = CreatePage(folder, mouse, p => { p.Position = PositionMode.Fixed; p.FixedX = 1_000_000; });
            UiTestHost.Render(invalid, 1200, 900);
            invalid.StartRun();
            Assert.Equal(AutoClickerState.Idle, invalid.State); // refused, error shown, no click
            Assert.Empty(mouse.Actions);

            var page = CreatePage(folder, mouse, p => p.Cps = 20);
            page.StartRun();
            page.StartRun();
            Assert.False(page.Engine.WaitForIdle(TimeSpan.FromMilliseconds(200)));
            page.EmergencyStop();
            Assert.True(page.Engine.WaitForIdle(TimeSpan.FromSeconds(1)));
            page.Shutdown();
        });
        Assert.True(mouse.Balanced());
    }

    [Fact]
    public void Every_autoclicker_text_exists_in_all_languages()
    {
        var placeholder = new Regex(@"\{\d\}");
        foreach (var (key, texts) in AutoClickerStrings.Table)
        {
            Assert.True(texts.Length == Loc.Languages.Length, $"{key} has {texts.Length} languages.");
            Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t), key));
            var expected = placeholder.Matches(texts[0]).Select(m => m.Value).OrderBy(v => v).ToList();
            foreach (var text in texts)
                Assert.True(expected.SequenceEqual(placeholder.Matches(text).Select(m => m.Value).OrderBy(v => v)), $"{key}: placeholders differ in \"{text}\".");
        }

        var keys = new List<string>();
        keys.AddRange(Enum.GetValues<MouseButtonKind>().SelectMany(b => new[] { "ac.button." + b, "ac.buttonShort." + b }));
        keys.AddRange(Enum.GetValues<ClickKind>().SelectMany(k => new[] { "ac.kind." + k, $"ac.kind.{k}.help" }));
        keys.AddRange(Enum.GetValues<RepeatMode>().Select(r => "ac.repeat." + r));
        keys.AddRange(Enum.GetValues<PositionMode>().Select(p => "ac.pos." + p));
        keys.AddRange(Enum.GetValues<HotkeyAction>().Select(h => "ac.hk." + h));
        keys.AddRange(Enum.GetValues<SequenceEventKind>().Select(k => "ac.rec.kind." + k));
        keys.AddRange(new[] { "target", "actual", "average", "clicks", "failed", "runtime", "remaining", "skipped", "deviation", "cursor" }.Select(m => "ac.metric." + m));
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ViennaAtomicClock", "AutoClicker"));
        if (Directory.Exists(source))
            foreach (var file in Directory.GetFiles(source, "*.cs"))
                keys.AddRange(Regex.Matches(File.ReadAllText(file), "\"(ac\\.[A-Za-z0-9_.]+[A-Za-z0-9])\"").Select(m => m.Groups[1].Value));
        var missing = keys.Distinct().Where(k => !AutoClickerStrings.Table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing texts: " + string.Join(", ", missing));
        Assert.True(Strings.Table.ContainsKey("ac.start")); // merged into the app's table
    }
}
