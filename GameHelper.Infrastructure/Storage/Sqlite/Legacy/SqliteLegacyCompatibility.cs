using System.Text.Json;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;
using Microsoft.Data.Sqlite;
using static GameHelper.Infrastructure.Storage.Sqlite.Legacy.LegacySql;

namespace GameHelper.Infrastructure.Storage.Sqlite.Legacy;

/// <summary>A fixed-revision set of legacy projections. Null contents mean the absent in-flight snapshot.</summary>
public sealed record LegacyProjection(long DataRevision, long SnapshotRevision, IReadOnlyDictionary<string, byte[]?> Files);
/// <summary>Downgrade baseline identity for one occurrence, including legitimate duplicate rows.</summary>
public sealed record DowngradeSession(string SessionId, string Key, DateTime Start, DateTime End, long Minutes);
/// <summary>Published only after the database archive and all legacy files have been verified.</summary>
public sealed record DowngradeManifest(string DowngradeId, string DatabaseId, long DataRevision, long SnapshotRevision,
    string DatabaseHash, IReadOnlyDictionary<string, string> FileHashes, IReadOnlyList<DowngradeSession> Sessions,
    IReadOnlyList<string> ConfigPaths, bool PreserveHistoryKinds = false);

/// <summary>Legacy projections for the compatibility window, plus offline downgrade and reupgrade maintenance.</summary>
public sealed class SqliteLegacyCompatibility
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyList<GameConfig> _configs;
    private readonly Action<string>? _checkpoint;
    private readonly bool _preserveHistoryKinds;

    /// <summary>Inject the same clock/timezone/config used by old statistics validation.</summary>
    public SqliteLegacyCompatibility(SqliteConnectionFactory factory, TimeProvider clock, IReadOnlyList<GameConfig>? configs = null, Action<string>? checkpoint = null, bool preserveHistoryKinds = false)
    { _factory = factory; _clock = clock; _configs = configs ?? []; _checkpoint = checkpoint; _preserveHistoryKinds = preserveHistoryKinds; }

    /// <summary>Reads the complete projection from one SQLite snapshot, without writing legacy files.</summary>
    public LegacyProjection ReadProjection()
    {
        using var c = _factory.OpenConnection();
        using var tx = c.BeginTransaction(deferred: true);
        return Capture(c, tx);
    }

    /// <summary>Writes atomic per-file projections, retaining dirty/failure state on interruption or failure.</summary>
    public async Task<LegacyProjection> ExportAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await SqliteDirectoryLock.AcquireAsync(_factory.DataDirectory, cancellationToken).ConfigureAwait(false);
        return ExportUnderLock(cancellationToken);
    }

    /// <summary>Runtime requests materialize only dirty, unregistered or missing projection targets.</summary>
    internal async Task ExportPendingAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await SqliteDirectoryLock.AcquireAsync(_factory.DataDirectory, cancellationToken).ConfigureAwait(false);
        ExportUnderLock(cancellationToken, pendingOnly: true);
    }

    /// <summary>Compatibility window projection for the session writer; Desktop remains on legacy files.</summary>
    public LegacyProjection ExportSessions()
    {
        // Runs on the serial projection worker or offline maintenance, never under automation locks.
        using var lease = SqliteDirectoryLock.AcquireAsync(_factory.DataDirectory, CancellationToken.None).GetAwaiter().GetResult();
        return ExportUnderLock(CancellationToken.None, sessionsOnly: true);
    }

    /// <summary>Offline read-only preflight: checks health, fixed-revision legacy bytes and old-reader statistics.</summary>
    public async Task CheckDowngradeAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await SqliteDirectoryLock.AcquireAsync(_factory.DataDirectory, cancellationToken).ConfigureAwait(false);
        using var c = _factory.OpenConnection();
        using var tx = c.BeginTransaction(deferred: true);
        SqliteDatabaseHealth.Check(c, full: true, tx);
        var projection = Capture(c, tx);
        VerifyLegacyHistory(c, tx, projection);
        foreach (var (path, bytes) in projection.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = SqliteLegacyMigration.ReadSource(path);
            if (bytes is null ? actual is not null : actual is null || !bytes.AsSpan().SequenceEqual(actual))
                throw new InvalidDataException("旧格式投影缺失或过期，请先准备降级：" + path);
        }
    }

    /// <summary>Archives the full database (including ratings) and verified projections before an offline downgrade.</summary>
    public async Task<string> PrepareDowngradeAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await SqliteDirectoryLock.AcquireAsync(_factory.DataDirectory, cancellationToken).ConfigureAwait(false);
        var projection = ExportUnderLock(cancellationToken);
        using var c = _factory.OpenConnection();
        using var tx = c.BeginTransaction(deferred: false); // Offline callers; reject unexpected writers during verification/archive.
        CheckRevision(c, tx, projection);
        VerifyLegacyHistory(c, tx, projection);
        var id = Guid.NewGuid().ToString("D");
        var root = Path.Combine(_factory.DataDirectory, "downgrade-backups");
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, ".pending-" + id);
        var published = Path.Combine(root, id);
        Directory.CreateDirectory(temporary);
        try
        {
            var sessions = Sessions(c, tx);
            var configs = Configs(c, tx);
            var databaseId = (string)Scalar(c, tx, "SELECT database_id FROM storage_meta;")!;
            Commit(tx, "downgrade.snapshot"); // BackupDatabase cannot run while the source has a write transaction.
            Hit("downgrade.archive.before");
            using (var destination = SqliteConnectionFactory.OpenFile(Path.Combine(temporary, "userdata.db"), SqliteOpenMode.ReadWriteCreate))
            {
                c.BackupDatabase(destination);
                SqliteDatabaseHealth.Check(destination, full: true);
                SqliteDatabaseHealth.Checkpoint(destination);
                SqliteConnectionFactory.Execute(destination, "PRAGMA journal_mode=DELETE;");
                using var verify = destination.BeginTransaction(deferred: true);
                CheckRevision(destination, verify, projection);
            }
            Hit("downgrade.archive.after");
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var (path, bytes) in projection.Files)
            {
                var current = SqliteLegacyMigration.ReadSource(path);
                if (bytes is null ? current is not null : current is null || !bytes.AsSpan().SequenceEqual(current))
                    throw new InvalidDataException("Projection changed during downgrade preparation: " + path);
                hashes[path] = bytes is null ? "missing" : Hash(bytes);
                if (bytes is not null) File.WriteAllBytes(Path.Combine(temporary, (index++).ToString("D4") + ".legacy"), bytes);
            }
            var manifest = new DowngradeManifest(id, databaseId, projection.DataRevision, projection.SnapshotRevision,
                Hash(File.ReadAllBytes(Path.Combine(temporary, "userdata.db"))), hashes, sessions, configs, _preserveHistoryKinds);
            Hit("downgrade.manifest.before");
            WriteJson(Path.Combine(temporary, "manifest.json"), manifest);
            Hit("downgrade.manifest.after");
            Hit("downgrade.rename.before");
            Directory.Move(temporary, published);
            Hit("downgrade.rename.after");
            // The immutable manifest is the receipt. A failed/missing caller response is safe to retry.
            return Path.Combine(published, "manifest.json");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    /// <summary>
    /// Merges an offline old-version interval into the same database; known occurrence IDs and ratings survive.
    /// Old history must retain the exported prefix. Deletions/edits/reordering block automatic merging.
    /// </summary>
    public async Task ReupgradeAsync(string downgradeManifestPath, CancellationToken cancellationToken = default)
    {
        using var lease = await SqliteDirectoryLock.AcquireAsync(_factory.DataDirectory, cancellationToken).ConfigureAwait(false);
        var baseline = ReadJson<DowngradeManifest>(downgradeManifestPath);
        var archive = Path.Combine(Path.GetDirectoryName(downgradeManifestPath)!, "userdata.db");
        if (Hash(File.ReadAllBytes(archive)) != baseline.DatabaseHash) throw new InvalidDataException("Downgrade archive hash mismatch.");
        // Preserve the latest old files before parsing, even when merge fails.
        var receipt = CaptureOldInterval(baseline);
        var manifest = ReadJson<ImportManifest>(receipt);
        var decoded = SqliteLegacyMigration.Decode(manifest, Path.GetDirectoryName(receipt)!);
        var csv = manifest.Sources.Single(s => s.Kind == "csv");
        var items = decoded[csv.SourceId];
        if (!csv.Exists || items.Any(item => !item.Accepted)) throw new InvalidDataException("Reupgrade requires intact CSV; invalid rows need manual reconciliation.");
        var rows = items.Select(item => (PlaytimeCsvRow)item.Value!).ToList();
        if (rows.Count < baseline.Sessions.Count) throw new InvalidDataException("Old version deleted history; automatic merge stopped.");
        for (var i = 0; i < baseline.Sessions.Count; i++)
        {
            var known = baseline.Sessions[i];
            var row = rows[i];
            if (known.Key != row.GameName || known.Start != row.StartTime || known.End != row.EndTime || known.Minutes != row.DurationMinutes)
                throw new InvalidDataException("Old version changed/reordered history at occurrence " + (i + 1));
        }
        using var c = _factory.OpenConnection();
        using var tx = c.BeginTransaction(deferred: false);
        if ((string)Scalar(c, tx, "SELECT database_id FROM storage_meta;")! != baseline.DatabaseId)
            throw new InvalidDataException("Downgrade baseline belongs to another database.");
        var appliedName = "reupgrade:" + baseline.DowngradeId;
        var fingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(manifest.Sources.Select(s => new { s.Path, s.Exists, s.Sha256 })));
        if (Scalar(c, tx, "SELECT value FROM migration_state WHERE name=$0;", appliedName) is string applied)
        {
            if (applied != fingerprint) throw new InvalidDataException("This downgrade interval was already merged with different sources.");
            return;
        }
        CheckRevision(c, tx, new(baseline.DataRevision, baseline.SnapshotRevision, new Dictionary<string, byte[]?>()));
        // Check the identity/sequence map too, rather than trusting only external hashes and revisions.
        var current = Sessions(c, tx);
        LegacyImportValidation.Equal(baseline.Sessions, current, "downgrade identity baseline");
        LegacyRelationalWriter.Source(c, tx, csv, null);
        for (var i = 0; i < baseline.Sessions.Count; i++)
        {
            Execute(c, tx, "INSERT INTO import_records VALUES($0,$1,'accepted','matched downgrade occurrence',$2);",
                csv.SourceId, items[i].Ordinal, baseline.Sessions[i].SessionId);
            Execute(c, tx, "UPDATE import_sources SET accepted_count=accepted_count+1 WHERE source_id=$0;", csv.SourceId);
        }
        for (var i = baseline.Sessions.Count; i < items.Count; i++)
            LegacyRelationalWriter.Item(c, tx, csv, null, items[i], _clock.GetUtcNow());
        CompleteSource(c, tx, csv);
        foreach (var config in baseline.ConfigPaths)
        {
            var scope = LegacyRelationalWriter.Scope(c, tx, config)!.Value;
            LegacyRelationalWriter.ClearSide(c, tx, scope);
            foreach (var source in manifest.Sources.Where(s => s.ConfigPath == config))
            {
                LegacyRelationalWriter.Source(c, tx, source, scope);
                foreach (var item in decoded[source.SourceId]) LegacyRelationalWriter.Item(c, tx, source, scope, item, manifest.CreatedAtUtc);
                CompleteSource(c, tx, source);
                if (source.Exists) LegacyImportValidation.EqualBytes(LegacyImportValidation.ExpectedSide(source.Kind, decoded[source.SourceId]),
                    LegacyDatabaseReader.Side(c, tx, scope, source.Kind), source.Path);
            }
        }
        var inflight = manifest.Sources.Single(s => s.Kind == "inflight");
        LegacyRelationalWriter.Source(c, tx, inflight, null);
        foreach (var item in decoded[inflight.SourceId]) LegacyRelationalWriter.Item(c, tx, inflight, null, item, manifest.CreatedAtUtc);
        CompleteSource(c, tx, inflight);
        if (decoded[inflight.SourceId].FirstOrDefault()?.Value is not InFlightSnapshot)
            foreach (var table in new[] { "inflight_processes", "inflight_sessions", "pending_launches", "runtime_snapshot" }) Execute(c, tx, "DELETE FROM " + table + ";");
        SqliteLegacyMigration.EnsureSourcesUnchanged(manifest);
        LegacyImportValidation.CompareHistory(LegacyImportValidation.ExpectedHistory(items),
            LegacyImportValidation.ExpectedHistory(LegacyDecoder.Csv(System.Text.Encoding.UTF8.GetString(LegacyDatabaseReader.Csv(LegacyDatabaseReader.History(c, tx), baseline.PreserveHistoryKinds)))), _clock, _configs);
        Execute(c, tx, "UPDATE storage_meta SET history_revision=history_revision+$0,data_revision=data_revision+1; UPDATE compatibility_exports SET dirty=1;",
            rows.Count > baseline.Sessions.Count ? 1 : 0);
        Execute(c, tx, "INSERT INTO migration_state VALUES($0,$1);", appliedName, fingerprint);
        Commit(tx, "reupgrade");
    }

    private LegacyProjection ExportUnderLock(CancellationToken token, bool sessionsOnly = false, bool pendingOnly = false)
    {
        LegacyProjection projection;
        using var c = _factory.OpenConnection();
        using (var tx = c.BeginTransaction(deferred: true)) projection = Capture(c, tx, sessionsOnly, pendingOnly);
        if (projection.Files.Count == 0) return projection;
        using (var tx = c.BeginTransaction(deferred: false))
        {
            foreach (var (path, bytes) in projection.Files)
                Execute(c, tx, """
                    INSERT INTO compatibility_exports VALUES($0,$1,$2,$3,'',1)
                    ON CONFLICT(target) DO UPDATE SET data_revision=excluded.data_revision,snapshot_revision=excluded.snapshot_revision,
                        content_hash=excluded.content_hash,failure='',dirty=1;
                    """, path, projection.DataRevision, projection.SnapshotRevision, bytes is null ? "missing" : Hash(bytes));
            Commit(tx, "projection.dirty");
        }
        foreach (var (path, bytes) in projection.Files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Hit("projection.replace.before:" + Path.GetFileName(path));
                if (bytes is null) File.Delete(path);
                else if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) WriteBytes(path, bytes);
                Hit("projection.replace.after:" + Path.GetFileName(path));
                using var tx = c.BeginTransaction(deferred: false);
                Execute(c, tx, """
                    UPDATE compatibility_exports SET dirty=CASE WHEN
                        (SELECT data_revision FROM storage_meta)=$1 AND coalesce((SELECT revision FROM runtime_snapshot),0)=$2
                        THEN 0 ELSE 1 END,failure='' WHERE target=$0;
                    """, path, projection.DataRevision, projection.SnapshotRevision);
                Commit(tx, "projection.clean:" + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                Execute(c, null, "UPDATE compatibility_exports SET dirty=1,failure=$1 WHERE target=$0;", path, ex.Message);
                throw;
            }
        }
        return projection;
    }

    private LegacyProjection Capture(SqliteConnection c, SqliteTransaction tx, bool sessionsOnly = false, bool pendingOnly = false)
    {
        bool Pending(string path) => !pendingOnly || !File.Exists(path) ||
            Scalar(c, tx, "SELECT dirty FROM compatibility_exports WHERE target=$0;", path) is not long dirty || dirty != 0;
        var files = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var csv = Path.Combine(_factory.DataDirectory, "playtime.csv");
        if (Pending(csv))
        {
            Hit("projection.capture.history");
            files[csv] = LegacyDatabaseReader.Csv(LegacyDatabaseReader.History(c, tx), _preserveHistoryKinds);
        }
        var inflight = Path.Combine(_factory.DataDirectory, "inflight-sessions.json");
        if (Pending(inflight))
        {
            var snapshot = LegacyDatabaseReader.Snapshot(c, tx);
            files[inflight] = snapshot is null ? null : LegacyDatabaseReader.SnapshotBytes(snapshot);
        }
        foreach (var config in sessionsOnly ? [] : Configs(c, tx))
        {
            var scope = Convert.ToInt64(Scalar(c, tx, "SELECT scope_id FROM config_scopes WHERE config_path=$0;", config));
            foreach (var kind in LegacyDecoder.SideKinds)
            {
                var path = config + ".desktop-" + kind + ".json";
                if (Pending(path)) files[path] = LegacyDatabaseReader.Side(c, tx, scope, kind);
            }
        }
        return new(Convert.ToInt64(Scalar(c, tx, "SELECT data_revision FROM storage_meta;")),
            Convert.ToInt64(Scalar(c, tx, "SELECT coalesce((SELECT revision FROM runtime_snapshot),0);")), files);
    }
    private void VerifyLegacyHistory(SqliteConnection c, SqliteTransaction tx, LegacyProjection projection)
    {
        var bytes = projection.Files[Path.Combine(_factory.DataDirectory, "playtime.csv")]!;
        var legacy = LegacyImportValidation.ExpectedHistory(LegacyDecoder.Csv(System.Text.Encoding.UTF8.GetString(bytes)));
        // CSV intentionally drops Kind and subsecond precision. Compare to the database's same legacy projection.
        var expected = LegacyImportValidation.ExpectedHistory(LegacyDecoder.Csv(System.Text.Encoding.UTF8.GetString(LegacyDatabaseReader.Csv(LegacyDatabaseReader.History(c, tx), _preserveHistoryKinds))));
        LegacyImportValidation.CompareHistory(expected, legacy, _clock, _configs);
        // Also exercise the real old reader, not only the strict migration reader.
        var old = new FilePlaytimeSnapshotProvider(_factory.DataDirectory).GetSnapshot();
        if (old.ReadFailed) throw new InvalidDataException("Legacy read path failed during downgrade verification.");
        LegacyImportValidation.CompareHistory(expected, old.Records, _clock, _configs);
    }
    private static List<DowngradeSession> Sessions(SqliteConnection c, SqliteTransaction tx)
    {
        using var command = Command(c, tx, """
            SELECT session_id,s.data_key,started_value,ended_value,duration_minutes
            FROM play_sessions s JOIN history_keys h ON h.data_key=s.data_key ORDER BY h.first_sequence,s.sequence;
            """);
        using var reader = command.ExecuteReader();
        var result = new List<DowngradeSession>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), Project(reader.GetString(2)), Project(reader.GetString(3)), reader.GetInt64(4)));
        return result;
    }
    private static DateTime Project(string value) => DateTime.SpecifyKind(DateTime.ParseExact(value[..19], "yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
    private static List<string> Configs(SqliteConnection c, SqliteTransaction tx)
    {
        using var command = Command(c, tx, "SELECT config_path FROM config_scopes ORDER BY scope_id;");
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
    private static void CheckRevision(SqliteConnection c, SqliteTransaction tx, LegacyProjection projection)
    {
        if (Convert.ToInt64(Scalar(c, tx, "SELECT data_revision FROM storage_meta;")) != projection.DataRevision ||
            Convert.ToInt64(Scalar(c, tx, "SELECT coalesce((SELECT revision FROM runtime_snapshot),0);")) != projection.SnapshotRevision)
            throw new InvalidDataException("Database changed since the fixed-revision baseline.");
    }
    private string CaptureOldInterval(DowngradeManifest baseline)
    {
        var root = Path.Combine(_factory.DataDirectory, "reupgrade-backups");
        Directory.CreateDirectory(root);
        var inventory = baseline.FileHashes.Keys.Select(path =>
        {
            string? config = baseline.ConfigPaths.FirstOrDefault(p => path.StartsWith(p + ".desktop-", StringComparison.OrdinalIgnoreCase));
            var kind = config is null ? Path.GetFileName(path) == "playtime.csv" ? "csv" : "inflight" : path[(config.Length + ".desktop-".Length)..^5];
            var bytes = SqliteLegacyMigration.ReadSource(path);
            return (Path: path, Config: config, Kind: kind, Bytes: bytes);
        }).ToList();
        // Content-derived generation makes committed retries idempotent even if the receipt response was lost.
        var generation = Hash(JsonSerializer.SerializeToUtf8Bytes(inventory.Select(s => new { s.Path, Hash = s.Bytes is null ? "missing" : Hash(s.Bytes) })));
        var directory = Path.Combine(root, baseline.DowngradeId + "-" + generation);
        if (Directory.Exists(directory)) return Path.Combine(directory, "manifest.json");
        var temporary = directory + ".pending-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);
        try
        {
            var sources = new List<ImportSource>();
            foreach (var source in inventory)
            {
                var name = sources.Count.ToString("D4") + ".original";
                if (source.Bytes is not null) File.WriteAllBytes(Path.Combine(temporary, name), source.Bytes);
                sources.Add(new(Id(baseline.DowngradeId + generation, sources.Count), source.Path, source.Config, source.Kind,
                    source.Bytes is not null, source.Bytes is null ? "" : Hash(source.Bytes), source.Bytes?.LongLength ?? 0, name));
            }
            WriteJson(Path.Combine(temporary, "manifest.json"), new ImportManifest(generation, 1, typeof(SqliteLegacyCompatibility).Assembly.GetName().Version!.ToString(), _clock.GetUtcNow(), sources));
            Hit("reupgrade.backup.rename.before");
            Directory.Move(temporary, directory);
            Hit("reupgrade.backup.rename.after");
            return Path.Combine(directory, "manifest.json");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }
    private static void CompleteSource(SqliteConnection c, SqliteTransaction tx, ImportSource source) =>
        Execute(c, tx, "UPDATE import_sources SET status=$1,validation_summary='verified offline reupgrade' WHERE source_id=$0;",
            source.SourceId, source.Exists ? "complete" : "missing");
    private void Commit(SqliteTransaction tx, string name) { Hit(name + ".commit.before"); tx.Commit(); Hit(name + ".commit.after"); }
    private void Hit(string name) => _checkpoint?.Invoke(name);
}
