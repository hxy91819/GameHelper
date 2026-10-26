using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>Owns foreground-game reminder timing for one monitor lifecycle.</summary>
public interface IBreakReminderService : IDisposable
{
    bool IsRunning { get; }
    void Start(AppConfig configuration);
    void ReloadConfig(AppConfig configuration);
    /// <summary>Defers the next reminder by at least this much additional foreground play.</summary>
    void Snooze(TimeSpan duration);
    /// <summary>Clears the current foreground-play cycle and any visible reminder.</summary>
    void ResetCycle();
    void Stop();
}
