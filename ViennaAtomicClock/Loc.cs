using System.Globalization;
using System.Windows;

namespace ViennaAtomicClock;

/// <summary>
/// UI languages. XAML uses {DynamicResource L.key}; code uses Loc.T("key") / Loc.F("key", …).
/// Switching the language rewrites the L.* resources, so bound texts update immediately.
/// </summary>
public static class Loc
{
    public static readonly (string Code, string Name, string Culture)[] Languages =
    {
        ("de", "Deutsch", "de-AT"), ("en", "English", "en-GB"), ("es", "Español", "es-ES"), ("pt", "Português", "pt-PT"),
        ("ru", "Русский", "ru-RU"), ("zh", "中文", "zh-CN"), ("ja", "日本語", "ja-JP")
    };

    private static int _index;

    public static string Language => Languages[_index].Code;
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("de-AT");
    public static event Action? Changed;

    public static void Apply(string? code)
    {
        _index = Math.Max(0, Array.FindIndex(Languages, l => l.Code == code));
        Culture = CultureInfo.GetCultureInfo(Languages[_index].Culture);
        if (Application.Current is { } app)
            foreach (var (key, texts) in Strings.Table)
                app.Resources["L." + key] = texts[_index];
        Changed?.Invoke();
    }

    public static string T(string key) => Strings.Table.TryGetValue(key, out var texts) ? texts[_index] : key;

    public static string F(string key, params object[] args) => string.Format(Culture, T(key), args);

    /// <summary>Translates a text that was produced earlier in another language (for status texts set before a switch).</summary>
    public static string Retranslate(string text, params string[] keys)
    {
        foreach (var key in keys)
            if (Strings.Table.TryGetValue(key, out var texts) && texts.Contains(text))
                return texts[_index];
        return text;
    }

    /// <summary>Language name for the AI system prompt, e.g. "Englisch".</summary>
    public static string PromptLanguage => T("lang.prompt");
}
