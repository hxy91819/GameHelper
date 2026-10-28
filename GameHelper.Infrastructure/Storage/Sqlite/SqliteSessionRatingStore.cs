using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Data.Sqlite;
using static GameHelper.Infrastructure.Storage.Sqlite.Legacy.LegacySql;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>Short WAL reads and deletion-aware compare-and-swap edits of local ratings.</summary>
public sealed class SqliteSessionRatingStore(SqliteConnectionFactory factory, TimeProvider clock) : ISessionRatingStore
{
    private const string Select = """
        SELECT p.session_id,p.data_key,p.started_value,p.ended_value,p.started_kind,p.ended_kind,p.duration_minutes,
        r.stars,r.thought,r.created_at_utc,r.updated_at_utc,r.revision,
        coalesce((SELECT CAST(value AS INTEGER) FROM migration_state WHERE name='rating:'||p.session_id),r.revision,0),p.sequence
        FROM play_sessions p LEFT JOIN session_ratings r ON r.session_id=p.session_id
        """;
    public RatedSession Get(Guid sessionId)
    {
        using var c = factory.OpenConnection();
        using var tx = c.BeginTransaction(deferred: true);
        return Get(c, tx, sessionId);
    }
    private static RatedSession Get(SqliteConnection c, SqliteTransaction tx, Guid id)
    {
        using var cmd = Command(c, tx, Select + " WHERE p.session_id=$0;", id.ToString("D"));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Row(r) : throw new InvalidOperationException("找不到已结束的会话。");
    }
    private static RatedSession Row(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), r.GetString(1),
        Date(r.GetString(2), r.GetInt32(4)),
        Date(r.GetString(3), r.GetInt32(5)), r.GetInt64(6),
        r.IsDBNull(7) ? null : new(r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8), DateTimeOffset.Parse(r.GetString(9)), DateTimeOffset.Parse(r.GetString(10)), r.GetInt64(11)), r.GetInt64(12));
    public SessionReviewPage Read(SessionReviewQuery query)
    {
        using var c = factory.OpenConnection();
        using var tx = c.BeginTransaction(deferred: true);
        using var cmd = Command(c, tx, Select + """
             WHERE ($0 IS NULL OR p.data_key=$0) AND ($1 IS NULL OR p.sequence<$1)
             AND ($2=0 OR r.session_id IS NOT NULL) ORDER BY p.sequence DESC LIMIT $3;
            """, query.DataKey, query.BeforeSequence, query.RatedOnly ? 1 : 0, query.Limit + 1);
        using var r = cmd.ExecuteReader();
        var rows = new List<RatedSession>(); long? cursor = null;
        while (r.Read())
        {
            if (rows.Count == query.Limit) return new(rows, cursor);
            rows.Add(Row(r)); cursor = r.GetInt64(13);
        }
        return new(rows, null);
    }
    internal static IReadOnlyList<RatedSession> ReadRatings(SqliteConnection c, SqliteTransaction tx)
    {
        using var cmd = Command(c, tx, Select + " WHERE r.session_id IS NOT NULL ORDER BY p.ended_value,p.sequence;");
        using var r = cmd.ExecuteReader(); var rows = new List<RatedSession>();
        while (r.Read()) rows.Add(Row(r));
        return rows;
    }
    public IReadOnlyList<SessionRatingPoint> Timeline(string dataKey)
    {
        using var c = factory.OpenConnection();
        using var tx = c.BeginTransaction(deferred: true);
        using var cmd = Command(c, tx, "SELECT p.session_id,p.ended_value,p.ended_kind,r.stars FROM play_sessions p JOIN session_ratings r ON r.session_id=p.session_id WHERE p.data_key=$0;", dataKey);
        using var r = cmd.ExecuteReader(); var rows = new List<SessionRatingPoint>();
        while (r.Read()) rows.Add(new(Guid.Parse(r.GetString(0)), Date(r.GetString(1), r.GetInt32(2)), r.GetInt32(3)));
        return rows;
    }
    public RatedSession Save(SaveSessionRating request)
    {
        using var c = factory.OpenConnection();
        using var tx = new SqliteEventWriteBudget(clock.GetLocalNow().DateTime).BeginWrite(c);
        var current = Get(c, tx, request.SessionId);
        if (current.Revision != request.ExpectedRevision) throw new InvalidOperationException("本局评分已被另一窗口修改，请重载后再保存；当前输入已保留。");
        if (request.Stars is null && current.Rating is null) return current;
        var id = request.SessionId.ToString("D");
        Execute(c, tx, "UPDATE storage_meta SET data_revision=data_revision+1;");
        var revision = Convert.ToInt64(Scalar(c, tx, "SELECT data_revision FROM storage_meta;"));
        Execute(c, tx, "INSERT INTO migration_state(name,value) VALUES($0,$1) ON CONFLICT(name) DO UPDATE SET value=excluded.value;", "rating:" + id, revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (request.Stars is null) Execute(c, tx, "DELETE FROM session_ratings WHERE session_id=$0;", id);
        else Execute(c, tx, """
            INSERT INTO session_ratings VALUES($0,$1,$2,$3,$3,$4) ON CONFLICT(session_id) DO UPDATE SET
            stars=excluded.stars,thought=excluded.thought,updated_at_utc=excluded.updated_at_utc,revision=excluded.revision;
            """, id, request.Stars, request.Thought, clock.GetUtcNow().ToString("O"), revision);
        var result = Get(c, tx, request.SessionId); tx.Commit(); return result;
    }
}
