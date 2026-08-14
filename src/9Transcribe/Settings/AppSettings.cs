using System.Text.Json.Serialization;

namespace NineTranscribe.Settings;

/// <summary>
/// The whole persisted configuration. Serialized to
/// <c>%APPDATA%\9Transcribe\settings.json</c> by <see cref="SettingsStore"/>.
/// Every member needs a sensible default: fields missing from an older file are
/// simply left at their default, which is why additive changes need no migration.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Base64 of the DPAPI-protected API key. Never the key itself.</summary>
    public string? ApiKeyProtected { get; set; }

    public string Model { get; set; } = "gpt-4o-transcribe";

    /// <summary>ISO-639-1 code sent to the API. Empty string means auto-detect.</summary>
    public string Language { get; set; } = "th";

    public double Temperature { get; set; }

    public string PromptPrefix { get; set; } = "บทสนทนาภาษาไทยปนคำศัพท์อังกฤษ: ";

    public List<string> CustomVocabulary { get; set; } = new();

    public List<ReplacementRuleSetting> Replacements { get; set; } = new();

    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>Capture device friendly name; null follows the Windows default device.</summary>
    public string? MicDeviceFriendlyName { get; set; }

    public InsertionMethod InsertionMethod { get; set; } = InsertionMethod.ClipboardPaste;

    public bool RestoreClipboard { get; set; } = true;

    public int ClipboardRestoreDelayMs { get; set; } = 500;

    public int TypingIntervalMs { get; set; } = 10;

    /// <summary>Process names (without extension) that must never receive a Ctrl+V.</summary>
    public List<string> ForceTypingProcesses { get; set; } = new() { "putty", "kitty" };

    /// <summary>Process names that swallow injected Unicode and must use the clipboard.</summary>
    public List<string> ForceClipboardProcesses { get; set; } = new() { "mstsc", "vmware-vmx" };

    public TrailingText TrailingText { get; set; } = TrailingText.None;

    public bool CollapseSpaces { get; set; } = true;

    public OverlayPosition OverlayPosition { get; set; } = OverlayPosition.Bottom;

    public bool ShowOverlay { get; set; } = true;

    public VadSettings Vad { get; set; } = new();

    public int MinUtteranceMs { get; set; } = 300;

    /// <summary>
    /// Type each sentence as soon as the speaker pauses, instead of waiting for the whole session
    /// to end. The pause that counts as a sentence break is <see cref="VadSettings.HangoverMs"/>.
    /// </summary>
    public bool SegmentOnPause { get; set; } = true;

    /// <summary>Stops a session nobody is talking into. Zero disables it.</summary>
    public int IdleStopSeconds { get; set; } = 60;

    public int MaxRecordingSeconds { get; set; } = 600;

    public int ApiTimeoutSeconds { get; set; } = 30;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool StartWithWindows { get; set; }

    public bool HistoryEnabled { get; set; } = true;

    public int HistoryMaxItems { get; set; } = 20;

    /// <summary>Off by default: dictated text is sensitive and stays in memory only.</summary>
    public bool SaveHistoryToDisk { get; set; }

    public AppSettings Clone()
    {
        return new AppSettings
        {
            SchemaVersion = SchemaVersion,
            ApiKeyProtected = ApiKeyProtected,
            Model = Model,
            Language = Language,
            Temperature = Temperature,
            PromptPrefix = PromptPrefix,
            CustomVocabulary = new List<string>(CustomVocabulary),
            Replacements = Replacements.Select(r => r.Clone()).ToList(),
            Hotkeys = Hotkeys.Clone(),
            MicDeviceFriendlyName = MicDeviceFriendlyName,
            InsertionMethod = InsertionMethod,
            RestoreClipboard = RestoreClipboard,
            ClipboardRestoreDelayMs = ClipboardRestoreDelayMs,
            TypingIntervalMs = TypingIntervalMs,
            ForceTypingProcesses = new List<string>(ForceTypingProcesses),
            ForceClipboardProcesses = new List<string>(ForceClipboardProcesses),
            TrailingText = TrailingText,
            CollapseSpaces = CollapseSpaces,
            OverlayPosition = OverlayPosition,
            ShowOverlay = ShowOverlay,
            Vad = Vad.Clone(),
            MinUtteranceMs = MinUtteranceMs,
            SegmentOnPause = SegmentOnPause,
            IdleStopSeconds = IdleStopSeconds,
            MaxRecordingSeconds = MaxRecordingSeconds,
            ApiTimeoutSeconds = ApiTimeoutSeconds,
            Theme = Theme,
            StartWithWindows = StartWithWindows,
            HistoryEnabled = HistoryEnabled,
            HistoryMaxItems = HistoryMaxItems,
            SaveHistoryToDisk = SaveHistoryToDisk,
        };
    }

    /// <summary>
    /// Clamps every numeric field into its supported range. Called after load so a
    /// hand-edited file can never push the recorder or the API past their limits.
    /// </summary>
    public void Normalize()
    {
        Model = string.IsNullOrWhiteSpace(Model) ? "gpt-4o-transcribe" : Model.Trim();
        Language = Language?.Trim() ?? "th";
        Temperature = Math.Clamp(Temperature, 0.0, 1.0);
        PromptPrefix ??= string.Empty;
        CustomVocabulary ??= new List<string>();
        Replacements ??= new List<ReplacementRuleSetting>();
        Hotkeys ??= new HotkeySettings();
        ForceTypingProcesses ??= new List<string>();
        ForceClipboardProcesses ??= new List<string>();
        Vad ??= new VadSettings();
        Vad.Normalize();

        ClipboardRestoreDelayMs = Math.Clamp(ClipboardRestoreDelayMs, 0, 5000);
        TypingIntervalMs = Math.Clamp(TypingIntervalMs, 0, 50);
        MinUtteranceMs = Math.Clamp(MinUtteranceMs, 100, 2000);
        IdleStopSeconds = IdleStopSeconds <= 0 ? 0 : Math.Clamp(IdleStopSeconds, 10, 600);
        // 720 s of 16 kHz mono PCM is ~23 MB, just inside the API's 25 MB limit.
        MaxRecordingSeconds = Math.Clamp(MaxRecordingSeconds, 5, 720);
        ApiTimeoutSeconds = Math.Clamp(ApiTimeoutSeconds, 5, 300);
        HistoryMaxItems = Math.Clamp(HistoryMaxItems, 1, 200);
    }
}

public sealed class ReplacementRuleSetting
{
    public string Find { get; set; } = string.Empty;

    public string Replace { get; set; } = string.Empty;

    public bool MatchCase { get; set; }

    public ReplacementRuleSetting Clone() => new()
    {
        Find = Find,
        Replace = Replace,
        MatchCase = MatchCase,
    };
}

public sealed class HotkeySettings
{
    /// <summary>Hold to record. Right Ctrl by default: inert alone, unused by Kedmanee typing.</summary>
    public HotkeyBinding PushToTalk { get; set; } = HotkeyBinding.RightControl();

    /// <summary>Press to start, press again (or stay silent) to stop. F8 by default.</summary>
    public HotkeyBinding Toggle { get; set; } = HotkeyBinding.F8();

    public HotkeySettings Clone() => new()
    {
        PushToTalk = PushToTalk.Clone(),
        Toggle = Toggle.Clone(),
    };
}

/// <summary>
/// A hotkey stored as a virtual-key code plus a modifier mask. Storing the VK rather
/// than a character keeps bindings stable across the Thai Kedmanee and English layouts,
/// which share physical key positions.
/// </summary>
public sealed class HotkeyBinding
{
    /// <summary>Virtual-key code; 0 means the binding is disabled.</summary>
    public int Vk { get; set; }

    /// <summary>Bit mask of <c>HotkeyModifiers</c>: Ctrl=1, Shift=2, Alt=4, Win=8.</summary>
    public int Mods { get; set; }

    /// <summary>Whether the key event is hidden from the foreground application.</summary>
    public bool Swallow { get; set; }

    /// <summary>Cached display string, regenerated on load; never authoritative.</summary>
    public string Display { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsEnabled => Vk != 0;

    public HotkeyBinding Clone() => new()
    {
        Vk = Vk,
        Mods = Mods,
        Swallow = Swallow,
        Display = Display,
    };

    public bool SameGesture(HotkeyBinding other) => Vk == other.Vk && Mods == other.Mods;

    public static HotkeyBinding None() => new();

    public static HotkeyBinding RightControl() => new()
    {
        Vk = 0xA3, // VK_RCONTROL
        Mods = 0,
        Swallow = false,
        Display = "Right Ctrl",
    };

    public static HotkeyBinding F8() => new()
    {
        Vk = 0x77, // VK_F8
        Mods = 0,
        Swallow = true,
        Display = "F8",
    };
}

/// <summary>
/// Voice-activity detection tuning. The detector is an adaptive RMS gate: speech starts
/// above <see cref="EnterDbfs"/> (or the measured noise floor plus a margin, whichever is
/// higher) and ends after <see cref="HangoverMs"/> below the exit threshold.
/// </summary>
public sealed class VadSettings
{
    public double EnterDbfs { get; set; } = -35.0;

    /// <summary>Exit threshold sits this many dB below the enter threshold (hysteresis).</summary>
    public double ExitOffsetDb { get; set; } = 10.0;

    /// <summary>
    /// Silence required before an utterance is considered finished. Thai dictation has
    /// frequent 400–800 ms inter-phrase pauses, so anything under a second cuts people off.
    /// </summary>
    public int HangoverMs { get; set; } = 1200;

    /// <summary>Track the room's noise floor and raise the enter threshold to match.</summary>
    public bool Adaptive { get; set; } = true;

    public VadSettings Clone() => new()
    {
        EnterDbfs = EnterDbfs,
        ExitOffsetDb = ExitOffsetDb,
        HangoverMs = HangoverMs,
        Adaptive = Adaptive,
    };

    public void Normalize()
    {
        EnterDbfs = Math.Clamp(EnterDbfs, -60.0, -10.0);
        ExitOffsetDb = Math.Clamp(ExitOffsetDb, 3.0, 30.0);
        HangoverMs = Math.Clamp(HangoverMs, 300, 5000);
    }
}
