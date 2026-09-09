using System.Windows;
using Microsoft.Win32;
using NineTranscribe.Settings;

namespace NineTranscribe.UI;

/// <summary>
/// Swaps the brush dictionary between the light and dark palettes, following the Windows
/// app theme when the user has not picked one explicitly.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static AppTheme _preference = AppTheme.System;

    /// <summary>Whether the dark palette is the one currently loaded.</summary>
    public static bool IsDarkActive { get; private set; }

    public static void Apply(AppTheme preference)
    {
        _preference = preference;
        bool dark = preference switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => IsSystemDark(),
        };

        if (Application.Current is not { } app)
        {
            return;
        }

        var palette = new ResourceDictionary
        {
            Source = new Uri(
                dark ? "Themes/Dark.xaml" : "Themes/Light.xaml",
                UriKind.Relative),
        };

        // Only the palette is swapped. Clearing the whole collection would also throw away the
        // control templates merged alongside it, and every control would snap back to WPF's
        // stock look on the first theme change.
        var merged = app.Resources.MergedDictionaries;
        int index = -1;
        for (int i = 0; i < merged.Count; i++)
        {
            string? source = merged[i].Source?.OriginalString;
            if (source is not null
                && (source.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase)
                    || source.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            merged[index] = palette;
        }
        else
        {
            merged.Insert(0, palette);
        }

        IsDarkActive = dark;
    }

    /// <summary>Re-evaluates the system theme; called when Windows reports a colour change.</summary>
    public static void Refresh()
    {
        if (_preference == AppTheme.System)
        {
            Apply(AppTheme.System);
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
