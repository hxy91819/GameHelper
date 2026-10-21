# 正常开机启动与窗口宿主

## 默认交互入口

开机自启、桌面快捷方式及部署后重启统一直接运行发布目录的 `GameHelper.ConsoleHost.exe`，正常显示交互窗口，保留彩色界面。

后台无焦点启动方向已按用户决定废弃，见[架构决定](../adr/0001-normal-interactive-startup.md)。发布产物不再包含 Launcher，也不保留隐藏、最小化启动及就绪握手。

部署成功后，只将目标为本发布目录旧 Launcher 的桌面快捷方式迁回 ConsoleHost；参数、图标及其他快捷方式保持不变。新实例将自启注册同步为 ConsoleHost，旧版本自启入口不需手工调整。

## CLI 与直接启动

已有终端里的 CLI 命令继续使用 `GameHelper.ConsoleHost.exe` 或 `dotnet run --project GameHelper.ConsoleHost -- ...`。

直接启动 ConsoleHost 时，旧的 Windows Terminal 宿主判断仍适用：取 `GetConsoleWindow` 的根所有者，若不是 WindowsTerminal，尝试以 `wt.exe -w -1` 重新拉起。沙盒、重定向、调试器、已有实例、缺少 wt.exe 或 `GAMEHELPER_DISABLE_TERMINAL_RELAUNCH=1` 时跳过；内部标记防止反复转移。

`startup.launchOnStartup` 的 Run 注册值直接指向 ConsoleHost。沙盒和一次性 CLI 不修改 Run 注册值。

## 验证

部署与 `PublishedInteractiveStartupTests` 直接加载同一个无顶层部署副作用的 `scripts/start-checked-instance.ps1`，调用实际的 `Start-CheckedInstance`；测试不提取或改写生产函数源文本。测试使用独立数据目录与单实例隔离，断言窗口可见且未最小化，并确认没有发布废弃 Launcher。另覆盖隐藏实例拒绝与 ConPTY 隐藏兼容窗口的可见 owner 核验。部署还必须通过完整性级别、数据目录和 ETW 就绪探针，窗口可见不能代替监控可用。
