# Dinkum Save Protection Mod

**English** · [简体中文](README.md)

A BepInEx plugin that caches and restores personal progress for Dinkum multiplayer players. Client-only installation provides local protection; a compatible host running the plugin can also retain a host-side copy.

**Current version: 0.1.5 preview.** Validated with 87 offline tests and temporary-directory installer isolation tests. Real Unity lifecycle behavior, Steam multiplayer, actual crashes, and specific mod combinations still require in-game validation. Back up your saves and validate with a test character first.

[Download the v0.1.5 preview](https://github.com/H-J-F/DinkumSaveProtectionMod/releases/tag/v0.1.5) · [Report an issue](https://github.com/H-J-F/DinkumSaveProtectionMod/issues)

## Features

- Independent snapshots of inventory, cursor items, equipment, wallet and bank balances, appearance, progression, licences, recipes, quests, collections, relationships, mail, private storage, and basic health/stamina.
- Background cache commits with integrity checks and retained history. Unchanged content creates no new version. Changes are coalesced for 250ms, ordinary capture is limited to 1Hz, and direct field changes are audited every 5 seconds.
- Recovery eligibility based on exit reason and host session. Divergent history, externally changed files, or incompatible data pause recovery rather than silently overwrite progress.
- In 0.1.5, **Save and Return to Main Menu** after a disconnect can project an eligible complete snapshot into vanilla `Player.es3`, then return after successful saving.
- An isolated install/upgrade/rollback/uninstall manager and a personal-data adapter API for other mods.

Only the last successfully committed version is recoverable. Changes immediately before a forced termination may be lost. Personal snapshots cannot rebuild missing world files, provide cross-world transaction atomicity, or automatically replace world items.

## Supported environment

| Component | Current support |
|---|---|
| Game | Dinkum Steam Build **25568182**, new `Player.es3` format |
| Platform | Windows x64, Unity Mono |
| Loader | [BepInEx 5.4.23.5 x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) |
| Plugin target | .NET Standard 2.1 |
| Build / manager | .NET 10 SDK / .NET 10 Runtime |

Patches are disabled when the game assembly fingerprint does not match. Other game builds, IL2CPP, Linux, macOS, and arbitrary mod combinations have not been validated. Install the game and loader separately; neither is bundled. This is an unofficial community mod.

## Install, upgrade, and uninstall

1. Back up your entire Dinkum save directory, fully close the game, and separately install BepInEx.
2. Download the ZIP and `.sha256` from the preview release above. Compare `Get-FileHash .\DinkumSaveProtection-0.1.5.zip -Algorithm SHA256` against the checksum file.
3. Extract into a temporary directory. Copy the root `DinkumSaveProtection.dll` and `install-manifest.json` into `BepInEx/plugins/DinkumSaveProtection/` inside your game directory. Do not place the package's `tools/` directory in plugins.
4. On launch, check the plugin version and compatibility messages in the BepInEx log. To upgrade, close the game and replace only those two files, keeping configuration and caches.

Alternatively, run the manager from the extracted package. Replace the example game path:

```powershell
dotnet ./tools/ProtectionManager.dll inspect --game 'C:\Games\Dinkum'
dotnet ./tools/ProtectionManager.dll install --game 'C:\Games\Dinkum' --package '.'
dotnet ./tools/ProtectionManager.dll rollback --game 'C:\Games\Dinkum'
dotnet ./tools/ProtectionManager.dll uninstall --game 'C:\Games\Dinkum'
```

The manager requires .NET 10 Runtime. Close the game before install, rollback, or uninstall. Missing loaders, duplicate GUIDs, and modified managed files cause refusal. It manages only its own DLL and installation receipt, preserving other mods, the loader, configuration, and player caches. For manual removal, move this plugin's directory outside plugins.

**If you want recovered progress to remain available in vanilla after uninstalling, confirm a successful vanilla save first. Uninstalling does not automatically export the independent cache.** Items supplied by content mods still require those mods.

## Recovery rules

| Previous exit | Host session on rejoin | Automatic recovery eligibility |
|---|---|---|
| Voluntary exit | Same | Allowed, including after restarting the client |
| Voluntary exit | Changed or unknown | Use vanilla saves without replaying the old cache |
| Crash / unexpected disconnect | Same or changed | Allowed, subject to data and history checks |

A supported modded host creates a session ID for each hosted session. Vanilla or older-protocol hosts use SteamID + LobbyID. With an unconfirmed LAN session, voluntary exits do not qualify for automatic recovery. Declining old-cache recovery does not delete or roll back successfully saved vanilla progress.

After a disconnect, **Return to Main Menu** does not save vanilla data. **Save and Return to Main Menu** waits for eligible cached data, validates an export, and returns only after success. Stale or divergent caches, external file changes, lossy conversions, and insufficient inventory space for cursor items can block export. Extra adapter fields remain in the independent cache.

Conflicts pause by default. After inspecting and backing up your data, set `Choice = Native` (use vanilla), `Choice = Backup` (use an eligible cache), or `Choice = Pause` in the configuration's `[Recovery]` section. For host/client history divergence, use `Source = Local` or `Host`. These settings cannot bypass session eligibility and reset after successful loading. Do not force a selection without understanding its source.

## Configuration and data locations

- Configuration: `BepInEx/config/io.dinkumsaveprotection.playerdata.cfg` inside the game directory.
- Windows save root: usually `%USERPROFILE%\AppData\LocalLow\James Bendon\Dinkum`, the game's `Application.persistentDataPath`.
- Local cache: `DinkumSaveProtection/characters/<SteamID>/slotN/<characterUUID>/` under the save root. `identity.bin` lives one directory above the character directory; back up the entire slot directory, including `resume.bin`.
- Host copies: `DinkumSaveProtection/host/<worldUUID>/<SteamID>/<characterUUID>/`.
- Vanilla export backups: `DinkumSaveProtection/native-exports/<characterUUID>/<operationUUID>/`. Do not manually delete `native-export.pending` for an unfinished transaction.
- Plugin upgrade backups: `BepInEx/DinkumSaveProtection-backups/` inside the game directory.

Local storage retains the latest 32 complete versions and normal-save checkpoints; divergent candidates are retained for inspection. Host history is not automatically pruned, so manage its disk usage.

## Build and test

You need Windows, PowerShell, .NET 10 SDK, and a supported local game installation:

```powershell
git clone https://github.com/H-J-F/DinkumSaveProtectionMod.git
cd DinkumSaveProtectionMod
./scripts/build.ps1 -GamePath 'C:\Games\Dinkum'
```

The script downloads and verifies a pinned official BepInEx archive into local `.cache/`, reads game assemblies as compile references, builds the plugin and manager, and runs offline tests and temporary installer fixtures. It creates `dist/DinkumSaveProtection-0.1.5.zip`, `.zip.sha256`, and a build receipt. It does not launch the game, install the plugin, or write real saves. The public source builds without any extra analysis materials.

Run 86 standalone offline tests without owning the game, or add a supported installation path for one extra read-only compatibility check:

```powershell
dotnet run --project tests/DinkumSaveProtection.Tests -c Release
dotnet run --project tests/DinkumSaveProtection.Tests -c Release -- 'C:\Games\Dinkum'
```

`-SkipTests` is for quick local builds only, not release validation. See the [verification scope](docs/Verification.md) (Chinese). Additional [installation/recovery](docs/Installation.md) and [compatibility/API](docs/Compatibility.md) documentation is in Chinese; this README provides the English installation and recovery guide.

## Other mods and isolation

The plugin uses its own GUID, assembly, Harmony owner, network messages, and directories, and distributes only its own artifacts. BepInEx plugins share the game process; separate directories are not a runtime sandbox and cannot guarantee compatibility with every mod.

Custom personal fields require an `IPlayerDataAdapter` registered with `PlayerDataAdapters.Register(adapter)`. Call `PlayerDataAdapters.NotifyChanged()` when data changes. The public interface is:

```csharp
public interface IPlayerDataAdapter
{
    string Id { get; }
    string Version { get; }
    byte[] Capture();
    void Validate(byte[] data, string savedVersion);
    void Restore(byte[] data, string savedVersion);
}
```

The interface and registry are in namespace `DinkumSaveProtection`. Register on the main thread after plugin initialization. Use a unique ID of up to 124 letters, digits, dots, hyphens, or underscores and a nonempty version. Capture, validation, and restoration run on the main thread. Capture must return deterministic, complete personal data; validation must not modify gameplay; restoration must be idempotent and must not grant rewards twice or modify shared containers. Missing or incompatible adapters pause recovery. Do not distribute an additional copy of this plugin DLL with your adapter.

## Feedback and licence

Open an [issue](https://github.com/H-J-F/DinkumSaveProtectionMod/issues) with the game Build, plugin and BepInEx versions, relevant mod list, reproduction steps, and redacted log excerpts. Do not post complete saves, SteamIDs, or other private data.

Original project source, manager, and documentation are licensed under [MIT](LICENSE). Dinkum, Unity, BepInEx, and other third-party components retain their respective rights and licences; this repository does not relicense them.
