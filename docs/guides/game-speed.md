# 游戏倍速配置

GameHelper 可以在匹配到游戏进程后调用用户已安装的 Cheat Engine，按进程 PID 自动附加并应用指定倍速。每款游戏只需在 `config.yml` 中配置 `speed`，不需要创建或维护 `.CT` 文件。

## 配置

```yaml
cheatEngine:
  executable: "C:\\Program Files\\Cheat Engine\\cheatengine-x86_64-SSE4-AVX2.exe"

games:
  - dataKey: example
    executable: "D:\\Games\\Example\\Example.exe"
    displayName: Example
    enabled: true
    hdr: false
    speed: 2.0
    # 可选：immediate = 游戏一启动就附加速度（旧行为）；省略或 auto = 检测到 mod loader 时延迟附加
    # speedAttach: immediate
```

- `cheatEngine.executable` 必须是 Cheat Engine 可执行文件的绝对路径。
- `speed` 必须是大于 `0` 的数字。`1.0` 是原速，`0.5` 是半速，`2.0` 是两倍速。
- 省略 `speed` 时，GameHelper 不启动 Cheat Engine，也不修改该游戏速度。
- `speedAttach` 可选：`auto`（默认，省略时等效）与 `immediate`。仅在游戏装了 mod loader 且行为异常时才需要显式设置 `immediate`。
- 同一游戏出现多个匹配进程时，只在第一个活动进程上应用一次；游戏完全退出并再次启动后会重新应用。

GameHelper 将生成的临时表写入 `%AppData%\GameHelper\cheat-engine\`，表中只包含目标 PID 和倍速。旧表会自动清理。配置热重载后，新的倍速对下一次游戏启动生效。

## 与 mod loader（BepInEx / MelonLoader）共存

Cheat Engine 附加进程时会把自己的辅助 DLL（含 autorun 目录里的 MonoDataCollector 等）注入游戏。若游戏同时装有 mod loader，其启动敏感阶段会与这次注入竞争，导致游戏以访问冲突崩溃且两边都没有日志（BepInEx 首次运行生成 interop 程序集的窗口尤其危险，实测在 Unity 6000.0.62 IL2CPP 游戏上稳定复现）。

因此默认策略（`speedAttach: auto`）是：游戏目录旁检测到 `BepInEx\` 或 `MelonLoader\` 时，GameHelper 推迟附加速度，直到 loader 日志标记完成启动：

- BepInEx：`BepInEx\LogOutput.log` 出现 `Chainloader startup complete`（BepInEx 每次启动都会重写该文件，旧内容不会误判）。
- MelonLoader：没有等价的完成标记，退化为超时追加。
- 兜底：等待上限 90 秒，超时后照常附加；游戏中途退出则放弃本次附加（并带 PID 复用防护）。
- 无 loader 的游戏维持旧有的立即附加行为，不受影响。

已知的缓存失效场景会让 BepInEx 重新进入慢速生成阶段（游戏大版本更新、BepInEx 升级、删除 `BepInEx\interop\`）。门控在所有这类场景下都会自动等待，不需要人工干预。

### 手动排障参照

游戏启动即崩且 GameHelper 日志出现 "exited ... after the speed controller attached Cheat Engine"（附 WER 报告号）时：

1. 先临时把该游戏的 `speed` 配置项删掉，重启 GameHelper；
2. 完整启动一次游戏，让 BepInEx 重建 interop 缓存；
3. 恢复 `speed` 配置，之后启动会重新被门控延迟附加，两者可长期共存。

## Cheat Engine 一次性设置

生成的表通过 Lua 调用 Cheat Engine 的 `openProcess` 和 `speedhack_setSpeed`。如果 Cheat Engine 启用了“Ask to run lua scripts from tables”，加载时仍会出现确认框。若希望完全自动化，可在 Cheat Engine 的常规设置中关闭这项询问；这会允许所有已打开表的 Lua 脚本执行，因此只应打开来源可信的表。

Cheat Engine 与 GameHelper 应以足以访问目标游戏的相同或更高权限运行。部分游戏自身的计时实现不兼容通用 speedhack，此时配置可能无法生效。

## 安全边界

该功能用于离线单机游戏或自研程序。不要对联网游戏或启用了 EAC、BattlEye、ACE 等反作弊系统的游戏配置 `speed`；这可能违反游戏协议并造成封禁。
