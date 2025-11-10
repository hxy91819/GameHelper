namespace GameHelper.Core.Models;

/// <summary>
/// Represents a process lifecycle notification emitted by <see cref="Abstractions.IProcessMonitor" /> implementations.
/// Includes both the executable name (file name) and, when available, the absolute executable path.
/// </summary>
/// <param name="ExecutableName">Process executable name (e.g., "game.exe").</param>
/// <param name="ExecutablePath">Optional fully-qualified executable path.</param>
public readonly record struct ProcessEventInfo(string ExecutableName, string? ExecutablePath)
{
    /// <summary>
    /// Indicates whether the event includes a usable executable path.
    /// </summary>
    public bool HasExecutablePath => !string.IsNullOrWhiteSpace(ExecutablePath);
}
