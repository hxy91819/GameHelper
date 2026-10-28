using System.Text.Json;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;
using Microsoft.Data.Sqlite;
using static GameHelper.Infrastructure.Storage.Sqlite.Legacy.LegacySql;

namespace GameHelper.Infrastructure.Storage.Sqlite.Legacy;

internal static class LegacyRelationalWriter
{
    internal static long? Scope(SqliteConnection c, SqliteTransaction tx, string? config)
    {
        if (config is null) return null;
        Execute(c, tx, "INSERT OR IGNORE INTO config_scopes(config_path) VALUES($0);", config);
        return Convert.ToInt64(Scalar(c, tx, "SELECT scope_id FROM config_scopes WHERE config_path=$0;", config));
    }

    internal static void Source(SqliteConnection c, SqliteTransaction tx, ImportSource source, long? scope)
    {
        Execute(c, tx, "INSERT OR IGNORE INTO import_sources VALUES($0,$1,$2,$3,$4,1,'pending',0,0,'');",
            source.SourceId, scope, source.Path, source.Sha256, source.SizeBytes);
    }

    internal static void Item(SqliteConnection c, SqliteTransaction tx, ImportSource source, long? scope, LegacyItem item, DateTimeOffset now)
    {
        if (Scalar(c, tx, "SELECT 1 FROM import_records WHERE source_id=$0 AND record_ordinal=$1;", source.SourceId, item.Ordinal) is not null) return;
        string? sessionId = null;
        if (item.Accepted)
        {
            switch (item.Value)
            {
                case PlaytimeCsvRow row:
                    sessionId = Id(source.SourceId, item.Ordinal);
                    Session(c, tx, row, sessionId, source.SourceId, item.Ordinal, now);
                    break;
                case LegacyJournal journal:
                    Execute(c, tx, """
                        INSERT INTO journals(scope_id,data_key,status,completed_on,note,revision,position) VALUES($0,$1,$2,$3,$4,1,$5)
                        ON CONFLICT(scope_id,data_key) DO UPDATE SET status=excluded.status,completed_on=excluded.completed_on,note=excluded.note;
                        """, scope, item.Key, journal.Status, journal.CompletedOn?.ToString("yyyy-MM-dd"), journal.Note, item.Ordinal);
                    break;
                case string[] genres:
                    Execute(c, tx, "INSERT INTO genre_overrides(scope_id,data_key,revision,position) VALUES($0,$1,1,$2);", scope, item.Key, item.Ordinal);
                    for (var i = 0; i < genres.Length; i++)
                        Execute(c, tx, "INSERT INTO genre_override_items VALUES($0,$1,$2,$3);", scope, item.Key, genres[i], i);
                    break;
                case uint appId:
                    Execute(c, tx, "INSERT INTO artwork_links(scope_id,data_key,steam_app_id,revision,position) VALUES($0,$1,$2,1,$3);", scope, item.Key, (long)appId, item.Ordinal);
                    break;
                case LegacyDraft draft:
                    Execute(c, tx, "INSERT INTO desktop_drafts VALUES($0,$1,$2,$3,1,$4,1);", scope, Id(source.SourceId, item.Ordinal), item.Key, item.Ordinal, JsonSerializer.Serialize(draft));
                    break;
                case LegacyScan scan:
                    for (var i = 0; i < scan.Directories.Count; i++) Execute(c, tx, "INSERT INTO scan_roots VALUES($0,$1,$2);", scope, scan.Directories[i], i);
                    for (var i = 0; i < scan.Ignored.Count; i++) Execute(c, tx, "INSERT INTO scan_ignored(scope_id,executable,position) VALUES($0,$1,$2);", scope, scan.Ignored[i], i);
                    break;
                case LegacyTheme theme:
                    Execute(c, tx, "INSERT INTO desktop_preferences VALUES($0,$1,1);", scope, theme.Theme);
                    break;
                case InFlightSnapshot snapshot: Snapshot(c, tx, source.SourceId, snapshot); break;
            }
        }
        Execute(c, tx, "INSERT INTO import_records VALUES($0,$1,$2,$3,$4);", source.SourceId, item.Ordinal,
            item.Accepted ? "accepted" : "skipped", item.Reason, sessionId);
        Execute(c, tx, "UPDATE import_sources SET accepted_count=accepted_count+$1,skipped_count=skipped_count+$2 WHERE source_id=$0;",
            source.SourceId, item.Accepted ? 1 : 0, item.Accepted ? 0 : 1);
    }

    internal static void Session(SqliteConnection c, SqliteTransaction tx, PlaytimeCsvRow row, string id, string source, long ordinal, DateTimeOffset now)
    {
        var sequence = Convert.ToInt64(Scalar(c, tx, "SELECT coalesce(max(sequence),0)+1 FROM play_sessions;"));
        Execute(c, tx, "INSERT OR IGNORE INTO history_keys VALUES($0,$1);", row.GameName, sequence);
        Execute(c, tx, "INSERT INTO play_sessions VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,'unknown','legacy',$9,$10,$11);",
            id, row.GameName, sequence, Date(row.StartTime), Date(row.EndTime), (int)row.StartTime.Kind, (int)row.EndTime.Kind,
            (row.EndTime - row.StartTime).Ticks, row.DurationMinutes, now.ToString("O"), source, ordinal);
    }

    internal static void ClearSide(SqliteConnection c, SqliteTransaction tx, long scope)
    {
        foreach (var table in new[] { "journals", "genre_override_items", "genre_overrides", "artwork_links", "desktop_drafts", "scan_roots", "scan_ignored", "desktop_preferences" })
            Execute(c, tx, $"DELETE FROM {table} WHERE scope_id=$0;", scope);
    }

    internal static void Snapshot(SqliteConnection c, SqliteTransaction tx, string source, InFlightSnapshot snapshot)
    {
        foreach (var table in new[] { "inflight_processes", "inflight_sessions", "pending_launches", "runtime_snapshot" }) Execute(c, tx, "DELETE FROM " + table + ";");
        Execute(c, tx, "INSERT INTO runtime_snapshot VALUES(1,$0,$1,1);", Date(snapshot.SnapshotAt), snapshot.HdrEnabledByGameHelper);
        var groups = new Dictionary<(string, string, string?), string>();
        var ordinal = 0;
        foreach (var process in snapshot.Sessions)
        {
            ordinal++;
            if (string.IsNullOrWhiteSpace(process.DataKey)) continue;
            var key = (process.DataKey.ToUpperInvariant(), Date(process.StartedAt), process.RecoveryEndAt is { } re ? Date(re) : null);
            if (!groups.TryGetValue(key, out var id))
            {
                id = process.RecoveryEndAt is { } end ? Scalar(c, tx, """
                    SELECT session_id FROM play_sessions WHERE data_key=$0
                    AND substr(started_value,1,19)=$1 AND substr(ended_value,1,19)=$2 ORDER BY sequence LIMIT 1;
                    """, process.DataKey, Date(process.StartedAt)[..19], Date(end)[..19]) as string : null;
                id ??= Id(source + ":inflight", ordinal);
                groups[key] = id;
                Execute(c, tx, "INSERT INTO inflight_sessions VALUES($0,$1,$2,$3,$4,$5,$6);", id, process.DataKey, Date(process.StartedAt), (int)process.StartedAt.Kind,
                    process.LaunchedByHelper is null ? "unknown" : process.LaunchedByHelper.Value ? "library" : "external",
                    process.RecoveryEndAt is { } recovery ? Date(recovery) : null, process.RecoveryEndAt is { } r ? (int)r.Kind : null);
            }
            Execute(c, tx, "INSERT INTO inflight_processes VALUES($0,$1,$2,$3,$4,$5,$6,$7);", ordinal, id, process.ProcessId,
                process.NormalizedName, process.NormalizedPath, process.ProcessStartedAtUtc is { } utc ? Date(utc) : null, process.LaunchedByHelper, process.DataKey);
        }
        var launchPosition = 0;
        foreach (var (key, deadline) in snapshot.PendingLaunches)
            Execute(c, tx, "INSERT INTO pending_launches VALUES($0,$1,$2);", key, deadline.ToString("O"), launchPosition++);
    }
}
