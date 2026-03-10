namespace GameHelper.Core.Models;

public sealed class HotkeyRegistrationResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;
}
