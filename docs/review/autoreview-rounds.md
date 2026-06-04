# AutoReview 审查记录

用于记录 autoreview 技能运行过程中的发现、决策和修复。

## 第 0 轮：前置说明

- 审查引擎：codex（默认已修改）
- 排除范围：WinForm 相关代码（GameHelper.WinUI 仍审查）
- 决策标签：
  - [FIXED] — 已修复
  - [REJECTED] — 拒绝修复（附理由）
  - [ACCEPTED] — 接受为已知问题（暂不修复）

---

## 第 1 轮：HEAD 提交审查（初始运行）

**命令：**
`powershell
python .agents\skills\autoreview\scripts\autoreview --mode commit --commit HEAD --stream-engine-output 2>&1
`

**结果：** utoreview clean: no accepted/actionable findings reported

**overall：** patch is correct (0.95)

**发现：** 未发现可接受/可执行的问题。Codex 确认之前的 3 个修复（MonitorControlService 状态一致性、ETW 缓存线程安全、PID 重用 stale path）均正确。

---

## 第 2 轮：autoreview 技能修复验证

**问题：** autoreview 技能在调用 codex 引擎时，Codex CLI 的 OpenTelemetry SDK 输出大量 telemetry warnings 到 stderr，淹没了审查输出。

**修复：**
1. 修改 .agents/skills/autoreview/scripts/autoreview：
   - 为 un_with_heartbeat 和 un_with_stream 添加 env 参数
   - 在两个 subprocess.Popen 调用中传递 env=env
   - 在 un_codex 调用 un_with_heartbeat 时传入 env={**os.environ, "OTEL_SDK_DISABLED": "true"}
   - 在 CodexStreamDisplay.__call__ 中过滤 stderr 的 telemetry 输出行
2. 重新将默认引擎设为 codex。

**验证：** 第 3 轮 autoreview 运行全程无 telemetry warnings 输出，审查体验正常。

---

## 第 3 轮：main 分支全量代码审查

**命令：**
`powershell
python .agents\skills\autoreview\scripts\autoreview --mode branch --base origin/main --prompt "Review the ENTIRE GameHelper codebase..."
`

**结果：** utoreview findings: 3

| # | 优先级 | 标题 | 文件 | 决策 | 理由 |
|---|--------|------|------|------|------|
| 1 | P1 | EtwProcessMonitor path cache leaks entries when stop events are disabled | EtwProcessMonitor.cs:229 | [FIXED] | 当前 diff 引入。_stopEventsEnabled=false 时 OnProcessStop 提前返回，缓存条目无界累积，导致内存泄漏。 |
| 2 | P2 | EtwProcessMonitor Start() retry path orphans partial session state | EtwProcessMonitor.cs:91 | [FIXED] | 当前 diff 引入。资源耗尽重试路径中，旧 _session 实例未被 dispose 即被覆盖，导致句柄泄漏。 |
| 3 | P2 | MonitorControlService.Stop() lacks symmetric monitor rollback | MonitorControlService.cs:55 | [FIXED] | 当前 diff 引入。Stop() 异常时未对称回滚 monitor 状态，导致服务契约不一致。 |
| - | P3 | ProcessMonitorFactory specific COMException handler is now dead code | ProcessMonitorFactory.cs:85 | [REJECTED] | 这是已有代码的问题，不是当前 diff 引入；autoreview 自身也标记为 out-of-scope。 |

**overall：** patch is incorrect (0.85)

**修复详情：**

1. **Fix P1 - 缓存泄漏：** OnProcessStart 中改为 if (!string.IsNullOrWhiteSpace(realPath) && _stopEventsEnabled)，仅在 stop 事件启用时缓存路径。禁用期间退出的进程不会留下 stale entry。
2. **Fix P2 - Session orphan：** Start() 的资源耗尽 catch 块中，在 CleanupStaleSessions() 之前调用 SafeCleanup()，确保部分初始化的 _session 被 deterministically dispose。
3. **Fix P3 - Stop 不对称：** Stop() 的 catch 块中，先尝试 _monitor.Stop()，再尝试 _automationService.Stop()，与 Start() 的回滚逻辑对称。

**构建/测试结果：** dotnet build GameHelper.sln 0 错误；dotnet test GameHelper.sln 217 通过，0 失败。

---

## 第 4 轮：修复验证

**命令：**
`powershell
python .agents\skills\autoreview\scripts\autoreview --mode branch --base origin/main --prompt "Review the ENTIRE GameHelper codebase..."
`

**结果：** （待运行）
