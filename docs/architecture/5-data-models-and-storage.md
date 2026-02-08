# 5. Data Models and Storage

## 5.1. Data Models

  * **游戏配置**: `GameHelper.Core/Models/GameConfig.cs`。**(已更新)** 定义了用户在 `config.yml` 中配置的每个游戏条目。
      * `DataKey` (string, 必需): 关联 `playtime.csv` 的唯一标识符（例如 "Elden Ring"）。
      * `ExecutablePath` (string, 可选): 用于 L1 精确路径匹配的完整路径。
      * `ExecutableName` (string, 可选): 用于 L2 回退匹配的 .exe 名称。
      * `DisplayName` (string, 可选): 用于 UI 显示的友好名称。
  * **应用配置**: `GameHelper.Core/Models/AppConfig.cs`。(未更改) 定义了全局设置，如 `ProcessMonitorType`。

## 5.2. Data Storage

  * **游戏时长**: 数据存储在 `playtime.csv` 文件中。**(已更新)** 这是一个 CSV 文件，包含 `GameName`, `StartTime`, `EndTime`, `DurationSeconds` 等字段。`GameName` 列现在由 `GameConfig.DataKey` 填充和关联，以确保历史数据的连续性。
