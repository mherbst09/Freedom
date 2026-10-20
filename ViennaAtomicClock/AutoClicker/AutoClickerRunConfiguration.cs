using System.Collections.Immutable;

namespace ViennaAtomicClock.AutoClicker;

public enum RunKind { Clicker, Playback }

/// <summary>A validation problem as a text key plus arguments (translated by the page).</summary>
public sealed record ValidationError(string Key, params object[] Args);

/// <summary>
/// Immutable snapshot of all settings for one run. The engine only ever sees this copy, so editing the page
/// while the clicker runs cannot change a running engine.
/// </summary>
public sealed record AutoClickerRunConfiguration
{
    public RunKind Kind { get; init; } = RunKind.Clicker;
    public string ProfileName { get; init; } = "";
    public bool TestMode { get; init; }

    /// <summary>Milliseconds between two complete single clicks.</summary>
    public double IntervalMs { get; init; } = 100;
    /// <summary>Milliseconds from the start of one action (single/double/triple click) to the next.</summary>
    public double ActionIntervalMs { get; init; } = 100;
    public double JitterMs { get; init; }

    public MouseButtonKind Button { get; init; }
    public ClickKind ClickKind { get; init; }
    public int ClicksPerAction { get; init; } = 1;
    public double HoldMs { get; init; }
    public double GapMs { get; init; }

    public RepeatMode Repeat { get; init; }
    public long? ClickLimit { get; init; }
    public TimeSpan? Duration { get; init; }
    public TimeSpan StartDelay { get; init; }
    /// <summary>For <see cref="RepeatMode.WhileHeld"/>: is the hotkey still pressed?</summary>
    public Func<bool>? IsHeld { get; init; }

    public PositionMode Position { get; init; }
    public int FixedX { get; init; }
    public int FixedY { get; init; }
    public ImmutableArray<ClickPoint> Points { get; init; } = ImmutableArray<ClickPoint>.Empty;
    public bool LoopPoints { get; init; } = true;
    public ScreenArea? Area { get; init; }
    public bool MoveBack { get; init; }

    public TimeSpan? MaxRuntime { get; init; }
    public long? MaxClicks { get; init; }

    public ImmutableArray<ClickSequenceEvent> Sequence { get; init; } = ImmutableArray<ClickSequenceEvent>.Empty;
    public double SequenceSpeed { get; init; } = 1;
    public int SequenceRepeat { get; init; } = 1;

    public double TargetCps => 1000 / IntervalMs;

    /// <summary>Builds and checks a configuration from a profile. <paramref name="screen"/> is the virtual desktop in physical pixels.</summary>
    public static (AutoClickerRunConfiguration? Config, List<ValidationError> Errors) Create(
        AutoClickerProfile profile, bool testMode, ScreenArea screen, double systemDoubleClickMs, Func<bool>? isHeld = null)
    {
        var errors = new List<ValidationError>();
        var interval = profile.IntervalMs;
        if (double.IsNaN(interval) || double.IsInfinity(interval) || interval <= 0) errors.Add(new("ac.err.interval"));
        else if (interval < ClickMath.MinIntervalMs - 1e-9) errors.Add(new("ac.err.tooFast", ClickMath.MaxCps));
        else if (interval > ClickMath.MaxIntervalMs) errors.Add(new("ac.err.tooSlow"));

        var jitter = profile.RandomJitter ? profile.JitterMs : 0;
        if (double.IsNaN(jitter) || jitter < 0 || jitter > 3_600_000) errors.Add(new("ac.err.jitter"));
        if (double.IsNaN(profile.HoldMs) || profile.HoldMs < 0 || profile.HoldMs > 60_000) errors.Add(new("ac.err.hold"));
        if (double.IsNaN(profile.MultiClickGapMs) || profile.MultiClickGapMs < 0 || profile.MultiClickGapMs > 5_000) errors.Add(new("ac.err.gap"));

        switch (profile.Repeat)
        {
            case RepeatMode.Count when profile.ClickCount is < 1 or > 100_000_000:
                errors.Add(new("ac.err.count"));
                break;
            case RepeatMode.Duration when double.IsNaN(profile.DurationSeconds) || profile.DurationSeconds <= 0 || profile.DurationSeconds > 7 * 24 * 3600:
                errors.Add(new("ac.err.duration"));
                break;
            case RepeatMode.WhileHeld when isHeld is null:
                errors.Add(new("ac.err.holdKey"));
                break;
        }
        if (profile.StartDelay && (double.IsNaN(profile.StartDelaySeconds) || profile.StartDelaySeconds < 0 || profile.StartDelaySeconds > 600))
            errors.Add(new("ac.err.delay"));
        if (profile.MaxRuntimeEnabled && (double.IsNaN(profile.MaxRuntimeMinutes) || profile.MaxRuntimeMinutes <= 0)) errors.Add(new("ac.err.maxRuntime"));
        if (profile.MaxClicksEnabled && profile.MaxClicks < 1) errors.Add(new("ac.err.maxClicks"));

        bool Inside(int x, int y) => x >= screen.X && y >= screen.Y && x < screen.X + screen.Width && y < screen.Y + screen.Height;
        switch (profile.Position)
        {
            case PositionMode.Fixed when !Inside(profile.FixedX, profile.FixedY):
                errors.Add(new("ac.err.position", profile.FixedX, profile.FixedY));
                break;
            case PositionMode.Sequence when profile.Points.Count == 0:
                errors.Add(new("ac.err.noPoints"));
                break;
            case PositionMode.Sequence:
                foreach (var point in profile.Points.Where(p => !Inside(p.X, p.Y) || p.DelayMs is < 0 or double.NaN))
                    errors.Add(new("ac.err.position", point.X, point.Y));
                break;
            case PositionMode.RandomArea when profile.Area is not { IsValid: true } area
                                              || !Inside(area.X, area.Y) || !Inside(area.X + area.Width - 1, area.Y + area.Height - 1):
                errors.Add(new("ac.err.area"));
                break;
        }

        if (errors.Count > 0) return (null, errors);

        var clicks = ClickMath.ClicksPerAction(profile.ClickKind);
        var (action, hold, gap) = ClickMath.Timing(profile, systemDoubleClickMs);

        return (new AutoClickerRunConfiguration
        {
            Kind = RunKind.Clicker,
            ProfileName = profile.Name,
            TestMode = testMode,
            IntervalMs = interval,
            ActionIntervalMs = action,
            JitterMs = jitter,
            Button = profile.Button,
            ClickKind = profile.ClickKind,
            ClicksPerAction = clicks,
            HoldMs = hold,
            GapMs = gap,
            Repeat = profile.Repeat,
            ClickLimit = profile.Repeat == RepeatMode.Count ? profile.ClickCount : null,
            Duration = profile.Repeat == RepeatMode.Duration ? TimeSpan.FromSeconds(profile.DurationSeconds) : null,
            StartDelay = profile.StartDelay ? TimeSpan.FromSeconds(profile.StartDelaySeconds) : TimeSpan.Zero,
            IsHeld = profile.Repeat == RepeatMode.WhileHeld ? isHeld : null,
            Position = profile.Position,
            FixedX = profile.FixedX,
            FixedY = profile.FixedY,
            Points = profile.Points.ToImmutableArray(),
            LoopPoints = profile.LoopPoints,
            Area = profile.Area,
            MoveBack = profile.MoveBack,
            MaxRuntime = profile.MaxRuntimeEnabled ? TimeSpan.FromMinutes(profile.MaxRuntimeMinutes) : null,
            MaxClicks = profile.MaxClicksEnabled ? profile.MaxClicks : null
        }, errors);
    }

    /// <summary>Configuration that replays a recorded sequence.</summary>
    public static (AutoClickerRunConfiguration? Config, List<ValidationError> Errors) ForPlayback(
        ClickSequence sequence, AutoClickerProfile safety, bool testMode, ScreenArea screen)
    {
        var errors = new List<ValidationError>();
        var events = sequence.Events.Where(e => e.Enabled).ToList();
        if (events.Count == 0) errors.Add(new("ac.err.emptySequence"));
        if (double.IsNaN(sequence.Speed) || sequence.Speed is < 0.1 or > 20) errors.Add(new("ac.err.speed"));
        if (sequence.Repeat is < 1 or > 100_000) errors.Add(new("ac.err.repeat"));
        foreach (var e in events.Where(e => e.X < screen.X || e.Y < screen.Y || e.X >= screen.X + screen.Width || e.Y >= screen.Y + screen.Height || e.DelayMs < 0))
            errors.Add(new("ac.err.position", e.X, e.Y));
        if (errors.Count > 0) return (null, errors);
        return (new AutoClickerRunConfiguration
        {
            Kind = RunKind.Playback,
            ProfileName = sequence.Name,
            TestMode = testMode,
            IntervalMs = 1000,
            ActionIntervalMs = 1000,
            Sequence = events.Select(Copy).ToImmutableArray(),
            SequenceSpeed = sequence.Speed,
            SequenceRepeat = sequence.Repeat,
            MaxRuntime = safety.MaxRuntimeEnabled ? TimeSpan.FromMinutes(safety.MaxRuntimeMinutes) : null,
            MaxClicks = safety.MaxClicksEnabled ? safety.MaxClicks : null
        }, errors);
    }

    private static ClickSequenceEvent Copy(ClickSequenceEvent e) => new()
    {
        Enabled = e.Enabled, Kind = e.Kind, Button = e.Button, X = e.X, Y = e.Y, DelayMs = e.DelayMs, HoldMs = e.HoldMs, WheelDelta = e.WheelDelta
    };
}
