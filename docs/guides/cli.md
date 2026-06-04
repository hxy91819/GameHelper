# GameHelper CLI 使用指南

本文档描述当前仓库中 `GameHelper.ConsoleHost` 的主要用法。若命令或参数发生变化，应同步更新本文和 `README.md`。

## 运行前提

- Windows 10/11。
- .NET 8 SDK 或已发布的可执行文件。
- 若使用 ETW，建议以管理员权限启动；权限不足时会自动回退到 WMI。

## 启动方式

```powershell
# 默认进入交互式 Shell
dotnet run --project .\GameHelper.ConsoleHost --

# 显式进入交互式 Shell
dotnet run --project .\GameHelper.ConsoleHost -- interactive
```

## 常用命令

```powershell
# 启动监控
dotnet run --project .\GameHelper.ConsoleHost -- monitor

# 指定监控方式
dotnet run --project .\GameHelper.ConsoleHost -- monitor --monitor-type ETW
dotnet run --project .\GameHelper.ConsoleHost -- monitor --monitor-type WMI

# 查看统计
dotnet run --project .\GameHelper.ConsoleHost -- stats
dotnet run --project .\GameHelper.ConsoleHost -- stats --game <dataKey>

# 配置管理
dotnet run --project .\GameHelper.ConsoleHost -- config list
dotnet run --project .\GameHelper.ConsoleHost -- config add <exe>
dotnet run --project .\GameHelper.ConsoleHost -- config remove <entryId|dataKey>

# 配置迁移与校验
dotnet run --project .\GameHelper.ConsoleHost -- convert-config
dotnet run --project .\GameHelper.ConsoleHost -- validate-config
dotnet run --project .\GameHelper.ConsoleHost -- migrate
```

## 配置文件

- 默认路径：`%AppData%\GameHelper\config.yml`
- 当前默认监听方式：`ETW`
- 仍可显式配置 `processMonitorType: WMI`

示例：

```yaml
processMonitorType: ETW
autoStartInteractiveMonitor: false
launchOnSystemStartup: false

games:
  - entryId: "8c5f5ccf30b648f88f4d2f1f8b4b6c7e"
    dataKey: "witcher3"
    executablePath: "D:\\Games\\The Witcher 3\\bin\\x64\\witcher3.exe"
    executableName: "witcher3.exe"
    displayName: "巫师3"
    isEnabled: true
    hdrEnabled: false
```

字段说明：

- `dataKey`：统计与历史数据的稳定标识。
- `executablePath`：路径精确匹配使用的完整路径。
- `executableName`：旧配置或回退匹配使用的可执行文件名。
- `displayName`：界面显示名称。

## 拖放添加游戏

- 支持拖放 `.exe`、`.lnk`、`.url` 到 `GameHelper.ConsoleHost.exe`。
- 若主实例已运行，新的拖放请求会自动转发给主实例处理。
- 系统会尝试提取 `ProductName` 并生成建议的 `dataKey`。
- 保存后配置会立即写入 `config.yml`。

## 数据文件

- 配置：`%AppData%\GameHelper\config.yml`
- 游玩时长：`%AppData%\GameHelper\playtime.csv`

`stats` 命令按 `dataKey` 聚合，优先显示 `displayName`。
