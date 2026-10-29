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

namespace GameHelper.Tests;

public sealed class SqliteStatisticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameHelper.Statistics." + Guid.NewGuid().ToString("N"));
    private readonly ImportClock _clock = new();
    private SqliteConnectionFactory Factory => new(_directory);
    private SqliteSessionStore Store(Action<string>? checkpoint = null) => new(Factory, _clock, () => { }, checkpoint);

    private async Task Seed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "playtime.csv"), LegacyFixtures.Read("csv.input.bin"));
        var migration = new SqliteLegacyMigration(Factory, _clock);
        var candidate = await migration.PrepareAsync([]);
        await migration.ActivateAsync(candidate.ManifestPath);
    }

    [Fact]
    public async Task DatabaseSnapshot_MatchesFrozenDashboardPeriodsAndEverySyncByte_WithoutProjection()
    {
        await Seed();
        var old = new FilePlaytimeSnapshotProvider(_directory).GetSnapshot().Records;
        File.Delete(Path.Combine(_directory, "playtime.csv"));
        var snapshot = Store().GetSnapshot();
        Assert.NotNull(snapshot.HistoryVersion);
        Assert.Null(snapshot.SourcePath);
        Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(snapshot.Records));
        foreach (var days in new[] { 0, 7, 30, 90 })
            Assert.Equal(LegacyFixtures.Read($"dashboard-{days}.expected.bin"),
                JsonSerializer.SerializeToUtf8Bytes(PlaytimeDashboardBuilder.Build(snapshot.Records, [], new(days), _clock)));
        foreach (var year in new[] { 2024, 2025 })
            foreach (int? quarter in new int?[] { null, 1, 2, 3, 4 })
                Assert.Equal(LegacyFixtures.Read($"report-{year}-{quarter?.ToString() ?? "year"}.expected.bin"),
                    JsonSerializer.SerializeToUtf8Bytes(PlaytimeReportBuilder.Build(snapshot.Records, [], new(year, quarter), _clock)));
        var report = new StatsReportBuilder().Build(LegacyFixtures.ReportUtcWallClock(snapshot.Records), [], _clock.GetLocalNow().DateTime, true);
        foreach (var file in report.Files)
            Assert.Equal(LegacyFixtures.Platform(file.RelativePath.Replace('/', '_')), Encoding.UTF8.GetBytes(file.Content));

        // The public Core/IPC boundary must consume this provider without entering automation state.
        var config = new Mock<IGameConfiguration>(); config.Setup(c => c.Read()).Returns(new AppConfig());
        var automation = new Mock<IGameAutomationService>(MockBehavior.Strict);
        var library = new GameLibrary(config.Object, Mock.Of<IGameCatalogService>(), automation.Object,
            Mock.Of<IMonitorControlService>(), Mock.Of<IGameLaunchCoordinator>(), Mock.Of<ISteamGameResolver>(), Store(), _clock);
        var response = await library.ExecuteAsync(new() { Operation = GameLibraryOperation.Report, ReportPeriod = new(2025, 1) });
        Assert.True(response.Success);
        Assert.Equal(LegacyFixtures.Read("report-2025-1.expected.bin"), JsonSerializer.SerializeToUtf8Bytes(response.Report));
        var stats = new StatisticsService(Store(), config.Object, _clock);
        var oldStats = new StatisticsService(new MemoryHistory(old), config.Object, _clock);
        Assert.Equal(JsonSerializer.Serialize(oldStats.GetOverview()), JsonSerializer.Serialize(stats.GetOverview()));
        foreach (var game in old)
            Assert.Equal(JsonSerializer.Serialize(oldStats.GetDetails(game.GameName)), JsonSerializer.Serialize(stats.GetDetails(game.GameName)));
        automation.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadTransaction_PinsRevisionAndHistory_WriterCommitsWhileReaderOpen_CheckpointCanFinishAfterRelease()
    {
        await Seed();
        var store = Store();
        var before = store.GetSnapshot();
        using var readStarted = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        var reading = Task.Run(() => Store(point =>
        {
            if (point != "history.read.version") return;
            readStarted.Set();
            if (!releaseRead.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("reader release");
        }).GetSnapshot());
        try
        {
            Assert.True(readStarted.Wait(TimeSpan.FromSeconds(15)));
            Append(store, "new"); // Must commit while the other connection holds its read transaction.
            using var c = Factory.OpenConnection();
            using var command = c.CreateCommand(); command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            using var result = command.ExecuteReader(); Assert.True(result.Read());
            Assert.True(result.GetInt64(1) > result.GetInt64(2)); // Reader pins old WAL frames.
        }
        finally { releaseRead.Set(); }
        var pinned = await reading;
        Assert.Equal(before.HistoryVersion, pinned.HistoryVersion);
        Assert.Equal(JsonSerializer.Serialize(before.Records), JsonSerializer.Serialize(pinned.Records));
        var after = store.GetSnapshot();
        Assert.Equal(before.HistoryVersion!.HistoryRevision + 1, after.HistoryVersion!.HistoryRevision);
        Assert.Equal(7, after.Records.Sum(g => g.Sessions.Count));
        using var checkpoint = Factory.OpenConnection();
        using var truncate = checkpoint.CreateCommand(); truncate.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var finished = truncate.ExecuteReader(); Assert.True(finished.Read());
        Assert.Equal(0L, finished.GetInt64(0)); Assert.Equal(0L, finished.GetInt64(1));
    }

    [Fact]
    public async Task ActivationFailure_AdapterRetainsLegacyHistoryAndNoRevisionBookmark()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "playtime.csv");
        var original = LegacyFixtures.Read("csv.input.bin");
        File.WriteAllBytes(path, original);
        using var storage = new SessionStorage(Factory, _clock, NullLoggerFactory.Instance);
        await storage.InitializeAsync(Path.Combine(_directory, "config.yml"), [], checkpoint: point =>
        {
            if (point == "candidate.validated.commit.before") throw new InvalidDataException("test activation failure");
        });
        Assert.False(storage.UsesSqlite);
        IPlaytimeSnapshotProvider adapter = storage.Snapshot;
        Assert.Null(adapter.GetHistoryVersion());
        var snapshot = adapter.GetSnapshot();
        Assert.False(snapshot.ReadFailed); Assert.Null(snapshot.HistoryVersion);
        Assert.Equal(path, snapshot.SourcePath);
        Assert.Equal(JsonSerializer.Serialize(new FilePlaytimeSnapshotProvider(_directory).GetSnapshot().Records),
            JsonSerializer.Serialize(snapshot.Records));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Berlin")]
    public async Task CoreAggregation_RetainsTimezoneFilteringAndStableTies(string zone)
    {
        _clock.Zone = TimeZoneInfo.FindSystemTimeZoneById(zone);
        await Seed();
        var store = Store();
        // Same start across groups; future history is filtered only by dashboard/report, never sync.
        Append(store, "TieB"); Append(store, "TieA");
        new SqliteLegacyCompatibility(Factory, _clock, preserveHistoryKinds: true).ExportSessions();
        var old = new FilePlaytimeSnapshotProvider(_directory).GetSnapshot().Records;
        var records = store.GetSnapshot().Records;
        LegacyImportValidation.CompareHistory(old, records, _clock, [new() { DataKey = "étoile", DisplayName = "同名" }]);
        var raw = new StatsReportBuilder().Build(records, [], _clock.GetLocalNow().DateTime, true).Files.Last().Content;
        Assert.True(raw.IndexOf("TieB", StringComparison.Ordinal) < raw.IndexOf("TieA", StringComparison.Ordinal));
    }

    private void Append(SqliteSessionStore store, string key) => store.Complete(new()
    {
        SessionId = Guid.NewGuid(), DataKey = key, StartedAt = _clock.GetLocalNow().DateTime.AddMinutes(1)
    }, _clock.GetLocalNow().DateTime.AddMinutes(11), "observed");

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class MemoryHistory(IReadOnlyList<GamePlaytimeRecord> records) : IPlaytimeSnapshotProvider
    {
        public IReadOnlyList<GamePlaytimeRecord> GetPlaytimeRecords() => records;
    }
}
