# The offline algorithm `locationchildren.json` enables

Written 2026-09-23 alongside the prefab-child walk in `tools\SeedLab.Dumper`. Everything below is from
the decompiled 1.0.15 assemblies (`decompile.ps1`), not from memory. Where something is **not**
computable offline it says so in as many words — that is the point of the last section.

Nothing here has been run against a real dump yet: the dumper has been built and preflighted, but the
child walk has not been executed (the user runs it). Treat the arithmetic as derived, the numbers as
pending.

**Revised 2026-09-23, second pass.** The first pass walked LOCATION prefabs only, and that turned out
to be the wrong shape for the question it was built to answer - see section 7. The dump now also walks
every dungeon/camp ROOM prefab, normalises every name the way the game does, and writes an explicit
FOUND / NOT FOUND verdict per sought name (section 8). Read sections 7 and 8 before section 4: the
maypole hypothesis changed.

---

## 1. Getting to the zone seed

SeedLab already reproduces location placement bit-exactly, so for a seed it knows, for every placed
location: the zone `(zx, zy)`, the `ZoneLocation` it is, and `m_position`.

`ZoneSystem.PlaceLocations` (assembly_valheim, 1.0.15) then does, in this order:

```csharp
Vector3 p = instance.m_position;
GetGroundData(ref p, out _, out _, out _, out _);       // p.y <- terrain raycast, see §5(b)
if (location.m_snapToWater) p.y = 30f;
Quaternion rot = Quaternion.identity;
if (location.m_slopeRotation)  rot = <yaw from the terrain slope, snapped to 22.5 deg>;
else if (location.m_randomRotation) rot = Quaternion.Euler(0, Random.Range(0,16) * 22.5f, 0);
int seed = WorldGenerator.instance.GetSeed() + zoneID.x * 4271 + zoneID.y * 9187;
SpawnLocation(location, seed, p, rot, mode, spawnedObjects);
```

So:

```
zoneSeed = worldSeed + zx * 4271 + zy * 9187        // int32, wrapping
```

`worldSeed` is `WorldGenerator.GetSeed()`, which SeedLab already has. **Note the `+9187` on y** — the
dungeon seed in `DungeonGenerator.GetSeed` uses `-7187` for the same term, and they are different
constants. `unchecked` arithmetic: the multiplications overflow for far zones and the game relies on it.

Then `UnityEngine.Random.InitState(zoneSeed)`, which `src\SeedLab.WorldGen\Unity\UnityRandom.cs`
already ports, gives the stream.

---

## 2. The draw order

From `ZoneSystem.SpawnLocation`. The three arrays are **built** in the order ZNetView, RandomObject,
RandomSpawn, and **consumed** in the order RandomSpawn, RandomObject. Only the consumption order
matters:

```csharp
RandomObject[] randObjs   = Utils.GetEnabledComponentsInChildren<RandomObject>(asset);
RandomSpawn[]  randSpawns = Utils.GetEnabledComponentsInChildren<RandomSpawn>(asset);
foreach (RandomSpawn s in randSpawns) s.Prepare();
...
Random.InitState(seed);
foreach (RandomSpawn o in randSpawns) o.Randomize(pos + rot * o.transform.position, locationComponent);
foreach (RandomObject o in randObjs)  o.Randomize(pos + rot * o.transform.position, locationComponent);
```

`SpawnLocation` calls `Random.InitState(seed)` **three** times (once before the custom-interior work,
then once at the top of each of the two spawn-mode branches). The one immediately before the Randomize
loop is the one that sets the stream, and it is the same seed each time, so the loop always starts from
draw 0. The preflight asserts the count is still 3.

**Draw budget:**

| index in the stream | who spends it | call |
|---|---|---|
| `0 .. randomSpawnCount-1` | `randomSpawns[i]` | `Random.Range(0f, 100f)` |
| `randomSpawnCount + j` | `randomObjects[j]` | `Random.Range(0f, totalWeight_j)` |

Both are unconditional:

* `RandomSpawn.Randomize` opens with `bool spawned = Random.Range(0f,100f) <= m_chanceToSpawn;` — the
  draw happens before any gate, and the gates only turn a `true` into a `false`.
* `RandomObject.Randomize` opens with `GameObject weightedObject = GetWeightedObject();` — also before
  any gate. **A RandomObject that is gated off still spends its draw.** The preflight asserts that
  `Randomize` contains zero direct `Random::Range` calls and exactly one call to `GetWeightedObject`,
  and that `GetWeightedObject` contains exactly one `Range`.

`Utils.GetEnabledComponentsInChildren<T>(root)` is `root.GetComponentsInChildren<T>()` (active only),
minus any component sitting on the root transform itself, minus anything whose `activeSelf` chain up to
the root is broken (`Utils.IsEnabledInheirarcy`). The dumper calls **that same method** rather than
reimplementing the filter, so the array order in `locationchildren.json` is the game's order by
construction. `index` in the file *is* the draw index.

`Random.Range(float, float)` is settled: `f = (float)(long)(Next() & 0x7FFFFF) * Scale;
return (1-f)*max + f*min;` (`UnityRandom.Range`, closed by golden `D6-range-float` / `D6b`). For
`Range(0f, 100f)` that is `(1-f) * 100f`, i.e. it runs **downwards** as `f` runs 0→1, and it always
consumes a draw even when min == max. Compare with `<=` against `m_chanceToSpawn`, using the `bits`
value from the dump, never the printed decimal.

---

## 3. The gates, in the order the game applies them

Written for a LOCATION. **Inside a room, gates 2 and 3 swap roles** - `PlaceRoom` passes
`Randomize(pos, null, this)`, so the theme gate is live and the biome gate is dead. See section 7.

For `randomSpawns[i]`, with `pos2 = p + rot * prefabPosition_i` (and see §5(f): on the nine prefabs
with an active custom interior transform, `prefabPosition_i` is not what the game reads):

1. **chance** — `spawned = draw_i <= m_chanceToSpawn`.
2. **dungeon theme** — `if (dg != null && m_dungeonRequireTheme != None && !dg.m_themes.HasFlag(...))`.
   `SpawnLocation` calls `Randomize(pos2, component)` and leaves `dg` defaulted to null, so **this gate
   never fires for a location's own RandomSpawns**. It is dumped because the same component type is
   also driven by `DungeonGenerator`, where it does.
3. **biome** — `if (m_requireBiome != None)`: if `loc.m_biome == None` then
   `loc.m_biome = WorldGenerator.instance.GetBiome(pos2)`; then
   `if (!m_requireBiome.HasFlag(loc.m_biome)) spawned = false`. **See §5(a): `loc` is the shared prefab
   asset and `m_biome` is a session-wide cache.**
4. **lava** — `if (m_notInLava && ZoneSystem.instance && ZoneSystem.IsLavaPreHeightmap(pos2))`.
   `IsLavaPreHeightmap(Vector3 position, float lavaValue = 0.6f)` is
   `GetBiome(x,z) == AshLands && GetBiomeHeight(AshLands, x, z, out mask).mask.a > 0.6f`. **Fully
   computable offline** — SeedLab's ported `WorldGenerator` has both.
5. **elevation** — `if (pos2.y < (float)m_minElevation || pos2.y > (float)m_maxElevation)`. The
   comparison is against the *int* field cast to float. Defaults are -10000 / 10000, i.e. inert.

For `randomObjects[j]` the gates are the same minus the chance, and the winner is
`first entry whose running sum >= draw`, walking `m_objects` in list order; if nothing matches (only
possible with a zero total) `GetWeightedObject` returns null and nothing spawns. An entry with a null
`m_object` still contributes its weight and can still win, in which case nothing spawns — the dumper
records those entries and warns.

**What "spawned" does to the hierarchy** (`RandomSpawn.SetSpawned`):

* spawned → the object is activated (only when it has no `ZNetView` of its own), and
  `m_OffObject.SetActive(false)`;
* not spawned → the object and every `ZNetView` in its `Prepare()` list are deactivated, and
  `m_OffObject.SetActive(true)`.

`activatedNetViewNames` in the dump is `Prepare()`'s `m_childNetViews` by **normalised** GameObject
name (section 9) — **distinct and sorted ordinal, not in Prepare's order**, with the true list length in
`activatedNetViewCount`. Prepare's order carries no information (it builds the list only to
`SetActive` everything together, and nothing in it touches the RNG), while writing a building's
300-piece list verbatim once per RandomSpawn would be most of the file. The query is reproduced by
reading, never by calling `Prepare()`, which would write private state on a shared asset. It names the
pieces a RandomSpawn switches on, and it is never truncated.

(The first pass called this "the list that names `piece_maypole`". That was an assumption, not a fact -
it presumed the maypole is inside a location prefab at all. `search.json` is what names it now; this
list is one of the places that file looks.)

---

## 4. The two questions this answers

**Maypole. Start at `search.json`, not here.** As of the second pass the dump answers this question
directly: `search.json` has one entry per name in `SoughtPrefabNames` (default `piece_maypole`) saying
FOUND - with every host prefab, the hierarchy path, the gating RandomSpawn's index, chance and gates -
or NOT FOUND in as many words, with a `coverage` block saying what was and was not searched and an
`inconclusive` flag when the search had a real gap. Read the verdict sentence first; everything below
is how to interpret a FOUND.

**Where it lives is an open hypothesis, not a fact.** Three possibilities, and the dump distinguishes
them:

1. *Inside a location prefab.* Then the hit's `kind` is `location`, and the arithmetic below applies
   as written.
2. *Inside a dungeon/camp ROOM prefab* - the reviewer's hypothesis, and the reason section 7 exists.
   Then `kind` is `room`, the hit names the room list and `RoomData.m_theme`, and the arithmetic is
   the room one in section 7: a different seed, from the room's placement position, which comes out of
   a dungeon layout that is rolled once and saved to a ZDO.
3. *Nowhere reachable.* Then `found` is false, and `existsInZNetScene` says whether the piece exists
   in the build at all. A player-built piece is registered in `ZNetScene` and placed by nothing.

The only independent evidence so far: the string `piece_maypole` occurs exactly twice in
`valheim_Data\resources.assets`, and **both are localisation entries** - the display name "Maypole"
and its description. That file says the piece exists; it says nothing about where it is placed. Treat
every claim about its host as pending the dump.

**If the Meadows restriction is real**, the dump says which mechanism enforces it. A gating RandomSpawn
with `requireBiome` set to the Meadows bit (1) means the game enforces it, and then §5(a) is in play
and the answer is only as good as the shared `m_biome` cache. `requireBiome: 0` on every candidate
entry means the restriction comes purely from the host's own placement rules, §5(a) and §5(c) both
stop mattering, and the answer is exactly computable from the chance draw. **Do not assume which; read
`requireBiome` off the dump.** Inside a room, note that `requireBiome` is **inert** whatever its value
(section 7).

Given a hit, the arithmetic: call the gating RandomSpawn's `index` N and its `chanceToSpawn` C. Seed
with the host's stream, take draw N, and the piece is present iff `draw_N <= C` and the live gates
pass. A `gatedByRandomSpawnIndex` of -1 means nothing gates it: it is unconditional in that prefab.

The per-prefab indices are still there for cross-checking, and the second pass changed which one to
use. `prefabNames` is the **normalised** index (`Utils.GetPrefabName`, see section 9) and is the one to
ask identity questions of; `childNames` keeps the raw spellings. `activatedNetViewNames` on a
RandomSpawn is likewise normalised now. `subtreeChildNames` is a broader safety net but is capped at
`maxNamesPerEntry`, with the true count in `subtreeChildNameCount` - use it to notice a truncation,
never to conclude an absence.

**Axe heads.** For each `WoodHouse*` entry, read its `containers[]`. Each has a
`gatedByRandomSpawnIndex` (−1 when the chest is unconditional) and a `defaultItems` drop table listing
`itemPrefabName` + `weight` per entry. The claim that falls out is *"the chest in WoodHouseN **can**
contain X"*, with the probability `weight / totalWeight` per spin, `dropMin..dropMax` spins, gated by
`Random.value > dropChance` returning nothing at all. **Never "contains".** The chest's actual contents
are rolled at spawn time from the ambient, unseeded stream, not from the location's seeded one, so no
amount of this data predicts a particular chest.

---

## 5. Where this is genuinely uncertain — read before quoting a number

**(a) `Location.m_biome` is a session-wide cache on a shared asset. This is the big one.**

`SpawnLocation` passes `location.m_prefab.Asset.GetComponent<Location>()` — the **shared prefab
asset's** component, the same object for every instance of that location in the world. `Randomize` sets
`loc.m_biome` when it finds `None`, and **nothing ever clears it** (verified by IL: the only writers of
`Location::m_biome` anywhere in `assembly_valheim` are `RandomSpawn::Randomize`,
`RandomObject::Randomize` and `MaterialVariationWorld::Update`, and the last one operates on an
*instantiated* Location found by `Location.GetZoneLocation`, not on the asset).

Consequences:

* If `m_biome` is authored non-None, gate 3 is a **constant** and the location's rotation is irrelevant
  to it.
* If it is authored `None`, the value is sampled **once per prefab per session**, at the `pos2` of the
  first biome-gated entry of whichever instance spawned first — and then reused everywhere. It is not a
  per-instance test at all, and it is not reproducible offline, because it depends on which zone the
  player loaded first.

The 2026-09-22 dump says which case we are in: 181 of 186 prefabs report `biome: 0` and the five
non-zero ones are `AncientUpgradeStation` (Mountain) and `NorthMemorialPlace` / `DN_hut01` /
`DN_gammeltrollFrac01` / `DN_gammeltrollFrac02` (DeepNorth) — none of which could have spawned near
that dump's Meadows spawn point, so non-zero means authored. **All of WoodHouse1..13 are `None`.**

In practice the poison is usually benign: WoodHouse is Meadows-only placement, so a Meadows-gated child
samples Meadows and the gate passes forever. It bites when the sample point `pos2` — which the
unseeded rotation moves, see (c) — lands just outside the Meadows patch on the *first* WoodHouse of the
session. Then every WoodHouse in that session loses its Meadows-gated children. That is a plausible
explanation for inconsistent community reports and it is worth saying out loud rather than modelling.

**Whether any WoodHouse RandomSpawn actually has a non-None `m_requireBiome` is exactly what the dump
will tell us.** If none does, (a) and (c) both stop mattering for the maypole and the answer is
exact. Do not write the tool as if that is already known.

**(b) `pos2.y` is a physics raycast, not a generator height.** `GetGroundData` raycasts straight down
from `p + up*5000` against `m_terrainRayMask` and takes `hitInfo.point.y` — i.e. the built Heightmap
*collider mesh*, which is a 64-vertex-per-zone lattice with interpolation between vertices, not
`WorldGenerator.GetHeight(x, z)` evaluated at the point. Offline the two agree closely but not
bit-exactly. It only matters when a `minElevation` / `maxElevation` is authored away from the
-10000/10000 defaults **and** the location sits near that threshold; `m_snapToWater` locations have
`p.y = 30f` exactly and are unaffected. Flag any near-threshold case rather than answering it.

**(c) The location's rotation is unseeded, and it moves the biome and lava sample points.**
`rot` comes from `Random.Range(0, 16) * 22.5f` on the **ambient** stream (before `InitState`) when
`m_randomRotation`, or from the terrain slope when `m_slopeRotation`. **Neither is reproducible
offline.** The slope case is not the "computable from terrain" escape it looks like:
`ZoneSystem.GetTerrainDelta` takes ten `Random.insideUnitCircle` samples — also from the ambient
stream — and reads `GetGroundHeight` at each, so its yaw depends on both an unseeded draw sequence
and the raycast fidelity of (b). It is then snapped to 22.5 degrees like the random case, so both end
up as one of 16 yaws with no way to know which.
`pos2 = p + rot * prefabPosition`, so the XZ of the sample point swings around `p` by up to
`|prefabPosition.xz|` — the prefab radius. That perturbs gate 3 (biome) and gate 4 (lava).

**It does not perturb gate 5 (elevation), and that is provable, not assumed:** both rotation sources are
yaw-only — `Quaternion.Euler(0, k*22.5f, 0)` in the random case, and in the slope case
`Quaternion.LookRotation(forward)` with `forward.y == 0` and the default `Vector3.up`, whose euler `y`
is then snapped. A rotation about Y leaves the y component of any vector untouched, so
`pos2.y == p.y + prefabPosition.y` exactly, for every one of the 16 possible rotations.

So: **the elevation and chance gates are exact; the biome and lava gates are rotation-dependent.** When
a gated entry's `prefabPosition.xz` is small relative to the biome patch, all 16 rotations agree and the
answer is still exact — the tool should evaluate all 16 and report "yes / no / depends on the rotation"
rather than picking one. That holds for both rotation
sources: the snapped yaw is always a multiple of 22.5 degrees, so 16 candidates cover every case.

**(d) The shared asset's active state drifts during a session.** `SpawnLocation` finishes by calling
`Reset()` on every RandomSpawn, which is `SetSpawned(true)`, which sets `m_OffObject` **inactive** and
never restores it. So on a prefab whose location has already spawned this session,
`Utils.GetEnabledComponentsInChildren` can return a *shorter* array than the authored prefab has - and
a shorter array is a different draw budget. `DungeonGenerator.PlaceRoom` does the same thing to a room
prefab. The dumper records both counts (`randomSpawnCount` vs `randomSpawnCountAll`, same for
RandomObject) and writes a per-prefab `warnings` entry plus a manifest note when they differ. **If a
warning fires for a prefab you are about to answer about, re-dump from a freshly CREATED world before
trusting its draw indices** - see section 10.

*Corrected 2026-09-23 (second pass).* That comparison used to fire on healthy prefabs.
`Utils.GetEnabledComponentsInChildren` drops **two** kinds of component: one whose `activeSelf` chain
to the root is broken, and one mounted on the ROOT TRANSFORM itself
(`componentsInChildren[i].transform == root.transform`). The "present" count used
`GetComponentsInChildren<T>(true)`, which includes the root one - so a clean prefab with a root-mounted
RandomSpawn reported a mismatch and the reader was told to distrust a good dump. Both counts now
exclude the root component, and the preflight pins the game's exclusion with a call count. A warning
that fires on healthy data is worse than no warning: it teaches the reader to skip warnings.

**(e) `prefabPosition` is computed, not read the way the game reads it.** The game zeroes the asset
root's position and rotation (leaving `localScale`) and reads `child.transform.position` back. The
dumper must not mutate a shared asset, so it computes `Inverse(rootRot) * (childWorld - rootPos)`.
When the root is already at the identity — `rootAtIdentity: true` in the file — this is exact. If any
prefab reports `rootAtIdentity: false`, its `prefabPosition` is a derived value that may differ in the
last bit, and that should be checked before it is used for a near-threshold gate. (Deliberately **not**
`InverseTransformPoint`, which also divides by the root scale the game keeps.)

For a ROOM the same field is anchored on the **`Room` component's** transform rather than the asset
root, because that is what `PlaceRoom` measures from. `roomComponentOnRoot` says whether the two are
the same object, which they normally are.

**(f) On nine location prefabs the game MOVES the child before it reads its position.** Added
2026-09-23; this was a silent wrong answer until then.

The nine, from the 2026-09-22 `locationprefabs.json` (`useCustomInteriorTransform: true` on the
`Location`, and on the `DungeonGenerator` of the same nine): `MountainCave02`, `Mistlands_DvergrTownEntrance1`, `Mistlands_DvergrTownEntrance2`, `Mistlands_DvergrBossEntrance1`, `Hildir_cave`, `Hildir_crypt`, `Hildir_plainsfortress`, `TheHole01` and `MorkBorg`. The dump's
`customInteriorTransformActive` is the stricter test — it also requires `m_interiorTransform` and
`m_generator` to be non-null, which is what `SpawnLocation` checks — so read it rather than assuming
all nine qualify.

When the root `Location` has `m_useCustomInteriorTransform` **and** a non-null `m_interiorTransform`
**and** a non-null `m_generator` - `SpawnLocation`'s local `flag`, all three conditions - the game
mutates the shared asset between `Random.InitState(seed)` and the Randomize loop:

```csharp
component.m_generator.transform.localPosition = Vector3.zero;
component.m_interiorTransform.localPosition   = <from the zone centre, the instance position, and rot>;
component.m_interiorTransform.localRotation   = Quaternion.Inverse(rot);
```

So for any entry under either transform, the `prefabPosition` in the dump is **not** the value the game
reads. The generator case is a constant shift (to zero); the interior case is
**instance-dependent** - it depends on which zone the instance is in and which of the 16 unseeded yaws
it got - and therefore not statable by any dump at all.

The dump does not paper over this. Each affected entry carries `underGeneratorTransform`,
`underInteriorTransform` and `prefabPositionIsInstanceDependent`; the prefab carries
`customInteriorTransformActive`, `interiorTransformPath`, `generatorTransformPath` and
`instanceDependentPositionCount`; and a warning names the count. A prefab with
`m_useCustomInteriorTransform` set but a null interior or generator gets a different warning saying the
mutation does **not** happen, so its positions are good.

**Draw order and draw count are completely unaffected** - the mutation moves transforms, not the
stream. Only the elevation and lava gates of those particular entries become unknowable, and gate 3
(biome) too, since it samples at the same moved point.

---

## 6. Checklist for the offline implementation

1. `zoneSeed = unchecked(worldSeed + zx*4271 + zy*9187)`; `InitState(zoneSeed)`.
2. Take `randomSpawnCount` draws of `Range(0f,100f)`, then `randomObjectCount` draws of
   `Range(0f, totalWeight_j)`. Never skip a draw for a gated-off entry.
3. Chance gate: `draw <= chanceToSpawn`, comparing the bit patterns.
4. Biome gate: only when `requireBiome != 0`; then say "depends on the shared cache" unless the
   location's whole placement footprint is inside one biome for all 16 yaws.
5. Lava gate: `notInLava` → `GetBiome == AshLands && GetBiomeHeight(AshLands,...).mask.a > 0.6f`.
6. Elevation gate: exact, using `p.y + prefabPosition.y`, with the raycast caveat from (b).
7. Read `warnings[]` on the prefab before answering. An entry there invalidates the draw indices.
   Read `error` too: a non-null `error` on a `loaded` entry means the asset loaded and the WALK failed
   - the arrays and counts are zeroed, and they mean "unknown", never "none".
8. Skip `prefabPosition` for any entry with `prefabPositionIsInstanceDependent` - §5(f). Its draw
   index is still exact; its position gates are not evaluable.
9. Match names on the **normalised** form (section 9), never on the raw `GameObject.name`.
10. Phrase every container answer as **can contain**.
11. For a ROOM hit, use section 7's seed, not section 1's - and stop at "can contain", because the
    room's placement position comes from a saved dungeon layout.

---

## 7. Rooms: the other half of the world, and the other algorithm

The first pass assumed a location prefab's child tree was the whole of a location. It is not, and this
is the single most important correction in these notes.

A location with an interior - a crypt, a cave, a Fuling camp, a Meadows village - contains a
`DungeonGenerator` and nothing else. Its contents are **room prefabs** out of `DungeonDB`,
instantiated at generation time. **Nothing in the location prefab's child tree names them.** So a
complete, correct location walk can answer "which prefab contains X" with silence for any interior
piece - and silence reads as "this game has no X".

### Reaching the rooms

`DungeonDB.Start` -> `SetupRooms()` instantiates every prefab in `m_roomLists` and concatenates each
resulting `RoomList.m_rooms` into the private `m_rooms`; `GenerateHashList()` then keys them by
`m_prefab.Name.GetStableHashCode()` and **logs an error and drops any later duplicate**. The dump reads
all four inputs (`GetRooms()`, `RoomList.GetAllRoomLists()`, `m_roomByHash` by reflection,
`m_roomLists` / `m_roomScenes`) rather than assuming which one is populated, and records
`duplicateOfIndex` for a room whose hash was already taken.

### Which dungeon can contain which room

```csharp
// DungeonGenerator.SetupAvailableRooms, 1.0.15
foreach (DungeonDB.RoomData room in DungeonDB.GetRooms())
    if ((room.m_theme & m_themes) != Room.Theme.None && room.m_enabled)
        m_availableRooms.Add(room);
```

**It filters on `RoomData`, not on the `Room` component.** The component has its own `m_theme` and
`m_enabled` and they are normally equal; the dump writes both and warns when they differ. To go from a
location to the rooms it can hold: take that location's `DungeonGeneratorDef.themes` from
`locationprefabs.json` and intersect it with each room's `roomDataTheme` in `roomchildren.json`.

### The room's own RNG stream

```csharp
// DungeonGenerator.PlaceRoom(RoomData, pos, rot, fromConnection, mode), 1.0.15
Vector3 v = m_useCustomInteriorTransform ? pos - base.transform.position : pos;
int num = (int)v.x * 4271 + (int)v.y * 9187 + (int)v.z * 2134;
if (m_addBaseSeedToRandomSpawn) num += GetSeed();
Random.State state = Random.state;          // the AMBIENT stream is saved...
Random.InitState(num);
// ... three arrays built exactly as SpawnLocation builds them, Prepare() on every RandomSpawn ...
foreach (RandomSpawn s in randSpawns)  s.Randomize(pos + rot * prefabPos, null, this);
foreach (RandomObject o in randObjs)   o.Randomize(pos + rot * prefabPos, null, this);
// ...
Random.state = state;                       // ... and restored
```

Four things to carry away, all verified from the shipped IL on 2026-09-23:

* **The draw budget has the same shape as a location's**: one `Range(0f,100f)` per RandomSpawn in array
  order, then one weighted draw per RandomObject, all unconditional. Entry N consumes draw N.
* **`+2134`, not `-2134`.** `DungeonGenerator.GetSeed()` uses `pos.z * -2134` for the dungeon's own
  seed; `PlaceRoom` uses `(int)v.z * 2134` for the room's. Different constants in adjacent methods -
  exactly the kind of thing a port gets wrong once and never notices.
* **`m_addBaseSeedToRandomSpawn` decides whether two identical rooms in two different dungeons roll
  identically.** It is now recorded per generator in `locationprefabs.json`.
* **The gates swap over.** `PlaceRoom` passes `Randomize(pos, null, this)`. In `RandomSpawn.Randomize`
  the biome gate is `loc != null && m_requireBiome != None` and the theme gate is
  `dg != null && m_dungeonRequireTheme != None`. So inside a room `m_requireBiome` is **inert** and
  `m_dungeonRequireTheme` is **live** - the exact opposite of a location, where `SpawnLocation` passes
  the Location and defaults the generator to null.

### What is NOT computable offline for a room

`pos` is where the room landed, and that comes out of the dungeon layout. The layout is rolled from
`DungeonGenerator.GetSeed() = worldSeed + zone.x*4271 + zone.y*-7187 + (int)pos.x*-4271 +
(int)pos.y*9187 + (int)pos.z*-2134` and then **saved to the generator's ZDO** - generated once, never
re-rolled (valheim-worldgen skill, section 7). Reproducing it offline means reproducing
`GenerateRooms` / `PlaceRooms` / the collision tests in full, which is a project of its own.

**So `roomchildren.json` states what a room CAN contain and under which gate. It does not predict a
particular dungeon.** Every answer built on it has to be phrased that way.

---

## 8. `search.json`: making an absence say so

Every other file in the dump is a table, and a table cannot distinguish three very different things:
the name is not in the game; the name is somewhere the walk never went; the name was in a prefab that
failed to load. A user who has just spent a game launch on a dump must not be left guessing between
them. So:

* `found` is written for every configured name, true or false. **Never an omission.**
* Each hit carries the host (location or room), the room list and theme for a room, the hierarchy path,
  the raw name that matched, the gating RandomSpawn's index, chance and every gate, and whether the
  object sits under an `m_OffObject`.
* `coverage.searched` and `coverage.notSearched` say what was and was not covered **this run**;
  `coverage.standingLimitations` says what no run can ever cover. They are separate on purpose -
  folding the standing limits into the gaps would mark every verdict inconclusive forever and make the
  word worthless.
* `inconclusive` is set when `found` is false **and** `notSearched` is non-empty. A NOT FOUND with that
  flag is not evidence of absence, and the verdict sentence says so in English.
* `existsInZNetScene` is `ZNetScene.GetPrefab(name) != null` - a pure dictionary read. It separates
  "the name is wrong" from "the piece exists but nothing in world generation places it", which is
  exactly how a player-built piece behaves.
* The verdict sentences are printed to the console and copied into `manifest.json`'s `notes`.

It searches location prefabs and their children, room prefabs and their children, the weighted options
of every `RandomObject` (prefab *references*, not children - they appear in no name index), every
`Container` drop table, and the location and vegetation tables.

**`SoughtPrefabNames` must be set before the run.** The walk `Release`s each prefab the moment it has
read it, so a name chosen afterwards cannot be searched for without dumping again.

---

## 9. Name normalisation: `Utils.GetPrefabName` is the identity

```csharp
// assembly_utils, 1.0.15
private static readonly char[] extraCharacters = new char[2] { '(', ' ' };
public static string GetPrefabName(string name)
{
    int num = name.IndexOfAny(extraCharacters);
    return num != -1 ? name.Remove(num) : name;
}
```

Truncate at the **first** `(` **or** space, discard the rest. That is the game's own identity for a
GameObject, and everything that asks "what prefab is this" goes through it.

Unity appends ` (1)` to duplicated siblings and `(Clone)` to instantiated ones, and the authored
prefabs are full of both, so a child that is `piece_maypole` to the game can be called
`piece_maypole (1)` in the hierarchy. An exact-match query against raw `GameObject.name` is therefore a
**false negative** for every duplicated child - and a false "not found" is the worst answer this dump
can produce, because the reader takes it as "the game has no maypole" and stops looking.

The dump now keeps both forms everywhere: `name` (raw) beside `normalizedName`, and two whole-prefab
indices - `childNames` (raw spellings, unchanged from the first pass) and `prefabNames` (normalised,
each entry listing the raw spellings it merged and how many there were). **Ask identity questions of
`prefabNames`.** Every search matches on the normalised form, including the configured sought names
themselves, so a config entry typed as `piece_maypole (1)` still looks for the right thing.

The dumper CALLS `Utils.GetPrefabName` rather than reimplementing it; its fallback copy of the rule is
used only if the call throws, and it logs loudly when that happens. The preflight pins the method's
shape with call counts (one `IndexOfAny`, one `Remove`) as well as its name, because a version that
looked things up in a dictionary would still be called `GetPrefabName` and would still compile.

---

## 10. Run procedure, revised

1. Build with `-p:DeployToGame=false`, run `preflight.ps1` and `preflight.ps1 -SelfTest`. Both must say
   `PASS`. Confirm the deployed DLL's SHA-256 against the build output before installing.
2. Set `SoughtPrefabNames` in `BepInEx\config\DoomMachine.SeedLabDumper.cfg` **before launching**.
   Default `piece_maypole`; comma-separated. A name chosen after the run cannot be searched for.
3. Leave `WalkLocationPrefabs`, `WalkPrefabChildren` and `WalkRoomPrefabs` on. Any of them off makes
   every NOT FOUND inconclusive, and the file says which one.
4. **Create a NEW world and dump before exploring.** This is a correctness requirement, not tidiness.
   `SpawnLocation` and `PlaceRoom` both end by calling `Reset()` on every RandomSpawn of the **shared
   prefab asset**, which is `SetSpawned(true)`, which leaves that spawn's `m_OffObject` inactive and
   never puts it back. Anything under a switched-off off-object drops out of
   `Utils.GetEnabledComponentsInChildren`, so a later dump can see a **shorter enabled array than the
   authored prefab has** - and that array is the draw budget everything here is built on.

   It is minimal, not zero: even a brand-new world has already spawned the locations in the zones
   around the spawn point (StartTemple and whatever Meadows locations landed nearby), and
   `WoodVillage` / `WoodFarm` are `DungeonGenerator` camps, so a few MeadowsVillage and MeadowsFarm
   rooms may have been through `PlaceRoom` too. The per-entry `warnings` say which prefabs were
   touched.
5. Stand in the world, press **F4** (or `seedlab_dump` in the console). Wait for `DONE`. The prefab
   walk and the room walk each run one prefab per frame with a solo/no-peers re-check between every
   two, so expect it to take noticeably longer than the 8-second first run.
6. **Read the last console lines**: they are the `search.json` verdicts. Then read `manifest.json`'s
   `notes`, which carry the same verdicts plus everything the dumper was unsure about.
