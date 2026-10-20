using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ViennaAtomicClock.Weather;

namespace ViennaAtomicClock;

/// <summary>Shows weather notifications as a small card inside Freedom and lets the taskbar button blink once when the window is in the background.</summary>
internal sealed class InAppNotifier : IWeatherNotificationService
{
    private readonly MainWindow _window;
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(10) };

    public InAppNotifier(MainWindow window)
    {
        _window = window;
        _hide.Tick += (_, _) => { _hide.Stop(); _window.HideToast(); };
    }

    public void Notify(string title, string message, AlertSeverity? severity)
    {
        _window.Dispatcher.BeginInvoke(() =>
        {
            var stripe = severity is { } s ? WeatherIcons.Frozen(WeatherPage.SeverityColor(s)) : null;
            _window.ShowToast(title, message, stripe);
            _hide.Stop();
            _hide.Start();
            if (!_window.IsActive) Flash(_window);
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    private static void Flash(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        // FLASHW_TRAY (2) | FLASHW_TIMERNOFG (12): blink the taskbar button until the window comes to the front.
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = 2 | 12, Count = 3 };
        FlashWindowEx(ref info);
    }
}
