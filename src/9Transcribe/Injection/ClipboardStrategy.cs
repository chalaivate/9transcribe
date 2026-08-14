using System.Runtime.InteropServices;
using NineTranscribe.Diagnostics;
using NineTranscribe.Interop;

namespace NineTranscribe.Injection;

/// <summary>
/// Puts the text on the clipboard and sends Ctrl+V. Raw Win32 rather than
/// <c>System.Windows.Clipboard</c>, which throws COM exceptions the moment a clipboard manager
/// holds the board and offers no way to publish the privacy formats below.
/// </summary>
internal sealed class ClipboardStrategy
{
    private const int OpenAttempts = 10;
    private const int OpenRetryDelayMs = 50;
    private const int MaxBackupBytes = 8 * 1024 * 1024;

    private static readonly uint[] BackedUpFormats =
    {
        InputNative.CfUnicodeText,
        InputNative.CfHdrop,
        InputNative.CfDib,
    };

    private readonly IntPtr _ownerWindow;
    private readonly uint _excludeFromMonitors;
    private readonly uint _allowHistory;
    private readonly uint _allowCloudUpload;

    internal ClipboardStrategy(IntPtr ownerWindow)
    {
        _ownerWindow = ownerWindow;

        // Registered once: these are the documented opt-outs that keep dictated text out of
        // Win+V clipboard history and out of cross-device cloud sync.
        _excludeFromMonitors = InputNative.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        _allowHistory = InputNative.RegisterClipboardFormatW("CanIncludeInClipboardHistory");
        _allowCloudUpload = InputNative.RegisterClipboardFormatW("CanUploadToCloudClipboard");
    }

    /// <summary>Places the text without pasting, used when the target refuses injected input.</summary>
    internal bool PlaceOnly(string text) => Publish(text, backup: null, out _);

    internal InsertionResult Insert(string text, bool restoreClipboard, int restoreDelayMs)
    {
        List<ClipboardBackup>? backup = restoreClipboard ? new List<ClipboardBackup>() : null;

        if (!Publish(text, backup, out uint sequenceAfterSet))
        {
            return new InsertionResult(
                InsertionOutcome.ClipboardLocked,
                "คลิปบอร์ดถูกโปรแกรมอื่นใช้งานอยู่");
        }

        SendPaste();

        if (backup is null)
        {
            return new InsertionResult(InsertionOutcome.Success);
        }

        Thread.Sleep(Math.Clamp(restoreDelayMs, 0, 5000));

        // If the sequence moved, the user copied something while we waited; restoring the old
        // contents on top of their fresh copy would be worse than not restoring at all.
        if (InputNative.GetClipboardSequenceNumber() == sequenceAfterSet)
        {
            RestoreBackup(backup);
        }

        return new InsertionResult(InsertionOutcome.Success);
    }

    private bool Publish(string text, List<ClipboardBackup>? backup, out uint sequenceAfterSet)
    {
        sequenceAfterSet = 0;

        if (!OpenWithRetry())
        {
            return false;
        }

        try
        {
            if (backup is not null)
            {
                CaptureBackup(backup);
            }

            IntPtr handle = AllocateUnicode(text);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            // Emptied only once the replacement is ready, so a failed allocation cannot leave
            // the user with an empty clipboard and their previous copy gone.
            if (!InputNative.EmptyClipboard())
            {
                InputNative.GlobalFree(handle);
                return false;
            }

            if (InputNative.SetClipboardData(InputNative.CfUnicodeText, handle) == IntPtr.Zero)
            {
                InputNative.GlobalFree(handle);
                return false;
            }

            PublishPrivacyFlags();
            sequenceAfterSet = InputNative.GetClipboardSequenceNumber();
            return true;
        }
        finally
        {
            InputNative.CloseClipboard();
        }
    }

    private void PublishPrivacyFlags()
    {
        SetDwordFormat(_allowHistory, 0);
        SetDwordFormat(_allowCloudUpload, 0);

        if (_excludeFromMonitors != 0)
        {
            // The presence of the format is the signal; an empty block is what other apps use.
            IntPtr marker = InputNative.GlobalAlloc(InputNative.GmemMoveable, new UIntPtr(1));
            if (marker != IntPtr.Zero
                && InputNative.SetClipboardData(_excludeFromMonitors, marker) == IntPtr.Zero)
            {
                InputNative.GlobalFree(marker);
            }
        }
    }

    private static void SetDwordFormat(uint format, int value)
    {
        if (format == 0)
        {
            return;
        }

        IntPtr handle = InputNative.GlobalAlloc(InputNative.GmemMoveable, new UIntPtr(sizeof(int)));
        if (handle == IntPtr.Zero)
        {
            return;
        }

        IntPtr pointer = InputNative.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            InputNative.GlobalFree(handle);
            return;
        }

        Marshal.WriteInt32(pointer, value);
        InputNative.GlobalUnlock(handle);

        if (InputNative.SetClipboardData(format, handle) == IntPtr.Zero)
        {
            InputNative.GlobalFree(handle);
        }
    }

    private static IntPtr AllocateUnicode(string text)
    {
        // Windows line endings, and room for the terminating null.
        string normalized = text.ReplaceLineEndings("\r\n");
        int bytes = (normalized.Length + 1) * sizeof(char);

        IntPtr handle = InputNative.GlobalAlloc(InputNative.GmemMoveable, new UIntPtr((uint)bytes));
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr pointer = InputNative.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            InputNative.GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            Marshal.Copy(normalized.ToCharArray(), 0, pointer, normalized.Length);
            Marshal.WriteInt16(pointer, normalized.Length * sizeof(char), 0);
        }
        finally
        {
            InputNative.GlobalUnlock(handle);
        }

        return handle;
    }

    /// <summary>
    /// Copies a few well-known formats only. Enumerating everything would force delay-rendered
    /// producers (Excel publishes around twenty-five formats on a copy) to materialize megabytes.
    /// </summary>
    private static void CaptureBackup(List<ClipboardBackup> backup)
    {
        foreach (uint format in BackedUpFormats)
        {
            if (!InputNative.IsClipboardFormatAvailable(format))
            {
                continue;
            }

            IntPtr source = InputNative.GetClipboardData(format);
            if (source == IntPtr.Zero)
            {
                continue;
            }

            ulong size = InputNative.GlobalSize(source).ToUInt64();
            if (size == 0 || size > MaxBackupBytes)
            {
                continue;
            }

            IntPtr sourcePointer = InputNative.GlobalLock(source);
            if (sourcePointer == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                byte[] copy = new byte[size];
                Marshal.Copy(sourcePointer, copy, 0, (int)size);
                backup.Add(new ClipboardBackup(format, copy));
            }
            finally
            {
                InputNative.GlobalUnlock(source);
            }
        }
    }

    private void RestoreBackup(List<ClipboardBackup> backup)
    {
        if (backup.Count == 0 || !OpenWithRetry())
        {
            return;
        }

        try
        {
            InputNative.EmptyClipboard();

            foreach (ClipboardBackup entry in backup)
            {
                IntPtr handle = InputNative.GlobalAlloc(
                    InputNative.GmemMoveable,
                    new UIntPtr((uint)entry.Data.Length));
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                IntPtr pointer = InputNative.GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    InputNative.GlobalFree(handle);
                    continue;
                }

                Marshal.Copy(entry.Data, 0, pointer, entry.Data.Length);
                InputNative.GlobalUnlock(handle);

                if (InputNative.SetClipboardData(entry.Format, handle) == IntPtr.Zero)
                {
                    InputNative.GlobalFree(handle);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Clipboard could not be restored: {ex.Message}");
        }
        finally
        {
            InputNative.CloseClipboard();
        }
    }

    private bool OpenWithRetry()
    {
        for (int attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (InputNative.OpenClipboard(_ownerWindow))
            {
                return true;
            }

            Thread.Sleep(OpenRetryDelayMs);
        }

        Log.Warn("OpenClipboard failed after every retry");
        return false;
    }

    private static void SendPaste()
    {
        ushort controlScan = (ushort)InputNative.MapVirtualKeyW(InputNative.VkControl, InputNative.MapVkToVsc);
        ushort vScan = (ushort)InputNative.MapVirtualKeyW(InputNative.VkV, InputNative.MapVkToVsc);

        InputNative.Input[] inputs =
        {
            KeyInput(InputNative.VkControl, controlScan, up: false),
            KeyInput(InputNative.VkV, vScan, up: false),
            KeyInput(InputNative.VkV, vScan, up: true),
            KeyInput(InputNative.VkControl, controlScan, up: true),
        };

        InputNative.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<InputNative.Input>());
    }

    private static InputNative.Input KeyInput(ushort vk, ushort scan, bool up) => new()
    {
        Type = InputNative.InputKeyboard,
        Data = new InputNative.InputUnion
        {
            Keyboard = new InputNative.KeyboardInput
            {
                Vk = vk,
                Scan = scan,
                Flags = up ? InputNative.KeyEventKeyUp : 0,
                Time = 0,
                ExtraInfo = InjectionTag.Value,
            },
        },
    };

    private readonly record struct ClipboardBackup(uint Format, byte[] Data);
}
