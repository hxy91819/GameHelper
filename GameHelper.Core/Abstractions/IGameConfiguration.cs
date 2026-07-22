using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>
/// Owns the complete Game Configuration document and commits changes atomically.
/// </summary>
public interface IGameConfiguration
{
    /// <summary>
    /// Reads a detached snapshot of the complete configuration document.
    /// </summary>
    AppConfig Read();

    /// <summary>
    /// Reloads the latest document, applies one change, validates it, and commits it atomically.
    /// The change is not committed when <paramref name="change"/> throws.
    /// </summary>
    AppConfig Change(Action<AppConfig> change);
}
