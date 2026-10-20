using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ViennaAtomicClock.GlbViewer;

/// <summary>
/// Owns the WebView2 control: locked-down settings, embedded page, model folder mapping and the validated
/// message channel. The page can neither navigate away, open windows nor reach the network.
/// </summary>
internal sealed class ViewerBridge : IDisposable
{
    private CoreWebView2? _core;

    public ViewerBridge()
    {
        View = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 20, 22, 27) };
    }

    public WebView2 View { get; }
    public bool IsReady { get; private set; }

    /// <summary>Raised on the UI thread for every valid message from the page.</summary>
    public event Action<ViewerMessage>? Message;

    /// <summary>A file was dropped on the 3D view (path supplied by WebView2, never read by the page).</summary>
    public event Action<string>? FileDropped;

    /// <summary>The browser process or renderer crashed; the page is reloaded automatically.</summary>
    public event Action<string>? Crashed;

    public static string UserDataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViennaAtomicClock", "WebView2");

    /// <summary>Starts WebView2 and loads the viewer page. Throws <see cref="WebView2RuntimeNotFoundException"/> without the runtime.</summary>
    public async Task InitializeAsync(string? extraBrowserArguments = null)
    {
        var options = new CoreWebView2EnvironmentOptions(extraBrowserArguments ?? "");
        var environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder, options);
        await View.EnsureCoreWebView2Async(environment);
        _core = View.CoreWebView2;

        var settings = _core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false; // no reload, find or print inside the viewer
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsWebMessageEnabled = true;

        _core.AddWebResourceRequestedFilter(ViewerResources.AppOrigin + "/*", CoreWebView2WebResourceContext.All);
        _core.AddWebResourceRequestedFilter("https://" + ViewerResources.ModelHost + "/*", CoreWebView2WebResourceContext.All);
        _core.WebResourceRequested += OnResourceRequested;
        _core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(ViewerResources.AppOrigin + "/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        _core.NewWindowRequested += (_, e) => e.Handled = true;
        _core.DownloadStarting += (_, e) => e.Cancel = true;
        _core.WebMessageReceived += OnMessage;
        _core.ProcessFailed += (_, e) =>
        {
            IsReady = false;
            Crashed?.Invoke(e.ProcessFailedKind.ToString());
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                _core?.Reload();
        };
        _core.Navigate(ViewerResources.IndexUrl);
    }

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_core is null) return;
        var uri = new Uri(e.Request.Uri);
        if (uri.Host.Equals(ViewerResources.ModelHost, StringComparison.OrdinalIgnoreCase))
        {
            ServeModelFile(uri, e);
            return;
        }
        var resource = ViewerResources.Resolve(uri);
        if (resource is not { } found)
        {
            e.Response = _core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        var headers = $"Content-Type: {found.ContentType}\r\nCache-Control: no-cache\r\nX-Content-Type-Options: nosniff";
        if (found.ContentType.StartsWith("text/html", StringComparison.Ordinal))
            headers += "\r\nContent-Security-Policy: " + ViewerResources.ContentSecurityPolicy;
        e.Response = _core.Environment.CreateWebResourceResponse(found.Content, 200, "OK", headers);
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(ViewerResources.AppOrigin + "/", StringComparison.OrdinalIgnoreCase)) return;
        var message = ViewerProtocol.Parse(e.WebMessageAsJson);
        if (message is null) return;
        if (message.Type == "ready") IsReady = true;
        if (message.Type == "drop")
        {
            foreach (var item in e.AdditionalObjects ?? Enumerable.Empty<object>())
                if (item is CoreWebView2File file && !string.IsNullOrEmpty(file.Path))
                {
                    FileDropped?.Invoke(file.Path);
                    break;
                }
            return;
        }
        Message?.Invoke(message);
    }

    private string? _modelPath;
    private readonly List<SelfClosingStream> _openStreams = new();

    /// <summary>The file the page may read now; external textures are allowed only from its folder.</summary>
    public void SetModel(string path)
    {
        ReleaseFiles();
        _modelPath = Path.GetFullPath(path);
    }

    /// <summary>Closes every file handed to the page (after loading, cancelling or closing nothing stays locked).</summary>
    public void ReleaseFiles()
    {
        lock (_openStreams)
        {
            foreach (var stream in _openStreams) stream.Dispose();
            _openStreams.Clear();
        }
    }

    /// <summary>
    /// Streams the current model (or a file next to it) read-only from disk. WebView2 pulls the stream in chunks,
    /// so even large files never block the interface; nothing else on the PC is reachable.
    /// </summary>
    private void ServeModelFile(Uri uri, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var file = ViewerResources.ResolveModelFile(_modelPath, uri);
        if (file is null || !File.Exists(file))
        {
            e.Response = _core!.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Access-Control-Allow-Origin: " + ViewerResources.AppOrigin);
            return;
        }
        try
        {
            var stream = new SelfClosingStream(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan));
            lock (_openStreams)
            {
                _openStreams.RemoveAll(s => s.IsClosed);
                _openStreams.Add(stream);
            }
            var headers = $"Content-Type: {ViewerResources.ContentType(file)}\r\nContent-Length: {stream.Length}\r\n" +
                          $"Access-Control-Allow-Origin: {ViewerResources.AppOrigin}\r\nCache-Control: no-store";
            e.Response = _core!.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            e.Response = _core!.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "Access-Control-Allow-Origin: " + ViewerResources.AppOrigin);
        }
    }

    public void Send(string type, object? payload = null)
    {
        if (_core is null) return;
        _core.PostWebMessageAsJson(ViewerProtocol.Command(type, payload));
    }

    public void Focus() => View.Focus();

    /// <summary>Collects browser log entries (errors, CSP and network problems) – used by the tests for diagnosis.</summary>
    public async Task EnableDiagnosticsAsync(Action<string> log)
    {
        if (_core is null) return;
        _core.GetDevToolsProtocolEventReceiver("Log.entryAdded").DevToolsProtocolEventReceived += (_, e) => log(e.ParameterObjectAsJson);
        _core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) => log(e.ParameterObjectAsJson);
        _core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, e) => log(e.ParameterObjectAsJson);
        await _core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
        await _core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
    }

    public void Dispose()
    {
        ReleaseFiles();
        _core = null;
        View.Dispose();
    }
}

/// <summary>
/// Read-only file stream for WebView2 that closes the file as soon as it has been read to the end (or when it
/// is released), so an opened model is never kept locked.
/// </summary>
internal sealed class SelfClosingStream : Stream
{
    private Stream? _inner;
    private readonly long _length;

    public SelfClosingStream(Stream inner)
    {
        _inner = inner;
        _length = inner.Length;
    }

    public bool IsClosed => _inner is null;
    public override bool CanRead => true;
    public override bool CanSeek => _inner is not null;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _inner?.Position ?? _length;
        set { if (_inner is not null) _inner.Position = value; }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var inner = _inner;
        if (inner is null) return 0;
        int read;
        try { read = inner.Read(buffer, offset, count); }
        catch (ObjectDisposedException) { return 0; }
        if (read == 0) Dispose();
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner?.Seek(offset, origin) ?? _length;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Interlocked.Exchange(ref _inner, null)?.Dispose();
        base.Dispose(disposing);
    }
}
