# 旧 codec 固化样本

基线：`origin/main` 的批次 1 提交 `0aac8d5`（此时所有生产读写仍为 CSV/JSON）。
`Generate.cs.txt` 是一次性生成程序，调用当时的 `PlaytimeCsvCodec`、`CsvBackedPlayTimeService`、
`FilePlaytimeSnapshotProvider`、`StatsReportBuilder`、`JsonInFlightSessionStore`，并链接 Desktop 的
`DesktopDocumentStore`、`LibraryJournalStore`、`LibraryGenreStore`、`LibraryDraftStore`、`LibraryScanStore`。
artwork/theme 直接使用各旧 store 相同的 `DesktopDocumentStore.Write` DTO 形状。

生成器在独立临时 net8.0-windows console 工程运行，AssemblyName 为 GameHelper.Tests，引用旧
Infrastructure，并链接上述 Desktop 文件。没有引用新导入器或投影器。生成时区为 UTC；统计时点
固定为 2025-04-01 12:00。`*.unix.bin` 是旧实现的原始输出，`*.windows.bin` 按旧实现的 Windows
平台换行规则转换（样本内部换行也明确为 CRLF）。JSON 不作换行转换；inflight 包含旧写法的 BOM。
`.bin` 禁用 git 文本转换。测试运行时只读取这些已提交字节，不执行生成器。

CSV 样本含 BOM、中文、逗号、引号、跨行字段、大小写归组、合法重复行、保存分钟数与时间差不等、
UTC/无时区、跨日跨年、零分钟、逆序、坏日期、负分钟、错误引号、未闭合引号。
legacy JSON 另含旧投影忽略的 Duration/unknown 字段及毫秒；保留原件，投影不包含这些字段。
六类 Desktop 期望文件也用作合法输入；journal 另有含非法状态和大小写覆盖的输入。
完整草稿包含 Baseline、启动选项、动作、DS4 与倍速热键；inflight 包含多进程、空身份、
不同启动来源、待重试结束时间、HDR 与待启动截止时间。
