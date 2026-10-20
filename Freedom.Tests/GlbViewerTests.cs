using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using ViennaAtomicClock;
using ViennaAtomicClock.GlbViewer;
using Xunit;
using Xunit.Abstractions;

namespace Freedom.Tests;

/// <summary>Builds small GLB files (header, JSON chunk, optional BIN chunk) for container tests.</summary>
internal static class GlbBuilder
{
    public static byte[] Build(string json, byte[]? bin = null, uint? version = null, uint? declaredLength = null, uint magic = GlbInspector.Magic)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var jsonPadded = Pad(jsonBytes, (byte)' ');
        var binPadded = bin is null ? null : Pad(bin, 0);
        var length = 12 + 8 + jsonPadded.Length + (binPadded is null ? 0 : 8 + binPadded.Length);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(magic);
        writer.Write(version ?? 2u);
        writer.Write(declaredLength ?? (uint)length);
        writer.Write((uint)jsonPadded.Length);
        writer.Write(0x4E4F534Au);
        writer.Write(jsonPadded);
        if (binPadded is not null)
        {
            writer.Write((uint)binPadded.Length);
            writer.Write(0x004E4942u);
            writer.Write(binPadded);
        }
        return stream.ToArray();
    }

    private static byte[] Pad(byte[] data, byte fill)
    {
        var padded = new byte[(data.Length + 3) / 4 * 4];
        Array.Fill(padded, fill);
        data.CopyTo(padded, 0);
        return padded;
    }

    /// <summary>One triangle with a material, ready to render.</summary>
    public static string Triangle(string extra = "") => """
        {"asset":{"version":"2.0","generator":"Freedom tests"},"scene":0,"scenes":[{"nodes":[0]}],
         "nodes":[{"mesh":0,"name":"Dreieck"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"material":0}]}],
         "materials":[{"name":"Rot","pbrMetallicRoughness":{"baseColorFactor":[1,0,0,1]}}],
         "buffers":[{"byteLength":36}],"bufferViews":[{"buffer":0,"byteLength":36}],
         "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]}]
         EXTRA}
        """.Replace("EXTRA", extra);

    public static byte[] TriangleBin()
    {
        var floats = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        var bytes = new byte[36];
        Buffer.BlockCopy(floats, 0, bytes, 0, 36);
        return bytes;
    }

    public static string Write(string folder, string name, byte[] data)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }
}

public sealed class GlbInspectorTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "glb", name);

    [Theory]
    [InlineData("DamagedHelmet.glb", 1, 1, 1, 1, 5, 0, 0)]
    [InlineData("Fox.glb", 1, 26, 1, 1, 1, 3, 1)]
    [InlineData("BoxAnimated.glb", 1, 4, 2, 2, 0, 1, 0)]
    [InlineData("AnimatedMorphCube.glb", 1, 1, 1, 1, 0, 1, 0)]
    [InlineData("AlphaBlendModeTest.glb", 1, 9, 9, 6, 4, 0, 0)]
    public void Real_models_are_counted_correctly(string file, int scenes, int nodes, int meshes, int materials, int textures, int animations, int skins)
    {
        var info = GlbInspector.Inspect(Fixture(file));
        Assert.Equal(2, info.Version);
        Assert.Equal((scenes, nodes, meshes, materials, textures, animations, skins), (info.Scenes, info.Nodes, info.Meshes, info.Materials, info.Textures, info.Animations, info.Skins));
        Assert.True(info.Vertices > 0);
        Assert.True(info.Triangles > 0);
        Assert.Empty(info.Warnings);
        Assert.Equal(new FileInfo(Fixture(file)).Length, info.FileSize);
    }

    [Fact]
    public void A_built_triangle_is_valid_and_counted()
    {
        using var folder = new TempFolder();
        var path = GlbBuilder.Write(folder.Path, "dreieck.glb", GlbBuilder.Build(GlbBuilder.Triangle(), GlbBuilder.TriangleBin()));
        var info = GlbInspector.Inspect(path);
        Assert.Equal(3, info.Vertices);
        Assert.Equal(1, info.Triangles);
        Assert.Equal("Freedom tests", info.Generator);
    }

    [Theory]
    [InlineData("model.gltf", "glb.err.extension")]
    [InlineData("model.obj", "glb.err.extension")]
    public void Wrong_extensions_are_rejected(string name, string key)
    {
        using var folder = new TempFolder();
        var path = GlbBuilder.Write(folder.Path, name, GlbBuilder.Build(GlbBuilder.Triangle(), GlbBuilder.TriangleBin()));
        Assert.Equal(key, Assert.Throws<GlbValidationException>(() => GlbInspector.Inspect(path)).Key);
    }

    [Fact]
    public void Broken_files_give_clear_errors()
    {
        using var folder = new TempFolder();
        string Key(byte[] data) => Assert.Throws<GlbValidationException>(() => GlbInspector.Inspect(GlbBuilder.Write(folder.Path, "x.glb", data))).Key;
        var good = GlbBuilder.Build(GlbBuilder.Triangle(), GlbBuilder.TriangleBin());

        Assert.Equal("glb.err.header", Key(Array.Empty<byte>()));
        Assert.Equal("glb.err.header", Key(Encoding.ASCII.GetBytes("this is not a model at all")));
        Assert.Equal("glb.err.header", Key(GlbBuilder.Build(GlbBuilder.Triangle(), magic: 0x12345678)));
        Assert.Equal("glb.err.version", Key(GlbBuilder.Build(GlbBuilder.Triangle(), version: 1)));
        Assert.Equal("glb.err.length", Key(good[..(good.Length - 20)])); // truncated download
        Assert.Equal("glb.err.length", Key(GlbBuilder.Build(GlbBuilder.Triangle(), declaredLength: 10)));
        Assert.Equal("glb.err.json", Key(GlbBuilder.Build("{ \"asset\": ")));
        Assert.Equal("glb.err.asset", Key(GlbBuilder.Build("{\"scenes\":[]}")));
        Assert.Equal("glb.err.version", Key(GlbBuilder.Build("{\"asset\":{\"version\":\"1.0\"}}")));
        var chunk = (byte[])good.Clone();
        chunk[16] = 0x41; // chunk type is no longer JSON
        Assert.Equal("glb.err.chunk", Key(chunk));
        Assert.Equal("glb.err.missing", Assert.Throws<GlbValidationException>(() => GlbInspector.Inspect(Path.Combine(folder.Path, "nope.glb"))).Key);
    }

    [Fact]
    public void Extensions_are_checked()
    {
        using var folder = new TempFolder();
        var required = GlbBuilder.Build(GlbBuilder.Triangle(",\"extensionsUsed\":[\"KHR_unknown_magic\"],\"extensionsRequired\":[\"KHR_unknown_magic\"]"), GlbBuilder.TriangleBin());
        var error = Assert.Throws<GlbValidationException>(() => GlbInspector.Inspect(GlbBuilder.Write(folder.Path, "a.glb", required)));
        Assert.Equal("glb.err.extensions", error.Key);
        Assert.Contains("KHR_unknown_magic", (string)error.Args[0]);

        var optional = GlbBuilder.Build(GlbBuilder.Triangle(",\"extensionsUsed\":[\"KHR_materials_variants\",\"KHR_draco_mesh_compression\"]"), GlbBuilder.TriangleBin());
        var info = GlbInspector.Inspect(GlbBuilder.Write(folder.Path, "b.glb", optional));
        Assert.Equal(new[] { "KHR_materials_variants" }, info.UnsupportedExtensions);
        Assert.Contains(info.Warnings, w => w.Key == "glb.warn.extensions");

        // Everything the bundled loader can decode is accepted, even when required.
        var supported = GlbBuilder.Build(GlbBuilder.Triangle(",\"extensionsUsed\":[\"KHR_draco_mesh_compression\",\"EXT_meshopt_compression\",\"KHR_texture_basisu\"],\"extensionsRequired\":[\"KHR_draco_mesh_compression\"]"), GlbBuilder.TriangleBin());
        Assert.Empty(GlbInspector.Inspect(GlbBuilder.Write(folder.Path, "c.glb", supported)).Warnings);
    }

    [Fact]
    public void Missing_textures_and_broken_materials_are_reported()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(Path.Combine(folder.Path, "da.png"), new byte[] { 0x89, 0x50 });
        var extra = ",\"images\":[{\"uri\":\"da.png\"},{\"uri\":\"fehlt.png\"},{\"uri\":\"https://example.com/remote.png\"},{\"uri\":\"../../outside.png\"}]," +
                    "\"textures\":[{\"source\":0}]," +
                    "\"materials\":[{\"name\":\"Kaputt\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":7}}}]";
        var json = GlbBuilder.Triangle(extra).Replace("\"materials\":[{\"name\":\"Rot\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,0,0,1]}}],", "");
        var info = GlbInspector.Inspect(GlbBuilder.Write(folder.Path, "m.glb", GlbBuilder.Build(json, GlbBuilder.TriangleBin())));
        Assert.Contains(info.Warnings, w => w.Key == "glb.warn.missingTexture" && (string)w.Args[0] == "fehlt.png");
        Assert.Contains(info.Warnings, w => w.Key == "glb.warn.missingTexture" && (string)w.Args[0] == "../../outside.png"); // never read outside the folder
        Assert.Contains(info.Warnings, w => w.Key == "glb.warn.remote");
        Assert.Contains(info.Warnings, w => w.Key == "glb.warn.badMaterial" && (string)w.Args[0] == "Kaputt");
        Assert.DoesNotContain(info.Warnings, w => (string?)w.Args.FirstOrDefault() == "da.png");
    }

    [Fact]
    public void External_paths_stay_inside_the_model_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "modelle");
        Assert.Equal(Path.Combine(folder, "tex", "a.png"), GlbInspector.ResolveExternal(folder, "tex/a.png"));
        Assert.Null(GlbInspector.ResolveExternal(folder, "../geheim.txt"));
        Assert.Null(GlbInspector.ResolveExternal(folder, "C:\\Windows\\win.ini"));
        Assert.Null(GlbInspector.ResolveExternal(folder, ""));
    }
}

public sealed class ViewerProtocolTests
{
    [Fact]
    public void Valid_messages_are_accepted_and_others_dropped()
    {
        Assert.NotNull(ViewerProtocol.Parse("""{"type":"ready"}"""));
        Assert.NotNull(ViewerProtocol.Parse("""{"type":"progress","loaded":10,"total":100}"""));
        Assert.NotNull(ViewerProtocol.Parse("""{"type":"key","key":"f11"}"""));
        Assert.NotNull(ViewerProtocol.Parse("""{"type":"selection","id":null}"""));
        Assert.NotNull(ViewerProtocol.Parse("""{"type":"loaded","info":{},"tree":{},"animations":[],"warnings":[]}"""));

        Assert.Null(ViewerProtocol.Parse(null));
        Assert.Null(ViewerProtocol.Parse("not json"));
        Assert.Null(ViewerProtocol.Parse("[1,2]"));
        Assert.Null(ViewerProtocol.Parse("""{"type":"runScript","code":"alert(1)"}""")); // unknown type
        Assert.Null(ViewerProtocol.Parse("""{"type":"progress","loaded":"viel"}"""));
        Assert.Null(ViewerProtocol.Parse("""{"type":"key","key":"alt+f4"}"""));
        Assert.Null(ViewerProtocol.Parse("""{"type":"screenshot","data":"x","target":"C:/Windows"}"""));
        Assert.Null(ViewerProtocol.Parse("""{"type":"loaded","info":{}}"""));
        Assert.Null(ViewerProtocol.Parse("""{"type":5}"""));
    }

    [Fact]
    public void Commands_are_whitelisted_json()
    {
        var json = ViewerProtocol.Command("setVisible", new { id = 3, visible = false });
        using var document = JsonDocument.Parse(json);
        Assert.Equal("setVisible", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("id").GetInt32());
        Assert.Throws<ArgumentException>(() => ViewerProtocol.Command("eval", new { code = "x" }));
    }
}

public sealed class ViewerResourceTests
{
    [Fact]
    public void All_viewer_files_are_embedded()
    {
        var files = ViewerResources.Files.ToHashSet();
        foreach (var required in new[]
                 {
                     "index.html", "viewer.js", "viewer.css", "protocol.js", "viewmodes.js", "lib/three.module.min.js", "lib/THREE-LICENSE.txt",
                     "lib/jsm/controls/OrbitControls.js", "lib/jsm/loaders/GLTFLoader.js", "lib/jsm/loaders/DRACOLoader.js", "lib/jsm/loaders/KTX2Loader.js",
                     "lib/jsm/environments/RoomEnvironment.js", "lib/jsm/utils/BufferGeometryUtils.js", "lib/jsm/utils/WorkerPool.js",
                     "lib/jsm/libs/meshopt_decoder.module.js", "lib/jsm/libs/ktx-parse.module.js", "lib/jsm/libs/zstddec.module.js",
                     "lib/jsm/libs/draco/gltf/draco_decoder.js", "lib/jsm/libs/draco/gltf/draco_decoder.wasm",
                     "lib/jsm/libs/basis/basis_transcoder.js", "lib/jsm/libs/basis/basis_transcoder.wasm"
                 })
            Assert.True(files.Contains(required), $"{required} is not embedded.");
    }

    [Fact]
    public void Resources_resolve_with_types_and_never_leave_the_app_folder()
    {
        var html = ViewerResources.Resolve(new Uri(ViewerResources.IndexUrl));
        Assert.NotNull(html);
        Assert.StartsWith("text/html", html!.Value.ContentType);
        Assert.Equal("application/wasm", ViewerResources.Resolve(new Uri(ViewerResources.AppOrigin + "/lib/jsm/libs/draco/gltf/draco_decoder.wasm"))!.Value.ContentType);
        Assert.StartsWith("text/javascript", ViewerResources.Resolve(new Uri(ViewerResources.AppOrigin + "/viewer.js"))!.Value.ContentType);
        Assert.Null(ViewerResources.Resolve(new Uri(ViewerResources.AppOrigin + "/../Freedom.dll")));
        Assert.Null(ViewerResources.Resolve(new Uri(ViewerResources.AppOrigin + "/lib/%2e%2e/%2e%2e/secret")));
        Assert.Null(ViewerResources.Resolve(new Uri("https://example.com/index.html")));
        Assert.Null(ViewerResources.Resolve(new Uri("http://" + ViewerResources.AppHost + "/index.html")));
        Assert.Null(ViewerResources.Resolve(new Uri(ViewerResources.AppOrigin + "/missing.js")));
        Assert.Equal("https://model.freedom-viewer.local/Mein%20Modell.glb", ViewerResources.ModelUrl(@"C:\x\Mein Modell.glb"));
    }

    [Fact]
    public void Content_security_policy_is_strict()
    {
        var csp = ViewerResources.ContentSecurityPolicy;
        Assert.Contains("default-src 'none'", csp);
        Assert.DoesNotContain("'unsafe-eval'", csp);
        Assert.DoesNotContain("'unsafe-inline'", csp);
        Assert.DoesNotMatch(@"https://(?!model\.freedom-viewer\.local)", csp);
        // The hash allows exactly the import map in index.html.
        using var reader = new StreamReader(ViewerResources.Resolve(new Uri(ViewerResources.IndexUrl))!.Value.Content);
        var importMap = Regex.Match(reader.ReadToEnd(), "<script type=\"importmap\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        var expected = "sha256-" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(importMap)));
        Assert.Contains($"'{expected}'", csp);
        Assert.Contains("\"three\":\"./lib/three.module.min.js\"", importMap);
    }

    [Fact]
    public void Viewer_scripts_have_no_eval_and_no_cdn()
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ViennaAtomicClock", "GlbViewer", "Web"));
        if (!Directory.Exists(web)) return;
        foreach (var file in new[] { "viewer.js", "protocol.js", "viewmodes.js", "index.html" })
        {
            var text = File.ReadAllText(Path.Combine(web, file));
            Assert.DoesNotMatch(@"\beval\s*\(|new Function\s*\(", text);
            Assert.DoesNotMatch(@"https?://(?!model\.freedom-viewer\.local)", text);
            foreach (Match import in Regex.Matches(text, @"from\s+'([^']+)'"))
                Assert.True(import.Groups[1].Value is "three" || import.Groups[1].Value.StartsWith("./") || import.Groups[1].Value.StartsWith("three/addons/"),
                    $"{file} imports {import.Groups[1].Value}");
        }
    }

    [Fact]
    public void Settings_are_repaired()
    {
        var settings = GlbViewerSettings.Normalize(new GlbViewerSettings { Exposure = double.NaN, LightIntensity = 99, AnimationSpeed = 3, Background = (ViewerBackground)42 });
        Assert.Equal(1, settings.Exposure);
        Assert.Equal(4, settings.LightIntensity);
        Assert.Equal(1, settings.AnimationSpeed);
        Assert.Equal(ViewerBackground.Dark, settings.Background);
    }

    [Fact]
    public void Every_viewer_text_exists_in_all_languages()
    {
        var placeholder = new Regex(@"\{\d\}");
        foreach (var (key, texts) in GlbStrings.Table)
        {
            Assert.True(texts.Length == Loc.Languages.Length, key);
            Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t), key));
            var expected = placeholder.Matches(texts[0]).Select(m => m.Value).OrderBy(v => v).ToList();
            foreach (var text in texts)
                Assert.True(expected.SequenceEqual(placeholder.Matches(text).Select(m => m.Value).OrderBy(v => v)), $"{key}: {text}");
        }
        var keys = new List<string>();
        keys.AddRange(Enum.GetValues<ViewerBackground>().Select(b => "glb.bg." + b));
        keys.AddRange(new[] { "normal", "unlit", "wireframe", "basecolor", "normals", "metalrough" }.SelectMany(m => new[] { "glb.mode." + m, $"glb.mode.{m}.tip" }));
        keys.AddRange(new[] { "mesh", "skinned", "camera", "light", "bone", "points", "line", "node" }.Select(k => "glb.kind." + k));
        keys.AddRange(new[] { "loading", "parsing", "cancelled", "missingTexture", "badMaterial", "emptyTitle", "emptyHint", "drop", "of", "webgl" }.Select(k => "glb.js." + k));
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ViennaAtomicClock", "GlbViewer"));
        if (Directory.Exists(source))
            foreach (var file in Directory.GetFiles(source, "*.cs"))
                keys.AddRange(Regex.Matches(File.ReadAllText(file), "\"(glb\\.[A-Za-z0-9_.]+[A-Za-z0-9])\"").Select(m => m.Groups[1].Value));
        var missing = keys.Distinct().Where(k => !GlbStrings.Table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing texts: " + string.Join(", ", missing));
    }
}

/// <summary>
/// Real end-to-end check: WebView2 + the bundled Three.js render the sample models in a hidden window.
/// Covers static PBR and skinned/morph animations, screenshots, a broken file and repeated loading.
/// </summary>
public sealed class GlbViewerFunctionalTests
{
    private readonly ITestOutputHelper _output;

    public GlbViewerFunctionalTests(ITestOutputHelper output) => _output = output;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "glb", name);

    private static async Task<ViewerMessage> WaitFor(GlbViewerPage page, Func<ViewerMessage, bool> match, int timeoutMs = 30000)
    {
        var done = new TaskCompletionSource<ViewerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ViewerMessage m)
        {
            if (match(m)) done.TrySetResult(m);
        }
        page.MessageReceived += Handler;
        try
        {
            var finished = await Task.WhenAny(done.Task, Task.Delay(timeoutMs));
            if (finished != done.Task) throw new TimeoutException("No matching message from the 3D view. Last error: " + page.LastError);
            return done.Task.Result;
        }
        finally
        {
            page.MessageReceived -= Handler;
        }
    }

    private static async Task<ViewerMessage> Open(GlbViewerPage page, string path)
    {
        var loaded = WaitFor(page, m => m.Type is "loaded" or "loadFailed");
        await page.OpenFileAsync(path);
        var message = await loaded;
        Assert.True(message.Type == "loaded", "Load failed: " + (message.Data.TryGetProperty("message", out var text) ? text.GetString() : page.LastError));
        return message;
    }

    [Fact]
    public void Models_render_animate_and_release_memory()
    {
        using var folder = new TempFolder();
        var log = new List<string>();
        var console = new List<string>();
        try
        {
        UiTestHost.RunAsync(async () =>
        {
            MapTilesOff();
            var page = new GlbViewerPage(new GlbViewerSettings()) { ScreenshotFolder = folder.Path, ShowDialogs = false };
            // A real window (off screen) so WebView2 renders exactly as in the app.
            var window = new Window
            {
                Width = 1280, Height = 800, Left = -4000, Top = 100, ShowInTaskbar = false, ShowActivated = false,
                WindowStyle = WindowStyle.None, Content = page
            };
            window.Show();
            try
            {
                var ready = WaitFor(page, m => m.Type == "ready");
                await page.EnsureInitializedAsync("--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling");
                Assert.Null(page.LastError);
                await page.Bridge!.EnableDiagnosticsAsync(line => { lock (console) console.Add(line); });
                await ready;

                // 1. Static PBR model: normal, emissive, AO, metal/roughness textures.
                var helmet = await Open(page, Fixture("DamagedHelmet.glb"));
                var info = helmet.Data.GetProperty("info");
                log.Add($"Helmet: {info}");
                Assert.Equal(1, info.GetProperty("meshes").GetInt32());
                Assert.Equal(5, info.GetProperty("textures").GetInt32());
                Assert.Equal(15452, info.GetProperty("triangles").GetInt64()); // 46 356 indices / 3
                Assert.Equal(14556, info.GetProperty("vertices").GetInt64());
                Assert.Empty(helmet.Data.GetProperty("animations").EnumerateArray());
                Assert.Equal(0, helmet.Data.GetProperty("warnings").GetArrayLength());
                var dims = info.GetProperty("dimensions").EnumerateArray().Select(d => d.GetDouble()).ToList();
                Assert.All(dims, d => Assert.InRange(d, 1.0, 3.0));
                log.Add($"Helmet: {info}");

                // Selection, transform and visibility through the message channel.
                var root = helmet.Data.GetProperty("tree").GetProperty("id").GetInt32();
                var selected = WaitFor(page, m => m.Type == "selection");
                page.Select(root + 1);
                var selection = await selected;
                Assert.Equal(root + 1, selection.Data.GetProperty("id").GetInt32());
                Assert.True(selection.Data.GetProperty("props").GetProperty("vertices").GetInt64() > 0);

                foreach (var mode in new[] { "unlit", "wireframe", "basecolor", "normals", "metalrough", "normal" }) page.SetViewMode(mode);

                // Screenshots: a real PNG of the viewport.
                var shot = WaitFor(page, m => m.Type == "screenshot");
                page.Screenshot(clipboard: false);
                await shot;
                await Task.Delay(100);
                Assert.NotNull(page.LastScreenshotPath);
                BitmapFrame png;
                using (var file = File.OpenRead(page.LastScreenshotPath!))
                    png = BitmapFrame.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Assert.True(png.PixelWidth >= 400 && png.PixelHeight >= 200);

                // 2. Animated, skinned model with three clips.
                var fox = await Open(page, Fixture("Fox.glb"));
                var clips = fox.Data.GetProperty("animations").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
                Assert.Equal(new[] { "Survey", "Walk", "Run" }, clips);
                var first = await WaitFor(page, m => m.Type == "animation" && m.Data.GetProperty("playing").GetBoolean());
                await Task.Delay(600);
                var later = await WaitFor(page, m => m.Type == "animation" && m.Data.GetProperty("time").GetDouble() > first.Data.GetProperty("time").GetDouble() + 0.2);
                Assert.True(later.Data.GetProperty("playing").GetBoolean());
                var switched = WaitFor(page, m => m.Type == "animation" && m.Data.GetProperty("clip").GetInt32() == 2);
                page.Animation("clip", 2);
                await switched;
                var paused = WaitFor(page, m => m.Type == "animation" && !m.Data.GetProperty("playing").GetBoolean());
                page.Animation("pause");
                await paused;
                var seeked = WaitFor(page, m => m.Type == "animation" && Math.Abs(m.Data.GetProperty("time").GetDouble() - 0.3) < 0.01);
                page.Animation("seek", 0.3);
                await seeked;

                // Frame rate while the model rotates continuously.
                page.Animation("play");
                var stats = await WaitFor(page, m => m.Type == "stats" && m.Data.GetProperty("fps").GetDouble() > 0, 10000);
                stats = await WaitFor(page, m => m.Type == "stats" && m.Data.GetProperty("fps").GetDouble() > 0, 10000);
                log.Add($"Fox animated: {stats.Data}");

                // 3. Morph targets and alpha modes load too.
                var morph = await Open(page, Fixture("AnimatedMorphCube.glb"));
                Assert.Single(morph.Data.GetProperty("animations").EnumerateArray());
                var alpha = await Open(page, Fixture("AlphaBlendModeTest.glb"));
                Assert.Equal(6, alpha.Data.GetProperty("info").GetProperty("materials").GetInt32());

                // 4. A broken file leaves the open model untouched.
                var broken = GlbBuilder.Write(folder.Path, "kaputt.glb", Encoding.ASCII.GetBytes("this is not a 3D model"));
                await page.OpenFileAsync(broken);
                Assert.Equal("glb.err.header", ErrorKey(page.LastError));
                Assert.EndsWith("AlphaBlendModeTest.glb", page.ModelPath);

                // A file that passes the container check but cannot be parsed fails in the renderer, also without losing the model.
                var badJson = GlbBuilder.Write(folder.Path, "falsch.glb", GlbBuilder.Build("{\"asset\":{\"version\":\"2.0\"},\"scenes\":[{\"nodes\":[5]}],\"nodes\":[]}"));
                var failed = WaitFor(page, m => m.Type is "loadFailed" or "loaded");
                await page.OpenFileAsync(badJson);
                var outcome = await failed;
                log.Add($"Invalid scene: {outcome.Type}");
                if (outcome.Type == "loadFailed") Assert.EndsWith("AlphaBlendModeTest.glb", page.ModelPath);

                // 5. Repeated loading does not keep GPU memory alive.
                long Geometries(ViewerMessage m) => m.Data.GetProperty("geometries").GetInt64();
                long Textures(ViewerMessage m) => m.Data.GetProperty("textures").GetInt64();
                var baseline = new List<(long, long)>();
                for (var round = 0; round < 4; round++)
                {
                    await Open(page, Fixture("DamagedHelmet.glb"));
                    await Open(page, Fixture("Fox.glb"));
                    var memory = WaitFor(page, m => m.Type == "stats");
                    page.RequestStats();
                    var m = await memory;
                    baseline.Add((Geometries(m), Textures(m)));
                }
                log.Add("Memory per round (geometries, textures): " + string.Join(" ", baseline));
                Assert.True(baseline[^1].Item1 <= baseline[0].Item1 + 2, "Geometries keep growing: " + string.Join(" ", baseline));
                Assert.True(baseline[^1].Item2 <= baseline[0].Item2 + 2, "Textures keep growing: " + string.Join(" ", baseline));

                // 6. Model information export (never onto the GLB itself).
                var exportPath = Path.Combine(folder.Path, "info.json");
                page.ExportInfo(exportPath);
                using var export = JsonDocument.Parse(File.ReadAllText(exportPath));
                Assert.Equal(3, export.RootElement.GetProperty("gltf").GetProperty("animations").GetInt32());
                var before = File.ReadAllBytes(Fixture("Fox.glb")).Length;
                page.ExportInfo(Fixture("Fox.glb"));
                Assert.Equal(before, File.ReadAllBytes(Fixture("Fox.glb")).Length);
            }
            finally
            {
                page.Shutdown();
                window.Close();
            }
        }, TimeSpan.FromMinutes(3));
        }
        finally
        {
            foreach (var line in log) _output.WriteLine(line);
            lock (console) foreach (var line in console.Take(40)) _output.WriteLine("console: " + line);
        }
    }

    private static string? ErrorKey(string? message) =>
        message is null ? null : GlbStrings.Table.FirstOrDefault(p => p.Value.Contains(message)).Key;

    private static void MapTilesOff() => ViennaAtomicClock.Weather.MapTiles.Enabled = false;
}
