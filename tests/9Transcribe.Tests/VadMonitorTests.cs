using System.Buffers.Binary;
using NineTranscribe.Audio;
using NineTranscribe.Settings;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class VadMonitorTests
{
    private const int FrameBytes = 960;

    [Fact]
    public void Process_AfterSpeechStops_ReachesSilenceTimeoutAtTheHangover()
    {
        var monitor = new VadMonitor(new VadSettings { HangoverMs = 600 });

        monitor.Process(Silence(10));
        monitor.Process(Tone(20, -20));

        Assert.True(monitor.HasSpeech);
        Assert.False(monitor.SilenceTimeoutReached);

        monitor.Process(Silence(10));

        Assert.False(monitor.SilenceTimeoutReached);

        monitor.Process(Silence(15));

        Assert.True(monitor.SilenceTimeoutReached);
    }

    [Fact]
    public void Process_SingleLoudFrame_DoesNotCountAsSpeech()
    {
        var monitor = new VadMonitor(new VadSettings());

        monitor.Process(Silence(10));
        monitor.Process(Tone(1, -6));
        monitor.Process(Silence(10));

        Assert.False(monitor.HasSpeech);
        Assert.Equal(-1L, monitor.FirstSpeechByteOffset);
    }

    [Fact]
    public void Process_ConstantDcOffset_IsNotSpeech()
    {
        var monitor = new VadMonitor(new VadSettings());

        monitor.Process(Constant(50, 8000));

        Assert.False(monitor.HasSpeech);
        Assert.Equal(-90.0, monitor.CurrentRmsDbfs, 3);
    }

    [Fact]
    public void Process_ByteOffsets_BracketTheLoudRegion()
    {
        var monitor = new VadMonitor(new VadSettings());

        monitor.Process(Concat(Silence(30), Tone(30, -20), Silence(30)));

        Assert.Equal(30L * FrameBytes, monitor.FirstSpeechByteOffset);
        Assert.Equal(60L * FrameBytes, monitor.LastSpeechByteOffset);
    }

    [Fact]
    public void Process_SplitAcrossUnalignedChunks_KeepsTheSameOffsets()
    {
        byte[] signal = Concat(Silence(30), Tone(30, -20), Silence(30));
        var whole = new VadMonitor(new VadSettings());
        var chunked = new VadMonitor(new VadSettings());

        whole.Process(signal);
        for (int offset = 0; offset < signal.Length; offset += 137)
        {
            chunked.Process(signal.AsSpan(offset, Math.Min(137, signal.Length - offset)));
        }

        Assert.Equal(whole.FirstSpeechByteOffset, chunked.FirstSpeechByteOffset);
        Assert.Equal(whole.LastSpeechByteOffset, chunked.LastSpeechByteOffset);
        Assert.Equal(whole.HasSpeech, chunked.HasSpeech);
    }

    [Fact]
    public void Reset_ClearsEverySpeechFlag()
    {
        var monitor = new VadMonitor(new VadSettings());

        monitor.Process(Concat(Silence(10), Tone(60, -20), Silence(60)));

        Assert.True(monitor.HasSpeech);
        Assert.True(monitor.SilenceTimeoutReached);

        monitor.Reset();

        Assert.False(monitor.HasSpeech);
        Assert.False(monitor.SilenceTimeoutReached);
        Assert.Equal(-1L, monitor.FirstSpeechByteOffset);
        Assert.Equal(-1L, monitor.LastSpeechByteOffset);
        Assert.Equal(-90.0, monitor.CurrentRmsDbfs, 3);
    }

    [Fact]
    public void EnterThresholdDbfs_TracksTheNoiseFloorWhenAdaptive()
    {
        var monitor = new VadMonitor(new VadSettings { EnterDbfs = -35.0, Adaptive = true });

        monitor.Process(Noise(200, -45.0, seed: 1234));

        Assert.False(monitor.HasSpeech);
        Assert.InRange(monitor.NoiseFloorDbfs, -49.0, -41.0);
        Assert.InRange(monitor.EnterThresholdDbfs, -35.0, -29.0);
    }

    [Fact]
    public void EnterThresholdDbfs_WithAdaptiveOff_IsTheConfiguredValue()
    {
        var monitor = new VadMonitor(new VadSettings { EnterDbfs = -30.0, Adaptive = false });

        monitor.Process(Noise(100, -40.0, seed: 7));

        Assert.Equal(-30.0, monitor.EnterThresholdDbfs, 6);
    }

    private static byte[] Silence(int frames) => new byte[frames * FrameBytes];

    private static byte[] Constant(int frames, short value)
    {
        byte[] pcm = new byte[frames * FrameBytes];
        for (int i = 0; i < pcm.Length / 2; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), value);
        }

        return pcm;
    }

    /// <summary>A 440 Hz sine whose RMS lands on <paramref name="dbfs"/> (hence the sqrt(2)).</summary>
    private static byte[] Tone(int frames, double dbfs)
    {
        byte[] pcm = new byte[frames * FrameBytes];
        double amplitude = Math.Min(32767.0, 32768.0 * Math.Pow(10.0, dbfs / 20.0) * Math.Sqrt(2.0));
        for (int i = 0; i < pcm.Length / 2; i++)
        {
            double value = amplitude * Math.Sin(2.0 * Math.PI * 440.0 * i / 16000.0);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), (short)Math.Round(value));
        }

        return pcm;
    }

    /// <summary>Uniform noise scaled so its RMS is <paramref name="dbfs"/> (hence the sqrt(3)).</summary>
    private static byte[] Noise(int frames, double dbfs, int seed)
    {
        var random = new Random(seed);
        byte[] pcm = new byte[frames * FrameBytes];
        double bound = 32768.0 * Math.Pow(10.0, dbfs / 20.0) * Math.Sqrt(3.0);
        for (int i = 0; i < pcm.Length / 2; i++)
        {
            double value = ((random.NextDouble() * 2.0) - 1.0) * bound;
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(i * 2, 2),
                (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
        }

        return pcm;
    }

    [Fact]
    public void Process_TwoSentences_ReportsEachEndingSeparately()
    {
        var monitor = new VadMonitor(new VadSettings { HangoverMs = 300 });

        monitor.Process(Silence(10));
        monitor.Process(Tone(20, -20));
        VadFrameResult first = monitor.Process(Silence(20));

        Assert.True(first.UtteranceEnded);
        long firstStart = monitor.UtteranceStartByteOffset;
        long firstEnd = monitor.UtteranceEndByteOffset;
        Assert.True(firstEnd > firstStart);

        // What the recorder does once it has cut the first sentence loose.
        monitor.BeginUtterance();
        Assert.False(monitor.UtteranceStartByteOffset >= 0);

        monitor.Process(Tone(20, -20));
        VadFrameResult second = monitor.Process(Silence(20));

        Assert.True(second.UtteranceEnded);

        // The second sentence must live entirely after the first, or the recorder would either
        // send the same audio twice or lose the gap between them.
        Assert.True(monitor.UtteranceStartByteOffset >= firstEnd);
        Assert.True(monitor.UtteranceEndByteOffset > monitor.UtteranceStartByteOffset);
    }

    [Fact]
    public void Process_UtteranceEnded_IsAnEdgeNotALatch()
    {
        var monitor = new VadMonitor(new VadSettings { HangoverMs = 300 });

        monitor.Process(Silence(10));
        monitor.Process(Tone(20, -20));

        Assert.True(monitor.Process(Silence(20)).UtteranceEnded);
        Assert.False(monitor.Process(Silence(20)).UtteranceEnded);
    }

    [Fact]
    public void BeginUtterance_KeepsTheLearnedNoiseFloorAndOffsetAlignment()
    {
        var monitor = new VadMonitor(new VadSettings { HangoverMs = 300 });

        monitor.Process(Tone(40, -45));
        double floorBefore = monitor.NoiseFloorDbfs;
        monitor.Process(Tone(20, -20));
        monitor.Process(Silence(20));

        long consumed = monitor.UtteranceEndByteOffset;
        monitor.BeginUtterance();

        Assert.Equal(floorBefore, monitor.NoiseFloorDbfs, 3);

        monitor.Process(Tone(20, -20));
        monitor.Process(Silence(20));

        // Offsets stay absolute, so they still line up with the recorder's capture buffer.
        Assert.True(monitor.UtteranceStartByteOffset >= consumed);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(part => part.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }
}
