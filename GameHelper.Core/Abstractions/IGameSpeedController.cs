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
    void ApplySpeed(int processId, double speedMultiplier);
}
