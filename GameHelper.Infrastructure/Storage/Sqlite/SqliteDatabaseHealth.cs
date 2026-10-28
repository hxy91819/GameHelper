using Microsoft.Data.Sqlite;

namespace GameHelper.Infrastructure.Storage.Sqlite;

internal static class SqliteDatabaseHealth
{
    internal static void Check(SqliteConnection connection, bool full, SqliteTransaction? transaction = null)
    {
        try { CheckCore(connection, full, transaction); }
        catch (InvalidDataException) when (!full)
        {
            // Escalate quick-check failures for full diagnostics; never accept a failed quick check.
            CheckCore(connection, full: true, transaction);
            throw;
        }
    }

    private static void CheckCore(SqliteConnection connection, bool full, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = full ? "PRAGMA integrity_check;" : "PRAGMA quick_check;";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
                throw new InvalidDataException("SQLite integrity check failed; preserve the database and WAL/SHM for recovery.");
        }
        command.CommandText = "PRAGMA foreign_key_check;";
        using var foreignKeys = command.ExecuteReader();
        if (foreignKeys.Read()) throw new InvalidDataException("SQLite foreign key check failed; writes refused.");
    }

    internal static void Checkpoint(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 0 || reader.GetInt32(1) != reader.GetInt32(2))
            throw new IOException("SQLite checkpoint is busy; database cannot be published.");
    }
}
