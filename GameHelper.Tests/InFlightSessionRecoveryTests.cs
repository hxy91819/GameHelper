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

        public void Configure(ProcessObservationPolicy policy) => Policies.Add(policy);

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

        public InFlightSnapshot? Load() => Snapshot;

        public void Save(InFlightSnapshot snapshot) => Saved.Add(snapshot);

        public void Clear() => ClearCalls++;
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

    private sealed class FakePlayTime : IPlayTimeService
    {
        public List<string> Started { get; } = new();

        public List<(string Game, DateTime Start)> Resumed { get; } = new();

        public List<(string Game, DateTime Start, DateTime End)> Recovered { get; } = new();

        public List<string> Stopped { get; } = new();

        public void StartTracking(string gameName) => Started.Add(gameName);

        public void ResumeTracking(string gameName, DateTime startTime) => Resumed.Add((gameName, startTime));

        public void WriteRecoveredSession(string gameName, DateTime startTime, DateTime endTime) =>
            Recovered.Add((gameName, startTime, endTime));

        public PlaySession? StopTracking(string gameName)
        {
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
        Assert.Equal(1, store.ClearCalls);

        // The adopted session participates in normal stop matching afterwards.
        monitor.RaiseStopped(new ProcessEventInfo("game1.exe", null, 1111));
        Assert.Contains("g1", playTime.Stopped);
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
}