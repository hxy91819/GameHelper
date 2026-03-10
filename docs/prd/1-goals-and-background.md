# 1. 目标和背景

## 核心目标

1. 优先使用 `ExecutablePath` 做精确匹配，降低同名进程歧义。
2. 对缺少 `ExecutablePath` 的旧配置，保留基于 `ExecutableName` 的回退匹配能力。
3. 用 `DataKey` 统一配置、统计和历史数据关联。
4. 通过 CLI / 交互式 Shell / 文件拖放降低游戏录入成本。
5. 以 ETW 作为默认监听方式，在权限不足时自动回退到 WMI。

## 当前范围结论

上述目标已在当前版本中实现并进入维护阶段，对应交付记录见 `docs/archives/`。

## 明确推迟

- 监控层预过滤优化仍然推迟。系统继续监听全量进程，并在 `GameAutomationService` 中完成过滤与匹配。
- 额外的新能力探索，如倍速相关方案，不在当前 PRD 范围内，保留在 `docs/plans/`。

