# 4. Source Tree and Module Organization

## 4.1. 项目结构 (实际)

```text
GameHelper/
├── GameHelper.WinUI/           # WinUI 3 桌面壳层
│   ├── ViewModels/             # 桌面端视图模型
│   ├── Services/               # UI 日志与服务定位辅助
│   └── MainWindow.xaml         # 主窗口
├── GameHelper.ConsoleHost/     # 控制台应用入口和交互式 UI
│   ├── Commands/               # CLI 命令实现 (stats, config)
│   ├── Interactive/            # 交互式 Shell 实现
│   └── Program.cs              # 应用主入口和 DI 容器配置
├── GameHelper.Core/            # 核心业务逻辑与抽象
│   ├── Abstractions/           # 核心接口 (IProcessMonitor, IConfigProvider)
│   ├── Models/                 # 核心数据模型 (GameConfig, ProcessMonitorType)
│   └── Services/               # 核心服务 (GameAutomationService)
├── GameHelper.Infrastructure/  # 基础设施实现
│   ├── Processes/              # WMI 和 ETW 进程监控实现
│   ├── Providers/              # 配置提供程序 (YamlConfigProvider)
│   └── ...                     # 其他平台相关实现
├── GameHelper.Tests/           # 单元测试和集成测试
└── docs/                       # 项目文档（活文档 + archives）
```

## 4.2. 关键模块及其用途

  * **进程监控**: 位于 `GameHelper.Infrastructure/Processes/`。提供 `IProcessMonitor` 接口 的两种实现（WMI 和 ETW）。**(未更改)** 该模块继续监听所有进程启动事件，并将原始事件（包括完整路径）传递给自动化服务。
  * **自动化服务**: `GameHelper.Core/Services/GameAutomationService.cs`。**(已更新)** 订阅进程监控事件。实现 2 级混合匹配策略（L1 路径优先，L2 元数据/模糊回退）。根据匹配结果管理游戏会话的开始和结束。
  * **倍速控制**: `GameHelper.Core/Services/SpeedControlService.cs` 与 `GameHelper.Infrastructure/Hotkeys/`、`GameHelper.Infrastructure/Controllers/WindowsSpeedEngine.cs`。**(新增)** 在 CLI 监控会话内注册 Windows 全局热键，解析前台进程，复用同一套游戏匹配规则，并将倍速开关转发给原生 speedhack 组件。
  * **配置管理**: `GameHelper.Infrastructure/Providers/YamlConfigProvider.cs`。**(已更新)** 负责从用户目录 (`%AppData%`) 或命令行指定路径加载、解析和保存 YAML 配置。现在必须验证 `DataKey` 的存在性，并能解析包含 `ExecutablePath`（可选）和 `ExecutableName`（可选） 的新 `GameConfig` 模型。
  * **数据存储**: `GameHelper.Core/Services/CsvBackedPlayTimeService.cs`。**(已更新)** 以 CSV 格式持久化游戏时长。现在*必须*使用 `GameConfig.DataKey` 字段与 `playtime.csv` 中的 `GameName` 列进行关联读写。
  * **命令行接口**: `GameHelper.ConsoleHost/Commands/` 和 `Interactive/`。**(已更新)** 处理 CLI 命令和交互式会话。现在必须支持拖放添加游戏、倍速默认设置、全局热键配置，以及处理包含 `DataKey`、`ExecutablePath`、`speedEnabled`、`speedMultiplier` 的配置格式。
  * **桌面壳层**: `GameHelper.WinUI/`。提供 WinUI 3 界面，通过 Core 服务完成设置、游戏列表、监控与统计操作，不直接承载业务规则。
