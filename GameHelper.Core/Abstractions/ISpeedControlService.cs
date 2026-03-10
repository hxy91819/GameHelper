using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface ISpeedControlService
{
    bool IsRunning { get; }

    void Start();

    void Stop();

    SpeedControlStatus GetStatus();
}
