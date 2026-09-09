using System.Globalization;
using System.Text;

namespace NineTranscribe.Overlay;

/// <summary>
/// Cuts text into the small pieces the overlay fades in one after another.
/// </summary>
/// <remarks>
/// Thai vowels and tone marks are separate code points that sit on the consonant before them,
/// so cutting between arbitrary characters would show a mark before its consonant for a frame.
/// Pieces are therefore built from grapheme clusters, which keep a syllable's marks with its
/// base. A space always ends a piece, so Latin words appear whole.
/// </remarks>
public static class TextReveal
{
    /// <summary>The whole text is on screen within this long, however many pieces it has.</summary>
    public const int MaxRevealMs = 450;

    private const int ClustersPerChunk = 3;

    public static IReadOnlyList<string> Chunks(string text) => Chunks(text, ClustersPerChunk);

    public static IReadOnlyList<string> Chunks(string text, int clustersPerChunk)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (clustersPerChunk < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(clustersPerChunk));
        }

        var chunks = new List<string>();
        var current = new StringBuilder();
        int count = 0;

        TextElementEnumerator clusters = StringInfo.GetTextElementEnumerator(text);
        while (clusters.MoveNext())
        {
            string cluster = clusters.GetTextElement();
            current.Append(cluster);
            count++;

            if (char.IsWhiteSpace(cluster[0]) || count >= clustersPerChunk)
            {
                chunks.Add(current.ToString());
                current.Clear();
                count = 0;
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }
}
