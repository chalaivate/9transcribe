using System.Threading.Channels;
using Microsoft.Win32;
using NineTranscribe.Diagnostics;
using NineTranscribe.Settings;

namespace NineTranscribe.Hotkeys;

/// <summary>
/// Matches the user's bindings against the raw hook stream and turns them into events.
/// </summary>
/// <remarks>
/// Matching runs on the hook thread and must stay trivial, so every event is handed to a
/// single-reader <see cref="Channel{T}"/> and raised from a worker task. Handlers therefore
/// run on a thread-pool thread in event order and must marshal to the UI themselves.
/// <para>
/// The <see cref="KeyboardHookService"/> passed to the constructor is not owned here:
/// <see cref="Dispose"/> detaches from it but leaves it installed for its real owner to dispose.
/// </para>
/// </remarks>
public sealed class HotkeyManager : IDisposable
{
    private const int WatchdogIntervalMs = 100;
    private const int HealthIntervalMs = 60_000;

    /// <summary>
    /// How many watchdog ticks may disagree with the hook stream before a missed key-up is
    /// believed. The hook stream and GetAsyncKeyState are different sources of truth: another
    /// low-level hook or filter driver below us in the chain can consume the key so the system
    /// never records it as down, and half a second of disagreement is the point where a genuinely
    /// lost key-up becomes more likely than a slow one.
    /// </summary>
    private const int MissedUpTicksBeforeRelease = 5;

    /// <summary>
    /// A held key auto-repeats every few tens of milliseconds, so once the repeats stop for this
    /// long the key is certainly up. This is how the latch clears when the key-up event itself is
    /// the thing going missing — it reads the same stream the press came from rather than trusting
    /// the key state that already proved unreliable. A key that never repeats simply clears the
    /// latch early, which is harmless: with no repeats there is nothing to re-arm a hold.
    /// </summary>
    private const int RepeatGapMs = 300;

    private readonly KeyboardHookService _hook;
    private readonly Channel<QueuedEvent> _events;
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _watchdog;
    private readonly Timer _health;

    /// <summary>
    /// Guards the state machine. Entered by the hook thread, so the critical sections must stay
    /// free of I/O and of anything that could block for longer than a few microseconds.
    /// </summary>
    private readonly object _gate = new();

    private readonly HashSet<ushort> _swallowed = new();
    private readonly HashSet<ushort> _captureHeld = new();

    private Task? _worker;
    private bool _started;
    private bool _disposed;
    private bool _captureMode;

    private bool _leftCtrl;
    private bool _rightCtrl;
    private bool _leftShift;
    private bool _rightShift;
    private bool _leftAlt;
    private bool _rightAlt;
    private bool _leftWin;
    private bool _rightWin;

    private bool _pushHeld;
    private ushort _pushVk;
    private HotkeyModifiers _pushMods;
    private long _pushStartedTicks;
    private int _missedUpTicks;

    /// <summary>
    /// Set when a hold is ended by anything other than the user letting go, and cleared only by
    /// a genuine key-up (or by the key really being up). Without it the next hardware auto-repeat
    /// — which arrives about every 30 ms while the key is held — would immediately start another
    /// hold, and the app would flip between recording and not recording for as long as the key
    /// stayed down.
    /// </summary>
    private bool _awaitingRealKeyUp;
    private ushort _awaitedVk;
    private long _lastAwaitedDownTicks;

    private bool _toggleHeld;
    private ushort _toggleVk;

    private ushort _captureVk;
    private ushort _captureLastModifier;
    private HotkeyModifiers _captureMods;

    public HotkeyManager(KeyboardHookService hook)
    {
        ArgumentNullException.ThrowIfNull(hook);

        _hook = hook;
        _events = Channel.CreateUnbounded<QueuedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _watchdog = new Timer(OnWatchdogTick, null, Timeout.Infinite, Timeout.Infinite);
        _health = new Timer(OnHealthTick, null, Timeout.Infinite, Timeout.Infinite);

        PushToTalk = HotkeyBinding.RightControl();
        Toggle = HotkeyBinding.F8();
    }

    /// <summary>Hold-to-record binding. May be a bare modifier such as Right Ctrl.</summary>
    public HotkeyBinding PushToTalk { get; set; }

    /// <summary>Press-to-start / press-to-stop binding.</summary>
    public HotkeyBinding Toggle { get; set; }

    /// <summary>Master switch from the tray menu; the hook stays installed either way.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Set only while recording, so a bare Esc can cancel without stealing Esc otherwise.</summary>
    public bool CancelKeyArmed { get; set; }

    /// <summary>Absolute failsafe: a hold longer than this is released as if the key came up.</summary>
    public int MaxHoldSeconds { get; set; } = 600;

    /// <summary>While true, matching is suspended and the next gesture is captured for rebinding.</summary>
    public bool CaptureMode
    {
        get => _captureMode;
        set
        {
            lock (_gate)
            {
                if (_captureMode == value)
                {
                    return;
                }

                _captureMode = value;
                ResetCapture();
            }
        }
    }

    public event EventHandler? PushToTalkPressed;

    public event EventHandler? PushToTalkReleased;

    public event EventHandler? TogglePressed;

    public event EventHandler? CancelPressed;

    /// <summary>Live "Ctrl + Alt + …" text while a gesture is being held in capture mode.</summary>
    public event EventHandler<string>? CapturePreview;

    /// <summary>Fired once the captured gesture is fully released.</summary>
    public event EventHandler<HotkeyBinding>? GestureCaptured;

    public event EventHandler? CaptureCancelled;

    /// <summary>Every real key-down, injected input excluded. The text injector aborts on this.</summary>
    public event EventHandler<KeyEventData>? PhysicalKeyPressed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started)
        {
            return;
        }

        _started = true;
        _worker = Task.Run(() => RunAsync(_cts.Token));
        _hook.Filter = OnKeyEvent;
        _hook.Install();
        _health.Change(HealthIntervalMs, HealthIntervalMs);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        Log.Info("Hotkey manager started");
    }

    /// <summary>Detaches from the hook and drops all held state. The hook itself stays installed.</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _health.Change(Timeout.Infinite, Timeout.Infinite);
        _watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        _hook.Filter = null;

        lock (_gate)
        {
            if (_pushHeld)
            {
                ReleasePush();
            }

            _swallowed.Clear();
            ResetCapture();
        }

        Log.Info("Hotkey manager stopped");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _cts.Cancel();
        _events.Writer.TryComplete();

        Task? worker = _worker;
        if (worker is not null)
        {
            try
            {
                worker.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The worker only ever faults on cancellation, which is the expected shutdown path.
            }
        }

        _watchdog.Dispose();
        _health.Dispose();
        _cts.Dispose();
    }

    private bool OnKeyEvent(KeyEventData e)
    {
        // Our own Ctrl+V and Unicode typing come back through this hook; without this test the
        // paste would re-trigger the hotkey that produced it.
        if (e.Injected)
        {
            return false;
        }

        lock (_gate)
        {
            return Handle(e);
        }
    }

    /// <summary>Internal so the state machine can be driven directly by tests, without a hook.</summary>
    internal bool Handle(KeyEventData e)
    {
        ushort vk = e.Vk;
        UpdateModifierState(vk, e.IsUp);

        // Maintained ahead of every other branch: the latch has to keep tracking the key even
        // while the app is disabled or capturing a new binding, or it would decide the key had
        // been let go and let the next auto-repeat start a hold nobody pressed.
        if (_awaitingRealKeyUp && vk == _awaitedVk)
        {
            if (e.IsUp)
            {
                _awaitingRealKeyUp = false;
            }
            else
            {
                _lastAwaitedDownTicks = Environment.TickCount64;
            }
        }

        if (!e.IsUp)
        {
            Publish(QueuedEvent.Physical(e));
        }

        if (_captureMode)
        {
            return HandleCapture(vk, e.IsUp);
        }

        bool swallow = e.IsUp ? _swallowed.Remove(vk) : _swallowed.Contains(vk);

        if (!IsEnabled)
        {
            if (_pushHeld)
            {
                ForceReleasePush();
            }

            return swallow;
        }

        if (e.IsUp)
        {
            if (_toggleHeld && vk == _toggleVk)
            {
                _toggleHeld = false;
            }

            if (_pushHeld && ShouldReleasePush(vk))
            {
                ReleasePush();
            }

            return swallow;
        }

        if (CancelKeyArmed && vk == KeyboardNative.VkEscape && CurrentModifiers() == HotkeyModifiers.None)
        {
            Publish(QueuedEvent.Simple(HotkeySignal.Cancel));
            _swallowed.Add(vk);
            return true;
        }

        HotkeyBinding push = PushToTalk;
        if (push.IsEnabled && Matches(push, vk))
        {
            if (_awaitingRealKeyUp && vk == _awaitedVk)
            {
                // Still physically down, so this is a repeat of the hold that was already ended.
                return ApplySwallow(push, vk);
            }

            // The hook repeats key-down while the key is held; the flag keeps one press per hold.
            if (!_pushHeld)
            {
                _pushHeld = true;
                _pushVk = vk;
                _pushMods = (HotkeyModifiers)push.Mods;
                _pushStartedTicks = Environment.TickCount64;
                _missedUpTicks = 0;
                _watchdog.Change(WatchdogIntervalMs, WatchdogIntervalMs);
                Publish(QueuedEvent.Simple(HotkeySignal.PushPressed));
            }

            return ApplySwallow(push, vk);
        }

        HotkeyBinding toggle = Toggle;
        if (toggle.IsEnabled && Matches(toggle, vk))
        {
            if (!_toggleHeld)
            {
                _toggleHeld = true;
                _toggleVk = vk;
                Publish(QueuedEvent.Simple(HotkeySignal.Toggle));
            }

            return ApplySwallow(toggle, vk);
        }

        return swallow;
    }

    /// <summary>
    /// The mask must match exactly, so Ctrl+Alt+Space stays silent under Ctrl+Alt+Shift+Space.
    /// A binding whose main key is itself a modifier contributes its own bit, which is removed
    /// first so bare Right Ctrl can match with an empty mask.
    /// </summary>
    private bool Matches(HotkeyBinding binding, ushort vk)
    {
        if (binding.Vk != vk)
        {
            return false;
        }

        HotkeyModifiers effective = CurrentModifiers() & ~HotkeyDisplay.ModifierFlagOf(vk);
        return effective == (HotkeyModifiers)binding.Mods;
    }

    /// <summary>Combos are released in any order, so losing a required modifier ends the hold too.</summary>
    private bool ShouldReleasePush(ushort vk)
    {
        if (vk == _pushVk)
        {
            return true;
        }

        HotkeyModifiers effective = CurrentModifiers() & ~HotkeyDisplay.ModifierFlagOf(_pushVk);
        return (effective & _pushMods) != _pushMods;
    }

    private bool ApplySwallow(HotkeyBinding binding, ushort vk)
    {
        if (!binding.Swallow || HotkeyDisplay.IsModifierKey(vk))
        {
            return false;
        }

        if (_swallowed.Add(vk) && ((HotkeyModifiers)binding.Mods & (HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0)
        {
            Publish(QueuedEvent.Simple(HotkeySignal.SendInertKey));
        }

        return true;
    }

    private void ReleasePush()
    {
        _pushHeld = false;
        _missedUpTicks = 0;
        _watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        Publish(QueuedEvent.Simple(HotkeySignal.PushReleased));
    }

    /// <summary>
    /// Ends a hold that the user has not let go of, latching until a real key-up arrives so the
    /// key's auto-repeat cannot immediately start another one.
    /// </summary>
    /// <summary>Internal so a test can reproduce the releases that used to restart the hold.</summary>
    internal void ForceReleasePush()
    {
        _awaitingRealKeyUp = true;
        _awaitedVk = _pushVk;
        _lastAwaitedDownTicks = Environment.TickCount64;
        ReleasePush();

        // The watchdog keeps ticking so the latch can notice the repeats stopping.
        _watchdog.Change(WatchdogIntervalMs, WatchdogIntervalMs);
    }

    private bool HandleCapture(ushort vk, bool isUp)
    {
        bool modifier = HotkeyDisplay.IsModifierKey(vk);

        if (isUp)
        {
            _captureHeld.Remove(vk);
            if (_captureHeld.Count == 0 && (_captureVk != 0 || _captureLastModifier != 0))
            {
                CommitCapture();
            }

            return !modifier;
        }

        if (vk == KeyboardNative.VkEscape && _captureVk == 0 && CurrentModifiers() == HotkeyModifiers.None)
        {
            ResetCapture();
            Publish(QueuedEvent.Simple(HotkeySignal.CaptureCancelled));
            return true;
        }

        _captureHeld.Add(vk);

        if (modifier)
        {
            _captureLastModifier = vk;
            _captureMods |= HotkeyDisplay.ModifierFlagOf(vk);
        }
        else if (_captureVk == 0)
        {
            _captureVk = vk;
            _captureMods = CurrentModifiers();
        }

        Publish(QueuedEvent.Preview(_captureVk, _captureMods));

        // Modifiers are never swallowed: capture mode can end while one is down, and a swallowed
        // modifier key-down would leave the foreground app believing it is still held.
        return !modifier;
    }

    private void CommitCapture()
    {
        ushort vk = _captureVk;
        HotkeyModifiers mods = _captureMods;

        if (vk == 0)
        {
            vk = _captureLastModifier;
            mods &= ~HotkeyDisplay.ModifierFlagOf(vk);
        }

        var binding = new HotkeyBinding
        {
            Vk = vk,
            Mods = (int)mods,
            Swallow = HotkeyDisplay.ShouldSwallowByDefault(vk, mods),
            Display = HotkeyDisplay.Describe(vk, mods),
        };

        ResetCapture();
        Publish(QueuedEvent.Gesture(binding));
    }

    /// <summary>
    /// Clears modifier tracking after a hook re-arm, where a key-up landing in the gap would
    /// otherwise leave a modifier stuck down for the rest of the session.
    /// </summary>
    private void ResetModifierState()
    {
        _leftCtrl = _rightCtrl = false;
        _leftShift = _rightShift = false;
        _leftAlt = _rightAlt = false;
        _leftWin = _rightWin = false;
    }

    private void ResetCapture()
    {
        _captureHeld.Clear();
        _captureVk = 0;
        _captureLastModifier = 0;
        _captureMods = HotkeyModifiers.None;
    }

    /// <summary>
    /// Modifier state is tracked from the hook's own stream rather than GetAsyncKeyState, which
    /// races against the event being processed. Physical keyboards report the side-specific
    /// codes, but RDP clients, VM consoles, on-screen keyboards and some remappers send the
    /// side-agnostic ones, and dropping those left every combination unmatched there.
    /// </summary>
    private void UpdateModifierState(ushort vk, bool isUp)
    {
        bool down = !isUp;
        switch (vk)
        {
            case KeyboardNative.VkControl:
                _leftCtrl = down;
                break;
            case KeyboardNative.VkShift:
                _leftShift = down;
                break;
            case KeyboardNative.VkMenu:
                _leftAlt = down;
                break;
            case KeyboardNative.VkLControl:
                _leftCtrl = down;
                break;
            case KeyboardNative.VkRControl:
                _rightCtrl = down;
                break;
            case KeyboardNative.VkLShift:
                _leftShift = down;
                break;
            case KeyboardNative.VkRShift:
                _rightShift = down;
                break;
            case KeyboardNative.VkLMenu:
                _leftAlt = down;
                break;
            case KeyboardNative.VkRMenu:
                _rightAlt = down;
                break;
            case KeyboardNative.VkLWin:
                _leftWin = down;
                break;
            case KeyboardNative.VkRWin:
                _rightWin = down;
                break;
            default:
                break;
        }
    }

    private HotkeyModifiers CurrentModifiers()
    {
        HotkeyModifiers mods = HotkeyModifiers.None;

        if (_leftCtrl || _rightCtrl)
        {
            mods |= HotkeyModifiers.Ctrl;
        }

        if (_leftShift || _rightShift)
        {
            mods |= HotkeyModifiers.Shift;
        }

        if (_leftAlt || _rightAlt)
        {
            mods |= HotkeyModifiers.Alt;
        }

        if (_leftWin || _rightWin)
        {
            mods |= HotkeyModifiers.Win;
        }

        return mods;
    }

    /// <summary>
    /// UAC elevation, Win+L and RDP transitions all swallow the key-up, which would leave the
    /// app recording forever. Poll the real key state and force the release when it disagrees.
    /// </summary>
    private void OnWatchdogTick(object? state)
    {
        lock (_gate)
        {
            if (_awaitingRealKeyUp
                && Environment.TickCount64 - _lastAwaitedDownTicks >= RepeatGapMs)
            {
                _awaitingRealKeyUp = false;
            }

            if (!_pushHeld)
            {
                if (!_awaitingRealKeyUp)
                {
                    _watchdog.Change(Timeout.Infinite, Timeout.Infinite);
                }

                return;
            }

            long heldMs = Environment.TickCount64 - _pushStartedTicks;
            if (heldMs >= Math.Max(1, MaxHoldSeconds) * 1000L)
            {
                Log.Warn($"Push-to-talk force-released after {heldMs} ms");
                ForceReleasePush();
                return;
            }

            if ((KeyboardNative.GetAsyncKeyState(_pushVk) & KeyboardNative.KeyDownBit) != 0)
            {
                _missedUpTicks = 0;
                return;
            }

            _missedUpTicks++;
            if (_missedUpTicks < MissedUpTicksBeforeRelease)
            {
                return;
            }

            // Deliberately not re-arming the hook here. Tearing it down and installing it
            // again drops the events that land in the gap, and the event most likely to be lost
            // is the very key-up this path is waiting for.
            Log.Warn("Push-to-talk key-up never arrived; releasing the hold");
            ForceReleasePush();
        }
    }

    private void OnHealthTick(object? state)
    {
        try
        {
            Reinstall();
        }
        catch (Exception ex)
        {
            Log.Error("Periodic hook re-arm failed", ex);
        }
    }

    /// <summary>Re-arms the hook and drops the modifier state the gap may have invalidated.</summary>
    private void Reinstall()
    {
        _hook.Reinstall();

        lock (_gate)
        {
            ResetModifierState();
        }
    }

    /// <summary>Arrives on a SystemEvents thread; nothing here may touch the UI.</summary>
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                Publish(QueuedEvent.Simple(HotkeySignal.Reinstall));
                break;

            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                lock (_gate)
                {
                    if (_pushHeld)
                    {
                        Log.Warn($"Session switch ({e.Reason}) while holding push-to-talk; releasing");
                        ForceReleasePush();
                    }
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Takes the signals queued so far. Internal for tests, which drive the state machine
    /// directly and so never start the worker that would normally drain them.
    /// </summary>
    internal IReadOnlyList<HotkeySignal> DrainSignals()
    {
        var drained = new List<HotkeySignal>();
        while (_events.Reader.TryRead(out QueuedEvent item))
        {
            drained.Add(item.Signal);
        }

        return drained;
    }

    private void Publish(QueuedEvent item)
    {
        if (!_events.Writer.TryWrite(item))
        {
            Log.Warn("Hotkey event queue rejected an event");
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        string lastPreview = string.Empty;

        try
        {
            await foreach (QueuedEvent item in _events.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    switch (item.Signal)
                    {
                        case HotkeySignal.PushPressed:
                            PushToTalkPressed?.Invoke(this, EventArgs.Empty);
                            break;

                        case HotkeySignal.PushReleased:
                            PushToTalkReleased?.Invoke(this, EventArgs.Empty);
                            break;

                        case HotkeySignal.Toggle:
                            TogglePressed?.Invoke(this, EventArgs.Empty);
                            break;

                        case HotkeySignal.Cancel:
                            CancelPressed?.Invoke(this, EventArgs.Empty);
                            break;

                        case HotkeySignal.CaptureCancelled:
                            lastPreview = string.Empty;
                            CaptureCancelled?.Invoke(this, EventArgs.Empty);
                            break;

                        case HotkeySignal.PhysicalKey:
                            PhysicalKeyPressed?.Invoke(this, item.Key);
                            break;

                        case HotkeySignal.CapturePreview:
                            lastPreview = RaisePreview(item, lastPreview);
                            break;

                        case HotkeySignal.GestureCaptured:
                            lastPreview = string.Empty;
                            if (item.Binding is not null)
                            {
                                GestureCaptured?.Invoke(this, item.Binding);
                            }

                            break;

                        case HotkeySignal.SendInertKey:
                            KeyboardNative.SendInertKey();
                            break;

                        case HotkeySignal.Reinstall:
                            Reinstall();
                            break;

                        default:
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Hotkey event handler failed", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown: Dispose cancels the token to drain the reader.
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey event pump stopped", ex);
        }
    }

    private string RaisePreview(QueuedEvent item, string lastPreview)
    {
        string text = item.Vk != 0
            ? HotkeyDisplay.Describe(item.Vk, item.Mods)
            : HotkeyDisplay.Describe(0, item.Mods) + " + …";

        if (string.Equals(text, lastPreview, StringComparison.Ordinal))
        {
            return lastPreview;
        }

        CapturePreview?.Invoke(this, text);
        return text;
    }

    internal enum HotkeySignal
    {
        PushPressed,
        PushReleased,
        Toggle,
        Cancel,
        CapturePreview,
        GestureCaptured,
        CaptureCancelled,
        PhysicalKey,
        SendInertKey,
        Reinstall,
    }

    private readonly record struct QueuedEvent(
        HotkeySignal Signal,
        ushort Vk,
        HotkeyModifiers Mods,
        HotkeyBinding? Binding,
        KeyEventData Key)
    {
        public static QueuedEvent Simple(HotkeySignal signal) =>
            new(signal, 0, HotkeyModifiers.None, null, default);

        public static QueuedEvent Physical(KeyEventData key) =>
            new(HotkeySignal.PhysicalKey, key.Vk, HotkeyModifiers.None, null, key);

        public static QueuedEvent Preview(ushort vk, HotkeyModifiers mods) =>
            new(HotkeySignal.CapturePreview, vk, mods, null, default);

        public static QueuedEvent Gesture(HotkeyBinding binding) =>
            new(HotkeySignal.GestureCaptured, (ushort)binding.Vk, (HotkeyModifiers)binding.Mods, binding, default);
    }
}
