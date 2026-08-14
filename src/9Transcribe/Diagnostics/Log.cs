using System.IO;
using System.Text;

namespace NineTranscribe.Diagnostics;

/// <summary>
/// Minimal append-only file log under <c>%APPDATA%\9Transcribe\logs</c>. Deliberately tiny:
/// the app has no logging framework, and nothing here may ever record transcript text or
/// the API key.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1_000_000;

    private static readonly object Gate = new();
    private static string? _directory;

    public static string Directory => _directory ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "9Transcribe",
        "logs");

    public static string FilePath => Path.Combine(Directory, "app.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} :: {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                RollIfLarge();
                string line = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
                File.AppendAllText(FilePath, line, new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // Logging must never take the app down.
        }
    }

    private static void RollIfLarge()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        string previous = Path.Combine(Directory, "app.previous.log");
        File.Copy(FilePath, previous, overwrite: true);
        File.Delete(FilePath);
    }
}
