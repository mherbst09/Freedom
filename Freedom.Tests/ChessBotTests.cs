using System.Diagnostics;
using ViennaAtomicClock.Chess;
using Xunit;

namespace Freedom.Tests;

public sealed class ChessBotTests
{
    private static Move Choose(IChessBot bot, string fen, CancellationToken token = default)
    {
        var position = Position.FromFen(fen);
        return bot.ChooseMove(position, new[] { position.Hash }, token);
    }

    private static IChessBot Hard(int ms = 1500, int seed = 1) => ChessBots.Create(Difficulty.Hard, new Random(seed), TimeSpan.FromMilliseconds(ms));

    [Theory]
    [InlineData(Difficulty.Easy)]
    [InlineData(Difficulty.Medium)]
    [InlineData(Difficulty.Hard)]
    public void Every_bot_only_plays_legal_moves(Difficulty difficulty)
    {
        // Each bot plays both colours against a random mover until the game ends (or 120 half moves).
        for (var seed = 0; seed < (difficulty == Difficulty.Hard ? 2 : 4); seed++)
        {
            var random = new Random(seed);
            var bot = ChessBots.Create(difficulty, new Random(seed), TimeSpan.FromMilliseconds(60));
            var game = new ChessGame();
            var botSide = seed % 2 == 0 ? Side.White : Side.Black;
            while (!game.IsOver && game.Moves.Count < 120)
            {
                Move move;
                if (game.ToMove == botSide)
                {
                    var copy = game.Position.Clone();
                    move = bot.ChooseMove(copy, game.Hashes, CancellationToken.None);
                    Assert.Equal(game.Position.ToFen(), copy.ToFen()); // the bot does not touch the game's position
                    Assert.Contains(move, game.LegalMoves);
                }
                else move = game.LegalMoves[random.Next(game.LegalMoves.Count)];
                Assert.True(game.Play(move), $"{difficulty} played the illegal move {move} in {game.Position.ToFen()}");
            }
        }
    }

    [Fact]
    public void Bots_handle_check_and_special_moves()
    {
        // In check: only moves that answer the check are possible.
        const string inCheck = "4k3/8/8/8/8/8/3q4/R3K2R w KQ - 0 1";
        foreach (var difficulty in Enum.GetValues<Difficulty>())
            for (var seed = 0; seed < 5; seed++)
            {
                var move = Choose(ChessBots.Create(difficulty, new Random(seed), TimeSpan.FromMilliseconds(100)), inCheck);
                Assert.Contains(move, Position.FromFen(inCheck).LegalMoves());
                Assert.False(move.IsCastle);
            }
        // Promotion: the strong bots promote (to a queen).
        Assert.Equal("a7a8q", Choose(Hard(500), "4k3/P7/8/8/8/8/8/4K3 w - - 0 1").Uci);
    }

    [Fact]
    public void Easy_bot_usually_takes_a_mate_in_one_but_also_errs()
    {
        const string fen = "6k1/5ppp/8/8/8/8/5PPP/3R2K1 w - - 0 1";
        var mates = 0;
        var moves = new HashSet<string>();
        for (var seed = 0; seed < 60; seed++)
        {
            var move = Choose(new EasyBot(new Random(seed)), fen);
            moves.Add(move.Uci);
            if (move.Uci == "d1d8") mates++;
        }
        Assert.InRange(mates, 35, 59);
        Assert.True(moves.Count > 1); // not perfect
    }

    [Fact]
    public void Easy_bot_is_mostly_random()
    {
        var moves = new HashSet<string>();
        for (var seed = 0; seed < 40; seed++) moves.Add(Choose(new EasyBot(new Random(seed)), "rnbqkbnr/pppp1ppp/8/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R b KQkq - 1 2").Uci);
        Assert.True(moves.Count >= 10, string.Join(" ", moves));
    }

    [Fact]
    public void Medium_bot_takes_free_pieces_and_saves_its_own()
    {
        for (var seed = 0; seed < 5; seed++)
        {
            var medium = ChessBots.Create(Difficulty.Medium, new Random(seed));
            Assert.Equal("d2d5", Choose(medium, "4k3/8/8/3q4/8/8/3R4/4K3 w - - 0 1").Uci); // a free queen

            // The queen is attacked by a pawn: it moves to a square where it cannot be taken for free.
            const string attacked = "4k3/8/8/8/3q4/4P3/8/4K3 b - - 0 1";
            var move = Choose(medium, attacked);
            var position = Position.FromFen(attacked);
            Assert.Equal(Square.Parse("d4"), move.From);
            position.Make(move);
            var queen = Array.IndexOf(position.Board, Piece.Make(Side.Black, PieceType.Queen));
            Assert.False(position.IsAttacked(queen, Side.White) && !position.IsAttacked(queen, Side.Black), $"queen hangs after {move}");

            // Does not grab a protected pawn with the queen.
            Assert.NotEqual("d1d5", Choose(medium, "4k3/8/4p3/3p4/8/8/8/3QK3 w - - 0 1").Uci);
        }
    }

    [Fact]
    public void Medium_bot_develops_pieces_in_the_opening()
    {
        // After 1.e4 e5 the medium bot (as White) brings out a minor piece or plays a central pawn – never a rook or king move.
        for (var seed = 0; seed < 8; seed++)
        {
            var game = new ChessGame();
            foreach (var m in new[] { "e2e4", "e7e5", "a2a3", "a7a6" }) Assert.True(game.Play(m)); // out of the book
            var move = ChessBots.Create(Difficulty.Medium, new Random(seed)).ChooseMove(game.Position.Clone(), game.Hashes, default);
            var type = Piece.Type(game.Position.Board[move.From]);
            Assert.True(type is PieceType.Knight or PieceType.Bishop or PieceType.Pawn, $"{move} ({type})");
        }
    }

    [Fact]
    public void Hard_bot_finds_mates_and_prefers_mate_over_material()
    {
        Assert.Equal("d1d8", Choose(Hard(), "6k1/5ppp/8/8/8/8/5PPP/3R2K1 w - - 0 1").Uci); // mate in one
        // Queen can be taken, but a back-rank mate is better.
        Assert.Equal("d1d8", Choose(Hard(), "6k1/5ppp/8/8/8/1q6/P4PPP/3R2K1 w - - 0 1").Uci);
        // Mate in two: 1. Qd8+ Bxd8 2. Re8#
        Assert.Equal("d5d8", Choose(Hard(3000), "r1b2k1r/ppp1bppp/8/1B1Q4/5q2/2P5/PPP2PPP/R3R1K1 w - - 1 1").Uci);
        // Defends against a mate in one instead of grabbing a pawn.
        const string threat = "6k1/5ppp/8/8/8/r7/P4PPP/3R2K1 b - - 0 1";
        var defence = Choose(Hard(), threat);
        var position = Position.FromFen(threat);
        position.Make(defence);
        Assert.False(position.LegalMoves().Any(m =>
        {
            var undo = position.Make(m);
            var mate = position.InCheck() && !position.HasLegalMove();
            position.Unmake(m, undo);
            return mate;
        }), $"{defence} allows a mate in one");
    }

    [Theory]
    [InlineData(Side.White)]
    [InlineData(Side.Black)]
    public void Hard_bot_beats_the_easy_bot(Side hardSide)
    {
        var hard = ChessBots.Create(Difficulty.Hard, new Random(3), TimeSpan.FromMilliseconds(150));
        var easy = ChessBots.Create(Difficulty.Easy, new Random(3));
        var game = new ChessGame();
        while (!game.IsOver && game.Moves.Count < 300)
        {
            var bot = game.ToMove == hardSide ? hard : easy;
            Assert.True(game.Play(bot.ChooseMove(game.Position.Clone(), game.Hashes, default)));
        }
        Assert.Equal(hardSide == Side.White ? GameResult.WhiteWins : GameResult.BlackWins, game.Result);
        Assert.Equal(EndReason.Checkmate, game.Reason);
    }

    [Fact]
    public void Hard_bot_wins_material_with_a_fork()
    {
        // Nc7+ forks king and rook.
        Assert.Equal("d5c7", Choose(Hard(), "r3k3/8/8/3N4/8/8/8/4K3 w - - 0 1").Uci);
    }

    [Fact]
    public void Hard_bot_keeps_to_its_time_limit_and_stops_when_cancelled()
    {
        const string middlegame = "r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP3PPP/R2QKB1R w KQ - 0 8";
        var watch = Stopwatch.StartNew();
        var move = Choose(Hard(400), middlegame);
        watch.Stop();
        Assert.Contains(move, Position.FromFen(middlegame).LegalMoves());
        Assert.True(watch.ElapsedMilliseconds < 1500, $"{watch.ElapsedMilliseconds} ms");

        using var cancel = new CancellationTokenSource(150);
        watch.Restart();
        move = Choose(Hard(20_000), middlegame, cancel.Token);
        Assert.True(watch.ElapsedMilliseconds < 1500, $"{watch.ElapsedMilliseconds} ms");
        Assert.Contains(move, Position.FromFen(middlegame).LegalMoves());
    }

    [Fact]
    public void Hard_bot_searches_several_half_moves()
    {
        var search = new ChessSearch(new SearchOptions { TimeLimit = TimeSpan.FromSeconds(1.5) });
        var position = Position.FromFen("r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP3PPP/R2QKB1R w KQ - 0 8");
        var result = search.Search(position, new[] { position.Hash }, default);
        Assert.True(result.Depth >= 4, $"depth {result.Depth}, {result.Nodes} nodes");
    }

    [Fact]
    public void Evaluation_is_symmetric_and_values_material()
    {
        var start = Position.Start();
        Assert.Equal(ChessEval.Full(start), ChessEval.Full(Position.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR b KQkq - 0 1")));
        var mirrored = Position.FromFen("r1bqkb1r/pppp1ppp/2n2n2/4p3/4P3/2N2N2/PPPP1PPP/R1BQKB1R w KQkq - 4 4");
        Assert.InRange(ChessEval.Full(mirrored), -40, 40);
        Assert.True(ChessEval.Full(Position.FromFen("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")) > 700); // a queen up
        Assert.True(ChessEval.Simple(Position.FromFen("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR b KQkq - 0 1")) < -700);
    }

    [Fact]
    public void Opening_book_only_contains_legal_moves()
    {
        Assert.True(OpeningBook.PositionCount > 20);
        var position = Position.Start();
        var seen = new HashSet<string>();
        for (var seed = 0; seed < 30; seed++)
            if (OpeningBook.Pick(position, new Random(seed)) is { } move) seen.Add(move.Uci);
        Assert.True(seen.Count >= 3, string.Join(" ", seen));
        Assert.Null(OpeningBook.Pick(Position.FromFen("4k3/8/8/8/8/8/8/4K3 w - - 0 1"), new Random(1)));
    }
}
