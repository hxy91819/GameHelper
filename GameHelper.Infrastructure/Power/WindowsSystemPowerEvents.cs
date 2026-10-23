using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GameHelper.Infrastructure.Power;

/// <summary>
/// 基于 <see cref="SystemEvents"/> 的电源事件桥接。SystemEvents 用自带隐藏窗口线程广播
/// 事件，订阅方线程无需消息泵。若广播线程不可用（如特殊会话环境），事件不会触发，
/// 计时退回到旧行为（休眠时间计入游玩时长），不影响其他功能。
/// </summary>
public sealed class WindowsSystemPowerEvents : ISystemPowerEvents, IDisposable
{
    private readonly ILogger<WindowsSystemPowerEvents>? _logger;

    /// <inheritdoc />
    public event EventHandler? Suspending;

    /// <inheritdoc />
    public event EventHandler? Resumed;

    public WindowsSystemPowerEvents(ILogger<WindowsSystemPowerEvents>? logger = null)
    {
        _logger = logger;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        try
        {
            EventHandler? handlers = e.Mode switch
            {
                PowerModes.Suspend => Suspending,
                PowerModes.Resume => Resumed,
                _ => null
            };
            handlers?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle PowerModeChanged event {Mode}.", e.Mode);
        }
    }

    public void Dispose() => SystemEvents.PowerModeChanged -= OnPowerModeChanged;
}