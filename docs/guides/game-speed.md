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

- BepInEx：`BepInEx\LogOutput.log` 出现 `Chainloader startup complete`，且日志写入时间不早于本次游戏进程的启动时间（BepInEx 每次启动都会重写该文件，上一轮留下的旧内容不会误判；反过来，loader 在门控开始等待之前就已完成启动的快速启动场景也能立即识别，不会白等 90 秒）。
- MelonLoader：没有等价的完成标记，退化为超时追加。
- 兜底：等待上限 90 秒，超时后照常附加；游戏中途退出则放弃本次附加（并带 PID 复用防护）。
- 无 loader 的游戏维持旧有的立即附加行为，不受影响。

已知的缓存失效场景会让 BepInEx 重新进入慢速生成阶段（游戏大版本更新、BepInEx 升级、删除 `BepInEx\interop\`）。门控在所有这类场景下都会自动等待，不需要人工干预。

### 手动排障参照

游戏启动即崩且 GameHelper 日志出现 "exited ... after the speed controller attached Cheat Engine"（附 WER 报告号）时，说明崩溃时间晚于本次附加；WER 报告的崩溃时间取自 `Report.wer` 的 `EventTime`（归档时间晚于崩溃时间），因此上一轮游戏留下的旧报告不会被误关联到本次附加。此时：

1. 先临时把该游戏的 `speed` 配置项删掉，重启 GameHelper；
2. 完整启动一次游戏，让 BepInEx 重建 interop 缓存；
3. 恢复 `speed` 配置，之后启动会重新被门控延迟附加，两者可长期共存。

## Cheat Engine 一次性设置

生成的表通过 Lua 调用 Cheat Engine 的 `openProcess` 和 `speedhack_setSpeed`。如果 Cheat Engine 启用了“Ask to run lua scripts from tables”，加载时仍会出现确认框。若希望完全自动化，可在 Cheat Engine 的常规设置中关闭这项询问；这会允许所有已打开表的 Lua 脚本执行，因此只应打开来源可信的表。

Cheat Engine 与 GameHelper 应以足以访问目标游戏的相同或更高权限运行。部分游戏自身的计时实现不兼容通用 speedhack，此时配置可能无法生效。

## 安全边界

该功能用于离线单机游戏或自研程序。不要对联网游戏或启用了 EAC、BattlEye、ACE 等反作弊系统的游戏配置 `speed`；这可能违反游戏协议并造成封禁。

## 游戏先于监控启动

ETW 建立会话后会按配置中的进程名补扫已运行游戏，沿用完整路径匹配与 mod loader 附加门控。已有在途快照的进程不会重复接管；没有快照时从本次接管开始计时，并应用 speed。后续排队的同次启动事件不会重复附加。

诊断 CE 表必须用无 BOM UTF-8，并以 <?xml 开头；CE 会把带 BOM 的 .ct 错认成压缩 trainer，报 data error。项目生成器已保持这个格式。

## Unity 游戏重复附加

CE 7.7 的 SpeedhackV3 会复用游戏中已存在的 Unity 钩子，但新 CE 实例不会自动恢复 set_timeScale 方法句柄。GameHelper 在这个分支重新连接 Unity 元数据并刷新当前正数 timeScale；暂停的 0 不会被主动恢复。乘法钩子传入基准 1，再由钩子乘配置倍速，避免 1.5 被重复乘成 2.25。普通时钟钩子及首次附加继续沿用 CE 原路径。

可设置 GAMEHELPER_TEST_CHEAT_ENGINE_EXE 指向本机 CE，运行 dotnet test GameHelper.sln --filter FullyQualifiedName~GeneratedLua_ReusedUnityHook。测试在 CE 自身 Lua 中执行真实生成脚本，但用隔离的进程 API 桩模拟重附加，不打开任何游戏。覆盖乘法/替换钩子、暂停与普通时钟路径。
