# 开发完成后的验证与部署 Workflow

本文档定义 GameHelper 每次功能开发完成后的**固定验证与部署流程**。所有涉及代码改动的任务在收尾前必须完成本流程（或明确说明无法完成的原因）。

## 核心原则

1. **绝不停止或干扰用户正在运行的 GameHelper 实例。** 用户可能随时开着监控；验证必须通过"独立测试实例"完成，而不是重启用户实例。
2. **测试实例必须与用户数据隔离。** 测试实例加载数据走沙盒目录，不读不写真实 `%AppData%\GameHelper`。
3. **测试实例不能与主实例争抢单实例互斥。** 通过环境变量禁用互斥，允许两者共存。
4. **开发完成后必须部署到本机发布目录**，让用户下一次启动快捷方式时直接生效；若发布目录被运行实例锁定，执行部分部署并保留新产物等待重试。

## 测试实例隔离机制

| 环境变量 | 作用 |
| --- | --- |
| `GAMEHELPER_DATA_DIR` | 将整个数据目录（`config.yml`、`playtime.csv` 等）重定向到指定目录。实现见 `GameHelper.Core/Utilities/AppDataPath.cs`。 |
| `GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE` | 设为 `1` 时跳过全局单实例互斥，允许测试实例与用户正在运行的主实例共存。 |

测试实例启动时同时设置这两个变量；数据沙盒目录在验证结束后删除。

## 固定流程

### 1. 构建与测试

```powershell
dotnet build GameHelper.sln
dotnet test GameHelper.sln
```

两者必须全绿（既有 Skip 项除外）。

### 2. 发布 + 实机验证 + 部署（一键脚本）

```powershell
powershell -File scripts\verify-on-machine.ps1
```

脚本执行三步：

1. **发布**：`dotnet publish` 到独立临时目录（不会被运行实例锁定）。
2. **实机验证**：以隔离环境变量启动发布产物（独立测试实例），运行 `validate-config`、`config list` 冒烟断言。
3. **部署**：把新产物复制到快捷方式指向的 `GameHelper.ConsoleHost\bin\Release\net8.0-windows\win-x64\publish\`。
   - 无实例运行 → 完整部署成功。
   - 实例运行中、文件被锁 → **部分部署**：未锁文件已更新，锁定文件保留旧版；脚本保留新产物路径，提示用户关闭实例后重跑：

```powershell
powershell -File scripts\verify-on-machine.ps1 -SkipVerify
```

可选参数：`-SkipVerify` 跳过实机验证（仅部署）；`-SkipDeploy` 只发布+验证，不部署。

### 3. 端到端回归测试

`GameHelper.Tests\EndToEnd\PublishedExeEndToEndTests.cs` 在发布产物存在时自动执行：
以独立测试实例 + 沙盒数据目录运行真实 exe，验证配置校验、统计数据读取、与主实例共存。
若发布产物不存在则跳过（因此 workflow 中应先跑第 2 步再跑测试）。

### 4. 用户生效确认

部署成功（或用户关闭实例后补跑 `-SkipVerify` 完成）后，用户下一次点击快捷方式启动即运行新版本。**不要主动要求用户重启实例**，由用户自行决定时机。

## 功能级验证要求

除上述通用流程外，涉及用户可见界面的改动（如交互式 Shell 渲染）还应：

- 用沙盒数据目录 + `--config` 参数或预置 `playtime.csv` 驱动真实渲染路径，人工确认输出版面。
- 保留或新增对应的自动化测试断言，确保后续回归有安全网。