# WPF 游戏库

日常打开桌面的 **GameHelper 游戏库**，或运行正式发布目录下的 `desktop\GameHelper.Desktop.exe`。窗口连接现有监控实例，没有实例时在后台启动同一发布目录下的运行时。开机只启动后台监控，关闭游戏库继续监控；自动更新恢复游戏库时不激活窗口。

## 使用

- 网格 / 列表切换、名称或程序搜索，`Ctrl+F` 聚焦，`Esc` 清空。显示已结算的历史时长，不把待启动时间记作游玩时间。
- 选中游戏后编辑名称、HDR、倍速和启动入口；“保存设置”原子保存并热更新配置。“启动游戏”会先保存当前游戏的修改。
- 启动入口可为完整 exe、`.lnk` / `.url` 快捷方式路径，或 `steam://rungameid/数字`。参数和工作目录在展开区设置；Steam URI 不附带这两项。仅有进程文件名的旧配置需要补全启动入口；旧绝对 exe 路径默认可启动。
- 使用 Mod、平台启动器的游戏，选择原本使用的快捷方式。用于识别的程序仍填写实际游戏进程，两者可以不同。
- “添加游戏”选择 exe；“扫描 Steam”读取本地游戏库，确认选中条目后导入。新条目使用 Steam 平台入口，重复导入保留已有条目的全部设置。
- “从目录移除”需要确认，历史记录保留。关闭自动化后仍可普通启动，但不应用 HDR、倍速或计时。
- 本地 Steam 封面缓存可定位时使用缓存，否则显示程序图标或名称。不会联网抓取封面。

## HDR 和启动失败

开启监控 → 确认系统 HDR → 原入口启动 → 等待游戏进程 → 真实计时和倍速。HDR 查询失败或未能确认开启时不启动游戏。平台入口返回不等于游戏成功运行；两分钟内未识别到已登记进程会显示超时并释放 HDR。启动中的同一游戏不能重复发起。

最后一个需要 HDR 的游戏及待启动请求结束后，仅恢复本工具原先开启的 HDR。用户原本已打开的 HDR 保持开启。归属与启动截止时间随会话快照跨程序重启保存，恢复时不重复启动游戏。HDR 已开启或状态未知期间不尝试 DDC 显示器预设切换。

从 Steam 等外部入口直接启动仍可被监控，但无法保证 HDR 在游戏初始化前开启。游戏需要前置 HDR 时应从游戏库启动。

## 保存、草稿与冲突

界面修改先成为草稿，不立即改写 `config.yml`。草稿单独保存在同目录 `config.yml.desktop-drafts.json`，包括尚未输入完成的倍率文本；关闭、崩溃或部署重启后继续编辑。“重新载入”确认后放弃草稿。不同配置的游戏库互不影响，同一配置只打开一个编辑窗口。

外部修改或 CLI 修改同一游戏后，旧草稿保存会报告冲突。重新载入最新设置再修改，避免覆盖其他入口的更新。通信超时不会自动重试保存或启动；刷新确认实际状态后决定下一步。

## 配置格式

```yaml
games:
  - dataKey: my-game
    executable: C:\Games\MyGame\game.exe
    displayName: My Game
    enabled: true
    hdr: true
    launch:
      target: C:\Games\MyGame\mod-loader.exe
      arguments: -windowed
      workingDirectory: C:\Games\MyGame
```

`launch` 可省略；绝对 `executable` 路径作为默认入口。它不改变 `dataKey`、进程匹配或历史记录身份。保存运行中的游戏设置时，倍速在下一次真实启动时应用。

## 开发与验证

先正常启动 ConsoleHost，再执行 `dotnet run --project GameHelper.Desktop`。自定义配置使用 `--config <完整路径>`。`GAMEHELPER_DATA_DIR` 仍按既有隔离规则追加 `GameHelper` 子目录。

发布 ConsoleHost 的 win-x64 产物会在 `desktop/` 一起发布 WPF；不能把另一种运行时布局逐文件覆盖进去。完整部署仍执行 `scripts/verify-on-machine.ps1`。脚本会一并原子换入两个入口，重启原本打开的游戏库，并通过实际 IPC 查询验证连接。

只读验收支持 `GameHelper.Desktop.exe --config <沙盒配置> --snapshot <输出目录>`：不激活地显示渲染窗口，连接指定隔离实例，输出网格、列表、空搜索和最小窗口截图后退出，不保存设置或启动游戏。`--check <输出JSON>` 执行无窗口的只读连接探针。

`--background` 无窗口连接或启动监控；可组合 `--check` 返回就绪信息和运行时 PID。`--restore` 恢复游戏库但不激活窗口。后台运行时的输出在配置旁 `config.yml.runtime.log`，超过 5 MiB 时下次启动轮转为 `.previous`。排错仍可手动从终端运行 ConsoleHost 命令。
