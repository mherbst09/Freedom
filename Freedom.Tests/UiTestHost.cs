using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ViennaAtomicClock;

namespace Freedom.Tests;

/// <summary>
/// One real Freedom <see cref="App"/> (styles, theme brushes, texts) on a dedicated STA thread.
/// UI tests run their code on it with <see cref="Run"/>, so pages render exactly as in the app.
/// </summary>
internal static class UiTestHost
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    public static Dispatcher Dispatcher
    {
        get
        {
            lock (Gate)
            {
                if (_dispatcher is not null) return _dispatcher;
                using var ready = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent();
                    Loc.Apply("de");
                    App.ApplyTheme(Themes.Standard, true);
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                }) { IsBackground = true, Name = "Freedom UI tests" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait();
                return _dispatcher!;
            }
        }
    }

    public static void Run(Action action)
    {
        Exception? failure = null;
        Dispatcher.Invoke(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Runs async UI code on the host thread (the dispatcher keeps pumping while it awaits).</summary>
    public static void RunAsync(Func<Task> action, TimeSpan timeout)
    {
        var task = Dispatcher.InvokeAsync(action).Task.Unwrap();
        if (!task.Wait(timeout)) throw new TimeoutException("UI test did not finish in time.");
        task.GetAwaiter().GetResult();
    }

    public static T Run<T>(Func<T> action)
    {
        T result = default!;
        Run(() => { result = action(); });
        return result;
    }

    /// <summary>Lays an element out at a size and renders it (like a real frame).</summary>
    public static RenderTargetBitmap Render(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        // Let queued layout/data work run, as the dispatcher would between frames.
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    /// <summary>Saves a picture when FREEDOM_RENDER_DIR is set (handy for looking at a page without starting the app).</summary>
    public static void SaveIfRequested(BitmapSource bitmap, string name)
    {
        var folder = Environment.GetEnvironmentVariable("FREEDOM_RENDER_DIR");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }
}
