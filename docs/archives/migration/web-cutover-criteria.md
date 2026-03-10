# Web Runtime Cutover Criteria

本文件定义从临时 Web 运行路径切换到 WinUI-first 的完成标准。

## 必须满足

- WinUI 壳层已覆盖核心用户路径：
  - Settings 更新并持久化
  - Games 的 list/add/update/delete/toggle
  - Stats 概览与明细查询
- CLI 仍可作为回退入口运行并复用同一套 Core 用例
- `--web/--port` 已弃用，且不再触发产品运行时 Web 服务
- 发布流程已切换到 WinUI 产物主通道

## 验证方式

- Core/Infrastructure 单元与集成测试通过
- WinUI ViewModel 测试通过
- 桌面 smoke（FlaUI）在 Windows runner 可执行并产出结果

## 回滚通道

- 保留 `GameHelper.ConsoleHost` 可执行用于紧急诊断和回退
- Release 保留上一稳定标签，可快速回滚
