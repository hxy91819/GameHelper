# 2. 基础设施与部署（棕地）

## 2.1. 关键文件与入口 (Quick Reference)

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

## 2.2. 数据库与数据存储

* **当前状态**: 项目不依赖关系型数据库；所有会话数据持久化在 `%AppData%/GameHelper/playtime.csv` 中，文件结构遵循 `game,start_time,end_time,duration_minutes` 头部。
* **首次初始化流程**:
    1.  准备包含有效 `DataKey` 的 `config.yml`（可使用交互式 Shell `config add` 生成）。
    2.  执行 `dotnet run --project .\GameHelper.ConsoleHost -- monitor` 或在任何会话内触发 `StartTracking`，`CsvBackedPlayTimeService` 会自动创建目录并写入带表头的 CSV。
    3.  运行 `dotnet run --project .\GameHelper.ConsoleHost -- stats` 或直接检查文件确认生成成功。
* **验证策略**: 依托 `CsvBackedPlayTimeService` 的单元测试确保写入重试、JSON -> CSV 迁移及表头生成逻辑。上线前额外执行一次手动验证：用历史 `playtime.json` 启动 ConsoleHost，确认日志输出 `Migrating playtime data...` 且 CSV 内容与旧记录一致。
* **混合匹配数据迁移评估**:
    * 旧版本使用 `GameName` 直接写入 CSV。DataKey 引入后，映射策略为：继续把旧 `GameName` 写入 CSV `game` 列，新的匹配逻辑通过 `GameConfig.DataKey` 读写同一字段，保证追加记录不破坏历史数据。
    * 风险控制：在升级前使用脚本（示例位于 `GameHelper.Tests/Fixtures/PlayTimeSample`）生成 `GameName -> DataKey` 对照表；上线后对照运行 `dotnet run -- stats` 与 CSV 历史记录，确认未出现孤立的旧 `GameName`。

## 2.3. API 与服务配置

* **外部依赖**: 当前不调用任何外部 API 或微服务；CLI 直接与本地服务交互。文档中保持 N/A 说明以避免误判。
* **初始化顺序与依赖关系**:
    1.  `Program.cs` 创建 `HostApplicationBuilder`，注册 `YamlConfigProvider`、`CsvBackedPlayTimeService`、`ProcessMonitorFactory` 与 `GameAutomationService` 等依赖。
    2.  构建主机时，根据配置选择 ETW 或 WMI 监控实现，并通过 `ProcessMonitorFactory` 注入 `GameAutomationService`。
    3.  `Worker`（后台托管服务）在启动阶段解析配置、打开监控，再将事件流交给 `GameAutomationService`。
    4.  交互式 Shell 和 CLI 命令通过同一依赖注入容器解析服务，因此在运行 CLI 前应保证监控服务与配置提供器都已成功初始化。
* **部署注记**: 若需临时禁用 ETW，可在配置中设置 `ProcessMonitorType=Wmi`，主机按上述顺序自动完成降级，无需改动代码。

## 2.4. 测试基础设施

* **准备步骤**: 在任何测试运行前执行 `dotnet restore`（若已在开发环境步骤执行可跳过），随后运行 `dotnet build` 以确保生成的测试基线一致。
* **测试命令**: 所有测试通过 `dotnet test GameHelper.sln` 触发；Windows 环境可完整运行，非 Windows 环境需启用 `--monitor-dry-run` 相关测试配置。
* **回归既有功能计划**: 复用第 9.3 节列出的回归脚本，并在每次版本冻结前由架构负责人和 QA 负责人共同签署回归结果，形成冲刺检查点。
* **负责人**: 架构负责人 Winston 负责监控初始化与 CSV 兼容性验证，QA 负责人（当前为 QA 团队 lead）负责测试套件维护及回归记录归档。
