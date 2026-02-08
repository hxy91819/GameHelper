# GameHelper - 游戏助手

一个专为游戏玩家设计的 Console-first 工具，提供基于进程监控的自动化与游玩时长统计。

## 功能特性

- **进程监控**: 支持 WMI 和 ETW 两种监控方式自动检测游戏进程的启动和退出
  - **WMI 监控**: 兼容性最好，适用于所有 Windows 版本
  - **ETW 监控**: 低延迟高性能，需要管理员权限
- **游戏时间统计**: 记录每个游戏的运行时间
- **HDR 控制**: 为将来的 HDR 自动切换预留接口（当前为空实现）
- **配置管理**: 支持 YAML 配置文件，可添加/移除游戏
- **交互式命令行**: 默认提供图形化的终端界面，可视化管理监控、配置、统计与工具
- **自动降级**: ETW 监控失败时自动回退到 WMI 监控
- **拖拽添加**: 拖动游戏快捷方式或exe到程序的快捷方式上，自动添加到配置

## 快速开始

### 系统要求
- Windows 11 (推荐) 或 Windows 10
- .NET 8.0 Runtime
- 管理员权限（用于进程监控）

### 运行方式（开发/验证）
```powershell
# 体验全新互动命令行（无命令时默认进入）
dotnet run --project .\GameHelper.ConsoleHost --

# 启动监控（默认使用 ETW，需要管理员权限）
dotnet run --project .\GameHelper.ConsoleHost -- monitor [--config <path>] [--debug]

# 显式使用 ETW 监控（需要管理员权限，更低延迟）
dotnet run --project .\GameHelper.ConsoleHost -- monitor --monitor-type ETW [--config <path>] [--debug]

# 使用 WMI 监控（兼容性最好，无需管理员权限）
dotnet run --project .\GameHelper.ConsoleHost -- monitor --monitor-type WMI [--config <path>] [--debug]

# Dry-run 演练（跳过实时监控模块，便于跨平台验证）
dotnet run --project .\GameHelper.ConsoleHost -- monitor --monitor-dry-run [--config <path>] [--debug]

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

互动模式会在终端中提供面板、选择列表与可视化表格，帮助你：

- 一目了然地查看当前生效的配置文件、监控模式与日志级别。
- 通过选择和表单快速添加、编辑或删除游戏条目，并控制别名/启用状态/HDR 设置。
- 查看两周内的游戏时长统计，自动汇总总时长与会话次数。
- 一键触发配置转换与校验工具，结果将以高亮表格方式呈现。

### Web UI 管理界面

GameHelper 内置了一个 Web 管理界面，可通过浏览器查看游戏统计和管理配置。

```powershell
# 启动时同时开启 Web 服务（默认端口 5123）
dotnet run --project .\GameHelper.ConsoleHost -- --web

# 指定端口
dotnet run --project .\GameHelper.ConsoleHost -- --web --port 8080

# 搭配其他选项使用
dotnet run --project .\GameHelper.ConsoleHost -- --web --monitor-dry-run --debug
```

启动后在浏览器访问 `http://127.0.0.1:5123`（生产模式）或 `http://localhost:3000`（开发模式）。

Web 界面提供以下功能页面：
- **Dashboard**: 统计概览、最近游玩时间柱状图
- **Game Library**: 游戏配置 CRUD（添加、编辑、删除、开关监控/HDR）
- **Statistics**: 游戏时长排行、每日趋势图、单游戏详情
- **Settings**: 全局设置（监控方式、自启动等）

#### 前端开发模式

```powershell
# 1. 启动后端 API
dotnet run --project .\GameHelper.ConsoleHost -- --web --monitor-dry-run

# 2. 在另一个终端启动前端开发服务器
cd GameHelper.Web
npm install
npm run dev
```

前端开发服务器默认运行在 `http://localhost:3000`，通过环境变量 `NEXT_PUBLIC_API_URL` 连接后端 API（默认 `http://localhost:5123`）。

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
processMonitorType: ETW  # 可选: ETW (默认，需管理员权限) 或 WMI (兼容性最好)

# 游戏配置
games:
  - name: "witcher3.exe"
    alias: "巫师3"
    isEnabled: true
    hdrEnabled: true
```

**配置说明**：
- `processMonitorType`: 进程监控方式，可选 `ETW`（默认，需管理员权限）或 `WMI`（兼容性最好）
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
  interactive         启动全新的互动命令行体验（无命令时默认）
  monitor [--config <path>] [--monitor-type <type>] [--debug]
  config list [--config <path>] [--debug]
  config add <exe> [--config <path>] [--debug]
  config remove <exe> [--config <path>] [--debug]
  stats [--game <name>] [--config <path>] [--debug]
  convert-config
  validate-config

Global options:
  --config, -c       Override path to config.yml
  --monitor-type     Process monitor type: ETW (default) or WMI
                     ETW provides lower latency but requires admin privileges
                     WMI works without admin privileges but has higher latency
  --monitor-dry-run  Dry-run monitor flow without starting background services
  --debug, -v        Enable verbose debug logging
  --interactive      强制进入互动模式（等价于 interactive 命令）
  --web              启动内嵌 Web 管理界面（默认端口 5123）
  --port <number>    指定 Web 服务端口（默认 5123）
```

### 监控类型选择优先级

1. **命令行参数** `--monitor-type` （最高优先级）
2. **配置文件** `processMonitorType` 设置
3. **默认值** ETW（最低优先级，需管理员权限）

**注意**：如果 ETW 初始化失败（如非管理员权限），系统会自动降级到 WMI 监控。

### 从旧版本迁移

**如果你从旧版本（默认 WMI）升级到新版本（默认 ETW）**：

1. **无需修改配置**：如果你有管理员权限，新版本会自动使用 ETW，性能更好
2. **继续使用 WMI**：如果你希望保持使用 WMI（如无管理员权限），在配置文件中添加：
   ```yaml
   processMonitorType: WMI
   ```
3. **自动降级**：如果你没有管理员权限且未配置 WMI，程序会自动降级到 WMI 并记录警告日志

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
- [ ] 支持 Steam URL：steam://rungameid/<appid>
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
- `GameHelper.ConsoleHost`: 控制台应用程序入口（含 ASP.NET Core Minimal API）
- `GameHelper.Web`: Web 前端（Next.js + TypeScript + Tailwind CSS + shadcn/ui）
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
- **ASP.NET Core Minimal API**: Web API 端点
- **Next.js 16**: Web 前端框架（App Router + TypeScript）
- **Tailwind CSS + shadcn/ui**: UI 组件库
- **Recharts**: 数据可视化图表
- **SWR**: 数据请求与缓存

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
- **默认使用 ETW 监控**，需要管理员权限才能正常运行
- 如果 ETW 初始化失败，程序会自动降级到 WMI 监控
- 如无法获得管理员权限，建议在配置文件中显式设置 `processMonitorType: WMI`
- HDR 切换当前未实现（NoOp），仅记录会话与统计数据
- 程序不会收集或上传任何个人数据