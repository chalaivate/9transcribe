using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NineTranscribe.Processing;

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
/// Drives the status pill. UI-thread affine — callers marshal before touching it. One timer
/// serves every timed transition, and it is cancelled on each state change, or a stale preview
/// timer would hide a pill that a new dictation has just shown.
/// </summary>
public sealed class OverlayViewModel : ObservableObject
{
    private const int ErrorHoldMs = 4000;
    private const int NoticeHoldMs = 1500;
    private const int PreviewMinHoldMs = 2500;
    private const int PreviewMaxHoldMs = 8000;
    private const int PreviewMsPerChar = 45;

    private readonly DispatcherTimer _timer;

    private OverlayState _state = OverlayState.Hidden;
    private double _level;
    private string _previewText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isToggleMode;
    private DateTime _listeningSince;

    public OverlayViewModel()
    {
        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            HideNow();
        };
    }

    /// <summary>Raised when the pill's content changed enough that it needs re-placing.</summary>
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

    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
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

    public void ShowListening(bool toggleMode)
    {
        _timer.Stop();
        IsToggleMode = toggleMode;
        Level = 0;
        PreviewText = string.Empty;
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

    public void ShowPreview(string text)
    {
        _timer.Stop();
        PreviewText = TranscriptPostProcessor.ForPreview(text.Trim());
        StatusText = "Pasted";
        State = OverlayState.Preview;
        ContentChanged?.Invoke(this, EventArgs.Empty);

        // Longer text stays up longer, because reading it is the point.
        int hold = Math.Clamp(
            PreviewMinHoldMs + (PreviewText.Length * PreviewMsPerChar),
            PreviewMinHoldMs,
            PreviewMaxHoldMs);
        StartTimer(hold);
    }

    /// <summary>
    /// Shows a sentence that has just been typed while the session keeps running. Unlike
    /// <see cref="ShowPreview"/> this stays in the listening state, so the waveform keeps
    /// moving and no auto-hide timer takes the tab away mid-session.
    /// </summary>
    public void ShowSegment(string text)
    {
        if (State != OverlayState.Listening)
        {
            return;
        }

        _timer.Stop();
        PreviewText = TranscriptPostProcessor.ForPreview(text.Trim());
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowError(string message)
    {
        _timer.Stop();
        StatusText = message;
        PreviewText = string.Empty;
        State = OverlayState.Error;
        ContentChanged?.Invoke(this, EventArgs.Empty);
        StartTimer(ErrorHoldMs);
    }

    /// <summary>A short neutral message such as "no speech heard".</summary>
    public void ShowNotice(string message)
    {
        _timer.Stop();
        StatusText = message;
        PreviewText = string.Empty;
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

        // Fast attack, slow decay, so the bars track speech instead of flickering.
        double clamped = Math.Clamp(normalized, 0, 1);
        Level = Math.Max(clamped, Level * 0.85);
    }

    public void HideNow()
    {
        _timer.Stop();
        State = OverlayState.Hidden;
        PreviewText = string.Empty;
        StatusText = string.Empty;
        Level = 0;
    }

    private void StartTimer(int milliseconds)
    {
        _timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _timer.Start();
    }
}
