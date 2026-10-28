using Microsoft.Data.Sqlite;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>Serializes initialization and upgrades across processes in the same data directory.</summary>
public sealed class SqliteDatabaseInitializer
{
    /// <summary>Highest embedded numbered SQL migration supported by this build.</summary>
    public const int CurrentVersion = 3;
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<string> _migrations;

    /// <summary>Creates an initializer; the clock is also used for upgrade backup metadata.</summary>
    public SqliteDatabaseInitializer(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
        : this(factory, timeProvider ?? TimeProvider.System, LoadMigrations()) { }

    internal SqliteDatabaseInitializer(SqliteConnectionFactory factory, TimeProvider timeProvider, IReadOnlyList<string> migrations)
    {
        _factory = factory;
        _timeProvider = timeProvider;
        _migrations = migrations;
    }

    /// <summary>Checks health and atomically applies all pending versions. Failure leaves the previous schema intact.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await SqliteDirectoryLock.AcquireAsync(_factory.DataDirectory, cancellationToken).ConfigureAwait(false);
        InitializeUnderLock(_factory.DatabasePath, cancellationToken);
    }

    internal void InitializeUnderLock(string path, CancellationToken cancellationToken, Action<string>? checkpoint = null)
    {
        using var connection = SqliteConnectionFactory.OpenFile(path, SqliteOpenMode.ReadWriteCreate);
        var version = SqliteConnectionFactory.ReadVersion(connection);
        SqliteConnectionFactory.EnsureSupported(version);
        SqliteDatabaseHealth.Check(connection, full: version < CurrentVersion);
        if (version > 0 && version < CurrentVersion)
            SqliteDatabaseBackup.CreateUnderLock(_factory, connection, "upgrade", _timeProvider);

        var journal = SqliteConnectionFactory.Scalar(connection, "PRAGMA journal_mode=WAL;")?.ToString();
        if (!string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase))
            throw new IOException("SQLite could not enable WAL mode.");
        if (version == CurrentVersion) return;

        using var transaction = connection.BeginTransaction(deferred: false);
        for (var next = version + 1; next <= CurrentVersion; next++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SqliteConnectionFactory.Execute(connection, _migrations[next - 1], transaction);
            if (next == 1)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO storage_meta VALUES (1, $id, 0, 0, 'legacy');";
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                insert.ExecuteNonQuery();
            }
            SqliteConnectionFactory.Execute(connection, $"PRAGMA user_version={next};", transaction);
        }
        SqliteDatabaseHealth.Check(connection, full: true, transaction);
        checkpoint?.Invoke("candidate.schema.commit.before");
        transaction.Commit();
        checkpoint?.Invoke("candidate.schema.commit.after");
    }

    private static string[] LoadMigrations()
    {
        var assembly = typeof(SqliteDatabaseInitializer).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Storage.Sqlite.Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (names.Length != CurrentVersion) throw new InvalidOperationException("Missing embedded SQLite migrations.");
        return names.Select((name, index) =>
        {
            if (!name.Contains($".Migrations.{index + 1:000}_", StringComparison.Ordinal))
                throw new InvalidOperationException("SQLite migration numbers must be contiguous.");
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }).ToArray();
    }
}
