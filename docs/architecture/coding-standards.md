# Coding Standards

> 维护者：架构组（Winston） · 最近更新：2025-11-10

本文件描述 GameHelper 仓库的代码约定，旨在保证多团队（dev / QA / AI 代理）协作时的统一性。所有提交都必须遵循以下准则；如需新增例外，请在 PR 中记录并更新本文。

## 1. 通用原则

1. **保持单一职责**：类型和方法应关注单一任务，必要时拆分为私有 Helper 或扩展方法。
2. **显式优于隐式**：偏好显式依赖注入、显式访问修饰符、明确的控制流。
3. **防止回归**：任何破坏性修改需附带针对性的单元/集成测试。
4. **跨平台友好**：除 Windows 专属代码外，避免依赖平台特性；必要时提供 `NoOp` 或 Feature Flag。

## 2. C# 语言风格

| 约定 | 说明 | 示例 |
| --- | --- | --- |
| 文件级命名空间 | 使用 `namespace Foo.Bar;` 格式，避免嵌套块。 | `namespace GameHelper.Core.Services;` |
| 成员可访问性 | 所有成员均显式声明 (`public`, `internal`, `private`, `protected`)。 | `public sealed class GameAutomationService` |
| `var` 使用 | 当类型在初始化表达式中可直观得知时使用 `var`，否则写出显式类型。 | `var logger = _loggerFactory.CreateLogger(...);` |
| 表达式成员 | 仅在提升可读性时使用；复杂逻辑保持代码块形式。 | `public override string ToString() => _cachedValue;` |
| 空值处理 | 使用空合并、卫语句和参数验证；禁止吞掉异常。 | `ArgumentNullException.ThrowIfNull(options);` |
| 插值字符串 | 优先使用 `$"..."`，并注意日志模板占位符与结构化日志兼容。 | `_logger.LogInformation("Processing {Game}", gameId);` |

## 3. 命名约定

- **类型**：`PascalCase`；接口以 `I` 前缀（`IProcessMonitor`）。
- **方法/属性**：`PascalCase`（`StartMonitoring`、`DataKey`）。
- **字段**：私有只读字段用 `_camelCase`；静态字段以 `s_` 前缀。
- **局部变量/参数**：`camelCase`，避免简写（除常用缩写如 `id`, `url`）。
- **异步方法**：以 `Async` 结尾（`LoadConfigAsync`）。
- **测试**：`MethodUnderTest_State_ExpectedOutcome` 命名模式。

## 4. 结构化代码布局

1. 文件头顺序：`using` -> 命名空间 -> 类型。
2. `using` 分组：系统命名空间在前，第三方在后，按字母排序；禁止使用文件作用域内 `global using`。
3. 类型内部成员顺序：
   1. 常量 / 静态字段
   2. 字段
   3. 构造函数
   4. 公共属性 / 方法
   5. 受保护成员
   6. 私有方法
4. 单个文件仅定义一个公开类型；内部辅助类型使用嵌套类或独立文件。

## 5. 错误处理与日志

- **卫语句优先**：遇到无效输入时尽早返回或抛出异常。
- **异常类型**：使用框架标准异常（`ArgumentException`, `InvalidOperationException` 等）；需要自定义异常时继承 `Exception` 并添加序列化构造函数。
- **日志级别**：
  - `Trace`/`Debug`：诊断信息。
  - `Information`：状态流或关键动作。
  - `Warning`：可恢复异常。
  - `Error`：需要人工干预的失败。
- 记录异常时使用 `_logger.LogError(ex, "message {Context}", value);`，保留结构化上下文。

## 6. 异步与多线程

1. 所有 I/O 操作提供 `async` 版本，避免在同步方法中调用 `Result`/`Wait()`。
2. 在库代码中传播 `CancellationToken`，方法签名包含可选参数 `CancellationToken cancellationToken = default`。
3. 使用 `ConfigureAwait(false)` 仅限库内部调用；应用层不需要。
4. 避免在后台线程中更新 Spectre.Console UI；使用提供的异步 API 或主线程调度器。

## 7. 测试与可测性

- **单元测试**：位于 `GameHelper.Tests`，与被测类型命名对应。
- **Mock 策略**：优先模拟接口；如需文件系统/时间依赖，使用 Provider/Clock 抽象。
- **断言**：使用 FluentAssertions 或 xUnit 原生断言；禁止多个 `Assert.True` 混合无描述。
- **数据驱动**：充分利用 `[Theory]` + `MemberData` 复用用例。
- **新增功能**：必须至少提供一条正向路径测试；Bug 修复需包含回归测试。

## 8. 文档与注释

1. 公共 API（接口、公开类）添加 XML 注释描述用途和参数含义。
2. 复杂逻辑前添加简短块注释说明意图；禁止冗余注释如 "// increment i"。
3. 对外行为变更时，更新 `docs/history/Bug_Fix_Summary_zh.md` 与相关架构/故事文档。
4. README 或 CLI 使用方式变化需同步 `docs/history/CLI_Manual_zh.md`。

## 9. 代码评审流程

1. 先在本地运行：
   ```powershell
   dotnet build GameHelper.sln
   dotnet test GameHelper.sln
   ```
   > 遵循用户规则：新增功能只运行相关测试时需在 PR 中说明跳过全量测试的原因。
2. PR 描述需包含：变更摘要、影响范围、测试命令、文档更新情况。
3. 评审者确认：
   - 依赖注入、异常处理、日志符合规范。
   - 引入新依赖时已更新 `tech-stack.md`、`Directory.Build.props`。

## 10. 例外管理

- 例外必须在 PR 中列明，并在合并前得到架构组批准。
- 合并后将例外记录在 `docs/architecture/7-technical-debt-and-known-issues.md`，并标注跟踪工单。
