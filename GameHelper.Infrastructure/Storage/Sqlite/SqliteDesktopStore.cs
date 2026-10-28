using System.Text;
using System.Text.Json;
using GameHelper.Infrastructure.Storage.Sqlite.Legacy;
using Microsoft.Data.Sqlite;
using static GameHelper.Infrastructure.Storage.Sqlite.Legacy.LegacySql;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>Opaque optimistic snapshot owned by one editor, never shared between windows.</summary>
public sealed record DesktopDocumentSnapshot(string Json, IReadOnlyDictionary<string, long> Revisions, IReadOnlyList<string>? DraftIds = null);

/// <summary>Scoped Desktop persistence. JSON is the frozen document boundary, not the database layout.</summary>
public sealed class SqliteDesktopStore(SqliteConnectionFactory factory, string configPath, Action<string>? checkpoint = null)
{
    private readonly string _config = Path.GetFullPath(configPath);
    private static string Table(string kind) => kind switch
    {
        "journal" => "journals", "genres" => "genre_overrides", "artwork" => "artwork_links",
        "drafts" => "desktop_drafts", "theme" => "desktop_preferences", "scan" => "scan_roots",
        _ => throw new ArgumentException("Unknown Desktop document.")
    };

    /// <summary>Checks durable activation without creating a database or falling back after database errors.</summary>
    public static bool IsActivated(SqliteConnectionFactory factory)
    {
        if (!File.Exists(factory.DatabasePath)) return false;
        using var c = factory.OpenConnection();
        return Scalar(c, null, "SELECT value FROM migration_state WHERE name='activated';") is not null;
    }

    /// <summary>Does not create or activate a database. Existing activation failures are surfaced.</summary>
    public static async Task<bool> PrepareAsync(SqliteConnectionFactory factory, string configPath)
    {
        if (!File.Exists(factory.DatabasePath)) return false;
        await new SqliteDatabaseInitializer(factory).InitializeAsync().ConfigureAwait(false);
        using (var c = factory.OpenConnection())
            if (Scalar(c, null, "SELECT value FROM migration_state WHERE name='activated';") is null) return false;
        await new SqliteLegacyMigration(factory, TimeProvider.System).ImportScopeAsync(configPath).ConfigureAwait(false);
        return true;
    }

    public DesktopDocumentSnapshot Read(string kind)
    {
        _ = Table(kind);
        using var c = factory.OpenConnection();
        using var tx = c.BeginTransaction(deferred: true);
        var scope = Scope(c, tx);
        return Read(c, tx, scope, kind);
    }

    private long Scope(SqliteConnection c, SqliteTransaction tx) =>
        Scalar(c, tx, "SELECT scope_id FROM config_scopes WHERE config_path=$0;", _config) is { } value
            ? Convert.ToInt64(value) : throw new InvalidOperationException("Desktop scope 尚未导入。");

    private static string Token(long scope, string kind, string key) => $"desktop:{scope}:{kind}:{key.ToUpperInvariant()}";
    private static long Revision(SqliteConnection c, SqliteTransaction tx, long scope, string kind, string key) =>
        Convert.ToInt64(Scalar(c, tx, "SELECT value FROM migration_state WHERE name=$0;", Token(scope, kind, key)) ?? 0L);

    private static DesktopDocumentSnapshot Read(SqliteConnection c, SqliteTransaction tx, long scope, string kind)
    {
        var json = Encoding.UTF8.GetString(LegacyDatabaseReader.Side(c, tx, scope, kind));
        var revisions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var prefix = Token(scope, kind, "");
        using (var cmd = Command(c, tx, "SELECT name,value FROM migration_state WHERE substr(name,1,length($0))=$0;", prefix))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) revisions[reader.GetString(0)[prefix.Length..]] = long.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
        var ids = new List<string>();
        if (kind == "drafts")
        {
            using var cmd = Command(c, tx, "SELECT draft_id FROM desktop_drafts WHERE scope_id=$0 ORDER BY position,draft_id;", scope);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        return new(json, revisions, kind == "drafts" ? ids : null);
    }

    private static Dictionary<string, LegacyItem> Entries(string kind, string json)
    {
        var items = LegacyDecoder.DecodeJson(kind, json);
        if (items.Any(i => !i.Accepted)) throw new ArgumentException("Desktop 文档含无效字段。");
        // Draft collections preserve duplicate keys and source order; conflict detection covers the whole collection.
        if (kind is "drafts" or "scan" or "theme") return new() { [""] = new(1, "", json) };
        return items.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Prepares/validates outside the lock, then changes only edited rows. Conflicts preserve the caller's snapshot.</summary>
    public DesktopDocumentSnapshot Save(string kind, DesktopDocumentSnapshot baseline, string json)
    {
        _ = Table(kind);
        var before = Entries(kind, baseline.Json);
        var after = Entries(kind, json);
        var changes = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(key => !before.TryGetValue(key, out var a) || !after.TryGetValue(key, out var b) ||
                JsonSerializer.Serialize(a.Value) != JsonSerializer.Serialize(b.Value)).ToList();
        if (changes.Count == 0) return baseline;
        // Parsing, decoding and serialization all precede BEGIN; no file projection under the writer lock.
        var decoded = LegacyDecoder.DecodeJson(kind, json);
        var payloads = decoded.ToDictionary(i => i.Ordinal, i => JsonSerializer.Serialize(i.Value));
        var ids = new List<string>();
        if (kind == "drafts")
        {
            var previous = LegacyDecoder.DecodeJson(kind, baseline.Json);
            var available = previous.Select((item, i) => (item.Key, Id: baseline.DraftIds![i]))
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => new Queue<string>(g.Select(x => x.Id)), StringComparer.OrdinalIgnoreCase);
            foreach (var item in decoded)
                ids.Add(available.TryGetValue(item.Key, out var queue) && queue.Count > 0 ? queue.Dequeue() : Guid.NewGuid().ToString("D"));
        }
        var budget = new SqliteEventWriteBudget(default);
        using var admission = SqliteMaintenanceAdmission.EnterWrite(factory.DataDirectory, () => budget.Remaining);
        using var c = factory.OpenConnection();
        using var tx = budget.BeginWrite(c);
        using var deadline = new SqliteWriteDeadline(c);
        checkpoint?.Invoke("desktop.write.acquired");
        void Deadline() => deadline.Check();
        var scope = Scope(c, tx);
        var revision = Convert.ToInt64(Scalar(c, tx, "SELECT data_revision+1 FROM storage_meta;"));
        foreach (var key in changes)
        {
            Deadline();
            if (Revision(c, tx, scope, kind, key) != baseline.Revisions.GetValueOrDefault(key))
                throw new InvalidOperationException("数据已被另一窗口修改，请重载后重试；编辑已保留。");
            var position = kind is "journal" or "genres" or "artwork"
                ? Convert.ToInt64(Scalar(c, tx, $"SELECT position FROM {Table(kind)} WHERE scope_id=$0 AND data_key=$1;", scope, key)
                    ?? Scalar(c, tx, $"SELECT coalesce(max(position),0)+1 FROM {Table(kind)} WHERE scope_id=$0;", scope)!) : 0;
            Delete(c, tx, scope, kind, key);
            foreach (var item in decoded.Where(i => key.Length == 0 && kind is "drafts" or "scan" or "theme" || StringComparer.OrdinalIgnoreCase.Equals(i.Key, key)))
            {
                Deadline();
                Insert(c, tx, scope, kind, item, revision, position, kind == "drafts" ? ids[(int)item.Ordinal - 1] : null, payloads[item.Ordinal], Deadline);
            }
            Execute(c, tx, "INSERT OR REPLACE INTO migration_state VALUES($0,$1);", Token(scope, kind, key), revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        Execute(c, tx, "UPDATE storage_meta SET data_revision=$0;", revision);
        Execute(c, tx, """
            INSERT INTO compatibility_exports VALUES($0,$1,0,'','',1)
            ON CONFLICT(target) DO UPDATE SET dirty=1;
            """, _config + ".desktop-" + kind + ".json", revision);
        Deadline();
        tx.Commit();
        // Return the editor's own values: unrelated concurrent edits must not silently refresh its baseline.
        var versions = new Dictionary<string, long>(baseline.Revisions, StringComparer.OrdinalIgnoreCase);
        foreach (var key in changes) versions[key] = revision;
        return new(json, versions, kind == "drafts" ? ids : null);
    }

    private static void Delete(SqliteConnection c, SqliteTransaction tx, long scope, string kind, string key)
    {
        if (kind == "genres") Execute(c, tx, "DELETE FROM genre_override_items WHERE scope_id=$0 AND data_key=$1;", scope, key);
        if (kind == "scan") Execute(c, tx, "DELETE FROM scan_ignored WHERE scope_id=$0;", scope);
        Execute(c, tx, $"DELETE FROM {Table(kind)} WHERE scope_id=$0" + (kind is "journal" or "genres" or "artwork" ? " AND data_key=$1;" : ";"), scope, key);
    }

    private static void Insert(SqliteConnection c, SqliteTransaction tx, long scope, string kind, LegacyItem item, long revision, long position, string? draftId, string payload, Action deadline)
    {
        switch (item.Value)
        {
            case LegacyJournal j:
                Execute(c, tx, "INSERT INTO journals VALUES($0,$1,$2,$3,$4,$5,$6);", scope, item.Key, j.Status, j.CompletedOn?.ToString("yyyy-MM-dd"), j.Note, revision, position); break;
            case string[] genres:
                Execute(c, tx, "INSERT INTO genre_overrides VALUES($0,$1,$2,$3);", scope, item.Key, revision, position);
                for (var i = 0; i < genres.Length; i++) { deadline(); Execute(c, tx, "INSERT INTO genre_override_items VALUES($0,$1,$2,$3);", scope, item.Key, genres[i], i); }
                break;
            case uint id:
                Execute(c, tx, "INSERT INTO artwork_links VALUES($0,$1,$2,$3,$4);", scope, item.Key, (long)id, revision, position); break;
            case LegacyDraft draft:
                Execute(c, tx, "INSERT INTO desktop_drafts VALUES($0,$1,$2,$3,1,$4,$5);", scope, draftId, draft.Baseline.Config.DataKey, item.Ordinal, payload, revision); break;
            case LegacyScan scan:
                for (var i = 0; i < scan.Directories.Count; i++) { deadline(); Execute(c, tx, "INSERT INTO scan_roots VALUES($0,$1,$2);", scope, scan.Directories[i], i); }
                for (var i = 0; i < scan.Ignored.Count; i++) { deadline(); Execute(c, tx, "INSERT INTO scan_ignored VALUES($0,$1,$2);", scope, scan.Ignored[i], i); }
                break;
            case LegacyTheme theme:
                Execute(c, tx, "INSERT INTO desktop_preferences VALUES($0,$1,$2);", scope, theme.Theme, revision); break;
        }
    }
}
