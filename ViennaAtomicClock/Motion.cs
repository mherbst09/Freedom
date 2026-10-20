using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ViennaAtomicClock;

/// <summary>Small motion helpers that make the app feel fluid: page fades, popup pops, crossfades and eased scrolling.</summary>
internal static class Motion
{
    private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static IEasingFunction Freeze(CubicEase ease)
    {
        ease.Freeze();
        return ease;
    }

    /// <summary>Fades a page in and lets it glide up a few pixels.</summary>
    public static void PageIn(UIElement element)
    {
        var shift = new TranslateTransform(0, 14);
        element.RenderTransform = shift;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = EaseOut };
        var glide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = EaseOut };
        // Drop the transform afterwards so text snaps back to whole pixels.
        glide.Completed += (_, _) => { if (element.RenderTransform == shift) element.RenderTransform = Transform.Identity; };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.YProperty, glide);
    }

    /// <summary>Popup content grows out of its button.</summary>
    public static void PopIn(FrameworkElement element, double originX = 0.5)
    {
        var scale = new ScaleTransform(0.94, 0.94);
        element.RenderTransformOrigin = new Point(originX, 0);
        element.RenderTransform = scale;
        var grow = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = EaseOut };
        grow.Completed += (_, _) => { if (element.RenderTransform == scale) element.RenderTransform = Transform.Identity; };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = EaseOut });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>Takes a picture of <paramref name="surface"/>, runs the change and fades the old picture out over the new look.</summary>
    public static void Crossfade(FrameworkElement surface, Panel host, Action change)
    {
        Image? cover = null;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(surface);
            var width = (int)Math.Ceiling(surface.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(surface.ActualHeight * dpi.DpiScaleY);
            if (width > 0 && height > 0 && width * height < 40_000_000)
            {
                var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                bitmap.Freeze();
                cover = new Image { Source = bitmap, Stretch = Stretch.Fill, IsHitTestVisible = false, Margin = surface.Margin };
                Panel.SetZIndex(cover, 10_000);
                Grid.SetRowSpan(cover, 99);
                Grid.SetColumnSpan(cover, 99);
                host.Children.Add(cover);
            }
        }
        catch
        {
            // Without a snapshot the change simply happens at once.
        }

        change();
        if (cover is null) return;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = EaseOut };
        fade.Completed += (_, _) => host.Children.Remove(cover);
        cover.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Animates a property of an element towards a value (hover states built in code).</summary>
    public static void To(UIElement element, DependencyProperty property, double value, int milliseconds = 140) =>
        element.BeginAnimation(property, new DoubleAnimation(value, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = EaseOut });
}

/// <summary>
/// Eased mouse-wheel scrolling for every scroll viewer in a window: each notch sets a target and the
/// content glides there frame by frame instead of jumping in steps.
/// </summary>
internal static class SmoothScroll
{
    private sealed class State
    {
        public double Target;
        public bool Running;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    public static void Attach(Window window) => window.PreviewMouseWheel += OnWheel;

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is not ScrollViewer viewer || viewer.ScrollableHeight <= 0) continue;
            if (viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
            // Item-by-item scrolling (virtualised lists) keeps its own behaviour.
            if (viewer.CanContentScroll && viewer.TemplatedParent is ItemsControl list && VirtualizingPanel.GetScrollUnit(list) != ScrollUnit.Pixel) return;

            var state = States.GetOrCreateValue(viewer);
            if (!state.Running) state.Target = viewer.VerticalOffset;
            var down = e.Delta < 0;
            var atEnd = down ? state.Target >= viewer.ScrollableHeight - 0.5 : state.Target <= 0.5;
            if (atEnd) continue; // Let an outer scroll viewer take over.

            state.Target = Math.Clamp(state.Target - e.Delta * 0.55, 0, viewer.ScrollableHeight);
            e.Handled = true;
            if (!state.Running) Run(viewer, state);
            return;
        }
    }

    private static void Run(ScrollViewer viewer, State state)
    {
        state.Running = true;
        var last = DateTime.UtcNow;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            var now = DateTime.UtcNow;
            var dt = Math.Min(0.05, (now - last).TotalSeconds);
            last = now;
            state.Target = Math.Clamp(state.Target, 0, viewer.ScrollableHeight);
            var offset = viewer.VerticalOffset;
            var step = (state.Target - offset) * Math.Min(1, dt * 16);
            if (Math.Abs(state.Target - offset) < 0.6 || !viewer.IsVisible)
            {
                viewer.ScrollToVerticalOffset(Math.Round(state.Target));
                CompositionTarget.Rendering -= tick;
                state.Running = false;
                return;
            }
            viewer.ScrollToVerticalOffset(offset + step);
        };
        CompositionTarget.Rendering += tick;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}

/// <summary>Native Windows 11 frame for the borderless window: rounded corners and the system shadow without a transparent (slow) window.</summary>
internal static class WindowFrame
{
    private const int CornerPreference = 33;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>Rounded corners (true) or square ones for fullscreen (false).</summary>
    public static void SetRounded(Window window, bool rounded)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var value = rounded ? 2 : 1;
            DwmSetWindowAttribute(handle, CornerPreference, ref value, sizeof(int));
        }
        catch
        {
            // Older Windows versions simply keep square corners.
        }
    }

    /// <summary>How far a maximised borderless window hangs over the screen edge, in device-independent pixels.</summary>
    public static Thickness MaximizedOverhang(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var resize = SystemParameters.WindowResizeBorderThickness;
        var padded = GetSystemMetrics(92) / dpi.DpiScaleX;
        return new Thickness(resize.Left + padded, resize.Top + padded, resize.Right + padded, resize.Bottom + padded);
    }
}
