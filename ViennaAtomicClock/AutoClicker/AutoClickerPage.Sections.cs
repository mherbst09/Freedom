using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ViennaAtomicClock.Weather;

namespace ViennaAtomicClock.AutoClicker;

internal sealed partial class AutoClickerPage
{
    private Border? _statusPill;
    private Ellipse? _statusDot;
    private TextBlock? _statusText;
    private TextBlock? _profileText;
    private TextBlock? _hotkeyHint;
    private Button? _startButton;
    private Button? _pauseButton;
    private Button? _emergencyButton;
    private TextBlock? _countdownText;
    private Border? _progressFill;
    private Grid? _progressTrack;
    private TextBlock? _progressText;
    private LineChart? _cpsChart;
    private UniformGrid? _metricGrid;
    private Grid? _statusLayout;
    private StackPanel? _statusControls;
    private readonly Dictionary<string, TextBlock> _metrics = new();
    private TextBlock? _intervalInfo;
    private Border? _highCpsNotice;
    private TextBlock? _jitterPreview;
    private TextBlock? _recordingStatus;
    private MonitorPreview? _preview;
    private string _lastHeader = "";
    private DateTime _lastHeaderUpdate;

    // ───────────────────────── small builders ─────────────────────────

    private static Button Chip(string text, bool selected, Action click, string? name = null)
    {
        var chip = Ui.Button("ChipButton", text, (_, _) => click());
        chip.Height = 34;
        chip.Margin = new Thickness(0, 0, 6, 6);
        chip.Padding = new Thickness(12, 0, 12, 0);
        Ui.Select(chip, selected);
        AutomationProperties.SetName(chip, name ?? text);
        AutomationProperties.SetItemStatus(chip, selected ? Loc.T("ac.selected") : "");
        return chip;
    }

    /// <summary>On/off switch as a chip (keyboard and screen reader friendly).</summary>
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

    private static TextBlock Label(string text)
    {
        var label = Ui.Text(text, 11.5, "TextSecondary");
        label.Margin = new Thickness(0, 0, 0, 4);
        return label;
    }

    private static TextBlock Info(string text, string brush = "TextMuted", double size = 11.5)
    {
        var block = Ui.Text(text, size, brush);
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        block.Margin = new Thickness(0, 4, 0, 6);
        return block;
    }

    private static TextBlock Heading(string text)
    {
        var block = Ui.Text(text.ToUpper(Loc.Culture), 10.5, "TextMuted", bold: true);
        block.Margin = new Thickness(0, 10, 0, 6);
        return block;
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, Loc.Culture, out value) || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Labelled number box. <paramref name="apply"/> returns an error text key or null.</summary>
    private StackPanel Number(string label, double value, string format, Func<double, string?> apply, double width = 96, string? help = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 10, 8) };
        stack.Children.Add(Label(label));
        var (host, box) = Ui.Input("");
        box.Height = 38;
        box.Text = value.ToString(format, Loc.Culture);
        host.Width = width;
        host.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(box, label);
        if (help is not null)
        {
            box.ToolTip = help;
            AutomationProperties.SetHelpText(box, help);
        }
        box.TextChanged += (_, _) =>
        {
            string? error = TryNumber(box.Text, out var parsed) && !double.IsNaN(parsed) && !double.IsInfinity(parsed) ? apply(parsed) : "ac.err.number";
            if (error is null)
            {
                box.SetResourceReference(Control.BorderBrushProperty, "CardBorder");
                box.ToolTip = help;
            }
            else
            {
                box.SetResourceReference(Control.BorderBrushProperty, "OrangeBrush");
                box.ToolTip = Loc.T(error);
            }
            AutomationProperties.SetHelpText(box, error is null ? help ?? "" : Loc.T(error));
        };
        stack.Children.Add(host);
        return stack;
    }

    private static WrapPanel Row(params UIElement[] children)
    {
        var row = new WrapPanel();
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private Border Card(string titleKey, string glyph, UIElement content)
    {
        var stack = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var icon = Ui.Text(glyph, 15, "AccentBrush");
        icon.Margin = new Thickness(0, 0, 8, 0);
        title.Children.Add(icon);
        title.Children.Add(Ui.Text(Loc.T(titleKey), 15, bold: true, title: true));
        stack.Children.Add(title);
        stack.Children.Add(content);
        var card = Ui.Card(stack, 18);
        AutomationProperties.SetName(card, Loc.T(titleKey));
        return card;
    }

    private void BuildCards()
    {
        void Define(string name, Func<UIElement> build)
        {
            if (!_cards.TryGetValue(name, out var entry))
            {
                entry = (new Border { Margin = new Thickness(0, 0, 0, 14) }, build);
                _cards[name] = entry;
            }
            entry.Host.Child = build();
        }

        Define("interval", () => Card("ac.card.interval", "◷", BuildInterval()));
        Define("jitter", () => Card("ac.card.jitter", "≈", BuildJitter()));
        Define("click", () => Card("ac.card.click", "◉", BuildClick()));
        Define("repeat", () => Card("ac.card.repeat", "↻", BuildRepeat()));
        Define("position", () => Card("ac.card.position", "⌖", BuildPosition()));
        Define("hotkeys", () => Card("ac.card.hotkeys", "⌨", BuildHotkeys()));
        Define("profiles", () => Card("ac.card.profiles", "▤", BuildProfiles()));
        Define("recording", () => Card("ac.card.recording", "●", BuildRecording()));
        Define("safety", () => Card("ac.card.safety", "⛨", BuildSafety()));
        Define("test", () => Card("ac.card.test", "⚗", BuildTest()));
        Define("messages", () => Card("ac.card.messages", "✉", BuildMessages()));
        foreach (var name in BottomCards) _cards[name].Host.Margin = new Thickness(0, 0, 7, 14);
        _layoutMode = "";
        ApplyLayout();
        UpdateStatus();
    }

    private void RefreshCard(string name)
    {
        if (_cards.TryGetValue(name, out var entry)) entry.Host.Child = entry.Build();
    }

    // ───────────────────────── status card ─────────────────────────

    private UIElement BuildStatusCard()
    {
        _statusLayout = new Grid();
        _statusLayout.ColumnDefinitions.Add(new ColumnDefinition());
        _statusLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _statusLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _statusLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel();
        var pillContent = new StackPanel { Orientation = Orientation.Horizontal };
        _statusDot = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _statusText = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        pillContent.Children.Add(_statusDot);
        pillContent.Children.Add(_statusText);
        _statusPill = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 6, 16, 6), Child = pillContent, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetLiveSetting(_statusPill, AutomationLiveSetting.Polite);
        head.Children.Add(_statusPill);
        _profileText = Ui.Text("", 12.5, "TextSecondary");
        _profileText.Margin = new Thickness(2, 8, 0, 0);
        head.Children.Add(_profileText);
        _hotkeyHint = Ui.Text("", 11.5, "TextMuted");
        _hotkeyHint.Margin = new Thickness(2, 3, 0, 0);
        _hotkeyHint.TextWrapping = TextWrapping.Wrap;
        head.Children.Add(_hotkeyHint);
        _countdownText = Ui.Text("", 30, "OrangeBrush", bold: true);
        _countdownText.Visibility = Visibility.Collapsed;
        head.Children.Add(_countdownText);
        _statusLayout.Children.Add(head);

        _statusControls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        _startButton = Ui.Button("ActionButton", "", (_, _) => { if (_engine.State == AutoClickerState.Idle && !_playing) StartRun(); else StopRun(); });
        _startButton.Height = 52;
        _startButton.MinWidth = 170;
        _startButton.FontSize = 16;
        _startButton.Margin = new Thickness(0, 0, 8, 0);
        _pauseButton = Ui.Button("SecondaryButton", "", (_, _) => TogglePause());
        _pauseButton.Height = 52;
        _pauseButton.MinWidth = 110;
        _pauseButton.Margin = new Thickness(0, 0, 8, 0);
        _emergencyButton = Ui.Button("SecondaryButton", "", (_, _) => EmergencyStop());
        _emergencyButton.Height = 52;
        _emergencyButton.MinWidth = 120;
        _emergencyButton.SetResourceReference(Control.ForegroundProperty, "OrangeBrush");
        _statusControls.Children.Add(_startButton);
        _statusControls.Children.Add(_pauseButton);
        _statusControls.Children.Add(_emergencyButton);
        SetColumn(_statusControls, 1);
        _statusLayout.Children.Add(_statusControls);

        _metricGrid = new UniformGrid { Columns = 5, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var key in new[] { "target", "actual", "average", "clicks", "failed", "runtime", "remaining", "skipped", "deviation", "cursor" })
        {
            var tile = new StackPanel { Margin = new Thickness(0, 0, 12, 10) };
            tile.Children.Add(Ui.Text(Loc.T("ac.metric." + key), 10.5, "TextMuted"));
            var value = Ui.Text("–", 17, bold: true);
            tile.Children.Add(value);
            AutomationProperties.SetName(value, Loc.T("ac.metric." + key));
            _metrics[key] = value;
            _metricGrid.Children.Add(tile);
        }
        SetRow(_metricGrid, 1);
        SetColumnSpan(_metricGrid, 2);
        _statusLayout.Children.Add(_metricGrid);

        var bottom = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        var progress = new StackPanel();
        _progressTrack = new Grid { Height = 8 };
        var track = new Border { CornerRadius = new CornerRadius(4) };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        _progressFill = new Border { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _progressFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _progressTrack.Children.Add(track);
        _progressTrack.Children.Add(_progressFill);
        progress.Children.Add(_progressTrack);
        _progressText = Ui.Text("", 11, "TextMuted");
        _progressText.Margin = new Thickness(0, 4, 0, 0);
        progress.Children.Add(_progressText);
        bottom.Children.Add(progress);
        _cpsChart = new LineChart { Height = 46, Fill = true, RevealOnShow = false, Minimum = 0 };
        AutomationProperties.SetName(_cpsChart, Loc.T("ac.chart"));
        SetColumn(_cpsChart, 2);
        bottom.Children.Add(_cpsChart);
        SetRow(bottom, 2);
        SetColumnSpan(bottom, 2);
        _statusLayout.Children.Add(bottom);

        RebuildStatusTexts();
        return Ui.Card(_statusLayout, 18);
    }

    private void RebuildStatusTexts()
    {
        if (_emergencyButton is null || _metricGrid is null) return;
        _emergencyButton.Content = "■  " + Loc.T("ac.emergency");
        AutomationProperties.SetName(_emergencyButton, Loc.T("ac.emergency"));
        var keys = new[] { "target", "actual", "average", "clicks", "failed", "runtime", "remaining", "skipped", "deviation", "cursor" };
        for (var i = 0; i < keys.Length && i < _metricGrid.Children.Count; i++)
            if (_metricGrid.Children[i] is StackPanel tile && tile.Children[0] is TextBlock label) label.Text = Loc.T("ac.metric." + keys[i]);
        UpdateStatus();
    }

    private void UpdateStatusLayout()
    {
        if (_statusControls is null || _metricGrid is null) return;
        var narrow = _layoutMode == "narrow";
        SetColumn(_statusControls, narrow ? 0 : 1);
        SetRow(_statusControls, 0);
        _statusControls.Margin = narrow ? new Thickness(0, 120, 0, 0) : new Thickness(0);
        _statusControls.HorizontalAlignment = HorizontalAlignment.Left;
        _metricGrid.Columns = narrow ? 2 : _layoutMode == "medium" ? 4 : 5;
        if (narrow && _statusLayout is not null)
        {
            // Below 750 px the buttons move under the status text.
            _statusLayout.RowDefinitions[0].Height = GridLength.Auto;
            ((FrameworkElement)_statusLayout.Children[0]).Margin = new Thickness(0, 0, 0, 70);
        }
        else if (_statusLayout is not null) ((FrameworkElement)_statusLayout.Children[0]).Margin = new Thickness(0);
    }

    /// <summary>Status colours, button texts and locked settings (on state changes, not every frame).</summary>
    private void UpdateStatus()
    {
        if (_statusText is null || _statusDot is null || _statusPill is null || _startButton is null || _pauseButton is null) return;
        var state = _engine.State;
        var running = state is AutoClickerState.Running or AutoClickerState.Starting or AutoClickerState.Paused or AutoClickerState.Stopping || _playing;
        var (key, color) = state switch
        {
            AutoClickerState.Running => (_playing ? "ac.state.playing" : "ac.state.running", "GreenBrush"),
            AutoClickerState.Starting => ("ac.state.starting", "OrangeBrush"),
            AutoClickerState.Paused => ("ac.state.paused", "OrangeBrush"),
            AutoClickerState.Stopping => ("ac.state.stopping", "TextMuted"),
            AutoClickerState.Faulted => ("ac.state.error", "OrangeBrush"),
            _ when _recorder.IsRecording => ("ac.state.recording", "OrangeBrush"),
            _ when _faultShown => ("ac.state.error", "OrangeBrush"),
            _ => ("ac.state.ready", "TextMuted")
        };
        var errorState = key == "ac.state.error";
        _statusText.Text = (errorState ? "⚠ " : running ? "● " : "") + Loc.T(key);
        var brush = errorState ? new SolidColorBrush(Color.FromRgb(0xE0, 0x3A, 0x3A)) : TryFindResource(color) as Brush ?? Brushes.Gray;
        _statusDot.Fill = brush;
        _statusText.Foreground = brush;
        _statusPill.SetResourceReference(Border.BackgroundProperty, state == AutoClickerState.Running ? "GreenSoft" : "SurfaceAlt");

        // A calm pulse while clicking – never a fast blink.
        if (state == AutoClickerState.Running && SystemParameters.ClientAreaAnimation)
            _statusDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        else
        {
            _statusDot.BeginAnimation(OpacityProperty, null);
            _statusDot.Opacity = 1;
        }

        _startButton.Content = running ? "■  " + Loc.T("ac.stop") : "▶  " + Loc.T("ac.start");
        AutomationProperties.SetName(_startButton, running ? Loc.T("ac.stop") : Loc.T("ac.start"));
        _pauseButton.Content = state == AutoClickerState.Paused ? "▶  " + Loc.T("ac.resume") : "❚❚  " + Loc.T("ac.pause");
        _pauseButton.IsEnabled = state is AutoClickerState.Running or AutoClickerState.Paused;
        AutomationProperties.SetName(_pauseButton, state == AutoClickerState.Paused ? Loc.T("ac.resume") : Loc.T("ac.pause"));

        if (_profileText is not null)
            _profileText.Text = Loc.F("ac.status.profile", _profile.Name) + (_settings.TestMode ? "  ·  " + Loc.T("ac.testModeOn") : "");
        if (_hotkeyHint is not null)
        {
            string Key(HotkeyAction a) => _profile.Hotkeys.GetValueOrDefault(a)?.ToString() ?? "–";
            _hotkeyHint.Text = Loc.F("ac.status.hotkeys", Key(HotkeyAction.StartStop), Key(HotkeyAction.EmergencyStop)) +
                               (_profile.EscapeStopsWhileRunning ? " · " + Loc.T("ac.status.escape") : "");
        }

        // Settings are locked while the engine runs; it works on its own copy anyway.
        foreach (var (name, (host, _)) in _cards)
            host.IsEnabled = !(running || _recorder.IsRecording) || name is "messages" or "test" && !running || name == "recording" && _recorder.IsRecording;
        if (running || _recorder.IsRecording) _cards["test"].Host.IsEnabled = false;
        UpdateLive();
    }

    /// <summary>Live values – called at most ten times per second, independent of the click rate.</summary>
    private void UpdateLive()
    {
        if (_metrics.Count == 0) return;
        var state = _engine.State;
        var now = Stopwatch.GetTimestamp();
        var active = state != AutoClickerState.Idle;
        var s = active || _lastResult is null ? _engine.Statistics.Snapshot(now) : _lastResult.Statistics;
        var config = _engine.Configuration;
        var playing = config?.Kind == RunKind.Playback && (active || _playing);
        var targetCps = playing ? (double?)null : active && config is not null ? config.TargetCps : _profile.Cps;

        void Set(string key, string text)
        {
            if (_metrics[key].Text != text) _metrics[key].Text = text;
        }
        Set("target", targetCps is { } t ? Cps(t) : "–");
        Set("actual", active || _lastResult is not null ? Cps(s.ActualCps) : "–");
        Set("average", s.Elapsed.TotalSeconds > 0 ? Cps(s.AverageCps) : "–");
        Set("clicks", s.Successful.ToString("N0", Loc.Culture));
        Set("failed", s.Failed.ToString("N0", Loc.Culture));
        Set("runtime", s.Elapsed.ToString(s.Elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss\.f", Loc.Culture));
        Set("skipped", s.Skipped.ToString("N0", Loc.Culture));
        Set("deviation", s.Successful > 0 ? s.LastDeviationMs.ToString("0.00", Loc.Culture) + " ms" : "–");
        if (_mouse.TryGetCursor(out var cx, out var cy) || new WindowsMouseInputService().TryGetCursor(out cx, out cy))
            Set("cursor", $"{cx} · {cy}");

        // Remaining clicks or time, plus the progress bar.
        string remaining = "–";
        double progress = 0;
        var cfg = active ? config : null;
        if (cfg?.ClickLimit is { } limit)
        {
            remaining = Math.Max(0, limit - s.Successful).ToString("N0", Loc.Culture);
            progress = limit > 0 ? (double)s.Successful / limit : 0;
        }
        else if (cfg?.Duration is { } duration)
        {
            var left = duration - s.Elapsed;
            remaining = (left < TimeSpan.Zero ? TimeSpan.Zero : left).ToString(@"m\:ss", Loc.Culture);
            progress = s.Elapsed.TotalSeconds / duration.TotalSeconds;
        }
        else if (cfg?.MaxRuntime is { } max)
        {
            var left = max - s.Elapsed;
            remaining = (left < TimeSpan.Zero ? TimeSpan.Zero : left).ToString(@"h\:mm\:ss", Loc.Culture);
            progress = s.Elapsed.TotalSeconds / max.TotalSeconds;
        }
        Set("remaining", remaining);
        if (_progressFill is not null && _progressTrack is not null)
        {
            _progressFill.Width = Math.Clamp(progress, 0, 1) * _progressTrack.ActualWidth;
            if (_progressText is not null)
                _progressText.Text = active
                    ? (cfg?.ClickLimit is { } l ? Loc.F("ac.progress.count", s.Successful.ToString("N0", Loc.Culture), l.ToString("N0", Loc.Culture))
                        : Loc.F("ac.progress.button", Loc.T("ac.button." + (cfg?.Button ?? _profile.Button))))
                    : _lastResultText ?? Loc.T("ac.progress.idle");
        }

        if (_countdownText is not null)
        {
            var countdown = _engine.CountdownRemaining;
            _countdownText.Visibility = countdown > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
            if (countdown > TimeSpan.Zero) _countdownText.Text = Loc.F("ac.countdown", Math.Ceiling(countdown.TotalSeconds));
        }

        // CPS history: a bounded list, drawn at the same 10 Hz.
        if (state == AutoClickerState.Running && _cpsChart is not null)
        {
            _cpsHistory.AddLast(s.ActualCps);
            while (_cpsHistory.Count > 100) _cpsHistory.RemoveFirst();
            if (_cpsHistory.Count > 1) _cpsChart.SetData(_cpsHistory.ToList());
        }

        if (_recordingStatus is not null && _recorder.IsRecording)
            _recordingStatus.Text = Loc.F("ac.rec.live", _recorder.Count, (DateTime.Now - _recordingStarted).ToString(@"m\:ss", Loc.Culture));

        // Screen reader summary every few seconds while running; header line twice per second.
        if (_statusPill is not null && state == AutoClickerState.Running && DateTime.Now - _lastSpoken > TimeSpan.FromSeconds(5))
        {
            _lastSpoken = DateTime.Now;
            AutomationProperties.SetName(_statusPill, Loc.F("ac.spoken", Cps(config?.TargetCps ?? 0), Cps(s.ActualCps), s.Successful.ToString("N0", Loc.Culture)));
        }
        else if (_statusPill is not null && state != AutoClickerState.Running) AutomationProperties.SetName(_statusPill, _statusText?.Text ?? "");

        if (DateTime.Now - _lastHeaderUpdate > TimeSpan.FromMilliseconds(500))
        {
            _lastHeaderUpdate = DateTime.Now;
            var header = StatusLine;
            if (header != _lastHeader)
            {
                _lastHeader = header;
                Changed?.Invoke();
            }
        }
    }

    // ───────────────────────── interval ─────────────────────────

    private bool _syncing;

    private UIElement BuildInterval()
    {
        var stack = new StackPanel();
        stack.Children.Add(Row(
            Chip(Loc.T("ac.interval.time"), _profile.IntervalInput == IntervalInput.Time, () => { _profile.IntervalInput = IntervalInput.Time; ProfileChanged("interval"); }),
            Chip(Loc.T("ac.interval.cps"), _profile.IntervalInput == IntervalInput.Cps, () => { _profile.IntervalInput = IntervalInput.Cps; ProfileChanged("interval"); })));

        if (_profile.IntervalInput == IntervalInput.Time)
        {
            var (h, m, sec, ms) = ClickMath.PartsFromInterval(_profile.IntervalMs);
            var parts = new double[] { h, m, sec, ms };
            string? Apply(int index, double value)
            {
                if (value < 0) return "ac.err.negative";
                parts[index] = value;
                var total = ClickMath.IntervalFromParts(parts[0], parts[1], parts[2], parts[3]);
                if (total < ClickMath.MinIntervalMs) return "ac.err.tooFastShort";
                if (total > ClickMath.MaxIntervalMs) return "ac.err.tooSlow";
                _profile.IntervalMs = total;
                AfterIntervalChange();
                return null;
            }
            stack.Children.Add(Row(
                Number(Loc.T("ac.unit.hours"), h, "0", v => Apply(0, Math.Floor(v)), 76),
                Number(Loc.T("ac.unit.minutes"), m, "0", v => Apply(1, Math.Floor(v)), 76),
                Number(Loc.T("ac.unit.seconds"), sec, "0", v => Apply(2, Math.Floor(v)), 76),
                Number(Loc.T("ac.unit.milliseconds"), ms, "0.###", v => Apply(3, v), 96)));
        }
        else
        {
            TextBox? cpsBox = null;
            var slider = new Slider { Minimum = 0, Maximum = 1000, Value = CpsToSlider(_profile.Cps), Margin = new Thickness(0, 6, 0, 6), SmallChange = 5, LargeChange = 50 };
            AutomationProperties.SetName(slider, Loc.T("ac.interval.slider"));
            var field = Number(Loc.T("ac.interval.cpsField"), Math.Round(_profile.Cps, 3), "0.###", v =>
            {
                if (v <= 0) return "ac.err.cpsRange";
                if (v > ClickMath.MaxCps) return "ac.err.cpsRange";
                _profile.Cps = v;
                if (!_syncing)
                {
                    _syncing = true;
                    slider.Value = CpsToSlider(v);
                    _syncing = false;
                }
                AfterIntervalChange();
                return null;
            }, 110, Loc.T("ac.interval.cpsHelp"));
            cpsBox = (TextBox)((Grid)field.Children[1]).Children[0];
            slider.ValueChanged += (_, _) =>
            {
                if (_syncing) return;
                _syncing = true;
                var cps = SliderToCps(slider.Value);
                cpsBox.Text = cps.ToString(cps < 10 ? "0.#" : "0", Loc.Culture);
                _syncing = false;
            };
            stack.Children.Add(field);
            stack.Children.Add(slider);
            var presets = new WrapPanel();
            foreach (var preset in new[] { 1, 5, 10, 20, 50, 100, 250, 500 })
                presets.Children.Add(Chip(preset + " CPS", Math.Abs(_profile.Cps - preset) < 0.001, () => { _profile.Cps = preset; ProfileChanged("interval", "jitter"); }, Loc.F("ac.interval.preset", preset)));
            stack.Children.Add(presets);
        }

        _intervalInfo = Info("", "TextSecondary", 12.5);
        stack.Children.Add(_intervalInfo);
        _highCpsNotice = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(1) };
        _highCpsNotice.SetResourceReference(Border.BorderBrushProperty, "OrangeBrush");
        var notice = new StackPanel();
        notice.Children.Add(Info("⚠ " + Loc.T("ac.highCps"), "TextPrimary", 12));
        notice.Children.Add(Row(
            Chip(Loc.T("ac.highCps.ok"), false, () => { _highCpsAcknowledged = true; UpdateIntervalInfo(); }),
            Chip(Loc.T("ac.highCps.never"), false, () => { _settings.HighCpsNoticeDismissed = true; _storage.SaveSettings(_settings); UpdateIntervalInfo(); })));
        _highCpsNotice.Child = notice;
        stack.Children.Add(_highCpsNotice);
        UpdateIntervalInfo();
        return stack;
    }

    private bool _highCpsAcknowledged;

    /// <summary>Logarithmic slider: fine steps at low rates, still reaches 500.</summary>
    private static double CpsToSlider(double cps) => Math.Log(Math.Clamp(cps, 1, 500)) / Math.Log(500) * 1000;
    private static double SliderToCps(double value)
    {
        var cps = Math.Pow(500, value / 1000);
        return cps < 10 ? Math.Round(cps, 1) : Math.Round(cps);
    }

    private void AfterIntervalChange()
    {
        _save.Stop();
        _save.Start();
        UpdateIntervalInfo();
        UpdateJitterPreview();
        UpdateLive();
    }

    private void UpdateIntervalInfo()
    {
        if (_intervalInfo is null) return;
        var ms = _profile.IntervalMs;
        var clicks = ClickMath.ClicksPerAction(_profile.ClickKind);
        var text = Loc.F("ac.interval.info", ms.ToString(ms < 10 ? "0.###" : "0.#", Loc.Culture), Cps(_profile.Cps));
        if (clicks > 1)
            text += " " + (_profile.CpsCountsActions ? Loc.F("ac.interval.actions", clicks) : Loc.F("ac.interval.singles", clicks, Cps(_profile.Cps / clicks)));
        _intervalInfo.Text = text;
        if (_highCpsNotice is not null)
            _highCpsNotice.Visibility = _profile.Cps > 100 && !_settings.HighCpsNoticeDismissed && !_highCpsAcknowledged ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────── jitter ─────────────────────────

    private UIElement BuildJitter()
    {
        var stack = new StackPanel();
        var help = Info(Loc.T("ac.jitter.help"));
        help.Visibility = Visibility.Collapsed;
        var helpButton = Ui.Button("ChipButton", "?", (_, _) => help.Visibility = help.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible, Loc.T("ac.jitter.helpTip"));
        helpButton.Width = 34;
        helpButton.Height = 34;
        helpButton.Margin = new Thickness(0, 0, 6, 6);
        AutomationProperties.SetName(helpButton, Loc.T("ac.jitter.helpTip"));
        stack.Children.Add(Row(Toggle(Loc.T("ac.jitter.enable"), _profile.RandomJitter, v => { _profile.RandomJitter = v; ProfileChanged("jitter"); }), helpButton));
        stack.Children.Add(help);
        if (_profile.RandomJitter)
        {
            var slider = new Slider { Minimum = 0, Maximum = Math.Max(5, Math.Min(500, _profile.IntervalMs)), Value = _profile.JitterMs, Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetName(slider, Loc.T("ac.jitter.slider"));
            var field = Number(Loc.T("ac.jitter.amount"), _profile.JitterMs, "0.##", v =>
            {
                if (v < 0) return "ac.err.negative";
                if (v > 3_600_000) return "ac.err.jitter";
                _profile.JitterMs = v;
                if (!_syncing)
                {
                    _syncing = true;
                    slider.Value = Math.Min(slider.Maximum, v);
                    _syncing = false;
                }
                _save.Stop();
                _save.Start();
                UpdateJitterPreview();
                return null;
            }, 110);
            var box = (TextBox)((Grid)field.Children[1]).Children[0];
            slider.ValueChanged += (_, _) =>
            {
                if (_syncing) return;
                _syncing = true;
                box.Text = Math.Round(slider.Value, slider.Value < 10 ? 1 : 0).ToString(Loc.Culture);
                _syncing = false;
            };
            stack.Children.Add(field);
            stack.Children.Add(slider);
        }
        _jitterPreview = Info("", "TextSecondary", 12.5);
        stack.Children.Add(_jitterPreview);
        UpdateJitterPreview();
        return stack;
    }

    private void UpdateJitterPreview()
    {
        if (_jitterPreview is null) return;
        if (!_profile.RandomJitter)
        {
            _jitterPreview.Text = Loc.F("ac.jitter.off", _profile.IntervalMs.ToString("0.###", Loc.Culture));
            return;
        }
        var (min, max) = ClickMath.JitterRange(_profile.IntervalMs, _profile.JitterMs);
        // The 2 ms floor shifts the average up when the range touches it.
        var average = Enumerable.Range(0, 200).Select(i => Math.Max(ClickMath.MinIntervalMs, _profile.IntervalMs - _profile.JitterMs + 2 * _profile.JitterMs * (i + 0.5) / 200)).Average();
        _jitterPreview.Text = Loc.F("ac.jitter.preview", min.ToString("0.##", Loc.Culture), max.ToString("0.##", Loc.Culture)) + " " +
                              Loc.F("ac.jitter.average", Cps(1000 / average));
    }

    // ───────────────────────── click options ─────────────────────────

    private UIElement BuildClick()
    {
        var stack = new StackPanel();
        stack.Children.Add(Label(Loc.T("ac.click.button")));
        var buttons = new WrapPanel();
        foreach (var button in Enum.GetValues<MouseButtonKind>())
            buttons.Children.Add(Chip(Loc.T("ac.button." + button), _profile.Button == button, () => { _profile.Button = button; ProfileChanged("click"); }));
        stack.Children.Add(buttons);

        stack.Children.Add(Label(Loc.T("ac.click.type")));
        var kinds = new WrapPanel();
        foreach (var kind in Enum.GetValues<ClickKind>())
            kinds.Children.Add(Chip(Loc.T("ac.kind." + kind), _profile.ClickKind == kind, () => { _profile.ClickKind = kind; ProfileChanged("click", "interval"); }));
        stack.Children.Add(kinds);
        stack.Children.Add(Info(Loc.T("ac.kind." + _profile.ClickKind + ".help")));

        if (_profile.ClickKind is ClickKind.Single or ClickKind.Double or ClickKind.Triple)
        {
            var clicks = ClickMath.ClicksPerAction(_profile.ClickKind);
            var (_, effective, effectiveGap) = ClickMath.Timing(_profile, NativeTiming.DoubleClickTime());
            var holdRow = new WrapPanel();
            holdRow.Children.Add(Number(Loc.T("ac.click.hold"), _profile.HoldMs, "0.##", v =>
            {
                if (v < 0) return "ac.err.negative";
                if (v > 60_000) return "ac.err.hold";
                _profile.HoldMs = v;
                _save.Stop();
                _save.Start();
                return null;
            }, 100, Loc.T("ac.click.holdHelp")));
            if (clicks > 1)
            {
                holdRow.Children.Add(Number(Loc.T("ac.click.gap"), _profile.MultiClickGapMs, "0.##", v =>
                {
                    if (v < 0) return "ac.err.negative";
                    if (v > 5000) return "ac.err.gap";
                    _profile.MultiClickGapMs = v;
                    _save.Stop();
                    _save.Start();
                    return null;
                }, 100));
            }
            stack.Children.Add(holdRow);
            if (effective + 0.001 < _profile.HoldMs) stack.Children.Add(Info(Loc.F("ac.click.holdLimited", effective.ToString("0.##", Loc.Culture)), "OrangeBrush"));
            if (clicks > 1 && effectiveGap + 0.001 < _profile.MultiClickGapMs) stack.Children.Add(Info(Loc.F("ac.click.gapLimited", effectiveGap.ToString("0.##", Loc.Culture)), "TextSecondary"));
            if (clicks > 1)
            {
                stack.Children.Add(Toggle(Loc.F("ac.click.systemDouble", NativeTiming.DoubleClickTime().ToString("0", Loc.Culture)), _profile.UseSystemDoubleClickTime,
                    v => { _profile.UseSystemDoubleClickTime = v; ProfileChanged("click"); }));
                stack.Children.Add(Label(Loc.T("ac.click.cpsMeans")));
                stack.Children.Add(Row(
                    Chip(Loc.T("ac.click.cpsSingles"), !_profile.CpsCountsActions, () => { _profile.CpsCountsActions = false; ProfileChanged("click", "interval"); }),
                    Chip(Loc.T("ac.click.cpsActions"), _profile.CpsCountsActions, () => { _profile.CpsCountsActions = true; ProfileChanged("click", "interval"); })));
            }
        }
        return stack;
    }

    // ───────────────────────── repeat ─────────────────────────

    private UIElement BuildRepeat()
    {
        var stack = new StackPanel();
        var modes = new WrapPanel();
        foreach (var mode in Enum.GetValues<RepeatMode>())
            modes.Children.Add(Chip(Loc.T("ac.repeat." + mode), _profile.Repeat == mode, () => { _profile.Repeat = mode; ProfileChanged("repeat"); }));
        stack.Children.Add(modes);

        switch (_profile.Repeat)
        {
            case RepeatMode.Count:
                stack.Children.Add(Number(Loc.T("ac.repeat.countField"), _profile.ClickCount, "0", v =>
                {
                    if (v < 1 || v > 100_000_000 || Math.Floor(v) != v) return "ac.err.count";
                    _profile.ClickCount = (long)v;
                    _save.Stop();
                    _save.Start();
                    return null;
                }, 150));
                break;
            case RepeatMode.Duration:
                var total = TimeSpan.FromSeconds(_profile.DurationSeconds);
                var parts = new double[] { Math.Floor(total.TotalHours), total.Minutes, total.Seconds + total.Milliseconds / 1000.0 };
                string? Apply(int i, double v)
                {
                    if (v < 0) return "ac.err.negative";
                    parts[i] = v;
                    var seconds = parts[0] * 3600 + parts[1] * 60 + parts[2];
                    if (seconds <= 0 || seconds > 7 * 24 * 3600) return "ac.err.duration";
                    _profile.DurationSeconds = seconds;
                    _save.Stop();
                    _save.Start();
                    return null;
                }
                stack.Children.Add(Row(
                    Number(Loc.T("ac.unit.hours"), parts[0], "0", v => Apply(0, Math.Floor(v)), 76),
                    Number(Loc.T("ac.unit.minutes"), parts[1], "0", v => Apply(1, Math.Floor(v)), 76),
                    Number(Loc.T("ac.unit.seconds"), parts[2], "0.#", v => Apply(2, v), 86)));
                break;
            case RepeatMode.WhileHeld:
                var key = _profile.Hotkeys.GetValueOrDefault(HotkeyAction.StartStop) ?? _profile.Hotkeys.GetValueOrDefault(HotkeyAction.Start);
                stack.Children.Add(Info(key is null ? Loc.T("ac.err.holdKey") : Loc.F("ac.repeat.holdInfo", key.ToString()), key is null ? "OrangeBrush" : "TextSecondary", 12.5));
                break;
            default:
                stack.Children.Add(Info(Loc.T("ac.repeat.untilInfo"), "TextSecondary", 12.5));
                break;
        }

        stack.Children.Add(Heading(Loc.T("ac.repeat.delay")));
        var delayRow = new WrapPanel();
        delayRow.Children.Add(Toggle(Loc.T("ac.repeat.delayEnable"), _profile.StartDelay, v => { _profile.StartDelay = v; ProfileChanged("repeat"); }));
        stack.Children.Add(delayRow);
        if (_profile.StartDelay)
            stack.Children.Add(Number(Loc.T("ac.repeat.delaySeconds"), _profile.StartDelaySeconds, "0.#", v =>
            {
                if (v < 0 || v > 600) return "ac.err.delay";
                _profile.StartDelaySeconds = v;
                _save.Stop();
                _save.Start();
                return null;
            }, 90, Loc.T("ac.repeat.delayHelp")));
        return stack;
    }

    // ───────────────────────── position ─────────────────────────

    private UIElement BuildPosition()
    {
        var stack = new StackPanel();
        var modes = new WrapPanel();
        foreach (var mode in Enum.GetValues<PositionMode>())
            modes.Children.Add(Chip(Loc.T("ac.pos." + mode), _profile.Position == mode, () => { _profile.Position = mode; ProfileChanged("position"); }));
        stack.Children.Add(modes);
        var owner = Window.GetWindow(this);

        switch (_profile.Position)
        {
            case PositionMode.Current:
                stack.Children.Add(Info(Loc.T("ac.pos.currentInfo"), "TextSecondary", 12.5));
                break;
            case PositionMode.Fixed:
                stack.Children.Add(Row(
                    Number("X", _profile.FixedX, "0", v => SetCoordinate(v, x => _profile.FixedX = x), 96, Loc.T("ac.pos.physical")),
                    Number("Y", _profile.FixedY, "0", v => SetCoordinate(v, y => _profile.FixedY = y), 96, Loc.T("ac.pos.physical"))));
                stack.Children.Add(Row(
                    Ui.Button("ActionButton", "⌖  " + Loc.T("ac.pos.pick"), async (_, _) => await PickPointAsync(p => { _profile.FixedX = p.X; _profile.FixedY = p.Y; })),
                    Ui.Button("SecondaryButton", "◷  " + Loc.T("ac.pos.countdown"), (_, _) => CaptureAfterCountdown(p => { _profile.FixedX = p.X; _profile.FixedY = p.Y; }))));
                break;
            case PositionMode.Sequence:
                var list = new StackPanel();
                for (var i = 0; i < _profile.Points.Count; i++) list.Children.Add(PointRow(i));
                if (_profile.Points.Count == 0) list.Children.Add(Info(Loc.T("ac.pos.noPoints"), "OrangeBrush"));
                stack.Children.Add(list);
                stack.Children.Add(Row(
                    Ui.Button("ActionButton", "+  " + Loc.T("ac.pos.addPick"), async (_, _) => await PickPointAsync(p => _profile.Points.Add(new ClickPoint(p.X, p.Y)))),
                    Ui.Button("SecondaryButton", "◷  " + Loc.T("ac.pos.addCountdown"), (_, _) => CaptureAfterCountdown(p => _profile.Points.Add(new ClickPoint(p.X, p.Y))))));
                stack.Children.Add(Row(
                    Chip(Loc.T("ac.pos.loop"), _profile.LoopPoints, () => { _profile.LoopPoints = true; ProfileChanged("position"); }),
                    Chip(Loc.T("ac.pos.once"), !_profile.LoopPoints, () => { _profile.LoopPoints = false; ProfileChanged("position"); })));
                stack.Children.Add(Info(Loc.T("ac.pos.delayInfo")));
                break;
            case PositionMode.RandomArea:
                var area = _profile.Area ?? new ScreenArea(0, 0, 0, 0);
                ScreenArea Change(int? x = null, int? y = null, int? w = null, int? h = null) =>
                    new(x ?? (_profile.Area?.X ?? 0), y ?? (_profile.Area?.Y ?? 0), w ?? (_profile.Area?.Width ?? 0), h ?? (_profile.Area?.Height ?? 0));
                stack.Children.Add(Row(
                    Number("X", area.X, "0", v => SetCoordinate(v, x => _profile.Area = Change(x: x)), 86),
                    Number("Y", area.Y, "0", v => SetCoordinate(v, y => _profile.Area = Change(y: y)), 86),
                    Number(Loc.T("ac.pos.width"), area.Width, "0", v => v < 1 ? "ac.err.area" : SetCoordinate(v, w => _profile.Area = Change(w: w)), 86),
                    Number(Loc.T("ac.pos.height"), area.Height, "0", v => v < 1 ? "ac.err.area" : SetCoordinate(v, h => _profile.Area = Change(h: h)), 86)));
                stack.Children.Add(Row(Ui.Button("ActionButton", "⬚  " + Loc.T("ac.pos.pickArea"), async (_, _) =>
                {
                    if (owner is null) return;
                    var picked = await CursorPickerWindow.PickAreaAsync(owner, Loc.T("ac.pos.pickAreaHint"));
                    if (picked is null) { AddMessage("·", Loc.T("ac.msg.pickCancelled")); return; }
                    _profile.Area = picked;
                    ProfileChanged("position");
                })));
                break;
        }

        foreach (var button in stack.Children.OfType<WrapPanel>().SelectMany(w => w.Children.OfType<Button>()).Where(b => b.Style is null || b.Height is double.NaN))
        {
            if (!double.IsNaN(button.Height) && button.Height > 0) continue;
            button.Height = 38;
            button.Margin = new Thickness(0, 4, 8, 6);
            button.Padding = new Thickness(14, 0, 14, 0);
        }

        if (_profile.Position != PositionMode.Current)
        {
            stack.Children.Add(Toggle(Loc.T("ac.pos.moveBack"), _profile.MoveBack, v => { _profile.MoveBack = v; ProfileChanged("position"); }, Loc.T("ac.pos.moveBackHelp")));
            _preview = new MonitorPreview { Height = 120, Margin = new Thickness(0, 6, 0, 4) };
            _preview.Update(_profile);
            stack.Children.Add(_preview);
            stack.Children.Add(Info(MonitorText()));
        }
        return stack;
    }

    private string? SetCoordinate(double value, Action<int> set)
    {
        if (Math.Floor(value) != value || value is < -100_000 or > 100_000) return "ac.err.coordinate";
        set((int)value);
        _save.Stop();
        _save.Start();
        _preview?.Update(_profile);
        return null;
    }

    private string MonitorText()
    {
        (int X, int Y)? point = _profile.Position switch
        {
            PositionMode.Fixed => (_profile.FixedX, _profile.FixedY),
            PositionMode.Sequence when _profile.Points.Count > 0 => (_profile.Points[0].X, _profile.Points[0].Y),
            PositionMode.RandomArea when _profile.Area is { } a => (a.X, a.Y),
            _ => null
        };
        if (point is not { } p) return "";
        var monitor = ScreenInfo.MonitorAt(p.X, p.Y);
        return monitor is null
            ? "⚠ " + Loc.T("ac.pos.offscreen")
            : Loc.F("ac.pos.monitor", monitor.Index, monitor.Bounds.Width, monitor.Bounds.Height, Math.Round(monitor.Scale * 100)) + (monitor.Primary ? " · " + Loc.T("ac.pos.primary") : "");
    }

    private UIElement PointRow(int index)
    {
        var point = _profile.Points[index];
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        var number = Ui.Text($"{index + 1}.", 13, bold: true);
        number.Width = 26;
        number.Margin = new Thickness(0, 26, 0, 0);
        row.Children.Add(number);
        row.Children.Add(Number("X", point.X, "0", v => SetCoordinate(v, x => _profile.Points[index] = _profile.Points[index] with { X = x }), 80));
        row.Children.Add(Number("Y", point.Y, "0", v => SetCoordinate(v, y => _profile.Points[index] = _profile.Points[index] with { Y = y }), 80));
        var delay = Number(Loc.T("ac.pos.delay"), point.DelayMs ?? 0, "0.##", v =>
        {
            if (v < 0) return "ac.err.negative";
            _profile.Points[index] = _profile.Points[index] with { DelayMs = v <= 0 ? null : Math.Max(ClickMath.MinIntervalMs, v) };
            _save.Stop();
            _save.Start();
            return null;
        }, 96, Loc.T("ac.pos.delayHelp"));
        row.Children.Add(delay);
        Button Small(string text, string tip, Action click)
        {
            var button = Ui.Button("ChipButton", text, (_, _) => click(), tip);
            button.Width = 34;
            button.Height = 34;
            button.Margin = new Thickness(0, 22, 4, 0);
            AutomationProperties.SetName(button, $"{tip} {index + 1}");
            return button;
        }
        row.Children.Add(Small("▲", Loc.T("ac.moveUp"), () => { if (index > 0) { (_profile.Points[index - 1], _profile.Points[index]) = (_profile.Points[index], _profile.Points[index - 1]); ProfileChanged("position"); } }));
        row.Children.Add(Small("▼", Loc.T("ac.moveDown"), () => { if (index < _profile.Points.Count - 1) { (_profile.Points[index + 1], _profile.Points[index]) = (_profile.Points[index], _profile.Points[index + 1]); ProfileChanged("position"); } }));
        row.Children.Add(Small("✕", Loc.T("ac.remove"), () => { _profile.Points.RemoveAt(index); ProfileChanged("position"); }));
        return row;
    }

    private async Task PickPointAsync(Action<(int X, int Y)> apply)
    {
        if (Window.GetWindow(this) is not { } owner) return;
        var picked = await CursorPickerWindow.PickPointAsync(owner, Loc.T("ac.pos.pickHint"));
        if (picked is not { } point)
        {
            AddMessage("·", Loc.T("ac.msg.pickCancelled"));
            return;
        }
        apply(point);
        AddMessage("⌖", Loc.F("ac.msg.captured", point.X, point.Y));
        ProfileChanged("position");
    }

    private DispatcherTimer? _captureTimer;

    /// <summary>Alternative to the picker: take wherever the cursor is after three seconds.</summary>
    private void CaptureAfterCountdown(Action<(int X, int Y)> apply)
    {
        _captureTimer?.Stop();
        var left = 3;
        AddMessage("◷", Loc.F("ac.msg.captureIn", left));
        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _captureTimer.Tick += (_, _) =>
        {
            left--;
            if (left > 0)
            {
                AddMessage("◷", Loc.F("ac.msg.captureIn", left));
                return;
            }
            _captureTimer!.Stop();
            if (!new WindowsMouseInputService().TryGetCursor(out var x, out var y)) return;
            apply((x, y));
            AddMessage("⌖", Loc.F("ac.msg.captured", x, y));
            ProfileChanged("position");
        };
        _captureTimer.Start();
    }

    // ───────────────────────── hotkeys ─────────────────────────

    private UIElement BuildHotkeys()
    {
        var stack = new StackPanel();
        stack.Children.Add(Toggle(Loc.T("ac.hk.enable"), _settings.GlobalHotkeys, v =>
        {
            _settings.GlobalHotkeys = v;
            _storage.SaveSettings(_settings);
            EnsureHotkeys();
            RefreshCard("hotkeys");
        }, Loc.T("ac.hk.enableHelp")));

        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        var row = 0;
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = Ui.Text(Loc.T("ac.hk." + action), 13);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.TextWrapping = TextWrapping.Wrap;
            SetRow(name, row);
            grid.Children.Add(name);

            var key = _profile.Hotkeys.GetValueOrDefault(action);
            var capturing = _capturing == action;
            var button = Ui.Button("ChipButton", capturing ? Loc.T("ac.hk.press") : key?.ToString() ?? Loc.T("ac.hk.none"), (_, _) =>
            {
                _capturing = action;
                RefreshCard("hotkeys");
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Focus());
            }, Loc.T("ac.hk.assignHelp"));
            button.Height = 34;
            button.MinWidth = 130;
            button.Margin = new Thickness(8, 0, 6, 6);
            Ui.Select(button, capturing);
            AutomationProperties.SetName(button, $"{Loc.T("ac.hk." + action)}: {key?.ToString() ?? Loc.T("ac.hk.none")}");
            SetRow(button, row);
            SetColumn(button, 1);
            grid.Children.Add(button);

            var status = _hotkeyStatus.GetValueOrDefault(action);
            var (glyph, brush, tip) = status switch
            {
                HotkeyStatus.Registered => ("✓", "GreenBrush", "ac.hk.ok"),
                HotkeyStatus.InUse => ("⚠", "OrangeBrush", "ac.hk.inUse"),
                HotkeyStatus.Duplicate => ("⚠", "OrangeBrush", "ac.hk.duplicate"),
                _ => key is not null && !_settings.GlobalHotkeys ? ("○", "TextMuted", "ac.hk.off") : ("", "TextMuted", "ac.hk.none")
            };
            var mark = Ui.Text(glyph, 14, brush, bold: true);
            mark.ToolTip = Loc.T(tip);
            mark.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(mark, Loc.T(tip));
            SetRow(mark, row);
            SetColumn(mark, 2);
            grid.Children.Add(mark);
            row++;
        }
        stack.Children.Add(grid);
        stack.Children.Add(Info(_capturing is null ? Loc.T("ac.hk.info") : Loc.T("ac.hk.captureInfo")));
        stack.Children.Add(Toggle(Loc.T("ac.safety.escape"), _profile.EscapeStopsWhileRunning, v => { _profile.EscapeStopsWhileRunning = v; ProfileChanged("hotkeys"); }));
        return stack;
    }

    // ───────────────────────── profiles ─────────────────────────

    private bool _renaming;

    private UIElement BuildProfiles()
    {
        var stack = new StackPanel();
        var chips = new WrapPanel();
        foreach (var profile in _profiles)
        {
            var label = (profile.Name == _settings.DefaultProfile ? "★ " : "") + profile.Name;
            chips.Children.Add(Chip(label, profile == _profile, () => SwitchProfile(profile), Loc.F("ac.profile.load", profile.Name)));
        }
        stack.Children.Add(chips);

        if (_renaming)
        {
            var (host, box) = Ui.Input(Loc.T("ac.profile.newName"));
            box.Text = _profile.Name;
            box.Height = 38;
            AutomationProperties.SetName(box, Loc.T("ac.profile.newName"));
            void Commit()
            {
                var name = box.Text.Trim();
                _renaming = false;
                if (name.Length == 0 || name == _profile.Name) { RefreshCard("profiles"); return; }
                name = AutoClickerStorage.UniqueName(name, _profiles.Where(p => p != _profile).Select(p => p.Name));
                var wasDefault = _settings.DefaultProfile == _profile.Name;
                _storage.RenameProfile(_profile, name);
                if (wasDefault) _settings.DefaultProfile = name;
                SaveNow();
                RefreshCard("profiles");
                UpdateStatus();
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) Commit();
                if (e.Key == System.Windows.Input.Key.Escape) { _renaming = false; RefreshCard("profiles"); e.Handled = true; }
            };
            var ok = Ui.Button("ActionButton", Loc.T("save"), (_, _) => Commit());
            ok.Margin = new Thickness(8, 0, 0, 0);
            ok.Padding = new Thickness(14, 0, 14, 0);
            var line = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
            DockPanel.SetDock(ok, Dock.Right);
            line.Children.Add(ok);
            line.Children.Add(host);
            stack.Children.Add(line);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { box.Focus(); box.SelectAll(); });
        }

        Button Action(string text, Action click, string? tip = null)
        {
            var button = Ui.Button("SecondaryButton", text, (_, _) => click(), tip);
            button.Height = 34;
            button.Margin = new Thickness(0, 0, 6, 6);
            button.Padding = new Thickness(12, 0, 12, 0);
            return button;
        }
        stack.Children.Add(Row(
            Action("+ " + Loc.T("ac.profile.new"), () => AddProfile(new AutoClickerProfile { Name = AutoClickerStorage.UniqueName(Loc.T("ac.profile.defaultName"), _profiles.Select(p => p.Name)) })),
            Action("⧉ " + Loc.T("ac.profile.duplicate"), () => AddProfile(_profile.Clone(AutoClickerStorage.UniqueName(_profile.Name, _profiles.Select(p => p.Name))))),
            Action("✎ " + Loc.T("ac.profile.rename"), () => { _renaming = true; RefreshCard("profiles"); }),
            Action("✕ " + Loc.T("ac.profile.delete"), DeleteProfile),
            Action((_settings.DefaultProfile == _profile.Name ? "★ " : "☆ ") + Loc.T("ac.profile.default"), () =>
            {
                _settings.DefaultProfile = _settings.DefaultProfile == _profile.Name ? null : _profile.Name;
                _storage.SaveSettings(_settings);
                RefreshCard("profiles");
            }, Loc.T("ac.profile.defaultHelp")),
            Action("⇪ " + Loc.T("ac.profile.export"), ExportProfile),
            Action("⇩ " + Loc.T("ac.profile.import"), ImportProfile)));
        stack.Children.Add(Info(Loc.F("ac.profile.info", _storage.Folder)));
        return stack;
    }

    private void SwitchProfile(AutoClickerProfile profile)
    {
        if (profile == _profile) return;
        _save.Stop();
        SaveNow();
        _profile = profile;
        _renaming = false;
        _settings.LastProfile = profile.Name;
        _storage.SaveSettings(_settings);
        EnsureHotkeys();
        BuildCards();
        AddMessage("▤", Loc.F("ac.msg.profileLoaded", profile.Name));
    }

    private void AddProfile(AutoClickerProfile profile)
    {
        _profiles.Add(profile);
        _storage.SaveProfile(profile);
        SwitchProfile(profile);
    }

    private void DeleteProfile()
    {
        if (_profiles.Count <= 1)
        {
            AddMessage("⚠", Loc.T("ac.msg.lastProfile"));
            return;
        }
        if (Window.GetWindow(this) is { } owner && MessageBox.Show(owner, Loc.F("ac.confirm.delete", _profile.Name), "Freedom", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var removed = _profile;
        _storage.DeleteProfile(removed.Name);
        _profiles.Remove(removed);
        if (_settings.DefaultProfile == removed.Name) _settings.DefaultProfile = null;
        _profile = _profiles.FirstOrDefault(p => p.Name == _settings.DefaultProfile) ?? _profiles[0];
        _settings.LastProfile = _profile.Name;
        _storage.SaveSettings(_settings);
        EnsureHotkeys();
        BuildCards();
        AddMessage("✕", Loc.F("ac.msg.profileDeleted", removed.Name));
    }

    private void ExportProfile()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = AutoClickerStorage.FileNameFor(_profile.Name), Filter = "Freedom-Autoclicker (*.json)|*.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            AutoClickerStorage.Export(_profile, dialog.FileName);
            AddMessage("⇪", Loc.F("ac.msg.exported", dialog.FileName));
        }
        catch (Exception error)
        {
            AddMessage("⚠", error.Message);
        }
    }

    private void ImportProfile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Freedom-Autoclicker (*.json)|*.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (AutoClickerStorage.Import(dialog.FileName) is not { } profile)
        {
            AddMessage("⚠", Loc.T("ac.msg.importFailed"));
            return;
        }
        profile.Name = AutoClickerStorage.UniqueName(profile.Name, _profiles.Select(p => p.Name));
        AddProfile(profile);
        AddMessage("⇩", Loc.F("ac.msg.imported", profile.Name));
    }

    // ───────────────────────── recording ─────────────────────────

    private UIElement BuildRecording()
    {
        var stack = new StackPanel();
        var recording = _recorder.IsRecording;
        var playing = _playing && _engine.State != AutoClickerState.Idle;
        _recordingStatus = Info(recording ? Loc.F("ac.rec.live", _recorder.Count, "0:00") : Loc.F("ac.rec.ready", _sequence.Events.Count), recording ? "OrangeBrush" : "TextSecondary", 13);
        AutomationProperties.SetLiveSetting(_recordingStatus, AutomationLiveSetting.Polite);
        stack.Children.Add(_recordingStatus);

        var record = Ui.Button(recording ? "ActionButton" : "SecondaryButton", recording ? "■  " + Loc.T("ac.rec.stop") : "●  " + Loc.T("ac.rec.start"), (_, _) =>
        {
            if (_recorder.IsRecording) StopRecording();
            else StartRecording();
        });
        var play = Ui.Button("SecondaryButton", playing ? "■  " + Loc.T("ac.rec.stopPlayback") : "▶  " + Loc.T("ac.rec.play"), (_, _) =>
        {
            if (playing) StopRun();
            else StartPlayback();
        });
        foreach (var b in new[] { record, play })
        {
            b.Height = 38;
            b.Margin = new Thickness(0, 0, 8, 8);
            b.Padding = new Thickness(14, 0, 14, 0);
        }
        play.IsEnabled = !recording && _sequence.Events.Count > 0;
        stack.Children.Add(Row(record, play));
        stack.Children.Add(Info(Loc.T("ac.rec.privacy")));

        if (_sequence.Events.Count > 0 && !recording)
        {
            var speedRow = new WrapPanel();
            speedRow.Children.Add(Number(Loc.T("ac.rec.speed"), _sequence.Speed, "0.##", v =>
            {
                if (v < 0.1 || v > 20) return "ac.err.speed";
                _sequence.Speed = v;
                _storage.SaveSequence(_sequence);
                return null;
            }, 90, Loc.T("ac.rec.speedHelp")));
            speedRow.Children.Add(Number(Loc.T("ac.rec.repeat"), _sequence.Repeat, "0", v =>
            {
                if (v < 1 || v > 100_000 || Math.Floor(v) != v) return "ac.err.repeat";
                _sequence.Repeat = (int)v;
                _storage.SaveSequence(_sequence);
                return null;
            }, 90));
            stack.Children.Add(speedRow);
            stack.Children.Add(SequenceEditor());
        }
        return stack;
    }

    private UIElement SequenceEditor()
    {
        var list = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        string[] heads = { "", "#", Loc.T("ac.rec.time"), Loc.T("ac.rec.delay"), "X", "Y", Loc.T("ac.rec.button"), Loc.T("ac.rec.kind"), "" };
        double[] widths = { 30, 30, 62, 86, 74, 74, 76, 86, 112 };
        Grid Line()
        {
            var g = new Grid();
            foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            return g;
        }
        header = Line();
        for (var i = 0; i < heads.Length; i++)
        {
            var t = Ui.Text(heads[i], 10.5, "TextMuted", bold: true);
            SetColumn(t, i);
            header.Children.Add(t);
        }
        list.Children.Add(header);

        var time = 0.0;
        const int maxRows = 150;
        for (var index = 0; index < _sequence.Events.Count && index < maxRows; index++)
        {
            var e = _sequence.Events[index];
            time += e.DelayMs;
            var line = Line();
            line.Margin = new Thickness(0, 0, 0, 3);
            var i = index;
            UIElement[] cells =
            {
                Toggle("", e.Enabled, v => { e.Enabled = v; SequenceChanged(); }, Loc.T("ac.rec.enabled")),
                Ui.Text((index + 1).ToString(), 12, "TextSecondary"),
                Ui.Text((time / 1000).ToString("0.00", Loc.Culture) + " s", 12, "TextSecondary"),
                SmallNumber(e.DelayMs, "0", v => { if (v < 0) return false; e.DelayMs = v; return true; }, Loc.F("ac.rec.delayOf", index + 1)),
                SmallNumber(e.X, "0", v => { if (Math.Floor(v) != v) return false; e.X = (int)v; return true; }, $"X {index + 1}"),
                SmallNumber(e.Y, "0", v => { if (Math.Floor(v) != v) return false; e.Y = (int)v; return true; }, $"Y {index + 1}"),
                Ui.Button("ChipButton", Loc.T("ac.buttonShort." + e.Button), (_, _) =>
                {
                    e.Button = (MouseButtonKind)(((int)e.Button + 1) % 5);
                    SequenceChanged(true);
                }, Loc.T("ac.rec.cycleButton")),
                Ui.Text(Loc.T("ac.rec.kind." + e.Kind) + (e.Kind == SequenceEventKind.Wheel ? $" {e.WheelDelta}" : ""), 12),
                Row(MiniButton("▲", Loc.T("ac.moveUp"), () => Move(i, -1)), MiniButton("▼", Loc.T("ac.moveDown"), () => Move(i, 1)), MiniButton("✕", Loc.T("ac.remove"), () =>
                {
                    _sequence.Events.RemoveAt(i);
                    SequenceChanged(true);
                }))
            };
            for (var c = 0; c < cells.Length; c++)
            {
                if (cells[c] is FrameworkElement fe)
                {
                    fe.VerticalAlignment = VerticalAlignment.Center;
                    if (fe is Button b && c != 8) { b.Height = 28; b.Margin = new Thickness(0, 0, 4, 0); b.Padding = new Thickness(6, 0, 6, 0); }
                }
                SetColumn(cells[c], c);
                line.Children.Add(cells[c]);
            }
            list.Children.Add(line);
        }
        if (_sequence.Events.Count > maxRows) list.Children.Add(Info(Loc.F("ac.rec.more", _sequence.Events.Count - maxRows)));
        return new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };

        void Move(int index, int direction)
        {
            var target = index + direction;
            if (target < 0 || target >= _sequence.Events.Count) return;
            (_sequence.Events[index], _sequence.Events[target]) = (_sequence.Events[target], _sequence.Events[index]);
            SequenceChanged(true);
        }
    }

    private Button MiniButton(string text, string tip, Action click)
    {
        var button = Ui.Button("ChipButton", text, (_, _) => click(), tip);
        button.Width = 30;
        button.Height = 28;
        button.Margin = new Thickness(0, 0, 3, 0);
        AutomationProperties.SetName(button, tip);
        return button;
    }

    private UIElement SmallNumber(double value, string format, Func<double, bool> apply, string name)
    {
        var (host, box) = Ui.Input("");
        box.Height = 28;
        box.Padding = new Thickness(6, 0, 6, 0);
        box.Text = value.ToString(format, Loc.Culture);
        host.Margin = new Thickness(0, 0, 6, 0);
        AutomationProperties.SetName(box, name);
        box.TextChanged += (_, _) =>
        {
            var ok = TryNumber(box.Text, out var v) && apply(v);
            box.SetResourceReference(Control.BorderBrushProperty, ok ? "CardBorder" : "OrangeBrush");
            if (ok) _storage.SaveSequence(_sequence);
        };
        return host;
    }

    private void SequenceChanged(bool rebuild = false)
    {
        _storage.SaveSequence(_sequence);
        if (rebuild) RefreshCard("recording");
    }

    // ───────────────────────── safety, test, messages ─────────────────────────

    private UIElement BuildSafety()
    {
        var stack = new StackPanel();
        stack.Children.Add(Toggle(Loc.T("ac.safety.lock"), _profile.StopOnLock, v => { _profile.StopOnLock = v; ProfileChanged("safety"); }));
        stack.Children.Add(Toggle(Loc.T("ac.safety.display"), _profile.StopOnDisplayOff, v => { _profile.StopOnDisplayOff = v; ProfileChanged("safety"); }));
        stack.Children.Add(Toggle(Loc.T("ac.safety.maxRuntime"), _profile.MaxRuntimeEnabled, v => { _profile.MaxRuntimeEnabled = v; ProfileChanged("safety"); }));
        if (_profile.MaxRuntimeEnabled)
            stack.Children.Add(Number(Loc.T("ac.safety.minutes"), _profile.MaxRuntimeMinutes, "0.#", v =>
            {
                if (v <= 0 || v > 10_080) return "ac.err.maxRuntime";
                _profile.MaxRuntimeMinutes = v;
                _save.Stop();
                _save.Start();
                return null;
            }, 100));
        stack.Children.Add(Toggle(Loc.T("ac.safety.maxClicks"), _profile.MaxClicksEnabled, v => { _profile.MaxClicksEnabled = v; ProfileChanged("safety"); }));
        if (_profile.MaxClicksEnabled)
            stack.Children.Add(Number(Loc.T("ac.safety.clicks"), _profile.MaxClicks, "0", v =>
            {
                if (v < 1 || Math.Floor(v) != v) return "ac.err.maxClicks";
                _profile.MaxClicks = (long)v;
                _save.Stop();
                _save.Start();
                return null;
            }, 140));
        stack.Children.Add(Toggle(Loc.T("ac.safety.confirm"), _profile.ConfirmLongRuns, v => { _profile.ConfirmLongRuns = v; ProfileChanged("safety"); }));
        if (_settings.HighCpsNoticeDismissed)
            stack.Children.Add(Toggle(Loc.T("ac.safety.highCps"), false, _ => { _settings.HighCpsNoticeDismissed = false; _highCpsAcknowledged = false; _storage.SaveSettings(_settings); RefreshCard("safety"); UpdateIntervalInfo(); }));
        stack.Children.Add(Info(Loc.T("ac.safety.info")));
        return stack;
    }

    private UIElement BuildTest()
    {
        var stack = new StackPanel();
        stack.Children.Add(Toggle(Loc.T("ac.test.enable"), _settings.TestMode, v =>
        {
            _settings.TestMode = v;
            _storage.SaveSettings(_settings);
            RefreshCard("test");
            UpdateStatus();
        }, Loc.T("ac.test.help")));
        stack.Children.Add(Info(Loc.T("ac.test.info")));
        var quick = new WrapPanel();
        foreach (var cps in new[] { 100, 250, 500 })
        {
            var button = Ui.Button("SecondaryButton", Loc.F("ac.test.run", cps), (_, _) =>
            {
                var profile = _profile.Clone();
                profile.Cps = cps;
                profile.Repeat = RepeatMode.Duration;
                profile.DurationSeconds = 5;
                profile.StartDelay = false;
                profile.Position = PositionMode.Current;
                profile.ClickKind = ClickKind.Single;
                profile.RandomJitter = false;
                StartRun(testMode: true, overrideProfile: profile);
            });
            button.Height = 34;
            button.Margin = new Thickness(0, 0, 6, 6);
            button.Padding = new Thickness(12, 0, 12, 0);
            quick.Children.Add(button);
        }
        stack.Children.Add(quick);

        if (_testResult is { } r)
        {
            var target = _lastResult?.Statistics == r && _engine.Configuration is { } c ? c.TargetCps : 0;
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
            void Add(string key, string value)
            {
                grid.Children.Add(Ui.Text(Loc.T(key), 12, "TextSecondary"));
                grid.Children.Add(Ui.Text(value, 12.5, bold: true));
            }
            Add("ac.test.target", Cps(target));
            Add("ac.test.average", Cps(r.AverageCps));
            Add("ac.test.min", Cps(r.MinWindowCps));
            Add("ac.test.max", Cps(r.MaxWindowCps));
            Add("ac.test.interval", r.AverageIntervalMs.ToString("0.000", Loc.Culture) + " ms");
            Add("ac.test.deviation", r.MaxDeviationMs.ToString("0.000", Loc.Culture) + " ms");
            Add("ac.test.skipped", r.Skipped.ToString("N0", Loc.Culture));
            Add("ac.test.cpu", r.CpuPercent is { } cpu ? cpu.ToString("0.0", Loc.Culture) + " %" : Loc.T("ac.test.cpuUnknown"));
            stack.Children.Add(Heading(Loc.T("ac.test.results")));
            stack.Children.Add(grid);
            stack.Children.Add(Info(Loc.T("ac.test.note")));
        }
        return stack;
    }

    private UIElement BuildMessages()
    {
        var stack = new StackPanel();
        if (_messages.Count == 0) stack.Children.Add(Info(Loc.T("ac.msg.none")));
        foreach (var (time, glyph, text) in _messages)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var stamp = Ui.Text(time.ToString("HH:mm:ss", Loc.Culture), 11, "TextMuted");
            stamp.Width = 62;
            DockPanel.SetDock(stamp, Dock.Left);
            row.Children.Add(stamp);
            var icon = Ui.Text(glyph, 12, glyph == "⚠" ? "OrangeBrush" : "AccentBrush", bold: true);
            icon.Width = 20;
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var message = Info(text, "TextPrimary", 12);
            message.Margin = new Thickness(0);
            row.Children.Add(message);
            stack.Children.Add(row);
        }
        AutomationProperties.SetLiveSetting(stack, AutomationLiveSetting.Polite);
        return stack;
    }
}

/// <summary>Miniature of all monitors with the click point(s) or area – helps to see which screen is used.</summary>
internal sealed class MonitorPreview : FrameworkElement
{
    private List<ScreenInfo.Monitor> _monitors = new();
    private AutoClickerProfile? _profile;

    public void Update(AutoClickerProfile profile)
    {
        _profile = profile;
        _monitors = ScreenInfo.Monitors();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_monitors.Count == 0 || ActualWidth <= 0 || _profile is null) return;
        var left = _monitors.Min(m => m.Bounds.X);
        var top = _monitors.Min(m => m.Bounds.Y);
        var right = _monitors.Max(m => m.Bounds.X + m.Bounds.Width);
        var bottom = _monitors.Max(m => m.Bounds.Y + m.Bounds.Height);
        var scale = Math.Min(ActualWidth / (right - left), ActualHeight / (bottom - top));
        var offsetX = (ActualWidth - (right - left) * scale) / 2;
        Point P(double x, double y) => new(offsetX + (x - left) * scale, (y - top) * scale);

        var fill = TryFindResource("SurfaceAlt") as Brush ?? Brushes.LightGray;
        var border = new Pen(TryFindResource("CardBorder") as Brush ?? Brushes.Gray, 1);
        var text = TryFindResource("TextMuted") as Brush ?? Brushes.Gray;
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        foreach (var m in _monitors)
        {
            var rect = new Rect(P(m.Bounds.X, m.Bounds.Y), P(m.Bounds.X + m.Bounds.Width, m.Bounds.Y + m.Bounds.Height));
            rect.Inflate(-1.5, -1.5);
            dc.DrawRoundedRectangle(fill, border, rect, 3, 3);
            var label = new FormattedText(m.Index.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(rect.X + 5, rect.Y + 3));
        }

        switch (_profile.Position)
        {
            case PositionMode.Fixed:
                dc.DrawEllipse(accent, new Pen(Brushes.White, 1.5), P(_profile.FixedX, _profile.FixedY), 4.5, 4.5);
                break;
            case PositionMode.Sequence:
                Point? previous = null;
                var pen = new Pen(accent, 1.2);
                foreach (var point in _profile.Points)
                {
                    var p = P(point.X, point.Y);
                    if (previous is { } from) dc.DrawLine(pen, from, p);
                    dc.DrawEllipse(accent, new Pen(Brushes.White, 1), p, 3.5, 3.5);
                    previous = p;
                }
                break;
            case PositionMode.RandomArea when _profile.Area is { IsValid: true } area:
                var color = (accent as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B)), new Pen(accent, 1.5),
                    new Rect(P(area.X, area.Y), P(area.X + area.Width, area.Y + area.Height)));
                break;
        }
    }
}
