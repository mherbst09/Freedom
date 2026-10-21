using System.IO;
using System.Text.Json;

namespace ViennaAtomicClock.Treeman;

/// <summary>
/// The visible tree as a fixed ring of segments (object pool: nothing is allocated per chop). Index 0 is the
/// segment at the lumberjack's height.
/// </summary>
public sealed class TreeController
{
    private readonly Side[] _segments;
    private readonly long[] _ids;
    private int _bottom;
    private long _nextId;
    private readonly TreeGenerator _generator;

    public TreeController(TreeGenerator generator, int visibleSegments)
    {
        _generator = generator;
        _segments = new Side[visibleSegments];
        _ids = new long[visibleSegments];
    }

    public int Count => _segments.Length;

    /// <summary>Branch of segment <paramref name="index"/> counted from the bottom.</summary>
    public Side this[int index] => _segments[(_bottom + index) % _segments.Length];

    /// <summary>Stable id of a segment (lets the renderer keep bark patterns while segments slide down).</summary>
    public long IdAt(int index) => _ids[(_bottom + index) % _segments.Length];

    public Side Bottom => this[0];

    public void Reset(ulong seed)
    {
        _generator.Reset(seed);
        _bottom = 0;
        _nextId = 0;
        for (var i = 0; i < _segments.Length; i++)
        {
            _segments[i] = _generator.Next(0);
            _ids[i] = _nextId++;
        }
    }

    /// <summary>Removes the bottom segment, lets everything slide down and recycles the slot as the new top.</summary>
    public Side Chop(int level)
    {
        var removed = _segments[_bottom];
        var slot = _bottom;
        _bottom = (_bottom + 1) % _segments.Length;
        _segments[slot] = _generator.Next(level); // the freed slot becomes the new top segment
        _ids[slot] = _nextId++;
        return removed;
    }

    /// <summary>True if the segment now at the player's height has a branch on the player's side.</summary>
    public bool Hits(Side playerSide) => playerSide != Side.None && Bottom == playerSide;
}

/// <summary>The draining time bar. Only <see cref="Tick"/> lowers it, so a paused game loses nothing.</summary>
public sealed class TimeMeter
{
    private readonly TimeSettings _settings;
    private bool _expiredReported;

    public TimeMeter(TimeSettings settings)
    {
        _settings = settings;
        Reset();
    }

    public double Max => _settings.MaxEnergy;
    public double Current { get; private set; }
    public double Share => Max <= 0 ? 0 : Current / Max;
    public bool IsLow => Share <= _settings.LowWarningShare;

    /// <summary>Drain right now (blends towards <see cref="TargetDrain"/>).</summary>
    public double Drain { get; private set; }
    public double TargetDrain { get; private set; }

    /// <summary>Raised once when the bar reaches zero.</summary>
    public event Action? Expired;

    public void Reset()
    {
        Current = _settings.MaxEnergy;
        Drain = TargetDrain = _settings.StartDrainPerSecond;
        _expiredReported = false;
    }

    public void SetLevel(int level) =>
        TargetDrain = Math.Min(_settings.MaxDrainPerSecond, _settings.StartDrainPerSecond + level * _settings.DrainIncreasePerLevel);

    public void Tick(double seconds)
    {
        if (seconds <= 0 || _expiredReported) return;
        // Smooth step to the new level's drain instead of a jump.
        if (Drain < TargetDrain)
        {
            // One level's extra drain is blended in over LevelBlendSeconds.
            var step = _settings.LevelBlendSeconds <= 0 ? double.MaxValue : Math.Max(_settings.DrainIncreasePerLevel, 0.01) * seconds / _settings.LevelBlendSeconds;
            Drain = Math.Min(TargetDrain, Drain + step);
        }
        else Drain = TargetDrain;
        Current = Math.Max(0, Current - Drain * seconds);
        if (Current <= 0)
        {
            _expiredReported = true;
            Expired?.Invoke();
        }
    }

    public void Refill(double amount)
    {
        if (_expiredReported || amount <= 0) return;
        Current = Math.Min(Max, Current + amount);
    }
}

/// <summary>The lumberjack: side, facing and the swing timer for the animation. No game rules in here.</summary>
public sealed class PlayerController
{
    private readonly PlayerSettings _settings;

    public PlayerController(PlayerSettings settings) => _settings = settings;

    public Side Side { get; private set; } = Side.Left;
    /// <summary>Seconds since the last swing started (large = idle).</summary>
    public double SwingAge { get; private set; } = 10;
    public double SwingProgress => Math.Clamp(SwingAge / _settings.SwingSeconds, 0, 1);
    public bool IsSwinging => SwingAge < _settings.SwingSeconds;

    /// <summary>Raised when the lumberjack changes sides (for the side-switch sound).</summary>
    public event Action<Side>? SideChanged;

    public void Reset()
    {
        Side = Side.Left;
        SwingAge = 10;
    }

    /// <summary>Steps to a side and starts a new swing at once (a running swing never delays the next one).</summary>
    public void Swing(Side side)
    {
        if (side != Side && side != Side.None)
        {
            Side = side;
            SideChanged?.Invoke(side);
        }
        SwingAge = 0;
    }

    public void Tick(double seconds) => SwingAge += seconds;
}

public enum InputAction { Left, Right, Confirm, Pause }

/// <summary>
/// Turns key presses into actions. Chops faster than the cooldown are kept in a small queue instead of being
/// dropped, then executed one per cooldown – so rapid tapping stays stable and nothing is lost. Other input
/// devices (touch, gamepad) only need to call <see cref="Press"/>.
/// </summary>
public sealed class InputController
{
    private readonly PlayerSettings _settings;
    private readonly Side[] _queue;
    private int _queueStart;
    private int _queueCount;
    private double _sinceLastChop = double.MaxValue;

    public InputController(PlayerSettings settings)
    {
        _settings = settings;
        _queue = new Side[Math.Max(1, settings.InputBufferSize + 1)];
    }

    public int Pending => _queueCount;

    /// <summary>Maps keyboard keys; returns null for keys the game ignores.</summary>
    public static InputAction? Map(System.Windows.Input.Key key) => key switch
    {
        System.Windows.Input.Key.A or System.Windows.Input.Key.Left => InputAction.Left,
        System.Windows.Input.Key.D or System.Windows.Input.Key.Right => InputAction.Right,
        System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter => InputAction.Confirm,
        System.Windows.Input.Key.Escape => InputAction.Pause,
        _ => null
    };

    public void Clear()
    {
        _queueStart = _queueCount = 0;
        _sinceLastChop = double.MaxValue;
    }

    /// <summary>Handles one press. Chops go through the cooldown queue; other actions act immediately.</summary>
    public void Press(InputAction action, GameManager game)
    {
        switch (action)
        {
            case InputAction.Confirm:
                if (game.State is GameState.MainMenu or GameState.GameOver) game.StartRound();
                else if (game.State == GameState.Paused) game.Resume();
                break;
            case InputAction.Pause:
                if (game.State is GameState.Playing or GameState.Countdown) game.Pause();
                else if (game.State == GameState.Paused) game.Resume();
                break;
            default:
                if (game.State != GameState.Playing) return; // chops only count while playing
                var side = action == InputAction.Left ? Side.Left : Side.Right;
                if (_queueCount == 0 && _sinceLastChop >= _settings.InputCooldownSeconds)
                {
                    _sinceLastChop = 0;
                    game.Chop(side);
                }
                else if (_queueCount < _queue.Length)
                {
                    _queue[(_queueStart + _queueCount) % _queue.Length] = side;
                    _queueCount++;
                }
                break;
        }
    }

    /// <summary>Releases buffered chops once their cooldown has passed.</summary>
    public void Update(double seconds, GameManager game)
    {
        _sinceLastChop += seconds;
        if (game.State != GameState.Playing)
        {
            _queueStart = _queueCount = 0;
            return;
        }
        if (_queueCount > 0 && _sinceLastChop >= _settings.InputCooldownSeconds)
        {
            var side = _queue[_queueStart];
            _queueStart = (_queueStart + 1) % _queue.Length;
            _queueCount--;
            _sinceLastChop = 0;
            game.Chop(side);
        }
    }
}

/// <summary>Saved values: highscore, coins, skins and the volumes.</summary>
public sealed class TreemanSaveData
{
    public int Highscore { get; set; }
    /// <summary>Every point of a finished round becomes one coin.</summary>
    public long Coins { get; set; }
    public List<string> OwnedSkins { get; set; } = new() { SkinCatalog.DefaultId };
    public string EquippedSkin { get; set; } = SkinCatalog.DefaultId;
    public double MasterVolume { get; set; } = 0.8;
    public double MusicVolume { get; set; } = 0.45;
    public double EffectsVolume { get; set; } = 0.8;
}

/// <summary>Persistence behind an interface (the PlayerPrefs counterpart), so it can be replaced later (e.g. cloud saves).</summary>
public interface ISaveSystem
{
    TreemanSaveData Data { get; }
    /// <summary>Stores the score if it beats the highscore; returns true for a new record.</summary>
    bool SubmitScore(int score);
    void Save();
}

/// <summary>JSON file in %AppData%\ViennaAtomicClock\treeman.json; a broken file falls back to defaults.</summary>
public sealed class JsonSaveSystem : ISaveSystem
{
    private readonly string _path;

    public JsonSaveSystem(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock", "treeman.json");
        Data = Load();
    }

    public TreemanSaveData Data { get; }

    private TreemanSaveData Load()
    {
        try
        {
            var data = File.Exists(_path) ? JsonSerializer.Deserialize<TreemanSaveData>(File.ReadAllText(_path)) ?? new TreemanSaveData() : new TreemanSaveData();
            data.Highscore = Math.Max(0, data.Highscore);
            data.Coins = Math.Max(0, data.Coins);
            data.OwnedSkins = (data.OwnedSkins ?? new List<string>()).Where(SkinCatalog.Exists).Append(SkinCatalog.DefaultId).Distinct().ToList();
            if (data.EquippedSkin is null || !data.OwnedSkins.Contains(data.EquippedSkin)) data.EquippedSkin = SkinCatalog.DefaultId;
            data.MasterVolume = Clamp(data.MasterVolume, 0.8);
            data.MusicVolume = Clamp(data.MusicVolume, 0.45);
            data.EffectsVolume = Clamp(data.EffectsVolume, 0.8);
            return data;
        }
        catch
        {
            return new TreemanSaveData();
        }
    }

    private static double Clamp(double value, double fallback) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;

    public bool SubmitScore(int score)
    {
        if (score <= Data.Highscore) return false;
        Data.Highscore = score;
        Save();
        return true;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch
        {
            // The game keeps working without a writable profile.
        }
    }
}
