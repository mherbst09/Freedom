using System.Reflection;

namespace ViennaAtomicClock;

/// <summary>Build metadata embedded by ViennaAtomicClock.csproj and shown in the About window.</summary>
internal static class BuildInfo
{
    private static readonly Assembly AppAssembly = typeof(BuildInfo).Assembly;
    private static readonly IReadOnlyDictionary<string, string> Metadata = AppAssembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .GroupBy(attribute => attribute.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last().Value ?? "", StringComparer.OrdinalIgnoreCase);

    public static string SemanticVersion
    {
        get
        {
            var informational = AppAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0];
            var version = AppAssembly.GetName().Version;
            return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static string GitCommit
    {
        get
        {
            var explicitCommit = Value("GitCommit");
            if (explicitCommit != "unknown") return explicitCommit;

            var informational = AppAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var separator = informational?.IndexOf('+') ?? -1;
            if (separator < 0 || informational is null || separator == informational.Length - 1) return "unknown";
            var candidate = informational[(separator + 1)..].Split('.')[0];
            return candidate.Length is >= 7 and <= 40 && candidate.All(Uri.IsHexDigit) ? candidate : "unknown";
        }
    }

    public static DateTimeOffset? BuildDateUtc =>
        DateTimeOffset.TryParse(Value("BuildDateUtc"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToUniversalTime()
            : null;

    private static string Value(string key) => Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : "unknown";
}
