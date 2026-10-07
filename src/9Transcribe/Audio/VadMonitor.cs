using System.Runtime.InteropServices;
using NineTranscribe.Settings;

namespace NineTranscribe.Audio;

/// <summary>What the detector concluded about the most recent 30 ms frame in a chunk.</summary>
/// <param name="RmsDbfs">DC-corrected RMS level of the frame, floored at -90.</param>
/// <param name="PeakDbfs">Highest absolute sample of the frame, floored at -90.</param>
/// <param name="IsSpeech">Whether the detector is currently inside an utterance.</param>
/// <param name="SilenceTimeoutReached">Whether the hangover has elapsed since the last speech.</param>
/// <param name="UtteranceEnded">
/// True on the single frame where an utterance just ended. Unlike the latching
/// <paramref name="SilenceTimeoutReached"/>, this is an edge, so a caller can act once per
/// sentence and keep recording through the pauses.
/// </param>
/// <param name="CutForced">
/// The utterance was cut at its quietest recent moment because it ran too long without a
/// pause, not because the speaker stopped. Speech carries on past the cut.
/// </param>
public readonly record struct VadFrameResult(
    double RmsDbfs,
    double PeakDbfs,
    bool IsSpeech,
    bool SilenceTimeoutReached,
    bool UtteranceEnded,
    bool CutForced = false);

/// <summary>
/// How a segmenting session breaks up long stretches of speech, so text keeps appearing while
/// somebody talks without stopping. Thai speakers pause for 0.3–0.8 s between phrases, well
/// under the hangover that ends a sentence, so without this nothing is cut until the key is
/// released.
/// </summary>
/// <param name="SoftCutAfterMs">Once an utterance is this long, a short pause is enough to end it.</param>
/// <param name="SoftPauseMs">The short pause that ends a long utterance.</param>
/// <param name="MaxUtteranceMs">Past this, the utterance is cut at its quietest recent moment.</param>
public sealed record SegmentPacing(int SoftCutAfterMs, int SoftPauseMs, int MaxUtteranceMs)
{
    public const int DefaultSoftPauseMs = 420;

    /// <summary>The pacing for a user-facing "send at least every N seconds" setting.</summary>
    public static SegmentPacing ForMaxSeconds(int maxSeconds)
    {
        int max = Math.Clamp(maxSeconds, 5, 30) * 1000;
        int soft = Math.Max(2500, max * 2 / 5);
        return new SegmentPacing(soft, DefaultSoftPauseMs, max);
    }
}

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

    /// <summary>How far back a forced cut looks for a quiet moment: 1.2 s.</summary>
    private const int CutSearchFrames = 40;

    /// <summary>A forced cut never leaves a piece shorter than this: 1 s.</summary>
    private const int MinFramesBeforeCut = 33;

    private const int RmsHistoryFrames = 64;

    private readonly double _enterDbfs;
    private readonly double _exitOffsetDb;
    private readonly bool _adaptive;
    private readonly int _hangoverFrames;
    private readonly int _frameBytes;
    private readonly byte[] _partialFrame;

    private int _partialCount;
    private long _framesProcessed;
    private bool _utteranceEnded;
    private int _aboveEnterFrames;
    private int _silenceFrames;
    private bool _inSpeech;
    private double _noiseFloorDbfs = SeedNoiseFloorDbfs;
    private double _peakDbfs = MinDbfs;

    private readonly bool _paced;
    private readonly int _softAfterFrames;
    private readonly int _softPauseFrames;
    private readonly int _maxFrames;
    private readonly double[] _rmsHistory = new double[RmsHistoryFrames];
    private long _utteranceStartFrame = -1;
    private bool _cutForced;
    private long _pendingCutOffset = -1;
    private long _pendingCutFrame = -1;

    public VadMonitor(VadSettings settings, int sampleRate = 16000, SegmentPacing? pacing = null)
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

        if (pacing is not null)
        {
            _paced = true;
            _maxFrames = Math.Max(MinFramesBeforeCut + CutSearchFrames, pacing.MaxUtteranceMs / FrameMs);
            _softAfterFrames = Math.Clamp(pacing.SoftCutAfterMs / FrameMs, 1, _maxFrames);
            _softPauseFrames = Math.Clamp((pacing.SoftPauseMs + FrameMs - 1) / FrameMs, 1, _hangoverFrames);
        }
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

    /// <summary>Offset of the first frame of the utterance in progress; -1 between utterances.</summary>
    public long UtteranceStartByteOffset { get; private set; } = -1;

    /// <summary>Offset just past the last speech frame of the utterance in progress; -1 between them.</summary>
    public long UtteranceEndByteOffset { get; private set; } = -1;

    /// <summary>
    /// Feeds one capture buffer. Bytes that do not fill a whole frame are carried over to the
    /// next call, so the byte offsets stay aligned to the stream the caller is accumulating.
    /// </summary>
    public VadFrameResult Process(ReadOnlySpan<byte> pcm16)
    {
        _utteranceEnded = false;
        _cutForced = false;

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

        return new VadFrameResult(
            CurrentRmsDbfs,
            _peakDbfs,
            _inSpeech,
            SilenceTimeoutReached,
            _utteranceEnded,
            _cutForced);
    }

    /// <summary>
    /// Starts a fresh utterance after the caller has consumed the previous one, keeping the
    /// frame counter so byte offsets stay aligned with the caller's stream, and keeping the
    /// noise floor learned from the room so far. <see cref="Reset"/> would discard both.
    /// </summary>
    public void BeginUtterance()
    {
        if (_pendingCutOffset >= 0)
        {
            // A forced cut lands mid-speech: the next utterance starts exactly at the cut and
            // the speaker is still talking, so nothing else is reset.
            UtteranceStartByteOffset = _pendingCutOffset;
            UtteranceEndByteOffset = Math.Max(LastSpeechByteOffset, _pendingCutOffset);
            _utteranceStartFrame = _pendingCutFrame;
            _pendingCutOffset = -1;
            _pendingCutFrame = -1;
            _utteranceEnded = false;
            _cutForced = false;
            return;
        }

        _utteranceStartFrame = -1;
        _aboveEnterFrames = 0;
        _silenceFrames = 0;
        _inSpeech = false;
        _utteranceEnded = false;
        UtteranceStartByteOffset = -1;
        UtteranceEndByteOffset = -1;
    }

    public void Reset()
    {
        _partialCount = 0;
        _framesProcessed = 0;
        _aboveEnterFrames = 0;
        _silenceFrames = 0;
        _inSpeech = false;
        _utteranceEnded = false;
        _noiseFloorDbfs = SeedNoiseFloorDbfs;
        _peakDbfs = MinDbfs;
        CurrentRmsDbfs = MinDbfs;
        HasSpeech = false;
        SilenceTimeoutReached = false;
        FirstSpeechByteOffset = -1;
        LastSpeechByteOffset = -1;
        UtteranceStartByteOffset = -1;
        UtteranceEndByteOffset = -1;
        _utteranceStartFrame = -1;
        _cutForced = false;
        _pendingCutOffset = -1;
        _pendingCutFrame = -1;
        Array.Clear(_rmsHistory);
    }

    private void ProcessFrame(ReadOnlySpan<byte> frame)
    {
        long frameIndex = _framesProcessed;
        long frameStart = frameIndex * _frameBytes;
        _framesProcessed++;

        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(frame);
        Measure(samples, out double rmsDbfs, out double peakDbfs);
        CurrentRmsDbfs = rmsDbfs;
        _peakDbfs = peakDbfs;
        _rmsHistory[frameIndex % RmsHistoryFrames] = rmsDbfs;

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
                long onset = Math.Max(0, frameStart - ((SpeechDebounceFrames - 1) * (long)_frameBytes));
                if (FirstSpeechByteOffset < 0)
                {
                    FirstSpeechByteOffset = onset;
                }

                if (UtteranceStartByteOffset < 0)
                {
                    UtteranceStartByteOffset = onset;
                    _utteranceStartFrame = Math.Max(0, frameIndex - (SpeechDebounceFrames - 1));
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

        // A forced cut waits for the caller to take it; until then the end it reported must not move.
        bool cutPending = _pendingCutOffset >= 0;

        if (rmsDbfs >= exit)
        {
            _silenceFrames = 0;
            LastSpeechByteOffset = frameStart + _frameBytes;
            if (!cutPending)
            {
                UtteranceEndByteOffset = LastSpeechByteOffset;
                TryForceCut(frameIndex);
            }

            return;
        }

        _silenceFrames++;
        if (cutPending)
        {
            return;
        }

        // The longer somebody has talked without a real pause, the shorter the pause that ends it.
        bool longUtterance = _paced
            && _utteranceStartFrame >= 0
            && frameIndex + 1 - _utteranceStartFrame >= _softAfterFrames;
        int needed = longUtterance ? _softPauseFrames : _hangoverFrames;

        if (_silenceFrames >= needed)
        {
            _inSpeech = false;
            _silenceFrames = 0;
            _utteranceEnded = true;

            // Only the full hangover means the speaker stopped; a short pause ends a sentence, not a session.
            if (needed == _hangoverFrames)
            {
                SilenceTimeoutReached = true;
            }

            return;
        }

        TryForceCut(frameIndex);
    }

    /// <summary>
    /// Ends an utterance that has run past the maximum without a pause, at the quietest frame of
    /// the last 1.2 s — most likely the gap between two words — so no word is cut in half.
    /// </summary>
    private void TryForceCut(long frameIndex)
    {
        if (!_paced || _utteranceStartFrame < 0 || frameIndex + 1 - _utteranceStartFrame < _maxFrames)
        {
            return;
        }

        long earliest = Math.Max(_utteranceStartFrame + MinFramesBeforeCut, frameIndex - CutSearchFrames + 1);
        long quietest = frameIndex;
        double quietestRms = double.MaxValue;
        for (long f = earliest; f <= frameIndex; f++)
        {
            double rms = _rmsHistory[f % RmsHistoryFrames];
            if (rms <= quietestRms)
            {
                quietestRms = rms;
                quietest = f;
            }
        }

        long cut = (quietest * _frameBytes) + (_frameBytes / 2);
        cut -= cut % WavUtil.BlockAlign;

        UtteranceEndByteOffset = cut;
        _pendingCutOffset = cut;
        _pendingCutFrame = quietest;
        _utteranceEnded = true;
        _cutForced = true;
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
