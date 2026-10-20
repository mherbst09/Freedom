using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ViennaAtomicClock.Weather;

/// <summary>Time zone helpers: provider times are local wall-clock times of the place.</summary>
public static class WeatherTime
{
    public static TimeZoneInfo Zone(string? ianaId, int utcOffsetSeconds = 0)
    {
        if (!string.IsNullOrEmpty(ianaId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { }
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaId, out var windowsId))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
                catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { }
            }
        }
        var offset = TimeSpan.FromSeconds(utcOffsetSeconds);
        return TimeZoneInfo.CreateCustomTimeZone("UTC" + offset, offset, "UTC" + offset, "UTC" + offset);
    }

    /// <summary>Turns a local wall-clock time of the place into an exact moment (skips the hour that DST removes).</summary>
    public static DateTimeOffset FromLocal(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public static DateTimeOffset Parse(string text, TimeZoneInfo zone) =>
        FromLocal(DateTime.ParseExact(text, text.Length > 10 ? "yyyy-MM-dd'T'HH:mm" : "yyyy-MM-dd", CultureInfo.InvariantCulture), zone);

    public static DateTimeOffset ToPlace(DateTimeOffset moment, string? ianaId) => TimeZoneInfo.ConvertTime(moment, Zone(ianaId));
}

/// <summary>Open-Meteo (free, no key, CC BY 4.0) for forecasts, air quality, climate data and map grids; GeoSphere Austria for warnings.</summary>
public sealed class OpenMeteoProvider : IWeatherProvider
{
    private const string Forecast = "https://api.open-meteo.com/v1/forecast";
    private const string AirQualityUrl = "https://air-quality-api.open-meteo.com/v1/air-quality";
    private const string Archive = "https://archive-api.open-meteo.com/v1/archive";

    private const string CurrentFields = "temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,weather_code,cloud_cover,pressure_msl,wind_speed_10m,wind_direction_10m,wind_gusts_10m,visibility,uv_index,dew_point_2m";
    private const string HourlyFields = "temperature_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,snowfall,weather_code,wind_speed_10m,wind_direction_10m,wind_gusts_10m,uv_index,visibility,pressure_msl,relative_humidity_2m,dew_point_2m,cloud_cover,cloud_cover_low,cloud_cover_mid,cloud_cover_high,is_day";
    private const string DailyFields = "weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum,rain_sum,showers_sum,snowfall_sum,precipitation_probability_max,wind_speed_10m_max,wind_gusts_10m_max,wind_direction_10m_dominant,uv_index_max,sunrise,sunset,daylight_duration";

    private readonly HttpClient _http;
    private readonly GeoSphereAlerts _alerts;
    private readonly Func<DateTimeOffset> _now;

    public OpenMeteoProvider(HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        _http = http ?? WeatherHttp.Shared;
        _alerts = new GeoSphereAlerts(_http);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string Name => "Open-Meteo";

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    public async Task<WeatherSnapshot> GetSnapshotAsync(WeatherLocation location, CancellationToken cancellation)
    {
        var url = $"{Forecast}?latitude={F(location.Latitude)}&longitude={F(location.Longitude)}&current={CurrentFields}" +
                  $"&hourly={HourlyFields}&daily={DailyFields}&minutely_15=precipitation,snowfall&forecast_minutely_15=8" +
                  "&timezone=auto&forecast_days=10";
        var forecastTask = WeatherHttp.GetStringAsync(_http, url, cancellation);
        var airTask = TryAsync<AirQuality>(() => GetAirQualityAsync(location, cancellation));
        var alertsTask = _alerts.Covers(location) ? TryAsync<List<WeatherAlert>>(async () => await _alerts.GetAsync(location, Loc.Language, cancellation)) : Task.FromResult<List<WeatherAlert>?>(null);

        var snapshot = ParseForecast(await forecastTask.ConfigureAwait(false), location, _now());
        snapshot.AirQuality = await airTask.ConfigureAwait(false);
        var alerts = await alertsTask.ConfigureAwait(false);
        snapshot.AlertsAvailable = alerts is not null;
        snapshot.Alerts = alerts ?? new List<WeatherAlert>();
        snapshot.Attribution.Add("Wetterdaten: Open-Meteo.com (CC BY 4.0)");
        if (_alerts.Covers(location)) snapshot.Attribution.Add("Warnungen: GeoSphere Austria");
        return snapshot;
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T?>> load) where T : class
    {
        try { return await load().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; } // optional data: the page hides the card instead of failing
    }

    public static WeatherSnapshot ParseForecast(string json, WeatherLocation location, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tzId = root.TryGetProperty("timezone", out var tz) ? tz.GetString() ?? "" : "";
        var offset = root.TryGetProperty("utc_offset_seconds", out var off) ? off.GetInt32() : 0;
        var zone = WeatherTime.Zone(tzId, offset);

        var snapshot = new WeatherSnapshot { Location = location, FetchedAt = fetchedAt, TimeZoneId = tzId };

        if (root.TryGetProperty("current", out var c))
        {
            snapshot.Current = new CurrentWeather
            {
                Time = WeatherTime.Parse(c.GetProperty("time").GetString()!, zone),
                Temperature = Num(c, "temperature_2m") ?? 0,
                ApparentTemperature = Num(c, "apparent_temperature"),
                Condition = WeatherText.FromWmo((int)(Num(c, "weather_code") ?? 3)),
                IsDay = (Num(c, "is_day") ?? 1) > 0,
                Humidity = Num(c, "relative_humidity_2m"),
                DewPoint = Num(c, "dew_point_2m"),
                Pressure = Num(c, "pressure_msl"),
                Visibility = Num(c, "visibility"),
                CloudCover = Num(c, "cloud_cover"),
                Precipitation = Num(c, "precipitation"),
                UvIndex = Num(c, "uv_index"),
                Wind = Num(c, "wind_speed_10m") is { } speed
                    ? new WindData { Speed = speed, Direction = Num(c, "wind_direction_10m") ?? 0, Gusts = Num(c, "wind_gusts_10m") }
                    : null
            };
        }

        if (root.TryGetProperty("hourly", out var h))
        {
            var times = h.GetProperty("time");
            for (var i = 0; i < times.GetArrayLength(); i++)
            {
                var temperature = At(h, "temperature_2m", i);
                if (temperature is null) continue; // missing data is left out, never shown as 0
                snapshot.Hourly.Add(new HourlyForecast
                {
                    Time = WeatherTime.Parse(times[i].GetString()!, zone),
                    Temperature = temperature.Value,
                    ApparentTemperature = At(h, "apparent_temperature", i),
                    Condition = WeatherText.FromWmo((int)(At(h, "weather_code", i) ?? 3)),
                    IsDay = (At(h, "is_day", i) ?? 1) > 0,
                    PrecipitationProbability = At(h, "precipitation_probability", i),
                    Precipitation = At(h, "precipitation", i),
                    Rain = At(h, "rain", i),
                    Showers = At(h, "showers", i),
                    Snowfall = At(h, "snowfall", i),
                    Wind = At(h, "wind_speed_10m", i) is { } speed
                        ? new WindData { Speed = speed, Direction = At(h, "wind_direction_10m", i) ?? 0, Gusts = At(h, "wind_gusts_10m", i) }
                        : null,
                    UvIndex = At(h, "uv_index", i),
                    Visibility = At(h, "visibility", i),
                    Pressure = At(h, "pressure_msl", i),
                    Humidity = At(h, "relative_humidity_2m", i),
                    DewPoint = At(h, "dew_point_2m", i),
                    CloudCover = At(h, "cloud_cover", i),
                    CloudLow = At(h, "cloud_cover_low", i),
                    CloudMid = At(h, "cloud_cover_mid", i),
                    CloudHigh = At(h, "cloud_cover_high", i)
                });
            }
            snapshot.Hourly.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        if (root.TryGetProperty("daily", out var d))
        {
            var times = d.GetProperty("time");
            for (var i = 0; i < times.GetArrayLength(); i++)
            {
                if (At(d, "temperature_2m_max", i) is not { } high || At(d, "temperature_2m_min", i) is not { } low) continue;
                snapshot.Daily.Add(new DailyForecast
                {
                    Date = WeatherTime.Parse(times[i].GetString()!, zone),
                    High = high,
                    Low = low,
                    Condition = WeatherText.FromWmo((int)(At(d, "weather_code", i) ?? 3)),
                    PrecipitationProbability = At(d, "precipitation_probability_max", i),
                    PrecipitationSum = At(d, "precipitation_sum", i),
                    RainSum = At(d, "rain_sum", i),
                    ShowersSum = At(d, "showers_sum", i),
                    SnowfallSum = At(d, "snowfall_sum", i),
                    WindMax = At(d, "wind_speed_10m_max", i),
                    GustMax = At(d, "wind_gusts_10m_max", i),
                    WindDirection = At(d, "wind_direction_10m_dominant", i),
                    UvMax = At(d, "uv_index_max", i),
                    Sunrise = Text(d, "sunrise", i) is { Length: > 0 } rise ? WeatherTime.Parse(rise, zone) : null,
                    Sunset = Text(d, "sunset", i) is { Length: > 0 } set ? WeatherTime.Parse(set, zone) : null,
                    DaylightSeconds = At(d, "daylight_duration", i)
                });
            }
        }

        if (root.TryGetProperty("minutely_15", out var m) && m.TryGetProperty("time", out var mTimes))
        {
            var list = new List<MinutePrecipitation>();
            for (var i = 0; i < mTimes.GetArrayLength(); i++)
            {
                if (At(m, "precipitation", i) is not { } precipitation) continue;
                list.Add(new MinutePrecipitation
                {
                    Time = WeatherTime.Parse(mTimes[i].GetString()!, zone),
                    Precipitation = precipitation,
                    Snowfall = At(m, "snowfall", i) ?? 0
                });
            }
            snapshot.Minutely = list.Count > 0 ? list : null;
        }

        return snapshot;
    }

    private async Task<AirQuality?> GetAirQualityAsync(WeatherLocation location, CancellationToken cancellation)
    {
        var url = $"{AirQualityUrl}?latitude={F(location.Latitude)}&longitude={F(location.Longitude)}" +
                  "&current=european_aqi,pm2_5,pm10,ozone,nitrogen_dioxide&hourly=european_aqi&forecast_hours=24&timezone=auto";
        return ParseAirQuality(await WeatherHttp.GetStringAsync(_http, url, cancellation, retries: 1).ConfigureAwait(false));
    }

    public static AirQuality? ParseAirQuality(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("current", out var c) || Num(c, "european_aqi") is not { } index) return null;
        var air = new AirQuality
        {
            Index = index,
            Pm25 = Num(c, "pm2_5"),
            Pm10 = Num(c, "pm10"),
            Ozone = Num(c, "ozone"),
            NitrogenDioxide = Num(c, "nitrogen_dioxide")
        };
        if (root.TryGetProperty("hourly", out var h) && h.TryGetProperty("european_aqi", out var values))
            foreach (var value in values.EnumerateArray())
                if (value.ValueKind == JsonValueKind.Number) air.HourlyIndex.Add(value.GetDouble());
        return air;
    }

    public async Task<ClimateNormals?> GetNormalsAsync(WeatherLocation location, CancellationToken cancellation)
    {
        // Ten years of daily values in one request; the days within ±7 days of today form the "normal".
        var today = WeatherTime.ToPlace(_now(), location.TimeZoneId).Date;
        var end = today.AddDays(-7);
        var start = new DateTime(end.Year - 10, 1, 1);
        var url = $"{Archive}?latitude={F(location.Latitude)}&longitude={F(location.Longitude)}" +
                  $"&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}&daily=temperature_2m_max,temperature_2m_min,precipitation_sum&timezone=auto";
        var json = await WeatherHttp.GetStringAsync(_http, url, cancellation, retries: 1, timeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);
        return ParseNormals(json, today, _now());
    }

    public static ClimateNormals? ParseNormals(string json, DateTime today, DateTimeOffset computed)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("daily", out var d)) return null;
        var times = d.GetProperty("time");
        double highs = 0, lows = 0, rain = 0;
        int count = 0, rainCount = 0;
        var years = new HashSet<int>();
        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            var date = DateTime.ParseExact(times[i].GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var distance = Math.Abs((date.DayOfYear - today.DayOfYear + 366 + 183) % 366 - 183);
            if (distance > 7) continue;
            if (At(d, "temperature_2m_max", i) is not { } high || At(d, "temperature_2m_min", i) is not { } low) continue;
            highs += high;
            lows += low;
            count++;
            years.Add(date.Year);
            if (At(d, "precipitation_sum", i) is { } p)
            {
                rain += p;
                rainCount++;
            }
        }
        if (count < 30) return null;
        return new ClimateNormals
        {
            AverageHigh = highs / count,
            AverageLow = lows / count,
            AveragePrecipitation = rainCount > 0 ? rain / rainCount : 0,
            Years = years.Count,
            Computed = computed
        };
    }

    public async Task<WeatherMapGrid> GetMapGridAsync(WeatherLocation center, WeatherMapLayer layer, CancellationToken cancellation)
    {
        const int size = 9;
        var lats = Enumerable.Range(0, size).Select(i => Math.Round(center.Latitude + 2.4 - i * 0.6, 3)).ToArray();
        var lons = Enumerable.Range(0, size).Select(i => Math.Round(center.Longitude - 3.6 + i * 0.9, 3)).ToArray();
        var pointLats = new List<string>();
        var pointLons = new List<string>();
        foreach (var lat in lats)
            foreach (var lon in lons)
            {
                pointLats.Add(F(Math.Clamp(lat, -85, 85)));
                pointLons.Add(F(((lon + 540) % 360) - 180));
            }

        var (baseUrl, field) = layer switch
        {
            WeatherMapLayer.Temperature => (Forecast, "temperature_2m"),
            WeatherMapLayer.Wind => (Forecast, "wind_speed_10m,wind_direction_10m"),
            WeatherMapLayer.Clouds => (Forecast, "cloud_cover"),
            WeatherMapLayer.Pressure => (Forecast, "pressure_msl"),
            WeatherMapLayer.AirQuality => (AirQualityUrl, "european_aqi"),
            _ => (Forecast, "precipitation")
        };
        var url = $"{baseUrl}?latitude={string.Join(',', pointLats)}&longitude={string.Join(',', pointLons)}&hourly={field}&forecast_hours=25&timezone=GMT";
        var json = await WeatherHttp.GetStringAsync(_http, url, cancellation, timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return ParseGrid(json, layer, lats, lons);
    }

    public static WeatherMapGrid ParseGrid(string json, WeatherMapLayer layer, double[] lats, double[] lons)
    {
        using var document = JsonDocument.Parse(json);
        var points = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToList()
            : new List<JsonElement> { document.RootElement };
        var field = layer switch
        {
            WeatherMapLayer.Temperature => "temperature_2m",
            WeatherMapLayer.Wind => "wind_speed_10m",
            WeatherMapLayer.Clouds => "cloud_cover",
            WeatherMapLayer.Pressure => "pressure_msl",
            WeatherMapLayer.AirQuality => "european_aqi",
            _ => "precipitation"
        };

        var first = points[0].GetProperty("hourly");
        var times = first.GetProperty("time").EnumerateArray()
            .Select(t => new DateTimeOffset(DateTime.ParseExact(t.GetString()!, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture), TimeSpan.Zero)).ToArray();
        var values = new float[times.Length][];
        var directions = layer == WeatherMapLayer.Wind ? new float[times.Length][] : null;
        for (var t = 0; t < times.Length; t++)
        {
            values[t] = new float[points.Count];
            if (directions is not null) directions[t] = new float[points.Count];
        }

        for (var p = 0; p < points.Count; p++)
        {
            var hourly = points[p].GetProperty("hourly");
            for (var t = 0; t < times.Length; t++)
            {
                values[t][p] = At(hourly, field, t) is { } v ? (float)v : float.NaN;
                if (directions is not null) directions[t][p] = At(hourly, "wind_direction_10m", t) is { } dir ? (float)dir : float.NaN;
            }
        }

        return new WeatherMapGrid { Layer = layer, Latitudes = lats, Longitudes = lons, Times = times, Values = values, Directions = directions };
    }

    private static double? Num(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static double? At(JsonElement element, string name, int index)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength()) return null;
        var value = array[index];
        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }

    private static string? Text(JsonElement element, string name, int index)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength()) return null;
        var value = array[index];
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

/// <summary>Official warnings of GeoSphere Austria (only for places in Austria).</summary>
public sealed class GeoSphereAlerts
{
    private readonly HttpClient _http;

    public GeoSphereAlerts(HttpClient http) => _http = http;

    public bool Covers(WeatherLocation location) =>
        location.CountryCode.Equals("AT", StringComparison.OrdinalIgnoreCase)
        || (location.CountryCode.Length == 0 && location.Latitude is > 46.35 and < 49.05 && location.Longitude is > 9.5 and < 17.2);

    public async Task<List<WeatherAlert>> GetAsync(WeatherLocation location, string language, CancellationToken cancellation)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"https://warnungen.zamg.at/wsapp/api/getWarningsForCoords?lon={location.Longitude:0.####}&lat={location.Latitude:0.####}&lang={(language == "de" ? "de" : "en")}");
        return Parse(await WeatherHttp.GetStringAsync(_http, url, cancellation, retries: 1).ConfigureAwait(false));
    }

    public static List<WeatherAlert> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var alerts = new List<WeatherAlert>();
        if (!document.RootElement.TryGetProperty("properties", out var properties)) return alerts;
        var region = properties.TryGetProperty("location", out var location) && location.TryGetProperty("properties", out var lp) && lp.TryGetProperty("name", out var name)
            ? name.GetString() ?? ""
            : "";
        if (!properties.TryGetProperty("warnings", out var warnings) || warnings.ValueKind != JsonValueKind.Array) return alerts;

        var vienna = WeatherTime.Zone("Europe/Vienna", 3600);
        foreach (var warning in warnings.EnumerateArray())
        {
            if (!warning.TryGetProperty("properties", out var w)) continue;
            var level = w.TryGetProperty("warnstufeid", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 1;
            var type = w.TryGetProperty("warntypid", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
            DateTimeOffset start, end;
            if (w.TryGetProperty("rawinfo", out var raw) && long.TryParse(raw.GetProperty("start").ToString(), out var s) && long.TryParse(raw.GetProperty("end").ToString(), out var e))
            {
                start = DateTimeOffset.FromUnixTimeSeconds(s);
                end = DateTimeOffset.FromUnixTimeSeconds(e);
            }
            else
            {
                start = WeatherTime.FromLocal(DateTime.ParseExact(Str(w, "begin"), "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture), vienna);
                end = WeatherTime.FromLocal(DateTime.ParseExact(Str(w, "end"), "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture), vienna);
            }

            alerts.Add(new WeatherAlert
            {
                Id = "geosphere-" + (w.TryGetProperty("warnid", out var id) ? id.ToString() : Guid.NewGuid().ToString("N")) + "-" + (w.TryGetProperty("chgid", out var chg) ? chg.ToString() : "0"),
                Severity = level switch { >= 3 => AlertSeverity.Extreme, 2 => AlertSeverity.Severe, _ => AlertSeverity.Moderate },
                Kind = type switch
                {
                    1 => AlertKind.Wind, 2 => AlertKind.Rain, 3 => AlertKind.Snow, 4 => AlertKind.Ice,
                    5 => AlertKind.Thunderstorm, 6 => AlertKind.Heat, 7 => AlertKind.Cold, _ => AlertKind.Other
                },
                Title = Clean(Str(w, "text")),
                Issuer = "GeoSphere Austria",
                Start = start,
                End = end,
                Region = region,
                Summary = Clean(Str(w, "auswirkungen")),
                Description = Clean(Str(w, "meteotext")),
                Instructions = Clean(Str(w, "empfehlungen")),
                Source = "warnungen.zamg.at"
            });
        }
        return alerts.OrderByDescending(a => a.Severity).ThenBy(a => a.Start).ToList();
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>Strips HTML tags and list stars from the warning texts.</summary>
    private static string Clean(string text) =>
        Regex.Replace(Regex.Replace(text, "<[^>]+>", ""), @"^\*\s*", "• ", RegexOptions.Multiline).Replace("\r\n", "\n").Trim();
}

/// <summary>Place search via the Open-Meteo geocoding API (names and postcodes); reverse lookup via OpenStreetMap Nominatim.</summary>
public sealed class OpenMeteoGeocoding : IGeocodingService
{
    private readonly HttpClient _http;

    public OpenMeteoGeocoding(HttpClient? http = null) => _http = http ?? WeatherHttp.Shared;

    /// <summary>Country codes listed first in postcode results (e.g. the countries of saved places).</summary>
    public Func<IEnumerable<string>>? PreferredCountries { get; set; }

    /// <summary>"5020", "80331", "SW1A 1AA": short, with a digit, no spaces at the ends.</summary>
    public static bool LooksLikePostcode(string query) =>
        query.Length is >= 3 and <= 10 && query.Any(char.IsDigit) && Regex.IsMatch(query, "^[0-9A-Za-z][0-9A-Za-z -]*$");

    public async Task<IReadOnlyList<WeatherLocation>> SearchAsync(string query, string language, CancellationToken cancellation)
    {
        query = query.Trim();
        if (query.Length < 2) return Array.Empty<WeatherLocation>();
        if (LooksLikePostcode(query))
        {
            // Open-Meteo hardly knows postcodes; OpenStreetMap does.
            var postUrl = $"https://nominatim.openstreetmap.org/search?postalcode={Uri.EscapeDataString(query)}&format=jsonv2&addressdetails=1&limit=8&accept-language={language}";
            var found = ParsePostcodes(await WeatherHttp.GetStringAsync(_http, postUrl, cancellation, retries: 1, timeout: TimeSpan.FromSeconds(8)).ConfigureAwait(false),
                PreferredCountries?.Invoke() ?? Array.Empty<string>());
            if (found.Count > 0) return found;
        }
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=8&language={language}&format=json";
        return ParseSearch(await WeatherHttp.GetStringAsync(_http, url, cancellation, retries: 1, timeout: TimeSpan.FromSeconds(8)).ConfigureAwait(false));
    }

    public static IReadOnlyList<WeatherLocation> ParsePostcodes(string json, IEnumerable<string> preferredCountries)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<WeatherLocation>();
        var preferred = preferredCountries.Select(c => c.ToUpperInvariant()).ToList();
        var list = new List<WeatherLocation>();
        foreach (var r in document.RootElement.EnumerateArray())
        {
            if (!r.TryGetProperty("address", out var address)) continue;
            string S(string name) => address.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var place = new[] { "city", "town", "village", "municipality", "suburb", "city_district", "county", "state" }.Select(S).FirstOrDefault(n => n.Length > 0) ?? "";
            if (!double.TryParse(r.GetProperty("lat").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(r.GetProperty("lon").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
            var postcode = S("postcode");
            list.Add(new WeatherLocation
            {
                Id = "osm" + (r.TryGetProperty("place_id", out var id) ? id.ToString() : Guid.NewGuid().ToString("N")),
                Name = place.Length > 0 ? $"{place} ({postcode})" : postcode,
                Region = S("state"),
                Country = S("country"),
                CountryCode = S("country_code").ToUpperInvariant(),
                Latitude = lat,
                Longitude = lon
            });
        }
        return list.Select((l, i) => (l, i))
            .OrderBy(x => preferred.IndexOf(x.l.CountryCode) is var p and >= 0 ? p : int.MaxValue)
            .ThenBy(x => x.i)
            .Select(x => x.l)
            .ToList();
    }

    public static IReadOnlyList<WeatherLocation> ParseSearch(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results)) return Array.Empty<WeatherLocation>();
        var list = new List<WeatherLocation>();
        foreach (var r in results.EnumerateArray())
        {
            string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            list.Add(new WeatherLocation
            {
                Id = "geo" + (r.TryGetProperty("id", out var id) ? id.ToString() : Guid.NewGuid().ToString("N")),
                Name = S("name"),
                Region = S("admin1"),
                Country = S("country"),
                CountryCode = S("country_code"),
                Latitude = r.GetProperty("latitude").GetDouble(),
                Longitude = r.GetProperty("longitude").GetDouble(),
                TimeZoneId = S("timezone")
            });
        }
        return list;
    }

    public async Task<WeatherLocation?> ReverseAsync(double latitude, double longitude, string language, CancellationToken cancellation)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={latitude:0.####}&lon={longitude:0.####}&zoom=10&accept-language={language}");
        using var document = JsonDocument.Parse(await WeatherHttp.GetStringAsync(_http, url, cancellation, retries: 1, timeout: TimeSpan.FromSeconds(8)).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("address", out var address)) return null;
        string S(string name) => address.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        var place = new[] { "city", "town", "village", "municipality", "county" }.Select(S).FirstOrDefault(n => n.Length > 0) ?? "";
        return new WeatherLocation
        {
            Name = place,
            Region = S("state"),
            Country = S("country"),
            CountryCode = S("country_code").ToUpperInvariant(),
            Latitude = latitude,
            Longitude = longitude
        };
    }
}

/// <summary>Device position through Windows location services (respects the Windows privacy setting).</summary>
public sealed class WindowsLocationService : ILocationService
{
    public async Task<(double Latitude, double Longitude)?> GetPositionAsync(CancellationToken cancellation)
    {
        try
        {
            var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync().AsTask(cancellation).ConfigureAwait(false);
            if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed) return null;
            var locator = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.Default };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10)).AsTask(cancellation).ConfigureAwait(false);
            return (position.Coordinate.Point.Position.Latitude, position.Coordinate.Point.Position.Longitude);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
