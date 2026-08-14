using System.Text;

namespace NineTranscribe.Api;

/// <summary>
/// Builds the <c>prompt</c> field: a short Thai lead-in that primes the model for
/// code-switched speech, followed by the user's glossary. Both model families read the
/// prompt as preceding context, so a plain comma-separated list conditions spelling well.
/// </summary>
public static class PromptBuilder
{
    public static string Build(string? prefix, IEnumerable<string>? vocabulary, ModelCapabilities model)
    {
        ArgumentNullException.ThrowIfNull(model);

        string head = (prefix ?? string.Empty).Trim();
        List<string> terms = Normalize(vocabulary);

        if (terms.Count == 0)
        {
            return Truncate(head, model.PromptCharBudget);
        }

        var builder = new StringBuilder();
        if (head.Length > 0)
        {
            builder.Append(head);
            if (!head.EndsWith(' ') && !head.EndsWith(':'))
            {
                builder.Append(' ');
            }
        }

        int budget = model.PromptCharBudget;
        int used = builder.Length;
        bool first = true;

        foreach (string term in terms)
        {
            int cost = term.Length + (first ? 0 : 2);
            if (used + cost > budget)
            {
                // The budget is enforced here rather than left to the API: whisper-1
                // silently drops everything before its final 224 tokens, which would
                // discard whichever half of the glossary the user cared about.
                break;
            }

            if (!first)
            {
                builder.Append(", ");
            }

            builder.Append(term);
            used += cost;
            first = false;
        }

        return Truncate(builder.ToString(), budget);
    }

    /// <summary>Characters the current glossary and prefix would consume.</summary>
    public static int MeasureLength(string? prefix, IEnumerable<string>? vocabulary, ModelCapabilities model) =>
        Build(prefix, vocabulary, model).Length;

    /// <summary>Splits the settings text box (one term per line) into a clean term list.</summary>
    public static List<string> ParseVocabulary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return Normalize(lines);
    }

    private static List<string> Normalize(IEnumerable<string>? vocabulary)
    {
        var result = new List<string>();
        if (vocabulary is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in vocabulary)
        {
            if (raw is null)
            {
                continue;
            }

            string term = raw.Replace('\r', ' ').Trim().Trim(',');
            if (term.Length == 0 || !seen.Add(term))
            {
                continue;
            }

            result.Add(term);
        }

        return result;
    }

    private static string Truncate(string value, int budget) =>
        value.Length <= budget ? value : value[..budget].TrimEnd();
}
