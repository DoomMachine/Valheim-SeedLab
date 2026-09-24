# Porting spec 04 — Game asset data: what to capture, when it exists, and how to get it out

> Target build: **Valheim 1.0.15**, network 40, Steam build 25390630,
> `assembly_valheim.dll` sha256 `59f53fb55d99d22a33e8ed094eec8d21e9f133543bce92bc3d80dce44033adb1`
> (`check-game-version.ps1`, exit 0 on 2026-09-22 — the knowledge base matches the installed build).
> Unity 6000.0.75f1, BepInEx 5.4.23.3, HarmonyX 2.9.0.0.
>
> Citations are `Type.Member` from the decompiled sources in `scratchpad\decomp\`, or a file plus an
> offset for things read out of shipped data. Anything not settled by evidence is marked
> **Unverified:** with what would settle it. Probe scripts are listed in Appendix A.

---

## 0. Executive summary

1. **The tool needs ~50 serialized fields that do not exist in the IL.** All of them come from four
   places: the `ZoneSystem` MonoBehaviour in the `main` scene, the six `LocationList` prefabs it
   instantiates, the one `AltBiomeList` prefab, and the `Minimap` / zone-`Heightmap` prefabs. §1 is the
   complete inventory with a reason per field.

2. **The main menu is not enough.** `ZoneSystem` does not exist in `start.unity` — the whole location
   table lives in `main.unity` (verified: the string `ZoneSystem` appears 0 times in the decompressed
   `start.unity` bundle and once in `main.unity`, which also carries the `ZoneLocation` type tree and
   the `StartTemple`/`Eikthyrnir` entries). A world must be loaded, **as host**. A client of a dedicated
   server has `m_locations` populated (it is prefab data, filled in `ZoneSystem.Start`) but nothing
   else — no `m_locationInstances`, no `World.m_biomeData`. §2.

3. **Two dumper modes, hard-separated.** Mode A (asset tables) is pure read-only reflection and is
   safe anywhere. Mode B (ground truth for many seeds) rewrites `WorldGenerator`'s static instance and
   mutates `AltBiomeList.m_altBiomes`; it must be physically impossible to run it in a session with
   peers. §3.2, §3.7.

4. **`AltBiome.Sectors` is never cleared** (`AltBiomeWorldData.GenerateAltBiomes` resets only
   `ValidPlacementSectors` / `ValidPlacementSectorCombos`). Generating alt biomes twice in one process
   silently starves the second world. This is the single biggest trap in multi-seed dumping and the
   dumper must clear it explicitly. §3.7.

5. **Offline extraction without launching the game is FEASIBLE**, and was demonstrated far enough to
   be trusted: the bundles are `UnityFS` (Unity 6000.0.75f1, LZ4HC), **type trees are present**, the
   `ZoneLocation` field layout is readable, and a hand-decode of the `StartTemple` entry produced
   sensible values that match the game's behaviour. The SoftRef text manifest maps `AssetID` →
   `Assets/world/Locations/.../<Name>.prefab`, and `SoftReference<T>.Name` is exactly that filename —
   which is the RNG stream seed. §4 gives the verdict, the work items and the residual risks.

6. **213 location prefabs ship**; **183** of them are enabled `ZoneLocation` entries with non-zero
   quantity (the game's own log line, this machine, today); **176** distinct types actually registered
   at least one instance in the user's world `asdasdasd`. Full list with evidence in §5.

7. **A ready-made, exact ground-truth corpus already exists on this machine and costs nothing:** the
   minimap cache of world `asdasdasd` is 2048×2048 float16 samples of `GetBiomeHeight` for seed
   −1772362158, and the world's `.db2` holds **12 314** location instances for the same seed. §3.6.4.

---

## 1. The inventory — every serialized value the offline tool needs

Field lists are the decompiled declarations; defaults shown are the **code** defaults, which the
prefab almost always overrides (proven: `Minimap.m_textureSize` is `256` in code and **2048** in the
shipped prefab). Never ship a code default as if it were the game's value.

### 1.1 `ZoneSystem.ZoneLocation` — all 40 serialized fields

Declaration: `ZoneSystem.cs:148-262`. Order below is declaration order, which is also the Unity
serialization order (§4.3). "Read by" names the step in the placement routine
(`ZoneSystem.GenerateLocationsTimeSliced(ZoneLocation, ...)`, `ZoneSystem.cs:1877-2141`) — see spec 02
for the algorithm itself.

| # | Field | Type / code default | Read by — why the tool needs it |
|---|---|---|---|
| 1 | `m_name` | string | **Not a label — it is read by a placement filter.** The alt-biome block test is `biomeSector.AltBiomes.Any(x => x.m_blockLocationNames.Contains(location.m_name))` (`ZoneSystem.cs:2039`), i.e. it matches `m_name`, **not** `m_prefabName`. The vegetation equivalent at `ZoneSystem.cs:1417` matches `veg.m_name` the same way. Dump it verbatim, byte for byte. It is still not the RNG key. |
| 2 | `m_enable` | bool `true` | Outer filter: `!m_enable` removes the entry before ordering. |
| 3 | `m_prefabName` | string, `[HideInInspector]` | **Overwritten at runtime** in `ZoneSystem.SetupLocations` from `m_prefab.Name`. It is what the save hashes (`ZoneSystem.Save` writes `m_location.m_prefabName.GetStableHashCode()`), so it is the key for cross-checking against a real `.db2`. Dump the runtime value, not the serialized one. |
| 4 | `m_prefab` | `SoftReference<GameObject>` → `AssetID {v3,v2,v1,v0}` | **Load-bearing twice.** `m_prefab.Name` is the RNG stream key: `seed = WorldGenerator.instance.GetSeed() + location.m_prefab.Name.GetStableHashCode()` (`ZoneSystem.cs:1880`). The `AssetID` is the identity used by `m_locationIDCache`, i.e. the "same prefab" test inside `HaveLocationInRange`. Dump both the four uints and the resolved name. |
| 5 | `m_biome` | `Heightmap.Biome` bitmask | Zone draw (`RandomBiomeFromBiomes`, with its bugs) **and** the per-point biome filter. |
| 6 | `m_biomeArea` | `Heightmap.BiomeArea`, `Everything` | Zone-centre filter against `WorldGenerator.GetBiomeArea(zoneCenter)`. |
| 7 | `m_quantity` | int | Loop bound (`placed < location.m_quantity`). |
| 8 | `m_prioritized` | bool | Ordering key (`OrderByDescending`, stable) **and** attempt budget: 60000 vs 12000. |
| 9 | `m_centerFirst` | bool | Selects `GetRandomZone(maxRange)` with `maxRange` starting at `m_minDistance` and `maxRange++` per attempt, instead of a biome-point draw. Changes the whole RNG consumption pattern. |
| 10 | `m_unique` | bool | `if (!location.m_unique || placed <= 0)` — skips the whole type when one is already placed; later drives `RemoveUnplacedLocations`. |
| 11 | `m_group` | string `""` | Same-group proximity rejection with `m_minDistanceFromSimilar`. |
| 12 | `m_minDistanceFromSimilar` | float | Reject if a same-AssetID or same-group instance (placed **or not**) is within this 3-D distance. |
| 13 | `m_groupMax` | string `""` | Group for the "must be near" test. |
| 14 | `m_maxDistanceFromSimilar` | float | Require a same-prefab / same-`m_groupMax` instance within this distance. |
| 15 | `m_iconAlways` | bool | The location shows on every player's map from the start (`ZoneSystem.GetLocationIcons`). Directly a user-facing feature of the tool. |
| 16 | `m_iconPlaced` | bool | Shows on the map only once its zone is generated. |
| 17 | `m_randomRotation` | bool `true` | Rotation at spawn: `Random.Range(0,16) * 22.5°` from the **ambient** RNG — not seed-determined. Needed so the tool can say "rotation unknown", and because rotation shifts a dungeon generator's world position and therefore its seed. |
| 18 | `m_slopeRotation` | bool | Same, via **`ZoneSystem.GetTerrainDelta`** (`ZoneSystem.cs:2701`), 10 × `Random.insideUnitCircle` from ambient state. Note this is a *different* function from the identically-named `WorldGenerator.GetTerrainDelta` used by the placement filter (rows 23/24): `ZoneSystem`'s samples `GetGroundHeight` (physics raycast), `WorldGenerator`'s samples `GetHeight` (pure). Both draw 10 `insideUnitCircle`. Do not merge them in the port. |
| 19 | `m_snapToWater` | bool | At spawn, `y = 30`. Changes the reported y and the dungeon seed's `(int)position.y` term. |
| 20 | `m_interiorRadius` | float | `maxRadius = Max(m_exteriorRadius, m_interiorRadius)` → the inset in `GetRandomPointInZone` (`Random.Range(-32+maxRadius, 32-maxRadius)`), so it changes the drawn coordinate itself. |
| 21 | `m_exteriorRadius` | float | Same `maxRadius`; **and** the radius passed to `GetTerrainDelta`; **and** the ClearArea half-size. |
| 22 | `m_clearArea` | bool | Adds a ClearArea of half-size `m_exteriorRadius` (axis-aligned **square**, `InsideClearArea`) — needed for vegetation prediction. |
| 23 | `m_minTerrainDelta` | float `0` | Terrain-delta filter, via **`WorldGenerator.GetTerrainDelta(point, m_exteriorRadius, …)`** (`WorldGenerator.cs:1418`, called at `ZoneSystem.cs:2001`). It is **unconditional** — there is no guard on the field values, so it runs on every surviving candidate point and consumes **10 `Random.insideUnitCircle` draws** every time, whatever the bounds are. It is on the RNG-consumption path, not just the accept path. The 10 samples use `WorldGenerator.GetHeight` (pure), never a raycast. |
| 24 | `m_maxTerrainDelta` | float `2` | Same call, same 10 draws. |
| 25 | `m_minimumVegetation` | float `0` | Vegetation-mask filter: reject if `> 0 && mask.a <= min`. |
| 26 | `m_maximumVegetation` | float `1` | Reject if `< 1 && mask.a >= max`. |
| 27 | `m_surroundCheckVegetation` | bool | Enables the ring sampling. |
| 28 | `m_surroundCheckDistance` | float `20` | Ring radius. |
| 29 | `m_surroundCheckLayers` | int `2` | `layers × 6` samples. |
| 30 | `m_surroundBetterThanAverage` | float | Acceptance threshold against the running baseline. |
| 31 | `m_inForest` | bool | Enables the `WorldGenerator.GetForestFactor` filter (static, seedless). |
| 32 | `m_forestTresholdMin` | float `0` | Bound. |
| 33 | `m_forestTresholdMax` | float `1` | Bound. |
| 34 | `m_minDistanceFromCenter` | float | Second, redundant distance pair (`Utils.LengthXZ`). |
| 35 | `m_maxDistanceFromCenter` | float | Same. |
| 36 | `m_minDistance` | float | First distance pair (`point.magnitude`, y = 0) **and** the initial `maxRange` when `m_centerFirst`. |
| 37 | `m_maxDistance` | float | Same pair. |
| 38 | `m_minAltitude` | float `-1000` | Altitude filter (`y - 30`). **Also selects the zone-draw function**: `m_minAltitude < 0` → `GetRandomPointByBiomes`, else `GetRandomPointByBiomesAboveSeaLevel` (`ZoneSystem.cs:1919`). A sign error here changes every draw. |
| 39 | `m_maxAltitude` | float `1000` | Altitude filter. |
| 40 | `m_foldout` | bool, `[HideInInspector]` | Editor state. Dump it only because it occupies a slot in the serialized layout (§4.3); ignore its value. |

Also dump, per entry:

* `AltBiomeParent` (private `m_altBiomeParent`, set at runtime by `SetupLocations` for entries that came
  from an `AltBiome.m_addLocations`). Drives the alt-biome gate in the point filter.
* `m_prefab.Name.GetStableHashCode()` — precomputed so the port never has to trust its own hash.
* The **index in `m_locations`** and the index in the `ordered` list actually produced by
  `m_locations.OrderByDescending(a => a.m_prioritized)` after the `m_enable`/`m_quantity` filter. Order
  decides everything downstream; do not let the port re-derive it.
* Which source contributed the entry (ZoneSystem prefab / which `LocationList` / which `AltBiome`), for
  diagnosing a game update.

### 1.2 `ZoneSystem.ZoneVegetation` — all 40 serialized fields

Declaration: `ZoneSystem.cs:34-145` (class body 34–145, `Clone()` at 141). **Corrected: 40, not 38** —
counting the public fields in the declaration gives 40, and the list below already names 40. Order below
is declaration order = Unity serialization order. Lower priority (vegetation acceptance uses physics raycasts and is
therefore only *seed-driven*, not reproducible bit-for-bit — `ZoneSystem.PlaceVegetation`), but the
table is cheap to dump and the tool will want ore deposits and berry bushes eventually.

`m_name`, `m_prefab` (**a plain `GameObject` reference, not a SoftReference** — the RNG key is
`veg.m_prefab.name`, the object's name, so the dumper must read `m_prefab.name` at runtime; it is not
recoverable from the SoftRef manifest), `m_enable`, `m_min`, `m_max` (`m_max < 1` means "chance of
exactly one"), `m_forcePlacement` (tries = count × 50), `m_scaleMin`, `m_scaleMax`, `m_randTilt`,
`m_chanceToUseGroundTilt`, `m_biome`, `m_biomeArea`, `m_blockCheck`, `m_snapToStaticSolid`,
`m_minAltitude`, `m_maxAltitude`, `m_minVegetation`, `m_maxVegetation` (filter applies only when
min ≠ max), `m_surroundCheckVegetation`, `m_surroundCheckDistance`, `m_surroundCheckLayers`,
`m_surroundBetterThanAverage`, `m_minOceanDepth`, `m_maxOceanDepth` (only when min ≠ max), `m_minTilt`,
`m_maxTilt`, `m_terrainDeltaRadius` (filter only when > 0), `m_maxTerrainDelta`, `m_minTerrainDelta`,
`m_snapToWater`, `m_groundOffset`, `m_groupSizeMin`, `m_groupSizeMax`, `m_groupRadius` (also the
in-zone inset: `±(32 - m_groupRadius)`), `m_minDistanceFromCenter`, `m_maxDistanceFromCenter`,
`m_inForest`, `m_forestTresholdMin`, `m_forestTresholdMax`, `m_foldout`, plus runtime `AltBiomeParent`.

Per-entry extras: `m_prefab.name`, its `GetStableHashCode()`, the index in `m_vegetation`, and the
source list. Vanilla ships 25+33+35 = 93 vegetation entries from `LocationList`s alone (game log,
2026-09-22 20:36:17) plus the ZoneSystem prefab's own and any `AltBiome.m_addVegetation`.

### 1.3 `AltBiomeList` / `AltBiome`

`AltBiomeList.m_alts` (`AltBiomeList.cs`) is copied into the **static** `AltBiomeList.m_altBiomes` in
`AltBiomeList.Awake`. Dump `m_altBiomes` in list order.

**Settled here (the knowledge base currently marks this Unverified): vanilla 1.0.15 ships 28 AltBiomes
and all of them have `m_enabled = true`.** Evidence: a structural scan of the decompressed bundle
`SoftRef/Bundles/d59cfac` found 28 contiguous `AltBiome` records at offsets 5156960–5181556 matching
the pattern (`m_name` string, `m_enabled` = 1, `m_biome` a valid bitmask, three name strings,
`m_levelUpChanceMultiplier` in range). **Re-verified independently** by decoding each record's
`m_name` / `m_enabled` / `m_biome` / three name strings / `m_levelUpChanceMultiplier` from
`probe/d59cfac.bin` (`probe/altbiome_decode.py`): 28 records, offsets 5 156 960 (Dark Meadows) –
5 181 556 (BroodSwarm Mistlands), **`m_enabled = 1` on all 28**, `m_namePrefix` / `m_nameSuffix` /
`m_nameOverride` all `""`, and `m_biome` exactly as grouped below.

**Notation:** `(×N)` below is `m_levelUpChanceMultiplier`, not a repeat count — every name in the table
is one AltBiome, and the table therefore totals 6 + 7 + 4 + 3 + 3 + 5 = 28. Measured values:
`Dark Meadows`, `Death Plains`, `Drake Mountain`, `Goblin Plains`, `Pinetree Black Forest`,
`Rock Black Forest`, `Wolf Mountain` = **2.0**; `Fortress Mountain` = **3.0**; the other 20 = **1.0**.
Names and biomes:

| Biome | AltBiomes |
|---|---|
| Meadows (1) | Dark Meadows (levelUp ×2), Peaceful Meadows, Dandelion Meadows, Raspberry Meadows, Smalltree Meadows, Birch Meadows |
| Black Forest (8) | Troll Black Forest, Root Black Forest, Ruin Black Forest, Rock Black Forest (×2), Pinetree Black Forest (×2), Blueberry Black Forest, Kalhygge Black Forest |
| Swamp (2) | Hut Swamp, Bog Swamp, Bat Swamp, Abomination Swamp |
| Mountain (4) | Wolf Mountain (×2), Drake Mountain (×2), Fortress Mountain (×3) |
| Plains (16) | Lox Plains, Goblin Plains (×2), Death Plains (×2) |
| Mistlands (512) | Rockless Mistlands, Trees Mistlands, Swords Mistlands, Hare Mistlands, BroodSwarm Mistlands |

Corroboration from the running game: `BepInEx\LogOutput.log` 2026-09-22 20:37:19 —
`Loading: Placed 0/1-2 of 'Fortress Mountain' altbiome. (Valid, sectors: 0, combos: 2)`. (Only the
below-minimum case is a warning; the other 27 go to `ZLog.DevLog` and are suppressed.) Further
corroboration: the same world's genloc log contains `TarPit1_1`, `TarPit2_1`, `TarPit3_1`,
`SwampHut1_1`, `SwampHut2_1`, `SwampHut3_1`, `GoblinCamp2_1` — the `_1` variants that only an
`AltBiome.m_addLocations` can contribute, and `SwampHut1_1` placed 33 of 50, so alt biomes really do
get sectors.

Fields the placement path reads (dump all of them anyway; these are the required ones):

* **Identity / RNG:** `m_name` — the per-pair stream seed is
  `(int)biome + m_name.GetStableHashCode() + worldSeed` (`AltBiomeWorldData.GenerateAltBiomes`;
  in source the addition is done in the enum's underlying `int`, `InitState((int)(biome.Key +
  validAltBiome2.m_name.GetStableHashCode() + WorldGenerator.instance.GetSeed()))`, which is the same
  value with 32-bit wraparound). **Two RNG facts the field list alone hides, both required to reproduce
  the assignment:** (a) `GenerateAltBiomes` opens with `UnityEngine.Random.InitState(
  WorldGenerator.instance.GetSeed() + 920)` before any loop — the literal is `920`; (b) immediately
  after each per-pair `InitState` it calls `biome.Value.Sectors.Shuffle()`, so the per-biome sector list
  is permuted **in place** on the global stream, and the acceptance loop then draws one
  `Random.Range(0f, 1f)` per candidate sector. `m_name` is therefore load-bearing twice: stream seed,
  and the string the `m_blockLocationNames` / `m_incompatibleAltBiomes` tests compare against.
* **Eligibility:** `m_enabled`, `m_biome` (bitmask, matched with `HasFlag` in
  `AltBiomeList.GetValidAltBiomes`). **The key set is not the nine biomes.** `GenerateAltBiomes`
  iterates `AltBiomeWorldData.Biomes`, a `Dictionary<Heightmap.Biome, BiomeTypeInfo>` filled in
  `AltBiomeWorldData..ctor` from `Enum.GetValues(typeof(Heightmap.Biome))` — **twelve** keys, because
  the enum also declares `None = 0`, `Land = 0x27F` and `All = 0x37F`. `x.HasFlag(None)` is `true` for
  every value, so **every enabled AltBiome is "valid" for the `None` key** and gets an extra
  `InitState` + `Shuffle` + per-sector `Range(0f,1f)` pass over `Biomes[None].Sectors`. That is why the
  game log reports `combos: 2` for `Fortress Mountain` (`m_biome = Mountain`): the two keys are `None`
  and `Mountain`, not two Mountain-flagged lists. A port that iterates only the nine real biomes will
  desynchronise the stream. **Unverified:** the enumeration order of that `Dictionary`. .NET returns
  insertion order for a dictionary that has never had a removal, and insertion order is
  `Enum.GetValues` order (ascending numeric: None, Meadows, Swamp, Mountain, BlackForest, Plains,
  AshLands, DeepNorth, Ocean, Mistlands, Land, All), but this is an implementation detail of
  `Dictionary<TKey,TValue>`, not a contract — the Mode A / H2 dump must record the observed key order.
* **Quota / chance:** `m_minAmountSpawned` (1), `m_maxAmountSpawned` (10), `m_chance` (0.1).
* **`BiomeSector.CanAddModifier` filters:** `m_minDistanceFromCenter` (1000), `m_minEdgeSize` (50),
  `m_maxEdgeSize` (1500), `m_minAvgHeight` (30), `m_maxAvgHeight` (10000), `m_belowWorldX`,
  `m_aboveWorldX`, `m_belowWorldY`, `m_aboveWorldY`, `m_incompatibleAltBiomes`, `m_requireNeighbor`,
  `m_notNeighbor`.
* **Content:** `m_addLocations` (full `ZoneLocation` records — dump them inline **and** note that
  `SetupLocations` appends them to `m_locations` after every `LocationList`), `m_blockLocationNames`,
  `m_addVegetation`, `m_blockVegetationNames`.

Not needed for placement, but dump for the UI: `m_namePrefix`, `m_nameSuffix`, `m_nameOverride`
(`BiomeSector.GetName` composes the displayed biome name from these), `m_levelUpChanceMultiplier`.
Not needed at all: `m_forceMusic`, `m_forceEnvironment`, `m_addEnvironments`, `m_blockEnvironments`,
`m_spawn`, `m_blockSpawnNames`, `m_terrainTextureOverride`, and the `[HideInInspector]` heightmap
fields (`AltBiome.heightMapChanges` is `const bool false`).

### 1.4 `ZoneSystem` scalars

| Field | Code default | Why |
|---|---|---|
| `m_locationVersion` | `1` in code | The regeneration trigger: `ZoneSystem.Load` forces `m_locationsGenerated = false` when the saved value differs. **Settled: the shipped value is 32.** Evidence: the user's world `asdasdasd`, `_main.3.db2`, location-version field = 32 (read with `valheim_saves.py locations`, read-only; today `vseed world asdasdasd --locations` prints the same field). The tool must print it, and must refuse to compare its prediction with a save whose stored version differs. |
| `m_waterLevel` | `30f` (also `c_WaterLevel = 30f`) | Sea level; every altitude filter is `y - m_waterLevel`. Dump to confirm the prefab does not override the const. |
| `m_zoneSize` | `64f` (also `c_ZoneSize`) | Zone grid. Same reasoning. |
| `m_zoneTTL`, `m_zoneTTS` | 4, 4 | Not needed by the tool; dump for completeness (cheap). |
| `m_locationScenes` | `List<string>` | **Dead at runtime** — `find-usages` shows one reference, the `stfld` in `ZoneSystem..ctor`. Dump it once to prove it is empty and then ignore it. |
| `m_locationLists` | `List<GameObject>` | Provenance: `ZoneSystem.Awake` instantiates each; their `LocationList.Awake` registers them. Dump the count and each `gameObject.name` + `m_sortOrder`. Vanilla has **6** (game log: six `Added N locations ... from main` lines, contributing 3, 2, 27, 4, 25, 25 = **86** locations and 0, 0, 25, 0, 33, 35 = **93** vegetation entries). |
| `m_altBiomeLists` | `List<AltBiomeList>` | Same; one in vanilla. |

### 1.5 `Minimap` — needed to read the ground-truth cache

| Field | Code default | Shipped value | Why |
|---|---|---|---|
| `m_textureSize` | `256` | **2048** | The cache files are `m_textureSize²` samples. |
| `m_pixelSize` | `64f` | **12.0** | World metres per cache pixel. |

Both are **verified from data, not from a dump**:

* Size: `cacheMinimapBiome` and `cacheMinimapMask` decompress (gzip, `Utils.Decompress`) to exactly
  16 777 216 bytes = 2048² `Color32`; `cacheMinimapHeight` to 8 388 608 bytes = 2048² `ushort` halves.
* Pixel size: `Minimap.GenerateWorldMap` computes `wx = (j - 1024) * m_pixelSize + m_pixelSize/2`. The
  Ashlands test `WorldGenerator.IsAshlands(x,y)` is a **closed-form, seedless** function
  (`Length(x, y - 4000) > 12000 + sin(atan2(x,y)*20)*100`, from `ashlandsMinDistance = 12000f`,
  `ashlandsYOffset = -4000f`, `WorldGenerator.WorldAngle`). Fitting `m_pixelSize` over 5.0–20.0 in 0.25
  steps against the Ashlands mask in the user's `cacheMinimapBiome` gives **0 mismatches out of 262 144
  sampled pixels at 12.0**, and 5 325 mismatches at the nearest alternative (12.25). Probe:
  `probe/fitpx.py`.
* Independent corroboration from code: `AltBiomeWorldData.c_textureSize = 2048`,
  `c_pixelSize = 12f`, `c_halfWidth = 1024`, `c_halfPixel = 6f` — the biome-point grid uses the same
  geometry.

Also dump the nine biome colours (`m_meadowsColor`, `m_ashlandsColor`, `m_blackforestColor`,
`m_deepnorthColor`, `m_heathColor`, `m_swampColor`, `m_mountainColor`, `m_mistlandsColor`, and the
hard-coded `Color.white` for Ocean — `Minimap.GetPixelColor`, `Minimap.cs:2092`, whose `default` arm is
also `Color.white`). **Seven of those eight fields are `public` (`Minimap.cs:260-272`) and therefore
prefab-overridable; `m_mistlandsColor` is `private Color` with no `[SerializeField]`
(`Minimap.cs:274`), so Unity does not serialize it, the prefab *cannot* override it, and its value is
guaranteed to be the code default `(0.2, 0.2, 0.2)` = `333333`. H4 must read it by reflection, and the
"never ship a code default" rule does not apply to this one field — it is the shipped value by
construction.** The tool needs them to render a map that
looks like the game's, **and** to decode `cacheMinimapBiome`. Observed in the user's cache (RGBA hex,
sampled every 2 px, with the mean geometry that identifies each):

| Colour | Biome | Evidence |
|---|---|---|
| `7b2020` | AshLands | 0/262144 mismatches against the closed-form `IsAshlands` |
| `92a75c` | Meadows | radius 8–5095 m |
| `6b743f` | BlackForest | radius 511–10377 m (the `> 5000+angle` fallback reaches past 10 km) |
| `a37258` | Swamp | radius 2000–6000 m, exactly the `minMarshDistance`..`maxMarshDistance(v2)` band |
| `e7ab78` | Plains | radius 2903–8000 m |
| `333333` | Mistlands | radius 5903–10000 m; matches the code default `m_mistlandsColor = (0.2,0.2,0.2)` |
| `ffffff` | Ocean **and** Mountain **and** DeepNorth | code: `Ocean => Color.white`, `m_mountainColor = (1,1,1)`, `m_deepnorthColor = (1,1,1)` |

**Consequence, and it matters:** `cacheMinimapBiome` is a **lossy** ground truth — three biomes share
white. Use `cacheMinimapHeight` as the primary reference (it is one float16 per pixel of
`GetBiomeHeight`, unambiguous), and the biome cache only as a coarse cross-check with the collision
declared.

Other `Minimap` prefab data worth one dump: `m_locationIcons` — a `List<LocationSpriteData>` of
`(m_name, m_icon)`. **It has exactly 5 entries in 1.0.15**: `StartTemple`, `Vendor_BlackForest`,
`Hildir_camp`, `BogWitch_Camp`, `AncientUpgradeStation`, in that order (read out of `main.unity`: a
count field of 5 followed by five entries of `string m_name` then a 12-byte
`PPtr{int32 fileID, int64 pathID}`; **the count field is at offset 1 939 300** — corrected from
1 939 275, which is not 4-aligned — in the decompressed bundle `17245031`. Re-decoded here; the five
PPtrs all have `fileID = 7`). A location with `m_iconAlways`/`m_iconPlaced` but no sprite entry
draws no pin (`Minimap.UpdateLocationPins`). Also useful: `m_exploreRadius` (100), `m_exploreInterval`
(2), `m_removeRadius` (128) if the tool ever models exploration.

### 1.6 Zone `Heightmap`

`Heightmap.m_width` (code default `32`) and `m_scale` (`1f`), on the `ZoneSystem.m_zonePrefab`'s
`Heightmap` component, and separately on the distant-LOD heightmap. Nothing in the code ties them to
the 64 m zone size — `HeightmapBuilder.RequestTerrainSync(position, m_width, m_scale, m_isDistantLod,
WorldGenerator.instance)` just forwards them — so they are genuinely prefab data. The tool needs them
only for M3-level fidelity (the in-game ground height comes from a `(m_width+1)²` vertex grid with the
four corner biomes blended, which is why `WorldGenerator.GetHeight` is an estimate, not the ground).
**Unverified: the shipped values.** Settled by one dump, or by reading the zone prefab offline (§4).

### 1.7 Per-location-prefab data (`Location`, `DungeonGenerator`)

Needed only for features beyond "where is it":

* `Location.m_exteriorRadius` (code 20), `m_interiorRadius` (20), `m_hasInterior`,
  `m_noBuildRadiusOverride`, `m_discoverLabel` — these are the *instantiated* location's radii, used by
  `Location.IsInsideLocation` and the discover label, not by placement. Dump if the tool reports "how
  big is it" or the in-game biome label.
* `Location.m_useCustomInteriorTransform` and `DungeonGenerator.m_useCustomInteriorTransform`, plus the
  generator's **local position offset** inside the location prefab. The dungeon seed is
  `worldSeed + zx*4271 + zy*(-7187) + (int)x*(-4271) + (int)y*9187 + (int)z*(-2134)` using the
  generator's **world** position, which is `locationPos + locationRot * generatorLocalPos`.
  **Re-verified verbatim** against the decompiled `DungeonGenerator.GetSeed`:

  ```csharp
  int seed = WorldGenerator.instance.GetSeed();
  Vector3 position = base.transform.position;
  Vector2i vector2i = ZoneSystem.GetZone(base.transform.position).ToVector2i();
  m_generatedSeed = seed + vector2i.x * 4271 + vector2i.y * -7187
                  + (int)position.x * -4271 + (int)position.y * 9187 + (int)position.z * -2134;
  ```

  Three details the formula alone hides and the port must copy: the zone term uses
  `ZoneSystem.GetZone(transform.position)` on the **generator's** position (which can differ from the
  location's zone if the offset crosses a boundary); `(int)` is a C# float→int **truncation toward
  zero**, not a floor, so it is asymmetric about 0 and `m_snapToWater`'s `y = 30` matters; and the
  result is memoised in `m_generatedSeed` behind `m_hasGeneratedSeed`, with a one-shot
  `m_forceSeed` override (`!= int.MinValue`) that consumes itself. With a non-zero XZ offset the
  unseeded location rotation feeds the dungeon seed, which is why the
  `m_useCustomInteriorTransform` flags exist — `Location.m_useCustomInteriorTransform`'s own tooltip
  says "Must use together with DungeonGenerator.m_useCustomInteriorTransform to make sure seeds are
  deterministic." **Unverified: which vanilla locations have a non-zero offset** — asset data; one dump
  settles it.
* These are the only fields that require touching 213 prefabs. They are `SoftReference` assets loaded
  on demand; see §3.3 for how to do this without blocking.

### 1.8 Version constants (read from the IL, not dumped — but pin them)

`Version.World.DeepNorth = 41` (the current world file version; `Minimap.TryLoadMinimapTextureData`
refuses a cache whose world is not exactly this), `Version.CachedMinimap.Original = 1`,
`Version.Player.DeepNorth = 46`, network version 40 (`Version.c_networkVersion = 40u`). All four
re-verified against the decompiled `Version` class. The user's `_main.3.db2` starts with `41`, and the
cache meta file is `52 e6 5b 96 | 01 00 00 00` = seed −1772362158, cached-minimap version 1 (re-read
here; the file is exactly 8 bytes).

Two details the port must not smooth over:

* The gate in `Minimap.TryLoadMinimapTextureData` is `Version.World.DeepNorth != ZNet.World.m_worldVersion`
  → `return false`, i.e. it is an **exact-equality** test on the world version, not a range test.
* The writer does **not** write the enum: `Minimap.SaveMapTextureDataToDisk` emits
  `BitConverter.GetBytes(ZNet.World.m_seed)` then `BitConverter.GetBytes(1)` — a hard-coded literal
  `1` — while the reader compares the field against `Version.CachedMinimap.Original`. They agree today;
  a tool that derives the written value from the enum would silently diverge if Iron Gate bumps the
  enum and forgets the literal.

### 1.9 Explicitly *not* needed

`EnvSetup` / `BiomeEnvSetup` / `RandomEvent` / `ClutterSystem.Clutter` from the `LocationList`s,
`SpawnSystem.SpawnData`, all `Minimap` UI prefab references, `ZoneSystem.m_zoneCtrlPrefab` /
`m_locationProxyPrefab`. Record their counts in the manifest only so that a game update that changes
them is visible.

---

## 2. When each value exists at runtime

### 2.1 Scene map (verified)

| Scene | Bundle | Contains |
|---|---|---|
| `Assets/Scenes/start.unity` | `10ba9da1` | `FejdStartup` (27 string hits), `Game` (45), `ZNet` (2). **`ZoneSystem`: 0. `Minimap`: 0. `WorldGenerator`: 0.** |
| `Assets/Scenes/loading.unity` | `cd5ddfe8` | nothing relevant |
| `Assets/Scenes/main.unity` | `17245031` | the `_ZoneSystem` GameObject, the `ZoneSystem` MonoBehaviour with the `ZoneLocation`/`ZoneVegetation` type trees and 114 of the location entries, the `Minimap` MonoBehaviour (`m_textureSize`/`m_pixelSize` type-tree entries at offset 340 641), `AltBiome`/`m_addLocations` type trees |

(Probe: decompress each bundle with `probe/unityfs.py`, count byte occurrences.)

So: **the main menu alone is not enough.** `FejdStartup.Awake` does call
`WorldGenerator.Initialize(World.GetMenuWorld())` (`FejdStartup.cs:318`), so `WorldGenerator.instance`
is non-null at the menu — but it is the *menu* world (`m_menu = true`, seed 0), and no `ZoneSystem`,
no `AltBiomeList`, no `Minimap` exists yet. Loading `main` is mandatory for Mode A.

### 2.2 Lifecycle inside `main`

```
SystemResourceManager.FastLoadScene(m_mainScene)          FejdStartup.cs:2961
  Game.Awake / ZNet.Awake ...
  ZoneSystem.Awake        "Zonesystem Awake <frame>"      ZoneSystem.cs:673
      Instantiate(each m_locationLists prefab)  -> LocationList.Awake registers it
      Instantiate(each m_altBiomeLists prefab)  -> AltBiomeList.Awake fills the static m_altBiomes
  ZoneSystem.Start        "Zonesystem Start <frame>"      ZoneSystem.cs:691
      SetupLocations()    <-- THE TABLE IS COMPLETE HERE
      ValidateVegetation()
  ZNet.Start
      IsServer -> ServerLoadWorld():
          LoadWorld() / LoadOldWorld()
          AltBiomeWorldData.VerifyBiomeData(world)   <-- m_biomeData + sectors + alt biomes
          ZoneSystem.GenerateLocationsIfNeeded()     <-- coroutine, "Loading: Generating locations"
          OnGenerationFinished -> OpenServer()
      !IsServer -> ClientConnect() ... ZNet.RPC_PeerInfo delivers seed/uid/worldGenVersion,
                   then WorldGenerator.Initialize(clientWorld)
  Minimap.Awake  (paths set)   ... Minimap.Update: TryLoadMinimapTextureData || GenerateWorldMap
```

`SetupLocations` builds the final list in this exact order (`ZoneSystem.SetupLocations`):

1. the `ZoneSystem` prefab's own `m_locations` / `m_vegetation` (already there);
2. `LocationList.GetAllLocationLists()` **sorted by `m_sortOrder` with `List.Sort` — an unstable sort**,
   then `AddRange` of each list's `m_locations`, then `m_vegetation`;
3. for each `AltBiome` in `AltBiomeList.m_altBiomes`: `AddRange(altBiome.m_addLocations)` and tag each
   with `AltBiomeParent = altBiome.m_name`; same for `m_addVegetation`;
4. for every entry with `(m_enable || m_prefab.IsValid) && Application.isPlaying`: set
   `m_prefabName = m_prefab.Name` and register in `m_locationsByHash` keyed by
   `ZoneLocation.Hash` — which is `m_prefab.Name.GetStableHashCode()`, **the property, not
   `m_prefabName`** (they are equal only because step 4 just assigned one from the other); duplicate
   hash → `ZLog.LogError`, later entry ignored.
5. **(missing from the original list, and it changes §3.3's prefab-walk plan)** if
   `Settings.AssetMemoryUsagePolicy.HasFlag(AssetMemoryUsagePolicy.KeepAsynchronousLoadedBit)`,
   `SetupLocations` then adds a `ReferenceHolder` to its own GameObject and runs
   `location2.m_prefab.Load(); referenceHolder.HoldReferenceTo(location2.m_prefab);
   location2.m_prefab.Release();` for **every** entry with `m_enable` — so under that policy all
   enabled location prefabs are already resident and held, and the Mode A prefab walk costs almost
   nothing. Dump the policy flags so the walk's cost estimate is not guesswork.

**The unstable sort is a real hazard.** Two `LocationList`s with the same `m_sortOrder` may be ordered
differently between runs of the same build in principle. The dumper must record each list's
`m_sortOrder` and its contributed range so the port can detect a tie; if there are no ties, say so
explicitly in the manifest.

### 2.3 Availability table

| Value | Menu (`start`) | Host / single-player, after `ZoneSystem.Start` | Client of a dedicated server |
|---|---|---|---|
| `m_locations`, `m_vegetation`, `m_locationVersion`, `m_waterLevel` | ✗ (no ZoneSystem) | ✓ | ✓ — it is prefab data; `SetupLocations` runs on both |
| `AltBiomeList.m_altBiomes` (fields) | ✗ | ✓ | ✓ |
| `AltBiome.Sectors` (the *assignment*) | ✗ | ✓ after `VerifyBiomeData` | ✗ — `GenerateAltBiomes` runs only inside `ServerLoadWorld` |
| `World.m_biomeData` (2048² point grid, `Biomes[b].AllPoints`) | ✗ | ✓ after `VerifyBiomeData` | ✗ — and the fallback is **`BiomeSector.EmptyBlackForest`**, not `EmptyMeadows`: `WorldGenerator.GetBiomeSector(int,int,bool)` (`WorldGenerator.cs:845`) tests `m_world.m_biomeData == null` → `EmptyBlackForest` **first**, and only a non-null-but-`!IsReady` grid yields `EmptyMeadows`. On a client `m_biomeData` is never assigned, so the client case is `EmptyBlackForest` (Biome `BlackForest`, empty `AltBiomes`). |
| `ZoneSystem.m_locationInstances` | ✗ | ✓ (server-side only) | ✗ — always empty on a client |
| `Minimap.m_textureSize` / `m_pixelSize` / colours / `m_locationIcons` | ✗ | ✓ | ✓ |
| zone `Heightmap.m_width` / `m_scale` | ✗ | ✓ | ✓ |
| `WorldGenerator.instance` + private offsets | ✓ but menu world (seed 0) | ✓ | null from `ZNet.Awake` until `RPC_PeerInfo` |

**Therefore:** run the dumper as **host of a local, throwaway, single-player world**. That is the only
configuration where every item above is available at once, and it is also the only configuration where
Mode B is safe.

---

## 3. The dumper plugin

### 3.1 Identity and build

```
_ModSource\ValheimDataDumper\
  ValheimDataDumper.csproj        copied from .claude\skills\valheim-modding\assets\plugin-template
  src\Plugin.cs  src\ModeA_Assets.cs  src\ModeB_GroundTruth.cs  src\Json.cs  src\RandomGuard.cs
  preflight.ps1
```

* `[BepInPlugin("DoomMachine.ValheimDataDumper", "ValheimDataDumper", "1.0.0")]`,
  `[BepInProcess("valheim.exe")]`.
* `netstandard2.1`, `LangVersion latest`, game DLLs with `<Private>false</Private>`, HarmonyX 2.9.
* **Build with `-p:DeployToGame=false` by default.** This plugin must not sit in `BepInEx\plugins\`
  during normal play; see §3.2. Deploy it deliberately, into its own folder, and remove it afterwards
  (move to `_ModSource\_retired\`, never delete).
* Serialization: **do not use `JsonUtility`** — it will not serialize `Dictionary`, nested
  `List<List<>>`, or produce stable key order. `Newtonsoft.Json.dll` ships with the game
  (`valheim_Data\Managed\Newtonsoft.Json.dll`); reference it with `<Private>false</Private>` and
  configure `Formatting.Indented`, invariant culture, and `FloatFormatHandling` such that every float
  is written **as its raw bits as well as its decimal form** (see §3.5).
* `preflight.ps1` must assert, against the shipped DLLs, that every reflected member still exists:
  the 40 `ZoneLocation` fields, the 40 `ZoneVegetation` fields, the `AltBiome` fields, the
  `WorldGenerator` private fields `m_offset0..4`, `m_riverSeed`, `m_streamSeed`, `m_lakes`, `m_rivers`,
  `m_streams`, `m_riverPoints`, and the hook targets in §3.3. Exit non-zero on any miss. Test the
  tripwire by renaming one field in the list and confirming it fires.

### 3.2 Trigger design — inert during normal play

The user plays multiplayer and cares about not perturbing it. The design is defence in depth; each
layer alone would be enough, and all four are cheap.

1. **Opt-in file, not a config default.** The plugin does nothing at all unless a file named
   `dumper.enable` exists next to the plugin DLL. No file → `Awake` logs one line
   (`ValheimDataDumper: no dumper.enable, idle`) and returns before applying any Harmony patch. Harmony
   patches that are never applied cannot interact with anything.
2. **Mode gate.** `dumper.enable` contains one word: `assets` or `groundtruth`. Anything else → idle.
3. **Hard multiplayer refusal, re-checked at the moment of action.** Before Mode A writes anything and
   before Mode B touches any static:
   ```
   ZNet.instance != null
   && ZNet.instance.IsServer()
   && ZNet.instance.GetPeers().Count == 0      // nobody connected
   && !ZNet.instance.IsDedicated()             // we are the client host
   && ZNet.World != null
   ```
   Mode B additionally requires `ZNet.instance.GetPeerConnections() == 0` re-checked **every frame** of
   the dump loop, and aborts and restores on the first peer. Refusal is loud: one `LogError` naming the
   reason, then permanent self-disable for the session.
4. **Manual keystroke for Mode A, console command for Mode B.** Mode A runs on a key the user presses.
   **Do not pick a default in this spec.** Check the candidate first against vanilla's bindings *and*
   every installed mod's config (found by scanning the game assemblies and `BepInEx\config`) — this
   project has already shipped a default (F9) that collided with a vanilla binding. Bind it through
   `ConfigEntry<KeyCode>` + `ZInput.GetKeyDown` (`UnityEngine.Input` does not work — new Input
   System), and gate it on `TextInput.IsVisible()`,
   `Console.IsVisible()` and `Chat.instance.HasFocus()` like every other hotkey here.
   Mode B runs only from a `Terminal.ConsoleCommand` registered in a
   `Terminal.InitTerminal` postfix — the conventional, conflict-free place — named
   `dumpgroundtruth <seedText>|<file-of-seeds> [flags]`, and only when rule 3 passes.

No `Update` loop, no automatic behaviour on world load, nothing that fires without a human action.
Anything the plugin does write goes to
`%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\..\..\..\valheim-dumper\` — a folder chosen because it
is **outside** every path the game manages. Explicit prohibitions, enforced by a path check in the one
write helper: never write under `worlds`, `worlds_local`, `characters`, the Steam Cloud `remote`
directory, or the game install.

### 3.3 Hooks, in order

All patches are `[HarmonyPostfix]` unless noted; each patch class gets its own
`harmony.PatchAll(typeof(X))` in its own try/catch (a `PatchAll(assembly)` aborts on the first
unresolvable target and would silently disable the rest).

| # | Target | Kind | What it does |
|---|---|---|---|
| H1 | `ZoneSystem.SetupLocations` | postfix | The **only** correct place to read the location and vegetation tables: the lists are complete and `m_prefabName` has been filled. Snapshot into plain C# records immediately (do not hold `ZoneLocation` references — `ZoneLocation.Clone()` is a `MemberwiseClone` and the game hands the same objects to `AltBiome` tagging). Also snapshot `AltBiomeList.m_altBiomes`, `m_locationVersion`, `m_waterLevel`, `m_zoneSize`, the `m_locationLists` names/sort orders. |
| H2 | `AltBiomeWorldData.GenerateAltBiomes` | prefix **and** postfix | Prefix: record `AltBiomeList.m_altBiomes[i].Sectors.Count` **before** the call (this is how the §3.7 contamination bug is detected in the field, not just in theory), plus the `Biomes` dictionary's key order and each key's `Sectors.Count` — see §1.3, the key set includes `None`/`Land`/`All`. Postfix: record, per AltBiome, `Sectors.Count`, `ValidPlacementSectors`, `ValidPlacementSectorCombos`, and the `(Center, EdgeCount, HeightAvg, DistanceFromCenter, Biome)` of each assigned sector. **Identify every sector by its index in `AltBiomeWorldData.Sectors`, never by its position in a `BiomeTypeInfo.Sectors` list** — `GenerateAltBiomes` calls `biome.Value.Sectors.Shuffle()` once per (biome, altBiome) pair, permuting those lists in place, so a postfix dump and a later re-read of the same data list them in different orders with no error. `AltBiomeWorldData.Sectors` itself is append-only (`GenerateSectors`) and is the stable identity. |
| H3 | `ZoneSystem.GenerateLocationsTimeSliced()` (the no-arg coroutine) | postfix | Fires once generation finishes. Dump `m_locationInstances` as `(zoneX, zoneY, prefabName, hash, x, y, z, placed)`, plus the `ordered` list the run actually used. Alternative with no patch at all: subscribe to the public `ZoneSystem.GenerateLocationsCompleted` event — prefer this, it is public and fires immediately if generation already finished. |
| H4 | `Minimap.Awake` | postfix | Read `m_textureSize`, `m_pixelSize`, the nine colours, `m_locationIcons`. (`Minimap.instance` is a raw `ldsfld` and can hand back a destroyed object — compare with `!= null`, never `ReferenceEquals`.) |
| H5 | `ZoneSystem.Awake` | postfix | Read `m_zonePrefab`'s `Heightmap` (`m_width`, `m_scale`) before anything instantiates it. Use `GetComponentInChildren<Heightmap>(true)`. |
| — | none | — | Location-prefab fields (§1.7) need the prefabs loaded. Do **not** patch anything: walk the snapshot, call `loc.m_prefab.Load()` (synchronous `SoftReference<T>.Load()`), read `Location` / `DungeonGenerator` off `loc.m_prefab.Asset`, then `Release()`. Do it **only in Mode A**, only after H3, spread over frames (a few per frame in a coroutine), and log progress. 213 synchronous loads in one frame will stall the game for many seconds. |

Nothing in the plugin may throw out of `Update` or `OnGUI`, and any repeating error must be
rate-limited.

### 3.4 `UnityEngine.Random` hygiene

The game's world generation, alt-biome assignment and location placement all run on the **global**
`UnityEngine.Random`. The plugin must be provably invisible to it.

**Rule: every code path in the plugin that can touch `UnityEngine.Random` is wrapped in a guard that
saves and restores `UnityEngine.Random.state`.**

```csharp
internal readonly struct RandomGuard : IDisposable
{
    private readonly UnityEngine.Random.State _saved;
    public RandomGuard(int? initState = null)
    {
        _saved = UnityEngine.Random.state;                 // struct copy, 4 ints
        if (initState.HasValue) UnityEngine.Random.InitState(initState.Value);
    }
    public void Dispose() { UnityEngine.Random.state = _saved; }
}
```

This is exactly what the game itself does (`WorldGenerator..ctor` saves `Random.state`, calls
`InitState(m_world.m_seed)`, draws seven values, and restores — note the `new FastNoise(m_world.m_seed)`
between the `InitState` and the draws is checked, and `FastNoise..ctor` is
`{ m_seed = seed; CalculateFractalBounding(); }` with no `UnityEngine.Random` use anywhere in
`FastNoise`, so the once-per-process static `m_noiseGen` creation does **not** shift the draw sequence
for the first world; `ZoneSystem.PlaceVegetation` at
`ZoneSystem.cs:1383`/`1584`; `ZoneSystem.GenerateLocationsTimeSliced` at `1881`/`2139`; and inside the
time-sliced loop it swaps the location's stream out and the ambient state in around every `yield`,
`ZoneSystem.cs:1912-1917` and `1945-1950`, which is why time slicing does not break determinism).

Consequences to respect:

* **Mode A must draw nothing at all.** Reading fields cannot perturb `Random`; the only risk is a
  helper that does. **`Utils.GenerateUID` is confirmed to draw** — its last statement is
  `return (long)(host + ":" + domain).GetHashCode() + (long)UnityEngine.Random.Range(1, int.MaxValue);`
  (`Utils.cs:424`) — and `World..ctor(string name, string seed)` calls it for `m_uid`, so **every
  `new World(...)` consumes exactly one draw from whatever stream is current.** In Mode B that is
  harmless *only* because (a) the draw happens before `WorldGenerator.Initialize`, whose constructor
  re-seeds with `InitState(m_world.m_seed)`, and (b) the outer `RandomGuard` restores the ambient
  stream at the end; it is **not** harmless if a future revision moves the `new World` after the
  `Initialize` or drops the guard. Assert it in a debug build: record `Random.state` at the start and
  end of the Mode A dump and log an error if the four words changed.
* **The state round-trip must be verified, not assumed.** `Random.state` is a public readable/writable
  struct of four ints, but the setter is native. The dumper's very first ground-truth item is a
  round-trip proof: `s = Random.state; Random.InitState(k); Random.state = s;` then draw and compare
  with an unperturbed control sequence.
* **Never call `Random.InitState` outside a guard**, including in Mode B's per-seed loop — the game's
  own `WorldGenerator` constructor will do it, and the guard around the whole per-seed body restores
  the ambient stream afterwards.
* The `HeightmapBuilder` thread calls `WorldGenerator.GetBiome`/`GetBiomeHeight`/`GetBiomeSector`
  concurrently with the main thread. It does **not** touch `UnityEngine.Random` (those functions are
  pure), so the guard is sufficient for RNG — but see §3.7 for what it does mean for the
  `WorldGenerator.instance` swap.

### 3.5 Output schema

Layout (this extends the tree in spec 08 §2.2 and must stay compatible with it):

```
valheim-dumper/
  1.0.15-59f53fb5/                         <gameVersion>-<first 8 hex of assembly_valheim sha256>
    manifest.json
    locations.json
    vegetation.json
    altbiomes.json
    prefab-constants.json
    version-constants.json
    locationprefabs.json                   Location/DungeonGenerator fields per location prefab
    goldens/
      random-<sha>.json                    UnityEngine.Random traces      (spec 03 input)
      perlin-<sha>.bin                     Mathf.PerlinNoise (in,out) f32 (spec 03 input)
      libm-<sha>.json                      Sin/Atan2/Pow bit comparison   (spec 03 input)
      worldgen-<seedHex>.json              per-seed offsets, river seeds, lakes/rivers/streams
      worldgrid-<seedHex>-<gridId>.bin     GetBiome byte + GetHeight f32 grids
      locationinstances-<seedHex>.json     the game's own genloc output
```

`manifest.json`:

```json
{
  "schema": 1,
  "stamp": "DATA-STAMP game-version=1.0.15 network=40 steam-build=25390630 assembly_valheim-sha256=59f5...adb1 dumped=2026-09-22 dumper=1.0.0 schema=1",
  "game": { "version": "1.0.15", "networkVersion": 40, "steamBuild": 25390630,
            "assemblyValheimSha256": "59f5...adb1", "unityVersion": "6000.0.75f1" },
  "dumper": { "version": "1.0.0", "mode": "assets", "utc": "2026-09-22T20:38:00Z" },
  "world": { "name": "dumper_throwaway", "seedText": "MWd8eV6svz", "seed": -1772362158,
             "worldGenVersion": 2, "worldVersion": 41, "locationVersion": 32 },
  "counts": { "locations": "<m_locations.Count - MEASURE IT, do not copy 213>",
              "locationsEnabled": 183,
              "locationPrefabsInSoftRefManifest": 213,
              "vegetation": "<m_vegetation.Count - MEASURE IT>",
              "altBiomes": 28, "locationLists": 6, "altBiomeLists": 1 },
  "sortOrderTies": false,
  "files": { "locations.json": { "sha256": "...", "bytes": 412345 }, "...": {} }
}
```

Every file repeats the `stamp` string as its first property, so a file lifted out of the folder is
still self-identifying.

**Corrected:** `counts.locations` originally read `213`. That conflates two different numbers. **213**
is the count of location *prefabs* in `StreamingAssets/SoftRef/manifest` (re-counted here: 587 assets
total, 212 under `Assets/world/Locations/`, plus `BogWitch_Camp` under `Assets/world/Props/BogWitchHut/`
= 213). `m_locations.Count` is the count of `ZoneLocation` *entries* after `SetupLocations`, which is
the ZoneSystem prefab's own list **plus** 86 from the six `LocationList`s (game log 2026-09-22 20:36:17:
3+2+27+4+25+25) **plus** every `AltBiome.m_addLocations` entry — and nothing measured in this document
gives that total. `vegetation: 118` was likewise not measured; only the 93 from the `LocationList`s
(0+0+25+0+33+35) is in evidence. **Unverified: `m_locations.Count` and `m_vegetation.Count`.** Settled
by the Mode A dump; until then the manifest must carry the measured value or nothing, never an
inferred one.

`locations.json` — an **array in `m_locations` order**, never an object (order is semantic):

```json
[
  { "index": 0,
    "orderedIndex": 0,
    "source": { "kind": "ZoneSystemPrefab" },
    "name": "StartTemple",
    "prefabName": "StartTemple",
    "assetId": { "v3": 619103153, "v2": 4191162132, "v1": 1260222343, "v0": 1222169926,
                 "hex": "24e6c3b1f9d00f144b1d778748d8d546" },
    "prefabPath": "Assets/world/Locations/Meadows/StartTemple.prefab",
    "nameHash": -1544986047,
    "enable": true,
    "biome": 1, "biomeArea": 2,
    "quantity": 1, "prioritized": true, "centerFirst": true, "unique": false,
    "group": "", "minDistanceFromSimilar": 0.0,
    "groupMax": "", "maxDistanceFromSimilar": 0.0,
    "iconAlways": true, "iconPlaced": false,
    "randomRotation": false, "slopeRotation": false, "snapToWater": false,
    "interiorRadius": 0.0, "exteriorRadius": 25.0, "clearArea": true,
    "minTerrainDelta": 0.0, "maxTerrainDelta": 3.0,
    "minimumVegetation": 0.0, "maximumVegetation": 1.0,
    "surroundCheckVegetation": false, "surroundCheckDistance": 20.0,
    "surroundCheckLayers": 2, "surroundBetterThanAverage": 0.0,
    "inForest": true, "forestTresholdMin": 1.0, "forestTresholdMax": 5.0,
    "minDistanceFromCenter": 0.0, "maxDistanceFromCenter": 0.0,
    "minDistance": 0.0, "maxDistance": 10000.0,
    "minAltitude": 3.0, "maxAltitude": 1000.0,
    "foldout": false,
    "altBiomeParent": null,
    "bits": { "minDistanceFromSimilar": "0x00000000", "exteriorRadius": "0x41C80000",
              "maxTerrainDelta": "0x40400000", "minAltitude": "0x40400000",
              "maxDistance": "0x461C4000", "forestTresholdMax": "0x40A00000",
              "…": "every float field" } }
]
```

**Corrected, and this was the most dangerous error in the document.** The example above originally
carried `inForest: false`, `forestTresholdMin: 0.0`, `forestTresholdMax: 1.0`, `maxDistance: 0.0`,
`minAltitude: -1000.0` — i.e. the **code defaults**, substituted where the shipped prefab differs,
which is precisely the mistake §1 forbids. The values printed above are the byte-exact decode of the
`StartTemple` entry in the decompressed `main.unity` blob at offset **1 513 248**
(`probe/starttemple_decode.py`; the decode terminates exactly where the next entry's
`0a 00 00 00 "Eikthyrnir"` begins, which is what proves the 40-field layout and the field alignment).
The consequence is not cosmetic: `m_minAltitude = 3.0` is **≥ 0**, so per row 38 `StartTemple` draws its
zone through `GetRandomPointByBiomesAboveSeaLevel`, **not** `GetRandomPointByBiomes`. A port seeded
from the old example would take the wrong branch for the game's spawn point and then diverge on every
subsequent draw of that stream. The four `assetId` uints were also wrong (they did not reproduce their
own `hex` field); the corrected values are
`struct.unpack("<4I", bytes.fromhex("b1c3e624140fd0f987771d4b46d5d848"))`, and
`"".join("%08x" % u)` over them reproduces `24e6c3b1f9d00f144b1d778748d8d546`, which the SoftRef
manifest (line 10380) maps to bundle `1e488b5`, `Assets/world/Locations/Meadows/StartTemple.prefab`.
`nameHash: -1544986047` is correct — recomputed from `StringExtensionMethods.GetStableHashCode` over
`"StartTemple"`. `foldout` was missing from the example although §1.1 row 40 requires it.

**Unverified:** `"index": 0` / `"orderedIndex": 0` / `"source": {"kind":"ZoneSystemPrefab"}` for this
entry. `StartTemple` is the first `ZoneLocation` in the `ZoneSystem` MonoBehaviour's own serialized
`m_locations` in `main.unity` and is `m_prioritized`, which is consistent with both being 0, but the
`ordered` list is built from the full concatenated `m_locations` and only the Mode A / H3 dump settles
the indices.

Three schema rules that are not negotiable:

* **Every float carries its `BitConverter.SingleToInt32Bits` value** in the parallel `bits` object.
  A decimal round trip through JSON is not bit-exact in every reader, and a one-ulp error in
  `m_exteriorRadius` moves `GetRandomPointInZone`'s draw range and therefore every subsequent draw.
* **Enums are dumped as integers**, never names. `m_biome` is a bitmask
  (None 0, Meadows 1, Swamp 2, Mountain 4, BlackForest 8, Plains 16, AshLands 32, DeepNorth 64,
  Ocean 256, Mistlands 512); `m_biomeArea` is Edge 1, Median 2, Everything 3.
* **Nothing is omitted when it equals a default.** The schema is a complete record, so a future
  default change cannot be mistaken for an unchanged value.

`vegetation.json` mirrors this for `ZoneVegetation` (with `prefabName` taken from
`veg.m_prefab.name`, and its hash).

`altbiomes.json` — array in `AltBiomeList.m_altBiomes` order, every field of §1.3, plus `nameHash`,
plus the nested `addLocations` / `addVegetation` arrays in the same schema as above, plus (when the
dump ran after `GenerateAltBiomes`) the assignment result: `sectorsAssigned`, `validPlacementSectors`,
`validPlacementSectorCombos`, and per sector `{biome, center, edgeCount, heightMin, heightMax,
heightAvg, distanceFromCenter, neighborBiomes}`.

`prefab-constants.json`: `zoneSystem { locationVersion, waterLevel, zoneSize, zoneTTL, zoneTTS,
locationScenes, locationLists[{name,sortOrder,locationCount,vegetationCount}], altBiomeLists[] }`,
`minimap { textureSize, pixelSize, colors{…, bits}, locationIcons[name], exploreRadius,
exploreInterval, removeRadius }`, `heightmap { zone{width,scale}, distantLod{width,scale} }`.

### 3.6 The ground-truth dump (Mode B)

#### 3.6.1 `WorldGenerator` private state, per seed

All of these are private instance fields on `WorldGenerator` (declared `WorldGenerator.cs:60-80`:
`m_offset0` 60, `m_offset1` 62, `m_offset2` 64, `m_offset3` 66, `m_offset4` 68, `m_riverSeed` 70,
`m_streamSeed` 72, `m_lakes` 74, `m_rivers` 76, `m_streams` 78, `m_riverPoints` 80 — the original
range 54–86 also covers the *static* `m_instance` (54), the *public* `m_world` (56) and
`m_cachedRiverPoints`/`m_cachedRiverGrid`/`m_riverCacheLock` (82/84/86), none of which are in this
list);
read them with `AccessTools.Field(typeof(WorldGenerator), "m_offset0").GetValue(instance)` (not
`FieldRef`, which the C#-5 fallback compiler cannot use — irrelevant here but keeps the pattern
uniform).

```json
{ "seedText": "MWd8eV6svz", "seed": -1772362158, "worldGenVersion": 2, "menu": false,
  "version": 2,
  "offsets": { "m_offset0": {"f": -3178.0, "bits": "0xC546A000"},
               "m_offset1": {...}, "m_offset2": {...}, "m_offset3": {...}, "m_offset4": {...} },
  "riverSeed": 123456789, "streamSeed": -987654321,
  "randomTrace": [ { "call": "InitState", "arg": -1772362158,
                     "stateAfter": ["0x...","0x...","0x...","0x..."] },
                   { "call": "Range(-10000,10000)", "resultBits": "0x...",
                     "stateAfter": [ ... ] }, "… 7 draws, in constructor order" ],
  "lakes":   [ {"x": {"f":..,"bits":".."}, "y": {...}} ],
  "rivers":  [ {"p0":{...},"p1":{...},"center":{...},
                "widthMin":{...},"widthMax":{...},
                "curveWidth":{...},"curveWavelength":{...}} ],
  "streams": [ ... ],
  "riverPoints": { "gridSize": 64.0,
                   "cells": [ {"gx":-3,"gy":7,
                               "points":[{"p":{...},"w":{...},"w2":{...}}] } ] } }
```

Notes that decide whether this is usable:

* **The draw order in the constructor is `m_offset0,1,2,3` (`Range(-10000,10000)`), then `m_riverSeed`
  and `m_streamSeed` (`Range(int.MinValue,int.MaxValue)`), then `m_offset4`** (`WorldGenerator..ctor`,
  **`WorldGenerator.cs:223-229`** — corrected from 222-228; line 222 is `m_noiseGen.SetSeed(0)`, the
  seven draws are 223, 224, 225, 226 (`m_offset0..3`), 227 (`m_riverSeed`), 228 (`m_streamSeed`),
  229 (`m_offset4`); `InitState(m_world.m_seed)` is at 213 and the restore
  `UnityEngine.Random.state = state` at 234). `m_offset4` is drawn **last**, after the two river seeds.
  Getting this wrong misplaces the Mistlands and nothing else, which makes it exactly the kind of error
  that survives a casual check.
* **The `FastNoise` is not world-seeded.** The ctor does `new FastNoise(m_world.m_seed)` only when the
  *static* `m_noiseGen` is null, and then unconditionally `m_noiseGen.SetSeed(0)` at line 222 — so the
  cellular generator every world uses is seeded **0**, and `m_world.m_seed` never reaches it.
  `FastNoise..ctor` is `{ m_seed = seed; CalculateFractalBounding(); }` and `FastNoise` contains no
  `UnityEngine.Random` use at all, so the once-per-process construction also does not shift the seven
  draws. Dump `m_noiseGen.GetSeed()` anyway, as a tripwire.
* `m_rivers`, `m_streams`, `m_lakes` and `m_riverPoints` only exist when `Pregenerate()` ran, which it
  does for any non-menu world. `PlaceStreams(isDN: true)` is called for its side effect on
  `m_riverPoints` and its return value is discarded, so `m_streams` holds only the non-DeepNorth
  streams — dump `m_riverPoints` too, it is where the DeepNorth streams live.
* `m_riverPoints` is a `Dictionary<Vector2i, RiverPoint[]>` on a 64 m grid; for seed −1772362158 it is
  large. Dump it as a separate binary sidecar if the JSON exceeds ~50 MB: `int cellCount`, then per
  cell `int gx, int gy, int n`, then `n × (float p.x, float p.y, float w, float w2)` little-endian.
  Raw IEEE-754 bytes, no text.
* **Everything is bit patterns.** Every float in this file appears as `"bits": "0x…"`; the decimal is a
  convenience for humans and the port must never parse it.

#### 3.6.2 Grids

For each named seed, on a declared grid, raw little-endian binary (no JSON):

```
header: magic "VGT1", int32 schema, int32 seed, int32 worldGenVersion,
        float32 x0, float32 z0, float32 step, int32 nx, int32 nz, int32 flags
body:   nz*nx * { uint16 biome, float32 height, float32 maskR,G,B,A }
```

Grids to dump, per seed:

| Grid | Extent | Step | Why |
|---|---|---|---|
| `full12` | **−12282..+12282** | 12.0 | Directly comparable with the minimap cache geometry (2048², `wx = (j-1024)*12+6`, `AltBiomeWorldData.MapSpaceToWorldSpace(x) = (x - 1024f) * 12f + 6f`). Use the **same half-pixel offset**, which puts the samples at −12282 (j=0) and +12282 (j=2047), **not** at ±12288. Corrected: ±12288 with a 12.0 step and a half-pixel offset lands on a different lattice from the cache and would report a whole-grid mismatch. |
| `coarse128` | −10496..10496 | 128.0 | Cheap regression grid. **Corrected: this is *not* the lattice `FindLakes` walks.** `WorldGenerator.FindLakes` runs `for (float y = -10000f; y <= 10000f; y += 128.0)` and the same for x, i.e. the points `-10000 + 128k`, k = 0..156 (last value 9968, 157×157 points), then rejects `magnitude > 10000f` and keeps `GetBaseHeight(x, y, menuTerrain:false) < 0.05f`. `-10496 + 128m` and `-10000 + 128k` never coincide (10000 is not a multiple of 128), so the two lattices are **disjoint** and `coarse128` cannot validate `FindLakes` at all. |
| `findlakes` | −10000..9968 | 128.0 | **Added.** The exact `FindLakes` lattice, y outer / x inner, sample order preserved, with `GetBaseHeight` per point and a flag for the `magnitude > 10000f` rejection and the `< 0.05f` acceptance — plus the resulting `m_lakes` after `MergePoints(list, 800f)`. This is the only grid that tests lake placement, and lake placement feeds `PlaceRivers`. |
| `edges` | explicit point list | — | The discontinuities: r = 5000/6000/8000/10000/10490/10500, the Ashlands ring at `12000 + sin(atan2(x,y)*20)*100` offset by y−4000, the DeepNorth ring at 12000 offset by y+4000, river centre lines taken from `m_rivers`, and ±600 m around `m_minMountainDistance`. A uniform grid will not hit these and they are where a port breaks. |

Also dump `GetBiomeArea(Vector2s)` over a zone grid, `GetForestFactor` over `coarse128` (it is static
and seedless — one file for all seeds), and `GetBaseHeight(x, y, menuTerrain:false)` (private; via
reflection) on `coarse128`, because `GetBiome`'s ocean test and the Swamp/Mistlands/Plains bands all
key off the *base* height, and separating base height from final height localises a port bug to one
function instead of nine.

#### 3.6.3 Native-function samples (the interface with spec 03)

**Assumption, stated because spec 03 does not exist at the time of writing:** spec 03 covers the
native functions (`Mathf.PerlinNoise`, `UnityEngine.Random`, the libm delta). The dumper produces the
evidence; spec 03 consumes it. If spec 03 asks for something else, its list wins and this section is
the floor, not the ceiling. The requirement list below is taken from spec 08 §3.4, which is the
in-session authority.

1. **`UnityEngine.Random`** — for ≥512 seeds (0, ±1, `int.MinValue`, `int.MaxValue`, −1772362158, the
   real `worldSeed + nameHash` values for all 183 enabled locations, and random 32-bit values):
   `InitState(s)`, then the four `Random.state` words, then the seven constructor draws in order with
   the state after each. Then, from a fixed state: `Random.value` ×N with state deltas;
   `Random.Range(int,int)` for `(0,0)`, `(0,1)`, `(-3,3)`, `(-n,n)` with large n — recording the
   **state delta** so the port learns whether `min == max` consumes a draw; `Random.Range(float,float)`
   including `min > max`; `Random.insideUnitCircle` over many samples with state deltas, to decide
   between "2 draws, `angle = v*2π`, `r = sqrt(v)`" and rejection sampling. And the state round-trip
   proof from §3.4.
2. **`Mathf.PerlinNoise`** — 10⁷ (in.x, in.y, out) float32 triples: (a) the exact coordinates the
   generator produces for four seeds on the `coarse128` grid — capture them by a temporary Harmony
   prefix on the game's own call sites, not by re-deriving them; (b) a stress set with negative inputs,
   exact integer lattice points, values near 2²³, and the `+0.123f / +0.15123f / +0.321f / +0.231f`
   offsets the sea-channel term uses.
3. **libm delta** — `Math.Sin`, `Math.Atan2`, `Math.Pow` over the exact arguments used by
   `WorldGenerator.WorldAngle` (`Sin((float)((float)Atan2(wx,wy) * 20.0))`), by the Mistlands `^1.5`
   and the Ashlands `^1.4`, as input bits → output bits, so the port can diff against .NET 10 without
   reasoning.
4. **`GetStableHashCode`** — 256 (string, hash) pairs including every location and vegetation prefab
   name, so the port's hash is proven on the exact inputs that matter.

#### 3.6.4 Ground truth that already exists — use it before writing any of the above

Two corpora are already on this machine, produced by the real game, and cost nothing:

* **`asdasdasd` minimap cache** —
  `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\worlds_local\asdasdasd\`. `cacheMinimapMeta` is
  8 bytes: `int32 seed` (−1772362158) and `int32 Version.CachedMinimap` (1). `cacheMinimapHeight` is
  gzip of 2048² `ushort` float16 (`Mathf.FloatToHalf`) of
  `GetBiomeHeight(GetBiome(wx,wy), wx, wy, out _)` at `wx = (j-1024)*12+6`, `wy = (i-1024)*12+6`, index
  `i*2048+j` (`Minimap.GenerateWorldMap`, `Utils.FloatsToCompressedHalfBuffer`, `Utils.Compress` =
  `GZipStream`). `cacheMinimapBiome` is gzip of 2048² `Color32` of `GetPixelColor(biome)` (lossy, §1.5).
  `cacheMinimapMask` is gzip of 2048² `Color32` of `GetMaskColor(wx, wy, height, biome)` — which
  exercises `InForest`, `GetForestFactor`, `GetAshlandsOceanGradient` and
  `GetAshlandsHeight(..., cheap:true)`, i.e. four more functions for free.
  **Half precision is ~3 decimal digits**: use this to find gross errors, never to claim bit-exactness.
* **`asdasdasd` location instances** — `<Steam>\userdata\<accountId>\892970\remote\worlds\asdasdasd\_main.<N>.db2`
  holds **12 314** instances across **176** distinct types for seed −1772362158, location version 32,
  112 zones generated. Read it read-only with `vseed world asdasdasd --locations`. Every hash resolves
  against the 213 manifest prefab names (0 unknown), which is itself a proof that the name→hash→save
  chain is understood end to end.

  **Caveats that must travel with this corpus.** (a) Placement ran once, at world creation, when no
  zone had been generated — so it is a clean fresh-world run and matches the case the port models. The
  112 generated zones accumulated afterwards, from play. (b) Only a few dozen of the 12 314 instances
  have `placed = true`; the rest are unplaced candidates, which is exactly what makes the file useful
  (it is the raw output of the algorithm, before `RemoveUnplacedLocations` prunes anything). (c) A
  later `genloc` — or a game update that bumps `m_locationVersion` past 32 — would re-roll every
  unplaced candidate, and the file would then no longer be a fresh-world run. Copy it, record its
  `location_version` and modification time, and re-check both before every comparison.

**Both paths are read-only. Never write to either; Steam Cloud syncs the second one.**

### 3.7 Many seeds in one launch

This is the part most likely to produce quietly wrong data.

#### The mechanism

`WorldGenerator.Initialize(World world)` is **public static** and simply does
`m_instance?.CleanCachedRiverData(); m_instance = new WorldGenerator(world);`. `new World(name, seed)`
sets `m_seed = seedText.GetStableHashCode()` and `m_worldGenVersion = 2`. So a loop is:

```csharp
var savedWorldGen = WorldGenerator.instance;            // private static m_instance, via reflection
var savedZNetWorld = ZNet.World;                        // ZNet.m_world
using (new RandomGuard())
foreach (var seedText in seeds)
{
    var w = new World("dumper_" + seedText, seedText);  // m_worldGenVersion = 2, m_menu = false
    WorldGenerator.Initialize(w);                       // runs Pregenerate(): lakes, rivers, streams
    DumpWorldGenPrivates(w, WorldGenerator.instance);
    DumpGrids(w);
    if (needBiomeGrid)
    {
        ResetAltBiomeAssignment();                      // see below - MANDATORY
        AltBiomeWorldData.GenerateBiomePoints(w);       // fills w.m_biomeData, ~4.2M GetBiome+GetBiomeHeight
        w.m_biomeData.GenerateSectors();                // flood fill + GenerateAltBiomes()
        DumpBiomeData(w);
    }
}
WorldGenerator.Initialize(savedWorldGen.m_world);       // MUST be this; see Invariant 0 below
```

#### Invariant 0 — you cannot restore the saved `WorldGenerator` object, only re-create it

The original text offered "`WorldGenerator.Initialize(savedWorldGen.m_world)` **or restore
`m_instance` directly**". **The second option is wrong and fails silently.**
`WorldGenerator.Initialize(World world)` is

```csharp
public static void Initialize(World world)
{
    m_instance?.CleanCachedRiverData();
    m_instance = new WorldGenerator(world);
}
```

and `CleanCachedRiverData()` is `m_riverPoints.Clear(); m_rivers.Clear(); m_streams.Clear();
m_cachedRiverPoints = null;`. So the **first** `Initialize` inside the loop guts the object
`savedWorldGen` still points at: its rivers, streams and river points are gone and cannot be
recovered, because `Pregenerate()` runs only from the constructor. Writing `m_instance = savedWorldGen`
at the end therefore hands the rest of the session a generator whose `GetHeight` has no rivers and no
streams — terrain that is plausible, self-consistent and wrong, with no error anywhere.

Re-`Initialize` from the saved `World` instead, which builds a fresh generator and re-runs
`Pregenerate()`. Two consequences to respect: the re-`Initialize` itself draws the seven constructor
values (wrap the whole restore in the same `RandomGuard`), and `WorldGenerator..ctor` clears the static
`s_cachedBiomeAreas` / `s_cachedBiomes`, which is what you want here. In the recommended main-menu mode
the saved world is `World.GetMenuWorld()` with `m_menu = true`, so `Pregenerate()` is skipped and there
is no river data to lose — but the loop must not rely on that, because the same code is what someone
will later re-use in-world.

#### Invariant 1 — never call `AltBiomeWorldData.VerifyBiomeData`

`VerifyBiomeData(world)` starts with `RemoveCache(world.m_name)`, which **deletes**
`<LocalLow>\IronGate\Valheim\cache\<worldName>_biomedatacache.bin`. Call `GenerateBiomePoints(world)`
and `world.m_biomeData.GenerateSectors()` directly instead; they do everything `VerifyBiomeData` does
minus the delete. If `VerifyBiomeData` is used anyway, the synthetic world name must be prefixed
(`dumper_…`) so it cannot name a real world — but the direct calls are strictly better.

#### Invariant 2 — clear `AltBiome.Sectors` before every `GenerateAltBiomes`

`AltBiomeWorldData.GenerateAltBiomes` resets only `ValidPlacementSectors` and
`ValidPlacementSectorCombos`. It never clears `AltBiome.Sectors`, and its quota tests are
`validAltBiome2.Sectors.Count < m_maxAmountSpawned` and `< m_minAmountSpawned`. `BiomeSector.AddModifier`
appends to both `modifier.Sectors` and `sector.AltBiomes`. In normal play this is harmless because a new
`main` scene load re-instantiates the `AltBiomeList` prefab and produces fresh `AltBiome` objects
(`ZoneSystem.Awake` → `Instantiate`, `AltBiomeList.Awake` → `m_altBiomes.AddRange(m_alts)`,
`OnDestroy` → remove). In a dumper loop the objects persist, so:

* seed 2 sees every AltBiome already at or near `m_maxAmountSpawned` and assigns almost nothing;
* the stale `BiomeSector` objects belong to seed 1's `AltBiomeWorldData` and hold references to it,
  leaking the whole 2048² grid for every seed dumped.

So `ResetAltBiomeAssignment()` must, for every `AltBiome` in `AltBiomeList.m_altBiomes`:
`a.Sectors.Clear(); a.ValidPlacementSectors = 0; a.ValidPlacementSectorCombos = 0;`.
(The two counters are redundant — `GenerateAltBiomes` zeroes them itself in its first loop — but
`Sectors` is not, and clearing all three keeps the reset auditable. The **other** side of
`AddModifier`, `sector.AltBiomes`, needs no reset: every `BiomeSector` is constructed fresh by
`GenerateSectors` for each new `AltBiomeWorldData`, and the four static
`BiomeSector.Empty*` singletons are built with `world == null` and are never added to any
`BiomeTypeInfo.Sectors`, so `AddModifier` can never reach them.) H2's prefix assert
(§3.3) exists to catch a regression here. The same bug is reachable in-game by running `genloc alt`
twice — worth reporting, and worth the tool refusing to compare against a world where that happened.

#### Invariant 3 — the `WorldGenerator.instance` swap is visible to another thread

`HeightmapBuilder` runs its own thread and calls `GetBiome`/`GetBiomeHeight`/`GetBiomeSector` on
whatever `WorldGenerator` its queued `HMBuildData` captured; `Heightmap.Regenerate` re-requests when
`m_buildData.m_worldGen != WorldGenerator.instance` (`Heightmap.cs:430`). Swapping the static while a
world is loaded therefore makes every loaded heightmap rebuild against the wrong generator — visible
terrain corruption, and worse, `HeightmapBuilder` may be mid-build against an instance the loop is
about to replace. `WorldGenerator` also carries a `ReaderWriterLockSlim m_riverCacheLock` and the
**static** caches `s_cachedBiomeAreas` / `s_cachedBiomes`, which the constructor clears — so a
concurrent `GetBiomeArea` on the builder thread can read a cache that belongs to another seed.

Mitigations, in order of preference:

1. **Run Mode B with no world loaded.** From the main menu (`ZNet.instance == null`), there is no
   `ZoneSystem`, no loaded `Heightmap` except the menu backdrop, and no peers. The menu backdrop will
   flicker as the generator changes; restore `World.GetMenuWorld()` at the end. This is the recommended
   mode, and it is enough for everything in §3.6.1–§3.6.3.
2. If Mode B must run in-world (only to reuse a loaded `ZoneSystem`), first stop the builder:
   `HeightmapBuilder.instance.Dispose()` is destructive and cannot be undone in-session, so instead
   require the user to accept that the session is disposable and quit to the menu afterwards without
   saving. Prefer (1).
3. Never run the per-seed loop off the main thread. `UnityEngine.Random` is not thread-safe, the state
   guard is global, and `Instantiate`/`Object` comparisons are main-thread only.

#### Invariant 4 — `ZNet.World` and location placement

The placement routine reads `ZNet.World.m_biomeData` (`ZoneSystem.cs:1919`) and
`WorldGenerator.instance.GetSeed()`. **Reproducing the game's own `genloc` for an arbitrary seed
in-process is therefore a much bigger intervention** — it needs `ZNet.World` swapped, `ZoneSystem`
present, `m_locationInstances` cleared, and the coroutine driven to completion. Do not do it. For
location ground truth, create real throwaway worlds through the normal UI (one seed per world, ~35 s
each on this machine) and let H3 dump `m_locationInstances`; a handful of seeds is enough to validate a
port, and the `asdasdasd` corpus already provides one for free.

#### Cost

Per seed, the biome grid alone is 4 194 304 `GetBiome` + `GetBiomeHeight` calls plus a 2048² flood
fill. Measured in-game on this machine (`LogOutput.log`, 2026-09-22): ~8 s from `ZNet Start` to
`Loading: Generating locations` (minimap + biome grid), then 33.4 s of genloc. Budget ~10 s per seed
for grids and ~40 s if placement is included. Dump 8–32 seeds, not thousands; the dumper produces
*reference* data, not the product.

---

## 4. Can the asset data be extracted without launching the game?

**Verdict: FEASIBLE.** Not trivial, but demonstrated far enough that the remaining work is ordinary
engineering with no unknowns of principle. This matters because it decides whether the tool can be
refreshed after a game update without the user launching Valheim — and the answer is that it can, with
the launch-based dumper kept as the cross-check.

### 4.1 What is actually in `valheim_Data`

```
valheim_Data\
  Managed\                      assembly_valheim.dll and friends (code, no location data)
  globalgamemanagers            205 668 B   engine settings
  globalgamemanagers.assets     342 960 B
  level0                        1 408 B     <- the built-in scene list is a stub
  resources.assets              79 228 948 B
  sharedassets0.assets          13 444 B
  StreamingAssets\SoftRef\
    hash                        17 B
    manifest                    228 955 B   TEXT, UTF-8 BOM, CRLF
    manifest_extended           3 296 936 B TEXT, same format, 21 886 assets
    Bundles\                    799 files, 4.1 GB, all "UnityFS" v8, Unity 6000.0.75f1
```

`resources.assets` and `globalgamemanagers.assets` contain **none** of the location prefab names
(grep for `Mistlands_Giant1`, `FaderLocation`, `CharredFortress`, `MorkBorg`, `Hildir_cave`,
`BogWitch_Camp`: 0 hits each). Everything is in the bundles.

### 4.2 The SoftRef manifest is a plain-text index — and it is the RNG key

`StreamingAssets/SoftRef/manifest` is human-readable:

```
SoftRef manifest - Text
version: 2
bundles directory: ./Bundles
bundle dependencies:
- bundle: 1080ee37
  dependencies:
  - 6a33a62
  ...
asset locations:                                   (line 10124)
- asset ID: 24e6c3b1f9d00f144b1d778748d8d546
  bundle: 1e488b5
  path in bundle: Assets/world/Locations/Meadows/StartTemple.prefab
```

587 assets: 212 under `Assets/world/Locations/`, 358 under `Assets/world/Rooms/`, plus
`Assets/world/Props/BogWitchHut/BogWitch_Camp.prefab`, the three scenes and a few graphics assets.
`manifest_extended` (21 886 assets) is the same format for everything else.

This is decisive for the RNG, because:

```
SoftReference<T>.Name  ==  m_name ?? (m_name = Shared.GetFileName(Runtime.GetAssetPath(m_assetID),
                                                                 withExtension: false))

Shared.GetFileName(p, withExtension):          // exact, both guards included
    s = p;
    i = s.LastIndexOfAny(['/', '\\']);   if (i >= 0) s = s.Substring(i + 1);
    if (!withExtension) { i = s.LastIndexOf('.');  if (i >= 0) s = s.Substring(0, i); }
    return s;                                   // null in -> null out
```

(IL of `SoftReferenceableAssets.SoftReference\`1::get_Name` and `SoftReferenceableAssets.Shared::GetFileName`
in `valheim_Data\Managed\SoftReferenceableAssets.dll`, re-read with Mono.Cecil. **Corrected:** the
original chained-`Substring` form drops both `i >= 0` guards and would throw on a path with no
separator or no dot. No shipped SoftRef path is affected — every one has both — but the offline
resolver must implement the guarded form, not the chain. Also note `get_Name` **memoises** into
`m_name`: the second read never re-resolves, so a dumper that mutates `m_assetID` in place after a read
gets the stale name.) `Runtime.GetAssetPath` goes through `IAssetLoader.GetPath`, which is backed by
the same manifest. So `Assets/world/Locations/Meadows/StartTemple.prefab` → `StartTemple` →
`GetStableHashCode()` → the per-type RNG stream seed, **all offline, from a text file**.

`SoftReference<T>.m_name` is a private field with no `[SerializeField]`, and the type tree confirms
only `m_assetID` is serialized — so the name is always derived, never stored, and the manifest is the
single source of truth. Re-verified with Cecil: the type has exactly three fields, and only
`m_assetID` carries a custom attribute, `[SerializeField]`; `m_loadedAsset` and `m_name` carry none.

### 4.3 The bundles carry type trees

Bundle format (parsed by hand in `probe/unityfs.py`): `UnityFS\0`, big-endian `int32 version = 8`,
`"5.x.x\0"`, `"6000.0.75f1\0"`, `int64 size`, `int32 compressedBlocksInfoSize`,
`int32 uncompressedBlocksInfoSize`, `int32 flags = 0x243` (= LZ4HC | directory-info-combined |
block-info-needs-padding). Blocks info: 16-byte hash, `int32 blockCount`, blocks of
`(int32 uncompressed, int32 compressed, uint16 flags)`, `int32 nodeCount`, nodes of
`(int64 offset, int64 size, uint32 flags, cstring name)`. Data blocks are LZ4 block-compressed;
a ~150-line pure-Python decoder handled all 799 bundles (4.1 GB compressed) at roughly 30 MB/s.

Inside `main.unity`'s SerializedFile the **type tree strings are present**, including the full
`ZoneSystem` MonoBehaviour layout (offset 265 300 in the decompressed blob):

```
… m_zonePrefab|m_zoneCtrlPrefab|m_locationProxyPrefab|m_waterLevel|m_locationVersion|
m_locationScenes|m_locationLists|m_altBiomeLists|PPtr<$AltBiomeList>|ZoneVegetation|m_vegetation|
m_name|m_prefab|m_enable|m_min|m_max|m_forcePlacement| … |ZoneLocation|m_locations|m_prefabName|
SoftReference`1|AssetID|m_assetID|v3|v2|v1|v0|m_quantity|m_prioritized|m_centerFirst|m_unique|
m_group|m_minDistanceFromSimilar|m_groupMax|m_maxDistanceFromSimilar|m_iconAlways|m_iconPlaced|
m_randomRotation|m_slopeRotation|m_interiorRadius|m_exteriorRadius|m_clearArea|m_minimumVegetation|
m_maximumVegetation|m_minDistance|m_maxDistance|m_didZoneTest …
```

(Fields shared with `ZoneVegetation` — `m_biome`, `m_biomeArea`, `m_snapToWater`, `m_minAltitude`,
`m_maxAltitude`, `m_minTerrainDelta`, `m_maxTerrainDelta`, `m_surround*`, `m_inForest`,
`m_forestTreshold*`, `m_minDistanceFromCenter`, `m_maxDistanceFromCenter`, `m_foldout` — do not repeat
in the string table; Unity references them by offset. A parser that assumes "new string per field"
will produce a truncated, wrong layout. Use a real type-tree reader.)

**Hand-decode proof.** At offset **1 513 248** (corrected from 1 513 240, which points 8 bytes early,
at the previous entry's `m_maxAltitude = 1000.0f` and `m_foldout`) in the decompressed `main.unity`
blob, the `StartTemple` entry reads (4-byte alignment after every bool and every string). The decode
has since been carried through all 40 fields (`probe/starttemple_decode.py`) and terminates exactly on
the next entry's `0a 00 00 00 "Eikthyrnir"` at offset 1 513 444, which is what proves the layout; the
full field values are in §3.5:

| bytes | field | value |
|---|---|---|
| `0b000000 "StartTemple" 00` | `m_name` | "StartTemple" |
| `01000000` | `m_enable` | true |
| `0b000000 "StartTemple" 00` | `m_prefabName` | "StartTemple" |
| `b1c3e624 140fd0f9 87771d4b 46d5d848` | `m_prefab.m_assetID` | = manifest `24e6c3b1f9d00f144b1d778748d8d546` (four LE uint32) |
| `01000000` `02000000` | `m_biome` / `m_biomeArea` | Meadows / Median |
| `01000000` `01000000` `01000000` `00000000` | quantity / prioritized / centerFirst / unique | 1 / true / true / false |
| `00000000` ×4 | group, minDistFromSimilar, groupMax, maxDistFromSimilar | "" / 0 / "" / 0 |
| `01000000` `00000000` ×4 | iconAlways / iconPlaced, randomRotation, slopeRotation, snapToWater | true, all false |
| `00000000` `0000c841` | interiorRadius / exteriorRadius | 0 / **25.0** |
| `01000000` `00000000` `00004040` | clearArea / minTerrainDelta / maxTerrainDelta | true / 0 / **3.0** |
| … 24 bytes … `01000000` `0000803f` `0000a040` | inForest / forestTresholdMin / forestTresholdMax | **true / 1.0 / 5.0** |
| `00000000` `00000000` `00000000` `00401c46` | minDistFromCenter / maxDistFromCenter / minDistance / maxDistance | 0 / 0 / 0 / **10000.0** |
| `00004040` `00007a44` `00000000` | minAltitude / maxAltitude / foldout | **3.0** / 1000.0 / false |

Every value is plausible and `m_iconAlways = true` is independently required by
`Game.FindSpawnPoint`, which locates the spawn via `GetLocationIcon("StartTemple")`
(`Game.m_StartLocation = "StartTemple"`, the only location name that appears as a string literal
anywhere in `assembly_valheim.dll` — Cecil scan of every `ldstr`).

### 4.4 Where the data is spread

* `main.unity` (bundle `17245031`) — the `ZoneSystem` MonoBehaviour, its own `m_locations`
  (114 of the 213 prefab names appear there as length-prefixed strings), `Minimap`, the type trees.
* `d59cfac` (106 MB, one of `main.unity`'s nine external CAB references) — the six `LocationList`
  prefabs and the `AltBiomeList` prefab; contains `m_sortOrder`, `m_addLocations`, and 194 of the 213
  prefab names. Located by resolving `main.unity`'s `archive:/CAB-…` externals against a CAB→bundle
  index built from all 799 bundles' node names (cheap: header-only parse).
* The remaining **19** of 213 names appear as a length-prefixed literal in neither blob, yet **12 of
  them are demonstrably registered `ZoneLocation`s** — they have instances in the `asdasdasd` `.db2`:
  `DN_hut01`, `FrozenShip01_DN`, `FrozenShip02_DN`, `FrozenShip03_DN`, `IcePond1`, `LumberCamp`,
  `MorkBorg`, `ShipSetting02`, `ShipSetting03`, `ShipWreck01_DN`, `ShipWreck02_DN`, `TheHole01`. Of the
  other 7, three more (`TarPit1_1`, `TarPit2_1`, `TarPit3_1`) are proven registered by the genloc log
  (`Failed to place all TarPit1_1, placed 0 out of 50`).

  **SETTLED — this is no longer an open question.** The original text offered two hypotheses; the first
  is refuted and the second is confirmed.

  *Refuted:* "Unity's string table de-duplicates a name shared with another field." Unity's
  de-duplicated string table is the **type tree's field-name table** (§4.3); serialized string
  **values** are written inline, every time. Proof: the `StartTemple` entry writes
  `0b 00 00 00 "StartTemple" 00` for `m_name` and then, 4 bytes later, `0b 00 00 00 "StartTemple" 00`
  again for `m_prefabName` — two full copies, 24 bytes apart, in `main.unity` at offset 1 513 248.

  *Confirmed:* the entries exist and only their **strings** lack the prefab name. Every one of the 19
  names has its 16-byte `AssetID` (four LE `uint32`) present in the decompressed `d59cfac` blob —
  exactly once each, and zero times in `main.unity` — while the name string itself is present zero
  times in both. Run `probe/missing19_assetids.py`; the table it prints is:
  `DN_hut01 f310f31e28f5017a29f96a2901d140f6`, `IcePond1 d087b7cd92256be4d93e0a738e4b5ce9`,
  `LumberCamp 0cff811fb1890a250821c524b24ab5c5`, `MorkBorg 0d1990dcf7e0f6abea7a16617c55f468`,
  `TheHole01 85cfea4399f9b47339f5dda3b48bee1a`, `TarPit1_1 938f2db7f5d95ce5f832becfabca1fec`,
  `TarPit2_1 de2f6cb28e3de44d881e10ffd5f8d5e0`, `TarPit3_1 159ea4c3843998c168c9c3d8a128bde9`,
  `HotSpring1 38a6aa34cea4ab04fad5f0e3ccad96d9`, `HotSpring2 7cf7393f55d3710419444c27a68c596e`,
  `HotSpring3 c7767107254b4464a83a9a3f2603a6b1`, `TheDarkestHole 7b52eb876e7e213b58a0404f73b1d6a3`,
  and likewise for the four `FrozenShip*_DN`/`ShipWreck*_DN` and two `ShipSetting0*` entries — all
  `1` occurrence in `d59cfac`, `0` name-string occurrences anywhere.

  So `HotSpring1/2/3` and `TheDarkestHole` are **not** unaccounted for: they are serialized
  `ZoneLocation` entries whose designer-set `m_name` and `m_prefabName` simply do not spell the prefab
  name (`m_prefabName` is `[HideInInspector]` and is overwritten from `m_prefab.Name` at runtime
  anyway). **The operational consequence is the one that matters: a name-string search over the bundles
  is not a valid inventory of the location table. The `AssetID` is the identity; resolve the name
  through the SoftRef manifest, exactly as the game does.** What is still open is only
  `m_enable` / `m_quantity` for these entries, which the Mode A dump settles.

### 4.5 Verdict and the work it takes

| | |
|---|---|
| **Feasible** | yes |
| **Effort** | ~2–4 days for a `Valheim.AssetReader` library: UnityFS (done, ~150 lines), SerializedFile header + type-tree reader, type-tree-driven value reader, PPtr/external resolution, MonoScript→class-name mapping to find the `ZoneSystem` / `LocationList` / `AltBiomeList` / `Minimap` / `Heightmap` behaviours. A proven off-the-shelf reader (AssetsTools.NET, or UnityPy for a Python side tool) removes most of this; both read Unity 6 bundles with type trees. |
| **Fragile where** | (a) the type-tree reader must honour Unity's align flags and the de-duplicated string table (§4.3) — get this wrong and you get plausible garbage; (b) bundle/CAB ids change every build, so the pipeline must *discover* the bundles holding the behaviours, never hard-code `17245031`/`d59cfac`; (c) if Iron Gate ever ships with type trees stripped, the whole approach collapses to "match the field layout against Mono.Cecil's field order", which is possible but far more brittle; (d) **~~the 19 unresolved names above~~ — settled, see §4.4: they are ordinary entries whose `m_name`/`m_prefabName` strings do not spell the prefab name, and the lesson is that a name-string search is not a valid inventory. The reader must key on `AssetID`.** |
| **Robust where** | the prefab **names** — the single most load-bearing piece, because they are the RNG stream keys — come from a plain UTF-8 text manifest that needs no Unity knowledge at all. Even if the bundle reader breaks, the name list survives a game update trivially. |

**Recommended policy.** Build both. The offline reader is the refresh path after a game update
(no launch, runs in CI); the in-game dumper is the oracle that validates it. Ship a `compare` command
that diffs the two dumps field by field and fails loudly on any difference — the day they disagree is
the day the type-tree reader broke, and it will not announce itself any other way.

---

## 5. Location prefab names, with evidence

Evidence codes:

* **M** — `valheim_Data\StreamingAssets\SoftRef\manifest`, "asset locations" section: an `asset ID` +
  `bundle` + `path in bundle` triple. This proves the prefab ships and gives the exact name that
  `SoftReference.Name` returns (§4.2).
* **S** — registered as a `ZoneLocation` and present in the user's world save
  `asdasdasd\_main.<N>.db2`: `m_prefabName.GetStableHashCode()` matched. The number is the instance
  count in that world (candidates, placed or not) — a **lower bound** on `m_quantity`.
* **B** — the location's own bundle was decompressed and contains the named string, proving what the
  location holds.
* **C** — a string literal in `assembly_valheim.dll`.

All 213 M-names are in `probe/loc_manifest.txt`. The 176 S-types with counts are reproduced by
`probe/hashmap.py`. Below are the ones the tool's user-facing features need.

### 5.1 Bosses

| Feature | Prefab | Evidence |
|---|---|---|
| Eikthyr | `Eikthyrnir` | M `Assets/world/Locations/Meadows/Eikthyrnir.prefab` · S 3 (1 placed) · B `Eikthyr` ×7 |
| The Elder | `GDKing` | M `.../BlackForest/GDKing.prefab` · S 4 · B `GDKing` ×6 |
| Bonemass | `Bonemass` | M `.../Swamp/Bonemass.prefab` · S 5 · B `Bonemass` ×6 |
| Moder | `Dragonqueen` | M `.../Mountains/Dragonqueen.prefab` · S 3 · B `Dragon` ×29 |
| Yagluth | `GoblinKing` | M `.../Heath/GoblinKing.prefab` · S 4 · B `GoblinKing` ×6, `Yagluth` ×14 |
| The Queen | `Mistlands_DvergrBossEntrance1` | M `.../Mistlands/Mistlands_DvergrBossEntrance1.prefab` · S 5 · B `Queen` ×1 |
| Fader | `FaderLocation` | M `.../Ashlands/FaderLocation.prefab` · S 3 · B `Fader` ×82 |
| Deep North boss | `DN_Bossroom` | M `.../DeepNorth/DN_Bossroom.prefab` · S 3 · B `Boss` ×161. **Unverified: the boss's identity** — the bundle names it only as `…Boss…`. Ship the prefab name, not a guess at the creature. |

`CharredFortress` also contains `Fader` ×2 (B) — it is the Ashlands fortress, not Fader's arena; do not
confuse them.

### 5.2 Traders and services

| Feature | Prefab | Evidence |
|---|---|---|
| Haldor | `Vendor_BlackForest` | M `.../BlackForest/Vendor_BlackForest.prefab` · S 10 · B `Haldor` ×6, `Vendor` ×2 · icon entry in `Minimap.m_locationIcons` |
| Hildir | `Hildir_camp` | M `.../Meadows/Hildir_camp.prefab` · S 10 · B `Hildir` ×326 · icon entry |
| The Bog Witch | `BogWitch_Camp` | M **`Assets/world/Props/BogWitchHut/BogWitch_Camp.prefab`** (note: under Props, not Locations) · S 10 · B `BogWitch` ×183 · icon entry |
| Ancient upgrade station | `AncientUpgradeStation` | M `.../Mountains/AncientUpgradeStation.prefab` · S 10 · B `Upgrade` ×7 · icon entry |
| Start temple | `StartTemple` | M `.../Meadows/StartTemple.prefab` · S 1 (placed) · C `Game.m_StartLocation` · icon entry · `m_iconAlways = true` decoded from the bundle (§4.3) |

Those five are **exactly** the contents of `Minimap.m_locationIcons` (§1.5), i.e. the only locations
that can draw a map pin from `ZoneSystem.GetLocationIcons`. Bosses are *not* among them — they are
revealed by Vegvisir/runestone through `Game.DiscoverClosestLocation`, which is a server query over all
registered instances. The tool can and should report boss positions; the game just does not put them on
the map for free.

### 5.3 Dungeons

| Feature | Prefabs | Evidence |
|---|---|---|
| Burial chambers | `Crypt2`, `Crypt3`, `Crypt4` | M `.../BlackForest/Crypt{2,3,4}.prefab` · S 200/200/175 · B `forestcrypt` ×6, `Skeleton` ×3 each |
| Troll caves | `TrollCave02` | M `.../BlackForest/TrollCave02.prefab` · S 200 · B `Troll` ×6, `trollcave` ×2 |
| Bear cave | `BearCave` | M `.../BlackForest/BearCave.prefab` · S 50 · B `Bear` ×3 |
| Sunken crypts | `SunkenCrypt4` | M `.../Swamp/SunkenCrypt4.prefab` · S 175 · B `Draugr`, `sunken` |
| Swamp grave | `Grave1` | M `.../Swamp/Grave1.prefab` · S 200 · B `Draugr` ×5 |
| Frost caves | `MountainCave02` | M `.../Mountains/MountainCave02.prefab` · S 120 · B `DG_Cave`, `Interior`, `Gateway`, `Teleport` |
| Infested mines | `Mistlands_DvergrTownEntrance1`, `Mistlands_DvergrTownEntrance2` | M `.../Mistlands/…` · S 120 each · B `Seeker` ×5 / ×4 |
| Infested trees | `InfestedTree01` | M `.../Swamp/InfestedTree01.prefab` · S 700 · B `infested` ×2 |
| Dvergr excavations | `Mistlands_Excavation1/2/3` | M · S 40 each · B `Dvergr` ×713 |
| Morgen holes | `MorgenHole1/2/3` | M `.../Ashlands/…` · S 40 each · B `Morgen` ×4 |
| Hildir's trials | `Hildir_cave` (Mountains), `Hildir_crypt` (BlackForest), `Hildir_plainsfortress` (Plains) | M each · S 3 each · B `Hildir` ×4 / ×4 / ×8 |
| Deep North holes | `TheHole01`, `TheDarkestHole` | M `.../DeepNorth/…` · S: `TheHole01` 40; `TheDarkestHole` has **no instance** in the save (§5.5) |
| Half-buried crypt | `HalfBurried_ForestCrypt` | M `.../BlackForest/HalfBurried_ForestCrypt.prefab`; **no instance** in the save. Its `AssetID` `168f3b074cdb8e644824f1b6cbed08df` **is** present once in `d59cfac` (`probe/missing19_assetids.py`), and so is its name string — so a `SoftReference<GameObject>` to it is serialized in the `LocationList`/`AltBiomeList` blob. That makes "only a room set" unlikely, but a `SoftReference` field alone does not prove the containing record is a `ZoneLocation` rather than some other serialized reference. **Unverified:** its `m_enable` and `m_quantity` (a disabled or zero-quantity entry explains the absent instances). One Mode A dump settles it. |

### 5.4 Camps, resources, landmarks

| Feature | Prefabs | Evidence |
|---|---|---|
| Greydwarf camps | `Greydwarf_camp1` | M · S 300 |
| Fuling villages | `GoblinCamp2` (S 190), `GoblinCamp2_1` (S 5, 0 placed — the AltBiome variant) | M · B `Fuling` ×1, `Goblin` ×3 |
| Fuling huts | `GoblinHut01/02/03` (S 30 / 14 / 1), `GoblinHut01..03` also in Meadows dir | M |
| Surtling geysers | `FireHole` | M `.../Misc/FireHole.prefab` · S 75 · B `Spawner_imp_respawn` (surtlings are "imp" internally), `FireBurn`, `FireWarmth` |
| Tar pits | `TarPit1/2/3` (S 60 / 16 / 100) and `TarPit1_1/2_1/3_1` (S 50 / 20 / 100, all 0 placed) | M `.../Plains/…` · B `Tar` ×32, `Blob` ×9 |
| Charred fortress | `CharredFortress` | M `.../Ashlands/CharredFortress.prefab` · S 20 · B `Charred` ×145 |
| Charred spawners | `CharredStone_Spawner` | M · S 300 · B `Charred` ×27 |
| Volture nests | `VoltureNest` | M · S 350 · B `Volture` ×12 |
| Drake nests | `DrakeNest01` | M · S 200 · B `Drake` ×6 |
| Stone circles | `StoneCircle` | M `.../Misc/StoneCircle.prefab` · S 25 (2 placed). Also the global key `StoneCircle` (`ZoneSystem.GlobalKeys`, C). |
| Runestones | `Runestone_{Meadows,Boars,BlackForest,Greydwarfs,Swamps,Draugr,Mountains,Plains,Mistlands,Ashlands,DeepNorth}` | M · S 100/50/50/25/100/50/67/100/50/70/70 |
| Deep North village | `NorthVillage` | M · S 57 |
| Fimbul / Mork Borg / lumber camp | `FimbulLocation01`, `MorkBorg`, `LumberCamp` | M; S: `MorkBorg` 40, `LumberCamp` 50, `FimbulLocation01` **absent from the save** |
| Places of Mystery | `PlaceofMystery1/2/3` | M `.../Ashlands/…` · S 1 each |

**Ore deposits are not locations.** Copper, tin, silver, obsidian, flametal and the Mistlands/Ashlands
rock nodes are `ZoneVegetation` entries placed per zone during zone generation
(`ZoneSystem.PlaceVegetation`), keyed by `veg.m_prefab.name`, and gated by **physics raycasts** against
whatever colliders happen to be loaded. They are therefore *seed-driven but not guaranteed
bit-identical*, and their names are not in the SoftRef manifest (the vegetation prefab is a plain
`GameObject` reference, not a `SoftReference`). The dumper's `vegetation.json` is the only reliable
source for those names. Say so in the tool's UI rather than promising ore locations.

### 5.5 What the counts do and do not prove

* **213** location prefabs ship (M): 212 under `Assets/world/Locations/` plus `BogWitch_Camp` under
  `Assets/world/Props/BogWitchHut/`.
* **183** is the number of `ZoneLocation` entries with `m_enable == true && m_quantity != 0` that the
  placement loop actually processed: `LogOutput.log`, 2026-09-22 20:37:57 —
  `Loading: Done. … (Total genloc time: 00:00:38.1089140, locations: 183, iterations: 0)`.
* **176** distinct types registered ≥1 instance in `asdasdasd` (S). Exactly **37** manifest names have
  no instance there. The arithmetic is consistent: 213 − 183 = 30 prefabs that are not registered as
  `ZoneLocation`s at all or are disabled / quantity 0, plus 7 enabled types that happened to place
  nothing in that world — 30 + 7 = 37. (Consistent with, not proof of: it assumes an unmodded list with
  no duplicate prefabs. The Mode A dump settles it outright by printing `m_enable` and `m_quantity`.)
* The 37 absent names, in full:
  `DevBedchamber`, `DevCombatRange`, `DevCombatRing`, `DevDressingRoom`, `DevFloor1`, `DevForge`,
  `DevGarden`, `DevGround1`, `DevGround2`, `DevHouse1`–`DevHouse5`, `DevHouseStart`, `DevKitchen`,
  `DevMageRoom`, `DevSoundTest`, `DevWall1`, `DevWall2`, `DevWallAsh` (21 developer test rooms),
  `FimbulLocation01`, `HalfBurried_ForestCrypt`, `HotSpring1`, `HotSpring2`, `HotSpring3`,
  `StoneHouse1_heath`, `StoneHouse2_heath`, `StoneHouse5_heath`, `TheDarkestHole`,
  and seven that the game's own log proves **are** enabled and simply placed 0:
  `GoblinCamp2_1` (0/5), `StoneTowerRuins05_leet` (0/10), `SwampHut2_1` (2/50 in another world),
  `SwampHut3_1` (2/50), `TarPit1_1` (0/50), `TarPit2_1` (0/20), `TarPit3_1` (0/100).
  The `Dev*` family should be hidden from the tool's UI by name prefix but **kept in the data** — an
  update could enable one and every later type's outcome would shift.
* The S counts are per-world outcomes, **not** `m_quantity`. Where the log says
  `Failed to place all X, placed a out of b`, `b` **is** `m_quantity`: e.g. `Crypt4` 200, `TarPit1` 100,
  `MountainCave02` 120, `NorthVillage` 135, `Runestone_Mountains` 100, `MountainWell1` 25,
  `AbandonedLogCabin04` 50, `SwampHut1_1` 50, `WoodVillage2` 15, `TarPit2` 16,
  `StoneTowerRuins05` 50, `Mistlands_Giant2` 85, `MorgenHole3` 40, `GoblinHut02` 30, `GoblinHut03` 20.
* The two worlds in today's log differ in which types placed nothing (`StoneTowerRuins10_sunk` placed
  0/10 in the 20:37 world but has 6 instances in `asdasdasd`), which is the expected consequence of
  §3.6.4's caveat: an S count is one sample of one seed, not a property of the type.

---

## Appendix A — probes run for this document

All in `scratchpad\probe\`, all read-only with respect to game and save data.

| Script | What it established |
|---|---|
| `mmcache.py` | minimap cache is gzip; 2048² for all three textures; meta = (seed −1772362158, version 1) |
| `pixelsize.py` | the seven distinct biome colours in the user's cache |
| `fitpx.py` | `m_pixelSize = 12.0` exactly: 0/262144 mismatches against the closed-form Ashlands test |
| `unityfs.py` | UnityFS v8 parser (LZ4 block decoder in pure Python); decompresses any of the 799 bundles |
| `dbg.py` | header/blocks-info debugging for the above |
| `cabmap.py` | header-only scan of all 799 bundles → CAB name → bundle id index |
| `scan.py`, `scanbig.py` | streaming needle search across bundles; located `d59cfac` |
| `hashmap.py` | maps every location hash in `asdasdasd\_main.<N>.db2` to a manifest prefab name (176 distinct, 0 unknown) |
| `starttemple_decode.py` | **added by review.** Byte-exact decode of all 40 `ZoneLocation` fields of `StartTemple` at offset 1 513 248 in `main_unity.bin`; terminates on `"Eikthyrnir"` at 1 513 444, which proves the field layout. Source of the corrected §3.5 example. |
| `altbiome_decode.py` | **added by review.** Decodes `m_name`/`m_enabled`/`m_biome`/3 name strings/`m_levelUpChanceMultiplier` for all 28 AltBiome records in `d59cfac.bin`; 28/28 clean, all `m_enabled = 1`. |
| `missing19_assetids.py` | **added by review.** Parses the SoftRef manifest, then searches `main_unity.bin`/`d59cfac.bin` for each prefab's 16-byte `AssetID`. Settles §4.4: all 19 "missing" names are present as `AssetID`s, absent as strings. |
| `chk_rev.py` | **added by review.** Independent `GetStableHashCode` implementation; confirms `"StartTemple" → -1544986047` and `"MWd8eV6svz" → -1772362158`, and the `AssetID` uint/hex round trip. |

Mono.Cecil (via PowerShell) was used for: `SoftReference\`1::get_Name` and `Shared::GetFileName` IL,
the `Version.*` enums, and the `ldstr` scan of `assembly_valheim.dll` for location names.

## Appendix B — facts settled here, for the knowledge base

1. `Minimap.m_textureSize = 2048`, `m_pixelSize = 12.0` in the shipped prefab (code defaults 256 / 64).
   Evidence: cache file sizes + the Ashlands fit. Corroborated by `AltBiomeWorldData.c_textureSize`/
   `c_pixelSize`.
2. `ZoneSystem.m_locationVersion = 32` in the shipped prefab (code default 1). Evidence: the user's
   `_main.<N>.db2`.
3. Vanilla ships **28 enabled AltBiomes** (names, biomes and `m_levelUpChanceMultiplier` in §1.3;
   re-verified record by record by `probe/altbiome_decode.py`, all 28 with `m_enabled = 1`,
   `m_namePrefix`/`m_nameSuffix`/`m_nameOverride` all `""`). This replaces the
   "**Unverified:** whether vanilla ships any enabled AltBiomes" entry in
   `valheim-worldgen/references/zones-locations-vegetation.md` §3.4.
4. `AltBiomeWorldData.GenerateAltBiomes` never clears `AltBiome.Sectors`; running it twice in one
   process (`genloc alt`, or a multi-seed dumper) starves the second run. New pitfall.
5. `AltBiomeWorldData.GenerateSectors` sets `sector.MaxZone = ZoneSystem.GetZone(new Vector3(Min.x, Min.y))`
   — `Min` where `Max` was meant, and a `Vector3(x, y)` whose `z` is 0 while `GetZone` reads `p.z`. So
   `MinZone == MaxZone` always, `ZoneCount == 1`, and the `IsDiscovered` loop body never executes
   (which is why `GenerateSectors` can run without a `ZoneSystem` instance). A port must reproduce this.
6. `SoftReference<T>.Name` = filename-without-extension of the SoftRef manifest path; the manifest is
   plain text and readable offline. The location-name → hash → RNG-stream chain is fully offline.
7. `Minimap.m_locationIcons` has exactly 5 entries: `StartTemple`, `Vendor_BlackForest`, `Hildir_camp`,
   `BogWitch_Camp`, `AncientUpgradeStation`.
8. `cacheMinimapBiome` is lossy: Ocean, Mountain and DeepNorth all render `#ffffff`.
9. `WorldGenerator.GetBiomeHeight` computes `BiomeSector biomeSector = GetBiomeSector(wx, wy)` and never
   uses it — so height is independent of alt-biome data, and the minimap height cache is a clean
   ground truth for a pure function.
10. `ZoneSystem.m_locationScenes` is dead: the only reference is the `stfld` in `ZoneSystem..ctor`.
11. Location list provenance in 1.0.15: 6 `LocationList`s contributing 3/2/27/4/25/25 locations and
    0/0/25/0/33/35 vegetation entries, all from the `main` scene; 183 enabled location entries total.
    (Re-read from `BepInEx\LogOutput.log`, both world loads of 2026-09-22 agree. Note the log line is
    `... from " + item.gameObject.scene.name`, i.e. the scene the *instantiated* `LocationList` lives
    in — always `main`, because `ZoneSystem.Awake` instantiates them there. It says nothing about which
    bundle the prefab came from.)
12. `AltBiomeWorldData.GenerateAltBiomes` iterates a 12-key dictionary, not the 9 biomes: the keys are
    `Enum.GetValues(typeof(Heightmap.Biome))`, which includes `None = 0`, `Land = 0x27F` and
    `All = 0x37F`, and `HasFlag(None)` is true for every value — so every enabled AltBiome runs an
    extra `InitState` + `Shuffle` + per-sector `Range(0f,1f)` pass under the `None` key. This is what
    makes the log report `combos: 2` for a single-biome AltBiome. §1.3.
13. `GenerateAltBiomes` opens with `UnityEngine.Random.InitState(WorldGenerator.instance.GetSeed() + 920)`
    and shuffles `BiomeTypeInfo.Sectors` **in place** once per (biome, altBiome) pair. Any code that
    reads a per-biome sector list twice gets two different orders. §1.3, §3.3 H2.
14. A prefab-name string search over the shipped bundles is **not** a valid inventory of the location
    table: 19 of the 213 location prefabs are referenced only by `AssetID`, their `m_name` and
    `m_prefabName` strings never containing the prefab name. §4.4. New pitfall.
15. `Utils.GenerateUID` draws one `UnityEngine.Random.Range(1, int.MaxValue)` (`Utils.cs:424`), and
    `World..ctor(name, seed)` calls it — so constructing a `World` perturbs the ambient RNG stream.
    §3.4. New pitfall.
16. `WorldGenerator.Initialize` calls `CleanCachedRiverData()` on the **outgoing** instance, clearing
    its `m_riverPoints`/`m_rivers`/`m_streams`. A saved `WorldGenerator` reference cannot be restored
    by assigning it back to the static — it must be re-created from its `World`. §3.7 Invariant 0.
    New pitfall.

## Appendix C — open items

| Item | Why it is not settled | What would settle it |
|---|---|---|
| Zone `Heightmap.m_width` / `m_scale` | prefab data; not derivable from code, and the zone prefab was not located in the bundles for this document | one Mode A dump (H5), or the offline reader |
| Which vanilla locations have a non-zero `DungeonGenerator` local offset | prefab data inside 213 SoftRef prefabs | Mode A prefab walk (§3.3) |
| ~~Why 19 manifest names have no literal in `main.unity`/`d59cfac`~~ | **CLOSED by review, §4.4.** They are referenced by `AssetID` only; their `m_name`/`m_prefabName` strings do not spell the prefab name. Evidence: `probe/missing19_assetids.py` (19/19 AssetIDs present once in `d59cfac`, 0 name-string hits), plus the refutation of the string-table hypothesis from the `StartTemple` decode. | — |
| `DN_Bossroom`'s boss identity | the bundle only exposes `…Boss…` strings | load the prefab in Mode A and read the component/creature names |
| Whether two `LocationList`s share an `m_sortOrder` (unstable-sort hazard) | the log does not print sort orders | Mode A dump records them (§1.4); manifest flag `sortOrderTies` |
| Exact `m_quantity` for every type | only the failing ones appear in the log | Mode A dump |
| `m_locations.Count` and `m_vegetation.Count` after `SetupLocations` | **added by review.** The manifest example asserted 213/118; neither is measured, and 213 is the SoftRef *prefab* count, not the entry count. §3.5 | Mode A dump (H1) |
| Enumeration order of `AltBiomeWorldData.Biomes` (`Dictionary<Heightmap.Biome, BiomeTypeInfo>`) | **added by review.** It drives the order of `GenerateAltBiomes`' `InitState`/`Shuffle` passes. Insertion order = `Enum.GetValues` order in practice, but `Dictionary` enumeration order is not a documented contract. §1.3 | Mode A / H2 records the observed key order; a port must then hard-code it, not re-derive it |
| Whether `m_enable` / `m_quantity` are non-zero for `HalfBurried_ForestCrypt`, `HotSpring1/2/3`, `TheDarkestHole` | **narrowed by review.** All are serialized entries (AssetID evidence, §4.4/§5.3); only the two flags are unknown, and a disabled or zero-quantity entry would explain their absence from the save | Mode A dump |

---

## Verification

Independent adversarial check of this document against the decompiled sources in `scratchpad\decomp`,
the shipped data in `valheim_Data`, the user's read-only ground truth, and `BepInEx\LogOutput.log`.
Every claim below was re-derived from the source or from the bytes, not taken from the text it checks.

### Corrected in place (each with its evidence)

| § | Was | Is | Evidence |
|---|---|---|---|
| 3.5 | `StartTemple` example: `inForest false`, `forestTresholdMin 0.0`, `forestTresholdMax 1.0`, `maxDistance 0.0`, **`minAltitude -1000.0`** | `true`, `1.0`, `5.0`, `10000.0`, **`3.0`** | Byte decode of all 40 fields at `main_unity.bin` offset 1 513 248; the decode terminates on `"Eikthyrnir"` at 1 513 444. `probe/starttemple_decode.py`. **`m_minAltitude >= 0` flips the zone draw to `GetRandomPointByBiomesAboveSeaLevel` (`ZoneSystem.cs:1919`)** — the single most consequential error found. The five wrong values were the *code defaults*, i.e. exactly the mistake §1 forbids. |
| 3.5 | `assetId` v3..v0 = 617632689 / 4190638356 / 1259608967 / 1222067014 | 619103153 / 4191162132 / 1260222343 / 1222169926 | `struct.unpack("<4I", bytes.fromhex("b1c3e624140fd0f987771d4b46d5d848"))`; the old four do not reproduce the `hex` field printed beside them. SoftRef manifest line 10380 confirms that hex resolves to `Assets/world/Locations/Meadows/StartTemple.prefab`. |
| 3.7 | "...or restore `m_instance` directly" | New Invariant 0: you cannot; re-`Initialize` from the saved `World` | `WorldGenerator.Initialize` runs `m_instance?.CleanCachedRiverData()` on the **outgoing** instance, clearing `m_riverPoints`/`m_rivers`/`m_streams` and nulling `m_cachedRiverPoints`. Restoring the object hands the session a river-less generator — plausible, self-consistent, wrong, and silent. |
| 1.2, 3.1 | `ZoneVegetation` has "38 serialized fields" | **40** | `ZoneSystem.cs:34-145`; the document's own field list already named 40. |
| 3.6.2 | `coarse128` (-10496..10496 / 128) is "exactly the lattice `FindLakes` walks" | The two lattices are **disjoint** | `WorldGenerator.FindLakes`: `for (float y = -10000f; y <= 10000f; y += 128.0)`. `-10000 + 128k` never equals `-10496 + 128m` because 10000 is not a multiple of 128. A separate `findlakes` grid was added. |
| 3.6.2 | `full12` extent -12288..12288 | **-12282..+12282** | `AltBiomeWorldData.MapSpaceToWorldSpace(x) = (x - 1024f) * 12f + 6f`; j=0 gives -12282, j=2047 gives +12282. With the stated half-pixel offset, +/-12288 is a different lattice from the cache. |
| 3.6.1 | ctor draws at `WorldGenerator.cs:222-228` | **223-229** (222 is `m_noiseGen.SetSeed(0)`) | grep of the decompiled constructor. |
| 3.6.1 | private fields "declared `WorldGenerator.cs:54-86`" | **60-80** for the fields actually listed | 54 is the *static* `m_instance`, 56 the *public* `m_world`, 82-86 the cache/lock fields. |
| 1.1 r1 | `m_name` is "label only ... not the RNG key" | Not the RNG key, but **read by a placement filter**: `x.m_blockLocationNames.Contains(location.m_name)` | `ZoneSystem.cs:2039`, and `:1417` for the vegetation equivalent. |
| 1.1 r18/r23 | one `GetTerrainDelta` | **two** same-named methods: `ZoneSystem.GetTerrainDelta` (`:2701`, physics `GetGroundHeight`, used at spawn for `m_slopeRotation`) and `WorldGenerator.GetTerrainDelta` (`WorldGenerator.cs:1418`, pure `GetHeight`, used by the placement filter at `ZoneSystem.cs:2001`) | Both draw 10 x `Random.insideUnitCircle`; the filter call is unconditional, with no guard on the field values. |
| 1.5 | `m_locationIcons` at offset 1 939 275 | **1 939 300** (the count field; 1 939 275 is not 4-aligned) | Re-decoded: the five names and their 12-byte `PPtr{int32 fileID=7, int64 pathID}` parse cleanly from there and nowhere near 1 939 275. |
| 4.3 | `StartTemple` entry at offset 1 513 240 | **1 513 248** | 1 513 240 points 8 bytes early, at the previous entry's `m_maxAltitude = 1000.0f` and `m_foldout`. |
| 3.5 | `counts.locations: 213`, `vegetation: 118` | 213 is the SoftRef **prefab** count, not `m_locations.Count`; both entry counts are unmeasured | 587 manifest assets, 212 under `Assets/world/Locations/`, plus `BogWitch_Camp` under `Assets/world/Props/BogWitchHut/` = 213. Only 86 locations / 93 vegetation from the `LocationList`s are in evidence. |
| 2.3 | client fallback is `BiomeSector.EmptyMeadows` | **`EmptyBlackForest`** | `WorldGenerator.cs:845-854` tests `m_world.m_biomeData == null` first and returns `EmptyBlackForest`; `EmptyMeadows` is only the non-null-but-`!IsReady` arm. A client never has `m_biomeData` assigned. |
| 4.2 | `Shared.GetFileName` as a bare `Substring` chain | the guarded form, both `i >= 0` tests; plus `get_Name` memoises into `m_name` | Cecil IL of `SoftReferenceableAssets.SoftReference\`1::get_Name` and `Shared::GetFileName`. |
| 4.4 | two hypotheses for the 19 missing names, marked Unverified | **settled**: the first is refuted, the second confirmed | see below. |

### Added (material, and establishable from evidence)

* **§1.3** `GenerateAltBiomes` opens with `UnityEngine.Random.InitState(WorldGenerator.instance.GetSeed() + 920)`
  and calls `biome.Value.Sectors.Shuffle()` once per (biome, altBiome) pair. Neither was in the
  document, and both are RNG-consumption points. Propagated to §3.3 H2: record sectors by index into
  `AltBiomeWorldData.Sectors` (append-only), never by position in a shuffled `BiomeTypeInfo.Sectors`.
* **§1.3** The `Biomes` dictionary has **twelve** keys, not nine — `Enum.GetValues(typeof(Heightmap.Biome))`
  also yields `None = 0`, `Land = 0x27F` and `All = 0x37F`, and `HasFlag(None)` is true for every
  value. That is why the log reports `combos: 2` for a single-biome AltBiome, and a port that iterates
  nine biomes desynchronises the stream.
* **§1.5** `m_mistlandsColor` is `private Color` with no `[SerializeField]` (`Minimap.cs:274`): Unity
  does not serialize it, the prefab cannot override it, its value is guaranteed `(0.2,0.2,0.2)`, and
  H4 must read it by reflection. The other seven colour fields are `public` and overridable.
* **§1.7** `DungeonGenerator.GetSeed` quoted verbatim (the formula in the document was exactly right),
  plus three traps it hid: the zone term uses the **generator's** transform, `(int)` is a truncation
  toward zero rather than a floor, and the result is memoised behind `m_hasGeneratedSeed` with a
  self-consuming `m_forceSeed` override.
* **§1.8** `Minimap.SaveMapTextureDataToDisk` writes a hard-coded literal `1`, not
  `(int)Version.c_CachedMinimapVersion`, while the reader compares against the enum.
* **§2.2 step 5** `SetupLocations` itself `Load()`s, holds and `Release()`s every enabled location
  prefab when `AssetMemoryUsagePolicy.KeepAsynchronousLoadedBit` is set — which changes §3.3's
  prefab-walk cost estimate from "213 synchronous loads" to nearly free.
* **§3.4** `Utils.GenerateUID` confirmed to draw (`Utils.cs:424`, `UnityEngine.Random.Range(1, int.MaxValue)`)
  and `World..ctor` calls it, so `new World(...)` perturbs the ambient stream; the review states why
  the Mode B loop survives that today and under what edit it would stop surviving. Also confirmed
  `FastNoise..ctor` does **not** draw, so the once-per-process static `m_noiseGen` creation does not
  shift the seven constructor draws for the first world.
* **§3.6.1** The `FastNoise` is `SetSeed(0)` unconditionally at line 222 — the world seed never
  reaches the cellular generator.
* **§3.7 Invariant 2** `sector.AltBiomes` needs no reset (fresh `BiomeSector`s per grid), and the four
  static `BiomeSector.Empty*` singletons are unreachable from `AddModifier` because they are built
  with `world == null` and are never added to a `BiomeTypeInfo.Sectors`.

### Re-derived and found CORRECT (no change)

`WorldGenerator..ctor` draw order (offset0-3, riverSeed, streamSeed, offset4 — seven draws, offset4
last) - `ZoneSystem.cs:1880` stream seed `GetSeed() + m_prefab.Name.GetStableHashCode()` - attempts
60000/12000 - the `m_minAltitude < 0f` branch at `:1919` - `GetTerrainDelta` = 10 draws -
`GetRandomPointInZone` inset `Range(-32f + r, 32f - r)` - `m_snapToWater` sets `p.y = 30f` -
`m_randomRotation` = `Range(0, 16) * 22.5f` - `InsideClearArea` is an axis-aligned **square** of
half-size `m_exteriorRadius` - `ZoneSystem.Save` hashes `m_prefabName` (`:1010`) - `m_locationIDCache`
keyed by `AssetID` (`HaveLocationInRange`) - the `ZoneLocation` 40-field table **and its order**
(proven outright by the byte decode) - the `ZoneVegetation` field list and order - `m_max < 1` means
chance-of-one, `m_forcePlacement` means tries x 50, the `+/-(32 - m_groupRadius)` inset - `ZoneSystem`
scalars (`m_zoneSize 64f`, `m_waterLevel 30f`, `m_locationVersion 1`, `m_zoneTTL`/`m_zoneTTS` 4f,
`c_ZoneSize`, `c_WaterLevel`) - `Minimap.m_textureSize 256` / `m_pixelSize 64f` code defaults and the
`GenerateWorldMap` half-pixel formula - `Heightmap.m_width 32` / `m_scale 1f` code defaults and
`RequestTerrainSync`'s signature - `Version.World.DeepNorth 41`, `CachedMinimap.Original 1`,
`Player.DeepNorth 46`, `c_networkVersion 40u` - `TryLoadMinimapTextureData`'s exact-equality world-version
gate - `AltBiomeWorldData` constants 2048 / 12f / 1024 / 6f and `MapSpaceToWorldSpace` -
`VerifyBiomeData = RemoveCache + GenerateBiomePoints + GenerateSectors`, so Invariant 1 stands -
`GenerateAltBiomes` resets only the two counters and never `Sectors`, so Invariant 2 stands -
`Heightmap.cs:430` `m_buildData.m_worldGen != WorldGenerator.instance`, so Invariant 3 stands -
`IsAshlands` closed form including the fact that `WorldAngle` takes `(x, y)` while the length term
takes `(x, y + ashlandsYOffset)`, and `ashlandsMinDistance 12000f` / `ashlandsYOffset -4000f` /
`deepNorthYOffset 4000f` - `WorldAngle = Sin((float)((float)Atan2(wx,wy) * 20.0))` - `Pregenerate`
discards `PlaceStreams(isDN: true)`'s return so `m_streams` holds only non-DeepNorth streams -
`GetBiomeHeight` computes `GetBiomeSector` and never uses it (App. B item 9) - `GenerateSectors` sets
`MaxZone` from `Min`, so `MinZone == MaxZone`, `ZoneCount == 1` and the `IsDiscovered` loop body never
runs (App. B item 5) - `FejdStartup.cs:318` and `:2961` - `ZoneSystem.Awake` 673 / `Start` 691 - the
`GenerateLocationsCompleted` event's add-accessor invoking immediately when already generated -
`Random.state` save/restore sites `1383`/`1584`, `1881`/`2139`, `1912-1917`, `1945-1950` -
`ZNet.GetPeers()`, `IsServer()`, `IsDedicated()`, `GetPeerConnections()` (returns `int`) and
`ZNet.World` all exist - `ZInput.GetKeyDown(KeyCode, bool)` exists - `Newtonsoft.Json.dll` ships in
`valheim_Data\Managed`.

Re-measured from data rather than from the text: `GetStableHashCode("StartTemple") = -1544986047` and
`GetStableHashCode("MWd8eV6svz") = -1772362158`; `cacheMinimapBiome` and `cacheMinimapMask` gunzip to
16 777 216 B = 2048^2 x 4, `cacheMinimapHeight` to 8 388 608 B = 2048^2 x 2, `cacheMinimapMeta` is
`52 e6 5b 96 | 01 00 00 00` = (-1772362158, 1); the log's six `LocationList`s contribute
3/2/27/4/25/25 locations and 0/0/25/0/33/35 vegetation, and `locations: 183`; exactly one AltBiome
warning line (`Fortress Mountain`, `combos: 2`); 587 SoftRef assets, 212 under `Locations/`, 213 with
`BogWitch_Camp`; `Minimap.m_locationIcons` holds the five named entries in the order given.
**All 28 AltBiome records were decoded individually: 28/28 with `m_enabled = 1`, biomes exactly as
grouped in §1.3, offsets 5 156 960 to 5 181 556 as stated, and the `(xN)` annotation confirmed to be
`m_levelUpChanceMultiplier` (seven at 2.0, `Fortress Mountain` at 3.0, the other twenty at 1.0).**

**§4.4 settled.** The "Unity string table de-duplicates a name shared with another field" hypothesis is
**refuted**: Unity's de-duplicated table is the type tree's field-name table, and serialized string
*values* are written inline every time — the `StartTemple` entry writes `"StartTemple"` twice, 24 bytes
apart, once for `m_name` and once for `m_prefabName`. The second hypothesis is **confirmed**: all 19
names have their 16-byte `AssetID` present exactly once in `d59cfac` and zero times in `main.unity`,
while the name string appears zero times in both (`probe/missing19_assetids.py`). The operational
consequence is the one that matters — a prefab-name string search over the bundles is not a valid
inventory of the location table; key on `AssetID` and resolve the name through the SoftRef manifest,
as the game does.

### Still unverified after this review

* Zone `Heightmap.m_width` / `m_scale` on `ZoneSystem.m_zonePrefab` — prefab data, not reachable from
  the IL or from the two blobs decompressed for this document. Only the code defaults (32 / 1f) are
  established. **Do not ship them as the game's values.**
* Which vanilla locations have a non-zero `DungeonGenerator` local XZ offset.
* `m_quantity` and the per-entry flags for the 183 enabled entries. Only the failing ones appear in the
  log, and only `StartTemple` has been decoded byte-exactly.
* `m_locations.Count` / `m_vegetation.Count`, and the `index` / `orderedIndex` / `source` of any entry.
* The enumeration order of `AltBiomeWorldData.Biomes` in `GenerateAltBiomes`.
* `DN_Bossroom`'s boss identity; whether two `LocationList`s tie on `m_sortOrder`.
* `m_enable` / `m_quantity` for `HalfBurried_ForestCrypt`, `HotSpring1/2/3`, `TheDarkestHole` (all are
  now known to be serialized entries; only the two flags are open).
* Whether `Utils.ColorsToCompressedBuffer`'s gzip output and `Mathf.FloatToHalf` round-trip identically
  on .NET 10 — still assumed, not measured. The cache decode done here used Python's `gzip`, which
  proves the container and the sizes, not the .NET encoder's byte-for-byte output.
* §1.5's `m_pixelSize = 12.0` fit and §2.1's scene/bundle string counts were **not** re-run; they rest
  on `probe/fitpx.py` and `probe/unityfs.py` as reported. The independent corroboration from
  `AltBiomeWorldData.c_pixelSize = 12f` and the 2048^2-sized cache files does hold.
* The `asdasdasd` `.db2` figures (12 314 instances, 176 distinct types, location version 32, 112 zones
  generated) were **not** re-read; they rest on `valheim_saves.py` as reported.
* §3.6.3 still states spec 03's requirements as an assumed interface contract. Spec 03 now exists
  (`specs/03-unity-natives.md`); its list was not diffed against §3.6.3 in this review.

*— checked by an independent reviewer, 2026-09-22*
