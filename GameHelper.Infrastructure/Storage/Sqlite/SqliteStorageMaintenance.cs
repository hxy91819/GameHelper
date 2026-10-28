using Microsoft.Data.Sqlite;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>Lock-external health, passive checkpoint and verified daily backup. Failed work is retried next tick.</summary>
public sealed class SqliteStorageMaintenance(SqliteConnectionFactory factory, TimeProvider clock)
{
    internal bool Completed { get; private set; }

    /// <summary>Returns a backup path when published; null also means unchanged or an occupied maintenance slot.</summary>
    public async Task<string?> RunAsync(CancellationToken cancellationToken = default)
    {
        Completed = false;
        using var admission = SqliteMaintenanceAdmission.TryEnterMaintenance(factory.DataDirectory);
        if (admission is null) return null;
        using var lease = await SqliteDirectoryLock.AcquireAsync(factory.DataDirectory, cancellationToken).ConfigureAwait(false);
        try
        {
            var backup = RunUnderLock(cancellationToken);
            Completed = true;
            return backup;
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is SqliteException { SqliteErrorCode: 11 or 26 })
        {
            factory.Quarantine(ex);
            throw;
        }
    }

    private string? RunUnderLock(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = factory.OpenConnection();
        var day = clock.GetUtcNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var last = SqliteConnectionFactory.Scalar(connection, "SELECT value FROM migration_state WHERE name='maintenance.health.utc';") as string;
        if (last != day)
        {
            SqliteDatabaseHealth.Check(connection, full: true);
            using var tx = new SqliteEventWriteBudget(default).BeginWrite(connection);
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT OR REPLACE INTO migration_state VALUES('maintenance.health.utc',$day);";
            command.Parameters.AddWithValue("$day", day);
            command.ExecuteNonQuery();
            tx.Commit();
        }
        // PASSIVE never waits for pinned readers; later ticks finish after they release the WAL.
        SqliteConnectionFactory.Execute(connection, "PRAGMA wal_checkpoint(PASSIVE);");
        return new SqliteDatabaseBackup(factory, clock).CreateDailyUnderLock(connection);
    }
}
