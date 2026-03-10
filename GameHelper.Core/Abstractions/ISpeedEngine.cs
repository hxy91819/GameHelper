using System;
using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface ISpeedEngine : IDisposable
{
    bool IsSupported { get; }

    string UnavailableReason { get; }

    SpeedOperationResult ApplySpeed(int processId, double multiplier);

    SpeedOperationResult ResetSpeed(int processId);
}
