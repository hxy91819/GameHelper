# GameHelper - 游戏助手

一个专为游戏玩家设计的 Console-first 工具，提供基于进程监控的自动化与游玩时长统计。

## 功能特性

### 进程监控与统计
- 事件驱动：基于 WMI 的进程启动/停止事件，实时低开销
- 游玩时长：自动记录会话并写入 `%AppData%/GameHelper/playtime.json`
- 稳健落盘：即使未收到进程停止事件，在服务停止时也会补偿落盘

### 配置与工具
- YAML 配置：`%AppData%/GameHelper/config.yml`，支持 `alias`、大小写不敏感匹配
- CLI 工具：`monitor / stats / validate-config / convert-config`

> 重要：HDR 切换功能当前为占位实现（NoOp），未对系统 HDR 状态做实际开关。后续将重新实现 HDR 控制器（可能采用 Windows API/显卡驱动接口/辅助进程方案）。

## 快速开始

### 系统要求
- Windows 11 (推荐) 或 Windows 10
- .NET 8.0 Runtime
- 管理员权限（用于进程监控）

### 运行方式（开发/验证）
```powershell
# 监控（可选全局参数：--config <path> / -c <path>，--debug / -v / --verbose）
dotnet run --project .\GameHelper.ConsoleHost -- monitor [--config <path>] [--debug]

# 统计（支持按游戏名过滤）
dotnet run --project .\GameHelper.ConsoleHost -- stats [--game <name>] [--config <path>] [--debug]

# 配置校验 / 格式转换
dotnet run --project .\GameHelper.ConsoleHost -- validate-config [--config <path>]
dotnet run --project .\GameHelper.ConsoleHost -- convert-config
```

### 发布自包含可执行
```powershell
dotnet clean .\GameHelper.ConsoleHost -c Release
dotnet publish .\GameHelper.ConsoleHost -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false
```
产物：`GameHelper.ConsoleHost/bin/Release/net8.0-windows/win-x64/publish/`

### 发布后启动示例
```powershell
$pub = ".\GameHelper.ConsoleHost\bin\Release\net8.0-windows\win-x64\publish"
& "$pub\GameHelper.ConsoleHost.exe" monitor --config "$env:APPDATA\GameHelper\config.yml" --debug

# 或查看统计：
& "$pub\GameHelper.ConsoleHost.exe" stats --config "$env:APPDATA\GameHelper\config.yml"
```

启动时会输出：
- Using config: <配置文件路径>
- Build time: yyyy-MM-dd HH:mm:ss
- Log level: Information/Debug

## 使用说明

### 配置文件（YAML）
路径：`%AppData%/GameHelper/config.yml`
```yaml
games:
  - name: "witcher3.exe"
    alias: "巫师3"
    isEnabled: true
    hDREnabled: true
```
说明：`name` 匹配进程名（大小写不敏感）；`alias` 用于显示；`hDREnabled` 为将来 HDR 控制之用，当前不会实际切换 HDR。

### 自动化工作流程
```
游戏启动 → 事件检测 → 记录会话（必要时启用HDR：待实现）
游戏关闭 → 事件检测 → 结束会话并落盘
```

## 技术特性

- 事件驱动架构：使用 WMI 事件系统监控进程
- 资源优化：空闲时几乎不消耗系统资源
- 文件容错：`playtime.json` 解析失败时自动重建

## 开发计划

见 [TODOs.md](TODOs.md)

## 贡献

欢迎提交Issue和Pull Request！

### 开发环境
- Visual Studio 2022 或 VS Code
- .NET 8.0 SDK
- Windows 11 开发环境

## 许可证

本项目采用 MIT 许可证 - 查看 [LICENSE](LICENSE) 文件了解详情

## 致谢

- 感谢 Microsoft 提供的 WMI 事件系统

---

**注意**：本程序需要管理员权限来监控系统进程。HDR 切换当前未实现（NoOp），仅记录会话与统计数据；程序不会收集或上传任何个人数据。