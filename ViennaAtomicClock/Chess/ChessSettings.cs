namespace ViennaAtomicClock.Chess;

/// <summary>Choices that are remembered locally (%AppData%\ViennaAtomicClock\chess.json), plus the running game.</summary>
public sealed class ChessSettings
{
    public static readonly string[] Difficulties = { "easy", "medium", "hard" };
    public static readonly string[] Colors = { "white", "black", "random" };
    public static readonly string[] Clocks = { "off", "3+2", "5+0", "10+0", "15+10", "30+0" };
    public static readonly string[] Schemes = { "app", "light", "dark" };

    public string Difficulty { get; set; } = "medium";
    public string Color { get; set; } = "white";
    public string Clock { get; set; } = "off";
    public string Scheme { get; set; } = "app";
    public string Board { get; set; } = "wood";
    public bool Hints { get; set; } = true;
    public bool Coordinates { get; set; } = true;
    public bool Animations { get; set; } = true;
    public bool UndoButton { get; set; } = true;
    public SavedChessGame? Game { get; set; }

    /// <summary>Replaces unknown values (hand-edited or old files) with the defaults.</summary>
    public ChessSettings Normalize()
    {
        if (!Difficulties.Contains(Difficulty)) Difficulty = "medium";
        if (!Colors.Contains(Color)) Color = "white";
        if (!Clocks.Contains(Clock)) Clock = "off";
        if (!Schemes.Contains(Scheme)) Scheme = "app";
        Board = BoardTheme.Get(Board).Id;
        return this;
    }

    public static Difficulty ParseDifficulty(string id) => id switch { "easy" => Chess.Difficulty.Easy, "hard" => Chess.Difficulty.Hard, _ => Chess.Difficulty.Medium };

    public static string Id(Difficulty difficulty) => Difficulties[(int)difficulty];

    /// <summary>Start time and increment of a clock setting, or null without a clock.</summary>
    public static (TimeSpan Initial, TimeSpan Increment)? ClockTimes(string id)
    {
        var parts = id.Split('+');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var minutes) || !int.TryParse(parts[1], out var seconds)) return null;
        return (TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(seconds));
    }
}

/// <summary>A game in progress, so it continues after Freedom is restarted.</summary>
public sealed class SavedChessGame
{
    /// <summary>Moves in UCI form, separated by spaces.</summary>
    public string Moves { get; set; } = "";
    public string Player { get; set; } = "white";
    public string Difficulty { get; set; } = "medium";
    public string Clock { get; set; } = "off";
    public long WhiteMs { get; set; }
    public long BlackMs { get; set; }
}
