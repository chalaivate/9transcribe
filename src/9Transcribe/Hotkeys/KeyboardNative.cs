using System.Runtime.InteropServices;
using System.Text;
using NineTranscribe.Interop;

namespace NineTranscribe.Hotkeys;

/// <summary>
/// The Win32 surface the hotkey subsystem needs, declared locally so this folder builds
/// without depending on any other interop file.
/// </summary>
internal static class KeyboardNative
{
    internal const int WhKeyboardLl = 13;

    /// <summary>The only <c>nCode</c> a low-level hook may act on; anything below zero must pass through.</summary>
    internal const int HcAction = 0;

    internal const int WmKeyDown = 0x0100;
    internal const int WmKeyUp = 0x0101;
    internal const int WmSysKeyDown = 0x0104;
    internal const int WmSysKeyUp = 0x0105;
    internal const uint WmQuit = 0x0012;

    /// <summary>
    /// Private thread message asking the hook thread to tear down and re-arm the hook.
    /// Taken from the WM_APP range, which is reserved for application-private messages and
    /// therefore safe on a thread queue that also carries system messages.
    /// </summary>
    internal const uint WmReinstall = 0x8000 + 1;

    internal const uint LlkhfLowerIlInjected = 0x02;
    internal const uint LlkhfInjected = 0x10;

    internal const uint MapvkVkToVsc = 0;

    internal const uint InputKeyboard = 1;
    internal const uint KeyEventFKeyUp = 0x0002;

    /// <summary>
    /// Officially unassigned virtual key. Sent between a swallowed Alt/Win combo and the
    /// modifier's key-up so the foreground app does not read the modifier as a lone press
    /// and open its menu bar or Office key tips.
    /// </summary>
    internal const ushort VkInert = 0xE8;

    internal const ushort VkBack = 0x08;
    internal const ushort VkTab = 0x09;
    internal const ushort VkReturn = 0x0D;
    internal const ushort VkShift = 0x10;
    internal const ushort VkControl = 0x11;
    internal const ushort VkMenu = 0x12;
    internal const ushort VkCapital = 0x14;
    internal const ushort VkEscape = 0x1B;
    internal const ushort VkSpace = 0x20;
    internal const ushort VkDelete = 0x2E;
    internal const ushort VkH = 0x48;
    internal const ushort VkLWin = 0x5B;
    internal const ushort VkRWin = 0x5C;
    internal const ushort VkF1 = 0x70;
    internal const ushort VkF5 = 0x74;
    internal const ushort VkF9 = 0x78;
    internal const ushort VkF24 = 0x87;
    internal const ushort VkLShift = 0xA0;
    internal const ushort VkRShift = 0xA1;
    internal const ushort VkLControl = 0xA2;
    internal const ushort VkRControl = 0xA3;
    internal const ushort VkLMenu = 0xA4;
    internal const ushort VkRMenu = 0xA5;
    internal const ushort VkOem3 = 0xC0;

    /// <summary>Bit set in the <see cref="GetAsyncKeyState"/> result while the key is physically down.</summary>
    internal const int KeyDownBit = 0x8000;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HardwareInput
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeybdInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hmod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessageW(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int GetKeyNameTextW(int lParam, StringBuilder lpString, int cchSize);

    /// <summary>
    /// Sends a down/up pair of <see cref="VkInert"/>, tagged so our own hook ignores it.
    /// </summary>
    internal static void SendInertKey()
    {
        Input[] inputs = new Input[2];
        inputs[0].Type = InputKeyboard;
        inputs[0].Union.Keyboard = new KeybdInput
        {
            Vk = VkInert,
            ExtraInfo = InjectionTag.Value,
        };
        inputs[1].Type = InputKeyboard;
        inputs[1].Union.Keyboard = new KeybdInput
        {
            Vk = VkInert,
            Flags = KeyEventFKeyUp,
            ExtraInfo = InjectionTag.Value,
        };

        SendInput(2, inputs, Marshal.SizeOf<Input>());
    }
}
