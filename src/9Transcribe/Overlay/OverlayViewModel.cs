using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NineTranscribe.Overlay;

public enum OverlayState
{
    Hidden,
    Listening,
    Processing,
    Preview,
    Error,
    Notice,
}

/// <summary>
/// Drives the overlay. UI-thread affine — callers marshal before touching it. One timer serves
/// every timed transition, and it is cancelled on each state change, or a stale preview timer
/// would hide an overlay that a new dictation has just shown.
/// </summary>
public sealed class OverlayViewModel : ObservableObject
{
    private const int ErrorHoldMs = 4000;
    private const int NoticeHoldMs = 1500;
    private const int ProblemHoldMs = 3500;
    private const int PreviewMinHoldMs = 2500;
    private const int PreviewMaxHoldMs = 8000;
    private const int PreviewMsPerChar = 45;

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _problemTimer;
    private readonly RollingTranscript _transcript = new();

    private OverlayState _state = OverlayState.Hidden;
    private double _level;
    private string _statusText = string.Empty;
    private string _problemText = string.Empty;
    private bool _isToggleMode;
    private DateTime _listeningSince;
    private int _pendingSegments;
    private int _transcriptRevision;
    private bool _finishWhenSettled;

    public OverlayViewModel()
    {
        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            HideNow();
        };

        _problemTimer = new DispatcherTimer();
        _problemTimer.Tick += (_, _) =>
        {
            _problemTimer.Stop();
            ProblemText = string.Empty;
            ContentChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Raised when the overlay's content changed enough that it needs re-placing.</summary>
    public event EventHandler? ContentChanged;

    public OverlayState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsVisible));
            }
        }
    }

    public bool IsVisible => _state != OverlayState.Hidden;

    /// <summary>Smoothed microphone level, 0 to 1, driving the waveform in the tab.</summary>
    public double Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>
    /// A short warning about one piece of a dictation that is still running — shown above the
    /// line for a few seconds without taking the listening tab away.
    /// </summary>
    public string ProblemText
    {
        get => _problemText;
        private set => SetProperty(ref _problemText, value);
    }

    public bool IsToggleMode
    {
        get => _isToggleMode;
        private set => SetProperty(ref _isToggleMode, value);
    }

    /// <summary>When the current listening session began; the tab counts up from it.</summary>
    public DateTime ListeningSince
    {
        get => _listeningSince;
        private set => SetProperty(ref _listeningSince, value);
    }

    /// <summary>Pieces of this dictation sent for transcription and not back yet.</summary>
    public int PendingSegments
    {
        get => _pendingSegments;
        private set => SetProperty(ref _pendingSegments, value);
    }

    /// <summary>Earlier text of this dictation, drawn dimmed.</summary>
    public string TranscriptOlder => _transcript.Older;

    /// <summary>The piece that arrived last, which animates in.</summary>
    public string TranscriptNewest => _transcript.Newest;

    public string TranscriptText => _transcript.Text;

    /// <summary>Bumped whenever a piece is added; the window redraws the transcript on it.</summary>
    public int TranscriptRevision
    {
        get => _transcriptRevision;
        private set => SetProperty(ref _transcriptRevision, value);
    }

    public void ShowListening(bool toggleMode)
    {
        _timer.Stop();
        _problemTimer.Stop();
        IsToggleMode = toggleMode;
        Level = 0;
        _finishWhenSettled = false;
        PendingSegments = 0;
        ProblemText = string.Empty;
        ClearTranscript();
        ListeningSince = DateTime.UtcNow;
        StatusText = "Listening";
        State = OverlayState.Listening;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowProcessing()
    {
        _timer.Stop();
        StatusText = "Transcribing…";
        State = OverlayState.Processing;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowRetrying()
    {
        _timer.Stop();
        StatusText = "Retrying…";
        State = OverlayState.Processing;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The dictation is finished and its last piece has been typed.</summary>
    /// <param name="separator">What was typed between the previous piece and this one.</param>
    public void ShowPreview(string text, string separator = "")
    {
        _timer.Stop();
        _finishWhenSettled = false;
        PendingSegments = 0;
        AppendTranscript(separator, text);
        StatusText = "Pasted";
        State = OverlayState.Preview;
        ContentChanged?.Invoke(this, EventArgs.Empty);

        StartTimer(PreviewHoldMs());
    }

    /// <summary>A piece of the dictation has been sent off while the user keeps talking.</summary>
    public void SegmentQueued()
    {
        if (!IsSessionRunning)
        {
            return;
        }

        PendingSegments++;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A piece that was sent off has come back, typed or not.</summary>
    public void SegmentSettled()
    {
        if (PendingSegments == 0)
        {
            return;
        }

        PendingSegments--;
        if (PendingSegments == 0 && _finishWhenSettled)
        {
            Finish();
            return;
        }

        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The user stopped a segmented dictation on a pause, so there is no tail to transcribe.
    /// Pieces may still be on their way back; once they are in, the whole text is shown as
    /// pasted, the same way a dictation that ended mid-sentence finishes.
    /// </summary>
    public void EndSegmentedSession()
    {
        if (!IsSessionRunning)
        {
            return;
        }

        if (PendingSegments > 0)
        {
            _finishWhenSettled = true;
            ShowProcessing();
            return;
        }

        Finish();
    }

    /// <summary>
    /// Shows a piece that has just been typed while the session keeps running. Unlike
    /// <see cref="ShowPreview"/> this keeps the current state, so the waveform keeps moving and
    /// no auto-hide timer takes the overlay away mid-session. It also lands while the tail is
    /// being transcribed, so a piece that comes back after the key is released still shows.
    /// </summary>
    /// <param name="separator">What was typed between the previous piece and this one.</param>
    public void ShowSegment(string separator, string text)
    {
        if (!IsSessionRunning)
        {
            return;
        }

        AppendTranscript(separator, text);
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One piece of a running dictation failed. Shown briefly above the line; the session and the
    /// listening tab carry on, because the rest of what the user says still works.
    /// </summary>
    public void ShowSegmentProblem(string message)
    {
        if (!IsSessionRunning)
        {
            return;
        }

        ProblemText = message;
        _problemTimer.Stop();
        _problemTimer.Interval = TimeSpan.FromMilliseconds(ProblemHoldMs);
        _problemTimer.Start();
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowError(string message)
    {
        _timer.Stop();
        _problemTimer.Stop();
        StatusText = message;
        ProblemText = string.Empty;
        _finishWhenSettled = false;
        PendingSegments = 0;
        ClearTranscript();
        State = OverlayState.Error;
        ContentChanged?.Invoke(this, EventArgs.Empty);
        StartTimer(ErrorHoldMs);
    }

    /// <summary>A short neutral message such as "no speech heard".</summary>
    public void ShowNotice(string message)
    {
        _timer.Stop();
        _problemTimer.Stop();
        StatusText = message;
        ProblemText = string.Empty;
        _finishWhenSettled = false;
        PendingSegments = 0;
        ClearTranscript();
        State = OverlayState.Notice;
        ContentChanged?.Invoke(this, EventArgs.Empty);
        StartTimer(NoticeHoldMs);
    }

    public void UpdateLevel(double normalized)
    {
        if (_state != OverlayState.Listening)
        {
            return;
        }

        // Fast attack, slow decay, so the waveform tracks speech instead of flickering.
        double clamped = Math.Clamp(normalized, 0, 1);
        Level = Math.Max(clamped, Level * 0.85);
    }

    public void HideNow()
    {
        _timer.Stop();
        _problemTimer.Stop();
        State = OverlayState.Hidden;
        _finishWhenSettled = false;
        PendingSegments = 0;
        ProblemText = string.Empty;
        ClearTranscript();
        StatusText = string.Empty;
        Level = 0;
    }

    private bool IsSessionRunning => _state is OverlayState.Listening or OverlayState.Processing;

    private void Finish()
    {
        _finishWhenSettled = false;
        if (!_transcript.HasText)
        {
            HideNow();
            return;
        }

        _timer.Stop();
        StatusText = "Pasted";
        State = OverlayState.Preview;
        ContentChanged?.Invoke(this, EventArgs.Empty);
        StartTimer(PreviewHoldMs());
    }

    /// <summary>Longer text stays up longer, because reading it is the point.</summary>
    private int PreviewHoldMs() => Math.Clamp(
        PreviewMinHoldMs + (TranscriptText.Length * PreviewMsPerChar),
        PreviewMinHoldMs,
        PreviewMaxHoldMs);

    private void AppendTranscript(string separator, string text)
    {
        _transcript.Append(separator, text);
        TranscriptRevision++;
    }

    private void ClearTranscript()
    {
        if (!_transcript.HasText)
        {
            return;
        }

        _transcript.Clear();
        TranscriptRevision++;
    }

    private void StartTimer(int milliseconds)
    {
        _timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _timer.Start();
    }
}
