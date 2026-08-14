using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NineTranscribe.Diagnostics;

namespace NineTranscribe.History;

public sealed record HistoryEntry(
    DateTimeOffset Timestamp,
    string Text,
    string Model,
    double AudioSeconds,
    long ApiLatencyMs)
{
    /// <summary>A one-line label for the tray submenu, ellipsized.</summary>
    public string MenuLabel
    {
        get
        {
            string flat = Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            const int max = 48;
            return flat.Length <= max ? flat : string.Concat(flat.AsSpan(0, max).TrimEnd(), "…");
        }
    }
}

/// <summary>
/// The last N transcripts, newest first. Kept in memory by default: dictated text routinely
/// contains client names and passwords, so writing it to disk is opt-in.
/// </summary>
public sealed class TranscriptionHistory
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly LinkedList<HistoryEntry> _entries = new();
    private readonly string _filePath;

    public TranscriptionHistory(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "9Transcribe");
        _filePath = Path.Combine(directory, "history.json");
    }

    public event EventHandler? Changed;

    public int MaxItems { get; set; } = 20;

    public bool IsEnabled { get; set; } = true;

    public bool PersistToDisk { get; set; }

    public IReadOnlyList<HistoryEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToList();
        }
    }

    public void Add(HistoryEntry entry)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(entry.Text))
        {
            return;
        }

        lock (_gate)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > Math.Max(1, MaxItems))
            {
                _entries.RemoveLast();
            }
        }

        if (PersistToDisk)
        {
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }

        if (PersistToDisk)
        {
            Save();
        }
        else
        {
            DeleteFile();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Load()
    {
        if (!PersistToDisk || !File.Exists(_filePath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(_filePath, Encoding.UTF8);
            List<HistoryEntry>? loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(json, SerializerOptions);
            if (loaded is null)
            {
                return;
            }

            lock (_gate)
            {
                _entries.Clear();
                foreach (HistoryEntry entry in loaded.Take(Math.Max(1, MaxItems)))
                {
                    _entries.AddLast(entry);
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"History could not be loaded: {ex.Message}");
        }
    }

    /// <summary>Removes the history file, used when the user turns disk persistence off.</summary>
    public void DeleteFile()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"History file could not be deleted: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(Snapshot(), SerializerOptions), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"History could not be saved: {ex.Message}");
        }
    }
}
