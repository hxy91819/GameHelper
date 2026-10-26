using System.Collections.Generic;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Controllers;

/// <summary>
/// No-op monitor preset controller used on non-Windows platforms, in tests, or wherever
/// the real DDC/CI controller is unavailable.
/// </summary>
public class NoOpMonitorModeController : IMonitorModeController
{
    /// <inheritdoc />
    public bool IsAvailable => true;

    public MonitorModeSnapshot? ReadSnapshot() => null;

    /// <inheritdoc />
    public IReadOnlyDictionary<byte, uint>? TryReadState(IReadOnlyList<MonitorModeWrite> probeCodes) => null;

    /// <inheritdoc />
    public void Apply(IReadOnlyList<MonitorModeWrite> writes)
    {
        // No hardware attached; nothing to do.
    }
}