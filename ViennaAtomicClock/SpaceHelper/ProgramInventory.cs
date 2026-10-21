using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace ViennaAtomicClock.SpaceHelper;

/// <summary>An installed program as Windows lists it under "Installierte Apps".</summary>
public sealed record InstalledProgram(string Name, string Publisher, string Version, long Size, DateTime? Installed, DateTime? LastChanged, string? InstallLocation,
    string UninstallCommand, bool PerUser)
{
    /// <summary>Days since the program folder was last changed (null when unknown).</summary>
    public int? DaysUnchanged(DateTime now) => LastChanged is { } changed ? (int)(now - changed).TotalDays : null;
}

/// <summary>
/// Reads the official uninstall entries (HKLM 64/32-bit and HKCU) and removes programs only through their own
/// registered Windows uninstaller – the same one "Apps &amp; Features" starts.
/// </summary>
public static class ProgramInventory
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledProgram> Read()
    {
        var programs = new List<InstalledProgram>();
        var sources = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default)
        };
        foreach (var (hive, view) in sources)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(name);
                        if (key is null) continue;
                        var program = FromKey(key, hive == RegistryHive.CurrentUser);
                        if (program is not null) programs.Add(program);
                    }
                    catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
                    {
                        // Unreadable entry.
                    }
                }
            }
            catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                // Unreadable hive.
            }
        }
        // The same program can be registered in both views.
        return programs.GroupBy(p => (p.Name, p.Version)).Select(g => g.First()).OrderByDescending(p => p.Size).ToList();
    }

    private static InstalledProgram? FromKey(RegistryKey key, bool perUser)
    {
        var name = key.GetValue("DisplayName") as string;
        var uninstall = key.GetValue("UninstallString") as string;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uninstall)) return null;
        // Updates, system components and parts of other programs are not shown (like in Windows itself).
        if (key.GetValue("SystemComponent") is int system && system == 1) return null;
        if (key.GetValue("ParentKeyName") is string || key.GetValue("ReleaseType") is string type && type.Contains("Update", StringComparison.OrdinalIgnoreCase)) return null;
        var size = key.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0L;
        DateTime? installed = null;
        if (key.GetValue("InstallDate") is string date && DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)) installed = parsed;
        var location = (key.GetValue("InstallLocation") as string)?.Trim('"', ' ');
        DateTime? changed = null;
        if (!string.IsNullOrEmpty(location) && Directory.Exists(location))
        {
            try
            {
                changed = Directory.GetLastWriteTimeUtc(location);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Unknown.
            }
        }
        return new InstalledProgram(name.Trim(), (key.GetValue("Publisher") as string ?? "").Trim(), (key.GetValue("DisplayVersion") as string ?? "").Trim(),
            size, installed, changed, location, uninstall.Trim(), perUser);
    }

    /// <summary>Splits a registered command into program and arguments (quoted or unquoted path).</summary>
    public static (string File, string Arguments) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (command[..(exe + 4)], command[(exe + 4)..].Trim());
        var space = command.IndexOf(' ');
        return space > 0 ? (command[..space], command[(space + 1)..].Trim()) : (command, "");
    }

    /// <summary>Starts the program's own uninstaller. Windows asks for administrator rights itself when the uninstaller needs them.</summary>
    public static bool StartUninstall(InstalledProgram program)
    {
        var (file, arguments) = SplitCommand(program.UninstallCommand);
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return false; // missing uninstaller or the user said no to the elevation prompt
        }
    }

    /// <summary>Opens "Installierte Apps" in the Windows settings.</summary>
    public static void OpenWindowsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Settings app not available.
        }
    }
}
