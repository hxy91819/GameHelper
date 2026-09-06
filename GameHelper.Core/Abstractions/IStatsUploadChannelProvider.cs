using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>
/// 按配置选择上传渠道（当前为 GitHub 的 git / api 两种方式）。
/// </summary>
public interface IStatsUploadChannelProvider
{
    /// <summary>根据配置返回可用渠道；provider/method 不支持时抛出 <see cref="NotSupportedException"/>。</summary>
    IStatsUploadChannel GetChannel(SyncSettings settings);
}
