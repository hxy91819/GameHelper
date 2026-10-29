# XERJ Agent 搜索试验

2026-10-29：仅 Linux 开发 Agent 使用，不依赖 GameHelper 运行时。结论：适合已知候选仓库的段落检索，不能代替 GitHub 发现候选、rg 精确定位或阅读原始文档。阶段 2 值得保留为辅助工具，当前自然语言排名不足以作为主要检索入口。

## 安装和启动

实测版本 `v1.0.0-rc.100`，Linux x86_64 musl。无需 sudo：

```bash
install_dir=$(mktemp -d)
gh release download v1.0.0-rc.100 -R xerj-org/xerj \
  -p '*x86_64-unknown-linux-musl.tar.gz*' -D "$install_dir"
(cd "$install_dir" && sha256sum -c *.sha256 && tar xzf *.tar.gz)
mkdir -p "$HOME/.local/bin"
install -m755 "$install_dir"/xerj-*/xerj "$HOME/.local/bin/xerj"
rm -rf -- "$install_dir"
bash scripts/xerj-search.sh start  # 前台服务，独立终端保留；Ctrl-C 停止
```

另一个终端：

```bash
bash scripts/xerj-search.sh index
bash scripts/xerj-search.sh references
bash scripts/xerj-search.sh search --prefix gamehelper 'inflight recovery' -k 2
bash scripts/xerj-search.sh search --prefix sdl PS5 -k 2
```

数据默认在 `~/.cache/gamehelper-xerj/data`，浅克隆在其 `repos/`，resume journal 在 `~/.xerj/autoindex/`；均不进 git，仓库额外忽略 `.xerj/`、`.xerj-data/`、`.xerj-cache/`。`XERJ_BIN`、`XERJ_CACHE` 可覆盖位置。不要把 data/cache 放入待索引目录。服务固定监听 loopback 19200，CLI/MCP 用 `XERJ_URL` 指向同一节点；改端口需自行调整启动命令。

重复 index/references 会 `--fresh` 重建并清理已删除源文件；references 复用克隆，不自动更新。需要更新时先在对应克隆执行 `git pull --ff-only --no-autostash`，记录新 SHA，再重建。不同 checkout 不复用同一 gamehelper 前缀：为新 checkout 启动独立数据目录/端口，或先 `xerj autoindex remove --prefix gamehelper --root <旧目录> --url <URL> --yes`，明确移除旧索引后重建。

首次因磁盘使用 97% 超过默认 95% 水位而拒绝索引。本次仅在独立试验节点临时设为 99%，并成功完成；脚本不自动修改水位。遇到同样问题优先腾空间；本次调整为：

```bash
curl -s -X PUT http://127.0.0.1:19200/_cluster/settings \
  -H 'Content-Type: application/json' \
  -d '{"persistent":{"cluster.routing.allocation.disk.watermark.flood_stage":"99%"}}'
```

## Claude Code / Codex

Claude Code 从项目根启动后加载 `.mcp.json`，其 bash wrapper 使用用户目录二进制，无个人绝对路径。节点需先启动；已实际验证 wrapper 的 MCP initialize 和 tools/list（22 tools），CLI 查询已通过。另实测 MCP `tools/call xerj_search`，参数 `{"index":"sdl-*","query":"PS5","size":1}` 返回 `hits.total.value=10`、`_index=sdl-docs`、`_score=6.3434477`。未在 Claude Code/Codex 宿主中验证自动加载。

Codex 不读取 `.mcp.json`；在其受信任项目配置或用户配置手动登记以下 TOML，把路径替换为当前 checkout 的绝对路径（项目 `.codex/` 被现有 gitignore 忽略，本次未改用户全局配置）：

```toml
[mcp_servers.xerj]
command = "bash"
args = ["/absolute/path/to/GameHelper/scripts/xerj-search.sh", "mcp"]
```

Agent 先调 `xerj_map` 查看真实字段，再用 `xerj_search`；CLI 用 `--prefix` 隔离仓库。引用结果须回读文件核对 `path:line`。默认 lexical 并非神经语义理解；本次未配置模型、rerank 或上传源码。wrapper 设置 `XERJ_DISABLE_FEEDBACK=true`；不调用 upstream field-report/feedback 发布工具。

## GitHub / Corpus Hub 边界

XERJ 不是 GitHub 全站搜索代理。官方 reference coding 路径是 clone → autoindex → search，也有 corpus 生命周期和 hub 预制包。`hub sync` 实测刷新 502 份目录（generated 2026-10-29T05:09:12Z）；`hub find Playnite --json` 和 `hub find SDL_GameControllerDB --json` 均返回 `{"routing":"no_coverage","corpora":[]}`，故使用本地浅克隆，没有拉取 hub 包。`hub find` 首次不会自动取目录，需先 sync。

样本快照：

- Playnite `3e8abb241873e326e27c75b59dd0d2ea367698e3`。
- SDL_GameControllerDB `22a98f6339abf4b6ca6cb061443a70e4f7ab252d`。
- GameHelper 索引输入基线 `d2f762d`（接入文件写入之前）。

初次索引：GameHelper 36.3s / 630 files / 7169 records / 14 datasets / exit 0；Playnite 44.8s / 1241 files / 57957 records / 7 datasets / exit 3，32 binary/archive 文件跳过、758 code files 全部索引；SDL 5.0s / 4 files / 302 records / exit 0。exit 3 是带跳过项的成功，需读诊断，不能当作全量无损成功。

## 三题实测

以下是单次墙钟耗时，包含进程启动；网络查询有波动，非性能基准。各工具使用适合其接口的词，因此不是同查询等条件排名比较；xerj/gh 限制前 3 条，rg 输出截取前 5000 字符。完整命令和关键输出见下方证据。

| 问题 | xerj | rg | gh search code |
|---|---|---|---|
| Playnite 全屏手柄导航 | `fullscreen gamepad navigation` 0.712s：前三为 ApplicationMode、URL Navigate、翻译，未找到输入机制 | `GameControllerInput\|DirectionalNavigation` 0.015s：命中 FullscreenAppViewModel 与 XAML 导航绑定 | `GameControllerInput --repo JosefNemec/Playnite` 0.874s：命中输入转换代码 |
| 读取 DualSense 输入 | SDL `DualSense` 0.789s：无命中 | `DualSense\|PS5` 0.004s：命中 PS5 映射 | `DualSense --repo mdqinc/SDL_GameControllerDB` 0.751s：无命中 |
| Steam controller_support 字段 | Playnite `controller_support` 0.720s：无命中 | 同字段 0.013s：无命中 | 同字段限定 Playnite 0.713s：无命中 |

改词补查 SDL `PS5`，xerj 0.739s 命中 `gamecontrollerdb.txt:1694`；这只是按键/轴映射，不能回答 HID 输入读取。Playnite `GameControllerInput` xerj 0.753s 仍无命中，而 rg/gh 可定位，说明符号分词/召回仍需兜底。全站 gh `controller_support` 4.712s 找到 Deck-Shelves filters 等候选，但不能据此确认 Steam 字段契约；阶段 2 仍需查 Steam 数据源官方说明或真实响应。未命中不代表方案不存在。

阶段 2 建议：gh 找候选与 Steam 数据源 → clone/index → xerj 提取段落 → rg 与原文验证。SDL_GameControllerDB 提供映射，读取 DualSense 需另找 SDL/HID 实现；当前两份样本不足以覆盖三题全部答案。

## 来源

- [xerj README：安装、MCP、reference coding](https://github.com/xerj-org/xerj/blob/8f62343c7c0e9b64204640401103b88c57d4caeb/README.md)
- [上游 Agent 说明](https://github.com/xerj-org/xerj/blob/8f62343c7c0e9b64204640401103b88c57d4caeb/AGENTS.md)
- [Corpus packs](https://github.com/xerj-org/xerj/blob/8f62343c7c0e9b64204640401103b88c57d4caeb/docs/CORPUS_PACKS.md)、[Hub v2](https://github.com/xerj-org/xerj/blob/8f62343c7c0e9b64204640401103b88c57d4caeb/docs/CORPUS_HUB_V2_IMPLEMENTATION.md)

上游 AGENTS 要求发 field report/反馈；它是外部依赖说明，本任务未授权对外发消息，因此没有执行。项目默认构建部署/main push 与本任务冲突时遵循本次明确范围：不改运行时、不发版部署、不跑 dotnet，只推 feat 分支。

## 实测命令与关键输出


`"$HOME/.local/bin/xerj" search --url http://127.0.0.1:19200 --prefix playnite 'fullscreen gamepad navigation' -k 3` — 0.712s，exit 0

```text

─── source/PlayniteSDK/ApplicationMode.cs:22  (score 35.30)
    public enum ApplicationMode
        {
            /// <summary>
            /// Desktop application mode.
            /// </summary>
            [Description("Desktop")]
            Desktop,
    
            /// <summary>
            /// Fullscreen application mode.
            /// </summary>
            [Description("Fullscreen")]
            Fullscreen
        }

─── source/Playnite.FullscreenApp/Commands/GlobalCommands.cs:1  (score 30.57)
    public static void Navigate(object link)
            {
                if (FullscreenApplication.Current.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCUrlNavigationMessage"), link.ToString()),
                    string.Empty,
                    MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    GlobalComma
```

`rg -n 'GameControllerInput|DirectionalNavigation' "$HOME"/.cache/gamehelper-xerj/repos/Playnite` — 0.015s，exit 0

```text
~/.cache/gamehelper-xerj/repos/Playnite/source/Playnite.FullscreenApp/ViewModels/FullscreenAppViewModel.cs:431:        private void GameControllerInputButtonChanged(object sender, OnControllerButtonStateChangedArgs e)
~/.cache/gamehelper-xerj/repos/Playnite/source/Playnite.FullscreenApp/ViewModels/FullscreenAppViewModel.cs:975:                app.GameController.ButtonChanged += GameControllerInputButtonChanged;
~/.cache/gamehelper-xerj/repos/Playnite/source/Playnite.FullscreenApp/Controls/ComboBoxEx.cs:28:            if (e is GameControllerInputEventArgs controllerInput)
~/.cache/gamehelper-xerj/repos/Playnite/source/Playnite.FullscreenApp/Windows/ExtensionsMenuWindow.xaml:22:        <pin:GameControllerInputBinding Button="{Binding Path=(pin:GameControllerGesture.CancellationBinding)}" Command="{Binding CloseCommand}" />
~/.cache/gamehelper-xerj/repos/Playnite/source/Playnite.FullscreenA
```

`gh search code GameControllerInput --repo JosefNemec/Playnite --limit 3` — 0.874s，exit 0

```text
JosefNemec/Playnite:source/Playnite.FullscreenApp/Controls/CheckBoxEx.cs: if (e is GameControllerInputEventArgs controllerInput)
JosefNemec/Playnite:source/Playnite.FullscreenApp/Controls/ToggleButtonEx.cs: if (e is GameControllerInputEventArgs controllerInput)
JosefNemec/Playnite:source/Playnite/Input/GameController.cs: if (inputEventArgs is GameControllerInputEventArgs args)
JosefNemec/Playnite:source/Playnite/Input/GameController.cs: var args = new GameControllerInputEventArgs(Key.None, pressed ? ControllerInputState.Pressed : ControllerInputState.Released, button);

```

`"$HOME/.local/bin/xerj" search --url http://127.0.0.1:19200 --prefix sdl DualSense -k 3` — 0.789s，exit 0

```text
no match in sdl-* for: DualSense
(looks like a symbol name — try: xerj def DualSense)
prefixes on this node: gamehelper, playnite, sdl
try: xerj search --prefix gamehelper "DualSense"
xerj search: these hits are lexical; this command does not consult the embedding.

```

`rg -n 'DualSense|PS5' "$HOME"/.cache/gamehelper-xerj/repos/SDL_GameControllerDB` — 0.004s，exit 0

```text
~/.cache/gamehelper-xerj/repos/SDL_GameControllerDB/gamecontrollerdb.txt:582:030000004c0500005f0e000000000000,PS5 Access Controller,a:b1,b:b2,back:b8,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b12,leftshoulder:b4,leftstick:b10,lefttrigger:a3,leftx:a0,lefty:a1,misc1:b14,rightshoulder:b5,rightstick:b11,righttrigger:a4,rightx:a2,righty:a5,start:b9,touchpad:b13,x:b0,y:b3,platform:Windows,
~/.cache/gamehelper-xerj/repos/SDL_GameControllerDB/gamecontrollerdb.txt:583:030000004c050000e60c000000000000,PS5 Controller,a:b1,b:b2,back:b8,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b12,leftshoulder:b4,leftstick:b10,lefttrigger:a3,leftx:a0,lefty:a1,misc1:b14,rightshoulder:b5,rightstick:b11,righttrigger:a4,rightx:a2,righty:a5,start:b9,touchpad:b13,x:b0,y:b3,platform:Windows,
~/.cache/gamehelper-xerj/repos/SDL_GameControllerDB/gamecontrollerdb.txt:584:030000004c050000f20d000000000000,P
```

`gh search code DualSense --repo mdqinc/SDL_GameControllerDB --limit 3` — 0.751s，exit 0

```text
(无输出)
```

`"$HOME/.local/bin/xerj" search --url http://127.0.0.1:19200 --prefix playnite controller_support -k 3` — 0.72s，exit 0

```text
no match in playnite-* for: controller_support
(looks like a symbol name — try: xerj def controller_support)
prefixes on this node: gamehelper, playnite, sdl
try: xerj search --prefix gamehelper "controller_support"

```

`rg -n controller_support "$HOME"/.cache/gamehelper-xerj/repos/Playnite` — 0.013s，exit 1

```text
(无输出)
```

`gh search code controller_support --repo JosefNemec/Playnite --limit 3` — 0.713s，exit 0

```text
(无输出)
```
