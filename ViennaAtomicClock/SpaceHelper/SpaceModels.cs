using System.IO;

namespace ViennaAtomicClock.SpaceHelper;

/// <summary>File type groups shown in the overview.</summary>
public enum FileKind { Video, Image, Document, Archive, Program, Other }

/// <summary>How safe it is to remove an item. Only <see cref="Safe"/> and <see cref="Review"/> can ever be selected.</summary>
public enum Safety { Safe, Review, Keep }

/// <summary>Why an item showed up (one cleanup area each).</summary>
public enum Finding
{
    WindowsTemp, AppTemp, Thumbnails, Logs, BrowserCache, Installers, Large, Duplicate, OldDownload, Stale, Custom
}

/// <summary>One file in the results. Nothing is ever removed unless the user ticks it and confirms.</summary>
public sealed class FoundFile
{
    public FoundFile(string path, long size, DateTime modifiedUtc, DateTime accessedUtc, Finding finding)
    {
        Path = path;
        Size = size;
        ModifiedUtc = modifiedUtc;
        AccessedUtc = accessedUtc;
        Finding = finding;
        Kind = FileRules.KindOf(path);
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public long Size { get; }
    public DateTime ModifiedUtc { get; }
    public DateTime AccessedUtc { get; }
    public FileKind Kind { get; }
    public Finding Finding { get; }
    public Safety Safety { get; set; } = Safety.Review;
    /// <summary>String key explaining the safety rating.</summary>
    public string ReasonKey { get; set; } = "";
    /// <summary>Duplicate group (0 = none). The first file of each group is kept.</summary>
    public int Group { get; set; }
    public bool IsGroupOriginal { get; set; }
    public bool Selected { get; set; }
    public bool CanSelect => Safety != Safety.Keep;
}

/// <summary>A folder with the total size of everything below it (for the chart and the tree).</summary>
public sealed class FolderNode
{
    public FolderNode(string path, FolderNode? parent)
    {
        Path = path;
        Parent = parent;
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } name ? name : Path;
    public FolderNode? Parent { get; }
    public long Size { get; set; }
    public long Files { get; set; }
    public List<FolderNode> Children { get; } = new();
    /// <summary>Files directly in this folder.</summary>
    public long OwnSize { get; set; }
}

/// <summary>Group of identical files (same size and SHA-256).</summary>
public sealed record DuplicateGroup(int Id, long Size, string Hash, IReadOnlyList<FoundFile> Files)
{
    public long Wasted => Size * (Files.Count - 1);
}

/// <summary>Everything a scan found.</summary>
public sealed class ScanResult
{
    public required string Root { get; init; }
    public required FolderNode Tree { get; init; }
    public Dictionary<FileKind, long> KindSizes { get; } = Enum.GetValues<FileKind>().ToDictionary(k => k, _ => 0L);
    public Dictionary<FileKind, long> KindCounts { get; } = Enum.GetValues<FileKind>().ToDictionary(k => k, _ => 0L);
    public List<FoundFile> Large { get; } = new();
    public List<FoundFile> Stale { get; } = new();
    /// <summary>Files of at least the duplicate minimum, by size (input for the duplicate search).</summary>
    public Dictionary<long, List<FoundFile>> SameSize { get; } = new();
    public long Files { get; set; }
    public long Folders { get; set; }
    public long Bytes { get; set; }
    public long Inaccessible { get; set; }
    public long LinksSkipped { get; set; }
    public TimeSpan Elapsed { get; set; }
    public bool Canceled { get; set; }
}

/// <summary>Progress of a scan, sent about ten times per second.</summary>
public readonly record struct ScanProgress(long Files, long Bytes, string CurrentFolder, double Share);

/// <summary>User settings (thresholds, extra folders). Stored in %AppData%\ViennaAtomicClock\spacehelper.json.</summary>
public sealed class SpaceSettings
{
    public int LargeFileMb { get; set; } = 100;
    public int StaleDays { get; set; } = 365;
    public int DownloadDays { get; set; } = 90;
    public int LogDays { get; set; } = 30;
    public int InstallerDays { get; set; } = 30;
    public int DuplicateMinKb { get; set; } = 1024;
    /// <summary>Simulation: everything is shown, nothing is changed.</summary>
    public bool Simulation { get; set; }
    public bool RestorePointBeforeLargeCleanup { get; set; }
    public bool IncludeBrowserCache { get; set; } = true;
    /// <summary>Duplicates by size only (also renamed copies, much slower) instead of size + file name.</summary>
    public bool DuplicatesByContentOnly { get; set; }
    public List<string> CustomFolders { get; set; } = new();

    public SpaceSettings Normalize()
    {
        LargeFileMb = Math.Clamp(LargeFileMb, 10, 100_000);
        StaleDays = Math.Clamp(StaleDays, 30, 3650);
        DownloadDays = Math.Clamp(DownloadDays, 7, 3650);
        LogDays = Math.Clamp(LogDays, 1, 3650);
        InstallerDays = Math.Clamp(InstallerDays, 1, 3650);
        DuplicateMinKb = Math.Clamp(DuplicateMinKb, 1, 10_000_000);
        CustomFolders = (CustomFolders ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        return this;
    }
}

/// <summary>Byte sizes in readable form (1,5 GB).</summary>
public static class Bytes
{
    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return (unit == 0 ? value.ToString("0", Loc.Culture) : value.ToString(value < 10 ? "0.0" : "0", Loc.Culture)) + " " + units[unit];
    }
}

/// <summary>Drive overview for the start page.</summary>
public sealed record DriveSummary(string Name, string Label, string Format, long Total, long Free)
{
    public long Used => Math.Max(0, Total - Free);
    public double UsedShare => Total > 0 ? Used / (double)Total : 0;

    public static List<DriveSummary> ReadAll()
    {
        var drives = new List<DriveSummary>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                drives.Add(new DriveSummary(drive.Name, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A drive that disappears or locks while reading is simply left out.
            }
        }
        return drives;
    }
}
