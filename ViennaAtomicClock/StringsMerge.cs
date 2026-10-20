namespace ViennaAtomicClock;

internal static partial class Strings
{
    /// <summary>Feature modules keep their texts in their own tables; they join the main table here.</summary>
    static Strings()
    {
        foreach (var (key, texts) in Weather.WeatherStrings.Table) Table[key] = texts;
        foreach (var (key, texts) in AutoClicker.AutoClickerStrings.Table) Table[key] = texts;
        foreach (var (key, texts) in GlbViewer.GlbStrings.Table) Table[key] = texts;
    }
}
