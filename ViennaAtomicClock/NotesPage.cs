using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ViennaAtomicClock;

internal sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int Color { get; set; }
    public bool Pinned { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
}

internal sealed class NoteStore
{
    public List<Note> Notes { get; set; } = new();
    public bool Initialized { get; set; }

    public static NoteStore Load()
    {
        var store = DataFile.Load<NoteStore>("notes.json");
        if (!store.Initialized)
        {
            store.Initialized = true;
            store.Notes.Add(new Note
            {
                Title = "Willkommen bei Notizen",
                Body = "Alles wird automatisch gespeichert.\n\n" +
                       "☐ Strg+N legt eine neue Notiz an\n" +
                       "☐ Strg+L macht aus einer Zeile einen Punkt zum Abhaken, Strg+D hakt ihn ab\n" +
                       "☐ Mit 📌 bleibt eine Notiz oben, die Farbpunkte markieren sie\n" +
                       "☐ Oben links kannst du alle Notizen durchsuchen",
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
/// Notes page in the style of Apple Notes and Google Keep: searchable list with pinned notes and
/// colour tags on the left, a distraction-free editor with checklists and autosave on the right.
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

    private readonly NoteStore _store = NoteStore.Load();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private Guid? _selected;
    private string _search = "";
    private bool _loadingEditor;

    private readonly StackPanel _list = new();
    private readonly TextBlock _count = Ui.Text("", 12, "TextSecondary");
    private readonly Border _listCard;
    private readonly Border _editorCard;
    private readonly Grid _editor = new();
    private readonly StackPanel _empty = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBox _titleBox = PlainBox(26, bold: true);
    private readonly TextBox _bodyBox = PlainBox(15, bold: false, multiline: true);
    private readonly TextBlock _meta = Ui.Text("", 11.5, "TextMuted");
    private readonly Button _pinButton;
    private readonly StackPanel _colorDots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    public event Action? Changed;

    public NotesPage()
    {
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _store.Save(); BuildList(); };

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star), MinWidth = 260 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.8, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });

        // Note list.
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
        add.Padding = new Thickness(16, 0, 16, 0);
        add.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        DockPanel.SetDock(header, Dock.Top);
        listPanel.Children.Add(header);

        var (searchHost, searchBox) = Ui.Input("", resourceKey: "L.notes.search");
        searchBox.TextChanged += (_, _) => { _search = searchBox.Text.Trim(); BuildList(); };
        searchHost.Margin = new Thickness(0, 14, 0, 12);
        DockPanel.SetDock(searchHost, Dock.Top);
        listPanel.Children.Add(searchHost);
        listPanel.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Margin = new Thickness(0, 0, -6, 0) });
        _listCard = Ui.Card(listPanel, 18);
        Children.Add(_listCard);

        // Editor.
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        _pinButton = Ui.Button("ChipButton", "", (_, _) => TogglePin());
        _pinButton.SetResourceReference(ToolTipProperty, "L.notes.pinTip");
        tools.Children.Add(_pinButton);
        var check = Ui.Button("ChipButton", "", (_, _) => ToggleChecklistLine());
        check.SetResourceReference(ContentControl.ContentProperty, "L.notes.checklist");
        check.SetResourceReference(ToolTipProperty, "L.notes.checklistTip");
        tools.Children.Add(check);
        tools.Children.Add(_colorDots);
        toolbar.Children.Add(tools);
        var delete = Ui.Button("IconButton", "🗑", (_, _) => DeleteNote());
        delete.SetResourceReference(ToolTipProperty, "L.notes.deleteTip");
        delete.SetResourceReference(Control.ForegroundProperty, "OrangeBrush");
        Grid.SetColumn(delete, 2);
        toolbar.Children.Add(delete);

        _editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _editor.Children.Add(toolbar);
        Grid.SetRow(_titleBox, 1);
        _editor.Children.Add(_titleBox);
        _meta.Margin = new Thickness(2, 2, 0, 12);
        Grid.SetRow(_meta, 2);
        _editor.Children.Add(_meta);
        Grid.SetRow(_bodyBox, 3);
        _editor.Children.Add(_bodyBox);

        _titleBox.TextChanged += (_, _) => Edited(n => n.Title = _titleBox.Text);
        _bodyBox.TextChanged += (_, _) => Edited(n => n.Body = _bodyBox.Text);
        _titleBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _bodyBox.Focus(); _bodyBox.CaretIndex = 0; e.Handled = true; } };

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
        _editorCard = Ui.Card(editorHost, 24);
        Grid.SetColumn(_editorCard, 2);
        Children.Add(_editorCard);

        PreviewKeyDown += OnKeys;
        _selected = Sorted().FirstOrDefault()?.Id;
        BuildList();
        LoadEditor();
        Loc.Changed += () => { BuildList(); if (Current is { } note) { UpdateMeta(note); UpdateToolbar(note); } };
    }

    public int Count => _store.Notes.Count;

    public void Flush()
    {
        _saveTimer.Stop();
        _store.Save();
    }

    /// <summary>List beside the editor; on narrow windows the list sits above it.</summary>
    public void ApplyLayout(double width)
    {
        var narrow = width < 900;
        ColumnDefinitions[0].MinWidth = narrow ? 0 : 260;
        ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0.9, GridUnitType.Star);
        ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 14);
        ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1.8, GridUnitType.Star);
        RowDefinitions[0].Height = narrow ? new GridLength(0.8, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        RowDefinitions[1].Height = new GridLength(narrow ? 14 : 0);
        RowDefinitions[2].Height = narrow ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(_editorCard, narrow ? 0 : 2);
        Grid.SetRow(_editorCard, narrow ? 2 : 0);
    }

    private static TextBox PlainBox(double size, bool bold, bool multiline = false)
    {
        var box = new TextBox
        {
            FontSize = size,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = multiline,
            AcceptsTab = multiline,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        if (bold) box.FontWeight = FontWeights.Bold;
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        box.SetResourceReference(TextBox.CaretBrushProperty, "AccentBrush");
        box.SetResourceReference(TextBox.SelectionBrushProperty, "AccentBrush");
        box.SetResourceReference(Control.FontFamilyProperty, bold ? "StencilFont" : "UiFont");
        return box;
    }

    private IEnumerable<Note> Sorted() => _store.Notes.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Updated);

    private Note? Current => _store.Notes.FirstOrDefault(n => n.Id == _selected);

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.N) { NewNote(); e.Handled = true; }
        else if (e.Key == Key.L) { ToggleChecklistLine(); e.Handled = true; }
        else if (e.Key == Key.D) { CheckLine(); e.Handled = true; }
    }

    private void Edited(Action<Note> change)
    {
        if (_loadingEditor || Current is not { } note) return;
        change(note);
        note.Updated = DateTime.Now;
        UpdateMeta(note);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void NewNote()
    {
        var note = new Note();
        _store.Notes.Add(note);
        _selected = note.Id;
        _search = "";
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
        _store.Notes.Remove(note);
        _selected = Sorted().FirstOrDefault()?.Id;
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

    /// <summary>Adds or removes a "☐ " box at the start of the line under the caret.</summary>
    private void ToggleChecklistLine()
    {
        if (Current is null) return;
        var (start, line) = CaretLine();
        var caret = _bodyBox.CaretIndex;
        if (line.StartsWith("☐ ") || line.StartsWith("☑ "))
        {
            _bodyBox.Text = _bodyBox.Text.Remove(start, 2);
            _bodyBox.CaretIndex = Math.Max(start, caret - 2);
        }
        else
        {
            _bodyBox.Text = _bodyBox.Text.Insert(start, "☐ ");
            _bodyBox.CaretIndex = caret + 2;
        }
        _bodyBox.Focus();
    }

    /// <summary>Ticks or unticks the checklist box on the caret line.</summary>
    private void CheckLine()
    {
        var (start, line) = CaretLine();
        if (line.Length < 2) return;
        var caret = _bodyBox.CaretIndex;
        var mark = line[0] == '☐' ? "☑" : line[0] == '☑' ? "☐" : null;
        if (mark is null) return;
        _bodyBox.Text = _bodyBox.Text.Remove(start, 1).Insert(start, mark);
        _bodyBox.CaretIndex = caret;
    }

    private (int Start, string Line) CaretLine()
    {
        var text = _bodyBox.Text;
        var caret = Math.Min(_bodyBox.CaretIndex, text.Length);
        var start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var end = text.IndexOf('\n', caret);
        return (start, text[start..(end < 0 ? text.Length : end)]);
    }

    private void BuildList()
    {
        _list.Children.Clear();
        var notes = Sorted().Where(Matches).ToList();
        _count.Text = _search.Length > 0 ? Loc.F("notes.hits", notes.Count) : _store.Notes.Count == 1 ? Loc.T("notes.one") : Loc.F("notes.n", _store.Notes.Count);

        if (notes.Count == 0)
        {
            var none = Ui.Text(Loc.T(_search.Length > 0 ? "notes.nothingFound" : "notes.none"), 13, "TextSecondary");
            none.Margin = new Thickness(4, 20, 0, 0);
            _list.Children.Add(none);
            return;
        }

        var pinned = notes.Where(n => n.Pinned).ToList();
        if (pinned.Count > 0) _list.Children.Add(Section(Loc.T("notes.pinnedSection")));
        foreach (var note in pinned) _list.Children.Add(NoteRow(note));
        var others = notes.Where(n => !n.Pinned).ToList();
        if (pinned.Count > 0 && others.Count > 0) _list.Children.Add(Section(Loc.T("notes.section")));
        foreach (var note in others) _list.Children.Add(NoteRow(note));
    }

    private bool Matches(Note note) =>
        _search.Length == 0 ||
        note.Title.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ||
        note.Body.Contains(_search, StringComparison.CurrentCultureIgnoreCase);

    private static TextBlock Section(string text)
    {
        var label = Ui.Text(text, 10.5, "TextMuted", bold: true);
        label.Margin = new Thickness(4, 6, 0, 6);
        return label;
    }

    private UIElement NoteRow(Note note)
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

        var preview = string.Join(" ", note.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(3)).Trim();
        var previewText = Ui.Text(preview.Length > 0 ? preview : Loc.T("notes.noText"), 12, "TextSecondary");
        previewText.TextTrimming = TextTrimming.CharacterEllipsis;
        previewText.TextWrapping = TextWrapping.Wrap;
        previewText.MaxHeight = 36;
        previewText.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(previewText);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        return Ui.Row(grid, () => { _selected = note.Id; BuildList(); LoadEditor(); }, note.Id == _selected);
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
        if (note is null) return;

        _loadingEditor = true;
        _titleBox.Text = note.Title;
        _bodyBox.Text = note.Body;
        _loadingEditor = false;
        UpdateMeta(note);
        UpdateToolbar(note);
    }

    private void UpdateMeta(Note note)
    {
        var words = note.Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var open = note.Body.Split('\n').Count(l => l.StartsWith("☐"));
        var done = note.Body.Split('\n').Count(l => l.StartsWith("☑"));
        var edited = note.Updated.Date == DateTime.Today ? Loc.F("notes.todayAt", note.Updated.ToString("HH:mm")) : note.Updated.ToString("g", Culture);
        _meta.Text = $"{Loc.F("notes.edited", edited)}  ·  {words} {Loc.T(words == 1 ? "notes.word" : "notes.words")}" + (open + done > 0 ? "  ·  " + Loc.F("notes.checked", done, open + done) : "");
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
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand,
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
}
