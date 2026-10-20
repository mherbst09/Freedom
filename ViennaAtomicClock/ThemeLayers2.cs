using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ViennaAtomicClock;

/// <summary>Tetris theme backdrop: bevelled blocks fall in a grid, stack up and clear lines with a flash.</summary>
internal sealed class TetrisBackdrop : AnimatedLayer
{
    private const double Cell = 26;
    private const double StepSeconds = 0.32;

    private static readonly Color[] PieceColors =
    {
        Color.FromRgb(0, 240, 240), Color.FromRgb(240, 240, 0), Color.FromRgb(160, 0, 240), Color.FromRgb(0, 240, 0),
        Color.FromRgb(240, 0, 0), Color.FromRgb(30, 80, 240), Color.FromRgb(240, 160, 0)
    };

    // I, O, T, S, Z, J, L as cell offsets.
    private static readonly (int X, int Y)[][] Shapes =
    {
        new[] { (0, 0), (1, 0), (2, 0), (3, 0) }, new[] { (0, 0), (1, 0), (0, 1), (1, 1) },
        new[] { (0, 0), (1, 0), (2, 0), (1, 1) }, new[] { (1, 0), (2, 0), (0, 1), (1, 1) },
        new[] { (0, 0), (1, 0), (1, 1), (2, 1) }, new[] { (0, 0), (0, 1), (1, 1), (2, 1) },
        new[] { (2, 0), (0, 1), (1, 1), (2, 1) }
    };

    private sealed class Piece { public (int X, int Y)[] Cells = Array.Empty<(int, int)>(); public int Kind, X, Y; }

    private int[,] _stack = new int[0, 0];
    private readonly List<Piece> _pieces = new();
    private readonly List<int> _flashRows = new();
    private double _clock, _flash;

    public TetrisBackdrop() : base(30) { }

    private int Cols => _stack.GetLength(0);
    private int Rows => _stack.GetLength(1);

    protected override void Step(double dt)
    {
        var cols = Math.Max(4, (int)(ActualWidth / Cell));
        var rows = Math.Max(6, (int)(ActualHeight / Cell));
        if (cols != Cols || rows != Rows)
        {
            _stack = new int[cols, rows];
            _pieces.Clear();
        }

        if (_flash > 0)
        {
            _flash -= dt;
            if (_flash <= 0) ClearFlashedRows();
            return;
        }

        _clock += dt;
        if (_clock < StepSeconds) return;
        _clock = 0;

        while (_pieces.Count < Math.Max(2, Cols / 9)) _pieces.Add(Spawn());

        foreach (var piece in _pieces.ToList())
        {
            if (Random.NextDouble() < 0.15) TryRotate(piece);
            if (Random.NextDouble() < 0.2) TryMove(piece, Random.Next(2) == 0 ? -1 : 1, 0);
            if (!TryMove(piece, 0, 1)) Lock(piece);
        }
    }

    private Piece Spawn()
    {
        var kind = Random.Next(Shapes.Length);
        return new Piece { Kind = kind, Cells = Shapes[kind], X = Random.Next(Math.Max(1, Cols - 4)), Y = -2 };
    }

    private bool Free(Piece piece, int dx, int dy, (int X, int Y)[]? cells = null)
    {
        foreach (var (cx, cy) in cells ?? piece.Cells)
        {
            var x = piece.X + cx + dx;
            var y = piece.Y + cy + dy;
            if (x < 0 || x >= Cols || y >= Rows) return false;
            if (y >= 0 && _stack[x, y] != 0) return false;
        }
        return true;
    }

    private bool TryMove(Piece piece, int dx, int dy)
    {
        if (!Free(piece, dx, dy)) return false;
        piece.X += dx;
        piece.Y += dy;
        return true;
    }

    private void TryRotate(Piece piece)
    {
        var rotated = piece.Cells.Select(c => (-c.Y + 2, c.X)).ToArray();
        var minX = rotated.Min(c => c.Item1);
        rotated = rotated.Select(c => (c.Item1 - minX, c.Item2)).ToArray();
        if (Free(piece, 0, 0, rotated)) piece.Cells = rotated;
    }

    private void Lock(Piece piece)
    {
        _pieces.Remove(piece);
        foreach (var (cx, cy) in piece.Cells)
        {
            var x = piece.X + cx;
            var y = piece.Y + cy;
            if (y < 0) { Reset(); return; }
            _stack[x, y] = piece.Kind + 1;
        }

        // Full lines flash and clear. With random drops the stack also gets "cleared" once it gets too tall.
        for (var y = 0; y < Rows; y++)
            if (Enumerable.Range(0, Cols).All(x => _stack[x, y] != 0)) _flashRows.Add(y);
        var height = Enumerable.Range(0, Rows).FirstOrDefault(y => Enumerable.Range(0, Cols).Any(x => _stack[x, y] != 0), Rows);
        if (_flashRows.Count == 0 && Rows - height > 6)
            for (var y = Rows - 3; y < Rows; y++) _flashRows.Add(y);
        if (_flashRows.Count > 0) _flash = 0.45;
    }

    private void ClearFlashedRows()
    {
        foreach (var row in _flashRows.OrderBy(r => r))
        {
            for (var y = row; y > 0; y--)
                for (var x = 0; x < Cols; x++) _stack[x, y] = _stack[x, y - 1];
            for (var x = 0; x < Cols; x++) _stack[x, 0] = 0;
        }
        _flashRows.Clear();
    }

    private void Reset()
    {
        _stack = new int[Cols, Rows];
        _pieces.Clear();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Cols == 0) return;
        var dark = App.IsDark;
        var alpha = dark ? 0.5 : 0.42;
        var offsetX = (ActualWidth - Cols * Cell) / 2;
        var offsetY = ActualHeight - Rows * Cell;

        PushShellClip(dc);
        // Keep the title bar free so the buttons stay readable.
        dc.PushClip(new RectangleGeometry(new Rect(0, 72, ActualWidth, Math.Max(0, ActualHeight - 72))));
        for (var x = 0; x < Cols; x++)
            for (var y = 0; y < Rows; y++)
                if (_stack[x, y] != 0)
                {
                    var flashing = _flashRows.Contains(y) && ((int)(_flash * 12) & 1) == 0;
                    DrawBlock(dc, offsetX + x * Cell, offsetY + y * Cell, flashing ? Colors.White : PieceColors[_stack[x, y] - 1], flashing ? 0.9 : alpha);
                }

        foreach (var piece in _pieces)
            foreach (var (cx, cy) in piece.Cells)
                if (piece.Y + cy >= 0)
                    DrawBlock(dc, offsetX + (piece.X + cx) * Cell, offsetY + (piece.Y + cy) * Cell, PieceColors[piece.Kind], alpha + 0.15);
        dc.Pop();
        dc.Pop();
    }

    private void DrawBlock(DrawingContext dc, double x, double y, Color color, double alpha)
    {
        var r = new Rect(x + 1, y + 1, Cell - 2, Cell - 2);
        dc.DrawRectangle(Fill(color, alpha), null, r);
        dc.DrawRectangle(Fill(Colors.White, alpha * 0.55), null, new Rect(r.X, r.Y, r.Width, 3));
        dc.DrawRectangle(Fill(Colors.White, alpha * 0.35), null, new Rect(r.X, r.Y, 3, r.Height));
        dc.DrawRectangle(Fill(Colors.Black, alpha * 0.45), null, new Rect(r.X, r.Bottom - 3, r.Width, 3));
        dc.DrawRectangle(Fill(Colors.Black, alpha * 0.3), null, new Rect(r.Right - 3, r.Y, 3, r.Height));
    }
}

/// <summary>America theme backdrop: a softly waving stars-and-stripes field with twinkling stars.</summary>
internal sealed class AmericaBackdrop : AnimatedLayer
{
    private static readonly Color Red = Color.FromRgb(178, 34, 52);
    private static readonly Color Navy = Color.FromRgb(10, 49, 97);
    private double _time;

    public AmericaBackdrop() : base(24) { }

    protected override void Step(double dt) => _time += dt;

    private double Wave(double x, double y) =>
        Math.Sin(x / 140 - _time * 1.6) * 9 * (0.4 + x / Math.Max(1, ActualWidth)) + Math.Sin(y / 90 + _time) * 2;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0) return;
        var dark = App.IsDark;
        var stripeAlpha = dark ? 0.16 : 0.12;

        PushShellClip(dc);
        var stripe = h / 13;
        for (var i = 0; i < 13; i++)
        {
            var color = i % 2 == 0 ? Red : Colors.White;
            if (!dark && i % 2 == 1) continue; // white stripes vanish on a light background anyway
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                var top = i * stripe;
                c.BeginFigure(new Point(0, top + Wave(0, top)), true, true);
                for (var x = 20.0; x <= w + 20; x += 20) c.LineTo(new Point(x, top + Wave(x, top)), true, false);
                for (var x = w + 20; x >= 0; x -= 20) c.LineTo(new Point(x, top + stripe + Wave(x, top + stripe)), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(Fill(color, stripeAlpha), null, g);
        }

        // Union (blue field) with 50 stars in alternating rows of 6 and 5.
        var unionW = w * 0.4;
        var unionH = stripe * 7;
        var union = new StreamGeometry();
        using (var c = union.Open())
        {
            c.BeginFigure(new Point(0, Wave(0, 0)), true, true);
            for (var x = 20.0; x <= unionW; x += 20) c.LineTo(new Point(x, Wave(x, 0)), true, false);
            for (var x = unionW; x >= 0; x -= 20) c.LineTo(new Point(x, unionH + Wave(x, unionH)), true, false);
        }
        union.Freeze();
        dc.DrawGeometry(Fill(Navy, dark ? 0.55 : 0.18), null, union);

        var starSize = Math.Min(unionW / 12, unionH / 10) * 0.42;
        for (var row = 0; row < 9; row++)
        {
            var count = row % 2 == 0 ? 6 : 5;
            for (var col = 0; col < count; col++)
            {
                var x = unionW * ((col * 2 + (row % 2 == 0 ? 1 : 2)) / 12.0);
                var y = unionH * ((row + 1) / 10.0);
                y += Wave(x, y);
                var twinkle = 0.55 + 0.45 * Math.Sin(_time * 2.2 + row * 1.7 + col * 2.3);
                dc.PushTransform(new TranslateTransform(x, y));
                dc.PushTransform(new ScaleTransform(starSize, starSize));
                dc.DrawGeometry(Fill(Colors.White, (dark ? 0.55 : 0.35) * twinkle), null, Star);
                dc.Pop();
                dc.Pop();
            }
        }
        dc.Pop();
    }

    private static readonly Geometry Star = CreateStar();

    private static Geometry CreateStar()
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            for (var i = 0; i < 10; i++)
            {
                var a = -Math.PI / 2 + i * Math.PI / 5;
                var r = i % 2 == 0 ? 1 : 0.4;
                var p = new Point(Math.Cos(a) * r, Math.Sin(a) * r);
                if (i == 0) c.BeginFigure(p, true, true);
                else c.LineTo(p, true, false);
            }
        }
        g.Freeze();
        return g;
    }
}

