using System.Windows.Threading;
using NineTranscribe.Api;
using NineTranscribe.Audio;
using NineTranscribe.Diagnostics;
using NineTranscribe.History;
using NineTranscribe.Hotkeys;
using NineTranscribe.Injection;
using NineTranscribe.Overlay;
using NineTranscribe.Processing;
using NineTranscribe.Settings;

namespace NineTranscribe.Core;

public enum DictationState
{
    Idle,
    Recording,

    /// <summary>Uploading to the API and inserting the result; both are one busy state.</summary>
    Transcribing,
}

/// <summary>
/// Drives one dictation from hotkey to typed text: record, transcribe, clean up, insert.
/// Everything else in the app is a component this orchestrates — the subsystems never call
/// each other directly.
/// </summary>
public sealed class DictationController : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly AudioRecorder _recorder;
    private readonly HotkeyManager _hotkeys;
    private readonly OpenAiTranscriptionClient _api;
    private readonly TextInjector _injector;
    private readonly OverlayViewModel _overlay;
    private readonly OverlayWindow _overlayWindow;
    private readonly TranscriptionHistory _history;
    private readonly Dispatcher _dispatcher;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private DictationState _state = DictationState.Idle;
    private bool _startedByToggle;
    private bool _recordingActive;
    private int _pipelinesRunning;
    private long _dictationSequence;

    public DictationController(
        SettingsStore settings,
        AudioRecorder recorder,
        HotkeyManager hotkeys,
        OpenAiTranscriptionClient api,
        TextInjector injector,
        OverlayViewModel overlay,
        OverlayWindow overlayWindow,
        TranscriptionHistory history,
        Dispatcher dispatcher)
    {
        _settings = settings;
        _recorder = recorder;
        _hotkeys = hotkeys;
        _api = api;
        _injector = injector;
        _overlay = overlay;
        _overlayWindow = overlayWindow;
        _history = history;
        _dispatcher = dispatcher;
    }

    public event EventHandler<DictationState>? StateChanged;

    public DictationState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public void Start()
    {
        _hotkeys.PushToTalkPressed += OnPushToTalkPressed;
        _hotkeys.PushToTalkReleased += OnPushToTalkReleased;
        _hotkeys.TogglePressed += OnTogglePressed;
        _hotkeys.CancelPressed += OnCancelPressed;
        _hotkeys.PhysicalKeyPressed += OnPhysicalKeyPressed;

        _recorder.RecordingCompleted += OnRecordingCompleted;
        _recorder.Error += OnRecorderError;
        _recorder.LevelChanged += OnLevelChanged;

        _api.Retrying += OnApiRetrying;

        ApplySettings(_settings.Current);
    }

    /// <summary>Pushes the current configuration into the components that cache it.</summary>
    public void ApplySettings(AppSettings settings)
    {
        _hotkeys.PushToTalk = settings.Hotkeys.PushToTalk;
        _hotkeys.Toggle = settings.Hotkeys.Toggle;
        _hotkeys.MaxHoldSeconds = settings.MaxRecordingSeconds;

        _overlayWindow.Anchor = settings.OverlayPosition;

        _history.IsEnabled = settings.HistoryEnabled;
        _history.MaxItems = settings.HistoryMaxItems;
        _history.PersistToDisk = settings.SaveHistoryToDisk;
    }

    /// <summary>Surfaces a start-up problem (a corrupt settings file, say) on the overlay.</summary>
    public void ShowStartupNotice(string messageThai) =>
        ShowOverlay(o => o.ShowNotice(messageThai), CurrentSequence);

    /// <summary>
    /// Runs the full pipeline on an existing recording without inserting the result. The
    /// settings window uses this so the test button exercises the real prompt and the real
    /// correction rules.
    /// </summary>
    public async Task<string> TranscribeForTestAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        AppSettings settings = _settings.Current;
        TranscriptionResult result = await _api
            .TranscribeAsync(wavBytes, BuildRequestOptions(settings), cancellationToken)
            .ConfigureAwait(false);
        return TranscriptPostProcessor.Process(result.Text, settings);
    }

    public void Dispose()
    {
        _hotkeys.PushToTalkPressed -= OnPushToTalkPressed;
        _hotkeys.PushToTalkReleased -= OnPushToTalkReleased;
        _hotkeys.TogglePressed -= OnTogglePressed;
        _hotkeys.CancelPressed -= OnCancelPressed;
        _hotkeys.PhysicalKeyPressed -= OnPhysicalKeyPressed;

        _recorder.RecordingCompleted -= OnRecordingCompleted;
        _recorder.Error -= OnRecorderError;
        _recorder.LevelChanged -= OnLevelChanged;

        _api.Retrying -= OnApiRetrying;

        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private void OnPushToTalkPressed(object? sender, EventArgs e) => BeginRecording(toggleMode: false);

    private void OnPushToTalkReleased(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!_recordingActive || _startedByToggle)
            {
                return;
            }
        }

        _recorder.StopRecording(StopReason.HotkeyReleased);
    }

    private void OnTogglePressed(object? sender, EventArgs e)
    {
        bool stopping;
        lock (_gate)
        {
            if (_recordingActive && !_startedByToggle)
            {
                // A push-to-talk hold is in progress; leave it alone rather than fighting it.
                return;
            }

            stopping = _recordingActive;
        }

        if (stopping)
        {
            _recorder.StopRecording(StopReason.UserStopped);
            return;
        }

        BeginRecording(toggleMode: true);
    }

    private void OnCancelPressed(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!_recordingActive)
            {
                return;
            }

            _recordingActive = false;
        }

        _recorder.Cancel();
        RefreshState();
        _hotkeys.CancelKeyArmed = false;
        RunOnUi(() => _overlay.HideNow());
    }

    private void OnPhysicalKeyPressed(object? sender, KeyEventData e)
    {
        // Our own hotkeys are not "the user started typing" — treating them as such would make
        // starting a second dictation abort the insertion of the first one mid-word.
        if (e.Vk == _hotkeys.PushToTalk.Vk || e.Vk == _hotkeys.Toggle.Vk)
        {
            return;
        }

        _injector.NotifyPhysicalKeyPressed();
    }

    private void BeginRecording(bool toggleMode)
    {
        lock (_gate)
        {
            if (_recordingActive)
            {
                return;
            }

            _recordingActive = true;
            _startedByToggle = toggleMode;
            _dictationSequence++;
        }

        AppSettings settings = _settings.Current;

        if (settings.ShowOverlay)
        {
            RunOnUi(() =>
            {
                // The monitor is chosen once, from where the user is actually typing.
                _overlayWindow.AttachToMonitorOfForegroundWindow();
                _overlay.ShowListening(toggleMode);
            });
        }

        var options = new RecordingOptions(
            settings.MicDeviceFriendlyName,
            AutoStopOnSilence: toggleMode,
            settings.Vad,
            settings.MaxRecordingSeconds,
            settings.MinUtteranceMs);

        try
        {
            _recorder.StartRecording(options);
        }
        catch (Exception ex)
        {
            Log.Error("Recording could not be started", ex);
            AbandonRecording("เริ่มบันทึกเสียงไม่ได้ กรุณาตรวจสอบไมโครโฟน");
            return;
        }

        // The recorder reports a failed device open through its Error event rather than by
        // throwing, and it ignores a start request while something else already holds the
        // microphone. Either way the only reliable signal is whether it actually started.
        if (_recorder.State != RecorderState.Recording)
        {
            AbandonRecording("เริ่มบันทึกเสียงไม่ได้ ไมโครโฟนอาจถูกใช้งานอยู่");
            return;
        }

        RefreshState();
        _hotkeys.CancelKeyArmed = true;
    }

    /// <summary>Clears a session that never really began, so the next hotkey press still works.</summary>
    private void AbandonRecording(string messageThai)
    {
        lock (_gate)
        {
            _recordingActive = false;
        }

        _hotkeys.CancelKeyArmed = false;
        ShowError(messageThai);
        RefreshState();
    }

    /// <summary>Drops any recording in progress, used when the tray master switch goes off.</summary>
    public void CancelActive()
    {
        lock (_gate)
        {
            if (!_recordingActive)
            {
                return;
            }

            _recordingActive = false;
        }

        _recorder.Cancel();
        _hotkeys.CancelKeyArmed = false;
        RefreshState();
        RunOnUi(() => _overlay.HideNow());
    }

    private void OnLevelChanged(object? sender, LevelEventArgs e)
    {
        lock (_gate)
        {
            if (!_recordingActive)
            {
                return;
            }
        }

        double level = e.Normalized;
        RunOnUi(() => _overlay.UpdateLevel(level));
    }

    private void OnRecordingCompleted(object? sender, RecordingCompletedEventArgs e)
    {
        lock (_gate)
        {
            if (!_recordingActive)
            {
                // The settings window drives the same recorder for its test button. Without
                // this guard a test recording would be transcribed and typed into whatever
                // window happens to be behind the settings window.
                return;
            }

            _recordingActive = false;
        }

        _hotkeys.CancelKeyArmed = false;

        if (e.Reason == StopReason.Cancelled)
        {
            RefreshState();
            return;
        }

        if (!e.HasSpeech || e.WavBytes.Length == 0)
        {
            RefreshState();
            ShowOverlay(o => o.ShowNotice("ไม่พบเสียงพูด"), CurrentSequence);
            return;
        }

        lock (_gate)
        {
            _pipelinesRunning++;
        }

        long sequence;
        lock (_gate)
        {
            sequence = _dictationSequence;
        }

        RefreshState();
        ShowOverlay(o => o.ShowProcessing(), sequence);

        // Deliberately not cancelling any pipeline already in flight: chaining a second
        // dictation while the first is still uploading must not throw the first one away.
        _ = Task.Run(() => RunPipelineAsync(e, sequence, _shutdown.Token), CancellationToken.None);
    }

    private async Task RunPipelineAsync(
        RecordingCompletedEventArgs recording,
        long sequence,
        CancellationToken cancellationToken)
    {
        AppSettings settings = _settings.Current;

        try
        {
            TranscriptionResult result = await _api
                .TranscribeAsync(recording.WavBytes, BuildRequestOptions(settings), cancellationToken)
                .ConfigureAwait(false);

            string text = TranscriptPostProcessor.Process(result.Text, settings);
            if (text.Length == 0)
            {
                ShowOverlay(o => o.ShowNotice("ไม่พบข้อความ"), sequence);
                return;
            }

            _history.Add(new HistoryEntry(
                DateTimeOffset.Now,
                text,
                result.Model,
                recording.Duration.TotalSeconds,
                (long)result.Elapsed.TotalMilliseconds));

            InsertionResult insertion = await _injector.InsertAsync(text, cancellationToken).ConfigureAwait(false);

            switch (insertion.Outcome)
            {
                case InsertionOutcome.Success:
                    ShowOverlay(o => o.ShowPreview(text), sequence);
                    break;

                case InsertionOutcome.Cancelled:
                    ShowOverlay(o => o.HideNow(), sequence);
                    break;

                default:
                    ShowOverlay(
                        o => o.ShowError(insertion.UserMessageThai ?? "วางข้อความไม่สำเร็จ"),
                        sequence);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            ShowOverlay(o => o.HideNow(), sequence);
        }
        catch (TranscriptionException ex)
        {
            Log.Warn($"Transcription failed: {ex.Kind} (HTTP {ex.HttpStatus?.ToString() ?? "-"})");

            if (ex.Kind == TranscriptionErrorKind.AudioTooShort)
            {
                ShowOverlay(o => o.ShowNotice("ไม่พบเสียงพูด"), sequence);
            }
            else
            {
                ShowOverlay(o => o.ShowError(ex.UserMessageThai), sequence);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dictation pipeline failed", ex);
            ShowOverlay(
                o => o.ShowError("เกิดข้อผิดพลาดที่ไม่คาดคิด กรุณาดูรายละเอียดใน log"),
                sequence);
        }
        finally
        {
            lock (_gate)
            {
                _pipelinesRunning--;
            }

            RefreshState();
        }
    }

    private TranscriptionRequestOptions BuildRequestOptions(AppSettings settings)
    {
        ModelCapabilities capabilities = ModelCapabilities.For(settings.Model);
        string prompt = PromptBuilder.Build(settings.PromptPrefix, settings.CustomVocabulary, capabilities);

        return new TranscriptionRequestOptions(
            settings.Model,
            settings.Language,
            prompt,
            settings.Temperature,
            settings.ApiTimeoutSeconds);
    }

    private void OnApiRetrying(object? sender, int attempt) =>
        ShowOverlay(o => o.ShowRetrying(), CurrentSequence);

    private void OnRecorderError(object? sender, RecorderErrorEventArgs e)
    {
        Log.Warn($"Recorder error: {e.Kind}");

        if (e.Kind == RecorderErrorKind.DeviceFallback)
        {
            ShowOverlay(o => o.ShowNotice(e.MessageThai), CurrentSequence);
            return;
        }

        ShowError(e.MessageThai);
    }

    /// <summary>
    /// Recomputes the state from what is actually happening. Recording outranks a transcription
    /// still in flight, so chaining dictations keeps the tray icon on the live one.
    /// </summary>
    private void RefreshState()
    {
        DictationState next;

        lock (_gate)
        {
            next = _recordingActive
                ? DictationState.Recording
                : _pipelinesRunning > 0 ? DictationState.Transcribing : DictationState.Idle;

            if (_state == next)
            {
                return;
            }

            _state = next;
        }

        StateChanged?.Invoke(this, next);
    }

    private long CurrentSequence
    {
        get
        {
            lock (_gate)
            {
                return _dictationSequence;
            }
        }
    }

    private void ShowError(string messageThai) => ShowOverlay(o => o.ShowError(messageThai), CurrentSequence);

    /// <summary>
    /// Runs an overlay update, unless a newer dictation has taken over the pill in the meantime
    /// or the user has turned the overlay off.
    /// </summary>
    private void ShowOverlay(Action<OverlayViewModel> update, long sequence)
    {
        if (!_settings.Current.ShowOverlay)
        {
            return;
        }

        RunOnUi(() =>
        {
            lock (_gate)
            {
                if (sequence != _dictationSequence)
                {
                    return;
                }
            }

            update(_overlay);
        });
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action);
    }
}
