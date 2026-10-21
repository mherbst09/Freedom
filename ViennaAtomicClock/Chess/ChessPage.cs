using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ViennaAtomicClock.Chess;

/// <summary>
/// Chess against three computer opponents. The rules live in <see cref="ChessGame"/>, the bots in
/// <see cref="ChessBots"/>; this page only shows the game and passes moves on. The bot thinks on a background
/// thread, the board takes no input meanwhile and "Bot denkt …" is shown.
/// </summary>
internal sealed class ChessPage : Grid
{
    private const string SettingsFile = "chess.json";
    private const double WideLayout = 900;
    private static readonly Brush DangerBrush = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));

    private readonly ChessSettings _settings;
    private readonly bool _persist;
    private readonly Func<Difficulty, IChessBot> _botFactory;
    private readonly ChessBoardView _board = new();
    private readonly Border _boardFrame;
    private readonly Grid _layoutHost = new();
    private readonly Border _toolbarHost = new();
    private readonly Border _backdrop = new() { Margin = new Thickness(-12), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Grid _overlay = new() { Visibility = Visibility.Collapsed };
    private readonly Border _overlayHost = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) };
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };

    private ChessGame _game = new();
    private IChessBot _bot;
    private Difficulty _difficulty;
    private Side _player = Side.White;
    private string _clockId = "off";
    private ChessClock? _clock;
    private CancellationTokenSource? _botCancel;
    private int _gameVersion;
    private bool _thinking;
    private bool _awaitingSetup;
    private string _overlayKind = "";
    private (int From, int To)? _pendingPromotion;
    private bool? _wide;

    // Choices in the "new game" dialog before they are confirmed.
    private string _pickDifficulty;
    private string _pickColor;
    private string _pickClock;

    // Parts that change while playing.
    private StackPanel _boardColumn = new();
    private readonly PlayerBar _topBar = new();
    private readonly PlayerBar _bottomBar = new();
    private TextBlock _statusTitle = new();
    private TextBlock _statusText = new();
    private Image _statusPiece = new();
    private Border _statusStripe = new();
    private Grid _moveList = new();
    private ScrollViewer _moveScroll = new();
    private Button? _undoButton;
    private Button? _resignButton;

    public event Action? Changed;

    public ChessPage() : this(DataFile.Load<ChessSettings>(SettingsFile).Normalize(), persist: true) { }

    internal ChessPage(ChessSettings settings, bool persist, Func<Difficulty, IChessBot>? botFactory = null)
    {
        _settings = settings;
        _persist = persist;
        _botFactory = botFactory ?? (d => ChessBots.Create(d));
        _difficulty = ChessSettings.ParseDifficulty(settings.Difficulty);
        _bot = _botFactory(_difficulty);
        _pickDifficulty = settings.Difficulty;
        _pickColor = settings.Color;
        _pickClock = settings.Clock;

        _backdrop.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
        _backdrop.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        _boardFrame = new Border { Child = _board, Padding = new Thickness(0), CornerRadius = new CornerRadius(10) };
        _boardFrame.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Opacity = 0.3 };
        _board.MoveRequested += OnMoveRequested;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        SetRowSpan(_backdrop, 2);
        Children.Add(_backdrop);
        Children.Add(_toolbarHost);
        SetRow(_layoutHost, 1);
        Children.Add(_layoutHost);
        var dim = new Border { Background = Frozen(Color.FromArgb(150, 0, 0, 0)) };
        dim.MouseLeftButtonUp += (_, _) => { if (_overlayKind is "settings" or "over") CloseOverlay(); };
        _overlay.Children.Add(dim);
        _overlay.Children.Add(_overlayHost);
        SetRowSpan(_overlay, 2);
        SetZIndex(_overlay, 10);
        Children.Add(_overlay);

        _timer.Tick += (_, _) => Tick();
        Loc.Changed += OnLanguageChanged;
        App.ThemeChanged += ApplyScheme;
        SizeChanged += (_, e) =>
        {
            var wide = e.NewSize.Width >= WideLayout;
            if (wide != _wide) BuildLayout();
        };
        IsVisibleChanged += (_, _) =>
        {
            _clock?.Pause(!IsVisible || _awaitingSetup);
            if (IsVisible) _timer.Start(); else _timer.Stop();
        };
        PreviewKeyDown += OnKey;

        ApplyScheme();
        ApplyBoardSettings();
        if (!Restore(settings.Game))
        {
            NewGame(_difficulty, ResolveColor(settings.Color), settings.Clock);
            _awaitingSetup = true;
        }
        BuildLayout();
        if (_awaitingSetup) ShowSetup();
        else ContinueGame();
    }

    // ───────────────────────── state for the header and tests ─────────────────────────

    internal ChessGame Game => _game;
    internal ChessBoardView BoardView => _board;
    internal Side PlayerSide => _player;
    internal Difficulty CurrentDifficulty => _difficulty;
    internal bool Thinking => _thinking;
    internal string OverlayKind => _overlay.Visibility == Visibility.Visible ? _overlayKind : "";
    internal ChessClock? Clock => _clock;
    internal Task BotTurn { get; private set; } = Task.CompletedTask;
    /// <summary>Multiplies the bot's minimum "thinking" pause (0 in tests).</summary>
    internal double ThinkDelayScale { get; set; } = 1;

    public string StatusLine
    {
        get
        {
            if (_game.IsOver) return ResultTitle();
            if (_thinking) return Loc.T("ch.thinking");
            var text = _game.ToMove == _player ? Loc.T("ch.turn.you") : Loc.T(_game.ToMove == Side.White ? "ch.turn.white" : "ch.turn.black");
            return _game.InCheck ? text + " · " + Loc.T("ch.check") : text;
        }
    }

    public void Shutdown()
    {
        Loc.Changed -= OnLanguageChanged;
        App.ThemeChanged -= ApplyScheme;
        _botCancel?.Cancel();
        _timer.Stop();
        Save();
    }

    private void OnLanguageChanged()
    {
        BuildLayout();
        if (_overlay.Visibility == Visibility.Visible) ReopenOverlay();
    }

    // ───────────────────────── game flow ─────────────────────────

    private Side ResolveColor(string choice) => choice switch
    {
        "black" => Side.Black,
        "random" => Random.Shared.Next(2) == 0 ? Side.White : Side.Black,
        _ => Side.White
    };

    /// <summary>Starts a fresh game: new board, new bot, new clock. Nothing of the old game is kept.</summary>
    internal void NewGame(Difficulty difficulty, Side player, string clockId)
    {
        _botCancel?.Cancel();
        _gameVersion++;
        _thinking = false;
        _pendingPromotion = null;
        _game = new ChessGame();
        _difficulty = difficulty;
        _player = player;
        _bot = _botFactory(difficulty);
        _clockId = clockId;
        _clock = ChessSettings.ClockTimes(clockId) is { } times ? new ChessClock(times.Initial, times.Increment) : null;
        _board.Flipped = player == Side.Black;
        _board.ClearSelection();
        RefreshBoard(null, null);
        RefreshPanel();
    }

    /// <summary>Continues from a set-up position (tests).</summary>
    internal void LoadPosition(string fen, Side player)
    {
        NewGame(_difficulty, player, _clockId);
        _game = new ChessGame(fen);
        ContinueGame();
    }

    /// <summary>Lets the game run: the bot moves if it is its turn.</summary>
    private void ContinueGame()
    {
        _awaitingSetup = false;
        _clock?.Pause(!IsVisible);
        if (IsVisible) _timer.Start();
        RefreshBoard(null, null);
        RefreshPanel();
        if (!_game.IsOver && _game.ToMove != _player) StartBotTurn();
    }

    private void OnMoveRequested(int from, int to)
    {
        if (!PlayerMayMove) return;
        var moves = _game.LegalMoves.Where(m => m.From == from && m.To == to).ToList();
        if (moves.Count == 0) return;
        if (moves.Any(m => m.IsPromotion))
        {
            _pendingPromotion = (from, to);
            ShowPromotion();
            return;
        }
        Play(moves[0]);
    }

    private bool PlayerMayMove => !_game.IsOver && !_thinking && !_awaitingSetup && _game.ToMove == _player && _overlay.Visibility != Visibility.Visible;

    /// <summary>Plays a move for the player as if it was made on the board (tests). Returns false if it is not allowed now.</summary>
    internal bool RequestMove(string uci)
    {
        if (!PlayerMayMove) return false;
        var from = Square.Parse(uci[..2]);
        var to = Square.Parse(uci[2..4]);
        if (!_game.LegalMoves.Any(m => m.From == from && m.To == to)) return false;
        OnMoveRequested(from, to);
        return true;
    }

    internal void ChoosePromotion(PieceType type)
    {
        if (_pendingPromotion is not { } pending) return;
        _pendingPromotion = null;
        CloseOverlay();
        var move = _game.LegalMoves.FirstOrDefault(m => m.From == pending.From && m.To == pending.To && m.Promotion == type);
        if (!move.IsNull) Play(move);
    }

    private void Play(Move move)
    {
        var mover = _game.ToMove;
        var before = (int[])_game.Position.Board.Clone();
        if (!_game.Play(move)) return;
        if (_clock is not null && !_game.IsOver)
        {
            // White's first move is free; from then on each move stops the own clock and starts the other.
            if (_game.Moves.Count == 1) _clock.Start(Piece.Other(mover));
            else _clock.Press(mover);
        }
        if (_game.IsOver) _clock?.Stop();
        RefreshBoard(move, before);
        RefreshPanel();
        Save();
        if (_game.IsOver) ShowGameOverSoon();
        else if (_game.ToMove != _player) StartBotTurn();
    }

    private void StartBotTurn()
    {
        if (_game.IsOver || _game.ToMove == _player || _thinking) return;
        BotTurn = RunBotAsync();
    }

    private async Task RunBotAsync()
    {
        _thinking = true;
        _botCancel = new CancellationTokenSource();
        var token = _botCancel.Token;
        var version = _gameVersion;
        RefreshPanel();
        var position = _game.Position.Clone();
        var history = _game.Hashes.ToArray();
        var bot = _bot;
        var watch = Stopwatch.StartNew();
        Move move;
        try
        {
            move = await Task.Run(() => bot.ChooseMove(position, history, token), token);
            // A short pause, so even an instant answer feels like a move and the slide can be followed.
            var pause = TimeSpan.FromMilliseconds(MinimumThinkMs() * ThinkDelayScale) - watch.Elapsed;
            if (pause > TimeSpan.Zero) await Task.Delay(pause, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (version == _gameVersion) _thinking = false;
        }
        if (token.IsCancellationRequested || version != _gameVersion) return;
        if (move.IsNull || !_game.LegalMoves.Contains(move))
        {
            // Never happens with the built-in bots; a legal fallback keeps the game going anyway.
            var legal = _game.LegalMoves;
            if (legal.Count == 0) return;
            move = legal[Random.Shared.Next(legal.Count)];
        }
        Play(move);
    }

    private int MinimumThinkMs() => _difficulty switch { Difficulty.Easy => 450, Difficulty.Medium => 650, _ => 350 };

    private void CancelBot()
    {
        _botCancel?.Cancel();
        _gameVersion++;
        _thinking = false;
    }

    /// <summary>Whether "Zug zurück" can do something now.</summary>
    private bool CanUndo => _settings.UndoButton && !_awaitingSetup && _game.Reason is not (EndReason.Resignation or EndReason.Timeout)
        && _game.Moves.Select((m, i) => i).Any(i => SideOfPly(i) == _player);

    private Side SideOfPly(int index)
    {
        // The start position may have Black to move; plies alternate from there.
        var startSide = _game.Moves.Count % 2 == 0 ? _game.ToMove : Piece.Other(_game.ToMove);
        return index % 2 == 0 ? startSide : Piece.Other(startSide);
    }

    /// <summary>Takes back the player's last move (and the bot's answer), so it is the player's turn again.</summary>
    internal bool UndoMove()
    {
        if (!CanUndo) return false;
        if (_thinking) CancelBot();
        var removedOwn = false;
        while (_game.Moves.Count > 0 && (!removedOwn || _game.ToMove != _player))
        {
            var mover = SideOfPly(_game.Moves.Count - 1);
            if (!_game.Undo()) break;
            if (mover == _player) removedOwn = true;
        }
        if (_clock is not null)
        {
            if (_game.Moves.Count == 0) _clock.Stop();
            else _clock.Start(_game.ToMove);
        }
        _board.ClearSelection();
        CloseOverlay();
        RefreshBoard(null, null);
        RefreshPanel();
        Save();
        if (!_game.IsOver && _game.ToMove != _player) StartBotTurn();
        return true;
    }

    internal void Resign()
    {
        if (_game.IsOver || _awaitingSetup) return;
        CancelBot();
        _game.Resign(_player);
        _clock?.Stop();
        RefreshBoard(null, null);
        RefreshPanel();
        Save();
        ShowGameOver();
    }

    private void Tick()
    {
        if (_clock is not null)
        {
            if (_clock.Flagged is { } flagged && !_game.IsOver)
            {
                CancelBot();
                _game.Timeout(flagged);
                _clock.Stop();
                RefreshBoard(null, null);
                RefreshPanel();
                Save();
                ShowGameOver();
            }
            UpdateClocks();
        }
        if (_thinking) UpdateThinking();
    }

    private void ShowGameOverSoon()
    {
        var version = _gameVersion;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_settings.Animations ? 650 : 150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (version == _gameVersion && _game.IsOver && _overlay.Visibility != Visibility.Visible) ShowGameOver();
        };
        timer.Start();
    }

    // ───────────────────────── saving ─────────────────────────

    private void Save()
    {
        if (!_persist) return;
        _settings.Game = _game.IsOver || _game.Moves.Count == 0 ? null : new SavedChessGame
        {
            Moves = string.Join(' ', _game.Moves.Select(m => m.Move.Uci)),
            Player = _player == Side.White ? "white" : "black",
            Difficulty = ChessSettings.Id(_difficulty),
            Clock = _clockId,
            WhiteMs = (long)(_clock?.Remaining(Side.White).TotalMilliseconds ?? 0),
            BlackMs = (long)(_clock?.Remaining(Side.Black).TotalMilliseconds ?? 0)
        };
        DataFile.Save(SettingsFile, _settings);
    }

    private bool Restore(SavedChessGame? saved)
    {
        if (saved is null || string.IsNullOrWhiteSpace(saved.Moves)) return false;
        var game = new ChessGame();
        foreach (var uci in saved.Moves.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!game.Play(uci)) return false; // a damaged save is dropped, never half-loaded
        if (game.IsOver) return false;
        NewGame(ChessSettings.ParseDifficulty(saved.Difficulty), saved.Player == "black" ? Side.Black : Side.White, ChessSettings.Clocks.Contains(saved.Clock) ? saved.Clock : "off");
        _game = game;
        if (_clock is not null)
        {
            _clock.Set(Side.White, TimeSpan.FromMilliseconds(Math.Max(1000, saved.WhiteMs)));
            _clock.Set(Side.Black, TimeSpan.FromMilliseconds(Math.Max(1000, saved.BlackMs)));
            _clock.Start(_game.ToMove);
        }
        return true;
    }

    // ───────────────────────── look ─────────────────────────

    /// <summary>"Wie die App" uses the app's colours, "Hell"/"Dunkel" put the app theme in that brightness on this page only.</summary>
    private void ApplyScheme()
    {
        Resources.Clear();
        if (_settings.Scheme == "app")
        {
            _backdrop.Visibility = Visibility.Collapsed;
            return;
        }
        Themes.Apply(Resources, App.ThemeName, _settings.Scheme == "dark", null);
        _backdrop.Visibility = Visibility.Visible;
    }

    private void ApplyBoardSettings()
    {
        _board.Theme = BoardTheme.Get(_settings.Board);
        _board.ShowHints = _settings.Hints;
        _board.ShowCoordinates = _settings.Coordinates;
        _board.Animate = _settings.Animations;
        _board.InvalidateVisual();
    }

    private void SaveSettings()
    {
        if (_persist) DataFile.Save(SettingsFile, _settings);
    }

    // ───────────────────────── layout ─────────────────────────

    private void BuildLayout()
    {
        _wide = ActualWidth <= 0 || ActualWidth >= WideLayout;
        _toolbarHost.Child = Toolbar();

        // Board and player bars move into a new column; they keep their state.
        foreach (var part in new FrameworkElement[] { _topBar.Root, _boardFrame, _bottomBar.Root })
            (part.Parent as Panel)?.Children.Remove(part);
        _boardColumn = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        _boardColumn.Children.Add(_topBar.Root);
        _boardColumn.Children.Add(_boardFrame);
        _boardColumn.Children.Add(_bottomBar.Root);
        _topBar.Root.Margin = new Thickness(0, 0, 0, 8);
        _bottomBar.Root.Margin = new Thickness(0, 8, 0, 0);
        _layoutHost.Children.Clear();
        _layoutHost.ColumnDefinitions.Clear();

        var side = SidePanel();
        if (_wide == true)
        {
            _layoutHost.ColumnDefinitions.Add(new ColumnDefinition());
            _layoutHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            _layoutHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            var area = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            area.Children.Add(_boardColumn);
            area.SizeChanged += (_, _) => SizeBoard(area.ActualWidth, area.ActualHeight);
            _layoutHost.Children.Add(area);
            SetColumn(side, 2);
            side.Margin = new Thickness(0, 12, 0, 0);
            _layoutHost.Children.Add(side);
        }
        else
        {
            var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            stack.Children.Add(_boardColumn);
            side.Margin = new Thickness(0, 16, 0, 0);
            stack.Children.Add(side);
            var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
            scroll.SizeChanged += (_, _) => SizeBoard(Math.Min(scroll.ActualWidth - 4, 640), double.PositiveInfinity);
            _layoutHost.Children.Add(scroll);
            _moveScroll.MaxHeight = 240;
        }
        RefreshBoard(null, null);
        RefreshPanel();
        Changed?.Invoke();
    }

    private void SizeBoard(double width, double height)
    {
        const double bars = 2 * (52 + 8);
        var size = Math.Floor(Math.Clamp(Math.Min(width, height - bars), 240, 920) / 8) * 8;
        if (size <= 0 || double.IsNaN(size)) return;
        _board.Width = _board.Height = size;
        _topBar.Root.Width = _bottomBar.Root.Width = size;
    }

    private UIElement Toolbar()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var shortcuts = Ui.Text(Loc.T("ch.shortcuts"), 11.5, "TextMuted");
        shortcuts.TextWrapping = TextWrapping.Wrap;
        shortcuts.TextTrimming = TextTrimming.None;
        shortcuts.VerticalAlignment = VerticalAlignment.Center;
        shortcuts.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(shortcuts);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(ToolButton("ActionButton", "＋  " + Loc.T("ch.new"), ShowSetup));
        _undoButton = ToolButton("SecondaryButton", "↶  " + Loc.T("ch.undo"), () => UndoMove());
        buttons.Children.Add(_undoButton);
        _resignButton = ToolButton("SecondaryButton", "⚑  " + Loc.T("ch.resign"), ShowResign);
        buttons.Children.Add(_resignButton);
        buttons.Children.Add(ToolButton("SecondaryButton", "⇅", Flip, Loc.T("ch.flip")));
        buttons.Children.Add(ToolButton("SecondaryButton", "⚙", ShowSettings, Loc.T("ch.settings")));
        SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return grid;
    }

    private static Button ToolButton(string style, string text, Action click, string? tooltip = null)
    {
        var button = Ui.Button(style, text, (_, _) => click(), tooltip);
        button.Margin = new Thickness(8, 4, 0, 4);
        button.Height = 38;
        button.MinWidth = 42;
        button.Padding = new Thickness(14, 0, 14, 0);
        AutomationProperties.SetName(button, tooltip ?? text);
        return button;
    }

    private void Flip()
    {
        _board.Flipped = !_board.Flipped;
        RefreshBoard(null, null);
        RefreshPanel();
    }

    private Grid SidePanel()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());

        // Whose turn it is, check, thinking and the result.
        var status = new Grid();
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition());
        _statusStripe = new Border { Width = 5, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 14, 0) };
        status.Children.Add(_statusStripe);
        _statusPiece = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        SetColumn(_statusPiece, 1);
        status.Children.Add(_statusPiece);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _statusTitle = Ui.Text("", 18, bold: true);
        _statusText = Ui.Text("", 12.5, "TextSecondary");
        _statusText.TextWrapping = TextWrapping.Wrap;
        _statusText.TextTrimming = TextTrimming.None;
        texts.Children.Add(_statusTitle);
        texts.Children.Add(_statusText);
        SetColumn(texts, 2);
        status.Children.Add(texts);
        var statusCard = Ui.Card(status, 16);
        grid.Children.Add(statusCard);

        // Opponent and settings of this game.
        var info = new StackPanel();
        info.Children.Add(Heading(Loc.T("ch.info.title")));
        info.Children.Add(InfoRow(Loc.T("ch.info.opponent"), Loc.T("ch.difficulty." + ChessSettings.Id(_difficulty))));
        info.Children.Add(InfoRow(Loc.T("ch.info.side"), Loc.T(_player == Side.White ? "ch.color.white" : "ch.color.black")));
        info.Children.Add(InfoRow(Loc.T("ch.info.clock"), ClockName(_clockId)));
        var help = Ui.Text(Loc.T("ch.difficulty." + ChessSettings.Id(_difficulty) + ".help"), 11.5, "TextMuted");
        help.TextWrapping = TextWrapping.Wrap;
        help.TextTrimming = TextTrimming.None;
        help.Margin = new Thickness(0, 8, 0, 0);
        info.Children.Add(help);
        var infoCard = Ui.Card(info, 16);
        infoCard.Margin = new Thickness(0, 12, 0, 0);
        SetRow(infoCard, 1);
        grid.Children.Add(infoCard);

        // Move list in pairs: 1. e4 e5
        var moves = new DockPanel();
        var heading = Heading(Loc.T("ch.moves"));
        DockPanel.SetDock(heading, Dock.Top);
        moves.Children.Add(heading);
        _moveList = new Grid();
        _moveList.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        _moveList.ColumnDefinitions.Add(new ColumnDefinition());
        _moveList.ColumnDefinitions.Add(new ColumnDefinition());
        _moveScroll = new ScrollViewer { Content = _moveList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, MinHeight = 80 };
        moves.Children.Add(_moveScroll);
        var movesCard = Ui.Card(moves, 16);
        movesCard.Margin = new Thickness(0, 12, 0, 0);
        SetRow(movesCard, 2);
        grid.Children.Add(movesCard);
        return grid;
    }

    private static UIElement InfoRow(string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var text = Ui.Text(value, 13, bold: true);
        DockPanel.SetDock(text, Dock.Right);
        row.Children.Add(text);
        row.Children.Add(Ui.Text(label, 13, "TextSecondary"));
        return row;
    }

    private static TextBlock Heading(string text)
    {
        var block = Ui.Text(text.ToUpper(Loc.Culture), 10.5, "TextMuted", bold: true);
        block.Margin = new Thickness(0, 0, 0, 8);
        return block;
    }

    private static string ClockName(string id) => ChessSettings.ClockTimes(id) is { } t
        ? t.Increment == TimeSpan.Zero ? Loc.F("ch.clock.minutes", (int)t.Initial.TotalMinutes) : Loc.F("ch.clock.increment", (int)t.Initial.TotalMinutes, (int)t.Increment.TotalSeconds)
        : Loc.T("ch.clock.off");

    // ───────────────────────── refreshing ─────────────────────────

    private void RefreshBoard(Move? animate, int[]? before)
    {
        var position = _game.Position;
        _board.LastMove = _game.LastMove is { } last ? (last.Move.From, last.Move.To) : null;
        _board.CheckSquare = !_game.IsOver || _game.Reason == EndReason.Checkmate ? (_game.InCheck ? position.KingSquare(_game.ToMove) : -1) : -1;
        _board.InputEnabled = PlayerMayMove;
        _board.SetPosition(position.Board, _board.InputEnabled ? _game.LegalMoves : Array.Empty<Move>(), animate, before);
    }

    private void RefreshPanel()
    {
        var botSide = Piece.Other(_player);
        var bottomSide = _board.Flipped ? Side.Black : Side.White;
        var topSide = Piece.Other(bottomSide);
        FillBar(_topBar, topSide);
        FillBar(_bottomBar, bottomSide);

        // Status card.
        string title, text;
        var stripe = "AccentBrush";
        if (_game.IsOver)
        {
            title = ResultTitle();
            text = ReasonText();
            stripe = _game.Result == GameResult.Draw ? "TextMuted" : Winner() == _player ? "GreenBrush" : "OrangeBrush";
        }
        else if (_thinking)
        {
            title = Loc.T("ch.thinking");
            text = Loc.T(_game.ToMove == Side.White ? "ch.turn.white" : "ch.turn.black") + " · " + Loc.F("ch.moveNumber", _game.Position.Fullmove);
        }
        else
        {
            title = _game.ToMove == _player ? Loc.T("ch.turn.you") : Loc.T(_game.ToMove == Side.White ? "ch.turn.white" : "ch.turn.black");
            text = Loc.T(_game.ToMove == Side.White ? "ch.turn.white" : "ch.turn.black") + " · " + Loc.F("ch.moveNumber", _game.Position.Fullmove);
        }
        if (!_game.IsOver && _game.InCheck)
        {
            title = Loc.T("ch.check");
            text = Loc.T(_game.ToMove == _player ? "ch.check.you" : "ch.check.bot");
            stripe = "OrangeBrush";
        }
        _statusTitle.Text = title;
        _statusText.Text = text;
        _statusStripe.SetResourceReference(Border.BackgroundProperty, stripe);
        _statusPiece.Source = PieceArt.Image(Piece.Make(_game.IsOver && Winner() is { } w ? w : _game.ToMove, PieceType.King));
        AutomationProperties.SetName(_statusTitle, title + ". " + text);

        // Move list.
        _moveList.Children.Clear();
        _moveList.RowDefinitions.Clear();
        if (_game.Moves.Count == 0)
        {
            var empty = Ui.Text(Loc.T("ch.moves.empty"), 12.5, "TextMuted");
            empty.TextWrapping = TextWrapping.Wrap;
            SetColumnSpan(empty, 3);
            _moveList.Children.Add(empty);
        }
        var startBlack = _game.Moves.Count > 0 && Piece.SideOf(_game.Moves[0].Piece) == Side.Black;
        var firstNumber = Position.FromFen(_game.StartFen).Fullmove;
        for (var i = 0; i < _game.Moves.Count; i++)
        {
            var ply = i + (startBlack ? 1 : 0);
            var row = ply / 2;
            while (_moveList.RowDefinitions.Count <= row)
            {
                _moveList.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var number = Ui.Text($"{firstNumber + _moveList.RowDefinitions.Count - 1}.", 12.5, "TextMuted");
                number.Margin = new Thickness(0, 3, 0, 3);
                SetRow(number, _moveList.RowDefinitions.Count - 1);
                _moveList.Children.Add(number);
            }
            var cell = new Border { Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 1, 6, 1), CornerRadius = new CornerRadius(5) };
            if (i == _game.Moves.Count - 1) cell.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            cell.Child = Ui.Text(_game.Moves[i].San, 13, i == _game.Moves.Count - 1 ? "AccentBrush" : "TextPrimary", bold: i == _game.Moves.Count - 1);
            SetRow(cell, row);
            SetColumn(cell, 1 + ply % 2);
            _moveList.Children.Add(cell);
        }
        _moveScroll.ScrollToEnd();

        if (_undoButton is not null)
        {
            _undoButton.Visibility = _settings.UndoButton ? Visibility.Visible : Visibility.Collapsed;
            _undoButton.IsEnabled = CanUndo;
        }
        if (_resignButton is not null) _resignButton.IsEnabled = !_game.IsOver && !_awaitingSetup && _game.Moves.Count > 0;
        UpdateClocks();
        Changed?.Invoke();
    }

    private void FillBar(PlayerBar bar, Side side)
    {
        var isPlayer = side == _player;
        bar.Avatar.Source = PieceArt.Image(Piece.Make(side, PieceType.King));
        bar.Name.Text = isPlayer ? Loc.T("ch.you") : Loc.F("ch.bot", Loc.T("ch.difficulty." + ChessSettings.Id(_difficulty)));
        bar.Thinking.Visibility = !isPlayer && _thinking ? Visibility.Visible : Visibility.Collapsed;
        bar.Thinking.Text = Loc.T("ch.thinking.short") + " …";
        bar.Captured.Children.Clear();
        // Captured pieces, the most valuable first, like on a real table.
        foreach (var piece in _game.CapturedBy(side).OrderByDescending(p => ChessEval.Values[(int)Piece.Type(p)]))
            bar.Captured.Children.Add(new Image { Source = PieceArt.Image(piece), Width = 20, Height = 20, Margin = new Thickness(0, 0, -5, 0) });
        var balance = _game.MaterialBalance() * (side == Side.White ? 1 : -1);
        bar.Advantage.Text = balance > 0 ? $"+{balance}" : "";
        bar.Side = side;
        bar.Turn.Visibility = !_game.IsOver && _game.ToMove == side ? Visibility.Visible : Visibility.Collapsed;
        bar.ClockBox.Visibility = _clock is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateClocks()
    {
        if (_clock is null) return;
        foreach (var bar in new[] { _topBar, _bottomBar })
        {
            var left = _clock.Remaining(bar.Side);
            bar.ClockText.Text = left.TotalSeconds < 10 ? left.ToString(@"m\:ss\.f") : left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"m\:ss");
            var running = _clock.Running == bar.Side && !_game.IsOver;
            bar.ClockBox.SetResourceReference(Border.BackgroundProperty, running ? "AccentSoft" : "SurfaceAlt");
            if (left.TotalSeconds < 20 && running) bar.ClockText.Foreground = DangerBrush;
            else bar.ClockText.SetResourceReference(TextBlock.ForegroundProperty, running ? "AccentBrush" : "TextSecondary");
        }
    }

    private void UpdateThinking()
    {
        var dots = (int)(Environment.TickCount64 / 350 % 4);
        foreach (var bar in new[] { _topBar, _bottomBar })
            if (bar.Thinking.Visibility == Visibility.Visible) bar.Thinking.Text = Loc.T("ch.thinking.short") + new string('.', dots);
    }

    private Side? Winner() => _game.Result switch { GameResult.WhiteWins => Side.White, GameResult.BlackWins => Side.Black, _ => null };

    private string ResultTitle() => _game.Result == GameResult.Draw ? Loc.T("ch.result.draw") : Winner() == _player ? Loc.T("ch.result.win") : Loc.T("ch.result.loss");

    private string ReasonText() => _game.Reason switch
    {
        EndReason.Resignation => Loc.T(Winner() == _player ? "ch.reason.resignation.bot" : "ch.reason.resignation.you"),
        EndReason.Timeout => Loc.T(_game.Result == GameResult.Draw ? "ch.reason.timeoutdraw" : "ch.reason.timeout"),
        var reason => Loc.T("ch.reason." + reason.ToString().ToLowerInvariant())
    };

    // ───────────────────────── overlays ─────────────────────────

    private void ShowOverlay(string kind, UIElement content, double maxWidth = 560)
    {
        _overlayKind = kind;
        var card = Ui.Card(content, 22);
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.35 };
        _overlayHost.MaxWidth = maxWidth;
        _overlayHost.Child = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        _overlay.Visibility = Visibility.Visible;
        _board.InputEnabled = false;
        Motion.PopIn(card);
    }

    internal void CloseOverlay()
    {
        if (_overlay.Visibility != Visibility.Visible) return;
        var kind = _overlayKind;
        _overlay.Visibility = Visibility.Collapsed;
        _overlayHost.Child = null;
        _overlayKind = "";
        if (kind == "promotion") _pendingPromotion = null;
        if (kind == "setup" && _awaitingSetup) ContinueGame();
        else RefreshBoard(null, null);
    }

    private void ReopenOverlay()
    {
        switch (_overlayKind)
        {
            case "setup": ShowSetup(); break;
            case "promotion": ShowPromotion(); break;
            case "over": ShowGameOver(); break;
            case "resign": ShowResign(); break;
            case "settings": ShowSettings(); break;
        }
    }

    internal void ShowSetup()
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.T("ch.setup.title"), 22, bold: true, title: true));

        stack.Children.Add(SectionTitle(Loc.T("ch.setup.difficulty")));
        var levels = new UniformGrid3();
        foreach (var id in ChessSettings.Difficulties)
        {
            var icon = new Image { Source = PieceArt.Image(Piece.Make(Side.Black, id switch { "easy" => PieceType.Pawn, "medium" => PieceType.Knight, _ => PieceType.Queen })), Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Left };
            var content = new StackPanel();
            content.Children.Add(icon);
            content.Children.Add(Ui.Text(Loc.T("ch.difficulty." + id), 15, bold: true));
            var help = Ui.Text(Loc.T("ch.difficulty." + id + ".help"), 11.5, "TextSecondary");
            help.TextWrapping = TextWrapping.Wrap;
            help.TextTrimming = TextTrimming.None;
            help.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(help);
            var choice = OptionCard(content, _pickDifficulty == id, () => { _pickDifficulty = id; ShowSetup(); });
            AutomationProperties.SetName(choice, Loc.T("ch.difficulty." + id));
            levels.Add(choice);
        }
        stack.Children.Add(levels.Root);

        stack.Children.Add(SectionTitle(Loc.T("ch.setup.color")));
        var colors = new WrapPanel();
        foreach (var id in ChessSettings.Colors)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (id == "random") row.Children.Add(new TextBlock { Text = "🎲", FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            else row.Children.Add(new Image { Source = PieceArt.Image(Piece.Make(id == "white" ? Side.White : Side.Black, PieceType.King)), Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(new TextBlock { Text = Loc.T("ch.color." + id), VerticalAlignment = VerticalAlignment.Center });
            colors.Children.Add(Chip(row, Loc.T("ch.color." + id), _pickColor == id, () => { _pickColor = id; ShowSetup(); }));
        }
        stack.Children.Add(colors);

        stack.Children.Add(SectionTitle(Loc.T("ch.setup.clock")));
        var clocks = new WrapPanel();
        foreach (var id in ChessSettings.Clocks)
            clocks.Children.Add(Chip(ClockName(id), ClockName(id), _pickClock == id, () => { _pickClock = id; ShowSetup(); }));
        stack.Children.Add(clocks);

        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        var start = Ui.Button("ActionButton", "♟  " + Loc.T("ch.setup.start"), (_, _) => StartFromSetup());
        start.Height = 42;
        start.Padding = new Thickness(22, 0, 22, 0);
        buttons.Children.Add(start);
        if (!_awaitingSetup)
        {
            var cancel = Ui.Button("SecondaryButton", Loc.T("ch.cancel"), (_, _) => CloseOverlay());
            cancel.Height = 42;
            cancel.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(cancel);
        }
        stack.Children.Add(buttons);
        ShowOverlay("setup", stack, 640);
    }

    /// <summary>Starts the game chosen in the dialog and remembers the choices.</summary>
    internal void StartFromSetup()
    {
        _settings.Difficulty = _pickDifficulty;
        _settings.Color = _pickColor;
        _settings.Clock = _pickClock;
        NewGame(ChessSettings.ParseDifficulty(_pickDifficulty), ResolveColor(_pickColor), _pickClock);
        _awaitingSetup = true; // closing the dialog starts the game
        CloseOverlay();
        BuildLayout();
        Save();
    }

    private void ShowPromotion()
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.T("ch.promotion.title"), 20, bold: true, title: true));
        var text = Ui.Text(Loc.T("ch.promotion.text"), 13, "TextSecondary");
        text.Margin = new Thickness(0, 4, 0, 14);
        stack.Children.Add(text);
        var row = new WrapPanel();
        foreach (var type in new[] { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight })
        {
            var content = new StackPanel { Width = 78 };
            content.Children.Add(new Image { Source = PieceArt.Image(Piece.Make(_player, type)), Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center });
            var name = Ui.Text(Loc.T("ch.piece." + type.ToString().ToLowerInvariant()), 12.5, bold: true);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(name);
            var button = OptionCard(content, type == PieceType.Queen, () => ChoosePromotion(type));
            button.Margin = new Thickness(0, 0, 10, 10);
            AutomationProperties.SetName(button, Loc.T("ch.piece." + type.ToString().ToLowerInvariant()));
            row.Children.Add(button);
        }
        stack.Children.Add(row);
        var cancel = Ui.Button("SecondaryButton", Loc.T("ch.cancel"), (_, _) => CloseOverlay());
        cancel.HorizontalAlignment = HorizontalAlignment.Left;
        cancel.Height = 38;
        cancel.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(cancel);
        ShowOverlay("promotion", stack, 580);
    }

    private void ShowGameOver()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var icon = _game.Result == GameResult.Draw ? "🤝" : Winner() == _player ? "🏆" : "🤖";
        stack.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 54, HorizontalAlignment = HorizontalAlignment.Center });
        var title = Ui.Text(ResultTitle(), 24, bold: true, title: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(title);
        var reason = Ui.Text(ReasonText(), 13.5, "TextSecondary");
        reason.TextWrapping = TextWrapping.Wrap;
        reason.TextTrimming = TextTrimming.None;
        reason.TextAlignment = TextAlignment.Center;
        reason.HorizontalAlignment = HorizontalAlignment.Center;
        reason.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(reason);
        var moves = Ui.Text(Loc.F("ch.over.moves", (_game.Moves.Count + 1) / 2) + " · " + Loc.T("ch.difficulty." + ChessSettings.Id(_difficulty)), 12, "TextMuted");
        moves.HorizontalAlignment = HorizontalAlignment.Center;
        moves.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(moves);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        var again = Ui.Button("ActionButton", "♟  " + Loc.T("ch.new"), (_, _) => ShowSetup());
        again.Height = 42;
        again.Padding = new Thickness(20, 0, 20, 0);
        buttons.Children.Add(again);
        if (CanUndo)
        {
            var undo = Ui.Button("SecondaryButton", "↶  " + Loc.T("ch.undo"), (_, _) => UndoMove());
            undo.Height = 42;
            undo.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(undo);
        }
        var view = Ui.Button("SecondaryButton", Loc.T("ch.over.view"), (_, _) => CloseOverlay());
        view.Height = 42;
        view.Margin = new Thickness(10, 0, 0, 0);
        buttons.Children.Add(view);
        stack.Children.Add(buttons);
        ShowOverlay("over", stack, 520);
    }

    private void ShowResign()
    {
        if (_game.IsOver || _awaitingSetup) return;
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.T("ch.resign.title"), 20, bold: true, title: true));
        var text = Ui.Text(Loc.T("ch.resign.text"), 13, "TextSecondary");
        text.TextWrapping = TextWrapping.Wrap;
        text.Margin = new Thickness(0, 6, 0, 16);
        stack.Children.Add(text);
        var buttons = new WrapPanel();
        var resign = Ui.Button("ActionButton", "⚑  " + Loc.T("ch.resign.confirm"), (_, _) => { CloseOverlay(); Resign(); });
        resign.Background = DangerBrush;
        resign.Height = 40;
        buttons.Children.Add(resign);
        var keep = Ui.Button("SecondaryButton", Loc.T("ch.resign.keep"), (_, _) => CloseOverlay());
        keep.Height = 40;
        keep.Margin = new Thickness(10, 0, 0, 0);
        buttons.Children.Add(keep);
        stack.Children.Add(buttons);
        ShowOverlay("resign", stack, 440);
    }

    internal void ShowSettings()
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(Loc.T("ch.settings.title"), 22, bold: true, title: true));

        stack.Children.Add(SectionTitle(Loc.T("ch.settings.scheme")));
        var schemes = new WrapPanel();
        foreach (var id in ChessSettings.Schemes)
            schemes.Children.Add(Chip(Loc.T("ch.scheme." + id), Loc.T("ch.scheme." + id), _settings.Scheme == id, () =>
            {
                _settings.Scheme = id;
                ApplyScheme();
                SaveSettings();
                ShowSettings();
            }));
        stack.Children.Add(schemes);

        stack.Children.Add(SectionTitle(Loc.T("ch.settings.board")));
        var boards = new WrapPanel();
        foreach (var theme in BoardTheme.All)
        {
            var preview = new UniformGridPreview(theme);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(preview.Root);
            row.Children.Add(new TextBlock { Text = Loc.T("ch.board." + theme.Id), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            boards.Children.Add(Chip(row, Loc.T("ch.board." + theme.Id), _settings.Board == theme.Id, () =>
            {
                _settings.Board = theme.Id;
                ApplyBoardSettings();
                SaveSettings();
                ShowSettings();
            }));
        }
        stack.Children.Add(boards);

        stack.Children.Add(SectionTitle(Loc.T("ch.settings.play")));
        var toggles = new WrapPanel();
        toggles.Children.Add(Toggle(Loc.T("ch.settings.hints"), _settings.Hints, v => _settings.Hints = v));
        toggles.Children.Add(Toggle(Loc.T("ch.settings.coordinates"), _settings.Coordinates, v => _settings.Coordinates = v));
        toggles.Children.Add(Toggle(Loc.T("ch.settings.animations"), _settings.Animations, v => _settings.Animations = v));
        toggles.Children.Add(Toggle(Loc.T("ch.settings.undo"), _settings.UndoButton, v => _settings.UndoButton = v));
        stack.Children.Add(toggles);

        var done = Ui.Button("ActionButton", Loc.T("ch.settings.done"), (_, _) => CloseOverlay());
        done.HorizontalAlignment = HorizontalAlignment.Left;
        done.Height = 40;
        done.Padding = new Thickness(22, 0, 22, 0);
        done.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(done);
        ShowOverlay("settings", stack, 600);
    }

    internal void SetScheme(string id)
    {
        _settings.Scheme = ChessSettings.Schemes.Contains(id) ? id : "app";
        ApplyScheme();
    }

    private Button Toggle(string text, bool value, Action<bool> set)
    {
        var chip = Chip((value ? "☑  " : "☐  ") + text, text, value, () =>
        {
            set(!value);
            ApplyBoardSettings();
            RefreshPanel();
            SaveSettings();
            ShowSettings();
        });
        return chip;
    }

    private static TextBlock SectionTitle(string text)
    {
        var block = Ui.Text(text.ToUpper(Loc.Culture), 10.5, "TextMuted", bold: true);
        block.Margin = new Thickness(0, 16, 0, 8);
        return block;
    }

    private static Button Chip(object content, string name, bool selected, Action click)
    {
        var chip = Ui.Button("ChipButton", content, (_, _) => click());
        chip.Height = 36;
        chip.Margin = new Thickness(0, 0, 8, 8);
        chip.Padding = new Thickness(12, 0, 12, 0);
        Ui.Select(chip, selected);
        AutomationProperties.SetName(chip, name);
        AutomationProperties.SetItemStatus(chip, selected ? Loc.T("ac.selected") : "");
        return chip;
    }

    /// <summary>Large selectable card (difficulty, promotion piece).</summary>
    private static Button OptionCard(UIElement content, bool selected, Action click)
    {
        var border = new Border { Child = content, Padding = new Thickness(14), BorderThickness = new Thickness(2) };
        border.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        border.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSoft" : "SurfaceAlt");
        border.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBrush" : "SurfaceAlt");
        var button = new Button { Content = border, Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        button.Template = BareTemplate;
        button.Click += (_, _) => click();
        AutomationProperties.SetItemStatus(button, selected ? Loc.T("ac.selected") : "");
        return button;
    }

    private static readonly ControlTemplate BareTemplate = CreateBareTemplate();

    private static ControlTemplate CreateBareTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        template.VisualTree = presenter;
        template.Seal();
        return template;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _overlay.Visibility == Visibility.Visible)
        {
            CloseOverlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _board.Selected >= 0)
        {
            _board.ClearSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _overlay.Visibility != Visibility.Visible)
        {
            UndoMove();
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control && _overlay.Visibility != Visibility.Visible)
        {
            ShowSetup();
            e.Handled = true;
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Name, captured pieces, material lead and clock of one side, shown above and below the board.</summary>
    private sealed class PlayerBar
    {
        public readonly Grid Root = new() { Height = 52 };
        public readonly Image Avatar = new() { Width = 34, Height = 34 };
        public readonly TextBlock Name = Ui.Text("", 14, bold: true);
        public readonly TextBlock Thinking = Ui.Text("", 12.5, "AccentBrush", bold: true);
        public readonly WrapPanel Captured = new() { Height = 20, ClipToBounds = true };
        public readonly TextBlock Advantage = Ui.Text("", 12, "TextSecondary", bold: true);
        public readonly Border Turn = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        public readonly Border ClockBox = new() { Padding = new Thickness(14, 6, 14, 6), MinWidth = 92, VerticalAlignment = VerticalAlignment.Center };
        public readonly TextBlock ClockText = new() { FontSize = 20, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas, Cascadia Mono"), HorizontalAlignment = HorizontalAlignment.Center };
        public Side Side;

        public PlayerBar()
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition());
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var avatar = new Border { Child = Avatar, Width = 44, Height = 44, Padding = new Thickness(5), Margin = new Thickness(0, 0, 10, 0) };
            avatar.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
            avatar.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
            Root.Children.Add(avatar);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(Name);
            Turn.SetResourceReference(Border.BackgroundProperty, "GreenBrush");
            nameRow.Children.Add(Turn);
            Thinking.Margin = new Thickness(10, 0, 0, 0);
            Thinking.VerticalAlignment = VerticalAlignment.Center;
            nameRow.Children.Add(Thinking);
            texts.Children.Add(nameRow);
            var capturedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            capturedRow.Children.Add(Captured);
            Advantage.Margin = new Thickness(10, 0, 0, 0);
            Advantage.VerticalAlignment = VerticalAlignment.Center;
            capturedRow.Children.Add(Advantage);
            texts.Children.Add(capturedRow);
            Grid.SetColumn(texts, 1);
            Root.Children.Add(texts);
            ClockBox.Child = ClockText;
            ClockBox.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
            Grid.SetColumn(ClockBox, 2);
            Root.Children.Add(ClockBox);
        }
    }

    /// <summary>Three equally wide columns that fall back to one column when narrow.</summary>
    private sealed class UniformGrid3
    {
        public readonly System.Windows.Controls.Primitives.UniformGrid Root = new() { Columns = 3 };

        public UniformGrid3() => Root.SizeChanged += (_, e) => Root.Columns = e.NewSize.Width < 420 ? 1 : 3;

        public void Add(Button button)
        {
            button.Margin = new Thickness(0, 0, 10, 10);
            Root.Children.Add(button);
        }
    }

    /// <summary>2×2 squares in the colours of a board design.</summary>
    private sealed class UniformGridPreview
    {
        public readonly System.Windows.Controls.Primitives.UniformGrid Root = new() { Columns = 2, Rows = 2, Width = 20, Height = 20 };

        public UniformGridPreview(BoardTheme theme)
        {
            foreach (var light in new[] { true, false, false, true })
                Root.Children.Add(new Border { Background = Frozen(light ? theme.Light : theme.Dark) });
        }
    }
}
