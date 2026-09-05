# 统计推送（sync）指南

GameHelper 可以把游戏时长统计自动推送到远端 GitHub 仓库，用于公开展示（如 Profile README）或私有备份。当前渠道为 GitHub，支持两种上传方式：

| 方式 | 凭据 | 适用场景 |
| --- | --- | --- |
| `method: git`（默认） | 本机 git 凭据管理器 | 已安装 git 且推送过 GitHub 的用户，零 token 配置 |
| `method: api` | GitHub PAT（配置或环境变量） | 未安装 git、无人值守环境、或想避免本机克隆 |

## 推送内容

默认只推送聚合数据，写入仓库的 `sync.directory` 子目录（默认 `game-stats/`），绝不触碰该目录之外的任何文件：

```
game-stats/
├── README.md   # Markdown 报告：各游戏总时长/会话数、最近 7 天趋势、本月合计
└── daily.csv   # date,game,minutes 按日×按游戏累计聚合
```

- 报告与聚合 CSV 不含精确开始/结束时间戳，适合公开仓库。
- `includeRawCsv: true` 可附带 `raw/playtime.csv`（完整会话明细，含精确时间），**仅建议私有仓库开启**。
- `daily.csv` 的 `game` 列使用稳定的 `dataKey`（不随显示名变化）。

## 配置

在 `%AppData%\GameHelper\config.yml` 中加入 `sync` 段：

```yaml
sync:
  enabled: true
  provider: github
  method: git            # git（默认）或 api
  repo: yourname/game-stats
  branch: main           # 可省略：git 方式用克隆默认分支，api 方式用仓库默认分支
  directory: game-stats  # 可省略，默认 game-stats
  token: ""              # 仅 method: api 需要；留空时读取环境变量 GAMEHELPER_GITHUB_TOKEN
  intervalMinutes: 1440  # 自动推送最小间隔（分钟），默认每天最多一次
  includeRawCsv: false   # 是否附带原始会话明细（私有仓库再开）
```

所有字段说明：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `enabled` | `false` | 总开关；关闭时后台循环与 `sync now` 都不会上传 |
| `provider` | `github` | 上传渠道，当前仅支持 `github` |
| `method` | `git` | `git`=本机 git.exe；`api`=REST API + token |
| `repo` | 必填 | 目标仓库，`owner/name` 格式 |
| `branch` | 仓库默认分支 | 目标分支 |
| `directory` | `game-stats` | 仓库内写入子目录（不允许 `..`） |
| `token` | 空 | 仅 `api` 方式；为空回退环境变量 `GAMEHELPER_GITHUB_TOKEN` |
| `intervalMinutes` | `1440` | 两次自动推送的最小间隔，最小 5 |
| `includeRawCsv` | `false` | 附带原始会话明细 |

## 方式一：本机 git（默认，零 token）

前提：已安装 Git for Windows，且本机 git 对目标仓库已有推送凭据（例如之前用 HTTPS 推送过，凭据已存入 Windows 凭据管理器）。

1. 准备一个目标仓库（新建一个公开的 `game-stats` 仓库即可，空仓库也可以，首次推送会自动建立分支）。
2. 在 `config.yml` 写入最小配置：

   ```yaml
   sync:
     enabled: true
     repo: yourname/game-stats
   ```

3. 运行 `sync test` 校验凭据；如提示凭据未就绪，手动对任意该仓库的克隆执行一次 `git push` 让凭据管理器记住凭据，或改用 `api` 方式。

工作机制：GameHelper 在数据目录维护专属克隆（`%AppData%\GameHelper\sync\<owner-repo>\`），每次上传先 `fetch` 并强制对齐远端，再整目录重写、按需提交、推送。该克隆仅供 GameHelper 使用，不要在其中手工编辑。

## 方式二：GitHub REST API + token

1. 在 GitHub 创建 **fine-grained PAT**（Settings → Developer settings → Fine-grained tokens）：
   - Repository access：仅选择目标仓库；
   - Permissions：**Contents: Read and write**（其他保持 No access）。
2. 把 token 写入 `config.yml` 的 `sync.token`，或设置环境变量：

   ```powershell
   setx GAMEHELPER_GITHUB_TOKEN "github_pat_xxx"
   ```

3. 配置 `method: api` 并运行 `sync test` 校验。

token 只保存在本机配置/环境变量中，不会写入日志；推送错误信息中也做了脱敏。

## 自动推送时机与性能

- **会话结束路径零新增磁盘写入**：游戏退出时只写原有的一行 `playtime.csv`，推送功能不做任何额外写盘。
- **轻量检查**：监控运行期间后台每 15 分钟检查一次（启动后延迟 3 分钟首查），检查内容仅为 config 读取 + 文件 mtime 对比。
- **触发条件**：距上次成功推送 ≥ `intervalMinutes` **且** 本地数据比上次推送更新。
- **写盘频率**：仅在推送成功/失败后更新一次 `sync-state.json`（正常 ≈ 每天 1 次）。
- **内容去重**：内容与上次一致时跳过上传，不产生空提交。
- **失败退避**：推送失败后 60 分钟内不自动重试（`sync now --force` 可穿透）。
- WinUI 外壳当前只注册了推送服务，不启动后台循环；自动推送依赖 `monitor` 命令运行的 ConsoleHost 实例。

## 命令

```powershell
# 立即推送一次（自动跳过“未到间隔/无新数据/内容一致”时给出原因）
dotnet run --project .\GameHelper.ConsoleHost -- sync now
dotnet run --project .\GameHelper.ConsoleHost -- sync now --force   # 穿透间隔与退避

# 校验渠道（git 方式验证凭据可达性；api 方式验证 token 与仓库/分支可见性）
dotnet run --project .\GameHelper.ConsoleHost -- sync test

# 查看状态：上次成功/失败时间、待推送数据
dotnet run --project .\GameHelper.ConsoleHost -- sync status
```

> 注意：`sync` 命令与 `stats` 等命令一样受单实例约束——若监控实例正在运行，请先关闭它，或临时设置 `GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE=1`。

## 隐私说明

- 默认推送内容为聚合数据（按日/按游戏的分钟数），不含精确作息时间。
- 公开仓库前请确认游戏列表本身不敏感；`daily.csv` 中的 `game` 列为 `dataKey`。
- 需要完整备份原始明细时再开启 `includeRawCsv`，并把仓库设为私有。
