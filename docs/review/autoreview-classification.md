# AutoReview 问题分类：agents.md 强化 vs pre-commit

本文档将 5 轮 autoreview 发现的所有 actionable 问题按**可防范机制**分类，指导后续流程改进。

---

## 一、全部已修复问题清单

| # | 轮次 | 优先级 | 标题 | 根因类别 |
|---|------|--------|------|----------|
| 1 | R1 | P1 | MonitorControlService finally 无条件设 IsRunning=false | 异常安全/状态一致性 |
| 2 | R1 | P1 | ETW 缓存非线程安全（Dictionary） | 并发安全 |
| 3 | R1 | P1 | PID 重用 ContainsKey 守卫阻止覆盖 | 竞态条件/缓存一致性 |
| 4 | R3 | P1 | 缓存泄漏：stop 事件禁用时条目无界累积 | 资源生命周期/缓存管理 |
| 5 | R3 | P2 | Session orphan：重试路径覆盖 _session 未 dispose | 资源管理/确定性释放 |
| 6 | R3 | P2 | Stop() 缺乏对称 monitor 回滚 | 异常安全/状态一致性 |
| 7 | R4 | P1 | Stop 事件切换时缓存返回 stale path | 缓存生命周期/功能切换影响 |
| 8 | R4 | P2 | Stop() catch 块 cleanup 后未设 IsRunning=false | 异常安全/状态一致性 |

---

## 二、按防范机制分类

### A. 适合强化 agents.md（开发规范/设计约定）

这些问题本质上是**语义级设计缺陷**，无法通过简单的静态分析或格式化工具自动捕获，但可以通过在 `agents.md` 中增加明确的工程规范来避免。

#### A1. 服务生命周期与状态一致性（#1, #6, #8）

**问题模式：** Start/Stop 方法在异常路径中未能保持状态标志与底层服务实际状态一致。

**建议 agents.md 新增条款：**
```markdown
### 服务生命周期规范
- 所有实现 `Start()`/`Stop()` 的服务必须保持**状态对称**：
  - `Start()` 成功 → `IsRunning = true`
  - `Stop()` 成功或已执行 best-effort cleanup → `IsRunning = false`
  - **异常路径中不得提前返回而不更新状态标志**；catch 块完成 cleanup 后必须同步状态。
- Start/Stop 必须成对设计：如果 Start() 有回滚逻辑（失败时 Stop 已启动的子服务），Stop() 也必须有对称的 best-effort cleanup。
```

#### A2. 资源管理与确定性释放（#5）

**问题模式：** 重试逻辑中直接覆盖 `_session` 字段，旧实例未被 dispose，导致 ETW 会话句柄泄漏。

**建议 agents.md 新增条款：**
```markdown
### 资源管理规范
- 任何持有 IDisposable 字段的类型，在**重新赋值前必须先 dispose 旧实例**（或确保旧实例已被释放）。
- 重试逻辑（retry path）不得跳过旧资源的清理；必须在创建新资源前执行 `SafeCleanup()` 或等价操作。
- 使用 `SafeCleanup()` 模式：所有清理操作必须放在 try/catch 中，确保单点失败不阻断后续清理。
```

#### A3. 并发安全与共享可变状态（#2, #3）

**问题模式：** 跨线程共享的 `Dictionary<int, string>` 未加保护；PID 重用场景下 `ContainsKey` 守卫阻止了合法覆盖。

**建议 agents.md 新增条款：**
```markdown
### 并发安全规范
- 任何被**后台线程（ETW 回调线程、定时器线程、事件处理线程）**访问的可变状态，必须使用线程安全集合（`ConcurrentDictionary`、`ConcurrentQueue` 等）或显式同步原语。
- **禁止在并发写路径中使用 `ContainsKey` + `Add` 的组合**；优先使用原子操作（`TryAdd`、`TryRemove`、`TryUpdate`）。
- 缓存必须考虑 PID 重用场景：不得假设 PID 在缓存生命周期内唯一。
```

#### A4. 缓存生命周期与功能切换（#4, #7）

**问题模式：** `SetStopEventsEnabled(bool)` 切换功能开关时，未考虑对已有缓存的影响，导致 stale entry 泄漏或返回错误路径。

**建议 agents.md 新增条款：**
```markdown
### 缓存与功能切换规范
- 当功能开关（feature toggle）影响数据的**写入路径**时，必须同时审查对**读取路径和清理路径**的影响。
- 缓存的失效策略必须与功能生命周期绑定：功能禁用时，应评估是否需要主动清空或限制写入。
- 缓存键若来自操作系统回收资源（PID、句柄），必须假设**键空间会重用**，缓存条目必须及时清理。
```

---

### B. 适合增加 pre-commit（提交前自动化检查）

这些问题可以通过**提交前的自动化脚本**来捕获或降低发生概率。

#### B1. 强制构建 + 测试通过（已有，可强化）

`agents.md` 已要求 `dotnet build` 和 `dotnet test`，但 pre-commit hook 可以**强制执行**：

```yaml
# .pre-commit-config.yml 或 .git/hooks/pre-commit 示例
repos:
  - repo: local
    hooks:
      - id: dotnet-build
        name: dotnet build
        entry: dotnet build GameHelper.sln --no-restore
        language: system
        pass_filenames: false
        stages: [commit]
      - id: dotnet-test
        name: dotnet test
        entry: dotnet test GameHelper.sln --no-build
        language: system
        pass_filenames: false
        stages: [commit]
```

**价值：** 确保每位开发者在提交前本地验证通过，避免 broken commit 进入历史。

#### B2. 代码格式化（风格一致性）

```yaml
      - id: dotnet-format
        name: dotnet format
        entry: dotnet format GameHelper.sln --verify-no-changes
        language: system
        pass_filenames: false
```

**价值：** 避免纯格式问题（如第 5 轮 reject 的"多余空行"）进入 diff，减少 review 噪音。

#### B3. 文件编码与 BOM 检查

部分 diff 中出现了 UTF-8 BOM 变更（如 `using` 行前加 `﻿`），可以通过 pre-commit 的 `fix-byte-order-marker` 等 hook 统一。

---

## 三、不适合 agents.md 也不适合 pre-commit 的问题

| 问题 | 说明 | 建议替代方案 |
|------|------|-------------|
| autoreview 默认引擎配置 | 这是工具链/脚本配置问题，与代码规范无关 | 直接修改 `.agents/skills/autoreview/scripts/autoreview` 的默认值 |
| Codex CLI telemetry stderr 噪音 | 运行时环境问题 | 脚本中通过环境变量（`OTEL_SDK_DISABLED`）和 stderr 过滤解决 |
| ProcessMonitorFactory dead code | 已有遗留代码，非当前 diff 引入 | 单独创建清理任务，不在功能分支中处理 |

---

## 四、分类汇总

| 防范机制 | 覆盖问题 | 占比 |
|----------|----------|------|
| **强化 agents.md**（生命周期、资源、并发、缓存规范） | #1, #2, #3, #4, #5, #6, #7, #8 | **100% 语义级 bug** |
| **pre-commit**（构建、测试、格式化） | 风格问题、broken commit 预防 | 辅助性 |
| **直接脚本修改** | autoreview 引擎/telemetry | 2 个 |

### 结论

本轮审查发现的 **8 个核心 actionable bug 全部属于语义级设计缺陷**（状态一致性、资源生命周期、并发安全、缓存管理）。这类问题：

- **无法通过 pre-commit 的自动化工具（formatter、简单 linter）有效捕获**，因为测试通过的情况下它们依然可以存在（如 #1、#6、#8 的异常路径几乎不会被单元测试覆盖）。
- **最适合通过强化 `agents.md` 的开发规范来预防**。在 `agents.md` 中增加"服务生命周期"、"资源管理"、"并发安全"、"缓存与功能切换"四个专项规范，可以在设计阶段就引导开发者写出更健壮的代码。

**建议下一步：**
1. 在 `agents.md` 中新增上述 4 个规范章节。
2. 增加 pre-commit hook 强制 `dotnet build + dotnet test + dotnet format`，作为最后一道防线。
