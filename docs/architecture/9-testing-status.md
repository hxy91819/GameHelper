# 9. Testing Status

## 9.1. Current Test Coverage

  * **单元测试**: 使用 xUnit 和 Moq 对核心逻辑和服务进行测试。
  * **集成测试**: `ProcessMonitorIntegrationTests.cs` 等文件测试了监控器与系统事件的集成。
  * **端到端测试**: `EndToEndProcessMonitoringTests.cs` 验证了从进程启动到时间记录的完整流程。
  * **已知差距 (当前)**:
      * 交互式命令行测试仍依赖输入模拟，覆盖范围有限且较脆弱。
      * 部分历史 QA Gate 已归档，新增功能需要在当前实现基础上重新生成评审证据，而不是继续复用旧 Gate。

## 9.2. Running Tests

```bash
dotnet test GameHelper.sln
```

## 9.3. 旧功能回归策略

* **进程监控回归**: 在 Windows 11 环境中分别以管理员和普通权限运行 `dotnet run -- monitor`，观察 WMI 与 ETW 的自动降级路径，确保历史版本依赖的 WMI 监听仍可用。
* **配置与数据兼容性**: 使用历史 `config.yml` 与 `playtime.csv` 进行回放测试：执行 `dotnet run -- config list` 核对 `DataKey` 映射是否保持；运行一次模拟游戏会话验证 CSV 追加不破坏旧记录。
* **CLI/交互功能**: 复用 `GameHelper.Tests` 的交互式测试套件，并补充一次手动脚本（参考 `docs/guides/cli.md`）执行既有命令序列，确认输出未发生破坏性变更。
* **回归节奏**: 在每个功能迭代的冲刺结束前，执行上述回归检查并记录结果。如发现异常，按照 8.3 节的回滚路径恢复后，再推进修复。

### 9.3.1. 签字与记录流程

1.  QA 负责人复核 9.3 节的回归脚本执行结果，并将当期记录写入团队当前的发布记录或 PR 描述。
2.  架构负责人确认关键链路（配置、匹配、数据、CLI / WinUI）均通过后，再允许进入发布。
3.  需要保留的历史材料统一归档到 `docs/archives/qa/` 或 `docs/archives/history/`。
