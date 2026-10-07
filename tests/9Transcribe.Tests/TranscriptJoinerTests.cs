using NineTranscribe.Processing;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class TranscriptJoinerTests
{
    [Fact]
    public void ThaiCutMidPhrase_IsJoinedWithoutASpace()
    {
        Assert.Equal(string.Empty, TranscriptJoiner.Separator(joinsPrevious: true, 'น', "วันนี้"));
    }

    [Fact]
    public void ThaiAfterAPause_GetsTheSentenceSpace()
    {
        Assert.Equal(" ", TranscriptJoiner.Separator(joinsPrevious: false, 'น', "วันนี้"));
    }

    [Theory]
    [InlineData('I', "เป็น")]
    [InlineData('น', "Power")]
    [InlineData('s', "data")]
    public void LatinOnEitherSide_KeepsItsSpaceEvenMidPhrase(char last, string next)
    {
        Assert.Equal(" ", TranscriptJoiner.Separator(joinsPrevious: true, last, next));
    }

    [Fact]
    public void ExistingWhitespace_IsNotDoubled()
    {
        Assert.Equal(string.Empty, TranscriptJoiner.Separator(false, ' ', "next"));
        Assert.Equal(string.Empty, TranscriptJoiner.Separator(false, 'a', " next"));
    }

    [Fact]
    public void NothingTypedYet_NeedsNoSeparator()
    {
        Assert.Equal(string.Empty, TranscriptJoiner.Separator(false, null, "first"));
        Assert.Equal(string.Empty, TranscriptJoiner.Separator(false, 'a', string.Empty));
    }
}
