using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ViennaAtomicClock.Treeman;

internal enum GameIconKind { Coin, Trophy, Level, Shirt, Box, Clock, Axe, Tree, Pine, Pumpkin, Egg, Sun, Sunset, Moon }

internal enum GameButtonColor { Green, Orange, Blue, Red, Wood }

/// <summary>
/// Treeman's own look, independent of the Freedom theme: wooden boards with nails, a ribbon banner, chunky
/// 3D buttons, outlined lettering and small drawn icons. Everything is vector drawing with frozen resources.
/// </summary>
internal static class GameUi
{
    public static readonly FontFamily Font = new("Segoe UI Black, Microsoft YaHei UI, Yu Gothic UI, Segoe UI");
    public static readonly Typeface Face = new(Font, FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
    public static readonly FontFamily TextFont = new("Segoe UI Semibold, Microsoft YaHei UI, Yu Gothic UI, Segoe UI");

    public static readonly Color InkColor = Color.FromRgb(0x3A, 0x26, 0x18);
    public static readonly Brush Ink = Solid(InkColor);
    public static readonly Brush Cream = Solid(Color.FromRgb(0xFF, 0xF4, 0xDC));
    public static readonly Brush CreamDim = Solid(Color.FromRgb(0xF0, 0xD6, 0xAE));
    public static readonly Brush Gold = Solid(Color.FromRgb(0xFF, 0xD2, 0x4A));
    public static readonly Brush InsetBrush = Solid(Color.FromArgb(150, 0x2A, 0x18, 0x0C));
    public static readonly Brush TitleFill = Freeze(new LinearGradientBrush(Color.FromRgb(0xFF, 0xE8, 0x6A), Color.FromRgb(0xF2, 0x8C, 0x22), 90));

    public static TextBlock Label(string text, double size, Brush? brush = null, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? Cream, FontFamily = bold ? Font : TextFont,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };

    public static GameButton Button(string text, GameButtonColor color, Action click, Action<SoundEffect> sound, GameIconKind? icon = null, double height = 54)
    {
        var button = new GameButton(text, color, icon) { Height = height, Margin = new Thickness(0, 0, 0, 10) };
        button.Click += (_, _) =>
        {
            sound(SoundEffect.MenuSelect);
            click();
        };
        return button;
    }

    /// <summary>Two buttons side by side.</summary>
    public static Grid Row(FrameworkElement left, FrameworkElement right)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 2);
        row.Children.Add(left);
        row.Children.Add(right);
        return row;
    }

    /// <summary>A wooden board with an optional ribbon title sticking out at the top.</summary>
    public static FrameworkElement Board(string? title, UIElement content, double width)
    {
        var stack = new StackPanel();
        if (title is not null) stack.Children.Add(new Ribbon(title) { Margin = new Thickness(-40, -62, -40, 6), HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(content);
        return new WoodBoard { Child = stack, Width = width, Margin = new Thickness(0, title is null ? 0 : 40, 0, 0) };
    }

    /// <summary>Darker recessed area on the board.</summary>
    public static Border Inset(UIElement child, Thickness? padding = null) => new()
    {
        Background = InsetBrush, CornerRadius = new CornerRadius(12), Padding = padding ?? new Thickness(12, 8, 12, 8), Child = child,
        BorderBrush = Solid(Color.FromArgb(90, 0, 0, 0)), BorderThickness = new Thickness(0, 2, 0, 0)
    };

    /// <summary>Icon, big value and small caption in an inset (main menu and round summary).</summary>
    public static Border Chip(GameIconKind icon, string value, string caption)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new GameIcon(icon) { Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new GameText(value, 20) { HorizontalAlignment = HorizontalAlignment.Left });
        var label = Label(caption, 11.5, CreamDim);
        label.HorizontalAlignment = HorizontalAlignment.Left;
        label.TextAlignment = TextAlignment.Left;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.TextWrapping = TextWrapping.NoWrap;
        label.MaxWidth = 120;
        texts.Children.Add(label);
        panel.Children.Add(texts);
        var chip = Inset(panel, new Thickness(10, 6, 12, 6));
        chip.Margin = new Thickness(4);
        return chip;
    }

    /// <summary>HUD badge: icon plus a value on a dark rounded plate.</summary>
    public static Border Badge(UIElement icon, TextBlock text)
    {
        text.FontFamily = Font;
        text.FontSize = 17;
        text.Foreground = Cream;
        text.VerticalAlignment = VerticalAlignment.Center;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 8, 0);
        panel.Children.Add(icon);
        panel.Children.Add(text);
        return new Border
        {
            Background = Solid(Color.FromArgb(205, 0x3A, 0x26, 0x18)), CornerRadius = new CornerRadius(17), BorderBrush = Solid(Color.FromArgb(110, 0xFF, 0xF4, 0xDC)),
            BorderThickness = new Thickness(2), Padding = new Thickness(6, 4, 14, 4), Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left, Child = panel
        };
    }

    public static Brush Solid(Color color) => Freeze(new SolidColorBrush(color));

    public static Pen Pen(Color color, double thickness) =>
        Freeze(new Pen(Solid(color), thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    public static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Outlined text geometry (used for buttons, ribbons and the big numbers).</summary>
    public static Geometry TextGeometry(string text, double size, out Size extent)
    {
        var formatted = new FormattedText(text, Loc.Culture, FlowDirection.LeftToRight, Face, size, Brushes.Black, 1.0);
        extent = new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
        var geometry = formatted.BuildGeometry(new Point(0, 0));
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>Chunky outlined lettering with a soft drop shadow.</summary>
internal sealed class GameText : FrameworkElement
{
    private static readonly Brush ShadowBrush = GameUi.Solid(Color.FromArgb(110, 0x1A, 0x0E, 0x06));
    private string _text = "";
    private Geometry? _geometry;
    private Size _extent;
    private readonly double _size;
    private readonly Pen _stroke;
    private readonly TranslateTransform _offset = new();
    private readonly TranslateTransform _shadowOffset = new();
    private readonly Pen _shadowPen;

    public GameText(string text, double size, Brush? fill = null, double? stroke = null)
    {
        _size = size;
        Fill = fill ?? GameUi.Cream;
        _stroke = GameUi.Pen(GameUi.InkColor, stroke ?? Math.Max(3, size * 0.11));
        _shadowPen = GameUi.Freeze(new Pen(ShadowBrush, _stroke.Thickness) { LineJoin = PenLineJoin.Round });
        IsHitTestVisible = false;
        Text = text;
    }

    public Brush Fill { get; set; }

    public string Text
    {
        get => _text;
        set
        {
            if (value == _text && _geometry is not null) return;
            _text = value;
            _geometry = GameUi.TextGeometry(value, _size, out _extent);
            AutomationProperties.SetName(this, value);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private double Pad => _stroke.Thickness;

    protected override Size MeasureOverride(Size availableSize) => new(_extent.Width + Pad * 2, _extent.Height + Pad * 2 + _size * 0.06);

    protected override void OnRender(DrawingContext dc)
    {
        if (_geometry is null) return;
        var x = (ActualWidth - _extent.Width) / 2;
        _shadowOffset.X = x;
        _shadowOffset.Y = Pad + _size * 0.07;
        dc.PushTransform(_shadowOffset);
        dc.DrawGeometry(ShadowBrush, _shadowPen, _geometry);
        dc.Pop();
        _offset.X = x;
        _offset.Y = Pad;
        dc.PushTransform(_offset);
        dc.DrawGeometry(null, _stroke, _geometry);
        dc.DrawGeometry(Fill, null, _geometry);
        dc.Pop();
    }
}

/// <summary>Small vector icons in a 24×24 design box.</summary>
internal static class IconPainter
{
    private static readonly Pen Outline = GameUi.Pen(GameUi.InkColor, 1.8);
    private static readonly Pen Thin = GameUi.Pen(GameUi.InkColor, 1.2);
    private static readonly Brush CoinFace = GameUi.Solid(Color.FromRgb(0xFF, 0xCC, 0x3A));
    private static readonly Pen CoinRing = GameUi.Pen(Color.FromRgb(0xC8, 0x8A, 0x1A), 1.4);
    private static readonly Brush Shine = GameUi.Solid(Color.FromArgb(190, 255, 255, 255));
    private static readonly Brush TrophyGold = GameUi.Solid(Color.FromRgb(0xFF, 0xC8, 0x2E));
    private static readonly Brush LeafGreen = GameUi.Solid(Color.FromRgb(0x5C, 0xC2, 0x4A));
    private static readonly Brush ShirtRed = GameUi.Solid(Color.FromRgb(0xD8, 0x40, 0x3A));
    private static readonly Brush ShirtDark = GameUi.Solid(Color.FromRgb(0x96, 0x24, 0x24));
    private static readonly Brush Crate = GameUi.Solid(Color.FromRgb(0xC0, 0x80, 0x44));
    private static readonly Brush CrateDark = GameUi.Solid(Color.FromRgb(0x8C, 0x56, 0x2A));
    private static readonly Brush ClockFace = GameUi.Solid(Color.FromRgb(0xFF, 0xF4, 0xDC));
    private static readonly Brush Steel = GameUi.Solid(Color.FromRgb(0xD4, 0xDC, 0xE6));
    private static readonly Brush Handle = GameUi.Solid(Color.FromRgb(0x9A, 0x66, 0x36));

    private static readonly Geometry Cup = G("M 6,3 L 18,3 L 18,9 Q 18,15 12,15 Q 6,15 6,9 Z");
    private static readonly Geometry Handles = G("M 6,5 Q 2,5 2.5,8 Q 3,11 6.5,11.5 M 18,5 Q 22,5 21.5,8 Q 21,11 17.5,11.5");
    private static readonly Geometry Leaf = G("M 12,2 Q 22,8 18,18 Q 12,23 6,18 Q 2,8 12,2 Z");
    private static readonly Geometry Vein = G("M 12,5 L 12,20 M 12,11 L 16,8 M 12,15 L 8,12");
    private static readonly Geometry Shirt = G("M 8,3 L 3.5,6 L 5,11 L 8,10 L 8,21 L 16,21 L 16,10 L 19,11 L 20.5,6 L 16,3 Q 12,6 8,3 Z");
    private static readonly Geometry Blade = G("M 9,3 Q 20,2 21,10 Q 16,9 12,11 Z");
    private static readonly Geometry PineShape = G("M 12,1.5 L 17,8 L 14.5,8 L 19,14 L 16,14 L 21,20 L 3,20 L 8,14 L 5,14 L 9.5,8 L 7,8 Z");
    private static readonly Geometry PumpkinFace = G("M 7.5,11 L 9.5,8.5 L 11,11 Z M 13,11 L 14.5,8.5 L 16.5,11 Z M 7,14 L 9,16.5 L 12,15 L 15,16.5 L 17,14 Q 12,18.5 7,14 Z");
    private static readonly Geometry MoonShape = G("M 15,2.5 A 9.5,9.5 0 1 0 21.5,16 A 7.5,7.5 0 1 1 15,2.5 Z");
    private static readonly Brush TreeGreen = GameUi.Solid(Color.FromRgb(0x46, 0xA8, 0x46));
    private static readonly Brush PineGreen = GameUi.Solid(Color.FromRgb(0x2E, 0x8A, 0x4C));
    private static readonly Brush Pumpkin = GameUi.Solid(Color.FromRgb(0xF0, 0x84, 0x2A));
    private static readonly Brush Glow = GameUi.Solid(Color.FromRgb(0xFF, 0xE3, 0x6A));
    private static readonly Brush EggPink = GameUi.Solid(Color.FromRgb(0xFF, 0x9C, 0xC6));
    private static readonly Brush EggBand = GameUi.Solid(Color.FromRgb(0x7F, 0xD3, 0xFF));
    private static readonly Brush SunYellow = GameUi.Solid(Color.FromRgb(0xFF, 0xD2, 0x3A));
    private static readonly Brush SunsetOrange = GameUi.Solid(Color.FromRgb(0xFF, 0x8A, 0x3C));
    private static readonly Brush MoonPale = GameUi.Solid(Color.FromRgb(0xF4, 0xEE, 0xC8));
    private static readonly Pen Ray = GameUi.Pen(Color.FromRgb(0xFF, 0xC2, 0x2A), 2);

    public static void Draw(DrawingContext dc, GameIconKind kind, Rect box)
    {
        var s = Math.Min(box.Width, box.Height) / 24;
        dc.PushTransform(new MatrixTransform(s, 0, 0, s, box.X + (box.Width - 24 * s) / 2, box.Y + (box.Height - 24 * s) / 2));
        switch (kind)
        {
            case GameIconKind.Coin:
                dc.DrawEllipse(CoinFace, Outline, new Point(12, 12), 10.5, 10.5);
                dc.DrawEllipse(null, CoinRing, new Point(12, 12), 7, 7);
                dc.DrawRectangle(CoinRing.Brush, null, new Rect(10.8, 8, 2.4, 8));
                dc.DrawEllipse(Shine, null, new Point(8, 7.5), 2.2, 1.4);
                break;
            case GameIconKind.Trophy:
                dc.DrawGeometry(null, Outline, Handles);
                dc.DrawGeometry(TrophyGold, Outline, Cup);
                dc.DrawRectangle(TrophyGold, Thin, new Rect(10.5, 15, 3, 4));
                dc.DrawRoundedRectangle(TrophyGold, Outline, new Rect(6.5, 19, 11, 3.5), 1, 1);
                dc.DrawEllipse(Shine, null, new Point(9.5, 6.5), 1.5, 2.2);
                break;
            case GameIconKind.Level:
                dc.DrawGeometry(LeafGreen, Outline, Leaf);
                dc.DrawGeometry(null, Thin, Vein);
                break;
            case GameIconKind.Shirt:
                dc.DrawGeometry(ShirtRed, Outline, Shirt);
                dc.DrawRectangle(ShirtDark, null, new Rect(8.5, 13, 7, 1.8));
                dc.DrawRectangle(ShirtDark, null, new Rect(11.2, 6, 1.6, 14.5));
                break;
            case GameIconKind.Box:
                dc.DrawRectangle(Crate, Outline, new Rect(3, 8, 18, 13));
                dc.DrawRectangle(CrateDark, Outline, new Rect(2, 4.5, 20, 5));
                dc.DrawRectangle(TrophyGold, Thin, new Rect(10.5, 4.5, 3, 16.5));
                dc.DrawLine(Thin, new Point(4, 14.5), new Point(20, 14.5));
                break;
            case GameIconKind.Clock:
                dc.DrawEllipse(ClockFace, Outline, new Point(12, 12), 10, 10);
                dc.DrawLine(Outline, new Point(12, 12), new Point(12, 6.5));
                dc.DrawLine(Outline, new Point(12, 12), new Point(16, 14));
                dc.DrawEllipse(GameUi.Ink, null, new Point(12, 12), 1.5, 1.5);
                break;
            case GameIconKind.Tree:
                dc.DrawRectangle(Handle, Thin, new Rect(10.5, 14, 3, 8));
                dc.DrawEllipse(TreeGreen, Outline, new Point(12, 10), 8.5, 8);
                dc.DrawEllipse(Shine, null, new Point(9, 7), 2, 1.4);
                break;
            case GameIconKind.Pine:
                dc.DrawRectangle(Handle, Thin, new Rect(10.5, 19, 3, 4));
                dc.DrawGeometry(PineGreen, Outline, PineShape);
                dc.DrawEllipse(ShirtRed, null, new Point(10, 12), 1.4, 1.4);
                dc.DrawEllipse(SunYellow, null, new Point(14, 16.5), 1.4, 1.4);
                dc.DrawEllipse(SunYellow, null, new Point(12, 2.5), 1.8, 1.8);
                break;
            case GameIconKind.Pumpkin:
                dc.DrawRectangle(LeafGreen, Thin, new Rect(11, 2, 2.5, 4));
                dc.DrawEllipse(Pumpkin, Outline, new Point(12, 13), 10, 8.5);
                dc.DrawGeometry(Glow, null, PumpkinFace);
                break;
            case GameIconKind.Egg:
                dc.DrawEllipse(EggPink, Outline, new Point(12, 13), 7.5, 9.5);
                dc.DrawRectangle(EggBand, null, new Rect(5, 11.5, 14, 3));
                dc.DrawEllipse(Shine, null, new Point(9.5, 8), 1.6, 2.2);
                break;
            case GameIconKind.Sun:
                for (var i = 0; i < 8; i++)
                {
                    var a = i * Math.PI / 4;
                    dc.DrawLine(Ray, new Point(12 + Math.Cos(a) * 8, 12 + Math.Sin(a) * 8), new Point(12 + Math.Cos(a) * 11, 12 + Math.Sin(a) * 11));
                }
                dc.DrawEllipse(SunYellow, Outline, new Point(12, 12), 6, 6);
                break;
            case GameIconKind.Sunset:
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, 24, 15)));
                dc.DrawEllipse(SunsetOrange, Outline, new Point(12, 15), 8, 8);
                dc.Pop();
                dc.DrawLine(Outline, new Point(2, 15), new Point(22, 15));
                dc.DrawLine(Ray, new Point(6, 19), new Point(18, 19));
                break;
            case GameIconKind.Moon:
                dc.DrawGeometry(MoonPale, Outline, MoonShape);
                dc.DrawEllipse(SunYellow, null, new Point(19, 5), 1.2, 1.2);
                break;
            case GameIconKind.Axe:
                dc.DrawLine(GameUi.Pen(GameUi.InkColor, 4.4), new Point(6, 22), new Point(14, 4));
                dc.DrawLine(GameUi.Pen(Color.FromRgb(0x9A, 0x66, 0x36), 2.4), new Point(6, 22), new Point(14, 4));
                dc.DrawGeometry(Steel, Outline, Blade);
                break;
        }
        dc.Pop();
    }

    private static Geometry G(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}

internal sealed class GameIcon : FrameworkElement
{
    private GameIconKind _kind;

    public GameIcon(GameIconKind kind)
    {
        _kind = kind;
        Width = Height = 22;
        IsHitTestVisible = false;
    }

    public GameIconKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc) => IconPainter.Draw(dc, _kind, new Rect(0, 0, ActualWidth, ActualHeight));
}

/// <summary>Arcade button: coloured face on a darker lip, gloss, outlined caption. Presses down when clicked.</summary>
internal sealed class GameButton : Button
{
    private static readonly ControlTemplate PresenterOnly = MakeTemplate();
    private readonly ButtonFace _face;

    public GameButton(string text, GameButtonColor color, GameIconKind? icon)
    {
        Template = PresenterOnly;
        FocusVisualStyle = null;
        Cursor = System.Windows.Input.Cursors.Hand;
        Background = Brushes.Transparent;
        Content = _face = new ButtonFace(this, color, icon);
        Text = text;
    }

    public string Text
    {
        get => _face.Text;
        set
        {
            _face.Text = value;
            AutomationProperties.SetName(this, value);
        }
    }

    public GameButtonColor Color
    {
        get => _face.Color;
        set => _face.Color = value;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsPressedProperty || e.Property == IsMouseOverProperty || e.Property == IsEnabledProperty || e.Property == IsKeyboardFocusedProperty)
            _face.InvalidateVisual();
    }

    private static ControlTemplate MakeTemplate()
    {
        var template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        template.Seal();
        return template;
    }

    private sealed class ButtonFace : FrameworkElement
    {
        private const double Lip = 6;
        private static readonly Pen Outline = GameUi.Pen(GameUi.InkColor, 3);
        private static readonly Pen FocusRing = GameUi.Pen(System.Windows.Media.Color.FromRgb(0xFF, 0xE0, 0x5A), 3);
        private static readonly Pen TextStroke = GameUi.Pen(GameUi.InkColor, 4.5);
        private static readonly Brush Gloss = GameUi.Solid(System.Windows.Media.Color.FromArgb(55, 255, 255, 255));
        private static readonly Brush DisabledFace = GameUi.Solid(System.Windows.Media.Color.FromRgb(0x9A, 0x8C, 0x7C));
        private static readonly Brush DisabledLip = GameUi.Solid(System.Windows.Media.Color.FromRgb(0x6A, 0x5E, 0x52));
        private static readonly Dictionary<GameButtonColor, (Brush Face, Brush Lip, Brush Hover)> Palette = new()
        {
            [GameButtonColor.Green] = (GameUi.Solid(Rgb(0x5B, 0xC2, 0x4A)), GameUi.Solid(Rgb(0x3A, 0x86, 0x2E)), GameUi.Solid(Rgb(0x71, 0xD6, 0x5E))),
            [GameButtonColor.Orange] = (GameUi.Solid(Rgb(0xF2, 0xA1, 0x3A)), GameUi.Solid(Rgb(0xB0, 0x68, 0x1A)), GameUi.Solid(Rgb(0xFF, 0xB5, 0x52))),
            [GameButtonColor.Blue] = (GameUi.Solid(Rgb(0x4A, 0x90, 0xE2)), GameUi.Solid(Rgb(0x2B, 0x5F, 0xA6)), GameUi.Solid(Rgb(0x63, 0xA6, 0xF2))),
            [GameButtonColor.Red] = (GameUi.Solid(Rgb(0xE0, 0x54, 0x4A)), GameUi.Solid(Rgb(0x9E, 0x30, 0x2A)), GameUi.Solid(Rgb(0xF0, 0x6C, 0x62))),
            [GameButtonColor.Wood] = (GameUi.Solid(Rgb(0xC0, 0x84, 0x4C)), GameUi.Solid(Rgb(0x7E, 0x4F, 0x27)), GameUi.Solid(Rgb(0xD2, 0x96, 0x5C)))
        };

        private readonly GameButton _owner;
        private readonly GameIconKind? _icon;
        private readonly TranslateTransform _textOffset = new();
        private string _text = "";
        private Geometry? _textGeometry;
        private Size _textExtent;
        private GameButtonColor _color;

        public ButtonFace(GameButton owner, GameButtonColor color, GameIconKind? icon)
        {
            _owner = owner;
            _color = color;
            _icon = icon;
        }

        public GameButtonColor Color
        {
            get => _color;
            set
            {
                _color = value;
                InvalidateVisual();
            }
        }

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                _textGeometry = GameUi.TextGeometry(value.ToUpper(Loc.Culture), FontSize, out _textExtent);
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        private double FontSize => _owner.Height is > 0 and var h && !double.IsNaN(h) ? Math.Clamp(h * 0.32, 13, 24) : 16;

        protected override Size MeasureOverride(Size availableSize) => new(_textExtent.Width + (_icon is null ? 0 : 32) + 40, double.IsNaN(_owner.Height) ? 54 : _owner.Height);

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth;
            var h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var enabled = _owner.IsEnabled;
            var (face, lip, hover) = Palette[_color];
            var pressed = _owner.IsPressed && enabled;
            var down = pressed ? Lip - 2 : 0;
            var radius = Math.Min(16, (h - Lip) / 2);
            dc.DrawRoundedRectangle(enabled ? lip : DisabledLip, Outline, new Rect(1.5, Lip + 1.5, w - 3, h - Lip - 3), radius, radius);
            var faceRect = new Rect(1.5, 1.5 + down, w - 3, h - Lip - 3);
            dc.DrawRoundedRectangle(!enabled ? DisabledFace : _owner.IsMouseOver ? hover : face, Outline, faceRect, radius, radius);
            dc.DrawRoundedRectangle(Gloss, null, new Rect(faceRect.X + 6, faceRect.Y + 4, faceRect.Width - 12, faceRect.Height * 0.42), radius * 0.7, radius * 0.7);
            if (_owner.IsKeyboardFocused) dc.DrawRoundedRectangle(null, FocusRing, new Rect(-2, -2 + down, w + 4, h - Lip + 4), radius + 3, radius + 3);

            var iconSize = _icon is null ? 0 : Math.Min(28, faceRect.Height - 14);
            var contentWidth = _textExtent.Width + (_icon is null ? 0 : iconSize + 8);
            var x = (w - contentWidth) / 2;
            var centerY = faceRect.Y + faceRect.Height / 2;
            if (_icon is { } icon)
            {
                IconPainter.Draw(dc, icon, new Rect(x, centerY - iconSize / 2, iconSize, iconSize));
                x += iconSize + 8;
            }
            if (_textGeometry is null) return;
            _textOffset.X = x;
            _textOffset.Y = centerY - _textExtent.Height / 2;
            dc.PushTransform(_textOffset);
            if (!enabled) dc.PushOpacity(0.75);
            dc.DrawGeometry(null, TextStroke, _textGeometry);
            dc.DrawGeometry(GameUi.Cream, null, _textGeometry);
            if (!enabled) dc.Pop();
            dc.Pop();
        }

        private static Color Rgb(byte r, byte g, byte b) => System.Windows.Media.Color.FromRgb(r, g, b);
    }
}

/// <summary>Wooden board with planks, grain, a light bevel and four nails. Content gets a fixed padding.</summary>
internal sealed class WoodBoard : Decorator
{
    private const double Padding = 28;
    private static readonly Brush Wood = GameUi.Freeze(new LinearGradientBrush(Color.FromRgb(0xB9, 0x80, 0x49), Color.FromRgb(0x8E, 0x5A, 0x2F), 90));
    private static readonly Brush Shadow = GameUi.Solid(Color.FromArgb(90, 0, 0, 0));
    private static readonly Pen Outline = GameUi.Pen(GameUi.InkColor, 4);
    private static readonly Pen Groove = GameUi.Pen(Color.FromArgb(170, 0x6A, 0x40, 0x20), 3);
    private static readonly Pen GrooveLight = GameUi.Pen(Color.FromArgb(70, 0xF2, 0xC2, 0x86), 1.5);
    private static readonly Pen Grain = GameUi.Pen(Color.FromArgb(55, 0x5A, 0x34, 0x18), 1.6);
    private static readonly Pen Bevel = GameUi.Pen(Color.FromArgb(110, 0xF2, 0xC2, 0x86), 2);
    private static readonly Brush Nail = GameUi.Solid(Color.FromRgb(0x9A, 0x9A, 0xA4));
    private static readonly Pen NailPen = GameUi.Pen(GameUi.InkColor, 1.5);
    private static readonly Brush NailShine = GameUi.Solid(Color.FromArgb(170, 255, 255, 255));

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return new Size(Padding * 2, Padding * 2);
        Child.Measure(new Size(Math.Max(0, constraint.Width - Padding * 2), Math.Max(0, constraint.Height - Padding * 2)));
        return new Size(Child.DesiredSize.Width + Padding * 2, Child.DesiredSize.Height + Padding * 2);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(Padding, Padding, Math.Max(0, arrangeSize.Width - Padding * 2), Math.Max(0, arrangeSize.Height - Padding * 2)));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRoundedRectangle(Shadow, null, new Rect(4, 10, w - 4, h - 4), 22, 22);
        var board = new Rect(2, 2, w - 4, h - 4);
        dc.DrawRoundedRectangle(Wood, Outline, board, 22, 22);
        // Planks and wood grain (deterministic, so the board looks the same every time).
        var plank = 0;
        for (var y = 2.0; y < h - 10; y += 62, plank++)
        {
            if (y > 2)
            {
                dc.DrawLine(Groove, new Point(10, y), new Point(w - 10, y));
                dc.DrawLine(GrooveLight, new Point(10, y + 2.5), new Point(w - 10, y + 2.5));
            }
            for (var g = 0; g < 2; g++)
            {
                var gy = y + 18 + g * 22;
                if (gy > h - 16) break;
                var start = 20 + (plank * 97 + g * 53) % Math.Max(1, (int)(w * 0.4));
                var length = Math.Min(w - 40 - start, 110 + (plank * 31 + g * 17) % 140);
                if (length < 30) continue;
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(new Point(start, gy), false, false);
                    ctx.QuadraticBezierTo(new Point(start + length / 2, gy + (g == 0 ? 4 : -4)), new Point(start + length, gy), true, true);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, Grain, geometry);
            }
        }
        dc.DrawRoundedRectangle(null, Bevel, new Rect(9, 9, w - 18, h - 18), 16, 16);
        foreach (var (nx, ny) in new[] { (18.0, 18.0), (w - 18, 18.0), (18.0, h - 18), (w - 18, h - 18) })
        {
            dc.DrawEllipse(Nail, NailPen, new Point(nx, ny), 5, 5);
            dc.DrawEllipse(NailShine, null, new Point(nx - 1.5, ny - 1.5), 1.8, 1.8);
        }
    }
}

/// <summary>Red banner with folded tails for panel titles.</summary>
internal sealed class Ribbon : FrameworkElement
{
    private static readonly Brush Band = GameUi.Freeze(new LinearGradientBrush(Color.FromRgb(0xE8, 0x55, 0x45), Color.FromRgb(0xC2, 0x3A, 0x30), 90));
    private static readonly Brush Tail = GameUi.Solid(Color.FromRgb(0xA3, 0x30, 0x28));
    private static readonly Brush Fold = GameUi.Solid(Color.FromRgb(0x6E, 0x1E, 0x1A));
    private static readonly Brush Gloss = GameUi.Solid(Color.FromArgb(45, 255, 255, 255));
    private static readonly Pen Outline = GameUi.Pen(GameUi.InkColor, 3);
    private static readonly Pen TextStroke = GameUi.Pen(GameUi.InkColor, 6);
    private readonly Geometry _text;
    private readonly Size _extent;
    private readonly TranslateTransform _offset = new();

    public Ribbon(string text)
    {
        _text = GameUi.TextGeometry(text.ToUpper(Loc.Culture), 28, out _extent);
        Height = 66;
        IsHitTestVisible = false;
        AutomationProperties.SetName(this, text);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Max(300, _extent.Width + 130), 66);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        dc.DrawGeometry(Tail, Outline, Geometry.Parse(FormattableString.Invariant($"M 0,20 L 46,20 L 46,62 L 0,62 L 14,41 Z")));
        dc.DrawGeometry(Tail, Outline, Geometry.Parse(FormattableString.Invariant($"M {w},20 L {w - 46},20 L {w - 46},62 L {w},62 L {w - 14},41 Z")));
        dc.DrawGeometry(Fold, Outline, Geometry.Parse(FormattableString.Invariant($"M 30,52 L 46,62 L 46,52 Z M {w - 30},52 L {w - 46},62 L {w - 46},52 Z")));
        var band = new Rect(30, 4, w - 60, 48);
        dc.DrawRoundedRectangle(Band, Outline, band, 6, 6);
        dc.DrawRectangle(Gloss, null, new Rect(band.X + 4, band.Y + 4, band.Width - 8, 14));
        _offset.X = (w - _extent.Width) / 2;
        _offset.Y = band.Y + (band.Height - _extent.Height) / 2;
        dc.PushTransform(_offset);
        dc.DrawGeometry(null, TextStroke, _text);
        dc.DrawGeometry(GameUi.Cream, null, _text);
        dc.Pop();
    }
}

/// <summary>Time bar framed like a log, with a clock at the start and tick marks.</summary>
internal sealed class TimeBar : FrameworkElement
{
    private static readonly Brush Good = GameUi.Freeze(new LinearGradientBrush(Color.FromRgb(0x9A, 0xE8, 0x62), Color.FromRgb(0x46, 0xA8, 0x38), 90));
    private static readonly Brush Warn = GameUi.Freeze(new LinearGradientBrush(Color.FromRgb(0xFF, 0xDC, 0x62), Color.FromRgb(0xE0, 0x9A, 0x1E), 90));
    private static readonly Brush Low = GameUi.Freeze(new LinearGradientBrush(Color.FromRgb(0xFF, 0x86, 0x6A), Color.FromRgb(0xD0, 0x36, 0x2C), 90));
    private static readonly Brush Track = GameUi.Solid(Color.FromRgb(0x24, 0x17, 0x0E));
    private static readonly Brush Gloss = GameUi.Solid(Color.FromArgb(70, 255, 255, 255));
    private static readonly Pen Outline = GameUi.Pen(GameUi.InkColor, 4);
    private static readonly Pen Tick = GameUi.Pen(Color.FromArgb(70, 0, 0, 0), 2);
    private static readonly Pen Glow = GameUi.Pen(Color.FromArgb(190, 0xFF, 0x50, 0x40), 10);

    public double Share { get; set; } = 1;
    public double WarnShare { get; set; } = 0.25;
    public double Pulse { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var trackRect = new Rect(h * 0.55, 3, w - h * 0.55 - 2, h - 6);
        var r = trackRect.Height / 2;
        if (Pulse > 0)
        {
            dc.PushOpacity(Pulse);
            dc.DrawRoundedRectangle(null, Glow, trackRect, r, r);
            dc.Pop();
        }
        dc.DrawRoundedRectangle(Track, Outline, trackRect, r, r);
        var inner = new Rect(trackRect.X + 4, trackRect.Y + 4, Math.Max(0, (trackRect.Width - 8) * Math.Clamp(Share, 0, 1)), trackRect.Height - 8);
        if (inner.Width > 1)
        {
            var fill = Share <= WarnShare ? Low : Share <= 0.5 ? Warn : Good;
            var ir = Math.Min(inner.Height / 2, inner.Width / 2);
            dc.DrawRoundedRectangle(fill, null, inner, ir, ir);
            dc.DrawRoundedRectangle(Gloss, null, new Rect(inner.X + 3, inner.Y + 2, Math.Max(0, inner.Width - 6), inner.Height * 0.38), ir * 0.6, ir * 0.6);
        }
        for (var i = 1; i < 10; i++)
        {
            var x = trackRect.X + 4 + (trackRect.Width - 8) * i / 10;
            dc.DrawLine(Tick, new Point(x, trackRect.Y + 6), new Point(x, trackRect.Bottom - 6));
        }
        IconPainter.Draw(dc, GameIconKind.Clock, new Rect(0, -4, h + 8, h + 8));
    }
}
