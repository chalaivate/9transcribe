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

    /// <summary>
    /// Tail of the typing queue. Each piece of a session takes the current tail as its
    /// predecessor and leaves its own completion in its place, which keeps the sentences in
    /// spoken order even though they are transcribed concurrently. The state travels along the
    /// queue rather than living in a field, because a field would be rewritten by the next
    /// session while the previous one still had a sentence in flight.
    /// </summary>
    private Task<ChainState> _insertionChain = Task.FromResult(default(ChainState));

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
        _recorder.SegmentReady += OnSegmentReady;
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

        RunOnUi(() => _overlayWindow.ApplyStyle(settings));

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
        _recorder.SegmentReady -= OnSegmentReady;
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
            // A pause now ends a sentence, not the session: the user asked for the session to
            // end when they press the key, and only when they press the key.
            AutoStopOnSilence: false,
            settings.Vad,
            settings.MaxRecordingSeconds,
            settings.MinUtteranceMs,
            SegmentOnSilence: settings.SegmentOnPause,
            IdleStopSeconds: toggleMode ? settings.IdleStopSeconds : 0);

        StartOutcome outcome;
        try
        {
            outcome = _recorder.StartRecording(options);
        }
        catch (Exception ex)
        {
            Log.Error("Recording could not be started", ex);
            AbandonRecording("เริ่มบันทึกเสียงไม่ได้ กรุณาตรวจสอบไมโครโฟน");
            return;
        }

        // A failed device open is reported through the Error event rather than by throwing, so
        // the outcome is the only reliable signal. Queued is a success: pressing again quickly
        // lands while the previous session is still closing, and it starts by itself.
        if (outcome == StartOutcome.Failed)
        {
            AbandonRecording("เริ่มบันทึกเสียงไม่ได้ ไมโครโฟนอาจถูกใช้งานอยู่");
            return;
        }

        RefreshState();

        lock (_gate)
        {
            // A very short press can finalize before we get here; arming then would swallow
            // every Esc system-wide until the next dictation.
            if (!_recordingActive)
            {
                return;
            }
        }

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

    /// <summary>
    /// A sentence finished while the user keeps talking. Everything the completion handler does
    /// to wind the session down is deliberately absent here: the recording is still running.
    /// </summary>
    private void OnSegmentReady(object? sender, SegmentReadyEventArgs e)
    {
        long sequence;
        lock (_gate)
        {
            // Same guard as the completion path: the settings window's microphone test shares
            // this recorder, and its audio must never be typed into the user's document.
            if (!_recordingActive)
            {
                return;
            }

            _pipelinesRunning++;
            sequence = _dictationSequence;
        }

        RefreshState();

        // Claim this segment's place in the queue now, while the order is still known. The
        // pipelines run concurrently and a short sentence overtakes a long one, so without this
        // the sentences would be typed in whichever order the API happened to answer.
        var mine = new TaskCompletionSource<ChainState>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ChainState> previous = Interlocked.Exchange(ref _insertionChain, mine.Task);

        _ = Task.Run(
            () => RunPipelineAsync(
                new PipelineInput(e.WavBytes, sequence, previous, mine, IsSegment: true),
                _shutdown.Token),
            CancellationToken.None);
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

            // A segmented session that ends just after a pause has an empty tail, and everything
            // said has already been typed — that is a normal finish, not a failed dictation.
            if (e.SegmentCount == 0)
            {
                ShowOverlay(o => o.ShowNotice("No speech detected"), CurrentSequence);
            }
            else if (e.Reason == StopReason.IdleTimeout)
            {
                ShowOverlay(o => o.ShowNotice("Stopped after a long silence"), CurrentSequence);
            }
            else
            {
                ShowOverlay(o => o.HideNow(), CurrentSequence);
            }

            return;
        }

        long sequence;
        lock (_gate)
        {
            _pipelinesRunning++;
            sequence = _dictationSequence;
        }

        RefreshState();
        ShowOverlay(o => o.ShowProcessing(), sequence);

        // The tail queues behind the sentences already handed over during the session, so it is
        // typed last however quickly it comes back.
        var mine = new TaskCompletionSource<ChainState>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ChainState> previous = Interlocked.Exchange(ref _insertionChain, mine.Task);

        // Deliberately not cancelling any pipeline already in flight: chaining a second
        // dictation while the first is still uploading must not throw the first one away.
        _ = Task.Run(
            () => RunPipelineAsync(
                new PipelineInput(e.WavBytes, sequence, previous, mine, IsSegment: false),
                _shutdown.Token),
            CancellationToken.None);
    }

    /// <summary>One unit of work through the pipeline, and its place in the typing queue.</summary>
    /// <param name="Predecessor">Completes when the previous piece has been typed.</param>
    /// <param name="Completion">Signals the next piece that its turn has come.</param>
    private readonly record struct PipelineInput(
        byte[] WavBytes,
        long Sequence,
        Task<ChainState> Predecessor,
        TaskCompletionSource<ChainState> Completion,
        bool IsSegment);

    /// <summary>
    /// What the queue passes from one piece to the next: which dictation it belonged to, and
    /// whether anything has actually been typed for it yet. The sequence is what stops a new
    /// dictation from being glued onto the previous one's last sentence.
    /// </summary>
    private readonly record struct ChainState(long Sequence, bool AnyTyped);

    private async Task RunPipelineAsync(PipelineInput input, CancellationToken cancellationToken)
    {
        AppSettings settings = _settings.Current;
        long sequence = input.Sequence;
        ChainState carried = default;
        bool reachedQueue = false;
        bool typed = false;

        try
        {
            TranscriptionResult result = await _api
                .TranscribeAsync(input.WavBytes, BuildRequestOptions(settings), cancellationToken)
                .ConfigureAwait(false);

            string text = TranscriptPostProcessor.Process(result.Text, settings);
            if (text.Length == 0)
            {
                ShowOverlay(o => o.ShowNotice("No text"), sequence);
                return;
            }

            _history.Add(new HistoryEntry(
                DateTimeOffset.Now,
                text,
                result.Model,
                TimeSpan.FromSeconds(input.WavBytes.Length / (double)WavUtil.BytesPerSecond).TotalSeconds,
                (long)result.Elapsed.TotalMilliseconds));

            // Wait for the earlier sentences of this session to be typed before adding this one,
            // so the text lands in the order it was spoken.
            carried = await input.Predecessor.ConfigureAwait(false);
            reachedQueue = true;

            InsertionResult insertion = await _injector
                .InsertAsync(SeparatorFor(carried, sequence) + text, cancellationToken)
                .ConfigureAwait(false);

            typed = insertion.Outcome == InsertionOutcome.Success;

            switch (insertion.Outcome)
            {
                case InsertionOutcome.Success:
                    // A segment leaves the pill listening: the session is still running and the
                    // preview's own auto-hide would take the pill away mid-sentence.
                    if (input.IsSegment)
                    {
                        ShowOverlay(o => o.ShowSegment(text), sequence);
                    }
                    else
                    {
                        ShowOverlay(o => o.ShowPreview(text), sequence);
                    }

                    break;

                case InsertionOutcome.Cancelled:
                    ShowOverlay(o => o.HideNow(), sequence);
                    break;

                default:
                    ShowOverlay(
                        o => o.ShowError(insertion.UserMessageThai ?? "Paste failed"),
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
                ShowOverlay(o => o.ShowNotice("No speech detected"), sequence);
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
                o => o.ShowError("Unexpected error — see the log for details"),
                sequence);
        }
        finally
        {
            // Always release the next piece, even after a failure: one sentence that could not
            // be transcribed must not strand the rest of the session behind it. A piece that
            // failed before reaching the queue still has to wait its turn, or it would hand the
            // successor a state older than the one already in flight.
            if (!reachedQueue)
            {
                carried = await WaitForTurnAsync(input.Predecessor).ConfigureAwait(false);
            }

            bool anyTyped = (carried.Sequence == sequence && carried.AnyTyped) || typed;
            input.Completion.TrySetResult(new ChainState(sequence, anyTyped));

            lock (_gate)
            {
                _pipelinesRunning--;
            }

            RefreshState();
        }
    }

    /// <summary>Awaits the predecessor without letting its failure become ours.</summary>
    private static async Task<ChainState> WaitForTurnAsync(Task<ChainState> predecessor)
    {
        try
        {
            return await predecessor.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Previous insertion did not complete cleanly: {ex.Message}");
            return default;
        }
    }

    /// <summary>
    /// What goes between two sentences of the same dictation. Thai does not space between words,
    /// but it does between clauses, and without it the sentences run together. A new dictation
    /// starts clean, so its first sentence never inherits a separator from the last one.
    /// </summary>
    private static string SeparatorFor(ChainState carried, long sequence) =>
        carried.Sequence == sequence && carried.AnyTyped ? " " : string.Empty;

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
    /// or the user has turned the overlay off. The staleness check happens here rather than
    /// inside the dispatched action: by the time the UI thread runs, the next dictation has
    /// usually bumped the sequence already, which silently swallowed every message.
    /// </summary>
    private void ShowOverlay(Action<OverlayViewModel> update, long sequence)
    {
        if (!_settings.Current.ShowOverlay)
        {
            return;
        }

        lock (_gate)
        {
            if (sequence != _dictationSequence)
            {
                return;
            }
        }

        RunOnUi(() => update(_overlay));
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
