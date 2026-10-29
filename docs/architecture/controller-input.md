# 桌面手柄输入

输入仅属于 Desktop，不进入监控运行时。SdlControllerSource 在 WPF Dispatcher 上初始化、泵事件、枚举/关闭热插拔设备并释放 SDL 子系统；16ms DispatcherTimer 不引入后台共享状态。ControllerNavigation 通过 IControllerSource 与 TimeProvider 解耦，负责轴映射、单设备仲裁、边沿及重复节奏。MainWindow.Controller 将语义操作交给现有选中项、LaunchFromPoster 和弹窗，不创建另一套启动流程。

GameHelper.Input.Tests 链接纯逻辑源文件，net8.0 可在 Linux 运行；Windows 集成由现有 solution CI 检查。类型区分 Unknown/Xbox/PlayStation，供后续 glyph 使用。库加载失败时保留键鼠功能并显示状态。

参考 Playnite GameController.cs（3e8abb241873e326e27c75b59dd0d2ea367698e3）的轮询→语义→WPF 桥接思路，当前代码独立编写，没有拷入其源码。第三方记录见根 THIRD-PARTY.md。
