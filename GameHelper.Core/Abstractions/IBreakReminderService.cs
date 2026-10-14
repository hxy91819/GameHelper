using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>Owns foreground-game reminder timing for one monitor lifecycle.</summary>
public interface IBreakReminderService : IDisposable
{
    bool IsRunning { get; }
    void Start(AppConfig configuration);
    void ReloadConfig(AppConfig configuration);
    void Stop();
}
