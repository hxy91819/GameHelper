namespace GameHelper.Core.Models;

/// <summary>
/// Represents a process lifecycle notification emitted by <see cref="Abstractions.IProcessMonitor" /> implementations.
/// Includes both the executable name (file name) and, when available, the absolute executable path.
/// </summary>
/// <param name="ExecutableName">Process executable name (e.g., "game.exe").</param>
/// <param name="ExecutablePath">Optional executable path hint from the event source.</param>
/// <param name="ProcessId">Optional operating-system process identifier for lazy path resolution and stop correlation.</param>
/// <param name="Preexisting">监控器启动时枚举到的"已在运行"进程；保持外部来源，不在游戏初始化后补切 HDR。</param>
public readonly record struct ProcessEventInfo(string ExecutableName, string? ExecutablePath, int? ProcessId = null, bool Preexisting = false)
{
    /// <summary>
    /// Indicates whether the event includes a usable executable path.
    /// </summary>
    public bool HasExecutablePath => !string.IsNullOrWhiteSpace(ExecutablePath);
}
