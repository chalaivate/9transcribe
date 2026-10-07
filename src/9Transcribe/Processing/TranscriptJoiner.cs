namespace NineTranscribe.Processing;

/// <summary>
/// Decides what goes between two pieces of the same dictation when they are typed one after
/// the other.
/// </summary>
public static class TranscriptJoiner
{
    /// <summary>
    /// A space between sentences, as the user asked for, except where the recorder had to cut a
    /// long stretch of Thai speech mid-phrase: Thai writes the words of a phrase together, so a
    /// space there would split the phrase. Latin text keeps its space either way.
    /// </summary>
    /// <param name="joinsPrevious">The boundary is a cut made mid-speech, not a pause.</param>
    /// <param name="previousLast">The last character already typed for this dictation.</param>
    /// <param name="next">The text about to be typed.</param>
    public static string Separator(bool joinsPrevious, char? previousLast, string next)
    {
        if (previousLast is not { } last || string.IsNullOrEmpty(next))
        {
            return string.Empty;
        }

        if (char.IsWhiteSpace(last) || char.IsWhiteSpace(next[0]))
        {
            return string.Empty;
        }

        if (joinsPrevious && IsThai(last) && IsThai(next[0]))
        {
            return string.Empty;
        }

        return " ";
    }

    public static bool IsThai(char c) => c is >= '฀' and <= '๿';
}
