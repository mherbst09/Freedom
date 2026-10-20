using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ViennaAtomicClock;

public partial class MainWindow : Window
{
    private readonly PreciseClock _clock = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _syncTimer;
    private readonly AppSettings _settings;
    private readonly CaffeineStore _caffeineStore;
    private readonly TimeZoneInfo _viennaZone;
    /// <summary>Zone shown by the main clock; school logic always stays on Vienna time.</summary>
    private TimeZoneInfo _displayZone;
    private static CultureInfo _culture => Loc.Culture;
    private CancellationTokenSource? _syncCancellation;
    private bool _focusMode;
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private Rect _previousBounds;
    private bool _previousTopmost;
    private string _page = "clock";
    private bool _pickingColor;
    private bool _syncing;
    private bool _showingCaffeine => _page == "caffeine";
    private string _selectedCategory = "Alle";
    private string _clockHeaderStatus = Loc.T("sync.syncing");
    private string? _clockHeaderForeground;
    private string? _clockHeaderBackground;
    private int _lastTrackerRefreshMinute = -1;
    private DateTime _lastFireworkSlot = DateTime.MinValue;
    private FireworksWindow? _fireworks;
    private GloomWindow? _gloom;
    private DateTime _lastGloomSlot = DateTime.MinValue;
    private readonly List<(Lesson Lesson, Border Row, TextBlock Status, TextBlock Subject)> _scheduleRows = new();
    private TimetableData? _timetable;
    private DateTime _lastTimetableAttempt = DateTime.MinValue;
    private bool _timetableLoading;
    private bool _forceTimetable = true;
    private bool _timetableOffline;
    private int _timetableVersion;
    private TimetableCatalog? _timetableCatalog;
    private bool _classCatalogLoading;
    private CancellationTokenSource? _classCatalogCancellation;
    private CancellationTokenSource? _classSelectionCancellation;
    private int _selectedTimetableDepartmentId;
    private long _lastRenderedClockSecond = long.MinValue;
    private long _lastSchoolSecond = -1;
    private string _scheduleKey = "";

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        if (string.IsNullOrWhiteSpace(_settings.TimetableClass))
            _settings.TimetableClass = WebUntisTimetable.DefaultClassName;
        _selectedTimetableDepartmentId = _settings.TimetableDepartmentId;
        _caffeineStore = CaffeineStore.Load();
        Topmost = _settings.AlwaysOnTop;
        PinButton.Foreground = Topmost ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextSecondary");
        ThemeColorPicker.ColorChanged += color =>
        {
            _pickingColor = true;
            SetLook(App.ThemeName, true, color, save: false);
            _pickingColor = false;
        };
        ThemeColorPicker.ColorCommitted += color => SetLook(App.ThemeName, true, color);
        AnimeTop.Remark = () => _page == "clock" && LessonLabelText.Text == Loc.T("lesson.endsIn") && LessonCountdownText.Text.Length >= 5
            ? Loc.F("anime.minutes", LessonCountdownText.Text[..2].TrimStart('0').PadLeft(1, '0'))
            : null;
        AnimeTop.SeatAnchor = () => ClockPage.IsVisible && ClockCard.IsVisible
            ? ClockCard.TransformToAncestor(WindowShell).TransformBounds(new Rect(ClockCard.RenderSize))
            : null;
        ApplyThemeVisuals();
        SettingsPanel.Width = Math.Max(SettingsPanel.MinWidth, _settings.SettingsWidth);
        SettingsPanel.Height = Math.Max(SettingsPanel.MinHeight, _settings.SettingsHeight);
        TodoView.Changed += () => { if (_page == "todo") UpdateHeaderForCurrentPage(); };
        NotesView.Changed += () => { if (_page == "notes") UpdateHeaderForCurrentPage(); };
        WeatherView.Changed += () => { if (_page == "weather") UpdateHeaderForCurrentPage(); };
        WeatherView.Notifier = new InAppNotifier(this);
        ClickerView.Changed += UpdateHeaderForCurrentPage;
        ViewerView.FullscreenRequested += () =>
        {
            if (_page == "viewer") ToggleFocusMode();
        };
        ViewerView.CloseRequested += () => SwitchPage(_pageBeforeViewer);

        _viennaZone = FindViennaTimeZone();
        _displayZone = FindDisplayZone(_settings.TimeZoneId) ?? _viennaZone;
        DailyLimitTextBox.Text = _settings.CaffeineDailyLimitMg.ToString(_culture);
        RefreshProducts();
        RefreshCaffeineSummary();
        _timetable = WebUntisTimetable.LoadCache(_settings.TimetableClass);
        _timetableCatalog = WebUntisTimetable.LoadCatalogCache();
        SchoolSchedule.DayLessons = RealLessonsOn;
        UpdateGroupButton();
        BuildScheduleList();

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _renderTimer.Tick += (_, _) => RenderTime();

        _syncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _syncTimer.Tick += async (_, _) => await SynchronizeAsync();

        Loaded += async (_, _) =>
        {
            _renderTimer.Start();
            _syncTimer.Start();
            RenderTime();
            SwitchPage(_settings.ActivePage);
            await SynchronizeAsync();
        };

        Closing += (_, _) =>
        {
            // The autoclicker stops first: no click, held button, hook or hotkey outlives the window.
            ClickerView.Shutdown();
            ViewerView.Shutdown();
            _syncCancellation?.Cancel();
            _classCatalogCancellation?.Cancel();
            _classSelectionCancellation?.Cancel();
            _fireworks?.Close();
            _gloom?.Close();
            _settings.DarkMode = App.IsDark;
            _settings.AlwaysOnTop = _focusMode ? _previousTopmost : Topmost;
            _settings.ActivePage = _page;
            TodoView.Flush();
            NotesView.Flush();
            _settings.Save();
            _caffeineStore.Save();
        };

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SourceInitialized += (_, _) => WindowFrame.SetRounded(this, !_focusMode);
        StateChanged += (_, _) => UpdateMaximizedMargin();
        SmoothScroll.Attach(this);
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Loc.Changed += OnLanguageChanged;
        OnLanguageChanged();
    }

    /// <summary>Rearranges and rescales the pages for anything from a small side window to a fullscreen beamer.</summary>
    private void ApplyResponsiveLayout()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var compact = width < 640;
        var narrow = width < 960;
        var scheduleBelow = narrow && height >= 760;
        var scale = Math.Clamp(Math.Min(width / 1120, height / 720), 0.65, 2.2);

        // Title bar and page margins.
        AppSubtitleText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HeaderStatusBadge.Visibility = width < 760 ? Visibility.Collapsed : Visibility.Visible;
        TitleBarGrid.Margin = compact ? new Thickness(12, 0, 8, 0) : new Thickness(24, 0, 16, 0);
        var pageMargin = compact ? new Thickness(12, 2, 12, 12) : new Thickness(24, 4, 24, 24);
        ClockPage.Margin = pageMargin;
        CaffeineScroll.Margin = pageMargin;
        TodoView.Margin = pageMargin;
        NotesView.Margin = pageMargin;
        TodoView.ApplyLayout(width);
        NotesView.ApplyLayout(width);
        TutorView.Margin = pageMargin;
        TutorView.ApplyLayout(width);

        // Clock page: timetable beside the clock, below it on narrow windows, or hidden when there is no room.
        var columns = ClockPage.ColumnDefinitions;
        columns[1].Width = new GridLength(narrow ? 0 : 18);
        columns[2].MinWidth = narrow ? 0 : 380;
        columns[2].Width = narrow ? new GridLength(0) : new GridLength(1.25, GridUnitType.Star);
        var rows = ClockPage.RowDefinitions;
        rows[0].Height = new GridLength(scheduleBelow ? 0.8 : 1, GridUnitType.Star);
        rows[1].Height = new GridLength(scheduleBelow ? 14 : 0);
        rows[2].Height = scheduleBelow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(ScheduleCard, scheduleBelow ? 2 : 0);
        Grid.SetColumn(ScheduleCard, narrow ? 0 : 2);
        ScheduleCard.Visibility = narrow && !scheduleBelow ? Visibility.Collapsed : Visibility.Visible;
        var listScale = Math.Clamp(scale, 0.9, 1.6);
        ScheduleList.LayoutTransform = new ScaleTransform(listScale, listScale);

        // Lesson countdown grows with the window, e.g. for the beamer.
        LessonCountdownText.FontSize = 60 * scale;
        LessonLabelText.FontSize = Math.Max(14, 20 * scale);
        LessonNextText.FontSize = Math.Max(11, 12 * scale);
        LessonBadgeText.FontSize = Math.Max(10, 10 * scale);
        LessonRangeText.FontSize = Math.Max(11, 12 * scale);
        LessonCard.Padding = compact ? new Thickness(18, 14, 18, 14) : new Thickness(28 * Math.Min(scale, 1.4), 20 * Math.Min(scale, 1.4), 28 * Math.Min(scale, 1.4), 20 * Math.Min(scale, 1.4));

        // Caffeine page: three columns, or stacked cards with scrolling on narrow windows.
        var stacked = width < 1000;
        CaffeineScroll.VerticalScrollBarVisibility = stacked ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        var caffeineColumns = CaffeinePage.ColumnDefinitions;
        var caffeineRows = CaffeinePage.RowDefinitions;
        double[] wideWeights = { 0.9, 0, 1.45, 0, 0.85 };
        double[] wideMinimums = { 260, 0, 360, 0, 230 };
        for (var i = 0; i < 5; i++)
        {
            var gap = i % 2 == 1;
            caffeineColumns[i].MinWidth = stacked ? 0 : wideMinimums[i];
            caffeineColumns[i].Width = stacked
                ? (i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(0))
                : gap ? new GridLength(14) : new GridLength(wideWeights[i], GridUnitType.Star);
            caffeineRows[i].Height = stacked
                ? (gap ? new GridLength(14) : GridLength.Auto)
                : (i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(0));
        }

        Border[] cards = { CaffeineSummaryCard, CaffeineCatalogCard, CaffeineLogCard };
        double[] stackedHeights = { 540, 560, 420 };
        for (var i = 0; i < cards.Length; i++)
        {
            Grid.SetRow(cards[i], stacked ? i * 2 : 0);
            Grid.SetColumn(cards[i], stacked ? 0 : i * 2);
            cards[i].Height = stacked ? stackedHeights[i] : double.NaN;
        }

        CustomEntryCard.Width = Math.Min(430, Math.Max(280, width - 70));
    }

    private void RenderTime()
    {
        var utc = _clock.UtcNow;
        var vienna = TimeZoneInfo.ConvertTime(utc, _viennaZone);
        var shown = TimeZoneInfo.ConvertTime(utc, _displayZone);
        var microseconds = (shown.Ticks % TimeSpan.TicksPerSecond) / 10;

        MicrosecondsText.Text = $".{microseconds:000000}";
        SecondProgress.ScaleX = (shown.Second + shown.Millisecond / 1000.0) / 60.0;

        // The blurred glow is comparatively expensive. Updating its text 60 times per second,
        // even though HH:mm:ss did not change, made it visibly twitch over animated themes.
        var displayedSecond = shown.Ticks / TimeSpan.TicksPerSecond;
        if (displayedSecond != _lastRenderedClockSecond)
        {
            _lastRenderedClockSecond = displayedSecond;
            var timeText = shown.ToString("HH:mm:ss", _culture);
            MainTimeText.Text = timeText;
            MainTimeGlow.Text = timeText;
            DateText.Text = shown.ToString("D", _culture);

            var offset = _displayZone.GetUtcOffset(shown.DateTime);
            UtcOffsetBadge.Text = $"UTC{(offset < TimeSpan.Zero ? "−" : "+")}{offset.Duration():hh\\:mm}";
            var dst = _displayZone.IsDaylightSavingTime(shown.DateTime);
            DstText.Text = _displayZone == _viennaZone
                ? Loc.T(dst ? "clock.dst" : "clock.std")
                : dst ? _displayZone.DaylightName : _displayZone.StandardName;
        }

        CheckFireworks(vienna.DateTime);
        CheckGloom(vienna.DateTime);

        var second = vienna.Ticks / TimeSpan.TicksPerSecond;
        if (second != _lastSchoolSecond)
        {
            _lastSchoolSecond = second;
            UpdateSchoolPanel(vienna.DateTime);
            if (_page == "tutor") HeaderStatusText.Text = TutorView.StatusLine;
        }

        if (_showingCaffeine && vienna.Minute != _lastTrackerRefreshMinute)
        {
            _lastTrackerRefreshMinute = vienna.Minute;
            RefreshCaffeineSummary();
        }
    }

    private void CheckFireworks(DateTime vienna)
    {
        // A few seconds of tolerance so a stalled frame or NTP correction cannot skip the slot.
        if (vienna.Second >= 5) return;
        var slot = new DateTime(vienna.Year, vienna.Month, vienna.Day, vienna.Hour, vienna.Minute, 0);
        if (slot == _lastFireworkSlot || !SchoolSchedule.IsLessonEnd(slot)) return;

        _lastFireworkSlot = slot;
        if (_settings.FireworksEnabled) LaunchFireworks();
    }

    private void CheckGloom(DateTime vienna)
    {
        if (vienna.Second >= 5) return;
        var slot = new DateTime(vienna.Year, vienna.Month, vienna.Day, vienna.Hour, vienna.Minute, 0);
        if (slot == _lastGloomSlot || !SchoolSchedule.IsLessonStart(slot)) return;
        _lastGloomSlot = slot;
        if (_settings.GloomEnabled) LaunchGloom();
    }

    private void LaunchGloom()
    {
        if (_gloom is not null) return;
        _gloom = new GloomWindow(FireworksWindow.MonitorArea(this));
        _gloom.Closed += (_, _) => _gloom = null;
        _gloom.Show();
    }

    private void GloomTest_Click(object sender, RoutedEventArgs e) => LaunchGloom();

    private void FireworksToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.FireworksEnabled = !_settings.FireworksEnabled;
        _settings.Save();
        UpdateAnimationToggles();
    }

    private void GloomToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.GloomEnabled = !_settings.GloomEnabled;
        _settings.Save();
        UpdateAnimationToggles();
    }

    private void UpdateAnimationToggles()
    {
        FireworksToggle.Content = Loc.T(_settings.FireworksEnabled ? "anim.on" : "anim.off");
        GloomToggle.Content = Loc.T(_settings.GloomEnabled ? "anim.on" : "anim.off");
        SetChip(FireworksToggle, _settings.FireworksEnabled);
        SetChip(GloomToggle, _settings.GloomEnabled);
    }

    private void LaunchFireworks()
    {
        if (_fireworks is not null) return;
        _fireworks = new FireworksWindow(FireworksWindow.MonitorArea(this));
        _fireworks.Closed += (_, _) => _fireworks = null;
        _fireworks.Show();
    }

    private void FireworksButton_Click(object sender, RoutedEventArgs e) => LaunchFireworks();

    private void BuildScheduleList()
    {
        ScheduleList.Children.Clear();
        _scheduleRows.Clear();
        Lesson? previous = null;

        foreach (var lesson in SchoolSchedule.Lessons)
        {
            // Only longer breaks get their own line; the 5-minute changeovers would just add noise.
            if (previous is not null && lesson.Start - previous.End > TimeSpan.FromMinutes(5))
            {
                var pause = new TextBlock
                {
                    Text = "☕  " + Loc.F("pause.min", (lesson.Start - previous.End).TotalMinutes.ToString("0")),
                    FontSize = 12,
                    Margin = new Thickness(14, 2, 0, 6)
                };
                pause.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                ScheduleList.Children.Add(pause);
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var number = new TextBlock { Text = $"{lesson.Number}.", FontSize = 14.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            number.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

            var range = new TextBlock { Text = $"{lesson.Start:hh\\:mm} – {lesson.End:hh\\:mm}", FontSize = 14.5, VerticalAlignment = VerticalAlignment.Center };
            range.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");

            var subject = new TextBlock { FontSize = 15.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var status = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(range, 1);
            Grid.SetColumn(subject, 2);
            Grid.SetColumn(status, 3);
            grid.Children.Add(number);
            grid.Children.Add(range);
            grid.Children.Add(subject);
            grid.Children.Add(status);

            var row = new Border
            {
                Child = grid,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent
            };
            ScheduleList.Children.Add(row);
            _scheduleRows.Add((lesson, row, status, subject));
            previous = lesson;
        }
    }

    private void UpdateSchoolPanel(DateTime now)
    {
        var state = SchoolSchedule.GetState(now);
        var remaining = state.PhaseEnd - now;
        var todayLessons = SchoolSchedule.LessonsOn(now).ToList();
        var lessonCount = todayLessons.Count;
        var accent = "AccentBrush";
        var soft = "AccentSoft";

        switch (state.Phase)
        {
            case SchoolPhase.Lesson:
                var lesson = state.Current!;
                LessonBadgeText.Text = Loc.F("lesson.badge", lesson.Number);
                LessonRangeText.Text = $"{lesson.Start:hh\\:mm} – {lesson.End:hh\\:mm}{SlotLabel(now.Date, lesson, " · ")}";
                LessonOfDayText.Text = Loc.F("lesson.ofDay", todayLessons.IndexOf(lesson) + 1, lessonCount);
                LessonLabelText.Text = Loc.T("lesson.endsIn");
                LessonNextText.Text = state.Next is { } next
                    ? next.Start - lesson.End > TimeSpan.FromMinutes(5)
                        ? Loc.F("lesson.afterPause", (next.Start - lesson.End).TotalMinutes.ToString("0"), Loc.F("lesson.n", next.Number) + SlotLabel(now.Date, next, " "), next.Start.ToString("hh\\:mm"))
                        : Loc.F("lesson.afterNext", Loc.F("lesson.n", next.Number) + SlotLabel(now.Date, next, " "), next.Start.ToString("hh\\:mm"))
                    : Loc.T("lesson.afterEnd");
                // Last five minutes: the countdown turns orange.
                if (remaining <= TimeSpan.FromMinutes(5)) accent = "OrangeBrush";
                break;

            case SchoolPhase.Break:
                LessonBadgeText.Text = Loc.T("badge.pause");
                LessonRangeText.Text = $"{state.Current!.End:hh\\:mm} – {state.Next!.Start:hh\\:mm}";
                LessonOfDayText.Text = Loc.F("lesson.done", todayLessons.IndexOf(state.Current) + 1, lessonCount);
                LessonLabelText.Text = Loc.T("lesson.nextIn");
                LessonNextText.Text = $"{Loc.F("lesson.n", state.Next.Number)}{SlotLabel(now.Date, state.Next, " · ")} · {state.Next.Start:hh\\:mm} – {state.Next.End:hh\\:mm}";
                accent = "GreenBrush";
                soft = "GreenSoft";
                break;

            case SchoolPhase.BeforeSchool:
                LessonBadgeText.Text = Loc.T("badge.before");
                LessonRangeText.Text = "";
                LessonOfDayText.Text = Loc.F("lesson.count", lessonCount);
                LessonLabelText.Text = Loc.T("lesson.startsIn");
                LessonNextText.Text = $"{Loc.F("lesson.n", state.Next!.Number)}{SlotLabel(now.Date, state.Next, " · ")} · {state.Next.Start:hh\\:mm} – {state.Next.End:hh\\:mm}";
                accent = "OrangeBrush";
                break;

            default:
                var weekend = state.Phase == SchoolPhase.Weekend;
                var realWeekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                LessonBadgeText.Text = Loc.T(weekend ? (realWeekend ? "badge.weekend" : "badge.free") : "badge.end");
                LessonRangeText.Text = "";
                LessonOfDayText.Text = weekend ? "" : Loc.F("lesson.done", lessonCount, lessonCount);
                LessonLabelText.Text = Loc.T(weekend ? (realWeekend ? "label.weekend" : "label.free") : "label.end");
                LessonNextText.Text = Loc.F("label.continue", state.PhaseEnd.ToString("dddd", _culture), state.PhaseEnd.ToString("HH\\:mm"));
                accent = "GreenBrush";
                soft = "GreenSoft";
                break;
        }

        LessonCountdownText.Text = state.Phase is SchoolPhase.AfterSchool or SchoolPhase.Weekend ? Loc.T("free") : FormatCountdown(remaining);
        LessonCountdownText.SetResourceReference(TextBlock.ForegroundProperty, accent);
        LessonProgressFill.SetResourceReference(Border.BackgroundProperty, accent);
        LessonBadge.SetResourceReference(Border.BackgroundProperty, soft);
        LessonBadgeText.SetResourceReference(TextBlock.ForegroundProperty, accent);
        LessonProgress.ScaleX = state.Phase is SchoolPhase.Lesson or SchoolPhase.Break
            ? state.Progress(now)
            : state.Phase == SchoolPhase.AfterSchool ? 1 : 0;

        UpdateScheduleRows(state, now);
    }

    private static string FormatCountdown(TimeSpan remaining)
    {
        // Round up so the display reaches 00:00 exactly when the bell rings.
        var seconds = (long)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span:mm\\:ss}" : span.ToString("mm\\:ss");
    }

    private void UpdateScheduleRows(SchoolState state, DateTime now)
    {
        // After school and on free days the list already shows the next school day.
        var showNextDay = state.Phase is SchoolPhase.AfterSchool or SchoolPhase.Weekend;
        var day = showNextDay ? state.PhaseEnd.Date : now.Date;
        var key = $"{now.Date:yyyyMMdd}|{day:yyyyMMdd}|{state.Phase}|{state.Current?.Number}|{_timetableVersion}|{_settings.TimetableGroup}|{_timetableLoading}";
        if (key == _scheduleKey) return;
        _scheduleKey = key;

        var dayName = day == now.Date ? day.ToString("dddd", _culture)
            : day == now.Date.AddDays(1) ? Loc.T("tomorrow") : $"{day.ToString("dddd", _culture)}, {day.ToString("M", _culture)}";
        var covered = _timetable?.Covers(day) == true;
        var source = covered
            ? $"{Loc.F("schedule.stand", _timetable!.FetchedAt.ToString("HH\\:mm"))}{(_timetableOffline ? Loc.T("schedule.offline") : "")}"
            : Loc.T(_timetableLoading ? "schedule.loading" : "schedule.fixed");
        ScheduleDayText.Text = dayName;
        ScheduleClassText.Text = _settings.TimetableClass;
        ScheduleSourceText.Text = source;

        var entries = covered ? _timetable!.On(day, _settings.TimetableGroup) : new List<TimetableEntry>();
        var isToday = day == now.Date;

        foreach (var (lesson, row, status, subject) in _scheduleRows)
        {
            var slot = entries.Where(e => e.Start < lesson.End && e.End > lesson.Start).ToList();
            var free = covered && slot.Count == 0;
            var cancelled = slot.Count > 0 && slot.All(e => e.Status == EntryStatus.Cancelled);
            var changed = slot.Any(e => e.Status == EntryStatus.Changed);
            var exam = slot.Any(e => e.Status == EntryStatus.Exam);

            var isCurrent = isToday && state.Phase == SchoolPhase.Lesson && state.Current == lesson;
            var isUpcoming = isToday && state.Phase is SchoolPhase.Break or SchoolPhase.BeforeSchool && state.Next == lesson;
            var isDone = isToday && now.TimeOfDay >= lesson.End;

            subject.Inlines.Clear();
            subject.TextDecorations = cancelled ? TextDecorations.Strikethrough : null;
            if (slot.Count > 0)
            {
                foreach (var entry in slot)
                {
                    if (subject.Inlines.Count > 0) subject.Inlines.Add(new Run(" / "));
                    subject.Inlines.Add(new Run(entry.Subject) { FontWeight = FontWeights.Bold });
                    if (entry.Room.Length > 0)
                    {
                        var room = new Run("  " + entry.Room) { FontSize = 13 };
                        room.SetResourceReference(TextElement.ForegroundProperty, "TextSecondary");
                        subject.Inlines.Add(room);
                    }
                }
                row.ToolTip = string.Join("\n", slot.Select(e =>
                    $"{e.SubjectLong}{(e.Room.Length > 0 ? " · " + e.Room : "")}{(e.Info.Length > 0 ? " · " + e.Info : "")}"));
            }
            else
            {
                subject.Inlines.Add(new Run(free ? Loc.T("free") : ""));
                row.ToolTip = null;
            }
            subject.SetResourceReference(TextBlock.ForegroundProperty, free ? "TextMuted" : "TextPrimary");

            row.Opacity = isDone || free ? 0.45 : 1;
            row.ClearValue(Border.BackgroundProperty);
            row.ClearValue(Border.BorderBrushProperty);
            row.Background = Brushes.Transparent;
            row.BorderBrush = Brushes.Transparent;

            if (isCurrent)
            {
                row.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                SetStatus(status, Loc.T("status.now"), "AccentBrush");
            }
            else if (cancelled && !isDone)
                SetStatus(status, Loc.T("status.cancelled"), "OrangeBrush");
            else if (exam && !isDone)
                SetStatus(status, Loc.T("status.test"), "OrangeBrush");
            else if (changed && !isDone)
                SetStatus(status, Loc.T("status.subst"), "OrangeBrush");
            else if (isUpcoming)
            {
                row.SetResourceReference(Border.BorderBrushProperty, "GreenBrush");
                SetStatus(status, Loc.T("status.next"), "GreenBrush");
            }
            else if (isDone && !free)
                SetStatus(status, "✓", "TextMuted");
            else
                status.Text = "";
        }
    }

    private static void SetStatus(TextBlock status, string text, string brush)
    {
        status.Text = text;
        status.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    /// <summary>Lesson slots that really take place on a day, or null when the timetable does not cover it.</summary>
    private IReadOnlyList<Lesson>? RealLessonsOn(DateTime date)
    {
        if (_timetable is null || !_timetable.Covers(date)) return null;
        var entries = _timetable.On(date, _settings.TimetableGroup).Where(e => e.Status != EntryStatus.Cancelled).ToList();
        return SchoolSchedule.Lessons.Where(l => entries.Any(e => e.Start < l.End && e.End > l.Start)).ToList();
    }

    /// <summary>"AM in H307" for a slot after the given prefix, or empty without timetable.</summary>
    private string SlotLabel(DateTime date, Lesson lesson, string prefix)
    {
        if (_timetable is null || !_timetable.Covers(date)) return "";
        var slot = _timetable.On(date, _settings.TimetableGroup)
            .Where(e => e.Status != EntryStatus.Cancelled && e.Start < lesson.End && e.End > lesson.Start).ToList();
        if (slot.Count == 0) return "";
        return prefix + string.Join(" / ", slot.Select(e => e.Room.Length > 0 ? Loc.F("slot.in", e.Subject, e.Room) : e.Subject));
    }

    private async Task RefreshTimetableAsync(bool force, CancellationToken cancellation)
    {
        if (_timetableLoading || (!force && DateTime.Now - _lastTimetableAttempt < TimeSpan.FromMinutes(10))) return;
        _timetableLoading = true;
        _lastTimetableAttempt = DateTime.Now;
        var requestedClass = _settings.TimetableClass;
        try
        {
            var today = TimeZoneInfo.ConvertTime(_clock.UtcNow, _viennaZone).DateTime;
            var timetable = await WebUntisTimetable.FetchAsync(today, requestedClass, cancellation);
            if (string.Equals(requestedClass, _settings.TimetableClass, StringComparison.OrdinalIgnoreCase))
            {
                _timetable = timetable;
                _timetableOffline = false;
                _timetableCatalog = WebUntisTimetable.LoadCatalogCache() ?? _timetableCatalog;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Offline or WebUntis unreachable: keep the last saved timetable (or the fixed times).
            if (string.Equals(requestedClass, _settings.TimetableClass, StringComparison.OrdinalIgnoreCase))
                _timetableOffline = true;
        }
        finally
        {
            _timetableLoading = false;
            _timetableVersion++;
            _lastSchoolSecond = -1;
        }
    }

    private void GroupButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.TimetableGroup = (_settings.TimetableGroup + 1) % 3;
        _settings.Save();
        UpdateGroupButton();
        _lastSchoolSecond = -1;
    }

    private void UpdateGroupButton() =>
        GroupButton.Content = _settings.TimetableGroup == 0 ? Loc.T("group.both") : Loc.F("group.n", _settings.TimetableGroup);

    private async void ScheduleClassButton_Click(object sender, RoutedEventArgs e)
    {
        ClassPickerPopup.IsOpen = true;
        if (_timetableCatalog is not null) BuildClassPicker();
        await EnsureClassCatalogAsync();
    }

    private async Task EnsureClassCatalogAsync()
    {
        if (_classCatalogLoading) return;
        if (_timetableCatalog is not null && DateTime.Now - _timetableCatalog.FetchedAt < TimeSpan.FromHours(12))
        {
            BuildClassPicker();
            return;
        }

        _classCatalogLoading = true;
        ClassPickerStatusText.Text = Loc.T("schedule.classes.loading");
        ClassPickerStatusText.Visibility = Visibility.Visible;
        _classCatalogCancellation?.Cancel();
        _classCatalogCancellation?.Dispose();
        var catalogCancellation = new CancellationTokenSource();
        _classCatalogCancellation = catalogCancellation;
        try
        {
            var today = TimeZoneInfo.ConvertTime(_clock.UtcNow, _viennaZone).DateTime;
            _timetableCatalog = await WebUntisTimetable.LoadCatalogAsync(today, catalogCancellation.Token);
            BuildClassPicker();
        }
        catch (OperationCanceledException) when (catalogCancellation.IsCancellationRequested)
        {
            // A closed window or a newer request owns the picker now.
        }
        catch
        {
            if (_timetableCatalog is null)
            {
                ClassPickerStatusText.Text = Loc.T("schedule.classes.error");
                ClassPickerStatusText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _classCatalogLoading = false;
        }
    }

    private void BuildClassPicker()
    {
        if (_timetableCatalog is not { } catalog) return;

        var availableDepartments = catalog.Departments
            .Where(department => catalog.Classes.Any(item => item.DepartmentIds.Contains(department.Id)))
            .ToList();
        var selectedClass = catalog.Classes.FirstOrDefault(item =>
            string.Equals(item.Name, _settings.TimetableClass, StringComparison.OrdinalIgnoreCase));
        if (!availableDepartments.Any(item => item.Id == _selectedTimetableDepartmentId))
        {
            var classDepartment = selectedClass?.DepartmentIds.FirstOrDefault(id => availableDepartments.Any(item => item.Id == id)) ?? 0;
            _selectedTimetableDepartmentId = classDepartment != 0 ? classDepartment : availableDepartments.FirstOrDefault()?.Id ?? 0;
        }

        DepartmentList.Children.Clear();
        foreach (var department in availableDepartments)
        {
            var chip = PickerChip(department.Name, department.Name, department.Id == _selectedTimetableDepartmentId);
            var picked = department.Id;
            chip.Click += (_, _) => SelectTimetableDepartment(picked);
            DepartmentList.Children.Add(chip);
        }

        BuildClassList(catalog);
    }

    private void SelectTimetableDepartment(int departmentId)
    {
        _selectedTimetableDepartmentId = departmentId;
        _settings.TimetableDepartmentId = departmentId;
        _settings.Save();
        BuildClassPicker();
    }

    private void BuildClassList(TimetableCatalog catalog)
    {
        ClassList.Children.Clear();
        foreach (var item in catalog.Classes.Where(item => item.DepartmentIds.Contains(_selectedTimetableDepartmentId)))
        {
            var tooltip = string.IsNullOrWhiteSpace(item.LongName) || item.LongName == item.Name ? item.Name : item.LongName;
            var chip = PickerChip(item.Name, tooltip,
                string.Equals(item.Name, _settings.TimetableClass, StringComparison.OrdinalIgnoreCase));
            var picked = item;
            chip.Click += async (_, _) => await SelectTimetableClassAsync(picked);
            ClassList.Children.Add(chip);
        }

        ClassPickerStatusText.Visibility = ClassList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ClassList.Children.Count == 0) ClassPickerStatusText.Text = Loc.T("schedule.classes.error");
    }

    private Button PickerChip(string text, string tooltip, bool selected)
    {
        var chip = new Button
        {
            Content = text,
            ToolTip = tooltip,
            Height = 32,
            Margin = new Thickness(0, 0, 7, 7),
            Padding = new Thickness(11, 0, 11, 0)
        };
        chip.SetResourceReference(StyleProperty, "ChipButton");
        SetChip(chip, selected);
        return chip;
    }

    private async Task SelectTimetableClassAsync(TimetableClass selectedClass)
    {
        ClassPickerPopup.IsOpen = false;
        if (string.Equals(selectedClass.Name, _settings.TimetableClass, StringComparison.OrdinalIgnoreCase)) return;

        _settings.TimetableClass = selectedClass.Name;
        _settings.TimetableDepartmentId = _selectedTimetableDepartmentId;
        _settings.Save();
        _timetable = WebUntisTimetable.LoadCache(selectedClass.Name);
        _timetableOffline = _timetable is not null;
        _lastTimetableAttempt = DateTime.MinValue;
        _scheduleKey = "";
        _lastSchoolSecond = -1;
        _timetableVersion++;
        RenderTime();

        _classSelectionCancellation?.Cancel();
        _classSelectionCancellation?.Dispose();
        _classSelectionCancellation = new CancellationTokenSource();
        var cancellation = _classSelectionCancellation.Token;
        try
        {
            // A start-up refresh may still be finishing for the old class.
            while (_timetableLoading)
                await Task.Delay(50, cancellation);
            await RefreshTimetableAsync(force: true, cancellation);
            _scheduleKey = "";
            _lastSchoolSecond = -1;
            RenderTime();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Another class was selected or the app is closing.
        }
    }

    private async Task SynchronizeAsync()
    {
        if (_syncCancellation is not null) return;

        _syncCancellation = new CancellationTokenSource();
        SyncButton.IsEnabled = false;
        _syncing = true;
        SyncButton.Content = Loc.T("sync.running");
        SyncStatusText.Text = Loc.T("sync.connecting");
        _clockHeaderStatus = Loc.T("sync.syncing");
        if (_page == "clock" && !ClickerView.IsActive) HeaderStatusText.Text = _clockHeaderStatus;

        try
        {
            var result = await NtpClient.SynchronizeAsync(_syncCancellation.Token);
            if (result.Success)
            {
                _clock.Set(result.CorrectedUtc);
                SourceText.Text = result.Server;
                OffsetText.Text = FormatOffset(result.Offset);
                LatencyText.Text = $"{result.RoundTrip.TotalMilliseconds:0.0} ms";
                LastSyncText.Text = TimeZoneInfo.ConvertTime(result.CorrectedUtc, _displayZone).ToString("HH:mm:ss", _culture);
                SyncStatusText.Text = Loc.T("sync.ok");
                SetClockStatus(Loc.T("sync.headerOk"), "GreenBrush", "GreenSoft");
            }
            else
            {
                _clock.Set(DateTimeOffset.UtcNow);
                SourceText.Text = Loc.T("sync.system");
                OffsetText.Text = Loc.T("sync.na");
                LatencyText.Text = "— ms";
                SyncStatusText.Text = Loc.T("sync.offline");
                SetClockStatus(Loc.T("sync.headerSystem"), "OrangeBrush", "AccentSoft");
            }
        }
        catch (OperationCanceledException)
        {
            // Window is closing.
        }

        try
        {
            if (_syncCancellation is { } cancellation)
                await RefreshTimetableAsync(_forceTimetable, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Window is closing.
        }
        finally
        {
            _forceTimetable = false;
            _syncCancellation?.Dispose();
            _syncCancellation = null;
            SyncButton.IsEnabled = true;
            _syncing = false;
            SyncButton.Content = Loc.T("sync.button");
        }
    }

    private void SetStatusColor(Brush foreground, Brush background)
    {
        HeaderStatusDot.Fill = foreground;
        HeaderStatusText.Foreground = foreground;
        if (HeaderStatusText.Parent is StackPanel panel && panel.Parent is Border border)
            border.Background = background;
    }

    private void SetClockStatus(string text, string foreground, string background)
    {
        _clockHeaderStatus = text;
        _clockHeaderForeground = foreground;
        _clockHeaderBackground = background;
        if (_page == "clock")
        {
            HeaderStatusText.Text = text;
            SetStatusColor((Brush)FindResource(foreground), (Brush)FindResource(background));
        }
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset.TotalMilliseconds < -0.0005 ? "−" : offset.TotalMilliseconds > 0.0005 ? "+" : "±";
        return $"{sign} {Math.Abs(offset.TotalMilliseconds):0.000} ms";
    }

    private static TimeZoneInfo FindViennaTimeZone()
    {
        foreach (var id in new[] { "Europe/Vienna", "W. Europe Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.Local;
    }

    private static TimeZoneInfo? FindDisplayZone(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    /// <summary>City part of a zone name, e.g. "(UTC+09:00) Osaka, Sapporo, Tokio" -> "OSAKA, SAPPORO, TOKIO".</summary>
    private static string ZoneCities(TimeZoneInfo zone)
    {
        var name = zone.DisplayName;
        var close = name.IndexOf(") ", StringComparison.Ordinal);
        return (close >= 0 ? name[(close + 2)..] : name).ToUpper(_culture);
    }

    private void UpdateClockCity()
    {
        if (_displayZone == _viennaZone)
            ClockCityText.SetResourceReference(TextBlock.TextProperty, "L.clock.city");
        else
            ClockCityText.Text = ZoneCities(_displayZone);
        ClockCityText.ToolTip = _displayZone.DisplayName;
    }

    private void SetDisplayZone(TimeZoneInfo zone)
    {
        _displayZone = zone;
        _settings.TimeZoneId = zone == _viennaZone ? "" : zone.Id;
        _settings.Save();
        UpdateClockCity();
        _lastRenderedClockSecond = long.MinValue;
        RenderTime();
        BuildTimeZoneList();
    }

    private void TimeZoneSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        TimeZoneSearchHint.Visibility = TimeZoneSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildTimeZoneList();
    }

    /// <summary>All Windows time zones, filtered by the search box; the selected one is highlighted.</summary>
    private void BuildTimeZoneList()
    {
        var query = TimeZoneSearchBox.Text.Trim();
        TimeZoneList.Children.Clear();
        Button? selectedChip = null;
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (query.Length > 0
                && !zone.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && !zone.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            var chip = new Button
            {
                Content = new TextBlock { Text = zone.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = zone.DisplayName, Height = 32, Margin = new Thickness(0, 0, 0, 3), Padding = new Thickness(10, 0, 10, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            chip.SetResourceReference(StyleProperty, "ChipButton");
            var selected = zone.Id == _displayZone.Id;
            SetChip(chip, selected);
            if (selected) selectedChip = chip;
            var picked = zone;
            chip.Click += (_, _) => SetDisplayZone(picked);
            TimeZoneList.Children.Add(chip);
        }
        if (selectedChip is not null && query.Length == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => selectedChip.BringIntoView());
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSettingsPopup();
        BuildTimeZoneList();
        SettingsPopup.IsOpen = true;
    }

    private void LightModeButton_Click(object sender, RoutedEventArgs e) => SetLookSmooth(App.ThemeName, false, null);
    private void DarkModeButton_Click(object sender, RoutedEventArgs e) => SetLookSmooth(App.ThemeName, true, null);
    private void ColorModeButton_Click(object sender, RoutedEventArgs e) =>
        SetLookSmooth(App.ThemeName, dark: true, App.ParseColor(_settings.CustomColor) ?? Color.FromRgb(61, 123, 255));

    /// <summary>Theme change with a short crossfade from the old look to the new one.</summary>
    private void SetLookSmooth(string theme, bool dark, Color? custom) =>
        Motion.Crossfade(WindowShell, RootGrid, () => SetLook(theme, dark, custom));

    private void Popup_Opened(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: FrameworkElement content } popup)
            Motion.PopIn(content, popup == SettingsPopup ? 0.92 : 0.1);
    }

    /// <param name="custom">Colour for the coloured mode, or null for the light/dark palette.</param>
    private void SetLook(string theme, bool dark, Color? custom, bool save = true)
    {
        App.ApplyTheme(theme, dark, custom);
        _settings.Theme = App.ThemeName;
        _settings.DarkMode = dark;
        _settings.ColorMode = custom is not null;
        if (custom is { } color) _settings.CustomColor = color.ToString();
        if (save) _settings.Save();

        ApplyThemeVisuals();
        PinButton.Foreground = Topmost ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextSecondary");
        UpdateNavigationVisuals();
        UpdateCategoryButtons();
        UpdateHeaderForCurrentPage();
        RefreshCaffeineSummary();
        _scheduleKey = "";
        _lastSchoolSecond = -1;
    }

    /// <summary>Everything a theme changes beyond colours and fonts: animated layers, glow, corners, texts.</summary>
    private void ApplyThemeVisuals()
    {
        var theme = App.ThemeName;
        FilmGrain.Visibility = theme == Themes.War ? Visibility.Visible : Visibility.Collapsed;
        HackerRainLayer.Visibility = theme == Themes.Hacker ? Visibility.Visible : Visibility.Collapsed;
        HackerScanLayer.Visibility = theme == Themes.Hacker ? Visibility.Visible : Visibility.Collapsed;
        AnimeLayer.Visibility = theme == Themes.Anime ? Visibility.Visible : Visibility.Collapsed;
        AnimeTop.Visibility = theme == Themes.Anime ? Visibility.Visible : Visibility.Collapsed;
        TetrisLayer.Visibility = theme == Themes.Tetris ? Visibility.Visible : Visibility.Collapsed;
        AmericaLayer.Visibility = theme == Themes.America ? Visibility.Visible : Visibility.Collapsed;
        MinecraftLayer.Visibility = theme == Themes.Minecraft ? Visibility.Visible : Visibility.Collapsed;
        MinecraftTop.Visibility = theme == Themes.Minecraft ? Visibility.Visible : Visibility.Collapsed;
        EagleLayer.Visibility = theme == Themes.Eagle ? Visibility.Visible : Visibility.Collapsed;
        MysticLayer.Visibility = theme == Themes.Mystic ? Visibility.Visible : Visibility.Collapsed;
        MysticTop.Visibility = theme == Themes.Mystic ? Visibility.Visible : Visibility.Collapsed;
        if (theme == Themes.Mystic) MysticLayer.RefreshTheme();

        var accent = ((SolidColorBrush)FindResource("AccentBrush")).Color;
        // Glows sit on a blurred copy behind the text; an effect on the text itself would make it soft.
        MainTimeGlow.Effect = theme switch
        {
            Themes.Hacker => new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 18, ShadowDepth = 0, Opacity = App.IsDark ? 0.9 : 0.35 },
            Themes.Anime => new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.55 },
            Themes.Tetris => new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.6 },
            Themes.America => new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(10, 31, 68), BlurRadius = 0, ShadowDepth = 4, Direction = 315, Opacity = App.IsDark ? 0.9 : 0.35 },
            // Hard pixel shadow like game text.
            Themes.Minecraft => new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 0, ShadowDepth = 5, Direction = 315, Opacity = 0.6 },
            Themes.Eagle => new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 24, ShadowDepth = 0, Opacity = App.IsDark ? 0.6 : 0.35 },
            Themes.Mystic => new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 28, ShadowDepth = 0, Opacity = App.IsDark ? 0.7 : 0.45 },
            _ => App.CustomColor is not null && theme != Themes.Standard ? new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.45 } : null
        };
        LessonCountdownGlow.Effect = theme == Themes.Hacker && App.IsDark
            ? new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.8 }
            : null;
        MainTimeGlow.Visibility = MainTimeGlow.Effect is null ? Visibility.Collapsed : Visibility.Visible;
        LessonCountdownGlow.Visibility = LessonCountdownGlow.Effect is null ? Visibility.Collapsed : Visibility.Visible;
        AppSwitcherButton.Content = theme switch
        {
            Themes.Hacker => ">_", Themes.Anime => "🐾", Themes.Tetris => "▦", Themes.America => "★", Themes.Minecraft => "⛏", Themes.Eagle => "✦", Themes.Mystic => "🌸", _ => "⊞"
        };
        AppSwitcherButton.FontSize = theme == Themes.Hacker ? 17 : 22;

        ApplyShellRadius();
        UpdateTitleTexts();
        UpdateSettingsPopup();
    }

    private void ApplyShellRadius()
    {
        // Windows 11 rounds the window itself; the layers only need to stay inside it.
        WindowFrame.SetRounded(this, !_focusMode);
        var inner = 0;
        FilmGrain.Radius = inner;
        HackerRainLayer.Radius = inner;
        HackerScanLayer.Radius = inner;
        AnimeLayer.Radius = inner;
        AnimeTop.Radius = inner;
        TetrisLayer.Radius = inner;
        AmericaLayer.Radius = inner;
        MinecraftLayer.Radius = inner;
        MinecraftTop.Radius = inner;
        EagleLayer.Radius = inner;
        MysticLayer.Radius = inner;
        MysticTop.Radius = inner;
    }

    private void UpdateTitleTexts()
    {
        var theme = App.ThemeName;
        if (_page == "tutor")
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "EAGLE_1.1_", Themes.Anime => "Eagle 1.1 ♡", _ => "EAGLE 1.1" };
            AppSubtitleText.Text = Loc.T("title.ai.sub");
        }
        else if (_page == "todo")
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "TODO.SH_", Themes.Anime => Loc.T("window.todo") + " ♡", _ => Loc.T("title.todo") };
            AppSubtitleText.Text = theme switch
            {
                Themes.Hacker => "root@freedom:~$ todo --list",
                Themes.Anime => "nya~ ♡ " + Loc.T("title.todo.sub"),
                _ => Loc.T("title.todo.sub")
            };
        }
        else if (_page == "notes")
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "NOTES.TXT_", Themes.Anime => Loc.T("window.notes") + " ♡", _ => Loc.T("title.notes") };
            AppSubtitleText.Text = theme switch
            {
                Themes.Hacker => "root@freedom:~$ vim notes",
                Themes.Anime => "nya~ ♡ " + Loc.T("title.notes.sub"),
                _ => Loc.T("title.notes.sub")
            };
        }
        else if (_page == "viewer")
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "VIEWER.GLB_", Themes.Anime => Loc.T("tile.viewer") + " ♡", _ => Loc.T("title.viewer") };
            AppSubtitleText.Text = theme switch
            {
                Themes.Hacker => "root@freedom:~$ three --offline model.glb",
                Themes.Anime => "nya~ ♡ " + Loc.T("title.viewer.sub"),
                _ => Loc.T("title.viewer.sub")
            };
        }
        else if (_page == "clicker")
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "CLICKER.EXE_", Themes.Anime => Loc.T("tile.clicker") + " ♡", _ => Loc.T("title.clicker") };
            AppSubtitleText.Text = theme switch
            {
                Themes.Hacker => "root@freedom:~$ click --cps 500",
                Themes.Anime => "nya~ ♡ " + Loc.T("title.clicker.sub"),
                _ => Loc.T("title.clicker.sub")
            };
        }
        else if (_page == "weather")
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "WEATHER.SH_", Themes.Anime => Loc.T("tile.weather") + " ♡", _ => Loc.T("title.weather") };
            AppSubtitleText.Text = theme switch
            {
                Themes.Hacker => "root@freedom:~$ curl wttr --forecast",
                Themes.Anime => "nya~ ♡ " + Loc.T("title.weather.sub"),
                _ => Loc.T("title.weather.sub")
            };
        }
        else if (_showingCaffeine)
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "CAFFEINE.EXE_", Themes.Anime => Loc.T("tile.coffee") + " ♡", _ => Loc.T("title.coffee") };
            AppSubtitleText.Text = theme switch
            {
                Themes.Hacker => "root@freedom:~$ track --caffeine",
                Themes.Anime => "nya~ ♡ " + Loc.T("title.coffee.sub"),
                _ => Loc.T("title.coffee.sub")
            };
        }
        else
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "FREEDOM_", Themes.Anime => "Freedom ♡", Themes.America => "FREEDOM ★", _ => "FREEDOM" };
            AppSubtitleText.Text = theme switch
            {
                Themes.Hacker => "root@freedom:~$ sync --time --timetable",
                Themes.Anime => "nya~ ฅ^•ﻌ•^ฅ " + Loc.T("title.clock.sub"),
                Themes.Tetris => "LEVEL 4BHITS · " + Loc.T("title.clock.sub"),
                Themes.America => "Land of the free · Home of the Stundenplan",
                Themes.Minecraft => "Survival · " + Loc.T("title.clock.sub"),
                _ => Loc.T("title.clock.sub")
            };
        }
    }

    private void UpdateSettingsPopup()
    {
        var colored = App.CustomColor is not null;
        SetChip(LightModeButton, !colored && !App.IsDark);
        SetChip(DarkModeButton, !colored && App.IsDark);
        SetChip(ColorModeButton, colored);
        ColorPickerHost.Visibility = colored ? Visibility.Visible : Visibility.Collapsed;
        if (colored && !_pickingColor) ThemeColorPicker.Color = App.CustomColor!.Value;

        UpdateAnimationToggles();
        LanguageList.Children.Clear();
        foreach (var (code, name, _) in Loc.Languages)
        {
            var chip = new Button { Content = name, Height = 34, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(12, 0, 12, 0) };
            chip.SetResourceReference(StyleProperty, "ChipButton");
            SetChip(chip, code == Loc.Language);
            chip.Click += (_, _) => SetLanguage(code);
            LanguageList.Children.Add(chip);
        }

        ThemeList.Children.Clear();
        foreach (var (id, _, glyph, _) in Themes.All)
        {
            var selected = id == App.ThemeName;

            // Faded preview of the theme behind the name, so you can see what to expect.
            var preview = new Border { Background = ThemePreview.Create(id, App.IsDark), Opacity = 0.6 };
            preview.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");

            var label = new Border { VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(10, 6, 10, 6) };
            label.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
            label.Opacity = 0.92;
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock
            {
                Text = $"{glyph}  {Loc.T("theme." + id)}{(selected ? "  ✓" : "")}",
                FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis
            });
            var hintText = new TextBlock { Text = Loc.T("theme." + id + ".hint"), FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
            hintText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            texts.Children.Add(hintText);
            label.Child = texts;

            var tile = new Grid { ClipToBounds = true };
            tile.Children.Add(preview);
            tile.Children.Add(label);

            var button = new Button
            {
                Content = tile, Height = 104, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                BorderThickness = new Thickness(selected ? 2 : 1), Tag = id
            };
            button.SetResourceReference(StyleProperty, "SecondaryButton");
            button.SetResourceReference(BackgroundProperty, selected ? "AccentSoft" : "SurfaceAlt");
            // The button template ignores borders, so the selection ring is drawn around the tile.
            if (selected)
            {
                var ring = new Border { BorderThickness = new Thickness(2) };
                ring.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                ring.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
                tile.Children.Add(ring);
            }
            // The shared button template centres its content, so the tile is sized to fill the button by hand.
            button.SizeChanged += (_, _) => { tile.Width = button.ActualWidth; tile.Height = button.ActualHeight; };
            button.Click += (_, _) => SetLookSmooth(id, _settings.DarkMode, App.CustomColor);
            ThemeList.Children.Add(button);
        }
    }

    private void SetLanguage(string code)
    {
        _settings.Language = code;
        _settings.Save();
        Loc.Apply(code);
    }

    /// <summary>Re-renders every text that code builds when the language changes (XAML texts update by themselves).</summary>
    private void OnLanguageChanged()
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag);
        _clockHeaderStatus = Loc.Retranslate(_clockHeaderStatus, "sync.headerOk", "sync.headerSystem", "sync.syncing");
        SyncStatusText.Text = Loc.Retranslate(SyncStatusText.Text, "sync.ok", "sync.offline", "sync.connecting", "sync.checking");
        SourceText.Text = Loc.Retranslate(SourceText.Text, "sync.system");
        OffsetText.Text = Loc.Retranslate(OffsetText.Text, "sync.na");
        UpdateTitleTexts();
        UpdateClockCity();
        UpdateHeaderForCurrentPage();
        UpdateGroupButton();
        UpdateSettingsPopup();
        PinButton.ToolTip = Loc.T(Topmost ? "tip.unpin" : "tip.pin");
        if (!_syncing) SyncButton.Content = Loc.T("sync.button");
        BuildScheduleList();
        RefreshProducts();
        RefreshCaffeineSummary();
        _lastRenderedClockSecond = long.MinValue;
        _scheduleKey = "";
        _lastSchoolSecond = -1;
        Title = _page switch { "caffeine" => Loc.T("window.coffee"), "todo" => Loc.T("window.todo"), "notes" => Loc.T("window.notes"), "tutor" => "AI – Eagle 1.1", "weather" => Loc.T("window.weather"), "clicker" => Loc.T("window.clicker"), "viewer" => Loc.T("window.viewer"), _ => "Freedom" };
    }

    private void SettingsPanel_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ThemeList.Columns = SettingsPanel.ActualWidth >= 640 ? 3 : 2;

    private void SettingsExpand_Click(object sender, RoutedEventArgs e)
    {
        // Toggle between the compact panel and a large one that uses most of the window.
        var large = SettingsPanel.Width < 600;
        SettingsPanel.Width = large ? Math.Max(400, Math.Min(820, ActualWidth - 120)) : 400;
        SettingsPanel.Height = large ? Math.Max(380, Math.Min(760, ActualHeight - 140)) : 640;
        SettingsExpandButton.Content = large ? "⤡" : "⤢";
        RememberSettingsSize();
    }

    private void SettingsClose_Click(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = false;

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void SettingsResize_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        SettingsPanel.Width = Math.Clamp(SettingsPanel.Width + e.HorizontalChange, SettingsPanel.MinWidth, Math.Max(SettingsPanel.MinWidth, SystemParameters.WorkArea.Width - 80));
        SettingsPanel.Height = Math.Clamp(SettingsPanel.Height + e.VerticalChange, SettingsPanel.MinHeight, Math.Max(SettingsPanel.MinHeight, SystemParameters.WorkArea.Height - 120));
        RememberSettingsSize();
    }

    private void RememberSettingsSize()
    {
        _settings.SettingsWidth = SettingsPanel.Width;
        _settings.SettingsHeight = SettingsPanel.Height;
    }

    private void SetChip(Button button, bool selected)
    {
        button.SetResourceReference(BackgroundProperty, selected ? "AccentSoft" : "SurfaceAlt");
        button.SetResourceReference(ForegroundProperty, selected ? "AccentBrush" : "TextPrimary");
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Foreground = Topmost ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextSecondary");
        PinButton.ToolTip = Loc.T(Topmost ? "tip.unpin" : "tip.pin");
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        _forceTimetable = true;
        await SynchronizeAsync();
    }

    private void FocusButton_Click(object sender, RoutedEventArgs e) => ToggleFocusMode();

    /// <summary>A maximised borderless window hangs over the screen edges, so the content is pulled back in.</summary>
    private void UpdateMaximizedMargin()
    {
        if (_focusMode) return;
        WindowShell.Margin = WindowState == WindowState.Maximized ? WindowFrame.MaximizedOverhang(this) : new Thickness(0);
    }

    private void ToggleFocusMode()
    {
        if (!_focusMode)
        {
            _previousWindowState = WindowState;
            _previousWindowStyle = WindowStyle;
            _previousBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _previousTopmost = Topmost;

            // Real fullscreen: cover the whole monitor including the taskbar. A maximized
            // borderless window only fills the work area, so the bounds are set by hand.
            var screen = FireworksWindow.MonitorArea(this);
            WindowShell.Margin = new Thickness(0);
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            Left = screen.Left;
            Top = screen.Top;
            Width = screen.Width;
            Height = screen.Height;
            Topmost = true;
            Activate();
            _focusMode = true;
            ApplyShellRadius();
            UpdateViewerFullscreen();
        }
        else
        {
            Topmost = _previousTopmost;
            ResizeMode = ResizeMode.CanResize;
            Left = _previousBounds.Left;
            Top = _previousBounds.Top;
            Width = _previousBounds.Width;
            Height = _previousBounds.Height;
            WindowState = _previousWindowState;
            WindowStyle = _previousWindowStyle;
            _focusMode = false;
            UpdateMaximizedMargin();
            ApplyShellRadius();
            UpdateViewerFullscreen();
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFocusMode();
            e.Handled = true;
        }
        else if (e.Key == Key.F9)
        {
            LaunchFireworks();
            e.Handled = true;
        }
        else if (e.Key == Key.F8)
        {
            LaunchGloom();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _focusMode)
        {
            ToggleFocusMode();
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _focusMode) return;

        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
        {
            // A maximized borderless window must be restored before Windows can drag it.
            if (WindowState == WindowState.Maximized)
            {
                var cursorPosition = PointToScreen(e.GetPosition(this));
                var relativeX = e.GetPosition(this).X / Math.Max(1, ActualWidth);
                WindowState = WindowState.Normal;
                Left = cursorPosition.X - ActualWidth * relativeX;
                Top = Math.Max(0, cursorPosition.Y - 28);
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The mouse may already have been released during a slow frame.
            }
        }
    }

    internal void ShowToast(string title, string text, Brush? stripe)
    {
        ToastTitle.Text = title;
        ToastText.Text = text;
        if (stripe is null) ToastStripe.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        else ToastStripe.Background = stripe;
        System.Windows.Automation.AutomationProperties.SetName(ToastHost, $"{title}. {text}");
        var wasHidden = ToastHost.Visibility != Visibility.Visible;
        ToastHost.Visibility = Visibility.Visible;
        if (wasHidden) Motion.PopIn(ToastHost, 1);
    }

    internal void HideToast() => ToastHost.Visibility = Visibility.Collapsed;

    private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    internal void RecoverFromUiError()
    {
        CustomEntryOverlay.Visibility = Visibility.Collapsed;
        AppSwitcherPopup.IsOpen = false;
        SwitchPage("clock");
    }

    private void AppSwitcherButton_Click(object sender, RoutedEventArgs e) => AppSwitcherPopup.IsOpen = true;

    private void ClockTileButton_Click(object sender, RoutedEventArgs e)
    {
        AppSwitcherPopup.IsOpen = false;
        SwitchPage("clock");
    }

    private void CaffeineTileButton_Click(object sender, RoutedEventArgs e)
    {
        AppSwitcherPopup.IsOpen = false;
        SwitchPage("caffeine");
    }

    private void TodoTileButton_Click(object sender, RoutedEventArgs e)
    {
        AppSwitcherPopup.IsOpen = false;
        SwitchPage("todo");
    }

    private void NotesTileButton_Click(object sender, RoutedEventArgs e)
    {
        AppSwitcherPopup.IsOpen = false;
        SwitchPage("notes");
    }

    private void TutorTileButton_Click(object sender, RoutedEventArgs e)
    {
        AppSwitcherPopup.IsOpen = false;
        SwitchPage("tutor");
    }

    private void WeatherTileButton_Click(object sender, RoutedEventArgs e)
    {
        AppSwitcherPopup.IsOpen = false;
        SwitchPage("weather");
    }

    private void ClickerTileButton_Click(object sender, RoutedEventArgs e)
    {
        AppSwitcherPopup.IsOpen = false;
        SwitchPage("clicker");
    }

    private void ViewerTileButton_Click(object sender, RoutedEventArgs e)
    {
        AppSwitcherPopup.IsOpen = false;
        SwitchPage("viewer");
    }

    private string _pageBeforeViewer = "clock";

    /// <summary>The 3D viewer's full screen also hides Freedom's title bar and margins.</summary>
    private void UpdateViewerFullscreen()
    {
        var full = _focusMode && _page == "viewer";
        ViewerView.SetFullscreen(full);
        TitleBarGrid.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        if (WindowShell.Child is Grid shell) shell.RowDefinitions[0].Height = new GridLength(full ? 0 : 72);
        ViewerView.Margin = full ? new Thickness(0) : new Thickness(24, 4, 24, 24);
    }

    private void SwitchPage(string page)
    {
        var previous = _page;
        if (page == "viewer" && previous != "viewer") _pageBeforeViewer = previous;
        if (previous == "viewer" && page != "viewer" && _focusMode) ToggleFocusMode();
        _page = page is "caffeine" or "todo" or "notes" or "tutor" or "weather" or "clicker" or "viewer" ? page : "clock";
        ClockPage.Visibility = _page == "clock" ? Visibility.Visible : Visibility.Collapsed;
        CaffeineScroll.Visibility = _page == "caffeine" ? Visibility.Visible : Visibility.Collapsed;
        TodoView.Visibility = _page == "todo" ? Visibility.Visible : Visibility.Collapsed;
        NotesView.Visibility = _page == "notes" ? Visibility.Visible : Visibility.Collapsed;
        TutorView.Visibility = _page == "tutor" ? Visibility.Visible : Visibility.Collapsed;
        WeatherView.Visibility = _page == "weather" ? Visibility.Visible : Visibility.Collapsed;
        ClickerView.Visibility = _page == "clicker" ? Visibility.Visible : Visibility.Collapsed;
        ViewerView.Visibility = _page == "viewer" ? Visibility.Visible : Visibility.Collapsed;
        UpdateViewerFullscreen();
        UpdateTitleTexts();
        Title = _page switch { "caffeine" => Loc.T("window.coffee"), "todo" => Loc.T("window.todo"), "notes" => Loc.T("window.notes"), "tutor" => "AI – Eagle 1.1", "weather" => Loc.T("window.weather"), "clicker" => Loc.T("window.clicker"), "viewer" => Loc.T("window.viewer"), _ => "Freedom" };
        _settings.ActivePage = _page;
        if (previous != _page || !IsLoaded)
            Motion.PageIn(_page switch { "caffeine" => CaffeineScroll, "todo" => TodoView, "notes" => NotesView, "tutor" => TutorView, "weather" => WeatherView, "clicker" => ClickerView, "viewer" => ViewerView, _ => (UIElement)ClockPage });
        UpdateNavigationVisuals();
        UpdateHeaderForCurrentPage();
        if (_showingCaffeine) RefreshCaffeineSummary();
    }

    private void UpdateNavigationVisuals()
    {
        if (ClockTileButton is null || CaffeineTileButton is null) return;
        ClockTileButton.Background = (Brush)FindResource(_page == "clock" ? "AccentSoft" : "SurfaceAlt");
        CaffeineTileButton.Background = (Brush)FindResource(_page == "caffeine" ? "AccentSoft" : "SurfaceAlt");
        TodoTileButton.Background = (Brush)FindResource(_page == "todo" ? "AccentSoft" : "SurfaceAlt");
        NotesTileButton.Background = (Brush)FindResource(_page == "notes" ? "AccentSoft" : "SurfaceAlt");
        TutorTileButton.Background = (Brush)FindResource(_page == "tutor" ? "AccentSoft" : "SurfaceAlt");
        WeatherTileButton.Background = (Brush)FindResource(_page == "weather" ? "AccentSoft" : "SurfaceAlt");
        ClickerTileButton.Background = (Brush)FindResource(_page == "clicker" ? "AccentSoft" : "SurfaceAlt");
        ViewerTileButton.Background = (Brush)FindResource(_page == "viewer" ? "AccentSoft" : "SurfaceAlt");
    }

    private void UpdateHeaderForCurrentPage()
    {
        if (_page != "clicker" && ClickerView.IsActive)
        {
            // A running autoclicker stays visible on every page.
            HeaderStatusText.Text = "⌖ " + Loc.T("tile.clicker") + ": " + ClickerView.StatusLine;
            SetStatusColor((Brush)FindResource("GreenBrush"), (Brush)FindResource("GreenSoft"));
            return;
        }
        if (_page == "tutor")
        {
            HeaderStatusText.Text = TutorView.StatusLine;
            SetStatusColor((Brush)FindResource("AccentBrush"), (Brush)FindResource("AccentSoft"));
        }
        else if (_page == "todo")
        {
            HeaderStatusText.Text = TodoView.TodayCount > 0 ? Loc.F("todo.headerToday", TodoView.OpenCount, TodoView.TodayCount) : Loc.F("todo.headerOpen", TodoView.OpenCount);
            SetStatusColor((Brush)FindResource("AccentBrush"), (Brush)FindResource("AccentSoft"));
        }
        else if (_page == "notes")
        {
            HeaderStatusText.Text = NotesView.Count == 1 ? Loc.T("notes.one") : Loc.F("notes.n", NotesView.Count);
            SetStatusColor((Brush)FindResource("AccentBrush"), (Brush)FindResource("AccentSoft"));
        }
        else if (_page == "weather")
        {
            HeaderStatusText.Text = WeatherView.StatusLine;
            SetStatusColor((Brush)FindResource("AccentBrush"), (Brush)FindResource("AccentSoft"));
        }
        else if (_page == "viewer")
        {
            HeaderStatusText.Text = ViewerView.ModelPath is { } model ? System.IO.Path.GetFileName(model) : Loc.T("glb.subtitle");
            SetStatusColor((Brush)FindResource("AccentBrush"), (Brush)FindResource("AccentSoft"));
        }
        else if (_page == "clicker")
        {
            HeaderStatusText.Text = ClickerView.StatusLine;
            var active = ClickerView.IsActive;
            SetStatusColor((Brush)FindResource(active ? "GreenBrush" : "AccentBrush"), (Brush)FindResource(active ? "GreenSoft" : "AccentSoft"));
        }
        else if (_showingCaffeine)
        {
            var total = GetTodayEntries().Sum(e => e.CaffeineMg);
            HeaderStatusText.Text = Loc.F("coffee.header", total);
            SetStatusColor((Brush)FindResource("AccentBrush"), (Brush)FindResource("AccentSoft"));
        }
        else
        {
            HeaderStatusText.Text = _clockHeaderStatus;
            SetStatusColor(
                (Brush)FindResource(_clockHeaderForeground ?? "GreenBrush"),
                (Brush)FindResource(_clockHeaderBackground ?? "GreenSoft"));
        }
    }

    private void ProductSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHintText is not null)
            SearchHintText.Visibility = string.IsNullOrWhiteSpace(ProductSearchTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshProducts();
    }

    private void CategoryButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedCategory = (sender as Button)?.Tag?.ToString() ?? "Alle";
        UpdateCategoryButtons();
        RefreshProducts();
    }

    private void UpdateCategoryButtons()
    {
        if (AllCategoryButton is null) return;
        foreach (var button in new[] { AllCategoryButton, EnergyCategoryButton, CoffeeCategoryButton, MoreCategoryButton })
        {
            var selected = Equals(button.Tag?.ToString(), _selectedCategory);
            button.Background = (Brush)FindResource(selected ? "AccentSoft" : "SurfaceAlt");
            button.Foreground = (Brush)FindResource(selected ? "AccentBrush" : "TextPrimary");
        }
    }

    private void RefreshProducts()
    {
        if (ProductsItemsControl is null) return;
        var query = ProductSearchTextBox?.Text?.Trim().ToLowerInvariant() ?? "";
        var products = CaffeineCatalog.Products.Where(product =>
        {
            var categoryMatches = _selectedCategory switch
            {
                "Alle" => true,
                "Mehr" => product.Category is "Eiskaffee" or "Tee" or "Softdrink",
                _ => product.Category == _selectedCategory
            };
            return categoryMatches && (query.Length == 0 || product.SearchText.Contains(query));
        }).ToList();

        ProductsItemsControl.ItemsSource = products;
        if (CatalogCountText is not null)
            CatalogCountText.Text = Loc.F("coffee.count", products.Count, CaffeineCatalog.Products.Count);
    }

    private void AddProductButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not CaffeineProduct product) return;
        AddEntry(new CaffeineEntry
        {
            Brand = product.Brand,
            ProductName = product.Name,
            Category = product.Category,
            ServingMl = product.ServingMl,
            CaffeineMg = product.CaffeineMg,
            Icon = product.Icon,
            ConsumedAt = DateTimeOffset.Now
        });
    }

    private void AddEntry(CaffeineEntry entry)
    {
        _caffeineStore.Entries.Add(entry);
        _caffeineStore.Save();
        RefreshCaffeineSummary();
    }

    private void DeleteEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not CaffeineEntry entry) return;
        _caffeineStore.Entries.RemoveAll(item => item.Id == entry.Id);
        _caffeineStore.Save();
        RefreshCaffeineSummary();
    }

    private List<CaffeineEntry> GetTodayEntries()
    {
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, _viennaZone).Date;
        return _caffeineStore.Entries
            .Where(entry => TimeZoneInfo.ConvertTime(entry.ConsumedAt, _viennaZone).Date == today)
            .OrderByDescending(entry => entry.ConsumedAt)
            .ToList();
    }

    private void RefreshCaffeineSummary()
    {
        if (CaffeineTotalText is null) return;
        var entries = GetTodayEntries();
        var total = entries.Sum(entry => entry.CaffeineMg);
        var limit = Math.Clamp(_settings.CaffeineDailyLimitMg, 50, 2000);
        var now = DateTimeOffset.Now;
        var active = _caffeineStore.Entries
            .Where(entry => entry.ConsumedAt <= now && entry.ConsumedAt >= now.AddHours(-48))
            .Sum(entry => entry.CaffeineMg * Math.Pow(0.5, (now - entry.ConsumedAt).TotalHours / 5.0));

        var viennaNow = TimeZoneInfo.ConvertTime(now, _viennaZone);
        TrackerDateText.Text = $"{viennaNow.ToString("dddd", _culture)}, {viennaNow.ToString("M", _culture)}";
        CaffeineTotalText.Text = total.ToString(_culture);
        ActiveCaffeineText.Text = $"{active:0} mg";
        RemainingCaffeineText.Text = $"{Math.Max(0, limit - total)} mg";
        DailyLimitTextBox.Text = limit.ToString(_culture);
        EntryCountText.Text = entries.Count == 0 ? Loc.T("coffee.noEntries") : entries.Count == 1 ? Loc.T("coffee.oneEntry") : Loc.F("coffee.nEntries", entries.Count);
        HistoryItemsControl.ItemsSource = entries;
        EmptyHistoryPanel.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressArc(total / (double)limit);

        if (total >= limit)
        {
            LimitStatusText.Text = Loc.T("coffee.over");
            LimitStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107));
            LimitStatusBadge.Background = new SolidColorBrush(Color.FromArgb(38, 255, 107, 107));
        }
        else if (total >= limit * 0.8)
        {
            LimitStatusText.Text = Loc.T("coffee.near");
            LimitStatusText.Foreground = (Brush)FindResource("OrangeBrush");
            LimitStatusBadge.Background = (Brush)FindResource("AccentSoft");
        }
        else
        {
            LimitStatusText.Text = Loc.T("coffee.ok");
            LimitStatusText.Foreground = (Brush)FindResource("GreenBrush");
            LimitStatusBadge.Background = (Brush)FindResource("GreenSoft");
        }

        TrackerInsightText.Text = entries.Count == 0
            ? Loc.T("coffee.insightEmpty")
            : Loc.F("coffee.insight", active.ToString("0"));

        if (_showingCaffeine) UpdateHeaderForCurrentPage();
    }

    private void UpdateProgressArc(double progress)
    {
        if (CaffeineProgressArc is null) return;
        if (progress <= 0)
        {
            CaffeineProgressArc.Data = Geometry.Empty;
            return;
        }

        const double center = 95;
        const double radius = 82.5;
        var angle = Math.Min(359.9, progress * 360.0);
        var radians = (angle - 90) * Math.PI / 180.0;
        var end = new Point(center + radius * Math.Cos(radians), center + radius * Math.Sin(radians));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center, center - radius), false, false);
            context.ArcTo(end, new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        CaffeineProgressArc.Data = geometry;
    }

    private void DailyLimitTextBox_LostFocus(object sender, RoutedEventArgs e) => SaveDailyLimit();

    private void DailyLimitTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveDailyLimit();
        Keyboard.ClearFocus();
    }

    private void SaveDailyLimit()
    {
        if (int.TryParse(DailyLimitTextBox.Text, out var limit))
            _settings.CaffeineDailyLimitMg = Math.Clamp(limit, 50, 2000);
        DailyLimitTextBox.Text = _settings.CaffeineDailyLimitMg.ToString(_culture);
        _settings.Save();
        RefreshCaffeineSummary();
    }

    private void OpenCustomEntryButton_Click(object sender, RoutedEventArgs e)
    {
        CustomEntryErrorText.Text = "";
        CustomEntryOverlay.Visibility = Visibility.Visible;
        CustomBrandTextBox.Focus();
    }

    private void CloseCustomEntryButton_Click(object sender, RoutedEventArgs e) => CustomEntryOverlay.Visibility = Visibility.Collapsed;

    private void SaveCustomEntryButton_Click(object sender, RoutedEventArgs e)
    {
        var name = CustomNameTextBox.Text.Trim();
        var brand = CustomBrandTextBox.Text.Trim();
        if (name.Length == 0 || !int.TryParse(CustomMgTextBox.Text, out var mg) || mg < 0 || mg > 2000 ||
            !int.TryParse(CustomMlTextBox.Text, out var ml) || ml < 0 || ml > 5000)
        {
            CustomEntryErrorText.Text = Loc.T("custom.error");
            return;
        }

        AddEntry(new CaffeineEntry
        {
            Brand = brand.Length == 0 ? Loc.T("custom.ownProduct") : brand,
            ProductName = name,
            Category = "Eigenes Produkt",
            ServingMl = ml,
            CaffeineMg = mg,
            Icon = "＋",
            ConsumedAt = DateTimeOffset.Now
        });

        CustomNameTextBox.Clear();
        CustomBrandTextBox.Clear();
        CustomEntryOverlay.Visibility = Visibility.Collapsed;
    }
}
