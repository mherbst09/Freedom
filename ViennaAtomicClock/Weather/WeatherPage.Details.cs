using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ViennaAtomicClock.Weather;

internal sealed partial class WeatherPage
{
    private IInputElement? _returnFocus;
    private Action? _overlayRefresh;

    // ───────────────────────── overlay frame ─────────────────────────

    private void OpenOverlay(string title, UIElement content, double maxWidth = 660, Action? refresh = null)
    {
        var wasOpen = _overlayHost.Visibility == Visibility.Visible;
        _returnFocus ??= Keyboard.FocusedElement;
        _overlayRefresh = refresh;
        _overlayHost.Children.Clear();
        var dim = new Border { Background = WeatherIcons.Frozen(Color.FromArgb(110, 0, 0, 0)) };
        dim.MouseLeftButtonDown += (_, _) => CloseOverlay();
        _overlayHost.Children.Add(dim);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        var head = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        var heading = Ui.Text(title, 20, title: true);
        heading.VerticalAlignment = VerticalAlignment.Center;
        heading.Margin = new Thickness(0, 0, 50, 0);
        head.Children.Add(heading);
        var close = Ui.Button("IconButton", "×", (_, _) => CloseOverlay(), Loc.T("close"));
        close.FontSize = 22;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationHelpers.Name(close, Loc.T("close"));
        head.Children.Add(close);
        layout.Children.Add(head);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(0, 0, 6, 0) };
        if (content is WeatherMapPanel) scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);

        var sheet = new Border
        {
            BorderThickness = new Thickness(1), Padding = new Thickness(22, 18, 16, 18), Child = layout, MaxWidth = maxWidth,
            Margin = new Thickness(18), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
            MaxHeight = Math.Max(300, ActualHeight - 36)
        };
        if (content is WeatherMapPanel) sheet.Height = Math.Max(420, ActualHeight - 36);
        sheet.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        sheet.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        sheet.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        KeyboardNavigation.SetTabNavigation(sheet, KeyboardNavigationMode.Cycle);
        System.Windows.Automation.AutomationProperties.SetName(sheet, title);
        _overlayHost.Children.Add(sheet);
        _overlayHost.Visibility = Visibility.Visible;
        if (wasOpen) return; // refreshed content: no pop-in, focus stays where the user is
        if (ChartBase.AnimationsEnabled) Motion.PopIn(sheet);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => close.Focus());
    }

    private void CloseOverlay()
    {
        if (_overlayHost.Visibility != Visibility.Visible) return;
        _overlayHost.Visibility = Visibility.Collapsed;
        _overlayHost.Children.Clear();
        _overlayRefresh = null;
        if (_returnFocus is UIElement element && element.IsVisible) element.Focus();
        _returnFocus = null;
    }

    private static Grid InfoGrid(IEnumerable<(string Label, string? Value)> rows)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var r = 0;
        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrEmpty(value)) continue; // missing values are left out, never shown as 0
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = Ui.Text(label, 12.5, "TextSecondary");
            name.Margin = new Thickness(0, 5, 10, 5);
            name.TextWrapping = TextWrapping.Wrap;
            name.TextTrimming = TextTrimming.None;
            SetRow(name, r);
            grid.Children.Add(name);
            var text = Ui.Text(value, 13.5, bold: true);
            text.Margin = new Thickness(0, 5, 0, 5);
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.None;
            SetRow(text, r);
            SetColumn(text, 1);
            grid.Children.Add(text);
            r++;
        }
        return grid;
    }

    private static UIElement Heading(string text)
    {
        var block = Ui.Text(text.ToUpper(Loc.Culture), 11, "TextMuted", bold: true);
        block.Margin = new Thickness(0, 14, 0, 4);
        return block;
    }

    private LineChart HourChart(IReadOnlyList<HourlyForecast> hours, Func<HourlyForecast, double?> value, Func<double, string> label, string brush = "AccentBrush", bool fill = true)
    {
        var points = hours.Where(h => value(h) is not null).ToList();
        var chart = new LineChart { Height = 150, LineBrush = brush, Fill = fill, Margin = new Thickness(0, 6, 0, 6) };
        if (points.Count > 1)
        {
            var format = Format;
            var labels = points.Select((h, i) => i % 3 == 0 ? format.Hour(h.Time) : "").ToList();
            var values = points.Select(h => value(h)!.Value).ToList();
            var valueLabels = values.Select((v, i) => i % 3 == 0 ? label(v) : "").ToList();
            chart.SetData(values, labels, valueLabels);
        }
        return chart;
    }

    private List<HourlyForecast> Next24 => _snapshot?.Hourly.Where(h => h.Time > PlaceNow.AddHours(-1)).Take(24).ToList() ?? new();

    // ───────────────────────── hour, day, alert ─────────────────────────

    private void ShowHour(HourlyForecast h)
    {
        var format = Format;
        var stack = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(WeatherIcons.Small(h.Condition, h.IsDay, 56));
        var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Text(format.Degrees(h.Temperature), 30, bold: true));
        texts.Children.Add(Ui.Text(WeatherText.Condition(h.Condition, h.IsDay), 14, "TextSecondary"));
        top.Children.Add(texts);
        stack.Children.Add(top);
        stack.Children.Add(InfoGrid(new (string, string?)[]
        {
            (Loc.T("wx.feels.title"), h.ApparentTemperature is { } a ? format.Degrees(a) : null),
            (Loc.T("wx.precip.chance"), h.PrecipitationProbability is { } p ? format.Percent(p) : null),
            (Loc.T("wx.precip.amount"), h.Precipitation is { } pr ? format.Precipitation(pr) : null),
            (Loc.T("wx.precip.snow"), h.Snowfall is > 0 ? format.Snow(h.Snowfall.Value) : null),
            (Loc.T("wx.wind"), h.Wind is { } w ? $"{format.Wind(w.Speed)} {WeatherText.Compass(w.Direction)}" : null),
            (Loc.T("wx.wind.gustsLabel"), h.Wind?.Gusts is { } g ? format.Wind(g) : null),
            (Loc.T("wx.humidity.title"), h.Humidity is { } hu ? format.Percent(hu) : null),
            (Loc.T("wx.humidity.dewLabel"), h.DewPoint is { } d ? format.Degrees(d) : null),
            (Loc.T("wx.uv.title"), h.UvIndex is { } uv ? uv.ToString("0", format.Culture) : null),
            (Loc.T("wx.vis.title"), h.Visibility is { } v ? format.Distance(v) : null),
            (Loc.T("wx.pressure.title"), h.Pressure is { } pa ? format.Pressure(pa) : null),
            (Loc.T("wx.clouds.title"), h.CloudCover is { } c ? format.Percent(c) : null)
        }));
        OpenOverlay($"{format.Weekday(h.Time)} {format.Time(h.Time)}", stack, 520);
    }

    private void ShowDay(DailyForecast day)
    {
        if (_snapshot is null) return;
        var format = Format;
        var s = _snapshot;
        var stack = new StackPanel();
        stack.Children.Add(Wrapped(WeatherText.DaySummary(day, format), 14, "TextPrimary"));

        var hours = s.Hourly.Where(h => h.Time >= day.Date && h.Time < day.Date.AddDays(1)).ToList();
        var dayHours = hours.Where(h => h.Time.Hour is >= 6 and < 18).ToList();
        var nightHours = s.Hourly.Where(h => h.Time >= day.Date.AddHours(18) && h.Time < day.Date.AddHours(30)).ToList();
        var halves = new UniformGrid2(2);
        if (dayHours.Count > 0) halves.Add(HalfDay(Loc.T("wx.day.day"), WeatherText.Dominant(dayHours), true, dayHours.Max(h => h.Temperature), format));
        if (nightHours.Count > 0) halves.Add(HalfDay(Loc.T("wx.day.night"), WeatherText.Dominant(nightHours), false, nightHours.Min(h => h.Temperature), format));
        halves.Panel.Margin = new Thickness(0, 12, 0, 4);
        stack.Children.Add(halves.Panel);

        if (hours.Count > 0)
        {
            stack.Children.Add(Heading(Loc.T("wx.hourly.title")));
            var strip = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var hour in hours) strip.Children.Add(HourItem(hour, false, null, format));
            var scroller = new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.HorizontalOnly, Focusable = false };
            HorizontalWheel.Attach(scroller);
            stack.Children.Add(scroller);
            stack.Children.Add(Heading(Loc.T("wx.day.temperature")));
            stack.Children.Add(HourChart(hours, h => WeatherUnits.Temperature(h.Temperature, _settings.Temperature), v => WeatherUnits.RoundDegrees(v) + "°", "OrangeBrush"));
        }

        var astro = Astronomy.ForDay(day.Date, WeatherTime.Zone(s.TimeZoneId), _location.Latitude, _location.Longitude);
        stack.Children.Add(InfoGrid(new (string, string?)[]
        {
            (Loc.T("wx.high"), format.Degrees(day.High)),
            (Loc.T("wx.low"), format.Degrees(day.Low)),
            (Loc.T("wx.precip.chance"), day.PrecipitationProbability is { } p ? format.Percent(p) : null),
            (Loc.T("wx.precip.amount"), day.PrecipitationSum is { } ps ? format.Precipitation(ps) : null),
            (Loc.T("wx.precip.rain"), day.RainSum is > 0 ? format.Precipitation(day.RainSum.Value + (day.ShowersSum ?? 0)) : null),
            (Loc.T("wx.precip.snow"), day.SnowfallSum is > 0 ? format.Snow(day.SnowfallSum.Value) : null),
            (Loc.T("wx.wind"), day.WindMax is { } w ? format.Wind(w) + (day.WindDirection is { } dir ? " " + WeatherText.Compass(dir) : "") : null),
            (Loc.T("wx.wind.gustsLabel"), day.GustMax is { } g ? format.Wind(g) : null),
            (Loc.T("wx.uv.max"), day.UvMax is { } uv ? $"{uv.ToString("0", format.Culture)} · {Loc.T("wx.uv.level" + WeatherText.UvLevel(uv))}" : null),
            (Loc.T("wx.marker.Sunrise"), day.Sunrise is { } rise ? format.Time(rise) : null),
            (Loc.T("wx.marker.Sunset"), day.Sunset is { } set ? format.Time(set) : null),
            (Loc.T("wx.sun.length"), day.DaylightSeconds is { } dl ? format.Duration(TimeSpan.FromSeconds(dl)) : null),
            (Loc.T("wx.moon.title"), $"{Loc.T("wx.moon.phase" + WeatherText.MoonPhaseIndex(astro.MoonPhase))} · {format.Percent(astro.MoonIllumination * 100)}")
        }));
        var title = (day.Date.Date == PlaceNow.Date ? Loc.T("wx.today") : Format.Culture.DateTimeFormat.GetDayName(day.Date.DayOfWeek)) + ", " + format.ShortDate(day.Date);
        OpenOverlay(title, stack, 720);
    }

    private static UIElement HalfDay(string label, WeatherCondition condition, bool isDay, double temperature, WeatherFormatter format)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
        row.Children.Add(WeatherIcons.Small(condition, isDay, 42));
        var texts = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Text(label, 11.5, "TextMuted", bold: true));
        texts.Children.Add(Ui.Text($"{WeatherText.Condition(condition, isDay)} · {format.Degrees(temperature)}", 13.5));
        row.Children.Add(texts);
        return row;
    }

    private void ShowAlert(WeatherAlert alert)
    {
        var format = Format;
        var stack = new StackPanel();
        var band = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = WeatherIcons.Frozen(SeverityColor(alert.Severity)), Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(band);
        stack.Children.Add(Ui.Text($"{AlertGlyph(alert.Kind)}  {Loc.T("wx.alert.severity." + alert.Severity)} · {Loc.T("wx.alert.kind." + alert.Kind)}", 13, "TextSecondary", bold: true));
        var title = Wrapped(alert.Title, 17, "TextPrimary");
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(0, 6, 0, 4);
        stack.Children.Add(title);
        var local = (DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, TimeZoneInfo.Local);
        stack.Children.Add(InfoGrid(new (string, string?)[]
        {
            (Loc.T("wx.alert.valid"), $"{format.Weekday(local(alert.Start))} {format.ShortDate(local(alert.Start))} {format.Time(local(alert.Start))} – {format.Weekday(local(alert.End))} {format.ShortDate(local(alert.End))} {format.Time(local(alert.End))}"),
            (Loc.T("wx.alert.region"), alert.Region),
            (Loc.T("wx.alert.issuer"), alert.Issuer),
            (Loc.T("wx.alert.source"), alert.Source)
        }));
        if (alert.Summary.Length > 0) { stack.Children.Add(Heading(Loc.T("wx.alert.impacts"))); stack.Children.Add(Wrapped(alert.Summary, 13, "TextPrimary")); }
        if (alert.Description.Length > 0) { stack.Children.Add(Heading(Loc.T("wx.alert.situation"))); stack.Children.Add(Wrapped(alert.Description, 13, "TextPrimary")); }
        if (alert.Instructions.Length > 0) { stack.Children.Add(Heading(Loc.T("wx.alert.advice"))); stack.Children.Add(Wrapped(alert.Instructions, 13, "TextPrimary")); }
        OpenOverlay(Loc.T("wx.alert.title"), stack, 680);
    }

    // ───────────────────────── detail cards ─────────────────────────

    private void ShowAirQuality()
    {
        if (_snapshot?.AirQuality is not { } air) return;
        var format = Format;
        var level = WeatherText.AirQualityLevel(air.Index);
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text($"{air.Index.ToString("0", format.Culture)} · {Loc.T("wx.aq.level" + level)}", 26, bold: true));
        var scale = ScaleBar.AirQuality();
        scale.Position = Math.Clamp(air.Index / 100, 0, 1);
        scale.Margin = new Thickness(0, 8, 0, 8);
        stack.Children.Add(scale);
        stack.Children.Add(Wrapped(Loc.T("wx.aq.advice" + level), 13.5, "TextPrimary"));
        string Ug(double? v) => v is { } x ? x.ToString("0", format.Culture) + " µg/m³" : "";
        stack.Children.Add(InfoGrid(new (string, string?)[]
        {
            (Loc.T("wx.aq.main"), WeatherText.MainPollutant(air)),
            ("PM2.5", Ug(air.Pm25)), ("PM10", Ug(air.Pm10)), (Loc.T("wx.aq.ozone"), Ug(air.Ozone)), (Loc.T("wx.aq.no2"), Ug(air.NitrogenDioxide))
        }));
        if (air.HourlyIndex.Count > 1)
        {
            stack.Children.Add(Heading(Loc.T("wx.aq.next24")));
            var chart = new LineChart { Height = 120 };
            var start = PlaceNow;
            chart.SetData(air.HourlyIndex, air.HourlyIndex.Select((_, i) => i % 3 == 0 ? format.Hour(start.AddHours(i)) : "").ToList());
            stack.Children.Add(chart);
        }
        stack.Children.Add(Wrapped(Loc.T("wx.aq.source"), 11, "TextMuted"));
        OpenOverlay(Loc.T("wx.aq.title"), stack);
    }

    private void ShowUv()
    {
        if (_snapshot is null) return;
        var format = Format;
        var today = _snapshot.Hourly.Where(h => h.Time.Date == PlaceNow.Date && h.UvIndex is not null).ToList();
        var stack = new StackPanel();
        var current = _snapshot.Current.UvIndex ?? 0;
        stack.Children.Add(Ui.Text($"{current.ToString("0", format.Culture)} · {Loc.T("wx.uv.level" + WeatherText.UvLevel(current))}", 26, bold: true));
        var scale = ScaleBar.Uv();
        scale.Position = Math.Clamp(current / 11, 0, 1);
        scale.Margin = new Thickness(0, 8, 0, 8);
        stack.Children.Add(scale);
        var peak = today.MaxBy(h => h.UvIndex);
        stack.Children.Add(InfoGrid(new (string, string?)[]
        {
            (Loc.T("wx.uv.max"), peak?.UvIndex is { } max ? $"{max.ToString("0", format.Culture)} · {Loc.T("wx.uv.level" + WeatherText.UvLevel(max))}" : null),
            (Loc.T("wx.uv.peakTime"), peak is not null ? format.Time(peak.Time) : null)
        }));
        stack.Children.Add(Wrapped(Loc.T("wx.uv.advice" + WeatherText.UvLevel(Math.Max(current, peak?.UvIndex ?? 0))), 13.5, "TextPrimary"));
        stack.Children.Add(Heading(Loc.T("wx.uv.course")));
        var chart = HourChart(today, h => h.UvIndex, v => v.ToString("0", format.Culture), "OrangeBrush");
        chart.Minimum = 0;
        stack.Children.Add(chart);
        OpenOverlay(Loc.T("wx.uv.title"), stack);
    }

    private void ShowWind()
    {
        if (_snapshot?.Current.Wind is not { } wind) return;
        var format = Format;
        var stack = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var compass = new WindCompass { Width = 150, Height = 150, Cardinals = CardinalLetters(), CenterText = WeatherText.Compass(wind.Direction) };
        compass.TurnTo(wind.Direction);
        row.Children.Add(compass);
        var info = InfoGrid(new (string, string?)[]
        {
            (Loc.T("wx.wind.speed"), format.Wind(wind.Speed)),
            (Loc.T("wx.wind.gustsLabel"), wind.Gusts is { } g ? format.Wind(g) : null),
            (Loc.T("wx.wind.direction"), $"{WeatherText.Compass(wind.Direction)} ({Math.Round(wind.Direction)}°)")
        });
        info.Margin = new Thickness(18, 0, 0, 0);
        info.MinWidth = 240;
        row.Children.Add(info);
        stack.Children.Add(row);
        stack.Children.Add(Heading(Loc.T("wx.wind.course")));
        stack.Children.Add(HourChart(Next24, h => h.Wind is { } w ? WeatherUnits.Wind(w.Speed, _settings.Wind) : null, v => v.ToString("0", format.Culture)));
        stack.Children.Add(Ui.Text(WeatherUnits.WindSymbol(_settings.Wind), 11, "TextMuted"));
        OpenOverlay(Loc.T("wx.wind"), stack);
    }

    private void ShowSun()
    {
        if (_snapshot is null) return;
        var format = Format;
        var now = PlaceNow;
        var today = _snapshot.Daily.FirstOrDefault(d => d.Date.Date == now.Date) ?? _snapshot.Daily.FirstOrDefault();
        if (today is null) return;
        var tomorrow = _snapshot.Daily.FirstOrDefault(d => d.Date.Date == now.Date.AddDays(1));
        var astro = Astronomy.ForDay(today.Date, WeatherTime.Zone(_snapshot.TimeZoneId), _location.Latitude, _location.Longitude);
        var stack = new StackPanel();
        stack.Children.Add(new SunArc { Height = 130, Sunrise = today.Sunrise, Sunset = today.Sunset, Now = now, Margin = new Thickness(0, 6, 0, 10) });
        string? remaining = null;
        if (today.Sunrise is { } rise && now < rise) remaining = Loc.F("wx.sun.untilRise", format.Duration(rise - now));
        else if (today.Sunset is { } set && now < set) remaining = Loc.F("wx.sun.untilSet", format.Duration(set - now));
        else if (tomorrow?.Sunrise is { } next) remaining = Loc.F("wx.sun.untilRise", format.Duration(next - now));
        if (remaining is not null) stack.Children.Add(Wrapped(remaining, 14, "TextPrimary"));
        stack.Children.Add(InfoGrid(new (string, string?)[]
        {
            (Loc.T("wx.sun.dawn"), astro.CivilDawn is { } dawn ? format.Time(dawn) : null),
            (Loc.T("wx.marker.Sunrise"), today.Sunrise is { } r ? format.Time(r) : null),
            (Loc.T("wx.marker.Sunset"), today.Sunset is { } s ? format.Time(s) : null),
            (Loc.T("wx.sun.dusk"), astro.CivilDusk is { } dusk ? format.Time(dusk) : null),
            (Loc.T("wx.sun.length"), today.DaylightSeconds is { } dl ? format.Duration(TimeSpan.FromSeconds(dl)) : null),
            (Loc.T("wx.sun.tomorrowRise"), tomorrow?.Sunrise is { } tr ? format.Time(tr) : null),
            (Loc.T("wx.sun.tomorrowSet"), tomorrow?.Sunset is { } ts ? format.Time(ts) : null)
        }));
        stack.Children.Add(Wrapped(Loc.T("wx.sun.dawnHelp"), 11, "TextMuted"));
        OpenOverlay(Loc.T("wx.sun.title"), stack);
    }

    private void ShowPrecipitation()
    {
        if (_snapshot is null) return;
        var format = Format;
        var now = PlaceNow;
        var today = _snapshot.Daily.FirstOrDefault(d => d.Date.Date == now.Date);
        var todayHours = _snapshot.Hourly.Where(h => h.Time.Date == now.Date).ToList();
        var stack = new StackPanel();
        stack.Children.Add(InfoGrid(new (string, string?)[]
        {
            (Loc.T("wx.precip.chance"), today?.PrecipitationProbability is { } p ? format.Percent(p) : null),
            (Loc.T("wx.precip.soFar"), format.Precipitation(todayHours.Where(h => h.Time <= now).Sum(h => h.Precipitation ?? 0))),
            (Loc.T("wx.precip.expected"), today?.PrecipitationSum is { } sum ? format.Precipitation(sum) : null),
            (Loc.T("wx.precip.rain"), today?.RainSum is { } rain ? format.Precipitation(rain) : null),
            (Loc.T("wx.precip.showers"), today?.ShowersSum is { } showers ? format.Precipitation(showers) : null),
            (Loc.T("wx.precip.snow"), today?.SnowfallSum is { } snow ? format.Snow(snow) : null)
        }));
        var next = Next24;
        stack.Children.Add(Heading(Loc.T("wx.precip.next24")));
        var bars = new BarChart { Height = 120, FullScale = Math.Max(2, next.Max(h => h.Precipitation ?? 0)) };
        bars.SetData(next.Select(h => h.Precipitation ?? 0).ToList(), next.Select((h, i) => i % 3 == 0 ? format.Hour(h.Time) : "").ToList());
        stack.Children.Add(bars);
        stack.Children.Add(Heading(Loc.T("wx.precip.chance")));
        var chance = HourChart(next, h => h.PrecipitationProbability, v => format.Percent(v));
        chance.Minimum = 0;
        chance.Maximum = 100;
        stack.Children.Add(chance);
        stack.Children.Add(Wrapped(Loc.T("wx.precip.graupel"), 11, "TextMuted"));
        OpenOverlay(Loc.T("wx.precip.title"), stack);
    }

    private void ShowFeelsLike()
    {
        if (_snapshot?.Current is not { ApparentTemperature: { } apparent } current) return;
        var format = Format;
        var stack = new StackPanel();
        stack.Children.Add(Wrapped(Loc.T("wx.feels.reason" + WeatherText.FeelsLikeReason(current.Temperature, apparent, current.Wind?.Speed, current.Humidity)), 14, "TextPrimary"));
        stack.Children.Add(InfoGrid(new (string, string?)[]
        {
            (Loc.T("wx.feels.title"), format.Degrees(apparent)),
            (Loc.T("wx.feels.measured"), format.Degrees(current.Temperature)),
            (Loc.T("wx.wind"), current.Wind is { } w ? format.Wind(w.Speed) : null),
            (Loc.T("wx.humidity.title"), current.Humidity is { } h ? format.Percent(h) : null)
        }));
        stack.Children.Add(Heading(Loc.T("wx.feels.course")));
        stack.Children.Add(HourChart(Next24, h => h.ApparentTemperature is { } a ? WeatherUnits.Temperature(a, _settings.Temperature) : null, v => WeatherUnits.RoundDegrees(v) + "°", "OrangeBrush"));
        OpenOverlay(Loc.T("wx.feels.title"), stack);
    }

    private void ShowHumidity()
    {
        if (_snapshot?.Current is not { Humidity: { } humidity } current) return;
        var format = Format;
        var stack = new StackPanel();
        if (current.DewPoint is { } dew) stack.Children.Add(Wrapped(Loc.T("wx.humidity.level" + WeatherText.HumidityLevel(dew)), 14, "TextPrimary"));
        stack.Children.Add(InfoGrid(new (string, string?)[]
        {
            (Loc.T("wx.humidity.title"), format.Percent(humidity)),
            (Loc.T("wx.humidity.dewLabel"), current.DewPoint is { } d ? format.Degrees(d) : null)
        }));
        stack.Children.Add(Wrapped(Loc.T("wx.humidity.help"), 11.5, "TextMuted"));
        stack.Children.Add(Heading(Loc.T("wx.humidity.course")));
        var chart = HourChart(Next24, h => h.Humidity, v => format.Percent(v));
        chart.Minimum = 0;
        chart.Maximum = 100;
        stack.Children.Add(chart);
        OpenOverlay(Loc.T("wx.humidity.title"), stack);
    }

    private void ShowVisibility()
    {
        if (_snapshot?.Current is not { Visibility: { } visibility }) return;
        var format = Format;
        var level = WeatherText.VisibilityLevel(visibility);
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text($"{format.Distance(visibility)} · {Loc.T("wx.vis.level" + level)}", 24, bold: true));
        stack.Children.Add(Wrapped(Loc.T("wx.vis.explain" + level), 14, "TextPrimary"));
        stack.Children.Add(Heading(Loc.T("wx.vis.course")));
        stack.Children.Add(HourChart(Next24, h => h.Visibility is { } v ? WeatherUnits.Distance(v, _settings.Distance) : null, v => v.ToString("0", format.Culture)));
        stack.Children.Add(Ui.Text(WeatherUnits.DistanceSymbol(_settings.Distance), 11, "TextMuted"));
        OpenOverlay(Loc.T("wx.vis.title"), stack);
    }

    private void ShowPressure()
    {
        if (_snapshot?.Current is not { Pressure: { } pressure }) return;
        var format = Format;
        var now = PlaceNow;
        var trend = WeatherText.PressureTrend(_snapshot.Hourly, now);
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(format.Pressure(pressure), 26, bold: true));
        stack.Children.Add(Wrapped((trend > 0 ? "↗ " : trend < 0 ? "↘ " : "→ ") + Loc.T("wx.pressure.explain" + trend), 14, "TextPrimary"));
        var around = _snapshot.Hourly.Where(h => h.Time > now.AddHours(-12) && h.Time < now.AddHours(13)).ToList();
        stack.Children.Add(Heading(Loc.T("wx.pressure.course")));
        var chart = HourChart(around, h => h.Pressure is { } p ? WeatherUnits.Pressure(p, _settings.Pressure) : null,
            v => v.ToString(_settings.Pressure == PressureUnit.InchesOfMercury ? "0.00" : "0", format.Culture), fill: false);
        chart.MarkIndex = around.Where(h => h.Pressure is not null).ToList().FindLastIndex(h => h.Time <= now);
        stack.Children.Add(chart);
        OpenOverlay(Loc.T("wx.pressure.title"), stack);
    }

    private void ShowClouds()
    {
        if (_snapshot?.Current is not { CloudCover: { } clouds }) return;
        var format = Format;
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(format.Percent(clouds), 26, bold: true));
        var hour = _snapshot.Hourly.LastOrDefault(h => h.Time <= PlaceNow);
        if (hour is { CloudLow: not null, CloudMid: not null, CloudHigh: not null })
        {
            stack.Children.Add(Heading(Loc.T("wx.clouds.layers")));
            stack.Children.Add(CloudLayers(hour));
        }
        stack.Children.Add(Heading(Loc.T("wx.clouds.course")));
        var chart = HourChart(Next24, h => h.CloudCover, v => format.Percent(v), "TextSecondary");
        chart.Minimum = 0;
        chart.Maximum = 100;
        stack.Children.Add(chart);
        OpenOverlay(Loc.T("wx.clouds.title"), stack);
    }

    private void ShowMoon()
    {
        if (_snapshot is null) return;
        var format = Format;
        var now = PlaceNow;
        var zone = WeatherTime.Zone(_snapshot.TimeZoneId);
        var astro = Astronomy.ForDay(new DateTimeOffset(now.Date, now.Offset), zone, _location.Latitude, _location.Longitude);
        var stack = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new MoonPhaseGlyph { Width = 110, Height = 110, Phase = astro.MoonPhase });
        var texts = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Text(Loc.T("wx.moon.phase" + WeatherText.MoonPhaseIndex(astro.MoonPhase)), 22, bold: true));
        texts.Children.Add(Ui.Text(Loc.F("wx.moon.lit", format.Percent(astro.MoonIllumination * 100)), 14, "TextSecondary"));
        top.Children.Add(texts);
        stack.Children.Add(top);
        stack.Children.Add(InfoGrid(new (string, string?)[]
        {
            (Loc.T("wx.moon.rise"), astro.Moonrise is { } rise ? format.Time(rise) : Loc.T("wx.moon.none")),
            (Loc.T("wx.moon.set"), astro.Moonset is { } set ? format.Time(set) : Loc.T("wx.moon.none")),
            (Loc.T("wx.moon.nextFull"), Loc.F("wx.moon.inDays", Math.Round(astro.DaysToFullMoon))),
            (Loc.T("wx.moon.nextNew"), Loc.F("wx.moon.inDays", Math.Round(astro.DaysToNewMoon)))
        }));
        stack.Children.Add(Heading(Loc.T("wx.moon.week")));
        var week = new WrapPanel();
        for (var i = 0; i < 8; i++)
        {
            var date = now.Date.AddDays(i);
            var phase = Astronomy.MoonIllumination(date.AddHours(12).ToUniversalTime()).Phase;
            var day = new StackPanel { Width = 56, Margin = new Thickness(0, 0, 6, 6) };
            day.Children.Add(new MoonPhaseGlyph { Width = 34, Height = 34, Phase = phase, HorizontalAlignment = HorizontalAlignment.Center });
            var label = Ui.Text(i == 0 ? Loc.T("wx.today") : format.Culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek), 10.5, "TextMuted");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            day.Children.Add(label);
            week.Children.Add(day);
        }
        stack.Children.Add(week);
        OpenOverlay(Loc.T("wx.moon.title"), stack);
    }

    private void ShowAverages()
    {
        if (_snapshot?.Normals is not { } normals) return;
        var format = Format;
        var today = _snapshot.Daily.FirstOrDefault();
        if (today is null) return;
        var stack = new StackPanel();
        stack.Children.Add(Wrapped(AverageSentence(today.High - normals.AverageHigh, format), 15, "TextPrimary"));
        var rainDiff = (today.PrecipitationSum ?? 0) - normals.AveragePrecipitation;
        stack.Children.Add(InfoGrid(new (string, string?)[]
        {
            (Loc.T("wx.avg.todayHigh"), format.Degrees(today.High)),
            (Loc.T("wx.avg.normalHigh"), format.Degrees(normals.AverageHigh)),
            (Loc.T("wx.avg.todayLow"), format.Degrees(today.Low)),
            (Loc.T("wx.avg.normalLow"), format.Degrees(normals.AverageLow)),
            (Loc.T("wx.avg.todayRain"), today.PrecipitationSum is { } p ? format.Precipitation(p) : null),
            (Loc.T("wx.avg.normalRain"), format.Precipitation(normals.AveragePrecipitation)),
            (Loc.T("wx.avg.rainDiff"), (rainDiff >= 0 ? "+" : "−") + format.Precipitation(Math.Abs(rainDiff)))
        }));
        stack.Children.Add(Wrapped(Loc.F("wx.avg.basis", normals.Years), 11, "TextMuted"));
        OpenOverlay(Loc.T("wx.avg.title"), stack);
    }

    // ───────────────────────── map ─────────────────────────

    private void OpenMap(WeatherMapLayer layer)
    {
        var panel = new WeatherMapPanel(_service.Provider, _location, WeatherStore.Ordered(_settings.Locations), layer, () => Format);
        OpenOverlay(Loc.T("wx.map.title"), panel, 1100);
    }

    // ───────────────────────── places ─────────────────────────

    private CancellationTokenSource? _placesRefresh;

    private void OpenLocations()
    {
        var stack = new StackPanel();
        var locate = Ui.Button("ActionButton", "⌖  " + Loc.T("wx.useCurrent"), async (_, _) =>
        {
            _settings.AutoLocation = true;
            WeatherStore.Save(_settings);
            if (!await LocateAsync(selectIt: true)) ShowLocationsMessage(Loc.T("wx.err.location"));
        });
        locate.HorizontalAlignment = HorizontalAlignment.Left;
        locate.Padding = new Thickness(16, 0, 16, 0);
        locate.Margin = new Thickness(0, 0, 0, 6);
        stack.Children.Add(locate);
        stack.Children.Add(Wrapped(Loc.T("wx.places.searchHint"), 11.5, "TextMuted"));
        _locationsMessage = Wrapped("", 12, "OrangeBrush");
        stack.Children.Add(_locationsMessage);

        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var place in WeatherStore.Ordered(_settings.Locations)) list.Children.Add(PlaceCard(place));
        stack.Children.Add(list);
        OpenOverlay(Loc.T("wx.places"), stack, 640, OpenLocations);
        _ = RefreshPlacesAsync();
    }

    private TextBlock? _locationsMessage;

    private void ShowLocationsMessage(string text)
    {
        if (_locationsMessage is not null) _locationsMessage.Text = text;
    }

    /// <summary>Loads missing previews one after another while the place list is open.</summary>
    private async Task RefreshPlacesAsync()
    {
        _placesRefresh?.Cancel();
        _placesRefresh = new CancellationTokenSource();
        var token = _placesRefresh.Token;
        var changed = false;
        foreach (var place in _settings.Locations.ToList())
        {
            if (token.IsCancellationRequested || _overlayRefresh is null) return;
            var cached = _service.Cached(place.Id);
            if (cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(30)) continue;
            try
            {
                var result = await _service.RefreshAsync(place, false, token);
                changed |= result.Snapshot is not null && !result.FromCache;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (changed && _overlayRefresh == (Action)OpenLocations && !token.IsCancellationRequested) OpenLocations();
    }

    private UIElement PlaceCard(WeatherLocation place)
    {
        var format = Format;
        var snapshot = place.Id == _location.Id ? _snapshot : _service.Cached(place.Id);
        var grid = new Grid { MinHeight = 96 };
        var scene = snapshot is null ? WeatherScene.CloudyDay : WeatherBackdrop.SceneFor(snapshot.Current.Condition, snapshot.Current.IsDay);
        var background = new Border { Background = WeatherBackdrop.SkyBrush(scene), CornerRadius = new CornerRadius(10) };
        grid.Children.Add(background);

        var content = new Grid { Margin = new Thickness(14, 10, 14, 10) };
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        var badges = new List<string>();
        if (place.IsCurrentLocation) badges.Add("⌖ " + Loc.T("wx.currentLocation"));
        if (place.IsFavorite) badges.Add("★ " + Loc.T("wx.favorite"));
        if (_settings.DefaultLocationId == place.Id) badges.Add(Loc.T("wx.defaultPlace"));
        if (badges.Count > 0) left.Children.Add(new TextBlock { Text = string.Join("  ·  ", badges), FontSize = 11, Foreground = HeaderSoft });
        left.Children.Add(new TextBlock { Text = place.Name, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = HeaderText, TextTrimming = TextTrimming.CharacterEllipsis });
        var zoneId = snapshot?.TimeZoneId ?? place.TimeZoneId;
        left.Children.Add(new TextBlock { Text = format.Time(WeatherTime.ToPlace(DateTimeOffset.UtcNow, zoneId)) + (place.Subtitle.Length > 0 ? "  ·  " + place.Subtitle : ""), FontSize = 12, Foreground = HeaderSoft, TextTrimming = TextTrimming.CharacterEllipsis });
        if (snapshot is not null)
        {
            left.Children.Add(new TextBlock { Text = WeatherText.Condition(snapshot.Current.Condition, snapshot.Current.IsDay), FontSize = 13, Foreground = HeaderText, Margin = new Thickness(0, 6, 0, 0) });
            if (snapshot.Alerts.Any(a => a.End > DateTimeOffset.UtcNow))
                left.Children.Add(new TextBlock { Text = "⚠ " + Loc.T("wx.alert.active"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = WeatherIcons.Frozen(Color.FromRgb(0xFF, 0xD5, 0x4F)) });
        }
        content.Children.Add(left);
        if (snapshot is not null)
        {
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(new TextBlock { Text = format.Degrees(snapshot.Current.Temperature), FontSize = 36, FontWeight = FontWeights.Light, Foreground = HeaderText, HorizontalAlignment = HorizontalAlignment.Right });
            if (snapshot.Daily.FirstOrDefault() is { } day)
                right.Children.Add(new TextBlock { Text = $"{Loc.T("wx.high")} {format.Degrees(day.High)}  {Loc.T("wx.low")} {format.Degrees(day.Low)}", FontSize = 12, Foreground = HeaderSoft });
            SetColumn(right, 1);
            content.Children.Add(right);
        }
        grid.Children.Add(content);

        var name = $"{place.Name}" + (snapshot is null ? "" : $", {format.Degrees(snapshot.Current.Temperature)}, {WeatherText.Condition(snapshot.Current.Condition, snapshot.Current.IsDay)}");
        var card = Clickable(grid, async () => await SelectLocationAsync(place), name);
        card.Margin = new Thickness(0, 0, 0, 4);

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        Button Action(string text, string tip, Action click)
        {
            var b = Ui.Button("ChipButton", text, (_, _) => click(), tip);
            b.Height = 30;
            b.Padding = new Thickness(10, 0, 10, 0);
            b.Margin = new Thickness(0, 0, 6, 0);
            AutomationHelpers.Name(b, $"{tip}: {place.Name}");
            return b;
        }
        var favorite = Action(place.IsFavorite ? "★" : "☆", Loc.T("wx.favorite"), () => { place.IsFavorite = !place.IsFavorite; SavePlaces(); });
        Ui.Select(favorite, place.IsFavorite);
        actions.Children.Add(favorite);
        actions.Children.Add(Action("▲", Loc.T("wx.moveUp"), () => { WeatherStore.Move(_settings.Locations, place.Id, -1); SavePlaces(); }));
        actions.Children.Add(Action("▼", Loc.T("wx.moveDown"), () => { WeatherStore.Move(_settings.Locations, place.Id, 1); SavePlaces(); }));
        var isDefault = _settings.DefaultLocationId == place.Id;
        var makeDefault = Action(Loc.T("wx.setDefault"), Loc.T("wx.setDefault"), () => { _settings.DefaultLocationId = isDefault ? null : place.Id; SavePlaces(); });
        Ui.Select(makeDefault, isDefault);
        actions.Children.Add(makeDefault);
        actions.Children.Add(Action("✕", Loc.T("wx.remove"), () =>
        {
            if (place.IsCurrentLocation) _settings.AutoLocation = false;
            WeatherStore.Remove(_settings, place.Id);
            _service.Forget(place.Id);
            SavePlaces();
            if (place.Id == _location.Id) _ = SelectLocationAsync(WeatherStore.Selected(_settings));
        }));

        var host = new StackPanel();
        host.Children.Add(card);
        host.Children.Add(actions);
        return host;
    }

    private void SavePlaces()
    {
        WeatherStore.Save(_settings);
        _overlayRefresh?.Invoke();
    }

    // ───────────────────────── settings ─────────────────────────

    private void OpenSettings()
    {
        var stack = new StackPanel();
        void Group<T>(string title, (T Value, string Text)[] options, T current, Action<T> set)
        {
            stack.Children.Add(Heading(title));
            var row = new WrapPanel();
            foreach (var (value, text) in options)
            {
                var chip = Ui.Button("ChipButton", text, (_, _) => { set(value); AfterSettingsChange(); });
                chip.Height = 34;
                chip.Margin = new Thickness(0, 0, 6, 6);
                chip.Padding = new Thickness(14, 0, 14, 0);
                Ui.Select(chip, EqualityComparer<T>.Default.Equals(value, current));
                AutomationHelpers.Name(chip, $"{title}: {text}");
                row.Children.Add(chip);
            }
            stack.Children.Add(row);
        }
        (bool, string)[] OnOff() => new[] { (true, Loc.T("wx.on")), (false, Loc.T("wx.off")) };

        Group(Loc.T("wx.set.temperature"), new[] { (TemperatureUnit.Celsius, "°C"), (TemperatureUnit.Fahrenheit, "°F") }, _settings.Temperature, v => _settings.Temperature = v);
        Group(Loc.T("wx.set.wind"), new[] { (WindUnit.KilometersPerHour, "km/h"), (WindUnit.MetersPerSecond, "m/s"), (WindUnit.MilesPerHour, "mph"), (WindUnit.Knots, Loc.T("wx.set.knots")) }, _settings.Wind, v => _settings.Wind = v);
        Group(Loc.T("wx.set.pressure"), new[] { (PressureUnit.Hectopascal, "hPa"), (PressureUnit.MillimetersOfMercury, "mmHg"), (PressureUnit.InchesOfMercury, "inHg") }, _settings.Pressure, v => _settings.Pressure = v);
        Group(Loc.T("wx.set.precip"), new[] { (PrecipitationUnit.Millimeters, Loc.T("wx.set.mm")), (PrecipitationUnit.Inches, Loc.T("wx.set.inch")) }, _settings.Precipitation, v => _settings.Precipitation = v);
        Group(Loc.T("wx.set.distance"), new[] { (DistanceUnit.Kilometers, Loc.T("wx.set.km")), (DistanceUnit.Miles, Loc.T("wx.set.miles")) }, _settings.Distance, v => _settings.Distance = v);
        Group(Loc.T("wx.set.clock"), new[] { (true, Loc.T("wx.set.24h")), (false, Loc.T("wx.set.12h")) }, Format.Use24Hour, v => _settings.Use24Hour = v);
        Group(Loc.T("wx.set.animations"), OnOff(), _settings.Animations, v => _settings.Animations = v);
        Group(Loc.T("wx.set.reduced"), OnOff(), _settings.ReducedMotion, v => _settings.ReducedMotion = v);
        Group(Loc.T("wx.set.autoLocation"), OnOff(), _settings.AutoLocation, v =>
        {
            _settings.AutoLocation = v;
            if (v) _ = LocateAsync(selectIt: false);
        });
        Group(Loc.T("wx.set.autoRefresh"), OnOff(), _settings.AutoRefresh, v => _settings.AutoRefresh = v);
        Group(Loc.T("wx.set.interval"), new[] { (15, "15 min"), (30, "30 min"), (60, "60 min") }, _settings.RefreshMinutes, v => _settings.RefreshMinutes = v);
        Group(Loc.T("wx.set.notifySevere"), OnOff(), _settings.NotifySevere, v => _settings.NotifySevere = v);
        Group(Loc.T("wx.set.notifyPrecip"), OnOff(), _settings.NotifyPrecipitation, v => _settings.NotifyPrecipitation = v);
        stack.Children.Add(Wrapped(Loc.T("wx.set.notifyHelp"), 11, "TextMuted"));
        if (!SystemParameters.ClientAreaAnimation) stack.Children.Add(Wrapped(Loc.T("wx.set.systemAnimations"), 11, "TextMuted"));
        OpenOverlay(Loc.T("wx.settings"), stack, 560, OpenSettings);
    }

    private void AfterSettingsChange()
    {
        WeatherStore.Normalize(_settings);
        WeatherStore.Save(_settings);
        ConfigureAutoRefresh();
        ApplyAnimationSettings();
        Rebuild();
        _overlayRefresh?.Invoke();
    }

    /// <summary>Tiny helper: equal-width columns that wrap nothing (used for the day/night halves).</summary>
    private sealed class UniformGrid2
    {
        public UniformGrid2(int columns) => Panel = new System.Windows.Controls.Primitives.UniformGrid { Columns = columns };
        public System.Windows.Controls.Primitives.UniformGrid Panel { get; }
        public void Add(UIElement element) => Panel.Children.Add(element);
    }
}
