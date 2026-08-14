using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using NineTranscribe.Diagnostics;
using NineTranscribe.Interop;
using NineTranscribe.Settings;

namespace NineTranscribe.Injection;

public enum InsertionOutcome
{
    Success,
    TargetElevated,
    ClipboardLocked,
    Cancelled,
    Empty,
    Failed,
}

public sealed record InsertionResult(InsertionOutcome Outcome, string? UserMessageThai = null)
{
    public bool IsSuccess => Outcome == InsertionOutcome.Success;
}

/// <summary>Picks the insertion method, honouring the per-application overrides.</summary>
public static class InsertionPolicy
{
    public static InsertionMethod Choose(
        InsertionMethod configured,
        string processName,
        IReadOnlyList<string> forceTyping,
        IReadOnlyList<string> forceClipboard)
    {
        if (Matches(processName, forceTyping))
        {
            // Terminals like PuTTY interpret Ctrl+V as a literal control character.
            return InsertionMethod.UnicodeTyping;
        }

        if (Matches(processName, forceClipboard))
        {
            // Remote-desktop and VM consoles forward scan codes and drop injected Unicode.
            return InsertionMethod.ClipboardPaste;
        }

        return configured;
    }

    private static bool Matches(string processName, IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0 || string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        string name = Trim(processName);
        foreach (string pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern)
                && string.Equals(name, Trim(pattern), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Trim(string value)
    {
        string trimmed = value.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }
}

/// <summary>
/// Inserts transcribed text into whatever window has focus. All work runs on one dedicated STA
/// thread: the clipboard wants a stable owner window, and two insertions must never interleave.
/// </summary>
public sealed class TextInjector : IDisposable
{
    private const int ModifierPollMs = 15;
    private const int ModifierTimeoutMs = 1500;

    /// <summary>
    /// Side-specific codes, because a forced release has to name the key it is releasing: the
    /// side-agnostic VK_CONTROL maps to the left control's scan code, so releasing it leaves a
    /// held right control down — and holding right control is the default way to dictate.
    /// </summary>
    private static readonly ushort[] Modifiers =
    {
        InputNative.VkLShift,
        InputNative.VkRShift,
        InputNative.VkLControl,
        InputNative.VkRControl,
        InputNative.VkLMenu,
        InputNative.VkRMenu,
        InputNative.VkLwin,
        InputNative.VkRwin,
    };

    /// <summary>Keys whose scan code only identifies them with the extended-key flag set.</summary>
    private static bool IsExtendedKey(ushort vk) => vk is InputNative.VkRControl
        or InputNative.VkRMenu or InputNative.VkLwin or InputNative.VkRwin;

    private readonly Func<AppSettings> _settingsProvider;
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _worker;
    private readonly TypingStrategy _typing = new();

    private ClipboardStrategy? _clipboard;
    private long _lastPhysicalKeyTicks;
    private bool _disposed;

    public TextInjector(Func<AppSettings> settingsProvider)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));

        _worker = new Thread(RunWorker)
        {
            IsBackground = true,
            Name = "TextInjector",
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public Task<InsertionResult> InsertAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(new InsertionResult(InsertionOutcome.Empty));
        }

        if (_disposed)
        {
            return Task.FromResult(new InsertionResult(InsertionOutcome.Failed));
        }

        var completion = new TaskCompletionSource<InsertionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _queue.Add(new WorkItem(text, cancellationToken, completion), CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The queue was completed by Dispose between the check above and here.
            completion.TrySetResult(new InsertionResult(InsertionOutcome.Failed));
        }

        return completion.Task;
    }

    /// <summary>
    /// Records that the user pressed a real key. Typing aborts on the next chunk boundary:
    /// injected characters racing the user's own typing into a field corrupt both.
    /// </summary>
    public void NotifyPhysicalKeyPressed() => Interlocked.Exchange(ref _lastPhysicalKeyTicks, Environment.TickCount64);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private void RunWorker()
    {
        // A real message-only window, not the HWND_MESSAGE sentinel: EmptyClipboard sets the
        // clipboard owner to whatever window opened it, and a null owner makes the following
        // SetClipboardData fail.
        using var owner = new HwndSource(new HwndSourceParameters("9TranscribeClipboardOwner")
        {
            ParentWindow = InputNative.HwndMessage,
            WindowStyle = 0,
        });
        _clipboard = new ClipboardStrategy(owner.Handle);

        foreach (WorkItem item in _queue.GetConsumingEnumerable())
        {
            InsertionResult result;
            try
            {
                result = Insert(item);
            }
            catch (Exception ex)
            {
                Log.Error("Text insertion failed", ex);
                result = new InsertionResult(
                    InsertionOutcome.Failed,
                    "วางข้อความไม่สำเร็จ กรุณาดูรายละเอียดใน log");
            }

            item.Completion.TrySetResult(result);
        }
    }

    private InsertionResult Insert(WorkItem item)
    {
        if (item.CancellationToken.IsCancellationRequested)
        {
            return new InsertionResult(InsertionOutcome.Cancelled);
        }

        AppSettings settings = _settingsProvider();
        TargetInfo target = ForegroundTarget.Capture();

        if (target.IsElevated)
        {
            // UIPI drops synthetic input aimed at a higher integrity level and reports success,
            // so leave the text on the clipboard and tell the user to paste it themselves.
            bool copied = _clipboard?.PlaceOnly(item.Text) ?? false;
            return new InsertionResult(
                InsertionOutcome.TargetElevated,
                copied
                    ? "วางข้อความอัตโนมัติไม่ได้ — โปรแกรมปลายทางเปิดแบบผู้ดูแลระบบ ข้อความถูกคัดลอกไว้แล้ว กด Ctrl+V ได้เลย"
                    : "วางข้อความอัตโนมัติไม่ได้ — โปรแกรมปลายทางเปิดแบบผู้ดูแลระบบ");
        }

        WaitForModifiersToClear();

        InsertionMethod method = InsertionPolicy.Choose(
            settings.InsertionMethod,
            target.ProcessName,
            settings.ForceTypingProcesses,
            settings.ForceClipboardProcesses);

        if (method == InsertionMethod.ClipboardPaste)
        {
            InsertionResult result = _clipboard!.Insert(
                item.Text,
                settings.RestoreClipboard,
                settings.ClipboardRestoreDelayMs);

            if (result.Outcome != InsertionOutcome.ClipboardLocked)
            {
                return result;
            }

            Log.Warn("Clipboard was locked; falling back to typing the text");
        }

        long startedAt = Environment.TickCount64;
        return _typing.Insert(
            item.Text,
            settings.TypingIntervalMs,
            () => Interlocked.Read(ref _lastPhysicalKeyTicks) > startedAt,
            item.CancellationToken);
    }

    /// <summary>
    /// Waits for the user to let go of Ctrl, Alt, Shift and Win. Injecting while a modifier is
    /// held turns Ctrl+V into Ctrl+Alt+V and turns typed characters into shortcuts.
    /// </summary>
    private static void WaitForModifiersToClear()
    {
        long deadline = Environment.TickCount64 + ModifierTimeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (!Modifiers.Any(InputNative.IsKeyDown))
            {
                return;
            }

            Thread.Sleep(ModifierPollMs);
        }

        // Still held after the timeout: release them ourselves and let the physical state
        // resynchronize on the user's next real keypress.
        foreach (ushort modifier in Modifiers)
        {
            if (InputNative.IsKeyDown(modifier))
            {
                ForceKeyUp(modifier);
            }
        }
    }

    private static void ForceKeyUp(ushort vk)
    {
        ushort scan = (ushort)InputNative.MapVirtualKeyW(vk, InputNative.MapVkToVsc);
        uint flags = InputNative.KeyEventKeyUp;
        if (IsExtendedKey(vk))
        {
            flags |= InputNative.KeyEventExtendedKey;
        }

        InputNative.Input[] inputs =
        {
            new()
            {
                Type = InputNative.InputKeyboard,
                Data = new InputNative.InputUnion
                {
                    Keyboard = new InputNative.KeyboardInput
                    {
                        Vk = vk,
                        Scan = scan,
                        Flags = flags,
                        Time = 0,
                        ExtraInfo = InjectionTag.Value,
                    },
                },
            },
        };

        InputNative.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<InputNative.Input>());
    }

    private readonly record struct WorkItem(
        string Text,
        CancellationToken CancellationToken,
        TaskCompletionSource<InsertionResult> Completion);
}
