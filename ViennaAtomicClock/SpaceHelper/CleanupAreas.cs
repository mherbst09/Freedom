using System.IO;
using System.Runtime.InteropServices;

namespace ViennaAtomicClock.SpaceHelper;

/// <summary>Files of one cleanup area with their total size.</summary>
public sealed class AreaResult
{
    public AreaResult(Finding finding) => Finding = finding;

    public Finding Finding { get; }
    public List<FoundFile> Files { get; } = new();
    public long Size => Files.Sum(f => f.Size);
    public long Inaccessible { get; set; }
    public bool Truncated { get; set; }
}

/// <summary>
/// Finds the typical space wasters in their known places. Every file is rated by <see cref="FileRules"/>;
/// recently changed temp files are left out because programs may still use them.
/// </summary>
public sealed class CleanupAreas
{
    public const int MaxPerArea = 20000;
    private static readonly string[] InstallerExtensions = { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle" };

    private readonly SpaceSettings _settings;
    private readonly SystemPaths _paths;
    private readonly Func<DateTime> _now;

    public CleanupAreas(SpaceSettings settings, SystemPaths? paths = null, Func<DateTime>? now = null)
    {
        _settings = settings;
        _paths = paths ?? SystemPaths.Current;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public Task<List<AreaResult>> ScanAsync(CancellationToken token) => Task.Run(() => Scan(token), token);

    public List<AreaResult> Scan(CancellationToken token)
    {
        var now = _now();
        var day = now - TimeSpan.FromDays(1);
        var results = new List<AreaResult>
        {
            Collect(Finding.WindowsTemp, new[] { Path.Combine(_paths.Windows, "Temp") }, f => f.LastWriteTimeUtc < day, token),
            Collect(Finding.AppTemp, new[] { _paths.Temp }, f => f.LastWriteTimeUtc < day, token),
            Collect(Finding.Thumbnails, new[] { Path.Combine(_paths.LocalAppData, "Microsoft", "Windows", "Explorer") },
                f => f.Name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) || f.Name.StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase), token, recursive: false),
            Collect(Finding.Logs, new[]
            {
                Path.Combine(_paths.LocalAppData, "Microsoft", "Windows", "WER"), Path.Combine(_paths.ProgramData, "Microsoft", "Windows", "WER"),
                Path.Combine(_paths.LocalAppData, "CrashDumps")
            }, f => f.LastWriteTimeUtc < now - TimeSpan.FromDays(_settings.LogDays) || f.Extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase), token),
            Collect(Finding.BrowserCache, _settings.IncludeBrowserCache ? BrowserCache.Locations(_paths).Select(l => l.Folder).ToArray() : Array.Empty<string>(), _ => true, token),
            Collect(Finding.Installers, new[] { _paths.Downloads },
                f => InstallerExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase) && f.LastWriteTimeUtc < now - TimeSpan.FromDays(_settings.InstallerDays), token),
            Collect(Finding.OldDownload, new[] { _paths.Downloads },
                f => !InstallerExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase) && f.LastWriteTimeUtc < now - TimeSpan.FromDays(_settings.DownloadDays)
                     && (f.LastAccessTimeUtc == DateTime.MinValue || f.LastAccessTimeUtc < now - TimeSpan.FromDays(_settings.DownloadDays)), token),
            Collect(Finding.Custom, _settings.CustomFolders.ToArray(), _ => true, token)
        };
        return results;
    }

    private AreaResult Collect(Finding finding, IEnumerable<string> folders, Func<FileInfo, bool> include, CancellationToken token, bool recursive = true)
    {
        var area = new AreaResult(finding);
        var options = new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0 };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in folders.Where(Directory.Exists))
        {
            var pending = new Stack<string>();
            pending.Push(start);
            while (pending.Count > 0 && !token.IsCancellationRequested)
            {
                var folder = pending.Pop();
                try
                {
                    foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue; // never follow links
                        if (entry is DirectoryInfo directory)
                        {
                            if (recursive) pending.Push(directory.FullName);
                            continue;
                        }
                        if (entry is not FileInfo file || !seen.Add(file.FullName)) continue;
                        try
                        {
                            if (!include(file)) continue;
                            if (area.Files.Count >= MaxPerArea)
                            {
                                area.Truncated = true;
                                continue;
                            }
                            var found = new FoundFile(file.FullName, file.Length, file.LastWriteTimeUtc, file.LastAccessTimeUtc, finding);
                            FileRules.Assess(found, _paths, file.Attributes);
                            area.Files.Add(found);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                            area.Inaccessible++;
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    area.Inaccessible++;
                }
            }
        }
        area.Files.Sort((a, b) => b.Size.CompareTo(a.Size));
        return area;
    }
}

/// <summary>The Windows recycle bin: size, item count and emptying through the official shell function.</summary>
public static class RecycleBin
{
    public readonly record struct Info(long Size, long Items, bool Available);

    public static Info Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        try
        {
            return SHQueryRecycleBin(null, ref info) == 0 ? new Info(info.i64Size, info.i64NumItems, true) : new Info(0, 0, false);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return new Info(0, 0, false);
        }
    }

    /// <summary>Empties all recycle bins. Only called after the user confirmed in SpaceHelper's own dialog.</summary>
    public static bool Empty(IntPtr owner)
    {
        const uint noConfirmation = 0x1, noProgress = 0x2, noSound = 0x4;
        var result = SHEmptyRecycleBin(owner, null, noConfirmation | noProgress | noSound);
        return result == 0 || result == unchecked((int)0x8000FFFF); // E_UNEXPECTED = already empty
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
