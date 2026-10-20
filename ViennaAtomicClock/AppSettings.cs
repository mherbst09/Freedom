using System.Text.Json;
using System.IO;

namespace ViennaAtomicClock;

internal sealed class AppSettings
{
    public bool DarkMode { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public int CaffeineDailyLimitMg { get; set; } = 400;
    public string ActivePage { get; set; } = "clock";
    /// <summary>Group for split lessons (E1, ITP2, INSY, SEW): 0 = show both, 1 or 2.</summary>
    public int TimetableGroup { get; set; }
    /// <summary>Look of the app: "war", "hacker" or "anime".</summary>
    public string Theme { get; set; } = "war";
    /// <summary>Coloured mode: palette generated from <see cref="CustomColor"/> (#AARRGGBB).</summary>
    public bool ColorMode { get; set; }
    public string CustomColor { get; set; } = "#FF3D7BFF";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ViennaAtomicClock",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A read-only profile should never prevent the clock from running.
        }
    }
}
