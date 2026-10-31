using GameHelper.Desktop.Input;
using Xunit;

public sealed class ControllerNavigationTests
{
    [Theory]
    [InlineData(ControllerAction.Menu)]
    [InlineData(ControllerAction.ModeModifier)]
    public void GlobalModeChordWorksInBackgroundWithoutOtherActions(ControllerAction first)
    {
        var source = new Source { States = [new(1, ControllerKind.Xbox, 0)] };
        var nav = new ControllerNavigation(source, new Clock()); nav.Poll(false, true);
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.Confirm | first)];
        Assert.Empty(nav.Poll(false, true));
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.Menu | ControllerAction.ModeModifier | ControllerAction.Confirm)];
        Assert.Equal([ControllerAction.ToggleConsoleMode], nav.Poll(false, true));
        Assert.Empty(nav.Poll(true, true));
        Assert.Empty(nav.Poll(false, true));
        source.States = [new(1, ControllerKind.Xbox, first)]; Assert.Empty(nav.Poll(false, true));
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.Menu | ControllerAction.ModeModifier)]; Assert.Empty(nav.Poll(false, true));
        source.States = [new(1, ControllerKind.Xbox, 0)]; Assert.Empty(nav.Poll(false, true));
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.Menu | ControllerAction.ModeModifier)];
        Assert.Equal([ControllerAction.ToggleConsoleMode], nav.Poll(false, true));
        var nextWindow = new ControllerNavigation(source, new Clock());
        Assert.Empty(nextWindow.Poll(true, true)); // Held chord cannot replay into the new window.
    }

    [Fact]
    public void GlobalChordCannotCombineDevicesOrReplayVirtualDuplicate()
    {
        var chord = ControllerAction.Menu | ControllerAction.ModeModifier;
        var source = new Source { States = [new(1, ControllerKind.Xbox, 0), new(2, ControllerKind.PlayStation, 0)] };
        var nav = new ControllerNavigation(source, new Clock()); nav.Poll(false, true);
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.Menu), new(2, ControllerKind.PlayStation, ControllerAction.ModeModifier)];
        Assert.Empty(nav.Poll(false, true));
        source.States = [new(1, ControllerKind.Xbox, chord), new(2, ControllerKind.PlayStation, ControllerAction.ModeModifier)];
        Assert.Equal([ControllerAction.ToggleConsoleMode], nav.Poll(false, true));
        source.States = [new(1, ControllerKind.Xbox, chord), new(2, ControllerKind.PlayStation, chord)];
        Assert.Empty(nav.Poll(false, true));
    }
    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }
    private sealed class Source : IControllerSource
    {
        public IReadOnlyList<ControllerState> States = [];
        public IReadOnlyList<ControllerState> Read() => States;
        public void Dispose() { }
    }
    [Theory]
    [InlineData(15999, 0, ControllerAction.None)]
    [InlineData(-32768, 0, ControllerAction.Left)]
    [InlineData(20000, -25000, ControllerAction.Up)]
    public void StickUsesDeadZoneAndDominantAxis(short x, short y, ControllerAction expected) =>
        Assert.Equal(expected, ControllerNavigation.Map(new(1, ControllerKind.Xbox, 0, x, y)));

    [Fact]
    public void RepeatUsesInjectedClockAndConfirmNeverRepeats()
    {
        var source = new Source { States = [new(1, ControllerKind.Xbox, 0)] };
        var clock = new Clock(); var nav = new ControllerNavigation(source, clock);
        nav.Poll(true);
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.Right | ControllerAction.Confirm)];
        Assert.Equal(2, nav.Poll(true).Count);
        clock.Now = 399; Assert.Empty(nav.Poll(true));
        clock.Now = 400; Assert.Equal([ControllerAction.Right], nav.Poll(true));
        clock.Now = 489; Assert.Empty(nav.Poll(true));
        clock.Now = 490; Assert.Equal([ControllerAction.Right], nav.Poll(true));
    }
    [Fact]
    public void PhysicalAndDelayedVirtualReportsNeverDoubleNavigate()
    {
        var source = new Source { States = [new(1, ControllerKind.Xbox, 0), new(2, ControllerKind.PlayStation, 0)] };
        var nav = new ControllerNavigation(source, new Clock()); nav.Poll(true);
        source.States = [new(1, ControllerKind.Xbox, 0), new(2, ControllerKind.PlayStation, ControllerAction.Confirm)];
        Assert.Equal([ControllerAction.Confirm], nav.Poll(true));
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.Confirm), new(2, ControllerKind.PlayStation, 0)];
        Assert.Empty(nav.Poll(true)); Assert.Equal(ControllerKind.PlayStation, nav.DeviceKind);
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.Confirm)];
        Assert.Empty(nav.Poll(true)); // Hot unplug cannot replay a held confirmation.
    }
    [Fact]
    public void IdleOwnerAllowsNewPressAtHalfSecondAndChangesKind()
    {
        var source = new Source { States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, 0)] };
        var clock = new Clock(); var nav = new ControllerNavigation(source, clock); nav.Poll(true);
        clock.Now = 500;
        source.States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, ControllerAction.Right)];
        Assert.Equal([ControllerAction.Right], nav.Poll(true));
        Assert.Equal(ControllerKind.Xbox, nav.DeviceKind);
    }
    [Fact]
    public void DelayedMirrorDuringCooldownCannotTakeOverEvenIfHeldPastDeadline()
    {
        var source = new Source { States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, 0)] };
        var clock = new Clock(); var nav = new ControllerNavigation(source, clock); nav.Poll(true);
        source.States = [new(1, ControllerKind.PlayStation, ControllerAction.Confirm), new(2, ControllerKind.Xbox, 0)];
        Assert.Equal([ControllerAction.Confirm], nav.Poll(true));
        clock.Now = 10;
        source.States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, 0)]; nav.Poll(true);
        clock.Now = 509;
        source.States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, ControllerAction.Confirm)];
        Assert.Empty(nav.Poll(true));
        clock.Now = 1000; Assert.Empty(nav.Poll(true));
        Assert.Equal(ControllerKind.PlayStation, nav.DeviceKind);
        source.States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, 0)]; nav.Poll(true);
        source.States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, ControllerAction.Confirm)];
        Assert.Equal([ControllerAction.Confirm], nav.Poll(true));
        Assert.Equal(ControllerKind.Xbox, nav.DeviceKind);
    }
    [Fact]
    public void TakeoverWithNewDirectionDoesNotReplayHeldConfirm()
    {
        var source = new Source { States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, 0)] };
        var clock = new Clock(); var nav = new ControllerNavigation(source, clock); nav.Poll(true);
        clock.Now = 100;
        source.States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, ControllerAction.Confirm)];
        Assert.Empty(nav.Poll(true));
        clock.Now = 600; Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, ControllerAction.Confirm | ControllerAction.Right)];
        Assert.Equal([ControllerAction.Right], nav.Poll(true));
        Assert.Equal(ControllerKind.Xbox, nav.DeviceKind);
        source.States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, 0)]; nav.Poll(true);
        source.States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, ControllerAction.Confirm)];
        Assert.Equal([ControllerAction.Confirm], nav.Poll(true));
    }
    [Fact]
    public void ActiveOwnerRejectsOtherDeviceEvenAfterLongTime()
    {
        var source = new Source { States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, 0)] };
        var clock = new Clock(); var nav = new ControllerNavigation(source, clock); nav.Poll(true);
        source.States = [new(1, ControllerKind.PlayStation, 0, 20000), new(2, ControllerKind.Xbox, 0)]; nav.Poll(true);
        clock.Now = 2000;
        source.States = [new(1, ControllerKind.PlayStation, 0, 20000), new(2, ControllerKind.Xbox, ControllerAction.Confirm)];
        Assert.DoesNotContain(ControllerAction.Confirm, nav.Poll(true));
        Assert.Equal(ControllerKind.PlayStation, nav.DeviceKind);
    }
    [Fact]
    public void ReinitializedNavigationDoesNotReplayHeldConfirm()
    {
        var source = new Source { States = [new(1, ControllerKind.Xbox, ControllerAction.Confirm)] };
        var nav = new ControllerNavigation(source, new Clock());
        Assert.Empty(nav.Poll(true)); Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.Xbox, 0)]; nav.Poll(true);
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.Confirm)];
        Assert.Equal([ControllerAction.Confirm], nav.Poll(true));
    }
    [Fact]
    public void FocusReturnRequiresRelease()
    {
        var source = new Source { States = [new(1, ControllerKind.Xbox, 0)] };
        var nav = new ControllerNavigation(source, new Clock()); nav.Poll(true);
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.Confirm)];
        Assert.Empty(nav.Poll(false)); Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.Xbox, 0)]; nav.Poll(true);
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.Confirm)];
        Assert.Equal([ControllerAction.Confirm], nav.Poll(true));
    }
    [Theory]
    [InlineData(ControllerAction.Menu)]
    [InlineData(ControllerAction.ModeModifier)]
    public void ModeChordAcceptsEitherOrderWithoutOpeningMenu(ControllerAction first)
    {
        var source = new Source { States = [new(1, ControllerKind.Xbox, 0)] };
        var clock = new Clock(); var nav = new ControllerNavigation(source, clock); nav.Poll(true);
        source.States = [new(1, ControllerKind.Xbox, first)];
        Assert.Empty(nav.Poll(true));
        // Even a slow chord cannot open the single-key menu before the second press.
        clock.Now = 2000;
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.ModeModifier | ControllerAction.Menu | ControllerAction.Confirm)];
        Assert.Equal([ControllerAction.ToggleConsoleMode], nav.Poll(true));
        clock.Now = 4000; Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.Xbox, first)];
        Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.ModeModifier | ControllerAction.Menu)];
        Assert.Empty(nav.Poll(true)); // Partial release cannot trigger a second toggle.
        source.States = [new(1, ControllerKind.Xbox, 0)]; Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.ModeModifier | ControllerAction.Menu)];
        Assert.Equal([ControllerAction.ToggleConsoleMode], nav.Poll(true));
    }
    [Fact]
    public void StandaloneMenuFiresOnceOnReleaseAndModifierAloneIsSilent()
    {
        var source = new Source { States = [new(1, ControllerKind.PlayStation, 0)] };
        var nav = new ControllerNavigation(source, new Clock()); nav.Poll(true);
        source.States = [new(1, ControllerKind.PlayStation, ControllerAction.Menu)];
        Assert.Empty(nav.Poll(true)); Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.PlayStation, 0)];
        Assert.Equal([ControllerAction.Menu], nav.Poll(true)); Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.PlayStation, ControllerAction.ModeModifier)]; Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.PlayStation, 0)]; Assert.Empty(nav.Poll(true));
    }
    [Fact]
    public void ModeChordCannotReplayAcrossWindowOrFocusChange()
    {
        var chord = ControllerAction.ModeModifier | ControllerAction.Menu;
        var source = new Source { States = [new(1, ControllerKind.Xbox, chord)] };
        var nav = new ControllerNavigation(source, new Clock());
        Assert.Empty(nav.Poll(true)); Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.Xbox, 0)]; nav.Poll(true);
        source.States = [new(1, ControllerKind.Xbox, ControllerAction.Menu)]; nav.Poll(true);
        Assert.Empty(nav.Poll(false));
        source.States = [new(1, ControllerKind.Xbox, chord)]; Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.Xbox, 0)]; Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.Xbox, chord)];
        Assert.Equal([ControllerAction.ToggleConsoleMode], nav.Poll(true));
    }
    [Fact]
    public void ChordKeysFromDifferentDevicesCannotCombine()
    {
        var source = new Source { States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, 0)] };
        var clock = new Clock(); var nav = new ControllerNavigation(source, clock); nav.Poll(true);
        source.States = [new(1, ControllerKind.PlayStation, ControllerAction.ModeModifier), new(2, ControllerKind.Xbox, ControllerAction.Menu)];
        Assert.Empty(nav.Poll(true));
        clock.Now = 2000; Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, 0)];
        Assert.Empty(nav.Poll(true));
    }
    [Fact]
    public void TriggerUsesHysteresisAndOnlyOnePagePerPress()
    {
        var source = new Source { States = [new(1, ControllerKind.PlayStation, 0)] };
        var clock = new Clock(); var nav = new ControllerNavigation(source, clock); nav.Poll(true);
        source.States = [new(1, ControllerKind.PlayStation, 0, RightTrigger: 15999)]; Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.PlayStation, 0, RightTrigger: 16000)];
        Assert.Equal([ControllerAction.NextShelfPage], nav.Poll(true));
        clock.Now = 2000; Assert.Empty(nav.Poll(true));
        foreach (short value in new short[] { 15000, 16001, 8000, 15999, 32767 })
        { source.States = [new(1, ControllerKind.PlayStation, 0, RightTrigger: value)]; Assert.Empty(nav.Poll(true)); }
        source.States = [new(1, ControllerKind.PlayStation, 0, RightTrigger: 7999)]; Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.PlayStation, 0, RightTrigger: 16000)];
        Assert.Equal([ControllerAction.NextShelfPage], nav.Poll(true));
        source.States = [new(1, ControllerKind.PlayStation, 0, LeftTrigger: 32767)];
        Assert.Equal([ControllerAction.PreviousShelfPage], nav.Poll(true));
    }
    [Fact]
    public void HeldTriggerCannotReplayOnFocusReturnOrDeviceTakeover()
    {
        var source = new Source { States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, 0)] };
        var clock = new Clock(); var nav = new ControllerNavigation(source, clock); nav.Poll(true);
        source.States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, 0, RightTrigger: 32767)];
        Assert.Empty(nav.Poll(true));
        clock.Now = 600; Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.PlayStation, 0), new(2, ControllerKind.Xbox, ControllerAction.Right, RightTrigger: 32767)];
        Assert.Equal([ControllerAction.Right], nav.Poll(true));
        Assert.Empty(nav.Poll(false)); Assert.Empty(nav.Poll(true));
        source.States = [new(2, ControllerKind.Xbox, 0)]; nav.Poll(true);
        source.States = [new(2, ControllerKind.Xbox, 0, RightTrigger: 32767)];
        Assert.Equal([ControllerAction.NextShelfPage], nav.Poll(true));
    }
    [Fact]
    public void ReinitializedNavigationRequiresHeldTriggerRelease()
    {
        var source = new Source { States = [new(1, ControllerKind.PlayStation, 0, LeftTrigger: 32767)] };
        var nav = new ControllerNavigation(source, new Clock());
        Assert.Empty(nav.Poll(true)); Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.PlayStation, 0, LeftTrigger: 12000)]; Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.PlayStation, 0)]; Assert.Empty(nav.Poll(true));
        source.States = [new(1, ControllerKind.PlayStation, 0, LeftTrigger: 32767)];
        Assert.Equal([ControllerAction.PreviousShelfPage], nav.Poll(true));
    }
}
