namespace GameHelper.Core.Models;

public sealed class GameEntryUpsertRequest
{
    public string? ExecutableName { get; set; }

    public string? ExecutablePath { get; set; }

    public string? DisplayName { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool HdrEnabled { get; set; }

    public bool SpeedEnabled { get; set; }

    public double? SpeedMultiplier { get; set; }
}
