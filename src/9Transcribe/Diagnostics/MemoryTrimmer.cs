using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace NineTranscribe.Diagnostics;

/// <summary>
/// Gives memory back to Windows at the moments the app has just stopped needing it: after
/// start-up and after the settings window closes. The heap is compacted so freed pages can be
/// released, then the working set is emptied, which is what the Memory column in Task Manager
/// shows. Windows pages anything the app touches again straight back in — the next key press
/// costs a few soft page faults, nothing a person can notice.
/// </summary>
public sealed class MemoryTrimmer
{
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _isBusy;

    /// <param name="isBusy">While this returns true a trim is postponed: someone is using the app.</param>
    public MemoryTrimmer(Func<bool> isBusy)
    {
        _isBusy = isBusy;
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle);
        _timer.Tick += (_, _) => Trim();
    }

    /// <summary>Asks for a trim after <paramref name="delay"/>; a newer request replaces an older one.</summary>
    public void Schedule(TimeSpan delay)
    {
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    private void Trim()
    {
        _timer.Stop();

        if (_isBusy())
        {
            Schedule(TimeSpan.FromSeconds(30));
            return;
        }

        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            // (-1, -1) is the documented way to ask Windows to empty the working set.
            SetProcessWorkingSetSizeEx(GetCurrentProcess(), -1, -1, 0);
        }
        catch (Exception ex)
        {
            Log.Warn($"Memory trim failed: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, nint minimum, nint maximum, uint flags);
}
