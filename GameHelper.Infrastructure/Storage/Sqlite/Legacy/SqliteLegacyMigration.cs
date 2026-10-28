using GameHelper.Core.Models;
using Microsoft.Data.Sqlite;
using static GameHelper.Infrastructure.Storage.Sqlite.Legacy.LegacySql;

namespace GameHelper.Infrastructure.Storage.Sqlite.Legacy;

/// <summary>
/// Candidate import primitives used by first activation and offline maintenance. Callers must exclude old writers.
/// Prepare never activates a candidate or changes legacy files.
/// </summary>
public sealed class SqliteLegacyMigration
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyList<GameConfig> _configs;
    private readonly Action<string>? _checkpoint;
    private string Candidate => _factory.DatabasePath + ".importing";
    private string Pointer => Path.Combine(_factory.DataDirectory, "migration-candidate.json");

    /// <summary>The injected clock and configuration are used for old/new statistics validation.</summary>
    public SqliteLegacyMigration(SqliteConnectionFactory factory, TimeProvider clock, IReadOnlyList<GameConfig>? configs = null, Action<string>? checkpoint = null)
    { _factory = factory; _clock = clock; _configs = configs ?? []; _checkpoint = checkpoint; }

    /// <summary>Inventories only the supplied configs. Changed sources invalidate the candidate, retaining originals.</summary>
    public async Task<CandidateImport> PrepareAsync(IEnumerable<string> configPaths, int batchSize = 500, CancellationToken cancellationToken = default, bool sessionsOnly = false)
    {
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize));
        using var lease = await SqliteDirectoryLock.AcquireAsync(_factory.DataDirectory, cancellationToken).ConfigureAwait(false);
        if (File.Exists(_factory.DatabasePath)) throw new InvalidOperationException("A main database already exists; candidate import cannot replace shared history.");
        var inventory = Inventory(configPaths);
        string manifestPath;
        ImportManifest manifest;
        if (File.Exists(Pointer) && File.Exists(manifestPath = ReadJson<string>(Pointer)) &&
            SameSources(manifest = ReadJson<ImportManifest>(manifestPath), inventory) && manifest.SessionsOnly == sessionsOnly) { }
        else
        {
            (manifest, manifestPath) = Capture(inventory, sessionsOnly);
            Hit("manifest.publish.before");
            WriteJson(Pointer, manifestPath);
            Hit("manifest.publish.after");
        }
        var backupDirectory = Path.GetDirectoryName(manifestPath)!;
        if (File.Exists(Candidate))
        {
            using var existing = SqliteConnectionFactory.OpenFile(Candidate, SqliteOpenMode.ReadWrite);
            var table = Scalar(existing, null, "SELECT name FROM sqlite_schema WHERE name='migration_state';");
            var generation = table is null ? null : Scalar(existing, null, "SELECT value FROM migration_state WHERE name='candidate';") as string;
            existing.Close();
            if (generation != manifest.MigrationId) RetainInvalidCandidate();
        }
        new SqliteDatabaseInitializer(_factory, _clock).InitializeUnderLock(Candidate, cancellationToken, _checkpoint);
        using var connection = SqliteConnectionFactory.OpenFile(Candidate, SqliteOpenMode.ReadWrite);
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            Execute(connection, tx, "INSERT OR IGNORE INTO migration_state VALUES('candidate',$0);", manifest.MigrationId);
            Commit(tx, "candidate.identity");
        }
        var decoded = Decode(manifest, backupDirectory);
        foreach (var source in manifest.Sources)
        {
            if (manifest.SessionsOnly && source.ConfigPath is not null) continue;
            var items = decoded[source.SourceId];
            using (var tx = connection.BeginTransaction(deferred: false))
            {
                var scope = LegacyRelationalWriter.Scope(connection, tx, source.ConfigPath);
                LegacyRelationalWriter.Source(connection, tx, source, scope);
                Commit(tx, "source.register:" + source.Kind);
            }
            foreach (var batch in items.Chunk(batchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var tx = connection.BeginTransaction(deferred: false);
                var scope = LegacyRelationalWriter.Scope(connection, tx, source.ConfigPath);
                foreach (var item in batch) LegacyRelationalWriter.Item(connection, tx, source, scope, item, manifest.CreatedAtUtc);
                Commit(tx, "source.batch:" + source.Kind);
            }
            using (var tx = connection.BeginTransaction(deferred: false))
            {
                Execute(connection, tx, "UPDATE import_sources SET status=$1,validation_summary=$2 WHERE source_id=$0;", source.SourceId,
                    !source.Exists ? "missing" : source.Kind == "json" && manifest.Sources.Any(s => s.Kind == "csv" && s.Exists) ? "unused" : "complete",
                    $"accepted={items.Count(i => i.Accepted)}; skipped={items.Count(i => !i.Accepted)}");
                Commit(tx, "source.complete:" + source.Kind);
            }
        }
        EnsureSourcesUnchanged(manifest);
        LegacyImportValidation.Validate(connection, manifest, backupDirectory, decoded, _clock, _configs);
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            if (Scalar(connection, tx, "SELECT 1 FROM migration_state WHERE name='validated';") is null)
                Execute(connection, tx, """
                    UPDATE storage_meta SET history_revision=CASE WHEN EXISTS(SELECT 1 FROM play_sessions) THEN 1 ELSE 0 END,
                        data_revision=CASE WHEN EXISTS(SELECT 1 FROM import_records WHERE disposition='accepted') THEN 1 ELSE 0 END;
                    """);
            Execute(connection, tx, "INSERT OR REPLACE INTO migration_state VALUES('validated',$0);", manifest.MigrationId);
            Commit(tx, "candidate.validated");
        }
        return new(Candidate, manifestPath, decoded.Values.Sum(items => items.Count(i => i.Accepted)), decoded.Values.Sum(items => items.Count(i => !i.Accepted)));
    }

    /// <summary>Activates only after validation; the startup coordinator must exclude legacy writers.</summary>
    public async Task ActivateAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        using var lease = await SqliteDirectoryLock.AcquireAsync(_factory.DataDirectory, cancellationToken).ConfigureAwait(false);
        var manifest = ReadJson<ImportManifest>(manifestPath);
        if (File.Exists(_factory.DatabasePath))
        {
            using var active = _factory.OpenConnection();
            if (Scalar(active, null, "SELECT value FROM migration_state WHERE name='activated';") as string != manifest.MigrationId)
                throw new InvalidOperationException("A different main database exists.");
            Diagnostic(manifest);
            return;
        }
        EnsureSourcesUnchanged(manifest);
        using (var candidate = SqliteConnectionFactory.OpenFile(Candidate, SqliteOpenMode.ReadWrite))
        {
            if (Scalar(candidate, null, "SELECT value FROM migration_state WHERE name='validated';") as string != manifest.MigrationId)
                throw new InvalidDataException("Candidate has not passed validation for this manifest.");
            LegacyImportValidation.Validate(candidate, manifest, Path.GetDirectoryName(manifestPath)!, Decode(manifest, Path.GetDirectoryName(manifestPath)!), _clock, _configs);
            using (var tx = candidate.BeginTransaction(deferred: false))
            {
                Execute(candidate, tx, "INSERT OR REPLACE INTO migration_state VALUES('activated',$0); UPDATE storage_meta SET compatibility_mode='sqlite';", manifest.MigrationId);
                Commit(tx, "activation.mark");
            }
            SqliteDatabaseHealth.Check(candidate, full: true);
            SqliteDatabaseHealth.Checkpoint(candidate);
        }
        SqliteConnection.ClearAllPools();
        if (File.Exists(Candidate + "-wal") && new FileInfo(Candidate + "-wal").Length > 0) throw new IOException("Candidate still has WAL frames.");
        Hit("activation.rename.before");
        File.Move(Candidate, _factory.DatabasePath); // No overwrite: never replace an active database.
        Hit("activation.rename.after");
        Diagnostic(manifest);
    }

    /// <summary>Imports only a previously unknown config scope into an existing database, in one transaction.</summary>
    public async Task ImportScopeAsync(string configPath, CancellationToken cancellationToken = default)
    {
        using var lease = await SqliteDirectoryLock.AcquireAsync(_factory.DataDirectory, cancellationToken).ConfigureAwait(false);
        var config = Path.GetFullPath(configPath);
        using var connection = _factory.OpenConnection();
        if (Scalar(connection, null, "SELECT 1 FROM config_scopes WHERE config_path=$0;", config) is not null) return;
        var (manifest, manifestPath) = Capture(Inventory([config]).Where(s => s.ConfigPath is not null).ToList());
        var decoded = Decode(manifest, Path.GetDirectoryName(manifestPath)!);
        EnsureSourcesUnchanged(manifest);
        var expectedSides = manifest.Sources.Where(s => s.Exists).ToDictionary(s => s.SourceId,
            s => LegacyImportValidation.ExpectedSide(s.Kind, decoded[s.SourceId]));
        using var tx = new SqliteEventWriteBudget(default).BeginWrite(connection);
        using var deadline = new SqliteWriteDeadline(connection);
        void Deadline() => deadline.Check();
        var scope = LegacyRelationalWriter.Scope(connection, tx, config);
        foreach (var source in manifest.Sources)
        {
            LegacyRelationalWriter.Source(connection, tx, source, scope);
            foreach (var item in decoded[source.SourceId]) LegacyRelationalWriter.Item(connection, tx, source, scope, item, manifest.CreatedAtUtc, Deadline);
            Execute(connection, tx, "UPDATE import_sources SET status=$1 WHERE source_id=$0;", source.SourceId, source.Exists ? "complete" : "missing");
            Deadline();
            if (source.Exists) LegacyImportValidation.EqualBytes(expectedSides[source.SourceId],
                LegacyDatabaseReader.Side(connection, tx, scope!.Value, source.Kind, Deadline), source.Path);
            Deadline();
        }
        Execute(connection, tx, "UPDATE storage_meta SET data_revision=data_revision+1; UPDATE compatibility_exports SET dirty=1;");
        foreach (var kind in LegacyDecoder.SideKinds)
            Execute(connection, tx, """
                INSERT INTO compatibility_exports
                SELECT $0,data_revision,0,'','',1 FROM storage_meta WHERE singleton=1
                ON CONFLICT(target) DO UPDATE SET dirty=1;
                """, config + ".desktop-" + kind + ".json");
        Deadline();
        Commit(tx, "scope.complete");
    }

    private List<ImportSource> Inventory(IEnumerable<string> configs)
    {
        var sources = new List<ImportSource>();
        void Add(string path, string? config, string kind) => sources.Add(new("", path, config, kind, false, "", 0, ""));
        Add(Path.Combine(_factory.DataDirectory, "playtime.csv"), null, "csv");
        Add(Path.Combine(_factory.DataDirectory, "playtime.json"), null, "json");
        Add(Path.Combine(_factory.DataDirectory, "inflight-sessions.json"), null, "inflight");
        foreach (var config in configs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            foreach (var kind in LegacyDecoder.SideKinds) Add(config + ".desktop-" + kind + ".json", config, kind);
        return sources;
    }

    private (ImportManifest Manifest, string Path) Capture(List<ImportSource> inventory, bool sessionsOnly = false)
    {
        var id = Guid.NewGuid().ToString("D");
        var root = Path.Combine(_factory.DataDirectory, "migration-backups");
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, ".pending-" + id);
        var published = Path.Combine(root, id);
        Directory.CreateDirectory(temporary);
        try
        {
            var sources = new List<ImportSource>();
            foreach (var source in inventory)
            {
                var bytes = ReadSource(source.Path);
                var name = sources.Count.ToString("D4") + ".original";
                if (bytes is not null) File.WriteAllBytes(Path.Combine(temporary, name), bytes);
                sources.Add(source with { SourceId = Id(id, sources.Count), Exists = bytes is not null,
                    Sha256 = bytes is null ? "" : Hash(bytes), SizeBytes = bytes?.LongLength ?? 0, BackupName = name });
            }
            var manifest = new ImportManifest(id, 1, typeof(SqliteLegacyMigration).Assembly.GetName().Version!.ToString(), _clock.GetUtcNow(), sources, sessionsOnly);
            Hit("backup.manifest.before");
            WriteJson(Path.Combine(temporary, "manifest.json"), manifest);
            Hit("backup.manifest.after");
            EnsureSourcesUnchanged(manifest);
            Hit("backup.rename.before");
            Directory.Move(temporary, published);
            Hit("backup.rename.after");
            return (manifest, Path.Combine(published, "manifest.json"));
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    internal static Dictionary<string, List<LegacyItem>> Decode(ImportManifest manifest, string directory)
    {
        var result = new Dictionary<string, List<LegacyItem>>();
        foreach (var source in manifest.Sources)
        {
            try { result[source.SourceId] = LegacyDecoder.Decode(source, directory, manifest.Sources.Any(s => s.Kind == "csv" && s.Exists)); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or ArgumentException or IOException)
            {
                WriteJson(Path.Combine(directory, "errors", Guid.NewGuid().ToString("N") + ".json"),
                    new { source.SourceId, source.Path, Error = ex.Message, Type = ex.GetType().FullName });
                throw;
            }
        }
        return result;
    }
    internal static byte[]? ReadSource(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    internal static void EnsureSourcesUnchanged(ImportManifest manifest)
    {
        foreach (var source in manifest.Sources)
        {
            var bytes = ReadSource(source.Path);
            if (source.Exists != (bytes is not null) || (bytes is not null && (Hash(bytes) != source.Sha256 || bytes.LongLength != source.SizeBytes)))
                throw new InvalidDataException("Legacy source changed; prepare a new candidate: " + source.Path);
        }
    }
    private static bool SameSources(ImportManifest manifest, List<ImportSource> inventory)
    {
        if (manifest.Sources.Count != inventory.Count) return false;
        for (var i = 0; i < inventory.Count; i++)
            if (!StringComparer.OrdinalIgnoreCase.Equals(manifest.Sources[i].Path, inventory[i].Path)) return false;
        try { EnsureSourcesUnchanged(manifest); return true; }
        catch (InvalidDataException) { return false; }
    }
    private void RetainInvalidCandidate()
    {
        var directory = Path.Combine(_factory.DataDirectory, "migration-backups", "invalidated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var suffix in new[] { "-wal", "-shm", "" })
            if (File.Exists(Candidate + suffix))
            {
                Hit("candidate.invalidate.rename.before:" + suffix);
                File.Move(Candidate + suffix, Path.Combine(directory, "userdata.db" + suffix));
                Hit("candidate.invalidate.rename.after:" + suffix);
            }
    }
    private void Diagnostic(ImportManifest manifest)
    {
        Hit("activation.diagnostic.before");
        WriteJson(Path.Combine(_factory.DataDirectory, "migration-complete.json"), new { manifest.MigrationId });
        Hit("activation.diagnostic.after");
    }
    private void Commit(SqliteTransaction tx, string name) { Hit(name + ".commit.before"); tx.Commit(); Hit(name + ".commit.after"); }
    private void Hit(string name) => _checkpoint?.Invoke(name);
}
