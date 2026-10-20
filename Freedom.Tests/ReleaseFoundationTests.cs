using System.Text.RegularExpressions;
using System.Windows;
using ViennaAtomicClock;
using ViennaAtomicClock.GlbViewer;
using Xunit;

namespace Freedom.Tests;

public sealed class ReleaseFoundationTests
{
    [Fact]
    public void Build_metadata_contains_semantic_version_commit_and_date()
    {
        Assert.Equal("1.1.0", BuildInfo.SemanticVersion);
        Assert.True(BuildInfo.GitCommit == "unknown" || Regex.IsMatch(BuildInfo.GitCommit, "^[0-9a-f]{7,40}(-dirty)?$", RegexOptions.IgnoreCase));
        Assert.NotNull(BuildInfo.BuildDateUtc);
    }

    [Fact]
    public void WebView2_download_uses_the_official_https_site()
    {
        var uri = new Uri(WebView2Runtime.DownloadUrl);
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.Equal("developer.microsoft.com", uri.Host);
    }

    [Fact]
    public void About_texts_exist_in_every_language()
    {
        var keys = new[] { "about.open", "about.title", "about.subtitle", "about.version", "about.commit", "about.built", "about.runtime", "about.local", "about.data", "about.unknown" };
        foreach (var key in keys)
        {
            Assert.True(Strings.Table.TryGetValue(key, out var texts), key);
            Assert.Equal(Loc.Languages.Length, texts!.Length);
            Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        }
    }

    [Fact]
    public void About_window_constructs_and_renders()
    {
        UiTestHost.Run(() =>
        {
            var window = new AboutWindow();
            Assert.Equal(Loc.T("about.open"), window.Title);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var bitmap = UiTestHost.Render(content, 540, 520);
            Assert.Equal(540, bitmap.PixelWidth);
            Assert.Equal(520, bitmap.PixelHeight);
        });
    }
}
