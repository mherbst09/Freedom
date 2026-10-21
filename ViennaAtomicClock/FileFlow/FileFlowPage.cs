using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ViennaAtomicClock.FileFlow;

/// <summary>Saved choices of the FileFlow page.</summary>
public sealed class FileFlowSettings
{
    public int AudioBitrate { get; set; } = 192;
    public int VideoHeight { get; set; }
    public VideoQuality VideoQuality { get; set; } = VideoQuality.Medium;
    public int ImageQuality { get; set; } = 90;
    public int ImageMaxSide { get; set; }
    public PdfPageSize PageSize { get; set; } = PdfPageSize.A4;
    public bool MergeImages { get; set; } = true;

    public ConversionOptions ToOptions() => new()
    {
        AudioBitrate = ConversionOptions.AudioBitrates.Contains(AudioBitrate) ? AudioBitrate : 192,
        VideoHeight = ConversionOptions.VideoHeights.Contains(VideoHeight) ? VideoHeight : 0,
        VideoQuality = VideoQuality,
        ImageQuality = ConversionOptions.ImageQualities.Contains(ImageQuality) ? ImageQuality : 90,
        ImageMaxSide = ConversionOptions.ImageSizes.Contains(ImageMaxSide) ? ImageMaxSide : 0,
        PageSize = PageSize,
        MergeImages = MergeImages
    };
}

/// <summary>
/// FileFlow – converts files on this PC: big drop area, link box for own/free YouTube and SoundCloud audio, options
/// (audio, video, image, PDF), a list with preview, progress, cancel, save and ZIP, and the conversion catalogue as cards.
/// </summary>
internal sealed class FileFlowPage : Grid
{
    private const string SettingsFile = "fileflow.json";
    private const double WideWidth = 980;
    private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));

    private readonly FileFlowService _service;
    private readonly FileFlowSettings _settings;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
    private readonly StackPanel _content = new() { MaxWidth = 1180 };
    private readonly Grid _top = new();
    private readonly Border _dropZone = new();
    private readonly Rectangle _dropFrame = new() { StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 6, 4 }, RadiusX = 24, RadiusY = 24, IsHitTestVisible = false };
    private readonly StackPanel _selectionHost = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _optionsCard = new();
    private readonly Border _linkCard = new();
    private readonly Border _jobsCard = new();
    private readonly StackPanel _jobList = new();
    private readonly TextBlock _jobsTitle = Ui.Text("", 15, bold: true);
    private readonly TextBlock _notice = Ui.Text("", 12, "TextSecondary");
    private readonly StackPanel _footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly WrapPanel _cards = new();
    private readonly WrapPanel _categoryChips = new();
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Dictionary<Guid, RowParts> _rows = new();
    private TextBox? _linkBox;
    private bool _rightsConfirmed;
    private Conversion? _selected;
    private FormatCategory? _category;
    private string _search = "";
    private string _layout = "";

    /// <summary>Raised when the header line of the main window should change.</summary>
    public event Action? Changed;

    public FileFlowPage() : this(new FileFlowService(), DataFile.Load<FileFlowSettings>(SettingsFile)) { }

    public FileFlowPage(FileFlowService service, FileFlowSettings settings)
    {
        _service = service;
        _settings = settings;
        _service.JobChanged += job => Dispatcher.BeginInvoke(() => OnJobChanged(job));
        _service.ListChanged += () => Dispatcher.BeginInvoke(RebuildJobs);

        _scroll.Content = _content;
        Children.Add(_scroll);
        AllowDrop = true;
        Background = Brushes.Transparent; // drops anywhere on the page
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => Highlight(false);
        Drop += OnDrop;
        SizeChanged += (_, _) => ApplyLayout();
        IsVisibleChanged += (_, _) => { if (IsVisible) { ToolLocator.Refresh(); Build(); } };
        Loc.Changed += Build; // removed again in Shutdown
        _tick.Tick += (_, _) => UpdateProgress();
        Build();
    }

    public FileFlowService Service => _service;
    internal Conversion? Selected => _selected;
    internal string LayoutMode => _layout;

    public string StatusLine
    {
        get
        {
            var jobs = _service.Jobs;
            var running = jobs.Count(j => j.IsActive);
            if (running > 0) return Loc.F("ff.header.running", running);
            var done = jobs.Count(j => j.State == JobState.Done);
            return done > 0 ? Loc.F("ff.header.done", done) : Loc.T("ff.header.ready");
        }
    }

    public void Shutdown()
    {
        Loc.Changed -= Build;
        _tick.Stop();
        DataFile.Save(SettingsFile, _settings);
        _service.Dispose();
    }

    // ───────────────────────── layout ─────────────────────────

    private void Build()
    {
        _content.Children.Clear();
        _content.Children.Add(Hero());
        _content.Children.Add(PrivacyNote());
        BuildDropZone();
        BuildLinkCard();
        BuildOptions();
        _top.Margin = new Thickness(0, 14, 0, 0);
        _content.Children.Add(_top);
        BuildJobsCard();
        _content.Children.Add(_jobsCard);
        _content.Children.Add(Catalog());
        _layout = "";
        ApplyLayout();
        RebuildJobs();
    }

    /// <summary>Wide: drop zone and link box left, options right. Narrow: everything below each other.</summary>
    private void ApplyLayout()
    {
        var mode = ActualWidth >= WideWidth || ActualWidth == 0 ? "wide" : "narrow";
        if (mode == _layout) return;
        _layout = mode;
        _top.Children.Clear();
        _top.ColumnDefinitions.Clear();
        _top.RowDefinitions.Clear();
        var left = new StackPanel();
        Detach(_dropZone);
        Detach(_linkCard);
        Detach(_optionsCard);
        left.Children.Add(_dropZone);
        left.Children.Add(_linkCard);
        if (mode == "wide")
        {
            _top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
            _top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            _top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
            _top.Children.Add(left);
            SetColumn(_optionsCard, 2);
            _optionsCard.Margin = new Thickness(0);
            _top.Children.Add(_optionsCard);
        }
        else
        {
            SetColumn(_optionsCard, 0);
            _optionsCard.Margin = new Thickness(0, 14, 0, 0);
            left.Children.Add(_optionsCard);
            _top.Children.Add(left);
        }
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.Parent is Panel panel) panel.Children.Remove(element);
    }

    private UIElement Hero()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        var name = Ui.Text("FileFlow", 30, bold: true);
        name.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        title.Children.Add(name);
        var tagline = Ui.Text(Loc.T("ff.tagline"), 14, "TextSecondary");
        tagline.TextWrapping = TextWrapping.Wrap;
        title.Children.Add(tagline);
        grid.Children.Add(title);

        var tools = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 380 };
        foreach (var (tool, label, install) in new[]
                 {
                     (ExternalTool.FFmpeg, "FFmpeg", "winget install Gyan.FFmpeg"),
                     (ExternalTool.LibreOffice, "LibreOffice", "winget install TheDocumentFoundation.LibreOffice"),
                     (ExternalTool.YtDlp, "yt-dlp", "winget install yt-dlp.yt-dlp")
                 })
        {
            var ok = ToolLocator.IsAvailable(tool);
            var chip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 6) };
            chip.SetResourceReference(Border.BackgroundProperty, ok ? "GreenSoft" : "SurfaceAlt");
            var text = Ui.Text((ok ? "✓ " : "✕ ") + label, 11.5, ok ? "GreenBrush" : "TextMuted", bold: true);
            chip.Child = text;
            chip.ToolTip = ok ? Loc.F("ff.tool.ok", label) : Loc.F("ff.tool.missing", label, install);
            AutomationProperties.SetName(chip, chip.ToolTip as string);
            tools.Children.Add(chip);
        }
        SetColumn(tools, 1);
        grid.Children.Add(tools);
        return grid;
    }

    private UIElement PrivacyNote()
    {
        var row = new DockPanel();
        var icon = Ui.Text("🕒", 16);
        icon.FontFamily = new FontFamily("Segoe UI Emoji");
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var text = Ui.Text(Loc.F("ff.privacy", (int)FileFlowService.Retention.TotalMinutes), 12.5, "TextSecondary");
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        row.Children.Add(text);
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 12, 0, 0), Child = row };
        card.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        return card;
    }

    // ───────────────────────── drop zone and link ─────────────────────────

    private void BuildDropZone()
    {
        Detach(_selectionHost);
        Detach(_dropFrame);
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 26, 20, 26) };
        var circle = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), HorizontalAlignment = HorizontalAlignment.Center };
        circle.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        var arrow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 16,4 L 16,22 M 8,12 L 16,4 L 24,12 M 4,22 L 4,27 Q 4,29 6,29 L 26,29 Q 28,29 28,27 L 28,22"),
            StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        arrow.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        circle.Child = arrow;
        stack.Children.Add(circle);
        var title = Ui.Text(Loc.T("ff.drop.title"), 20, bold: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 12, 0, 2);
        stack.Children.Add(title);
        var or = Ui.Text(Loc.T("ff.drop.or"), 12.5, "TextMuted");
        or.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(or);
        var pick = Ui.Button("ActionButton", Loc.T("ff.drop.pick"), (_, _) => PickFiles());
        pick.Height = 42;
        pick.Padding = new Thickness(22, 0, 22, 0);
        pick.Margin = new Thickness(0, 10, 0, 10);
        pick.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetName(pick, Loc.T("ff.drop.pick"));
        stack.Children.Add(pick);
        var hint = Ui.Text(Loc.F("ff.drop.hint", FileFlowService.FormatSize(FileFlowService.MaxFileBytes)), 12, "TextMuted");
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextAlignment = TextAlignment.Center;
        hint.TextTrimming = TextTrimming.None;
        stack.Children.Add(hint);
        _selectionHost.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_selectionHost);
        UpdateSelectionChip();

        var layers = new Grid();
        layers.Children.Add(stack);
        _dropFrame.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        _dropFrame.Opacity = 0.55;
        layers.Children.Add(_dropFrame);
        _dropZone.Child = layers;
        _dropZone.CornerRadius = new CornerRadius(24);
        _dropZone.MinHeight = 240;
        _dropZone.Cursor = Cursors.Hand;
        _dropZone.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _dropZone.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Button) PickFiles(); };
        AutomationProperties.SetName(_dropZone, Loc.T("ff.drop.title"));
    }

    private void UpdateSelectionChip()
    {
        _selectionHost.Children.Clear();
        if (_selected is null) return;
        var chip = Ui.Button("ChipButton", Loc.F("ff.drop.selected", _selected.Title) + "   ✕", (_, _) => Select(null));
        chip.Height = 32;
        chip.Padding = new Thickness(12, 0, 12, 0);
        Ui.Select(chip, true);
        chip.ToolTip = Loc.T("ff.drop.clearSelection");
        _selectionHost.Children.Add(chip);
    }

    private void BuildLinkCard()
    {
        var previous = _linkBox?.Text ?? "";
        var stack = new StackPanel();
        var head = new DockPanel();
        var title = Ui.Text(Loc.T("ff.link.title"), 15, bold: true);
        head.Children.Add(title);
        stack.Children.Add(head);
        var (host, box) = Ui.Input(Loc.T("ff.link.hint"));
        _linkBox = box;
        box.Text = previous;
        host.Margin = new Thickness(0, 8, 0, 8);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddLink(); };
        AutomationProperties.SetName(box, Loc.T("ff.link.hint"));
        stack.Children.Add(host);
        var rights = Ui.Button("ChipButton", (_rightsConfirmed ? "☑  " : "☐  ") + Loc.T("ff.link.rights"), (_, _) =>
        {
            _rightsConfirmed = !_rightsConfirmed;
            BuildLinkCard();
        });
        if (rights.Content is string label)
        {
            var text = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
            rights.Content = text;
        }
        rights.MinHeight = 34;
        rights.Height = double.NaN;
        rights.Padding = new Thickness(12, 7, 12, 7);
        rights.HorizontalContentAlignment = HorizontalAlignment.Left;
        rights.HorizontalAlignment = HorizontalAlignment.Stretch;
        Ui.Select(rights, _rightsConfirmed);
        AutomationProperties.SetName(rights, Loc.T("ff.link.rights"));
        AutomationProperties.SetItemStatus(rights, _rightsConfirmed ? Loc.T("wx.on") : Loc.T("wx.off"));
        stack.Children.Add(rights);
        var rules = Ui.Text(Loc.T("ff.link.rules"), 11.5, "TextMuted");
        rules.TextWrapping = TextWrapping.Wrap;
        rules.TextTrimming = TextTrimming.None;
        rules.Margin = new Thickness(0, 6, 0, 8);
        stack.Children.Add(rules);
        var add = Ui.Button("ActionButton", Loc.T("ff.link.add"), (_, _) => AddLink());
        add.Height = 38;
        add.Padding = new Thickness(18, 0, 18, 0);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.IsEnabled = _rightsConfirmed;
        stack.Children.Add(add);
        if (!ToolLocator.IsAvailable(ExternalTool.YtDlp))
        {
            var missing = Ui.Text(Loc.F("ff.tool.missing", "yt-dlp", "winget install yt-dlp.yt-dlp"), 11.5, "OrangeBrush");
            missing.TextWrapping = TextWrapping.Wrap;
            missing.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(missing);
        }
        _linkCard.Child = Ui.Card(stack, 16);
        _linkCard.Margin = new Thickness(0, 14, 0, 0);
    }

    private void AddLink()
    {
        if (_linkBox is null || string.IsNullOrWhiteSpace(_linkBox.Text)) return;
        var job = _service.AddLink(_linkBox.Text, _rightsConfirmed);
        if (job.State == JobState.Ready)
        {
            _linkBox.Text = "";
            _service.Start(job, _settings.ToOptions());
        }
        BringJobsIntoView();
    }

    // ───────────────────────── options ─────────────────────────

    private void BuildOptions()
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.T("ff.opt.title"), 15, bold: true));
        var relevant = RelevantCategories();
        void Group(string key, UIElement chips)
        {
            stack.Children.Add(Heading(Loc.T(key)));
            stack.Children.Add(chips);
        }
        WrapPanel Chips<T>(IEnumerable<T> values, T current, Func<T, string> text, Action<T> set)
        {
            var panel = new WrapPanel();
            foreach (var value in values)
            {
                var chip = Chip(text(value), EqualityComparer<T>.Default.Equals(value, current), () => { set(value); SaveSettings(); });
                panel.Children.Add(chip);
            }
            return panel;
        }

        if (relevant.Contains("audio"))
            Group("ff.opt.audio", Chips(ConversionOptions.AudioBitrates, _settings.AudioBitrate, b => Loc.F("ff.opt.kbps", b), b => _settings.AudioBitrate = b));
        if (relevant.Contains("video"))
        {
            Group("ff.opt.resolution", Chips(ConversionOptions.VideoHeights, _settings.VideoHeight, h => h == 0 ? Loc.T("ff.opt.original") : h + "p", h => _settings.VideoHeight = h));
            Group("ff.opt.videoQuality", Chips(Enum.GetValues<VideoQuality>(), _settings.VideoQuality, q => Loc.T("ff.opt.q." + q.ToString().ToLowerInvariant()), q => _settings.VideoQuality = q));
        }
        if (relevant.Contains("image"))
        {
            Group("ff.opt.imageQuality", Chips(ConversionOptions.ImageQualities, _settings.ImageQuality, q => q + " %", q => _settings.ImageQuality = q));
            Group("ff.opt.imageSize", Chips(ConversionOptions.ImageSizes, _settings.ImageMaxSide, s => s == 0 ? Loc.T("ff.opt.original") : Loc.F("ff.opt.px", s), s => _settings.ImageMaxSide = s));
        }
        if (relevant.Contains("pdf"))
        {
            Group("ff.opt.page", Chips(Enum.GetValues<PdfPageSize>(), _settings.PageSize, p => p == PdfPageSize.Auto ? Loc.T("ff.opt.page.auto") : p.ToString(), p => _settings.PageSize = p));
            var merge = Toggle(Loc.T("ff.opt.merge"), _settings.MergeImages, v => { _settings.MergeImages = v; SaveSettings(); });
            merge.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(merge);
        }
        var note = Ui.Text(relevant.Count == 4 ? Loc.T("ff.opt.allNote") : Loc.T("ff.opt.relevantNote"), 11.5, "TextMuted");
        note.TextWrapping = TextWrapping.Wrap;
        note.TextTrimming = TextTrimming.None;
        note.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(note);
        _optionsCard.Child = Ui.Card(stack, 16);
    }

    /// <summary>Option groups that matter for the files in the list (or the chosen card); all groups when nothing is chosen.</summary>
    private HashSet<string> RelevantCategories()
    {
        var pairs = _service.Jobs.Where(j => j.State == JobState.Ready || j.Kind == JobKind.Link && j.IsActive).Select(j => (j.Format, j.Target)).ToList();
        if (_selected is not null) pairs.AddRange(_selected.From.Select(f => (f, _selected.To)));
        var set = new HashSet<string>();
        foreach (var (from, to) in pairs)
        {
            if (to is FileFormat.Mp3 or FileFormat.M4a or FileFormat.Aac) set.Add("audio");
            if (to == FileFormat.Mp4) set.Add("video");
            if (to is FileFormat.Jpg or FileFormat.Png or FileFormat.Webp || (to == FileFormat.Pdf && FileFlowCatalog.IsImage(from))) set.Add("image");
            if (to == FileFormat.Pdf && (FileFlowCatalog.IsImage(from) || from == FileFormat.Txt)) set.Add("pdf");
        }
        return set.Count == 0 ? new HashSet<string> { "audio", "video", "image", "pdf" } : set;
    }

    private void SaveSettings()
    {
        DataFile.Save(SettingsFile, _settings);
        BuildOptions();
        RebuildJobs();
    }

    // ───────────────────────── files ─────────────────────────

    private void PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Loc.T("ff.drop.pick"), Filter = DialogFilter() };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddFiles(dialog.FileNames);
    }

    /// <summary>File dialog filter: the chosen card's formats first, then everything FileFlow understands.</summary>
    private string DialogFilter()
    {
        var all = FileFlowCatalog.Formats.Where(f => f.Extensions.Length > 0 && FileFlowCatalog.TargetsFor(f.Format).Count > 0).SelectMany(f => f.Extensions).Select(e => "*" + e);
        var filter = $"{Loc.T("ff.dialog.supported")}|{string.Join(";", all)}|{Loc.T("ff.dialog.all")}|*.*";
        if (_selected is { IsLink: false })
        {
            var own = _selected.From.SelectMany(f => FileFlowCatalog.Info(f).Extensions).Select(e => "*" + e);
            filter = $"{_selected.Title}|{string.Join(";", own)}|" + filter;
        }
        return filter;
    }

    internal void AddFiles(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Take(100));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Skip unreadable folders.
                }
            }
            else files.Add(path);
        }
        foreach (var file in files) _service.AddFile(file, _selected?.To ?? FileFormat.Unknown);
        BuildOptions();
        BringJobsIntoView();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        Highlight(files);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        Highlight(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths);
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text && MediaEngine.ClassifyLink(text) != FileFormat.Unknown && _linkBox is not null) _linkBox.Text = text.Trim();
        e.Handled = true;
    }

    private void Highlight(bool on)
    {
        _dropFrame.Opacity = on ? 1 : 0.55;
        _dropFrame.StrokeThickness = on ? 3 : 2;
        _dropZone.SetResourceReference(Border.BackgroundProperty, on ? "AccentSoft" : "SurfaceBrush");
    }

    private void BringJobsIntoView()
    {
        if (_service.Jobs.Count > 0) Dispatcher.BeginInvoke(() => _jobsCard.BringIntoView(), DispatcherPriority.Loaded);
    }

    // ───────────────────────── job list ─────────────────────────

    private sealed class RowParts
    {
        public required Border Fill;
        public required Grid Track;
        public required TextBlock Status;
        public JobState State;
    }

    private void BuildJobsCard()
    {
        foreach (var reused in new FrameworkElement[] { _jobsTitle, _jobList, _notice, _footer }) Detach(reused);
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(_jobsTitle);
        stack.Children.Add(head);
        stack.Children.Add(_jobList);
        _notice.TextWrapping = TextWrapping.Wrap;
        _notice.TextTrimming = TextTrimming.None;
        _notice.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_notice);
        _footer.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(_footer);
        Detach(_jobsCard);
        _jobsCard.Child = Ui.Card(stack, 16);
        _jobsCard.Margin = new Thickness(0, 14, 0, 0);
    }

    private void RebuildJobs()
    {
        var jobs = _service.Jobs;
        _jobsCard.Visibility = jobs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _jobsTitle.Text = Loc.F("ff.jobs.title", jobs.Count);
        _jobList.Children.Clear();
        _rows.Clear();
        var mergeMode = _settings.MergeImages && jobs.Count(IsMergeCandidate) >= 2;
        foreach (var job in jobs) _jobList.Children.Add(JobRow(job, mergeMode));

        _footer.Children.Clear();
        var ready = jobs.Count(j => j.State == JobState.Ready);
        var done = jobs.Count(j => j.State == JobState.Done);
        if (ready > 0) _footer.Children.Add(FooterButton("ActionButton", Loc.F("ff.jobs.startAll", ready), StartAll));
        if (done > 0) _footer.Children.Add(FooterButton("SecondaryButton", Loc.T("ff.jobs.zip"), SaveZip));
        _footer.Children.Add(FooterButton("SecondaryButton", Loc.T("ff.jobs.clear"), () => _service.Clear()));
        if (mergeMode) Notice(Loc.T("ff.jobs.mergeHint"));
        UpdateTimer();
        Changed?.Invoke();
    }

    private static bool IsMergeCandidate(FileFlowJob job) => job.State == JobState.Ready && job.Kind == JobKind.File && job.Target == FileFormat.Pdf && FileFlowCatalog.IsImage(job.Format);

    private Button FooterButton(string style, string text, Action click)
    {
        var button = Ui.Button(style, text, (_, _) => click());
        button.Height = 38;
        button.Padding = new Thickness(16, 0, 16, 0);
        button.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetName(button, text);
        return button;
    }

    private void StartAll()
    {
        _service.StartAll(_settings.ToOptions());
        BuildOptions();
    }

    private UIElement JobRow(FileFlowJob job, bool mergeMode)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Thumbnail(job));

        var info = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(job.DisplayName, 13.5, bold: true);
        name.ToolTip = job.Sources.Count == 1 ? job.Sources[0] : string.Join("\n", job.Sources.Select(System.IO.Path.GetFileName));
        info.Children.Add(name);
        info.Children.Add(Ui.Text(MetaLine(job), 11.5, "TextMuted"));
        var status = Ui.Text("", 11.5, "TextSecondary");
        status.TextWrapping = TextWrapping.Wrap;
        status.TextTrimming = TextTrimming.None;
        var track = new Grid { Height = 6, Margin = new Thickness(0, 6, 0, 2) };
        var trackBack = new Border { CornerRadius = new CornerRadius(3) };
        trackBack.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        track.Children.Add(trackBack);
        track.Children.Add(fill);

        if (job.CanChangeTarget)
        {
            var targets = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            targets.Children.Add(new TextBlock { Text = Loc.T("ff.jobs.to"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6), FontSize = 11.5, Foreground = (Brush)FindResource("TextMuted") });
            foreach (var target in job.PossibleTargets)
            {
                var chip = Chip(FileFlowCatalog.Info(target).Label, target == job.Target, () =>
                {
                    job.Target = target;
                    BuildOptions();
                    RebuildJobs();
                });
                chip.Height = 28;
                chip.Padding = new Thickness(10, 0, 10, 0);
                targets.Children.Add(chip);
            }
            info.Children.Add(targets);
        }
        else
        {
            info.Children.Add(track);
            info.Children.Add(status);
        }
        SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (job.State == JobState.Ready)
        {
            if (mergeMode && IsMergeCandidate(job))
            {
                actions.Children.Add(Icon("↑", Loc.T("ff.jobs.up"), () => _service.Move(job, -1)));
                actions.Children.Add(Icon("↓", Loc.T("ff.jobs.down"), () => _service.Move(job, 1)));
            }
            else actions.Children.Add(Small("ActionButton", Loc.T("ff.jobs.start"), () => _service.Start(job, _settings.ToOptions())));
        }
        if (job.IsActive) actions.Children.Add(Small("SecondaryButton", Loc.T("ff.jobs.cancel"), () => _service.Cancel(job)));
        if (job.State == JobState.Done) actions.Children.Add(Small("ActionButton", Loc.T("ff.jobs.save"), () => Save(job)));
        if (job.State is JobState.Failed or JobState.Canceled or JobState.Expired && job.Kind != JobKind.ImageBundle && CanRetry(job))
            actions.Children.Add(Small("SecondaryButton", Loc.T("ff.jobs.retry"), () => Retry(job)));
        if (!job.IsActive) actions.Children.Add(Icon("✕", Loc.T("ff.jobs.remove"), () => _service.Remove(job)));
        SetColumn(actions, 2);
        grid.Children.Add(actions);

        var row = new Border { Child = grid, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(14) };
        row.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        AutomationProperties.SetName(row, job.DisplayName);
        var parts = new RowParts { Fill = fill, Track = track, Status = status, State = job.State };
        _rows[job.Id] = parts;
        track.SizeChanged += (_, _) => UpdateRow(job, parts);
        UpdateRow(job, parts);
        return row;
    }

    private static bool CanRetry(FileFlowJob job) =>
        job.Kind == JobKind.Link ? MediaEngine.ClassifyLink(job.Sources[0]) != FileFormat.Unknown : job.Format is not (FileFormat.Unknown or FileFormat.Executable) && File.Exists(job.Sources[0]);

    private void Retry(FileFlowJob job)
    {
        var target = job.Target;
        _service.Remove(job);
        if (job.Kind == JobKind.Link) _service.AddLink(job.Sources[0], _rightsConfirmed);
        else _service.AddFile(job.Sources[0], target);
    }

    private string MetaLine(FileFlowJob job)
    {
        var from = FileFlowCatalog.Info(job.Format).Label;
        var to = FileFlowCatalog.Info(job.Target).Label;
        return job.Kind switch
        {
            JobKind.Link => Loc.F("ff.jobs.metaLink", from),
            JobKind.ImageBundle => Loc.F("ff.jobs.metaBundle", job.Sources.Count, FileFlowService.FormatSize(job.SizeBytes)),
            _ when job.Format is FileFormat.Unknown or FileFormat.Executable => FileFlowService.FormatSize(job.SizeBytes),
            _ => job.State == JobState.Ready
                ? Loc.F("ff.jobs.metaReady", FileFlowService.FormatSize(job.SizeBytes), from)
                : $"{FileFlowService.FormatSize(job.SizeBytes)} · {from} → {to}"
        };
    }

    private FrameworkElement Thumbnail(FileFlowJob job)
    {
        var box = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(12), ClipToBounds = true };
        if (job.Thumbnail is not null)
        {
            box.Background = new ImageBrush(job.Thumbnail) { Stretch = Stretch.UniformToFill };
            return box;
        }
        var category = FileFlowCatalog.Info(job.Format).Category;
        box.Background = Frozen(CategoryColor(category), 46);
        var label = new TextBlock
        {
            Text = job.Format is FileFormat.Unknown ? "?" : FileFlowCatalog.Info(job.Format).Label.ToUpperInvariant(), FontWeight = FontWeights.Bold, FontSize = job.Format is FileFormat.SoundCloud ? 8.5 : 11,
            Foreground = Frozen(CategoryColor(category)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        box.Child = label;
        return box;
    }

    private void UpdateRow(FileFlowJob job, RowParts parts)
    {
        var text = job.State switch
        {
            JobState.Queued => Loc.T("ff.state.queued"),
            JobState.Importing => Loc.F("ff.state.importing", (int)(job.Progress * 100)),
            JobState.Converting => job.Progress > 0 ? Loc.F("ff.state.converting", (int)(job.Progress * 100)) : Loc.T("ff.state.convertingNoPercent"),
            JobState.Done => Loc.F("ff.state.done", job.Outputs.Count, job.ExpiresUtc?.ToLocalTime().ToString("t", Loc.Culture) ?? ""),
            JobState.Failed => job.Message,
            JobState.Canceled => Loc.T("ff.state.canceled"),
            JobState.Expired => Loc.T("ff.state.expired"),
            _ => ""
        };
        if (parts.Status.Text != text) parts.Status.Text = text;
        if (job.State == JobState.Failed) parts.Status.Foreground = ErrorBrush;
        else parts.Status.SetResourceReference(TextBlock.ForegroundProperty, job.State == JobState.Done ? "GreenBrush" : "TextSecondary");
        parts.Track.Visibility = job.IsActive || job.State == JobState.Done ? Visibility.Visible : Visibility.Collapsed;
        var share = job.State == JobState.Done ? 1 : job.State == JobState.Importing ? job.Progress * 0.15 : job.State == JobState.Converting ? 0.15 + job.Progress * 0.85 : 0;
        parts.Fill.Width = Math.Max(0, parts.Track.ActualWidth * share);
    }

    private void OnJobChanged(FileFlowJob job)
    {
        if (!_rows.TryGetValue(job.Id, out var parts) || parts.State != job.State || job.State == JobState.Ready)
        {
            RebuildJobs(); // the row layout depends on the state (buttons, target chips, preview)
            if (job.State is JobState.Done or JobState.Failed) BuildOptions();
            return;
        }
        UpdateRow(job, parts);
    }

    private void UpdateProgress()
    {
        foreach (var job in _service.Jobs)
            if (job.IsActive && _rows.TryGetValue(job.Id, out var parts)) UpdateRow(job, parts);
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        var active = _service.Jobs.Any(j => j.IsActive);
        if (active && IsVisible) _tick.Start();
        else _tick.Stop();
    }

    // ───────────────────────── saving ─────────────────────────

    private void Save(FileFlowJob job)
    {
        try
        {
            List<string> written;
            if (job.Outputs.Count == 1)
            {
                var output = job.Outputs[0];
                var extension = System.IO.Path.GetExtension(output);
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = System.IO.Path.GetFileName(output), DefaultExt = extension,
                    Filter = $"{FileFlowCatalog.Info(job.Target).Label}|*{extension}|{Loc.T("ff.dialog.all")}|*.*", OverwritePrompt = true
                };
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
                File.Copy(output, dialog.FileName, true);
                File.SetAttributes(dialog.FileName, FileAttributes.Normal);
                written = new List<string> { dialog.FileName };
            }
            else
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.F("ff.dialog.folder", job.Outputs.Count) };
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
                written = FileFlowService.SaveOutputs(job, dialog.FolderName);
            }
            SavedNotice(written);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notice(Loc.T("ff.err.save"), error: true);
        }
    }

    private void SaveZip()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"FileFlow-{DateTime.Now:yyyy-MM-dd-HHmm}.zip", DefaultExt = ".zip", Filter = "ZIP|*.zip", OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            FileFlowService.SaveZip(_service.Jobs, dialog.FileName);
            SavedNotice(new List<string> { dialog.FileName });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notice(Loc.T("ff.err.save"), error: true);
        }
    }

    private void SavedNotice(List<string> written)
    {
        if (written.Count == 0) return;
        Notice(written.Count == 1 ? Loc.F("ff.saved.one", written[0]) : Loc.F("ff.saved.many", written.Count, System.IO.Path.GetDirectoryName(written[0]) ?? ""));
        var open = FooterButton("SecondaryButton", Loc.T("ff.saved.show"), () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + written[0] }, UseShellExecute = false });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Explorer not available.
            }
        });
        _footer.Children.Insert(0, open);
    }

    private void Notice(string text, bool error = false)
    {
        _notice.Text = text;
        if (error) _notice.Foreground = ErrorBrush;
        else _notice.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
    }

    // ───────────────────────── catalogue ─────────────────────────

    private UIElement Catalog()
    {
        Detach(_categoryChips);
        Detach(_cards);
        var stack = new StackPanel { Margin = new Thickness(0, 22, 0, 20) };
        var head = Ui.Text(Loc.T("ff.cat.title"), 18, bold: true);
        stack.Children.Add(head);
        var sub = Ui.Text(Loc.F("ff.cat.sub", FileFlowCatalog.Conversions.Count), 12.5, "TextSecondary");
        sub.Margin = new Thickness(0, 2, 0, 10);
        stack.Children.Add(sub);
        var filterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var (searchHost, search) = Ui.Input(Loc.T("ff.cat.search"));
        searchHost.Width = 240;
        searchHost.Margin = new Thickness(10, 0, 0, 6);
        search.Text = _search;
        search.TextChanged += (_, _) => { _search = search.Text.Trim(); FillCards(); };
        AutomationProperties.SetName(search, Loc.T("ff.cat.search"));
        DockPanel.SetDock(searchHost, Dock.Right);
        filterRow.Children.Add(searchHost);
        filterRow.Children.Add(_categoryChips);
        stack.Children.Add(filterRow);
        stack.Children.Add(_cards);
        FillCategoryChips();
        FillCards();
        return stack;
    }

    private void FillCategoryChips()
    {
        _categoryChips.Children.Clear();
        _categoryChips.Children.Add(Chip(Loc.T("ff.cat.all"), _category is null, () => { _category = null; FillCategoryChips(); FillCards(); }));
        foreach (var category in Enum.GetValues<FormatCategory>())
            _categoryChips.Children.Add(Chip(Loc.T("ff.cat." + category.ToString().ToLowerInvariant()), _category == category, () => { _category = category; FillCategoryChips(); FillCards(); }));
    }

    private void FillCards()
    {
        _cards.Children.Clear();
        var list = FileFlowCatalog.Conversions
            .Where(c => _category is null || c.Category == _category)
            .Where(c => _search.Length == 0 || c.Title.Contains(_search, StringComparison.OrdinalIgnoreCase) || Description(c).Contains(_search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Featured);
        foreach (var conversion in list) _cards.Children.Add(Card(conversion));
        if (_cards.Children.Count == 0) _cards.Children.Add(Ui.Text(Loc.T("ff.cat.none"), 13, "TextMuted"));
    }

    private UIElement Card(Conversion conversion)
    {
        var color = CategoryColor(conversion.Category);
        var stack = new StackPanel();
        var top = new DockPanel();
        var pills = new StackPanel { Orientation = Orientation.Horizontal };
        pills.Children.Add(Pill(string.Join("/", conversion.From.Select(f => FileFlowCatalog.Info(f).Label)), Frozen(color, 40), Frozen(color)));
        var arrow = Ui.Text("→", 15, "TextMuted", bold: true);
        arrow.Margin = new Thickness(6, 0, 6, 0);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        pills.Children.Add(arrow);
        var to = Pill(FileFlowCatalog.Info(conversion.To).Label, null, null);
        to.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        ((TextBlock)to.Child).SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        pills.Children.Add(to);
        if (conversion.Featured)
        {
            var badge = Ui.Text("★ " + Loc.T("ff.cat.popular"), 10.5, "OrangeBrush", bold: true);
            badge.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(badge, Dock.Right);
            top.Children.Add(badge);
        }
        top.Children.Add(pills);
        stack.Children.Add(top);
        var title = Ui.Text(Loc.F("ff.card.title", string.Join("/", conversion.From.Select(f => FileFlowCatalog.Info(f).Label)), FileFlowCatalog.Info(conversion.To).Label), 14, bold: true);
        title.Margin = new Thickness(0, 10, 0, 2);
        stack.Children.Add(title);
        var description = Ui.Text(Description(conversion), 11.5, "TextSecondary");
        description.TextWrapping = TextWrapping.Wrap;
        description.TextTrimming = TextTrimming.None;
        stack.Children.Add(description);

        var selected = _selected?.Id == conversion.Id;
        var card = new Border
        {
            Width = conversion.Featured ? 262 : 214, MinHeight = 118, Padding = new Thickness(14), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(16),
            Child = stack, Cursor = Cursors.Hand, BorderThickness = new Thickness(conversion.Featured || selected ? 2 : 1), Focusable = true
        };
        card.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSoft" : "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, conversion.Featured || selected ? "AccentBrush" : "CardBorder");
        card.MouseEnter += (_, _) => Motion.To(card, UIElement.OpacityProperty, 0.88, 120);
        card.MouseLeave += (_, _) => Motion.To(card, UIElement.OpacityProperty, 1, 180);
        card.MouseLeftButtonUp += (_, _) => Choose(conversion);
        card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Choose(conversion); e.Handled = true; } };
        AutomationProperties.SetName(card, conversion.Title + " – " + Description(conversion));
        return card;
    }

    private static Border Pill(string text, Brush? background, Brush? foreground)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeights.Bold, FontSize = 12 };
        if (foreground is not null) block.Foreground = foreground;
        return new Border { Background = background, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 3), Child = block };
    }

    /// <summary>Clicking a card selects it and opens the file dialog (or the link box for links).</summary>
    private void Choose(Conversion conversion)
    {
        Select(conversion);
        if (conversion.IsLink)
        {
            _linkBox?.Focus();
            _linkCard.BringIntoView();
        }
        else PickFiles();
    }

    private void Select(Conversion? conversion)
    {
        _selected = conversion;
        UpdateSelectionChip();
        BuildOptions();
        FillCards();
    }

    internal static string Description(Conversion conversion)
    {
        var from = conversion.From[0];
        var to = FileFlowCatalog.Info(conversion.To).Label;
        var fromCategory = FileFlowCatalog.Info(from).Category;
        return conversion switch
        {
            { IsLink: true } => Loc.F("ff.desc.link", FileFlowCatalog.Info(from).Label),
            _ when fromCategory == FormatCategory.Video && conversion.To is FileFormat.Mp3 or FileFormat.Wav or FileFormat.Aac => Loc.F("ff.desc.soundtrack", to),
            _ when fromCategory == FormatCategory.Audio => Loc.F("ff.desc.audio", to),
            _ when conversion.To == FileFormat.Mp4 => Loc.T(from == FileFormat.Gif ? "ff.desc.gif" : "ff.desc.video"),
            _ when FileFlowCatalog.IsImage(from) && conversion.To == FileFormat.Pdf => Loc.T("ff.desc.imagesPdf"),
            _ when FileFlowCatalog.IsImage(from) => Loc.F("ff.desc.image", to),
            _ when from == FileFormat.Pdf && conversion.To is FileFormat.Jpg or FileFormat.Png => Loc.F("ff.desc.pdfPages", to),
            _ when from == FileFormat.Pdf && conversion.To == FileFormat.Txt => Loc.T("ff.desc.pdfText"),
            _ when from == FileFormat.Pdf => Loc.T("ff.desc.pdfDocx"),
            _ when conversion.To == FileFormat.Txt => Loc.T("ff.desc.docxText"),
            _ => Loc.T("ff.desc.docPdf")
        };
    }

    private static Color CategoryColor(FormatCategory category) => category switch
    {
        FormatCategory.Audio => Color.FromRgb(0x8B, 0x5C, 0xF6),
        FormatCategory.Video => Color.FromRgb(0x3B, 0x82, 0xF6),
        FormatCategory.Image => Color.FromRgb(0x14, 0xB8, 0x8A),
        FormatCategory.Link => Color.FromRgb(0xEF, 0x44, 0x6F),
        _ => Color.FromRgb(0xF5, 0x9E, 0x0B)
    };

    // ───────────────────────── small builders ─────────────────────────

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

    private static Button Toggle(string text, bool value, Action<bool> set)
    {
        var chip = Ui.Button("ChipButton", (value ? "☑  " : "☐  ") + text, (_, _) => set(!value));
        chip.Height = 34;
        chip.Padding = new Thickness(12, 0, 12, 0);
        chip.HorizontalAlignment = HorizontalAlignment.Left;
        Ui.Select(chip, value);
        AutomationProperties.SetName(chip, text);
        AutomationProperties.SetItemStatus(chip, value ? Loc.T("wx.on") : Loc.T("wx.off"));
        return chip;
    }

    private static Button Small(string style, string text, Action click)
    {
        var button = Ui.Button(style, text, (_, _) => click());
        button.Height = 32;
        button.Padding = new Thickness(12, 0, 12, 0);
        button.Margin = new Thickness(6, 0, 0, 0);
        AutomationProperties.SetName(button, text);
        return button;
    }

    private static Button Icon(string glyph, string tooltip, Action click)
    {
        var button = Ui.Button("IconButton", glyph, (_, _) => click(), tooltip);
        button.Width = 32;
        button.Height = 32;
        button.Margin = new Thickness(4, 0, 0, 0);
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private static TextBlock Heading(string text)
    {
        var block = Ui.Text(text.ToUpper(Loc.Culture), 10.5, "TextMuted", bold: true);
        block.Margin = new Thickness(0, 12, 0, 6);
        return block;
    }

    private static Brush Frozen(Color color, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
