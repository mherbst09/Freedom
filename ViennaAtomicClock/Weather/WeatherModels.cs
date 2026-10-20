using System.Text.Json.Serialization;

namespace ViennaAtomicClock.Weather;

/// <summary>Weather situation in a provider-independent form (drives icons, texts and the animated backdrop).</summary>
public enum WeatherCondition
{
    Clear, MostlyClear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, HeavyRain, FreezingRain,
    Showers, Snow, HeavySnow, SnowShowers, Thunderstorm, Hail
}

public enum AlertSeverity { Moderate, Severe, Extreme }

public enum AlertKind { Wind, Rain, Snow, Ice, Thunderstorm, Heat, Cold, Fog, Other }

public enum WeatherMapLayer { Precipitation, Temperature, Wind, AirQuality, Clouds, Pressure }

public sealed class WeatherLocation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>District, state or region (optional).</summary>
    public string Region { get; set; } = "";
    public string Country { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>IANA time zone id of the place, e.g. "Europe/Vienna".</summary>
    public string TimeZoneId { get; set; } = "";
    /// <summary>True for the entry that follows the device position.</summary>
    public bool IsCurrentLocation { get; set; }
    public bool IsFavorite { get; set; }

    [JsonIgnore]
    public string Subtitle => string.Join(", ", new[] { Region, Country }.Where(p => p.Length > 0 && p != Name).Distinct());
}

public sealed class WindData
{
    /// <summary>km/h.</summary>
    public double Speed { get; set; }
    /// <summary>Degrees the wind comes from (0 = north).</summary>
    public double Direction { get; set; }
    /// <summary>km/h, if known.</summary>
    public double? Gusts { get; set; }
}

public sealed class CurrentWeather
{
    public DateTimeOffset Time { get; set; }
    public double Temperature { get; set; }
    public double? ApparentTemperature { get; set; }
    public WeatherCondition Condition { get; set; }
    public bool IsDay { get; set; } = true;
    public double? Humidity { get; set; }
    public double? DewPoint { get; set; }
    /// <summary>Sea-level pressure in hPa.</summary>
    public double? Pressure { get; set; }
    /// <summary>Metres.</summary>
    public double? Visibility { get; set; }
    public double? CloudCover { get; set; }
    /// <summary>mm in the last interval.</summary>
    public double? Precipitation { get; set; }
    public double? UvIndex { get; set; }
    public WindData? Wind { get; set; }
}

public sealed class HourlyForecast
{
    public DateTimeOffset Time { get; set; }
    public double Temperature { get; set; }
    public double? ApparentTemperature { get; set; }
    public WeatherCondition Condition { get; set; }
    public bool IsDay { get; set; } = true;
    public double? PrecipitationProbability { get; set; }
    /// <summary>mm (rain + showers + snow water).</summary>
    public double? Precipitation { get; set; }
    public double? Rain { get; set; }
    public double? Showers { get; set; }
    /// <summary>cm of fresh snow.</summary>
    public double? Snowfall { get; set; }
    public WindData? Wind { get; set; }
    public double? UvIndex { get; set; }
    public double? Visibility { get; set; }
    public double? Pressure { get; set; }
    public double? Humidity { get; set; }
    public double? DewPoint { get; set; }
    public double? CloudCover { get; set; }
    public double? CloudLow { get; set; }
    public double? CloudMid { get; set; }
    public double? CloudHigh { get; set; }
}

public sealed class DailyForecast
{
    /// <summary>Local midnight of the day at the place (offset of that place).</summary>
    public DateTimeOffset Date { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public WeatherCondition Condition { get; set; }
    public double? PrecipitationProbability { get; set; }
    public double? PrecipitationSum { get; set; }
    public double? RainSum { get; set; }
    public double? ShowersSum { get; set; }
    public double? SnowfallSum { get; set; }
    public double? WindMax { get; set; }
    public double? GustMax { get; set; }
    public double? WindDirection { get; set; }
    public double? UvMax { get; set; }
    public DateTimeOffset? Sunrise { get; set; }
    public DateTimeOffset? Sunset { get; set; }
    public double? DaylightSeconds { get; set; }
}

/// <summary>Short-term precipitation (Open-Meteo delivers 15-minute steps).</summary>
public sealed class MinutePrecipitation
{
    public DateTimeOffset Time { get; set; }
    /// <summary>mm within the step.</summary>
    public double Precipitation { get; set; }
    /// <summary>cm of snow within the step.</summary>
    public double Snowfall { get; set; }
}

public sealed class WeatherAlert
{
    public string Id { get; set; } = "";
    public AlertSeverity Severity { get; set; }
    public AlertKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Issuer { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public string Region { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class AirQuality
{
    /// <summary>European air quality index (0 = best).</summary>
    public double Index { get; set; }
    public double? Pm25 { get; set; }
    public double? Pm10 { get; set; }
    public double? Ozone { get; set; }
    public double? NitrogenDioxide { get; set; }
    /// <summary>Hourly index for the coming day (may be empty).</summary>
    public List<double> HourlyIndex { get; set; } = new();
}

/// <summary>Long-term values for the same time of year.</summary>
public sealed class ClimateNormals
{
    public double AverageHigh { get; set; }
    public double AverageLow { get; set; }
    /// <summary>Average precipitation per day in mm.</summary>
    public double AveragePrecipitation { get; set; }
    public int Years { get; set; }
    public DateTimeOffset Computed { get; set; }
}

/// <summary>Sun and moon data calculated locally for one day.</summary>
public sealed class AstronomyData
{
    public DateTimeOffset? Sunrise { get; init; }
    public DateTimeOffset? Sunset { get; init; }
    public DateTimeOffset? CivilDawn { get; init; }
    public DateTimeOffset? CivilDusk { get; init; }
    public DateTimeOffset? Moonrise { get; init; }
    public DateTimeOffset? Moonset { get; init; }
    /// <summary>0 = new moon, 0.25 = first quarter, 0.5 = full moon, 0.75 = last quarter.</summary>
    public double MoonPhase { get; init; }
    /// <summary>Illuminated fraction 0..1.</summary>
    public double MoonIllumination { get; init; }
    public double DaysToFullMoon { get; init; }
    public double DaysToNewMoon { get; init; }
}

/// <summary>One weather layer sampled on a grid for the map (values per hour and grid point).</summary>
public sealed class WeatherMapGrid
{
    public WeatherMapLayer Layer { get; set; }
    public double[] Latitudes { get; set; } = Array.Empty<double>();
    public double[] Longitudes { get; set; } = Array.Empty<double>();
    public DateTimeOffset[] Times { get; set; } = Array.Empty<DateTimeOffset>();
    /// <summary>[time][row * columns + column], NaN where no value exists.</summary>
    public float[][] Values { get; set; } = Array.Empty<float[]>();
    /// <summary>Wind direction per point (only for the wind layer).</summary>
    public float[][]? Directions { get; set; }

    public int Rows => Latitudes.Length;
    public int Columns => Longitudes.Length;
}

/// <summary>Everything the weather page needs for one place.</summary>
public sealed class WeatherSnapshot
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public WeatherLocation Location { get; set; } = new();
    public DateTimeOffset FetchedAt { get; set; }
    public string TimeZoneId { get; set; } = "";
    public CurrentWeather Current { get; set; } = new();
    public List<HourlyForecast> Hourly { get; set; } = new();
    public List<DailyForecast> Daily { get; set; } = new();
    /// <summary>Null when the source has no short-term precipitation for the region.</summary>
    public List<MinutePrecipitation>? Minutely { get; set; }
    public List<WeatherAlert> Alerts { get; set; } = new();
    /// <summary>False when no warning service covers the place.</summary>
    public bool AlertsAvailable { get; set; }
    public AirQuality? AirQuality { get; set; }
    public ClimateNormals? Normals { get; set; }
    public List<string> Attribution { get; set; } = new();
}

public enum TemperatureUnit { Celsius, Fahrenheit }
public enum WindUnit { KilometersPerHour, MetersPerSecond, MilesPerHour, Knots }
public enum PressureUnit { Hectopascal, MillimetersOfMercury, InchesOfMercury }
public enum PrecipitationUnit { Millimeters, Inches }
public enum DistanceUnit { Kilometers, Miles }

public sealed class WeatherSettings
{
    public TemperatureUnit Temperature { get; set; }
    public WindUnit Wind { get; set; }
    public PressureUnit Pressure { get; set; }
    public PrecipitationUnit Precipitation { get; set; }
    public DistanceUnit Distance { get; set; }
    /// <summary>Null = follow the UI language.</summary>
    public bool? Use24Hour { get; set; }
    public bool Animations { get; set; } = true;
    public bool ReducedMotion { get; set; }
    public bool AutoLocation { get; set; }
    public bool AutoRefresh { get; set; } = true;
    public int RefreshMinutes { get; set; } = 30;
    public bool NotifySevere { get; set; } = true;
    public bool NotifyPrecipitation { get; set; }
    public List<WeatherLocation> Locations { get; set; } = new();
    public string? SelectedLocationId { get; set; }
    public string? DefaultLocationId { get; set; }
    /// <summary>Alerts and rain starts already announced, so nothing is reported twice.</summary>
    public List<string> Announced { get; set; } = new();
}
