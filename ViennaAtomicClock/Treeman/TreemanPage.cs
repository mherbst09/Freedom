using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ViennaAtomicClock.Treeman;

/// <summary>
/// Treeman – a fast reaction arcade game: chop the endless tree from the left or right and dodge the branches
/// before the time runs out. The page wires the systems together: GameManager (rules), InputController,
/// TreemanView (drawing), UIManager (menus/HUD) and AudioManager. The game loop only runs while the page is visible.
/// </summary>
internal sealed class TreemanPage : Grid
{
    private readonly GameManager _game;
    private readonly InputController _input;
    private readonly TreemanView _view;
    private readonly UIManager _ui;
    private readonly ISaveSystem _save;
    private AudioManager? _audio;
    private TimeSpan _lastFrame;
    private bool _looping;
    private int _lastCountdownSecond = -1;
    private SceneStyle? _nextScene;

    /// <summary>Random source of the map shuffle (replaceable in tests).</summary>
    internal Random SceneRandom { get; set; } = new();

    /// <summary>"Beenden" in the main menu leaves Treeman.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised when the header line of the main window should change.</summary>
    public event Action? Changed;

    public TreemanPage() : this(TreemanConfig.Load(), new JsonSaveSystem()) { }

    public TreemanPage(TreemanConfig config, ISaveSystem save)
    {
        _save = save;
        _game = new GameManager(config, save);
        _input = new InputController(config.Player);
        _view = new TreemanView(_game) { ReducedMotion = !SystemParameters.ClientAreaAnimation, Skin = SkinCatalog.Get(save.Data.EquippedSkin) };
        _ui = new UIManager(_game, save, Play, Leave, () => _audio);
        _ui.SetScene(_view.Scene);
        _ui.SkinChanged += () =>
        {
            _view.Skin = SkinCatalog.Get(_save.Data.EquippedSkin);
            _view.InvalidateVisual();
        };
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Children.Add(_view);
        Children.Add(_ui.Root);
        AutomationProperties.SetName(this, "Treeman");

        _game.StateChanged += (_, state) =>
        {
            _audio?.SetMusic(state is GameState.Countdown or GameState.Playing);
            if (state is GameState.GameOver or GameState.MainMenu or GameState.Paused) _input.Clear();
            _lastCountdownSecond = -1;
            StartLoop();
            Changed?.Invoke();
        };
        _game.Chopped += chop =>
        {
            _audio?.Play(SoundEffect.Chop, 0.85f + (float)(chop.Score % 3) * 0.05f);
            if (chop.Milestone) Changed?.Invoke();
        };
        _game.Player.SideChanged += _ => _audio?.Play(SoundEffect.SideSwitch, 0.6f);
        _game.RoundEnded += result =>
        {
            // A new map and time of day is drawn after every death; it appears with the next round.
            _nextScene = SceneStyle.Shuffle(SceneRandom, _view.Scene);
            _audio?.Play(SoundEffect.GameOver);
            if (result.NewHighscore) _audio?.Play(SoundEffect.Highscore, 0.9f);
            Changed?.Invoke();
        };

        _game.RoundReset += () =>
        {
            if (_nextScene is null) return;
            _view.Scene = _nextScene;
            _nextScene = null;
            _ui.SetScene(_view.Scene);
        };

        PreviewKeyDown += OnKeyDown;
        MouseDown += (_, _) => Focus();
        IsVisibleChanged += (_, _) => OnVisibilityChanged();
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window) window.Deactivated += (_, _) => _game.Pause(); // never lose a round in the background
        };
        Unloaded += (_, _) => StopLoop();
    }

    public GameManager Game => _game;
    internal TreemanView View => _view;
    internal UIManager Ui => _ui;

    public string StatusLine => _game.State switch
    {
        GameState.Playing or GameState.Countdown => Loc.F("tm.header.playing", _game.Score),
        GameState.Paused => Loc.T("tm.paused"),
        _ => Loc.F("tm.header.best", _game.Highscore)
    };

    private void Play(SoundEffect effect) => _audio?.Play(effect);

    private void Leave()
    {
        _game.ShowMainMenu();
        CloseRequested?.Invoke();
    }

    private void OnVisibilityChanged()
    {
        if (IsVisible)
        {
            if (_audio is null)
            {
                _audio = new AudioManager(_save.Data);
                _audio.Start();
            }
            _audio.SetMusic(_game.State is GameState.Countdown or GameState.Playing);
            _ui.Refresh();
            StartLoop();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => Focus());
        }
        else
        {
            _game.Pause();
            _audio?.SetMusic(false);
            StopLoop();
        }
    }

    /// <summary>Stops sound and the loop when Freedom closes.</summary>
    public void Shutdown()
    {
        _ui.Detach();
        StopLoop();
        _save.Save();
        _audio?.Dispose();
        _audio = null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e) => HandleKey(e);

    /// <summary>Game keys. MainWindow forwards them too, so they work wherever the keyboard focus is.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Handled || !IsVisible) return;
        if (e.OriginalSource is Slider or TextBox) return; // arrow keys move the volume sliders in the options
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_ui.OptionsOpen)
        {
            if (_ui.HandleSubPanelKey(key)) e.Handled = true;
            StartLoop();
            return;
        }
        if (InputController.Map(key) is not { } action) return;
        if (e.IsRepeat && action is InputAction.Left or InputAction.Right) { e.Handled = true; return; } // holding a key is not chopping
        if (action == InputAction.Confirm && _game.State is GameState.MainMenu or GameState.GameOver or GameState.Paused) Play(SoundEffect.MenuSelect);
        _input.Press(action, _game);
        e.Handled = true;
        StartLoop();
    }

    // ───────────────────────── game loop ─────────────────────────

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

    /// <summary>One frame: input buffer, rules, effects, HUD. Stops by itself when nothing moves.</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args || args.RenderingTime == _lastFrame) return;
        var seconds = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.1, (args.RenderingTime - _lastFrame).TotalSeconds);
        _lastFrame = args.RenderingTime;
        Step(seconds);
        if (_game.State is not (GameState.Countdown or GameState.Playing) && !_view.IsAnimating && !_ui.IsAnimating && !_view.HasAmbientMotion) StopLoop();
    }

    /// <summary>Advances everything by <paramref name="seconds"/> (also used by the tests).</summary>
    internal void Step(double seconds)
    {
        _input.Update(seconds, _game);
        _game.Tick(seconds);
        if (_game.State == GameState.Countdown)
        {
            var second = (int)Math.Ceiling(_game.CountdownLeft);
            if (second != _lastCountdownSecond)
            {
                _lastCountdownSecond = second;
                _audio?.Play(SoundEffect.Countdown);
            }
        }
        _view.Advance(seconds);
        _ui.UpdateFrame(seconds);
    }
}

/// <summary>
/// Menus and HUD on top of the game view, in Treeman's own wooden arcade look (see <see cref="GameUi"/>): score,
/// time bar and badges while playing; main menu, pause, round summary, options, boxes and skins as wooden boards.
/// Reacts to GameManager events only.
/// </summary>
internal sealed class UIManager
{
    private static readonly Brush Vignette = GameUi.Freeze(new RadialGradientBrush(Color.FromArgb(70, 10, 6, 2), Color.FromArgb(185, 10, 6, 2)) { RadiusX = 0.75, RadiusY = 0.75 });
    private static readonly Brush ScoreFill = GameUi.Cream;
    private static readonly Brush StampRed = GameUi.Solid(Color.FromRgb(0xE0, 0x3A, 0x2E));

    private readonly GameManager _game;
    private readonly ISaveSystem _save;
    private readonly Action<SoundEffect> _sound;
    private readonly Action _leave;
    private readonly Func<AudioManager?> _audio;

    private readonly Grid _hud = new() { IsHitTestVisible = false };
    private readonly GameText _score = new("0", 76) { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ScaleTransform _scorePop = new(1, 1);
    private readonly TextBlock _best = new();
    private readonly TextBlock _level = new();
    private readonly TextBlock _coins = new();
    private readonly TextBlock _map = new();
    private readonly GameIcon _mapIcon = new(GameIconKind.Tree);
    private readonly GameIcon _timeIcon = new(GameIconKind.Sun) { Margin = new Thickness(2, 0, 0, 0) };
    private readonly LootboxSystem _boxes;
    private readonly LootboxPanel _loot;
    private readonly TimeBar _bar = new() { Width = 500, Height = 34, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly GameText _countdown = new("3", 170, GameUi.TitleFill, 10) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScaleTransform _countdownPop = new(1, 1);
    private readonly Grid _overlay = new();
    private readonly Border _dim = new() { Background = Vignette };
    private readonly Border _panelHost = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) };
    private double _pulse;
    private double _pop;
    private double _countPop;
    private string _countText = "";
    private string _shownPanel = "";
    private bool _optionsOpen;

    public UIManager(GameManager game, ISaveSystem save, Action<SoundEffect> sound, Action leave, Func<AudioManager?> audio)
    {
        _game = game;
        _save = save;
        _sound = sound;
        _leave = leave;
        _audio = audio;
        Root = new Grid();
        _boxes = new LootboxSystem(game.Config.Loot, save);
        _loot = new LootboxPanel(_boxes, save, game.Config.Loot, sound, () =>
        {
            UpdateTexts();
            SkinChanged?.Invoke();
        }, CloseOptions);

        // HUD: big score, time bar, badges.
        var top = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        _score.RenderTransformOrigin = new Point(0.5, 0.5);
        _score.RenderTransform = _scorePop;
        AutomationProperties.SetLiveSetting(_score, AutomationLiveSetting.Off);
        top.Children.Add(_score);
        top.Children.Add(_bar);
        _hud.Children.Add(top);
        var corner = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16) };
        corner.Children.Add(GameUi.Badge(new GameIcon(GameIconKind.Trophy), _best));
        corner.Children.Add(GameUi.Badge(new GameIcon(GameIconKind.Level), _level));
        corner.Children.Add(GameUi.Badge(new GameIcon(GameIconKind.Coin), _coins));
        _hud.Children.Add(corner);
        var mapBadge = GameUi.Badge(new StackPanel { Orientation = Orientation.Horizontal, Children = { _mapIcon, _timeIcon } }, _map);
        mapBadge.HorizontalAlignment = HorizontalAlignment.Right;
        mapBadge.VerticalAlignment = VerticalAlignment.Top;
        mapBadge.Margin = new Thickness(16);
        mapBadge.IsHitTestVisible = false;
        Root.Children.Add(mapBadge);
        Root.Children.Add(_hud);
        _countdown.RenderTransformOrigin = new Point(0.5, 0.5);
        _countdown.RenderTransform = _countdownPop;
        Root.Children.Add(_countdown);

        _overlay.Children.Add(_dim);
        _overlay.Children.Add(_panelHost);
        Root.Children.Add(_overlay);

        game.StateChanged += (_, _) => Refresh();
        game.Chopped += chop =>
        {
            _score.Text = chop.Score.ToString(Loc.Culture);
            if (chop.Milestone) _pop = 1;
        };
        game.LevelChanged += _ => UpdateTexts();
        game.RoundReset += () => { _pop = 0; UpdateTexts(); };
        Loc.Changed += OnLanguageChanged;
        Refresh();
    }

    private void OnLanguageChanged()
    {
        _shownPanel = "";
        Refresh();
    }

    /// <summary>Stops listening to language changes (the page is going away).</summary>
    public void Detach() => Loc.Changed -= OnLanguageChanged;

    public Grid Root { get; }
    /// <summary>True while options, the box shop or the skin collection cover the game panels.</summary>
    public bool OptionsOpen => _optionsOpen;
    public bool IsAnimating => _pop > 0 || _countPop > 0 || _loot.IsAnimating;
    /// <summary>Name of the panel on screen ("menu", "pause", "gameover", "options", "shop", "skins" or "").</summary>
    public string ShownPanel => _shownPanel;
    internal LootboxPanel Loot => _loot;
    internal LootboxSystem Boxes => _boxes;

    /// <summary>The equipped skin changed (box or collection).</summary>
    public event Action? SkinChanged;

    private SceneStyle? _scene;

    /// <summary>Shows the current map and time of day in the top right corner.</summary>
    public void SetScene(SceneStyle scene)
    {
        _scene = scene;
        _map.Text = Loc.T("tm.map." + scene.Theme.ToString().ToLowerInvariant()) + " · " + Loc.T("tm.time." + scene.Time.ToString().ToLowerInvariant());
        _mapIcon.Kind = scene.Theme switch { MapTheme.Christmas => GameIconKind.Pine, MapTheme.Halloween => GameIconKind.Pumpkin, MapTheme.Easter => GameIconKind.Egg, _ => GameIconKind.Tree };
        _timeIcon.Kind = scene.Time switch { DayTime.Sunset => GameIconKind.Sunset, DayTime.Night => GameIconKind.Moon, _ => GameIconKind.Sun };
    }

    /// <summary>Keys while a sub panel is open: Esc closes it, Space/Enter skip the box reel.</summary>
    public bool HandleSubPanelKey(Key key)
    {
        if (_loot.IsSpinning && key is Key.Space or Key.Enter or Key.Escape)
        {
            _loot.Skip();
            return true;
        }
        if (key != Key.Escape) return false;
        CloseOptions();
        return true;
    }

    private void UpdateTexts()
    {
        _score.Text = _game.Score.ToString(Loc.Culture);
        _best.Text = _game.Highscore.ToString("N0", Loc.Culture);
        _level.Text = Loc.F("tm.hud.level", _game.Level + 1);
        _coins.Text = _save.Data.Coins.ToString("N0", Loc.Culture);
        AutomationProperties.SetName(_best, Loc.F("tm.hud.best", _game.Highscore));
        AutomationProperties.SetName(_coins, Loc.F("tm.coins", _save.Data.Coins));
        if (_scene is not null) SetScene(_scene);
    }

    /// <summary>Shows the panel that belongs to the current state.</summary>
    public void Refresh()
    {
        UpdateTexts();
        var playing = _game.State is GameState.Playing or GameState.Countdown or GameState.Paused;
        _hud.Visibility = playing || _game.State == GameState.GameOver ? Visibility.Visible : Visibility.Collapsed;
        _countdown.Visibility = _game.State == GameState.Countdown ? Visibility.Visible : Visibility.Collapsed;
        if (_optionsOpen) return;
        var panel = _game.State switch
        {
            GameState.MainMenu => "menu",
            GameState.Paused => "pause",
            GameState.GameOver => "gameover",
            _ => ""
        };
        if (panel == _shownPanel && panel != "gameover") return;
        _shownPanel = panel;
        _overlay.Visibility = panel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Show(panel switch
        {
            "menu" => MainMenu(),
            "pause" => PauseMenu(),
            "gameover" => GameOverPanel(),
            _ => null
        });
    }

    private void Show(FrameworkElement? panel)
    {
        if (panel is null)
        {
            _panelHost.Child = null;
            return;
        }
        // Boards keep their size and only shrink when the window is too small.
        var fit = new Viewbox { Child = panel, StretchDirection = StretchDirection.DownOnly };
        _panelHost.Child = fit;
        Motion.PopIn(fit);
    }

    /// <summary>Per-frame HUD work: time bar, countdown number, score pop.</summary>
    public void UpdateFrame(double seconds)
    {
        _bar.Share = _game.Time.Share;
        _bar.WarnShare = _game.Config.Time.LowWarningShare;
        if (_game.Time.IsLow && _game.State == GameState.Playing)
        {
            _pulse += seconds * 9;
            _bar.Pulse = 0.5 + 0.5 * Math.Sin(_pulse);
        }
        else _bar.Pulse = 0;
        _bar.InvalidateVisual();
        if (_game.State == GameState.Countdown)
        {
            var text = Math.Ceiling(_game.CountdownLeft).ToString(Loc.Culture);
            if (_countText != text)
            {
                _countText = text;
                _countdown.Text = text;
                _countPop = 1;
            }
        }
        if (_countPop > 0)
        {
            _countPop = Math.Max(0, _countPop - seconds / 0.3);
            _countdownPop.ScaleX = _countdownPop.ScaleY = 1 + 0.6 * _countPop * _countPop;
            _countdown.Opacity = 1 - 0.3 * _countPop;
        }
        _loot.Update(seconds);
        if (_pop > 0)
        {
            _pop = Math.Max(0, _pop - seconds / 0.35);
            var scale = 1 + 0.35 * Math.Sin(_pop * Math.PI);
            _scorePop.ScaleX = _scorePop.ScaleY = scale;
        }
    }

    // ───────────────────────── panels ─────────────────────────

    private GameButton MenuButton(string key, GameButtonColor color, Action click, GameIconKind? icon = null, double height = 54) =>
        GameUi.Button(Loc.T(key), color, click, _sound, icon, height);

    /// <summary>"TREEMAN" logo with two axes, tilted like a sign.</summary>
    private static FrameworkElement Logo()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        var left = new GameIcon(GameIconKind.Axe) { Width = 48, Height = 48, VerticalAlignment = VerticalAlignment.Center };
        var right = new GameIcon(GameIconKind.Axe) { Width = 48, Height = 48, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(-1, 1) };
        row.Children.Add(left);
        row.Children.Add(new GameText("TREEMAN", 64, GameUi.TitleFill, 9) { Margin = new Thickness(6, 0, 6, 0) });
        row.Children.Add(right);
        row.RenderTransformOrigin = new Point(0.5, 0.5);
        row.RenderTransform = new RotateTransform(-3);
        return row;
    }

    private FrameworkElement MainMenu()
    {
        var stack = new StackPanel();
        stack.Children.Add(Logo());
        var tagline = GameUi.Label(Loc.T("tm.tagline"), 14.5, GameUi.CreamDim);
        tagline.Margin = new Thickness(0, 0, 0, 14);
        stack.Children.Add(tagline);
        var stats = new UniformGrid3();
        stats.Add(GameUi.Chip(GameIconKind.Trophy, _game.Highscore.ToString("N0", Loc.Culture), Loc.T("tm.stat.best")));
        stats.Add(GameUi.Chip(GameIconKind.Coin, _save.Data.Coins.ToString("N0", Loc.Culture), Loc.T("tm.stat.coins")));
        stats.Add(GameUi.Chip(GameIconKind.Shirt, $"{_save.Data.OwnedSkins.Count}/{SkinCatalog.All.Count}", SkinCatalog.Get(_save.Data.EquippedSkin).Name));
        stats.Panel.Margin = new Thickness(-4, 0, -4, 16);
        stack.Children.Add(stats.Panel);
        stack.Children.Add(MenuButton("tm.play", GameButtonColor.Green, () => _game.StartRound(), GameIconKind.Axe, 66));
        stack.Children.Add(ShopRow());
        stack.Children.Add(GameUi.Row(MenuButton("tm.options", GameButtonColor.Wood, () => OpenOptions()), MenuButton("tm.quit", GameButtonColor.Red, _leave)));
        var help = GameUi.Inset(GameUi.Label(Loc.T("tm.controls"), 12.5, GameUi.CreamDim));
        help.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(help);
        return GameUi.Board(null, stack, 480);
    }

    private FrameworkElement PauseMenu()
    {
        var stack = new StackPanel();
        var hint = GameUi.Label(Loc.T("tm.pausedHint"), 14, GameUi.CreamDim);
        hint.Margin = new Thickness(0, 0, 0, 16);
        stack.Children.Add(hint);
        stack.Children.Add(MenuButton("tm.resume", GameButtonColor.Green, _game.Resume, height: 62));
        stack.Children.Add(MenuButton("tm.restart", GameButtonColor.Orange, () => _game.Restart()));
        stack.Children.Add(GameUi.Row(MenuButton("tm.options", GameButtonColor.Wood, () => OpenOptions()), MenuButton("tm.mainMenu", GameButtonColor.Red, _game.ShowMainMenu)));
        return GameUi.Board(Loc.T("tm.paused"), stack, 420);
    }

    private FrameworkElement GameOverPanel()
    {
        var result = _game.LastResult;
        var stack = new StackPanel();
        stack.Children.Add(GameUi.Label(Loc.T(_game.LastReason == GameOverReason.TimeUp ? "tm.reason.time" : "tm.reason.branch"), 15, GameUi.CreamDim));

        // Big score with the record stamp.
        var scoreArea = new Grid { Margin = new Thickness(0, 6, 0, 4) };
        var scoreStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        scoreStack.Children.Add(new GameText((result?.Score ?? _game.Score).ToString(Loc.Culture), 88, GameUi.TitleFill, 9) { HorizontalAlignment = HorizontalAlignment.Center });
        scoreStack.Children.Add(GameUi.Label(Loc.T("tm.points").ToUpper(Loc.Culture), 13, GameUi.CreamDim, bold: true));
        scoreArea.Children.Add(scoreStack);
        if (result is { NewHighscore: true })
        {
            var stamp = new Border
            {
                BorderBrush = StampRed, BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 10, 4),
                Background = GameUi.Solid(Color.FromArgb(225, 0xFF, 0xF4, 0xDC)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, -10, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(12),
                Child = new TextBlock { Text = Loc.T("tm.newRecord").ToUpper(Loc.Culture), FontFamily = GameUi.Font, FontSize = 15, Foreground = StampRed, TextAlignment = TextAlignment.Center, MaxWidth = 130, TextWrapping = TextWrapping.Wrap }
            };
            scoreArea.Children.Add(stamp);
            Motion.PopIn(stamp);
        }
        stack.Children.Add(scoreArea);

        var stats = new UniformGrid3();
        var best = result is { NewHighscore: true, PreviousHighscore: > 0 } r ? Loc.F("tm.previousBest", r.PreviousHighscore) : Loc.T("tm.stat.best");
        stats.Add(GameUi.Chip(GameIconKind.Trophy, _game.Highscore.ToString("N0", Loc.Culture), best));
        stats.Add(GameUi.Chip(GameIconKind.Coin, "+" + (result?.CoinsEarned ?? 0).ToString("N0", Loc.Culture), Loc.F("tm.coins", _save.Data.Coins.ToString("N0", Loc.Culture))));
        stats.Add(GameUi.Chip(GameIconKind.Level, (_game.Level + 1).ToString(Loc.Culture), Loc.T("tm.stat.level")));
        stats.Panel.Margin = new Thickness(-4, 4, -4, 8);
        stack.Children.Add(stats.Panel);
        var hint = GameUi.Label(Loc.T("tm.restartHint"), 12.5, GameUi.CreamDim);
        hint.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(hint);
        stack.Children.Add(MenuButton("tm.playAgain", GameButtonColor.Green, () => _game.StartRound(), GameIconKind.Axe, 62));
        stack.Children.Add(ShopRow());
        stack.Children.Add(MenuButton("tm.mainMenu", GameButtonColor.Wood, _game.ShowMainMenu));
        return GameUi.Board(Loc.T("tm.roundOver"), stack, 480);
    }

    /// <summary>"Kisten" and "Skins" side by side.</summary>
    private UIElement ShopRow() => GameUi.Row(
        MenuButton("tm.shop", GameButtonColor.Orange, OpenShop, GameIconKind.Box),
        MenuButton("tm.skins", GameButtonColor.Blue, OpenSkins, GameIconKind.Shirt));

    public void OpenShop() => OpenSub("shop", Loc.T("tm.box.title"), _loot.BuildShop());

    public void OpenSkins() => OpenSub("skins", Loc.T("tm.skins"), _loot.BuildCollection());

    private void OpenSub(string name, string title, FrameworkElement content)
    {
        _optionsOpen = true;
        _shownPanel = name;
        _overlay.Visibility = Visibility.Visible;
        Show(GameUi.Board(title, content, 830));
    }

    public void OpenOptions()
    {
        _optionsOpen = true;
        _shownPanel = "options";
        var stack = new StackPanel();
        void Volume(string key, double value, Action<double> set)
        {
            var label = GameUi.Label(Loc.T(key), 14, GameUi.Cream, bold: true);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            label.Margin = new Thickness(2, 10, 0, 4);
            stack.Children.Add(label);
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = value * 100, SmallChange = 5, LargeChange = 10, IsMoveToPointEnabled = true };
            AutomationProperties.SetName(slider, Loc.T(key));
            slider.ValueChanged += (_, _) =>
            {
                set(slider.Value / 100);
                _save.Save();
            };
            stack.Children.Add(GameUi.Inset(slider, new Thickness(12, 10, 12, 10)));
        }
        Volume("tm.volume.master", _save.Data.MasterVolume, v => _save.Data.MasterVolume = v);
        Volume("tm.volume.music", _save.Data.MusicVolume, v => _save.Data.MusicVolume = v);
        Volume("tm.volume.effects", _save.Data.EffectsVolume, v =>
        {
            _save.Data.EffectsVolume = v;
            _audio()?.Play(SoundEffect.Chop);
        });
        if (_audio() is { Available: false })
        {
            var none = GameUi.Label(Loc.T("tm.noAudio"), 12.5, GameUi.CreamDim);
            none.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(none);
        }
        var back = MenuButton("tm.back", GameButtonColor.Green, CloseOptions);
        back.Margin = new Thickness(0, 20, 0, 0);
        stack.Children.Add(back);
        _overlay.Visibility = Visibility.Visible;
        Show(GameUi.Board(Loc.T("tm.options"), stack, 420));
    }

    public void CloseOptions()
    {
        if (_loot.IsSpinning) return; // the reel always finishes (or is skipped) first
        _optionsOpen = false;
        _shownPanel = "";
        Refresh();
    }

    /// <summary>Three equally wide chips in a row.</summary>
    private sealed class UniformGrid3
    {
        public readonly System.Windows.Controls.Primitives.UniformGrid Panel = new() { Columns = 3 };
        public void Add(UIElement child) => Panel.Children.Add(child);
    }
}
