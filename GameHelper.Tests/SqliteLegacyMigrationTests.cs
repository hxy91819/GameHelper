using System.Text;
using System.Text.Json;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Storage.Sqlite;
using GameHelper.Infrastructure.Storage.Sqlite.Legacy;
using Microsoft.Data.Sqlite;

namespace GameHelper.Tests;

public sealed class SqliteLegacyMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameHelper.Import." + Guid.NewGuid().ToString("N"));
    private readonly ImportClock _clock = new();
    private SqliteConnectionFactory Factory => new(_directory);
    private string Config => Path.Combine(_directory, "catalog.yml");
    private SqliteLegacyMigration Import(Action<string>? checkpoint = null) => new(Factory, _clock, checkpoint: checkpoint);

    [Fact]
    public async Task Prepare_FullInventory_PreservesSourcesAndExactLegacyValuesWithoutActivating()
    {
        Seed();
        var before = Directory.GetFiles(_directory).ToDictionary(path => path, File.ReadAllBytes);
        var result = await Import().PrepareAsync([Config], batchSize: 2);
        Assert.False(File.Exists(Factory.DatabasePath));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        var manifest = LegacySql.ReadJson<ImportManifest>(result.ManifestPath);
        Assert.Equal(9, manifest.Sources.Count);
        Assert.Contains(manifest.Sources, s => s.Kind == "json" && !s.Exists);
        foreach (var source in manifest.Sources.Where(s => s.Exists))
            Assert.Equal(before[source.Path], File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(result.ManifestPath)!, source.BackupName)));
        using var c = OpenCandidate(result);
        Assert.Equal(6L, Scalar(c, "SELECT count(*) FROM play_sessions;"));
        Assert.Equal(4L, Scalar(c, "SELECT skipped_count FROM import_sources WHERE path LIKE '%playtime.csv';"));
        Assert.Equal(1L, Scalar(c, "SELECT skipped_count FROM import_sources WHERE path LIKE '%desktop-journal.json';"));
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM inflight_sessions;"));
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM inflight_processes;"));
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM inflight_sessions i JOIN play_sessions p ON p.session_id=i.session_id;"));
        Assert.Equal(4L, Scalar(c, "SELECT sequence FROM play_sessions WHERE session_id=(SELECT session_id FROM inflight_sessions);"));
        using var tx = c.BeginTransaction(deferred: true);
        Assert.Equal(LegacyFixtures.Platform("csv.expected"), LegacyDatabaseReader.Csv(LegacyDatabaseReader.History(c, tx)));
        var scope = Convert.ToInt64(LegacySql.Scalar(c, tx, "SELECT scope_id FROM config_scopes;"));
        foreach (var kind in LegacyDecoder.SideKinds)
            Assert.Equal(LegacyFixtures.Read(kind + ".expected.bin"), LegacyDatabaseReader.Side(c, tx, scope, kind));
        Assert.Equal(LegacyFixtures.Platform("inflight.expected"), LegacyDatabaseReader.SnapshotBytes(LegacyDatabaseReader.Snapshot(c, tx)!));
        var report = new StatsReportBuilder().Build(LegacyDatabaseReader.History(c, tx), [], _clock.GetLocalNow().DateTime, true);
        foreach (var file in report.Files)
            Assert.Equal(LegacyFixtures.Platform(file.RelativePath.Replace('/', '_')), Encoding.UTF8.GetBytes(file.Content));
    }

    [Fact]
    public async Task Prepare_LegacyJson_UsesOldCsvProjectionIncludingSavedMinutesAndDateKind()
    {
        Write("playtime.json", LegacyFixtures.Read("legacy.input.bin"));
        var result = await Import().PrepareAsync([]);
        using var c = OpenCandidate(result);
        using var tx = c.BeginTransaction(deferred: true);
        var history = LegacyDatabaseReader.History(c, tx);
        Assert.Equal(LegacyFixtures.Platform("legacy.expected"), LegacyDatabaseReader.Csv(history));
        var first = history[0].Sessions[0];
        Assert.Equal(9, first.DurationMinutes);
        Assert.Equal(TimeSpan.FromSeconds(90), first.Duration);
        Assert.Equal(DateTimeKind.Unspecified, first.StartTime.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\uFEFF")]
    [InlineData("game,start_time,end_time,duration_minutes\r\n")]
    public async Task Prepare_EmptyCsv_TakesPrecedenceOverLegacyJson(string csv)
    {
        Write("playtime.csv", Encoding.UTF8.GetBytes(csv));
        Write("playtime.json", Encoding.UTF8.GetBytes("not json"));
        var result = await Import().PrepareAsync([]);
        using var c = OpenCandidate(result);
        Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM play_sessions;"));
        Assert.Equal("unused", Scalar(c, "SELECT status FROM import_sources WHERE path LIKE '%playtime.json';"));
    }

    [Theory]
    [InlineData("playtime.csv", "unexpected,header\n")]
    [InlineData("playtime.json", "{")]
    [InlineData("playtime.json", "{\"games\":{}}")]
    [InlineData("inflight-sessions.json", "{")]
    [InlineData("catalog.yml.desktop-journal.json", "{")]
    [InlineData("catalog.yml.desktop-genres.json", "{\"a\":null}")]
    [InlineData("catalog.yml.desktop-artwork.json", "{\"a\":0}")]
    [InlineData("catalog.yml.desktop-drafts.json", "[null]")]
    [InlineData("catalog.yml.desktop-scan.json", "{\"Directories\":null}")]
    [InlineData("catalog.yml.desktop-theme.json", "{")]
    public async Task Prepare_CorruptOrUnrepresentableSource_BlocksAndRetainsOriginal(string name, string content)
    {
        Write(name, Encoding.UTF8.GetBytes(content));
        await Assert.ThrowsAnyAsync<Exception>(() => Import().PrepareAsync([Config]));
        Assert.False(File.Exists(Factory.DatabasePath));
        Assert.Equal(content, File.ReadAllText(Path.Combine(_directory, name)));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "migration-backups"), "manifest.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Prepare_UnreadableFormalSource_BlocksRatherThanTreatingAsMissing()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "playtime.csv"));
        await Assert.ThrowsAnyAsync<Exception>(() => Import().PrepareAsync([]));
        Assert.False(File.Exists(Factory.DatabasePath));
    }

    [Fact]
    public async Task Prepare_ChangedSource_ReplacesCandidateButKeepsBothOriginalPackages()
    {
        SeedSimple();
        var first = await Import().PrepareAsync([]);
        using (var c = OpenCandidate(first)) Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM play_sessions;"));
        File.AppendAllText(Path.Combine(_directory, "playtime.csv"), "new,2025-01-02T10:00:00,2025-01-02T10:01:00,1\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => Import().ActivateAsync(first.ManifestPath));
        var second = await Import().PrepareAsync([]);
        Assert.NotEqual(first.ManifestPath, second.ManifestPath);
        Assert.True(File.Exists(first.ManifestPath));
        using var candidate = OpenCandidate(second);
        Assert.Equal(3L, Scalar(candidate, "SELECT count(*) FROM play_sessions;"));
        Assert.Single(Directory.GetDirectories(Path.Combine(_directory, "migration-backups"), "invalidated-*"));
    }

    [Fact]
    public async Task Prepare_ChangedGenerationEveryRenameInterruption_NeverMixesTwoSources()
    {
        SeedSimple();
        await Import().PrepareAsync([]);
        File.AppendAllText(Path.Combine(_directory, "playtime.csv"), "new,2025-01-02T10:00:00,2025-01-02T10:01:00,1\n");
        var trace = new List<string>();
        await Import(trace.Add).PrepareAsync([]);
        Directory.Delete(_directory, true);
        for (var point = 0; point < trace.Count; point++)
        {
            SeedSimple();
            var original = await Import().PrepareAsync([]);
            File.AppendAllText(Path.Combine(_directory, "playtime.csv"), "new,2025-01-02T10:00:00,2025-01-02T10:01:00,1\n");
            var fault = new FailAt(point);
            await Assert.ThrowsAsync<InjectedFailure>(() => Import(fault.Hit).PrepareAsync([]));
            var result = await Import().PrepareAsync([]);
            Assert.True(File.Exists(original.ManifestPath));
            using (var c = OpenCandidate(result)) Assert.Equal(3L, Scalar(c, "SELECT count(*) FROM play_sessions;"));
            Directory.Delete(_directory, true);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"games\":null}")]
    [InlineData("{\"games\":[]}")]
    public async Task Prepare_LegalEmptyLegacyJson_IsDistinctFromCorruptJson(string json)
    {
        Write("playtime.json", Encoding.UTF8.GetBytes(json));
        var result = await Import().PrepareAsync([]);
        using var c = OpenCandidate(result);
        Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM play_sessions;"));
    }

    [Fact]
    public async Task Prepare_InFlightRejectedKey_IsReportedAndOtherProcessesRemain()
    {
        Write("inflight-sessions.json", Encoding.UTF8.GetBytes("""
            {"SnapshotAt":"2025-01-01T10:00:00","Sessions":[{"DataKey":" "},{"DataKey":"Valid","StartedAt":"2025-01-01T09:00:00"}]}
            """));
        var result = await Import().PrepareAsync([]);
        using var c = OpenCandidate(result);
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM inflight_processes;"));
        Assert.Equal(1L, Scalar(c, "SELECT skipped_count FROM import_sources WHERE path LIKE '%inflight-sessions.json';"));
        Assert.Contains("blank DataKey", (string)Scalar(c, "SELECT reason FROM import_records WHERE disposition='skipped';")!);
    }

    [Fact]
    public async Task Prepare_EveryCommitRenameAndMarkerInterruption_ResumesWithoutDuplicateRows()
    {
        var trace = new List<string>();
        SeedSimple();
        await Import(trace.Add).PrepareAsync([], 1);
        Directory.Delete(_directory, true);
        Assert.Contains("source.batch:csv.commit.before", trace);
        Assert.Contains("source.batch:csv.commit.after", trace);
        for (var point = 0; point < trace.Count; point++)
        {
            SeedSimple();
            var fault = new FailAt(point);
            await Assert.ThrowsAsync<InjectedFailure>(() => Import(fault.Hit).PrepareAsync([], 1));
            var result = await Import().PrepareAsync([], 1);
            var again = await Import().PrepareAsync([], 1);
            Assert.Equal(result.ManifestPath, again.ManifestPath);
            using (var c = OpenCandidate(again))
            {
                Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM play_sessions;"));
                Assert.Equal(2L, Scalar(c, "SELECT count(DISTINCT session_id) FROM play_sessions;"));
                Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM import_records;"));
            }
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task Activate_EveryInterruption_UsesDatabaseActivationFactAndRepairsDiagnostic()
    {
        SeedSimple();
        var result = await Import().PrepareAsync([]);
        var trace = new List<string>();
        await Import(trace.Add).ActivateAsync(result.ManifestPath);
        Directory.Delete(_directory, true);
        for (var point = 0; point < trace.Count; point++)
        {
            SeedSimple();
            result = await Import().PrepareAsync([]);
            var fault = new FailAt(point);
            await Assert.ThrowsAsync<InjectedFailure>(() => Import(fault.Hit).ActivateAsync(result.ManifestPath));
            await Import().ActivateAsync(result.ManifestPath);
            await Import().ActivateAsync(result.ManifestPath);
            using (var c = Factory.OpenConnection())
            {
                Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM play_sessions;"));
                Assert.Equal("sqlite", Scalar(c, "SELECT compatibility_mode FROM storage_meta;"));
            }
            Assert.True(File.Exists(Path.Combine(_directory, "migration-complete.json")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Import().PrepareAsync([]));
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task Scopes_AreCaseInsensitiveAndIsolated_SharedHistoryIsNeverImportedTwice()
    {
        Seed();
        var other = Path.Combine(_directory, "other.yml");
        File.WriteAllText(other + ".desktop-journal.json", "{\"Étoile\":{\"Status\":\"搁置\",\"Note\":\"另一个配置\"}}");
        var result = await Import().PrepareAsync([Config, Config.ToUpperInvariant(), other]);
        await Import().ActivateAsync(result.ManifestPath);
        await Import().ImportScopeAsync(other.ToUpperInvariant());
        var third = Path.Combine(_directory, "third.yml");
        File.WriteAllBytes(third + ".desktop-genres.json", Encoding.UTF8.GetBytes("{\"Étoile\":[]}"));
        var fail = new FailAt(1); // after scope transaction commit
        await Assert.ThrowsAsync<InjectedFailure>(() => Import(fail.Hit).ImportScopeAsync(third));
        await Import().ImportScopeAsync(third);
        using var c = Factory.OpenConnection();
        Assert.Equal(3L, Scalar(c, "SELECT count(*) FROM config_scopes;"));
        Assert.Equal(6L, Scalar(c, "SELECT count(*) FROM play_sessions;"));
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM journals WHERE data_key='étoile';"));
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM genre_overrides WHERE data_key='étoile';"));
        Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM genre_override_items WHERE scope_id=(SELECT scope_id FROM config_scopes WHERE config_path LIKE '%third.yml');"));
    }

    [Fact]
    public async Task Scope_InterruptedBeforeCommit_DoesNotExposePartialDocuments()
    {
        var result = await Import().PrepareAsync([]);
        await Import().ActivateAsync(result.ManifestPath);
        File.WriteAllBytes(Config + ".desktop-journal.json", LegacyFixtures.Read("journal.input.bin"));
        await Assert.ThrowsAsync<InjectedFailure>(() => Import(_ => throw new InjectedFailure()).ImportScopeAsync(Config));
        using (var c = Factory.OpenConnection()) Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM config_scopes;"));
        await Import().ImportScopeAsync(Config);
        using var complete = Factory.OpenConnection();
        Assert.Equal(2L, Scalar(complete, "SELECT count(*) FROM journals;"));
    }

    [Fact]
    public async Task Prepare_FixedTimezone_ValidatesUtcAndDstWithoutReinterpretingUnspecifiedDates()
    {
        _clock.Zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Central European Standard Time" : "Europe/Berlin");
        Seed();
        var result = await Import().PrepareAsync([Config]);
        using var c = OpenCandidate(result);
        using var tx = c.BeginTransaction(deferred: true);
        var sessions = LegacyDatabaseReader.History(c, tx).SelectMany(g => g.Sessions).ToList();
        Assert.Contains(sessions, s => s.StartTime.Kind == DateTimeKind.Utc);
        Assert.Contains(sessions, s => s.StartTime.Kind == DateTimeKind.Unspecified);
    }

    private void Seed()
    {
        Write("playtime.csv", LegacyFixtures.Read("csv.input.bin"));
        Write("inflight-sessions.json", LegacyFixtures.Read("inflight.input.bin"));
        foreach (var kind in LegacyDecoder.SideKinds)
            File.WriteAllBytes(Config + ".desktop-" + kind + ".json", LegacyFixtures.Read(kind + (kind == "journal" ? ".input.bin" : ".expected.bin")));
    }
    private void SeedSimple() => Write("playtime.csv", Encoding.UTF8.GetBytes("game,start_time,end_time,duration_minutes\nA,2025-01-01T10:00:00,2025-01-01T10:01:00,1\nA,2025-01-01T10:00:00,2025-01-01T10:01:00,1\n"));
    private void Write(string name, byte[] bytes) { Directory.CreateDirectory(_directory); File.WriteAllBytes(Path.Combine(_directory, name), bytes); }
    private static SqliteConnection OpenCandidate(CandidateImport result) => SqliteConnectionFactory.OpenFile(result.CandidatePath, SqliteOpenMode.ReadWrite);
    private static object? Scalar(SqliteConnection c, string sql) => SqliteConnectionFactory.Scalar(c, sql);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

internal sealed class ImportClock : TimeProvider
{
    internal TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
    public override TimeZoneInfo LocalTimeZone => Zone;
    public override DateTimeOffset GetUtcNow() => new(2025, 4, 1, 12, 0, 0, TimeSpan.Zero);
}
internal sealed class InjectedFailure : Exception;
internal sealed class FailAt(int point)
{
    private int _current;
    internal void Hit(string name) { if (_current++ == point) throw new InjectedFailure(); }
}
