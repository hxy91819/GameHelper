namespace GameHelper.Core.Models;

public sealed record GameEntry
{
    public required string DataKey { get; init; }

    public required ExecutableIdentity Executable { get; init; }

    public string ExecutableName => Executable.Name;

    public string? ExecutablePath => Executable.Path;

    public string? DisplayName { get; init; }

    public bool IsEnabled { get; init; } = true;

    public bool HdrEnabled { get; init; }

    public double? SpeedMultiplier { get; init; }

    /// <summary>Optional in-game CE speed presets and primary/original toggle shortcuts.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<SpeedHotkey>? SpeedHotkeys { get; init; }

    public string? SpeedAttach { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<GameAction>? Actions { get; init; }

    public GameLaunchOptions? Launch { get; init; }
}
