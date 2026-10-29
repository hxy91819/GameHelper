namespace GameHelper.Desktop;

/// <summary>Tracks only the runtime-confirmed process belonging to this window's request.</summary>
internal sealed class ConsoleLaunchState(TimeProvider clock)
{
    internal enum Phase { Idle, Waiting, Running, Completed, TimedOut, Failed }
    internal enum Effect { None, Minimize, Restore }
    internal Phase State { get; private set; }
    internal string? Key { get; private set; }
    internal string? Name { get; private set; }
    internal Guid? RequestId { get; private set; }
    private int? processId;
    private DateTimeOffset deadline;
    private bool dismissed;
    internal bool Waiting => State == Phase.Waiting;
    internal bool Tracking => State is Phase.Waiting or Phase.Running;
    internal bool ShowOverlay => Waiting && !dismissed;
    internal string? Message => State switch
    {
        Phase.Waiting => $"正在等待 {Name} 启动",
        Phase.TimedOut => "等待游戏进程超时，请返回详情检查启动状态。",
        _ => null
    };
    internal bool Begin(string key, string name)
    {
        if (Tracking) return false;
        Key = key; Name = name; RequestId = Guid.NewGuid(); processId = null;
        deadline = clock.GetUtcNow().AddMinutes(2); dismissed = false; State = Phase.Waiting;
        return true;
    }
    internal void Back()
    {
        if (Waiting) dismissed = true;
        else if (!Tracking) { State = Phase.Idle; RequestId = null; }
    }
    internal void Fail() { if (Waiting) { State = Phase.Failed; RequestId = null; } }
    internal Effect Observe(bool running, Guid? requestId, int? pid)
    {
        if (Waiting)
        {
            // Expire before accepting any observation, including one received after a slow IPC call.
            if (clock.GetUtcNow() >= deadline) { State = Phase.TimedOut; RequestId = null; return Effect.None; }
            if (running && requestId == RequestId && pid is not null)
            { processId = pid; State = Phase.Running; return Effect.Minimize; }
        }
        else if (State == Phase.Running && (!running || requestId != RequestId || pid != processId))
        { State = Phase.Completed; RequestId = null; return Effect.Restore; }
        return Effect.None;
    }
}
