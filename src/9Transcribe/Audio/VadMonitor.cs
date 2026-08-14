using System.Runtime.InteropServices;
using NineTranscribe.Settings;

namespace NineTranscribe.Audio;

/// <summary>What the detector concluded about the most recent 30 ms frame in a chunk.</summary>
/// <param name="RmsDbfs">DC-corrected RMS level of the frame, floored at -90.</param>
/// <param name="PeakDbfs">Highest absolute sample of the frame, floored at -90.</param>
/// <param name="IsSpeech">Whether the detector is currently inside an utterance.</param>
/// <param name="SilenceTimeoutReached">Whether the hangover has elapsed since the last speech.</param>
public readonly record struct VadFrameResult(
    double RmsDbfs,
    double PeakDbfs,
    bool IsSpeech,
    bool SilenceTimeoutReached);

/// <summary>
/// Adaptive RMS voice-activity detector. Pure arithmetic over PCM bytes with no audio APIs,
/// so it can be driven from a unit test with a synthesized buffer. One instance belongs to
/// one capture session and is not thread-safe: <see cref="AudioRecorder"/> only ever touches
/// it while holding its state lock.
/// </summary>
public sealed class VadMonitor
{
    /// <summary>Analysis window. Matches the recorder's 30 ms WinMM buffers, so a buffer is one frame.</summary>
    public const int FrameMs = 30;

    private const double MinDbfs = -90.0;
    private const double FullScale = 32768.0;

    /// <summary>Frames above the enter threshold before speech is declared: rejects keyboard clicks.</summary>
    private const int SpeechDebounceFrames = 3;

    private const double AdaptiveMarginDb = 12.0;

    // The floor starts pessimistically low so that, in a room noisier than the estimate, the
    // adaptive threshold simply degrades to the configured fixed one instead of going deaf.
    private const double SeedNoiseFloorDbfs = -60.0;
    private const double FloorMinDbfs = -90.0;
    private const double FloorMaxDbfs = -40.0;

    // Asymmetric EMA: drop to a quieter measurement almost at once, rise slowly, and while a
    // frame is loud enough to be speech barely move at all — otherwise a long sentence walks
    // the floor up until the speaker's own voice falls below the threshold.
    private const double FallAlpha = 0.3;
    private const double RiseAlpha = 0.02;
    private const double SpeechAlpha = 0.0005;

    private readonly double _enterDbfs;
    private readonly double _exitOffsetDb;
    private readonly bool _adaptive;
    private readonly int _hangoverFrames;
    private readonly int _frameBytes;
    private readonly byte[] _partialFrame;

    private int _partialCount;
    private long _framesProcessed;
    private int _aboveEnterFrames;
    private int _silenceFrames;
    private bool _inSpeech;
    private double _noiseFloorDbfs = SeedNoiseFloorDbfs;
    private double _peakDbfs = MinDbfs;

    public VadMonitor(VadSettings settings, int sampleRate = 16000)
    {
        ArgumentNullException.ThrowIfNull(settings);

        VadSettings snapshot = settings.Clone();
        snapshot.Normalize();
        _enterDbfs = snapshot.EnterDbfs;
        _exitOffsetDb = snapshot.ExitOffsetDb;
        _adaptive = snapshot.Adaptive;
        _hangoverFrames = Math.Max(1, snapshot.HangoverMs / FrameMs);

        int rate = Math.Clamp(sampleRate, 8000, 48000);
        _frameBytes = rate / 1000 * FrameMs * WavUtil.BlockAlign;
        _partialFrame = new byte[_frameBytes];
    }

    /// <summary>Latches once an utterance has been confirmed; the caller skips the API when it is false.</summary>
    public bool HasSpeech { get; private set; }

    public double CurrentRmsDbfs { get; private set; } = MinDbfs;

    public double NoiseFloorDbfs => _noiseFloorDbfs;

    public double EnterThresholdDbfs => _adaptive
        ? Math.Max(_enterDbfs, _noiseFloorDbfs + AdaptiveMarginDb)
        : _enterDbfs;

    /// <summary>Offset of the first frame of the first confirmed utterance; -1 until speech starts.</summary>
    public long FirstSpeechByteOffset { get; private set; } = -1;

    /// <summary>Offset just past the last frame that stayed above the exit threshold; -1 until speech starts.</summary>
    public long LastSpeechByteOffset { get; private set; } = -1;

    /// <summary>Latches once the hangover has elapsed after an utterance; cleared only by <see cref="Reset"/>.</summary>
    public bool SilenceTimeoutReached { get; private set; }

    /// <summary>
    /// Feeds one capture buffer. Bytes that do not fill a whole frame are carried over to the
    /// next call, so the byte offsets stay aligned to the stream the caller is accumulating.
    /// </summary>
    public VadFrameResult Process(ReadOnlySpan<byte> pcm16)
    {
        int consumed = 0;
        while (consumed < pcm16.Length)
        {
            int take = Math.Min(_frameBytes - _partialCount, pcm16.Length - consumed);
            pcm16.Slice(consumed, take).CopyTo(_partialFrame.AsSpan(_partialCount));
            _partialCount += take;
            consumed += take;

            if (_partialCount == _frameBytes)
            {
                ProcessFrame(_partialFrame);
                _partialCount = 0;
            }
        }

        return new VadFrameResult(CurrentRmsDbfs, _peakDbfs, _inSpeech, SilenceTimeoutReached);
    }

    public void Reset()
    {
        _partialCount = 0;
        _framesProcessed = 0;
        _aboveEnterFrames = 0;
        _silenceFrames = 0;
        _inSpeech = false;
        _noiseFloorDbfs = SeedNoiseFloorDbfs;
        _peakDbfs = MinDbfs;
        CurrentRmsDbfs = MinDbfs;
        HasSpeech = false;
        SilenceTimeoutReached = false;
        FirstSpeechByteOffset = -1;
        LastSpeechByteOffset = -1;
    }

    private void ProcessFrame(ReadOnlySpan<byte> frame)
    {
        long frameStart = _framesProcessed * _frameBytes;
        _framesProcessed++;

        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(frame);
        Measure(samples, out double rmsDbfs, out double peakDbfs);
        CurrentRmsDbfs = rmsDbfs;
        _peakDbfs = peakDbfs;

        UpdateNoiseFloor(rmsDbfs);

        double enter = EnterThresholdDbfs;
        double exit = enter - _exitOffsetDb;

        if (rmsDbfs >= enter)
        {
            _aboveEnterFrames++;
            if (!_inSpeech && _aboveEnterFrames >= SpeechDebounceFrames)
            {
                _inSpeech = true;
                HasSpeech = true;
                if (FirstSpeechByteOffset < 0)
                {
                    long onset = frameStart - ((SpeechDebounceFrames - 1) * (long)_frameBytes);
                    FirstSpeechByteOffset = Math.Max(0, onset);
                }
            }
        }
        else
        {
            _aboveEnterFrames = 0;
        }

        if (!_inSpeech)
        {
            return;
        }

        if (rmsDbfs >= exit)
        {
            _silenceFrames = 0;
            LastSpeechByteOffset = frameStart + _frameBytes;
            return;
        }

        _silenceFrames++;
        if (_silenceFrames >= _hangoverFrames)
        {
            _inSpeech = false;
            _silenceFrames = 0;
            SilenceTimeoutReached = true;
        }
    }

    private void UpdateNoiseFloor(double rmsDbfs)
    {
        double alpha;
        if (rmsDbfs < _noiseFloorDbfs)
        {
            alpha = FallAlpha;
        }
        else if (!_inSpeech && rmsDbfs < EnterThresholdDbfs)
        {
            alpha = RiseAlpha;
        }
        else
        {
            alpha = SpeechAlpha;
        }

        double next = _noiseFloorDbfs + (alpha * (rmsDbfs - _noiseFloorDbfs));
        _noiseFloorDbfs = Math.Clamp(next, FloorMinDbfs, FloorMaxDbfs);
    }

    /// <summary>
    /// Removes the frame's DC offset before measuring: some USB and laptop capture paths carry
    /// a constant bias that is inaudible but would otherwise read as a steady speech-level RMS.
    /// </summary>
    private static void Measure(ReadOnlySpan<short> samples, out double rmsDbfs, out double peakDbfs)
    {
        if (samples.Length == 0)
        {
            rmsDbfs = MinDbfs;
            peakDbfs = MinDbfs;
            return;
        }

        long sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            sum += samples[i];
        }

        double mean = (double)sum / samples.Length;
        double sumSquares = 0.0;
        double peak = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            double value = samples[i] - mean;
            sumSquares += value * value;
            double magnitude = Math.Abs(value);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        rmsDbfs = ToDbfs(Math.Sqrt(sumSquares / samples.Length));
        peakDbfs = ToDbfs(peak);
    }

    private static double ToDbfs(double amplitude)
    {
        if (amplitude <= 0.0)
        {
            return MinDbfs;
        }

        return Math.Max(MinDbfs, 20.0 * Math.Log10(amplitude / FullScale));
    }
}
