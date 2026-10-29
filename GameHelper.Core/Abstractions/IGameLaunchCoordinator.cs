namespace GameHelper.Core.Abstractions;

/// <summary>Prepares HDR before launch, then transfers ownership to observed gameplay or expires the pending launch.</summary>
public interface IGameLaunchCoordinator
{
    IReadOnlyList<GameHelper.Core.Models.GameProcess> GetRunningProcesses() => [];
    void Launch(string dataKey);
    /// <summary>Associate an optional UI request with the existing launch workflow.</summary>
    void Launch(string dataKey, Guid requestId) => Launch(dataKey);
    /// <summary>Confirmed active processes owned by correlated library requests; external sessions are absent.</summary>
    IReadOnlyDictionary<string, (Guid RequestId, int ProcessId)> GetLaunchIdentities() => new Dictionary<string, (Guid, int)>();
    IReadOnlyDictionary<string, string> GetGameStates();

    /// <summary>
    /// 映射当前处于"运行中"状态的 dataKey 到本局会话的本地开始时间（监视端用于
    /// 界面实时计时；与 <see cref="GetGameStates"/> 保持一致的锁定快照）。
    /// </summary>
    IReadOnlyDictionary<string, DateTime?> GetRunningSessionStarts();
}
