using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class GameLaunchCoordinatorTests
{
    [Theory]
    [InlineData(HdrAutomationAvailability.Unknown)]
    [InlineData(HdrAutomationAvailability.Unavailable)]
    public void HdrDisplayAvailabilityNotConfirmed_NeverCreatesGameOrLease(HdrAutomationAvailability availability)
    {
        using var app = new Fixture();
        app.Hdr.Availability = availability;
        Assert.Throws<InvalidOperationException>(() => app.Service.Launch("a"));
        Assert.Equal(0, app.Launcher.Starts);
        Assert.False(app.Hdr.IsEnabled);
        Assert.False(app.Store.Snapshot!.HdrEnabledByGameHelper);
    }

    [Fact]
    public void HdrDisplayPolicySkipped_ExpiryAndRestart_DoNotAcquireLease()
    {
        using var first = new Fixture();
        first.Hdr.Availability = HdrAutomationAvailability.ExcludedByPolicy;
        first.Service.Launch("a");
        Assert.Equal(1, first.Launcher.Starts);
        Assert.False(first.Store.Snapshot!.HdrEnabledByGameHelper);
        using var second = new Fixture(sharedHdr: first.Hdr, sharedStore: first.Store);
        Assert.False(second.Hdr.IsEnabled);
        second.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False(second.Store.Snapshot!.HdrEnabledByGameHelper);
        Assert.Equal(0, second.Hdr.DisableCalls);
    }

    [Fact]
    public void Launch_ConfirmsHdrBeforeCreatingProcess_ThenTracksOnlyObservedProcess()
    {
        using var app = new Fixture();
        app.Launcher.OnStart = () => Assert.True(app.Hdr.IsEnabled);
        app.Service.Launch("a");
        Assert.Equal("启动中", app.Service.GetGameStates()["a"]);
        app.Playtime.Verify(playtime => playtime.StartTracking(It.IsAny<string>()), Times.Never);
        Assert.True(app.Store.Snapshot!.HdrEnabledByGameHelper);
        Assert.Throws<InvalidOperationException>(() => app.Service.Launch("a"));
        app.Start("a", 101);
        Assert.Equal("运行中", app.Service.GetGameStates()["a"]);
        Assert.Empty(app.Store.Snapshot!.PendingLaunches);
        app.Playtime.Verify(playtime => playtime.StartTracking("a"), Times.Once);
        app.Stop("a", 101);
        Assert.False(app.Hdr.IsEnabled);
        Assert.False(app.Store.Snapshot!.HdrEnabledByGameHelper);
    }

    [Fact]
    public void PendingSecondHdrGame_KeepsHdrEnabledAfterFirstExits_ThenExpires()
    {
        using var app = new Fixture();
        app.Service.Launch("a"); app.Start("a", 101);
        app.Service.Launch("b"); app.Stop("a", 101);
        Assert.True(app.Hdr.IsEnabled);
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(app.Hdr.IsEnabled);
        Assert.Contains("超时", app.Service.GetGameStates()["b"]);
        Assert.False(app.Store.Snapshot!.HdrEnabledByGameHelper);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HdrUnknownOrUnconfirmed_NeverCreatesTheGame(bool enableFails)
    {
        using var app = new Fixture();
        app.Hdr.Known = enableFails;
        app.Hdr.EnableFails = enableFails;
        Assert.Throws<InvalidOperationException>(() => app.Service.Launch("a"));
        Assert.Equal(0, app.Launcher.Starts);
        Assert.False(app.Hdr.IsEnabled);
        Assert.DoesNotContain("a", app.Service.GetGameStates().Keys);
    }

    [Fact]
    public void RequestIdentityBindsOnlyConfirmedProcessAndIsRemovedOnExit()
    {
        using var app = new Fixture(); var id = Guid.NewGuid();
        app.Service.Launch("a", id);
        Assert.Empty(app.Service.GetLaunchIdentities());
        app.Start("a", 101);
        Assert.Equal((id, 101), app.Service.GetLaunchIdentities()["a"]);
        app.Stop("a", 101); Assert.Empty(app.Service.GetLaunchIdentities());
        app.Start("a", 102); Assert.Empty(app.Service.GetLaunchIdentities());
    }

    [Theory]
    [InlineData(999)]
    public void DifferentKnownPidCannotBindExternalStart(int? pid)
    {
        using var app = new Fixture(); app.Launcher.TrackedPid = pid;
        app.Service.Launch("a", Guid.NewGuid()); app.Start("a", 101);
        Assert.Empty(app.Service.GetLaunchIdentities());
    }

    [Fact]
    public void OpaqueLaunchBindsFirstFreshProcessUntilSessionExit()
    {
        using var app = new Fixture(); app.Launcher.TrackedPid = null; var id = Guid.NewGuid();
        app.Service.Launch("a", id); app.Start("a", 101);
        Assert.Equal((id, 101), app.Service.GetLaunchIdentities()["a"]);
        app.Start("a", 102);
        app.Stop("a", 101); Assert.Equal((id, 101), app.Service.GetLaunchIdentities()["a"]);
        app.Stop("a", 102); Assert.Empty(app.Service.GetLaunchIdentities());
    }

    [Fact]
    public void AlreadyRunningCannotBecomeOpaqueLaunch()
    {
        using var app = new Fixture(); app.Launcher.TrackedPid = null; app.Start("a", 101);
        Assert.Throws<InvalidOperationException>(() => app.Service.Launch("a", Guid.NewGuid()));
        Assert.Empty(app.Service.GetLaunchIdentities());
    }

    [Fact]
    public void ExpiredRequestCannotBindLaterExternalProcess()
    {
        using var app = new Fixture(); app.Launcher.TrackedPid = null; app.Service.Launch("a", Guid.NewGuid());
        app.Clock.Advance(TimeSpan.FromMinutes(3)); app.Start("a", 101);
        Assert.Empty(app.Service.GetLaunchIdentities());
    }

    [Fact]
    public void ProcessCreationFailure_RestoresOwnedHdr()
    {
        using var app = new Fixture();
        app.Launcher.OnStart = () => throw new IOException("entry missing");
        Assert.Throws<IOException>(() => app.Service.Launch("a"));
        Assert.False(app.Hdr.IsEnabled);
        Assert.Empty(app.Store.Snapshot!.PendingLaunches);
        Assert.False(app.Store.Snapshot!.HdrEnabledByGameHelper);
    }

    [Fact]
    public void OriginallyEnabledHdr_IsLeftEnabledAfterGameExit()
    {
        using var app = new Fixture(initialHdr: true);
        app.Service.Launch("a"); app.Start("a", 101); app.Stop("a", 101);
        Assert.True(app.Hdr.IsEnabled);
        Assert.Equal(0, app.Hdr.DisableCalls);
    }

    [Fact]
    public void PendingHdrLease_SurvivesRestartWithoutReplayingLaunch()
    {
        using var first = new Fixture();
        first.Service.Launch("a");
        using var second = new Fixture(sharedHdr: first.Hdr, sharedStore: first.Store);
        Assert.True(second.Hdr.IsEnabled);
        Assert.Equal("启动中", second.Service.GetGameStates()["a"]);
        Assert.Equal(0, second.Launcher.Starts);
        second.Start("a", 101); second.Stop("a", 101);
        Assert.False(second.Hdr.IsEnabled);
    }

    [Fact]
    public void ExpiredHdrLease_AfterRestart_RestoresAndPersistsTheRestoration()
    {
        using var first = new Fixture();
        first.Service.Launch("a");
        var clock = new Clock();
        clock.Advance(TimeSpan.FromMinutes(3));
        using var second = new Fixture(sharedHdr: first.Hdr, sharedStore: first.Store, clock: clock);
        Assert.False(second.Hdr.IsEnabled);
        Assert.False(second.Store.Snapshot!.HdrEnabledByGameHelper);
    }

    [Fact]
    public void DisabledAutomation_StillAllowsPlainLaunchWithoutHdrOrPendingTracking()
    {
        using var app = new Fixture(disabled: true);
        app.Service.Launch("a");
        Assert.Equal(1, app.Launcher.Starts);
        Assert.False(app.Hdr.IsEnabled);
        Assert.Empty(app.Service.GetGameStates());
    }

    [Fact]
    public void Launch_WhileOwnLaunchedGameRunning_Throws()
    {
        using var app = new Fixture();
        app.Service.Launch("a"); app.Start("a", 101);
        Assert.Throws<InvalidOperationException>(() => app.Service.Launch("a"));
        Assert.Equal(1, app.Launcher.Starts);
    }

    [Fact]
    public void Launch_WhileExternalGameRunning_RejectsLateHdrAndRelaunch()
    {
        using var app = new Fixture();
        app.Start("a", 101);
        Assert.Throws<InvalidOperationException>(() => app.Service.Launch("a"));
        Assert.Equal(0, app.Launcher.Starts);
        Assert.Equal("运行中·外部", app.Service.GetGameStates()["a"]);
        Assert.False(app.Hdr.IsEnabled);
        app.Playtime.Verify(playtime => playtime.StartTracking("a"), Times.Once);
    }

    [Fact]
    public void MixedDisplays_ExcludedHdrDesktopAndAllowedSdrTelevision_RejectsLaunch()
    {
        var controller = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance,
            () => new[]
            {
                new WindowsHdrController.DisplayAdvancedColorInfo("desk", true, false, false, "desk-path"),
                new WindowsHdrController.DisplayAdvancedColorInfo("TV", false, false, false, "tv-path")
            });
        controller.Configure(new HdrSettings { AllowedDisplays = ["tv-path"] });
        Assert.Equal(HdrAutomationAvailability.Unavailable, controller.GetAutomationAvailability());

        using var app = new Fixture(hdrController: controller,
            hdrSettings: new HdrSettings { AllowedDisplays = ["tv-path"] });
        Assert.Throws<InvalidOperationException>(() => app.Service.Launch("a"));
        Assert.Equal(0, app.Launcher.Starts);
        Assert.False(controller.IsEnabled);
        Assert.False(app.Store.Snapshot!.HdrEnabledByGameHelper);
    }

    private sealed class Fixture : IDisposable
    {
        public Mock<IProcessMonitor> Monitor { get; } = new();
        public Mock<IPlayTimeService> Playtime { get; } = new();
        public Hdr Hdr { get; }
        public IHdrController HdrController { get; }
        public Launcher Launcher { get; } = new();
        public Store Store { get; }
        public Clock Clock { get; }
        public GameAutomationService Service { get; }
        public Fixture(bool initialHdr = false, bool disabled = false, Hdr? sharedHdr = null, Store? sharedStore = null,
            Clock? clock = null, IHdrController? hdrController = null, HdrSettings? hdrSettings = null)
        {
            Hdr = sharedHdr ?? new Hdr { IsEnabled = initialHdr };
            HdrController = hdrController ?? Hdr;
            Store = sharedStore ?? new Store(); Clock = clock ?? new Clock();
            var configuration = new Mock<IGameConfiguration>();
            configuration.Setup(source => source.Read()).Returns(new AppConfig
            {
                Hdr = hdrSettings,
                Games = [new() { DataKey = "a", Executable = "a.exe", HdrEnabled = true, IsEnabled = !disabled, Launch = new() { Target = @"C:\Games\a.exe" } },
                    new() { DataKey = "b", Executable = "b.exe", HdrEnabled = true, Launch = new() { Target = @"C:\Games\b.exe" } }]
            });
            Service = new GameAutomationService(Monitor.Object, configuration.Object, HdrController, Playtime.Object,
                NullLogger<GameAutomationService>.Instance, inFlightSessionStore: Store, timeProvider: Clock, gameLauncher: Launcher);
            Service.Start();
        }
        public void Start(string key, int pid) => Monitor.Raise(monitor => monitor.ProcessStarted += null, new ProcessEventInfo(key + ".exe", null, pid));
        public void Stop(string key, int pid) => Monitor.Raise(monitor => monitor.ProcessStopped += null, new ProcessEventInfo(key + ".exe", null, pid));
        public void Dispose() => Service.Stop();
    }
    private sealed class Hdr : IHdrController
    {
        public bool IsEnabled { get; set; }
        public bool Known { get; set; } = true;
        public bool EnableFails { get; set; }
        public HdrAutomationAvailability Availability { get; set; } = HdrAutomationAvailability.Available;
        public HdrAutomationAvailability GetAutomationAvailability() => Availability;
        public int DisableCalls { get; private set; }
        public bool TryGetEnabled(out bool enabled) { enabled = IsEnabled; return Known; }
        public void Enable() { if (!EnableFails) IsEnabled = true; }
        public void Disable() { IsEnabled = false; DisableCalls++; }
    }
    private sealed class Launcher : IGameLauncher
    {
        public int? TrackedPid { get; set; } = 101;
        public int? StartTracked(GameLaunchOptions options) { Start(options); return TrackedPid; }
        public int Starts { get; private set; }
        public Action? OnStart { get; set; }
        public void ExecuteAction(GameAction action) { }
        public void StopCompanion() { }
        public void EnsureCompanionRunning(string path) { }
            public void Validate(GameLaunchOptions options) { }
        public void Start(GameLaunchOptions options) { Starts++; OnStart?.Invoke(); }
    }
    private sealed class Store : IInFlightSessionStore
    {
        public InFlightSnapshot? Snapshot { get; private set; }
        public InFlightSnapshot? Load() => Snapshot;
        public void Save(InFlightSnapshot snapshot) => Snapshot = snapshot;
        public void Clear() => Snapshot = null;
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private Action? _tick;
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _tick = () => callback(state); return Mock.Of<ITimer>(); }
        public void Advance(TimeSpan duration) { _now += duration; _tick?.Invoke(); }
    }
}
