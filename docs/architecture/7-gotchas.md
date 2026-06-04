# 7\. 变通方法和注意事项 (Gotchas)

  * **跨平台开发约束**:
      * **核心依赖**: 项目的核心功能（WMI, ETW）强依赖于 Windows API。
      * **非 Windows 环境开发**: 在 macOS 或 Linux 上进行开发时，大部分功能将无法运行。测试时需要有条件地跳过这些功能。`Program.cs` 中的 `NoOpProcessMonitor` 和 `NoOpAutoStartManager` 是在这种情况下使用的关键组件，可以通过 `--monitor-dry-run` 标志启用。
  * **ETW 监控权限**: `EtwProcessMonitor` 需要管理员权限才能运行。如果权限不足，`ProcessMonitorFactory` 会自动降级到 WMI，但这会增加进程事件的延迟。
  * **交互式命令行测试**: `GameHelper.Tests` 中的测试通过模拟用户输入来验证交互式命令行的行为。这使得测试很脆弱，如果 UI 布局或文本发生变化，测试可能会失败。
