namespace GameHelper.Core.Models;

public sealed record GamePlaytimeOverviewRecord(
    string GameName,
    long TotalMinutes,
    long RecentMinutes,
    int SessionCount);
