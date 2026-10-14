namespace GameHelper.Core.Models;

/// <summary>
/// Locates the user-installed Cheat Engine executable used for optional per-game speed control.
/// </summary>
public sealed class CheatEngineSettings
{
    /// <summary>
    /// Absolute path to the Cheat Engine executable.
    /// </summary>
    public string? Executable { get; set; }
}
