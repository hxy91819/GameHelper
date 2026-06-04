namespace GameHelper.Core.Models;

public sealed class GameEntry
{
    public string DataKey { get; set; } = string.Empty;

    public string? ExecutableName { get; set; }

    public string? ExecutablePath { get; set; }

    public string? DisplayName { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool HdrEnabled { get; set; }
}
