## Why

GameHelper 目前仅提供 CLI 界面来管理游戏配置和查看统计数据，操作门槛较高且不直观。增加一个 Web 前端可以让用户通过浏览器以可视化的方式管理游戏列表、修改配置、查看游戏时间统计图表，大幅提升用户体验。采用 CLI + Web 前端分离模式，在现有 .NET 后端中嵌入轻量 HTTP API，前端使用 Next.js + Tailwind + shadcn/ui 构建。

## What Changes

- 在 `GameHelper.ConsoleHost` 中嵌入 ASP.NET Core Minimal API，暴露配置管理和统计数据的 REST 端点
- 新增 `GameHelper.Web` 项目（Next.js + TypeScript + Tailwind CSS + shadcn/ui），提供 Web 管理界面
- Web 前端包含两个核心页面：**配置管理**（游戏列表 CRUD、全局设置）和**统计仪表盘**（游戏时间可视化）
- CLI 启动时可选启动内嵌 Web Server，用户通过浏览器访问 `http://localhost:<port>`

## Capabilities

### New Capabilities
- `web-api`: 在 ConsoleHost 中嵌入 ASP.NET Core Minimal API，提供游戏配置 CRUD 和统计数据查询的 REST 端点
- `web-dashboard`: Next.js Web 前端，包含配置管理页面和游戏统计仪表盘页面

### Modified Capabilities
<!-- 无现有 spec 需要修改 -->

## Impact

- **新增依赖**: ConsoleHost 需要引入 ASP.NET Core 相关 NuGet 包（Microsoft.AspNetCore.App）；新增 Node.js 项目依赖（Next.js, Tailwind, shadcn/ui, recharts 等）
- **项目结构**: 新增 `GameHelper.Web/` 目录存放前端代码
- **端口占用**: 内嵌 Web Server 默认监听一个本地端口（如 5123），需在配置中可调
- **构建流程**: 发布时需同时构建 .NET 后端和 Next.js 前端，前端静态资源可嵌入或独立部署
- **现有功能**: 不影响现有 CLI 命令和进程监控功能，Web Server 为可选组件
