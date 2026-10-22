namespace GameHelper.Core.Abstractions;

/// <summary>Prepares HDR before launch, then transfers ownership to observed gameplay or expires the pending launch.</summary>
public interface IGameLaunchCoordinator
{
    void Launch(string dataKey);
    IReadOnlyDictionary<string, string> GetGameStates();

    /// <summary>
    /// 映射当前处于"运行中"状态的 dataKey 到本局会话的本地开始时间（监视端用于
    /// 界面实时计时；与 <see cref="GetGameStates"/> 保持一致的锁定快照）。
    /// </summary>
    IReadOnlyDictionary<string, DateTime?> GetRunningSessionStarts();
}
