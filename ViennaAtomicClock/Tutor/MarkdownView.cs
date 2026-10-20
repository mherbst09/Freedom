using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace ViennaAtomicClock.Tutor;

/// <summary>Renders a Markdown answer: paragraphs, headings, lists, quotes and highlighted code blocks with a copy button.</summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata("", (d, _) => ((MarkdownView)d).Render()));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Called with the code when a copy button is pressed.</summary>
    public static Action<string>? CopyHandler { get; set; }

    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");

    private static readonly Dictionary<TokenKind, Brush> TokenBrushes = new()
    {
        [TokenKind.Plain] = Frozen(Color.FromRgb(220, 223, 228)),
        [TokenKind.Keyword] = Frozen(Color.FromRgb(120, 170, 255)),
        [TokenKind.Type] = Frozen(Color.FromRgb(86, 212, 188)),
        [TokenKind.String] = Frozen(Color.FromRgb(230, 160, 120)),
        [TokenKind.Comment] = Frozen(Color.FromRgb(120, 150, 110)),
        [TokenKind.Number] = Frozen(Color.FromRgb(190, 210, 150))
    };

    private void Render()
    {
        Children.Clear();
        foreach (var block in MarkdownParser.Parse(Markdown ?? ""))
            Children.Add(block.Kind switch
            {
                BlockKind.Code => CodeBlock(block),
                BlockKind.Heading => Text(block.Text, block.Level <= 1 ? 20 : block.Level == 2 ? 17 : 15, bold: true, top: 10),
                BlockKind.ListItem => ListItem(block),
                BlockKind.Quote => Quote(block),
                BlockKind.Rule => Rule(),
                _ => Text(block.Text, 14.5)
            });
    }

    private static TextBlock Text(string text, double size, bool bold = false, double top = 0)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, LineHeight = size * 1.5, Margin = new Thickness(0, top, 0, 8) };
        if (bold) block.FontWeight = FontWeights.SemiBold;
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        foreach (var span in MarkdownParser.Inline(text))
        {
            Inline inline = span.Kind switch
            {
                SpanKind.Bold => new Run(span.Text) { FontWeight = FontWeights.Bold },
                SpanKind.Italic => new Run(span.Text) { FontStyle = FontStyles.Italic },
                SpanKind.Code => InlineCode(span.Text),
                _ => new Run(span.Text)
            };
            block.Inlines.Add(inline);
        }
        return block;
    }

    private static Inline InlineCode(string code)
    {
        var run = new Run(code) { FontFamily = CodeFont, FontSize = 13 };
        run.SetResourceReference(TextElement.BackgroundProperty, "SurfaceAlt");
        run.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        return run;
    }

    private static UIElement ListItem(MarkdownBlock block)
    {
        var grid = new Grid { Margin = new Thickness(12 + block.Level * 18, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var marker = new TextBlock { Text = block.Marker, FontSize = 14.5 };
        marker.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        grid.Children.Add(marker);
        var text = Text(block.Text, 14.5);
        text.Margin = new Thickness(0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static UIElement Quote(MarkdownBlock block)
    {
        var border = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2), Margin = new Thickness(0, 0, 0, 8) };
        border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        var text = Text(block.Text, 14.5);
        text.Margin = new Thickness(0);
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        border.Child = text;
        return border;
    }

    private static UIElement Rule()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 12) };
        line.SetResourceReference(Border.BackgroundProperty, "CardBorder");
        return line;
    }

    private static UIElement CodeBlock(MarkdownBlock block)
    {
        var language = block.Language.Length > 0 ? block.Language : "code";
        var header = new Grid { Margin = new Thickness(12, 6, 6, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = language, FontFamily = CodeFont, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        header.Children.Add(label);
        var copy = new Button { Content = "⧉ Kopieren", Padding = new Thickness(10, 2, 10, 2), Height = 26, FontSize = 11.5, Margin = new Thickness(0) };
        copy.SetResourceReference(StyleProperty, "ChipButton");
        copy.Click += (_, _) =>
        {
            CopyHandler?.Invoke(block.Text);
            copy.Content = "✓ Kopiert";
        };
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        var code = new TextBlock { FontFamily = CodeFont, FontSize = 13, LineHeight = 20, Margin = new Thickness(14, 6, 14, 12) };
        foreach (var (kind, text) in CodeHighlighter.Tokenize(block.Text, language))
            code.Inlines.Add(new Run(text) { Foreground = TokenBrushes[kind] });

        var body = new ScrollViewer
        {
            Content = code, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        // Let the mouse wheel scroll the chat, not the code block.
        body.PreviewMouseWheel += (s, e) =>
        {
            e.Handled = true;
            ((UIElement)((FrameworkElement)s).Parent).RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
        };

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(body);
        var border = new Border
        {
            Child = panel, Margin = new Thickness(0, 2, 0, 12), BorderThickness = new Thickness(1),
            Background = Frozen(Color.FromRgb(13, 15, 20))
        };
        border.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        border.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        return border;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value switch { bool b => b, string s => s.Length > 0, int i => i > 0, null => false, _ => true };
        return flag ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
