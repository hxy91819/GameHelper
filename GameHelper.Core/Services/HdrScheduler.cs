using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// 根据活跃会话和配置中的 HDR 偏好，决定开启或关闭 HDR。
/// </summary>
internal sealed class HdrScheduler
{
    /// <summary>
    /// 评估当前活跃会话，在需要时切换 HDR 状态。
    /// </summary>
    public void Update(
        IEnumerable<string> activeDataKeys,
        IReadOnlyDictionary<string, GameConfig> configsByDataKey,
        IHdrController hdr,
        ILogger logger)
    {
        var shouldEnableHdr = activeDataKeys.Any(dataKey =>
            configsByDataKey.TryGetValue(dataKey, out var config) && config.HDREnabled);

        if (shouldEnableHdr && !hdr.IsEnabled)
        {
            logger.LogInformation("Enabling HDR (active HDR-enabled game)");
            hdr.Enable();
        }
        else if (!shouldEnableHdr && hdr.IsEnabled)
        {
            logger.LogInformation("Disabling HDR (no HDR-enabled game remaining)");
            hdr.Disable();
        }
    }
}
