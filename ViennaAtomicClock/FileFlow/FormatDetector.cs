using System.IO;
using System.IO.Compression;
using System.Text;

namespace ViennaAtomicClock.FileFlow;

/// <summary>Result of looking into a file: the real format and whether the file name agreed.</summary>
public readonly record struct Detection(FileFormat Format, FileFormat ByExtension)
{
    public bool ExtensionMatches => ByExtension == Format;
}

/// <summary>
/// Finds the real format from the file's first bytes (signatures, containers, ZIP contents) – the file name is
/// only used to tell apart formats whose bytes are identical (plain text vs. Markdown, MP4 vs. M4A brands).
/// Programs and scripts are recognised and refused.
/// </summary>
public static class FormatDetector
{
    private const int HeadSize = 8192;

    /// <summary>Program and script types: refused even when their content is plain text.</summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".com", ".scr", ".msi", ".msix", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".lnk", ".jar", ".sh", ".cpl", ".reg", ".appx", ".sys", ".drv"
    };

    public static Detection Detect(string path)
    {
        var byExtension = FileFlowCatalog.FromExtension(Path.GetExtension(path));
        if (BlockedExtensions.Contains(Path.GetExtension(path))) return new Detection(FileFormat.Executable, byExtension);
        byte[] head;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            head = new byte[(int)Math.Min(HeadSize, stream.Length)];
            stream.ReadExactly(head);
        }
        catch (Exception) when (!File.Exists(path))
        {
            return new Detection(FileFormat.Unknown, byExtension);
        }
        var format = FromBytes(head, byExtension);
        if (format == FileFormat.Unknown && head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4) format = FromZip(path);
        return new Detection(format, byExtension);
    }

    /// <summary>Format from the first bytes; <paramref name="hint"/> (from the name) only settles ties.</summary>
    public static FileFormat FromBytes(ReadOnlySpan<byte> b, FileFormat hint = FileFormat.Unknown)
    {
        if (b.Length < 4) return LooksLikeText(b) && b.Length > 0 ? TextFormat(b, hint) : FileFormat.Unknown;

        // Programs and scripts are never accepted.
        if (b[0] == 'M' && b[1] == 'Z') return FileFormat.Executable;
        if (b[0] == 0x7F && b[1] == 'E' && b[2] == 'L' && b[3] == 'F') return FileFormat.Executable;
        if (b[0] == '#' && b[1] == '!') return FileFormat.Executable;
        if (b[0] == 0xCF && b[1] == 0xFA && b[2] == 0xED && b[3] == 0xFE) return FileFormat.Executable;

        if (Starts(b, 0x89, (byte)'P', (byte)'N', (byte)'G')) return FileFormat.Png;
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return FileFormat.Jpg;
        if (Ascii(b, 0, "GIF8")) return FileFormat.Gif;
        if (Ascii(b, 0, "BM") && b.Length >= 14) return FileFormat.Bmp;
        if (Starts(b, (byte)'I', (byte)'I', 42, 0) || Starts(b, (byte)'M', (byte)'M', 0, 42)) return FileFormat.Tiff;
        if (Ascii(b, 0, "%PDF-") || IndexOf(b, "%PDF-", 1024) >= 0) return FileFormat.Pdf;
        if (Ascii(b, 0, "{\\rtf")) return FileFormat.Rtf;
        if (Ascii(b, 0, "fLaC")) return FileFormat.Flac;
        if (Ascii(b, 0, "OggS")) return FileFormat.Ogg;
        if (Ascii(b, 0, "FLV")) return FileFormat.Flv;
        if (Starts(b, 0x30, 0x26, 0xB2, 0x75)) return FileFormat.Wma; // ASF container
        if (Starts(b, 0x1A, 0x45, 0xDF, 0xA3)) return IndexOf(b, "webm", b.Length) >= 0 ? FileFormat.Webm : FileFormat.Mkv;
        if (Ascii(b, 0, "RIFF") && b.Length >= 12)
        {
            if (Ascii(b, 8, "AVI ")) return FileFormat.Avi;
            if (Ascii(b, 8, "WAVE")) return FileFormat.Wav;
            if (Ascii(b, 8, "WEBP")) return FileFormat.Webp;
            return FileFormat.Unknown;
        }
        if (b.Length >= 12 && Ascii(b, 4, "ftyp")) return FromBrand(Encoding.ASCII.GetString(b.Slice(8, 4)), hint);
        if (Ascii(b, 0, "ID3")) return FileFormat.Mp3;
        if (b[0] == 0xFF && (b[1] & 0xE0) == 0xE0)
        {
            var layer = (b[1] >> 1) & 3;
            return layer == 0 ? FileFormat.Aac : FileFormat.Mp3; // ADTS has layer bits 00
        }
        if (LooksLikeText(b)) return TextFormat(b, hint);
        return FileFormat.Unknown;
    }

    private static FileFormat FromBrand(string brand, FileFormat hint)
    {
        switch (brand)
        {
            case "M4A ": case "M4B ": return FileFormat.M4a;
            case "M4V ": case "M4VH": case "M4VP": return FileFormat.M4v;
            case "qt  ": return FileFormat.Mov;
            case "heic": case "heix": case "hevc": case "hevx": case "heim": case "heis": case "mif1": case "msf1": return FileFormat.Heic;
            case "avif": case "avis": return FileFormat.Unknown;
        }
        // Generic ISO brands: the name decides between the MP4 family members.
        return hint is FileFormat.M4a or FileFormat.M4v or FileFormat.Mov ? hint : FileFormat.Mp4;
    }

    private static FileFormat TextFormat(ReadOnlySpan<byte> b, FileFormat hint)
    {
        var text = Encoding.UTF8.GetString(b.Length > 2048 ? b[..2048] : b).TrimStart('﻿', ' ', '\t', '\r', '\n').ToLowerInvariant();
        if (text.StartsWith("<svg") || (text.StartsWith("<?xml") && text.Contains("<svg"))) return FileFormat.Svg;
        if (text.StartsWith("<!doctype html") || text.StartsWith("<html") || (hint == FileFormat.Html && text.StartsWith('<'))) return FileFormat.Html;
        if (text.StartsWith("<?php") || text.StartsWith("@echo off")) return FileFormat.Executable;
        // Markdown and HTML need the name; any other readable text (also .csv, .json …) is plain text.
        return hint is FileFormat.Markdown or FileFormat.Html ? hint : FileFormat.Txt;
    }

    /// <summary>Office files, OpenDocument and EPUB are ZIP containers – their content tells them apart.</summary>
    private static FileFormat FromZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var mimetype = zip.GetEntry("mimetype");
            if (mimetype is not null)
            {
                using var reader = new StreamReader(mimetype.Open());
                var type = reader.ReadToEnd().Trim();
                if (type == "application/epub+zip") return FileFormat.Epub;
                if (type == "application/vnd.oasis.opendocument.text") return FileFormat.Odt;
            }
            if (zip.GetEntry("[Content_Types].xml") is not null)
            {
                if (zip.GetEntry("word/document.xml") is not null) return FileFormat.Docx;
                if (zip.GetEntry("ppt/presentation.xml") is not null) return FileFormat.Pptx;
                if (zip.GetEntry("xl/workbook.xml") is not null) return FileFormat.Xlsx;
            }
        }
        catch (InvalidDataException)
        {
            // Not a readable ZIP.
        }
        return FileFormat.Unknown;
    }

    /// <summary>No NUL bytes and valid UTF-8 (a cut-off multi-byte character at the end is fine).</summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> b)
    {
        if (b.IndexOf((byte)0) >= 0) return false;
        var span = b;
        // Ignore up to three bytes of a character cut off by the read limit.
        for (var cut = 0; cut <= 3 && span.Length > 0; cut++, span = span[..^1])
            if (Utf8IsValid(span)) return true;
        return false;
    }

    private static bool Utf8IsValid(ReadOnlySpan<byte> span)
    {
        try
        {
            new UTF8Encoding(false, true).GetCharCount(span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool Starts(ReadOnlySpan<byte> b, params byte[] prefix) => b.Length >= prefix.Length && b[..prefix.Length].SequenceEqual(prefix);

    private static bool Ascii(ReadOnlySpan<byte> b, int offset, string text)
    {
        if (b.Length < offset + text.Length) return false;
        for (var i = 0; i < text.Length; i++) if (b[offset + i] != text[i]) return false;
        return true;
    }

    private static int IndexOf(ReadOnlySpan<byte> b, string text, int limit) =>
        b[..Math.Min(limit, b.Length)].IndexOf(Encoding.ASCII.GetBytes(text));
}
