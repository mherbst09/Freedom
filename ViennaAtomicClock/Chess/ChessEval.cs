namespace ViennaAtomicClock.Chess;

/// <summary>
/// Static evaluation in centipawns from the view of the side to move. The full version looks at material,
/// piece placement (centre control, development), king safety, pawn structure, rook files and mobility;
/// the simple one (medium bot) only at material, placement and development.
/// </summary>
public static class ChessEval
{
    public static readonly int[] Values = { 0, 100, 320, 330, 500, 900, 0 };

    // Placement tables from White's view, rank 8 in the first row (a8 … h8), so they read like a board.
    private static readonly int[] PawnTable =
    {
         0,  0,  0,  0,  0,  0,  0,  0,
        50, 50, 50, 50, 50, 50, 50, 50,
        10, 10, 20, 30, 30, 20, 10, 10,
         5,  5, 10, 25, 25, 10,  5,  5,
         0,  0,  0, 20, 20,  0,  0,  0,
         5, -5,-10,  0,  0,-10, -5,  5,
         5, 10, 10,-20,-20, 10, 10,  5,
         0,  0,  0,  0,  0,  0,  0,  0
    };
    private static readonly int[] KnightTable =
    {
        -50,-40,-30,-30,-30,-30,-40,-50,
        -40,-20,  0,  0,  0,  0,-20,-40,
        -30,  0, 10, 15, 15, 10,  0,-30,
        -30,  5, 15, 20, 20, 15,  5,-30,
        -30,  0, 15, 20, 20, 15,  0,-30,
        -30,  5, 10, 15, 15, 10,  5,-30,
        -40,-20,  0,  5,  5,  0,-20,-40,
        -50,-40,-30,-30,-30,-30,-40,-50
    };
    private static readonly int[] BishopTable =
    {
        -20,-10,-10,-10,-10,-10,-10,-20,
        -10,  0,  0,  0,  0,  0,  0,-10,
        -10,  0,  5, 10, 10,  5,  0,-10,
        -10,  5,  5, 10, 10,  5,  5,-10,
        -10,  0, 10, 10, 10, 10,  0,-10,
        -10, 10, 10, 10, 10, 10, 10,-10,
        -10,  5,  0,  0,  0,  0,  5,-10,
        -20,-10,-10,-10,-10,-10,-10,-20
    };
    private static readonly int[] RookTable =
    {
         0,  0,  0,  0,  0,  0,  0,  0,
         5, 10, 10, 10, 10, 10, 10,  5,
        -5,  0,  0,  0,  0,  0,  0, -5,
        -5,  0,  0,  0,  0,  0,  0, -5,
        -5,  0,  0,  0,  0,  0,  0, -5,
        -5,  0,  0,  0,  0,  0,  0, -5,
        -5,  0,  0,  0,  0,  0,  0, -5,
         0,  0,  0,  5,  5,  0,  0,  0
    };
    private static readonly int[] QueenTable =
    {
        -20,-10,-10, -5, -5,-10,-10,-20,
        -10,  0,  0,  0,  0,  0,  0,-10,
        -10,  0,  5,  5,  5,  5,  0,-10,
         -5,  0,  5,  5,  5,  5,  0, -5,
          0,  0,  5,  5,  5,  5,  0, -5,
        -10,  5,  5,  5,  5,  5,  0,-10,
        -10,  0,  5,  0,  0,  0,  0,-10,
        -20,-10,-10, -5, -5,-10,-10,-20
    };
    private static readonly int[] KingMiddleTable =
    {
        -30,-40,-40,-50,-50,-40,-40,-30,
        -30,-40,-40,-50,-50,-40,-40,-30,
        -30,-40,-40,-50,-50,-40,-40,-30,
        -30,-40,-40,-50,-50,-40,-40,-30,
        -20,-30,-30,-40,-40,-30,-30,-20,
        -10,-20,-20,-20,-20,-20,-20,-10,
         20, 20,  0,  0,  0,  0, 20, 20,
         20, 30, 10,  0,  0, 10, 30, 20
    };
    private static readonly int[] KingEndTable =
    {
        -50,-40,-30,-20,-20,-30,-40,-50,
        -30,-20,-10,  0,  0,-10,-20,-30,
        -30,-10, 20, 30, 30, 20,-10,-30,
        -30,-10, 30, 40, 40, 30,-10,-30,
        -30,-10, 30, 40, 40, 30,-10,-30,
        -30,-10, 20, 30, 30, 20,-10,-30,
        -30,-30,  0,  0,  0,  0,-30,-30,
        -50,-30,-30,-30,-30,-30,-30,-50
    };
    private static readonly int[][] Tables = { Array.Empty<int>(), PawnTable, KnightTable, BishopTable, RookTable, QueenTable, KingMiddleTable };
    private static readonly int[] PhaseWeight = { 0, 0, 1, 1, 2, 4, 0 };
    private static readonly int[] PassedBonus = { 0, 10, 15, 25, 45, 75, 120, 0 };
    private static readonly int[] KnightJumps = { 17, 15, 10, 6, -6, -10, -15, -17 };

    /// <summary>Index into a table for a piece of <paramref name="side"/> on <paramref name="square"/>.</summary>
    private static int TableIndex(int square, Side side) =>
        side == Side.White ? (7 - Square.Rank(square)) * 8 + Square.File(square) : square;

    /// <summary>0 = only kings and pawns left, 24 = all pieces on the board.</summary>
    public static int Phase(Position position)
    {
        var phase = 0;
        foreach (var piece in position.Board) phase += PhaseWeight[(int)Piece.Type(piece)];
        return Math.Min(24, phase);
    }

    public static int Simple(Position position)
    {
        var score = 0;
        var phase = Phase(position);
        for (var sq = 0; sq < 64; sq++)
        {
            var piece = position.Board[sq];
            if (piece == Piece.None) continue;
            var side = Piece.SideOf(piece);
            var type = Piece.Type(piece);
            var value = Values[(int)type] + Placement(type, TableIndex(sq, side), phase);
            score += side == Side.White ? value : -value;
        }
        score += Development(position, Side.White) - Development(position, Side.Black);
        return position.ToMove == Side.White ? score : -score;
    }

    private static int Placement(PieceType type, int index, int phase)
    {
        if (type != PieceType.King) return Tables[(int)type][index];
        return (KingMiddleTable[index] * phase + KingEndTable[index] * (24 - phase)) / 24;
    }

    /// <summary>Penalty for knights and bishops still at home and a queen out too early, while the opening lasts.</summary>
    private static int Development(Position position, Side side)
    {
        if (position.Fullmove > 14) return 0;
        var home = side == Side.White ? 0 : 56;
        var penalty = 0;
        foreach (var file in new[] { 1, 2, 5, 6 })
        {
            var type = Piece.Type(position.Board[home + file]);
            if (Piece.SideOf(position.Board[home + file]) == side && type is PieceType.Knight or PieceType.Bishop) penalty += 12;
        }
        var queen = Piece.Make(side, PieceType.Queen);
        if (position.Board[home + 3] != queen && penalty >= 24 && Array.IndexOf(position.Board, queen) >= 0) penalty += 15;
        return -penalty;
    }

    public static int Full(Position position)
    {
        var board = position.Board;
        var phase = Phase(position);
        Span<int> pawnFiles = stackalloc int[16]; // [side * 8 + file]
        Span<int> bishops = stackalloc int[2];
        var score = 0;

        for (var sq = 0; sq < 64; sq++)
        {
            var piece = board[sq];
            if (piece == Piece.None) continue;
            var side = Piece.SideOf(piece);
            var type = Piece.Type(piece);
            var value = Values[(int)type] + Placement(type, TableIndex(sq, side), phase);
            if (type == PieceType.Pawn) pawnFiles[(int)side * 8 + Square.File(sq)]++;
            else if (type == PieceType.Bishop) bishops[(int)side]++;
            score += side == Side.White ? value : -value;
        }

        for (var sq = 0; sq < 64; sq++)
        {
            var piece = board[sq];
            if (piece == Piece.None) continue;
            var side = Piece.SideOf(piece);
            var sign = side == Side.White ? 1 : -1;
            var own = (int)side * 8;
            var enemy = (1 - (int)side) * 8;
            var file = Square.File(sq);
            switch (Piece.Type(piece))
            {
                case PieceType.Pawn:
                    score += sign * PawnStructure(board, sq, side, file, pawnFiles, own, phase);
                    break;
                case PieceType.Rook:
                    if (pawnFiles[own + file] == 0) score += sign * (pawnFiles[enemy + file] == 0 ? 20 : 10);
                    score += sign * Mobility(board, sq, side, 0, 4) * 2;
                    break;
                case PieceType.Queen:
                    score += sign * Mobility(board, sq, side, 0, 8);
                    break;
                case PieceType.Bishop:
                    score += sign * Mobility(board, sq, side, 4, 8) * 4;
                    break;
                case PieceType.Knight:
                    score += sign * KnightMobility(board, sq, side) * 4;
                    break;
            }
        }

        if (bishops[0] >= 2) score += 30;
        if (bishops[1] >= 2) score -= 30;
        score += (KingSafety(position, Side.White, pawnFiles) - KingSafety(position, Side.Black, pawnFiles)) * phase / 24;
        score += Development(position, Side.White) - Development(position, Side.Black);
        var tempo = 10;
        return (position.ToMove == Side.White ? score : -score) + tempo;
    }

    private static int PawnStructure(int[] board, int sq, Side side, int file, Span<int> pawnFiles, int own, int phase)
    {
        var score = 0;
        if (pawnFiles[own + file] > 1) score -= 12; // doubled (counted for each pawn of the pair)
        var left = file > 0 ? pawnFiles[own + file - 1] : 0;
        var right = file < 7 ? pawnFiles[own + file + 1] : 0;
        if (left == 0 && right == 0) score -= 15; // isolated
        // Passed: no enemy pawn ahead on this or a neighbouring file.
        var enemyPawn = Piece.Make(Piece.Other(side), PieceType.Pawn);
        var rank = Square.Rank(sq);
        var step = side == Side.White ? 1 : -1;
        for (var r = rank + step; r is > 0 and < 7; r += step)
            for (var f = Math.Max(0, file - 1); f <= Math.Min(7, file + 1); f++)
                if (board[Square.Of(f, r)] == enemyPawn) return score;
        var advanced = side == Side.White ? rank : 7 - rank;
        // Passed pawns matter most once the pieces are gone.
        return score + PassedBonus[advanced] * (48 - phase) / 48;
    }

    private static readonly (int File, int Rank)[] Steps = { (0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (-1, 1), (1, -1), (-1, -1) };

    /// <summary>Squares the slider reaches that are empty or hold an enemy piece, minus a small base.</summary>
    private static int Mobility(int[] board, int sq, Side side, int firstDirection, int endDirection)
    {
        var count = 0;
        int f = Square.File(sq), r = Square.Rank(sq);
        for (var d = firstDirection; d < endDirection; d++)
        {
            for (int x = f + Steps[d].File, y = r + Steps[d].Rank; x is >= 0 and < 8 && y is >= 0 and < 8; x += Steps[d].File, y += Steps[d].Rank)
            {
                var p = board[Square.Of(x, y)];
                if (p == Piece.None) { count++; continue; }
                if (Piece.SideOf(p) != side) count++;
                break;
            }
        }
        return count - (endDirection - firstDirection);
    }

    private static int KnightMobility(int[] board, int sq, Side side)
    {
        var count = 0;
        int f = Square.File(sq);
        foreach (var jump in KnightJumps)
        {
            var to = sq + jump;
            if (to is < 0 or > 63 || Math.Abs(Square.File(to) - f) > 2) continue;
            var p = board[to];
            if (p == Piece.None || Piece.SideOf(p) != side) count++;
        }
        return count - 4;
    }

    /// <summary>Pawn shield in front of the king and open files next to it (weighted by the game phase outside).</summary>
    private static int KingSafety(Position position, Side side, Span<int> pawnFiles)
    {
        var king = position.KingSquare(side);
        int kf = Square.File(king), kr = Square.Rank(king);
        var step = side == Side.White ? 1 : -1;
        var pawn = Piece.Make(side, PieceType.Pawn);
        var score = 0;
        for (var f = Math.Max(0, kf - 1); f <= Math.Min(7, kf + 1); f++)
        {
            var r1 = kr + step;
            var r2 = kr + 2 * step;
            if (r1 is >= 0 and < 8 && position.Board[Square.Of(f, r1)] == pawn) score += 12;
            else if (r2 is >= 0 and < 8 && position.Board[Square.Of(f, r2)] == pawn) score += 6;
            else score -= 8;
            if (pawnFiles[(int)side * 8 + f] == 0) score -= pawnFiles[(1 - (int)side) * 8 + f] == 0 ? 18 : 10;
        }
        return score;
    }
}
