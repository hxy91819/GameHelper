using System.Collections.Concurrent;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class EtwProcessMonitorRecoveryTests
{
    private static readonly DateTime StartTime = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HealthCheck_ReaderDiesWithoutCompletionCallback_Recovers()
    {
        var runtime = new FakeRuntime { HoldRecovery = false };
        var clock = new ManualClock();
        using var monitor = new EtwProcessMonitor(runtime, timeProvider: clock);
        monitor.Start();
        runtime.Sessions.Single().LoseReader();
        clock.Tick();
        await monitor.RecoveryTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, runtime.Sessions.Count);
        Assert.True(runtime.Sessions.Last().IsAlive);
        monitor.Stop();
        clock.Tick();
        Assert.Equal(2, runtime.Sessions.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configure_StopToggle_EvictsExitedProcessesAndPreservesLiveOnes(bool exitedWhileDisabled)
    {
        var runtime = new FakeRuntime();
        using var monitor = new EtwProcessMonitor(runtime, new[] { "game.exe" });
        var stopped = new List<ProcessEventInfo>();
        monitor.ProcessStopped += stopped.Add;
        monitor.Start();
        var session = runtime.Sessions.Single();
        session.StartProcess(100);
        monitor.Configure(new ProcessObservationPolicy(new[] { "game.exe" }, observeStopEvents: false));
        if (exitedWhileDisabled) session.StopProcess(100);
        monitor.Configure(new ProcessObservationPolicy(new[] { "other.exe" }, observeStopEvents: true));
        session.StopProcess(100);
        Assert.Equal(exitedWhileDisabled ? 0 : 1, stopped.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_StopOrDisposeDuringBackoff_DoesNotRestart(bool dispose)
    {
        var runtime = new FakeRuntime();
        using var monitor = new EtwProcessMonitor(runtime);
        monitor.Start();
        runtime.Sessions.Single().End();
        await runtime.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (dispose) monitor.Dispose(); else monitor.Stop();
        runtime.Continue.TrySetResult();
        await monitor.RecoveryTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(runtime.Sessions);
        Assert.All(runtime.Sessions, session => Assert.True(session.Disposed));
    }

    [Fact]
    public async Task Recovery_StartDuringBackoff_DoesNotCreateCompetingSession()
    {
        var runtime = new FakeRuntime();
        using var monitor = new EtwProcessMonitor(runtime);
        monitor.Start();
        runtime.Sessions.Single().End();
        await runtime.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.Start();
        Assert.Single(runtime.Sessions);
        runtime.Continue.TrySetResult();
        await monitor.RecoveryTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, runtime.Sessions.Count);
        Assert.True(runtime.Sessions.First().Disposed);
        Assert.True(runtime.Sessions.Last().IsAlive);
    }

    [Fact]
    public async Task Recovery_StopThenStart_OldRecoveryCannotChangeNewRun()
    {
        var runtime = new FakeRuntime();
        using var monitor = new EtwProcessMonitor(runtime);
        monitor.Start();
        var oldSession = runtime.Sessions.Single();
        oldSession.End();
        await runtime.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recovery = monitor.RecoveryTask;
        monitor.Stop();
        monitor.Start();
        var newSession = runtime.Sessions.Last();
        oldSession.End();
        runtime.Continue.TrySetResult();
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, runtime.Sessions.Count);
        Assert.True(newSession.IsAlive);
        Assert.False(newSession.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_AllAttemptsFail_DisposesEveryPartialSession(bool readerEnds)
    {
        var runtime = new FakeRuntime { HoldRecovery = false, FailReplacements = !readerEnds, EndReplacements = readerEnds };
        using var monitor = new EtwProcessMonitor(runtime);
        monitor.Start();
        runtime.Sessions.Single().End();
        await monitor.RecoveryTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1 + EtwProcessMonitor.SessionRecoveryAttempts, runtime.Sessions.Count);
        Assert.All(runtime.Sessions, session => Assert.True(session.Disposed));
        runtime.FailReplacements = false;
        runtime.EndReplacements = false;
        monitor.Start();
        Assert.True(runtime.Sessions.Last().IsAlive);
    }

    [Fact]
    public async Task Recovery_LiveGame_PreservesStopAndRejectsOldReaderCallbacks()
    {
        var runtime = new FakeRuntime();
        using var monitor = new EtwProcessMonitor(runtime, new[] { "game.exe" });
        var stopped = new List<ProcessEventInfo>();
        monitor.ProcessStopped += stopped.Add;
        monitor.Start();
        var oldSession = runtime.Sessions.Single();
        oldSession.StartProcess(100);
        oldSession.End();
        await runtime.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Continue.TrySetResult();
        await monitor.RecoveryTask.WaitAsync(TimeSpan.FromSeconds(5));
        oldSession.StopProcess(100);
        Assert.Empty(stopped);
        runtime.Sessions.Last().StopProcess(100);
        Assert.Equal(100, Assert.Single(stopped).ProcessId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_MissedExitOrReusedPid_ClosesOldGameAndNextGameCanFinish(bool reusedPid)
    {
        var runtime = new FakeRuntime();
        using var monitor = new EtwProcessMonitor(runtime);
        var configuration = new Mock<IGameConfiguration>();
        configuration.Setup(x => x.Read()).Returns(new AppConfig
        {
            Games = new List<GameConfig> { new() { DataKey = "game", Executable = "game.exe", IsEnabled = true } }
        });
        var playtime = new Mock<IPlayTimeService>();
        var automation = new GameAutomationService(monitor, configuration.Object, Mock.Of<IHdrController>(),
            playtime.Object, NullLogger<GameAutomationService>.Instance);
        automation.Start();
        monitor.Start();
        var oldSession = runtime.Sessions.Single();
        oldSession.StartProcess(100);
        oldSession.End();
        await runtime.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Processes[100] = reusedPid ? new(false, StartTime.AddSeconds(2)) : new(true, null);
        runtime.Continue.TrySetResult();
        await monitor.RecoveryTask.WaitAsync(TimeSpan.FromSeconds(5));
        playtime.Verify(x => x.StopTracking("game"), Times.Once);

        var session = runtime.Sessions.Last();
        session.StopProcess(100, "other.exe");
        session.StartProcess(200);
        session.StopProcess(200);
        playtime.Verify(x => x.StartTracking("game"), Times.Exactly(2));
        playtime.Verify(x => x.StopTracking("game"), Times.Exactly(2));
        monitor.Stop();
        automation.Stop();
    }

    [Fact]
    public async Task Recovery_UnknownProcessIdentity_DoesNotFabricateStop()
    {
        var runtime = new FakeRuntime { HoldRecovery = false };
        using var monitor = new EtwProcessMonitor(runtime);
        var stopped = new List<ProcessEventInfo>();
        monitor.ProcessStopped += stopped.Add;
        monitor.Start();
        runtime.Sessions.Single().StartProcess(100);
        runtime.Sessions.Single().End();
        await monitor.RecoveryTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(stopped);
        runtime.Sessions.Last().StopProcess(100);
        Assert.Single(stopped);
    }

    [Fact]
    public void ProcessEvents_PidReusedByNonCandidate_ClosesOldGameOnlyOnce()
    {
        var runtime = new FakeRuntime();
        using var monitor = new EtwProcessMonitor(runtime, new[] { "game.exe" });
        var stopped = new List<ProcessEventInfo>();
        monitor.ProcessStopped += stopped.Add;
        monitor.Start();
        var session = runtime.Sessions.Single();
        session.StartProcess(100);
        session.StartProcess(100, "other.exe", 2);
        session.StopProcess(100, "other.exe");
        Assert.Equal("game.exe", Assert.Single(stopped).ExecutableName);
    }

    [Fact]
    public void ProcessEvents_LateStopCannotEndNewIncarnationOfSamePid()
    {
        var runtime = new FakeRuntime();
        using var monitor = new EtwProcessMonitor(runtime);
        var stopped = new List<ProcessEventInfo>();
        monitor.ProcessStopped += stopped.Add;
        monitor.Start();
        var session = runtime.Sessions.Single();
        session.StartProcess(100);
        session.StartProcess(100, seconds: 20);
        Assert.Single(stopped);
        session.StopProcess(100); // timestamp +10 predates the replacement's start.
        Assert.Single(stopped);
        session.StopProcess(100, seconds: 30);
        Assert.Equal(2, stopped.Count);
    }

    private sealed class FakeRuntime : IEtwRuntime
    {
        public ConcurrentQueue<FakeSession> Sessions { get; } = new();
        public ConcurrentDictionary<int, ProcessProbe> Processes { get; } = new();
        public TaskCompletionSource DelayEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldRecovery { get; set; } = true;
        public bool FailReplacements { get; set; }
        public bool EndReplacements { get; set; }
        public void EnsureElevated() { }
        public IEtwSession CreateSession(string name)
        {
            var replacement = !Sessions.IsEmpty;
            var session = new FakeSession(replacement && FailReplacements, replacement && EndReplacements);
            Sessions.Enqueue(session);
            return session;
        }
        public IReadOnlyCollection<string> GetActiveSessionNames() => Array.Empty<string>();
        public void StopSession(string name) => throw new InvalidOperationException("No live sessions may be stopped by unit tests");
        public ProcessProbe InspectProcess(int processId) => Processes.GetValueOrDefault(processId, new(false, null));
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayEntered.TrySetResult();
            return HoldRecovery ? Continue.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
        }
    }

    private sealed class FakeSession(bool failStart, bool endOnStart) : IEtwSession
    {
        private Action<EtwProcessEvent>? _started;
        private Action<EtwProcessEvent>? _stopped;
        private Action? _ended;
        public bool IsAlive { get; private set; }
        public bool Disposed { get; private set; }
        public void Start(Action<EtwProcessEvent> started, Action<EtwProcessEvent> stopped, Action ended)
        {
            _started = started;
            _stopped = stopped;
            _ended = ended;
            if (failStart) throw new InvalidOperationException("Injected provider initialization failure");
            IsAlive = true;
            if (endOnStart) End();
        }
        public void StartProcess(int pid, string name = "game.exe", int seconds = 0) =>
            _started!(new(new ProcessEventInfo(name, null, pid), StartTime.AddSeconds(seconds)));
        public void StopProcess(int pid, string name = "game.exe", int seconds = 10) =>
            _stopped!(new(new ProcessEventInfo(name, null, pid), StartTime.AddSeconds(seconds)));
        public void End() { IsAlive = false; _ended!(); }
        public void LoseReader() => IsAlive = false;
        public void Dispose() { Disposed = true; IsAlive = false; }
    }

    private sealed class ManualClock : TimeProvider
    {
        private Action? _tick;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _tick = () => callback(state);
            return new ManualTimer();
        }
        // Also allows a late queued tick after Dispose, to verify generation checks.
        public void Tick() => _tick!();
        private sealed class ManualTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
