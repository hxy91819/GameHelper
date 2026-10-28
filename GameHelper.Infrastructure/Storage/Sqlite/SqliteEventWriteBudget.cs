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
        // nonblocking native attempts: the provider otherwise retries BUSY with a new whole-second timeout.
        // This fixed no-op leaves data/revisions unchanged, but obtains the same writer lock as BEGIN IMMEDIATE.
        var transaction = connection.BeginTransaction(deferred: true);
        try
        {
            // SQLite's built-in busy handler counts requested sleeps, which can overrun on Windows.
            // Disable it and check our absolute deadline after each bounded backoff instead.
            raw.sqlite3_busy_timeout(connection.Handle, 0);
            while (true)
            {
                var result = raw.sqlite3_exec(connection.Handle, "UPDATE storage_meta SET singleton=singleton WHERE singleton=1;");
                var remaining = Math.Max(0, SqliteConnectionFactory.TimeoutSeconds * 1000 - 100 - (int)Stopwatch.GetElapsedTime(_started).TotalMilliseconds);
                if (((result & 0xff) is raw.SQLITE_BUSY or raw.SQLITE_LOCKED) && remaining > 0)
                {
                    Thread.Sleep(Math.Min(10, remaining));
                    continue;
                }
                SqliteException.ThrowExceptionForRC(result, connection.Handle);
                break;
            }
            return transaction;
        }
        catch { transaction.Dispose(); throw; }
    }
}
