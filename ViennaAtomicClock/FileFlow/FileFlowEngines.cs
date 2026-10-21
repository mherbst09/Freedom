using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ViennaAtomicClock.FileFlow;

public enum VideoQuality { High, Medium, Small }

public enum PdfPageSize { A4, Letter, Auto }

/// <summary>User choices for a conversion (quality, size, page format).</summary>
public sealed record ConversionOptions
{
    public static readonly int[] AudioBitrates = { 128, 192, 256, 320 };
    public static readonly int[] VideoHeights = { 0, 2160, 1080, 720, 480 };
    public static readonly int[] ImageQualities = { 100, 90, 75, 60 };
    public static readonly int[] ImageSizes = { 0, 3840, 2560, 1920, 1280, 800 };

    public int AudioBitrate { get; init; } = 192;
    /// <summary>Maximum video height in pixels, 0 = keep.</summary>
    public int VideoHeight { get; init; }
    public VideoQuality VideoQuality { get; init; } = VideoQuality.Medium;
    public int ImageQuality { get; init; } = 90;
    /// <summary>Maximum long side of images in pixels, 0 = keep.</summary>
    public int ImageMaxSide { get; init; }
    public PdfPageSize PageSize { get; init; } = PdfPageSize.A4;
    /// <summary>Several images → PDF become one document.</summary>
    public bool MergeImages { get; init; } = true;
}

// ───────────────────────── audio and video ─────────────────────────

/// <summary>FFmpeg for audio and video (and as helper for WEBP, HEIC and SVG images); yt-dlp for links.</summary>
internal static class MediaEngine
{
    public static string RequireFfmpeg() => ToolLocator.Find(ExternalTool.FFmpeg) ?? throw new ConversionException("ff.err.needFfmpeg");

    public static async Task ConvertAsync(string input, FileFormat to, string output, ConversionOptions options, IProgress<double> progress, TimeSpan timeout, CancellationToken token)
    {
        var ffmpeg = RequireFfmpeg();
        var (duration, hasAudio, hasVideo) = await ProbeAsync(input, token);
        var audioOnly = to is FileFormat.Mp3 or FileFormat.Wav or FileFormat.Aac or FileFormat.M4a;
        if (audioOnly && hasAudio == false) throw new ConversionException("ff.err.noAudio");
        if (to == FileFormat.Mp4 && hasVideo == false) throw new ConversionException("ff.err.noVideo");

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };
        var bitrate = options.AudioBitrate.ToString(CultureInfo.InvariantCulture) + "k";
        switch (to)
        {
            case FileFormat.Mp3: args.AddRange(new[] { "-vn", "-map_metadata", "0", "-c:a", "libmp3lame", "-b:a", bitrate }); break;
            case FileFormat.Wav: args.AddRange(new[] { "-vn", "-c:a", "pcm_s16le" }); break;
            case FileFormat.Aac: args.AddRange(new[] { "-vn", "-c:a", "aac", "-b:a", bitrate, "-f", "adts" }); break;
            case FileFormat.M4a: args.AddRange(new[] { "-vn", "-map_metadata", "0", "-c:a", "aac", "-b:a", bitrate, "-movflags", "+faststart" }); break;
            case FileFormat.Mp4:
                var crf = options.VideoQuality switch { VideoQuality.High => "18", VideoQuality.Small => "28", _ => "23" };
                // H.264 needs even sizes; never upscale.
                var scale = options.VideoHeight > 0
                    ? $"scale=-2:'trunc(min({options.VideoHeight},ih)/2)*2'"
                    : "scale='trunc(iw/2)*2':'trunc(ih/2)*2'";
                args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?", "-vf", scale, "-c:v", "libx264", "-preset", "medium", "-crf", crf, "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart" });
                break;
            default: throw new ConversionException("ff.err.unsupported");
        }
        args.AddRange(new[] { "-progress", "pipe:1", "-nostats", output });
        var result = await ProcessRunner.RunAsync(ffmpeg, args, timeout, token, line => ReportTime(line, duration, progress));
        if (!result.Success || !File.Exists(output) || new FileInfo(output).Length == 0) throw MapFfmpegError(result.Output);
        progress.Report(1);
    }

    /// <summary>Runs FFmpeg with the given arguments (images, thumbnails). Throws a friendly error on failure.</summary>
    public static async Task RunAsync(IEnumerable<string> arguments, string output, TimeSpan timeout, CancellationToken token)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };
        args.AddRange(arguments);
        args.Add(output);
        var result = await ProcessRunner.RunAsync(RequireFfmpeg(), args, timeout, token);
        if (!result.Success || !File.Exists(output) || new FileInfo(output).Length == 0) throw MapFfmpegError(result.Output);
    }

    private static void ReportTime(string line, double duration, IProgress<double> progress)
    {
        if (duration <= 0) return;
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
        {
            // Both keys carry microseconds.
            if (long.TryParse(line.AsSpan(line.IndexOf('=') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us > 0)
                progress.Report(Math.Clamp(us / 1_000_000.0 / duration, 0, 0.99));
        }
    }

    /// <summary>Duration in seconds and which stream types exist (null = unknown, e.g. without ffprobe).</summary>
    public static async Task<(double Duration, bool? HasAudio, bool? HasVideo)> ProbeAsync(string input, CancellationToken token)
    {
        var ffprobe = ToolLocator.Find(ExternalTool.FFprobe);
        if (ffprobe is null) return (0, null, null);
        try
        {
            var result = await ProcessRunner.RunAsync(ffprobe, new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "default=nw=1", input },
                TimeSpan.FromSeconds(30), token);
            if (!result.Success) throw new ConversionException("ff.err.damaged");
            var duration = 0.0;
            bool audio = false, video = false;
            foreach (var line in result.Output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("duration=", StringComparison.Ordinal))
                    double.TryParse(trimmed.AsSpan(9), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                if (trimmed == "codec_type=audio") audio = true;
                if (trimmed == "codec_type=video") video = true;
            }
            return (duration, audio, video);
        }
        catch (ConversionException error) when (error.Key == "ff.err.timeout")
        {
            return (0, null, null);
        }
    }

    public static ConversionException MapFfmpegError(string output)
    {
        if (output.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase) || output.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase))
            return new ConversionException("ff.err.damaged");
        if (output.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase) || output.Contains("matches no streams", StringComparison.OrdinalIgnoreCase))
            return new ConversionException("ff.err.noAudio");
        if (output.Contains("No space left", StringComparison.OrdinalIgnoreCase)) return new ConversionException("ff.err.disk");
        if (output.Contains("Decoder", StringComparison.OrdinalIgnoreCase) && output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return new ConversionException("ff.err.codec");
        return new ConversionException("ff.err.failed");
    }

    // ───────────── links ─────────────

    /// <summary>Which platform a link belongs to (only YouTube and SoundCloud over http(s)).</summary>
    public static FileFormat ClassifyLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return FileFormat.Unknown;
        var host = uri.Host.ToLowerInvariant();
        if (host is "youtu.be" or "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com") return FileFormat.YouTube;
        if (host is "soundcloud.com" or "www.soundcloud.com" or "m.soundcloud.com" or "on.soundcloud.com") return FileFormat.SoundCloud;
        return FileFormat.Unknown;
    }

    /// <summary>
    /// Audio from a public link via yt-dlp. No cookies, no account, no geo or DRM workarounds: anything private,
    /// paid, age-gated, members-only or protected fails with a clear message instead.
    /// </summary>
    public static async Task<string> DownloadAudioAsync(string url, string outDir, int bitrate, IProgress<double> progress, TimeSpan timeout, CancellationToken token)
    {
        var ytdlp = ToolLocator.Find(ExternalTool.YtDlp) ?? throw new ConversionException("ff.err.needYtDlp");
        var ffmpeg = RequireFfmpeg();
        var args = new List<string>
        {
            "--ignore-config", "--no-playlist", "--newline", "--no-colors", "--no-mtime", "--no-cache-dir", "--no-continue",
            "--windows-filenames", "--match-filter", "!is_live", "--max-filesize", "1G",
            "-f", "bestaudio/best", "-x", "--audio-format", "mp3", "--audio-quality", bitrate.ToString(CultureInfo.InvariantCulture) + "K",
            "--ffmpeg-location", Path.GetDirectoryName(ffmpeg)!,
            "-o", Path.Combine(outDir, "%(title).100B.%(ext)s"),
            "--", url.Trim()
        };
        var percent = new Regex(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
        var result = await ProcessRunner.RunAsync(ytdlp, args, timeout, token, line =>
        {
            var match = percent.Match(line);
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) progress.Report(p / 100 * 0.88);
            else if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal)) progress.Report(0.92);
        }, outDir);
        var mp3 = Directory.EnumerateFiles(outDir, "*.mp3").FirstOrDefault();
        if (!result.Success || mp3 is null) throw MapLinkError(result.Output);
        progress.Report(1);
        return mp3;
    }

    public static ConversionException MapLinkError(string output)
    {
        var text = output.ToLowerInvariant();
        if (text.Contains("private")) return new ConversionException("ff.err.link.private");
        if (text.Contains("drm")) return new ConversionException("ff.err.link.drm");
        if (text.Contains("sign in to confirm your age") || text.Contains("age-restricted") || text.Contains("inappropriate for some users")) return new ConversionException("ff.err.link.age");
        if (text.Contains("members") || text.Contains("premium") || text.Contains("payment") || text.Contains("purchase") || text.Contains("go+")) return new ConversionException("ff.err.link.paid");
        if (text.Contains("live event") || text.Contains("is live") || text.Contains("does not pass filter")) return new ConversionException("ff.err.link.live");
        if (text.Contains("geo") || text.Contains("in your country") || text.Contains("403")) return new ConversionException("ff.err.link.blocked");
        if (text.Contains("max-filesize") || text.Contains("larger than max")) return new ConversionException("ff.err.tooLarge", "1 GB");
        if (text.Contains("unavailable") || text.Contains("404") || text.Contains("not found") || text.Contains("unsupported url")) return new ConversionException("ff.err.link.unavailable");
        if (text.Contains("unable to download") || text.Contains("network") || text.Contains("timed out") || text.Contains("getaddrinfo")) return new ConversionException("ff.err.link.network");
        return new ConversionException("ff.err.link.failed");
    }
}

// ───────────────────────── images ─────────────────────────

/// <summary>Images through Windows Imaging (WIC); FFmpeg helps with SVG, WEBP output and missing HEIC/WEBP codecs.</summary>
internal static class ImageEngine
{
    public const int MaxPixels = 120_000_000; // ~ 11000×11000, protects the memory

    public static async Task ConvertAsync(string input, FileFormat from, FileFormat to, string output, string workDir, ConversionOptions options, CancellationToken token)
    {
        var image = Resize(await LoadAsync(input, from, workDir, token), options.ImageMaxSide);
        token.ThrowIfCancellationRequested();
        switch (to)
        {
            case FileFormat.Png: Save(new PngBitmapEncoder(), image, output); break;
            case FileFormat.Jpg: Save(new JpegBitmapEncoder { QualityLevel = Math.Clamp(options.ImageQuality, 1, 100) }, Flatten(image), output); break;
            case FileFormat.Webp:
                var png = Path.Combine(workDir, "webp-source.png");
                Save(new PngBitmapEncoder(), image, png);
                await MediaEngine.RunAsync(new[] { "-i", png, "-c:v", "libwebp", "-quality", options.ImageQuality.ToString(CultureInfo.InvariantCulture), "-frames:v", "1" }, output, TimeSpan.FromMinutes(3), token);
                break;
            default: throw new ConversionException("ff.err.unsupported");
        }
    }

    /// <summary>Decodes an image (first frame, EXIF rotation applied) as frozen BGRA.</summary>
    public static async Task<BitmapSource> LoadAsync(string input, FileFormat format, string workDir, CancellationToken token)
    {
        if (format == FileFormat.Svg) return Decode(await RasterizeAsync(input, workDir, token));
        try
        {
            return Decode(input);
        }
        catch (Exception error) when (format is FileFormat.Heic or FileFormat.Webp && error is not OperationCanceledException and not ConversionException { Key: "ff.err.imageTooLarge" })
        {
            // Windows without the HEIF/WEBP codec: let FFmpeg decode it.
            var png = Path.Combine(workDir, "decoded.png");
            try
            {
                await MediaEngine.RunAsync(new[] { "-i", input, "-frames:v", "1" }, png, TimeSpan.FromMinutes(2), token);
            }
            catch (ConversionException inner) when (inner.Key == "ff.err.needFfmpeg")
            {
                throw new ConversionException(format == FileFormat.Heic ? "ff.err.heicCodec" : "ff.err.damaged");
            }
            return Decode(png);
        }
    }

    /// <summary>SVG → PNG with FFmpeg (librsvg), or LibreOffice if FFmpeg cannot.</summary>
    private static async Task<string> RasterizeAsync(string svg, string workDir, CancellationToken token)
    {
        var png = Path.Combine(workDir, "svg.png");
        try
        {
            await MediaEngine.RunAsync(new[] { "-i", svg, "-frames:v", "1" }, png, TimeSpan.FromMinutes(2), token);
            return png;
        }
        catch (ConversionException) when (ToolLocator.IsAvailable(ExternalTool.LibreOffice))
        {
            return await OfficeEngine.ConvertWithLibreOfficeAsync(svg, "png", workDir, token);
        }
    }

    public static BitmapSource Decode(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels) throw new ConversionException("ff.err.imageTooLarge");
            BitmapSource image = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var angle = Orientation(frame);
            if (angle != 0) image = new TransformedBitmap(image, new RotateTransform(angle));
            image = new WriteableBitmap(image); // materialise once, then it is cheap to reuse
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw new ConversionException("ff.err.damaged");
        }
    }

    /// <summary>EXIF orientation of photos (1 = normal, 3 = 180°, 6 = 90°, 8 = 270°).</summary>
    private static double Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("System.Photo.Orientation"))
                return metadata.GetQuery("System.Photo.Orientation") switch { (ushort)3 => 180, (ushort)6 => 90, (ushort)8 => 270, _ => 0 };
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // No readable metadata.
        }
        return 0;
    }

    public static BitmapSource Resize(BitmapSource image, int maxSide)
    {
        var longSide = Math.Max(image.PixelWidth, image.PixelHeight);
        if (maxSide <= 0 || longSide <= maxSide) return image;
        var scale = maxSide / (double)longSide;
        var resized = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        var result = new WriteableBitmap(resized);
        result.Freeze();
        return result;
    }

    /// <summary>Transparent areas on white (JPEG and PDF have no transparency).</summary>
    public static BitmapSource Flatten(BitmapSource image)
    {
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        image.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a == 255) continue;
            for (var c = 0; c < 3; c++) pixels[i + c] = (byte)((pixels[i + c] * a + 255 * (255 - a)) / 255);
            pixels[i + 3] = 255;
        }
        var flat = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        flat.Freeze();
        return flat;
    }

    public static byte[] Jpeg(BitmapSource image, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(Flatten(image)));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    public static void Save(BitmapEncoder encoder, BitmapSource image, string path)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }
}

// ───────────────────────── PDF ─────────────────────────

/// <summary>Writes simple, valid PDF files: image pages (JPEG) and plain text pages (Courier).</summary>
public sealed class SimplePdfWriter
{
    private readonly List<byte[]> _pageObjects = new(); // content + resources per page, filled at Save
    private readonly List<(double W, double H, byte[] Content, byte[]? Jpeg, int ImageW, int ImageH)> _pages = new();

    public int PageCount => _pages.Count;

    public static (double W, double H) PageSize(PdfPageSize size, bool landscape) => size switch
    {
        PdfPageSize.Letter => landscape ? (792, 612) : (612, 792),
        _ => landscape ? (841.89, 595.28) : (595.28, 841.89)
    };

    /// <summary>A page with a JPEG image, fitted with margins (A4/Letter, turned for wide images) or exactly sized (Auto).</summary>
    public void AddImagePage(byte[] jpeg, int pixelWidth, int pixelHeight, PdfPageSize size)
    {
        double pageW, pageH, x, y, w, h;
        if (size == PdfPageSize.Auto)
        {
            pageW = pixelWidth * 72.0 / 96;
            pageH = pixelHeight * 72.0 / 96;
            (x, y, w, h) = (0, 0, pageW, pageH);
        }
        else
        {
            (pageW, pageH) = PageSize(size, pixelWidth > pixelHeight);
            const double margin = 28;
            var scale = Math.Min((pageW - 2 * margin) / pixelWidth, (pageH - 2 * margin) / pixelHeight);
            w = pixelWidth * scale;
            h = pixelHeight * scale;
            x = (pageW - w) / 2;
            y = (pageH - h) / 2;
        }
        var content = Ascii(FormattableString.Invariant($"q {w:0.###} 0 0 {h:0.###} {x:0.###} {y:0.###} cm /Im0 Do Q"));
        _pages.Add((pageW, pageH, content, jpeg, pixelWidth, pixelHeight));
    }

    /// <summary>Plain text in Courier 10 pt, wrapped and split over as many pages as needed.</summary>
    public void AddText(string text, PdfPageSize size)
    {
        var (pageW, pageH) = size == PdfPageSize.Auto ? PageSize(PdfPageSize.A4, false) : PageSize(size, false);
        const double margin = 56, fontSize = 10, leading = 13;
        var columns = (int)((pageW - 2 * margin) / (fontSize * 0.6));
        var rows = (int)((pageH - 2 * margin) / leading);
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Replace("\t", "    ");
            if (line.Length == 0) lines.Add("");
            while (line.Length > 0)
            {
                var take = line.Length <= columns ? line.Length : BreakAt(line, columns);
                lines.Add(line[..take].TrimEnd());
                line = line[take..].TrimStart();
            }
        }
        if (lines.Count == 0) lines.Add("");
        for (var start = 0; start < lines.Count; start += rows)
        {
            var content = new StringBuilder();
            content.Append(FormattableString.Invariant($"BT /F1 {fontSize} Tf {leading} TL {margin} {pageH - margin - fontSize} Td\n"));
            foreach (var line in lines.Skip(start).Take(rows)) content.Append('(').Append(Escape(line)).Append(") Tj T*\n");
            content.Append("ET");
            _pages.Add((pageW, pageH, Latin1(content.ToString()), null, 0, 0));
        }
    }

    private static int BreakAt(string line, int columns)
    {
        var space = line.LastIndexOf(' ', columns - 1, columns);
        return space > columns / 2 ? space + 1 : columns;
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '(' or ')' or '\\') builder.Append('\\').Append(c);
            else if (c < 32) builder.Append(' ');
            else builder.Append(c <= 0xFF && c is not (>= '\u0080' and <= '\u009F') ? c : '?'); // Courier with WinAnsi covers Latin-1
        }
        return builder.ToString();
    }

    public void Save(string path)
    {
        if (_pages.Count == 0) throw new InvalidOperationException("No pages.");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var offsets = new List<long>();
        void Write(byte[] bytes) => stream.Write(bytes);
        void Obj(int number, params byte[][] parts)
        {
            while (offsets.Count < number) offsets.Add(0);
            offsets[number - 1] = stream.Position;
            Write(Ascii($"{number} 0 obj\n"));
            foreach (var part in parts) Write(part);
            Write(Ascii("\nendobj\n"));
        }

        Write(Ascii("%PDF-1.4\n"));
        Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
        // 1 catalog, 2 pages, 3 font, then per page: page, content, (image).
        var next = 4;
        var pageNumbers = new List<int>();
        var layout = new List<(int Page, int Content, int Image)>();
        foreach (var page in _pages)
        {
            var entry = (next, next + 1, page.Jpeg is null ? 0 : next + 2);
            next += page.Jpeg is null ? 2 : 3;
            layout.Add(entry);
            pageNumbers.Add(entry.Item1);
        }
        Obj(1, Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        Obj(2, Ascii($"<< /Type /Pages /Count {_pages.Count} /Kids [{string.Join(" ", pageNumbers.Select(n => $"{n} 0 R"))}] >>"));
        Obj(3, Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>"));
        for (var i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            var (pageObj, contentObj, imageObj) = layout[i];
            var resources = imageObj > 0 ? $"<< /XObject << /Im0 {imageObj} 0 R >> >>" : "<< /Font << /F1 3 0 R >> >>";
            Obj(pageObj, Ascii(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.W:0.##} {page.H:0.##}] /Resources {resources} /Contents {contentObj} 0 R >>")));
            Obj(contentObj, Ascii($"<< /Length {page.Content.Length} >>\nstream\n"), page.Content, Ascii("\nendstream"));
            if (imageObj > 0)
                Obj(imageObj, Ascii($"<< /Type /XObject /Subtype /Image /Width {page.ImageW} /Height {page.ImageH} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg!.Length} >>\nstream\n"),
                    page.Jpeg, Ascii("\nendstream"));
        }
        var xref = stream.Position;
        var table = new StringBuilder();
        table.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) table.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        table.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(Ascii(table.ToString()));
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);
}

/// <summary>PDF pages as images (Windows PDF renderer), PDF text (PdfPig) and PDF → DOCX.</summary>
internal static class PdfEngine
{
    public const int MaxPages = 500;

    public static async Task<List<string>> RenderPagesAsync(string pdf, FileFormat to, string outDir, string baseName, ConversionOptions options, IProgress<double> progress, CancellationToken token)
    {
        Windows.Data.Pdf.PdfDocument document;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(pdf);
            document = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception error) when (error.HResult == unchecked((int)0x8007052B) || error.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConversionException("ff.err.pdfLocked");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new ConversionException("ff.err.damaged");
        }
        if (document.IsPasswordProtected) throw new ConversionException("ff.err.pdfLocked");
        var count = (int)Math.Min(document.PageCount, MaxPages);
        var outputs = new List<string>();
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            using var page = document.GetPage((uint)i);
            // About 150 dpi by default; a chosen image size limits the long side.
            var scale = 150.0 / 96;
            var longSide = Math.Max(page.Size.Width, page.Size.Height) * scale;
            if (options.ImageMaxSide > 0 && longSide > options.ImageMaxSide) scale *= options.ImageMaxSide / longSide;
            using var memory = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var render = new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
                DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
            };
            await page.RenderToStreamAsync(memory, render);
            memory.Seek(0);
            var image = DecodeStream(System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(memory));
            var name = Path.Combine(outDir, $"{baseName}-{(i + 1).ToString(count >= 100 ? "D3" : "D2", CultureInfo.InvariantCulture)}{FileFlowCatalog.Info(to).Extension}");
            if (to == FileFormat.Jpg) ImageEngine.Save(new JpegBitmapEncoder { QualityLevel = options.ImageQuality }, ImageEngine.Flatten(image), name);
            else ImageEngine.Save(new PngBitmapEncoder(), image, name);
            outputs.Add(name);
            progress.Report((i + 1) / (double)count);
        }
        if (outputs.Count == 0) throw new ConversionException("ff.err.pdfEmpty");
        return outputs;
    }

    private static BitmapSource DecodeStream(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource image = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        image = new WriteableBitmap(image);
        image.Freeze();
        return image;
    }

    /// <summary>Text of every page in reading order (PdfPig).</summary>
    public static List<string> ExtractPages(string pdf, CancellationToken token)
    {
        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
            var pages = new List<string>();
            foreach (var page in document.GetPages().Take(MaxPages))
            {
                token.ThrowIfCancellationRequested();
                pages.Add(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page).Trim());
            }
            if (pages.All(string.IsNullOrWhiteSpace)) throw new ConversionException("ff.err.pdfNoText");
            return pages;
        }
        catch (Exception error) when (error is not ConversionException and not OperationCanceledException)
        {
            throw new ConversionException(error.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase)
                ? "ff.err.pdfLocked" : "ff.err.damaged");
        }
    }

    public static void ToText(string pdf, string output, CancellationToken token) =>
        File.WriteAllText(output, string.Join("\n\n\f", ExtractPages(pdf, token)) + "\n", new UTF8Encoding(true));

    /// <summary>The text of each page as paragraphs; pages are separated by page breaks. The layout is simplified.</summary>
    public static void ToDocx(string pdf, string output, CancellationToken token)
    {
        var pages = ExtractPages(pdf, token).Select(page => page.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.None)
            .Select(paragraph => paragraph.Replace('\n', ' ').Trim()).Where(p => p.Length > 0).ToList()).ToList();
        DocxDocument.Write(output, pages);
    }
}

/// <summary>Minimal Word (DOCX) reading and writing without Office.</summary>
public static class DocxDocument
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Plain text of a DOCX: paragraphs as lines, tabs and table cells as tabs.</summary>
    public static string ReadText(string path)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml") ?? throw new ConversionException("ff.err.damaged");
            using var stream = entry.Open();
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            var text = new StringBuilder();
            while (reader.Read())
            {
                if (reader.NamespaceURI != W) continue;
                if (reader.NodeType == System.Xml.XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "t": text.Append(reader.ReadElementContentAsString()); break;
                        case "tab": text.Append('\t'); break;
                        case "br": case "cr": text.Append('\n'); break;
                    }
                }
                else if (reader.NodeType == System.Xml.XmlNodeType.EndElement)
                {
                    if (reader.LocalName == "p") text.Append('\n');
                    else if (reader.LocalName == "tc") text.Append('\t');
                }
            }
            return text.ToString().Replace("\t\n", "\n");
        }
        catch (Exception error) when (error is InvalidDataException or System.Xml.XmlException or IOException)
        {
            throw new ConversionException("ff.err.damaged");
        }
    }

    /// <summary>Writes paragraphs; every inner list becomes one page.</summary>
    public static void Write(string path, IReadOnlyList<IReadOnlyList<string>> pages)
    {
        var body = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            if (i > 0) body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
            foreach (var paragraph in pages[i])
                body.Append("<w:p><w:r><w:t xml:space=\"preserve\">").Append(System.Security.SecurityElement.Escape(Clean(paragraph))).Append("</w:t></w:r></w:p>");
        }
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create);
        void Entry(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        Entry("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Entry("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Entry("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{W}\"><w:body>{body}<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1418\" w:right=\"1418\" w:bottom=\"1134\" w:left=\"1418\" w:header=\"709\" w:footer=\"709\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>");
    }

    /// <summary>Removes characters XML 1.0 does not allow.</summary>
    private static string Clean(string text) => new(text.Where(c => c is '\t' or '\n' or '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF)).ToArray());
}

// ───────────────────────── office documents ─────────────────────────

/// <summary>Office, OpenDocument, RTF, HTML, Markdown, EPUB and text → PDF with LibreOffice (headless).</summary>
internal static class OfficeEngine
{
    private static readonly SemaphoreSlim LibreOfficeGate = new(1, 1); // one LibreOffice at a time

    /// <summary>LibreOffice settings only (no documents), shared by all conversions.</summary>
    public static readonly string ProfileFolder = Path.Combine(Path.GetTempPath(), "Freedom-FileFlow", "libreoffice-profile");

    public static async Task ToPdfAsync(string input, FileFormat from, string output, string workDir, ConversionOptions options, CancellationToken token)
    {
        var source = input;
        string? filter = null;
        switch (from)
        {
            case FileFormat.Markdown:
                source = Path.Combine(workDir, "document.html");
                await File.WriteAllTextAsync(source, MarkdownRenderer.ToHtmlDocument(await File.ReadAllTextAsync(input, token)), new UTF8Encoding(false), token);
                break;
            case FileFormat.Html:
                source = Path.Combine(workDir, "document.html");
                await File.WriteAllTextAsync(source, HtmlCleaner.Clean(await File.ReadAllTextAsync(input, token)), new UTF8Encoding(false), token);
                break;
            case FileFormat.Epub:
                source = EpubReader.ToHtml(input, Path.Combine(workDir, "epub"));
                break;
            case FileFormat.Txt:
                if (!ToolLocator.IsAvailable(ExternalTool.LibreOffice))
                {
                    // Works without LibreOffice too (Latin characters only).
                    var writer = new SimplePdfWriter();
                    writer.AddText(await File.ReadAllTextAsync(input, token), options.PageSize);
                    writer.Save(output);
                    return;
                }
                filter = "Text (encoded):UTF8,LF,,,";
                break;
        }
        var pdf = await ConvertWithLibreOfficeAsync(source, "pdf", workDir, token, filter);
        File.Move(pdf, output, overwrite: true);
    }

    /// <summary>Runs LibreOffice headless with its own profile (never touches a running LibreOffice) and returns the result file.</summary>
    public static async Task<string> ConvertWithLibreOfficeAsync(string input, string extension, string workDir, CancellationToken token, string? inputFilter = null)
    {
        var soffice = ToolLocator.Find(ExternalTool.LibreOffice) ?? throw new ConversionException("ff.err.needLibreOffice");
        var outDir = Path.Combine(workDir, "lo-out-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(ProfileFolder);
        var args = new List<string>
        {
            "-env:UserInstallation=" + new Uri(ProfileFolder + Path.DirectorySeparatorChar).AbsoluteUri,
            "--headless", "--invisible", "--nologo", "--norestore", "--nolockcheck", "--nodefault", "--nofirststartwizard"
        };
        if (inputFilter is not null) args.Add("--infilter=" + inputFilter);
        args.AddRange(new[] { "--convert-to", extension, "--outdir", outDir, input });
        await LibreOfficeGate.WaitAsync(token);
        try
        {
            var result = await ProcessRunner.RunAsync(soffice, args, TimeSpan.FromMinutes(5), token, workingDirectory: workDir);
            var produced = Directory.EnumerateFiles(outDir, "*." + extension).FirstOrDefault();
            if (produced is null || new FileInfo(produced).Length == 0)
                throw new ConversionException(result.Output.Contains("password", StringComparison.OrdinalIgnoreCase) ? "ff.err.pdfLocked" : "ff.err.office");
            return produced;
        }
        finally
        {
            LibreOfficeGate.Release();
        }
    }
}

/// <summary>Small Markdown → HTML renderer (headings, emphasis, code, lists, quotes, links, tables, rules).</summary>
public static class MarkdownRenderer
{
    private const string Style = "body{font-family:'Segoe UI',Arial,sans-serif;font-size:11pt;line-height:1.5;color:#1d1d1f}h1,h2,h3{color:#2b2d6e}code{font-family:Consolas,monospace;background:#f2f2f7}pre{background:#f2f2f7;padding:8pt}blockquote{border-left:3pt solid #8b8fe8;margin-left:0;padding-left:10pt;color:#555}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:4pt 8pt}";

    public static string ToHtmlDocument(string markdown) =>
        $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>{Style}</style></head><body>{ToHtml(markdown)}</body></html>";

    public static string ToHtml(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var html = new StringBuilder();
        var paragraph = new List<string>();
        string? list = null;
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            html.Append("<p>").Append(Inline(string.Join(" ", paragraph))).Append("</p>\n");
            paragraph.Clear();
        }
        void CloseList()
        {
            if (list is null) return;
            html.Append($"</{list}>\n");
            list = null;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```"))
            {
                FlushParagraph();
                CloseList();
                html.Append("<pre><code>");
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```"); i++) html.Append(Escape(lines[i])).Append('\n');
                html.Append("</code></pre>\n");
                continue;
            }
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                CloseList();
                continue;
            }
            var heading = Regex.Match(trimmed, @"^(#{1,6})\s+(.*?)\s*#*$");
            if (heading.Success)
            {
                FlushParagraph();
                CloseList();
                var level = heading.Groups[1].Length;
                html.Append($"<h{level}>").Append(Inline(heading.Groups[2].Value)).Append($"</h{level}>\n");
                continue;
            }
            if (Regex.IsMatch(trimmed, @"^(\*\s*){3,}$|^(-\s*){3,}$|^(_\s*){3,}$"))
            {
                FlushParagraph();
                CloseList();
                html.Append("<hr/>\n");
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                CloseList();
                html.Append("<blockquote>").Append(Inline(trimmed.TrimStart('>').Trim())).Append("</blockquote>\n");
                continue;
            }
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1].Trim(), @"^\|?\s*:?-{2,}"))
            {
                FlushParagraph();
                CloseList();
                html.Append("<table>\n<tr>");
                foreach (var cell in Cells(trimmed)) html.Append("<th>").Append(Inline(cell)).Append("</th>");
                html.Append("</tr>\n");
                for (i += 2; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                {
                    html.Append("<tr>");
                    foreach (var cell in Cells(lines[i].Trim())) html.Append("<td>").Append(Inline(cell)).Append("</td>");
                    html.Append("</tr>\n");
                }
                i--;
                html.Append("</table>\n");
                continue;
            }
            var bullet = Regex.Match(trimmed, @"^[-*+]\s+(.*)$");
            var number = Regex.Match(trimmed, @"^\d+[.)]\s+(.*)$");
            if (bullet.Success || number.Success)
            {
                FlushParagraph();
                var kind = bullet.Success ? "ul" : "ol";
                if (list != kind)
                {
                    CloseList();
                    html.Append($"<{kind}>\n");
                    list = kind;
                }
                html.Append("<li>").Append(Inline((bullet.Success ? bullet : number).Groups[1].Value)).Append("</li>\n");
                continue;
            }
            CloseList();
            paragraph.Add(trimmed);
        }
        FlushParagraph();
        CloseList();
        return html.ToString();
    }

    private static IEnumerable<string> Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim());

    private static string Inline(string text)
    {
        var html = Escape(text);
        html = Regex.Replace(html, @"`([^`]+)`", "<code>$1</code>");
        html = Regex.Replace(html, @"!\[([^\]]*)\]\(([^)\s]+)\)", m => SafeUrl(m.Groups[2].Value) is { } url ? $"<img alt=\"{m.Groups[1].Value}\" src=\"{url}\"/>" : m.Groups[1].Value);
        html = Regex.Replace(html, @"\[([^\]]+)\]\(([^)\s]+)\)", m => SafeUrl(m.Groups[2].Value) is { } url ? $"<a href=\"{url}\">{m.Groups[1].Value}</a>" : m.Groups[1].Value);
        html = Regex.Replace(html, @"\*\*(.+?)\*\*|__(.+?)__", m => $"<strong>{m.Groups[1].Value}{m.Groups[2].Value}</strong>");
        html = Regex.Replace(html, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])|(?<!\w)_(?!\s)(.+?)(?<!\s)_(?!\w)", m => $"<em>{m.Groups[1].Value}{m.Groups[2].Value}</em>");
        html = Regex.Replace(html, @"~~(.+?)~~", "<del>$1</del>");
        return html;
    }

    /// <summary>Only web and mail links – no javascript: or file: URLs.</summary>
    private static string? SafeUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? url : null;

    private static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text);
}

/// <summary>Removes active content from HTML before LibreOffice reads it.</summary>
public static class HtmlCleaner
{
    public static string Clean(string html)
    {
        html = Regex.Replace(html, @"<(script|iframe|object|embed|applet|frameset|frame)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, @"<(script|iframe|object|embed|applet|frame|meta\s+http-equiv)\b[^>]*/?>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"\son\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"(href|src)\s*=\s*([""'])\s*(javascript|vbscript|file):[^""']*\2", "$1=$2#$2", RegexOptions.IgnoreCase);
        return html;
    }
}

/// <summary>Turns an EPUB into one HTML file in reading order (with its images), ready for LibreOffice.</summary>
public static class EpubReader
{
    public static string ToHtml(string epub, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            using var zip = System.IO.Compression.ZipFile.OpenRead(epub);
            var container = Xml(zip, "META-INF/container.xml");
            var opfPath = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value ?? throw new ConversionException("ff.err.damaged");
            var opf = Xml(zip, opfPath);
            var baseDir = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
            var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
                .ToDictionary(e => e.Attribute("id")?.Value ?? "", e => (Href: Uri.UnescapeDataString(e.Attribute("href")?.Value ?? ""), Type: e.Attribute("media-type")?.Value ?? ""));
            var title = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "";
            var body = new StringBuilder();
            // Images are extracted next to the HTML under safe, generated names.
            var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (href, type) in manifest.Values.Where(m => m.Type.StartsWith("image/")))
            {
                var entry = zip.GetEntry(Combine(baseDir, href));
                if (entry is null || entry.Length > 50_000_000) continue;
                var name = "img" + images.Count.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(href).ToLowerInvariant();
                System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, Path.Combine(folder, name), true);
                images[Combine(baseDir, href)] = name;
            }
            foreach (var itemref in opf.Descendants().Where(e => e.Name.LocalName == "itemref"))
            {
                if (!manifest.TryGetValue(itemref.Attribute("idref")?.Value ?? "", out var item)) continue;
                var path = Combine(baseDir, item.Href);
                var entry = zip.GetEntry(path);
                if (entry is null) continue;
                using var reader = new StreamReader(entry.Open());
                var xhtml = reader.ReadToEnd();
                var match = Regex.Match(xhtml, @"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var chapter = match.Success ? match.Groups[1].Value : xhtml;
                var chapterDir = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
                chapter = Regex.Replace(chapter, @"(src|xlink:href)\s*=\s*([""'])([^""']+)\2", m =>
                {
                    var target = Combine(chapterDir, Uri.UnescapeDataString(m.Groups[3].Value));
                    return images.TryGetValue(target, out var local) ? $"{m.Groups[1].Value}={m.Groups[2].Value}{local}{m.Groups[2].Value}" : "";
                }, RegexOptions.IgnoreCase);
                body.Append("<div style=\"page-break-before:always\">").Append(chapter).Append("</div>\n");
            }
            if (body.Length == 0) throw new ConversionException("ff.err.damaged");
            var html = Path.Combine(folder, "book.html");
            File.WriteAllText(html, HtmlCleaner.Clean($"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{System.Net.WebUtility.HtmlEncode(title)}</title><style>body{{font-family:Georgia,serif;font-size:11pt;line-height:1.5}}img{{max-width:100%}}</style></head><body>{body}</body></html>"),
                new UTF8Encoding(false));
            return html;
        }
        catch (Exception error) when (error is InvalidDataException or System.Xml.XmlException or IOException or ArgumentException)
        {
            throw new ConversionException("ff.err.damaged");
        }
    }

    private static System.Xml.Linq.XDocument Xml(System.IO.Compression.ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? throw new ConversionException("ff.err.damaged");
        using var stream = entry.Open();
        using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null });
        return System.Xml.Linq.XDocument.Load(reader);
    }

    /// <summary>Joins ZIP paths and resolves "..", never leaving the archive root.</summary>
    private static string Combine(string baseDir, string href)
    {
        var parts = new List<string>();
        foreach (var part in (baseDir + href.Split('#')[0]).Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return string.Join("/", parts);
    }
}
