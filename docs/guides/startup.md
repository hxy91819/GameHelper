# WPF 与后台启动

## 日常入口

开机 Run 项使用正式发布目录 `desktop\GameHelper.Desktop.exe --background`。此模式不创建 WPF 窗口，连接现有运行时或以 `CreateNoWindow` 启动 `ConsoleHost monitor --background`，确认 IPC 就绪后退出引导进程。后台输出写到配置旁 `config.yml.runtime.log`，超过 5 MiB 时在下次启动轮转。

这是用户在 WPF 成为主界面后重新确认的决定，见 [ADR-0002](../adr/0002-wpf-library-and-launch-ownership.md)；旧 Console 交互界面的移除见 [ADR-0003](../adr/0003-remove-console-interactive-ui.md)。原 [ADR-0001](../adr/0001-normal-interactive-startup.md) 已被替代。

主动双击游戏库才打开并激活 WPF。关闭游戏库继续监控；重复打开复用同一配置的窗口。部署更新只恢复原本打开的窗口，使用 `--restore` 不激活。旧 Console 和 Launcher 桌面入口迁到游戏库。

## CLI 与直接启动

已有终端里的显式 CLI 命令继续使用 `GameHelper.ConsoleHost.exe` 或 `dotnet run --project GameHelper.ConsoleHost -- ...`。

不带参数启动 `GameHelper.ConsoleHost` 会打开 WPF 游戏库。显式传入 `--config <path>` 时，路径会透传给游戏库；不传时游戏库使用默认配置路径。后台 `--background` 启动不触发游戏库窗口。

`startup.launchOnStartup` 的 Run 注册值指向 WPF `--background` 入口。沙盒和一次性 CLI 不修改 Run 注册值。

## 验证

部署和后台发布回归共用 `scripts/start-checked-instance.ps1`，验证实际后台入口、运行时 PID 与目标路径、无可见控制台、IPC 和 ETW/CSV。单元测试和 CLI 冒烟默认运行。部署健康验收保留权限、数据目录和监控就绪检查。旧版本回滚通过兼容的隐藏 `monitor` 命令恢复监控。

## 托盘快捷操作

非沙盒后台实例在通知区驻留。

托盘使用专用的淡紫底白色 G / 播放标记，图标内嵌在 Infrastructure 程序集中，包含 16、20、24、32、48、64 像素六档，不依赖外部文件位置。按任务栏实际 DPI 获取物理小图标尺寸，优先使用不小于目标的素材，避免被后台进程的 DPI 虚拟化限制为 16×16 后再放大；例如 200% 缩放使用 32×32。随托盘定时刷新检查尺寸，缩放或任务栏重建后重新加载所需图像；尺寸不变时复用原生图标。停止后台时移除并释放原生图标。Windows 仍可能按用户通知区偏好把图标折叠；GameHelper 不强改系统显示设置。

双击打开游戏库，右键每次读取最新菜单：

- 有活跃游戏时逐行显示“名称 · 本局 N 分钟”，含外部启动的游戏；只读，未知起点明确显示时长未知，没有游戏时隐藏。
- 显示主显示器 HDR 的当前状态，点击直接切换该显示器，不改变自动化的显示器规则，也不登记为工具的 HDR 回滚目标。待启动、运行中或恢复的工具 HDR 租约存在时禁用并注明原因；执行时再次在同一租约锁内检查。系统原本已开启 HDR 不算工具租约。主显示器无法唯一识别、状态未知或不支持 HDR 时禁用。
- 休息提醒启用时提供“暂停 30 分钟 / 暂停到明天 / 恢复”；明天指本地次日零点。暂停期间仍计前台游戏时长，仅抑制通知；到期或恢复后按累计时长继续提醒。显示暂停到期时间，未暂停时“恢复”禁用。不写配置，禁用提醒、停止监控或重启会清除暂停。
- 最近游戏按历史最近游玩时间降序取 5 款（同时间按 `dataKey` 排序，未游玩条目不列出）。点击通过与游戏库 IPC 相同的 `IGameLibrary` 启动命令执行，包括已有 HDR、DS4Windows 等前置流程；运行中（含外部启动）和启动中的游戏禁用。

菜单读取和动作在工作线程执行，消息线程只负责原生菜单与图标；操作失败写日志并显示托盘气泡。异步读取完成后才呈现菜单，避免磁盘或显示器查询阻塞消息循环。退出仍关闭后台监控。

2026-10-30 本机“似乎没有图标”排查：实际图标已注册且位于主任务栏内，未复现注册失败或折叠；旧入口使用通用系统应用图标，本轮改为清晰品牌标记。隔离原生验收覆盖注册、主动移除测试图标后的缺失检测、仅向测试窗口发送 `TaskbarCreated` 后恢复、停止清理，不重启 Explorer 或干扰用户实例。

同日清晰度反馈复现：在与后台一致的 DPI unaware 上下文，任务栏 DPI 为 192，原 `GetSystemMetrics` 返回 16，实际 HICON 也是 16×16，低于所需 32×32；资源已经包含 32 像素图，问题在加载尺寸。改用目标任务栏 DPI 的显式尺寸查询，保留后台及其他窗口的 DPI 行为；依据见 [GetDpiForWindow](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getdpiforwindow) 与 [Windows 图标尺寸](https://learn.microsoft.com/windows/win32/menurc/about-icons)。
