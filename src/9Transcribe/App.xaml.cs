using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NineTranscribe.Diagnostics;
using NineTranscribe.Settings;

namespace NineTranscribe;

public partial class App : Application
{
    private const string MutexName = @"Local\9Transcribe.SingleInstance";
    private const string ShowSettingsEventName = @"Local\9Transcribe.ShowSettings";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSettingsSignal;
    private CancellationTokenSource? _signalListener;
    private DateTime _lastUnhandledException = DateTime.MinValue;

    internal SettingsStore SettingsStore { get; private set; } = new();

    /// <summary>Raised when another instance was launched and asked this one to show itself.</summary>
    internal event EventHandler? ShowSettingsRequested;

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

        SettingsStore = new SettingsStore();
        SettingsStore.Load();

        Log.Info($"9Transcribe started (settings: {SettingsStore.FilePath})");
        StartSignalListener();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _signalListener?.Cancel();
        _signalListener?.Dispose();
        _showSettingsSignal?.Dispose();

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owning thread; the handle close below still frees it.
            }

            _instanceMutex.Dispose();
        }

        Log.Info("9Transcribe exited");
        base.OnExit(e);
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

    private static void SignalRunningInstance()
    {
        // The first instance creates its mutex before its event, so a second launch in
        // that window has to wait a moment for the event to appear.
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
                int index = WaitHandle.WaitAny(handles);
                if (index != 0 || token.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.BeginInvoke(() => ShowSettingsRequested?.Invoke(this, EventArgs.Empty));
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
            // A tight failure loop would spin forever behind a swallowed exception.
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
