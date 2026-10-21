using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ViennaAtomicClock.SpaceHelper;

public enum CleanAction { Recycle, Delete, Move, Archive }

/// <summary>Why a file was not removed.</summary>
public enum SkipReason { None, Protected, Changed, Missing, InUse, AccessDenied, Link, Failed }

public sealed record CleanFailure(string Path, SkipReason Reason);

/// <summary>Outcome of a cleanup (or of a simulation).</summary>
public sealed class CleanReport
{
    public CleanAction Action { get; init; }
    public bool Simulated { get; init; }
    public int Done { get; set; }
    public long FreedBytes { get; set; }
    public List<CleanFailure> Failed { get; } = new();
    public bool Canceled { get; set; }
    public string? ArchivePath { get; set; }
}

/// <summary>
/// Executes a cleanup the user confirmed. Every file is checked again right before it is touched (still there,
/// unchanged since the scan, not protected, not a link). Default is the Windows recycle bin; permanent deletion only
/// when explicitly chosen. Runs one file at a time and can be cancelled; a log line goes to the local log.
/// </summary>
public sealed class SpaceCleaner
{
    private readonly SystemPaths _paths;
    private readonly string _logPath;

    public SpaceCleaner(SystemPaths? paths = null, string? logPath = null)
    {
        _paths = paths ?? SystemPaths.Current;
        _logPath = logPath ?? DefaultLogPath;
    }

    public static string DefaultLogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock", "spacehelper-log.jsonl");
    public string LogPath => _logPath;

    public Task<CleanReport> RunAsync(IReadOnlyList<FoundFile> files, CleanAction action, string? target, bool simulate, IProgress<double>? progress, CancellationToken token) =>
        Task.Run(() => Run(files, action, target, simulate, progress, token), CancellationToken.None);

    public CleanReport Run(IReadOnlyList<FoundFile> files, CleanAction action, string? target, bool simulate, IProgress<double>? progress, CancellationToken token)
    {
        var report = new CleanReport { Action = action, Simulated = simulate };
        if ((action is CleanAction.Move or CleanAction.Archive) && string.IsNullOrWhiteSpace(target)) throw new ArgumentException("A target is required.", nameof(target));
        ZipArchive? archive = null;
        FileStream? archiveStream = null;
        var archiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archived = new List<(FoundFile File, string Entry)>();
        try
        {
            if (action == CleanAction.Archive && !simulate)
            {
                archiveStream = new FileStream(target!, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                archive = new ZipArchive(archiveStream, ZipArchiveMode.Create);
                report.ArchivePath = target;
            }
            for (var i = 0; i < files.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    report.Canceled = true;
                    break;
                }
                var file = files[i];
                var reason = Check(file);
                if (reason == SkipReason.None && !simulate)
                {
                    reason = action switch
                    {
                        CleanAction.Recycle => Recycle(file.Path),
                        CleanAction.Delete => Delete(file.Path),
                        CleanAction.Move => Move(file.Path, target!),
                        _ => AddToArchive(file, archive!, archiveNames, archived)
                    };
                    if (action == CleanAction.Archive && reason == SkipReason.None) continue; // counted after the ZIP check
                }
                if (reason == SkipReason.None)
                {
                    report.Done++;
                    report.FreedBytes += action == CleanAction.Move && SameDrive(file.Path, target!) ? 0 : file.Size;
                }
                else report.Failed.Add(new CleanFailure(file.Path, reason));
                progress?.Report((i + 1) / (double)files.Count);
            }
        }
        finally
        {
            archive?.Dispose();
            archiveStream?.Dispose();
        }
        if (archived.Count > 0) FinishArchive(archived, target!, report);
        Log(report, files.Count);
        return report;
    }

    /// <summary>Final safety check right before a file is touched.</summary>
    public SkipReason Check(FoundFile file)
    {
        if (!file.CanSelect) return SkipReason.Protected;
        FileInfo info;
        try
        {
            info = new FileInfo(file.Path);
            if (!info.Exists) return SkipReason.Missing;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return SkipReason.Link;
            if (FileRules.ProtectionReason(file.Path, _paths, info.Attributes) is not null) return SkipReason.Protected;
            if (info.Length != file.Size || Math.Abs((info.LastWriteTimeUtc - file.ModifiedUtc).TotalSeconds) > 2) return SkipReason.Changed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return SkipReason.AccessDenied;
        }
        // Folders above the file must not be links either (the path could lead somewhere else now).
        for (var folder = info.Directory; folder is not null; folder = folder.Parent)
        {
            try
            {
                if (folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) != 0) return SkipReason.Link;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return SkipReason.AccessDenied;
            }
        }
        return SkipReason.None;
    }

    private static SkipReason Recycle(string path)
    {
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING
        };
        var result = SHFileOperation(ref operation);
        if (result == 0 && !operation.fAnyOperationsAborted && !File.Exists(path)) return SkipReason.None;
        return File.Exists(path) ? (IsLocked(path) ? SkipReason.InUse : SkipReason.Failed) : SkipReason.None;
    }

    private static SkipReason Delete(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return SkipReason.None;
        }
        catch (UnauthorizedAccessException)
        {
            return SkipReason.AccessDenied;
        }
        catch (IOException)
        {
            return SkipReason.InUse;
        }
    }

    private static SkipReason Move(string path, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.Move(path, UniquePath(Path.Combine(folder, Path.GetFileName(path))));
            return SkipReason.None;
        }
        catch (UnauthorizedAccessException)
        {
            return SkipReason.AccessDenied;
        }
        catch (IOException)
        {
            return SkipReason.InUse;
        }
    }

    /// <summary>Adds the file to the ZIP; the original is only removed after the finished ZIP was checked.</summary>
    private static SkipReason AddToArchive(FoundFile file, ZipArchive archive, HashSet<string> names, List<(FoundFile File, string Entry)> archived)
    {
        try
        {
            var name = file.Name;
            var stem = Path.GetFileNameWithoutExtension(name);
            for (var i = 2; !names.Add(name); i++) name = $"{stem} ({i}){Path.GetExtension(file.Name)}";
            archive.CreateEntryFromFile(file.Path, name, CompressionLevel.Optimal);
            archived.Add((file, name));
            return SkipReason.None;
        }
        catch (UnauthorizedAccessException)
        {
            return SkipReason.AccessDenied;
        }
        catch (IOException)
        {
            return SkipReason.InUse;
        }
    }

    /// <summary>Reads the closed ZIP back; every original whose entry has the right size goes to the recycle bin (still restorable).</summary>
    private static void FinishArchive(List<(FoundFile File, string Entry)> archived, string zipPath, CleanReport report)
    {
        Dictionary<string, long> lengths;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            lengths = zip.Entries.ToDictionary(e => e.FullName, e => e.Length, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            foreach (var (file, _) in archived) report.Failed.Add(new CleanFailure(file.Path, SkipReason.Failed));
            return;
        }
        foreach (var (file, entry) in archived)
        {
            var reason = lengths.TryGetValue(entry, out var length) && length == file.Size ? Recycle(file.Path) : SkipReason.Failed;
            if (reason == SkipReason.None)
            {
                report.Done++;
                report.FreedBytes += file.Size;
            }
            else report.Failed.Add(new CleanFailure(file.Path, reason));
        }
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SameDrive(string a, string b) => string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>One JSON line per cleanup: time, action, counts and the affected paths – never file contents. Stays on this PC.</summary>
    private void Log(CleanReport report, int requested)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            var line = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.Now.ToString("O"),
                action = report.Action.ToString(),
                simulated = report.Simulated,
                requested,
                done = report.Done,
                freedBytes = report.FreedBytes,
                canceled = report.Canceled,
                archive = report.ArchivePath,
                failed = report.Failed.Select(f => new { path = f.Path, reason = f.Reason.ToString() })
            });
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Logging never blocks a cleanup.
        }
    }

    // ───────────── recycle bin through the shell (undo-able) ─────────────

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);
}

/// <summary>Windows restore point before a large cleanup (needs administrator rights, so Windows asks).</summary>
public static class RestorePoint
{
    public static async Task<bool> CreateAsync(string description)
    {
        var safe = new string(description.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').ToArray());
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"try {{ Checkpoint-Computer -Description '{safe}' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; exit 0 }} catch {{ exit 1 }}\""
        };
        try
        {
            using var process = Process.Start(info);
            if (process is null) return false;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
            return process.ExitCode == 0;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            return false; // elevation declined, System Restore off or Windows' one-per-day limit
        }
    }
}
