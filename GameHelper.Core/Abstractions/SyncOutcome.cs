namespace GameHelper.Core.Abstractions;

public enum SyncOutcomeStatus
{
    /// <summary>已成功上传。</summary>
    Uploaded,

    /// <summary>本次无需上传（未到间隔/无新数据/内容未变化等），见 <see cref="SyncOutcome.Detail"/>。</summary>
    Skipped,

    /// <summary>上传失败，见 <see cref="SyncOutcome.Error"/>。</summary>
    Failed,

    /// <summary>渠道校验通过（不写入数据）。</summary>
    Validated
}

/// <summary>一次同步尝试的结果。</summary>
public sealed record SyncOutcome(
    SyncOutcomeStatus Status,
    string? CommitId = null,
    string? Detail = null,
    string? Error = null)
{
    public bool Success => Status != SyncOutcomeStatus.Failed;

    public static SyncOutcome Skipped(string detail) => new(SyncOutcomeStatus.Skipped, Detail: detail);

    public static SyncOutcome UploadedCommit(string? commitId) => new(SyncOutcomeStatus.Uploaded, CommitId: commitId);

    public static SyncOutcome FailedWithError(string error) => new(SyncOutcomeStatus.Failed, Error: error);
}
