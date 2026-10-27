# GameHelper 桌面端闭环、多档变速与游玩手帐演进

更新：2026-10-26

## 路线

- [x] 阶段 1：游玩档案手帐化与 GitHub SVG 玩家卡片交互原型试验（最近线程 thr_hnjudfj36y）
- [x] 阶段 2：CE 游戏内热键多档变速与一键原速切换（分支 feat/ce-speed-hotkeys，最近线程 thr_aqkyyrhcqu）
- [x] 阶段 3：按显示器指定 HDR 策略与 DDC 降级（分支 feat/per-display-hdr，最近线程 thr_6ipq7bi95p）
- [x] 阶段 4：WPF 桌面端闭环（全局设置页、系统托盘、DDC 向导、海报选中框修复与运行中动态光效，最近线程 thr_6gx8b3reeb、thr_n5vzzhtijx）
- [ ] 阶段 5：按阶段 1 试验反馈落地选中的游玩档案手帐能力与 GitHub 同步 SVG 卡片
- [x] 阶段 6：Linux 集成与独立代码审查，输出 Windows Agent 实机验收与部署提示词（分支 origin/feat/steward-integration，最近线程 thr_8djvx3mw6c）
- [x] 阶段 7：全局默认倍速热键（`cheatEngine.speedHotkeys` + 全局设置页编辑，未单独配置热键的倍速游戏继承；最近线程 thr_ct4fdsegmq）
- [x] 阶段 8：目录扫描记住最近目录一键重扫、默认只显示新游戏目录、按目录忽略（最近线程 thr_ct4fdsegmq）

## 关键决策

- 2026-10-26 演进主线定为“桌面端闭环 + CE 多档热键变速 + 按显示器 HDR + 游玩手帐试验”，不做单机存档位置猜测；旧 Console 交互界面不再维护，后续能力与界面完全围绕 WPF 演进。原因：用户确认以 WPF 为主、放弃维护 Console 界面，且存档路径难以自动识别。影响：全部阶段。
- 2026-10-26 游玩档案手帐化与 GitHub SVG 卡片先做单页交互原型试验，用户看效果拍板后再改正式代码。原因：用户希望先看试验效果。影响：阶段 1、阶段 5。
- 2026-10-26 HDR 自动化支持按显示器指定：当前仅连接被排除/未允许的显示器时，库内启动勾选 HDR 的游戏自动跳过 HDR（不报错阻断）并降级应用 DDC 游戏预设；接电视等允许显示器时正常开启 HDR。原因：用户显示器 HDR 效果差，仅在接电视时需开启 HDR。影响：阶段 3、阶段 4。
- 2026-10-26 倍速热键支持全局默认：游戏自己的非空 `speedHotkeys` 整体替换默认，不做逐键合并。原因：用户希望热键不必逐个游戏配置，且要在全局设置页可编辑；整体替换语义最少歧义。影响：阶段 7。
- 2026-10-27 目录扫描不做“扫过就跳过”的增量扫描：每次完整遍历，只在结果中收起已在库与忽略的目录。原因：常扫的游戏库目录正是新游戏出现的位置，目录修改时间也无法反映深层变化；对齐 Playnite/媒体库“保存目录 + 全量重扫 + 只突出新条目 + 排除列表”的做法。影响：阶段 8。
- 2026-10-26 系统托盘在 `ConsoleHost` 后台监控内以原生 Win32 `Shell_NotifyIconW` STA 消息泵实现，双击/菜单拉起 `GameHelper.Desktop`。原因：保持 `ADR-0002` 与 `verify-on-machine.ps1` 的 Desktop 引导即退及窗口恢复契约。影响：阶段 4。

## 用户待办

| 状态 | 事项 | 为什么只能用户做 | 挡住 | 提出 |
| --- | --- | --- | --- | --- |
| 待办 | 预览阶段 1 产出的游玩手帐与 SVG 卡片交互原型并拍板保留项 | 体验与产品方向取舍需用户确认 | 阶段 5 | 2026-10-26 |
| 待办 | 在 Windows 拉取 main，跑 `dotnet test GameHelper.sln` 并执行 `scripts\verify-on-machine.ps1 -SkipVerify -Restart` 部署阶段 7、8，核对全局设置页“默认倍速热键”与游戏内按键切档、扫描目录的最近目录重扫与忽略/收起 | 当前为 Linux，WPF 运行时与 CE 实机按键需 Windows | 阶段 7、8 部署 | 2026-10-26 |
| 已完成 | 将阶段 6 产出的实机验收提示词交给 Windows 端 Agent 执行验证与部署 | 当前为 Linux 环境，WPF 渲染截图与实机部署需 Windows | 无（2026-10-26 Windows Agent 完成：build 0 错误、925 测试全绿含 Strict mock 对齐修复 a9208cc、明暗快照与全局设置页人工核对通过、`verify-on-machine.ps1 -SkipVerify -Restart` 部署成功） | 2026-10-26 |
| 待办 | 在 Windows 拉取 main，跑 `dotnet test GameHelper.sln` 并执行 `scripts\verify-on-machine.ps1 -SkipVerify -Restart` 部署阶段 7、8，核对全局设置页“默认倍速热键”与游戏内按键切档、扫描目录的最近目录重扫与忽略/收起 | 当前为 Linux，WPF 运行时与 CE 实机按键需 Windows | 阶段 7、8 部署 | 2026-10-26 |
