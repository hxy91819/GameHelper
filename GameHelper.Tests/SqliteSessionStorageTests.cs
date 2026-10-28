using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Storage.Sqlite;
using GameHelper.Infrastructure.Storage.Sqlite.Legacy;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit.Abstractions;

namespace GameHelper.Tests;

public sealed class SqliteSessionStorageTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameHelper.Sessions." + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private string Config => Path.Combine(_directory, "config.yml");
    private SqliteConnectionFactory Factory => new(_directory);
    private readonly List<SessionStorage> _storages = [];
    private SessionStorage Storage()
    {
        var storage = new SessionStorage(Factory, _clock, NullLoggerFactory.Instance);
        _storages.Add(storage);
        return storage;
    }
    private static readonly GameConfig Game = new() { DataKey = "游戏", Executable = "game.exe", HdrEnabled = true };

    [Theory]
    [InlineData("completion.commit.before")]
    [InlineData("completion.commit.after")]
    public async Task Completion_InterruptedTransaction_RestartRetainsIdentityAndCommitsOnce(string fault)
    {
        var initial = Storage();
        await initial.InitializeAsync(Config, [Game]);
        var process = Session();
        initial.InFlight.Load();
        initial.InFlight.Save(new() { SnapshotAt = _clock.Now, Sessions = [process] });
        var store = new SqliteSessionStore(Factory, _clock, () => { }, point => { if (point == fault) throw new IOException(point); });
        store.Load();
        Assert.Throws<IOException>(() => store.Complete(process, _clock.Now.AddMinutes(20), "observed"));
        Assert.Equal(fault.EndsWith("before") ? 0L : 1L, Count("play_sessions"));
        Assert.Equal(fault.EndsWith("before") ? 1L : 0L, Count("inflight_sessions"));

        var restarted = Storage();
        await restarted.InitializeAsync(Config, [Game]);
        var durable = restarted.InFlight.Load();
        if (durable!.Sessions.Count != 0)
        {
            Assert.Equal(process.SessionId, durable.Sessions[0].SessionId);
            ((IIdentifiedPlayTimeService)restarted.PlayTime).WriteRecoveredSession(durable.Sessions[0], _clock.Now.AddMinutes(20));
        }
        // Lost completion acknowledgements and repeated stops cannot produce another occurrence.
        ((IIdentifiedPlayTimeService)restarted.PlayTime).WriteRecoveredSession(process, _clock.Now.AddMinutes(20));
        Assert.Null(restarted.PlayTime.StopTracking(Game.DataKey));
        Assert.Equal(1L, Count("play_sessions"));
        Assert.Equal(0L, Count("inflight_sessions"));
        Assert.Equal(1L, Scalar("SELECT history_revision FROM storage_meta;"));
        restarted.FlushProjection();
        Assert.Single(Assert.Single(new FilePlaytimeSnapshotProvider(_directory).GetSnapshot().Records).Sessions);
    }

    [Theory]
    [InlineData("snapshot.commit.before")]
    [InlineData("snapshot.commit.after")]
    public async Task Snapshot_FaultBoundary_IsWholeAndRecoveryCanContinue(string fault)
    {
        var initial = Storage(); await initial.InitializeAsync(Config, [Game]);
        var process = Session();
        initial.InFlight.Load();
        initial.InFlight.Save(new() { SnapshotAt = _clock.Now, Sessions = [process] });
        var store = new SqliteSessionStore(Factory, _clock, () => { }, point => { if (point == fault) throw new IOException(point); });
        store.Load();
        Assert.Throws<IOException>(() => store.Save(new() { SnapshotAt = _clock.Now.AddMinutes(1), Sessions = [process], HdrEnabledByGameHelper = true }));
        var reopened = new SqliteSessionStore(Factory, _clock, () => { });
        var snapshot = reopened.Load()!;
        Assert.Equal(fault.EndsWith("after"), snapshot.HdrEnabledByGameHelper);
        Assert.Equal(process.SessionId, Assert.Single(snapshot.Sessions).SessionId);
        reopened.Complete(process, _clock.Now.AddMinutes(20), "recovered");
        Assert.Equal(1L, Count("play_sessions"));
    }

    [Fact]
    public async Task Automation_MultipleProcessesTwoRecoveriesSuspendAndDuplicateStops_OneContinuousSession()
    {
        var first = Storage(); await first.InitializeAsync(Config, [Game]);
        var monitor = new Monitor(); var probe = new Probe(_clock.Now);
        var power = new Power();
        var service = Automation(first, monitor, probe, power);
        service.Start();
        monitor.Started(10); monitor.Started(11);
        var before = first.InFlight.Load()!;
        Assert.Equal(2, before.Sessions.Count);
        var id = before.Sessions[0].SessionId;
        Assert.All(before.Sessions, row => Assert.Equal(id, row.SessionId));
        _clock.Advance(10);
        power.Suspend(); _clock.Advance(60); power.Resume();
        Assert.All(first.InFlight.Load()!.Sessions, row => Assert.Equal(before.Sessions[0].StartedAt.AddHours(1), row.StartedAt));
        // Simulate two killed hosts: no Stop/Dispose flushes are invoked on the abandoned generations.
        for (var restart = 0; restart < 2; restart++)
        {
            var storage = Storage(); await storage.InitializeAsync(Config, [Game]);
            monitor = new Monitor();
            service = Automation(storage, monitor, probe);
            service.Start();
            Assert.All(storage.InFlight.Load()!.Sessions, row => Assert.Equal(id, row.SessionId));
        }
        _clock.Advance(15);
        monitor.Stopped(10); Assert.Equal(0L, Count("play_sessions"));
        monitor.Stopped(11); monitor.Stopped(11); service.Stop(); service.Stop();
        var session = Assert.Single(Assert.Single(first.Snapshot.GetSnapshot().Records).Sessions);
        Assert.Equal(25, session.DurationMinutes);
        Assert.Equal(1L, Count("play_sessions"));
        Assert.Equal(id.ToString("D"), Scalar("SELECT session_id FROM play_sessions;"));
    }

    [Fact]
    public async Task Recovery_ReusedPid_ClosesAtLastAliveRatherThanAdoptingNewProcess()
    {
        var storage = Storage(); await storage.InitializeAsync(Config, [Game]);
        var process = Session(); process.ProcessStartedAtUtc = _clock.Now.AddMinutes(-2);
        storage.InFlight.Load(); storage.InFlight.Save(new() { SnapshotAt = _clock.Now, Sessions = [process] });
        _clock.Advance(120);
        var service = Automation(storage, new Monitor(), new Probe(_clock.Now));
        service.Start();
        Assert.Empty(service.GetRunningSessionStarts());
        var completed = Assert.Single(Assert.Single(storage.Snapshot.GetSnapshot().Records).Sessions);
        Assert.Equal(process.StartedAt, completed.StartTime);
        Assert.Equal(20, completed.DurationMinutes);
        Assert.Equal(process.StartedAt.AddMinutes(20), completed.EndTime);
        service.Stop();
    }

    [Fact]
    public async Task Completion_WriteFailureAndHeartbeatRetry_KeepsOriginalEndAcrossClockAdvance()
    {
        var storage = Storage(); var fail = true;
        await storage.InitializeAsync(Config, [Game], checkpoint: point =>
        {
            if (point == "completion.commit.before" && fail) { _clock.Advance(30); throw new IOException("disk unavailable"); }
        });
        var monitor = new Monitor(); var service = Automation(storage, monitor, new Probe(_clock.Now));
        service.Start(); monitor.Started(10); _clock.Advance(15);
        var end = _clock.Now;
        monitor.Stopped(10);
        Assert.Equal(end, Assert.Single(storage.InFlight.Load()!.Sessions).RecoveryEndAt);
        Assert.Equal(0L, Count("play_sessions"));
        fail = false; _clock.Advance(60); _clock.Tick(); _clock.Tick();
        var completed = Assert.Single(Assert.Single(storage.Snapshot.GetSnapshot().Records).Sessions);
        Assert.Equal(end, completed.EndTime); Assert.Equal(15, completed.DurationMinutes);
        Assert.Empty(storage.InFlight.Load()!.Sessions);
        service.Stop();
    }

    [Fact]
    public async Task Recovery_HdrAndPendingDeadline_ContinueWithoutReplayingLaunches()
    {
        var storage = Storage(); await storage.InitializeAsync(Config, [Game]);
        var process = Session(); process.LaunchedByHelper = null;
        var deadline = _clock.GetUtcNow().AddMinutes(2);
        storage.InFlight.Load(); storage.InFlight.Save(new()
        {
            SnapshotAt = _clock.Now, Sessions = [process], HdrEnabledByGameHelper = true,
            PendingLaunches = new(StringComparer.OrdinalIgnoreCase) { ["pending"] = deadline }
        });
        var launcher = new Mock<IGameLauncher>(MockBehavior.Strict);
        launcher.Setup(l => l.StopCompanion());
        var hdr = new Mock<IHdrController>(); hdr.SetupGet(h => h.IsEnabled).Returns(true);
        var service = Automation(storage, new Monitor(), new Probe(_clock.Now.AddMinutes(-1)), hdr: hdr.Object, launcher: launcher.Object);
        service.Start();
        var snapshot = storage.InFlight.Load()!;
        Assert.True(snapshot.HdrEnabledByGameHelper);
        Assert.Null(Assert.Single(snapshot.Sessions).LaunchedByHelper); // unknown provenance is not invented as library
        Assert.Equal(deadline, snapshot.PendingLaunches["pending"]);
        launcher.Verify(l => l.StopCompanion(), Times.Once);
        launcher.VerifyNoOtherCalls();
        service.Stop();
    }

    [Fact]
    public async Task Snapshot_StaleWriter_IsRejectedBeforeReplacingNewGeneration()
    {
        var storage = Storage(); await storage.InitializeAsync(Config, [Game]);
        var stale = new SqliteSessionStore(Factory, _clock, () => { }); stale.Load();
        storage.InFlight.Load(); storage.InFlight.Save(new() { SnapshotAt = _clock.Now, Sessions = [Session()] });
        Assert.Throws<InvalidOperationException>(() => stale.Save(new() { SnapshotAt = _clock.Now }));
        Assert.Single(storage.InFlight.Load()!.Sessions);
    }

    [Theory]
    [InlineData("playtime.csv", "wrong,header\n1,2\n")]
    [InlineData("inflight-sessions.json", "{broken")]
    [InlineData("config.yml.desktop-artwork.json", "{\"游戏\":0}")]
    [InlineData("config.yml.desktop-genres.json", "{\"游戏\":[\"RPG\",\"RPG\"]}")]
    public async Task Activation_InvalidOrUnrepresentableSource_FallsBackWithoutTouchingOriginals(string file, string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, file), content, new UTF8Encoding(false));
        var before = Directory.GetFiles(_directory).ToDictionary(p => p, File.ReadAllBytes);
        var storage = Storage(); await storage.InitializeAsync(Config, [Game]);
        Assert.False(storage.UsesSqlite); Assert.NotNull(storage.Warning);
        Assert.IsType<CsvBackedPlayTimeService>(storage.PlayTime);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(Factory.DatabasePath));
    }

    [Fact]
    public async Task Activation_ValidationFault_LeavesSourcesAndRestartUsesCommittedDatabase()
    {
        Directory.CreateDirectory(_directory);
        var bytes = Encoding.UTF8.GetBytes("game,start_time,end_time,duration_minutes\n游戏,2025-01-01T00:00:00,2025-01-01T00:10:00,10\n");
        File.WriteAllBytes(Path.Combine(_directory, "playtime.csv"), bytes);
        var blocked = Storage();
        await blocked.InitializeAsync(Config, [Game], checkpoint: point => { if (point == "candidate.validated.commit.before") throw new InvalidDataException("validation fault"); });
        Assert.False(blocked.UsesSqlite); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_directory, "playtime.csv")));
        var storage = Storage(); await storage.InitializeAsync(Config, [Game]); Assert.True(storage.UsesSqlite);
        var id = Scalar("SELECT session_id FROM play_sessions;");
        File.Delete(Path.Combine(_directory, "migration-complete.json"));
        var restart = Storage(); await restart.InitializeAsync(Config, [Game]);
        Assert.Equal(id, Scalar("SELECT session_id FROM play_sessions;"));
        Assert.Equal(1L, Count("play_sessions"));
        Assert.True(File.Exists(Path.Combine(_directory, "migration-complete.json")));
    }

    [Fact]
    public async Task Compatibility_ProjectionFailure_DoesNotUndoCompletionOrBlockDatabaseSync()
    {
        var fail = false;
        var storage = Storage(); await storage.InitializeAsync(Config, [Game], checkpoint: point =>
        {
            if (fail && point == "projection.replace.before:playtime.csv") throw new IOException("projection locked");
        });
        storage.InFlight.Load(); storage.PlayTime.StartTracking(Game.DataKey); _clock.Advance(20); fail = true;
        Assert.NotNull(storage.PlayTime.StopTracking(Game.DataKey));
        Assert.Equal(1L, Count("play_sessions"));
        Assert.Throws<IOException>(storage.FlushProjection);
        Assert.Contains("旧版兼容投影失败", storage.Warning);
        Assert.False(new CompatibilityPlaytimeSnapshotProvider(storage).GetSnapshot().ReadFailed);
        var config = new Mock<IGameConfiguration>();
        config.Setup(c => c.Read()).Returns(new AppConfig
        {
            Games = [Game], SyncSettings = new() { Enabled = true, Repo = "owner/repo", Method = "api", Token = "test" }
        });
        var channel = new Mock<IStatsUploadChannel>();
        channel.Setup(c => c.UploadAsync(It.IsAny<SyncSettings>(), It.IsAny<IReadOnlyList<StatsUploadFile>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new StatsUploadResult("uploaded", NoChanges: false));
        var channels = new Mock<IStatsUploadChannelProvider>(MockBehavior.Strict);
        channels.Setup(c => c.GetChannel(It.IsAny<SyncSettings>())).Returns(channel.Object);
        var sync = new SyncService(config.Object, new CompatibilityPlaytimeSnapshotProvider(storage),
            channels.Object, new StatsReportBuilder(), _clock, NullLogger<SyncService>.Instance,
            Path.Combine(_directory, "playtime.csv"), Path.Combine(_directory, "sync-state.json"));
        Assert.Equal(SyncOutcomeStatus.Uploaded, (await sync.SyncNowAsync(force: true)).Status);
        Assert.Null(sync.GetStatus().LastError);
        fail = false; storage.FlushProjection(); Assert.Null(storage.Warning);
        Assert.Single(Assert.Single(new FilePlaytimeSnapshotProvider(_directory).GetSnapshot().Records).Sessions);
        var csv = Path.Combine(_directory, "playtime.csv"); var mtime = File.GetLastWriteTimeUtc(csv);
        storage.InFlight.Save(new() { SnapshotAt = _clock.Now });
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(csv)); // heartbeat cannot wake sync
    }

    [Fact]
    public async Task Activation_DesktopScopeImportsOnce_RuntimeProjectsDatabaseValues()
    {
        Directory.CreateDirectory(_directory);
        var side = Config + ".desktop-theme.json";
        File.WriteAllText(side, "{\"Theme\":\"light\"}");
        var storage = Storage(); await storage.InitializeAsync(Config, [Game]);
        Assert.True(storage.UsesSqlite); Assert.Equal(1L, Count("config_scopes"));
        File.WriteAllText(side, "{\"Theme\":\"dark\"}");
        storage.InFlight.Load(); storage.InFlight.Save(new() { SnapshotAt = _clock.Now });
        storage.FlushProjection();
        Assert.Equal("{\"Theme\":\"light\"}", File.ReadAllText(side));
    }

    [Theory]
    [InlineData("activation.rename.before", false)]
    [InlineData("activation.rename.after", true)]
    [InlineData("activation.diagnostic.before", true)]
    public async Task Activation_PublicationFault_UsesOnlyTheDurableAuthority(string fault, bool activated)
    {
        var storage = Storage();
        await storage.InitializeAsync(Config, [Game], checkpoint: point => { if (point == fault) throw new IOException(point); });
        Assert.Equal(activated, storage.UsesSqlite);
        Assert.Equal(activated, File.Exists(Factory.DatabasePath));
        var restarted = Storage(); await restarted.InitializeAsync(Config, [Game]);
        Assert.True(restarted.UsesSqlite);
    }

    [Fact]
    public async Task Activation_LegacyWriterStillOpen_DoesNotCaptureOrChangeSources()
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "playtime.json");
        var bytes = Encoding.UTF8.GetBytes("{\"games\":[]}"); File.WriteAllBytes(file, bytes);
        var storage = Storage();
        await storage.InitializeAsync(Config, [Game], () => throw new InvalidOperationException("old UI still open"));
        Assert.False(storage.UsesSqlite); Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.False(File.Exists(Path.Combine(_directory, "playtime.csv")));
        Assert.False(Directory.Exists(Path.Combine(_directory, "migration-backups")));
    }

    [Fact]
    public async Task Activated_UnmarkedOrFutureDatabase_RefusesLegacyFallback()
    {
        await new SqliteDatabaseInitializer(Factory, _clock).InitializeAsync();
        var storage = Storage();
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.InitializeAsync(Config, [Game]));
        Assert.Throws<InvalidOperationException>(() => storage.PlayTime);
        using (var c = Factory.OpenConnection())
        using (var command = c.CreateCommand()) { command.CommandText = "PRAGMA user_version=999;"; command.ExecuteNonQuery(); }
        await Assert.ThrowsAsync<NotSupportedException>(() => Storage().InitializeAsync(Config, [Game]));
    }

    [Fact]
    public async Task Recovery_OverlongDowntime_CapsAtTwelveHoursWithoutChangingId()
    {
        var storage = Storage(); await storage.InitializeAsync(Config, [Game]);
        var row = Session(); row.StartedAt = _clock.Now.AddDays(-2); row.ProcessId = null;
        storage.InFlight.Load(); storage.InFlight.Save(new() { SnapshotAt = _clock.Now, Sessions = [row] });
        var automation = Automation(storage, new Monitor(), new Probe(_clock.Now));
        automation.Start(); automation.Stop();
        var completed = Assert.Single(Assert.Single(storage.Snapshot.GetSnapshot().Records).Sessions);
        Assert.Equal(720, completed.DurationMinutes);
        Assert.Equal(row.SessionId.ToString("D"), Scalar("SELECT session_id FROM play_sessions;"));
    }

    [Fact]
    public async Task Recovery_IdenticalTimesWithDistinctIds_PreservesBothLegitimateSessions()
    {
        var storage = Storage(); await storage.InitializeAsync(Config, [Game]);
        var first = Session(); first.ProcessId = null; first.RecoveryEndAt = _clock.Now;
        var second = Session(); second.ProcessId = null; second.RecoveryEndAt = _clock.Now;
        storage.InFlight.Load(); storage.InFlight.Save(new() { SnapshotAt = _clock.Now, Sessions = [first, second] });
        Assert.Equal(2L, Count("inflight_sessions"));
        var automation = Automation(storage, new Monitor(), new Probe(_clock.Now));
        automation.Start(); automation.Stop();
        Assert.Equal(2L, Count("play_sessions")); Assert.Equal(2L, Scalar("SELECT history_revision FROM storage_meta;"));
    }

    [Fact]
    public async Task Compatibility_UtcHistory_PreservesOldReadSemanticsAndSyncBytes()
    {
        Directory.CreateDirectory(_directory);
        var csv = Path.Combine(_directory, "playtime.csv");
        File.WriteAllText(csv, "game,start_time,end_time,duration_minutes\n游戏,2025-01-01T23:50:00Z,2025-01-02T00:10:00Z,20\n");
        var old = new FilePlaytimeSnapshotProvider(_directory).GetSnapshot().Records;
        var before = new StatsReportBuilder().Build(old, [Game], _clock.Now, true);
        var storage = Storage(); await storage.InitializeAsync(Config, [Game]);
        var projected = new CompatibilityPlaytimeSnapshotProvider(storage).GetSnapshot().Records;
        Assert.Equal(DateTimeKind.Utc, Assert.Single(Assert.Single(projected).Sessions).EndTime.Kind);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(new StatsReportBuilder().Build(projected, [Game], _clock.Now, true)));
        // The production maintenance entry uses the same date-preserving codec for old-version handoff.
        var compatibility = new SqliteLegacyCompatibility(Factory, _clock, preserveHistoryKinds: true);
        var manifest = await compatibility.PrepareDowngradeAsync();
        await new SqliteLegacyCompatibility(Factory, _clock).ReupgradeAsync(manifest);
        Assert.Equal(1L, Count("play_sessions"));
    }

    [Fact]
    public async Task Compatibility_DowngradeOldJsonAdoption_PreservesOneContinuousRow()
    {
        var storage = Storage(); await storage.InitializeAsync(Config, [Game]);
        storage.InFlight.Load(); var process = Session();
        storage.InFlight.Save(new() { SnapshotAt = _clock.Now, Sessions = [process] });
        storage.FlushProjection();
        var manifest = await new SqliteLegacyCompatibility(Factory, _clock).PrepareDowngradeAsync();
        Assert.True(File.Exists(manifest));
        var old = new JsonInFlightSessionStore(Path.Combine(_directory, "inflight-sessions.json")).Load()!;
        var oldProcess = Assert.Single(old.Sessions);
        Assert.Equal(process.StartedAt, oldProcess.StartedAt); Assert.Equal(process.ProcessId, oldProcess.ProcessId);
        var oldWriter = new CsvBackedPlayTimeService(_directory, null, _clock);
        oldWriter.ResumeTracking(process.DataKey, oldProcess.StartedAt); _clock.Advance(10); oldWriter.StopTracking(process.DataKey);
        new JsonInFlightSessionStore(Path.Combine(_directory, "inflight-sessions.json")).Clear();
        await new SqliteLegacyCompatibility(Factory, _clock).ReupgradeAsync(manifest);
        Assert.Equal(1L, Count("play_sessions"));
    }

    [Fact]
    public async Task Recovery_KnownFifteenHourEndAfterFailedCompletion_RestartKeepsNineHundredMinutes()
    {
        var storage = Storage();
        await storage.InitializeAsync(Config, [Game], checkpoint: point =>
        {
            if (point == "completion.commit.before") throw new IOException("completion unavailable");
        });
        var monitor = new Monitor();
        var automation = Automation(storage, monitor, new Probe(_clock.Now));
        automation.Start(); monitor.Started(10); _clock.Advance(900);
        var endedAt = _clock.Now;
        monitor.Stopped(10);
        Assert.Equal(endedAt, Assert.Single(storage.InFlight.Load()!.Sessions).RecoveryEndAt);
        var restarted = Storage(); await restarted.InitializeAsync(Config, [Game]);
        var resumed = Automation(restarted, new Monitor(), new Probe(_clock.Now));
        resumed.Start();
        var completed = Assert.Single(Assert.Single(restarted.Snapshot.GetSnapshot().Records).Sessions);
        Assert.Equal(900, completed.DurationMinutes);
        Assert.Equal(endedAt, completed.EndTime);
        resumed.Stop();
    }

    [Fact]
    public async Task StopEvent_ContendedWriter_SharesFiveSecondBudgetAndKeepsObservedEnd()
    {
        var storage = Storage();
        var advanceDuringWrite = false;
        await storage.InitializeAsync(Config, [Game], checkpoint: point =>
        {
            if (advanceDuringWrite && point == "write.acquire.before")
            {
                advanceDuringWrite = false;
                _clock.Advance(30); // deterministically moves the gameplay clock after event-time capture
            }
        });
        var monitor = new Monitor(); var automation = Automation(storage, monitor, new Probe(_clock.Now));
        automation.Start(); monitor.Started(10); storage.FlushProjection();
        _clock.Advance(15); var observedEnd = _clock.Now;
        using (var blocker = Factory.OpenConnection())
        using (var transaction = blocker.BeginTransaction(deferred: false))
        {
            advanceDuringWrite = true;
            var elapsed = await Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                monitor.Stopped(10);
                return watch.Elapsed;
            }).WaitAsync(TimeSpan.FromSeconds(8));
            output.WriteLine($"contended stop event: {elapsed.TotalMilliseconds:F1} ms; budget: {SqliteConnectionFactory.TimeoutSeconds * 1000} ms");
            Assert.InRange(elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(SqliteConnectionFactory.TimeoutSeconds));
            Assert.Equal(0L, Count("play_sessions"));
            transaction.Rollback();
        }
        _clock.Tick();
        var completed = Assert.Single(Assert.Single(storage.Snapshot.GetSnapshot().Records).Sessions);
        Assert.Equal(observedEnd, completed.EndTime);
        Assert.Equal(15, completed.DurationMinutes);
        automation.Stop();
    }

    [Fact]
    public async Task Projection_BlockedAtFileReplacement_DoesNotHoldAutomationStateLock()
    {
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var block = false;
        var storage = Storage();
        await storage.InitializeAsync(Config, [Game], checkpoint: point =>
        {
            if (Volatile.Read(ref block) && point == "projection.replace.before:playtime.csv")
            {
                reached.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("projection test barrier");
            }
        });
        var monitor = new Monitor(); var automation = Automation(storage, monitor, new Probe(_clock.Now));
        automation.Start(); storage.FlushProjection();
        Volatile.Write(ref block, true);
        var started = Task.Run(() => monitor.Started(10));
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10)), "projection worker reached the file boundary");
            await started.WaitAsync(TimeSpan.FromSeconds(2));
            var states = await Task.Run(automation.GetGameStates).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Single(states, pair => pair.Key == Game.DataKey && pair.Value.StartsWith("运行中", StringComparison.Ordinal));
        }
        finally { Volatile.Write(ref block, false); release.Set(); await started; }
        automation.Stop();
    }

    [Fact]
    public async Task Activation_UnwritableDiagnosticReceipt_DoesNotPreventMonitoring()
    {
        var storage = Storage(); await storage.InitializeAsync(Config, [Game]);
        var receipt = Path.Combine(_directory, "migration-complete.json");
        File.Delete(receipt);
        Directory.CreateDirectory(receipt); // File.Move reports access denied on Windows; the DB remains writable.
        var restarted = Storage();
        await restarted.InitializeAsync(Config, [Game], checkpoint: point =>
        {
            if (point == "activation.diagnostic.repair.before") throw new UnauthorizedAccessException("receipt is not writable");
        });
        Assert.True(restarted.UsesSqlite);
        var monitor = new Monitor(); var automation = Automation(restarted, monitor, new Probe(_clock.Now));
        automation.Start(); monitor.Started(10); _clock.Advance(15); monitor.Stopped(10);
        Assert.Equal(15, Assert.Single(Assert.Single(restarted.Snapshot.GetSnapshot().Records).Sessions).DurationMinutes);
        automation.Stop();
    }

    private InFlightSession Session() => new()
    {
        SessionId = Guid.NewGuid(), DataKey = Game.DataKey, ProcessId = 10, NormalizedName = "game.exe",
        StartedAt = _clock.Now.AddMinutes(-20), ProcessStartedAtUtc = _clock.Now.AddMinutes(-1), LaunchedByHelper = false
    };
    private GameAutomationService Automation(SessionStorage storage, Monitor monitor, Probe probe, Power? power = null,
        IHdrController? hdr = null, IGameLauncher? launcher = null)
    {
        var config = new Mock<IGameConfiguration>(); config.Setup(c => c.Read()).Returns(new AppConfig { Games = [Game, new GameConfig { DataKey = "pending", Executable = "pending.exe", HdrEnabled = true }] });
        return new(monitor, config.Object, hdr ?? Mock.Of<IHdrController>(), storage.PlayTime,
            NullLogger<GameAutomationService>.Instance, inFlightSessionStore: storage.InFlight,
            processIdentityProbe: probe, timeProvider: _clock, powerEvents: power, gameLauncher: launcher);
    }
    private object? Scalar(string sql)
    {
        using var c = Factory.OpenConnection(); using var command = c.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }
    private long Count(string table) => Convert.ToInt64(Scalar("SELECT count(*) FROM " + table + ";"));
    public void Dispose()
    {
        foreach (var storage in _storages) storage.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class Probe(DateTime created) : IProcessIdentityProbe
    {
        public bool TryGetImagePath(int id, out string? path) { path = "D:/Games/game.exe"; return true; }
        public bool TryGetStartTimeUtc(int id, out DateTime start) { start = created; return true; }
    }
    private sealed class Monitor : IProcessMonitor
    {
        public event Action<ProcessEventInfo>? ProcessStarted;
        public event Action<ProcessEventInfo>? ProcessStopped;
        public void Configure(ProcessObservationPolicy policy) { }
        public void AssumeRunning(ProcessEventInfo info, DateTime at) { }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Started(int id) => ProcessStarted?.Invoke(new("game.exe", null, id));
        public void Stopped(int id) => ProcessStopped?.Invoke(new("game.exe", null, id));
    }
    private sealed class Power : ISystemPowerEvents
    {
        public event EventHandler? Suspending;
        public event EventHandler? Resumed;
        public void Suspend() => Suspending?.Invoke(this, EventArgs.Empty);
        public void Resume() => Resumed?.Invoke(this, EventArgs.Empty);
    }
    private sealed class Clock : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> _timers = [];
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddYears(55);
        public DateTime Now => GetLocalNow().DateTime;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(int minutes) => _now = _now.AddMinutes(minutes);
        public void Tick() { foreach (var timer in _timers.ToArray()) timer.Callback(timer.State); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
        { _timers.Add((callback, state)); return new Timer(); }
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan due, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
