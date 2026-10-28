using GameHelper.Core.Models;
using GameHelper.Infrastructure.Storage.Sqlite;
using GameHelper.Infrastructure.Storage.Sqlite.Legacy;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests;

public sealed class SqliteMaintenanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GH.Maintenance." + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private SqliteConnectionFactory Factory => new(_directory);
    private string Config => Path.Combine(_directory, "config.yml");
    private Task Initialize() => new SqliteDatabaseInitializer(Factory, _clock).InitializeAsync();
    private void Sql(string sql) { using var c = Factory.OpenConnection(); SqliteConnectionFactory.Execute(c, sql); }

    [Fact]
    public async Task Maintenance_DailyChangedData_BackupAndHealthHaveIndependentRetryAndRetention()
    {
        await Initialize();
        var maintenance = new SqliteStorageMaintenance(Factory, _clock);
        Assert.Null(await maintenance.RunAsync());
        for (var revision = 1; revision <= 9; revision++)
        {
            Sql($"UPDATE storage_meta SET data_revision={revision};");
            Assert.NotNull(await maintenance.RunAsync());
            Assert.Null(await maintenance.RunAsync());
            _clock.AdvanceDay();
            Assert.Null(await maintenance.RunAsync());
        }
        Assert.Equal(7, Directory.GetDirectories(Path.Combine(_directory, "backups"), "daily-*").Length);
        using var c = Factory.OpenConnection();
        Assert.Equal(_clock.GetUtcNow().ToString("yyyy-MM-dd"), SqliteConnectionFactory.Scalar(c,
            "SELECT value FROM migration_state WHERE name='maintenance.health.utc';"));
    }

    [Fact]
    public async Task Maintenance_ActiveBusinessWrite_SkipsRatherThanWaitingOrBackingUp()
    {
        await Initialize(); Sql("UPDATE storage_meta SET data_revision=1;");
        using (SqliteMaintenanceAdmission.EnterWrite(_directory, () => TimeSpan.Zero))
            Assert.Null(await new SqliteStorageMaintenance(Factory, _clock).RunAsync());
        Assert.False(Directory.Exists(Path.Combine(_directory, "backups")));
        Assert.NotNull(await new SqliteStorageMaintenance(Factory, _clock).RunAsync());
    }

    [Fact]
    public async Task BackupIoFailure_WarnsAndRetriesWithoutHidingProjectionWarning()
    {
        using var storage = new SessionStorage(Factory, _clock, NullLoggerFactory.Instance);
        await storage.InitializeAsync(Config, []);
        // Directory-shaped projection target and file-shaped backup root are deterministic I/O substitutes.
        File.Delete(Path.Combine(_directory, "playtime.csv"));
        Directory.CreateDirectory(Path.Combine(_directory, "playtime.csv"));
        File.WriteAllText(Path.Combine(_directory, "backups"), "unwritable destination");
        new SqliteSessionStore(Factory, _clock, () => { }).Complete(new()
        {
            SessionId = Guid.NewGuid(), DataKey = "saved", StartedAt = _clock.GetUtcNow().UtcDateTime.AddMinutes(-1)
        }, _clock.GetUtcNow().UtcDateTime, "observed");
        await storage.MaintainAsync();
        Assert.Contains("旧版兼容投影失败", storage.Warning);
        Assert.Contains("备份失败", storage.Warning);
        Assert.Single(storage.Snapshot.GetPlaytimeRecords());
        Directory.Delete(Path.Combine(_directory, "playtime.csv"));
        storage.FlushProjection();
        Assert.Contains("备份失败", storage.Warning);
        File.Delete(Path.Combine(_directory, "backups"));
        await storage.MaintainAsync();
        Assert.Null(storage.Warning);
        Assert.Single(Directory.GetDirectories(Path.Combine(_directory, "backups"), "daily-*"));
    }

    [Fact]
    public async Task IntegrityFailure_StopsOtherFactoriesWritesAndSyncReads_UntilVerifiedRestore()
    {
        await Initialize(); Sql("UPDATE storage_meta SET data_revision=1;");
        var backup = (await new SqliteDatabaseBackup(Factory, _clock).CreateDailyBackupAsync())!;
        Sql("PRAGMA foreign_keys=OFF; INSERT INTO session_ratings VALUES('missing',3,NULL,'utc','utc',1);");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SqliteStorageMaintenance(Factory, _clock).RunAsync());
        Assert.Throws<InvalidDataException>(() => new SqliteConnectionFactory(_directory).OpenConnection());
        var snapshot = new SqliteSessionStore(Factory, _clock, () => { }).GetSnapshot();
        Assert.True(snapshot.ReadFailed);
        await new SqliteDatabaseBackup(Factory, _clock).RestoreAsync(backup);
        await Initialize();
        using var restored = Factory.OpenConnection();
        Assert.Equal(0L, SqliteConnectionFactory.Scalar(restored, "SELECT count(*) FROM session_ratings;"));
        Assert.Single(Directory.GetDirectories(Path.Combine(_directory, "recovery-originals")));
    }

    [Fact]
    public async Task Restore_BackupWithMatchingHashButBrokenForeignKeys_RefusesSwap()
    {
        await Initialize(); Sql("UPDATE storage_meta SET data_revision=1;");
        var backup = (await new SqliteDatabaseBackup(Factory, _clock).CreateDailyBackupAsync())!;
        var database = Path.Combine(backup, "userdata.db");
        using (var candidate = SqliteConnectionFactory.OpenFile(database, SqliteOpenMode.ReadWrite))
            SqliteConnectionFactory.Execute(candidate, "PRAGMA foreign_keys=OFF; INSERT INTO session_ratings VALUES('orphan',2,NULL,'utc','utc',1);");
        var manifestPath = Path.Combine(backup, "manifest.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["Sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database)));
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var before = File.ReadAllBytes(Factory.DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SqliteDatabaseBackup(Factory, _clock).RestoreAsync(backup));
        Assert.Equal(before, File.ReadAllBytes(Factory.DatabasePath));
        Assert.False(Directory.Exists(Path.Combine(_directory, "recovery-originals")));
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 13)]
    public async Task NativeReadOnlyAndFullDiskSubstitutes_RollBackWholeTransaction(bool full, int error)
    {
        await Initialize();
        using var c = Factory.OpenConnection();
        if (full)
        {
            var pages = SqliteConnectionFactory.Scalar(c, "PRAGMA page_count;");
            SqliteConnectionFactory.Execute(c, $"PRAGMA max_page_count={pages};");
        }
        else SqliteConnectionFactory.Execute(c, "PRAGMA query_only=ON;");
        using (var tx = c.BeginTransaction(deferred: true))
        {
            var ex = Assert.Throws<SqliteException>(() => SqliteConnectionFactory.Execute(c,
                "INSERT INTO migration_state VALUES('probe',hex(zeroblob(1048576)));", tx));
            Assert.Equal(error, ex.SqliteErrorCode);
        }
        Assert.Equal(0L, SqliteConnectionFactory.Scalar(c, "SELECT count(*) FROM migration_state WHERE name='probe';"));
        Assert.Equal(0L, SqliteConnectionFactory.Scalar(c, "SELECT data_revision FROM storage_meta;"));
    }

    [Fact]
    public async Task DowngradePreflight_RejectsStaleFiles_ThenAcceptsCompleteProjection()
    {
        using var storage = new SessionStorage(Factory, _clock, NullLoggerFactory.Instance);
        await storage.InitializeAsync(Config, []);
        var compatibility = new SqliteLegacyCompatibility(Factory, _clock, preserveHistoryKinds: true);
        await compatibility.CheckDowngradeAsync();
        File.AppendAllText(Path.Combine(_directory, "playtime.csv"), "stale");
        await Assert.ThrowsAsync<InvalidDataException>(() => compatibility.CheckDowngradeAsync());
        var manifest = await compatibility.PrepareDowngradeAsync();
        Assert.True(File.Exists(manifest));
        await compatibility.CheckDowngradeAsync();
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddYears(55);
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        internal void AdvanceDay() => _now = _now.AddDays(1);
    }
}
