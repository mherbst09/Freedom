namespace ViennaAtomicClock.Weather;

/// <summary>Weather settings and saved places in %AppData%\ViennaAtomicClock\weather.json.</summary>
internal static class WeatherStore
{
    public const string FileName = "weather.json";
    public const string CurrentLocationId = "current";

    public static WeatherSettings Load()
    {
        var settings = DataFile.Load<WeatherSettings>(FileName);
        Normalize(settings);
        return settings;
    }

    public static void Save(WeatherSettings settings) => DataFile.Save(FileName, settings);

    /// <summary>Repairs values a hand-edited or old file might contain.</summary>
    public static void Normalize(WeatherSettings settings)
    {
        settings.RefreshMinutes = settings.RefreshMinutes is 15 or 30 or 60 ? settings.RefreshMinutes : 30;
        settings.Locations ??= new List<WeatherLocation>();
        settings.Locations = settings.Locations
            .Where(l => l is not null && l.Name.Length > 0 && l.Latitude is >= -90 and <= 90 && l.Longitude is >= -180 and <= 180)
            .GroupBy(l => l.Id).Select(g => g.First()).ToList();
        if (settings.Locations.Count == 0) settings.Locations.Add(DefaultPlace());
        settings.Announced ??= new List<string>();
        if (settings.Announced.Count > 200) settings.Announced = settings.Announced.TakeLast(200).ToList();
    }

    public static WeatherLocation DefaultPlace() => new()
    {
        Id = "geo2766824",
        Name = "Salzburg",
        Region = "Salzburg",
        Country = "Österreich",
        CountryCode = "AT",
        Latitude = 47.79941,
        Longitude = 13.04399,
        TimeZoneId = "Europe/Vienna"
    };

    /// <summary>Display order: current position first, then favourites, then the rest – each group in the user's order.</summary>
    public static List<WeatherLocation> Ordered(IEnumerable<WeatherLocation> locations) =>
        locations.Select((l, i) => (l, i))
            .OrderBy(x => x.l.IsCurrentLocation ? 0 : x.l.IsFavorite ? 1 : 2)
            .ThenBy(x => x.i)
            .Select(x => x.l)
            .ToList();

    /// <summary>Moves a place one step up or down within the saved list.</summary>
    public static bool Move(List<WeatherLocation> locations, string id, int direction)
    {
        var ordered = Ordered(locations);
        var index = ordered.FindIndex(l => l.Id == id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= ordered.Count) return false;
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        locations.Clear();
        locations.AddRange(ordered);
        return true;
    }

    /// <summary>Adds a place (or returns the saved one at nearly the same position).</summary>
    public static WeatherLocation Add(WeatherSettings settings, WeatherLocation place)
    {
        var existing = settings.Locations.FirstOrDefault(l => l.Id == place.Id
            || (!l.IsCurrentLocation && Math.Abs(l.Latitude - place.Latitude) < 0.01 && Math.Abs(l.Longitude - place.Longitude) < 0.01));
        if (existing is not null) return existing;
        settings.Locations.Add(place);
        return place;
    }

    public static void Remove(WeatherSettings settings, string id)
    {
        settings.Locations.RemoveAll(l => l.Id == id);
        if (settings.Locations.Count == 0) settings.Locations.Add(DefaultPlace());
        if (settings.SelectedLocationId == id) settings.SelectedLocationId = null;
        if (settings.DefaultLocationId == id) settings.DefaultLocationId = null;
    }

    public static WeatherLocation Selected(WeatherSettings settings) =>
        settings.Locations.FirstOrDefault(l => l.Id == settings.SelectedLocationId)
        ?? settings.Locations.FirstOrDefault(l => l.Id == settings.DefaultLocationId)
        ?? Ordered(settings.Locations)[0];
}
