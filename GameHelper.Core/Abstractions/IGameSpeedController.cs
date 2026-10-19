namespace GameHelper.Core.Abstractions;

/// <summary>
/// Applies a speed multiplier to a running game process (Cheat Engine integration).
/// </summary>
public interface IGameSpeedController
{
    /// <summary>
    /// Applies <paramref name="speedMultiplier"/> to <paramref name="processId"/>.
    /// Implementations should report failures through logging instead of disrupting monitoring.
    /// </summary>
    /// <returns>
    /// True when an attach was actually initiated for <paramref name="processId"/>; false when the
    /// controller bailed out (invalid pid, missing executable, already attached, ...).
    /// </returns>
    bool ApplySpeed(int processId, double speedMultiplier);
}
