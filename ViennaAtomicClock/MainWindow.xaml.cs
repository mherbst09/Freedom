using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    private readonly CultureInfo _culture = CultureInfo.GetCultureInfo("de-AT");
    private CancellationTokenSource? _syncCancellation;
    private bool _focusMode;
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private Rect _previousBounds;
    private bool _previousTopmost;
    private string _page = "clock";
    private bool _pickingColor;
    private bool _showingCaffeine => _page == "caffeine";
    private string _selectedCategory = "Alle";
    private string _clockHeaderStatus = "Synchronisiere …";
    private string? _clockHeaderForeground;
    private string? _clockHeaderBackground;
    private int _lastTrackerRefreshMinute = -1;
    private DateTime _lastFireworkSlot = DateTime.MinValue;
    private FireworksWindow? _fireworks;
    private readonly List<(Lesson Lesson, Border Row, TextBlock Status, TextBlock Subject)> _scheduleRows = new();
    private TimetableData? _timetable;
    private DateTime _lastTimetableAttempt = DateTime.MinValue;
    private bool _timetableLoading;
    private bool _forceTimetable = true;
    private bool _timetableOffline;
    private int _timetableVersion;
    private long _lastSchoolSecond = -1;
    private string _scheduleKey = "";

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
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
        AnimeTop.Remark = () => _page == "clock" && LessonLabelText.Text == "Stunde endet in" && LessonCountdownText.Text.Length >= 5
            ? $"Noch {LessonCountdownText.Text[..2].TrimStart('0').PadLeft(1, '0')} min! ♡"
            : null;
        AnimeTop.SeatAnchor = () => ClockPage.IsVisible && ClockCard.IsVisible
            ? ClockCard.TransformToAncestor(WindowShell).TransformBounds(new Rect(ClockCard.RenderSize))
            : null;
        ApplyThemeVisuals();
        TodoView.Changed += () => { if (_page == "todo") UpdateHeaderForCurrentPage(); };
        NotesView.Changed += () => { if (_page == "notes") UpdateHeaderForCurrentPage(); };

        _viennaZone = FindViennaTimeZone();
        DailyLimitTextBox.Text = _settings.CaffeineDailyLimitMg.ToString(_culture);
        RefreshProducts();
        RefreshCaffeineSummary();
        _timetable = WebUntisTimetable.LoadCache();
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
            _syncCancellation?.Cancel();
            _fireworks?.Close();
            _settings.DarkMode = App.IsDark;
            _settings.AlwaysOnTop = _focusMode ? _previousTopmost : Topmost;
            _settings.ActivePage = _page;
            TodoView.Flush();
            NotesView.Flush();
            _settings.Save();
            _caffeineStore.Save();
        };

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
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
        var microseconds = (vienna.Ticks % TimeSpan.TicksPerSecond) / 10;

        MainTimeText.Text = vienna.ToString("HH:mm:ss", _culture);
        MicrosecondsText.Text = $".{microseconds:000000}";
        DateText.Text = vienna.ToString("dddd, d. MMMM yyyy", _culture);
        SecondProgress.ScaleX = (vienna.Second + vienna.Millisecond / 1000.0) / 60.0;

        var offset = _viennaZone.GetUtcOffset(vienna.DateTime);
        UtcOffsetBadge.Text = $"UTC{(offset < TimeSpan.Zero ? "−" : "+")}{offset.Duration():hh\\:mm}";
        DstText.Text = _viennaZone.IsDaylightSavingTime(vienna.DateTime) ? "Sommerzeit (MESZ)" : "Normalzeit (MEZ)";

        CheckFireworks(vienna.DateTime);

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
        LaunchFireworks();
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
                    Text = $"☕  {(lesson.Start - previous.End).TotalMinutes:0} min Pause",
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
                LessonBadgeText.Text = $"{lesson.Number}. STUNDE";
                LessonRangeText.Text = $"{lesson.Start:hh\\:mm} – {lesson.End:hh\\:mm}{SlotLabel(now.Date, lesson, " · ")}";
                LessonOfDayText.Text = $"Stunde {todayLessons.IndexOf(lesson) + 1} von {lessonCount}";
                LessonLabelText.Text = "Stunde endet in";
                LessonNextText.Text = state.Next is { } next
                    ? next.Start - lesson.End > TimeSpan.FromMinutes(5)
                        ? $"Danach {(next.Start - lesson.End).TotalMinutes:0} min Pause · {next.Number}. Stunde{SlotLabel(now.Date, next, " ")} ab {next.Start:hh\\:mm}"
                        : $"Danach {next.Number}. Stunde{SlotLabel(now.Date, next, " ")} ab {next.Start:hh\\:mm}"
                    : "Danach: Schulschluss 🎉";
                // Last five minutes: the countdown turns orange.
                if (remaining <= TimeSpan.FromMinutes(5)) accent = "OrangeBrush";
                break;

            case SchoolPhase.Break:
                LessonBadgeText.Text = "PAUSE";
                LessonRangeText.Text = $"{state.Current!.End:hh\\:mm} – {state.Next!.Start:hh\\:mm}";
                LessonOfDayText.Text = $"{todayLessons.IndexOf(state.Current) + 1} von {lessonCount} geschafft";
                LessonLabelText.Text = "Nächste Stunde in";
                LessonNextText.Text = $"{state.Next.Number}. Stunde{SlotLabel(now.Date, state.Next, " · ")} · {state.Next.Start:hh\\:mm} – {state.Next.End:hh\\:mm}";
                accent = "GreenBrush";
                soft = "GreenSoft";
                break;

            case SchoolPhase.BeforeSchool:
                LessonBadgeText.Text = "VOR UNTERRICHT";
                LessonRangeText.Text = "";
                LessonOfDayText.Text = $"{lessonCount} Stunden heute";
                LessonLabelText.Text = "Unterricht beginnt in";
                LessonNextText.Text = $"{state.Next!.Number}. Stunde{SlotLabel(now.Date, state.Next, " · ")} · {state.Next.Start:hh\\:mm} – {state.Next.End:hh\\:mm}";
                accent = "OrangeBrush";
                break;

            default:
                var weekend = state.Phase == SchoolPhase.Weekend;
                var realWeekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                LessonBadgeText.Text = weekend ? (realWeekend ? "WOCHENENDE" : "UNTERRICHTSFREI") : "SCHULSCHLUSS";
                LessonRangeText.Text = "";
                LessonOfDayText.Text = weekend ? "" : $"{lessonCount} von {lessonCount} geschafft";
                LessonLabelText.Text = weekend ? (realWeekend ? "Schönes Wochenende!" : "Heute kein Unterricht") : "Geschafft für heute 🎉";
                LessonNextText.Text = $"Weiter am {state.PhaseEnd.ToString("dddd", _culture)} um {state.PhaseEnd:HH\\:mm}";
                accent = "GreenBrush";
                soft = "GreenSoft";
                break;
        }

        LessonCountdownText.Text = state.Phase is SchoolPhase.AfterSchool or SchoolPhase.Weekend ? "frei" : FormatCountdown(remaining);
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
            : day == now.Date.AddDays(1) ? "Morgen" : day.ToString("dddd, d. M.", _culture);
        var covered = _timetable?.Covers(day) == true;
        var source = covered
            ? $"{WebUntisTimetable.ClassName} · Stand {_timetable!.FetchedAt:HH\\:mm}{(_timetableOffline ? " (offline)" : "")}"
            : _timetableLoading ? "Stundenplan wird geladen …" : "feste Zeiten (kein Stundenplan)";
        ScheduleSubtitleText.Text = $"{dayName} · {source}";

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
                subject.Inlines.Add(new Run(free ? "frei" : ""));
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
                SetStatus(status, "● jetzt", "AccentBrush");
            }
            else if (cancelled && !isDone)
                SetStatus(status, "Entfall", "OrangeBrush");
            else if (exam && !isDone)
                SetStatus(status, "Test", "OrangeBrush");
            else if (changed && !isDone)
                SetStatus(status, "Vertretung", "OrangeBrush");
            else if (isUpcoming)
            {
                row.SetResourceReference(Border.BorderBrushProperty, "GreenBrush");
                SetStatus(status, "als Nächstes", "GreenBrush");
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
        return prefix + string.Join(" / ", slot.Select(e => e.Room.Length > 0 ? $"{e.Subject} in {e.Room}" : e.Subject));
    }

    private async Task RefreshTimetableAsync(bool force, CancellationToken cancellation)
    {
        if (_timetableLoading || (!force && DateTime.Now - _lastTimetableAttempt < TimeSpan.FromMinutes(10))) return;
        _timetableLoading = true;
        _lastTimetableAttempt = DateTime.Now;
        try
        {
            var today = TimeZoneInfo.ConvertTime(_clock.UtcNow, _viennaZone).DateTime;
            _timetable = await WebUntisTimetable.FetchAsync(today, cancellation);
            _timetableOffline = false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Offline or WebUntis unreachable: keep the last saved timetable (or the fixed times).
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
        GroupButton.Content = _settings.TimetableGroup == 0 ? "Gruppe: beide" : $"Gruppe {_settings.TimetableGroup}";

    private async Task SynchronizeAsync()
    {
        if (_syncCancellation is not null) return;

        _syncCancellation = new CancellationTokenSource();
        SyncButton.IsEnabled = false;
        SyncButton.Content = "↻  Läuft …";
        SyncStatusText.Text = "Verbindung …";
        _clockHeaderStatus = "Synchronisiere …";
        if (_page == "clock") HeaderStatusText.Text = _clockHeaderStatus;

        try
        {
            var result = await NtpClient.SynchronizeAsync(_syncCancellation.Token);
            if (result.Success)
            {
                _clock.Set(result.CorrectedUtc);
                SourceText.Text = result.Server;
                OffsetText.Text = FormatOffset(result.Offset);
                LatencyText.Text = $"{result.RoundTrip.TotalMilliseconds:0.0} ms";
                LastSyncText.Text = TimeZoneInfo.ConvertTime(result.CorrectedUtc, _viennaZone).ToString("HH:mm:ss", _culture);
                SyncStatusText.Text = "Synchronisiert";
                SetClockStatus("NTP synchronisiert", "GreenBrush", "GreenSoft");
            }
            else
            {
                _clock.Set(DateTimeOffset.UtcNow);
                SourceText.Text = "Windows-Systemzeit";
                OffsetText.Text = "nicht verfügbar";
                LatencyText.Text = "— ms";
                SyncStatusText.Text = "Offline-Modus";
                SetClockStatus("Systemzeit", "OrangeBrush", "AccentSoft");
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
            SyncButton.Content = "↻  Synchronisieren";
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

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSettingsPopup();
        SettingsPopup.IsOpen = true;
    }

    private void LightModeButton_Click(object sender, RoutedEventArgs e) => SetLook(App.ThemeName, false, null);
    private void DarkModeButton_Click(object sender, RoutedEventArgs e) => SetLook(App.ThemeName, true, null);
    private void ColorModeButton_Click(object sender, RoutedEventArgs e) =>
        SetLook(App.ThemeName, dark: true, App.ParseColor(_settings.CustomColor) ?? Color.FromRgb(61, 123, 255));

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

        var accent = ((SolidColorBrush)FindResource("AccentBrush")).Color;
        MainTimeText.Effect = theme switch
        {
            Themes.Hacker => new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 18, ShadowDepth = 0, Opacity = App.IsDark ? 0.9 : 0.35 },
            Themes.Anime => new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.55 },
            Themes.Tetris => new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.6 },
            Themes.America => new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(10, 31, 68), BlurRadius = 0, ShadowDepth = 4, Direction = 315, Opacity = App.IsDark ? 0.9 : 0.35 },
            // Hard pixel shadow like game text.
            Themes.Minecraft => new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 0, ShadowDepth = 5, Direction = 315, Opacity = 0.6 },
            _ => App.CustomColor is not null ? new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.45 } : null
        };
        LessonCountdownText.Effect = theme == Themes.Hacker && App.IsDark
            ? new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.8 }
            : null;
        AppSwitcherButton.Content = theme switch
        {
            Themes.Hacker => ">_", Themes.Anime => "🐾", Themes.Tetris => "▦", Themes.America => "★", Themes.Minecraft => "⛏", _ => "⊞"
        };
        AppSwitcherButton.FontSize = theme == Themes.Hacker ? 17 : 22;

        ApplyShellRadius();
        UpdateTitleTexts();
        UpdateSettingsPopup();
    }

    private void ApplyShellRadius()
    {
        var radius = _focusMode ? 0 : Themes.WindowRadius(App.ThemeName);
        WindowShell.CornerRadius = new CornerRadius(radius);
        var inner = Math.Max(0, radius - 1);
        FilmGrain.Radius = inner;
        HackerRainLayer.Radius = inner;
        HackerScanLayer.Radius = inner;
        AnimeLayer.Radius = inner;
        AnimeTop.Radius = inner;
        TetrisLayer.Radius = inner;
        AmericaLayer.Radius = inner;
        MinecraftLayer.Radius = inner;
        MinecraftTop.Radius = inner;
    }

    private void UpdateTitleTexts()
    {
        var theme = App.ThemeName;
        if (_page == "tutor")
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "EAGLE_1.1_", Themes.Anime => "Eagle 1.1 ♡", _ => "EAGLE 1.1" };
            AppSubtitleText.Text = "AI · lokal über Ollama · ohne Cloud";
        }
        else if (_page == "todo")
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "TODO.SH_", Themes.Anime => "Aufgaben ♡", _ => "AUFGABEN" };
            AppSubtitleText.Text = theme switch
            {
                Themes.Hacker => "root@freedom:~$ todo --list",
                Themes.Anime => "nya~ eins nach dem anderen!",
                _ => "Befehle für heute · lokal gespeichert"
            };
        }
        else if (_page == "notes")
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "NOTES.TXT_", Themes.Anime => "Notizen ♡", _ => "NOTIZEN" };
            AppSubtitleText.Text = theme switch
            {
                Themes.Hacker => "root@freedom:~$ vim notes",
                Themes.Anime => "nya~ Gedanken sammeln",
                _ => "Feldtagebuch · automatisch gespeichert"
            };
        }
        else if (_showingCaffeine)
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "CAFFEINE.EXE_", Themes.Anime => "Koffein ♡", _ => "CAFFEINE TRACKER" };
            AppSubtitleText.Text = theme switch
            {
                Themes.Hacker => "root@freedom:~$ track --caffeine",
                Themes.Anime => "nya~ wie viel Energie heute?",
                _ => "Koffein im Blick · lokal gespeichert"
            };
        }
        else
        {
            AppTitleText.Text = theme switch { Themes.Hacker => "FREEDOM_", Themes.Anime => "Freedom ♡", Themes.America => "FREEDOM ★", _ => "FREEDOM" };
            AppSubtitleText.Text = theme switch
            {
                Themes.Hacker => "root@freedom:~$ sync --time --timetable",
                Themes.Anime => "nya~ Atomzeit & Stundenplan ฅ^•ﻌ•^ฅ",
                Themes.Tetris => "LEVEL 4BHITS · Atomzeit & Stundenplan",
                Themes.America => "Land of the free · Home of the Stundenplan",
                Themes.Minecraft => "Survival-Modus · Atomzeit & Stundenplan",
                _ => "Atomzeit · Stundenplan · Wien"
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

        ThemeList.Children.Clear();
        foreach (var (id, label, glyph, hint) in Themes.All)
        {
            var selected = id == App.ThemeName;
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new TextBlock { Text = glyph, FontSize = 20, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = label + (selected ? "  ✓" : ""), FontWeight = FontWeights.Bold, FontSize = 13 });
            var hintText = new TextBlock { Text = hint, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
            hintText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            texts.Children.Add(hintText);
            Grid.SetColumn(texts, 1);
            content.Children.Add(icon);
            content.Children.Add(texts);

            var button = new Button
            {
                Content = content,
                Height = 56,
                Margin = new Thickness(0, 0, 6, 6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 0, 12, 0),
                Tag = id
            };
            button.SetResourceReference(StyleProperty, "SecondaryButton");
            button.SetResourceReference(BackgroundProperty, selected ? "AccentSoft" : "SurfaceAlt");
            button.Click += (_, _) => SetLook(id, _settings.DarkMode, App.CustomColor);
            ThemeList.Children.Add(button);
        }
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
        PinButton.ToolTip = Topmost ? "Vordergrundmodus deaktivieren" : "Immer im Vordergrund";
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        _forceTimetable = true;
        await SynchronizeAsync();
    }

    private void FocusButton_Click(object sender, RoutedEventArgs e) => ToggleFocusMode();

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
            WindowShell.CornerRadius = new CornerRadius(0);
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
            WindowShell.Margin = new Thickness(14);
            _focusMode = false;
            ApplyShellRadius();
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

    private void SwitchPage(string page)
    {
        _page = page is "caffeine" or "todo" or "notes" or "tutor" ? page : "clock";
        ClockPage.Visibility = _page == "clock" ? Visibility.Visible : Visibility.Collapsed;
        CaffeineScroll.Visibility = _page == "caffeine" ? Visibility.Visible : Visibility.Collapsed;
        TodoView.Visibility = _page == "todo" ? Visibility.Visible : Visibility.Collapsed;
        NotesView.Visibility = _page == "notes" ? Visibility.Visible : Visibility.Collapsed;
        TutorView.Visibility = _page == "tutor" ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitleTexts();
        Title = _page switch { "caffeine" => "Caffeine Tracker", "todo" => "Aufgaben", "notes" => "Notizen", "tutor" => "AI – Eagle 1.1", _ => "Freedom" };
        _settings.ActivePage = _page;
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
    }

    private void UpdateHeaderForCurrentPage()
    {
        if (_page == "tutor")
        {
            HeaderStatusText.Text = TutorView.StatusLine;
            SetStatusColor((Brush)FindResource("AccentBrush"), (Brush)FindResource("AccentSoft"));
        }
        else if (_page == "todo")
        {
            HeaderStatusText.Text = TodoView.TodayCount > 0 ? $"{TodoView.OpenCount} offen · {TodoView.TodayCount} heute" : $"{TodoView.OpenCount} offen";
            SetStatusColor((Brush)FindResource("AccentBrush"), (Brush)FindResource("AccentSoft"));
        }
        else if (_page == "notes")
        {
            HeaderStatusText.Text = NotesView.Count == 1 ? "1 Notiz" : $"{NotesView.Count} Notizen";
            SetStatusColor((Brush)FindResource("AccentBrush"), (Brush)FindResource("AccentSoft"));
        }
        else if (_showingCaffeine)
        {
            var total = GetTodayEntries().Sum(e => e.CaffeineMg);
            HeaderStatusText.Text = $"Heute {total} mg";
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
            CatalogCountText.Text = $"{products.Count} von {CaffeineCatalog.Products.Count} Produkten";
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
        TrackerDateText.Text = viennaNow.ToString("dddd, d. MMMM", _culture);
        CaffeineTotalText.Text = total.ToString(_culture);
        ActiveCaffeineText.Text = $"{active:0} mg";
        RemainingCaffeineText.Text = $"{Math.Max(0, limit - total)} mg";
        DailyLimitTextBox.Text = limit.ToString(_culture);
        EntryCountText.Text = entries.Count == 0 ? "Noch keine Einträge" : entries.Count == 1 ? "1 Eintrag heute" : $"{entries.Count} Einträge heute";
        HistoryItemsControl.ItemsSource = entries;
        EmptyHistoryPanel.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressArc(total / (double)limit);

        if (total >= limit)
        {
            LimitStatusText.Text = "Limit erreicht";
            LimitStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107));
            LimitStatusBadge.Background = new SolidColorBrush(Color.FromArgb(38, 255, 107, 107));
        }
        else if (total >= limit * 0.8)
        {
            LimitStatusText.Text = "nahe am Limit";
            LimitStatusText.Foreground = (Brush)FindResource("OrangeBrush");
            LimitStatusBadge.Background = (Brush)FindResource("AccentSoft");
        }
        else
        {
            LimitStatusText.Text = "im Rahmen";
            LimitStatusText.Foreground = (Brush)FindResource("GreenBrush");
            LimitStatusBadge.Background = (Brush)FindResource("GreenSoft");
        }

        TrackerInsightText.Text = entries.Count == 0
            ? "Deine Koffeinbilanz erscheint nach dem ersten Eintrag."
            : $"Noch etwa {active:0} mg geschätzt aktiv. Berechnung mit einer vereinfachten Halbwertszeit von 5 Stunden.";

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
            CustomEntryErrorText.Text = "Bitte Produktname sowie gültige ml- und mg-Werte eingeben.";
            return;
        }

        AddEntry(new CaffeineEntry
        {
            Brand = brand.Length == 0 ? "Eigenes Produkt" : brand,
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
