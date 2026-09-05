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
dotnet run --project .\GameHelper.ConsoleHost -- config add <exe|path-to-exe>
dotnet run --project .\GameHelper.ConsoleHost -- config import-steam
dotnet run --project .\GameHelper.ConsoleHost -- config remove <dataKey>

# 配置工具与校验
dotnet run --project .\GameHelper.ConsoleHost -- convert-config
dotnet run --project .\GameHelper.ConsoleHost -- validate-config
dotnet run --project .\GameHelper.ConsoleHost -- migrate

# 统计推送
dotnet run --project .\GameHelper.ConsoleHost -- sync now [--force]
dotnet run --project .\GameHelper.ConsoleHost -- sync test
dotnet run --project .\GameHelper.ConsoleHost -- sync status
```

`migrate` 会复用 Core 监控匹配阈值迁移旧 `playtime.csv`，只自动改写唯一的精确或模糊匹配；歧义记录会保留给人工处理。

`config list` 会输出每个条目的 `dataKey`、`displayName`、启用状态和 HDR 设置，便于核对本地配置。

## 统计推送（sync）

把聚合后的游戏时长统计自动推送到 GitHub 仓库。详见 [sync 指南](sync.md)。

- `sync now [--force]`：立即推送一次。默认会跳过“未到间隔/无新数据/内容一致”并给出原因；`--force` 穿透间隔与失败退避。
- `sync test`：校验配置与渠道可达性（git 方式验证推送凭据，api 方式验证 token 与仓库/分支），不写入数据。
- `sync status`：查看启用状态、目标仓库、上次成功/失败时间与待推送数据。

自动推送随 `monitor` 命令在后台运行（启动延迟 3 分钟，之后每 15 分钟做一次 mtime 轻量检查，默认每天最多推送一次）。`sync` 命令受单实例约束：监控实例运行期间无法执行，可先关闭或临时设置 `GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE=1`。

## 配置文件

- 默认路径：`%AppData%\GameHelper\config.yml`
- 当前默认监听方式：`ETW`
- 仍可显式配置 `monitor: WMI`

示例：

```yaml
monitor: ETW
startup:
  autoStartMonitor: false
  launchOnStartup: false
sync:
  enabled: false
  provider: github
  method: git
  repo: yourname/game-stats
  directory: game-stats
  intervalMinutes: 1440
games:
  - dataKey: witcher3
    executable: "D:\\Games\\The Witcher 3\\bin\\x64\\witcher3.exe"
    displayName: 巫师3
    enabled: true
    hdr: false
```

字段说明：

- `dataKey`：统计与历史数据的稳定标识。
- `executable`：可执行文件路径或进程文件名；路径会用于精确匹配，文件名会自动从路径派生。
- `displayName`：界面显示名称。
- `enabled`：是否参与监控、时长统计和自动化。
- `hdr`：是否在该游戏运行时由 GameHelper 自动开启 HDR；`false` 不会关闭用户已经手动开启的 HDR。
- `startup.autoStartMonitor`：交互模式启动后是否自动进入实时监控。
- `startup.launchOnStartup`：是否随系统启动 GameHelper。
- `sync`：统计推送配置（上述为节选，完整字段与 token 说明见 [sync 指南](sync.md)）；省略整段表示未配置推送。

`config add` 可接收可执行文件名或 `.exe` 路径，并统一保存到 `executable`；运行时会从该字段派生路径匹配与候选进程名。

`config import-steam` 会扫描 Steam 主目录及 `libraryfolders.vdf` 中的库，读取已安装游戏的 `appmanifest_*.acf`，为每个游戏选择一个候选主可执行文件，并通过一次批量写入添加到配置。相同可执行文件已存在时会更新其显示名称和启用状态。

交互式 Shell 的“配置管理”也提供“扫描并导入 Steam 游戏”入口：会先显示候选游戏清单，用户确认后才写入配置。

所有配置写操作都会在最新完整文档上应用一次变更，再通过临时文件原子替换；游戏清单、监控设置和启动设置不会因彼此更新而丢失。

## 实时监控与历史记录预览

交互式 Shell 选择“实时监控”后，启动监控前会展示“历史记录预览”面板：

- 按游戏聚合最近 7 个自然日（含今天）的游玩记录，显示次数、总时长和最近游玩时间，按总时长排序；同一天多次启动同一游戏不再重复占行。
- 面板下方附最近 7 天的每日游玩时长趋势条形图（按会话结束时间的本地日期归属）。
- 窗口外的旧会话不参与预览统计；没有任何窗口内记录时会显示占位提示。

会话结束（按 Q 返回）后，仍会像之前一样汇总本次新增的游玩记录。

## 拖放添加游戏

- 支持拖放 `.exe`、`.lnk`、`.url` 到 `GameHelper.ConsoleHost.exe`。
- 若主实例已运行，新的拖放请求会自动转发给主实例处理。
- 转发后的批次使用主实例当前配置路径；IPC 请求不能切换主实例的 `--config`。
- 系统会尝试提取 `ProductName` 并生成建议的 `dataKey`。
- 整个批次通过一次 Game Catalog Intake 提交，成功后只触发一次监控配置热重载。

## 数据文件

- 配置：`%AppData%\GameHelper\config.yml`
- 游玩时长：`%AppData%\GameHelper\playtime.csv`

`stats` 命令按 `dataKey` 聚合，优先显示 `displayName`。
