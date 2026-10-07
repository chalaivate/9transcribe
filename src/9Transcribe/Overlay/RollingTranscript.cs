using System.Globalization;
using System.Text;

namespace NineTranscribe.Overlay;

/// <summary>
/// The text of the current dictation as it is shown above the guide line: pieces arrive one at
/// a time, the newest is the one that animates in, and once the whole would no longer fit the
/// oldest pieces scroll away behind an ellipsis — like live captions.
/// </summary>
public sealed class RollingTranscript
{
    public const int DefaultMaxChars = 180;
    private const string Ellipsis = "… ";

    private readonly List<string> _pieces = new();
    private readonly int _maxChars;

    public RollingTranscript(int maxChars = DefaultMaxChars)
    {
        _maxChars = Math.Max(20, maxChars);
    }

    public bool HasText => _pieces.Count > 0;

    /// <summary>Whether earlier text has scrolled away.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Everything before the newest piece, drawn dimmed.</summary>
    public string Older
    {
        get
        {
            if (_pieces.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            if (Truncated)
            {
                builder.Append(Ellipsis);
            }

            for (int i = 0; i < _pieces.Count - 1; i++)
            {
                builder.Append(i == 0 && Truncated ? _pieces[i].TrimStart() : _pieces[i]);
            }

            return builder.ToString();
        }
    }

    /// <summary>The newest piece, including the separator that joins it to the rest.</summary>
    public string Newest
    {
        get
        {
            if (_pieces.Count == 0)
            {
                return string.Empty;
            }

            string newest = _pieces[^1];
            return _pieces.Count == 1 && Truncated ? newest.TrimStart() : newest;
        }
    }

    public string Text => Older + Newest;

    public void Clear()
    {
        _pieces.Clear();
        Truncated = false;
    }

    /// <param name="separator">What was typed between the previous piece and this one.</param>
    public void Append(string separator, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        _pieces.Add(_pieces.Count == 0 ? text : separator + text);

        while (_pieces.Count > 1 && TotalLength() > _maxChars)
        {
            _pieces.RemoveAt(0);
            Truncated = true;
        }

        // One piece on its own can still be too long: keep its end, which is what was just said.
        if (_pieces.Count == 1 && _pieces[0].Length > _maxChars)
        {
            _pieces[0] = Tail(_pieces[0], _maxChars - Ellipsis.Length);
            Truncated = true;
        }
    }

    private int TotalLength()
    {
        int total = 0;
        foreach (string piece in _pieces)
        {
            total += piece.Length;
        }

        return total;
    }

    /// <summary>
    /// The last <paramref name="maxChars"/> or so characters, started at a space when there is one
    /// close by and never inside a grapheme cluster, so no Thai vowel or tone mark is orphaned.
    /// </summary>
    internal static string Tail(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        int cut = text.Length - maxChars;

        int space = text.IndexOf(' ', cut);
        if (space >= 0 && space - cut <= 30 && space < text.Length - 1)
        {
            return text[(space + 1)..];
        }

        int boundary = 0;
        while (boundary < cut)
        {
            boundary += Math.Max(1, StringInfo.GetNextTextElementLength(text, boundary));
        }

        return boundary < text.Length ? text[boundary..] : text[cut..];
    }
}
