# 启动窗口与焦点

## 默认交互入口

发布目录的 `GameHelper.Launcher.exe` 是无控制台的 GUI 入口。开机自启、部署后重启和迁移后的桌面快捷方式使用它，最终仍运行同目录的 `GameHelper.ConsoleHost.exe`。

启动器以 `CREATE_NEW_CONSOLE`、不继承标准句柄、`SW_SHOWMINNOACTIVE` 创建子进程。子进程完成首次控制台输出后，通过本次启动独有的本地命名事件报告就绪；启动器再以 `SW_SHOWNOACTIVATE` 显示正常大小窗口。它不调用 `SetForegroundWindow`，不把焦点抢走后再还给游戏，也不留下需要手动还原的常驻最小化窗口。单独使用隐藏创建或显示参数在本机全屏游戏测试中仍出现激活，因此使用经发布测试验证的创建与显示组合。

启动器传入内部 `--in-windows-terminal` 标记以跳过二次终端转移；这里的标记表示无需再次迁移宿主。该入口会沿用新控制台的外观，可能是经典 conhost。保持前台游戏焦点优先于自动统一 Windows Terminal 外观。

失败时不会留下不可见且持有单实例锁的后台实例。桌面快捷方式只迁移目标恰为本次发布目录 ConsoleHost 的条目；参数、图标及其他快捷方式不变。直接调用 `dotnet publish GameHelper.ConsoleHost` 会同时发布启动器，启动器只复制自身四个文件，共享运行时由 ConsoleHost 发布产物提供。

## CLI 与直接启动

已有终端里的 CLI 命令继续使用 `GameHelper.ConsoleHost.exe` 或 `dotnet run --project GameHelper.ConsoleHost -- ...`。直接双击 ConsoleHost 仍由 Windows 创建窗口，无法从进程内阻止它出现前的激活；需要后台启动时使用 Launcher。

直接启动 ConsoleHost 时，旧的 Windows Terminal 宿主判断仍适用：取 `GetConsoleWindow` 的根所有者，若不是 WindowsTerminal，尝试以 `wt.exe -w -1` 重新拉起。沙盒、重定向、调试器、已有实例、缺少 wt.exe 或 `GAMEHELPER_DISABLE_TERMINAL_RELAUNCH=1` 时跳过；内部标记防止反复转移。

`startup.launchOnStartup` 的 Run 注册值会优先指向同目录 Launcher；没有发布启动器的开发构建回退 ConsoleHost。沙盒和一次性 CLI 不修改 Run 注册值。

## 验证

`PublishedInteractiveStartupTests` 调用部署脚本实际的 `Start-CheckedInstance`，使用独立数据目录与单实例隔离，采样前台窗口，断言最终控制台可见、未最小化且启动期间未成为前台。另覆盖隐藏实例拒绝与 ConPTY 隐藏兼容窗口的可见 owner 核验。部署还必须通过完整性级别、数据目录和 ETW 就绪探针，窗口可见不能代替监控可用。