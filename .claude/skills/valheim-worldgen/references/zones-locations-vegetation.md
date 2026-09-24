# Zones, Locations, Vegetation, Dungeons (ZoneSystem & friends)

> Researched 2026-09-22 against Valheim 1.0.15 by decompiling the shipped assemblies; every claim was then checked by an independent refute-by-default verifier, who corrected errors in place. Items marked **Unverified:** could not be settled from code. Re-check with `valheim-modding/scripts/decompile.ps1` after a game update.

Verified by decompiling `assembly_valheim.dll` (Unity 6000.0.75 / BepInEx 5.4.23.3 Steam build) with the ILSpy-based
decomp tool. Citations look like `(Type.Member / decompiled)`. Anything not provable from code is marked **Unverified:**.

## Summary (read this first)
- **Zone = 64 x 64 m square, centred on `id * 64`.** `GetZone(p) = floor((p.x+32)/64), floor((p.z+32)/64)`. ZDO sectors are the same grid. (ZoneSystem.GetZone / GetZonePos / ZDO.GetSector)
- **Only the server generates.** "Generated" means the zone's ID is in the private `m_generatedZones` set, saved in the world's `.db2` file. Generation happens once, when the server's or a peer's reference position first comes near the zone. It places the location (if any), vegetation and `_ZoneCtrl` as ZDOs. After that the zone is never rolled again. (ZoneSystem.SpawnZone / PokeLocalZone / CreateGhostZones)
- **Location placement** (`GenerateLocations`) runs **on the server at world load** when the saved `locationsGenerated` flag is false: a new world, a `m_locationVersion` change, or the `genloc` command. It is not part of the world-creation UI. The random source is `UnityEngine.Random.InitState(worldSeed + prefabName.GetStableHashCode())`, re-seeded once per location type. The server doesn't open, or spawn any zone, until this finishes. (ZNet.ServerLoadWorld / ZoneSystem.GenerateLocationsTimeSliced)
- **At most one location per zone.** Instances live in the public field `Dictionary<Vector2s, LocationInstance> m_locationInstances`, keyed by zone. Each instance is saved as (prefab-name hash, x, y, z, placed). **Server only:** clients never load this dictionary. They only receive a list of map icons. (ZoneSystem.RegisterLocation / Save / Load / RPC_LocationIcons)
- **Location placement is reproduced offline, bit-exactly** -- 12 228/12 228 instances of a freshly generated world, and both played worlds' whole `.db2` lists. See 3.7 for the measurements, the vanilla table facts that decide which code paths are live, and the four details no oracle discriminates. **Independently corroborated** on 2026-09-23 against bobmitch.com/valheim, an unrelated JavaScript implementation set to 1.0.7: on seeds `bmbp74` and `138fmg` the two agree on all 183 prefab types, all 24 564 instances and their heights, to better than 0.01 m in aggregate. That also means **1.0.7 and 1.0.15 place locations identically** as far as two seeds can show.
- **The candidate list is seed-deterministic but not fully seed-determined.** It also depends on the location list and its order (game version, mods), the world-gen version, and the zones that were already generated when it ran. **Location rotation is NOT seeded from the world seed.** For `m_unique` locations, the candidate that survives is **whichever candidate zone gets generated first**, so it depends on exploration order.
- **Vegetation** is seeded per zone and per prefab: `worldSeed + zx*4271 + zy*9187 + prefab.name.GetStableHashCode()`. The acceptance tests use physics raycasts, and all results are then stored as ZDOs. (ZoneSystem.PlaceVegetation)
- **Dungeon seed** = `worldSeed + zx*4271 - zy*7187 - (int)x*4271 + (int)y*9187 - (int)z*2134`, using the generator's world position. The layout is then **saved to the generator's ZDO** (`s_roomData`) and is never regenerated. (DungeonGenerator.GetSeed / Save / Load)
- **Height helpers:** `GetGroundHeight` casts from y=6000 over 10000 m against the *terrain* layer only. `GetSolidHeight(p, out h, margin=1000)` casts from **p.y + margin over 2000 m** (confirmed), against Default/static_solid/Default_small/piece/terrain. Both work only where colliders are loaded.

---

## 1. Zones

### 1.1 Constants (ZoneSystem fields / decompiled)
| Name | Value | Notes |
|---|---|---|
| `c_ZoneSize` / `m_zoneSize` | 64f | Tooltip: "should match netscene sector size" |
| `c_ZoneSizeHalf` | 32f | |
| `c_WaterLevel` / `m_waterLevel` | 30f | All "altitude" filters use `y - 30` |
| `m_zoneTTL` | code 4f, **shipped prefab 10f** | Seconds before an inactive zone root is destroyed. The 4f is the field initialiser (ZoneSystem.cs:461); the value the game actually runs is the prefab override, read out of the loaded `ZoneSystem` 2026-09-22 (`prefab-constants.json` in `data\1.0.15-59f53fb5\`; the **seedlab** skill, and 2.3 below, describe that snapshot). |
| `m_zoneTTS` | code 4f, **shipped prefab 5f** | Declared; not used in the ZoneSystem code read here. Same source. |
| `c_ZonesPerChunk` | 8 | Save chunks = 8x8 zones (ZoneSystem.GetZonesChunk) |
| `c_GenerateLocationsTimeBuffer` | 1/150 | |
| Update tick | 0.1 s | `m_updateTimer > 0.1f` (ZoneSystem.Update) |

### 1.2 Position ↔ zone
```csharp
public static Vector2s GetZone(Vector3 p) {           // ZoneSystem.GetZone / decompiled
    int x = Utils.FloorToInt((float)(((double)p.x + 32.0) / 64.0));
    int y = Utils.FloorToInt((float)(((double)p.z + 32.0) / 64.0));   // note: uses p.z
    return new Vector2s(x, y); }
public static Vector3 GetZonePos(Vector2s id) => new Vector3(id.x * 64f, 0f, id.y * 64f); // zone CENTRE
```
- Zone (0,0) covers x,z in [-32, 32). `GetZonePos` returns the **centre**, not a corner.
- `Utils.FloorToInt(f)` (assembly_utils) is `(int)(f + 64000f) - 64000`. It is a true floor only for f > -64000, which covers all valid world coordinates.
- `Vector2s.y` holds the world **z** axis.
- Sector index (ZDO storage): `SectorToIndex(x,y) = (y+256)*512 + (x+256)`. Zones outside [-256, 255] map to index 0, so the valid grid is about ±16 km (ZoneSystem.SectorToIndex). `ZDO.GetSector` calls `ZoneSystem.GetZone` (xref).
- Useful public statics: `GetZone`, `GetZonePos`, `GetSectorIndex`, `SectorToIndex`, `IndexToSector`, `GetZonesChunk`. **Private:** `GetZoneCenter(Vector2s)`, which returns `Vector2s(id*64)`.

### 1.3 Zone lifecycle (ZoneSystem.Update / CreateLocalZones / CreateGhostZones / PokeLocalZone / SpawnZone)
- Every 0.1 s: `CreateLocalZones(ZNet.GetReferencePosition())` spawns **at most one** zone per tick within `NearSimulationDistance`. If none was spawned and this is the server, it then calls `CreateGhostZones` for its own reference position and for **each peer's** reference position (`peer.GetRefPos()`), within `TotalSimulationDistance`. Each call spawns at most one zone.
- On the **server**, zone creation is paused until locations are generated: `if (IsServer && !LocationsGenerated) return;` (ZoneSystem.Update).
- Spawn modes (`ZoneSystem.SpawnMode`: Full, Client, Ghost):
  - `PokeLocalZone`: `mode = (!IsServer || IsZoneGenerated(z)) ? Client : Full`. **Clients never generate zones.** They only instantiate the zone prefab (the terrain Heightmap); all objects arrive as ZDOs.
  - Ghost (server, around peers): runs the full generation to create ZDOs, then destroys the GameObjects and the zone root at once.
- `SpawnZone` returns false (try again later) if `HeightmapBuilder.IsTerrainReady(...)` is false. It also returns false if the zone has an unplaced location whose prefab (and dungeon room prefabs) haven't finished async loading (`PokeCanSpawnLocation`).
- **What "generated" means:** for Full/Ghost mode on a not-yet-generated zone, `SpawnZone` runs, in order:
  `PlaceLocations` → `PlaceVegetation` → `PlaceZoneCtrl` → `SetZoneGenerated(zoneID)` (adds the ID to `m_generatedZones`).
  `m_generatedZones` (`HashSet<Vector2s>`), `IsZoneGenerated` and `SetZoneGenerated` are all **private**. Use reflection or Harmony to read them.
- `IsZoneLoaded(Vector2s/Vector3)` (public) means the zone root exists and no ZDOs in it are still loading (`m_loadingObjectsInZones`).
- Zone roots are destroyed when `m_ttl > m_zoneTTL` (**10 s** in the shipped prefab, not the 4f in code — see 1.1) and `ZNetScene` has no instance in that sector. At most one is destroyed per tick (ZoneSystem.UpdateTTL).
- Radius test for the non-classic mode: `ZonesWithinRadius` compares zone-centre distance against `r*64 + (ghost ? 0.8*64 : 0.5*64)`. In classic mode the area is a square loop.
- Simulation distance presets (SimulationDistance.GetSimulationDistance): level 0 = (near 1, far 2, classic); 1 = (2,2); 2 = `OriginalDistance` (2,2, classic); 3/4/5 = (3|4|5, 2); higher = (level, 2). Total = near + far. The server's value is synced via `ZNet.GetSyncedSimulationDistance()` (ZoneSystem.ApplySettings).

---

## 2. Location data model

### 2.1 `ZoneSystem.ZoneLocation` (public nested, [Serializable]) – key fields and defaults
`m_name`, `m_enable=true`, `m_prefabName` (set at runtime from the prefab name), `m_prefab` (`SoftReference<GameObject>`), `m_biome` (bitmask), `m_biomeArea = Everything`, `m_quantity`, `m_prioritized`, `m_centerFirst`, `m_unique`, `m_group=""`, `m_minDistanceFromSimilar`, `m_groupMax=""`, `m_maxDistanceFromSimilar`, `m_iconAlways`, `m_iconPlaced`, `m_randomRotation=true`, `m_slopeRotation`, `m_snapToWater`, `m_interiorRadius`, `m_exteriorRadius`, `m_clearArea`, `m_minTerrainDelta=0`, `m_maxTerrainDelta=2`, `m_minimumVegetation=0`, `m_maximumVegetation=1`, `m_surroundCheckVegetation`, `m_surroundCheckDistance=20`, `m_surroundCheckLayers=2`, `m_surroundBetterThanAverage`, `m_inForest`, `m_forestTresholdMin=0`, `m_forestTresholdMax=1`, `m_minDistanceFromCenter`, `m_maxDistanceFromCenter`, `m_minDistance`, `m_maxDistance`, `m_minAltitude=-1000`, `m_maxAltitude=1000`. There is also `Hash => m_prefab.Name.GetStableHashCode()` and a private `AltBiomeParent`. (ZoneSystem.ZoneLocation / decompiled)

### 2.2 `ZoneSystem.LocationInstance` (public struct)
`ZoneLocation m_location; Vector3 m_position; bool m_placed;`. `m_placed` = the zone has actually been generated, so the objects exist as ZDOs. **No rotation is stored here.**

### 2.3 Where the location list comes from (ZoneSystem.SetupLocations, called from Start)
1. `m_locations` starts with the ZoneSystem prefab's own serialized list.
2. It then appends `m_locations` from every `LocationList` (`LocationList.GetAllLocationLists()`), sorted by `m_sortOrder` with `List.Sort`, which is an unstable sort. The same step appends vegetation, environments, clutter and random events.
3. Then it appends each `AltBiome.m_addLocations` / `m_addVegetation` from `AltBiomeList.m_altBiomes`, tagging each with `AltBiomeParent`. **This loop has no `m_enabled` test** — a *disabled* alt-biome still injects its locations into `m_locations` with `AltBiomeParent` set, and those then fail filter 10 on every point because no sector will ever carry a disabled alt-biome (`AltBiomeList.GetValidAltBiomes` is the only place `m_enabled` is read on this path; `AltBiomeList.Awake` does `m_altBiomes.AddRange(m_alts)` unconditionally). Note also that `LocationList.GetAllLocationLists()` returns the static list **itself**, so `SetupLocations` sorts it in place.
4. It builds `m_locationsByHash` (prefab-name hash → ZoneLocation). On a duplicate hash it logs an error and ignores the later entry.
- **Modder note:** location mods (Jotunn etc.) change this list. Because instances are saved by **prefab-name hash**, loading a world without the mod silently drops that mod's instances (`ZLog.DevLog("Failed to find location ...")` in ZoneSystem.Load). They are then lost at the next save.
- **Vanilla 1.0.15 numbers** (verified 2026-09-22): **213** location prefabs ship — 212 under
  `Assets/world/Locations/` plus `Assets/world/Props/BogWitchHut/BogWitch_Camp.prefab`, all listed by
  name and `AssetID` in the plain-text `valheim_Data\StreamingAssets\SoftRef\manifest`. **183** of them
  are `ZoneLocation` entries with `m_enable && m_quantity != 0` (game log: `Loading: Done. … locations: 183`).
  The lists come from **6** `LocationList` prefabs, all in the `main` scene, contributing 3/2/27/4/25/25
  locations and 0/0/25/0/33/35 vegetation entries (`Added N locations … from main`, ×6).
  Trader/service locations confirmed from a real world save: `Vendor_BlackForest` (Haldor),
  `Hildir_camp`, `BogWitch_Camp`, `AncientUpgradeStation`, `StartTemple`. Bosses: `Eikthyrnir`,
  `GDKing`, `Bonemass`, `Dragonqueen`, `GoblinKing`, `Mistlands_DvergrBossEntrance1`, `FaderLocation`,
  `DN_Bossroom`. `"StartTemple"` is the only location name that appears as a string literal in
  `assembly_valheim.dll` (`Game.m_StartLocation`, Game..ctor); it has `m_iconAlways = true`.
- **`Minimap.m_locationIcons` has exactly 5 entries** in 1.0.15 — `StartTemple`, `Vendor_BlackForest`,
  `Hildir_camp`, `BogWitch_Camp`, `AncientUpgradeStation`. A location with `m_iconAlways`/`m_iconPlaced`
  but no sprite entry draws no pin, so **bosses never get a free map icon**; they are revealed only via
  Vegvisir/runestone (`Game.DiscoverClosestLocation`). (Read out of the shipped `main.unity` bundle, count
  field = 5, 2026-09-22.)
- **`SoftReference<T>.Name` is derived, never serialized:** `Shared.GetFileName(Runtime.GetAssetPath(m_assetID), withExtension:false)`
  = the manifest path's filename without its extension (IL of `SoftReferenceableAssets.SoftReference\`1::get_Name`
  and `Shared::GetFileName`). Only `m_assetID` is serialized. So the RNG stream key for a location type
  (`worldSeed + m_prefab.Name.GetStableHashCode()`) is fully recoverable offline from that text manifest.
- **Settled 2026-09-22 (was Unverified): every per-entry flag of every entry is now recorded.** A BepInEx
  plugin read `ZoneSystem.m_locations` out of the running game after `SetupLocations`; the table lives in
  `data\1.0.15-59f53fb5\` (dumped from the running game 2026-09-22 by `tools\SeedLab.Dumper`; `vseed data --verify` re-checks it) as `locations.json`, in list order, with all 40 serialized fields, the `AssetID`, the
  precomputed `m_prefab.Name.GetStableHashCode()`, which list each entry came from, and the entry's
  position in the ordered placement list. `vegetation.json` (257 entries) and `altbiomes.json` do the same
  for `m_vegetation` and `AltBiomeList.m_altBiomes`.
- **Measured 1.0.15 table shape** (counted from that dump, cross-checked against the game's own log):
  **232** `ZoneLocation` entries after `SetupLocations`; **186** with `m_enable`; **183** with
  `m_enable && m_quantity != 0` (the list the run walks, and the number the log reports); **200** with a
  non-zero `m_quantity` whether enabled or not. Beware the arithmetic: 3 entries are enabled with
  quantity 0 and 17 carry a quantity while disabled, so "enabled" and "183" are **not** the same set.
  By origin: **130** come from the `ZoneSystem` prefab's own serialized list (88 of them in the ordered
  183), **86** from the six `LocationList`s (79 ordered), **16** from an `AltBiome.m_addLocations`
  (all 16 ordered). **9** entries are `m_unique`: `Vendor_BlackForest`, `BigRockClearing`,
  `AncientUpgradeStation`, `Hildir_camp`, `PlaceofMystery1/2/3`, `BogWitch_Camp`, `TheDarkestHole`.
  `StartTemple`'s hand-decoded values were right: `m_exteriorRadius = 25`, `m_clearArea = true`,
  `m_maxTerrainDelta = 3`, `m_iconAlways = true`, `m_unique = false`,
  `m_prioritized = m_centerFirst = true`, `m_quantity = 1`.
- **The Valheim wiki's "Point of interest" placement table is stale on 9 quantities** (compared
  2026-09-24 against this dump; page text supplied by the user). 113 of its rows name a dumped prefab;
  the biome agrees on all 113 and the quantity on 104. The 9 that differ all have the wiki higher:
  `SunkenCrypt4` 400 vs **175**, `SwampRuin1` / `SwampRuin2` 50 vs **30**, `FireHole` 200 vs **75**,
  `TrollCave02` 250 vs **200**, `TarPit2` 100 vs **16**, `MountainCave02` 160 vs **120**,
  `Mistlands_Giant1` 350 vs **250**, `Mistlands_Giant2` 100 vs **85** (dump in bold). The dump is the
  authority: these are the quantities with which SeedLab reproduces a fresh world's 12,228 instances
  bit-exactly (3.7). The page's `WoodHouse2` chest at 50 % and `WoodHouse6` chest always present
  agree with the dump's `RandomSpawn` reading (5.3).

---

## 3. Location placement algorithm (world-level)

### 3.1 When it runs (ZNet.Start → ServerLoadWorld; ZoneSystem.Load)
- `ZNet.Start`: `if (m_isServer) ServerLoadWorld(); else ClientConnect();`
- `ServerLoadWorld`: `LoadWorld()`/`LoadOldWorld()` → `AltBiomeWorldData.VerifyBiomeData(world)` (rebuilds the biome grid, see 3.4) → `ZoneSystem.GenerateLocationsIfNeeded()` → subscribes `OnGenerationFinished`, which runs `OpenServer()` only if `m_openServer`. **So a server only opens to players after location generation completes.**
- `GenerateLocationsIfNeeded` runs only if `!LocationsGenerated`. The flag comes from the `.db2`. It is forced false when the saved `m_locationVersion` ≠ the current `m_locationVersion` (ZoneSystem.Load). For a new world there is no `.db2`, so the flag is false.
  The shipped prefab value is **`m_locationVersion = 32`** (code default 1). Verified 2026-09-22 from the
  user's world save `asdasdasd\_main.<N>.db2`, whose stored location version — written by this build — is 32
  (`python scripts/valheim_saves.py locations "asdasdasd"`, read-only).
- Console: `genloc` (server-only) calls `GenerateLocations()` again. `genloc alt` re-runs `m_biomeData.GenerateAltBiomes()` instead **and returns** (`Terminal.<>c.<InitTerminal>b__7_17`, IL read 2026-09-22: `argv.Length >= 2 && argv[1].ToLower() == "alt"` → `ZNet.instance.GetWorld().m_biomeData.GenerateAltBiomes()`; otherwise `ZoneSystem.instance.GenerateLocations()`). `GenerateLocationsIfNeeded` has exactly **one** caller, `ZNet.ServerLoadWorld` (Cecil xref). `GenerateLocations()` guards on `m_generateLocationsCoroutine == null`, does **not** reset `LocationsGenerated`, and calls `SetupLocations()` only when `!Application.isPlaying` — so a console re-run never rebuilds the location table.
- It runs as a coroutine (`GenerateLocationsTimeSliced`) with a time budget of 0.1 s per frame. The budget is smaller while the intro or a cinematic plays. `GenerateLocationsProgress`, `GetEstimatedGenerationCompletionTimeFromNow()` and the event `GenerateLocationsCompleted` are public. The event fires at once if generation has already finished.

### 3.2 Outer loop (ZoneSystem.GenerateLocationsTimeSliced() / decompiled)
1. `ClearNonPlacedLocations()`: **all unplaced instances of every type are discarded.** Only placed ones survive.
2. `ordered = m_locations.OrderByDescending(a => a.m_prioritized).ToList()`. LINQ's sort is stable, so prioritized entries come first and list order is kept within each group. Entries with `!m_enable || m_quantity == 0` are removed.
3. Each remaining location is processed in turn by the per-location routine below.
4. At the end: `LocationsGenerated = true`.

### 3.3 Per-location routine (ZoneSystem.GenerateLocationsTimeSliced(ZoneLocation, ...) / decompiled)
```csharp
int seed = WorldGenerator.instance.GetSeed() + location.m_prefab.Name.GetStableHashCode();
UnityEngine.Random.InitState(seed);                       // own RNG stream per location type
float maxRadius = Mathf.Max(location.m_exteriorRadius, location.m_interiorRadius);
int attempts = location.m_prioritized ? 60000 : 12000;    // outer zone draws
int placed = CountNrOfLocation(location);                 // already-existing (placed) instances
if (!location.m_unique || placed <= 0) { ... }            // unique + already exists -> skip entirely
```
- `WorldGenerator.GetSeed()` returns `m_world.m_seed`, the int seed stored in `World` (WorldGenerator.GetSeed).
- **The time slicing does not break determinism.** At every `yield`, the code swaps the location's `Random.state` out and restores the outside state, then swaps back after resuming. The location's stream is therefore isolated from other code.
- Loop: `while (i < attempts && placed < m_quantity)`. Each outer iteration draws **one candidate zone**:
  - `m_centerFirst`: `GetRandomZone(maxRange)`. `maxRange` starts at `m_minDistance` and grows by **+1 m per attempt**. `GetRandomZone`: `n = (int)range/64`, zone = `(Random.Range(-n, n), Random.Range(-n, n))` (int, upper bound exclusive), retried until `|GetZonePos| < 10000`. So the search starts at the centre and expands outward.
  - else if `m_minAltitude >= 0`: zone of `MapSpaceToWorldSpace(m_biomeData.GetRandomPointByBiomesAboveSeaLevel(m_biome))`.
  - else: zone of `MapSpaceToWorldSpace(m_biomeData.GetRandomPointByBiomes(m_biome))`.
- The zone is rejected if it **already has a location instance** (`m_locationInstances.ContainsKey`) or is **already generated**. It is also rejected if `WorldGenerator.GetBiomeArea(zoneCenter)` doesn't match `m_biomeArea`. BiomeArea is `Edge` if any of the 8 neighbours at ±64 m has a different biome, otherwise `Median` (WorldGenerator.GetBiomeArea(Vector2s)).
- Otherwise it makes **up to 6 point tries** in that zone. `GetRandomPointInZone`: x,z = zone centre + `Random.Range(-32 + maxRadius, 32 - maxRadius)`. The filters, in this exact order:
  1. `m_minDistance` / `m_maxDistance` (when ≠ 0) against `point.magnitude` with y = 0. This is the horizontal distance from world origin.
  2. `(m_biome & WorldGenerator.GetBiome(point)) != 0`
  3. `y = WorldGenerator.GetHeight(x, z, out mask)`; altitude `y - 30` must lie in [`m_minAltitude`, `m_maxAltitude`]
  4. `m_inForest`: `WorldGenerator.GetForestFactor(point)` must lie in [min, max]. Note: `GetForestFactor` is **static and has no seed input**. It is `DUtils.Fbm(pos*0.01*0.4, 3, 1.6, 0.7)`.
  5. `m_minDistanceFromCenter` / `m_maxDistanceFromCenter` (when > 0), using `Utils.LengthXZ`. This is a second, redundant distance pair that has no error counter.
  6. Terrain delta: `WorldGenerator.GetTerrainDelta(point, m_exteriorRadius)`, which takes 10 `Random.insideUnitCircle` samples (**this uses the seeded stream**). The result must lie in [`m_minTerrainDelta`, `m_maxTerrainDelta`].
  7. `m_minDistanceFromSimilar > 0`: rejected if any instance with the **same prefab AssetID**, or the same non-empty `m_group`, lies within that radius (`HaveLocationInRange`, 3-D squared distance, strictly `<`; counts **unplaced** candidates too). Both positions carry `y = GetHeight(...)`, so terrain height participates — it is not a 2-D test.
  8. `m_maxDistanceFromSimilar > 0`: rejected unless a same-prefab instance, or one in the same `m_groupMax`, lies within that radius. The AssetID bucket is consulted in **both** 7 and 8.
  - `HaveLocationInRange` reads three caches filled by `RegisterLocation`: `m_locationIDCache` (key `m_prefab.m_assetID`), `m_locationGroupCache` (key `m_group`) and `m_locationMaxGroupCache` (key `m_groupMax`) — all three **public** fields. Empty group keys are still added (inert, since lookups require `group.Length > 0`). Two `ZoneLocation` entries sharing one prefab would share the AssetID bucket **and** the per-type RNG seed, since both derive from `m_prefab.Name`.
    **Corrected 2026-09-22: `X` / `X_1` is not an example of that.** Every genloc log line is formatted with `location.m_prefab.Name` (`ZoneSystem.GenerateLocationsTimeSliced`: `$"Failed to place all {location.m_prefab.Name}, ..."`), so `TarPit1` and `TarPit1_1` are two *different* prefab names — different `AssetID`s, different buckets, different streams. **Settled 2026-09-22 (was Unverified): no two entries share a prefab.** All 232 entries have distinct `m_prefabName` values and distinct `Hash`es (`distinctLocationHashes: 232`, `duplicateHashPrefabNames: []` in the runtime dump), so every entry is its own RNG stream and its own `m_locationIDCache` bucket. Note `ZoneSystem.CheckLocationDuplicates`, which would warn about it, is **dead code — nothing calls it** (only its definition appears in `ZoneSystem`), so its silence proves nothing; the live check is the duplicate-**hash** `ZLog.LogError` in `SetupLocations`.
  - `LocationInstance` is a **struct**, so the caches hold *copies*, and a copy's `m_placed` is frozen at whatever `RegisterLocation(location, pos, generated)` was given. `PlaceLocations` sets `m_placed = true` only on the `m_locationInstances` entry.
    **Corrected 2026-09-22 — this was previously overstated as "strips every cached entry".** `ZoneSystem.Load` replays each saved instance through `RegisterLocation(location, pos, generated)` with `generated` read from the `.db2` (`bool generated = zPackage.ReadBool();`), so instances that were already placed when the world was saved carry `m_placed == true` in the dictionary **and** in all three caches and survive `RemoveNonPlaced`. The staleness is confined to instances that `PlaceLocations` promoted **during the current session**. On a re-run, `ClearNonPlacedLocations` → `RemoveNonPlaced` drops genuine candidates (correct) plus exactly that session-placed subset, which does survive in `m_locationInstances` (ZoneSystem.AddToCache / RemoveNonPlaced / PlaceLocations / Load).
  9. Vegetation mask `mask.a`: rejected if `m_minimumVegetation > 0 && a <= min`, or if `m_maximumVegetation < 1 && a >= max`.
     **Corrected 2026-09-23 — the earlier claim "`mask.a` is exactly 0 outside Mistlands and AshLands" was wrong.** `GetBiomeHeight` starts with `mask = Color.black`, and **`UnityEngine.Color.black` is `(0, 0, 0, 1)`** — alpha **one**, not zero. Only three branches overwrite it: `GetMistlandsHeight` → `new Color(0,0,0,num5)`, `GetAshlandsHeight` → `new Color(0,0,0,num19)`, and `GetDeepNorthHeight` → `new Color(0, num9, 0, 0)` — green, so Deep North is the one biome where alpha really is 0. Therefore `mask.a` is:
     **1** everywhere except **0** in Deep North and a computed value in Mistlands / AshLands.
     Consequences, which are the opposite of the old reading: `m_minimumVegetation > 0` does **not** confine a location to Mistlands/AshLands (the test is `a <= m_minimumVegetation`, and `a == 1` passes unless the field is ≥ 1) — it excludes **Deep North**; and `m_maximumVegetation < 1` excludes everything *except* Deep North and the low-alpha parts of Mistlands/AshLands. The surround check (11) scores the constant `sum(weights)` outside those three biomes rather than 0, so the `score >= cutoff` test still passes trivially there, but a point whose rings straddle a Deep North edge now gets a genuinely mixed score. Both defaults (`m_minimumVegetation = 0`, `m_maximumVegetation = 1`) switch the filter off, so this only bites for entries that override them. **Settled 2026-09-22 (was Unverified): 22 of the 232 entries do**, and they are almost all Ashlands/Deep North content — `FaderLocation`, `PlaceofMystery1/2/3`, `CharredFortress`, `Runestone_Ashlands` and `VoltureNest` use `m_maximumVegetation = 0.1`, `FortressRuins` and `SulfurArch` 0.8, `CharredStone_Spawner` 0.2, and `LeviathanLava` is the one entry with a non-zero `m_minimumVegetation` (0.6). The full list is `locations.json` in `data\1.0.15-59f53fb5\` (dumped from the running game 2026-09-22 by `tools\SeedLab.Dumper`; `vseed data --verify` re-checks it). (WorldGenerator.GetBiomeHeight line 1022 `mask = Color.black;` / GetMistlandsHeight / GetAshlandsHeight / GetDeepNorthHeight, re-read 2026-09-23; `Color.black` is Unity's own `(0,0,0,1)`.)
  10. Alt-biome: if the location belongs to an AltBiome, the point's `BiomeSector` must carry that alt biome. Rejected if any alt biome on the sector lists the location in `m_blockLocationNames`.
  11. `m_surroundCheckVegetation`: sums `mask.a` on `layers x 6` rings. The first 9 passing samples only build the baseline (`s_tempVeg.Count < 10` → reject). After that it needs `score >= avg + (max-avg)*m_surroundBetterThanAverage`. The outermost ring always has weight 0 (`(dist - r)/(dist*2)` with `r == m_surroundCheckDistance`), so it contributes nothing.
  - **`s_tempVeg` is an *instance* field of `ZoneSystem`, not a static** (`private List<float> s_tempVeg`, despite the `s_` prefix), and it is shared with `ZoneSystem.PlaceVegetation`, which clears and refills the same list for its own surround check. During a normal fresh-world genloc they cannot interleave, because `ZoneSystem.Update` returns before `CreateLocalZones` while `IsServer && !LocationsGenerated`. A console `genloc` **can** interleave them: `GenerateLocations()` never clears `LocationsGenerated`, so zone spawning continues between the coroutine's frames and `PlaceVegetation` wipes the baseline mid-location-type. A `genloc` re-run is therefore not just "different", it is **frame-timing dependent** (ZoneSystem line 418 / GenerateLocationsTimeSliced / PlaceVegetation / Update, decompiled 2026-09-22).
  - When all filters pass: `RegisterLocation(location, point, generated:false)`, `placed++`, and the loop moves to the next outer attempt.
- `RegisterLocation` **recomputes the zone from the point** (`GetZone(pos)`), not from the candidate zone.
  **Corrected 2026-09-23 — the escape threshold is `maxRadius > 64`, not `> 32`.** With `r > 32`,
  `GetRandomPointInZone`'s `Random.Range(-32 + r, 32 - r)` does have `min > max`, but
  `Random.Range(float,float)` is the lerp `(1-f)*max + f*min` with `f ∈ [0,1)`, so it spans the interval
  whichever way round the bounds are: the offset covers `±(r - 32)`, which is **narrower** than the zone
  until `r - 32 > 32`. So `32 < r ≤ 64` merely makes the entry sample an ever narrower strip around the
  zone centre (the opposite of the intended inset), and only `r > 64` lets the point leave the candidate
  zone. Measured in the SeedLab port (2026-09-23): `exteriorRadius = 40` put 0 of 20 000 draws outside
  the zone; `exteriorRadius = 70` put points outside it. When it does escape, the instance is registered
  in a zone whose biome area was never checked, and if that zone is already taken `RegisterLocation` logs
  `"Location already exist in zone"` and returns **without registering, while the caller still does
  `placed++`** (ZoneSystem.RegisterLocation / GetRandomPointInZone). The lerp form of
  `Range(float,float)` is no longer an inference from the disassembly: it was **measured in the running
  game** 2026-09-23 — `(1f - f)*max + f*min`, with the two rival forms separated by recorded draws
  (`world-generator.md` 9).
- If it can't reach `m_quantity`, it logs `"Failed to place all X, placed a out of b ..."`. There is no retry.
- The `200000 / 100000` constants in the outer loop are only the progress-bar ETA weights; the attempt budget is 60000/12000. In a release build `UnityEngine.Debug.isDebugBuild` is false, so the logged error total and `iterations:` print as 0 (the counters still increment; only the aggregation is gated).
- Observed on this install (LogOutput.log, world `test1`, 2026-09-22): six `LocationList`s contributed **3, 2, 27, 4, 25, 25 = 86** locations, all from scene `main` (so all from `ZoneSystem.m_locationLists`, none from `m_locationScenes`), and the run processed **183** entries (`locations: 183` = `ordered.Count` after the `m_enable`/`m_quantity` filter). **Corrected 2026-09-22 from the runtime dump: `ZoneSystem`'s own serialized `m_locations` supplies 130 entries, of which 88 survive the `m_enable`/`m_quantity` filter** — not "roughly 97 live". The estimate subtracted the 86 `LocationList` entries from 183 and forgot that the filter removes entries from both sources and that 16 more are injected by `AltBiome.m_addLocations`. 88 + 79 + 16 = 183.

### 3.4 Biome point sampling (AltBiomeWorldData / decompiled) – new in this build
- `VerifyBiomeData` runs on **every server world load**: it deletes the cache file, then calls `GenerateBiomePoints` and `GenerateSectors`, so nothing here is persisted.
- The grid is 2048 x 2048 points at **12 m** spacing: `MapSpaceToWorldSpace(i) = (i - 1024) * 12 + 6`. **The float overload evaluates in double with one narrowing at the return** (Mono loads the `float32` argument as the native `F` type and only converts back when returning), which is invisible for the integer callers -- every intermediate is exact there -- but decides `BiomeSector.Center`, its only non-integer caller. Measured 2026-09-23: the per-step float spelling misses 57 of 938 sector centres by exactly 1 ULP on seed 75539276, and 44 of the derived `DistanceFromCenter` values with them; the double spelling misses none. (AltBiomeWorldData.MapSpaceToWorldSpace / decompiled + `goldens\altbiomes-assignment-0480A34C.json`.) Points with `x²+z² > 110250000` (radius **10500 m**) are forced to Ocean with height -1000. Every other point stores `GetBiome` and `GetBiomeHeight(biome, x, z)` — which for a known biome is identical to `WorldGenerator.GetHeight(x, z)`. Arrays are indexed `[x, z]`; the build loop is `for i (z) { for j (x) }`. `world.m_biomeData` is assigned only **after** the loop, so `GetBiomeSector` sees null during it — harmless, because the `BiomeSector` local in `GetBiomeHeight` is dead code and `AltBiome.heightMapChanges` is a `const false`: **no alt biome ever changes terrain height in this build**.
- `GenerateSectors`: AshLands, DeepNorth and Ocean each get **one** sector (created in that order), filled by a row-major scan; every other biome is 4-connected flood-filled into components, outer scan `for y { for x }`, neighbours pushed `+x, -x, +y, -y`, popped LIFO. **Quirk: the seed point of each flood-filled component is never added to `AllPoints`/`AllPointsAboveSeaLevel`** — only `tryFill` appends, and the outer scan marks the seed visited and pushes it directly. One point per component of every non-global biome is therefore invisible to the point pickers.
- `BiomeSector.Center` is the mean of that sector's **edge** points (points with a differing 4-neighbour), computed over the interior `1..Size-2` only, then mapped to world space; `Min`/`Max` default to `(0,0)` rather than ±infinity. The edge pass probes neighbours in the order **`-x, +x, -y, +y`** (not the flood fill's `+x, -x, +y, -y`), and because the four comparisons are chained with `||`, the captured `item` is the **first** differing neighbour only: **at most one `BiomeSector` is offered to `Neighbors` per edge point**, even when two, three or four neighbours differ. `Neighbors` is read only by `CanAddModifier`. (AltBiomeWorldData.GenerateSectors / decompiled 2026-09-22) **`sector.MaxZone` is assigned from `sector.Min`** (and `new Vector3(x, y)` puts y in the Y slot, so `GetZone` reads `z = 0`), so `MaxZone == MinZone`, `ZoneCount` is always 1 and `IsDiscovered` is **always false**. None of it is read on the placement path.
- Each biome gets `AllPoints`, plus `AllPointsAboveSeaLevel` (points with `PointHeights >= 30`, the raw height, not `height - 30`). `GetRandomPointByBiome(s)` picks `Random.Range(0, count)` from that list (seeded location stream). If the above-sea list is empty, it falls back to all points — the count is tested before any draw, so exactly one int is drawn either way.
- The `Biomes` dictionary is keyed by `Enum.GetValues(typeof(Heightmap.Biome))`, so it also contains `None`, `Land (639)` and `All (895)` with empty lists, and is iterated in ascending-value order.
- **`RandomBiomeFromBiomes`** (single-bit or zero masks return unchanged, no draw). Otherwise `num` = popcount over the 9 named bits, `num2 = Random.Range(0, num - 1)` (one int, **exclusive** upper bound), then a fall-through chain testing, in order, Meadows→Meadows, Swamp→Swamp, Mountain→Mountain, BlackForest→BlackForest, **Plains→BlackForest**, AshLands→AshLands, DeepNorth→DeepNorth, **Meadows→Ocean** (the 8th test reads the *Meadows* bit, not Ocean), else `return Mistlands`. With `P7` = popcount over Meadows..DeepNorth and `M` = Meadows set:
  - the draw range is `[0, num-2]`, so the highest slot is unreachable; with exactly 2 bits set the draw is always 0 and the first tested biome always wins;
  - reachable slots are the `P7` tested biomes, then one extra slot returning **Ocean** if Meadows is set; any draw `>= P7 + M` falls through to **Mistlands**;
  - the Ocean and Mistlands bits only inflate `num`; the Ocean bit is never tested. **Ocean is returned only when Meadows, Ocean and Mistlands are all set** and the draw equals exactly `P7`;
  - e.g. `Ocean|Mistlands` → always Mistlands; `Meadows|BlackForest` → always Meadows; `Swamp|Mountain|Mistlands` → never Mistlands.

  Result: the returned biome **need not be in the requested mask**, so multi-biome locations draw candidate *zones* from a skewed biome and the per-point biome filter (step 2) still decides the final biome — a large share of attempts is wasted by design.
- Biome enum values (Heightmap.Biome via Cecil): None 0, Meadows 1, Swamp 2, Mountain 4, BlackForest 8, Plains 16, AshLands 32, DeepNorth 64, Ocean 256, Mistlands 512, All 895, Land 639. BiomeArea: Edge 1, Median 2, Everything 3.
- **`m_biomeArea` in the shipped assets holds values outside the enum, and one of them is fatal.** The dumped table (`SeedLab/data/1.0.15-59f53fb5/locations.json`, 232 entries) carries `m_biomeArea` = 7 (the default), 6, 3, 2, 1, -1 and **0**. Only bits 1 (Edge) and 2 (Median) are ever *returned* by `WorldGenerator.GetBiomeArea`, so what matters is the low two bits: 7, 3 and -1 all mean "unconstrained", 6 and 2 mean **Median only**, 1 means Edge only, and **0 matches nothing at all** — `if ((location.m_biomeArea & biomeArea) == 0) continue;` is then true for every zone in every seed. Exactly one running entry has 0: **`GoblinCamp2_1`** (m_quantity 5, enabled), which is therefore **structurally unplaceable in all 4,294,967,296 worlds**, not merely rare (verified 2026-09-23).
- AltBiomes: `GenerateAltBiomes` seeds with `worldSeed + 920` (pointless — every pair re-seeds), then `(int)biome + altName.GetStableHashCode() + worldSeed` per biome/alt pair. It then `Utils.Shuffle`s that biome's `Sectors` list **in place** (Fisher-Yates from the end, `Count-1` int draws), so the order seen by the next alt biome depends on all previous shuffles, and walks it drawing one `Random.Range(0f,1f)` per sector examined while `Sectors.Count < m_maxAmountSpawned` (even for sectors that then fail `CanAddModifier`). `GetValidAltBiomes` uses `m_biome.HasFlag(key)`, which is always true for the `None` key.
  `BiomeSector.CanAddModifier` tests, in order: `DistanceFromCenter >= m_minDistanceFromCenter`; `EdgeCount` in `[m_minEdgeSize, m_maxEdgeSize)`; `HeightAvg` in `[m_minAvgHeight, m_maxAvgHeight)`; the four world-bound fields against `Center` (each ignored when 0); mutual `m_incompatibleAltBiomes`; then `m_requireNeighbor`/`m_notNeighbor`.
  **Corrected 2026-09-22 — the earlier description of the two neighbour tests ("can only ever match None/Meadows/Swamp/Mountain/BlackForest") was wrong.** Both loops run `for (int i = 0; i < 10; i++)`, test the mask with `mask.HasFlag(((Heightmap.BiomeIndex)i).ToBiome())` and then compare `neighbor.Biome == (Heightmap.Biome)i`. `i` is a **BiomeIndex** (None 0, Meadows 1, Swamp 2, Mountain 3, BlackForest 4, Plains 5, AshLands 6, DeepNorth 7, Ocean 8, Mistlands 9 — `Heightmap.BiomeIndex`, mapped by `BiomeHelpers.ToBiome`), so the bit examined and the biome compared come apart:
  - **`m_requireNeighbor != None` makes `CanAddModifier` return `false` unconditionally.** At `i == 0`, `((BiomeIndex)0).ToBiome()` is `Biome.None == 0` and `Enum.HasFlag(0)` is true for every mask, so that iteration is never skipped and demands a neighbour with `Biome == Biome.None`. No `BiomeSector` can have `Biome == None`: the three global sectors are built with AshLands/DeepNorth/Ocean and every flood-fill sector with `PointBiomes[x,y].ToBiome()`, and `WorldGenerator.GetBiome` never returns `None`. So **no alt-biome with a non-zero `m_requireNeighbor` can ever be placed.**
  - `m_notNeighbor`'s `j == 0` iteration is harmless (it would reject only on a `None` neighbour). For `j >= 1` the mapping is: Meadows bit → rejects a Meadows(1) neighbour (correct); Swamp bit → Swamp(2) (correct); Mountain bit → `(Biome)3`, unreachable; **BlackForest bit → rejects a *Mountain*(4) neighbour**; Plains/AshLands/DeepNorth bits → `(Biome)5/6/7`, unreachable; **Ocean bit → rejects a *BlackForest*(8) neighbour**; Mistlands bit → `(Biome)9`, unreachable.
  (BiomeSector.CanAddModifier, BiomeHelpers.ToBiome, Heightmap.BiomeIndex / decompiled 2026-09-22.)
  **Vanilla 1.0.15 ships 32 AltBiomes and every one has `m_enabled = true`** (corrected 2026-09-22 from
  **28**, and replacing the earlier "Unverified: whether vanilla ships any enabled AltBiomes"). The
  authority is now `AltBiomeList.m_altBiomes` read out of the running game — `altbiomes.json` in
  `data\1.0.15-59f53fb5\` (dumped from the running game 2026-09-22 by `tools\SeedLab.Dumper`; `vseed data --verify` re-checks it) — which has `count: 32, enabledCount: 32`. The earlier 28 came from a structural scan of
  the decompressed bundle `valheim_Data\StreamingAssets\SoftRef\Bundles\d59cfac`, which found 28
  contiguous `AltBiome` records at offsets 5156960–5181556; **that scan missed four** — `Mushroom`,
  `Lantern`, `Bones` and `Menhir` — so a contiguous-record count is a lower bound, not a census.
  Between them the 32 inject **16** locations (`m_addLocations`) and **65** vegetation entries
  (`m_addVegetation`) into `ZoneSystem`'s tables. Full names, in list order: Mushroom, Lantern, Bones,
  Menhir · Meadows: Dark, Peaceful, Dandelion, Raspberry, Smalltree, Birch · Black Forest: Troll, Root,
  Ruin, Rock, Pinetree, Blueberry, Kalhygge · Swamp: Hut, Bog, Bat, Abomination · Mountain: Wolf, Drake,
  Fortress · Plains: Lox, Goblin, Death · Mistlands: Rockless, Trees, Swords, Hare, BroodSwarm.
  The game's own log for a fresh world on 2026-09-22 20:37:19 contains
  `Loading: Placed 0/1-2 of 'Fortress Mountain' altbiome. (Valid, sectors: 0, combos: 2)`; the others log
  through `ZLog.DevLog`, which is why no warning appeared before. Corroboration in the same log: the `_1` location variants that only an
  `AltBiome.m_addLocations` can contribute are processed by genloc (`SwampHut1_1` placed 33 of 50,
  `TarPit1_1` 0 of 50, `GoblinCamp2_1` 0 of 5).
- **`GenerateAltBiomes` never clears `AltBiome.Sectors`.** It resets only `ValidPlacementSectors` and
  `ValidPlacementSectorCombos`, while its quotas test `Sectors.Count` against `m_maxAmountSpawned` /
  `m_minAmountSpawned` and `BiomeSector.AddModifier` appends to `modifier.Sectors`. Running it twice in
  one process — `genloc alt`, or any tool that generates several worlds without reloading the `main`
  scene — therefore starves the second run and leaks the first world's 2048² grid through the stale
  `BiomeSector` references. Normal play is unaffected because a `main` scene load re-instantiates the
  `AltBiomeList` prefab and creates fresh `AltBiome` objects (ZoneSystem.Awake → Instantiate;
  AltBiomeList.Awake/OnDestroy).
- Because `MaxZone == MinZone` (see 3.4's edge-pass note above), the `IsDiscovered` loop body
  `for k = MinZone.y; k < MaxZone.y` never executes, so `GenerateSectors` never actually dereferences
  `ZoneSystem.instance` — it can be called from a tool with no `ZoneSystem` in the scene.
- **Measured offline, 2026-09-23** (`src\SeedLab.Locations`, harness
  `tools\SeedLab.LocationLab grid`). The point grid is the *same geometry* as the minimap cache
  (`Minimap.GenerateWorldMap` samples pixel `(j,i)` at `(j-1024)*12+6 / (i-1024)*12+6` and stores it at
  `k = i*2048 + j`, which is exactly `MapSpaceToWorldSpace`), so the cached minimap is a legitimate
  oracle for it:
  - **biomes: 1 479 782 / 1 479 782 (100 %) decodable pixels inside the 10 500 m cut-off agree** on
    `asdasdasd`, and 1 499 670 / 1 499 670 on the hold-out `testworldclaude`. Outside the cut-off the two
    are *expected* to differ — `GenerateBiomePoints` forces Ocean/-1000 there while the minimap keeps
    sampling `GetBiome` — and all 1 062 710 decodable pixels there do differ, none of them Ocean.
  - **heights: 2 405 324 / 2 405 324 binary16 codes identical per world**, using the away-from-zero
    tie rule the acceptance suite measured for `Mathf.FloatToHalf`.
  - **The `sqrMagnitude > 110250000f` vs `DUtils.Length > 10500f` mismatch has an EMPTY intersection on
    this lattice**: 0 of 4 194 304 points pass the first test and fail the second, on both worlds. Both
    tests are pure geometry on a fixed 12 m lattice, so that is seed-independent — no grid point ever
    stores the `-400` that `GetBiomeHeight`'s own cut-off returns. This settles the open question in
    porting spec 02 §2.4/§13.
  - **The missing-seed-point quirk is exactly one point per flood-filled sector**: 4 193 348 of
    4 194 304 points are reachable by `GetRandomPointByBiome`, and the 956 unreachable ones equal the
    956 flood-filled sectors (959 total minus the three global ones). Hold-out: 948 of 951.
  - Cost on a 16-core desktop: ~270 ms for the 4 194 304 `GetBiome`+`GetBiomeHeight` pairs (parallel
    across row bands, one `WorldGeneratorPort.Fork` per worker), ~50 ms for the flood fill and edge
    pass, on top of ~300-400 ms to construct the `WorldGenerator` itself. Sector count ≈ 950 per world.

### 3.5 Is placement deterministic from the seed?
**The inputs that fully determine the candidate list:**
- world seed (`World.m_seed`)
- `m_worldGenVersion`, which feeds terrain and biomes (WorldGenerator ctor → VersionSetup)
- the exact ZoneLocation list, its order and its parameters, which change with game version and mods
- AltBiome data
- the zones already generated, and the locations already placed

**Not inputs:**
- frame timing (the state swap isolates the stream)
- world modifiers / global keys: nothing in the placement path reads them
- player count

**Consequences:**
- Each location type has its own RNG stream (seed + name hash). Adding a new location type does not change the random *draws* of other types. It can still change their *outcomes*, because the zone becomes occupied and the similar/group distance checks see the new instance. Only locations processed later in the order are affected.
- Re-running generation (`genloc` or a version bump) keeps placed instances, re-rolls all unplaced ones, and skips generated zones. New or changed locations therefore appear **only in unexplored zones**, and unplaced candidates move after exploration.
- `m_unique` locations: every candidate registers. When the first candidate's zone is generated, `PlaceLocations` calls `RemoveUnplacedLocations(location)`, which deletes all other unplaced instances of that type (ZoneSystem.PlaceLocations / RemoveUnplacedLocations). **The surviving position depends on which candidate zone a player (or ghost zone) reaches first.**
- Mid-game additions that are not seed-derived:
  - `SpawnLocationMidGame(name, pos, out spawned)`: server-only. It clamps the position inside the zone and calls `RegisterLocation(... placed:false)`. It is used by `PersistentEventSystem.RPC_RequestStartEvent`.
  - `TestSpawnLocation` (console `location`): spawns at once, disables world saving unless `SAVE` is given, and uses `Random.Range(0, 99999)` as the seed.

### 3.6 Storage (ZNet.SaveWorldThread / ZoneSystem.Save / Load / SaveSystem)
- This build uses a **chunked save**: `<SaveDataPath>/worlds_local|worlds/<worldName>/_main.<saveNumber>.db2` (plus `.chunks` for ZDOs, `.fwl2` for metadata, and an OK marker) (SaveSystem.MainDb2FileName / GetWorldsSaveRootPath / World.GetSaveDirectory). The legacy single-file format goes through `ZNet.LoadOldWorld` → `ZoneSystem.LoadOld`.
- `.db2` layout: `int 41` (version), `double netTime`, then `ZoneSystem.Save`, `RandEventSystem.Save`, `PersistentEventSystem.Save`.
- `ZoneSystem.Save` writes one compressed `ZPackage`, as an int length followed by the bytes:
  `int nGeneratedZones, Vector2s[]` · `int m_locationVersion` · `int nKeys, string[]` (**only non-server-option global keys**) · `bool locationsGenerated` · `int nLocations`, then `{int prefabNameHash, float x, float y, float z, bool placed}` for each location.
- The actual location objects (proxy, networked parts, dungeon rooms) are ZDOs in `.chunks`.

### 3.7 Reproduced offline, bit-exactly (2026-09-23)

`src\SeedLab.Locations` is a port of `GenerateLocationsTimeSliced`,
`AltBiomeWorldData.GenerateSectors` and `GenerateAltBiomes`. As of 2026-09-23 it reproduces the game's
own output exactly on three worlds. Gate: `dotnet run -c Release --project tools\SeedLab.LocationLab -- gate`.

| oracle | what it is | result |
|---|---|---|
| `goldens\locationinstances-0480A34C.json` | the FRESH world (seed 75539276) dumped straight after genloc, nothing explored, so 12 228 instances of which only 25 were ever placed | **12 228 / 12 228** with zone, prefab and x/y/z bit-identical; 0 missing, 0 extra, 178/178 prefabs exact, every type's `placed` counter equal |
| `goldens\altbiomes-assignment-0480A34C.json` | the sector decomposition and alt-biome assignment of the same world | **938 / 938** sectors agree on biome, EdgeCount, Center, Min, Max, MinZone, MaxZone, HeightMin/Max/Avg, DistanceFromCenter and the neighbour list (floats as bit patterns); all 12 biome keys agree on `AllPoints`/`AllPointsAboveSeaLevel`; **32 / 32** alt-biomes have an identical sector list *in `AddModifier` order*, 99/99 sector slots, and equal `ValidPlacementSectors`/`ValidPlacementSectorCombos` |
| `groundtruth\worlds\*\_main.0.db2` | the two PLAYED worlds | **12 314 / 12 314** (`asdasdasd`) and **12 287 / 12 287** (`testworldclaude`), and **0** engine instances the save no longer holds |
| `groundtruth\LogOutput-20260922-worldgen.log` | the game's own log of `testworldclaude`'s creation | all **29** types that logged a `placed N out of M` line reproduced exactly (Crypt4 170/200, TarPit1 91/100, NorthVillage 53/135, MountainCave02 82/120, GoblinHut03 2/20, TarPit3_1 0/100 ...), plus the single alt-biome under-min warning (`Fortress Mountain` 0/1-2, valid sectors 0, combos 2) with the same counters |

**A played world needs no special handling — but the precondition is real, so state it.** An offline
reproduction must run with an **empty generated-zone set**, and that is legitimate only because genloc
runs once, at creation, before any zone exists -- the log shows
`missing /worlds/testworldclaude/_main.0.db2` immediately before `Loading: Generating locations` -- and
is never re-run while `LocationsGenerated` is true and `m_locationVersion` is unchanged. Feed a played
world's `m_generatedZones` back in and the answer is **wrong**, not more accurate. In
both of them **no `m_unique` candidate has been pruned yet**: all 10 `Vendor_BlackForest`, 10
`Hildir_camp` and 10 `BogWitch_Camp` candidates are still in the save.

**Vanilla 1.0.15 table facts, measured from the dump rather than assumed** (they decide which parts of
the algorithm are live):
- **183 entries run** = `m_enable && m_quantity != 0`; 232 total, 186 `m_enable`, 200 `m_quantity != 0`.
  183 is the number the game's own log prints (`locations: 183`).
- **No running entry has `m_exteriorRadius == 0`**, so the "ten `insideUnitCircle` draws are spent even
  at radius 0" behaviour is never observed in vanilla.
- **The largest `Mathf.Max(m_exteriorRadius, m_interiorRadius)` is exactly 32.** The point draw's bounds
  therefore never invert and no point ever leaves its candidate zone, so `RegisterLocation`'s
  recomputed zone always equals the candidate zone and its silent "location already exists in zone"
  drop is unreachable.
  **Measured 2026-09-23 — the `placed` counter and the instances that exist are the same number.**
  Over a 5 000-seed x 183-type study, in all **915 000 type-seed cells** the `placed` counter equalled
  the number of instances actually registered. (Measured in the SeedLab **port**, not in 5 000 game
  launches; the port's counter is bit-exact with the game's on every oracle in this section, including
  the 29 logged `placed N out of M` lines.) The two can only diverge where
  `RegisterLocation` drops a point into an already-taken zone while the caller still does `placed++`,
  which first requires the point to leave its candidate zone — and that needs a radius above the
  shipped cap (3.3: the point only actually escapes at `maxRadius > 64`). So a reproduction may read
  `placed` as the instance count for every vanilla type.
  (`docs\studies\altar-quantity-findings.md`.)
- **19 entries have no `m_prefab.m_assetID`** (all-zero, `isValid: false`) and every one of them is
  `m_enable = false`, so an id-less entry never reaches `m_locationIDCache`.
- **All 32 alt-biomes have `m_requireNeighbor == None` and `m_notNeighbor == None`**, so both neighbour
  bugs documented above are real but dead in vanilla.
- **8 running entries are `m_unique`**: `Vendor_BlackForest` 10, `AncientUpgradeStation` 10,
  `Hildir_camp` 10, `BogWitch_Camp` 10, `BigRockClearing` 10, and `PlaceofMystery1/2/3` 1 each. Only the
  quantity-1 ones have a predictable position.
- **Boss altars are not unique and not single.** `Eikthyrnir` 3, `GDKing` 4, `Bonemass` 5,
  `Dragonqueen` 3, `GoblinKing` 4, `Mistlands_DvergrBossEntrance1` 5, `FaderLocation` 3,
  `DN_Bossroom` 3 -- all `m_prioritized`, so they are `Ordered[0..22]` and answerable from a
  23-entry prefix of the run.
- **21 running location prefabs carry a `DungeonGenerator`** (`locationprefabs.json`), which is the only
  evidence-based definition of "dungeon" in the dumped data.
- **The boss and trader lists ARE derivable and need not stay curated** (2026-09-24). A boss altar is a
  prefab holding an `OfferingBowl` with `m_bossPrefab` set and that prefab's `Character.m_boss` true;
  the boss's player-facing name is that Character's `m_name` resolved through localization, and
  `m_bossOrder` is the progression index. A trader camp is a prefab holding a `Trader`, named by
  `Trader.m_name`. **The location walk alone finds only seven of the eight bosses.** The Mistlands
  queen's bowl is not in a location prefab at all: it is in the ROOM prefab
  `dvergr_new_bossroom_ENTRANCE02` (`roomTheme` 256) -- the only `OfferingBowl` among all 358 walked
  rooms -- because `Mistlands_DvergrBossEntrance1` generates its interior rather than containing it.
  Joining `locationprefabs` `generators[].themes` to the room's theme (theme 256 matches that one
  location and no other) completes the set and makes `m_bossOrder` contiguous 0-7. **Join on
  `roomDataTheme`, not `roomTheme`**: `DungeonGenerator.SetupAvailableRooms` filters on
  `DungeonDB.RoomData.m_theme`, and the two disagree on 3 of the 358 rooms -- `CharredRuins9` and
  `CharredRuins24` (component 12288, RoomData 4096) and `CharredRuins_new03` (component 4096, RoomData
  12288), all `_RoomList_Ashlands`. For the queen's room both read 256, so the boss table is the same
  either way; an Ashlands question would not be.

  | order | location prefab | boss |
  |---|---|---|
  | 0 | `DN_Bossroom` | Kall Fimbulbringer |
  | 1 | `Eikthyrnir` | Eikthyr |
  | 2 | `GDKing` | The Elder |
  | 3 | `Bonemass` | Bonemass |
  | 4 | `Dragonqueen` | Moder |
  | 5 | `GoblinKing` | Yagluth |
  | 6 | `Mistlands_DvergrBossEntrance1` | The Queen *(via the room join)* |
  | 7 | `FaderLocation` | Fader |

  `NorthMemorialPlace` also carries a bowl ("Ancient Altar", Memorial Coal x3) but its `bossFlag` is
  false -- it is not a boss site. Traders are `Vendor_BlackForest` -> Haldor and `Hildir_camp` ->
  Hildir; `BogWitch_Camp`'s `Trader.m_name` is the **empty string** in this build, so the game itself
  supplies no name there (localization does hold `npc_bogwitch` = "The Bog Witch", but nothing joins
  the two -- treat that as a gap, not a name).

**Which details are load-bearing, measured by planting each defect and re-running the fresh-world
oracle** (9 of 12 caught; the fresh world is the discriminator, not an accident):

| planted defect | fresh-world instances still exact |
|---|---|
| the two float draws of `GetRandomPointInZone` swapped | 408 / 12 228 (3.3 %) |
| the flood fill's seed point appended to `AllPoints` ("the fix") | 2 788 (22.8 %) |
| `Ordered` not partitioned `m_prioritized`-first | 4 016 (32.8 %) |
| `RandomBiomeFromBiomes` drawing `Range(0, num)` instead of `Range(0, num - 1)` | 8 456 (69.2 %) |
| the surround check keeping its first nine points instead of discarding them | 9 393 (76.8 %) |
| the `Plains` slot of `RandomBiomeFromBiomes` returning Plains instead of BlackForest | 11 675 (95.5 %) |
| `MapSpaceToWorldSpace` evaluated per step in float | 12 228 -- but 57 sector centres and 44 `DistanceFromCenter` wrong |
| the alt-biome walk skipping the `Biome.None` key | 12 228 -- but `ValidPlacementSectorCombos` wrong on all 32 |
| `Utils.LengthXZ` used for filter 1 instead of `Vector3.magnitude` | 12 228 -- **not discriminated** (only 18 entries use filter 1, y is 0 there, and no draw straddled the 1-ULP gap) |
| `GetTerrainDelta` skipped at `m_exteriorRadius == 0` | 12 228 -- **not discriminated** (no running entry has radius 0) |
| `RegisterLocation` keyed on the candidate zone | 12 228 -- **not discriminated** (max radius is 32) |
| `CanAddModifier`'s `m_requireNeighbor` loop started at `i = 1` | 12 228 -- **not discriminated** (no alt-biome sets it) |

The last four are correct by IL reading and are **not** confirmed by any oracle that exists; a modded
table would reach the first three of them.

### 3.8 Searching the seed space for a location (2026-09-23)

Everything here is measured with `SeedLab.Locations`, which is bit-exact against the game's own dumps
(3.7), over **512 seeds** drawn by a fixed xorshift spread. It is what a "find me a seed with X" query
can and cannot promise.

**The ordered-prefix property.** Each type opens its own stream with
`worldSeed + m_prefab.Name.GetStableHashCode()`, so **nothing after a type in `Ordered` can change
it** -- but everything before it can, through four shared channels: zone occupancy (global, one
location per zone), the `m_locationIDCache` AssetID bucket, the `m_group`/`m_groupMax` buckets, and
`CountNrOfLocation`. So a type's instances are final exactly when **its own entry's loop returns**, and
the cheapest correct run for a question is `OrderedIndexOf(target) + 1` entries -- no shorter.
Verified against the real vanilla table: a 38-entry prefix reproduces `Eikthyrnir`,
`Vendor_BlackForest`, `FaderLocation`, `Crypt2` and `GoblinCamp2` with **float32-identical x/z** to the
full 183-entry run (`tests\SeedLab.Search.Tests`, section 6).

| question | ordered prefix of 183 |
|---|---|
| the spawn temple (`StartTemple`) | 1 |
| one boss (`Eikthyrnir`) | 2 |
| sunken crypts (`SunkenCrypt4`) | 6 |
| infested mines (`Mistlands_DvergrTownEntrance1/2`) | 11 |
| all seven boss altars (last is `FaderLocation`, ordered 16) | 17 |
| charred fortresses | 21 |
| all three traders (last is `BogWitch_Camp`, ordered 21) | 22 |
| surtling geysers (`FireHole`) | 30 |
| Fuling villages (`GoblinCamp2`) | 38 |
| troll caves (`TrollCave02`) | 62 |
| burial chambers (last is `Crypt4`, ordered 66) | 67 |
| tar pits (`TarPit3`, ordered 105) | 106 |
| frost caves (`MountainCave02`) | 107 |
| the alt-biome tar pits (`TarPit3_1`, the last entry) | 183 |

**The prefix is not where most of the time goes.** One seed on one core, this machine, idle:
`WorldGeneratorPort` construction + lake/river/stream pre-generation 0.31-0.44 s; the 2048x2048
biome-and-height point grid (4,194,304 `GetBiome` + `GetBiomeHeight` pairs) **1.44-1.54 s**; the sector
decomposition 0.064-0.069 s; the alt-biome assignment 0.001-0.003 s. Only then the placement: 0.002 s
for one boss, 0.07 s for all seven, 0.08-0.11 s for bosses and traders, 1.1-1.4 s to reach `Crypt4`,
**6.5-6.8 s for all 183**. The grid is not optional -- `GetRandomPointByBiomes` draws the candidate
zone out of it and filters 10a/10b read the sector a point falls in -- so **~1.9 s per seed per core is
a floor no location query can go under**, and the prefix only shortens what sits on top of it.

**Hard rings, from the table itself.** Filter 1 tests `Vector3(x, 0, z).magnitude` against
`m_minDistance`/`m_maxDistance` (0 = off) and filter 5 tests `Utils.LengthXZ` against
`m_min/maxDistanceFromCenter`; filter 2 forces the point into `m_biome`, so `WorldGenerator.GetBiome`'s
own distance band applies too. Those combine into a ring that holds **for every seed**:

| type | ring | why |
|---|---|---|
| `Eikthyrnir` | 0 .. 1,000 m | `m_maxDistance` 1000 |
| `Vendor_BlackForest` (Haldor) | >= 1,500 m | `m_minDistance` 1500 |
| `Hildir_camp`, `BogWitch_Camp` | >= 3,000 m | `m_minDistance` 3000 |
| `GDKing` | 1,000 .. 7,000 m | `m_min/maxDistance` |
| `Bonemass` | 2,000 .. 6,000 m | `m_minDistance` 2000, Swamp band ends at `m_maxMarshDistance` 6000 |
| `Mistlands_DvergrBossEntrance1` | >= 5,900 m | Mistlands needs `dist > 6000 + A`, `A` in [-100, 100] |
| `FaderLocation`, `CharredFortress` | >= 7,900 m | AshLands: `|(x, z - 4000)| > 12000 + A` |
| `SunkenCrypt4` | >= 2,000 m | Swamp needs `dist > 2000` (no wobble on that one) |

So **"all seven bosses within 6 km" and "all three traders within 3 km" are impossible for all
4,294,967,296 worlds**, not merely rare, and a search tool should say so instead of scanning for them.

**Measured distributions** -- distance from the world centre to the *nearest* instance of a type, over
the same 512 seeds, metres:

| type | min | p5 | median | p95 | max |
|---|---|---|---|---|---|
| `StartTemple` | 0 | 3 | 65 | 366 | 538 |
| `Eikthyrnir` | 29 | 105 | 302 | 549 | 850 |
| `GDKing` | 1,005 | 1,122 | 1,882 | 3,894 | 5,610 |
| `Bonemass` | 2,086 | 2,173 | 2,843 | 4,213 | 5,186 |
| `Dragonqueen` | 827 | 1,092 | 3,604 | 6,087 | 7,120 |
| `GoblinKing` | 3,013 | 3,159 | 3,836 | 5,304 | 5,978 |
| `Mistlands_DvergrBossEntrance1` | 5,974 | 6,096 | 6,480 | 7,407 | 8,244 |
| `FaderLocation` | 8,302 | 8,561 | 8,979 | 9,424 | 9,810 |
| `Vendor_BlackForest` (nearest candidate) | 1,504 | 1,534 | 1,914 | -- | 5,533 |
| `Vendor_BlackForest` (furthest candidate) | 5,771 | 8,261 | 9,565 | -- | 10,281 |
| `Hildir_camp` (nearest candidate) | 3,000 | 3,003 | 3,071 | 3,478 | 3,919 |
| `BogWitch_Camp` (nearest candidate) | 3,000 | 3,014 | 3,236 | 3,870 | 4,950 |
| `Crypt2` | 509 | 535 | 628 | 778 | 1,294 |
| `SunkenCrypt4` | 2,028 | 2,067 | 2,114 | 2,426 | 2,811 |
| `MountainCave02` | 689 | 749 | 1,154 | 2,279 | 3,847 |
| `GoblinCamp2` | 2,902 | 2,923 | 2,978 | 3,059 | 3,161 |
| `FireHole` | 2,001 | 2,030 | 2,182 | 2,882 | 3,875 |
| `CharredFortress` | 8,450 | 8,539 | 8,738 | 8,924 | 9,089 |

Counts, same 512 seeds: burial chambers within 2 km min 24 / median 72 / max 127; sunken crypts within
5 km median 113; troll caves within 5 km median 80; frost caves within 5 km median 38; infested mine
entrances within 8 km median 155; boss altars (all 27 of the seven types) within 5 km median 11,
max 17.

**`StartTemple` is essentially the origin.** Median 65 m, 95th percentile 366 m, maximum 538 m over
384 seeds -- so "the island at (0,0)" and "the island the player spawns on" are the same component in
all but a handful of worlds. That is what makes a centre-based spawn-island metric usable without
running a placement.

**A group count is not "one of each".** `m_quantity` is 3-5 for every boss altar, so "7 boss altars
within 6 km" is satisfied by three `Eikthyrnir` and four `GoblinKing` with five of the seven bosses
missing. To mean one of each you need the largest of the per-type nearest distances.

**Alt-biome blocking is live in vanilla.** All 32 alt-biomes are `m_enabled`, and five of them carry a
non-empty `m_blockLocationNames` that matches a running entry's `m_name`: `Lox Plains` and
`Death Plains` both block `GoblinCamp2`, `StoneTower1` and `StoneTower3` (Death Plains also blocks
`StoneHenge1/2/3`), `Rock Black Forest` blocks `TrollCave02`, `Fortress Mountain` blocks
`StoneTowerRuins05`, `Dark Meadows` blocks `WoodFarm1`, and `Bog Swamp` blocks `InfestedTree01`. The
eight entries injected by an `m_addLocations` are correspondingly rare: `TarPit1_1`/`TarPit2_1`/`TarPit3_1`
placed anything in **one** of four probe seeds (1, 1 and 4 instances, all beyond 6.6 km) and in **none**
of the three ground-truth worlds. *Unverified at a useful sample size:* that is seven seeds, not 512 --
treat the direction as measured and the rate as a hint. Their parent `Death Plains` has
`m_minAmountSpawned = 0`, as do `Lox Plains` and `Goblin Plains`, so those three alt-biomes -- and every
location that names them as a parent -- can be absent from a world outright.
**Corrected 2026-09-23 — `GoblinCamp2_1` does not belong in this paragraph.** It placed 0 of 5 in every
seed ever probed, but the cause is not alt-biome rarity: its `m_biomeArea` is **0**, which matches no
zone (see section 2). Its siblings under the same `Goblin Plains` parent, `GoblinHut01..03`, placed
30 of 30 in two of the three ground-truth worlds, so the parent was plainly present. `GoblinCamp2_1` is
impossible, not rare, and a seed search may refuse any query that requires it.

---

## 4. Placing a location in a zone (ZoneSystem.PlaceLocations / SpawnLocation / CreateLocationProxy)
- This only happens during zone generation, and only if the zone has an unplaced instance.
- Position: a **local copy** of `m_position` is re-snapped with `GetGroundData`, a terrain raycast from +5000 over 10000 m (if `m_snapToWater`, then `y = 30`) and that copy is what gets spawned. **`LocationInstance.m_position` itself is never updated**, so the position stored in the `.db2` is exactly the one `GenerateLocations` produced, with `y = WorldGenerator.GetHeight(x, z)`. That makes a world save an exact oracle for validating an offline reimplementation.
- `m_clearArea` adds a ClearArea with radius `m_exteriorRadius`. The vegetation test `InsideClearArea` treats it as an **axis-aligned square** of half-size r, not a circle.
- **Rotation is not seeded from the world seed:**
  - `m_slopeRotation`: uses the private `GetTerrainDelta`, which draws `Random.insideUnitCircle` samples, snapped to 22.5°.
  - otherwise `m_randomRotation`: `Random.Range(0,16) * 22.5°`.
  - otherwise identity.

  No `InitState` precedes these draws in `SpawnZone`/`PlaceLocations`, so they come from the ambient global `UnityEngine.Random` state. The rotation is persisted as the proxy's ZDO rotation (`ZNetView.Awake` → `m_zdo.SetRotation`).
- Zone seed: `seed = worldSeed + zone.x*4271 + zone.y*9187`. `SpawnLocation` calls `Random.InitState(seed)` and then runs `RandomSpawn.Randomize` and `RandomObject.Randomize`. It instantiates each enabled `ZNetView` child at `pos + rot*localPos` (so each gets its own ZDO) and calls `DungeonGenerator.Generate(mode)` on any generator among them. Finally it creates a **LocationProxy**.
- `LocationProxy.SetLocation(name, seed, spawnNow)` stores `ZDOVars.s_location` (prefab-name hash) and `ZDOVars.s_seed` in the proxy ZDO. On every peer that loads the proxy, `LocationProxy.SpawnLocation` → `ZoneSystem.SpawnProxyLocation(hash, seed, pos, rot)` → `SpawnLocation(..., SpawnMode.Client)`. That call re-seeds with the same seed and instantiates the **non-networked** prefab root (with its `Location` component), with the ZNetView children deactivated. As a result, every client rebuilds identical decorative randomisation.

### 4.1 The seeded child stream inside SpawnLocation (decompiled 2026-09-23, 1.0.15)

What `Random.InitState(zoneSeed)` is actually spent on, and in what order. This is what makes a location's decorative content computable offline.

- `SpawnLocation` builds three arrays with `Utils.GetEnabledComponentsInChildren<T>(location.m_prefab.Asset)` in the order `ZNetView`, `RandomObject`, `RandomSpawn`, then calls `Prepare()` on every RandomSpawn. It **consumes** them in the order **RandomSpawn, then RandomObject**. Build order and consume order are different; only the consume order matters.
- **One draw each, unconditionally, in array order.** `RandomSpawn.Randomize` opens with `bool spawned = Random.Range(0f,100f) <= m_chanceToSpawn;`, before any gate. `RandomObject.Randomize` opens with `GetWeightedObject()`, which is one `Random.Range(0f, totalWeight)` - also before any gate, so **a RandomObject that is gated off still spends its draw**. Entry N of the RandomSpawn array consumes draw N; entry M of the RandomObject array consumes draw `randomSpawnCount + M`.
- `Utils.GetEnabledComponentsInChildren<T>(root)` = `root.GetComponentsInChildren<T>()` (active only), minus components on the root transform itself, minus anything whose `activeSelf` chain up to the root is broken (`Utils.IsEnabledInheirarcy`). Both helpers are in `assembly_utils`.
- The position each `Randomize` is given is `pos2 = pos + rot * child.transform.position`, read while `SpawnLocation` has temporarily set the asset root to `Vector3.zero` / `Quaternion.identity` (it leaves `localScale` alone) and restores it afterwards.
- `RandomSpawn` gates, in order: chance; dungeon theme (**never fires for a location** - `SpawnLocation` calls `Randomize(pos2, component)` and leaves `dg` null); biome; lava; elevation. `RandomObject` has the same minus the chance.
  - lava: `ZoneSystem.IsLavaPreHeightmap(Vector3 position, float lavaValue = 0.6f)` = `GetBiome(x,z) == AshLands && GetBiomeHeight(AshLands, x, z, out mask).mask.a > 0.6f`. Fully computable offline.
  - elevation: `pos2.y < (float)m_minElevation || pos2.y > (float)m_maxElevation`, the **int** fields cast to float; defaults -10000 / 10000. `pos2.y` is the `GetGroundData` raycast height plus the child's own y - **not** `LocationInstance.m_position.y`.
- **`Location.m_biome` is a session-wide cache on the SHARED PREFAB ASSET, not a per-instance value.** `SpawnLocation` passes `location.m_prefab.Asset.GetComponent<Location>()` to every `Randomize`. When a biome-gated entry finds `m_biome == None` it writes `WorldGenerator.instance.GetBiome(pos2)` into it and **nothing ever clears it**. The only writers of `Location::m_biome` anywhere in `assembly_valheim` are `RandomSpawn::Randomize`, `RandomObject::Randomize` and `MaterialVariationWorld::Update` (verified by IL scan; the last one operates on an *instantiated* Location found via `Location.GetZoneLocation`, not on the asset). So the first biome-gated entry of the first instance of that prefab to spawn in a session fixes the gate for every later instance anywhere in the world - and since the location's rotation is unseeded, which `pos2` gets sampled is not reproducible. On the 2026-09-22 asset dump 181 of 186 location prefabs read `m_biome == None`; the five that do not are `AncientUpgradeStation` (Mountain) and `NorthMemorialPlace` / `DN_hut01` / `DN_gammeltrollFrac01` / `DN_gammeltrollFrac02` (DeepNorth), none of which could have spawned near that dump's Meadows spawn point - which is the evidence that a non-zero value there means *authored*.
- **`RandomSpawn.Reset()` leaves `m_OffObject` inactive on the shared asset.** `SpawnLocation` ends by calling `Reset()` on every RandomSpawn and RandomObject, and `RandomSpawn.Reset()` is `SetSpawned(true)`, which does `m_OffObject.SetActive(false)`. Only ZNetView-bearing children get re-activated afterwards. So after the first location of a given prefab spawns in a session, `Utils.GetEnabledComponentsInChildren` on that asset can return a **shorter** array than the authored prefab has - i.e. a different draw budget. Anything reading these arrays out of a running game must compare against `GetComponentsInChildren<T>(includeInactive: true)` and say so.
- `RandomSpawn.SetSpawned(bool)`: not spawned -> the object and every `ZNetView` in `Prepare()`'s `m_childNetViews` are deactivated and `m_OffObject` is activated; spawned -> the object is activated **only when it has no `ZNetView` of its own** (`m_nview == null`), and `m_OffObject` is deactivated. `Prepare()`'s list is `GetComponentsInChildren<ZNetView>(true)` over the RandomSpawn's own subtree filtered by `Utils.IsEnabledInheirarcy(child, this.gameObject)`.
- `RandomObject.GetWeightedObject()`: total = sum of every `m_weight` **including entries whose `m_object` is null**; the winner is the first entry whose running sum is `>=` `Random.Range(0f, total)`, walking `m_objects` in list order. A null winner spawns nothing.
- **Container contents are NOT in this stream.** `Container.m_defaultItems` is a `DropTable`, rolled from the ambient unseeded stream: `Random.value > m_dropChance` -> nothing; otherwise `Random.Range(m_dropMin, m_dropMax + 1)` spins, each `Random.Range(0f, totalWeight)` walking the running sum with `draw <= sum`, removing the winner when `m_oneOfEach`. A drop table therefore says what a chest **can** contain, never what it **does**.
- **Neither rotation source is reproducible offline**, and the slope one is not the exception it looks like: `GetTerrainDelta` takes **ten `Random.insideUnitCircle` samples** from the ambient stream and reads `GetGroundHeight` at each (decompiled 2026-09-23), so its yaw depends on an unseeded draw sequence as well as on the terrain. Both are then snapped to 22.5 degrees, so either way the answer is one of 16 yaws.
- Both rotation sources in `PlaceLocations` are **yaw-only**: `Quaternion.Euler(0, k*22.5f, 0)` in the random case, and `Quaternion.LookRotation(forward)` with `forward.y == 0` and the default `Vector3.up` in the slope case. A rotation about Y leaves the y component of any vector untouched, so the unseeded rotation moves `pos2` in XZ (perturbing the biome and lava gates) but leaves `pos2.y` - and therefore the elevation gate - exact.

- After placing: `m_placed = true`. If `m_unique`, other candidates are removed. If `m_iconPlaced`, it calls `SendLocationIcons(0L)` to broadcast new icons.
- `Location` component (public): `m_exteriorRadius=20`, `m_noBuild=true`, `m_noBuildRadiusOverride`, `m_hasInterior`, `m_interiorRadius=20`, `m_discoverLabel`, enemy-level overrides. Public statics: `Location.GetLocation(point)`, `GetZoneLocation(point|zone)`, `IsInsideLocation`, `IsInsideNoBuildLocation`, `IsInsideActiveBossDungeon`. Apart from `IsInsideActiveBossDungeon` (which only compares the point's zone with the zone of `EnemyHud`'s active boss), these only see **instantiated** locations (static `s_allLocations`, filled in Awake), which means loaded zones only, on any peer.
- Interiors: `Location.Awake` spawns the interior environment volume at `y + 5000`. `Character.InInterior(pos)` is simply `pos.y > 3000f` (Character.InInterior / decompiled).
- Public queries (**server only**, because clients have an empty `m_locationInstances`):
  - `FindClosestLocation(name, point, out inst)` uses 3-D `Vector3.Distance`
  - `FindLocations(name, ref list)`
  - `GetLocationList()`
  - `ZoneHasLocation(zone|pos)`
  - `CanSpawnLocationMidGame(pos)`

  All of these include **unplaced** candidates.

---

### 4.1 The custom interior transform moves children BEFORE the Randomize loop
(SpawnLocation / decompiled 2026-09-23.) When the root `Location` satisfies all three of
`m_useCustomInteriorTransform && m_interiorTransform != null && m_generator != null` — the method's
local `flag` — `SpawnLocation` mutates the **shared prefab asset** between `Random.InitState(seed)` and
the Randomize loop:

```csharp
component.m_generator.transform.localPosition = Vector3.zero;
Vector3 v = GetZonePos(GetZone(pos)) + interiorLocalPos + generatorLocalPos - pos;
Vector3 p = (Matrix4x4.Rotate(Quaternion.Inverse(rot)) * Matrix4x4.Translate(v)).GetColumn(3);
p.y = component.m_interiorTransform.localPosition.y;
component.m_interiorTransform.localPosition = p;
component.m_interiorTransform.localRotation = Quaternion.Inverse(rot);
```

(All three are restored at the end of the method, so the asset is left as it was found.)

Consequences for anything reading positions off the asset: for a `RandomSpawn` / `RandomObject` under
`m_generator`'s transform, the position the game reads is shifted by the generator's authored offset;
for one under `m_interiorTransform`, it is **instance-dependent** — it depends on the zone, the
instance position and the instance rotation — and cannot be stated from the asset at all. The draw
ORDER and COUNT are unaffected: the mutation moves transforms, not the stream. `SpawnLocation` also
warns when `Location.m_useCustomInteriorTransform != DungeonGenerator.m_useCustomInteriorTransform`.

---

## 5. Discovery, map icons, runestones, Vegvisir

### 5.1 Location icons (always-visible map symbols such as the start temple and the trader)
- Server: `GetLocationIcons(dict)` adds every instance with `m_iconAlways`, or with `m_iconPlaced && m_placed`, as `position → prefab name`.
- Clients: the server sends the icon list over RPC `"LocationIcons"` when a peer joins (`OnNewPeer`) and whenever an `m_iconPlaced` location gets placed. The client stores it in the private `m_locationIcons` and returns it from the same `GetLocationIcons`. `GetLocationIcon(name, out pos)` works on both sides.
- `Minimap.UpdateLocationPins` (private, every 5 s):
  - It diffs against the private `Minimap.m_locationPins` (`Dictionary<Vector3, PinData>`).
  - It adds a pin `AddPin(pos, PinType.None, "", save:false, ...)` with `m_icon` taken from the **public** `Minimap.m_locationIcons` (`List<LocationSpriteData>`, which pairs m_name with m_icon) and sets `m_doubleSize = true`.
  - A location without a sprite entry gets no pin.
  - These pins are **not saved**; they are rebuilt from ZoneSystem.
- The default player spawn point (used when there is no logout point to resume at and no bed spawn point) is `GetLocationIcon("StartTemple")` + 2 m up (Game.FindSpawnPoint).

### 5.2 DiscoverClosestLocation flow (Game / Minimap / RuneStone / Vegvisir, decompiled)
1. `RuneStone.Interact`: if `m_locationName` is set, it calls `Game.instance.DiscoverClosestLocation(m_locationName, stonePos, m_pinName, (int)m_pinType /*default Boss*/, m_showMap)`.
   `Vegvisir.Interact`: calls the same for each `VegvisrLocation {m_locationName, m_pinName, m_pinType, m_discoverAll, m_showMap=true}`, then optionally `SetGlobalKey(m_setsGlobalKey)` and `player.AddUniqueKey(m_setsPlayerKey)`.
2. `Game.DiscoverClosestLocation` (public) sends the routed RPC `"RPC_DiscoverClosestLocation"` to the **server**. The handler is registered only when `IsServer`.
3. The server calls `FindClosestLocation`, or `FindLocations` if `discoverAll`, over **all registered instances, placed or not**. It replies to the sender with `"RPC_DiscoverLocationResponse"(pinName, pinType, pos, showMap)`, once per instance when `discoverAll` is set.
4. The client calls `Minimap.DiscoverLocation(pos, type, name, showMap)` (public). If `HaveSimilarPin` finds a pin with the same name, type and save flag within 1 m XZ, it returns false without adding a pin; only when `showMap` is set does it also show "$msg_pin_exist" and open the map on the point. Otherwise it calls `AddPin(pos, type, name, save:true, isChecked:false, 0)` and shows "$msg_pin_added: name". `showMap` opens the map on the point (`ShowPointOnMap` → `MapMode.Large`). Afterwards `Game.RPC_DiscoverLocationResponse` turns the player toward the point (`SetLookDir`, 3.5) only if `Minimap.m_mode == MapMode.None`. In normal play the minimap sits in `Small` mode, so this happens only in a no-map world (`SetMapMode` forces `None` when `Game.m_noMap`) or while the player is dead.
- The random runestone text is seeded with `(int)pos.x * (int)pos.z` of the stone (RuneStone.GetRandomText).
- `Location.m_discoverLabel` is added to the player's known location names when standing inside the location (Player.UpdateBiome → AddKnownLocationName).

---

### 5.3 What a location's chests hold is NOT seed-determined

The seed fixes where a location is; `DropTable.GetDropListItems` fills its containers from the
**global, unseeded `UnityEngine.Random`** when the container spawns (see
`valheim-modding\references\vanilla-behaviour.md` §11 for the draw loop and the arithmetic). Ask "which
location types can hold item X, and where are they", never "which chest holds X".

**The axe heads**, because they are the case that comes up: exactly two location prefabs can yield one
in 1.0.15, and nothing in any dungeon room can.

| location prefab | item | localized name | instances (`bmbp74`) | its chest | P(axe head) |
|---|---|---|---|---|---|
| `WoodHouse6` | `AxeHead1` | Curious Axe Head | 20 | a plain child, always present | **31/56 = 55.4 %** |
| `WoodHouse2` | `AxeHead2` | Mysterious Axe Head | 20 | `RandomSpawn` 50, `m_chanceToSpawn 50` | **31/112 = 27.7 %** |

**Two different uncertainties, and only one of them is unknowable.** Whether `WoodHouse2`'s chest
exists is a `RandomSpawn` draw out of the zone-seeded stream (section 4), so it is seed-determined and
an offline tool can compute it. What any chest *contains* is an unseeded `UnityEngine.Random` draw at
container spawn and never can be. Do not multiply the two into a single "chance of an axe head"
without saying which half is which. "The nearest axe-head house" is a lower bound on the walk in the
same sense as the distance to Haldor. Evidence: `data\1.0.15-59f53fb5\locationchildren.json`,
`roomchildren.json` (no hits), decompiled `DropTable` and `RandomSpawn`.

**How far they are from spawn** (SeedLab, measured 2026-09-24 on two independent 512-seed samples,
shuffled over the whole int32 range, no seed in common; distance from the `StartTemple` point, not the
world centre). All 1,024 seeds place all 40 houses. Nearest of either kind: p25 117 / 124 m, median
189 / 194 m, p90 364 m, range 37-1,080 m; within 150 m in 37.5 % / 37.9 % of seeds. By kind (second
sample): `WoodHouse6` p25 173, median 273, p90 492 m; `WoodHouse2` p25 182, median 269, p90 579 m; the
nearest house is a `WoodHouse6` in 270 of 512 seeds. Houses of either kind within 500 m of spawn: 0 to
12, median 4. This is the calibration behind SeedLab's `axe-heads` preset.

---

## 6. Vegetation / resources (ZoneSystem.PlaceVegetation / decompiled)
- Runs during zone generation (Full or Ghost, server only), right after `PlaceLocations`. It saves `Random.state` on entry and restores it on exit.
- For each `ZoneVegetation veg` in `m_vegetation`, in list order, skipping entries with `!m_enable` or where `!hmap.HaveBiome(veg.m_biome)` (none of the zone heightmap's 4 corner biomes match):
```csharp
UnityEngine.Random.InitState(seed + zoneID.x * 4271 + zoneID.y * 9187 + veg.m_prefab.name.GetStableHashCode());
int count = 1;
if (veg.m_max < 1f) { if (Random.value > veg.m_max) continue; }   // m_max<1 = chance of ONE
else count = Random.Range((int)veg.m_min, (int)veg.m_max + 1);
int tries = veg.m_forcePlacement ? count * 50 : count;
```
- Candidate: x,z uniform in zone centre ± `(32 - m_groupRadius)`. Group size is `Range(groupSizeMin, groupSizeMax+1)`, and group members sit at `GetRandomPointInRadius(center, groupRadius)`. For every object it draws Y-rotation (`Random.Range(0, 360)`, the int overload, so whole degrees 0–359), scale and tilt (`±m_randTilt`) **before** testing.
- Filters, in order:
  - alt-biome parent/block, using `m_blockVegetationNames`
  - `m_blockCheck && IsBlocked(p)`: a raycast from `y+2000` over 10000 m against Default/static_solid/Default_small/piece
  - biome and biomeArea of the terrain hit (`GetGroundData`)
  - optional `m_snapToStaticSolid`
  - altitude `y-30` within [min, max]
  - vegetation mask (only if min ≠ max)
  - ocean depth (only if min ≠ max)
  - tilt via normal.y against `cos(maxTilt)..cos(minTilt)`
  - terrain delta (if `m_terrainDeltaRadius > 0`)
  - distance from centre
  - forest factor
  - surround check (its weight formula is `(1 - r)/(2*dist)`, which differs from the location version)
  - outside every ClearArea
- Placement: `m_snapToWater` sets `y = 30`, then `y += m_groundOffset`. With chance `m_chanceToUseGroundTilt` the object aligns to the ground normal. Prefabs with a `ZNetView` get their scale via `ZNetView.SetLocalScale`. The count increments once per group that placed at least one object, and the loop stops at `count`.
- **Deterministic?** The RNG stream is fully seed-derived per (zone, prefab name), and each entry's stream is independent of the other entries. However, acceptance uses **physics raycasts on whatever colliders exist at that moment**: this zone's location objects, earlier vegetation, and colliders from neighbouring zones or player builds near the edges. `ZoneSystem.GetTerrainDelta` samples `GetGroundHeight`, which returns the input y where no terrain collider is loaded. So treat vegetation as *seed-driven but not guaranteed bit-identical*.
  **Unverified:** whether objects instantiated earlier in the same frame are visible to `Physics.Raycast`. That depends on Unity auto-sync-transforms settings.
- Once generated, every resource is a ZDO in the save and is never re-rolled. A changed vegetation list only affects zones that have not been generated yet.
- Console `vegetation <prefab>` spawns one entry in front of the player without any seeding.

---

## 7. Dungeon interiors (DungeonGenerator / decompiled)
- `Generate(mode)` → `GetSeed()`:
```csharp
if (m_forceSeed != int.MinValue) { m_generatedSeed = m_forceSeed; m_forceSeed = int.MinValue; }  // console "nextseed N"
else {
  int seed = WorldGenerator.instance.GetSeed();
  Vector3 position = transform.position;                 // generator's WORLD position after placement
  Vector2i z = ZoneSystem.GetZone(position).ToVector2i();
  m_generatedSeed = seed + z.x * 4271 + z.y * -7187 + (int)position.x * -4271 + (int)position.y * 9187 + (int)position.z * -2134;
}
```
- Then `Random.InitState(seed)` → `GenerateRooms`, using one of the `Algorithm` values Dungeon, CampGrid or CampRadial. The ambient state is restored afterwards.
- The generator's world position is `locationPos + locationRot * generatorLocalPos`, and y includes the terrain height at the location. If the generator has a non-zero XZ offset, the unseeded location rotation (section 4) changes the dungeon seed. That is why `Location.m_useCustomInteriorTransform` and `DungeonGenerator.m_useCustomInteriorTransform` exist: their tooltip says they are needed "to make sure seeds are deterministic". With them, the generator is moved to local zero.
  **Settled 2026-09-22 (was Unverified): 12 of the 21 location prefabs that carry a `DungeonGenerator` have a non-zero XZ offset**, measured by walking every location prefab in the running game (`locationprefabs.json` in `data\1.0.15-59f53fb5\` (dumped from the running game 2026-09-22 by `tools\SeedLab.Dumper`; `vseed data --verify` re-checks it)). They are `Crypt2`, `Crypt3`, `Crypt4` (localPosition z = -1), `SunkenCrypt4` (x = -0.6780014), `MountainCave02`, `Mistlands_DvergrTownEntrance1/2`, `Hildir_cave`, `Hildir_crypt`, `MorkBorg` (z = -30), `Mistlands_DvergrBossEntrance1` (z = -24) and `TheHole01` (z = -18.5). For those twelve the unseeded location rotation feeds the dungeon seed; the other nine sit at local zero.
- Per room: `roomSeed = (int)v.x*4271 + (int)v.y*9187 + (int)v.z*2134`, where `v` = the room's **placement** position (the `pos` argument of `PlaceRoom`), or `pos - generator.transform.position` when `m_useCustomInteriorTransform`. It adds `GetSeed()` **only if `m_addBaseSeedToRandomSpawn`**. The seed drives the room's RandomSpawn/RandomObject and is stored in `Room.m_seed` (DungeonGenerator.PlaceRoom). Note `+2134` here against `-2134` in `GetSeed()` above — different constants in adjacent methods.

### 7.1 Where the rooms come from (DungeonDB / decompiled 2026-09-23)
**A location prefab never contains a room.** A location with an interior holds a `DungeonGenerator` and
nothing else; the contents are room prefabs out of `DungeonDB`, instantiated at generation time. Any
"which prefab contains X" search that walks only location prefabs will answer with silence for every
interior piece.

- `DungeonDB.Start()` → `SetupRooms()` → `Instantiate`s every prefab in the public
  `m_roomLists` (`List<GameObject>`), then concatenates each `RoomList.m_rooms` from
  `RoomList.GetAllRoomLists()` into the **private** `m_rooms` (`List<DungeonDB.RoomData>`). The public
  static `DungeonDB.GetRooms()` returns that list — but it dereferences the private static
  `m_instance` **with no null check**, so test `DungeonDB.instance != null` first. `m_roomScenes`
  (`List<string>`) is empty as shipped in 1.0.15.
- `GenerateHashList()` keys them by `RoomData.Hash` = `m_prefab.Name.GetStableHashCode()` into the
  private `m_roomByHash`, **logs an error and DROPS any later duplicate**. `GetRoom(hash)` reads that
  dictionary, so a dropped duplicate is unreachable by hash — while `SetupAvailableRooms`, which walks
  the list, would still pick it.
- `LoadRooms()` `Load()`s and holds every **enabled** room's prefab, but only when
  `Settings.AssetMemoryUsagePolicy` has `KeepAsynchronousLoadedBit`.
- `DungeonDB.RoomData` (a `[Serializable]` class): `m_prefab` (SoftReference), `m_enabled`, `m_theme`,
  the `Hash` property, and `RoomInPrefab` — **a property with a side effect**: it caches
  `m_prefab.Asset.GetComponent<Room>()` into the private `m_loadedRoom` and `Debug.LogError`s when the
  asset is not resident. Read-only tooling should call `GetComponent<Room>()` on an asset it loaded
  itself instead.
- **`DungeonGenerator.SetupAvailableRooms` filters on the RoomData, not on the `Room` component:**
  `if ((room.m_theme & m_themes) != Room.Theme.None && room.m_enabled)`. The component carries its own
  `m_theme` / `m_enabled` and they are normally equal, but the RoomData pair is the one that decides
  which dungeon can contain which room.
- `Room` (MonoBehaviour) serializes 13 fields: `m_size` (Vector3Int), `m_theme`, `m_enabled`,
  `m_entrance`, `m_endCap`, `m_divider`, `m_endCapPrio`, `m_minPlaceOrder`, `m_weight`, `m_faceCenter`,
  `m_perimeter`, `m_placeOrder`, `m_seed`. **`m_placeOrder` and `m_seed` on the ASSET are authored
  defaults** — `PlaceRoom` writes the real values on the instance it creates, never on the asset.
  `Room.Theme` is a bitmask: Crypt 1, SunkenCrypt 2, Cave 4, ForestCrypt 8, GoblinCamp 16,
  MeadowsVillage 32, MeadowsFarm 64, DvergerTown 128, DvergerBoss 256, ForestCryptHildir 512,
  CaveHildir 1024, PlainsFortHildir 2048, AshlandRuins 4096, FortressRuins 8192, Hole 16384,
  NorthVillage 65536, MorkHalla 131072.

### 7.2 A room's own RandomSpawn stream (DungeonGenerator.PlaceRoom / decompiled 2026-09-23)
The 5-argument `PlaceRoom(RoomData, Vector3 pos, Quaternion rot, RoomConnection, SpawnMode)` treats a
room's children exactly as `SpawnLocation` treats a location's — same three
`Utils.GetEnabledComponentsInChildren` arrays in the order ZNetView, RandomObject, RandomSpawn, same
`Prepare()` loop, same one-draw-per-entry budget, RandomSpawns first — with three differences:

1. **It saves and restores the ambient stream around its own seeded run** (`Random.State state =
   Random.state; Random.InitState(roomSeed); ...; Random.state = state;`), so a dungeon's rooms do not
   perturb anything generated after them.
2. **The anchor is the `Room` component's transform**, not the asset root:
   `Inverse(room.transform.rotation) * (child.position - room.transform.position)`, then
   `pos + rot * that`. `SpawnLocation` instead zeroes the asset root and reads `child.position` back.
3. **The gates swap over.** `PlaceRoom` calls `Randomize(pos, null, this)` while `SpawnLocation` calls
   `Randomize(pos2, locationComponent)` with the generator defaulted to null. In
   `RandomSpawn.Randomize` the biome gate is `loc != null && m_requireBiome != None` and the theme gate
   is `dg != null && m_dungeonRequireTheme != None` — so **`m_requireBiome` is inert inside a room and
   `m_dungeonRequireTheme` is inert inside a location**, whatever the authored values say.

`PlaceRoom` also sets the instance's `gameObject.name` to `roomData.m_prefab.Name`, which is what
`CheckRequiredRooms` matches `DungeonGenerator.m_requiredRooms` against.
- **Persistence:** `Save()` writes `{count, [roomHash, position, rotation]...}` into the generator's ZDO as `ZDOVars.s_roomData` (a byte array) and cleans up the old per-key format (`rooms`, `room{i}`, `_pos`, `_rot`, `_seed`). `Awake → Load()` rebuilds the rooms from the ZDO in Client mode, after async prefab loading. **A dungeon is generated once, and the saved layout is authoritative after that.**
- The console `nextseed` command sets `DungeonGenerator.m_forceSeed` and `m_didZoneTest = true`, which disables saving.

### 7.3 Which of the three algorithms a dungeon uses (DungeonGenerator.GenerateRooms / decompiled 2026-09-23)
`GenerateRooms` switches on `m_algorithm` and calls one of `GenerateDungeon` (`Algorithm.Dungeon`, 0),
`GenerateCampGrid` (1) or `GenerateCampRadial` (2).

**No shipped 1.0.15 location uses CampGrid.** Of the 21 location prefabs that carry a
`DungeonGenerator`, **11 are Dungeon and 10 are CampRadial** (`generators[].algorithm` in
`data\1.0.15-59f53fb5\locationprefabs.json`). An offline reproduction of vanilla
therefore needs two algorithms, not three; `GenerateCampGrid` is live code that only a mod's own
generator would reach.

### 7.4 What GenerateCampRadial reads — the complete list (DungeonGenerator.GenerateCampRadial / decompiled 2026-09-23)
| input | how it is used |
|---|---|
| `m_campRadiusMin` / `m_campRadiusMax` | one `Random.Range` for the camp radius |
| `m_minRooms` / `m_maxRooms` | one `Random.Range` for the room target |
| `m_perimeterBuffer` | every candidate's distance from the centre is `Random.Range(0f, radius - m_perimeterBuffer)` |
| `m_maxTilt` | rejects a candidate when `normal.y < Mathf.Cos(PI/180 * m_maxTilt)` |
| `m_minAltitude` | rejects when `p.y - 30f < m_minAltitude` (the usual 30 m water level) |
| `m_perimeterSections` | when > 0, `PlaceWall` runs after the main loop |
| the rooms' `m_weight` | the weighted pick among the available rooms |

Nothing else is read, and the candidate budget is **20 x the room target**.

**The shipped values of four of these are not in the data yet.** `m_maxTilt`, `m_minAltitude`,
`m_perimeterBuffer` and `m_perimeterSections` are among the fields the 2026-09-22 dump
(`locationprefabs.json`) never captured — see the field-coverage pitfall in
`valheim-modding\references\pitfalls.md`. The dumper was extended on 2026-09-23 to capture them but has
not been re-run, so **do not substitute the C# field initialisers quoted in that pitfall for the
authored prefab values**; that is exactly the mistake the pitfall records.

### 7.5 Whether a room fits: TestCollision and IsInsideDungeon (DungeonGenerator / decompiled 2026-09-23)
- `TestCollision(Room room, Vector3 pos, Quaternion rot)` uses **only `Room.m_size`**. It sets two
  `BoxCollider`s — one to `m_size - 0.1`, one to `m_size` — and calls `Physics.ComputePenetration`. No
  mesh, and no collider authored on the room prefab, takes any part. So an offline reproduction of room
  placement needs `m_size` and nothing else about a room's geometry.
- It calls `IsInsideDungeon` first, which tests **all eight corners** of that box against
  `new Bounds(m_zoneCenter, m_zoneSize)`. `m_zoneCenter` / `m_zoneSize` therefore gate the **camp**
  algorithms too, not only `GenerateDungeon`.

### 7.6 Room connections (Room.GetConnections / RoomConnection / DungeonGenerator.CalculateRoomPosRot, decompiled 2026-09-23)
- `Room.GetConnections()` is `GetComponentsInChildren<RoomConnection>(includeInactive: false)`
  **cached into the component's private `m_roomConnections`** — and that component lives on the shared
  prefab asset, so the first caller in a session freezes the answer for everyone. Same shared-asset trap
  as `DungeonDB.RoomData.RoomInPrefab` (7.1) and the `Location` asset mutations (4.1): read-only tooling
  must walk the children itself, with and without inactive, and compare the two counts.
- `RoomConnection` serializes `m_type`, `m_entrance`, `m_allowDoor` and
  `m_doorOnlyIfOtherAlsoAllowsDoor`. **`m_placeOrder` is `[NonSerialized]`** and written on the placed
  instance, so it is never authored data and cannot be read off the asset.
- `CalculateRoomPosRot` reads the connection's **parent-relative** `transform.localPosition` /
  `localRotation` — not its transform relative to the `Room`. `PlaceRoom` logs a warning when the
  connection is not a direct child of the room, and then uses the parent-relative transform anyway. So a
  dump has to record both the parent-relative transform and whether the connection is a direct child;
  the room-relative transform alone is the wrong frame for the rooms that trip the warning.
- **Index parity has a boundary.** The array's order is what `GetConnection`, `HaveConnection` and
  `GetEntrance` see, because all three run on the ASSET. It is not guaranteed for
  `m_openConnections`: `AddOpenConnections` walks the **clone's** connections, after `PlaceRoom` has
  deactivated every `ZNetView`-bearing object on the asset and after `Randomize` has deactivated the
  subtrees of every RandomSpawn/RandomObject that did not spawn. A connection parented under one of
  those is absent from that walk.

### 7.7 m_originalPosition is overwritten for a custom-interior location (ZoneSystem.SpawnLocation, decompiled 2026-09-23)
`DungeonGenerator.m_originalPosition` as authored on the prefab is **not** the value `Generate` reads,
for the locations that have an interior transform:

```csharp
Vector3 vector2 = component.m_generator.transform.localPosition;              // SpawnLocation 2413
bool flag = component && component.m_useCustomInteriorTransform
            && component.m_interiorTransform && component.m_generator;        //              2420
if (flag) { component2.m_originalPosition = vector2; }                        //              2482
component2.Generate(mode);                                                    //              2484
// DungeonGenerator.Generate 215:
m_zoneCenter.y = base.transform.position.y - m_originalPosition.y;
```

- It applies to the **18** location prefabs with `Location.m_useCustomInteriorTransform` in 1.0.15.
- The value stored is the generator's **parent-relative** `localPosition`, which is a different
  quantity from `root.InverseTransformPoint(generator.position)` — **9 of the 21 shipped generators
  hang under an `Interior/` parent**, so the two disagree in general.
- Getting it wrong does not throw: `m_zoneCenter.y` is off, and `IsInsideDungeon`'s eight corner tests
  (7.5) then accept or reject the wrong candidate rooms. A wrong layout, silently.

---

## 8. Ground / solid height helpers (ZoneSystem / decompiled)
Layer masks (ZoneSystem.Awake):
- `m_terrainRayMask` = terrain
- `m_blockRayMask` = Default, static_solid, Default_small, piece
- `m_solidRayMask` = Default, static_solid, Default_small, piece, terrain
- `m_staticSolidRayMask` = static_solid, terrain

| Method (all public) | Ray origin | Length | Mask | On miss |
|---|---|---|---|---|
| `float GetGroundHeight(p)` | (x, **6000**, z) | 10000 | terrain | returns `p.y` |
| `bool GetGroundHeight(p, out h)` | (x, 6000, z) | 10000 | terrain | false, h=0 |
| `float GetSolidHeight(p)` | p.y **+ 1000** | **2000** | solid | returns `p.y`; does NOT reject rigidbodies |
| `bool GetSolidHeight(p, out h, int heightMargin = 1000)` | **p.y + heightMargin** | **2000** | solid | false, h=0; also false if the first hit has `attachedRigidbody` (no retry) |
| `bool GetSolidHeight(p, radius, out h, Transform ignore)` | p.y+1000 (SphereCast if radius>0) | 2000 | solid | h preset to p.y-1000 |
| `bool GetSolidHeight(p, out h, out normal, out go)` | p.y+1000 | 2000 | solid | rejects rigidbody |
| `bool GetStaticSolidHeight(p, out h, out normal)` | p.y+1000 | 2000 | static_solid+terrain | |
| `bool FindFloor(p, out h)` | p.y+1 | 1000 | solid | |
| `void GetGroundData(ref p, out normal, out biome, out biomeArea, out hmap)` | p+5000 up | 10000 | terrain | normal=up, biome None |
| `bool IsBlocked(p)` | p.y+2000 | 10000 | block (no terrain) | |

- **Confirmed:** the solid ray starts at *the y you pass + margin* and covers only [y+margin-2000, y+margin]. Pass a sensible y, such as the ground height or player y. Passing y=0 still covers -1000..+1000, which is fine for surface terrain.
- `GetGroundHeight` ignores p.y and always returns the **top terrain surface**. Inside a dungeon (y > 3000) it returns the surface terrain height below, not the dungeon floor.
- All of these need **loaded colliders**, i.e. a loaded zone. For unloaded areas, use `WorldGenerator.instance.GetHeight(x, z)` (public; procedural height from the seed). **Unverified:** that it matches the built heightmap exactly. It never includes player terrain edits.
- Quirk: in the radius overload, the collider/ignore test reads the unsorted `rayHits[i]` while the height reads the sorted `s_rayHitsHeight[i]`, so the index pairing can mismatch.

---

## 9. Global keys (ZoneSystem / decompiled)
- Storage:
  - `HashSet<string> m_globalKeys` (private) holds full lines such as `"key value"`.
  - `Dictionary<string,string> m_globalKeysValues` (public) maps key to value.
  - `HashSet<GlobalKeys> m_globalKeysEnums` (public) holds the enum keys.
- `GlobalKeyAdd` lowercases the **whole** string before splitting at the first space, so values are lowercased too.
- The `GlobalKeys` enum has indices 0..40 for **server options** (PlayerDamage … NoHeavySnow, AllHeavySnow, Preset), then `NonServerOption` (41), then `defeated_eikthyr, defeated_dragon, defeated_goblinking, defeated_gdking, defeated_bonemass, activeBosses, StoneCircle, KilledTroll, killed_surtling, KilledBat, AshlandsOcean, Count`. Any unknown string is treated as `NonServerOption`.
- Persistence:
  - Keys with `gk < NonServerOption` (world modifiers) are stored in `World.m_startingGlobalKeys` and stripped from `.db2`. That list is written to the `.fwl2` metadata along with `m_name`, `m_seedName`, `m_seed`, `m_uid` and `m_worldGenVersion` (World.SaveWorldFWLData).
  - All other keys (boss kills, custom strings) are saved in `.db2` (ZoneSystem.Save).
- Networking:
  - `SetGlobalKey(string|GlobalKeys|GlobalKeys,float)` and `RemoveGlobalKey` send routed RPCs to the **server** (`InvokeRoutedRPC(name, ...)` targets `GetServerPeerID()`).
  - The server handlers are registered only when `IsServer`. They mutate the keys and broadcast the full list with `SendGlobalKeys(0L)` → client RPC `"GlobalKeys"`, which clears the client's keys and re-adds all of them.
  - Keys are also sent to each new peer.
- Reading: `GetGlobalKey(string name)` looks up `m_globalKeysValues[name.ToLower()]`, so the name is case-insensitive and matches only the key token. `GetGlobalKey(GlobalKeys, out float)` parses the value with the invariant culture. `GetGlobalKeyExact(fullLine)` and `GetGlobalKeys()` (returns a copy) are also public. Clients read their synced copy.
- Every add or remove calls `UpdateWorldRates()` → `Game.UpdateWorldRates(...)`.

---

## 10. Modder cheat-sheet
- **Client or server:**
  - Location instances, generation, `FindClosestLocation` and similar are **server-only**. A client of a dedicated server has an empty `m_locationInstances`. From a client, use `GetLocationIcons` (icon locations only), or ask the server through your own RPC / `Game.DiscoverClosestLocation`.
  - Zone generation, vegetation and dungeon generation run on the server (or the host).
- **Private members** needing reflection: `m_generatedZones`, `IsZoneGenerated`, `SetZoneGenerated`, `m_locationIcons` (ZoneSystem), `m_locationsByHash`, `GetLocation(string|int)`, `SpawnZone`, `PlaceLocations`, `PlaceVegetation`, `RegisterLocation`, `Minimap.m_locationPins`, `Minimap.UpdateLocationPins`.
- **Public:** `m_locationInstances`, `m_locations`, `m_vegetation`, `LocationsGenerated`, `GenerateLocationsCompleted` event, `SpawnLocationMidGame`, `GetLocationIcons`, `Minimap.DiscoverLocation`, `Game.DiscoverClosestLocation`, `Minimap.m_locationIcons`.
- Harmony hook points:
  - `ZoneSystem.SetupLocations` (postfix) to add or modify locations and vegetation before generation.
  - `ZoneSystem.SpawnZone` / `PlaceVegetation` for per-zone changes.
  - `Minimap.DiscoverLocation` to react to runestone or Vegvisir reveals.
- To predict locations offline you need all of the following: the seed, the exact location list and order with every parameter, the world-gen code (`WorldGenerator` + `AltBiomeWorldData`), and an exact reimplementation of Unity's native `Random`. **Settled 2026-09-23 (was "Unverified: that it is Xorshift128"):** it *is* xorshift128 — `InitState(s)` seeds `s0 = s`, `s1..s3 = prev*1812433253 + 1`, the step uses shifts 11/8/19, and the range mappings are recorded in `world-generator.md` 9, replayed against the running game on 268 seeds and 1 980 draws. All of it is implemented in SeedLab (this repository, skill **seedlab**), which reproduces whole location tables bit-exactly (3.7). Even so, unique-location winners and rotations remain unpredictable, for the reasons in 3.5 and 4.
- `Utils.GetSaveDataPath(src)` (assembly_utils) returns `""` when cloud storage is supported and enabled and `src` is Auto/Cloud. Otherwise it returns `m_saveDataOverride` if set, else `persistantDataPath`.
  `persistantDataPath` is initialised from `Application.persistentDataPath` (Utils static field / decompiled). `valheim_Data/app.info` names the company `IronGate` and the product `Valheim`, so on Windows it is `%USERPROFILE%/AppData/LocalLow/IronGate/Valheim`. That folder exists on this machine and contains `worlds`, `worlds_local` and `characters`.
