using System.Diagnostics;
using System.Text.Json.Serialization;

namespace ViennaAtomicClock.AutoClicker;

public enum MouseButtonKind { Left, Right, Middle, X1, X2 }

public enum ClickKind { Single, Double, Triple, DownOnly, UpOnly, Hold }

public enum RepeatMode { UntilStopped, Count, Duration, WhileHeld }

public enum PositionMode { Current, Fixed, Sequence, RandomArea }

public enum IntervalInput { Time, Cps }

public enum AutoClickerState { Idle, Starting, Running, Paused, Stopping, Faulted }

public enum HotkeyAction { StartStop, Start, Stop, PauseResume, EmergencyStop, CapturePosition, RecordToggle, PlaybackToggle }

[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }

/// <summary>A key combination (virtual key + modifiers) or a mouse side button.</summary>
public sealed record Hotkey(int VirtualKey, HotkeyModifiers Modifiers = HotkeyModifiers.None, MouseButtonKind? MouseButton = null)
{
    public static Hotkey Key(int virtualKey, HotkeyModifiers modifiers = HotkeyModifiers.None) => new(virtualKey, modifiers);

    [JsonIgnore]
    public bool IsMouse => MouseButton is MouseButtonKind.X1 or MouseButtonKind.X2;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Strg");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Umschalt");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(IsMouse ? (MouseButton == MouseButtonKind.X1 ? "Maus X1" : "Maus X2") : KeyName(VirtualKey));
        return string.Join(" + ", parts);
    }

    public static string KeyName(int vk) => vk switch
    {
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
        0x1B => "Esc",
        0x20 => "Leertaste",
        0x13 => "Pause",
        0x2D => "Einfg",
        0x2E => "Entf",
        0x24 => "Pos1",
        0x23 => "Ende",
        0x21 => "Bild ↑",
        0x22 => "Bild ↓",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x91 => "Rollen",
        _ => "0x" + vk.ToString("X2")
    };
}

/// <summary>One point of a click route; <see cref="DelayMs"/> overrides the interval before this point.</summary>
public sealed record ClickPoint(int X, int Y, double? DelayMs = null);

/// <summary>Physical screen rectangle (virtual desktop pixels; may be negative on the left or top).</summary>
public sealed record ScreenArea(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width >= 1 && Height >= 1;
}

/// <summary>Everything a user can save as a profile (versioned JSON).</summary>
public sealed class AutoClickerProfile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Standard";

    // Interval
    public IntervalInput IntervalInput { get; set; } = IntervalInput.Cps;
    /// <summary>Milliseconds between two complete single clicks (1000 / CPS); may be fractional.</summary>
    public double IntervalMs { get; set; } = 100;
    public bool RandomJitter { get; set; }
    public double JitterMs { get; set; } = 10;

    // Click
    public MouseButtonKind Button { get; set; } = MouseButtonKind.Left;
    public ClickKind ClickKind { get; set; } = ClickKind.Single;
    public double HoldMs { get; set; } = 2;
    public double MultiClickGapMs { get; set; } = 40;
    public bool UseSystemDoubleClickTime { get; set; } = true;
    /// <summary>False (default): CPS counts single clicks, a double click uses two of them. True: CPS counts whole actions.</summary>
    public bool CpsCountsActions { get; set; }

    // Repeat
    public RepeatMode Repeat { get; set; } = RepeatMode.UntilStopped;
    public long ClickCount { get; set; } = 100;
    public double DurationSeconds { get; set; } = 60;
    public bool StartDelay { get; set; }
    public double StartDelaySeconds { get; set; } = 3;

    // Position
    public PositionMode Position { get; set; } = PositionMode.Current;
    public int FixedX { get; set; }
    public int FixedY { get; set; }
    public List<ClickPoint> Points { get; set; } = new();
    public bool LoopPoints { get; set; } = true;
    public ScreenArea? Area { get; set; }
    public bool MoveBack { get; set; }

    // Hotkeys
    public Dictionary<HotkeyAction, Hotkey?> Hotkeys { get; set; } = DefaultHotkeys();

    // Safety
    public bool StopOnLock { get; set; } = true;
    public bool StopOnDisplayOff { get; set; } = true;
    public bool MaxRuntimeEnabled { get; set; }
    public double MaxRuntimeMinutes { get; set; } = 30;
    public bool MaxClicksEnabled { get; set; }
    public long MaxClicks { get; set; } = 100_000;
    public bool EscapeStopsWhileRunning { get; set; } = true;
    public bool ConfirmLongRuns { get; set; }

    [JsonIgnore]
    public double Cps
    {
        get => IntervalMs > 0 ? 1000 / IntervalMs : 0;
        set => IntervalMs = value > 0 ? 1000 / value : 0;
    }

    public static Dictionary<HotkeyAction, Hotkey?> DefaultHotkeys() => new()
    {
        [HotkeyAction.StartStop] = Hotkey.Key(0x75), // F6
        [HotkeyAction.PauseResume] = Hotkey.Key(0x76), // F7
        [HotkeyAction.EmergencyStop] = Hotkey.Key(0x77), // F8
        [HotkeyAction.Start] = null,
        [HotkeyAction.Stop] = null,
        [HotkeyAction.CapturePosition] = null,
        [HotkeyAction.RecordToggle] = null,
        [HotkeyAction.PlaybackToggle] = null
    };

    public AutoClickerProfile Clone(string? name = null)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, AutoClickerStorage.JsonOptions);
        var copy = System.Text.Json.JsonSerializer.Deserialize<AutoClickerProfile>(json, AutoClickerStorage.JsonOptions)!;
        if (name is not null) copy.Name = name;
        return copy;
    }
}

/// <summary>Settings that are not part of a profile.</summary>
public sealed class AutoClickerSettings
{
    public string? DefaultProfile { get; set; }
    public string? LastProfile { get; set; }
    public bool HighCpsNoticeDismissed { get; set; }
    public bool GlobalHotkeys { get; set; } = true;
    public bool TestMode { get; set; }
    /// <summary>Set once the page was opened: from then on the global hotkeys are registered when Freedom starts.</summary>
    public bool Used { get; set; }
}

public enum SequenceEventKind { Click, Down, Up, Wheel }

/// <summary>One recorded mouse action. <see cref="DelayMs"/> is the pause before it.</summary>
public sealed class ClickSequenceEvent
{
    public bool Enabled { get; set; } = true;
    public SequenceEventKind Kind { get; set; }
    public MouseButtonKind Button { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public double DelayMs { get; set; }
    /// <summary>Time between down and up of a click.</summary>
    public double HoldMs { get; set; }
    public int WheelDelta { get; set; }
}

public sealed class ClickSequence
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Aufnahme";
    public List<ClickSequenceEvent> Events { get; set; } = new();
    /// <summary>2 = twice as fast.</summary>
    public double Speed { get; set; } = 1;
    public int Repeat { get; set; } = 1;
}

/// <summary>Validation and the CPS/interval relationship in one place.</summary>
public static class ClickMath
{
    public const double MinIntervalMs = 2;
    public const double MaxCps = 500;
    public const double MaxIntervalMs = 24 * 3600 * 1000.0;

    public static double IntervalFromCps(double cps) => 1000 / cps;
    public static double CpsFromInterval(double intervalMs) => 1000 / intervalMs;

    public static double IntervalFromParts(double hours, double minutes, double seconds, double milliseconds) =>
        ((hours * 60 + minutes) * 60 + seconds) * 1000 + milliseconds;

    public static (int Hours, int Minutes, int Seconds, double Milliseconds) PartsFromInterval(double intervalMs)
    {
        var total = Math.Max(0, intervalMs);
        var hours = (int)(total / 3_600_000);
        total -= hours * 3_600_000.0;
        var minutes = (int)(total / 60_000);
        total -= minutes * 60_000.0;
        var seconds = (int)(total / 1000);
        total -= seconds * 1000.0;
        return (hours, minutes, seconds, Math.Round(total, 3));
    }

    public static long MsToTicks(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000.0);
    public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public static int ClicksPerAction(ClickKind kind) => kind switch { ClickKind.Double => 2, ClickKind.Triple => 3, _ => 1 };

    /// <summary>Uniform jitter around the base interval, never below the 2 ms minimum.</summary>
    public static double Jittered(double baseMs, double jitterMs, Random random) =>
        jitterMs <= 0 ? Math.Max(MinIntervalMs, baseMs) : Math.Max(MinIntervalMs, baseMs + (random.NextDouble() * 2 - 1) * jitterMs);

    /// <summary>Range shown in the preview, e.g. 80–120 ms (the lower end never below 2 ms).</summary>
    public static (double Min, double Max) JitterRange(double baseMs, double jitterMs) =>
        (Math.Max(MinIntervalMs, baseMs - Math.Max(0, jitterMs)), Math.Max(MinIntervalMs, baseMs + Math.Max(0, jitterMs)));

    /// <summary>
    /// Real timing of one action: interval between actions, hold time and gap between the clicks of a double or
    /// triple click – limited so that everything fits into the interval (used by the engine and the page alike).
    /// </summary>
    public static (double ActionMs, double HoldMs, double GapMs) Timing(AutoClickerProfile profile, double systemDoubleClickMs)
    {
        var clicks = ClicksPerAction(profile.ClickKind);
        var action = profile.CpsCountsActions ? profile.IntervalMs : profile.IntervalMs * clicks;
        var hold = Math.Clamp(profile.HoldMs, 0, action / (2.0 * clicks));
        var gap = 0.0;
        if (clicks > 1)
        {
            gap = profile.MultiClickGapMs;
            if (profile.UseSystemDoubleClickTime && systemDoubleClickMs > 0) gap = Math.Min(gap, systemDoubleClickMs * 0.5);
            gap = Math.Max(0, Math.Min(gap, (action * 0.9 - clicks * hold) / (clicks - 1)));
        }
        if (profile.ClickKind is ClickKind.DownOnly or ClickKind.UpOnly or ClickKind.Hold) hold = 0;
        return (action, hold, gap);
    }
}
