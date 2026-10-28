# SQLite 存储内核（ADR-0004 批次 1）

此内核尚未接入 ConsoleHost、Desktop 或生产 DI；CSV/JSON 仍是当前读写路径。不要把初始化内核当成旧数据导入或切库入口。

## 边界与入口

`GameHelper.Infrastructure.Storage.Sqlite` 使用显式 SQL；Core 不引用 SQLite 包。`SqliteConnectionFactory` 默认复用 `AppDataPath.GetGameHelperDirectory()`，因此 `GAMEHELPER_DATA_DIR` 仍是 base directory，其下追加 `GameHelper/userdata.db`。显式构造参数则是已解析的数据库目录，供隔离测试和未来候选库工具使用。

调用 `SqliteDatabaseInitializer.InitializeAsync` 后，使用 `OpenConnection` 获取短生命周期连接。连接不池化、不共享 cache；每连接注册固定 `GH_ORDINAL_IGNORE_CASE`（`StringComparer.OrdinalIgnoreCase`），开启外键、FULL 同步与 5000ms busy timeout，provider/command 默认超时 5 秒；不额外重试 SQLite 操作。所有连接先检查 `user_version`，高版本明确拒写；普通连接也拒绝未初始化或尚待升级的库。

目录中的 `.userdata.lock` 以独占文件句柄协调初始化、迁移、备份与恢复；释放后保留锁文件，防止删除锁文件产生两个锁身份。获取锁最多等待约 5 秒，支持取消。锁顺序是目录协调锁 → SQLite 事务；禁止业务事务内反向取得目录锁。普通业务读写仍由 SQLite 管理，不持目录锁。

## Schema 与健康检查

唯一版本事实源是 `PRAGMA user_version`。嵌入资源 `001_tables.sql` 建立 ADR 全部 20 张表，`002_indexes.sql` 增加 4 个显式查询索引；主键、导入记录来源/序号及来源关联另外由唯一约束保护。所有待执行版本的 DDL、初始 metadata 和 user_version 同事务提交，失败全部回滚。初始化不会导入任何旧数据，初始 compatibility_mode 为 `legacy`，修订为零；后续适配器负责业务修订递增。

手帐状态保存现有中文值。SQLite 的 note 长度 CHECK 按 Unicode code point 计数；后续 Core/侧数据适配器仍须沿用现有 .NET UTF-16 长度 ≤80 的用户校验。DateTime 值与 Kind 分列，不添加会改变旧历史语义的日期排序或 duration 等值约束。history_keys.first_sequence 显式保存首次出现顺序，未来导入器分配序号并按其排序，不依赖无 ORDER BY 查询或 VACUUM 可改变的隐式 rowid。

初始化日常执行 quick_check 和独立 foreign_key_check；新建/升级在提交前执行完整 integrity_check 和 foreign_key_check。失败抛出故障，不返回空数据，也不回退旧文件写入。WAL 在初始化锁内启用并读回确认。每日维护调度、运行时故障提示与生产写入停用策略由后续批次接入。

## 备份与离线恢复

升级已有版本前执行 BackupDatabase，失败阻断迁移。备份先写 `backups/.pending-<uuid>/userdata.db`，完整性/外键检查、checkpoint 并转为 DELETE journal，关闭连接后计算 SHA-256，写 manifest，再原子发布整个目录。manifest 记录数据库身份、schema、两个修订、注入时钟 UTC 时刻及备份毫秒耗时。只在成功发布后保留最近 3 个升级前版本、7 个每日版本；失败不删除已有成功备份。旧格式原件包由批次 2 实现，与这些数据库备份分开。

`SqliteDatabaseBackup.CreateDailyBackupAsync` 是未调度的维护原语：按注入时钟 UTC 日期，首次有 data_revision 变化时备份，同日不重复，无变化不备份。调用者需安排在无完成写入待处理的空隙；BackupDatabase 会阻挡其他连接写入。批次 6 再启用维护调度，不在本批创建后台计时器。

`RestoreAsync(backupDirectory)` **只允许离线调用**：调用者必须先停止所有数据库使用者。先核对哈希、完整性、外键和 metadata，再复制到独立候选库、生成新 database_id、检查并关闭连接，保留原 DB/WAL/SHM 到 `recovery-originals/<uuid>/`，以 File.Replace 原子替换主库；替换前失败还原已移动的伴随文件。原库现场不轮换删除。恢复完成后再次运行初始化器以升级旧版备份；恢复不导入兼容投影、不承诺恢复备份之后的增量。恢复工具及进程停止编排属于后续批次。进程若在离线恢复移动伴随文件时中止，保留现场供显式恢复，不自动启动生产写入。

## 验证

`SqliteStorageTests` 使用真实临时磁盘、独立连接及屏障覆盖建库、升级、事务回滚、高版本拒写、Unicode 键、约束、备份恢复、保留策略和并发初始化。测试时钟固定，不依赖实际日期。

`scripts/sqlite-publish-smoke.ps1` 在 Windows 发布 ConsoleHost win-x64 的框架依赖和自包含布局，先检查生产产物及 desktop 子目录的 `e_sqlite3.dll` 与 managed assembly（原生库哈希必须一致），再仅加入测试 driver 的四个入口文件，使用生产依赖执行真实 SQL；4 个子进程由命名事件/信号量同时放行初始化同一目录，核对唯一 database_id 与主库。driver 引用 ConsoleHost 以保持完整依赖图一致，仅入口文件进入临时冒烟目录；不进入生产发布项目、DI 或 CLI。CI 保留原有全量测试和隔离后台运行验证。
