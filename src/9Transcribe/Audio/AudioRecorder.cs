using System.IO;
using NAudio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NineTranscribe.Diagnostics;
using NineTranscribe.Settings;

namespace NineTranscribe.Audio;

public enum RecorderState
{
    Idle,
    Monitoring,
    Recording,
    Stopping,
}

/// <summary>Why a recording ended. Carried to the caller on <see cref="RecordingCompletedEventArgs"/>.</summary>
public enum StopReason
{
    UserStopped,
    HotkeyReleased,
    SilenceDetected,
    MaxDurationReached,

    /// <summary>Nothing was said for long enough that the session was assumed to be forgotten.</summary>
    IdleTimeout,
    DeviceLost,
    Cancelled,
}

/// <summary>What became of a <see cref="AudioRecorder.StartRecording"/> request.</summary>
public enum StartOutcome
{
    /// <summary>Capture is running.</summary>
    Started,

    /// <summary>Queued behind a session that is closing; it will start on its own.</summary>
    Queued,

    /// <summary>The device could not be opened, or something else already holds it.</summary>
    Failed,
}

public enum RecorderErrorKind
{
    DeviceLost,
    DeviceFallback,
    MicBusy,
    Unknown,
}

/// <summary>
/// A capture endpoint the recorder can open. <c>FriendlyName</c> is the full WASAPI name and is
/// what gets persisted; <c>WaveInIndex</c> is the WinMM index it resolved to and is only valid
/// until the device list changes.
/// </summary>
public sealed record AudioDeviceInfo(string FriendlyName, int WaveInIndex, bool IsDefault);

/// <summary>Level of one 30 ms frame. Raised on the capture thread ~33 times a second.</summary>
/// <summary>One sentence cut loose from a session that is still recording.</summary>
public sealed class SegmentReadyEventArgs : EventArgs
{
    public SegmentReadyEventArgs(byte[] wavBytes, TimeSpan duration, int index)
    {
        WavBytes = wavBytes;
        Duration = duration;
        Index = index;
    }

    public byte[] WavBytes { get; }

    public TimeSpan Duration { get; }

    /// <summary>Position in the session, counted from zero, so the caller can keep them in order.</summary>
    public int Index { get; }
}

public sealed class LevelEventArgs : EventArgs
{
    public LevelEventArgs(
        double rmsDbfs,
        double peakDbfs,
        bool isSpeech,
        double thresholdDbfs,
        double noiseFloorDbfs)
    {
        RmsDbfs = rmsDbfs;
        PeakDbfs = peakDbfs;
        IsSpeech = isSpeech;
        ThresholdDbfs = thresholdDbfs;
        NoiseFloorDbfs = noiseFloorDbfs;
        Normalized = Math.Clamp((rmsDbfs + 60.0) / 60.0, 0.0, 1.0);
    }

    public double RmsDbfs { get; }

    public double PeakDbfs { get; }

    public bool IsSpeech { get; }

    public double ThresholdDbfs { get; }

    public double NoiseFloorDbfs { get; }

    /// <summary>0..1 meter position mapped from the -60..0 dBFS range that matters for speech.</summary>
    public double Normalized { get; }
}

public sealed class RecordingCompletedEventArgs : EventArgs
{
    public RecordingCompletedEventArgs(
        byte[] wavBytes,
        TimeSpan duration,
        StopReason reason,
        bool hasSpeech,
        int segmentCount = 0)
    {
        WavBytes = wavBytes;
        Duration = duration;
        Reason = reason;
        HasSpeech = hasSpeech;
        SegmentCount = segmentCount;
    }

    /// <summary>A complete WAV file, or empty when <see cref="HasSpeech"/> is false.</summary>
    public byte[] WavBytes { get; }

    /// <summary>How much audio was captured, measured before silence trimming.</summary>
    public TimeSpan Duration { get; }

    public StopReason Reason { get; }

    /// <summary>False when nothing worth transcribing was heard; the caller must not call the API.</summary>
    public bool HasSpeech { get; }

    /// <summary>
    /// Sentences already handed over during the session. When this is non-zero the payload is
    /// only the tail, and an empty one means the session simply ended on a pause.
    /// </summary>
    public int SegmentCount { get; }
}

public sealed class RecorderErrorEventArgs : EventArgs
{
    public RecorderErrorEventArgs(RecorderErrorKind kind, string messageThai)
    {
        Kind = kind;
        MessageThai = messageThai;
    }

    public RecorderErrorKind Kind { get; }

    public string MessageThai { get; }
}

/// <summary>
/// One recording session's configuration. A null <c>DeviceFriendlyName</c> means "whatever
/// Windows currently calls the default capture device".
/// </summary>
/// <param name="SegmentOnSilence">
/// Cut each sentence loose at the pause that follows it and hand it over straight away, instead
/// of holding the whole session back until the user stops. Recording continues across the cut.
/// </param>
/// <param name="IdleStopSeconds">
/// Stop by itself after this much unbroken silence, so a session left running by accident does
/// not hold the microphone forever. Zero disables it.
/// </param>
public sealed record RecordingOptions(
    string? DeviceFriendlyName,
    bool AutoStopOnSilence,
    VadSettings Vad,
    int MaxDurationSeconds = 600,
    int MinUtteranceMs = 300,
    bool SegmentOnSilence = false,
    int IdleStopSeconds = 0);

/// <summary>
/// The single owner of the microphone. Capture runs through WinMM (<see cref="WaveInEvent"/>)
/// because it accepts a 16 kHz mono format directly and follows WAVE_MAPPER, i.e. the Windows
/// default device, without any extra plumbing; WASAPI here is only used to put full device
/// names in front of the user.
/// <para>
/// Threading: <see cref="LevelChanged"/> and <see cref="SpeechDetected"/> fire on the capture
/// thread, <see cref="RecordingCompleted"/> and <see cref="Error"/> on a thread-pool thread.
/// Nothing is marshalled for the subscriber. A device-loss failure raises
/// <see cref="RecordingCompleted"/> for whatever was captured and then <see cref="Error"/>.
/// </para>
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    /// <summary>WinMM device index that follows the current Windows default capture device.</summary>
    private const int WaveMapperDevice = -1;

    private const int CaptureBufferMs = 30;
    private const int CaptureBufferCount = 4;

    /// <summary>Audio kept from before the hotkey press. Thai unvoiced initials (ข ผ ฝ ถ) live here.</summary>
    private const int PreRollMs = 300;

    private const int TrimPadMs = 200;
    private const int InitialCaptureCapacity = 512 * 1024;

    /// <summary>
    /// Ceiling on a single session's capture buffer. Segmenting removes the reason to stop at
    /// the per-recording cap, so without this a session left running while somebody talks would
    /// grow the buffer without limit — this is an hour of audio, about 115 MB.
    /// </summary>
    private const long SessionCaptureLimitBytes = 3600L * WavUtil.BytesPerSecond;

    private readonly object _gate = new();
    private readonly byte[] _preRoll = new byte[WavUtil.BytesForMilliseconds(PreRollMs)];

    private int _preRollWrite;
    private int _preRollFilled;

    private WaveInEvent? _waveIn;
    private RecorderState _state = RecorderState.Idle;
    private MemoryStream? _capture;
    private VadMonitor? _vad;
    private RecordingOptions? _options;
    private MonitorRequest? _resumeMonitor;
    private PendingStart? _pending;
    private string? _activeDeviceName;
    private long _capturedBytes;
    private long _maxBytes;

    /// <summary>
    /// How much of the capture buffer has already been handed over as a segment. Everything the
    /// recorder reports afterwards starts here, so the tail is never transcribed twice.
    /// </summary>
    private int _segmentConsumed;
    private int _segmentIndex;
    private long _silentBytes;
    private long _idleStopBytes;
    private bool _speechRaised;
    private bool _cancelled;
    private bool _finalizeAsRecording;
    private bool _disposed;
    private StopReason _stopReason = StopReason.UserStopped;
    private double _lastNoiseFloorDbfs = -60.0;

    public RecorderState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Noise floor measured by the most recent frame; what the calibrate button reads.</summary>
    public double LastNoiseFloorDbfs
    {
        get
        {
            lock (_gate)
            {
                return _lastNoiseFloorDbfs;
            }
        }
    }

    public event EventHandler<LevelEventArgs>? LevelChanged;

    /// <summary>Raised once per session, the first time the detector confirms an utterance.</summary>
    public event EventHandler? SpeechDetected;

    public event EventHandler<RecordingCompletedEventArgs>? RecordingCompleted;

    public event EventHandler<RecorderErrorEventArgs>? Error;

    /// <summary>
    /// A sentence is ready while recording continues. Raised on the capture thread like the
    /// others, so subscribers marshal for themselves.
    /// </summary>
    public event EventHandler<SegmentReadyEventArgs>? SegmentReady;

    /// <summary>
    /// Active capture endpoints, newest device list each call. The list holds real devices only:
    /// the "system default" choice is a null device name, not an entry here.
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;

            // WAVE_MAPPER follows the Console role, so that is the one worth flagging as
            // default; the Communications role is a different device on many machines.
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
            {
                using MMDevice defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                defaultId = defaultDevice.ID;
            }

            foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (endpoint)
                {
                    string name = endpoint.FriendlyName;
                    int index = FindWaveInIndex(name);
                    if (index < 0)
                    {
                        continue;
                    }

                    devices.Add(new AudioDeviceInfo(
                        name,
                        index,
                        string.Equals(endpoint.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("WASAPI capture enumeration failed; falling back to WinMM device names", ex);
        }

        if (devices.Count == 0)
        {
            int count = SafeDeviceCount();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    WaveInCapabilities capabilities = WaveInEvent.GetCapabilities(i);
                    devices.Add(new AudioDeviceInfo(capabilities.ProductName ?? string.Empty, i, false));
                }
                catch (Exception ex)
                {
                    Log.Warn($"waveInGetDevCaps({i}) failed: {ex.Message}");
                }
            }
        }

        return devices;
    }

    /// <summary>
    /// Opens the device and reports levels without keeping any audio. Calling it again with the
    /// same device and the same tuning is a no-op, so the running noise-floor estimate survives.
    /// </summary>
    public void StartMonitoring(string? deviceFriendlyName, VadSettings vad)
    {
        ArgumentNullException.ThrowIfNull(vad);

        var request = new MonitorRequest(deviceFriendlyName, vad.Clone());
        var errors = new List<RecorderErrorEventArgs>();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            MonitorRequest? previous = _resumeMonitor;
            _resumeMonitor = request;

            if (_state == RecorderState.Idle)
            {
                BeginSessionLocked(null, request, errors);
            }
            else if (_state == RecorderState.Monitoring)
            {
                if (!SameDeviceLocked(request.DeviceFriendlyName))
                {
                    _pending = new PendingStart(null, request);
                    RequestStopLocked(StopReason.UserStopped);
                }
                else if (previous is null || !SameTuning(previous.Vad, request.Vad))
                {
                    _vad = new VadMonitor(request.Vad);
                }
            }
        }

        RaiseErrors(errors);
    }

    public void StopMonitoring()
    {
        lock (_gate)
        {
            _resumeMonitor = null;
            if (_state != RecorderState.Monitoring)
            {
                return;
            }

            _pending = null;
            RequestStopLocked(StopReason.UserStopped);
        }
    }

    /// <summary>
    /// Starts buffering. When monitoring is already running on the same device the capture is
    /// kept open and its pre-roll is carried into the recording, which is the only way the first
    /// syllable survives the delay between speaking and pressing the key.
    /// </summary>
    /// <summary>
    /// Starts capturing, or queues the start behind a session that is still winding down. The
    /// caller cannot tell those apart from <see cref="State"/> alone — the device takes tens of
    /// milliseconds to close, so a quick second press legitimately lands in <c>Stopping</c> —
    /// hence the explicit outcome.
    /// </summary>
    public StartOutcome StartRecording(RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Vad);

        var errors = new List<RecorderErrorEventArgs>();
        StartOutcome outcome;

        lock (_gate)
        {
            if (_disposed)
            {
                return StartOutcome.Failed;
            }

            if (_state == RecorderState.Recording)
            {
                Log.Warn("StartRecording ignored: a recording is already running");
                outcome = StartOutcome.Failed;
            }
            else if (_state == RecorderState.Stopping)
            {
                _pending = new PendingStart(options, null);
                outcome = StartOutcome.Queued;
            }
            else if (_state == RecorderState.Monitoring && SameDeviceLocked(options.DeviceFriendlyName))
            {
                PromoteToRecordingLocked(options);
                outcome = _state == RecorderState.Recording ? StartOutcome.Started : StartOutcome.Failed;
            }
            else if (_state == RecorderState.Monitoring)
            {
                _pending = new PendingStart(options, null);
                RequestStopLocked(StopReason.UserStopped);
                outcome = StartOutcome.Queued;
            }
            else
            {
                BeginSessionLocked(options, null, errors);
                outcome = _state == RecorderState.Recording ? StartOutcome.Started : StartOutcome.Failed;
            }
        }

        RaiseErrors(errors);
        return outcome;
    }

    /// <summary>Safe from any thread, including the capture thread.</summary>
    public void StopRecording(StopReason reason)
    {
        lock (_gate)
        {
            if (_state == RecorderState.Stopping && _pending?.Recording is not null)
            {
                // A start queued behind the previous session's teardown has already been
                // superseded by this stop; letting it through would start a recording nobody
                // is holding a key for, which then runs to the duration cap.
                _pending = _resumeMonitor is null ? null : new PendingStart(null, _resumeMonitor);
                return;
            }

            if (_state != RecorderState.Recording)
            {
                return;
            }

            RequestStopLocked(reason);
        }
    }

    /// <summary>Stops and throws the audio away: no <see cref="RecordingCompleted"/> is raised.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_state == RecorderState.Recording)
            {
                _cancelled = true;
                RequestStopLocked(StopReason.Cancelled);
            }
            else if (_state == RecorderState.Stopping && _finalizeAsRecording)
            {
                _cancelled = true;
                _stopReason = StopReason.Cancelled;
            }
        }
    }

    public void Dispose()
    {
        WaveInEvent? device = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = null;
            _resumeMonitor = null;
            _cancelled = true;

            if (_state == RecorderState.Monitoring || _state == RecorderState.Recording)
            {
                RequestStopLocked(StopReason.Cancelled);
            }
            else if (_state == RecorderState.Idle)
            {
                device = _waveIn;
                _waveIn = null;
                _capture?.Dispose();
                _capture = null;
            }
        }

        device?.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Always trust BytesRecorded: the buffer is reused at its full length and the tail of
        // it still holds the previous frame.
        int count = e.BytesRecorded;
        if (count <= 0)
        {
            return;
        }

        LevelEventArgs level;
        bool speechStarted = false;
        SegmentReadyEventArgs? segment = null;

        lock (_gate)
        {
            if (!ReferenceEquals(sender, _waveIn) || _vad is null)
            {
                return;
            }

            if (_capture is not null && _state is RecorderState.Recording or RecorderState.Stopping)
            {
                _capture.Write(e.Buffer, 0, count);
                _capturedBytes += count;
            }
            else
            {
                WritePreRollLocked(e.Buffer, count);
            }

            VadFrameResult frame = _vad.Process(e.Buffer.AsSpan(0, count));
            _lastNoiseFloorDbfs = _vad.NoiseFloorDbfs;
            level = new LevelEventArgs(
                frame.RmsDbfs,
                frame.PeakDbfs,
                frame.IsSpeech,
                _vad.EnterThresholdDbfs,
                _vad.NoiseFloorDbfs);

            if (_vad.HasSpeech && !_speechRaised)
            {
                _speechRaised = true;
                speechStarted = true;
            }

            if (_state == RecorderState.Recording && _options is not null)
            {
                TrackIdleLocked(frame, count);

                if (_options.SegmentOnSilence && frame.UtteranceEnded)
                {
                    segment = CutSegmentLocked();
                }

                // Measured against the part not yet handed over, so a segmented session can run
                // for as long as the user keeps talking while each upload stays a sane size.
                if (_capturedBytes - _segmentConsumed >= _maxBytes
                    || _capturedBytes >= SessionCaptureLimitBytes)
                {
                    Log.Info("Recording hit the maximum duration");
                    RequestStopLocked(StopReason.MaxDurationReached);
                }
                else if (_idleStopBytes > 0 && _silentBytes >= _idleStopBytes)
                {
                    Log.Info("Recording stopped after a long silence");
                    RequestStopLocked(StopReason.IdleTimeout);
                }
                else if (_options.AutoStopOnSilence && _vad.HasSpeech && frame.SilenceTimeoutReached)
                {
                    RequestStopLocked(StopReason.SilenceDetected);
                }
            }
        }

        // Never raise while holding the lock: a subscriber that marshals to the UI thread would
        // deadlock against any start/stop call made from that thread.
        LevelChanged?.Invoke(this, level);
        if (speechStarted)
        {
            SpeechDetected?.Invoke(this, EventArgs.Empty);
        }

        if (segment is not null)
        {
            SegmentReady?.Invoke(this, segment);
        }
    }

    /// <summary>
    /// Counts unbroken silence so a forgotten session can stop itself. Any speech resets it.
    /// </summary>
    private void TrackIdleLocked(VadFrameResult frame, int count)
    {
        if (frame.IsSpeech)
        {
            _silentBytes = 0;
        }
        else
        {
            _silentBytes += count;
        }
    }

    /// <summary>
    /// Copies the sentence that just ended out of the capture buffer and marks it consumed.
    /// Runs on the capture thread under the lock, so it stays a memcpy — the event itself is
    /// raised by the caller once the lock is gone.
    /// </summary>
    private SegmentReadyEventArgs? CutSegmentLocked()
    {
        if (_capture is null || _vad is null || _options is null)
        {
            return null;
        }

        if (_vad.UtteranceStartByteOffset < 0 || _vad.UtteranceEndByteOffset < 0)
        {
            return null;
        }

        int length = (int)_capture.Length;
        int pad = WavUtil.BytesForMilliseconds(TrimPadMs);
        int start = (int)Math.Clamp(_vad.UtteranceStartByteOffset - pad, _segmentConsumed, length);
        int end = (int)Math.Clamp(_vad.UtteranceEndByteOffset + pad, start, length);
        start -= start % WavUtil.BlockAlign;
        end -= end % WavUtil.BlockAlign;

        int kept = Math.Max(0, end - start);
        int minBytes = Math.Max(WavUtil.BlockAlign, WavUtil.BytesForMilliseconds(_options.MinUtteranceMs));

        // Consume up to the end of the utterance either way: a fragment too short to be worth
        // sending must not be left behind to reappear in the next segment or in the tail.
        _segmentConsumed = end;
        _vad.BeginUtterance();

        if (kept < minBytes)
        {
            Log.Info($"Segment skipped: {kept} bytes is below the minimum utterance");
            return null;
        }

        byte[] wav = WavUtil.CreateWav(_capture.GetBuffer().AsSpan(start, kept));
        var duration = TimeSpan.FromSeconds(kept / (double)WavUtil.BytesPerSecond);
        Log.Info($"Segment {_segmentIndex} ready: {kept} bytes");
        return new SegmentReadyEventArgs(wav, duration, _segmentIndex++);
    }

    /// <summary>
    /// The one place a session is finalized. Late buffers keep arriving after the device is asked
    /// to stop, and this handler runs only once they have all been delivered.
    /// </summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        var errors = new List<RecorderErrorEventArgs>();
        RecordingCompletedEventArgs? completed = null;
        WaveInEvent? device;
        PendingStart? pending = null;
        MonitorRequest? resume = null;

        lock (_gate)
        {
            if (!ReferenceEquals(sender, _waveIn))
            {
                (sender as WaveInEvent)?.Dispose();
                return;
            }

            device = _waveIn;
            _waveIn = null;

            bool deviceLost = e.Exception is not null;
            bool wasRecording = _state == RecorderState.Recording
                || (_state == RecorderState.Stopping && _finalizeAsRecording);
            StopReason reason = deviceLost
                ? StopReason.DeviceLost
                : _state == RecorderState.Stopping ? _stopReason : StopReason.UserStopped;

            if (deviceLost)
            {
                Log.Error("Capture device stopped unexpectedly", e.Exception);
            }

            if (wasRecording && !_cancelled)
            {
                completed = BuildCompletionLocked(reason);
            }

            if (deviceLost)
            {
                errors.Add(new RecorderErrorEventArgs(
                    RecorderErrorKind.DeviceLost,
                    "ไมโครโฟนหยุดทำงานหรือถูกถอดออก กรุณาตรวจสอบอุปกรณ์"));
                _pending = null;
                _resumeMonitor = null;
            }

            ResetSessionLocked();
            _state = RecorderState.Idle;

            if (!_disposed)
            {
                pending = _pending;
                resume = pending is null ? _resumeMonitor : null;
            }

            _pending = null;
        }

        device?.Dispose();

        if (completed is not null)
        {
            RecordingCompleted?.Invoke(this, completed);
        }

        RaiseErrors(errors);

        if (pending?.Recording is not null)
        {
            StartRecording(pending.Recording);
        }
        else if (pending?.Monitor is not null)
        {
            StartMonitoring(pending.Monitor.DeviceFriendlyName, pending.Monitor.Vad);
        }
        else if (resume is not null)
        {
            StartMonitoring(resume.DeviceFriendlyName, resume.Vad);
        }
    }

    /// <summary>
    /// Builds the result for the session. In a segmented session this is only the tail — whatever
    /// was said after the last sentence was handed over — because everything before it has already
    /// been transcribed and typed.
    /// </summary>
    private RecordingCompletedEventArgs BuildCompletionLocked(StopReason reason)
    {
        int length = _capture is null ? 0 : (int)_capture.Length;
        int available = Math.Max(0, length - _segmentConsumed);
        var duration = TimeSpan.FromSeconds(available / (double)WavUtil.BytesPerSecond);
        int minBytes = Math.Max(WavUtil.BlockAlign, WavUtil.BytesForMilliseconds(_options?.MinUtteranceMs ?? 300));

        // A segmented session that ends on a pause has an empty tail, which is the normal way to
        // finish rather than a failure — the caller distinguishes them by the segment count.
        bool tailHasSpeech = _vad is not null
            && (_segmentConsumed > 0 ? _vad.UtteranceStartByteOffset >= 0 : _vad.HasSpeech);

        if (_capture is null || _vad is null || !tailHasSpeech || available < minBytes)
        {
            Log.Info($"Recording tail discarded: {available} bytes, no usable speech, reason={reason}");
            return new RecordingCompletedEventArgs(
                Array.Empty<byte>(), duration, reason, false, _segmentIndex);
        }

        (int start, int kept) = SpeechRange(length, _vad);
        byte[] wav = WavUtil.CreateWav(_capture.GetBuffer().AsSpan(start, kept));
        Log.Info($"Recording finished: {length} bytes captured, {kept} bytes kept, reason={reason}");
        return new RecordingCompletedEventArgs(wav, duration, reason, true, _segmentIndex);
    }

    /// <summary>
    /// Pads the detected speech by 200 ms on both sides. Cutting the trailing silence matters as
    /// much as keeping the onset: the model invents text when it is handed a long quiet tail.
    /// </summary>
    private (int Start, int Length) SpeechRange(int pcmLength, VadMonitor vad)
    {
        // In a segmented session the tail's own onset is what matters; the session-wide first
        // offset points at a sentence that was handed over long ago.
        long first = _segmentConsumed > 0 ? vad.UtteranceStartByteOffset : vad.FirstSpeechByteOffset;
        long last = _segmentConsumed > 0 ? vad.UtteranceEndByteOffset : vad.LastSpeechByteOffset;

        if (first < 0 || last < 0)
        {
            return (_segmentConsumed, Math.Max(0, pcmLength - _segmentConsumed));
        }

        int pad = WavUtil.BytesForMilliseconds(TrimPadMs);
        int start = (int)Math.Clamp(first - pad, _segmentConsumed, pcmLength);
        int end = (int)Math.Clamp(last + pad, start, pcmLength);
        start -= start % WavUtil.BlockAlign;
        end -= end % WavUtil.BlockAlign;
        return (start, Math.Max(0, end - start));
    }

    private void BeginSessionLocked(
        RecordingOptions? recording,
        MonitorRequest? monitor,
        List<RecorderErrorEventArgs> errors)
    {
        string? requested = recording is not null ? recording.DeviceFriendlyName : monitor?.DeviceFriendlyName;
        int deviceNumber = ResolveDeviceIndex(requested, out bool fellBack);
        if (fellBack)
        {
            errors.Add(new RecorderErrorEventArgs(
                RecorderErrorKind.DeviceFallback,
                "ไม่พบไมโครโฟนที่เลือกไว้ กำลังใช้ไมโครโฟนเริ่มต้นของระบบแทน"));
        }

        try
        {
            _waveIn = StartCapture(deviceNumber);
        }
        catch (MmException ex)
        {
            Log.Error($"Could not open capture device {deviceNumber}", ex);
            errors.Add(new RecorderErrorEventArgs(
                RecorderErrorKind.MicBusy,
                "ไมโครโฟนถูกใช้งานโดยโปรแกรมอื่น"));
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not start capture on device {deviceNumber}", ex);
            errors.Add(new RecorderErrorEventArgs(
                RecorderErrorKind.Unknown,
                "เปิดไมโครโฟนไม่สำเร็จ กรุณาลองใหม่อีกครั้ง"));
            return;
        }

        _activeDeviceName = requested;
        ClearPreRollLocked();

        if (recording is not null)
        {
            StartCaptureBufferLocked(recording);
            _state = RecorderState.Recording;
            Log.Info($"Recording started on waveIn device {deviceNumber}");
        }
        else
        {
            _options = null;
            _capture = null;
            _vad = new VadMonitor(monitor!.Vad);
            _state = RecorderState.Monitoring;
            Log.Info($"Monitoring started on waveIn device {deviceNumber}");
        }
    }

    private void PromoteToRecordingLocked(RecordingOptions options)
    {
        StartCaptureBufferLocked(options);
        CopyPreRollLocked();
        _state = RecorderState.Recording;
        Log.Info("Recording started from the running monitor session");
    }

    private void StartCaptureBufferLocked(RecordingOptions options)
    {
        _options = options;
        _vad = new VadMonitor(options.Vad);
        _capture = new MemoryStream(InitialCaptureCapacity);
        _capturedBytes = 0;
        _segmentConsumed = 0;
        _segmentIndex = 0;
        _silentBytes = 0;
        _speechRaised = false;
        _cancelled = false;
        _stopReason = StopReason.UserStopped;
        _finalizeAsRecording = false;
        _maxBytes = (long)Math.Clamp(options.MaxDurationSeconds, 5, 720) * WavUtil.BytesPerSecond;
        _idleStopBytes = options.IdleStopSeconds <= 0
            ? 0
            : (long)options.IdleStopSeconds * WavUtil.BytesPerSecond;
    }

    private void RequestStopLocked(StopReason reason)
    {
        if (_state != RecorderState.Monitoring && _state != RecorderState.Recording)
        {
            return;
        }

        WaveInEvent? device = _waveIn;
        if (device is null)
        {
            // Nothing is open, so no RecordingStopped will ever arrive to finish the session.
            ResetSessionLocked();
            _state = RecorderState.Idle;
            return;
        }

        _stopReason = reason;
        _finalizeAsRecording = _state == RecorderState.Recording;
        _state = RecorderState.Stopping;

        // WinMM deadlocks if the device is stopped from inside its own data callback, and
        // blocking on it while holding this lock would wedge the capture thread either way.
        Task.Run(() =>
        {
            try
            {
                device.StopRecording();
            }
            catch (Exception ex)
            {
                Log.Error("waveInStop failed", ex);
            }
        });
    }

    private void ResetSessionLocked()
    {
        _capture?.Dispose();
        _capture = null;
        _vad = null;
        _options = null;
        _activeDeviceName = null;
        _capturedBytes = 0;
        _maxBytes = 0;
        _segmentConsumed = 0;
        _segmentIndex = 0;
        _silentBytes = 0;
        _idleStopBytes = 0;
        _speechRaised = false;
        _cancelled = false;
        _finalizeAsRecording = false;
        ClearPreRollLocked();
    }

    private bool SameDeviceLocked(string? requested) =>
        string.Equals(_activeDeviceName, requested, StringComparison.OrdinalIgnoreCase);

    private void ClearPreRollLocked()
    {
        _preRollWrite = 0;
        _preRollFilled = 0;
    }

    private void WritePreRollLocked(byte[] data, int count)
    {
        if (count >= _preRoll.Length)
        {
            Buffer.BlockCopy(data, count - _preRoll.Length, _preRoll, 0, _preRoll.Length);
            _preRollWrite = 0;
            _preRollFilled = _preRoll.Length;
            return;
        }

        int first = Math.Min(count, _preRoll.Length - _preRollWrite);
        Buffer.BlockCopy(data, 0, _preRoll, _preRollWrite, first);
        int rest = count - first;
        if (rest > 0)
        {
            Buffer.BlockCopy(data, first, _preRoll, 0, rest);
        }

        _preRollWrite = (_preRollWrite + count) % _preRoll.Length;
        _preRollFilled = Math.Min(_preRoll.Length, _preRollFilled + count);
    }

    private void CopyPreRollLocked()
    {
        if (_preRollFilled == 0 || _capture is null || _vad is null)
        {
            return;
        }

        int start = ((_preRollWrite - _preRollFilled) + _preRoll.Length) % _preRoll.Length;
        int first = Math.Min(_preRollFilled, _preRoll.Length - start);
        _capture.Write(_preRoll, start, first);
        _vad.Process(_preRoll.AsSpan(start, first));

        int rest = _preRollFilled - first;
        if (rest > 0)
        {
            _capture.Write(_preRoll, 0, rest);
            _vad.Process(_preRoll.AsSpan(0, rest));
        }

        _capturedBytes = _preRollFilled;
        ClearPreRollLocked();
    }

    private WaveInEvent StartCapture(int deviceNumber)
    {
        // WaveInEvent captures SynchronizationContext.Current in its constructor and posts
        // RecordingStopped to it. Building it with no context keeps finalization on a pool
        // thread, so a busy or already-shut-down UI dispatcher can never strand a session.
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        WaveInEvent device;
        try
        {
            device = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(WavUtil.SampleRate, WavUtil.BitsPerSample, WavUtil.Channels),
                BufferMilliseconds = CaptureBufferMs,
                NumberOfBuffers = CaptureBufferCount,
            };
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        device.DataAvailable += OnDataAvailable;
        device.RecordingStopped += OnRecordingStopped;

        try
        {
            device.StartRecording();
        }
        catch (Exception)
        {
            device.DataAvailable -= OnDataAvailable;
            device.RecordingStopped -= OnRecordingStopped;
            device.Dispose();
            throw;
        }

        return device;
    }

    private static int ResolveDeviceIndex(string? friendlyName, out bool fellBack)
    {
        fellBack = false;
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return WaveMapperDevice;
        }

        int index = FindWaveInIndex(friendlyName);
        if (index >= 0)
        {
            return index;
        }

        fellBack = true;
        Log.Warn("Configured capture device is not present; falling back to the system default");
        return WaveMapperDevice;
    }

    private static int FindWaveInIndex(string friendlyName)
    {
        int count = SafeDeviceCount();
        for (int i = 0; i < count; i++)
        {
            try
            {
                WaveInCapabilities capabilities = WaveInEvent.GetCapabilities(i);
                if (NamesMatch(friendlyName, capabilities.ProductName))
                {
                    return i;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"waveInGetDevCaps({i}) failed: {ex.Message}");
            }
        }

        return -1;
    }

    /// <summary>
    /// Compares a stored or WASAPI name with a WinMM product name. WinMM truncates product
    /// names to 31 characters, so only the shared prefix can be compared.
    /// </summary>
    private static bool NamesMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        int length = Math.Min(a.Length, b.Length);
        return a.AsSpan(0, length).Equals(b.AsSpan(0, length), StringComparison.OrdinalIgnoreCase);
    }

    private static int SafeDeviceCount()
    {
        try
        {
            return WaveInEvent.DeviceCount;
        }
        catch (Exception ex)
        {
            Log.Error("waveInGetNumDevs failed", ex);
            return 0;
        }
    }

    private static bool SameTuning(VadSettings a, VadSettings b) =>
        a.EnterDbfs.Equals(b.EnterDbfs)
        && a.ExitOffsetDb.Equals(b.ExitOffsetDb)
        && a.HangoverMs == b.HangoverMs
        && a.Adaptive == b.Adaptive;

    private void RaiseErrors(List<RecorderErrorEventArgs> errors)
    {
        foreach (RecorderErrorEventArgs error in errors)
        {
            Log.Warn($"Recorder error: {error.Kind}");
            Error?.Invoke(this, error);
        }
    }

    private sealed record MonitorRequest(string? DeviceFriendlyName, VadSettings Vad);

    private sealed record PendingStart(RecordingOptions? Recording, MonitorRequest? Monitor);
}
