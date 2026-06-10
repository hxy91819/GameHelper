# InteractiveShell 拆分重构规划

> **目标**：将 `InteractiveShell`（1903 行）拆分为专职模块。
> **范围**：`GameHelper.ConsoleHost/Interactive/` 及测试。
> **原则**：同 GameAutomationService 拆分——零风险渐进式，旧壳子保留做转发，测试全绿才推进。

---

## 一、现状分析

### 1903 行中的 6 大职责区域

| 区域 | 行数范围 | 方法/逻辑 | 问题 |
|------|---------|-----------|------|
| **PromptUI 基础设施** | 1418-1747 | `PromptSelection`、`Prompt`、`PromptConfirm`、`WaitForMenuReturn`、`RenderNumberedChoices`、`TrySelectByNumber`、`TryReadKey`、`TryReadKeyIfAvailable`、`NumberedChoice` record | ~330 行 UI 基础设施，与业务无关 |
| **监控运行时** | 226-360 | `LaunchMonitorAsync`、`WaitForMonitorExitAsync`、`WaitForExitByPromptAsync`、`IsQuitCommand`、`RenderMonitorHistory`、`RenderSessionSummary`、`CaptureSessionSnapshot`、`SessionSnapshot`/`SessionRecord`/`SessionKey` | ~200 行进程生命周期管理 |
| **配置 CRUD** | 811-1141 | `HandleConfigurationAsync`、`RenderConfigTable`、`AddGameAsync`、`EditGameAsync`、`RemoveGameAsync`、`LoadConfigs`、`PersistAsync` | ~330 行，AddGame 包含元数据提取、DataKey 生成、快捷方式解析等业务逻辑 |
| **统计展示** | 1143-1232 | `ShowStatistics`、`TryLoadPlaytimeData` | ~90 行，包含 Linq 聚合和格式化 |
| **工具与诊断** | 1234-1344 | `HandleTools`、`ConvertLegacyConfig`、`ValidateCurrentConfig` | ~110 行，`ValidateCurrentConfig` 直接 `new YamlConfigProvider()` 绕过 DI |
| **设置管理** | 501-809 | `HandleSettingsAsync`、`ConfigureMonitorAutoStartAsync`、`ConfigureSystemAutoStartAsync`、`TryReadSystemAutoStartState` | ~310 行 |

### 测试现状

| 测试类 | 测试数 | 覆盖范围 |
|--------|-------|---------|
| `InteractiveShellTests` | 10 | 查看配置、添加游戏、设置自启动、显示统计、监控启动/DryRun、自动启动监控 |

### 测试缺口

- [ ] Prompt 基础设施无独立测试（`PromptSelection` 的脚本注入、数字键盘选择逻辑）
- [ ] `ShowStatistics` 的 Linq 聚合逻辑无独立测试
- [ ] `AddGameAsync` 的复杂分支（快捷方式解析、元数据提取、重复检测）无安全网
- [ ] `ConvertLegacyConfig` / `ValidateCurrentConfig` 无测试（工具方法）

---

## 二、目标架构

```
InteractiveShell (~500 行，编排层)
├── IPromptUI → PromptUI (~330 行)
│   ├── PromptSelection（脚本注入 + 数字键盘选择）
│   ├── Prompt（基础 TextPrompt 包装）
│   ├── PromptConfirm
│   ├── WaitForMenuReturn
│   ├── RenderNumberedChoices
│   └── NumberedChoice
├── IGameCatalogUI → GameCatalogUI (~330 行)
│   ├── RenderConfigTable
│   ├── AddGameAsync（保留，但简化 —— 业务逻辑委托给 Core）
│   ├── EditGameAsync
│   └── RemoveGameAsync
├── IStatisticsUI → StatisticsUI (~90 行)
│   └── ShowStatistics（聚合逻辑保留）
├── IMonitorUI → MonitorUI (~200 行)
│   ├── LaunchMonitorAsync
│   ├── WaitForMonitorExitAsync
│   ├── RenderMonitorHistory
│   └── RenderSessionSummary
├── IToolsUI → ToolsUI (~110 行)
│   ├── ConvertLegacyConfig
│   └── ValidateCurrentConfig
└── ISettingsUI → SettingsUI (~310 行)
    ├── ConfigureMonitorAutoStartAsync
    └── ConfigureSystemAutoStartAsync
```

---

## 三、拆分步骤

### Step 0：补充安全网测试

操作：
1. 新建 `GameHelper.Tests/Interactive/InteractiveShellRefactorSafetyTests.cs`
2. 覆盖以下场景：
   - `PromptSelection` 脚本注入（`InteractiveScript.TryDequeue` 返回预定义值）
   - `PromptSelection` 数字键盘选择（`TrySelectByNumber`）
   - `AddGameAsync` 的快捷方式解析分支（.lnk → .exe）
   - `ShowStatistics` 的 Linq 聚合（TOTAL 行、DisplayName 解析）
   - `ValidateCurrentConfig` 的 YAML 校验（正/反面）
3. 全量测试确认：260/260 通过
4. Commit

### Step 1：拆 PromptUI 基础设施

操作：
1. 新建 `GameHelper.ConsoleHost/Interactive/PromptUI.cs`（~330 行）
2. 包含：`PromptSelection`、`Prompt`、`PromptConfirm`、`WaitForMenuReturn`、`RenderNumberedChoices`、`TrySelectByNumber`、`TryReadKey`、`TryReadKeyIfAvailable`、`NumberedChoice` record
3. `InteractiveShell` 保留薄转发层
4. 编译 + 测试 → 全绿 → Commit

### Step 2：拆 SessionSnapshot 辅助类型

操作：
1. 新建 `GameHelper.ConsoleHost/Interactive/SessionSnapshot.cs`
2. 包含：`SessionSnapshot`、`SessionRecord`、`SessionKey` records
3. 保留原有行为，不改变任何字段
4. 编译 + 测试 → 全绿 → Commit

### Step 3：拆 MonitorUI（监控运行时）

操作：
1. 新建 `GameHelper.ConsoleHost/Interactive/MonitorUI.cs`（~200 行）
2. 包含：`LaunchMonitorAsync`、`WaitForMonitorExitAsync`、`WaitForExitByPromptAsync`、`IsQuitCommand`、`RenderMonitorHistory`、`RenderSessionSummary`
3. `CaptureSessionSnapshot` 保留在 InteractiveShell（它需要访问 `_configProvider` 和 `TryLoadPlaytimeData`）
4. 编译 + 测试 → 全绿 → Commit

### Step 4：拆 StatisticsUI

操作：
1. 新建 `GameHelper.ConsoleHost/Interactive/StatisticsUI.cs`（~90 行）
2. 包含：`ShowStatistics`
3. 保留 `TryLoadPlaytimeData`（多个模块需要它，作为公共服务保留或移到独立类）
4. 编译 + 测试 → 全绿 → Commit

### Step 5：拆 GameCatalogUI（配置 CRUD）

操作：
1. 新建 `GameHelper.ConsoleHost/Interactive/GameCatalogUI.cs`（~330 行）
2. 包含：`HandleConfigurationAsync`、`RenderConfigTable`、`AddGameAsync`、`EditGameAsync`、`RemoveGameAsync`
3. `LoadConfigs` 和 `PersistAsync` 保留在 InteractiveShell 作为公共服务（或提取为 `ConfigPersistenceHelper`）
4. 编译 + 测试 → 全绿 → Commit

### Step 6：拆 ToolsUI

操作：
1. 新建 `GameHelper.ConsoleHost/Interactive/ToolsUI.cs`（~110 行）
2. 包含：`HandleTools`、`ConvertLegacyConfig`、`ValidateCurrentConfig`
3. **重点**：`ValidateCurrentConfig` 中 `new YamlConfigProvider()` → 改为接收 `IConfigProvider`
4. 编译 + 测试 → 全绿 → Commit

### Step 7：拆 SettingsUI

操作：
1. 新建 `GameHelper.ConsoleHost/Interactive/SettingsUI.cs`（~310 行）
2. 包含：`HandleSettingsAsync`、`ConfigureMonitorAutoStartAsync`、`ConfigureSystemAutoStartAsync`、`TryReadSystemAutoStartState`
3. 编译 + 测试 → 全绿 → Commit

### Step 8：清理旧壳子，提取公共服务

操作：
1. 提取 `LoadConfigs`/`PersistAsync`/`TryLoadPlaytimeData`/`ResolveDisplayName`/`FormatTimestamp`/`CaptureSessionSnapshot` 到 `InteractiveHelpers` 或保留在 Shell
2. 删除 `InteractiveShell` 中所有转发层方法
3. 清理未使用的 using
4. 编译 + 测试 → 全绿 → Commit

---

## 四、安全检查清单（每步执行）

```
□ dotnet build GameHelper.sln —— 编译通过
□ dotnet test GameHelper.sln —— 260+ 测试全绿
□ git diff --stat —— 改动范围可控
□ git commit -m "Step X: 描述"
```

---

## 五、回滚策略

- 每步独立 commit，粒度小
- 若某步搞崩 → `git revert HEAD` 回退该 commit
- 若已多步推进 → `git reset --hard <上一步commit>` 回退到安全状态

---

**规划制定日期**：2026-06-10
**基准**：main 已包含 GameAutomationService 拆分成果
