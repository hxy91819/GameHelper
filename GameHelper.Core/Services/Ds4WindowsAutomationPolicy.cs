using System;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// Applies the DS4Windows setting to external and recovered games. This policy has no
/// mutable state; the launcher owns process synchronization and resource cleanup.
/// Callers keep companion process I/O outside the automation state lock.
/// </summary>
internal sealed class Ds4WindowsAutomationPolicy
{
    private readonly IGameLauncher? _gameLauncher;
    private readonly ILogger _logger;

    internal Ds4WindowsAutomationPolicy(IGameLauncher? gameLauncher, ILogger logger)
    {
        _gameLauncher = gameLauncher;
        _logger = logger;
    }

    internal void Apply(GameConfig game)
    {
        if (_gameLauncher is null) return;
        try
        {
            if (game.Launch?.Ds4WindowsPath is { } path) _gameLauncher.EnsureCompanionRunning(path);
            else _gameLauncher.StopCompanion();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply the configured companion state for {DataKey}; playtime and other automation continue.", game.DataKey);
        }
    }
}
