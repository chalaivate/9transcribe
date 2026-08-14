using System.IO;
using Microsoft.Win32;
using NineTranscribe.Diagnostics;

namespace NineTranscribe.Tray;

/// <summary>
/// Toggles "start with Windows" through the per-user Run key, which needs no administrator
/// rights and no installer.
/// </summary>
public static class StartupRegistrar
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "9Transcribe";

    /// <summary>Command line written to the Run key. --tray suppresses the first-run window.</summary>
    private static string CommandLine
    {
        get
        {
            // Assembly.Location is empty in a single-file publish; ProcessPath is the exe.
            string exe = Environment.ProcessPath ?? string.Empty;
            return $"\"{exe}\" --tray";
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Warn($"Run key could not be read: {ex.Message}");
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Run key could not be written: {ex.Message}");
        }
    }

    /// <summary>
    /// Rewrites the stored path when it no longer matches this executable. The app is
    /// portable, so the user can move or rename the exe at any time and the entry would
    /// otherwise point at nothing.
    /// </summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string current || current.Length == 0)
            {
                return;
            }

            if (!string.Equals(current, CommandLine, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
                Log.Info("Run key path refreshed after the executable moved");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Run key could not be refreshed: {ex.Message}");
        }
    }
}
