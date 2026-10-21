using System.IO;
using System.IO.Compression;
using ViennaAtomicClock.SpaceHelper;
using Xunit;

namespace Freedom.Tests;

/// <summary>A fake Windows layout inside a temp folder, so the rules can be tested without touching real system files.</summary>
internal sealed class FakeSystem : IDisposable
{
    public FakeSystem()
    {
        Root = Directory.CreateTempSubdirectory("spacehelper-").FullName;
        string D(params string[] parts) => Directory.CreateDirectory(Path.Combine(new[] { Root }.Concat(parts).ToArray())).FullName;
        var profile = D("Users", "Anna");
        Paths = new SystemPaths(D("Windows"), D("Program Files"), D("Program Files (x86)"), D("ProgramData"), profile,
            D("Users", "Anna", "AppData", "Local"), D("Users", "Anna", "AppData", "Roaming"), D("Users", "Anna", "AppData", "Local", "Temp"), D("Users", "Anna", "Downloads"));
    }

    public string Root { get; }
    public SystemPaths Paths { get; }

    public string File(string relative, int size = 1000, DateTime? modified = null, byte fill = 1)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[size];
        Array.Fill(data, fill);
        System.IO.File.WriteAllBytes(path, data);
        if (modified is { } time)
        {
            System.IO.File.SetLastWriteTimeUtc(path, time);
            System.IO.File.SetLastAccessTimeUtc(path, time);
        }
        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) System.IO.File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

public sealed class SpaceRulesTests
{
    [Theory]
    [InlineData("film.MKV", FileKind.Video)]
    [InlineData("urlaub.heic", FileKind.Image)]
    [InlineData("bericht.docx", FileKind.Document)]
    [InlineData("backup.7z", FileKind.Archive)]
    [InlineData("setup.msi", FileKind.Program)]
    [InlineData("daten.xyz", FileKind.Other)]
    [InlineData("ohne_endung", FileKind.Other)]
    public void File_types_are_grouped(string name, FileKind kind) => Assert.Equal(kind, FileRules.KindOf(name));

    [Fact]
    public void System_program_driver_and_profile_files_are_protected()
    {
        using var fake = new FakeSystem();
        var p = fake.Paths;
        var root = Path.GetPathRoot(fake.Root)!;
        Assert.Equal("sh.reason.windows", FileRules.ProtectionReason(Path.Combine(p.Windows, "System32", "kernel32.dll"), p));
        Assert.Equal("sh.reason.programs", FileRules.ProtectionReason(Path.Combine(p.ProgramFiles, "App", "app.exe"), p));
        Assert.Equal("sh.reason.programs", FileRules.ProtectionReason(Path.Combine(p.ProgramFilesX86, "Old", "data.bin"), p));
        Assert.Equal("sh.reason.programData", FileRules.ProtectionReason(Path.Combine(p.ProgramData, "Vendor", "license.dat"), p));
        Assert.Equal("sh.reason.appData", FileRules.ProtectionReason(Path.Combine(p.RoamingAppData, "Game", "save.dat"), p));
        Assert.Equal("sh.reason.profile", FileRules.ProtectionReason(Path.Combine(p.UserProfile, "NTUSER.DAT"), p));
        Assert.Equal("sh.reason.driver", FileRules.ProtectionReason(Path.Combine(p.UserProfile, "Downloads", "treiber.sys"), p));
        Assert.Equal("sh.reason.restore", FileRules.ProtectionReason(Path.Combine(root, "System Volume Information", "x"), p));
        Assert.Equal("sh.reason.systemFile", FileRules.ProtectionReason(Path.Combine(root, "pagefile.sys"), p));
        Assert.Equal("sh.reason.systemFile", FileRules.ProtectionReason(Path.Combine(p.UserProfile, "Documents", "x.dat"), p, FileAttributes.System | FileAttributes.Hidden));
        // A folder that only starts with the same letters is not inside.
        Assert.Null(FileRules.ProtectionReason(Path.Combine(fake.Root, "Windows.old.backup", "foto.jpg"), p));
        Assert.Null(FileRules.ProtectionReason(Path.Combine(p.UserProfile, "Documents", "foto.jpg"), p));
    }

    [Fact]
    public void Known_caches_are_safe_but_browser_secrets_are_not()
    {
        using var fake = new FakeSystem();
        var p = fake.Paths;
        Assert.True(FileRules.IsKnownCacheLocation(Path.Combine(p.Temp, "abc.tmp"), p));
        Assert.True(FileRules.IsKnownCacheLocation(Path.Combine(p.Windows, "Temp", "setup.log"), p));
        Assert.True(FileRules.IsKnownCacheLocation(Path.Combine(p.LocalAppData, "Microsoft", "Windows", "Explorer", "thumbcache_256.db"), p));
        Assert.False(FileRules.IsKnownCacheLocation(Path.Combine(p.LocalAppData, "Microsoft", "Windows", "Explorer", "other.db"), p));
        var chrome = Path.Combine(p.LocalAppData, "Google", "Chrome", "User Data", "Default");
        Assert.True(BrowserCache.IsCachePath(Path.Combine(chrome, "Cache", "Cache_Data", "f_000001"), p));
        Assert.True(BrowserCache.IsCachePath(Path.Combine(chrome, "Code Cache", "js", "abc"), p));
        Assert.False(BrowserCache.IsCachePath(Path.Combine(chrome, "Cookies"), p));
        Assert.False(BrowserCache.IsCachePath(Path.Combine(chrome, "Login Data"), p));
        Assert.False(BrowserCache.IsCachePath(Path.Combine(chrome, "Network", "Cookies"), p));
        Assert.False(BrowserCache.IsCachePath(Path.Combine(chrome, "Sessions", "Session_1"), p));
        Assert.True(BrowserCache.IsCachePath(Path.Combine(p.LocalAppData, "Mozilla", "Firefox", "Profiles", "x.default", "cache2", "entries", "A1"), p));
        Assert.False(BrowserCache.IsCachePath(Path.Combine(p.RoamingAppData, "Mozilla", "Firefox", "Profiles", "x.default", "logins.json"), p));
        Assert.Equal("sh.reason.appData", FileRules.ProtectionReason(Path.Combine(chrome, "Login Data"), p));
    }

    [Fact]
    public void Ratings_follow_the_finding()
    {
        using var fake = new FakeSystem();
        FoundFile F(string path, Finding finding) => new(path, 10, DateTime.UtcNow, DateTime.UtcNow, finding);
        var temp = F(Path.Combine(fake.Paths.Temp, "a.tmp"), Finding.AppTemp);
        var download = F(Path.Combine(fake.Paths.Downloads, "alt.zip"), Finding.OldDownload);
        var system = F(Path.Combine(fake.Paths.Windows, "System32", "big.dll"), Finding.Large);
        var original = F(Path.Combine(fake.Paths.UserProfile, "a.jpg"), Finding.Duplicate);
        original.IsGroupOriginal = true;
        foreach (var file in new[] { temp, download, system, original }) FileRules.Assess(file, fake.Paths);
        Assert.Equal(Safety.Safe, temp.Safety);
        Assert.Equal(Safety.Review, download.Safety);
        Assert.Equal(Safety.Keep, system.Safety);
        Assert.False(system.CanSelect);
        Assert.Equal(Safety.Keep, original.Safety);
        Assert.Equal("sh.reason.original", original.ReasonKey);
    }
}

public sealed class SpaceScannerTests
{
    [Fact]
    public void Folder_sizes_types_and_large_files_add_up()
    {
        using var fake = new FakeSystem();
        var docs = Path.Combine(fake.Paths.UserProfile, "Documents");
        fake.File(@"Users\Anna\Documents\a.pdf", 3000);
        fake.File(@"Users\Anna\Documents\Fotos\b.jpg", 5000);
        fake.File(@"Users\Anna\Documents\Fotos\Urlaub\c.mp4", 2 * 1024 * 1024);
        fake.File(@"Users\Anna\Documents\d.zip", 700);
        var settings = new SpaceSettings { LargeFileMb = 10 }.Normalize();
        var result = new SpaceScanner(settings, fake.Paths).Scan(docs, 0, null, CancellationToken.None);

        Assert.Equal(4, result.Files);
        Assert.Equal(3000 + 5000 + 2 * 1024 * 1024 + 700, result.Bytes);
        Assert.Equal(result.Bytes, result.Tree.Size);
        var fotos = result.Tree.Children.Single(c => c.Name == "Fotos");
        Assert.Equal(5000 + 2 * 1024 * 1024, fotos.Size);
        Assert.Equal(2, fotos.Files);
        Assert.Equal(2 * 1024 * 1024, fotos.Children.Single().Size);
        Assert.Equal(3700, result.Tree.OwnSize);
        Assert.Equal(2 * 1024 * 1024, result.KindSizes[FileKind.Video]);
        Assert.Equal(3000, result.KindSizes[FileKind.Document]);
        Assert.Equal(700, result.KindSizes[FileKind.Archive]);
        Assert.Empty(result.Large); // nothing above 10 MB
        Assert.Same(fotos, result.Tree.Children[0]); // biggest first
    }

    [Fact]
    public void Large_and_long_unused_files_are_found_with_ratings()
    {
        using var fake = new FakeSystem();
        var old = DateTime.UtcNow.AddYears(-3);
        fake.File(@"Users\Anna\Videos\gross.mkv", 12 * 1024 * 1024);
        fake.File(@"Users\Anna\Videos\alt.mov", 2 * 1024 * 1024, old);
        fake.File(@"Windows\System32\riesig.bin", 11 * 1024 * 1024, old);
        var result = new SpaceScanner(new SpaceSettings { LargeFileMb = 10, StaleDays = 365 }.Normalize(), fake.Paths).Scan(fake.Root, 0, null, CancellationToken.None);
        Assert.Equal(new[] { "gross.mkv", "riesig.bin" }, result.Large.Select(f => f.Name).OrderBy(n => n));
        Assert.Equal(Safety.Keep, result.Large.Single(f => f.Name == "riesig.bin").Safety);
        Assert.Equal(Safety.Review, result.Large.Single(f => f.Name == "gross.mkv").Safety);
        Assert.Equal("alt.mov", Assert.Single(result.Stale).Name); // the Windows file is never offered as unused
    }

    [Fact]
    public void Links_are_not_followed_and_cancel_stops()
    {
        using var fake = new FakeSystem();
        var outside = Directory.CreateDirectory(Path.Combine(fake.Root, "Outside")).FullName;
        fake.File(@"Outside\secret.bin", 5000);
        var area = Directory.CreateDirectory(Path.Combine(fake.Root, "Area")).FullName;
        fake.File(@"Area\own.txt", 100);
        var link = Path.Combine(area, "LinkToOutside");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (IOException)
        {
            // Without the privilege to create links a junction does the same.
            var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false });
            junction!.WaitForExit();
        }
        catch (UnauthorizedAccessException)
        {
            var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false });
            junction!.WaitForExit();
        }
        Assert.True(Directory.Exists(link));
        var result = new SpaceScanner(new SpaceSettings().Normalize(), fake.Paths).Scan(area, 0, null, CancellationToken.None);
        Assert.Equal(1, result.Files);
        Assert.Equal(100, result.Bytes);
        Assert.Equal(1, result.LinksSkipped);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var canceled = new SpaceScanner(new SpaceSettings().Normalize(), fake.Paths).Scan(fake.Root, 0, null, cancel.Token);
        Assert.True(canceled.Canceled);
        Directory.Delete(link);
    }

    [Fact]
    public void Locked_or_missing_folders_do_not_stop_the_scan()
    {
        using var fake = new FakeSystem();
        fake.File(@"Daten\ok.txt", 10);
        var result = new SpaceScanner(new SpaceSettings().Normalize(), fake.Paths).Scan(Path.Combine(fake.Root, "Daten"), 0, null, CancellationToken.None);
        Assert.Equal(1, result.Files);
        var missing = new SpaceScanner(new SpaceSettings().Normalize(), fake.Paths).Scan(Path.Combine(fake.Root, "gibtsnicht"), 0, null, CancellationToken.None);
        Assert.Equal(0, missing.Files);
        Assert.Equal(1, missing.Inaccessible);
    }
}

public sealed class DuplicateFinderTests
{
    [Fact]
    public async Task Only_identical_content_counts_and_the_oldest_is_the_original()
    {
        using var fake = new FakeSystem();
        var old = DateTime.UtcNow.AddDays(-10);
        fake.File(@"Users\Anna\Pictures\foto.jpg", 300_000, old, fill: 7);
        fake.File(@"Users\Anna\Downloads\foto (1).jpg", 300_000, fill: 7);
        fake.File(@"Users\Anna\Desktop\kopie.jpg", 300_000, fill: 7);
        // Same size, same start and end, different middle: only the full hash tells them apart.
        var tricky = fake.File(@"Users\Anna\Pictures\anders.jpg", 300_000, fill: 7);
        using (var stream = new FileStream(tricky, FileMode.Open)) { stream.Seek(150_000, SeekOrigin.Begin); stream.WriteByte(9); }
        fake.File(@"Users\Anna\Pictures\klein.png", 2_000_000, fill: 3); // unique size
        fake.File(@"Windows\System32\foto.jpg", 300_000, fill: 7); // protected: not a candidate

        var settings = new SpaceSettings { DuplicateMinKb = 100 }.Normalize();
        var scan = new SpaceScanner(settings, fake.Paths).Scan(fake.Root, 0, null, CancellationToken.None);
        Assert.Equal(new long[] { 300_000 }, scan.SameSize.Keys);
        var groups = await new DuplicateFinder(fake.Paths, byName: false).FindAsync(scan.SameSize, null, CancellationToken.None);
        var group = Assert.Single(groups);
        Assert.Equal(3, group.Files.Count);
        Assert.Equal(600_000, group.Wasted);
        Assert.Equal("foto.jpg", group.Files[0].Name);
        Assert.True(group.Files[0].IsGroupOriginal);
        Assert.Equal(Safety.Keep, group.Files[0].Safety);
        Assert.All(group.Files.Skip(1), f => Assert.Equal(Safety.Review, f.Safety));
        Assert.DoesNotContain(group.Files, f => f.Name == "anders.jpg");
    }

    [Fact]
    public async Task By_default_only_same_size_and_same_name_are_hashed()
    {
        using var fake = new FakeSystem();
        fake.File(@"a\urlaub.jpg", 200_000, fill: 5);
        fake.File(@"b\urlaub (1).jpg", 200_000, fill: 5);
        fake.File(@"c\Urlaub - Kopie.JPG", 200_000, fill: 5);
        fake.File(@"d\ganz-anders.jpg", 200_000, fill: 5); // same content, other name: only found by the thorough mode
        var scan = new SpaceScanner(new SpaceSettings { DuplicateMinKb = 100 }.Normalize(), fake.Paths).Scan(fake.Root, 0, null, CancellationToken.None);
        var byName = await new DuplicateFinder(fake.Paths).FindAsync(scan.SameSize, null, CancellationToken.None);
        Assert.Equal(3, Assert.Single(byName).Files.Count);
        var thorough = await new DuplicateFinder(fake.Paths, byName: false).FindAsync(scan.SameSize, null, CancellationToken.None);
        Assert.Equal(4, Assert.Single(thorough).Files.Count);
    }

    [Theory]
    [InlineData(@"C:\x\foto (1).jpg", "foto.jpg")]
    [InlineData(@"C:\x\Foto - Kopie.JPG", "foto.jpg")]
    [InlineData(@"C:\x\Foto - Kopie (2).jpg", "foto.jpg")]
    [InlineData(@"C:\x\Copy of report.pdf", "report.pdf")]
    [InlineData(@"C:\x\report_copy.pdf", "report.pdf")]
    [InlineData(@"C:\x\setup.exe", "setup.exe")]
    [InlineData(@"C:\x\(1).txt", "(1).txt")]
    public void Copy_suffixes_are_ignored_for_names(string path, string expected) => Assert.Equal(expected, DuplicateFinder.NormalizedName(path));

    [Fact]
    public async Task Small_files_and_unreadable_files_are_handled()
    {
        using var fake = new FakeSystem();
        fake.File(@"a\x.txt", 5000, fill: 1);
        fake.File(@"b\y.txt", 5000, fill: 1);
        var locked = fake.File(@"c\z.txt", 5000, fill: 1);
        var scan = new SpaceScanner(new SpaceSettings { DuplicateMinKb = 1 }.Normalize(), fake.Paths).Scan(fake.Root, 0, null, CancellationToken.None);
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var groups = await new DuplicateFinder(fake.Paths, byName: false).FindAsync(scan.SameSize, null, CancellationToken.None);
            Assert.Equal(2, Assert.Single(groups).Files.Count); // the locked one is simply left out
        }
        Assert.Equal(DuplicateFinder.QuickHash(locked, 5000), DuplicateFinder.FullHash(locked, CancellationToken.None) is null ? null : DuplicateFinder.QuickHash(locked, 5000));
        Assert.Null(DuplicateFinder.QuickHash(locked, 4999)); // size changed since the scan
    }
}

public sealed class CleanupAreaTests
{
    [Fact]
    public void Temp_caches_logs_installers_and_old_downloads_are_found()
    {
        using var fake = new FakeSystem();
        var old = DateTime.UtcNow.AddDays(-200);
        fake.File(@"Users\Anna\AppData\Local\Temp\alt.tmp", 100, old);
        fake.File(@"Users\Anna\AppData\Local\Temp\neu.tmp", 100); // still in use maybe: left out
        fake.File(@"Windows\Temp\setup.log", 200, old);
        fake.File(@"Users\Anna\AppData\Local\Microsoft\Windows\Explorer\thumbcache_256.db", 300);
        fake.File(@"Users\Anna\AppData\Local\Microsoft\Windows\WER\ReportArchive\x\Report.wer", 50, old);
        fake.File(@"Users\Anna\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_1", 400);
        fake.File(@"Users\Anna\AppData\Local\Google\Chrome\User Data\Default\Cookies", 999);
        fake.File(@"Users\Anna\Downloads\setup.exe", 500, old);
        fake.File(@"Users\Anna\Downloads\rechnung.pdf", 600, old);
        fake.File(@"Users\Anna\Downloads\heute.pdf", 600);
        var areas = new CleanupAreas(new SpaceSettings().Normalize(), fake.Paths).Scan(CancellationToken.None).ToDictionary(a => a.Finding);

        Assert.Equal("alt.tmp", Assert.Single(areas[Finding.AppTemp].Files).Name);
        Assert.Equal("setup.log", Assert.Single(areas[Finding.WindowsTemp].Files).Name);
        Assert.Equal(Safety.Safe, areas[Finding.WindowsTemp].Files[0].Safety);
        Assert.Equal("thumbcache_256.db", Assert.Single(areas[Finding.Thumbnails].Files).Name);
        Assert.Equal("Report.wer", Assert.Single(areas[Finding.Logs].Files).Name);
        Assert.Equal("f_1", Assert.Single(areas[Finding.BrowserCache].Files).Name); // never the cookies
        Assert.Equal("setup.exe", Assert.Single(areas[Finding.Installers].Files).Name);
        Assert.Equal(Safety.Review, areas[Finding.Installers].Files[0].Safety);
        Assert.Equal("rechnung.pdf", Assert.Single(areas[Finding.OldDownload].Files).Name);
        Assert.Equal(400, areas[Finding.BrowserCache].Size);

        var withoutBrowser = new CleanupAreas(new SpaceSettings { IncludeBrowserCache = false }.Normalize(), fake.Paths).Scan(CancellationToken.None);
        Assert.Empty(withoutBrowser.Single(a => a.Finding == Finding.BrowserCache).Files);
    }

    [Fact]
    public void The_recycle_bin_can_be_queried()
    {
        var info = RecycleBin.Query();
        Assert.True(info.Size >= 0 && info.Items >= 0);
    }
}

public sealed class SpaceCleanerTests
{
    private static FoundFile Found(string path, Finding finding, SystemPaths paths)
    {
        var info = new FileInfo(path);
        var file = new FoundFile(path, info.Length, info.LastWriteTimeUtc, info.LastAccessTimeUtc, finding);
        FileRules.Assess(file, paths, info.Attributes);
        return file;
    }

    [Fact]
    public void Simulation_changes_nothing_but_reports_the_space()
    {
        using var fake = new FakeSystem();
        var a = fake.File(@"Users\Anna\AppData\Local\Temp\a.tmp", 1000);
        var b = fake.File(@"Users\Anna\Downloads\b.zip", 2000);
        var log = Path.Combine(fake.Root, "log.jsonl");
        var report = new SpaceCleaner(fake.Paths, log).Run(new[] { Found(a, Finding.AppTemp, fake.Paths), Found(b, Finding.OldDownload, fake.Paths) }, CleanAction.Delete, null, simulate: true, null, CancellationToken.None);
        Assert.True(report.Simulated);
        Assert.Equal(2, report.Done);
        Assert.Equal(3000, report.FreedBytes);
        Assert.True(File.Exists(a) && File.Exists(b));
        var line = File.ReadAllLines(log).Single();
        Assert.Contains("\"simulated\":true", line);
    }

    [Fact]
    public void Protected_changed_missing_and_linked_files_are_refused()
    {
        using var fake = new FakeSystem();
        var system = Found(fake.File(@"Windows\System32\x.dll", 10), Finding.Large, fake.Paths);
        var changed = Found(fake.File(@"Users\Anna\Downloads\c.txt", 10), Finding.OldDownload, fake.Paths);
        File.WriteAllText(changed.Path, "now different");
        var missing = Found(fake.File(@"Users\Anna\Downloads\m.txt", 10), Finding.OldDownload, fake.Paths);
        File.Delete(missing.Path);
        var cleaner = new SpaceCleaner(fake.Paths, Path.Combine(fake.Root, "log.jsonl"));
        Assert.Equal(SkipReason.Protected, cleaner.Check(system));
        Assert.Equal(SkipReason.Changed, cleaner.Check(changed));
        Assert.Equal(SkipReason.Missing, cleaner.Check(missing));

        // A file whose rating was forced to "safe" is still refused inside Windows.
        system.Safety = Safety.Safe;
        Assert.Equal(SkipReason.Protected, cleaner.Check(system));
        var report = cleaner.Run(new[] { system, changed, missing }, CleanAction.Delete, null, false, null, CancellationToken.None);
        Assert.Equal(0, report.Done);
        Assert.True(File.Exists(system.Path));
        Assert.Equal(3, report.Failed.Count);
    }

    [Fact]
    public void Files_go_to_the_recycle_bin_get_moved_archived_or_deleted()
    {
        using var fake = new FakeSystem();
        var cleaner = new SpaceCleaner(fake.Paths, Path.Combine(fake.Root, "log.jsonl"));
        var recycle = Found(fake.File(@"Users\Anna\Downloads\spacehelper-test-recycle.bin", 1234), Finding.OldDownload, fake.Paths);
        var report = cleaner.Run(new[] { recycle }, CleanAction.Recycle, null, false, null, CancellationToken.None);
        Assert.Equal(1, report.Done);
        Assert.Equal(1234, report.FreedBytes);
        Assert.False(File.Exists(recycle.Path));

        var move = Found(fake.File(@"Users\Anna\Downloads\umzug.txt", 50), Finding.OldDownload, fake.Paths);
        var target = Path.Combine(fake.Root, "Archiv");
        Assert.Equal(1, cleaner.Run(new[] { move }, CleanAction.Move, target, false, null, CancellationToken.None).Done);
        Assert.True(File.Exists(Path.Combine(target, "umzug.txt")));

        var a = Found(fake.File(@"Users\Anna\Downloads\x\doppelt.txt", 40, fill: 2), Finding.OldDownload, fake.Paths);
        var b = Found(fake.File(@"Users\Anna\Downloads\y\doppelt.txt", 40, fill: 3), Finding.OldDownload, fake.Paths);
        var zip = Path.Combine(fake.Root, "alt.zip");
        var archived = cleaner.Run(new[] { a, b }, CleanAction.Archive, zip, false, null, CancellationToken.None);
        Assert.Equal(2, archived.Done);
        using (var archive = ZipFile.OpenRead(zip))
            Assert.Equal(new[] { "doppelt.txt", "doppelt (2).txt" }, archive.Entries.Select(e => e.Name));
        Assert.False(File.Exists(a.Path));

        var delete = Found(fake.File(@"Users\Anna\AppData\Local\Temp\weg.tmp", 10), Finding.AppTemp, fake.Paths);
        Assert.Equal(1, cleaner.Run(new[] { delete }, CleanAction.Delete, null, false, null, CancellationToken.None).Done);
        Assert.False(File.Exists(delete.Path));
    }

    [Fact]
    public void A_locked_file_is_reported_and_cancel_stops_the_rest()
    {
        using var fake = new FakeSystem();
        var cleaner = new SpaceCleaner(fake.Paths, Path.Combine(fake.Root, "log.jsonl"));
        var locked = Found(fake.File(@"Users\Anna\AppData\Local\Temp\busy.tmp", 10), Finding.AppTemp, fake.Paths);
        using (new FileStream(locked.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = cleaner.Run(new[] { locked }, CleanAction.Delete, null, false, null, CancellationToken.None);
            Assert.Equal(SkipReason.InUse, Assert.Single(report.Failed).Reason);
        }
        var files = Enumerable.Range(0, 5).Select(i => Found(fake.File($@"Users\Anna\AppData\Local\Temp\f{i}.tmp", 10), Finding.AppTemp, fake.Paths)).ToList();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var canceled = cleaner.Run(files, CleanAction.Delete, null, false, null, cancel.Token);
        Assert.True(canceled.Canceled);
        Assert.Equal(0, canceled.Done);
        Assert.All(files, f => Assert.True(File.Exists(f.Path)));
    }

    [Fact]
    public void Uninstall_commands_are_split_correctly()
    {
        Assert.Equal(("C:\\Program Files\\App\\uninst.exe", "/S"), ProgramInventory.SplitCommand("\"C:\\Program Files\\App\\uninst.exe\" /S"));
        Assert.Equal(("MsiExec.exe", "/X{1234}"), ProgramInventory.SplitCommand("MsiExec.exe /X{1234}"));
        Assert.Equal(("C:\\Tools\\Old App\\remove.exe", "--uninstall"), ProgramInventory.SplitCommand("C:\\Tools\\Old App\\remove.exe --uninstall"));
        Assert.NotNull(ProgramInventory.Read());
    }
}
