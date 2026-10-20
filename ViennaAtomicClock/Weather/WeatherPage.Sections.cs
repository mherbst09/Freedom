using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace ViennaAtomicClock.Weather;

internal sealed partial class WeatherPage
{
    private static readonly Brush HeaderText = WeatherIcons.Frozen(Colors.White);
    /// <summary>Windows icon font (Windows 10 and 11), independent of the theme's text font.</summary>
    private static readonly FontFamily SymbolFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Brush HeaderSoft = WeatherIcons.Frozen(Color.FromArgb(225, 255, 255, 255));

    private Grid? _toolbar;
    private TextBox? _searchBox;
    private Popup? _suggestionPopup;
    private StackPanel? _suggestionList;
    private int _suggestionIndex = -1;
    private List<WeatherLocation> _suggestions = new();
    private CancellationTokenSource? _searchCancel;
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private TextBlock? _statusText;
    private TextBlock? _noticeText;
    private RotateTransform? _refreshSpin;
    private TextBlock? _updatedText;
    private TemperatureText? _temperature;
    private double? _lastShownTemperature;

    private readonly Grid _main = new();
    private FrameworkElement? _hourlyCard;
    private FrameworkElement? _dailyCard;
    private FrameworkElement? _mapCard;
    private UniformGrid? _detailsGrid;
    private readonly List<FrameworkElement> _revealCards = new();
    private SunArc? _sunArc;

    /// <summary>Rebuilds the page from the current snapshot (also after a language or unit change).</summary>
    public void Rebuild()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Rebuild);
            return;
        }

        var offset = _scroll.VerticalOffset;
        _revealCards.Clear();
        _content.Children.Clear();
        _toolbar ??= BuildToolbar();
        _content.Children.Add(_toolbar);

        if (_snapshot is null)
        {
            _hourlyCard = _dailyCard = _mapCard = null;
            _detailsGrid = null;
            _main.Children.Clear();
            _content.Children.Add(_error == WeatherLoadError.None ? BuildSkeleton() : BuildErrorCard());
        }
        else
        {
            var header = BuildHeader(_snapshot);
            _content.Children.Add(header);
            header.SizeChanged += (_, _) => _headerHeight = header.ActualHeight;
            if (BuildAlerts(_snapshot) is { } alerts) _content.Children.Add(alerts);
            if (BuildMinuteCard(_snapshot) is { } minute) _content.Children.Add(minute);
            _hourlyCard = BuildHourly(_snapshot);
            _dailyCard = BuildDaily(_snapshot);
            _mapCard = BuildMapPreview();
            _detailsGrid = BuildDetails(_snapshot);
            _layoutMode = "";
            _content.Children.Add(_main);
            if (_snapshot.AirQuality is null)
            {
                var note = Ui.Text(Loc.T("wx.err.airQuality"), 11.5, "TextMuted");
                note.TextWrapping = TextWrapping.Wrap;
                note.Margin = new Thickness(4, 2, 0, 8);
                _content.Children.Add(Glass(note, 10));
            }
            _content.Children.Add(BuildAttribution(_snapshot));
            ApplyLayout();
            if (_layoutMode == "") ArrangeMain();
            _compactText.Text = $"{_location.Name}   {Format.Degrees(_snapshot.Current.Temperature)}   {WeatherText.Condition(_snapshot.Current.Condition, _snapshot.Current.IsDay)}";

            // Cards glide in when data first appears; later updates replace values without movement.
            if (_firstData && ChartBase.AnimationsEnabled)
                foreach (var card in _revealCards) Motion.PageIn(card);
            _firstData = false;
        }

        UpdateScene();
        UpdateStatus();
        UpdateCompactHeader();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _scroll.ScrollToVerticalOffset(offset));
        Changed?.Invoke();
    }

    private void ArrangeMain()
    {
        if (_hourlyCard is null || _dailyCard is null || _mapCard is null || _detailsGrid is null) return;
        foreach (var element in new FrameworkElement[] { _hourlyCard, _dailyCard, _mapCard, _detailsGrid })
            if (element.Parent is Panel parent) parent.Children.Remove(element);
        _main.Children.Clear();
        _main.ColumnDefinitions.Clear();
        _main.RowDefinitions.Clear();
        var width = ActualWidth;

        switch (_layoutMode)
        {
            case "wide":
            {
                _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
                _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var left = new StackPanel();
                left.Children.Add(_hourlyCard);
                left.Children.Add(_dailyCard);
                var right = new StackPanel();
                right.Children.Add(_mapCard);
                right.Children.Add(_detailsGrid);
                SetColumn(right, 2);
                _main.Children.Add(left);
                _main.Children.Add(right);
                _detailsGrid.Columns = 2;
                break;
            }
            case "medium":
            {
                _main.ColumnDefinitions.Add(new ColumnDefinition());
                _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                _main.ColumnDefinitions.Add(new ColumnDefinition());
                for (var i = 0; i < 3; i++) _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                SetColumnSpan(_hourlyCard, 3);
                SetRow(_dailyCard, 1);
                SetRow(_mapCard, 1);
                SetColumn(_mapCard, 2);
                SetRow(_detailsGrid, 2);
                SetColumnSpan(_detailsGrid, 3);
                _main.Children.Add(_hourlyCard);
                _main.Children.Add(_dailyCard);
                _main.Children.Add(_mapCard);
                _main.Children.Add(_detailsGrid);
                _detailsGrid.Columns = 3;
                break;
            }
            default:
            {
                var stack = new StackPanel();
                foreach (var element in new FrameworkElement[] { _hourlyCard, _dailyCard, _mapCard, _detailsGrid })
                {
                    SetRow(element, 0);
                    SetColumn(element, 0);
                    SetColumnSpan(element, 1);
                    stack.Children.Add(element);
                }
                _main.Children.Add(stack);
                _detailsGrid.Columns = width >= 520 ? 2 : 1;
                break;
            }
        }
    }

    // ───────────────────────── building blocks ─────────────────────────

    /// <summary>Translucent card in the active theme's colours, so text stays readable on any sky.</summary>
    private static Grid Glass(UIElement child, double padding = 16)
    {
        var host = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        var back = new Border { Opacity = 0.9 };
        back.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        back.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        var front = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(padding), Child = child };
        front.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        front.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        host.Children.Add(back);
        host.Children.Add(front);
        return host;
    }

    /// <summary>Makes any card a keyboard-focusable button with a soft hover and a visible focus ring.</summary>
    private static Button Clickable(FrameworkElement card, Action click, string name, string? tooltip = null)
    {
        var hover = new Border { Opacity = 0, IsHitTestVisible = false, Margin = card.Margin };
        hover.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        hover.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        var layers = new Grid();
        layers.Children.Add(card);
        layers.Children.Add(hover);
        var button = new Button
        {
            Content = layers, Cursor = Cursors.Hand, ToolTip = tooltip, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            Template = PlainTemplate, FocusVisualStyle = FocusRing
        };
        button.Click += (_, _) => click();
        button.MouseEnter += (_, _) => Motion.To(hover, OpacityProperty, 0.6);
        button.MouseLeave += (_, _) => Motion.To(hover, OpacityProperty, 0, 220);
        AutomationHelpers.Name(button, name);
        return button;
    }

    private static readonly ControlTemplate PlainTemplate = CreatePlainTemplate();
    private static readonly Style FocusRing = CreateFocusRing();

    private static ControlTemplate CreatePlainTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        template.VisualTree = presenter;
        template.Seal();
        return template;
    }

    private static Style CreateFocusRing()
    {
        var style = new Style(typeof(Control));
        var template = new ControlTemplate(typeof(Control));
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        ring.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        ring.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        ring.SetValue(MarginProperty, new Thickness(-3));
        template.VisualTree = ring;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Seal();
        return style;
    }

    private static StackPanel TitleRow(string glyph, string title)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var icon = Ui.Text(glyph, 13, "TextMuted");
        icon.Margin = new Thickness(0, 0, 7, 0);
        row.Children.Add(icon);
        row.Children.Add(Ui.Text(title.ToUpper(Loc.Culture), 11.5, "TextMuted", bold: true));
        return row;
    }

    private static TextBlock Wrapped(string text, double size = 12.5, string brush = "TextSecondary")
    {
        var block = Ui.Text(text, size, brush);
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    private Grid Section(UIElement content, string glyph, string title, double padding = 16)
    {
        var stack = new StackPanel();
        stack.Children.Add(TitleRow(glyph, title));
        stack.Children.Add(content);
        var card = Glass(stack, padding);
        _revealCards.Add(card);
        return card;
    }

    // ───────────────────────── toolbar, status, skeleton ─────────────────────────

    private Grid BuildToolbar()
    {
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 420 });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.RowDefinitions.Add(new RowDefinition());
        bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var placesContent = new StackPanel { Orientation = Orientation.Horizontal };
        placesContent.Children.Add(new TextBlock { Text = "", FontFamily = SymbolFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) });
        placesContent.Children.Add(new TextBlock { Text = Loc.T("wx.places"), VerticalAlignment = VerticalAlignment.Center });
        var places = Ui.Button("ChipButton", placesContent, (_, _) => OpenLocations(), Loc.T("wx.places.tip"));
        places.Height = 38;
        places.Margin = new Thickness(0, 0, 8, 0);
        AutomationHelpers.Name(places, Loc.T("wx.places"));
        bar.Children.Add(places);

        var (searchHost, searchBox) = Ui.Input(Loc.T("wx.search"));
        searchBox.Height = 38;
        AutomationHelpers.Name(searchBox, Loc.T("wx.search"));
        _searchBox = searchBox;
        searchBox.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        searchBox.PreviewKeyDown += SearchKeyDown;
        _searchDelay.Tick += async (_, _) => { _searchDelay.Stop(); await SearchAsync(); };
        SetColumn(searchHost, 1);
        bar.Children.Add(searchHost);

        _suggestionList = new StackPanel();
        var suggestionCard = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(6), Child = _suggestionList, MinWidth = 300 };
        suggestionCard.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        suggestionCard.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        suggestionCard.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        _suggestionPopup = new Popup
        {
            PlacementTarget = searchHost, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            VerticalOffset = 6, Child = suggestionCard
        };
        bar.Children.Add(_suggestionPopup);

        var spinIcon = new TextBlock { Text = "", FontFamily = SymbolFont, FontSize = 15, RenderTransformOrigin = new Point(0.5, 0.5) };
        _refreshSpin = new RotateTransform();
        spinIcon.RenderTransform = _refreshSpin;
        var refresh = Ui.Button("ChipButton", spinIcon, async (_, _) => await RefreshAsync(force: true), Loc.T("wx.refresh"));
        refresh.Width = 40;
        refresh.Height = 38;
        refresh.Margin = new Thickness(8, 0, 0, 0);
        AutomationHelpers.Name(refresh, Loc.T("wx.refresh"));
        SetColumn(refresh, 3);
        bar.Children.Add(refresh);

        var settings = Ui.Button("ChipButton", new TextBlock { Text = "", FontFamily = SymbolFont, FontSize = 15 }, (_, _) => OpenSettings(), Loc.T("wx.settings"));
        settings.Width = 40;
        settings.Height = 38;
        settings.Margin = new Thickness(8, 0, 0, 0);
        AutomationHelpers.Name(settings, Loc.T("wx.settings"));
        SetColumn(settings, 4);
        bar.Children.Add(settings);

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 0, 0) };
        _statusText = new TextBlock { FontSize = 12, Foreground = HeaderSoft, TextWrapping = TextWrapping.Wrap };
        _noticeText = new TextBlock { FontSize = 12, Foreground = HeaderSoft, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_statusText, System.Windows.Automation.AutomationLiveSetting.Polite);
        statusRow.Children.Add(_statusText);
        statusRow.Children.Add(_noticeText);
        SetRow(statusRow, 1);
        SetColumnSpan(statusRow, 5);
        bar.Children.Add(statusRow);
        return bar;
    }

    private void UpdateStatus()
    {
        if (_statusText is null) return;
        string text;
        if (_refreshing) text = Loc.T("wx.status.updating");
        else if (_error == WeatherLoadError.Offline) text = _snapshot is null ? Loc.T("wx.err.offlineNoData") : Loc.T("wx.err.offline");
        else if (_error == WeatherLoadError.Failed) text = Loc.T("wx.err.update");
        else if (_snapshot is null) text = "";
        else text = Loc.F("wx.status.updated", Ago(_snapshot.FetchedAt));

        if (_snapshot is not null && _service.IsStale(_snapshot) && !_refreshing) text = "⚠ " + Loc.F("wx.status.stale", Ago(_snapshot.FetchedAt));
        _statusText.Text = text;

        if (_refreshSpin is not null)
        {
            if (_refreshing && ChartBase.AnimationsEnabled)
                _refreshSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            else
                _refreshSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        }
        if (_updatedText is not null && _snapshot is not null)
            _updatedText.Text = Loc.F("wx.updatedAt", Format.Time(WeatherTime.ToPlace(_snapshot.FetchedAt, _snapshot.TimeZoneId)));
    }

    private static string Ago(DateTimeOffset time)
    {
        var minutes = (int)Math.Max(0, (DateTimeOffset.UtcNow - time).TotalMinutes);
        return minutes < 1 ? Loc.T("wx.ago.now") : minutes < 60 ? Loc.F("wx.ago.minutes", minutes) : minutes < 48 * 60 ? Loc.F("wx.ago.hours", minutes / 60) : Loc.F("wx.ago.days", minutes / 1440);
    }

    private void ShowNotice(string text)
    {
        if (_noticeText is null) return;
        _noticeText.Text = text;
        var clear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        clear.Tick += (_, _) => { clear.Stop(); if (_noticeText.Text == text) _noticeText.Text = ""; };
        clear.Start();
    }

    /// <summary>Called every 30 seconds: "updated x min ago", sun position and local times move on.</summary>
    private void UpdateLiveTexts()
    {
        UpdateStatus();
        if (_sunArc is not null)
        {
            _sunArc.Now = PlaceNow;
            _sunArc.InvalidateVisual();
        }
        UpdateScene();
    }

    private UIElement BuildSkeleton()
    {
        var stack = new StackPanel();
        Border Block(double width, double height, double margin = 10)
        {
            var block = new Border { Width = width, Height = height, Margin = new Thickness(0, 0, 0, margin), HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.35 };
            block.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
            block.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
            return block;
        }
        var header = new StackPanel { Margin = new Thickness(4, 18, 0, 22) };
        header.Children.Add(Block(220, 32));
        header.Children.Add(Block(150, 90));
        header.Children.Add(Block(260, 20));
        stack.Children.Add(header);
        foreach (var height in new[] { 130.0, 300, 220 })
        {
            var inner = new StackPanel();
            inner.Children.Add(Block(140, 12, 16));
            var filler = Block(double.NaN, height - 60);
            filler.HorizontalAlignment = HorizontalAlignment.Stretch;
            inner.Children.Add(filler);
            stack.Children.Add(Glass(inner));
        }
        AutomationHelpers.Name(stack, Loc.T("wx.status.loading"));
        return stack;
    }

    private UIElement BuildErrorCard()
    {
        var stack = new StackPanel { Margin = new Thickness(6) };
        stack.Children.Add(Ui.Text("☁", 40, "TextMuted"));
        var title = Ui.Text(Loc.T("wx.err.title"), 20, title: true);
        title.Margin = new Thickness(0, 8, 0, 6);
        stack.Children.Add(title);
        stack.Children.Add(Wrapped(_error == WeatherLoadError.Offline ? Loc.T("wx.err.offlineNoData") : Loc.T("wx.err.update")));
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var retry = Ui.Button("ActionButton", Loc.T("wx.retry"), async (_, _) => await RefreshAsync(force: true));
        retry.Padding = new Thickness(18, 0, 18, 0);
        retry.Margin = new Thickness(0, 0, 8, 8);
        buttons.Children.Add(retry);
        var other = Ui.Button("SecondaryButton", Loc.T("wx.otherPlace"), (_, _) => OpenLocations());
        other.Padding = new Thickness(18, 0, 18, 0);
        other.Margin = new Thickness(0, 0, 8, 8);
        buttons.Children.Add(other);
        stack.Children.Add(buttons);
        return Glass(stack, 22);
    }

    private UIElement BuildAttribution(WeatherSnapshot snapshot)
    {
        var parts = snapshot.Attribution.Concat(new[] { Loc.T("wx.attribution.map") }).Distinct();
        var text = new TextBlock { Text = string.Join("  ·  ", parts), FontSize = 10.5, Foreground = HeaderSoft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 0, 0) };
        return text;
    }

    // ───────────────────────── search ─────────────────────────

    private async Task SearchAsync()
    {
        if (_searchBox is null || _suggestionList is null || _suggestionPopup is null) return;
        var query = _searchBox.Text.Trim();
        _searchCancel?.Cancel();
        if (query.Length < 2)
        {
            _suggestionPopup.IsOpen = false;
            return;
        }
        _searchCancel = new CancellationTokenSource();
        var token = _searchCancel.Token;
        try
        {
            var results = await _geocoding.SearchAsync(query, Loc.Language, token);
            if (token.IsCancellationRequested) return;
            _suggestions = results.ToList();
            _suggestionIndex = _suggestions.Count > 0 ? 0 : -1;
            RenderSuggestions(_suggestions.Count == 0 ? Loc.T("wx.search.none") : null);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _suggestions.Clear();
            RenderSuggestions(Loc.T("wx.search.failed"));
        }
    }

    private void RenderSuggestions(string? message)
    {
        if (_suggestionList is null || _suggestionPopup is null) return;
        _suggestionList.Children.Clear();
        if (message is not null) _suggestionList.Children.Add(Wrapped(message, 12, "TextMuted"));
        for (var i = 0; i < _suggestions.Count; i++)
        {
            var place = _suggestions[i];
            var content = new StackPanel();
            content.Children.Add(Ui.Text(place.Name, 13, bold: true));
            content.Children.Add(Ui.Text(place.Subtitle, 11, "TextMuted"));
            var item = Ui.Button("ChipButton", content, async (_, _) => await ChooseSuggestionAsync(place));
            item.HorizontalContentAlignment = HorizontalAlignment.Left;
            item.Height = 46;
            item.Margin = new Thickness(0, 0, 0, 4);
            item.Padding = new Thickness(10, 0, 10, 0);
            Ui.Select(item, i == _suggestionIndex);
            AutomationHelpers.Name(item, $"{place.Name}, {place.Subtitle}");
            _suggestionList.Children.Add(item);
        }
        _suggestionPopup.IsOpen = true;
    }

    private async void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (_suggestionPopup is null || _searchBox is null) return;
        switch (e.Key)
        {
            case Key.Down when _suggestions.Count > 0:
                _suggestionIndex = (_suggestionIndex + 1) % _suggestions.Count;
                RenderSuggestions(null);
                e.Handled = true;
                break;
            case Key.Up when _suggestions.Count > 0:
                _suggestionIndex = (_suggestionIndex - 1 + _suggestions.Count) % _suggestions.Count;
                RenderSuggestions(null);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                if (_searchDelay.IsEnabled)
                {
                    _searchDelay.Stop();
                    await SearchAsync();
                }
                if (_suggestionIndex >= 0 && _suggestionIndex < _suggestions.Count) await ChooseSuggestionAsync(_suggestions[_suggestionIndex]);
                break;
            case Key.Escape:
                if (_suggestionPopup.IsOpen || _searchBox.Text.Length > 0)
                {
                    _suggestionPopup.IsOpen = false;
                    _searchBox.Clear();
                    e.Handled = true;
                }
                break;
        }
    }

    private async Task ChooseSuggestionAsync(WeatherLocation place)
    {
        if (_suggestionPopup is not null) _suggestionPopup.IsOpen = false;
        _searchBox?.Clear();
        var saved = WeatherStore.Add(_settings, place);
        WeatherStore.Save(_settings);
        await SelectLocationAsync(saved);
    }

    // ───────────────────────── header ─────────────────────────

    private FrameworkElement BuildHeader(WeatherSnapshot s)
    {
        var format = Format;
        var today = s.Daily.FirstOrDefault();
        var grid = new Grid { Margin = new Thickness(4, 12, 4, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.35 } };
        if (_location.IsCurrentLocation)
            text.Children.Add(new TextBlock { Text = "⌖  " + Loc.T("wx.currentLocation"), FontSize = 13, Foreground = HeaderSoft, Margin = new Thickness(2, 0, 0, 2) });
        text.Children.Add(new TextBlock { Text = _location.Name, FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = HeaderText, TextTrimming = TextTrimming.CharacterEllipsis });
        if (_location.Subtitle.Length > 0)
            text.Children.Add(new TextBlock { Text = _location.Subtitle, FontSize = 14, Foreground = HeaderSoft, Margin = new Thickness(2, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });

        _temperature = new TemperatureText(c => Format.Degrees(c)) { FontSize = 96, FontWeight = FontWeights.Light, Foreground = HeaderText, Margin = new Thickness(-4, -6, 0, -10) };
        _temperature.Show(s.Current.Temperature, _lastShownTemperature, ChartBase.AnimationsEnabled);
        _lastShownTemperature = s.Current.Temperature;
        text.Children.Add(_temperature);
        text.Children.Add(new TextBlock { Text = WeatherText.Condition(s.Current.Condition, s.Current.IsDay), FontSize = 22, Foreground = HeaderText, TextWrapping = TextWrapping.Wrap });
        var details = new List<string>();
        if (s.Current.ApparentTemperature is { } apparent) details.Add(Loc.F("wx.feelsLike", format.Degrees(apparent)));
        if (today is not null) details.Add($"{Loc.T("wx.high")} {format.Degrees(today.High)}   {Loc.T("wx.low")} {format.Degrees(today.Low)}");
        text.Children.Add(new TextBlock { Text = string.Join("     ", details), FontSize = 16, Foreground = HeaderSoft, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        _updatedText = new TextBlock { FontSize = 11.5, Foreground = HeaderSoft, Margin = new Thickness(0, 8, 0, 0) };
        text.Children.Add(_updatedText);
        grid.Children.Add(text);

        var icon = new AnimatedWeatherIcon { Width = 150, Height = 150, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        icon.Show(s.Current.Condition, s.Current.IsDay, _settings.Animations && !_settings.ReducedMotion && SystemParameters.ClientAreaAnimation);
        SetColumn(icon, 1);
        grid.Children.Add(icon);
        if (ActualWidth is > 0 and < 520) icon.Width = icon.Height = 96;

        var spoken = WeatherText.SpokenSummary(s, format);
        System.Windows.Automation.AutomationProperties.SetName(grid, spoken);
        grid.ToolTip = null;
        return grid;
    }

    // ───────────────────────── warnings ─────────────────────────

    private static string AlertGlyph(AlertKind kind) => kind switch
    {
        AlertKind.Wind => "≋", AlertKind.Rain => "☂", AlertKind.Snow => "❄", AlertKind.Ice => "❆",
        AlertKind.Thunderstorm => "⚡", AlertKind.Heat => "☀", AlertKind.Cold => "❅", AlertKind.Fog => "≡", _ => "⚠"
    };

    public static Color SeverityColor(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Extreme => Color.FromRgb(0xE0, 0x3A, 0x3A),
        AlertSeverity.Severe => Color.FromRgb(0xF2, 0x8C, 0x28),
        _ => Color.FromRgb(0xF2, 0xC9, 0x2E)
    };

    private UIElement? BuildAlerts(WeatherSnapshot s)
    {
        var active = s.Alerts.Where(a => a.End > DateTimeOffset.UtcNow).ToList();
        if (active.Count == 0) return null; // no empty placeholder
        var stack = new StackPanel();
        var format = Format;
        foreach (var alert in active)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Border { Background = WeatherIcons.Frozen(SeverityColor(alert.Severity)), CornerRadius = new CornerRadius(3) });
            var glyph = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
                Background = WeatherIcons.Frozen(Color.FromArgb(60, SeverityColor(alert.Severity).R, SeverityColor(alert.Severity).G, SeverityColor(alert.Severity).B)),
                Child = new TextBlock { Text = AlertGlyph(alert.Kind), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            glyph.Child.SetValue(TextBlock.ForegroundProperty, WeatherIcons.Frozen(SeverityColor(alert.Severity)));
            SetColumn(glyph, 1);
            row.Children.Add(glyph);
            var texts = new StackPanel();
            texts.Children.Add(Ui.Text($"⚠ {Loc.T("wx.alert.severity." + alert.Severity)} · {Loc.T("wx.alert.kind." + alert.Kind)}", 11.5, "TextSecondary", bold: true));
            var title = Wrapped(alert.Title, 14.5, "TextPrimary");
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0, 2, 0, 2);
            texts.Children.Add(title);
            texts.Children.Add(Ui.Text($"{alert.Issuer} · {AlertPeriod(alert, format)}{(alert.Region.Length > 0 ? " · " + alert.Region : "")}", 11.5, "TextMuted"));
            var summary = alert.Summary.Split('\n').FirstOrDefault() ?? "";
            if (summary.Length > 0)
            {
                var line = Wrapped(summary, 12);
                line.MaxHeight = 36;
                line.Margin = new Thickness(0, 4, 0, 0);
                texts.Children.Add(line);
            }
            SetColumn(texts, 2);
            row.Children.Add(texts);
            var card = Glass(row, 12);
            stack.Children.Add(Clickable(card, () => ShowAlert(alert), $"{Loc.T("wx.alert.severity." + alert.Severity)}: {alert.Title}"));
            _revealCards.Add(card);
        }
        return stack;
    }

    private static string AlertPeriod(WeatherAlert alert, WeatherFormatter format)
    {
        var start = TimeZoneInfo.ConvertTime(alert.Start, TimeZoneInfo.Local);
        var end = TimeZoneInfo.ConvertTime(alert.End, TimeZoneInfo.Local);
        var startDay = start.Date == end.Date ? "" : format.Weekday(start) + " ";
        return $"{startDay}{format.Time(start)} – {format.Weekday(end)} {format.Time(end)}";
    }

    // ───────────────────────── next-hour precipitation ─────────────────────────

    private UIElement? BuildMinuteCard(WeatherSnapshot s)
    {
        var now = PlaceNow;
        var format = Format;
        var stack = new StackPanel();
        var outlook = WeatherText.NextHour(s.Minutely, now);
        if (outlook is not null && s.Minutely is { } steps)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(outlook.Kind == MinuteOutlookKind.None
                ? WeatherIcons.Small(s.Current.Condition, s.Current.IsDay, 34)
                : WeatherIcons.Small(outlook.Type switch
                {
                    PrecipitationType.Snow => WeatherCondition.Snow,
                    PrecipitationType.Mixed => WeatherCondition.FreezingRain,
                    _ => WeatherCondition.Rain
                }, true, 34));
            var sentence = Wrapped(WeatherText.Describe(outlook, format), 15, "TextPrimary");
            sentence.Margin = new Thickness(10, 0, 0, 0);
            sentence.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(sentence);
            stack.Children.Add(header);
            if (outlook.Kind == MinuteOutlookKind.None)
            {
                var note = Ui.Text(Loc.T("wx.minute.resolution"), 10.5, "TextMuted");
                note.Margin = new Thickness(44, 2, 0, 0);
                stack.Children.Add(note);
                return Section(stack, "☂", Loc.T("wx.minute.title"));
            }

            // 5-minute bars from the 15-minute sums (mm per 15 min × 4 = mm/h).
            var values = new List<double>();
            var labels = new List<string>();
            for (var i = 0; i < 12; i++)
            {
                var at = now.AddMinutes(i * 5 + 2.5);
                var step = steps.FirstOrDefault(m => m.Time > at && m.Time - TimeSpan.FromMinutes(15) <= at);
                values.Add((step?.Precipitation ?? 0) * 4);
                labels.Add(i % 3 == 0 ? (i == 0 ? Loc.T("wx.now") : $"+{i * 5}") : "");
            }
            var chart = new BarChart
            {
                Height = 110, Margin = new Thickness(0, 12, 0, 0), FullScale = 10, BarBrush = "AccentBrush",
                Guides = new[] { (0.25, Loc.T("wx.intensity.Light")), (0.6, Loc.T("wx.intensity.Moderate")), (0.95, Loc.T("wx.intensity.Heavy")) }
            };
            chart.SetData(values, labels);
            AutomationHelpers.Name(chart, WeatherText.Describe(outlook, format));
            stack.Children.Add(chart);
            stack.Children.Add(Ui.Text(Loc.T("wx.minute.resolution"), 10.5, "TextMuted"));
            return Section(stack, "☂", Loc.T("wx.minute.title"));
        }

        // Fallback: hourly probability, clearly labelled as such.
        var next = s.Hourly.Where(h => h.Time > now.AddHours(-1)).Take(3).ToList();
        if (next.Count == 0 || next.All(h => h.PrecipitationProbability is null)) return null;
        var max = next.Max(h => h.PrecipitationProbability ?? 0);
        stack.Children.Add(Wrapped(max < 20 ? Loc.T("wx.min.none") : Loc.F("wx.minute.fallback", format.Percent(max)), 15, "TextPrimary"));
        stack.Children.Add(Ui.Text(Loc.T("wx.minute.fallbackNote"), 10.5, "TextMuted"));
        return Section(stack, "☂", Loc.T("wx.minute.title"));
    }

    // ───────────────────────── hourly strip ─────────────────────────

    private bool _allHours;

    private FrameworkElement BuildHourly(WeatherSnapshot s)
    {
        var format = Format;
        var now = PlaceNow;
        var hours = s.Hourly.Where(h => h.Time > now.AddHours(-1)).Take(_allHours ? 240 : 25).ToList();
        var markers = WeatherText.Markers(hours, s.Daily);
        var strip = new StackPanel { Orientation = Orientation.Horizontal };

        for (var i = 0; i < hours.Count; i++)
        {
            var hour = hours[i];
            var tag = markers.FirstOrDefault(m => m.Kind is HourMarkerKind.RainStart or HourMarkerKind.RainEnd or HourMarkerKind.TemperatureRise or HourMarkerKind.TemperatureDrop && m.Time == hour.Time);
            strip.Children.Add(HourItem(hour, i == 0, tag, format));
            foreach (var sun in markers.Where(m => m.Kind is HourMarkerKind.Sunrise or HourMarkerKind.Sunset && m.Time > hour.Time && m.Time < hour.Time.AddHours(1)))
                strip.Children.Add(SunItem(sun, format));
        }

        var scroller = new ScrollViewer
        {
            Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.HorizontalOnly, Focusable = false, Padding = new Thickness(0, 0, 0, 6)
        };
        HorizontalWheel.Attach(scroller);

        var body = new StackPanel();
        body.Children.Add(scroller);
        var more = Ui.Button("ChipButton", Loc.T(_allHours ? "wx.hourly.less" : "wx.hourly.more"), (_, _) =>
        {
            _allHours = !_allHours;
            Rebuild();
        });
        more.Height = 30;
        more.Margin = new Thickness(0, 8, 0, 0);
        more.HorizontalAlignment = HorizontalAlignment.Left;
        more.Padding = new Thickness(12, 0, 12, 0);
        body.Children.Add(more);
        if (s.Daily.FirstOrDefault() is { } today)
        {
            var summary = Wrapped(WeatherText.DaySummary(today, format), 12.5);
            summary.Margin = new Thickness(0, 0, 0, 10);
            body.Children.Insert(0, summary);
        }
        return Section(body, "◷", Loc.T("wx.hourly.title"));
    }

    private Button HourItem(HourlyForecast hour, bool first, HourMarker? tag, WeatherFormatter format)
    {
        var stack = new StackPanel { Width = 62, HorizontalAlignment = HorizontalAlignment.Center };
        var tagText = tag is null ? "" : Loc.T("wx.marker." + tag.Kind);
        var tagBlock = Ui.Text(tagText, 9.5, "AccentBrush", bold: true);
        tagBlock.HorizontalAlignment = HorizontalAlignment.Center;
        tagBlock.Height = 14;
        stack.Children.Add(tagBlock);
        var time = Ui.Text(first ? Loc.T("wx.now") : format.Hour(hour.Time), 12.5, first ? "TextPrimary" : "TextSecondary", bold: first);
        time.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(time);
        var icon = WeatherIcons.Small(hour.Condition, hour.IsDay, 34);
        icon.Margin = new Thickness(0, 6, 0, 2);
        stack.Children.Add(icon);
        var probability = Ui.Text(hour.PrecipitationProbability is { } p && p >= 10 ? format.Percent(p) : " ", 10.5, "AccentBrush", bold: true);
        probability.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(probability);
        var temperature = Ui.Text(format.Degrees(hour.Temperature), 16, bold: true);
        temperature.HorizontalAlignment = HorizontalAlignment.Center;
        temperature.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(temperature);
        if (hour.Wind is { } wind)
        {
            var windText = Ui.Text(format.Wind(wind.Speed), 9.5, "TextMuted");
            windText.HorizontalAlignment = HorizontalAlignment.Center;
            windText.ToolTip = Loc.T("wx.wind") + " " + format.Wind(wind.Speed);
            stack.Children.Add(windText);
        }

        var name = $"{(first ? Loc.T("wx.now") : format.Time(hour.Time))}, {format.Degrees(hour.Temperature)}, {WeatherText.Condition(hour.Condition, hour.IsDay)}" +
                   (hour.PrecipitationProbability is { } pp ? ", " + Loc.F("wx.precipChance", format.Percent(pp)) : "");
        var button = Clickable(new Border { Padding = new Thickness(2, 4, 2, 4), Child = stack }, () => ShowHour(hour), name, name);
        return button;
    }

    private static UIElement SunItem(HourMarker marker, WeatherFormatter format)
    {
        var stack = new StackPanel { Width = 70 };
        stack.Children.Add(new Border { Height = 14 });
        var time = Ui.Text(format.Time(marker.Time), 12.5, "TextSecondary");
        time.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(time);
        var glyph = Ui.Text(marker.Kind == HourMarkerKind.Sunrise ? "☀↑" : "☀↓", 20, "OrangeBrush");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.Margin = new Thickness(0, 8, 0, 6);
        stack.Children.Add(glyph);
        var label = Ui.Text(Loc.T("wx.marker." + marker.Kind), 10.5, "TextMuted");
        label.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(label);
        AutomationHelpers.Name(stack, $"{Loc.T("wx.marker." + marker.Kind)} {format.Time(marker.Time)}");
        return new Border { Padding = new Thickness(2, 4, 2, 4), Child = stack };
    }

    // ───────────────────────── 10 days ─────────────────────────

    private FrameworkElement BuildDaily(WeatherSnapshot s)
    {
        var format = Format;
        var now = PlaceNow;
        var days = s.Daily.Take(10).ToList();
        var range = WeatherText.TemperatureRange(days, s.Current.Temperature);
        var list = new StackPanel();
        foreach (var day in days)
        {
            var isToday = day.Date.Date == now.Date;
            var row = new Grid { Height = 48 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(Ui.Text(isToday ? Loc.T("wx.today") : format.Weekday(day.Date), 14.5, bold: true));
            names.Children.Add(Ui.Text(format.ShortDate(day.Date), 10.5, "TextMuted"));
            row.Children.Add(names);

            var iconStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            iconStack.Children.Add(WeatherIcons.Small(day.Condition, true, 30));
            if (day.PrecipitationProbability is { } p && p >= 20)
            {
                var chance = Ui.Text(format.Percent(p), 10, "AccentBrush", bold: true);
                chance.HorizontalAlignment = HorizontalAlignment.Center;
                iconStack.Children.Add(chance);
            }
            SetColumn(iconStack, 1);
            row.Children.Add(iconStack);

            var low = Ui.Text(format.Degrees(day.Low), 14.5, "TextMuted");
            low.VerticalAlignment = VerticalAlignment.Center;
            low.HorizontalAlignment = HorizontalAlignment.Right;
            low.Margin = new Thickness(0, 0, 8, 0);
            SetColumn(low, 2);
            row.Children.Add(low);

            var bar = new TemperatureBar(range, day.Low, day.High, isToday ? s.Current.Temperature : null) { Height = 6, VerticalAlignment = VerticalAlignment.Center };
            SetColumn(bar, 3);
            row.Children.Add(bar);

            var high = Ui.Text(format.Degrees(day.High), 14.5, bold: true);
            high.VerticalAlignment = VerticalAlignment.Center;
            high.HorizontalAlignment = HorizontalAlignment.Right;
            SetColumn(high, 4);
            row.Children.Add(high);

            var name = $"{(isToday ? Loc.T("wx.today") : format.Weekday(day.Date))} {format.ShortDate(day.Date)}: {WeatherText.Condition(day.Condition, true)}, " +
                       $"{Loc.T("wx.low")} {format.Degrees(day.Low)}, {Loc.T("wx.high")} {format.Degrees(day.High)}";
            var button = Clickable(new Border { Padding = new Thickness(6, 0, 6, 0), Child = row }, () => ShowDay(day), name);
            list.Children.Add(button);
            if (day != days[^1]) list.Children.Add(new Border { Height = 1, Opacity = 0.6, Background = TryFindResource("CardBorder") as Brush });
        }
        return Section(list, "▦", Loc.F("wx.daily.title", days.Count));
    }

    // ───────────────────────── map preview ─────────────────────────

    private FrameworkElement BuildMapPreview()
    {
        var map = new MapCanvas(false) { Height = 230, Radius = 8 };
        map.CenterOn(_location.Latitude, _location.Longitude, 6);
        foreach (var place in _settings.Locations.Where(p => p.IsCurrentLocation)) map.Markers.Add((place.Latitude, place.Longitude, true, place.Name));
        map.Markers.Add((_location.Latitude, _location.Longitude, false, _location.Name));
        var location = _location;
        _ = Task.Run(async () =>
        {
            var grid = await WeatherMapPanel.PreviewGridAsync(_service.Provider, location, CancellationToken.None);
            if (grid is not null) await Dispatcher.InvokeAsync(() => map.SetLayer(grid, 0));
        });
        var stack = new StackPanel();
        stack.Children.Add(map);
        var hint = Ui.Text(Loc.T("wx.map.open"), 11, "TextMuted");
        hint.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(hint);
        var card = Section(stack, "◎", Loc.T("wx.map.title"));
        return Clickable(card, () => OpenMap(WeatherMapLayer.Precipitation), Loc.T("wx.map.title"));
    }

    // ───────────────────────── detail cards ─────────────────────────

    private UniformGrid BuildDetails(WeatherSnapshot s)
    {
        var grid = new UniformGrid { Columns = 2 };
        var format = Format;
        var now = PlaceNow;
        var current = s.Current;
        var today = s.Daily.FirstOrDefault(d => d.Date.Date == now.Date) ?? s.Daily.FirstOrDefault();
        var todayHours = s.Hourly.Where(h => h.Time.Date == now.Date).ToList();
        var next24 = s.Hourly.Where(h => h.Time > now.AddHours(-1)).Take(24).ToList();

        void Add(string glyph, string title, string value, string? sub, string explanation, UIElement? extra, Action open)
        {
            var stack = new StackPanel();
            stack.Children.Add(TitleRow(glyph, title));
            var big = Ui.Text(value, 28, bold: false);
            big.FontWeight = FontWeights.SemiBold;
            stack.Children.Add(big);
            if (sub is not null) stack.Children.Add(Ui.Text(sub, 13.5, "TextPrimary", bold: true));
            if (extra is not null)
            {
                if (extra is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 8);
                stack.Children.Add(extra);
            }
            var line = Wrapped(explanation, 12);
            line.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(line);
            var card = Glass(stack, 14);
            card.Margin = new Thickness(0, 0, 7, 14);
            card.MinHeight = 168;
            _revealCards.Add(card);
            grid.Children.Add(Clickable(card, open, $"{title}: {value}{(sub is null ? "" : ", " + sub)}. {explanation}"));
        }

        if (s.AirQuality is { } air)
        {
            var level = WeatherText.AirQualityLevel(air.Index);
            var scale = ScaleBar.AirQuality();
            scale.Position = Math.Clamp(air.Index / 100, 0, 1);
            Add("❂", Loc.T("wx.aq.title"), air.Index.ToString("0", format.Culture), Loc.T("wx.aq.level" + level), Loc.T("wx.aq.advice" + level), scale, ShowAirQuality);
        }

        var uv = current.UvIndex ?? todayHours.FirstOrDefault(h => h.Time.Hour == now.Hour)?.UvIndex;
        if (uv is { } uvNow)
        {
            var scale = ScaleBar.Uv();
            scale.Position = Math.Clamp(uvNow / 11, 0, 1);
            var level = WeatherText.UvLevel(uvNow);
            Add("☀", Loc.T("wx.uv.title"), uvNow.ToString("0", format.Culture), Loc.T("wx.uv.level" + level), Loc.T("wx.uv.advice" + level), scale, ShowUv);
        }

        if (current.Wind is { } wind)
        {
            var compass = new WindCompass { Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Left, Cardinals = CardinalLetters(), CenterText = WeatherText.Compass(wind.Direction) };
            compass.TurnTo(wind.Direction);
            var gust = wind.Gusts is { } g ? Loc.F("wx.wind.gusts", format.Wind(g)) : null;
            Add("≋", Loc.T("wx.wind"), format.Wind(wind.Speed), gust, Loc.F("wx.wind.from", WeatherText.Compass(wind.Direction), Math.Round(wind.Direction)), compass, ShowWind);
        }

        if (today?.Sunrise is { } rise && today.Sunset is { } set)
        {
            _sunArc = new SunArc { Height = 70, Sunrise = rise, Sunset = set, Now = now };
            var tomorrow = s.Daily.Skip(1).FirstOrDefault();
            string value, sub;
            if (now < rise) { value = format.Time(rise); sub = Loc.T("wx.marker.Sunrise"); }
            else if (now < set) { value = format.Time(set); sub = Loc.T("wx.marker.Sunset"); }
            else { value = tomorrow?.Sunrise is { } next ? format.Time(next) : format.Time(rise); sub = Loc.T("wx.sun.tomorrowRise"); }
            var length = TimeSpan.FromSeconds(today.DaylightSeconds ?? (set - rise).TotalSeconds);
            Add("◒", Loc.T("wx.sun.title"), value, sub, Loc.F("wx.sun.daylight", format.Duration(length)), _sunArc, ShowSun);
        }

        if (today is not null && (today.PrecipitationSum is not null || next24.Any(h => h.PrecipitationProbability is not null)))
        {
            var soFar = todayHours.Where(h => h.Time <= now).Sum(h => h.Precipitation ?? 0);
            var maxChance = next24.Max(h => h.PrecipitationProbability ?? 0);
            Add("☂", Loc.T("wx.precip.title"), format.Precipitation(today.PrecipitationSum ?? 0), Loc.T("wx.precip.today"),
                Loc.F("wx.precip.explain", format.Precipitation(soFar), format.Percent(maxChance)), null, ShowPrecipitation);
        }

        if (current.ApparentTemperature is { } apparent)
        {
            var reason = WeatherText.FeelsLikeReason(current.Temperature, apparent, current.Wind?.Speed, current.Humidity);
            Add("♨", Loc.T("wx.feels.title"), format.Degrees(apparent), Loc.F("wx.feels.actual", format.Degrees(current.Temperature)), Loc.T("wx.feels.reason" + reason), null, ShowFeelsLike);
        }

        if (current.Humidity is { } humidity)
        {
            var sub = current.DewPoint is { } dew ? Loc.F("wx.humidity.dew", format.Degrees(dew)) : null;
            var explain = current.DewPoint is { } d ? Loc.T("wx.humidity.level" + WeatherText.HumidityLevel(d)) : "";
            Add("∴", Loc.T("wx.humidity.title"), format.Percent(humidity), sub, explain, null, ShowHumidity);
        }

        if (current.Visibility is { } visibility)
        {
            var level = WeatherText.VisibilityLevel(visibility);
            var cause = level == 0 ? "" : " " + Loc.T(current.Condition == WeatherCondition.Fog ? "wx.vis.causeFog"
                : WeatherText.IsSnowy(current.Condition) ? "wx.vis.causeSnow" : WeatherText.IsWet(current.Condition) ? "wx.vis.causeRain" : "wx.vis.causeHaze");
            Add("◉", Loc.T("wx.vis.title"), format.Distance(visibility), Loc.T("wx.vis.level" + level), Loc.T("wx.vis.explain" + level) + cause, null, ShowVisibility);
        }

        if (current.Pressure is { } pressure)
        {
            var trend = WeatherText.PressureTrend(s.Hourly, now);
            var spark = new LineChart { Height = 44, Fill = false, RevealOnShow = true };
            var around = s.Hourly.Where(h => h.Time > now.AddHours(-12) && h.Time < now.AddHours(12) && h.Pressure is not null).ToList();
            if (around.Count > 1)
            {
                spark.SetData(around.Select(h => h.Pressure!.Value).ToList());
                spark.MarkIndex = around.FindLastIndex(h => h.Time <= now);
            }
            Add("◷", Loc.T("wx.pressure.title"), format.Pressure(pressure), (trend > 0 ? "↗ " : trend < 0 ? "↘ " : "→ ") + Loc.T("wx.pressure.trend" + trend),
                Loc.T("wx.pressure.explain" + trend), around.Count > 1 ? spark : null, ShowPressure);
        }

        if (current.CloudCover is { } clouds)
        {
            var hour = todayHours.FirstOrDefault(h => h.Time.Hour == now.Hour);
            UIElement? layers = hour is { CloudLow: not null, CloudMid: not null, CloudHigh: not null } ? CloudLayers(hour) : null;
            Add("☁", Loc.T("wx.clouds.title"), format.Percent(clouds), null, Loc.T(clouds < 25 ? "wx.clouds.few" : clouds < 70 ? "wx.clouds.some" : "wx.clouds.overcast"), layers, ShowClouds);
        }

        var astro = Astronomy.ForDay(new DateTimeOffset(now.Date, now.Offset), WeatherTime.Zone(s.TimeZoneId), _location.Latitude, _location.Longitude);
        var moon = new MoonPhaseGlyph { Width = 52, Height = 52, Phase = astro.MoonPhase, HorizontalAlignment = HorizontalAlignment.Left };
        var phaseName = Loc.T("wx.moon.phase" + WeatherText.MoonPhaseIndex(astro.MoonPhase));
        Add("☾", Loc.T("wx.moon.title"), phaseName, Loc.F("wx.moon.lit", format.Percent(astro.MoonIllumination * 100)),
            astro.DaysToFullMoon < astro.DaysToNewMoon ? Loc.F("wx.moon.toFull", Math.Round(astro.DaysToFullMoon)) : Loc.F("wx.moon.toNew", Math.Round(astro.DaysToNewMoon)), moon, ShowMoon);

        if (s.Normals is { } normals && today is not null)
        {
            var diff = today.High - normals.AverageHigh;
            var value = (diff >= 0 ? "+" : "−") + format.Difference(diff);
            Add("≈", Loc.T("wx.avg.title"), value, Loc.F("wx.avg.normal", format.Degrees(normals.AverageHigh)), AverageSentence(diff, format), null, ShowAverages);
        }

        return grid;
    }

    private static string AverageSentence(double diff, WeatherFormatter format) =>
        Math.Abs(diff) < 1 ? Loc.T("wx.avg.same") : Loc.F(diff > 0 ? "wx.avg.warmer" : "wx.avg.colder", format.Difference(diff));

    private static string[] CardinalLetters()
    {
        var names = Loc.T("wx.compass").Split(' ');
        return names.Length == 16 ? new[] { names[0], names[4], names[8], names[12] } : new[] { "N", "E", "S", "W" };
    }

    private UIElement CloudLayers(HourlyForecast hour)
    {
        var stack = new StackPanel();
        foreach (var (key, value) in new[] { ("wx.clouds.high", hour.CloudHigh!.Value), ("wx.clouds.mid", hour.CloudMid!.Value), ("wx.clouds.low", hour.CloudLow!.Value) })
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(Ui.Text(Loc.T(key), 10.5, "TextMuted"));
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
            track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
            var fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            fill.SetResourceReference(Border.BackgroundProperty, "TextSecondary");
            var bars = new Grid();
            bars.Children.Add(track);
            bars.Children.Add(fill);
            bars.SizeChanged += (_, _) => fill.Width = bars.ActualWidth * value / 100;
            SetColumn(bars, 1);
            row.Children.Add(bars);
            stack.Children.Add(row);
        }
        return stack;
    }
}

/// <summary>Large temperature that counts smoothly to a new value instead of jumping.</summary>
internal sealed class TemperatureText : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(TemperatureText), new PropertyMetadata(0.0, (d, _) => ((TemperatureText)d).Render()));

    private readonly Func<double, string> _format;

    public TemperatureText(Func<double, string> format) => _format = format;

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public void Show(double value, double? from, bool animate)
    {
        if (animate && from is { } start && Math.Abs(start - value) >= 0.5)
            BeginAnimation(ValueProperty, new DoubleAnimation(start, value, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            BeginAnimation(ValueProperty, null);
            Value = value;
        }
        Render();
    }

    private void Render() => Text = _format(Value);
}

/// <summary>Bar for one day on the shared temperature scale, coloured from cool to warm, with today's dot.</summary>
internal sealed class TemperatureBar : FrameworkElement
{
    private readonly (double Min, double Max) _range;
    private readonly double _low, _high;
    private readonly double? _now;

    public TemperatureBar((double Min, double Max) range, double low, double high, double? now)
    {
        _range = range;
        _low = low;
        _high = high;
        _now = now;
        ToolTip = null;
    }

    public static Color ColorFor(double celsius) => celsius switch
    {
        < -5 => Color.FromRgb(0x7A, 0x6C, 0xF0),
        < 5 => Color.FromRgb(0x4C, 0xA6, 0xF5),
        < 12 => Color.FromRgb(0x4C, 0xC9, 0xC0),
        < 20 => Color.FromRgb(0x9C, 0xD2, 0x4A),
        < 27 => Color.FromRgb(0xF5, 0xC2, 0x3A),
        _ => Color.FromRgb(0xF2, 0x70, 0x34)
    };

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0) return;
        var track = TryFindResource("TrackBrush") as Brush ?? Brushes.LightGray;
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, width, height), height / 2, height / 2);
        var x1 = WeatherText.Scale(_low, _range) * width;
        var x2 = Math.Max(x1 + height, WeatherText.Scale(_high, _range) * width);
        var fill = new LinearGradientBrush(ColorFor(_low), ColorFor(_high), 0);
        fill.Freeze();
        dc.DrawRoundedRectangle(fill, null, new Rect(x1, 0, Math.Min(width, x2) - x1, height), height / 2, height / 2);
        if (_now is { } now)
        {
            var x = WeatherText.Scale(now, _range) * width;
            dc.DrawEllipse(Brushes.White, new Pen(TryFindResource("SurfaceBrush") as Brush ?? Brushes.Black, 1.5), new Point(x, height / 2), height * 0.8, height * 0.8);
        }
    }
}

/// <summary>Mouse wheel scrolls a horizontal strip sideways, gliding instead of jumping.</summary>
internal static class HorizontalWheel
{
    public static void Attach(ScrollViewer viewer)
    {
        double target = 0;
        var running = false;
        void Tick(object? sender, EventArgs e)
        {
            var current = viewer.HorizontalOffset;
            var next = current + (target - current) * 0.22;
            if (Math.Abs(target - next) < 0.5)
            {
                next = target;
                CompositionTarget.Rendering -= Tick;
                running = false;
            }
            viewer.ScrollToHorizontalOffset(next);
        }
        viewer.PreviewMouseWheel += (_, e) =>
        {
            if (viewer.ScrollableWidth <= 0) return;
            if (!running) target = viewer.HorizontalOffset;
            var next = Math.Clamp(target - e.Delta * 0.8, 0, viewer.ScrollableWidth);
            if (Math.Abs(next - target) < 0.5 && (next <= 0 || next >= viewer.ScrollableWidth)) return; // let the page scroll at the ends
            target = next;
            e.Handled = true;
            if (!running)
            {
                running = true;
                CompositionTarget.Rendering += Tick;
            }
        };
        viewer.Unloaded += (_, _) =>
        {
            if (running) CompositionTarget.Rendering -= Tick;
            running = false;
        };
    }
}
