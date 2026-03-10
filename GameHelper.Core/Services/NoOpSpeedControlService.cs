using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

public sealed class NoOpSpeedControlService : ISpeedControlService
{
    public bool IsRunning { get; private set; }

    public void Start()
    {
        IsRunning = true;
    }

    public void Stop()
    {
        IsRunning = false;
    }

    public SpeedControlStatus GetStatus() => new()
    {
        IsSupported = false,
        Hotkey = string.Empty,
        Message = "倍速服务未启用。"
    };
}
