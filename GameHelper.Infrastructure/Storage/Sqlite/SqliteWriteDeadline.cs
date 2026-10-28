using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>Cooperative Desktop writer deadline, including long single SQLite statements.</summary>
internal sealed class SqliteWriteDeadline : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly long _started = Stopwatch.GetTimestamp();
    internal SqliteWriteDeadline(SqliteConnection connection)
    {
        _connection = connection;
        raw.sqlite3_progress_handler(connection.Handle, 1000, state => ((SqliteWriteDeadline)state).Expired ? 1 : 0, this);
    }
    private bool Expired => Stopwatch.GetElapsedTime(_started) > TimeSpan.FromMilliseconds(250);
    internal void Check()
    {
        if (Expired) throw new InvalidOperationException("保存事务超时，请重试；编辑与导入原件已保留。");
    }
    // Clear before the transaction is disposed, so rollback can always finish.
    public void Dispose() => raw.sqlite3_progress_handler(_connection.Handle, 0, null, null);
}
