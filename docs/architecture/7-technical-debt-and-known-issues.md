# 7. Technical Debt and Known Issues

1.  **HDR 切换路径**: `WindowsHdrController` 已实现 HDR 状态检测（基于 Windows DisplayConfig API：`GetDisplayConfigBufferSizes` + `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo` with `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO`，多显示器感知，识别支持/启用/`ForceDisabled` 三态）和切换（通过 `SendInput` 注入 Win+Alt+B，失败时回退至 `keybd_event`）。`NoOpHdrController` 仅作为非 Windows 环境的占位实现。**已知限制**：当前切换路径依赖前台焦点与桌面会话状态，不适用于无人值守服务场景；`DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE`（type 10）的 SET API 已在枚举中声明但尚未启用作为切换路径，是否引入待评估。
2.  **进程过滤 (已更新状态: 明确推迟)**: 原始架构 指出，当前的 WMI/ETW 监控 会监听所有进程，然后在 `GameAutomationService` 中进行过滤，并建议未来进行优化。
      * **更新决策:** 此项优化（即在 WMI/ETW 级别 预先过滤）已被*明确推迟*。
      * **理由:** 为了支持新的 L2 回退匹配逻辑（使用文件元数据 和模糊匹配 来处理如 `cheatenginex8664sse4avx2.exe` 这样的变体名称），系统*必须*继续监听所有进程启动事件，以捕获它们的完整路径和名称。预过滤已被证实是不可靠的。我们将接受在服务级别 进行过滤所带来的性能开销。
3.  **存档复制**: 自动复制存档（如魂类游戏）的功能仍在计划中，尚未实现。
4.  **错误处理**: 尽管存在文件容错，但在某些边缘情况下，不正确的配置（例如 `DataKey` 缺失或与 `playtime.csv` 不匹配）或损坏的 CSV 文件 可能导致未经处理的异常。
