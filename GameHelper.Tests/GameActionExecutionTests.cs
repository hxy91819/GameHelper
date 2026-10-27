using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class GameActionExecutionTests
{
    [Fact]
    public void LibraryLaunch_BeforeActionsRunInListOrderBeforeGame()
    {
        using var app = new Fixture();
        app.Game.Actions = [Action("one"), Action("exit", "afterExit"), Action("two")];
        app.Service.Launch("game");
        Assert.Equal(new[] { "one", "two", "game" }, app.Calls);
    }

    [Fact]
    public void BeforeFailure_AbortsRemainingActionsAndGameAndRestoresOwnedHdr()
    {
        using var app = new Fixture();
        app.Game.HdrEnabled = true;
        app.Game.Actions = [Action("fail"), Action("later")];
        app.OnAction = _ => throw new IOException("failure");
        Assert.Contains("启动前动作失败", Assert.Throws<InvalidOperationException>(() => app.Service.Launch("game")).Message);
        Assert.Equal(new[] { "fail" }, app.Calls);
        Assert.False(app.Hdr.Enabled);
        Assert.Empty(app.Service.GetGameStates());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LastProcessExit_ExecutesOnceForLibraryAndExternalGames(bool library)
    {
        using var app = new Fixture();
        app.Game.Actions = [Action("before"), Action("exit-one", "afterExit"), Action("exit-two", "afterExit")];
        if (library) app.Service.Launch("game");
        app.Start(1); app.Start(2); app.Calls.Clear();
        app.Stop(1); Assert.Empty(app.Calls);
        app.Stop(2); app.Stop(2);
        Assert.Equal(new[] { "exit-one", "exit-two" }, app.Calls);
        app.Play.Verify(play => play.StopTracking("game"), Times.Once);
    }

    [Fact]
    public void ExternalStart_DoesNotRunBeforeActions()
    {
        using var app = new Fixture();
        app.Game.Actions = [Action("before")];
        app.Start(1);
        Assert.Empty(app.Calls);
    }

    [Fact]
    public void AfterFailure_WarnsAndContinuesRemainingActions()
    {
        var logger = new Mock<ILogger<GameAutomationService>>();
        using var app = new Fixture(logger: logger.Object);
        app.Game.Actions = [Action("fail", "afterExit"), Action("later", "afterExit")];
        app.OnAction = action => { if (action.Path == "fail") throw new IOException("failure"); };
        app.Start(1); app.Stop(1);
        Assert.Equal(new[] { "fail", "later" }, app.Calls);
        logger.Verify(log => log.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public void StopAndRestartInsideAction_InvalidatesRestOfLaunch()
    {
        using var app = new Fixture();
        app.Game.Actions = [Action("one"), Action("two")];
        app.OnAction = _ => { app.Service.Stop(); app.Service.Start(); };
        Assert.Throws<InvalidOperationException>(() => app.Service.Launch("game"));
        Assert.Equal(new[] { "one" }, app.Calls);
    }

    [Fact]
    public void StopInsideAfterAction_InvalidatesRemainingActions()
    {
        using var app = new Fixture();
        app.Game.Actions = [Action("one", "afterExit"), Action("two", "afterExit")];
        app.OnAction = _ => app.Service.Stop();
        app.Start(1); app.Stop(1);
        Assert.Equal(new[] { "one" }, app.Calls);
    }

    [Fact]
    public void StopDoesNotRunExitActionsAndOldStopDelegateCannotBorrowRestartGeneration()
    {
        using var app = new Fixture();
        app.Game.Actions = [Action("exit", "afterExit")];
        app.Start(1);
        var oldStop = app.Monitor.Stopped;
        app.Service.Stop(); app.Service.Start(); app.Start(2);
        oldStop?.Invoke(new("game.exe", @"C:\Games\game.exe", 2));
        Assert.Empty(app.Calls);
        app.Stop(2);
        Assert.Equal(new[] { "exit" }, app.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_NeverReplaysBeforeOrDowntimeExitButLiveRecoveredSessionRunsAfterExit(bool alive)
    {
        var snapshot = new InFlightSnapshot { SnapshotAt = DateTime.UnixEpoch.AddHours(1), Sessions = [new()
        {
            DataKey = "game", ProcessId = 1, NormalizedName = "game.exe", NormalizedPath = @"C:\Games\game.exe",
            StartedAt = DateTime.UnixEpoch
        }] };
        using var app = new Fixture(snapshot, alive);
        Assert.Empty(app.Calls);
        if (alive)
        {
            app.Play.Verify(play => play.ResumeTracking("game", DateTime.UnixEpoch), Times.Once);
            app.Stop(1);
            Assert.Equal(new[] { "exit" }, app.Calls);
        }
        else
        {
            app.Play.Verify(play => play.WriteRecoveredSession("game", It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Once);
            app.Stop(1); Assert.Empty(app.Calls);
        }
    }

    [Fact]
    public void ActionIo_DoesNotHoldStateLock()
    {
        using var app = new Fixture();
        app.Game.Actions = [Action("one")];
        app.OnAction = _ =>
        {
            // A separate reader must complete while executable I/O is entered.
            var read = Task.Run(app.Service.GetGameStates);
            Assert.True(read.Wait(TimeSpan.FromSeconds(5)));
        };
        app.Service.Launch("game");
    }

    private static GameAction Action(string path, string when = "beforeLaunch") => new() { Path = path, When = when, Type = "run" };

    private sealed class Fixture : IDisposable
    {
        public GameConfig Game { get; } = new() { DataKey = "game", Executable = @"C:\Games\game.exe",
            Launch = new() { Target = "steam://rungameid/620" }, Actions = [Action("before"), Action("exit", "afterExit")] };
        public Monitor Monitor { get; } = new();
        public Mock<IPlayTimeService> Play { get; } = new();
        public Hdr Hdr { get; } = new();
        public List<string> Calls { get; } = [];
        public Action<GameAction>? OnAction { get; set; }
        public GameAutomationService Service { get; }
        public Fixture(InFlightSnapshot? snapshot = null, bool alive = false, ILogger<GameAutomationService>? logger = null)
        {
            var config = new Mock<IGameConfiguration>();
            config.Setup(value => value.Read()).Returns(new AppConfig { Games = [Game] });
            var launcher = new Mock<IGameLauncher>();
            launcher.Setup(value => value.ExecuteAction(It.IsAny<GameAction>())).Callback<GameAction>(action => { Calls.Add(action.Path); OnAction?.Invoke(action); });
            launcher.Setup(value => value.Start(It.IsAny<GameLaunchOptions>())).Callback(() => Calls.Add("game"));
            var store = new Mock<IInFlightSessionStore>();
            store.Setup(value => value.Load()).Returns(snapshot);
            Service = new GameAutomationService(Monitor, config.Object, Hdr, Play.Object, logger ?? NullLogger<GameAutomationService>.Instance,
                gameLauncher: launcher.Object, inFlightSessionStore: store.Object, processIdentityProbe: new Probe(alive), timeProvider: new Clock());
            Service.Start();
        }
        public void Start(int pid) => Monitor.Started?.Invoke(new("game.exe", @"C:\Games\game.exe", pid));
        public void Stop(int pid) => Monitor.Stopped?.Invoke(new("game.exe", @"C:\Games\game.exe", pid));
        public void Dispose() => Service.Stop();
    }
    private sealed class Monitor : IProcessMonitor
    {
        public Action<ProcessEventInfo>? Started;
        public Action<ProcessEventInfo>? Stopped;
        public event Action<ProcessEventInfo>? ProcessStarted { add => Started += value; remove => Started -= value; }
        public event Action<ProcessEventInfo>? ProcessStopped { add => Stopped += value; remove => Stopped -= value; }
        public void Configure(ProcessObservationPolicy policy) { }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
    private sealed class Hdr : IHdrController
    {
        public bool Enabled { get; private set; }
        public bool IsEnabled => Enabled;
        public bool TryGetEnabled(out bool enabled) { enabled = Enabled; return true; }
        public void Enable() => Enabled = true;
        public void Disable() => Enabled = false;
    }
    private sealed class Probe(bool alive) : IProcessIdentityProbe
    {
        public bool TryGetImagePath(int processId, out string? path) { path = alive ? @"C:\Games\game.exe" : null; return alive; }
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddHours(2);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => Mock.Of<ITimer>();
    }
}
