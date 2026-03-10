using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Controllers;

public sealed class NoOpSpeedEngine : ISpeedEngine
{
    public bool IsSupported => false;

    public string UnavailableReason => "当前平台不支持倍速功能。";

    public SpeedOperationResult ApplySpeed(int processId, double multiplier)
    {
        return new SpeedOperationResult
        {
            Success = false,
            Message = UnavailableReason
        };
    }

    public SpeedOperationResult ResetSpeed(int processId)
    {
        return new SpeedOperationResult
        {
            Success = false,
            Message = UnavailableReason
        };
    }

    public void Dispose()
    {
    }
}
