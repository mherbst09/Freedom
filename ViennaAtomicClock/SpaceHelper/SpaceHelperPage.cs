using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ViennaAtomicClock.SpaceHelper;

/// <summary>
/// SpaceHelper – finds what fills the drives and frees space safely. Overview (drives, analysis, categories,
/// charts), file list (filters, selection, actions), installed programs and settings. Nothing is ever removed
/// without the user ticking it and confirming the dialog; by default files go to the recycle bin.
/// </summary>
internal sealed class SpaceHelperPage : Grid
{
    private const string SettingsFile = "spacehelper.json";
    private const int PageSize = 150;
    private static readonly Brush DangerBrush = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush SafeColor = Frozen(Color.FromRgb(0x22, 0xA0, 0x6B));
    private static readonly Brush ReviewColor = Frozen(Color.FromRgb(0xE0, 0x8A, 0x1A));
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly SpaceSettings _settings;
    private readonly SystemPaths _paths;
    private readonly SpaceCleaner _cleaner;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
    private readonly StackPanel _content = new() { MaxWidth = 1240 };
    private readonly WrapPanel _tabs = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _actionBar = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Grid _overlay = new() { Visibility = Visibility.Collapsed };
    private readonly Border _overlayHost = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), MaxWidth = 640 };

    private List<DriveSummary> _drives = new();
    private string _root = "";
    private string _view = "overview";
    private CancellationTokenSource? _work;
    private string _phase = "";
    private ScanProgress _progress;
    private double _duplicateProgress;
    private readonly DispatcherTimer _progressTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
    private Border? _progressFill;
    private TextBlock? _progressText;
    private TextBlock? _progressFolder;

    private ScanResult? _scan;
    private List<AreaResult> _areas = new();
    private List<DuplicateGroup> _duplicates = new();
    private List<InstalledProgram>? _programs;
    private RecycleBin.Info _recycle;
    private FolderNode? _chartFolder;

    // File list state.
    private Finding? _source;
    private bool _allSources = true;
    private string _search = "";
    private string _sort = "size";
    private FileKind? _kindFilter;
    private Safety? _safetyFilter;
    private long _minSize;
    private int _minAgeDays;
    private int _shown = PageSize;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FoundFile> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public SpaceHelperPage() : this(DataFile.Load<SpaceSettings>(SettingsFile).Normalize(), SystemPaths.Current, new SpaceCleaner()) { }

    public SpaceHelperPage(SpaceSettings settings, SystemPaths paths, SpaceCleaner cleaner)
    {
        _settings = settings;
        _paths = paths;
        _cleaner = cleaner;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(Header());
        _scroll.Content = _content;
        _scroll.Margin = new Thickness(0, 12, 0, 0);
        SetRow(_scroll, 1);
        Children.Add(_scroll);
        SetRow(_actionBar, 2);
        Children.Add(_actionBar);
        var dim = new Border { Background = Frozen(Color.FromArgb(150, 0, 0, 0)) };
        _overlay.Children.Add(dim);
        _overlay.Children.Add(_overlayHost);
        SetRowSpan(_overlay, 3);
        Children.Add(_overlay);
        _progressTimer.Tick += (_, _) => UpdateProgress();
        IsVisibleChanged += (_, _) => { if (IsVisible) { RefreshDrives(); Rebuild(); } };
        Loc.Changed += OnLanguageChanged;
        SizeChanged += (_, e) => { if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 120 && _view == "files") Rebuild(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _overlay.Visibility == Visibility.Visible && _work is null) { CloseOverlay(); e.Handled = true; }
        };
        RefreshDrives();
        Rebuild();
    }

    internal ScanResult? Scan => _scan;
    internal IReadOnlyList<AreaResult> Areas => _areas;
    internal IReadOnlyCollection<string> SelectedPaths => _selected;
    internal string View => _view;
    internal bool Busy => _work is not null;

    public string StatusLine
    {
        get
        {
            if (_work is not null) return Loc.F("sh.header.busy", (int)(Math.Max(_progress.Share, _duplicateProgress) * 100));
            var drive = _drives.FirstOrDefault(d => _root.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase)) ?? _drives.FirstOrDefault();
            return drive is null ? "SpaceHelper" : Loc.F("sh.header.free", drive.Name.TrimEnd('\\'), Bytes.Format(drive.Free));
        }
    }

    private void OnLanguageChanged()
    {
        RebuildHeader();
        Rebuild();
    }

    public void Shutdown()
    {
        Loc.Changed -= OnLanguageChanged;
        _work?.Cancel();
        SaveSettings();
    }

    private void SaveSettings() => DataFile.Save(SettingsFile, _settings);

    // ───────────────────────── header and tabs ─────────────────────────

    private UIElement Header()
    {
        var stack = new StackPanel();
        var row = new DockPanel();
        var titles = new StackPanel();
        var title = Ui.Text("SpaceHelper", 28, bold: true);
        title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        titles.Children.Add(title);
        var tagline = Ui.Text(Loc.T("sh.tagline"), 13.5, "TextSecondary");
        tagline.TextWrapping = TextWrapping.Wrap;
        titles.Children.Add(tagline);
        row.Children.Add(titles);
        stack.Children.Add(row);
        if (_tabs.Parent is Panel previous) previous.Children.Remove(_tabs); // rebuilt header after a language change
        stack.Children.Add(_tabs);
        RebuildTabs();
        return stack;
    }

    private void RebuildHeader()
    {
        Children.RemoveAt(0);
        Children.Insert(0, Header());
    }

    private void RebuildTabs()
    {
        _tabs.Children.Clear();
        foreach (var (view, key) in new[] { ("overview", "sh.tab.overview"), ("files", "sh.tab.files"), ("programs", "sh.tab.programs"), ("settings", "sh.tab.settings") })
        {
            var text = Loc.T(key);
            if (view == "files" && _selected.Count > 0) text += $"  ({_selected.Count})";
            _tabs.Children.Add(Chip(text, _view == view, () => Show(view)));
        }
        var simulation = Chip((_settings.Simulation ? "☑  " : "☐  ") + Loc.T("sh.simulation"), _settings.Simulation, () =>
        {
            _settings.Simulation = !_settings.Simulation;
            SaveSettings();
            RebuildTabs();
            UpdateActionBar();
        });
        simulation.ToolTip = Loc.T("sh.simulation.help");
        simulation.Margin = new Thickness(18, 0, 6, 6);
        _tabs.Children.Add(simulation);
    }

    private void Show(string view)
    {
        _view = view;
        if (view == "programs" && _programs is null) _ = LoadProgramsAsync();
        RebuildTabs();
        Rebuild();
        _scroll.ScrollToTop();
    }

    private void Rebuild()
    {
        _content.Children.Clear();
        switch (_view)
        {
            case "files": BuildFiles(); break;
            case "programs": BuildPrograms(); break;
            case "settings": BuildSettings(); break;
            default: BuildOverview(); break;
        }
        UpdateActionBar();
        Changed?.Invoke();
    }

    // ───────────────────────── overview ─────────────────────────

    private void RefreshDrives()
    {
        _drives = DriveSummary.ReadAll();
        if (_root.Length == 0 || (!Directory.Exists(_root)))
            _root = _drives.FirstOrDefault(d => d.Name.StartsWith(Path.GetPathRoot(_paths.Windows) ?? "C:", StringComparison.OrdinalIgnoreCase))?.Name ?? _drives.FirstOrDefault()?.Name ?? "";
        _recycle = RecycleBin.Query();
    }

    private void BuildOverview()
    {
        _content.Children.Add(Section(Loc.T("sh.drives")));
        var drives = new WrapPanel();
        foreach (var drive in _drives) drives.Children.Add(DriveCard(drive));
        drives.Children.Add(FolderCard());
        _content.Children.Add(drives);

        var analyze = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (_work is not null) analyze.Children.Add(ProgressCard());
        else
        {
            var button = Ui.Button("ActionButton", "🔍  " + Loc.T("sh.analyze"), (_, _) => _ = AnalyzeAsync());
            button.Height = 56;
            button.FontSize = 17;
            button.Padding = new Thickness(34, 0, 34, 0);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.IsEnabled = _root.Length > 0;
            AutomationProperties.SetName(button, Loc.T("sh.analyze"));
            analyze.Children.Add(button);
            analyze.Children.Add(Info(Loc.F("sh.analyze.hint", _root)));
        }
        _content.Children.Add(analyze);

        if (_scan is null && _areas.Count == 0)
        {
            _content.Children.Add(SafetyExplainer());
            return;
        }
        if (_scan is not null) _content.Children.Add(Info(ScanSummary(_scan), "TextSecondary", 12.5));
        _content.Children.Add(Section(Loc.T("sh.categories")));
        var tiles = new WrapPanel();
        foreach (var tile in CategoryTiles()) tiles.Children.Add(tile);
        _content.Children.Add(tiles);

        if (_scan is not null)
        {
            var charts = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            var wide = ActualWidth == 0 || ActualWidth > 980;
            if (wide)
            {
                charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
                charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                charts.ColumnDefinitions.Add(new ColumnDefinition());
            }
            else
            {
                charts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                charts.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
                charts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            var folders = FolderChart(_scan);
            var kinds = KindChart(_scan);
            if (wide) SetColumn(kinds, 2);
            else SetRow(kinds, 2);
            charts.Children.Add(folders);
            charts.Children.Add(kinds);
            _content.Children.Add(charts);
        }
        _content.Children.Add(SafetyExplainer());
    }

    private UIElement DriveCard(DriveSummary drive)
    {
        var stack = new StackPanel();
        var head = new DockPanel();
        head.Children.Add(Glyph("", 20));
        var name = Ui.Text($"{drive.Name.TrimEnd('\\')}  {drive.Label}", 15, bold: true);
        name.Margin = new Thickness(8, 0, 0, 0);
        head.Children.Add(name);
        stack.Children.Add(head);
        var bar = UsageBar(drive.UsedShare, 10);
        bar.Margin = new Thickness(0, 10, 0, 6);
        stack.Children.Add(bar);
        stack.Children.Add(Ui.Text(Loc.F("sh.drive.free", Bytes.Format(drive.Free), Bytes.Format(drive.Total)), 12.5, "TextSecondary"));
        stack.Children.Add(Ui.Text(Loc.F("sh.drive.used", Bytes.Format(drive.Used), (int)Math.Round(drive.UsedShare * 100)), 11.5, "TextMuted"));
        var selected = string.Equals(_root, drive.Name, StringComparison.OrdinalIgnoreCase);
        return SelectableCard(stack, selected, () => { _root = drive.Name; Rebuild(); }, $"{drive.Name} {drive.Label}");
    }

    private UIElement FolderCard()
    {
        var stack = new StackPanel();
        var head = new DockPanel();
        head.Children.Add(Glyph("", 20));
        var name = Ui.Text(Loc.T("sh.folder.pick"), 15, bold: true);
        name.Margin = new Thickness(8, 0, 0, 0);
        head.Children.Add(name);
        stack.Children.Add(head);
        var isFolder = _root.Length > 0 && !_drives.Any(d => string.Equals(d.Name, _root, StringComparison.OrdinalIgnoreCase));
        var text = Ui.Text(isFolder ? _root : Loc.T("sh.folder.hint"), 12, isFolder ? "TextPrimary" : "TextMuted");
        text.TextWrapping = TextWrapping.Wrap;
        text.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(text);
        return SelectableCard(stack, isFolder, () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("sh.folder.pick") };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                _root = dialog.FolderName;
                Rebuild();
            }
        }, Loc.T("sh.folder.pick"));
    }

    private Border SelectableCard(UIElement content, bool selected, Action click, string name)
    {
        var card = new Border
        {
            Width = 270, MinHeight = 120, Padding = new Thickness(16), Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(16), Child = content,
            BorderThickness = new Thickness(selected ? 2 : 1), Cursor = Cursors.Hand, Focusable = true
        };
        card.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSoft" : "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBrush" : "CardBorder");
        card.MouseLeftButtonUp += (_, _) => click();
        card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { click(); e.Handled = true; } };
        AutomationProperties.SetName(card, name);
        return card;
    }

    private static Grid UsageBar(double share, double height)
    {
        var grid = new Grid { Height = height };
        var track = new Border { CornerRadius = new CornerRadius(height / 2) };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        grid.Children.Add(track);
        var fill = new Border { CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left };
        if (share > 0.9) fill.Background = DangerBrush;
        else if (share > 0.75) fill.Background = ReviewColor;
        else fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        grid.Children.Add(fill);
        grid.SizeChanged += (_, _) => fill.Width = Math.Max(height, grid.ActualWidth * Math.Clamp(share, 0, 1));
        return grid;
    }

    private UIElement ProgressCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.T("sh.phase." + _phase), 15, bold: true));
        var track = new Grid { Height = 10, Margin = new Thickness(0, 10, 0, 8) };
        var back = new Border { CornerRadius = new CornerRadius(5) };
        back.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        _progressFill = new Border { CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _progressFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        track.Children.Add(back);
        track.Children.Add(_progressFill);
        stack.Children.Add(track);
        _progressText = Ui.Text("", 12.5, "TextSecondary");
        stack.Children.Add(_progressText);
        _progressFolder = Ui.Text("", 11.5, "TextMuted");
        stack.Children.Add(_progressFolder);
        var cancel = Ui.Button("SecondaryButton", Loc.T("sh.cancel"), (_, _) => _work?.Cancel());
        cancel.Height = 36;
        cancel.Padding = new Thickness(18, 0, 18, 0);
        cancel.Margin = new Thickness(0, 12, 0, 0);
        cancel.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(cancel);
        UpdateProgress();
        return Ui.Card(stack, 18);
    }

    private void UpdateProgress()
    {
        if (_progressFill?.Parent is not Grid track) return;
        var share = _phase == "duplicates" ? _duplicateProgress : _progress.Share;
        _progressFill.Width = track.ActualWidth * Math.Clamp(share, 0, 1);
        if (_progressText is not null)
            _progressText.Text = _phase == "duplicates"
                ? Loc.F("sh.progress.duplicates", (int)(share * 100))
                : Loc.F("sh.progress.files", _progress.Files.ToString("N0", Loc.Culture), Bytes.Format(_progress.Bytes), share > 0 ? $" · {(int)(share * 100)} %" : "");
        if (_progressFolder is not null) _progressFolder.Text = _phase == "drive" ? _progress.CurrentFolder : "";
        Changed?.Invoke();
    }

    private string ScanSummary(ScanResult scan)
    {
        var text = Loc.F("sh.summary", scan.Files.ToString("N0", Loc.Culture), Bytes.Format(scan.Bytes), scan.Elapsed.TotalSeconds.ToString("0", Loc.Culture));
        if (scan.Inaccessible > 0) text += " · " + Loc.F("sh.summary.locked", scan.Inaccessible.ToString("N0", Loc.Culture));
        if (scan.LinksSkipped > 0) text += " · " + Loc.F("sh.summary.links", scan.LinksSkipped.ToString("N0", Loc.Culture));
        if (scan.Canceled) text += " · " + Loc.T("sh.summary.canceled");
        return text;
    }

    // ───────────────────────── analysis ─────────────────────────

    /// <summary>Runs the full analysis (drive or folder, cleanup areas, duplicates). Also used by the tests.</summary>
    internal async Task AnalyzeAsync(string? root = null)
    {
        if (root is not null) _root = root;
        if (_work is not null || _root.Length == 0) return;
        _work = new CancellationTokenSource();
        var token = _work.Token;
        _selected.Clear();
        _progress = default;
        _duplicateProgress = 0;
        var drive = _drives.FirstOrDefault(d => string.Equals(d.Name, _root, StringComparison.OrdinalIgnoreCase));
        _progressTimer.Start();
        try
        {
            SetPhase("drive");
            var progress = new Progress<ScanProgress>(p => _progress = p);
            _scan = await new SpaceScanner(_settings, _paths).ScanAsync(_root, drive?.Used ?? 0, progress, token);
            _chartFolder = _scan.Tree;
            if (!token.IsCancellationRequested)
            {
                SetPhase("areas");
                _areas = await new CleanupAreas(_settings, _paths).ScanAsync(token);
                SetPhase("duplicates");
                _duplicates = await new DuplicateFinder(_paths, byName: !_settings.DuplicatesByContentOnly).FindAsync(_scan.SameSize, new Progress<double>(p => _duplicateProgress = p), token);
                _recycle = RecycleBin.Query();
            }
        }
        catch (OperationCanceledException)
        {
            // Partial results stay visible.
        }
        finally
        {
            _progressTimer.Stop();
            _work.Dispose();
            _work = null;
            _phase = "";
            IndexFiles();
            RefreshDrives();
            Rebuild();
        }
    }

    private void SetPhase(string phase)
    {
        _phase = phase;
        Rebuild();
    }

    /// <summary>One entry per path; if any finding rates a file "Nicht löschen", it stays unselectable everywhere.</summary>
    private void IndexFiles()
    {
        _byPath.Clear();
        foreach (var file in AllFindings())
        {
            if (!_byPath.TryGetValue(file.Path, out var existing) || file.Safety > existing.Safety) _byPath[file.Path] = file;
        }
        _selected.RemoveWhere(path => !_byPath.TryGetValue(path, out var file) || !file.CanSelect);
    }

    private IEnumerable<FoundFile> AllFindings()
    {
        foreach (var area in _areas) foreach (var file in area.Files) yield return file;
        if (_scan is not null)
        {
            foreach (var file in _scan.Large) yield return file;
            foreach (var file in _scan.Stale) yield return file;
        }
        foreach (var group in _duplicates) foreach (var file in group.Files) yield return file;
    }

    private IEnumerable<FoundFile> FilesOf(Finding finding) => finding switch
    {
        Finding.Large => _scan?.Large ?? (IEnumerable<FoundFile>)Array.Empty<FoundFile>(),
        Finding.Stale => _scan?.Stale ?? (IEnumerable<FoundFile>)Array.Empty<FoundFile>(),
        Finding.Duplicate => _duplicates.SelectMany(g => g.Files),
        _ => _areas.FirstOrDefault(a => a.Finding == finding)?.Files ?? (IEnumerable<FoundFile>)Array.Empty<FoundFile>()
    };

    private static string Icon(Finding finding) => finding switch
    {
        Finding.WindowsTemp => "", Finding.AppTemp => "", Finding.Thumbnails => "", Finding.Logs => "",
        Finding.BrowserCache => "", Finding.Installers => "", Finding.Large => "", Finding.Duplicate => "",
        Finding.OldDownload => "", Finding.Stale => "", _ => ""
    };

    private IEnumerable<UIElement> CategoryTiles()
    {
        foreach (var finding in Enum.GetValues<Finding>())
        {
            var files = FilesOf(finding).ToList();
            if (finding == Finding.Custom && _settings.CustomFolders.Count == 0) continue;
            var size = finding == Finding.Duplicate ? _duplicates.Sum(g => g.Wasted) : files.Sum(f => f.Size);
            var count = finding == Finding.Duplicate ? files.Count(f => !f.IsGroupOriginal) : files.Count;
            var safety = files.Count == 0 ? Safety.Safe : files.Where(f => f.CanSelect).Select(f => f.Safety).DefaultIfEmpty(Safety.Keep).Max();
            yield return Tile(Icon(finding), Loc.T("sh.area." + finding.ToString().ToLowerInvariant()), size, Loc.F("sh.tile.files", count.ToString("N0", Loc.Culture)),
                count == 0 ? null : safety, Loc.T("sh.areaHelp." + finding.ToString().ToLowerInvariant()), count == 0 ? null : () => OpenList(finding));
        }
        // Recycle bin: emptied through Windows, after our own confirmation.
        yield return Tile("", Loc.T("sh.area.recycle"), _recycle.Size, Loc.F("sh.tile.files", _recycle.Items.ToString("N0", Loc.Culture)),
            _recycle.Items > 0 ? Safety.Review : null, Loc.T("sh.areaHelp.recycle"), _recycle.Items > 0 ? ConfirmEmptyRecycleBin : null, Loc.T("sh.recycle.empty"));
        var programs = _programs;
        yield return Tile("", Loc.T("sh.area.programs"), programs?.Sum(p => p.Size) ?? 0,
            programs is null ? Loc.T("sh.tile.programsOpen") : Loc.F("sh.tile.programs", programs.Count), null, Loc.T("sh.areaHelp.programs"), () => Show("programs"), Loc.T("sh.tile.open"));
    }

    private Border Tile(string glyph, string title, long size, string detail, Safety? safety, string help, Action? open, string? openText = null)
    {
        var stack = new StackPanel();
        var head = new DockPanel();
        var icon = Glyph(glyph, 18);
        icon.Margin = new Thickness(0, 0, 8, 0);
        head.Children.Add(icon);
        if (safety is { } s)
        {
            var badge = SafetyBadge(s);
            DockPanel.SetDock(badge, Dock.Right);
            head.Children.Add(badge);
        }
        var name = Ui.Text(title, 13.5, bold: true);
        head.Children.Add(name);
        stack.Children.Add(head);
        var big = Ui.Text(Bytes.Format(size), 22, bold: true);
        big.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(big);
        stack.Children.Add(Ui.Text(detail, 11.5, "TextMuted"));
        if (open is not null)
        {
            var button = Ui.Button("SecondaryButton", openText ?? Loc.T("sh.tile.show"), (_, _) => open());
            button.Height = 30;
            button.Padding = new Thickness(12, 0, 12, 0);
            button.Margin = new Thickness(0, 10, 0, 0);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(button);
        }
        var card = new Border { Width = 232, MinHeight = 140, Padding = new Thickness(14), Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(16), Child = stack, BorderThickness = new Thickness(1), ToolTip = help };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        AutomationProperties.SetName(card, $"{title}: {Bytes.Format(size)}, {detail}");
        AutomationProperties.SetHelpText(card, help);
        return card;
    }

    private UIElement FolderChart(ScanResult scan)
    {
        var folder = _chartFolder ?? scan.Tree;
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.T("sh.chart.folders"), 15, bold: true));
        var crumbs = new WrapPanel { Margin = new Thickness(0, 6, 0, 8) };
        var chain = new List<FolderNode>();
        for (var node = folder; node is not null; node = node.Parent) chain.Insert(0, node);
        foreach (var node in chain)
        {
            var target = node;
            var crumb = Chip(node.Name, node == folder, () => { _chartFolder = target; Rebuild(); });
            crumb.Height = 26;
            crumbs.Children.Add(crumb);
        }
        stack.Children.Add(crumbs);
        var items = folder.Children.Take(10).ToList();
        var total = Math.Max(1, folder.Size);
        if (folder.OwnSize > 0) stack.Children.Add(Ui.Text(Loc.F("sh.chart.own", Bytes.Format(folder.OwnSize), folder.Files.ToString("N0", Loc.Culture)), 11.5, "TextMuted"));
        foreach (var child in items)
        {
            var target = child;
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0), Cursor = child.Children.Count > 0 ? Cursors.Hand : Cursors.Arrow, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new StackPanel();
            label.Children.Add(Ui.Text(child.Name, 12.5, bold: true));
            var bar = UsageBar(child.Size / (double)total, 8);
            bar.Margin = new Thickness(0, 4, 12, 0);
            label.Children.Add(bar);
            row.Children.Add(label);
            var size = Ui.Text($"{Bytes.Format(child.Size)} · {(child.Size * 100.0 / total).ToString("0", Loc.Culture)} %", 12, "TextSecondary");
            size.VerticalAlignment = VerticalAlignment.Bottom;
            SetColumn(size, 1);
            row.Children.Add(size);
            row.ToolTip = child.Path;
            if (child.Children.Count > 0) row.MouseLeftButtonUp += (_, _) => { _chartFolder = target; Rebuild(); };
            AutomationProperties.SetName(row, $"{child.Name}: {Bytes.Format(child.Size)}");
            stack.Children.Add(row);
        }
        if (items.Count == 0) stack.Children.Add(Info(Loc.T("sh.chart.empty")));
        var open = Ui.Button("SecondaryButton", Loc.T("sh.showInExplorer"), (_, _) => OpenInExplorer(folder.Path, select: false));
        open.Height = 30;
        open.Padding = new Thickness(12, 0, 12, 0);
        open.Margin = new Thickness(0, 12, 0, 0);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(open);
        return Ui.Card(stack, 16);
    }

    private static readonly Color[] KindColors =
    {
        Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x14, 0xB8, 0x8A), Color.FromRgb(0xF5, 0x9E, 0x0B),
        Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xEF, 0x44, 0x6F), Color.FromRgb(0x94, 0xA3, 0xB8)
    };

    private UIElement KindChart(ScanResult scan)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.T("sh.chart.kinds"), 15, bold: true));
        var total = Math.Max(1, scan.KindSizes.Values.Sum());
        var strip = new Grid { Height = 18, Margin = new Thickness(0, 12, 0, 12), ClipToBounds = true };
        var kinds = Enum.GetValues<FileKind>().OrderByDescending(k => scan.KindSizes[k]).ToList();
        foreach (var kind in kinds)
        {
            var share = scan.KindSizes[kind] / (double)total;
            if (share <= 0) continue;
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 0.004), GridUnitType.Star) });
            var piece = new Border { Background = Frozen(KindColors[(int)kind]), ToolTip = $"{Loc.T("sh.kind." + kind.ToString().ToLowerInvariant())}: {Bytes.Format(scan.KindSizes[kind])}" };
            SetColumn(piece, strip.ColumnDefinitions.Count - 1);
            strip.Children.Add(piece);
        }
        var frame = new Border { CornerRadius = new CornerRadius(9), Child = strip, ClipToBounds = true };
        stack.Children.Add(frame);
        foreach (var kind in kinds)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Frozen(KindColors[(int)kind]), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(dot);
            var value = Ui.Text($"{Bytes.Format(scan.KindSizes[kind])} · {(scan.KindSizes[kind] * 100.0 / total).ToString("0.#", Loc.Culture)} %", 12, "TextSecondary");
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            row.Children.Add(Ui.Text($"{Loc.T("sh.kind." + kind.ToString().ToLowerInvariant())} ({scan.KindCounts[kind].ToString("N0", Loc.Culture)})", 12.5));
            stack.Children.Add(row);
        }
        return Ui.Card(stack, 16);
    }

    private UIElement SafetyExplainer()
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.T("sh.explain.title"), 15, bold: true));
        foreach (var safety in Enum.GetValues<Safety>())
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var badge = SafetyBadge(safety);
            badge.Margin = new Thickness(0, 0, 10, 0);
            badge.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(badge);
            var text = Ui.Text(Loc.T("sh.explain." + safety.ToString().ToLowerInvariant()), 12.5, "TextSecondary");
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.None;
            row.Children.Add(text);
            stack.Children.Add(row);
        }
        stack.Children.Add(Info(Loc.T("sh.explain.rules")));
        var card = Ui.Card(stack, 16);
        card.Margin = new Thickness(0, 12, 0, 20);
        return card;
    }

    // ───────────────────────── file list ─────────────────────────

    private void OpenList(Finding finding)
    {
        _allSources = false;
        _source = finding;
        _shown = PageSize;
        Show("files");
    }

    private void BuildFiles()
    {
        if (_byPath.Count == 0)
        {
            _content.Children.Add(Info(Loc.T("sh.files.empty"), "TextSecondary", 13.5));
            return;
        }
        var sources = new WrapPanel();
        sources.Children.Add(Chip(Loc.F("sh.files.all", _byPath.Count.ToString("N0", Loc.Culture)), _allSources, () => { _allSources = true; _source = null; _shown = PageSize; Rebuild(); }));
        foreach (var finding in Enum.GetValues<Finding>())
        {
            var count = FilesOf(finding).Count();
            if (count == 0) continue;
            sources.Children.Add(Chip($"{Loc.T("sh.area." + finding.ToString().ToLowerInvariant())} ({count.ToString("N0", Loc.Culture)})", !_allSources && _source == finding,
                () => { _allSources = false; _source = finding; _shown = PageSize; Rebuild(); }));
        }
        _content.Children.Add(sources);

        // Search, sort and filters.
        var filters = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var (searchHost, search) = Ui.Input(Loc.T("sh.files.search"));
        searchHost.Width = Math.Min(420, Math.Max(240, ActualWidth - 40));
        searchHost.HorizontalAlignment = HorizontalAlignment.Left;
        search.Text = _search;
        search.TextChanged += (_, _) =>
        {
            _search = search.Text.Trim();
            _shown = PageSize;
            RebuildListOnly();
        };
        AutomationProperties.SetName(search, Loc.T("sh.files.search"));
        filters.Children.Add(searchHost);
        filters.Children.Add(FilterRow("sh.files.sort", new[] { ("size", "sh.sort.size"), ("date", "sh.sort.date"), ("name", "sh.sort.name"), ("kind", "sh.sort.kind"), ("folder", "sh.sort.folder") },
            _sort, v => _sort = v));
        filters.Children.Add(FilterRow("sh.files.kind", new[] { ((FileKind?)null, "sh.filter.all") }.Concat(Enum.GetValues<FileKind>().Select(k => ((FileKind?)k, "sh.kind." + k.ToString().ToLowerInvariant()))).ToArray(),
            _kindFilter, v => _kindFilter = v));
        filters.Children.Add(FilterRow("sh.files.safety", new[] { ((Safety?)null, "sh.filter.all") }.Concat(Enum.GetValues<Safety>().Select(s => ((Safety?)s, "sh.safety." + s.ToString().ToLowerInvariant()))).ToArray(),
            _safetyFilter, v => _safetyFilter = v));
        filters.Children.Add(FilterRow("sh.files.size", new[] { (0L, "sh.filter.all"), (10L << 20, "sh.filter.10mb"), (100L << 20, "sh.filter.100mb"), (1L << 30, "sh.filter.1gb") }, _minSize, v => _minSize = v));
        filters.Children.Add(FilterRow("sh.files.age", new[] { (0, "sh.filter.all"), (30, "sh.filter.30d"), (365, "sh.filter.1y"), (1095, "sh.filter.3y") }, _minAgeDays, v => _minAgeDays = v));
        _content.Children.Add(filters);

        var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        tools.Children.Add(SmallButton(Loc.T("sh.select.safe"), () => SelectVisible(f => f.Safety == Safety.Safe)));
        tools.Children.Add(SmallButton(Loc.T("sh.select.visible"), () => SelectVisible(_ => true)));
        tools.Children.Add(SmallButton(Loc.T("sh.select.none"), () => { _selected.Clear(); RebuildListOnly(); }));
        _content.Children.Add(tools);
        _listHost = new StackPanel();
        _content.Children.Add(_listHost);
        FillList();
    }

    private StackPanel? _listHost;

    private void RebuildListOnly()
    {
        if (_listHost is null || _view != "files") return;
        FillList();
        UpdateActionBar();
        RebuildTabs();
    }

    private UIElement FilterRow<T>(string labelKey, (T Value, string Key)[] options, T current, Action<T> set)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var label = Ui.Text(Loc.T(labelKey), 11.5, "TextMuted", bold: true);
        label.Width = 110;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 0, 6);
        row.Children.Add(label);
        var chips = new WrapPanel();
        foreach (var (value, key) in options)
        {
            var chip = Chip(Loc.T(key), EqualityComparer<T>.Default.Equals(value, current), () => { set(value); _shown = PageSize; Rebuild(); });
            chip.Height = 28;
            chips.Children.Add(chip);
        }
        row.Children.Add(chips);
        return row;
    }

    private IEnumerable<FoundFile> VisibleFiles()
    {
        IEnumerable<FoundFile> files = _allSources ? _byPath.Values : FilesOf(_source ?? Finding.Large).Select(f => _byPath.TryGetValue(f.Path, out var c) && c.Safety > f.Safety ? c : f);
        var now = DateTime.UtcNow;
        if (_search.Length > 0) files = files.Where(f => f.Path.Contains(_search, StringComparison.OrdinalIgnoreCase));
        if (_kindFilter is { } kind) files = files.Where(f => f.Kind == kind);
        if (_safetyFilter is { } safety) files = files.Where(f => f.Safety == safety);
        if (_minSize > 0) files = files.Where(f => f.Size >= _minSize);
        if (_minAgeDays > 0) files = files.Where(f => now - f.ModifiedUtc >= TimeSpan.FromDays(_minAgeDays));
        var duplicatesView = !_allSources && _source == Finding.Duplicate;
        if (duplicatesView) return files.OrderByDescending(f => _duplicates.FirstOrDefault(g => g.Id == f.Group)?.Wasted ?? 0).ThenBy(f => f.Group).ThenByDescending(f => f.IsGroupOriginal);
        return _sort switch
        {
            "date" => files.OrderBy(f => f.ModifiedUtc),
            "name" => files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
            "kind" => files.OrderBy(f => f.Kind).ThenByDescending(f => f.Size),
            "folder" => files.OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderByDescending(f => f.Size)
        };
    }

    private void SelectVisible(Func<FoundFile, bool> which)
    {
        foreach (var file in VisibleFiles().Where(f => f.CanSelect && which(f)).Take(20000)) _selected.Add(file.Path);
        RebuildListOnly();
    }

    private void FillList()
    {
        if (_listHost is null) return;
        _listHost.Children.Clear();
        var files = VisibleFiles().ToList();
        var total = files.Sum(f => f.Size);
        _listHost.Children.Add(Ui.Text(Loc.F("sh.files.count", files.Count.ToString("N0", Loc.Culture), Bytes.Format(total)), 12, "TextSecondary"));
        var narrow = ActualWidth > 0 && ActualWidth < 860;
        var lastGroup = 0;
        foreach (var file in files.Take(_shown))
        {
            if (file.Group != 0 && file.Group != lastGroup && !_allSources && _source == Finding.Duplicate)
            {
                lastGroup = file.Group;
                var group = _duplicates.First(g => g.Id == file.Group);
                var header = Ui.Text(Loc.F("sh.dup.group", group.Files.Count, Bytes.Format(group.Size), Bytes.Format(group.Wasted)), 12, "TextSecondary", bold: true);
                header.Margin = new Thickness(4, 12, 0, 4);
                _listHost.Children.Add(header);
            }
            _listHost.Children.Add(FileRow(file, narrow));
        }
        if (files.Count > _shown)
        {
            var more = SmallButton(Loc.F("sh.files.more", Math.Min(PageSize, files.Count - _shown)), () => { _shown += PageSize; FillList(); });
            more.Margin = new Thickness(0, 8, 0, 0);
            _listHost.Children.Add(more);
        }
    }

    private UIElement FileRow(FoundFile file, bool narrow)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (!narrow)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            IsChecked = _selected.Contains(file.Path), IsEnabled = file.CanSelect, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            ToolTip = file.CanSelect ? null : Loc.T(file.ReasonKey)
        };
        AutomationProperties.SetName(check, Loc.F("sh.files.select", file.Name));
        check.Checked += (_, _) => { _selected.Add(file.Path); UpdateActionBar(); RebuildTabs(); };
        check.Unchecked += (_, _) => { _selected.Remove(file.Path); UpdateActionBar(); RebuildTabs(); };
        grid.Children.Add(check);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(file.Name, 13, bold: true);
        if (file.IsGroupOriginal) name.Text += "   · " + Loc.T("sh.dup.original");
        info.Children.Add(name);
        var path = Ui.Text(file.Path, 11, "TextMuted");
        path.ToolTip = file.Path;
        info.Children.Add(path);
        var meta = $"{Loc.T("sh.kind." + file.Kind.ToString().ToLowerInvariant())} · {Loc.T("sh.area." + file.Finding.ToString().ToLowerInvariant())}";
        if (narrow) meta = $"{Bytes.Format(file.Size)} · {file.ModifiedUtc.ToLocalTime().ToString("d", Loc.Culture)} · " + meta;
        info.Children.Add(Ui.Text(meta, 11, "TextSecondary"));
        SetColumn(info, 1);
        grid.Children.Add(info);

        var column = 2;
        if (!narrow)
        {
            var size = Ui.Text(Bytes.Format(file.Size), 12.5, bold: true);
            size.VerticalAlignment = VerticalAlignment.Center;
            size.HorizontalAlignment = HorizontalAlignment.Right;
            size.Margin = new Thickness(0, 0, 12, 0);
            SetColumn(size, column++);
            grid.Children.Add(size);
            var date = Ui.Text(file.ModifiedUtc.ToLocalTime().ToString("d", Loc.Culture), 12, "TextSecondary");
            date.VerticalAlignment = VerticalAlignment.Center;
            date.ToolTip = Loc.F("sh.files.dates", file.ModifiedUtc.ToLocalTime().ToString("g", Loc.Culture), file.AccessedUtc.ToLocalTime().ToString("g", Loc.Culture));
            SetColumn(date, column++);
            grid.Children.Add(date);
            var badge = SafetyBadge(file.Safety);
            badge.ToolTip = Loc.T(file.ReasonKey);
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.HorizontalAlignment = HorizontalAlignment.Left;
            SetColumn(badge, column++);
            grid.Children.Add(badge);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (narrow)
        {
            var badge = SafetyBadge(file.Safety);
            badge.ToolTip = Loc.T(file.ReasonKey);
            badge.Margin = new Thickness(0, 0, 6, 0);
            actions.Children.Add(badge);
        }
        var explorer = Ui.Button("IconButton", "", (_, _) => OpenInExplorer(file.Path, select: true), Loc.T("sh.showInExplorer"));
        explorer.FontFamily = IconFont;
        explorer.Width = 32;
        explorer.Height = 32;
        AutomationProperties.SetName(explorer, Loc.T("sh.showInExplorer"));
        actions.Children.Add(explorer);
        SetColumn(actions, grid.ColumnDefinitions.Count - 1);
        grid.Children.Add(actions);

        var row = new Border { Child = grid, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 4, 0, 0), CornerRadius = new CornerRadius(12), ToolTip = Loc.T(file.ReasonKey) };
        row.SetResourceReference(Border.BackgroundProperty, _selected.Contains(file.Path) ? "AccentSoft" : "SurfaceAlt");
        if (file.CanSelect)
        {
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) is not null) return;
                check.IsChecked = check.IsChecked != true;
                row.SetResourceReference(Border.BackgroundProperty, check.IsChecked == true ? "AccentSoft" : "SurfaceAlt");
            };
        }
        else row.Opacity = 0.75;
        return row;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var node = child; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is T found) return found;
        return null;
    }

    // ───────────────────────── action bar and cleanup ─────────────────────────

    private List<FoundFile> SelectedFiles() => _selected.Select(p => _byPath.TryGetValue(p, out var f) ? f : null).Where(f => f is { CanSelect: true }).Cast<FoundFile>().ToList();

    private void UpdateActionBar()
    {
        var files = SelectedFiles();
        if (files.Count == 0 || _view is "programs" or "settings")
        {
            _actionBar.Visibility = Visibility.Collapsed;
            return;
        }
        var bar = new DockPanel();
        var sim = _settings.Simulation;
        var summary = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        summary.Children.Add(Ui.Text(Loc.F("sh.bar.selected", files.Count.ToString("N0", Loc.Culture)), 13, bold: true));
        summary.Children.Add(Ui.Text(Loc.F(sim ? "sh.bar.wouldFree" : "sh.bar.free", Bytes.Format(files.Sum(f => f.Size))), 12.5, "AccentBrush", bold: true));
        bar.Children.Add(summary);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var recycle = Ui.Button("ActionButton", "  " + Loc.T("sh.action.recycle"), (_, _) => Confirm(CleanAction.Recycle));
        recycle.Height = 38;
        recycle.Padding = new Thickness(16, 0, 16, 0);
        recycle.Margin = new Thickness(0, 4, 8, 4);
        recycle.FontFamily = new FontFamily("Segoe UI, Segoe Fluent Icons, Segoe MDL2 Assets");
        AutomationProperties.SetName(recycle, Loc.T("sh.action.recycle"));
        buttons.Children.Add(recycle);
        buttons.Children.Add(SmallButton(Loc.T("sh.action.move"), () => Confirm(CleanAction.Move)));
        buttons.Children.Add(SmallButton(Loc.T("sh.action.archive"), () => Confirm(CleanAction.Archive)));
        var delete = SmallButton("⚠ " + Loc.T("sh.action.delete"), () => Confirm(CleanAction.Delete));
        delete.Foreground = DangerBrush;
        delete.ToolTip = Loc.T("sh.action.deleteHelp");
        buttons.Children.Add(delete);
        bar.Children.Add(buttons);
        var card = Ui.Card(bar, 12);
        if (sim) card.Child = new StackPanel { Children = { Ui.Text("🧪 " + Loc.T("sh.bar.simulation"), 11.5, "OrangeBrush", bold: true), bar } };
        _actionBar.Child = card;
        _actionBar.Visibility = Visibility.Visible;
    }

    /// <summary>Ticks files (tests and keyboard users); unselectable ones are ignored.</summary>
    internal void SelectPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths) if (_byPath.TryGetValue(path, out var file) && file.CanSelect) _selected.Add(path);
        RebuildTabs();
        Rebuild();
    }

    internal void ShowView(string view) => Show(view);

    internal void OpenFindings(Finding finding) => OpenList(finding);

    internal bool OverlayOpen => _overlay.Visibility == Visibility.Visible;

    internal void Confirm(CleanAction action)
    {
        var files = SelectedFiles();
        if (files.Count == 0) return;
        string? target = null;
        if (action == CleanAction.Move)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("sh.move.pick") };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            target = dialog.FolderName;
            if (files.Any(f => FileRules.IsUnder(target, f.Folder) && string.Equals(f.Folder, target, StringComparison.OrdinalIgnoreCase))) target = Path.Combine(target, "SpaceHelper");
        }
        else if (action == CleanAction.Archive)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"SpaceHelper-Archiv-{DateTime.Now:yyyy-MM-dd}.zip", DefaultExt = ".zip", Filter = "ZIP|*.zip", OverwritePrompt = false };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            target = dialog.FileName;
            if (File.Exists(target))
            {
                ShowMessage(Loc.T("sh.archive.exists"), error: true);
                return;
            }
        }

        var size = files.Sum(f => f.Size);
        var sim = _settings.Simulation;
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.T("sh.confirm." + action.ToString().ToLowerInvariant()), 20, bold: true));
        if (sim) stack.Children.Add(Ui.Text("🧪 " + Loc.T("sh.confirm.simulation"), 12.5, "OrangeBrush", bold: true));
        var big = Ui.Text(Loc.F(action == CleanAction.Move ? "sh.confirm.moveSize" : "sh.confirm.free", Bytes.Format(size)), 26, "AccentBrush", bold: true);
        big.Margin = new Thickness(0, 10, 0, 2);
        stack.Children.Add(big);
        var safe = files.Count(f => f.Safety == Safety.Safe);
        stack.Children.Add(Ui.Text(Loc.F("sh.confirm.counts", files.Count.ToString("N0", Loc.Culture), safe.ToString("N0", Loc.Culture), (files.Count - safe).ToString("N0", Loc.Culture)), 12.5, "TextSecondary"));
        var consequence = Ui.Text(Loc.T("sh.consequence." + action.ToString().ToLowerInvariant()) + (target is null ? "" : "\n" + target), 12.5, action == CleanAction.Delete ? "OrangeBrush" : "TextSecondary");
        consequence.TextWrapping = TextWrapping.Wrap;
        consequence.TextTrimming = TextTrimming.None;
        consequence.Margin = new Thickness(0, 10, 0, 10);
        stack.Children.Add(consequence);
        if (files.Any(f => f.Finding == Finding.BrowserCache)) stack.Children.Add(Info(Loc.T("sh.consequence.browser"), "TextSecondary"));
        if (files.Any(f => f.Finding == Finding.Thumbnails)) stack.Children.Add(Info(Loc.T("sh.consequence.thumbnails"), "TextSecondary"));

        var list = new StackPanel();
        foreach (var file in files.OrderByDescending(f => f.Size).Take(8))
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var s = Ui.Text(Bytes.Format(file.Size), 12, "TextSecondary");
            DockPanel.SetDock(s, Dock.Right);
            row.Children.Add(s);
            var n = Ui.Text(file.Path, 12);
            n.ToolTip = file.Path;
            row.Children.Add(n);
            list.Children.Add(row);
        }
        if (files.Count > 8) list.Children.Add(Ui.Text(Loc.F("sh.confirm.more", (files.Count - 8).ToString("N0", Loc.Culture)), 12, "TextMuted"));
        var listCard = new Border { Child = list, Padding = new Thickness(10), CornerRadius = new CornerRadius(10) };
        listCard.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        stack.Children.Add(listCard);

        var restore = _settings.RestorePointBeforeLargeCleanup || size > 5L * 1024 * 1024 * 1024;
        var understood = action != CleanAction.Delete;
        Button? go = null;
        if (!sim && action != CleanAction.Move)
        {
            var restoreToggle = Chip((restore ? "☑  " : "☐  ") + Loc.T("sh.confirm.restore"), restore, () => { });
            restoreToggle.Margin = new Thickness(0, 12, 0, 0);
            restoreToggle.ToolTip = Loc.T("sh.confirm.restoreHelp");
            restoreToggle.Click += (_, _) =>
            {
                restore = !restore;
                restoreToggle.Content = (restore ? "☑  " : "☐  ") + Loc.T("sh.confirm.restore");
                Ui.Select(restoreToggle, restore);
            };
            stack.Children.Add(restoreToggle);
        }
        if (action == CleanAction.Delete && !sim)
        {
            var warn = Chip("☐  " + Loc.T("sh.confirm.understand"), false, () => { });
            warn.Foreground = DangerBrush;
            warn.Margin = new Thickness(0, 8, 0, 0);
            warn.Click += (_, _) =>
            {
                understood = !understood;
                warn.Content = (understood ? "☑  " : "☐  ") + Loc.T("sh.confirm.understand");
                if (go is not null) go.IsEnabled = understood;
            };
            stack.Children.Add(warn);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(SmallButton(Loc.T("sh.cancel"), CloseOverlay));
        go = Ui.Button("ActionButton", sim ? Loc.T("sh.confirm.simulate") : Loc.T("sh.confirm.go." + action.ToString().ToLowerInvariant()), (_, _) => _ = RunCleanupAsync(files, action, target, sim, restore && !sim));
        go.Height = 38;
        go.Padding = new Thickness(18, 0, 18, 0);
        go.Margin = new Thickness(8, 0, 0, 0);
        go.IsEnabled = understood || sim;
        if (action == CleanAction.Delete && !sim) go.Background = DangerBrush;
        buttons.Children.Add(go);
        stack.Children.Add(buttons);
        ShowOverlay(stack);
        go.Focus();
    }

    private async Task RunCleanupAsync(List<FoundFile> files, CleanAction action, string? target, bool simulate, bool restorePoint)
    {
        _work = new CancellationTokenSource();
        var progress = 0.0;
        var text = Ui.Text(Loc.T(restorePoint ? "sh.run.restore" : "sh.run.working"), 15, bold: true);
        var track = new Grid { Height = 10, Margin = new Thickness(0, 12, 0, 12), Width = 420 };
        var back = new Border { CornerRadius = new CornerRadius(5) };
        back.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        var fill = new Border { CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        track.Children.Add(back);
        track.Children.Add(fill);
        var cancel = SmallButton(Loc.T("sh.cancel"), () => _work?.Cancel());
        ShowOverlay(new StackPanel { Children = { text, track, cancel } });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => fill.Width = track.Width * progress;
        timer.Start();
        try
        {
            if (restorePoint)
            {
                var ok = await RestorePoint.CreateAsync("SpaceHelper Bereinigung");
                if (!ok)
                {
                    ShowMessage(Loc.T("sh.run.restoreFailed"), error: true);
                    return;
                }
                text.Text = Loc.T("sh.run.working");
            }
            var report = await _cleaner.RunAsync(files, action, target, simulate, new Progress<double>(p => progress = p), _work.Token);
            ShowReport(report);
            if (!simulate)
            {
                var gone = new HashSet<string>(files.Select(f => f.Path).Except(report.Failed.Select(f => f.Path)), StringComparer.OrdinalIgnoreCase);
                RemoveFromResults(gone);
                RefreshDrives();
            }
        }
        finally
        {
            timer.Stop();
            _work.Dispose();
            _work = null;
        }
    }

    private void RemoveFromResults(HashSet<string> gone)
    {
        foreach (var area in _areas) area.Files.RemoveAll(f => gone.Contains(f.Path));
        _scan?.Large.RemoveAll(f => gone.Contains(f.Path));
        _scan?.Stale.RemoveAll(f => gone.Contains(f.Path));
        _duplicates = _duplicates.Select(g => g with { Files = g.Files.Where(f => !gone.Contains(f.Path)).ToList() }).Where(g => g.Files.Count >= 2).ToList();
        _selected.ExceptWith(gone);
        IndexFiles();
    }

    private void ShowReport(CleanReport report)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.T(report.Simulated ? "sh.report.simTitle" : report.Canceled ? "sh.report.canceled" : "sh.report.title"), 20, bold: true));
        var big = Ui.Text(Loc.F(report.Simulated ? "sh.report.wouldFree" : report.Action == CleanAction.Move ? "sh.report.moved" : "sh.report.freed", Bytes.Format(report.FreedBytes)), 26, "AccentBrush", bold: true);
        big.Margin = new Thickness(0, 8, 0, 4);
        stack.Children.Add(big);
        stack.Children.Add(Ui.Text(Loc.F("sh.report.done", report.Done.ToString("N0", Loc.Culture)), 13, "TextSecondary"));
        if (report.ArchivePath is not null) stack.Children.Add(Info(Loc.F("sh.report.archive", report.ArchivePath), "TextSecondary"));
        if (report.Failed.Count > 0)
        {
            stack.Children.Add(Info(Loc.F("sh.report.failed", report.Failed.Count.ToString("N0", Loc.Culture)), "OrangeBrush", 12.5));
            foreach (var group in report.Failed.GroupBy(f => f.Reason))
                stack.Children.Add(Info($"• {Loc.T("sh.skip." + group.Key.ToString().ToLowerInvariant())}: {group.Count()}", "TextSecondary"));
        }
        if (report.Action is CleanAction.Recycle or CleanAction.Archive && !report.Simulated && report.Done > 0) stack.Children.Add(Info(Loc.T("sh.report.recycleHint")));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(SmallButton(Loc.T("sh.report.log"), () => OpenInExplorer(_cleaner.LogPath, select: true)));
        var ok = Ui.Button("ActionButton", "OK", (_, _) => { CloseOverlay(); Rebuild(); });
        ok.Height = 36;
        ok.Padding = new Thickness(22, 0, 22, 0);
        ok.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);
        ShowOverlay(stack);
    }

    private void ConfirmEmptyRecycleBin()
    {
        _recycle = RecycleBin.Query();
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.T("sh.recycle.title"), 20, bold: true));
        var big = Ui.Text(Loc.F("sh.confirm.free", Bytes.Format(_recycle.Size)), 26, "AccentBrush", bold: true);
        big.Margin = new Thickness(0, 10, 0, 4);
        stack.Children.Add(big);
        stack.Children.Add(Ui.Text(Loc.F("sh.tile.files", _recycle.Items.ToString("N0", Loc.Culture)), 12.5, "TextSecondary"));
        var warn = Ui.Text(Loc.T("sh.recycle.warning"), 12.5, "OrangeBrush");
        warn.TextWrapping = TextWrapping.Wrap;
        warn.TextTrimming = TextTrimming.None;
        warn.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(warn);
        if (_settings.Simulation) stack.Children.Add(Info("🧪 " + Loc.T("sh.confirm.simulation"), "OrangeBrush"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(SmallButton(Loc.T("sh.cancel"), CloseOverlay));
        var go = Ui.Button("ActionButton", Loc.T(_settings.Simulation ? "sh.confirm.simulate" : "sh.recycle.empty"), (_, _) =>
        {
            if (_settings.Simulation)
            {
                ShowMessage(Loc.F("sh.report.wouldFree", Bytes.Format(_recycle.Size)));
                return;
            }
            var handle = new WindowInteropHelper(Window.GetWindow(this)!).Handle;
            var ok = RecycleBin.Empty(handle);
            var freed = _recycle.Size;
            _recycle = RecycleBin.Query();
            ShowMessage(ok ? Loc.F("sh.report.freed", Bytes.Format(Math.Max(0, freed - _recycle.Size))) : Loc.T("sh.recycle.failed"), !ok);
            RefreshDrives();
        });
        go.Height = 38;
        go.Padding = new Thickness(18, 0, 18, 0);
        go.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(go);
        stack.Children.Add(buttons);
        ShowOverlay(stack);
    }

    private void ShowMessage(string text, bool error = false)
    {
        var stack = new StackPanel();
        var message = Ui.Text(text, 15, error ? "OrangeBrush" : "TextPrimary", bold: true);
        message.TextWrapping = TextWrapping.Wrap;
        message.TextTrimming = TextTrimming.None;
        stack.Children.Add(message);
        var ok = Ui.Button("ActionButton", "OK", (_, _) => { CloseOverlay(); Rebuild(); });
        ok.Height = 36;
        ok.Padding = new Thickness(22, 0, 22, 0);
        ok.Margin = new Thickness(0, 16, 0, 0);
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        stack.Children.Add(ok);
        ShowOverlay(stack);
    }

    private void ShowOverlay(UIElement content)
    {
        var card = Ui.Card(content, 22);
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.35 };
        _overlayHost.Child = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        _overlay.Visibility = Visibility.Visible;
        Motion.PopIn(card);
    }

    private void CloseOverlay()
    {
        _overlay.Visibility = Visibility.Collapsed;
        _overlayHost.Child = null;
    }

    // ───────────────────────── programs ─────────────────────────

    private async Task LoadProgramsAsync()
    {
        _programs = await Task.Run(ProgramInventory.Read);
        if (_view is "programs" or "overview") Rebuild();
    }

    private void BuildPrograms()
    {
        _content.Children.Add(Section(Loc.T("sh.programs.title")));
        var intro = Info(Loc.T("sh.programs.intro"), "TextSecondary", 12.5);
        _content.Children.Add(intro);
        var settings = SmallButton(Loc.T("sh.programs.settings"), ProgramInventory.OpenWindowsSettings);
        settings.HorizontalAlignment = HorizontalAlignment.Left;
        settings.Margin = new Thickness(0, 0, 0, 10);
        _content.Children.Add(settings);
        if (_programs is null)
        {
            _content.Children.Add(Info(Loc.T("sh.programs.loading")));
            return;
        }
        var now = DateTime.UtcNow;
        foreach (var program in _programs.Where(p => _search.Length == 0 || p.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)).Take(400))
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            info.Children.Add(Ui.Text(program.Name, 13.5, bold: true));
            var details = new List<string>();
            if (program.Publisher.Length > 0) details.Add(program.Publisher);
            if (program.Version.Length > 0) details.Add(program.Version);
            if (program.Size > 0) details.Add(Bytes.Format(program.Size));
            if (program.Installed is { } installed) details.Add(Loc.F("sh.programs.installed", installed.ToString("d", Loc.Culture)));
            info.Children.Add(Ui.Text(string.Join(" · ", details), 11.5, "TextMuted"));
            if (program.DaysUnchanged(now) is { } days && days > 180)
                info.Children.Add(Ui.Text(Loc.F("sh.programs.unused", days.ToString("N0", Loc.Culture)), 11.5, "OrangeBrush"));
            grid.Children.Add(info);
            var uninstall = SmallButton(Loc.T("sh.programs.uninstall"), () => ConfirmUninstall(program));
            uninstall.VerticalAlignment = VerticalAlignment.Center;
            SetColumn(uninstall, 1);
            grid.Children.Add(uninstall);
            var row = new Border { Child = grid, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 4, 0, 0), CornerRadius = new CornerRadius(12) };
            row.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
            _content.Children.Add(row);
        }
    }

    private void ConfirmUninstall(InstalledProgram program)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.F("sh.uninstall.title", program.Name), 18, bold: true));
        var text = Ui.Text(Loc.T("sh.uninstall.text"), 12.5, "TextSecondary");
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        text.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(text);
        stack.Children.Add(Info(program.UninstallCommand));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(SmallButton(Loc.T("sh.cancel"), CloseOverlay));
        var go = Ui.Button("ActionButton", Loc.T("sh.uninstall.go"), (_, _) =>
        {
            if (_settings.Simulation)
            {
                ShowMessage("🧪 " + Loc.T("sh.uninstall.simulated"));
                return;
            }
            var ok = ProgramInventory.StartUninstall(program);
            ShowMessage(ok ? Loc.T("sh.uninstall.started") : Loc.T("sh.uninstall.failed"), !ok);
            _programs = null;
        });
        go.Height = 38;
        go.Padding = new Thickness(18, 0, 18, 0);
        go.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(go);
        stack.Children.Add(buttons);
        ShowOverlay(stack);
    }

    // ───────────────────────── settings ─────────────────────────

    private void BuildSettings()
    {
        var stack = new StackPanel();
        void Choice<T>(string key, IEnumerable<T> values, T current, Func<T, string> text, Action<T> set)
        {
            stack.Children.Add(Heading(Loc.T(key)));
            var chips = new WrapPanel();
            foreach (var value in values)
                chips.Children.Add(Chip(text(value), EqualityComparer<T>.Default.Equals(value, current), () => { set(value); SaveSettings(); Rebuild(); }));
            stack.Children.Add(chips);
        }
        Choice("sh.set.large", new[] { 50, 100, 250, 500, 1000 }, _settings.LargeFileMb, v => Loc.F("sh.set.mb", v), v => _settings.LargeFileMb = v);
        Choice("sh.set.stale", new[] { 180, 365, 730, 1095 }, _settings.StaleDays, v => Loc.F("sh.set.days", v), v => _settings.StaleDays = v);
        Choice("sh.set.downloads", new[] { 30, 90, 180, 365 }, _settings.DownloadDays, v => Loc.F("sh.set.days", v), v => _settings.DownloadDays = v);
        Choice("sh.set.duplicates", new[] { 100, 1024, 10240, 102400 }, _settings.DuplicateMinKb, v => Bytes.Format(v * 1024L), v => _settings.DuplicateMinKb = v);
        stack.Children.Add(Heading(Loc.T("sh.set.options")));
        stack.Children.Add(Toggle(Loc.T("sh.set.browser"), _settings.IncludeBrowserCache, v => { _settings.IncludeBrowserCache = v; SaveSettings(); Rebuild(); }, Loc.T("sh.set.browserHelp")));
        stack.Children.Add(Toggle(Loc.T("sh.simulation"), _settings.Simulation, v => { _settings.Simulation = v; SaveSettings(); RebuildTabs(); Rebuild(); }, Loc.T("sh.simulation.help")));
        stack.Children.Add(Toggle(Loc.T("sh.set.dupContent"), _settings.DuplicatesByContentOnly, v => { _settings.DuplicatesByContentOnly = v; SaveSettings(); Rebuild(); }, Loc.T("sh.set.dupContentHelp")));
        stack.Children.Add(Toggle(Loc.T("sh.set.restore"), _settings.RestorePointBeforeLargeCleanup, v => { _settings.RestorePointBeforeLargeCleanup = v; SaveSettings(); Rebuild(); }, Loc.T("sh.confirm.restoreHelp")));

        stack.Children.Add(Heading(Loc.T("sh.set.custom")));
        foreach (var folder in _settings.CustomFolders.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = Ui.Button("IconButton", "✕", (_, _) => { _settings.CustomFolders.Remove(folder); SaveSettings(); Rebuild(); }, Loc.T("sh.set.remove"));
            remove.Width = 28;
            remove.Height = 28;
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var protection = FileRules.ProtectionReason(Path.Combine(folder, "x"), _paths);
            row.Children.Add(Ui.Text(folder + (protection is null ? "" : "  ·  " + Loc.T(protection)), 12.5, protection is null ? "TextPrimary" : "OrangeBrush"));
            stack.Children.Add(row);
        }
        var add = SmallButton(Loc.T("sh.set.addFolder"), () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("sh.set.addFolder") };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true && !_settings.CustomFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
            {
                _settings.CustomFolders.Add(dialog.FolderName);
                _settings.Normalize();
                SaveSettings();
                Rebuild();
            }
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(add);

        stack.Children.Add(Heading(Loc.T("sh.set.log")));
        stack.Children.Add(Info(Loc.F("sh.set.logText", _cleaner.LogPath)));
        var log = SmallButton(Loc.T("sh.report.log"), () => OpenInExplorer(_cleaner.LogPath, select: true));
        log.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(log);
        _content.Children.Add(Ui.Card(stack, 18));
        _content.Children.Add(SafetyExplainer());
    }

    // ───────────────────────── helpers ─────────────────────────

    private static void OpenInExplorer(string path, bool select)
    {
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            if (select && File.Exists(path)) info.ArgumentList.Add("/select," + path);
            else info.ArgumentList.Add(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
            Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Explorer missing.
        }
    }

    private static Border SafetyBadge(Safety safety)
    {
        var (brush, key) = safety switch
        {
            Safety.Safe => (SafeColor, "sh.safety.safe"),
            Safety.Review => (ReviewColor, "sh.safety.review"),
            _ => (DangerBrush, "sh.safety.keep")
        };
        var color = ((SolidColorBrush)brush).Color;
        var text = new TextBlock { Text = Loc.T(key), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = brush };
        return new Border { Background = Frozen(color, 40), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 2, 7, 2), Child = text };
    }

    private static TextBlock Glyph(string glyph, double size)
    {
        var text = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        return text;
    }

    private static TextBlock Section(string text)
    {
        var block = Ui.Text(text, 17, bold: true);
        block.Margin = new Thickness(0, 14, 0, 10);
        return block;
    }

    private static TextBlock Info(string text, string brush = "TextMuted", double size = 11.5)
    {
        var block = Ui.Text(text, size, brush);
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        block.Margin = new Thickness(0, 6, 0, 6);
        return block;
    }

    private static TextBlock Heading(string text)
    {
        var block = Ui.Text(text.ToUpper(Loc.Culture), 10.5, "TextMuted", bold: true);
        block.Margin = new Thickness(0, 12, 0, 6);
        return block;
    }

    private static Button Chip(string text, bool selected, Action click)
    {
        var chip = Ui.Button("ChipButton", text, (_, _) => click());
        chip.Height = 32;
        chip.Margin = new Thickness(0, 0, 6, 6);
        chip.Padding = new Thickness(12, 0, 12, 0);
        Ui.Select(chip, selected);
        AutomationProperties.SetName(chip, text);
        AutomationProperties.SetItemStatus(chip, selected ? Loc.T("ac.selected") : "");
        return chip;
    }

    private static Button Toggle(string text, bool value, Action<bool> set, string? tooltip = null)
    {
        var chip = Ui.Button("ChipButton", (value ? "☑  " : "☐  ") + text, (_, _) => set(!value), tooltip);
        chip.Height = 34;
        chip.Margin = new Thickness(0, 0, 6, 6);
        chip.Padding = new Thickness(12, 0, 12, 0);
        chip.HorizontalAlignment = HorizontalAlignment.Left;
        Ui.Select(chip, value);
        AutomationProperties.SetName(chip, text);
        AutomationProperties.SetItemStatus(chip, value ? Loc.T("wx.on") : Loc.T("wx.off"));
        return chip;
    }

    private static Button SmallButton(string text, Action click)
    {
        var button = Ui.Button("SecondaryButton", text, (_, _) => click());
        button.Height = 34;
        button.Padding = new Thickness(14, 0, 14, 0);
        button.Margin = new Thickness(0, 4, 8, 4);
        AutomationProperties.SetName(button, text);
        return button;
    }

    private static Brush Frozen(Color color, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
