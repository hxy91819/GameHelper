# GameHelper - 游戏助手

一个专为游戏玩家设计的 Console-first 工具，提供基于进程监控的自动化与游玩时长统计。

## 功能特性

- **进程监控**: 支持 WMI 和 ETW 两种监控方式自动检测游戏进程的启动和退出
  - **WMI 监控**: 兼容性最好，适用于所有 Windows 版本
  - **ETW 监控**: 低延迟高性能，需要管理员权限
- **游戏时间统计**: 记录每个游戏的运行时间
- **HDR 控制**: 为将来的 HDR 自动切换预留接口（当前为空实现）
- **配置管理**: 支持 YAML 配置文件，可添加/移除游戏
- **命令行界面**: 提供监控、配置管理、统计查看等功能
- **自动降级**: ETW 监控失败时自动回退到 WMI 监控
- **拖拽添加**: 拖动游戏快捷方式或exe到程序的快捷方式上，自动添加到配置

## 快速开始

### 系统要求
- Windows 11 (推荐) 或 Windows 10
- .NET 8.0 Runtime
- 管理员权限（用于进程监控）

### 运行方式（开发/验证）
```powershell
# 启动监控（默认使用 WMI）
dotnet run --project .\GameHelper.ConsoleHost -- monitor [--config <path>] [--debug]

# 使用 ETW 监控（需要管理员权限，更低延迟）
dotnet run --project .\GameHelper.ConsoleHost -- monitor --monitor-type ETW [--config <path>] [--debug]

# 使用 WMI 监控（兼容性最好）
dotnet run --project .\GameHelper.ConsoleHost -- monitor --monitor-type WMI [--config <path>] [--debug]

# 统计（支持按游戏名过滤）
dotnet run --project .\GameHelper.ConsoleHost -- stats [--game <name>] [--config <path>] [--debug]

# 配置管理
dotnet run --project .\GameHelper.ConsoleHost -- config list [--config <path>] [--debug]
dotnet run --project .\GameHelper.ConsoleHost -- config add <exe> [--config <path>] [--debug]
dotnet run --project .\GameHelper.ConsoleHost -- config remove <exe> [--config <path>] [--debug]

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

### GitHub Release 自动化

仓库内置的 GitHub Actions 工作流会在推送形如 `v*` 的标签时自动构建自包含的 Win-x64 可执行文件，并将压缩包上传到 Release 资产中。如果你想发布 `0.0.1` 版本，可以执行：

```powershell
git tag v0.0.1
git push origin v0.0.1
```

也可以通过 GitHub 页面手动触发 `Release` 工作流，并在输入参数中指定要发布的标签（例如 `v0.0.1`）。

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
# 全局设置
processMonitorType: ETW  # 可选: WMI (默认) 或 ETW

# 游戏配置
games:
  - name: "witcher3.exe"
    alias: "巫师3"
    isEnabled: true
    hdrEnabled: true
```

**配置说明**：
- `processMonitorType`: 进程监控方式，可选 `WMI`（默认）或 `ETW`
- `name`: 匹配进程名（大小写不敏感）
- `alias`: 显示名称
- `isEnabled`: 是否启用该游戏的所有自动化功能
- `hdrEnabled`: 为将来 HDR 控制预留，当前不会实际切换 HDR

### 自动化工作流程

1. **进程检测**: 使用 WMI 或 ETW 监控系统进程启动/退出事件
2. **游戏识别**: 根据配置文件中的 `name` 字段匹配进程
3. **状态管理**: 跟踪活跃游戏进程，记录启动/停止时间
4. **HDR 控制**: 当有游戏运行时调用 HDR 控制器（当前为空实现）
5. **时间统计**: 将游戏运行时间保存到 CSV 文件

### 进程监控方式对比

| 特性 | WMI | ETW |
|------|-----|-----|
| **延迟** | 1-3 秒 | < 1 秒 |
| **权限要求** | 普通用户 | 管理员 |
| **兼容性** | 所有 Windows 版本 | Windows Vista+ |
| **资源占用** | 中等 | 低 |
| **推荐场景** | 日常使用 | 对延迟敏感的场景 |

## 技术特性

- **双重监控架构**: 支持 WMI 和 ETW 两种进程监控方式
- **自动降级机制**: ETW 失败时自动回退到 WMI，确保服务可用性
- **事件驱动架构**: 使用 Windows 原生事件系统监控进程
- **资源优化**: 空闲时几乎不消耗系统资源
- **文件容错**: 配置和数据文件解析失败时自动重建
- **命令行优先**: 完整的 CLI 界面，支持脚本化和自动化

## 命令行选项

```
GameHelper Console
Usage:
  monitor [--config <path>] [--monitor-type <type>] [--debug]
  config list [--config <path>] [--debug]
  config add <exe> [--config <path>] [--debug]
  config remove <exe> [--config <path>] [--debug]
  stats [--game <name>] [--config <path>] [--debug]
  convert-config
  validate-config

Global options:
  --config, -c       Override path to config.yml
  --monitor-type     Process monitor type: WMI (default) or ETW
                     ETW provides lower latency but requires admin privileges
  --debug, -v        Enable verbose debug logging
```

### 监控类型选择优先级

1. **命令行参数** `--monitor-type` （最高优先级）
2. **配置文件** `processMonitorType` 设置
3. **默认值** WMI（最低优先级）

## 故障排除

### 常见问题

1. **进程检测不到**
   - 检查游戏名称是否正确配置
   - 确认游戏进程确实在运行
   - 查看调试日志 `--debug`
   - 尝试切换监控方式（WMI ↔ ETW）

2. **ETW 监控问题**
   - **权限不足**: ETW 需要管理员权限，请以管理员身份运行
   - **自动降级**: 如果 ETW 失败，程序会自动切换到 WMI
   - **防火墙/安全软件**: 某些安全软件可能阻止 ETW 访问

3. **配置文件问题**
   - 使用 `validate-config` 命令检查配置
   - 确保 YAML 格式正确
   - 检查 `processMonitorType` 值是否为 `WMI` 或 `ETW`

4. **权限问题**
   - 确保有写入配置目录的权限
   - 检查 CSV 文件的写入权限

### ETW 监控最佳实践

- **开发/测试**: 使用 WMI 监控，无需管理员权限
- **生产环境**: 使用 ETW 监控获得最佳性能
- **服务器部署**: 配置为 Windows 服务并以管理员权限运行
- **故障恢复**: 利用自动降级机制确保服务可用性

## 性能优化

### 监控方式选择建议

**使用 ETW 的场景**:
- 需要快速响应游戏启动（< 1秒）
- 系统资源充足
- 可以获得管理员权限
- 对延迟敏感的自动化场景

**使用 WMI 的场景**:
- 无法获得管理员权限
- 系统兼容性要求高
- 对延迟不敏感（1-3秒可接受）
- 稳定性优先的生产环境

### 配置优化

```yaml
# 高性能配置
processMonitorType: ETW
games:
  - name: "game.exe"
    isEnabled: true    # 只监控需要的游戏
    hdrEnabled: false  # 暂时禁用未实现的功能
```

## 开发计划

### 核心自动化与监控
- [x] 游戏启动时自动计时
  - [ ] 优化计时清单页面
- [ ] 进程监听性能优化
  - [x] 过滤监听：未启动游戏时不监听
  - [ ] 只监听配置列表中的进程
  - [ ] 游戏第一次停止之后，记录fuzzyname，下次可以进行精准进程监听，进一步提升性能
- [ ] 弱提示，避免魂游这类游戏被误终止
- [ ] 自动开关HDR
  - [ ] 检测当前HDR开启状态
  - [ ] 提前开启HDR，避免部分游戏内部未识别HDR（当前实现太粗糙，遇到核心技术问题，暂缓实现）

### 游戏库管理与发现
- [x] 拖动图标到程序上自动添加
  - [x] 拖动时自动以快捷方式名替换 alias
- [x] 支持 Steam URL：steam://rungameid/<appid>
- [ ] 自动扫描游戏（合并原重复项）
  - [ ] 支持多平台及“学习版”来源

### 数据与存储
- [x] 存储格式由 JSON 改为 CSV（支持追加写入，避免全量写）
- [ ] 本地存储 + 云端同步
- [ ] 魂游自动复制存档

### 交互与易用性
- [ ] 支持手柄
- [ ] 自定义快捷键
- [ ] 将命令行改为交互式命令行，提升体验

### 界面与显示
- [ ] 可显示在游戏窗口上方（叠加/OSD）
- [ ] 套用游戏图标进行显示
- [ ] 游戏方向深度适配（不同游戏风格）

### 健康与提醒
- [ ] 番茄钟：提醒活动与用眼休息

### 发布与维护
- [ ] 自动更新


## 贡献

欢迎提交Issue和Pull Request！

### 开发环境
- Visual Studio 2022 或 VS Code
- .NET 8.0 SDK
- Windows 11 开发环境

### 项目结构

- `GameHelper.Core`: 核心业务逻辑和抽象接口
- `GameHelper.Infrastructure`: 基础设施实现（WMI、ETW、文件操作等）
- `GameHelper.ConsoleHost`: 控制台应用程序入口
- `GameHelper.Tests`: 单元测试和集成测试

### 进程监控架构

```
IProcessMonitor (接口)
├── WmiProcessMonitor (WMI 实现)
├── EtwProcessMonitor (ETW 实现)
└── NoOpProcessMonitor (测试用空实现)

ProcessMonitorFactory
├── Create() - 创建指定类型的监控器
├── CreateWithFallback() - 创建带降级的监控器
└── CreateNoOp() - 创建空监控器
```

### 技术栈

- **.NET 8**: 现代 C# 开发平台
- **WMI**: Windows 进程监控（兼容性方案）
- **ETW**: Event Tracing for Windows（高性能方案）
- **Microsoft.Diagnostics.Tracing.TraceEvent**: ETW 事件处理
- **YamlDotNet**: YAML 配置文件解析
- **xUnit**: 单元测试框架
- **Microsoft.Extensions.Hosting**: 后台服务框架

## 许可证

本项目采用双重许可证：

- **开源使用**：遵循 AGPL-3.0 许可证，适用于开源项目和非商业用途
- **商业使用**：需要购买商业许可证，适用于商业产品、SaaS应用或内部商业系统

详细信息请查看 [LICENSE](LICENSE) 文件。如需商业许可证，请联系：bwjava819@gmail.com

## 致谢

- 感谢 Microsoft 提供的 WMI 事件系统和 ETW 框架
- 感谢 Microsoft.Diagnostics.Tracing.TraceEvent 项目提供的 ETW 支持

---

**注意**：
- ETW 监控需要管理员权限，WMI 监控可在普通用户权限下运行
- HDR 切换当前未实现（NoOp），仅记录会话与统计数据
- 程序不会收集或上传任何个人数据
- 建议在生产环境中使用 ETW 监控以获得最佳性能