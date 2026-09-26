# How vanilla actually behaves (decompiled, Valheim 1.0.15; sections 14-18 and lines marked "1.0.16" on 1.0.16)

Mechanisms behind the API, each read from decompiled code (cited as `Type.Member`). This is the file
that answers "what will the game do if I...". Re-read the member with `scripts/decompile.ps1` before
relying on a detail after a game update.

Contents: 1. Map pins · 2. Map input gestures · 3. Map data, profiles and the Cartography Table ·
4. Input gating and the cursor · 5. HUD and messages · 6. Console · 7. Player lifecycle ·
8. World height and zones · 9. Keys vanilla uses · 10. BepInEx loader · 11. Container contents ·
12. Dungeon doors and Vegvisirs · 13. The game's runtime: parsing, formatting and pasting ·
14. Object loading: what exists on this machine · 15. Characters · 16. Pathfinding tiles ·
17. Audio volume · 18. Rendering and the main camera

---

## 1. Map pins

- **`Minimap.AddPin`**: an out-of-range or negative `type` is coerced to Icon3 with a log warning; a null
  name becomes ""; `PinNameData` (the on-map label) is created only when the name is non-empty; the pin
  is appended to `m_pins`; if that icon type is filtered off
  (`(int)type < m_visibleIconTypes.Length && !m_visibleIconTypes[(int)type]`) it calls
  `ToggleIconFilter(type)` to turn the filter back on; sets `m_pinUpdateRequired`. It indexes
  `m_visibleIconTypes`, which is allocated in **`Minimap.Start`** — calling AddPin between Awake and Start
  throws. **`ToggleIconFilter`** starts with `GamepadRumble.instance.PlayGlobalSelectVibration()`, flips
  `m_visibleIconTypes[type]` and recolours the filter buttons — so every pin a mod adds while the player
  has that icon type hidden un-hides the type for all its pins and rumbles a gamepad (decompiled
  2026-09-24).
- **`Minimap.RemovePin(PinData)`**: `m_pinUpdateRequired = true; DestroyPinMarker(pin); m_pins.Remove(pin)`.
  `RemovePin(Vector3, float)` = `GetClosestPin(pos, radius)` + `RemovePin(pin)`; its only callers (1.0.16,
  find-usages 2026-09-26) are `RemovePinUnderPointer` (right click, touch long-press) and `UpdateMap` (the
  gamepad's `JoyTabRight`), so one prefix on it takes every delete gesture.
- **`Minimap.GetClosestPin(pos, radius, mustBeVisible)`** first skips every pin with `m_save == false`,
  then (if mustBeVisible) pins whose `m_uiElement` is missing or inactive, then picks the nearest by
  `Utils.DistanceXZ` within radius. **`HavePinInRange`** uses the same `m_save` skip. Consequence:
  `save:false` pins are invisible to every vanilla lookup — including `GetClosestPinToCursor`, the
  right-click delete, and `OnMapLeftClick`.
- **`Minimap.UpdatePins`** renders every pin in `m_pins` regardless of `m_save`. It destroys a marker only
  when the point is off-screen, its icon type is filtered, or `m_sharedMapDataFade <= 0 && m_ownerID != 0`.
- Readers of `PinData.m_save` (complete list): `GetMapData`, `GetSharedMapData`, `GetClosestPin`,
  `HavePinInRange`, `HaveSimilarPin`, and `AddPin` (writer).
- **`Minimap.ClearPins`** destroys every marker and clears `m_pins` without calling `RemovePin` (a
  RemovePin patch never sees it). Its only caller is `SetMapData`, which runs when the character's map
  data is loaded — so every pin object is replaced then.
- `m_pins` is assigned exactly once, in the constructor; the list object is stable for a Minimap's life.
- The dynamic updaters (`UpdatePingPins`, `UpdateShoutPins`, `UpdateLocationPins`, `UpdateEventPin`,
  `UpdatePlayerPins`) only remove pins from their own lists — they never touch other pins.
  `UpdatePingPins`, `UpdateShoutPins`, `UpdatePlayerPins` and `UpdateEventPin` add their pins (types Ping,
  Shout, Player, EventArea, RandomEvent) into `m_pins` with `save: false` and owner 0 — the same flags a
  mod marker uses, so those flags do not identify a mod's own pins. Ping, shout and player pins are
  **recreated whenever their count changes and otherwise overwritten by index**, so a held `PinData` can
  vanish or come to stand for a different ping or player (2026-09-24 review, decompiled).
- The spawn-point (bed) marker is `AddPin(..., PinType.Bed, "", save: false, ...)` in
  `UpdateProfilePins` — vanilla uses `save:false` for its own dynamic markers too.
- The tombstone marker's name is a localization token, e.g. `$hud_mapday 9`.

## 2. Map input gestures

`Minimap.Start` wires the large map's `UIInputHandler`: right click → `RemovePinUnderPointer`,
middle click → `OnMapMiddleClick`, left down/up → `OnMapLeftDown`/`OnMapLeftUp`. The handler is
EventSystem-driven, so it also fires for clicks on an IMGUI window drawn over the map (pitfalls.md
section 5).

- **`OnMapLeftUp`** calls `OnMapLeftClick` when the button was held less than `m_clickDuration`, then
  separately runs its own double-click test (`Time.time - m_leftClickTime < 0.3f` and
  `(pos - m_leftClickPosition).magnitude < PinInteractRadius`) which leads to `OnMapDblClick` —
  **whatever a prefix on `OnMapLeftClick` did**, so a mod that consumes a click still lets the second
  click of a pair through. The chain is `OnMapDblClick` → `ShowPinNameInput` → `AddPin(..., save: true)`,
  or `Chat.SendPing` when Ping is selected. `HidePinTextInput` and `OnPinTextEntered("")` only null
  `m_namePin`: the unnamed saved pin stays on the map. `OnMapDblClick`'s only caller is `OnMapLeftUp`;
  `ShowPinNameInput`'s are `OnMapDblClick` and `UpdateMap` (`find-usages`, 2026-09-24).
- **Dragging:** `UpdateMap`'s drag branch sits outside `if (takeInput)` and compares the timestamp
  `m_leftDownTime` with the duration `m_clickDuration`, so a drag arms on the press itself.
- **Gamepad (large map, inside `if (takeInput)` in `UpdateMap`):** `JoyTabRight` calls
  `RemovePin(ScreenToWorldPoint(screen centre), PinInteractRadius)` and then `HidePinTextInput` — it does
  **not** go through `RemovePinUnderPointer`, so a patch there covers only right-click and touch.
  `JoyTabLeft` acts on `GetClosestPin(screen centre, PinInteractRadius)`.
- **Callers of `RemovePin(Vector3, float)`:** `UpdateMap` (JoyTabRight), `RemovePinUnderPointer`, and, among
  mods, ServerDevcommands' `resetpins`, which loops `while (RemovePin(pos, r))`.
  **`RemovePinUnderPointer`'s callers:** `<Start>b__10_0` (right click) and `TouchLongPressPerformed` (only
  when `GetClosestPinToCursor() != null`). So `RemovePin(Vector3, float)` is the one choke point for every
  delete gesture (2026-09-24 review, decompiled and `find-usages -Plugins`).
- **`OnMapLeftClick`** does NOT place pins: `HidePinTextInput`; `GetClosestPin(ScreenToWorldPoint(pointer),
  PinInteractRadius)`; if found, clears its `m_ownerID` or toggles `m_checked` (the checkmark);
  `m_pinUpdateRequired = true`.
- **`OnMapDblClick`** is the "place a pin" gesture (opens the name input) unless the selected type is
  Death; with Ping selected it sends a ping via `Chat.instance.SendPing`.
- **`RemovePinUnderPointer`**: log, `HidePinTextInput(false)`,
  `RemovePin(ScreenToWorldPoint(pointer), PinInteractRadius)`.
- **`ScreenToWorldPoint`** is pure uvRect maths (`RectTransformUtility` → `MapPointToWorld`) with no fog or
  explored check — a click anywhere, including unexplored fog, yields exact world X/Z.
- `PinInteractRadius` is `m_removeRadius * LargeZoom * 2` (×1.3 on touch) — a constant fraction of the
  map image height on screen at any zoom. **Corrected 2026-09-23: `m_removeRadius` is 300 in the shipped
  prefab, not the 128f this file quoted** (that is the field initialiser; read out of the loaded
  `Minimap` component in the running game, `prefab-constants.json` in the SeedLab data snapshot). So the
  interact radius is ≈**60 m** of world at the code-default `m_largeZoom = 0.1`, not ≈25 m — a pin up to
  60 m away answers a click. Anything that tuned a gesture against 25 m is picking pins from 2.3× the
  area it expected. **Unverified:** whether the prefab also overrides `m_largeZoom` (0.1 in code), which
  scales this.
- Other Minimap fields where the prefab overrides the code default, from the same dump:
  `m_textureSize` **2048** (code 256), `m_pixelSize` **12** (64f), `m_exploreRadius` **50** (100f),
  `m_exploreInterval` **0.25 s** (2f).
- Vanilla reads no modifier keys in the map click path (only `OnMapMiddleClick` checks LeftControl,
  behind debug mode), so a modifier+click gesture is free for mods. `Player.TakeInput` is false while
  the large map is open, so held modifiers have no gameplay effect there.

## 3. Map data, profiles and the Cartography Table

- **`Minimap.GetMapData`** (the character profile's map) writes explored bits and, per pin with
  `m_save == true` only: name, pos, type, checked, ownerID, author.
- **`Minimap.GetSharedMapData`** (Cartography Table payload) writes explored bits and every pin with
  `m_save && m_type != PinType.Death`, attributing unowned pins to the local player id.
- **`MapTable.OnWrite`** → `MapTable.GetMapData(current)` → `Utils.Compress(Minimap.instance.GetSharedMapData(current))`
  → RPC "MapData" → stored in the table's ZDO (`ZDOVars.s_data`).
- **`MapTable.OnRead`** → `Minimap.instance.AddSharedMapData(Utils.Decompress(zdoBytes))`.
- **`AddSharedMapData`**: merges explored bits; marks every pin with `m_ownerID != 0 && != localId` as
  `m_shouldDelete`; for each incoming pin, if `HavePinInRange(pos, 1f)` it un-marks the closest (same
  `m_save` filter, so consistent), else adds it with `save: true` and the sender as owner; finally removes
  still-marked foreign pins with a backwards index loop. Pins with ownerID 0 are never touched.
  `MapTable.OnRead` has no `IsServer` check, so a joined client reads tables too (1.0.16, 2026-09-26).
- **Where exploration comes from** (1.0.16, 2026-09-26):
  - `m_explored` (own): `UpdateExplore` → `Explore` (the player's position), `ExploreAll` (console), and
    `SetMapData` (profile restore).
  - `m_exploredOthers`: only `AddSharedMapData` → `ExploreOthers` (a table read), and `SetMapData` →
    `ResetAndExplore`.
- **No Map** (1.0.16, 2026-09-26).
  - `Game.UpdateNoMap` sets `Game.m_noMap` from `GlobalKeys.NoMap`, **or** from the per-character
    `PlatformPrefs` value `mapenabled_<playerName>` = 0. So No Map can be one character's own choice, not only the
    host's.
  - While it is set, `Minimap.SetMapMode` forces `MapMode.None`: the large and small map roots are inactive, so
    there are no visible pins or markers and no map clicks. The `nomap` command run as host sets the global key
    for everyone.
  - **Exploration continues anyway:** `Minimap.Update` calls `UpdateExplore(deltaTime, localPlayer)` before any
    map-mode handling, and `AddSharedMapData` has no No Map check. So `m_explored` keeps growing and table reads
    still merge while the map is hidden.
  - Server Devcommands can lift No Map for one player (its `DisableNoMap` patch, a postfix on `Game.UpdateNoMap`).

## 4. Input gating and the cursor

- **`Player.TakeInput()`** (protected) returns false if any of: `Chat.HasFocus`, `Console.IsVisible`,
  `TextInput.IsVisible`, `StoreGui.IsVisible`, `InventoryGui.IsVisible`, `Menu.IsVisible`,
  `TextViewer.IsVisible`, `Minimap.IsOpen`, `GameCamera.InFreeFly`, the barber GUI, plus Hud radial and
  similar. It gates hotbar keys 1–8 (`Player.Update` reads `ZInput.GetButtonDown("Hotbar{n}")`), Use,
  attacks and more.
- **`PlayerController.TakeInput(bool look)`** (private) gates movement and mouse look.
- **`TextInput.IsVisible()`** callers (complete): `Player.TakeInput`, `PlayerController.TakeInput`,
  `Chat.Update`, `Menu.Update` (Escape-opens-menu), `Minimap.Update`, `GameCamera.UpdateMouseCapture`.
  NOT consulted by `InventoryGui.Update` (so Tab still works) or by the console bind dispatcher.
  **Unchanged in 1.0.16** (`find-usages.ps1 -Needle "TextInput::IsVisible"`, 2026-09-26; the body is
  `if ((bool)m_instance) return m_instance.m_visibleFrame; return false;`). While it is true:
  `Player.TakeInput` is false, so `Player.Update` skips its whole input block (Use, Hide, ToggleWalk, the
  radial/emote menu, Guardian power, AutoPickup, Hotbar1-8) but still runs `UpdateHover`;
  `PlayerController.TakeInput` is false, so `FixedUpdate` sends all-false controls (no movement, jump,
  attack, block, crouch, dodge) and `LateUpdate` zero look; in `Minimap.Update` the Map key, the large
  map's Escape/`JoyButtonB`/`JoyMap` close and everything under `UpdateMap(takeInput: false)` (wheel zoom,
  `MapZoomIn/Out`, gamepad pin and zoom keys, small-map zoom) are dead, but the large map's drag and the
  gamepad left-stick pan are outside `takeInput` and still work. An already-running auto-run continues
  (`Player.SetControls` with all-false input meets none of its stop conditions). (MobTracker review,
  decompiled 1.0.16, 2026-09-26.)
- **Escape and gamepad B:** `Menu.Update`'s open-on-Escape and `Minimap.Update`'s Escape/`JoyButtonB` close
  gate on `!TextInput.IsVisible`, so a mod forcing that flag true makes Escape do nothing. `Console.Update`
  and `InventoryGui`'s hide branch handle Escape without a TextInput check; `InventoryGui.Update` and
  `StoreGui.Update` gate their Escape/B/Use hide branches on `!Chat.HasFocus` (2026-09-24 review).
- **`Chat.HasFocus` gates what `TextInput.IsVisible` does not:** `InventoryGui.Update` (Tab / `JoyButtonY`),
  `GameCamera.UpdateCamera` (wheel and gamepad zoom), `HotkeyBar.Update` and `Player.UpdatePlacementGhost`;
  also `StoreGui.Update`, `TextInput.Update` and `Minimap.Update`. Each is a `!HasFocus()` suppress-gate;
  `Chat.Update` itself uses `m_wasFocused`. Other mods read it too, and the Chatter mod overrides it
  (check your own install with `scripts/find-usages.ps1 -Needle "Chat::HasFocus" -Plugins`).
- **`ZInput.GetMouseScrollWheel()`** is `m_instance?.Internal_GetMouseScrollWheel() ?? 0f`, and the internal
  method calls `OnInput(allowSwitchInputSource: true)` when the wheel moved — so suppress the wheel with a
  **postfix**; a skipping prefix would also drop the input-source switch. IMGUI is unaffected:
  `GUI.EndScrollView` scrolls from `Event.current` (`EventType.ScrollWheel`), not from ZInput.
- **`GameCamera.LateUpdate`** calls `UpdateMouseCapture()` every frame. If none of Hud radial,
  InventoryGui (not waiting for stack), `TextInput.IsVisible`, `Menu.IsActive`, `Minimap.IsOpen`,
  `StoreGui.IsVisible`, piece selection, barber, UnifiedPopup, password dialog is active →
  `ZCursor.LockState = Locked; ZCursor.Hide()`. Otherwise (and no menu visible) →
  `ZCursor.LockState = None; ZCursor.Show()`. Ctrl+F1 toggles `m_mouseCapture`.
- **`Chat.HasFocus()`** = chat window active && input field focused. **`Console.IsVisible()`** = console
  window active; the dev console is disabled unless enabled (`PlatformPrefs "EnableConsole"`, or the
  `-console` launch option).
- **`Chat.Update`** dispatches console key binds (`Terminal.m_binds`) whenever the chat input is not
  focused and the console window is not open — a mod window does not suppress them. **With
  ServerDevcommands installed, vanilla's dispatch is replaced:** its `InitializeChat` (a
  `Chat.Awake` postfix) calls `BindManager.Load`, which clears `Terminal.m_bindList` and `m_binds`; its
  `DisableDefaultBindExecution` transpiler makes `Chat.Update` skip the bind loop; and its
  `ExecuteBestBinds` postfix runs `binds.yaml`, gated only on `m_input.isFocused` or the console window
  being active. So no `TextInput` or `HasFocus` patch stops console binds (2026-09-24 review, decompiled).
- **`GameCamera.UpdateCamera`** reads `ZInput.GetMouseScrollWheel()` for zoom only when none of
  `Chat.HasFocus`, `Console.IsVisible`, `InventoryGui.IsVisible`, `StoreGui.IsVisible`, `Menu.IsVisible`,
  `Minimap.IsOpen`, `Hud.IsPieceSelectionVisible`, `Hud.InRadial`, cutscene, or rotatable piece placement
  applies — `TextInput.IsVisible` is **not** in the list, so the wheel zooms while a mod window is open.
  (Free-fly debug camera reads it too, in `UpdateFreeFly`.)

## 5. HUD and messages

- `Hud.Update` computes `SetVisible(!m_userHidden && !localPlayer.InCutscene())`; `Hud.IsVisible()` covers
  only those two. **`Player.InCutscene()`** is true for the cutscene animator tag, `InIntro()`,
  `m_sleeping` and `CinematicsManager.IsPlaying()` (`Character.InCutscene` returns false), so **sleeping,
  the intro and cinematics are already covered**; dead and teleporting still need explicit checks.
  (Corrected 2026-09-24, decompiled: this line said sleeping needs an explicit check.) `InventoryGui.Update`
  and `StoreGui.Update` also hide on `InCutscene`. Ctrl+F3 (or the gamepad HUD combination) does
  `m_userHidden = !m_userHidden` in `Hud.Update`. An IMGUI overlay draws above the game's canvas, so to
  hide with the HUD it must test these itself (and `Menu`/`InventoryGui`/`StoreGui.IsVisible()` to stay
  off the pause menu, inventory and trader).
- **`MessageHud.ShowMessage`** (re-read on 1.0.16, 2026-09-26), in order:
  1. `m_showDespiteHiddenHUD = showDespiteHiddenHUD` - **on every call**, so a caller passing the default
     false resets a true another caller set; while the HUD is hidden `MessageHud.Update` then calls
     `HideAll()`, which can hide a message that was meant to show despite the hidden HUD.
  2. `if (Hud.IsUserHidden() && !showDespiteHiddenHUD) return;` - dropped silently: not shown, not logged.
  3. The text is localized.
  4. `TopLeft` is enqueued. `MessageHud.UpdateMessage` dequeues the next one once `m_msgQueueTimer`
     (reset at each dequeue) reaches 1 s, or at once when it repeats the text and icon of the message
     shown less than 4 s ago; a repeat adds its `m_amount` to the shown one, and " xN" is appended only
     when the sum exceeds 1, so amount-0 repeats merge silently. Frames with dt > 0.5 are skipped.
  5. `Center` writes `m_messageCenterText` at once and cross-fades it out over 4 s (`ignoreTimeScale`) -
     not queued, not de-duplicated: the next Center message from anyone replaces it, and two in one frame
     show only the last.
  6. With `log` true the text also goes to the message log.
  Chatter, when set to show Center messages, copies them from a `ShowMessage` postfix, so it records them
  even when vanilla dropped them.
- `Minimap.UpdateMap` feeds `Utils.GetMainCamera().transform.rotation` into `Minimap.UpdatePlayerMarker`,
  which rotates the map's player marker by `Quaternion.Euler(0, 0, -yaw)` — the game's own facing
  indicator is camera-driven, and uGUI rotation is the negative of a compass bearing.

## 6. Console

- `Terminal.ConsoleCommand`'s constructors begin by writing themselves into the static
  `Terminal.commands` dictionary (lower-cased name) — construction is registration.
- `Chat.isAllowedCommand` rejects only `IsCheat` commands; other commands can be typed in chat too, with a leading
  `/`. `Chat.InputText` turns text without `/` into `say <text>` and strips the `/` otherwise, then calls
  `TryRunCommand`. So a joined client can run a mod's non-cheat command (TomTom's `/waypoint ...`) from chat without
  the F5 console. A command's replies through `Terminal.AddString(string)` only append to the local chat buffer;
  nothing is sent to other players (1.0.16, 2026-09-26). In the dedicated server's DLL the same method first writes
  `ZLog.Log("Console: " + text)`, so on a server the console's lines land in the server's log ([S], decompiled
  2026-09-26; multiplayer.md section 1.4).
- The F5 console is enabled **per machine**: `Console.Awake` reads `PlatformPrefs.GetInt("EnableConsole") == 1`
  (Server Devcommands also patches `Console.IsConsoleEnabled`). None of this depends on the server.
- Vanilla `pos` is `hideBehindDevCommands`; `goto`, `find`, `findbiome`, `exploremap` are cheats — a
  normal player has no coordinate readout or teleport without devcommands.
- **`pos` prints** `string.Format("Player position (X,Y,Z): {0} , Zone: {1}, Center dist: {2}",
  position.ToString("F0"), ZoneSystem.GetZone(position), Utils.DistanceXZ(Vector3.zero, position))`, e.g.
  `Player position (X,Y,Z): (1234, 30, -567) , Zone: 19,-9, Center dist: ...` — whole numbers, in X, Y
  (altitude), Z order, and the zone is a `Vector2s` printed `x,y` (`Terminal.InitTerminal`, decompiled
  2026-09-24). Pasted whole into TomTom, such a line lands correctly only in raw mode, and a zone y of 3 or
  more digits trips the digit-grouping refusal.

## 7. Player lifecycle

- **Death**: `Player.m_localPlayer` becomes null ("Local player destroyed" in the log) while the Minimap,
  its pins, ZNet and the world all survive; `Game` respawns a new Player ("Starting respawn" — also
  logged on the first spawn into a world). Confirmed live with TomTom on 2026-09-22.
- **Logout**: player and the game scene (Minimap included) are destroyed; the main menu logs
  `Valheim version: ...` again.
- **World identity**: `ZNet.GetWorldUID()` is stable per world; clients receive the real UID, seed and
  world-gen version from the server in `ZNet.RPC_PeerInfo`, which then calls `WorldGenerator.Initialize`.
- **`WorldGenerator.Initialize`** is also called from `FejdStartup.Awake` with the menu world, so
  `WorldGenerator.instance` exists at the main menu (seed 0). On a joining client, `ZNet.Awake` clears it
  and it stays null until `RPC_PeerInfo`. Full startup order: game-operations.md.
- `PlayerProfile.SavingStarted` / `SavingFinished` are public static `Action`s the game itself subscribes to.

## 8. World height and zones

- **`WorldGenerator.GetHeight(wx, wz)`** is procedural (`GetBiome` → `GetBiomeHeight`) and returns an
  absolute world Y — no colliders, no loaded zone. `ZoneSystem.GenerateLocations` uses it the same way.
  It is an estimate of the real ground: the terrain builder blends the four corner biomes of each
  heightmap cell and terrain edits apply on top (see the valheim-worldgen skill).
- **`ZoneSystem.GetGroundHeight(Vector3)`** raycasts from y=6000 down 10000 against terrain and returns
  the caller's own y on a miss. **`GetSolidHeight(p, out h, margin)`** raycasts from `p.y + margin`, 2000 m.
- **Everything `WorldGenerator` does from the seed is reproducible outside the game**, bit-for-bit:
  biomes, heights, rivers and the whole location table. Unity's `Mathf.PerlinNoise` and
  `UnityEngine.Random` — the two native pieces — were recorded from the running game and solved
  (the valheim-worldgen skill, `world-generator.md` section 9). The offline tool is the **seedlab** skill; use
  it instead of writing new world-generation code, and for anything a mod would otherwise have to
  measure in-game.
- **Straight, kilometre-long biome edges in the map are vanilla**, and predictable from the seed's noise
  offsets: the valheim-worldgen skill, `world-generator.md` section 5.1.
- `ZoneSystem`'s shipped prefab values differ from the code defaults where it matters for timing:
  `m_zoneTTL` **10 s** (code 4f), `m_zoneTTS` **5** (4f), `m_locationVersion` **32** (1).
- More in the valheim-worldgen skill.

## 9. Keys vanilla uses (verified 1.0.15 — re-check with scripts/find-key-usage.ps1)

| Key | Vanilla use |
| --- | --- |
| Ctrl+F1 | toggle mouse capture (`GameCamera.UpdateMouseCapture`) |
| F2 | connect/network panel (`ConnectPanel.Update`) |
| Ctrl+F3 | hide HUD (`Hud.Update`) |
| F5 | console (rebindable button "Console", `ZInput.ResetKBMButtons`) |
| F9 | cycle gamepad controller layout (`KeyHints.Update`) |
| F11 | screenshot to `LocalLow\IronGate\Valheim\screenshots` (`GameCamera.LateUpdate`, `FejdStartup.LateUpdate`) |
| F4, F6, F7, F8, F10, F12, plain F1/F3 | unused by **vanilla** (F12 is Steam's default screenshot key). Installed mods often hold several of them (ConfigurationManager takes F1), and SeedLab's dumper holds F4 while it is armed: check your own install with `scripts/find-key-usage.ps1 -Plugins` |

## 10. BepInEx loader

- `Chainloader.Start` drops, in one pass, any plugin whose `[BepInIncompatibility]` GUID is present; with
  mutual incompatibility exactly one loads and the other logs `Could not load [X] because it is
  incompatible with: Y`.
- `TomlTypeConverter` handles primitives and enums, and lazily adds Unity types (Color, Vector2/3/4,
  Quaternion, Rect) on first use.
- `KeyboardShortcut.IsDown()` goes through `BepInEx.UnityInput.Current` (legacy Input probe) — prefer ZInput.

## 11. Container contents are NOT seed-determined (DropTable, decompiled 2026-09-23)

`DropTable.GetDropListItems()` draws with **`UnityEngine.Random.value` / `UnityEngine.Random.Range`** —
the global, unseeded Unity RNG — at the moment the container spawns. It never touches the world seed.
So the world seed fixes **where** a chest is, never **what is in it**. Any tool that claims to find "the
chest containing item X" from a seed is wrong; the honest claim is "the location types whose chests can
contain X, and where those are".

The draw loop, exactly:

```csharp
if (Random.value > m_dropChance) return empty;         // whole table can miss
int n = Random.Range(m_dropMin, m_dropMax + 1);        // how many entries
for (i < n) { pick by weight over the remaining entries;
              if (m_oneOfEach) remove that entry and subtract its weight; }
```

`m_oneOfEach` therefore means draws **without replacement**, so an entry's chance rises with each
further draw. Worked example — the axe-head houses, the only two location prefabs in 1.0.15 that can
yield one (see the worldgen reference, 5.3): `dropMin 2, dropMax 3, dropChance 1, oneOfEach true`,
seven entries totalling weight 8, axe head weight 2. P(axe head, given the chest exists) = 15/28 on a
2-item roll and 9/14 on a 3-item roll, and the two roll sizes are equally likely, so
**31/56 = 55.4 %**. *Arithmetic on the dumped table plus this loop; not measured in game.*

**Keep that separate from whether the chest exists at all**, which is a different roll of a different
kind. `WoodHouse6`'s chest is a plain child and is always there, so that house is 31/56.
`WoodHouse2`'s is `RandomSpawn` entry 50 with `m_chanceToSpawn 50`, and that draw comes out of the
zone-seeded stream `ZoneSystem.SpawnLocation` opens with
`Random.InitState(worldSeed + zx*4271 + zy*9187)` — so it **is** seed-determined and an offline
tool can compute it, unlike the contents. That house is 31/112 = 27.7 %. The two uncertainties must
never be collapsed into one number.

## 12. Dungeon doors (`Teleport`) and `Vegvisir` - decompiled 2026-09-24

**A dungeon's player-facing name is `Teleport.m_enterText`, and it is not in code.** `Teleport` has
four public fields: `m_hoverText` (default `"$location_enter"`), `m_enterText` (default `""`),
`m_targetPoint` (a `Teleport`) and `m_hoverOffset`. `Teleport.Interact(character, hold, alt)`:

- returns false on `hold`, and false when `m_targetPoint == null`;
- with the `NoBossPortals` global key set, refuses (`"$msg_blockedbyboss"`, Center) when the character
  is `InInterior()` and `Location.IsInsideActiveBossDungeon(position)`;
- otherwise `character.TeleportTo(m_targetPoint.GetTeleportPoint(), m_targetPoint.transform.rotation,
  distantTeleport: false)`, where `GetTeleportPoint()` = `position + forward - up` of the TARGET; on
  success it increments `PortalDungeonOut` when the character is `InInterior()`, else `PortalDungeonIn`,
  and **if `m_enterText.Length > 0` calls `MessageHud.instance.ShowBiomeFoundMsg(m_enterText,
  playStinger: false)`** - the big caption on walking into a crypt.

`OnTriggerEnter` calls `Interact` for the local player's collider, so a door is walk-through as well as
use-key. **No code assigns `m_targetPoint`** - `find-usages` shows only `Teleport.Interact` reading it -
so the door-to-door link is authored in the prefab. `m_enterText` has one code reference besides
`Interact`: the constructor's `""`. The `$location_*` tokens a player sees (`location_forestcrypt` =
"Burial Chambers", `location_sunkencrypt` = "Sunken Crypts", `location_mountaincave` = "Frost Caves",
16 `location_*` keys in the English table) therefore live only on prefabs inside the SoftRef bundles.

**Which prefab carries which token - read from the prefabs by SeedLab dumper run 6 (2026-09-24,
Valheim 1.0.15).** 38 `Teleport`s sit in 19 location prefabs and none in any of the 358 dungeon rooms.
Each prefab has one entrance (hover `$location_enter`, a caption) and one exit (`$location_exit`, empty
caption), each targeting the other inside the same prefab. The captions: Crypt2/3/4
`$location_forestcrypt` "Burial Chambers" (all three - the name is shared); TrollCave02
`$location_forestcave` "Troll Cave"; SunkenCrypt4 `$location_sunkencrypt` "Sunken Crypts"; MountainCave02
`$location_mountaincave` "Frost Caves"; Mistlands_DvergrTownEntrance1/2 `$location_dvergrtown` "Infested
Mine" (shared); Mistlands_DvergrBossEntrance1 `$location_dvergrboss` "Infested Citadel" (the Queen's
arena); MorgenHole1/2/3 `$location_morgenhole` "Putrid Hole" (shared); PlaceofMystery3
`$location_mausoleum` "Tomb of Lord Reto"; TheHole01 `$location_thehole` "Winding tunnels"; MorkBorg
`$location_morkhalla` "Mörkhalla"; BearCave `$location_bearcave` "Bear Cave"; Hildir_cave `$hud_pin_hildir2`
"Howling Cavern" and Hildir_crypt `$hud_pin_hildir1` "Smouldering Tomb" (Hildir's doors use her map-pin
tokens, not `location_*`). DN_Bossroom's door `$location_dnbossroomnew` ("The Prison") is **inactive** in
the prefab - what enables it is not in the dump. `$location_dnbossroom` ("The First Prison") and
`$location_darkesthole` ("The Hole") are on no door. The Valheim wiki contradicts none of these; it has no
page for Bear Cave, Winding tunnels or Mörkhalla ("Gates of Mörkhalla" in its future-content page).

**Vegvisirs in the prefabs (same run):** 25 in location prefabs and 20 in rooms. All pin boss places,
DN_Bossroom ("Aesir Passage") or PlaceofMystery1/2/3; the chain MorgenHole1/2/3 -> PlaceofMystery1 -> 2 -> 3
is pinned "Mysterious Location" with pin type Hildir1. `hildir_maptable` is a Vegvisir with 3 entries,
`m_discoverAll` true, setting the player key `HildirMap`, pin types 14/15/16 - and it pins Hildir's two
dungeons with the same tokens her doors carry.

**`Vegvisir`** fields: `m_name` (`"$piece_vegvisir"`), `m_useText` (`"$piece_register_location"`),
`m_hoverName` (`"Pin"`), `m_hoverOffset`, `m_setsGlobalKey`, `m_setsPlayerKey`, and
`List<Vegvisir.VegvisrLocation> m_locations` (the game's spelling: *Vegvisr*), each entry
`m_locationName` (`""`), `m_pinName` (`"Pin"`), `Minimap.PinType m_pinType`, `m_discoverAll`
(tooltip: "Discovers all locations of given name, rather than just the closest one.") and `m_showMap`
(`true`). `Interact` returns false on `hold`; otherwise, per entry in list order,
`Game.instance.DiscoverClosestLocation(m_locationName, transform.position, m_pinName, (int)m_pinType,
m_showMap, m_discoverAll)` plus an analytics event, then sets the global key and (for a `Player`) the
player unique key when those are non-empty. Like a `RuneStone` (see pitfalls, "A RuneStone names the
place it REVEALS"), **a Vegvisir names the places it points at, never its host.**

## 13. The game's runtime: parsing, formatting and pasting (decompiled 2026-09-24)

A mod runs on the game's own Mono mscorlib (`valheim_Data\Managed\mscorlib.dll`), not on the .NET a test
project uses, and text handling differs. Decompiled from the game's mscorlib during the 2026-09-24 TomTom
review, and confirmed by running tests under the Unity 6000.0.75f1 editor's `mono.exe` with
`MONO_PATH=<game>\valheim_Data\Managed` (`typeof(object).Assembly.Location` = the game's mscorlib;
environment.md):

- **`Single.TryParse` accepts `NaN`, `Infinity` and `-Infinity` by ordinal comparison after the numeric
  parse fails, whatever the `NumberStyles`** — case-sensitive, so `nan` is refused, while .NET 10 accepts
  it. Either way a parser needs its own finite check (pitfalls.md section 10).
- **`Number.IsWhite` is only U+0020 and U+0009-U+000D**, so `AllowLeadingWhite`/`AllowTrailingWhite` do not
  cover a no-break space (U+00A0) or thin space; `string.Trim` does strip U+00A0 at the ends.
- **The sign is matched exactly** (U+2212 MINUS SIGN is not a minus) and **`IsDigit` is ASCII only**
  (full-width digits are not digits).
- **`RoundNumber` clears the sign when every digit rounds away**: `(-0.4f).ToString("0")` is `"0"` in the
  game and `"-0"` on .NET 10.
- **A culture-sensitive `StartsWith("//")` ignores leading LRM, BOM and ZWJ characters** on this corlib,
  so an invisibly prefixed `// 100 200` still counts as a comment; the ordinal overload would not.
- **`UnityEngine.TextEditingUtilities.Paste` inserts the clipboard verbatim** into a multiline IMGUI
  `TextArea`, CR/LF included — a pasted Windows list arrives with `\r\n` line ends.

## 14. Object loading: what exists on this machine (decompiled 1.0.16, 2026-09-26)

From the 2026-09-26 review of the MobTracker plugin: investigators plus refute-by-default verifiers, and
`SimulationDistance.GetSimulationDistance`, `ZDOMan.FindSectorObjects`, `ZoneSystem.ZonesWithinRadius`,
`Character.CustomFixedUpdate`, `Character.SetVisible` and `ZNetScene.PointInsideActiveArea` re-read by
hand. Ownership and what a client receives: multiplayer.md sections 3.2 and 3.5.

**The setting.** `ZoneSystem.m_activeArea` and `m_activeDistantArea` **do not exist in 1.0.16**. The
loaded area is the struct `SimulationDistance(near, far, classic)`, from the graphics setting "Simulation
distance" (menu label "Draw distance"), stored under the PlatformPrefs key `"SimulationDistance"` (on Steam
that is Unity PlayerPrefs: `SteamPlatform` has no `PreferencesProvider`).
- `SimulationDistance.GetSimulationDistance(level)`: 0 = (1,2) classic; 1 = (2,2) disc; 2 =
  `OriginalDistance` (2,2) classic, the code default (`s_defaultGraphicsSettings.m_simulationDistance = 2`);
  3/4/5 = (3|4|5, 2) disc; any other level (level, 2) disc. A negative level is clamped to 0. The UI offers
  0..6 (`GraphicsSettingIntExtensions.GetRange`); a stored level above 6 is used as (level, 2) with only a
  log warning. "Classic" loads a square, "disc" a circle of whole zones.
- **Value in use:** `ZNet.GetSyncedSimulationDistance()` = the smaller of `ZNet.m_simulationDistance` (the
  server-validated value) and the desired one (`GraphicsSettingsManager.Instance.GetSimulationDistance()`),
  compared with `SimulationDistance.operator<`, a partial order: near strictly smaller and far <=, or equal
  near and far with the left side disc and the right side classic (classic ranks higher).
- On a host or in singleplayer `ZNet.SimulationDistanceServerHandshake` calls `ApplySimulationDistance(desired)`,
  which sets `ZNet.m_simulationDistance` and, through `ZoneSystem.ApplySettings`, ZoneSystem's own copy. It
  runs from `ZNet.Awake` and on every `GraphicsSettingsManager.GraphicsSettingsChanged`, and returns early
  when the value is unchanged.
- The launch option `-simulationdistance N` (`FejdStartup.ParseArguments` -> `ZNet.s_onZNetStart` -> one
  `ApplySimulationDistance` in `ZNet.Start`) can only **lower** a player's own range, because the synced
  value is the minimum; on a server it sets the cap handed to clients.
- The menu text "square/circular $1 m", with $1 = (2·(near+far)+1)·32 (224, 288, 288, 352, 416, 480, 544 m
  for levels 0-6), is the half-width of the near+far area, which only `Distant` objects reach - ordinary
  objects stop much closer (table below). The `$settings_simulationdistance` token is from SeedLab's
  1.0.15 localization data.

**The tick.** `ZNetScene.Update` runs `CreateDestroyObjects` whenever its `deltaTime` timer reaches 1/30 s
(then resets it to 0): zone = `ZoneSystem.GetZone(ZNet.GetReferencePosition())`, then
`ZDOMan.FindSectorObjects(zone, synced, near, distant)`, `CreateObjects`, `RemoveObjects`. Selection uses
each ZDO's horizontal sector only: **height plays no part**. The reference position is the local player's
feet, written every frame by `Player.LateUpdate` (other writers: multiplayer.md section 5.2), so loading
lags the player by one frame.

**`ZDOMan.FindSectorObjects` geometry.** Near list: every ZDO in the player's zone and in rings 1..N - the
whole square in classic mode; in disc mode only zones passing `ZoneSystem.ZonesWithinRadius(zone, z, N)`,
i.e. `|centreA - centreB|² < (N·64 + 0.5·64)²` on horizontal zone centres (`GetZonePos = (x·64, 0, y·64)`).
Distant list: only ZDOs with the `ZDO.Distant` flag, from rings `(classic ? N+1 : 1) .. N+F`, disc radius
`(N+F)·64 + 0.8·64`, skipping sectors already scanned.

Loaded radius around the player, per level. "Guaranteed" is the largest disc always fully loaded, at the
worst player position (zone edge or corner), with the zone-centre figure after the slash. "Max reach" is
the farthest loaded point, from a player at the zone centre to the worst case. "Owned block" is the area
whose objects get an owner (multiplayer.md section 3.2), guaranteed / max. Computed by two independent
reimplementations of `FindSectorObjects` and `ZonesWithinRadius` (one float32-emulated) sweeping player
positions within a zone; they agree to the decimal.

| Level | (near, far) mode | Near zones | Guaranteed | Max reach, ordinary | Max reach, `Distant` | Owned block |
|---|---|---|---|---|---|---|
| 0 | (1,2) classic | 9 (3×3) | 64 / 96 m | 135.8-181.0 m | 316.8-362.0 m | 32 / 135.8 m |
| 1 | (2,2) disc | 21 | 90.5 / 135.8 m | 186.6-230.8 m | 329.5-373.2 m | 64 / ≈157.3 m |
| 2 (default) | (2,2) classic | 25 (5×5) | 128 / 160 m | 226.3-271.5 m | 407.3-452.5 m | 64 / 181.0 m |
| 3 | (3,2) disc | 37 | 143.1 / 186.6 m | 243.7-286.2 m | 407.3-452.5 m | 64 / 181.0 m |
| 4 | (4,2) disc | 69 | 230.8 / 275.3 m | 329.5-373.2 m | 472.5-516.0 m | 64 / 181.0 m |
| 5 | (5,2) disc | 97 | 271.5 / 316.8 m | 386.7-429.3 m | 529.7-572.4 m | 64 / 181.0 m |
| 6 | (6,2) disc | 137 | 344.7 / 386.7 m | 454.8-499.9 m | 595.2-640.0 m | 64 / 181.0 m |

**Seen live** (2026-09-26, the user in a throwaway world with god mode and flying on, through MobTracker, which lists
exactly the loaded Characters): a tracked creature was dropped as unloaded at about 210 m. That fits the
ordinary-creature bands of level 3 and of level 2 alike, so it confirms the order of magnitude (well beyond
sight range, well short of the menu's "352 m") but does not show which level applied.

- The `Distant` column applies only to objects whose prefab has `ZNetView.m_distant` true: `ZDO.Distant` is
  copied from that serialized field in `ZNetView.Awake` (or restored from save or network data), and no game
  code writes it at runtime. **Unverified:** whether any creature prefab sets it (serialized data).
- `ZoneSystem.m_zoneSize` is 64 by field initializer and in the SeedLab 1.0.15 dump; its 1.0.16 serialized
  value was not re-read, but `GetZone` and `GetZonePos` hard-code 64 anyway.

**Creation** (`ZNetScene.CreateObjects` -> `CreateObjectsSorted`):
- Nothing near is created until `ZoneSystem.IsActiveAreaLoaded()`: every near zone around the reference
  position is already in `ZoneSystem.m_zones` (disc mode tests only in-radius zones).
- `ZoneSystem.Update` creates at most **one** local zone per >0.1 s tick, only while ZNet is connected (on a
  server, only after `LocationsGenerated`) and `HeightmapBuilder.IsTerrainReady`, so objects appear in
  batches after the terrain.
- Order: `ObjectType` descending (Terrain 3 > Solid 2 > Prioritized 1 > Default 0), then **3D** squared
  distance (`Utils.DistanceSqr`) to the reference position - dungeon interiors ~5000 m up come last within
  their type.
- Cap per tick: `max(count/100, 10)`, or `max(count/100, 100)` while `Player.m_localPlayer` is null or
  teleporting (`ZNetScene.InLoadingScreen`). ZDOs in a zone failing `ZoneSystem.IsZoneReadyForType` (a
  `LocationProxy` or `DungeonGenerator` still loading) are skipped.
- `Distant` objects are created afterwards with no terrain or zone gate, and that step is skipped once
  `created > 10` (100 in the loading screen).
- Anything instantiated runs its `Awake` at once, even outside the loaded set; the next tick removes it.
- After a teleport `Player.UpdateTeleport` waits at least 2 s (8 s for a distant teleport) and then until
  `ZNetScene.IsAreaReady(target)`: every valid ZDO in the 3×3 block (`SimulationDistance(1,0)`) is
  instanced. The outer rings stream in afterwards.

**Removal** (`ZNetScene.RemoveObjects`): each ZDO in this tick's lists is marked with
`(byte)(Time.frameCount & 0xFF)`; every instance whose ZDO is unmarked gets `ZNetView.ResetZDO()` and
`Object.Destroy` (plus `DestroyZDO` when the ZDO is non-persistent and owned here). There is no gate.
`ResetZDO` clears `ZDO.Created`, so the object is re-instantiated when its zone returns. Instances are
destroyed only in `RemoveObjects`, `OnZDODestroyed`, `Destroy` and `Shutdown` (logout), so a change of owner
never re-creates an object. All four reset the ZDO before `Object.Destroy`, so until Unity destroys the
object it is still there with `m_nview.IsValid() == false` (**Unverified:** that it stays non-null for the
rest of the frame - engine behaviour).

**Server ghost zones.** On a server `ZoneSystem.Update` generates Ghost zones around **every** peer's
reference position; objects those locations and rooms place directly exist for one frame, possibly far from
the host. The 1.0.15 dump has three Characters placed that way: BogWitchKvastur (BogWitch_Camp), FrozenKing
(DN_Bossroom) and SeekerQueen (dvergr_new_bossroom_ENTRANCE02).

**Loaded is not visible, and visible is not simulated.** `Character.CustomFixedUpdate` (run for
`Character.Instances` by `MonoUpdaters.FixedUpdate`) calls `SetVisible(zdo.HasOwner())`; `Character.SetVisible(false)`
moves `LODGroup.localReferencePoint` to (999999, 999999, 999999), which culls the model (a no-op with no
LODGroup). Motion, `CheckDeath` and `BaseAI.UpdateAI` run only when `zdo.IsOwner()`. Owners are handed out
only in the block around a player (table), so every creature loaded beyond it is **invisible and frozen**
(no owner) or **visible but frozen** (a stale owner that moved away). Visible = `GetZDO().HasOwner()`;
simulated by this machine = `ZNetView.IsOwner()`. **Unverified:** that every creature prefab has a
LODGroup on its "Visual" child, and that creature ZNetViews are persistent (serialized data).

**Dungeon interiors load with the surface above them.** Interiors sit about 5000 m above their zone
(valheim-worldgen `zones-locations-vegetation.md`: `Location.Awake`, `Character.InInterior` is y > 3000),
and `DungeonGenerator.TestCollision` -> `IsInsideDungeon` rejects rooms outside
`Bounds(m_zoneCenter, m_zoneSize)`, so an interior stays within its 64×64 m zone. Because loading and
ownership ignore height (`PointInsideActiveArea` zeroes y), creatures that already exist inside a loaded
dungeon are instantiated - and, inside the owned block, simulated - while the player is on the surface,
about 5 km away by 3D distance; the reverse holds too. Exclude them with `Character.InInterior`, or measure
with `Utils.DistanceXZ`. Interior creatures exist only once someone has been inside:
`CreatureSpawner.UpdateSpawner` runs on the spawner's owner every `m_spawnInterval` (initializer 5 s) and
needs `Player.IsPlayerInRange(pos, m_triggerDistance)`, a 3D test (initializer 60 m); per-spawner
serialized values are **Unverified**. Heights from the SeedLab 1.0.15 dump: Crypt2/3/4 at local y 5000,
SunkenCrypt4 at 5001; for the locations with `m_useCustomInteriorTransform` the runtime height is
**Unverified**. **Seen live** (2026-09-26, the user, through MobTracker's list): from inside a Burial Chamber a
surface Neck was listed 5033 m away, and from outside a Skeleton of the chamber 4996 m away - both directions
load, measured in 3D.

## 15. Characters (decompiled 1.0.16, 2026-09-26)

- **`Character.GetAllCharacters()`** returns the live `private static readonly List<Character> s_characters`
  itself, not a copy. Its only writers: `Character.Awake`, whose first statement is `s_characters.Add(this)`,
  and `Character.OnDestroy` (`m_seman.OnDestroy(); s_characters.Remove(this); ...`). `SetActive(false)` does
  not remove an entry (`OnEnable`/`OnDisable` maintain only `Character.Instances`). `Humanoid.Awake` and
  `Player.Awake` call `base.Awake()` first. So the list is "every Character GameObject between its `Awake`
  and its `OnDestroy`", i.e. what object loading (section 14) created, players included.
- **Only `Humanoid : Character` and `Player : Humanoid` derive from `Character`.** `Fish`,
  `RandomFlyingBird`, `Leviathan`, `Turret`, `Trader`, `Catapult`, `Ragdoll`, `Tail`, `Tameable`, the
  `BaseAI`/`MonsterAI`/`AnimalAI` family, `Growup`, `Procreation`, `CharacterDrop` and `SpawnAbility` are
  plain MonoBehaviours, so fish, birds, the Leviathan, turrets and corpses are never in the list.
  **Unverified for 1.0.16:** that vendors have a `Trader` and no `Character` (the 1.0.15 dump says so).
- **A stuck entry.** If `Awake` throws anywhere from the `Add` up to and including
  `m_seman = new SEMan(this, m_nview)`, `m_seman` stays null, `OnDestroy` throws before the `Remove`, and the
  entry stays for ever. `SEMan..ctor` calls `m_nview.Register`, so a Character with no ZNetView and no
  `m_nViewOverride` always gets stuck. `GetZDOID()` and `IsTamed()` dereference `m_nview` without a check:
  guard each entry when iterating.
- **`IsDead()` is true only on the creature's owner.** `Character.IsDead` is `return m_isDead;`, a local
  flag written only by private `Character.CheckDeath` (`!IsDead() && GetHealth() <= 0f`), which only
  `CustomFixedUpdate` calls, inside `if (zdo.IsOwner())`; it is never reset. On the owner it turns true at
  the first physics tick at 0 HP, before any death animation; on every other machine it stays false until
  the owner's routed destroy arrives (`ZNetScene.OnZDODestroyed`), so a dying creature stays "alive" there
  through its animation. `Character.OnDeath` returns at once on a non-owner. **`Player.IsDead()` overrides
  it** with `m_nview.GetZDO()?.GetBool(ZDOVars.s_dead) ?? false`, correct everywhere.
- **`IsTamed()`** = `IsTamed(Time.time)`: false when `!m_nview.IsValid()`; a non-owner re-reads ZDO
  `s_tamed` at most once per second per creature; the owner returns `m_tamed`, read in `Awake` and changed
  only by `RPC_SetTamed` (via `Character.SetTamed`). `Tameable.IsTamed` also returns `m_startsTamed`, so the
  two can disagree on a client before the ZDO flag arrives.
- **`GetLevel()`** returns `m_level` (initializer 1), read from ZDO `s_level` in `Character.Awake` only;
  vanilla calls `SetLevel` only in the method that instantiated the creature (`CreatureSpawner.Spawn`,
  `SpawnSystem.Spawn`, `SpawnArea.SpawnOne`, `TriggerSpawner.Spawn`, `SpawnAbility.Spawn`,
  `EggGrow.GrowUpdate`, `Growup.GrowUpdate`, `Procreation.Procreate`, the `spawn` command).
- **`GetCenterPoint()`** = `m_collider.bounds.center`, where `m_collider = GetComponent<CapsuleCollider>()`
  on the root, assigned only in `Awake`: the centre of the root capsule's world bounds, roughly mid-body.
- **Names.** `Utils.GetPrefabName(gameObject)` (game-api.md) equals the ZNetScene prefab name for a
  networked creature (`ZNetView.Awake` hashes the same string). `m_name` is a localization token;
  `Character.GetHoverName` also uses a tamed pet's given name (`Tameable.GetHoverName`) and ZDO
  `s_overrideHoverName`. `Character.IsPlayer()` is virtual `false`, overridden `true` only by `Player`.
  `Character.Faction` has 14 values, Players 0 .. DeepNorth 13 (PlayerSpawned 11, TrainingDummy 12).

## 16. Pathfinding tiles (decompiled 1.0.16, 2026-09-26)

- **One instance**, a scene object in `main.unity`; `Pathfinding.m_instance` is not cleared in `OnDestroy`,
  so test `Pathfinding.instance` with Unity's `!= null`.
- **Serialized values** (main.unity, read from the scene's type tree by the 2026-09-26 review; code
  initializer in brackets): `m_tileSize` 32 (32), `m_defaultCost` 1 (1), **`m_waterCost` 100 (4)**,
  `m_linkCost` 10 (10), `m_linkWidth` 1 (1), **`m_updateInterval` 10 (5)**, **`m_tileTimeout` 60 (30)**,
  `m_layers` = Default, piece, terrain, static_solid, Default_small, blocker, pathblocker; `m_waterLayers` =
  Water.
- **Humanoid agent** (`Pathfinding.SetupAgents`): height 1.8, climb 0.3, radius 0.4, slope 85, can swim,
  swim depth 0, area mask -1. `BaseAI.m_pathAgentType` defaults to Humanoid; per-prefab overrides are
  **Unverified**.
- **Tiles.** `GetTile(p, agent) = (floor((x+16)/32), floor((z+16)/32), (int)agent)` - a separate grid per
  agent type. A tile's centre is (i·32, 2500, j·32) and `BuildTile` covers 32 × 6000 × 32 (y -500..5500), so
  a surface tile's column includes the dungeon interiors above it. The private `GetNavTile(Vector3, AgentType)`
  **creates** a missing tile (`m_pokeTime = m_buildTime = -1000`); there is no build queue. `NavMeshTile`
  (private nested class) has public fields `m_tile`, `m_center`, `m_pokeTime`, `m_buildTime`, `m_data`,
  `m_instance`, `m_links1`, `m_links2`.
- **Build loop.** `Pathfinding.Update` ticks every >0.1 s while no async build runs. `Buildtiles` first
  finalizes a finished build (`AddNavMeshData`, `RebuildLinks`) and returns; otherwise it builds the **one**
  tile with the largest `m_pokeTime - m_buildTime` above `m_updateInterval` and sets `m_buildTime =
  Time.time`. `TimeoutTiles` removes at most one tile per tick whose last poke is older than
  `m_tileTimeout`. **There is no dirty flag: time since the last build, carried forward by pokes, is the only
  rebuild trigger** - at most about 5 builds per second, and an unbuilt tile (priority `pokeTime + 1000`)
  outranks every rebuild. `BuildTile` collects colliders on `m_layers` (plus Water as area 3 for swimmers)
  and does not check that the zone is loaded.
- **Links.** A tile creates links only on its own +x and +z edges when its build is finalized: every 1 m,
  at each surface a `FindGround` raycast finds at least 1.8 m from the previous one; 0.2 m long, cost ×10,
  area 2, bidirectional. Tiles that touch only at a corner are not linked.
- **Shifting `m_buildTime` together with `m_pokeTime` freezes a tile.** If a mod "keeps a tile alive" by
  adding the elapsed time to both, `pokeTime - buildTime` never grows: the tile never rebuilds (buildings,
  terrain edits and removed trees after its build never appear) and never times out while the mod keeps
  poking, and fresh tiles it pokes every second hold the single build slot. MobTracker's GroundPath mode
  does this. To keep a tile, poke it through `GetNavTile` and leave `m_buildTime` alone.

## 17. Audio volume (decompiled 1.0.16, 2026-09-26)

The Master and SFX sliders act only through the AudioMixer. `AudioMan.Start` sets `AudioListener.volume = 1`
(0 on a Null graphics device) and calls `SetSFXVolume(MasterVolume × SfxVolume)`; `AudioMan.SetSFXVolume`
writes the mixer parameters `SfxVol` and `GuiVol` (`Log10(Clamp(vol, 0.001, 1)) × 10` dB, -80 at 0); the
settings page calls `SetSFXVolume(master × sfx)` and sets `MusicMan.m_masterMusicVolume = master × music`
(`Valheim.SettingsGui.AudioSettings.SetGlobalVolumeExceptHaptics`). `AudioListener.volume` is otherwise
written only by `CinematicsManager` (0 during a cinematic unless NoMute). **So a mod's `AudioSource` with no
`outputAudioMixerGroup` ignores both sliders**: its loudness is its own volume × the listener volume × the
OS volume (that a null group bypasses the mixer is standard Unity behaviour, not in the bytes).
**Unverified:** which of the game's mixer groups a mod should route to, and how to reach it.

## 18. Rendering and the main camera (1.0.16, 2026-09-26)

- **Built-in render pipeline:** `valheim_Data\Managed` has no `Unity.RenderPipelines.*` assembly (checked
  2026-09-26); post-processing is PostProcessing v1 (`assembly_postprocessing.dll`).
- **Built-in shaders that ship:** `Hidden/Internal-Colored` (`unity default resources`), `Sprites/Default` and
  `UI/Default` (`unity_builtin_extra`), per the globalgamemanagers shader map decoded by the review
  (MobTracker's `Shader.Find` chain over these three has never logged its "no usable shader" error).
  `Hidden/Internal-Colored` in this build: one pass, no LightMode tag and no ShadowCaster pass,
  `Queue=Transparent`; unlit vertex colour × `_Color`, no texture, lighting or fog; per-material
  `_SrcBlend`, `_DstBlend`, `_ZWrite`, `_ZTest` (default 4, LessEqual), `_Cull`, `_ZBias`.
- **Main camera** (main.unity "Main Camera", serialized): culling mask all layers, DeferredShading, HDR, no
  MSAA, near 0.5, far 20,000, FOV 65. **`GameCamera`** serializes `m_minDistance` 1, `m_maxDistance` 8,
  `m_maxDistanceBoat` 16, and nothing writes `m_minDistance`, so zooming never reaches first person.
- **Colour space: Linear** (PlayerSettings). **Unverified (engine):** how vertex colours display under
  linear, HDR and grading, and whether a forward-drawn object is missing from `_CameraDepthTexture`, so that
  depth-based effects (PostProcessing v1, `AmplifyOcclusionEffect`) shade it with the background's depth.

## 19. World spawning: which rules can fire (decompiled 1.0.16, 2026-09-26)

- **`SpawnSystem.UpdateSpawnList(spawners, currentTime, eventSpawners, groupSalt)`**, per rule in list order:
  skip it if `!m_enabled` or `!m_heightmap.HaveBiome(m_biome)`, or if any of the zone's
  `m_heightmap.m_cornerAltBiomes[i].m_blockSpawnNames` contains the rule's **`m_name`** (not the prefab name).
  A per-rule timer lives in the zone's ZDO under `(groupSalt + m_prefab.name + index).GetStableHashCode()`.
  For each due spawn: `Random.Range(0, 100) > m_spawnChance` skips it; the loop **breaks** when
  `m_requiredGlobalKey` is set and `!ZoneSystem.instance.GetGlobalKey(key)`, when `m_requiredEnvironments` is
  non-empty and `!EnvMan.instance.IsEnvironment(...)`, on a day/night mismatch, or at `m_maxSpawned`; then
  `FindBaseSpawnPoint`; then, if `m_requiredPersistentEvent` is set,
  `PersistentEventSystem.instance.GetActiveEvent(spawnCenter)` must be non-null with `internalName` equal to it
  (`InvariantCultureIgnoreCase`), else break. `UpdateSpawning` also runs every corner alt biome's own `m_spawn`
  list (2026-09-26 MobTracker review).
- **`ZoneSystem.GetGlobalKey(string name)`** = `m_globalKeysValues.TryGetValue(name.ToLower(), out _)`; the
  `GlobalKeys` overload reads `m_globalKeysEnums`. A client has the world's keys: `OnNewPeer` (server only) →
  `SendGlobalKeys(peer)`, and the client's `RPC_GlobalKeys` → `ClearGlobalKeys()` + `GlobalKeyAdd` each, which
  fills `m_globalKeysValues` (see valheim-worldgen seeds-and-world-files.md for the key lifecycle). So a
  client-side mod can ask the same question the zone owner's `UpdateSpawnList` asks.
- **The shipped rules** (1.0.16; read from the `_SpawnList_*` type trees of bundle `c4210710` by the
  2026-09-26 MobTracker review):
  103 rules in `_SpawnList_base`, `_mistlands`, `_ashlands`, `_DeepNorth`. **15 are key-gated**, most of them
  "other biomes once the boss is dead" rules with a broad `m_biome`: `defeated_eikthyr` (Greydwarf ×2),
  `defeated_gdking` (Draugr, Greydwarf_Elite, Greydwarf_Shaman, odin), `defeated_bonemass` (Skeleton),
  `defeated_goblinking` (Goblin), `defeated_queen` (Seeker, SeekerBrood, Tick), `defeated_fader`
  (Charred_Archer, Charred_Melee), `jotun_killed` (JotunWarrior, JotunWitch). **4 are event-gated**, all
  `jotun_invasion` with `m_biome` -1: Elaking, JotunWarrior, JotunWitch, projectile_FimbulvinterMeteor. The only
  rule with a distance-from-centre band is Writhan, 2000-8000 m.
- **Sub-biome spawns** (bundle `d59cfac`, `AltBiomes_Erik`, 27 rules):
  Bat_Swamp, TentaRoot_wild, Skeleton_Poison and Skeleton_Mountains spawn in the wild **only** from sub-biome
  lists. Blocks (`m_blockSpawnNames`, rule names): Goblin Plains blocks Lox and Deathsquito; Death Plains Lox and
  Goblin; Lox Plains Goblin; Wolf Mountain Skeleton and Hatchling; Drake Mountain Wolf and Skeleton; Root Black
  Forest Skeleton; Peaceful Meadows Greyling, Greydwarf, Skeleton, Skeleton_Poison, Draugr, Draugr_Elite,
  Greydwarf_Shaman, Greydwarf_Elite, Neck, Troll and Bjorn. **Unverified:** whether alt biomes outside
  `AltBiomes_Erik` add more (the review's notes counted 9 alt biomes with blocks in a SeedLab `altbiomes.json`,
  this bundle has 7).
- **`SpawnSystem.IsSpawnPointGood`**: altitude = ground y − 30; distance from centre is `LengthXZ`, min strict
  `<`, max strict `>`, 0 = no limit. Biome and biome area come from `ZoneSystem.GetGroundData` →
  `Heightmap.GetBiome` (a blend of the zone's corner biomes) and `Heightmap.GetBiomeArea` (Edge iff the four
  corner sectors differ; `HeightmapBuilder` takes the corners from `GetBiomeSector` at the centre ± 32 m). Code
  that asks `WorldGenerator.GetBiome` at a point can disagree near a border (2026-09-26 review census: mostly
  zones missed, a few pinned that the game rejects).
- **Bred and grown animals** (Boar_piggy, Wolf_cub, Lox_Calf, Hen/Chicken, Asksvin_hatchling) have no world-spawn
  rule at all (none of them is a prefab in the base lists).
