# GameHelper

GameHelper 是一个面向 Windows 玩家的桌面助手，提供进程监控、游戏时长统计和 YAML 配置管理，并同时提供 CLI 与 WinUI 两个入口。

`README.md` 的职责是：
- 指导用户如何安装、运行和使用项目。
- 给开发者提供最短路径的本地启动与验证步骤。
- 指向更详细的设计和规范文档，而不是重复它们。

详细设计与规范见 `docs/index.md`。

## 快速开始

### 环境要求
- Windows 10 (19041+) / Windows 11
- .NET 8 SDK（当前仓库通过 `global.json` 锁定 `8.0.417`，允许 `latestPatch` 滚动）
- （仅 WinUI 运行）Windows App SDK 1.6+（当前项目引用 `Microsoft.WindowsAppSDK` `1.6.250205002`）

### 常用 CLI 命令

```powershell
# 交互模式（默认）
dotnet run --project .\GameHelper.ConsoleHost --

# 启动监控
dotnet run --project .\GameHelper.ConsoleHost -- monitor [--monitor-type ETW|WMI] [--debug]

# 查看统计
dotnet run --project .\GameHelper.ConsoleHost -- stats [--game <name>]

# 配置游戏
dotnet run --project .\GameHelper.ConsoleHost -- config list
dotnet run --project .\GameHelper.ConsoleHost -- config add <exe|path-to-exe>
dotnet run --project .\GameHelper.ConsoleHost -- config import-steam
dotnet run --project .\GameHelper.ConsoleHost -- config remove <dataKey>

# 历史数据迁移
dotnet run --project .\GameHelper.ConsoleHost -- migrate

# 统计推送到 GitHub 仓库（自动/手动）
dotnet run --project .\GameHelper.ConsoleHost -- sync now [--force]
dotnet run --project .\GameHelper.ConsoleHost -- sync test
dotnet run --project .\GameHelper.ConsoleHost -- sync status
```

`migrate` 会按当前 Core 匹配规则把旧 `playtime.csv` 中的游戏名映射到 `dataKey`；歧义匹配不会自动改写。

`config list` 会同时显示 `dataKey` 和 `displayName`，方便核对配置显示名。

`config import-steam` 会扫描本机 Steam 的所有游戏库，并批量添加已安装且可定位到主可执行文件的游戏。

更多 CLI 说明见 `docs/guides/cli.md`。

### 统计推送到 GitHub（已支持）

把游戏时长统计自动推送到你指定的 GitHub 仓库（公开展示或私有备份）：

- 推送内容为聚合报告（Markdown）+ 按日×按游戏聚合 CSV，只写入仓库的 `game-stats/` 子目录，不含精确作息时间；原始明细可选附带。
- 两种上传方式：`method: git`（默认，复用本机 git 凭据，零 token）或 `method: api`（GitHub token）。
- 自动推送默认每天最多一次，会话结束路径零新增磁盘写入；也可用 `sync now` 手动触发。
- 配置方法、token 创建与隐私说明见 `docs/guides/sync.md`。

### 运行中拖拽添加（已支持）
- 支持拖拽 `.exe` / `.lnk` / `.url`。
- 当 CLI 主进程已在运行时，新的拖拽启动请求会自动转发给主进程处理。
- 转发请求始终写入运行中主进程当前使用的配置；新启动进程不会通过 IPC 切换主进程的 `--config`。
- 配置会立即热重载，对后续新启动的进程生效。
- 不会破坏“单实例”约束（主进程始终只有一个）。

## 发布

### 发布 CLI

```powershell
# 自包含（目标机器无需预装 .NET Runtime）
dotnet publish .\GameHelper.ConsoleHost\GameHelper.ConsoleHost.csproj -c Release -r win-x64 --self-contained true

# 非自包含（目标机器需安装 .NET 8 Runtime）
dotnet publish .\GameHelper.ConsoleHost\GameHelper.ConsoleHost.csproj -c Release -r win-x64 --self-contained false
```

默认输出目录：
`GameHelper.ConsoleHost\bin\Release\net8.0-windows\win-x64\publish`

发布后 smoke 验证：

```powershell
.\scripts\publish-console-smoke.ps1
```

该脚本会发布 ConsoleHost，并用临时 YAML 配置运行发布目录中的 `validate-config` 和 `config list`，用于提前发现发布产物缺文件、嵌入资源异常或配置解析失败。

### 发布 WinUI

```powershell
dotnet publish .\GameHelper.WinUI -p:PublishProfile=WinUI-SelfContained
```

## 配置文件

默认路径：`%AppData%\GameHelper\config.yml`

示例：

```yaml
monitor: ETW
startup:
  autoStartMonitor: false
  launchOnStartup: false
games:
  - dataKey: witcher3
    executable: "D:\\Games\\The Witcher 3\\bin\\x64\\witcher3.exe"
    displayName: 巫师3
    enabled: true
    hdr: false
```

说明：
- `dataKey`：统计主键，写入 `playtime.csv` 的 `game` 字段，必须全局唯一。
- `executable`：可执行文件路径或进程文件名；路径会用于精确匹配，文件名会自动从路径派生。
- `enabled`：是否参与监控、时长统计和自动化。
- `hdr`：是否在该游戏运行时由 GameHelper 自动开启 HDR；`false` 不会关闭用户已经手动开启的 HDR。
- `startup.autoStartMonitor`：交互模式启动后是否自动进入实时监控。
- `startup.launchOnStartup`：是否随系统启动 GameHelper。
- `sync`：统计推送到 GitHub 的配置（省略整段表示未配置；完整字段见 `docs/guides/sync.md`）。

CLI `config add` 可接收可执行文件名或 `.exe` 路径。传入路径时会保存到 `executable`，运行时从中派生路径与候选进程名。

配置更新以完整文档事务提交：修改游戏清单不会覆盖监控或启动设置，写入失败也不会留下部分更新。

监控匹配：
- GameHelper 只处理启用配置中的候选进程名，候选名来自 `executable` 的文件名；ETW 与 WMI 降级路径都使用这道候选名门控。
- 完整路径只在候选进程需要路径消歧时解析；非候选进程不会触发路径查询、WMI 详情查询、ProductName 读取或模糊匹配。

## 项目结构
- `GameHelper.WinUI`：WinUI 桌面入口
- `GameHelper.ConsoleHost`：CLI 入口
- `GameHelper.Core`：核心模型与业务逻辑
- `GameHelper.Infrastructure`：平台集成与持久化
- `GameHelper.Tests`：单元/集成测试
- `docs`：活文档、计划与归档材料

## 开发与验证

```powershell
dotnet build GameHelper.sln
dotnet test GameHelper.sln
.\scripts\publish-console-smoke.ps1
```

更完整的架构、规范和计划见：
- `docs/architecture/index.md`
- `docs/prd/index.md`
- `docs/plans/index.md`

## 许可
- 开源使用：AGPL-3.0
- 商业使用：见 `LICENSE`
