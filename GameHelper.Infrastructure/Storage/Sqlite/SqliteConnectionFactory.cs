using GameHelper.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>Creates private, short-lived connections shared by session persistence and offline maintenance.</summary>
public sealed class SqliteConnectionFactory
{
    /// <summary>The versioned comparison contract; never replace it with SQLite NOCASE.</summary>
    public const string CollationName = "GH_ORDINAL_IGNORE_CASE";
    /// <summary>SQLite and provider lock waits are both bounded to five seconds.</summary>
    public const int TimeoutSeconds = 5;

    /// <summary>Uses AppDataPath verbatim unless an explicit database directory is supplied.</summary>
    public SqliteConnectionFactory(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? AppDataPath.GetGameHelperDirectory());
        DatabasePath = Path.Combine(DataDirectory, "userdata.db");
    }

    /// <summary>Resolved directory, including the GameHelper suffix for the default path.</summary>
    public string DataDirectory { get; }
    /// <summary>Main file; its WAL/SHM companions must not be moved independently while open.</summary>
    public string DatabasePath { get; }

    /// <summary>Opens an initialized database, rejecting unsupported versions before returning a writer.</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = OpenFile(DatabasePath, SqliteOpenMode.ReadWrite);
        try
        {
            var version = ReadVersion(connection);
            EnsureSupported(version);
            if (version != SqliteDatabaseInitializer.CurrentVersion)
                throw new InvalidOperationException("SQLite schema is not initialized; run the shared initializer first.");
            if (!string.Equals(Scalar(connection, "PRAGMA journal_mode;")?.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite database is not in WAL mode.");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal static SqliteConnection OpenFile(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Cache = SqliteCacheMode.Private,
            Pooling = false, DefaultTimeout = TimeoutSeconds
        }.ToString());
        try
        {
            connection.Open();
            connection.CreateCollation(CollationName, StringComparer.OrdinalIgnoreCase.Compare);
            // Apply the same policy to maintenance/candidate/read-only connections as to business connections.
            // Version is checked first so opening a future database never applies writable configuration.
            EnsureSupported(ReadVersion(connection));
            Configure(connection);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal static void Configure(SqliteConnection connection) => Execute(connection,
        "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");

    internal static int ReadVersion(SqliteConnection connection) => Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));

    internal static void EnsureSupported(int version)
    {
        if (version < 0 || version > SqliteDatabaseInitializer.CurrentVersion)
            throw new NotSupportedException($"SQLite schema {version} exceeds supported range 0..{SqliteDatabaseInitializer.CurrentVersion}; writes refused.");
    }

    internal static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
