using System.Diagnostics;
using System.Text.Json;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Storage.Sqlite;
using GameHelper.Infrastructure.Storage.Sqlite.Legacy;

namespace GameHelper.Tests;

public sealed class SqliteDesktopDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GH.DesktopSqlite." + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private SqliteConnectionFactory Factory => new(_root);
    private string Config => Path.Combine(_root, "config.yml");
    private SqliteDesktopStore Store => new(Factory, Config);
    private async Task Seed()
    {
        Directory.CreateDirectory(_root);
        var migration = new SqliteLegacyMigration(Factory, _clock);
        var candidate = await migration.PrepareAsync([Config], sessionsOnly: true);
        await migration.ActivateAsync(candidate.ManifestPath);
        await migration.ImportScopeAsync(Config);
        using var connection = Factory.OpenConnection();
        using var markers = connection.CreateCommand();
        markers.CommandText = "SELECT count(*) FROM compatibility_exports WHERE dirty=1 AND target LIKE '%.desktop-%.json';";
        Assert.Equal(6L, markers.ExecuteScalar()); // Scope publication includes durable pending projection targets.
    }
    private static string Journal(string key, string note) => JsonSerializer.Serialize(new Dictionary<string, object>
        { [key] = new { Status = "已通关", CompletedOn = new DateOnly(2025, 1, 1), Note = note } });

    [Fact]
    public async Task NewScope_ImportsOnce_IsCaseInsensitiveAndDoesNotReimportSharedHistory()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "playtime.csv"), "game,start_time,end_time,duration_minutes\n游戏,2025-01-01T00:00:00,2025-01-01T00:20:00,20\n");
        File.WriteAllText(Config + ".desktop-journal.json", Journal("游戏", "甲"));
        await Seed();
        var second = Path.Combine(_root, "other.yml");
        File.WriteAllText(second + ".desktop-journal.json", Journal("游戏", "乙"));
        Assert.True(await SqliteDesktopStore.PrepareAsync(Factory, second));
        Assert.Contains("甲", JsonSerializer.Deserialize<JsonElement>(Store.Read("journal").Json).GetProperty("游戏").GetProperty("Note").GetString());
        Assert.Equal("乙", JsonSerializer.Deserialize<JsonElement>(new SqliteDesktopStore(Factory, second.ToUpperInvariant()).Read("journal").Json).GetProperty("游戏").GetProperty("Note").GetString());
        File.WriteAllText(second + ".desktop-journal.json", Journal("游戏", "不重导"));
        Assert.True(await SqliteDesktopStore.PrepareAsync(Factory, second));
        Assert.Equal("乙", JsonSerializer.Deserialize<JsonElement>(new SqliteDesktopStore(Factory, second).Read("journal").Json).GetProperty("游戏").GetProperty("Note").GetString());
        Assert.Single(Assert.Single(new SqliteSessionStore(Factory, _clock, () => { }).GetSnapshot().Records).Sessions);
    }

    [Fact]
    public async Task IndependentRowsMerge_StaleSameRowAndDeleteRecreateConflict()
    {
        await Seed();
        var first = Store.Read("journal"); var second = Store.Read("journal");
        first = Store.Save("journal", first, Journal("étoile", "一"));
        Store.Save("journal", second, Journal("other", "二"));
        Assert.Equal(2, JsonSerializer.Deserialize<JsonElement>(Store.Read("journal").Json).EnumerateObject().Count());
        Assert.Throws<InvalidOperationException>(() => Store.Save("journal", second, Journal("ÉTOILE", "冲突")));
        var beforeDelete = Store.Read("journal");
        Store.Save("journal", Store.Read("journal"), "{}");
        Store.Save("journal", Store.Read("journal"), Journal("étoile", "一"));
        Assert.Throws<InvalidOperationException>(() => Store.Save("journal", beforeDelete, Journal("étoile", "旧编辑")));
    }

    [Fact]
    public async Task EmptyGenreOverride_ArtworkAndTheme_RoundtripAndCompatibilityBytes()
    {
        await Seed();
        Store.Save("genres", Store.Read("genres"), "{\"游戏\":[]}");
        Assert.Equal("[]", JsonSerializer.Deserialize<JsonElement>(Store.Read("genres").Json).GetProperty("游戏").GetRawText());
        Store.Save("artwork", Store.Read("artwork"), "{\"游戏\":4294967295}");
        Store.Save("theme", Store.Read("theme"), "{\"Theme\":\"light\"}");
        await new SqliteLegacyCompatibility(Factory, _clock).ExportAsync();
        foreach (var kind in new[] { "genres", "artwork", "theme" })
            Assert.Equal(Store.Read(kind).Json, File.ReadAllText(Config + ".desktop-" + kind + ".json"));
        Store.Save("genres", Store.Read("genres"), "{}");
        Assert.False(JsonSerializer.Deserialize<JsonElement>(Store.Read("genres").Json).TryGetProperty("游戏", out _));
    }

    [Fact]
    public async Task Drafts_AllFrozenFieldsRoundtrip_PreserveIdsAndConflictAsCollection()
    {
        await Seed();
        var draft = new LegacyDraft(new LibraryGame { Config = new() { DataKey = "游戏", Executable = "game.exe", Launch = new() { Target = "a", Arguments = "baseline", WorkingDirectory = "base" } }, Revision = "baseline" },
            "名字", "new.exe", "target", "--args", "work", "2.", true, false, true, true, "ds4", "Ctrl+F1=", [new() { When = "beforeLaunch", Type = "run", Path = @"C:\helper.exe", Arguments = "--test", Wait = true }]);
        var json = JsonSerializer.Serialize(new[] { draft });
        var stale = Store.Read("drafts");
        var saved = Store.Save("drafts", stale, json);
        var read = Store.Read("drafts");
        Assert.Equal(json, read.Json);
        Assert.Equal(saved.DraftIds, read.DraftIds);
        var edited = JsonSerializer.Serialize(new[] { draft with { Name = "编辑" } });
        Store.Save("drafts", read, edited);
        Assert.Equal(read.DraftIds, Store.Read("drafts").DraftIds);
        Assert.Throws<InvalidOperationException>(() => Store.Save("drafts", stale, json));
    }

    [Fact]
    public async Task Scan_OrderLimitAndIgnoredPersist_OverLimitRollsBack()
    {
        await Seed();
        var document = new LegacyScan { Directories = ["F", "E", "D", "C", "B", "A"], Ignored = ["é.exe", "a.exe"] };
        var json = JsonSerializer.Serialize(document);
        Store.Save("scan", Store.Read("scan"), json);
        Assert.Equal(json, Store.Read("scan").Json);
        Assert.Throws<InvalidDataException>(() => Store.Save("scan", Store.Read("scan"), JsonSerializer.Serialize(document with { Directories = [.. document.Directories, "G"] })));
        Assert.Equal(json, Store.Read("scan").Json);
    }

    [Fact]
    public async Task Report_ReadTransactionPinsJournalWithHistory_WhenAnotherWriterCommits()
    {
        await Seed();
        Store.Save("journal", Store.Read("journal"), Journal("游戏", "原快照"));
        using var reading = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var report = Task.Run(() => new SqliteSessionStore(Factory, _clock, () => { }, point =>
        {
            if (point != "report.read.history") return;
            reading.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
        }).GetReportSnapshot(Config));
        try
        {
            Assert.True(reading.Wait(TimeSpan.FromSeconds(15)));
            Store.Save("journal", Store.Read("journal"), Journal("游戏", "新值"));
        }
        finally { release.Set(); }
        Assert.Equal("原快照", Assert.Single((await report).Journal!).Note);
        Assert.Equal("新值", Assert.Single(new SqliteSessionStore(Factory, _clock, () => { }).GetReportSnapshot(Config).Journal!).Note);
    }

    [Fact]
    public async Task DesktopWriter_OverlapsStopWriter_StopCompletesWithinEventBudget()
    {
        await Seed();
        using var acquired = new ManualResetEventSlim(); using var attempted = new ManualResetEventSlim();
        var baseline = Store.Read("journal");
        var desktop = Task.Run(() => new SqliteDesktopStore(Factory, Config, point =>
        {
            acquired.Set();
            if (!attempted.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
        }).Save("journal", baseline, Journal("游戏", "短事务")));
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(15)));
        var elapsed = Stopwatch.StartNew();
        var sessions = new SqliteSessionStore(Factory, _clock, () => { }, point => { if (point == "write.acquire.before") attempted.Set(); });
        using (sessions.BeginEvent(_clock.GetLocalNow().DateTime))
            sessions.Complete(new() { SessionId = Guid.NewGuid(), DataKey = "游戏", StartedAt = _clock.GetLocalNow().DateTime.AddMinutes(-20) }, _clock.GetLocalNow().DateTime, "observed");
        await desktop;
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), elapsed.Elapsed.ToString());
        Assert.Single(Assert.Single(sessions.GetSnapshot().Records).Sessions);
    }

    [Fact]
    public async Task MillionIgnoredItems_RollsBackWithinCooperativeBudget_ConcurrentStopCommits()
    {
        await Seed();
        var baseline = Store.Read("scan");
        var json = JsonSerializer.Serialize(new LegacyScan { Ignored = Enumerable.Range(0, 1_000_000).Select(i => "ignored-" + i).ToList() });
        using var acquired = new ManualResetEventSlim(); using var attempted = new ManualResetEventSlim();
        var held = new Stopwatch();
        var desktop = Task.Run(() =>
        {
            try
            {
                return Record.Exception(() => new SqliteDesktopStore(Factory, Config, _ =>
                {
                    held.Start(); acquired.Set();
                    if (!attempted.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                }).Save("scan", baseline, json));
            }
            finally { held.Stop(); }
        });
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(30)));
        var elapsed = Stopwatch.StartNew();
        var sessions = new SqliteSessionStore(Factory, _clock, () => { }, point => { if (point == "write.acquire.before") attempted.Set(); });
        using (sessions.BeginEvent(_clock.GetLocalNow().DateTime))
            sessions.Complete(new() { SessionId = Guid.NewGuid(), DataKey = "游戏", StartedAt = _clock.GetLocalNow().DateTime.AddMinutes(-20) }, _clock.GetLocalNow().DateTime, "observed");
        Assert.NotNull(await desktop);
        Assert.True(held.Elapsed < TimeSpan.FromMilliseconds(1500), "250 ms cooperative budget plus one-second scheduling/disk margin: " + held.Elapsed);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), elapsed.Elapsed.ToString());
        Assert.Equal(baseline.Json, Store.Read("scan").Json);
        Assert.Single(Assert.Single(sessions.GetSnapshot().Records).Sessions);
    }

    [Fact]
    public async Task LongSingleDelete_IsInterruptedAndRolledBackBySqliteProgressDeadline()
    {
        await Seed();
        Store.Save("scan", Store.Read("scan"), JsonSerializer.Serialize(new LegacyScan { Ignored = ["old.exe"] }));
        var baseline = Store.Read("scan");
        using (var connection = Factory.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            // Forces a long VM statement inside DELETE, independent of disk speed or the input loop.
            command.CommandText = """
                CREATE TRIGGER slow_delete BEFORE DELETE ON scan_ignored BEGIN
                  SELECT sum(n) FROM (WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<100000000) SELECT n FROM numbers);
                END;
                """;
            command.ExecuteNonQuery();
        }
        var elapsed = Stopwatch.StartNew();
        var failure = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Store.Save("scan", baseline, JsonSerializer.Serialize(new LegacyScan())));
        Assert.Equal(9, failure.SqliteErrorCode); // SQLITE_INTERRUPT, not a post-hoc timeout after DELETE finished.
        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(1500), elapsed.Elapsed.ToString());
        Assert.Equal(baseline.Json, Store.Read("scan").Json);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddYears(55).AddDays(2);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
