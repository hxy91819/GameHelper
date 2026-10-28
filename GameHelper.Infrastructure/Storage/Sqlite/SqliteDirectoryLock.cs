using System.Diagnostics;

namespace GameHelper.Infrastructure.Storage.Sqlite;

// The directory file lease is acquired before any SQLite transaction. Never acquire it inside one.
// Keep the file permanently: deleting a released lock file can split concurrent waiters across inodes.
internal static class SqliteDirectoryLock
{
    internal static async Task<FileStream> AcquireAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(directory, ".userdata.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(SqliteConnectionFactory.TimeoutSeconds))
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
