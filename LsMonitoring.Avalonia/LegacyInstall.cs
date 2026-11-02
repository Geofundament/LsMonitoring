using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace LsMonitoring.Avalonia;

/// <summary>
/// Up to 0.8 the app was installed by Velopack into %LOCALAPPDATA%\LsMonitoring and updated from the public
/// repository. Since 0.9 the shared Geofundament installer puts it into %LOCALAPPDATA%\Programs\Геофундамент.
/// The old copy is removed so it neither runs next to the new one (same Gotify and bot ports) nor sends
/// alerts without a key. Settings live in %LOCALAPPDATA%\LS Monitoring and are not touched by this.
/// </summary>
internal static class LegacyInstall
{
    private const string VelopackUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LsMonitoring";

    public static void RemoveVelopackCopy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(VelopackUninstallKey);
            if (key?.GetValue("QuietUninstallString") is not string command ||
                key.GetValue("InstallLocation") is not string location)
            {
                return;
            }

            var root = Path.TrimEndingDirectorySeparator(location) + Path.DirectorySeparatorChar;
            if (AppContext.BaseDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // "C:\...\LsMonitoring\Update.exe" --uninstall --silent
            var close = command.StartsWith('"') ? command.IndexOf('"', 1) : -1;
            if (close < 0)
            {
                return;
            }

            var exe = command[1..close];
            if (File.Exists(exe))
            {
                using var _ = Process.Start(new ProcessStartInfo(exe, command[(close + 1)..].Trim())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
        {
            // The old copy stays; it can still be removed from "Apps" in Windows settings.
        }
    }
}
