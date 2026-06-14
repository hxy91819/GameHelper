# 7. Technical Debt and Known Issues

1.  **HDR 切换路径**: `WindowsHdrController` 已实现 HDR 状态检测（基于 Windows DisplayConfig API：`GetDisplayConfigBufferSizes` + `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo` with `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO`，多显示器感知，识别支持/启用/`ForceDisabled` 三态）和切换（通过 `SendInput` 注入 Win+Alt+B，失败时回退至 `keybd_event`）。`NoOpHdrController` 仅作为非 Windows 环境的占位实现。**已知限制**：
    - **API 选择**：`DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE`（type 10）的 SET API 在枚举中声明但未启用——维护者在 Windows 11 环境对该 API 写过 demo，未生效，因此回退至快捷键路径（参见 issue #34）。
    - **时序问题（更深层）**：进程启动事件触发时切换 HDR 已晚于游戏内 HDR 选项可用判断；引入 ETW 仅提升成功率而未根除时延（毫秒级提速对游戏初始化窗口而言不足）。理想方案需在游戏启动**之前**完成 HDR 状态切换。
    - **依赖前台焦点**：当前切换路径依赖桌面会话状态，不适用于无人值守服务场景。
    - **业务必要性待评估**：在支持 HDR 常开的显示器上，自动切换的实际收益降低（参见 issue #34 维护者反馈）。
2.  **进程过滤 (已更新状态: 明确推迟)**: 原始架构 指出，当前的 WMI/ETW 监控 会监听所有进程，然后在 `GameAutomationService` 中进行过滤，并建议未来进行优化。
      * **更新决策:** 此项优化（即在 WMI/ETW 级别 预先过滤）已被*明确推迟*。
      * **理由:** 为了支持新的 L2 回退匹配逻辑（使用文件元数据 和模糊匹配 来处理如 `cheatenginex8664sse4avx2.exe` 这样的变体名称），系统*必须*继续监听所有进程启动事件，以捕获它们的完整路径和名称。预过滤已被证实是不可靠的。我们将接受在服务级别 进行过滤所带来的性能开销。
3.  **存档复制**: 自动复制存档（如魂类游戏）的功能仍在计划中，尚未实现。
4.  **错误处理**: 尽管存在文件容错，但在某些边缘情况下，不正确的配置（例如 `DataKey` 缺失或与 `playtime.csv` 不匹配）或损坏的 CSV 文件 可能导致未经处理的异常。
