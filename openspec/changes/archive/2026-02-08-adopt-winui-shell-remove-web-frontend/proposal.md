## Why

当前 `ConsoleHost + Minimal API + Next.js` 的临时 Web 方案增加了运行与发布复杂度（前后端构建、静态资源打包、端口与运行模式协调），并与项目的长期方向（Windows-only、原生桌面体验、简洁可维护）不一致。现在需要收敛到 WinUI 3 + .NET 8 原生桌面架构，并清理临时 Web/Next.js 代码路径，降低系统复杂度并提高后续 AI 协作开发稳定性。

## Goals

- 建立 WinUI 3 原生桌面壳，作为面向用户的主交互入口。
- 复用并强化 Core/Infrastructure 业务能力，确保 UI/CLI 共享逻辑。
- 移除临时 Web 前端及内嵌 Web Server 运行路径，简化发布与运维。
- 建立可执行的分层测试策略（单元/集成/UI smoke）与 CI 分层门禁。

## Non-goals

- 不引入 Rust/Node 作为桌面主进程。
- 不在本次变更中实现跨平台桌面运行时支持（运行时仍为 Windows-only）。
- 不追求大规模 UI 自动化覆盖；仅建立关键流程 smoke 方案。

## Migration Strategy

1. 先抽离并稳定 Core 接口边界（业务逻辑与系统调用解耦）。
2. 再引入 WinUI 3 壳并接入核心功能（优先覆盖关键用户路径）。
3. 在 WinUI 功能达到等价可用后，移除 Web/Next.js 代码与相关发布链路。
4. 通过 staged rollout 控制风险：预发布验证 -> 小范围验证 -> 默认切换。

## What Changes

- 新增 WinUI 3 桌面壳（主入口），采用 MVVM + 依赖注入，明确 UI 与业务边界。
- 将现有业务能力统一收敛到 Core/Infrastructure，可被 WinUI 与 CLI 复用。
- **BREAKING**：移除 `GameHelper.Web`（Next.js）及其运行依赖，移除 `--web/--port` 相关产品级运行路径。
- **BREAKING**：调整发布产物结构，发布流程从“Console + Web 静态资源”切换为“WinUI 原生桌面应用（可附带 CLI）”。
- CI 与测试策略升级：
  - 单元/集成测试作为主质量门禁。
  - UI 自动化采用 FlaUI-first，GitHub hosted Windows 仅跑轻量 smoke；完整桌面 UI 套件在 self-hosted Windows interactive runner 执行。

## Capabilities

### New Capabilities
- `windows-desktop-shell`: WinUI 3 原生桌面壳能力（导航、设置、统计展示、应用生命周期管理）。
- `layered-core-contracts`: 强制分层契约能力（UI/Core/Infrastructure/CLI 边界、依赖方向、系统调用接口化）。
- `desktop-ui-automation`: Windows 桌面 UI 自动化基线能力（FlaUI smoke + CI 分层执行策略）。

### Modified Capabilities
- `web-api`: 从“对外产品入口能力”调整为“迁移过渡能力”，并在切换后移除其运行时职责。
- `web-dashboard`: 标记为弃用并移除，由原生桌面壳能力替代。

## Impact

- 受影响层与边界：
  - UI 层：新增 WinUI 3 项目；移除/替代 Next.js 页面与组件。
  - Core 层：补齐接口契约与应用服务边界，避免 UI 直接耦合 Infrastructure。
  - Infrastructure 层：保留并强化 WMI/ETW/存储实现，通过接口供 Core 调用。
  - CLI 层：保留为可选壳，复用 Core 逻辑作为自动化与回归入口。
- 受影响代码与目录：`GameHelper.Web/`、`GameHelper.ConsoleHost/Api/*`、参数解析与发布脚本、CI workflows。
- 架构复杂度：运行时复杂度下降（减少 Web 运行路径与前端构建依赖），迁移期短期复杂度上升（双栈并行）。
- 外部依赖变化：减少 Node/Next.js 运行时依赖，增加 WinUI 3 桌面壳相关依赖与工具链。
- 测试与 CI 影响：新增 Windows 桌面 smoke 任务；跨平台 runner 继续承担 Core 逻辑与工具链回归。
- 发布与回滚：
  - 发布：新增 WinUI 打包与签名流程。
  - 回滚：保留 CLI 作为后备入口；在切换窗口内保留最小可恢复分支与构建模板。
