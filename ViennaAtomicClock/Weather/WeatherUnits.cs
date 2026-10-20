using System.Globalization;

namespace ViennaAtomicClock.Weather;

/// <summary>All unit conversions and number formats of the weather page in one place. Stored values are metric.</summary>
public static class WeatherUnits
{
    public static double Temperature(double celsius, TemperatureUnit unit) =>
        unit == TemperatureUnit.Fahrenheit ? celsius * 9 / 5 + 32 : celsius;

    public static double Wind(double kmh, WindUnit unit) => unit switch
    {
        WindUnit.MetersPerSecond => kmh / 3.6,
        WindUnit.MilesPerHour => kmh / 1.609344,
        WindUnit.Knots => kmh / 1.852,
        _ => kmh
    };

    public static double Pressure(double hectopascal, PressureUnit unit) => unit switch
    {
        PressureUnit.MillimetersOfMercury => hectopascal * 0.750061683,
        PressureUnit.InchesOfMercury => hectopascal * 0.0295299831,
        _ => hectopascal
    };

    public static double Precipitation(double millimeters, PrecipitationUnit unit) =>
        unit == PrecipitationUnit.Inches ? millimeters / 25.4 : millimeters;

    public static double Distance(double meters, DistanceUnit unit) =>
        unit == DistanceUnit.Miles ? meters / 1609.344 : meters / 1000;

    public static string TemperatureSymbol(TemperatureUnit unit) => unit == TemperatureUnit.Fahrenheit ? "°F" : "°C";

    public static string WindSymbol(WindUnit unit) => unit switch
    {
        WindUnit.MetersPerSecond => "m/s",
        WindUnit.MilesPerHour => "mph",
        WindUnit.Knots => "kn",
        _ => "km/h"
    };

    public static string PressureSymbol(PressureUnit unit) => unit switch
    {
        PressureUnit.MillimetersOfMercury => "mmHg",
        PressureUnit.InchesOfMercury => "inHg",
        _ => "hPa"
    };

    public static string PrecipitationSymbol(PrecipitationUnit unit) => unit == PrecipitationUnit.Inches ? "in" : "mm";
    public static string DistanceSymbol(DistanceUnit unit) => unit == DistanceUnit.Miles ? "mi" : "km";

    /// <summary>Whole degrees without a misleading "-0".</summary>
    public static int RoundDegrees(double value)
    {
        var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }
}

/// <summary>Formats metric values for display with the user's units and the UI culture.</summary>
public sealed class WeatherFormatter
{
    public WeatherFormatter(WeatherSettings settings, CultureInfo culture)
    {
        Settings = settings;
        Culture = culture;
    }

    public WeatherSettings Settings { get; }
    public CultureInfo Culture { get; }

    public bool Use24Hour => Settings.Use24Hour ?? Culture.DateTimeFormat.ShortTimePattern.Contains('H');

    /// <summary>"18°".</summary>
    public string Degrees(double celsius) =>
        WeatherUnits.RoundDegrees(WeatherUnits.Temperature(celsius, Settings.Temperature)).ToString(Culture) + "°";

    /// <summary>"18 °C".</summary>
    public string Temperature(double celsius) =>
        WeatherUnits.RoundDegrees(WeatherUnits.Temperature(celsius, Settings.Temperature)).ToString(Culture) + " " + WeatherUnits.TemperatureSymbol(Settings.Temperature);

    /// <summary>Temperature difference (no offset for Fahrenheit).</summary>
    public string Difference(double celsius)
    {
        var value = Settings.Temperature == TemperatureUnit.Fahrenheit ? celsius * 9 / 5 : celsius;
        return Math.Abs(value).ToString("0", Culture) + " " + WeatherUnits.TemperatureSymbol(Settings.Temperature);
    }

    public string Wind(double kmh) =>
        WeatherUnits.Wind(kmh, Settings.Wind).ToString("0", Culture) + " " + WeatherUnits.WindSymbol(Settings.Wind);

    public string Pressure(double hectopascal) => Settings.Pressure switch
    {
        PressureUnit.InchesOfMercury => WeatherUnits.Pressure(hectopascal, Settings.Pressure).ToString("0.00", Culture),
        _ => WeatherUnits.Pressure(hectopascal, Settings.Pressure).ToString("0", Culture)
    } + " " + WeatherUnits.PressureSymbol(Settings.Pressure);

    public string Precipitation(double millimeters)
    {
        var value = WeatherUnits.Precipitation(millimeters, Settings.Precipitation);
        var format = Settings.Precipitation == PrecipitationUnit.Inches ? "0.00" : value < 10 ? "0.#" : "0";
        return value.ToString(format, Culture) + " " + WeatherUnits.PrecipitationSymbol(Settings.Precipitation);
    }

    /// <summary>Snow in cm (or inches).</summary>
    public string Snow(double centimeters) => Settings.Precipitation == PrecipitationUnit.Inches
        ? (centimeters / 2.54).ToString("0.0", Culture) + " in"
        : centimeters.ToString(centimeters < 10 ? "0.#" : "0", Culture) + " cm";

    public string Distance(double meters)
    {
        var value = WeatherUnits.Distance(meters, Settings.Distance);
        return value.ToString(value < 10 ? "0.#" : "0", Culture) + " " + WeatherUnits.DistanceSymbol(Settings.Distance);
    }

    public string Percent(double value) => Math.Round(value).ToString("0", Culture) + " %";

    public string Time(DateTimeOffset time) => time.ToString(Use24Hour ? "HH:mm" : "h:mm tt", Culture);

    /// <summary>Hour label for the hourly strip ("14" or "2 PM").</summary>
    public string Hour(DateTimeOffset time) => time.ToString(Use24Hour ? "HH" : "h tt", Culture).Trim();

    public string Weekday(DateTimeOffset date) => Culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek);

    public string ShortDate(DateTimeOffset date) => date.ToString(Culture.DateTimeFormat.MonthDayPattern, Culture);

    public string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes:00} min" : $"{Math.Max(0, span.Minutes)} min";
}
