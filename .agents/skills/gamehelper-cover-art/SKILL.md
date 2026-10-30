---
name: gamehelper-cover-art
description: "给 GameHelper 游戏库手动补充 Steam 封面（海报）。用户说“补封面”“补海报”“缺封面的都找下”“匹配海报”时使用；只写本地海报关联与缓存，不删条目、不改启动配置。"
---

# GameHelper 封面补充

给已安装的 GameHelper 游戏库补 Steam 竖版封面。全程是数据操作，不需要重新编译或部署；桌面端只认 Steam AppID，因此没有 Steam 商店页的游戏只能保留字母占位。

## 边界

- 只改 `%AppData%\GameHelper` 下的海报数据：`config.yml` 里的游戏清单只读，`config.yml.desktop-artwork.json` 存 `dataKey → AppID`，`cache\steam-posters\<appid>.jpg` 是桌面端优先读取的图片缓存。
- 不删除游戏、不改 `executable` / `launch`、不动历史时长；用户要求合并重复条目时再单独处理。
- 不伪造 AppID。找不到 Steam 商店页（如 古龙风云录、Harem Hotel、JinGu、That New Teacher）就如实报告并保留占位。
- 关联文件只在游戏库窗口创建时读取一次；写完必须关闭并重新打开游戏库窗口才能看到。主机热重载配置与海报无关。
- **SQLite 激活后关联真身在数据库里**：`%AppData%\GameHelper\userdata.db` 存在且 `migration_state` 有 `activated` 行时，桌面端从 `artwork_links` 表读关联，`config.yml.desktop-artwork.json` 只是单向兼容投影。此时 `poster-tool Add` 只写 JSON，会在几十秒内被下一次全量投影（任何桌面保存都会触发 `SqliteLegacyCompatibility.ExportAsync` 重写全部侧文件）按数据库真身覆写掉。先探明模式再动手。

## SQLite 模式写入配方

1. 只读探查用 python `sqlite3`（`file:...?mode=ro`），连接上必须 `create_collation('GH_ORDINAL_IGNORE_CASE', lambda a,b: (a.upper()>b.upper())-(a.upper()<b.upper()))`，否则索引扫描直接报 no such collation。
2. 写入走发布目录里的真实代码路径（自带锁/修订/投影协议），临时 console 项目引用 publish 下的 `GameHelper.Infrastructure.dll`、`GameHelper.Core.dll`、`Microsoft.Data.Sqlite.dll` 及 SQLitePCLRaw 三件套：
   - `store = new SqliteDesktopStore(factory, configPath)`，`baseline = store.Read("artwork")`；
   - 反序列化 `Dictionary<string,uint>` 加上新键后 `store.Save("artwork", baseline, json)`；
   - 再 `await new SqliteLegacyCompatibility(factory, TimeProvider.System).ExportAsync()` 让投影 JSON 同步。
   - 演练时先把真库 `backup()` 到临时目录、跳过 ExportAsync（它会按 config_path 写真实文件）；运行中实例的会话写入会推进 `data_revision`，属正常竞争。
3. 桌面端 `_links` 在进程构造时读取一次，**重开窗口没用**，必须整个 App 重启才生效；乐观修订协议会保护外部写入（旧进程后续保存会报"已被另一窗口修改"而非静默删除）。重启用部署脚本的手法：`Stop-Process -Force` 桌面进程（不碰 ConsoleHost 计时）+ `dot-source scripts/start-checked-instance.ps1` 后 `Start-RestoredDesktop <publish>\desktop\GameHelper.Desktop.exe`（`--restore`：不抢前台、不自动进主机模式）。用户游戏在飞时等会话结束再重启。

## 数据与解析顺序

- 桌面端解析：手动关联 → 启动入口里的 `steam://rungameid/<id>`、`.url`、`steam_appid.txt`、Steam `appmanifest` → `%AppData%\GameHelper\cache\steam-posters\<id>.jpg` → Steam 客户端 `appcache\librarycache` → exe 图标。
- 关联文件是 UTF-8。用 Windows PowerShell 5.1 读写时必须显式指定 UTF8，否则中文 `dataKey` 会乱码。

## 流程

1. 盘点缺封面：
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .agents\skills\gamehelper-cover-art\scripts\poster-tool.ps1 -Action List
   ```
2. 搜索候选（中英文各搜一次，见下方“国区漏结果”）：
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .agents\skills\gamehelper-cover-art\scripts\poster-tool.ps1 -Action Search -Term "Being a DIK"
   ```
3. 逐个确认：只取 `type=app` 的本体，排除 Soundtrack、DLC、Artbook、Guide、Bundle。拿不准时用 `-Action Add` 打印的 Store item 名称和 `type=0` 核对。
4. 写入并下载（幂等，可重复执行）：
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .agents\skills\gamehelper-cover-art\scripts\poster-tool.ps1 -Action Add -DataKey beingadik -AppId 1126320
   ```
5. 验证：
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .agents\skills\gamehelper-cover-art\scripts\poster-tool.ps1 -Action Verify
   ```
   目标 `dataKey` 应出现在 List 的有封面列表里；然后关闭并重开游戏库窗口确认显示。

`-ConfigPath <沙盒目录>\config.yml` 可把整套读写指向复制出来的配置做演练，缓存与关联文件都会落在同目录。

## 常见坑

- **国区搜索漏结果**：`l=schinese&cc=CN` 会漏掉成人/未过审游戏（Being a DIK、SEXNATURAL、Yakuza Rogue 等都只在 US 商店返回）。统一先搜 `cc=US&l=english`，再用中文补一遍。
- **原声带陷阱**：极品采花郎的 3929550 是 Soundtrack，本体是 2897760。`Add` 默认拒绝 `type≠0`；只有用户明确要求给非本体配图时才加 `-Force`。
- **灰色占位图**：直接拼 `library_600x900_2x.jpg` 对部分游戏返回纯灰图（如 Battlefield 6）。必须优先用 StoreBrowse `assets` 里的 `library_capsule_2x` 带哈希路径；脚本已按此顺序回退。
- **同名多条**：同一游戏可能在配置里有多条（如 Shambles 的旧目录）；先确认要改的 `dataKey`，不要靠显示名猜。
- **共享工作区**：可能还有别的实例或 worktree 在用同一份 AppData；不要杀进程、不要改 `config.yml`，只写海报关联和缓存。

## 完成判据

- `Verify` 的 stale 和 missing 为 0（或剩下的都确认没有 Steam 页）；SQLite 模式下用 python 只读查询核对 `artwork_links` 行确实写入。
- 重开后的游戏库窗口能看到新封面；没有 Steam 页的条目保持字母占位，并在汇报里列出来。SQLite 模式下"重开"指整个桌面 App 重启。
