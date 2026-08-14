using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NineTranscribe.Diagnostics;

namespace NineTranscribe.Audio;

/// <summary>
/// Reports capture devices appearing or disappearing and the default capture endpoint changing,
/// so the settings screen can refresh its list and the recorder can be told its device is gone.
/// <para>
/// Both events are raised on a COM notification thread after a short coalescing delay — Windows
/// fires several notifications for one physical unplug — and subscribers must marshal before
/// touching UI state.
/// </para>
/// </summary>
public sealed class AudioDeviceWatcher : IDisposable
{
    private const int CoalesceMs = 400;

    private readonly object _gate = new();
    private readonly Timer _coalesce;

    private MMDeviceEnumerator? _enumerator;
    private NotificationClient? _client;
    private bool _devicesChanged;
    private bool _defaultChanged;
    private bool _disposed;

    public AudioDeviceWatcher()
    {
        _coalesce = new Timer(OnCoalesceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            _enumerator = new MMDeviceEnumerator();
            _client = new NotificationClient(this);
            _enumerator.RegisterEndpointNotificationCallback(_client);
        }
        catch (Exception ex)
        {
            // Without notifications the app still works; the device list just goes stale until
            // the user reopens the settings window.
            Log.Error("Could not subscribe to WASAPI device notifications", ex);
            _enumerator?.Dispose();
            _enumerator = null;
            _client = null;
        }
    }

    /// <summary>A capture or render endpoint was added, removed, or changed state.</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>The default capture device for the Console role changed.</summary>
    public event EventHandler? DefaultDeviceChanged;

    public void Dispose()
    {
        MMDeviceEnumerator? enumerator;
        NotificationClient? client;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            enumerator = _enumerator;
            client = _client;
            _enumerator = null;
            _client = null;
        }

        _coalesce.Dispose();

        try
        {
            if (enumerator is not null && client is not null)
            {
                enumerator.UnregisterEndpointNotificationCallback(client);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Unregistering device notifications failed: {ex.Message}");
        }

        enumerator?.Dispose();
    }

    private void Schedule(bool devices, bool defaultDevice)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _devicesChanged |= devices;
            _defaultChanged |= defaultDevice;
            _coalesce.Change(CoalesceMs, Timeout.Infinite);
        }
    }

    private void OnCoalesceElapsed(object? state)
    {
        bool devices;
        bool defaultDevice;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            devices = _devicesChanged;
            defaultDevice = _defaultChanged;
            _devicesChanged = false;
            _defaultChanged = false;
        }

        if (devices)
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }

        if (defaultDevice)
        {
            DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The COM callback sink. Add/remove/state notifications do not say which direction the
    /// endpoint belongs to, and querying a device that has just been removed fails, so every
    /// endpoint change is reported and the subscriber re-enumerates.
    /// </summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioDeviceWatcher _owner;

        internal NotificationClient(AudioDeviceWatcher owner)
        {
            _owner = owner;
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
            _owner.Schedule(devices: true, defaultDevice: false);

        public void OnDeviceAdded(string pwstrDeviceId) =>
            _owner.Schedule(devices: true, defaultDevice: false);

        public void OnDeviceRemoved(string deviceId) =>
            _owner.Schedule(devices: true, defaultDevice: false);

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // WAVE_MAPPER follows the Console role only; ignoring the others keeps the recorder
            // from restarting when a soft phone claims the Communications default.
            if (flow == DataFlow.Capture && role == Role.Console)
            {
                _owner.Schedule(devices: false, defaultDevice: true);
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Property churn (volume, format) is constant and says nothing about availability.
        }
    }
}
