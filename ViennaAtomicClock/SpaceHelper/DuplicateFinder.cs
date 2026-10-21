using System.IO;
using System.Security.Cryptography;

namespace ViennaAtomicClock.SpaceHelper;

/// <summary>
/// Finds identical files in steps, each much cheaper than the next: same size and (by default) the same file name
/// without copy suffixes – both known from the scan, no file access – then a quick hash of the first and last 64 KB,
/// and only then the full SHA-256. At most two files are read at a time. The oldest file of each group counts as the
/// original and is never offered for removal.
/// </summary>
public sealed class DuplicateFinder
{
    public const int SampleBytes = 64 * 1024;
    public const int MaxParallel = 2;
    private readonly SystemPaths _paths;

    private readonly bool _byName;

    public DuplicateFinder(SystemPaths? paths = null, bool byName = true)
    {
        _paths = paths ?? SystemPaths.Current;
        _byName = byName;
    }

    private static readonly System.Text.RegularExpressions.Regex CopySuffix = new(
        @"^(?:kopie von |copy of )?(?<stem>.*?)(?: - (?:kopie|copy)(?: \(\d+\))?| \(\d+\)|[ _-]copy\d*|[ _-]kopie\d*)?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>File name without copy suffixes ("foto (1).jpg", "foto - Kopie.jpg", "Copy of foto.jpg" → "foto.jpg").</summary>
    public static string NormalizedName(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path).Trim();
        var match = CopySuffix.Match(name);
        var stem = match.Success && match.Groups["stem"].Length > 0 ? match.Groups["stem"].Value : name;
        return (stem.Trim() + System.IO.Path.GetExtension(path)).ToLowerInvariant();
    }

    public async Task<List<DuplicateGroup>> FindAsync(IReadOnlyDictionary<long, List<FoundFile>> sameSize, IProgress<double>? progress, CancellationToken token)
    {
        // Step 1: same size (and same name) – no file access needed.
        var candidates = sameSize.SelectMany(p => _byName
                ? p.Value.GroupBy(f => NormalizedName(f.Path)).Where(g => g.Count() >= 2).Select(g => new KeyValuePair<long, List<FoundFile>>(p.Key, g.ToList()))
                : p.Value.Count >= 2 ? new[] { p } : Array.Empty<KeyValuePair<long, List<FoundFile>>>())
            .OrderByDescending(p => p.Key).ToList();
        var totalBytes = Math.Max(1, candidates.Sum(p => p.Key * (long)p.Value.Count));
        long doneBytes = 0;
        var gate = new SemaphoreSlim(MaxParallel);
        var groups = new List<DuplicateGroup>();
        var id = 0;

        foreach (var (size, files) in candidates)
        {
            token.ThrowIfCancellationRequested();
            // Step 2: quick sample hash – most same-size files differ already here.
            var quick = await HashAllAsync(files, f => QuickHash(f.Path, size), gate, token);
            foreach (var bucket in quick.Where(q => q.Hash is not null).GroupBy(q => q.Hash!).Where(g => g.Count() >= 2))
            {
                // Step 3: full SHA-256 to be sure (files up to the sample size are already hashed completely).
                var members = bucket.Select(b => b.File).ToList();
                var full = size <= SampleBytes * 2
                    ? members.Select(m => (File: m, Hash: (string?)bucket.Key)).ToList()
                    : await HashAllAsync(members, f => FullHash(f.Path, token), gate, token);
                foreach (var same in full.Where(f => f.Hash is not null).GroupBy(f => f.Hash!).Where(g => g.Count() >= 2))
                {
                    var ordered = same.Select(s => s.File).OrderBy(f => f.ModifiedUtc).ThenBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
                    id++;
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        ordered[i].Group = id;
                        ordered[i].IsGroupOriginal = i == 0;
                        FileRules.Assess(ordered[i], _paths);
                    }
                    groups.Add(new DuplicateGroup(id, size, same.Key, ordered));
                }
            }
            doneBytes += size * files.Count;
            progress?.Report(Math.Min(1, doneBytes / (double)totalBytes));
        }
        progress?.Report(1);
        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    private static async Task<List<(FoundFile File, string? Hash)>> HashAllAsync(List<FoundFile> files, Func<FoundFile, string?> hash, SemaphoreSlim gate, CancellationToken token)
    {
        var tasks = files.Select(async file =>
        {
            await gate.WaitAsync(token);
            try
            {
                return (file, await Task.Run(() => hash(file), token));
            }
            finally
            {
                gate.Release();
            }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>Hash of size + first and last 64 KB (null if the file cannot be read).</summary>
    public static string? QuickHash(string path, long expectedSize)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            if (stream.Length != expectedSize) return null; // changed since the scan
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(BitConverter.GetBytes(expectedSize));
            var buffer = new byte[SampleBytes];
            var read = stream.ReadAtLeast(buffer, (int)Math.Min(SampleBytes, expectedSize), throwOnEndOfStream: false);
            sha.AppendData(buffer, 0, read);
            if (expectedSize > SampleBytes * 2)
            {
                stream.Seek(-SampleBytes, SeekOrigin.End);
                read = stream.ReadAtLeast(buffer, SampleBytes, throwOnEndOfStream: false);
                sha.AppendData(buffer, 0, read);
            }
            else if (expectedSize > SampleBytes)
            {
                read = stream.ReadAtLeast(buffer, (int)(expectedSize - SampleBytes), throwOnEndOfStream: false);
                sha.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null; // locked or gone: never treated as a duplicate
        }
    }

    public static string? FullHash(string path, CancellationToken token)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
