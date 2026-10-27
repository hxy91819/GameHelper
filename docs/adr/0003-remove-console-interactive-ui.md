# 移除 Console 交互界面与遗留配置迁移

2026-10-27 用户决定旧 Console 交互界面不再维护，删除交互式 Shell、Windows Terminal 宿主转移、`convert-config` / JSON 配置适配器和 `migrate` 命令。原因：日常界面与后续能力完全围绕 WPF 游戏库演进，保留两套界面会持续分散维护成本；用户也明确旧 Console 不需要兼容历史。

无参数启动 `GameHelper.ConsoleHost` 不再进入交互式 Shell，而是通过 `SystemTrayLogic.LibraryStartInfo` 拉起同一发布目录的 WPF 游戏库；显式 `--config` 会透传给游戏库，未指定时由游戏库使用默认配置路径。`--interactive`、`--menu`、`interactive` 命令以及把交互窗口搬进 Windows Terminal 的自动转移一并删除；`monitor --background` 与 WPF `--background` 后台入口保持不变。

配置只保留 YAML。`startup.autoStartMonitor` 不再读取或写入，旧配置中的该键会被忽略；`startup.launchOnStartup`、`monitor` 及其余顶层键保持原义。旧 `config.json` 不再由程序自动转换，需要保留的配置应手工迁移为 `config.yml`。

保留的 CLI 为 `monitor`、`config`、`stats`、`sync`、`validate-config`。`migrate` 对 `playtime.csv` 游戏名的改写能力不再提供；CSV 仍是当前游玩历史格式，首次运行从旧 `playtime.json` 导入到 CSV 的兼容路径仍保留在基础设施内。
