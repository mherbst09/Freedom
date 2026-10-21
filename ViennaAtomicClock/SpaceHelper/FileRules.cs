using System.IO;
using System.Runtime.InteropServices;

namespace ViennaAtomicClock.SpaceHelper;

/// <summary>Important Windows folders. Replaceable for tests.</summary>
public sealed record SystemPaths(string Windows, string ProgramFiles, string ProgramFilesX86, string ProgramData, string UserProfile,
    string LocalAppData, string RoamingAppData, string Temp, string Downloads)
{
    public static SystemPaths Current { get; } = Detect();

    private static SystemPaths Detect()
    {
        static string F(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
        var profile = F(Environment.SpecialFolder.UserProfile);
        return new SystemPaths(
            F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData), profile, F(Environment.SpecialFolder.LocalApplicationData), F(Environment.SpecialFolder.ApplicationData),
            Path.GetTempPath(), KnownDownloads() ?? Path.Combine(profile, "Downloads"));
    }

    /// <summary>The real Downloads folder (it can be moved to another drive).</summary>
    private static string? KnownDownloads()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer) != 0) return null;
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}

/// <summary>
/// Decides the file type and how safe removing a file is. The rules are deliberately cautious: Windows, program,
/// driver, recovery and profile files are "Nicht löschen" unless a known cache/temp location makes them clearly safe.
/// </summary>
public static class FileRules
{
    private static readonly Dictionary<string, FileKind> Kinds = new(StringComparer.OrdinalIgnoreCase);

    static FileRules()
    {
        void Add(FileKind kind, string list)
        {
            foreach (var ext in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) Kinds["." + ext] = kind;
        }
        Add(FileKind.Video, "mp4 mkv mov avi wmv flv webm m4v mpg mpeg 3gp ts mts m2ts vob ogv");
        Add(FileKind.Image, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 nef arw dng psd svg ico avif");
        Add(FileKind.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt md csv epub one pages key numbers xps");
        Add(FileKind.Archive, "zip rar 7z tar gz tgz bz2 xz iso img cab zst lz wim vhd vhdx");
        Add(FileKind.Program, "exe msi msix msixbundle appx appxbundle dll sys bat cmd ps1 jar apk com scr drv efi");
    }

    public static FileKind KindOf(string path) => Kinds.TryGetValue(Path.GetExtension(path), out var kind) ? kind : FileKind.Other;

    /// <summary>System and driver file types that are never offered for removal.</summary>
    private static readonly HashSet<string> SystemExtensions = new(StringComparer.OrdinalIgnoreCase) { ".sys", ".drv", ".efi", ".cat", ".mui", ".manifest" };

    /// <summary>Files in the root of a drive that Windows needs.</summary>
    private static readonly HashSet<string> RootSystemFiles = new(StringComparer.OrdinalIgnoreCase) { "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "DumpStack.log", "DumpStack.log.tmp" };

    /// <summary>Folders below a drive root that belong to Windows (restore points, recovery, boot).</summary>
    private static readonly string[] RootSystemFolders = { "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI", "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "Config.Msi", "Documents and Settings" };

    public static bool IsUnder(string path, string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        var full = Normalize(path);
        var root = Normalize(folder).TrimEnd('\\') + "\\";
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || string.Equals(full.TrimEnd('\\') + "\\", root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full path; paths that are already absolute and clean (as the scanner delivers them) are used as they are.</summary>
    internal static string Normalize(string path)
    {
        if (Path.IsPathFullyQualified(path) && path.IndexOf('/') < 0 && !path.Contains("\\.", StringComparison.Ordinal)) return path;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>The important folders as "C:\…\" prefixes, prepared once per <see cref="SystemPaths"/> (the rules run for every file).</summary>
    internal sealed class Prefixes
    {
        public Prefixes(SystemPaths p)
        {
            static string P(string folder) => Normalize(folder).TrimEnd('\\') + "\\";
            Temp = P(p.Temp);
            WindowsTemp = P(Path.Combine(p.Windows, "Temp"));
            Explorer = P(Path.Combine(p.LocalAppData, "Microsoft", "Windows", "Explorer"));
            WerLocal = P(Path.Combine(p.LocalAppData, "Microsoft", "Windows", "WER"));
            WerData = P(Path.Combine(p.ProgramData, "Microsoft", "Windows", "WER"));
            CrashDumps = P(Path.Combine(p.LocalAppData, "CrashDumps"));
            Windows = P(p.Windows);
            ProgramFiles = P(p.ProgramFiles);
            ProgramFilesX86 = P(p.ProgramFilesX86);
            ProgramData = P(p.ProgramData);
            Local = P(p.LocalAppData);
            Roaming = P(p.RoamingAppData);
            Profile = P(p.UserProfile);
        }

        public string Temp, WindowsTemp, Explorer, WerLocal, WerData, CrashDumps, Windows, ProgramFiles, ProgramFilesX86, ProgramData, Local, Roaming, Profile;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SystemPaths, Prefixes> CompiledPrefixes = new();

    internal static Prefixes For(SystemPaths paths) => CompiledPrefixes.GetValue(paths, p => new Prefixes(p));

    /// <summary>Fast "inside this folder" test for a normalized path and a prefix ending in "\".</summary>
    internal static bool Under(string full, string prefix) =>
        full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || (full.Length == prefix.Length - 1 && prefix.StartsWith(full, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] RootSystemPrefixes = RootSystemFolders.Select(f => f + "\\").ToArray();

    /// <summary>Known locations whose content Windows or the programs rebuild on their own.</summary>
    public static bool IsKnownCacheLocation(string path, SystemPaths paths) => IsKnownCache(Normalize(path), For(paths));

    private static bool IsKnownCache(string full, Prefixes p)
    {
        if (Under(full, p.Temp) || Under(full, p.WindowsTemp)) return true;
        if (Under(full, p.Explorer) && Path.GetFileName(full.AsSpan()).StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase)) return true;
        if (Under(full, p.WerLocal) || Under(full, p.WerData) || Under(full, p.CrashDumps)) return true;
        return (Under(full, p.Local) || Under(full, p.Roaming)) && BrowserCache.IsCacheCandidate(full, p);
    }

    /// <summary>Hard protection: Windows, programs, drivers, restore points, profile data. Returns a reason key or null.</summary>
    public static string? ProtectionReason(string path, SystemPaths paths, FileAttributes attributes = 0)
    {
        var full = Normalize(path);
        var p = For(paths);
        if (IsKnownCache(full, p)) return null;
        if ((attributes & FileAttributes.System) != 0 && (attributes & FileAttributes.Hidden) != 0) return "sh.reason.systemFile";
        var root = Path.GetPathRoot(full) ?? "";
        var name = Path.GetFileName(full);
        if (full.Length - name.Length == root.Length && RootSystemFiles.Contains(name)) return "sh.reason.systemFile";
        if (SystemExtensions.Contains(Path.GetExtension(full))) return "sh.reason.driver";
        var below = full.AsSpan(root.Length);
        foreach (var folder in RootSystemPrefixes)
            if (below.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || below.Equals(folder.AsSpan(0, folder.Length - 1), StringComparison.OrdinalIgnoreCase)) return "sh.reason.restore";
        if (Under(full, p.Windows)) return "sh.reason.windows";
        if (Under(full, p.ProgramFiles) || Under(full, p.ProgramFilesX86)) return "sh.reason.programs";
        if (Under(full, p.ProgramData)) return "sh.reason.programData";
        if (Under(full, p.Local) || Under(full, p.Roaming)) return "sh.reason.appData";
        // Registry hives and profile files directly in the user folder.
        if (full.Length - name.Length == p.Profile.Length && Under(full, p.Profile) &&
            (name.StartsWith("ntuser", StringComparison.OrdinalIgnoreCase) || name.StartsWith("UsrClass", StringComparison.OrdinalIgnoreCase)))
            return "sh.reason.profile";
        return null;
    }

    /// <summary>Rates an item and explains why (Sicher löschbar / Prüfung empfohlen / Nicht löschen).</summary>
    public static void Assess(FoundFile file, SystemPaths paths, FileAttributes attributes = 0)
    {
        var protection = ProtectionReason(file.Path, paths, attributes);
        if (protection is not null)
        {
            file.Safety = Safety.Keep;
            file.ReasonKey = protection;
            return;
        }
        // Cloud placeholders: removing them removes the file in the cloud too.
        if ((attributes & (FileAttributes.Offline | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */)) != 0)
        {
            file.Safety = Safety.Review;
            file.ReasonKey = "sh.reason.cloud";
            return;
        }
        (file.Safety, file.ReasonKey) = file.Finding switch
        {
            Finding.WindowsTemp or Finding.AppTemp => (Safety.Safe, "sh.reason.temp"),
            Finding.Thumbnails => (Safety.Safe, "sh.reason.thumbnails"),
            Finding.Logs => (Safety.Safe, "sh.reason.logs"),
            Finding.BrowserCache => (Safety.Safe, "sh.reason.browserCache"),
            Finding.Installers => (Safety.Review, "sh.reason.installer"),
            Finding.Duplicate when file.IsGroupOriginal => (Safety.Keep, "sh.reason.original"),
            Finding.Duplicate => (Safety.Review, "sh.reason.duplicate"),
            Finding.OldDownload => (Safety.Review, "sh.reason.oldDownload"),
            Finding.Stale => (Safety.Review, "sh.reason.stale"),
            _ => (Safety.Review, file.Kind == FileKind.Program ? "sh.reason.program" : "sh.reason.large")
        };
    }
}

/// <summary>Browser cache folders: only the HTTP/code/GPU caches – never cookies, passwords, history or sessions.</summary>
public static class BrowserCache
{
    /// <summary>Chromium profile sub-folders that only contain re-downloadable cache data.</summary>
    private static readonly string[] ChromiumCacheFolders = { "Cache", "Code Cache", "GPUCache", "Service Worker\\CacheStorage", "DawnCache", "DawnGraphiteCache", "GrShaderCache", "ShaderCache" };

    /// <summary>Files that must never be touched even if they sit next to a cache.</summary>
    private static readonly string[] Forbidden = { "Cookies", "Login Data", "Web Data", "History", "Bookmarks", "Preferences", "Secure Preferences", "Sessions", "Session Storage", "Local Storage", "IndexedDB", "Network", "key4.db", "logins.json", "cookies.sqlite", "places.sqlite", "sessionstore" };

    /// <summary>Chromium "User Data" folders (Chrome, Edge, Brave, Vivaldi, Opera) under %LocalAppData%/%AppData%.</summary>
    public static IEnumerable<(string Browser, string Folder)> Locations(SystemPaths paths)
    {
        var chromium = new (string Name, string Path)[]
        {
            ("Google Chrome", Path.Combine(paths.LocalAppData, "Google", "Chrome", "User Data")),
            ("Microsoft Edge", Path.Combine(paths.LocalAppData, "Microsoft", "Edge", "User Data")),
            ("Brave", Path.Combine(paths.LocalAppData, "BraveSoftware", "Brave-Browser", "User Data")),
            ("Vivaldi", Path.Combine(paths.LocalAppData, "Vivaldi", "User Data")),
            ("Opera", Path.Combine(paths.RoamingAppData, "Opera Software", "Opera Stable"))
        };
        foreach (var (name, root) in chromium)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> profiles;
            try
            {
                profiles = Directory.EnumerateDirectories(root).Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ", StringComparison.Ordinal)).Append(root).ToList();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var profile in profiles)
                foreach (var cache in ChromiumCacheFolders)
                {
                    var folder = Path.Combine(profile, cache);
                    if (Directory.Exists(folder)) yield return (name, folder);
                }
        }
        // Firefox keeps its cache under Local, separate from the profile data in Roaming.
        var firefox = Path.Combine(paths.LocalAppData, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefox))
        {
            List<string> profiles;
            try
            {
                profiles = Directory.EnumerateDirectories(firefox).ToList();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                profiles = new List<string>();
            }
            foreach (var profile in profiles)
            {
                var cache = Path.Combine(profile, "cache2");
                if (Directory.Exists(cache)) yield return ("Firefox", cache);
            }
        }
    }

    private static readonly string[] ForbiddenSegments = Forbidden.Select(f => "\\" + f + "\\").ToArray();
    private static readonly string[] CacheSegments = ChromiumCacheFolders.Select(c => "\\" + c + "\\").ToArray();

    public static bool IsCachePath(string path, SystemPaths paths)
    {
        var full = FileRules.Normalize(path);
        var p = FileRules.For(paths);
        return (FileRules.Under(full, p.Local) || FileRules.Under(full, p.Roaming)) && IsCacheCandidate(full, p);
    }

    /// <summary>For a normalized path already known to be inside AppData.</summary>
    internal static bool IsCacheCandidate(string full, FileRules.Prefixes p)
    {
        var isFirefox = full.Contains("\\Mozilla\\Firefox\\Profiles\\", StringComparison.OrdinalIgnoreCase);
        var isChromium = full.Contains("\\User Data\\", StringComparison.OrdinalIgnoreCase) || full.Contains("\\Opera Stable\\", StringComparison.OrdinalIgnoreCase);
        if (!isFirefox && !isChromium) return false; // the common case ends here cheaply
        var name = Path.GetFileName(full);
        foreach (var forbidden in Forbidden)
            if (name.Equals(forbidden, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var segment in ForbiddenSegments)
            if (full.Contains(segment, StringComparison.OrdinalIgnoreCase)) return false;
        if (isFirefox) return full.Contains("\\cache2\\", StringComparison.OrdinalIgnoreCase) && FileRules.Under(full, p.Local);
        foreach (var segment in CacheSegments)
            if (full.Contains(segment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
