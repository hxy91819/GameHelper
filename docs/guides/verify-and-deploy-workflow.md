# 开发完成后的验证与部署 Workflow

本文档定义 GameHelper 每次功能开发完成后的**固定验证与部署流程**。所有涉及代码改动的任务在收尾前必须完成本流程（或明确说明无法完成的原因）。

## 核心原则

1. **直接更新是常驻授权策略。** 用户已授权"改完直接更新"：开发任务收尾后即执行
   `powershell -File scripts\verify-on-machine.ps1 -SkipVerify -Restart` —— 停止运行中的实例、清理孤儿 ETW
   会话、原子换入新产物并重启确认存活，无需逐次向用户请示。在途游戏会话由
   [跨重启会话续算](#跨重启会话续算in-flight-sessions)机制保证不丢失、连续计时。
2. **续算能力是直接更新的安全网。** 仅当它不可用（例如运行中的实例仍是 2026-10-17 之前的旧版本、
   或快照恢复被禁用）且检测到有被监控游戏正在运行时，才退回保守路径：保留新产物并等待用户自行决定重启时机。
3. **测试实例必须与用户数据隔离。** 测试实例加载数据走沙盒目录，不读不写真实 `%AppData%\GameHelper`。
4. **测试实例不能与主实例争抢单实例互斥。** 通过环境变量禁用互斥，允许两者共存。
5. **开发完成后必须部署到本机发布目录**，让用户下一次启动快捷方式时直接生效；若发布目录被运行实例锁定且未使用 `-Restart`，脚本整体跳过替换并保留新产物等待重试（详见下文部署小节）。

## 测试实例隔离机制

| 环境变量 | 作用 |
| --- | --- |
| `GAMEHELPER_DATA_DIR` | 将整个数据目录（`config.yml`、`playtime.csv` 等）重定向到指定目录。实现见 `GameHelper.Core/Utilities/AppDataPath.cs`。 |
| `GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE` | 设为 `1` 时跳过全局单实例互斥，允许测试实例与用户正在运行的主实例共存。 |
| `GAMEHELPER_ALLOW_MULTIPLE_INSTANCES` | 设为 `1` 时才允许同时存在多个沙盒实例；默认（不设）沙盒实例仍受"同时只能有一个"的插槽互斥约束（`Global\GameHelper.ConsoleHost.Sandbox`）。 |

测试实例启动时同时设置前两个变量；数据沙盒目录在验证结束后删除。
沙盒实例默认只允许一个在跑：跳过全局单实例互斥是为了和用户实例共存，不是为了无限多开；需要并行跑多个测试实例（如 E2E 套件）时显式设置 `GAMEHELPER_ALLOW_MULTIPLE_INSTANCES=1`。
设置 `GAMEHELPER_DATA_DIR` 的实例使用无操作开机启动适配器，不会修改用户 HKCU Run 项；一次性 CLI 命令也不会应用开机启动偏好。

## Windows CI 的隔离实例验收

CI 的 `Isolated Runtime Verify (Windows)` job 在 `windows-latest` 上运行：

```powershell
./scripts/verify-on-machine.ps1 -SkipDeploy -VerifyRuntime -DiagnosticsDirectory "$env:RUNNER_TEMP/runtime-verify"
```

`-VerifyRuntime` 是可选扩展，不改变本地默认验证行为。它在 CLI 配置校验后，复用部署的
WPF 后台启动和健康探针，验证无可见控制台、沙盒数据目录可写且运行时已写日志、监控就绪、
本实例拥有 ETW 会话，以及游戏库 `--check` 命名管道能连到同一 PID。
实例通过 `GAMEHELPER_DATA_DIR` 和 `GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE=1` 隔离，
结束时仅停止临时发布目录中的进程并删除数据沙盒；不部署、不重启用户实例、不修改快捷方式。
失败时上传 `isolated-runtime-diagnostics` artifact（沙盒配置、运行时日志、IPC 结果及发布错误日志）。
现有测试 job 构建整个 `GameHelper.sln`，包含 Desktop；测试命令保持不变。

CI 默认严格要求 ETW，不主动降级。如果 runner 确认不能创建 ETW 会话，可使用已有
`-AllowDegradedMonitor`，但必须在 job 摘要记录实际失败原因，不能用它掩盖运行时回归。

仍需 Windows 真机验收：HDR 显示切换、DDC 显示器控制、CE 注入与热键、实际发布目录交换、
用户实例重启及在途游戏会话续算。CI 的隔离验收不替代这些硬件和真实部署检查。

## 跨重启会话续算（in-flight sessions）

部署的强制重启（或异常崩溃）不再中断游戏时长统计，机制如下：

- **快照落盘**：`GameAutomationService` 在每次进程注册/退出以及每 10 秒心跳时，把全部在途
  会话原子写入 `%AppData%\GameHelper\inflight-sessions.json`（进程 id + 归一化名/路径 + 原始开始时间 +
  快照时间与进程创建时间）。多进程共享原会话起点，任何一个仍运行就继续计时。快照随状态持续刷新，因此进程被强杀（`Stop-Process -Force`）后快照仍然可用。
- **启动恢复**（`GameAutomationService.Start` 内一次性执行）：
  - 快照内进程仍在运行、且映像路径/文件名和创建时间与快照身份一致（旧快照无创建时间时要求进程早于快照存在，
    防止同一 exe 复用 PID 误认）→ 视为游戏从未停止：`ResumeTracking` 以**原开始时间**续算，游戏真正退出时落
    一条连续记录，计时不割裂；
  - 接管进程同时登记到 ETW 观察表，并保留至 ETW 启动完成；真实停止事件与每 30 秒漏退出补偿均可结算；
  - 接管后直接原子替换快照，避免先删除再保存的空窗，支持连续多次强杀/更新；
  - 进程已退出或身份不符 → `WriteRecoveredSession` 以"最后已知存活时刻"（快照时间）收尾落库；恢复的
    时长跨度超过 12 小时按上限封顶并记录告警（真实退出时间在停机期间不可知，封顶防止陈旧快照无限膨胀）。
  - 恢复写入失败时保留条目及固定结束时间，随心跳和下次启动重试；全部结算成功且无在途进程后才清空。
    同一游戏的多局待补录按起止时间分别处理；CSV 重放同一恢复记录时去重，避免清理快照前再次崩溃产生重复行。
    正常游戏退出时 CSV 被占用而写入失败，也会转为待补录快照。
- **正常停止语义不变**：用户主动停止监控时依旧逐会话即时落库并清空快照文件；快照恢复只在监控启动时执行一次。
- 快照内无 pid（历史极端数据）的会话按"未存活"收尾处理。

因此旧流程中"有被监控游戏在运行就中止重启"的门槛不再是硬约束。

## 固定流程

WPF 游戏库随 ConsoleHost win-x64 发布到 `desktop/`。部署对两个入口执行同一次目录交换；`-Restart` 通过 WPF `--background` 无窗口启动监控，并以 `--restore` 不激活地恢复原本打开的游戏库。界面草稿单独原子保存。验收后台无可见控制台、监控健康和 WPF `--check` 的真实命名管道；失败回滚。成功部署创建桌面“GameHelper 游戏库”入口，开机 Run 项同步为 WPF 后台入口。

### 1. 构建与测试

```powershell
dotnet build GameHelper.sln
dotnet test GameHelper.sln
```

两者必须全绿（既有 Skip 项除外）。

### 2. 发布 + 实机验证 + 部署（一键脚本）

```powershell
powershell -File scripts\verify-on-machine.ps1
```

脚本执行三步：

1. **发布**：`dotnet publish`（self-contained，与现有部署布局一致）到独立临时目录（不会被运行实例锁定）。
2. **实机验证**：以隔离环境变量启动发布产物（独立测试实例），运行 `validate-config`、`config list` 冒烟断言。
3. **部署**：目录级原子替换快捷方式指向的 `GameHelper.ConsoleHost\bin\Release\net8.0-windows\win-x64\publish\`。
   - 默认（不带 `-Restart`）：实例运行中、发布目录 exe 被锁（按运行实例进程路径判定；无法读取路径时保守视为占用）→ **整体跳过**：新产物保留在临时目录，脚本打印运行中实例 PID 与重试命令；关闭实例后重跑：

```powershell
powershell -File scripts\verify-on-machine.ps1 -SkipVerify
```

   - `-SkipVerify -Restart`：确认目标进程路径 → 停止实例 → 只清理已退出属主的 ETW 会话 → 原子换入 → 无窗口启动监控并查询就绪 PID → 检查权限、数据、ETW 和 IPC；原有 WPF 窗口不激活地恢复。旧产物保留至健康检查通过，失败恢复旧目录和监控。无法读取实例路径时保守中止，不强杀未知实例。在途会话由跨重启续算机制接续。

> 注意：禁止逐文件覆盖发布目录——framework-dependent 产物覆盖 self-contained 布局（或反之）会混装运行时文件，导致下一次启动报 "No frameworks were found"。锁定检测不能只用目录探针：运行实例锁的是 exe/dll 文件本身，目录仍可写。
>
> 部署脚本在换入目录后写入 `deploy-manifest.json`（HEAD SHA、分支、未提交文件列表），工作区脏时打印警告；要求产物与提交严格一致时加 `-RequireClean`，脚本会直接拒绝部署。重启后还会执行健康探针：进程完整性级别、数据目录可写、`monitor: ETW` 时必须存在本实例的 ETW 会话；任一失败回滚到旧目录。`-AllowDegradedMonitor` 可把 ETW 缺失降为警告。
>
> 脚本在换入新目录后会执行 `icacls /setintegritylevel (OI)(CI)M /T`，把发布树的强制完整性级别归一到 Medium。原因：在沙箱/隔离 shell 里构建的产物可能整个继承 Low 完整性标签，而 Windows 会把进程完整性钉在镜像文件的标签上——Low 进程写不了 `%AppData%`（Medium）也建不了内核 ETW 会话，表现为 `inflight-sessions.json.tmp` Access Denied 与 "Administrator rights required to start ETW" 同时出现。手动部署或从其他目录复制产物时，同样应确认发布目录未被标记为 Low。

可选参数：`-SkipVerify` 跳过实机验证（仅部署）；`-SkipDeploy` 只发布+验证，不部署；`-Restart` 直接更新与重启。

### 3. 端到端回归测试

`GameHelper.Tests\EndToEnd\PublishedExeEndToEndTests.cs` 在发布产物存在时自动执行：
以独立测试实例 + 沙盒数据目录运行真实 exe，验证配置校验、统计数据读取、与主实例共存。
若发布产物不存在则跳过（因此 workflow 中应先跑第 2 步再跑测试）。

`PublishedMonitorEndToEndTests` 还在管理员 Windows 上启动真实 ETW 和独立探针，验证连续两次强杀重启后
原始起点不变、每次快照重写、游戏最后退出仅产生一条连续记录。显示器真实寄存器的显式验证见
[monitorMode 硬件回归](./monitor-mode.md#发布产物与真实硬件回归)。

若实例锁定正式发布目录（未用 `-Restart` 时常见），可让端到端测试直接验证脚本保留的临时新产物，避免验证到旧版本：

```powershell
$env:GAMEHELPER_TEST_PUBLISH_EXE = '<脚本输出的临时目录>\GameHelper.ConsoleHost.exe'
dotnet test GameHelper.sln --filter FullyQualifiedName~EndToEnd.Published
Remove-Item Env:GAMEHELPER_TEST_PUBLISH_EXE
```

该变量只用于测试选择产物；指定路径不存在时测试失败。

### 4. 用户生效确认

使用 `-Restart` 部署后实例已经运行新版本，功能即刻生效。使用默认路径时，部署完成（或用户关闭实例后补跑 `-SkipVerify`）后，用户下一次点击快捷方式启动即运行新版本；主动重启实例不再属于需要请示的动作。

每次功能或修复完成都须实际更新本机。日常入口是 WPF，后台 ConsoleHost 不显示窗口。验收没有可见 Console / Terminal 窗口，并确认运行时的 ETW 会话和游戏库 IPC；WMI 配置验证 WMI 进程事件。仅确认 PID 存活不足以完成交付。

后台窗口核验使用 `scripts/probe-console-window.ps1 -TargetProcessId <pid> -RequireNoConsole`，没有控制台或仅有不可见兼容对象时成功；控制台自身和其 Terminal owner 都必须不可见。探针运行在独立进程中。旧 Console 交互入口已移除，不再需要可见窗口验收；不能用 `Process.MainWindowHandle` 代替控制台检查。

### 5. 收尾：commit 并 push（常驻授权）

部署成功即代表功能"完整完成"。此为固定收尾步骤，**无需等待用户额外指示**：

1. 复查 `git status`，只暂存本任务交付涉及的文件（不混入无关的并行 WIP）；
2. 中文提交信息概括功能与影响组件（与既有历史一致；单行主题 + 简短验证说明正文）；
3. `git push` 当前分支（main）；远端领先时先 pull/rebase 再重试；
4. 只有部署本身失败（或被保守回退拦下）的任务才暂缓提交——未完成部署的功能按常规流程等待，不入库半成品。

## 续算安全网失效时的保守回退

以下情形需要回到保守路径（保留新产物、等待用户决定重启时机）而不是强制重启：

- 运行中的实例尚无续算能力（2026-10-17 之前的旧版本），且检测到被监控游戏正在运行；
- 在途快照文件被确认不可恢复（损坏且无备份），同时有被监控游戏在运行。

排查手法（沿用旧授权流程）：从 `%AppData%\GameHelper\config.yml` 提取全部 `executable`，与
`Get-CimInstance Win32_Process` 的进程比对（带路径的条目按路径后缀匹配、只写 exe 名的条目按进程名匹配，
忽略 GameHelper 自身进程）。

### 自动化启动注意

`ConsoleHost` 在 **stdout 被管道捕获且没有真实控制台** 的场景下启动即卡死（零输出）。自动化拉起实例（测试实例或正式实例皆然）不要用捕获 stdout 的方式（如 `exe | tee`、`subprocess.run(capture_output=True)`），应改用 `Start-Process`（不经由当前管道）或 `cmd /c ... > 日志文件 2>&1` 的文件重定向，并通过轮询日志文件 / 进程存活来确认启动成功。

后台运行时由 WPF 无窗口引导，以 `CreateNoWindow` 拉起并把输出写入配置旁日志。后台发布回归调用实际部署启动函数，检查无可见窗口、重复启动复用同一 PID、IPC 和 ETW/CSV。可见旧 Console 入口已移除，默认测试不会弹出 Console。

## 功能级验证要求

除上述通用流程外，涉及用户可见界面的改动（如 WPF 游戏库渲染）还应：

- 用沙盒数据目录 + `--config` 参数或预置 `playtime.csv` 驱动真实 WPF/CLI 渲染路径，人工确认输出版面。
- 保留或新增对应的自动化测试断言，确保后续回归有安全网。

### WPF 与后台启动入口

用户在 WPF 成为主界面后重新授权后台启动，替代此前正常可见 Console 策略。部署与测试共用 `scripts/start-checked-instance.ps1`，调用 WPF `--background --check`，校验 IPC 返回 PID 的映像路径后才登记为部署拥有的进程。成功后旧 Console / Launcher 桌面入口迁到游戏库，新运行时同步 Run 项。旧版本回滚使用兼容的隐藏 `monitor` 路径。

E2E 监控测试的共享启动助手也必须显式传入 `monitor --background`，不能只隐藏外层 cmd（不能据此保证内部监控进程没有可见控制台）。测试助手复用正式 `BackgroundRuntime.CreateStartInfo`，直接启动后台宿主，不再包装 cmd；读取配置旁 `.runtime.log` 判断就绪；ETW 落盘回归同时通过独立控制台探针断言无可见窗口。

所有自动辅助启动使用隐藏窗口或无控制台参数。CE 和 DS4Windows 请求 Hidden；真实游戏启动仍遵循用户入口。端到端游戏替身必须是 winexe 无窗口程序，不能把 cmd.exe 作为待启动游戏；其启动经过真实 ShellExecute 时仍会分配控制台。

WPF `--snapshot` 在屏幕外渲染，不激活、不显示任务栏按钮；沙盒中的长驻 UI 下载验证设置 `GAMEHELPER_TEST_OFFSCREEN=1` 也使用相同位置，测试对话框同步放到屏幕外。该测试开关只在存在 `GAMEHELPER_DATA_DIR` 时生效。
