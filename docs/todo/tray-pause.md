# feat/in-house-tray 分支暂停说明

## 状态
**暂停，未合并到 main。**

## 已实现的探索内容

本分支尝试为 GameHelper.ConsoleHost 添加系统托盘图标功能，包含以下实现：

- TrayIconService — 基于 System.Windows.Forms.NotifyIcon 的托盘图标服务
- ConsoleWindowHelper — P/Invoke 封装，用于隐藏/显示控制台窗口
- HideSmokeTest / TrayIconSmokeTest — 托盘图标功能的自动化验证测试
- --tray 启动参数 — 通过 ArgumentParser 支持托盘模式启动
- Program.cs 启动逻辑 — 托盘模式下的启动流程适配

调研文档见 [	ray.md](./tray.md)。

## 暂停原因

### 核心问题：Console 程序做托盘，技术原理上不成立

GameHelper.ConsoleHost 是一个 **Console Application**（OutputType = Exe），其运行模型天然绑定了一个可见的控制台窗口：

1. **进程类型限制**：Console 进程启动时，Windows 会自动为其分配一个控制台宿主（conhost.exe）。即使通过 P/Invoke 隐藏窗口句柄，这个控制台宿主仍然附着在进程上，无法在架构层面"变成"一个没有控制台的托盘程序。

2. **输出重定向丢失**：一旦隐藏控制台窗口，所有的 Console.WriteLine / stdout / stderr 输出将失去可见的显示目标。在开发调试和实际运行时，这会使得日志输出、异常信息对用户完全不可见。

3. **消息循环冲突**：托盘图标需要一个 STA 线程和 Application.Run() 消息循环（本分支已尝试在独立线程中运行）。但 Console 程序的主线程通常没有消息泵，且 IHost.RunAsync() 与 WinForms 消息循环在生命周期管理上存在冲突。

4. **启动方式的根本差异**：
   - 托盘程序应该是 **WinExe**（没有控制台窗口）
   - ConsoleHost 是 **Exe**（有控制台窗口）
   - 要在同一个程序里同时支持两种模式，需要引入复杂的双模式启动逻辑（如先启动一个 stub 进程，再 detach 控制台），改造量远超预期。

### 改造量评估

为了让当前 ConsoleHost 真正支持托盘驻留，需要：
- 将项目 OutputType 改为 WinExe，或创建一个新的 WinUI/WinExe 外壳项目
- 重建日志输出系统，使其在 WinExe 模式下通过文件或 UI 面板展示
- 重写启动流程，分离 Console 模式与 Tray 模式的生命周期管理
- 处理 IHostedService（Worker、IPC Server 等）与 WinForms 消息循环的协调

这些改动触及了程序的**核心架构**，而不是一个可独立合并的功能模块。

## 结论与后续方向

本分支的托盘图标实现证明了：在现有 ConsoleHost 架构上"打补丁"式地添加托盘功能，会导致代码复杂度和维护成本急剧上升，且无法达到理想的用户体验（隐藏不彻底、输出丢失、生命周期管理混乱）。

如果未来确实需要托盘驻留功能，推荐方案是：
- **方案 A**：创建一个新的 GameHelper.TrayHost 或 GameHelper.WinUI 项目，以 WinExe 模式运行，将核心逻辑作为服务引用。
- **方案 B**：保持 ConsoleHost 不变，通过第三方工具（如 Traymond）将现有控制台窗口最小化到托盘。

因此，eat/in-house-tray 分支保留为技术探索记录，不合并到 main。