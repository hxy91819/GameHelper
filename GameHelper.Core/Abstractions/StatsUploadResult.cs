namespace GameHelper.Core.Abstractions;

/// <summary>上传结果。</summary>
public sealed record StatsUploadResult(string? CommitId, bool NoChanges);
