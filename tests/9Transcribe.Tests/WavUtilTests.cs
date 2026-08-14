using System.Buffers.Binary;
using System.Text;
using NineTranscribe.Audio;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class WavUtilTests
{
    [Fact]
    public void CreateWav_WritesTheCanonicalRiffHeader()
    {
        byte[] pcm = new byte[1000];

        byte[] wav = WavUtil.CreateWav(pcm);

        Assert.Equal("RIFF", Tag(wav, 0));
        Assert.Equal("WAVE", Tag(wav, 8));
        Assert.Equal("fmt ", Tag(wav, 12));
        Assert.Equal("data", Tag(wav, 36));

        Assert.Equal(36 + pcm.Length, ReadInt32(wav, 4));
        Assert.Equal(16, ReadInt32(wav, 16));
        Assert.Equal(1, ReadInt16(wav, 20));
        Assert.Equal(1, ReadInt16(wav, 22));
        Assert.Equal(16000, ReadInt32(wav, 24));
        Assert.Equal(32000, ReadInt32(wav, 28));
        Assert.Equal(2, ReadInt16(wav, 32));
        Assert.Equal(16, ReadInt16(wav, 34));
        Assert.Equal(pcm.Length, ReadInt32(wav, 40));
    }

    [Fact]
    public void CreateWav_LengthIsHeaderPlusPayload()
    {
        Assert.Equal(44, WavUtil.CreateWav(ReadOnlySpan<byte>.Empty).Length);
        Assert.Equal(44 + 6400, WavUtil.CreateWav(new byte[6400]).Length);
        Assert.Equal(0, ReadInt32(WavUtil.CreateWav(ReadOnlySpan<byte>.Empty), 40));
    }

    [Fact]
    public void CreateWav_CopiesThePayloadVerbatim()
    {
        byte[] pcm = { 1, 2, 3, 4, 250, 251 };

        byte[] wav = WavUtil.CreateWav(pcm);

        Assert.Equal(pcm, wav.AsSpan(44).ToArray());
    }

    [Fact]
    public void CreateSilence_HasTheRequestedDurationAndIsAllZero()
    {
        byte[] wav = WavUtil.CreateSilence(TimeSpan.FromSeconds(0.5));

        Assert.Equal(44 + 16000, wav.Length);
        Assert.Equal(16000, ReadInt32(wav, 40));
        Assert.All(wav.Skip(44), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void CreateSilence_ClampsANegativeDurationToNothing()
    {
        byte[] wav = WavUtil.CreateSilence(TimeSpan.FromSeconds(-5));

        Assert.Equal(44, wav.Length);
        Assert.Equal(0, ReadInt32(wav, 40));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-10, 0)]
    [InlineData(200, 6400)]
    [InlineData(300, 9600)]
    [InlineData(1000, 32000)]
    public void BytesForMilliseconds_MatchesTheCaptureFormat(int milliseconds, int expected)
    {
        Assert.Equal(expected, WavUtil.BytesForMilliseconds(milliseconds));
    }

    private static string Tag(byte[] wav, int offset) => Encoding.ASCII.GetString(wav, offset, 4);

    private static int ReadInt32(byte[] wav, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset, 4));

    private static short ReadInt16(byte[] wav, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(offset, 2));
}
