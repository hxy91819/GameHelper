using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using GameHelper.Infrastructure.Controllers;

namespace GameHelper.Tests.Services;

public sealed class MonitorModeAutomationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LibraryHdrLaunch_DisplayPolicy_SelectsHdrOrSdrGamePreset(bool whitelist, bool televisionConnected)
    {
        var displays = new List<WindowsHdrController.DisplayAdvancedColorInfo>
        {
            new("desk", true, false, false, "desk-path")
        };
        if (televisionConnected) displays.Add(new("TV", true, false, false, "tv-path"));
        var hdrWrites = new List<(string, bool)>();
        var hdr = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance, () => displays.ToArray(),
            (display, enabled) =>
            {
                hdrWrites.Add((display.Name, enabled));
                var index = displays.FindIndex(d => d.DevicePath == display.DevicePath);
                displays[index] = display with { Enabled = enabled };
            }, () => { });
        var config = new AppConfig
        {
            Hdr = whitelist ? new HdrSettings { AllowedDisplays = ["TV"] }
                : new HdrSettings { DisabledDisplays = ["desk-path"] },
            MonitorMode = new MonitorModeSettings { Enabled = true, DaylightStart = "00:00", DaylightEnd = "23:59" },
            Games = [new() { DataKey = "game", Executable = "game.exe", HdrEnabled = true, IsEnabled = true,
                Launch = new() { Target = @"C:\Games\game.exe" } }]
        };
        var source = new Mock<IGameConfiguration>();
        source.Setup(c => c.Read()).Returns(config);
        var monitor = new Mock<IProcessMonitor>();
        var launcher = new Mock<IGameLauncher>();
        var store = new Mock<IInFlightSessionStore>();
        InFlightSnapshot? snapshot = null;
        store.Setup(s => s.Save(It.IsAny<InFlightSnapshot>())).Callback<InFlightSnapshot>(s => snapshot = s);
        var clock = new Clock();
        var ddc = new Controller();
        var automation = new GameAutomationService(monitor.Object, source.Object, hdr,
            Mock.Of<IPlayTimeService>(), NullLogger<GameAutomationService>.Instance,
            monitorModeController: ddc, timeProvider: clock, gameLauncher: launcher.Object, inFlightSessionStore: store.Object);
        try
        {
            automation.Start();
            var initialTouches = ddc.Touches;
            automation.Launch("game");
            launcher.Verify(l => l.Start(It.IsAny<GameLaunchOptions>()), Times.Once);
            Assert.Equal(televisionConnected, snapshot!.HdrEnabledByGameHelper);
            monitor.Raise(m => m.ProcessStarted += null, new ProcessEventInfo("game.exe", null, 123));
            clock.Tick();
            Assert.False(displays[0].Enabled);
            if (televisionConnected)
            {
                Assert.Equal(new[] { ("TV", true) }, hdrWrites);
                Assert.Equal(initialTouches, ddc.Touches);
                // Hot reload must not treat an existing lease as a policy-skipped launch,
                // even if an external actor turns HDR off before the next evaluation.
                config.Hdr = new HdrSettings { DisabledDisplays = ["desk", "TV"] };
                displays[1] = displays[1] with { Enabled = false };
                automation.ReloadConfig();
                Assert.Equal(HdrAutomationAvailability.ExcludedByPolicy, hdr.GetAutomationAvailability());
                clock.Tick();
                Assert.Equal(initialTouches, ddc.Touches);
            }
            else
            {
                Assert.Empty(hdrWrites);
                Assert.Equal(0u, ddc.State[0xF0]); // Daylight game recipe, not comfort.
                Assert.False(snapshot.HdrEnabledByGameHelper);
            }
            monitor.Raise(m => m.ProcessStopped += null, new ProcessEventInfo("game.exe", null, 123));
            Assert.Equal(0x0Cu, ddc.State[0xF0]);
            Assert.All(displays, display => Assert.False(display.Enabled));
        }
        finally { automation.Stop(); }
    }

    [Fact]
    public void SkippedHdrGame_ConnectTelevisionOnTick_EnablesHdrAndSuspendsDdc()
    {
        var televisionConnected = false;
        var televisionHdr = false;
        var queryFails = false;
        var hdr = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance,
            () =>
            {
                if (queryFails) throw new InvalidOperationException("display query failed");
                return televisionConnected
                    ? [new("TV", true, televisionHdr, false, "tv-path")]
                    : new[] { new WindowsHdrController.DisplayAdvancedColorInfo("desk", true, false, false, "desk-path") };
            },
            (_, enabled) => televisionHdr = enabled, () => { });
        var config = new AppConfig
        {
            Hdr = new HdrSettings { AllowedDisplays = ["TV"] },
            MonitorMode = new MonitorModeSettings { Enabled = true, DaylightStart = "00:00", DaylightEnd = "23:59" },
            Games = [new() { DataKey = "game", Executable = "game.exe", HdrEnabled = true, IsEnabled = true }]
        };
        var source = new Mock<IGameConfiguration>();
        source.Setup(c => c.Read()).Returns(config);
        var monitor = new Mock<IProcessMonitor>();
        var clock = new Clock();
        var ddc = new Controller();
        var automation = new GameAutomationService(monitor.Object, source.Object, hdr,
            Mock.Of<IPlayTimeService>(), NullLogger<GameAutomationService>.Instance,
            monitorModeController: ddc, timeProvider: clock, gameLauncher: Mock.Of<IGameLauncher>());
        try
        {
            automation.Start();
            automation.Launch("game");
            monitor.Raise(m => m.ProcessStarted += null, new ProcessEventInfo("game.exe", null, 123));
            Assert.Equal(0u, ddc.State[0xF0]);
            var touches = ddc.Touches;
            queryFails = true;
            clock.Tick();
            Assert.Equal(touches, ddc.Touches); // Query failure must not keep using SDR fallback.
            queryFails = false;
            televisionConnected = true;
            clock.Tick();
            Assert.True(televisionHdr);
            Assert.Equal(touches, ddc.Touches);
        }
        finally { automation.Stop(); }
    }

    [Fact]
    public void HdrEnabled_StartReloadGameEventsAndTicks_DoNotTouchDdc()
    {
        var config = new AppConfig
        {
            MonitorMode = new MonitorModeSettings { Enabled = true },
            Games = new() { new GameConfig { DataKey = "game", Executable = "game.exe", IsEnabled = true } }
        };
        var source = new Mock<IGameConfiguration>();
        source.Setup(c => c.Read()).Returns(config);
        var monitor = new Mock<IProcessMonitor>();
        var clock = new Clock();
        var controller = new Controller();
        var hdr = new Hdr { Enabled = true };
        var automation = new GameAutomationService(monitor.Object, source.Object, hdr,
            Mock.Of<IPlayTimeService>(), NullLogger<GameAutomationService>.Instance,
            monitorModeController: controller, timeProvider: clock);
        try
        {
            automation.Start();
            automation.ReloadConfig();
            monitor.Raise(m => m.ProcessStarted += null, new ProcessEventInfo("game.exe", null, 123));
            monitor.Raise(m => m.ProcessStopped += null, new ProcessEventInfo("game.exe", null, 123));
            clock.Tick();
            Assert.Equal(0, controller.Touches);
            Assert.True(hdr.Enabled);
        }
        finally { automation.Stop(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HdrEnabledOrUnknown_ThenSdr_ReevaluatesUnchangedPresetRule(bool querySucceeded)
    {
        var source = new Mock<IGameConfiguration>();
        source.Setup(c => c.Read()).Returns(new AppConfig { MonitorMode = new MonitorModeSettings { Enabled = true } });
        var clock = new Clock();
        var controller = new Controller();
        var hdr = new Hdr();
        var automation = new GameAutomationService(Mock.Of<IProcessMonitor>(), source.Object, hdr,
            Mock.Of<IPlayTimeService>(), NullLogger<GameAutomationService>.Instance,
            monitorModeController: controller, timeProvider: clock);
        try
        {
            automation.Start();
            Assert.Equal(0x0Cu, controller.State[0xF0]);
            var sdrTouches = controller.Touches;
            hdr.Enabled = querySucceeded;
            hdr.Known = querySucceeded; // Unknown deliberately has a false enabled value.
            controller.State.Clear();
            clock.Tick();
            automation.ReloadConfig();
            Assert.Equal(sdrTouches, controller.Touches);
            hdr.Enabled = false;
            hdr.Known = true;
            clock.Tick();
            Assert.Equal(0x0Cu, controller.State[0xF0]);
        }
        finally { automation.Stop(); }
    }

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
            new Hdr(), Mock.Of<IPlayTimeService>(), NullLogger<GameAutomationService>.Instance,
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
            new Hdr(), Mock.Of<IPlayTimeService>(), NullLogger<GameAutomationService>.Instance,
            monitorModeController: controller, timeProvider: clock);
        automation.Start();
        Assert.Equal(0x0Cu, controller.State[0xF0]);
        config.MonitorMode.ComfortPresets.Single(p => p.Code == "F0").Value = "0x0D";
        automation.ReloadConfig();
        Assert.Equal(0x0Du, controller.State[0xF0]);
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
            new Hdr(), Mock.Of<IPlayTimeService>(), NullLogger<GameAutomationService>.Instance,
            monitorModeController: controller, timeProvider: clock);
        automation.Start();
        controller.FailWrites = false;
        clock.Tick();
        Assert.Equal(0x0Cu, controller.State[0xF0]);
        automation.Stop();
    }

    private sealed class Controller : IMonitorModeController
    {
        public int Touches { get; private set; }
        public bool IsAvailable { get { Touches++; return true; } }
        public bool FailWrites { get; set; }
        public Dictionary<byte, uint> State { get; } = new();
        public IReadOnlyDictionary<byte, uint>? TryReadState(IReadOnlyList<MonitorModeWrite> probeCodes)
        {
            Touches++;
            return new Dictionary<byte, uint>(State);
        }
        public void Apply(IReadOnlyList<MonitorModeWrite> writes)
        {
            Touches++;
            if (FailWrites) return;
            foreach (var write in writes) State[write.Code] = write.Value;
        }
    }

    private sealed class Hdr : IHdrController
    {
        public bool Enabled { get; set; }
        public bool Known { get; set; } = true;
        public bool IsEnabled => Enabled;
        public bool TryGetEnabled(out bool enabled) { enabled = Enabled; return Known; }
        public void Enable() => Enabled = true;
        public void Disable() => Enabled = false;
    }

    private sealed class Clock : TimeProvider
    {
        private Action? _tick;
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
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
