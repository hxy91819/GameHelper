# 8. Development and Deployment

## 8.1. Local Development Setup

1.  克隆仓库。
2.  安装 .NET 8.0 SDK。
3.  执行 `dotnet restore` 以安装所有 NuGet 依赖。
4.  使用 Visual Studio 2022 或 VS Code 打开解决方案。
5.  运行 `GameHelper.ConsoleHost` 项目。
      * 在 Windows 上，可以直接运行所有功能。
      * 在 macOS/Linux 上，使用 `dotnet run -- --monitor-dry-run` 来跳过 Windows 特定的监控服务。

## 8.2. Build and Deployment Process

  * **构建命令**: `dotnet build`
  * **发布命令**: `README.md` 中提供了详细的 `dotnet publish` 命令，用于创建自包含的 Windows 可执行文件。
  * **自动化部署**: 项目包含一个位于 `.github/workflows/release.yml` 的 GitHub Actions 工作流。当一个形如 `v*` 的标签被推送到仓库时，它会自动构建项目，并将结果打包上传到 GitHub Release。

## 8.3. 回滚与应急预案

| 集成点 | 回滚策略 | 应急验证 |
| --- | --- | --- |
| 进程监控 (ETW/WMI) | 在 `appsettings` 中切换 `ProcessMonitorType=Wmi`，或回滚至上一稳定分支 tag（例如 `release/2025.10`）。 | 使用既有 `EndToEndProcessMonitoringTests` 与手动触发测试进程确认事件仍被捕获。 |
| 配置加载 (`YamlConfigProvider`) | 暂时将 `config.yml` 的新字段置为可选并启用 `--monitor-dry-run`，如需恢复旧行为，可回滚到 `YamlConfigProvider` 先前提交。 | 导入历史 `config.yml` 与 `playtime.csv`，执行 `dotnet run -- config list` 验证字段解析。 |
| 数据存储 (`CsvBackedPlayTimeService`) | 切换到 `FileBackedPlayTimeService`（回退服务）或回滚至 `data-key` 引入前版本。 | 使用测试脚本写入/读取既有 CSV，核对 `GameName` 列是否保留旧值。 |
| CLI/交互式 Shell | 若新命令导致问题，可恢复到先前 CLI 版本并保留 `InteractiveShell` 的旧构建，同步关闭新命令的入口。 | 运行 `GameHelper.Tests` 中交互式测试并手动执行关键命令（monitor/config/stats）。 |

## 8.4. CLI 用户旅程（含异常分支）

| 场景 | 用户步骤 | 系统期望行为 | 常见异常与提示 |
| --- | --- | --- | --- |
| **初次配置并启动监控** | 1) 运行 `dotnet run --project .\GameHelper.ConsoleHost --` 进入交互式 Shell；2) 选择“配置”面板，使用 `config add` 或拖放 EXE/LNK 创建游戏条目；3) 返回主面板启动 `monitor`。 | Shell 展示新增游戏，监控启动后进入实时摘要视图；会话结束后自动写入 `playtime.csv` 并弹出新增记录摘要。 | *缺少管理员权限*: Shell 显示 WMI 订阅失败并建议以管理员重试；*config 缺少 DataKey*: `YamlConfigProvider` 拦截并在界面提示补齐字段。 |
| **拖放 Steam 快捷方式** | 1) 在交互式 Shell 提示下拖放 `.lnk` 或 `steam://` 快捷方式；2) 确认解析出的路径与 `ProductName`；3) 保存条目并继续监控。 | `SteamGameResolver` 解析目标 EXE，Shell 预填 `ExecutablePath` 与建议 `DataKey`；确认后将条目写入 `config.yml` 并刷新列表。 | *解析失败*: 显示“无法解析快捷方式”并回退到手动输入路径；*重复 DataKey*: Shell 阻止保存并提示选择重命名或覆盖。 |
| **查看与导出统计** | 1) 在 Shell 或命令行运行 `dotnet run --project .\GameHelper.ConsoleHost -- stats`; 2) （可选）指定 `--game` 过滤。 | CLI 按 `DataKey` 聚合并渲染 Spectre 表格；提供 CSV 导出路径说明。 | *playtime.csv 缺失*: 自动创建空文件并提示“尚无会话记录”；*CSV 损坏*: Shell 报告恢复动作并记录日志，建议用户备份损坏文件。 |
| **验证配置合法性** | 1) 在 Shell 中选择“配置校验”或运行 `dotnet run --project .\GameHelper.ConsoleHost -- validate-config`; 2) 根据提示修正。 | 输出 YAML 结构错误、缺失字段或重复键的位置；成功时提示“一切就绪”。 | *文件锁定*: 提示关闭占用该文件的编辑器；*语法错误*: 指出行号并建议对照 `config.template.yml`。 |

> 参考资料：更详尽的命令示例位于 `docs/history/CLI_Manual_zh.md`，流程对应的故事验收标准记录在 `docs/prd/2-史诗和故事.md`。
