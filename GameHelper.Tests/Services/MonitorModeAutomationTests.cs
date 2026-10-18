using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests.Services;

public sealed class MonitorModeAutomationTests
{
    [Fact]
    public async Task AutoLocation_CooldownTicksDoNotPreventLaterRetry()
    {
        var config = new AppConfig { MonitorMode = new MonitorModeSettings { Enabled = true, Location = "auto" } };
        var source = new Mock<IGameConfiguration>();
        source.Setup(c => c.Read()).Returns(config);
        var location = new Mock<IUserLocationSource>();
        var calls = 0;
        location.Setup(l => l.TryGetLocation()).Returns(() =>
            Interlocked.Increment(ref calls) == 1 ? null : new UserCoordinate(28.228, 112.939));
        var cache = new Mock<ILocationCacheStore>();
        var clock = new Clock();
        var automation = new GameAutomationService(Mock.Of<IProcessMonitor>(), source.Object,
            Mock.Of<IHdrController>(), Mock.Of<IPlayTimeService>(), NullLogger<GameAutomationService>.Instance,
            monitorModeController: new Controller(), locationSource: location.Object, locationCacheStore: cache.Object,
            timeProvider: clock);
        automation.Start();
        for (var tick = 0; tick < 30 && Volatile.Read(ref calls) < 2; tick++)
        {
            await Task.Delay(10);
            clock.Advance(TimeSpan.FromSeconds(10));
            clock.Tick();
        }
        Assert.Equal(2, Volatile.Read(ref calls));
        automation.Stop();
    }

    [Fact]
    public void Reload_ChangesRecipeImmediately_AndStoppedTicksDoNotWrite()
    {
        var config = new AppConfig { MonitorMode = new MonitorModeSettings { Enabled = true } };
        var source = new Mock<IGameConfiguration>();
        source.Setup(c => c.Read()).Returns(() => config);
        var clock = new Clock();
        var controller = new Controller();
        var automation = new GameAutomationService(Mock.Of<IProcessMonitor>(), source.Object,
            Mock.Of<IHdrController>(), Mock.Of<IPlayTimeService>(), NullLogger<GameAutomationService>.Instance,
            monitorModeController: controller, timeProvider: clock);
        automation.Start();
        Assert.Equal(0x1Du, controller.State[0xE2]);
        config.MonitorMode.ComfortPresets.Single(p => p.Code == "E2").Value = "0x1E";
        automation.ReloadConfig();
        Assert.Equal(0x1Eu, controller.State[0xE2]);
        automation.Stop();
        controller.State.Clear();
        clock.Tick(); // A queued callback can arrive after timer disposal.
        Assert.Empty(controller.State);
    }

    [Fact]
    public void FailedHardwareWrite_IsRetriedOnNextTickWithoutGameStateChange()
    {
        var source = new Mock<IGameConfiguration>();
        source.Setup(c => c.Read()).Returns(new AppConfig { MonitorMode = new MonitorModeSettings { Enabled = true } });
        var clock = new Clock();
        var controller = new Controller { FailWrites = true };
        var automation = new GameAutomationService(Mock.Of<IProcessMonitor>(), source.Object,
            Mock.Of<IHdrController>(), Mock.Of<IPlayTimeService>(), NullLogger<GameAutomationService>.Instance,
            monitorModeController: controller, timeProvider: clock);
        automation.Start();
        controller.FailWrites = false;
        clock.Tick();
        Assert.Equal(0x1Du, controller.State[0xE2]);
        automation.Stop();
    }

    private sealed class Controller : IMonitorModeController
    {
        public bool IsAvailable => true;
        public bool FailWrites { get; set; }
        public Dictionary<byte, uint> State { get; } = new();
        public IReadOnlyDictionary<byte, uint>? TryReadState(IReadOnlyList<MonitorModeWrite> codes) => new Dictionary<byte, uint>(State);
        public void Apply(IReadOnlyList<MonitorModeWrite> writes)
        {
            if (FailWrites) return;
            foreach (var write in writes) State[write.Code] = write.Value;
        }
    }

    private sealed class Clock : TimeProvider
    {
        private Action? _tick;
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _tick = () => callback(state);
            return Mock.Of<ITimer>();
        }
        public void Tick() => _tick!();
    }
}
