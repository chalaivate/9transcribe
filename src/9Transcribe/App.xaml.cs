using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NineTranscribe.Api;
using NineTranscribe.Audio;
using NineTranscribe.Core;
using NineTranscribe.Diagnostics;
using NineTranscribe.History;
using NineTranscribe.Hotkeys;
using NineTranscribe.Injection;
using NineTranscribe.Overlay;
using NineTranscribe.Settings;
using NineTranscribe.Tray;
using NineTranscribe.UI;

namespace NineTranscribe;

public partial class App : Application
{
    private const string MutexName = @"Local\9Transcribe.SingleInstance";
    private const string ShowSettingsEventName = @"Local\9Transcribe.ShowSettings";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSettingsSignal;
    private CancellationTokenSource? _signalListener;
    private DateTime _lastUnhandledException = DateTime.MinValue;

    private SettingsStore _store = new();
    private AudioRecorder? _recorder;
    private KeyboardHookService? _hook;
    private HotkeyManager? _hotkeys;
    private TextInjector? _injector;
    private OpenAiTranscriptionClient? _api;
    private OverlayWindow? _overlayWindow;
    private TranscriptionHistory? _history;
    private DictationController? _controller;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ClaimSingleInstance())
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        bool startedByWindows = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);

        _store = new SettingsStore();
        AppSettings settings = _store.Load();
        ThemeManager.Apply(settings.Theme);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        if (settings.StartWithWindows)
        {
            StartupRegistrar.RefreshPathIfEnabled();
        }

        BuildServices(settings);
        StartSignalListener();

        Log.Info($"9Transcribe started (settings: {_store.FilePath})");

        if (_store.LoadWarning is { } warning)
        {
            _controller?.ShowStartupNotice(warning);
        }

        // Nothing works without a key, so the first run opens straight onto the API tab.
        if (string.IsNullOrEmpty(settings.ApiKeyProtected) && !startedByWindows)
        {
            ShowSettings(SettingsWindow.ApiPageIndex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        _signalListener?.Cancel();
        _signalListener?.Dispose();
        _showSettingsSignal?.Dispose();

        _settingsViewModel?.Dispose();
        _controller?.Dispose();
        _tray?.Dispose();
        _injector?.Dispose();
        _hotkeys?.Dispose();
        _hook?.Dispose();
        _recorder?.Dispose();

        ReleaseMutex();

        Log.Info("9Transcribe exited");
        base.OnExit(e);
    }

    private void BuildServices(AppSettings settings)
    {
        _recorder = new AudioRecorder();
        _hook = new KeyboardHookService();
        _hotkeys = new HotkeyManager(_hook);
        _injector = new TextInjector(() => _store.Current);
        _api = new OpenAiTranscriptionClient(() => ApiKeyProtector.Unprotect(_store.Current.ApiKeyProtected));

        _history = new TranscriptionHistory();
        _history.IsEnabled = settings.HistoryEnabled;
        _history.MaxItems = settings.HistoryMaxItems;
        _history.PersistToDisk = settings.SaveHistoryToDisk;
        _history.Load();

        var overlayViewModel = new OverlayViewModel();
        _overlayWindow = new OverlayWindow(overlayViewModel);
        _overlayWindow.ApplyStyle(settings);
        // Shown once at startup and kept alive: the window is invisible until a state change,
        // and creating it up front avoids first-show jank in the middle of a dictation.
        _overlayWindow.Show();

        _controller = new DictationController(
            _store,
            _recorder,
            _hotkeys,
            _api,
            _injector,
            overlayViewModel,
            _overlayWindow,
            _history,
            Dispatcher);
        _controller.Start();

        _tray = new TrayService(_history);
        _tray.SettingsRequested += (_, _) => ShowSettings(SettingsWindow.GeneralPageIndex);
        _tray.AboutRequested += (_, _) => ShowSettings(SettingsWindow.AboutPageIndex);
        _tray.ExitRequested += (_, _) => Shutdown();
        _tray.EnabledChanged += OnTrayEnabledChanged;
        _tray.HistoryEntryChosen += OnHistoryEntryChosen;
        _controller.StateChanged += (_, state) => Dispatcher.BeginInvoke(() => _tray?.SetState(state));

        _hotkeys.IsEnabled = true;
        _hotkeys.Start();
        UpdateTrayTooltip(settings);
    }

    private void ShowSettings(int tabIndex)
    {
        if (_settingsWindow is null)
        {
            _settingsViewModel = new SettingsViewModel(_store, _recorder!, _hotkeys!, _api!, _controller!);
            _settingsViewModel.ThemeChanged += (_, theme) =>
            {
                ThemeManager.Apply(theme);
                _settingsWindow?.ApplyTitleBarTheme();
            };
            _settingsViewModel.SettingsApplied += OnSettingsApplied;

            _settingsWindow = new SettingsWindow(_settingsViewModel);
        }

        _settingsWindow.ShowOnTab(tabIndex);
    }

    private void OnSettingsApplied(object? sender, AppSettings settings)
    {
        _controller?.ApplySettings(settings);
        UpdateTrayTooltip(settings);

        if (!settings.SaveHistoryToDisk)
        {
            _history?.DeleteFile();
        }
    }

    private void UpdateTrayTooltip(AppSettings settings)
    {
        string hold = HotkeyDisplay.Describe(settings.Hotkeys.PushToTalk);
        _tray?.SetTooltip($"9Transcribe — กด {hold} ค้างเพื่อพูด");
    }

    private void OnTrayEnabledChanged(object? sender, bool enabled)
    {
        if (_hotkeys is not null)
        {
            _hotkeys.IsEnabled = enabled;
        }

        if (!enabled)
        {
            // Otherwise a toggle-mode recording keeps running with no key left to stop it.
            _controller?.CancelActive();
        }

        _tray?.SetState(_controller?.State ?? DictationState.Idle);
    }

    private static void OnHistoryEntryChosen(object? sender, string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Log.Warn($"History entry could not be copied: {ex.Message}");
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            ThemeManager.Refresh();
            _settingsWindow?.ApplyTitleBarTheme();
        });
    }

    private bool ClaimSingleInstance()
    {
        // Local\ rather than Global\: two users on the same machine each get their own copy.
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew)
        {
            return true;
        }

        _instanceMutex.Dispose();
        _instanceMutex = null;
        return false;
    }

    private void ReleaseMutex()
    {
        if (_instanceMutex is null)
        {
            return;
        }

        try
        {
            _instanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owning thread; closing the handle still frees it.
        }

        _instanceMutex.Dispose();
        _instanceMutex = null;
    }

    private static void SignalRunningInstance()
    {
        // The first instance creates its mutex before its event, so a second launch inside
        // that window has to wait for the event to appear.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using EventWaitHandle handle = EventWaitHandle.OpenExisting(ShowSettingsEventName);
                handle.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private void StartSignalListener()
    {
        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        _signalListener = new CancellationTokenSource();
        CancellationToken token = _signalListener.Token;
        EventWaitHandle signal = _showSettingsSignal;

        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { signal, token.WaitHandle };
            while (!token.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles) != 0 || token.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.BeginInvoke(() => ShowSettings(0));
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceSignal",
        };
        thread.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled dispatcher exception", e.Exception);

        DateTime now = DateTime.UtcNow;
        bool repeating = (now - _lastUnhandledException) < TimeSpan.FromSeconds(10);
        _lastUnhandledException = now;

        if (repeating)
        {
            // A tight failure loop would spin forever behind a swallowed exception, so let
            // the second one in ten seconds take the process down (and the tray icon with it).
            _tray?.Dispose();
            e.Handled = false;
            return;
        }

        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Error("Unhandled exception", exception);
        }
    }
}
