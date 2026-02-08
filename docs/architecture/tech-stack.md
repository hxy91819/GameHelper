# Tech Stack

> 维护者：架构组（Winston） · 最近更新：2025-11-10

本文件记录 GameHelper 目前采用的技术栈与关键依赖，供开发、QA、运维快速查阅。任何依赖版本或平台策略的调整，都必须同步更新此文档与 `Directory.Build.props`。

## 1. Runtime & Platform

| 组件 | 版本/平台 | 说明 |
| --- | --- | --- |
| .NET 运行时 | **.NET 8.0** (`net8.0-windows`) | 强制目标框架。核心功能依赖 Windows API（WMI/ETW），非 Windows 环境仅支持 `--monitor-dry-run` 模式。|
| 操作系统 | Windows 11 (64-bit) | 开发、测试、发布默认平台；ETW 监控需管理员权限。|
| 编译工具链 | `dotnet sdk 8.0.x` | VS 2022、VS Code + C# 扩展兼容。|

## 2. Application Framework & Libraries

| 类别 | 名称 | 当前版本 | 用途 |
| --- | --- | --- | --- |
| 依赖注入 & 宿主 | `Microsoft.Extensions.Hosting` | 9.0.8 | 构建通用 Host、注册后台服务。|
| 控制台 UI | `Spectre.Console` | 0.47.0 | 渲染 CLI、交互式 Shell。|
| 配置解析 | `YamlDotNet` | 13.7.1 | 解析 `config.yml`，兼容旧字段。|
| 进程监控 (ETW) | `Microsoft.Diagnostics.Tracing.TraceEvent` | 3.1.8 | 低延迟内核事件监控。|
| 进程监控 (WMI) | `System.Management` | 8.0.0 | 兼容性更高的 WMI 事件订阅。|
| 模糊匹配 | `FuzzySharp` | 2.0.2 | L2 回退匹配：`ExecutableName` + `ProductName`。|
| 测试框架 | `xUnit` | 2.9.2 | 单元与集成测试。|
| Mock 框架 | `Moq` | 4.20.72 | 服务与外部依赖模拟。|

## 3. Storage & Persistence

| 场景 | 技术 | 说明 |
| --- | --- | --- |
| 配置存储 | YAML (`config.yml`) | `YamlConfigProvider` 负责解析与验证，支持路径/名称混合匹配字段。|
| 游玩时长 | CSV (`playtime.csv`) | `CsvBackedPlayTimeService` 和 `FileBackedPlayTimeService` 提供写入/读取与 JSON 迁移。|
| 临时数据/缓存 | 本地文件系统 | 暂无额外数据库；未来 HDR/自动化扩展再评估。|

## 4. Tooling & Automation

- **源代码管理**：Git + GitHub。CI/CD 通过 `.github/workflows/release.yml`。
- **开发环境**：VS 2022 / VS Code，推荐启用 C# Dev Kit 与 NuGet Manager。
- **静态分析**：暂无强制集成，建议启用 Roslyn 分析器；如加入需同步更新本节。
- **打包发布**：`dotnet publish -c Release -r win-x64 --self-contained true`（详见 `README.md`）。

## 5. 依赖版本管理策略

1. **锁定来源**：全部通过官方 NuGet 源获取，禁止直接引用临时包或私有源。
2. **升级流程**：
   - 本地验证：`dotnet restore && dotnet build && dotnet test`。
   - Windows 11 实机验证：执行 `dotnet run -- monitor`，确认 ETW/WMI 事件与 CSV 写入正常。
   - 更新文档：同步修改 `Directory.Build.props`、本文件、`docs/history/Bug_Fix_Summary_zh.md`。
3. **冲突处理**：优先升级 `TraceEvent` 以解决绑定冲突；如需降级，必须记录于 `docs/architecture/7-technical-debt-and-known-issues.md`。

## 6. 兼容性检查清单

- [ ] `config.yml` 旧字段可读，可写入包含 `DataKey` 的新条目。
- [ ] `playtime.csv` 追加记录后保持旧版本兼容。
- [ ] WMI 降级路径在非管理员权限仍可运行。
- [ ] CLI/Shell 在 Spectre Console 升级后输出无 ANSI 破损。
- [ ] 引入新的字符串/本地化库时验证不影响 FuzzySharp。

> 若需新增库或服务，请先提交架构评审，说明对现有兼容性、部署和测试流程的影响。
