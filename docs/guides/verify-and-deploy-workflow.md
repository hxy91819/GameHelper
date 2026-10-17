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

测试实例启动时同时设置这两个变量；数据沙盒目录在验证结束后删除。

## 跨重启会话续算（in-flight sessions）

部署的强制重启（或异常崩溃）不再中断游戏时长统计，机制如下：

- **快照落盘**：`GameAutomationService` 在每次会话状态变化（游戏首进程注册 / 末进程退出）时，把全部在途
  会话原子写入 `%AppData%\GameHelper\inflight-sessions.json`（进程 id + 归一化名/路径 + 原始开始时间 +
  快照时间）。快照随状态持续刷新，因此进程被强杀（`Stop-Process -Force`）后快照仍然可用。
- **启动恢复**（`GameAutomationService.Start` 内一次性执行，随后删除快照文件）：
  - 快照内进程仍在运行、且映像路径/文件名与快照身份一致（`IProcessIdentityProbe` 读取进程映像路径比对，
    防止 PID 复用误认）→ 视为游戏从未停止：`ResumeTracking` 以**原开始时间**续算，游戏真正退出时落
    一条连续记录，计时不割裂；
  - 进程已退出或身份不符 → `WriteRecoveredSession` 以"最后已知存活时刻"（快照时间）收尾落库；恢复的
    时长跨度超过 12 小时按上限封顶并记录告警（真实退出时间在停机期间不可知，封顶防止陈旧快照无限膨胀）。
- **正常停止语义不变**：用户主动停止监控时依旧逐会话即时落库并清空快照文件；快照恢复只在监控启动时执行一次。
- 快照内无 pid（历史极端数据）的会话按"未存活"收尾处理。

因此旧流程中"有被监控游戏在运行就中止重启"的门槛不再是硬约束。

## 固定流程

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

   - `-SkipVerify -Restart`（常驻直接更新策略的标准收尾命令）：按进程路径判定运行实例 → `Stop-Process -Force` 结束 → `logman -ets` 清理 `GameHelper-ETW-*` 孤儿 ETW 会话 → 原子换入 → 从发布目录 `Start-Process` 重启（不经管道 stdout），5 秒后确认新 PID 存活。在途会话由跨重启续算机制接续。

> 注意：禁止逐文件覆盖发布目录——framework-dependent 产物覆盖 self-contained 布局（或反之）会混装运行时文件，导致下一次启动报 "No frameworks were found"。锁定检测不能只用目录探针：运行实例锁的是 exe/dll 文件本身，目录仍可写。

可选参数：`-SkipVerify` 跳过实机验证（仅部署）；`-SkipDeploy` 只发布+验证，不部署；`-Restart` 直接更新与重启。

### 3. 端到端回归测试

`GameHelper.Tests\EndToEnd\PublishedExeEndToEndTests.cs` 在发布产物存在时自动执行：
以独立测试实例 + 沙盒数据目录运行真实 exe，验证配置校验、统计数据读取、与主实例共存。
若发布产物不存在则跳过（因此 workflow 中应先跑第 2 步再跑测试）。

若实例锁定正式发布目录（未用 `-Restart` 时常见），可让端到端测试直接验证脚本保留的临时新产物，避免验证到旧版本：

```powershell
$env:GAMEHELPER_TEST_PUBLISH_EXE = '<脚本输出的临时目录>\GameHelper.ConsoleHost.exe'
dotnet test GameHelper.sln --filter FullyQualifiedName~EndToEnd.Published
Remove-Item Env:GAMEHELPER_TEST_PUBLISH_EXE
```

该变量只用于测试选择产物；指定路径不存在时测试失败。

### 4. 用户生效确认

使用 `-Restart` 部署后实例已经运行新版本，功能即刻生效。使用默认路径时，部署完成（或用户关闭实例后补跑 `-SkipVerify`）后，用户下一次点击快捷方式启动即运行新版本；主动重启实例不再属于需要请示的动作。

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

## 功能级验证要求

除上述通用流程外，涉及用户可见界面的改动（如交互式 Shell 渲染）还应：

- 用沙盒数据目录 + `--config` 参数或预置 `playtime.csv` 驱动真实渲染路径，人工确认输出版面。
- 保留或新增对应的自动化测试断言，确保后续回归有安全网。