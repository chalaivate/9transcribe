using System.Runtime.InteropServices;
using System.Text;

namespace NineTranscribe.Injection;

/// <summary>
/// The Win32 surface this folder needs: synthetic input, clipboard, and enough of the process
/// token API to tell whether the foreground window outranks us.
/// </summary>
internal static class InputNative
{
    internal const uint InputKeyboard = 1;

    internal const uint KeyEventExtendedKey = 0x0001;
    internal const uint KeyEventKeyUp = 0x0002;
    internal const uint KeyEventUnicode = 0x0004;
    internal const uint KeyEventScanCode = 0x0008;

    internal const ushort VkShift = 0x10;
    internal const ushort VkControl = 0x11;
    internal const ushort VkMenu = 0x12;
    internal const ushort VkLShift = 0xA0;
    internal const ushort VkRShift = 0xA1;
    internal const ushort VkLControl = 0xA2;
    internal const ushort VkRControl = 0xA3;
    internal const ushort VkLMenu = 0xA4;
    internal const ushort VkRMenu = 0xA5;
    internal const ushort VkLwin = 0x5B;
    internal const ushort VkRwin = 0x5C;
    internal const ushort VkReturn = 0x0D;
    internal const ushort VkTab = 0x09;
    internal const ushort VkV = 0x56;

    internal const uint MapVkToVsc = 0;

    internal const uint CfUnicodeText = 13;
    internal const uint CfDib = 8;
    internal const uint CfHdrop = 15;

    internal const uint GmemMoveable = 0x0002;

    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint TokenQuery = 0x0008;
    internal const int TokenIntegrityLevel = 25;

    /// <summary>Mandatory-label RIDs. Medium is a normal user process; High is elevated.</summary>
    internal const int SecurityMandatoryMediumRid = 0x2000;

    internal static readonly IntPtr HwndMessage = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        internal KeyboardInput Keyboard;

        [FieldOffset(0)]
        internal MouseInput Mouse;

        [FieldOffset(0)]
        internal HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort Vk;
        internal ushort Scan;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    // Present only so InputUnion is laid out at the size SendInput expects on x64.
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HardwareInput
    {
        internal uint Msg;
        internal ushort ParamL;
        internal ushort ParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenMandatoryLabel
    {
        internal SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterClipboardFormatW")]
    internal static extern uint RegisterClipboardFormatW(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern UIntPtr GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(
        IntPtr process,
        uint flags,
        StringBuilder exeName,
        ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        IntPtr token,
        int informationClass,
        IntPtr information,
        uint length,
        out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    /// <summary>True when the key is physically down right now.</summary>
    internal static bool IsKeyDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
