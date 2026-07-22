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
}
