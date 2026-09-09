using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using NineTranscribe.Core;
using NineTranscribe.Diagnostics;
using NineTranscribe.History;

namespace NineTranscribe.Tray;

/// <summary>
/// The system tray icon and its menu — the app has no main window, so this is the only
/// thing the user can click when it is idle.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly TranscriptionHistory _history;
    private readonly MenuItem _enabledItem;
    private readonly MenuItem _historyItem;
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.Ordinal);

    private bool _disposed;

    public TrayService(TranscriptionHistory history)
    {
        _history = history;

        _enabledItem = new MenuItem
        {
            Header = "เปิดใช้งาน",
            IsCheckable = true,
            IsChecked = true,
        };
        _enabledItem.Click += (_, _) => EnabledChanged?.Invoke(this, _enabledItem.IsChecked);

        _historyItem = new MenuItem { Header = "ประวัติล่าสุด" };
        _historyItem.SubmenuOpened += (_, _) => RebuildHistoryMenu();
        // A placeholder keeps the submenu arrow visible before the first open.
        _historyItem.Items.Add(new MenuItem { Header = "(ยังไม่มี)", IsEnabled = false });

        var settingsItem = new MenuItem { Header = "การตั้งค่า…" };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var adminItem = new MenuItem { Header = "เริ่มใหม่แบบผู้ดูแลระบบ" };
        adminItem.Click += (_, _) => RestartAsAdministrator();

        var logItem = new MenuItem { Header = "เปิดโฟลเดอร์บันทึก (log)" };
        logItem.Click += (_, _) => OpenLogFolder();

        var aboutItem = new MenuItem { Header = "เกี่ยวกับ 9Transcribe" };
        aboutItem.Click += (_, _) => AboutRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new MenuItem { Header = "ออกจากโปรแกรม" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenu();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(_historyItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(adminItem);
        menu.Items.Add(logItem);
        menu.Items.Add(aboutItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _icon = new TaskbarIcon
        {
            IconSource = LoadIcon("tray-idle"),
            ToolTipText = "9Transcribe",
            ContextMenu = menu,
        };
        _icon.TrayMouseDoubleClick += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _icon.ForceCreate(false);
    }

    public event EventHandler? SettingsRequested;

    /// <summary>The user asked to see the About page.</summary>
    public event EventHandler? AboutRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler<bool>? EnabledChanged;

    public event EventHandler<string>? HistoryEntryChosen;

    public bool IsEnabled
    {
        get => _enabledItem.IsChecked;
        set => _enabledItem.IsChecked = value;
    }

    /// <summary>Swaps the icon so the tray shows at a glance whether the app is listening.</summary>
    public void SetState(DictationState state)
    {
        string name = state switch
        {
            DictationState.Recording => "tray-rec",
            DictationState.Transcribing => "tray-busy",
            _ => IsEnabled ? "tray-idle" : "tray-off",
        };

        _icon.IconSource = LoadIcon(name);
    }

    public void SetTooltip(string text) => _icon.ToolTipText = text;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Without this the icon lingers in the tray until the user hovers over it.
        _icon.Dispose();
    }

    private void RebuildHistoryMenu()
    {
        _historyItem.Items.Clear();
        IReadOnlyList<HistoryEntry> entries = _history.Snapshot();

        if (entries.Count == 0)
        {
            _historyItem.Items.Add(new MenuItem { Header = "(ยังไม่มี)", IsEnabled = false });
            return;
        }

        foreach (HistoryEntry entry in entries)
        {
            var item = new MenuItem { Header = entry.MenuLabel };
            string text = entry.Text;
            item.Click += (_, _) => HistoryEntryChosen?.Invoke(this, text);
            _historyItem.Items.Add(item);
        }

        _historyItem.Items.Add(new Separator());
        var clear = new MenuItem { Header = "ล้างประวัติ" };
        clear.Click += (_, _) => _history.Clear();
        _historyItem.Items.Add(clear);
    }

    private ImageSource LoadIcon(string name)
    {
        if (_iconCache.TryGetValue(name, out ImageSource? cached))
        {
            return cached;
        }

        var source = new BitmapImage();
        source.BeginInit();
        source.UriSource = new Uri($"pack://application:,,,/Assets/{name}.ico", UriKind.Absolute);
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.EndInit();
        source.Freeze();

        _iconCache[name] = source;
        return source;
    }

    private static void RestartAsAdministrator()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--tray",
            });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            // The user declining the UAC prompt lands here; nothing to report.
            Log.Info($"Restart as administrator was not completed: {ex.Message}");
        }
    }

    private static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
            Process.Start(new ProcessStartInfo(Log.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Log folder could not be opened: {ex.Message}");
        }
    }
}
