# GameHelper 命令行操作手册 (CLI)

本手册介绍 Console 版本 GameHelper 的使用方法、配置文件格式与示例。

## 安装与运行前提
- .NET 8 运行时
- Windows（当前实现针对 Windows，后续可能扩展）

## 基本命令

- monitor（默认）
  - 说明：启动宿主服务，订阅进程监控事件，并通过编排服务自动记录游玩会话与切换 HDR。
  - 当前状态：HDR 切换为占位实现（NoOp），暂不对系统 HDR 状态进行实际开关，仅记录会话。
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

### 旧配置迁移
- 如你仍有旧的 `config.json`，可运行一次：
  ```powershell
  dotnet run --project GameHelper.ConsoleHost -- convert-config
  ```
  将在同目录生成 `config.yml`。

## 游玩时长文件说明

- 路径：`%AppData%/GameHelper/playtime.json`
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
- 写入时机：在 `StopTracking(game)` 时追加一条会话并落盘。
- 容错：当文件损坏或无法解析时，系统会忽略旧内容并从内存的当前数据重新写入。

## 运行日志
- 控制台输出包含基础日志（启动、停止、错误信息等）。
- 日志系统：`Microsoft.Extensions.Logging` 控制台提供程序。

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
