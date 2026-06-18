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
dotnet run --project .\GameHelper.ConsoleHost -- config add <exe>
dotnet run --project .\GameHelper.ConsoleHost -- config remove <exe>

# 历史数据迁移
dotnet run --project .\GameHelper.ConsoleHost -- migrate
```

`migrate` 会按当前 Core 匹配规则把旧 `playtime.csv` 中的游戏名映射到 `dataKey`；歧义匹配不会自动改写。

更多 CLI 说明见 `docs/guides/cli.md`。

### 运行中拖拽添加（已支持）
- 支持拖拽 `.exe` / `.lnk` / `.url`。
- 当 CLI 主进程已在运行时，新的拖拽启动请求会自动转发给主进程处理。
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

### 发布 WinUI

```powershell
dotnet publish .\GameHelper.WinUI -p:PublishProfile=WinUI-SelfContained
```

## 配置文件

默认路径：`%AppData%\GameHelper\config.yml`

示例：

```yaml
processMonitorType: ETW

games:
  - entryId: "8c5f5ccf30b648f88f4d2f1f8b4b6c7e"
    dataKey: "witcher3"
    executablePath: "D:\\Games\\The Witcher 3\\bin\\x64\\witcher3.exe"
    executableName: "witcher3.exe"
    displayName: "巫师3"
    isEnabled: true
    hDREnabled: false
```

说明：
- `entryId`：配置条目的内部唯一标识（自动生成）。
- `dataKey`：统计主键，写入 `playtime.csv` 的 `game` 字段，必须全局唯一。
- `hDREnabled`：是否在该游戏运行时由 GameHelper 自动开启 HDR；`false` 不会关闭用户已经手动开启的 HDR。

监控匹配：
- GameHelper 只处理启用配置中的候选进程名，候选名来自 `executableName` 和 `executablePath` 的文件名。
- 完整路径只在候选进程需要路径消歧时解析；非候选进程不会触发路径查询、ProductName 读取或模糊匹配。

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
```

更完整的架构、规范和计划见：
- `docs/architecture/index.md`
- `docs/prd/index.md`
- `docs/plans/index.md`

## 许可
- 开源使用：AGPL-3.0
- 商业使用：见 `LICENSE`
