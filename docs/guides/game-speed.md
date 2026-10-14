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
```

- `cheatEngine.executable` 必须是 Cheat Engine 可执行文件的绝对路径。
- `speed` 必须是大于 `0` 的数字。`1.0` 是原速，`0.5` 是半速，`2.0` 是两倍速。
- 省略 `speed` 时，GameHelper 不启动 Cheat Engine，也不修改该游戏速度。
- 同一游戏出现多个匹配进程时，只在第一个活动进程上应用一次；游戏完全退出并再次启动后会重新应用。

GameHelper 将生成的临时表写入 `%AppData%\GameHelper\cheat-engine\`，表中只包含目标 PID 和倍速。旧表会自动清理。配置热重载后，新的倍速对下一次游戏启动生效。

## Cheat Engine 一次性设置

生成的表通过 Lua 调用 Cheat Engine 的 `openProcess` 和 `speedhack_setSpeed`。如果 Cheat Engine 启用了“Ask to run lua scripts from tables”，加载时仍会出现确认框。若希望完全自动化，可在 Cheat Engine 的常规设置中关闭这项询问；这会允许所有已打开表的 Lua 脚本执行，因此只应打开来源可信的表。

Cheat Engine 与 GameHelper 应以足以访问目标游戏的相同或更高权限运行。部分游戏自身的计时实现不兼容通用 speedhack，此时配置可能无法生效。

## 安全边界

该功能用于离线单机游戏或自研程序。不要对联网游戏或启用了 EAC、BattlEye、ACE 等反作弊系统的游戏配置 `speed`；这可能违反游戏协议并造成封禁。
