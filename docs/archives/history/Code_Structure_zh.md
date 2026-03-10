# 代码结构说明

## 重构概览

为了提高代码的可维护性，我们将原本 600+ 行的 `Program.cs` 重构为更清晰的模块化结构。

## 新的文件结构

### Commands（命令处理）
- `Commands/StatsCommand.cs` - 统计命令处理
- `Commands/ConfigCommand.cs` - 配置命令处理（list/add/remove）
- `Commands/ConvertConfigCommand.cs` - 配置转换命令
- `Commands/ValidateConfigCommand.cs` - 配置验证命令
- `Commands/CommandHelpers.cs` - 命令通用帮助方法

### Services（业务服务）
- `Services/PlaytimeDataReader.cs` - 游玩时长数据读取（CSV/JSON）
- `Services/FileDropHandler.cs` - 文件拖拽处理（自动添加游戏）

### Utilities（工具类）
- `Utilities/ArgumentParser.cs` - 命令行参数解析
- `Utilities/CsvParser.cs` - CSV 解析工具
- `Utilities/DurationFormatter.cs` - 时长格式化工具
- `Utilities/ExecutableResolver.cs` - 可执行文件路径解析

### Models（数据模型）
- `Models/GameItem.cs` - 游戏数据传输对象

## 重构后的 Program.cs

现在的 `Program.cs` 只有约 80 行，职责清晰：

1. **参数解析** - 使用 `ArgumentParser` 处理命令行参数
2. **依赖注入配置** - 配置服务容器
3. **初始化信息输出** - 显示配置路径和构建信息
4. **文件拖拽处理** - 处理拖拽文件到程序的场景
5. **命令路由** - 根据命令类型调用相应的处理器

## 优势

### 1. 单一职责原则
每个类都有明确的单一职责：
- `StatsCommand` 只处理统计显示
- `CsvParser` 只处理 CSV 解析
- `DurationFormatter` 只处理时长格式化

### 2. 易于测试
每个组件都可以独立测试，不需要依赖整个 Program 类。

### 3. 易于扩展
- 添加新命令：只需创建新的 Command 类
- 添加新的数据格式：只需扩展 `PlaytimeDataReader`
- 添加新的工具方法：在相应的 Utilities 类中添加

### 4. 代码复用
工具类可以在多个地方复用，避免代码重复。

### 5. 清晰的依赖关系
- Commands 依赖 Services 和 Utilities
- Services 依赖 Utilities 和 Models
- Utilities 和 Models 没有外部依赖

## 迁移说明

这次重构是**无破坏性**的：
- 所有现有功能保持不变
- 所有命令行接口保持兼容
- 所有测试继续通过
- CSV 迁移功能正常工作

## 未来扩展建议

1. **添加新命令**：在 `Commands/` 目录下创建新的命令类
2. **添加新的数据处理**：在 `Services/` 目录下创建相应服务
3. **添加通用工具**：在 `Utilities/` 目录下添加工具类
4. **添加数据模型**：在 `Models/` 目录下定义新的数据结构

这种结构使得代码更容易理解、维护和扩展。