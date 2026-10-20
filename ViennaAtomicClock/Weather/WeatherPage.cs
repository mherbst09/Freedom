using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ViennaAtomicClock.Weather;

/// <summary>
/// Weather page inspired by the structure of a phone weather app: big current conditions over an animated sky,
/// warnings, next-hour precipitation, hourly strip, 10-day list, map and a grid of detail cards.
/// </summary>
internal sealed partial class WeatherPage : Grid
{
    private const double WideWidth = 1200;
    private const double MediumWidth = 800;

    private readonly WeatherService _service;
    private readonly IGeocodingService _geocoding;
    private readonly ILocationService _locator;
    private readonly WeatherSettings _settings;

    private readonly WeatherBackdrop _backdrop = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
    private readonly StackPanel _content = new() { Margin = new Thickness(22, 14, 22, 22) };
    private readonly Border _compactHeader = new() { VerticalAlignment = VerticalAlignment.Top, Opacity = 0, IsHitTestVisible = false, Padding = new Thickness(22, 10, 22, 10) };
    private readonly TextBlock _compactText = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Grid _overlayHost = new() { Visibility = Visibility.Collapsed };
    private readonly Border _glint = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _autoRefresh = new();
    private readonly DispatcherTimer _glintTimer = new() { Interval = TimeSpan.FromSeconds(13) };

    private WeatherSnapshot? _snapshot;
    private WeatherLocation _location;
    private WeatherLoadError _error;
    private bool _refreshing;
    private bool _firstData = true;
    private CancellationTokenSource? _refreshCancel;
    private string _layoutMode = "";
    private DateTimeOffset _lastLocate = DateTimeOffset.MinValue;

    /// <summary>Raised when the header line of the main window should change.</summary>
    public event Action? Changed;

    public IWeatherNotificationService? Notifier { get; set; }

    public WeatherPage() : this(new WeatherService(new OpenMeteoProvider(), new WeatherFileCache()), new OpenMeteoGeocoding(), new WindowsLocationService(), WeatherStore.Load())
    {
    }

    public WeatherPage(WeatherService service, IGeocodingService geocoding, ILocationService locator, WeatherSettings settings)
    {
        _service = service;
        _geocoding = geocoding;
        _locator = locator;
        _settings = settings;
        WeatherStore.Normalize(_settings);
        if (geocoding is OpenMeteoGeocoding search)
            search.PreferredCountries = () => _settings.Locations.Select(l => l.CountryCode).Where(c => c.Length > 0).Distinct();
        _location = WeatherStore.Selected(_settings);

        ClipToBounds = true;
        _backdrop.Radius = 12;
        Children.Add(_backdrop);

        _scroll.Content = _content;
        _scroll.ScrollChanged += (_, _) => UpdateCompactHeader();
        Children.Add(_scroll);

        _compactHeader.Background = new LinearGradientBrush(Color.FromArgb(150, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90);
        _compactHeader.Child = _compactText;
        Children.Add(_compactHeader);

        _glint.Background = MakeGlint();
        _glint.RenderTransform = new TranslateTransform();
        Children.Add(_glint);

        Children.Add(_overlayHost);

        _clock.Tick += (_, _) => UpdateLiveTexts();
        _autoRefresh.Tick += async (_, _) => await AutoRefreshAsync();
        _glintTimer.Tick += (_, _) => PlayGlint();
        IsVisibleChanged += async (_, _) => await OnVisibilityChangedAsync();
        SizeChanged += (_, _) => ApplyLayout();
        PreviewKeyDown += OnPreviewKeyDown;
        Loc.Changed += () => Rebuild();
        Loaded += async (_, _) => await StartAsync();
        Unloaded += (_, _) => { _clock.Stop(); _glintTimer.Stop(); };

        ApplyAnimationSettings();
        _snapshot = _service.Cached(_location.Id);
        Rebuild();
    }

    public WeatherSettings Settings => _settings;
    public WeatherSnapshot? Snapshot => _snapshot;
    public WeatherLocation Location => _location;
    private WeatherFormatter Format => new(_settings, Loc.Culture);
    private DateTimeOffset PlaceNow => WeatherTime.ToPlace(DateTimeOffset.UtcNow, _snapshot?.TimeZoneId ?? _location.TimeZoneId);

    /// <summary>Header line for the main window ("18° · Salzburg").</summary>
    public string StatusLine => _snapshot is { } s ? $"{Format.Degrees(s.Current.Temperature)} · {_location.Name}" : _location.Name;

    private bool _started;

    private async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        ConfigureAutoRefresh();
        _clock.Start();
        if (_settings.AutoLocation) await LocateAsync(selectIt: _settings.SelectedLocationId is null || _settings.SelectedLocationId == WeatherStore.CurrentLocationId);
        await RefreshAsync(force: false);
    }

    private async Task OnVisibilityChangedAsync()
    {
        if (IsVisible)
        {
            _clock.Start();
            UpdateLiveTexts();
            ApplyAnimationSettings();
            if (_snapshot is null || DateTimeOffset.UtcNow - _snapshot.FetchedAt > TimeSpan.FromMinutes(10)) await RefreshAsync(force: false);
        }
        else
        {
            _clock.Stop();
            _glintTimer.Stop();
        }
    }

    private void ConfigureAutoRefresh()
    {
        _autoRefresh.Stop();
        if (!_settings.AutoRefresh) return;
        _autoRefresh.Interval = TimeSpan.FromMinutes(_settings.RefreshMinutes);
        _autoRefresh.Start();
    }

    private async Task AutoRefreshAsync()
    {
        if (_settings.AutoLocation && DateTimeOffset.UtcNow - _lastLocate > TimeSpan.FromMinutes(25)) await LocateAsync(selectIt: false);
        await RefreshAsync(force: false);
    }

    /// <summary>Loads fresh data while the cached snapshot stays visible.</summary>
    public async Task RefreshAsync(bool force)
    {
        if (_refreshing && !force) return;
        _refreshCancel?.Cancel();
        _refreshCancel = new CancellationTokenSource();
        var token = _refreshCancel.Token;
        var location = _location;
        _refreshing = true;
        UpdateStatus();
        try
        {
            var result = await _service.RefreshAsync(location, force, token);
            if (token.IsCancellationRequested || location != _location) return;
            _error = result.Error;
            if (result.Snapshot is not null)
            {
                var changed = _snapshot is null || _snapshot.FetchedAt != result.Snapshot.FetchedAt || _snapshot.Location.Id != result.Snapshot.Location.Id;
                _snapshot = result.Snapshot;
                if (changed) Rebuild();
                if (!result.FromCache) CheckNotifications();
            }
            else if (_snapshot is null) Rebuild();
            WeatherStore.Save(_settings);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (location == _location)
            {
                _refreshing = false;
                UpdateStatus();
            }
        }
    }

    public async Task SelectLocationAsync(WeatherLocation location)
    {
        CloseOverlay();
        _location = location;
        _settings.SelectedLocationId = location.Id;
        WeatherStore.Save(_settings);
        _snapshot = _service.Cached(location.Id);
        _error = WeatherLoadError.None;
        _firstData = true;
        Rebuild();
        _scroll.ScrollToTop();
        await RefreshAsync(force: false);
    }

    /// <summary>Asks Windows for the position and keeps a "current location" entry up to date.</summary>
    private async Task<bool> LocateAsync(bool selectIt)
    {
        _lastLocate = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var position = await _locator.GetPositionAsync(timeout.Token);
            if (position is not { } p)
            {
                ShowNotice(Loc.T("wx.err.location"));
                return false;
            }

            var entry = _settings.Locations.FirstOrDefault(l => l.IsCurrentLocation);
            var moved = entry is null || Math.Abs(entry.Latitude - p.Latitude) > 0.02 || Math.Abs(entry.Longitude - p.Longitude) > 0.02;
            if (entry is null)
            {
                entry = new WeatherLocation { Id = WeatherStore.CurrentLocationId, IsCurrentLocation = true, Name = Loc.T("wx.currentLocation") };
                _settings.Locations.Insert(0, entry);
            }
            entry.Latitude = p.Latitude;
            entry.Longitude = p.Longitude;
            if (moved || entry.Name == Loc.T("wx.currentLocation"))
            {
                try
                {
                    var named = await _geocoding.ReverseAsync(p.Latitude, p.Longitude, Loc.Language, timeout.Token);
                    if (named is { Name.Length: > 0 })
                    {
                        entry.Name = named.Name;
                        entry.Region = named.Region;
                        entry.Country = named.Country;
                        entry.CountryCode = named.CountryCode;
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException || !timeout.IsCancellationRequested)
                {
                    // The coordinates are enough for the forecast.
                }
                entry.TimeZoneId = "";
            }
            WeatherStore.Save(_settings);
            if (selectIt) await SelectLocationAsync(entry);
            return true;
        }
        catch
        {
            ShowNotice(Loc.T("wx.err.location"));
            return false;
        }
    }

    private void CheckNotifications()
    {
        if (_snapshot is null || Notifier is null) return;
        var format = Format;
        if (_settings.NotifySevere)
            foreach (var alert in _snapshot.Alerts.Where(a => a.Severity >= AlertSeverity.Severe && a.End > DateTimeOffset.UtcNow))
            {
                if (_settings.Announced.Contains(alert.Id)) continue;
                _settings.Announced.Add(alert.Id);
                Notifier.Notify($"{AlertGlyph(alert.Kind)} {Loc.T("wx.alert.severity." + alert.Severity)} · {_location.Name}", alert.Title, alert.Severity);
            }

        if (_settings.NotifyPrecipitation && WeatherText.NextHour(_snapshot.Minutely, PlaceNow) is { Kind: MinuteOutlookKind.Starts, Minutes: <= 30 } outlook)
        {
            var key = $"precip-{_location.Id}-{outlook.Time:yyyyMMddHH}";
            if (!_settings.Announced.Contains(key))
            {
                _settings.Announced.Add(key);
                Notifier.Notify("☂ " + _location.Name, WeatherText.Describe(outlook, format), null);
            }
        }
        WeatherStore.Normalize(_settings);
    }

    private void ApplyAnimationSettings()
    {
        var animate = _settings.Animations && !_settings.ReducedMotion && SystemParameters.ClientAreaAnimation;
        _backdrop.Animate = animate;
        ChartBase.AnimationsEnabled = !_settings.ReducedMotion && SystemParameters.ClientAreaAnimation;
        if (animate && IsVisible && _snapshot is not null && WeatherBackdrop.SceneFor(_snapshot.Current.Condition, _snapshot.Current.IsDay) is WeatherScene.Rain or WeatherScene.Thunder)
            _glintTimer.Start();
        else
            _glintTimer.Stop();
    }

    private void UpdateScene()
    {
        if (_snapshot is null)
        {
            _backdrop.SetScene(WeatherScene.CloudyDay, 0, 0.3, 0);
            return;
        }
        var current = _snapshot.Current;
        var scene = WeatherBackdrop.SceneFor(current.Condition, current.IsDay);
        // Twilight: strongest at sunrise/sunset, fading out one hour before and after.
        var twilight = 0.0;
        var now = PlaceNow;
        var today = _snapshot.Daily.FirstOrDefault(d => d.Date.Date == now.Date);
        foreach (var edge in new[] { today?.Sunrise, today?.Sunset })
            if (edge is { } t)
                twilight = Math.Max(twilight, 1 - Math.Min(1, Math.Abs((now - t).TotalMinutes) / 60));
        var intensity = current.Precipitation is { } p ? Math.Clamp(p / 4, 0.1, 1) : 0.4;
        if (current.Condition is WeatherCondition.HeavyRain or WeatherCondition.HeavySnow) intensity = 1;
        _backdrop.SetScene(scene, twilight, (current.CloudCover ?? 50) / 100, intensity);
        ApplyAnimationSettings();
    }

    private void PlayGlint()
    {
        if (!IsVisible || ActualWidth <= 0) return;
        var shift = (TranslateTransform)_glint.RenderTransform;
        _glint.Opacity = 1;
        var move = new DoubleAnimation(-ActualWidth, ActualWidth, TimeSpan.FromMilliseconds(1400)) { EasingFunction = new SineEase() };
        move.Completed += (_, _) => _glint.Opacity = 0;
        shift.BeginAnimation(TranslateTransform.XProperty, move);
    }

    private static Brush MakeGlint()
    {
        var brush = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0, 255, 255, 255), 0.35), new(Color.FromArgb(20, 255, 255, 255), 0.5), new(Color.FromArgb(0, 255, 255, 255), 0.65)
        }, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private void UpdateCompactHeader()
    {
        var show = _snapshot is not null && _scroll.VerticalOffset > Math.Max(80, _headerHeight - 70);
        if (show == _compactHeader.IsHitTestVisible) return;
        _compactHeader.IsHitTestVisible = show;
        Motion.To(_compactHeader, OpacityProperty, show ? 1 : 0, 180);
    }

    private double _headerHeight = 260;

    private void ApplyLayout()
    {
        var width = ActualWidth;
        if (width <= 0) return;
        var mode = width >= WideWidth ? "wide" : width >= MediumWidth ? "medium" : "narrow";
        _content.Margin = new Thickness(mode == "narrow" ? 12 : 22, 14, mode == "narrow" ? 12 : 22, 22);
        if (mode == _layoutMode) return;
        _layoutMode = mode;
        ArrangeMain();
    }

    /// <summary>Single column below 800 px, flexible grid up to 1200 px, two columns above.</summary>
    public string LayoutMode => _layoutMode;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _overlayHost.Visibility == Visibility.Visible)
        {
            CloseOverlay();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = RefreshAsync(force: true);
            e.Handled = true;
        }
    }
}
