using System.Windows.Media;

namespace NineTranscribe.UI;

/// <summary>Parses the #RRGGBB / #AARRGGBB strings the settings file stores.</summary>
public static class ColorHex
{
    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        try
        {
            return (Color)ColorConverter.ConvertFromString(hex.Trim());
        }
        catch (FormatException)
        {
            return fallback;
        }
        catch (NotSupportedException)
        {
            return fallback;
        }
    }
}
