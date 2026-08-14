using System.IO;
using NineTranscribe.Settings;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory;

    public SettingsTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "9transcribe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Load_WithNoFile_ReturnsDefaults()
    {
        var store = new SettingsStore(_directory);

        AppSettings settings = store.Load();

        Assert.Equal("gpt-4o-transcribe", settings.Model);
        Assert.Equal("th", settings.Language);
        Assert.Equal(InsertionMethod.ClipboardPaste, settings.InsertionMethod);
        Assert.Equal(TrailingText.None, settings.TrailingText);
        Assert.Equal(OverlayPosition.Bottom, settings.OverlayPosition);
        Assert.Equal(0xA3, settings.Hotkeys.PushToTalk.Vk);
        Assert.Equal(0x77, settings.Hotkeys.Toggle.Vk);
        Assert.Null(store.LoadWarning);
    }

    [Fact]
    public void Save_OnFirstRun_CreatesFileWithoutThrowing()
    {
        var store = new SettingsStore(_directory);
        AppSettings settings = store.Load();
        settings.Model = "whisper-1";

        store.Save(settings);

        Assert.True(File.Exists(store.FilePath));
        Assert.Null(store.LoadWarning);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var store = new SettingsStore(_directory);
        AppSettings settings = store.Load();
        settings.Model = "gpt-4o-mini-transcribe";
        settings.CustomVocabulary.Add("Power BI");
        settings.CustomVocabulary.Add("9Expert Training");
        settings.Replacements.Add(new ReplacementRuleSetting { Find = "พาวเวอร์บีไอ", Replace = "Power BI" });
        settings.TrailingText = TrailingText.Smart;
        settings.OverlayPosition = OverlayPosition.TopRight;
        settings.Vad.HangoverMs = 900;
        settings.Hotkeys.Toggle.Vk = 0x78;

        store.Save(settings);
        AppSettings reloaded = new SettingsStore(_directory).Load();

        Assert.Equal("gpt-4o-mini-transcribe", reloaded.Model);
        Assert.Equal(new[] { "Power BI", "9Expert Training" }, reloaded.CustomVocabulary);
        Assert.Equal("Power BI", Assert.Single(reloaded.Replacements).Replace);
        Assert.Equal(TrailingText.Smart, reloaded.TrailingText);
        Assert.Equal(OverlayPosition.TopRight, reloaded.OverlayPosition);
        Assert.Equal(900, reloaded.Vad.HangoverMs);
        Assert.Equal(0x78, reloaded.Hotkeys.Toggle.Vk);
    }

    [Fact]
    public void SaveTwice_ReplacesTheExistingFile()
    {
        var store = new SettingsStore(_directory);
        AppSettings settings = store.Load();

        store.Save(settings);
        settings.Model = "whisper-1";
        store.Save(settings);

        Assert.Equal("whisper-1", new SettingsStore(_directory).Load().Model);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void Load_WithCorruptFile_QuarantinesItAndUsesDefaults()
    {
        var store = new SettingsStore(_directory);
        File.WriteAllText(store.FilePath, "{ this is not json");

        AppSettings settings = store.Load();

        Assert.Equal("gpt-4o-transcribe", settings.Model);
        Assert.True(File.Exists(Path.Combine(_directory, "settings.bad.json")));
        Assert.NotNull(store.LoadWarning);
    }

    [Fact]
    public void Load_WithUnknownFields_KeepsDefaultsForMissingOnes()
    {
        var store = new SettingsStore(_directory);
        File.WriteAllText(
            store.FilePath,
            """{ "Model": "whisper-1", "SomeFutureField": 42 }""");

        AppSettings settings = store.Load();

        Assert.Equal("whisper-1", settings.Model);
        Assert.Equal("th", settings.Language);
        Assert.Equal(1200, settings.Vad.HangoverMs);
    }

    [Fact]
    public void Normalize_ClampsOutOfRangeValues()
    {
        var settings = new AppSettings
        {
            MaxRecordingSeconds = 100_000,
            ApiTimeoutSeconds = 1,
            TypingIntervalMs = 900,
            Temperature = 5,
        };
        settings.Vad.HangoverMs = 99_999;

        settings.Normalize();

        Assert.Equal(720, settings.MaxRecordingSeconds);
        Assert.Equal(5, settings.ApiTimeoutSeconds);
        Assert.Equal(50, settings.TypingIntervalMs);
        Assert.Equal(1.0, settings.Temperature);
        Assert.Equal(5000, settings.Vad.HangoverMs);
    }

    [Fact]
    public void Clone_DoesNotShareCollections()
    {
        var settings = new AppSettings();
        settings.CustomVocabulary.Add("DAX");

        AppSettings copy = settings.Clone();
        copy.CustomVocabulary.Add("Copilot");

        Assert.Single(settings.CustomVocabulary);
        Assert.Equal(2, copy.CustomVocabulary.Count);
    }
}
