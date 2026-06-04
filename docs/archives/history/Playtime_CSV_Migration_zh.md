# 游玩时长存储从 JSON 迁移到 CSV 方案（Playtime JSON -> CSV）

## 背景与目标
- 现状：`IPlayTimeService` 的实现 `FileBackedPlayTimeService` 在每次 `StopTracking(game)` 时将内存对象全量序列化为 `%AppData%/GameHelper/playtime.json`。这会带来：
  - 频繁重写整文件，I/O 开销较大
  - 任何部分写入失败可能导致整个文件损坏
- 目标：引入 CSV 逐行追加存储，使“一个会话（session）= 一行记录”，减少 I/O，提升可靠性，并便于外部分析工具直接读取。

## 设计概览
- 文件：`%AppData%/GameHelper/playtime.csv`
- 表头：
  ```text
  game,start_time,end_time,duration_minutes
  ```
- 行示例：
  ```text
  witcher3.exe,2025-08-16T10:00:00,2025-08-16T11:40:00,100
  ```
- 字段约定：
  - `game`：规范化的可执行文件名（例如 `witcher3.exe`），大小写不敏感
  - `start_time`/`end_time`：ISO-8601 本地时间（与现有 JSON 一致，便于兼容）；如未来需要可统一为 UTC
  - `duration_minutes`：向下取整的分钟数（与现有 JSON 一致）
- 编码：UTF-8（无 BOM）。必要时按 RFC 4180 使用双引号转义字段中的分隔符或引号。

## 写入与并发策略
- 写入时机：在 `StopTracking(game)` 事件触发时追加一行（仅写入完成的会话）。
- 文件模式：`FileMode.Append + FileAccess.Write + FileShare.Read`，保证统计读取不阻塞写入。
- 进程内锁：与现有实现一致，在服务内使用锁避免重复 `StopTracking` 造成的并发写入。
- 原子性：每条会话以“一行”为单位写入，尾部包含 `\n`；异常时记录日志并在下一次 `StopTracking` 重试，最大化降低损坏范围。

## 读取与统计策略（CLI stats）
- 优先读取 CSV：对 `playtime.csv` 按 `game` 分组聚合 `duration_minutes`，并基于 `config.yml` 的 `alias` 显示友好名称。
- 回退 JSON：若 CSV 不存在，则回退读取 `playtime.json`（与当前逻辑一致），保障过渡期可用。

## 迁移策略
- 自动迁移（首次运行 CSV 实现时）：
  - 条件：检测到 `playtime.csv` 不存在且 `playtime.json` 存在。
  - 行为：解析 JSON 的 `games[].Sessions[]`，逐会话展开为 CSV 多行写入；之后继续采用 CSV 追加策略。
  - 幂等性：仅在 CSV 不存在时触发，避免重复导入。
- 兼容期：
  - `stats` 命令优先读 CSV，回退 JSON；用户无感迁移。
- 回滚/回退：
  - 如需回退，可删除 `playtime.csv` 并继续使用 JSON；或保留两者，`stats` 仍会优先读取 CSV。

## 与 JSON 的差异与保留
- JSON：树形结构（按游戏聚合），写入需要全量重写。
- CSV：扁平结构（按会话逐行），天然适合追加与外部工具分析。
- 保留：统计逻辑与显示（含 `alias`）保持一致；持续支持读取 JSON（作为兼容回退）。

## 代码改动（规划）
- 新增：`GameHelper.Infrastructure/Providers/CsvBackedPlayTimeService.cs`
  - 实现 `IPlayTimeService`：
    - `StartTracking(game)`：记录开始时间（与现状一致）
    - `StopTracking(game)`：计算分钟数并向 CSV 追加一行；如文件不存在先写入表头
    - 初始化时执行一次性 JSON -> CSV 导入（若满足条件）
- DI 切换：`GameHelper.ConsoleHost/Program.cs`
  - 从 `FileBackedPlayTimeService` 切换为 `CsvBackedPlayTimeService`
  - 示例：`services.AddSingleton<IPlayTimeService, CsvBackedPlayTimeService>();`
- CLI 调整：`RunStatsCommand()`（`GameHelper.ConsoleHost/Program.cs`）
  - 新增 CSV 读取与聚合逻辑；不存在 CSV 时回退到现有 JSON 分支

## 测试计划
- 单元测试：`CsvBackedPlayTimeServiceTests`
  - 单会话写入生成文件与表头
  - 大小写不敏感的同名游戏多次写入
  - 连续 `Start/Stop` 多次追加
  - CSV 不存在但 JSON 存在时的首次导入
  - 部分写入/损坏行的容错（读取时跳过异常行）
- 集成测试：`stats` 在 CSV 存在/不存在两种场景下的聚合与显示（含 `--game` 过滤）

## 风险与缓解
- 写入中断导致尾行不完整：读取时跳过无法解析的行，并记录日志。
- 并发/多进程写入：当前架构为单进程服务，进程内锁 + `FileShare.Read` 足够；如未来多进程写入，需引入跨进程文件锁或日志聚合器。
- 时间格式差异：保持本地时间与现有一致，避免迁移期混淆；若后续统一 UTC，需在读写两侧同时切换并提供升级脚本。

## 实施步骤（Checklist）
1) 新增 `CsvBackedPlayTimeService` 并实现 CSV 逻辑（含 JSON -> CSV 首次导入）
2) 调整 DI：将 `IPlayTimeService` 绑定至 CSV 实现
3) 更新 `stats`：优先读 CSV，回退 JSON
4) 增加/调整测试：覆盖 CSV 写入与 stats 聚合；保留 JSON 回退测试
5) 文档已更新：
   - `docs/WORK_SUMMARY_zh.md` 增加 CSV 设计与计划
   - `docs/CLI_Manual_zh.md` 增加“即将变更（设计）：CSV 逐行追加存储”
6) 发布说明：在 README/Release Notes 标明“playtime.csv 上线、自动从 JSON 导入、stats 优先读 CSV”

## 版本与兼容性
- 最小影响升级：新版本上线后会自动生成 `playtime.csv` 并导入旧数据；旧 `playtime.json` 可保留以备回退。
- 版本识别：无需额外版本标记；以文件存在性与 CLI 分支决定行为。

## FAQ
- Q：需要手动执行迁移命令吗？
  - A：不需要。初次运行支持 CSV 的版本时会自动导入 JSON 数据。
- Q：未来是否提供手动转换命令？
  - A：如有需要，可新增 `convert-playtime` 命令进行显式转换与校验，但默认不必手动执行。
- Q：跨工具分析需要注意什么？
  - A：CSV 为 UTF-8，无 BOM；时间为 ISO-8601 本地时间；某些工具可能默认按逗号分隔并将日期作为文本读取，请注意格式设置。
