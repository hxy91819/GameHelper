using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace GameHelper.Tests;

public class InFlightSessionRecoveryTests
{
    private sealed class FakeMonitor : IProcessMonitor
    {
        public event Action<ProcessEventInfo>? ProcessStarted;

        public event Action<ProcessEventInfo>? ProcessStopped;

        public List<ProcessObservationPolicy> Policies { get; } = new();

        public List<(ProcessEventInfo Info, DateTime AssumedTimestampUtc)> AssumedRunning { get; } = new();

        public void Configure(ProcessObservationPolicy policy) => Policies.Add(policy);

        public void AssumeRunning(ProcessEventInfo info, DateTime assumedTimestampUtc) =>
            AssumedRunning.Add((info, assumedTimestampUtc));

        public void Start() { }

        public void Stop() { }

        public void Dispose() { }

        public void RaiseStarted(ProcessEventInfo info) => ProcessStarted?.Invoke(info);

        public void RaiseStopped(ProcessEventInfo info) => ProcessStopped?.Invoke(info);
    }

    private sealed class FakeInFlightStore : IInFlightSessionStore
    {
        public InFlightSnapshot? Snapshot { get; set; }

        public List<InFlightSnapshot> Saved { get; } = new();

        public int ClearCalls { get; private set; }
        public bool FailClear { get; set; }

        public InFlightSnapshot? Load() => Snapshot;

        public void Save(InFlightSnapshot snapshot) => Saved.Add(snapshot);

        public void Clear()
        {
            if (FailClear) throw new IOException("snapshot deletion denied");
            ClearCalls++;
        }
    }

    private sealed class FakeProbe : IProcessIdentityProbe
    {
        private readonly IReadOnlyDictionary<int, string?> _images;

        public FakeProbe(IReadOnlyDictionary<int, string?> images) => _images = images;

        public bool TryGetImagePath(int processId, out string? imagePath)
        {
            if (_images.TryGetValue(processId, out var image) && !string.IsNullOrWhiteSpace(image))
            {
                imagePath = image;
                return true;
            }

            imagePath = null;
            return false;
        }
    }

    private sealed class RecoverySpeedController : IGameSpeedController
    {
        public List<(int ProcessId, double Speed)> Applied { get; } = new();
        public bool ApplySpeed(int processId, double speedMultiplier)
        {
            Applied.Add((processId, speedMultiplier));
            return true;
        }
    }

    private sealed class FakePlayTime : IPlayTimeService
    {
        public List<string> Started { get; } = new();

        public List<(string Game, DateTime Start)> Resumed { get; } = new();

        public List<(string Game, DateTime Start, DateTime End)> Recovered { get; } = new();
        public bool FailRecovery { get; set; }
        public bool FailStop { get; set; }

        public List<string> Stopped { get; } = new();

        public List<TimeSpan> ShiftedStarts { get; } = new();

        public void StartTracking(string gameName) => Started.Add(gameName);

        public void ResumeTracking(string gameName, DateTime startTime) => Resumed.Add((gameName, startTime));

        public void WriteRecoveredSession(string gameName, DateTime startTime, DateTime endTime)
        {
            if (FailRecovery) throw new IOException("History temporarily unavailable");
            Recovered.Add((gameName, startTime, endTime));
        }

        public void ShiftActiveStarts(TimeSpan offset) => ShiftedStarts.Add(offset);

        public PlaySession? StopTracking(string gameName)
        {
            if (FailStop) throw new IOException("History temporarily unavailable");
            Stopped.Add(gameName);
            return new PlaySession(gameName, DateTime.MinValue, DateTime.MinValue, TimeSpan.Zero, 0);
        }
    }

    private static readonly DateTime SessionStart = new(2026, 10, 17, 9, 30, 0);
    private static readonly DateTime SnapshotAt = new(2026, 10, 17, 9, 31, 0);

    private static Mock<IGameConfiguration> CreateConfig()
    {
        var config = new Mock<IGameConfiguration>();
        config.Setup(configuration => configuration.Read()).Returns(new AppConfig
        {
            Games =
            {
                new GameConfig { DataKey = "g1", Executable = "game1.exe", DisplayName = "Game 1" },
                new GameConfig { DataKey = "g2", Executable = "game2.exe", DisplayName = "Game 2" }
            }
        });
        return config;
    }

    private static GameAutomationService CreateService(
        FakeMonitor monitor,
        Mock<IGameConfiguration> config,
        FakePlayTime playTime,
        FakeInFlightStore store,
        IProcessIdentityProbe probe) =>
        new(
            monitor,
            config.Object,
            Mock.Of<IHdrController>(),
            playTime,
            NullLogger<GameAutomationService>.Instance,
            inFlightSessionStore: store,
            processIdentityProbe: probe);

    [Fact]
    public void Recovery_AliveProcess_ResumesSessionWithOriginalStart()
    {
        var monitor = new FakeMonitor();
        var playTime = new FakePlayTime();
        var store = new FakeInFlightStore
        {
            Snapshot = new InFlightSnapshot
            {
                SnapshotAt = SnapshotAt,
                Sessions =
                {
                    new InFlightSession
                    {
                        DataKey = "g1",
                        ProcessId = 1111,
                        NormalizedName = "game1.exe",
                        NormalizedPath = "D:\\Games\\game1.exe",
                        StartedAt = SessionStart
                    }
                }
            }
        };
        var probe = new FakeProbe(new Dictionary<int, string?> { [1111] = "d:\\games\\game1.exe" });

        CreateService(monitor, CreateConfig(), playTime, store, probe).Start();

        var resumed = Assert.Single(playTime.Resumed);
        Assert.Equal("g1", resumed.Game);
        Assert.Equal(SessionStart, resumed.Start);
        Assert.Empty(playTime.Recovered);
        Assert.Equal(0, store.ClearCalls);

        // 被接管进程必须登记为监控层的"已观察"进程,否则它的停止事件与
        // 漏退出补偿都会被 ETW 适配层无视(曾导致时长丢失)。
        var assumed = Assert.Single(monitor.AssumedRunning);
        Assert.Equal(1111, assumed.Info.ProcessId);
        Assert.Equal("game1.exe", assumed.Info.ExecutableName);
        Assert.Equal("D:\\Games\\game1.exe", assumed.Info.ExecutablePath);
        Assert.Equal(SnapshotAt.ToUniversalTime(), assumed.AssumedTimestampUtc);

        // 关键兜底断言：接管完成必须立即重写快照，否则下一次强杀恢复时该会话会第二次丢失。
        Assert.Contains(store.Saved, saved =>
            saved.Sessions.Count == 1
            && saved.Sessions[0].DataKey == "g1"
            && saved.Sessions[0].ProcessId == 1111
            && saved.Sessions[0].StartedAt == SessionStart);

        // The adopted session participates in normal stop matching afterwards.
        monitor.RaiseStopped(new ProcessEventInfo("game1.exe", null, 1111));
        Assert.Contains("g1", playTime.Stopped);
    }

    private static GameAutomationService CreateSpeedService(
        FakeMonitor monitor,
        FakePlayTime playTime,
        FakeInFlightStore store,
        IProcessIdentityProbe probe,
        RecoverySpeedController speed) =>
        new(
            monitor,
            CreateSpeedConfig().Object,
            Mock.Of<IHdrController>(),
            playTime,
            NullLogger<GameAutomationService>.Instance,
            speedController: speed,
            inFlightSessionStore: store,
            processIdentityProbe: probe);

    private static Mock<IGameConfiguration> CreateSpeedConfig()
    {
        var config = new Mock<IGameConfiguration>();
        config.Setup(configuration => configuration.Read()).Returns(new AppConfig
        {
            Games =
            {
                new GameConfig
                {
                    DataKey = "g1",
                    Executable = "D:\\Games\\game1.exe",
                    DisplayName = "Game 1",
                    IsEnabled = true,
                    SpeedMultiplier = 1.5
                }
            }
        });
        return config;
    }

    [Fact]
    public void Recovery_OwnSession_ReAppliesSpeedAndKeepsFlag()
    {
        var store = new FakeInFlightStore
        {
            Snapshot = new InFlightSnapshot
            {
                SnapshotAt = SnapshotAt,
                HdrEnabledByGameHelper = true,
                Sessions =
                {
                    new InFlightSession
                    {
                        DataKey = "g1",
                        ProcessId = 1111,
                        NormalizedName = "game1.exe",
                        NormalizedPath = "D:\\Games\\game1.exe",
                        StartedAt = SessionStart,
                        LaunchedByHelper = true
                    }
                }
            }
        };
        var probe = new FakeProbe(new Dictionary<int, string?> { [1111] = "D:\\Games\\game1.exe" });
        var speed = new RecoverySpeedController();
        var playTime = new FakePlayTime();

        CreateSpeedService(new FakeMonitor(), playTime, store, probe, speed).Start();

        // 库内会话续算：时长接续，变速补挂，快照保持库内标记。
        Assert.Single(playTime.Resumed);
        var applied = Assert.Single(speed.Applied);
        Assert.Equal(1111, applied.ProcessId);
        Assert.Equal(1.5, applied.Speed);
        Assert.True(Assert.Single(store.Saved[^1].Sessions).LaunchedByHelper);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recovery_ExternalSession_RespectsAutomationSettingAndKeepsExternalFlag(bool enabled)
    {
        var store = new FakeInFlightStore
        {
            Snapshot = new InFlightSnapshot
            {
                SnapshotAt = SnapshotAt,
                Sessions =
                {
                    new InFlightSession
                    {
                        DataKey = "g1",
                        ProcessId = 1111,
                        NormalizedName = "game1.exe",
                        NormalizedPath = "D:\\Games\\game1.exe",
                        StartedAt = SessionStart,
                        LaunchedByHelper = false
                    }
                }
            }
        };
        var probe = new FakeProbe(new Dictionary<int, string?> { [1111] = "D:\\Games\\game1.exe" });
        var speed = new RecoverySpeedController();
        var playTime = new FakePlayTime();

        var config = CreateSpeedConfig();
        var game = config.Object.Read().Games.Single();
        game.IsEnabled = enabled;
        game.HdrEnabled = true;
        game.Launch = new() { Target = @"C:\MissingGame.exe", Ds4WindowsPath = @"C:\DS4Windows.exe" };
        var launcher = new Mock<IGameLauncher>();
        var hdr = new Mock<IHdrController>();
        var service = new GameAutomationService(new FakeMonitor(), config.Object, hdr.Object, playTime,
            NullLogger<GameAutomationService>.Instance, speedController: speed, inFlightSessionStore: store,
            processIdentityProbe: probe, gameLauncher: launcher.Object);
        service.Start();

        // 外部会话续算并补挂变速，快照保持外部标记，不取得 HDR 资格。
        Assert.Single(playTime.Resumed);
        if (enabled) Assert.Equal(1111, Assert.Single(speed.Applied).ProcessId);
        else Assert.Empty(speed.Applied);
        Assert.False(Assert.Single(store.Saved[^1].Sessions).LaunchedByHelper);
        launcher.Verify(value => value.EnsureCompanionRunning(game.Launch.Ds4WindowsPath), Times.Exactly(enabled ? 1 : 0));
        launcher.Verify(value => value.Start(It.IsAny<GameLaunchOptions>()), Times.Never);
        hdr.Verify(value => value.Enable(), Times.Never);
        service.Stop();
    }

    [Fact]
    public void Recovery_LegacySnapshot_StillReAppliesSpeedForContinuity()
    {
        var store = new FakeInFlightStore
        {
            Snapshot = new InFlightSnapshot
            {
                SnapshotAt = SnapshotAt,
                HdrEnabledByGameHelper = true,
                Sessions =
                {
                    new InFlightSession
                    {
                        DataKey = "g1",
                        ProcessId = 1111,
                        NormalizedName = "game1.exe",
                        NormalizedPath = "D:\\Games\\game1.exe",
                        StartedAt = SessionStart
                    }
                }
            }
        };
        var probe = new FakeProbe(new Dictionary<int, string?> { [1111] = "D:\\Games\\game1.exe" });
        var speed = new RecoverySpeedController();
        var playTime = new FakePlayTime();

        CreateSpeedService(new FakeMonitor(), playTime, store, probe, speed).Start();

        // 旧版快照未写标记（升级前落盘），续算按库内启动对待以保持行为连续。
        Assert.Single(playTime.Resumed);
        var applied = Assert.Single(speed.Applied);
        Assert.Equal(1111, applied.ProcessId);
        Assert.Equal(1.5, applied.Speed);
        Assert.True(Assert.Single(store.Saved[^1].Sessions).LaunchedByHelper);
    }

    [Fact]
    public void ProcessStop_FailedHistoryWrite_RetainsCompletedRecoveryRecord()
    {
        var monitor = new FakeMonitor();
        var playTime = new FakePlayTime { FailStop = true };
        var store = new FakeInFlightStore();
        var service = CreateService(monitor, CreateConfig(), playTime, store, new FakeProbe(new Dictionary<int, string?>()));
        service.Start();
        monitor.RaiseStarted(new ProcessEventInfo("game1.exe", null, 100));
        var originalStart = Assert.Single(store.Saved[^1].Sessions).StartedAt;
        monitor.RaiseStopped(new ProcessEventInfo("game1.exe", null, 100));
        var pending = Assert.Single(store.Saved[^1].Sessions);
        Assert.Equal(originalStart, pending.StartedAt);
        Assert.NotNull(pending.RecoveryEndAt);
        service.Stop();
        Assert.Single(store.Saved[^1].Sessions);
    }

    [Fact]
    public void Recovery_DeleteSnapshotFails_MonitoringStillStartsAndEmptyReplacementIsSaved()
    {
        var store = new FakeInFlightStore
        {
            FailClear = true,
            Snapshot = new InFlightSnapshot
            {
                SnapshotAt = SnapshotAt,
                Sessions = { new InFlightSession { DataKey = "g1", StartedAt = SessionStart } }
            }
        };
        var monitor = new FakeMonitor();
        var playTime = new FakePlayTime();
        var service = CreateService(monitor, CreateConfig(), playTime, store, new FakeProbe(new Dictionary<int, string?>()));
        service.Start();
        Assert.Single(playTime.Recovered);
        Assert.Empty(store.Saved[^1].Sessions);
        monitor.RaiseStarted(new ProcessEventInfo("game1.exe", null, 100));
        Assert.Single(playTime.Started);
        service.Stop();
    }

    [Fact]
    public void Recovery_WriteFails_RetainsSnapshotAndOriginalEndForNextRestart()
    {
        var store = new FakeInFlightStore
        {
            Snapshot = new InFlightSnapshot
            {
                SnapshotAt = SnapshotAt,
                Sessions = { new InFlightSession { DataKey = "g1", ProcessId = 1111, NormalizedName = "game1.exe", StartedAt = SessionStart } }
            }
        };
        var firstPlayTime = new FakePlayTime { FailRecovery = true };
        var first = CreateService(new FakeMonitor(), CreateConfig(), firstPlayTime, store, new FakeProbe(new Dictionary<int, string?>()));
        first.Start();
        Assert.Equal(0, store.ClearCalls);
        Assert.Equal(SnapshotAt, Assert.Single(store.Saved[^1].Sessions).RecoveryEndAt);
        store.Snapshot = store.Saved[^1];
        var recovered = new FakePlayTime();
        var second = CreateService(new FakeMonitor(), CreateConfig(), recovered, store, new FakeProbe(new Dictionary<int, string?>()));
        second.Start();
        Assert.Equal(SnapshotAt, Assert.Single(recovered.Recovered).End);
        first.Stop();
        second.Stop();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_PendingOldSessionsAndNewLiveSessionForSameGame_AreHandledSeparately(bool withLiveSession)
    {
        var oldStart = SessionStart.AddHours(-1);
        var store = new FakeInFlightStore
        {
            Snapshot = new InFlightSnapshot
            {
                SnapshotAt = SnapshotAt,
                Sessions =
                {
                    new InFlightSession { DataKey = "g1", StartedAt = oldStart, RecoveryEndAt = oldStart.AddMinutes(20) },
                    new InFlightSession { DataKey = "G1", StartedAt = oldStart.AddMinutes(30), RecoveryEndAt = oldStart.AddMinutes(40) }
                }
            }
        };
        if (withLiveSession) store.Snapshot.Sessions.Add(new InFlightSession { DataKey = "g1", ProcessId = 1111, NormalizedName = "game1.exe", StartedAt = SessionStart });
        var playTime = new FakePlayTime();
        var service = CreateService(new FakeMonitor(), CreateConfig(), playTime, store,
            new FakeProbe(new Dictionary<int, string?> { [1111] = @"D:\Games\game1.exe" }));
        service.Start();
        Assert.Equal(2, playTime.Recovered.Count);
        Assert.Equal(withLiveSession ? 1 : 0, playTime.Resumed.Count);
        if (withLiveSession) Assert.Equal(1111, Assert.Single(store.Saved[^1].Sessions).ProcessId);
        service.Stop();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_SameExecutableReusesPid_DoesNotResumeOldSession(bool legacySnapshot)
    {
        var store = new FakeInFlightStore
        {
            Snapshot = new InFlightSnapshot
            {
                SnapshotAt = SnapshotAt,
                Sessions = { new InFlightSession
                {
                    DataKey = "g1", ProcessId = 1111, NormalizedName = "game1.exe", StartedAt = SessionStart,
                    ProcessStartedAtUtc = legacySnapshot ? null : SessionStart.ToUniversalTime()
                } }
            }
        };
        var probe = new Mock<IProcessIdentityProbe>();
        string? image = @"D:\Games\game1.exe";
        probe.Setup(p => p.TryGetImagePath(1111, out image)).Returns(true);
        var created = SnapshotAt.AddSeconds(1).ToUniversalTime();
        probe.Setup(p => p.TryGetStartTimeUtc(1111, out created)).Returns(true);
        var playTime = new FakePlayTime();
        var service = CreateService(new FakeMonitor(), CreateConfig(), playTime, store, probe.Object);
        service.Start();
        Assert.Empty(playTime.Resumed);
        Assert.Single(playTime.Recovered);
        service.Stop();
    }

    [Fact]
    public void Recovery_DeadProcess_WritesInterruptedRowAtSnapshotTime()
    {
        var monitor = new FakeMonitor();
        var playTime = new FakePlayTime();
        var store = new FakeInFlightStore
        {
            Snapshot = new InFlightSnapshot
            {
                SnapshotAt = SnapshotAt,
                Sessions =
                {
                    new InFlightSession
                    {
                        DataKey = "g2",
                        ProcessId = 2222,
                        NormalizedName = "game2.exe",
                        NormalizedPath = "D:\\Games\\game2.exe",
                        StartedAt = SessionStart
                    }
                }
            }
        };

        CreateService(monitor, CreateConfig(), playTime, store, new FakeProbe(new Dictionary<int, string?>())).Start();

        var recovered = Assert.Single(playTime.Recovered);
        Assert.Equal("g2", recovered.Game);
        Assert.Equal(SessionStart, recovered.Start);
        Assert.Equal(SnapshotAt, recovered.End);
        Assert.Empty(playTime.Resumed);
        Assert.Equal(1, store.ClearCalls);
    }

    [Fact]
    public void Recovery_PidReusedByDifferentExecutable_TreatedAsDead()
    {
        var monitor = new FakeMonitor();
        var playTime = new FakePlayTime();
        var store = new FakeInFlightStore
        {
            Snapshot = new InFlightSnapshot
            {
                SnapshotAt = SnapshotAt,
                Sessions =
                {
                    new InFlightSession
                    {
                        DataKey = "g1",
                        ProcessId = 1111,
                        NormalizedName = "game1.exe",
                        NormalizedPath = "D:\\Games\\game1.exe",
                        StartedAt = SessionStart
                    }
                }
            }
        };
        // The pid came back alive but now points at a different executable.
        var probe = new FakeProbe(new Dictionary<int, string?> { [1111] = "D:\\Other\\unrelated.exe" });

        CreateService(monitor, CreateConfig(), playTime, store, probe).Start();

        Assert.Single(playTime.Recovered);
        Assert.Empty(playTime.Resumed);
    }

    [Fact]
    public void Recovery_SessionWithoutPid_ClosedInsteadOfAdopted()
    {
        var monitor = new FakeMonitor();
        var playTime = new FakePlayTime();
        var store = new FakeInFlightStore
        {
            Snapshot = new InFlightSnapshot
            {
                SnapshotAt = SnapshotAt,
                Sessions =
                {
                    new InFlightSession
                    {
                        DataKey = "g2",
                        ProcessId = null,
                        NormalizedName = "game2.exe",
                        StartedAt = SessionStart
                    }
                }
            }
        };

        CreateService(monitor, CreateConfig(), playTime, store, new FakeProbe(new Dictionary<int, string?>())).Start();

        Assert.Single(playTime.Recovered);
        Assert.Empty(playTime.Resumed);
    }

    [Fact]
    public void ProcessLifecycle_UpdatesInFlightSnapshot()
    {
        var monitor = new FakeMonitor();
        var playTime = new FakePlayTime();
        var store = new FakeInFlightStore { Snapshot = null };

        var automation = CreateService(monitor, CreateConfig(), playTime, store, new FakeProbe(new Dictionary<int, string?>()));
        automation.Start();
        monitor.RaiseStarted(new ProcessEventInfo("game1.exe", null, 9999));

        Assert.NotEmpty(store.Saved);
        var afterStart = store.Saved[^1];
        var startedSession = Assert.Single(afterStart.Sessions);
        Assert.Equal("g1", startedSession.DataKey);
        Assert.Equal(9999, startedSession.ProcessId);
        Assert.Equal("game1.exe", startedSession.NormalizedName);

        monitor.RaiseStopped(new ProcessEventInfo("game1.exe", null, 9999));

        Assert.Empty(store.Saved[^1].Sessions);
        Assert.Contains("g1", playTime.Stopped);
        Assert.Contains("g1", playTime.Started);

        automation.Stop();
        Assert.True(store.ClearCalls >= 1);
    }

    [Fact]
    public void NameOnlyEtwHint_DoesNotPersistInventedWorkingDirectoryPath()
    {
        var monitor = new FakeMonitor();
        var store = new FakeInFlightStore();
        var automation = CreateService(monitor, CreateConfig(), new FakePlayTime(), store, new FakeProbe(new Dictionary<int, string?>()));
        automation.Start();
        monitor.RaiseStarted(new ProcessEventInfo("game1.exe", "game1.exe", 100));
        Assert.Null(Assert.Single(store.Saved[^1].Sessions).NormalizedPath);
        automation.Stop();
    }

    [Fact]
    public void MultipleProcesses_EveryChangePersistsRemainingIdentitiesWithOriginalStart()
    {
        var monitor = new FakeMonitor();
        var playTime = new FakePlayTime();
        var store = new FakeInFlightStore();
        var automation = CreateService(monitor, CreateConfig(), playTime, store, new FakeProbe(new Dictionary<int, string?>()));
        automation.Start();
        monitor.RaiseStarted(new ProcessEventInfo("game1.exe", null, 100));
        var originalStart = Assert.Single(store.Saved[^1].Sessions).StartedAt;
        monitor.RaiseStarted(new ProcessEventInfo("game1.exe", null, 200));
        Assert.Equal(new int?[] { 100, 200 }, store.Saved[^1].Sessions.Select(s => s.ProcessId));
        monitor.RaiseStopped(new ProcessEventInfo("game1.exe", null, 100));
        var remaining = Assert.Single(store.Saved[^1].Sessions);
        Assert.Equal(200, remaining.ProcessId);
        Assert.Equal(originalStart, remaining.StartedAt);
        Assert.Empty(playTime.Stopped);
        automation.Stop();
    }

    [Fact]
    public void Start_WithoutStore_DoesNothingSpecial()
    {
        var monitor = new FakeMonitor();
        var playTime = new FakePlayTime();

        var automation = new GameAutomationService(
            monitor,
            CreateConfig().Object,
            Mock.Of<IHdrController>(),
            playTime,
            NullLogger<GameAutomationService>.Instance);
        automation.Start();
        monitor.RaiseStarted(new ProcessEventInfo("game1.exe", null, 42));
        monitor.RaiseStopped(new ProcessEventInfo("game1.exe", null, 42));

        Assert.Single(playTime.Started);
        Assert.Single(playTime.Stopped);
        Assert.Empty(playTime.Resumed);
        Assert.Empty(playTime.Recovered);
    }

    [Fact]
    public void SystemSuspendResume_PersistsShiftedSessionStartInSnapshot()
    {
        var monitor = new FakeMonitor();
        var store = new FakeInFlightStore();
        var playTime = new FakePlayTime();
        var power = new FakePowerEvents();
        var clock = new FakePowerClock();
        var automation = new GameAutomationService(
            monitor,
            CreateConfig().Object,
            Mock.Of<IHdrController>(),
            playTime,
            NullLogger<GameAutomationService>.Instance,
            inFlightSessionStore: store,
            timeProvider: clock,
            powerEvents: power);
        automation.Start();
        monitor.RaiseStarted(new ProcessEventInfo("game1.exe", null, 42));

        var startedAt = Assert.Single(store.Saved[^1].Sessions).StartedAt;
        power.RaiseSuspend();
        clock.Advance(TimeSpan.FromMinutes(45));
        power.RaiseResume();

        Assert.Equal(TimeSpan.FromMinutes(45), Assert.Single(playTime.ShiftedStarts));
        var persisted = Assert.Single(store.Saved[^1].Sessions);
        Assert.Equal(TimeSpan.FromMinutes(45), persisted.StartedAt - startedAt);
        automation.Stop();
    }

    private sealed class FakePowerEvents : ISystemPowerEvents
    {
        public event EventHandler? Suspending;
        public event EventHandler? Resumed;
        public void RaiseSuspend() => Suspending?.Invoke(this, EventArgs.Empty);
        public void RaiseResume() => Resumed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakePowerClock : TimeProvider
    {
        public FakePowerClock() { }
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch.AddYears(55);
        public void Advance(TimeSpan duration) => UtcNow += duration;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
