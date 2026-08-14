using NineTranscribe.Hotkeys;
using NineTranscribe.Settings;
using Xunit;

namespace NineTranscribe.Tests;

/// <summary>
/// Drives the matching state machine directly, without installing a hook, by feeding it the
/// same key events the hook would deliver.
/// </summary>
public sealed class HotkeyManagerTests
{
    private const ushort RightCtrl = 0xA3;

    [Fact]
    public void Handle_HoldingTheKey_StartsExactlyOneHold()
    {
        using HotkeyManager manager = CreateManager();

        // Auto-repeat: the hook reports a key-down every few tens of milliseconds while held.
        for (int i = 0; i < 20; i++)
        {
            manager.Handle(Down());
        }

        Assert.Equal(1, CountPresses(manager));
    }

    [Fact]
    public void Handle_AfterAForcedRelease_DoesNotRestartWhileTheKeyIsStillDown()
    {
        using HotkeyManager manager = CreateManager();

        manager.Handle(Down());
        manager.DrainSignals();

        // What the watchdog, the max-hold failsafe and a session lock all do. The key is still
        // physically down, so its auto-repeat keeps arriving — and used to start a new hold each
        // time, flipping the app between recording and idle for as long as the key was held.
        manager.ForceReleasePush();
        Assert.Contains(HotkeyManager.HotkeySignal.PushReleased, manager.DrainSignals());

        for (int i = 0; i < 20; i++)
        {
            manager.Handle(Down());
        }

        Assert.Equal(0, CountPresses(manager));
    }

    [Fact]
    public void Handle_AfterAForcedReleaseAndARealKeyUp_StartsTheNextHoldNormally()
    {
        using HotkeyManager manager = CreateManager();

        manager.Handle(Down());
        manager.ForceReleasePush();
        manager.DrainSignals();

        manager.Handle(Up());
        manager.Handle(Down());

        Assert.Equal(1, CountPresses(manager));
    }

    [Fact]
    public void Handle_ReleasingTheKey_EndsTheHold()
    {
        using HotkeyManager manager = CreateManager();

        manager.Handle(Down());
        manager.Handle(Up());

        IReadOnlyList<HotkeyManager.HotkeySignal> signals = manager.DrainSignals();

        Assert.Contains(HotkeyManager.HotkeySignal.PushPressed, signals);
        Assert.Contains(HotkeyManager.HotkeySignal.PushReleased, signals);
    }

    private static int CountPresses(HotkeyManager manager) =>
        manager.DrainSignals().Count(signal => signal == HotkeyManager.HotkeySignal.PushPressed);

    private static HotkeyManager CreateManager()
    {
        // The hook is inert until it is armed, which this test never does.
        var manager = new HotkeyManager(new KeyboardHookService())
        {
            IsEnabled = true,
            PushToTalk = new HotkeyBinding { Vk = RightCtrl, Mods = 0, Swallow = false },
            Toggle = new HotkeyBinding { Vk = 0x77, Mods = 0, Swallow = true },
        };

        return manager;
    }

    private static KeyEventData Down() => new(RightCtrl, 0, false, false, UIntPtr.Zero);

    private static KeyEventData Up() => new(RightCtrl, 0, true, false, UIntPtr.Zero);
}
