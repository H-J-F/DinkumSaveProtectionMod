# Dinkum Save Protection Mod

[English](README.en.md) · **简体中文**

为 Dinkum 联机玩家提供个人进度缓存和恢复的 BepInEx 插件。客户端独立安装即可使用本地保护；兼容主机也安装本插件时，可保存额外的主机副本。

**当前版本：0.1.5 预览版。** 已通过 87 组离线测试及临时目录安装隔离测试；Unity 实际生命周期、Steam 多机、真实闪退与具体 Mod 组合仍待实机验收。请先备份存档，在测试角色上验证。

[下载 v0.1.5 预览包](https://github.com/H-J-F/DinkumSaveProtectionMod/releases/tag/v0.1.5) · [报告问题](https://github.com/H-J-F/DinkumSaveProtectionMod/issues)

## 功能

- 为背包、光标物品、装备、钱包与银行、外观、成长、许可证、配方、任务、图鉴、关系、邮件、私人储藏及基础生命体力保存独立快照。
- 后台提交缓存，校验完整性并保留历史；内容不变时不新增版本。事件合并 250ms，普通采集最高 1Hz，直接字段变化每 5 秒巡检。
- 根据退出原因和主机会话决定是否恢复，遇到历史分叉、文件改动或不兼容数据时暂停，避免盲目覆盖进度。
- 0.1.5 支持断线后点击“保存并回到主菜单”，将合格的完整缓存投影到原版 `Player.es3`；成功后返回菜单。
- 提供独立安装、升级、回滚、卸载管理工具，以及第三方个人数据适配器接口。

缓存只能恢复最后成功提交的版本，不能保证保留强杀前的每一次变化。个人缓存不能重建丢失的世界文件，也不提供跨世界交易原子性或自动补发物品。

## 支持环境

| 项目 | 当前支持 |
|---|---|
| 游戏 | Dinkum Steam Build **25568182**，新格式 `Player.es3` |
| 平台 | Windows x64，Unity Mono |
| 加载器 | [BepInEx 5.4.23.5 x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) |
| 插件目标框架 | .NET Standard 2.1 |
| 构建 / 管理工具 | .NET 10 SDK / .NET 10 Runtime |

游戏程序集指纹不匹配时插件会停用补丁。其他游戏版本、IL2CPP、Linux、macOS 及任意 Mod 组合未获验证。游戏和加载器需自行安装，本包不包含这些依赖。本项目为非官方社区 Mod。

## 安装、升级与卸载

1. 备份整个 Dinkum 存档目录，完全退出游戏，并单独安装 BepInEx。
2. 从上方预发布页面下载 ZIP 和 `.sha256`。使用 `Get-FileHash .\DinkumSaveProtection-0.1.5.zip -Algorithm SHA256` 与校验文件比对。
3. 解压到临时目录，将根目录的 `DinkumSaveProtection.dll` 和 `install-manifest.json` 放入游戏目录的 `BepInEx/plugins/DinkumSaveProtection/`。不要把 ZIP 中的 `tools/` 放入 plugins。
4. 启动后检查 BepInEx 日志中的插件版本和兼容提示。升级时关闭游戏，仅替换上述两个文件，保留配置与缓存。

也可以在解压目录使用管理工具，以下路径请替换为实际游戏位置：

```powershell
dotnet ./tools/ProtectionManager.dll inspect --game 'C:\Games\Dinkum'
dotnet ./tools/ProtectionManager.dll install --game 'C:\Games\Dinkum' --package '.'
dotnet ./tools/ProtectionManager.dll rollback --game 'C:\Games\Dinkum'
dotnet ./tools/ProtectionManager.dll uninstall --game 'C:\Games\Dinkum'
```

管理工具需要 .NET 10 Runtime，安装/回滚/卸载前必须关闭游戏。缺少加载器、重复 GUID 或受管理文件被改动时会拒绝覆盖。只管理自身 DLL 与安装清单，保留其他 Mod、加载器、配置和玩家缓存。手动卸载时，将本插件目录移出 plugins。

**卸载前若希望原版保留恢复进度，应先确认原版保存成功。独立缓存不会因卸载自动写入原版文件。** 内容 Mod 的物品仍需要对应 Mod。详见[安装与恢复说明](docs/Installation.md)。

## 恢复规则

| 上次退出原因 | 重新加入时的主机会话 | 自动恢复资格 |
|---|---|---|
| 主动退出 | 相同 | 允许，包括客户端重启后 |
| 主动退出 | 改变或无法确认 | 不重放旧缓存，使用原版存档 |
| 闪退 / 意外断线 | 相同或改变 | 允许，仍须通过数据和历史冲突检查 |

支持的插件主机每次开房生成会话标识；原版或旧协议主机使用 SteamID + LobbyID 判断。LAN 无法确认会话时，主动退出后不自动恢复。不恢复旧缓存不会删除或回滚已成功保存的原版进度。

断线后，“返回主菜单”不写原版存档；“保存并回到主菜单”会等待合格缓存并验证导出，保存成功后才返回。若缓存落后/分叉、原文件被改动、数据转换可能丢字段或背包没有空间容纳光标物品等情况，会拒绝覆盖。额外扩展字段继续保存在独立缓存中。

冲突默认暂停。检查并备份后，可在配置的 `[Recovery]` 节设置 `Choice = Native`（采用原版）、`Choice = Backup`（采用合格缓存）或 `Choice = Pause`；历史分叉可设置 `Source = Local` 或 `Host`。这些选项不能绕过会话资格，成功加载后会重置。不要在不清楚数据来源时强行选择。

## 配置和数据位置

- 配置：游戏目录内 `BepInEx/config/io.dinkumsaveprotection.playerdata.cfg`。
- Windows 存档根目录通常为 `%USERPROFILE%\AppData\LocalLow\James Bendon\Dinkum`，即游戏的 `Application.persistentDataPath`。
- 本地缓存：存档根目录内 `DinkumSaveProtection/characters/<SteamID>/slotN/<角色UUID>/`，身份文件 `identity.bin` 位于角色目录的上一级；备份时保留整个 slot 目录及 `resume.bin`。
- 主机副本：`DinkumSaveProtection/host/<世界UUID>/<SteamID>/<角色UUID>/`。
- 原版导出备份：`DinkumSaveProtection/native-exports/<角色UUID>/<操作UUID>/`；不要手动删除未完成事务的 `native-export.pending`。
- 插件升级备份：游戏目录内 `BepInEx/DinkumSaveProtection-backups/`。

本地保留最近 32 个完整版本及正常保存检查点，分叉候选保留检查。主机历史不自动裁剪，需要自行管理空间。

## 从源码构建与测试

需要 Windows、PowerShell、.NET 10 SDK 和上述受支持的本地游戏安装：

```powershell
git clone https://github.com/H-J-F/DinkumSaveProtectionMod.git
cd DinkumSaveProtectionMod
./scripts/build.ps1 -GamePath 'C:\Games\Dinkum'
```

脚本从官方固定版本下载并校验 BepInEx 到本地 `.cache/`，从游戏目录只读引用程序集，编译插件与管理工具，运行离线测试和临时安装夹具，生成 `dist/DinkumSaveProtection-0.1.5.zip`、`.zip.sha256` 及构建回执。不会启动游戏、安装插件或写入真实存档。公开源码和构建无需任何额外分析资料。

不持有游戏也能运行 86 组独立离线测试；提供受支持的游戏目录时额外执行 1 组只读兼容检查：

```powershell
dotnet run --project tests/DinkumSaveProtection.Tests -c Release
dotnet run --project tests/DinkumSaveProtection.Tests -c Release -- 'C:\Games\Dinkum'
```

`-SkipTests` 仅用于本地快速构建，不能作为发布验证依据。详见[验证范围](docs/Verification.md)。

## 第三方 Mod 与隔离

插件使用独立 GUID、程序集、Harmony owner、网络消息和目录，只分发自身产物。BepInEx 插件共享游戏进程，独立目录不等于运行时沙箱；不能承诺任意插件组合兼容。

第三方扩展的个人字段需实现 `IPlayerDataAdapter`，通过 `PlayerDataAdapters.Register(adapter)` 注册，变更时调用 `PlayerDataAdapters.NotifyChanged()`。采集、验证和恢复均在主线程；适配器应完整验证数据并幂等恢复。缺失或不兼容适配器会暂停恢复。接口和约束见[兼容与适配器文档](docs/Compatibility.md)。

## 反馈与许可

请在 [Issues](https://github.com/H-J-F/DinkumSaveProtectionMod/issues) 提供游戏 Build、插件与 BepInEx 版本、相关 Mod 列表、复现步骤和脱敏后的日志片段。请勿公开完整存档、SteamID 或其他私人信息。

本项目原创源码、管理工具及文档采用 [MIT 协议](LICENSE)。Dinkum、Unity、BepInEx 及其他第三方组件保留各自权利和许可，不因本仓库的 MIT 协议而重新授权。
