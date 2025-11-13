# 9. Testing Status

## 9.1. Current Test Coverage

  * **单元测试**: 使用 xUnit 和 Moq 对核心逻辑和服务进行测试。
  * **集成测试**: `ProcessMonitorIntegrationTests.cs` 等文件测试了监控器与系统事件的集成。
  * **端到端测试**: `EndToEndProcessMonitoringTests.cs` 验证了从进程启动到时间记录的完整流程。
  * **已知差距 (已更新)**:
      * 交互式命令行的测试依赖于输入模拟，覆盖范围有限且较为脆弱。
      * **新差距**: 需要为 `GameAutomationService` 中新的 2 级混合匹配策略（L1 路径 和 L2 元数据/模糊回退）添加专门的单元测试。
      * **新差距**: 需要为新的拖放添加游戏功能 添加测试。

## 9.2. Running Tests

```bash
dotnet test
```

## 9.3. 旧功能回归策略

* **进程监控回归**: 在 Windows 11 环境中分别以管理员和普通权限运行 `dotnet run -- monitor`，观察 WMI 与 ETW 的自动降级路径，确保历史版本依赖的 WMI 监听仍可用。
* **配置与数据兼容性**: 使用历史 `config.yml` 与 `playtime.csv` 进行回放测试：执行 `dotnet run -- config list` 核对 `DataKey` 映射是否保持；运行一次模拟游戏会话验证 CSV 追加不破坏旧记录。
* **CLI/交互功能**: 复用 `GameHelper.Tests` 的交互式测试套件，并补充一次手动脚本（参考 `docs/history/CLI_Manual_zh.md`）执行既有命令序列，确认输出未发生破坏性变更。
* **回归节奏**: 在每个功能迭代的冲刺结束前，执行上述回归检查并记录结果。如发现异常，按照 8.3 节的回滚路径恢复后，再推进修复。

### 9.3.1. 签字与记录流程

1.  QA 负责人复核 9.3 节的回归脚本执行结果，填写 `docs/history/Regression_Signoff_Log_zh.md` 中当期条目，并在日志中附上测试数据或脚本链接。
2.  架构负责人确认关键链路（配置、匹配、数据、CLI）均按日志通过后，在同一条目下签名并备注是否需要额外补救措施。
3.  PO 根据日志结论决定是否允许发布日期进入发布流程；若驳回，需在日志附带原因并分配后续修复负责人。
4.  所有签字完整的条目需同步摘要到 `docs/history/Bug_Fix_Summary_zh.md`，确保历史版本知识库可追溯。
