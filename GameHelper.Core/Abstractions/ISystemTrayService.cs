namespace GameHelper.Core.Abstractions;

/// <summary>Owns the background monitor's native notification-area entry.</summary>
public interface ISystemTrayService : IDisposable
{
    bool IsRunning { get; }
    void Start();
    void Stop();
}
