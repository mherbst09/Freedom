using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ViennaAtomicClock.AutoClicker;

/// <summary>
/// Full-screen picker on every monitor: crosshair, live physical coordinates next to the cursor, left click
/// confirms, Escape cancels. One window per monitor keeps each one at its own DPI; coordinates always come from
/// GetCursorPos, so WPF units and physical pixels are never mixed up.
/// </summary>
internal sealed class CursorPickerWindow : Window
{
    private static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromArgb(46, 0, 0, 0)));
    private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)));
    private static readonly Brush LineShadow = Freeze(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    private static readonly Brush AreaFill = Freeze(new SolidColorBrush(Color.FromArgb(50, 0x3D, 0x8B, 0xFF)));
    private static readonly Brush AreaStroke = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0xFF)));

    private readonly ScreenArea _bounds;
    private readonly bool _area;
    private readonly Action<CursorPickerWindow, object?> _done;
    private readonly Canvas _canvas = new() { Background = Dim, ClipToBounds = true };
    private readonly Line _horizontal = new() { Stroke = LineBrush, StrokeThickness = 1, IsHitTestVisible = false };
    private readonly Line _vertical = new() { Stroke = LineBrush, StrokeThickness = 1, IsHitTestVisible = false };
    private readonly Line _horizontalShadow = new() { Stroke = LineShadow, StrokeThickness = 3, IsHitTestVisible = false };
    private readonly Line _verticalShadow = new() { Stroke = LineShadow, StrokeThickness = 3, IsHitTestVisible = false };
    private readonly Border _label = new() { Background = Freeze(new SolidColorBrush(Color.FromArgb(230, 20, 22, 28))), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), IsHitTestVisible = false };
    private readonly TextBlock _labelText = new() { Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Consolas, Segoe UI") };
    private readonly Rectangle _selection = new() { Fill = AreaFill, Stroke = AreaStroke, StrokeThickness = 2, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private Point? _dragStart;
    private (int X, int Y)? _dragStartPhysical;

    private CursorPickerWindow(ScreenArea bounds, bool area, string hint, Action<CursorPickerWindow, object?> done)
    {
        _bounds = bounds;
        _area = area;
        _done = done;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        Cursor = Cursors.Cross;
        Left = 0;
        Top = 0;
        Width = 100;
        Height = 100;

        _label.Child = _labelText;
        var banner = new Border
        {
            Background = Freeze(new SolidColorBrush(Color.FromArgb(235, 20, 22, 28))), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 10, 16, 10),
            Child = new TextBlock { Text = hint, Foreground = Brushes.White, FontSize = 15, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 },
            IsHitTestVisible = false
        };
        _canvas.Children.Add(_selection);
        _canvas.Children.Add(_horizontalShadow);
        _canvas.Children.Add(_verticalShadow);
        _canvas.Children.Add(_horizontal);
        _canvas.Children.Add(_vertical);
        _canvas.Children.Add(_label);
        _canvas.Children.Add(banner);
        banner.Loaded += (_, _) => { Canvas.SetLeft(banner, (ActualWidth - banner.ActualWidth) / 2); Canvas.SetTop(banner, 24); };
        Content = _canvas;
        System.Windows.Automation.AutomationProperties.SetName(this, hint);

        SourceInitialized += (_, _) =>
        {
            // Physical placement on the monitor (Freedom is per-monitor DPI aware).
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
        };
        MouseMove += (_, e) => Track(e.GetPosition(_canvas));
        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += OnUp;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            _done(this, null);
        };
        MouseRightButtonDown += (_, _) => _done(this, null);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void Track(Point local)
    {
        _horizontal.X1 = _horizontalShadow.X1 = 0;
        _horizontal.X2 = _horizontalShadow.X2 = ActualWidth;
        _horizontal.Y1 = _horizontal.Y2 = _horizontalShadow.Y1 = _horizontalShadow.Y2 = Math.Round(local.Y) + 0.5;
        _vertical.Y1 = _verticalShadow.Y1 = 0;
        _vertical.Y2 = _verticalShadow.Y2 = ActualHeight;
        _vertical.X1 = _vertical.X2 = _verticalShadow.X1 = _verticalShadow.X2 = Math.Round(local.X) + 0.5;
        GetCursorPos(out var p);
        _labelText.Text = _dragStartPhysical is { } start
            ? string.Create(CultureInfo.InvariantCulture, $"X {Math.Min(start.X, p.X)}  Y {Math.Min(start.Y, p.Y)}  ·  {Math.Abs(p.X - start.X) + 1} × {Math.Abs(p.Y - start.Y) + 1}")
            : string.Create(CultureInfo.InvariantCulture, $"X {p.X}   Y {p.Y}");
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = local.X + 18 + _label.DesiredSize.Width > ActualWidth ? local.X - 18 - _label.DesiredSize.Width : local.X + 18;
        var top = local.Y + 18 + _label.DesiredSize.Height > ActualHeight ? local.Y - 18 - _label.DesiredSize.Height : local.Y + 18;
        Canvas.SetLeft(_label, left);
        Canvas.SetTop(_label, top);

        if (_dragStart is { } origin)
        {
            Canvas.SetLeft(_selection, Math.Min(origin.X, local.X));
            Canvas.SetTop(_selection, Math.Min(origin.Y, local.Y));
            _selection.Width = Math.Abs(local.X - origin.X);
            _selection.Height = Math.Abs(local.Y - origin.Y);
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        GetCursorPos(out var p);
        if (!_area)
        {
            _done(this, (p.X, p.Y));
            return;
        }
        _dragStart = e.GetPosition(_canvas);
        _dragStartPhysical = (p.X, p.Y);
        _selection.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_area || _dragStartPhysical is not { } start) return;
        ReleaseMouseCapture();
        GetCursorPos(out var p);
        var area = new ScreenArea(Math.Min(start.X, p.X), Math.Min(start.Y, p.Y), Math.Abs(p.X - start.X) + 1, Math.Abs(p.Y - start.Y) + 1);
        _dragStart = null;
        _dragStartPhysical = null;
        _selection.Visibility = Visibility.Collapsed;
        _done(this, area.Width >= 3 && area.Height >= 3 ? area : null);
    }

    /// <summary>Lets the user click a point; null when cancelled.</summary>
    public static Task<(int X, int Y)?> PickPointAsync(Window owner, string hint) =>
        Pick(owner, false, hint).ContinueWith(t => t.Result is ValueTuple<int, int> point ? ((int, int)?)point : null, TaskScheduler.FromCurrentSynchronizationContext());

    /// <summary>Lets the user drag a rectangle; null when cancelled.</summary>
    public static Task<ScreenArea?> PickAreaAsync(Window owner, string hint) =>
        Pick(owner, true, hint).ContinueWith(t => t.Result as ScreenArea, TaskScheduler.FromCurrentSynchronizationContext());

    private static Task<object?> Pick(Window owner, bool area, string hint)
    {
        var result = new TaskCompletionSource<object?>();
        var windows = new List<CursorPickerWindow>();
        var restore = owner.WindowState;
        owner.Hide(); // Freedom steps aside so it never covers the target

        void Done(CursorPickerWindow _, object? value)
        {
            if (result.Task.IsCompleted) return;
            foreach (var window in windows) window.Close();
            owner.Show();
            owner.WindowState = restore;
            owner.Activate();
            result.SetResult(value);
        }

        foreach (var monitor in ScreenInfo.Monitors())
        {
            var window = new CursorPickerWindow(monitor.Bounds, area, hint, Done);
            windows.Add(window);
            window.Show();
        }
        if (windows.Count == 0) Done(null!, null);
        // Keyboard focus goes to the window under the cursor, so Escape works right away.
        GetCursorPos(out var cursor);
        (windows.FirstOrDefault(w => cursor.X >= w._bounds.X && cursor.X < w._bounds.X + w._bounds.Width && cursor.Y >= w._bounds.Y && cursor.Y < w._bounds.Y + w._bounds.Height) ?? windows.FirstOrDefault())?.Activate();
        return result.Task;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
