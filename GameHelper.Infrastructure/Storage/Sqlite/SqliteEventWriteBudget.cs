using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>One event's monotonic contention deadline, independent of its injected gameplay clock.</summary>
internal sealed class SqliteEventWriteBudget(DateTime eventTime)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    internal DateTime EventTime { get; } = eventTime;

    internal SqliteTransaction BeginWrite(SqliteConnection connection)
    {
        // A deferred BEGIN itself does not wait for a writer. Acquire the write intent through a
        // single native step: the provider otherwise retries BUSY with a new whole-second timeout.
        // This fixed no-op leaves data/revisions unchanged, but obtains the same writer lock as BEGIN IMMEDIATE.
        var transaction = connection.BeginTransaction(deferred: true);
        try
        {
            // Reserve 100 ms for callback/rollback overhead. Zero remaining time still permits one nonblocking attempt.
            var remaining = Math.Max(0, SqliteConnectionFactory.TimeoutSeconds * 1000 - 100 - (int)Stopwatch.GetElapsedTime(_started).TotalMilliseconds);
            raw.sqlite3_busy_timeout(connection.Handle, remaining);
            var result = raw.sqlite3_exec(connection.Handle, "UPDATE storage_meta SET singleton=singleton WHERE singleton=1;");
            SqliteException.ThrowExceptionForRC(result, connection.Handle);
            raw.sqlite3_busy_timeout(connection.Handle, 0);
            return transaction;
        }
        catch { transaction.Dispose(); throw; }
    }
}
