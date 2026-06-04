## Context

当前实现以 `ConsoleHost + Minimal API + Next.js` 为主，属于“可用但临时”的过渡方案。该方案在开发阶段需要双栈工具链（.NET + Node.js），在发布阶段需要额外处理前端静态资源打包与运行模式切换。项目未来已明确为 Windows-only，并且希望降低架构复杂度、提高 AI 协作稳定性与可测试性。

本设计将前端入口收敛为 WinUI 3 桌面壳，同时保留 Core/Infrastructure 为业务与系统能力中心，CLI 作为可选壳继续复用核心逻辑。

### Target Architecture

```text
┌─────────────────────────────────────────────────────────────┐
│                 GameHelper.WinUI (Windows UI Shell)        │
│      MVVM + DI, no domain logic duplication in View layer  │
└───────────────┬─────────────────────────────────────────────┘
                │ calls application services (interfaces)
┌───────────────▼─────────────────────────────────────────────┐
│                     GameHelper.Core                         │
│  domain rules / use cases / orchestration / contracts      │
│  (must not reference WinUI/WPF/Web frameworks)             │
└───────────────┬─────────────────────────────────────────────┘
                │ depends on abstractions only
┌───────────────▼─────────────────────────────────────────────┐
│                 GameHelper.Infrastructure                   │
│   WMI/ETW/Win32 adapters, config/store providers, IO       │
└───────────────┬─────────────────────────────────────────────┘
                │
                ▼
         Windows OS APIs + Filesystem

(Optional)
GameHelper.ConsoleHost (CLI Shell) -> GameHelper.Core (same contracts)
```

依赖方向固定为：`UI/CLI -> Core -> Infrastructure`，禁止反向引用。

## Goals / Non-Goals

**Goals:**
- 用 WinUI 3 构建原生桌面壳，替代临时 Web 前端作为主入口。
- 保持“一套核心逻辑，多壳复用”（WinUI + CLI）。
- 清理 Web/Next.js 运行路径与构建链路，降低整体复杂度。
- 形成分层测试体系：单元/集成为主，桌面 UI smoke 为辅。
- 让非 Windows 环境仍能验证核心逻辑（mock/fake 驱动）。

**Non-Goals:**
- 不引入 Rust/Node 主进程。
- 不在本次变更实现跨平台桌面运行时。
- 不构建大规模像素级 UI 自动化回归。

## Decisions

### D1: UI 主壳采用 WinUI 3 + .NET 8

**Decision:** 新建 WinUI 3 应用项目作为主交互壳。  
**Alternatives considered:**
- 继续使用 Next.js 内嵌 WebView2
- 迁移到 Electron / Tauri

**Rationale:**
- 与 Windows-only 目标一致，系统集成能力最直接。
- 复用 .NET 生态，避免新主进程语言栈带来的运维/调试复杂度。
- 运行时链路简化（不再依赖 Node 前端服务路径）。

### D2: 业务逻辑强制收敛到 Core（UI 不含业务规则）

**Decision:** 所有用例逻辑在 Core；WinUI 使用 ViewModel 调用应用服务接口。  
**Alternatives considered:**
- 在 UI 层编写页面专属业务逻辑

**Rationale:**
- 保证 CLI 与 WinUI 行为一致性。
- 最大化单元测试覆盖，降低 UI 自动化负担。
- 提升 AI 协作可读性（边界清晰、上下文稳定）。

### D3: Windows 系统能力全部接口化

**Decision:** WMI/ETW/Win32 调用经 `Core.Abstractions` 暴露接口，再由 Infrastructure 提供实现。  
**Alternatives considered:**
- 在 Core 或 UI 直接调用系统 API

**Rationale:**
- 保持 Core 的平台中立可测性。
- 非 Windows 环境通过 Fake/Mock 实现执行逻辑测试。
- 降低未来替换底层实现成本。

### D4: Web/Next.js 与 ConsoleHost API 路径按阶段下线

**Decision:** 迁移期保留最小兼容路径，达到 WinUI 功能等价后删除 `GameHelper.Web` 与 `ConsoleHost/Api/*` 产品路径。  
**Alternatives considered:**
- 长期双前端并存

**Rationale:**
- 双栈长期并存会增加维护成本和行为漂移风险。
- 目标是收敛复杂度，不是保留临时形态。

### D5: UI 自动化采用 FlaUI-first，分层执行

**Decision:**
- 桌面 UI 自动化主工具：FlaUI。
- Hosted Windows runner 仅跑 deterministic smoke。
- Full UI 套件放在 self-hosted Windows（interactive desktop）。

**Alternatives considered:**
- Appium/WinAppDriver 作为主方案
- 在 CI 全量跑 UI E2E

**Rationale:**
- FlaUI 更贴近 .NET 工作流，调试链路短。
- 桌面 UI 自动化天然易抖动，必须控制范围与执行环境。

## Risks / Trade-offs

- **[Risk] 迁移期双壳并行导致行为不一致**  
  → **Mitigation:** 用例逻辑集中在 Core；关键流程建立回归测试（CLI + Core integration）。

- **[Risk] WinUI 团队经验不足导致交付速度波动**  
  → **Mitigation:** 先覆盖核心场景；统一 MVVM 模板与组件规范；限制一次性重构范围。

- **[Risk] UI 自动化不稳定影响 CI 可信度**  
  → **Mitigation:** smoke 最小集合、AutomationId-first、固定测试数据、仅在合适 runner 跑全量。

- **[Risk] 一次性删除 Web 路径导致回滚困难**  
  → **Mitigation:** 按阶段下线；在切换窗口保留 CLI 后备入口与快速回滚分支。

- **[Trade-off] 放弃 Web 端复用能力**  
  → **Mitigation:** 明确产品目标是 Windows native，优先本地体验与维护简洁度。

## Migration Plan

### Phase 0: Core contract hardening
- 盘点并固定 Core 接口（配置、统计、监控控制、系统能力抽象）。
- 清理 UI/API 对 Infrastructure 的直接依赖。
- 建立核心回归测试基线（跨平台可跑）。

### Phase 1: WinUI shell introduction
- 新建 WinUI 项目，落地主导航与关键页面（Dashboard/Settings/Games/Stats）。
- 接入 Core 用例接口，完成关键用户路径等价。
- 引入桌面 UI smoke（FlaUI）并接入 Windows CI。

### Phase 2: Web path removal and release switch
- 移除 `GameHelper.Web` 与 `ConsoleHost/Api/*` 产品运行路径。
- 清理 `--web/--port` 参数与相关文档、脚本、pipeline。
- 切换发布链路到 WinUI 产物（CLI 可选附带）。

### Rollback
- 在 Phase 2 切换窗口内保留 CLI 后备入口。
- 通过 release channel 保留上一稳定版可回退。

## Open Questions

- WinUI 打包策略最终选择：MSIX、Unpackaged，还是双通道？
- UI 自动化执行基座是否立即引入 self-hosted Windows runner，还是先 hosted smoke？
- CLI 在最终产品中的定位：默认可见入口，还是仅高级/诊断模式？
- 需要保留多长时间的 Web 迁移兼容窗口（按版本还是按日期）？
