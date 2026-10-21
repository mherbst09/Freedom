using ViennaAtomicClock.Chess;
using Xunit;

namespace Freedom.Tests;

public sealed class ChessRulesTests
{
    private static long Perft(Position position, int depth, bool checkHash = false)
    {
        if (checkHash) Assert.Equal(position.ComputeHash(), position.Hash);
        var moves = position.LegalMoves();
        if (depth == 1) return moves.Count;
        long total = 0;
        foreach (var move in moves)
        {
            var undo = position.Make(move);
            total += Perft(position, depth - 1, checkHash);
            position.Unmake(move, undo);
        }
        return total;
    }

    // Reference counts of all legal move sequences (the standard check for chess move generators).
    [Theory]
    [InlineData(Position.StartFen, new long[] { 20, 400, 8902, 197281 })]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", new long[] { 48, 2039, 97862 })]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", new long[] { 14, 191, 2812, 43238, 674624 })]
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", new long[] { 6, 264, 9467, 422333 })]
    [InlineData("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", new long[] { 44, 1486, 62379 })]
    [InlineData("r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10", new long[] { 46, 2079, 89890 })]
    public void Move_generation_matches_the_reference_counts(string fen, long[] expected)
    {
        var position = Position.FromFen(fen);
        for (var depth = 1; depth <= expected.Length; depth++)
            Assert.Equal(expected[depth - 1], Perft(position, depth));
        Assert.Equal(fen, position.ToFen()); // every move was taken back exactly
    }

    [Fact]
    public void Hash_stays_correct_through_every_move_and_take_back()
    {
        var position = Position.FromFen("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        Assert.Equal(2039, Perft(position, 2, checkHash: true));
        Assert.Equal(position.ComputeHash(), position.Hash);
    }

    private static List<string> Targets(string fen, string from) =>
        Position.FromFen(fen).LegalMoves().Where(m => m.From == Square.Parse(from)).Select(m => Square.Name(m.To)).OrderBy(s => s).ToList();

    [Fact]
    public void Each_piece_moves_only_by_its_rules()
    {
        // Knight in the centre: 8 jumps; at the edge: fewer.
        Assert.Equal(new[] { "c3", "c5", "d2", "d6", "f2", "f6", "g3", "g5" }, Targets("4k3/8/8/8/4N3/8/8/K7 w - - 0 1", "e4"));
        Assert.Equal(new[] { "b3", "c2" }, Targets("4k3/8/8/8/8/8/8/N3K3 w - - 0 1", "a1"));
        // Bishop: diagonals only, stops at own pieces, captures the first enemy piece.
        Assert.Equal(new[] { "a7", "b6", "c5", "e3", "e5", "f2", "f6", "g1", "g7" }, Targets("4k3/6p1/8/8/3B4/2P5/8/4K3 w - - 0 1", "d4"));
        // Rook: lines only.
        Assert.Equal(14, Targets("4k3/8/8/8/3R4/8/8/K7 w - - 0 1", "d4").Count);
        Assert.DoesNotContain("e5", Targets("4k3/8/8/8/3R4/8/8/K7 w - - 0 1", "d4"));
        // Queen: lines and diagonals.
        Assert.Equal(27, Targets("k7/8/8/8/3Q4/8/8/7K w - - 0 1", "d4").Count);
        // King: one square in every direction.
        Assert.Equal(8, Targets("k7/8/8/8/3K4/8/8/8 w - - 0 1", "d4").Count);
        // Pawn: one or two from the start, one later, diagonal only to capture, blocked by any piece in front.
        Assert.Equal(new[] { "e3", "e4" }, Targets(Position.StartFen, "e2"));
        Assert.Equal(new[] { "e4" }, Targets("4k3/8/8/8/8/4P3/8/4K3 w - - 0 1", "e3"));
        Assert.Equal(new[] { "d5", "f5" }, Targets("4k3/8/8/3pnp2/4P3/8/8/K7 w - - 0 1", "e4"));
        Assert.Empty(Targets("4k3/8/8/8/8/4n3/4P3/4K3 w - - 0 1", "e2"));
        Assert.Empty(Targets("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR b KQkq - 0 1", "e2")); // not your turn
    }

    [Fact]
    public void King_cannot_step_into_an_attacked_square()
    {
        // Black rook on the d-file and the 2nd rank: the king on e1 may only go to f1.
        var targets = Targets("4k3/8/8/8/8/8/3r4/4K3 w - - 0 1", "e1");
        Assert.Equal(new[] { "d2", "f1" }, targets.OrderBy(s => s)); // d2 captures the unprotected rook
        targets = Targets("4k3/8/8/8/8/2b5/3r4/4K3 w - - 0 1", "e1"); // now d2 is protected
        Assert.Equal(new[] { "f1" }, targets);
        // Kings may never stand next to each other.
        Assert.DoesNotContain("e3", Targets("8/8/8/8/4k3/8/4K3/8 w - - 0 1", "e2"));
    }

    [Fact]
    public void Pinned_pieces_only_move_along_the_pin()
    {
        // Knight pinned on the e-file: no moves at all.
        Assert.Empty(Targets("4k3/4r3/8/8/8/8/4N3/4K3 w - - 0 1", "e2"));
        // Bishop pinned on a diagonal: may move along it, up to taking the pinning piece.
        Assert.Equal(new[] { "f3", "g4", "h5" }, Targets("4k3/8/8/7b/8/8/4B3/3K4 w - - 0 1", "e2"));
        // Rook pinned on a file can slide along it.
        Assert.Equal(new[] { "e3", "e4", "e5", "e6", "e7" }, Targets("4k3/4r3/8/8/8/8/4R3/4K3 w - - 0 1", "e2"));
        // En passant that would uncover a check along the rank is not allowed.
        var position = Position.FromFen("8/8/8/K2pP2q/8/8/8/7k w - d6 0 1");
        Assert.DoesNotContain(position.LegalMoves(), m => (m.Flags & MoveFlags.EnPassant) != 0);
    }

    [Fact]
    public void Check_checkmate_and_stalemate_are_recognised()
    {
        var game = new ChessGame();
        foreach (var move in new[] { "f2f3", "e7e5", "g2g4" }) Assert.True(game.Play(move));
        Assert.False(game.InCheck);
        Assert.True(game.Play("d8h4")); // fool's mate
        Assert.True(game.InCheck);
        Assert.True(game.IsOver);
        Assert.Equal(GameResult.BlackWins, game.Result);
        Assert.Equal(EndReason.Checkmate, game.Reason);
        Assert.Empty(game.LegalMoves);
        Assert.Equal("Qh4#", game.Moves[^1].San);

        var check = new ChessGame("4k3/8/8/8/8/8/8/R3K3 w Q - 0 1");
        Assert.True(check.Play("a1a8"));
        Assert.True(check.InCheck);
        Assert.False(check.IsOver);
        Assert.Equal("Ra8+", check.Moves[^1].San);
        Assert.All(check.LegalMoves, m => Assert.Equal(Square.Parse("e8"), m.From)); // only the king can help

        var stalemate = new ChessGame("7k/8/6K1/5Q2/8/8/8/8 w - - 0 1");
        Assert.True(stalemate.Play("f5f7"));
        Assert.True(stalemate.IsOver);
        Assert.Equal(GameResult.Draw, stalemate.Result);
        Assert.Equal(EndReason.Stalemate, stalemate.Reason);
    }

    [Fact]
    public void Castling_only_under_the_allowed_conditions()
    {
        const string ready = "r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1";
        var targets = Targets(ready, "e1");
        Assert.Contains("g1", targets);
        Assert.Contains("c1", targets);
        var game = new ChessGame(ready);
        Assert.True(game.Play("e1g1"));
        Assert.Equal(Piece.Make(Side.White, PieceType.Rook), game.Position.Board[Square.Parse("f1")]);
        Assert.Equal(Piece.None, game.Position.Board[Square.Parse("h1")]);
        Assert.Equal("O-O", game.Moves[0].San);
        Assert.True(game.Play("e8c8"));
        Assert.Equal(Piece.Make(Side.Black, PieceType.Rook), game.Position.Board[Square.Parse("d8")]);
        Assert.Equal("O-O-O", game.Moves[1].San);

        // Pieces in between.
        Assert.DoesNotContain("g1", Targets("r3k2r/8/8/8/8/8/8/R3K1NR w KQkq - 0 1", "e1"));
        Assert.DoesNotContain("c1", Targets("r3k2r/8/8/8/8/8/8/RN2K2R w KQkq - 0 1", "e1"));
        // In check.
        Assert.DoesNotContain("g1", Targets("4k3/4r3/8/8/8/8/8/R3K2R w KQ - 0 1", "e1"));
        // Through an attacked square (f1), onto an attacked square (g1).
        Assert.DoesNotContain("g1", Targets("4k3/5r2/8/8/8/8/8/R3K2R w KQ - 0 1", "e1"));
        Assert.DoesNotContain("g1", Targets("4k3/6r1/8/8/8/8/8/R3K2R w KQ - 0 1", "e1"));
        Assert.Contains("c1", Targets("4k3/6r1/8/8/8/8/8/R3K2R w KQ - 0 1", "e1"));
        // b1 may be attacked on the long side – the king does not cross it.
        Assert.Contains("c1", Targets("4k3/1r6/8/8/8/8/8/R3K2R w KQ - 0 1", "e1"));
        // After the king or the rook has moved.
        var moved = new ChessGame(ready);
        foreach (var m in new[] { "e1f1", "e8f8", "f1e1", "f8e8" }) Assert.True(moved.Play(m));
        Assert.DoesNotContain(moved.LegalMoves, m => m.IsCastle);
        var rook = new ChessGame(ready);
        foreach (var m in new[] { "h1g1", "a8b8", "g1h1", "b8a8" }) Assert.True(rook.Play(m));
        Assert.DoesNotContain(rook.LegalMoves, m => m.IsCastle && m.To == Square.Parse("g1"));
        Assert.Contains(rook.LegalMoves, m => m.IsCastle && m.To == Square.Parse("c1"));
        // A captured rook takes the right with it.
        var captured = new ChessGame("r3k2r/8/8/8/8/8/6p1/R3K2R b KQkq - 0 1");
        Assert.True(captured.Play("g2h1q"));
        Assert.Equal(0, captured.Position.Castling & Position.WhiteKingside);
    }

    [Fact]
    public void En_passant_only_right_after_the_double_step()
    {
        var game = new ChessGame("4k3/3p4/8/4P3/8/8/8/4K3 b - - 0 1");
        Assert.True(game.Play("d7d5"));
        Assert.Contains(game.LegalMoves, m => (m.Flags & MoveFlags.EnPassant) != 0);
        Assert.True(game.Play("e5d6"));
        Assert.Equal(Piece.None, game.Position.Board[Square.Parse("d5")]); // the passed pawn is gone
        Assert.Equal(Piece.Make(Side.White, PieceType.Pawn), game.Position.Board[Square.Parse("d6")]);
        Assert.Equal("exd6", game.Moves[^1].San);
        Assert.Equal(Piece.Make(Side.Black, PieceType.Pawn), game.Moves[^1].Captured);

        // One move later the chance is gone.
        var late = new ChessGame("4k3/3p4/8/4P3/8/8/7P/4K3 b - - 0 1");
        Assert.True(late.Play("d7d5"));
        Assert.True(late.Play("h2h3"));
        Assert.True(late.Play("e8e7"));
        Assert.DoesNotContain(late.LegalMoves, m => (m.Flags & MoveFlags.EnPassant) != 0);
        // A single step never allows it.
        var single = new ChessGame("4k3/8/3p4/4P3/8/8/8/4K3 b - - 0 1");
        Assert.True(single.Play("d6d5"));
        Assert.DoesNotContain(single.LegalMoves, m => (m.Flags & MoveFlags.EnPassant) != 0);
    }

    [Fact]
    public void Pawns_promote_on_the_last_rank_to_the_chosen_piece()
    {
        var position = Position.FromFen("1n2k3/P7/8/8/8/8/8/4K3 w - - 0 1");
        var promotions = position.LegalMoves().Where(m => m.From == Square.Parse("a7")).ToList();
        Assert.Equal(8, promotions.Count); // a8 and xb8, four pieces each
        Assert.Equal(new[] { PieceType.Knight, PieceType.Bishop, PieceType.Rook, PieceType.Queen }.OrderBy(t => t), promotions.Where(m => m.To == Square.Parse("a8")).Select(m => m.Promotion).OrderBy(t => t));
        foreach (var type in new[] { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight })
        {
            var game = new ChessGame("1n2k3/P7/8/8/8/8/8/4K3 w - - 0 1");
            Assert.True(game.Play(new Move(Square.Parse("a7"), Square.Parse("b8"), type)));
            Assert.Equal(Piece.Make(Side.White, type), game.Position.Board[Square.Parse("b8")]);
            Assert.Equal(Piece.None, game.Position.Board[Square.Parse("a7")]);
            Assert.True(game.Undo());
            Assert.Equal(Piece.Make(Side.White, PieceType.Pawn), game.Position.Board[Square.Parse("a7")]);
            Assert.Equal(Piece.Make(Side.Black, PieceType.Knight), game.Position.Board[Square.Parse("b8")]);
        }
        var san = new ChessGame("4k3/P7/8/8/8/8/8/4K3 w - - 0 1");
        Assert.True(san.Play("a7a8q"));
        Assert.Equal("a8=Q+", san.Moves[0].San);
        // Without choosing, a move to the last rank is not legal.
        Assert.False(new ChessGame("4k3/P7/8/8/8/8/8/4K3 w - - 0 1").Play(new Move(Square.Parse("a7"), Square.Parse("a8"))));
    }

    [Fact]
    public void Threefold_repetition_is_a_draw()
    {
        var game = new ChessGame();
        var shuffle = new[] { "g1f3", "g8f6", "f3g1", "f6g8" };
        foreach (var move in shuffle) Assert.True(game.Play(move));
        Assert.Equal(2, game.RepetitionCount);
        Assert.False(game.IsOver);
        foreach (var move in shuffle) Assert.True(game.Play(move));
        Assert.True(game.IsOver);
        Assert.Equal(GameResult.Draw, game.Result);
        Assert.Equal(EndReason.Repetition, game.Reason);
        Assert.True(game.Undo());
        Assert.False(game.IsOver);
    }

    [Fact]
    public void Fifty_move_rule_is_a_draw_but_mate_comes_first()
    {
        var game = new ChessGame("4k3/8/8/8/8/8/8/R3K3 w - - 99 80");
        Assert.True(game.Play("a1a2"));
        Assert.Equal(EndReason.FiftyMoves, game.Reason);
        Assert.Equal(GameResult.Draw, game.Result);
        // A pawn move resets the counter.
        var pawn = new ChessGame("4k3/8/8/8/8/8/P7/R3K3 w - - 98 80");
        Assert.True(pawn.Play("a2a3"));
        Assert.Equal(0, pawn.Position.Halfmove);
        Assert.False(pawn.IsOver);
        // Mate on the 100th half move counts as mate.
        var mate = new ChessGame("6k1/5ppp/8/8/8/8/8/3R2K1 w - - 99 80");
        Assert.True(mate.Play("d1d8"));
        Assert.Equal(EndReason.Checkmate, mate.Reason);
    }

    [Theory]
    [InlineData("4k3/8/8/8/8/8/8/4K3 w - - 0 1", true)]
    [InlineData("4k3/8/8/8/8/8/8/2B1K3 w - - 0 1", true)]
    [InlineData("4k3/8/8/8/8/8/8/1N2K3 w - - 0 1", true)]
    [InlineData("2b1k3/8/8/8/8/8/8/2B1K3 w - - 0 1", false)] // bishops on different colours: a mate exists
    [InlineData("4kb2/8/8/8/8/8/8/2B1K3 w - - 0 1", true)]  // both on dark squares
    [InlineData("4k3/8/8/8/8/8/8/1NN1K3 w - - 0 1", false)]
    [InlineData("4k3/8/8/8/8/8/P7/4K3 w - - 0 1", false)]
    [InlineData("4k3/8/8/8/8/8/8/R3K3 w - - 0 1", false)]
    public void Insufficient_material_is_recognised(string fen, bool insufficient)
    {
        Assert.Equal(insufficient, Position.FromFen(fen).IsInsufficientMaterial());
        Assert.Equal(insufficient, new ChessGame(fen).Reason == EndReason.InsufficientMaterial);
    }

    [Fact]
    public void Capturing_the_last_piece_ends_in_a_draw()
    {
        var game = new ChessGame("4k3/8/8/8/8/8/3r4/4K3 w - - 0 1");
        Assert.True(game.Play("e1d2"));
        Assert.Equal(EndReason.InsufficientMaterial, game.Reason);
    }

    [Fact]
    public void Illegal_moves_are_refused_and_change_nothing()
    {
        var game = new ChessGame();
        var before = game.Position.ToFen();
        Assert.False(game.Play("e2e5"));
        Assert.False(game.Play("e7e5")); // wrong side
        Assert.False(game.Play("e1e2")); // own piece there
        Assert.False(game.Play(new Move(12, 12)));
        Assert.Equal(before, game.Position.ToFen());
        Assert.Empty(game.Moves);
    }

    [Fact]
    public void Notation_names_pieces_and_disambiguates()
    {
        var game = new ChessGame("4k3/8/8/8/8/8/8/1N1NK3 w - - 0 1");
        Assert.True(game.Play("b1c3"));
        Assert.Equal("Nbc3", game.Moves[0].San);
        var ranks = new ChessGame("4k3/8/8/R7/8/8/8/R3K3 w - - 0 1");
        Assert.True(ranks.Play("a1a3"));
        Assert.Equal("R1a3", ranks.Moves[0].San);
        var opening = new ChessGame();
        foreach (var move in new[] { "e2e4", "d7d5", "e4d5", "g8f6" }) Assert.True(opening.Play(move));
        Assert.Equal(new[] { "e4", "d5", "exd5", "Nf6" }, opening.Moves.Select(m => m.San));
    }

    [Fact]
    public void Undo_restores_the_exact_position()
    {
        var game = new ChessGame();
        var start = game.Position.ToFen();
        var hash = game.Position.Hash;
        foreach (var move in new[] { "e2e4", "d7d5", "e4d5", "d8d5", "b1c3", "d5a5", "e1e2" }) Assert.True(game.Play(move));
        Assert.Single(game.CapturedBy(Side.White));
        Assert.Single(game.CapturedBy(Side.Black));
        while (game.Undo()) { }
        Assert.Equal(start, game.Position.ToFen());
        Assert.Equal(hash, game.Position.Hash);
        Assert.Single(game.Hashes);
    }

    [Fact]
    public void Resignation_and_time_end_the_game()
    {
        var game = new ChessGame();
        game.Resign(Side.White);
        Assert.Equal(GameResult.BlackWins, game.Result);
        Assert.Equal(EndReason.Resignation, game.Reason);
        Assert.False(game.Undo());
        Assert.False(game.Play("e2e4"));

        var flag = new ChessGame();
        flag.Timeout(Side.Black);
        Assert.Equal(GameResult.WhiteWins, flag.Result);
        // Time runs out, but the other side only has a king: draw.
        var bare = new ChessGame("4k3/8/8/8/8/8/8/Q3K3 b - - 0 1");
        bare.Timeout(Side.White);
        Assert.Equal(GameResult.Draw, bare.Result);
    }

    [Fact]
    public void Clock_counts_down_and_adds_the_increment()
    {
        var clock = new ChessClock(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2));
        Assert.Null(clock.Running);
        clock.Start(Side.Black);
        Thread.Sleep(120);
        Assert.True(clock.Remaining(Side.Black) < TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(60), clock.Remaining(Side.White));
        clock.Press(Side.Black);
        Assert.Equal(Side.White, clock.Running);
        Assert.True(clock.Remaining(Side.Black) > TimeSpan.FromSeconds(61)); // + 2 s increment
        clock.Pause(true);
        var paused = clock.Remaining(Side.White);
        Thread.Sleep(60);
        Assert.Equal(paused, clock.Remaining(Side.White));
        clock.Pause(false);
        clock.Set(Side.White, TimeSpan.Zero);
        Assert.Equal(Side.White, clock.Flagged);
    }

    [Fact]
    public void Fen_round_trips_and_rejects_nonsense()
    {
        foreach (var fen in new[] { Position.StartFen, "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", "8/8/8/K2pP2q/8/8/8/7k w - d6 0 1" })
            Assert.Equal(fen, Position.FromFen(fen).ToFen());
        Assert.Throws<FormatException>(() => Position.FromFen("8/8/8/8/8/8/8/8 w - - 0 1"));
        Assert.Throws<FormatException>(() => Position.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP w KQkq - 0 1"));
        Assert.Throws<FormatException>(() => Position.FromFen("garbage"));
        // Rights that cannot exist are dropped.
        Assert.Equal(0, Position.FromFen("4k3/8/8/8/8/8/8/4K3 w KQkq - 0 1").Castling);
    }

    [Fact]
    public void New_game_starts_from_the_full_start_position()
    {
        var game = new ChessGame();
        foreach (var move in new[] { "e2e4", "e7e5", "d1h5" }) Assert.True(game.Play(move));
        var fresh = new ChessGame();
        Assert.Equal(Position.StartFen, fresh.Position.ToFen());
        Assert.Empty(fresh.Moves);
        Assert.Equal(20, fresh.LegalMoves.Count);
        Assert.Equal(GameResult.Ongoing, fresh.Result);
        Assert.Empty(fresh.CapturedBy(Side.White));
    }
}
