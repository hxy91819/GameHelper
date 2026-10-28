using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>Verified local backup primitives. Scheduling and production activation belong to later ADR batches.</summary>
public sealed class SqliteDatabaseBackup
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Uses an injectable clock for backup dates, metadata and retention.</summary>
    public SqliteDatabaseBackup(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Creates a daily backup only after data changes, at most once per UTC date. No scheduler is installed.</summary>
    public async Task<string?> CreateDailyBackupAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await SqliteDirectoryLock.AcquireAsync(_factory.DataDirectory, cancellationToken).ConfigureAwait(false);
        using var source = _factory.OpenConnection();
        var current = ReadMetadata(source, "daily", _timeProvider.GetUtcNow(), 0);
        var previous = ReadBackups(_factory.DataDirectory, "daily").FirstOrDefault();
        if (current.DataRevision == 0 && previous.Manifest is null) return null;
        if (previous.Manifest is { } prior && prior.DatabaseId == current.DatabaseId &&
            (prior.CreatedAtUtc.UtcDateTime.Date == current.CreatedAtUtc.UtcDateTime.Date || prior.DataRevision == current.DataRevision))
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        return CreateUnderLock(_factory, source, "daily", _timeProvider);
    }

    /// <summary>
    /// Restores a verified backup offline. All database users must be stopped first.
    /// Preserves the old DB/WAL/SHM together and changes database_id to invalidate sync bookmarks.
    /// </summary>
    public async Task RestoreAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        using var lease = await SqliteDirectoryLock.AcquireAsync(_factory.DataDirectory, cancellationToken).ConfigureAwait(false);
        var manifest = ReadManifest(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, "userdata.db");
        if (Hash(backupPath) != manifest.Sha256) throw new InvalidDataException("Backup SHA-256 mismatch.");
        var candidate = Path.Combine(_factory.DataDirectory, $"restore-{Guid.NewGuid():N}.db");
        try
        {
            using (var source = SqliteConnectionFactory.OpenFile(backupPath, SqliteOpenMode.ReadOnly))
            {
                SqliteConnectionFactory.EnsureSupported(SqliteConnectionFactory.ReadVersion(source));
                SqliteDatabaseHealth.Check(source, full: true);
                VerifyMetadata(source, manifest);
                using var destination = SqliteConnectionFactory.OpenFile(candidate, SqliteOpenMode.ReadWriteCreate);
                source.BackupDatabase(destination);
            }
            using (var destination = SqliteConnectionFactory.OpenFile(candidate, SqliteOpenMode.ReadWrite))
            {
                using var update = destination.CreateCommand();
                update.CommandText = "UPDATE storage_meta SET database_id=$id WHERE singleton=1;";
                update.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("Backup has no storage identity.");
                SqliteDatabaseHealth.Check(destination, full: true);
                SqliteDatabaseHealth.Checkpoint(destination);
                SqliteConnectionFactory.Execute(destination, "PRAGMA journal_mode=DELETE;");
            }
            cancellationToken.ThrowIfCancellationRequested();
            PublishRestore(candidate);
        }
        finally { DeleteCandidate(candidate); }
    }

    internal static string CreateUnderLock(SqliteConnectionFactory factory, SqliteConnection source, string kind, TimeProvider clock)
    {
        var elapsed = Stopwatch.StartNew();
        var root = Path.Combine(factory.DataDirectory, "backups");
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, $".pending-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            var database = Path.Combine(temporary, "userdata.db");
            BackupManifest manifest;
            using (var destination = SqliteConnectionFactory.OpenFile(database, SqliteOpenMode.ReadWriteCreate))
            {
                source.BackupDatabase(destination);
                SqliteConnectionFactory.EnsureSupported(SqliteConnectionFactory.ReadVersion(destination));
                SqliteDatabaseHealth.Check(destination, full: true);
                SqliteDatabaseHealth.Checkpoint(destination);
                SqliteConnectionFactory.Execute(destination, "PRAGMA journal_mode=DELETE;");
                manifest = ReadMetadata(destination, kind, clock.GetUtcNow(), elapsed.ElapsedMilliseconds);
            }
            // The injected clock may repeat or move backwards; retention follows publication order.
            var sequence = ReadBackups(factory.DataDirectory, kind).Select(item => item.Manifest.BackupSequence).DefaultIfEmpty(0).Max() + 1;
            manifest = manifest with { Sha256 = Hash(database), BackupSequence = sequence };
            File.WriteAllText(Path.Combine(temporary, "manifest.json"), JsonSerializer.Serialize(manifest));
            var published = Path.Combine(root, $"{kind}-{manifest.CreatedAtUtc:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}");
            Directory.Move(temporary, published);
            // Rotate only after a fully verified backup and manifest have been atomically published.
            foreach (var old in ReadBackups(factory.DataDirectory, kind).Skip(kind == "daily" ? 7 : 3))
                Directory.Delete(old.Directory, recursive: true);
            return published;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    private void PublishRestore(string candidate)
    {
        var retained = Path.Combine(_factory.DataDirectory, "recovery-originals", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(retained);
        var moved = new List<(string Original, string Retained)>();
        try
        {
            // Offline-only: no writer may recreate companions between these moves and the final rename.
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var original = _factory.DatabasePath + suffix;
                if (!File.Exists(original)) continue;
                var target = Path.Combine(retained, "userdata.db" + suffix);
                File.Move(original, target);
                moved.Add((original, target));
            }
            if (File.Exists(_factory.DatabasePath))
                File.Replace(candidate, _factory.DatabasePath, Path.Combine(retained, "userdata.db"));
            else
                File.Move(candidate, _factory.DatabasePath);
        }
        catch
        {
            foreach (var item in moved.AsEnumerable().Reverse()) File.Move(item.Retained, item.Original);
            throw;
        }
    }

    private static BackupManifest ReadMetadata(SqliteConnection connection, string kind, DateTimeOffset createdAt, long elapsed)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT database_id, history_revision, data_revision FROM storage_meta WHERE singleton=1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Missing storage metadata.");
        var id = reader.GetString(0);
        var history = reader.GetInt64(1);
        var data = reader.GetInt64(2);
        reader.Close();
        return new BackupManifest(kind, id, SqliteConnectionFactory.ReadVersion(connection), history, data, createdAt, elapsed, "", 0);
    }

    private static void VerifyMetadata(SqliteConnection source, BackupManifest expected)
    {
        var actual = ReadMetadata(source, expected.Kind, expected.CreatedAtUtc, expected.DurationMilliseconds);
        if (actual.DatabaseId != expected.DatabaseId || actual.SchemaVersion != expected.SchemaVersion ||
            actual.HistoryRevision != expected.HistoryRevision || actual.DataRevision != expected.DataRevision)
            throw new InvalidDataException("Backup metadata mismatch.");
    }

    private static IEnumerable<(string Directory, BackupManifest Manifest)> ReadBackups(string directory, string kind)
    {
        var root = Path.Combine(directory, "backups");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root, kind + "-*")
            .Select(path => (Directory: path, Manifest: ReadManifest(path)))
            .OrderByDescending(item => item.Manifest.BackupSequence)
            .ThenByDescending(item => item.Manifest.CreatedAtUtc).ToArray();
    }

    private static BackupManifest ReadManifest(string directory) =>
        JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")))
        ?? throw new InvalidDataException("Missing backup manifest.");

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static void DeleteCandidate(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }

    private sealed record BackupManifest(string Kind, string DatabaseId, int SchemaVersion, long HistoryRevision,
        long DataRevision, DateTimeOffset CreatedAtUtc, long DurationMilliseconds, string Sha256, long BackupSequence);
}
