# 启动窗口宿主：始终由 Windows Terminal 托管

## 现象

从开机自启、桌面快捷方式或脚本拉起 GameHelper 时，窗口可能落到经典控制台 conhost：字体、窗口边框、制表符渲染都和 Windows Terminal 不同，看起来像"换了一个程序"。从 Windows Terminal 里手动启动时一切正常。

这不是自启动指向了别的可执行文件，而是**控制台宿主**不同：同一个 exe 可以被 conhost、OpenConsole 或 Windows Terminal 托管，外观随之改变。

## 机制

启动最早期（解析完参数、占用单实例互斥体之前），程序会检查自己所在的窗口：

1. 取当前控制台窗口，再取它的根所有者窗口（`GetAncestor(hwnd, GA_ROOTOWNER)`）。Windows Terminal 持有 ConPTY 的隐藏兼容窗口，真正的显示窗口是它的根所有者。
2. 根所有者进程是 `WindowsTerminal` → 已经在 Windows Terminal 里，保持不动。
3. 否则用 `wt.exe` 重新拉起自己：

   ```
   wt.exe -w -1 -d "<程序目录>" -- "<自身路径>" <原始参数...> --in-windows-terminal
   ```

   拉起后最多等 6 秒确认新实例真的出现：确认到就交给它并让当前进程退出；没确认到就留在当前控制台并打印提示，不会出现"窗口交出去却什么都没起来"的情况。
4. `--in-windows-terminal` 是内部标记（`ArgumentParser` 会把它从有效参数里剥离），保证同一条启动链最多转移一次，不会反复开窗。

## 不转移的情形

任一条件成立就留在当前宿主，启动流程照常进行：

| 条件 | 原因 |
| --- | --- |
| 非 Windows | 不适用 |
| 文件拖放启动 | 只弹结果提示后退出，没必要换宿主 |
| `GAMEHELPER_DATA_DIR` 或 `GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE` 已设置 | 沙盒/测试实例不改变用户环境 |
| `GAMEHELPER_DISABLE_TERMINAL_RELAUNCH=1` | 用户显式关闭 |
| 参数里已有 `--in-windows-terminal` | 已经转移过 |
| 附加了调试器 | 调试时不换窗口 |
| stdin 或 stdout 被重定向 | 换成终端会丢掉管道输出 |
| 已在 Windows Terminal 内 | 无需转移 |
| 找不到 `wt.exe`（`%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe`） | 无法转移 |
| 已有实例在运行 | 交给单实例逻辑处理，避免多开窗口 |

框架依赖方式（`dotnet run`）下 `Environment.ProcessPath` 是 `dotnet.exe`，此时也不会转移。

## 与"默认终端应用"设置的关系

两层保证：

- **系统设置**：`HKCU\Console\%%Startup` 的 `DelegationConsole`/`DelegationTerminal` 指向 Windows Terminal 时，所有控制台程序都由 WT 托管。
- **进程内自转移**：系统设置被重置、或某个入口拿不到委托（例如提权后 WT 无法托管非提权窗口）时，程序仍会自己搬进 Windows Terminal。

提权启动时 `wt.exe` 以相同权限被拉起，因此看到的仍是 Windows Terminal 窗口（可能是独立的 WT 实例）。

## 关闭与排障

- 关闭自动转移：`setx GAMEHELPER_DISABLE_TERMINAL_RELAUNCH 1`（新开的终端生效）。
- 确认当前宿主：宿主进程名是 `conhost.exe` 说明没转移成功；是 `WindowsTerminal.exe`（窗口进程）或 `OpenConsole.exe` 说明已由 WT 托管。
- 转移失败时控制台会打印 `Failed to relaunch inside Windows Terminal: <原因>`，常见原因是 `wt.exe` 缺失或参数引用异常；失败不会阻止启动。

## 后台启动

自启动就是把发布目录的可执行文件写进 Run 键，和手动双击走完全同一条路径：启动时若不在 Windows Terminal 里，就用 `wt.exe` 重新拉起自己，原进程退出。

- 曾试过"`start /min` 零焦点 + 还原窗口时再延迟接管"的组合，它要维护窗口状态监视、单实例让渡和一段启动稳定期，实际体验只换来任务栏里一个图标，复杂度远高于收益，已移除。
- 代价：自启动时窗口会正常出现在前台（和有窗口的普通程序一样），不再做最小化。
- 自动切换宿主仍受以下条件约束：沙盒实例、被重定向的 stdio、调试器、已有实例、`wt.exe` 缺失时都留在当前宿主。

## 相关测试

- `GameHelper.Tests/WindowsTerminalHostTests.cs`：覆盖全部"不转移"分支、命令行引用规则，以及内部标记从有效参数里被剥离。
- `scripts/verify-on-machine.ps1`：`Start-CheckedInstance` 在首次拉起的 PID 退出时按"同路径 + 启动时间"接管转移后的新实例，再执行窗口可见性探测。
