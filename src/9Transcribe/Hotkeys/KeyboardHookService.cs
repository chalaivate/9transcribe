using System.Runtime.InteropServices;
using NineTranscribe.Diagnostics;
using NineTranscribe.Interop;

namespace NineTranscribe.Hotkeys;

/// <summary>
/// One key transition as seen by the low-level hook, already decoded.
/// </summary>
/// <param name="Vk">Virtual-key code. Modifiers arrive side-specific (VK_LCONTROL / VK_RCONTROL).</param>
/// <param name="ScanCode">Hardware scan code, stable across keyboard layouts.</param>
/// <param name="IsUp">True for WM_KEYUP / WM_SYSKEYUP.</param>
/// <param name="Injected">True when the event was synthesized rather than typed.</param>
/// <param name="ExtraInfo">The event's <c>dwExtraInfo</c>; ours carries <see cref="InjectionTag"/>.</param>
public readonly record struct KeyEventData(ushort Vk, uint ScanCode, bool IsUp, bool Injected, UIntPtr ExtraInfo);

/// <summary>
/// A WH_KEYBOARD_LL hook living on its own dedicated message-pumping thread.
/// </summary>
/// <remarks>
/// The hook deliberately does not run on the WPF UI thread. Windows silently uninstalls a
/// low-level hook whose owning thread does not return within LowLevelHooksTimeout (~300 ms by
/// default), so a single UI hitch would kill every hotkey with no error anywhere.
/// <para>
/// RegisterHotKey is not used at all: it delivers no key-up (push-to-talk is impossible),
/// cannot bind a lone modifier, and fails outright when another process already owns the combo.
/// </para>
/// </remarks>
public sealed class KeyboardHookService : IDisposable
{
    private readonly object _gate = new();

    /// <summary>Held in a field for the object's lifetime: native code keeps the raw pointer.</summary>
    private readonly KeyboardNative.HookProc _proc;

    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private volatile uint _threadId;
    private IntPtr _hook;
    private volatile bool _disposed;

    public KeyboardHookService()
    {
        _proc = HookCallback;
    }

    /// <summary>
    /// Called for every key transition; returning true swallows the event. Runs on the hook
    /// thread and must return in well under a millisecond — no locks held by the UI thread,
    /// no allocation-heavy work, never <c>Dispatcher.Invoke</c>.
    /// </summary>
    public Func<KeyEventData, bool>? Filter { get; set; }

    public bool IsInstalled => Volatile.Read(ref _hook) != IntPtr.Zero;

    /// <summary>Starts the hook thread and arms the hook. Idempotent.</summary>
    public void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            var thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "KbdHook",
                Priority = ThreadPriority.Highest,
            };
            thread.SetApartmentState(ApartmentState.STA);
            _thread = thread;
            thread.Start();
        }

        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Tears the hook down and arms a fresh one on the hook thread. Idempotent and cheap, so
    /// it is safe to call on a timer or on any session transition.
    /// </summary>
    public void Reinstall()
    {
        if (_disposed)
        {
            return;
        }

        uint id = _threadId;
        if (id == 0)
        {
            Install();
            return;
        }

        if (!KeyboardNative.PostThreadMessageW(id, KeyboardNative.WmReinstall, UIntPtr.Zero, IntPtr.Zero))
        {
            Log.Warn($"Hook reinstall message was not posted, win32={Marshal.GetLastWin32Error()}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Filter = null;

        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
        }

        uint id = _threadId;
        if (id != 0)
        {
            KeyboardNative.PostThreadMessageW(id, KeyboardNative.WmQuit, UIntPtr.Zero, IntPtr.Zero);
        }

        thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void ThreadMain()
    {
        _threadId = KeyboardNative.GetCurrentThreadId();
        Arm();
        _ready.Set();

        try
        {
            while (true)
            {
                int result = KeyboardNative.GetMessageW(out KeyboardNative.Msg msg, IntPtr.Zero, 0, 0);
                if (result == 0 || result == -1)
                {
                    break;
                }

                // Thread messages carry a null window handle and are never dispatched.
                if (msg.Hwnd == IntPtr.Zero && msg.Message == KeyboardNative.WmReinstall)
                {
                    Arm();
                    continue;
                }

                KeyboardNative.TranslateMessage(ref msg);
                KeyboardNative.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Keyboard hook thread failed", ex);
        }
        finally
        {
            Disarm();
            _threadId = 0;
        }
    }

    private void Arm()
    {
        Disarm();

        IntPtr module = KeyboardNative.GetModuleHandleW(null);
        IntPtr hook = KeyboardNative.SetWindowsHookExW(KeyboardNative.WhKeyboardLl, _proc, module, 0);
        Volatile.Write(ref _hook, hook);

        if (hook == IntPtr.Zero)
        {
            Log.Error($"SetWindowsHookExW failed, win32={Marshal.GetLastWin32Error()}");
        }
    }

    private void Disarm()
    {
        IntPtr hook = Volatile.Read(ref _hook);
        if (hook == IntPtr.Zero)
        {
            return;
        }

        Volatile.Write(ref _hook, IntPtr.Zero);
        KeyboardNative.UnhookWindowsHookEx(hook);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != KeyboardNative.HcAction)
        {
            return KeyboardNative.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        Func<KeyEventData, bool>? filter = Filter;
        if (filter is not null)
        {
            try
            {
                KeyboardNative.KbdLlHookStruct data =
                    Marshal.PtrToStructure<KeyboardNative.KbdLlHookStruct>(lParam);

                int message = (int)wParam;
                bool isUp = message == KeyboardNative.WmKeyUp || message == KeyboardNative.WmSysKeyUp;
                bool injected =
                    (data.Flags & (KeyboardNative.LlkhfInjected | KeyboardNative.LlkhfLowerIlInjected)) != 0
                    || InjectionTag.IsOurs(data.ExtraInfo);

                var evt = new KeyEventData((ushort)data.VkCode, data.ScanCode, isUp, injected, data.ExtraInfo);
                if (filter(evt))
                {
                    return (IntPtr)1;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Keyboard hook filter threw", ex);
            }
        }

        return KeyboardNative.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
