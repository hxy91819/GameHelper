# 史诗 PRD：迁移到基于完整路径的匹配（混合模式）

## 1. 目标和背景

**史诗目标：** 1. 优先使用 `ExecutablePath`（完整路径）进行精确匹配，以解决歧义。2. 对*没有* `ExecutablePath` 的配置，使用增强的回退逻辑（优先使用 `FileVersionInfo`，其次使用模糊匹配）来匹配 `ExecutableName`。3. 所有历史数据 通过 `DataKey` 进行关联。4. 支持通过拖放添加新游戏。

**明确推迟：** 解决技术债务（性能优化）。我们将继续监听所有进程启动事件，并在 `GameAutomationService` 中进行过滤。

---

## 2. 史诗和故事

### 史诗 1：混合匹配与 UI 增强

**史诗描述：** 彻底重构匹配逻辑，以支持精确路径、元数据和模糊匹配，同时更新 UI 以支持拖放和新的配置模型。

**故事 1.1：更新数据模型和配置加载（混合模式）**
* **作为** 架构师,
* **我想要** 修改 `GameConfig` 模型 以支持可选的 `ExecutablePath` 和必需的 `DataKey`,
* **以便** 配置系统能同时处理新（路径）旧（名称）格式，并确保数据关联 的完整性。
* **验收标准：**
    1.  `GameConfig.cs` 必须包含：`DataKey` (string, 必需), `ExecutablePath` (string, 可选), `ExecutableName` (string, 可选), `DisplayName` (string, 可选)。
    2.  `YamlConfigProvider.cs` 加载时*必须*要求 `DataKey` 存在，否则抛出错误。
    3.  `YamlConfigProvider.cs` 必须能成功加载同时包含 `ExecutablePath` 和 `ExecutableName` 的条目。
    4.  `YamlConfigProvider.cs` 加载*只*包含 `ExecutableName`（没有 `ExecutablePath`）的条目时，必须成功加载，并记录一个“警告”日志。

**故事 1.2：更新核心匹配逻辑（混合策略）**
* **作为** 系统,
* **我想要** 在 `GameAutomationService` 中实现一个 2 级匹配策略,
* **以便** 优先使用精确路径匹配，同时为旧配置保留增强的（元数据/模糊）名称匹配。
* **验收标准：**
    1.  服务启动时，`GameConfig` 列表必须被分成 `PathBasedConfigs` 和 `NameBasedConfigs` 两个列表。
    2.  **第 1 级（路径匹配）：** 当进程事件到达时，必须*首先*尝试将其 `exePath` 与 `PathBasedConfigs` 列表进行精确（不区分大小写）匹配。
    3.  **第 2 级（回退匹配）：** 仅当第 1 级匹配失败时，才尝试第 2 级。
    4.  第 2 级必须尝试从 `exePath` 获取 `FileVersionInfo.ProductName`。
    5.  第 2 级必须使用 `FuzzySharp` 将 `ProductName`（或 `exeName`）与 `NameBasedConfigs` 列表中的 `config.ExecutableName` 进行比较。
    6.  如果模糊匹配（例如 `Fuzz.Ratio` > 80）成功，则匹配该配置。
    7.  所有旧的（非元数据/模糊）前缀或词干匹配逻辑必须被移除。

**故事 1.3：更新 UI 以支持拖放和新配置**
* **作为** 用户,
* **我想要** 能够通过拖放 EXE 或 LNK 文件来添加新游戏,
* **以便** 快速配置新游戏并自动提取其元数据。
* **验收标准：**
    1.  `ConsoleHost` 必须能接受文件拖放。
    2.  如果拖放的是 LNK（快捷方式），系统必须能解析出目标的 EXE 路径。
    3.  系统必须使用 `FileVersionInfo.GetVersionInfo(path)` 尝试提取 `ProductName`。
    4.  必须提示用户确认自动提取的 `ProductName` 作为 `DataKey`，并将 EXE 路径作为 `ExecutablePath`。
    5.  `InteractiveShell.cs` 中的“添加游戏”命令必须更新，以提示输入 `ExecutablePath` 和 `DataKey`。
    6.  （可选）必须提供一个“编辑游戏”命令，允许为现有配置添加 `ExecutablePath`。

**故事 1.4：确保数据服务的数据关联**
* **作为** 用户,
* **我想要** 无论游戏是通过路径还是名称匹配的，我的游戏时长都能正确记录和报告,
* **以便** 保持我的历史数据 连续性。
* **验收标准：**
    1.  `CsvBackedPlayTimeService.cs` 在记录新会话时，必须使用匹配到的 `GameConfig` 上的 `DataKey` 字段作为 `GameName` 写入 `playtime.csv`。
    2.  `stats` 命令 在聚合 `playtime.csv` 时，必须按 `DataKey` 分组。
    3.  `stats` 命令 在显示时，必须优先使用 `GameConfig.DisplayName`（如果存在），其次使用 `DataKey` 作为游戏名称。

**故事 1.5：启动自检与一次性修复向导**
* **作为** 产品团队,
* **我想要** 在应用启动时检测并引导修复缺失 `DataKey` 的历史配置,
* **以便** 用户能顺利迁移到新的配置模型且避免运行期异常。
* **验收标准：**
    1.  应用启动加载配置后，必须扫描并统计缺失 `DataKey` 的条目。
    2.  若存在缺失项，CLI 或交互式 Shell 必须暂停正常流程，启动修复向导，允许用户为每个条目确认或输入 `DataKey`（默认建议可基于 `ExecutableName`）。
    3.  修复完成后，系统必须在用户确认下回写更新后的配置，并记录修复结果日志。
    4.  用户可以选择跳过修复；系统须警示“缺少 `DataKey` 将导致匹配失败”，并提供继续（功能受限）或退出的选项。
    5.  必须添加单元测试覆盖有缺失、已修复、用户跳过等场景，确保流程稳定。