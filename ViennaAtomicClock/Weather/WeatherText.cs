namespace ViennaAtomicClock.Weather;

public enum PrecipitationType { Rain, Snow, Mixed }
public enum PrecipitationIntensity { Light, Moderate, Heavy }
public enum MinuteOutlookKind { None, Starts, Ends, Continues }
public enum HourMarkerKind { Sunrise, Sunset, RainStart, RainEnd, TemperatureRise, TemperatureDrop }

/// <summary>What happens with precipitation in the next 60 minutes.</summary>
public sealed record MinuteOutlook(MinuteOutlookKind Kind, PrecipitationType Type, PrecipitationIntensity Intensity, int Minutes, DateTimeOffset? Time);

public sealed record HourMarker(HourMarkerKind Kind, DateTimeOffset Time);

/// <summary>Weather wording and ratings. Logic is pure (testable); texts come from <see cref="Loc"/>.</summary>
public static class WeatherText
{
    /// <summary>Threshold for "it is precipitating" within one 15-minute step (≈ 0.2 mm/h).</summary>
    public const double WetStep = 0.05;

    public static WeatherCondition FromWmo(int code) => code switch
    {
        0 => WeatherCondition.Clear,
        1 => WeatherCondition.MostlyClear,
        2 => WeatherCondition.PartlyCloudy,
        3 => WeatherCondition.Cloudy,
        45 or 48 => WeatherCondition.Fog,
        51 or 53 or 55 => WeatherCondition.Drizzle,
        56 or 57 or 66 or 67 => WeatherCondition.FreezingRain,
        61 or 63 => WeatherCondition.Rain,
        65 => WeatherCondition.HeavyRain,
        71 or 73 or 77 => WeatherCondition.Snow,
        75 => WeatherCondition.HeavySnow,
        80 or 81 => WeatherCondition.Showers,
        82 => WeatherCondition.HeavyRain,
        85 or 86 => WeatherCondition.SnowShowers,
        95 => WeatherCondition.Thunderstorm,
        96 or 99 => WeatherCondition.Hail,
        _ => WeatherCondition.Cloudy
    };

    public static bool IsWet(WeatherCondition condition) => condition is WeatherCondition.Drizzle or WeatherCondition.Rain
        or WeatherCondition.HeavyRain or WeatherCondition.FreezingRain or WeatherCondition.Showers or WeatherCondition.Thunderstorm or WeatherCondition.Hail;

    public static bool IsSnowy(WeatherCondition condition) => condition is WeatherCondition.Snow or WeatherCondition.HeavySnow or WeatherCondition.SnowShowers;

    public static string Condition(WeatherCondition condition, bool isDay) =>
        condition is WeatherCondition.Clear or WeatherCondition.MostlyClear
            ? Loc.T($"wx.cond.{condition}.{(isDay ? "day" : "night")}")
            : Loc.T("wx.cond." + condition);

    public static PrecipitationIntensity Intensity(double millimetersPerHour) =>
        millimetersPerHour < 2.5 ? PrecipitationIntensity.Light : millimetersPerHour < 7.6 ? PrecipitationIntensity.Moderate : PrecipitationIntensity.Heavy;

    /// <summary>Looks at the 15-minute steps of the next hour (from <paramref name="now"/>).</summary>
    public static MinuteOutlook? NextHour(IReadOnlyList<MinutePrecipitation>? steps, DateTimeOffset now)
    {
        if (steps is null || steps.Count == 0) return null;
        // Each value is the sum of the 15 minutes *before* its time stamp. Take the step that contains "now" plus the following hour.
        var step = TimeSpan.FromMinutes(15);
        var window = steps.Where(s => s.Time > now && s.Time - step < now.AddMinutes(60)).OrderBy(s => s.Time).ToList();
        if (window.Count == 0) return null;

        var wet = window.Where(s => s.Precipitation >= WetStep).ToList();
        if (wet.Count == 0) return new MinuteOutlook(MinuteOutlookKind.None, PrecipitationType.Rain, PrecipitationIntensity.Light, 0, null);

        var snow = wet.Sum(s => s.Snowfall);
        var rainWater = wet.Sum(s => s.Precipitation) - snow * 0.7; // ~0.7 mm water per cm of snow
        var type = snow <= 0.01 ? PrecipitationType.Rain : rainWater <= 0.05 ? PrecipitationType.Snow : PrecipitationType.Mixed;
        var intensity = Intensity(wet.Max(s => s.Precipitation) * 4);

        var nowWet = window[0].Precipitation >= WetStep;
        if (!nowWet)
        {
            var start = wet[0].Time - step < now ? now : wet[0].Time - step;
            return new MinuteOutlook(MinuteOutlookKind.Starts, type, intensity, Math.Max(1, (int)Math.Round((start - now).TotalMinutes)), start);
        }

        var firstDry = window.FirstOrDefault(s => s.Precipitation < WetStep);
        if (firstDry is null) return new MinuteOutlook(MinuteOutlookKind.Continues, type, intensity, 60, null);
        var end = firstDry.Time - step;
        return new MinuteOutlook(MinuteOutlookKind.Ends, type, intensity, Math.Max(1, (int)Math.Round((end - now).TotalMinutes)), end);
    }

    public static string Describe(MinuteOutlook outlook, WeatherFormatter format)
    {
        var what = Loc.T($"wx.min.{outlook.Intensity}.{outlook.Type}");
        return outlook.Kind switch
        {
            MinuteOutlookKind.None => Loc.T("wx.min.none"),
            MinuteOutlookKind.Starts => Loc.F("wx.min.starts", what, outlook.Minutes),
            MinuteOutlookKind.Ends => Loc.F("wx.min.ends", what, format.Time(outlook.Time!.Value)),
            _ => Loc.F("wx.min.continues", what)
        };
    }

    /// <summary>Sunrise/sunset, rain start/end and big temperature jumps within the hourly strip.</summary>
    public static List<HourMarker> Markers(IReadOnlyList<HourlyForecast> hours, IEnumerable<DailyForecast> days)
    {
        var markers = new List<HourMarker>();
        if (hours.Count == 0) return markers;
        var from = hours[0].Time;
        var to = hours[^1].Time.AddHours(1);

        foreach (var day in days)
        {
            if (day.Sunrise is { } rise && rise > from && rise < to) markers.Add(new HourMarker(HourMarkerKind.Sunrise, rise));
            if (day.Sunset is { } set && set > from && set < to) markers.Add(new HourMarker(HourMarkerKind.Sunset, set));
        }

        static bool Wet(HourlyForecast h) => (h.Precipitation ?? 0) >= 0.2 || (h.PrecipitationProbability ?? 0) >= 60;
        for (var i = 1; i < hours.Count; i++)
        {
            if (Wet(hours[i]) && !Wet(hours[i - 1])) markers.Add(new HourMarker(HourMarkerKind.RainStart, hours[i].Time));
            else if (!Wet(hours[i]) && Wet(hours[i - 1])) markers.Add(new HourMarker(HourMarkerKind.RainEnd, hours[i].Time));
        }

        // A change of at least 5 °C within three hours is worth a label (one label per jump).
        for (var i = 3; i < hours.Count; i++)
        {
            var delta = hours[i].Temperature - hours[i - 3].Temperature;
            if (Math.Abs(delta) < 5) continue;
            var kind = delta > 0 ? HourMarkerKind.TemperatureRise : HourMarkerKind.TemperatureDrop;
            if (markers.Any(m => m.Kind == kind && (hours[i].Time - m.Time).TotalHours < 4)) continue;
            markers.Add(new HourMarker(kind, hours[i].Time));
        }

        return markers.OrderBy(m => m.Time).ToList();
    }

    /// <summary>Lowest and highest value of all days, so every bar uses the same scale.</summary>
    public static (double Min, double Max) TemperatureRange(IEnumerable<DailyForecast> days, double? current = null)
    {
        var list = days.ToList();
        if (list.Count == 0) return (0, 1);
        var min = list.Min(d => d.Low);
        var max = list.Max(d => d.High);
        if (current is { } now)
        {
            min = Math.Min(min, now);
            max = Math.Max(max, now);
        }
        return max - min < 1 ? (min - 0.5, max + 0.5) : (min, max);
    }

    /// <summary>Position (0..1) of a value on the shared scale.</summary>
    public static double Scale(double value, (double Min, double Max) range) =>
        Math.Clamp((value - range.Min) / (range.Max - range.Min), 0, 1);

    public static int AirQualityLevel(double index) => index switch
    {
        < 20 => 0,
        < 40 => 1,
        < 60 => 2,
        < 80 => 3,
        < 100 => 4,
        _ => 5
    };

    /// <summary>Pollutant with the highest share of its EAQI limit ("very poor" thresholds).</summary>
    public static string? MainPollutant(AirQuality air)
    {
        var shares = new List<(string Name, double Share)>();
        if (air.Pm25 is { } pm25) shares.Add(("PM2.5", pm25 / 50));
        if (air.Pm10 is { } pm10) shares.Add(("PM10", pm10 / 100));
        if (air.Ozone is { } o3) shares.Add(("O₃", o3 / 240));
        if (air.NitrogenDioxide is { } no2) shares.Add(("NO₂", no2 / 230));
        return shares.Count == 0 ? null : shares.MaxBy(s => s.Share).Name;
    }

    public static int UvLevel(double index) => index switch
    {
        < 3 => 0,
        < 6 => 1,
        < 8 => 2,
        < 11 => 3,
        _ => 4
    };

    public static string Compass(double degrees)
    {
        var names = Loc.T("wx.compass").Split(' ');
        if (names.Length != 16) names = "N NNE NE ENE E ESE SE SSE S SSW SW WSW W WNW NW NNW".Split(' ');
        var index = (int)Math.Round(((degrees % 360) + 360) % 360 / 22.5) % 16;
        return names[index];
    }

    public static int VisibilityLevel(double meters) => meters >= 10_000 ? 0 : meters >= 1_000 ? 1 : 2;

    /// <summary>-1 falling, 0 steady, 1 rising (change over three hours).</summary>
    public static int PressureTrend(IReadOnlyList<HourlyForecast> hours, DateTimeOffset now)
    {
        var past = hours.LastOrDefault(h => h.Time <= now.AddHours(-3) && h.Pressure is not null);
        var current = hours.LastOrDefault(h => h.Time <= now && h.Pressure is not null);
        if (past?.Pressure is not { } before || current?.Pressure is not { } after) return 0;
        var delta = after - before;
        return delta > 1 ? 1 : delta < -1 ? -1 : 0;
    }

    /// <summary>0 dry … 3 oppressive, by dew point.</summary>
    public static int HumidityLevel(double dewPoint) => dewPoint switch
    {
        < 10 => 0,
        < 16 => 1,
        < 20 => 2,
        _ => 3
    };

    /// <summary>0 = like the real temperature, 1 = wind makes it colder, 2 = humidity makes it warmer, 3 = colder, 4 = warmer.</summary>
    public static int FeelsLikeReason(double temperature, double apparent, double? windKmh, double? humidity)
    {
        var delta = apparent - temperature;
        if (Math.Abs(delta) < 1) return 0;
        if (delta < 0) return (windKmh ?? 0) >= 10 ? 1 : 3;
        return (humidity ?? 0) >= 60 ? 2 : 4;
    }

    public static int MoonPhaseIndex(double phase) => (int)Math.Round(phase * 8) % 8;

    /// <summary>Most frequent condition of a set of hours (worse weather wins a tie).</summary>
    public static WeatherCondition Dominant(IEnumerable<HourlyForecast> hours)
    {
        var groups = hours.GroupBy(h => h.Condition).ToList();
        return groups.Count == 0 ? WeatherCondition.Cloudy : groups.OrderByDescending(g => g.Count()).ThenByDescending(g => (int)g.Key).First().Key;
    }

    /// <summary>Short sentence for a day: condition, temperatures and wind.</summary>
    public static string DaySummary(DailyForecast day, WeatherFormatter format)
    {
        var text = Loc.F("wx.day.summary", Condition(day.Condition, true), format.Degrees(day.Low), format.Degrees(day.High));
        if (day.WindMax is { } wind && day.WindDirection is { } direction)
            text += " " + Loc.F("wx.day.wind", Compass(direction), format.Wind(day.GustMax ?? wind));
        if (day.PrecipitationProbability is { } p && p >= 30)
            text += " " + Loc.F("wx.day.precip", format.Percent(p));
        return text;
    }

    /// <summary>Screen reader sentence for the header.</summary>
    public static string SpokenSummary(WeatherSnapshot snapshot, WeatherFormatter format)
    {
        var today = snapshot.Daily.FirstOrDefault();
        var unit = Loc.T(format.Settings.Temperature == TemperatureUnit.Fahrenheit ? "wx.spoken.fahrenheit" : "wx.spoken.degrees");
        string Spoken(double c) => WeatherUnits.RoundDegrees(WeatherUnits.Temperature(c, format.Settings.Temperature)).ToString(format.Culture) + " " + unit;
        var text = $"{snapshot.Location.Name}, {Spoken(snapshot.Current.Temperature)}, {Condition(snapshot.Current.Condition, snapshot.Current.IsDay)}";
        if (today is not null) text += ", " + Loc.F("wx.spoken.highlow", Spoken(today.High), Spoken(today.Low));
        return text + ".";
    }
}
