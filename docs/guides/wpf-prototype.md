# WPF 游戏库体验原型

这个独立、可丢弃的原型用于回答：原生 WPF 能否提供适合 GameHelper 的游戏库浏览与启动偏好体验？尚未选定正式界面方案。

## 运行

```powershell
powershell -ExecutionPolicy Bypass -File scripts/open-wpf-prototype.ps1
# 使用明确标记的演示目录，不读取真实目录
powershell -ExecutionPolicy Bypass -File scripts/open-wpf-prototype.ps1 -Demo
# 指定配置或导出三个布局与空结果的实际 WPF 渲染图片，并完成交互冒烟检查后退出
powershell -ExecutionPolicy Bypass -File scripts/open-wpf-prototype.ps1 -ConfigPath C:\sandbox\config.yml -SnapshotDirectory C:\sandbox\preview
```

需要仓库使用的 .NET 8 SDK 和 Windows Desktop Runtime。每次运行发布到独立的 `artifacts/wpf-prototype/<随机标识>/`，可直接从输出路径再次打开 exe；关闭原型后可删除对应目录。脚本不修改 ConsoleHost 发布目录、注册表或快捷方式。

默认只读载入 `%AppData%\GameHelper\config.yml`。指定 `GAMEHELPER_DATA_DIR` 时，读取该目录下的 `GameHelper\config.yml`；`-ConfigPath` 优先。配置读取失败会显示错误，不会静默替换为演示数据。空目录显示空结果。

## 比较的内容

- **封面网格**：浏览为主，选中后可进入启动偏好。
- **紧凑列表**：名称和自动化摘要为主，右侧编辑本次预览偏好。
- **详情视图**：窄列表配合更大的详情区。
- `Ctrl+F` 聚焦搜索，`Esc` 清空搜索，`Alt+1/2/3` 切换布局；列表支持原生键盘选择和滚动。搜索支持名称和程序标识，HDR / 倍速筛选使用已载入的配置。
- 偏好在选中项之间切换时保留在内存；“预览启动步骤”显示所选偏好对应的流程，不产生系统操作。

封面使用明确标注的图形占位，不联网抓取图片。没有模拟游玩统计、扫描进度或运行状态。现有 Steam 导入仍通过 `config import-steam` 使用，原型暂未接入导入按钮。

## 边界与后续

源代码位于 `prototypes/GameHelper.WpfPrototype`，引用现有配置读取适配器。没有创建监控器、HDR / DDC 控制器、CE 控制器或配置写入器。主程序仍正常开机启动与部署。

原型的代码后置事件与内存状态仅服务于体验比较。用户选定布局后，正式实现需要独立的视图模型、可取消的启动编排、平台启动入口，以及与运行中唯一自动化实例的协调；不得把当前预览按钮直接当成真实启动实现。扫描目录、封面获取和游戏统计均尚未接入。

验证使用真实 WPF 渲染和搜索、清空搜索、HDR 偏好流程预览冒烟检查。它证明基本布局可运行，不代表已验证大量游戏下的性能、读屏兼容或正式启动链路。
