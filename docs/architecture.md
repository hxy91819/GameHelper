# GameHelper Brownfield Architecture Document

## 1. Introduction

本文档旨在捕获 **GameHelper** 代码库的 **当前状态**，包括其架构、实现模式、技术债务和现实世界中的约束。它将作为未来开发人员和 AI 代理进行功能增强和维护工作的核心参考。

### 1.1. 文档范围

本文档对整个系统进行全面的文档记录，重点关注其核心功能、复杂模块和开发环境约束。

### 1.2. 变更日志

| 日期 | 版本 | 描述 | 作者 |
| --- | --- | --- | --- |
| 2025-10-19 | 1.0 | 初始棕地分析 | Winston (Architect) |
| 2025-11-09 | 1.1 | 更新以支持混合匹配 (路径/元数据) 和 DataKey | Winston (Architect) |

## 2. 基础设施与部署（棕地）

### 2.1. 关键文件与入口 (Quick Reference)

* **主入口点**: `GameHelper.ConsoleHost/Program.cs` - 应用程序的启动、依赖注入配置、命令分发中心，**以及用于添加新游戏的拖放事件处理**。
* **核心后台服务**: `GameHelper.ConsoleHost/Worker.cs` - 托管 `GameAutomationService` 的后台服务，是监控逻辑的长期运行宿主。
* **核心业务逻辑**: `GameHelper.Core/Services/GameAutomationService.cs` - **(已更新)** 响应进程事件，管理游戏会话。实现一个 2 级混合匹配策略：
    1.  **L1 (路径匹配):** 优先对具有 `ExecutablePath` 的配置进行精确路径匹配。
    2.  **L2 (回退匹配):** 如果 L1 失败，则对*仅*具有 `ExecutableName` 的配置，使用文件元数据 (`FileVersionInfo.ProductName`) 和模糊匹配 (FuzzySharp) 进行回退。
* **进程监控实现**:
    * `GameHelper.Infrastructure/Processes/WmiProcessMonitor.cs` - (未更改) 基于 WMI 的监控。
    * `GameHelper.Infrastructure/Processes/EtwProcessMonitor.cs` - (未更改) 基于 ETW 的监控。
    * `GameHelper.Infrastructure/Processes/ProcessMonitorFactory.cs` - 创建监控器实例并处理从 ETW 到 WMI 自动降级逻辑的工厂。
    * **注意**: 监控器将继续监听所有进程。过滤逻辑完全在 `GameAutomationService` 中处理。
* **核心数据模型**: `GameHelper.Core/Models/GameConfig.cs` - **(已更新)** 定义 `config.yml` 中的游戏条目。现在包含：
    * `DataKey` (必需): 用于关联 `playtime.csv` 的唯一标识符。
    * `ExecutablePath` (可选): 用于 L1 精确路径匹配。
    * `ExecutableName` (可选): 用于 L2 回退匹配。
    * `DisplayName` (可选): 用于 UI 显示的友好名称。
* **配置加载**: `GameHelper.Infrastructure/Providers/YamlConfigProvider.cs` - **(已更新)** 负责加载和解析 `config.yml`。现在必须验证 `DataKey` 的存在性，并能解析新旧混合的配置格式。
* **数据存储**: `GameHelper.Core/Services/CsvBackedPlayTimeService.cs` - **(已更新)** 将游戏时长数据 追加到 CSV 文件中。现在*必须*使用 `GameConfig.DataKey` 作为 CSV 中的 `GameName` 字段进行读写。
* **交互式命令行**: `GameHelper.ConsoleHost/Interactive/InteractiveShell.cs` - **(已更新)** 实现交互式 UI。现在必须支持添加/编辑使用 `DataKey` 和 `ExecutablePath` 的新配置。

### 2.2. 数据库与数据存储

* **当前状态**: 项目不依赖关系型数据库；所有会话数据持久化在 `%AppData%/GameHelper/playtime.csv` 中，文件结构遵循 `game,start_time,end_time,duration_minutes` 头部。
* **首次初始化流程**:
    1.  准备包含有效 `DataKey` 的 `config.yml`（可使用交互式 Shell `config add` 生成）。
    2.  执行 `dotnet run --project .\GameHelper.ConsoleHost -- monitor` 或在任何会话内触发 `StartTracking`，`CsvBackedPlayTimeService` 会自动创建目录并写入带表头的 CSV。
    3.  运行 `dotnet run --project .\GameHelper.ConsoleHost -- stats` 或直接检查文件确认生成成功。
* **验证策略**: 依托 `CsvBackedPlayTimeService` 的单元测试确保写入重试、JSON -> CSV 迁移及表头生成逻辑。上线前额外执行一次手动验证：用历史 `playtime.json` 启动 ConsoleHost，确认日志输出 `Migrating playtime data...` 且 CSV 内容与旧记录一致。
* **混合匹配数据迁移评估**:
    * 旧版本使用 `GameName` 直接写入 CSV。DataKey 引入后，映射策略为：继续把旧 `GameName` 写入 CSV `game` 列，新的匹配逻辑通过 `GameConfig.DataKey` 读写同一字段，保证追加记录不破坏历史数据。
    * 风险控制：在升级前使用脚本（示例位于 `GameHelper.Tests/Fixtures/PlayTimeSample`）生成 `GameName -> DataKey` 对照表；上线后对照运行 `dotnet run -- stats` 与 CSV 历史记录，确认未出现孤立的旧 `GameName`。

### 2.3. API 与服务配置

* **外部依赖**: 当前不调用任何外部 API 或微服务；CLI 直接与本地服务交互。文档中保持 N/A 说明以避免误判。
* **初始化顺序与依赖关系**:
    1.  `Program.cs` 创建 `HostApplicationBuilder`，注册 `YamlConfigProvider`、`CsvBackedPlayTimeService`、`ProcessMonitorFactory` 与 `GameAutomationService` 等依赖。
    2.  构建主机时，根据配置选择 ETW 或 WMI 监控实现，并通过 `ProcessMonitorFactory` 注入 `GameAutomationService`。
    3.  `Worker`（后台托管服务）在启动阶段解析配置、打开监控，再将事件流交给 `GameAutomationService`。
    4.  交互式 Shell 和 CLI 命令通过同一依赖注入容器解析服务，因此在运行 CLI 前应保证监控服务与配置提供器都已成功初始化。
* **部署注记**: 若需临时禁用 ETW，可在配置中设置 `ProcessMonitorType=Wmi`，主机按上述顺序自动完成降级，无需改动代码。

### 2.4. 测试基础设施

* **准备步骤**: 在任何测试运行前执行 `dotnet restore`（若已在开发环境步骤执行可跳过），随后运行 `dotnet build` 以确保生成的测试基线一致。
* **测试命令**: 所有测试通过 `dotnet test GameHelper.sln` 触发；Windows 环境可完整运行，非 Windows 环境需启用 `--monitor-dry-run` 相关测试配置。
* **回归既有功能计划**: 复用第 9.3 节列出的回归脚本，并在每次版本冻结前由架构负责人和 QA 负责人共同签署回归结果，形成冲刺检查点。
* **负责人**: 架构负责人 Winston 负责监控初始化与 CSV 兼容性验证，QA 负责人（当前为 QA 团队 lead）负责测试套件维护及回归记录归档。

## 3. High-Level Architecture

### 3.1. 技术摘要

GameHelper 是一个基于 .NET 8 的 Windows 控制台应用程序，其核心是事件驱动的进程监控系统。它采用分层架构，将核心逻辑、基础设施实现和控制台主机分离开来。

**(已更新)** 应用现在支持一种**混合匹配策略**：
1.  **精确路径匹配 (L1):** 优先匹配 `config.yml` 中配置的 `ExecutablePath`。
2.  **回退匹配 (L2):** 对于仅配置了 `ExecutableName` 的条目，使用文件元数据 (`FileVersionInfo.ProductName`) 和模糊匹配 (FuzzySharp) 进行回退。

**(未更改)** 应用支持两种进程监控模式（WMI 和 ETW），并具有自动降级功能。性能优化（仅监控特定进程） 已被明确推迟，以支持 L2 回退匹配所需的“监听所有进程”架构。

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
| (新) 模糊匹配 | FuzzySharp | (TBD) | (新) 用于 L2 回退匹配逻辑 |

### 3.3. 仓库结构

* **类型**: 单一仓库（Monorepo），包含多个 .NET 项目。
* **包管理器**: NuGet
* **显著特点**: 项目按功能分层（Core, Infrastructure, ConsoleHost, Tests），职责清晰。

### 3.4. 依赖管理策略与兼容性约束

1.  **安装顺序与早期准备**: 在克隆仓库后立即执行 `dotnet restore`，确保所有 NuGet 依赖在进入功能开发或运行测试前即已拉取完毕；随后再打开 IDE 或运行 `dotnet build`，可避免 IDE 在缺少包时反复触发还原。
2.  **包来源与锁定策略**: 依赖统一由 NuGet 提供，核心库版本（见上表）固定在 `.csproj` 文件中，合并请求在变更依赖版本时需同步更新本节表格与 `Directory.Build.props`。
3.  **兼容性验证**: 每次升级依赖前需在 Windows 11 生产等价环境中使用存量 `config.yml` 与 `playtime.csv` 执行一次 `dotnet test` 与 `dotnet run -- monitor`，核对进程监控、配置解析和播放时间写入是否保持一致，以确认对历史数据结构零破坏。
4.  **冲突与特殊要求**: 
    * `Microsoft.Diagnostics.Tracing.TraceEvent` 与 `System.Management` 必须保持与 .NET 8 SDK 发布分支兼容；若出现绑定冲突，优先升级 TraceEvent 到微软发布的最新补丁版本，并在 `GameHelper.Infrastructure` 中验证 ETW 回退逻辑。
    * `Spectre.Console` 与 `FuzzySharp` 均依赖本地化字符串处理；当引入其他字符串库时，需确认不会影响 Spectre 的 ANSI 输出或 FuzzySharp 的文化信息。
5.  **棕地兼容性记录**: 新版本 ConsoleHost 继续沿用原 WPF 时代的核心服务层。保留 `config.yml`（含旧字段）和 `playtime.csv` 的向后兼容性测试，每个里程碑需记录在 `docs/history/Bug_Fix_Summary_zh.md` 中，以确保对旧生产版本的兼容性审查有据可查。

## 4. Source Tree and Module Organization

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

  * **进程监控**: 位于 `GameHelper.Infrastructure/Processes/`。提供 `IProcessMonitor` 接口 的两种实现（WMI 和 ETW）。**(未更改)** 该模块继续监听所有进程启动事件，并将原始事件（包括完整路径）传递给自动化服务。
  * **自动化服务**: `GameHelper.Core/Services/GameAutomationService.cs`。**(已更新)** 订阅进程监控事件。实现 2 级混合匹配策略（L1 路径优先，L2 元数据/模糊回退）。根据匹配结果管理游戏会话的开始和结束。
  * **配置管理**: `GameHelper.Infrastructure/Providers/YamlConfigProvider.cs`。**(已更新)** 负责从用户目录 (`%AppData%`) 或命令行指定路径加载、解析和保存 YAML 配置。现在必须验证 `DataKey` 的存在性，并能解析包含 `ExecutablePath`（可选）和 `ExecutableName`（可选） 的新 `GameConfig` 模型。
  * **数据存储**: `GameHelper.Core/Services/CsvBackedPlayTimeService.cs`。**(已更新)** 以 CSV 格式持久化游戏时长。现在*必须*使用 `GameConfig.DataKey` 字段与 `playtime.csv` 中的 `GameName` 列进行关联读写。
  * **命令行接口**: `GameHelper.ConsoleHost/Commands/` 和 `Interactive/`。**(已更新)** 处理 CLI 命令和交互式会话。现在必须支持拖放添加游戏，以及处理包含 `DataKey` 和 `ExecutablePath` 的新配置格式。

## 5. Data Models and Storage

### 5.1. Data Models

  * **游戏配置**: `GameHelper.Core/Models/GameConfig.cs`。**(已更新)** 定义了用户在 `config.yml` 中配置的每个游戏条目。
      * `DataKey` (string, 必需): 关联 `playtime.csv` 的唯一标识符（例如 "Elden Ring"）。
      * `ExecutablePath` (string, 可选): 用于 L1 精确路径匹配的完整路径。
      * `ExecutableName` (string, 可选): 用于 L2 回退匹配的 .exe 名称。
      * `DisplayName` (string, 可选): 用于 UI 显示的友好名称。
  * **应用配置**: `GameHelper.Core/Models/AppConfig.cs`。(未更改) 定义了全局设置，如 `ProcessMonitorType`。

### 5.2. Data Storage

  * **游戏时长**: 数据存储在 `playtime.csv` 文件中。**(已更新)** 这是一个 CSV 文件，包含 `GameName`, `StartTime`, `EndTime`, `DurationSeconds` 等字段。`GameName` 列现在由 `GameConfig.DataKey` 填充和关联，以确保历史数据的连续性。

## 6. 功能排序与依赖

### 6.1. 功能依赖

| 顺序 | 工作项 | 阻断条件 | 验收门槛 | 负责人 |
| --- | --- | --- | --- | --- |
| 1 | 故事 1.1（数据模型与配置加载） | 未完成 DataKey/ExecutablePath 扩展，则混合匹配及 UI 迭代不得进入开发 | `GameConfig` 与 `YamlConfigProvider` 通过单元测试，并完成对旧 `config.yml` 的手动验证 | 架构负责人 |
| 2 | 故事 1.2（核心匹配逻辑） | 未合并 1.1 输出的模型改动，阻断服务层实现；FuzzySharp 依赖未还原 | `GameAutomationService` 新增单元测试通过，进程监控集成测试在 Windows 环境 green | 后端负责人 |
| 3 | 故事 1.3（CLI/交互式 UI） | 1.2 未完成导致缺少新的匹配结果事件；Steam 解析器未注册 | 交互式 Shell 手动脚本（参考 docs/history/CLI_Manual_zh.md）跑通，并通过 QA 验收清单 | CLI/体验负责人 |
| 4 | 故事 1.4（数据服务关联） | 1.2、1.3 未提供稳定的 DataKey 输出，无法验证 CSV 写入 | `CsvBackedPlayTimeService` 回归测试（含 CSV 初始化与数据追加）通过；`stats` 命令展示符合预期 | 数据负责人 |

> 备注：史诗以阻断链的形式推进，任何条目的验收失败会阻止后续故事进入开发阶段，直至责任人完成补救并更新依赖表。

### 6.2. 技术依赖与发布门槛

1.  **配置/模型先行**: 所有涉及混合匹配的代码变更必须在 `GameConfig` 与 `YamlConfigProvider` 更新合入主分支后才能启动，以避免旧配置在功能分支中产生破坏性差异。
2.  **监控服务与匹配逻辑联动**: 在 `ProcessMonitorFactory` 中启用新的匹配服务前，需要确认 ETW/WMI 路径均已运行 `dotnet test`，并在 Windows 环境下执行一次 `dotnet run -- monitor` 观察事件流是否按 L1/L2 顺序传递。
3.  **发布前置清单**:
    * 完成 `CsvBackedPlayTimeService` 的回归（见 10.3 节）并输出操作日志。
    * 由架构负责人出具“混合匹配发布”验收结论，确认配置、服务、UI、数据四条链路的依赖均已满足。
    * QA 负责人确认 CLI/交互式脚本的回归通过，才能触发 GitHub Release 工作流。

## 7. Technical Debt and Known Issues

1.  **HDR 功能**: `IHdrController` 的实现 `NoOpHdrController` 是一个空操作。如 `README.md` 所述，实际的 HDR 自动切换功能因技术挑战而暂缓实现。
2.  **进程过滤 (已更新状态: 明确推迟)**: 原始架构 指出，当前的 WMI/ETW 监控 会监听所有进程，然后在 `GameAutomationService` 中进行过滤，并建议未来进行优化。
      * **更新决策:** 此项优化（即在 WMI/ETW 级别 预先过滤）已被*明确推迟*。
      * **理由:** 为了支持新的 L2 回退匹配逻辑（使用文件元数据 和模糊匹配 来处理如 `cheatenginex8664sse4avx2.exe` 这样的变体名称），系统*必须*继续监听所有进程启动事件，以捕获它们的完整路径和名称。预过滤已被证实是不可靠的。我们将接受在服务级别 进行过滤所带来的性能开销。
3.  **存档复制**: 自动复制存档（如魂类游戏）的功能仍在计划中，尚未实现。
4.  **错误处理**: 尽管存在文件容错，但在某些边缘情况下，不正确的配置（例如 `DataKey` 缺失或与 `playtime.csv` 不匹配）或损坏的 CSV 文件 可能导致未经处理的异常。

## 7\. 变通方法和注意事项 (Gotchas)

  * **跨平台开发约束**:
      * **核心依赖**: 项目的核心功能（WMI, ETW）强依赖于 Windows API。
      * **非 Windows 环境开发**: 在 macOS 或 Linux 上进行开发时，大部分功能将无法运行。测试时需要有条件地跳过这些功能。`Program.cs` 中的 `NoOpProcessMonitor` 和 `NoOpAutoStartManager` 是在这种情况下使用的关键组件，可以通过 `--monitor-dry-run` 标志启用。
  * **ETW 监控权限**: `EtwProcessMonitor` 需要管理员权限才能运行。如果权限不足，`ProcessMonitorFactory` 会自动降级到 WMI，但这会增加进程事件的延迟。
  * **交互式命令行测试**: `GameHelper.Tests` 中的测试通过模拟用户输入来验证交互式命令行的行为。这使得测试很脆弱，如果 UI 布局或文本发生变化，测试可能会失败。

## 8. Development and Deployment

### 8.1. Local Development Setup

1.  克隆仓库。
2.  安装 .NET 8.0 SDK。
3.  执行 `dotnet restore` 以安装所有 NuGet 依赖。
4.  使用 Visual Studio 2022 或 VS Code 打开解决方案。
5.  运行 `GameHelper.ConsoleHost` 项目。
      * 在 Windows 上，可以直接运行所有功能。
      * 在 macOS/Linux 上，使用 `dotnet run -- --monitor-dry-run` 来跳过 Windows 特定的监控服务。

### 8.2. Build and Deployment Process

  * **构建命令**: `dotnet build`
  * **发布命令**: `README.md` 中提供了详细的 `dotnet publish` 命令，用于创建自包含的 Windows 可执行文件。
  * **自动化部署**: 项目包含一个位于 `.github/workflows/release.yml` 的 GitHub Actions 工作流。当一个形如 `v*` 的标签被推送到仓库时，它会自动构建项目，并将结果打包上传到 GitHub Release。

### 8.3. 回滚与应急预案

| 集成点 | 回滚策略 | 应急验证 |
| --- | --- | --- |
| 进程监控 (ETW/WMI) | 在 `appsettings` 中切换 `ProcessMonitorType=Wmi`，或回滚至上一稳定分支 tag（例如 `release/2025.10`）。 | 使用既有 `EndToEndProcessMonitoringTests` 与手动触发测试进程确认事件仍被捕获。 |
| 配置加载 (`YamlConfigProvider`) | 暂时将 `config.yml` 的新字段置为可选并启用 `--monitor-dry-run`，如需恢复旧行为，可回滚到 `YamlConfigProvider` 先前提交。 | 导入历史 `config.yml` 与 `playtime.csv`，执行 `dotnet run -- config list` 验证字段解析。 |
| 数据存储 (`CsvBackedPlayTimeService`) | 切换到 `FileBackedPlayTimeService`（回退服务）或回滚至 `data-key` 引入前版本。 | 使用测试脚本写入/读取既有 CSV，核对 `GameName` 列是否保留旧值。 |
| CLI/交互式 Shell | 若新命令导致问题，可恢复到先前 CLI 版本并保留 `InteractiveShell` 的旧构建，同步关闭新命令的入口。 | 运行 `GameHelper.Tests` 中交互式测试并手动执行关键命令（monitor/config/stats）。 |

### 8.4. CLI 用户旅程（含异常分支）

| 场景 | 用户步骤 | 系统期望行为 | 常见异常与提示 |
| --- | --- | --- | --- |
| **初次配置并启动监控** | 1) 运行 `dotnet run --project .\GameHelper.ConsoleHost --` 进入交互式 Shell；2) 选择“配置”面板，使用 `config add` 或拖放 EXE/LNK 创建游戏条目；3) 返回主面板启动 `monitor`。 | Shell 展示新增游戏，监控启动后进入实时摘要视图；会话结束后自动写入 `playtime.csv` 并弹出新增记录摘要。 | *缺少管理员权限*: Shell 显示 WMI 订阅失败并建议以管理员重试；*config 缺少 DataKey*: `YamlConfigProvider` 拦截并在界面提示补齐字段。 |
| **拖放 Steam 快捷方式** | 1) 在交互式 Shell 提示下拖放 `.lnk` 或 `steam://` 快捷方式；2) 确认解析出的路径与 `ProductName`；3) 保存条目并继续监控。 | `SteamGameResolver` 解析目标 EXE，Shell 预填 `ExecutablePath` 与建议 `DataKey`；确认后将条目写入 `config.yml` 并刷新列表。 | *解析失败*: 显示“无法解析快捷方式”并回退到手动输入路径；*重复 DataKey*: Shell 阻止保存并提示选择重命名或覆盖。 |
| **查看与导出统计** | 1) 在 Shell 或命令行运行 `dotnet run --project .\GameHelper.ConsoleHost -- stats`; 2) （可选）指定 `--game` 过滤。 | CLI 按 `DataKey` 聚合并渲染 Spectre 表格；提供 CSV 导出路径说明。 | *playtime.csv 缺失*: 自动创建空文件并提示“尚无会话记录”；*CSV 损坏*: Shell 报告恢复动作并记录日志，建议用户备份损坏文件。 |
| **验证配置合法性** | 1) 在 Shell 中选择“配置校验”或运行 `dotnet run --project .\GameHelper.ConsoleHost -- validate-config`; 2) 根据提示修正。 | 输出 YAML 结构错误、缺失字段或重复键的位置；成功时提示“一切就绪”。 | *文件锁定*: 提示关闭占用该文件的编辑器；*语法错误*: 指出行号并建议对照 `config.template.yml`。 |

> 参考资料：更详尽的命令示例位于 `docs/history/CLI_Manual_zh.md`，流程对应的故事验收标准记录在 `docs/prd/2-史诗和故事.md`。

## 9. Testing Status

### 9.1. Current Test Coverage

  * **单元测试**: 使用 xUnit 和 Moq 对核心逻辑和服务进行测试。
  * **集成测试**: `ProcessMonitorIntegrationTests.cs` 等文件测试了监控器与系统事件的集成。
  * **端到端测试**: `EndToEndProcessMonitoringTests.cs` 验证了从进程启动到时间记录的完整流程。
  * **已知差距 (已更新)**:
      * 交互式命令行的测试依赖于输入模拟，覆盖范围有限且较为脆弱。
      * **新差距**: 需要为 `GameAutomationService` 中新的 2 级混合匹配策略（L1 路径 和 L2 元数据/模糊回退）添加专门的单元测试。
      * **新差距**: 需要为新的拖放添加游戏功能 添加测试。

### 9.2. Running Tests

```bash
dotnet test
```

### 9.3. 旧功能回归策略

* **进程监控回归**: 在 Windows 11 环境中分别以管理员和普通权限运行 `dotnet run -- monitor`，观察 WMI 与 ETW 的自动降级路径，确保历史版本依赖的 WMI 监听仍可用。
* **配置与数据兼容性**: 使用历史 `config.yml` 与 `playtime.csv` 进行回放测试：执行 `dotnet run -- config list` 核对 `DataKey` 映射是否保持；运行一次模拟游戏会话验证 CSV 追加不破坏旧记录。
* **CLI/交互功能**: 复用 `GameHelper.Tests` 的交互式测试套件，并补充一次手动脚本（参考 `docs/history/CLI_Manual_zh.md`）执行既有命令序列，确认输出未发生破坏性变更。
* **回归节奏**: 在每个功能迭代的冲刺结束前，执行上述回归检查并记录结果。如发现异常，按照 8.3 节的回滚路径恢复后，再推进修复。

#### 9.3.1. 签字与记录流程

1.  QA 负责人复核 9.3 节的回归脚本执行结果，填写 `docs/history/Regression_Signoff_Log_zh.md` 中当期条目，并在日志中附上测试数据或脚本链接。
2.  架构负责人确认关键链路（配置、匹配、数据、CLI）均按日志通过后，在同一条目下签名并备注是否需要额外补救措施。
3.  PO 根据日志结论决定是否允许发布日期进入发布流程；若驳回，需在日志附带原因并分配后续修复负责人。
4.  所有签字完整的条目需同步摘要到 `docs/history/Bug_Fix_Summary_zh.md`，确保历史版本知识库可追溯。

## 10. Appendix - Useful Commands

(源自 `README.md`)

```powershell
# Start interactive command line
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
