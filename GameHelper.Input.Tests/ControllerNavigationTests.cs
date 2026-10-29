using GameHelper.Desktop.Input;
using Xunit;

public sealed class ControllerNavigationTests
{
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
}
