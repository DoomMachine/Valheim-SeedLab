# How vanilla actually behaves (decompiled, Valheim 1.0.15)

Mechanisms behind the API, each read from decompiled code (cited as `Type.Member`). This is the file
that answers "what will the game do if I...". Re-read the member with `scripts/decompile.ps1` before
relying on a detail after a game update.

Contents: 1. Map pins · 2. Map input gestures · 3. Map data, profiles and the Cartography Table ·
4. Input gating and the cursor · 5. HUD and messages · 6. Console · 7. Player lifecycle ·
8. World height and zones · 9. Keys vanilla uses · 10. BepInEx loader · 11. Container contents ·
12. Dungeon doors and Vegvisirs · 13. The game's runtime: parsing, formatting and pasting

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
  `RemovePin(Vector3, float)` = `GetClosestPin(pos, radius)` + `RemovePin(pin)`.
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
- **`MessageHud.ShowMessage`**: `TopLeft` messages are enqueued; `Center` writes
  `m_messageCenterText` directly and restarts its fade — two Center messages in one frame, only the
  last shows. With the HUD hidden it returns early unless `showDespiteHiddenHUD`.
- `Minimap.UpdateMap` feeds `Utils.GetMainCamera().transform.rotation` into `Minimap.UpdatePlayerMarker`,
  which rotates the map's player marker by `Quaternion.Euler(0, 0, -yaw)` — the game's own facing
  indicator is camera-driven, and uGUI rotation is the negative of a compass bearing.

## 6. Console

- `Terminal.ConsoleCommand`'s constructors begin by writing themselves into the static
  `Terminal.commands` dictionary (lower-cased name) — construction is registration.
- `Chat.isAllowedCommand` rejects only `IsCheat` commands; other commands can be typed in chat too.
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
