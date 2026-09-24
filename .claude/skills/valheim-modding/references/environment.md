# Environment — the machine, the game build, the toolchain

KB-STAMP game-version=1.0.15 network=40 steam-build=25390630 assembly_valheim-sha256=59f53fb55d99d22a33e8ed094eec8d21e9f133543bce92bc3d80dce44033adb1

Every game-code fact in the valheim-* skills was verified against the build in the stamp above.
`scripts/check-game-version.ps1` compares it with what is installed now (exit 2 = the game changed).
Last full verification: 2026-09-22. Reconciled and extended 2026-09-23 (the SeedLab findings, several
serialized prefab values corrected from a runtime dump) with the stamp re-checked and unchanged - the
game has not moved since, so nothing needed re-deriving.

## The game

| | |
| --- | --- |
| Install | `<Valheim>` - the game folder, `<Steam library>\steamapps\common\Valheim` (Steam app 892970) |
| Game version | **1.0.15**, network version 40 (from the game's own startup log line `Valheim version: ...`) |
| Engine | Unity **6000.0.75f1**, changeset `26349cd2a5c8` (the strings `6000.0.75f1 (26349cd2a5c8` in `UnityPlayer.dll` and `6000.0.75f1` in `valheim_Data/globalgamemanagers`, read 2026-09-24; `UnityPlayer.dll` FileVersion 6000.0.75.2503836) |
| Mod loader | BepInEx **5.4.23.3** via BepInExPack Valheim 5.4.2333 (Thunderstore) |
| Harmony | 0Harmony **2.9.0.0** (HarmonyX) in `BepInEx\core` — `__runOriginal`, `__state`, `__result`, `__instance` all available |
| Executable | `valheim.exe` (client). The dedicated server's name `valheim_server.exe` is **Unverified** from game code (see multiplayer.md §1.1). `[BepInProcess("valheim.exe")]` keeps a mod off the server — it is a *process-name* filter, not an OS filter (boot chain, step 3) |

**Where the game code lives.** `valheim_Data\Managed\assembly_valheim.dll` (2.5 MB) holds nearly all
game code. `Assembly-CSharp.dll` is a 23 KB stub — do not look there. Also:
`assembly_utils.dll` (ZInput, ZCursor, Utils, FileHelpers), `assembly_guiutils.dll` (Localization,
GuiScaler), `gui_framework.dll` (GUIFramework.* UI widgets), `Splatform.dll` (PlatformUserID and
platform abstraction), `Unity.InputSystem.dll`, `Unity.TextMeshPro.dll`, `Newtonsoft.Json.dll` (ships
with the game), `UnityEngine.JSONSerializeModule.dll` (JsonUtility).

**Input.** The game uses Unity's **new Input System**. `UnityEngine.Input` is unavailable — use
`ZInput` (see vanilla-behaviour.md). BepInEx's own `KeyboardShortcut.IsDown()` goes through
`BepInEx.UnityInput.Current`, which probes legacy Input; prefer `ConfigEntry<KeyCode>` + `ZInput.GetKeyDown`.

## Boot chain (how a mod gets loaded)

1. `winhttp.dll` + `doorstop_config.ini` (Unity Doorstop, `.doorstop_version` 4.4.0 in the root) inject
   BepInEx. **The installed pack is cross-platform**: `doorstop_libs\libdoorstop_x64.so` and
   `libdoorstop_x64.dylib` ship beside `winhttp.dll`, and decompiled `BepInEx.Preloader.PlatformUtils`
   declares `[DllImport("libc.so.6", EntryPoint="uname")]` and
   `[DllImport("/usr/lib/libSystem.dylib", EntryPoint="uname")]` with nested utsname structs
   (verified 2026-09-23).
   **A Mono debugger is one setting away:** `doorstop_config.ini` `[UnityMono]` has `debug_enabled = false`,
   `debug_address = 127.0.0.1:10000` and `debug_suspend = false` (read 2026-09-24); per the UnityDoorstop
   README, `debug_enabled = true` runs Mono's debugger agent inside the release player. **Unverified:** that
   Visual Studio Tools for Unity's "Attach Unity Debugger > Input IP" binds breakpoints through it. If
   tried: revert the setting afterwards, and use a local-only `-p:DebugType=portable -p:DeployToGame=false`
   build that is never packaged (the `.pdb` path leak, pitfalls.md section 1).
2. `BepInEx\core\BepInEx.Preloader.dll` runs preloader patchers from `BepInEx\patchers\` (a
   compatibility patcher, for example, logs "... not installed; nothing to do" when its target mod is absent).
3. The Chainloader loads every `BaseUnityPlugin` found under `BepInEx\plugins\` — the scan uses
   `SearchOption.AllDirectories`, so a plugin DLL loads whether it sits loose in `plugins\` or inside a
   subfolder (decompiled `BepInEx.Bootstrap.Chainloader.Start`, BepInEx.dll 5.4.23.3, 2026-09-23).
   - **`[BepInProcess("valheim.exe")]` does not restrict a plugin to Windows.** The same
     `Chainloader.Start` filters with `x.ProcessName.Replace(".exe", "")` compared to
     `Paths.ProcessName` using `StringComparison.InvariantCultureIgnoreCase`, so it accepts any
     executable whose name minus its final extension is `valheim` (a Linux or Proton client included).
     The filter does exclude the dedicated server everywhere, because `valheim_server` != `valheim`;
     the Chainloader logs `Skipping [...] because of process filters`. (The server executable's own name
     is still corroborated only by a third-party mod's attribute — multiplayer.md §1.1 — and the Linux
     client/server binary names remain unknown.)
   - A plugin whose `[BepInDependency]` is missing is skipped: a plugin that needs Jotunn, installed without
     it, never loads ("missing dependencies: com.jotunn.jotunn").
   - `[BepInIncompatibility(guid)]`: in one pass over all plugins, any plugin whose incompatible GUID is
     still present is dropped with `Could not load [X] because it is incompatible with: Y`. With mutual
     incompatibility exactly one of the pair loads (decompiled `BepInEx.Bootstrap.Chainloader.Start`).
   - A plugin that throws in `Awake` is dropped entirely.
4. Log: `BepInEx\LogOutput.log` — overwritten each launch; includes Unity's own log lines
   (`[Info : Unity Log]`), so it is the best record of what happened in a session. How it is opened
   (BepInEx 5.4.23.3 `DiskLogListener` + `Utility.TryOpenFileStream`, decompiled 2026-09-24):
   `FileMode.Create` (truncated every start unless `[Logging.Disk] AppendLog = true`; this install has
   `false`), `FileAccess.Write`, `FileShare.Read` - so a reader must open it with `FileShare.ReadWrite`
   while the game runs. On an `IOException` (in use - a second game instance) it tries
   `LogOutput.log.1` .. `.4`, then runs with **no** disk log; an `UnauthorizedAccessException`
   (read-only file) is not caught. Stale `.N` files are never deleted. Lines are flushed by a 2 s
   timer, so a hard kill can lose the last ~2 s. SeedLab's session log copies this model.
5. Config: `BepInEx\config\<GUID>.cfg`, created on first run. ConfigurationManager (F1) edits them in-game.

## Local data (verified: GameCamera.ScreenShot uses Utils.GetSaveDataPath(FileHelpers.FileSource.Local))

`%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\` — worlds, characters, `screenshots\`
(F11 writes `screenshot_<yyyy-MM-dd>_<HHmmss>.png`). See the valheim-worldgen skill and
game-operations.md for the file layout.

**On this machine the world saves are in Steam Cloud, not in `worlds_local\`** (measured 2026-09-23
while building `vseed worlds`). `...\IronGate\Valheim\worlds_local\<world>\` holds *only* the four
`cacheMinimap*` files - `Minimap.Start` always builds the cache path from
`ZNet.World.GetSaveDirectory(FileHelpers.FileSource.Local)` - while the `_main.N.fwl2/.db2/.chunks`
live under `<Steam>\userdata\<accountId>\892970\remote\worlds\<world>\`. Two consequences for any
save-reading tool: a world legitimately appears in two folders and must be merged by name, and the
minimap cache for a Cloud world is **not** beside its save. Note Steam itself is installed outside
both Program Files locations on this machine (registry `HKCU\Software\Valve\Steam\SteamPath` names
it), so a tool that probes only the defaults finds nothing.

## Toolchain

| Tool | Where | Notes |
| --- | --- | --- |
| .NET SDK **10.0.401** | `C:\Program Files\dotnet` | `dotnet new sln` makes a **.slnx**, not .sln. Runtime 10.0.12; the ASP.NET Core 10 shared runtime is present (SeedLab's local web UI needs it) |
| Visual Studio **18** | `C:\Program Files\Microsoft Visual Studio\18` | opens .slnx |
| ILSpy (GUI only) | `%LOCALAPPDATA%\Programs\ILSpy` | no `ilspycmd`; `scripts/decompile.ps1` uses its engine DLL |
| Legacy csc (C# 5) | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` | fallback only |
| Python 3.12 + PIL | on PATH | used for rendering texture previews offline |
| Mono.Cecil | `BepInEx\core\Mono.Cecil.dll` | powers the inspection scripts |
| Unity Editor | **6000.0.75f1** (changeset `26349cd2a5c8` - the game's exact build) at `C:/Program Files/Unity/Hub/Editor/6000.0.75f1/Editor/Unity.exe` (Unity Hub's default), with Windows standalone **Mono** player support (`Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_player_nondevelopment_mono`) | **Use 6000.0.75f1 for anything the game loads; a newer editor such as 6000.6.2f1 is too new to build AssetBundles for this game.** Unity's manual: "Unity doesn't support forward-compatibility of AssetBundles, so you can't load an AssetBundle built with a newer version of Unity into an older version of Unity" (https://docs.unity3d.com/6000.0/Documentation/Manual/AssetBundlesIntro.html, fetched 2026-09-24). A bundle for Valheim must come from a Unity **no newer than 6000.0.75f1**; the exact match is the best choice (the manual: older bundles are "usually compatible" but go through slow safe binary reads, avoided by rebuilding "to match the current Player build"), but not a requirement - PlantEverything's **2022.3.50f1** bundle loads on this player (checked in game). Corrected in place 2026-09-24: this cell said the bundle "must come from 6000.0.75f1 itself". Hub link `unityhub://6000.0.75f1/26349cd2a5c8` (https://unity.com/releases/editor/whats-new/6000.0.75f1). Bundles should keep type trees (the default): without them any serialization change in a later Unity breaks loading. Every Valheim engine upgrade is a reason to re-check a bundle-based mod |

**The 6000.0.75f1 editor carries the game's exact runtime** (sha256, 2026-09-24): the game's
`valheim_Data\Managed\mscorlib.dll` is byte-identical to the editor's
`Data\MonoBleedingEdge\lib\mono\unityjit-win32\mscorlib.dll` (`5ed1180f…`), and the game's
`MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll` to the copy under
`Variations\win64_player_nondevelopment_mono` (`35fd9d80…`); 6000.6.2f1's mscorlib differs (`acbfa829…`).
Two uses, no Editor project needed:
- **Tests on the game's own corlib:** the editor's `Data\MonoBleedingEdge\bin\mono.exe` with
  `MONO_PATH=<game>\valheim_Data\Managed` runs a C# 5 test exe against the game's mscorlib and System.dll
  (`typeof(object).Assembly.Location` reports the game's file). TomTom's `run-tests.sh` does this since
  2026-09-24; the text-handling differences it catches are in vanilla-behaviour.md section 13. An earlier
  claim that the editor's Mono "cannot load the game's corlib" came from trying the 6000.6.2f1 copy.
- **Profiling:** `Variations\win64_player_development_mono` is present, so the Unity Profiler's
  development-player swap is available for a *copy* of the game (the release player cannot be profiled).

Shells: Git Bash (the Bash tool) and Windows PowerShell 5.1. See pitfalls.md for the traps in each.

## Project layout

```
<Valheim>\                     the game folder
  BepInEx\                     core\ (Mono.Cecil for the scripts), plugins\, config\, LogOutput.log
  _ModSource\SeedLab\          where the author keeps the SeedLab repository (a clone can live anywhere)
  _ModSource\_retired\         superseded installs, kept rather than deleted

<SeedLab repository>\          SeedLab: the offline, bit-exact world generator and the vseed CLI (skill: seedlab)
  .claude\skills\              these skills (valheim-modding, valheim-worldgen, seedlab)
    valheim-modding\scripts\   decompile, api-surface, find-usages, find-key-usage, scan-mod-patches,
                               check-game-version (PowerShell), validate-kb.py (needs PyYAML)
    valheim-worldgen\scripts\  valheim_saves.py (read-only seed hash and save parsers)
  .claude\agents\              valheim-api-investigator
  data\1.0.15-59f53fb5\        game data read out of the running game (location table, alt biomes, prefab
                               constants, native goldens), stamped with the build it came from (local only)
  groundtruth\                 what the game itself wrote: two worlds' saves and map caches, its own logs
                               (local only)
  tools\SeedLab.Dumper\        the BepInEx plugin that captured data\ - installed only while a dump is run
```

The scripts find the game folder themselves (`-ValheimDir`, then `SEEDLAB_VALHEIM_DIR`, then the folders
above the script, then the Steam libraries), so they work from a clone outside the game folder.

**The SeedLab data snapshot is the authority for serialized prefab values** that the code defaults get
wrong (`Minimap` 2048/12, `ZoneSystem.m_locationVersion` 32, `m_zoneTTL` 10 / `m_zoneTTS` 5, 32 alt
biomes, the 232-entry location table). It is tied to this build by a `DATA-STAMP` line carrying the
same `assembly_valheim.dll` SHA-256 as the KB-STAMP above, so after a game update **both** stamps have
to be re-earned, not just edited. See the **seedlab** skill.

Everything under the game folder is lost if the game is uninstalled or the folder is deleted by Steam.
