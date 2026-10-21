using System.IO;
using System.Text.RegularExpressions;
using ViennaAtomicClock;
using ViennaAtomicClock.FileFlow;
using Xunit;

namespace Freedom.Tests;

public sealed class FileFlowPageTests
{
    [Fact]
    public void The_page_shows_files_in_every_state_wide_and_narrow()
    {
        using var folder = new TempFolder();
        UiTestHost.RunAsync(async () =>
        {
            var service = new FileFlowService(Path.Combine(folder.Path, "work"));
            var page = new FileFlowPage(service, new FileFlowSettings());
            UiTestHost.Render(page, 1280, 900);
            await Task.Delay(300);
            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 1500), "fileflow-empty");
            Assert.Equal("wide", page.LayoutMode);

            var png = Samples.Image(folder.Path, "Urlaubsfoto.png", 320, 200);
            var jpg = Samples.Image(folder.Path, "Scan Seite 2.jpg", 200, 280);
            var exe = Path.Combine(folder.Path, "setup.exe");
            File.WriteAllBytes(exe, Convert.FromHexString("4D5A90000300000004000000"));
            var docx = Path.Combine(folder.Path, "Bewerbung.docx");
            DocxDocument.Write(docx, new[] { new[] { "Hallo" } });
            page.AddFiles(new[] { png, jpg, exe, docx });
            var done = service.AddFile(Samples.Image(folder.Path, "Logo.bmp"), FileFormat.Png);
            await service.RunNowAsync(done, new ConversionOptions());
            service.AddLink("https://youtu.be/abc", rightsConfirmed: false);
            await Task.Delay(400); // previews and fade-in
            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 1280, 1700), "fileflow-list");
            Assert.Equal(6, service.Jobs.Count);
            Assert.Equal(2, service.Jobs.Count(j => j.State == JobState.Failed));
            Assert.Contains("Bewerbung.docx", page.StatusLine.Length > 0 ? service.Jobs.Select(j => j.DisplayName) : Array.Empty<string>());

            UiTestHost.SaveIfRequested(UiTestHost.Render(page, 760, 1900), "fileflow-narrow");
            Assert.Equal("narrow", page.LayoutMode);
            page.Shutdown();
        }, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Every_theme_and_language_renders()
    {
        using var folder = new TempFolder();
        UiTestHost.Run(() =>
        {
            var service = new FileFlowService(Path.Combine(folder.Path, "work"));
            var page = new FileFlowPage(service, new FileFlowSettings());
            page.AddFiles(new[] { Samples.Image(folder.Path, "a.png") });
            try
            {
                foreach (var (code, _, _) in Loc.Languages)
                {
                    Loc.Apply(code);
                    UiTestHost.Render(page, 1100, 900);
                    Assert.False(string.IsNullOrWhiteSpace(page.StatusLine));
                }
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
        });
    }

    [Fact]
    public void Descriptions_exist_for_every_card()
    {
        foreach (var conversion in FileFlowCatalog.Conversions)
        {
            var text = FileFlowPage.Description(conversion);
            Assert.False(string.IsNullOrWhiteSpace(text), conversion.Id);
            Assert.DoesNotContain("ff.desc", text);
        }
    }

    [Fact]
    public void Every_fileflow_text_exists_in_all_languages()
    {
        var placeholder = new Regex(@"\{\d\}");
        foreach (var (key, texts) in FileFlowStrings.Table)
        {
            Assert.Equal(Loc.Languages.Length, texts.Length);
            var expected = placeholder.Matches(texts[0]).Select(m => m.Value).Distinct().OrderBy(v => v).ToList();
            foreach (var text in texts)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), key);
                Assert.True(expected.SequenceEqual(placeholder.Matches(text).Select(m => m.Value).Distinct().OrderBy(v => v)), $"{key}: {text}");
            }
        }
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ViennaAtomicClock", "FileFlow"));
        Assert.True(Directory.Exists(source), source);
        var keys = Directory.GetFiles(source, "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"(ff\\.[A-Za-z0-9_.]+[A-Za-z0-9])\"").Select(m => m.Groups[1].Value)).Distinct().ToList();
        var missing = keys.Where(k => !FileFlowStrings.Table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing: " + string.Join(", ", missing));
        // Keys built from parts (categories, quality levels) must exist too.
        foreach (var category in Enum.GetValues<FormatCategory>()) Assert.True(FileFlowStrings.Table.ContainsKey("ff.cat." + category.ToString().ToLowerInvariant()));
        foreach (var quality in Enum.GetValues<VideoQuality>()) Assert.True(FileFlowStrings.Table.ContainsKey("ff.opt.q." + quality.ToString().ToLowerInvariant()));
    }
}
