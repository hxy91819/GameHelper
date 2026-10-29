namespace GameHelper.Core.Models;

public sealed record LibraryGame
{
    public GameConfig Config { get; init; } = new();
    /// <summary>Runtime-confirmed library request that owns this observed process; absent for external sessions.</summary>
    public Guid? LaunchRequestId { get; init; }
    public int? LaunchProcessId { get; init; }
    public string Revision { get; init; } = string.Empty;
    public string State { get; init; } = "未运行";
    public long TotalMinutes { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
    public GameInstallation Installation { get; init; } = new(GameInstallationState.Unknown, "尚未检查本地安装位置");
    /// <summary>本局会话的本地开始时间（监视端视角，休眠暂停已剔除）；仅 State 为"运行中"时可能有值。</summary>
    public DateTime? RunningStartedAt { get; init; }
}
