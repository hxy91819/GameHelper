# 倍速方案调研（未立项）

本文档保留在 `docs/plans/`，表示它仍是调研草案，不属于当前已批准 PRD 或架构基线。

在 Windows 上给任意游戏做“倍速”（speedhack）的开源方案主要还是原生 C/C++ 实现，并没有成熟的、纯 C# 的“一行引用就能全局加速任意游戏”的组件；通常做法是用 C# 做外壳/界面，底层依赖注入到目标进程的原生 DLL 来实现倍速功能。Cheat Engine 本身和第三方的 speedhack 库都可以作为参考或被间接复用。[1][2][3]

> 先提醒一下：对网络游戏或带反作弊系统的游戏做 speedhack 很容易违反用户协议并导致封号甚至触法，下面内容适合在本地离线/自制程序、学习和实验场景使用。

***

## 基本原理概览

绝大多数通用 speedhack 的核心思路是：在目标进程内部拦截其获取时间的 API（如 `GetTickCount`、`QueryPerformanceCounter`、`timeGetTime`、`gettimeofday` 等），然后用一个“加速后的虚拟时间”替代真实时间。[3][1]

例如 Cheat Engine 的新 speedhack 实现思路大致是：在注入的 DLL 中记录一个基准真实时间，再根据用户设置的倍速系数计算返回值：返回时间 ≈ 基准时间 + (当前真实时间 − 基准时间) × 速度倍数。[2][1]

***

## 开源倍速组件

目前比较典型、能参考或复用的开源 speedhack 项目主要是原生代码：

- **Cheat Engine 的 speedhack 源码**  
  Cheat Engine 是开源的，官方论坛明确给出了它的 speedhack 源码目录链接，可直接浏览实现细节。[2]
  其中包含了注入 DLL、获取当前时间、按倍速计算虚拟时间并 hook 系统计时函数等逻辑，思路上就是前面说的“基准时间 + 系数”模型。[1][2]

- **absoIute/Speedhack（C++ 轻量实现）**  
  GitHub 上有一个 `absoIute/Speedhack` 仓库，定位是“轻量级 speedhacking 源码”。[3]
  它依赖 Microsoft Detours，在目标进程内部 hook 时间相关函数，然后通过 `Speedhack::SetSpeed(…)` 设置速度倍数；要作用到其它进程，需要把它编译为 DLL 并注入。[3]

这两种都是“要在目标进程内部运行”的原生实现，本身与 C# 无关，你若用 C#，常见模式是“C# 程序 + 注入的 C++ DLL”。

***

## C# 可用库（更多是内存修改）

和 “时间 hook / 倍速” 这种深度行为相比，C# 生态里更常见的是做“游戏修改器 / Trainer”的内存读写库，例如：

- **memory.dll（erfg12/memory.dll）**  
  一个专门为 PC 游戏 Trainer 准备的 C# 内存操作库，封装了 `OpenProcess`、`ReadProcessMemory`、`WriteProcessMemory` 等底层 API，并提供 FreezeValue 等高级功能。[4]
  它适合做“血量、子弹、金钱”之类数值型修改，但并不直接实现像 Cheat Engine 那样的全局 speedhack（没有帮你拦截 `GetTickCount` 之类 API）。[4]

另外，社区里大量教程（YouTube 等）会教你用 C# P/Invoke 调 WinAPI：`OpenProcess`、`WriteProcessMemory`、枚举模块、做多级指针寻址等，本质还是“变量修改”，而不是时间函数 hook。[5][6][7][8]

因此，如果你坚持 C# 技术栈，比较现实的路线是：

- 用 C# + memory.dll / 自己的 P/Invoke 做**普通内存修改**；
- 对于“倍速”，在原生层（C++）写 DLL 并注入，再暴露一个简单的接口给 C# 调用（例如导出 `SetSpeed(double speed)`），而不是指望一个纯托管库就搞定。[4][3]

***

## 内嵌 Cheat Engine 的可行性

你问的“能否内嵌类似 Cheat Engine 这样的软件”，大致有三种方向，各有优缺点：

1. **直接利用 Cheat Engine 自身（不真正“嵌入”）**  
   - CE 支持 Lua 脚本和 Auto Attach、自动设置 speedhack 等能力，你可以写 Lua 脚本让它在附加到指定进程后自动调整速度。[9][10]
   - 然后通过外部脚本、批处理或 C# 起一个辅助进程来启动游戏和 CE，并传参数/脚本给 CE 执行。[9]
   - 这种方式不是 DLL 级嵌入，但开发成本最低。

2. **基于“Cheat Engine core DLL 封装”的第三方项目**  
   - 有社区作者做过一个“基于 Cheat Engine 核心的 DLL 库”，把 CE 核心嵌进一个 DLL，导出一组函数，让你在 C/C++/C# 里调用（包含注入脚本、操作虚拟 cheat table 等）。[11]
   - 该项目是开源的，但并非官方维护，示例主要是 C++，C# 使用需要你自己 P/Invoke；同时它直接捆绑 CE 内核，意味着需要认真看清原项目和 CE 的许可证以及安全/兼容性问题。[11]

3. **只借鉴 CE 的实现思路，自行实现“CE-lite”**  
   - Cheat Engine 是开源的，你可以直接阅读它的 speedhack 源码目录（论坛给出了具体路径）。[2]
   - 一个常见做法是：参考 CE 或 `absoIute/Speedhack` 的实现，在 C++ 中写一份精简版 speedhack DLL（只做时间 hook，不带 CE 其它复杂功能），编译为 32/64 位 DLL，然后：
     - 用 C# 做界面和业务逻辑；
     - 在 C# 中通过 P/Invoke 或单独的小注入器 EXE 把这个 DLL 注入到目标进程；
     - 通过导出的函数（比如 `SetSpeed`）或 IPC 调整倍速。[5][3]
   - 这种模式下，“嵌入”的是你自家的原生 DLL，而不是完整的 CE，本质更可控，也更容易规避许可证和安全问题。

另外，你也可以站在 CE 的另一侧：给 CE 写 C# 插件。CE 仓库和 Issue 里有 C# 插件模板示例，说明 CE 本身支持用 C# 写插件。  不过这是“在 CE 里嵌入 C#”，而不是“在你的 C# 程序里嵌入 CE”，方向相反。[12]

***

## 实践建议

综合目前的开源生态，如果你的目标是：

- **单机/自研项目，想做一个可控的倍速功能，同时保持 C# 为主技术栈：**

  - 选用模式：“C# UI + 原生 speedhack DLL” 比较现实。  
  - speedhack DLL 可以基于 `absoIute/Speedhack` 或参考 Cheat Engine 的 speedhack 源码自行裁剪实现。[2][3]
  - C# 侧用 P/Invoke 调用 DLL 导出的简单接口即可，内存读写则可选用 memory.dll 等库。[4]

- **只是偶尔给自己玩的单机游戏倍速，不想维护代码：**

  - 直接使用 Cheat Engine 的 speedhack 功能，或写一小段 Lua/脚本让 CE 自动附加并设置倍速，配合批处理/PowerShell 启动即可，无需自己造轮子。[10][9]

无论采用哪种方案，都要注意：

- 避免用于网络游戏或任何带反作弊的环境，以免触犯协议或法律；
- 仔细阅读各个开源项目的许可证（CE、absoIute/Speedhack、第三方 CE 库等），确保与你的项目授权模式相容。[11][3][2]

如果你愿意具体说说目标场景（单机/网络、自研引擎/现成商用游戏、是工具内部用还是要发布给用户），我可以帮你更细化一下架构选型和“哪部分用 C#、哪部分必须用 C++”。

来源
[1] How does CheatEngine's speed hack work? [closed] - Stack Overflow https://stackoverflow.com/questions/17512906/how-does-cheatengines-speed-hack-work
[2] View topic - CheatEngine speedhack functionality - Cheat Engine https://forum.cheatengine.org/viewtopic.php?p=5740375
[3] absoIute/Speedhack - GitHub https://github.com/absoIute/Speedhack
[4] erfg12/memory.dll: C# Hacking library for making PC game trainers. https://github.com/erfg12/memory.dll/
[5] Simple C# Memory Library for Game Hacking - YouTube https://www.youtube.com/watch?v=VKaudl5_3w8
[6] Hack ANY game with C# under 10 minutes! [ Tutorial ] - YouTube https://www.youtube.com/watch?v=If7TTn24uLc
[7] Making a game cheat trainer from scratch in C# - YouTube https://www.youtube.com/watch?v=OKJsbDDh5CE
[8] Make Ur Own Cheat-Trainer in c# [2021] - YouTube https://www.youtube.com/watch?v=w9m0gmcS82Y
[9] Auto SpeedHack on process open : r/cheatengine - Reddit https://www.reddit.com/r/cheatengine/comments/18lb42f/auto_speedhack_on_process_open/
[10] View topic - Help making simple speedhack for games - Cheat Engine https://cheatengine.org/forum/viewtopic.php?p=5353196&sid=
[11] How to use cheat engine as a dll library [delphi,c++,c,c# ... - MPGH https://www.mpgh.net/forum/showthread.php?t=829888
[12] Is using .NET 9 Possible in C# Plugin? · Issue #3207 · cheat-engine ... https://github.com/cheat-engine/cheat-engine/issues/3207
[13] Assult cube speed hack C# +Source - UnKnoWnCheaTs https://www.unknowncheats.me/forum/other-fps-games/444622-assult-cube-speed-hack-source.html
[14] Making a game cheat trainer from scratch in C# : r/csharp - Reddit https://www.reddit.com/r/csharp/comments/6yvtet/making_a_game_cheat_trainer_from_scratch_in_c/
[15] View topic - C# Trainer Tutorial (With Example) - Cheat Engine https://forum.cheatengine.org/viewtopic.php?t=530207
