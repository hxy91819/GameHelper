namespace GameHelper.Core.Abstractions;

/// <summary>
/// 系统电源状态变化事件（睡眠 / 休眠与唤醒）。事件在系统广播回调线程上触发，
/// 订阅方必须自行保证线程安全，处理器内不得做耗时操作（唤醒路径除外，可做持久化）。
/// </summary>
public interface ISystemPowerEvents
{
    /// <summary>系统即将进入睡眠或休眠。</summary>
    event EventHandler? Suspending;

    /// <summary>系统从睡眠或休眠中唤醒。</summary>
    event EventHandler? Resumed;
}