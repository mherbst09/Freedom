using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViennaAtomicClock.PunchFactory;

/// <summary>Career and lifetime progress (stored as JSON with a checksum).</summary>
public sealed class PunchSave
{
    public int Version { get; set; } = 1;
    public BigNum Punch { get; set; }
    /// <summary>Punch power earned in the current career (prestige requirement).</summary>
    public BigNum EarnedRun { get; set; }
    /// <summary>Punch power earned over all careers (fame, environments).</summary>
    public BigNum EarnedAll { get; set; }
    public BigNum BestPps { get; set; }
    public long Clicks { get; set; }
    public long Crits { get; set; }
    public long FuryCount { get; set; }
    public long AbilitiesUsed { get; set; }
    public long EventsCaught { get; set; }
    public long UpgradesBought { get; set; }
    public long Prestiges { get; set; }
    public int MaxCombo { get; set; }
    public double PlaySeconds { get; set; }
    public double FameEarned { get; set; }
    public double FameSpent { get; set; }
    public Dictionary<string, int> Producers { get; set; } = new();
    public Dictionary<string, int> Upgrades { get; set; } = new();
    public Dictionary<string, int> PrestigeLevels { get; set; } = new();
    public Dictionary<string, double> Cooldowns { get; set; } = new();
    public HashSet<string> Achievements { get; set; } = new();
    public HashSet<string> Cosmetics { get; set; } = new(PunchContent.DefaultCosmetics);
    public Dictionary<CosmeticSlot, string> Equipped { get; set; } = PunchContent.Cosmetics.GroupBy(c => c.Slot).ToDictionary(g => g.Key, g => g.First().Id);
    public string Environment { get; set; } = "garage";
    public int HighestEnvironment { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSaveUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public double FameAvailable => Math.Max(0, FameEarned - FameSpent);

    public int Count(string producer) => Producers.TryGetValue(producer, out var n) ? n : 0;
    public int Level(string upgrade) => Upgrades.TryGetValue(upgrade, out var n) ? n : 0;
    public int PrestigeLevel(string upgrade) => PrestigeLevels.TryGetValue(upgrade, out var n) ? n : 0;

    /// <summary>Repairs impossible values from an edited or damaged file.</summary>
    public PunchSave Normalize()
    {
        static BigNum NonNegative(BigNum value) => value.IsNegative ? BigNum.Zero : value;
        Punch = NonNegative(Punch);
        EarnedRun = NonNegative(EarnedRun);
        EarnedAll = BigNum.Max(NonNegative(EarnedAll), EarnedRun);
        BestPps = NonNegative(BestPps);
        Clicks = Math.Max(0, Clicks);
        Crits = Math.Max(0, Crits);
        FuryCount = Math.Max(0, FuryCount);
        AbilitiesUsed = Math.Max(0, AbilitiesUsed);
        EventsCaught = Math.Max(0, EventsCaught);
        UpgradesBought = Math.Max(0, UpgradesBought);
        Prestiges = Math.Max(0, Prestiges);
        MaxCombo = Math.Max(0, MaxCombo);
        PlaySeconds = double.IsFinite(PlaySeconds) ? Math.Max(0, PlaySeconds) : 0;
        FameEarned = double.IsFinite(FameEarned) ? Math.Max(0, FameEarned) : 0;
        FameSpent = double.IsFinite(FameSpent) ? Math.Clamp(FameSpent, 0, FameEarned) : 0;
        Producers = (Producers ?? new()).Where(p => p.Value > 0).ToDictionary(p => p.Key, p => Math.Min(p.Value, 1_000_000));
        Upgrades = (Upgrades ?? new()).Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value);
        PrestigeLevels = (PrestigeLevels ?? new()).Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value);
        Cooldowns = (Cooldowns ?? new()).Where(p => double.IsFinite(p.Value) && p.Value > 0).ToDictionary(p => p.Key, p => Math.Min(p.Value, 3600));
        Achievements ??= new();
        Cosmetics ??= new();
        foreach (var id in PunchContent.DefaultCosmetics) Cosmetics.Add(id);
        Equipped ??= new();
        foreach (var slot in Enum.GetValues<CosmeticSlot>())
            if (!Equipped.TryGetValue(slot, out var id) || !Cosmetics.Contains(id) || PunchContent.Cosmetic(id).Slot != slot)
                Equipped[slot] = PunchContent.Cosmetics.First(c => c.Slot == slot).Id;
        if (string.IsNullOrEmpty(Environment)) Environment = "garage";
        HighestEnvironment = Math.Clamp(HighestEnvironment, 0, 64);
        return this;
    }
}

/// <summary>Settings, stored apart from the progress.</summary>
public sealed class PunchSettings
{
    public double MasterVolume { get; set; } = 0.8;
    public double MusicVolume { get; set; } = 0.35;
    public double EffectsVolume { get; set; } = 0.8;
    public bool ScreenShake { get; set; } = true;
    public bool Flashes { get; set; } = true;
    public bool FloatingNumbers { get; set; } = true;
    public bool Vibration { get; set; } = true;
    public bool ReducedMotion { get; set; }

    public PunchSettings Normalize()
    {
        static double V(double v, double fallback) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : fallback;
        MasterVolume = V(MasterVolume, 0.8);
        MusicVolume = V(MusicVolume, 0.35);
        EffectsVolume = V(EffectsVolume, 0.8);
        return this;
    }
}

/// <summary>
/// Local save files: progress with SHA-256 checksum, a backup of the last good save, settings separately, and
/// export/import as a text code. A damaged or edited file never crashes the game – the backup or a new career is used.
/// </summary>
public sealed class PunchStorage
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };
    private const string ExportPrefix = "PUNCHFACTORY1:";

    public PunchStorage(string? folder = null)
    {
        Folder = folder ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock");
    }

    public string Folder { get; }
    public string SavePath => Path.Combine(Folder, "punchfactory-save.json");
    public string BackupPath => SavePath + ".bak";
    public string SettingsPath => Path.Combine(Folder, "punchfactory-settings.json");

    /// <summary>How the last load went (for a message in the game).</summary>
    public string LoadNote { get; private set; } = "";

    public void ClearLoadNote() => LoadNote = "";

    public PunchSave Load()
    {
        LoadNote = "";
        if (TryRead(SavePath, out var save)) return save;
        if (File.Exists(SavePath) && TryRead(BackupPath, out var backup))
        {
            LoadNote = "pf.save.restored";
            return backup;
        }
        if (File.Exists(SavePath)) LoadNote = "pf.save.damaged";
        return new PunchSave().Normalize();
    }

    private static bool TryRead(string path, out PunchSave save)
    {
        save = new PunchSave();
        try
        {
            if (!File.Exists(path)) return false;
            return TryDecode(File.ReadAllText(path), out save);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Save(PunchSave save)
    {
        save.LastSaveUtc = DateTime.UtcNow;
        var text = Encode(save);
        try
        {
            Directory.CreateDirectory(Folder);
            // Keep the previous good file as backup, then replace atomically.
            if (File.Exists(SavePath) && TryRead(SavePath, out _)) File.Copy(SavePath, BackupPath, true);
            File.WriteAllText(SavePath + ".tmp", text);
            File.Move(SavePath + ".tmp", SavePath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Saving is retried at the next autosave.
        }
    }

    public PunchSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath)) return (JsonSerializer.Deserialize<PunchSettings>(File.ReadAllText(SettingsPath), Json) ?? new PunchSettings()).Normalize();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // Defaults below.
        }
        return new PunchSettings();
    }

    public void SaveSettings(PunchSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not critical.
        }
    }

    /// <summary>Deletes progress and backup (after the double confirmation in the game).</summary>
    public void DeleteProgress()
    {
        foreach (var path in new[] { SavePath, BackupPath, SavePath + ".tmp" })
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Overwritten by the next save anyway.
            }
        }
    }

    /// <summary>{"checksum":…, "data":"…"} – the checksum covers the exact data text.</summary>
    public static string Encode(PunchSave save)
    {
        var data = JsonSerializer.Serialize(save, Json);
        return JsonSerializer.Serialize(new Envelope { Checksum = Hash(data), Data = data }, Json);
    }

    public static bool TryDecode(string text, out PunchSave save)
    {
        save = new PunchSave();
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(text, Json);
            if (envelope?.Data is null || envelope.Checksum != Hash(envelope.Data)) return false;
            var loaded = JsonSerializer.Deserialize<PunchSave>(envelope.Data, Json);
            if (loaded is null || loaded.Version is < 1 or > 1) return false;
            save = loaded.Normalize();
            return true;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Export code: prefix + Base64 of the checksummed file.</summary>
    public static string Export(PunchSave save) => ExportPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(Encode(save)));

    public static bool TryImport(string code, out PunchSave save)
    {
        save = new PunchSave();
        code = code.Trim();
        if (!code.StartsWith(ExportPrefix, StringComparison.Ordinal) || code.Length > 5_000_000) return false;
        try
        {
            return TryDecode(Encoding.UTF8.GetString(Convert.FromBase64String(code[ExportPrefix.Length..])), out save);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Hash(string data) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("PunchFactory|" + data)));

    private sealed class Envelope
    {
        public string? Checksum { get; set; }
        public string? Data { get; set; }
    }
}
