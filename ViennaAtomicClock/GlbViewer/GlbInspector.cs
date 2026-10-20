using System.IO;
using System.Text;
using System.Text.Json;

namespace ViennaAtomicClock.GlbViewer;

/// <summary>Why a file cannot be opened (text key + arguments, translated by the page).</summary>
public sealed class GlbValidationException : Exception
{
    public GlbValidationException(string key, params object[] args) : base(key)
    {
        Key = key;
        Args = args;
    }

    public string Key { get; }
    public object[] Args { get; }
}

/// <summary>What a quick look at the GLB container and its JSON tells before the renderer sees it.</summary>
public sealed record GlbInspection(
    string Path, long FileSize, int Version,
    int Scenes, int Nodes, int Meshes, int Materials, int Textures, int Images, int Animations, int Cameras, int Skins,
    long Vertices, long Triangles,
    IReadOnlyList<string> ExtensionsUsed, IReadOnlyList<string> ExtensionsRequired,
    IReadOnlyList<string> UnsupportedExtensions,
    IReadOnlyList<GlbWarning> Warnings,
    string? Generator);

public sealed record GlbWarning(string Key, params object[] Args);

/// <summary>
/// Checks extension, GLB header, chunk layout and the glTF JSON without loading geometry. Nothing is written,
/// the original file is only read.
/// </summary>
public static class GlbInspector
{
    public const uint Magic = 0x46546C67; // "glTF"
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;
    /// <summary>Hard limit so a broken header cannot make the viewer allocate gigabytes.</summary>
    public const long MaxFileSize = 2L * 1024 * 1024 * 1024;

    /// <summary>Extensions the bundled Three.js GLTFLoader (r169) understands, including the decoders shipped with Freedom.</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        "KHR_binary_glTF", "KHR_draco_mesh_compression", "KHR_lights_punctual", "KHR_materials_clearcoat", "KHR_materials_dispersion",
        "KHR_materials_ior", "KHR_materials_sheen", "KHR_materials_specular", "KHR_materials_transmission", "KHR_materials_iridescence",
        "KHR_materials_anisotropy", "KHR_materials_unlit", "KHR_materials_volume", "KHR_texture_basisu", "KHR_texture_transform",
        "KHR_mesh_quantization", "KHR_materials_emissive_strength", "EXT_materials_bump", "EXT_texture_webp", "EXT_texture_avif",
        "EXT_meshopt_compression", "EXT_mesh_gpu_instancing"
    };

    public static GlbInspection Inspect(string path)
    {
        if (!string.Equals(System.IO.Path.GetExtension(path), ".glb", StringComparison.OrdinalIgnoreCase))
            throw new GlbValidationException("glb.err.extension");
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) throw new GlbValidationException("glb.err.missing");
        }
        catch (Exception error) when (error is not GlbValidationException)
        {
            throw new GlbValidationException("glb.err.read", error.Message);
        }
        if (info.Length > MaxFileSize) throw new GlbValidationException("glb.err.tooLarge");

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            return Inspect(stream, path, info.Length);
        }
        catch (GlbValidationException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new GlbValidationException("glb.err.read", error.Message);
        }
    }

    public static GlbInspection Inspect(Stream stream, string path, long fileSize)
    {
        Span<byte> header = stackalloc byte[12];
        if (stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12) throw new GlbValidationException("glb.err.header");
        var magic = BitConverter.ToUInt32(header[..4]);
        var version = (int)BitConverter.ToUInt32(header[4..8]);
        var length = BitConverter.ToUInt32(header[8..12]);
        if (magic != Magic) throw new GlbValidationException("glb.err.header");
        if (version != 2) throw new GlbValidationException("glb.err.version", version);
        if (length < 20 || length > fileSize) throw new GlbValidationException("glb.err.length", length, fileSize);

        Span<byte> chunkHeader = stackalloc byte[8];
        if (stream.ReadAtLeast(chunkHeader, 8, throwOnEndOfStream: false) < 8) throw new GlbValidationException("glb.err.chunk");
        var jsonLength = BitConverter.ToUInt32(chunkHeader[..4]);
        var jsonType = BitConverter.ToUInt32(chunkHeader[4..8]);
        if (jsonType != JsonChunk || jsonLength == 0 || 20 + (long)jsonLength > length || jsonLength > 256 * 1024 * 1024)
            throw new GlbValidationException("glb.err.chunk");
        var json = new byte[jsonLength];
        if (stream.ReadAtLeast(json, (int)jsonLength, throwOnEndOfStream: false) < jsonLength) throw new GlbValidationException("glb.err.chunk");

        // An optional BIN chunk must fit into the declared length.
        var afterJson = 20 + (long)jsonLength;
        var hasBin = false;
        if (length - afterJson >= 8)
        {
            if (stream.ReadAtLeast(chunkHeader, 8, throwOnEndOfStream: false) < 8) throw new GlbValidationException("glb.err.chunk");
            var binLength = BitConverter.ToUInt32(chunkHeader[..4]);
            var binType = BitConverter.ToUInt32(chunkHeader[4..8]);
            if (binType == BinChunk)
            {
                if (afterJson + 8 + binLength > length) throw new GlbValidationException("glb.err.chunk");
                hasBin = true;
            }
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(Encoding.UTF8.GetString(json).TrimEnd(' ', '\0'));
        }
        catch (JsonException error)
        {
            throw new GlbValidationException("glb.err.json", error.Message);
        }

        using (document)
            return Analyze(document.RootElement, path, fileSize, version, hasBin);
    }

    private static GlbInspection Analyze(JsonElement root, string path, long fileSize, int version, bool hasBin)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("asset", out var asset) || asset.ValueKind != JsonValueKind.Object)
            throw new GlbValidationException("glb.err.asset");
        var assetVersion = asset.TryGetProperty("version", out var av) ? av.GetString() : null;
        if (assetVersion is null || !assetVersion.StartsWith("2.", StringComparison.Ordinal)) throw new GlbValidationException("glb.err.version", assetVersion ?? "?");

        int Count(string name) => root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.GetArrayLength() : 0;
        List<string> Strings(string name) => root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : new List<string>();

        var used = Strings("extensionsUsed");
        var required = Strings("extensionsRequired");
        var unsupportedRequired = required.Where(e => !Supported.Contains(e)).ToList();
        if (unsupportedRequired.Count > 0) throw new GlbValidationException("glb.err.extensions", string.Join(", ", unsupportedRequired));
        var unsupported = used.Where(e => !Supported.Contains(e)).ToList();

        var warnings = new List<GlbWarning>();
        if (unsupported.Count > 0) warnings.Add(new GlbWarning("glb.warn.extensions", string.Join(", ", unsupported)));

        // External files referenced next to the GLB (buffers and images with a relative uri).
        var folder = System.IO.Path.GetDirectoryName(path) ?? "";
        foreach (var kind in new[] { "images", "buffers" })
        {
            if (!root.TryGetProperty(kind, out var array) || array.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in array.EnumerateArray())
            {
                if (!item.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String) continue;
                var value = uri.GetString()!;
                if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
                {
                    warnings.Add(new GlbWarning("glb.warn.remote", value)); // never fetched: the viewer stays offline
                    continue;
                }
                var local = ResolveExternal(folder, Uri.UnescapeDataString(value));
                if (local is null || !File.Exists(local)) warnings.Add(new GlbWarning(kind == "images" ? "glb.warn.missingTexture" : "glb.warn.missingBuffer", value));
            }
        }
        if (!hasBin && root.TryGetProperty("buffers", out var buffers) && buffers.ValueKind == JsonValueKind.Array
            && buffers.EnumerateArray().Any(b => !b.TryGetProperty("uri", out _)))
            warnings.Add(new GlbWarning("glb.warn.noBin"));

        // Materials pointing at textures that do not exist.
        var textures = Count("textures");
        if (root.TryGetProperty("materials", out var materials) && materials.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var material in materials.EnumerateArray())
            {
                foreach (var reference in TextureIndices(material))
                    if (reference < 0 || reference >= textures)
                    {
                        warnings.Add(new GlbWarning("glb.warn.badMaterial", material.TryGetProperty("name", out var n) ? n.GetString() ?? $"#{index}" : $"#{index}"));
                        break;
                    }
                index++;
            }
        }

        var (vertices, triangles) = Geometry(root);
        return new GlbInspection(path, fileSize, version,
            Count("scenes"), Count("nodes"), Count("meshes"), Count("materials"), textures, Count("images"), Count("animations"), Count("cameras"), Count("skins"),
            vertices, triangles, used, required, unsupported, warnings,
            asset.TryGetProperty("generator", out var generator) ? generator.GetString() : null);
    }

    /// <summary>A relative uri inside the model's folder, or null if it would leave it.</summary>
    public static string? ResolveExternal(string folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative)) return null;
        try
        {
            var root = System.IO.Path.GetFullPath(folder.EndsWith(System.IO.Path.DirectorySeparatorChar) ? folder : folder + System.IO.Path.DirectorySeparatorChar);
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<int> TextureIndices(JsonElement element)
    {
        // Every {"index": n} below a material is a texture reference (core and extensions alike).
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "index" && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value))
                    yield return value;
                foreach (var nested in TextureIndices(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var nested in TextureIndices(item)) yield return nested;
    }

    /// <summary>Vertex and triangle totals from the accessors (each mesh counted once).</summary>
    private static (long Vertices, long Triangles) Geometry(JsonElement root)
    {
        if (!root.TryGetProperty("meshes", out var meshes) || meshes.ValueKind != JsonValueKind.Array) return (0, 0);
        var accessors = root.TryGetProperty("accessors", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : new List<JsonElement>();
        long Accessor(JsonElement index) =>
            index.TryGetInt32(out var i) && i >= 0 && i < accessors.Count && accessors[i].TryGetProperty("count", out var c) && c.TryGetInt64(out var count) ? count : 0;

        long vertices = 0, triangles = 0;
        foreach (var mesh in meshes.EnumerateArray())
        {
            if (!mesh.TryGetProperty("primitives", out var primitives) || primitives.ValueKind != JsonValueKind.Array) continue;
            foreach (var primitive in primitives.EnumerateArray())
            {
                var count = primitive.TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty("POSITION", out var position) ? Accessor(position) : 0;
                vertices += count;
                var mode = primitive.TryGetProperty("mode", out var m) && m.TryGetInt32(out var modeValue) ? modeValue : 4;
                var indexCount = primitive.TryGetProperty("indices", out var indices) ? Accessor(indices) : count;
                triangles += mode switch
                {
                    4 => indexCount / 3,
                    5 or 6 => Math.Max(0, indexCount - 2),
                    _ => 0
                };
            }
        }
        return (vertices, triangles);
    }
}
