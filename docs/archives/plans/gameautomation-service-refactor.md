# GameAutomationService 拆分重构规划

> **目标**：把 `GameAutomationService`（1080 行，6 项杂活混在一个类里）拆成独立的专职模块。
> **范围**：仅涉及 `GameHelper.Core` 和 `GameHelper.ConsoleHost`，排除 WinUI。
> **原则**：零风险渐进式拆分，旧壳子保留做转发，测试全绿才推进。

---

## 一、现状

| 指标 | 数值 |
|------|------|
| `GameAutomationService.cs` 总行数 | 1080 行 |
| 公共方法数 | 3 个（`Start` / `Stop` / `ReloadConfig`） |
| `private` / `private static` 方法数 | 20+ 个 |
| 当前测试数（直接相关） | 3 个测试类，共约 1120 行 |
| 当前测试通过率 | 218/218 全绿 ✅ |

### 混在一起的 6 项杂活

1. **路径归一化**（~200 行）：`NormalizePath`、`TryResolveWindowsPath`、`TryResolveDrivePath`、`TryResolveUncPath`
2. **名称归一化**（~20 行）：`NormalizeName`
3. **游戏匹配引擎**（~150 行）：`MatchByPath`、`MatchByMetadata`、`CalculateFuzzyThreshold`、`IsSystemPath`、`IsPathRelated`、`TryGetProductName`
4. **活跃会话追踪**（~120 行）：`RegisterActive`、`ReleaseActive`、`TryResolveActive`、`RemoveEntry`
5. **HDR 调度**（~20 行）：`UpdateHdrState`
6. **格式化辅助**（~20 行）：`FormatDuration`

### 已有测试覆盖

| 测试类 | 覆盖范围 |
|--------|---------|
| `GameAutomationServiceTests` | 生命周期、HDR 切换、计时、Reload、重复名称检测 |
| `GameAutomationPlaytimeIntegrationTests` | 端到端会话持久化 |
| `FuzzyMatchingSafetyTests` | 动态阈值、路径相关性、系统黑名单、旧配置兼容、回归场景 |

### 测试缺口（重构前补充）

- [ ] L1 精确路径匹配的独立单元测试
- [ ] 多进程引用计数（同一 DataKey 启动多次再停止）
- [ ] `NormalizePath` 的边界情况（UNC 路径、驱动器路径尾部 `\`、相对路径）
- [ ] `IsPathRelated` 的边界情况
- [ ] 歧义消解（多个候选同分时路径相关性裁决）

---

## 二、目标架构

```
GameAutomationService（精简到 ~250 行，只做编排）
├── IPathNormalizer        → PathNormalizer（~200 行）
├── IGameMatcher           → GameMatcher（~150 行）
│   ├── IFuzzyThresholdCalculator
│   ├── ISystemPathFilter
│   └── IPathRelatednessValidator
├── ISessionTracker        → SessionTracker（~120 行）
├── IHdrScheduler          → HdrScheduler（~20 行）
└── 公用：FormatDuration   → TimeFormatting（~20 行）
```

---

## 三、拆分步骤（严格顺序执行）

### Step 0：补充测试缺口（安全网织密）

**操作**：
1. 在 `GameHelper.Tests` 新建 `GameAutomationServiceRefactorSafetyTests.cs`
2. 补充上述 5 个缺口的测试用例
3. 跑 `dotnet test --filter "GameAutomation"`，确认全绿
4. Commit：`git add . && git commit -m "Step 0: 补充 GameAutomationService 重构前安全网测试"`

**验收标准**：
- 新增测试全部通过
- 原有 218 个测试仍然全绿
- 新增测试不依赖反射（为重构后字段变动做准备）

---

### Step 1：拆纯函数（零风险，先练手）

#### 1.1 `FormatDuration`

**操作**：
1. 新建 `GameHelper.Core/Utilities/TimeFormatting.cs`
2. 把 `FormatDuration` 原封不动搬进去
3. `GameAutomationService` 里改成：
   ```csharp
   private static string FormatDuration(TimeSpan duration)
   {
       return TimeFormatting.FormatDuration(duration);
   }
   ```

**验证**：
```bash
dotnet build GameHelper.sln
dotnet test GameHelper.sln --filter "GameAutomation"
```

**验收标准**：全绿。Commit。

#### 1.2 `NormalizeName`

**操作**：同上，放入 `PathNormalizer.cs`。

**验证**：同上。

#### 1.3 `NormalizePath` + `TryResolveWindowsPath` + `TryResolveDrivePath` + `TryResolveUncPath` + `IsWindowsDrivePath` + `IsWindowsUncPath`

**操作**：
1. 新建 `GameHelper.Core/Utilities/PathNormalizer.cs`
2. 把上述方法原封不动搬进去
3. `GameAutomationService` 里改成薄转发层
4. 同时把 `GameHelper.Infrastructure/Providers/` 中 `JsonConfigProvider` 和 `YamlConfigProvider` 的 `NormalizeLoadedConfig` / `NormalizeForSave` 中对路径的处理，后续统一使用 `PathNormalizer`

**注意**：这一步只搬家，不替换 Provider 中的调用（那是另一个 Step）。

**验证**：
```bash
dotnet build GameHelper.sln
dotnet test GameHelper.sln
```

**验收标准**：全绿。Commit。

---

### Step 2：拆 `GameMatcher`（有副作用但无并发）

**操作**：
1. 新建 `GameHelper.Core/Services/GameMatcher.cs`（`internal` 类）
2. 把 `MatchByMetadata`、`CalculateFuzzyThreshold`、`IsSystemPath`、`IsPathRelated`、`TryGetProductName` 搬进去
3. `MatchByPath` 也搬进去（它是纯字典查找）
4. `GameAutomationService` 里注入 `IGameMatcher`，旧方法变成转发层：
   ```csharp
   private GameConfig? MatchByPath(string? normalizedPath)
   {
       return _gameMatcher.MatchByPath(normalizedPath, _configsByPath);
   }
   ```
5. `LoadAndBuildIndexes` 中构建 `_nameConfigs` 的逻辑，目前为 `GameMatcher` 提供候选列表。重构后保持现状（`GameMatcher` 接收 `NameConfigEntry[]` 参数）。

**风险点**：
- `FileVersionInfo.GetVersionInfo` 可能抛异常 → 原代码有 `try/catch`，搬家时**原封不动搬**
- `Fuzz.Ratio` 调用 → 直接搬

**验证**：
```bash
dotnet build GameHelper.sln
dotnet test GameHelper.sln
```

**验收标准**：全绿。Commit。

---

### Step 3：拆 `SessionTracker`（有锁有状态，最危险，放最后）

**操作**：
1. 新建 `GameHelper.Core/Services/SessionTracker.cs`（`internal` 类）
2. 把三个字典 + `_stateLock` + `RegisterActive`/`ReleaseActive`/`TryResolveActive`/`RemoveEntry` 整体搬进去
3. **不优化并发**：保留 `lock` + `Dictionary`，不改 `ConcurrentDictionary`
4. `GameAutomationService` 里注入 `ISessionTracker`

**过渡安全机制（双跑验证）**：

重构后过渡期内，`GameAutomationService` 同时调用新旧实现，对比结果：

```csharp
// 过渡期代码（最多保留 1 个 commit，验证后删除）
private bool RegisterActive(string dataKey, string? name, string? path)
{
    var old = OldRegisterActive(dataKey, name, path);
    var neu = _sessionTracker.Register(dataKey, name, path);
    if (old != neu)
        _logger.LogError("SessionTracker 双跑不一致！old={Old}, new={New}", old, neu);
    return neu;
}
```

运行测试 + 手动场景（如果有条件），确认无 `LogError` 后，下一 commit 删除旧代码。

**验证**：
```bash
dotnet build GameHelper.sln
dotnet test GameHelper.sln
```

**验收标准**：全绿 + 双跑无 Error 日志。Commit。

---

### Step 4：拆 `HdrScheduler`

**操作**：
1. 新建 `GameHelper.Core/Services/HdrScheduler.cs`（`internal` 类）
2. 把 `UpdateHdrState` 的判断逻辑搬进去
3. `GameAutomationService` 里注入 `IHdrScheduler`

**验证**：同上。全绿。Commit。

---

### Step 5：清理旧壳子

**操作**：
1. 删除 `GameAutomationService` 中所有已搬走的 `private` 方法（转发层）
2. 确认 `GameAutomationService` 精简到 ~250 行，只剩：
   - 构造函数（注入新模块）
   - `Start()` / `Stop()` / `ReloadConfig()`
   - `OnProcessStarted()` / `OnProcessStopped()`（编排代码，调用各模块）
3. 确认内部不再有任何 `private static` 工具方法

**验证**：
```bash
dotnet build GameHelper.sln
dotnet test GameHelper.sln
```

**验收标准**：全绿。`GameAutomationService` < 300 行。Commit。

---

### Step 6：提取接口（可选，看需求）

如果后续需要让 ConsoleHost 的命令也能复用这些模块（比如 `MigrateCommand` 使用 `IGameMatcher`），在这一步给每个内部类提取公共接口：

- `IPathNormalizer`
- `IGameMatcher`
- `ISessionTracker`
- `IHdrScheduler`

注册到 DI 容器中。

**验证**：同上。Commit。

---

## 四、每一步的安全检查清单

```
□ dotnet build GameHelper.sln —— 编译通过
□ dotnet test GameHelper.sln —— 218+ 测试全绿
□ 新增测试覆盖本步拆出的模块
□ git diff --stat 确认改动范围可控（< 5 个文件优先）
□ git commit -m "Step X: 具体描述"
```

---

## 五、回滚策略

- 每步独立 commit，粒度小
- 若某步测试失败且 10 分钟内无法定位 → `git revert HEAD` 回退该 commit
- 若已推进多步才发现问题 → `git reset --hard <上一步commit>` 回退到已知安全状态
- 所有修改仅限 `GameHelper.Core` 和 `GameHelper.Tests`，不影响 Infrastructure 和 ConsoleHost 的其他命令

---

## 六、预期收益

| 模块 | 行数 | 可独立测试 | 可被复用 |
|------|------|-----------|---------|
| `GameAutomationService`（编排层） | ~250 | ✅（集成测试） | — |
| `PathNormalizer` | ~200 | ✅（纯函数，无需 Mock） | `MigrateCommand`、`ConfigProvider` |
| `GameMatcher` | ~150 | ✅（传入假数据即可测） | `MigrateCommand`（消除 638 行中的复制粘贴） |
| `SessionTracker` | ~120 | ✅（内存状态可断言） | — |
| `HdrScheduler` | ~20 | ✅ | — |
| `TimeFormatting` | ~20 | ✅ | 全局 |

---

## 七、后续可选项（本次规划不包含）

- `MigrateCommand` 复用 `IGameMatcher`（消除模糊匹配复制粘贴）
- `JsonConfigProvider` / `YamlConfigProvider` 复用 `IPathNormalizer`（消除归一化复制粘贴）
- `IStopEventsControl` 的消除（合并进 `IProcessMonitor` 或彻底留在 Infrastructure）
- 使用 `ConcurrentDictionary` 替代 `Dictionary` + `lock`（独立优化，不在本次拆分范围内）

---

**规划制定日期**：2026-06-10
**执行顺序**：Step 0 → Step 1 → Step 2 → Step 3 → Step 4 → Step 5 → Step 6
**每一步结束标准**：`dotnet test` 全绿 + commit
