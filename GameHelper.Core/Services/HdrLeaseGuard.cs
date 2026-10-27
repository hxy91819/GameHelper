using System.Runtime.CompilerServices;
using GameHelper.Core.Abstractions;

namespace GameHelper.Core.Services;

/// <summary>
/// One gate per controller. Lock order: automation state → lease gate → controller.
/// Manual operations take only lease gate → controller and never re-enter automation.
/// Recovery calls HdrScheduler.Update before the tray starts, publishing adopted ownership.
/// </summary>
internal sealed class HdrLeaseGuard
{
    private static readonly ConditionalWeakTable<IHdrController, HdrLeaseGuard> Guards = new();
    internal static HdrLeaseGuard For(IHdrController controller) => Guards.GetValue(controller, _ => new());
    internal object Sync { get; } = new();
    internal bool Owned { get; set; }
}
