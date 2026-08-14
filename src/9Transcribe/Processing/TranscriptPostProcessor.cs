using System.Text;
using System.Text.RegularExpressions;
using NineTranscribe.Settings;

namespace NineTranscribe.Processing;

/// <summary>A single find/replace rule applied to every transcript, in list order.</summary>
public sealed record ReplacementRule(string Find, string Replace, bool MatchCase)
{
    public static ReplacementRule From(ReplacementRuleSetting setting) =>
        new(setting.Find ?? string.Empty, setting.Replace ?? string.Empty, setting.MatchCase);
}

/// <summary>
/// Cleans up a raw transcript before it is typed: trims, applies the user's correction
/// dictionary, and decides what (if anything) follows the text.
/// </summary>
public static class TranscriptPostProcessor
{
    private static readonly Regex MultipleSpaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    public static string Process(string? raw, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string text = raw.Trim();

        foreach (ReplacementRuleSetting rule in settings.Replacements)
        {
            text = ApplyReplacement(text, ReplacementRule.From(rule));
        }

        if (settings.CollapseSpaces)
        {
            text = MultipleSpaces.Replace(text, " ");
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return text + TrailingFor(text, settings.TrailingText);
    }

    /// <summary>
    /// Plain substring replacement — Thai has no spaces between words, so a whole-word
    /// option would silently never match Thai terms.
    /// </summary>
    public static string ApplyReplacement(string text, ReplacementRule rule)
    {
        if (string.IsNullOrEmpty(rule.Find) || string.IsNullOrEmpty(text))
        {
            return text;
        }

        return text.Replace(
            rule.Find,
            rule.Replace ?? string.Empty,
            rule.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Thai runs words together, so an unconditional trailing space inserts a phrase break
    /// every time the user dictates mid-sentence. Only Latin text needs the separator.
    /// </summary>
    public static string TrailingFor(string text, TrailingText mode) => mode switch
    {
        TrailingText.Space => " ",
        TrailingText.NewLine => Environment.NewLine,
        TrailingText.Smart => EndsWithLatinOrDigit(text) ? " " : string.Empty,
        _ => string.Empty,
    };

    private static bool EndsWithLatinOrDigit(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        char last = text[^1];
        return (last >= 'a' && last <= 'z')
            || (last >= 'A' && last <= 'Z')
            || (last >= '0' && last <= '9');
    }

    /// <summary>
    /// Splits a transcript for the overlay preview, keeping it to a readable single blob.
    /// </summary>
    public static string ForPreview(string text, int maxChars = 240)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var builder = new StringBuilder(maxChars + 1);
        builder.Append(text.AsSpan(0, maxChars).TrimEnd());
        builder.Append('…');
        return builder.ToString();
    }
}
