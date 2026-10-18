namespace GameHelper.Core.Abstractions;

/// <summary>
/// Applies an optional speed multiplier to a running game process.
/// </summary>
public interface IGameSpeedController
{
    /// <summary>
    /// Applies <paramref name="speedMultiplier"/> to <paramref name="processId"/>.
    /// Implementations should report failures through logging instead of disrupting monitoring.
    /// </summary>
    /// <returns>True when the attach was actually initiated (or would be, for test fakes); false when the controller bailed out (invalid pid, missing executable, ...).</returns>
    bool ApplySpeed(int processId, double speedMultiplier);
}