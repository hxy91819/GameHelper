namespace GameHelper.Core.Abstractions;

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
