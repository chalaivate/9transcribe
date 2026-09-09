using NineTranscribe.Overlay;
using NineTranscribe.Settings;
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

    [Theory]
    [InlineData(OverlayPosition.Top, 0, -1)]
    [InlineData(OverlayPosition.Bottom, 0, 1)]
    [InlineData(OverlayPosition.Left, -1, 0)]
    [InlineData(OverlayPosition.Right, 1, 0)]
    [InlineData(OverlayPosition.TopRight, 1, -1)]
    [InlineData(OverlayPosition.BottomLeft, -1, 1)]
    public void EntranceOffset_PointsTowardsTheAnchoredEdge(OverlayPosition anchor, int signX, int signY)
    {
        (double dx, double dy) = OverlayPositioner.EntranceOffset(anchor, 12);

        Assert.Equal(signX, Math.Sign(dx));
        Assert.Equal(signY, Math.Sign(dy));
    }
}
