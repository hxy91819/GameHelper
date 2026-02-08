namespace GameHelper.ConsoleHost.Api.Models;

public sealed class GameDto
{
    public string DataKey { get; set; } = string.Empty;
    public string? ExecutableName { get; set; }
    public string? ExecutablePath { get; set; }
    public string? DisplayName { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool HdrEnabled { get; set; }
}

public sealed class CreateGameRequest
{
    public string? ExecutableName { get; set; }
    public string? ExecutablePath { get; set; }
    public string? DisplayName { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool HdrEnabled { get; set; }
}

public sealed class UpdateGameRequest
{
    public string? ExecutableName { get; set; }
    public string? ExecutablePath { get; set; }
    public string? DisplayName { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool HdrEnabled { get; set; }
}
