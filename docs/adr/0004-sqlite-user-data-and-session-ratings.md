# 用户数据迁入 SQLite，为本局打分保留会话身份

2026-10-28 用户决定把 GameHelper 的用户数据迁到嵌入式 SQLite，使用 `Microsoft.Data.Sqlite`、一个主数据库和 WAL；`config.yml` 保持 YAML，sync 的 `daily.csv`、`README.md` 和可选 raw CSV 继续由库中历史导出，格式不变。原因：CSV 没有稳定的单局身份，多个 JSON 文档也无法与会话原子关联；后续本局打分需要持久的会话 ID、关联查询和可验证的迁移。

本记录是待分批实施的目标设计，不表示当前实现已切换。沿用 ADR-0002 的运行时归属：ConsoleHost 唯一拥有监控、计时和 HDR/CE，Desktop 不创建另一套自动化；本决定替代 ADR-0003 中“CSV 是当前游玩历史格式”的长期存储选择，不恢复遗留配置迁移命令。实现前后的统计语义分别以现有代码、[游玩档案](../guides/playtime-dashboard.md)、[年度／季度报告](../guides/playtime-report.md)和 [sync](../guides/sync.md) 为兼容基线。

## 范围与文件去向

数据库为 `AppDataPath.GetGameHelperDirectory()/userdata.db`，默认位于 `%AppData%\GameHelper\`。路径必须复用 `AppDataPath` 的实际解析结果：当前 `GAMEHELPER_DATA_DIR` 覆盖的是 base directory，其下仍追加 `GameHelper`，不能重新解释为数据库父目录。隔离实例沿用此规则。一个主库不等于运行中只有一个物理文件；WAL 还会产生 `userdata.db-wal`、`userdata.db-shm`，不能单独搬动主文件。[SQLite WAL 文档](https://www.sqlite.org/wal.html)说明了这些伴随文件及同机文件系统限制。

历史目前在数据目录共享，Desktop 侧文档则位于各自配置文件旁。迁移必须保留这一差异：会话仍是数据目录级历史；手帐等侧数据带 `scope_id`，由规范化的绝对配置路径识别，路径比较保持 Windows 的不区分大小写语义。使用另一个 `--config` 不应看到前一个配置的手帐或草稿；同一 DataKey 的共享时长也不能被导入多次。不擅自扫描整台机器寻找配置，每个实际打开的配置仅导入自己的侧文档。

下表中的 `<config>` 是实际 YAML 配置完整路径，默认即 `config.yml`；它不是固定文件名。

| 现有文件／目录 | 决定 | 理由与迁移内容 |
| --- | --- | --- |
| `playtime.csv` | 进库；兼容期保留文件投影 | 导入全部旧 codec 接受的行，保留顺序、大小写、重复行及保存的分钟数；最终不再是权威存储。 |
| `playtime.json` | 仅作遗留导入源，保留原件 | 仅 CSV 不存在时使用；CSV 存在但为空或有坏行也不与 JSON 合并，保持现有优先级。 |
| `inflight-sessions.json` | 进库；兼容期输出旧快照 | 含每个进程、创建时间、有效开始时间、待重试结束时间、启动来源、快照时间、HDR 归属、待启动截止时间；与历史完成事务合并，消除完成写入与快照清除之间的重复窗口。 |
| `<config>.desktop-journal.json` | 进库 | 按 scope + DataKey 保留五种状态、可空通关日期、单行 80 字短评；不是单局评分。 |
| `<config>.desktop-genres.json` | 进库 | 保存手动多类型覆盖，区分“无覆盖，采用 Steam 数据”和“显式空覆盖，未分类”。 |
| `<config>.desktop-artwork.json` | 关联进库 | 保留手动 DataKey → Steam AppID；图片本体不入库。 |
| `<config>.desktop-drafts.json` | 进库 | 保留整个草稿及 Baseline、启动选项、动作、倍速热键；草稿保存不等于配置保存。 |
| `<config>.desktop-scan.json` | 进库 | 最近扫描目录的顺序和最多六项、忽略的 executable 是用户选择，不能当可删缓存丢弃。 |
| `<config>.desktop-theme.json` | 进库 | 按配置保存 light/dark 偏好，仍与 YAML 设置独立。 |
| `config.yml`／自定义 YAML | 保留文件 | 手写配置、热重载、凭据、启动配置和 Catalog 继续由 `IGameConfiguration` 原子修改，遵守配置兼容契约；不复制为第二份数据库配置。 |
| `location-cache.json` | 保留文件 | 可重建的自动定位缓存，失效后沿用固定时间回退；不属于必须迁移的用户记录。 |
| `cache/steam-genres/<appid>.json` | 保留文件 | 可重新请求的 Steam 元数据，与手动类型覆盖分开。 |
| `cache/steam-posters/<appid>.jpg`、`.capsule.jpg`、`.header.jpg` 及 `.retry` | 保留文件 | 下载图片和重试节流缓存，可删后重建；外部 Steam `appcache/librarycache` 不归本工具迁移。 |
| `sync-state.json` | 保留文件并扩展本地书签 | 上传目标、成功／失败时间、内容指纹和退避是运行簿记，可重建；新增数据库标识与历史修订号，不引入上传事务与用户数据事务耦合。 |
| `sync/<owner-repo>/` | 保留文件 | git 上传专用的可重建浅克隆，不是用户历史备份。 |
| `cheat-engine/speed-*.ct` | 保留临时文件 | CE 消费的瞬态脚本，保留原到期清理机制。 |
| `<config>.runtime.log`、Desktop 临时错误日志、`--snapshot` 截图／验证产物、用户导出的报告 Markdown | 保留文件 | 日志、诊断派生物和用户选择的导出目标，不作库内主数据；不迁移安装目录资源、Steam 清单或部署 manifest。 |
| 原子写入 `.tmp` 等残留物 | 不导入 | 不是已提交的数据；迁移只读取正式源文件，原件备份不把临时文件当权威。 |

## 关系结构、时间与版本

使用显式 SQL 和 Infrastructure 内的存储适配器，不引入 ORM。Core 保留存储无关的用例与统计，扩展 `IPlayTimeService`／`IPlaytimeSnapshotProvider` 和侧数据边界，不把 SQL、数据库路径或连接暴露给视图。所有连接注册同一个、版本固定的 `GH_ORDINAL_IGNORE_CASE` collation，委托 .NET `StringComparer.OrdinalIgnoreCase`；DataKey、配置路径、忽略路径沿用原有比较，不能直接以 SQLite 内置 `NOCASE` 替代 Unicode 比较。原始字符串同时保留以维持展示和导出。

下表是目标 schema 契约，`TEXT`／`INTEGER` 为 SQLite 存储类型；除明确可空项外均非空。PK、FK、UNIQUE、CHECK 由库约束，值范围和用户提示由 Core 规则负责。所有 scope_id 都引用 config_scopes；侧表的 DataKey 不外键依赖历史或 YAML，允许没有历史的在库游戏与已移除游戏。所有业务 DataKey 列采用上述 collation。未列业务默认值由当前模型沿用，不在迁库时改变。

| 表 | 主键、列与约束 |
| --- | --- |
| `storage_meta` | 单行 PK；`database_id TEXT`（随机 UUID）、`history_revision INTEGER`、`data_revision INTEGER`、`compatibility_mode TEXT`。完成会话才递增 history_revision；任意用户数据变化递增 data_revision，快照心跳不递增二者。 |
| `config_scopes` | `scope_id INTEGER PK`、`config_path TEXT UNIQUE COLLATE GH_ORDINAL_IGNORE_CASE`。不把配置内容或密钥放入此表。 |
| `history_keys` | `data_key TEXT PK COLLATE GH_ORDINAL_IGNORE_CASE`；`first_sequence INTEGER UNIQUE` 保留首次出现顺序，读取显式 ORDER BY first_sequence，不依赖 VACUUM 可重编号的隐式 rowid；保留首次历史拼写。不是 YAML Catalog 的镜像，移除游戏不删此行。 |
| `play_sessions` | `session_id TEXT PK`（UUID）、`data_key TEXT FK history_keys`、`sequence INTEGER UNIQUE`、`started_value TEXT`、`ended_value TEXT`、`started_kind INTEGER`、`ended_kind INTEGER`、`duration_ticks INTEGER`、`duration_minutes INTEGER CHECK >= 0`、`launch_source TEXT CHECK IN ('library','external','unknown')`、`completion_source TEXT CHECK IN ('observed','recovered','legacy')`、`created_at_utc TEXT`、可空 `import_source_id TEXT`／`import_record_ordinal INTEGER`，两者联合 UNIQUE。 |
| `session_ratings` | `session_id TEXT PK/FK play_sessions ON DELETE RESTRICT`、`stars INTEGER CHECK BETWEEN 1 AND 5`、`thought TEXT`（可空）、`created_at_utc TEXT`、`updated_at_utc TEXT`、`revision INTEGER`。每局最多一条当前评分；未评分就是无行，不用 0 星。 |
| `journals` | `(scope_id, data_key) PK`，scope FK；`status TEXT CHECK` 为现有五种状态、`completed_on TEXT NULL`（ISO 日期）、`note TEXT`（单行 ≤80 字）、`revision INTEGER`、`position INTEGER`。不依赖游戏仍在 YAML 中。 |
| `genre_overrides` | `(scope_id, data_key) PK`、`revision INTEGER`、`position INTEGER`；父行存在表示显式覆盖，即使没有子项。 |
| `genre_override_items` | `(scope_id, data_key, genre) PK/FK genre_overrides`、`position INTEGER`；采用现有 `Normalize` 规则，不把 Steam 自动类型复制为手动项。 |
| `artwork_links` | `(scope_id, data_key) PK`、`steam_app_id INTEGER CHECK BETWEEN 1 AND 4294967295`、`revision INTEGER`；`position INTEGER`；解除关联删除行。 |
| `desktop_drafts` | `(scope_id, draft_id) PK`、`data_key TEXT`、`position INTEGER`、`payload_version INTEGER`、`payload_json TEXT`、`revision INTEGER`；保留完整现有 LibraryDraft（含 Baseline），版本化 payload 不参与运行配置读取；旧列表按来源 ID + 项序号分配 draft_id，后续新草稿分配 UUID。 |
| `scan_roots`／`scan_ignored` | 分别 `(scope_id, directory) PK` + `position`、`(scope_id, executable) PK` + `position`，路径采用上述 collation；保存最近目录作为一个事务，最多六项。 |
| `desktop_preferences` | `scope_id PK/FK`、`theme TEXT CHECK IN ('light','dark')`、`revision INTEGER`。 |
| `runtime_snapshot` | 单行 PK，`snapshot_at TEXT`（含原时间语义）、`hdr_enabled_by_helper INTEGER CHECK IN (0,1)`、`revision INTEGER`。保持现在数据目录级唯一监控拥有者。 |
| `inflight_sessions` | `session_id TEXT PK`、`data_key TEXT`、`started_value TEXT`／`started_kind INTEGER`、`launch_source TEXT`、可空 `recovery_end_value TEXT`／`recovery_end_kind INTEGER`；一局可对应多个进程，但只有一个历史 session_id。 |
| `inflight_processes` | `process_row_id INTEGER PK`、`session_id FK inflight_sessions`、可空 `pid INTEGER`、可空 `normalized_name TEXT`／`normalized_path TEXT`／`process_started_at_utc TEXT`、可空 `launched_by_helper INTEGER`、可空原始 `data_key TEXT`；以 process_row_id 保持进程顺序，原始 key 与逐进程来源保留旧快照字段；PID 不是永久主键。 |
| `pending_launches` | `data_key TEXT PK`、`deadline TEXT`（DateTimeOffset round-trip）、`position INTEGER`；不代表已开始会话，不重放启动命令。 |
| `import_sources`／`import_records` | `import_sources`：`source_id TEXT PK`、可空 `scope_id INTEGER FK`（历史源无 scope）、`path TEXT`、`sha256 TEXT`、`size_bytes INTEGER CHECK >=0`、`codec_version INTEGER`、`status TEXT`、`accepted_count INTEGER CHECK >=0`、`skipped_count INTEGER CHECK >=0`、`validation_summary TEXT`；`import_records`：`(source_id, record_ordinal) PK`、source FK、`disposition TEXT CHECK IN ('accepted','skipped')`、`reason TEXT`、可空 `session_id TEXT FK play_sessions`。失败明细留本机。 |
| `migration_state` | `name TEXT PK`、`value TEXT`；候选来源代际、校验通过、激活与离线再升级的库内事实，不安装生产切库入口。 |
| `compatibility_exports` | `target TEXT PK`、`data_revision INTEGER`、`snapshot_revision INTEGER`、`content_hash TEXT`、`failure TEXT`（无失败为空字符串）、`dirty INTEGER CHECK IN (0,1)`；事务内置脏标记，事务外投影，不把“库成功、文件失败”当两边都成功。 |

新会话在首次被实际进程观察时分配 session_id，写入在途记录，重启接管和完成后保持 ID；多进程同 DataKey 的引用计数只产生一局。休眠调整有效开始时间不能改变 ID。现有 `PlaySession` 只有 GameName（实际为 DataKey）、StartTime、EndTime、Duration、DurationMinutes；启动来源目前仅在在途快照的可空 `LaunchedByHelper` 中，历史 CSV 不含来源，导入必须记 `unknown`，不可伪造为库内启动。旧快照缺少此字段时，HDR 续算仍按现有“视作库内”规则执行，存储事实继续标为 unknown。

历史日期以 round-trip 值和原 DateTimeKind 保存，不把无时区时间猜成 UTC；有明确 UTC 的保留 UTC，已有 Local 的读取行为保持原 .NET 语义。`duration_minutes` 是统计事实，不由起止时间反算；`duration_ticks` 保留旧运行路径的 Duration（CSV 为 end-start，legacy JSON 先按旧 JSON→CSV 投影后同样取 end-start），不可强制二者相等；JSON 中被旧迁移忽略的字段仍留在原件包中。未来新会话额外保存已知 UTC／offset 时仍保留有效开始时间与旧导出投影。档案和年度／季度报告继续按注入时区的本地结束日整局归档、不切分跨日，零分钟计次数，过滤未来／逆序记录；sync 继续走 `StatsReportBuilder` 的现有口径，不能给它顺手套用档案过滤规则。

索引至少包括 `play_sessions(data_key, sequence)`、`session_ratings(updated_at_utc, session_id)`、`inflight_sessions(data_key)`、`inflight_processes(session_id)`、各侧表的 scope 联合主键和导入来源唯一索引。日期字符串含不同 Kind，不能依靠其词典序做跨时区统计范围过滤；第一阶段仍由 Core 对一致快照计算统计。评分曲线先按 DataKey 索引取会话，再按转换后的结束时刻、sequence 排序；确有性能证据后才新增可重建的日期投影索引。

用 `PRAGMA user_version` 作为唯一 schema 版本号，按嵌入程序集的编号 SQL 逐版前进，不另设第二个版本事实源。迁移在启动协调锁下先备份，DDL、数据回填与 user_version 更新置于同一事务；失败全部回滚。批次 1 的版本 1 建全部表，版本 2 建查询索引；初始 storage_meta.compatibility_mode 为 `legacy`，两个修订为 0，不表示已导入或切库。每次连接先检查版本，Desktop 也通过同一初始化器，数据库版本高于程序支持范围时明确拒绝写入，不自动降 schema。批次 2 增加版本 3：旧 JSON 顺序列、可空进程身份与逐进程来源/拼写、库内维护状态；不改动已发布的版本 1/2 SQL。导入状态不是 schema 版本，另由 import_sources 记录。[SQLite PRAGMA 文档](https://www.sqlite.org/pragma.html#pragma_user_version)定义了 user_version，并区分 integrity_check 与外键检查。

## 两个进程与在途恢复

选择 **ConsoleHost 和 Desktop 直接共享数据库 + WAL**，而不是所有写入经 IPC 交给后台。ConsoleHost 独占会话、在途快照、HDR 归属与待启动状态的业务写权；Desktop 只写手帐、评分、类型、图片关联、草稿、扫描偏好与主题。这样后台未运行时仍可保存私人档案及草稿，避免把本地编辑变成依赖监控进程在线的操作。现有启动、监控、配置保存和统计响应继续走既有 IPC，Desktop 不绕过 IPC 控制自动化；统计通过同一存储边界读取，评分和手帐的报告合并也使用同一读事务快照：ConsoleHost 的 Core 报告入口按当前 scope 一次读取会话、手帐和评分，返回合并结果，Desktop 不在 IPC 返回后另读一份新手帐再拼接。

WAL 允许读写并行但仍只有一个 writer。每次操作新建短生命周期连接，不跨线程共享 connection／command／reader；使用私有 cache（不启用 shared cache），每连接 `foreign_keys=ON`、`busy_timeout=5000`、`synchronous=FULL`，初始化协调阶段启用并读回 `journal_mode=WAL`。Microsoft.Data.Sqlite 的 command/default timeout 同样设为 5 秒，并在适配层统一超时预算，不能层层重试让等待无限叠加；锁冲突时回滚整个业务事务，UI 保留编辑、运行时保留待重试完成行。短写事务提前取得写锁，UI 数据准备、网络、解码、进程探测、通知和上传全部在事务外。官方说明见 [WAL 并发](https://www.sqlite.org/wal.html#concurrency)及 [Microsoft.Data.Sqlite 错误与超时](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors)。

初始化／首次导入／schema 迁移／兼容投影通过按数据目录隔离的跨进程协调锁串行化，读写业务仍由 SQLite 事务协调。后台完成初始化后才报告存储就绪，Desktop 不得在导入中读取半成品；手动 CLI 也复用同一初始化和存储边界。数据库只放本机磁盘，不把 WAL 库当云盘或网络共享文件跨机并发打开。

在途快照进库，保留现有每十秒刷新和状态变化持久化节奏。完成会话时以同一个 session_id 幂等插入历史、递增修订、移除对应在途进程和会话、标记兼容投影脏；提交后才发布完成事件。未提交则原在途行仍在，恢复可重试。进程探测放在事务外，提交前核对快照 revision 与运行代际。保持 PID + 创建时间 + executable 核验、停机死亡按最后存活快照时间封口，只有终点未知的估算恢复跨度限十二小时；写失败保留固定 RecoveryEndAt，不裁剪已知终点、HDR 归属和待启动截止时间、不重放启动或退出动作。已有快照中相同 DataKey/StartedAt/RecoveryEndAt 的多进程组分配一个稳定 ID；恢复行若已存在旧 CSV，使用旧版按秒、忽略 DataKey 大小写的匹配规则关联既有历史，不再新增一局。匹配存在多个重复行时记录选择的最早 sequence，不删除其余合法历史。

## 首次导入与回滚窗口

1. 先停止旧版 ConsoleHost 和 Desktop，取得数据目录协调锁，再启动新版初始化器；不在旧进程仍写 CSV/JSON 时拍快照。首次迁移在开放监控与 Desktop 编辑前完成，保存所有存在的正式源文件原始字节到 `migration-backups/<migration-id>/`，manifest 记录路径、scope、哈希、长度、codec／程序版本；缺失文件也记录为缺失。不能复用 DesktopDocumentStore 的“读取失败返回空文档”掩盖原件损坏。
2. 在独立候选库 `userdata.db.importing` 建 schema。历史按 CSV 优先、否则 legacy JSON 的规则解析，JSON 到历史采用现有 JSON→CSV 的投影语义；侧文档沿用各 store 的已接受字段与校验规则。类型读取不额外执行仅在旧 Save 才做的 Normalize。旧 Read 可接受但当前关系约束无法无损表达的值（如 0 AppID、大小写冲突的 artwork、重复类型或超限/重复扫描项）保留原件并阻断，不能静默截断或改值。语法坏行及语义被跳过的侧项逐项记录，原字节保留；整份 JSON 解析失败、非空 CSV 表头错误或来源不可读则阻断激活，不能成功迁成空历史。合法空文件／无历史必须与异常明确区分。
3. 来源 ID 固定于本次源清单，历史 UUID 由来源 ID + 逻辑记录序号确定；唯一约束与导入游标在同一批事务提交。完全相同的两行有不同序号，不能以内容哈希去重。重复运行同一清单继续未完成批次，已提交批次不重复；源哈希改变就作废尚未激活的候选库，重新备份导入，不能把两代文件混装。未知配置以后首次打开再做 scope 级导入：备份和来源清单规则相同，在现有库内用一个事务提交该 scope 的侧数据与完成标记；不得用新候选库覆盖已激活的共享历史。
4. 在固定时钟、相同时区和同份配置上，旧读路径与候选库分别生成比较结果：逐游戏的记录数、总分钟、最近窗口分钟、每个本地结束日分钟／次数、零分钟次数、最早／最晚记录、跨日归属、已移除游戏、档案各期间 KPI 和年度／季度报告逐项一致。再以原 StatsReportBuilder 比较 daily.csv、README、raw CSV 的完整 UTF-8 字节和内容指纹；接受／跳过数量与 manifest 对账，侧文档读取值、显式空覆盖及草稿字段逐项一致。失败不激活，不以“总时长一样”替代校验。
5. 完成 `integrity_check` 和 `foreign_key_check`，成功 checkpoint 后关闭候选库全部连接及连接池，确认没有剩余 WAL 帧，再原子改名为 `userdata.db`。库内激活记录才是事实源；外部 `migration-complete.json` 仅供诊断，可在重启时从库补写，避免跨文件标记成为另一事务。原 CSV/JSON 的不可变备份永不覆盖；原位置在兼容期转为投影文件，以 manifest 明确区分。候选库激活前崩溃继续步骤 3，激活后标记漏写则补标记，不能重导历史。
6. 激活前任一失败，保留错误与原件，旧版可继续从未改动的源文件运行；不能让新版一边报迁移失败一边写空库。激活后的 SQLite I/O／损坏错误不自动回落到旧 CSV 写入，以免形成两个权威历史；显示存储故障，保留在途重试状态，进入显式恢复流程。

选择 **首个 SQLite 正式发布版本保留完整旧格式投影一个发布周期**，不做两个互不协调的权威写入。库提交时在 compatibility_exports 标脏，串行投影器在提交后以临时文件替换输出最新完整 `playtime.csv`、已迁移的六类 Desktop JSON 和 `inflight-sessions.json`；保留各旧 codec 的字段、排序、编码、换行和有效值，位置不变。跨文件投影不是原子事务，失败不会撤销已成功库提交，也不能谎报旧版可安全接手；启动、正常停止和部署降级前均重试排空。完成路径因此有兼容期额外写盘，这是对现 sync 指南“会话结束只写一行 CSV”的有意阶段性变更；sync 不增加远端上传动作。

回到旧版必须先由新版的离线维护入口执行“准备降级”：停止两个进程、取得锁、输出到同一固定库修订、核对所有投影哈希与旧读路径统计、备份当前主库及源文件并写降级 manifest，验证成功后才允许旧版启动。正在运行的游戏也要输出当前旧快照，旧版继续按现机制接管。若投影／校验失败则阻断降级而不是丢弃库内增量。UUID、启动来源和未来评分等旧格式无法表达的字段保留在封存库，旧版不可见但不删除。

从旧版重新升级时读取降级 manifest：按旧版 append-only 契约核对导出时 CSV 的完整有序前缀，以逐行基线和 occurrence（含合法重复行）匹配已知 session_id，旧版新增记录只追加一次；侧文档在对应 scope 原子导入旧版的最新值。评分继续关联原 ID。旧版删除／改写历史导致无法无歧义匹配时停止自动合并，保留两边原件并给出差异，不重新分配已有评分的身份。没有降级 manifest 的手工覆盖安装不承诺安全降级，恢复应使用封存库与导出工具。下一发布版本可停止实时旧格式投影，但“准备降级”离线导出入口和不可表达数据的封存能力继续保留；清理旧备份必须是显式维护动作，不在首次成功启动时删除。

## 一致性、损坏检测与备份

会话完成／在途删除／history_revision／兼容标记是一个事务；快照整体替换（含 HDR 和待启动）、手帐一次保存、类型父子替换、扫描目录排序、草稿集合保存各自为事务。评分 upsert 带客户端期望 revision，手帐及其他可编辑行同样检查 revision，冲突保留用户编辑并要求重载；不能以旧的整个 JSON 字典覆盖另一进程新写入。跨 YAML 与库不假装有分布式事务：保存配置前草稿已经独立持久化，配置保存仍核对 Baseline／catalog revision，失败保留草稿，成功后再清除；重复清除幂等，重启遇到旧草稿仍按 Baseline 冲突规则处理。

统计、评分曲线、年度／季度报告在同一个短读事务获取需要的会话、手帐和评分后释放连接，再在内存聚合、渲染或发网络请求。导出的历史顺序复现旧快照“DataKey 首次出现的分组顺序 + 组内原记录顺序”，并保留同开始时间的稳定顺序，避免 raw CSV 排序同分时改变字节。DataKey 拼写遵守旧快照分组取首项的行为。

sync 脏检查从 CSV mtime 改为 `database_id + history_revision` 加既有配置变更检查，不以 `userdata.db` 或 `-wal` 的 mtime 判断更新。每次导出捕获历史 revision，上传成功只记录实际上传的 revision；上传期间有新会话则仍为待推送。数据库恢复／替换后更换 database_id，使旧书签必定失效。保留间隔、退避、内容指纹、受管文件清单和 includeRawCsv 关闭后的远端删除行为；评分、手帐、快照心跳不触发 sync，也不把私人想法加入同步或模型请求。

首次激活、每次 schema 升级后、每日首次维护及备份验证运行 `PRAGMA integrity_check`，并单独运行 `PRAGMA foreign_key_check`；日常启动先做 `quick_check`，异常升级完整检查。检查失败停止库写入和 sync 上传，不把损坏结果解释为空历史，不自动覆盖损坏原件。保留原主库及 WAL/SHM 的现场，在关闭全部连接后恢复到独立候选路径，校验成功再原子换入；恢复备份之后的未备份数据仍需从现场／兼容投影核对补回，备份不能承诺零数据损失。

迁移前原件包、schema 升级前备份、每日首次有用户数据变更后的自动备份分开管理。每日按注入时钟的 UTC 日期划分；批次 1 提供可调用原语，批次 6 才接入生产维护调度。每日备份由进程协调锁指定一个执行者，通过 `SqliteConnection.BackupDatabase` 写独立临时库，验证完整性与外键后原子发布到 `backups/`，保留最近 7 个每日成功版本和最近 3 个升级前版本；首次迁移原件包单独保留。记录数据库 ID、schema、修订、时刻与摘要；无变更不备份、失败不轮换掉上一份成功备份，下次维护重试。使用注入时钟确定日期与保留策略，备份在业务事务外执行并报告耗时；[Microsoft 备份文档](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup)指出 BackupDatabase 期间会阻挡其他连接写入，因此安排在无完成写入待处理的维护空隙，不能称为完全无阻塞后台任务。

数据库备份包含私人文本与草稿，仅落本机，不等于 sync 公共统计。可恢复整套用户环境还需一起保留 YAML 和配置路径／scope 清单；图片及定位缓存不必备份，可重建。恢复动作停止所有进程并关闭连接池，校验候选库后整体替换，绝不只复制运行中的 `.db` 而丢弃 WAL，也不逐文件覆盖正在使用的数据库。

## 后续本局打分的行为边界

一局指同 DataKey 从第一个有效进程进入到最后一个退出的一段已完成会话，多进程不是多局；session_id 独立于进程 ID、显示名和起止时间。ConsoleHost 在正常观察退出且历史成功提交后，对保存时长 ≥10 分钟、尚未评分的一局发一次托盘邀请；通知参数只携 session_id，点开 Desktop 小窗选择 1–5 星和可选想法，只有明确保存才写 session_ratings。取消／忽略不新增评分、草稿或已读标记，也不重复追发；重启恢复补记、历史导入及手动停止不补发邀请。通知投递采用尽力而为，崩溃窗口允许漏通知，档案补评兜底，不为提醒引入持久队列。

档案增加按需分页的会话回顾入口，以 session_id 补评或修改任意已结束历史（十分钟门槛只限制自动邀请）；保持当前汇总页默认不请求全量会话。游戏详情按会话结束时间回顾星级变化，想法仅本机展示。当前值覆盖修改，不引入评分修订历史；同局并发修改按 revision 冲突。年度／季度页面及手动 Markdown 报告增加期间内已评分会话的星级算术平均与样本数，不按时长加权，未评分不算 0，零样本显示未评分；归档日期仍按会话结束日，补评时间不改变归属。平均分不加入 sync README，避免违背已确认的远端格式不变。想法不进入 sync 或可选大模型请求，手帐短评的既有隐私边界保持。

## 可独立合入的实施批次与验收

所有批次在 Windows CI 上跑构建与相关行为测试，新增 SQLite 依赖时同步 tech-stack／依赖定义。存储测试用临时数据目录和真实磁盘数据库，不用纯内存连接替代 WAL 多进程行为；仅发布运行探针涉及 ETW/WPF，其余迁移与并发测试不需要管理员权限。每批启用新行为时更新对应指南与 README，不能把本 ADR 的目标描述当成已经上线的使用说明。

| 批次 | 可独立交付内容 | CI 验收要点 |
| --- | --- | --- |
| 1：存储内核 | 引入 Microsoft.Data.Sqlite、连接／collation／初始化器、schema 编号迁移、备份恢复；生产仍用旧存储。 | 空库建立、逐版升级、未知高版本拒写、DDL 故障回滚、外键／星级约束、Unicode DataKey 对比、备份恢复可读；相同数据目录初始化竞争不产生两份 schema。 |
| 2：导入与兼容导出工具 | 固化旧 codec 样本和期望产物，候选库迁移、manifest、幂等恢复、完整旧格式投影及准备降级／再升级；尚不自动切换生产。 | CSV／legacy JSON、六类侧 JSON、多配置 scope、缺失和损坏、重复行、逗号／双引号／换行／中文、BOM、UTC／无时区、跨日跨年、零分钟、无效行；注入每个提交／改名／标记断点，重跑不重计；离线降级再升级保留 ID 和新增数据。 |
| 3：会话与恢复切换 | 引入稳定 ID，历史和在途一起切库，启用基础 SQLite snapshot adapter 和首个发布周期兼容投影；自动激活只在全部校验通过后开放。 | 在真实 SQLite 事务边界故障注入：完成前后强杀、重复 stop、PID 复用、多进程引用、休眠偏移、连续两次恢复、写失败固定结束时间、HDR／待启动接续；同一局只落一行，旧版投影可接管。 |
| 4：统计与 sync | 完善 SQLite snapshot adapter 与报告一致快照；档案、年度季度统计复用 Core，sync 改 revision 门控并从库导出。 | 与旧版固化样本逐项比 KPI、期间汇总与 daily.csv／README／raw CSV 完整字节（UTF-8、换行、排序、转义、大小写、同分顺序）；上传中追加不会漏下一次推送，checkpoint／心跳不触发脏判，includeRawCsv 关闭删除仍通过。 |
| 5：Desktop 侧数据切换 | 迁入手帐、类型、关联、草稿、扫描与主题，保留现有 UI 和各字段语义；初次打开新配置补导入。 | 两 scope 同 DataKey 不串数据、共享历史不重导；手帐报告一致、显式空类型覆盖、草稿全部字段往返与冲突、扫描顺序上限、保存失败编辑不丢；两进程同时保存互不覆盖。 |
| 6：迁移发布与恢复闭环 | 启用每日备份和健康维护、旧格式投影故障提示、降级工具发布及兼容窗口说明；下个正式版本关闭实时旧格式投影另行验收。 | 真实子进程并发 reader／writer、writer 争用超时、进程中止、磁盘满／只读替身、长读事务释放后的 checkpoint、损坏备份拒换入、恢复后 sync 书签失效；发布隔离实例验证迁移、IPC、重启续算和降级预检。 |
| 7：本局打分（最后） | 启用评分表用例、结束邀请、小窗、档案补评、详情趋势和报告平均分，不改变 sync。 | 注入时钟与通知替身验证 9 分钟／10 分钟边界、多进程只邀请一次、导入／恢复不补发、忽略取消零写入、保存失败保留输入、并发评分冲突、补评不改时长、跨期归属、无评分与平均分样本数、sync 字节与模型请求白名单不变。 |

批次 1–2 只增加未启用基础能力；批次 3 上线时，凡已切库的数据必须已有可用的兼容投影与降级工具，不能等批次 6 才补救。批次 3 尚未改造的 sync 暂以完整兼容 CSV 的 mtime 工作，批次 4 才切 revision 门控。尚未切库的 Desktop 文档继续按旧路径工作，批次 5 才导入并改其写权。批次 6 将维护与发布验收闭环，不作为之前批次正确性的隐藏前置条件。评分表结构可提前建立空表，但评分行为始终最后启用。

测试使用固定 `TimeProvider`、明确时区及相对测试时点，覆盖夏令时与期间边界，不依赖执行机器今天、午夜或真实延迟；并发以屏障／事件协调而非 sleep 赌时序。期望 CSV 字节由旧版本一次生成后固化入库，禁止测试中调用新序列化器自造期望。若历史数据被旧实现跳过，报告必须列出源记录与原因，让迁移验收同时证明“统计兼容”和“原始数据没有被悄悄删除”。

批次 2 的未启用 API、验证与离线调用边界见 [导入与兼容工具](../architecture/sqlite-import-tools.md)。本批不部署、不自动激活，也不增加生产 CLI/DI/UI；已导入 scope 的缺失侧文档可在显式投影时生成默认空文档。导入首次校验成功时按有无历史/用户记录初始化修订为 1/0，续导不重复递增。


## 批次 3 实施澄清

- 生产首次候选清单使用 `sessionsOnly`：备份并严格解码当前配置的六种侧文档，无法无损表达仍阻断；
  仅历史/在途写库并激活，侧表和 scope 留给批次 5，运行时投影只输出 CSV 与在途 JSON。
- 状态锁内存储例外仅保留会话/在途短事务，事务和结果处于同一 generation 执行边界；
  完整兼容投影、目录锁及投影标记事务改由锁外串行执行器运行，提交后仅投递请求，Stop 在锁外排空。
  一次事件的完成及失败补录共用五秒单调争用预算，计时采用等待前固定的事件时间。
  此收窄例外及完整锁关系见[会话存储](../architecture/sqlite-session-storage.md#锁边界的有意选择)。
- 兼容文件内容相同时不替换，快照心跳不会改变 CSV mtime。sync 在读 CSV 前排空投影，失败阻止陈旧上传；
  修订门控仍按批次 4 实施。首次上线提供已有降级/再升级原语的离线 CLI。

- 生产兼容 CSV 为保持原旧读取语义，保留旧 codec 已接受的 UTC `Z`/Local offset 后缀；不将 UTC 字面值改为无时区时间。降级 manifest 记录格式，再升级复用；无时区字段与旧 writer 固化工具默认模式不变。
