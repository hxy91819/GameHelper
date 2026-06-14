namespace GameHelper.Core.Models;

public sealed class GameEntryUpsertRequest
{
    public string? DataKey { get; set; }

    public string? ExecutableName { get; set; }

    public string? ExecutablePath { get; set; }

    public bool ClearExecutablePath { get; set; }

    public string? DisplayName { get; set; }

    public bool ClearDisplayName { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool HdrEnabled { get; set; }
}
