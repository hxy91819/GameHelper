# 当前工作总结（架构、进展、计划、待办）

## 架构与分层
- __Core (`GameHelper.Core/`)__：抽象与核心服务
  - 抽象：`IProcessMonitor`、`IHdrController`、`IConfigProvider`、`IPlayTimeService`、`IGameAutomationService`
  - 模型：`Models/GameConfig`
  - 服务：`Services/GameAutomationService`（编排：监控进程事件、控制 HDR、记录会话）
- __Infrastructure (`GameHelper.Infrastructure/`)__：具体实现
  - 进程监控：`Processes/WmiProcessMonitor`（WMI 真实监控，Windows-only）
  - HDR：`Controllers/NoOpHdrController`（占位：当前不切换系统 HDR，后续将重实现）
  - 配置（运行时）：`Providers/YamlConfigProvider`（仅 YAML，支持 `Alias`）
  - 配置校验：`Validators/YamlConfigValidator`（基于内置模板 `Validators/config.template.yml` 动态校验）
  - 迁移工具：`Providers/JsonConfigProvider`（仅供 `convert-config` 一次性迁移使用）
  - 游玩时长：`Providers/FileBackedPlayTimeService`（与 WPF `playtime.json` 兼容）
- __ConsoleHost (`GameHelper.ConsoleHost/`)__：宿主进程 + CLI
  - `Program.cs`：注册 DI、解析命令（monitor/config/stats/convert-config/validate-config）
    - `config` 为可选/遗留工具，推荐直接编辑 YAML
  - `Worker.cs`：启动/停止 `IGameAutomationService` 与 `IProcessMonitor`
- __UI (`GameHelper/`)__：WPF 旧项目已从解决方案移除（保留源码目录以备参考）；后续按需迁移到 Web/Electron/JS
- __Tests (`GameHelper.Tests/`)__：xUnit 测试集合

## 近期改动摘要
- 新增：`IGameAutomationService` + `GameAutomationService`，并接入 `Worker`
- 新增：Console CLI
  - monitor（默认）、config list/add/remove、stats [--game]、convert-config、validate-config
- 修复：`Program.cs` 中本地类定义导致的 C# 语法错误（将 DTO 移至文件顶层）
- 测试适配：`WorkerTests` 增加 `FakeAutomation` 并断言 Start/Stop
- 包依赖：Core 引入 `Microsoft.Extensions.Logging.Abstractions`
- 新增：`GameAutomationServiceTests` 覆盖单/多游戏、禁用游戏、Stop 取消订阅等场景（待运行确认）
- 新增：基于模板的 YAML 校验器与 `validate-config` 命令（支持未知字段警告、必填/类型校验、重复 name 检测）

- ## 文件与格式
- 配置（仅 YAML）：`%AppData%/GameHelper/config.yml`（支持 `Alias`）
- 旧配置迁移：提供一次性命令 `convert-config` 将 `%AppData%/GameHelper/config.json` 转换为 `config.yml`；运行时不再读取 JSON（`JsonConfigProvider` 仅供迁移使用）。
- 时长：`%AppData%/GameHelper/playtime.json`
  - 根键 `games`，每项含 `GameName` 与 `Sessions[{ StartTime, EndTime, DurationMinutes }]`

## 运行与验证
- 运行监控：`dotnet run --project GameHelper.ConsoleHost -- monitor`
- 配置：直接编辑 `config.yml`（不再支持 JSON 运行时读取）
- 查看统计：`dotnet run --project GameHelper.ConsoleHost -- stats [--game <name>]`
- 校验配置：`dotnet run --project GameHelper.ConsoleHost -- validate-config`
- 测试：`dotnet test GameHelper.sln -c Debug`（当前通过 22/22）

## Notes
- 监控使用 `WmiProcessMonitor`（WMI 事件 `Win32_ProcessStartTrace/StopTrace`）。需要 Windows 且 WMI 服务可用，建议以管理员权限运行。
- HDR 控制：当前实现为 NoOp（不执行实际开关），仅用于编排占位；后续将引入真实的 HDR 切换实现（可能基于 Windows API/驱动/辅助进程）。
- `validate-config` 使用嵌入模板（`Validators/config.template.yml`）推导字段与类型；后续仅需维护模板即可。
- Core/Infra/Console 已解耦，后续 UI 可独立替换。
- 所有字典键采用大小写不敏感比较，避免 exe 名大小写差异。

## Plan（短期）
1. __完善自动化服务测试__：补充异常流程、重复事件去重、重复 Stop/Start 容错等边界用例。
2. __增强 CLI__：
   - stats 支持按周/月/年聚合（先实现周/月）。
3. __YAML-only + 模板校验 已完成__：补充更多 YAML 示例。
4. __打包发布__：提供单文件可执行的发布说明并产物签名（按需）。
5. __移除 WPF 项目（待确认）__：从解决方案中移除 `GameHelper/` 项目，保留源码归档。

## TODOs
- __[高]__ 寻找HDR切换功能更优的实现方案，能够在程序启动之前执行。

## 风险与缓解
- 进程监控对权限/WMI 服务依赖：提供 `NoOp` 回退与错误日志；在文档中提示需开启 WMI 服务
- 文件并发写入：目前在 `FileBackedPlayTimeService` 内部使用锁并在 Stop 时写入；后续监控进程并发下需注意
- 配置/时长文件损坏：实现已具备容错；保留最小可运行策略

## 里程碑
- M1：Console CLI + 自动化服务（已完成）
- M2：WMI 进程监控 + 自动化服务测试（已完成）
- M3：统计增强 + CLI 测试（进行中）；YAML 配置（已完成，改为 YAML-only）

## 小变更记录

- 2025-08-16 14:01：新增 GameAutomationServiceTests；更新 Plan/TODOs。
- 2025-08-17 11:18：实现并接入 `WmiProcessMonitor`，ConsoleHost 目标框架调整为 `net8.0-windows`，Infra 引入 `System.Management`；新增 `IProcessEventWatcher` 以提升可测性，并添加 `WmiProcessMonitorTests`。
- 2025-08-17 11:46：引入 `Alias` 字段；新增 `YamlConfigProvider` 与 `AutoConfigProvider`（优先 YAML）；`JsonConfigProvider` 兼容读写 `Alias`；`Program.cs` 改为注册 `AutoConfigProvider`；文档更新为推荐直接编辑配置文件。
- 2025-08-17 11:42：测试全绿（26/26）。修复 ConsoleHost `Program.cs` 中 `RunConfigCommand` 的签名问题；解决测试中 `GameConfig` 命名冲突与可空性警告；补充 `GamePlayTimeManagerTests` 的 `IDisposable` 与反射空值断言；更新文档与 TODO。
- 2025-08-17 12:10：新增模板驱动的 `YamlConfigValidator` 与 `validate-config` 命令；修订文档。
- 2025-08-17 12:41：README 与中文文档更新：标注 HDR 为 NoOp（待重实现）、调整测试统计为 22/22、补充运行说明。
