using System;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// Applies the user's preset-mode rule on top of the daylit window and game activity:
/// during daylight an active game wants the "game" preset, everything else (no game,
/// or after sunset) wants the "comfort" preset. Stateless: current monitor state is
/// read from the controller so no decision is cached.
/// </summary>
public sealed class MonitorModeScheduler
{
    /// <summary>
    /// Evaluates the rule and, when the physical state differs, applies the recipe.
    /// May block briefly (DDC/CI bus I/O); callers must not hold locks while calling.
    /// </summary>
    public void Update(
        MonitorModePlan plan,
        bool anyGameActive,
        IMonitorModeController controller,
        ILogger logger,
        DateTimeOffset localNow)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(logger);

        if (!controller.IsAvailable)
        {
            logger.LogDebug("Monitor mode controller unavailable; skipping preset automation.");
            return;
        }

        var isDaylight = plan.Daylight.IsDaylight(localNow);
        var desiredMode = isDaylight && anyGameActive ? MonitorMode.Game : MonitorMode.Comfort;
        var targetWrites = desiredMode == MonitorMode.Game ? plan.GameWrites : plan.ComfortWrites;

        var state = controller.TryReadState(plan.ProbeWrites);
        if (state is not null && plan.MatchesTarget(state, desiredMode))
        {
            logger.LogDebug("Monitor already in {Mode} mode; no DDC/CI writes needed.", desiredMode);
            return;
        }

        var reason = isDaylight
            ? (anyGameActive ? "daylight with an active game" : "daylight without an active game")
            : "after sunset";
        logger.LogInformation(
            "Switching monitor preset to {Mode} ({Reason}); writing DDC/CI recipe {Recipe}.",
            desiredMode,
            reason,
            string.Join(", ", targetWrites));

        controller.Apply(targetWrites);
    }
}