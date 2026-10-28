using System.Text;
using System.Text.Json;
using GameHelper.Infrastructure.Storage.Sqlite;
using GameHelper.Infrastructure.Storage.Sqlite.Legacy;
using Microsoft.Data.Sqlite;

namespace GameHelper.Tests;

public sealed class SqliteLegacyCompatibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameHelper.Compatibility." + Guid.NewGuid().ToString("N"));
    private readonly ImportClock _clock = new();
    private SqliteConnectionFactory Factory => new(_directory);
    private string Config => Path.Combine(_directory, "config.yml");
    private SqliteLegacyCompatibility Compatibility(Action<string>? checkpoint = null) => new(Factory, _clock, checkpoint: checkpoint);

    [Fact]
    public async Task Projection_IsGeneratedFromRelationalValuesAndMatchesFrozenBytes()
    {
        await Seed(full: true);
        var files = Compatibility().ReadProjection().Files;
        Assert.Equal(LegacyFixtures.Platform("csv.expected"), files[Path.Combine(_directory, "playtime.csv")]);
        Assert.Equal(LegacyFixtures.Read("inflight.expected.bin"), files[Path.Combine(_directory, "inflight-sessions.json")]);
        foreach (var kind in LegacyDecoder.SideKinds)
            Assert.Equal(LegacyFixtures.Read(kind + ".expected.bin"), files[Config + ".desktop-" + kind + ".json"]);
        using (var c = Factory.OpenConnection())
            Execute(c, "UPDATE journals SET note='changed in database'; UPDATE storage_meta SET data_revision=data_revision+1;");
        var changed = Compatibility().ReadProjection();
        Assert.Contains("changed in database", Encoding.UTF8.GetString(changed.Files[Config + ".desktop-journal.json"]!));
        await Compatibility().ExportAsync();
        using var complete = Factory.OpenConnection();
        Assert.Equal(0L, Scalar(complete, "SELECT sum(dirty) FROM compatibility_exports;"));
    }

    [Fact]
    public async Task Projection_EveryCommitReplaceAndMarkerInterruption_IsRetryable()
    {
        await Seed(full: true);
        var trace = new List<string>();
        await Compatibility(trace.Add).ExportAsync();
        foreach (var point in Enumerable.Range(0, trace.Count))
        {
            var fault = new FailAt(point);
            await Assert.ThrowsAsync<InjectedFailure>(() => Compatibility(fault.Hit).ExportAsync());
            await Compatibility().ExportAsync();
            using var c = Factory.OpenConnection();
            Assert.Equal(6L, Scalar(c, "SELECT count(*) FROM play_sessions;"));
            Assert.Equal(0L, Scalar(c, "SELECT sum(dirty) FROM compatibility_exports;"));
            foreach (var (path, bytes) in Compatibility().ReadProjection().Files)
                Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public async Task Projection_FailedReplacement_RecordsFailureAndDoesNotChangeDatabaseHistory()
    {
        await Seed();
        var path = Path.Combine(_directory, "playtime.csv");
        File.Delete(path); Directory.CreateDirectory(path);
        await Assert.ThrowsAnyAsync<Exception>(() => Compatibility().ExportAsync());
        using (var c = Factory.OpenConnection())
        {
            Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM play_sessions;"));
            Assert.Equal(1L, Scalar(c, "SELECT dirty FROM compatibility_exports WHERE target LIKE '%playtime.csv';"));
            Assert.NotEqual("", Scalar(c, "SELECT failure FROM compatibility_exports WHERE target LIKE '%playtime.csv';"));
        }
        Directory.Delete(path);
        await Compatibility().ExportAsync();
    }

    [Fact]
    public async Task Projection_ConcurrentRevisionChange_RemainsDirtyAndBlocksDowngrade()
    {
        await Seed();
        var changed = false;
        await Compatibility(name =>
        {
            if (changed || !name.StartsWith("projection.replace.after:", StringComparison.Ordinal)) return;
            changed = true;
            using var c = Factory.OpenConnection();
            Execute(c, "UPDATE storage_meta SET data_revision=data_revision+1;");
        }).ExportAsync();
        using (var c = Factory.OpenConnection()) Assert.True((long)Scalar(c, "SELECT sum(dirty) FROM compatibility_exports;")! > 0);
        await Assert.ThrowsAsync<InvalidDataException>(() => Compatibility(name =>
        {
            if (!name.StartsWith("projection.replace.after:", StringComparison.Ordinal)) return;
            using var c = Factory.OpenConnection();
            Execute(c, "UPDATE storage_meta SET data_revision=data_revision+1;");
        }).PrepareDowngradeAsync());
    }

    [Fact]
    public async Task DowngradeAndReupgrade_PreservesKnownIdsRatingsAndDuplicateOccurrencesAndAddsOldVersionDataOnce()
    {
        await Seed(full: true);
        var originalIds = Ids();
        var rated = originalIds[0];
        using (var c = Factory.OpenConnection())
            LegacySql.Execute(c, null, "INSERT INTO session_ratings VALUES($0,5,'private','created','updated',1); UPDATE storage_meta SET data_revision=data_revision+1;", rated);
        var receipt = await Compatibility().PrepareDowngradeAsync();
        var manifest = LegacySql.ReadJson<DowngradeManifest>(receipt);
        Assert.Equal(6, manifest.Sessions.Count);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(receipt)!, "userdata.db")));
        // Old application appends both a new game and another occurrence identical to a known row.
        File.AppendAllText(Path.Combine(_directory, "playtime.csv"), "OldNew,2025-04-01T10:00:00,2025-04-01T10:15:00,15\n" +
            "éTOILE,2025-01-02T10:00:00,2025-01-02T10:10:00,3\n");
        File.WriteAllText(Config + ".desktop-journal.json", "{\"Étoile\":{\"Status\":\"已通关\",\"CompletedOn\":\"2025-04-01\",\"Note\":\"旧版新增\"}}");
        File.WriteAllText(Config + ".desktop-genres.json", "{\"Étoile\":[],\"OldNew\":[\"新类型\"]}");
        File.Delete(Path.Combine(_directory, "inflight-sessions.json"));
        await Compatibility().ReupgradeAsync(receipt);
        await Compatibility().ReupgradeAsync(receipt);
        using var merged = Factory.OpenConnection();
        Assert.Equal(8L, Scalar(merged, "SELECT count(*) FROM play_sessions;"));
        foreach (var id in originalIds) Assert.Contains(id, Ids());
        Assert.Equal(rated, Scalar(merged, "SELECT session_id FROM session_ratings;"));
        Assert.Equal(5L, Scalar(merged, "SELECT stars FROM session_ratings;"));
        Assert.Equal("旧版新增", Scalar(merged, "SELECT note FROM journals WHERE data_key='éTOILE';"));
        Assert.Equal(0L, Scalar(merged, "SELECT count(*) FROM genre_override_items WHERE data_key='éTOILE';"));
        Assert.Equal(0L, Scalar(merged, "SELECT count(*) FROM runtime_snapshot;"));
        Assert.Equal(1L, Scalar(merged, "SELECT count(*) FROM play_sessions WHERE data_key='OldNew';"));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("edit")]
    [InlineData("invalid")]
    [InlineData("missing")]
    public async Task Reupgrade_AmbiguousOrInvalidHistory_StopsWithoutChangingIdsOrPrivateData(string change)
    {
        await Seed();
        var ids = Ids();
        var receipt = await Compatibility().PrepareDowngradeAsync();
        var csv = Path.Combine(_directory, "playtime.csv");
        switch (change)
        {
            case "delete": File.WriteAllText(csv, "game,start_time,end_time,duration_minutes\n"); break;
            case "edit": File.WriteAllText(csv, File.ReadAllText(csv).Replace(",1", ",2", StringComparison.Ordinal)); break;
            case "invalid": File.AppendAllText(csv, "bad,row\n"); break;
            case "missing": File.Delete(csv); break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Compatibility().ReupgradeAsync(receipt));
        Assert.Equal(ids, Ids());
        Assert.Single(Directory.GetDirectories(Path.Combine(_directory, "reupgrade-backups")));
    }

    [Fact]
    public async Task Reupgrade_EveryBackupAndCommitInterruption_ResumesWithoutRecounting()
    {
        await Seed();
        var receipt = await Compatibility().PrepareDowngradeAsync();
        Append();
        var trace = new List<string>();
        await Compatibility(trace.Add).ReupgradeAsync(receipt);
        Directory.Delete(_directory, true);
        for (var point = 0; point < trace.Count; point++)
        {
            await Seed();
            receipt = await Compatibility().PrepareDowngradeAsync();
            var known = Ids();
            Append();
            var fault = new FailAt(point);
            await Assert.ThrowsAsync<InjectedFailure>(() => Compatibility(fault.Hit).ReupgradeAsync(receipt));
            await Compatibility().ReupgradeAsync(receipt);
            await Compatibility().ReupgradeAsync(receipt);
            using (var c = Factory.OpenConnection())
            {
                Assert.Equal(3L, Scalar(c, "SELECT count(*) FROM play_sessions;"));
                foreach (var id in known) Assert.Contains(id, Ids());
            }
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task Downgrade_EveryPublicationAndProjectionInterruption_RetryHasVerifiedArchiveAndHashes()
    {
        await Seed();
        var trace = new List<string>();
        await Compatibility(trace.Add).PrepareDowngradeAsync();
        foreach (var point in Enumerable.Range(0, trace.Count))
        {
            var known = Ids();
            var fault = new FailAt(point);
            await Assert.ThrowsAsync<InjectedFailure>(() => Compatibility(fault.Hit).PrepareDowngradeAsync());
            var receipt = await Compatibility().PrepareDowngradeAsync();
            var manifest = LegacySql.ReadJson<DowngradeManifest>(receipt);
            Assert.Equal(known, Ids());
            foreach (var (path, hash) in manifest.FileHashes)
                Assert.Equal(hash, File.Exists(path) ? LegacySql.Hash(File.ReadAllBytes(path)) : "missing");
            using var archive = SqliteConnectionFactory.OpenFile(Path.Combine(Path.GetDirectoryName(receipt)!, "userdata.db"), SqliteOpenMode.ReadOnly);
            Assert.Equal(2L, Scalar(archive, "SELECT count(*) FROM play_sessions;"));
        }
    }

    [Fact]
    public async Task Reupgrade_ChangedDatabaseOrChangedAlreadyMergedInterval_Blocks()
    {
        await Seed();
        var receipt = await Compatibility().PrepareDowngradeAsync();
        Append();
        await Compatibility().ReupgradeAsync(receipt);
        Append();
        await Assert.ThrowsAsync<InvalidDataException>(() => Compatibility().ReupgradeAsync(receipt));
        using var c = Factory.OpenConnection();
        Assert.Equal(3L, Scalar(c, "SELECT count(*) FROM play_sessions;"));
    }

    private async Task Seed(bool full = false)
    {
        Directory.CreateDirectory(_directory);
        if (full)
        {
            File.WriteAllBytes(Path.Combine(_directory, "playtime.csv"), LegacyFixtures.Read("csv.input.bin"));
            File.WriteAllBytes(Path.Combine(_directory, "inflight-sessions.json"), LegacyFixtures.Read("inflight.expected.bin"));
            foreach (var kind in LegacyDecoder.SideKinds)
                File.WriteAllBytes(Config + ".desktop-" + kind + ".json", LegacyFixtures.Read(kind + ".expected.bin"));
        }
        else File.WriteAllText(Path.Combine(_directory, "playtime.csv"), "game,start_time,end_time,duration_minutes\nA,2025-01-01T10:00:00,2025-01-01T10:01:00,1\nA,2025-01-01T10:00:00,2025-01-01T10:01:00,1\n");
        var importer = new SqliteLegacyMigration(Factory, _clock);
        var candidate = await importer.PrepareAsync(full ? [Config] : []);
        await importer.ActivateAsync(candidate.ManifestPath);
    }
    private void Append() => File.AppendAllText(Path.Combine(_directory, "playtime.csv"), "New,2025-04-01T10:00:00,2025-04-01T10:15:00,15\n");
    private List<string> Ids()
    {
        using var c = Factory.OpenConnection();
        using var command = c.CreateCommand(); command.CommandText = "SELECT session_id FROM play_sessions ORDER BY sequence;";
        using var reader = command.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
    private static object? Scalar(SqliteConnection c, string sql) => SqliteConnectionFactory.Scalar(c, sql);
    private static void Execute(SqliteConnection c, string sql) => SqliteConnectionFactory.Execute(c, sql);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
