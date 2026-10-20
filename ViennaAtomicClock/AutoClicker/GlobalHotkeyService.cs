using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ViennaAtomicClock.AutoClicker;

public enum HotkeyStatus { None, Registered, Duplicate, InUse }

public interface IGlobalHotkeyService : IDisposable
{
    /// <summary>Raised on the UI thread; handlers must return quickly.</summary>
    event Action<HotkeyAction>? Pressed;

    /// <summary>Raised when Windows reports the session locked or the display switched off.</summary>
    event Action<string>? SystemSignal;

    IReadOnlyDictionary<HotkeyAction, HotkeyStatus> Apply(IReadOnlyDictionary<HotkeyAction, Hotkey?> map);

    /// <summary>Extra registration that only exists for a while (Escape as emergency stop during a run).</summary>
    bool SetTemporary(HotkeyAction action, Hotkey? hotkey);

    void UnregisterAll();
}

/// <summary>
/// Global hotkeys through RegisterHotKey on a hidden message window. Keyboard input is never recorded:
/// Windows only reports the registered combinations. Mouse side buttons (X1/X2) need a low-level mouse hook,
/// which is installed only while such a hotkey is configured and only looks at side-button presses.
/// </summary>
public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private const int WmWtsSessionChange = 0x02B1;
    private const int WtsSessionLock = 0x7;
    private const uint ModNoRepeat = 0x4000;
    private const int FirstId = 0x6100;
    private const int TemporaryId = 0x61FF;
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly HwndSource _window;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, HotkeyAction> _ids = new();
    private readonly Dictionary<MouseButtonKind, (HotkeyAction Action, HotkeyModifiers Modifiers)> _mouseKeys = new();
    private readonly IntPtr _powerNotification;
    private LowLevelMouseHook? _mouseHook;
    private HotkeyAction? _temporaryAction;
    private bool _temporaryRegistered;

    public event Action<HotkeyAction>? Pressed;
    public event Action<string>? SystemSignal;

    public GlobalHotkeyService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        // A message-only window: invisible, never activated, only receives WM_HOTKEY and power messages.
        _window = new HwndSource(new HwndSourceParameters("Freedom hotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WindowProc);
        var guid = ConsoleDisplayState;
        _powerNotification = RegisterPowerSettingNotification(_window.Handle, ref guid, 0);
        WTSRegisterSessionNotification(_window.Handle, 0);
    }

    /// <summary>Actions that share the same key combination (they cannot all work).</summary>
    public static HashSet<HotkeyAction> FindDuplicates(IReadOnlyDictionary<HotkeyAction, Hotkey?> map) =>
        map.Where(p => p.Value is not null)
            .GroupBy(p => p.Value!)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(p => p.Key))
            .ToHashSet();

    public IReadOnlyDictionary<HotkeyAction, HotkeyStatus> Apply(IReadOnlyDictionary<HotkeyAction, Hotkey?> map)
    {
        UnregisterMain();
        var result = new Dictionary<HotkeyAction, HotkeyStatus>();
        var duplicates = FindDuplicates(map);
        var id = FirstId;
        foreach (var (action, hotkey) in map)
        {
            if (hotkey is null) { result[action] = HotkeyStatus.None; continue; }
            if (duplicates.Contains(action)) { result[action] = HotkeyStatus.Duplicate; continue; }
            if (hotkey.IsMouse)
            {
                _mouseKeys[hotkey.MouseButton!.Value] = (action, hotkey.Modifiers);
                result[action] = HotkeyStatus.Registered;
                continue;
            }
            if (RegisterHotKey(_window.Handle, id, ToNative(hotkey.Modifiers) | ModNoRepeat, (uint)hotkey.VirtualKey))
            {
                _ids[id] = action;
                result[action] = HotkeyStatus.Registered;
            }
            else result[action] = HotkeyStatus.InUse; // another program owns this combination
            id++;
        }

        if (_mouseKeys.Count > 0 && _mouseHook is null)
        {
            _mouseHook = new LowLevelMouseHook(OnHookEvent);
            if (!_mouseHook.Install())
            {
                _mouseHook = null;
                foreach (var (_, (action, _)) in _mouseKeys) result[action] = HotkeyStatus.InUse;
                _mouseKeys.Clear();
            }
        }
        return result;
    }

    public bool SetTemporary(HotkeyAction action, Hotkey? hotkey)
    {
        if (_temporaryRegistered)
        {
            UnregisterHotKey(_window.Handle, TemporaryId);
            _temporaryRegistered = false;
        }
        _temporaryAction = hotkey is null ? null : action;
        if (hotkey is null || hotkey.IsMouse) return false;
        _temporaryRegistered = RegisterHotKey(_window.Handle, TemporaryId, ToNative(hotkey.Modifiers) | ModNoRepeat, (uint)hotkey.VirtualKey);
        return _temporaryRegistered;
    }

    private void UnregisterMain()
    {
        foreach (var id in _ids.Keys) UnregisterHotKey(_window.Handle, id);
        _ids.Clear();
        _mouseKeys.Clear();
        _mouseHook?.Dispose();
        _mouseHook = null;
    }

    public void UnregisterAll()
    {
        UnregisterMain();
        SetTemporary(HotkeyAction.EmergencyStop, null);
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_powerNotification != IntPtr.Zero) UnregisterPowerSettingNotification(_powerNotification);
        WTSUnRegisterSessionNotification(_window.Handle);
        _window.RemoveHook(WindowProc);
        _window.Dispose();
    }

    private void OnHookEvent(LowLevelMouseHook.Event e)
    {
        if (!e.Down || e.Injected || e.Button is not (MouseButtonKind.X1 or MouseButtonKind.X2)) return;
        if (!_mouseKeys.TryGetValue(e.Button, out var entry)) return;
        if (CurrentModifiers() != entry.Modifiers) return;
        // Leave the hook immediately; the action runs on the next dispatcher turn.
        _dispatcher.BeginInvoke(() => Pressed?.Invoke(entry.Action));
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmHotkey:
                var id = wParam.ToInt32();
                if (id == TemporaryId && _temporaryAction is { } temporary) Pressed?.Invoke(temporary);
                else if (_ids.TryGetValue(id, out var action)) Pressed?.Invoke(action);
                handled = true;
                break;
            case WmPowerBroadcast when wParam.ToInt32() == PbtPowerSettingChange && lParam != IntPtr.Zero:
                var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
                if (setting.PowerSetting == ConsoleDisplayState && setting.Data == 0) SystemSignal?.Invoke("display");
                break;
            case WmWtsSessionChange when wParam.ToInt32() == WtsSessionLock:
                SystemSignal?.Invoke("lock");
                break;
        }
        return IntPtr.Zero;
    }

    private static uint ToNative(HotkeyModifiers modifiers) =>
        (modifiers.HasFlag(HotkeyModifiers.Alt) ? 1u : 0) | (modifiers.HasFlag(HotkeyModifiers.Control) ? 2u : 0) |
        (modifiers.HasFlag(HotkeyModifiers.Shift) ? 4u : 0) | (modifiers.HasFlag(HotkeyModifiers.Windows) ? 8u : 0);

    private static HotkeyModifiers CurrentModifiers() =>
        (IsDown(0x11) ? HotkeyModifiers.Control : 0) | (IsDown(0x12) ? HotkeyModifiers.Alt : 0) |
        (IsDown(0x10) ? HotkeyModifiers.Shift : 0) | (IsDown(0x5B) || IsDown(0x5C) ? HotkeyModifiers.Windows : 0);

    /// <summary>Is this one key (or mouse button) held right now? Used only for the configured hold-to-click key.</summary>
    public static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public static int VirtualKeyOf(Hotkey hotkey) => hotkey.MouseButton switch
    {
        MouseButtonKind.X1 => 0x05,
        MouseButtonKind.X2 => 0x06,
        _ => hotkey.VirtualKey
    };

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
}

/// <summary>
/// WH_MOUSE_LL hook. Only mouse buttons and the wheel are looked at – never keyboard input, window contents or
/// the clipboard. The callback does almost nothing so the system stays responsive.
/// </summary>
public sealed class LowLevelMouseHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int LlmhfInjected = 0x01;

    public readonly record struct Event(bool Down, bool Wheel, MouseButtonKind Button, int X, int Y, int WheelDelta, bool Injected, long Timestamp);

    private readonly Action<Event> _callback;
    private readonly HookProc _proc;
    private IntPtr _hook;

    public LowLevelMouseHook(Action<Event> callback)
    {
        _callback = callback;
        _proc = Proc; // keep the delegate alive while the hook exists
    }

    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Translates a hook message (pure, for tests).</summary>
    public static Event? Translate(int message, int x, int y, uint mouseData, uint flags, long timestamp)
    {
        var injected = (flags & LlmhfInjected) != 0;
        var high = (short)(mouseData >> 16);
        return message switch
        {
            0x0201 => new Event(true, false, MouseButtonKind.Left, x, y, 0, injected, timestamp),
            0x0202 => new Event(false, false, MouseButtonKind.Left, x, y, 0, injected, timestamp),
            0x0204 => new Event(true, false, MouseButtonKind.Right, x, y, 0, injected, timestamp),
            0x0205 => new Event(false, false, MouseButtonKind.Right, x, y, 0, injected, timestamp),
            0x0207 => new Event(true, false, MouseButtonKind.Middle, x, y, 0, injected, timestamp),
            0x0208 => new Event(false, false, MouseButtonKind.Middle, x, y, 0, injected, timestamp),
            0x020B => new Event(true, false, high == 2 ? MouseButtonKind.X2 : MouseButtonKind.X1, x, y, 0, injected, timestamp),
            0x020C => new Event(false, false, high == 2 ? MouseButtonKind.X2 : MouseButtonKind.X1, x, y, 0, injected, timestamp),
            0x020A => new Event(false, true, MouseButtonKind.Middle, x, y, high, injected, timestamp),
            _ => null // mouse moves and everything else are ignored
        };
    }

    private IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
            if (Translate(wParam.ToInt32(), data.X, data.Y, data.MouseData, data.Flags, System.Diagnostics.Stopwatch.GetTimestamp()) is { } e)
            {
                try { _callback(e); }
                catch { /* a hook must never throw into Windows */ }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}

/// <summary>Builds a sequence from hook events: relative times, down/up pairs become clicks.</summary>
public sealed class SequenceBuilder
{
    private const int PairDistance = 4;
    private const double PairMaxHoldMs = 1500;
    private readonly List<ClickSequenceEvent> _events = new();
    private readonly Dictionary<MouseButtonKind, (int Index, long Timestamp)> _openDowns = new();
    private readonly long _start;
    private long _lastTimestamp;

    public SequenceBuilder(long startTimestamp)
    {
        _start = startTimestamp;
        _lastTimestamp = startTimestamp;
    }

    public int Count => _events.Count;

    /// <summary>Adds a hook event. Injected input (Freedom's own clicks) and clicks on Freedom itself are skipped.</summary>
    public void Add(LowLevelMouseHook.Event e, bool onOwnWindow)
    {
        if (e.Injected || onOwnWindow) return;
        if (!e.Wheel && !e.Down && _openDowns.Remove(e.Button, out var open))
        {
            var down = _events[open.Index];
            if (Math.Abs(down.X - e.X) <= PairDistance && Math.Abs(down.Y - e.Y) <= PairDistance && ClickMath.TicksToMs(e.Timestamp - open.Timestamp) <= PairMaxHoldMs)
            {
                down.Kind = SequenceEventKind.Click;
                down.HoldMs = ClickMath.TicksToMs(e.Timestamp - open.Timestamp);
                return;
            }
        }

        var item = new ClickSequenceEvent
        {
            Kind = e.Wheel ? SequenceEventKind.Wheel : e.Down ? SequenceEventKind.Down : SequenceEventKind.Up,
            Button = e.Button,
            X = e.X,
            Y = e.Y,
            WheelDelta = e.WheelDelta,
            DelayMs = Math.Round(ClickMath.TicksToMs(e.Timestamp - _lastTimestamp), 1)
        };
        _lastTimestamp = e.Timestamp;
        _events.Add(item);
        if (e.Down && !e.Wheel) _openDowns[e.Button] = (_events.Count - 1, e.Timestamp);
    }

    public ClickSequence Build(string name) => new() { Name = name, Events = _events.ToList() };

    public TimeSpan Elapsed(long now) => TimeSpan.FromSeconds((now - _start) / (double)System.Diagnostics.Stopwatch.Frequency);
}

/// <summary>Records mouse actions (and nothing else) until stopped.</summary>
public sealed class ClickRecorderService : IDisposable
{
    private readonly Func<int, int, bool> _isOwnWindow;
    private LowLevelMouseHook? _hook;
    private SequenceBuilder? _builder;

    /// <param name="isOwnWindow">True for a physical point on Freedom's own window (those clicks are not recorded).</param>
    public ClickRecorderService(Func<int, int, bool> isOwnWindow) => _isOwnWindow = isOwnWindow;

    public bool IsRecording => _hook is not null;
    public int Count => _builder?.Count ?? 0;
    public event Action? Changed;

    public bool Start()
    {
        if (_hook is not null) return true;
        _builder = new SequenceBuilder(System.Diagnostics.Stopwatch.GetTimestamp());
        _hook = new LowLevelMouseHook(e =>
        {
            _builder?.Add(e, !e.Injected && _isOwnWindow(e.X, e.Y));
            Changed?.Invoke();
        });
        if (_hook.Install()) return true;
        _hook = null;
        return false;
    }

    public ClickSequence Stop(string name)
    {
        _hook?.Dispose();
        _hook = null;
        var sequence = _builder?.Build(name) ?? new ClickSequence { Name = name };
        _builder = null;
        return sequence;
    }

    public void Dispose() => Stop("");
}
