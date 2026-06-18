# GameHelper CLI 使用指南

本文档描述当前仓库中 `GameHelper.ConsoleHost` 的主要用法。若命令或参数发生变化，应同步更新本文和 `README.md`。

## 运行前提

- Windows 10/11。
- .NET 8 SDK 或已发布的可执行文件。
- 若使用 ETW，建议以管理员权限启动；权限不足时会自动回退到 WMI。
- ETW 和 WMI 都只对已启用配置中的候选进程执行后续路径/详情解析。

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

`migrate` 会复用 Core 监控匹配阈值迁移旧 `playtime.csv`，只自动改写唯一的精确或模糊匹配；歧义记录会保留给人工处理。

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
    hDREnabled: false
```

字段说明：

- `dataKey`：统计与历史数据的稳定标识。
- `executablePath`：路径精确匹配使用的完整路径。
- `executableName`：旧配置或回退匹配使用的可执行文件名。
- `displayName`：界面显示名称。
- `hDREnabled`：是否在该游戏运行时由 GameHelper 自动开启 HDR；`false` 不会关闭用户已经手动开启的 HDR。

## 拖放添加游戏

- 支持拖放 `.exe`、`.lnk`、`.url` 到 `GameHelper.ConsoleHost.exe`。
- 若主实例已运行，新的拖放请求会自动转发给主实例处理。
- 系统会尝试提取 `ProductName` 并生成建议的 `dataKey`。
- 保存后配置会立即写入 `config.yml`。

## 数据文件

- 配置：`%AppData%\GameHelper\config.yml`
- 游玩时长：`%AppData%\GameHelper\playtime.csv`

`stats` 命令按 `dataKey` 聚合，优先显示 `displayName`。
