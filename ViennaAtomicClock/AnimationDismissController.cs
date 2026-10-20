using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ViennaAtomicClock;

/// <summary>
/// Adds unobtrusive ways to dismiss a click-through animation without making the full-screen
/// overlay intercept mouse input. Escape is registered only while the animation is visible and
/// the close glyph lives in its own tiny, non-activating window above the overlay.
/// </summary>
internal sealed class AnimationDismissController
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkEscape = 0x1B;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private static int _nextHotkeyId = 0x5200;

    private readonly Window _animation;
    private readonly int _hotkeyId = Interlocked.Increment(ref _nextHotkeyId);
    private HwndSource? _source;
    private Window? _closeWindow;
    private bool _hotkeyRegistered;

    public AnimationDismissController(Window animation)
    {
        _animation = animation;
        _animation.SourceInitialized += OnSourceInitialized;
        _animation.Loaded += OnLoaded;
        _animation.Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_animation).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowProc);
        _hotkeyRegistered = RegisterHotKey(handle, _hotkeyId, ModNoRepeat, VkEscape);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_closeWindow is not null || !_animation.IsVisible) return;

        var glyph = new TextBlock
        {
            Text = "×",
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0),
            IsHitTestVisible = false
        };
        var surface = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(150, 18, 18, 20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = glyph,
            Cursor = Cursors.Hand,
            ToolTip = "Animation schließen (Esc)"
        };

        var closeWindow = new Window
        {
            Owner = _animation,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            Width = 22,
            Height = 22,
            Left = _animation.Left + _animation.ActualWidth - 28,
            Top = _animation.Top + 6,
            Content = surface,
            Title = "Animation schließen"
        };
        closeWindow.SourceInitialized += (_, _) => MakeNonActivating(closeWindow);
        closeWindow.PreviewMouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            _animation.Close();
        };

        _closeWindow = closeWindow;
        closeWindow.Show();
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == _hotkeyId)
        {
            handled = true;
            _animation.Close();
        }
        return IntPtr.Zero;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(new WindowInteropHelper(_animation).Handle, _hotkeyId);
            _hotkeyRegistered = false;
        }

        if (_source is not null)
        {
            _source.RemoveHook(WindowProc);
            _source = null;
        }

        var closeWindow = _closeWindow;
        _closeWindow = null;
        if (closeWindow is { IsVisible: true }) closeWindow.Close();
    }

    private static void MakeNonActivating(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExToolWindow | WsExNoActivate);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
