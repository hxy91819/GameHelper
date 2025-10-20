# GameHelper 棕地架构文档

## 1. 介绍

本文档旨在捕获 **GameHelper** 代码库的 **当前状态**，包括其架构、实现模式、技术债务和现实世界中的约束。它将作为未来开发人员和 AI 代理进行功能增强和维护工作的核心参考。

### 1.1. 文档范围

本文档对整个系统进行全面的文档记录，重点关注其核心功能、复杂模块和开发环境约束。

### 1.2. 变更日志

| 日期 | 版本 | 描述 | 作者 |
| --- | --- | --- | --- |
| 2025-10-19 | 1.0 | 初始棕地分析 | Winston (Architect) |

## 2. 快速参考 - 关键文件和入口点

### 2.1. 理解系统的关键文件

*   **主入口点**: `GameHelper.ConsoleHost/Program.cs` - 应用程序的启动、依赖注入配置和命令分发中心。
*   **核心后台服务**: `GameHelper.ConsoleHost/Worker.cs` - 托管 `GameAutomationService` 的后台服务，是监控逻辑的长期运行宿主。
*   **核心业务逻辑**: `GameHelper.Core/Services/GameAutomationService.cs` - 响应进程事件、管理游戏会话和调用其他服务的中心协调器。
*   **进程监控实现**:
    *   `GameHelper.Infrastructure/Processes/WmiProcessMonitor.cs` - 基于 WMI 的进程监控，兼容性好。
    *   `GameHelper.Infrastructure/Processes/EtwProcessMonitor.cs` - 基于 ETW 的进程监控，延迟低但需要管理员权限。
    *   `GameHelper.Infrastructure/Processes/ProcessMonitorFactory.cs` - 创建监控器实例并处理从 ETW 到 WMI 自动降级逻辑的工厂。
*   **配置加载**: `GameHelper.Infrastructure/Providers/YamlConfigProvider.cs` - 负责加载和解析 `config.yml` 文件。
*   **数据存储**: `GameHelper.Core/Services/CsvBackedPlayTimeService.cs` - 将游戏时长数据追加到 CSV 文件中。
*   **交互式命令行**: `GameHelper.ConsoleHost/Interactive/InteractiveShell.cs` - 使用 Spectre.Console 构建的交互式 UI 的实现。

## 3. 高层架构

### 3.1. 技术摘要

GameHelper 是一个基于 .NET 8 的 Windows 控制台应用程序，其核心是事件驱动的进程监控系统。它采用分层架构，将核心逻辑、基础设施实现和控制台主机分离开来。应用支持两种进程监控模式（WMI 和 ETW），并具有自动降级功能以确保稳健性。

### 3.2. 实际技术栈

| 类别 | 技术 | 版本 | 备注 |
| --- | --- | --- | --- |
| 运行时 | .NET | 8.0 | 目标平台为 `net8.0-windows` |
| 依赖注入 | Microsoft.Extensions.Hosting | 9.0.8 | 用于管理服务生命周期和依赖关系 |
| 控制台 UI | Spectre.Console | 0.47.0 | 用于构建丰富的交互式命令行界面 |
| 配置文件 | YamlDotNet | 13.7.1 | 用于解析 YAML 配置文件 |
| ETW 监控 | Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.8 | 用于处理低延迟的 ETW 内核事件 |
| WMI 监控 | System.Management | 8.0.0 | 用于兼容性更好的 WMI 事件 |
| 测试框架 | xUnit | 2.9.2 | 单元测试和集成测试的主要框架 |
| 模拟库 | Moq | 4.20.72 | 用于在测试中创建模拟对象 |

### 3.3. 仓库结构

*   **类型**: 单一仓库（Monorepo），包含多个 .NET 项目。
*   **包管理器**: NuGet
*   **显著特点**: 项目按功能分层（Core, Infrastructure, ConsoleHost, Tests），职责清晰。

## 4. 源码树和模块组织

### 4.1. 项目结构 (实际)

```text
GameHelper/
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
└── docs/                       # 项目文档
```

### 4.2. 关键模块及其用途

*   **进程监控**: 位于 `GameHelper.Infrastructure/Processes/`。提供 `IProcessMonitor` 接口的两种实现（WMI 和 ETW），并通过 `ProcessMonitorFactory` 进行抽象创建。这是系统的核心和最复杂的部分。
*   **自动化服务**: `GameHelper.Core/Services/GameAutomationService.cs`。订阅进程监控事件，并根据用户配置管理游戏会话的开始和结束。
*   **配置管理**: `GameHelper.Infrastructure/Providers/YamlConfigProvider.cs`。负责从用户目录 (`%AppData%`) 或命令行指定路径加载、解析和保存 YAML 配置。
*   **数据存储**: `GameHelper.Core/Services/CsvBackedPlayTimeService.cs`。以 CSV 格式持久化游戏时长，支持追加写入以避免数据丢失。
*   **命令行接口**: `GameHelper.ConsoleHost/Commands/` 和 `Interactive/`。分别处理独立的 CLI 命令和功能丰富的交互式会话。

## 5. 数据模型和存储

### 5.1. 数据模型

*   **游戏配置**: `GameHelper.Core/Models/GameConfig.cs` 定义了用户在 `config.yml` 中配置的每个游戏条目。
*   **应用配置**: `GameHelper.Core/Models/AppConfig.cs` 定义了全局设置，如 `ProcessMonitorType`。

### 5.2. 数据存储

*   **游戏时长**: 数据存储在 `playtime.csv` 文件中。这是一个简单的 CSV 文件，包含 `GameName`, `StartTime`, `EndTime`, `DurationSeconds` 等字段。该文件由 `CsvBackedPlayTimeService` 管理。

## 6. 技术债务和已知问题

1.  **HDR 功能**: `IHdrController` 的实现 `NoOpHdrController` 是一个空操作。如 `README.md` 所述，实际的 HDR 自动切换功能因技术挑战而暂缓实现。
2.  **进程过滤**: 当前的 WMI/ETW 监控会监听所有进程，然后在 `GameAutomationService` 中进行过滤。`README.md` 提到未来可以优化为只监听配置列表中的进程，以提升性能。
3.  **存档复制**: 自动复制存档（如魂类游戏）的功能仍在计划中，尚未实现。
4.  **错误处理**: 尽管存在文件容错，但在某些边缘情况下，不正确的配置或损坏的 CSV 文件可能导致未经处理的异常。

## 7. 变通方法和注意事项 (Gotchas)

*   **跨平台开发约束**:
    *   **核心依赖**: 项目的核心功能（WMI, ETW）强依赖于 Windows API。
    *   **非 Windows 环境开发**: 在 macOS 或 Linux 上进行开发时，大部分功能将无法运行。测试时需要有条件地跳过这些功能。`Program.cs` 中的 `NoOpProcessMonitor` 和 `NoOpAutoStartManager` 是在这种情况下使用的关键组件，可以通过 `--monitor-dry-run` 标志启用。
*   **ETW 监控权限**: `EtwProcessMonitor` 需要管理员权限才能运行。如果权限不足，`ProcessMonitorFactory` 会自动降级到 WMI，但这会增加进程事件的延迟。
*   **交互式命令行测试**: `GameHelper.Tests` 中的测试通过模拟用户输入来验证交互式命令行的行为。这使得测试很脆弱，如果 UI 布局或文本发生变化，测试可能会失败。

## 8. 开发与部署

### 8.1. 本地开发设置

1.  克隆仓库。
2.  安装 .NET 8.0 SDK。
3.  使用 Visual Studio 2022 或 VS Code 打开解决方案。
4.  运行 `GameHelper.ConsoleHost` 项目。
    *   在 Windows 上，可以直接运行所有功能。
    *   在 macOS/Linux 上，使用 `dotnet run -- --monitor-dry-run` 来跳过 Windows 特定的监控服务。

### 8.2. 构建和部署过程

*   **构建命令**: `dotnet build`
*   **发布命令**: `README.md` 中提供了详细的 `dotnet publish` 命令，用于创建自包含的 Windows 可执行文件。
*   **自动化部署**: 项目包含一个位于 `.github/workflows/release.yml` 的 GitHub Actions 工作流。当一个形如 `v*` 的标签被推送到仓库时，它会自动构建项目，并将结果打包上传到 GitHub Release。

## 9. 测试现状

### 9.1. 当前测试覆盖范围

*   **单元测试**: 使用 xUnit 和 Moq 对核心逻辑和服务进行测试。
*   **集成测试**: `ProcessMonitorIntegrationTests.cs` 等文件测试了监控器与系统事件的集成。
*   **端到端测试**: `EndToEndProcessMonitoringTests.cs` 验证了从进程启动到时间记录的完整流程。
*   **已知差距**: 交互式命令行的测试依赖于输入模拟，覆盖范围有限且较为脆弱。

### 9.2. 运行测试

```bash
dotnet test
```

## 10. 附录 - 有用的命令

(源自 `README.md`)

```powershell
# 启动交互式命令行
dotnet run --project .\GameHelper.ConsoleHost --

# 启动监控 (默认 WMI)
dotnet run --project .\GameHelper.ConsoleHost -- monitor

# 使用 ETW 监控 (需要管理员权限)
dotnet run --project .\GameHelper.ConsoleHost -- monitor --monitor-type ETW

# 查看统计
dotnet run --project .\GameHelper.ConsoleHost -- stats

# 配置管理
dotnet run --project .\GameHelper.ConsoleHost -- config list
```
