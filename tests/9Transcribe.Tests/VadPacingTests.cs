using System.Buffers.Binary;
using NineTranscribe.Audio;
using NineTranscribe.Settings;
using Xunit;

namespace NineTranscribe.Tests;

/// <summary>
/// Long speech without a real pause must still be handed over in pieces, or nothing appears
/// on screen until the key is released.
/// </summary>
public sealed class VadPacingTests
{
    private const int FrameBytes = 960;

    // Default settings: 1.2 s hangover. Pacing for "at least every 10 s": a 420 ms pause ends an
    // utterance once it is 4 s long, and 10 s of unbroken speech is cut at its quietest moment.
    private static readonly SegmentPacing TenSeconds = SegmentPacing.ForMaxSeconds(10);

    [Fact]
    public void ForMaxSeconds_DerivesTheSoftCutAndClampsTheRange()
    {
        Assert.Equal(new SegmentPacing(4000, 420, 10000), SegmentPacing.ForMaxSeconds(10));
        Assert.Equal(2500, SegmentPacing.ForMaxSeconds(5).SoftCutAfterMs);
        Assert.Equal(30000, SegmentPacing.ForMaxSeconds(100).MaxUtteranceMs);
        Assert.Equal(5000, SegmentPacing.ForMaxSeconds(1).MaxUtteranceMs);
    }

    [Fact]
    public void LongUtterance_EndsAtAShortPause()
    {
        var monitor = new VadMonitor(new VadSettings(), pacing: TenSeconds);

        monitor.Process(Silence(10));
        Assert.False(monitor.Process(Tone(170, -20)).UtteranceEnded);
        VadFrameResult pause = monitor.Process(Silence(16));

        Assert.True(pause.UtteranceEnded);
        Assert.False(pause.CutForced);

        // A short pause ends a sentence, not the session.
        Assert.False(monitor.SilenceTimeoutReached);
    }

    [Fact]
    public void ShortUtterance_KeepsWaitingForTheFullHangover()
    {
        var monitor = new VadMonitor(new VadSettings(), pacing: TenSeconds);

        monitor.Process(Silence(10));
        monitor.Process(Tone(60, -20));

        // 1.8 s into a sentence, a half-second breath is part of the sentence.
        Assert.False(monitor.Process(Silence(16)).UtteranceEnded);
        Assert.False(monitor.Process(Tone(20, -20)).UtteranceEnded);
    }

    [Fact]
    public void WithoutPacing_AShortPauseNeverEndsAnUtterance()
    {
        var monitor = new VadMonitor(new VadSettings());

        monitor.Process(Silence(10));
        monitor.Process(Tone(170, -20));

        Assert.False(monitor.Process(Silence(16)).UtteranceEnded);
    }

    [Fact]
    public void UnbrokenSpeech_IsCutAtTheQuietestRecentFrame()
    {
        var monitor = new VadMonitor(new VadSettings(), pacing: TenSeconds);

        monitor.Process(Silence(10));
        monitor.Process(Tone(300, -20));
        monitor.Process(Silence(2));
        VadFrameResult result = monitor.Process(Tone(40, -20));

        Assert.True(result.UtteranceEnded);
        Assert.True(result.CutForced);
        Assert.True(result.IsSpeech);

        // The two-frame dip sits at frames 310 and 311; the cut lands in the middle of the later one.
        Assert.Equal((311L * FrameBytes) + (FrameBytes / 2), monitor.UtteranceEndByteOffset);
    }

    [Fact]
    public void ForcedCut_StaysPutUntilTheCallerTakesIt()
    {
        var monitor = new VadMonitor(new VadSettings(), pacing: TenSeconds);

        monitor.Process(Silence(10));
        monitor.Process(Tone(300, -20));
        monitor.Process(Silence(2));
        monitor.Process(Tone(40, -20));
        long cut = monitor.UtteranceEndByteOffset;

        monitor.Process(Tone(30, -20));

        Assert.Equal(cut, monitor.UtteranceEndByteOffset);
    }

    [Fact]
    public void AfterAForcedCut_TheNextUtteranceStartsExactlyAtTheCut()
    {
        var monitor = new VadMonitor(new VadSettings(), pacing: TenSeconds);

        monitor.Process(Silence(10));
        monitor.Process(Tone(300, -20));
        monitor.Process(Silence(2));
        monitor.Process(Tone(40, -20));
        long cut = monitor.UtteranceEndByteOffset;

        // What the recorder does once it has handed the first piece over.
        monitor.BeginUtterance();

        Assert.Equal(cut, monitor.UtteranceStartByteOffset);
        Assert.False(monitor.Process(Tone(10, -20)).UtteranceEnded);

        // The speaker carries on and then stops properly.
        VadFrameResult end = monitor.Process(Silence(45));
        Assert.True(end.UtteranceEnded);
        Assert.False(end.CutForced);
        Assert.Equal(cut, monitor.UtteranceStartByteOffset);
        Assert.True(monitor.UtteranceEndByteOffset > cut);
    }

    private static byte[] Silence(int frames) => new byte[frames * FrameBytes];

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
}
