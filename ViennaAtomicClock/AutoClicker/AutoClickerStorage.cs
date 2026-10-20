using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViennaAtomicClock.AutoClicker;

/// <summary>Result of loading all profiles: the good ones plus the names of files that could not be read.</summary>
public sealed record ProfileLoadResult(List<AutoClickerProfile> Profiles, List<string> Damaged);

/// <summary>
/// Profiles, recordings and settings as versioned JSON in %AppData%\ViennaAtomicClock\AutoClicker\.
/// Every profile is its own file, so a damaged file never hides the others.
/// </summary>
public sealed class AutoClickerStorage
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _folder;

    public AutoClickerStorage(string? folder = null)
    {
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock", "AutoClicker");
    }

    public string Folder => _folder;
    private string ProfileFolder => Path.Combine(_folder, "profiles");
    private string SequenceFolder => Path.Combine(_folder, "sequences");

    public static string FileNameFor(string name)
    {
        var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '.' ? '_' : c).ToArray()).Trim();
        return (safe.Length == 0 ? "Profil" : safe) + ".json";
    }

    public AutoClickerSettings LoadSettings() => Read<AutoClickerSettings>(Path.Combine(_folder, "settings.json")) ?? new AutoClickerSettings();

    public void SaveSettings(AutoClickerSettings settings) => Write(Path.Combine(_folder, "settings.json"), settings);

    public ProfileLoadResult LoadProfiles()
    {
        var profiles = new List<AutoClickerProfile>();
        var damaged = new List<string>();
        if (Directory.Exists(ProfileFolder))
            foreach (var file in Directory.GetFiles(ProfileFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var profile = Read<AutoClickerProfile>(file);
                if (profile is null || !Normalize(profile)) damaged.Add(Path.GetFileName(file));
                else if (profiles.All(p => !p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))) profiles.Add(profile);
            }
        if (profiles.Count == 0) profiles.Add(new AutoClickerProfile());
        return new ProfileLoadResult(profiles, damaged);
    }

    public void SaveProfile(AutoClickerProfile profile)
    {
        profile.Version = AutoClickerProfile.CurrentVersion;
        Write(Path.Combine(ProfileFolder, FileNameFor(profile.Name)), profile);
    }

    public void DeleteProfile(string name)
    {
        try { File.Delete(Path.Combine(ProfileFolder, FileNameFor(name))); }
        catch { }
    }

    public void RenameProfile(AutoClickerProfile profile, string newName)
    {
        DeleteProfile(profile.Name);
        profile.Name = newName;
        SaveProfile(profile);
    }

    public static void Export(AutoClickerProfile profile, string path)
    {
        profile.Version = AutoClickerProfile.CurrentVersion;
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    /// <summary>Reads an exported profile; null if the file is not a valid profile.</summary>
    public static AutoClickerProfile? Import(string path)
    {
        var profile = Read<AutoClickerProfile>(path);
        return profile is not null && Normalize(profile) ? profile : null;
    }

    public List<ClickSequence> LoadSequences()
    {
        var list = new List<ClickSequence>();
        if (!Directory.Exists(SequenceFolder)) return list;
        foreach (var file in Directory.GetFiles(SequenceFolder, "*.json"))
            if (Read<ClickSequence>(file) is { Version: ClickSequence.CurrentVersion } sequence)
                list.Add(sequence);
        return list;
    }

    public void SaveSequence(ClickSequence sequence) => Write(Path.Combine(SequenceFolder, FileNameFor(sequence.Name)), sequence);

    /// <summary>Unique name like "Profil (2)".</summary>
    public static string UniqueName(string wanted, IEnumerable<string> existing)
    {
        var names = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        wanted = string.IsNullOrWhiteSpace(wanted) ? "Profil" : wanted.Trim();
        if (!names.Contains(wanted)) return wanted;
        for (var i = 2; ; i++)
            if (!names.Contains($"{wanted} ({i})")) return $"{wanted} ({i})";
    }

    /// <summary>Repairs missing parts and rejects profiles of an unknown (newer) version.</summary>
    public static bool Normalize(AutoClickerProfile profile)
    {
        if (profile.Version is < 1 or > AutoClickerProfile.CurrentVersion) return false;
        if (string.IsNullOrWhiteSpace(profile.Name)) return false;
        profile.Points ??= new List<ClickPoint>();
        profile.Hotkeys ??= AutoClickerProfile.DefaultHotkeys();
        foreach (var (action, key) in AutoClickerProfile.DefaultHotkeys())
            if (!profile.Hotkeys.ContainsKey(action)) profile.Hotkeys[action] = key;
        if (double.IsNaN(profile.IntervalMs) || profile.IntervalMs <= 0) profile.IntervalMs = 100;
        return true;
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, JsonOptions));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch
        {
            // A read-only profile folder must not break the clicker.
        }
    }
}
