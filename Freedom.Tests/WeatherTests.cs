using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ViennaAtomicClock;
using ViennaAtomicClock.Weather;
using Xunit;
using Xunit.Abstractions;

namespace Freedom.Tests;

internal static class WeatherFixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static WeatherLocation Salzburg => WeatherStoreAccess.DefaultPlace();

    public static WeatherSnapshot Snapshot()
    {
        var snapshot = OpenMeteoProvider.ParseForecast(Read("forecast-salzburg.json"), Salzburg, DateTimeOffset.UtcNow);
        snapshot.AirQuality = OpenMeteoProvider.ParseAirQuality(Read("air-salzburg.json"));
        snapshot.Alerts = GeoSphereAlerts.Parse(Read("warnings-innsbruck.json"));
        snapshot.AlertsAvailable = true;
        snapshot.Normals = new ClimateNormals { AverageHigh = 13.5, AverageLow = 4.2, AveragePrecipitation = 2.8, Years = 10, Computed = DateTimeOffset.UtcNow };
        snapshot.Attribution.Add("Wetterdaten: Open-Meteo.com (CC BY 4.0)");
        return snapshot;
    }
}

/// <summary>WeatherStore is internal to the app; this keeps the tests readable.</summary>
internal static class WeatherStoreAccess
{
    public static WeatherLocation DefaultPlace() => WeatherStore.DefaultPlace();
}

internal sealed class FakeProvider : IWeatherProvider
{
    public int SnapshotCalls;
    public Func<WeatherLocation, CancellationToken, Task<WeatherSnapshot>>? OnSnapshot;
    public string Name => "Fake";

    public async Task<WeatherSnapshot> GetSnapshotAsync(WeatherLocation location, CancellationToken cancellation)
    {
        Interlocked.Increment(ref SnapshotCalls);
        if (OnSnapshot is not null) return await OnSnapshot(location, cancellation);
        var snapshot = WeatherFixtures.Snapshot();
        snapshot.Location = location;
        return snapshot;
    }

    public Task<ClimateNormals?> GetNormalsAsync(WeatherLocation location, CancellationToken cancellation) =>
        Task.FromResult<ClimateNormals?>(new ClimateNormals { AverageHigh = 12, AverageLow = 3, AveragePrecipitation = 2, Years = 10, Computed = DateTimeOffset.UtcNow });

    public Task<WeatherMapGrid> GetMapGridAsync(WeatherLocation center, WeatherMapLayer layer, CancellationToken cancellation) =>
        Task.FromResult(SyntheticGrid(center, layer));

    /// <summary>3×3 grid with values rising to the east, two time steps.</summary>
    public static WeatherMapGrid SyntheticGrid(WeatherLocation center, WeatherMapLayer layer) => new()
    {
        Layer = layer,
        Latitudes = new[] { center.Latitude + 1, center.Latitude, center.Latitude - 1 },
        Longitudes = new[] { center.Longitude - 1, center.Longitude, center.Longitude + 1 },
        Times = new[] { DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1) },
        Values = new[] { new float[] { 0, 2, 5, 0, 1, 8, 0, 0, float.NaN }, new float[] { 1, 3, 6, 0, 2, 9, 0, 1, 4 } },
        Directions = layer == WeatherMapLayer.Wind ? new[] { new float[9], new float[9] } : null
    };
}

internal sealed class MemoryWeatherCache : IWeatherCache
{
    public readonly Dictionary<string, WeatherSnapshot> Items = new();
    public WeatherSnapshot? Load(string locationId) => Items.GetValueOrDefault(locationId);
    public void Save(WeatherSnapshot snapshot) => Items[snapshot.Location.Id] = snapshot;
    public void Remove(string locationId) => Items.Remove(locationId);
}

internal sealed class FakeGeocoding : IGeocodingService
{
    public Task<IReadOnlyList<WeatherLocation>> SearchAsync(string query, string language, CancellationToken cancellation) =>
        Task.FromResult(OpenMeteoGeocoding.ParseSearch(WeatherFixtures.Read("geocoding-salzburg.json")));

    public Task<WeatherLocation?> ReverseAsync(double latitude, double longitude, string language, CancellationToken cancellation) =>
        Task.FromResult<WeatherLocation?>(null);
}

internal sealed class NoLocation : ILocationService
{
    public Task<(double Latitude, double Longitude)?> GetPositionAsync(CancellationToken cancellation) => Task.FromResult<(double, double)?>(null);
}

/// <summary>Answers every request with a fixed result after an optional delay (or never).</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;
    public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _answer(request, cancellationToken);
}

public sealed class WeatherUnitTests
{
    [Fact]
    public void Units_convert_from_metric()
    {
        Assert.Equal(32, WeatherUnits.Temperature(0, TemperatureUnit.Fahrenheit), 6);
        Assert.Equal(212, WeatherUnits.Temperature(100, TemperatureUnit.Fahrenheit), 6);
        Assert.Equal(-40, WeatherUnits.Temperature(-40, TemperatureUnit.Fahrenheit), 6);
        Assert.Equal(27.7778, WeatherUnits.Wind(100, WindUnit.MetersPerSecond), 3);
        Assert.Equal(62.1371, WeatherUnits.Wind(100, WindUnit.MilesPerHour), 3);
        Assert.Equal(53.9957, WeatherUnits.Wind(100, WindUnit.Knots), 3);
        Assert.Equal(760.0, WeatherUnits.Pressure(1013.25, PressureUnit.MillimetersOfMercury), 1);
        Assert.Equal(29.92, WeatherUnits.Pressure(1013.25, PressureUnit.InchesOfMercury), 2);
        Assert.Equal(1.0, WeatherUnits.Precipitation(25.4, PrecipitationUnit.Inches), 6);
        Assert.Equal(1.0, WeatherUnits.Distance(1609.344, DistanceUnit.Miles), 6);
        Assert.Equal(24.1, WeatherUnits.Distance(24_100, DistanceUnit.Kilometers), 6);
    }

    [Fact]
    public void Rounded_degrees_never_show_minus_zero()
    {
        Assert.Equal(0, WeatherUnits.RoundDegrees(-0.4));
        Assert.Equal("0°", new WeatherFormatter(new WeatherSettings(), CultureInfo.GetCultureInfo("de-AT")).Degrees(-0.3));
        Assert.Equal(-1, WeatherUnits.RoundDegrees(-0.5));
        Assert.Equal(3, WeatherUnits.RoundDegrees(2.5));
    }

    [Fact]
    public void Formatting_follows_culture_and_units()
    {
        var german = new WeatherFormatter(new WeatherSettings(), CultureInfo.GetCultureInfo("de-AT"));
        var english = new WeatherFormatter(new WeatherSettings { Temperature = TemperatureUnit.Fahrenheit, Wind = WindUnit.MilesPerHour, Use24Hour = false }, CultureInfo.GetCultureInfo("en-GB"));
        Assert.Equal("1,2 mm", german.Precipitation(1.24));
        Assert.Equal("1.2 mm", english.Precipitation(1.24));
        Assert.Equal("68°", english.Degrees(20));
        Assert.Equal("20 °C", german.Temperature(20));
        Assert.Equal("62 mph", english.Wind(100));
        var time = new DateTimeOffset(2026, 10, 20, 15, 5, 0, TimeSpan.FromHours(2));
        Assert.Equal("15:05", german.Time(time));
        Assert.Contains("3:05", english.Time(time));
        Assert.True(german.Use24Hour);
        Assert.Equal("1 h 05 min", german.Duration(TimeSpan.FromMinutes(65)));
    }

    [Fact]
    public void Ten_day_bars_share_one_scale()
    {
        var days = new[]
        {
            new DailyForecast { Low = 4, High = 12 }, new DailyForecast { Low = -2, High = 6 }, new DailyForecast { Low = 8, High = 21 }
        };
        var range = WeatherText.TemperatureRange(days);
        Assert.Equal((-2.0, 21.0), range);
        Assert.Equal(0, WeatherText.Scale(-2, range), 6);
        Assert.Equal(1, WeatherText.Scale(21, range), 6);
        Assert.Equal(6 / 23.0, WeatherText.Scale(4, range), 6);
        // The current temperature widens the scale if it is outside the forecast range.
        Assert.Equal((-5.0, 21.0), WeatherText.TemperatureRange(days, -5));
        // A flat forecast still produces a usable scale.
        var flat = WeatherText.TemperatureRange(new[] { new DailyForecast { Low = 5, High = 5 } });
        Assert.True(flat.Max > flat.Min);
    }

    [Fact]
    public void Local_times_use_the_zone_of_the_place()
    {
        var vienna = WeatherTime.Zone("Europe/Vienna");
        Assert.Equal(TimeSpan.FromHours(2), WeatherTime.Parse("2026-07-01T12:00", vienna).Offset);
        Assert.Equal(TimeSpan.FromHours(1), WeatherTime.Parse("2026-01-01T12:00", vienna).Offset);
        // 02:30 does not exist on the spring-forward day; it moves to 03:30 summer time.
        var gap = WeatherTime.Parse("2026-03-29T02:30", vienna);
        Assert.Equal(new DateTime(2026, 3, 29, 3, 30, 0), gap.DateTime);
        Assert.Equal(TimeSpan.FromHours(2), gap.Offset);

        var tokyo = WeatherTime.ToPlace(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), "Asia/Tokyo");
        Assert.Equal(9, tokyo.Hour);
        Assert.Equal(TimeSpan.FromHours(9), tokyo.Offset);
        // Unknown ids fall back to the offset the provider reported.
        Assert.Equal(TimeSpan.FromHours(3), WeatherTime.Zone("Nowhere/Unknown", 3 * 3600).GetUtcOffset(DateTime.UtcNow));
    }

    [Fact]
    public void Forecast_parsing_keeps_place_time_and_order()
    {
        var snapshot = OpenMeteoProvider.ParseForecast(WeatherFixtures.Read("forecast-salzburg.json"), WeatherFixtures.Salzburg, DateTimeOffset.UtcNow);
        Assert.Equal("Europe/Vienna", snapshot.TimeZoneId);
        Assert.Equal(240, snapshot.Hourly.Count);
        Assert.Equal(10, snapshot.Daily.Count);
        Assert.All(snapshot.Hourly.Zip(snapshot.Hourly.Skip(1)), pair => Assert.True(pair.First.Time < pair.Second.Time));
        Assert.Equal(TimeSpan.Zero, snapshot.Daily[0].Date.TimeOfDay);
        Assert.Equal(TimeSpan.FromHours(2), snapshot.Hourly[0].Offset());
        Assert.NotNull(snapshot.Daily[0].Sunrise);
        Assert.NotNull(snapshot.Minutely);
        Assert.All(snapshot.Daily, d => Assert.True(d.High >= d.Low));
    }

    [Fact]
    public void Hourly_forecast_is_sorted_even_if_the_source_is_not()
    {
        const string json = """
        {"timezone":"Europe/Vienna","utc_offset_seconds":7200,
         "hourly":{"time":["2026-10-20T03:00","2026-10-20T01:00","2026-10-20T02:00"],"temperature_2m":[3,1,2],"weather_code":[0,0,0]}}
        """;
        var snapshot = OpenMeteoProvider.ParseForecast(json, WeatherFixtures.Salzburg, DateTimeOffset.UtcNow);
        Assert.Equal(new[] { 1.0, 2, 3 }, snapshot.Hourly.Select(h => h.Temperature));
    }

    [Fact]
    public void Missing_optional_values_stay_missing()
    {
        const string json = """
        {"timezone":"Europe/Vienna","utc_offset_seconds":7200,
         "current":{"time":"2026-10-20T19:00","temperature_2m":11.6,"weather_code":0,"is_day":0},
         "hourly":{"time":["2026-10-20T19:00","2026-10-20T20:00"],"temperature_2m":[11.6,null],"weather_code":[0,1],"uv_index":[null,null],"precipitation_probability":[null,5]},
         "daily":{"time":["2026-10-20"],"temperature_2m_max":[14.5],"temperature_2m_min":[null],"weather_code":[0]}}
        """;
        var snapshot = OpenMeteoProvider.ParseForecast(json, WeatherFixtures.Salzburg, DateTimeOffset.UtcNow);
        Assert.Null(snapshot.Current.Humidity);
        Assert.Null(snapshot.Current.ApparentTemperature);
        Assert.Null(snapshot.Current.Wind);
        Assert.Null(snapshot.Current.UvIndex);
        Assert.Single(snapshot.Hourly); // the hour without a temperature is left out, not shown as 0 °C
        Assert.Null(snapshot.Hourly[0].UvIndex);
        Assert.Null(snapshot.Hourly[0].PrecipitationProbability);
        Assert.Empty(snapshot.Daily); // a day without a low is not drawn with a fake value
        Assert.Null(snapshot.Minutely);
        Assert.Null(OpenMeteoProvider.ParseAirQuality("""{"current":{"time":"2026-10-20T19:00","european_aqi":null}}"""));
    }

    [Fact]
    public void Air_quality_and_geocoding_are_parsed()
    {
        var air = OpenMeteoProvider.ParseAirQuality(WeatherFixtures.Read("air-salzburg.json"));
        Assert.NotNull(air);
        Assert.InRange(air!.Index, 0, 500);
        Assert.NotNull(WeatherText.MainPollutant(air));
        Assert.Equal(24, air.HourlyIndex.Count);

        var places = OpenMeteoGeocoding.ParseSearch(WeatherFixtures.Read("geocoding-salzburg.json"));
        Assert.Equal("Salzburg", places[0].Name);
        Assert.Equal("AT", places[0].CountryCode);
        Assert.Equal("Europe/Vienna", places[0].TimeZoneId);
    }

    [Fact]
    public void Postcodes_are_found_and_saved_countries_come_first()
    {
        Assert.True(OpenMeteoGeocoding.LooksLikePostcode("5020"));
        Assert.True(OpenMeteoGeocoding.LooksLikePostcode("SW1A 1AA"));
        Assert.False(OpenMeteoGeocoding.LooksLikePostcode("Salzburg"));
        Assert.False(OpenMeteoGeocoding.LooksLikePostcode("12"));
        var places = OpenMeteoGeocoding.ParsePostcodes(WeatherFixtures.Read("postcode-5020.json"), new[] { "AT" });
        Assert.True(places.Count >= 2);
        Assert.Equal("AT", places[0].CountryCode);
        Assert.Contains("5020", places[0].Name);
        Assert.InRange(places[0].Latitude, 47.7, 47.9);
        // Without a preference the service order is kept.
        Assert.Equal("BE", OpenMeteoGeocoding.ParsePostcodes(WeatherFixtures.Read("postcode-5020.json"), Array.Empty<string>())[0].CountryCode);
    }

    [Fact]
    public void Map_grid_is_parsed_per_point_and_hour()
    {
        var grid = OpenMeteoProvider.ParseGrid(WeatherFixtures.Read("grid-wind.json"), WeatherMapLayer.Wind, new[] { 48.0, 47.0 }, new[] { 13.0 });
        Assert.Equal(3, grid.Times.Length);
        Assert.Equal(2, grid.Values[0].Length);
        Assert.NotNull(grid.Directions);
        Assert.All(grid.Values.SelectMany(v => v), v => Assert.False(float.IsNaN(v)));
        Assert.Equal(TimeSpan.Zero, grid.Times[0].Offset);
    }

    [Fact]
    public void Map_colours_follow_the_value()
    {
        Assert.Equal(0, MapPalette.ColorAt(WeatherMapLayer.Precipitation, 0).A);
        Assert.True(MapPalette.ColorAt(WeatherMapLayer.Precipitation, 5).A > 150);
        Assert.Equal(Colors.Transparent, MapPalette.ColorAt(WeatherMapLayer.Temperature, double.NaN));
        var cold = MapPalette.ColorAt(WeatherMapLayer.Temperature, -15);
        var hot = MapPalette.ColorAt(WeatherMapLayer.Temperature, 35);
        Assert.True(cold.B > cold.R);
        Assert.True(hot.R > hot.B);
    }

    [Fact]
    public void Warnings_are_parsed_with_severity_kind_and_times()
    {
        var alerts = GeoSphereAlerts.Parse(WeatherFixtures.Read("warnings-innsbruck.json"));
        var alert = Assert.Single(alerts);
        Assert.Equal(AlertKind.Wind, alert.Kind);
        Assert.Equal(AlertSeverity.Moderate, alert.Severity);
        Assert.Equal("Innsbruck", alert.Region);
        Assert.Equal("GeoSphere Austria", alert.Issuer);
        Assert.True(alert.End > alert.Start);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792497600), alert.Start);
        Assert.StartsWith("•", alert.Instructions);
        Assert.DoesNotContain("<", alert.Summary);

        const string red = """
        {"properties":{"location":{"properties":{"name":"Testdorf"}},"warnings":[
          {"properties":{"warnid":1,"chgid":0,"warntypid":5,"warnstufeid":3,"begin":"20.10.2026 14:00","end":"20.10.2026 20:00","text":"<b>Rote</b> Gewitterwarnung","auswirkungen":"","empfehlungen":"","meteotext":""}}]}}
        """;
        var extreme = Assert.Single(GeoSphereAlerts.Parse(red));
        Assert.Equal(AlertSeverity.Extreme, extreme.Severity);
        Assert.Equal(AlertKind.Thunderstorm, extreme.Kind);
        Assert.Equal("Rote Gewitterwarnung", extreme.Title);
        Assert.Equal(TimeSpan.FromHours(2), extreme.Start.Offset); // Vienna summer time
        Assert.Empty(GeoSphereAlerts.Parse("""{"type":"Feature","properties":{"warnings":[]}}"""));
    }

    [Fact]
    public void Warnings_only_cover_austria()
    {
        var alerts = new GeoSphereAlerts(new HttpClient());
        Assert.True(alerts.Covers(WeatherFixtures.Salzburg));
        Assert.False(alerts.Covers(new WeatherLocation { Name = "Berlin", CountryCode = "DE", Latitude = 52.5, Longitude = 13.4 }));
    }

    [Fact]
    public void Next_hour_outlook_describes_start_end_and_dry()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(2));
        List<MinutePrecipitation> Steps(params double[] mm) =>
            mm.Select((p, i) => new MinutePrecipitation { Time = now.AddMinutes(15 * (i + 1)), Precipitation = p }).ToList();

        Assert.Equal(MinuteOutlookKind.None, WeatherText.NextHour(Steps(0, 0, 0, 0), now)!.Kind);
        var starts = WeatherText.NextHour(Steps(0, 0, 0.3, 0.5), now)!;
        Assert.Equal(MinuteOutlookKind.Starts, starts.Kind);
        Assert.Equal(30, starts.Minutes);
        var ends = WeatherText.NextHour(Steps(0.4, 0.2, 0, 0), now)!;
        Assert.Equal(MinuteOutlookKind.Ends, ends.Kind);
        Assert.Equal(now.AddMinutes(30), ends.Time);
        Assert.Equal(MinuteOutlookKind.Continues, WeatherText.NextHour(Steps(1, 1, 1, 1), now)!.Kind);
        Assert.Equal(PrecipitationIntensity.Moderate, WeatherText.NextHour(Steps(1, 1, 1, 1), now)!.Intensity);
        Assert.Null(WeatherText.NextHour(null, now));
        var snow = Steps(0.3, 0.3, 0.3, 0.3);
        snow.ForEach(s => s.Snowfall = 0.5);
        Assert.Equal(PrecipitationType.Snow, WeatherText.NextHour(snow, now)!.Type);
    }

    [Fact]
    public void Hourly_strip_marks_sun_rain_and_temperature_jumps()
    {
        var start = new DateTimeOffset(2026, 10, 20, 5, 0, 0, TimeSpan.FromHours(2));
        var hours = Enumerable.Range(0, 12).Select(i => new HourlyForecast
        {
            Time = start.AddHours(i),
            Temperature = i < 6 ? 5 : 12,
            PrecipitationProbability = i is >= 3 and < 5 ? 80 : 0
        }).ToList();
        var days = new[] { new DailyForecast { Date = start.Date, Sunrise = start.AddHours(2.5), Sunset = start.AddHours(13) } };
        var markers = WeatherText.Markers(hours, days);
        Assert.Contains(markers, m => m.Kind == HourMarkerKind.Sunrise && m.Time == start.AddHours(2.5));
        Assert.DoesNotContain(markers, m => m.Kind == HourMarkerKind.Sunset); // after the strip
        Assert.Contains(markers, m => m.Kind == HourMarkerKind.RainStart && m.Time == start.AddHours(3));
        Assert.Contains(markers, m => m.Kind == HourMarkerKind.RainEnd && m.Time == start.AddHours(5));
        Assert.Single(markers, m => m.Kind == HourMarkerKind.TemperatureRise);
        Assert.Equal(markers.OrderBy(m => m.Time), markers);
    }

    [Fact]
    public void Ratings_and_compass_cover_their_ranges()
    {
        Assert.Equal(0, WeatherText.AirQualityLevel(10));
        Assert.Equal(5, WeatherText.AirQualityLevel(140));
        Assert.Equal(0, WeatherText.UvLevel(2));
        Assert.Equal(4, WeatherText.UvLevel(11));
        Assert.Equal(2, WeatherText.VisibilityLevel(500));
        Assert.Equal(1, WeatherText.FeelsLikeReason(5, 1, 25, 50));
        Assert.Equal(2, WeatherText.FeelsLikeReason(28, 31, 5, 80));
        Assert.Equal(0, WeatherText.FeelsLikeReason(10, 10.4, 5, 50));
        Assert.Equal(WeatherCondition.Thunderstorm, WeatherText.FromWmo(95));
        Assert.Equal(WeatherCondition.Fog, WeatherText.FromWmo(45));
        Assert.Equal(4, WeatherText.MoonPhaseIndex(0.5));
        Assert.Equal(0, WeatherText.MoonPhaseIndex(0.98));
    }

    [Fact]
    public void Sun_and_moon_match_published_values()
    {
        var snapshot = OpenMeteoProvider.ParseForecast(WeatherFixtures.Read("forecast-salzburg.json"), WeatherFixtures.Salzburg, DateTimeOffset.UtcNow);
        var day = snapshot.Daily[0];
        var astro = Astronomy.ForDay(day.Date, WeatherTime.Zone("Europe/Vienna"), 47.7994, 13.044);
        Assert.InRange(Math.Abs((astro.Sunrise!.Value - day.Sunrise!.Value).TotalMinutes), 0, 4);
        Assert.InRange(Math.Abs((astro.Sunset!.Value - day.Sunset!.Value).TotalMinutes), 0, 4);
        Assert.True(astro.CivilDawn < astro.Sunrise && astro.CivilDusk > astro.Sunset);
        Assert.InRange(astro.MoonIllumination, 0, 1);
        Assert.InRange(astro.DaysToFullMoon, 0, Astronomy.SynodicMonth);

        // Full moon on 2026-08-28 (published): illumination close to 100 %.
        var (phase, fraction) = Astronomy.MoonIllumination(new DateTime(2026, 8, 28, 4, 18, 0, DateTimeKind.Utc));
        Assert.InRange(fraction, 0.98, 1);
        Assert.InRange(phase, 0.45, 0.55);
        // Polar night: no sunrise in Tromsø in mid-December.
        Assert.Null(Astronomy.SunTimes(new DateTime(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc), 69.65, 18.96).Sunrise);
    }

    [Fact]
    public void Places_are_sorted_current_first_then_favourites()
    {
        var places = new List<WeatherLocation>
        {
            new() { Id = "a", Name = "Graz", Latitude = 47.07 }, new() { Id = "b", Name = "Linz", Latitude = 48.3, IsFavorite = true },
            new() { Id = "c", Name = "Wien", Latitude = 48.2 }, new() { Id = "current", Name = "Hier", Latitude = 47.07, IsCurrentLocation = true },
            new() { Id = "d", Name = "Bregenz", Latitude = 47.5, IsFavorite = true }
        };
        Assert.Equal(new[] { "current", "b", "d", "a", "c" }, WeatherStore.Ordered(places).Select(p => p.Id));
        Assert.True(WeatherStore.Move(places, "c", -1));
        Assert.Equal(new[] { "current", "b", "d", "c", "a" }, WeatherStore.Ordered(places).Select(p => p.Id));
        Assert.False(WeatherStore.Move(places, "current", -1));

        var settings = new WeatherSettings { Locations = places };
        var again = WeatherStore.Add(settings, new WeatherLocation { Id = "x", Name = "Graz 2", Latitude = 47.071 });
        Assert.Equal("a", again.Id); // nearly the same position → the saved place is reused
        WeatherStore.Remove(settings, "b");
        Assert.DoesNotContain(settings.Locations, l => l.Id == "b");
    }

    [Fact]
    public void Settings_are_repaired_when_loaded()
    {
        var settings = new WeatherSettings { RefreshMinutes = 7, Locations = new List<WeatherLocation> { new() { Name = "", Latitude = 400 } } };
        WeatherStore.Normalize(settings);
        Assert.Equal(30, settings.RefreshMinutes);
        Assert.Equal("Salzburg", Assert.Single(settings.Locations).Name);
        Assert.Equal("Salzburg", WeatherStore.Selected(settings).Name);
    }
}

public sealed class WeatherServiceTests
{
    [Fact]
    public void Cache_round_trips_and_ignores_broken_files()
    {
        using var folder = new TempFolder();
        var cache = new WeatherFileCache(folder.Path);
        var snapshot = WeatherFixtures.Snapshot();
        cache.Save(snapshot);
        var other = WeatherFixtures.Snapshot();
        other.Location = new WeatherLocation { Id = "geo2", Name = "Linz" };
        cache.Save(other);

        var loaded = cache.Load(snapshot.Location.Id);
        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Hourly.Count, loaded!.Hourly.Count);
        Assert.Equal(snapshot.Hourly[5].Time, loaded.Hourly[5].Time);
        Assert.Equal(snapshot.Alerts.Count, loaded.Alerts.Count);
        Assert.Equal(WeatherSnapshot.CurrentVersion, loaded.Version);

        // A damaged file only hides its own place.
        File.WriteAllText(Path.Combine(folder.Path, "geo2.json"), "{ broken");
        Assert.Null(cache.Load("geo2"));
        Assert.NotNull(cache.Load(snapshot.Location.Id));

        // Unknown future versions are not trusted.
        var json = File.ReadAllText(Path.Combine(folder.Path, snapshot.Location.Id + ".json")).Replace("\"Version\":1", "\"Version\":99");
        File.WriteAllText(Path.Combine(folder.Path, snapshot.Location.Id + ".json"), json);
        Assert.Null(cache.Load(snapshot.Location.Id));
        Assert.Null(cache.Load("never-saved"));
    }

    [Fact]
    public async Task Offline_refresh_keeps_showing_the_cached_snapshot()
    {
        var cache = new MemoryWeatherCache();
        var cached = WeatherFixtures.Snapshot();
        cached.FetchedAt = DateTimeOffset.UtcNow.AddHours(-2);
        cache.Save(cached);
        var provider = new FakeProvider { OnSnapshot = (_, _) => throw new HttpRequestException("No connection") };
        var service = new WeatherService(provider, cache);

        var result = await service.RefreshAsync(cached.Location, force: true, CancellationToken.None);
        Assert.Same(cached, result.Snapshot);
        Assert.True(result.FromCache);
        Assert.Equal(WeatherLoadError.Offline, result.Error);

        var failed = new WeatherService(new FakeProvider { OnSnapshot = (_, _) => throw new InvalidDataException("bad json") }, cache);
        Assert.Equal(WeatherLoadError.Failed, (await failed.RefreshAsync(cached.Location, true, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Parallel_requests_for_one_place_are_joined()
    {
        var gate = new TaskCompletionSource();
        var provider = new FakeProvider();
        provider.OnSnapshot = async (location, token) =>
        {
            await gate.Task.WaitAsync(token);
            var snapshot = WeatherFixtures.Snapshot();
            snapshot.Location = location;
            return snapshot;
        };
        var service = new WeatherService(provider, new MemoryWeatherCache());
        var place = WeatherFixtures.Salzburg;
        var first = service.RefreshAsync(place, true, CancellationToken.None);
        var second = service.RefreshAsync(place, true, CancellationToken.None);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, provider.SnapshotCalls);
        Assert.Same((await first).Snapshot, (await second).Snapshot);
    }

    [Fact]
    public async Task Refresh_rate_is_limited_unless_forced()
    {
        var cache = new MemoryWeatherCache();
        var fresh = WeatherFixtures.Snapshot();
        fresh.FetchedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        cache.Save(fresh);
        var provider = new FakeProvider();
        var service = new WeatherService(provider, cache);
        var result = await service.RefreshAsync(fresh.Location, force: false, CancellationToken.None);
        Assert.Equal(0, provider.SnapshotCalls);
        Assert.True(result.FromCache);
        await service.RefreshAsync(fresh.Location, force: true, CancellationToken.None);
        Assert.Equal(1, provider.SnapshotCalls);
        Assert.NotNull(cache.Load(fresh.Location.Id)!.Normals);
    }

    [Fact]
    public async Task Network_requests_can_be_cancelled_quickly()
    {
        var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var provider = new OpenMeteoProvider(new HttpClient(handler));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetSnapshotAsync(WeatherFixtures.Salzburg, cancel.Token));
        Assert.True(watch.ElapsedMilliseconds < 2000, $"Cancelling took {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task Hanging_server_times_out_and_server_errors_are_retried()
    {
        var hanging = new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await Assert.ThrowsAsync<TimeoutException>(() =>
            WeatherHttp.GetStringAsync(new HttpClient(hanging), "https://example.invalid/", CancellationToken.None, retries: 0, timeout: TimeSpan.FromMilliseconds(200)));

        var attempts = 0;
        var flaky = new StubHandler((_, _) => Task.FromResult(++attempts < 2
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }));
        Assert.Equal("ok", await WeatherHttp.GetStringAsync(new HttpClient(flaky), "https://example.invalid/", CancellationToken.None));
        Assert.Equal(2, attempts);

        var notFound = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<HttpRequestException>(() => WeatherHttp.GetStringAsync(new HttpClient(notFound), "https://example.invalid/", CancellationToken.None));
    }
}

public sealed class WeatherTextTests
{
    private static readonly Regex Placeholder = new(@"\{\d\}");

    [Fact]
    public void Every_weather_text_exists_in_all_languages_with_the_same_placeholders()
    {
        foreach (var (key, texts) in WeatherStrings.Table)
        {
            Assert.True(texts.Length == Loc.Languages.Length, $"{key} has {texts.Length} languages.");
            Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t), $"{key} has an empty text."));
            var expected = Placeholder.Matches(texts[0]).Select(m => m.Value).OrderBy(v => v).ToList();
            foreach (var text in texts)
                Assert.True(expected.SequenceEqual(Placeholder.Matches(text).Select(m => m.Value).OrderBy(v => v)), $"{key}: placeholders differ in \"{text}\".");
        }
        Assert.All(WeatherStrings.Table["wx.compass"], c => Assert.Equal(16, c.Split(' ').Length));
        // The weather table is merged into the app's text table.
        Assert.True(Strings.Table.ContainsKey("wx.now"));
    }

    [Fact]
    public void Every_key_used_by_the_weather_page_has_a_text()
    {
        var keys = new List<string>();
        foreach (var condition in Enum.GetValues<WeatherCondition>())
            keys.AddRange(condition is WeatherCondition.Clear or WeatherCondition.MostlyClear
                ? new[] { $"wx.cond.{condition}.day", $"wx.cond.{condition}.night" }
                : new[] { $"wx.cond.{condition}" });
        keys.AddRange(Enum.GetValues<AlertKind>().Select(k => "wx.alert.kind." + k));
        keys.AddRange(Enum.GetValues<AlertSeverity>().Select(k => "wx.alert.severity." + k));
        keys.AddRange(Enum.GetValues<WeatherMapLayer>().Select(k => "wx.layer." + k));
        keys.AddRange(Enum.GetValues<HourMarkerKind>().Select(k => "wx.marker." + k));
        foreach (var intensity in Enum.GetValues<PrecipitationIntensity>())
        {
            keys.Add("wx.intensity." + intensity);
            keys.AddRange(Enum.GetValues<PrecipitationType>().Select(t => $"wx.min.{intensity}.{t}"));
        }
        keys.AddRange(Enumerable.Range(0, 6).SelectMany(i => new[] { "wx.aq.level" + i, "wx.aq.advice" + i }));
        keys.AddRange(Enumerable.Range(0, 5).SelectMany(i => new[] { "wx.uv.level" + i, "wx.uv.advice" + i, "wx.feels.reason" + i }));
        keys.AddRange(Enumerable.Range(0, 4).Select(i => "wx.humidity.level" + i));
        keys.AddRange(Enumerable.Range(0, 3).SelectMany(i => new[] { "wx.vis.level" + i, "wx.vis.explain" + i }));
        keys.AddRange(Enumerable.Range(0, 8).Select(i => "wx.moon.phase" + i));
        keys.AddRange(new[] { -1, 0, 1 }.SelectMany(i => new[] { "wx.pressure.trend" + i, "wx.pressure.explain" + i }));

        // Plain keys written in the source code.
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ViennaAtomicClock", "Weather"));
        if (Directory.Exists(source))
            foreach (var file in Directory.GetFiles(source, "*.cs"))
                keys.AddRange(Regex.Matches(File.ReadAllText(file), "\"(wx\\.[A-Za-z0-9_.]+[A-Za-z0-9])\"").Select(m => m.Groups[1].Value));

        var missing = keys.Distinct().Where(k => !WeatherStrings.Table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing texts: " + string.Join(", ", missing));
    }
}

[Collection(TimingCollection.Name)]
public sealed class WeatherRenderTests
{
    private readonly ITestOutputHelper _output;

    public WeatherRenderTests(ITestOutputHelper output) => _output = output;

    private static WeatherPage CreatePage(out MemoryWeatherCache cache)
    {
        MapTiles.Enabled = false;
        cache = new MemoryWeatherCache();
        var snapshot = WeatherFixtures.Snapshot();
        // Keep the warning active and the data "now", whatever day the test runs.
        foreach (var alert in snapshot.Alerts) alert.End = DateTimeOffset.UtcNow.AddHours(3);
        cache.Save(snapshot);
        var settings = new WeatherSettings { Locations = new List<WeatherLocation> { snapshot.Location }, SelectedLocationId = snapshot.Location.Id, Animations = false, ReducedMotion = true };
        return new WeatherPage(new WeatherService(new FakeProvider(), cache), new FakeGeocoding(), new NoLocation(), settings);
    }

    [Fact]
    public void Weather_page_renders_responsively_in_every_theme()
    {
        UiTestHost.Run(() =>
        {
            var page = CreatePage(out _);
            foreach (var (width, height, mode) in new[] { (1500.0, 950.0, "wide"), (1000.0, 800.0, "medium"), (620.0, 900.0, "narrow") })
            {
                var bitmap = UiTestHost.Render(page, width, height);
                Assert.Equal(mode, page.LayoutMode);
                UiTestHost.SaveIfRequested(bitmap, "weather-" + mode);
            }

            try
            {
                foreach (var (theme, _, _, _) in Themes.All)
                    foreach (var dark in new[] { true, false })
                    {
                        App.ApplyTheme(theme, dark);
                        var themed = CreatePage(out _);
                        var bitmap = UiTestHost.Render(themed, 1300, 850);
                        if (theme is Themes.Standard or Themes.Hacker or Themes.Anime) UiTestHost.SaveIfRequested(bitmap, $"weather-{theme}-{(dark ? "dark" : "light")}");
                    }
                App.ApplyTheme(Themes.Standard, true, Color.FromRgb(0xE0, 0x40, 0x80));
                UiTestHost.Render(CreatePage(out _), 1300, 850);
            }
            finally
            {
                App.ApplyTheme(Themes.Standard, true);
            }
        });
    }

    [Fact]
    public void Weather_page_renders_in_every_language()
    {
        UiTestHost.Run(() =>
        {
            var page = CreatePage(out _);
            try
            {
                foreach (var (code, _, _) in Loc.Languages)
                {
                    Loc.Apply(code);
                    UiTestHost.Render(page, 1200, 800);
                }
            }
            finally
            {
                Loc.Apply("de");
            }
        });
    }

    [Fact]
    public void Every_detail_view_opens_without_errors()
    {
        UiTestHost.Run(() =>
        {
            var page = CreatePage(out _);
            UiTestHost.Render(page, 1300, 900);
            var snapshot = page.Snapshot!;
            var type = typeof(WeatherPage);
            void Open(string name, params object[] args)
            {
                var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(name);
                method.Invoke(page, args);
                var bitmap = UiTestHost.Render(page, 1300, 900);
                UiTestHost.SaveIfRequested(bitmap, "weather-detail-" + name);
                type.GetMethod("CloseOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            }

            Open("ShowHour", snapshot.Hourly[14]);
            Open("ShowDay", snapshot.Daily[1]);
            Open("ShowAlert", snapshot.Alerts[0]);
            foreach (var name in new[] { "ShowAirQuality", "ShowUv", "ShowWind", "ShowSun", "ShowPrecipitation", "ShowFeelsLike", "ShowHumidity", "ShowVisibility", "ShowPressure", "ShowClouds", "ShowMoon", "ShowAverages", "OpenLocations", "OpenSettings" })
                Open(name);
            Open("OpenMap", WeatherMapLayer.Wind);
        });
    }

    [Fact]
    public void Units_change_the_page_without_errors()
    {
        UiTestHost.Run(() =>
        {
            var page = CreatePage(out _);
            page.Settings.Temperature = TemperatureUnit.Fahrenheit;
            page.Settings.Wind = WindUnit.Knots;
            page.Settings.Pressure = PressureUnit.InchesOfMercury;
            page.Settings.Precipitation = PrecipitationUnit.Inches;
            page.Settings.Distance = DistanceUnit.Miles;
            page.Settings.Use24Hour = false;
            page.Rebuild();
            UiTestHost.Render(page, 1300, 900);
            Assert.Contains("°", page.StatusLine);
        });
    }

    [Fact]
    public void Map_overlay_and_icons_are_built_once_and_frozen()
    {
        UiTestHost.Run(() =>
        {
            var grid = FakeProvider.SyntheticGrid(WeatherFixtures.Salzburg, WeatherMapLayer.Precipitation);
            var overlay = MapCanvas.BuildOverlay(grid, 0, out var bounds);
            Assert.True(overlay.IsFrozen);
            Assert.Equal(96, overlay.PixelWidth);
            Assert.Equal(WeatherFixtures.Salzburg.Latitude + 1, bounds.North, 6);
            var map = new MapCanvas(true);
            map.SetLayer(OpenMeteoProvider.ParseGrid(WeatherFixtures.Read("grid-wind.json"), WeatherMapLayer.Wind, new[] { 48.0, 47.0 }, new[] { 13.0 }), 0); // one column: no overlay, no crash
            UiTestHost.Render(map, 400, 300);
            foreach (var condition in Enum.GetValues<WeatherCondition>())
            {
                var icon = WeatherIcons.Image(condition, true);
                Assert.True(icon.IsFrozen);
                Assert.Same(icon, WeatherIcons.Image(condition, true));
            }
        });
    }

    [Fact]
    public void Animated_sky_fits_the_60_fps_budget_in_every_scene()
    {
        UiTestHost.Run(() =>
        {
            const int width = 1920, height = 1080;
            foreach (var scene in Enum.GetValues<WeatherScene>())
            {
                var sky = new WeatherBackdrop { Animate = false };
                sky.SetScene(scene, scene == WeatherScene.ClearDay ? 0.6 : 0, 0.8, 1);
                sky.Measure(new Size(width, height));
                sky.Arrange(new Rect(0, 0, width, height));
                sky.UpdateLayout();
                for (var i = 0; i < 120; i++) sky.Advance(1 / 60.0); // particles moving
                sky.UpdateLayout();
                var warm = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                warm.Render(sky); // the whole sky once, including the static layers

                // Per frame only the particle layer is redrawn; sky, rays and crossfade run in the compositor.
                var moving = sky.MovingLayer;
                const int frames = 20;
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < frames; i++)
                {
                    sky.Advance(1 / 60.0);
                    moving.UpdateLayout();
                    var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    target.Render(moving);
                }
                var ms = watch.Elapsed.TotalMilliseconds / frames;
                _output.WriteLine($"{scene}: {ms:F2} ms/frame");
                UiTestHost.SaveIfRequested(warm, "sky-" + scene);
                Assert.True(ms <= 16.67, $"{scene} took {ms:F2} ms per Full-HD frame.");
            }
        });
    }
}

internal static class HourlyExtensions
{
    public static TimeSpan Offset(this HourlyForecast hour) => hour.Time.Offset;
}

/// <summary>Real requests against the public services; runs only with FREEDOM_LIVE=1 (no network in normal test runs).</summary>
public sealed class WeatherLiveTests
{
    private readonly ITestOutputHelper _output;

    public WeatherLiveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Live_services_deliver_a_complete_snapshot()
    {
        if (Environment.GetEnvironmentVariable("FREEDOM_LIVE") != "1") return;
        var provider = new OpenMeteoProvider();
        var place = WeatherFixtures.Salzburg;
        var snapshot = await provider.GetSnapshotAsync(place, CancellationToken.None);
        Assert.True(snapshot.Hourly.Count >= 200);
        Assert.Equal(10, snapshot.Daily.Count);
        Assert.True(snapshot.AlertsAvailable);
        var normals = await provider.GetNormalsAsync(place, CancellationToken.None);
        Assert.NotNull(normals);
        var grid = await provider.GetMapGridAsync(place, WeatherMapLayer.Temperature, CancellationToken.None);
        Assert.Equal(81, grid.Values[0].Length);
        var air = await provider.GetMapGridAsync(place, WeatherMapLayer.AirQuality, CancellationToken.None);
        Assert.Equal(81, air.Values[0].Length);
        var found = await new OpenMeteoGeocoding().SearchAsync("5020", "de", CancellationToken.None);
        var reverse = await new OpenMeteoGeocoding().ReverseAsync(47.8, 13.04, "de", CancellationToken.None);
        _output.WriteLine($"{snapshot.Current.Temperature} °C, {snapshot.Current.Condition}, AQI {snapshot.AirQuality?.Index}, alerts {snapshot.Alerts.Count}, " +
                          $"normal high {normals!.AverageHigh:F1} ({normals.Years} y), postcode → {found.FirstOrDefault()?.Name}, reverse → {reverse?.Name}");
    }
}
