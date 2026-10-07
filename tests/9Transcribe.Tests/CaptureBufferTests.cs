using NineTranscribe.Audio;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class CaptureBufferTests
{
    [Fact]
    public void Slice_ReturnsTheBytesAtTheirAbsoluteOffsets()
    {
        var buffer = new CaptureBuffer(1024);
        buffer.Append(Pattern(0, 5000));

        Assert.Equal(5000, buffer.End);
        Assert.Equal(Pattern(1200, 300), buffer.Slice(1200, 1500).ToArray());
    }

    [Fact]
    public void DiscardBefore_KeepsAbsoluteOffsetsValidAfterCompacting()
    {
        var buffer = new CaptureBuffer(1024);
        buffer.Append(Pattern(0, 10_000));

        buffer.DiscardBefore(9_000);
        buffer.Append(Pattern(10_000, 500));

        Assert.Equal(9_000, buffer.Origin);
        Assert.Equal(10_500, buffer.End);
        Assert.Equal(1_500, buffer.HeldBytes);
        Assert.Equal(Pattern(9_500, 1_000), buffer.Slice(9_500, 10_500).ToArray());
    }

    [Fact]
    public void DiscardBefore_ASmallPrefix_WaitsUntilTheCopyIsWorthIt()
    {
        var buffer = new CaptureBuffer(1024);
        buffer.Append(Pattern(0, 10_000));

        buffer.DiscardBefore(100);

        Assert.Equal(0, buffer.Origin);
        Assert.Equal(Pattern(100, 50), buffer.Slice(100, 150).ToArray());
    }

    [Fact]
    public void Slice_ClampsToWhatIsStillHeld()
    {
        var buffer = new CaptureBuffer(1024);
        buffer.Append(Pattern(0, 4_000));
        buffer.DiscardBefore(3_000);

        Assert.Equal(Pattern(3_000, 1_000), buffer.Slice(0, 99_999).ToArray());
        Assert.Equal(0, buffer.Slice(5_000, 6_000).Length);
    }

    [Fact]
    public void DiscardBefore_AfterABurst_GivesTheMemoryBack()
    {
        var buffer = new CaptureBuffer(64 * 1024);
        buffer.Append(new byte[4 * 1024 * 1024]);

        buffer.DiscardBefore(buffer.End - 1_000);

        Assert.Equal(64 * 1024, buffer.Capacity);
        Assert.Equal(1_000, buffer.HeldBytes);
    }

    [Fact]
    public void LongDictation_HoldsAboutOneSentence()
    {
        // Ten minutes of 30 ms frames, handed over every 10 s as a segmenting session would.
        var buffer = new CaptureBuffer(512 * 1024);
        byte[] frame = new byte[960];
        int maxHeld = 0;

        for (int i = 1; i <= 20_000; i++)
        {
            buffer.Append(frame);
            if (i % 333 == 0)
            {
                buffer.DiscardBefore(buffer.End - 9_600);
            }

            maxHeld = Math.Max(maxHeld, buffer.HeldBytes);
        }

        Assert.Equal(20_000L * 960, buffer.End);
        Assert.True(maxHeld < 2 * 1024 * 1024, $"held {maxHeld} bytes");
        Assert.True(buffer.Capacity <= 2 * 1024 * 1024, $"capacity {buffer.Capacity} bytes");
    }

    private static byte[] Pattern(long offset, int count)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)((offset + i) % 251);
        }

        return bytes;
    }
}
