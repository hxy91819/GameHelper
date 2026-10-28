using GameHelper.Infrastructure.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHelper.Tests;

public sealed class SqliteStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameHelper.SqliteTests." + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly TestClock _clock = new();

    public SqliteStorageTests() => _factory = new SqliteConnectionFactory(_directory);

    [Fact]
    public async Task Initialize_EmptyDiskDatabase_CreatesAllTablesAndRequiredIndexes()
    {
        await Initialize();
        using var connection = _factory.OpenConnection();
        Assert.Equal(2L, Scalar(connection, "PRAGMA user_version;"));
        Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode;"));
        Assert.Equal(5000L, Scalar(connection, "PRAGMA busy_timeout;"));
        Assert.Equal(1L, Scalar(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(2L, Scalar(connection, "PRAGMA synchronous;"));
        var tables = new[] { "storage_meta", "config_scopes", "history_keys", "play_sessions", "session_ratings", "journals",
            "genre_overrides", "genre_override_items", "artwork_links", "desktop_drafts", "scan_roots", "scan_ignored",
            "desktop_preferences", "runtime_snapshot", "inflight_sessions", "inflight_processes", "pending_launches",
            "import_sources", "import_records", "compatibility_exports" };
        foreach (var table in tables)
            Assert.Equal(1L, Scalar(connection, $"SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='{table}';"));
        Assert.Equal(4L, Scalar(connection, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name LIKE 'ix_%';"));
        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM storage_meta;"));
        Assert.True(Guid.TryParse((string)Scalar(connection, "SELECT database_id FROM storage_meta;"), out _));
    }

    [Fact]
    public async Task Initialize_VersionOne_UpgradesWithoutLosingRowsAndBacksUpPreviousVersion()
    {
        await Initialize();
        using (var connection = _factory.OpenConnection())
        {
            Execute(connection, "INSERT INTO history_keys VALUES ('旧游戏', 1); DROP INDEX ix_play_sessions_key_sequence; DROP INDEX ix_session_ratings_updated; DROP INDEX ix_inflight_sessions_key; DROP INDEX ix_inflight_processes_session; PRAGMA user_version=1;");
        }
        await Initialize();
        using var upgraded = _factory.OpenConnection();
        Assert.Equal("旧游戏", Scalar(upgraded, "SELECT data_key FROM history_keys;"));
        Assert.Equal(2L, Scalar(upgraded, "PRAGMA user_version;"));
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(_directory, "backups"), "upgrade-*"));
        using var original = SqliteConnectionFactory.OpenFile(Path.Combine(backup, "userdata.db"), SqliteOpenMode.ReadOnly);
        Assert.Equal(1L, Scalar(original, "PRAGMA user_version;"));
        Assert.Equal("旧游戏", Scalar(original, "SELECT data_key FROM history_keys;"));
    }

    [Fact]
    public async Task Initialize_FutureVersion_RefusesBothInitializerAndWriterWithoutChangingDatabase()
    {
        await Initialize();
        using (var connection = _factory.OpenConnection()) Execute(connection, "PRAGMA user_version=999;");
        var bytes = File.ReadAllBytes(_factory.DatabasePath);
        await Assert.ThrowsAsync<NotSupportedException>(Initialize);
        Assert.Throws<NotSupportedException>(() => _factory.OpenConnection());
        Assert.Equal(bytes, File.ReadAllBytes(_factory.DatabasePath));
    }

    [Fact]
    public async Task Initialize_FailingSecondDdl_RollsBackAllDdlMetadataAndVersion()
    {
        var migrations = ReadMigrations();
        migrations[1] += "CREATE TABLE failure_probe (id INTEGER); THIS IS INVALID SQL;";
        var broken = new SqliteDatabaseInitializer(_factory, _clock, migrations);
        await Assert.ThrowsAsync<SqliteException>(() => broken.InitializeAsync());
        using (var connection = SqliteConnectionFactory.OpenFile(_factory.DatabasePath, SqliteOpenMode.ReadWrite))
        {
            Assert.Equal(0L, Scalar(connection, "PRAGMA user_version;"));
            Assert.Equal(0L, Scalar(connection, "SELECT count(*) FROM sqlite_schema WHERE type='table';"));
        }
        await Initialize();
    }

    [Fact]
    public async Task Initialize_FailingUpgrade_KeepsPreviousSchemaDataAndVersion()
    {
        await Initialize();
        using (var connection = _factory.OpenConnection())
            Execute(connection, "INSERT INTO history_keys VALUES ('retained', 1); PRAGMA user_version=1;");
        var migrations = ReadMigrations();
        migrations[1] = "CREATE TABLE failure_probe (id INTEGER); INVALID SQL;";
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteDatabaseInitializer(_factory, _clock, migrations).InitializeAsync());
        using var previous = SqliteConnectionFactory.OpenFile(_factory.DatabasePath, SqliteOpenMode.ReadWrite);
        Assert.Equal(1L, Scalar(previous, "PRAGMA user_version;"));
        Assert.Equal("retained", Scalar(previous, "SELECT data_key FROM history_keys;"));
        Assert.Equal(0L, Scalar(previous, "SELECT count(*) FROM sqlite_schema WHERE name='failure_probe';"));
    }

    [Theory]
    [InlineData("étoile", "ÉTOILE")]
    [InlineData("игра", "ИГРА")]
    [InlineData("游戏", "游戏")]
    public async Task Connections_UnicodeKeys_UseOrdinalIgnoreCaseAndRetainOriginalSpelling(string original, string equivalent)
    {
        await Initialize();
        using var first = _factory.OpenConnection();
        using var second = _factory.OpenConnection();
        using var insert = first.CreateCommand();
        insert.CommandText = "INSERT INTO history_keys VALUES ($key, 1);";
        insert.Parameters.AddWithValue("$key", original);
        insert.ExecuteNonQuery();
        using var read = second.CreateCommand();
        read.CommandText = "SELECT data_key FROM history_keys WHERE data_key=$key;";
        read.Parameters.AddWithValue("$key", equivalent);
        Assert.Equal(original, read.ExecuteScalar());
        insert.Parameters[0].Value = equivalent;
        Assert.Equal(19, Assert.Throws<SqliteException>(() => insert.ExecuteNonQuery()).SqliteErrorCode);
    }

    [Fact]
    public async Task Schema_Constraints_EnforceForeignKeysRatingsAndScopeIsolation()
    {
        await Initialize();
        using var connection = _factory.OpenConnection();
        AssertConstraint(connection, "INSERT INTO session_ratings VALUES ('missing', 3, NULL, 'utc', 'utc', 1);");
        Execute(connection, "INSERT INTO history_keys VALUES ('Étoile', 1);" + SessionInsert);
        foreach (var stars in new[] { 0, 6 })
            AssertConstraint(connection, $"INSERT INTO session_ratings VALUES ('session', {stars}, NULL, 'utc', 'utc', 1);");
        AssertConstraint(connection, "INSERT INTO session_ratings VALUES ('session', 1.5, NULL, 'utc', 'utc', 1);");
        Execute(connection, "INSERT INTO session_ratings VALUES ('session', 5, NULL, 'utc', 'utc', 1);");
        AssertConstraint(connection, "DELETE FROM play_sessions WHERE session_id='session';");
        AssertConstraint(connection, "INSERT INTO journals VALUES (99, 'no-history', '在玩', NULL, '', 1);");
        Execute(connection, "INSERT INTO config_scopes VALUES (1, 'C:/a.yml'), (2, 'C:/b.yml'); INSERT INTO journals VALUES (1, 'no-history', '在玩', NULL, '', 1), (2, 'NO-HISTORY', '全成就', NULL, '', 1);");
        Assert.Equal(2L, Scalar(connection, "SELECT count(*) FROM journals;"));
        AssertConstraint(connection, "INSERT INTO config_scopes VALUES (3, 'c:/A.YML');");
        AssertConstraint(connection, "INSERT INTO scan_roots VALUES (1, 'C:/games', 6);");
        AssertConstraint(connection, "INSERT INTO artwork_links VALUES (1, 'x', 4294967296, 1);");
        AssertConstraint(connection, "UPDATE play_sessions SET duration_minutes=-1;");
    }

    [Fact]
    public async Task Initialize_ConcurrentFactoriesOnSameDisk_ProduceOneIdentityAndSchema()
    {
        using var ready = new CountdownEvent(8);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var factory = new SqliteConnectionFactory(_directory);
            ready.Signal();
            await start.Task;
            await new SqliteDatabaseInitializer(factory, _clock).InitializeAsync();
            using var connection = factory.OpenConnection();
            return (string)Scalar(connection, "SELECT database_id FROM storage_meta;");
        })).ToArray();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)));
        start.SetResult();
        var identities = await Task.WhenAll(tasks);
        Assert.Single(identities.Distinct());
        Assert.Single(Directory.GetFiles(_directory, "*.db"));
        Assert.False(Directory.Exists(Path.Combine(_directory, "backups")));
    }

    [Fact]
    public async Task BackupRestore_CommittedWalData_IsReadableAndChangesIdentityWhilePreservingOriginal()
    {
        await Initialize();
        string oldId;
        string backup;
        using (var connection = _factory.OpenConnection())
        {
            Execute(connection, "INSERT INTO history_keys VALUES ('Étoile', 1);" + SessionInsert + "UPDATE storage_meta SET history_revision=1, data_revision=1;");
            oldId = (string)Scalar(connection, "SELECT database_id FROM storage_meta;");
            Assert.True(File.Exists(_factory.DatabasePath + "-wal"));
            backup = (await new SqliteDatabaseBackup(_factory, _clock).CreateDailyBackupAsync())!;
            Assert.NotNull(backup);
            Execute(connection, "INSERT INTO history_keys VALUES ('after-backup', 2);");
        }
        await new SqliteDatabaseBackup(_factory, _clock).RestoreAsync(backup);
        await Initialize();
        using var restored = _factory.OpenConnection();
        Assert.Equal(1L, Scalar(restored, "SELECT count(*) FROM history_keys;"));
        Assert.Equal(1L, Scalar(restored, "SELECT duration_minutes FROM play_sessions;"));
        Assert.NotEqual(oldId, Scalar(restored, "SELECT database_id FROM storage_meta;"));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "recovery-originals"), "userdata.db", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Restore_DamagedBackup_RefusesReplacementAndRetainsLiveData()
    {
        await Initialize();
        using (var connection = _factory.OpenConnection()) Execute(connection, "UPDATE storage_meta SET data_revision=1;");
        var backups = new SqliteDatabaseBackup(_factory, _clock);
        var backup = (await backups.CreateDailyBackupAsync())!;
        File.WriteAllText(Path.Combine(backup, "userdata.db"), "broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.RestoreAsync(backup));
        using var live = _factory.OpenConnection();
        Assert.Equal(1L, Scalar(live, "SELECT data_revision FROM storage_meta;"));
        Assert.Empty(Directory.GetFiles(_directory, "restore-*"));
    }

    [Fact]
    public async Task DailyBackup_FixedClockAndRevisions_SkipsUnchangedAndRetainsSevenSuccessfulDates()
    {
        await Initialize();
        var backups = new SqliteDatabaseBackup(_factory, _clock);
        Assert.Null(await backups.CreateDailyBackupAsync());
        for (var revision = 1; revision <= 9; revision++)
        {
            using (var connection = _factory.OpenConnection()) Execute(connection, $"UPDATE storage_meta SET data_revision={revision};");
            Assert.NotNull(await backups.CreateDailyBackupAsync());
            Assert.Null(await backups.CreateDailyBackupAsync());
            _clock.AdvanceDay();
            Assert.Null(await backups.CreateDailyBackupAsync());
        }
        Assert.Equal(7, Directory.GetDirectories(Path.Combine(_directory, "backups"), "daily-*").Length);
    }

    [Fact]
    public async Task Backup_ForeignKeyFailure_KeepsPreviousSuccessfulBackupAndCleansCandidate()
    {
        await Initialize();
        using (var connection = _factory.OpenConnection()) Execute(connection, "UPDATE storage_meta SET data_revision=1;");
        var backups = new SqliteDatabaseBackup(_factory, _clock);
        var successful = await backups.CreateDailyBackupAsync();
        _clock.AdvanceDay();
        using (var connection = _factory.OpenConnection())
            Execute(connection, "PRAGMA foreign_keys=OFF; INSERT INTO session_ratings VALUES ('orphan', 2, NULL, 'utc', 'utc', 1); UPDATE storage_meta SET data_revision=2;");
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.CreateDailyBackupAsync());
        Assert.True(File.Exists(Path.Combine(successful!, "userdata.db")));
        Assert.Single(Directory.GetDirectories(Path.Combine(_directory, "backups")));
    }

    [Fact]
    public async Task HistoryKeys_FirstAppearanceOrder_SurvivesVacuumAndDoesNotDependOnSpelling()
    {
        await Initialize();
        using var connection = _factory.OpenConnection();
        Execute(connection, "INSERT INTO history_keys VALUES ('z', 1), ('A', 2); VACUUM;");
        Assert.Equal("z,A", Scalar(connection, "SELECT group_concat(data_key, ',') FROM (SELECT data_key FROM history_keys ORDER BY first_sequence);"));
        AssertConstraint(connection, "INSERT INTO history_keys VALUES ('another', 1);");
    }

    [Fact]
    public async Task Initialize_ForeignKeyCorruption_RefusesToTreatDatabaseAsHealthy()
    {
        await Initialize();
        using (var connection = _factory.OpenConnection())
            Execute(connection, "PRAGMA foreign_keys=OFF; INSERT INTO session_ratings VALUES ('missing', 3, NULL, 'utc', 'utc', 1);");
        await Assert.ThrowsAsync<InvalidDataException>(Initialize);
    }

    private const string SessionInsert = "INSERT INTO play_sessions VALUES ('session', 'éTOILE', 1, 'start', 'end', 0, 0, 10, 1, 'unknown', 'legacy', 'utc', NULL, NULL);";
    private Task Initialize() => new SqliteDatabaseInitializer(_factory, _clock).InitializeAsync();
    private static object Scalar(SqliteConnection connection, string sql) => SqliteConnectionFactory.Scalar(connection, sql)!;
    private static void Execute(SqliteConnection connection, string sql) => SqliteConnectionFactory.Execute(connection, sql);
    private static void AssertConstraint(SqliteConnection connection, string sql) =>
        Assert.Equal(19, Assert.Throws<SqliteException>(() => Execute(connection, sql)).SqliteErrorCode);

    private static string[] ReadMigrations()
    {
        var assembly = typeof(SqliteDatabaseInitializer).Assembly;
        return assembly.GetManifestResourceNames().Where(name => name.Contains(".Migrations.") && name.EndsWith(".sql"))
            .OrderBy(name => name, StringComparer.Ordinal).Select(name =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                return reader.ReadToEnd();
            }).ToArray();
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void AdvanceDay() => _now = _now.AddDays(1);
    }
}
