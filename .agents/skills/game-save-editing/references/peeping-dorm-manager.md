# Peeping Dorm Manager：已验证格式

适用样本：v1.0.9，Build 14020192，Unity 2021.3.33f1，Windows IL2CPP。更新版、其他发行包需重新验证。

## 存档与恢复

`app.info` 的公司/产品为 `Horny Doge` / `Peeping Dorm Manager`。正常路径候选：

```text
%USERPROFILE%/AppData/LocalLow/Horny Doge/Peeping Dorm Manager/
  GameSave_0.dat
  SystemSave.dat
  Player.log
  Player-prev.log
```

`GameSave_0.dat` 是本次使用的进度槽；`SystemSave.dat` 包含系统记录与最后使用的槽信息。先读槽信息或游戏代码，不能把槽 0 当作所有用户的目标。

本次丢档表现：普通启动目录缺少 `GameSave_0.dat`，Codex 私有缓存中仍有凌晨保存的完整副本。备份普通启动目录后恢复进度及配套系统文件，用户确认恢复正确。私有缓存中的 `LocalCache/LocalLow` 路径与普通路径可能被同一个工具视图合并，具体排查见 [新游戏分析](new-game.md)。

## 编解码格式

文件为 UTF-8 Base64 文本，解码后布局为：

```text
8 字节 IV || DES-CBC 密文
```

- 算法：DES-CBC，8 字节块，PKCS#7 padding。
- 派生：PBKDF2-HMAC-SHA1，1000 次，盐为头部 IV，输出 8 字节 DES key。
- 本版本密码：`PeepingHG20221124`，以 UTF-8 字节派生。
- 明文：UTF-8 JSON。反向处理为 padding → DES-CBC → 前置 IV → Base64。
- 改写副本时可以保留原 IV，便于证明无修改往返逐字节相等；新编码仍需反解核验。

参数证据来自 `LuXuan.SaveSystem.EncryptionUtility_Standalone.Encrypt/TryDecrypt`：加密创建 DES，取得 IV，以该 IV 作 Rfc2898DeriveBytes 的 salt，迭代 1000 次取 8 字节 key，先写 IV 再写加密明文。候选密码经完整解密与原密文往返确认，不是仅凭字符串猜测。

本样本可定位的方法 RVA（仅作该版本分析线索）：

| 方法 | RVA |
| --- | --- |
| SavedDataStorer_Standalone.StoreData_Game | 0x59AC00 |
| EncryptionUtility_Standalone.Encrypt | 0x58B980 |
| EncryptionUtility_Standalone.TryDecrypt | 0x58BDD0 |

## 金钱字段

外层结构包含 `version`、`saveOnSceneName`、`datas`。在 `datas` 中按 `key == "Protagonist"` 选择记录；其 `data` 是嵌套 JSON **字符串**，解析一次后取：

```text
walletData.ownMoney
```

`CharacterWallet.SavedData.ownMoney` 类型为 float（单精度），不是整数或加密包装字段。修改后保留 `data` 的字符串层级、体力和人物资料以及所有其他记录；可以只替换该字段的数值文本，再比较完整解析结构。不能对整个文件全局替换显示数字。

单精度数值较大时不能精确表达每一个整数；52,300,000 可以精确表示，但附近的小额增减可能受浮点精度影响。按用户指定值修改时，仍应说明存储类型带来的限制。

## 本次验证范围与未解决的差异

- 游戏正常退出后，读取实际目录的最终文件，备份后修改。
- 用户之前报告画面金钱为 523；最终落盘样本的 `ownMoney` 为约 124.90008544921875。该差异未在游戏内核实，不能声称原存档值确认为 523。
- 已把最终存档的该字段设为 52,300,000；无修改编解码与原密文一致，修改后反解、完整结构差异及实际目标文件回读均通过。
- 存档恢复已获用户确认；金钱修改的游戏内显示、实际消费与再次保存尚未获得确认。后续使用本资料时须区分文件验证与游戏行为验证。

只把参数用于匹配该版本的存档副本。参考资料不附真实玩家数据或内存转储。
