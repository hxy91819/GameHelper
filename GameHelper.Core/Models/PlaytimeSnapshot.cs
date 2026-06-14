namespace GameHelper.Core.Models;

public sealed record PlaytimeSnapshot(IReadOnlyList<GamePlaytimeRecord> Records, string? SourcePath);
