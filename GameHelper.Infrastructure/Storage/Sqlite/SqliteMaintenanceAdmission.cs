namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>Cross-process admission: maintenance starts only between business writes; writers keep their event deadline.</summary>
internal static class SqliteMaintenanceAdmission
{
    internal static FileStream? TryEnterMaintenance(string directory)
    {
        try { return Open(directory, FileShare.None); }
        catch (IOException) { return null; }
    }

    internal static FileStream EnterWrite(string directory, Func<TimeSpan> remaining)
    {
        while (true)
        {
            try { return Open(directory, FileShare.ReadWrite); }
            catch (IOException) when (remaining() > TimeSpan.Zero) { Thread.Sleep(10); }
        }
    }

    private static FileStream Open(string directory, FileShare share) => new(
        Path.Combine(directory, ".userdata.maintenance"), FileMode.OpenOrCreate, FileAccess.ReadWrite, share);
}
