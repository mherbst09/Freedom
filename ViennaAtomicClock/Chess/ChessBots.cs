using System.Diagnostics;

namespace ViennaAtomicClock.Chess;

public enum Difficulty { Easy, Medium, Hard }

/// <summary>A computer opponent. Runs on a background thread and only ever returns a legal move.</summary>
public interface IChessBot
{
    /// <param name="position">The bot works on a copy, so the game is never touched from the background thread.</param>
    /// <param name="history">Hashes of the positions played so far (to see repetitions).</param>
    Move ChooseMove(Position position, IReadOnlyList<ulong> history, CancellationToken token);
}

public static class ChessBots
{
    public static IChessBot Create(Difficulty difficulty, Random? random = null, TimeSpan? hardTimeLimit = null) => difficulty switch
    {
        Difficulty.Easy => new EasyBot(random ?? new Random()),
        Difficulty.Medium => new SearchBot(new SearchOptions { MaxDepth = 2, EndgameDepth = 3, TimeLimit = TimeSpan.FromSeconds(1.5), FullEval = false, RootNoise = 25, QuiescenceDepth = 4, UseBook = true }, random ?? new Random()),
        _ => new SearchBot(new SearchOptions { MaxDepth = 64, EndgameDepth = 64, TimeLimit = hardTimeLimit ?? TimeSpan.FromSeconds(2.5), FullEval = true, QuiescenceDepth = 12, UseBook = true }, random ?? new Random())
    };
}

/// <summary>
/// Beginner bot: mostly random legal moves. It takes a mate in one when it sees it, often grabs pieces that
/// hang and likes giving check, but makes plenty of obvious mistakes.
/// </summary>
public sealed class EasyBot : IChessBot
{
    private readonly Random _random;

    public EasyBot(Random random) => _random = random;

    public Move ChooseMove(Position position, IReadOnlyList<ulong> history, CancellationToken token)
    {
        var board = position.Clone();
        var moves = board.LegalMoves();
        if (moves.Count == 0) return default;
        var mates = new List<Move>();
        var checks = new List<Move>();
        foreach (var move in moves)
        {
            var undo = board.Make(move);
            if (board.InCheck())
            {
                if (!board.HasLegalMove()) mates.Add(move);
                else checks.Add(move);
            }
            board.Unmake(move, undo);
        }
        if (mates.Count > 0 && _random.NextDouble() < 0.8) return mates[_random.Next(mates.Count)];
        var roll = _random.NextDouble();
        if (roll < 0.25)
        {
            // Grab the most valuable piece it can take, without looking whether it is protected.
            var captures = moves.Where(m => m.IsCapture).ToList();
            if (captures.Count > 0)
            {
                var best = captures.Max(m => Victim(board, m));
                var top = captures.Where(m => Victim(board, m) == best).ToList();
                return top[_random.Next(top.Count)];
            }
        }
        else if (roll < 0.35 && checks.Count > 0) return checks[_random.Next(checks.Count)];
        return moves[_random.Next(moves.Count)];
    }

    private static int Victim(Position position, Move move) =>
        (move.Flags & MoveFlags.EnPassant) != 0 ? 100 : ChessEval.Values[(int)Piece.Type(position.Board[move.To])];
}

public sealed class SearchOptions
{
    public int MaxDepth { get; init; } = 64;
    /// <summary>Depth once few pieces are left (fewer moves, so the bot can see further in the same time).</summary>
    public int EndgameDepth { get; init; } = 64;
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromSeconds(2);
    public bool FullEval { get; init; } = true;
    /// <summary>Random centipawns added to each root move, so a weaker bot does not always play the same.</summary>
    public int RootNoise { get; init; }
    public int QuiescenceDepth { get; init; } = 12;
    public bool UseBook { get; init; }
}

public readonly record struct SearchResult(Move Best, int Score, int Depth, long Nodes, TimeSpan Elapsed);

/// <summary>Medium and hard bot: a minimax search with the given options.</summary>
public sealed class SearchBot : IChessBot
{
    private readonly ChessSearch _search;
    private readonly Random _random;
    private readonly SearchOptions _options;

    public SearchBot(SearchOptions options, Random random)
    {
        _options = options;
        _random = random;
        _search = new ChessSearch(options, random);
    }

    public SearchResult? LastResult { get; private set; }

    public Move ChooseMove(Position position, IReadOnlyList<ulong> history, CancellationToken token)
    {
        if (_options.UseBook && OpeningBook.Pick(position, _random) is { } book) return book;
        var result = _search.Search(position, history, token);
        LastResult = result;
        return result.Best;
    }
}

/// <summary>
/// Negamax with alpha-beta pruning, iterative deepening and a time limit. Move ordering: the best move from the
/// transposition table, then captures (most valuable victim first), promotions, killer moves and the history
/// heuristic. Quiescence search on captures, check extension, null-move pruning and late-move reductions.
/// Mates score higher than any material, and a shorter mate higher than a longer one.
/// </summary>
public sealed class ChessSearch
{
    public const int Mate = 30000;
    private const int Infinity = 32000;
    private const int MateBound = Mate - 512;
    private const int TableBits = 19;

    private readonly SearchOptions _options;
    private readonly Random _random;
    private readonly Entry[] _table = new Entry[1 << TableBits];
    private readonly Move[,] _killers = new Move[128, 2];
    private readonly int[,] _historyScores = new int[64, 64];
    private readonly List<Move>[] _moveLists = Enumerable.Range(0, 130).Select(_ => new List<Move>(64)).ToArray();
    private readonly int[][] _orderScores = Enumerable.Range(0, 130).Select(_ => new int[256]).ToArray();
    private readonly ulong[] _path = new ulong[1024];
    private int _pathLength;
    private Position _position = Position.Start();
    private Stopwatch _clock = new();
    private CancellationToken _token;
    private long _nodes;
    private bool _stopped;

    private struct Entry
    {
        public ulong Key;
        public int Move;
        public short Score;
        public sbyte Depth;
        public byte Bound; // 1 exact, 2 lower, 3 upper
    }

    public ChessSearch(SearchOptions options, Random? random = null)
    {
        _options = options;
        _random = random ?? new Random();
    }

    public SearchResult Search(Position root, IReadOnlyList<ulong> history, CancellationToken token)
    {
        _position = root.Clone();
        _token = token;
        _clock = Stopwatch.StartNew();
        _nodes = 0;
        _stopped = false;
        Array.Clear(_killers);
        for (var i = 0; i < 64; i++)
            for (var j = 0; j < 64; j++)
                _historyScores[i, j] /= 8;
        // Positions before the root, newest last: only those since the last capture or pawn move can repeat.
        _pathLength = 0;
        var keep = Math.Min(history.Count, Math.Min(_path.Length - 200, _position.Halfmove + 1));
        for (var i = history.Count - keep; i < history.Count; i++) _path[_pathLength++] = history[i];
        if (_pathLength == 0 || _path[_pathLength - 1] != _position.Hash) _path[_pathLength++] = _position.Hash;

        var rootMoves = _position.LegalMoves();
        if (rootMoves.Count == 0) return new SearchResult(default, 0, 0, 0, _clock.Elapsed);
        if (rootMoves.Count == 1) return new SearchResult(rootMoves[0], 0, 0, 0, _clock.Elapsed);

        var maxDepth = ChessEval.Phase(_position) <= 8 ? _options.EndgameDepth : _options.MaxDepth;
        var noise = rootMoves.Select(_ => _options.RootNoise > 0 ? _random.Next(_options.RootNoise + 1) : 0).ToArray();
        var scores = new int[rootMoves.Count];
        var best = rootMoves[0];
        var bestScore = -Infinity;
        var completedDepth = 0;

        for (var depth = 1; depth <= maxDepth; depth++)
        {
            // The best move so far is searched first, the others by their score from the last iteration.
            var order = Enumerable.Range(0, rootMoves.Count).OrderByDescending(i => rootMoves[i] == best ? int.MaxValue : scores[i]).ToArray();
            var alpha = -Infinity;
            var iterationBest = best;
            var iterationScore = -Infinity;
            var searchedAny = false;
            for (var k = 0; k < order.Length; k++)
            {
                var i = order[k];
                var move = rootMoves[i];
                var undo = _position.Make(move);
                _path[_pathLength++] = _position.Hash;
                int score;
                if (_options.RootNoise > 0 || k == 0)
                    score = -Negamax(depth - 1, 1, -Infinity, _options.RootNoise > 0 ? Infinity : -alpha, true);
                else
                {
                    score = -Negamax(depth - 1, 1, -alpha - 1, -alpha, true);
                    if (score > alpha && !_stopped) score = -Negamax(depth - 1, 1, -Infinity, -alpha, true);
                }
                _pathLength--;
                _position.Unmake(move, undo);
                if (_stopped) break;
                searchedAny = true;
                scores[i] = score;
                var ranked = score + (Math.Abs(score) < MateBound ? noise[i] : 0);
                if (ranked > iterationScore)
                {
                    iterationScore = ranked;
                    iterationBest = move;
                }
                if (score > alpha) alpha = score;
            }
            // A cut-off iteration still counts if it searched the old best move: anything that beat it is better.
            if (searchedAny && (!_stopped || iterationScore > bestScore))
            {
                best = iterationBest;
                bestScore = iterationScore;
                completedDepth = _stopped ? completedDepth : depth;
            }
            if (_stopped) break;
            if (Math.Abs(bestScore) >= MateBound && depth >= 2 && Mate - Math.Abs(bestScore) <= depth) break; // a mate is settled
            if (_clock.Elapsed.TotalMilliseconds > _options.TimeLimit.TotalMilliseconds * 0.45) break; // the next depth would not finish
        }
        return new SearchResult(best, bestScore, completedDepth, _nodes, _clock.Elapsed);
    }

    private void CheckStop()
    {
        if (_clock.Elapsed > _options.TimeLimit || _token.IsCancellationRequested) _stopped = true;
    }

    private bool IsRepetition()
    {
        var hash = _position.Hash;
        var limit = Math.Max(0, _pathLength - 1 - _position.Halfmove);
        for (var i = _pathLength - 3; i >= limit; i -= 2)
            if (_path[i] == hash) return true;
        return false;
    }

    private int Negamax(int depth, int ply, int alpha, int beta, bool allowNull)
    {
        if ((++_nodes & 1023) == 0) CheckStop();
        if (_stopped) return 0;
        if (_position.Halfmove >= 100 || IsRepetition() || _position.IsInsufficientMaterial()) return 0;
        if (ply >= 120) return Evaluate();

        // Mate-distance pruning: no line from here can beat an already found shorter mate.
        alpha = Math.Max(alpha, -Mate + ply);
        beta = Math.Min(beta, Mate - ply - 1);
        if (alpha >= beta) return alpha;

        var inCheck = _position.InCheck();
        if (inCheck) depth++;
        if (depth <= 0) return Quiescence(alpha, beta, ply, 0);

        var hash = _position.Hash;
        ref var entry = ref _table[hash & ((1UL << TableBits) - 1)];
        var ttMove = default(Move);
        if (entry.Key == hash)
        {
            ttMove = Move.Unpack(entry.Move);
            if (entry.Depth >= depth)
            {
                var stored = FromTable(entry.Score, ply);
                if (entry.Bound == 1 || (entry.Bound == 2 && stored >= beta) || (entry.Bound == 3 && stored <= alpha)) return stored;
            }
        }

        if (allowNull && !inCheck && depth >= 3 && beta < MateBound && HasPieces(_position.ToMove))
        {
            var undoNull = _position.MakeNull();
            _path[_pathLength++] = _position.Hash;
            var score = -Negamax(depth - 3, ply + 1, -beta, -beta + 1, false);
            _pathLength--;
            _position.UnmakeNull(undoNull);
            if (_stopped) return 0;
            if (score >= beta) return beta;
        }

        var moves = _moveLists[ply];
        moves.Clear();
        _position.GeneratePseudoLegal(moves);
        var order = _orderScores[ply];
        for (var i = 0; i < moves.Count; i++) order[i] = OrderScore(moves[i], ttMove, ply);

        var originalAlpha = alpha;
        var bestScore = -Infinity;
        var bestMove = default(Move);
        var legal = 0;
        for (var i = 0; i < moves.Count; i++)
        {
            var move = PickNext(moves, order, i);
            var undo = _position.Make(move);
            if (_position.LeftKingInCheck())
            {
                _position.Unmake(move, undo);
                continue;
            }
            legal++;
            _path[_pathLength++] = _position.Hash;
            int score;
            if (legal == 1) score = -Negamax(depth - 1, ply + 1, -beta, -alpha, true);
            else
            {
                // Late quiet moves are probably bad: look at them less deep first, and properly only if they surprise.
                var reduce = depth >= 3 && legal > 4 && !inCheck && !move.IsCapture && !move.IsPromotion && !_position.InCheck() ? 1 + (legal > 12 ? 1 : 0) : 0;
                score = -Negamax(depth - 1 - reduce, ply + 1, -alpha - 1, -alpha, true);
                if (score > alpha && reduce > 0) score = -Negamax(depth - 1, ply + 1, -alpha - 1, -alpha, true);
                if (score > alpha && score < beta) score = -Negamax(depth - 1, ply + 1, -beta, -alpha, true);
            }
            _pathLength--;
            _position.Unmake(move, undo);
            if (_stopped) return 0;
            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
            }
            if (score > alpha) alpha = score;
            if (alpha >= beta)
            {
                if (!move.IsCapture && !move.IsPromotion)
                {
                    if (_killers[ply, 0] != move)
                    {
                        _killers[ply, 1] = _killers[ply, 0];
                        _killers[ply, 0] = move;
                    }
                    _historyScores[move.From, move.To] += depth * depth;
                }
                break;
            }
        }
        if (legal == 0) return inCheck ? -Mate + ply : 0;

        var bound = bestScore <= originalAlpha ? (byte)3 : bestScore >= beta ? (byte)2 : (byte)1;
        if (entry.Key != hash || depth >= entry.Depth || bound == 1)
        {
            entry.Key = hash;
            entry.Move = bestMove.Pack();
            entry.Score = (short)ToTable(bestScore, ply);
            entry.Depth = (sbyte)Math.Min(127, depth);
            entry.Bound = bound;
        }
        return bestScore;
    }

    private int Quiescence(int alpha, int beta, int ply, int qDepth)
    {
        if ((++_nodes & 1023) == 0) CheckStop();
        if (_stopped) return 0;
        var stand = Evaluate();
        if (stand >= beta) return stand;
        if (stand > alpha) alpha = stand;
        if (qDepth >= _options.QuiescenceDepth || ply >= 125) return stand;

        var moves = _moveLists[ply];
        moves.Clear();
        _position.GeneratePseudoLegal(moves, tacticalOnly: true);
        var order = _orderScores[ply];
        for (var i = 0; i < moves.Count; i++) order[i] = OrderScore(moves[i], default, ply);
        var best = stand;
        for (var i = 0; i < moves.Count; i++)
        {
            var move = PickNext(moves, order, i);
            // Delta pruning: even winning this piece cannot lift the score to alpha.
            if (!move.IsPromotion && stand + Victim(move) + 200 < alpha) continue;
            var undo = _position.Make(move);
            if (_position.LeftKingInCheck())
            {
                _position.Unmake(move, undo);
                continue;
            }
            var score = -Quiescence(-beta, -alpha, ply + 1, qDepth + 1);
            _position.Unmake(move, undo);
            if (_stopped) return 0;
            if (score > best) best = score;
            if (score > alpha) alpha = score;
            if (alpha >= beta) break;
        }
        return best;
    }

    private int Evaluate() => _options.FullEval ? ChessEval.Full(_position) : ChessEval.Simple(_position);

    private int Victim(Move move) =>
        (move.Flags & MoveFlags.EnPassant) != 0 ? 100 : ChessEval.Values[(int)Piece.Type(_position.Board[move.To])];

    private int OrderScore(Move move, Move ttMove, int ply)
    {
        if (move == ttMove) return 10_000_000;
        if (move.IsCapture)
            return 1_000_000 + Victim(move) * 10 - ChessEval.Values[(int)Piece.Type(_position.Board[move.From])] / 10;
        if (move.IsPromotion) return 900_000 + ChessEval.Values[(int)move.Promotion];
        if (move == _killers[ply, 0]) return 800_000;
        if (move == _killers[ply, 1]) return 790_000;
        return Math.Min(700_000, _historyScores[move.From, move.To]);
    }

    /// <summary>Selection sort step: brings the best remaining move to position <paramref name="index"/>.</summary>
    private static Move PickNext(List<Move> moves, int[] order, int index)
    {
        var best = index;
        for (var j = index + 1; j < moves.Count; j++)
            if (order[j] > order[best]) best = j;
        if (best != index)
        {
            (moves[index], moves[best]) = (moves[best], moves[index]);
            (order[index], order[best]) = (order[best], order[index]);
        }
        return moves[index];
    }

    private bool HasPieces(Side side)
    {
        foreach (var piece in _position.Board)
            if (piece != Piece.None && Piece.SideOf(piece) == side && Piece.Type(piece) is not (PieceType.Pawn or PieceType.King)) return true;
        return false;
    }

    // Mate scores are stored relative to the node, so they stay right when the position is reached at another depth.
    private static int ToTable(int score, int ply) => score >= MateBound ? score + ply : score <= -MateBound ? score - ply : score;
    private static int FromTable(int score, int ply) => score >= MateBound ? score - ply : score <= -MateBound ? score + ply : score;
}

/// <summary>A few common, sound opening lines, so the bots do not open the same way every game.</summary>
public static class OpeningBook
{
    private static readonly string[] Lines =
    {
        "e2e4 e7e5 g1f3 b8c6 f1b5 a7a6 b5a4 g8f6 e1g1",
        "e2e4 e7e5 g1f3 b8c6 f1c4 f8c5 c2c3 g8f6",
        "e2e4 e7e5 g1f3 b8c6 d2d4 e5d4 f3d4 g8f6",
        "e2e4 c7c5 g1f3 d7d6 d2d4 c5d4 f3d4 g8f6 b1c3 a7a6",
        "e2e4 c7c5 g1f3 b8c6 d2d4 c5d4 f3d4 g8f6 b1c3",
        "e2e4 e7e6 d2d4 d7d5 b1c3 g8f6",
        "e2e4 c7c6 d2d4 d7d5 b1c3 d5e4 c3e4",
        "d2d4 d7d5 c2c4 e7e6 b1c3 g8f6 c1g5",
        "d2d4 d7d5 c2c4 c7c6 g1f3 g8f6 b1c3",
        "d2d4 g8f6 c2c4 e7e6 g1f3 d7d5 b1c3",
        "d2d4 g8f6 c2c4 g7g6 b1c3 f8g7 e2e4 d7d6",
        "c2c4 e7e5 b1c3 g8f6 g1f3 b8c6",
        "g1f3 d7d5 d2d4 g8f6 c2c4 e7e6"
    };

    private static readonly Lazy<Dictionary<ulong, List<string>>> Book = new(Build);

    private static Dictionary<ulong, List<string>> Build()
    {
        var book = new Dictionary<ulong, List<string>>();
        foreach (var line in Lines)
        {
            var position = Position.Start();
            foreach (var uci in line.Split(' '))
            {
                if (!book.TryGetValue(position.Hash, out var moves)) book[position.Hash] = moves = new List<string>();
                if (!moves.Contains(uci)) moves.Add(uci);
                var move = position.FindLegal(uci) ?? throw new InvalidOperationException($"Book move {uci} is illegal in {line}.");
                position.Make(move);
            }
        }
        return book;
    }

    public static Move? Pick(Position position, Random random)
    {
        if (!Book.Value.TryGetValue(position.Hash, out var moves)) return null;
        return position.FindLegal(moves[random.Next(moves.Count)]);
    }

    internal static int PositionCount => Book.Value.Count;
}
