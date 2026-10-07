using System.Globalization;
using NineTranscribe.Overlay;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class RollingTranscriptTests
{
    [Fact]
    public void Pieces_SplitIntoOlderAndNewest()
    {
        var transcript = new RollingTranscript();
        transcript.Append(string.Empty, "สวัสดีครับ");
        transcript.Append(" ", "วันนี้เรียน");
        transcript.Append(string.Empty, "Power BI");

        Assert.Equal("สวัสดีครับ วันนี้เรียน", transcript.Older);
        Assert.Equal("Power BI", transcript.Newest);
        Assert.Equal("สวัสดีครับ วันนี้เรียนPower BI", transcript.Text);
        Assert.False(transcript.Truncated);
    }

    [Fact]
    public void OldPieces_ScrollAwayBehindAnEllipsis()
    {
        var transcript = new RollingTranscript(maxChars: 30);
        transcript.Append(string.Empty, "one two three four");
        transcript.Append(" ", "five six seven");
        transcript.Append(" ", "eight nine");

        Assert.True(transcript.Truncated);
        Assert.StartsWith("… ", transcript.Older);
        Assert.DoesNotContain("one", transcript.Text);
        Assert.Equal(" eight nine", transcript.Newest);
    }

    [Fact]
    public void OneLongPiece_KeepsItsEnd()
    {
        var transcript = new RollingTranscript(maxChars: 40);
        string thai = string.Concat(Enumerable.Repeat("ที่ปู่กิ์", 20));

        transcript.Append(string.Empty, thai);

        Assert.True(transcript.Truncated);
        Assert.EndsWith("ที่ปู่กิ์", transcript.Newest);
        Assert.True(transcript.Newest.Length <= 40);

        // Never starts on a vowel or tone mark separated from its consonant.
        Assert.NotEqual(UnicodeCategory.NonSpacingMark, CharUnicodeInfo.GetUnicodeCategory(transcript.Newest[0]));
    }

    [Fact]
    public void Clear_StartsOver()
    {
        var transcript = new RollingTranscript();
        transcript.Append(string.Empty, "text");
        transcript.Clear();

        Assert.False(transcript.HasText);
        Assert.Equal(string.Empty, transcript.Text);
    }

    [Fact]
    public void BlankPieces_AreIgnored()
    {
        var transcript = new RollingTranscript();
        transcript.Append(" ", "   ");

        Assert.False(transcript.HasText);
    }
}
