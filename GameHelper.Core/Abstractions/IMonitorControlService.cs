namespace GameHelper.Core.Abstractions;

public interface IMonitorControlService
{
    bool IsRunning { get; }

    void Start();

    void Stop();
}
