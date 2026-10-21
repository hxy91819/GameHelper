namespace GameHelper.Core.Models;

public sealed record GameLibraryResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public string ConfigPath { get; init; } = string.Empty;
    public bool MonitorRunning { get; init; }
    public int RuntimeProcessId { get; init; }
    public IReadOnlyList<LibraryGame> Games { get; init; } = [];
    public IReadOnlyList<SteamInstalledGame> SteamGames { get; init; } = [];
    public PlaytimeDashboard? Statistics { get; init; }
}
