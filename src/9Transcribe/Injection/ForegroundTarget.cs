using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NineTranscribe.Diagnostics;

namespace NineTranscribe.Injection;

/// <summary>
/// The window that will receive the text. <see cref="IsElevated"/> means it runs at a higher
/// integrity level than this process, which is the case where Windows silently discards our
/// synthetic keystrokes.
/// </summary>
public sealed record TargetInfo(IntPtr Hwnd, int ProcessId, string ProcessName, bool IsElevated)
{
    public static TargetInfo Unknown { get; } = new(IntPtr.Zero, 0, string.Empty, false);
}

public static class ForegroundTarget
{
    /// <summary>Reads the foreground window and decides whether we are allowed to type into it.</summary>
    public static TargetInfo Capture()
    {
        IntPtr hwnd = InputNative.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return TargetInfo.Unknown;
        }

        _ = InputNative.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
        {
            return TargetInfo.Unknown;
        }

        IntPtr process = InputNative.OpenProcess(
            InputNative.ProcessQueryLimitedInformation,
            false,
            processId);

        if (process == IntPtr.Zero)
        {
            // Access denied here means the target is elevated or protected. Either way we
            // cannot inject into it, so treat it as elevated and let the caller warn.
            return new TargetInfo(hwnd, (int)processId, string.Empty, true);
        }

        try
        {
            string name = ReadProcessName(process);
            bool elevated = IsHigherIntegrity(process);
            return new TargetInfo(hwnd, (int)processId, name, elevated);
        }
        finally
        {
            InputNative.CloseHandle(process);
        }
    }

    private static string ReadProcessName(IntPtr process)
    {
        var buffer = new StringBuilder(512);
        uint size = (uint)buffer.Capacity;

        if (!InputNative.QueryFullProcessImageNameW(process, 0, buffer, ref size))
        {
            return string.Empty;
        }

        string path = buffer.ToString(0, (int)size);
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Compares mandatory integrity levels. SendInput into a higher-integrity window returns
    /// success and delivers nothing, so this is the only way to know before trying.
    /// </summary>
    private static bool IsHigherIntegrity(IntPtr targetProcess)
    {
        int? target = ReadIntegrityLevel(targetProcess);
        int? own = ReadIntegrityLevel(InputNative.GetCurrentProcess());

        if (target is null || own is null)
        {
            return false;
        }

        return target > own;
    }

    private static int? ReadIntegrityLevel(IntPtr process)
    {
        if (!InputNative.OpenProcessToken(process, InputNative.TokenQuery, out IntPtr token))
        {
            return null;
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            InputNative.GetTokenInformation(token, InputNative.TokenIntegrityLevel, IntPtr.Zero, 0, out uint size);
            if (size == 0)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal((int)size);
            if (!InputNative.GetTokenInformation(token, InputNative.TokenIntegrityLevel, buffer, size, out _))
            {
                return null;
            }

            var label = Marshal.PtrToStructure<InputNative.TokenMandatoryLabel>(buffer);
            IntPtr countPtr = InputNative.GetSidSubAuthorityCount(label.Label.Sid);
            if (countPtr == IntPtr.Zero)
            {
                return null;
            }

            uint lastIndex = (uint)(Marshal.ReadByte(countPtr) - 1);
            IntPtr ridPtr = InputNative.GetSidSubAuthority(label.Label.Sid, lastIndex);
            return ridPtr == IntPtr.Zero ? null : Marshal.ReadInt32(ridPtr);
        }
        catch (Exception ex)
        {
            Log.Warn($"Integrity level could not be read: {ex.Message}");
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            InputNative.CloseHandle(token);
        }
    }
}
