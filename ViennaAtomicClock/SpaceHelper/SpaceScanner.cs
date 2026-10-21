using System.Diagnostics;
using System.IO;

namespace ViennaAtomicClock.SpaceHelper;

/// <summary>
/// Walks a drive or folder on a background thread: folder sizes, file types, large files, long-unused files and
/// same-size candidates for the duplicate search. Symbolic links and junctions are never followed, locked or
/// unreadable folders are counted and skipped, and the scan can be cancelled at any time.
/// </summary>
public sealed class SpaceScanner
{
    public const int MaxLarge = 2000;
    public const int MaxStale = 3000;
    public const int MaxTreeChildren = 80;

    private readonly SpaceSettings _settings;
    private readonly SystemPaths _paths;
    private readonly Func<DateTime> _now;

    public SpaceScanner(SpaceSettings settings, SystemPaths? paths = null, Func<DateTime>? now = null)
    {
        _settings = settings;
        _paths = paths ?? SystemPaths.Current;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public Task<ScanResult> ScanAsync(string root, long expectedBytes, IProgress<ScanProgress>? progress, CancellationToken token) =>
        Task.Factory.StartNew(() => Scan(root, expectedBytes, progress, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Synchronous scan (runs on the calling thread).</summary>
    public ScanResult Scan(string root, long expectedBytes, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var rootPath = Path.GetFullPath(root);
        var tree = new FolderNode(rootPath, null);
        var result = new ScanResult { Root = rootPath, Tree = tree };
        var largeLimit = (long)_settings.LargeFileMb * 1024 * 1024;
        var duplicateMin = (long)_settings.DuplicateMinKb * 1024;
        var staleBefore = _now() - TimeSpan.FromDays(_settings.StaleDays);
        var large = new PriorityQueue<FoundFile, long>();
        var stale = new PriorityQueue<FoundFile, long>();
        var nodes = new List<FolderNode> { tree };
        var pending = new Stack<FolderNode>();
        pending.Push(tree);
        var lastReport = 0L;
        var options = new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };

        while (pending.Count > 0)
        {
            if (token.IsCancellationRequested)
            {
                result.Canceled = true;
                break;
            }
            var node = pending.Pop();
            result.Folders++;
            IEnumerator<FileSystemInfo>? entries = null;
            try
            {
                entries = new DirectoryInfo(node.Path).EnumerateFileSystemInfos("*", options).GetEnumerator();
                while (true)
                {
                    FileSystemInfo entry;
                    try
                    {
                        if (!entries.MoveNext()) break;
                        entry = entries.Current;
                    }
                    catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        result.Inaccessible++;
                        break;
                    }
                    var attributes = entry.Attributes;
                    // Never follow symbolic links, junctions or mount points – they can point outside the scan area or loop.
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        result.LinksSkipped++;
                        continue;
                    }
                    if (entry is DirectoryInfo directory)
                    {
                        var child = new FolderNode(directory.FullName, node);
                        node.Children.Add(child);
                        nodes.Add(child);
                        pending.Push(child);
                        continue;
                    }
                    if (entry is not FileInfo file) continue;
                    long size;
                    DateTime modified, accessed;
                    try
                    {
                        size = file.Length;
                        modified = file.LastWriteTimeUtc;
                        accessed = file.LastAccessTimeUtc;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        result.Inaccessible++;
                        continue; // deleted or locked while scanning
                    }
                    node.OwnSize += size;
                    node.Files++;
                    result.Files++;
                    result.Bytes += size;
                    var kind = FileRules.KindOf(file.Name);
                    result.KindSizes[kind] += size;
                    result.KindCounts[kind]++;
                    var path = file.FullName;

                    if (size >= largeLimit) Keep(large, MaxLarge, Create(path, size, modified, accessed, Finding.Large, attributes), size);
                    var protectedPath = FileRules.ProtectionReason(path, _paths, attributes) is not null;
                    if (!protectedPath && size >= 1024 * 1024 && modified < staleBefore && (accessed == DateTime.MinValue || accessed < staleBefore))
                        Keep(stale, MaxStale, Create(path, size, modified, accessed, Finding.Stale, attributes), size);
                    if (!protectedPath && size >= duplicateMin)
                    {
                        if (!result.SameSize.TryGetValue(size, out var list)) result.SameSize[size] = list = new List<FoundFile>(2);
                        list.Add(new FoundFile(path, size, modified, accessed, Finding.Duplicate));
                    }

                    var now = Environment.TickCount64;
                    if (progress is not null && now - lastReport > 100)
                    {
                        lastReport = now;
                        progress.Report(new ScanProgress(result.Files, result.Bytes, node.Path, expectedBytes > 0 ? Math.Min(0.99, result.Bytes / (double)expectedBytes) : 0));
                    }
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException or ArgumentException)
            {
                result.Inaccessible++;
            }
            finally
            {
                entries?.Dispose();
            }
        }

        // Folder sizes bottom-up: children were created after their parents.
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var node = nodes[i];
            node.Size += node.OwnSize;
            if (node.Parent is not null)
            {
                node.Parent.Size += node.Size;
                node.Parent.Files += node.Files;
            }
        }
        foreach (var node in nodes) Prune(node);
        // Sizes that occur once cannot be duplicates.
        foreach (var size in result.SameSize.Where(p => p.Value.Count < 2).Select(p => p.Key).ToList()) result.SameSize.Remove(size);

        result.Large.AddRange(Drain(large));
        result.Stale.AddRange(Drain(stale));
        result.Elapsed = clock.Elapsed;
        progress?.Report(new ScanProgress(result.Files, result.Bytes, rootPath, 1));
        return result;
    }

    private FoundFile Create(string path, long size, DateTime modified, DateTime accessed, Finding finding, FileAttributes attributes)
    {
        var file = new FoundFile(path, size, modified, accessed, finding);
        FileRules.Assess(file, _paths, attributes);
        return file;
    }

    /// <summary>Keeps the <paramref name="limit"/> biggest items (min-heap).</summary>
    private static void Keep(PriorityQueue<FoundFile, long> heap, int limit, FoundFile file, long size)
    {
        if (heap.Count < limit) heap.Enqueue(file, size);
        else if (heap.TryPeek(out _, out var smallest) && size > smallest) heap.EnqueueDequeue(file, size);
    }

    private static IEnumerable<FoundFile> Drain(PriorityQueue<FoundFile, long> heap)
    {
        var list = new List<FoundFile>(heap.Count);
        while (heap.TryDequeue(out var file, out _)) list.Add(file);
        list.Reverse(); // biggest first
        return list;
    }

    /// <summary>Only the biggest sub-folders stay in the tree (memory stays small on huge drives).</summary>
    private static void Prune(FolderNode node)
    {
        if (node.Children.Count == 0) return;
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (node.Children.Count > MaxTreeChildren) node.Children.RemoveRange(MaxTreeChildren, node.Children.Count - MaxTreeChildren);
    }
}
