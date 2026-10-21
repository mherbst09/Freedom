using System.IO;
using System.Text.RegularExpressions;
using ViennaAtomicClock;
using ViennaAtomicClock.SpaceHelper;
using Xunit;

namespace Freedom.Tests;

public sealed class SpaceHelperPageTests
{
    private static (FakeSystem Fake, SpaceHelperPage Page) Create(bool simulation = false)
    {
        var fake = new FakeSystem();
        var old = DateTime.UtcNow.AddDays(-400);
        fake.File(@"Users\Anna\AppData\Local\Temp\alt.tmp", 40_000, old);
        fake.File(@"Users\Anna\Downloads\setup-alt.exe", 300_000, old);
        fake.File(@"Users\Anna\Downloads\rechnung-2021.pdf", 120_000, old);
        fake.File(@"Users\Anna\Videos\urlaub.mp4", 12 * 1024 * 1024);
        fake.File(@"Users\Anna\Pictures\foto.jpg", 2 * 1024 * 1024, old, fill: 4);
        fake.File(@"Users\Anna\Desktop\foto kopie.jpg", 2 * 1024 * 1024, fill: 4);
        fake.File(@"Windows\System32\gross.dll", 11 * 1024 * 1024);
        var settings = new SpaceSettings { LargeFileMb = 10, Simulation = simulation }.Normalize();
        var page = new SpaceHelperPage(settings, fake.Paths, new SpaceCleaner(fake.Paths, Path.Combine(fake.Root, "log.jsonl")));
        return (fake, page);
    }

    [Fact]
    public void Analysis_fills_the_overview_and_the_lists()
    {
        UiTestHost.RunAsync(async () =>
        {
            var (fake, page) = Create();
            using (fake)
            {
                UiTestHost.Render(page, 1280, 900);
                await Task.Delay(300);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 1100), "spacehelper-start");
                await page.AnalyzeAsync(fake.Root);
                Assert.NotNull(page.Scan);
                Assert.False(page.Busy);
                Assert.Contains(page.Areas, a => a.Finding == Finding.AppTemp && a.Files.Count == 1);
                UiTestHost.Render(page, 1280, 900);
                await Task.Delay(300);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 1700), "spacehelper-overview");

                page.ShowView("files");
                UiTestHost.Render(page, 1280, 900);
                await Task.Delay(200);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 1500), "spacehelper-files");
                page.OpenFindings(Finding.Duplicate);
                UiTestHost.Render(page, 1280, 900);
                await Task.Delay(200);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 1000), "spacehelper-duplicates");
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 760, 1200), "spacehelper-narrow");
                page.Shutdown();
            }
        }, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Protected_files_cannot_be_selected_and_cleanup_needs_the_dialog()
    {
        UiTestHost.RunAsync(async () =>
        {
            var (fake, page) = Create();
            using (fake)
            {
                await page.AnalyzeAsync(fake.Root);
                var system = Path.Combine(fake.Paths.Windows, "System32", "gross.dll");
                var temp = Path.Combine(fake.Paths.Temp, "alt.tmp");
                var original = Path.Combine(fake.Paths.UserProfile, "Pictures", "foto.jpg");
                var copy = Path.Combine(fake.Paths.UserProfile, "Desktop", "foto kopie.jpg");
                page.SelectPaths(new[] { system, temp, original, copy });
                Assert.Equal(new[] { copy, temp }.OrderBy(p => p), page.SelectedPaths.OrderBy(p => p));
                Assert.True(File.Exists(temp)); // selecting changes nothing

                page.ShowView("files");
                page.Confirm(CleanAction.Delete);
                Assert.True(page.OverlayOpen);
                UiTestHost.Render(page, 1280, 900);
                await Task.Delay(300);
                UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 900), "spacehelper-confirm-delete");
                Assert.True(File.Exists(temp) && File.Exists(copy)); // the dialog alone deletes nothing
                page.Shutdown();
            }
        }, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Every_theme_language_and_view_renders()
    {
        UiTestHost.RunAsync(async () =>
        {
            var (fake, page) = Create(simulation: true);
            using (fake)
            {
                await page.AnalyzeAsync(fake.Root);
                try
                {
                    // One language switch per language (every switch rebuilds all pages), all views each time.
                    foreach (var (code, _, _) in Loc.Languages)
                    {
                        Loc.Apply(code);
                        foreach (var view in new[] { "overview", "files", "programs", "settings" })
                        {
                            page.ShowView(view);
                            UiTestHost.Render(page, 1100, 900);
                            Assert.False(string.IsNullOrWhiteSpace(page.StatusLine));
                        }
                    }
                    page.ShowView("settings");
                    UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1100, 1300), "spacehelper-settings");
                    foreach (var (theme, _, _, _) in Themes.All)
                        foreach (var dark in new[] { false, true })
                        {
                            App.ApplyTheme(theme, dark);
                            UiTestHost.Render(page, 1100, 900);
                        }
                }
                finally
                {
                    Loc.Apply("de");
                    App.ApplyTheme(Themes.Standard, true);
                    page.Shutdown();
                }
            }
        }, TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void Every_spacehelper_text_exists_in_all_languages()
    {
        var placeholder = new Regex(@"\{\d\}");
        foreach (var (key, texts) in SpaceHelperStrings.Table)
        {
            Assert.Equal(Loc.Languages.Length, texts.Length);
            var expected = placeholder.Matches(texts[0]).Select(m => m.Value).Distinct().OrderBy(v => v).ToList();
            foreach (var text in texts)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), key);
                Assert.True(expected.SequenceEqual(placeholder.Matches(text).Select(m => m.Value).Distinct().OrderBy(v => v)), $"{key}: {text}");
            }
        }
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ViennaAtomicClock", "SpaceHelper"));
        Assert.True(Directory.Exists(source), source);
        var keys = Directory.GetFiles(source, "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"(sh\\.[A-Za-z0-9_.]+[A-Za-z0-9])\"").Select(m => m.Groups[1].Value)).Distinct().ToList();
        var dynamic = Enum.GetValues<Finding>().SelectMany(f => new[] { "sh.area." + f.ToString().ToLowerInvariant(), "sh.areaHelp." + f.ToString().ToLowerInvariant() })
            .Concat(Enum.GetValues<FileKind>().Select(k => "sh.kind." + k.ToString().ToLowerInvariant()))
            .Concat(Enum.GetValues<Safety>().SelectMany(s => new[] { "sh.safety." + s.ToString().ToLowerInvariant(), "sh.explain." + s.ToString().ToLowerInvariant() }))
            .Concat(Enum.GetValues<SkipReason>().Select(r => "sh.skip." + r.ToString().ToLowerInvariant()))
            .Concat(Enum.GetValues<CleanAction>().SelectMany(a => new[] { "sh.confirm." + a.ToString().ToLowerInvariant(), "sh.consequence." + a.ToString().ToLowerInvariant(), "sh.confirm.go." + a.ToString().ToLowerInvariant() }))
            .Concat(new[] { "sh.phase.drive", "sh.phase.areas", "sh.phase.duplicates" });
        var missing = keys.Concat(dynamic).Where(k => !SpaceHelperStrings.Table.ContainsKey(k)).Distinct().ToList();
        Assert.True(missing.Count == 0, "Missing: " + string.Join(", ", missing));
    }
}
