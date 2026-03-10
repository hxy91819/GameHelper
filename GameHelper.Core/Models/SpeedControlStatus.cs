namespace GameHelper.Core.Models;

public sealed class SpeedControlStatus
{
    public bool IsSupported { get; init; }

    public bool HotkeyRegistered { get; init; }

    public string Hotkey { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
