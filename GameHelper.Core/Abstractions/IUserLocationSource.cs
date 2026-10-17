using System;
using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>
/// Provides the device's current geographic coordinates. Implementations are best-effort:
/// null means "unavailable" (permission denied, no providers, platform without support).
/// Callers must tolerate slow or failing calls.
/// </summary>
public interface IUserLocationSource
{
    /// <summary>Tries to read the current coordinate; null when unavailable.</summary>
    UserCoordinate? TryGetLocation();
}