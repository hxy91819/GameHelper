using System.Text;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Desktop;
using GameHelper.Infrastructure.Ipc;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Storage.Sqlite;
using GameHelper.Infrastructure.Storage.Sqlite.Legacy;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class SessionRatingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GH.Ratings." + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private SqliteConnectionFactory Factory => new(_directory);
    private SqliteSessionStore History => new(Factory, _clock, () => { });
    private SessionRatings Ratings => new(new SqliteSessionRatingStore(Factory, _clock), _clock);
    private Task Seed() => new SqliteDatabaseInitializer(Factory, _clock).InitializeAsync();
    private Guid Complete(string key, DateTime end, int minutes = 20, string source = "observed")
    {
        var id = Guid.NewGuid();
        History.Complete(new() { SessionId = id, DataKey = key, StartedAt = end.AddMinutes(-minutes) }, end, source);
        return id;
    }

    [Fact]
    public async Task Rating_EditClearAndRecreate_NeverChangesHistoryOrSyncBytes()
    {
        await Seed();
        var id = Complete("游戏", _clock.Now);
        var before = History.GetSnapshot();
        var bytes = SyncBytes(before);
        var rated = Ratings.Save(new(id, 5, "中文\n私人想法", 0));
        Assert.Equal(5, rated.Rating!.Stars);
        Assert.Equal(_clock.GetUtcNow(), rated.Rating.CreatedAtUtc);
        _clock.Advance(1);
        var edited = Ratings.Save(new(id, 2, "改分", rated.Revision));
        Assert.Equal(rated.Rating.CreatedAtUtc, edited.Rating!.CreatedAtUtc);
        Assert.Equal(_clock.GetUtcNow(), edited.Rating.UpdatedAtUtc);
        var cleared = Ratings.Save(new(id, null, null, edited.Revision));
        Assert.Null(cleared.Rating);
        Assert.Throws<InvalidOperationException>(() => Ratings.Save(new(id, 4, "陈旧编辑", 0)));
        var recreated = Ratings.Save(new(id, 3, null, cleared.Revision));
        Assert.True(recreated.Revision > cleared.Revision);
        var after = History.GetSnapshot();
        Assert.Equal(before.HistoryVersion, after.HistoryVersion);
        Assert.Equal(JsonSerializer.Serialize(before.Records), JsonSerializer.Serialize(after.Records));
        Assert.Equal(bytes, SyncBytes(after));
        Assert.Equal(20, Ratings.Get(id).Minutes);
    }
    private byte[] SyncBytes(PlaytimeSnapshot snapshot) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        new StatsReportBuilder().Build(snapshot.Records, [], _clock.Now, true).Files));

    [Fact]
    public async Task Rating_ConcurrentEditors_ExactlyOneWinsAndDeletionTombstonePreventsAba()
    {
        await Seed(); var id = Complete("g", _clock.Now);
        using var barrier = new Barrier(2);
        Task<bool> Edit(int stars) => Task.Run(() =>
        {
            var service = Ratings;
            var row = service.Get(id);
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            try { service.Save(new(id, stars, null, row.Revision)); return true; }
            catch (InvalidOperationException) { return false; }
        });
        var results = await Task.WhenAll(Edit(1), Edit(5));
        Assert.Single(results, result => result);
        var saved = Ratings.Get(id);
        Ratings.Save(new(id, null, null, saved.Revision));
        Assert.Throws<InvalidOperationException>(() => Ratings.Save(new(id, 4, null, saved.Revision)));
    }

    [Theory]
    [InlineData(0)] [InlineData(6)]
    public async Task InvalidStarsAndOversizeThought_DoNotWrite(int stars)
    {
        await Seed(); var id = Complete("g", _clock.Now);
        Assert.Throws<ArgumentException>(() => Ratings.Save(new(id, stars, null, 0)));
        Assert.Throws<ArgumentException>(() => Ratings.Save(new(id, 3, new string('想', 2001), 0)));
        Assert.Null(Ratings.Get(id).Rating);
        Assert.Equal(0, Ratings.Get(id).Revision);
    }

    [Theory]
    [InlineData(9, 0)] [InlineData(10, 1)]
    public async Task ObservedMultiProcessCompletion_InvitesOnceAtPersistedMinuteBoundary(int minutes, int invitations)
    {
        await Seed();
        var notifier = new Notifications(); var monitor = new Monitor();
        var store = History;
        var playtime = new SqlitePlayTimeService(store, _clock, notifier: notifier);
        var config = new Mock<IGameConfiguration>();
        config.Setup(c => c.Read()).Returns(new AppConfig { Games = [new() { DataKey = "g", Executable = "game.exe", HdrEnabled = false }] });
        var automation = new GameAutomationService(monitor, config.Object, Mock.Of<IHdrController>(), playtime,
            NullLogger<GameAutomationService>.Instance, timeProvider: _clock);
        automation.Start();
        try
        {
            monitor.Started(1); monitor.Started(2);
            _clock.Advance(minutes);
            monitor.Stopped(1); Assert.Empty(notifier.Ids);
            monitor.Stopped(2); monitor.Stopped(2);
            Assert.Equal(invitations, notifier.Ids.Count);
            var row = Assert.Single(Ratings.Read(new()).Sessions);
            Assert.Equal(minutes, row.Minutes); Assert.Null(row.Rating);
            if (invitations == 1) Assert.Equal(row.SessionId, notifier.Ids[0]);
            // Ignore the invitation and restart: no rating, draft, read marker or reminder queue is persisted.
            Assert.Equal(0, row.Revision);
        }
        finally { automation.Stop(); }
    }

    [Fact]
    public async Task ImportRecoveryManualStopAndDuplicateCompletion_DoNotInvite()
    {
        await Seed(); var notifier = new Notifications();
        var play = new SqlitePlayTimeService(History, _clock, notifier: notifier);
        var imported = Complete("legacy", _clock.Now, source: "legacy");
        var recovered = Guid.NewGuid();
        play.WriteRecoveredSession(new() { SessionId = recovered, DataKey = "recovery", StartedAt = _clock.Now.AddMinutes(-30) }, _clock.Now);
        play.StartTracking("manual"); _clock.Advance(30); play.StopTracking("manual");
        // Replaying an already completed ID as observed cannot invite an imported or rated session.
        play.ResumeTracking("legacy", _clock.Now.AddMinutes(-20), imported, false); play.StopObservedTracking("legacy");
        var rated = Complete("rated", _clock.Now); Ratings.Save(new(rated, 4, null, 0));
        play.ResumeTracking("rated", _clock.Now.AddMinutes(-20), rated, false); play.StopObservedTracking("rated");
        Assert.Empty(notifier.Ids);
        Assert.Equal(4, Ratings.Read(new()).Sessions.Count);
    }

    [Fact]
    public async Task Editor_OpeningAndCancellingWriteNothing_FailedSavePreservesInputAndRevision()
    {
        await Seed(); var id = Complete("g", _clock.Now);
        var backend = new Mock<IGameLibrary>(MockBehavior.Strict);
        var editor = new SessionRatingEditorViewModel(backend.Object, Ratings.Get(id));
        editor.Stars = 5; editor.Thought = "保留输入";
        backend.VerifyNoOtherCalls(); // Discarding this transient editor needs no persistence operation.
        Assert.Null(Ratings.Get(id).Rating);
        backend.Setup(b => b.ExecuteAsync(It.IsAny<GameLibraryRequest>(), default)).ReturnsAsync(new GameLibraryResponse { Error = "磁盘不可写" });
        Assert.False(await editor.SaveAsync());
        Assert.Equal(5, editor.Stars); Assert.Equal("保留输入", editor.Thought); Assert.Equal("磁盘不可写", editor.Status);
        Assert.Null(Ratings.Get(id).Rating);
        backend.Setup(b => b.ExecuteAsync(It.IsAny<GameLibraryRequest>(), default)).ReturnsAsync((GameLibraryRequest request, CancellationToken _) =>
            new GameLibraryResponse { Success = true, RatedSession = Ratings.Save(request.SessionRating!) });
        Assert.True(await editor.SaveAsync()); // Failed attempt did not advance its expected revision.
        Assert.Equal("保留输入", Ratings.Get(id).Rating!.Thought);
    }

    [Fact]
    public async Task PeriodAverage_IsUnweightedAndUsesLocalEnd_NotRatingDate_AndTimelineOrdersPlayDates()
    {
        await Seed();
        _clock.Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
        var first = Complete("g", new DateTime(2025, 3, 31, 22, 0, 0, DateTimeKind.Utc), 1); // Local April 1.
        var second = Complete("g", new DateTime(2025, 6, 30, 20, 0, 0, DateTimeKind.Utc), 600);
        var outside = Complete("g", new DateTime(2025, 6, 30, 22, 0, 0, DateTimeKind.Utc)); // Local July 1.
        Complete("g", new DateTime(2025, 5, 1, 0, 0, 0), 100);
        Ratings.Save(new(second, 1, "私密想法", 0)); Ratings.Save(new(first, 5, null, 0)); Ratings.Save(new(outside, 5, null, 0));
        var timeline = Ratings.Timeline("G");
        Assert.Equal(new[] { first, second, outside }, timeline.Select(s => s.SessionId));
        var summary = SessionRatings.Summarize(new SqliteSessionRatingStore(Factory, _clock).Read(new("g")).Sessions, new(2025, 2), _clock);
        Assert.Equal(3d, summary.Average); Assert.Equal(2, summary.Samples); Assert.Equal(first, summary.Highest!.SessionId);
        Assert.Equal(new DateTime(2025, 4, 1, 0, 0, 0), summary.Highest.End);
        var empty = SessionRatings.Summarize(new SqliteSessionRatingStore(Factory, _clock).Read(new("g")).Sessions, new(2025, 1), _clock);
        Assert.Null(empty.Average); Assert.Equal(0, empty.Samples); Assert.Null(empty.Highest);
        Assert.Contains("未评分（0 局）", PlaytimeReportMarkdown.Render(new()));
    }

    [Fact]
    public async Task PagedSessions_PreserveStableIdsAndDoNotReturnWholeHistoryByDefault()
    {
        await Seed();
        var ids = Enumerable.Range(0, 35).Select(i => Complete(i % 2 == 0 ? "Étoile" : "other", _clock.Now.AddDays(-i))).ToList();
        var first = Ratings.Read(new()); var second = Ratings.Read(new(BeforeSequence: first.NextSequence));
        Assert.Equal(30, first.Sessions.Count); Assert.Equal(5, second.Sessions.Count); Assert.Null(second.NextSequence);
        Assert.Equal(ids.Order(), first.Sessions.Concat(second.Sessions).Select(s => s.SessionId).Order());
        Assert.Equal(18, Ratings.Read(new("éTOILE")).Sessions.Count);
        Assert.Empty(Ratings.Read(new(RatedOnly: true)).Sessions);
        Ratings.Save(new(ids[0], 4, null, 0));
        Assert.Single(Ratings.Read(new(RatedOnly: true)).Sessions);
    }

    [Fact]
    public async Task Report_ContainsRatingsCapturedWithHistoryAndJournal_InOneReadTransaction()
    {
        await Seed(); var id = Complete("g", _clock.Now.AddDays(-1)); Ratings.Save(new(id, 4, "不导出想法", 0));
        var snapshot = History.GetReportSnapshot(Path.Combine(_directory, "config.yml"));
        Assert.Equal(id, Assert.Single(snapshot.Ratings!).SessionId);
        var config = new Mock<IGameConfiguration>(); config.Setup(c => c.Read()).Returns(new AppConfig());
        var history = new Mock<IPlaytimeSnapshotProvider>(); history.Setup(h => h.GetReportSnapshot()).Returns(snapshot);
        var library = new GameLibrary(config.Object, Mock.Of<IGameCatalogService>(), Mock.Of<IGameAutomationService>(), Mock.Of<IMonitorControlService>(),
            Mock.Of<IGameLaunchCoordinator>(), Mock.Of<ISteamGameResolver>(), history.Object, _clock, ratings: Ratings);
        var response = await library.ExecuteAsync(new() { Operation = GameLibraryOperation.Report, ReportPeriod = new(2026) });
        Assert.Equal(4d, response.Report!.AverageRating); Assert.Equal(1, response.Report.RatingSamples);
        var markdown = PlaytimeReportMarkdown.Render(response.Report);
        Assert.Contains("4.00 / 5 · 1 局", markdown); Assert.Contains("最高分的一局", markdown); Assert.DoesNotContain("不导出想法", markdown);
        using var stream = new MemoryStream();
        await GameLibraryPipe.WriteAsync(stream, new GameLibraryRequest { Operation = GameLibraryOperation.SaveSessionRating, SessionRating = new(id, 3, "想法", 1) }, default);
        stream.Position = 0; var read = await GameLibraryPipe.ReadAsync<GameLibraryRequest>(stream, default);
        Assert.Equal(id, read.SessionRating!.SessionId); Assert.Equal("想法", read.SessionRating.Thought);
    }

    [Fact]
    public async Task ReportSnapshot_PinsRatingBeforeConcurrentEdit_AndReleasesReader()
    {
        await Seed(); var id = Complete("g", _clock.Now); var original = Ratings.Save(new(id, 2, null, 0));
        using var reading = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var task = Task.Run(() => new SqliteSessionStore(Factory, _clock, () => { }, point =>
        {
            if (point != "report.read.history") return;
            reading.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("release report read");
        }).GetReportSnapshot(Path.Combine(_directory, "config.yml")));
        try
        {
            Assert.True(reading.Wait(TimeSpan.FromSeconds(10)));
            Ratings.Save(new(id, 5, "new", original.Revision));
        }
        finally { release.Set(); }
        Assert.Equal(2, Assert.Single((await task).Ratings!).Rating!.Stars);
        Assert.Equal(5, Ratings.Get(id).Rating!.Stars);
        using var c = Factory.OpenConnection(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var result = cmd.ExecuteReader(); Assert.True(result.Read()); Assert.Equal(0, result.GetInt32(0));
    }

    [Theory]
    [InlineData("completion.commit.before")] [InlineData("completion.commit.after")]
    public async Task FailedOrLostCompletionResponse_RecoveryDoesNotChaseInvitation(string boundary)
    {
        await Seed(); var notifier = new Notifications();
        var store = new SqliteSessionStore(Factory, _clock, () => { }, point => { if (point == boundary) throw new IOException("completion failed"); });
        var play = new SqlitePlayTimeService(store, _clock, notifier: notifier);
        play.StartTracking("g"); _clock.Advance(20);
        Assert.Throws<IOException>(() => play.StopObservedTracking("g"));
        Assert.Empty(notifier.Ids);
        var pending = play.GetPendingCompletion("g")!;
        new SqlitePlayTimeService(History, _clock, notifier: notifier).WriteRecoveredSession(pending, pending.RecoveryEndAt!.Value);
        Assert.Single(Ratings.Read(new()).Sessions); Assert.Empty(notifier.Ids);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => Zone;
        public DateTime Now => GetLocalNow().DateTime;
        public void Advance(int minutes) => _now = _now.AddMinutes(minutes);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Timer();
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Notifications : ISessionRatingNotifier
    {
        public List<Guid> Ids { get; } = [];
        public void Invite(Guid sessionId) => Ids.Add(sessionId);
    }
    private sealed class Monitor : IProcessMonitor
    {
        public event Action<ProcessEventInfo>? ProcessStarted;
        public event Action<ProcessEventInfo>? ProcessStopped;
        public void Configure(ProcessObservationPolicy policy) { }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Started(int id) => ProcessStarted?.Invoke(new("game.exe", null, id));
        public void Stopped(int id) => ProcessStopped?.Invoke(new("game.exe", null, id));
    }
}
