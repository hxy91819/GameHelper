using System.Text;
using System.Text.Json;
using GameHelper.Core.Models;
using Microsoft.Data.Sqlite;
using static GameHelper.Infrastructure.Storage.Sqlite.Legacy.LegacySql;

namespace GameHelper.Infrastructure.Storage.Sqlite.Legacy;

internal static class LegacyDatabaseReader
{
    internal static IReadOnlyList<GamePlaytimeRecord> History(SqliteConnection c, SqliteTransaction tx)
    {
        var map = new Dictionary<string, GamePlaytimeRecord>(StringComparer.OrdinalIgnoreCase);
        using var command = Command(c, tx, """
            SELECT s.data_key,started_value,ended_value,started_kind,ended_kind,duration_ticks,duration_minutes
            FROM play_sessions s JOIN history_keys h ON h.data_key=s.data_key ORDER BY h.first_sequence,s.sequence;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (!map.TryGetValue(key, out var record)) map[key] = record = new() { GameName = key };
            record.Sessions.Add(new(key, Date(reader.GetString(1), reader.GetInt32(3)), Date(reader.GetString(2), reader.GetInt32(4)),
                TimeSpan.FromTicks(reader.GetInt64(5)), reader.GetInt64(6)));
        }
        return map.Values.ToList();
    }

    internal static byte[] Csv(IReadOnlyList<GamePlaytimeRecord> records, bool preserveKinds = false)
    {
        // The frozen writer is the default. Production additionally retains old-reader accepted Kind suffixes
        // so UTC history is not reinterpreted as local time when sync/older versions read the projection.
        var output = new StringBuilder("game,start_time,end_time,duration_minutes" + Environment.NewLine);
        foreach (var record in records)
            foreach (var row in record.Sessions)
            {
                var key = row.GameName;
                if (key.IndexOfAny([',', '"', '\r', '\n']) >= 0) key = "\"" + key.Replace("\"", "\"\"") + "\"";
                output.Append(key).Append(',').Append(row.StartTime.ToString(preserveKinds ? "yyyy-MM-ddTHH:mm:ssK" : "yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(',').Append(row.EndTime.ToString(preserveKinds ? "yyyy-MM-ddTHH:mm:ssK" : "yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(',').Append(row.DurationMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(Environment.NewLine);
            }
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    internal static byte[] Side(SqliteConnection c, SqliteTransaction tx, long scope, string kind)
    {
        object document;
        switch (kind)
        {
            case "journal":
                var journals = new Dictionary<string, LegacyJournal>();
                Read(c, tx, "SELECT data_key,status,completed_on,note FROM journals WHERE scope_id=$0 ORDER BY position,data_key;", scope, r =>
                    journals.Add(r.GetString(0), new(r.GetString(1), r.IsDBNull(2) ? null : DateOnly.Parse(r.GetString(2), System.Globalization.CultureInfo.InvariantCulture), r.GetString(3))));
                document = journals; break;
            case "genres":
                var genres = new Dictionary<string, string[]>();
                var keys = new List<string>();
                Read(c, tx, "SELECT data_key FROM genre_overrides WHERE scope_id=$0 ORDER BY position,data_key;", scope, r => keys.Add(r.GetString(0)));
                foreach (var key in keys)
                {
                    using var command = Command(c, tx, "SELECT genre FROM genre_override_items WHERE scope_id=$0 AND data_key=$1 ORDER BY position;", scope, key);
                    using var reader = command.ExecuteReader();
                    var values = new List<string>();
                    while (reader.Read()) values.Add(reader.GetString(0));
                    genres.Add(key, values.ToArray());
                }
                document = genres; break;
            case "artwork":
                var artwork = new Dictionary<string, uint>();
                Read(c, tx, "SELECT data_key,steam_app_id FROM artwork_links WHERE scope_id=$0 ORDER BY position,data_key;", scope, r => artwork.Add(r.GetString(0), checked((uint)r.GetInt64(1))));
                document = artwork; break;
            case "drafts":
                var drafts = new List<LegacyDraft>();
                Read(c, tx, "SELECT payload_json FROM desktop_drafts WHERE scope_id=$0 ORDER BY position,draft_id;", scope, r =>
                    drafts.Add(JsonSerializer.Deserialize<LegacyDraft>(r.GetString(0)) ?? throw new InvalidDataException("Null draft payload.")));
                document = drafts; break;
            case "scan":
                var scan = new LegacyScan();
                Read(c, tx, "SELECT directory FROM scan_roots WHERE scope_id=$0 ORDER BY position;", scope, r => scan.Directories.Add(r.GetString(0)));
                Read(c, tx, "SELECT executable FROM scan_ignored WHERE scope_id=$0 ORDER BY position,executable;", scope, r => scan.Ignored.Add(r.GetString(0)));
                document = scan; break;
            case "theme": document = new LegacyTheme(Scalar(c, tx, "SELECT theme FROM desktop_preferences WHERE scope_id=$0;", scope) as string ?? "dark"); break;
            default: throw new InvalidDataException("Unknown side codec.");
        }
        return JsonSerializer.SerializeToUtf8Bytes(document, document.GetType());
    }

    internal static InFlightSnapshot? Snapshot(SqliteConnection c, SqliteTransaction tx)
    {
        InFlightSnapshot? snapshot = null;
        using (var command = Command(c, tx, "SELECT snapshot_at,hdr_enabled_by_helper,revision FROM runtime_snapshot;"))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) snapshot = new() { Revision = reader.GetInt64(2), SnapshotAt = DateTime.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind), HdrEnabledByGameHelper = reader.GetBoolean(1) };
        if (snapshot is null) return null;
        using (var command = Command(c, tx, """
            SELECT coalesce(p.data_key,s.data_key),s.started_value,s.started_kind,s.recovery_end_value,s.recovery_end_kind,
                p.pid,p.normalized_name,p.normalized_path,p.process_started_at_utc,p.launched_by_helper,s.session_id
            FROM inflight_processes p JOIN inflight_sessions s ON s.session_id=p.session_id ORDER BY p.process_row_id;
            """))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) snapshot.Sessions.Add(new()
            {
                SessionId = Guid.Parse(reader.GetString(10)),
                DataKey = reader.GetString(0), StartedAt = Date(reader.GetString(1), reader.GetInt32(2)),
                RecoveryEndAt = reader.IsDBNull(3) ? null : Date(reader.GetString(3), reader.GetInt32(4)),
                ProcessId = reader.IsDBNull(5) ? null : reader.GetInt32(5), NormalizedName = reader.IsDBNull(6) ? null : reader.GetString(6),
                NormalizedPath = reader.IsDBNull(7) ? null : reader.GetString(7),
                ProcessStartedAtUtc = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                LaunchedByHelper = reader.IsDBNull(9) ? null : reader.GetBoolean(9)
            });
        using (var command = Command(c, tx, "SELECT data_key,deadline FROM pending_launches ORDER BY position,data_key;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) snapshot.PendingLaunches.Add(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture));
        return snapshot;
    }

    internal static byte[] SnapshotBytes(InFlightSnapshot snapshot) =>
        [.. Encoding.UTF8.GetPreamble(), .. JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions { WriteIndented = true })];

    private static void Read(SqliteConnection c, SqliteTransaction tx, string sql, long scope, Action<SqliteDataReader> item)
    {
        using var command = Command(c, tx, sql, scope);
        using var reader = command.ExecuteReader();
        while (reader.Read()) item(reader);
    }
}
