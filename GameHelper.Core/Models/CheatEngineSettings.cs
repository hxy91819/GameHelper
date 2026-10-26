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

    /// <summary>
    /// Default in-game speed hotkeys for games with a speed but no own <c>speedHotkeys</c>.
    /// </summary>
    public List<SpeedHotkey>? SpeedHotkeys { get; set; }

    /// <summary>A game's own non-empty hotkey list replaces the defaults as a whole.</summary>
    public static IReadOnlyList<SpeedHotkey>? Resolve(IReadOnlyList<SpeedHotkey>? gameHotkeys, CheatEngineSettings? settings) =>
        gameHotkeys is { Count: > 0 } ? gameHotkeys : settings?.SpeedHotkeys is { Count: > 0 } defaults ? defaults : null;
}
