# Game API quick reference (Valheim 1.0.15)

What exists, its exact signature, and whether you can call it directly. **PRIVATE** means reach it with
HarmonyLib reflection (or patch it by name) — calling it is a compile error. Verified with Mono.Cecil
dumps and decompilation; re-check anything critical with `scripts/api-surface.ps1 -Type <T>`.
Behaviour (what these do inside) is in vanilla-behaviour.md.

Contents: Minimap · PinData / PinType / MapMode · Player / PlayerController · ZInput / ZCursor ·
Text input gates · Hud / MessageHud / GameCamera · Terminal (console) · World, height and zones · ZNet ·
Version / Platforms · Utils / Localization · BepInEx and Harmony · Unity APIs worth knowing

---

## Minimap (assembly_valheim)

| Member | Access | Notes |
| --- | --- | --- |
| `static Minimap instance { get; }` | public | raw static read, **can return a destroyed object** — test with `!= null` |
| `static bool IsOpen()` | public | LARGE map only, with a 2-frame grace after closing; null-safe |
| `static bool InTextInput()` | public | naming a pin on the large map |
| `PinData AddPin(Vector3 pos, PinType type, string name, bool save, bool isChecked, long ownerID = 0, PlatformUserID author = default)` | public | throws before `Start` (see behaviour) |
| `void RemovePin(PinData pin)` | public | |
| `bool RemovePin(Vector3 pos, float radius)` | public | uses GetClosestPin, so ignores `save:false` pins |
| `void OnMapLeftClick()` | public | toggles a pin's checkmark; only caller is `OnMapLeftUp` |
| `void OnMapDblClick()` | public | the vanilla "place a pin" gesture |
| `void ShowPointOnMap(Vector3)` | public | forces the large map open, 0.5 s input delay |
| `Vector3 ScreenToWorldPoint(Vector3 mousePos)` | PRIVATE | map cursor → world; no fog/explored check |
| `PinData GetClosestPinToCursor()` | PRIVATE | = GetClosestPin(ScreenToWorldPoint(pointer), PinInteractRadius, true) |
| `PinData GetClosestPin(Vector3 pos, float radius, bool mustBeVisible = true)` | PRIVATE | skips `m_save == false` |
| `bool HavePinInRange(Vector3, float)` | PRIVATE | skips `m_save == false` |
| `void RemovePinUnderPointer()` | PRIVATE | vanilla right-click delete |
| `void HidePinTextInput(bool)` | PRIVATE | |
| `void ClearPins()` | PRIVATE | only caller: `SetMapData` |
| `byte[] GetMapData()` / `void SetMapData(byte[])` | PRIVATE | profile map data |
| `byte[] GetSharedMapData(byte[])` / `bool AddSharedMapData(byte[])` | public | Cartography Table payload |
| `bool IsExplored(Vector3)` | see dump | exploration test |
| `void WorldToMapPoint(Vector3, out float, out float)` / `Vector3 MapPointToWorld(float, float)` | PRIVATE | |
| `float PinInteractRadius { get; }` | PRIVATE | `m_removeRadius * (LargeZoom * 2)`, ×1.3 on touch |
| `List<PinData> m_pins` | PRIVATE | assigned once in the constructor — safe to cache per Minimap |
| `bool[] m_visibleIconTypes` | PRIVATE | allocated in `Start` |
| `MapMode m_mode`, `GameObject m_largeRoot`, `m_smallRoot`, `RawImage m_mapImageLarge`, `RectTransform m_pinRootLarge`, `int m_textureSize`, `float m_pixelSize`, `float m_removeRadius`, `float m_clickDuration` | public fields | **shipped prefab values, not the code defaults**: `m_textureSize` 2048, `m_pixelSize` 12, `m_removeRadius` 300, `m_exploreRadius` 50, `m_exploreInterval` 0.25 |

## PinData / PinType / MapMode

`Minimap.PinData` is a **class** (reference type; safe to hold and compare by reference; it has no id).
All 16 fields public: `m_name, m_type, m_icon, m_pos, m_save, m_ownerID, m_author, m_shouldDelete,
m_checked, m_doubleSize, m_animate, m_worldSize, m_uiElement, m_checkedElement, m_iconElement, m_NamePinData`.

`Minimap.PinType` (real values, read from the assembly — note Icon4 is 6, not 4): Icon0=0 Icon1=1
Icon2=2 Icon3=3 Death=4 Bed=5 Icon4=6 Shout=7 None=8 Boss=9 Player=10 RandomEvent=11 Ping=12
EventArea=13 Hildir1=14 Hildir2=15 Hildir3=16 Memorial=17. Pins travel over the network as these ints.
`GetSprite(None)` returns null.

`Minimap.MapMode`: None=0, Small=1, Large=2.

`Splatform.PlatformUserID` is a struct: `default(PlatformUserID)` or `PlatformUserID.None` for `author`.

## Player / PlayerController

| Member | Access | Notes |
| --- | --- | --- |
| `static Player m_localPlayer` | public **field** | null between death and respawn |
| `static bool m_localPlayerExists` | public field | |
| `bool IsDead() / IsSleeping() / IsTeleporting() / InIntro() / InCutscene() / IsCrouching() / InBed()` | public | `InCutscene()` = cutscene animator tag, `InIntro()`, `m_sleeping` or `CinematicsManager.IsPlaying()` — the test `Hud.Update` hides the HUD on (vanilla-behaviour.md section 5) |
| `bool TakeInput()` | **protected virtual** | not callable; see behaviour for its gate list |
| `long GetPlayerID()`, `string GetPlayerName()` | public | |
| `Character.GetLookYaw()` / `GetLookDir()` | public | |
| `PlayerController.TakeInput(bool look)` | PRIVATE | single overload; patch by name |
| `static PlayerController.SetTakeInputDelay(float)` | public | |

`Player` derives Humanoid → Character → MonoBehaviour. `PlayerController` is a **separate**
MonoBehaviour, not a base class of Player.

## ZInput / ZCursor (assembly_utils — all public static)

`ZInput.GetKey(KeyCode, bool logWarning)`, `GetKeyDown(KeyCode, bool)`, `GetKeyUp(KeyCode, bool)` — pass
`false`; null-safe. `GetButton(string)`, `GetButtonDown(string)` (named buttons, e.g. "Console").
`GetMouseButton/Down/Up(int)`. `Vector3 pointerPosition { get; }` (use instead of Input.mousePosition;
Vector3.zero if no instance). `IsMouseActive()` (**not** null-safe), `IsGamepadActive()`,
`IsTouchActive()`. `IsKeyCodeValid(KeyCode)` = key != 0 && key <= 349 && key not 328/329.

`ZCursor`: `LockState { get; set; }`, `IsVisible { get; }`, `IsRequested { get; }`, `Show()`, `Hide()`.
Write the cursor through ZCursor, never `UnityEngine.Cursor`.

## Text input gates (use to keep hotkeys quiet while the player types)

`static TextInput.IsVisible()` · `Chat.instance.HasFocus()` (instance) · `static Console.IsVisible()`
(Valheim's `Console`, which shadows `System.Console` in the global namespace) · `static Minimap.InTextInput()`
· `static InventoryGui.IsVisible()` · `static StoreGui.IsVisible()` · `TextViewer.instance.IsVisible()` ·
`static Menu.IsVisible()`.

## Hud / MessageHud / GameCamera

`static Hud.instance`, `static Hud.IsUserHidden()` (null-safe), `Hud.IsVisible()` (instance, **not**
null-safe: dereferences `m_rootObject`), `static Hud.InRadial()`, `static Hud.IsPieceSelectionVisible()`,
public fields `m_rootObject`, `m_crosshair`.

`MessageHud.instance.ShowMessage(MessageHud.MessageType type, string text, int amount = 0,
Sprite icon = null, bool showDespiteHiddenHUD = false, bool log = true)` — 6 parameters, the last four
**optional** (correction 2026-09-23: this line used to say "exactly 6 args", which is the metadata/Harmony
argument count, not what a caller must pass; decompiled `MessageHud.ShowMessage`).
`MessageType.TopLeft` (queued), `Center` (not). Because `showDespiteHiddenHUD` defaults to **false**, a
mod's messages are dropped while the HUD is hidden (Ctrl+F3) unless it passes `true` — vanilla-behaviour.md §5.

`static GameCamera.instance` — the component sits on the camera GameObject, so
`GameCamera.instance.transform` **is** the camera transform. `static InFreeFly()`. `LateUpdate()` PRIVATE.
`UpdateMouseCapture()` public. `static ScreenShot()` public.

## Terminal (console)

`new Terminal.ConsoleCommand(string command, string description, Terminal.ConsoleEvent action,
bool isCheat, bool isNetwork, bool onlyServer, bool isSecret, bool allowInDevBuild,
bool hideBehindDevCommands, Terminal.ConsoleOptionsFetcher optionsFetcher, bool alwaysRefreshTabOptions,
bool remoteCommand, bool onlyAdmin)` — 13 args; there is also a `ConsoleEventFailable` overload. The
constructor registers the command itself.

`Terminal.ConsoleEvent` = `void (Terminal.ConsoleEventArgs args)`. `ConsoleEventArgs`: `string[] Args`
(Args[0] is the command name), `ArgsAll`, `FullLine`, `Terminal Context`, `Commmand` (sic),
`TryParameterInt/Long/Float`. Print with `args.Context.AddString(string)`.
`Terminal.InitTerminal()` is PRIVATE static — the usual postfix target.

## World, height and zones

| Member | Access | Notes |
| --- | --- | --- |
| `static WorldGenerator.instance` | public | non-null in the main menu too |
| `WorldGenerator.GetHeight(float wx, float wz)` (+ Vector3/Vector2 overloads) | public | procedural absolute height **estimate**, no loaded zone needed; null instance on a joining client until the seed arrives |
| `WorldGenerator.worldSize = 10000`, `waterEdge = 10500` | public const | |
| `static ZoneSystem.instance` | public | |
| `ZoneSystem.GetGroundHeight(Vector3)` / `(Vector3, out float)` | public | raycast; returns your y on a miss |
| `ZoneSystem.GetSolidHeight(Vector3)` and overloads | public | raycast from p.y + margin, 2000 m |
| `ZoneSystem.IsZoneLoaded(Vector3)` | public | world position |
| `ZoneSystem.c_ZoneSize = 64`, `c_WaterLevel = 30` | const | |
| `static NumberFormatInfo ZoneSystem.m_split3` | public | game formats numbers with a SPACE digit separator |

See the valheim-worldgen skill for biomes, seeds and location placement.

## ZNet

`static ZNet.instance`; `long GetWorldUID()` (**throws** with no world); `string GetWorldName()`
(null-safe); `World GetWorld()`; `bool IsServer()`; `static bool IsDedicated`-style checks — see
multiplayer.md.

## Version / Platforms

`static Platforms Version.GetPlatform()` — **public static**, in assembly_valheim. In this build its whole
body is `if (Settings.IsSteamRunningOnSteamDeck()) return Platforms.SteamDeckProton; return
Platforms.SteamWindows;`, so **this (Windows) build never returns `SteamLinux`**. The `Platforms` enum:
`SteamWindows=1, SteamLinux=2, SteamDeckProton=4, SteamDeckNative=8, MicrosoftStore=16, Xbox=32,
PlayStation=64, Switch2=128`. **Caveat:** this is the Windows build describing itself, so it is *not*
evidence about what a Steam Deck or a Linux client actually reports — only the Windows depot (892972) is
installed on this machine. Related: `Version.GetPlatformPrefix(string)`, `Version.GetHardwarePrefix()`,
`static GameVersion Version.CurrentVersion`, `Version.GetVersionString(bool includeMercurialHash)`
(verified 2026-09-23 with `scripts/api-surface.ps1 -Type Version` and decompilation).

## Utils / Localization

`Utils.GetMainCamera()`, `Utils.DistanceXZ(Vector3, Vector3)` (true horizontal distance),
`Utils.YawFromDirection(Vector3)` (compass degrees, 0 = +Z, clockwise, [0,360)),
`Utils.FixDegAngle(float)`, `Utils.GetSaveDataPath(FileHelpers.FileSource)`,
`Utils.WorldToScreenPointScaled(Camera, Vector3)` — public static (assembly_utils).

**`Utils.GetPrefabName` is the game's identity for a GameObject** (assembly_utils, verified from IL
2026-09-23). Two public static overloads, both taking one argument:

```csharp
private static readonly char[] extraCharacters = new char[2] { '(', ' ' };
public static string GetPrefabName(GameObject go) => GetPrefabName(go.name);
public static string GetPrefabName(string name)
{
    int num = name.IndexOfAny(extraCharacters);
    return num != -1 ? name.Remove(num) : name;   // truncate at the FIRST '(' or ' '
}
```

Unity appends ` (1)` to duplicated siblings and `(Clone)` to instantiated ones, so a raw
`GameObject.name` is not an identity: a child the game calls `piece_maypole` can be named
`piece_maypole (1)` in an authored prefab. **Any exact-match query against raw names is a false
negative for every duplicated object.** Normalise first. It throws on a null name.

**`Utils.GetEnabledComponentsInChildren<T>(GameObject root)`** (public static, one argument) is what
`ZoneSystem.SpawnLocation` and `DungeonGenerator.PlaceRoom` build their ordered component arrays with,
so its filter is load-bearing for anything replaying those RNG streams. It drops **two** kinds of
component (verified from IL 2026-09-23):

```csharp
T[] found = root.GetComponentsInChildren<T>();            // NOT includeInactive
foreach (...) if (!(found[i].transform == root.transform) // 1. a component on the ROOT transform
                && IsEnabledInheirarcy(found[i].gameObject, root))   // 2. a broken activeSelf chain
```

`Utils.IsEnabledInheirarcy(GameObject go, GameObject root)` walks `activeSelf` up to `root`. Use it
rather than Unity's `activeInHierarchy` on a prefab ASSET, where `activeInHierarchy` is false for
everything because the asset is not in a scene.

**`ZNetScene.instance.GetPrefab(string)`** and `GetPrefab(int hash)` are pure reads —
`m_namedPrefabs.TryGetValue(name.GetStableHashCode())` and nothing else. A cheap way to ask "does a
prefab by this name exist in this build at all", which is a different question from "does world
generation place it".

`Localization.instance.Localize(string)` (assembly_guiutils) — resolves `$tokens`. It returns a null or
empty input as is, and otherwise the `m_cache` entry for that text when there is one, so repeated calls
normally return the **same string instance**. But a result that is `""` or contains `MISSING KEY` or
`MISSING BUTTON` is returned **before** `m_cache.Put`, so a label with an untranslated `$token` is rebuilt
as a new string on every call — a cache keyed on `ReferenceEquals` of the result misses every time
(decompiled 2026-09-24).

## BepInEx and Harmony

`BepInEx.Paths.ConfigPath` (string). `Config.Bind<T>(section, key, default, description)` —
`T` may be primitives, enums (`KeyCode`, `Minimap.PinType`) and Unity types (`Color`, `Vector2/3/4`,
`Quaternion`, `Rect`). `AcceptableValueRange<T>` for sliders.

**Changing a setting's description never costs the user their value, and the new text reaches their
`.cfg` on the next start** (BepInEx 5.4.23.3, decompiled `BepInEx.Configuration.ConfigFile.Bind` and
`Save`, 2026-09-24): values read from the file sit in `OrphanedEntries` until bound; `Bind` creates the
entry with the plugin's current description, applies the stored value with
`configEntry.SetSerializedValue(orphan)`, removes the orphan, and then, because `SaveOnConfigSet`
defaults to `true`, calls `Save()` — which rewrites the whole file from `Entries`, each entry via
`WriteDescription`. So a description-only change is safe to ship without a migration, and the old text
in an existing `.cfg` is replaced the first time the new plugin binds.

Harmony 2.9.0.0: `[HarmonyPatch(typeof(T), "Method")]` resolves private members by name; add
`new Type[] { ... }` to pick an overload (e.g. `RemovePin` has two). `AccessTools.Method`,
`AccessTools.Field`, `AccessTools.Property(...).GetGetMethod(true)`,
`AccessTools.MethodDelegate<Func<TInstance, ..., TResult>>(methodInfo)` for a fast open-instance call.
Every prefix on a method runs, whatever the others return; postfixes run by `[HarmonyPriority]` descending,
then registration order (`Priority.Last` = 0 runs last) — pitfalls.md section 4.

## Unity APIs worth knowing (present and public in this build)

`Texture2D.SetPixels32 / Apply(bool, bool)`, `Sprite.Create(Texture2D, Rect, Vector2, float, uint,
SpriteMeshType)` (the `Create(Rect, ...)` overloads are **internal**), `GUIUtility.RotateAroundPivot`,
`GUI.DrawTexture`, `GUILayout.Window`, `Font.CreateDynamicFontFromOSFont`, `ColorUtility.TryParseHtmlString`,
`TMP_Text.textWrappingMode` (`enableWordWrapping` is obsolete), `Vector3.SignedAngle`.

`Mathf.PerlinNoise(float, float)` **and `Mathf.PerlinNoise1D(float)`** — both are
`[FreeFunction("PerlinNoise::NoiseNormalized", IsThreadSafe = true)] extern`, i.e. the same native entry
point, and they return bit-identical results for `(x, 0f)`; `Mathf.FloatToHalf(float)` /
`Mathf.HalfToFloat(ushort)` are public externs too (they are what writes and reads
`cacheMinimapHeight`). `Mathf.Round(float)` is `(float)Math.Round(f)`, so it is round-half-to-**even**,
and `Color -> Color32` is `(byte)Mathf.Round(Clamp01(c)*255f)` per channel.

**What those three natives actually do** (recorded from the running game 2026-09-22/23 and reproduced
bit-for-bit; full statement and evidence in valheim-worldgen, `world-generator.md` section 9):

- `Mathf.PerlinNoise(x, y)` = improved Perlin with an **`abs()` fold on both inputs**, the quintic fade
  `6t^5-15t^4+10t^3`, Ken Perlin's classic 256-entry permutation table doubled, the classic 16-gradient
  set keyed on the low 4 bits, normalised `(raw + 0.69f) / 1.483f` **with a real divide** — the
  reciprocal multiply `* (1f/1.483f)` is 1 ULP off on 37.5 % of samples. 262,780/262,780 samples exact.
- `UnityEngine.Random` = xorshift128 with shifts 11/8/19; `InitState(s)`: `s0 = s`, then
  `si = si-1 * 1812433253 + 1`. `value` = `(next & 0x7FFFFF) * (1f/8388607f)`;
  `Range(int,int)` = `min + next % (uint)(max-min)`; **`Range(float,float)` = `(1f-f)*max + f*min`**
  (not `min + f*(max-min)`, not `Mathf.Lerp`), so it is max-inclusive and spans the interval even when
  `min > max`. **`Range(a,a)` consumes a draw for the float overload and not for the int overload**, and
  `Range(20f,20f)` returns 20.000002f about 2.5 % of the time. `insideUnitCircle` is exactly two draws,
  no rejection: `x = cos(a)*r`, `y = sin(a)*r`, `a = Range(0f, 2π)`, `r = sqrt(Range(0f,1f))`.
- `Mathf.FloatToHalf` breaks ties **away from zero**, while .NET's `(Half)f` cast rounds ties **to
  even** — measured on 640 real midpoints across two worlds plus a 1,634-value adversarial set, and it
  holds for negatives. `FloatToHalf(NaN)` also returns a different NaN payload (0xFF00) from `(Half)`
  (0xFE00), and Unity's own `HalfToFloat` does not round-trip it. Never compare game half data with a
  tolerance; round the way Unity rounds and compare bits.

`UnityEngine.Random`: `InitState(int)`, `state` (get/set), `value`, `Range(int,int)`, `Range(float,float)`,
`insideUnitCircle`. **`Random.State`'s four words `s0`–`s3` are private `[SerializeField] int` fields** —
nothing public exposes them, so recording an RNG state needs reflection over a boxed copy
(`typeof(Random.State).GetField("s0", NonPublic | Instance)`). Reading `Random.state` does **not** advance
the generator, and its native setter **does** round-trip exactly - proven in the running game 2026-09-23, not assumed: save the state, draw four `Random.value`, restore, `InitState(999)` to perturb it hard, restore again, and both the four state words and the four draws come back identical (`valheim-dumper/.../goldens/natives-random.json`, `stateRoundTrip.statesEqual` and `drawsEqual`). Every guard that borrows the global RNG and puts it back depends on this.

Unity 6 object search is `Object.FindObjectsByType<T>(FindObjectsInactive, FindObjectsSortMode)`;
`FindObjectsOfType` is obsolete.

## SoftReferenceableAssets (SoftReferenceableAssets.dll — not assembly_valheim)

`SoftReference<T>`: `Name` (filename without extension, memoised — **this is the string Valheim hashes for
a location's RNG stream**), `IsValid`, `m_assetID`, `Load()` (synchronous), `LoadAsync(callback)`, `Asset`,
`Release()`. `Release()` must balance a `Load()` that actually returned.

`AssetID` is a struct of four uints declared **v3, v2, v1, v0 in that order**; `ToString()` concatenates
them in the same order as 8 hex digits each, which is the form the
`valheim_Data\StreamingAssets\SoftRef\manifest` text file uses. It is the identity Valheim treats as "the
same prefab" (`ZoneSystem.m_locationIDCache`, `HaveLocationInRange`) — the name is not.

**Not everything that points at a prefab is a `SoftReference`.** `RandomObject.ObjectEntry.m_object` is a
plain `UnityEngine.GameObject` field (`RandomObject/ObjectEntry`: `GameObject m_object`, `Single m_weight`
— shipped `assembly_valheim.dll`, 2026-09-23). It is an ordinary Unity serialised reference, so the option
prefab is **already resident** whenever the prefab that holds the `RandomObject` is: you can call
`GetComponentsInChildren<T>(true)` on it directly, with no `Load()`, no `Release()` and no asset-loader
reference count to unbalance. Only `ZoneSystem.ZoneLocation.m_prefab` and `DungeonDB.RoomData.m_prefab`
are `SoftReference<GameObject>`. Check the field's declared type before assuming a load is needed.
