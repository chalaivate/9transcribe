using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NineTranscribe.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under
/// <c>%APPDATA%\9Transcribe</c>. Writes go through a temp file so a crash mid-save can
/// never leave a half-written settings file behind.
/// </summary>
public sealed class SettingsStore
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    private readonly object _gate = new();

    public SettingsStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory;
        FilePath = Path.Combine(Directory, FileName);
        Current = new AppSettings();
    }

    /// <summary>Raised after a successful save, with the settings that were written.</summary>
    public event EventHandler<AppSettings>? Saved;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "9Transcribe");

    public string Directory { get; }

    public string FilePath { get; }

    /// <summary>The most recently loaded or saved settings. Never null.</summary>
    public AppSettings Current { get; private set; }

    /// <summary>
    /// Set when the last <see cref="Load"/> could not use the file on disk — the Thai text
    /// is shown to the user once, as a tray notification.
    /// </summary>
    public string? LoadWarning { get; private set; }

    public AppSettings Load()
    {
        lock (_gate)
        {
            LoadWarning = null;

            if (!File.Exists(FilePath))
            {
                Current = new AppSettings();
                Current.Normalize();
                return Current;
            }

            string json;
            try
            {
                json = File.ReadAllText(FilePath, Encoding.UTF8);
            }
            catch (IOException ex)
            {
                LoadWarning = $"อ่านไฟล์ตั้งค่าไม่ได้ ({ex.Message}) — ใช้ค่าเริ่มต้นชั่วคราว";
                Current = new AppSettings();
                Current.Normalize();
                return Current;
            }
            catch (UnauthorizedAccessException ex)
            {
                LoadWarning = $"อ่านไฟล์ตั้งค่าไม่ได้ ({ex.Message}) — ใช้ค่าเริ่มต้นชั่วคราว";
                Current = new AppSettings();
                Current.Normalize();
                return Current;
            }

            AppSettings? loaded = null;
            bool migrated = false;
            try
            {
                JsonNode? root = JsonNode.Parse(json);
                if (root is JsonObject obj)
                {
                    migrated = SettingsMigrator.Migrate(obj);
                    loaded = obj.Deserialize<AppSettings>(SerializerOptions);
                }
            }
            catch (JsonException)
            {
                loaded = null;
            }

            if (loaded is null)
            {
                QuarantineCorruptFile();
                Current = new AppSettings();
                Current.Normalize();
                return Current;
            }

            loaded.Normalize();
            Current = loaded;

            if (migrated)
            {
                // Persist the upgraded shape so the migration runs exactly once.
                TryWrite(Current);
            }

            return Current;
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

        lock (_gate)
        {
            TryWrite(settings);
            Current = settings;
        }

        Saved?.Invoke(this, settings);
    }

    /// <summary>Serializes settings to the JSON text that would be written to disk.</summary>
    public static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, SerializerOptions);

    public static AppSettings? Deserialize(string json)
    {
        try
        {
            AppSettings? result = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            result?.Normalize();
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void TryWrite(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Serialize(settings), new UTF8Encoding(false));

            if (File.Exists(FilePath))
            {
                // File.Replace is atomic but throws when the destination is missing,
                // which is exactly the state on first run — hence the branch.
                File.Replace(tmp, FilePath, null);
            }
            else
            {
                File.Move(tmp, FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadWarning = $"บันทึกการตั้งค่าไม่สำเร็จ: {ex.Message}";
        }
    }

    private void QuarantineCorruptFile()
    {
        string bad = Path.Combine(Directory, "settings.bad.json");
        try
        {
            File.Copy(FilePath, bad, overwrite: true);
            File.Delete(FilePath);
            LoadWarning = "ไฟล์ตั้งค่าเสียหาย — สำรองไว้เป็น settings.bad.json และเริ่มด้วยค่าเริ่มต้น";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadWarning = "ไฟล์ตั้งค่าเสียหายและย้ายออกไม่ได้ — ใช้ค่าเริ่มต้นชั่วคราว";
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // Keeps Thai readable in the file instead of a wall of \uXXXX escapes.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
