# 桌面手柄输入

输入仅属于 Desktop，不进入监控运行时。SdlControllerSource 在 WPF Dispatcher 上初始化、泵事件、枚举/关闭热插拔设备并释放 SDL 子系统；16ms DispatcherTimer 不引入后台共享状态。窗口失焦或最小化时停止 timer、逐个关闭设备并退出 SDL Gamepad；主窗口或所属 WPF 弹窗激活后重建输入源与导航状态，首帧重新 armed。清理步骤独立 best effort，失败也清空字段；不在游戏运行期间持有手柄。ControllerNavigation 通过 IControllerSource 与 TimeProvider 解耦，负责轴映射、单设备仲裁、边沿及重复节奏（当前设备空闲至少 500ms 才允许另一已连接设备的新按下接管，冷却期镜像边沿被消费，不延后重放；接管以候选设备上一帧为基线，不重放旧确认键）。MainWindow.Controller 将语义操作交给现有选中项、LaunchFromPoster 和弹窗，不创建另一套启动流程。

GameHelper.Input.Tests 链接纯逻辑源文件，net8.0 可在 Linux 运行；Windows 集成由现有 solution CI 检查。类型区分 Unknown/Xbox/PlayStation，供后续 glyph 使用。库加载失败时保留键鼠功能并显示状态。

桌面库与主机模式共用 `View/Back + Menu/Start`（PlayStation 为 `Share/Create + Options`）切换入口；SDL Back 映射为专用 ModeModifier，与 B/○ 的 Back 导航动作分开。两键可任意顺序按下，只有同一仲裁设备能形成组合；组合优先于其他动作，仅发一次 ToggleConsoleMode，两键全部松开后才允许下次触发。单独 Menu/Start 改为松开时触发原菜单，避免先按 Menu 再按 View 时误开菜单。窗口切换后仍使用原有 neutral arming，不重放手中尚未松开的组合。桌面自有模态弹窗中先用 B/○ 返回，不切换窗口。

组合键仅在 GameHelper 前台有效；不注册全局热键、不在后台轮询，也不能从游戏或其他软件直接唤起 GameHelper。失焦和最小化仍由 WindowControllerInput 停泵并关闭 SDL 设备，组合识别不改变原有释放边界。

SDL LeftTrigger/RightTrigger 轴映射为独立的 PreviousShelfPage/NextShelfPage，主机主页消费，桌面忽略；L1/R1 的 PreviousPage/NextPage 仍是页签。扳机按设备记录滞回状态（16000 按下、低于 8000 释放），断连清除，只发新边沿，不参与方向连发；仲裁与失焦 neutral arming 同时覆盖扳机。主机用实际 ScrollContentPresenter 宽度及卡片容器宽计算一屏数量，再滚动一屏并保证选中项可见，不把可能以条目计量的 ScrollViewer.ViewportWidth 当作像素。

Auxiliary（□ / X）在无覆盖层的主页/详情打开所选游戏设置，复用 RunInteractionAsync 忙碌保护与原设置草稿；覆盖层中的退格和填写想法不变。结束游戏经系统菜单或详情按钮进入原二次确认，不再绑定方块快捷键。

参考 Playnite GameController.cs（3e8abb241873e326e27c75b59dd0d2ea367698e3）的轮询→语义→WPF 桥接思路，当前代码独立编写，没有拷入其源码。第三方记录见根 THIRD-PARTY.md。

SDL 初始化前使用 `SetHintWithPriority(..., Override)` 禁用 enhanced reports，检查失败即拒绝初始化，通过现有窗口状态报告错误。Windows 测试验证继承环境变量为 1 时仍被覆盖为 0。

WindowControllerInput 统一窗口激活、SDL 持有与释放，MainWindow 与 ConsoleModeWindow 共用；静态前台 owner 在新窗口建立 SDL 前先释放旧 owner，所有调用在 WPF Dispatcher 上。主机窗口不把其他窗口视作所属模态，桌面库继续支持自身模态返回。ConsoleModePolicy 是可在 Linux 测试的页签、提示和恢复判定边界。

主机启动生命周期由 ConsoleLaunchState（TimeProvider）管理。IPC LaunchRequestId 随启动进入运行时待启动记录；LaunchSessionCorrelation（注入 TimeProvider）管理两分钟观察窗口。有启动器 PID 时严格匹配该 PID；无 PID 时，仅在请求前 DataKey 未运行、期限内首次出现新会话且并非 Preexisting 枚举进程时绑定观察到的 PID。关联随该运行组退出清理，不持久化到跨重启快照。窗口按请求 ID + PID 跟踪，等待、运行、超时与覆盖层展示分开；返回关闭覆盖层不释放等待状态。Steam URI／快捷方式在窗口内关联成功后与直接启动共用最小化、恢复及现有评分邀请路径；已运行或超时后的会话不绑定。按管家确认的折中，接受窗口内从其他入口启动同一 DataKey 的歧义；原启动、HDR 与计时行为不变。

运行时负责会话是否在两分钟内建立关联；界面先接受匹配本次请求的运行时关联，再对未关联请求判断本地超时，避免 IPC 延迟丢弃合法关联。主机窗口的 ConsoleRuntimeRefresh 每次定时回合均主动刷新库状态后再渲染，不依赖桌面是否停留在统计页或当前是否跟踪启动；慢请求不重叠，窗口关闭后不再渲染迟到响应。
