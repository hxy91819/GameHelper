# GameHelper 命令行操作手册 (CLI)

本手册介绍 Console 版本 GameHelper 的使用方法、配置文件格式与示例。

## 安装与运行前提
- .NET 8 运行时
- Windows（当前实现针对 Windows，后续可能扩展）

## 基本命令

- interactive（无命令时默认进入）
  - 说明：启动全新的互动式终端界面，可视化管理监控、配置、统计与工具。
  - 功能亮点：面板化展示当前配置、选择列表操作、Spectre.Console 表格统计等。
  - 新增体验：实时监控入口会预览最近三条记录，并在监控停止后自动汇总本次新增会话的起止时间与时长。
  - 示例：
    ```powershell
    dotnet run --project GameHelper.ConsoleHost --
    ```
  - 小贴士：任何命令前添加 `--interactive` 参数亦可强制先进入互动界面。

- monitor
  - 说明：启动宿主服务，订阅进程监控事件，并通过编排服务自动记录游玩会话与切换 HDR。
  - 当前状态：HDR 切换为占位实现（NoOp），暂不对系统 HDR 状态进行实际开关，仅记录会话。
  - 互动提示：若从互动界面进入，会在启动前提示监控模式与配置路径，结束后显示本次新增的游玩摘要及写入位置。
  - 示例：
    ```powershell
    dotnet run --project GameHelper.ConsoleHost -- monitor
    ```
  - 备注：已集成 `WmiProcessMonitor`（基于 WMI 的进程启动/停止事件）。需要 Windows 且 WMI 服务可用，建议以管理员权限运行。

// 说明：当前推荐直接编辑配置文件（见下文“配置文件说明”）。`config` 子命令可作为可选/临时工具使用。
// 如果使用：
//   dotnet run --project GameHelper.ConsoleHost -- config list|add <exe>|remove <exe>

- stats
  - 说明：读取 `%AppData%/GameHelper/playtime.json`，输出游玩时长统计。
  - 可选参数：
    - `--game <name>`：仅统计某个游戏
  - 示例：
    ```powershell
    dotnet run --project GameHelper.ConsoleHost -- stats
    dotnet run --project GameHelper.ConsoleHost -- stats --game witcher3.exe
    ```

- validate-config
  - 说明：按仓库内模板校验 `%AppData%/GameHelper/config.yml` 的结构、必填、类型与重复项。
  - 示例：
    ```powershell
    dotnet run --project GameHelper.ConsoleHost -- validate-config
    ```

## 配置文件说明（仅 YAML）

- 路径：`%AppData%/GameHelper/config.yml`
- 字符串比较：对进程名大小写不敏感（如 `WITCHER3.EXE` 与 `witcher3.exe` 等价）
- YAML 格式（支持别名 Alias）：
  ```yaml
  processMonitorType: ETW # 可选：覆盖进程监听实现
  autoStartInteractiveMonitor: true # 可选：进入互动命令行后自动启动监控
  launchOnSystemStartup: true # 可选：登录后自动启动 GameHelper（Windows 支持自动配置）
  games:
    - name: "witcher3.exe"
      alias: "巫师3"
      isEnabled: true
      hDREnabled: true
  ```
- 提示：
- `Name` 为唯一标识（规范 exe 名）。
- `Alias` 为可选的显示名称，不影响匹配与统计，只影响显示。
- `hDREnabled` 为未来 HDR 控制器使用的标记，当前不会实际切换 HDR。
- `autoStartInteractiveMonitor` 设为 `true` 时，启动互动命令行会直接进入“实时监控”，按 `Q` 可以返回主菜单。
- `launchOnSystemStartup` 控制是否在系统登录后自动启动 GameHelper；该选项可在互动命令行的“全局设置”中切换。
  互动界面会提示当前系统状态，若与配置不一致也可以直接在此处一键同步。

### 旧配置迁移
- 如你仍有旧的 `config.json`，可运行一次：
  ```powershell
  dotnet run --project GameHelper.ConsoleHost -- convert-config
  ```
  将在同目录生成 `config.yml`。

## 游玩时长文件说明

- 路径（现状）：`%AppData%/GameHelper/playtime.json`
- 结构与旧 WPF UI 对齐，根键为 `games`：
  ```json
  {
    "games": [
      {
        "GameName": "witcher3.exe",
        "Sessions": [
          { "StartTime": "2025-08-16T10:00:00", "EndTime": "2025-08-16T11:40:00", "DurationMinutes": 100 }
        ]
      }
    ]
  }
  ```
- 写入时机：在 `StopTracking(game)` 时记录该会话并落盘（当前实现为重写 JSON）。
- 容错：当文件损坏或无法解析时，系统会忽略旧内容并从内存的当前数据重新写入。

- 即将变更（设计）：切换为 CSV 逐行追加存储，便于“一个 session 一行”的增量写入
  - 路径：`%AppData%/GameHelper/playtime.csv`
  - 表头：`game,start_time,end_time,duration_minutes`
  - 行格式示例：`witcher3.exe,2025-08-16T10:00:00,2025-08-16T11:40:00,100`
  - 编码：UTF-8（无 BOM）；日期时间使用 ISO-8601（本地时间）；按需使用双引号转义包含分隔符的字段
  - 写入策略：`StopTracking(game)` 时仅追加一行，不再重写全文件
  - 兼容策略：`stats` 将优先读取 CSV；若不存在 CSV 则回退读取 JSON（过渡期保障）

## 运行日志
- 控制台输出包含基础日志（启动、停止、错误信息等）。
- 日志系统：`Microsoft.Extensions.Logging` 控制台提供程序。
- 当监控到游戏进程退出时，会额外打印本次游玩时长，便于快速确认记录结果。

## 进程匹配与监听策略

* __事件来源__
  - `Infrastructure/Processes/WmiProcessMonitor` 订阅 `Win32_ProcessStartTrace/StopTrace`。
  - 内部 `WmiEventWatcher` 使用事件中的 `ProcessID` 反查 `Win32_Process(Name, ExecutablePath)`，避免名称截断。
  - 维护短期缓存：在 Start 事件记录 `PID -> Name`，Stop 时若进程已消失无法反查则回退使用缓存；仍失败则回退 `ProcessName`。

* __名称规范化（Normalize）__
  - 在 `GameAutomationService.Normalize()` 将任意输入规整为“仅文件名 + 确保 .exe 后缀”，统一与配置键对齐。

* __模糊干（Stem）__
  - 在 `GameAutomationService.Stem()` 将文件名去扩展名、去标点，仅保留字母数字并转小写，用于宽松匹配（容忍轻微变体/截断）。

* __Start 流程（`OnProcessStarted()`）__
  - 先 Normalize 得到键 `key`。
  - 若 `key` 在配置中启用（`IsEnabled(key)`）→ 直接开始追踪。
  - 若未启用：对“已启用的配置键”按 Stem 做唯一候选的模糊匹配（仅当候选恰好为 1 个时命中），将 `key` 映射为该规范键。
  - 追踪开始后，若这是第一个活跃游戏，则调用 `IHdrController.Enable()`（当前实现为 NoOp）。

* __Stop 流程（`OnProcessStopped()`）__
  - Normalize 后尝试从 `_active` 精确移除；失败则在“当前活跃集合”中按 Stem 做唯一候选的模糊移除。
  - 会话结束写入 `playtime.json`；若活跃集合清空，则调用 `IHdrController.Disable()`（当前为 NoOp）。

* __为何不在 WMI 层按配置过滤？__
  - Start/Stop 事件的名称可能不一致（启动器/外壳进程、x86/x64 变体等），WQL 级别白名单容易漏报，导致错过 Start 或 Stop。
  - 现行策略：WMI 监听“全量进程”，在 Core 层基于配置做精确+模糊的判定与去噪，鲁棒性更高；性能开销可接受。

* __配置建议__
  - 常规仅需配置 `games[].name`（规范 exe 名），必要时可为个别游戏添加 `alias` 便于展示。
  - 若遇到多个可执行变体导致歧义，可在配置中新增更明确的条目（或后续引入 `aliases`/`paths`/`pattern` 等增强字段）。

## 常见问题
- monitor 无法感知游戏启动/退出？
  - 请确认：
    - Windows WMI 服务已启动（服务名 Windows Management Instrumentation）。
    - 以管理员权限运行控制台（或具备订阅进程事件的权限）。
    - 安全软件未拦截 WMI 事件订阅。
    - `config.yml` 中的 `name` 与实际进程名完全匹配（大小写不敏感）。
- stats 无数据？
  - 说明：需要先有一次会话的 `StartTracking`/`StopTracking` 才会生成/累计；您也可以手动构造 `playtime.json` 进行测试。

## 未来计划（CLI 相关）
- 支持更丰富的筛选（按周/月/年统计）。
- 引入 `System.CommandLine` 提升参数体验（可选）。
- 优先 YAML 配置已支持；后续补充示例与验证工具。

## 打包与发布（Console 可执行程序）

- 目标：生成 Windows x64 的单文件可执行产物，便于分发与部署。

### 发布命令

1) 框架依赖（体积较小，需安装 .NET 运行时）：
```powershell
dotnet publish GameHelper.ConsoleHost \
  -c Release \
  -r win-x64 \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=true \
  --self-contained false
```

2) 自包含（无需安装 .NET，体积较大）：
```powershell
dotnet publish GameHelper.ConsoleHost \
  -c Release \
  -r win-x64 \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  --self-contained true
```

- 输出目录：
  - 框架依赖：`GameHelper.ConsoleHost/bin/Release/net8.0-windows/win-x64/publish/`
  - 自包含：同上

### 使用说明

- 将生成的可执行文件（例如 `GameHelper.ConsoleHost.exe`）放置在任意目录运行。
- 配置文件路径：`%AppData%/GameHelper/config.yml`
  - 首次使用可手动创建该文件并参照仓库模板：`GameHelper.Infrastructure/Validators/config.template.yml`
- 常用命令：
  ```powershell
  .\GameHelper.ConsoleHost.exe monitor
  .\GameHelper.ConsoleHost.exe stats [--game <name>]
  .\GameHelper.ConsoleHost.exe validate-config
  .\GameHelper.ConsoleHost.exe convert-config
  ```

### 提示

- 若需开机自启，可在“任务计划程序”中创建任务，触发器为用户登录，操作指向发布目录的 EXE。
- 需要 WMI 服务与管理员权限以确保进程监控事件正常。
