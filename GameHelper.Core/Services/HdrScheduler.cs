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
    private bool _enabledByScheduler;
    public bool OwnsEnabledHdr => _enabledByScheduler;
    public void AdoptOwnership(bool owned) => _enabledByScheduler = owned;

    /// <summary>Confirms HDR before the game can initialize its display pipeline.</summary>
    public void PrepareForLaunch(IHdrController hdr, ILogger logger)
    {
        if (!hdr.TryGetEnabled(out var enabled)) throw new InvalidOperationException("无法确认 HDR 状态，游戏尚未启动。");
        if (enabled) return;
        var availability = hdr.GetAutomationAvailability();
        if (availability == HdrAutomationAvailability.ExcludedByPolicy)
        {
            logger.LogInformation("当前活跃显示器被 HDR 规则排除，跳过 HDR；使用 SDR 显示器预设自动化。");
            return;
        }
        if (availability != HdrAutomationAvailability.Available)
            throw new InvalidOperationException("无法确认允许且支持 HDR 的活跃显示器，游戏尚未启动。");
        // Ownership begins before the attempt, so partial failures can still be restored.
        _enabledByScheduler = true;
        hdr.Enable();
        if (!hdr.TryGetEnabled(out enabled) || !enabled)
            throw new InvalidOperationException("HDR 未确认开启，游戏尚未启动。请检查系统 HDR 设置。");
    }

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
            configsByDataKey.TryGetValue(dataKey, out var config) && config.HdrEnabled);

        if (shouldEnableHdr)
        {
            if (!hdr.TryGetEnabled(out var enabled)) return;
            if (!enabled && hdr.GetAutomationAvailability() == HdrAutomationAvailability.Available)
            {
                logger.LogInformation("Enabling HDR (active HDR-enabled game)");
                _enabledByScheduler = true;
                hdr.Enable();
            }
            return;
        }

        if (_enabledByScheduler)
        {
            if (!hdr.TryGetEnabled(out var enabled)) return;
            if (enabled)
            {
                logger.LogInformation("Disabling HDR (no HDR-enabled game remaining)");
                hdr.Disable();
            }
            // A failed restoration keeps the lease for a future reconciliation/restart.
            if (hdr.TryGetEnabled(out enabled) && !enabled) _enabledByScheduler = false;
        }
    }
}
