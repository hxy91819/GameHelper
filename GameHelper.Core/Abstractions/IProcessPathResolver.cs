namespace GameHelper.Core.Abstractions;

/// <summary>
/// Resolves a process identifier to its current executable path when matching requires path disambiguation.
/// </summary>
public interface IProcessPathResolver
{
    /// <summary>
    /// Returns the full executable path for <paramref name="processId"/>, or null when the process has exited
    /// or the current process lacks permission to inspect it.
    /// </summary>
    string? TryResolveExecutablePath(int processId);
}
