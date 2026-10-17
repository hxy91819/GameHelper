using System.Collections.Generic;
using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>
/// Applies monitor preset modes by writing DDC/CI (MCCS) VCP values, and reads back
/// the well-known VCP state used to detect the currently applied preset.
/// <para>
/// Implementations may be a no-op placeholder or talk to a real monitor over the
/// DDC/CI channel (HDMI/DP/USB-C). All methods are best-effort: hardware flakiness
/// is reported through the return value, never by throwing.
/// </para>
/// </summary>
public interface IMonitorModeController
{
    /// <summary>Whether a controllable monitor was detected. When false all calls are no-ops.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Reads the current value of the given VCP codes from the first controllable
    /// physical monitor. Returns null when any value could not be read (unsupported
    /// code, bus failure, monitor asleep, ...).
    /// </summary>
    IReadOnlyDictionary<byte, uint>? TryReadState(IReadOnlyList<MonitorModeWrite> probeCodes);

    /// <summary>
    /// Writes the given VCP code/value pairs in order. Order matters: some monitors only
    /// accept profile values after their mode selector has been updated. Best-effort;
    /// later calls should always be preceded by a fresh state read of the caller.
    /// </summary>
    void Apply(IReadOnlyList<MonitorModeWrite> writes);
}