# 桌面手柄输入

输入仅属于 Desktop，不进入监控运行时。SdlControllerSource 在 WPF Dispatcher 上初始化、泵事件、枚举/关闭热插拔设备并释放 SDL 子系统；16ms DispatcherTimer 不引入后台共享状态。窗口失焦或最小化时停止 timer、逐个关闭设备并退出 SDL Gamepad；主窗口或所属 WPF 弹窗激活后重建输入源与导航状态，首帧重新 armed。清理步骤独立 best effort，失败也清空字段；不在游戏运行期间持有手柄。ControllerNavigation 通过 IControllerSource 与 TimeProvider 解耦，负责轴映射、单设备仲裁、边沿及重复节奏（当前设备空闲至少 500ms 才允许另一已连接设备的新按下接管，冷却期镜像边沿被消费，不延后重放；接管以候选设备上一帧为基线，不重放旧确认键）。MainWindow.Controller 将语义操作交给现有选中项、LaunchFromPoster 和弹窗，不创建另一套启动流程。

GameHelper.Input.Tests 链接纯逻辑源文件，net8.0 可在 Linux 运行；Windows 集成由现有 solution CI 检查。类型区分 Unknown/Xbox/PlayStation，供后续 glyph 使用。库加载失败时保留键鼠功能并显示状态。

参考 Playnite GameController.cs（3e8abb241873e326e27c75b59dd0d2ea367698e3）的轮询→语义→WPF 桥接思路，当前代码独立编写，没有拷入其源码。第三方记录见根 THIRD-PARTY.md。

SDL 初始化前使用 `SetHintWithPriority(..., Override)` 禁用 enhanced reports，检查失败即拒绝初始化，通过现有窗口状态报告错误。Windows 测试验证继承环境变量为 1 时仍被覆盖为 0。

WindowControllerInput 统一窗口激活、SDL 持有与释放，MainWindow 与 ConsoleModeWindow 共用；静态前台 owner 在新窗口建立 SDL 前先释放旧 owner，所有调用在 WPF Dispatcher 上。主机窗口不把其他窗口视作所属模态，桌面库继续支持自身模态返回。ConsoleModePolicy 是可在 Linux 测试的页签、提示和恢复判定边界。

主机启动生命周期由 ConsoleLaunchState（TimeProvider）管理。IPC LaunchRequestId 随启动进入运行时待启动记录；仅启动器实际返回的 PID 在有效等待期限内被监控观察到才获得该请求关联，关联随该运行组退出清理，不持久化到跨重启快照。窗口按请求 ID + PID 跟踪，等待、运行、超时与覆盖层展示分开；返回关闭覆盖层不释放等待状态。Steam URI、快捷方式及派生启动器无法证明目标 PID 时不自动最小化或退出后抢前台，等待超时后保留手动操作；原启动、HDR 与计时行为不变。
