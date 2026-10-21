using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ViennaAtomicClock.Chess;

/// <summary>Colours of one board design.</summary>
public sealed record BoardTheme(string Id, Color Light, Color Dark, Color LastMove, Color Selected)
{
    public static readonly BoardTheme[] All =
    {
        new("wood", Color.FromRgb(0xF0, 0xD9, 0xB5), Color.FromRgb(0xB5, 0x88, 0x63), Color.FromArgb(0x8C, 0xF6, 0xD3, 0x4F), Color.FromArgb(0xA0, 0x7F, 0xC0, 0x4A)),
        new("tournament", Color.FromRgb(0xEE, 0xEE, 0xD2), Color.FromRgb(0x76, 0x96, 0x56), Color.FromArgb(0x96, 0xF5, 0xE9, 0x5C), Color.FromArgb(0xA0, 0xF2, 0xC2, 0x3A)),
        new("ice", Color.FromRgb(0xDE, 0xE3, 0xE6), Color.FromRgb(0x8C, 0xA2, 0xAD), Color.FromArgb(0x8C, 0x9B, 0xD0, 0xF5), Color.FromArgb(0xA0, 0x4F, 0x9B, 0xE8)),
        new("night", Color.FromRgb(0xA9, 0xB1, 0xC2), Color.FromRgb(0x4B, 0x55, 0x6B), Color.FromArgb(0x96, 0xC8, 0x9B, 0xF5), Color.FromArgb(0xB0, 0x8A, 0x6C, 0xF0))
    };

    public static BoardTheme Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}

/// <summary>
/// Draws pieces from the chess symbols of Segoe UI Symbol (part of Windows, nothing downloaded). White: a white
/// silhouette with the outline symbol on top. Black: a light silhouette with the solid symbol on top, so its
/// inner lines show light on a dark body.
/// </summary>
public static class PieceArt
{
    private static readonly Dictionary<PieceType, (Geometry Body, Geometry Lines, Geometry Solid)> Shapes = new();
    private static readonly Brush WhiteFill = Frozen(Color.FromRgb(0xFB, 0xFA, 0xF6));
    private static readonly Brush WhiteLines = Frozen(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush BlackFill = Frozen(Color.FromRgb(0x2A, 0x2A, 0x2D));
    private static readonly Brush BlackLines = Frozen(Color.FromRgb(0xC9, 0xC9, 0xC9));
    private static readonly Brush Shadow = Frozen(Color.FromArgb(0x48, 0, 0, 0));
    private static readonly Pen WhiteEdge = FrozenPen(Color.FromRgb(0x1E, 0x1E, 0x1E), 0.018);
    private static readonly Pen BlackEdge = FrozenPen(Color.FromRgb(0x0C, 0x0C, 0x0C), 0.022);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    /// <summary>The body (outer outline filled solid), the outline symbol and the solid symbol of a piece in a 1×1 box.</summary>
    public static (Geometry Body, Geometry Lines, Geometry Solid) Shape(PieceType type)
    {
        lock (Shapes)
        {
            if (Shapes.TryGetValue(type, out var shape)) return shape;
            var outlined = (char)(0x2654 + (6 - (int)type)); // ♔♕♖♗♘♙
            var solid = (char)(0x265A + (6 - (int)type));    // ♚♛♜♝♞♟
            var lines = Glyph(outlined);
            var filled = Glyph(solid);
            // Normalise both with the box of the solid symbol, so they line up exactly.
            var box = filled.Bounds;
            var scale = 0.8 / Math.Max(box.Width, box.Height);
            var transform = new TransformGroup();
            transform.Children.Add(new TranslateTransform(-box.X - box.Width / 2, -box.Y - box.Height));
            transform.Children.Add(new ScaleTransform(scale, scale));
            transform.Children.Add(new TranslateTransform(0.5, 0.9));
            transform.Freeze();
            var body = OuterFigures(filled);
            body.Transform = transform;
            body.Freeze();
            var detail = lines.Clone();
            detail.Transform = transform;
            detail.Freeze();
            var solidShape = filled.Clone();
            solidShape.Transform = transform;
            solidShape.Freeze();
            shape = (body, detail, solidShape);
            Shapes[type] = shape;
            return shape;
        }
    }

    private static PathGeometry Glyph(char symbol)
    {
        var text = new FormattedText(symbol.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 100, Brushes.Black, 1.0);
        return text.BuildGeometry(new Point(0, 0)).GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
    }

    /// <summary>Only the outer contours of the solid symbol: the holes that draw its inner lines are dropped.</summary>
    private static PathGeometry OuterFigures(PathGeometry geometry)
    {
        var figures = geometry.Figures.Select(f => (Figure: f, Bounds: new PathGeometry(new[] { f }).Bounds)).ToList();
        var outer = new PathGeometry { FillRule = FillRule.Nonzero };
        foreach (var (figure, bounds) in figures)
        {
            var inside = figures.Any(o => o.Figure != figure && o.Bounds.Contains(bounds) && o.Bounds != bounds);
            if (!inside) outer.Figures.Add(figure.Clone());
        }
        return outer;
    }

    public static void Draw(DrawingContext dc, int piece, Rect square, double opacity = 1)
    {
        if (piece == Piece.None) return;
        var (body, lines, solid) = Shape(Piece.Type(piece));
        var white = Piece.SideOf(piece) == Side.White;
        if (opacity < 1) dc.PushOpacity(opacity);
        dc.PushTransform(new MatrixTransform(square.Width, 0, 0, square.Height, square.X, square.Y));
        dc.PushTransform(new TranslateTransform(0.012, 0.03));
        dc.DrawGeometry(Shadow, null, body);
        dc.Pop();
        if (white)
        {
            dc.DrawGeometry(WhiteFill, WhiteEdge, body);
            dc.DrawGeometry(WhiteLines, null, lines);
        }
        else
        {
            dc.DrawGeometry(BlackLines, BlackEdge, body);
            dc.DrawGeometry(BlackFill, null, solid);
        }
        dc.Pop();
        if (opacity < 1) dc.Pop();
    }

    private static readonly Dictionary<int, DrawingImage> Images = new();

    /// <summary>A piece as a small picture (captured pieces, promotion choice). Cached, so it can be used often.</summary>
    public static DrawingImage Image(int piece)
    {
        lock (Images)
            if (Images.TryGetValue(piece, out var cached)) return cached;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 100, 100));
            Draw(dc, piece, new Rect(0, 0, 100, 100));
        }
        var image = new DrawingImage(group);
        image.Freeze();
        lock (Images) Images[piece] = image;
        return image;
    }
}

/// <summary>
/// The board: squares, coordinates, last move, selection, legal targets (dots for quiet moves, rings for captures),
/// check and pieces. Moves are chosen by clicking a piece and a target or by dragging. Moves slide in smoothly.
/// </summary>
public sealed class ChessBoardView : FrameworkElement
{
    private const double AnimationMs = 170;

    private int[] _board = new int[64];
    private IReadOnlyList<Move> _legal = Array.Empty<Move>();
    private int _selected = -1;
    private int _hover = -1;
    private bool _dragging;
    private bool _pressedOnSelected;
    private Point _dragPoint;
    private Point _pressPoint;

    // Running slide animation: up to two pieces (king and rook when castling) and a captured piece fading out.
    private readonly List<(int Piece, int From, int To)> _sliding = new();
    private (int Piece, int Square) _fading;
    private DateTime _animationStart;
    private bool _animating;

    public event Action<int, int>? MoveRequested;

    public BoardTheme Theme { get; set; } = BoardTheme.All[0];
    public bool Flipped { get; set; }
    public bool ShowHints { get; set; } = true;
    public bool ShowCoordinates { get; set; } = true;
    public bool Animate { get; set; } = true;
    public bool InputEnabled { get; set; }
    public (int From, int To)? LastMove { get; set; }
    public int CheckSquare { get; set; } = -1;
    public int Selected => _selected;
    internal bool IsAnimating => _animating;

    public ChessBoardView()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
    }

    /// <summary>Shows a new position. With a move given, the moved piece slides from its old square.</summary>
    public void SetPosition(int[] board, IReadOnlyList<Move> legal, Move? animate = null, int[]? before = null)
    {
        _legal = legal;
        if (_selected >= 0 && !legal.Any(m => m.From == _selected)) _selected = -1;
        StopAnimation();
        if (animate is { } move && Animate && before is not null && !move.IsNull)
        {
            _sliding.Add((board[move.To], move.From, move.To));
            if (move.IsCastle)
            {
                var (rookFrom, rookTo) = (move.Flags & MoveFlags.CastleKing) != 0 ? (move.From + 3, move.From + 1) : (move.From - 4, move.From - 1);
                _sliding.Add((board[rookTo], rookFrom, rookTo));
            }
            var victimSquare = (move.Flags & MoveFlags.EnPassant) != 0 ? move.To + (Square.Rank(move.From) == 4 ? -8 : 8) : move.To;
            _fading = move.IsCapture ? (before[victimSquare], victimSquare) : (Piece.None, -1);
            _animationStart = DateTime.UtcNow;
            _animating = true;
            CompositionTarget.Rendering += OnFrame;
        }
        _board = (int[])board.Clone();
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _selected = -1;
        _dragging = false;
        InvalidateVisual();
    }

    /// <summary>Selects a square as if it were clicked (tests and keyboard).</summary>
    internal void Click(int square) => HandlePress(square);

    private void StopAnimation()
    {
        if (_animating) CompositionTarget.Rendering -= OnFrame;
        _animating = false;
        _sliding.Clear();
        _fading = (Piece.None, -1);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if ((DateTime.UtcNow - _animationStart).TotalMilliseconds >= AnimationMs) StopAnimation();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(side)) side = 480;
        return new Size(side, side);
    }

    private double SquareSize => Math.Min(ActualWidth, ActualHeight) / 8;

    private Rect SquareRect(int square)
    {
        var size = SquareSize;
        var file = Square.File(square);
        var rank = Square.Rank(square);
        var x = Flipped ? 7 - file : file;
        var y = Flipped ? rank : 7 - rank;
        return new Rect(x * size, y * size, size, size);
    }

    private int SquareAt(Point point)
    {
        var size = SquareSize;
        if (size <= 0) return -1;
        var x = (int)Math.Floor(point.X / size);
        var y = (int)Math.Floor(point.Y / size);
        if (x is < 0 or > 7 || y is < 0 or > 7) return -1;
        return Flipped ? Square.Of(7 - x, y) : Square.Of(x, 7 - y);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = SquareSize;
        if (size <= 0) return;
        var full = new Rect(0, 0, size * 8, size * 8);
        dc.PushClip(new RectangleGeometry(full, size * 0.12, size * 0.12));
        var light = new SolidColorBrush(Theme.Light);
        var dark = new SolidColorBrush(Theme.Dark);
        dc.DrawRectangle(light, null, full);
        for (var sq = 0; sq < 64; sq++)
            if (!Square.IsLight(sq)) dc.DrawRectangle(dark, null, SquareRect(sq));

        if (LastMove is { } last)
        {
            var mark = new SolidColorBrush(Theme.LastMove);
            dc.DrawRectangle(mark, null, SquareRect(last.From));
            dc.DrawRectangle(mark, null, SquareRect(last.To));
        }
        if (_selected >= 0) dc.DrawRectangle(new SolidColorBrush(Theme.Selected), null, SquareRect(_selected));
        if (CheckSquare >= 0)
        {
            var glow = new RadialGradientBrush(Color.FromArgb(0xF0, 0xFF, 0x2A, 0x2A), Color.FromArgb(0, 0xE0, 0x1E, 0x1E)) { GradientOrigin = new Point(0.5, 0.5) };
            glow.GradientStops.Insert(1, new GradientStop(Color.FromArgb(0xB0, 0xF0, 0x30, 0x30), 0.45));
            dc.DrawRectangle(glow, null, SquareRect(CheckSquare));
        }

        if (ShowCoordinates) DrawCoordinates(dc, size, light, dark);

        // Targets of the selected piece.
        var targets = _selected >= 0 ? _legal.Where(m => m.From == _selected).Select(m => m.To).Distinct().ToList() : new List<int>();
        if (ShowHints)
        {
            var hint = new SolidColorBrush(Color.FromArgb(0x48, 0x10, 0x18, 0x10));
            foreach (var to in targets)
            {
                var rect = SquareRect(to);
                var center = new Point(rect.X + size / 2, rect.Y + size / 2);
                if (_board[to] != Piece.None || _legal.Any(m => m.From == _selected && m.To == to && m.IsCapture))
                {
                    // A capture: a ring around the piece that can be taken.
                    var ring = new PathGeometry { FillRule = FillRule.EvenOdd };
                    ring.AddGeometry(new RectangleGeometry(rect));
                    ring.AddGeometry(new EllipseGeometry(center, size * 0.52, size * 0.52));
                    dc.PushClip(new RectangleGeometry(rect));
                    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x58, 0x10, 0x18, 0x10)), null, ring);
                    dc.Pop();
                }
                else dc.DrawEllipse(hint, null, center, size * 0.16, size * 0.16);
            }
        }
        if (_hover >= 0 && targets.Contains(_hover))
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), Math.Max(2, size * 0.05)), Inset(SquareRect(_hover), size * 0.025));

        // Pieces; sliding and dragged pieces are drawn last, on top.
        var t = _animating ? Math.Clamp((DateTime.UtcNow - _animationStart).TotalMilliseconds / AnimationMs, 0, 1) : 1;
        var eased = 1 - Math.Pow(1 - t, 3);
        for (var sq = 0; sq < 64; sq++)
        {
            if (_board[sq] == Piece.None) continue;
            if (_sliding.Any(s => s.To == sq)) continue;
            if (_dragging && sq == _selected) continue;
            PieceArt.Draw(dc, _board[sq], SquareRect(sq));
        }
        if (_animating && _fading.Piece != Piece.None) PieceArt.Draw(dc, _fading.Piece, SquareRect(_fading.Square), 1 - eased);
        foreach (var (piece, from, to) in _sliding)
        {
            var a = SquareRect(from);
            var b = SquareRect(to);
            PieceArt.Draw(dc, piece, new Rect(a.X + (b.X - a.X) * eased, a.Y + (b.Y - a.Y) * eased, size, size));
        }
        if (_dragging && _selected >= 0 && _board[_selected] != Piece.None)
        {
            var big = size * 1.12;
            PieceArt.Draw(dc, _board[_selected], new Rect(_dragPoint.X - big / 2, _dragPoint.Y - big / 2, big, big));
        }
        dc.Pop();
    }

    private static Rect Inset(Rect rect, double by) => new(rect.X + by, rect.Y + by, Math.Max(0, rect.Width - 2 * by), Math.Max(0, rect.Height - 2 * by));

    private void DrawCoordinates(DrawingContext dc, double size, Brush light, Brush dark)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var fontSize = Math.Max(8, size * 0.17);
        for (var i = 0; i < 8; i++)
        {
            // Files along the bottom edge, ranks along the left edge, in the colour of the other square type.
            var fileSquare = Flipped ? Square.Of(7 - i, 7) : Square.Of(i, 0);
            var rect = SquareRect(fileSquare);
            var text = new FormattedText(((char)('a' + Square.File(fileSquare))).ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize,
                Square.IsLight(fileSquare) ? dark : light, dpi);
            dc.DrawText(text, new Point(rect.Right - text.Width - size * 0.06, rect.Bottom - text.Height - size * 0.02));

            var rankSquare = Flipped ? Square.Of(7, i) : Square.Of(0, 7 - i);
            rect = SquareRect(rankSquare);
            text = new FormattedText((Square.Rank(rankSquare) + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize,
                Square.IsLight(rankSquare) ? dark : light, dpi);
            dc.DrawText(text, new Point(rect.X + size * 0.05, rect.Y + size * 0.02));
        }
    }

    // ───────────────────────── input ─────────────────────────

    private bool IsMovable(int square) => _legal.Any(m => m.From == square);

    private bool IsTarget(int square) => _selected >= 0 && _legal.Any(m => m.From == _selected && m.To == square);

    /// <summary>Click logic: pick a piece, then a target. Clicking another own piece switches the selection.</summary>
    private bool HandlePress(int square)
    {
        if (!InputEnabled || square < 0) return false;
        if (IsTarget(square))
        {
            var from = _selected;
            _selected = -1;
            _dragging = false;
            InvalidateVisual();
            MoveRequested?.Invoke(from, square);
            return false;
        }
        if (IsMovable(square))
        {
            _pressedOnSelected = _selected == square;
            _selected = square;
            InvalidateVisual();
            return true;
        }
        _selected = -1;
        InvalidateVisual();
        return false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        if (HandlePress(SquareAt(point)))
        {
            _pressPoint = point;
            _dragPoint = point;
            _dragging = false;
            CaptureMouse();
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var hover = SquareAt(point);
        var redraw = hover != _hover;
        _hover = hover;
        if (IsMouseCaptured && _selected >= 0)
        {
            if (!_dragging && (point - _pressPoint).Length > SquareSize * 0.12) _dragging = true;
            _dragPoint = point;
            redraw |= _dragging;
        }
        Cursor = InputEnabled && (IsMovable(hover) || IsTarget(hover) || _dragging) ? Cursors.Hand : Cursors.Arrow;
        if (redraw) InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        var square = SquareAt(e.GetPosition(this));
        if (_dragging)
        {
            _dragging = false;
            if (square != _selected && IsTarget(square)) HandlePress(square);
            else InvalidateVisual();
        }
        else if (_pressedOnSelected && square == _selected)
        {
            // Second click on the selected piece puts it down again.
            _selected = -1;
            InvalidateVisual();
        }
        _pressedOnSelected = false;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
        {
            _dragging = false;
            InvalidateVisual();
        }
    }
}
