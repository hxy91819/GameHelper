using GameHelper.Core.Abstractions;

namespace GameHelper.Core.Services;

/// <summary>Protects manual primary-display HDR changes against pending, active and recovered leases.</summary>
public sealed class TrayHdrControl(IHdrController controller) : ITrayHdrControl
{
    public TrayHdrState Read()
    {
        var lease = HdrLeaseGuard.For(controller);
        lock (lease.Sync)
        {
            var state = controller is IPrimaryDisplayHdrController primary
                ? primary.ReadPrimary() : new TrayHdrState(null, false, "无法读取主显示器");
            return lease.Owned ? state with { CanToggle = false, Reason = "游戏持有 HDR 租约" } : state;
        }
    }

    public void Toggle()
    {
        var lease = HdrLeaseGuard.For(controller);
        lock (lease.Sync)
        {
            if (lease.Owned) throw new InvalidOperationException("游戏持有 HDR 租约，暂不能手动切换。");
            if (controller is not IPrimaryDisplayHdrController primary) throw new InvalidOperationException("无法控制主显示器 HDR。");
            primary.TogglePrimary();
        }
    }
}
