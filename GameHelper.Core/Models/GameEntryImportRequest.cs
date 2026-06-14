namespace GameHelper.Core.Models;

public sealed class GameEntryImportRequest
{
    public string? ExecutableName { get; set; }

    public string? ExecutablePath { get; set; }

    public string? DisplayName { get; set; }

    public string? BaseDataKey { get; set; }

    public bool IsEnabled { get; set; } = true;
}
