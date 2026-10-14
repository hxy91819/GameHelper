# 连续游戏休息提醒

ConsoleHost 和 WinUI 在启动监控后，可以按游戏处于前台的时间提醒休息。先把游戏加入配置并保持 `enabled: true`，再在 `config.yml` 添加：

```yaml
breakReminder:
  enabled: true
  playMinutes: 40
  breakMinutes: 5
  repeatMinutes: 10
  sound: true
```

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| `enabled` | `false` | 是否启用休息提醒；省略整个配置段也关闭 |
| `playMinutes` | `40` | 第一轮提醒所需的前台游戏分钟数 |
| `breakMinutes` | `5` | 连续离开游戏多久才清零 |
| `repeatMinutes` | `10` | 首次提醒后，再累计多少前台游戏分钟进行下一次提醒 |
| `sound` | `true` | 是否请求播放系统默认通知提示音 |

三个分钟数必须为正整数，`validate-config` 会指出无效字段。省略字段使用默认值。第一版通过配置文件设置；修改在下一次启动监控或现有配置重载时生效，不自动监听文件。修改提醒设置会清空当轮计时，单纯更新游戏目录会保留累计值并刷新游戏识别。

## 计时规则

- 只采样前台游戏进程身份；看剧情、没有键鼠动作但游戏仍在前台时也算游戏时间。用户离开电脑而游戏仍在前台时，程序无法据此判断实际离开。
- 在不同游戏之间切换合并累计。后台运行、桌面和其它应用不累计；连续离开不足5分钟会保留本轮游戏时间。
- 例如游戏20分钟、切出2分钟、再游戏20分钟，会在累计40分钟时提醒；切出满5分钟，再回游戏则从零开始。
- 锁屏、安全桌面和睡眠按离开处理，睡眠时间不算游戏时间；读取失败或长时间缺少采样时暂停，不据此确认已完成休息。
- 达到40分钟时通知“已连续游戏40分钟，建议休息5分钟”，之后在50、60分钟等节点继续提醒。离开游戏时不发送提醒。
- 提醒计时只在内存中保存，停止监控或重启程序后重新开始。现有 CSV 历史统计仍按游戏进程会话记录。

## 系统通知

通知使用 Win32 `Shell_NotifyIconW`，临时托盘图标会在通知结束、取消或关闭功能后清理，不抢游戏焦点，也不修改 HDR 或游戏速度。点击通知不会打开应用或手动重置计时。监控启动前已运行的游戏同样可以参与前台提醒。

Windows 勿扰、全屏及通知音量设置可能抑制显示或声音，发送成功不代表用户已看到通知。程序尊重系统设置，不强行弹窗；被抑制的提醒不排队补发。发送失败会记录日志，并在下一次正常提醒节点再尝试。[微软通知接口说明](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)

## 验证

核心测试使用虚拟时间，不用真实等待40分钟，也不需要管理员权限。可按需执行原生桌面 API 检查（会发送两条测试通知，其中一条请求提示音）：

```powershell
$env:GAMEHELPER_VERIFY_BREAK_NOTIFICATIONS = '1'
dotnet test GameHelper.Tests/GameHelper.Tests.csproj --filter FullyQualifiedName~WindowsBreakReminderIntegrationTests
Remove-Item Env:GAMEHELPER_VERIFY_BREAK_NOTIFICATIONS
```

原生检查验证提交、取消和资源清理；实际游戏全屏下的显示和声音仍需目视、听觉确认。实机监控验证必须使用 `GAMEHELPER_DATA_DIR` 沙盒和 `GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE=1`，并保持用户实例运行，详见[验证部署流程](verify-and-deploy-workflow.md)。
