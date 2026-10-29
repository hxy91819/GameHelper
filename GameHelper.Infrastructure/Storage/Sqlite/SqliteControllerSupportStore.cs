using GameHelper.Core.Models;
using static GameHelper.Infrastructure.Storage.Sqlite.Legacy.LegacySql;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>Short cache transactions share the maintenance admission and five-second write budget.</summary>
public sealed class SqliteControllerSupportStore(SqliteConnectionFactory factory, TimeProvider clock)
{
    public (ControllerSupport Value, DateTimeOffset RetryAt, int Failures)? Read(uint id)
    {
        using var c = factory.OpenConnection();
        using var cmd = Command(c, null, "SELECT status,dualshock,dualsense,retry_at,failures FROM controller_support_cache WHERE app_id=$0;", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (new(r.GetString(0), r.GetBoolean(1), r.GetBoolean(2)), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)), r.GetInt32(4)) : null;
    }
    public bool? Manual(string config, string key)
    {
        using var c = factory.OpenConnection();
        var value = Scalar(c, null, "SELECT supported FROM controller_support_overrides WHERE scope_id=(SELECT scope_id FROM config_scopes WHERE config_path=$0) AND data_key=$1;", Path.GetFullPath(config), key);
        return value is null ? null : Convert.ToInt32(value) != 0;
    }
    public void SaveManual(string config, string key, bool? value) => Write((c, tx) =>
    {
        var scope = Scalar(c, tx, "SELECT scope_id FROM config_scopes WHERE config_path=$0;", Path.GetFullPath(config)) ?? throw new InvalidOperationException("配置尚未导入 SQLite。");
        if (value is null) Execute(c, tx, "DELETE FROM controller_support_overrides WHERE scope_id=$0 AND data_key=$1;", scope, key);
        else Execute(c, tx, "INSERT INTO controller_support_overrides VALUES($0,$1,$2) ON CONFLICT(scope_id,data_key) DO UPDATE SET supported=excluded.supported;", scope, key, value.Value ? 1 : 0);
        Execute(c, tx, "UPDATE storage_meta SET data_revision=data_revision+1;");
    });
    public void Save(uint id, ControllerSupport value, DateTimeOffset retry, int failures) => Write((c, tx) =>
        Execute(c, tx, "INSERT INTO controller_support_cache VALUES($0,$1,$2,$3,$4,$5) ON CONFLICT(app_id) DO UPDATE SET status=excluded.status,dualshock=excluded.dualshock,dualsense=excluded.dualsense,retry_at=excluded.retry_at,failures=excluded.failures;", id, value.Status, value.DualShock ? 1 : 0, value.DualSense ? 1 : 0, retry.ToUnixTimeSeconds(), failures));
    private void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> action)
    {
        var budget = new SqliteEventWriteBudget(clock.GetLocalNow().DateTime);
        using var admission = SqliteMaintenanceAdmission.EnterWrite(factory.DataDirectory, () => budget.Remaining);
        using var c = factory.OpenConnection(); using var tx = budget.BeginWrite(c);
        action(c, tx); tx.Commit();
    }
}
