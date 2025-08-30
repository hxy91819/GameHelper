# GameHelper - 游戏助手

一个专为游戏玩家设计的 Console-first 工具，提供基于进程监控的自动化与游玩时长统计。

## 功能特性

1. 监听游戏启动/关闭事件，自动统计游玩时长（极小的资源占用）
2. 拖动游戏快捷方式或exe到程序的快捷方式上，自动添加到配置
3. [WIP]自动开关HDR

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

## 许可证

本项目采用双重许可证：

- **开源使用**：遵循 AGPL-3.0 许可证，适用于开源项目和非商业用途
- **商业使用**：需要购买商业许可证，适用于商业产品、SaaS应用或内部商业系统

详细信息请查看 [LICENSE](LICENSE) 文件。如需商业许可证，请联系：bwjava819@gmail.com

## 致谢

- 感谢 Microsoft 提供的 WMI 事件系统

---

**注意**：本程序需要管理员权限来监控系统进程。HDR 切换当前未实现（NoOp），仅记录会话与统计数据；程序不会收集或上传任何个人数据。