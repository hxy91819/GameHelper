namespace GameHelper.Core.Models;

/// <summary>History snapshot; ReadFailed distinguishes an unreadable source from valid empty history.</summary>
public sealed record PlaytimeSnapshot(IReadOnlyList<GamePlaytimeRecord> Records, string? SourcePath, bool ReadFailed = false,
    PlaytimeHistoryVersion? HistoryVersion = null, IReadOnlyList<ReportClearedGame>? Journal = null);

/// <summary>Identity of the committed history, independent of checkpoints and runtime heartbeats.</summary>
public sealed record PlaytimeHistoryVersion(string DatabaseId, long HistoryRevision);
