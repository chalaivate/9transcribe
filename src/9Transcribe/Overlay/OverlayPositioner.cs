using System.Windows;
using System.Windows.Interop;
using NineTranscribe.Settings;

namespace NineTranscribe.Overlay;

/// <summary>
/// Places the overlay against one of eight anchors on a monitor's work area. All arithmetic is
/// in physical pixels and goes through SetWindowPos: assigning WPF's Left/Top for a move across
/// monitors of different DPI makes the window land offset while WPF rescales it.
/// </summary>
public static class OverlayPositioner
{
    private const int MarginDip = 16;

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
    public static (int X, int Y) Compute(
        OverlayPosition anchor,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        int widthPx,
        int heightPx,
        int marginPx)
    {
        int x = anchor switch
        {
            OverlayPosition.Left or OverlayPosition.TopLeft or OverlayPosition.BottomLeft =>
                workLeft + marginPx,
            OverlayPosition.Right or OverlayPosition.TopRight or OverlayPosition.BottomRight =>
                workRight - widthPx - marginPx,
            _ => workLeft + ((workRight - workLeft - widthPx) / 2),
        };

        int y = anchor switch
        {
            OverlayPosition.Top or OverlayPosition.TopLeft or OverlayPosition.TopRight =>
                workTop + marginPx,
            OverlayPosition.Bottom or OverlayPosition.BottomLeft or OverlayPosition.BottomRight =>
                workBottom - heightPx - marginPx,
            _ => workTop + ((workBottom - workTop - heightPx) / 2),
        };

        return (x, y);
    }

    /// <summary>Moves an already-measured window to its anchor. Position only; WPF owns the size.</summary>
    public static void Place(Window window, OverlayPosition anchor, IntPtr monitor)
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
            anchor,
            work.Left,
            work.Top,
            work.Right,
            work.Bottom,
            widthPx,
            heightPx,
            (int)Math.Round(MarginDip * scale));

        WindowNative.SetWindowPos(
            hwnd,
            WindowNative.HwndTopmost,
            x,
            y,
            0,
            0,
            WindowNative.SwpNoSize | WindowNative.SwpNoActivate);
    }

    /// <summary>
    /// Where the pill starts its entrance, relative to where it ends up: it slides in from the
    /// screen edge it is anchored to, so a pill at the top drops in and one at the bottom rises.
    /// Corner anchors move diagonally by the same total distance.
    /// </summary>
    public static (double Dx, double Dy) EntranceOffset(OverlayPosition anchor, double distance)
    {
        double diagonal = distance * 0.7071;
        return anchor switch
        {
            OverlayPosition.Top => (0, -distance),
            OverlayPosition.Bottom => (0, distance),
            OverlayPosition.Left => (-distance, 0),
            OverlayPosition.Right => (distance, 0),
            OverlayPosition.TopLeft => (-diagonal, -diagonal),
            OverlayPosition.TopRight => (diagonal, -diagonal),
            OverlayPosition.BottomLeft => (-diagonal, diagonal),
            OverlayPosition.BottomRight => (diagonal, diagonal),
            _ => (0, distance),
        };
    }

    /// <summary>Widest the preview text may be on this monitor, in device-independent pixels.</summary>
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
