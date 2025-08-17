# 当前工作总结（架构、进展、计划、待办）

## 架构与分层
- __Core (`GameHelper.Core/`)__：抽象与核心服务
  - 抽象：`IProcessMonitor`、`IHdrController`、`IConfigProvider`、`IPlayTimeService`、`IGameAutomationService`
  - 模型：`Models/GameConfig`
  - 服务：`Services/GameAutomationService`（编排：监控进程事件、控制 HDR、记录会话）
- __Infrastructure (`GameHelper.Infrastructure/`)__：具体实现
  - 进程监控：`Processes/WmiProcessMonitor`（WMI 真实监控，Windows-only）
  - HDR：`Controllers/NoOpHdrController`
  - 配置：`Providers/JsonConfigProvider`（兼容旧格式字符串数组，写回新格式）
  - 游玩时长：`Providers/FileBackedPlayTimeService`（与 WPF `playtime.json` 兼容）
- __ConsoleHost (`GameHelper.ConsoleHost/`)__：宿主进程 + CLI
  - `Program.cs`：注册 DI、解析命令（monitor/config/stats）
  - `Worker.cs`：启动/停止 `IGameAutomationService` 与 `IProcessMonitor`
- __UI (`GameHelper/`)__：保留 WPF 旧项目，后续按需迁移到 Web/Electron/JS
- __Tests (`GameHelper.Tests/`)__：xUnit 测试集合

## 近期改动摘要
- 新增：`IGameAutomationService` + `GameAutomationService`，并接入 `Worker`
- 新增：Console CLI
  - monitor（默认）、config list/add/remove、stats [--game]
- 修复：`Program.cs` 中本地类定义导致的 C# 语法错误（将 DTO 移至文件顶层）
- 测试适配：`WorkerTests` 增加 `FakeAutomation` 并断言 Start/Stop
- 包依赖：Core 引入 `Microsoft.Extensions.Logging.Abstractions`
- 新增：`GameAutomationServiceTests` 覆盖单/多游戏、禁用游戏、Stop 取消订阅等场景（待运行确认）

## 文件与格式
- 配置：`%AppData%/GameHelper/config.json`
  - 新版：`{"games": [{"Name":"xxx.exe","IsEnabled":true,"HDREnabled":true}]}`
  - 旧版：`{"games": ["xxx.exe"]}`（读取时兼容，保存统一写新版）
- 时长：`%AppData%/GameHelper/playtime.json`
  - 根键 `games`，每项含 `GameName` 与 `Sessions[{ StartTime, EndTime, DurationMinutes }]`

## 运行与验证
- 运行监控：`dotnet run --project GameHelper.ConsoleHost -- monitor`
- 管理配置：`dotnet run --project GameHelper.ConsoleHost -- config list|add <exe>|remove <exe>`
- 查看统计：`dotnet run --project GameHelper.ConsoleHost -- stats [--game <name>]`
- 测试：`dotnet test GameHelper.sln -c Debug`（新增自动化服务测试已提交，待运行确认）

## Notes
- 监控使用 `WmiProcessMonitor`（WMI 事件 `Win32_ProcessStartTrace/StopTrace`）。需要 Windows 且 WMI 服务可用，建议以管理员权限运行。
- Core/Infra/Console 已解耦，后续 UI 可独立替换。
- 所有字典键采用大小写不敏感比较，避免 exe 名大小写差异。

## Plan（短期）
1. __完善自动化服务测试__：补充异常流程、重复事件去重、重复 Stop/Start 容错等边界用例。
2. __增强 CLI__：
   - stats 支持按周/月/年聚合（先实现周/月）。
   - config 支持批量导入/导出（为 YAML 铺路）。
3. __YAML 支持（可选）__：`YamlDotNet`，`config.yml` 导入/导出或替代存储。

## TODOs
- __[中]__ 完善 `GameAutomationServiceTests` 边界用例（异常流程、重复事件、并发顺序）
- __[中]__ CLI 的 `config`/`stats` 单元测试（临时目录隔离文件）
- __[低]__ stats 按时间范围聚合；展示最近游玩时间
- __[低]__ YAML 配置导入/导出

## 风险与缓解
- 进程监控对权限/WMI 服务依赖：提供 `NoOp` 回退与错误日志；在文档中提示需开启 WMI 服务
- 文件并发写入：目前在 `FileBackedPlayTimeService` 内部使用锁并在 Stop 时写入；后续监控进程并发下需注意
- 配置/时长文件损坏：实现已具备容错；保留最小可运行策略

## 里程碑
- M1：Console CLI + 自动化服务（已完成）
- M2：WMI 进程监控 + 自动化服务测试（进行中/下一步）
- M3：统计增强 + CLI 测试 + YAML（后续）

## 小变更记录

- 2025-08-16 14:01：新增 GameAutomationServiceTests；更新 Plan/TODOs。
- 2025-08-17 11:18：实现并接入 `WmiProcessMonitor`，ConsoleHost 目标框架调整为 `net8.0-windows`，Infra 引入 `System.Management`；新增 `IProcessEventWatcher` 以提升可测性，并添加 `WmiProcessMonitorTests`。
