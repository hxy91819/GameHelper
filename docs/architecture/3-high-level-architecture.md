# 3. High-Level Architecture

## 3.1. 技术摘要

GameHelper 是一个基于 .NET 8 的 Windows 控制台应用程序，其核心是事件驱动的进程监控系统。它采用分层架构，将核心逻辑、基础设施实现和控制台主机分离开来。

**(已更新)** 应用现在支持一种**混合匹配策略**：
1.  **精确路径匹配 (L1):** 优先匹配 `config.yml` 中配置的 `ExecutablePath`。
2.  **回退匹配 (L2):** 对于仅配置了 `ExecutableName` 的条目，使用文件元数据 (`FileVersionInfo.ProductName`) 和模糊匹配 (FuzzySharp) 进行回退。

**(未更改)** 应用支持两种进程监控模式（WMI 和 ETW），并具有自动降级功能。性能优化（仅监控特定进程） 已被明确推迟，以支持 L2 回退匹配所需的“监听所有进程”架构。

## 3.2. 实际技术栈

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

## 3.3. 仓库结构

* **类型**: 单一仓库（Monorepo），包含多个 .NET 项目。
* **包管理器**: NuGet
* **显著特点**: 项目按功能分层（Core, Infrastructure, ConsoleHost, Tests），职责清晰。

## 3.4. 依赖管理策略与兼容性约束

1.  **安装顺序与早期准备**: 在克隆仓库后立即执行 `dotnet restore`，确保所有 NuGet 依赖在进入功能开发或运行测试前即已拉取完毕；随后再打开 IDE 或运行 `dotnet build`，可避免 IDE 在缺少包时反复触发还原。
2.  **包来源与锁定策略**: 依赖统一由 NuGet 提供，核心库版本（见上表）固定在 `.csproj` 文件中，合并请求在变更依赖版本时需同步更新本节表格与 `Directory.Build.props`。
3.  **兼容性验证**: 每次升级依赖前需在 Windows 11 生产等价环境中使用存量 `config.yml` 与 `playtime.csv` 执行一次 `dotnet test` 与 `dotnet run -- monitor`，核对进程监控、配置解析和播放时间写入是否保持一致，以确认对历史数据结构零破坏。
4.  **冲突与特殊要求**: 
    * `Microsoft.Diagnostics.Tracing.TraceEvent` 与 `System.Management` 必须保持与 .NET 8 SDK 发布分支兼容；若出现绑定冲突，优先升级 TraceEvent 到微软发布的最新补丁版本，并在 `GameHelper.Infrastructure` 中验证 ETW 回退逻辑。
    * `Spectre.Console` 与 `FuzzySharp` 均依赖本地化字符串处理；当引入其他字符串库时，需确认不会影响 Spectre 的 ANSI 输出或 FuzzySharp 的文化信息。
5.  **棕地兼容性记录**: 新版本 ConsoleHost 继续沿用原 WPF 时代的核心服务层。保留 `config.yml`（含旧字段）和 `playtime.csv` 的向后兼容性测试，每个里程碑需记录在 `docs/history/Bug_Fix_Summary_zh.md` 中，以确保对旧生产版本的兼容性审查有据可查。
