# SQLite 会话与在途恢复（ADR-0004 批次 3–4）

ConsoleHost 首次执行 `monitor`、`stats`、`sync` 或本地文件导入时，在开放监控、IPC、上传前初始化
`SessionStorage`。主库位置复用 `AppDataPath`：`GAMEHELPER_DATA_DIR` 是 base，数据库仍在其
`GameHelper/userdata.db` 下。没有主库时调用批次 2 的候选导入、旧读路径逐项统计与字节校验、
完整性/FK 检查、checkpoint 和原子改名；有激活事实的库直接沿用，不因外部诊断文件缺失而重导。

## 首次激活与回退

生产调用 `PrepareAsync(..., sessionsOnly: true)`。manifest 标明此模式；正式历史、在途文件与当前配置的
六种 Desktop 文档全部先备份原始字节并严格解码。Desktop 的无法无损表达值同样阻断激活，
但它们尚不写入关系侧表、不登记已导入 scope，也不输出侧文档投影，防止覆盖批次 5 前的旧写入者。
批次 5 第一次打开配置时，仍须按当时最新文档执行 scope 导入。

ConsoleHost 现有单实例互斥排除旧后台；启动前检查当前配置的 Desktop 实例互斥体，旧游戏库仍开着时，
不捕获来源，继续旧存储并提示关闭游戏库再重启后台。新版 Desktop 先等待后台存储/IPC 就绪，再打开侧文档
和登记 UI 实例。来源捕获、校验及激活继续核对每份源哈希，旧进程不认识 SQLite 协调锁，不能用锁代替停机。
仅处理实际打开的配置，不扫描其他配置。

激活前任何失败：记录具体原因、保留候选和原件、使用 CSV/JSON 继续运行，托盘显示原因。
回退服务的构造不执行旧 JSON→CSV 迁移，因此初始化失败不改变源文件；后续真实结束会话时才执行旧写入行为。
激活改名后即使响应或诊断文件写入中断，也只沿用已激活库。已有主库无激活记录、版本过高、完整性错误
均拒绝继续，绝不悄悄写回 CSV。运行中库写失败保留固定结束时间的待补录状态，并记录日志/托盘存储故障。

## 稳定身份和事务

第一条真实进程观察分配 UUID。同 DataKey 的多个进程共享 ID、有效起点与启动来源；休眠偏移只改起点。
SQLite 在途读路径返回 ID；旧 JSON 投影省略 ID 和数据库 revision，保持旧 codec 字节格式。
旧快照导入仍按 DataKey/起点/固定终点分组；与既有 CSV 匹配时保留最早 sequence 的 ID 和其余合法重复行。
未知启动来源在库中仍为 unknown，恢复 HDR 时沿用旧版把未知视作库内的资格规则。

`SqliteSessionStore.Complete` 在提前取得写锁的单一事务中：按 ID 判断历史是否已提交；未提交则插入历史，
递增 history/data revision；删除该 ID 的在途进程和会话；更新快照 revision；将兼容目标标脏。
提交前失败全部回滚；提交后响应丢失，重试相同 ID 不重复插入、不重复递增历史修订。
快照整体替换包含 HDR、待启动截止时间和进程身份；已有历史 ID 不会被旧内存快照复活。
快照写入核对存储实例在读取/上次提交时持有的 revision，拒绝陈旧写入者。

保留 PID+创建时间+executable 核验、每十秒与状态变化持久化、未知终点按最后存活快照封口并限十二小时、
连续恢复和固定失败终点。已有 `RecoveryEndAt` 是已知终点，十五小时等长局不裁剪。恢复不重放启动/退出动作。后台停止仍即时完成活跃会话。

## 锁边界的有意选择

保留 `UpdateLock → _companionLock → _stateLock`。锁内存储例外仅限会话/在途短事务；
完整历史读取、CSV 生成、文件比较/刷盘、目录协调锁与投影标记事务全部在独立串行执行器运行。
业务提交后只向队列投递请求，不同步执行投影；重复请求合并，显式 flush 在该执行器中串行化。
统计和 sync 不等待投影；正常 Stop 释放全部自动化锁后才排空投影，存储 Dispose 关闭并 drain 执行器。
执行器不取得自动化锁，故不存在等待它时反向进入状态锁的路径；完整投影耗时不计入状态锁内例外。

事件/定时器在进入时核对捕获的 generation；Stop 在同一锁下失效代际。事务与结果仍处于该执行边界内。
进程事件的时间在等待状态锁/数据库前捕获，开始、结束与快照使用此固定时间；数据库等待不扩充已知会话终点。
每个事件建立一个单调时钟截止时间，完成写入及失败后的待补录快照共用最多五秒的争用预算，不分层重新计时。

存储适配器不取得自动化锁，也不在事务中调用生产回调、进程探测、通知、网络或投影。
每次操作创建私有短连接，以 provider 管理的 deferred 事务承载一次原生 SQLite no-op 写入，提前获取 writer；
其效果等价于立即取得写意图，且不会修改业务修订。原生 busy timeout 设为零，BUSY/LOCKED 由一个事件级循环处理，
每次最多退避 10 ms，实际唤醒后重新检查同一单调截止时间（预留 100 ms 做回滚/返回），避免 Windows 睡眠粒度
使 SQLite 按名义睡眠累计的超时越界，也避免 provider 整秒重试另加预算；预算耗尽只允许一次不等待的尝试，失败保留内存补录状态。
没有事件 scope 的独立存储调用仍各自采用五秒上限。文件 I/O、设备调用和整个 Stop 不承诺五秒完成。

## 兼容窗口、统计和 sync

首个 SQLite 版本在启动、每次库提交时投递，正常停止在锁外排空完整 `playtime.csv` 和旧在途 JSON 投影。
每个目标临时文件+原子替换，跨目标不承诺原子性；失败留 dirty/failure，不撤销库提交，也不让会话重写。
内容相同的文件不替换，因此快照心跳不改变 CSV mtime，不触发新的 sync 上传。

ConsoleHost 统计/档案/报告经基础 SQLite snapshot adapter 读取同一短读事务的完整历史，在内存计算。
生产兼容 CSV 保留旧读取器已支持的 UTC `Z`/Local offset 后缀，避免原 UTC 时间变成本地无时区时间，
从而改变跨日统计和 sync 字节；无时区时间、字段、秒精度、排序和编码不变。离线降级 receipt 记录此格式，
再升级从 receipt 选择相同投影语义。批次 2 的显式旧 writer 固化模式默认保持不变。

sync 从同一 SQLite 历史快照通过原 `StatsReportBuilder` 导出 daily.csv、README 和可选 raw CSV；
保留原格式、内容指纹、间隔、退避、受管文件清单及关闭 raw 时的远端删除。
脏判断使用 `database_id + history_revision` 和配置 mtime；成功只确认实际导出的修订，上传中的新会话不会被误确认。
快照读取失败返回 ReadFailed，metadata 读取失败抛出并由 sync 记录失败；均不更新成功书签、不上传空结果。
兼容投影故障只影响旧版交接，已提交历史仍可读可同步。未激活时保持旧文件读取及 mtime 门控。
侧文档读写、评分与自动备份调度尚未切换。

## 统计读取与 WAL checkpoint

每个读取自建私有连接，以 deferred 读事务先读 storage_meta，再按 history_keys.first_sequence、
play_sessions.sequence 读取历史；修订与会话来自同一视图。事务结束并释放连接后才做 Core 聚合、Markdown 渲染或上传，
不进入自动化状态锁，不持有目录协调锁，也不等待兼容投影。保留各行原始拼写、Kind、分钟数与重复记录，
按首次分组与组内顺序还原，维持 raw 同开始时间的稳定排序。档案的未来／逆序过滤不用于 sync。
本阶段只有历史已迁库，Desktop 手帐仍按原文件边界工作；批次 5 再扩展当前 scope 的同事务报告数据。

WAL 中读者固定自己的 end mark，长读事务不阻止另一个连接提交会话。
SQLite 默认自动 checkpoint（通常阈值 1000 页）可能在提交时做可完成的工作；活跃读者可能阻止回收全部 WAL 帧。
本批不新增后台维护或强制 checkpoint，不在读路径执行 TRUNCATE/RESTART。
PASSIVE checkpoint 不等待读者，允许暂时有未回收帧；读者释放后维护可再 checkpoint，TRUNCATE 才能清空 WAL。
长时间持有读事务会增加 WAL 大小，所以只在取数阶段保留事务，不跨越聚合和网络。

降级前停止后台和游戏库，运行：

```powershell
GameHelper.ConsoleHost.exe storage prepare-downgrade --config <config.yml>
# 成功输出降级 manifest 路径；失败不得启动旧版。
GameHelper.ConsoleHost.exe storage reupgrade <downgrade-manifest.json> --config <config.yml>
```

准备降级调用已有固定修订完整投影、哈希/旧统计检查和整库封存原语，包含当前旧快照，旧版可接管正在运行的游戏。
再升级在离线入口核对旧 CSV 有序前缀及 occurrence，仅追加旧版新增行，不覆盖原 ID/Kind/来源。
手工覆盖安装未经过准备降级不提供自动合并保证。

## 验证

`SqliteSessionStorageTests` 在真实临时磁盘库验证提交前后故障、重复停止、多进程、PID 复用、休眠、
两次恢复、固定失败终点与运行中心跳重试、HDR/待启动、陈旧 revision、激活前后故障、原件字节、
不可表达文档阻断、激活沿用、旧版接管和投影失败时直接从库上传。时间使用注入时钟与手动定时器。

`sqlite-publish-smoke.ps1` 在两种 win-x64 发布布局强杀停在真实提交前/后屏障的子进程，重新打开 WAL 检查
历史/在途二者原子性，再重复恢复两次检查同一 ID 仅一行、历史修订仅增一次。
`verify-on-machine.ps1 -SkipDeploy -VerifyRuntime` 先放入旧 CSV，验证迁移备份哈希、主库激活、
后台无控制台、监控就绪与库 IPC 返回导入的 17 分钟；保持用户实例与数据隔离。

审查回归另外覆盖：十五小时已知终点失败后重启仍记 900 分钟；真实 writer 争用时一次 stop 的完成/补录共享五秒，
注入的游戏时钟推进不改变原事件终点；文件投影屏障不阻塞状态查询；诊断标记权限错误不阻止监控；
ReadFailed（有/无记录）均显示同步失败、记录 LastError 且不调用上传。

`SqliteStatisticsTests` 固化比较 KPI、年度／季度汇总、sync 三种文件完整 UTF-8 字节，验证跨时区、重复和同分顺序；屏障固定长读快照，同时完成会话并验证 checkpoint 释放。`SqliteSyncTests` 验证上传期间追加、历史书签兼容、心跳／checkpoint 无脏判、raw 关闭删除和读取失败保留成功书签。
