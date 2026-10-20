using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ViennaAtomicClock.GlbViewer;

internal sealed partial class GlbViewerPage
{
    private static readonly FontFamily SymbolFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly Border _viewportHost = new() { ClipToBounds = true };
    private Border _toolbar = null!;
    private Border _status = null!;
    private Border _sceneCard = null!;
    private Border _detailsCard = null!;
    private Border _animationBar = null!;
    private readonly TextBlock _fileTitle = Ui.Text("", 15, bold: true);
    private readonly TextBlock _fileSubtitle = Ui.Text("", 11.5, "TextMuted");
    private Button _fullscreenButton = null!;
    private Button _sceneToggle = null!;
    private Button _detailsToggle = null!;
    private Button _transparentToggle = null!;
    private readonly List<(Button Button, TextBlock Label, string Key)> _labelled = new();
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly StackPanel _propertiesHost = new();
    private readonly StackPanel _displayHost = new();
    private readonly StackPanel _infoHost = new();
    private readonly StackPanel _animationHost = new();
    private readonly TextBlock _statusText = Ui.Text("", 12, "TextSecondary");
    private readonly TextBlock _statsText = Ui.Text("", 11.5, "TextMuted");
    private readonly Grid _progressTrack = new() { Height = 6, Width = 180, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _progressFill = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3) };
    private Button _cancelButton = null!;
    private Grid _main = null!;
    private readonly TextBlock _sceneTitle = Ui.Text("", 14, bold: true, title: true);
    private Slider? _timeline;
    private TextBlock? _timeText;
    private Button? _playButton;
    private bool _updatingTimeline;
    private DateTime _lastSeek;

    // ───────────────────────── layout ─────────────────────────

    private void BuildLayout()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _toolbar = BuildToolbar();
        Children.Add(_toolbar);

        _main = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });

        _sceneCard = BuildSceneCard();
        _main.Children.Add(_sceneCard);
        var viewport = new Border { BorderThickness = new Thickness(1), Child = _viewportHost };
        viewport.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        viewport.Background = new SolidColorBrush(Color.FromRgb(20, 22, 27));
        AutomationProperties.SetName(viewport, Loc.T("glb.viewport"));
        SetColumn(viewport, 2);
        _main.Children.Add(viewport);
        _detailsCard = BuildDetailsCard();
        SetColumn(_detailsCard, 4);
        _main.Children.Add(_detailsCard);
        SetRow(_main, 1);
        Children.Add(_main);

        _animationBar = Ui.Card(_animationHost, 10);
        _animationBar.Margin = new Thickness(0, 10, 0, 0);
        _animationBar.Visibility = Visibility.Collapsed;
        SetRow(_animationBar, 2);
        Children.Add(_animationBar);

        _status = BuildStatusBar();
        SetRow(_status, 3);
        Children.Add(_status);

        SizeChanged += (_, _) => ApplySidebars();
        RebuildTexts();
        ShowProperties(null);
        RefreshDisplayPanel();
        RefreshInfoPanel();
        ShowStatus(Loc.T("glb.status.empty"));
    }

    private Button ToolButton(string glyph, string labelKey, string tipKey, Action click)
    {
        var label = Ui.Text(Loc.T(labelKey), 12.5);
        label.Margin = new Thickness(7, 0, 0, 0);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = SymbolFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(label);
        var button = Ui.Button("ChipButton", content, (_, _) => click(), Loc.T(tipKey));
        button.Height = 36;
        button.Margin = new Thickness(0, 0, 6, 0);
        button.Padding = new Thickness(11, 0, 12, 0);
        AutomationProperties.SetName(button, Loc.T(labelKey));
        _labelled.Add((button, label, labelKey));
        button.Tag = tipKey;
        return button;
    }

    private Button IconButton(string glyph, string tipKey, Action click)
    {
        var button = Ui.Button("ChipButton", new TextBlock { Text = glyph, FontFamily = SymbolFont, FontSize = 14 }, (_, _) => click(), Loc.T(tipKey));
        button.Width = 36;
        button.Height = 36;
        button.Margin = new Thickness(0, 0, 6, 0);
        AutomationProperties.SetName(button, Loc.T(tipKey));
        button.Tag = tipKey;
        return button;
    }

    private Border BuildToolbar()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 14, 0) };
        var icon = new TextBlock { Text = "", FontFamily = SymbolFont, FontSize = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        title.Children.Add(icon);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 320 };
        names.Children.Add(_fileTitle);
        names.Children.Add(_fileSubtitle);
        title.Children.Add(names);
        grid.Children.Add(title);

        var tools = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(ToolButton("", "glb.open", "glb.tip.open", ShowOpenDialog));
        tools.Children.Add(ToolButton("", "glb.reset", "glb.tip.reset", ResetView));
        tools.Children.Add(ToolButton("", "glb.center", "glb.tip.center", CenterModel));
        _fullscreenButton = ToolButton("", "glb.fullscreen", "glb.tip.fullscreen", () => FullscreenRequested?.Invoke());
        tools.Children.Add(_fullscreenButton);
        tools.Children.Add(IconButton("", "glb.tip.screenshot", () => Screenshot(false)));
        tools.Children.Add(IconButton("", "glb.tip.copy", () => Screenshot(true)));
        _transparentToggle = IconButton("", "glb.tip.transparent", () =>
        {
            _settings.TransparentScreenshot = !_settings.TransparentScreenshot;
            _settings.Save();
            Ui.Select(_transparentToggle, _settings.TransparentScreenshot);
            AutomationProperties.SetItemStatus(_transparentToggle, _settings.TransparentScreenshot ? Loc.T("wx.on") : Loc.T("wx.off"));
        });
        Ui.Select(_transparentToggle, _settings.TransparentScreenshot);
        tools.Children.Add(_transparentToggle);
        tools.Children.Add(IconButton("", "glb.tip.export", () => ExportInfo()));
        SetColumn(tools, 1);
        grid.Children.Add(tools);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _sceneToggle = IconButton("", "glb.tip.scenePanel", () => { _settings.SceneOpen = !_settings.SceneOpen; _settings.Save(); ApplySidebars(); });
        _detailsToggle = IconButton("", "glb.tip.detailsPanel", () => { _settings.DetailsOpen = !_settings.DetailsOpen; _settings.Save(); ApplySidebars(); });
        right.Children.Add(_sceneToggle);
        right.Children.Add(_detailsToggle);
        var close = IconButton("", "glb.tip.close", Close);
        close.Margin = new Thickness(6, 0, 0, 0);
        right.Children.Add(close);
        SetColumn(right, 2);
        grid.Children.Add(right);
        return Ui.Card(grid, 10);
    }

    private Border BuildSceneCard()
    {
        var dock = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var showAll = Ui.Button("ChipButton", "", (_, _) => ShowAll(), Loc.T("glb.tip.showAll"));
        showAll.Height = 30;
        showAll.Padding = new Thickness(10, 0, 10, 0);
        showAll.Tag = "glb.showAll";
        DockPanel.SetDock(showAll, Dock.Right);
        head.Children.Add(showAll);
        head.Children.Add(_sceneTitle);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        // Selection colours from the active theme instead of the system blue.
        _tree.Resources[SystemColors.HighlightBrushKey] = TryFindResource("AccentSoft") ?? Brushes.SteelBlue;
        _tree.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = TryFindResource("AccentSoft") ?? Brushes.SteelBlue;
        _tree.Resources[SystemColors.HighlightTextBrushKey] = TryFindResource("TextPrimary") ?? Brushes.White;
        _tree.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        VirtualizingPanel.SetIsVirtualizing(_tree, true);
        VirtualizingPanel.SetVirtualizationMode(_tree, VirtualizationMode.Recycling);
        _tree.SelectedItemChanged += (_, e) =>
        {
            if (_selectingFromViewer) return;
            if (e.NewValue is TreeViewItem { Tag: TreeNodeInfo node }) Select(node.Id);
        };
        _tree.MouseDoubleClick += (_, _) => { if (_tree.SelectedItem is TreeViewItem { Tag: TreeNodeInfo node }) Focus(node.Id); };
        AutomationProperties.SetName(_tree, Loc.T("glb.scene"));
        dock.Children.Add(_tree);
        return Ui.Card(dock, 12);
    }

    private Border BuildDetailsCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(_propertiesHost);
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(_displayHost);
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(_infoHost);
        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(0, 0, 4, 0) };
        return Ui.Card(scroll, 12);
    }

    private Border BuildStatusBar()
    {
        var grid = new Grid { Margin = new Thickness(2, 8, 2, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var track = new Border { CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        _progressFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _progressTrack.Children.Add(track);
        _progressTrack.Children.Add(_progressFill);
        _progressTrack.Visibility = Visibility.Collapsed;
        grid.Children.Add(_progressTrack);
        _cancelButton = Ui.Button("ChipButton", "", (_, _) => CancelLoad(), Loc.T("glb.tip.cancel"));
        _cancelButton.Height = 28;
        _cancelButton.Padding = new Thickness(10, 0, 10, 0);
        _cancelButton.Margin = new Thickness(0, 0, 10, 0);
        _cancelButton.Visibility = Visibility.Collapsed;
        _cancelButton.Tag = "glb.cancel";
        SetColumn(_cancelButton, 1);
        grid.Children.Add(_cancelButton);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_statusText, AutomationLiveSetting.Polite);
        SetColumn(_statusText, 2);
        grid.Children.Add(_statusText);
        _statsText.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(_statsText, 3);
        grid.Children.Add(_statsText);
        return new Border { Child = grid };
    }

    private UIElement RuntimeMissingPanel(string? detail = null)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460 };
        var title = Ui.Text(Loc.T("glb.err.runtimeTitle"), 18, bold: true);
        title.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(title);
        var text = Ui.Text(Loc.T("glb.err.runtime") + (detail is null ? "" : "\n\n" + detail), 13, "TextSecondary");
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        text.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(text);
        var install = Ui.Button("ActionButton", Loc.T("glb.runtime.install"), (_, _) =>
        {
            if (!WebView2Runtime.OpenDownloadPage())
                ShowStatus(Loc.T("glb.err.runtimeOpen"), warning: true);
        }, Loc.T("glb.runtime.installHint"));
        install.Margin = new Thickness(0, 18, 0, 0);
        install.HorizontalAlignment = HorizontalAlignment.Left;
        System.Windows.Automation.AutomationProperties.SetName(install, Loc.T("glb.runtime.install"));
        stack.Children.Add(install);
        return stack;
    }

    /// <summary>Sidebars follow the user's choice, but make room on narrow windows and in full screen.</summary>
    private void ApplySidebars()
    {
        var width = ActualWidth;
        var sceneOpen = _settings.SceneOpen && !_fullscreen && (width <= 0 || width >= 760);
        var detailsOpen = _settings.DetailsOpen && !_fullscreen && (width <= 0 || width >= 980 || (!sceneOpen && width >= 700));
        _sceneCard.Visibility = sceneOpen ? Visibility.Visible : Visibility.Collapsed;
        _detailsCard.Visibility = detailsOpen ? Visibility.Visible : Visibility.Collapsed;
        _main.ColumnDefinitions[0].Width = new GridLength(sceneOpen ? Math.Clamp(width * 0.2, 220, 300) : 0);
        _main.ColumnDefinitions[1].Width = new GridLength(sceneOpen ? 10 : 0);
        _main.ColumnDefinitions[3].Width = new GridLength(detailsOpen ? 10 : 0);
        _main.ColumnDefinitions[4].Width = new GridLength(detailsOpen ? Math.Clamp(width * 0.24, 270, 340) : 0);
        Ui.Select(_sceneToggle, _settings.SceneOpen);
        Ui.Select(_detailsToggle, _settings.DetailsOpen);
        // Short labels when space is tight.
        foreach (var (_, label, _) in _labelled) label.Visibility = width is > 0 and < 1180 ? Visibility.Collapsed : Visibility.Visible;
        _main.Margin = new Thickness(0, _fullscreen ? 0 : 10, 0, 0);
    }

    private void RebuildTexts()
    {
        foreach (var (button, label, key) in _labelled)
        {
            label.Text = Loc.T(key);
            AutomationProperties.SetName(button, Loc.T(key));
        }
        foreach (var button in FindButtons(this))
            if (button.Tag is string tip)
            {
                if (tip.StartsWith("glb.tip.", StringComparison.Ordinal)) button.ToolTip = Loc.T(tip);
                else
                {
                    button.Content = Loc.T(tip);
                    AutomationProperties.SetName(button, Loc.T(tip));
                }
            }
        _sceneTitle.Text = Loc.T("glb.scene");
        if (_inspection is null)
        {
            _fileTitle.Text = Loc.T("glb.title");
            _fileSubtitle.Text = Loc.T("glb.subtitle");
        }
        RefreshDisplayPanel();
        RefreshInfoPanel();
        RefreshAnimationBar();
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button) yield return button;
            foreach (var nested in FindButtons(child)) yield return nested;
        }
    }

    // ───────────────────────── status and loading ─────────────────────────

    private void ShowStatus(string text, bool warning = false)
    {
        _statusText.Text = text;
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, warning ? "OrangeBrush" : "TextSecondary");
    }

    private void UpdateLoading(double? loaded, double total)
    {
        var active = loaded is not null;
        _progressTrack.Visibility = _cancelButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) return;
        var share = total > 0 ? Math.Clamp(loaded!.Value / total, 0, 1) : 0;
        _progressFill.Width = share * _progressTrack.Width;
        ShowStatus(Loc.F("glb.status.loading", FormatBytes(loaded!.Value), FormatBytes(total)));
    }

    private void UpdateStats(JsonElement d)
    {
        var fps = d.GetProperty("fps").GetDouble();
        var parts = new List<string>();
        if (fps > 0) parts.Add(Loc.F("glb.stats.fps", fps.ToString("0", Loc.Culture)));
        if (d.TryGetProperty("calls", out var calls) && calls.ValueKind == JsonValueKind.Number) parts.Add(Loc.F("glb.stats.calls", calls.GetInt32()));
        if (d.TryGetProperty("pixelRatio", out var ratio) && ratio.ValueKind == JsonValueKind.Number) parts.Add("× " + ratio.GetDouble().ToString("0.##", Loc.Culture));
        _statsText.Text = string.Join("  ·  ", parts);
    }

    // ───────────────────────── scene tree ─────────────────────────

    private bool _selectingFromViewer;

    private sealed class TreeNodeInfo
    {
        public int Id;
        public string Name = "";
        public string Kind = "node";
        public bool Visible = true;
        public JsonElement Children;
        public Button? Eye;
        public TextBlock? Label;

        public void SetVisible(bool visible)
        {
            Visible = visible;
            if (Eye is not null)
            {
                ((TextBlock)Eye.Content).Text = visible ? "" : "";
                AutomationProperties.SetItemStatus(Eye, visible ? Loc.T("glb.visible") : Loc.T("glb.hidden"));
            }
            if (Label is not null) Label.Opacity = visible ? 1 : 0.45;
        }
    }

    private static string KindGlyph(string kind) => kind switch
    {
        "mesh" => "", "skinned" => "", "camera" => "", "light" => "", "bone" => "",
        "points" or "line" => "", _ => ""
    };

    private void BuildTree(JsonElement root)
    {
        _tree.Items.Clear();
        // The glTF scene root is shown as the top entry; deep trees load their children when expanded.
        var item = CreateItem(root, 0);
        _tree.Items.Add(item);
        item.IsExpanded = true;
    }

    private TreeViewItem CreateItem(JsonElement json, int depth)
    {
        var node = new TreeNodeInfo
        {
            Id = json.GetProperty("id").GetInt32(),
            Name = json.GetProperty("name").GetString() ?? "",
            Kind = json.GetProperty("kind").GetString() ?? "node",
            Visible = json.GetProperty("visible").GetBoolean(),
            Children = json.GetProperty("children").Clone()
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var eye = new Button
        {
            Content = new TextBlock { FontFamily = SymbolFont, FontSize = 12 }, Width = 24, Height = 22, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Loc.T("glb.tip.visibility"), Focusable = true
        };
        eye.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
        eye.Click += (_, e) =>
        {
            node.SetVisible(!node.Visible);
            SetVisible(node.Id, node.Visible);
            e.Handled = true;
        };
        AutomationProperties.SetName(eye, Loc.F("glb.visibilityOf", DisplayName(node)));
        node.Eye = eye;
        header.Children.Add(eye);
        var glyph = new TextBlock { Text = KindGlyph(node.Kind), FontFamily = SymbolFont, FontSize = 12, Margin = new Thickness(2, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        glyph.ToolTip = Loc.T("glb.kind." + node.Kind);
        header.Children.Add(glyph);
        var label = Ui.Text(DisplayName(node), 12.5);
        label.VerticalAlignment = VerticalAlignment.Center;
        node.Label = label;
        header.Children.Add(label);
        node.SetVisible(node.Visible);

        var item = new TreeViewItem { Header = header, Tag = node, Padding = new Thickness(1) };
        item.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        AutomationProperties.SetName(item, $"{DisplayName(node)} ({Loc.T("glb.kind." + node.Kind)})");
        if (node.Children.GetArrayLength() > 0)
        {
            if (depth < 2) AddChildren(item, node, depth);
            else
            {
                item.Items.Add(new TreeViewItem { Header = "…" }); // placeholder until expanded
                item.Expanded += (_, _) =>
                {
                    if (item.Items.Count == 1 && item.Items[0] is TreeViewItem { Tag: null })
                    {
                        item.Items.Clear();
                        AddChildren(item, node, depth);
                    }
                };
            }
            item.IsExpanded = depth < 1;
        }
        return item;
    }

    private void AddChildren(TreeViewItem item, TreeNodeInfo node, int depth)
    {
        foreach (var child in node.Children.EnumerateArray()) item.Items.Add(CreateItem(child, depth + 1));
    }

    private static string DisplayName(TreeNodeInfo node) => node.Name.Length > 0 ? node.Name : $"({Loc.T("glb.kind." + node.Kind)} {node.Id})";

    private IEnumerable<TreeViewItem> AllTreeItems()
    {
        var stack = new Stack<TreeViewItem>(_tree.Items.OfType<TreeViewItem>());
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            yield return item;
            foreach (var child in item.Items.OfType<TreeViewItem>()) stack.Push(child);
        }
    }

    /// <summary>Highlights the entry picked in the 3D view (expanding lazy branches on the way).</summary>
    private void SelectInTree(int? id)
    {
        _selectingFromViewer = true;
        try
        {
            if (id is null)
            {
                if (_tree.SelectedItem is TreeViewItem selected) selected.IsSelected = false;
                return;
            }
            var path = new List<TreeViewItem>();
            if (FindPath(_tree.Items.OfType<TreeViewItem>(), id.Value, path))
            {
                foreach (var parent in path.Take(path.Count - 1)) parent.IsExpanded = true;
                path[^1].IsSelected = true;
                path[^1].BringIntoView();
            }
        }
        finally
        {
            _selectingFromViewer = false;
        }
    }

    private bool FindPath(IEnumerable<TreeViewItem> items, int id, List<TreeViewItem> path)
    {
        foreach (var item in items)
        {
            if (item.Tag is not TreeNodeInfo node) continue;
            path.Add(item);
            if (node.Id == id) return true;
            if (item.Items.Count == 1 && item.Items[0] is TreeViewItem { Tag: null } && ContainsId(node.Children, id))
            {
                item.Items.Clear();
                AddChildren(item, node, 3);
            }
            if (FindPath(item.Items.OfType<TreeViewItem>(), id, path)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    private static bool ContainsId(JsonElement children, int id)
    {
        foreach (var child in children.EnumerateArray())
            if (child.GetProperty("id").GetInt32() == id || ContainsId(child.GetProperty("children"), id)) return true;
        return false;
    }

    // ───────────────────────── properties ─────────────────────────

    private static TextBlock Heading(string text)
    {
        var block = Ui.Text(text.ToUpper(Loc.Culture), 11, "TextMuted", bold: true);
        block.Margin = new Thickness(0, 4, 0, 8);
        return block;
    }

    private static Grid Row(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var name = Ui.Text(label, 12, "TextSecondary");
        name.TextWrapping = TextWrapping.Wrap;
        grid.Children.Add(name);
        var text = Ui.Text(value, 12.5);
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static string Vector(JsonElement array) =>
        string.Join("  ", array.EnumerateArray().Select(v => v.GetDouble().ToString("0.###", Loc.Culture)));

    private void ShowProperties(JsonElement? props)
    {
        _propertiesHost.Children.Clear();
        _propertiesHost.Children.Add(Heading(Loc.T("glb.properties")));
        if (props is not { } p)
        {
            var hint = Ui.Text(Loc.T("glb.noSelection"), 12, "TextMuted");
            hint.TextWrapping = TextWrapping.Wrap;
            _propertiesHost.Children.Add(hint);
            return;
        }
        var id = p.GetProperty("id").GetInt32();
        var kind = p.GetProperty("kind").GetString() ?? "node";
        _propertiesHost.Children.Add(Row(Loc.T("glb.prop.name"), p.GetProperty("name").GetString() is { Length: > 0 } n ? n : "–"));
        _propertiesHost.Children.Add(Row(Loc.T("glb.prop.type"), $"{Loc.T("glb.kind." + kind)} ({p.GetProperty("type").GetString()})"));

        // Editable transform: changes apply immediately in the 3D view (the GLB file stays untouched).
        var boxes = new List<TextBox>();
        foreach (var (key, field) in new[] { ("glb.prop.position", "position"), ("glb.prop.rotation", "rotation"), ("glb.prop.scale", "scale") })
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(Ui.Text(Loc.T(key), 12, "TextSecondary"));
            var values = new UniformGrid { Columns = 3 };
            var axis = 0;
            foreach (var value in p.GetProperty(field).EnumerateArray())
            {
                var (host, box) = Ui.Input("");
                box.Height = 28;
                box.FontSize = 12;
                box.Padding = new Thickness(6, 0, 6, 0);
                box.Text = value.GetDouble().ToString("0.###", Loc.Culture);
                host.Margin = new Thickness(0, 0, 4, 0);
                AutomationProperties.SetName(box, $"{Loc.T(key)} {"XYZ"[axis++]}");
                box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) ApplyTransform(id, boxes); };
                box.LostKeyboardFocus += (_, _) => ApplyTransform(id, boxes);
                boxes.Add(box);
                values.Children.Add(host);
            }
            SetColumn(values, 1);
            row.Children.Add(values);
            _propertiesHost.Children.Add(row);
        }
        var materials = p.GetProperty("materials").EnumerateArray().Select(m => m.GetString()).Where(m => m is not null).ToList();
        if (materials.Count > 0) _propertiesHost.Children.Add(Row(Loc.T("glb.prop.material"), string.Join("\n", materials)));
        _propertiesHost.Children.Add(Row(Loc.T("glb.prop.geometry"), Loc.F("glb.prop.geometryValue", p.GetProperty("vertices").GetInt64().ToString("N0", Loc.Culture), p.GetProperty("triangles").GetInt64().ToString("N0", Loc.Culture))));
        _propertiesHost.Children.Add(Row(Loc.T("glb.prop.size"), Vector(p.GetProperty("size"))));
        var visible = p.GetProperty("visible").GetBoolean();
        _propertiesHost.Children.Add(Row(Loc.T("glb.prop.visible"), Loc.T(visible ? "glb.visible" : "glb.hidden")));

        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        Button Act(string textKey, Action click)
        {
            var b = Ui.Button("ChipButton", Loc.T(textKey), (_, _) => click());
            b.Height = 30;
            b.Padding = new Thickness(10, 0, 10, 0);
            b.Margin = new Thickness(0, 0, 6, 6);
            return b;
        }
        actions.Children.Add(Act("glb.focus", () => Focus(id)));
        actions.Children.Add(Act(visible ? "glb.hide" : "glb.show", () =>
        {
            SetVisible(id, !visible);
            foreach (var item in AllTreeItems()) if (item.Tag is TreeNodeInfo node && node.Id == id) node.SetVisible(!visible);
        }));
        if (kind == "camera") actions.Children.Add(Act("glb.useCamera", () => _bridge?.Send("useCamera", new { id })));
        _propertiesHost.Children.Add(actions);
    }

    private void ApplyTransform(int id, List<TextBox> boxes)
    {
        var values = new double[9];
        for (var i = 0; i < 9; i++)
        {
            if (!double.TryParse(boxes[i].Text, NumberStyles.Float, Loc.Culture, out values[i]) &&
                !double.TryParse(boxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
            {
                boxes[i].SetResourceReference(Control.BorderBrushProperty, "OrangeBrush");
                return;
            }
            boxes[i].SetResourceReference(Control.BorderBrushProperty, "CardBorder");
        }
        if (values.Skip(6).Any(v => Math.Abs(v) < 1e-9))
        {
            foreach (var box in boxes.Skip(6)) box.SetResourceReference(Control.BorderBrushProperty, "OrangeBrush");
            return;
        }
        _bridge?.Send("setTransform", new { id, position = values[..3], rotation = values[3..6], scale = values[6..] });
    }

    // ───────────────────────── display settings ─────────────────────────

    private Button Chip(string text, bool selected, Action click, string? tip = null)
    {
        var chip = Ui.Button("ChipButton", text, (_, _) => click(), tip);
        chip.Height = 30;
        chip.Padding = new Thickness(10, 0, 10, 0);
        chip.Margin = new Thickness(0, 0, 6, 6);
        Ui.Select(chip, selected);
        AutomationProperties.SetName(chip, text);
        AutomationProperties.SetItemStatus(chip, selected ? Loc.T("ac.selected") : "");
        return chip;
    }

    private Button Toggle(string text, bool on, Action click, string tip) => Chip((on ? "☑ " : "☐ ") + text, on, click, tip);

    private UIElement SliderRow(string label, double min, double max, double value, Action<double> set)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        var name = Ui.Text(label, 12, "TextSecondary");
        name.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(name);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center, SmallChange = 0.05, LargeChange = 0.25 };
        AutomationProperties.SetName(slider, label);
        var text = Ui.Text(value.ToString("0.00", Loc.Culture), 12);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.HorizontalAlignment = HorizontalAlignment.Right;
        slider.ValueChanged += (_, _) =>
        {
            text.Text = slider.Value.ToString("0.00", Loc.Culture);
            set(slider.Value);
            _settings.Save();
            SendSettings();
        };
        SetColumn(slider, 1);
        SetColumn(text, 2);
        grid.Children.Add(slider);
        grid.Children.Add(text);
        return grid;
    }

    private void RefreshDisplayPanel()
    {
        _displayHost.Children.Clear();
        _displayHost.Children.Add(Heading(Loc.T("glb.display")));
        var modes = new WrapPanel();
        foreach (var mode in new[] { "normal", "unlit", "wireframe", "basecolor", "normals", "metalrough" })
            modes.Children.Add(Chip(Loc.T("glb.mode." + mode), _viewMode == mode, () => SetViewMode(mode), Loc.T("glb.mode." + mode + ".tip")));
        _displayHost.Children.Add(modes);

        _displayHost.Children.Add(Ui.Text(Loc.T("glb.background"), 12, "TextSecondary"));
        var backgrounds = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach (var background in Enum.GetValues<ViewerBackground>())
            backgrounds.Children.Add(Chip(Loc.T("glb.bg." + background), _settings.Background == background, () => ChangeSettings(() => _settings.Background = background)));
        _displayHost.Children.Add(backgrounds);

        _displayHost.Children.Add(SliderRow(Loc.T("glb.light"), 0, 4, _settings.LightIntensity, v => _settings.LightIntensity = v));
        _displayHost.Children.Add(SliderRow(Loc.T("glb.exposure"), 0.1, 4, _settings.Exposure, v => _settings.Exposure = v));

        var toggles = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        toggles.Children.Add(Toggle(Loc.T("glb.shadows"), _settings.Shadows, () => ChangeSettings(() => _settings.Shadows = !_settings.Shadows), Loc.T("glb.tip.shadows")));
        toggles.Children.Add(Toggle(Loc.T("glb.grid"), _settings.Grid, () => ChangeSettings(() => _settings.Grid = !_settings.Grid), Loc.T("glb.tip.grid")));
        toggles.Children.Add(Toggle(Loc.T("glb.axes"), _settings.Axes, () => ChangeSettings(() => _settings.Axes = !_settings.Axes), Loc.T("glb.tip.axes")));
        toggles.Children.Add(Toggle(Loc.T("glb.autoRotate"), _autoRotate, ToggleAutoRotate, Loc.T("glb.tip.autoRotate")));
        _displayHost.Children.Add(toggles);
    }

    // ───────────────────────── model information ─────────────────────────

    private void RefreshInfoPanel()
    {
        _infoHost.Children.Clear();
        _infoHost.Children.Add(Heading(Loc.T("glb.info")));
        if (_info is not { } info || _inspection is not { } file)
        {
            var hint = Ui.Text(Loc.T("glb.status.empty"), 12, "TextMuted");
            hint.TextWrapping = TextWrapping.Wrap;
            _infoHost.Children.Add(hint);
            return;
        }
        string N(long v) => v.ToString("N0", Loc.Culture);
        var dims = info.GetProperty("dimensions").EnumerateArray().Select(v => v.GetDouble().ToString("0.###", Loc.Culture)).ToList();
        _infoHost.Children.Add(Row(Loc.T("glb.info.file"), System.IO.Path.GetFileName(file.Path)));
        _infoHost.Children.Add(Row(Loc.T("glb.info.size"), FormatBytes(file.FileSize)));
        _infoHost.Children.Add(Row(Loc.T("glb.info.scenes"), N(file.Scenes)));
        _infoHost.Children.Add(Row(Loc.T("glb.info.nodes"), N(file.Nodes)));
        _infoHost.Children.Add(Row(Loc.T("glb.info.meshes"), N(file.Meshes)));
        _infoHost.Children.Add(Row(Loc.T("glb.info.materials"), N(file.Materials)));
        _infoHost.Children.Add(Row(Loc.T("glb.info.textures"), N(file.Textures)));
        _infoHost.Children.Add(Row(Loc.T("glb.info.animations"), N(file.Animations)));
        _infoHost.Children.Add(Row(Loc.T("glb.info.vertices"), N(info.GetProperty("vertices").GetInt64())));
        _infoHost.Children.Add(Row(Loc.T("glb.info.triangles"), N(info.GetProperty("triangles").GetInt64())));
        _infoHost.Children.Add(Row(Loc.T("glb.info.dimensions"), dims.Count == 3 ? $"{dims[0]} × {dims[1]} × {dims[2]}" : "–"));
        _infoHost.Children.Add(Row(Loc.T("glb.info.loadTime"), info.GetProperty("loadMs").GetDouble().ToString("0", Loc.Culture) + " ms"));
        if (file.ExtensionsUsed.Count > 0) _infoHost.Children.Add(Row(Loc.T("glb.info.extensions"), string.Join("\n", file.ExtensionsUsed)));
        if (file.Generator is { Length: > 0 } generator) _infoHost.Children.Add(Row(Loc.T("glb.info.generator"), generator));
        if (_warnings.Count > 0)
        {
            _infoHost.Children.Add(Heading(Loc.T("glb.warnings")));
            foreach (var warning in _warnings.Take(20))
            {
                var text = Ui.Text("⚠ " + warning, 12, "OrangeBrush");
                text.TextWrapping = TextWrapping.Wrap;
                text.TextTrimming = TextTrimming.None;
                text.Margin = new Thickness(0, 0, 0, 4);
                _infoHost.Children.Add(text);
            }
        }
        var export = Ui.Button("SecondaryButton", Loc.T("glb.exportJson"), (_, _) => ExportInfo(), Loc.T("glb.tip.export"));
        export.Height = 32;
        export.Margin = new Thickness(0, 6, 0, 0);
        export.HorizontalAlignment = HorizontalAlignment.Left;
        export.Padding = new Thickness(12, 0, 12, 0);
        _infoHost.Children.Add(export);
    }

    // ───────────────────────── animation bar ─────────────────────────

    private int _clip;
    private bool _loop = true;

    private void RefreshAnimationBar()
    {
        _animationHost.Children.Clear();
        _animationBar.Visibility = _clips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_clips.Count == 0) return;

        var top = new DockPanel();
        _playButton = IconButton("", "glb.tip.play", () => Animation("toggle"));
        var stop = IconButton("", "glb.tip.stop", () => Animation("stop"));
        DockPanel.SetDock(_playButton, Dock.Left);
        DockPanel.SetDock(stop, Dock.Left);
        top.Children.Add(_playButton);
        top.Children.Add(stop);
        _timeText = Ui.Text("0:00.0 / 0:00.0", 12.5);
        _timeText.VerticalAlignment = VerticalAlignment.Center;
        _timeText.Margin = new Thickness(10, 0, 10, 0);
        _timeText.MinWidth = 120;
        DockPanel.SetDock(_timeText, Dock.Right);
        top.Children.Add(_timeText);
        _timeline = new Slider { Minimum = 0, Maximum = Math.Max(0.001, _clips.FirstOrDefault(c => c.Index == _clip).Duration), VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        AutomationProperties.SetName(_timeline, Loc.T("glb.timeline"));
        _timeline.ValueChanged += (_, _) =>
        {
            if (_updatingTimeline) return;
            // Throttled: the playhead can be dragged freely without flooding the 3D view.
            if (DateTime.Now - _lastSeek < TimeSpan.FromMilliseconds(30)) return;
            _lastSeek = DateTime.Now;
            Animation("seek", _timeline.Value);
        };
        top.Children.Add(_timeline);
        _animationHost.Children.Add(top);

        var bottom = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        bottom.Children.Add(Ui.Text(Loc.T("glb.clip"), 12, "TextSecondary"));
        ((TextBlock)bottom.Children[0]).Margin = new Thickness(0, 6, 8, 0);
        foreach (var (index, name, duration) in _clips.Take(40))
            bottom.Children.Add(Chip(name, index == _clip, () => { _clip = index; Animation("clip", index); }, $"{name} · {duration.ToString("0.0", Loc.Culture)} s"));
        bottom.Children.Add(Toggle(Loc.T("glb.loop"), _loop, () => { _loop = !_loop; Animation("loop", _loop); RefreshAnimationBar(); }, Loc.T("glb.tip.loop")));
        foreach (var speed in new[] { 0.25, 0.5, 1, 1.5, 2 })
            bottom.Children.Add(Chip(speed.ToString("0.##", Loc.Culture) + "×", Math.Abs(_settings.AnimationSpeed - speed) < 0.001, () =>
            {
                _settings.AnimationSpeed = speed;
                _settings.Save();
                Animation("speed", speed);
                RefreshAnimationBar();
            }, Loc.T("glb.tip.speed")));
        _animationHost.Children.Add(bottom);
    }

    private static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss\.f", Loc.Culture);

    private void UpdateAnimation(JsonElement d)
    {
        var clip = d.TryGetProperty("clip", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : _clip;
        if (clip != _clip)
        {
            _clip = clip;
            RefreshAnimationBar();
        }
        if (_timeline is null || _timeText is null || _playButton is null) return;
        var duration = d.GetProperty("duration").GetDouble();
        var time = d.GetProperty("time").GetDouble();
        var playing = d.GetProperty("playing").GetBoolean();
        _updatingTimeline = true;
        _timeline.Maximum = Math.Max(0.001, duration);
        if (!_timeline.IsMouseCaptureWithin) _timeline.Value = Math.Clamp(time % Math.Max(duration, 0.001), 0, _timeline.Maximum);
        _updatingTimeline = false;
        _timeText.Text = $"{Clock(time % Math.Max(duration, 0.001))} / {Clock(duration)}";
        ((TextBlock)_playButton.Content).Text = playing ? "" : "";
        _playButton.ToolTip = Loc.T(playing ? "glb.tip.pause" : "glb.tip.play");
        AutomationProperties.SetName(_playButton, Loc.T(playing ? "glb.tip.pause" : "glb.tip.play"));
    }
}
