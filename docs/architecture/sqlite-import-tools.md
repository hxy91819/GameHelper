# SQLite 旧数据导入与兼容工具（ADR-0004 批次 2）

这些 API 在批次 2 作为离线能力实现；批次 3 已将历史和快照接入生产自动激活，
并提供 `storage prepare-downgrade` / `storage reupgrade` 离线 CLI。Desktop 侧文档仍沿用旧路径。
生产选择、回退、锁边界与兼容窗口见[会话存储](./sqlite-session-storage.md)。

## 显式调用边界

`SqliteLegacyMigration(factory, clock, configs)` 使用调用方提供的固定时钟、时区和同份 GameConfig 做比较。
调用方须先停止 ConsoleHost/Desktop 等旧格式写入者。所有维护入口取得与初始化器相同的数据目录锁；
锁不代替停止旧版进程（旧版不认识这个锁）。只传实际已打开的配置路径，不扫描磁盘。

- `PrepareAsync(configPaths, batchSize)`：备份正式文件、发布不可变 manifest，再建立 `userdata.db.importing`。
  不修改源文件，不调用激活。原件包在 `migration-backups/<uuid>/`；缺失文件也进入清单，临时文件不进入。
  manifest 包含实际绝对路径、配置 scope、SHA-256、字节数、codec/程序版本。
- 来源清单未变时按 `import_records` 继续批次；同批会话、稳定 UUID、来源序号、诊断与计数在同一事务提交。
  重复内容的不同逻辑行保留不同 UUID。源变化时保留旧候选库至 `migration-backups/invalidated-*`，新清单重新导入。
- `ActivateAsync(manifestPath)` 是显式的离线原语：再次校验源与候选库，库内写激活事实，完整性/FK 检查、
  checkpoint、关闭连接/池、确认 WAL 已排空后无覆盖改名。已有不同主库则拒绝；重复调用同一激活仅补诊断文件。
  `migration-complete.json` 不是事实源，不因它缺失而重导。
- `ImportScopeAsync(configPath)` 仅为已有库补一个未知 scope；六文档、跳过原因、来源和完成状态在一个事务提交。
  已知 scope 不重导，共享历史不参与此入口。

CSV 优先于 legacy JSON，即使 CSV 为空或仅有坏行。坏 CSV 行按逻辑序号记录原因，未闭合引号也记入；
非空错表头、整份 JSON 损坏、正式来源不可读均阻断。legacy JSON 先复现旧 JSON→CSV 的秒精度、无 Kind
投影，不能保留旧迁移原本会丢掉的时区/毫秒到新统计中；原件仍完整备份。CSV 则保留原 Kind、时间差 ticks
与独立保存的分钟数，包括逆序、零分钟、重复行。

手帐使用旧逐项校验与大小写覆盖规则；类型导入保留旧 Read 的数组顺序和值，不擅自再 Normalize。
旧 store 会读取、但关系约束无法无损表达的文档（如 artwork 的 0 或大小写重名、重复类型、超六项/重复扫描目录、
缺 Baseline 的草稿）阻断并保留原件，不能静默归一化或当空文档成功。JSON `null` 依旧按旧空文档语义处理。

验证比较有序记录（含 DataKey 拼写、重复、Kind、ticks、保存分钟数），由此覆盖每游戏计数、总分钟、最近窗口、
零分钟、最早最晚与按结束日归档；另逐项比较 0/7/30/90 天档案和涉及年份的年度/四季度报告，复用原
StatsReportBuilder 比较含 raw CSV 的完整文件内容与指纹，核对每源接受/跳过数、六类文档字段和快照。
所有 SQLite 验证使用真实磁盘库。旧 codec 字节基线见 `GameHelper.Tests/Resources/LegacyUserData/README.md`。

## 完整投影与离线降级

`SqliteLegacyCompatibility.ReadProjection()` 从同一读事务生成整个旧 CSV、每 scope 六种 JSON 与在途快照。
所有业务值来自关系表；仅草稿使用版本化完整 payload。顺序列保留旧字典/列表顺序，按旧 codec 输出字段、
转义、平台换行、编码/BOM。不存在的快照投影为删除；已导入 scope 的缺失侧文档投影为旧格式默认空文档。

`ExportAsync()` 先记录各目标的固定 data/snapshot revision、哈希与 dirty，再逐文件原子替换。
成功后仅在库修订仍相同时清 dirty；失败留下错误与脏状态。跨文件输出不承诺原子性，重试会重建完整投影。

`PrepareDowngradeAsync()`：停止所有库/文件使用者后调用；输出固定修订、旧读路径验证、核对哈希，
使用 BackupDatabase 封存整个库（含评分等旧格式不可表达数据）与全部投影到 `downgrade-backups/<uuid>/`，
最后发布包含会话 ID/逐 occurrence 基线、scope 清单、库哈希与投影哈希的 manifest。未拿到成功 receipt
不能认为旧版已可接手。快照的旧恢复语义仍由旧版负责，不重放启动动作。

`ReupgradeAsync(downgradeManifestPath)`：使用同一封存身份的主库，先保存旧版最新文件到不可变
`reupgrade-backups/`，再核对 CSV 的完整已导出前缀；旧版是 append-only，前缀内合法重复行按 occurrence
绑定原 ID，前缀后的所有新 occurrence 均追加一次。保留原库的 Kind、UUID、启动来源、评分；最新侧数据与
快照、追加历史和合并标记同事务提交。重复执行同一 receipt/同源无副作用；已合并后来源再变化则要求新的
离线维护流程。删除、改写、重排、坏行、主库修订变化均停止自动合并并保留差异原件。

准备降级会验证旧 CSV 自身的统计兼容；CSV 本身无法表达 Kind/毫秒，因此再升级核对的是相同旧投影，
而不会把旧 CSV 的无时区值反向覆盖已知历史。不能从一份手工拷贝的 CSV 猜回已有会话 ID。

## 断点与测试

构造器可注入 `Action<string> checkpoint`，在 schema/批次/状态事务提交前后、原件包和候选库发布、
激活事实/诊断、投影 dirty/替换/clean、降级归档/manifest 和再升级备份/提交边界中断。它是同步故障注入点，
不安装调度器。测试枚举实际路径上的每次事件中断并重跑，包括同内容重复行、缺失源、源变代、scope 补导、
离线降级和再升级，不使用墙钟或 sleep。Windows CI 运行完整解决方案测试与现有发布/隔离实例探针。

批次 3 的 `sessionsOnly` 清单模式备份并严格解码当前配置侧文档，但只导入历史/在途，不登记 scope；
后续 scope 导入读取最新文件。运行时 `ExportSessions()` 只投影历史与在途，完整离线导出 API 保持原行为。

生产协调器与维护 CLI 构造兼容器时启用 `preserveHistoryKinds: true`：CSV 输出旧读取器支持的 `Z`/offset 后缀，
避免 UTC 历史的旧读统计变化。原始显式工具默认仍复现旧 writer 固化字节。降级 receipt 保存 `PreserveHistoryKinds`，
再升级按 receipt 选择格式，不要求调用者记住构造参数。
