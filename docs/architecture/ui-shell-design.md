# GameHelper WinUI 设计说明

本文档替代旧 Web UI 设计文档，描述 WinUI 3 壳层的界面约束与自动化约束。

## 页面结构

- Settings：监控类型与全局开关
- Games：游戏清单（list/add/update/delete/toggle）
- Stats：统计概览与明细入口

## 交互原则

- 页面只负责展示与交互，不承载业务规则
- 所有业务写操作通过 Core contracts 执行
- CLI 与 WinUI 使用同一套 Core 服务，保证行为一致

## AutomationId 约定

关键控件必须配置稳定 `AutomationId`，用于 FlaUI smoke：

- `Shell_ToggleMonitorButton`
- `Settings_MonitorTypeComboBox`
- `Settings_AutoStartToggle`
- `Settings_LaunchOnStartupToggle`
- `Settings_SaveButton`
- `Games_ListView`
- `Games_RefreshButton`
- `Games_AddButton`
- `Games_UpdateButton`
- `Games_DeleteButton`
- `Stats_RefreshButton`
- `Stats_ListView`

## CI 执行策略

- Hosted Windows：仅运行 smoke 集合
- Self-hosted interactive Windows：运行完整桌面 UI 自动化
