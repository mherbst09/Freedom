using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ViennaAtomicClock;

internal sealed class NoteSection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int Color { get; set; } = 3;
}

internal sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SectionId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Plain text of the page, for search, previews and older versions.</summary>
    public string Body { get; set; } = "";
    /// <summary>Formatted page as XAML; empty for pages written before formatting existed.</summary>
    public string Content { get; set; } = "";
    public int Color { get; set; }
    public bool Pinned { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
}

internal sealed class NoteStore
{
    public List<Note> Notes { get; set; } = new();
    public List<NoteSection> Sections { get; set; } = new();
    public Guid LastSection { get; set; }
    public bool Initialized { get; set; }

    public static NoteStore Load()
    {
        var store = DataFile.Load<NoteStore>("notes.json");
        if (store.Sections.Count == 0)
            store.Sections.Add(new NoteSection { Name = Loc.T("notes.sectionDefault"), Color = 3 });
        // Pages from before sections existed move into the first section.
        foreach (var note in store.Notes.Where(n => store.Sections.All(s => s.Id != n.SectionId)))
            note.SectionId = store.Sections[0].Id;

        if (!store.Initialized)
        {
            store.Initialized = true;
            store.Notes.Add(new Note
            {
                SectionId = store.Sections[0].Id,
                Title = Loc.T("notes.welcomeTitle"),
                Body = Loc.T("notes.welcomeBody"),
                Color = 1,
                Pinned = true
            });
            store.Save();
        }
        return store;
    }

    public void Save() => DataFile.Save("notes.json", this);
}

/// <summary>
/// Notes in the style of OneNote: coloured sections with pages, a formatting ribbon (text styles,
/// highlighter, lists, to-do tags, tables, pictures, date stamps), autosave and export to .txt, .pdf and .docx.
/// </summary>
internal sealed class NotesPage : Grid
{
    private static CultureInfo Culture => Loc.Culture;

    // Colour tags work on every theme because they are only used as small stripes and dots.
    private static readonly (string Name, Color Color)[] Colors =
    {
        ("notes.color.none", System.Windows.Media.Colors.Transparent),
        ("notes.color.yellow", Color.FromRgb(242, 201, 76)), ("notes.color.green", Color.FromRgb(111, 207, 151)), ("notes.color.blue", Color.FromRgb(86, 170, 242)),
        ("notes.color.pink", Color.FromRgb(242, 140, 191)), ("notes.color.purple", Color.FromRgb(187, 107, 217))
    };

    private static readonly Color[] SectionColors =
    {
        Color.FromRgb(86, 170, 242), Color.FromRgb(111, 207, 151), Color.FromRgb(242, 201, 76), Color.FromRgb(242, 140, 191),
        Color.FromRgb(187, 107, 217), Color.FromRgb(255, 150, 90), Color.FromRgb(80, 200, 200), Color.FromRgb(160, 160, 170)
    };

    private static readonly Color[] Highlighters =
    {
        Color.FromArgb(150, 255, 230, 0), Color.FromArgb(140, 90, 230, 120), Color.FromArgb(140, 80, 190, 255), Color.FromArgb(140, 255, 120, 190), Color.FromArgb(140, 255, 160, 60)
    };

    private static readonly Color[] TextColors =
    {
        Color.FromRgb(232, 72, 72), Color.FromRgb(255, 150, 40), Color.FromRgb(46, 170, 90), Color.FromRgb(50, 130, 240), Color.FromRgb(150, 90, 220), Color.FromRgb(140, 140, 150)
    };

    private const string ToDo = "☐", Done = "☑", Important = "★", Question = "❓";

    private readonly NoteStore _store = NoteStore.Load();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private Guid? _selected;
    private Guid _section;
    private string _search = "";
    private bool _loadingEditor;

    private readonly StackPanel _list = new();
    private readonly WrapPanel _sectionTabs = new();
    private readonly TextBlock _count = Ui.Text("", 12, "TextSecondary");
    private readonly Border _listCard;
    private readonly Border _editorCard;
    private readonly Grid _editor = new();
    private readonly StackPanel _empty = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBox _titleBox = PlainBox(26, bold: true);
    private readonly RichTextBox _body = new();
    private readonly TextBlock _meta = Ui.Text("", 11.5, "TextMuted");
    private readonly TextBlock _status = Ui.Text("", 11.5, "AccentBrush");
    private readonly Button _pinButton;
    private readonly Button _deleteSectionButton;
    private readonly StackPanel _colorDots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    public event Action? Changed;

    public NotesPage()
    {
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveEditor(); _store.Save(); BuildList(); };
        _section = _store.Sections.Any(s => s.Id == _store.LastSection) ? _store.LastSection : _store.Sections[0].Id;

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star), MinWidth = 270 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });

        // ---------- Sections and pages ----------
        var listPanel = new DockPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        var notesTitle = Ui.Text("", 22, title: true);
        notesTitle.SetResourceReference(TextBlock.TextProperty, "L.notes.title");
        titles.Children.Add(notesTitle);
        _count.Margin = new Thickness(0, 4, 0, 0);
        titles.Children.Add(_count);
        header.Children.Add(titles);
        var add = Ui.Button("ActionButton", "", (_, _) => NewNote());
        add.SetResourceReference(ContentControl.ContentProperty, "L.notes.new");
        add.SetResourceReference(ToolTipProperty, "L.notes.newTip");
        add.Height = 38;
        add.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        DockPanel.SetDock(header, Dock.Top);
        listPanel.Children.Add(header);

        var (searchHost, searchBox) = Ui.Input("", resourceKey: "L.notes.search");
        searchBox.TextChanged += (_, _) => { _search = searchBox.Text.Trim(); BuildList(); };
        searchHost.Margin = new Thickness(0, 14, 0, 10);
        DockPanel.SetDock(searchHost, Dock.Top);
        listPanel.Children.Add(searchHost);

        // Section tabs like the coloured tabs of a OneNote notebook.
        var sectionHeader = new Grid { Margin = new Thickness(2, 0, 0, 6) };
        sectionHeader.ColumnDefinitions.Add(new ColumnDefinition());
        sectionHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sectionLabel = Ui.Text("", 10.5, "TextMuted", bold: true);
        sectionLabel.SetResourceReference(TextBlock.TextProperty, "L.notes.sections");
        sectionLabel.VerticalAlignment = VerticalAlignment.Center;
        sectionHeader.Children.Add(sectionLabel);
        var sectionTools = new StackPanel { Orientation = Orientation.Horizontal };
        sectionTools.Children.Add(SmallTool("✎", "L.notes.renameSection", b => RenameSection(b)));
        sectionTools.Children.Add(SmallTool("●", "L.notes.sectionColor", b => PickSectionColor(b)));
        _deleteSectionButton = SmallTool("🗑", "L.notes.deleteSection", _ => DeleteSection());
        sectionTools.Children.Add(_deleteSectionButton);
        Grid.SetColumn(sectionTools, 1);
        sectionHeader.Children.Add(sectionTools);
        DockPanel.SetDock(sectionHeader, Dock.Top);
        listPanel.Children.Add(sectionHeader);
        _sectionTabs.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(_sectionTabs, Dock.Top);
        listPanel.Children.Add(_sectionTabs);

        listPanel.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Margin = new Thickness(0, 0, -6, 0) });
        _listCard = Ui.Card(listPanel, 18);
        Children.Add(_listCard);

        // ---------- Editor ----------
        _body.Resources.Add(typeof(Paragraph), ParagraphStyle());
        _body.Resources.Add(typeof(Table), TableStyle());
        _body.BorderThickness = new Thickness(0);
        _body.Background = Brushes.Transparent;
        _body.Padding = new Thickness(0);
        _body.FontSize = NoteDocument.BodySize;
        _body.AcceptsTab = true;
        _body.IsDocumentEnabled = true;
        _body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _body.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        _body.SetResourceReference(Control.FontFamilyProperty, "UiFont");
        _body.SetResourceReference(TextBoxBase.CaretBrushProperty, "AccentBrush");
        _body.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentBrush");
        _body.TextChanged += (_, _) => Edited(_ => { });
        _body.PreviewMouseLeftButtonUp += ClickCheckbox;
        _body.PreviewKeyDown += BodyKeys;
        DataObject.AddPastingHandler(_body, OnPaste);
        SpellCheck.SetIsEnabled(_body, false);

        var ribbon = BuildRibbon();
        var topRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        topRow.ColumnDefinitions.Add(new ColumnDefinition());
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.Children.Add(ribbon);
        var pageTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0) };
        _pinButton = Ui.Button("ChipButton", "", (_, _) => TogglePin());
        _pinButton.SetResourceReference(ToolTipProperty, "L.notes.pinTip");
        pageTools.Children.Add(_pinButton);
        var export = Ui.Button("ChipButton", "", (s, _) => ShowExportMenu((FrameworkElement)s));
        export.SetResourceReference(ContentControl.ContentProperty, "L.notes.export");
        pageTools.Children.Add(export);
        var delete = Ui.Button("IconButton", "🗑", (_, _) => DeleteNote());
        delete.Width = delete.Height = 34;
        delete.SetResourceReference(ToolTipProperty, "L.notes.deleteTip");
        delete.SetResourceReference(Control.ForegroundProperty, "OrangeBrush");
        pageTools.Children.Add(delete);
        Grid.SetColumn(pageTools, 1);
        topRow.Children.Add(pageTools);

        var metaRow = new Grid { Margin = new Thickness(2, 2, 0, 12) };
        metaRow.ColumnDefinitions.Add(new ColumnDefinition());
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _meta.TextWrapping = TextWrapping.Wrap;
        _meta.TextTrimming = TextTrimming.None;
        metaRow.Children.Add(_meta);
        Grid.SetColumn(_colorDots, 1);
        metaRow.Children.Add(_colorDots);

        _editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _editor.Children.Add(topRow);
        Grid.SetRow(_titleBox, 1);
        _editor.Children.Add(_titleBox);
        Grid.SetRow(metaRow, 2);
        _editor.Children.Add(metaRow);
        Grid.SetRow(_body, 3);
        _editor.Children.Add(_body);
        _status.Margin = new Thickness(2, 8, 0, 0);
        _status.Visibility = Visibility.Collapsed;
        Grid.SetRow(_status, 4);
        _editor.Children.Add(_status);

        _titleBox.TextChanged += (_, _) => Edited(n => n.Title = _titleBox.Text);
        _titleBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _body.Focus();
            _body.CaretPosition = _body.Document.ContentStart;
            e.Handled = true;
        };

        var emptyIcon = Ui.Text("✎", 46, "AccentBrush");
        emptyIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Children.Add(emptyIcon);
        var emptyText = Ui.Text("", 14, "TextSecondary");
        emptyText.SetResourceReference(TextBlock.TextProperty, "L.notes.pick");
        emptyText.TextAlignment = TextAlignment.Center;
        emptyText.Margin = new Thickness(0, 10, 0, 14);
        _empty.Children.Add(emptyText);
        var emptyButton = Ui.Button("ActionButton", "", (_, _) => NewNote());
        emptyButton.SetResourceReference(ContentControl.ContentProperty, "L.notes.newNote");
        _empty.Children.Add(emptyButton);

        var editorHost = new Grid();
        editorHost.Children.Add(_editor);
        editorHost.Children.Add(_empty);
        _editorCard = Ui.Card(editorHost, 22);
        Grid.SetColumn(_editorCard, 2);
        Children.Add(_editorCard);

        PreviewKeyDown += OnKeys;
        _selected = Sorted().FirstOrDefault(n => n.SectionId == _section)?.Id;
        BuildList();
        LoadEditor();
        Loc.Changed += () => { BuildList(); if (Current is { } note) { UpdateMeta(note); UpdateToolbar(note); } };
    }

    public int Count => _store.Notes.Count;

    public void Flush()
    {
        _saveTimer.Stop();
        SaveEditor();
        _store.Save();
    }

    /// <summary>List beside the editor; on narrow windows the list sits above it.</summary>
    public void ApplyLayout(double width)
    {
        var narrow = width < 900;
        ColumnDefinitions[0].MinWidth = narrow ? 0 : 270;
        ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0.9, GridUnitType.Star);
        ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 14);
        ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        RowDefinitions[0].Height = narrow ? new GridLength(0.75, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        RowDefinitions[1].Height = new GridLength(narrow ? 14 : 0);
        RowDefinitions[2].Height = narrow ? new GridLength(1.4, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(_editorCard, narrow ? 0 : 2);
        Grid.SetRow(_editorCard, narrow ? 2 : 0);
    }

    /// <summary>Paragraph spacing used in the editor and in exports.</summary>
    public static Style ParagraphStyle()
    {
        var style = new Style(typeof(Paragraph));
        style.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 0, 0, 6)));
        style.Setters.Add(new Setter(Block.LineHeightProperty, 22.0));
        return style;
    }

    public static Style TableStyle()
    {
        var style = new Style(typeof(Table));
        style.Setters.Add(new Setter(Table.CellSpacingProperty, 0.0));
        style.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 4, 0, 10)));
        return style;
    }

    private static TextBox PlainBox(double size, bool bold)
    {
        var box = new TextBox
        {
            FontSize = size, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top
        };
        if (bold) box.FontWeight = FontWeights.Bold;
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        box.SetResourceReference(TextBox.CaretBrushProperty, "AccentBrush");
        box.SetResourceReference(TextBox.SelectionBrushProperty, "AccentBrush");
        box.SetResourceReference(Control.FontFamilyProperty, bold ? "StencilFont" : "UiFont");
        return box;
    }

    // ---------- Ribbon ----------

    private Border BuildRibbon()
    {
        var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        void Group(params UIElement[] items)
        {
            if (panel.Children.Count > 0)
            {
                var line = new Border { Width = 1, Height = 20, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                line.SetResourceReference(Border.BackgroundProperty, "CardBorder");
                panel.Children.Add(line);
            }
            foreach (var item in items) panel.Children.Add(item);
        }

        var bold = Tool(new TextBlock { Text = "B", FontWeight = FontWeights.Bold }, "fmt.bold", () => EditingCommands.ToggleBold.Execute(null, _body));
        var italic = Tool(new TextBlock { Text = "I", FontStyle = FontStyles.Italic, FontFamily = new FontFamily("Georgia") }, "fmt.italic", () => EditingCommands.ToggleItalic.Execute(null, _body));
        var underline = Tool(new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline }, "fmt.underline", () => EditingCommands.ToggleUnderline.Execute(null, _body));
        var strike = Tool(new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough }, "fmt.strike", ToggleStrike);
        Group(bold, italic, underline, strike);

        var marker = Tool(new TextBlock { Text = "🖍" }, "fmt.highlight", null);
        marker.Click += (_, _) => ColorFlyout(marker, Highlighters, c => Apply(TextElement.BackgroundProperty, c is { } color ? Frozen(color) : null));
        var color = Tool(ColorGlyph(), "fmt.color", null);
        color.Click += (_, _) => ColorFlyout(color, TextColors, c => Apply(TextElement.ForegroundProperty, c is { } col ? Frozen(col) : null));
        Group(marker, color);

        Group(Tool(new TextBlock { Text = "H1", FontWeight = FontWeights.Bold }, "fmt.h1", () => SetStyle(26, true)),
              Tool(new TextBlock { Text = "H2", FontWeight = FontWeights.SemiBold, FontSize = 12 }, "fmt.h2", () => SetStyle(20, true)),
              Tool(new TextBlock { Text = "¶" }, "fmt.normal", () => SetStyle(null, false)));

        Group(Tool(new TextBlock { Text = "•≡" }, "fmt.bullets", () => EditingCommands.ToggleBullets.Execute(null, _body)),
              Tool(new TextBlock { Text = "1≡", FontSize = 12 }, "fmt.numbers", () => EditingCommands.ToggleNumbering.Execute(null, _body)),
              Tool(new TextBlock { Text = "⇤" }, "fmt.outdent", () => EditingCommands.DecreaseIndentation.Execute(null, _body)),
              Tool(new TextBlock { Text = "⇥" }, "fmt.indent", () => EditingCommands.IncreaseIndentation.Execute(null, _body)));

        Group(Tool(new TextBlock { Text = ToDo }, "fmt.todo", () => ToggleTag(ToDo)),
              Tool(new TextBlock { Text = Important, Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 40)) }, "fmt.important", () => ToggleTag(Important)),
              Tool(new TextBlock { Text = "?" , FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(160, 120, 255)) }, "fmt.question", () => ToggleTag(Question)));

        var table = Tool(new TextBlock { Text = "▦" }, "fmt.table", null);
        table.Click += (_, _) => Menu(table, new (string, Action)[]
        {
            (Loc.T("fmt.insertTable"), InsertTable),
            (Loc.T("fmt.addRow"), () => ChangeTable(addRow: true)),
            (Loc.T("fmt.addColumn"), () => ChangeTable(addColumn: true)),
            (Loc.T("fmt.deleteRow"), () => ChangeTable(deleteRow: true))
        });
        Group(table, Tool(new TextBlock { Text = "🖼" }, "fmt.image", InsertImageFromFile), Tool(new TextBlock { Text = "🕒" }, "fmt.date", InsertDate));

        Group(Tool(new TextBlock { Text = "↶" }, "fmt.undo", () => ApplicationCommands.Undo.Execute(null, _body)),
              Tool(new TextBlock { Text = "↷" }, "fmt.redo", () => ApplicationCommands.Redo.Execute(null, _body)),
              Tool(new TextBlock { Text = "A✕", FontSize = 11 }, "fmt.clear", ClearFormatting));

        var ribbon = new Border { Child = panel, Padding = new Thickness(4) };
        ribbon.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        ribbon.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        return ribbon;
    }

    private static UIElement ColorGlyph()
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "A", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new Border
        {
            Height = 3, Width = 14, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, -1, 0, 0),
            Background = new LinearGradientBrush(Color.FromRgb(232, 72, 72), Color.FromRgb(50, 130, 240), 0)
        });
        return stack;
    }

    private Button Tool(UIElement glyph, string tipKey, Action? action)
    {
        var button = Ui.Button("IconButton", glyph, (_, _) => { action?.Invoke(); if (action is not null) _body.Focus(); });
        button.Width = 32;
        button.Height = 32;
        button.Margin = new Thickness(1, 0, 1, 0);
        button.FontSize = 14;
        button.Focusable = false;
        button.SetResourceReference(ToolTipProperty, "L." + tipKey);
        return button;
    }

    private static Button SmallTool(string glyph, string tipKey, Action<Button> action)
    {
        Button? button = null;
        button = Ui.Button("IconButton", glyph, (_, _) => action(button!));
        button.Width = 28;
        button.Height = 26;
        button.Margin = new Thickness(1, 0, 0, 0);
        button.FontSize = 12;
        button.SetResourceReference(ToolTipProperty, tipKey);
        return button;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Small floating panel under a button, closed by clicking anywhere else.</summary>
    private static Popup Flyout(FrameworkElement target, UIElement content)
    {
        var shadow = new Border();
        shadow.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        shadow.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        shadow.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.3 };
        var card = new Border { Child = content, Padding = new Thickness(8), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        card.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        RenderOptions.SetClearTypeHint(card, ClearTypeHint.Enabled);
        var host = new Grid { Margin = new Thickness(0, 0, 10, 10) };
        TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
        host.Children.Add(shadow);
        host.Children.Add(card);
        var popup = new Popup { Child = host, PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        popup.Opened += (_, _) => Motion.PopIn(host, 0.1);
        popup.IsOpen = true;
        return popup;
    }

    private static void Menu(FrameworkElement target, IEnumerable<(string Text, Action Action)> items)
    {
        var stack = new StackPanel { MinWidth = 200 };
        Popup? popup = null;
        foreach (var (text, action) in items)
            stack.Children.Add(Ui.Row(Ui.Text(text, 13), () => { popup!.IsOpen = false; action(); }, false));
        popup = Flyout(target, stack);
    }

    private static void ColorFlyout(FrameworkElement target, Color[] colors, Action<Color?> pick)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        Popup? popup = null;
        foreach (var color in colors.Select(c => (Color?)c).Append(null))
        {
            var dot = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(3), Cursor = Cursors.Hand, BorderThickness = new Thickness(1.5) };
            dot.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            if (color is { } c) dot.Background = Frozen(c);
            else
            {
                dot.Child = Ui.Text("✕", 11, "TextSecondary");
                ((TextBlock)dot.Child).HorizontalAlignment = HorizontalAlignment.Center;
                ((TextBlock)dot.Child).VerticalAlignment = VerticalAlignment.Center;
                dot.ToolTip = Loc.T("fmt.none");
            }
            dot.MouseEnter += (_, _) => Motion.To(dot, OpacityProperty, 0.75);
            dot.MouseLeave += (_, _) => Motion.To(dot, OpacityProperty, 1);
            dot.MouseLeftButtonUp += (_, _) => { popup!.IsOpen = false; pick(color); };
            stack.Children.Add(dot);
        }
        popup = Flyout(target, stack);
    }

    // ---------- Formatting ----------

    private void Apply(DependencyProperty property, object? value)
    {
        _body.Focus();
        if (value is null)
        {
            // "No colour": take the value off every piece of text in the selection.
            foreach (var inline in InlinesIn(_body.Selection)) inline.ClearValue(property);
            Edited(_ => { });
        }
        else _body.Selection.ApplyPropertyValue(property, value);
    }

    private void ToggleStrike()
    {
        var current = _body.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        var on = current is TextDecorationCollection decorations && decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough);
        _body.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, on ? new TextDecorationCollection() : TextDecorations.Strikethrough);
    }

    private void SetStyle(double? size, bool bold)
    {
        foreach (var paragraph in ParagraphsIn(_body.Selection))
        {
            foreach (var inline in NoteDocument.AllInlines(paragraph.Inlines))
            {
                inline.ClearValue(TextElement.FontSizeProperty);
                inline.ClearValue(TextElement.FontWeightProperty);
            }
            if (size is { } s) paragraph.FontSize = s;
            else paragraph.ClearValue(TextElement.FontSizeProperty);
            if (bold) paragraph.FontWeight = FontWeights.Bold;
            else paragraph.ClearValue(TextElement.FontWeightProperty);
            paragraph.Margin = size is null ? new Thickness(0, 0, 0, 6) : new Thickness(0, 10, 0, 6);
            if (size is null) paragraph.ClearValue(Block.MarginProperty);
        }
        Edited(_ => { });
    }

    private void ClearFormatting()
    {
        foreach (var inline in InlinesIn(_body.Selection))
            foreach (var property in new DependencyProperty[]
                     {
                         TextElement.FontWeightProperty, TextElement.FontStyleProperty, TextElement.ForegroundProperty, TextElement.BackgroundProperty,
                         TextElement.FontSizeProperty, TextElement.FontFamilyProperty, Inline.TextDecorationsProperty
                     })
                inline.ClearValue(property);
        SetStyle(null, false);
    }

    private IEnumerable<Paragraph> ParagraphsIn(TextSelection selection)
    {
        var start = selection.Start.Paragraph;
        var end = selection.End.Paragraph;
        if (start is null) yield break;
        var inside = false;
        foreach (var paragraph in NoteDocument.AllBlocks(_body.Document.Blocks).OfType<Paragraph>())
        {
            if (paragraph == start) inside = true;
            if (inside) yield return paragraph;
            if (paragraph == (end ?? start)) yield break;
        }
    }

    private IEnumerable<Inline> InlinesIn(TextSelection selection) =>
        ParagraphsIn(selection).SelectMany(p => NoteDocument.AllInlines(p.Inlines)).Where(i =>
            !selection.IsEmpty && i.ContentEnd.CompareTo(selection.Start) > 0 && i.ContentStart.CompareTo(selection.End) < 0).ToList();

    /// <summary>OneNote-style tags at the start of a line: to-do box, important star, question mark.</summary>
    private void ToggleTag(string tag)
    {
        foreach (var paragraph in ParagraphsIn(_body.Selection).ToList())
        {
            var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            var text = new TextRange(start, paragraph.ContentEnd).Text;
            var existing = new[] { ToDo, Done, Important, Question }.FirstOrDefault(t => text.StartsWith(t + " "));
            if (existing is not null)
            {
                var end = start.GetPositionAtOffset(existing.Length + 1, LogicalDirection.Forward);
                if (end is not null) new TextRange(start, end).Text = "";
                if (existing == tag || (tag == ToDo && existing == Done)) continue;
                start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            }
            start.InsertTextInRun(tag + " ");
        }
        Edited(_ => { });
    }

    /// <summary>Ticks the to-do box on the caret line (Ctrl+D) or wherever the box is clicked.</summary>
    private bool ToggleCheck(Paragraph? paragraph)
    {
        if (paragraph is null) return false;
        var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var end = start.GetPositionAtOffset(1, LogicalDirection.Forward);
        if (end is null) return false;
        var box = new TextRange(start, end);
        if (box.Text == ToDo) box.Text = Done;
        else if (box.Text == Done) box.Text = ToDo;
        else return false;
        Edited(_ => { });
        return true;
    }

    private void ClickCheckbox(object sender, MouseButtonEventArgs e)
    {
        if (!_body.Selection.IsEmpty) return;
        var position = _body.GetPositionFromPoint(e.GetPosition(_body), false);
        if (position?.Paragraph is not { } paragraph) return;
        var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        if (new TextRange(start, position).Text.Length <= 1) ToggleCheck(paragraph);
    }

    private void InsertTable()
    {
        var table = new Table();
        for (var i = 0; i < 3; i++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        for (var r = 0; r < 3; r++) group.Rows.Add(NewRow(3, header: r == 0));
        table.RowGroups.Add(group);
        InsertBlock(table);
    }

    private static TableRow NewRow(int columns, bool header = false)
    {
        var row = new TableRow();
        for (var c = 0; c < columns; c++)
        {
            var cell = new TableCell(new Paragraph()) { BorderThickness = new Thickness(0.5), Padding = new Thickness(8, 5, 8, 5), BorderBrush = Frozen(Color.FromArgb(150, 140, 140, 150)) };
            if (header) cell.FontWeight = FontWeights.SemiBold;
            row.Cells.Add(cell);
        }
        return row;
    }

    private TableCell? CurrentCell()
    {
        for (DependencyObject? node = _body.CaretPosition.Parent; node is not null; node = (node as TextElement)?.Parent)
            if (node is TableCell cell) return cell;
        return null;
    }

    private void ChangeTable(bool addRow = false, bool addColumn = false, bool deleteRow = false)
    {
        if (CurrentCell() is not { Parent: TableRow { Parent: TableRowGroup { Parent: Table table } group } row } cell) return;
        if (addRow)
        {
            var created = NewRow(row.Cells.Count);
            group.Rows.Insert(group.Rows.IndexOf(row) + 1, created);
            _body.CaretPosition = created.Cells[0].ContentStart;
        }
        if (addColumn)
        {
            var index = row.Cells.IndexOf(cell) + 1;
            table.Columns.Add(new TableColumn());
            foreach (var r in group.Rows) r.Cells.Insert(Math.Min(index, r.Cells.Count), NewRow(1).Cells[0].Let(c => { if (r.Cells.Count > 0) c.FontWeight = r.Cells[0].FontWeight; }));
        }
        if (deleteRow)
        {
            if (group.Rows.Count <= 1) _body.Document.Blocks.Remove(table);
            else group.Rows.Remove(row);
        }
        Edited(_ => { });
    }

    private void InsertBlock(Block block)
    {
        var current = _body.CaretPosition.Paragraph;
        var blocks = current?.Parent switch
        {
            FlowDocument doc => doc.Blocks,
            Section section => section.Blocks,
            ListItem item => item.Blocks,
            TableCell cell => cell.Blocks,
            _ => _body.Document.Blocks
        };
        if (current is not null && blocks.Contains(current)) blocks.InsertAfter(current, block);
        else blocks.Add(block);
        var after = new Paragraph();
        blocks.InsertAfter(block, after);
        _body.CaretPosition = after.ContentStart;
        Edited(_ => { });
    }

    private void InsertImageFromFile()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("fmt.picture") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            if (NoteDocument.LoadImage(File.ReadAllBytes(dialog.FileName)) is { } image) InsertBlock(new BlockUIContainer(image));
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.F("notes.exportFailed", ex.Message));
        }
    }

    private void InsertDate()
    {
        _body.Focus();
        var now = DateTime.Now;
        _body.Selection.Text = now.ToString("D", Culture) + " " + now.ToString("t", Culture);
        _body.CaretPosition = _body.Selection.End;
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        // A copied picture (screenshot) becomes a picture in the note.
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText) && Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
        {
            e.CancelCommand();
            InsertBlock(new BlockUIContainer(NoteDocument.FromBitmap(bitmap)));
            return;
        }
        // Formatting from websites or Word stays, but their fixed fonts and black/white text would clash with the theme.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CleanPastedColors));
    }

    private void CleanPastedColors()
    {
        var text = ((SolidColorBrush)FindResource("TextPrimary")).Color;
        foreach (var element in NoteDocument.AllBlocks(_body.Document.Blocks).Cast<TextElement>()
                     .Concat(NoteDocument.AllBlocks(_body.Document.Blocks).OfType<Paragraph>().SelectMany(p => NoteDocument.AllInlines(p.Inlines))))
        {
            element.ClearValue(TextElement.FontFamilyProperty);
            if (element.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush { Color: var c } &&
                (Math.Abs(c.R - text.R) + Math.Abs(c.G - text.G) + Math.Abs(c.B - text.B) < 60 || c.R + c.G + c.B < 80 || c.R + c.G + c.B > 700))
                element.ClearValue(TextElement.ForegroundProperty);
            if (element.ReadLocalValue(TextElement.BackgroundProperty) is SolidColorBrush { Color: var b } && b.R + b.G + b.B > 700)
                element.ClearValue(TextElement.BackgroundProperty);
        }
    }

    // ---------- Keys ----------

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { NewNote(); e.Handled = true; }
    }

    private void BodyKeys(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.D1: case Key.L: ToggleTag(ToDo); e.Handled = true; break;
                case Key.D2: ToggleTag(Important); e.Handled = true; break;
                case Key.D3: ToggleTag(Question); e.Handled = true; break;
                case Key.D: e.Handled = ToggleCheck(_body.CaretPosition.Paragraph); break;
            }
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.X) { ToggleStrike(); e.Handled = true; }
        else if (mods == (ModifierKeys.Alt | ModifierKeys.Shift) && key == Key.D) { InsertDate(); e.Handled = true; }
        else if (mods == ModifierKeys.None && key == Key.Tab && CurrentCell() is { Parent: TableRow { Parent: TableRowGroup group } row } cell &&
                 row == group.Rows[^1] && cell == row.Cells[^1])
        {
            // Tab in the last cell adds a row, like in OneNote and Word.
            ChangeTable(addRow: true);
            e.Handled = true;
        }
    }

    // ---------- Pages and sections ----------

    private IEnumerable<Note> Sorted() => _store.Notes.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Updated);

    private Note? Current => _store.Notes.FirstOrDefault(n => n.Id == _selected);

    private NoteSection Section => _store.Sections.FirstOrDefault(s => s.Id == _section) ?? _store.Sections[0];

    private void Edited(Action<Note> change)
    {
        if (_loadingEditor || Current is not { } note) return;
        change(note);
        note.Updated = DateTime.Now;
        UpdateMeta(note);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveEditor()
    {
        if (Current is not { } note || _body.Document is null) return;
        note.Content = NoteDocument.Serialize(_body.Document);
        note.Body = NoteDocument.PlainText(_body.Document);
    }

    private void Select(Guid? id)
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveEditor(); _store.Save(); }
        _selected = id;
        BuildList();
        LoadEditor();
    }

    private void NewNote()
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveEditor(); }
        var note = new Note { SectionId = _section };
        _store.Notes.Add(note);
        _selected = note.Id;
        _store.Save();
        BuildList();
        LoadEditor();
        _titleBox.Focus();
        Changed?.Invoke();
    }

    private void DeleteNote()
    {
        if (Current is not { } note) return;
        if (note.Title.Length + note.Body.Length > 0 &&
            MessageBox.Show(Loc.F("notes.deleteQ", note.Title.Length > 0 ? note.Title : Loc.T("notes.untitled")), Loc.T("notes.deleteTip"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _saveTimer.Stop();
        _store.Notes.Remove(note);
        _selected = Sorted().FirstOrDefault(n => n.SectionId == _section)?.Id;
        _store.Save();
        BuildList();
        LoadEditor();
        Changed?.Invoke();
    }

    private void TogglePin()
    {
        if (Current is not { } note) return;
        note.Pinned = !note.Pinned;
        _store.Save();
        BuildList();
        UpdateToolbar(note);
    }

    private void OpenSection(Guid id)
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveEditor(); }
        _section = id;
        _store.LastSection = id;
        _store.Save();
        _selected = Sorted().FirstOrDefault(n => n.SectionId == id)?.Id;
        BuildList();
        LoadEditor();
    }

    private void AddSection()
    {
        var section = new NoteSection { Name = Loc.T("notes.sectionName"), Color = _store.Sections.Count % SectionColors.Length };
        _store.Sections.Add(section);
        OpenSection(section.Id);
        RenameSection(_sectionTabs.Children.OfType<FrameworkElement>().LastOrDefault(c => c.Tag is Guid g && g == section.Id) ?? _sectionTabs);
    }

    private void RenameSection(FrameworkElement anchor)
    {
        var section = Section;
        var (host, box) = Ui.Input("", resourceKey: "L.notes.renameSection");
        host.Width = 230;
        box.Text = section.Name;
        var popup = Flyout(anchor, host);
        void Commit()
        {
            if (box.Text.Trim().Length > 0) section.Name = box.Text.Trim();
            popup.IsOpen = false;
            _store.Save();
            BuildList();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) popup.IsOpen = false;
        };
        popup.Closed += (_, _) => { if (box.Text.Trim().Length > 0 && box.Text.Trim() != section.Name) Commit(); };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    private void PickSectionColor(FrameworkElement anchor) =>
        ColorFlyout(anchor, SectionColors, c =>
        {
            if (c is not { } color) return;
            Section.Color = Array.IndexOf(SectionColors, color);
            _store.Save();
            BuildList();
        });

    private void DeleteSection()
    {
        if (_store.Sections.Count <= 1) return;
        var section = Section;
        var pages = _store.Notes.Count(n => n.SectionId == section.Id);
        if (MessageBox.Show(Loc.F("notes.deleteSectionQ", section.Name, pages), Loc.T("notes.deleteSection"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _saveTimer.Stop();
        _store.Notes.RemoveAll(n => n.SectionId == section.Id);
        _store.Sections.Remove(section);
        OpenSection(_store.Sections[0].Id);
        Changed?.Invoke();
    }

    private void BuildSections()
    {
        _sectionTabs.Children.Clear();
        foreach (var section in _store.Sections)
        {
            var selected = section.Id == _section && _search.Length == 0;
            var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Margin = new Thickness(0, 0, 7, 0), Background = Frozen(SectionColors[Math.Clamp(section.Color, 0, SectionColors.Length - 1)]) };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(dot);
            var count = _store.Notes.Count(n => n.SectionId == section.Id);
            content.Children.Add(new TextBlock { Text = section.Name, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = $"  {count}", Opacity = 0.55 });
            var id = section.Id;
            var chip = Ui.Button("ChipButton", content, (_, _) => OpenSection(id));
            chip.Height = 30;
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.Tag = id;
            chip.MouseDoubleClick += (_, _) => RenameSection(chip);
            Ui.Select(chip, selected);
            _sectionTabs.Children.Add(chip);
        }
        var add = Ui.Button("ChipButton", "＋", (_, _) => AddSection());
        add.Height = 30;
        add.Margin = new Thickness(0, 0, 6, 6);
        add.SetResourceReference(ToolTipProperty, "L.notes.newSection");
        _sectionTabs.Children.Add(add);
        _deleteSectionButton.IsEnabled = _store.Sections.Count > 1;
    }

    private void BuildList()
    {
        BuildSections();
        _list.Children.Clear();
        var searching = _search.Length > 0;
        var notes = Sorted().Where(n => searching ? Matches(n) : n.SectionId == _section).ToList();
        var inSection = _store.Notes.Count(n => n.SectionId == _section);
        _count.Text = searching ? Loc.F("notes.hits", notes.Count) : Loc.F("notes.pagesIn", inSection, Section.Name, _store.Notes.Count);

        if (notes.Count == 0)
        {
            var none = Ui.Text(Loc.T(searching ? "notes.nothingFound" : "notes.none"), 13, "TextSecondary");
            none.Margin = new Thickness(4, 20, 0, 0);
            _list.Children.Add(none);
            return;
        }

        var pinned = notes.Where(n => n.Pinned).ToList();
        if (pinned.Count > 0) _list.Children.Add(Heading(Loc.T("notes.pinnedSection")));
        foreach (var note in pinned) _list.Children.Add(NoteRow(note, searching));
        var others = notes.Where(n => !n.Pinned).ToList();
        if (pinned.Count > 0 && others.Count > 0) _list.Children.Add(Heading(Loc.T("notes.section")));
        foreach (var note in others) _list.Children.Add(NoteRow(note, searching));
    }

    private bool Matches(Note note) =>
        note.Title.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ||
        note.Body.Contains(_search, StringComparison.CurrentCultureIgnoreCase);

    private static TextBlock Heading(string text)
    {
        var label = Ui.Text(text, 10.5, "TextMuted", bold: true);
        label.Margin = new Thickness(4, 6, 0, 6);
        return label;
    }

    private UIElement NoteRow(Note note, bool showSection)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var stripe = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Colors[note.Color].Color), Margin = new Thickness(0, 2, 0, 2) };
        grid.Children.Add(stripe);

        var texts = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(Ui.Text(note.Title.Length > 0 ? note.Title : Loc.T("notes.emptyTitle"), 14, note.Title.Length > 0 ? "TextPrimary" : "TextMuted", bold: true));
        var date = Ui.Text(DateLabel(note.Updated), 11, "TextMuted");
        date.Margin = new Thickness(8, 1, 0, 0);
        Grid.SetColumn(date, 1);
        titleRow.Children.Add(date);
        texts.Children.Add(titleRow);

        if (showSection && _store.Sections.FirstOrDefault(s => s.Id == note.SectionId) is { } section)
        {
            var where = Ui.Text("● " + section.Name, 11, "TextMuted");
            where.Foreground = Frozen(SectionColors[Math.Clamp(section.Color, 0, SectionColors.Length - 1)]);
            where.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(where);
        }

        var preview = string.Join(" ", note.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(3)).Trim();
        var previewText = Ui.Text(preview.Length > 0 ? preview : Loc.T("notes.noText"), 12, "TextSecondary");
        previewText.TextTrimming = TextTrimming.CharacterEllipsis;
        previewText.TextWrapping = TextWrapping.Wrap;
        previewText.MaxHeight = 36;
        previewText.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(previewText);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        return Ui.Row(grid, () =>
        {
            if (note.SectionId != _section) { _section = note.SectionId; _store.LastSection = _section; }
            Select(note.Id);
        }, note.Id == _selected);
    }

    private static string DateLabel(DateTime date)
    {
        var days = (DateTime.Today - date.Date).Days;
        return days switch
        {
            0 => date.ToString("HH:mm", Culture),
            1 => Loc.T("yesterday"),
            < 7 => date.ToString("dddd", Culture),
            _ => date.Year == DateTime.Today.Year ? date.ToString("M", Culture) : date.ToString("d", Culture)
        };
    }

    private void LoadEditor()
    {
        var note = Current;
        _editor.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = note is null ? Visibility.Visible : Visibility.Collapsed;
        _status.Visibility = Visibility.Collapsed;
        if (note is null) return;

        _loadingEditor = true;
        _titleBox.Text = note.Title;
        _body.Document = NoteDocument.Deserialize(note.Content, note.Body);
        _body.Document.PagePadding = new Thickness(0, 2, 6, 2);
        _body.Document.TextAlignment = TextAlignment.Left;
        _body.CaretPosition = _body.Document.ContentEnd;
        _loadingEditor = false;
        UpdateMeta(note);
        UpdateToolbar(note);
    }

    private void UpdateMeta(Note note)
    {
        var plain = _body.Document is not null && !_loadingEditor && note.Id == _selected ? NoteDocument.PlainText(_body.Document) : note.Body;
        var words = plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var lines = plain.Split('\n');
        var open = lines.Count(l => l.TrimStart('•', ' ').StartsWith(ToDo));
        var done = lines.Count(l => l.TrimStart('•', ' ').StartsWith(Done));
        var created = note.Created.ToString("D", Culture) + "  " + note.Created.ToString("t", Culture);
        var edited = note.Updated.Date == DateTime.Today ? Loc.F("notes.todayAt", note.Updated.ToString("HH:mm")) : note.Updated.ToString("g", Culture);
        _meta.Text = $"{created}  ·  {Loc.F("notes.edited", edited)}  ·  {words} {Loc.T(words == 1 ? "notes.word" : "notes.words")}" +
                     (open + done > 0 ? "  ·  " + Loc.F("notes.checked", done, open + done) : "");
    }

    private void UpdateToolbar(Note note)
    {
        _pinButton.Content = Loc.T(note.Pinned ? "notes.pinned" : "notes.pin");
        Ui.Select(_pinButton, note.Pinned);

        _colorDots.Children.Clear();
        for (var i = 0; i < Colors.Length; i++)
        {
            var index = i;
            var dot = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand,
                ToolTip = Loc.T(Colors[i].Name),
                BorderThickness = new Thickness(note.Color == i ? 3 : 1.5),
                Background = new SolidColorBrush(Colors[i].Color)
            };
            dot.SetResourceReference(Border.BorderBrushProperty, note.Color == i ? "AccentBrush" : "CardBorder");
            dot.MouseLeftButtonUp += (_, _) =>
            {
                note.Color = index;
                _store.Save();
                BuildList();
                UpdateToolbar(note);
            };
            _colorDots.Children.Add(dot);
        }
    }

    // ---------- Export ----------

    private void ShowExportMenu(FrameworkElement anchor) =>
        Menu(anchor, new (string, Action)[]
        {
            ("📄  " + Loc.T("notes.exportTxt"), () => Export("txt")),
            ("📕  " + Loc.T("notes.exportPdf"), () => Export("pdf")),
            ("📘  " + Loc.T("notes.exportDocx"), () => Export("docx")),
            ("🖨  " + Loc.T("notes.print"), Print)
        });

    private void Export(string kind)
    {
        if (Current is not { } note) return;
        var name = string.Concat((note.Title.Length > 0 ? note.Title : Loc.T("notes.untitled")).Split(Path.GetInvalidFileNameChars())).Trim();
        var dialog = new SaveFileDialog
        {
            FileName = name.Length > 0 ? name : "Notiz",
            DefaultExt = "." + kind,
            Filter = kind switch
            {
                "pdf" => Loc.T("notes.exportPdf") + "|*.pdf",
                "docx" => Loc.T("notes.exportDocx") + "|*.docx",
                _ => Loc.T("notes.exportTxt") + "|*.txt"
            }
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            switch (kind)
            {
                case "pdf": NoteExport.Pdf(dialog.FileName, NoteExport.PrintCopy(note.Title, note.Created, _body.Document)); break;
                case "docx": NoteExport.Word(dialog.FileName, NoteExport.PrintCopy(note.Title, note.Created, _body.Document)); break;
                default: NoteExport.Text(dialog.FileName, note.Title, _body.Document); break;
            }
            ShowStatus(Loc.F("notes.exported", Path.GetFileName(dialog.FileName)));
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.F("notes.exportFailed", ex.Message));
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void Print()
    {
        if (Current is not { } note) return;
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        var copy = NoteExport.PrintCopy(note.Title, note.Created, _body.Document);
        copy.PageWidth = dialog.PrintableAreaWidth;
        copy.PageHeight = dialog.PrintableAreaHeight;
        dialog.PrintDocument(((IDocumentPaginatorSource)copy).DocumentPaginator, note.Title);
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visibility = Visibility.Visible;
        _status.Opacity = 0;
        Motion.To(_status, OpacityProperty, 1, 200);
    }
}

internal static class ObjectExtensions
{
    /// <summary>Runs a small setup step on an object and returns it.</summary>
    public static T Let<T>(this T value, Action<T> setup)
    {
        setup(value);
        return value;
    }
}
