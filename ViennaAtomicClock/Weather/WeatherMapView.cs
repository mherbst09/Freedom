using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ViennaAtomicClock.Weather;

/// <summary>OpenStreetMap tiles in memory and on disk (the tile policy asks for caching and an identifying user agent).</summary>
internal static class MapTiles
{
    private const int MemoryLimit = 320;
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock", "Weather", "tiles");
    private static readonly Dictionary<(int Z, int X, int Y), BitmapSource> Memory = new();
    private static readonly LinkedList<(int, int, int)> Order = new();
    private static readonly HashSet<(int, int, int)> Loading = new();
    private static readonly SemaphoreSlim Downloads = new(4);

    public static event Action? TileArrived;

    /// <summary>False keeps the map offline (tests).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The tile if it is ready; otherwise starts loading it and returns null.</summary>
    public static BitmapSource? Get(int z, int x, int y)
    {
        var key = (z, x, y);
        if (Memory.TryGetValue(key, out var tile)) return tile;
        if (Enabled && Loading.Add(key)) _ = LoadAsync(key);
        return null;
    }

    private static async Task LoadAsync((int Z, int X, int Y) key)
    {
        try
        {
            var path = Path.Combine(Folder, key.Z.ToString(), key.X.ToString(), key.Y + ".png");
            byte[]? bytes = null;
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(14))
                bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            if (bytes is null)
            {
                await Downloads.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    bytes = await WeatherHttp.Shared.GetByteArrayAsync($"https://tile.openstreetmap.org/{key.Z}/{key.X}/{key.Y}.png", cancel.Token).ConfigureAwait(false);
                }
                finally
                {
                    Downloads.Release();
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                }
                catch
                {
                    // Caching is optional.
                }
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Memory[key] = image;
                Order.AddLast(key);
                while (Order.Count > MemoryLimit)
                {
                    Memory.Remove(Order.First!.Value);
                    Order.RemoveFirst();
                }
                Loading.Remove(key);
                TileArrived?.Invoke();
            });
        }
        catch
        {
            Application.Current?.Dispatcher.BeginInvoke(() => Loading.Remove(key));
        }
    }
}

/// <summary>Colours and legend for every map layer.</summary>
internal static class MapPalette
{
    public static (double Value, Color Color)[] Stops(WeatherMapLayer layer) => layer switch
    {
        WeatherMapLayer.Temperature => new[]
        {
            (-20.0, Color.FromArgb(150, 0x8E, 0x44, 0xC8)), (-10, Color.FromArgb(150, 0x3A, 0x6E, 0xE8)), (0, Color.FromArgb(150, 0x40, 0xC4, 0xE8)),
            (10, Color.FromArgb(150, 0x5C, 0xC8, 0x62)), (20, Color.FromArgb(150, 0xF2, 0xD8, 0x3A)), (30, Color.FromArgb(150, 0xF4, 0x8C, 0x2E)), (40, Color.FromArgb(160, 0xD8, 0x2E, 0x2E))
        },
        WeatherMapLayer.Wind => new[]
        {
            (0.0, Color.FromArgb(40, 0xB8, 0xE8, 0xC8)), (15, Color.FromArgb(120, 0x7C, 0xD6, 0x8A)), (35, Color.FromArgb(150, 0xF2, 0xD8, 0x3A)),
            (60, Color.FromArgb(160, 0xF4, 0x8C, 0x2E)), (90, Color.FromArgb(170, 0xD8, 0x2E, 0x2E)), (120, Color.FromArgb(180, 0x8E, 0x2E, 0xA8))
        },
        WeatherMapLayer.AirQuality => new[]
        {
            (0.0, Color.FromArgb(130, 0x50, 0xC8, 0x78)), (20, Color.FromArgb(130, 0x50, 0xB4, 0xE6)), (40, Color.FromArgb(140, 0xF0, 0xDC, 0x3C)),
            (60, Color.FromArgb(150, 0xFF, 0x96, 0x32)), (80, Color.FromArgb(160, 0xE6, 0x3C, 0x3C)), (100, Color.FromArgb(170, 0x96, 0x32, 0x96))
        },
        WeatherMapLayer.Clouds => new[]
        {
            (0.0, Color.FromArgb(0, 255, 255, 255)), (40, Color.FromArgb(70, 0xF4, 0xF6, 0xF9)), (80, Color.FromArgb(140, 0xE2, 0xE6, 0xEC)), (100, Color.FromArgb(175, 0xC6, 0xCC, 0xD4))
        },
        WeatherMapLayer.Pressure => new[]
        {
            (975.0, Color.FromArgb(150, 0x8E, 0x44, 0xC8)), (995, Color.FromArgb(140, 0x3A, 0x6E, 0xE8)), (1013, Color.FromArgb(120, 0x5C, 0xC8, 0x62)),
            (1025, Color.FromArgb(140, 0xF2, 0xD8, 0x3A)), (1040, Color.FromArgb(150, 0xD8, 0x2E, 0x2E))
        },
        _ => new[]
        {
            (0.0, Color.FromArgb(0, 0x6C, 0xB4, 0xFF)), (0.1, Color.FromArgb(110, 0x8C, 0xC8, 0xFF)), (1, Color.FromArgb(160, 0x3A, 0x8C, 0xF0)),
            (4, Color.FromArgb(185, 0x1E, 0x50, 0xD0)), (10, Color.FromArgb(195, 0x7A, 0x2E, 0xC8)), (30, Color.FromArgb(205, 0xD0, 0x2E, 0x9C))
        }
    };

    public static Color ColorAt(WeatherMapLayer layer, double value)
    {
        var stops = Stops(layer);
        if (double.IsNaN(value)) return Colors.Transparent;
        if (value <= stops[0].Value) return stops[0].Color;
        for (var i = 1; i < stops.Length; i++)
        {
            if (value > stops[i].Value) continue;
            var t = (value - stops[i - 1].Value) / (stops[i].Value - stops[i - 1].Value);
            var a = stops[i - 1].Color;
            var b = stops[i].Color;
            return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }
        return stops[^1].Color;
    }
}

/// <summary>Slippy map with OSM tiles, a weather layer, markers, drag/zoom with mouse, keyboard and touch.</summary>
internal sealed class MapCanvas : FrameworkElement
{
    private const double TileSize = 256;
    private static readonly Pen ArrowPen = MakePen(Color.FromArgb(200, 0x1E, 0x2A, 0x38), 1.4);
    private static readonly Brush AttributionBack = WeatherIcons.Frozen(Color.FromArgb(170, 255, 255, 255));
    private static readonly Brush AttributionText = WeatherIcons.Frozen(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly Brush Blank = WeatherIcons.Frozen(Color.FromRgb(0xD9, 0xDE, 0xE4));

    private Point? _dragStart;
    private (double Lat, double Lon) _dragCenter;
    private BitmapSource? _overlay;
    private (double North, double South, double West, double East) _overlayBounds;
    private float[]? _arrows;
    private WeatherMapGrid? _grid;

    public MapCanvas(bool interactive)
    {
        Interactive = interactive;
        ClipToBounds = true;
        Focusable = interactive;
        Cursor = interactive ? Cursors.SizeAll : Cursors.Hand;
        IsManipulationEnabled = interactive;
        Loaded += (_, _) => MapTiles.TileArrived += InvalidateVisual;
        Unloaded += (_, _) => MapTiles.TileArrived -= InvalidateVisual;
    }

    public bool Interactive { get; }
    public double Latitude { get; private set; } = 47.8;
    public double Longitude { get; private set; } = 13.04;
    public double Zoom { get; private set; } = 6;
    public double Radius { get; set; }
    public List<(double Lat, double Lon, bool Current, string Name)> Markers { get; } = new();

    public void CenterOn(double lat, double lon, double? zoom = null)
    {
        Latitude = Math.Clamp(lat, -84, 84);
        Longitude = lon;
        if (zoom is { } z) Zoom = Math.Clamp(z, 3, 11);
        InvalidateVisual();
    }

    public void ZoomBy(double delta, Point? around = null)
    {
        var focus = around ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var (lat, lon) = ToGeo(focus);
        Zoom = Math.Clamp(Zoom + delta, 3, 11);
        // Keep the point under the cursor in place.
        var (fx, fy) = World(lat, lon, Zoom);
        var cx = fx - (focus.X - ActualWidth / 2);
        var cy = fy - (focus.Y - ActualHeight / 2);
        (Latitude, Longitude) = FromWorld(cx, cy, Zoom);
        InvalidateVisual();
    }

    /// <summary>Shows one time step of a layer grid.</summary>
    public void SetLayer(WeatherMapGrid? grid, int step)
    {
        _grid = grid;
        if (grid is null || grid.Values.Length == 0 || grid.Rows < 2 || grid.Columns < 2)
        {
            _overlay = null;
            _arrows = null;
            InvalidateVisual();
            return;
        }
        step = Math.Clamp(step, 0, grid.Values.Length - 1);
        _overlay = BuildOverlay(grid, step, out _overlayBounds);
        _arrows = grid.Directions?[step];
        InvalidateVisual();
    }

    /// <summary>Interpolates the grid (bilinear, in Mercator rows) into a small frozen bitmap.</summary>
    public static BitmapSource BuildOverlay(WeatherMapGrid grid, int step, out (double North, double South, double West, double East) bounds)
    {
        const int width = 96, height = 96;
        var rows = grid.Rows;
        var cols = grid.Columns;
        var north = grid.Latitudes[0];
        var south = grid.Latitudes[^1];
        var west = grid.Longitudes[0];
        var east = grid.Longitudes[^1];
        bounds = (north, south, west, east);
        var values = grid.Values[step];
        var pixels = new byte[width * height * 4];
        var yTop = MercatorY(north);
        var yBottom = MercatorY(south);

        for (var py = 0; py < height; py++)
        {
            var lat = InverseMercatorY(yTop + (yBottom - yTop) * (py + 0.5) / height);
            var gy = Math.Clamp((north - lat) / (north - south) * (rows - 1), 0, rows - 1.0001);
            var r0 = (int)gy;
            var ty = gy - r0;
            for (var px = 0; px < width; px++)
            {
                var gx = Math.Clamp((px + 0.5) / width * (cols - 1), 0, cols - 1.0001);
                var c0 = (int)gx;
                var tx = gx - c0;
                double V(int r, int c) => values[r * cols + c];
                var a = V(r0, c0);
                var b = V(r0, c0 + 1);
                var c = V(r0 + 1, c0);
                var d = V(r0 + 1, c0 + 1);
                var value = double.IsNaN(a + b + c + d)
                    ? new[] { a, b, c, d }.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Average()
                    : (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
                var color = MapPalette.ColorAt(grid.Layer, value);
                var i = (py * width + px) * 4;
                // Premultiplied BGRA.
                pixels[i] = (byte)(color.B * color.A / 255);
                pixels[i + 1] = (byte)(color.G * color.A / 255);
                pixels[i + 2] = (byte)(color.R * color.A / 255);
                pixels[i + 3] = color.A;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static double MercatorY(double lat)
    {
        var rad = lat * Math.PI / 180;
        return (1 - Math.Log(Math.Tan(rad) + 1 / Math.Cos(rad)) / Math.PI) / 2;
    }

    private static double InverseMercatorY(double y) => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y))) * 180 / Math.PI;

    private static (double X, double Y) World(double lat, double lon, double zoom)
    {
        var scale = TileSize * Math.Pow(2, zoom);
        return ((lon + 180) / 360 * scale, MercatorY(Math.Clamp(lat, -85, 85)) * scale);
    }

    private static (double Lat, double Lon) FromWorld(double x, double y, double zoom)
    {
        var scale = TileSize * Math.Pow(2, zoom);
        var lon = x / scale * 360 - 180;
        return (InverseMercatorY(y / scale), ((lon + 540) % 360) - 180);
    }

    public Point ToScreen(double lat, double lon)
    {
        var (cx, cy) = World(Latitude, Longitude, Zoom);
        var (x, y) = World(lat, lon, Zoom);
        return new Point(x - cx + ActualWidth / 2, y - cy + ActualHeight / 2);
    }

    public (double Lat, double Lon) ToGeo(Point screen)
    {
        var (cx, cy) = World(Latitude, Longitude, Zoom);
        return FromWorld(cx + screen.X - ActualWidth / 2, cy + screen.Y - ActualHeight / 2, Zoom);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var bounds = new Rect(RenderSize);
        dc.PushClip(new RectangleGeometry(bounds, Radius, Radius));
        dc.DrawRectangle(Blank, null, bounds);

        var level = (int)Math.Floor(Zoom);
        var scale = Math.Pow(2, Zoom - level);
        var (cx, cy) = World(Latitude, Longitude, level);
        var size = TileSize * scale;
        var originX = ActualWidth / 2 - cx * scale;
        var originY = ActualHeight / 2 - cy * scale;
        var count = 1 << level;
        var firstX = (int)Math.Floor(-originX / size);
        var firstY = Math.Max(0, (int)Math.Floor(-originY / size));
        var lastX = (int)Math.Floor((ActualWidth - originX) / size);
        var lastY = Math.Min(count - 1, (int)Math.Floor((ActualHeight - originY) / size));
        for (var ty = firstY; ty <= lastY; ty++)
            for (var tx = firstX; tx <= lastX; tx++)
            {
                var wrapped = ((tx % count) + count) % count;
                var rect = new Rect(originX + tx * size, originY + ty * size, size + 0.5, size + 0.5);
                var tile = MapTiles.Get(level, wrapped, ty);
                if (tile is not null) dc.DrawImage(tile, rect);
                else if (level > 3 && MapTiles.Get(level - 1, wrapped / 2, ty / 2) is { } parent)
                {
                    // Show the coarser tile until the sharp one has arrived.
                    var source = new Int32Rect((wrapped % 2) * 128, (ty % 2) * 128, 128, 128);
                    dc.DrawImage(new CroppedBitmap(parent, source), rect);
                }
            }

        if (_overlay is not null)
        {
            var topLeft = ToScreen(_overlayBounds.North, _overlayBounds.West);
            var bottomRight = ToScreen(_overlayBounds.South, _overlayBounds.East);
            dc.DrawImage(_overlay, new Rect(topLeft, bottomRight));
        }

        if (_arrows is not null && _grid is not null)
            for (var r = 0; r < _grid.Rows; r++)
                for (var c = 0; c < _grid.Columns; c++)
                {
                    var dir = _arrows[r * _grid.Columns + c];
                    if (float.IsNaN(dir)) continue;
                    var p = ToScreen(_grid.Latitudes[r], _grid.Longitudes[c]);
                    var rad = (dir + 180) * Math.PI / 180;
                    var tip = new Point(p.X + Math.Sin(rad) * 9, p.Y - Math.Cos(rad) * 9);
                    var tail = new Point(p.X - Math.Sin(rad) * 9, p.Y + Math.Cos(rad) * 9);
                    dc.DrawLine(ArrowPen, tail, tip);
                    dc.DrawLine(ArrowPen, tip, new Point(tip.X - Math.Sin(rad - 0.5) * 5, tip.Y + Math.Cos(rad - 0.5) * 5));
                    dc.DrawLine(ArrowPen, tip, new Point(tip.X - Math.Sin(rad + 0.5) * 5, tip.Y + Math.Cos(rad + 0.5) * 5));
                }

        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        foreach (var (lat, lon, current, _) in Markers)
        {
            var p = ToScreen(lat, lon);
            dc.DrawEllipse(Brushes.White, null, p, current ? 9 : 8, current ? 9 : 8);
            dc.DrawEllipse(current ? WeatherIcons.Frozen(Color.FromRgb(0x2F, 0x84, 0xF5)) : accent, null, p, 5.5, 5.5);
        }

        var credit = new FormattedText("© OpenStreetMap", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, AttributionText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var box = new Rect(ActualWidth - credit.Width - 10, ActualHeight - credit.Height - 4, credit.Width + 8, credit.Height + 2);
        dc.DrawRoundedRectangle(AttributionBack, null, box, 3, 3);
        dc.DrawText(credit, new Point(box.X + 4, box.Y + 1));
        dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Interactive) return;
        Focus();
        _dragStart = e.GetPosition(this);
        _dragCenter = (Latitude, Longitude);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is not { } start || !IsMouseCaptured) return;
        var now = e.GetPosition(this);
        var (cx, cy) = World(_dragCenter.Lat, _dragCenter.Lon, Zoom);
        (Latitude, Longitude) = FromWorld(cx - (now.X - start.X), cy - (now.Y - start.Y), Zoom);
        Latitude = Math.Clamp(Latitude, -84, 84);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragStart = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Interactive) return;
        ZoomBy(e.Delta > 0 ? 0.5 : -0.5, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        base.OnManipulationDelta(e);
        var (cx, cy) = World(Latitude, Longitude, Zoom);
        (Latitude, Longitude) = FromWorld(cx - e.DeltaManipulation.Translation.X, cy - e.DeltaManipulation.Translation.Y, Zoom);
        var scale = (e.DeltaManipulation.Scale.X + e.DeltaManipulation.Scale.Y) / 2;
        if (Math.Abs(scale - 1) > 0.001) ZoomBy(Math.Log2(scale), e.ManipulationOrigin);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Interactive) return;
        var (cx, cy) = World(Latitude, Longitude, Zoom);
        switch (e.Key)
        {
            case Key.Left: (Latitude, Longitude) = FromWorld(cx - 80, cy, Zoom); break;
            case Key.Right: (Latitude, Longitude) = FromWorld(cx + 80, cy, Zoom); break;
            case Key.Up: (Latitude, Longitude) = FromWorld(cx, cy - 80, Zoom); break;
            case Key.Down: (Latitude, Longitude) = FromWorld(cx, cy + 80, Zoom); break;
            case Key.OemPlus or Key.Add: ZoomBy(0.5); break;
            case Key.OemMinus or Key.Subtract: ZoomBy(-0.5); break;
            default: return;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    private static Pen MakePen(Color color, double thickness)
    {
        var pen = new Pen(WeatherIcons.Frozen(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }
}

/// <summary>Full map view: layers, zoom, timeline with play/pause, legend and place selection.</summary>
internal sealed class WeatherMapPanel : Grid
{
    private static readonly Dictionary<(string, WeatherMapLayer), (WeatherMapGrid Grid, DateTime Loaded)> GridCache = new();

    private readonly IWeatherProvider _provider;
    private readonly Func<WeatherFormatter> _format;
    private readonly MapCanvas _map = new(true);
    private readonly WrapPanel _layerChips = new();
    private readonly WrapPanel _placeChips = new();
    private readonly WrapPanel _rangeChips = new();
    private readonly Slider _timeline = new() { Minimum = 0, Maximum = 24, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 3 };
    private readonly TextBlock _timeLabel = Ui.Text("", 12, "TextSecondary");
    private readonly TextBlock _status = Ui.Text("", 11.5, "TextMuted");
    private readonly Border _legendBar = new() { Height = 8, CornerRadius = new CornerRadius(4) };
    private readonly Grid _legendLabels = new();
    private readonly Button _play;
    private readonly DispatcherTimer _player = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly List<WeatherLocation> _places;
    private WeatherLocation _place;
    private WeatherMapLayer _layer;
    private WeatherMapGrid? _grid;
    private int _range = 24;
    private CancellationTokenSource? _loading;

    public WeatherMapPanel(IWeatherProvider provider, WeatherLocation place, IEnumerable<WeatherLocation> places, WeatherMapLayer layer, Func<WeatherFormatter> format)
    {
        _provider = provider;
        _format = format;
        _place = place;
        _places = places.ToList();
        _layer = layer;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 240 });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel();
        top.Children.Add(_layerChips);
        top.Children.Add(_placeChips);
        Children.Add(top);

        var mapHost = new Grid { Margin = new Thickness(0, 6, 0, 8) };
        _map.Radius = 10;
        AutomationHelpers.Name(_map, Loc.T("wx.map.title"));
        mapHost.Children.Add(_map);
        var zoom = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10) };
        zoom.Children.Add(MapButton("+", Loc.T("wx.map.zoomIn"), () => _map.ZoomBy(1)));
        zoom.Children.Add(MapButton("−", Loc.T("wx.map.zoomOut"), () => _map.ZoomBy(-1)));
        zoom.Children.Add(MapButton("◎", Loc.T("wx.map.reset"), () => _map.CenterOn(_place.Latitude, _place.Longitude, 6.5)));
        mapHost.Children.Add(zoom);
        SetRow(mapHost, 1);
        Children.Add(mapHost);

        var bottom = new StackPanel();
        var timeRow = new Grid();
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timeRow.ColumnDefinitions.Add(new ColumnDefinition());
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _play = Ui.Button("ChipButton", "▶", (_, _) => TogglePlay(), Loc.T("wx.map.play"));
        _play.Margin = new Thickness(0, 0, 10, 0);
        _play.Height = 34;
        AutomationHelpers.Name(_play, Loc.T("wx.map.play"));
        timeRow.Children.Add(_play);
        _timeline.VerticalAlignment = VerticalAlignment.Center;
        _timeline.ValueChanged += (_, _) => ShowStep();
        AutomationHelpers.Name(_timeline, Loc.T("wx.map.timeline"));
        SetColumn(_timeline, 1);
        timeRow.Children.Add(_timeline);
        _timeLabel.Margin = new Thickness(10, 0, 0, 0);
        _timeLabel.VerticalAlignment = VerticalAlignment.Center;
        _timeLabel.MinWidth = 110;
        SetColumn(_timeLabel, 2);
        timeRow.Children.Add(_timeLabel);
        bottom.Children.Add(timeRow);
        bottom.Children.Add(_rangeChips);
        var legend = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        legend.Children.Add(_legendBar);
        legend.Children.Add(_legendLabels);
        bottom.Children.Add(legend);
        _status.Margin = new Thickness(0, 6, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        bottom.Children.Add(_status);
        SetRow(bottom, 2);
        Children.Add(bottom);

        _player.Tick += (_, _) => _timeline.Value = _timeline.Value >= _timeline.Maximum ? 0 : _timeline.Value + 1;
        Unloaded += (_, _) => { _player.Stop(); _loading?.Cancel(); };

        UpdateMarkers();
        _map.CenterOn(place.Latitude, place.Longitude, 6.5);
        BuildChips();
        _ = LoadAsync();
    }

    private static Button MapButton(string text, string tip, Action click)
    {
        var button = Ui.Button("ChipButton", text, (_, _) => click(), tip);
        button.Width = 38;
        button.Height = 38;
        button.Margin = new Thickness(0, 0, 0, 6);
        button.FontSize = 16;
        AutomationHelpers.Name(button, tip);
        return button;
    }

    private void UpdateMarkers()
    {
        _map.Markers.Clear();
        foreach (var place in _places.Where(p => p.IsCurrentLocation)) _map.Markers.Add((place.Latitude, place.Longitude, true, place.Name));
        _map.Markers.Add((_place.Latitude, _place.Longitude, false, _place.Name));
    }

    private void BuildChips()
    {
        _layerChips.Children.Clear();
        foreach (var layer in Enum.GetValues<WeatherMapLayer>())
        {
            var chip = Chip(Loc.T("wx.layer." + layer), layer == _layer, () => { _layer = layer; BuildChips(); _ = LoadAsync(); });
            _layerChips.Children.Add(chip);
        }

        _placeChips.Children.Clear();
        if (_places.Count > 1)
            foreach (var place in _places)
                _placeChips.Children.Add(Chip((place.IsCurrentLocation ? "⌖ " : "") + place.Name, place.Id == _place.Id, () =>
                {
                    _place = place;
                    UpdateMarkers();
                    _map.CenterOn(place.Latitude, place.Longitude, 6.5);
                    BuildChips();
                    _ = LoadAsync();
                }));

        _rangeChips.Children.Clear();
        foreach (var hours in new[] { 1, 12, 24 })
            _rangeChips.Children.Add(Chip(Loc.F("wx.map.hours", hours), hours == _range, () =>
            {
                _range = hours;
                BuildChips();
                ApplyRange();
            }));
    }

    private static Button Chip(string text, bool selected, Action click)
    {
        var chip = Ui.Button("ChipButton", text, (_, _) => click());
        chip.Height = 32;
        chip.Margin = new Thickness(0, 0, 6, 6);
        chip.Padding = new Thickness(12, 0, 12, 0);
        Ui.Select(chip, selected);
        AutomationHelpers.Name(chip, text);
        return chip;
    }

    private void TogglePlay()
    {
        if (_player.IsEnabled)
        {
            _player.Stop();
            _play.Content = "▶";
            AutomationHelpers.Name(_play, Loc.T("wx.map.play"));
        }
        else
        {
            _player.Start();
            _play.Content = "❚❚";
            AutomationHelpers.Name(_play, Loc.T("wx.map.pause"));
        }
    }

    private async Task LoadAsync()
    {
        _loading?.Cancel();
        _loading = new CancellationTokenSource();
        var token = _loading.Token;
        var key = (_place.Id, _layer);
        if (GridCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Loaded < TimeSpan.FromMinutes(30))
        {
            _grid = cached.Grid;
            ApplyRange();
            return;
        }

        _status.Text = Loc.T("wx.map.loading");
        try
        {
            var grid = await _provider.GetMapGridAsync(_place, _layer, token);
            if (token.IsCancellationRequested) return;
            GridCache[key] = (grid, DateTime.UtcNow);
            _grid = grid;
            ApplyRange();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch
        {
            _grid = null;
            _map.SetLayer(null, 0);
            _status.Text = Loc.T("wx.map.failed");
        }
    }

    private void ApplyRange()
    {
        var steps = _grid?.Values.Length ?? 1;
        _timeline.Maximum = Math.Max(1, Math.Min(_range, steps - 1));
        if (_timeline.Value > _timeline.Maximum) _timeline.Value = 0;
        UpdateLegend();
        ShowStep();
    }

    private void ShowStep()
    {
        var step = (int)_timeline.Value;
        _map.SetLayer(_grid, step);
        if (_grid is null || _grid.Times.Length == 0) return;
        var time = WeatherTime.ToPlace(_grid.Times[Math.Min(step, _grid.Times.Length - 1)], _place.TimeZoneId);
        _timeLabel.Text = step == 0 ? Loc.T("wx.now") : $"+{step} h · {_format().Time(time)}";
        _status.Text = Loc.T("wx.map.source");
    }

    private void UpdateLegend()
    {
        var stops = MapPalette.Stops(_layer);
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i < stops.Length; i++)
        {
            var c = stops[i].Color;
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(Math.Max(c.A, (byte)90), c.R, c.G, c.B), i / (double)(stops.Length - 1)));
        }
        gradient.Freeze();
        _legendBar.Background = gradient;

        _legendLabels.Children.Clear();
        _legendLabels.ColumnDefinitions.Clear();
        var format = _format();
        string Label(double v) => _layer switch
        {
            WeatherMapLayer.Temperature => format.Degrees(v),
            WeatherMapLayer.Wind => format.Wind(v),
            WeatherMapLayer.Pressure => format.Pressure(v),
            WeatherMapLayer.Precipitation => format.Precipitation(v) + "/h",
            WeatherMapLayer.Clouds => format.Percent(v),
            _ => v.ToString("0", format.Culture)
        };
        foreach (var index in new[] { 0, stops.Length / 2, stops.Length - 1 })
        {
            _legendLabels.ColumnDefinitions.Add(new ColumnDefinition());
            var text = Ui.Text(Label(stops[index].Value), 10.5, "TextMuted");
            text.HorizontalAlignment = index == 0 ? HorizontalAlignment.Left : index == stops.Length - 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            SetColumn(text, _legendLabels.ColumnDefinitions.Count - 1);
            _legendLabels.Children.Add(text);
        }
    }

    /// <summary>Grid for the small preview (shared cache with the full view).</summary>
    public static async Task<WeatherMapGrid?> PreviewGridAsync(IWeatherProvider provider, WeatherLocation place, CancellationToken token)
    {
        var key = (place.Id, WeatherMapLayer.Precipitation);
        if (GridCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Loaded < TimeSpan.FromMinutes(30)) return cached.Grid;
        try
        {
            var grid = await provider.GetMapGridAsync(place, WeatherMapLayer.Precipitation, token);
            GridCache[key] = (grid, DateTime.UtcNow);
            return grid;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>Screen reader names and help texts.</summary>
internal static class AutomationHelpers
{
    public static void Name(UIElement element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);
    public static void Help(UIElement element, string help) => System.Windows.Automation.AutomationProperties.SetHelpText(element, help);
}
