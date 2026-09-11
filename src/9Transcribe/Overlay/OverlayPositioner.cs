using System.Windows;
using System.Windows.Interop;

namespace NineTranscribe.Overlay;

/// <summary>
/// Places the overlay on a guide line that runs across the monitor at a chosen percentage of
/// the work area's height. The window is centred horizontally; its "top part" (the transcript)
/// ends on the line and the rest (the listening tab) hangs below it. All arithmetic is in
/// physical pixels and goes through SetWindowPos: assigning WPF's Left/Top for a move across
/// monitors of different DPI makes the window land offset while WPF rescales it.
/// </summary>
public static class OverlayPositioner
{
    /// <summary>The monitor the user is actually looking at, chosen once per dictation.</summary>
    public static IntPtr MonitorForForegroundWindow()
    {
        IntPtr foreground = WindowNative.GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            IntPtr monitor = WindowNative.MonitorFromWindow(foreground, WindowNative.MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                return monitor;
            }
        }

        if (WindowNative.GetCursorPos(out WindowNative.Point cursor))
        {
            IntPtr monitor = WindowNative.MonitorFromPoint(cursor, WindowNative.MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                return monitor;
            }
        }

        return WindowNative.MonitorFromPoint(default, WindowNative.MonitorDefaultToPrimary);
    }

    /// <summary>
    /// Top-left corner for a window of the given size against the given work rectangle. Pure
    /// integer arithmetic relative to the rectangle, so a monitor at negative coordinates works
    /// exactly like the primary one.
    /// </summary>
    /// <param name="topPartPx">
    /// Height of the part of the window that must sit above the guide line. The line itself
    /// therefore lands <c>topPartPx</c> below the window's top edge.
    /// </param>
    /// <param name="baselinePercent">Where the guide line is, as a percentage of the work height from the top.</param>
    public static (int X, int Y) Compute(
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        int widthPx,
        int heightPx,
        int topPartPx,
        int baselinePercent)
    {
        int workHeight = workBottom - workTop;
        int lineY = workTop + (int)Math.Round(workHeight * baselinePercent / 100.0);

        // Centred; a window wider than the screen overhangs both sides equally.
        int x = workLeft + ((workRight - workLeft - widthPx) / 2);

        // Kept on screen: a tall transcript pushes the whole thing up rather than off the top,
        // and a line very near the bottom pushes it up rather than off the bottom.
        int lowest = Math.Max(workTop, workBottom - heightPx);
        int y = Math.Clamp(lineY - topPartPx, workTop, lowest);

        return (x, y);
    }

    /// <summary>Moves an already-measured window onto the guide line. Position only; WPF owns the size.</summary>
    public static void Place(Window window, int baselinePercent, double topPartDip, IntPtr monitor)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (monitor == IntPtr.Zero)
        {
            monitor = MonitorForForegroundWindow();
        }

        if (WindowNative.GetWorkArea(monitor) is not { } work)
        {
            return;
        }

        double scale = WindowNative.GetScale(monitor);
        int widthPx = (int)Math.Round(window.ActualWidth * scale);
        int heightPx = (int)Math.Round(window.ActualHeight * scale);
        if (widthPx <= 0 || heightPx <= 0)
        {
            return;
        }

        (int x, int y) = Compute(
            work.Left,
            work.Top,
            work.Right,
            work.Bottom,
            widthPx,
            heightPx,
            (int)Math.Round(topPartDip * scale),
            baselinePercent);

        WindowNative.SetWindowPos(
            hwnd,
            WindowNative.HwndTopmost,
            x,
            y,
            0,
            0,
            WindowNative.SwpNoSize | WindowNative.SwpNoActivate);
    }

    /// <summary>Widest the transcript may be on this monitor, in device-independent pixels.</summary>
    public static double MaxContentWidthDip(IntPtr monitor)
    {
        if (WindowNative.GetWorkArea(monitor) is not { } work)
        {
            return 860;
        }

        double scale = WindowNative.GetScale(monitor);
        double workWidthDip = (work.Right - work.Left) / scale;
        return Math.Min(workWidthDip * 0.6, 860);
    }
}
