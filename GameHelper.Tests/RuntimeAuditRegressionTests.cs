using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class RuntimeAuditRegressionTests
{
    [Fact]
    public void UnobservedPidStopCannotFinishAnotherLiveInstanceOfSameGame()
    {
        var starts = new CapturingWatcher();
        var stops = new CapturingWatcher();
        using var monitor = new WmiProcessMonitor(starts, stops);
        var configuration = new Mock<IGameConfiguration>();
        configuration.Setup(c => c.Read()).Returns(new AppConfig
        {
            Games = [new() { DataKey = "game", Executable = "game.exe", IsEnabled = true }]
        });
        var playtime = new Mock<IPlayTimeService>();
        var automation = new GameAutomationService(monitor, configuration.Object, Mock.Of<IHdrController>(),
            playtime.Object, NullLogger<GameAutomationService>.Instance);
        var control = new MonitorControlService(monitor, automation);
        try
        {
            control.Start();
            starts.Raise(new("game.exe", null, 10));
            stops.Raise(new("game.exe", null, 20)); // another instance predates monitoring
            playtime.Verify(p => p.StopTracking("game"), Times.Never);
            stops.Raise(new("game.exe", null, 10));
            playtime.Verify(p => p.StopTracking("game"), Times.Once);
        }
        finally { control.Stop(); }
    }

    [Fact]
    public void WmiOldSubscriptionCannotForwardIntoRestartedLifecycle()
    {
        var starts = new CapturingWatcher();
        var stops = new CapturingWatcher();
        using var monitor = new WmiProcessMonitor(starts, stops);
        var received = new List<ProcessEventInfo>();
        monitor.ProcessStarted += received.Add;
        monitor.ProcessStopped += received.Add;
        monitor.Start();
        var oldStart = starts.Capture();
        var oldStop = stops.Capture();
        monitor.Stop();
        oldStart(new("game.exe", null, 1));
        oldStop(new("game.exe", null, 1));
        Assert.Empty(received);
        monitor.Start();
        oldStart(new("game.exe", null, 1));
        oldStop(new("game.exe", null, 1));
        Assert.Empty(received);
        starts.Raise(new("game.exe", null, 2));
        Assert.Equal(2, Assert.Single(received).ProcessId);
    }

    private sealed class CapturingWatcher : IProcessEventWatcher
    {
        public event Action<ProcessEventInfo>? ProcessEvent;
        public void Start() { }
        public void Stop() { }
        public void Raise(ProcessEventInfo info) => ProcessEvent?.Invoke(info);
        public Action<ProcessEventInfo> Capture() => ProcessEvent!;
    }
}
