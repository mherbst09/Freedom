using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ViennaAtomicClock.FileFlow;
using Xunit;

namespace Freedom.Tests;

/// <summary>Creates real sample files (images with WPF, media with FFmpeg, office files with LibreOffice).</summary>
internal static class Samples
{
    public static string Image(string folder, string name, int width = 64, int height = 48, bool transparent = false)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(x * 4);
                pixels[i + 1] = (byte)(y * 5);
                pixels[i + 2] = 200;
                pixels[i + 3] = transparent && x < width / 2 ? (byte)0 : (byte)255;
            }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        BitmapEncoder encoder = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        encoder.Frames.Add(BitmapFrame.Create(image));
        var path = Path.Combine(folder, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    public static string Ffmpeg => ToolLocator.Find(ExternalTool.FFmpeg)!;

    /// <summary>Two seconds of test picture and/or tone in any container FFmpeg can write.</summary>
    public static string Media(string folder, string name, bool video, bool audio = true, double seconds = 2, string size = "320x240")
    {
        var path = Path.Combine(folder, name);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (video) args.AddRange(new[] { "-f", "lavfi", "-i", $"testsrc=duration={seconds.ToString(CultureInfo.InvariantCulture)}:size={size}:rate=25" });
        if (audio) args.AddRange(new[] { "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds.ToString(CultureInfo.InvariantCulture)}" });
        var ext = Path.GetExtension(name).ToLowerInvariant();
        args.AddRange(ext switch
        {
            ".webm" => new[] { "-c:v", "libvpx", "-c:a", "libvorbis" },
            ".flv" => new[] { "-c:v", "flv", "-c:a", "mp3", "-ar", "44100" },
            ".avi" => new[] { "-c:v", "mpeg4", "-c:a", "mp3" },
            ".wma" => new[] { "-c:a", "wmav2" },
            ".ogg" => new[] { "-c:a", "libvorbis" },
            ".m4a" or ".aac" => new[] { "-c:a", "aac" },
            ".gif" => Array.Empty<string>(),
            _ => Array.Empty<string>()
        });
        if (video && audio) args.Add("-shortest");
        args.Add(path);
        var result = ProcessRunner.RunAsync(Ffmpeg, args, TimeSpan.FromMinutes(2), CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(result.Success, result.Output);
        return path;
    }

    public static (double Duration, string Streams) Probe(string path)
    {
        var ffprobe = ToolLocator.Find(ExternalTool.FFprobe)!;
        var result = ProcessRunner.RunAsync(ffprobe, new[] { "-v", "error", "-show_entries", "format=duration,format_name:stream=codec_name,width,height", "-of", "default=nw=1", path },
            TimeSpan.FromSeconds(30), CancellationToken.None).GetAwaiter().GetResult();
        var duration = 0.0;
        foreach (var line in result.Output.Split('\n'))
            if (line.StartsWith("duration=")) double.TryParse(line[9..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        return (duration, result.Output);
    }

    public static string PdfText(string path)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        return string.Join("\n", document.GetPages().Select(p => p.Text));
    }

    public static int PdfPages(string path)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        return document.NumberOfPages;
    }
}

public sealed class FileFlowCatalogTests
{
    [Fact]
    public void All_47_requested_conversions_are_offered()
    {
        Assert.Equal(47, FileFlowCatalog.Conversions.Count);
        Assert.Equal(47, FileFlowCatalog.Conversions.Select(c => c.Id).Distinct().Count());
        Assert.Equal(7, FileFlowCatalog.Conversions.Count(c => c.Featured));
        var expected = new (FileFormat, FileFormat)[]
        {
            (FileFormat.Mp4, FileFormat.Mp3), (FileFormat.YouTube, FileFormat.Mp3), (FileFormat.SoundCloud, FileFormat.Mp3), (FileFormat.Png, FileFormat.Pdf),
            (FileFormat.Jpg, FileFormat.Pdf), (FileFormat.Txt, FileFormat.Pdf), (FileFormat.Docx, FileFormat.Pdf),
            (FileFormat.Mov, FileFormat.Mp4), (FileFormat.Avi, FileFormat.Mp4), (FileFormat.Mkv, FileFormat.Mp4), (FileFormat.Webm, FileFormat.Mp4), (FileFormat.Flv, FileFormat.Mp4),
            (FileFormat.M4v, FileFormat.Mp4), (FileFormat.Mp4, FileFormat.Wav), (FileFormat.Mp4, FileFormat.Aac), (FileFormat.Wav, FileFormat.Mp3), (FileFormat.M4a, FileFormat.Mp3),
            (FileFormat.Aac, FileFormat.Mp3), (FileFormat.Flac, FileFormat.Mp3), (FileFormat.Ogg, FileFormat.Mp3), (FileFormat.Wma, FileFormat.Mp3), (FileFormat.Mp3, FileFormat.Wav),
            (FileFormat.Mp3, FileFormat.M4a), (FileFormat.Png, FileFormat.Jpg), (FileFormat.Jpg, FileFormat.Png), (FileFormat.Png, FileFormat.Webp), (FileFormat.Jpg, FileFormat.Webp),
            (FileFormat.Webp, FileFormat.Jpg), (FileFormat.Webp, FileFormat.Png), (FileFormat.Heic, FileFormat.Jpg), (FileFormat.Heic, FileFormat.Png), (FileFormat.Gif, FileFormat.Mp4),
            (FileFormat.Svg, FileFormat.Png), (FileFormat.Bmp, FileFormat.Png), (FileFormat.Tiff, FileFormat.Jpg), (FileFormat.Pdf, FileFormat.Jpg), (FileFormat.Pdf, FileFormat.Png),
            (FileFormat.Pdf, FileFormat.Txt), (FileFormat.Pdf, FileFormat.Docx), (FileFormat.Docx, FileFormat.Txt), (FileFormat.Pptx, FileFormat.Pdf), (FileFormat.Xlsx, FileFormat.Pdf),
            (FileFormat.Odt, FileFormat.Pdf), (FileFormat.Rtf, FileFormat.Pdf), (FileFormat.Html, FileFormat.Pdf), (FileFormat.Markdown, FileFormat.Pdf), (FileFormat.Epub, FileFormat.Pdf)
        };
        foreach (var (from, to) in expected) Assert.True(FileFlowCatalog.IsSupported(from, to), $"{from} → {to}");
    }

    [Fact]
    public void Default_targets_fit_the_format()
    {
        Assert.Equal(FileFormat.Mp3, FileFlowCatalog.DefaultTarget(FileFormat.Mp4));
        Assert.Equal(FileFormat.Pdf, FileFlowCatalog.DefaultTarget(FileFormat.Jpg));
        Assert.Equal(FileFormat.Mp4, FileFlowCatalog.DefaultTarget(FileFormat.Mov));
        Assert.Contains(FileFormat.Txt, FileFlowCatalog.TargetsFor(FileFormat.Pdf));
    }
}

public sealed class FormatDetectorTests
{
    private static FileFormat Bytes(string hex, string ext = "") =>
        FormatDetector.FromBytes(Convert.FromHexString(hex.Replace(" ", "")), FileFlowCatalog.FromExtension(ext));

    private static FileFormat Text(string text, string ext) => FormatDetector.FromBytes(Encoding.UTF8.GetBytes(text), FileFlowCatalog.FromExtension(ext));

    [Fact]
    public void Signatures_decide_not_the_extension()
    {
        Assert.Equal(FileFormat.Png, Bytes("89504E470D0A1A0A0000", ".jpg"));
        Assert.Equal(FileFormat.Jpg, Bytes("FFD8FFE000104A464946", ".png"));
        Assert.Equal(FileFormat.Gif, Bytes("474946383961", ".bmp"));
        Assert.Equal(FileFormat.Pdf, Bytes("255044462D312E37", ".txt"));
        Assert.Equal(FileFormat.Webp, Bytes("52494646 00000000 57454250 5650384C"));
        Assert.Equal(FileFormat.Wav, Bytes("52494646 00000000 57415645 666D7420"));
        Assert.Equal(FileFormat.Avi, Bytes("52494646 00000000 41564920 4C495354"));
        Assert.Equal(FileFormat.Flac, Bytes("664C6143 00000022"));
        Assert.Equal(FileFormat.Ogg, Bytes("4F676753 00020000"));
        Assert.Equal(FileFormat.Mp3, Bytes("494433 04000000"));
        Assert.Equal(FileFormat.Mp3, Bytes("FFFB9064 0000"));
        Assert.Equal(FileFormat.Aac, Bytes("FFF15080 0000"));
        Assert.Equal(FileFormat.Wma, Bytes("3026B2758E66CF11A6D9"));
        Assert.Equal(FileFormat.Tiff, Bytes("49492A00 08000000"));
        Assert.Equal(FileFormat.Flv, Bytes("464C5601 05000000"));
        Assert.Equal(FileFormat.Mkv, Bytes("1A45DFA3 9F4286810142F7810142F2810442F381084282886D6174726F736B61"));
        Assert.Equal(FileFormat.Webm, Bytes("1A45DFA3 9F4286810142F7810142F2810442F381084282847765626D"));
        Assert.Equal(FileFormat.Mp4, Bytes("00000020 66747970 69736F6D 00000200"));
        Assert.Equal(FileFormat.M4a, Bytes("00000020 66747970 4D344120 00000000"));
        Assert.Equal(FileFormat.M4a, Bytes("00000020 66747970 6D703432 00000000", ".m4a")); // generic brand, name decides
        Assert.Equal(FileFormat.Mov, Bytes("00000014 66747970 71742020 00000000"));
        Assert.Equal(FileFormat.Heic, Bytes("00000018 66747970 68656963 00000000"));
        Assert.Equal(FileFormat.Rtf, Text("{\\rtf1\\ansi Hallo}", ".txt"));
    }

    [Fact]
    public void Text_formats_use_the_content_and_the_name()
    {
        Assert.Equal(FileFormat.Svg, Text("<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", ".txt"));
        Assert.Equal(FileFormat.Html, Text("<!DOCTYPE html><html><body>Hi</body></html>", ".txt"));
        Assert.Equal(FileFormat.Markdown, Text("# Titel\n\nText", ".md"));
        Assert.Equal(FileFormat.Txt, Text("Grüße aus Wien", ".txt"));
        Assert.Equal(FileFormat.Txt, Text("a;b;c\n1;2;3", ".csv"));
        Assert.Equal(FileFormat.Unknown, Bytes("00010203040506070809"));
    }

    [Fact]
    public void Programs_and_scripts_are_refused()
    {
        Assert.Equal(FileFormat.Executable, Bytes("4D5A90000300000004000000", ".png"));
        Assert.Equal(FileFormat.Executable, Bytes("7F454C46020101000000", ".mp4"));
        Assert.Equal(FileFormat.Executable, Text("#!/bin/sh\nrm -rf /", ".txt"));
        using var folder = new TempFolder();
        var script = Path.Combine(folder.Path, "run.bat");
        File.WriteAllText(script, "echo hi");
        Assert.Equal(FileFormat.Executable, FormatDetector.Detect(script).Format);
        var disguised = Path.Combine(folder.Path, "photo.jpg");
        File.WriteAllBytes(disguised, Convert.FromHexString("4D5A9000030000000400"));
        var detection = FormatDetector.Detect(disguised);
        Assert.Equal(FileFormat.Executable, detection.Format);
        Assert.False(detection.ExtensionMatches);
    }

    [Fact]
    public void Zip_containers_are_told_apart_by_their_content()
    {
        using var folder = new TempFolder();
        var docx = Path.Combine(folder.Path, "brief.zip");
        DocxDocument.Write(docx, new[] { new[] { "Hallo" } });
        Assert.Equal(FileFormat.Docx, FormatDetector.Detect(docx).Format);
        var epub = Path.Combine(folder.Path, "book.docx");
        using (var zip = ZipFile.Open(epub, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("mimetype").Open());
            writer.Write("application/epub+zip");
        }
        Assert.Equal(FileFormat.Epub, FormatDetector.Detect(epub).Format);
        var plainZip = Path.Combine(folder.Path, "stuff.zip");
        using (var zip = ZipFile.Open(plainZip, ZipArchiveMode.Create)) zip.CreateEntry("a.txt");
        Assert.Equal(FileFormat.Unknown, FormatDetector.Detect(plainZip).Format);
    }
}

public sealed class FileFlowServiceTests
{
    private static ConversionOptions Options => new();

    [Fact]
    public void Bad_files_become_rows_with_a_clear_reason()
    {
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var empty = Path.Combine(folder.Path, "leer.txt");
        File.WriteAllText(empty, "");
        var exe = Path.Combine(folder.Path, "virus.png");
        File.WriteAllBytes(exe, Convert.FromHexString("4D5A9000030000000400000000000000"));
        var unknown = Path.Combine(folder.Path, "data.bin");
        File.WriteAllBytes(unknown, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 });

        Assert.Equal("ff.err.empty", service.AddFile(empty).ErrorKey);
        Assert.Equal("ff.err.executable", service.AddFile(exe).ErrorKey);
        Assert.Equal("ff.err.unsupported", service.AddFile(unknown).ErrorKey);
        Assert.Equal("ff.err.missing", service.AddFile(Path.Combine(folder.Path, "gibtsnicht.mp4")).ErrorKey);
        Assert.All(service.Jobs, j => Assert.Equal(JobState.Failed, j.State));
        Assert.All(service.Jobs, j => Assert.False(string.IsNullOrWhiteSpace(j.Message)));
    }

    [Fact]
    public void A_good_file_is_ready_with_the_preferred_or_usual_target()
    {
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var png = Samples.Image(folder.Path, "bild.png");
        var job = service.AddFile(png);
        Assert.Equal(JobState.Ready, job.State);
        Assert.Equal(FileFormat.Png, job.Format);
        Assert.Equal(FileFormat.Pdf, job.Target);
        Assert.Equal(FileFormat.Webp, service.AddFile(png, FileFormat.Webp).Target);
        Assert.Equal(FileFormat.Pdf, service.AddFile(png, FileFormat.Mp3).Target); // impossible choice falls back
        Assert.Contains(FileFormat.Jpg, job.PossibleTargets);
    }

    [Fact]
    public async Task Images_convert_with_quality_size_and_transparency()
    {
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var png = Samples.Image(folder.Path, "logo.png", 400, 200, transparent: true);
        var jpg = service.AddFile(png, FileFormat.Jpg);
        await service.RunNowAsync(jpg, Options with { ImageMaxSide = 100, ImageQuality = 75 });
        Assert.Equal(JobState.Done, jpg.State);
        var result = ImageEngineDecode(jpg.Outputs.Single());
        Assert.Equal(100, result.PixelWidth);
        Assert.Equal(50, result.PixelHeight);
        Assert.Equal("logo.jpg", Path.GetFileName(jpg.Outputs[0]));
        var pixel = new byte[4];
        result.CopyPixels(new System.Windows.Int32Rect(5, 25, 1, 1), pixel, 4, 0);
        Assert.True(pixel[0] > 240 && pixel[1] > 240 && pixel[2] > 240, "transparent areas become white");

        foreach (var (name, target) in new[] { ("a.bmp", FileFormat.Png), ("b.tif", FileFormat.Jpg), ("c.jpg", FileFormat.Png) })
        {
            var job = service.AddFile(Samples.Image(folder.Path, name), target);
            await service.RunNowAsync(job, Options);
            Assert.True(job.State == JobState.Done, $"{name}: {job.Message}");
            Assert.Equal(target, FormatDetector.Detect(job.Outputs[0]).Format);
        }
    }

    private static BitmapSource ImageEngineDecode(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
    }

    [Fact]
    public async Task Webp_goes_both_ways()
    {
        if (!ToolLocator.IsAvailable(ExternalTool.FFmpeg)) return;
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var toWebp = service.AddFile(Samples.Image(folder.Path, "foto.jpg", 120, 80), FileFormat.Webp);
        await service.RunNowAsync(toWebp, Options);
        Assert.True(toWebp.State == JobState.Done, toWebp.Message);
        Assert.Equal(FileFormat.Webp, FormatDetector.Detect(toWebp.Outputs[0]).Format);

        var copy = Path.Combine(folder.Path, "zurueck.webp");
        File.Copy(toWebp.Outputs[0], copy);
        var back = service.AddFile(copy, FileFormat.Png);
        await service.RunNowAsync(back, Options);
        Assert.True(back.State == JobState.Done, back.Message);
        Assert.Equal(120, ImageEngineDecode(back.Outputs[0]).PixelWidth);
    }

    [Fact]
    public async Task Svg_becomes_png()
    {
        if (!ToolLocator.IsAvailable(ExternalTool.FFmpeg) && !ToolLocator.IsAvailable(ExternalTool.LibreOffice)) return;
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var svg = Path.Combine(folder.Path, "icon.svg");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"80\"><rect width=\"120\" height=\"80\" fill=\"#3355ff\"/><circle cx=\"60\" cy=\"40\" r=\"30\" fill=\"#ffcc00\"/></svg>");
        var job = service.AddFile(svg);
        Assert.Equal(FileFormat.Svg, job.Format);
        await service.RunNowAsync(job, Options);
        Assert.True(job.State == JobState.Done, job.Message);
        Assert.Equal(FileFormat.Png, FormatDetector.Detect(job.Outputs[0]).Format);
    }

    [Fact]
    public async Task Several_images_become_one_sorted_pdf()
    {
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var wide = service.AddFile(Samples.Image(folder.Path, "1-breit.png", 300, 100));
        var tall = service.AddFile(Samples.Image(folder.Path, "2-hoch.jpg", 100, 300));
        var third = service.AddFile(Samples.Image(folder.Path, "3-klein.png", 50, 50), FileFormat.Pdf);
        service.Move(third, -2); // the small one first
        Assert.Same(third, service.Jobs[0]);
        service.StartAll(Options with { PageSize = PdfPageSize.A4, MergeImages = true });
        var bundle = Assert.Single(service.Jobs);
        Assert.Equal(JobKind.ImageBundle, bundle.Kind);
        await WaitAsync(() => !bundle.IsActive);
        Assert.True(bundle.State == JobState.Done, bundle.Message);
        Assert.Equal("3-klein.pdf", Path.GetFileName(bundle.Outputs[0]));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bundle.Outputs[0]);
        Assert.Equal(3, pdf.NumberOfPages);
        Assert.Equal(595, (int)pdf.GetPage(1).Width); // square → portrait A4
        Assert.Equal(841, (int)pdf.GetPage(2).Width); // wide image → landscape A4
        Assert.Equal(595, (int)pdf.GetPage(3).Width);

        var auto = service.AddFile(Samples.Image(folder.Path, "auto.png", 192, 96));
        await service.RunNowAsync(auto, Options with { PageSize = PdfPageSize.Auto });
        using var autoPdf = UglyToad.PdfPig.PdfDocument.Open(auto.Outputs[0]);
        Assert.Equal(144, (int)Math.Round(autoPdf.GetPage(1).Width));
        Assert.Equal(72, (int)Math.Round(autoPdf.GetPage(1).Height));

        var letter = service.AddFile(Samples.Image(folder.Path, "letter.png", 100, 120));
        await service.RunNowAsync(letter, Options with { PageSize = PdfPageSize.Letter });
        using var letterPdf = UglyToad.PdfPig.PdfDocument.Open(letter.Outputs[0]);
        Assert.Equal(612, (int)letterPdf.GetPage(1).Width);
    }

    [Fact]
    public void The_pdf_writer_handles_long_text_and_special_characters()
    {
        using var folder = new TempFolder();
        var writer = new SimplePdfWriter();
        var text = string.Join("\n", Enumerable.Range(1, 150).Select(i => $"Zeile {i}: Grüße (Klammern) \\ Backslash – " + new string('x', i % 120)));
        writer.AddText(text, PdfPageSize.A4);
        var path = Path.Combine(folder.Path, "text.pdf");
        writer.Save(path);
        Assert.True(Samples.PdfPages(path) >= 3);
        var extracted = Samples.PdfText(path);
        Assert.Contains("Grüße", extracted);
        Assert.Contains("(Klammern)", extracted);
        Assert.Contains("Zeile 150", extracted);
    }

    [Fact]
    public async Task Pdf_becomes_text_word_and_pictures()
    {
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var writer = new SimplePdfWriter();
        writer.AddText("Erste Seite mit Text.\n\nZweiter Absatz.", PdfPageSize.A4);
        writer.AddText("Zweite Seite.", PdfPageSize.A4);
        var pdf = Path.Combine(folder.Path, "bericht.pdf");
        writer.Save(pdf);

        var txt = service.AddFile(pdf, FileFormat.Txt);
        await service.RunNowAsync(txt, Options);
        Assert.True(txt.State == JobState.Done, txt.Message);
        var text = File.ReadAllText(txt.Outputs[0]);
        Assert.Contains("Erste Seite mit Text.", text);
        Assert.Contains("Zweite Seite.", text);

        var docx = service.AddFile(pdf, FileFormat.Docx);
        await service.RunNowAsync(docx, Options);
        Assert.True(docx.State == JobState.Done, docx.Message);
        Assert.Equal(FileFormat.Docx, FormatDetector.Detect(docx.Outputs[0]).Format);
        Assert.Contains("Zweiter Absatz.", DocxDocument.ReadText(docx.Outputs[0]));

        foreach (var target in new[] { FileFormat.Png, FileFormat.Jpg })
        {
            var pictures = service.AddFile(pdf, target);
            await service.RunNowAsync(pictures, Options);
            Assert.True(pictures.State == JobState.Done, pictures.Message);
            Assert.Equal(2, pictures.Outputs.Count);
            Assert.All(pictures.Outputs, o => Assert.Equal(target, FormatDetector.Detect(o).Format));
            Assert.Equal("bericht-01" + FileFlowCatalog.Info(target).Extension, Path.GetFileName(pictures.Outputs[0]));
        }
    }

    [Fact]
    public async Task Pdf_without_text_and_broken_pdf_fail_clearly()
    {
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var writer = new SimplePdfWriter();
        writer.AddImagePage(File.ReadAllBytes(Samples.Image(folder.Path, "scan.jpg")), 64, 48, PdfPageSize.A4);
        var scanned = Path.Combine(folder.Path, "scan.pdf");
        writer.Save(scanned);
        var job = service.AddFile(scanned, FileFormat.Txt);
        await service.RunNowAsync(job, Options);
        Assert.Equal("ff.err.pdfNoText", job.ErrorKey);

        var broken = Path.Combine(folder.Path, "kaputt.pdf");
        File.WriteAllText(broken, "%PDF-1.7\nthis is not a pdf");
        var brokenJob = service.AddFile(broken, FileFormat.Png);
        await service.RunNowAsync(brokenJob, Options);
        Assert.Equal(JobState.Failed, brokenJob.State);
        Assert.Equal("ff.err.damaged", brokenJob.ErrorKey);
        Assert.False(Directory.Exists(Path.Combine(service.JobsFolder)) && Directory.EnumerateFiles(service.JobsFolder, "*", SearchOption.AllDirectories).Any(f => f.Contains("kaputt")));
    }

    [Fact]
    public async Task Word_becomes_text()
    {
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var docx = Path.Combine(folder.Path, "brief.docx");
        DocxDocument.Write(docx, new[] { new[] { "Sehr geehrte Damen & Herren,", "zweiter Absatz <mit> Zeichen." } });
        var job = service.AddFile(docx, FileFormat.Txt);
        await service.RunNowAsync(job, Options);
        Assert.True(job.State == JobState.Done, job.Message);
        var text = File.ReadAllText(job.Outputs[0]);
        Assert.Contains("Sehr geehrte Damen & Herren,\nzweiter Absatz <mit> Zeichen.", text.Replace("\r", ""));
    }

    [Fact]
    public async Task Audio_and_video_conversions_work_and_respect_the_options()
    {
        if (!ToolLocator.IsAvailable(ExternalTool.FFmpeg)) return;
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var cases = new (string Name, bool Video, FileFormat Target)[]
        {
            ("clip.mp4", true, FileFormat.Mp3), ("clip2.mp4", true, FileFormat.Wav), ("clip3.mp4", true, FileFormat.Aac),
            ("film.mov", true, FileFormat.Mp4), ("film.avi", true, FileFormat.Mp4), ("film.mkv", true, FileFormat.Mp4), ("film.webm", true, FileFormat.Mp4),
            ("film.flv", true, FileFormat.Mp4), ("film.m4v", true, FileFormat.Mp4),
            ("ton.wav", false, FileFormat.Mp3), ("ton.m4a", false, FileFormat.Mp3), ("ton.aac", false, FileFormat.Mp3), ("ton.flac", false, FileFormat.Mp3),
            ("ton.ogg", false, FileFormat.Mp3), ("ton.wma", false, FileFormat.Mp3), ("ton.mp3", false, FileFormat.Wav), ("ton2.mp3", false, FileFormat.M4a)
        };
        foreach (var (name, video, target) in cases)
        {
            var source = Samples.Media(folder.Path, name, video);
            var job = service.AddFile(source, target);
            Assert.True(job.State == JobState.Ready, $"{name}: detected {job.Format} – {job.Message}");
            await service.RunNowAsync(job, Options with { AudioBitrate = 128, VideoHeight = 120, VideoQuality = VideoQuality.Small });
            Assert.True(job.State == JobState.Done, $"{name} → {target}: {job.Message}");
            var (duration, streams) = Samples.Probe(job.Outputs[0]);
            Assert.InRange(duration, 1.5, 2.6);
            Assert.Equal(target, FormatDetector.Detect(job.Outputs[0]).Format);
            if (target == FileFormat.Mp4)
            {
                Assert.Contains("codec_name=h264", streams);
                Assert.Contains("height=120", streams); // resolution option
            }
        }
    }

    [Fact]
    public async Task Gif_becomes_mp4_and_a_video_without_sound_cannot_become_mp3()
    {
        if (!ToolLocator.IsAvailable(ExternalTool.FFmpeg)) return;
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var gif = Samples.Media(folder.Path, "anim.gif", video: true, audio: false, size: "101x75"); // odd size on purpose
        var job = service.AddFile(gif, FileFormat.Mp4);
        await service.RunNowAsync(job, Options);
        Assert.True(job.State == JobState.Done, job.Message);
        Assert.Contains("codec_name=h264", Samples.Probe(job.Outputs[0]).Streams);

        var silent = Samples.Media(folder.Path, "stumm.mp4", video: true, audio: false);
        var mp3 = service.AddFile(silent, FileFormat.Mp3);
        await service.RunNowAsync(mp3, Options);
        Assert.Equal("ff.err.noAudio", mp3.ErrorKey);
    }

    [Fact]
    public async Task A_broken_file_fails_alone_and_cancel_stops_cleanly()
    {
        if (!ToolLocator.IsAvailable(ExternalTool.FFmpeg)) return;
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var broken = Path.Combine(folder.Path, "kaputt.mp4");
        File.WriteAllBytes(broken, Convert.FromHexString("0000002066747970697336D000000200").Concat(Enumerable.Repeat((byte)0x55, 5000)).ToArray());
        var good = Samples.Media(folder.Path, "gut.mp4", video: true);
        var brokenJob = service.AddFile(broken, FileFormat.Mp3);
        var goodJob = service.AddFile(good, FileFormat.Mp3);
        await Task.WhenAll(service.RunNowAsync(brokenJob, Options), service.RunNowAsync(goodJob, Options));
        Assert.Equal(JobState.Failed, brokenJob.State);
        Assert.Equal("ff.err.damaged", brokenJob.ErrorKey);
        Assert.Equal(JobState.Done, goodJob.State);

        var big = Samples.Media(folder.Path, "lang.mov", video: true, seconds: 40, size: "1280x720");
        var slow = service.AddFile(big, FileFormat.Mp4);
        var run = service.RunNowAsync(slow, Options with { VideoQuality = VideoQuality.High });
        await WaitAsync(() => slow.State == JobState.Converting && slow.Progress > 0.01, 60);
        service.Cancel(slow);
        await run;
        Assert.Equal(JobState.Canceled, slow.State);
        Assert.Empty(slow.Outputs);
        await WaitAsync(() => Directory.GetDirectories(service.JobsFolder).Length == 1, 10); // only the finished good job keeps a folder
    }

    [Fact]
    public async Task At_most_two_conversions_run_at_the_same_time()
    {
        if (!ToolLocator.IsAvailable(ExternalTool.FFmpeg)) return;
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var jobs = Enumerable.Range(0, 5).Select(i => service.AddFile(Samples.Media(folder.Path, $"c{i}.mov", true, seconds: 6, size: "640x360"), FileFormat.Mp4)).ToList();
        var runs = jobs.Select(j => service.RunNowAsync(j, Options)).ToList();
        var maxBusy = 0;
        while (runs.Any(r => !r.IsCompleted))
        {
            maxBusy = Math.Max(maxBusy, jobs.Count(j => j.State is JobState.Importing or JobState.Converting));
            await Task.Delay(20);
        }
        Assert.InRange(maxBusy, 1, FileFlowService.MaxConcurrent);
        Assert.All(jobs, j => Assert.Equal(JobState.Done, j.State));
    }

    [Fact]
    public async Task Results_expire_after_one_hour_and_can_be_saved_or_zipped()
    {
        using var folder = new TempFolder();
        var now = DateTime.UtcNow;
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"), () => now);
        var a = service.AddFile(Samples.Image(folder.Path, "a.png"), FileFormat.Jpg);
        var b = service.AddFile(Samples.Image(folder.Path, "b.png"), FileFormat.Jpg);
        var c = service.AddFile(Samples.Image(folder.Path, "a.bmp"), FileFormat.Png);
        foreach (var job in new[] { a, b, c }) await service.RunNowAsync(job, Options);
        Assert.Equal(now + TimeSpan.FromHours(1), a.ExpiresUtc);

        var target = Directory.CreateDirectory(Path.Combine(folder.Path, "ziel")).FullName;
        var saved = FileFlowService.SaveOutputs(a, target);
        var again = FileFlowService.SaveOutputs(a, target);
        Assert.Equal("a.jpg", Path.GetFileName(saved[0]));
        Assert.Equal("a (2).jpg", Path.GetFileName(again[0]));
        var zipPath = Path.Combine(target, "alle.zip");
        FileFlowService.SaveZip(service.Jobs, zipPath);
        using (var zip = ZipFile.OpenRead(zipPath))
            Assert.Equal(new[] { "a.jpg", "b.jpg", "a.png" }, zip.Entries.Select(e => e.Name).ToArray());

        now += TimeSpan.FromMinutes(59);
        service.RemoveExpired();
        Assert.Equal(JobState.Done, a.State);
        now += TimeSpan.FromMinutes(2);
        service.RemoveExpired();
        Assert.All(service.Jobs, j => Assert.Equal(JobState.Expired, j.State));
        Assert.All(service.Jobs, j => Assert.Empty(j.Outputs));
        Assert.Empty(Directory.GetDirectories(service.JobsFolder));
        Assert.True(File.Exists(saved[0])); // saved copies belong to the user
    }

    [Fact]
    public void Leftovers_of_an_earlier_session_are_deleted_and_closing_cleans_up()
    {
        using var folder = new TempFolder();
        var work = Path.Combine(folder.Path, "work");
        var old = Directory.CreateDirectory(Path.Combine(work, "jobs", "abc123")).FullName;
        File.WriteAllText(Path.Combine(old, "secret.txt"), "x");
        var service = new FileFlowService(work);
        Assert.False(Directory.Exists(old));
        Directory.CreateDirectory(Path.Combine(service.JobsFolder, "new"));
        service.Dispose();
        Assert.False(Directory.Exists(service.JobsFolder));
    }

    [Fact]
    public void Temp_names_are_random_and_result_names_are_safe()
    {
        var names = Enumerable.Range(0, 200).Select(_ => FileFlowService.RandomName()).ToList();
        Assert.Equal(200, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches("^[0-9a-f]{24}$", n));
        Assert.Equal("Bericht_ 2024_", FileFlowService.SafeBaseName("Bericht: 2024?.docx"));
        Assert.Equal("datei", FileFlowService.SafeBaseName("....pdf"));
        Assert.Equal(80, FileFlowService.SafeBaseName(new string('a', 200) + ".txt").Length);
    }

    internal static async Task WaitAsync(Func<bool> condition, int seconds = 30)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("condition not reached");
            await Task.Delay(25);
        }
    }
}

public sealed class FileFlowLinkTests
{
    [Fact]
    public void Only_youtube_and_soundcloud_links_are_accepted()
    {
        Assert.Equal(FileFormat.YouTube, MediaEngine.ClassifyLink("https://www.youtube.com/watch?v=abc"));
        Assert.Equal(FileFormat.YouTube, MediaEngine.ClassifyLink("https://youtu.be/abc"));
        Assert.Equal(FileFormat.YouTube, MediaEngine.ClassifyLink("  https://music.youtube.com/watch?v=abc  "));
        Assert.Equal(FileFormat.SoundCloud, MediaEngine.ClassifyLink("https://soundcloud.com/artist/track"));
        Assert.Equal(FileFormat.SoundCloud, MediaEngine.ClassifyLink("https://on.soundcloud.com/xyz"));
        Assert.Equal(FileFormat.Unknown, MediaEngine.ClassifyLink("https://youtube.com.evil.example/watch"));
        Assert.Equal(FileFormat.Unknown, MediaEngine.ClassifyLink("file:///C:/Windows/win.ini"));
        Assert.Equal(FileFormat.Unknown, MediaEngine.ClassifyLink("javascript:alert(1)"));
        Assert.Equal(FileFormat.Unknown, MediaEngine.ClassifyLink("https://vimeo.com/123"));
        Assert.Equal(FileFormat.Unknown, MediaEngine.ClassifyLink(""));
    }

    [Fact]
    public void Links_need_the_rights_confirmation()
    {
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        Assert.Equal("ff.err.link.rights", service.AddLink("https://youtu.be/abc", rightsConfirmed: false).ErrorKey);
        Assert.Equal("ff.err.link.invalid", service.AddLink("https://example.com/song.mp3", rightsConfirmed: true).ErrorKey);
        var ok = service.AddLink("https://soundcloud.com/a/b", rightsConfirmed: true);
        Assert.Equal(JobState.Ready, ok.State);
        Assert.Equal(FileFormat.Mp3, ok.Target);
        Assert.False(ok.CanChangeTarget);
    }

    [Theory]
    [InlineData("ERROR: [youtube] abc: Private video. Sign in if you've been granted access", "ff.err.link.private")]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm your age", "ff.err.link.age")]
    [InlineData("ERROR: This video is DRM protected", "ff.err.link.drm")]
    [InlineData("ERROR: Join this channel to get access to members-only content", "ff.err.link.paid")]
    [InlineData("ERROR: [youtube] abc: Video unavailable", "ff.err.link.unavailable")]
    [InlineData("ERROR: The uploader has not made this video available in your country", "ff.err.link.blocked")]
    [InlineData("something else entirely", "ff.err.link.failed")]
    public void Link_errors_get_understandable_messages(string output, string key) => Assert.Equal(key, MediaEngine.MapLinkError(output).Key);
}

public sealed class FileFlowTextTests
{
    [Fact]
    public void Markdown_becomes_html_without_active_links()
    {
        var html = MarkdownRenderer.ToHtml("# Titel\n\nEin **fetter** und *kursiver* Text mit `code`.\n\n- eins\n- zwei\n\n1. a\n2. b\n\n> Zitat\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n[gut](https://example.com) [böse](javascript:alert(1)) <script>x</script>");
        Assert.Contains("<h1>Titel</h1>", html);
        Assert.Contains("<strong>fetter</strong>", html);
        Assert.Contains("<em>kursiver</em>", html);
        Assert.Contains("<code>code</code>", html);
        Assert.Contains("<ul>\n<li>eins</li>", html);
        Assert.Contains("<ol>", html);
        Assert.Contains("<blockquote>Zitat</blockquote>", html);
        Assert.Contains("<th>A</th>", html);
        Assert.Contains("<a href=\"https://example.com\">gut</a>", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void Html_is_cleaned_of_active_content()
    {
        var clean = HtmlCleaner.Clean("<p onclick=\"steal()\">Hi</p><script>alert(1)</script><iframe src=\"x\"></iframe><a href=\"javascript:bad()\">x</a><img src=x onerror=alert(1)>");
        Assert.DoesNotContain("script", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", clean);
        Assert.DoesNotContain("onerror", clean);
        Assert.DoesNotContain("iframe", clean);
        Assert.DoesNotContain("javascript", clean);
        Assert.Contains("<p>Hi</p>", clean);
    }
}

/// <summary>Conversions through LibreOffice (skipped when it is not installed).</summary>
public sealed class FileFlowOfficeTests
{
    private static ConversionOptions Options => new();

    [Fact]
    public async Task Documents_become_pdf()
    {
        if (!ToolLocator.IsAvailable(ExternalTool.LibreOffice)) return;
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        var files = new List<(string Path, string Expect)>();

        var txt = Path.Combine(folder.Path, "notiz.txt");
        File.WriteAllText(txt, "Grüße aus Wien – 日本語 auch.\nZweite Zeile.", new UTF8Encoding(false));
        files.Add((txt, "Grüße aus Wien"));
        var docx = Path.Combine(folder.Path, "brief.docx");
        DocxDocument.Write(docx, new[] { new[] { "Brief an FileFlow" } });
        files.Add((docx, "Brief an FileFlow"));
        var rtf = Path.Combine(folder.Path, "alt.rtf");
        File.WriteAllText(rtf, "{\\rtf1\\ansi{\\fonttbl\\f0 Arial;}\\f0 Rich Text Inhalt\\par}");
        files.Add((rtf, "Rich Text Inhalt"));
        var html = Path.Combine(folder.Path, "seite.html");
        File.WriteAllText(html, "<!DOCTYPE html><html><body><h1>Webseite</h1><script>document.write('BOESE')</script><p>Absatz</p></body></html>");
        files.Add((html, "Webseite"));
        var md = Path.Combine(folder.Path, "readme.md");
        File.WriteAllText(md, "# Markdown Titel\n\n- Punkt eins\n- Punkt zwei");
        files.Add((md, "Markdown Titel"));
        var epub = Path.Combine(folder.Path, "buch.epub");
        WriteEpub(epub);
        files.Add((epub, "Kapitel Eins"));
        var csv = Path.Combine(folder.Path, "tabelle.csv");
        File.WriteAllText(csv, "Name;Wert\nApfel;3\nBirne;5");
        var xlsx = await OfficeEngineConvert(csv, "xlsx", folder.Path);
        files.Add((xlsx, "Apfel"));
        var odt = await OfficeEngineConvert(docx, "odt", folder.Path);
        files.Add((odt, "Brief an FileFlow"));

        foreach (var (path, expect) in files)
        {
            var job = service.AddFile(path, FileFormat.Pdf);
            Assert.True(job.State == JobState.Ready, $"{Path.GetFileName(path)}: {job.Format} {job.Message}");
            await service.RunNowAsync(job, Options);
            Assert.True(job.State == JobState.Done, $"{Path.GetFileName(path)}: {job.Message}");
            var text = Samples.PdfText(job.Outputs[0]);
            Assert.Contains(expect, text);
            if (path == html) Assert.DoesNotContain("BOESE", text);
        }
    }

    [Fact]
    public async Task Presentations_become_pdf()
    {
        if (!ToolLocator.IsAvailable(ExternalTool.LibreOffice)) return;
        using var folder = new TempFolder();
        using var service = new FileFlowService(Path.Combine(folder.Path, "work"));
        // A presentation made by LibreOffice itself from a text outline.
        var odp = Path.Combine(folder.Path, "folien.fodp");
        File.WriteAllText(odp, """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0"
              xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0"
              office:version="1.3" office:mimetype="application/vnd.oasis.opendocument.presentation">
             <office:body><office:presentation><draw:page draw:name="Folie1">
              <draw:frame svg:width="20cm" svg:height="3cm" svg:x="2cm" svg:y="2cm"><draw:text-box><text:p>Folie Eins Inhalt</text:p></draw:text-box></draw:frame>
             </draw:page></office:presentation></office:body></office:document>
            """);
        var pptx = await OfficeEngineConvert(odp, "pptx", folder.Path);
        var job = service.AddFile(pptx, FileFormat.Pdf);
        Assert.Equal(FileFormat.Pptx, job.Format);
        await service.RunNowAsync(job, Options);
        Assert.True(job.State == JobState.Done, job.Message);
        Assert.Contains("Folie Eins Inhalt", Samples.PdfText(job.Outputs[0]));
    }

    private static async Task<string> OfficeEngineConvert(string input, string extension, string folder)
    {
        var work = Directory.CreateDirectory(Path.Combine(folder, "lo-" + extension)).FullName;
        var produced = await OfficeEngine.ConvertWithLibreOfficeAsync(input, extension, work, CancellationToken.None);
        var target = Path.Combine(folder, Path.GetFileNameWithoutExtension(input) + "." + extension);
        File.Move(produced, target, true);
        return target;
    }

    private static void WriteEpub(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        Entry("mimetype", "application/epub+zip");
        Entry("META-INF/container.xml", "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        Entry("OEBPS/content.opf", "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Testbuch</dc:title></metadata><manifest><item id=\"c1\" href=\"text/c1.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"c2\" href=\"text/c2.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"c1\"/><itemref idref=\"c2\"/></spine></package>");
        Entry("OEBPS/text/c1.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>Kapitel Eins</h1><p>Es war einmal.</p></body></html>");
        Entry("OEBPS/text/c2.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>Kapitel Zwei</h1><p>Ende.</p></body></html>");
    }
}
