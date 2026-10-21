using System.IO;
using System.Windows.Media.Imaging;
using ViennaAtomicClock.PunchFactory;
using Xunit;

namespace Freedom.Tests;

public sealed class PunchSceneTests
{
    [Fact]
    public void Every_studio_renders_fast_enough_with_hits_particles_and_sprites()
    {
        UiTestHost.Run(() =>
        {
            var scene = new PunchScene();
            var counts = new[] { 12, 5, 3, 2, 1, 1, 1, 1, 1, 1, 1 };
            var times = new List<double>();
            for (var env = 0; env < 8; env++)
            {
                scene.SetEnvironment(env);
                var active = new ActiveEvent { Def = new EventDef { Id = "glove", Lifetime = 8 }, X = 0.75, Y = 0.3, Remaining = 6, HitsLeft = 1 };
                for (var frame = 0; frame < 30; frame++)
                {
                    if (frame % 6 == 0) scene.Hit(new PunchResult(10, frame % 12 == 0, frame, env >= 6, PunchSource.Click, frame % 12 == 0 ? 2 : 1));
                    scene.Advance(1 / 60.0, env >= 6);
                    scene.Render(new SceneState(counts, env >= 6, active));
                    if (frame > 2) times.Add(scene.LastRenderMs);
                }
                var image = scene.Snapshot();
                var big = new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(3, 3));
                UiTestHost.SaveIfRequested(big, $"punch-scene-{env}");
            }
            times.Sort();
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "punch-scene-times.txt"), $"median {times[times.Count / 2]:0.00} ms, p95 {times[(int)(times.Count * 0.95)]:0.00} ms, max {times[^1]:0.00} ms");
            Assert.True(times[times.Count / 2] < 25, $"median render {times[times.Count / 2]:0.0} ms");
            Assert.True(scene.HitsBag(scene.Project(new System.Windows.Media.Media3D.Point3D(0, 2.2, 0.45))));
            Assert.False(scene.HitsBag(new System.Windows.Point(5, 5)));
        });
    }
}
