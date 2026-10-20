using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace ViennaAtomicClock.GlbViewer;

/// <summary>
/// 3D viewer for .glb files. WPF shows the controls (toolbar, scene tree, properties, display settings, model
/// information, animation bar); the 3D view is Three.js in WebView2, fed with validated JSON messages. Files are
/// only read from disk – nothing is uploaded or written back into the GLB.
/// </summary>
internal sealed partial class GlbViewerPage : Grid
{
    private readonly GlbViewerSettings _settings;
    private ViewerBridge? _bridge;
    private Task? _initialization;
    private string? _pendingPath;
    private GlbInspection? _inspection;
    private GlbInspection? _loadingInspection;
    private string? _loadingPath;
    private JsonElement? _info;
    private readonly List<string> _warnings = new();
    private readonly List<(int Index, string Name, double Duration)> _clips = new();
    private bool _fullscreen;
    private bool _loading;
    private string _viewMode = "normal";
    private bool _autoRotate;
    private int? _selectedId;

    public event Action? CloseRequested;
    public event Action? FullscreenRequested;

    /// <summary>Every valid message from the 3D view (used by the tests).</summary>
    public event Action<ViewerMessage>? MessageReceived;

    public GlbViewerPage() : this(GlbViewerSettings.Load()) { }

    public GlbViewerPage(GlbViewerSettings settings)
    {
        _settings = settings;
        AllowDrop = true;
        BuildLayout();
        IsVisibleChanged += (_, _) => { if (IsVisible) _ = EnsureInitializedAsync(); };
        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += OnDragOver;
        Drop += OnDrop;
        Loc.Changed += () => { RebuildTexts(); SendLanguage(); };
    }

    public bool IsReady => _bridge?.IsReady ?? false;
    public string? ModelPath => _inspection?.Path;
    public JsonElement? Info => _info;
    public IReadOnlyList<string> Warnings => _warnings;
    public string? LastError { get; private set; }
    public JsonElement? LastStats { get; private set; }
    public JsonElement? LastAnimation { get; private set; }
    public string? LastScreenshotPath { get; private set; }
    /// <summary>False suppresses message boxes (tests).</summary>
    public bool ShowDialogs { get; set; } = true;

    internal ViewerBridge? Bridge => _bridge;

    /// <summary>Where screenshots go without asking (tests); null opens a save dialog.</summary>
    public string? ScreenshotFolder { get; set; }

    /// <summary>Starts WebView2 once (also used by tests with extra browser arguments).</summary>
    public Task EnsureInitializedAsync(string? browserArguments = null) => _initialization ??= InitializeAsync(browserArguments);

    private async Task InitializeAsync(string? browserArguments)
    {
        _bridge = new ViewerBridge();
        _bridge.Message += OnMessage;
        _bridge.FileDropped += path => _ = OpenFileAsync(path);
        _bridge.Crashed += kind => ShowStatus(Loc.F("glb.err.crashed", kind), warning: true);
        _viewportHost.Child = _bridge.View;
        try
        {
            await _bridge.InitializeAsync(browserArguments);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _viewportHost.Child = RuntimeMissingPanel();
            LastError = Loc.T("glb.err.runtime");
        }
        catch (Exception error)
        {
            _viewportHost.Child = RuntimeMissingPanel(error.Message);
            LastError = error.Message;
        }
    }

    // ───────────────────────── opening files ─────────────────────────

    public void ShowOpenDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.T("glb.filter") + " (*.glb)|*.glb",
            InitialDirectory = Directory.Exists(_settings.LastFolder) ? _settings.LastFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _ = OpenFileAsync(dialog.FileName);
    }

    /// <summary>Checks the file in the background and hands it to the 3D view. A failure keeps the current model.</summary>
    public async Task OpenFileAsync(string path)
    {
        if (_loading) _bridge?.Send("cancelLoad");
        LastError = null;
        GlbInspection inspection;
        try
        {
            inspection = await Task.Run(() => GlbInspector.Inspect(path));
        }
        catch (GlbValidationException error)
        {
            Fail(Loc.F(error.Key, error.Args));
            return;
        }

        _settings.LastFolder = Path.GetDirectoryName(path);
        _settings.Save();
        await EnsureInitializedAsync();
        if (_bridge is null || LastError == Loc.T("glb.err.runtime")) return;
        _loadingInspection = inspection;
        _loadingPath = path;
        if (!_bridge.IsReady)
        {
            _pendingPath = path;
            return;
        }
        StartLoad(path, inspection);
    }

    private void StartLoad(string path, GlbInspection inspection)
    {
        _bridge!.SetModel(path);
        _loading = true;
        UpdateLoading(0, inspection.FileSize);
        _bridge.Send("load", new { url = ViewerResources.ModelUrl(path), name = Path.GetFileName(path), size = inspection.FileSize });
    }

    public void CancelLoad()
    {
        if (!_loading) return;
        _bridge?.Send("cancelLoad");
    }

    private void Fail(string message)
    {
        LastError = message;
        _loading = false;
        UpdateLoading(null, 0);
        ShowStatus(message, warning: true);
        if (ShowDialogs && Window.GetWindow(this) is { IsVisible: true } owner && IsVisible)
            MessageBox.Show(owner, message + (_inspection is null ? "" : "\n\n" + Loc.T("glb.err.kept")), Loc.T("glb.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ───────────────────────── messages from the 3D view ─────────────────────────

    private void OnMessage(ViewerMessage message)
    {
        var d = message.Data;
        switch (message.Type)
        {
            case "ready":
                SendLanguage();
                SendSettings();
                if (_pendingPath is { } pending && _loadingInspection is { } inspection)
                {
                    _pendingPath = null;
                    StartLoad(pending, inspection);
                }
                break;
            case "progress":
                UpdateLoading(d.GetProperty("loaded").GetDouble(), d.GetProperty("total").GetDouble());
                break;
            case "loaded":
                _bridge?.ReleaseFiles();
                OnLoaded(d);
                break;
            case "loadFailed":
                _bridge?.ReleaseFiles();
                _loading = false;
                UpdateLoading(null, 0);
                if (d.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True) ShowStatus(Loc.T("glb.status.cancelled"));
                else Fail(Loc.F("glb.err.load", d.GetProperty("message").GetString() ?? ""));
                break;
            case "selection":
                _selectedId = d.GetProperty("id").ValueKind == JsonValueKind.Number ? d.GetProperty("id").GetInt32() : null;
                ShowProperties(d.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object ? props : null);
                SelectInTree(_selectedId);
                break;
            case "stats":
                LastStats = d;
                UpdateStats(d);
                break;
            case "animation":
                LastAnimation = d;
                UpdateAnimation(d);
                break;
            case "state":
                _settings.Grid = d.GetProperty("grid").GetBoolean();
                _settings.Axes = d.GetProperty("axes").GetBoolean();
                _autoRotate = d.GetProperty("autoRotate").GetBoolean();
                _viewMode = d.GetProperty("viewMode").GetString() ?? "normal";
                _settings.Save();
                RefreshDisplayPanel();
                break;
            case "screenshot":
                SaveScreenshot(d);
                break;
            case "key":
                HandleKey(d.GetProperty("key").GetString());
                break;
            case "warning":
                break;
        }
        MessageReceived?.Invoke(message);
    }

    private void OnLoaded(JsonElement d)
    {
        _loading = false;
        UpdateLoading(null, 0);
        _inspection = _loadingInspection;
        _info = d.GetProperty("info").Clone();
        _warnings.Clear();
        if (_inspection is not null) _warnings.AddRange(_inspection.Warnings.Select(w => Loc.F(w.Key, w.Args)));
        foreach (var warning in d.GetProperty("warnings").EnumerateArray())
            if (warning.GetString() is { Length: > 0 } text && !_warnings.Contains(text)) _warnings.Add(text);
        _clips.Clear();
        foreach (var clip in d.GetProperty("animations").EnumerateArray())
            _clips.Add((clip.GetProperty("index").GetInt32(), clip.GetProperty("name").GetString() ?? "", clip.GetProperty("duration").GetDouble()));
        _selectedId = null;
        _fileTitle.Text = Path.GetFileName(_loadingPath ?? "");
        _fileSubtitle.Text = Loc.F("glb.status.loaded", FormatBytes(_inspection?.FileSize ?? 0), d.GetProperty("info").GetProperty("loadMs").GetDouble().ToString("0", Loc.Culture));
        BuildTree(d.GetProperty("tree"));
        ShowProperties(null);
        RefreshInfoPanel();
        RefreshAnimationBar();
        ShowStatus(_warnings.Count == 0 ? Loc.T("glb.status.ready") : Loc.F("glb.status.warnings", _warnings.Count), warning: _warnings.Count > 0);
        _bridge?.Focus();
    }

    private void SendSettings() => _bridge?.Send("settings", new
    {
        background = _settings.Background.ToString(), exposure = _settings.Exposure, light = _settings.LightIntensity,
        shadows = _settings.Shadows, grid = _settings.Grid, axes = _settings.Axes, speed = _settings.AnimationSpeed, autoRotate = _autoRotate
    });

    private void SendLanguage()
    {
        if (_bridge is not { IsReady: true }) return;
        var keys = new[] { "loading", "parsing", "cancelled", "missingTexture", "badMaterial", "emptyTitle", "emptyHint", "drop", "of", "webgl" };
        _bridge.Send("language", new { texts = keys.ToDictionary(k => k, k => Loc.T("glb.js." + k)) });
    }

    private void ChangeSettings(Action change)
    {
        change();
        GlbViewerSettings.Normalize(_settings);
        _settings.Save();
        SendSettings();
        RefreshDisplayPanel();
    }

    // ───────────────────────── commands ─────────────────────────

    public void ResetView() => _bridge?.Send("resetView");
    public void CenterModel() => _bridge?.Send("centerModel");
    public void Focus(int? id) => _bridge?.Send("focus", new { id });
    public void Select(int? id) => _bridge?.Send("select", new { id });
    public void SetVisible(int id, bool visible) => _bridge?.Send("setVisible", new { id, visible });
    public void ShowAll()
    {
        _bridge?.Send("showAll");
        foreach (var item in AllTreeItems()) if (item.Tag is TreeNodeInfo node) node.SetVisible(true);
    }
    public void SetViewMode(string mode)
    {
        _viewMode = mode;
        _bridge?.Send("viewMode", new { mode });
        RefreshDisplayPanel();
    }
    public void Animation(string action, object? value = null) => _bridge?.Send("animation", new { action, value });
    public void RequestStats() => _bridge?.Send("requestStats");

    public void Screenshot(bool clipboard) => _bridge?.Send("screenshot", new { target = clipboard ? "clipboard" : "file", transparent = _settings.TransparentScreenshot });

    private void ToggleAutoRotate()
    {
        _autoRotate = !_autoRotate;
        SendSettings();
        RefreshDisplayPanel();
    }

    public void Close()
    {
        if (_fullscreen) FullscreenRequested?.Invoke();
        CancelLoad();
        _bridge?.ReleaseFiles();
        CloseRequested?.Invoke();
    }

    /// <summary>The main window reports its full-screen state; the viewer then hides its own controls.</summary>
    public void SetFullscreen(bool fullscreen)
    {
        _fullscreen = fullscreen;
        _toolbar.Visibility = _status.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        ApplySidebars();
        _fullscreenButton.ToolTip = Loc.T(fullscreen ? "glb.tip.exitFullscreen" : "glb.tip.fullscreen");
    }

    /// <summary>Frees the 3D view when Freedom closes.</summary>
    public void Shutdown()
    {
        _settings.Save();
        _bridge?.Dispose();
        _bridge = null;
    }

    // ───────────────────────── keyboard and drag & drop ─────────────────────────

    private void HandleKey(string? key)
    {
        switch (key)
        {
            case "ctrl+o":
                ShowOpenDialog();
                break;
            case "f11":
                FullscreenRequested?.Invoke();
                break;
            case "escape":
                if (_fullscreen) FullscreenRequested?.Invoke();
                else Close();
                break;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // typing a value
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.O) { ShowOpenDialog(); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (key)
        {
            case Key.Escape: HandleKey("escape"); break;
            case Key.Space: Animation("toggle"); break;
            case Key.R: ResetView(); break;
            case Key.F: Focus(null); break;
            case Key.G: ChangeSettings(() => _settings.Grid = !_settings.Grid); break;
            case Key.W: SetViewMode(_viewMode == "wireframe" ? "normal" : "wireframe"); break;
            case Key.A: ToggleAutoRotate(); break;
            default: return;
        }
        e.Handled = true;
    }

    private static string? DroppedGlb(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files ? files[0] : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedGlb(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DroppedGlb(e) is { } path) _ = OpenFileAsync(path);
        e.Handled = true;
    }

    // ───────────────────────── screenshots and export ─────────────────────────

    private void SaveScreenshot(JsonElement d)
    {
        byte[] png;
        try
        {
            png = Convert.FromBase64String(d.GetProperty("data").GetString() ?? "");
            if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50) throw new FormatException();
        }
        catch (FormatException)
        {
            ShowStatus(Loc.T("glb.err.screenshot"), warning: true);
            return;
        }

        if (d.GetProperty("target").GetString() == "clipboard")
        {
            try
            {
                using var stream = new MemoryStream(png);
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var data = new DataObject();
                data.SetImage(image);
                data.SetData("PNG", new MemoryStream(png)); // keeps transparency for programs that read PNG
                Clipboard.SetDataObject(data, true);
                ShowStatus(Loc.T("glb.status.copied"));
            }
            catch (Exception error)
            {
                ShowStatus(Loc.F("glb.err.clipboard", error.Message), warning: true);
            }
            return;
        }

        var name = Path.GetFileNameWithoutExtension(_inspection?.Path ?? "freedom") + $"-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        string target;
        if (ScreenshotFolder is not null) target = Path.Combine(ScreenshotFolder, name);
        else
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = name, Filter = "PNG (*.png)|*.png", DefaultExt = ".png",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            target = dialog.FileName;
        }
        if (!ProtectSource(target)) return;
        try
        {
            File.WriteAllBytes(target, png);
            LastScreenshotPath = target;
            ShowStatus(Loc.F("glb.status.saved", target));
        }
        catch (Exception error)
        {
            ShowStatus(Loc.F("glb.err.save", error.Message), warning: true);
        }
    }

    /// <summary>Never write over the opened GLB.</summary>
    private bool ProtectSource(string target)
    {
        if (_inspection is null || !string.Equals(Path.GetFullPath(target), Path.GetFullPath(_inspection.Path), StringComparison.OrdinalIgnoreCase)) return true;
        ShowStatus(Loc.T("glb.err.overwrite"), warning: true);
        return false;
    }

    /// <summary>Model information as JSON: container check plus what the renderer counted.</summary>
    public string BuildInfoJson()
    {
        var export = new Dictionary<string, object?>
        {
            ["exportedBy"] = "Freedom GLB Viewer",
            ["exportedAt"] = DateTimeOffset.Now,
            ["file"] = _inspection is null ? null : new
            {
                path = _inspection.Path, name = Path.GetFileName(_inspection.Path), size = _inspection.FileSize, glbVersion = _inspection.Version,
                generator = _inspection.Generator
            },
            ["gltf"] = _inspection is null ? null : new
            {
                scenes = _inspection.Scenes, nodes = _inspection.Nodes, meshes = _inspection.Meshes, materials = _inspection.Materials,
                textures = _inspection.Textures, images = _inspection.Images, animations = _inspection.Animations, cameras = _inspection.Cameras,
                skins = _inspection.Skins, vertices = _inspection.Vertices, triangles = _inspection.Triangles,
                extensionsUsed = _inspection.ExtensionsUsed, extensionsRequired = _inspection.ExtensionsRequired
            },
            ["rendered"] = _info,
            ["animations"] = _clips.Select(c => new { index = c.Index, name = c.Name, duration = c.Duration }),
            ["warnings"] = _warnings
        };
        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }

    public void ExportInfo(string? path = null)
    {
        if (_inspection is null) return;
        if (path is null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.GetFileNameWithoutExtension(_inspection.Path) + "-info.json", Filter = "JSON (*.json)|*.json", DefaultExt = ".json",
                InitialDirectory = Path.GetDirectoryName(_inspection.Path)
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            path = dialog.FileName;
        }
        if (!ProtectSource(path)) return;
        try
        {
            File.WriteAllText(path, BuildInfoJson());
            ShowStatus(Loc.F("glb.status.saved", path));
        }
        catch (Exception error)
        {
            ShowStatus(Loc.F("glb.err.save", error.Message), warning: true);
        }
    }

    internal static string FormatBytes(double bytes) => bytes switch
    {
        < 1024 => $"{bytes:0} B",
        < 1024 * 1024 => (bytes / 1024).ToString("0.0", Loc.Culture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / 1024 / 1024).ToString("0.0", Loc.Culture) + " MB",
        _ => (bytes / 1024 / 1024 / 1024).ToString("0.00", Loc.Culture) + " GB"
    };
}
