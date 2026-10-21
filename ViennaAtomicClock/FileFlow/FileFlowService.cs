using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ViennaAtomicClock.FileFlow;

public enum JobState { Ready, Queued, Importing, Converting, Done, Failed, Canceled, Expired }

public enum JobKind { File, Link, ImageBundle }

/// <summary>One file (or link, or several images for one PDF) in the list.</summary>
public sealed class FileFlowJob
{
    internal CancellationTokenSource? Cancellation;

    internal FileFlowJob(JobKind kind, string displayName, IReadOnlyList<string> sources, FileFormat format, FileFormat target, long size, DateTime createdUtc)
    {
        Kind = kind;
        DisplayName = displayName;
        Sources = sources;
        Format = format;
        Target = target;
        SizeBytes = size;
        CreatedUtc = createdUtc;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public JobKind Kind { get; }
    public string DisplayName { get; internal set; }
    /// <summary>Original files (or the link) – they are only read, never changed.</summary>
    public IReadOnlyList<string> Sources { get; }
    public FileFormat Format { get; }
    public FileFormat Target { get; set; }
    public long SizeBytes { get; }
    public DateTime CreatedUtc { get; }
    public DateTime? StoredUtc { get; internal set; }
    public JobState State { get; internal set; }
    /// <summary>0..1 of the current phase (import or conversion).</summary>
    public double Progress { get; internal set; }
    public string? ErrorKey { get; internal set; }
    public object[] ErrorArgs { get; internal set; } = Array.Empty<object>();
    public List<string> Outputs { get; } = new();
    public BitmapSource? Thumbnail { get; internal set; }
    internal string? Folder { get; set; }

    public bool IsActive => State is JobState.Queued or JobState.Importing or JobState.Converting;
    public bool CanChangeTarget => State == JobState.Ready && Kind != JobKind.Link;
    public IReadOnlyList<FileFormat> PossibleTargets => Kind == JobKind.ImageBundle ? new[] { FileFormat.Pdf } : FileFlowCatalog.TargetsFor(Format);
    /// <summary>When the stored copies are deleted at the latest.</summary>
    public DateTime? ExpiresUtc => StoredUtc + FileFlowService.Retention;
    public string Message => ErrorKey is null ? "" : Loc.F(ErrorKey, ErrorArgs);
}

/// <summary>
/// Runs the conversions: copies files under random names into a private temp folder, checks type and size again,
/// converts with limited parallelism and time, and deletes everything after one hour at the latest (and when
/// Freedom closes). Failed conversions never affect other jobs or the app.
/// </summary>
public sealed class FileFlowService : IDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxConcurrent = 2;
    public const int MaxJobs = 200;

    private readonly List<FileFlowJob> _jobs = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly Func<DateTime> _now;
    private readonly System.Threading.Timer _cleanup;
    private readonly CancellationTokenSource _life = new();
    private readonly List<Task> _previews = new();
    private bool _disposed;

    public FileFlowService(string? workRoot = null, Func<DateTime>? now = null)
    {
        WorkRoot = workRoot ?? Path.Combine(Path.GetTempPath(), "Freedom-FileFlow");
        _now = now ?? (() => DateTime.UtcNow);
        // One folder per running Freedom, so a second window never touches the files of the first.
        var allJobs = Path.Combine(WorkRoot, "jobs");
        JobsFolder = Path.Combine(allJobs, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + RandomName()[..6]);
        // Nothing from an earlier session survives: those files are no longer tracked.
        if (Directory.Exists(allJobs))
            foreach (var folder in Directory.EnumerateDirectories(allJobs))
                if (!BelongsToRunningProcess(Path.GetFileName(folder))) TryDelete(folder);
        Directory.CreateDirectory(JobsFolder);
        _cleanup = new System.Threading.Timer(_ => RemoveExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public string WorkRoot { get; }
    public string JobsFolder { get; }
    /// <summary>Snapshot of the list (safe to enumerate while the cleanup timer runs).</summary>
    public IReadOnlyList<FileFlowJob> Jobs
    {
        get
        {
            lock (_jobs) return _jobs.ToList();
        }
    }

    /// <summary>A job changed its state or result (raised on worker threads).</summary>
    public event Action<FileFlowJob>? JobChanged;
    /// <summary>Jobs were added or removed.</summary>
    public event Action? ListChanged;

    // ───────────────────────── adding ─────────────────────────

    /// <summary>Adds a file. Unsupported, empty, too large or executable files are added as failed rows with the reason.</summary>
    public FileFlowJob AddFile(string path, FileFormat preferredTarget = FileFormat.Unknown)
    {
        var name = Path.GetFileName(path);
        long size = 0;
        string? error = null;
        object[] args = Array.Empty<object>();
        var detection = new Detection(FileFormat.Unknown, FileFormat.Unknown);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) error = "ff.err.missing";
            else
            {
                size = info.Length;
                detection = FormatDetector.Detect(path);
                if (size == 0) error = "ff.err.empty";
                else if (size > MaxFileBytes) (error, args) = ("ff.err.tooLarge", new object[] { FormatSize(MaxFileBytes) });
                else if (detection.Format == FileFormat.Executable) error = "ff.err.executable";
                else if (detection.Format == FileFormat.Unknown || FileFlowCatalog.TargetsFor(detection.Format).Count == 0) error = "ff.err.unsupported";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = "ff.err.read";
        }
        var target = error is null
            ? FileFlowCatalog.IsSupported(detection.Format, preferredTarget) ? preferredTarget : FileFlowCatalog.DefaultTarget(detection.Format)
            : FileFormat.Unknown;
        var job = new FileFlowJob(JobKind.File, name, new[] { path }, detection.Format, target, size, _now());
        if (error is not null) Fail(job, error, args, notify: false);
        Add(job);
        return job;
    }

    /// <summary>Adds a YouTube or SoundCloud link. Without the rights confirmation the link is refused.</summary>
    public FileFlowJob AddLink(string url, bool rightsConfirmed)
    {
        var platform = MediaEngine.ClassifyLink(url);
        var job = new FileFlowJob(JobKind.Link, url.Trim(), new[] { url.Trim() }, platform, FileFormat.Mp3, 0, _now());
        if (platform == FileFormat.Unknown) Fail(job, "ff.err.link.invalid", Array.Empty<object>(), notify: false);
        else if (!rightsConfirmed) Fail(job, "ff.err.link.rights", Array.Empty<object>(), notify: false);
        Add(job);
        return job;
    }

    private void Add(FileFlowJob job)
    {
        if (Jobs.Count >= MaxJobs)
        {
            var oldest = Jobs.FirstOrDefault(j => !j.IsActive);
            if (oldest is not null) Remove(oldest);
        }
        lock (_jobs) _jobs.Add(job);
        ListChanged?.Invoke();
        if (job.State == JobState.Ready && job.Kind == JobKind.File)
        {
            var preview = CreateThumbnailAsync(job, _life.Token);
            lock (_previews)
            {
                _previews.RemoveAll(t => t.IsCompleted);
                _previews.Add(preview);
            }
        }
    }

    // ───────────────────────── running ─────────────────────────

    /// <summary>Starts every ready job. With <see cref="ConversionOptions.MergeImages"/> all images going to PDF become one document (in list order).</summary>
    public void StartAll(ConversionOptions options)
    {
        var ready = Jobs.Where(j => j.State == JobState.Ready).ToList();
        var images = ready.Where(j => j.Kind == JobKind.File && j.Target == FileFormat.Pdf && FileFlowCatalog.IsImage(j.Format)).ToList();
        if (options.MergeImages && images.Count >= 2)
        {
            var bundle = new FileFlowJob(JobKind.ImageBundle, Path.GetFileNameWithoutExtension(images[0].DisplayName) + ".pdf",
                images.SelectMany(j => j.Sources).ToList(), images[0].Format, FileFormat.Pdf, images.Sum(j => j.SizeBytes), _now())
            {
                Thumbnail = images[0].Thumbnail
            };
            lock (_jobs)
            {
                var index = _jobs.IndexOf(images[0]);
                foreach (var image in images) _jobs.Remove(image);
                _jobs.Insert(Math.Max(0, index), bundle);
            }
            ListChanged?.Invoke();
            ready = ready.Except(images).Append(bundle).ToList();
        }
        foreach (var job in ready) Start(job, options);
    }

    public void Start(FileFlowJob job, ConversionOptions options)
    {
        if (job.State != JobState.Ready || _disposed) return;
        job.Cancellation = new CancellationTokenSource();
        Set(job, JobState.Queued, 0);
        _ = RunAsync(job, options, job.Cancellation.Token);
    }

    /// <summary>Converts and waits (used by the tests and the UI's single-job start).</summary>
    public Task RunNowAsync(FileFlowJob job, ConversionOptions options)
    {
        if (job.State != JobState.Ready) return Task.CompletedTask;
        job.Cancellation = new CancellationTokenSource();
        Set(job, JobState.Queued, 0);
        return RunAsync(job, options, job.Cancellation.Token);
    }

    private async Task RunAsync(FileFlowJob job, ConversionOptions options, CancellationToken token)
    {
        var slot = false;
        try
        {
            await _slots.WaitAsync(token);
            slot = true;
            job.Folder = Path.Combine(JobsFolder, RandomName());
            Directory.CreateDirectory(job.Folder);
            job.StoredUtc = _now();
            var output = Directory.CreateDirectory(Path.Combine(job.Folder, "out")).FullName;
            var work = Directory.CreateDirectory(Path.Combine(job.Folder, "work")).FullName;

            if (job.Kind == JobKind.Link)
            {
                Set(job, JobState.Converting, 0);
                var mp3 = await MediaEngine.DownloadAudioAsync(job.Sources[0], output, options.AudioBitrate, Reporter(job), TimeSpan.FromMinutes(30), token);
                job.Outputs.Add(mp3);
                job.DisplayName = Path.GetFileName(mp3);
            }
            else
            {
                Set(job, JobState.Importing, 0);
                var inputs = await ImportAsync(job, work, token);
                Set(job, JobState.Converting, 0);
                await ConvertAsync(job, inputs, work, output, options, token);
                TryDelete(work); // only the results are kept until they expire
            }
            if (job.Outputs.Count == 0) throw new ConversionException("ff.err.failed");
            Set(job, JobState.Done, 1);
        }
        catch (OperationCanceledException)
        {
            Cleanup(job);
            Set(job, JobState.Canceled, 0);
        }
        catch (ConversionException error)
        {
            Cleanup(job);
            Fail(job, error.Key, error.Args);
        }
        catch (Exception error)
        {
            // Never let one broken file take Freedom down.
            Cleanup(job);
            Fail(job, error is IOException or UnauthorizedAccessException ? "ff.err.read" : "ff.err.failed", Array.Empty<object>());
        }
        finally
        {
            if (slot) _slots.Release();
        }
    }

    /// <summary>Copies the originals under random names (with progress) and checks them again – the copy is what gets converted.</summary>
    private async Task<List<string>> ImportAsync(FileFlowJob job, string work, CancellationToken token)
    {
        var total = Math.Max(1, job.SizeBytes);
        long copied = 0;
        var inputs = new List<string>();
        foreach (var source in job.Sources)
        {
            var detection = FormatDetector.Detect(source);
            if (detection.Format is FileFormat.Executable) throw new ConversionException("ff.err.executable");
            if (detection.Format == FileFormat.Unknown) throw new ConversionException("ff.err.unsupported");
            var target = Path.Combine(work, RandomName() + FileFlowCatalog.Info(detection.Format).Extension);
            await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true))
            {
                if (from.Length > MaxFileBytes) throw new ConversionException("ff.err.tooLarge", FormatSize(MaxFileBytes));
                await using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true);
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await from.ReadAsync(buffer, token)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, read), token);
                    copied += read;
                    job.Progress = Math.Min(1, copied / (double)total);
                }
            }
            // Check the copy too: the original could have been swapped in the meantime.
            var check = FormatDetector.Detect(target);
            if (check.Format != detection.Format) throw new ConversionException("ff.err.changed");
            File.SetAttributes(target, FileAttributes.ReadOnly | FileAttributes.Temporary);
            inputs.Add(target);
        }
        return inputs;
    }

    private async Task ConvertAsync(FileFlowJob job, List<string> inputs, string work, string outDir, ConversionOptions options, CancellationToken token)
    {
        var from = job.Format;
        var to = job.Target;
        var progress = Reporter(job);
        var baseName = SafeBaseName(job.Kind == JobKind.ImageBundle ? job.DisplayName : job.DisplayName);
        var output = Path.Combine(outDir, baseName + FileFlowCatalog.Info(to).Extension);
        var category = FileFlowCatalog.Info(from).Category;

        if (job.Kind == JobKind.ImageBundle || (FileFlowCatalog.IsImage(from) && to == FileFormat.Pdf))
        {
            var writer = new SimplePdfWriter();
            for (var i = 0; i < inputs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var format = FormatDetector.Detect(inputs[i]).Format;
                var image = ImageEngine.Resize(await ImageEngine.LoadAsync(inputs[i], format, work, token), options.ImageMaxSide);
                writer.AddImagePage(ImageEngine.Jpeg(image, options.ImageQuality), image.PixelWidth, image.PixelHeight, options.PageSize);
                progress.Report((i + 1) / (double)inputs.Count * 0.95);
            }
            writer.Save(output);
        }
        else if (category is FormatCategory.Audio or FormatCategory.Video || (from == FileFormat.Gif && to == FileFormat.Mp4))
        {
            var timeout = to == FileFormat.Mp4 ? TimeSpan.FromMinutes(90) : TimeSpan.FromMinutes(30);
            await MediaEngine.ConvertAsync(inputs[0], to, output, options, progress, timeout, token);
        }
        else if (FileFlowCatalog.IsImage(from))
        {
            await ImageEngine.ConvertAsync(inputs[0], from, to, output, work, options, token);
        }
        else if (from == FileFormat.Pdf)
        {
            switch (to)
            {
                case FileFormat.Jpg or FileFormat.Png:
                    var pages = await PdfEngine.RenderPagesAsync(inputs[0], to, outDir, baseName, options, progress, token);
                    job.Outputs.AddRange(pages);
                    return;
                case FileFormat.Txt: await Task.Run(() => PdfEngine.ToText(inputs[0], output, token), token); break;
                case FileFormat.Docx: await Task.Run(() => PdfEngine.ToDocx(inputs[0], output, token), token); break;
                default: throw new ConversionException("ff.err.unsupported");
            }
        }
        else if (from == FileFormat.Docx && to == FileFormat.Txt)
        {
            var text = await Task.Run(() => DocxDocument.ReadText(inputs[0]), token);
            await File.WriteAllTextAsync(output, text, new System.Text.UTF8Encoding(true), token);
        }
        else if (to == FileFormat.Pdf)
        {
            await OfficeEngine.ToPdfAsync(inputs[0], from, output, work, options, token);
        }
        else throw new ConversionException("ff.err.unsupported");

        if (!File.Exists(output)) throw new ConversionException("ff.err.failed");
        job.Outputs.Add(output);
    }

    private IProgress<double> Reporter(FileFlowJob job) => new SyncProgress(value => job.Progress = Math.Clamp(value, 0, 1));

    /// <summary>Progress without SynchronizationContext hops (the UI reads it on a timer).</summary>
    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _report;
        public SyncProgress(Action<double> report) => _report = report;
        public void Report(double value) => _report(value);
    }

    // ───────────────────────── managing ─────────────────────────

    public void Cancel(FileFlowJob job)
    {
        if (job.State == JobState.Ready) return;
        job.Cancellation?.Cancel();
    }

    /// <summary>Removes the row, stops a running conversion and deletes its stored files.</summary>
    public void Remove(FileFlowJob job)
    {
        job.Cancellation?.Cancel();
        if (!job.IsActive) Cleanup(job);
        lock (_jobs) _jobs.Remove(job);
        ListChanged?.Invoke();
    }

    public void Clear()
    {
        foreach (var job in Jobs) Remove(job);
    }

    /// <summary>Moves a ready job up or down (the order of images in a combined PDF).</summary>
    public void Move(FileFlowJob job, int delta)
    {
        lock (_jobs)
        {
            var index = _jobs.IndexOf(job);
            var target = index + delta;
            if (index < 0 || target < 0 || target >= _jobs.Count) return;
            _jobs.RemoveAt(index);
            _jobs.Insert(target, job);
        }
        ListChanged?.Invoke();
    }

    /// <summary>Deletes results older than the retention time (runs every minute).</summary>
    public void RemoveExpired()
    {
        var now = _now();
        var jobs = Jobs;
        foreach (var job in jobs)
        {
            if (job.ExpiresUtc is not { } expires || expires > now || job.IsActive) continue;
            Cleanup(job);
            job.Outputs.Clear();
            Set(job, JobState.Expired, 0);
        }
        // Folders nobody tracks any more (e.g. removed rows whose files were locked).
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(JobsFolder))
                if (now - Directory.GetCreationTimeUtc(folder) > Retention && jobs.All(j => j.Folder != folder)) TryDelete(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Try again in a minute.
        }
    }

    /// <summary>Copies the results into a folder; returns the written paths (existing names get a number).</summary>
    public static List<string> SaveOutputs(FileFlowJob job, string folder)
    {
        var written = new List<string>();
        foreach (var output in job.Outputs.Where(File.Exists))
        {
            var target = UniquePath(Path.Combine(folder, Path.GetFileName(output)));
            File.Copy(output, target);
            File.SetAttributes(target, FileAttributes.Normal);
            written.Add(target);
        }
        return written;
    }

    /// <summary>All results of the given jobs in one ZIP (duplicate names get a number).</summary>
    public static void SaveZip(IEnumerable<FileFlowJob> jobs, string zipPath)
    {
        var temp = zipPath + ".part";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var output in jobs.Where(j => j.State == JobState.Done).SelectMany(j => j.Outputs).Where(File.Exists))
            {
                var name = Path.GetFileName(output);
                var stem = Path.GetFileNameWithoutExtension(name);
                for (var i = 2; !names.Add(name); i++) name = $"{stem} ({i}){Path.GetExtension(output)}";
                zip.CreateEntryFromFile(output, name, CompressionLevel.Fastest);
            }
        }
        File.Move(temp, zipPath, overwrite: true);
    }

    private void Set(FileFlowJob job, JobState state, double progress)
    {
        job.State = state;
        job.Progress = progress;
        JobChanged?.Invoke(job);
    }

    private void Fail(FileFlowJob job, string key, object[] args, bool notify = true)
    {
        job.ErrorKey = key;
        job.ErrorArgs = args;
        job.State = JobState.Failed;
        job.Progress = 0;
        if (notify) JobChanged?.Invoke(job);
    }

    private static void Cleanup(FileFlowJob job)
    {
        if (job.Folder is not null) TryDelete(job.Folder);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A program still holds a file; the minute timer tries again.
        }
    }

    // ───────────────────────── previews ─────────────────────────

    private async Task CreateThumbnailAsync(FileFlowJob job, CancellationToken token)
    {
        try
        {
            var path = job.Sources[0];
            BitmapSource? thumb = null;
            if (FileFlowCatalog.IsImage(job.Format) && job.Format != FileFormat.Svg)
                thumb = await Task.Run(() => Thumbnail(path));
            else if (job.Format == FileFormat.Pdf)
                thumb = await PdfThumbnailAsync(path);
            else if (FileFlowCatalog.Info(job.Format).Category == FormatCategory.Video && ToolLocator.IsAvailable(ExternalTool.FFmpeg))
            {
                var file = Path.Combine(WorkRoot, "thumbs", RandomName() + ".jpg");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await MediaEngine.RunAsync(new[] { "-ss", "1", "-i", path, "-frames:v", "1", "-vf", "scale=160:-2" }, file, TimeSpan.FromSeconds(30), token);
                thumb = await Task.Run(() => Thumbnail(file));
                File.Delete(file);
            }
            if (thumb is null) return;
            job.Thumbnail = thumb;
            JobChanged?.Invoke(job);
        }
        catch (Exception)
        {
            // A preview is optional.
        }
    }

    private static BitmapSource? Thumbnail(string path)
    {
        var image = new BitmapImage();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = 160;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static async Task<BitmapSource?> PdfThumbnailAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var document = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        if (document.PageCount == 0) return null;
        using var page = document.GetPage(0);
        using var memory = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(memory, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 160, BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255) });
        memory.Seek(0);
        var decoder = BitmapDecoder.Create(System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(memory), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>Folder names start with the owner's process id; folders of ended processes are leftovers.</summary>
    private static bool BelongsToRunningProcess(string folderName)
    {
        var dash = folderName.IndexOf('-');
        if (dash <= 0 || !int.TryParse(folderName.AsSpan(0, dash), out var pid) || pid == Environment.ProcessId) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Unpredictable name for temp files (never derived from the user's file name).</summary>
    public static string RandomName() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>File name for results: the original name without its extension, cleaned of characters Windows does not allow.</summary>
    public static string SafeBaseName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(stem.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length > 80) clean = clean[..80].Trim();
        return clean.Length == 0 ? "datei" : clean;
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return (unit == 0 ? value.ToString("0", Loc.Culture) : value.ToString(value < 10 ? "0.0" : "0", Loc.Culture)) + " " + units[unit];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanup.Dispose();
        _life.Cancel();
        foreach (var job in Jobs) job.Cancellation?.Cancel();
        // Give running programs a moment to end (their process trees are killed), then remove every stored file.
        Task[] previews;
        lock (_previews) previews = _previews.ToArray();
        try
        {
            Task.WaitAll(previews, 3000);
        }
        catch (AggregateException)
        {
            // Previews are optional.
        }
        Thread.Sleep(200);
        TryDelete(JobsFolder);
        TryDelete(Path.Combine(WorkRoot, "thumbs"));
    }
}
