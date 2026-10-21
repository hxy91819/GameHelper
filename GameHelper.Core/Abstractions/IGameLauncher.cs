using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>Starts a configured entry. Returning does not imply the observed game is running.</summary>
public interface IGameLauncher
{
    /// <summary>Checks that the entry can be started, before any HDR changes.</summary>
    void Validate(GameLaunchOptions options);
    void Start(GameLaunchOptions options);
}
