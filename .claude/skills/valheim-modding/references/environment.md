# Environment — the machine, the game build, the toolchain

KB-STAMP game-version=1.0.16 network=40 steam-build=25527674 assembly_valheim-sha256=96cfc004f7f4a6f30d070bef39eafd79c466a137121c4665a2f19fb9c15c6127

`scripts/check-game-version.ps1` compares the stamp above with what is installed now (exit 2 = the game
changed). The knowledge base was fully verified against 1.0.15 on 2026-09-22 (reconciled and extended
2026-09-23 with the SeedLab findings), and **re-stamped to 1.0.16 on 2026-09-26** after the checks below.

**What the stamp means since 2026-09-26.** 1.0.16 is Steam build 25527674, network 40, `assembly_valheim.dll`
SHA-256 `96cfc004f7f4a6f30d070bef39eafd79c466a137121c4665a2f19fb9c15c6127` (installed by Steam 2026-09-25
17:02). These surfaces were re-checked on 1.0.16 before the stamp moved:
- **Everything SeedLab reproduces**: biomes, heights, rivers, lakes and streams; location placement (the 11
  filters, draw order, alt biomes, spawn point, dungeon names, Vegvisir and rune-stone targets); seed text to
  seed, world set-up and the suggested seed; every save layout and version number; the map-cache format. Five
  read-only audits compared the 1.0.16 code with SeedLab's port instruction by instruction, and a critic
  checked what they left out; nothing changed (seedlab `history.md`, the 1.0.16 audit). Then proven
  on 1.0.16's own output: four worlds 1.0.16 created, and all of SeedLab's gates on them (seedlab
  `proofs-and-gates.md`).
- The MobTracker review's slice (vanilla-behaviour.md sections 4, 5 and 14-18; multiplayer.md sections 3.2,
  3.5 and 5.2) and TomTom 1.2.0's surface (preflight 49/49 on the installed DLL, and the decompiles that
  release needed, 2026-09-26).

**Facts outside those surfaces were verified on 1.0.15 and have not been re-read on 1.0.16 one by one.** The
update did change code elsewhere, so treat such a fact as **Unverified on 1.0.16** and re-read it with
`decompile.ps1` before relying on it. A line that says "1.0.16" has been re-read. SeedLab's `DATA-STAMP` is
separate (seedlab skill).

**What 1.0.16 changed, and what it did not** (2026-09-26, the audit's file hashes and dates; the code
compared as above):
- **Code rewritten** (new file dates 2026-09-25 17:02): `assembly_valheim.dll` (grew from 2,569,728 to
  2,572,288 bytes), `assembly_utils.dll` (`95810ce3...`), `SoftReferenceableAssets.dll` (`74f088e5...`), the
  other `assembly_*`, `gui_framework`, Splatform, PlayFab, MagicaClothV2, steamworks, `lib_burst_generated.dll`
  and `valheim.exe`. **None of the code SeedLab copies changed.**
- **Data rewritten:** `resources.assets`, `globalgamemanagers*`, `level0`, `sharedassets0`, and 14 SoftRef
  bundles, `d59cfac` (the alt biomes) among them. The re-dump (SeedLab dumper run 7) found the location table,
  alt biomes, vegetation, prefab constants and location children **identical to 1.0.15 apart from the stamp**;
  `localization.json` gained 2 keys and reworded 6, none a place name (seedlab `history.md`, 2026-09-26).
- **Not rewritten:** the SoftRef `manifest` and `manifest_extended` (dated 2026-09-17) and
  `Unity.TextMeshPro.dll` (2026-09-16).
- **The engine is unchanged**, by full SHA-256: the files below are byte-identical to the stock 6000.0.75f1
  editor's player files, except CoreModule, which every project strips for itself (the editor's copy is
  `302E3CB3...`) and which equals the hash SeedLab's spec 03 section 1 recorded under 1.0.15. `UnityPlayer.dll`
  also equals SeedLab's DATA-STAMP. `Player.log` reports `Initialize engine version: 6000.0.75f1 (26349cd2a5c8)`.
  `mono-2.0-bdwgc.dll` and `mscorlib.dll` equal the 1.0.15 hashes recorded below on 2026-09-24. `System.Core`
  and `System` are **Inferred** unchanged from 1.0.15: they are the stock files of the same engine, but no
  1.0.15 hash of them was recorded. Next time, hash the editor's copies rather than trust file dates.

  | File | SHA-256 |
  | --- | --- |
  | `UnityPlayer.dll` | `4D161E15D8CCDB32EB73262E7A3E0A66F8C175B50A38E22AE5B0E8FB9AEA98F3` |
  | `UnityEngine.CoreModule.dll` | `FBA3821A...D990` |
  | `MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll` | `35FD9D8065EE84D0C9312B4B3BB19ECA3C1196C952E66D74DC8895CFFF0AA028` |
  | `mscorlib.dll` | `5ED1180FC8CB409D57952296C7F573F2B1B3D3D2A2CD37258EF407498A17DD4D` |
  | `System.Core.dll` | `9493EBE16569F6067BEEF5281D68E83EE9511DB7F55245B1DC2C95E8E189FCE3` |
  | `System.dll` | `439CA04B265472AFE66DF7802BED26E880B3DB35E33C7E0ECF182611F4B9B6D7` |
- **Not kept:** a copy of the 1.0.15 assemblies, so no direct old-vs-new diff was possible (pitfalls.md
  section 1). An investigator copied 1.0.16's game DLLs into the author's archive folder
  unasked; whether to keep it is the user's decision (still open 2026-09-26). It is Iron Gate's code: local
  only, never published.

## The game

| | |
| --- | --- |
| Install | `<Valheim>` - the game folder, `<Steam library>\steamapps\common\Valheim` (Steam app 892970) |
| Game version | **1.0.16**, network version 40 (from the game's own startup log line `Valheim version: ...`), installed 2026-09-25; the KB was verified on 1.0.15 and re-stamped to 1.0.16 on 2026-09-26 after the re-checks above - read what the stamp covers |
| Engine | Unity **6000.0.75f1**, changeset `26349cd2a5c8` (the strings `6000.0.75f1 (26349cd2a5c8` in `UnityPlayer.dll` and `6000.0.75f1` in `valheim_Data/globalgamemanagers`, read 2026-09-24; `UnityPlayer.dll` FileVersion 6000.0.75.2503836) |
| Mod loader | BepInEx **5.4.23.3** via BepInExPack Valheim 5.4.2333 (Thunderstore) |
| Harmony | 0Harmony **2.9.0.0** (HarmonyX) in `BepInEx\core` — `__runOriginal`, `__state`, `__result`, `__instance` all available |
| Executable | `valheim.exe` (client). **The dedicated server is installed too**: `valheim_server.exe`, see "The dedicated server" below. `[BepInProcess("valheim.exe")]` keeps a mod off the server — it is a *process-name* filter, not an OS filter (boot chain, step 3). (**Corrected 2026-09-26:** this said the server's name was Unverified from game code.) |

**Where the game code lives.** `valheim_Data\Managed\assembly_valheim.dll` (2.5 MB) holds nearly all
game code. `Assembly-CSharp.dll` is a 23 KB stub — do not look there. Also:
`assembly_utils.dll` (ZInput, ZCursor, Utils, FileHelpers), `assembly_guiutils.dll` (Localization,
GuiScaler), `gui_framework.dll` (GUIFramework.* UI widgets), `Splatform.dll` (PlatformUserID and
platform abstraction), `Unity.InputSystem.dll`, `Unity.TextMeshPro.dll`, `Newtonsoft.Json.dll` (ships
with the game), `UnityEngine.JSONSerializeModule.dll` (JsonUtility).

**Input.** The game uses Unity's **new Input System**. `UnityEngine.Input` is unavailable — use
`ZInput` (see vanilla-behaviour.md). BepInEx's own `KeyboardShortcut.IsDown()` goes through
`BepInEx.UnityInput.Current`, which probes legacy Input; prefer `ConfigEntry<KeyCode>` + `ZInput.GetKeyDown`.

## The dedicated server

As of 2026-09-26 (checked on disk at about 21:40):

| | |
| --- | --- |
| Install | `<Steam library>\steamapps\common\Valheim dedicated server\` - Steam app **896660** "Valheim Dedicated Server", build **25527701** (`appmanifest_896660.acf`; updated 2026-09-25, the same day as the client), executable `valheim_server.exe` |
| Game code | its own `valheim_server_Data\Managed\assembly_valheim.dll`, SHA-256 `7cab9b49d31ec064591ca80402dd35c566e03b7297cfb7bf4696c38da4e24d8b`: the client's source built with a server symbol. What differs, and what does not: multiplayer.md section 1.4. `Splatform.Steam.dll` is not in its `Managed` folder |
| Mods | **None: vanilla, no BepInEx, no `winhttp.dll`.** A BepInEx test install (2026-09-26, a mod's server load test) was moved out again and kept, with its logs, in the author's archive |

**Running it** (the 2026-09-26 test): `valheim_server -nographics -batchmode -name "<name>" -port 2456 -world <world>
-password <pw> -public 0 -savedir <folder> -logFile <file>`. The server's `FejdStartup.Awake` quits unless the graphics
device is Null (multiplayer.md section 1.4).
- **Always pass `-logFile <path>`.** Without it the server writes Unity's log to the same
  `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\Player.log` as the client, and a server run on 2026-09-25 had
  overwritten the client's (pitfalls.md section 7).
- `-savedir <folder>` takes the admin, ban and permitted lists and `worlds_local\` with it (multiplayer.md section 6.3).
- **BepInEx works there unchanged** (live, 2026-09-26): BepInEx 5.4.23.3 copied from the game's own pack (the
  `winhttp.dll` doorstop) loaded a plugin with `[BepInProcess("valheim_server.exe")]`, and the log header read
  `BepInEx 5.4.23.3 - valheim_server`.
- **Headless noise, not a mod fault:** each start logs `AsyncResourceUpload failed.` (x2), `This custom render path
  shader needs to have at least 1 passes.` (x2), `Could not find material Hidden/VideoDecode ...`, `Could not find
  material Hidden/VideoComposite ...`, eight `Could not find video decode shader pass ...` and `Failed to play intro
  cinematic`. The same lines appear with BepInEx switched off (both logs are kept with that test install).

**Load-testing a plugin there:** install BepInEx from the game's own pack with the console off, and run with a
random password, `-public 0`, a scratch `-savedir` and `-logFile`, and `SteamAppId=892970` as the server's
`start_headless_server.bat` sets it; for a vanilla comparison run, switch doorstop off. Use a copy of the server
folder or ask its owner first. It tests loading only: a player joining needs the plugin in the client too. Each server run also leaves the Steam game-server client's own files in
the server folder - `logs\` (`connection_log_2456.txt` and others), `config\config.vdf` and `appcache\` (seen on
disk after the 2026-09-25 and 2026-09-26 runs; **Unverified:** their origin is inferred from names and times, not
decompiled). They are not part of a mod install: leave them, and never publish `config.vdf`.

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
     the Chainloader logs `Skipping [...] because of process filters`. (The server executable,
     `valheim_server.exe`, is installed here and was seen on disk on 2026-09-26; the Linux client and server
     binary names remain unknown.)
   - **How the filter matches** (decompiled `BepInProcess`, `Paths.SetExecutablePath`, `Chainloader.Start`, BepInEx
     5.4.23.3, 2026-09-26): `Paths.ProcessName` is `Path.GetFileNameWithoutExtension` of the executable path doorstop
     passes in. A plugin is skipped unless one of its `[BepInProcess]` names, with `".exe"` removed (a case-sensitive
     `Replace`), equals `Paths.ProcessName` ignoring case. The attribute is `AllowMultiple = true`, so one plugin can
     name both `valheim.exe` and `valheim_server.exe`; a plugin with **no** attribute loads in every process. So
     `[BepInProcess("valheim_server.exe")]` would also match a Linux `valheim_server.x86_64` (that name is
     **Unverified**). Loading on the Windows server was seen live (see "The dedicated server" above).
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
  **But that `mono.exe` is 32-bit x86** (PE machine 0x14C) and loads `mono-2.0-sgen.dll`, not the player's
  x64 `mono-2.0-bdwgc.dll` (checked 2026-09-26, the 1.0.16 audit). It is fine for corlib and text tests, and
  **not a numerics oracle** for the x64 player: its floating-point results are x86's, which is different
  evidence (pitfalls.md section 9, the `-O=-float32` entry).
- **Profiling:** `Variations\win64_player_development_mono` is present, so the Unity Profiler's
  development-player swap is available for a *copy* of the game (the release player cannot be profiled).

Shells: Git Bash (the Bash tool) and Windows PowerShell 5.1. See pitfalls.md for the traps in each.

## Project layout

```
<Valheim>\                     the game folder
  BepInEx\                     core\ (Mono.Cecil for the scripts), plugins\, config\, LogOutput.log
  _ModSource\SeedLab\          where the author keeps the SeedLab repository (a clone can live anywhere)
  _ModSource\_retired\         superseded installs, kept rather than deleted
<Valheim dedicated server>\    the dedicated server (Steam app 896660), in the same steamapps\common\ as the
                               game; asmdiff.ps1 and scene-scripts.py compare it with the client by default

<SeedLab repository>\          SeedLab: the offline, bit-exact world generator and the vseed CLI (skill: seedlab)
  .claude\skills\              these skills (valheim-modding, valheim-worldgen, seedlab)
    valheim-modding\scripts\   decompile, decompile-module (a whole assembly and its IL), api-surface,
                               find-usages, find-key-usage, scan-mod-patches, check-game-version,
                               asmdiff (two builds' assemblies compared) (PowerShell), validate-kb.py
                               (needs PyYAML), scene-scripts.py (two builds' main scenes)
    valheim-worldgen\scripts\  valheim_saves.py (read-only seed hash and save parsers)
  .claude\agents\              valheim-api-investigator
  data\<version>-<hash>\       game data read out of the running game (location table, alt biomes, prefab
                               constants, native goldens), stamped with the build it came from: 1.0.16-96cfc004\
                               (dumper run 7) and 1.0.15-59f53fb5\ (local only)
  groundtruth\                 what the game itself wrote: four 1.0.16 worlds' saves and map caches, its own
                               logs (local only)
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
