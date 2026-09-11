using NineTranscribe.Overlay;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class TextRevealTests
{
    [Fact]
    public void Chunks_NeverSplitsAThaiSyllableFromItsMarks()
    {
        // Three syllables, each a consonant plus an upper vowel and a tone mark stacked on it.
        IReadOnlyList<string> chunks = TextReveal.Chunks("ที่ปู่กิ์", clustersPerChunk: 1);

        Assert.Equal(new[] { "ที่", "ปู่", "กิ์" }, chunks);
    }

    [Fact]
    public void Chunks_GroupsClustersAndEndsAPieceAtASpace()
    {
        IReadOnlyList<string> chunks = TextReveal.Chunks("สวัสดี Power BI", clustersPerChunk: 3);

        // Rejoined, nothing is lost or reordered.
        Assert.Equal("สวัสดี Power BI", string.Concat(chunks));

        // Every piece that contains a space ends with it, so a Latin word is never cut in half
        // by a stagger boundary landing mid-word.
        foreach (string chunk in chunks)
        {
            if (chunk.Contains(' '))
            {
                Assert.EndsWith(" ", chunk);
            }
        }
    }

    [Fact]
    public void Chunks_EmptyText_HasNoPieces()
    {
        Assert.Empty(TextReveal.Chunks(string.Empty));
    }
}
