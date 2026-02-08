## 1. Backend API Setup

- [x] 1.1 Add ASP.NET Core dependencies to `GameHelper.ConsoleHost.csproj` (Microsoft.AspNetCore.App framework reference)
- [x] 1.2 Create `Api/GameEndpoints.cs` — register Minimal API routes for `/api/games` (GET list, POST create, PUT update, DELETE remove)
- [x] 1.3 Create `Api/SettingsEndpoints.cs` — register Minimal API routes for `/api/settings` (GET read, PUT update)
- [x] 1.4 Create `Api/StatsEndpoints.cs` — register Minimal API routes for `/api/stats` (GET all, GET by game name)
- [x] 1.5 Create `Api/WebServerHost.cs` — encapsulate Kestrel startup logic, bind to `127.0.0.1`, configurable port, CORS policy for `localhost:3000`
- [x] 1.6 Integrate `WebServerHost` into `Program.cs` — parse `--web` and `--port` CLI args, start web server alongside existing worker
- [x] 1.7 Add API DTO models (`Api/Models/`) for request/response serialization, mapping from/to `GameConfig` and `AppConfig`
- [x] 1.8 Write xUnit tests for game endpoints (list, add, update, delete) using in-memory `IConfigProvider`
- [x] 1.9 Write xUnit tests for stats endpoints using test playtime data
- [x] 1.10 Write xUnit tests for settings endpoints

## 2. Frontend Project Initialization

- [x] 2.1 Initialize `GameHelper.Web` with `create-next-app` (App Router, TypeScript, Tailwind CSS, ESLint)
- [x] 2.2 Install and configure shadcn/ui (init, add Button, Dialog, Table, Input, Switch, Card, Toast, DropdownMenu components)
- [x] 2.3 Install Recharts and SWR dependencies
- [x] 2.4 Configure API base URL via environment variable (`NEXT_PUBLIC_API_URL`, default `http://localhost:5123`)
- [x] 2.5 Create shared API client utility (`lib/api.ts`) with typed fetch helpers and SWR hooks

## 3. Frontend Layout & Navigation

- [x] 3.1 Create root layout with sidebar navigation (Configuration, Statistics links) using shadcn/ui
- [x] 3.2 Implement responsive sidebar — collapsible on mobile (< 768px)
- [x] 3.3 Add application header with GameHelper branding

## 4. Configuration Management Page

- [x] 4.1 Create `/games` page with game list table (Display Name, Executable Name, Enabled, HDR, Actions columns)
- [x] 4.2 Implement "Add Game" dialog with form fields (executable name, display name, enabled toggle, HDR toggle) and validation
- [x] 4.3 Implement "Edit Game" dialog pre-filled with current values
- [x] 4.4 Implement "Delete Game" confirmation dialog
- [x] 4.5 Create global settings page (process monitor type selector, auto-start toggle, launch on startup toggle)
- [x] 4.6 Add toast notifications for success/error feedback on all mutations
- [x] 4.7 Implement empty state for zero games

## 5. Statistics Dashboard Page

- [x] 5.1 Create `/stats` page with summary cards (total playtime, total sessions, tracked games count)
- [x] 5.2 Implement area chart showing playtime per game (sorted by recent activity) using Recharts
- [x] 5.3 Implement per-game detail view with session history table (Start Time, End Time, Duration)
- [x] 5.4 Implement line chart showing playtime trend over the last 14 days for selected game
- [x] 5.5 Implement empty state for no playtime data

## 6. Integration & Production Build

- [x] 6.1 Configure Next.js static export (`output: 'export'` in `next.config.js`)
- [x] 6.2 Add static file serving middleware in `WebServerHost.cs` to serve Next.js `out/` directory
- [x] 6.3 Verify end-to-end flow: start ConsoleHost with `--web`, open browser, manage config and view stats
- [x] 6.4 Update `README.md` with Web UI usage instructions and development setup guide
