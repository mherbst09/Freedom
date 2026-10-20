using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ViennaAtomicClock.AutoClicker;
using Xunit;
using Xunit.Abstractions;

namespace Freedom.Tests;

/// <summary>Records every action instead of moving the real mouse.</summary>
internal sealed class FakeMouse : IMouseInputService
{
    private readonly object _gate = new();
    public readonly List<(MouseAction Action, long Timestamp)> Log = new();
    /// <summary>Return false to make Windows "reject" this action.</summary>
    public Func<MouseAction, bool> Accept { get; set; } = _ => true;
    /// <summary>Called before each batch (e.g. to simulate a slow system).</summary>
    public Action<int>? BeforeSend { get; set; }
    private int _batches;
    public int LastError { get; private set; }

    public int Send(ReadOnlySpan<MouseAction> actions)
    {
        BeforeSend?.Invoke(Interlocked.Increment(ref _batches));
        var accepted = 0;
        lock (_gate)
        {
            foreach (var action in actions)
            {
                if (!Accept(action)) break;
                Log.Add((action, Stopwatch.GetTimestamp()));
                accepted++;
            }
        }
        LastError = accepted == actions.Length ? 0 : 5;
        return accepted;
    }

    public bool TryGetCursor(out int x, out int y)
    {
        x = 100;
        y = 200;
        return true;
    }

    public List<MouseAction> Actions
    {
        get { lock (_gate) return Log.Select(l => l.Action).ToList(); }
    }

    public int Count(MouseActionKind kind) => Actions.Count(a => a.Kind == kind);

    /// <summary>True if every down was followed by an up of the same button.</summary>
    public bool Balanced()
    {
        var pressed = new HashSet<MouseButtonKind>();
        foreach (var action in Actions)
        {
            if (action.Kind == MouseActionKind.Down) pressed.Add(action.Button);
            if (action.Kind == MouseActionKind.Up) pressed.Remove(action.Button);
        }
        return pressed.Count == 0;
    }
}

internal static class ClickerSetup
{
    public static readonly ScreenArea Screen = new(-1920, 0, 3840, 1080);

    public static AutoClickerRunConfiguration Config(Action<AutoClickerProfile>? change = null, bool testMode = false, Func<bool>? isHeld = null)
    {
        var profile = new AutoClickerProfile();
        change?.Invoke(profile);
        var (config, errors) = AutoClickerRunConfiguration.Create(profile, testMode, Screen, 500, isHeld);
        Assert.True(errors.Count == 0, string.Join(", ", errors.Select(e => e.Key)));
        return config!;
    }

    /// <summary>Starts, waits for the end (or the timeout) and returns the result.</summary>
    public static AutoClickerRunResult RunToEnd(AutoClickerEngine engine, AutoClickerRunConfiguration config, TimeSpan timeout)
    {
        AutoClickerRunResult? result = null;
        using var done = new ManualResetEventSlim();
        engine.Finished += r => { result = r; done.Set(); };
        Assert.True(engine.Start(config));
        if (!done.Wait(timeout)) engine.Stop();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "The engine did not finish.");
        return result!;
    }

    public static void WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.ElapsedMilliseconds < timeoutMs, "Timed out waiting for a condition.");
            Thread.Sleep(1);
        }
    }
}

public sealed class ClickIntervalTests
{
    [Theory]
    [InlineData(1, 1000)]
    [InlineData(10, 100)]
    [InlineData(20, 50)]
    [InlineData(100, 10)]
    [InlineData(250, 4)]
    [InlineData(500, 2)]
    public void Cps_and_interval_match(double cps, double intervalMs)
    {
        Assert.Equal(intervalMs, ClickMath.IntervalFromCps(cps), 9);
        Assert.Equal(cps, ClickMath.CpsFromInterval(intervalMs), 9);
        var profile = new AutoClickerProfile { Cps = cps };
        Assert.Equal(intervalMs, profile.IntervalMs, 9);
        var (config, errors) = AutoClickerRunConfiguration.Create(profile, false, ClickerSetup.Screen, 500);
        Assert.Empty(errors);
        Assert.Equal(cps, config!.TargetCps, 6);
    }

    [Fact]
    public void Fractional_intervals_are_kept_in_stopwatch_ticks()
    {
        var interval = ClickMath.IntervalFromCps(60); // 16.666… ms
        Assert.Equal(16.6667, interval, 4);
        var ticks = ClickMath.MsToTicks(interval);
        Assert.InRange(ClickMath.TicksToMs(ticks), 16.666, 16.668);
        Assert.Equal(1_000 + 500 + 3_600_000 * 2.0 + 60_000 * 3, ClickMath.IntervalFromParts(2, 3, 1, 500), 6);
        Assert.Equal((2, 3, 1, 500.0), ClickMath.PartsFromInterval(ClickMath.IntervalFromParts(2, 3, 1, 500)));
    }

    [Theory]
    [InlineData(600)]
    [InlineData(10_000)]
    public void Too_fast_is_rejected(double cps)
    {
        var (config, errors) = AutoClickerRunConfiguration.Create(new AutoClickerProfile { Cps = cps }, false, ClickerSetup.Screen, 500);
        Assert.Null(config);
        Assert.Contains(errors, e => e.Key == "ac.err.tooFast");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void Invalid_intervals_are_rejected(double intervalMs)
    {
        var (config, errors) = AutoClickerRunConfiguration.Create(new AutoClickerProfile { IntervalMs = intervalMs }, false, ClickerSetup.Screen, 500);
        Assert.Null(config);
        Assert.Contains(errors, e => e.Key == "ac.err.interval");
    }

    [Fact]
    public void Other_invalid_settings_are_reported()
    {
        static List<string> Errors(Action<AutoClickerProfile> change)
        {
            var profile = new AutoClickerProfile();
            change(profile);
            return AutoClickerRunConfiguration.Create(profile, false, ClickerSetup.Screen, 500).Errors.Select(e => e.Key).ToList();
        }
        Assert.Contains("ac.err.count", Errors(p => { p.Repeat = RepeatMode.Count; p.ClickCount = 0; }));
        Assert.Contains("ac.err.count", Errors(p => { p.Repeat = RepeatMode.Count; p.ClickCount = 100_000_001; }));
        Assert.Contains("ac.err.duration", Errors(p => { p.Repeat = RepeatMode.Duration; p.DurationSeconds = 0; }));
        Assert.Contains("ac.err.holdKey", Errors(p => p.Repeat = RepeatMode.WhileHeld));
        Assert.Contains("ac.err.jitter", Errors(p => { p.RandomJitter = true; p.JitterMs = -1; }));
        Assert.Contains("ac.err.hold", Errors(p => p.HoldMs = -1));
        Assert.Contains("ac.err.position", Errors(p => { p.Position = PositionMode.Fixed; p.FixedX = 5000; }));
        Assert.Contains("ac.err.noPoints", Errors(p => p.Position = PositionMode.Sequence));
        Assert.Contains("ac.err.area", Errors(p => { p.Position = PositionMode.RandomArea; p.Area = new ScreenArea(0, 0, 0, 10); }));
        Assert.Empty(Errors(p => { p.Position = PositionMode.Fixed; p.FixedX = -1900; p.FixedY = 20; })); // left monitor
    }

    [Fact]
    public void Hold_time_never_exceeds_the_interval()
    {
        var config = ClickerSetup.Config(p => { p.Cps = 500; p.HoldMs = 5; });
        Assert.True(config.HoldMs <= 1, $"Hold {config.HoldMs} ms does not fit into 2 ms.");
        var doubleClick = ClickerSetup.Config(p => { p.Cps = 100; p.HoldMs = 50; p.ClickKind = ClickKind.Double; p.MultiClickGapMs = 500; });
        Assert.Equal(20, doubleClick.ActionIntervalMs, 6); // CPS counts single clicks: a double click uses two
        Assert.True(doubleClick.HoldMs * 2 + doubleClick.GapMs <= doubleClick.ActionIntervalMs);
        var actions = ClickerSetup.Config(p => { p.Cps = 10; p.ClickKind = ClickKind.Double; p.CpsCountsActions = true; });
        Assert.Equal(100, actions.ActionIntervalMs, 6);
    }
}

public sealed class JitterTests
{
    [Fact]
    public void Jitter_stays_within_bounds_and_is_uniform()
    {
        var random = new Random(1);
        var values = Enumerable.Range(0, 20_000).Select(_ => ClickMath.Jittered(100, 20, random)).ToList();
        Assert.All(values, v => Assert.InRange(v, 80, 120));
        Assert.InRange(values.Average(), 99.5, 100.5);
        // Uniform: each quarter of the range gets roughly a quarter of the values.
        foreach (var (from, to) in new[] { (80.0, 90.0), (90.0, 100.0), (100.0, 110.0), (110.0, 120.0) })
            Assert.InRange(values.Count(v => v >= from && v < to), 4500, 5500);
        Assert.Equal((80, 120), ClickMath.JitterRange(100, 20));
    }

    [Fact]
    public void Jitter_never_goes_below_two_ms_or_above_500_cps()
    {
        var random = new Random(2);
        Assert.All(Enumerable.Range(0, 5000).Select(_ => ClickMath.Jittered(2, 5, random)), v => Assert.True(v >= 2));
        Assert.Equal((2, 7), ClickMath.JitterRange(2, 5));
        Assert.All(Enumerable.Range(0, 5000).Select(_ => 1000 / ClickMath.Jittered(3, 2, random)), cps => Assert.True(cps <= 500));
    }

    [Fact]
    public void Disabled_jitter_changes_nothing()
    {
        var random = new Random(3);
        Assert.All(Enumerable.Range(0, 100).Select(_ => ClickMath.Jittered(37.5, 0, random)), v => Assert.Equal(37.5, v));
        Assert.Equal(0, ClickerSetup.Config(p => { p.RandomJitter = false; p.JitterMs = 50; }).JitterMs);
    }
}

public sealed class ClickEngineTests
{
    private readonly ITestOutputHelper _output;

    public ClickEngineTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Fixed_count_is_met_exactly()
    {
        var mouse = new FakeMouse();
        using var engine = new AutoClickerEngine(mouse);
        var result = ClickerSetup.RunToEnd(engine, ClickerSetup.Config(p => { p.Cps = 250; p.Repeat = RepeatMode.Count; p.ClickCount = 137; }), TimeSpan.FromSeconds(10));
        Assert.Equal(RunEnd.Completed, result.End);
        Assert.Equal(137, mouse.Count(MouseActionKind.Down));
        Assert.Equal(137, mouse.Count(MouseActionKind.Up));
        Assert.Equal(137, result.Statistics.Successful);
        Assert.True(mouse.Balanced());
    }

    [Fact]
    public void Fixed_count_with_double_clicks_never_overshoots()
    {
        var mouse = new FakeMouse();
        using var engine = new AutoClickerEngine(mouse);
        ClickerSetup.RunToEnd(engine, ClickerSetup.Config(p => { p.Cps = 200; p.ClickKind = ClickKind.Triple; p.Repeat = RepeatMode.Count; p.ClickCount = 10; }), TimeSpan.FromSeconds(10));
        Assert.Equal(10, mouse.Count(MouseActionKind.Down));
        Assert.True(mouse.Balanced());
    }

    [Fact]
    public void Unlimited_run_stops_within_50_ms_even_at_500_cps()
    {
        foreach (var setup in new Action<AutoClickerProfile>[]
                 {
                     p => p.Cps = 500,
                     p => { p.Cps = 500; p.RandomJitter = true; p.JitterMs = 1; },
                     p => { p.Cps = 500; p.Position = PositionMode.Fixed; p.FixedX = 50; p.FixedY = 60; },
                     p => p.IntervalMs = 3_600_000 // one click per hour: stop must not wait for the next click
                 })
        {
            var mouse = new FakeMouse();
            using var engine = new AutoClickerEngine(mouse);
            using var idle = new ManualResetEventSlim();
            engine.Finished += _ => idle.Set();
            Assert.True(engine.Start(ClickerSetup.Config(setup)));
            ClickerSetup.WaitFor(() => engine.State == AutoClickerState.Running);
            Thread.Sleep(150);
            var watch = Stopwatch.StartNew();
            Assert.True(engine.Stop());
            Assert.True(idle.Wait(TimeSpan.FromSeconds(2)));
            watch.Stop();
            _output.WriteLine($"Stop took {watch.Elapsed.TotalMilliseconds:F1} ms");
            Assert.True(watch.Elapsed.TotalMilliseconds < 50, $"Stopping took {watch.Elapsed.TotalMilliseconds:F1} ms.");
            Assert.Equal(AutoClickerState.Idle, engine.State);
            Assert.True(mouse.Balanced());
        }
    }

    [Fact]
    public void Start_delay_can_be_cancelled_quickly()
    {
        var mouse = new FakeMouse();
        using var engine = new AutoClickerEngine(mouse);
        using var idle = new ManualResetEventSlim();
        engine.Finished += _ => idle.Set();
        engine.Start(ClickerSetup.Config(p => { p.StartDelay = true; p.StartDelaySeconds = 10; }));
        ClickerSetup.WaitFor(() => engine.CountdownRemaining > TimeSpan.Zero);
        Assert.Equal(AutoClickerState.Starting, engine.State);
        Assert.InRange(engine.CountdownRemaining.TotalSeconds, 8, 10);
        var watch = Stopwatch.StartNew();
        engine.EmergencyStop();
        Assert.True(idle.Wait(1000));
        Assert.True(watch.ElapsedMilliseconds < 50);
        Assert.Empty(mouse.Actions);
    }

    [Fact]
    public void Duration_mode_stops_when_time_is_up()
    {
        var mouse = new FakeMouse();
        using var engine = new AutoClickerEngine(mouse);
        var watch = Stopwatch.StartNew();
        var result = ClickerSetup.RunToEnd(engine, ClickerSetup.Config(p => { p.Cps = 100; p.Repeat = RepeatMode.Duration; p.DurationSeconds = 0.5; }), TimeSpan.FromSeconds(5));
        Assert.Equal(RunEnd.Completed, result.End);
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.45, 1.0);
        Assert.InRange(result.Statistics.Successful, 40, 55);
    }

    [Fact]
    public void Hold_key_mode_stops_when_the_key_is_released()
    {
        var held = true;
        var mouse = new FakeMouse();
        using var engine = new AutoClickerEngine(mouse);
        using var idle = new ManualResetEventSlim();
        engine.Finished += _ => idle.Set();
        engine.Start(ClickerSetup.Config(p => { p.Cps = 200; p.Repeat = RepeatMode.WhileHeld; }, isHeld: () => Volatile.Read(ref held)));
        ClickerSetup.WaitFor(() => mouse.Count(MouseActionKind.Down) > 10);
        var watch = Stopwatch.StartNew();
        Volatile.Write(ref held, false);
        Assert.True(idle.Wait(1000));
        Assert.True(watch.ElapsedMilliseconds < 50, $"Release took {watch.ElapsedMilliseconds} ms.");
        var downs = mouse.Count(MouseActionKind.Down);
        Thread.Sleep(50);
        Assert.Equal(downs, mouse.Count(MouseActionKind.Down));
        Assert.True(mouse.Balanced());
    }

    [Fact]
    public void Stopping_while_a_button_is_held_releases_it()
    {
        var mouse = new FakeMouse();
        using var engine = new AutoClickerEngine(mouse);
        using var idle = new ManualResetEventSlim();
        engine.Finished += _ => idle.Set();
        engine.Start(ClickerSetup.Config(p => { p.ClickKind = ClickKind.Hold; p.Button = MouseButtonKind.Right; }));
        ClickerSetup.WaitFor(() => mouse.Count(MouseActionKind.Down) == 1);
        Assert.False(mouse.Balanced());
        engine.Stop();
        Assert.True(idle.Wait(1000));
        Assert.True(mouse.Balanced());
        Assert.Equal(MouseAction.Up(MouseButtonKind.Right), mouse.Actions[^1]);
    }

    [Fact]
    public void Down_only_mode_is_released_when_the_run_ends()
    {
        var mouse = new FakeMouse();
        using var engine = new AutoClickerEngine(mouse);
        ClickerSetup.RunToEnd(engine, ClickerSetup.Config(p => { p.Cps = 100; p.ClickKind = ClickKind.DownOnly; p.Repeat = RepeatMode.Count; p.ClickCount = 5; }), TimeSpan.FromSeconds(5));
        Assert.Equal(5, mouse.Count(MouseActionKind.Down));
        Assert.True(mouse.Balanced());
    }

    [Fact]
    public void Pausing_releases_and_resuming_presses_again()
    {
        var mouse = new FakeMouse();
        using var engine = new AutoClickerEngine(mouse);
        using var idle = new ManualResetEventSlim();
        engine.Finished += _ => idle.Set();
        engine.Start(ClickerSetup.Config(p => p.ClickKind = ClickKind.Hold));
        ClickerSetup.WaitFor(() => mouse.Count(MouseActionKind.Down) == 1);
        Assert.True(engine.Pause());
        ClickerSetup.WaitFor(() => mouse.Balanced());
        Assert.Equal(AutoClickerState.Paused, engine.State);
        Assert.True(engine.Resume());
        ClickerSetup.WaitFor(() => mouse.Count(MouseActionKind.Down) == 2);
        engine.Stop();
        Assert.True(idle.Wait(1000));
        Assert.True(mouse.Balanced());
    }

    [Fact]
    public void Errors_end_the_run_and_still_release_the_button()
    {
        var downs = 0;
        // After a few presses Windows rejects every new press; releasing still works.
        var mouse = new FakeMouse { Accept = a => a.Kind != MouseActionKind.Down || Interlocked.Increment(ref downs) <= 3 };
        using var engine = new AutoClickerEngine(mouse);
        var states = new List<AutoClickerState>();
        engine.StateChanged += s => { lock (states) states.Add(s); };
        var result = ClickerSetup.RunToEnd(engine, ClickerSetup.Config(p => { p.Cps = 500; p.ClickKind = ClickKind.DownOnly; }), TimeSpan.FromSeconds(5));
        Assert.Equal(RunEnd.Faulted, result.End);
        Assert.NotNull(result.Error);
        Assert.Contains(AutoClickerState.Faulted, states);
        Assert.Equal(AutoClickerState.Idle, engine.State);
        Assert.True(mouse.Balanced());
        Assert.True(result.Statistics.Failed >= 25);
    }

    [Fact]
    public void A_click_only_counts_when_windows_accepted_it()
    {
        var calls = 0;
        var mouse = new FakeMouse { Accept = a => a.Kind != MouseActionKind.Down || Interlocked.Increment(ref calls) % 2 == 0 };
        using var engine = new AutoClickerEngine(mouse);
        var result = ClickerSetup.RunToEnd(engine, ClickerSetup.Config(p => { p.Cps = 200; p.Repeat = RepeatMode.Count; p.ClickCount = 10; }), TimeSpan.FromSeconds(5));
        Assert.Equal(10, result.Statistics.Successful);
        Assert.True(result.Statistics.Failed >= 9);
        Assert.Equal(10, mouse.Count(MouseActionKind.Up));
    }

    [Fact]
    public void A_delay_does_not_cause_a_burst_of_catch_up_clicks()
    {
        var mouse = new FakeMouse
        {
            // The 20th batch takes 80 ms (the system is busy) – at 500 CPS that is 40 missed click times.
            BeforeSend = n => { if (n == 20) Thread.Sleep(80); }
        };
        using var engine = new AutoClickerEngine(mouse);
        var result = ClickerSetup.RunToEnd(engine, ClickerSetup.Config(p => { p.Cps = 500; p.Repeat = RepeatMode.Count; p.ClickCount = 60; }), TimeSpan.FromSeconds(5));
        Assert.True(result.Statistics.Skipped >= 30, $"Only {result.Statistics.Skipped} skipped.");
        var downs = mouse.Log.Where(l => l.Action.Kind == MouseActionKind.Down).Select(l => l.Timestamp).ToList();
        // After the delay the clicks keep their 2 ms rhythm: never several clicks within one interval.
        var gaps = downs.Zip(downs.Skip(1), (a, b) => ClickMath.TicksToMs(b - a)).Skip(21).ToList();
        Assert.True(gaps.Count(g => g < 1.0) <= 1, "Clicks were fired in a burst: " + string.Join(", ", gaps.Select(g => g.ToString("F2"))));
    }

    [Fact]
    public void Positions_move_the_cursor_first_and_back_if_wanted()
    {
        var mouse = new FakeMouse();
        using var engine = new AutoClickerEngine(mouse);
        ClickerSetup.RunToEnd(engine, ClickerSetup.Config(p =>
        {
            p.Cps = 100; p.Repeat = RepeatMode.Count; p.ClickCount = 2;
            p.Position = PositionMode.Fixed; p.FixedX = -1500; p.FixedY = 300; p.MoveBack = true;
        }), TimeSpan.FromSeconds(5));
        var actions = mouse.Actions;
        Assert.Equal(MouseAction.Move(-1500, 300), actions[0]);
        Assert.Equal(MouseActionKind.Down, actions[1].Kind);
        Assert.Equal(MouseActionKind.Up, actions[2].Kind);
        Assert.Equal(MouseAction.Move(100, 200), actions[3]); // back to where the cursor was

        var route = new FakeMouse();
        using var routeEngine = new AutoClickerEngine(route);
        ClickerSetup.RunToEnd(routeEngine, ClickerSetup.Config(p =>
        {
            p.Cps = 200; p.Position = PositionMode.Sequence; p.LoopPoints = false;
            p.Points = new List<ClickPoint> { new(10, 10), new(20, 20, 30), new(30, 30) };
        }), TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 10, 20, 30 }, route.Actions.Where(a => a.Kind == MouseActionKind.Move).Select(a => a.X));

        var area = new FakeMouse();
        using var areaEngine = new AutoClickerEngine(area);
        ClickerSetup.RunToEnd(areaEngine, ClickerSetup.Config(p =>
        {
            p.Cps = 500; p.Repeat = RepeatMode.Count; p.ClickCount = 200;
            p.Position = PositionMode.RandomArea; p.Area = new ScreenArea(-100, 50, 40, 30);
        }), TimeSpan.FromSeconds(5));
        Assert.All(area.Actions.Where(a => a.Kind == MouseActionKind.Move), m =>
        {
            Assert.InRange(m.X, -100, -61);
            Assert.InRange(m.Y, 50, 79);
        });
    }

    [Fact]
    public void Invalid_state_changes_are_ignored()
    {
        var mouse = new FakeMouse();
        using var engine = new AutoClickerEngine(mouse);
        Assert.False(engine.Stop());
        Assert.False(engine.Pause());
        Assert.False(engine.Resume());
        var config = ClickerSetup.Config(p => p.Cps = 50);
        Assert.True(engine.Start(config));
        Assert.False(engine.Start(config)); // already running
        ClickerSetup.WaitFor(() => engine.State == AutoClickerState.Running);
        Assert.False(engine.Resume());
        Assert.True(engine.Pause());
        Assert.False(engine.Pause());
        Assert.True(engine.Stop());
        Assert.True(engine.WaitForIdle(TimeSpan.FromSeconds(2)));
        Assert.Equal(AutoClickerState.Idle, engine.State);
    }

    [Fact]
    public void A_running_engine_ignores_later_profile_changes()
    {
        var profile = new AutoClickerProfile { Cps = 100, Repeat = RepeatMode.Count, ClickCount = 20 };
        var (config, _) = AutoClickerRunConfiguration.Create(profile, false, ClickerSetup.Screen, 500);
        profile.ClickCount = 1000;
        profile.Cps = 1;
        profile.Points.Add(new ClickPoint(1, 1));
        Assert.Equal(20, config!.ClickLimit);
        Assert.Equal(10, config.IntervalMs, 6);
        Assert.Empty(config.Points);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    [InlineData(500)]
    public void Test_mode_reaches_the_target_rate_without_clicking(double cps)
    {
        var mouse = new FakeMouse();
        using var engine = new AutoClickerEngine(mouse);
        var result = ClickerSetup.RunToEnd(engine, ClickerSetup.Config(p => { p.Cps = cps; p.Repeat = RepeatMode.Duration; p.DurationSeconds = 1.5; }, testMode: true), TimeSpan.FromSeconds(5));
        var stats = result.Statistics;
        _output.WriteLine($"{cps} CPS: average {stats.AverageCps:F1}, window {stats.ActualCps:F1} (min {stats.MinWindowCps:F1}, max {stats.MaxWindowCps:F1}), " +
                          $"interval {stats.AverageIntervalMs:F3} ms ({stats.MinIntervalMs:F3}–{stats.MaxIntervalMs:F3}), max deviation {stats.MaxDeviationMs:F3} ms, " +
                          $"skipped {stats.Skipped}, CPU {stats.CpuPercent:F1} %");
        Assert.Empty(mouse.Actions); // no real input in test mode
        Assert.True(result.TestMode);
        Assert.InRange(stats.AverageCps, cps * 0.93, cps * 1.02);
        Assert.InRange(stats.AverageIntervalMs, 1000 / cps * 0.98, 1000 / cps * 1.08);
        Assert.NotNull(stats.CpuPercent);
        Assert.True(stats.CpuPercent < (cps >= 500 ? 90 : 60), $"Engine used {stats.CpuPercent:F1} % of a core.");
    }

    [Fact]
    public void Live_statistics_measure_a_rolling_second()
    {
        var stats = new AutoClickerRunStatistics();
        var f = Stopwatch.Frequency;
        stats.Start(f);
        for (var i = 1; i <= 300; i++)
        {
            stats.AddClick(f + i * f / 100, 0); // 100 per second for 3 s
            if (i % 25 == 0) stats.SampleWindow(f + i * f / 100);
        }
        var snapshot = stats.Snapshot(4 * f);
        Assert.InRange(snapshot.ActualCps, 99, 101);
        Assert.InRange(snapshot.AverageCps, 99, 101);
        Assert.Equal(10, snapshot.AverageIntervalMs, 3);
        Assert.Equal(300, snapshot.Successful);
        Assert.InRange(snapshot.MinWindowCps, 99, 101);
        Assert.InRange(snapshot.MaxWindowCps, 99, 101);
    }
}

public sealed class MouseInputMappingTests
{
    [Fact]
    public void Native_input_structure_has_the_size_windows_expects()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, WindowsMouseInputService.InputSize);
    }

    [Fact]
    public void Absolute_coordinates_cover_negative_virtual_desktops()
    {
        var screen = new ScreenArea(-1920, -200, 1920 + 2560, 1440 + 200);
        Assert.Equal((0, 0), WindowsMouseInputService.ToAbsolute(-1920, -200, screen));
        Assert.Equal((65535, 65535), WindowsMouseInputService.ToAbsolute(2559, 1439, screen));
        foreach (var (x, y) in new[] { (-1920, -200), (-1, 0), (0, 0), (1280, 720), (2559, 1439), (-960, 540) })
        {
            var (ax, ay) = WindowsMouseInputService.ToAbsolute(x, y, screen);
            Assert.Equal((x, y), WindowsMouseInputService.FromAbsolute(ax, ay, screen));
        }
    }

    [Fact]
    public void Every_button_maps_to_the_right_flags()
    {
        Assert.Equal((0x0002u, 0u), WindowsMouseInputService.ButtonFlags(MouseButtonKind.Left, true));
        Assert.Equal((0x0004u, 0u), WindowsMouseInputService.ButtonFlags(MouseButtonKind.Left, false));
        Assert.Equal((0x0008u, 0u), WindowsMouseInputService.ButtonFlags(MouseButtonKind.Right, true));
        Assert.Equal((0x0040u, 0u), WindowsMouseInputService.ButtonFlags(MouseButtonKind.Middle, false));
        Assert.Equal((0x0080u, 1u), WindowsMouseInputService.ButtonFlags(MouseButtonKind.X1, true));
        Assert.Equal((0x0100u, 2u), WindowsMouseInputService.ButtonFlags(MouseButtonKind.X2, false));
    }

    [Fact]
    public void Windows_accepts_a_harmless_move_to_the_current_position()
    {
        // No click: the cursor is "moved" to where it already is, through the real SendInput path.
        var mouse = new WindowsMouseInputService();
        Assert.True(mouse.TryGetCursor(out var x, out var y));
        var accepted = mouse.Send(stackalloc[] { MouseAction.Move(x, y) });
        if (accepted == 0) return; // locked session or secure desktop: Windows refuses input, which is fine
        Assert.Equal(1, accepted);
        Assert.Equal(0, mouse.LastError);
        Assert.True(mouse.TryGetCursor(out var x2, out var y2));
        Assert.InRange(x2, x - 1, x + 1);
        Assert.InRange(y2, y - 1, y + 1);
    }

    [Fact]
    public void Screen_information_is_available()
    {
        var screen = ScreenInfo.VirtualScreen();
        Assert.True(screen.Width > 0 && screen.Height > 0);
        var monitors = ScreenInfo.Monitors();
        Assert.NotEmpty(monitors);
        Assert.Contains(monitors, m => m.Primary);
        var primary = monitors.First(m => m.Primary);
        Assert.Equal(primary, ScreenInfo.MonitorAt(primary.Bounds.X + 1, primary.Bounds.Y + 1));
        Assert.Null(ScreenInfo.MonitorAt(screen.X - 10, screen.Y - 10));
        Assert.True(primary.Scale >= 1);
    }
}

public sealed class AutoClickerStorageTests
{
    [Fact]
    public void Profiles_round_trip_as_versioned_json()
    {
        using var folder = new TempFolder();
        var storage = new AutoClickerStorage(folder.Path);
        var profile = new AutoClickerProfile
        {
            Name = "Spiel", Cps = 250, RandomJitter = true, JitterMs = 1.5, Button = MouseButtonKind.X2, ClickKind = ClickKind.Double,
            Repeat = RepeatMode.Count, ClickCount = 1234, Position = PositionMode.Sequence,
            Points = new List<ClickPoint> { new(-100, 20), new(300, 400, 25) }, StopOnLock = false
        };
        profile.Hotkeys[HotkeyAction.Start] = new Hotkey(0x41, HotkeyModifiers.Control | HotkeyModifiers.Shift);
        profile.Hotkeys[HotkeyAction.PauseResume] = new Hotkey(0, MouseButton: MouseButtonKind.X1);
        storage.SaveProfile(profile);

        var json = File.ReadAllText(Path.Combine(folder.Path, "profiles", "Spiel.json"));
        Assert.Contains("\"Version\": 1", json);
        Assert.Contains("\"Double\"", json); // enums as readable names

        var loaded = Assert.Single(storage.LoadProfiles().Profiles);
        Assert.Equal("Spiel", loaded.Name);
        Assert.Equal(4, loaded.IntervalMs, 9);
        Assert.Equal(MouseButtonKind.X2, loaded.Button);
        Assert.Equal(2, loaded.Points.Count);
        Assert.Equal(25, loaded.Points[1].DelayMs);
        Assert.Equal(new Hotkey(0x41, HotkeyModifiers.Control | HotkeyModifiers.Shift), loaded.Hotkeys[HotkeyAction.Start]);
        Assert.Equal(MouseButtonKind.X1, loaded.Hotkeys[HotkeyAction.PauseResume]!.MouseButton);
        Assert.False(loaded.StopOnLock);
    }

    [Fact]
    public void Damaged_files_do_not_block_other_profiles()
    {
        using var folder = new TempFolder();
        var storage = new AutoClickerStorage(folder.Path);
        storage.SaveProfile(new AutoClickerProfile { Name = "Gut" });
        Directory.CreateDirectory(Path.Combine(folder.Path, "profiles"));
        File.WriteAllText(Path.Combine(folder.Path, "profiles", "Kaputt.json"), "{ \"Name\": ");
        File.WriteAllText(Path.Combine(folder.Path, "profiles", "Zukunft.json"), "{ \"Version\": 99, \"Name\": \"Zukunft\" }");
        var result = storage.LoadProfiles();
        Assert.Equal("Gut", Assert.Single(result.Profiles).Name);
        Assert.Equal(2, result.Damaged.Count);
    }

    [Fact]
    public void Defaults_are_safe_and_never_start_on_their_own()
    {
        using var folder = new TempFolder();
        var storage = new AutoClickerStorage(folder.Path);
        var profile = Assert.Single(storage.LoadProfiles().Profiles);
        Assert.Equal(10, profile.Cps, 6);
        Assert.Equal(RepeatMode.UntilStopped, profile.Repeat);
        Assert.Equal(Hotkey.Key(0x75), profile.Hotkeys[HotkeyAction.StartStop]); // F6
        Assert.Equal(Hotkey.Key(0x77), profile.Hotkeys[HotkeyAction.EmergencyStop]); // F8
        Assert.NotEqual(profile.Hotkeys[HotkeyAction.StartStop], profile.Hotkeys[HotkeyAction.EmergencyStop]);
        Assert.True(profile.StopOnLock);
        Assert.False(storage.LoadSettings().TestMode);

        // A profile file missing new fields gets the defaults.
        Directory.CreateDirectory(Path.Combine(folder.Path, "profiles"));
        File.WriteAllText(Path.Combine(folder.Path, "profiles", "Alt.json"), "{ \"Version\": 1, \"Name\": \"Alt\", \"IntervalMs\": 50 }");
        var old = storage.LoadProfiles().Profiles.Single(p => p.Name == "Alt");
        Assert.Equal(Hotkey.Key(0x77), old.Hotkeys[HotkeyAction.EmergencyStop]);
    }

    [Fact]
    public void Import_and_export_keep_the_profile()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "export.json");
        AutoClickerStorage.Export(new AutoClickerProfile { Name = "Export", Cps = 42 }, path);
        var imported = AutoClickerStorage.Import(path);
        Assert.NotNull(imported);
        Assert.Equal(42, imported!.Cps, 6);
        File.WriteAllText(path, "not json");
        Assert.Null(AutoClickerStorage.Import(path));
        Assert.Equal("Export (2)", AutoClickerStorage.UniqueName("Export", new[] { "export", "Andere" }));
        Assert.Equal("a_b_c.json", AutoClickerStorage.FileNameFor("a/b:c"));
    }

    [Fact]
    public void Sequences_and_settings_are_saved()
    {
        using var folder = new TempFolder();
        var storage = new AutoClickerStorage(folder.Path);
        storage.SaveSettings(new AutoClickerSettings { LastProfile = "X", HighCpsNoticeDismissed = true });
        Assert.Equal("X", storage.LoadSettings().LastProfile);
        storage.SaveSequence(new ClickSequence { Name = "Route", Events = { new ClickSequenceEvent { X = 5, Y = 6, DelayMs = 120 } } });
        Assert.Equal(120, Assert.Single(Assert.Single(storage.LoadSequences()).Events).DelayMs);
    }
}

public sealed class RecordingTests
{
    private static LowLevelMouseHook.Event Hook(int message, int x, int y, double ms, uint data = 0, bool injected = false) =>
        LowLevelMouseHook.Translate(message, x, y, data, injected ? 1u : 0u, (long)(ms * Stopwatch.Frequency / 1000))!.Value;

    [Fact]
    public void Only_mouse_buttons_and_wheel_are_recorded()
    {
        Assert.Null(LowLevelMouseHook.Translate(0x0200, 1, 1, 0, 0, 0)); // mouse move
        Assert.Null(LowLevelMouseHook.Translate(0x0100, 1, 1, 0, 0, 0)); // a keyboard message number is not a mouse event
        Assert.Equal(MouseButtonKind.X2, LowLevelMouseHook.Translate(0x020B, 1, 1, 2u << 16, 0, 0)!.Value.Button);
        Assert.Equal(-120, LowLevelMouseHook.Translate(0x020A, 1, 1, unchecked((uint)(-120 << 16)), 0, 0)!.Value.WheelDelta);
    }

    [Fact]
    public void Clicks_get_relative_times_and_pairs_become_clicks()
    {
        var builder = new SequenceBuilder(0);
        builder.Add(Hook(0x0201, 10, 10, 500), false);
        builder.Add(Hook(0x0202, 11, 10, 560), false);
        builder.Add(Hook(0x0204, 300, 40, 1500), false);
        builder.Add(Hook(0x0205, 300, 40, 1520), false);
        builder.Add(Hook(0x0201, 50, 50, 2000), false);
        builder.Add(Hook(0x0202, 400, 400, 2400), false); // dragged far: stays down + up
        builder.Add(Hook(0x020A, 400, 400, 2500, unchecked((uint)(120 << 16))), false);
        var sequence = builder.Build("Test");
        Assert.Equal(new[] { SequenceEventKind.Click, SequenceEventKind.Click, SequenceEventKind.Down, SequenceEventKind.Up, SequenceEventKind.Wheel }, sequence.Events.Select(e => e.Kind));
        Assert.Equal(500, sequence.Events[0].DelayMs, 1);
        Assert.Equal(60, sequence.Events[0].HoldMs, 1);
        Assert.Equal(1000, sequence.Events[1].DelayMs, 1);
        Assert.Equal(MouseButtonKind.Right, sequence.Events[1].Button);
        Assert.Equal(500, sequence.Events[2].DelayMs, 1);
        Assert.Equal(400, sequence.Events[3].DelayMs, 1);
        Assert.Equal(120, sequence.Events[4].WheelDelta);
    }

    [Fact]
    public void Injected_input_and_clicks_on_freedom_are_ignored()
    {
        var builder = new SequenceBuilder(0);
        builder.Add(Hook(0x0201, 10, 10, 100, injected: true), false);
        builder.Add(Hook(0x0201, 10, 10, 200), onOwnWindow: true);
        Assert.Equal(0, builder.Count);
    }

    [Fact]
    public void Playback_follows_the_recorded_times_and_can_be_stopped()
    {
        var sequence = new ClickSequence
        {
            Speed = 2,
            Events =
            {
                new ClickSequenceEvent { Kind = SequenceEventKind.Click, X = 1, Y = 1, DelayMs = 0, HoldMs = 10 },
                new ClickSequenceEvent { Kind = SequenceEventKind.Click, X = 2, Y = 2, DelayMs = 200, HoldMs = 10 },
                new ClickSequenceEvent { Kind = SequenceEventKind.Click, X = 3, Y = 3, DelayMs = 100, HoldMs = 10, Enabled = false },
                new ClickSequenceEvent { Kind = SequenceEventKind.Down, Button = MouseButtonKind.Middle, X = 4, Y = 4, DelayMs = 20 }
            }
        };
        var (config, errors) = AutoClickerRunConfiguration.ForPlayback(sequence, new AutoClickerProfile(), false, ClickerSetup.Screen);
        Assert.Empty(errors);
        var mouse = new FakeMouse();
        using var engine = new AutoClickerEngine(mouse);
        var result = ClickerSetup.RunToEnd(engine, config!, TimeSpan.FromSeconds(5));
        Assert.Equal(RunEnd.Completed, result.End);
        var moves = mouse.Log.Where(l => l.Action.Kind == MouseActionKind.Move).ToList();
        Assert.Equal(new[] { 1, 2, 4 }, moves.Select(m => m.Action.X)); // the disabled event is skipped
        Assert.InRange(ClickMath.TicksToMs(moves[1].Timestamp - moves[0].Timestamp), 95, 140); // 200 ms at double speed
        Assert.True(mouse.Balanced()); // the trailing "down" is released at the end

        var endless = new ClickSequence { Repeat = 100_000, Events = { new ClickSequenceEvent { Kind = SequenceEventKind.Click, X = 1, Y = 1, DelayMs = 1000 } } };
        var (loop, _) = AutoClickerRunConfiguration.ForPlayback(endless, new AutoClickerProfile(), false, ClickerSetup.Screen);
        using var second = new AutoClickerEngine(new FakeMouse());
        using var idle = new ManualResetEventSlim();
        second.Finished += _ => idle.Set();
        second.Start(loop!);
        ClickerSetup.WaitFor(() => second.State == AutoClickerState.Running);
        var watch = Stopwatch.StartNew();
        second.Stop();
        Assert.True(idle.Wait(1000));
        Assert.True(watch.ElapsedMilliseconds < 50);
        Assert.Null(AutoClickerRunConfiguration.ForPlayback(new ClickSequence(), new AutoClickerProfile(), false, ClickerSetup.Screen).Config);
    }
}

public sealed class HotkeyTests
{
    [Fact]
    public void Duplicate_combinations_are_detected()
    {
        var map = AutoClickerProfile.DefaultHotkeys();
        Assert.Empty(GlobalHotkeyService.FindDuplicates(map));
        map[HotkeyAction.EmergencyStop] = Hotkey.Key(0x75); // same as start/stop
        Assert.Equal(new HashSet<HotkeyAction> { HotkeyAction.StartStop, HotkeyAction.EmergencyStop }, GlobalHotkeyService.FindDuplicates(map));
        Assert.Equal("Strg + Umschalt + F12", new Hotkey(0x7B, HotkeyModifiers.Control | HotkeyModifiers.Shift).ToString());
        Assert.Equal("Maus X1", new Hotkey(0, MouseButton: MouseButtonKind.X1).ToString());
    }

    [Fact]
    public void Hotkeys_register_report_conflicts_and_unregister()
    {
        UiTestHost.Run(() =>
        {
            // An unusual combination nobody else uses.
            var rare = new Hotkey(0x87, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift); // F24
            using var first = new GlobalHotkeyService();
            var status = first.Apply(new Dictionary<HotkeyAction, Hotkey?> { [HotkeyAction.StartStop] = rare, [HotkeyAction.Stop] = null });
            Assert.Equal(HotkeyStatus.Registered, status[HotkeyAction.StartStop]);
            Assert.Equal(HotkeyStatus.None, status[HotkeyAction.Stop]);

            // A second registration of the same combination is reported as taken.
            using (var second = new GlobalHotkeyService())
                Assert.Equal(HotkeyStatus.InUse, second.Apply(new Dictionary<HotkeyAction, Hotkey?> { [HotkeyAction.StartStop] = rare })[HotkeyAction.StartStop]);

            var duplicates = first.Apply(new Dictionary<HotkeyAction, Hotkey?> { [HotkeyAction.Start] = rare, [HotkeyAction.EmergencyStop] = rare });
            Assert.All(duplicates.Values, s => Assert.Equal(HotkeyStatus.Duplicate, s));

            first.UnregisterAll();
            using var third = new GlobalHotkeyService();
            Assert.Equal(HotkeyStatus.Registered, third.Apply(new Dictionary<HotkeyAction, Hotkey?> { [HotkeyAction.StartStop] = rare })[HotkeyAction.StartStop]);
        });
    }
}
