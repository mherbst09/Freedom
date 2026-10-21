using System.IO;
using System.Text.RegularExpressions;
using ViennaAtomicClock;
using ViennaAtomicClock.Chess;
using Xunit;

namespace Freedom.Tests;

public sealed class ChessPageTests
{
    private static ChessPage Create(string difficulty = "easy", string color = "white", string clock = "off", int seed = 7)
    {
        var settings = new ChessSettings { Difficulty = difficulty, Color = color, Clock = clock }.Normalize();
        var page = new ChessPage(settings, persist: false, d => ChessBots.Create(d, new Random(seed), TimeSpan.FromMilliseconds(300))) { ThinkDelayScale = 0 };
        return page;
    }

    [Fact]
    public void Setup_comes_first_then_player_and_bot_take_turns()
    {
        UiTestHost.RunAsync(async () =>
        {
            var page = Create();
            try
            {
                UiTestHost.Render(page, 1280, 860);
                await Task.Delay(250);
                Assert.Equal("setup", page.OverlayKind);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 860), "chess-setup");
                Assert.False(page.RequestMove("e2e4")); // nothing can be played behind the dialog

                page.StartFromSetup();
                Assert.Equal("", page.OverlayKind);
                Assert.Equal(Side.White, page.PlayerSide);
                Assert.True(page.BoardView.InputEnabled);
                Assert.False(page.RequestMove("e7e5")); // not the player's pieces
                Assert.False(page.RequestMove("e2e5")); // not a legal move

                // Clicking a piece selects it; clicking a target plays the move.
                page.BoardView.Click(Square.Parse("g1"));
                Assert.Equal(Square.Parse("g1"), page.BoardView.Selected);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 860), "chess-selected");
                page.BoardView.Click(Square.Parse("f3"));
                Assert.Single(page.Game.Moves);
                Assert.True(page.Thinking);
                Assert.False(page.BoardView.InputEnabled); // input is blocked while the bot thinks
                Assert.Contains("…", page.StatusLine);
                Assert.False(page.RequestMove("d2d4"));
                await page.BotTurn;
                Assert.False(page.Thinking);
                Assert.Equal(2, page.Game.Moves.Count);
                Assert.Equal(Side.White, page.Game.ToMove);
                Assert.True(page.BoardView.InputEnabled);

                for (var i = 0; i < 6 && !page.Game.IsOver; i++)
                {
                    var moves = page.Game.LegalMoves.Where(m => !m.IsPromotion).ToList();
                    Assert.True(page.RequestMove(moves[i * 7 % moves.Count].Uci));
                    await page.BotTurn;
                }
                UiTestHost.Render(page, 1280, 860);
                await Task.Delay(300);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 860), "chess-game");
                UiTestHost.Render(page, 720, 1300);
                await Task.Delay(200);
                UiTestHost.Render(page, 720, 1300);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 720, 1300), "chess-narrow");
            }
            finally
            {
                page.Shutdown();
            }
        }, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Bot_starts_when_the_player_takes_black_and_the_ui_stays_responsive()
    {
        UiTestHost.RunAsync(async () =>
        {
            var settings = new ChessSettings { Difficulty = "hard", Color = "black" }.Normalize();
            var page = new ChessPage(settings, persist: false, d => new SlowBot());
            try
            {
                UiTestHost.Render(page, 1280, 860);
                page.StartFromSetup();
                Assert.Equal(Side.Black, page.PlayerSide);
                Assert.True(page.BoardView.Flipped);
                Assert.True(page.Thinking);
                // The bot thinks on another thread: the window keeps reacting meanwhile. If the search ran on the
                // UI thread, not a single one of these small waits could finish before the bot is done.
                var turns = 0;
                while (page.Thinking && turns < 3)
                {
                    await Task.Delay(20);
                    turns++;
                }
                Assert.Equal(3, turns);
                Assert.True(page.Thinking);
                Assert.False(page.BoardView.InputEnabled);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 860), "chess-thinking");
                await page.BotTurn;
                Assert.Single(page.Game.Moves);
                Assert.Equal(Side.Black, page.Game.ToMove);
            }
            finally
            {
                page.Shutdown();
            }
        }, TimeSpan.FromSeconds(30));
    }

    /// <summary>Takes its time like a deep search, then plays the first legal move.</summary>
    private sealed class SlowBot : IChessBot
    {
        public Move ChooseMove(Position position, IReadOnlyList<ulong> history, CancellationToken token)
        {
            token.WaitHandle.WaitOne(1500);
            return position.LegalMoves()[0];
        }
    }

    [Fact]
    public void Undo_takes_back_the_player_move_and_the_answer()
    {
        UiTestHost.RunAsync(async () =>
        {
            var page = Create();
            try
            {
                UiTestHost.Render(page, 1280, 860);
                page.StartFromSetup();
                Assert.True(page.RequestMove("e2e4"));
                await page.BotTurn;
                Assert.True(page.RequestMove("d2d4"));
                await page.BotTurn;
                Assert.Equal(4, page.Game.Moves.Count);
                Assert.True(page.UndoMove());
                Assert.Equal(2, page.Game.Moves.Count);
                Assert.Equal(Side.White, page.Game.ToMove);
                Assert.True(page.UndoMove());
                Assert.Empty(page.Game.Moves);
                Assert.Equal(Position.StartFen, page.Game.Position.ToFen());
                Assert.False(page.UndoMove());

                // Undo while the bot is still thinking: its search is dropped and the player's move goes back.
                var slow = new ChessPage(new ChessSettings().Normalize(), persist: false, d => new SlowBot());
                UiTestHost.Render(slow, 1280, 860);
                slow.StartFromSetup();
                Assert.True(slow.RequestMove("e2e4"));
                Assert.True(slow.Thinking);
                Assert.True(slow.UndoMove());
                Assert.False(slow.Thinking);
                Assert.Empty(slow.Game.Moves);
                await Task.Delay(300);
                Assert.Empty(slow.Game.Moves); // the cancelled bot never moves
                slow.Shutdown();
            }
            finally
            {
                page.Shutdown();
            }
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Promotion_asks_for_the_piece()
    {
        UiTestHost.RunAsync(async () =>
        {
            var page = Create();
            try
            {
                UiTestHost.Render(page, 1280, 860);
                page.StartFromSetup();
                page.LoadPosition("4k3/P7/8/8/8/8/8/4K3 w - - 0 1", Side.White);
                Assert.True(page.RequestMove("a7a8"));
                Assert.Equal("promotion", page.OverlayKind);
                Assert.Empty(page.Game.Moves); // nothing is played before the choice
                UiTestHost.Render(page, 1280, 860);
                await Task.Delay(300);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 860), "chess-promotion");
                page.ChoosePromotion(PieceType.Knight);
                Assert.Equal("", page.OverlayKind);
                Assert.Equal(Piece.Make(Side.White, PieceType.Knight), page.Game.Position.Board[Square.Parse("a8")]);
                await page.BotTurn;

                // Cancelling the choice keeps the pawn where it was.
                page.LoadPosition("4k3/P7/8/8/8/8/8/4K3 w - - 0 1", Side.White);
                Assert.True(page.RequestMove("a7a8"));
                page.CloseOverlay();
                Assert.Empty(page.Game.Moves);
                Assert.True(page.BoardView.InputEnabled);
            }
            finally
            {
                page.Shutdown();
            }
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Mate_shows_the_result_and_a_new_game_resets_everything()
    {
        UiTestHost.RunAsync(async () =>
        {
            var page = Create(clock: "5+0");
            try
            {
                UiTestHost.Render(page, 1280, 860);
                page.StartFromSetup();
                Assert.NotNull(page.Clock);
                page.LoadPosition("6k1/5ppp/8/8/8/8/5PPP/3R2K1 w - - 0 1", Side.White);
                Assert.True(page.RequestMove("d1d8"));
                Assert.True(page.Game.IsOver);
                Assert.Equal(GameResult.WhiteWins, page.Game.Result);
                Assert.False(page.BoardView.InputEnabled);
                for (var i = 0; i < 100 && page.OverlayKind != "over"; i++) await Task.Delay(50);
                Assert.Equal("over", page.OverlayKind);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 860), "chess-mate");

                page.ShowSetup();
                page.StartFromSetup();
                Assert.Equal(Position.StartFen, page.Game.Position.ToFen());
                Assert.Empty(page.Game.Moves);
                Assert.False(page.Game.IsOver);
                Assert.Null(page.BoardView.LastMove);
                Assert.Equal(-1, page.BoardView.CheckSquare);
                Assert.Equal(TimeSpan.FromMinutes(5), page.Clock!.Remaining(Side.White));
                Assert.Null(page.Clock.Running);
            }
            finally
            {
                page.Shutdown();
            }
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Resigning_ends_the_game()
    {
        UiTestHost.RunAsync(async () =>
        {
            var page = Create();
            try
            {
                UiTestHost.Render(page, 1280, 860);
                page.StartFromSetup();
                Assert.True(page.RequestMove("e2e4"));
                await page.BotTurn;
                page.Resign();
                Assert.Equal(GameResult.BlackWins, page.Game.Result);
                Assert.Equal(EndReason.Resignation, page.Game.Reason);
                Assert.Equal("over", page.OverlayKind);
                Assert.False(page.UndoMove());
            }
            finally
            {
                page.Shutdown();
            }
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Every_theme_language_scheme_and_overlay_renders()
    {
        UiTestHost.RunAsync(async () =>
        {
            var page = Create(clock: "3+2");
            try
            {
                UiTestHost.Render(page, 1280, 860);
                page.StartFromSetup();
                Assert.True(page.RequestMove("e2e4"));
                await page.BotTurn;
                foreach (var (code, _, _) in Loc.Languages)
                {
                    Loc.Apply(code);
                    UiTestHost.Render(page, 1100, 820);
                    Assert.False(string.IsNullOrWhiteSpace(page.StatusLine));
                }
                Loc.Apply("de");
                page.ShowSettings();
                UiTestHost.Render(page, 1280, 900);
                await Task.Delay(300);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 900), "chess-settings");
                page.CloseOverlay();
                foreach (var scheme in ChessSettings.Schemes)
                {
                    page.SetScheme(scheme);
                    foreach (var (theme, _, _, _) in Themes.All)
                        foreach (var dark in new[] { false, true })
                        {
                            App.ApplyTheme(theme, dark);
                            UiTestHost.Render(page, 1100, 820);
                        }
                }
                App.ApplyTheme(Themes.Standard, false);
                page.SetScheme("dark");
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 860), "chess-dark");
                page.SetScheme("light");
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 860), "chess-light");
            }
            finally
            {
                Loc.Apply("de");
                App.ApplyTheme(Themes.Standard, true);
                page.Shutdown();
            }
        }, TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Settings_are_normalised()
    {
        var settings = new ChessSettings { Difficulty = "godlike", Color = "green", Clock = "1+0", Scheme = "neon", Board = "lava" }.Normalize();
        Assert.Equal("medium", settings.Difficulty);
        Assert.Equal("white", settings.Color);
        Assert.Equal("off", settings.Clock);
        Assert.Equal("app", settings.Scheme);
        Assert.Equal("wood", settings.Board);
        Assert.Equal((TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(10)), ChessSettings.ClockTimes("15+10"));
        Assert.Null(ChessSettings.ClockTimes("off"));
    }

    [Fact]
    public void Every_chess_text_exists_in_all_languages()
    {
        var placeholder = new Regex(@"\{\d\}");
        foreach (var (key, texts) in ChessStrings.Table)
        {
            Assert.Equal(Loc.Languages.Length, texts.Length);
            var expected = placeholder.Matches(texts[0]).Select(m => m.Value).Distinct().OrderBy(v => v).ToList();
            foreach (var text in texts)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), key);
                Assert.True(expected.SequenceEqual(placeholder.Matches(text).Select(m => m.Value).Distinct().OrderBy(v => v)), $"{key}: {text}");
            }
        }
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ViennaAtomicClock", "Chess"));
        Assert.True(Directory.Exists(source), source);
        var keys = Directory.GetFiles(source, "*.cs").Where(f => !f.EndsWith("ChessStrings.cs"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"((?:ch|title|tile|window)\\.[A-Za-z0-9_.]+[A-Za-z0-9])\"").Select(m => m.Groups[1].Value)).Distinct().ToList();
        var dynamic = ChessSettings.Difficulties.SelectMany(d => new[] { "ch.difficulty." + d, "ch.difficulty." + d + ".help" })
            .Concat(ChessSettings.Colors.Select(c => "ch.color." + c))
            .Concat(ChessSettings.Schemes.Select(s => "ch.scheme." + s))
            .Concat(BoardTheme.All.Select(t => "ch.board." + t.Id))
            .Concat(new[] { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight }.Select(t => "ch.piece." + t.ToString().ToLowerInvariant()))
            .Concat(Enum.GetValues<EndReason>().Where(r => r is not (EndReason.Resignation or EndReason.Timeout)).Select(r => "ch.reason." + r.ToString().ToLowerInvariant()))
            .Concat(new[] { "tile.chess", "tile.chess.hint", "title.chess", "title.chess.sub", "window.chess" });
        var missing = keys.Concat(dynamic).Where(k => !ChessStrings.Table.ContainsKey(k)).Distinct().ToList();
        Assert.True(missing.Count == 0, "Missing: " + string.Join(", ", missing));
    }
}
