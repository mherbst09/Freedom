using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace ViennaAtomicClock.GlbViewer;

/// <summary>Detects the shared Evergreen runtime without creating a browser instance.</summary>
internal static class WebView2Runtime
{
    public const string DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/#download-section";

    public static bool IsAvailable(out string? version)
    {
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrWhiteSpace(version);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            version = null;
            return false;
        }
        catch (ArgumentException)
        {
            version = null;
            return false;
        }
    }

    public static bool OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
