using GameHelper.Core.Services;
using GameHelper.Desktop;
using Xunit;
namespace GameHelper.Input.Tests;

public sealed class LaunchSessionCorrelationTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }
    [Fact] public void UriSessionInsideWindowMinimizesThenRestoresAfterLongSession()
    {
        var clock = new Clock(); var correlation = new LaunchSessionCorrelation(clock); var window = new ConsoleLaunchState(clock);
        window.Begin("steam", "Steam game"); var request = window.RequestId!.Value;
        Assert.True(correlation.Begin("steam",request,null,clock.GetUtcNow().AddMinutes(2),false));
        clock.Advance(TimeSpan.FromSeconds(119)); correlation.Observe("steam",42,true,false);
        var identity = correlation.Snapshot()["steam"];
        Assert.Equal((request,42),identity);
        Assert.Equal(ConsoleLaunchState.Effect.Minimize,window.Observe(true,identity.RequestId,identity.ProcessId));
        Assert.False(window.ShowOverlay);
        // Additional processes never replace the first session's identity.
        correlation.Observe("steam",43,false,false); Assert.Equal(identity,correlation.Snapshot()["steam"]);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(ConsoleLaunchState.Effect.None,window.Observe(true,identity.RequestId,identity.ProcessId));
        correlation.Release("steam");
        Assert.Equal(ConsoleLaunchState.Effect.Restore,window.Observe(false,null,null));
        Assert.Equal(ConsoleLaunchState.Effect.None,window.Observe(false,null,null));
    }
    [Fact] public void AssociationAt119SecondsIsAcceptedWhenResponseArrivesAfterDeadline()
    {
        var clock = new Clock(); var correlation = new LaunchSessionCorrelation(clock); var window = new ConsoleLaunchState(clock);
        window.Begin("steam", "Steam game");
        correlation.Begin("steam",window.RequestId!.Value,null,clock.GetUtcNow().AddMinutes(2),false);
        clock.Advance(TimeSpan.FromSeconds(119)); correlation.Observe("steam",42,true,false);
        var identity = correlation.Snapshot()["steam"];
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(ConsoleLaunchState.Effect.Minimize,window.Observe(true,identity.RequestId,identity.ProcessId));
        Assert.Equal(ConsoleLaunchState.Phase.Running,window.State); Assert.False(window.ShowOverlay);
        Assert.Equal(ConsoleLaunchState.Effect.Restore,window.Observe(false,null,null));
    }
    [Theory] [InlineData(120)] [InlineData(121)]
    public void UriSessionAtOrAfterDeadlineCannotMinimizeOrRestore(int seconds)
    {
        var clock = new Clock(); var correlation = new LaunchSessionCorrelation(clock); var window = new ConsoleLaunchState(clock);
        window.Begin("steam", "Steam game");
        correlation.Begin("steam",window.RequestId!.Value,null,clock.GetUtcNow().AddMinutes(2),false);
        clock.Advance(TimeSpan.FromSeconds(seconds)); correlation.Observe("steam",42,true,false);
        Assert.Empty(correlation.Snapshot());
        Assert.Equal(ConsoleLaunchState.Effect.None,window.Observe(true,null,42));
        Assert.Equal(ConsoleLaunchState.Phase.TimedOut,window.State);
        Assert.Equal(ConsoleLaunchState.Effect.None,window.Observe(false,null,null));
    }
    [Fact] public void AlreadyRunningOrPreexistingSessionCannotBeClaimed()
    {
        var clock = new Clock(); var correlation = new LaunchSessionCorrelation(clock); var deadline = clock.GetUtcNow().AddMinutes(2);
        Assert.False(correlation.Begin("steam",Guid.NewGuid(),null,deadline,true));
        correlation.Observe("steam",42,false,false); Assert.Empty(correlation.Snapshot());
        correlation.Begin("steam",Guid.NewGuid(),null,deadline,false);
        correlation.Observe("steam",42,true,true); Assert.Empty(correlation.Snapshot());
    }
    [Fact] public void KnownPidNeverFallsBackToAnotherProcess()
    {
        var clock = new Clock(); var correlation = new LaunchSessionCorrelation(clock);
        correlation.Begin("game",Guid.NewGuid(),42,clock.GetUtcNow().AddMinutes(2),false);
        correlation.Observe("game",43,true,false); Assert.Empty(correlation.Snapshot());
    }
}
