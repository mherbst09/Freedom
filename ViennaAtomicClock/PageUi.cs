using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ViennaAtomicClock;

/// <summary>Small helpers that build theme-aware controls in code (all colours and fonts follow the active theme).</summary>
internal static class Ui
{
    public static TextBlock Text(string text, double size = 13, string brush = "TextPrimary", bool bold = false, bool title = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
        if (bold) block.FontWeight = FontWeights.Bold;
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (title) block.SetResourceReference(TextBlock.FontFamilyProperty, "StencilFont");
        return block;
    }

    public static Border Card(UIElement child, double padding = 20)
    {
        var card = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(padding), Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        card.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        return card;
    }

    public static Button Button(string style, object content, RoutedEventHandler click, string? tooltip = null)
    {
        var button = new Button { Content = content, ToolTip = tooltip };
        button.SetResourceReference(FrameworkElement.StyleProperty, style);
        button.Click += click;
        return button;
    }

    /// <summary>Chip that shows whether it is selected.</summary>
    public static void Select(Button chip, bool selected)
    {
        chip.SetResourceReference(Control.BackgroundProperty, selected ? "AccentSoft" : "SurfaceAlt");
        chip.SetResourceReference(Control.ForegroundProperty, selected ? "AccentBrush" : "TextPrimary");
    }

    /// <summary>Text box with a grey hint while it is empty.</summary>
    public static (Grid Host, TextBox Box) Input(string hint, bool multiline = false, double fontSize = 13)
    {
        var box = new TextBox { FontSize = fontSize };
        box.SetResourceReference(FrameworkElement.StyleProperty, "InputTextBox");
        var hintText = new TextBlock
        {
            Text = hint, FontSize = fontSize, IsHitTestVisible = false,
            Margin = new Thickness(14, multiline ? 10 : 0, 0, 0),
            VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
        };
        hintText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        if (multiline)
        {
            box.Height = double.NaN;
            box.AcceptsReturn = true;
            box.AcceptsTab = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.VerticalContentAlignment = VerticalAlignment.Top;
            box.Padding = new Thickness(12, 9, 12, 9);
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        box.TextChanged += (_, _) => hintText.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var host = new Grid();
        host.Children.Add(box);
        host.Children.Add(hintText);
        return (host, box);
    }

    public static Border Divider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
        line.SetResourceReference(Border.BackgroundProperty, "CardBorder");
        return line;
    }

    /// <summary>Row that highlights on hover and reacts to clicks.</summary>
    public static Border Row(UIElement child, Action click, bool selected)
    {
        var row = new Border { Child = child, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand };
        row.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        row.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSoft" : "SurfaceAlt");
        if (!selected)
        {
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            row.MouseLeave += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        }
        // Buttons inside the row (check box, star) handle their own clicks first.
        row.MouseLeftButtonUp += (_, _) => click();
        return row;
    }
}

/// <summary>Reads and writes one JSON file in the app's data folder.</summary>
internal static class DataFile
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string name) where T : new()
    {
        try
        {
            var path = Path.Combine(Folder, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T() : new T();
        }
        catch
        {
            return new T();
        }
    }

    public static void Save<T>(string name, T data)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, name);
            // Write to a temp file first so a crash mid-write never destroys the old data.
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, Options));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch
        {
            // A read-only profile should never break the app.
        }
    }
}
