# GameHelper

GameHelper 是一个面向 Windows 玩家的游戏启动器与桌面助手。WPF 游戏库提供浏览、启动、设置编辑和 Steam 扫描导入；ConsoleHost 统一负责进程监控、游戏时长与 HDR / 倍速自动化，并保留 CLI。当前界面与架构以本次 WPF 设计为准，旧 WinUI 方案已废弃。

`README.md` 的职责是：
- 指导用户如何安装、运行和使用项目。
- 给开发者提供最短路径的本地启动与验证步骤。
- 指向更详细的设计和规范文档，而不是重复它们。

详细设计与规范见 `docs/index.md`。

每次功能或修复交付都会更新本机发布目录并在后台重启监控，检查运行权限、ETW 和游戏库通信。WPF 是日常入口，Console 交互界面保留作手动诊断。部署步骤见[验证与部署工作流](docs/guides/verify-and-deploy-workflow.md)。

游戏库按 A / Studio 原型还原顶栏、横版 hero、海报网格与紧凑详情，使用原始 SVG 生成一致的 G / 播放标记，本周时长与趋势图自适应分栏，导航采用统一圆角线条图标，海报下仅展示时长，打开或返回游戏库、切换类型时默认隐藏本地未找到的游戏；保留明暗主题、实时计时及原有启动设置。侧栏支持 Steam 游戏类型多重归属、未分类和手动修正（自动更新不覆盖）；完整启动配置、海报及类型管理进入二级面板；游玩档案提供总览、趋势、六列排行、日历与游戏／日期游玩节奏汇总，不再显示逐次流水。详见[游戏库](docs/guides/game-library.md)与[游玩档案](docs/guides/playtime-dashboard.md)。

## 快速开始

### 环境要求
- 项目仅支持 Windows，开发、测试和 CI 均以 Windows 为准；不提供 Linux/macOS 兼容性支持。
- Windows 10 (19041+) / Windows 11
- .NET 8 SDK（当前仓库通过 `global.json` 锁定 `8.0.417`，允许 `latestPatch` 滚动）
- 使用 Cheat Engine 变速时需本机安装 Cheat Engine（仅离线单机游戏）

### 启动窗口与焦点

开机自启通过 `desktop/GameHelper.Desktop.exe --background` 无窗口启动监控，不创建控制台。主动打开游戏库才正常显示 WPF；部署只在原本打开游戏库时恢复窗口，并使用不激活方式。旧 Console / Launcher 桌面入口迁到游戏库；手动 CLI 仍可使用。详见[启动窗口宿主](docs/guides/terminal-host.md)。

### WPF 游戏库

界面采用石墨与薄荷绿的典藏风格，统一海报、统计图表和极简 Logo；详情栏底部固定启动与保存操作，筛选和网格 / 列表提供明确的选中反馈。侧栏底部可切换浅色 / 深色主题，立即生效并记住选择；浅色采用暖白与深绿，游戏库、游玩档案和弹窗同步适配。

每款游戏可开启“启动前运行 DS4Windows（PS 手柄）”：先启动指定的手柄兼容程序，已运行则复用，再启动游戏。未勾选时会关闭已运行的 DS4Windows；勾选后可自选路径，支持 Steam 入口，游戏退出后保持 DS4Windows 运行。外部启动的受监控游戏也应用该开关。详见[游戏库说明](docs/guides/game-library.md#ps-手柄兼容启动前运行-ds4windows)。

部署后打开桌面的 **GameHelper 游戏库**，或运行发布目录下的 `desktop/GameHelper.Desktop.exe`。支持网格 / 列表、搜索、历史时长、设置保存、添加程序和选择导入 Steam 游戏。启动前确认 HDR，再通过配置的程序、快捷方式或 Steam 入口启动；退出后恢复本工具修改的 HDR。HDR 切换仅支持库内启动；外部启动的游戏（库中显示“运行中·外部”）照常计时、应用倍速并启动或复用配套软件，HDR 勾选降级为显示器游戏模式（遵循预设开关与日照规则），不支持运行中补切 HDR。运行中的游戏卡片（含外部启动）优先排列，并逐秒显示本局已玩时长；系统睡眠 / 休眠期间计时自动暂停，不计入游玩时间。启动失败、两分钟内未识别到进程都会清理，计时只从真实进程开始。未保存的界面草稿单独保留，启动时先保存；关闭游戏库继续监控。详见[游戏库使用说明](docs/guides/game-library.md)。

游戏库会优先读取本地海报，缺少时按明确的 Steam App ID 自动下载官方竖版海报并缓存。Steam 启动入口、本地安装清单和 `steam_appid.txt` 可提供身份；其他游戏可通过“匹配 / 重试 Steam 海报”搜索名称并确认，也可粘贴商店链接或 App ID。关联只影响海报，不改变启动与自动化设置；离线保留已有缓存，失败回退图标。

侧栏 **游玩档案** 提供 7 / 30 / 90 天及全部历史统计：总时长、会话数、活跃日期、游戏数、日 / 月趋势和游戏排行。顶部年度游玩日历独立于时间筛选，始终展示最近一年；点击日期查看当日汇总，排行可按游戏筛选汇总。游戏库使用 2:3 竖版海报，海报与档案区域的纵向滚轮统一滚动整页。统计按会话结束日归档，使用已落盘的分钟数，包含已移出游戏库的历史，不计正在游玩的时间。详见[统计页面说明](docs/guides/playtime-dashboard.md)。

**扫描目录** 可识别候选程序，勾选后批量添加，新增游戏默认关闭 HDR 和倍速；默认游戏库只显示本机已安装，未找到和安装位置待确认的条目单独分组，历史始终保留。各分组内正在运行、最近游玩或最近添加的游戏优先；后台刷新保留菜单和列表状态。**按名称补全海报** 支持中英文名称定向搜索、预览选择和高置信度最佳匹配自动关联（排除 DLC / 画集等），也可关闭自动采用。详见[游戏库使用说明](docs/guides/game-library.md)。新版应用和快捷方式使用统一的 GameHelper [极简 G 轨道图标](docs/design/branding.md)。

开发时可运行 `dotnet run --project GameHelper.Desktop` 连接正在运行的 ConsoleHost。发布版本找不到运行实例时，会在后台启动同一发布目录的监控运行时。旧体验原型保存在 `codex/wpf-library-prototype` 分支。

### 常用 CLI 命令

```powershell
# 交互模式（默认）
dotnet run --project .\GameHelper.ConsoleHost --

# 启动监控
dotnet run --project .\GameHelper.ConsoleHost -- monitor [--monitor-type ETW|WMI] [--debug]

# 查看统计
dotnet run --project .\GameHelper.ConsoleHost -- stats [--game <name>]

# 配置游戏
dotnet run --project .\GameHelper.ConsoleHost -- config list
dotnet run --project .\GameHelper.ConsoleHost -- config add <exe|path-to-exe>
dotnet run --project .\GameHelper.ConsoleHost -- config import-steam
dotnet run --project .\GameHelper.ConsoleHost -- config remove <dataKey>

# 历史数据迁移
dotnet run --project .\GameHelper.ConsoleHost -- migrate

# 统计推送到 GitHub 仓库（自动/手动）
dotnet run --project .\GameHelper.ConsoleHost -- sync now [--force]
dotnet run --project .\GameHelper.ConsoleHost -- sync test
dotnet run --project .\GameHelper.ConsoleHost -- sync status
```

`migrate` 会按当前 Core 匹配规则把旧 `playtime.csv` 中的游戏名映射到 `dataKey`；歧义匹配不会自动改写。

`config list` 会同时显示 `dataKey` 和 `displayName`，方便核对配置显示名。

`config import-steam` 会扫描本机 Steam 的所有游戏库，并批量添加已安装且可定位到主可执行文件的游戏。

更多 CLI 说明见 `docs/guides/cli.md`。

ETW 会话意外中断时会自动尝试恢复，并核对已观察到的游戏进程，补结算中断期间退出的游戏，避免影响下一局计时。准确退出时间无法追回时按核对时刻结算并记录警告；连续恢复失败会明确报错。详见 [ETW 恢复行为与限制](docs/guides/etw-recovery.md)。

### 统计推送到 GitHub（已支持）

把游戏时长统计自动推送到你指定的 GitHub 仓库（公开展示或私有备份）：

- 推送内容为聚合报告（Markdown）+ 按日×按游戏聚合 CSV，只写入仓库的 `game-stats/` 子目录，不含精确作息时间；原始明细可选附带。
- 两种上传方式：`method: git`（默认，复用本机 git 凭据，零 token）或 `method: api`（GitHub token）。
- 自动推送默认每天最多一次，会话结束路径零新增磁盘写入；也可用 `sync now` 手动触发。
- 配置方法、token 创建与隐私说明见 `docs/guides/sync.md`。

### 运行中拖拽添加（已支持）
- 支持拖拽 `.exe` / `.lnk` / `.url`。
- 当 CLI 主进程已在运行时，新的拖拽启动请求会自动转发给主进程处理。
- 转发请求始终写入运行中主进程当前使用的配置；新启动进程不会通过 IPC 切换主进程的 `--config`。
- 配置会立即热重载，对后续新启动的进程生效。
- 不会破坏“单实例”约束（主进程始终只有一个）。

## 发布

### 发布 CLI

```powershell
# 自包含（目标机器无需预装 .NET Runtime）
dotnet publish .\GameHelper.ConsoleHost\GameHelper.ConsoleHost.csproj -c Release -r win-x64 --self-contained true

# 非自包含（目标机器需安装 .NET 8 Runtime）
dotnet publish .\GameHelper.ConsoleHost\GameHelper.ConsoleHost.csproj -c Release -r win-x64 --self-contained false
```

默认输出目录：
`GameHelper.ConsoleHost\bin\Release\net8.0-windows\win-x64\publish`

发布后 smoke 验证：

```powershell
.\scripts\publish-console-smoke.ps1
```

该脚本会发布 ConsoleHost，并用临时 YAML 配置运行发布目录中的 `validate-config` 和 `config list`，用于提前发现发布产物缺文件、嵌入资源异常或配置解析失败。

## 配置文件

### 配置热重载

在监控运行期间直接编辑 `%AppData%\GameHelper\config.yml`（文本编辑器保存即可），约 0.5 秒内自动生效，无需重启 GameHelper：

- 新增 / 修改 / 删除游戏条目、`enabled`、`hdr`、`speed`、`speedAttach`、`breakReminder`、`monitorMode` 规则都会自动重载；游戏匹配与变速对之后新启动的进程生效，显示器规则立即重新评估。
- 拖拽添加与 CLI 目录命令修改配置依旧立即重载，与文件监听路径互不冲突。
- 以下项仍需重启进程才会生效：`monitor`（监控器类型，启动时创建）、`sync` 后台推送参数（循环启动时读取）。
- 编辑器保存时文件会短暂处于“被替换/缺失”状态，监听器会跳过该窗口，不会把空配置读进运行中的服务；删除 `config.yml` 也不会清空当前监控状态。
- `validate-config`、`stats` 等一次性命令不会修改系统开机启动项；使用 `GAMEHELPER_DATA_DIR` 的隔离实例也不会修改该启动项。
- 监听失败时只降级热重载（日志有提示），监控本身不受影响。

### 连续游戏休息提醒

在 `config.yml` 添加 `breakReminder: { enabled: true }`，启动监控后累计前台游戏40分钟提醒休息，之后每10分钟再提醒；跨游戏累计，连续离开游戏满5分钟清零。默认关闭，使用系统通知及提示音，时间可配置。详情见[休息提醒指南](docs/guides/break-reminder.md)。

### 显示器预设自动切换（游戏/护眼）

在 `config.yml` 添加 `monitorMode: { enabled: true }` 后，GameHelper 通过 DDC/CI 直接切换显示器预设档位：白天打游戏切游戏模式（如 Dell Game），白天不玩、特别是日落后统一切护眼模式（如 Dell ComfortView）。日照窗口默认按固定时间（07:00–18:30）判定，配置经纬度则逐日按日出日落实时判定，也可以 `location: auto` 让 Windows 定位自动提供坐标（一次定位本地缓存，无第三方网络请求；需系统"定位"开关开启，拿不到就自动回落固定时间窗）。默认关闭；默认配方针对 Dell U2720QM 实测定标，其他显示器可覆盖。热重载可用，无需重启。详情见[显示器预设切换指南](docs/guides/monitor-mode.md)。

U2720QM 默认护眼配方改为单笔 `F0=0x0C`，固件联动完成 `DC=00 / E2=1D / F0=0C`，从游戏模式直接进入护眼；自定义配方仍按原顺序写入。`SetVCPFeature` 每次只写一个码，没有多码原子事务。

每 10 秒查询 Windows 当前 HDR 状态：HDR 开启或状态无法确认时，暂停 DDC 探测与读写；确认关闭后自动恢复当前游戏/日照规则，也能识别手动切换 HDR。与 HDR 也互斥：库内启动且勾选了"启动前开启 HDR"的游戏在跑期间，预设自动切换整体暂停，该会话退出后恢复；外部启动按游戏模式规则处理，不因 HDR 勾选而暂停。SDR 下继续检查显示器连接，连接提示仅在启动、断开或恢复时输出。

### 更新重启不断计时（会话跨重启续算）

GameHelper 更新替换重启（或异常崩溃）时，正在统计的游戏会话按**原开始时间**续算，游戏退出时落一条连续记录；游戏在停机间隙退出的，按最后已知存活时间收尾落库。在途快照 `%AppData%\GameHelper\inflight-sessions.json` 记录全部游戏进程，随每次进程变化及每 10 秒刷新；恢复后立即原子重写，连续多次更新也能接续。进程创建时间用于防止同一程序复用 PID 时串局，恢复落库失败则保留快照重试。部署脚本 `-Restart` 只清理已退出属主的 ETW 会话，且新实例启动失败会恢复旧产物和旧实例。详见[验证与部署工作流](docs/guides/verify-and-deploy-workflow.md)。

默认路径：`%AppData%\GameHelper\config.yml`

示例：

```yaml
monitor: ETW
startup:
  autoStartMonitor: false
  launchOnStartup: false
cheatEngine:
  executable: "C:\\Program Files\\Cheat Engine\\cheatengine-x86_64-SSE4-AVX2.exe"
games:
  - dataKey: witcher3
    executable: "D:\\Games\\The Witcher 3\\bin\\x64\\witcher3.exe"
    displayName: 巫师3
    enabled: true
    hdr: false
    speed: 2.0
    # 可选；默认 auto（检测到 mod loader 时延迟附加速度）
    # speedAttach: immediate
```

说明：
- `dataKey`：统计主键，写入 `playtime.csv` 的 `game` 字段，必须全局唯一。
- `executable`：可执行文件路径或进程文件名；路径会用于精确匹配，文件名会自动从路径派生。
- `enabled`：是否参与监控、时长统计和自动化。
- `hdr`：是否在该游戏运行时由 GameHelper 自动开启 HDR；`false` 不会关闭用户已经手动开启的 HDR。
- `speed`：可选的游戏倍速，必须大于 `0`；省略时不修改速度。GameHelper 为每次附加动态生成独立的临时 CE 表，同一 PID 的多次请求互不覆盖，无需维护 `.CT`。
- 游戏退出后，为它启动的 CE 倍速实例会自动关闭；手动打开的其他 CE 不受影响。手动把该实例切换到其他进程时，取消自动关闭。
- `speedAttach`：可选的 CE 附加时序策略，`auto`（默认）在检测到 BepInEx/MelonLoader 时推迟附加到 loader 启动完成之后（90 秒超时兜底），`immediate` 恢复一启动就附加的旧行为。
- `cheatEngine.executable`：Cheat Engine 可执行文件的绝对路径；只在至少一款游戏配置了 `speed` 时使用。
- `startup.autoStartMonitor`：交互模式启动后是否自动进入实时监控。
- `startup.launchOnStartup`：是否随系统启动 GameHelper。
- `sync`：统计推送到 GitHub 的配置（省略整段表示未配置；完整字段见 `docs/guides/sync.md`）。

ETW 启动后会补扫已经运行的配置内游戏，因此先开游戏、再开 GameHelper 也会应用倍速并开始计时；没有恢复快照时从接管时刻计时，已有快照则保留原起点。

Unity 游戏重复附加时会重新连接已有 CE 钩子并刷新实际速度，保留暂停状态，避免配置 1.5 倍在重启后只改目标变量或重复相乘。

首次使用倍速前，需要在 Cheat Engine 的设置中允许受信任表执行 Lua；否则 CE 会在每次加载 GameHelper 生成的表时询问。完整说明与注意事项见 [游戏倍速配置](docs/guides/game-speed.md)。该功能只适合离线单机游戏，不要用于联网或带反作弊的游戏。

CLI `config add` 可接收可执行文件名或 `.exe` 路径。传入路径时会保存到 `executable`，运行时从中派生路径与候选进程名。

配置更新以完整文档事务提交：修改游戏清单不会覆盖监控或启动设置，写入失败也不会留下部分更新。

监控匹配：
- GameHelper 只处理启用配置中的候选进程名，候选名来自 `executable` 的文件名；ETW 与 WMI 降级路径都使用这道候选名门控。
- 完整路径只在候选进程需要路径消歧时解析；非候选进程不会触发路径查询、WMI 详情查询、ProductName 读取或模糊匹配。

## 项目结构
- `GameHelper.ConsoleHost`：CLI 入口
- `GameHelper.Core`：核心模型与业务逻辑
- `GameHelper.Infrastructure`：平台集成与持久化
- `GameHelper.Tests`：单元/集成测试
- `docs`：活文档、计划与归档材料

## 开发与验证

```powershell
dotnet build GameHelper.sln
dotnet test GameHelper.sln
.\scripts\publish-console-smoke.ps1
```

更完整的架构、规范和计划见：
- `docs/architecture/index.md`
- `docs/prd/index.md`
- `docs/plans/index.md`

## 许可
- 开源使用：AGPL-3.0
- 商业使用：见 `LICENSE`

自动监控、更新及默认回归使用后台入口；CE 与 DS4Windows 请求隐藏启动。测试游戏采用无窗口探针，避免回归时弹出命令行；可见旧 Console 验收仅在显式启用时运行。
