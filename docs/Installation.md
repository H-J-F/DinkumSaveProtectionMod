# 安装、升级、卸载与恢复

适用版本：**0.1.5 预览版**。支持 Steam Build 25568182、Windows x64 Unity Mono、新格式 Player.es3。已完成离线验证，实机验收待完成。[English guide](../README.en.md)

## 安装与构建

先备份整个存档目录并完全关闭游戏，单独安装 BepInEx 5.4.23.5 x64。解压发布包，把根目录的 `DinkumSaveProtection.dll` 和 `install-manifest.json` 放入 `BepInEx/plugins/DinkumSaveProtection/`，其他文档和 tools 留在解压目录。升级时替换这两个文件，不删除配置和缓存。

需要自行构建时，安装 .NET 10 SDK：

```powershell
./scripts/build.ps1 -GamePath 'C:\Games\Dinkum'
```

路径必须替换为实际游戏目录。脚本只读引用游戏程序集，下载并核验 BepInEx 的固定官方 ZIP，运行离线测试，输出 `dist/DinkumSaveProtection-0.1.5.zip` 和 `.zip.sha256`。不会启动游戏、部署插件或改写真实存档。

## 管理工具

在发布包解压目录运行，需要 .NET 10 Runtime：

```powershell
dotnet ./tools/ProtectionManager.dll inspect --game 'C:\Games\Dinkum'
dotnet ./tools/ProtectionManager.dll install --game 'C:\Games\Dinkum' --package '.'
dotnet ./tools/ProtectionManager.dll rollback --game 'C:\Games\Dinkum'
dotnet ./tools/ProtectionManager.dll uninstall --game 'C:\Games\Dinkum'
```

inspect 只读。安装、回滚、卸载前必须关闭游戏。工具仅通过程序集元数据检查文件，不执行游戏或 Mod DLL；不安装或替换加载器、core、winhttp.dll、全局配置及其他 Mod。重复 GUID/程序集、版本或清单不匹配、受管理文件被改动、重解析点均拒绝覆盖。

升级备份位于游戏目录 `BepInEx/DinkumSaveProtection-backups/`。卸载只删除清单所管理的文件，保留配置、其他文件和玩家缓存。手动卸载可将本插件文件夹移出 plugins。若要在卸载后使用恢复进度，请先确认原版保存成功。

## 恢复资格

| 上次退出原因 | 当前主机会话 | 处理 |
|---|---|---|
| 主动返回菜单或正常退出 | 相同 | 可恢复，客户端重启不影响 |
| 主动退出 | 改变或无法确认 | 使用原版存档，不重放旧缓存或主机副本 |
| 闪退或意外断线 | 相同或改变 | 可恢复，仍执行完整性、适配器及历史冲突检查 |

支持的插件主机每次开房生成新会话标识；原版或旧协议主机使用主机 SteamID + LobbyID，重新开房或房主变化视为新会话。LAN 无法确认会话，主动退出后不恢复。意外断线后再点击返回菜单，不会改判成主动退出。

主动退出先提交最后有效快照和退出标记，成功后继续退出，最多等待 2 秒；失败或超时取消本次主动退出并记录日志。正常连接时的原版保存保持自身流程。异常断线后不把正在清理的角色状态直接写回原版文件。

“不恢复旧缓存”不会回滚或删除原版已经成功保存的进度。采用原版后建立新的保护分支，旧候选保留检查。

## 0.1.5 断线后显式保存

房主退出后，客户端点击“保存并回到主菜单”，会等待最近完整缓存，校验并转换后后台写入 Player.es3，显示成功后才返回。仅点击“返回主菜单”不写原版文件。

没有有效缓存、缓存落后或分叉、原文件被外部改动、转换可能丢字段时拒绝覆盖。鼠标物品及原版不保存的 idol 装备需要空背包格，导出会自动放入；空位不足时需重新加入整理后再保存。额外私人储藏和扩展字段不能保证由原版读取，仍保留在缓存；自定义物品仍需对应内容 Mod。

保存中请等待，不要结束进程。磁盘写入在后台进行，内存转换和序列化仍有主线程开销。原版备份保存在下文 native-exports 目录，未完成事务的 `native-export.pending` 不要自行删除。

## 配置与存储

配置位于游戏目录 `BepInEx/config/io.dinkumsaveprotection.playerdata.cfg`。游戏数据根目录是 `Application.persistentDataPath`，Windows 通常为 `%USERPROFILE%\AppData\LocalLow\James Bendon\Dinkum`。其下包含：

- `DinkumSaveProtection/characters/<SteamID>/slotN/<角色UUID>/`：角色缓存，包含 `resume.bin` 退出与恢复元数据。
- 上述角色目录的上一级 `identity.bin`：身份、原版文件指纹和检查点。备份时保留整个 slot 目录。
- `DinkumSaveProtection/host/<世界UUID>/<SteamID>/<角色UUID>/`：主机副本。
- `DinkumSaveProtection/native-exports/<角色UUID>/<操作UUID>/`：原版导出备份。

每角色一个提交中快照和一个最新待提交快照。主线程采集并校验，后台编码、哈希和写盘；250ms 合并、普通采集最高 1Hz、直接字段每 5 秒巡检。强杀可能丢失最后成功提交之后的变化。

本地保留最近 32 个完整版本及正常保存检查点，分叉候选不自动删除。旧清单移入 history，不保证其数据块始终可用。主机历史不自动裁剪，需自行管理空间。恢复前在自身 recovery 目录保存原版文件和来源清单；失败保持加载锁并保留来源。

## 冲突和旧缓存

原版文件指纹与已知检查点不同，默认暂停。关闭游戏、备份并核对后，在配置 `[Recovery]` 节选择：

- `Choice = Native`：采用原版，建立新起点。
- `Choice = Backup`：采用合格缓存。
- `Choice = Pause`：继续等待，默认值。

主机与客户端历史分叉时也默认暂停，可设置 `Source = Local` 或 `Host` 明确选端。这些选项在会话资格检查后执行，不能恢复已放弃会话的数据，成功加载后重置。

0.1.0 缓存缺少退出与主机会话元数据，升级后不自动恢复，需检查后明确选择。损坏 resume.bin 不回退到旧 Running 记录；身份文件可在验证后使用 .previous。

离线查看和选择分支：

```powershell
dotnet ./tools/ProtectionManager.dll list --store '<角色UUID目录>' --character '<角色UUID>'
dotnet ./tools/ProtectionManager.dll select --store '<角色UUID目录>' --character '<角色UUID>' --snapshot '<哈希>'
```

选择分支需关闭游戏，只辅助解决历史分叉，不改变退出策略。自动恢复依据 resume.bin 指针，不会仅因目录中另有更大序号就采用它。

原版损坏时，只有通过完整检查的 .bak/.bac 才能用于修复，原文件先隔离；没有有效候选、映射改变或适配器缺失时暂停，不保存默认状态。个人缓存不能重建缺失的世界文件。

## 实机验收建议

先备份完整存档并使用测试角色。调整背包，等待至少 10 秒，让房主退出，客户端点“保存并回到主菜单”并等待成功；关闭游戏，把本插件移出 plugins，再检查原版背包、余额和成长。不要同时移除内容 Mod 或删除缓存。

还需验证主动退出的同会话/新会话重连、客户端重启、真实闪退、跨日、主客机均装/客户端单装及具体 Mod 组合。离线测试不代表这些场景已通过。
