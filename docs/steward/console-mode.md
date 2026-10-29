# GameHelper 主机模式：手柄选游戏与启动、手柄支持标记

更新：2026-10-29；逐项状态、结果与材料见[要求帐](console-mode-要求帳.md)。

## 路线

- [x] 阶段 2：GitHub 现成组件调研，结论见 [调研](../research/console-mode-survey.md)，main b49f174
- [ ] 阶段 3a：主机模式 HTML 原型（两种布局），交用户选 ← 进行中 thr_vpbyiw8nhh
- [ ] 阶段 4a：手柄输入层（SDL3-CS + Playnite 桥接，Xbox/PS4/PS5），先接入现有游戏库 ← 进行中 thr_4fvyca827s
- [ ] 阶段 5：手柄支持检索（Steam appdetails）、缓存与"不支持"标记 ← 进行中 thr_zjvmwn2n3w
- [ ] 阶段 4b：全屏主机模式窗口与进出入口 ← 排在 阶段 3a 后：用户选定的布局
- [ ] 阶段 6：Windows 实机验收（USB/蓝牙、DS4Windows/Steam Input 共存）← 排在 阶段 4b 后：合入 main 的主机模式

## 关键决策

- 2026-10-29 本目标沿用管家编排：改动经 BB 子线程派发，管家验收后直接合并 main，Windows 实机验证合并后检查。原因：沿用 desktop-and-capabilities 约定。影响：全部阶段。
- 2026-10-29 不引用 xerj（已撤回，试验留在分支 feat/xerj-search）。原因：用户确认它不能在 GitHub 发现方案。影响：阶段 2。
- 2026-10-29 现成能力优先以组件（NuGet/库）引入，不行再以代码引入。原因：用户原话。影响：阶段 2–5。
- 2026-10-29 输入用 SDL3-CS（关增强报告）+ Playnite MIT 桥接；手柄数据只接 Steam appdetails，Steam 未标注显示为不支持、无 AppID 为未知、手动设置优先。原因：调研结论，可撤回。影响：阶段 4–5。

## 用户待办

无
