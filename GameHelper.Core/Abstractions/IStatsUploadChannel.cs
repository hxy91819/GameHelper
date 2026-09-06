using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>
/// 统计数据上传渠道。实现方负责把文件写入远端目标（如 GitHub 仓库），
/// 且只允许触碰 <see cref="SyncSettings.Directory"/> 指定的子目录中由 GameHelper 管理的文件。
/// </summary>
public interface IStatsUploadChannel
{
    /// <summary>上传一批文件（以单个提交为单位，尽量保证原子性）。</summary>
    Task<StatsUploadResult> UploadAsync(
        SyncSettings settings,
        IReadOnlyList<StatsUploadFile> files,
        string commitMessage,
        CancellationToken cancellationToken = default);

    /// <summary>校验渠道可达性（凭据、仓库、分支），不写入任何数据。</summary>
    Task ValidateAsync(SyncSettings settings, CancellationToken cancellationToken = default);
}
