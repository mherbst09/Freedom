using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ViennaAtomicClock.AutoClicker;

public enum MouseActionKind { Move, Down, Up, Wheel }

/// <summary>One mouse input. Coordinates are physical virtual-desktop pixels (only used for <see cref="MouseActionKind.Move"/>).</summary>
public readonly record struct MouseAction(MouseActionKind Kind, MouseButtonKind Button = MouseButtonKind.Left, int X = 0, int Y = 0, int WheelDelta = 0)
{
    public static MouseAction Move(int x, int y) => new(MouseActionKind.Move, X: x, Y: y);
    public static MouseAction Down(MouseButtonKind button) => new(MouseActionKind.Down, button);
    public static MouseAction Up(MouseButtonKind button) => new(MouseActionKind.Up, button);
    public static MouseAction Wheel(int delta) => new(MouseActionKind.Wheel, WheelDelta: delta);
}

/// <summary>Sends mouse input. Implementations must be thread-safe.</summary>
public interface IMouseInputService
{
    /// <summary>Sends the actions as one uninterrupted batch; returns how many Windows accepted.</summary>
    int Send(ReadOnlySpan<MouseAction> actions);

    bool TryGetCursor(out int x, out int y);

    /// <summary>Win32 error of the last failed <see cref="Send"/> (0 if none).</summary>
    int LastError { get; }
}

/// <summary>Test mode: accepts everything and does nothing.</summary>
public sealed class NullMouseInputService : IMouseInputService
{
    public static readonly NullMouseInputService Instance = new();
    public int Send(ReadOnlySpan<MouseAction> actions) => actions.Length;
    public bool TryGetCursor(out int x, out int y) { x = y = 0; return false; }
    public int LastError => 0;
}

/// <summary>
/// Win32 SendInput. Normal programs cannot inject input into programs running with administrator rights
/// (UIPI) or into the secure desktop (lock screen, UAC prompt); Windows silently drops such input – Freedom
/// never tries to get around that.
/// </summary>
public sealed class WindowsMouseInputService : IMouseInputService
{
    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventXDown = 0x0080;
    private const uint MouseEventXUp = 0x0100;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventVirtualDesk = 0x4000;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;

    [ThreadStatic] private static Input[]? _buffer;
    private int _lastError;

    public int LastError => _lastError;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HardwareInput
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    /// <summary>The union must have the size of its largest member, or SendInput rejects the whole array.</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>Size Windows expects per INPUT (40 bytes on x64, 28 on x86).</summary>
    public static int InputSize => Marshal.SizeOf<Input>();

    public int Send(ReadOnlySpan<MouseAction> actions)
    {
        if (actions.Length == 0) return 0;
        var buffer = _buffer is { } b && b.Length >= actions.Length ? b : _buffer = new Input[Math.Max(8, actions.Length)];
        var screen = ScreenInfo.VirtualScreen();
        for (var i = 0; i < actions.Length; i++) buffer[i] = ToInput(actions[i], screen);
        var sent = (int)SendInput((uint)actions.Length, buffer, InputSize);
        _lastError = sent == actions.Length ? 0 : Marshal.GetLastWin32Error();
        return sent;
    }

    public bool TryGetCursor(out int x, out int y)
    {
        var ok = GetCursorPos(out var point);
        x = point.X;
        y = point.Y;
        return ok;
    }

    internal static Input ToInput(MouseAction action, ScreenArea screen)
    {
        var mouse = new MouseInput();
        switch (action.Kind)
        {
            case MouseActionKind.Move:
                var (ax, ay) = ToAbsolute(action.X, action.Y, screen);
                mouse.Dx = ax;
                mouse.Dy = ay;
                mouse.Flags = MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk;
                break;
            case MouseActionKind.Wheel:
                mouse.Flags = MouseEventWheel;
                mouse.MouseData = unchecked((uint)action.WheelDelta);
                break;
            default:
                (mouse.Flags, mouse.MouseData) = ButtonFlags(action.Button, action.Kind == MouseActionKind.Down);
                break;
        }
        return new Input { Type = InputMouse, Data = new InputUnion { Mouse = mouse } };
    }

    public static (uint Flags, uint Data) ButtonFlags(MouseButtonKind button, bool down) => button switch
    {
        MouseButtonKind.Right => (down ? MouseEventRightDown : MouseEventRightUp, 0u),
        MouseButtonKind.Middle => (down ? MouseEventMiddleDown : MouseEventMiddleUp, 0u),
        MouseButtonKind.X1 => (down ? MouseEventXDown : MouseEventXUp, XButton1),
        MouseButtonKind.X2 => (down ? MouseEventXDown : MouseEventXUp, XButton2),
        _ => (down ? MouseEventLeftDown : MouseEventLeftUp, 0u)
    };

    /// <summary>
    /// Physical pixel → 0..65535 over the whole virtual desktop (MOUSEEVENTF_VIRTUALDESK). The left/top edge may be
    /// negative when a monitor sits left of or above the main one.
    /// </summary>
    public static (int X, int Y) ToAbsolute(int x, int y, ScreenArea screen)
    {
        static int Map(int value, int origin, int length) =>
            length <= 1 ? 0 : (int)Math.Round((value - origin) * 65535.0 / (length - 1));
        return (Map(x, screen.X, screen.Width), Map(y, screen.Y, screen.Height));
    }

    /// <summary>Inverse of <see cref="ToAbsolute"/> as Windows computes it (for tests).</summary>
    public static (int X, int Y) FromAbsolute(int ax, int ay, ScreenArea screen) =>
        ((int)Math.Round(ax * (screen.Width - 1) / 65535.0) + screen.X, (int)Math.Round(ay * (screen.Height - 1) / 65535.0) + screen.Y);

    public static string ErrorText(int error) => error == 0 ? "" : new Win32Exception(error).Message;
}

/// <summary>Monitors, virtual desktop and DPI (all in physical pixels; Freedom is per-monitor DPI aware).</summary>
public static class ScreenInfo
{
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    public sealed record Monitor(int Index, string Device, ScreenArea Bounds, bool Primary, double Scale);

    public static ScreenArea VirtualScreen()
    {
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        return width > 0 && height > 0
            ? new ScreenArea(GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen), width, height)
            : new ScreenArea(0, 0, 1920, 1080);
    }

    public static List<Monitor> Monitors()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref Rect _, IntPtr _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(handle, ref info))
            {
                var scale = GetDpiForMonitor(handle, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
                list.Add(new Monitor(list.Count + 1, info.Device,
                    new ScreenArea(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top),
                    (info.Flags & 1) != 0, scale));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The monitor containing a physical point (or null when it is off all screens).</summary>
    public static Monitor? MonitorAt(int x, int y) =>
        Monitors().FirstOrDefault(m => x >= m.Bounds.X && y >= m.Bounds.Y && x < m.Bounds.X + m.Bounds.Width && y < m.Bounds.Y + m.Bounds.Height);
}
