using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;

namespace ViennaAtomicClock;

public partial class App : Application
{
    public static bool IsDark { get; private set; } = true;
    private bool _handlingUiException;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        var settings = AppSettings.Load();
        ApplyTheme(settings.Theme, settings.DarkMode, settings.ColorMode ? ParseColor(settings.CustomColor) : null);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_handlingUiException)
        {
            Shutdown(-1);
            return;
        }

        _handlingUiException = true;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "error.log"), $"[{DateTimeOffset.Now:O}] {e.Exception}\n\n");
        }
        catch
        {
            // Logging must never obscure the original UI problem.
        }

        try
        {
            (MainWindow as MainWindow)?.RecoverFromUiError();
            e.Handled = true;
            MessageBox.Show("Die Ansicht konnte nicht vollständig geladen werden. Die App ist zur Atomuhr zurückgekehrt. Details wurden im lokalen Fehlerprotokoll gespeichert.",
                "Freedom", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _handlingUiException = false;
        }
    }

    public static string ThemeName { get; private set; } = Themes.War;

    /// <summary>Picked colour while the coloured mode is on, otherwise null.</summary>
    public static Color? CustomColor { get; private set; }

    public static void ApplyTheme(bool dark) => ApplyTheme(ThemeName, dark, null);

    public static void ApplyTheme(string theme, bool dark, Color? custom = null)
    {
        IsDark = dark || custom is not null;
        CustomColor = custom;
        ThemeName = Themes.Normalize(theme);
        Themes.Apply(Current.Resources, ThemeName, IsDark, custom);
    }

    public static Color? ParseColor(string? hex)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? null : (Color)ColorConverter.ConvertFromString(hex); }
        catch { return null; }
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
