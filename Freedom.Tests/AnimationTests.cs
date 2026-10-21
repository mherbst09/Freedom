using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ViennaAtomicClock;
using Xunit;
using Xunit.Abstractions;

namespace Freedom.Tests;

public sealed class AnimationTests
{
    private readonly ITestOutputHelper _output;

    public AnimationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Mystic_layers_are_synchronised_with_a_60_fps_clock()
    {
        UiTestHost.Run(() =>
        {
            Assert.Equal(0, new MysticBackdrop().TargetFramesPerSecond);
            Assert.Equal(60, new MysticOverlay().TargetFramesPerSecond);
        });
    }

    [Fact]
    public void Mystic_moving_full_hd_layer_fits_the_60_fps_frame_budget()
    {
        Exception? failure = null;
        double overlayMs = 0;
        var thread = new Thread(() =>
        {
            try
            {
                const int width = 1920;
                const int height = 1080;
                var overlay = new MysticOverlay();
                Prepare(overlay, width, height);
                Render(overlay, width, height);

                const int frames = 30;
                var stopwatch = Stopwatch.StartNew();
                for (var frame = 0; frame < frames; frame++) Render(overlay, width, height);
                stopwatch.Stop();
                overlayMs = stopwatch.Elapsed.TotalMilliseconds / frames;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Mystic performance test timed out.");
        Assert.Null(failure);
        _output.WriteLine($"Mystic moving layer: {overlayMs:F2} ms/frame");
        Assert.True(overlayMs <= 16.67, $"Mystic moving layer took {overlayMs:F2} ms/frame.");
    }

    [Fact]
    public void Christmas_halloween_and_jungle_have_complete_palettes_texts_and_layers()
    {
        UiTestHost.Run(() =>
        {
            var themes = new (string Id, Func<AnimatedLayer> Layer)[]
            {
                (Themes.Christmas, () => new ChristmasBackdrop()),
                (Themes.Halloween, () => new HalloweenBackdrop()),
                (Themes.Jungle, () => new JungleBackdrop())
            };

            try
            {
                foreach (var (id, createLayer) in themes)
                {
                    Assert.Equal(id, Themes.Normalize(id));
                    Assert.NotEqual(Themes.PreviewColors(id, false), Themes.PreviewColors(id, true));
                    Assert.True(Strings.Table.TryGetValue("theme." + id, out var names));
                    Assert.True(Strings.Table.TryGetValue("theme." + id + ".hint", out var hints));
                    Assert.Equal(Loc.Languages.Length, names!.Length);
                    Assert.Equal(Loc.Languages.Length, hints!.Length);

                    foreach (var dark in new[] { false, true })
                    {
                        App.ApplyTheme(id, dark);
                        Assert.NotNull(ThemePreview.Create(id, dark));
                        var layer = createLayer();
                        Assert.Equal(24, layer.TargetFramesPerSecond);
                        var bitmap = UiTestHost.Render(layer, 960, 540);
                        Assert.True(HasVisiblePixels(bitmap), $"{id} {(dark ? "dark" : "light")} rendered empty.");
                        UiTestHost.SaveIfRequested(bitmap, $"theme-{id}-{(dark ? "dark" : "light")}");
                    }
                }
            }
            finally
            {
                App.ApplyTheme(Themes.Standard, true);
            }
        });
    }

    [Fact]
    public void Both_overlays_advance_and_render_a_complete_show_without_errors()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                const int width = 640;
                const int height = 360;
                var focus = new Rect(0, 0, width, height);
                var fireworks = new FireworksCanvas(FireworkShow.Create(width, height, focus, new Random(42)), width, height);
                var gloom = new GloomCanvas(GloomPlan.Create(focus, new Random(42)), width, height);

                Prepare(fireworks, width, height);
                Prepare(gloom, width, height);

                const double dt = 1.0 / 30;
                for (var frame = 0; frame <= 15 * 30; frame++)
                {
                    var time = frame * dt;
                    fireworks.Advance(time, dt);
                    gloom.Advance(time, dt);

                    if (frame % 15 != 0) continue;
                    Render(fireworks, width, height);
                    Render(gloom, width, height);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Animation render test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void Enhanced_depth_scenes_render_key_frames()
    {
        UiTestHost.Run(() =>
        {
            const int width = 1280;
            const int height = 720;
            const double dt = 1.0 / 60;
            var focus = new Rect(0, 0, width, height);
            var fireworks = new FireworksCanvas(FireworkShow.Create(width, height, focus, new Random(42)), width, height);
            var gloomPlan = GloomPlan.Create(focus, new Random(42));
            var gloom = new GloomCanvas(gloomPlan, width, height);
            Prepare(fireworks, width, height);
            Prepare(gloom, width, height);

            var gloomFrame = (int)Math.Round((gloomPlan.Lightning[1] + 0.12) / dt);
            var fireworksFrame = (int)Math.Round((FireworkShow.FinaleJackpotAt + 0.34) / dt);
            for (var frame = 0; frame <= fireworksFrame; frame++)
            {
                var time = frame * dt;
                fireworks.Advance(time, dt);
                if (frame <= gloomFrame) gloom.Advance(time, dt);
            }

            var fireworksBitmap = UiTestHost.Render(fireworks, width, height);
            var gloomBitmap = UiTestHost.Render(gloom, width, height);
            Assert.True(HasVisiblePixels(fireworksBitmap));
            Assert.True(HasVisiblePixels(gloomBitmap));
            UiTestHost.SaveIfRequested(fireworksBitmap, "fireworks-depth-finale");
            UiTestHost.SaveIfRequested(gloomBitmap, "gloom-depth-lightning");
        });
    }

    [Fact]
    public void Expensive_full_hd_frames_fit_the_60_fps_budget()
    {
        Exception? failure = null;
        double fireworksMs = 0, gloomMs = 0;
        var thread = new Thread(() =>
        {
            try
            {
                const int width = 1920;
                const int height = 1080;
                const double dt = 1.0 / 60;
                var focus = new Rect(0, 0, width, height);
                var fireworks = new FireworksCanvas(FireworkShow.Create(width, height, focus, new Random(42)), width, height);
                var gloom = new GloomCanvas(GloomPlan.Create(focus, new Random(42)), width, height);
                Prepare(fireworks, width, height);
                Prepare(gloom, width, height);

                for (var frame = 0; frame <= 12 * 60; frame++)
                {
                    var time = frame * dt;
                    fireworks.Advance(time, dt);
                    gloom.Advance(time, dt);
                }

                Render(fireworks, width, height);
                Render(gloom, width, height);

                fireworksMs = MeasureFrames(fireworks, width, height, 12, dt);
                gloomMs = MeasureFrames(gloom, width, height, 12, dt);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Full-HD performance test timed out.");
        Assert.Null(failure);
        _output.WriteLine($"Fireworks: {fireworksMs:F2} ms/frame; Gloom: {gloomMs:F2} ms/frame");
        Assert.True(fireworksMs <= 16.67, $"Fireworks took {fireworksMs:F2} ms/frame.");
        Assert.True(gloomMs <= 16.67, $"Gloom took {gloomMs:F2} ms/frame.");
    }

    private static void Prepare(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void Render(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
    }

    private static double MeasureFrames(FrameworkElement element, int width, int height, double startTime, double dt)
    {
        const int frames = 30;
        var stopwatch = Stopwatch.StartNew();
        for (var frame = 1; frame <= frames; frame++)
        {
            if (element is FireworksCanvas fireworks) fireworks.Advance(startTime + frame * dt, dt);
            else if (element is GloomCanvas gloom) gloom.Advance(startTime + frame * dt, dt);
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(element);
        }
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds / frames;
    }

    private static bool HasVisiblePixels(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) return true;
        return false;
    }

}
