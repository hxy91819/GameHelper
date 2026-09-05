using System;

namespace GameHelper.Core.Models;

/// <summary>推送状态的只读视图，供 CLI/UI 展示。</summary>
public sealed class SyncStatusInfo
{
    public bool Configured { get; init; }

    public bool Enabled { get; init; }

    public string Provider { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string Repo { get; init; } = string.Empty;

    public string Directory { get; init; } = string.Empty;

    public int IntervalMinutes { get; init; }

    /// <summary>配置校验错误；null 表示配置有效。</summary>
    public string? ConfigError { get; init; }

    public DateTime LastSuccessUtc { get; init; }

    public DateTime LastAttemptUtc { get; init; }

    public string? LastError { get; init; }

    /// <summary>本地数据是否比上次成功推送更新（需要推送）。</summary>
    public bool HasPendingData { get; init; }
}
