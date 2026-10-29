# 主机模式：现成组件、前端与数据源事实调查

调查日期：2026-10-29。只交事实与候选，不确定产品方案、原型、默认库或数据源优先级；没有修改产品代码。范围按用户最新纠正缩为四题。

## 证据与现场

开始执行 `git fetch origin`；工作区 `main` 无未提交文件（无关未提交文件：无），`origin/main` 是 HEAD 的祖先。共享 main 调查期间继续前进，因此研究在独立 worktree `/tmp/gamehelper-console-research` 的 `research/console-mode` 分支完成，文档代码基线为 `066bcbdf6f307213b1fe28ac95401839500f3fb0`。下文仓库路径均相对于该基线，行号可用 `git show 066bcbd:<路径>` 核对。未克隆外部仓库，使用 GitHub API、搜索与原文；磁盘实测余量约 5 GB。

## 1. 全屏前端：可拿来什么

以下“适配度”和“改动量”都是静态代码判断（推测），不是已编译验证。没有发现可直接把完整主机界面嵌入 WPF 的独立通用 NuGet 控件；现成产品不等于可引用的界面组件。

| 候选 | 交互与导航事实 | 引入方式、适配度及改动量（推测） | 许可证与维护证据 |
|---|---|---|---|
| Playnite Fullscreen | 全屏游戏库；SDL 控制器输入接入 WPF `InputManager`，提供 `InputGesture`、确认/取消绑定、摇杆方向到导航键、长按重复；输入不是仅依赖 Xbox XInput | 最接近当前技术栈，但 FullscreenApp 是 `net462` / x86 可执行程序，引用 Playnite 与 SDK，不是 .NET8 UI 库。可拷入并裁剪 `GameController.cs` 等输入桥接源码，替换 SDK 事件、日志、Win32 与 SDL 引用；完整 XAML/VM 搬迁改动大，输入桥接为中等改动 | MIT，保留版权与许可；GitHub 未归档，2026-10-19 有推送。固定源码快照 `3e8abb2`：[输入源码](https://github.com/JosefNemec/Playnite/blob/3e8abb241873e326e27c75b59dd0d2ea367698e3/source/Playnite/Input/GameController.cs)、[项目依赖](https://github.com/JosefNemec/Playnite/blob/3e8abb241873e326e27c75b59dd0d2ea367698e3/source/Playnite.FullscreenApp/Playnite.FullscreenApp.csproj)、[许可](https://github.com/JosefNemec/Playnite/blob/master/LICENSE.md)、[仓库实时状态](https://api.github.com/repos/JosefNemec/Playnite) |
| Pegasus | 游戏库/模拟器启动前端；QtQuick/QML 主题完全控制界面，输入支持 SDL2 或 Qt Gamepad；`GamepadAxisNavigation.cpp` 将轴区间转换成方向按钮 | 无 WPF NuGet；可独立运行，或借鉴主题/输入分层、轴死区与配置交互。C++/Qt 源码直接搬入 C# 需重写，改动大；不是拿 QML 当 WPF XAML | GPLv3，导航文件标 GPLv3-or-later，素材另有品牌限制；未归档，2026-10-26 有推送。[README](https://github.com/mmatyas/pegasus-frontend/blob/cf28a1b7cf20c975b171d0bf7fd68be8a3d70d21/README.md)、[轴导航](https://github.com/mmatyas/pegasus-frontend/blob/cf28a1b7cf20c975b171d0bf7fd68be8a3d70d21/src/backend/model/internal/GamepadAxisNavigation.cpp)、[仓库状态](https://api.github.com/repos/mmatyas/pegasus-frontend) |
| ES-DE | 按系统/游戏浏览并启动；SDL 控制器映射，`InputManager` 管理设备并读写 `es_input.xml` | C++/SDL 产品，无 WPF NuGet；可借鉴系统→游戏导航、映射配置、设备热插拔设计。移植整个界面改动大；独立进程启动不能自动共享 GameHelper 的库与启动生命周期 | 取证副本为 **RetroDECK 定制分支，不冒称官方上游**；其输入文件和根许可为 MIT，2026-10-25 有推送。官方开发入口在 GitLab。[官方项目](https://gitlab.com/es-de/emulationstation-de)、[副本说明](https://github.com/RetroDECK/ES-DE/blob/retrodeck-main/README.md)、[输入实现](https://github.com/RetroDECK/ES-DE/blob/retrodeck-main/es-core/src/InputManager.cpp)、[许可](https://github.com/RetroDECK/ES-DE/blob/retrodeck-main/LICENSE)、[映射说明](https://github.com/RetroDECK/ES-DE/blob/retrodeck-main/INSTALL.md) |

许可证兼容边界：GameHelper 根 [LICENSE](../../LICENSE) 声明 AGPL-3.0/商业双许可。MIT 组件可保留声明后复用；Pegasus 的 GPLv3 源码不能当作自有商业双许可代码。GPLv3 §13 允许与 AGPLv3 作品结合但保留各自条款，商业闭源发行并不因此获授权；主题、图片与依赖还需逐件核对。这是候选筛选的许可事实，不是选型决定。[GPLv3 原文 §13](https://www.gnu.org/licenses/gpl-3.0.html#section13)

## 2. Windows / .NET 8 手柄输入组件

这些库处理设备输入，不会自动给 WPF 网格、弹窗、滚动与焦点实现完整的手柄导航；适配度是静态判断，未执行 restore/Windows PoC。

| 候选 | 引入方式、许可、维护快照 | 覆盖与 .NET8 WPF 适配 |
|---|---|---|
| Vortice.XInput | NuGet `Vortice.XInput`，MIT；3.8.3 于 2026-03-04 更新。[包与框架](https://www.nuget.org/packages/Vortice.XInput/) | 包支持 net8；读取 Xbox/XInput，也可读 DS4Windows/Steam 暴露的虚拟 Xbox；不原生解析 Sony HID。XInput 最多四槽；Xbox USB/无线能否使用取决于 Windows 是否暴露为 XInput。[微软说明](https://learn.microsoft.com/en-us/windows/win32/xinput/getting-started-with-xinput) |
| SDL3-CS（edwardgushchin） | NuGet `SDL3-CS` + 原生 `SDL3-CS.Windows`，zlib；v3.4.18.1 于 2026-10-21 发布；README 列 net7/8/9/10 与 Windows x86/x64/arm64。[仓库](https://github.com/edwardgushchin/SDL3-CS)、[release](https://github.com/edwardgushchin/SDL3-CS/releases/tag/v3.4.18.1) | 可引用输入绑定，SDL 负责 Xbox/PS4/PS5 后端，WPF 事件泵、线程与 Dispatcher 桥接需补（推测）。不是只靠映射数据库就支持设备；USB/BT 能力不同。[SDL 官方绑定目录](https://wiki.libsdl.org/SDL3/LanguageBindings)、[PS4 后端](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps4.c)、[PS5 后端](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps5.c) |
| SDL2-CS（flibitijibibo） | 可拷绑定源码，zlib；仓库 archived，2024-06-28 最后推送。[README](https://github.com/flibitijibibo/SDL2-CS)、[仓库状态](https://api.github.com/repos/flibitijibibo/SDL2-CS)；同名 NuGet 2.0.0 停留 2016，不能把它当现代 SDL 支持保证。[旧包](https://www.nuget.org/packages/SDL2-CS) | C API P/Invoke 绑定，需配套 SDL2 DLL；PS5 支持取决于配套原生版本与绑定 API。可与 Playnite 的输入桥接思路结合，但 .NET8 打包/事件泵要验证（推测）。 |
| Windows.Gaming.Input | 系统 WinRT API，经 Windows TFM/SDK 使用，非第三方手柄包；Windows SDK 条款，不是 MIT。维护随 Windows SDK。[RawGameController](https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.rawgamecontroller?view=winrt-26100) | Gamepad/RawGameController 不是 Sony USB/BT 全覆盖承诺；RawGameController 提供未命名轴/按钮/开关，需要自行映射。WPF 可接 Windows API，仍需验证目标系统、设备与线程。[官方使用说明](https://learn.microsoft.com/en-us/windows/uwp/gaming/raw-game-controller) |
| HidSharp | NuGet `HidSharp` 2.6.4，2025-10-14 更新，netstandard2.0；仓库 2026-07-02 推送、未归档。[包](https://www.nuget.org/packages/HidSharp)、[仓库](https://github.com/IntergatedCircuits/HidSharp)。许可原文 Apache-2.0，自动识别 NOASSERTION 不等于无许可。[许可](https://raw.githubusercontent.com/IntergatedCircuits/HidSharp/main-2.6/LICENSE.txt) | net8 可消费；只是通用 HID 层，不统一三种手柄。Sony USB/BT 报告解析和语义需自行实现，改动量大；Xbox 不保证走通用 HID。 |
| DualSenseAPI | NuGet `DualSenseAPI`，MIT，.NET Standard；仓库已归档，2021-12-03 最后推送。[源码/说明](https://github.com/BadMagic100/DualSenseAPI) | DualSense 专用轮询/回调与触摸、陀螺仪等，不能覆盖 Xbox/DS4；USB/BT 现状未核实。可专项引用，不能据此承诺三类手柄。 |

补充排除事实：另一个 `bottlenoselabs/SDL3-cs` README 目标为 .NET9，不能与上述 net8 绑定混淆。[README](https://github.com/bottlenoselabs/SDL3-cs)

### USB/蓝牙与共存

- SDL3 的 `SDL_HINT_JOYSTICK_ENHANCED_REPORTS` 默认 1；官方说明蓝牙 PS 增强报告会影响不使用 SDL 的 Windows DirectInput 程序，启用后需手柄断电才能退出该模式。因此“只读取导航”仍可能改变其他应用可读的报告模式。[官方 hint](https://wiki.libsdl.org/SDL3/SDL_HINT_JOYSTICK_ENHANCED_REPORTS)
- DS4Windows 物理/虚拟设备同时可见会双输入；HidHide 按应用允许访问隐藏设备，USB/BT 需要分别处理。旧独占隐藏还可能因其他进程先打开设备而失败。直接 HID 读取者可能因隐藏看不到设备、虚拟 XInput 不能可靠推出原始物理型号（后两点为推测）。[DS4Windows 项目文档](https://kanuan.github.io/DS4WSite/guides/solving-double-input/)
- Steam Input 可把控制器模拟成传统游戏手柄，官方提供 XInput 槽与 Steam handle/glyph 的映射。并行读物理与虚拟路径可能产生重复导航（推测）；不是每次共存都会冲突。[Steamworks](https://partner.steamgames.com/doc/features/steam_controller/steam_input_gamepad_emulation_bestpractices)
- GameHelper 自身会按游戏配置运行/关闭 DS4Windows，故主机界面与游戏中的设备可见性可能随启动变化（推测）。当前行为见第4节。没有在本轮替管家决定物理/虚拟设备选择、隐藏策略或焦点规则。

## 3. 游戏是否支持手柄：数据能回答到哪一层

| 来源 | 字段、语义与覆盖 | 接入、限流、条款及匹配 |
|---|---|---|
| Steam Store appdetails | `controller_support` 实测可为 `full`，未出现字段不能直接判否。Steam 对 full/partial 的用户含义有说明：完整与部分支持涉及是否需键鼠完成交互，不保证原生 Sony glyph、触摸或自适应扳机。[Steam 帮助](https://help.steampowered.com/ms/faqs/view/3725-76D3-3F31-FB63) | 按 AppID 查 Steam 商店条目，非 Steam 本地版本需先匹配同款并核对版本。商店端点不是已查明有 SLA/固定配额的公开 Web API 契约；本轮没有取得其官方限流与再分发授权，不能套用 Steam Web API 数字。 |
| PCGamingWiki | Input 编辑契约区分 controller support/full controller/PlayStation 型号；full 还包括菜单、文本及外部 launcher 无需键鼠；wrapper 应区别 native 与 hackable。覆盖为社区逐游戏资料，没有实测覆盖百分比。[编辑指南](https://www.pcgamingwiki.com/wiki/PCGamingWiki%3AEditing_guide/Input_settings) | 官方 API 页说明 2026年8月改动：Cargo 需账号/Bot Password 登录，`Infobox_game` 改 `Game`；60 requests/min，429 封锁60秒，通用 User-Agent 可能403；可按 Steam/GOG ID 跳转或 `idlookup`。[API](https://www.pcgamingwiki.com/wiki/API)。本轮页面直连403，以上经搜索取得官方正文；未登录查询，未验证新 schema，不能宣称已联通。内容再利用许可正文也未取回，商业/批量再分发权限未确认。 |
| IGDB | 官方 schema 的 games/external_games 可用于标题与外部商店 ID 匹配；本轮未找到专门的 controller-support 字段，platform=Windows/PS5 不能推出 PC 手柄兼容性。覆盖跨平台元数据，不等于覆盖控制器能力。[官方 schema](https://api-docs.igdb.com/) | OAuth client_credentials；4 req/s、最多8并发；非商业免费，商业需合作沟通。非 Steam 可按名称、平台、external_games 匹配，但必须核对版本；不建议把 client secret 随桌面二进制公开（候选接入约束，推测）。[认证、限流与条款](https://api-docs.igdb.com/) |

### Steam 最小响应实测（2026-10-29）

用 Python3 `urllib.request` 对 `https://store.steampowered.com/api/appdetails?appids=<id>&l=english` 发单 app 请求，提取 `success/name/controller_support/categories`，未抓取整个库：

| AppID | success / name | controller_support | 描述含 controller 的 categories |
|---|---|---|---|
| 620 | true / Portal 2 | `full` | 28 Full controller support；55/56 DualShock Controller Support；57/58 DualSense Controller Support |
| 730 | true / Counter-Strike 2 | 字段缺失 | 无 |
| 570 | true / Dota 2 | 字段缺失 | 无 |

响应证据：[620](https://store.steampowered.com/api/appdetails?appids=620&l=english)、[730](https://store.steampowered.com/api/appdetails?appids=730&l=english)、[570](https://store.steampowered.com/api/appdetails?appids=570&l=english)。这是观察值，不是字段全集/稳定契约；本轮未拿到 `partial` 样本，55/56 与57/58描述相同也不足以推断 USB/BT 子类型。

能力缺失、明确不支持、部分支持、完整支持，以及“支持 Xbox 模拟输入”与“原生 DS4/DualSense 功能”是不同信息；如何映射产品标记仍由后续方案决定。数据源未命中不构成“不支持”的证据（推测）。本地可用标识见第4节。


## 4. 当前游戏库的事实与切入位置

这里只列当前边界，不指定在哪里实现新功能。

| 边界 | 当前行为与文件位置 |
|---|---|
| WPF 窗口 | 项目是 `net8.0-windows` / `UseWPF`：[GameHelper.Desktop.csproj](../../GameHelper.Desktop/GameHelper.Desktop.csproj)。主窗口绑定 `LibraryViewModel`、注册 `PreviewKeyDown`；海报双击触发启动：[MainWindow.xaml.cs](../../GameHelper.Desktop/MainWindow.xaml.cs)、[键盘与海报操作](../../GameHelper.Desktop/MainWindow.xaml.cs)。当前并无独立主机模式窗口。 |
| 桌面启动/激活 | `App.OnStartup` 的 `--background` 路径只确保 runtime 后退出；正常路径创建 `MainWindow`；单实例激活会还原最小化窗口：[App.xaml.cs](../../GameHelper.Desktop/App.xaml.cs)、[单实例](../../GameHelper.Desktop/App.xaml.cs)。 |
| 游戏启动 | `LibraryViewModel.LaunchCommand` 发 `GameLibraryOperation.Launch`，允许前台接管并在成功返回后最小化：[LibraryViewModel.cs](../../GameHelper.Desktop/LibraryViewModel.cs)、[LaunchWindowHandoff.cs](../../GameHelper.Desktop/LaunchWindowHandoff.cs)。Core `GameLibrary` 把请求交给 `IGameLaunchCoordinator`：[GameLibrary.cs](../../GameHelper.Core/Services/GameLibrary.cs)。HDR/待启动所有权约定见 [架构](../architecture/index.md) 与 [ADR-0002](../adr/0002-wpf-library-and-launch-ownership.md)。 |
| IPC/托盘 | WPF 使用 `GameLibraryPipe`，服务端在 ConsoleHost；托盘 `TrayMenuService` 提供打开库、启动最近游戏等动作：[App.xaml.cs](../../GameHelper.Desktop/App.xaml.cs)、[GameLibraryServer.cs](../../GameHelper.ConsoleHost/Services/GameLibraryServer.cs)、[TrayMenuService.cs](../../GameHelper.Core/Services/TrayMenuService.cs)。 |
| 配置 | `AppConfig` 管全局配置、`GameConfig` 管单游戏；当前无主机模式开关或游戏手柄兼容性字段：[AppConfig.cs](../../GameHelper.Core/Models/AppConfig.cs)、[GameConfig.cs](../../GameHelper.Core/Models/GameConfig.cs)。桌面侧数据与运行时配置有独立边界：[DesktopSideData.cs](../../GameHelper.Desktop/DesktopSideData.cs)。 |
| DS4Windows 预设 | 界面的“手柄支持”实为 `UseDs4Windows`，不是检索出的游戏能力：[MainWindow.xaml](../../GameHelper.Desktop/MainWindow.xaml)、[预设编辑](../../GameHelper.Desktop/MainWindow.xaml)。路径存 `GameLaunchOptions.Ds4WindowsPath`；启动器有路径则确保运行，无路径则关闭 DS4Windows：[GameLaunchOptions.cs](../../GameHelper.Core/Models/GameLaunchOptions.cs)、[WindowsGameLauncher.cs](../../GameHelper.Infrastructure/Processes/WindowsGameLauncher.cs)。通用动作编辑在 [GameActionEditor.cs](../../GameHelper.Desktop/GameActionEditor.cs)。关闭第二款游戏的 DS4Windows 影响第一款的现状，已由用户接受，不能误写为本轮待修问题：[桌面工作簿](../steward/desktop-and-capabilities.md)。 |

### 游戏身份：现在已有的字段

- 持久化游戏有 `DataKey`、显示名、`Executable`（完整路径或 exe 名），另有 `Launch.Target/Arguments/WorkingDirectory`；没有独立 Steam AppID、IGDB ID 或 PCGamingWiki page ID 字段。`LibraryGame` 在配置上叠加运行状态、安装状态、时长；`DataKey` 是本地业务主键，不能直接当外部数据库 ID。[GameConfig.cs](../../GameHelper.Core/Models/GameConfig.cs)、[GameLaunchOptions.cs](../../GameHelper.Core/Models/GameLaunchOptions.cs)、[LibraryGame.cs](../../GameHelper.Core/Models/LibraryGame.cs)
- Steam 扫描时能取到 `SteamInstalledGame.AppId`，导入后启动目标存为 `steam://rungameid/{AppId}`，故该部分条目可反解 AppID。[SteamInstalledGame.cs](../../GameHelper.Core/Models/SteamInstalledGame.cs)、[GameLibrary.cs](../../GameHelper.Core/Services/GameLibrary.cs)
- 海报模块已有独立的 `DataKey → AppId` 关联，可人工指定/名称匹配；解析器还读 Steam URL、`.url`、安装清单和 `steam_appid.txt`。这提供匹配线索，海报对应的版本未必就是本地 exe 的发行版本（推测），不能自动当作能力已验证。[SteamPosterLibrary.cs](../../GameHelper.Desktop/SteamPosterLibrary.cs)、[关联](../../GameHelper.Desktop/SteamPosterLibrary.cs)、[SteamArtworkIdentity.cs](../../GameHelper.Infrastructure/Artwork/SteamArtworkIdentity.cs)
- 非 Steam 游戏可用显示名检索再核对发行年份、平台、版本与商店外部 ID（候选匹配方式，推测）；exe 名可能复用或不含标题，当前记录也没有保证具备年份/发行商。匹配失败和字段缺失均不足以断言“不支持”。[当前身份字段](../../GameHelper.Core/Models/GameConfig.cs)

## 我自行引入的决定

仅作研究执行选择：独立 worktree 保持共享主工作区为 main；不下载大仓库；按组件可引用性把“输入库”与“完整前端”分开列；适配/改动量只作静态推测。没有选择产品方案、默认输入库、字段设计、源优先级或“不支持”标记规则。

## 验证范围与未决项

本轮仅两份 Markdown，不改运行时，不执行构建、Windows 手柄实测、部署或重启。已核对引用源码和上游文档；组件兼容性仍需 Windows USB/蓝牙实测，不能把静态 API 覆盖当设备验收。

- Xbox 各代、DS4/DualSense 原装与第三方、USB/蓝牙、断线重连、Steam Input/DS4Windows/HidHide 的组合，没有实机矩阵结果。
- 前端源码拆分的实际文件数、工时、打包体积，以及选定 NuGet 与本项目发布方式的二进制兼容性，未做试编译。
- 没有对整个本地游戏库做逐条数据匹配，覆盖率不能给百分比；商店缺字段不等于游戏不支持，Steam 的“完整”也不等于原生 PS 按键/自适应扳机支持。
- 数据 API 的当前授权、商业使用和部署密钥方式需按未来实际使用主体确认；本次没有注册服务或引入运行时网络依赖。
