# 统计推送（sync）指南

GameHelper 可以把游戏时长统计自动推送到远端 GitHub 仓库，用于公开展示（如 Profile README）或私有备份。当前渠道为 GitHub，支持两种上传方式：

| 方式 | 凭据 | 适用场景 |
| --- | --- | --- |
| `method: git`（默认） | 本机 git 凭据管理器 | 已安装 git 且推送过 GitHub 的用户，零 token 配置 |
| `method: api` | GitHub PAT（配置或环境变量） | 未安装 git、无人值守环境、或想避免本机克隆 |

## 推送内容

默认只推送聚合数据，写入仓库的 `sync.directory` 子目录（默认 `game-stats/`）：

```
game-stats/
├── README.md   # Markdown 报告：各游戏总时长/会话数、最近 7 天趋势、本周/本月合计
└── daily.csv   # date,game,minutes 按日×按游戏累计聚合
```

- 报告与聚合 CSV 不含精确开始/结束时间戳，适合公开仓库。
- 报告不含"生成时刻"，同一自然日内内容指纹稳定；但"最近 7 天/本周/本月"窗口随自然日滑动，跨天时即使数据未变也会产生一次窗口更新的提交——这与"每天最多推一次"的默认节奏一致，报告的更新时间看仓库提交历史即可。
- GameHelper 只管理上述两个文件（开启 `includeRawCsv` 时还有 `raw/playtime.csv`）；你在该子目录里自放的文件不会被推送，也不会被删除。关闭 `includeRawCsv` 时，之前推送过的 `raw/playtime.csv` 会在下一次推送中自动从远端删除。
- `daily.csv` 的 `game` 列使用稳定的 `dataKey`（不随显示名变化）。

## 配置

在 `%AppData%\GameHelper\config.yml` 中加入 `sync` 段：

```yaml
sync:
  enabled: true
  provider: github
  method: git            # git（默认）或 api
  repo: yourname/game-stats
  branch: main           # 可省略：使用远端仓库当前默认分支
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

工作机制：GameHelper 在数据目录维护专属浅克隆（`%AppData%\GameHelper\sync\repo-<仓库身份 SHA-256>\`），每次上传先核对 origin 的读取与推送 URL 均指向配置仓库，再 `fetch` 并把本地分支强制对齐到远端，重写受管文件、按需提交、推送。仓库身份保留完整 owner/name，避免连字符组合相同的两个仓库误用同一克隆；旧版按 owner-repo 命名的克隆不复用、不删除。该克隆仅供 GameHelper 使用，不要在其中手工编辑或修改 remote。

显式指定非默认分支时，会先确认远端分支是否存在，并定向获取该分支的最新浅历史后再对齐；远端不存在的分支仍可首次创建。配置中的仓库、上传方式、分支或目录变更会使旧同步书签失效，下一次自动检查重新上传，不受旧目标的成功时间或内容指纹抑制。分支与远端目录按大小写区分，首尾空白及目录分隔符按配置规范归一化。旧版缺少分支/目录身份的状态文件会安全地重新上传一次。

清空 `sync.branch` 后，每次通过远端 HEAD 解析默认分支，跟随仓库默认分支变更，不沿用专属工作副本之前检出的显式分支。远端空仓库尚无 HEAD 时，才使用克隆的初始分支首次提交。

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

- **会话结束与兼容窗口**：SQLite 激活后先提交历史/在途事务，再输出完整兼容 `playtime.csv`；这是首个 SQLite 发布周期的阶段性写盘成本。sync 直接从 SQLite 一致历史快照导出，不等待兼容投影，未增加会话结束时的上传动作；未激活时仍沿用旧 CSV 追加与读取。
- **轻量检查**：监控运行期间后台每 15 分钟检查一次（启动后延迟 3 分钟首查），检查内容为 config 读取、配置文件 mtime 与 `database_id + history_revision` 对比；未激活 SQLite 时仍比较 CSV mtime。
- **触发条件**：距上次成功推送 ≥ `intervalMinutes` **且** 本地数据比上次推送更新。
- **历史书签**：成功后只保存实际导出快照的数据库标识和历史修订；上传期间完成的新会话仍待下次推送。旧状态没有书签时重新检查一次；数据库恢复／替换后的新标识也会使旧书签失效。心跳、手帐／评分等私人数据和 WAL checkpoint 不触发推送，不按数据库或 WAL 文件 mtime 判断。
- **读取失败**：记为同步失败，保留上次成功书签，不将失败当作空历史；兼容文件投影失败不阻止已提交库历史的上传。
- **写盘频率**：仅在每次推送尝试后更新一次 `sync-state.json`（正常 ≈ 每天 1 次）。
- **内容去重**：同一自然日内内容与上次一致时跳过上传，不产生空提交（跨天时报告窗口自然滑动，见上文）。
- **失败退避**：推送失败后 60 分钟内不自动重试（`sync now --force` 可穿透）。
- 自动推送依赖 `monitor` 命令运行的 ConsoleHost 实例。

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

年度／季度报告的 `report.llm.apiKey` 保存在本机 config.yml；sync 不上传配置文件，因此不会同步此密钥。模型点评与手动导出的 Markdown 也不进入自动同步文件集合。
