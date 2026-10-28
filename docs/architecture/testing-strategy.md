# Testing Strategy

The test suite should protect observable behaviour before large refactors. Tests should target stable interfaces and seams rather than private implementation details.

Windows is the sole supported platform. Local build/test gates and CI run on Windows; Linux/macOS compatibility and runners are outside scope. Permission-free unit tests isolate OS dependencies without implying support for other operating systems.

## Required Gates

Run these before committing code changes:

```powershell
dotnet build GameHelper.sln
dotnet test GameHelper.sln
```

For narrow iterations, run the smallest relevant `dotnet test --filter ...` first, then finish with the full solution test command before committing.

For ConsoleHost publish or startup-path changes, also run:

```powershell
.\scripts\publish-console-smoke.ps1
```

CI runs the same script on Windows to publish ConsoleHost and execute the published `GameHelper.ConsoleHost.exe` against a temporary YAML config.

## Coverage Priorities

- **Lifecycle symmetry**: services that start and stop external resources must leave state flags consistent after success, failure, and cleanup paths.
- **Process monitoring**: ETW/WMI/no-op adapters must preserve event semantics expected by core automation.
- **Automation behaviour**: matching, active session reference counting, HDR scheduling, and stop-event toggling must remain stable.
- **Persistence compatibility**: YAML config, `DataKey`, compact game entries, and CSV playtime history must survive roundtrips and migrations.
- **Configuration atomicity**: failed and concurrent `IGameConfiguration.Change` calls must never partially commit or discard unrelated settings.
- **Executable Identity**: name-only and path identities must expose the same order-independent, read-only projections across catalog flows.
- **Process Observation contracts**: ETW, WMI, and no-op adapters must share policy, filtering, stop-event, lifecycle, and PID eviction semantics.
- **Playtime codec**: writer-to-reader roundtrips must cover commas, quotes, newlines, malformed rows, and legacy JSON import.
- **Shell workflows**: CLI commands and file-drop forwarding should be covered through service-facing tests or smoke tests.
- **File-drop Intake**: a batch commits once, reloads automation once after success, and performs neither action for invalid input.
- **Command dispatch**: CLI routing should be covered through `ConsoleCommandDispatcher` tests rather than process-level tests when startup side effects are not under test.
- **Published console artifact**: `scripts/publish-console-smoke.ps1` covers the process-level path for the published executable, embedded YAML validator resources, and config values requiring YAML quoting.
- **Documentation navigation**: local Markdown links must stay valid while docs are archived or rewritten.

SQLite 内核的真实磁盘与并发验收见 [SQLite 存储内核](./sqlite-storage-kernel.md)。CI 另运行 `scripts/sqlite-publish-smoke.ps1`，验证 win-x64 两种发布布局的原生 SQLite 和四子进程初始化竞争。

## Windows-Specific Tests

- WMI and ETW integration tests are Windows-only.
- ETW tests that require administrator privileges should skip or self-report when not elevated.
- WPF view-model tests cover draft preservation, save-before-launch failure and search selection. The published library flow exercises real IPC, revision rejection, native launch, ETW/CSV and WPF rendering against an isolated host. Old WinUI/FlaUI suites are retired and do not constrain this design.
- The published background entry test calls the actual deployment startup function, verifies the runtime has no console, reuses the same instance on repeated startup, and tracks a real probe through ETW/CSV. Rendering snapshots use `ShowActivated=false`.

## 重构回归与失败溯源

- 并发回归使用屏障或可控替身暂停在实际竞态窗口，而非用 sleep 猜测时序。涉及事件订阅时，检查退订失败后 Stop→Start，一次开始／停止准确配对；涉及回调 drain 时，检查旧回调越过最后取消检查后暂停，Stop drain 超时并重启，再释放旧回调，确认不对新生命周期执行副作用或发布记录。断言服务行为，不绑定私有锁或字段。
- 重试行为分别覆盖运行中心跳和下次启动；不能以启动恢复测试替代运行中重试。用可控时钟／定时器触发失败后重试，断言保留原结束时间、成功后移除待重试状态、后续触发不重复写入。
- 对声称能保护某项修复的测试，在独立临时副本中回退该行为或做单点变异，保留断言，确认出现对应新增失败。变异仍通过时记为覆盖缺口；原有失败集合相同不等于该行为已有保护。
- 出现环境相关失败时，用 `git archive` 在临时目录运行整理前基线，使用该提交自身源码与测试，记录 SHA、命令、宿主及失败断言；区分既有失败、环境限制和新增回归。临时宿主若修改了目标框架、引用或排除了测试，列明差异与结果范围，不能将子集通过报告成完整 Windows 验收。

### 路径与文件共享样本

- 纯名称匹配／恢复测试的假路径优先写为 `D:/Games/game1.exe`，避免宿主的 `Path.GetFileName` 将反斜杠视为普通字符而改变测试前提。专门验证 Windows 反斜杠、盘符或 UNC 语义的用例保留 Windows 输入并在 Windows 验证；不为辅助宿主的样本问题修改产品路径逻辑。
- `FileShare` 冲突及相应 `IOException` 断言在 Windows 验证；纯 gate／标记内容判断与共享冲突断言分开，让辅助宿主仍能验证不依赖共享冲突的逻辑。辅助宿主未产生共享冲突不构成产品回归，也不能据此删掉 Windows 的共享语义保护。

配置相关的固化样本与兼容断言见[配置重构兼容契约](./configuration-compatibility.md)。以上辅助宿主约定不增加 Linux/macOS 支持或 CI 要求。

## Known Gaps

- Console host construction is covered through `ConsoleHostBootstrapper`, and CLI command routing is covered through `ConsoleCommandDispatcher`.
- Console process-level smoke coverage is intentionally narrow: it exercises published `validate-config` and `config list` only, avoiding long-running monitor flows.

SQLite 批次 2 的旧 codec 固化字节、候选导入断点、完整投影和降级/再升级测试见[导入与兼容工具](./sqlite-import-tools.md)。源坏行与逐项跳过诊断须对账；不以新投影器生成测试期望。

批次 3 的会话事务断点、真实子进程强杀及迁移后 IPC 运行验证见[SQLite 会话存储](./sqlite-session-storage.md#验证)。
