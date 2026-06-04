# 6. 功能排序与依赖

> 本节保留当前架构依赖关系，原史诗推进过程已完成，详细故事材料见 `docs/archives/stories/`。

## 6.1. 功能依赖

| 顺序 | 工作项 | 阻断条件 | 验收门槛 | 负责人 |
| --- | --- | --- | --- | --- |
| 1 | 配置模型与持久化 | `DataKey` / `ExecutablePath` 缺失将阻断上层匹配与统计 | `YamlConfigProvider`、`StatisticsService`、迁移命令通过回归测试 | Core / Infra |
| 2 | 进程匹配与监控 | 没有稳定的 L1/L2 匹配与 ETW/WMI 回退，监控链路无法上线 | `GameAutomationService`、`ProcessMonitorFactory` 在 Windows 环境验证通过 | Core / Infra |
| 3 | Shell 入口（CLI / WinUI） | 若共享服务契约变化未同步，壳层会出现行为漂移 | CLI 指南、WinUI 设计说明、关键 Smoke 测试保持一致 | Console / WinUI |
| 4 | 统计与迁移 | 未保持 `playtime.csv` 与 `config.yml` 的兼容，会导致历史数据不可用 | `stats`、`migrate`、CSV 回归测试通过 | Core / Console |

> 备注：后续新增功能应沿用以上依赖顺序，先改核心契约，再改监控与数据，最后同步到 CLI / WinUI。

## 6.2. 技术依赖与发布门槛

1.  **配置/模型先行**: 所有涉及混合匹配的代码变更必须在 `GameConfig` 与 `YamlConfigProvider` 更新合入主分支后才能启动，以避免旧配置在功能分支中产生破坏性差异。
2.  **监控服务与匹配逻辑联动**: 在 `ProcessMonitorFactory` 中启用新的匹配服务前，需要确认 ETW/WMI 路径均已运行 `dotnet test`，并在 Windows 环境下执行一次 `dotnet run -- monitor` 观察事件流是否按 L1/L2 顺序传递。
3.  **发布前置清单**:
    * 完成 `CsvBackedPlayTimeService` 的回归（见 10.3 节）并输出操作日志。
    * 由架构负责人出具“混合匹配发布”验收结论，确认配置、服务、UI、数据四条链路的依赖均已满足。
    * QA 负责人确认 CLI/交互式脚本的回归通过，才能触发 GitHub Release 工作流。
