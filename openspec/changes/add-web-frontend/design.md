## Context

GameHelper 是一个 .NET 8.0 控制台应用，通过 WMI/ETW 监控游戏进程，自动管理 HDR 和记录游戏时间。当前所有操作通过 CLI 完成（`config list/add/remove`、`stats`）。数据存储在 `%AppData%/GameHelper/` 下的 `config.yml`（YAML 配置）和 `playtime.json`（游戏时间记录）。

本设计为项目增加 Web 管理界面，采用 **后端嵌入 HTTP API + 独立 Next.js 前端** 的分离架构。

## Goals / Non-Goals

**Goals:**
- 在 ConsoleHost 中嵌入轻量 ASP.NET Core Minimal API，暴露配置和统计数据端点
- 构建 Next.js Web 前端，提供配置管理和统计仪表盘两个核心页面
- Web Server 为可选功能，不影响现有 CLI 和进程监控功能
- 前端开发体验优先，使用 AI 友好的技术栈（Next.js + Tailwind + shadcn/ui）

**Non-Goals:**
- 不做用户认证（仅本地 localhost 访问）
- 不做实时进程监控的 WebSocket 推送（首版仅 REST 轮询）
- 不做 Electron/Tauri 桌面打包（留给后续迭代）
- 不修改现有 CLI 命令的行为

## Decisions

### D1: 后端 API 框架 — ASP.NET Core Minimal API

**选择**: 在 ConsoleHost 中嵌入 ASP.NET Core Minimal API（通过 `WebApplication.CreateSlimBuilder`）。

**备选方案**:
- **独立 Web API 项目**: 增加部署复杂度，需要启动两个进程
- **gRPC**: 前端调用不友好，需要额外 gateway
- **嵌入式 HTTP 库（如 EmbedIO）**: 生态小，缺乏中间件支持

**理由**: Minimal API 直接集成在 .NET 8 中，零额外依赖，可复用现有 DI 容器中的 `IConfigProvider` 和 `IPlayTimeService`。

### D2: 前端框架 — Next.js (App Router) + TypeScript

**选择**: Next.js 14+ App Router + TypeScript + Tailwind CSS + shadcn/ui + Recharts

**备选方案**:
- **Vite + React**: 更轻量但缺少 SSR/路由约定
- **Vue/Nuxt**: 可行但 AI 代码生成对 React 生态支持最好

**理由**: Next.js 是当前 AI 生成前端代码质量最高的框架，shadcn/ui 提供高质量可定制组件，Recharts 用于统计图表。

### D3: 前后端通信 — REST API + SWR

**选择**: 前端通过 `fetch` + SWR 调用后端 REST API，所有端点返回 JSON。

**API 端点设计**:

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/games` | 获取所有游戏配置列表 |
| POST | `/api/games` | 添加游戏 |
| PUT | `/api/games/{dataKey}` | 更新游戏配置 |
| DELETE | `/api/games/{dataKey}` | 删除游戏 |
| GET | `/api/settings` | 获取全局设置 |
| PUT | `/api/settings` | 更新全局设置 |
| GET | `/api/stats` | 获取所有游戏统计数据 |
| GET | `/api/stats/{gameName}` | 获取单个游戏统计数据 |

### D4: Web Server 生命周期

**选择**: 在现有 ConsoleHost 的 `Worker` 或 `Program.cs` 中，通过配置项 `webServerEnabled: true` 和 `webServerPort: 5123` 控制是否启动内嵌 Web Server。

**启动方式**:
- CLI 参数: `gamehelper --web` 或 `gamehelper --web --port 5123`
- 配置文件: `config.yml` 中增加 `webServerPort` 字段
- Web Server 在后台线程运行，不阻塞 CLI 交互

### D5: 前端静态资源部署

**选择**: 开发时前后端分别启动（Next.js dev server + .NET API），生产环境将 Next.js `out/` 静态导出嵌入 .NET 发布包，由 Kestrel 提供静态文件服务。

**理由**: 单进程部署最简单，用户只需运行一个 exe。开发时分离启动保持热重载体验。

### D6: CORS 策略

**选择**: 开发环境允许 `localhost:3000`（Next.js dev server）跨域访问；生产环境由同一 Kestrel 服务，无跨域问题。

## Risks / Trade-offs

- **端口冲突** → 默认端口可配置，启动时检测端口占用并提示
- **并发写入配置文件** → API 层加锁，与现有 CLI 写入互斥（复用 `IConfigProvider` 内部锁机制）
- **Node.js 开发依赖** → 仅开发时需要，生产环境通过静态导出消除 Node.js 依赖
- **前端包体积** → Next.js 静态导出 + Tree Shaking，预计 < 5MB
- **安全风险（本地端口暴露）** → 仅绑定 `127.0.0.1`，不监听外部网络接口
