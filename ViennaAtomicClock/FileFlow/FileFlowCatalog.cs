namespace ViennaAtomicClock.FileFlow;

public enum FileFormat
{
    Unknown, Executable,
    Mp4, Mov, Avi, Mkv, Webm, Flv, M4v,
    Mp3, Wav, M4a, Aac, Flac, Ogg, Wma,
    Png, Jpg, Webp, Heic, Gif, Svg, Bmp, Tiff,
    Pdf, Txt, Docx, Pptx, Xlsx, Odt, Rtf, Html, Markdown, Epub,
    YouTube, SoundCloud
}

public enum FormatCategory { Audio, Video, Image, Document, Link }

/// <summary>Name, file extensions and category of a format.</summary>
public sealed record FormatInfo(FileFormat Format, string Label, FormatCategory Category, string[] Extensions)
{
    public string Extension => Extensions.Length > 0 ? Extensions[0] : "";
}

/// <summary>One offered conversion (shown as a card). <see cref="Featured"/> ones are highlighted.</summary>
public sealed record Conversion(FileFormat[] From, FileFormat To, FormatCategory Category, bool Featured = false)
{
    public string Id => string.Join("-", From.Select(f => f.ToString().ToLowerInvariant())) + "-" + To.ToString().ToLowerInvariant();
    public string Title => string.Join("/", From.Select(f => FileFlowCatalog.Info(f).Label)) + " → " + FileFlowCatalog.Info(To).Label;
    public bool IsLink => From.All(f => f is FileFormat.YouTube or FileFormat.SoundCloud);
}

/// <summary>All formats and the 47 conversions FileFlow offers.</summary>
public static class FileFlowCatalog
{
    private static readonly FormatInfo[] FormatList =
    {
        new(FileFormat.Mp4, "MP4", FormatCategory.Video, new[] { ".mp4" }),
        new(FileFormat.Mov, "MOV", FormatCategory.Video, new[] { ".mov", ".qt" }),
        new(FileFormat.Avi, "AVI", FormatCategory.Video, new[] { ".avi" }),
        new(FileFormat.Mkv, "MKV", FormatCategory.Video, new[] { ".mkv" }),
        new(FileFormat.Webm, "WEBM", FormatCategory.Video, new[] { ".webm" }),
        new(FileFormat.Flv, "FLV", FormatCategory.Video, new[] { ".flv" }),
        new(FileFormat.M4v, "M4V", FormatCategory.Video, new[] { ".m4v" }),
        new(FileFormat.Mp3, "MP3", FormatCategory.Audio, new[] { ".mp3" }),
        new(FileFormat.Wav, "WAV", FormatCategory.Audio, new[] { ".wav" }),
        new(FileFormat.M4a, "M4A", FormatCategory.Audio, new[] { ".m4a" }),
        new(FileFormat.Aac, "AAC", FormatCategory.Audio, new[] { ".aac" }),
        new(FileFormat.Flac, "FLAC", FormatCategory.Audio, new[] { ".flac" }),
        new(FileFormat.Ogg, "OGG", FormatCategory.Audio, new[] { ".ogg", ".oga" }),
        new(FileFormat.Wma, "WMA", FormatCategory.Audio, new[] { ".wma" }),
        new(FileFormat.Png, "PNG", FormatCategory.Image, new[] { ".png" }),
        new(FileFormat.Jpg, "JPG", FormatCategory.Image, new[] { ".jpg", ".jpeg", ".jpe", ".jfif" }),
        new(FileFormat.Webp, "WEBP", FormatCategory.Image, new[] { ".webp" }),
        new(FileFormat.Heic, "HEIC", FormatCategory.Image, new[] { ".heic", ".heif" }),
        new(FileFormat.Gif, "GIF", FormatCategory.Image, new[] { ".gif" }),
        new(FileFormat.Svg, "SVG", FormatCategory.Image, new[] { ".svg" }),
        new(FileFormat.Bmp, "BMP", FormatCategory.Image, new[] { ".bmp" }),
        new(FileFormat.Tiff, "TIFF", FormatCategory.Image, new[] { ".tif", ".tiff" }),
        new(FileFormat.Pdf, "PDF", FormatCategory.Document, new[] { ".pdf" }),
        new(FileFormat.Txt, "TXT", FormatCategory.Document, new[] { ".txt", ".text", ".log" }),
        new(FileFormat.Docx, "DOCX", FormatCategory.Document, new[] { ".docx" }),
        new(FileFormat.Pptx, "PPTX", FormatCategory.Document, new[] { ".pptx" }),
        new(FileFormat.Xlsx, "XLSX", FormatCategory.Document, new[] { ".xlsx" }),
        new(FileFormat.Odt, "ODT", FormatCategory.Document, new[] { ".odt" }),
        new(FileFormat.Rtf, "RTF", FormatCategory.Document, new[] { ".rtf" }),
        new(FileFormat.Html, "HTML", FormatCategory.Document, new[] { ".html", ".htm" }),
        new(FileFormat.Markdown, "Markdown", FormatCategory.Document, new[] { ".md", ".markdown" }),
        new(FileFormat.Epub, "EPUB", FormatCategory.Document, new[] { ".epub" }),
        new(FileFormat.YouTube, "YouTube", FormatCategory.Link, Array.Empty<string>()),
        new(FileFormat.SoundCloud, "SoundCloud", FormatCategory.Link, Array.Empty<string>())
    };

    private static readonly Dictionary<FileFormat, FormatInfo> ByFormat = FormatList.ToDictionary(f => f.Format);

    private static Conversion C(FileFormat from, FileFormat to, FormatCategory category, bool featured = false) => new(new[] { from }, to, category, featured);

    /// <summary>The seven main conversions first (highlighted), then the 40 additional ones in the order of the brief.</summary>
    public static readonly IReadOnlyList<Conversion> Conversions = new[]
    {
        C(FileFormat.Mp4, FileFormat.Mp3, FormatCategory.Audio, true),
        C(FileFormat.YouTube, FileFormat.Mp3, FormatCategory.Link, true),
        C(FileFormat.SoundCloud, FileFormat.Mp3, FormatCategory.Link, true),
        C(FileFormat.Png, FileFormat.Pdf, FormatCategory.Image, true),
        C(FileFormat.Jpg, FileFormat.Pdf, FormatCategory.Image, true),
        C(FileFormat.Txt, FileFormat.Pdf, FormatCategory.Document, true),
        C(FileFormat.Docx, FileFormat.Pdf, FormatCategory.Document, true),

        C(FileFormat.Mov, FileFormat.Mp4, FormatCategory.Video),
        C(FileFormat.Avi, FileFormat.Mp4, FormatCategory.Video),
        C(FileFormat.Mkv, FileFormat.Mp4, FormatCategory.Video),
        C(FileFormat.Webm, FileFormat.Mp4, FormatCategory.Video),
        C(FileFormat.Flv, FileFormat.Mp4, FormatCategory.Video),
        C(FileFormat.M4v, FileFormat.Mp4, FormatCategory.Video),
        C(FileFormat.Mp4, FileFormat.Wav, FormatCategory.Audio),
        C(FileFormat.Mp4, FileFormat.Aac, FormatCategory.Audio),
        C(FileFormat.Wav, FileFormat.Mp3, FormatCategory.Audio),
        C(FileFormat.M4a, FileFormat.Mp3, FormatCategory.Audio),
        C(FileFormat.Aac, FileFormat.Mp3, FormatCategory.Audio),
        C(FileFormat.Flac, FileFormat.Mp3, FormatCategory.Audio),
        C(FileFormat.Ogg, FileFormat.Mp3, FormatCategory.Audio),
        C(FileFormat.Wma, FileFormat.Mp3, FormatCategory.Audio),
        C(FileFormat.Mp3, FileFormat.Wav, FormatCategory.Audio),
        C(FileFormat.Mp3, FileFormat.M4a, FormatCategory.Audio),
        C(FileFormat.Png, FileFormat.Jpg, FormatCategory.Image),
        C(FileFormat.Jpg, FileFormat.Png, FormatCategory.Image),
        C(FileFormat.Png, FileFormat.Webp, FormatCategory.Image),
        C(FileFormat.Jpg, FileFormat.Webp, FormatCategory.Image),
        C(FileFormat.Webp, FileFormat.Jpg, FormatCategory.Image),
        C(FileFormat.Webp, FileFormat.Png, FormatCategory.Image),
        C(FileFormat.Heic, FileFormat.Jpg, FormatCategory.Image),
        C(FileFormat.Heic, FileFormat.Png, FormatCategory.Image),
        C(FileFormat.Gif, FileFormat.Mp4, FormatCategory.Video),
        C(FileFormat.Svg, FileFormat.Png, FormatCategory.Image),
        C(FileFormat.Bmp, FileFormat.Png, FormatCategory.Image),
        C(FileFormat.Tiff, FileFormat.Jpg, FormatCategory.Image),
        C(FileFormat.Pdf, FileFormat.Jpg, FormatCategory.Document),
        C(FileFormat.Pdf, FileFormat.Png, FormatCategory.Document),
        C(FileFormat.Pdf, FileFormat.Txt, FormatCategory.Document),
        C(FileFormat.Pdf, FileFormat.Docx, FormatCategory.Document),
        C(FileFormat.Docx, FileFormat.Txt, FormatCategory.Document),
        C(FileFormat.Pptx, FileFormat.Pdf, FormatCategory.Document),
        C(FileFormat.Xlsx, FileFormat.Pdf, FormatCategory.Document),
        C(FileFormat.Odt, FileFormat.Pdf, FormatCategory.Document),
        C(FileFormat.Rtf, FileFormat.Pdf, FormatCategory.Document),
        C(FileFormat.Html, FileFormat.Pdf, FormatCategory.Document),
        C(FileFormat.Markdown, FileFormat.Pdf, FormatCategory.Document),
        C(FileFormat.Epub, FileFormat.Pdf, FormatCategory.Document)
    };

    public static IEnumerable<FormatInfo> Formats => FormatList;

    public static FormatInfo Info(FileFormat format) =>
        ByFormat.TryGetValue(format, out var info) ? info : new FormatInfo(format, format == FileFormat.Executable ? "EXE" : "?", FormatCategory.Document, Array.Empty<string>());

    /// <summary>Formats a file of <paramref name="from"/> can be turned into.</summary>
    public static IReadOnlyList<FileFormat> TargetsFor(FileFormat from) =>
        Conversions.Where(c => c.From.Contains(from)).Select(c => c.To).Distinct().ToList();

    public static bool IsSupported(FileFormat from, FileFormat to) => Conversions.Any(c => c.To == to && c.From.Contains(from));

    /// <summary>The usual choice when a file arrives without a chosen card.</summary>
    public static FileFormat DefaultTarget(FileFormat from) => from switch
    {
        FileFormat.Mp4 => FileFormat.Mp3,
        FileFormat.Png or FileFormat.Jpg => FileFormat.Pdf,
        FileFormat.Pdf => FileFormat.Docx,
        _ => TargetsFor(from).FirstOrDefault()
    };

    public static FileFormat FromExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return FileFormat.Unknown;
        var ext = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        return FormatList.FirstOrDefault(f => f.Extensions.Contains(ext))?.Format ?? FileFormat.Unknown;
    }

    public static bool IsImage(FileFormat format) => Info(format).Category == FormatCategory.Image;
}
