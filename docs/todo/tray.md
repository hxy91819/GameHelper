可以，而且常见上有三条路：**改程序本身做成托盘应用**、用第三方工具把控制台窗口收进托盘，或者退一步只做“最小化启动”。[1][2][3]
其中真正符合“启动后隐藏到右下角托盘”的，通常是前两种；单纯把窗口设为最小化，往往只是缩到任务栏，不算托盘驻留。[4][1]

## 可行方案

| 方案 | 能否真正进托盘 | 适合场景 |
|---|---|---|
| 程序自己实现托盘图标 | 可以，PowerShell 示例里是用 Windows Forms 的 `NotifyIcon` 做托盘驻留，再配合隐藏窗口启动。[1] | 你能改源码，或者本来就是自己写的脚本/小工具。[1] |
| 第三方托盘工具 | 可以，Traymond 支持把任意窗口最小化到系统托盘，Tray.exe 这类工具则专门照顾 CMD/控制台程序。[2][3] | 不能改程序，但想把现有命令行窗口藏起来。[2][3] |
| 快捷方式“最小化运行” | 不完全算，只是最小化窗口，仍可能出现在 Alt-Tab 或任务栏里。[1] | 只是不想开机时弹黑框，对“托盘驻留”要求不高。[1] |

## 自己实现

如果这个命令行程序是你自己写的，最稳的方案是把它改成“后台逻辑 + 托盘壳”的形式，也就是运行时不依赖可见控制台窗口，而是由托盘图标负责显示状态、菜单和退出操作。[1]
针对 PowerShell，已有可行做法是在脚本里加入 Windows Forms 托盘逻辑，再用 `powershell.exe -WindowStyle Hidden` 启动，这样登录后不会弹出命令行窗口，而是直接驻留托盘。[1]

## 第三方工具

如果你不想改程序，Traymond 这类工具比较通用：它可以把任意窗口最小化到系统托盘，双击托盘图标恢复，还支持“自动将窗口最小化到托盘”和按窗口标题、类名、程序路径配置规则。[2]
另一类更贴近控制台场景的工具是社区里的 Tray.exe 方案，它的特点是单击托盘图标即可显示/隐藏黑窗口，不占任务栏，并且能跟随关联的 CMD 窗口一起退出。[3]

## 需要注意

“最小化”和“托盘化”不是一回事；有些程序号称隐藏后，实际效果只是任务栏里还留着一个黑窗口。[4][1]
在 Windows 11 的实际案例里，确实有人遇到“启动后未真正隐藏到托盘，只是最小化到任务栏黑窗口”的情况，所以如果你要的是稳定的托盘驻留，最好选程序原生支持或专门的托盘工具，而不是只改启动方式。[4]

## 怎么选

- 你能改代码：优先自己做托盘，效果最干净，也最可控。[1]
- 你不能改代码：先试 Traymond；如果目标是纯 CMD/脚本窗口，Tray.exe 这类方案更对口。[2][3]
- 你只是想别弹窗：把快捷方式设成“最小化运行”最省事，但它不等于托盘驻留。[1]

如果你的程序是 `cmd / bat / powershell / python` 里的某一种，我可以直接给你一套最合适的落地写法。

Sources
[1] 如何让开机启动的PowerShell循环脚本运行时最小化或驻留系统托盘？ https://www.volcengine.com/article/141185
[2] 增强版Traymond 让任意窗口最小化到系统托盘 - 白汤四物 https://www.fournoas.com/posts/traymond-minimize-any-window-to-the-system-tray/
[3] 【Tray】把脚本窗口、控制台程序收纳到托盘图标 - 小众软件论坛 https://meta.appinn.net/t/topic/79711
[4] Windows 11 下启动后未能真正隐藏到托盘，仅最小化到任务栏黑窗口 https://github.com/HaujetZhao/CapsWriter-Offline/issues/365
[5] 有没有什么办法能强制让应用程序启动时就最小化到托盘里？ : r/kde https://www.reddit.com/r/kde/comments/oi06kg/is_there_a_way_to_force_that_an_application/
[6] 完全隐藏Win 10托盘中自带的安全中心图标原创 - CSDN博客 https://blog.csdn.net/qq_24880013/article/details/127830052
[7] 在Windows 系统托盘中隐藏代理 https://learn.workforceexperience.hp.com/docs/zh-cn/how-to-hide-the-client-in-the-windows-system-tray
[8] Windows 命令提示符信息及其使用方法列表 - Dell https://www.dell.com/support/kbdoc/zh-cn/000130703/windows-%E5%91%BD%E4%BB%A4%E6%8F%90%E7%A4%BA%E7%AC%A6%E4%BF%A1%E6%81%AF%E5%8F%8A%E5%85%B6%E4%BD%BF%E7%94%A8%E6%96%B9%E6%B3%95%E5%88%97%E8%A1%A8
[9] 将控制台窗口最小化到系统托盘的实现 - CSDN博客 https://blog.csdn.net/weixin_30205153/article/details/148572634
[10] 有没有更简单的方法来运行命令行程序，而不用让它自动关闭？ - Reddit https://www.reddit.com/r/windows/comments/b9t18k/is_there_an_easier_way_to_run_command_line/
[11] 使用Windows 系统托盘管理网站和应用程序 - Microsoft Learn https://learn.microsoft.com/zh-cn/iis/extensions/using-iis-express/using-the-windows-system-tray-to-manage-websites-and-applications
[12] 对Windows 终端使用命令行参数 - Microsoft Learn https://learn.microsoft.com/zh-cn/windows/terminal/command-line-arguments
[13] CN102567105A - 隐藏Windows系统托盘的方法 - Google Patents https://patents.google.com/patent/CN102567105A/zh
[14] Windows 10 Pro 64位自定义Dock环境下如何关闭/完全隐藏任务栏 https://www.volcengine.com/article/532483
[15] Windows命令行常用命令- 明王不动心 - 博客园 https://www.cnblogs.com/yangmingxianshen/p/9518424.html