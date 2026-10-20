using System.IO;
using System.Reflection;
using System.Text.Json;

namespace ViennaAtomicClock.GlbViewer;

/// <summary>A validated message from the viewer page.</summary>
public sealed record ViewerMessage(string Type, JsonElement Data);

/// <summary>
/// Structured JSON messages between WPF and the Three.js page. Every incoming message is checked against a
/// whitelist of types and the fields each type needs; anything else is dropped.
/// </summary>
public static class ViewerProtocol
{
    /// <summary>Screenshots of large viewports arrive as base64 PNG; everything else is small.</summary>
    public const int MaxMessageLength = 96 * 1024 * 1024;

    private static readonly Dictionary<string, Func<JsonElement, bool>> Incoming = new()
    {
        ["ready"] = _ => true,
        ["progress"] = d => Number(d, "loaded") && Number(d, "total"),
        ["loaded"] = d => Object(d, "info") && Object(d, "tree") && Array(d, "animations") && Array(d, "warnings"),
        ["loadFailed"] = d => Text(d, "message"),
        ["selection"] = d => d.TryGetProperty("id", out var id) && (id.ValueKind == JsonValueKind.Null || id.ValueKind == JsonValueKind.Number),
        ["stats"] = d => Number(d, "fps"),
        ["screenshot"] = d => Text(d, "data") && Text(d, "target") && d.GetProperty("target").GetString() is "file" or "clipboard",
        ["key"] = d => Text(d, "key") && d.GetProperty("key").GetString() is "ctrl+o" or "f11" or "escape",
        ["animation"] = d => Number(d, "time") && Number(d, "duration") && Bool(d, "playing"),
        ["state"] = d => Bool(d, "grid") && Bool(d, "axes") && Bool(d, "autoRotate") && Text(d, "viewMode"),
        ["warning"] = d => Text(d, "message"),
        ["drop"] = d => true
    };

    /// <summary>Commands the page accepts (it validates them again on its side).</summary>
    public static readonly IReadOnlySet<string> Outgoing = new HashSet<string>
    {
        "load", "cancelLoad", "settings", "resetView", "centerModel", "focus", "select", "setVisible", "showAll", "setTransform",
        "viewMode", "animation", "screenshot", "useCamera", "unload", "language", "requestStats"
    };

    public static ViewerMessage? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxMessageLength) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return null;
            var name = type.GetString()!;
            if (!Incoming.TryGetValue(name, out var valid)) return null;
            var data = root.Clone();
            return valid(data) ? new ViewerMessage(name, data) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Command(string type, object? payload = null)
    {
        if (!Outgoing.Contains(type)) throw new ArgumentException("Unknown viewer command: " + type, nameof(type));
        var message = new Dictionary<string, object?> { ["type"] = type };
        if (payload is not null)
            foreach (var property in JsonSerializer.SerializeToElement(payload).EnumerateObject())
                message[property.Name] = property.Value;
        return JsonSerializer.Serialize(message);
    }

    private static bool Number(JsonElement d, string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble());
    private static bool Text(JsonElement d, string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String;
    private static bool Bool(JsonElement d, string name) => d.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool Object(JsonElement d, string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object;
    private static bool Array(JsonElement d, string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array;
}

/// <summary>
/// Serves the viewer page from resources embedded in Freedom.exe (no files on disk, no network). The model
/// itself is mapped through a separate virtual host straight from its folder, so WebView2 reads it on its own
/// threads and the WPF interface never waits for a large file.
/// </summary>
public static class ViewerResources
{
    public const string AppHost = "freedom-viewer.local";
    public const string ModelHost = "model.freedom-viewer.local";
    public const string AppOrigin = "https://" + AppHost;
    public const string IndexUrl = AppOrigin + "/index.html";
    private const string Prefix = "GlbViewer/Web/";

    /// <summary>
    /// No CDN, no eval: only own scripts, the import map (allowed by its hash), WebAssembly for the decoders and
    /// blob workers.
    /// </summary>
    public static string ContentSecurityPolicy => _csp ??=
        "default-src 'none'; script-src 'self' '" + ImportMapHash() + "' 'wasm-unsafe-eval' blob:; worker-src 'self' blob:; " +
        "connect-src 'self' blob: data: https://" + ModelHost + "; img-src 'self' blob: data: https://" + ModelHost + "; " +
        "style-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly Assembly Assembly = typeof(ViewerResources).Assembly;
    private static readonly HashSet<string> Names = Assembly.GetManifestResourceNames().Select(n => n.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);

    private static string? _csp;

    /// <summary>CSP hash of the inline import map in index.html (computed from the embedded file, so it never goes stale).</summary>
    public static string ImportMapHash()
    {
        var resource = Assembly.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/') == Prefix + "index.html");
        if (resource is null) return "sha256-";
        using var reader = new StreamReader(Assembly.GetManifestResourceStream(resource)!);
        var match = System.Text.RegularExpressions.Regex.Match(reader.ReadToEnd(), "<script type=\"importmap\">(.*?)</script>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(match.Groups[1].Value));
        return "sha256-" + Convert.ToBase64String(hash);
    }

    /// <summary>All embedded viewer files (for tests and diagnostics).</summary>
    public static IEnumerable<string> Files => Names.Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Select(n => n[Prefix.Length..]);

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".wasm" => "application/wasm",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".txt" => "text/plain; charset=utf-8",
        ".glb" => "model/gltf-binary",
        ".bin" => "application/octet-stream",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".ktx2" => "image/ktx2",
        _ => "application/octet-stream"
    };

    /// <summary>Embedded file for a request URL on the app host, or null (404). Paths with ".." never match.</summary>
    public static (Stream Content, string ContentType)? Resolve(Uri uri)
    {
        if (!uri.Host.Equals(AppHost, StringComparison.OrdinalIgnoreCase) || uri.Scheme != "https") return null;
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (path.Length == 0) path = "index.html";
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains('\\')) return null;
        var name = Prefix + path;
        if (!Names.Contains(name)) return null;
        var actual = Assembly.GetManifestResourceNames().First(n => n.Replace('\\', '/') == name);
        var stream = Assembly.GetManifestResourceStream(actual);
        return stream is null ? null : (stream, ContentType(path));
    }

    /// <summary>
    /// File for a request on the model host: the open model itself, or a relative file inside its folder
    /// (external textures/buffers). Anything outside that folder is refused.
    /// </summary>
    public static string? ResolveModelFile(string? modelPath, Uri uri)
    {
        if (modelPath is null || !uri.Host.Equals(ModelHost, StringComparison.OrdinalIgnoreCase) || uri.Scheme != "https") return null;
        var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (relative.Length == 0) return null;
        var folder = Path.GetDirectoryName(modelPath)!;
        if (relative == Path.GetFileName(modelPath)) return modelPath;
        return GlbInspector.ResolveExternal(folder, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>URL of the model file on the model host.</summary>
    public static string ModelUrl(string path) => "https://" + ModelHost + "/" + Uri.EscapeDataString(Path.GetFileName(path));
}

public enum ViewerBackground { Dark, Light, Transparent, Studio }

/// <summary>Viewer settings in %AppData%\ViennaAtomicClock\glbviewer.json (never inside the GLB).</summary>
public sealed class GlbViewerSettings
{
    public const string FileName = "glbviewer.json";

    public ViewerBackground Background { get; set; } = ViewerBackground.Dark;
    public double Exposure { get; set; } = 1;
    public double LightIntensity { get; set; } = 1;
    public bool Shadows { get; set; } = true;
    public bool Grid { get; set; } = true;
    public bool Axes { get; set; }
    public double AnimationSpeed { get; set; } = 1;
    public string? LastFolder { get; set; }
    public bool TransparentScreenshot { get; set; }
    public bool SceneOpen { get; set; } = true;
    public bool DetailsOpen { get; set; } = true;

    public static GlbViewerSettings Load() => Normalize(DataFile.Load<GlbViewerSettings>(FileName));

    public void Save() => DataFile.Save(FileName, this);

    public static GlbViewerSettings Normalize(GlbViewerSettings s)
    {
        s.Exposure = double.IsFinite(s.Exposure) ? Math.Clamp(s.Exposure, 0.1, 4) : 1;
        s.LightIntensity = double.IsFinite(s.LightIntensity) ? Math.Clamp(s.LightIntensity, 0, 4) : 1;
        s.AnimationSpeed = s.AnimationSpeed is 0.25 or 0.5 or 1 or 1.5 or 2 ? s.AnimationSpeed : 1;
        if (!Enum.IsDefined(s.Background)) s.Background = ViewerBackground.Dark;
        return s;
    }
}
