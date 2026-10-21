using System.Diagnostics;

namespace ViennaAtomicClock.Chess;

public enum GameResult { Ongoing, WhiteWins, BlackWins, Draw }

public enum EndReason { None, Checkmate, Stalemate, Repetition, FiftyMoves, InsufficientMaterial, Resignation, Timeout }

/// <summary>One played move with everything needed to show it and to take it back.</summary>
public sealed record PlayedMove(Move Move, int Piece, int Captured, string San, Undo Undo);

/// <summary>
/// A game from a start position: the moves played so far, the end of the game (mate, stalemate, the three draw
/// rules, resignation, time) and taking moves back. Knows nothing about the screen or the bots.
/// </summary>
public sealed class ChessGame
{
    private readonly List<PlayedMove> _moves = new();
    private readonly List<ulong> _hashes = new();
    private List<Move>? _legal;

    public ChessGame(string? fen = null)
    {
        StartFen = fen ?? Position.StartFen;
        Position = Position.FromFen(StartFen);
        _hashes.Add(Position.Hash);
        Evaluate();
    }

    public string StartFen { get; }
    public Position Position { get; }
    public IReadOnlyList<PlayedMove> Moves => _moves;
    /// <summary>Hash of every position so far, the current one last (for repetitions).</summary>
    public IReadOnlyList<ulong> Hashes => _hashes;
    public GameResult Result { get; private set; }
    public EndReason Reason { get; private set; }
    public bool IsOver => Result != GameResult.Ongoing;
    public Side ToMove => Position.ToMove;
    public bool InCheck => Position.InCheck();
    public PlayedMove? LastMove => _moves.Count > 0 ? _moves[^1] : null;

    public IReadOnlyList<Move> LegalMoves => IsOver ? Array.Empty<Move>() : _legal ??= Position.LegalMoves();

    public IEnumerable<Move> LegalMovesFrom(int square) => LegalMoves.Where(m => m.From == square);

    /// <summary>How often the current position has appeared (same side to move, castling and en-passant rights).</summary>
    public int RepetitionCount
    {
        get
        {
            var count = 0;
            // Only positions since the last capture or pawn move can repeat.
            for (var i = _hashes.Count - 1; i >= Math.Max(0, _hashes.Count - 1 - Position.Halfmove); i -= 2)
                if (_hashes[i] == Position.Hash) count++;
            return count;
        }
    }

    /// <summary>Plays the move if it is legal; returns false (and changes nothing) otherwise.</summary>
    public bool Play(Move move)
    {
        if (IsOver) return false;
        var legal = LegalMoves.FirstOrDefault(m => m.From == move.From && m.To == move.To && m.Promotion == move.Promotion);
        if (legal.IsNull) return false;
        var san = Position.San(legal);
        var piece = Position.Board[legal.From];
        var undo = Position.Make(legal);
        _moves.Add(new PlayedMove(legal, piece, undo.Captured, san, undo));
        _hashes.Add(Position.Hash);
        _legal = null;
        Evaluate();
        return true;
    }

    public bool Play(string uci)
    {
        var move = Position.FindLegal(uci);
        return move is { } m && Play(m);
    }

    /// <summary>Takes the last move back. Resigned or lost-on-time games stay over.</summary>
    public bool Undo()
    {
        if (_moves.Count == 0 || Reason is EndReason.Resignation or EndReason.Timeout) return false;
        var last = _moves[^1];
        _moves.RemoveAt(_moves.Count - 1);
        _hashes.RemoveAt(_hashes.Count - 1);
        Position.Unmake(last.Move, last.Undo);
        _legal = null;
        Result = GameResult.Ongoing;
        Reason = EndReason.None;
        Evaluate();
        return true;
    }

    public void Resign(Side loser) => End(loser == Side.White ? GameResult.BlackWins : GameResult.WhiteWins, EndReason.Resignation);

    /// <summary>The flag fell. If the opponent cannot mate with what is left, the game is a draw instead.</summary>
    public void Timeout(Side flagged)
    {
        if (IsOver) return;
        if (CannotMate(Piece.Other(flagged))) End(GameResult.Draw, EndReason.Timeout);
        else End(flagged == Side.White ? GameResult.BlackWins : GameResult.WhiteWins, EndReason.Timeout);
    }

    /// <summary>A lone king, or king with a single knight or bishop, can never mate.</summary>
    private bool CannotMate(Side side)
    {
        var minors = 0;
        foreach (var piece in Position.Board)
        {
            if (piece == Piece.None || Piece.SideOf(piece) != side) continue;
            var type = Piece.Type(piece);
            if (type is PieceType.Pawn or PieceType.Rook or PieceType.Queen) return false;
            if (type is PieceType.Knight or PieceType.Bishop) minors++;
        }
        return minors <= 1;
    }

    private void End(GameResult result, EndReason reason)
    {
        Result = result;
        Reason = reason;
        _legal = null;
    }

    private void Evaluate()
    {
        Result = GameResult.Ongoing;
        Reason = EndReason.None;
        if (!Position.HasLegalMove())
        {
            if (Position.InCheck()) End(ToMove == Side.White ? GameResult.BlackWins : GameResult.WhiteWins, EndReason.Checkmate);
            else End(GameResult.Draw, EndReason.Stalemate);
        }
        else if (Position.IsInsufficientMaterial()) End(GameResult.Draw, EndReason.InsufficientMaterial);
        else if (RepetitionCount >= 3) End(GameResult.Draw, EndReason.Repetition);
        else if (Position.Halfmove >= 100) End(GameResult.Draw, EndReason.FiftyMoves);
    }

    /// <summary>Pieces the given side has captured, in the order they were taken.</summary>
    public IEnumerable<int> CapturedBy(Side side) =>
        _moves.Where(m => m.Captured != Piece.None && Piece.SideOf(m.Captured) != side).Select(m => m.Captured);

    /// <summary>Material balance in pawns from White's view (P1 N3 B3 R5 Q9).</summary>
    public int MaterialBalance()
    {
        var total = 0;
        foreach (var piece in Position.Board)
        {
            var value = Piece.Type(piece) switch { PieceType.Pawn => 1, PieceType.Knight or PieceType.Bishop => 3, PieceType.Rook => 5, PieceType.Queen => 9, _ => 0 };
            total += Piece.SideOf(piece) == Side.White ? value : -value;
        }
        return total;
    }
}

/// <summary>Chess clock with an optional increment per move. Runs on a stopwatch, so it is exact however often it is read.</summary>
public sealed class ChessClock
{
    private readonly long[] _remaining = new long[2];
    private readonly Stopwatch _watch = new();
    private Side? _running;

    public ChessClock(TimeSpan initial, TimeSpan increment)
    {
        Initial = initial;
        Increment = increment;
        _remaining[0] = _remaining[1] = (long)initial.TotalMilliseconds;
    }

    public TimeSpan Initial { get; }
    public TimeSpan Increment { get; }
    public Side? Running => _running;
    public bool Paused { get; private set; }

    public TimeSpan Remaining(Side side)
    {
        var ms = _remaining[(int)side];
        if (_running == side) ms -= _watch.ElapsedMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Max(0, ms));
    }

    /// <summary>Sets the time left (restoring a saved game).</summary>
    public void Set(Side side, TimeSpan left) => _remaining[(int)side] = (long)left.TotalMilliseconds;

    /// <summary>Starts the clock of <paramref name="side"/>.</summary>
    public void Start(Side side)
    {
        _running = side;
        _watch.Restart();
        if (Paused) _watch.Stop();
    }

    /// <summary>The side that just moved stops its clock, gets the increment, and the other clock starts.</summary>
    public void Press(Side moved)
    {
        if (_running == moved) _remaining[(int)moved] -= _watch.ElapsedMilliseconds;
        _remaining[(int)moved] += (long)Increment.TotalMilliseconds;
        Start(Piece.Other(moved));
    }

    public void Stop()
    {
        if (_running is { } side) _remaining[(int)side] -= _watch.ElapsedMilliseconds;
        _running = null;
        _watch.Reset();
    }

    public void Pause(bool paused)
    {
        Paused = paused;
        if (paused) _watch.Stop();
        else if (_running is not null) _watch.Start();
    }

    public Side? Flagged => _running is { } side && Remaining(side) <= TimeSpan.Zero ? side : null;
}
