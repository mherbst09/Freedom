namespace ViennaAtomicClock.Treeman;

/// <summary>What happened in one chop (for effects and sounds).</summary>
public readonly record struct ChopResult(Side PlayerSide, Side RemovedBranch, int Score, bool Milestone, bool Fatal);

/// <summary>Result of a round.</summary>
public readonly record struct RoundResult(int Score, int Highscore, int PreviousHighscore, bool NewHighscore, GameOverReason Reason, int CoinsEarned = 0);

/// <summary>
/// Owns the state machine (MainMenu → Countdown → Playing ⇄ Paused → GameOver) and the round: score, level,
/// tree, time and player. Other systems (view, UI, audio) only listen to its events, so the rules stay free of
/// presentation code and can be tested without a window.
/// </summary>
public sealed class GameManager
{
    private readonly TreemanConfig _config;
    private readonly ISaveSystem _save;
    private readonly Func<ulong> _seedSource;
    private bool _roundOver;

    public GameManager(TreemanConfig config, ISaveSystem save, Func<ulong>? seedSource = null)
    {
        _config = config;
        _save = save;
        _seedSource = seedSource ?? (() => (ulong)Environment.TickCount64 ^ (ulong)Random.Shared.NextInt64());
        Tree = new TreeController(new TreeGenerator(config.Generation, 0), config.Player.VisibleSegments);
        Time = new TimeMeter(config.Time);
        Player = new PlayerController(config.Player);
        Time.Expired += () => EndRound(GameOverReason.TimeUp);
        Tree.Reset(0);
    }

    public TreemanConfig Config => _config;
    public TreeController Tree { get; }
    public TimeMeter Time { get; }
    public PlayerController Player { get; }
    public GameState State { get; private set; } = GameState.MainMenu;
    public int Score { get; private set; }
    public int Level { get; private set; }
    public int Highscore => _save.Data.Highscore;
    public double CountdownLeft { get; private set; }
    public RoundResult? LastResult { get; private set; }
    public GameOverReason LastReason { get; private set; }
    /// <summary>Seed of the current round (reproducible runs).</summary>
    public ulong Seed { get; private set; }

    public event Action<GameState, GameState>? StateChanged;
    public event Action<ChopResult>? Chopped;
    public event Action<int>? LevelChanged;
    public event Action<RoundResult>? RoundEnded;
    /// <summary>A fresh round was prepared (tree, time and score reset) – listeners drop old effects.</summary>
    public event Action? RoundReset;

    private bool SetState(GameState next)
    {
        var allowed = (State, next) switch
        {
            (GameState.MainMenu, GameState.Countdown) or (GameState.MainMenu, GameState.Playing) => true,
            (GameState.Countdown, GameState.Playing) or (GameState.Countdown, GameState.Paused) or (GameState.Countdown, GameState.MainMenu) => true,
            (GameState.Playing, GameState.Paused) or (GameState.Playing, GameState.GameOver) or (GameState.Playing, GameState.MainMenu) => true,
            (GameState.Paused, GameState.Playing) or (GameState.Paused, GameState.Countdown) or (GameState.Paused, GameState.MainMenu) => true,
            (GameState.GameOver, GameState.Countdown) or (GameState.GameOver, GameState.Playing) or (GameState.GameOver, GameState.MainMenu) => true,
            (GameState.Paused, GameState.GameOver) => false,
            _ => false
        };
        if (!allowed) return false;
        var previous = State;
        State = next;
        StateChanged?.Invoke(previous, next);
        return true;
    }

    private GameState _pausedFrom = GameState.Playing;

    /// <summary>Resets everything that belongs to one round (the highscore stays).</summary>
    public void ResetRound(ulong? seed = null)
    {
        Seed = seed ?? _seedSource();
        Score = 0;
        Level = 0;
        _roundOver = false;
        LastReason = GameOverReason.None;
        Time.Reset();
        Time.SetLevel(0);
        Player.Reset();
        Tree.Reset(Seed);
        CountdownLeft = _config.Player.CountdownSeconds;
        RoundReset?.Invoke();
    }

    /// <summary>Starts (or restarts) a round: countdown first if configured.</summary>
    public void StartRound(ulong? seed = null)
    {
        if (State == GameState.Paused) SetState(GameState.MainMenu);
        ResetRound(seed);
        SetState(CountdownLeft > 0 ? GameState.Countdown : GameState.Playing);
    }

    public void Restart(ulong? seed = null) => StartRound(seed);

    public void ShowMainMenu()
    {
        if (State == GameState.MainMenu) return;
        SetState(GameState.MainMenu);
        ResetRound(Seed);
    }

    public void Pause()
    {
        if (State is not (GameState.Playing or GameState.Countdown)) return;
        _pausedFrom = State;
        SetState(GameState.Paused);
    }

    public void Resume()
    {
        if (State != GameState.Paused) return;
        SetState(_pausedFrom);
    }

    /// <summary>Advances the countdown and the time bar. Nothing moves outside of Countdown/Playing.</summary>
    public void Tick(double seconds)
    {
        if (seconds <= 0) return;
        Player.Tick(seconds);
        switch (State)
        {
            case GameState.Countdown:
                CountdownLeft -= seconds;
                if (CountdownLeft <= 0)
                {
                    CountdownLeft = 0;
                    SetState(GameState.Playing);
                }
                break;
            case GameState.Playing:
                Time.Tick(seconds);
                break;
        }
    }

    /// <summary>One chop on the given side. Returns false when it was not allowed (not playing).</summary>
    public bool Chop(Side side)
    {
        if (State != GameState.Playing || _roundOver || side == Side.None) return false;
        Player.Swing(side);
        var removed = Tree.Chop(Level);
        Score++;
        Time.Refill(_config.Time.RefillPerChop);

        var level = Score / _config.Time.PointsPerLevel;
        if (level != Level)
        {
            Level = level;
            Time.SetLevel(level);
            LevelChanged?.Invoke(level);
        }

        var fatal = Tree.Hits(Player.Side);
        Chopped?.Invoke(new ChopResult(Player.Side, removed, Score, Score % _config.Player.MilestoneEvery == 0, fatal));
        if (fatal) EndRound(GameOverReason.Branch);
        return true;
    }

    /// <summary>Ends the round exactly once (a branch and the timer can never both end it).</summary>
    public void EndRound(GameOverReason reason)
    {
        if (_roundOver || State != GameState.Playing) return;
        _roundOver = true;
        LastReason = reason;
        var previous = _save.Data.Highscore;
        _save.Data.Coins += Score; // one coin per point
        var record = _save.SubmitScore(Score);
        if (!record) _save.Save();
        LastResult = new RoundResult(Score, _save.Data.Highscore, previous, record, reason, Score);
        SetState(GameState.GameOver);
        RoundEnded?.Invoke(LastResult.Value);
    }
}
