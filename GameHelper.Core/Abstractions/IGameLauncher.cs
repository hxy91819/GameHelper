using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>Starts a configured entry. Returning does not imply the observed game is running.</summary>
public interface IGameLauncher
{
    /// <summary>Checks that the entry can be started, before any HDR changes.</summary>
    void Validate(GameLaunchOptions options);
    void Start(GameLaunchOptions options);
    /// <summary>Starts or reuses a companion without validating or starting the game's launch entry.</summary>
    void EnsureCompanionRunning(string executablePath);
    /// <summary>Stops running DS4Windows instances for a game with controller support disabled.</summary>
    void StopCompanion();
}
