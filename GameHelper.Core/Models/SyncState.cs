using System;

namespace GameHelper.Core.Models;

/// <summary>
/// 推送状态的本地持久化快照（sync-state.json）。用于间隔去抖、脏数据判断与失败退避。
/// </summary>
public sealed class SyncState
{
    /// <summary>上次成功推送时间（UTC）；default 表示从未成功推送。</summary>
    public DateTime LastSuccessUtc { get; set; }

    /// <summary>上次尝试推送时间（UTC），无论成败。</summary>
    public DateTime LastAttemptUtc { get; set; }

    /// <summary>上次失败的错误摘要；成功推送后清空。</summary>
    public string? LastError { get; set; }

    /// <summary>上次成功推送的内容指纹；用于内容未变化时跳过提交。</summary>
    public string? ContentHash { get; set; }

    /// <summary>记录状态对应的目标，配置变更后旧状态作废。</summary>
    public string? Provider { get; set; }

    public string? Repo { get; set; }

    public string? Method { get; set; }

    /// <summary>状态是否仍与当前同步目标匹配（换了仓库/方式后需要全量重推）。</summary>
    public bool MatchesTarget(SyncSettings settings)
    {
        return string.Equals(Provider, settings.Provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Repo, settings.NormalizedRepo, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Method, settings.NormalizedMethod, StringComparison.OrdinalIgnoreCase);
    }
}
