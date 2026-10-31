# 公共文件与桌面基础件

- `Core/Utilities/AtomicFile` 负责同目录临时文件写入、发布和清理。调用方仍负责创建目录、编码、序列化、异常捕获及状态更新。同步与异步写入共用相同契约；保留原有固定临时路径、禁止覆盖以及清理异常策略的参数，避免重构改变调用方行为。
- `Infrastructure/Notifications/NativeMessageWindow` 负责窗口类注册、创建、消息循环与逐项尽力清理，在所属线程创建和销毁。窗口保持隐藏顶层窗口，托盘才能收到 Explorer 的 `TaskbarCreated` 广播；不改为 `HWND_MESSAGE`。托盘和提醒各自保留线程调度、通知图标、定时器及业务消息处理。
- `Desktop/DesktopDocumentStore` 只处理 `.desktop-*.json` 侧文档，不处理运行时配置。常规读取失败回退空文档，草稿保留严格读取，由现有调用方显示“上次未保存的修改未能读取”提示。JSON 格式、路径、写入去重和内存更新顺序保持原契约。
- `Desktop/MainWindowSnapshot` 负责原有快照、几何记录及交互探针，MainWindow 只接线已有界面操作。快照文件名、主题切换和恢复、等待及校验保留。
- `Core/Services/LibraryHistorySummary` 由每个 `GameLibrary` 实例独占，把历史快照一次聚合为按 DataKey 的总分钟与最后游玩时间。SQLite 每次列表查询仍读取廉价历史身份，只有同一 database_id/history_revision 才复用摘要；缓存使用实际快照事务返回的身份，数据库恢复、完成新会话、时钟回拨或未来会话到达可见时间时重算。legacy 的 null 身份每轮读取，不用文件 mtime 猜测版本。读取失败失效缓存，并向列表返回明确错误，不能把旧摘要或空历史冒充成功。并发列表查询在模块内串行化缓存刷新；配置、进程、启动身份和安装状态仍每轮读取。缓存不保留完整历史，也不跨 `GameLibrary` 实例共享。

`YamlConfigProvider` 的配置原子写入本轮保留，避免与配置兼容修复交叉；`BackgroundConsoleOutput` 的 `.previous` 文件轮换不是临时文件发布。

## 2026-10-27 Linux 重构验证

每项提交均执行 `~/.dotnet/dotnet build GameHelper.sln -p:EnableWindowsTargeting=true`，编译无错误。
临时非 WPF xUnit 工程引用 Core、Infrastructure、ConsoleHost，链接本轮涉及的持久化、Steam 素材、变速控制、托盘、通知与 Sync 测试，以及可独立编译的 Desktop 侧存储。

基线 133 例：129 通过、2 失败、2 跳过。新增 AtomicFile 6 例、DesktopDocumentStore 6 例后共 145 例：141 通过、2 失败、2 跳过；失败集合保持不变。两项失败均为 ProcessGitRunnerTests 在 Linux 启动 Windows 命令解释器失败；两项跳过是需要交互式 Windows 桌面的通知集成测试。临时工程在交付前删除。

Linux 可以交叉编译 Windows 目标，但无法运行 WPF 快照和 Win32 窗口、完成 Windows 真机验收和本机部署；这些仍需在 Windows 复验。

完整 solution 测试命令 `~/.dotnet/dotnet test GameHelper.sln -p:EnableWindowsTargeting=true --no-build` 因 Linux 缺少 Microsoft.WindowsDesktop.App 而中止。项目 autoreview helper 使用既定 Codex 引擎尝试审查全部改动，但本机凭证刷新失败（401），未取得独立审查结论。
