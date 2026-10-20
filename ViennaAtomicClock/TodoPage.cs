using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ViennaAtomicClock;

internal sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Note { get; set; } = "";
    public string ListId { get; set; } = TodoStore.DefaultListId;
    public DateTime? Due { get; set; }
    public bool Important { get; set; }
    public bool Done { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? Completed { get; set; }
}

internal sealed class TodoList
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
}

internal sealed class TodoStore
{
    public const string DefaultListId = "tasks";
    public List<TodoList> Lists { get; set; } = new();
    public List<TodoItem> Items { get; set; } = new();

    public static TodoStore Load()
    {
        var store = DataFile.Load<TodoStore>("todos.json");
        if (store.Lists.Count == 0)
        {
            store.Lists.Add(new TodoList { Id = DefaultListId, Name = "Aufgaben" });
            store.Lists.Add(new TodoList { Id = "school", Name = "Schule" });
            store.Lists.Add(new TodoList { Id = "private", Name = "Privat" });
        }
        return store;
    }

    public void Save() => DataFile.Save("todos.json", this);
}

/// <summary>
/// Todo page in the style of Microsoft To Do: smart lists (Mein Tag, Wichtig, Geplant), own lists,
/// due dates, stars, a collapsible "Erledigt" section and a details pane with notes.
/// </summary>
internal sealed class TodoPage : Grid
{
    private static CultureInfo Culture => Loc.Culture;

    private static readonly (string Id, string Icon, string Name)[] SmartLists =
    {
        ("myday", "☀", "todo.myday"), ("important", "★", "todo.important"), ("planned", "▦", "todo.planned"),
        ("all", "≡", "todo.all"), ("done", "✓", "todo.done")
    };

    private readonly TodoStore _store = TodoStore.Load();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string _view = "myday";
    private Guid? _selected;
    private DateTime? _newDue = DateTime.Today;
    private bool _showDone;
    private bool _narrow;

    private readonly StackPanel _lists = new();
    private readonly WrapPanel _listChips = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _title = Ui.Text("", 24, title: true);
    private readonly TextBlock _subtitle = Ui.Text("", 12.5, "TextSecondary");
    private readonly StackPanel _dueChips = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _tasks = new();
    private readonly StackPanel _details = new();
    private readonly TextBox _addBox;
    private readonly Button _deleteListButton;
    private readonly Border _listsCard;
    private readonly Border _tasksCard;
    private readonly Border _detailsCard;

    public event Action? Changed;

    public TodoPage()
    {
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _store.Save(); };

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star), MinWidth = 210 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 250 });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });

        // Lists column.
        var listsPanel = new DockPanel();
        var listsTitle = Ui.Text("", 18, title: true);
        listsTitle.SetResourceReference(TextBlock.TextProperty, "L.todo.lists");
        listsTitle.Margin = new Thickness(4, 0, 0, 12);
        DockPanel.SetDock(listsTitle, Dock.Top);
        listsPanel.Children.Add(listsTitle);
        var (newListHost, newListBox) = Ui.Input("", resourceKey: "L.todo.newList");
        newListBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(newListBox.Text)) return;
            var list = new TodoList { Name = newListBox.Text.Trim() };
            _store.Lists.Add(list);
            newListBox.Clear();
            _store.Save();
            SetView("list:" + list.Id);
        };
        newListHost.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(newListHost, Dock.Bottom);
        listsPanel.Children.Add(newListHost);
        listsPanel.Children.Add(new ScrollViewer { Content = _lists, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        _listsCard = Ui.Card(listsPanel, 16);
        Children.Add(_listsCard);

        // Tasks column.
        var tasksPanel = new DockPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(_title);
        _subtitle.Margin = new Thickness(0, 4, 0, 0);
        titles.Children.Add(_subtitle);
        header.Children.Add(titles);
        _deleteListButton = Ui.Button("ChipButton", "", (_, _) => DeleteCurrentList());
        _deleteListButton.SetResourceReference(ContentControl.ContentProperty, "L.todo.deleteList");
        _deleteListButton.Margin = new Thickness(0);
        _deleteListButton.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(_deleteListButton, 1);
        header.Children.Add(_deleteListButton);

        var top = new StackPanel();
        top.Children.Add(_listChips);
        top.Children.Add(header);

        var addRow = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition());
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var (addHost, addBox) = Ui.Input("", resourceKey: "L.todo.addHint");
        _addBox = addBox;
        _addBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddTask(); };
        addRow.Children.Add(addHost);
        var addButton = Ui.Button("ActionButton", "", (_, _) => AddTask());
        addButton.SetResourceReference(ContentControl.ContentProperty, "L.todo.add");
        addButton.Height = 42;
        addButton.Padding = new Thickness(18, 0, 18, 0);
        addButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(addButton, 1);
        addRow.Children.Add(addButton);
        top.Children.Add(addRow);
        top.Children.Add(_dueChips);
        top.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(top, Dock.Top);
        tasksPanel.Children.Add(top);
        tasksPanel.Children.Add(new ScrollViewer { Content = _tasks, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Margin = new Thickness(0, 0, -6, 0) });
        _tasksCard = Ui.Card(tasksPanel, 20);
        Grid.SetColumn(_tasksCard, 2);
        Children.Add(_tasksCard);

        // Details column.
        _detailsCard = Ui.Card(new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false }, 20);
        Grid.SetColumn(_detailsCard, 4);
        Children.Add(_detailsCard);

        Refresh();
        Loc.Changed += Refresh;
    }

    public int OpenCount => _store.Items.Count(i => !i.Done);
    public int TodayCount => _store.Items.Count(i => !i.Done && i.Due is { } d && d.Date <= DateTime.Today);

    public void Flush()
    {
        _saveTimer.Stop();
        _store.Save();
    }

    /// <summary>Three columns on wide windows; on narrow ones lists become chips and details move below.</summary>
    public void ApplyLayout(double width)
    {
        _narrow = width < 1000;
        ColumnDefinitions[0].MinWidth = _narrow ? 0 : 210;
        ColumnDefinitions[0].Width = _narrow ? new GridLength(0) : new GridLength(0.75, GridUnitType.Star);
        ColumnDefinitions[1].Width = new GridLength(_narrow ? 0 : 14);
        ColumnDefinitions[3].Width = new GridLength(_narrow ? 0 : 14);
        ColumnDefinitions[4].MinWidth = _narrow ? 0 : 250;
        ColumnDefinitions[4].Width = _narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _listsCard.Visibility = _narrow ? Visibility.Collapsed : Visibility.Visible;
        _listChips.Visibility = _narrow ? Visibility.Visible : Visibility.Collapsed;

        var showDetailsBelow = _narrow && _selected is not null;
        RowDefinitions[1].Height = new GridLength(showDetailsBelow ? 14 : 0);
        RowDefinitions[2].Height = showDetailsBelow ? new GridLength(0.9, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(_detailsCard, _narrow ? 2 : 4);
        Grid.SetRow(_detailsCard, _narrow ? 2 : 0);
        _detailsCard.Visibility = !_narrow || showDetailsBelow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        _store.Save();
        Refresh();
    }

    /// <summary>Saves and updates the lists without rebuilding the details pane (keeps focus and pending clicks).</summary>
    private void SaveQuiet()
    {
        _saveTimer.Stop();
        _store.Save();
        BuildLists();
        BuildHeader();
        BuildTasks();
        Changed?.Invoke();
    }

    private void SetView(string view)
    {
        _view = view;
        _selected = null;
        _newDue = view is "myday" or "planned" ? DateTime.Today : null;
        Refresh();
    }

    private void SaveLater()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke();
    }

    private string ListName(string id) => DisplayName(_store.Lists.FirstOrDefault(l => l.Id == id));

    /// <summary>Built-in lists keep their German default name in the file but are shown in the UI language.</summary>
    private static string DisplayName(TodoList? list) => list switch
    {
        null => Loc.T("todo.listTasks"),
        { Id: TodoStore.DefaultListId, Name: "Aufgaben" } => Loc.T("todo.listTasks"),
        { Id: "school", Name: "Schule" } => Loc.T("todo.listSchool"),
        { Id: "private", Name: "Privat" } => Loc.T("todo.listPrivate"),
        _ => list.Name
    };

    private IEnumerable<TodoItem> ItemsFor(string view)
    {
        var today = DateTime.Today;
        return view switch
        {
            "myday" => _store.Items.Where(i => i.Due is { } d && (d.Date == today || (!i.Done && d.Date < today))),
            "important" => _store.Items.Where(i => i.Important),
            "planned" => _store.Items.Where(i => i.Due is not null),
            "all" => _store.Items.Where(i => !i.Done),
            "done" => _store.Items.Where(i => i.Done),
            _ => _store.Items.Where(i => "list:" + i.ListId == view)
        };
    }

    private void Refresh()
    {
        BuildLists();
        BuildHeader();
        BuildTasks();
        BuildDetails();
        ApplyLayout(ActualWidth > 0 ? ActualWidth : 1200);
        Changed?.Invoke();
    }

    private void BuildLists()
    {
        _lists.Children.Clear();
        _listChips.Children.Clear();

        void Add(string view, string icon, string name)
        {
            var count = ItemsFor(view).Count(i => !i.Done || view == "done");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Ui.Text(icon, 14, "AccentBrush"));
            var label = Ui.Text(name, 13.5, bold: view == _view);
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);
            if (count > 0)
            {
                var badge = Ui.Text(count.ToString(Culture), 12, "TextSecondary");
                Grid.SetColumn(badge, 2);
                grid.Children.Add(badge);
            }
            _lists.Children.Add(Ui.Row(grid, () => SetView(view), view == _view));

            var chip = Ui.Button("ChipButton", $"{icon} {name}", (_, _) => SetView(view));
            chip.Margin = new Thickness(0, 0, 6, 6);
            Ui.Select(chip, view == _view);
            _listChips.Children.Add(chip);
        }

        foreach (var (id, icon, key) in SmartLists) Add(id, icon, Loc.T(key));
        _lists.Children.Add(Ui.Divider());
        foreach (var list in _store.Lists) Add("list:" + list.Id, "☰", DisplayName(list));
    }

    private void BuildHeader()
    {
        var smart = SmartLists.FirstOrDefault(s => s.Id == _view);
        _title.Text = smart.Id is not null ? Loc.T(smart.Name).ToUpper(Culture) : ListName(_view[5..]).ToUpper(Culture);
        var open = ItemsFor(_view).Count(i => !i.Done);
        _subtitle.Text = _view == "myday"
            ? $"{DateTime.Today.ToString("dddd", Culture)}, {DateTime.Today.ToString("M", Culture)} · {Loc.F("todo.open", open)}"
            : _view == "done" ? Loc.F("todo.doneCount", ItemsFor(_view).Count()) : Loc.F("todo.open", open);
        _deleteListButton.Visibility = _view.StartsWith("list:") && _view != "list:" + TodoStore.DefaultListId ? Visibility.Visible : Visibility.Collapsed;

        _dueChips.Children.Clear();
        _dueChips.Children.Add(Ui.Text(Loc.T("todo.due"), 11.5, "TextMuted"));
        ((TextBlock)_dueChips.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        ((TextBlock)_dueChips.Children[0]).Margin = new Thickness(2, 0, 8, 0);
        foreach (var (label, date) in DueOptions())
        {
            var chip = Ui.Button("ChipButton", label, (_, _) => { _newDue = date; BuildHeader(); });
            chip.Height = 28;
            Ui.Select(chip, _newDue?.Date == date?.Date);
            _dueChips.Children.Add(chip);
        }
    }

    private static IEnumerable<(string Label, DateTime? Date)> DueOptions()
    {
        var today = DateTime.Today;
        var nextMonday = today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7 is 0 ? 7 : ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7);
        yield return (Loc.T("today"), today);
        yield return (Loc.T("tomorrow"), today.AddDays(1));
        yield return (Loc.T("todo.nextWeek"), nextMonday);
        yield return (Loc.T("todo.none"), null);
    }

    private void AddTask()
    {
        var title = _addBox.Text.Trim();
        if (title.Length == 0) return;
        var item = new TodoItem
        {
            Title = title,
            Due = _view == "myday" && _newDue is null ? DateTime.Today : _newDue,
            Important = _view == "important",
            ListId = _view.StartsWith("list:") ? _view[5..] : TodoStore.DefaultListId
        };
        _store.Items.Add(item);
        _addBox.Clear();
        SaveNow();
        _addBox.Focus();
    }

    private void BuildTasks()
    {
        _tasks.Children.Clear();
        var items = ItemsFor(_view).ToList();
        var open = items.Where(i => !i.Done)
            .OrderBy(i => i.Due ?? DateTime.MaxValue).ThenByDescending(i => i.Important).ThenBy(i => i.Created).ToList();
        var done = items.Where(i => i.Done).OrderByDescending(i => i.Completed).ToList();

        if (open.Count == 0 && done.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 40, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            var icon = Ui.Text(_view == "myday" ? "☀" : "✓", 40, "AccentBrush");
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(icon);
            var hint = Ui.Text(Loc.T(_view == "myday" ? "todo.emptyMyDay" : "todo.empty"), 13, "TextSecondary");
            hint.Margin = new Thickness(0, 10, 0, 0);
            empty.Children.Add(hint);
            _tasks.Children.Add(empty);
            return;
        }

        foreach (var item in open) _tasks.Children.Add(TaskRow(item));

        if (done.Count > 0 && _view != "done")
        {
            var toggle = Ui.Button("ChipButton", $"{(_showDone ? "▾" : "▸")}  {Loc.F("todo.doneToggle", done.Count)}", (_, _) => { _showDone = !_showDone; BuildTasks(); });
            toggle.Margin = new Thickness(0, 10, 0, 8);
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            _tasks.Children.Add(toggle);
        }
        if (_showDone || _view == "done")
            foreach (var item in done) _tasks.Children.Add(TaskRow(item));
    }

    private UIElement TaskRow(TodoItem item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = Ui.Button("IconButton", item.Done ? "✔" : "○", (_, _) => ToggleDone(item), Loc.T(item.Done ? "todo.reopen" : "todo.complete"));
        check.Width = 32;
        check.Height = 32;
        check.Margin = new Thickness(0);
        check.FontSize = item.Done ? 15 : 22;
        check.SetResourceReference(Control.ForegroundProperty, item.Done ? "GreenBrush" : "TextSecondary");
        grid.Children.Add(check);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
        var title = Ui.Text(item.Title, 14.5, item.Done ? "TextMuted" : "TextPrimary", bold: !item.Done);
        if (item.Done) title.TextDecorations = TextDecorations.Strikethrough;
        texts.Children.Add(title);

        var meta = new List<string>();
        if (!_view.StartsWith("list:")) meta.Add(ListName(item.ListId));
        if (item.Due is { } due) meta.Add(DueLabel(due));
        if (item.Note.Length > 0) meta.Add(Loc.T("todo.noteTag"));
        if (meta.Count > 0)
        {
            var overdue = !item.Done && item.Due is { } d && d.Date < DateTime.Today;
            var today = !item.Done && item.Due?.Date == DateTime.Today;
            var metaText = Ui.Text(string.Join("  ·  ", meta), 11.5, overdue ? "OrangeBrush" : today ? "AccentBrush" : "TextSecondary");
            metaText.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(metaText);
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var star = Ui.Button("IconButton", item.Important ? "★" : "☆", (_, _) => { item.Important = !item.Important; SaveNow(); }, Loc.T("todo.important"));
        star.Width = 32;
        star.Height = 32;
        star.Margin = new Thickness(0);
        star.SetResourceReference(Control.ForegroundProperty, item.Important ? "OrangeBrush" : "TextMuted");
        Grid.SetColumn(star, 2);
        grid.Children.Add(star);

        return Ui.Row(grid, () => { _selected = item.Id; Refresh(); }, _selected == item.Id);
    }

    private static string DueLabel(DateTime due)
    {
        var days = (due.Date - DateTime.Today).Days;
        return days switch
        {
            0 => Loc.T("today"),
            1 => Loc.T("tomorrow"),
            -1 => Loc.T("yesterday"),
            < 0 => Loc.F("todo.overdue", due.ToString("M", Culture)),
            < 7 => due.ToString("dddd", Culture),
            _ => $"{due.ToString("ddd", Culture)}, {due.ToString("M", Culture)}"
        };
    }

    private void ToggleDone(TodoItem item)
    {
        item.Done = !item.Done;
        item.Completed = item.Done ? DateTime.Now : null;
        SaveNow();
    }

    private void DeleteCurrentList()
    {
        var id = _view[5..];
        if (id == TodoStore.DefaultListId) return;
        var name = ListName(id);
        var count = _store.Items.Count(i => i.ListId == id);
        var answer = MessageBox.Show(
            count > 0 ? Loc.F("todo.deleteListMove", name, count, Loc.T("todo.listTasks")) : Loc.F("todo.deleteListQ", name),
            Loc.T("todo.deleteList"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        foreach (var item in _store.Items.Where(i => i.ListId == id)) item.ListId = TodoStore.DefaultListId;
        _store.Lists.RemoveAll(l => l.Id == id);
        _view = "list:" + TodoStore.DefaultListId;
        SaveNow();
    }

    private void BuildDetails()
    {
        _details.Children.Clear();
        var item = _store.Items.FirstOrDefault(i => i.Id == _selected);
        if (item is null)
        {
            _selected = null;
            var empty = new StackPanel { Margin = new Thickness(0, 60, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            var icon = Ui.Text("☰", 38, "AccentBrush");
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(icon);
            var hint = Ui.Text(Loc.T("todo.pick"), 13, "TextSecondary");
            hint.TextAlignment = TextAlignment.Center;
            hint.Margin = new Thickness(0, 10, 0, 0);
            empty.Children.Add(hint);
            _details.Children.Add(empty);
            return;
        }

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Ui.Text(Loc.T("todo.details"), 16, title: true));
        var close = Ui.Button("IconButton", "×", (_, _) => { _selected = null; Refresh(); }, Loc.T("close"));
        close.Margin = new Thickness(0);
        close.Width = 30;
        close.Height = 30;
        close.FontSize = 20;
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        _details.Children.Add(head);

        var (titleHost, titleBox) = Ui.Input(Loc.T("todo.title"), fontSize: 15);
        titleBox.Text = item.Title;
        titleBox.FontWeight = FontWeights.Bold;
        titleBox.TextChanged += (_, _) => { if (titleBox.Text.Trim().Length > 0) { item.Title = titleBox.Text.Trim(); SaveLater(); } };
        titleBox.LostFocus += (_, _) => SaveQuiet();
        titleHost.Margin = new Thickness(0, 12, 0, 10);
        _details.Children.Add(titleHost);

        var toggles = new WrapPanel();
        var doneChip = Ui.Button("ChipButton", Loc.T(item.Done ? "todo.stateDone" : "todo.stateOpen"), (_, _) => ToggleDone(item));
        Ui.Select(doneChip, item.Done);
        var starChip = Ui.Button("ChipButton", (item.Important ? "★ " : "☆ ") + Loc.T("todo.important"), (_, _) => { item.Important = !item.Important; SaveNow(); });
        Ui.Select(starChip, item.Important);
        toggles.Children.Add(doneChip);
        toggles.Children.Add(starChip);
        _details.Children.Add(toggles);

        _details.Children.Add(Label(item.Due is { } due ? Loc.F("todo.dueValue", DueLabel(due)) : Loc.T("todo.dueLabel")));
        var dueChips = new WrapPanel();
        foreach (var (label, date) in DueOptions())
        {
            var chip = Ui.Button("ChipButton", label, (_, _) => { item.Due = date; SaveNow(); });
            chip.Margin = new Thickness(0, 0, 6, 6);
            Ui.Select(chip, item.Due?.Date == date?.Date);
            dueChips.Children.Add(chip);
        }
        _details.Children.Add(dueChips);

        _details.Children.Add(Label(Loc.T("todo.list")));
        var listChips = new WrapPanel();
        foreach (var list in _store.Lists)
        {
            var chip = Ui.Button("ChipButton", DisplayName(list), (_, _) => { item.ListId = list.Id; SaveNow(); });
            chip.Margin = new Thickness(0, 0, 6, 6);
            Ui.Select(chip, item.ListId == list.Id);
            listChips.Children.Add(chip);
        }
        _details.Children.Add(listChips);

        _details.Children.Add(Label(Loc.T("todo.note")));
        var (noteHost, noteBox) = Ui.Input(Loc.T("todo.noteHint"), multiline: true);
        noteBox.Text = item.Note;
        noteBox.MinHeight = 120;
        noteBox.TextChanged += (_, _) => { item.Note = noteBox.Text; SaveLater(); };
        noteBox.LostFocus += (_, _) => SaveQuiet();
        _details.Children.Add(noteHost);

        var info = Ui.Text(item.Done && item.Completed is { } c
            ? Loc.F("todo.completedAt", c.ToString("g", Culture))
            : Loc.F("todo.createdAt", item.Created.ToString("g", Culture)), 11, "TextMuted");
        info.Margin = new Thickness(0, 14, 0, 8);
        _details.Children.Add(info);

        var delete = Ui.Button("SecondaryButton", Loc.T("todo.deleteTask"), (_, _) =>
        {
            _store.Items.Remove(item);
            _selected = null;
            SaveNow();
        });
        delete.Height = 40;
        delete.SetResourceReference(Control.ForegroundProperty, "OrangeBrush");
        _details.Children.Add(delete);
    }

    private static TextBlock Label(string text)
    {
        var label = Ui.Text(text, 11.5, "TextMuted");
        label.Margin = new Thickness(0, 14, 0, 6);
        return label;
    }
}
