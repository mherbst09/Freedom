using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ViennaAtomicClock;

/// <summary>
/// Colour picker in the style of Blender: hue/saturation wheel with a brightness bar beside it,
/// RGB/HSV tabs with labelled slider bars, alpha, a hex field and a screen eyedropper.
/// </summary>
internal sealed class ColorPicker : StackPanel
{
    private readonly HueWheel _wheel = new();
    private readonly ValueBar _valueBar = new();
    private readonly ChannelBar[] _bars = { new(), new(), new(), new() };
    private readonly Button _rgbTab;
    private readonly Button _hsvTab;
    private readonly TextBox _hexBox;
    private readonly Border _preview = new() { Width = 42, Height = 34, BorderThickness = new Thickness(1) };
    private double _h, _s = 1, _v = 1, _a = 1;
    private bool _hsvMode = true;
    private bool _updating;

    /// <summary>Raised continuously while the colour changes.</summary>
    public event Action<Color>? ColorChanged;

    /// <summary>Raised when a change is finished (mouse released, hex entered, eyedropper used).</summary>
    public event Action<Color>? ColorCommitted;

    public ColorPicker()
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        _rgbTab = Ui.Button("ChipButton", "RGB", (_, _) => { _hsvMode = false; Sync(); });
        _hsvTab = Ui.Button("ChipButton", "HSV", (_, _) => { _hsvMode = true; Sync(); });
        _rgbTab.Width = _hsvTab.Width = 64;
        _rgbTab.Height = _hsvTab.Height = 28;
        tabs.Children.Add(_rgbTab);
        tabs.Children.Add(_hsvTab);
        Children.Add(tabs);

        var wheelRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        wheelRow.Children.Add(_wheel);
        _valueBar.Margin = new Thickness(14, 0, 0, 0);
        wheelRow.Children.Add(_valueBar);
        Children.Add(wheelRow);

        _wheel.Picked += (h, s) => { _h = h; _s = s; Changed(); };
        _wheel.Released += Commit;
        _valueBar.Picked += v => { _v = v; Changed(); };
        _valueBar.Released += Commit;

        var bars = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        for (var i = 0; i < _bars.Length; i++)
        {
            var index = i;
            _bars[i].Margin = new Thickness(0, 0, 0, 4);
            _bars[i].Picked += value => BarChanged(index, value);
            _bars[i].Released += Commit;
            bars.Children.Add(_bars[i]);
        }
        Children.Add(bars);

        var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _preview.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        _preview.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        bottom.Children.Add(_preview);
        var (hexHost, hexBox) = Ui.Input("Hex");
        _hexBox = hexBox;
        _hexBox.Height = 34;
        _hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };
        _hexBox.LostFocus += (_, _) => ApplyHex();
        hexHost.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(hexHost, 1);
        bottom.Children.Add(hexHost);
        var pipette = Ui.Button("ChipButton", "⌖", (_, _) => PickFromScreen(), "Pipette: Farbe vom Bildschirm aufnehmen (Esc bricht ab)");
        pipette.Margin = new Thickness(0);
        pipette.Height = 34;
        pipette.Width = 40;
        pipette.FontSize = 18;
        Grid.SetColumn(pipette, 2);
        bottom.Children.Add(pipette);
        Children.Add(bottom);

        Sync();
    }

    public Color Color
    {
        get => ColorMath.FromHsv(_h, _s, _v, _a);
        set
        {
            (_h, _s, _v) = ColorMath.ToHsv(value);
            _a = value.A / 255.0;
            Sync();
        }
    }

    private void Changed()
    {
        Sync();
        ColorChanged?.Invoke(Color);
    }

    private void Commit() => ColorCommitted?.Invoke(Color);

    private void BarChanged(int index, double value)
    {
        if (index == 3) _a = value;
        else if (_hsvMode)
        {
            if (index == 0) _h = value * 360;
            if (index == 1) _s = value;
            if (index == 2) _v = value;
        }
        else
        {
            var c = Color;
            var rgb = new[] { c.R / 255.0, c.G / 255.0, c.B / 255.0 };
            rgb[index] = value;
            var hue = _h;
            (_h, _s, _v) = ColorMath.ToHsv(Color.FromRgb((byte)(rgb[0] * 255), (byte)(rgb[1] * 255), (byte)(rgb[2] * 255)));
            if (_s == 0) _h = hue; // keep the hue while the colour is grey
        }
        Changed();
    }

    private void ApplyHex()
    {
        var text = _hexBox.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (App.ParseColor(text) is not { } parsed) { Sync(); return; }
        var alpha = _a;
        Color = parsed;
        if (text.Length <= 7) _a = alpha;
        Changed();
        Commit();
    }

    private void Sync()
    {
        if (_updating) return;
        _updating = true;
        var c = Color;
        var opaque = Color.FromRgb(c.R, c.G, c.B);
        _wheel.Set(_h, _s, _v);
        _valueBar.Set(_v, ColorMath.FromHsv(_h, _s, 1));
        Ui.Select(_rgbTab, !_hsvMode);
        Ui.Select(_hsvTab, _hsvMode);

        if (_hsvMode)
        {
            _bars[0].Set("Farbton", _h / 360, Rainbow(), $"{_h / 360:0.000}");
            _bars[1].Set("Sättigung", _s, Gradient(ColorMath.FromHsv(_h, 0, _v), ColorMath.FromHsv(_h, 1, _v)), $"{_s:0.000}");
            _bars[2].Set("Helligkeit", _v, Gradient(Colors.Black, ColorMath.FromHsv(_h, _s, 1)), $"{_v:0.000}");
        }
        else
        {
            _bars[0].Set("Rot", c.R / 255.0, Gradient(Color.FromRgb(0, c.G, c.B), Color.FromRgb(255, c.G, c.B)), $"{c.R / 255.0:0.000}");
            _bars[1].Set("Grün", c.G / 255.0, Gradient(Color.FromRgb(c.R, 0, c.B), Color.FromRgb(c.R, 255, c.B)), $"{c.G / 255.0:0.000}");
            _bars[2].Set("Blau", c.B / 255.0, Gradient(Color.FromRgb(c.R, c.G, 0), Color.FromRgb(c.R, c.G, 255)), $"{c.B / 255.0:0.000}");
        }
        _bars[3].Set("Alpha", _a, Gradient(Color.FromArgb(0, c.R, c.G, c.B), opaque), $"{_a:0.000}");

        _preview.Background = new SolidColorBrush(c);
        if (!_hexBox.IsKeyboardFocused) _hexBox.Text = ColorMath.ToHex(c);
        _updating = false;
    }

    private static Brush Gradient(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, 0);
        brush.Freeze();
        return brush;
    }

    private static Brush Rainbow()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= 6; i++) brush.GradientStops.Add(new GradientStop(ColorMath.FromHsv(i * 60, 1, 1), i / 6.0));
        brush.Freeze();
        return brush;
    }

    private void PickFromScreen()
    {
        var picker = new EyedropperWindow();
        picker.Closed += (_, _) =>
        {
            if (picker.Picked is not { } picked) return;
            var alpha = _a;
            Color = picked;
            _a = alpha;
            Changed();
            Commit();
        };
        picker.Show();
    }
}

/// <summary>Horizontal Blender-style slider: coloured track, label on the left, value on the right.</summary>
internal sealed class ChannelBar : FrameworkElement
{
    private string _label = "";
    private string _text = "";
    private double _value;
    private Brush _track = Brushes.Gray;

    public event Action<double>? Picked;
    public event Action? Released;

    public ChannelBar()
    {
        Height = 26;
        Cursor = Cursors.SizeWE;
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); Pick(e); };
        MouseMove += (_, e) => { if (IsMouseCaptured) Pick(e); };
        MouseLeftButtonUp += (_, _) => { if (IsMouseCaptured) { ReleaseMouseCapture(); Released?.Invoke(); } };
        MouseWheel += (_, e) => { Picked?.Invoke(Math.Clamp(_value + Math.Sign(e.Delta) * 0.01, 0, 1)); Released?.Invoke(); };
    }

    public void Set(string label, double value, Brush track, string text)
    {
        _label = label;
        _value = Math.Clamp(value, 0, 1);
        _track = track;
        _text = text;
        InvalidateVisual();
    }

    private void Pick(MouseEventArgs e) => Picked?.Invoke(Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1));

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(Checker, null, rect, 4, 4);
        dc.DrawRoundedRectangle(_track, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1), rect, 4, 4);
        var x = Math.Round(rect.Width * _value);
        dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(Math.Clamp(x - 2, 0, rect.Width - 4), 1, 4, rect.Height - 2));

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawShadowed(dc, _label, new Point(8, 0), rect.Height, dpi, left: true);
        DrawShadowed(dc, _text, new Point(rect.Width - 8, 0), rect.Height, dpi, left: false);
    }

    private static void DrawShadowed(DrawingContext dc, string text, Point at, double height, double dpi, bool left)
    {
        FormattedText Make(Brush brush) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), 11.5, brush, dpi);
        var shadow = Make(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)));
        var front = Make(Brushes.White);
        var x = left ? at.X : at.X - front.Width;
        var y = (height - front.Height) / 2;
        dc.DrawText(shadow, new Point(x + 1, y + 1));
        dc.DrawText(front, new Point(x, y));
    }

    internal static readonly Brush Checker = CreateChecker();

    private static Brush CreateChecker()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 12, 12))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(200, 200, 200)), null,
            new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 6, 6)), new RectangleGeometry(new Rect(6, 6, 6, 6)) } }));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 12, 12), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}

/// <summary>Colour wheel: hue around, saturation from the centre outwards, shown at the current brightness.</summary>
internal sealed class HueWheel : FrameworkElement
{
    private const int Size = 190;
    private WriteableBitmap? _bitmap;
    private double _bitmapValue = -1;
    private double _h, _s, _v = 1;

    public event Action<double, double>? Picked;
    public event Action? Released;

    public HueWheel()
    {
        Width = Height = Size;
        Cursor = Cursors.Cross;
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); Pick(e); };
        MouseMove += (_, e) => { if (IsMouseCaptured) Pick(e); };
        MouseLeftButtonUp += (_, _) => { if (IsMouseCaptured) { ReleaseMouseCapture(); Released?.Invoke(); } };
    }

    public void Set(double h, double s, double v)
    {
        (_h, _s, _v) = (h, s, v);
        InvalidateVisual();
    }

    private void Pick(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var dx = p.X - Size / 2.0;
        var dy = Size / 2.0 - p.Y;
        var hue = Math.Atan2(dy, dx) * 180 / Math.PI;
        Picked?.Invoke(hue < 0 ? hue + 360 : hue, Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / (Size / 2.0), 0, 1));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bitmap is null || Math.Abs(_bitmapValue - _v) > 0.01) Rebuild();
        dc.DrawImage(_bitmap, new Rect(0, 0, Size, Size));

        var r = Size / 2.0;
        var angle = _h * Math.PI / 180;
        var marker = new Point(r + Math.Cos(angle) * _s * r, r - Math.Sin(angle) * _s * r);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 3), marker, 6, 6);
        dc.DrawEllipse(new SolidColorBrush(ColorMath.FromHsv(_h, _s, _v)), new Pen(Brushes.White, 1.5), marker, 6, 6);
    }

    private void Rebuild()
    {
        _bitmapValue = _v;
        var pixels = new byte[Size * Size * 4];
        var r = Size / 2.0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var dx = x + 0.5 - r;
                var dy = r - (y + 0.5);
                var dist = Math.Sqrt(dx * dx + dy * dy) / r;
                if (dist > 1.01) continue;
                var hue = Math.Atan2(dy, dx) * 180 / Math.PI;
                var c = ColorMath.FromHsv(hue, Math.Min(dist, 1), _v);
                var alpha = Math.Clamp((1.01 - dist) * 100, 0, 1); // soft anti-aliased edge
                var i = (y * Size + x) * 4;
                pixels[i] = (byte)(c.B * alpha);
                pixels[i + 1] = (byte)(c.G * alpha);
                pixels[i + 2] = (byte)(c.R * alpha);
                pixels[i + 3] = (byte)(255 * alpha);
            }
        }
        _bitmap = new WriteableBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32, null);
        _bitmap.WritePixels(new Int32Rect(0, 0, Size, Size), pixels, Size * 4, 0);
        _bitmap.Freeze();
    }
}

/// <summary>Vertical brightness bar next to the wheel.</summary>
internal sealed class ValueBar : FrameworkElement
{
    private double _v = 1;
    private Color _top = Colors.White;

    public event Action<double>? Picked;
    public event Action? Released;

    public ValueBar()
    {
        Width = 24;
        Height = 190;
        Cursor = Cursors.SizeNS;
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); Pick(e); };
        MouseMove += (_, e) => { if (IsMouseCaptured) Pick(e); };
        MouseLeftButtonUp += (_, _) => { if (IsMouseCaptured) { ReleaseMouseCapture(); Released?.Invoke(); } };
    }

    public void Set(double v, Color fullBrightness)
    {
        _v = v;
        _top = fullBrightness;
        InvalidateVisual();
    }

    private void Pick(MouseEventArgs e) => Picked?.Invoke(Math.Clamp(1 - e.GetPosition(this).Y / Math.Max(1, ActualHeight), 0, 1));

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        var brush = new LinearGradientBrush(_top, Colors.Black, 90);
        dc.DrawRoundedRectangle(brush, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1), rect, 4, 4);
        var y = Math.Clamp((1 - _v) * rect.Height, 2, rect.Height - 2);
        dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(-2, y - 2, rect.Width + 4, 4));
    }
}

/// <summary>Full-screen, almost invisible window that reads the colour under the next click (Esc cancels).</summary>
internal sealed class EyedropperWindow : Window
{
    public Color? Picked { get; private set; }

    public EyedropperWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        MouseLeftButtonDown += (_, _) =>
        {
            GetCursorPos(out var point);
            Hide();
            var dc = GetDC(IntPtr.Zero);
            var pixel = GetPixel(dc, point.X, point.Y);
            ReleaseDC(IntPtr.Zero, dc);
            if (pixel != 0xFFFFFFFF)
                Picked = Color.FromRgb((byte)(pixel & 0xFF), (byte)((pixel >> 8) & 0xFF), (byte)((pixel >> 16) & 0xFF));
            Close();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => Activate();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);
}
