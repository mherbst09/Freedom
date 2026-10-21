using System.Text;

namespace ViennaAtomicClock.Chess;

public enum PieceType : byte { None, Pawn, Knight, Bishop, Rook, Queen, King }

public enum Side : byte { White, Black }

/// <summary>A piece is one int: the type in the low three bits, bit 8 set for black. 0 is an empty square.</summary>
public static class Piece
{
    public const int None = 0;
    public const int Black = 8;

    public static int Make(Side side, PieceType type) => (int)type | (side == Side.Black ? Black : 0);
    public static PieceType Type(int piece) => (PieceType)(piece & 7);
    public static Side SideOf(int piece) => (piece & Black) != 0 ? Side.Black : Side.White;
    public static Side Other(Side side) => side == Side.White ? Side.Black : Side.White;

    /// <summary>FEN letter: upper case for white.</summary>
    public static char ToChar(int piece)
    {
        var c = " pnbrqk"[(int)Type(piece)];
        return SideOf(piece) == Side.White ? char.ToUpperInvariant(c) : c;
    }

    public static int FromChar(char c)
    {
        var index = "pnbrqk".IndexOf(char.ToLowerInvariant(c));
        if (index < 0) return None;
        return Make(char.IsUpper(c) ? Side.White : Side.Black, (PieceType)(index + 1));
    }
}

/// <summary>Squares are 0..63: a1 = 0, h1 = 7, a8 = 56.</summary>
public static class Square
{
    public static int File(int square) => square & 7;
    public static int Rank(int square) => square >> 3;
    public static int Of(int file, int rank) => rank * 8 + file;
    public static string Name(int square) => $"{(char)('a' + File(square))}{Rank(square) + 1}";
    public static bool IsLight(int square) => ((File(square) + Rank(square)) & 1) == 1;

    public static int Parse(string name)
    {
        if (name.Length != 2 || name[0] < 'a' || name[0] > 'h' || name[1] < '1' || name[1] > '8') return -1;
        return Of(name[0] - 'a', name[1] - '1');
    }
}

[Flags]
public enum MoveFlags : byte
{
    None = 0,
    Capture = 1,
    DoublePush = 2,
    EnPassant = 4,
    CastleKing = 8,
    CastleQueen = 16
}

public readonly record struct Move(int From, int To, PieceType Promotion = PieceType.None, MoveFlags Flags = MoveFlags.None)
{
    public bool IsCapture => (Flags & MoveFlags.Capture) != 0;
    public bool IsCastle => (Flags & (MoveFlags.CastleKing | MoveFlags.CastleQueen)) != 0;
    public bool IsPromotion => Promotion != PieceType.None;
    public bool IsNull => From == To;

    /// <summary>Long algebraic form as used by engines, e.g. e2e4 or e7e8q.</summary>
    public string Uci => Square.Name(From) + Square.Name(To) + (IsPromotion ? " pnbrqk"[(int)Promotion].ToString() : "");

    /// <summary>Packs the move into one int (for the transposition table).</summary>
    public int Pack() => From | To << 6 | (int)Promotion << 12 | (int)Flags << 16;
    public static Move Unpack(int packed) => new(packed & 63, packed >> 6 & 63, (PieceType)(packed >> 12 & 7), (MoveFlags)(packed >> 16 & 255));

    public override string ToString() => Uci;
}

/// <summary>What a move changed, so it can be taken back exactly.</summary>
public readonly record struct Undo(int Captured, int Castling, int EnPassant, int Halfmove, ulong Hash);

/// <summary>
/// A chess position (board, side to move, castling rights, en-passant square, move counters) with move generation.
/// Only legal moves leave this class: every generated move is played and taken back to check the own king.
/// </summary>
public sealed class Position
{
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    public const int WhiteKingside = 1, WhiteQueenside = 2, BlackKingside = 4, BlackQueenside = 8;

    public readonly int[] Board = new int[64];
    public Side ToMove { get; private set; }
    /// <summary>Castling rights as WhiteKingside | WhiteQueenside | BlackKingside | BlackQueenside.</summary>
    public int Castling { get; private set; }
    /// <summary>Square a pawn may capture onto en passant, or -1.</summary>
    public int EnPassant { get; private set; } = -1;
    /// <summary>Half moves since the last capture or pawn move (50-move rule).</summary>
    public int Halfmove { get; private set; }
    public int Fullmove { get; private set; } = 1;
    public ulong Hash { get; private set; }
    private readonly int[] _kings = new int[2];

    public int KingSquare(Side side) => _kings[(int)side];

    // ───────────────────────── static tables ─────────────────────────

    private static readonly int[][] KnightTargets = new int[64][];
    private static readonly int[][] KingTargets = new int[64][];
    /// <summary>Rays[square][direction]: squares in that direction until the edge. Directions 0-3 straight, 4-7 diagonal.</summary>
    private static readonly int[][][] Rays = new int[64][][];
    private static readonly (int File, int Rank)[] Directions = { (0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (-1, 1), (1, -1), (-1, -1) };
    private static readonly int[] CastleMask = new int[64];

    private static readonly ulong[,] PieceKeys = new ulong[16, 64];
    private static readonly ulong[] CastleKeys = new ulong[16];
    private static readonly ulong[] EnPassantKeys = new ulong[8];
    private static readonly ulong SideKey;

    static Position()
    {
        for (var sq = 0; sq < 64; sq++)
        {
            int f = Square.File(sq), r = Square.Rank(sq);
            KnightTargets[sq] = Jumps(f, r, new[] { (1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2) });
            KingTargets[sq] = Jumps(f, r, Directions);
            Rays[sq] = new int[8][];
            for (var d = 0; d < 8; d++)
            {
                var ray = new List<int>();
                for (int x = f + Directions[d].File, y = r + Directions[d].Rank; x is >= 0 and < 8 && y is >= 0 and < 8; x += Directions[d].File, y += Directions[d].Rank)
                    ray.Add(Square.Of(x, y));
                Rays[sq][d] = ray.ToArray();
            }
            CastleMask[sq] = 15;
        }
        CastleMask[Square.Of(4, 0)] = 15 & ~(WhiteKingside | WhiteQueenside);
        CastleMask[Square.Of(7, 0)] = 15 & ~WhiteKingside;
        CastleMask[Square.Of(0, 0)] = 15 & ~WhiteQueenside;
        CastleMask[Square.Of(4, 7)] = 15 & ~(BlackKingside | BlackQueenside);
        CastleMask[Square.Of(7, 7)] = 15 & ~BlackKingside;
        CastleMask[Square.Of(0, 7)] = 15 & ~BlackQueenside;

        // Fixed seed: the same position always has the same hash, also across runs.
        var random = new Random(20261021);
        ulong Next() => (ulong)random.NextInt64() ^ (ulong)random.NextInt64() << 1;
        for (var p = 0; p < 16; p++)
            for (var sq = 0; sq < 64; sq++)
                PieceKeys[p, sq] = Next();
        for (var i = 0; i < 16; i++) CastleKeys[i] = Next();
        for (var i = 0; i < 8; i++) EnPassantKeys[i] = Next();
        SideKey = Next();
    }

    private static int[] Jumps(int f, int r, (int File, int Rank)[] steps) =>
        steps.Select(s => (x: f + s.File, y: r + s.Rank)).Where(p => p.x is >= 0 and < 8 && p.y is >= 0 and < 8).Select(p => Square.Of(p.x, p.y)).ToArray();

    // ───────────────────────── FEN ─────────────────────────

    public static Position Start() => FromFen(StartFen);

    public static Position FromFen(string fen)
    {
        var parts = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new FormatException("FEN needs at least the board and the side to move.");
        var position = new Position();
        var rows = parts[0].Split('/');
        if (rows.Length != 8) throw new FormatException("FEN board needs 8 rows.");
        int whiteKings = 0, blackKings = 0;
        for (var row = 0; row < 8; row++)
        {
            var file = 0;
            foreach (var c in rows[row])
            {
                if (char.IsDigit(c)) { file += c - '0'; continue; }
                var piece = Piece.FromChar(c);
                if (piece == Piece.None || file > 7) throw new FormatException($"Bad FEN piece '{c}'.");
                var sq = Square.Of(file, 7 - row);
                position.Board[sq] = piece;
                if (Piece.Type(piece) == PieceType.King)
                {
                    position._kings[(int)Piece.SideOf(piece)] = sq;
                    if (Piece.SideOf(piece) == Side.White) whiteKings++; else blackKings++;
                }
                file++;
            }
            if (file != 8) throw new FormatException("FEN row does not have 8 squares.");
        }
        if (whiteKings != 1 || blackKings != 1) throw new FormatException("Each side needs exactly one king.");
        position.ToMove = parts[1] == "b" ? Side.Black : Side.White;
        if (parts.Length > 2)
            foreach (var c in parts[2])
                position.Castling |= c switch { 'K' => WhiteKingside, 'Q' => WhiteQueenside, 'k' => BlackKingside, 'q' => BlackQueenside, _ => 0 };
        position.Castling &= position.PossibleCastling();
        if (parts.Length > 3) position.EnPassant = Square.Parse(parts[3]);
        if (parts.Length > 4 && int.TryParse(parts[4], out var half)) position.Halfmove = Math.Max(0, half);
        if (parts.Length > 5 && int.TryParse(parts[5], out var full)) position.Fullmove = Math.Max(1, full);
        position.Hash = position.ComputeHash();
        return position;
    }

    /// <summary>Drops castling rights whose king or rook is not on its start square (keeps hand-written FENs sane).</summary>
    private int PossibleCastling()
    {
        var rights = 0;
        if (Board[4] == Piece.Make(Side.White, PieceType.King))
        {
            if (Board[7] == Piece.Make(Side.White, PieceType.Rook)) rights |= WhiteKingside;
            if (Board[0] == Piece.Make(Side.White, PieceType.Rook)) rights |= WhiteQueenside;
        }
        if (Board[60] == Piece.Make(Side.Black, PieceType.King))
        {
            if (Board[63] == Piece.Make(Side.Black, PieceType.Rook)) rights |= BlackKingside;
            if (Board[56] == Piece.Make(Side.Black, PieceType.Rook)) rights |= BlackQueenside;
        }
        return rights;
    }

    public string ToFen()
    {
        var text = new StringBuilder();
        for (var rank = 7; rank >= 0; rank--)
        {
            var empty = 0;
            for (var file = 0; file < 8; file++)
            {
                var piece = Board[Square.Of(file, rank)];
                if (piece == Piece.None) { empty++; continue; }
                if (empty > 0) { text.Append(empty); empty = 0; }
                text.Append(Piece.ToChar(piece));
            }
            if (empty > 0) text.Append(empty);
            if (rank > 0) text.Append('/');
        }
        text.Append(ToMove == Side.White ? " w " : " b ");
        if (Castling == 0) text.Append('-');
        if ((Castling & WhiteKingside) != 0) text.Append('K');
        if ((Castling & WhiteQueenside) != 0) text.Append('Q');
        if ((Castling & BlackKingside) != 0) text.Append('k');
        if ((Castling & BlackQueenside) != 0) text.Append('q');
        text.Append(' ').Append(EnPassant >= 0 ? Square.Name(EnPassant) : "-");
        text.Append(' ').Append(Halfmove).Append(' ').Append(Fullmove);
        return text.ToString();
    }

    public Position Clone()
    {
        var copy = new Position { ToMove = ToMove, Castling = Castling, EnPassant = EnPassant, Halfmove = Halfmove, Fullmove = Fullmove, Hash = Hash };
        Array.Copy(Board, copy.Board, 64);
        copy._kings[0] = _kings[0];
        copy._kings[1] = _kings[1];
        return copy;
    }

    // ───────────────────────── hashing ─────────────────────────

    /// <summary>Hash from scratch. The en-passant file only counts when a pawn could really capture there,
    /// so repetitions are detected the way the rules define "the same position".</summary>
    public ulong ComputeHash()
    {
        ulong hash = 0;
        for (var sq = 0; sq < 64; sq++)
            if (Board[sq] != Piece.None) hash ^= PieceKeys[Board[sq], sq];
        hash ^= CastleKeys[Castling];
        if (EnPassantMatters()) hash ^= EnPassantKeys[Square.File(EnPassant)];
        if (ToMove == Side.Black) hash ^= SideKey;
        return hash;
    }

    private bool EnPassantMatters()
    {
        if (EnPassant < 0) return false;
        var pawn = Piece.Make(ToMove, PieceType.Pawn);
        var rank = ToMove == Side.White ? 4 : 3;
        var file = Square.File(EnPassant);
        return (file > 0 && Board[Square.Of(file - 1, rank)] == pawn) || (file < 7 && Board[Square.Of(file + 1, rank)] == pawn);
    }

    // ───────────────────────── making moves ─────────────────────────

    public Undo Make(Move move)
    {
        var side = ToMove;
        var piece = Board[move.From];
        var captured = Board[move.To];
        var undo = new Undo(captured, Castling, EnPassant, Halfmove, Hash);
        var hash = Hash;
        if (EnPassantMatters()) hash ^= EnPassantKeys[Square.File(EnPassant)];
        hash ^= CastleKeys[Castling];

        hash ^= PieceKeys[piece, move.From];
        Board[move.From] = Piece.None;
        if ((move.Flags & MoveFlags.EnPassant) != 0)
        {
            var victim = move.To + (side == Side.White ? -8 : 8);
            captured = Board[victim];
            hash ^= PieceKeys[captured, victim];
            Board[victim] = Piece.None;
            undo = undo with { Captured = captured };
        }
        else if (captured != Piece.None) hash ^= PieceKeys[captured, move.To];

        var placed = move.IsPromotion ? Piece.Make(side, move.Promotion) : piece;
        Board[move.To] = placed;
        hash ^= PieceKeys[placed, move.To];

        if (move.IsCastle)
        {
            var (rookFrom, rookTo) = (move.Flags & MoveFlags.CastleKing) != 0 ? (move.From + 3, move.From + 1) : (move.From - 4, move.From - 1);
            var rook = Board[rookFrom];
            Board[rookFrom] = Piece.None;
            Board[rookTo] = rook;
            hash ^= PieceKeys[rook, rookFrom] ^ PieceKeys[rook, rookTo];
        }
        if (Piece.Type(piece) == PieceType.King) _kings[(int)side] = move.To;

        Castling &= CastleMask[move.From] & CastleMask[move.To];
        hash ^= CastleKeys[Castling];
        EnPassant = (move.Flags & MoveFlags.DoublePush) != 0 ? (move.From + move.To) / 2 : -1;
        Halfmove = Piece.Type(piece) == PieceType.Pawn || captured != Piece.None ? 0 : Halfmove + 1;
        if (side == Side.Black) Fullmove++;
        ToMove = Piece.Other(side);
        hash ^= SideKey;
        if (EnPassantMatters()) hash ^= EnPassantKeys[Square.File(EnPassant)];
        Hash = hash;
        return undo;
    }

    public void Unmake(Move move, Undo undo)
    {
        ToMove = Piece.Other(ToMove);
        var side = ToMove;
        var placed = Board[move.To];
        var piece = move.IsPromotion ? Piece.Make(side, PieceType.Pawn) : placed;
        Board[move.From] = piece;
        if ((move.Flags & MoveFlags.EnPassant) != 0)
        {
            Board[move.To] = Piece.None;
            Board[move.To + (side == Side.White ? -8 : 8)] = undo.Captured;
        }
        else Board[move.To] = undo.Captured;

        if (move.IsCastle)
        {
            var (rookFrom, rookTo) = (move.Flags & MoveFlags.CastleKing) != 0 ? (move.From + 3, move.From + 1) : (move.From - 4, move.From - 1);
            Board[rookFrom] = Board[rookTo];
            Board[rookTo] = Piece.None;
        }
        if (Piece.Type(piece) == PieceType.King) _kings[(int)side] = move.From;
        if (side == Side.Black) Fullmove--;
        Castling = undo.Castling;
        EnPassant = undo.EnPassant;
        Halfmove = undo.Halfmove;
        Hash = undo.Hash;
    }

    /// <summary>Passes the turn (used by the search for null-move pruning only).</summary>
    public Undo MakeNull()
    {
        var undo = new Undo(Piece.None, Castling, EnPassant, Halfmove, Hash);
        var hash = Hash;
        if (EnPassantMatters()) hash ^= EnPassantKeys[Square.File(EnPassant)];
        EnPassant = -1;
        ToMove = Piece.Other(ToMove);
        Hash = hash ^ SideKey;
        Halfmove++;
        return undo;
    }

    public void UnmakeNull(Undo undo)
    {
        ToMove = Piece.Other(ToMove);
        EnPassant = undo.EnPassant;
        Halfmove = undo.Halfmove;
        Hash = undo.Hash;
    }

    // ───────────────────────── attacks ─────────────────────────

    /// <summary>True when a piece of <paramref name="by"/> attacks the square.</summary>
    public bool IsAttacked(int square, Side by)
    {
        int f = Square.File(square), r = Square.Rank(square);
        var pawn = Piece.Make(by, PieceType.Pawn);
        var pawnRank = by == Side.White ? r - 1 : r + 1;
        if (pawnRank is >= 0 and < 8)
        {
            if (f > 0 && Board[Square.Of(f - 1, pawnRank)] == pawn) return true;
            if (f < 7 && Board[Square.Of(f + 1, pawnRank)] == pawn) return true;
        }
        var knight = Piece.Make(by, PieceType.Knight);
        foreach (var t in KnightTargets[square])
            if (Board[t] == knight) return true;
        var king = Piece.Make(by, PieceType.King);
        foreach (var t in KingTargets[square])
            if (Board[t] == king) return true;
        var rook = Piece.Make(by, PieceType.Rook);
        var bishop = Piece.Make(by, PieceType.Bishop);
        var queen = Piece.Make(by, PieceType.Queen);
        var rays = Rays[square];
        for (var d = 0; d < 8; d++)
        {
            var slider = d < 4 ? rook : bishop;
            foreach (var t in rays[d])
            {
                var p = Board[t];
                if (p == Piece.None) continue;
                if (p == slider || p == queen) return true;
                break;
            }
        }
        return false;
    }

    public bool InCheck() => IsAttacked(_kings[(int)ToMove], Piece.Other(ToMove));

    /// <summary>True when the side that just moved left its own king in check (the move was illegal).</summary>
    public bool LeftKingInCheck() => IsAttacked(_kings[(int)Piece.Other(ToMove)], ToMove);

    // ───────────────────────── move generation ─────────────────────────

    /// <summary>Moves that follow the piece rules but may leave the own king in check.
    /// With <paramref name="tacticalOnly"/> only captures and queen promotions are produced (for the quiescence search).</summary>
    public void GeneratePseudoLegal(List<Move> moves, bool tacticalOnly = false)
    {
        var side = ToMove;
        var enemy = Piece.Other(side);
        for (var sq = 0; sq < 64; sq++)
        {
            var piece = Board[sq];
            if (piece == Piece.None || Piece.SideOf(piece) != side) continue;
            switch (Piece.Type(piece))
            {
                case PieceType.Pawn:
                    PawnMoves(moves, sq, side, tacticalOnly);
                    break;
                case PieceType.Knight:
                    Targets(moves, sq, KnightTargets[sq], enemy, tacticalOnly);
                    break;
                case PieceType.King:
                    Targets(moves, sq, KingTargets[sq], enemy, tacticalOnly);
                    if (!tacticalOnly) CastlingMoves(moves, sq, side);
                    break;
                case PieceType.Bishop:
                    Slides(moves, sq, 4, 8, enemy, tacticalOnly);
                    break;
                case PieceType.Rook:
                    Slides(moves, sq, 0, 4, enemy, tacticalOnly);
                    break;
                case PieceType.Queen:
                    Slides(moves, sq, 0, 8, enemy, tacticalOnly);
                    break;
            }
        }
    }

    private void Targets(List<Move> moves, int from, int[] targets, Side enemy, bool tacticalOnly)
    {
        foreach (var to in targets)
        {
            var p = Board[to];
            if (p == Piece.None) { if (!tacticalOnly) moves.Add(new Move(from, to)); }
            else if (Piece.SideOf(p) == enemy) moves.Add(new Move(from, to, PieceType.None, MoveFlags.Capture));
        }
    }

    private void Slides(List<Move> moves, int from, int firstDirection, int endDirection, Side enemy, bool tacticalOnly)
    {
        var rays = Rays[from];
        for (var d = firstDirection; d < endDirection; d++)
            foreach (var to in rays[d])
            {
                var p = Board[to];
                if (p == Piece.None)
                {
                    if (!tacticalOnly) moves.Add(new Move(from, to));
                    continue;
                }
                if (Piece.SideOf(p) == enemy) moves.Add(new Move(from, to, PieceType.None, MoveFlags.Capture));
                break;
            }
    }

    private static readonly PieceType[] PromotionTypes = { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight };

    private void PawnMoves(List<Move> moves, int from, Side side, bool tacticalOnly)
    {
        var step = side == Side.White ? 8 : -8;
        var startRank = side == Side.White ? 1 : 6;
        var lastRank = side == Side.White ? 7 : 0;
        int f = Square.File(from), r = Square.Rank(from);
        var one = from + step;
        if (Board[one] == Piece.None)
        {
            if (Square.Rank(one) == lastRank) AddPromotions(moves, from, one, MoveFlags.None, tacticalOnly);
            else if (!tacticalOnly)
            {
                moves.Add(new Move(from, one));
                if (r == startRank && Board[one + step] == Piece.None) moves.Add(new Move(from, one + step, PieceType.None, MoveFlags.DoublePush));
            }
        }
        foreach (var df in new[] { -1, 1 })
        {
            if (f + df is < 0 or > 7) continue;
            var to = one + df;
            var target = Board[to];
            if (target != Piece.None && Piece.SideOf(target) != side)
            {
                if (Square.Rank(to) == lastRank) AddPromotions(moves, from, to, MoveFlags.Capture, false);
                else moves.Add(new Move(from, to, PieceType.None, MoveFlags.Capture));
            }
            else if (to == EnPassant && target == Piece.None)
                moves.Add(new Move(from, to, PieceType.None, MoveFlags.Capture | MoveFlags.EnPassant));
        }
    }

    private static void AddPromotions(List<Move> moves, int from, int to, MoveFlags flags, bool queenOnly)
    {
        foreach (var type in PromotionTypes)
        {
            moves.Add(new Move(from, to, type, flags));
            if (queenOnly) break;
        }
    }

    private void CastlingMoves(List<Move> moves, int king, Side side)
    {
        var (kingside, queenside, home) = side == Side.White ? (WhiteKingside, WhiteQueenside, 4) : (BlackKingside, BlackQueenside, 60);
        if (king != home || (Castling & (kingside | queenside)) == 0) return;
        var enemy = Piece.Other(side);
        if (IsAttacked(king, enemy)) return; // never out of check
        if ((Castling & kingside) != 0 && Board[home + 1] == Piece.None && Board[home + 2] == Piece.None
            && !IsAttacked(home + 1, enemy) && !IsAttacked(home + 2, enemy))
            moves.Add(new Move(home, home + 2, PieceType.None, MoveFlags.CastleKing));
        if ((Castling & queenside) != 0 && Board[home - 1] == Piece.None && Board[home - 2] == Piece.None && Board[home - 3] == Piece.None
            && !IsAttacked(home - 1, enemy) && !IsAttacked(home - 2, enemy))
            moves.Add(new Move(home, home - 2, PieceType.None, MoveFlags.CastleQueen));
    }

    /// <summary>All legal moves: pseudo-legal moves that do not leave the own king in check.</summary>
    public List<Move> LegalMoves()
    {
        var moves = new List<Move>(48);
        GenerateLegal(moves);
        return moves;
    }

    public void GenerateLegal(List<Move> moves)
    {
        moves.Clear();
        GeneratePseudoLegal(moves);
        var write = 0;
        for (var i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            var undo = Make(move);
            var legal = !LeftKingInCheck();
            Unmake(move, undo);
            if (legal) moves[write++] = move;
        }
        moves.RemoveRange(write, moves.Count - write);
    }

    public bool HasLegalMove()
    {
        var moves = new List<Move>(48);
        GeneratePseudoLegal(moves);
        foreach (var move in moves)
        {
            var undo = Make(move);
            var legal = !LeftKingInCheck();
            Unmake(move, undo);
            if (legal) return true;
        }
        return false;
    }

    /// <summary>Finds the legal move written in UCI form (e2e4, e7e8q), or null.</summary>
    public Move? FindLegal(string uci)
    {
        foreach (var move in LegalMoves())
            if (move.Uci == uci || (move.IsPromotion && uci.Length == 4 && move.Promotion == PieceType.Queen && move.Uci[..4] == uci))
                return move;
        return null;
    }

    // ───────────────────────── draws ─────────────────────────

    /// <summary>
    /// Neither side can ever mate: only kings, a single knight or bishop, or bishops that all stand on squares
    /// of the same colour.
    /// </summary>
    public bool IsInsufficientMaterial()
    {
        int knights = 0, bishops = 0, lightBishops = 0;
        for (var sq = 0; sq < 64; sq++)
        {
            switch (Piece.Type(Board[sq]))
            {
                case PieceType.Pawn or PieceType.Rook or PieceType.Queen:
                    return false;
                case PieceType.Knight:
                    knights++;
                    break;
                case PieceType.Bishop:
                    bishops++;
                    if (Square.IsLight(sq)) lightBishops++;
                    break;
            }
        }
        if (knights + bishops <= 1) return true;
        return knights == 0 && (lightBishops == 0 || lightBishops == bishops);
    }

    // ───────────────────────── notation ─────────────────────────

    /// <summary>Standard algebraic notation (Nf3, exd5, O-O, e8=Q+, Qh7#). The move must be legal here.</summary>
    public string San(Move move)
    {
        var piece = Board[move.From];
        var type = Piece.Type(piece);
        string text;
        if (move.IsCastle) text = (move.Flags & MoveFlags.CastleKing) != 0 ? "O-O" : "O-O-O";
        else
        {
            var builder = new StringBuilder();
            if (type == PieceType.Pawn)
            {
                if (move.IsCapture) builder.Append((char)('a' + Square.File(move.From)));
            }
            else
            {
                builder.Append(" PNBRQK"[(int)type]);
                // Name the file, the rank or both when another piece of the same kind could go there too.
                var rivals = LegalMoves().Where(m => m.To == move.To && m.From != move.From && Board[m.From] == piece).ToList();
                if (rivals.Count > 0)
                {
                    var sameFile = rivals.Any(m => Square.File(m.From) == Square.File(move.From));
                    var sameRank = rivals.Any(m => Square.Rank(m.From) == Square.Rank(move.From));
                    if (!sameFile) builder.Append((char)('a' + Square.File(move.From)));
                    else if (!sameRank) builder.Append((char)('1' + Square.Rank(move.From)));
                    else builder.Append(Square.Name(move.From));
                }
            }
            if (move.IsCapture) builder.Append('x');
            builder.Append(Square.Name(move.To));
            if (move.IsPromotion) builder.Append('=').Append(" PNBRQK"[(int)move.Promotion]);
            text = builder.ToString();
        }
        var undo = Make(move);
        if (InCheck()) text += HasLegalMove() ? "+" : "#";
        Unmake(move, undo);
        return text;
    }
}
