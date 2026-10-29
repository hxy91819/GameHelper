using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>Starts a configured entry. Returning does not imply the observed game is running.</summary>
public interface IGameLauncher
{
    /// <summary>Checks that the entry can be started, before any HDR changes.</summary>
    void Validate(GameLaunchOptions options);
    void Start(GameLaunchOptions options);
    /// <summary>Starts once and returns a positively identified process, or null for opaque URI/shortcut launchers.</summary>
    int? StartTracked(GameLaunchOptions options) { Start(options); return null; }
    /// <summary>Runs or closes an executable action; throws if it fails or exceeds its wait deadline.</summary>
    void ExecuteAction(GameAction action);
    /// <summary>Starts or reuses a companion without validating or starting the game's launch entry.</summary>
    void EnsureCompanionRunning(string executablePath);
    /// <summary>Stops running DS4Windows instances for a game with controller support disabled.</summary>
    void StopCompanion();
}
