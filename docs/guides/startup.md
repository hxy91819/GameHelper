# WPF 与后台启动

## 日常入口

开机 Run 项使用正式发布目录 `desktop\GameHelper.Desktop.exe --background`。此模式不创建 WPF 窗口，连接现有运行时或以 `CreateNoWindow` 启动 `ConsoleHost monitor --background`，确认 IPC 就绪后退出引导进程。后台输出写到配置旁 `config.yml.runtime.log`，超过 5 MiB 时在下次启动轮转。

这是用户在 WPF 成为主界面后重新确认的决定，见 [ADR-0002](../adr/0002-wpf-library-and-launch-ownership.md)；旧 Console 交互界面的移除见 [ADR-0003](../adr/0003-remove-console-interactive-ui.md)。原 [ADR-0001](../adr/0001-normal-interactive-startup.md) 已被替代。

主动双击游戏库才打开并激活 WPF。关闭游戏库继续监控；重复打开复用同一配置的窗口。部署更新只恢复原本打开的窗口，使用 `--restore` 不激活。旧 Console 和 Launcher 桌面入口迁到游戏库。

## CLI 与直接启动

已有终端里的显式 CLI 命令继续使用 `GameHelper.ConsoleHost.exe` 或 `dotnet run --project GameHelper.ConsoleHost -- ...`。

不带参数启动 `GameHelper.ConsoleHost` 会打开 WPF 游戏库。显式传入 `--config <path>` 时，路径会透传给游戏库；不传时游戏库使用默认配置路径。后台 `--background` 启动不触发游戏库窗口。

`startup.launchOnStartup` 的 Run 注册值指向 WPF `--background` 入口。沙盒和一次性 CLI 不修改 Run 注册值。

## 验证

部署和后台发布回归共用 `scripts/start-checked-instance.ps1`，验证实际后台入口、运行时 PID 与目标路径、无可见控制台、IPC 和 ETW/CSV。单元测试和 CLI 冒烟默认运行。部署健康验收保留权限、数据目录和监控就绪检查。旧版本回滚通过兼容的隐藏 `monitor` 命令恢复监控。
