namespace GameHelper.Core.Models;

public sealed record GameCatalogUpdateRequest
{
    public ExecutableIdentity? Executable { get; init; }

    public string? DisplayName { get; init; }

    public bool ClearDisplayName { get; init; }

    public bool? IsEnabled { get; init; }

    public bool? HdrEnabled { get; init; }

    public double? SpeedMultiplier { get; init; }

    public List<SpeedHotkey>? SpeedHotkeys { get; init; }

    public bool ClearSpeedHotkeys { get; init; }

    public bool ClearSpeedMultiplier { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<GameAction>? Actions { get; init; }

    public bool ClearActions { get; init; }

    public GameLaunchOptions? Launch { get; init; }

    public bool ClearLaunch { get; init; }

    public string? ExpectedRevision { get; init; }
}
