using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ViennaAtomicClock.AutoClicker;

/// <summary>
/// Autoclicker page: status card with start/stop on top, settings in cards below (interval, jitter, click,
/// repeat, position, hotkeys, profiles, recording, safety, test mode). The engine runs on its own thread; the page
/// only reads its counters ten times per second.
/// </summary>
internal sealed partial class AutoClickerPage : Grid
{
    private const double WideWidth = 1100;
    private const double MediumWidth = 750;

    private readonly AutoClickerStorage _storage;
    private readonly AutoClickerSettings _settings;
    private readonly List<AutoClickerProfile> _profiles;
    private AutoClickerProfile _profile;
    private readonly AutoClickerEngine _engine;
    private readonly IMouseInputService _mouse;
    private readonly ClickRecorderService _recorder;
    private IGlobalHotkeyService? _hotkeys;
    private IReadOnlyDictionary<HotkeyAction, HotkeyStatus> _hotkeyStatus = new Dictionary<HotkeyAction, HotkeyStatus>();
    private ClickSequence _sequence = new();

    private readonly DispatcherTimer _ui = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
    private readonly Grid _columns = new();
    private readonly Dictionary<string, (Border Host, Func<UIElement> Build)> _cards = new();
    private readonly List<(DateTime Time, string Glyph, string Text)> _messages = new();
    private readonly LinkedList<double> _cpsHistory = new();
    private string _layoutMode = "";
    private bool _playing;
    private string? _lastResultText;
    private StatisticsSnapshot? _testResult;
    private AutoClickerRunResult? _lastResult;
    private bool _faultShown;
    private DateTime _lastSpoken;

    /// <summary>Raised (at most twice per second) when the header line of the main window should change.</summary>
    public event Action? Changed;

    public AutoClickerPage() : this(new AutoClickerStorage(), new WindowsMouseInputService()) { }

    public AutoClickerPage(AutoClickerStorage storage, IMouseInputService mouse)
    {
        _storage = storage;
        _mouse = mouse;
        _settings = storage.LoadSettings();
        var loaded = storage.LoadProfiles();
        _profiles = loaded.Profiles;
        _profile = _profiles.FirstOrDefault(p => p.Name == _settings.LastProfile)
                   ?? _profiles.FirstOrDefault(p => p.Name == _settings.DefaultProfile)
                   ?? _profiles[0];
        _sequence = storage.LoadSequences().FirstOrDefault() ?? new ClickSequence { Name = "Aufnahme" };
        _engine = new AutoClickerEngine(mouse);
        _engine.StateChanged += _ => Dispatcher.BeginInvoke(OnEngineState);
        _engine.Finished += result => Dispatcher.BeginInvoke(() => OnFinished(result));
        _recorder = new ClickRecorderService(IsOnOwnWindow);
        if (loaded.Damaged.Count > 0) AddMessage("⚠", Loc.F("ac.msg.damaged", string.Join(", ", loaded.Damaged)));

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        Children.Add(BuildStatusCard());
        _scroll.Content = _columns;
        _scroll.Margin = new Thickness(0, 14, 0, 0);
        SetRow(_scroll, 1);
        Children.Add(_scroll);

        BuildCards();
        _ui.Tick += (_, _) => UpdateLive();
        _save.Tick += (_, _) => { _save.Stop(); SaveNow(); };
        SizeChanged += (_, _) => ApplyLayout();
        IsVisibleChanged += (_, _) => OnVisibility();
        Loc.Changed += () => { BuildCards(); RebuildStatusTexts(); };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        Loaded += (_, _) => { if (_settings.Used) EnsureHotkeys(); };
        UpdateStatus();
    }

    public AutoClickerState State => _engine.State;
    public AutoClickerProfile Profile => _profile;
    public string LayoutMode => _layoutMode;
    internal AutoClickerEngine Engine => _engine;

    /// <summary>Short text for the main window's header.</summary>
    public string StatusLine => _engine.State switch
    {
        AutoClickerState.Running => Loc.F("ac.header.running", _engine.Statistics.Snapshot(Stopwatch.GetTimestamp()).ActualCps.ToString("0", Loc.Culture)),
        AutoClickerState.Paused => Loc.T("ac.state.paused"),
        AutoClickerState.Starting => Loc.T("ac.state.starting"),
        _ => Loc.T("ac.state.ready") + " · " + _profile.Name
    };

    public bool IsActive => _engine.State is not AutoClickerState.Idle || _playing || _recorder.IsRecording;

    // ───────────────────────── lifecycle ─────────────────────────

    private void OnVisibility()
    {
        if (IsVisible)
        {
            if (!_settings.Used)
            {
                _settings.Used = true; // from now on the hotkeys are registered whenever Freedom starts
                _storage.SaveSettings(_settings);
            }
            EnsureHotkeys();
            _ui.Start();
            UpdateLive();
        }
        else if (_engine.State == AutoClickerState.Idle && !_recorder.IsRecording) _ui.Stop();
    }

    /// <summary>Stops everything before Freedom closes: no click, hook or hotkey survives the app.</summary>
    public void Shutdown()
    {
        _save.Stop();
        SaveNow();
        _engine.EmergencyStop();
        _engine.WaitForIdle(TimeSpan.FromSeconds(1));
        if (_recorder.IsRecording) _recorder.Stop("");
        _hotkeys?.Dispose();
        _hotkeys = null;
        _engine.Dispose();
    }

    private void EnsureHotkeys()
    {
        if (!_settings.GlobalHotkeys)
        {
            _hotkeys?.UnregisterAll();
            _hotkeyStatus = new Dictionary<HotkeyAction, HotkeyStatus>();
            return;
        }
        if (_hotkeys is null)
        {
            try
            {
                _hotkeys = new GlobalHotkeyService();
                _hotkeys.Pressed += OnHotkey;
                _hotkeys.SystemSignal += OnSystemSignal;
            }
            catch (Exception error)
            {
                AddMessage("⚠", Loc.F("ac.msg.hotkeysFailed", error.Message));
                return;
            }
        }
        _hotkeyStatus = _hotkeys.Apply(_profile.Hotkeys);
        foreach (var (action, status) in _hotkeyStatus.Where(s => s.Value == HotkeyStatus.InUse))
            AddMessage("⚠", Loc.F("ac.msg.hotkeyInUse", _profile.Hotkeys[action]?.ToString() ?? "", Loc.T("ac.hk." + action)));
    }

    private void SaveNow()
    {
        _storage.SaveProfile(_profile);
        _settings.LastProfile = _profile.Name;
        _storage.SaveSettings(_settings);
    }

    /// <summary>Something in the profile changed: save soon and refresh what depends on it.</summary>
    private void ProfileChanged(params string[] cards)
    {
        _save.Stop();
        _save.Start();
        foreach (var card in cards) RefreshCard(card);
        UpdateStatus();
    }

    // ───────────────────────── run control ─────────────────────────

    private ScreenArea Screen => ScreenInfo.VirtualScreen();

    private Func<bool>? HoldKeyCheck()
    {
        var key = _profile.Hotkeys.GetValueOrDefault(HotkeyAction.StartStop) ?? _profile.Hotkeys.GetValueOrDefault(HotkeyAction.Start);
        if (key is null) return null;
        var vk = GlobalHotkeyService.VirtualKeyOf(key);
        return () => GlobalHotkeyService.IsDown(vk);
    }

    public void StartRun(bool? testMode = null, AutoClickerProfile? overrideProfile = null)
    {
        if (_engine.State != AutoClickerState.Idle || _playing) return;
        var profile = overrideProfile ?? _profile;
        var test = testMode ?? _settings.TestMode;
        var (config, errors) = AutoClickerRunConfiguration.Create(profile, test, Screen, NativeTiming.DoubleClickTime(), HoldKeyCheck());
        if (config is null)
        {
            ShowErrors(errors);
            return;
        }
        if (!test && profile.ConfirmLongRuns && profile.Repeat == RepeatMode.UntilStopped && !profile.MaxRuntimeEnabled
            && MessageBox.Show(Window.GetWindow(this)!, Loc.T("ac.confirm.long"), "Freedom", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _faultShown = false;
        _cpsHistory.Clear();
        _testResult = null;
        if (!_engine.Start(config)) return;
        if (profile.EscapeStopsWhileRunning) _hotkeys?.SetTemporary(HotkeyAction.EmergencyStop, Hotkey.Key(0x1B));
        AddMessage("▶", test ? Loc.F("ac.msg.testStarted", Cps(config.TargetCps)) : Loc.F("ac.msg.started", Cps(config.TargetCps), profile.Name));
        _ui.Start();
    }

    public void StopRun()
    {
        if (_playing || _engine.Configuration?.Kind == RunKind.Playback) _playing = false;
        _engine.Stop();
    }

    public void EmergencyStop()
    {
        _playing = false;
        _engine.EmergencyStop();
        AddMessage("■", Loc.T("ac.msg.emergency"));
        UpdateStatus();
    }

    private void TogglePause()
    {
        if (_engine.State == AutoClickerState.Running) _engine.Pause();
        else if (_engine.State == AutoClickerState.Paused) _engine.Resume();
    }

    private void ShowErrors(IEnumerable<ValidationError> errors)
    {
        foreach (var error in errors) AddMessage("⚠", Loc.F(error.Key, error.Args));
        _faultShown = true;
        UpdateStatus();
    }

    private void OnEngineState()
    {
        UpdateStatus();
        Changed?.Invoke();
    }

    private void OnFinished(AutoClickerRunResult result)
    {
        _hotkeys?.SetTemporary(HotkeyAction.EmergencyStop, null);
        _lastResult = result;
        var s = result.Statistics;
        var summary = Loc.F("ac.msg.finished", s.Successful.ToString("N0", Loc.Culture), s.Elapsed.TotalSeconds.ToString("0.0", Loc.Culture), Cps(s.AverageCps));
        switch (result.End)
        {
            case RunEnd.Faulted:
                _faultShown = true;
                AddMessage("⚠", Loc.F("ac.msg.faulted", result.Error ?? "?") + " " + Loc.T("ac.msg.uipi"));
                break;
            case RunEnd.EmergencyStop:
                AddMessage("■", summary);
                break;
            default:
                AddMessage(result.End == RunEnd.Completed ? "✓" : "■", summary);
                break;
        }
        if (s.Failed > 0 && result.End != RunEnd.Faulted) AddMessage("⚠", Loc.F("ac.msg.failedClicks", s.Failed.ToString("N0", Loc.Culture)) + " " + Loc.T("ac.msg.uipi"));
        if (result.TestMode) _testResult = s;
        _lastResultText = summary;
        _playing = false;
        RefreshCard("test");
        RefreshCard("recording");
        UpdateStatus();
        UpdateLive();
        Changed?.Invoke();
        if (!IsVisible && !_recorder.IsRecording) _ui.Stop();
    }

    // ───────────────────────── hotkeys and system ─────────────────────────

    private void OnHotkey(HotkeyAction action)
    {
        if (_capturing is not null) return; // the user is assigning a new key right now
        var state = _engine.State;
        switch (action)
        {
            case HotkeyAction.StartStop:
                if (state == AutoClickerState.Idle) StartRun();
                else StopRun();
                break;
            case HotkeyAction.Start:
                StartRun();
                break;
            case HotkeyAction.Stop:
                StopRun();
                break;
            case HotkeyAction.PauseResume:
                TogglePause();
                break;
            case HotkeyAction.EmergencyStop:
                EmergencyStop();
                if (_recorder.IsRecording) StopRecording();
                break;
            case HotkeyAction.CapturePosition:
                CaptureCursorPosition();
                break;
            case HotkeyAction.RecordToggle:
                if (_recorder.IsRecording) StopRecording();
                else StartRecording();
                break;
            case HotkeyAction.PlaybackToggle:
                if (_playing || _engine.Configuration?.Kind == RunKind.Playback && state != AutoClickerState.Idle) StopRun();
                else StartPlayback();
                break;
        }
    }

    private void OnSystemSignal(string signal)
    {
        if (_engine.State == AutoClickerState.Idle) return;
        if ((signal == "lock" && _profile.StopOnLock) || (signal == "display" && _profile.StopOnDisplayOff))
        {
            _engine.EmergencyStop();
            AddMessage("■", Loc.T(signal == "lock" ? "ac.msg.stoppedLock" : "ac.msg.stoppedDisplay"));
        }
    }

    private void CaptureCursorPosition()
    {
        if (!_mouse.TryGetCursor(out var x, out var y) && !new WindowsMouseInputService().TryGetCursor(out x, out y)) return;
        if (_engine.State != AutoClickerState.Idle) return;
        if (_profile.Position == PositionMode.Sequence) _profile.Points.Add(new ClickPoint(x, y));
        else
        {
            _profile.FixedX = x;
            _profile.FixedY = y;
            if (_profile.Position == PositionMode.Current) _profile.Position = PositionMode.Fixed;
        }
        AddMessage("⌖", Loc.F("ac.msg.captured", x, y));
        ProfileChanged("position");
    }

    private bool IsOnOwnWindow(int x, int y)
    {
        if (Window.GetWindow(this) is not { } window) return false;
        var own = new WindowInteropHelper(window).Handle;
        var hit = NativeTiming.RootWindowAt(x, y);
        return own != IntPtr.Zero && hit == own;
    }

    // ───────────────────────── recording and playback ─────────────────────────

    private DateTime _recordingStarted;

    private void StartRecording()
    {
        if (_engine.State != AutoClickerState.Idle || _recorder.IsRecording) return;
        if (!_recorder.Start())
        {
            AddMessage("⚠", Loc.T("ac.msg.recordFailed"));
            return;
        }
        _recordingStarted = DateTime.Now;
        AddMessage("●", Loc.T("ac.msg.recording"));
        _ui.Start();
        RefreshCard("recording");
        UpdateStatus();
    }

    private void StopRecording()
    {
        if (!_recorder.IsRecording) return;
        var recorded = _recorder.Stop(Loc.T("ac.rec.defaultName"));
        _sequence = new ClickSequence { Name = recorded.Name, Events = recorded.Events, Speed = _sequence.Speed, Repeat = _sequence.Repeat };
        _storage.SaveSequence(_sequence);
        AddMessage("■", Loc.F("ac.msg.recorded", _sequence.Events.Count));
        RefreshCard("recording");
        UpdateStatus();
    }

    private void StartPlayback()
    {
        if (_engine.State != AutoClickerState.Idle || _recorder.IsRecording) return;
        var (config, errors) = AutoClickerRunConfiguration.ForPlayback(_sequence, _profile, _settings.TestMode, Screen);
        if (config is null)
        {
            ShowErrors(errors);
            return;
        }
        _testResult = null;
        _playing = _engine.Start(config);
        if (!_playing) return;
        if (_profile.EscapeStopsWhileRunning) _hotkeys?.SetTemporary(HotkeyAction.EmergencyStop, Hotkey.Key(0x1B));
        AddMessage("▶", Loc.F("ac.msg.playback", config.Sequence.Length, config.SequenceRepeat));
        _ui.Start();
        RefreshCard("recording");
    }

    // ───────────────────────── keyboard: hotkey capture and Escape ─────────────────────────

    private HotkeyAction? _capturing;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_capturing is { } action)
        {
            e.Handled = true;
            if (key == Key.Escape) { _capturing = null; RefreshCard("hotkeys"); return; }
            if (key is Key.Delete or Key.Back) { AssignHotkey(action, null); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var modifiers = (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? HotkeyModifiers.Control : 0) |
                            (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? HotkeyModifiers.Alt : 0) |
                            (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? HotkeyModifiers.Shift : 0) |
                            (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows) ? HotkeyModifiers.Windows : 0);
            AssignHotkey(action, new Hotkey(KeyInterop.VirtualKeyFromKey(key), modifiers));
            return;
        }
        // Escape on the page is always an emergency stop while something runs.
        if (key == Key.Escape && (_engine.State != AutoClickerState.Idle || _playing))
        {
            EmergencyStop();
            e.Handled = true;
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_capturing is not { } action || e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        e.Handled = true;
        AssignHotkey(action, new Hotkey(0, HotkeyModifiers.None, e.ChangedButton == MouseButton.XButton1 ? MouseButtonKind.X1 : MouseButtonKind.X2));
    }

    private void AssignHotkey(HotkeyAction action, Hotkey? hotkey)
    {
        _capturing = null;
        var map = new Dictionary<HotkeyAction, Hotkey?>(_profile.Hotkeys) { [action] = hotkey };
        var duplicates = GlobalHotkeyService.FindDuplicates(map);
        if (hotkey is not null && duplicates.Contains(action))
        {
            var other = duplicates.First(a => a != action);
            AddMessage("⚠", Loc.F("ac.msg.hotkeyDuplicate", hotkey.ToString(), Loc.T("ac.hk." + other)));
            RefreshCard("hotkeys");
            return;
        }
        if (action == HotkeyAction.EmergencyStop && hotkey is null)
        {
            AddMessage("⚠", Loc.T("ac.msg.emergencyRequired"));
            RefreshCard("hotkeys");
            return;
        }
        _profile.Hotkeys[action] = hotkey;
        EnsureHotkeys();
        ProfileChanged("hotkeys", "repeat");
    }

    // ───────────────────────── messages ─────────────────────────

    private void AddMessage(string glyph, string text)
    {
        _messages.Insert(0, (DateTime.Now, glyph, text));
        if (_messages.Count > 8) _messages.RemoveAt(_messages.Count - 1);
        RefreshCard("messages");
    }

    private static string Cps(double cps) => cps.ToString(cps < 10 ? "0.##" : "0.#", Loc.Culture);

    // ───────────────────────── layout ─────────────────────────

    private static readonly string[] LeftCards = { "interval", "jitter", "click", "repeat" };
    private static readonly string[] RightCards = { "position", "hotkeys", "profiles", "recording" };
    private static readonly string[] BottomCards = { "safety", "test", "messages" };

    private void ApplyLayout()
    {
        var width = ActualWidth;
        if (width <= 0) return;
        var mode = width > WideWidth ? "wide" : width >= MediumWidth ? "medium" : "narrow";
        if (mode == _layoutMode) return;
        _layoutMode = mode;
        ArrangeCards();
        UpdateStatusLayout();
    }

    /// <summary>Two columns above 1100 px, a flexible two-column grid down to 750 px, one column below.</summary>
    private void ArrangeCards()
    {
        foreach (var (host, _) in _cards.Values)
            if (host.Parent is Panel panel) panel.Children.Remove(host);
        _columns.Children.Clear();
        _columns.ColumnDefinitions.Clear();
        _columns.RowDefinitions.Clear();

        StackPanel Stack(IEnumerable<string> names)
        {
            var stack = new StackPanel();
            foreach (var name in names) stack.Children.Add(_cards[name].Host);
            return stack;
        }

        switch (_layoutMode)
        {
            case "wide":
            case "medium":
            {
                _columns.ColumnDefinitions.Add(new ColumnDefinition());
                _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                _columns.ColumnDefinitions.Add(new ColumnDefinition());
                _columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var (left, right) = _layoutMode == "wide"
                    ? (LeftCards, RightCards)
                    : (new[] { "interval", "click", "position", "profiles" }, new[] { "jitter", "repeat", "hotkeys", "recording" });
                var l = Stack(left);
                var r = Stack(right);
                SetColumn(r, 2);
                _columns.Children.Add(l);
                _columns.Children.Add(r);
                var bottom = new UniformGrid { Columns = _layoutMode == "wide" ? 3 : 2 };
                foreach (var name in BottomCards) bottom.Children.Add(_cards[name].Host);
                SetRow(bottom, 1);
                SetColumnSpan(bottom, 3);
                _columns.Children.Add(bottom);
                break;
            }
            default:
                _columns.Children.Add(Stack(LeftCards.Concat(RightCards).Concat(BottomCards)));
                break;
        }
    }
}

/// <summary>Small Win32 helpers for the page.</summary>
internal static class NativeTiming
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>Windows double-click time in ms (default 500).</summary>
    public static double DoubleClickTime() => GetDoubleClickTime() is var ms and > 0 ? ms : 500;

    /// <summary>Top-level window under a physical point.</summary>
    public static IntPtr RootWindowAt(int x, int y) => GetAncestor(WindowFromPoint(new NativePoint { X = x, Y = y }), 2);
}
