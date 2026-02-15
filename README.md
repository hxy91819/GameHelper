# GameHelper

GameHelper 是一个面向 Windows 玩家的桌面助手，提供：
- 进程监控（ETW/WMI）
- 游戏时长统计
- 游戏配置管理（YAML）
- WinUI 桌面入口 + CLI 入口

## 快速开始

### 环境要求
- Windows 10 (19041+) / Windows 11
- .NET 8 SDK
- （仅 WinUI 运行）Windows App SDK 1.6+

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
```

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
    hdrEnabled: false
```

说明：
- `entryId`：配置条目的内部唯一标识（自动生成）。
- `dataKey`：统计主键，写入 `playtime.csv` 的 `game` 字段，必须全局唯一。

## 项目结构
- `GameHelper.WinUI`：WinUI 桌面入口
- `GameHelper.ConsoleHost`：CLI 入口
- `GameHelper.Core`：核心模型与业务逻辑
- `GameHelper.Infrastructure`：平台集成与持久化
- `GameHelper.Tests`：单元/集成测试

## 开发与验证

```powershell
dotnet build GameHelper.sln
dotnet test GameHelper.sln
```

## 许可
- 开源使用：AGPL-3.0
- 商业使用：见 `LICENSE`
