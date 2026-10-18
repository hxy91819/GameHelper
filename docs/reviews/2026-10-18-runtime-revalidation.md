# 2026-10-18 近期功能二次复核与实机验证

范围：`fdaaaf2...8d1798c` 的 15 个提交及交接未提交修复；包括趋势图、性能、休息提醒、CE 调度、
WinUI 移除、配置热重载、显示器预设和跨重启计时。规范依据为 `AGENTS.md`、`CONTEXT.md`、
`docs/architecture/`；行为依据为对应功能指南及交接要求。以下记录已交付行为和实际验证结果。

## Standards

独立规范复核确认并修复：

- 定位重试在冷却未到时就将单飞标记置位，后续再也不启动任务。改为先检查冷却再占用标记。
- 显示器已可用状态永久缓存，写入失败却被当作完成。持续探测可用性、读回确认配方，失败保留重试状态。
- 多进程会话只在首进程加入/末进程退出时保存，遗漏中间的进程交接。每次进程变化和每 10 秒刷新全部身份。
- 部署会清理仍有活跃属主的 ETW、强杀未知路径实例，且新进程启动前就删掉备份。现只处理确认的目标和孤儿，
  备份保留至启动成功；失败恢复旧目录和旧实例。

这些均违反服务隔离、缓存生命周期或自愈约定；不是命名/风格推测。规范轴 4 类问题，均已修复。

## Spec

独立功能复核确认并修复：

- 「原开始时间续算」：接管进程登记到 ETW，启动时保留登记，正常退出和漏退出核对均可结算；
  接管后原子重写快照，连续两次重启仍保留原始起点。
- 「防止 PID 复用误认」：同时核对进程创建时间，防止同一 exe 重用旧 PID。老格式快照兼容校验创建时间早于快照。
- 「最后已知存活时间收尾落库」：恢复写入失败保留固定结束时间；同游戏多局分别补录；重复恢复去重；
  清理快照失败仅告警，不阻止启动。正常退出的 CSV 写失败也转为待补录快照。
- 「显示器回来自动恢复」：检查周期改为 10 秒，读回未确认则重试；热重载立即重评，停止后的排队 tick 不再写入。

另由真实发布产物复现：ETW 裸文件名被拼成宿主当前目录路径，导致名字配置的游戏重启误判为已退出。
已只接受完整事件路径，裸文件名保留为名字身份。验证时还确认一次性命令和隔离实例会改写 HKCU 开机启动项；
现一次性命令不应用启动偏好，数据沙盒使用无操作启动适配器。功能轴 4 类问题及 2 项实机追加发现，均已修复。

## 实机证据

### 显示器真实寄存器

主实例显示器自动化临时关闭，仅测试实例启用。使用真实 ETW 游戏探针、真实发布宿主、真实 DDC 控制器。
每步读回后等待 3 秒再次核对，防止固件回退：

| 触发 | DC | E2 | F0 |
| --- | --- | --- | --- |
| 启动、无游戏 | 00 | 1D | 0C |
| 日间游戏启动 | 05 | 04 | 00 |
| 游戏存活、热重载夜间规则 | 00 | 1D | 0C |
| 游戏存活、热重载日间规则 | 05 | 04 | 00 |
| 游戏退出 | 00 | 1D | 0C |

五步均通过，两轮实测均无固件回退。结束后恢复主实例开关。
本轮没有物理拔线/关屏，离线与写失败重试由受控时间和硬件适配器故障回归覆盖。

### 两次强杀和最终结算

发布宿主启动长寿命探针，确认落盘，连续两次强杀宿主。每次恢复均断言：

1. 探针仍存活；中途没有完成 CSV；
2. 恢复日志和快照重写均出现；PID、原始起点保持，快照时刻更新；
3. 最后杀探针，只产生一条覆盖两次重启的 CSV，快照变空。

新临时发布产物和正式部署产物均通过。最初新回归失败，实际揭示并推动修复了裸文件名伪路径问题。

## 命令与结果

以下命令从仓库根执行；重复运行按最终结果归并，红灯阶段保留说明。

| 命令/设置 | 结果 |
| --- | --- |
| `dotnet test GameHelper.sln --nologo --filter FullyQualifiedName~AssumeRunning` | 初始超时；补启动前接管用例后 2 红/1 绿；修复后 3 绿 |
| `dotnet test GameHelper.sln --filter "FullyQualifiedName~MonitorModeAutomationTests\|FullyQualifiedName~MultipleProcesses_EveryChange"` | 最初 3 红；修复后全绿 |
| `dotnet test GameHelper.sln --filter "FullyQualifiedName~InFlightSessionRecoveryTests\|FullyQualifiedName~CsvPlayTimeResumeTests"` | 最终 21 绿，包含真实 CSV 文件独占锁、失败重试和恢复去重 |
| `dotnet format whitespace GameHelper.sln --include <本次修改的 C# 文件> --no-restore` | 通过 |
| `dotnet build GameHelper.sln --nologo -v quiet` | 最终成功，0 错误 |
| `powershell -File scripts/verify-on-machine.ps1 -SkipDeploy` | 新发布产物、隔离配置校验和目录列表均通过 |
| 设置新发布 exe 的 `GAMEHELPER_TEST_PUBLISH_EXE`，执行 `dotnet test GameHelper.sln --nologo` | 577 通过、2 个既有跳过、0 失败；本轮另启用 `GAMEHELPER_VERIFY_BREAK_NOTIFICATIONS=1`，真实通知及前台采样均通过 |
| 设置 `GAMEHELPER_TEST_DDC=1`，执行 `dotnet test GameHelper.sln --no-build --filter FullyQualifiedName~MonitorMode_WhenHardwareVerificationEnabled --logger "console;verbosity=detailed"` | 真实硬件五阶段通过 |
| `powershell -File scripts/verify-on-machine.ps1 -SkipVerify -Restart` | 正式实例停止、仅清理其孤儿 ETW、原子替换并重启成功 |
| `dotnet test GameHelper.sln --no-build --filter FullyQualifiedName~EndToEnd.Published` | 正式发布目录 5 通过；普通运行未启用硬件写入，硬件证据来自上一独立显式运行 |
| `git diff --check` | 通过 |

部署脚本还通过独立临时目录中的执行级故障注入：正常更新、启动失败回滚、无旧实例启动、未知路径保护、
默认保留运行实例五分支；未以这些故障注入触碰真实用户实例。

最终 `autoreview` 使用仓库约定的 subagent 模式；两轮修复后复核为
`clean — 当前完整未提交 diff 没有 accepted/actionable findings`。未接受推测性扩大范围的改造。

## 交付与边界

- 正式新实例存活且其 ETW 会话运行；HKCU 启动路径恢复为正式发布 exe。
- 真实配置中交接遗留探针已移除；临时探针和诊断启动文件清理；4 条合成探针 CSV 在备份原文件后移除，真实游戏记录保留。
- 两个既有跳过为交互 Shell 用例；没有把跳过算成实机通过。CE 覆盖表生成和调度测试，本轮未向真实游戏注入并测量倍率。
- 历史上已丢失、没有可靠起止时间的会话没有补造。真正发生于进程监控空窗内的整局游戏仍无法凭空恢复。
- 提交身份为既有批准的个人身份；新增内容秘密模式扫描 0 命中。系统未安装 gitleaks；身份脚本在本机编码读取失败，
  已直接核对 Git 配置和历史元数据。旧历史存在公司身份记录，本轮未改写既有历史。

## 后续修正：正式交互窗口启动验收

用户随后报告双击没有窗口或闪退。实机确认旧实例 PID 12332 仍运行、ETW 正常，但部署脚本以
`-WindowStyle Hidden` 启动了正式交互宿主，窗口不可见并持续持有单实例锁；再次双击只会提示已经运行后退出。
此前仅检查 PID 和 ETW 的交付验收遗漏了窗口可见性，该次验收结论不足。

现改为正常窗口启动，并由独立原生探针附着真实控制台核验显示窗口；不可见即判定启动失败，触发既有部署回滚。
探针兼容 Windows Terminal 的隐藏控制台兼容窗口，通过 owner 链检查实际终端窗口。
AGENTS、README 和部署指南同步明确每次功能或修复都须更新本机，验证可见窗口和监控就绪。

- `dotnet build GameHelper.sln --nologo -v quiet`：成功，0 错误。
- `powershell -File scripts/verify-on-machine.ps1 -SkipDeploy`：新产物发布、隔离配置验证通过。
- 指向新产物并启用通知实测的 `dotnet test GameHelper.sln --nologo`：580 通过、2 个既有跳过、0 失败。
- 三项真实启动专项覆盖正常显示、隐藏窗口拒绝，以及真实 Win32 窗口模拟的终端 owner 拓扑；后者并非完整 Windows Terminal 前端端到端测试。
- `autoreview` 使用 subagent 模式，修复终端兼容窗口误判并复核后，无 accepted/actionable findings。
- `powershell -File scripts/verify-on-machine.ps1 -SkipVerify -Restart`：原子替换成功；正式实例 PID 30724，
  原生探针返回 `Visible: true`，对应 `GameHelper-ETW-30724-*` 会话为 `Running`。
- 指向正式部署目录执行 `dotnet test GameHelper.sln --no-build --filter FullyQualifiedName~PublishedInteractiveStartupTests --nologo`：3 通过、0 跳过、0 失败。

此前另已按用户授权完成公司提交身份的历史清理；本次启动修复沿用已批准个人身份。

## 后续修正：DDC 可用性提示刷屏

用户报告持续刷 `DDC/CI monitor became available`。最小回归确认：单个控制器、显示器始终在线，
每次 10 秒冷却结束后的成功探测都被误记为“刚恢复”。现仅在不可用变为可用时提示恢复，可用变为不可用时警告一次；
持续在线仍定期探测，持续离线仍保留自愈重试。

- `dotnet test GameHelper.sln --filter FullyQualifiedName~DdcMonitorModeControllerTests --nologo -v quiet`：
  原行为 3 项失败，修复后 3 项通过，覆盖持续在线、首次恢复和断开重连。
- 使用临时只读 .NET 探针调用真实发布 DLL 的公开构造器及 `IsAvailable`，对 Dell U2720QM 各做四次检查（间隔 11 秒）：
  旧版本始终在线但重复恢复提示 3 次；新版本始终在线、初始就绪提示 1 次、恢复提示 0 次。未写入显示器寄存器。
- `dotnet build GameHelper.sln --nologo -v quiet`：成功，0 错误；
  `powershell -File scripts/verify-on-machine.ps1 -SkipDeploy`：隔离发布验证通过。
- `autoreview` 使用 subagent 模式，当前修复无 accepted/actionable findings。README 与显示器切换指南同步更新日志语义。
- 指向新发布 exe、启用 `GAMEHELPER_VERIFY_BREAK_NOTIFICATIONS=1` 执行
  `dotnet test GameHelper.sln --nologo --logger 'trx;LogFileName=ddc-log-fix.trx'`：583 通过、2 个既有跳过、0 失败。
- `powershell -File scripts/verify-on-machine.ps1 -SkipVerify -Restart`：更新本机并重启成功，正式 PID 14752，窗口可见、ETW 运行。
  脚本本次枚举 ETW 失败而保留旧会话；随后用 `logman query -ets` 核对，确认旧属主 PID 30724 已退出，
  仅停止其确切会话 `GameHelper-ETW-30724-9aa8f8c56af04ede960ed5da06d06fc8`，保留新实例会话。
