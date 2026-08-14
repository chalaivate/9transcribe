using System.Runtime.InteropServices;

namespace NineTranscribe.Overlay;

/// <summary>Window styling, monitor geometry and per-monitor DPI for the overlay.</summary>
internal static class WindowNative
{
    internal const int GwlExStyle = -20;

    internal const uint WsExNoActivate = 0x08000000;
    internal const uint WsExToolWindow = 0x00000080;
    internal const uint WsExTransparent = 0x00000020;

    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;

    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint MonitorDefaultToPrimary = 0x00000001;

    internal const int MdtEffectiveDpi = 0;

    internal static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>
    /// The work area of a monitor, in physical pixels. Returns null when the monitor handle is
    /// no longer valid, which happens if a display is unplugged mid-dictation.
    /// </summary>
    internal static Rect? GetWorkArea(IntPtr monitor)
    {
        var info = new MonitorInfo();
        // GetMonitorInfoW fails silently unless the size field is filled in first.
        info.Size = Marshal.SizeOf<MonitorInfo>();
        return GetMonitorInfoW(monitor, ref info) ? info.Work : null;
    }

    internal static double GetScale(IntPtr monitor)
    {
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
    }
}
