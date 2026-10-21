using System.Diagnostics;
using System.IO;
using System.Text;

namespace ViennaAtomicClock.FileFlow;

public enum ExternalTool { FFmpeg, FFprobe, YtDlp, LibreOffice }

/// <summary>Finds the free open-source helpers (PATH, winget links, winget packages, default install folders).</summary>
public static class ToolLocator
{
    private static readonly Dictionary<ExternalTool, string?> Cache = new();

    public static string? Find(ExternalTool tool)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(tool, out var cached) && (cached is null || File.Exists(cached))) return cached;
            var found = Search(tool);
            Cache[tool] = found;
            return found;
        }
    }

    /// <summary>Forgets earlier results (after installing a program while Freedom runs).</summary>
    public static void Refresh()
    {
        lock (Cache) Cache.Clear();
    }

    public static bool IsAvailable(ExternalTool tool) => Find(tool) is not null;

    private static string? Search(ExternalTool tool)
    {
        var exe = tool switch
        {
            ExternalTool.FFmpeg => "ffmpeg.exe",
            ExternalTool.FFprobe => "ffprobe.exe",
            ExternalTool.YtDlp => "yt-dlp.exe",
            _ => "soffice.exe"
        };
        var candidates = new List<string>();
        // The process PATH first, then the saved user and machine PATH (programs installed while Freedom runs).
        var paths = string.Join(Path.PathSeparator, Environment.GetEnvironmentVariable("PATH") ?? "",
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "", Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "");
        foreach (var dir in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim('"')), exe));
            }
            catch (ArgumentException)
            {
                // Broken PATH entry.
            }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", exe));
        if (tool == ExternalTool.LibreOffice)
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "LibreOffice", "program", exe));
        }
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;
        // winget "portable" packages live in versioned folders.
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            try
            {
                // The "full" FFmpeg build has every codec (librsvg for SVG, libwebp …).
                return Directory.EnumerateFiles(packages, exe, SearchOption.AllDirectories)
                    .OrderByDescending(p => p.Contains("full_build", StringComparison.OrdinalIgnoreCase)).ThenByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }
}

/// <summary>Outcome of an external program run.</summary>
public sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Raised when a conversion fails with a message meant for the user (a string table key + arguments).</summary>
public sealed class ConversionException : Exception
{
    public ConversionException(string key, params object[] args) : base(key)
    {
        Key = key;
        Args = args;
    }

    public string Key { get; }
    public object[] Args { get; }
}

/// <summary>
/// Starts an external program safely: arguments as a list (no shell, no command-line injection), no window,
/// output collected line by line, hard time limit and cancellation that kills the whole process tree.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken token,
        Action<string>? onLine = null, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath()
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // Helpers find their own helpers (yt-dlp needs a JavaScript runtime such as Deno) even if they were installed after Freedom started.
        info.Environment["PATH"] = string.Join(Path.PathSeparator, new[]
        {
            Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        }.Where(p => !string.IsNullOrEmpty(p)));
        var tail = new Queue<string>();
        void Collect(string? line)
        {
            if (line is null) return;
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 60) tail.Dequeue();
            }
            onLine?.Invoke(line);
        }

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived += (_, e) => Collect(e.Data);
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ConversionException("ff.err.toolStart", Path.GetFileName(exe));
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal; // conversions never make Freedom sluggish
        }
        catch (InvalidOperationException)
        {
            // Already finished.
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (token.IsCancellationRequested) throw;
            throw new ConversionException("ff.err.timeout", (int)Math.Ceiling(timeout.TotalMinutes));
        }
        process.WaitForExit(); // flush the output events
        string output;
        lock (tail) output = string.Join("\n", tail);
        return new ProcessResult(process.ExitCode, output);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
