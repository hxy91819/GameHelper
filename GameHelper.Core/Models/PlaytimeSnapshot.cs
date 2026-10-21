namespace GameHelper.Core.Models;

/// <summary>History snapshot; ReadFailed distinguishes an unreadable source from valid empty history.</summary>
public sealed record PlaytimeSnapshot(IReadOnlyList<GamePlaytimeRecord> Records, string? SourcePath, bool ReadFailed = false);
