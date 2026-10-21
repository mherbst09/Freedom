using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ViennaAtomicClock.PunchFactory;

/// <summary>
/// Punch Factory – idle boxing game. The page connects engine (rules), scene (3D pixel studio), audio, controller and
/// the panels: values, combo and abilities on the left, the studio in the middle, producers and upgrades on the right,
/// career/achievements/statistics/cosmetics/studios/settings at the bottom. The frame loop only runs while visible.
/// </summary>
internal sealed class PunchFactoryPage : Grid
{
    private const double WideWidth = 1180;
    private static readonly Brush Gold = Frozen(Color.FromRgb(0xF2, 0xC2, 0x32));
    private static readonly Brush FuryBrush = Frozen(Color.FromRgb(0xFF, 0x6A, 0x1A));

    private readonly PunchStorage _storage;
    private readonly PunchSettings _settings;
    private readonly PunchEngine _engine;
    private readonly PunchScene _scene = new();
    private readonly Gamepad _gamepad = new();
    private PunchAudio? _audio;
    private TimeSpan _lastFrame;
    private bool _looping;
    private double _uiTimer, _saveTimer, _shopTimer;
    private BuyMode _buyMode = BuyMode.One;
    private string _shopTab = "producers";
    private string _layout = "";
    private OfflineReport? _offline;

    // Layout parts.
    private readonly Grid _main = new();
    private readonly Border _sceneFrame = new() { CornerRadius = new CornerRadius(16), ClipToBounds = true, Background = Brushes.Black };
    private readonly Grid _sceneHost = new() { Background = Brushes.Transparent, Cursor = Cursors.Hand };
    private readonly Canvas _floaters = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly StackPanel _toasts = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0), IsHitTestVisible = false };
    private readonly StackPanel _left = new();
    private readonly Grid _right = new();
    private readonly StackPanel _shopList = new();
    private readonly WrapPanel _bottom = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Grid _overlay = new() { Visibility = Visibility.Collapsed };
    private readonly Border _overlayHost = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), MaxWidth = 760 };

    // Live texts.
    private readonly TextBlock _punch = Ui.Text("0", 34, bold: true);
    private readonly TextBlock _perSecond = Ui.Text("", 14, "TextSecondary", bold: true);
    private readonly TextBlock _perClick = Ui.Text("", 13, "TextSecondary");
    private readonly TextBlock _studioName = Ui.Text("", 12.5, "AccentBrush", bold: true);
    private readonly TextBlock _comboText = Ui.Text("0", 26, bold: true);
    private readonly TextBlock _comboMult = Ui.Text("×1", 15, "AccentBrush", bold: true);
    private readonly Border _comboFill = new() { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly Grid _comboTrack = new() { Height = 8, Margin = new Thickness(0, 6, 0, 2) };
    private readonly TextBlock _comboNext = Ui.Text("", 11, "TextMuted");
    private readonly TextBlock _furyText = Ui.Text("", 13, bold: true);
    private readonly TextBlock _stats = Ui.Text("", 12, "TextSecondary");
    private readonly List<(AbilityDef Def, Button Button, TextBlock Label)> _abilityButtons = new();
    private readonly List<(string Id, Border Row, TextBlock Cost, TextBlock Gain, TextBlock Count, TextBlock Extra)> _producerRows = new();
    private readonly List<(UpgradeDef Def, Border Row, TextBlock Cost, Button Buy)> _upgradeRows = new();
    private Button? _prestigeButton;
    private Button? _achievementButton;

    // Floating numbers (pooled).
    private const int FloaterCount = 40;
    private readonly TextBlock[] _floaterText = new TextBlock[FloaterCount];
    private readonly double[] _floaterLife = new double[FloaterCount];
    private readonly double[] _floaterSpeed = new double[FloaterCount];
    private int _nextFloater;

    public event Action? Changed;

    public PunchFactoryPage() : this(new PunchStorage(), BalanceConfig.Load()) { }

    public PunchFactoryPage(PunchStorage storage, BalanceConfig config, Random? random = null)
    {
        _storage = storage;
        _settings = storage.LoadSettings();
        var save = storage.Load();
        _engine = new PunchEngine(config, save, random);
        var report = _engine.ApplyOffline(DateTime.UtcNow);
        if (report.Gain > BigNum.Zero || report.ClockProblem) _offline = report;
        ApplySettings();

        _engine.Punched += OnPunched;
        _engine.FuryStarted += () => { _audio?.Play(PunchSound.Fury); _audio?.SetFury(true); Vibrate(0.8, 400); };
        _engine.FuryEnded += () => _audio?.SetFury(false);
        _engine.AchievementUnlocked += def => { Toast("🏆 " + AchievementTitle(def), Gold); _audio?.Play(PunchSound.Achievement); };
        _engine.EnvironmentUnlocked += def => { Toast("🏟 " + Loc.F("pf.toast.studio", Loc.T("pf.env." + def.Id)), Gold); _audio?.SetEnvironment(EnvironmentIndex()); UpdateScene(); };
        _engine.AbilityUsed += _ => _audio?.Play(PunchSound.Ability);
        _engine.EventSpawned += _ => _audio?.Play(PunchSound.EventSpawn, 1, 0.7f);
        _engine.EventCaught += (e, reward) =>
        {
            _audio?.Play(PunchSound.EventCatch);
            Toast(Loc.T("pf.ev." + e.Def.Id) + (reward > BigNum.Zero ? "  +" + Format(reward) : ""), Gold);
        };
        _engine.Changed += () => _shopTimer = 0;

        for (var i = 0; i < FloaterCount; i++)
        {
            _floaterText[i] = new TextBlock { FontWeight = FontWeights.Black, Visibility = Visibility.Collapsed, IsHitTestVisible = false, Effect = null };
            _floaters.Children.Add(_floaterText[i]);
        }

        Children.Add(_main);
        var dim = new Border { Background = Frozen(Color.FromArgb(160, 0, 0, 0)) };
        _overlay.Children.Add(dim);
        _overlay.Children.Add(_overlayHost);
        Children.Add(_overlay);

        _sceneHost.Children.Add(new Viewbox { Child = _scene.View, Stretch = Stretch.Uniform });
        _sceneHost.Children.Add(_floaters);
        _sceneHost.Children.Add(_toasts);
        _sceneFrame.Child = _sceneHost;
        _sceneHost.MouseLeftButtonDown += OnSceneClick;
        AutomationProperties.SetName(_sceneHost, Loc.T("pf.bag"));
        UpdateScene();

        Focusable = true;
        FocusVisualStyle = null;
        PreviewKeyDown += (_, e) => HandleKey(e);
        SizeChanged += (_, _) => ApplyLayout();
        IsVisibleChanged += (_, _) => OnVisibility();
        Loc.Changed += OnLanguageChanged;
        ApplyLayout();
    }

    internal PunchEngine Engine => _engine;
    internal PunchScene Scene => _scene;
    internal string LayoutMode => _layout;
    internal bool OverlayOpen => _overlay.Visibility == Visibility.Visible;

    public string StatusLine => Loc.F("pf.header", Format(_engine.PpsNow));

    private void OnLanguageChanged()
    {
        _layout = "";
        ApplyLayout();
    }

    public void Shutdown()
    {
        Loc.Changed -= OnLanguageChanged;
        StopLoop();
        Persist();
        _storage.SaveSettings(_settings);
        _gamepad.Stop();
        _audio?.Dispose();
        _audio = null;
    }

    private void Persist() => _storage.Save(_engine.Save);

    private string Format(BigNum value) => value.Format(Loc.Culture);

    // ───────────────────────── lifecycle and loop ─────────────────────────

    private void OnVisibility()
    {
        if (IsVisible)
        {
            if (_audio is null)
            {
                _audio = new PunchAudio(_settings);
                _audio.Start();
                _audio.SetEnvironment(EnvironmentIndex());
            }
            _audio.SetMusic(true);
            StartLoop();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => Focus());
            if (_storage.LoadNote.Length > 0)
            {
                Toast(Loc.T(_storage.LoadNote), FuryBrush);
                _storage.ClearLoadNote();
            }
            if (_offline is { } report)
            {
                _offline = null;
                ShowOffline(report);
            }
        }
        else
        {
            _audio?.SetMusic(false);
            StopLoop();
            Persist();
        }
    }

    private void StartLoop()
    {
        if (_looping || !IsVisible) return;
        _looping = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopLoop()
    {
        if (!_looping) return;
        _looping = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args || args.RenderingTime == _lastFrame) return;
        var seconds = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.25, (args.RenderingTime - _lastFrame).TotalSeconds);
        _lastFrame = args.RenderingTime;
        Step(seconds);
    }

    /// <summary>One frame: rules, controller, scene, numbers, panels (throttled), autosave. Also used by the tests.</summary>
    internal void Step(double seconds)
    {
        _engine.Tick(seconds);
        PollGamepad();
        _scene.Advance(seconds, _engine.Fury);
        _scene.Render(new SceneState(_engine.Config.Producers.Select(p => _engine.Save.Count(p.Id)).ToArray(), _engine.Fury, _engine.Event));
        UpdateFloaters(seconds);
        _uiTimer -= seconds;
        if (_uiTimer <= 0)
        {
            _uiTimer = 0.1;
            UpdateTexts();
        }
        _shopTimer -= seconds;
        if (_shopTimer <= 0)
        {
            _shopTimer = 0.3;
            UpdateShop();
        }
        _saveTimer += seconds;
        if (_saveTimer >= _engine.Config.AutosaveSeconds)
        {
            _saveTimer = 0;
            Persist();
        }
    }

    // ───────────────────────── input ─────────────────────────

    private void OnSceneClick(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(_scene.View);
        if (_engine.Event is { } active && _scene.HitsEvent(point, active))
        {
            _engine.HitEvent();
            Floater("✦", e.GetPosition(_sceneHost), Gold, 22);
        }
        else if (_scene.HitsBag(point)) Punch(PunchSource.Click, e.GetPosition(_sceneHost));
        Focus();
        e.Handled = true;
    }

    private void Punch(PunchSource source, Point? at = null)
    {
        _lastPunchPoint = at;
        _engine.Punch(source);
    }

    private Point? _lastPunchPoint;

    private void OnPunched(PunchResult punch)
    {
        _scene.Hit(punch);
        var pitch = 1 + Math.Min(punch.Combo, 250) / 400.0;
        var sound = punch.Critical ? PunchSound.Crit : punch.Strength >= 2.5 ? PunchSound.Heavy : punch.Combo % 3 == 0 ? PunchSound.Medium : PunchSound.Light;
        _audio?.Play(sound, pitch * (0.95 + Random.Shared.NextDouble() * 0.1), punch.Source == PunchSource.Flurry ? 0.6f : 1f);
        if (punch.Critical || punch.Source == PunchSource.PowerPunch) Vibrate(punch.Critical ? 0.6 : 0.9, 140);
        var center = new Point(_sceneHost.ActualWidth / 2, _sceneHost.ActualHeight * 0.45);
        var at = _lastPunchPoint ?? new Point(center.X + Random.Shared.Next(-60, 60), center.Y + Random.Shared.Next(-40, 40));
        _lastPunchPoint = null;
        var text = "+" + Format(punch.Amount) + (punch.Critical ? "  " + Loc.T("pf.crit") : "");
        Floater(text, at, punch.Critical ? Gold : punch.Fury ? FuryBrush : Brushes.White, punch.Critical ? 24 : 17);
    }

    internal void HandleKey(KeyEventArgs e)
    {
        if (e.Handled || !IsVisible) return;
        if (OverlayOpen)
        {
            if (e.Key == Key.Escape) { CloseOverlay(); e.Handled = true; }
            return;
        }
        if (e.OriginalSource is TextBox or Slider) return;
        if (e.Key is Key.Space or Key.Enter)
        {
            if (!e.IsRepeat) Punch(PunchSource.Click);
            e.Handled = true;
            return;
        }
        var index = e.Key switch { Key.D1 or Key.NumPad1 => 0, Key.D2 or Key.NumPad2 => 1, Key.D3 or Key.NumPad3 => 2, Key.D4 or Key.NumPad4 => 3, Key.D5 or Key.NumPad5 => 4, Key.D6 or Key.NumPad6 => 5, _ => -1 };
        if (index >= 0 && index < _engine.Config.Abilities.Count)
        {
            UseAbility(_engine.Config.Abilities[index]);
            e.Handled = true;
        }
        else if (e.Key == Key.E && _engine.Event is not null)
        {
            _engine.HitEvent();
            e.Handled = true;
        }
    }

    private void PollGamepad()
    {
        var pressed = _gamepad.Poll();
        if (pressed == 0) return;
        if ((pressed & Gamepad.A) != 0) Punch(PunchSource.Controller);
        var abilities = _engine.Config.Abilities;
        var map = new[] { Gamepad.X, Gamepad.Y, Gamepad.B, Gamepad.LeftShoulder, Gamepad.RightShoulder };
        for (var i = 0; i < map.Length && i < abilities.Count; i++)
            if ((pressed & map[i]) != 0) UseAbility(abilities[i]);
        if ((pressed & Gamepad.Start) != 0 && _engine.Event is not null) _engine.HitEvent();
    }

    private void Vibrate(double strength, int milliseconds)
    {
        if (_settings.Vibration) _gamepad.Vibrate(strength, TimeSpan.FromMilliseconds(milliseconds));
    }

    private void UseAbility(AbilityDef def)
    {
        if (!_engine.UseAbility(def.Id)) _audio?.Play(PunchSound.Denied, 1, 0.5f);
    }

    // ───────────────────────── layout ─────────────────────────

    private void ApplyLayout()
    {
        var mode = ActualWidth == 0 || ActualWidth >= WideWidth ? "wide" : "narrow";
        if (mode == _layout) return;
        _layout = mode;
        _main.Children.Clear();
        _main.RowDefinitions.Clear();
        _main.ColumnDefinitions.Clear();
        BuildLeft();
        BuildRight();
        BuildBottom();
        var top = TopBar();
        _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (mode == "wide")
        {
            _main.RowDefinitions.Add(new RowDefinition());
            _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
            _main.ColumnDefinitions.Add(new ColumnDefinition());
            _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            SetColumnSpan(top, 3);
            _main.Children.Add(top);
            var left = new ScrollViewer { Content = _left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 12, 0), Focusable = false };
            SetRow(left, 1);
            _main.Children.Add(left);
            _sceneFrame.Margin = new Thickness(0, 10, 0, 0);
            SetRow(_sceneFrame, 1);
            SetColumn(_sceneFrame, 1);
            _main.Children.Add(Detach(_sceneFrame));
            _right.Margin = new Thickness(12, 10, 0, 0);
            SetRow(_right, 1);
            SetColumn(_right, 2);
            _main.Children.Add(Detach(_right));
            SetRow(_bottom, 2);
            SetColumnSpan(_bottom, 3);
            _main.Children.Add(Detach(_bottom));
        }
        else
        {
            // Narrow windows: studio on top, then the panels below each other in one scroll area.
            _main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 220 });
            _main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });
            _main.Children.Add(top);
            _sceneFrame.Margin = new Thickness(0, 10, 0, 0);
            SetRow(_sceneFrame, 1);
            SetColumn(_sceneFrame, 0);
            _main.Children.Add(Detach(_sceneFrame));
            var stack = new StackPanel();
            stack.Children.Add(Detach(_bottom));
            _right.Margin = new Thickness(0, 10, 0, 0);
            _right.Height = 420;
            stack.Children.Add(Detach(_right));
            stack.Children.Add(Detach(_left));
            var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            SetRow(scroll, 2);
            _main.Children.Add(scroll);
        }
        UpdateTexts();
        UpdateShop();
    }

    private static T Detach<T>(T element) where T : FrameworkElement
    {
        if (element.Parent is Panel panel) panel.Children.Remove(element);
        else if (element.Parent is ScrollViewer viewer) viewer.Content = null;
        else if (element.Parent is Decorator decorator) decorator.Child = null;
        return element;
    }

    private UIElement TopBar()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Text("PUNCH FACTORY", 22, "AccentBrush", bold: true);
        title.SetResourceReference(TextBlock.FontFamilyProperty, "StencilFont");
        title.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(title);
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var punchRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        Detach(_punch);
        punchRow.Children.Add(_punch);
        var unit = Ui.Text(" " + Loc.T("pf.currency"), 14, "TextSecondary", bold: true);
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        unit.Margin = new Thickness(0, 0, 0, 7);
        punchRow.Children.Add(unit);
        center.Children.Add(punchRow);
        var rates = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        Detach(_perSecond);
        Detach(_perClick);
        rates.Children.Add(_perSecond);
        _perClick.Margin = new Thickness(16, 1, 0, 0);
        rates.Children.Add(_perClick);
        center.Children.Add(rates);
        SetColumn(center, 1);
        grid.Children.Add(center);
        Detach(_studioName);
        _studioName.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(_studioName, 2);
        grid.Children.Add(_studioName);
        return Ui.Card(grid, 12);
    }

    private void BuildLeft()
    {
        _left.Children.Clear();
        // Combo.
        var combo = new StackPanel();
        combo.Children.Add(Heading(Loc.T("pf.combo")));
        var row = new DockPanel();
        Detach(_comboMult);
        DockPanel.SetDock(_comboMult, Dock.Right);
        _comboMult.VerticalAlignment = VerticalAlignment.Bottom;
        row.Children.Add(_comboMult);
        Detach(_comboText);
        row.Children.Add(_comboText);
        combo.Children.Add(row);
        _comboTrack.Children.Clear();
        var back = new Border { CornerRadius = new CornerRadius(4) };
        back.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        _comboTrack.Children.Add(back);
        _comboFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _comboTrack.Children.Add(Detach(_comboFill));
        combo.Children.Add(Detach(_comboTrack));
        combo.Children.Add(Detach(_comboNext));
        combo.Children.Add(Detach(_furyText));
        _left.Children.Add(Card(combo));

        // Abilities.
        var abilities = new StackPanel();
        abilities.Children.Add(Heading(Loc.T("pf.abilities")));
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        _abilityButtons.Clear();
        var index = 1;
        foreach (var def in _engine.Config.Abilities)
        {
            var label = new TextBlock { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
            var button = Ui.Button("SecondaryButton", label, (_, _) => UseAbility(def));
            button.Height = 52;
            button.Margin = new Thickness(0, 0, 6, 6);
            button.Padding = new Thickness(4);
            button.ToolTip = AbilityTooltip(def, index);
            AutomationProperties.SetName(button, Loc.T("pf.ab." + def.Id));
            grid.Children.Add(button);
            _abilityButtons.Add((def, button, label));
            index++;
        }
        abilities.Children.Add(grid);
        abilities.Children.Add(Info(Loc.T("pf.keys")));
        _left.Children.Add(Card(abilities));

        // Values.
        var stats = new StackPanel();
        stats.Children.Add(Heading(Loc.T("pf.values")));
        _stats.TextWrapping = TextWrapping.Wrap;
        _stats.TextTrimming = TextTrimming.None;
        _stats.LineHeight = 19;
        stats.Children.Add(Detach(_stats));
        _left.Children.Add(Card(stats));
    }

    private string AbilityTooltip(AbilityDef def, int key) =>
        $"{Loc.T("pf.ab." + def.Id)}  [{key}]\n{Loc.T("pf.ab." + def.Id + ".desc")}\n{Loc.F("pf.cooldown", (int)_engine.CooldownOf(def))}";

    private void BuildRight()
    {
        _right.Children.Clear();
        _right.RowDefinitions.Clear();
        _right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _right.RowDefinitions.Add(new RowDefinition());
        var tabs = new WrapPanel();
        tabs.Children.Add(Chip(Loc.T("pf.tab.producers"), _shopTab == "producers", () => { _shopTab = "producers"; BuildRight(); }));
        tabs.Children.Add(Chip(Loc.T("pf.tab.upgrades"), _shopTab == "upgrades", () => { _shopTab = "upgrades"; BuildRight(); }));
        _right.Children.Add(tabs);
        if (_shopTab == "producers")
        {
            var amounts = new WrapPanel();
            foreach (var mode in new[] { BuyMode.One, BuyMode.Ten, BuyMode.TwentyFive, BuyMode.Hundred, BuyMode.Max })
            {
                var chip = Chip(mode == BuyMode.Max ? Loc.T("pf.buy.max") : "×" + (int)mode, _buyMode == mode, () => { _buyMode = mode; BuildRight(); });
                chip.Height = 28;
                amounts.Children.Add(chip);
            }
            SetRow(amounts, 1);
            _right.Children.Add(amounts);
        }
        _shopList.Children.Clear();
        _producerRows.Clear();
        _upgradeRows.Clear();
        if (_shopTab == "producers") foreach (var def in _engine.Config.Producers) _shopList.Children.Add(ProducerRow(def));
        else BuildUpgradeList();
        var scroll = new ScrollViewer { Content = Detach(_shopList), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        SetRow(scroll, 2);
        _right.Children.Add(scroll);
        UpdateShop();
    }

    private UIElement ProducerRow(ProducerDef def)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var index = _engine.Config.Producers.IndexOf(def);
        var badge = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 10, 0), Background = Frozen(ProducerColor(index), 60) };
        badge.Child = new TextBlock { Text = (index + 1).ToString(Loc.Culture), FontWeight = FontWeights.Black, FontSize = 15, Foreground = Frozen(ProducerColor(index)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(badge);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Ui.Text(Loc.T("pf.prod." + def.Id), 13.5, bold: true));
        var cost = Ui.Text("", 12, "AccentBrush", bold: true);
        info.Children.Add(cost);
        var gain = Ui.Text("", 11, "TextSecondary");
        info.Children.Add(gain);
        var extra = Ui.Text("", 10.5, "TextMuted");
        info.Children.Add(extra);
        SetColumn(info, 1);
        grid.Children.Add(info);
        var count = Ui.Text("0", 22, bold: true);
        count.VerticalAlignment = VerticalAlignment.Center;
        count.Margin = new Thickness(8, 0, 0, 0);
        SetColumn(count, 2);
        grid.Children.Add(count);
        var row = new Border { Child = grid, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 4, 6), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
        row.ToolTip = Loc.T("pf.prod." + def.Id + ".desc");
        row.MouseLeftButtonUp += (_, _) => BuyProducer(def.Id);
        AutomationProperties.SetName(row, Loc.T("pf.prod." + def.Id));
        _producerRows.Add((def.Id, row, cost, gain, count, extra));
        return row;
    }

    private void BuyProducer(string id)
    {
        if (_engine.BuyProducer(id, _buyMode)) _audio?.Play(PunchSound.Buy);
        else _audio?.Play(PunchSound.Denied, 1, 0.5f);
        UpdateShop();
    }

    private void BuildUpgradeList()
    {
        foreach (var group in new[] { "click", "auto" })
        {
            _shopList.Children.Add(Heading(Loc.T("pf.upgrades." + group)));
            foreach (var def in _engine.Config.Upgrades.Where(u => u.Group == group))
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var info = new StackPanel();
                info.Children.Add(Ui.Text(Loc.T("pf.up." + def.Id), 13, bold: true));
                var cost = Ui.Text("", 11.5, "TextSecondary");
                cost.TextWrapping = TextWrapping.Wrap;
                cost.TextTrimming = TextTrimming.None;
                info.Children.Add(cost);
                grid.Children.Add(info);
                var buy = Ui.Button("ActionButton", Loc.T("pf.buy"), (_, _) =>
                {
                    if (_engine.BuyUpgrade(def.Id)) _audio?.Play(PunchSound.Upgrade);
                    else _audio?.Play(PunchSound.Denied, 1, 0.5f);
                    UpdateShop();
                });
                buy.Height = 32;
                buy.Padding = new Thickness(12, 0, 12, 0);
                buy.VerticalAlignment = VerticalAlignment.Center;
                AutomationProperties.SetName(buy, Loc.T("pf.buy") + " " + Loc.T("pf.up." + def.Id));
                SetColumn(buy, 1);
                grid.Children.Add(buy);
                var row = new Border { Child = grid, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 4, 6), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), ToolTip = Loc.T("pf.up." + def.Id + ".desc") };
                _shopList.Children.Add(row);
                _upgradeRows.Add((def, row, cost, buy));
            }
        }
    }

    private void BuildBottom()
    {
        _bottom.Children.Clear();
        _prestigeButton = BottomButton("pf.btn.career", ShowPrestige);
        _achievementButton = BottomButton("pf.btn.achievements", ShowAchievements);
        _bottom.Children.Add(_prestigeButton);
        _bottom.Children.Add(_achievementButton);
        _bottom.Children.Add(BottomButton("pf.btn.stats", ShowStatistics));
        _bottom.Children.Add(BottomButton("pf.btn.cosmetics", ShowCosmetics));
        _bottom.Children.Add(BottomButton("pf.btn.studios", ShowStudios));
        _bottom.Children.Add(BottomButton("pf.btn.settings", ShowSettings));
    }

    private Button BottomButton(string key, Action click)
    {
        var button = Ui.Button("SecondaryButton", Loc.T(key), (_, _) => { _audio?.Play(PunchSound.Buy, 1.5, 0.4f); click(); });
        button.Height = 38;
        button.Padding = new Thickness(16, 0, 16, 0);
        button.Margin = new Thickness(4, 0, 4, 4);
        AutomationProperties.SetName(button, Loc.T(key));
        return button;
    }

    // ───────────────────────── live updates ─────────────────────────

    private void UpdateTexts()
    {
        var save = _engine.Save;
        var stats = _engine.Stats;
        _punch.Text = Format(save.Punch);
        _perSecond.Text = Loc.F("pf.perSecond", Format(_engine.PpsNow));
        _perClick.Text = Loc.F("pf.perClick", Format(_engine.ClickValue));
        _studioName.Text = Loc.T("pf.env." + _engine.ActiveEnvironment.Id) + (_engine.ActiveEnvironment.Bonus > 0 ? $"  +{_engine.ActiveEnvironment.Bonus * 100:0} %" : "");
        var combo = (int)_engine.Combo;
        _comboText.Text = combo.ToString(Loc.Culture);
        _comboMult.Text = "×" + _engine.ComboMultiplier.ToString("0.#", Loc.Culture);
        int[] tiers = { 10, 25, 50, 100, 250 };
        var next = tiers.FirstOrDefault(t => t > combo);
        var previous = tiers.LastOrDefault(t => t <= combo);
        var share = next == 0 ? 1 : (combo - previous) / (double)(next - previous);
        _comboFill.Width = Math.Max(0, _comboTrack.ActualWidth * share);
        _comboNext.Text = next == 0 ? Loc.T("pf.combo.max") : Loc.F("pf.combo.next", next, next == 250 ? "FURY" : "×" + (next switch { 10 => 1.2, 25 => 1.5, 50 => 2.0, _ => 3.0 }).ToString("0.#", Loc.Culture));
        _furyText.Text = _engine.Fury ? Loc.F("pf.fury", Math.Ceiling(_engine.FuryLeft)) : "";
        _furyText.Foreground = FuryBrush;
        if (_engine.Fury) _comboFill.Background = FuryBrush;
        else _comboFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        var buffs = string.Join("\n", _engine.Buffs.Select(b => $"⚡ {Loc.T("pf.buff." + b.Id)} {Math.Ceiling(b.Remaining)} s"));
        _stats.Text = string.Join("\n", new[]
        {
            Loc.F("pf.stat.click", Format(_engine.ClickValue)),
            Loc.F("pf.stat.pps", Format(_engine.PpsNow)),
            Loc.F("pf.stat.crit", (_engine.CritChanceNow * 100).ToString("0.#", Loc.Culture), stats.CritMult.ToString("0.#", Loc.Culture)),
            Loc.F("pf.stat.combo", _engine.ComboMultiplier.ToString("0.#", Loc.Culture), stats.MaxCombo),
            Loc.F("pf.stat.training", (stats.TrainingSpeed * 100).ToString("0", Loc.Culture)),
            Loc.F("pf.stat.offline", stats.OfflineHours.ToString("0.#", Loc.Culture)),
            Loc.F("pf.stat.fame", save.FameEarned.ToString("N0", Loc.Culture), (save.FameEarned * _engine.Config.FameBonusPerPoint * 100).ToString("0", Loc.Culture)),
            Loc.F("pf.stat.mastery", _engine.MasteryPoints, (_engine.MasteryPoints * _engine.Config.MasteryBonusPerPoint * 100).ToString("0", Loc.Culture))
        }) + (buffs.Length > 0 ? "\n" + buffs : "");
        foreach (var (def, button, label) in _abilityButtons)
        {
            var unlocked = _engine.IsAbilityUnlocked(def);
            var cooldown = _engine.CooldownLeft(def.Id);
            var active = _engine.Buffs.Any(b => b.Id == def.Id);
            var key = _engine.Config.Abilities.IndexOf(def) + 1;
            label.Text = !unlocked ? $"🔒 {Loc.T("pf.ab." + def.Id)}\n{Format(def.UnlockEarned)}"
                : cooldown > 0 ? $"{Loc.T("pf.ab." + def.Id)}\n{Math.Ceiling(cooldown)} s"
                : $"{Loc.T("pf.ab." + def.Id)}\n[{key}]";
            button.IsEnabled = unlocked && cooldown <= 0;
            if (active) button.SetResourceReference(Control.BackgroundProperty, "AccentSoft");
            else button.ClearValue(Control.BackgroundProperty);
        }
        if (_prestigeButton is not null) _prestigeButton.Content = Loc.T("pf.btn.career") + (_engine.CanPrestige ? $"  (+{_engine.FameGain:N0} ★)" : "");
        if (_achievementButton is not null) _achievementButton.Content = $"{Loc.T("pf.btn.achievements")}  {_engine.Save.Achievements.Count}/{PunchContent.Achievements.Count}";
        Changed?.Invoke();
    }

    private void UpdateShop()
    {
        foreach (var (id, row, cost, gain, count, extra) in _producerRows)
        {
            var (amount, price) = _engine.Quote(id, _buyMode);
            var affordable = price <= _engine.Save.Punch;
            cost.Text = $"{Format(price)}  (×{amount})";
            gain.Text = Loc.F("pf.gain", Format(_engine.ProducerGain(id, amount)));
            count.Text = _engine.Save.Count(id).ToString("N0", Loc.Culture);
            var milestone = _engine.NextMilestone(id);
            extra.Text = milestone > 0 ? Loc.F("pf.milestone", milestone, milestone - _engine.Save.Count(id)) : Loc.T("pf.milestone.done");
            row.SetResourceReference(Border.BackgroundProperty, affordable ? "AccentSoft" : "SurfaceAlt");
            row.SetResourceReference(Border.BorderBrushProperty, affordable ? "AccentBrush" : "CardBorder");
            row.Opacity = affordable ? 1 : 0.8;
        }
        foreach (var (def, row, cost, buy) in _upgradeRows)
        {
            var unlocked = _engine.IsUnlocked(def);
            var maxed = _engine.IsMaxed(def);
            row.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
            if (!unlocked) continue;
            var level = _engine.Save.Level(def.Id);
            var price = _engine.UpgradeCost(def);
            var affordable = !maxed && price <= _engine.Save.Punch;
            cost.Text = $"{Loc.F("pf.level", level, def.MaxLevel)} · {EffectText(def)}" + (maxed ? $"\n{Loc.T("pf.maxed")}" : $"\n{Format(price)} · {PreviewText(def)}");
            buy.IsEnabled = affordable;
            row.SetResourceReference(Border.BackgroundProperty, affordable ? "AccentSoft" : "SurfaceAlt");
            row.SetResourceReference(Border.BorderBrushProperty, affordable ? "AccentBrush" : "CardBorder");
        }
    }

    private string EffectText(UpgradeDef def)
    {
        var value = def.Effect switch
        {
            global::ViennaAtomicClock.PunchFactory.Effect.CritChance or global::ViennaAtomicClock.PunchFactory.Effect.ClickPpsShare or global::ViennaAtomicClock.PunchFactory.Effect.ComboDecay or global::ViennaAtomicClock.PunchFactory.Effect.AbilityPower or global::ViennaAtomicClock.PunchFactory.Effect.PowerPunch or global::ViennaAtomicClock.PunchFactory.Effect.GoldenRewards or global::ViennaAtomicClock.PunchFactory.Effect.TrainerSynergy or global::ViennaAtomicClock.PunchFactory.Effect.TrainingSpeed or global::ViennaAtomicClock.PunchFactory.Effect.TypeSynergy => (def.Value * 100).ToString("0.#", Loc.Culture),
            _ => def.Value.ToString("0.##", Loc.Culture)
        };
        var target = def.Target is null ? "" : def.Effect == global::ViennaAtomicClock.PunchFactory.Effect.KindMult ? Loc.T("pf.kind." + def.Target) : string.Join(", ", def.Target.Split(',').Select(t => Loc.T("pf.prod." + t.Trim())));
        return Loc.F("pf.eff." + def.Effect, value, target);
    }

    /// <summary>What the next level brings, measured with the real formulas.</summary>
    private string PreviewText(UpgradeDef def)
    {
        var (before, after) = _engine.UpgradePreview(def);
        var parts = new List<string>();
        if (after.Pps > before.Pps) parts.Add(Loc.F("pf.gain", Format(after.Pps - before.Pps)));
        if (after.ClickBase > before.ClickBase) parts.Add(Loc.F("pf.gainClick", Format(after.ClickBase - before.ClickBase)));
        if (parts.Count == 0) return Loc.T("pf.preview.other");
        return string.Join(" · ", parts);
    }

    private void UpdateScene()
    {
        _scene.SetEnvironment(EnvironmentIndex());
        var equipped = _engine.Save.Equipped;
        _scene.SetCosmetics(PunchContent.Cosmetic(equipped[CosmeticSlot.Glove]), PunchContent.Cosmetic(equipped[CosmeticSlot.Bag]), PunchContent.Cosmetic(equipped[CosmeticSlot.Particles]),
            PunchContent.Cosmetic(equipped[CosmeticSlot.Marks]), PunchContent.Cosmetic(equipped[CosmeticSlot.Outfit]), PunchContent.Cosmetic(equipped[CosmeticSlot.Decor]));
    }

    private int EnvironmentIndex() => Math.Max(0, _engine.Config.Environments.FindIndex(e => e.Id == _engine.Save.Environment));

    private void ApplySettings()
    {
        _scene.ReducedMotion = _settings.ReducedMotion;
        _scene.ScreenShake = _settings.ScreenShake && !_settings.ReducedMotion;
        _scene.Flashes = _settings.Flashes && !_settings.ReducedMotion;
    }

    // ───────────────────────── floating numbers and toasts ─────────────────────────

    private void Floater(string text, Point at, Brush brush, double size)
    {
        if (!_settings.FloatingNumbers) return;
        var i = _nextFloater;
        _nextFloater = (_nextFloater + 1) % FloaterCount;
        var block = _floaterText[i];
        block.Text = text;
        block.Foreground = brush;
        block.FontSize = size;
        Canvas.SetLeft(block, at.X - 20);
        Canvas.SetTop(block, at.Y - 20);
        block.Opacity = 1;
        block.Visibility = Visibility.Visible;
        _floaterLife[i] = 0.9;
        _floaterSpeed[i] = 60 + Random.Shared.NextDouble() * 30;
    }

    private void UpdateFloaters(double seconds)
    {
        for (var i = 0; i < FloaterCount; i++)
        {
            if (_floaterLife[i] <= 0) continue;
            _floaterLife[i] -= seconds;
            var block = _floaterText[i];
            if (_floaterLife[i] <= 0)
            {
                block.Visibility = Visibility.Collapsed;
                continue;
            }
            Canvas.SetTop(block, Canvas.GetTop(block) - _floaterSpeed[i] * seconds);
            block.Opacity = Math.Min(1, _floaterLife[i] / 0.4);
        }
    }

    private void Toast(string text, Brush accent)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = accent };
        var card = new Border { Background = Frozen(Color.FromArgb(215, 16, 18, 24)), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 0, 6), Child = block };
        _toasts.Children.Insert(0, card);
        while (_toasts.Children.Count > 3) _toasts.Children.RemoveAt(_toasts.Children.Count - 1);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600)) { BeginTime = TimeSpan.FromSeconds(3.2) };
        fade.Completed += (_, _) => _toasts.Children.Remove(card);
        card.BeginAnimation(OpacityProperty, fade);
    }

    // ───────────────────────── overlays ─────────────────────────

    private void ShowOverlay(string title, UIElement content, double maxWidth = 760)
    {
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = Ui.Button("IconButton", "✕", (_, _) => CloseOverlay(), Loc.T("pf.close"));
        close.Width = 32;
        close.Height = 32;
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(Ui.Text(title, 20, bold: true));
        stack.Children.Add(head);
        stack.Children.Add(content);
        var card = Ui.Card(stack, 20);
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.4 };
        _overlayHost.MaxWidth = maxWidth;
        _overlayHost.Child = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, MaxHeight = Math.Max(300, ActualHeight - 40) };
        _overlay.Visibility = Visibility.Visible;
        Motion.PopIn(card);
    }

    private void CloseOverlay()
    {
        _overlay.Visibility = Visibility.Collapsed;
        _overlayHost.Child = null;
        Focus();
    }

    private void ShowOffline(OfflineReport report)
    {
        var stack = new StackPanel();
        if (report.ClockProblem) stack.Children.Add(Info(Loc.T("pf.offline.clock"), "OrangeBrush", 13));
        else
        {
            stack.Children.Add(Ui.Text(Loc.F("pf.offline.away", Duration(report.Away)), 14, "TextSecondary"));
            var big = Ui.Text("+" + Format(report.Gain), 32, "AccentBrush", bold: true);
            big.Margin = new Thickness(0, 8, 0, 4);
            stack.Children.Add(big);
            stack.Children.Add(Info(report.Capped ? Loc.F("pf.offline.capped", Duration(report.Counted)) : Loc.F("pf.offline.counted", Duration(report.Counted))));
        }
        stack.Children.Add(OkButton());
        ShowOverlay(Loc.T("pf.offline.title"), stack, 480);
        _audio?.Play(PunchSound.Offline);
    }

    private static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? Loc.F("pf.duration.hm", (int)span.TotalHours, span.Minutes) : Loc.F("pf.duration.m", Math.Max(0, (int)span.TotalMinutes));

    private Button OkButton()
    {
        var ok = Ui.Button("ActionButton", "OK", (_, _) => CloseOverlay());
        ok.Height = 38;
        ok.Padding = new Thickness(26, 0, 26, 0);
        ok.Margin = new Thickness(0, 14, 0, 0);
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        return ok;
    }

    private void ShowPrestige()
    {
        var stack = new StackPanel();
        var save = _engine.Save;
        stack.Children.Add(Info(Loc.T("pf.prestige.intro"), "TextSecondary", 13));
        var gain = _engine.FameGain;
        var big = Ui.Text(_engine.CanPrestige ? Loc.F("pf.prestige.gain", gain.ToString("N0", Loc.Culture)) : Loc.F("pf.prestige.need", Format(_engine.Config.PrestigeRequirement), Format(save.EarnedRun)), 18, _engine.CanPrestige ? "AccentBrush" : "TextPrimary", bold: true);
        big.TextWrapping = TextWrapping.Wrap;
        big.Margin = new Thickness(0, 6, 0, 6);
        stack.Children.Add(big);
        stack.Children.Add(Info(Loc.F("pf.prestige.bonus", ((save.FameEarned + gain) * _engine.Config.FameBonusPerPoint * 100).ToString("N0", Loc.Culture)), "TextSecondary", 12.5));
        stack.Children.Add(Info(Loc.T("pf.prestige.lose"), "OrangeBrush", 12.5));
        stack.Children.Add(Info(Loc.T("pf.prestige.keep"), "GreenBrush", 12.5));
        var reset = Ui.Button("ActionButton", Loc.T("pf.prestige.go"), (_, _) =>
        {
            if (!_engine.DoPrestige()) return;
            _audio?.Play(PunchSound.Prestige);
            Persist();
            CloseOverlay();
            BuildRight();
            Toast("★ " + Loc.T("pf.prestige.done"), Gold);
        });
        reset.Height = 40;
        reset.Padding = new Thickness(20, 0, 20, 0);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.IsEnabled = _engine.CanPrestige;
        stack.Children.Add(reset);

        stack.Children.Add(Heading(Loc.F("pf.prestige.shop", save.FameAvailable.ToString("N0", Loc.Culture))));
        foreach (var def in _engine.Config.PrestigeUpgrades)
        {
            var level = save.PrestigeLevel(def.Id);
            var maxed = level >= def.MaxLevel;
            var cost = _engine.PrestigeCost(def);
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var buy = Ui.Button("SecondaryButton", maxed ? Loc.T("pf.maxed") : $"{cost:N0} ★", (_, _) =>
            {
                if (_engine.BuyPrestigeUpgrade(def.Id)) { _audio?.Play(PunchSound.Upgrade); ShowPrestige(); }
                else _audio?.Play(PunchSound.Denied, 1, 0.5f);
            });
            buy.Height = 32;
            buy.Padding = new Thickness(12, 0, 12, 0);
            buy.IsEnabled = !maxed && cost <= save.FameAvailable;
            DockPanel.SetDock(buy, Dock.Right);
            row.Children.Add(buy);
            var text = new StackPanel();
            text.Children.Add(Ui.Text($"{Loc.T("pf.pr." + def.Id)}  ({level}/{def.MaxLevel})", 13, bold: true));
            var desc = Ui.Text(Loc.T("pf.pr." + def.Id + ".desc"), 11.5, "TextMuted");
            desc.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(desc);
            row.Children.Add(text);
            stack.Children.Add(row);
        }
        ShowOverlay(Loc.T("pf.btn.career"), stack);
    }

    internal string AchievementTitle(AchievementDef def)
    {
        var key = "pf.ach." + def.Id;
        if (PunchFactoryStrings.Table.ContainsKey(key)) return Loc.T(key);
        var amount = def.Goal is Goal.Earned or Goal.Pps ? Format(def.Threshold) : def.Threshold.ToString("N0", Loc.Culture);
        return Loc.F("pf.goal." + def.Goal, amount, def.Target is null ? "" : Loc.T("pf.prod." + def.Target));
    }

    private void ShowAchievements()
    {
        var grid = new WrapPanel();
        foreach (var def in PunchContent.Achievements)
        {
            var done = _engine.Save.Achievements.Contains(def.Id);
            var stack = new StackPanel();
            stack.Children.Add(Ui.Text((done ? "🏆 " : "🔒 ") + AchievementTitle(def), 12.5, done ? "TextPrimary" : "TextMuted", bold: true));
            var progress = Math.Min(1, _engine.Progress(def) / def.Threshold);
            var track = new Grid { Height = 5, Margin = new Thickness(0, 6, 0, 2) };
            var back = new Border { CornerRadius = new CornerRadius(3) };
            back.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
            var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 196 * progress };
            fill.SetResourceReference(Border.BackgroundProperty, done ? "GreenBrush" : "AccentBrush");
            track.Children.Add(back);
            track.Children.Add(fill);
            stack.Children.Add(track);
            var reward = Loc.T("pf.ach.reward") + (def.Cosmetic is null ? "" : " + " + Loc.T("pf.cos." + def.Cosmetic));
            stack.Children.Add(Ui.Text(reward, 10.5, "TextMuted"));
            var card = new Border { Width = 220, Padding = new Thickness(10), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(10), Child = stack, Opacity = done ? 1 : 0.8 };
            card.SetResourceReference(Border.BackgroundProperty, done ? "AccentSoft" : "SurfaceAlt");
            grid.Children.Add(card);
        }
        var content = new StackPanel();
        content.Children.Add(Info(Loc.F("pf.ach.summary", _engine.Save.Achievements.Count, PunchContent.Achievements.Count, _engine.Save.Achievements.Count), "TextSecondary", 13));
        content.Children.Add(grid);
        ShowOverlay(Loc.T("pf.btn.achievements"), content, 980);
    }

    private void ShowStatistics()
    {
        var s = _engine.Save;
        var lines = new[]
        {
            (Loc.T("pf.st.earnedAll"), Format(s.EarnedAll)), (Loc.T("pf.st.earnedRun"), Format(s.EarnedRun)), (Loc.T("pf.st.bestPps"), Format(s.BestPps)),
            (Loc.T("pf.st.clicks"), s.Clicks.ToString("N0", Loc.Culture)), (Loc.T("pf.st.crits"), s.Crits.ToString("N0", Loc.Culture)), (Loc.T("pf.st.maxCombo"), s.MaxCombo.ToString("N0", Loc.Culture)),
            (Loc.T("pf.st.fury"), s.FuryCount.ToString("N0", Loc.Culture)), (Loc.T("pf.st.abilities"), s.AbilitiesUsed.ToString("N0", Loc.Culture)), (Loc.T("pf.st.events"), s.EventsCaught.ToString("N0", Loc.Culture)),
            (Loc.T("pf.st.upgrades"), s.UpgradesBought.ToString("N0", Loc.Culture)), (Loc.T("pf.st.producers"), s.Producers.Values.Sum().ToString("N0", Loc.Culture)),
            (Loc.T("pf.st.prestiges"), s.Prestiges.ToString("N0", Loc.Culture)), (Loc.T("pf.st.fame"), s.FameEarned.ToString("N0", Loc.Culture)),
            (Loc.T("pf.st.playtime"), Duration(TimeSpan.FromSeconds(s.PlaySeconds))), (Loc.T("pf.st.since"), s.CreatedUtc.ToLocalTime().ToString("d", Loc.Culture))
        };
        var stack = new StackPanel();
        foreach (var (label, value) in lines)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var v = Ui.Text(value, 13, bold: true);
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(v);
            row.Children.Add(Ui.Text(label, 13, "TextSecondary"));
            stack.Children.Add(row);
        }
        ShowOverlay(Loc.T("pf.btn.stats"), stack, 520);
    }

    private void ShowCosmetics()
    {
        var stack = new StackPanel();
        foreach (var slot in Enum.GetValues<CosmeticSlot>())
        {
            stack.Children.Add(Heading(Loc.T("pf.slot." + slot)));
            var row = new WrapPanel();
            foreach (var cosmetic in PunchContent.Cosmetics.Where(c => c.Slot == slot))
            {
                var owned = _engine.Save.Cosmetics.Contains(cosmetic.Id);
                var equipped = _engine.Save.Equipped[slot] == cosmetic.Id;
                var swatch = new StackPanel { Orientation = Orientation.Horizontal };
                swatch.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Frozen(cosmetic.Primary), BorderBrush = Frozen(cosmetic.Secondary), BorderThickness = new Thickness(3), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                swatch.Children.Add(new TextBlock { Text = (owned ? "" : "🔒 ") + Loc.T("pf.cos." + cosmetic.Id), VerticalAlignment = VerticalAlignment.Center });
                var chip = Ui.Button("ChipButton", swatch, (_, _) =>
                {
                    if (!owned) return;
                    _engine.Equip(cosmetic.Id);
                    UpdateScene();
                    ShowCosmetics();
                });
                chip.Height = 34;
                chip.Margin = new Thickness(0, 0, 6, 6);
                chip.Padding = new Thickness(10, 0, 12, 0);
                chip.IsEnabled = owned;
                chip.ToolTip = owned ? null : UnlockHint(cosmetic);
                Ui.Select(chip, equipped);
                AutomationProperties.SetName(chip, Loc.T("pf.cos." + cosmetic.Id));
                row.Children.Add(chip);
            }
            stack.Children.Add(row);
        }
        stack.Children.Add(Info(Loc.T("pf.cos.note")));
        ShowOverlay(Loc.T("pf.btn.cosmetics"), stack);
    }

    private string UnlockHint(CosmeticDef cosmetic)
    {
        var achievement = PunchContent.Achievements.FirstOrDefault(a => a.Cosmetic == cosmetic.Id || a.Id == cosmetic.Unlock);
        if (achievement is not null) return Loc.F("pf.cos.unlock", AchievementTitle(achievement));
        var prestige = _engine.Config.PrestigeUpgrades.FirstOrDefault(p => p.Cosmetic == cosmetic.Id);
        return prestige is not null ? Loc.F("pf.cos.unlockFame", Loc.T("pf.pr." + prestige.Id)) : "";
    }

    private void ShowStudios()
    {
        var stack = new StackPanel();
        foreach (var def in _engine.Config.Environments)
        {
            var unlocked = _engine.IsEnvironmentUnlocked(def);
            var active = def.Id == _engine.Save.Environment;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var button = Ui.Button(active ? "ActionButton" : "SecondaryButton", active ? Loc.T("pf.studio.active") : unlocked ? Loc.T("pf.studio.go") : "🔒", (_, _) =>
            {
                _engine.SelectEnvironment(def.Id);
                _audio?.SetEnvironment(EnvironmentIndex());
                UpdateScene();
                ShowStudios();
            });
            button.Height = 32;
            button.Padding = new Thickness(14, 0, 14, 0);
            button.IsEnabled = unlocked && !active;
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            var text = new StackPanel();
            text.Children.Add(Ui.Text(Loc.T("pf.env." + def.Id) + (def.Bonus > 0 ? $"  · +{def.Bonus * 100:0} %" : ""), 14, unlocked ? "TextPrimary" : "TextMuted", bold: true));
            text.Children.Add(Ui.Text(unlocked ? Loc.T("pf.env." + def.Id + ".desc") : Loc.F("pf.studio.unlock", Format(def.UnlockEarned)), 11.5, "TextMuted"));
            row.Children.Add(text);
            stack.Children.Add(row);
        }
        ShowOverlay(Loc.T("pf.btn.studios"), stack, 620);
    }

    private void ShowSettings()
    {
        var stack = new StackPanel();
        void Volume(string key, double value, Action<double> set)
        {
            stack.Children.Add(Heading(Loc.T(key)));
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = value * 100, SmallChange = 5, LargeChange = 10, IsMoveToPointEnabled = true };
            AutomationProperties.SetName(slider, Loc.T(key));
            slider.ValueChanged += (_, _) => { set(slider.Value / 100); _storage.SaveSettings(_settings); };
            stack.Children.Add(slider);
        }
        Volume("pf.set.master", _settings.MasterVolume, v => _settings.MasterVolume = v);
        Volume("pf.set.music", _settings.MusicVolume, v => _settings.MusicVolume = v);
        Volume("pf.set.effects", _settings.EffectsVolume, v => _settings.EffectsVolume = v);
        stack.Children.Add(Heading(Loc.T("pf.set.display")));
        void Toggle(string key, bool value, Action<bool> set)
        {
            var chip = Ui.Button("ChipButton", (value ? "☑  " : "☐  ") + Loc.T(key), (_, _) => { set(!value); _storage.SaveSettings(_settings); ApplySettings(); ShowSettings(); });
            chip.Height = 34;
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.Padding = new Thickness(12, 0, 12, 0);
            chip.HorizontalAlignment = HorizontalAlignment.Left;
            Ui.Select(chip, value);
            AutomationProperties.SetName(chip, Loc.T(key));
            stack.Children.Add(chip);
        }
        Toggle("pf.set.shake", _settings.ScreenShake, v => _settings.ScreenShake = v);
        Toggle("pf.set.flashes", _settings.Flashes, v => _settings.Flashes = v);
        Toggle("pf.set.reduced", _settings.ReducedMotion, v => _settings.ReducedMotion = v);
        Toggle("pf.set.numbers", _settings.FloatingNumbers, v => _settings.FloatingNumbers = v);
        Toggle("pf.set.vibration", _settings.Vibration, v => _settings.Vibration = v);
        if (_audio is { Available: false }) stack.Children.Add(Info(Loc.T("pf.set.noAudio"), "OrangeBrush"));

        stack.Children.Add(Heading(Loc.T("pf.set.save")));
        var (codeHost, codeBox) = Ui.Input(Loc.T("pf.set.importHint"), multiline: true, fontSize: 11);
        codeHost.Height = 70;
        stack.Children.Add(codeHost);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var export = Ui.Button("SecondaryButton", Loc.T("pf.set.export"), (_, _) =>
        {
            Persist();
            var code = PunchStorage.Export(_engine.Save);
            codeBox.Text = code;
            try { Clipboard.SetText(code); } catch (System.Runtime.InteropServices.ExternalException) { }
            Toast(Loc.T("pf.set.exported"), Gold);
        });
        var import = Ui.Button("SecondaryButton", Loc.T("pf.set.import"), (_, _) =>
        {
            if (PunchStorage.TryImport(codeBox.Text, out var imported))
            {
                _engine.Replace(imported);
                Persist();
                UpdateScene();
                BuildRight();
                CloseOverlay();
                Toast(Loc.T("pf.set.imported"), Gold);
            }
            else Toast(Loc.T("pf.set.importFailed"), FuryBrush);
        });
        var reset = Ui.Button("SecondaryButton", Loc.T("pf.set.reset"), (_, _) => ConfirmReset());
        reset.Foreground = FuryBrush;
        foreach (var b in new[] { export, import, reset })
        {
            b.Height = 34;
            b.Padding = new Thickness(14, 0, 14, 0);
            b.Margin = new Thickness(0, 0, 8, 6);
            buttons.Children.Add(b);
        }
        stack.Children.Add(buttons);
        stack.Children.Add(Info(Loc.F("pf.set.files", _storage.Folder)));
        stack.Children.Add(Info(Loc.F("pf.set.balance", BalanceConfig.DefaultPath)));
        ShowOverlay(Loc.T("pf.btn.settings"), stack, 560);
    }

    /// <summary>Full reset needs two confirmations.</summary>
    private void ConfirmReset(int step = 1)
    {
        var stack = new StackPanel();
        var text = Ui.Text(Loc.T(step == 1 ? "pf.reset.first" : "pf.reset.second"), 14, "OrangeBrush", bold: true);
        text.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = Ui.Button("SecondaryButton", Loc.T("pf.cancel"), (_, _) => CloseOverlay());
        var go = Ui.Button("ActionButton", Loc.T(step == 1 ? "pf.reset.continue" : "pf.reset.final"), (_, _) =>
        {
            if (step == 1)
            {
                ConfirmReset(2);
                return;
            }
            _storage.DeleteProgress();
            _engine.Replace(new PunchSave().Normalize());
            Persist();
            UpdateScene();
            BuildRight();
            CloseOverlay();
        });
        go.Background = FuryBrush;
        foreach (var b in new[] { cancel, go })
        {
            b.Height = 36;
            b.Padding = new Thickness(16, 0, 16, 0);
            b.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(b);
        }
        stack.Children.Add(buttons);
        ShowOverlay(Loc.T("pf.set.reset"), stack, 480);
    }

    // ───────────────────────── small builders ─────────────────────────

    private static Border Card(UIElement content)
    {
        var card = Ui.Card(content, 14);
        card.Margin = new Thickness(0, 0, 0, 10);
        return card;
    }

    private static TextBlock Heading(string text)
    {
        var block = Ui.Text(text.ToUpper(Loc.Culture), 10.5, "TextMuted", bold: true);
        block.Margin = new Thickness(0, 6, 0, 6);
        return block;
    }

    private static TextBlock Info(string text, string brush = "TextMuted", double size = 11.5)
    {
        var block = Ui.Text(text, size, brush);
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        block.Margin = new Thickness(0, 4, 0, 6);
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
        return chip;
    }

    private static Color ProducerColor(int index) => index switch
    {
        0 => Color.FromRgb(0x3B, 0x82, 0xF6), 1 => Color.FromRgb(0x14, 0xB8, 0x8A), 2 => Color.FromRgb(0xF5, 0x9E, 0x0B), 3 => Color.FromRgb(0xEF, 0x44, 0x6F),
        4 => Color.FromRgb(0x8B, 0x5C, 0xF6), 5 => Color.FromRgb(0x06, 0xB6, 0xD4), 6 => Color.FromRgb(0x22, 0xC5, 0x5E), 7 => Color.FromRgb(0xF9, 0x73, 0x16),
        8 => Color.FromRgb(0xE1, 0x1D, 0x48), 9 => Color.FromRgb(0x38, 0xBD, 0xF8), _ => Color.FromRgb(0xA7, 0x8B, 0xFA)
    };

    private static Brush Frozen(Color color, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
