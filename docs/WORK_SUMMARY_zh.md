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
 - 时长：当前版本为 JSON `playtime.json`；计划切换到 CSV `playtime.csv`（方便追加写入）
   - JSON（现有，WPF 兼容）：根键 `games`，每项含 `GameName` 与 `Sessions[{ StartTime, EndTime, DurationMinutes }]`
   - CSV（设计，拟）：文件路径 `%AppData%/GameHelper/playtime.csv`
     - 头部：`game,start_time,end_time,duration_minutes`
     - 行示例：`witcher3.exe,2025-08-16T10:00:00,2025-08-16T11:40:00,100`
     - 编码与格式：UTF-8，无 BOM；ISO-8601 本地时间；按 RFC 4180 规则在必要时使用双引号转义
     - 追加策略：每次 `StopTracking(game)` 仅写入一行，避免全量重写
   - 统计读取策略：将改为优先读取 CSV；若无 CSV 文件则回退读取 JSON（向后兼容）

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
 - 匹配与监听策略：
   - `Infrastructure/Processes/WmiProcessMonitor` 监听“全量进程”，不再在 WMI/WQL 层按配置白名单过滤（避免 Start/Stop 名称不一致导致漏报）。
   - `WmiEventWatcher` 基于事件 `ProcessID` 反查 `Win32_Process`，并维护短期 `PID -> Name` 缓存，Stop 失败时可复用。
   - `Core/Services/GameAutomationService` 在 Start/Stop 中执行 Normalize（仅文件名 + .exe）与 Stem（去扩展名/去标点/小写）以做精确+唯一模糊匹配，提升鲁棒性。

## Plan（短期）
1. __完善自动化服务测试__：补充异常流程、重复事件去重、重复 Stop/Start 容错等边界用例。
2. __游玩时长 CSV 存储改造__（设计完成，待实现）
   - 新增 `CsvBackedPlayTimeService`（实现 `IPlayTimeService`），在 `StopTracking` 时按行追加写入 `playtime.csv`；首次创建文件写入表头
   - 兼容读取：CLI `stats` 优先读取 CSV；若缺失则回退 JSON
   - 自动迁移：CSV 服务初始化时若发现仅有 JSON，执行一次性导入（逐会话展开写入）
   - 并发与原子性：独占写锁、`FileMode.Append` + `FileShare.Read`，每行写入 `\n` 结尾，失败重试与日志
   - 测试：新增 `CsvBackedPlayTimeServiceTests` 与 `stats` 针对 CSV 的用例；保留 JSON 测试作为回退路径验证
3. __增强 CLI__：
   - stats 支持按周/月/年聚合（先实现周/月）。
4. __YAML-only + 模板校验 已完成__：补充更多 YAML 示例。
5. __打包发布__：提供单文件可执行的发布说明并产物签名（按需）。
6. __移除 WPF 项目（待确认）__：从解决方案中移除 `GameHelper/` 项目，保留源码归档。

## TODOs
- __[高]__ 寻找HDR切换功能更优的实现方案，能够在程序启动之前执行。

## 风险与缓解
- 进程监控对权限/WMI 服务依赖：提供 `NoOp` 回退与错误日志；在文档中提示需开启 WMI 服务
 - 文件并发写入：JSON 方案内置锁并全量写入；CSV 方案将采用“独占写 + 逐行追加”的方式（`FileStream(FileMode.Append, FileAccess.Write, FileShare.Read)`），每条会话独立落盘，降低损坏面；仍保留进程内锁避免重复 Stop；遇到写入失败记录错误并在下次 Stop 重试
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
