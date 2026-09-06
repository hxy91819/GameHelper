using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>
/// 统计自动推送服务：负责脏检查、间隔去抖、构建报告并调用上传渠道。
/// </summary>
public interface ISyncService
{
    /// <summary>
    /// 执行一次同步。<paramref name="force"/> 为 true 时跳过间隔与退避判断，直接构建并上传。
    /// </summary>
    Task<SyncOutcome> SyncNowAsync(bool force = false, CancellationToken cancellationToken = default);

    /// <summary>校验当前配置与渠道可达性（凭据、仓库、分支），不写入远端数据。</summary>
    Task<SyncOutcome> ValidateAsync(CancellationToken cancellationToken = default);

    /// <summary>读取当前推送状态（不发起网络请求）。</summary>
    SyncStatusInfo GetStatus();
}
