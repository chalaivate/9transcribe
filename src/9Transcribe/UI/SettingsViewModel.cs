using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NineTranscribe.Api;
using NineTranscribe.Audio;
using NineTranscribe.Core;
using NineTranscribe.Diagnostics;
using NineTranscribe.Hotkeys;
using NineTranscribe.Settings;
using NineTranscribe.Tray;

namespace NineTranscribe.UI;

/// <summary>
/// Backs the whole settings window. Every control applies immediately: edits land in the
/// model, a short debounce collapses a burst of keystrokes into one write, and the changed
/// settings are pushed straight back into the running components.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private const int SaveDebounceMs = 500;

    private readonly SettingsStore _store;
    private readonly AudioRecorder _recorder;
    private readonly HotkeyManager _hotkeys;
    private readonly OpenAiTranscriptionClient _api;
    private readonly DictationController _controller;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _testCountdown;
    private readonly Dispatcher _dispatcher;

    private AppSettings _settings;
    private bool _isMonitoring;

    // Written from the audio callback thread, read by the meter timer on the UI thread.
    private volatile int _latestLevelPermille;
    private volatile bool _latestIsSpeech;

    private string _statusText = string.Empty;
    private string _apiTestMessage = string.Empty;
    private bool _apiTestSucceeded;
    private bool _hasApiTestResult;
    private bool _isTestingApi;
    private string? _pendingApiKey;
    private string _testTranscript = string.Empty;
    private bool _isTestRecording;
    private string _testButtonText = "ทดสอบอัดเสียง (3 วินาที)";
    private double _meterLevel;
    private bool _meterIsSpeech;
    private HotkeyField _capturingField = HotkeyField.None;
    private string _captureHint = string.Empty;
    private string _hotkeyWarning = string.Empty;
    private int _testSecondsLeft;

    public SettingsViewModel(
        SettingsStore store,
        AudioRecorder recorder,
        HotkeyManager hotkeys,
        OpenAiTranscriptionClient api,
        DictationController controller)
    {
        _store = store;
        _recorder = recorder;
        _hotkeys = hotkeys;
        _api = api;
        _controller = controller;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _settings = store.Current.Clone();

        Replacements = new ObservableCollection<ReplacementRow>(
            _settings.Replacements.Select(ReplacementRow.From));
        Replacements.CollectionChanged += OnReplacementsChanged;
        foreach (ReplacementRow row in Replacements)
        {
            row.PropertyChanged += OnReplacementRowChanged;
        }

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SaveDebounceMs) };
        _saveTimer.Tick += (_, _) => CommitSave();

        // The capture thread updates a volatile field and the UI pulls it at a fixed rate;
        // dispatching per audio frame would flood the dispatcher queue.
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += (_, _) => PullMeter();

        _testCountdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _testCountdown.Tick += OnTestCountdownTick;

        TestApiKeyCommand = new AsyncRelayCommand(TestApiKeyAsync, () => !IsTestingApi);
        TestRecordCommand = new AsyncRelayCommand(TestRecordAsync, () => !IsTestRecording);
        CalibrateCommand = new RelayCommand(Calibrate);
        AddReplacementCommand = new RelayCommand(() => Replacements.Add(new ReplacementRow()));
        ClearPushToTalkCommand = new RelayCommand(() => SetHotkey(HotkeyField.PushToTalk, HotkeyBinding.None()));
        ClearToggleCommand = new RelayCommand(() => SetHotkey(HotkeyField.Toggle, HotkeyBinding.None()));
        CapturePushToTalkCommand = new RelayCommand(() => BeginCapture(HotkeyField.PushToTalk));
        CaptureToggleCommand = new RelayCommand(() => BeginCapture(HotkeyField.Toggle));

        _hotkeys.CapturePreview += OnCapturePreview;
        _hotkeys.GestureCaptured += OnGestureCaptured;
        _hotkeys.CaptureCancelled += OnCaptureCancelled;
        _recorder.LevelChanged += OnLevelChanged;
        _recorder.RecordingCompleted += OnTestRecordingCompleted;
        _recorder.Error += OnRecorderError;

        RefreshDevices();
        UpdateHotkeyWarning();
    }

    private enum HotkeyField
    {
        None,
        PushToTalk,
        Toggle,
    }

    public IReadOnlyList<ModelCapabilities> Models => ModelCapabilities.Known;

    public IReadOnlyList<ChoiceItem<AppTheme>> ThemeChoices { get; } = new[]
    {
        new ChoiceItem<AppTheme>(AppTheme.System, "ตามระบบ"),
        new ChoiceItem<AppTheme>(AppTheme.Light, "สว่าง"),
        new ChoiceItem<AppTheme>(AppTheme.Dark, "มืด"),
    };

    public IReadOnlyList<ChoiceItem<TrailingText>> TrailingChoices { get; } = new[]
    {
        new ChoiceItem<TrailingText>(TrailingText.None, "ไม่เติมอะไร (แนะนำสำหรับภาษาไทย)"),
        new ChoiceItem<TrailingText>(TrailingText.Smart, "เติมเว้นวรรคเมื่อลงท้ายด้วยอังกฤษหรือตัวเลข"),
        new ChoiceItem<TrailingText>(TrailingText.Space, "เติมเว้นวรรคเสมอ"),
        new ChoiceItem<TrailingText>(TrailingText.NewLine, "ขึ้นบรรทัดใหม่"),
    };

    public ObservableCollection<ReplacementRow> Replacements { get; }

    public ObservableCollection<MicrophoneChoice> Microphones { get; } = new();

    public IAsyncRelayCommand TestApiKeyCommand { get; }

    public IAsyncRelayCommand TestRecordCommand { get; }

    public IRelayCommand CalibrateCommand { get; }

    public IRelayCommand AddReplacementCommand { get; }

    public IRelayCommand ClearPushToTalkCommand { get; }

    public IRelayCommand ClearToggleCommand { get; }

    public IRelayCommand CapturePushToTalkCommand { get; }

    public IRelayCommand CaptureToggleCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // ---- General ----------------------------------------------------------------

    public OverlayPosition Anchor
    {
        get => _settings.OverlayPosition;
        set
        {
            if (_settings.OverlayPosition == value)
            {
                return;
            }

            _settings.OverlayPosition = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOverlayTop));
            OnPropertyChanged(nameof(IsOverlayBottom));
            OnPropertyChanged(nameof(IsOverlayLeft));
            OnPropertyChanged(nameof(IsOverlayRight));
            OnPropertyChanged(nameof(IsOverlayTopLeft));
            OnPropertyChanged(nameof(IsOverlayTopRight));
            OnPropertyChanged(nameof(IsOverlayBottomLeft));
            OnPropertyChanged(nameof(IsOverlayBottomRight));
            QueueSave();
        }
    }

    public bool IsOverlayTop
    {
        get => Anchor == OverlayPosition.Top;
        set => SetAnchor(value, OverlayPosition.Top);
    }

    public bool IsOverlayBottom
    {
        get => Anchor == OverlayPosition.Bottom;
        set => SetAnchor(value, OverlayPosition.Bottom);
    }

    public bool IsOverlayLeft
    {
        get => Anchor == OverlayPosition.Left;
        set => SetAnchor(value, OverlayPosition.Left);
    }

    public bool IsOverlayRight
    {
        get => Anchor == OverlayPosition.Right;
        set => SetAnchor(value, OverlayPosition.Right);
    }

    public bool IsOverlayTopLeft
    {
        get => Anchor == OverlayPosition.TopLeft;
        set => SetAnchor(value, OverlayPosition.TopLeft);
    }

    public bool IsOverlayTopRight
    {
        get => Anchor == OverlayPosition.TopRight;
        set => SetAnchor(value, OverlayPosition.TopRight);
    }

    public bool IsOverlayBottomLeft
    {
        get => Anchor == OverlayPosition.BottomLeft;
        set => SetAnchor(value, OverlayPosition.BottomLeft);
    }

    public bool IsOverlayBottomRight
    {
        get => Anchor == OverlayPosition.BottomRight;
        set => SetAnchor(value, OverlayPosition.BottomRight);
    }

    public bool ShowOverlay
    {
        get => _settings.ShowOverlay;
        set => Assign(value, _settings.ShowOverlay, v => _settings.ShowOverlay = v);
    }

    public bool UseClipboard
    {
        get => _settings.InsertionMethod == InsertionMethod.ClipboardPaste;
        set
        {
            InsertionMethod method = value ? InsertionMethod.ClipboardPaste : InsertionMethod.UnicodeTyping;
            if (_settings.InsertionMethod == method)
            {
                return;
            }

            _settings.InsertionMethod = method;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UseTyping));
            QueueSave();
        }
    }

    public bool UseTyping
    {
        get => _settings.InsertionMethod == InsertionMethod.UnicodeTyping;
        set
        {
            if (value)
            {
                UseClipboard = false;
            }
        }
    }

    public bool RestoreClipboard
    {
        get => _settings.RestoreClipboard;
        set => Assign(value, _settings.RestoreClipboard, v => _settings.RestoreClipboard = v);
    }

    public double VadEnterDbfs
    {
        get => _settings.Vad.EnterDbfs;
        set
        {
            if (Math.Abs(_settings.Vad.EnterDbfs - value) < 0.01)
            {
                return;
            }

            _settings.Vad.EnterDbfs = value;
            OnPropertyChanged();
            QueueSave();
        }
    }

    public double VadHangoverSeconds
    {
        get => _settings.Vad.HangoverMs / 1000.0;
        set
        {
            int ms = (int)Math.Round(value * 1000);
            if (_settings.Vad.HangoverMs == ms)
            {
                return;
            }

            _settings.Vad.HangoverMs = ms;
            OnPropertyChanged();
            QueueSave();
        }
    }

    public bool SegmentOnPause
    {
        get => _settings.SegmentOnPause;
        set => Assign(value, _settings.SegmentOnPause, v => _settings.SegmentOnPause = v);
    }

    public int IdleStopSeconds
    {
        get => _settings.IdleStopSeconds;
        set
        {
            if (_settings.IdleStopSeconds == value)
            {
                return;
            }

            _settings.IdleStopSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IdleStopText));
            QueueSave();
        }
    }

    public string IdleStopText => _settings.IdleStopSeconds <= 0
        ? "ปิด"
        : $"{_settings.IdleStopSeconds} วิ";

    public AppTheme Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value)
            {
                return;
            }

            _settings.Theme = value;
            OnPropertyChanged();
            ThemeChanged?.Invoke(this, value);
            QueueSave();
        }
    }

    public bool SaveHistoryToDisk
    {
        get => _settings.SaveHistoryToDisk;
        set => Assign(value, _settings.SaveHistoryToDisk, v => _settings.SaveHistoryToDisk = v);
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value)
            {
                return;
            }

            _settings.StartWithWindows = value;
            StartupRegistrar.SetEnabled(value);
            OnPropertyChanged();
            QueueSave();
        }
    }

    public TrailingText TrailingMode
    {
        get => _settings.TrailingText;
        set => Assign(value, _settings.TrailingText, v => _settings.TrailingText = v);
    }

    // ---- API --------------------------------------------------------------------

    public string Model
    {
        get => _settings.Model;
        set
        {
            if (string.Equals(_settings.Model, value, StringComparison.Ordinal))
            {
                return;
            }

            _settings.Model = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VocabularyBudgetText));
            QueueSave();
        }
    }

    public bool HasStoredApiKey => !string.IsNullOrEmpty(_settings.ApiKeyProtected);

    public string ApiKeyHint => HasStoredApiKey
        ? "•••• บันทึกไว้แล้ว (ป้อนใหม่เพื่อเปลี่ยน)"
        : "ยังไม่ได้ตั้งค่า API Key";

    public string ApiTestMessage
    {
        get => _apiTestMessage;
        private set => SetProperty(ref _apiTestMessage, value);
    }

    public bool ApiTestSucceeded
    {
        get => _apiTestSucceeded;
        private set => SetProperty(ref _apiTestSucceeded, value);
    }

    public bool HasApiTestResult
    {
        get => _hasApiTestResult;
        private set => SetProperty(ref _hasApiTestResult, value);
    }

    public bool IsTestingApi
    {
        get => _isTestingApi;
        private set
        {
            if (SetProperty(ref _isTestingApi, value))
            {
                TestApiKeyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Called from the code-behind, because PasswordBox.Password is not bindable.</summary>
    public void SetApiKey(string? key)
    {
        _pendingApiKey = key;
        _settings.ApiKeyProtected = ApiKeyProtector.Protect(key);
        OnPropertyChanged(nameof(HasStoredApiKey));
        OnPropertyChanged(nameof(ApiKeyHint));
        QueueSave();
    }

    // ---- Hotkeys ----------------------------------------------------------------

    public string PushToTalkDisplay => HotkeyDisplay.Describe(_settings.Hotkeys.PushToTalk);

    public string ToggleDisplay => HotkeyDisplay.Describe(_settings.Hotkeys.Toggle);

    public bool IsCapturing => _capturingField != HotkeyField.None;

    public string CaptureHint
    {
        get => _captureHint;
        private set => SetProperty(ref _captureHint, value);
    }

    public string HotkeyWarning
    {
        get => _hotkeyWarning;
        private set
        {
            if (SetProperty(ref _hotkeyWarning, value))
            {
                OnPropertyChanged(nameof(HasHotkeyWarning));
            }
        }
    }

    public bool HasHotkeyWarning => _hotkeyWarning.Length > 0;

    // ---- Vocabulary -------------------------------------------------------------

    public string VocabularyText
    {
        get => string.Join(Environment.NewLine, _settings.CustomVocabulary);
        set
        {
            List<string> parsed = PromptBuilder.ParseVocabulary(value);
            if (parsed.SequenceEqual(_settings.CustomVocabulary, StringComparer.Ordinal))
            {
                return;
            }

            _settings.CustomVocabulary = parsed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VocabularyBudgetText));
            OnPropertyChanged(nameof(IsVocabularyOverBudget));
            QueueSave();
        }
    }

    public string VocabularyBudgetText
    {
        get
        {
            ModelCapabilities capabilities = ModelCapabilities.For(_settings.Model);
            int used = PromptBuilder.MeasureLength(
                _settings.PromptPrefix,
                _settings.CustomVocabulary,
                capabilities);
            return $"ใช้ไป {used}/{capabilities.PromptCharBudget} ตัวอักษร";
        }
    }

    public bool IsVocabularyOverBudget
    {
        get
        {
            ModelCapabilities capabilities = ModelCapabilities.For(_settings.Model);
            int raw = (_settings.PromptPrefix?.Trim().Length ?? 0)
                + _settings.CustomVocabulary.Sum(term => term.Length + 2);
            return raw > capabilities.PromptCharBudget;
        }
    }

    // ---- Test tab ---------------------------------------------------------------

    public MicrophoneChoice? SelectedMicrophone
    {
        get => Microphones.FirstOrDefault(m =>
            string.Equals(m.FriendlyName, _settings.MicDeviceFriendlyName, StringComparison.Ordinal))
            ?? Microphones.FirstOrDefault();
        set
        {
            if (value is null || string.Equals(_settings.MicDeviceFriendlyName, value.FriendlyName, StringComparison.Ordinal))
            {
                return;
            }

            _settings.MicDeviceFriendlyName = value.FriendlyName;
            OnPropertyChanged();
            QueueSave();

            if (_isMonitoring)
            {
                StopMonitoring();
                StartMonitoring();
            }
        }
    }

    public double MeterLevel
    {
        get => _meterLevel;
        private set => SetProperty(ref _meterLevel, value);
    }

    public bool MeterIsSpeech
    {
        get => _meterIsSpeech;
        private set => SetProperty(ref _meterIsSpeech, value);
    }

    public string TestTranscript
    {
        get => _testTranscript;
        private set => SetProperty(ref _testTranscript, value);
    }

    public string TestButtonText
    {
        get => _testButtonText;
        private set => SetProperty(ref _testButtonText, value);
    }

    public bool IsTestRecording
    {
        get => _isTestRecording;
        private set
        {
            if (SetProperty(ref _isTestRecording, value))
            {
                TestRecordCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public event EventHandler<AppTheme>? ThemeChanged;

    /// <summary>Raised after a save so the running app picks up the new configuration.</summary>
    public event EventHandler<AppSettings>? SettingsApplied;

    public void StartMonitoring()
    {
        if (_isMonitoring)
        {
            return;
        }

        try
        {
            _recorder.StartMonitoring(_settings.MicDeviceFriendlyName, _settings.Vad);
            _isMonitoring = true;
            _meterTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"Microphone monitoring could not start: {ex.Message}");
            StatusText = "เปิดไมโครโฟนไม่ได้";
        }
    }

    /// <summary>
    /// Called when the test tab loses visibility. The microphone stays closed the rest of
    /// the time so Windows does not show the "in use" indicator while the app idles.
    /// </summary>
    public void StopMonitoring()
    {
        if (!_isMonitoring)
        {
            return;
        }

        _isMonitoring = false;
        _meterTimer.Stop();
        _recorder.StopMonitoring();
        MeterLevel = 0;
        MeterIsSpeech = false;
    }

    public void RefreshDevices()
    {
        Microphones.Clear();
        Microphones.Add(new MicrophoneChoice(null, "(อุปกรณ์เริ่มต้นของระบบ)"));

        foreach (AudioDeviceInfo device in AudioRecorder.EnumerateDevices())
        {
            string label = device.IsDefault ? $"{device.FriendlyName} — ค่าเริ่มต้น" : device.FriendlyName;
            Microphones.Add(new MicrophoneChoice(device.FriendlyName, label));
        }

        OnPropertyChanged(nameof(SelectedMicrophone));
    }

    /// <summary>Writes any pending edit immediately, e.g. when the window is closing.</summary>
    public void Flush()
    {
        if (_saveTimer.IsEnabled)
        {
            CommitSave();
        }
    }

    public void Dispose()
    {
        StopMonitoring();
        CancelCapture();

        _hotkeys.CapturePreview -= OnCapturePreview;
        _hotkeys.GestureCaptured -= OnGestureCaptured;
        _hotkeys.CaptureCancelled -= OnCaptureCancelled;
        _recorder.LevelChanged -= OnLevelChanged;
        _recorder.RecordingCompleted -= OnTestRecordingCompleted;
        _recorder.Error -= OnRecorderError;

        _saveTimer.Stop();
        _meterTimer.Stop();
        _testCountdown.Stop();
    }

    private void SetAnchor(bool selected, OverlayPosition position)
    {
        if (selected)
        {
            Anchor = position;
        }
    }

    private void Assign<T>(T value, T current, Action<T> apply, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
        {
            return;
        }

        apply(value);
        OnPropertyChanged(propertyName);
        QueueSave();
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void CommitSave()
    {
        _saveTimer.Stop();

        _settings.Replacements = Replacements
            .Where(row => row.Find.Length > 0)
            .Select(row => row.ToSetting())
            .ToList();

        _store.Save(_settings);
        _settings = _store.Current.Clone();

        SettingsApplied?.Invoke(this, _store.Current);
        StatusText = "บันทึกแล้ว";
    }

    private void OnReplacementsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ReplacementRow row in e.OldItems.OfType<ReplacementRow>())
            {
                row.PropertyChanged -= OnReplacementRowChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ReplacementRow row in e.NewItems.OfType<ReplacementRow>())
            {
                row.PropertyChanged += OnReplacementRowChanged;
            }
        }

        QueueSave();
    }

    private void OnReplacementRowChanged(object? sender, PropertyChangedEventArgs e) => QueueSave();

    // ---- Hotkey capture ---------------------------------------------------------

    private void BeginCapture(HotkeyField field)
    {
        _capturingField = field;
        _hotkeys.CaptureMode = true;
        CaptureHint = "กดคีย์ที่ต้องการ… (Esc ยกเลิก)";
        OnPropertyChanged(nameof(IsCapturing));
    }

    private void CancelCapture()
    {
        if (_capturingField == HotkeyField.None)
        {
            return;
        }

        _capturingField = HotkeyField.None;
        _hotkeys.CaptureMode = false;
        CaptureHint = string.Empty;
        OnPropertyChanged(nameof(IsCapturing));
    }

    private void OnCapturePreview(object? sender, string preview) => CaptureHint = preview + " …";

    private void OnCaptureCancelled(object? sender, EventArgs e) => CancelCapture();

    private void OnGestureCaptured(object? sender, HotkeyBinding binding)
    {
        HotkeyField field = _capturingField;
        CancelCapture();

        if (field != HotkeyField.None)
        {
            SetHotkey(field, binding);
        }
    }

    private void SetHotkey(HotkeyField field, HotkeyBinding binding)
    {
        if (field == HotkeyField.PushToTalk)
        {
            _settings.Hotkeys.PushToTalk = binding;
            OnPropertyChanged(nameof(PushToTalkDisplay));
        }
        else
        {
            _settings.Hotkeys.Toggle = binding;
            OnPropertyChanged(nameof(ToggleDisplay));
        }

        UpdateHotkeyWarning();
        QueueSave();
    }

    private void UpdateHotkeyWarning()
    {
        HotkeyBinding ptt = _settings.Hotkeys.PushToTalk;
        HotkeyBinding toggle = _settings.Hotkeys.Toggle;

        if (ptt.IsEnabled && toggle.IsEnabled && ptt.SameGesture(toggle))
        {
            HotkeyWarning = "คีย์ลัดทั้งสองซ้ำกัน กรุณาเลือกคนละปุ่ม";
            return;
        }

        foreach (HotkeyBinding binding in new[] { ptt, toggle })
        {
            if (binding.IsEnabled
                && HotkeyDisplay.IsRisky((ushort)binding.Vk, (HotkeyModifiers)binding.Mods, out string warning))
            {
                HotkeyWarning = warning;
                return;
            }
        }

        HotkeyWarning = string.Empty;
    }

    // ---- API test ---------------------------------------------------------------

    private async Task TestApiKeyAsync()
    {
        IsTestingApi = true;
        HasApiTestResult = false;

        try
        {
            string? key = _pendingApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                key = ApiKeyProtector.Unprotect(_settings.ApiKeyProtected);
            }

            ApiKeyTestResult result = await _api
                .TestApiKeyAsync(key, _settings.Model, CancellationToken.None)
                .ConfigureAwait(true);

            ApiTestSucceeded = result.Success;
            ApiTestMessage = result.MessageThai;
            HasApiTestResult = true;
        }
        finally
        {
            IsTestingApi = false;
        }
    }

    // ---- Microphone meter and test recording ------------------------------------

    private void OnLevelChanged(object? sender, LevelEventArgs e)
    {
        _latestLevelPermille = (int)Math.Clamp(e.Normalized * 1000, 0, 1000);
        _latestIsSpeech = e.IsSpeech;
    }

    private void PullMeter()
    {
        MeterLevel = _latestLevelPermille / 1000.0;
        MeterIsSpeech = _latestIsSpeech;
    }

    private void Calibrate()
    {
        double floor = _recorder.LastNoiseFloorDbfs;
        if (double.IsNaN(floor) || double.IsInfinity(floor) || floor <= -89)
        {
            StatusText = "ยังวัดเสียงรบกวนไม่ได้ — เปิดแท็บนี้ค้างไว้สักครู่แล้วลองใหม่";
            return;
        }

        VadEnterDbfs = Math.Clamp(floor + 12.0, -50.0, -20.0);
        StatusText = $"ตั้งความไวเป็น {VadEnterDbfs:0.#} dB ตามเสียงรบกวนในห้อง";
    }

    private Task TestRecordAsync()
    {
        if (IsTestRecording)
        {
            return Task.CompletedTask;
        }

        StopMonitoring();
        TestTranscript = string.Empty;
        IsTestRecording = true;

        var options = new RecordingOptions(
            _settings.MicDeviceFriendlyName,
            AutoStopOnSilence: false,
            _settings.Vad,
            MaxDurationSeconds: 5,
            MinUtteranceMs: _settings.MinUtteranceMs);

        StartOutcome outcome;
        try
        {
            outcome = _recorder.StartRecording(options);
        }
        catch (Exception ex)
        {
            Log.Warn($"Test recording could not start: {ex.Message}");
            FinishTestRecording("เริ่มอัดเสียงไม่ได้ กรุณาตรวจสอบไมโครโฟน");
            return Task.CompletedTask;
        }

        // Only run the countdown if the test really owns the microphone, or it would end up
        // stopping a dictation it never started.
        if (outcome != StartOutcome.Started)
        {
            FinishTestRecording("เริ่มอัดเสียงไม่ได้ ไมโครโฟนอาจถูกใช้งานอยู่");
            return Task.CompletedTask;
        }

        _testSecondsLeft = 3;
        TestButtonText = $"กำลังอัด… {_testSecondsLeft}";
        _testCountdown.Start();

        return Task.CompletedTask;
    }

    private void OnTestCountdownTick(object? sender, EventArgs e)
    {
        _testSecondsLeft--;
        if (_testSecondsLeft > 0)
        {
            TestButtonText = $"กำลังอัด… {_testSecondsLeft}";
            return;
        }

        _testCountdown.Stop();

        if (IsTestRecording)
        {
            _recorder.StopRecording(StopReason.UserStopped);
        }
    }

    private void OnRecorderError(object? sender, RecorderErrorEventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnRecorderError(sender, e));
            return;
        }

        if (e.Kind == RecorderErrorKind.DeviceFallback)
        {
            StatusText = e.MessageThai;
            return;
        }

        // Without this the test button would stay stuck on its countdown forever, because a
        // failed open never produces a RecordingCompleted.
        if (IsTestRecording)
        {
            FinishTestRecording(e.MessageThai);
        }
        else
        {
            StatusText = e.MessageThai;
        }
    }

    private void OnTestRecordingCompleted(object? sender, RecordingCompletedEventArgs e)
    {
        // The recorder finalizes on a thread-pool thread; every field below is data-bound.
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnTestRecordingCompleted(sender, e));
            return;
        }

        if (!IsTestRecording)
        {
            return;
        }

        if (!e.HasSpeech || e.WavBytes.Length == 0)
        {
            FinishTestRecording("ไม่พบเสียงพูด — ลองพูดดังขึ้นหรือปรับความไว");
            return;
        }

        TestButtonText = "กำลังถอดเสียง…";
        _ = TranscribeTestAsync(e.WavBytes);
    }

    private async Task TranscribeTestAsync(byte[] wav)
    {
        try
        {
            string text = await _controller
                .TranscribeForTestAsync(wav, CancellationToken.None)
                .ConfigureAwait(true);
            FinishTestRecording(text.Length > 0 ? text : "ถอดเสียงแล้วแต่ไม่ได้ข้อความ");
        }
        catch (TranscriptionException ex)
        {
            FinishTestRecording(ex.UserMessageThai);
        }
        catch (Exception ex)
        {
            Log.Error("Test transcription failed", ex);
            FinishTestRecording("ถอดเสียงไม่สำเร็จ กรุณาดูรายละเอียดใน log");
        }
    }

    private void FinishTestRecording(string message)
    {
        _testCountdown.Stop();
        IsTestRecording = false;
        TestButtonText = "ทดสอบอัดเสียง (3 วินาที)";
        TestTranscript = message;
    }
}

/// <summary>An entry in the microphone picker; a null name means the Windows default.</summary>
public sealed record MicrophoneChoice(string? FriendlyName, string Label);

/// <summary>A value plus the Thai label shown for it in a picker.</summary>
public sealed record ChoiceItem<T>(T Value, string Label);
