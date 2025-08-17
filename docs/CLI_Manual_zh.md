# GameHelper 命令行操作手册 (CLI)

本手册介绍 Console 版本 GameHelper 的使用方法、配置文件格式与示例。

## 安装与运行前提
- .NET 8 运行时
- Windows（当前实现针对 Windows，后续可能扩展）

## 基本命令

- monitor（默认）
  - 说明：启动宿主服务，订阅进程监控事件，并通过编排服务自动记录游玩会话与切换 HDR。
  - 示例：
    ```powershell
    dotnet run --project GameHelper.ConsoleHost -- monitor
    ```
  - 备注：当前使用 `NoOpProcessMonitor` 占位，不会监听真实进程；待实现 `WmiProcessMonitor` 后生效。

- config
  - 说明：查看或修改要监控的游戏列表（配置文件：`%AppData%/GameHelper/config.json`）。
  - 子命令：
    - list：列出所有已配置的游戏
      ```powershell
      dotnet run --project GameHelper.ConsoleHost -- config list
      ```
    - add <exe>：添加一个要监控的游戏（默认 Enabled 与 HDR 均为 true）
      ```powershell
      dotnet run --project GameHelper.ConsoleHost -- config add cyberpunk2077.exe
      ```
    - remove <exe>：移除一个游戏
      ```powershell
      dotnet run --project GameHelper.ConsoleHost -- config remove cyberpunk2077.exe
      ```

- stats
  - 说明：读取 `%AppData%/GameHelper/playtime.json`，输出游玩时长统计。
  - 可选参数：
    - `--game <name>`：仅统计某个游戏
  - 示例：
    ```powershell
    dotnet run --project GameHelper.ConsoleHost -- stats
    dotnet run --project GameHelper.ConsoleHost -- stats --game witcher3.exe
    ```

## 配置文件说明

- 路径：`%AppData%/GameHelper/config.json`
- 字符串比较：对进程名大小写不敏感（如 `WITCHER3.EXE` 与 `witcher3.exe` 等价）
- 新版格式（推荐）：
  ```json
  {
    "games": [
      { "Name": "witcher3.exe", "IsEnabled": true, "HDREnabled": true },
      { "Name": "rdr2.exe",     "IsEnabled": true, "HDREnabled": true }
    ]
  }
  ```
- 兼容旧版格式（字符串数组）：
  ```json
  {
    "games": ["witcher3.exe", "rdr2.exe"]
  }
  ```
  - 载入时会按启用的默认配置转换为新版的内存结构；保存时统一写回新版格式。

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
  - 原因：当前默认使用 `NoOpProcessMonitor`。待集成 `WmiProcessMonitor` 后，`monitor` 将开始工作。
- stats 无数据？
  - 说明：需要先有一次会话的 `StartTracking`/`StopTracking` 才会生成/累计；您也可以手动构造 `playtime.json` 进行测试。

## 未来计划（CLI 相关）
- 支持更丰富的筛选（按周/月/年统计）。
- 引入 `System.CommandLine` 提升参数体验（可选）。
- YAML 配置支持（`config.yml` 导入/导出或替代）。
