# Valheim game operations: runtime lifecycle, saving, death/respawn, data locations

> Researched 2026-09-22 against Valheim 1.0.15 by decompiling the shipped assemblies; every claim was then checked by an independent refute-by-default verifier, who corrected errors in place. Items marked **Unverified:** could not be settled from code. Re-check with `valheim-modding/scripts/decompile.ps1` after a game update.

Build examined: Valheim `Version.CurrentVersion = 1.0.15`, network version 40, world version 41, player version 46,
on Unity 6000.0.75 with BepInEx 5.4.23.3 (Steam). Everything here comes from decompiling `assembly_valheim.dll` /
`assembly_utils.dll` with the ILSpy-based decomp tool. Callers were found with a Mono.Cecil IL cross-reference scan.
Some items are also backed by this machine's `Player.log` and save folders. Those are marked **(observed)**.

## Summary (read this first)

- **Scenes:** `EntryPoint` (the only build scene) -> `loading` (logos) -> `start` (main menu, `FejdStartup`) -> `main` (the game). Both scene switches (menu to game, game to menu) use `SystemResourceManager.FastLoadScene` (which calls the `SoftReferenceableAssets.SceneManagement.SceneManager.LoadScene` wrapper, not Unity's directly) in **Single** mode, so every in-game singleton is destroyed when you return to the menu.
- **Main-scene singletons:** `Game`, `ZNet`, `ZNetScene`, `ZoneSystem`, `Minimap` and `Hud` are scene objects. Each sets its static `instance` in `Awake` and clears it in `OnDestroy`. **The local `Player` is the only one created by code**, in `Game.SpawnPlayer`, several seconds after the scene loads.
- **Settled: `Minimap.LoadMapData` (-> `SetMapData` -> `ClearPins`) runs BEFORE `Player.m_localPlayer` is first set.** In practice this always holds (observed gap about 9 s). No explicit lock enforces it; the gap comes from the spawn waiting on zone loading. See section 3.
- **Death:** the `Player` GameObject is destroyed 10 s after death and a new one is instantiated. `Minimap`, its pins, `Game`, the `PlayerProfile` and the world survive. The death pin is `PinType.Death`, named `"$hud_mapday <day>"`, `save: true`. The game never removes it automatically.
- **Saving:** the autosave interval is `Game.m_saveInterval = 1800f` s (real time). Every peer saves its **character** (`.fch`). **Only the server/host** saves the **world** (chunked folder). Map exploration and pins live **inside the character file**, keyed by **world UID** (not seed, not name).
- **Paths:** local data root = `Application.persistentDataPath` = `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim` **(observed)**. Steam-Cloud saves live in `<Steam>\userdata\<id>\892970\remote\{worlds,characters}` **(observed)**. The minimap texture cache is always local: `worlds_local/<worldName>/cacheMinimap*`, validated against the seed.
- **Null windows:** in the menu, `ZNet.instance`, `ZNetScene.instance`, `ZoneSystem.instance`, `Minimap.instance`, `Hud.instance`, `Game.instance` and `Player.m_localPlayer` are all null. In the main scene, `Player.m_localPlayer` is null until the first spawn, again during every respawn, and after shutdown. **`WorldGenerator.instance` is NOT null in the menu (menu world), and `ZNet.World` is NOT null in the menu after the first session (stale last world; it is null only on the first menu visit after launch)**, so do not use either to detect being in-game.

---

## 1. Scenes and startup

### 1.1 Scene chain
| Scene | How it is loaded | Key MonoBehaviour |
|---|---|---|
| `Assets/Scenes/EntryPoint.unity` | only scene in `globalgamemanagers` (level0) **(observed)** | `EntryPointSceneLoader.Start` -> `SystemResourceManager.FastLoadSceneAsync(m_scene)`, activated once `PlatformInitializer.PreferencesInitialized` (EntryPointSceneLoader / decompiled) |
| `Assets/Scenes/loading.unity` | SoftRef bundle **(observed in `StreamingAssets/SoftRef/manifest`)** | `SceneLoader` (logos, health warning), then loads its `m_scene` (SceneLoader / decompiled) |
| `Assets/Scenes/start.unity` | SoftRef bundle | `FejdStartup` (main menu) |
| `Assets/Scenes/main.unity` | SoftRef bundle | `Game`, `ZNet`, `ZNetScene`, `ZoneSystem`, `Minimap`, `Hud`, ... |

- `FejdStartup.m_mainScene` and `Game.m_startScene` are serialized `SceneReference` fields. The code loads them with `SystemResourceManager.FastLoadScene(scene, LoadSceneMode mode = LoadSceneMode.Single)` (SystemResourceManager.FastLoadScene / decompiled).
  **Unverified:** that the serialized references point at `main.unity` and `start.unity`. The names are the only candidates in the manifest, but the serialized mapping is not visible in code.
- BepInEx chainloader (entrypoint `UnityEngine.CoreModule` / `GameObject..cctor`) finished **before** "Loading first scene!" was logged **(observed in BepInEx/LogOutput.log)**. So a plugin's `Awake` runs before any Valheim scene object exists, and every singleton is null at that point.

### 1.2 Main menu (`FejdStartup`)
- `FejdStartup.Awake`: `ParseArguments()`, `AwakePlatforms()` (Steam + PlayFab), `Settings.ApplyStartupSettings()`, **`WorldGenerator.Initialize(World.GetMenuWorld())`** (the menu world is `"menu"` with seed name `""` -> seed 0), `ZInput.Initialize()` (FejdStartup.Awake / decompiled).
  => **`WorldGenerator.instance` is non-null in the main menu** (menu world).
- `FejdStartup.Start` -> `SetupObjectDB()` adds an `ObjectDB` component to the menu object and `CopyOtherDB(prefab)`. **`ObjectDB.instance` therefore exists in the menu**, as a copy (FejdStartup.SetupObjectDB, ObjectDB.Awake / decompiled).
- Character chosen: `OnCharacterStart` -> `SelectCharacter` -> `PlatformPrefs.SetString("profile", name)` and **`Game.SetProfile(filename, fileSource)`**. That only stores two statics; the profile is loaded later, in `Game.Awake` (FejdStartup.SelectCharacter, Game.SetProfile / decompiled).
- **Start own world:** `OnWorldStart` -> `ZNet.SetServer(server:true, openServer, publicServer, m_world.m_name, password, m_world)` -> `ZNet.ResetServerHost()` -> `TransitionToMainScene()` (FejdStartup.OnWorldStart / decompiled).
- **Join server:** `JoinServer` -> `ZNet.SetServer(false, false, false, "", "", null)` (**sets `ZNet.m_world = null`**) -> `ZNet.SetServerHost(...)` (Steam user / PlayFab / dedicated IP, resolved asynchronously) -> `TransitionToMainScene()` (FejdStartup.JoinServer / decompiled).
- `TransitionToMainScene` fades out, then `Invoke("LoadMainSceneIfBackendSelected", m_instantStart ? 0f : 1.5f)`. If `m_startingWorld || ZNet.HasServerHost()` is true, it calls `LoadMainScene()` -> `SystemResourceManager.FastLoadScene(m_mainScene)`. Otherwise it retries every 0.25 s. After 50 retries it gives up with `ErrorConnectFailed` (FejdStartup.LoadMainSceneIfBackendSelected / decompiled).
- Command-line options that the client build honours: `-console`, `-demomode`, `-joinserverwithcharacter`, `-password`, `-simulationdistance`, `-joincode`, `+connect <addr>`, `+connect_lobby <id>` (FejdStartup.ParseArguments / HandleStartupJoin / decompiled).
  **`FejdStartup.ParseServerArguments` (`-savedir`, `-saveinterval`, `-backups`, `-world`, ...) has NO callers in this client assembly**, so those switches do nothing in the Steam client (IL xref: zero call sites). `ZNet.IsDedicated()` is hard-coded to `return false;` (ZNet.IsDedicated / decompiled).

### 1.3 Main scene: order of creation
Awake, then Start, then first frames. Each of these singletons assigns its static instance in `Awake`:
`Game.Awake: instance = this` · `ZNet.Awake: m_instance = this` · `ZNetScene.Awake: s_instance = this` ·
`ZoneSystem.Awake: s_instance = this` · `Minimap.Awake: s_instance = this` · `Hud.Awake: m_instance = this` (all decompiled).
No code creates them: the IL scan found no `AddComponent<Game|ZNet|ZNetScene|ZoneSystem|Minimap|Hud>` anywhere. They are scene objects.

**Hard ordering dependency found in code:** `ZNetScene.Awake` dereferences `ZDOMan.instance` and `ZRoutedRpc.instance`, and both are constructed only in `ZNet.Awake` (`m_routedRpc = new ZRoutedRpc(..)`, `m_zdoMan = new ZDOMan(512)`; IL xref: no other `newobj`). So `ZNet.Awake` must run before `ZNetScene.Awake`. Note that `ZDOMan.s_instance` and `ZRoutedRpc.s_instance` are never cleared, so outside a session they still point at the previous session's objects.
**Unverified:** what enforces that order. No `[DefaultExecutionOrder]` attribute exists on these classes, so it is either the project's Script Execution Order settings or scene/object order; this cannot be read from code.

**Observed Awake/Start order** (Player.log, all 4 main-scene sessions on 2026-09-22, identical each time; steps 6 and 7 log nothing, so their position relative to steps 1-5 is NOT observed):
1. `Game.Awake`. Logs "Loading player profile X": `new PlayerProfile(m_profileFilename, src).Load()` reads the `.fch` from disk **on every main-scene load**. Also `InvokeRepeating("CollectResourcesCheckPeriodic", 3600, 3600)`.
2. `ZoneSystem.Awake` ("Zonesystem Awake <frame>").
3. `DungeonDB.Awake`.
4. `ZNet.Awake`: `WorldGenerator.Deitialize()`. **Host only:** `WorldGenerator.Initialize(m_world)` and `m_connectionStatus = Connected` immediately.
5. (Start phase) `ZoneSystem.Start` -> `DungeonDB.Start` -> `ZNet.Start` ("Loading: ZNet Start").
   Host: `ServerLoadWorld()`, which loads the world synchronously (`LoadWorld()` for chunked saves) and then `ZoneSystem.GenerateLocationsIfNeeded()`. Client: `ClientConnect()`.
6. `Game.Start` registers routed RPCs, and if `m_playerProfile.m_firstSpawn` it sets `m_queuedIntro = true` (Game.Start / decompiled).
7. `Minimap.Start` creates the textures (`m_mapTexture`, `m_forestMaskTexture`, `m_heightTexture`, `m_fogTexture`), the `m_explored` / `m_exploredOthers` BitArrays and `m_visibleIconTypes`. **If `ZNet.World != null`** it also computes the cache paths (Minimap.Start / decompiled).

**Unverified:** where `Minimap.Awake`, `Hud.Awake` and `ZNetScene.Awake` fall relative to `Game`/`ZoneSystem`. They log nothing. Do not rely on any Awake order among scene objects except ZNet before ZNetScene.

### 1.4 Client connection
- `ZNet.Start` -> `ClientConnect()` -> `Connect(...)` sets the status to `Connecting`. Then handshake (`RPC_ClientHandshake`, password dialog if needed), then **`ZNet.RPC_PeerInfo`**. On the client this does: `m_world = new World(); m_world.m_name/m_seed/m_seedName/m_uid/m_worldGenVersion = <from server>; WorldGenerator.Initialize(m_world); AltBiomeWorldData.VerifyBiomeData(m_world); m_netTime = ...; ... m_connectionStatus = Connected` (ZNet.RPC_PeerInfo / decompiled).
- On a client, **`ZNet.World`, `ZNet.instance.GetWorldUID()` and `WorldGenerator.instance` are therefore unavailable until `RPC_PeerInfo`**. `GetWorldUID()` is `return m_world.m_uid;` and throws a NullReferenceException before then (ZNet.GetWorldUID / decompiled).
- The client-created `World` sets only `m_name`, not `m_worldName` (ZNet.RPC_PeerInfo / decompiled).
- `ZNet.GetConnectionStatus()` always returns `Connected` on the server (ZNet.GetConnectionStatus / decompiled).

### 1.5 First spawn of the local player
- `Game.FixedUpdate`: `if (!m_haveSpawned && ZNet.GetConnectionStatus() == Connected) { m_haveSpawned = true; RequestRespawn(0f); }`. After that it runs `UpdateRespawn(fixedDeltaTime)` every FixedUpdate. If the status is anything other than Connecting/Connected, it calls `Logout()` (Game.FixedUpdate / decompiled).
- `RequestRespawn(delay, afterDeath)` does `m_respawnAfterDeath = afterDeath; CancelInvoke/Invoke("_RequestRespawn", delay)`. `_RequestRespawn` saves and destroys any existing local player (see section 4), then sets `m_requestRespawn = true` (logs "Starting respawn").
- `UpdateRespawn` -> `FindSpawnPoint(out point, out usedLogoutPoint, dt)` (Game.FindSpawnPoint / decompiled):
  1. `!m_respawnAfterDeath && profile.HaveLogoutPoint()`: waits until `m_respawnWait > m_respawnLoadDuration` (**8 s**, field default `8f`) **and** `ZNetScene.IsAreaReady(p)`. Then, if the point is below the ground raycast height, it raises it to that height, and it always adds 0.25 m. If there is no ground, it clears the logout point, resets the wait and falls through to the bed/StartTemple branches on later ticks. Observed "Spawned after 8.019993".
  2. `profile.HaveCustomSpawnPoint()` (bed): same 8 s + area-ready wait. Then `FindBedNearby` returns any `Bed` with `IsCurrent()`; the `maxDistance` argument is ignored. If none is found, the custom spawn point is cleared.
  3. Otherwise: `ZoneSystem.GetLocationIcon(m_StartLocation = "StartTemple")`, plus 2 m up, once `IsAreaReady`.
- `Game.SpawnPlayer(point, spawnValkyrie)` (Game.SpawnPlayer / decompiled). **The exact order matters to modders:**
  ```
  Player p = Instantiate(m_playerPrefab, spawnPoint, identity).GetComponent<Player>(); // Player.Awake runs here; m_localPlayer is still old/null
  p.SetLocalPlayer();                 // Player.m_localPlayer = this (the ONLY assignment; only caller is Game.SpawnPlayer)
  m_playerProfile.LoadPlayerData(p);  // inventory/skills/etc. are loaded AFTER m_localPlayer is set
  ZNet.instance.SetCharacterID(p.GetZDOID());
  p.OnSpawned(spawnValkyrie);
  ```
  After that, `UpdateRespawn` sets the home point (unless the logout point was used) and calls `EnvMan.ForceInstantEnvironmentSwitch()`. On the **first spawn of this session only** (`Game.m_firstSpawn`) it also shouts `$text_player_arrived`, runs `UpdateNoMap()`, `JoinCode.Show`, and **fires `Game.m_playerInitialSpawn`** (a public static event) (Game.UpdateRespawn / decompiled).
- `spawnValkyrie = m_playerProfile.m_firstSpawn && m_inIntro`. `PlayerProfile.m_firstSpawn` is **one bool per character, not per world** (PlayerProfile fields + SavePlayerToDisk / decompiled). Skipping the intro (`Game.SkipIntro`) calls `RequestRespawn(0)`, which **destroys and re-creates the local Player** **(observed: "Starting respawn" + "Local player destroyed" right after intro)**.

---

## 2. Minimap initialization and the pin reload

`Minimap.Update` (decompiled, abridged, emphasis mine):
```csharp
if (ZInput.VirtualKeyboardOpen) return;
if (m_pauseUpdate > 0f) { m_pauseUpdate -= Time.deltaTime; return; }
...
if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || Utils.GetMainCamera() == null) return;
if (!m_hasGenerated) {
    if (WorldGenerator.instance == null) return;
    if (!TryLoadMinimapTextureData(ZNet.World.m_seed)) GenerateWorldMap();
    LoadMapData();                    // <-- restores explored fog + saved pins from the profile
    m_hasGenerated = true;
}
Player localPlayer = Player.m_localPlayer;
if (localPlayer == null) return;      // <-- player check comes AFTER the load
```
- `LoadMapData()` (private): `if (profile.GetMapData() != null) SetMapData(profile.GetMapData());` (Minimap.LoadMapData / decompiled).
  The profile's map data is looked up by `ZNet.instance.GetWorldUID()` (PlayerProfile.GetMapData / decompiled).
- `SetMapData(byte[])` (private) reads the explored bits. For `Version.Map >= Pins` it runs **`ClearPins()`**, which destroys the markers of **every** pin in `m_pins` (saved or not) and sets `m_deathPin = null`. It then re-adds every saved pin through `AddPin(pos, type, name, save:true, isChecked, ownerID, author)`. Finally it restores `ZNet.SetPublicReferencePosition(...)` (Minimap.SetMapData, Minimap.ClearPins / decompiled).
- **Callers (IL xref):** `LoadMapData` <- `Minimap.Update` only. `SetMapData` <- `LoadMapData` only. `ClearPins` <- `SetMapData` only.
  `m_hasGenerated` is only ever set to true, so **the reload runs exactly once per main-scene session**. It does not run again on respawn.
- `ClearPins` does not clear the dynamic-pin dictionaries (`m_locationPins`, `m_playerPins`, `m_pingPins`, `m_shoutPins`, event pins) or `m_spawnPointPin`. If a mod ever re-invokes `SetMapData` mid-session, those dictionaries keep references to removed pins (Minimap.ClearPins vs fields / decompiled). **Do not call `SetMapData` again without also resetting those dictionaries.**
- **`AddPin` before `Minimap.Start` throws a NullReferenceException**: it reads `m_visibleIconTypes.Length`, and that array is only created in `Start` (Minimap.AddPin / Minimap.Start / decompiled).

---

## 3. SETTLED: does `LoadMapData` run before or after `Player.m_localPlayer` is first set?

**Answer: BEFORE.** Evidence chain:
1. `Player.m_localPlayer` is assigned only in `Player.SetLocalPlayer()`, and that method's only caller is `Game.SpawnPlayer` (IL xref).
2. `Game.SpawnPlayer` is only reached from `Game.UpdateRespawn` after `FindSpawnPoint` returns true. All three branches require `ZNetScene.IsAreaReady(point)`: the zone must be loaded **and** every valid ZDO in it must be instantiated. The logout-point and bed branches also require `m_respawnWait > m_respawnLoadDuration` (8 s) (Game.FindSpawnPoint, ZNetScene.IsAreaReady / decompiled).
3. `Minimap.LoadMapData` does **not** wait for the player. It only needs `WorldGenerator.instance != null` (plus a main camera, no virtual keyboard, no `m_pauseUpdate`), and it runs **before** the `localPlayer == null` early return in `Minimap.Update` (section 2).
4. `WorldGenerator.instance` becomes non-null at the moment connection status becomes Connected. On the host that is `ZNet.Awake`, before the first frame. On a client it is `ZNet.RPC_PeerInfo`. Connected is also the precondition for `Game.FixedUpdate` to call `RequestRespawn(0)`. So the minimap's precondition is met no later than the spawn process starts, and the spawn then needs several more frames or seconds (area ready, plus the 8 s wait).
5. **(observed, Player.log, host, logout-point spawn):**
   `19:06:48 Starting respawn` -> `19:06:48 Loading minimap textures done [194ms]` -> `19:06:48 Minimap: unpacking compressed mapData 8,304 => 8,388,658 bytes` (= SetMapData) -> `19:06:57 Spawned after 8.019993` (= SpawnPlayer).
   On a new world: `19:05:28 Generating new world minimap done` -> player spawn at `19:05:43`.

**Caveat:** no lock or flag enforces this. It is a timing consequence. If `Minimap.Update` were suppressed for a long time (`m_pauseUpdate` via the public `PauseUpdateTemporarily()` = 0.1 s, `ZInput.VirtualKeyboardOpen`, or `Utils.GetMainCamera() == null`), the player could in theory spawn first. In that case any pin a mod added between spawn and the delayed load would be wiped by `ClearPins`.

**Modder rules derived from this:**
- Pins added **after `Player.m_localPlayer != null`** survive; in practice the reload has already happened.
- Pins cannot be added at all before `Minimap.Start`: at plugin Awake there is no `Minimap.instance`, and Awake-phase hooks such as a `ZNet.Awake` postfix run before `Minimap.Start`, so `AddPin` throws (section 2). Start-phase hooks (`ZNet.Start`/`Game.Start` postfix) may run before or after `Minimap.Start` (order unverified), so they either throw or succeed. Pins that do get added before the load (for example in a `Minimap.Start` postfix) **are wiped** by `ClearPins` if the profile has saved map data for this world UID. With no saved map data (a new world for this character), `SetMapData` is not called and nothing is wiped. That inconsistency is a trap.
- Two deterministic hooks: a Harmony **postfix on private `Minimap.LoadMapData`** (it runs once per session even when there is no saved data), or read the private bool `Minimap.m_hasGenerated` by reflection.
  `Game.m_playerInitialSpawn` (public static event) fires after the first spawn of each session. It is static and never cleared, so subscribe once, not once per session.

---

## 4. Death and respawn

### 4.1 `Player.OnDeath()` (override; it does not call `base.OnDeath`). It runs only on the owner (`!m_nview.IsOwner()` returns early)
In order (Player.OnDeath / decompiled):
1. `bool hard = HardDeath()` (`m_timeSinceDeath > m_hardDeathCooldown`; the field initializer is `10f`). **Unverified:** the prefab's serialized value.
2. ZDO `s_dead = true`. The `"OnDeath"` RPC goes to everybody, and `RPC_OnDeath` hides `m_visual`.
   `Player.IsDead()` reads the ZDO bool `s_dead` (Player.IsDead / decompiled).
3. Stats (Deaths, DeathBy*, ConsecutiveDaysSurvived = 0).
4. `profile.SetDeathPoint(transform.position)` stores a per-world death point in the profile. In this build nothing draws it: `Minimap.UpdateProfilePins` calls `HaveDeathPoint()` but ignores the result and just removes the legacy `m_deathPin` (Minimap.UpdateProfilePins / decompiled).
5. `CreateDeathEffects()` spawns the ragdoll from `m_deathEffects`.
6. `CreateTombStone()`, only if the inventory is non-empty and the global key `DeathKeepInventory` is off. The keys `DeathKeepEquip`, `DeathDeleteUnequipped` and `DeathDeleteItems` change what is unequipped or removed. It then does `Instantiate(m_tombstone, GetCenterPoint(), rotation)`, `MoveInventoryToGrave(m_inventory)` and `TombStone.Setup(profile.GetName(), profile.GetPlayerID())` (Player.CreateTombStone / decompiled).
7. `m_foods.Clear()`. Skills: `DeathSkillsReset` key -> `m_skills.Clear()`; else if hard death -> `m_skills.OnDeath()` (skill loss). `m_seman.RemoveAllStatusEffects()`.
8. **`Game.instance.RequestRespawn(10f, afterDeath: true)`**.
9. Messages `$msg_softdeath` (if not a hard death) and `$msg_youdied`, plus the "death" tutorial.
10. **Death pin:**
    ```csharp
    Minimap.instance.AddPin(transform.position, Minimap.PinType.Death,
        $"$hud_mapday {EnvMan.instance.GetDay(ZNet.instance.GetTimeSeconds())}", save: true, isChecked: false, 0L);
    ```
    `EnvMan.GetDay(t) = (int)(t / m_dayLengthSec)`. The field initializer is `m_dayLengthSec = 1200`; **Unverified:** the prefab value. The pin is created **client-side, on the dying player's machine only**. Its name is the untranslated token plus the day number, e.g. `"$hud_mapday 12"`, and it is localized when displayed. `ownerID = 0` and `author = default`.
11. `m_onDeath` callback, then analytics.

- **Nothing removes the death pin automatically.** `TombStone` contains no Minimap code at all (TombStone / decompiled), and no Minimap code path removes `PinType.Death` pins except user right-click / `RemovePin`.
- The tombstone self-destructs (`m_nview.Destroy()`, after `GiveBoost()` to its owner) once it is empty and not in use, checked every `m_updateDt = 2f` s (TombStone.UpdateDespawn / decompiled). Its pin stays.
- Death pins are **excluded from shared map data** (cartography table): `GetSharedMapData` filters `pin.m_type != PinType.Death`. The user cannot place `Death` pins by double-click (Minimap.GetSharedMapData / OnMapDblClick / decompiled).

### 4.2 `Game._RequestRespawn()` (10 s after death)
```csharp
if (Player.m_localPlayer) m_playerProfile.SavePlayerData(Player.m_localPlayer); // serialize Player into profile MEMORY (not disk)
if (Player.m_localPlayer) { ZNetScene.instance.Destroy(Player.m_localPlayer.gameObject); ZNet.instance.SetCharacterID(ZDOID.None); }
m_respawnWait = 0f; m_requestRespawn = true; MusicMan.instance.TriggerMusic("respawn");
```
(Game._RequestRespawn / decompiled)
- `ZNetScene.Destroy` destroys the ZDO (if owner) and the GameObject. `Player.OnDestroy` then sets `m_localPlayer = null` and `m_localPlayerExists = false` (logs "Local player destroyed") (Player.OnDestroy / decompiled).
- Respawn goes through the same `UpdateRespawn`/`FindSpawnPoint`, but `m_respawnAfterDeath = true` **skips the logout point**. The order is bed (after the 8 s wait) or the StartTemple location. Then `SetHomePoint(point)`, then `SpawnPlayer(point, false)`, which creates a **new `Player` instance** and loads the just-saved player data: empty inventory (moved to the grave), no food.
- `m_timeSinceDeath` is part of the serialized player data (`Player.Save`/`Load`), so the soft/hard-death timer carries across the respawn (Player / decompiled).
- Other callers of `RequestRespawn`: `Game.FixedUpdate` (first spawn), `Game.SkipIntro`, `TeleportHome.OnTriggerEnter` (IL xref).

### 4.3 What survives death
**Survives (confirmed):** `Minimap.instance` and all of `m_pins` (death pin, user pins, explored fog), because Minimap is a scene object. While the player is dead `Minimap.Update` runs `UpdateExplore` and then `SetMapMode(None)` and returns; while it is null it returns early. Nothing touches the pins (Minimap.Update / decompiled). `Game`, `ZNet`, `ZNetScene`, `ZoneSystem`, `Hud`, the in-memory `PlayerProfile` (including per-world map data), the tombstone (a networked world object), and all world state also survive.
**Destroyed:** the local `Player` GameObject and its ZDO, its placement ghost, and its per-instance state. Status effects and food are cleared at death.
**Not written to disk by death:** neither the profile nor the world. The death pin reaches disk at the next `Game.SavePlayerProfile` (section 6).

---

## 5. Logout, returning to menu, quitting

### 5.1 `Game.Logout(bool save = true, bool changeToStartScene = true)`
```csharp
if (!m_shuttingDown) {
    bool shouldExit = false;
    save = ZNet.instance.EnoughDiskSpaceAvailable(out var popupShown, exitGamePrompt: true, exit => { shouldExit = exit; ContinueLogout(save, shouldExit, changeToStartScene); });
    if (!popupShown) ContinueLogout(save, shouldExit, changeToStartScene);
}
```
(Game.Logout / decompiled)
- **The `save` argument is overwritten** by the disk-space check. In effect Logout always saves when disk space is sufficient. `Menu.m_saveOnLogout` (set false for clients after a manual save) has no effect through this path.
- `ContinueLogout`: if `!save && !shouldExit` it returns (you stay in game). Otherwise it calls `Shutdown(save)`, then `FastLoadScene(m_startScene)`, or `Application.Quit()` in demo mode.
- **Callers:** `Menu.Logout` (menu button), `Game.FixedUpdate` (on `ZNet.m_loadError`, and on **connection lost**: status not Connecting/Connected), `EndCredits.Update`, `PrivilegeManager.HandleLogout`, `SuspendManager`, `BlockCheckHandler`, `SessionPlayerListEntry.ProceedBlock` (IL xref).

### 5.2 `Game.Shutdown(bool saveWorld)` (private). Runs once and sets `m_shuttingDown = true`
```csharp
ZLog.Log("Shutting down"); m_shuttingDown = true;
if (saveWorld) SavePlayerProfile(setLogoutPoint: true);   // player still exists -> map + player data captured
ZNetScene.instance.Shutdown();   // ResetZDO + Object.Destroy on EVERY ZNetView instance (incl. local Player); ZDOs are kept
ZNet.instance.Shutdown(saveWorld); // Save(sync:true) [world only if server] -> StopAll(): join save thread, ZDOMan shutdown, disconnect
```
(Game.Shutdown, ZNetScene.Shutdown, ZNet.Shutdown / decompiled)
- After `m_shuttingDown`, `Game.Update` returns immediately, so no more autosaves. `Game.IsShuttingDown()` is public.
- **(observed order):** "Shutting down" -> "Minimap: compressed mapData" -> character cloud save -> "ZNet Shutdown" -> "Save World Thread Started" ... "World save (5/5) done" -> "Sending disconnect msg" -> "Local player destroyed" (deferred `Destroy` at end of frame) -> scene unload -> "ZNet OnDestroy" -> "Net scene destroyed" -> the menu scene's `FejdStartup.Awake` ("Valheim version: ...").
- The Single-mode load of `start` destroys every main-scene object. `OnDestroy` clears the statics: `Game.instance`, `ZNet.m_instance`, `ZNetScene.s_instance`, `ZoneSystem.s_instance`, `Minimap.s_instance` and `Hud.m_instance` all become null.
- **`ZNet.OnDestroy` does NOT clear the static `ZNet.m_world`**, so `ZNet.World` still returns the last world while in the menu (ZNet.OnDestroy / decompiled).
- `ZNet.Shutdown(save)` calls `Save(sync: true)` with `saveOtherPlayerProfiles = false`. **When a host logs out or quits, it does not ask connected clients to save their characters.**

### 5.3 Quitting the application
- In-game menu Quit: `Menu.OnQuitYes` -> `Application.Quit()` -> **`Game.OnApplicationQuit`**: `if (!m_shuttingDown) { Shutdown(saveWorld: ZNet.instance.EnoughDiskSpaceAvailable(out _)); HeightmapBuilder.instance.Dispose(); Thread.Sleep(2000); }` (Game.OnApplicationQuit / decompiled).
  So quitting from inside a world, including Alt-F4 via `OnApplicationQuit`, saves the character and (host) the world **synchronously**.
- Quitting from the main menu: `FejdStartup.OnApplicationQuit` only disposes `HeightmapBuilder`. No save, since there is nothing loaded.

---

## 6. Saving

### 6.1 Autosave (`Game.UpdateSaving(Time.unscaledDeltaTime)`, called from `Game.Update`)
- Interval **`Game.m_saveInterval = 1800f`** (public static, 30 min). It uses **unscaled** time, so it keeps counting while the game is paused (Game.UpdateSaving / Game fields / decompiled).
- When crossing 30 s before the interval: **server only** sends `MessageHud.MessageAll(Center, "$msg_worldsavewarning 30s")` (constant `m_preSaveWarning = 30f`).
- At `m_saveTimer > m_saveInterval`: if there is not enough disk space, `m_saveTimer -= 300`. Otherwise `SavePlayerProfile(setLogoutPoint: true)` then `ZNet.instance.Save(sync:false, saveOtherPlayerProfiles:true, waitForNextFrame:true)`.
- This runs on **every peer**. On a client `ZNet.Save` does nothing to the world (`m_isServer && m_world != null` is false), so each client saves its own character on its own timer. On the host, `saveOtherPlayerProfiles:true` also sends the `"SavePlayerProfile"` RPC to every peer. The RPC is sent from inside the world-save branch, so it is not sent when the world save is skipped (`m_loadError`, `SkipSaving`, or the `DontSaveWorld` flag). On each client, `ZNet.RPC_SavePlayerProfile` -> `Game.SavePlayerProfile(true, isFromRpc:true)` runs (ZNet.Save / SaveOtherPlayerProfiles / decompiled).
- The timer is reset to 0 by `SavePlayerProfile` and by `ZNet.Save`. `UpdateSaving` returns early only when **both** `DontSaveCharacter` and `DontSaveWorld` are set: `SaveSystem.HasSessionFlag` uses `Enum.HasFlag`, which needs every bit of the combined mask. A single flag blocks only its own half, inside `SavePlayerProfile` or `ZNet.Save`.
- `-saveinterval` does not work in this client (section 1.2). A mod can simply assign `Game.m_saveInterval`.

### 6.2 All save triggers (IL xref of callers)
| Trigger | Character (`Game.SavePlayerProfile`) | World (`ZNet.Save` -> `SaveWorld`) |
|---|---|---|
| Autosave (`Game.UpdateSaving`) | yes (setLogoutPoint true) | yes, server only; also asks clients to save |
| Sleep ends (`Game.SleepStop`, only if `m_saveTimer > 60`) | yes (setLogoutPoint **false**) | yes, server only (no other profiles) |
| Menu "Save" (`Menu.OnManualSave`, 60 s cooldown) | single player or no peers: yes; otherwise via `SaveWorldAndPlayerProfiles` | same |
| Console `save` / `ZNet.SaveWorldAndPlayerProfiles` | server: `RPC_Save(null)`; client: local save + `"Save"` RPC to the server (**admin only**) | server: yes + other profiles |
| Logout / quit (`Game.Shutdown`) | yes, if enough disk | yes, **sync**, server only |
| Server-requested (`RPC_SavePlayerProfile`) | yes | - |

### 6.3 `Game.SavePlayerProfile(bool setLogoutPoint, bool isFromRpc = false)` (public)
```csharp
if ((bool)Player.m_localPlayer) {
    m_playerProfile.SavePlayerData(Player.m_localPlayer);  // Player.Save -> m_playerData bytes
    Minimap.instance.SaveMapData();                         // explored + saved pins -> profile.m_worldData[worldUID].m_mapData
    if (setLogoutPoint) m_playerProfile.SaveLogoutPoint();
}
... m_playerProfile.Save();  // writes .fch
```
(Game.SavePlayerProfile / decompiled)
- **Pitfall:** if `Player.m_localPlayer` is null (for example during the post-death respawn wait), the save still writes the `.fch`, but the **map data and player data are the stale in-memory copies from the last successful capture**. Pins added since then are not persisted by that save.
- `SaveLogoutPoint`: the logout point is the player position; if the player is dead, it is the custom spawn (bed) or the home point. It is skipped during the intro.
- `Minimap.SaveMapData()` is public and only copies into profile memory. Writing to disk requires `PlayerProfile.Save()` or `Game.SavePlayerProfile`.

### 6.4 Character file (`PlayerProfile.SavePlayerToDisk`)
- Path: `SaveSystem.GetCharacterFolderPath(src) + name + ".fch"`, where the folder is `<SaveDataPath>/characters_local/` (Local) or `/characters/` (Cloud).
  It is written to `<name>.fch.new`, then `FileHelpers.ReplaceOldFile` swaps it in and keeps `<name>.fch.old`, then `ZNet.ConsiderAutoBackup` runs (PlayerProfile.SavePlayerToDisk, SaveSystem / decompiled). **(observed:** `<character>.fch`, `<character>.fch.old`, `<character>_backup_auto-20260922-190509.fch`**)**.
- Layout: `int len, bytes data, int hashLen, bytes hash`. Data starts `int 46` (`Version.Player.DeepNorth`), `205` stat types, `10` stat sets, ... `m_firstSpawn`. Then **per-world records keyed by `long worldUID`**: `haveCustomSpawn, spawnPoint, haveLogout, logoutPoint, haveDeath, deathPoint, homePoint, hasMap, mapData`. Then name, playerID, startSeed, usedCheats, dateCreated, and the player-data blob.
- **Map data is per world UID.** A world's UID is `name.GetStableHashCode() + Utils.GenerateUID()`, made at creation (World ctor / decompiled). Two worlds with the same seed therefore have **separate** maps and pins, and so does a recreated world with the same name.
- Map data blob (`Minimap.GetMapData`): `int 8` (`Version.Map.PinsAuthor`), then a compressed package: `int textureSize`, `textureSize^2` bool explored, `textureSize^2` bool exploredOthers, `int nSaved`, then per saved pin `string name, Vector3 pos, int type, bool checked, long ownerID, string author`, then `bool publicRefPos`. Only pins with `m_save == true` are written.
  **(observed, arithmetic)** Log "compressed mapData 8388617" = 4 + 2*T^2 + 4 + 1 with 0 pins, so **runtime `m_textureSize = 2048`** (the field initializer says 256; the prefab overrides it).
  **(observed)** runtime `m_pixelSize = 12.0` (field initializer 64f; the prefab overrides it). Two independent
  derivations from game-written files, 2026-09-22: (a) fitting the closed-form `IsAshlands` boundary against
  `cacheMinimapBiome` — 0 mismatches; (b) the world-edge discontinuity in `cacheMinimapHeight`, since
  `GetBiomeHeight` returns exactly −400 when `DUtils.Length(wx,wy) > 10500f`: with pixel radius
  `u = hypot(j−1024+0.5, i−1024+0.5)`, max `u` over non-−400 pixels is 874.999714 and min `u` over −400 pixels is
  875.000857, so `pixelSize = 10500/u* ∈ (11.999988, 12.000004)` — identical in both `asdasdasd` and
  `testworldclaude`, 1 788 980 edge pixels each. Corroborated by `AltBiomeWorldData.c_pixelSize = 12f`.
  (SeedLab `MinimapCache.DerivePixelSizeBracket`, run over both worlds' caches.) **Both values were then
  read directly out of the loaded `Minimap` component in the running game, 2026-09-22** — 2048 and 12,
  exactly as derived (`prefab-constants.json` in the SeedLab data snapshot), together with
  `m_removeRadius` **300** (code 128f), `m_exploreRadius` **50** (100f) and `m_exploreInterval` **0.25**
  (2f), which nothing had derived and which vanilla-behaviour.md had wrong.

### 6.5 World save (`ZNet.SaveWorld` -> background thread `SaveWorldThread`, server only)
- Main thread: `ZDOMan.PrepareSave`, `ZoneSystem.PrepareSave`, `RandEventSystem.PrepareSave`, `PersistentEventSystem.PrepareSave`, then a new `Thread(SaveWorldThread)`. `sync:true` joins it. `ZNet.WorldSaveStarted` (public static `Action`) fires at the start of `SaveWorld`. `WorldSaveFinished` does not fire when the thread ends. `ZNet.Update` -> `UpdateSave` calls `PrintWorldSaveMessage` about 0.5 s later, and that fires it. After `ZNet.Shutdown` (`enabled = false`) `Update` no longer runs, so it does not fire for the logout/quit save (ZNet.SaveWorld / UpdateSave / PrintWorldSaveMessage / decompiled).
- Files, in the world folder `World.GetSaveDirectory(src) = <worldsRoot>/<worldName>/` (worldsRoot = `<SaveDataPath>/worlds_local` or `/worlds`) (World, SaveSystem / decompiled):
  `<X>_<Y>__<n>_<m>.chunk` (ZDO chunks, `ZDOMan.SaveChunks`), `_main.<N>.chunks`, `_main.<N>.db2` (`int 41`, netTime, ZoneSystem, RandEventSystem, PersistentEventSystem), `_main.<N>.fwl2` (meta: `int 41`, name, seedName, seed, uid, worldGenVersion, needsDB, starting global keys, player history), `_main.<N>.ok` (written last).
  `N = SaveSystem.s_saveNumber + 1` at `BeginSave`. After success, the previous number's files are deleted. **(observed)** in `remote/worlds/asdasdasd/`.
  Legacy non-chunked worlds use `<worldsRoot>/<name>.fwl` + `.db`.
- `ZNet.Save` skips (and logs "Skipping world save") when `ZNet.m_loadError`, `ZoneSystem.SkipSaving()` or `DungeonDB.SkipSaving()`. After a world load error, `Game.FixedUpdate` calls `Logout()` and logs "World load failed, exiting without save". Only the world save is actually skipped: `Logout` replaces `save` with the disk-space check, so `Shutdown` still runs `SavePlayerProfile` and rewrites the character `.fch`.
- Auto-backups (`ZNet.ConsiderAutoBackup` -> `SaveSystem.ConsiderBackup`): count from `PlatformPrefs "AutoBackups"` (field default `m_backupCount = 2`; observed 4), `m_backupShort = 7200` s, `m_backupLong = 43200` s. No backup if the session has run less than 1200 s ("World session not long enough"). Naming: `<name>_backup_auto-yyyyMMdd-HHmmss`.

---

## 7. Local data locations

- `Utils.GetSaveDataPath(src)` (assembly_utils): returns `""` when `FileHelpers.CloudStorageSupportedAndEnabled && src.IsAutoOrCloud()`; otherwise `m_saveDataOverride` if one is set, else `Utils.persistantDataPath = Application.persistentDataPath` (Utils.GetSaveDataPath / decompiled).
  `SetSaveDataPath` is only called from the dead `ParseServerArguments`.
- On this machine **(observed)** `persistentDataPath` = `C:\Users\<user>\AppData\LocalLow\IronGate\Valheim\`, containing:
  - `Player.log`, `Player-prev.log`: the Unity player log, which includes all `ZLog` output. BepInEx mirrors it into `<game>\BepInEx\LogOutput.log` **(observed)**. The in-menu "show log" button opens `Application.persistentDataPath` (FejdStartup.OnButtonShowLog). `CustomLogger` symlinks are macOS-only (CustomLogger / decompiled).
  - `screenshots/screenshot_yyyy-MM-dd_HHmmss.png`: **F11** in `GameCamera.LateUpdate` (also RMB in free-fly). The path is always Local, and nothing is written if the file already exists (GameCamera.ScreenShot / decompiled; observed files).
  - `worlds_local/<worldName>/`: local worlds, **and the minimap texture cache for every world, including cloud worlds**.
  - `characters_local/`: local characters (path from code). **Not present on this machine**, because its only character is in Steam Cloud.
  - `cache/<worldName>_biomedatacache.bin`: `AltBiomeWorldData` cache (AltBiomeWorldData.GetFilePath / decompiled). The folder exists here but is empty **(observed)**.
  - `adminlist.txt`, `bannedlist.txt`, `permittedlist.txt`: server lists, created by the host's `ZNet.Awake` (Local storage).
  - `serverlist_local/`.
  - `worlds/` and `characters/` here only hold `steam_autocloud.vdf` **(observed)**.
- **Steam Cloud** saves (`FileSource.Cloud`) are **(observed)** at `<Steam>\userdata\<accountId>\892970\remote\worlds\<worldName>\...` and `...\remote\characters\<name>.fch`. On this machine that is a Steam folder outside Program Files, and the Player.log lines read "Cloud Save: ... /worlds/asdasdasd/_main.1.fwl2".
  **Settled:** `FileHelpers.CloudStorageSupportedAndEnabled` is true on this Steam PC. `FileWriter` logs "Cloud Save:" only when its source is Cloud, and its constructor picks Cloud only when that property is true. The relative `/worlds/...` paths in those log lines are the `""` root that `GetSaveDataPath` returns in that branch (FileWriter..ctor / FileWriter.Finish, assembly_utils / decompiled).

### 7.1 Minimap texture cache (`TryLoadMinimapTextureData` / `SaveMapTextureDataToDisk`)
- Paths are set in `Minimap.Start`, **only if `ZNet.World != null`**: `ZNet.World.GetSaveDirectory(FileSource.Local)` + `/cacheMinimapMask`, `/cacheMinimapBiome`, `/cacheMinimapHeight`, `/cacheMinimapMeta` (Minimap.Start / GetCompleteTexturePath / decompiled).
  That is **`worlds_local/<worldName>/`, keyed by world NAME (folder) and validated by SEED**.
- The meta file is 8 bytes: `int32 seed` + `int32 Version.CachedMinimap.Original (=1)`. **(observed:** `cacheMinimapMeta` = `-1772362158, 1`**)**.
- Load conditions (`TryLoadMinimapTextureData(ZNet.World.m_seed)`): all 4 files exist, `ZNet.World.m_worldVersion == Version.World.DeepNorth (41)`, the meta seed equals the world seed, and the meta version is 1. Otherwise it regenerates: `GenerateWorldMap()` samples `WorldGenerator.GetBiome`/`GetBiomeHeight` for every pixel (about 4.2-4.3 s observed), first calls `DeleteMapTextureData(ZNet.World.m_name)`, and then saves the cache if `FileHelpers.LocalStorageSupport == Supported`.
- **Clients never use or write the cache.** `Minimap.Start` runs at scene load, while `ZNet.World` is still null because `JoinServer` passed `null` to `SetServer`, so the cache paths stay null. `TryLoad...` returns false on the empty path, and `SaveMapTextureDataToDisk` returns early on the null `m_saveDirectory`.
  Side effect: `GenerateWorldMap` on a client still calls `DeleteMapTextureData(<server world name>)`, which deletes the cache of a **local** world with the same name, if one exists (Minimap.Start / GenerateWorldMap / SaveMapTextureDataToDisk / DeleteMapTextureData / decompiled).
- The cache stores only terrain, biome and forest textures, which are deterministic from the seed and world-gen version. Exploration fog and pins are **never** in the cache; they live in the character file (section 6.4).
- **The map texture and the fog texture are half a pixel out of step with each other, inside the game.**
  `GenerateWorldMap` samples pixel `j` at `wx = (j − m_textureSize/2) * m_pixelSize + m_pixelSize/2`, i.e. 6 m
  east/north of `(j − 1024)·12`, while `Minimap.WorldToPixel` (which `Explore(Vector3, float)`, the pin hit-tests
  and `IsExplored` all use) computes `px = Utils.RoundToInt(p.x / m_pixelSize + 1024f)` and `MapPointToWorld`
  inverts to `(px − 1024)·12` with **no** half-pixel term. Consequence, verified over all 2048² pixels:
  `WorldToPixel(sample point of pixel k)` returns **k + 1** in both axes, never k — because `wx/12f + 1024f` is
  exactly `j + 0.5` and `RoundToInt(j+0.5f) = (int)(j+64001.0f) − 64000 = j+1`.
  This is a 6 m display offset the game lives with; it matters only when converting a *pixel index* back to a
  world position. Index-for-index overlays (explored bitmap AND biome cache) are still exactly what the player
  saw, because the game composites the same two textures the same way.
  (Minimap.GenerateWorldMap / WorldToPixel lines 1815-1819 / MapPointToWorld lines 1803-1813 / Explore lines
  1832-1866 / decompiled; measured by SeedLab `MinimapGeometry`, 2026-09-22.)

---

## 8. When is it safe to touch what (null windows)

| Reference | Non-null from | Null again at | Notes |
|---|---|---|---|
| `Game.instance` | main-scene `Game.Awake` | `Game.OnDestroy` (scene unload) | `GetPlayerProfile()` is valid from Awake (profile loaded there) |
| `ZNet.instance` | `ZNet.Awake` | `ZNet.OnDestroy` | null in the menu (**inferred:** `ZNet.Start` immediately loads a world or connects, so it cannot live in the menu scene). `IsServer()` is valid from Awake |
| `ZNet.World` (static) | host: before the scene (SetServer); client: `RPC_PeerInfo` | **never cleared** by OnDestroy; set to null by `JoinServer` | null in the menu only before the first session (static initializer), stale afterwards. Do not use it to detect in-game |
| `ZNet.instance.GetWorldUID()` | host: Awake; client: `RPC_PeerInfo` | - | throws NullReferenceException on a client before PeerInfo |
| `WorldGenerator.instance` | menu: menu world (seed 0)!; host: `ZNet.Awake`; client: `RPC_PeerInfo` | `ZNet.Awake` calls `Deitialize()` | non-null in the menu. Do not use it to detect in-game |
| `ZNetScene.instance` | `ZNetScene.Awake` (after ZNet.Awake) | `OnDestroy`; `Shutdown()` disables it and destroys all instances | |
| `ZoneSystem.instance` | `ZoneSystem.Awake` | `OnDestroy` | `GetGroundHeight`/`GetSolidHeight` are **physics raycasts**. They only work where terrain colliders are loaded (near the player); otherwise they return `p.y` / `false` (ZoneSystem.GetGroundHeight / decompiled) |
| `Minimap.instance` | `Minimap.Awake` | `Minimap.OnDestroy` | **`AddPin` before `Minimap.Start` throws a NullReferenceException** (`m_visibleIconTypes`); pins added before `LoadMapData` can be wiped (section 3). Survives death |
| `Hud.instance` | `Hud.Awake` | `Hud.OnDestroy` | |
| `Player.m_localPlayer` (public static field) | `SetLocalPlayer` in `Game.SpawnPlayer` | `Player.OnDestroy`: at `_RequestRespawn` (10 s after death), on `SkipIntro`, and at `Game.Shutdown` (end of frame) | null for several seconds after scene load (8+ s for logout-point and bed spawns) and during each respawn. A bed respawn waits 8+ s. A StartTemple respawn waits only for `IsAreaReady` and can take under a second **(observed: "Starting respawn" and the new `Player(Clone)` both at 19:08:21)**. **When `SetLocalPlayer` runs, inventory and skills are NOT loaded yet** (`LoadPlayerData` comes next). In `Player.Awake`, `m_localPlayer` is not yet this player; use `m_nview.IsOwner()` |
| `ObjectDB.instance` | menu: `FejdStartup.Start` copy (via `AddComponent<ObjectDB>` + `CopyOtherDB`); game: the main scene's own ObjectDB (**inferred**; its `Awake` sets `m_instance = this`) | replaced per scene | |

**Useful public hooks and events:**
- `Game.m_playerInitialSpawn` (static event, first spawn per session).
- `PlayerProfile.SavingStarted` / `SavingFinished` (static `Action`).
- `ZNet.WorldSaveStarted` / `WorldSaveFinished` (static `Action`, server).
- `ZNet.s_onZNetStart` (public static `Action`, invoked in `ZNet.Start`).
- `Game.IsShuttingDown()`, `Game.WaitingForRespawn()`.

**Useful Harmony targets (private is fine):** `Minimap.LoadMapData` (postfix = pins restored), `Game.SpawnPlayer` (postfix = local player fully loaded), `Player.OnDeath` (owner only), `Game._RequestRespawn` (prefix = the player is about to be destroyed), `Game.Shutdown` (prefix = the last point where the player and minimap exist before the final save).

## 9. Client vs server, deterministic vs stored (quick reference)
- **Client-local only:** Minimap, all pins (user, death, bed, location icons), explored fog. Only the fog and the `m_save == true` pins (user, death, discovered-location and shared-map pins) are stored in the character `.fch` per world UID. The bed, location-icon, player, ping/shout and event pins are `save: false` and are rebuilt at runtime (Minimap AddPin call sites / decompiled). Death pins are created on the dying client. The minimap textures come from the seed (`WorldGenerator`), cached on the host only.
- **Server/host only:** world save (ZDOs, zones, locations, global keys, events), sleep detection (`UpdateSleeping`), portal connection, autosave warning broadcast, admin/ban lists.
- **Every peer:** autosave timer and character save; the server additionally pushes `"SavePlayerProfile"` to clients on autosave and console save.
- **Deterministic from the seed:** terrain, biomes, minimap base textures. **Stored in the save:** everything placed or changed (ZDOs), generated locations (`db2`), global keys.
