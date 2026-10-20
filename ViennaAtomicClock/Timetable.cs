using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ViennaAtomicClock;

internal enum EntryStatus { Regular, Changed, Cancelled, Exam }

/// <summary>One lesson of the real class timetable. Group is 1 or 2 for split classes, 0 for the whole class.</summary>
internal sealed record TimetableEntry(
    DateTime Date, TimeSpan Start, TimeSpan End, string Subject, string SubjectLong, string Room, EntryStatus Status, string Info,
    int Group = 0);

internal sealed class TimetableData
{
    public string ClassName { get; set; } = WebUntisTimetable.DefaultClassName;
    public DateTime FetchedAt { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public List<TimetableEntry> Entries { get; set; } = new();

    public bool Covers(DateTime date) => date.Date >= From && date.Date <= To;

    /// <summary>Lessons of one day for the chosen group (0 = show both groups), parallel groups merged.</summary>
    public List<TimetableEntry> On(DateTime date, int group) =>
        Entries
            .Where(e => e.Date == date.Date && (group == 0 || e.Group == 0 || e.Group == group))
            .GroupBy(e => (e.Start, e.End, e.Subject))
            .Select(Merge)
            .OrderBy(e => e.Start)
            .ToList();

    private static TimetableEntry Merge(IEnumerable<TimetableEntry> parallel)
    {
        var items = parallel.ToList();
        var status = items.All(i => i.Status == EntryStatus.Cancelled) ? EntryStatus.Cancelled
            : items.Any(i => i.Status == EntryStatus.Exam) ? EntryStatus.Exam
            : items.Any(i => i.Status is EntryStatus.Changed or EntryStatus.Cancelled) ? EntryStatus.Changed
            : EntryStatus.Regular;
        var rooms = items.Where(i => i.Status != EntryStatus.Cancelled || status == EntryStatus.Cancelled)
            .SelectMany(i => i.Room.Split(" / ", StringSplitOptions.RemoveEmptyEntries)).Distinct();
        var infos = items.Select(i => i.Info).Where(i => i.Length > 0).Distinct();
        if (status == EntryStatus.Changed && items.Any(i => i.Status == EntryStatus.Cancelled))
            infos = infos.Append("teilweise Entfall");
        var first = items[0];
        return first with { Room = string.Join(" / ", rooms), Status = status, Info = string.Join(" · ", infos), Group = 0 };
    }
}

internal sealed record TimetableDepartment(int Id, string Name);

internal sealed record TimetableClass(int Id, string Name, string LongName, List<int> DepartmentIds);

internal sealed class TimetableCatalog
{
    public DateTime FetchedAt { get; set; }
    public List<TimetableDepartment> Departments { get; set; } = new();
    public List<TimetableClass> Classes { get; set; } = new();
}

/// <summary>
/// Loads the class timetable from the school's public WebUntis (no login needed) and keeps
/// a copy on disk so the app still knows the week when it starts without network.
/// </summary>
internal static class WebUntisTimetable
{
    public const string DefaultClassName = "4BHITS";
    private const string School = "htl-salzburg";
    private const string BaseUrl = "https://htl-salzburg.webuntis.com/WebUntis";

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock", "timetable.json");
    private static readonly string CatalogCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock", "timetable-catalog.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static TimetableData? LoadCache(string className = DefaultClassName)
    {
        try
        {
            var data = File.Exists(CachePath)
                ? JsonSerializer.Deserialize<TimetableData>(File.ReadAllText(CachePath), JsonOptions)
                : null;
            return data is not null && string.Equals(data.ClassName, className, StringComparison.OrdinalIgnoreCase) ? data : null;
        }
        catch
        {
            return null;
        }
    }

    public static TimetableCatalog? LoadCatalogCache()
    {
        try
        {
            return File.Exists(CatalogCachePath)
                ? JsonSerializer.Deserialize<TimetableCatalog>(File.ReadAllText(CatalogCachePath), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(TimetableData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch
        {
            // A missing cache only costs the offline fallback.
        }
    }

    private static void SaveCatalogCache(TimetableCatalog catalog)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogCachePath)!);
            File.WriteAllText(CatalogCachePath, JsonSerializer.Serialize(catalog, JsonOptions));
        }
        catch
        {
            // The picker can still use the freshly loaded catalog in memory.
        }
    }

    /// <summary>Fetches this week and next week for the class.</summary>
    public static Task<TimetableData> FetchAsync(DateTime today, CancellationToken cancellation) =>
        FetchAsync(today, DefaultClassName, cancellation);

    /// <summary>Fetches this week and next week for the selected class.</summary>
    public static async Task<TimetableData> FetchAsync(DateTime today, string className, CancellationToken cancellation)
    {
        using var client = await OpenClientAsync(cancellation);

        var monday = today.Date.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var catalog = await LoadCatalogAsync(client, monday, cancellation);
        var selectedClass = catalog.Classes.FirstOrDefault(item =>
            string.Equals(item.Name, className, StringComparison.OrdinalIgnoreCase));
        if (selectedClass is null)
            throw new InvalidOperationException($"Klasse {className} nicht gefunden.");

        var entries = new List<TimetableEntry>();
        for (var week = 0; week < 2; week++)
        {
            var date = monday.AddDays(7 * week);
            var json = await client.GetStringAsync(
                $"{BaseUrl}/api/public/timetable/weekly/data?elementType=1&elementId={selectedClass.Id}&date={date:yyyy-MM-dd}&formatId=1", cancellation);
            entries.AddRange(Parse(json, selectedClass.Id));
        }

        var data = new TimetableData
        {
            ClassName = selectedClass.Name,
            FetchedAt = DateTime.Now,
            From = monday,
            To = monday.AddDays(13),
            Entries = entries
        };
        SaveCache(data);
        return data;
    }

    /// <summary>Loads the departments and classes shown by WebUntis' public picker.</summary>
    public static async Task<TimetableCatalog> LoadCatalogAsync(DateTime date, CancellationToken cancellation)
    {
        using var client = await OpenClientAsync(cancellation);
        return await LoadCatalogAsync(client, date, cancellation);
    }

    private static async Task<HttpClient> OpenClientAsync(CancellationToken cancellation)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Freedom-Schuluhr/1.1");
        try
        {
            // The start page hands out the school and tenant cookies the public API needs.
            (await client.GetAsync($"{BaseUrl}/?school={School}", cancellation)).EnsureSuccessStatusCode();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<TimetableCatalog> LoadCatalogAsync(HttpClient client, DateTime date, CancellationToken cancellation)
    {
        var json = await client.GetStringAsync($"{BaseUrl}/api/public/timetable/weekly/pageconfig?type=1&date={date:yyyy-MM-dd}", cancellation);
        var catalog = ParseCatalog(json);
        SaveCatalogCache(catalog);
        return catalog;
    }

    internal static TimetableCatalog ParseCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        var catalog = new TimetableCatalog { FetchedAt = DateTime.Now };

        foreach (var element in data.GetProperty("elements").EnumerateArray())
        {
            if (element.TryGetProperty("type", out var type) && type.GetInt32() != 1) continue;
            var departments = element.TryGetProperty("dids", out var dids) && dids.ValueKind == JsonValueKind.Array
                ? dids.EnumerateArray().Select(value => value.GetInt32()).Distinct().ToList()
                : new List<int>();
            catalog.Classes.Add(new TimetableClass(
                element.GetProperty("id").GetInt32(),
                element.GetProperty("name").GetString() ?? "",
                element.TryGetProperty("longName", out var longName) ? longName.GetString() ?? "" : "",
                departments));
        }

        if (data.TryGetProperty("filters", out var filters) && filters.ValueKind == JsonValueKind.Array)
        {
            foreach (var filter in filters.EnumerateArray())
            {
                var isDepartment = filter.TryGetProperty("type", out var type) && type.GetInt32() == 10;
                isDepartment |= filter.TryGetProperty("property", out var property)
                    && string.Equals(property.GetString(), "departmentId", StringComparison.OrdinalIgnoreCase);
                if (!isDepartment || !filter.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var element in elements.EnumerateArray())
                {
                    var name = element.TryGetProperty("name", out var shortName) ? shortName.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) && element.TryGetProperty("label", out var label)) name = label.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        catalog.Departments.Add(new TimetableDepartment(element.GetProperty("id").GetInt32(), name));
                }
            }
        }

        catalog.Classes = catalog.Classes
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .DistinctBy(item => item.Id)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        catalog.Departments = catalog.Departments
            .DistinctBy(item => item.Id)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return catalog;
    }

    private static IEnumerable<TimetableEntry> Parse(string json, int classId)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data").GetProperty("result").GetProperty("data");

        var names = new Dictionary<(int Type, int Id), (string Name, string LongName)>();
        foreach (var element in data.GetProperty("elements").EnumerateArray())
            names[(element.GetProperty("type").GetInt32(), element.GetProperty("id").GetInt32())] =
                (element.GetProperty("name").GetString() ?? "", element.TryGetProperty("longName", out var ln) ? ln.GetString() ?? "" : "");

        if (!data.GetProperty("elementPeriods").TryGetProperty(classId.ToString(CultureInfo.InvariantCulture), out var periods))
            return Array.Empty<TimetableEntry>();

        var raw = new List<TimetableEntry>();
        foreach (var period in periods.EnumerateArray())
        {
            var subjects = new List<(string Name, string LongName)>();
            var rooms = new List<string>();
            var notes = new List<string>();

            foreach (var element in period.GetProperty("elements").EnumerateArray())
            {
                var type = element.GetProperty("type").GetInt32();
                var key = (type, element.GetProperty("id").GetInt32());
                if (!names.TryGetValue(key, out var name)) continue;
                if (type == 3) subjects.Add(name);
                if (type == 4)
                {
                    rooms.Add(name.Name);
                    var original = element.TryGetProperty("orgId", out var org) ? org.GetInt32() : 0;
                    if (original != 0 && names.TryGetValue((4, original), out var originalRoom) && originalRoom.Name != name.Name)
                        notes.Add($"Raum statt {originalRoom.Name}");
                }
            }

            if (subjects.Count == 0 || subjects.Any(s => IsOtherReligion(s.Name, s.LongName))) continue;

            foreach (var field in new[] { "substText", "lessonText", "periodText" })
                if (period.TryGetProperty(field, out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
                    notes.Add(text.GetString()!.Trim());

            var cellState = period.TryGetProperty("cellState", out var cs) ? cs.GetString() ?? "" : "";
            var flags = period.TryGetProperty("is", out var isFlags) ? isFlags : default;
            bool Flag(string name) => flags.ValueKind == JsonValueKind.Object && flags.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.True;

            var status = cellState is "CANCEL" or "FREE" || Flag("cancelled") ? EntryStatus.Cancelled
                : cellState == "EXAM" || Flag("exam") ? EntryStatus.Exam
                : cellState == "STANDARD" ? EntryStatus.Regular
                : EntryStatus.Changed;

            var studentGroup = period.TryGetProperty("studentGroup", out var sg) ? sg.GetString() ?? "" : "";
            var group = studentGroup.EndsWith("_1") ? 1 : studentGroup.EndsWith("_2") ? 2 : 0;

            raw.Add(new TimetableEntry(
                ParseDate(period.GetProperty("date").GetInt32()),
                ParseTime(period.GetProperty("startTime").GetInt32()),
                ParseTime(period.GetProperty("endTime").GetInt32()),
                string.Join(" / ", subjects.Select(s => s.Name).Distinct()),
                string.Join(" / ", subjects.Select(s => s.LongName).Distinct()),
                string.Join(" / ", rooms.Distinct()),
                status,
                string.Join(" · ", notes.Distinct()),
                group));
        }

        return raw;
    }

    /// <summary>Michi attends Catholic religion, so ethics and every other religion class are hidden.</summary>
    private static bool IsOtherReligion(string name, string longName) =>
        !name.Equals("RK", StringComparison.OrdinalIgnoreCase) &&
        (name.Equals("ETH", StringComparison.OrdinalIgnoreCase)
         || longName.StartsWith("Ethik", StringComparison.OrdinalIgnoreCase)
         || longName.StartsWith("Religion", StringComparison.OrdinalIgnoreCase));

    private static DateTime ParseDate(int value) => new(value / 10000, value / 100 % 100, value % 100);

    private static TimeSpan ParseTime(int value) => new(value / 100, value % 100, 0);
}
