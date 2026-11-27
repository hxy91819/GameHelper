# 自定义启动前动作的进程生命周期验收（2026-11-27）

## 交付行为

启动前后台运行动作增加可选 `singleInstance` 与 `stopOnGameExit`，旧配置默认关闭。去重范围为数据目录、游戏 DataKey、exe 完整路径和参数，只复用本工具托管的动作。退出清理终止其 Windows Job Object 内全部进程，不按 cmd/node 程序名批量关闭；根脚本先退出时仍可清理子进程。

隐藏的系统 PowerShell 持有 Job 句柄，不加载 GameHelper 发布目录中的二进制，宿主更新重启不会终止配套工具或锁住发布目录。归属记录保存在数据目录，游戏退出时在后台队列清理。修改／删除动作后仍按已启动程序的归属记录清理；不补跑停机期间已结束会话的动作。

## 验证记录

- `dotnet build GameHelper.sln --nologo -v quiet`：通过，0 编译错误；仓库已有分析器警告仍存在。
- `dotnet test GameHelper.Tests --filter 'FullyQualifiedName~OwnedActionProcessesTests|FullyQualifiedName~GameAction'`：初版 Job 最后句柄关闭后无法通过名称重新打开，3 个真实进程用例失败；补入独立句柄托管后专项 33 项通过。
- 首轮未隔离的 `dotnet test GameHelper.sln`：桌面文档读取受本机已激活 SQLite 影响，出现 `Desktop scope 尚未导入`；中止该轮。使用 `git archive HEAD`（基线 `d424fdfd54b79cd839f6cfd6b56ee6c3ddcb2432`）展开到临时目录，在原代码运行 `dotnet test GameHelper.Tests --filter FullyQualifiedName~DraftStore_PreservesIncompleteTextAndBaselineAcrossRestart --logger 'console;verbosity=normal'`，同样在 `SqliteDesktopStore.Scope` 复现；没有 stash 或更改用户数据库。
- 改用 `GAMEHELPER_DATA_DIR`、`GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE=1` 隔离测试。中间一次全量因后续退出清理改入后台队列而中止，最终完整运行见下条。
- `powershell -File scripts/verify-on-machine.ps1 -SkipDeploy -VerifyRuntime`：本机签名策略阻止脚本执行；使用进程级 `-ExecutionPolicy Bypass` 重试，通过隔离发布、数据迁移、无可见控制台、ETW、库 IPC、两次强杀续算和离线降级预检。
- `dotnet test GameHelper.sln --configuration Release --logger 'console;verbosity=normal' --logger 'trx;LogFileName=final.trx'`：隔离数据且指定新发布产物，主测试集 **1355 通过、5 项原有可选测试跳过**，输入测试集 **65 通过**。
- 最后补强独立异常清理、无记录时跳过写盘、视觉截图白底后，`dotnet test GameHelper.sln --configuration Release --filter 'FullyQualifiedName~OwnedActionProcessesTests|FullyQualifiedName~GameAction'`：**35 通过**。
- 真实进程验证包括：管理器重建后复用、不同游戏互不复用、不误杀用户同 exe 进程、根脚本已退出后的子进程去重与清理、不同参数独立管理、退出清理选择关闭时保留程序。服务层验证最后一个游戏进程退出才清理，以及后续 pre 动作失败时回滚。
- 真实 WPF 动作 DataTemplate 离屏渲染、人工查看截图：两个选项文字完整、选中正确，等待退出时正确禁用。未操作真实游戏或倍速脚本。
- `git -c core.safecrlf=false diff --check`：通过；本轮动作测试未使用 `DateTime.Now` / `Today` 断言。

用户行为与 BAT 示例已更新 `README.md`、`docs/guides/game-library.md`，两个配置样例同步注释。

## 独立审查与最终部署

- 首选 subagent 审查会话超过 30 分钟未响应，恢复及精简上下文重试也无响应，已中断。检查发现本机 CLI 实际可用，技能中“未安装审查 CLI”的环境说明已过时，改用默认 OpenCode / `ollama-cloud/glm-5.2`。
- `python .agents/skills/autoreview/scripts/autoreview --mode local --engine opencode --model ollama-cloud/glm-5.2 --no-web-search --stream-engine-output`（附本次范围与测试上下文）：完成真实调用链审查。
- 采纳 1 项：复用时不得撤销已启动进程树的 `StopOnExit` 承诺。改为 OR 合并，并增加“可开启、不可撤销”真实进程回归。
- 拒绝 2 项低置信度 P3：本地同用户归属文件不构成提权边界，前缀校验也不能区分其它 GameHelper 作业；`IndexOf` 在每条记录都有新 GUID 的当前实现中正确，未来改变命名规则属于推测。已用内联注释说明真实不变量。
- 修正后再次执行 `dotnet test GameHelper.sln --configuration Release --filter 'FullyQualifiedName~OwnedActionProcessesTests|FullyQualifiedName~GameAction'`：**36 项通过**。
- 一次复核调用尝试从 Windows 工具执行 `cd /d D:\...`，被引擎的 external_directory 权限规则自动拒绝，随后输出格式校验失败；没有更改权限。改用已打包补丁复核，保留同一引擎和模型。
- 最终 `python .agents/skills/autoreview/scripts/autoreview --mode local --engine opencode --model ollama-cloud/glm-5.2 --no-tools --no-web-search`（附已验证的不变量与修正说明）：**退出码 0，findings=[]，`autoreview clean: no accepted/actionable findings reported`**。首次审查已读取实际上下文，最终针对修正后的完整补丁复核；未额外开启审查面板。
- 修正后重新执行 `powershell -ExecutionPolicy Bypass -File scripts/verify-on-machine.ps1 -SkipDeploy -VerifyRuntime`：通过新产物的隔离迁移、无控制台、ETW、IPC、两次强杀续算与离线降级预检。
- `powershell -ExecutionPolicy Bypass -File scripts/verify-on-machine.ps1 -SkipVerify -Restart`：**原子部署成功**，后台 PID **24168**；完整性、数据目录写入、ETW、游戏库 IPC 健康检查通过。没有按文件覆盖发布目录。
- 既有未暂存的存档技能和 `docs/index.md` 文档 WIP 不参与本次提交；部署清单如实保留 dirty 文件列表，这些无关文档不进入运行产物。
