# Porting spec 02 — Location placement (bosses, traders, dungeons, camps, ore deposits)

Target build: **Valheim 1.0.15** (assembly_valheim.dll, Unity 6000.0.75). Every statement below is taken
from the decompiled members cited as `Type.Member`. Anything that could not be settled from code is
marked **Unverified:** with what would settle it. Do not "fix" anything here that looks like a bug —
several of the bugs are load-bearing for reproducing the game's output.

Source files read (already dumped, `scratchpad/decomp/`): `ZoneSystem.cs`, `AltBiomeWorldData.cs`,
`BiomeSector.cs`, `AltBiome.cs`, `AltBiomeList.cs`, `LocationList.cs`, `WorldGenerator.cs`,
`Heightmap.cs`, `Utils.cs`, `StringExtensionMethods.cs`, `UnityEngine.Random.cs`. Additionally read for
this spec: `ZNet.ServerLoadWorld` (re-decompiled and confirmed verbatim), `BiomeHelpers`
(`ToBiome`/`ToBiomeIndex`), `Terminal.<>c.<InitTerminal>b__7_17` (the `genloc` command, read as IL).
`Vector2s`, `BiomePointCoordinate`, `BiomeTypeInfo`, `BiomePoint`, `SoftReferenceableAssets.AssetID` and
`SoftReference<T>.get_Name` were checked with Mono.Cecil against `assembly_utils.dll`,
`assembly_valheim.dll` and `SoftReferenceableAssets.dll` — note that
`scratchpad/decomp/Vector2s.cs` is **not** source: that dump failed and the file contains a PowerShell
"type not found" error. Do not read it.

Prerequisite: **spec 01** (WorldGenerator: `GetBiome`, `GetHeight`/`GetBiomeHeight`, `GetForestFactor`,
version setup). This spec treats those as available, exact functions of `(seed, worldGenVersion, x, z)`.

---

## 0. Executive summary for the implementer

1. On every **server** world load, `AltBiomeWorldData.VerifyBiomeData` rebuilds a **2048x2048 grid of
   sample points at 12 m spacing** (biome + height per point), flood-fills it into `BiomeSector`s, and
   assigns alt-biomes. Nothing of this is persisted (the cache file is deleted first).
2. Then, **only if the save's `locationsGenerated` flag is false**, `ZoneSystem.GenerateLocationsTimeSliced`
   walks the location list in a fixed order and, for each location type, opens **its own RNG stream**
   seeded `worldSeed + prefabName.GetStableHashCode()` and rolls candidate zones until `m_quantity`
   instances are registered or the attempt budget runs out.
3. Steps 1 and 2 consume **no** randomness except `AltBiomeWorldData.GenerateAltBiomes` (its own
   self-seeded stream) and the per-location streams. Frame timing cannot change the result.
4. For a **fresh, never-played world** (the only case a seed tool needs), `m_generatedZones` and
   `m_locationInstances` are both empty at the start, which kills three of the filters outright and makes
   the whole run a pure function of `(int seed, worldGenVersion, the asset-side location list, the
   asset-side alt-biome list)`.
5. The hard blockers on bit-exactness are **UnityEngine.Random** (native: `InitState`, `Range(int,int)`,
   `Range(float,float)`, `insideUnitCircle`) and the **asset-side location table**. Both must be captured
   with a BepInEx dumper; see §10 and §11.

---

## 1. When placement runs, and with what state

`ZNet.ServerLoadWorld` (exact, decompiled):

```csharp
if (m_world.IsChunkedSave()) LoadWorld(); else LoadOldWorld();
AltBiomeWorldData.VerifyBiomeData(m_world);
ZoneSystem.instance.GenerateLocationsIfNeeded();
ZoneSystem.instance.GenerateLocationsCompleted += OnGenerationFinished;
```

Order matters: the `.db2` is fully loaded (so `m_generatedZones`, `m_locationInstances`,
`m_locationsGenerated` are known) **before** the biome grid is built, and both are done before location
generation. `WorldGenerator.Initialize(world)` has already run (it clears the static caches
`s_cachedBiomes` / `s_cachedBiomeAreas` in the ctor — `WorldGenerator..ctor`).

`ZoneSystem.GenerateLocationsIfNeeded` → `GenerateLocations()` → coroutine
`GenerateLocationsTimeSliced()` only when `!LocationsGenerated`. `m_locationsGenerated` is read from the
`.db2` and forced to `false` when the stored `m_locationVersion` differs from the ZoneSystem prefab's
(`ZoneSystem.Load`, last two lines).

`ZoneSystem.GenerateLocationsIfNeeded` has exactly **one** caller, `ZNet.ServerLoadWorld` (Mono.Cecil
scan of every method body in `assembly_valheim.dll`, 2026-09-22). `ZoneSystem.GenerateLocations()` has
two: `GenerateLocationsIfNeeded` and the console command. Verified console behaviour
(`Terminal.<>c.<InitTerminal>b__7_17`, decompiled IL): `genloc alt` (argv[1] lower-cased == "alt") calls
`ZNet.instance.GetWorld().m_biomeData.GenerateAltBiomes()` **and returns**; any other form of `genloc`
calls `ZoneSystem.instance.GenerateLocations()`. `GenerateLocations()` guards on
`m_generateLocationsCoroutine == null`, does **not** reset `LocationsGenerated`, and calls
`SetupLocations()` only when `!Application.isPlaying` — so a console re-run does not rebuild the location
table (`ZoneSystem.GenerateLocations`).

`ZoneSystem.Update` refuses to spawn any zone while `IsServer && !LocationsGenerated`, so on the server
**no zone can be generated before location generation finishes**.

### 1.1 Fresh-world simplification (the case the tool models)

A world that has never been loaded has no `.db2`. Therefore at the start of generation:

| State | Value | Consequence |
|---|---|---|
| `m_generatedZones` | empty | `IsZoneGenerated(zoneID)` is always false → that rejection branch never fires |
| `m_locationInstances` | empty | `ClearNonPlacedLocations()` is a no-op; `CountNrOfLocation` returns 0 for every type |
| `m_locationIDCache` / `GroupCache` / `MaxGroupCache` | empty | `HaveLocationInRange` only ever sees instances made during this run |

Everything the tool needs to reproduce is then a pure function of the int seed, the world-gen version and
the asset-side location table. A tool **must not** try to model a world that has been played: unique
winners and post-exploration re-rolls are not predictable (§8c).

---

## 2. `AltBiomeWorldData` — the biome point grid

### 2.1 Constants (`AltBiomeWorldData` fields)

```
c_textureSize = 2048      // grid is 2048 x 2048 points
c_pixelSize   = 12f       // 12 m between points
c_halfWidth   = 1024
c_halfPixel   = 6f
waterEdgeSqr  = 110250000f   // WorldGenerator.waterEdgeSqr, = 10500^2
```

Grid covers x,z in `[-12282, +12282]` m — deliberately larger than the 10 500 m water edge.

### 2.2 Coordinate maps (`AltBiomeWorldData.MapSpaceToWorldSpace` / `WorldSpaceToMapSpace`)

```csharp
public static float MapSpaceToWorldSpace(float x) => (x - 1024f) * 12f + 6f;
public static int   WorldSpaceToMapSpace(float x) => (int)((x - 6f) / 12f + 1024f);
public static Vector3 MapSpaceToWorldSpace(BiomePointCoordinate v)
    => new Vector3(MapSpaceToWorldSpace(v.x), 0f, MapSpaceToWorldSpace(v.y));
```

`WorldSpaceToMapSpace` uses a **C-style truncating cast**, not a floor: the expression goes negative only
for `x < 6 - 12*1024 = -12282`, and there it truncates toward zero. In practice the argument is always
non-negative inside the world, so it behaves as a floor there. `WorldGenerator.GetBiomeSector(int, int,
bool)` clamps to `[0, 2047]` anyway — and note that its `bool clamp` parameter is **never read**: the
body clamps unconditionally. The same method returns the shared static `BiomeSector.EmptyBlackForest`
when `m_world.m_biomeData == null`, `BiomeSector.EmptyMeadows` when `!m_biomeData.IsReady` or when the
grid cell holds `null`. Those three statics have empty `AltBiomes` lists, which is what filters 10a/10b
(§5.4) would see if they were ever reached before the grid is ready. `BiomePointCoordinate` is
`{ short x; short y; }` (Mono.Cecil: two `Int16` fields).

Index convention throughout: **`PointBiomes[x, y]`, `PointHeights[x, y]`, `PointSectors[x, y]`**, where
`x` is the world-X axis and `y` is the world-Z axis.

### 2.3 `VerifyBiomeData` (`AltBiomeWorldData.VerifyBiomeData`)

```csharp
public static void VerifyBiomeData(World world) {
    RemoveCache(world.m_name);          // deletes <SaveDataPath>/cache/<worldName>_biomedatacache.bin
    GenerateBiomePoints(world);         // builds a NEW AltBiomeWorldData(2048) and assigns world.m_biomeData
    world.m_biomeData.GenerateSectors();
}
```

The cache is **deleted, never read**, on a server load. `TryLoadCache` / `SaveCache` exist but are not
called from this path, so a port never needs the `.bin` format. Everything here is recomputed each load,
which is why it can be recomputed offline.

### 2.4 `GenerateBiomePoints` — exact loops

```csharp
AltBiomeWorldData d = new AltBiomeWorldData(2048);
for (int i = 0; i < 2048; i++)            // i = Z index
for (int j = 0; j < 2048; j++) {          // j = X index
    Vector2 v = { x = MapSpaceToWorldSpace(j), y = MapSpaceToWorldSpace(i) };
    if (v.sqrMagnitude > 110250000f) {                       // float compare, 10500 m
        d.PointBiomes[j, i]  = Heightmap.Biome.Ocean.ToBiomeIndex();
        d.PointHeights[j, i] = -1000f;
    } else {
        Heightmap.Biome b    = WorldGenerator.instance.GetBiome(v.x, v.y);
        d.PointBiomes[j, i]  = b.ToBiomeIndex();
        d.PointHeights[j, i] = WorldGenerator.instance.GetBiomeHeight(b, v.x, v.y, out _);
    }
}
d.PointsGenerated = true;
world.m_biomeData = d;          // NOTE: assigned only AFTER the loop
```

Notes that matter:

* `v.sqrMagnitude` is `float` arithmetic on the two world coordinates (no y term).
* `GetBiomeHeight(b, x, z, out mask)` with the biome already known is **identical** to
  `WorldGenerator.GetHeight(x, z)`, because `GetHeight(wx,wy)` is literally
  `GetBiomeHeight(GetBiome(wx,wy), wx, wy, out mask)` (`WorldGenerator.GetHeight`). So
  `PointHeights[x,y] == GetHeight(worldX, worldZ)` for every non-cutoff point.
* `world.m_biomeData` is assigned **after** the loop (`GenerateBiomePoints`, last two lines also set
  `altBiomeWorldData.m_world = world`), so during the loop `WorldGenerator.GetBiomeSector` sees a
  null/old `m_biomeData`. This is harmless: the `BiomeSector biomeSector = GetBiomeSector(wx, wy);`
  local in `GetBiomeHeight` is **dead code** (assigned at `WorldGenerator.GetBiomeHeight` line 3 of the
  body, never read again anywhere in the method). **No alt-biome ever modifies terrain height in this
  build** — verified two ways: `AltBiome.heightMapChanges` is `public const bool = false` (a compile-time
  constant, not a serialized field), and a Mono.Cecil scan of every method body in
  `assembly_valheim.dll` (including nested types, 2026-09-22) finds `m_baseHeightMultiplier`,
  `m_baseHeightOffset`, `m_heightMapMultiplier`, `m_heightMapOffset`, `m_heightMapBiomeOverride` and
  `m_customGenerator` referenced **only** in `AltBiome..ctor` (field initialisers) and read nowhere.
* **A second, different height cutoff exists inside `GetBiomeHeight`.** `GenerateBiomePoints` cuts on
  `vector.sqrMagnitude > 110250000f` → `-1000f`; independently, `WorldGenerator.GetBiomeHeight` returns
  `-2f * GetHeightMultiplier()` = **`-400f`** (with `mask` left at `Color.black`) when
  `DUtils.Length(wx, wy) > 10500f`, before the biome switch. The two tests are computed differently in
  float (`x*x + y*y` vs `DUtils.Length`), so a thin ring of grid points passes the first and fails the
  second and stores **-400f**, not the biome height and not -1000f. It does not affect
  `AllPointsAboveSeaLevel` (both values are < 30) but it does feed `BiomeSector.HeightMin` → `HeightAvg`
  → `CanAddModifier`.
* Cost: 4 194 304 `GetBiome` + `GetBiomeHeight` evaluations per world. This dominates the per-seed cost
  (§11).

### 2.5 `GenerateSectors` — flood fill

Step A — the three "global" biomes get one sector each, created **in this order** and appended to
`Sectors` in this order:

```csharp
Sectors.Add(new BiomeSector(this, Heightmap.Biome.AshLands));   // dict key BiomeIndex.AshLands
Sectors.Add(new BiomeSector(this, Heightmap.Biome.DeepNorth));
Sectors.Add(new BiomeSector(this, Heightmap.Biome.Ocean));
```

Step B — a full row-major scan assigns every AshLands / DeepNorth / Ocean point to its single global
sector, marks it visited, and appends it to the biome's point lists:

```csharp
for (short y = 0; y < Size; y++)
for (short x = 0; x < Size; x++)
    if (globalSectorFor(PointBiomes[x, y], out sector)) {
        PointSectors[x, y] = sector;  visited[x, y] = true;
        Biomes[sector.Biome].AllPoints.Add(new BiomePointCoordinate(x, y));
        if (PointHeights[x, y] >= 30f)
            Biomes[sector.Biome].AllPointsAboveSeaLevel.Add(new BiomePointCoordinate(x, y));
    }
```

So **Ocean / AshLands / DeepNorth each have exactly one sector**, and their `AllPoints` are in
row-major (z-major, then x) order.

Step C — flood fill for every other biome:

```csharp
for (short y = 0; y < Size; y++)
for (short x = 0; x < Size; x++)
    if (!visited[x, y]) {
        visited[x, y] = true;
        BiomeSector s = new BiomeSector(this, PointBiomes[x, y].ToBiome());   // registers itself in Biomes[b].Sectors
        Sectors.Add(s);
        PointSectors[x, y] = s;
        stack.Push((x, y));
        while (stack.Count > 0) {
            var p = stack.Pop();
            var pBiome  = PointBiomes[p.x, p.y];
            var pSector = PointSectors[p.x, p.y];
            tryFill(p.x + 1, p.y); tryFill(p.x - 1, p.y);
            tryFill(p.x, p.y + 1); tryFill(p.x, p.y - 1);
        }
    }
```

`AltBiomeWorldData.tryFill(visited, openList, pBiome, pSector, x, y)`:

```csharp
if (x >= 0 && y >= 0 && x < Size && y < Size && !visited[x, y] && PointBiomes[x, y] == pBiome) {
    visited[x, y] = true;
    PointSectors[x, y] = pSector;
    Biomes[pSector.Biome].AllPoints.Add(new BiomePointCoordinate(x, y));
    if (PointHeights[x, y] >= 30f)
        Biomes[pSector.Biome].AllPointsAboveSeaLevel.Add(new BiomePointCoordinate(x, y));
    openList.Push(new BiomePointCoordinate(x, y));
}
```

**Quirk that must be reproduced: the seed point of every flood-filled component is never added to
`AllPoints` or `AllPointsAboveSeaLevel`.** Only `tryFill` appends, and the seed point is marked visited
and pushed directly by the outer scan. One point per connected component of every non-global biome is
therefore invisible to `GetRandomPointByBiome*`. Get this wrong and every index drawn from those lists
shifts.

Fill order is 4-connected DFS with neighbours pushed in the order `+x, -x, +y, -y` and popped LIFO. The
resulting `AllPoints` ordering is **not** row-major and **must** be reproduced exactly, because
`GetRandomPointByBiome` indexes straight into it.

Step D — edge pass (interior only, `i,j` from 1 to `Size-2`), per sector: `EdgeCount`, `Center` (running
**sum** of map-space coordinates of edge points), `Min`/`Max`, `Neighbors`, `HeightMin`/`HeightMax`. A
point is an "edge" point when any of its 4 neighbours has a different `BiomeSector` **reference**.

The exact source (`AltBiomeWorldData.GenerateSectors`, the `for (int i = 1; i < Size - 1; i++)` pass):

```csharp
BiomeSector item;
if ((item = PointSectors[j - 1, i]) != biomeSector2 || (item = PointSectors[j + 1, i]) != biomeSector2
 || (item = PointSectors[j, i - 1]) != biomeSector2 || (item = PointSectors[j, i + 1]) != biomeSector2)
{
    biomeSector2.EdgeCount++;
    biomeSector2.Center += new Vector2(j, i);
    ... Min/Max ...
    if (!biomeSector2.Neighbors.Contains(item)) biomeSector2.Neighbors.Add(item);
    ... HeightMin/HeightMax from PointHeights[j, i] ...
}
```

Two details that a port must copy and that the earlier prose did not state:

* The neighbour probe order in **this** pass is `-x, +x, -y, +y` — *not* the flood fill's `+x, -x, +y,
  -y`.
* `||` short-circuits, so `item` ends up holding **only the first differing neighbour** in that order.
  **At most one `BiomeSector` is offered to `Neighbors` per edge point**, even when two, three or four
  neighbours differ. A sector that touches another sector only along edges where some earlier-probed
  neighbour also differs will never list it in `Neighbors`. `Neighbors` is read only by
  `BiomeSector.CanAddModifier` (`m_requireNeighbor` / `m_notNeighbor`), so this matters only for
  alt-biomes — but see §2.9: `m_requireNeighbor` is unreachable for a different reason anyway.

Step E — post pass, only for sectors with `EdgeCount > 0`: `Center` becomes
`MapSpaceToWorldSpace(sum / EdgeCount)` per axis; `Min`/`Max` are converted to world space;
`HeightAvg = (HeightMin + HeightMax) / 2`.

Step F — `SectorsCalculated = true`; then the "IsDiscovered" pass; then
`DistanceFromCenter = Vector2.Distance(Center, Vector2.zero)` **for every sector**; then
`GenerateAltBiomes()`.

**Bugs in step E/F that a port should replicate by simply not implementing them:**

* `sector.MaxZone = ZoneSystem.GetZone(new Vector3(sector.Min.x, sector.Min.y))` — `MaxZone` is computed
  from `Min`, so `MaxZone == MinZone` always; and `new Vector3(a, b)` puts `b` in **y**, so `GetZone`
  reads `point.z == 0` and both zone values have `y == 0`. Consequently the `IsDiscovered` loop
  (`for k = MinZone.y; k < MaxZone.y`) never executes and `IsDiscovered` is **always false**.
  `BiomeSector.ZoneCount` is likewise always 1. Neither is read by anything on the placement path.
* `BiomeSector.Min` / `Max` are `Vector2` fields defaulting to `(0,0)`, not to ±infinity, so a sector that
  lies entirely in one quadrant gets a `Min`/`Max` box that always contains the origin. Only used by the
  dead `IsDiscovered` code.
* Sectors with `EdgeCount == 0` (possible only for components confined to the array border row/column)
  keep `Center = (0,0)` (map space), `HeightAvg = 0`, `DistanceFromCenter = 0`.

### 2.6 The `Biomes` dictionary (`AltBiomeWorldData..ctor`)

```csharp
foreach (object v in Enum.GetValues(typeof(Heightmap.Biome)))
    Biomes[(Heightmap.Biome)v] = new BiomeTypeInfo((Heightmap.Biome)v);
```

`Enum.GetValues` returns values sorted ascending, so the dictionary is populated — and iterated — in
this order:

`None(0), Meadows(1), Swamp(2), Mountain(4), BlackForest(8), Plains(16), AshLands(32), DeepNorth(64),
Ocean(256), Mistlands(512), Land(0x27F=639), All(895)`

The **composite** keys `Land` and `All` and the key `None` exist with empty lists. This iteration order
is what `GenerateAltBiomes` walks (§2.8). `BiomeTypeInfo` = `{ Biome; List<BiomeSector> Sectors;
List<BiomePointCoordinate> AllPoints; List<BiomePointCoordinate> AllPointsAboveSeaLevel; }`.

### 2.7 The point getters (`AltBiomeWorldData`)

```csharp
public  BiomePointCoordinate GetRandomPointByBiomes(Heightmap.Biome b)
        => GetRandomPointByBiome(RandomBiomeFromBiomes(b));
private BiomePointCoordinate GetRandomPointByBiome(Heightmap.Biome b)
        => Biomes[b].AllPoints[Random.Range(0, Biomes[b].AllPoints.Count)];

public  BiomePointCoordinate GetRandomPointByBiomesAboveSeaLevel(Heightmap.Biome b)
        => GetRandomPointByBiomeAboveSeaLevel(RandomBiomeFromBiomes(b));
private BiomePointCoordinate GetRandomPointByBiomeAboveSeaLevel(Heightmap.Biome b) {
    if (Biomes[b].AllPointsAboveSeaLevel.Count == 0) return GetRandomPointByBiome(b);  // fallback
    return Biomes[b].AllPointsAboveSeaLevel[Random.Range(0, Biomes[b].AllPointsAboveSeaLevel.Count)];
}
```

RNG consumption: `RandomBiomeFromBiomes` draws **0 or 1** int; the list pick draws **exactly 1** int in
every path (the empty-list fallback tests the count *before* drawing). `GetRandomSectorByBiome(s)` exists
but is **not used by location placement**.

Above-sea-level threshold is `PointHeights >= 30f` — the raw procedural height, **not** `height - 30`.

### 2.8 `RandomBiomeFromBiomes` — reproduce literally, bugs and all

```csharp
private Heightmap.Biome RandomBiomeFromBiomes(Heightmap.Biome biome) {
    if ((biome & (biome - 1)) == 0) return biome;         // 0 or 1 bit set -> no RNG draw
    int num = popcount over { Meadows, Swamp, Mountain, BlackForest, Plains, AshLands, DeepNorth, Ocean, Mistlands };
    int num2 = UnityEngine.Random.Range(0, num - 1);      // ONE int draw, upper bound EXCLUSIVE
    if ((biome & Meadows)    != 0 && num2-- == 0) return Meadows;
    if ((biome & Swamp)      != 0 && num2-- == 0) return Swamp;
    if ((biome & Mountain)   != 0 && num2-- == 0) return Mountain;
    if ((biome & BlackForest)!= 0 && num2-- == 0) return BlackForest;
    if ((biome & Plains)     != 0 && num2-- == 0) return BlackForest;   // <-- Plains returns BlackForest
    if ((biome & AshLands)   != 0 && num2-- == 0) return AshLands;
    if ((biome & DeepNorth)  != 0 && num2-- == 0) return DeepNorth;
    if ((biome & Meadows)    != 0 && num2-- == 0) return Ocean;         // <-- tests MEADOWS, not Ocean
    return Mistlands;
}
```

Consequences, stated exactly (let `P7` = number of set bits among Meadows..DeepNorth, `M` = 1 if Meadows
set, `O` = 1 if Ocean set, `Mi` = 1 if Mistlands set; `num = P7 + O + Mi`):

* The draw range is `[0, num - 2]` — `num - 1` values. The highest conceptual slot is unreachable.
* Reachable slots, in order, are the `P7` tested biomes (with Plains mapped to BlackForest), then, if
  Meadows is set, one extra slot returning **Ocean**. Any draw `>= P7 + M` falls through to **Mistlands**.
* The Ocean bit and the Mistlands bit only *inflate `num`*; the Ocean bit is never tested.
* Therefore **Ocean can be returned only when Meadows, Ocean and Mistlands are all set**, and only when
  the draw equals exactly `P7`.
* With exactly two bits set, `Random.Range(0, 1)` always yields 0, so the first tested biome always wins
  (e.g. `Meadows|BlackForest` → always Meadows; `Ocean|Mistlands` → always Mistlands).
* Examples: `Swamp|Mountain|Mistlands` → num=3, draw ∈ {0,1} → only Swamp or Mountain; **Mistlands is
  never returned** even though it is requested.
* **The returned biome need not be in the requested mask.** The candidate zone is then drawn from that
  biome's point list, and filter 2 (§5.4) re-tests the point's real biome. For multi-biome locations this
  wastes a large fraction of attempts — which is part of the result, not an inefficiency to optimise away.
* If the returned biome's `AllPoints` list were empty, `Random.Range(0,0)` yields 0 and the indexer
  throws. Not reachable for a real world, but assert it in the port.

### 2.9 `GenerateAltBiomes` (`AltBiomeWorldData.GenerateAltBiomes`)

```csharp
UnityEngine.Random.InitState(WorldGenerator.instance.GetSeed() + 920);   // (ambient global stream)
foreach (AltBiome a in AltBiomeList.m_altBiomes) { a.ValidPlacementSectors = 0; a.ValidPlacementSectorCombos = 0; }
foreach (var kv in Biomes) {                                  // dictionary order from §2.6
    AltBiomeList.GetValidAltBiomes(ref m_validAltBiomes, kv.Key);   // a.m_enabled && a.m_biome.HasFlag(kv.Key)
    foreach (var a in m_validAltBiomes) a.ValidPlacementSectorCombos++;
    foreach (AltBiome a in m_validAltBiomes) {
        UnityEngine.Random.InitState((int)(kv.Key + a.m_name.GetStableHashCode() + WorldGenerator.instance.GetSeed()));
        kv.Value.Sectors.Shuffle();                            // Utils.Shuffle<T>(IList<T>), in place
        foreach (BiomeSector s in kv.Value.Sectors) {
            if (a.Sectors.Count < a.m_maxAmountSpawned) {
                float r = UnityEngine.Random.Range(0f, 1f);    // ONE float draw per sector examined
                if ((a.Sectors.Count < a.m_minAmountSpawned || a.m_chance >= r) && s.CanAddModifier(a)) {
                    a.ValidPlacementSectors++;
                    s.AddModifier(a);                          // a.Sectors.Add(s); s.AltBiomes.Add(a);
                }
            }
        }
    }
}
```

* The `InitState(seed + 920)` at the top is pointless — every inner pair re-seeds before drawing.
  Reproduce it anyway if you ever share the ambient stream.
* `a.m_biome.HasFlag(kv.Key)` with `kv.Key == None (0)` is **always true**, so every enabled alt-biome is
  "valid" for the `None` key — harmless, because `Biomes[None].Sectors` is empty.
* `Utils.Shuffle<T>(IList<T>)` is a Fisher-Yates from the end:
  `for (int n = list.Count - 1; n > 0; n--) { int i = Random.Range(0, n + 1); swap(list[n], list[i]); }`
  — `Count - 1` int draws. It **mutates the per-biome `Sectors` list in place**, so the ordering seen by
  the next alt-biome in the same biome depends on all previous shuffles. Order is load-bearing.
* The `Random.Range(0f,1f)` draw happens for **every** sector examined while
  `a.Sectors.Count < a.m_maxAmountSpawned`, including sectors that then fail `CanAddModifier`. Once the
  max is reached, no further draws happen for that alt-biome.
* `BiomeSector.CanAddModifier` (full predicate, in order): `DistanceFromCenter >= m_minDistanceFromCenter`;
  `EdgeCount >= m_minEdgeSize`; `EdgeCount < m_maxEdgeSize`; `HeightAvg` in `[m_minAvgHeight,
  m_maxAvgHeight)`; the four `m_aboveWorldX/m_belowWorldX/m_aboveWorldY/m_belowWorldY` bounds against
  `Center` (each ignored when 0); mutual `m_incompatibleAltBiomes` both ways; then `m_requireNeighbor`
  and `m_notNeighbor` — **both of which are broken; see the next two bullets.** (Corrected: the earlier
  description of the neighbour tests was wrong.)
* **`m_requireNeighbor != None` makes `CanAddModifier` return false, always.** Source
  (`BiomeSector.CanAddModifier`):

  ```csharp
  if (modifier.m_requireNeighbor != Heightmap.Biome.None) {
      for (int i = 0; i < 10; i++) {
          if (!modifier.m_requireNeighbor.HasFlag(((Heightmap.BiomeIndex)i).ToBiome())) continue;
          bool flag = false;
          foreach (BiomeSector neighbor in Neighbors)
              if (neighbor.Biome == (Heightmap.Biome)i) { flag = true; break; }
          if (!flag) return false;
      }
  }
  ```

  `i` is a **`BiomeIndex`** (`Heightmap.BiomeIndex : byte` = None 0, Meadows 1, Swamp 2, Mountain 3,
  BlackForest 4, Plains 5, AshLands 6, DeepNorth 7, Ocean 8, Mistlands 9), and `BiomeHelpers.ToBiome`
  maps index → flag. At `i == 0`, `((BiomeIndex)0).ToBiome()` is `Biome.None == 0` and
  `Enum.HasFlag(0)` is **true for every mask**, so the `i == 0` iteration is never skipped and demands a
  neighbour with `Biome == Biome.None`. No `BiomeSector` can have `Biome == None`: the three global
  sectors are constructed with AshLands/DeepNorth/Ocean and every flood-fill sector with
  `PointBiomes[x,y].ToBiome()`, and `WorldGenerator.GetBiome` never returns `None`. Hence the whole
  block returns `false` whenever `m_requireNeighbor` is non-zero. Consistent with this machine's log
  (`Valid, sectors: 0` for the one shipped alt-biome), though that is not proof on its own.
* **`m_notNeighbor`'s index/flag mismatch, exactly.** The mask bit examined at step `j` is
  `((BiomeIndex)j).ToBiome()`, but the neighbour is compared against `(Heightmap.Biome)j`. So:
  `j=0` requires nothing usable (rejects only on a `None` neighbour, which cannot exist — harmless);
  `j=1` Meadows bit → rejects a **Meadows(1)** neighbour (correct); `j=2` Swamp bit → **Swamp(2)**
  (correct); `j=3` Mountain bit → `(Biome)3`, unreachable; `j=4` BlackForest bit → rejects a
  **Mountain(4)** neighbour; `j=5` Plains, `j=6` AshLands, `j=7` DeepNorth → `(Biome)5/6/7`,
  unreachable; `j=8` Ocean bit → rejects a **BlackForest(8)** neighbour; `j=9` Mistlands bit →
  `(Biome)9`, unreachable.
* **Settled, replacing the earlier "Unverified": vanilla 1.0.15 ships 28 `AltBiome`s and every one has
  `m_enabled = true`.** This was already established in the knowledge base
  (`.claude\skills\valheim-worldgen\references\zones-locations-vegetation.md` §3.4, verified
  2026-09-22) by a structural scan of the decompressed shipped bundle
  `valheim_Data\StreamingAssets\SoftRef\Bundles\d59cfac`, which holds 28 contiguous `AltBiome` records
  at offsets 5156960–5181556. Names by biome — Meadows: Dark Meadows, Peaceful Meadows, Dandelion
  Meadows, Raspberry Meadows, Smalltree Meadows, Birch Meadows · Black Forest: Troll, Root, Ruin, Rock,
  Pinetree, Blueberry, Kalhygge · Swamp: Hut, Bog, Bat, Abomination · Mountain: Wolf, Drake, Fortress ·
  Plains: Lox, Goblin, Death · Mistlands: Rockless, Trees, Swords, Hare, BroodSwarm. **§2.9 and filters
  10a/10b in §5.4 must be implemented in full.**
* Independent corroboration from this machine's log
  (`E:\SteamLibrary\steamapps\common\Valheim\BepInEx\LogOutput.log`, line 507, 2026-09-22 20:37:19,
  fresh world `testworldclaude`):
  `Loading: Placed 0/1-2 of 'Fortress Mountain' altbiome. (Valid, sectors: 0, combos: 2)`.
  That string comes only from `GenerateAltBiomes`'s trailing loop, and only on the `ZLog.LogWarning`
  branch (`altBiome2.Sectors.Count < altBiome2.m_minAmountSpawned`) — the other 27 met their minimum
  and logged through `ZLog.DevLog`, invisible in a release build. From that one line, for
  `"Fortress Mountain"`: `m_minAmountSpawned == 1`, `m_maxAmountSpawned == 2` (the code default for max
  is 10, so the asset overrides it); `combos: 2` is `ValidPlacementSectorCombos`, incremented once per
  `Biomes` dictionary key for which `m_biome.HasFlag(key)` held — the `None (0)` key always holds, so
  exactly one other of the 11 remaining keys did, i.e. **`m_biome` is a single biome flag** (any mask of
  two or more bits would match at least two single-bit keys), consistent with the KB's classification of
  it as a Mountain alt-biome; and `sectors: 0` is `ValidPlacementSectors`, so `CanAddModifier` returned
  false for every Mountain sector **in that world** — a per-seed outcome, not a general one.
* **`GenerateAltBiomes` never clears `AltBiome.Sectors`** — it resets only `ValidPlacementSectors` and
  `ValidPlacementSectorCombos`, while the quotas test `validAltBiome2.Sectors.Count` and
  `BiomeSector.AddModifier` does `modifier.Sectors.Add(this)`. **This is a trap for exactly the tool
  this spec is for:** a multi-seed sweep that reuses one set of `AltBiome` objects across worlds starves
  every run after the first (the counts never reset, so `Sectors.Count < m_maxAmountSpawned` fails
  immediately) and leaks each previous world's 2048² grid through the stale `BiomeSector` references.
  In the game this is masked because a `main` scene load re-instantiates the `AltBiomeList` prefab and
  builds fresh `AltBiome` objects (`ZoneSystem.Awake` → `Instantiate`, `AltBiomeList.Awake`/`OnDestroy`).
  A port must reset `Sectors` per world explicitly.
* `AltBiome` field defaults, read from the decompiled class (these are the code defaults; the asset may
  override any of them — the dumper decides): `m_enabled = true`, `m_levelUpChanceMultiplier = 1f`,
  `m_minDistanceFromCenter = 1000f`, `m_minAmountSpawned = 1`, `m_maxAmountSpawned = 10`,
  `m_chance = 0.1f`, `m_requireNeighbor = None`, `m_notNeighbor = None`, `m_minEdgeSize = 50`,
  `m_maxEdgeSize = 1500`, `m_minAvgHeight = 30f`, `m_maxAvgHeight = 10000f`,
  `m_belowWorldX/m_aboveWorldX/m_belowWorldY/m_aboveWorldY = 0f`. `ValidPlacementSectors` and
  `ValidPlacementSectorCombos` are `[NonSerialized]`.

### 2.10 Randomness consumed by the biome-data build

| Routine | RNG stream | Draws |
|---|---|---|
| `GenerateBiomePoints` | — | **none** |
| `GenerateSectors` (flood fill, edges, post pass) | — | **none** |
| `GenerateAltBiomes` | ambient global, re-seeded per (biome, altbiome) pair | shuffle `Count-1` ints + 1 float per sector examined |
| `GetRandomPointByBiome(s)[AboveSeaLevel]` | **the calling location's stream** | 1 int (+1 from `RandomBiomeFromBiomes` if multi-bit) |

Because `GenerateAltBiomes` re-seeds and every location type re-seeds, there is **no cross-talk**. A port
can model each location type's stream independently and skip the ambient stream entirely unless it needs
alt-biomes.

---

## 3. `ZoneSystem.SetupLocations` — how `m_locations` is assembled and ordered

Call chain: `ZoneSystem.Awake` instantiates every prefab in `m_locationLists` and `m_altBiomeLists`
(`LocationList.Awake` / `AltBiomeList.Awake` run synchronously inside `Instantiate`, appending to the
statics `LocationList.m_allLocationLists` and `AltBiomeList.m_altBiomes` in list order). Then
`ZoneSystem.Start` → `SetupLocations()`.

```csharp
private void SetupLocations() {
    List<LocationList> all = LocationList.GetAllLocationLists();
    all.Sort((a, b) => a.m_sortOrder.CompareTo(b.m_sortOrder));      // List<T>.Sort -> UNSTABLE introsort
    foreach (LocationList l in all) {
        m_locations.AddRange(l.m_locations);
        m_vegetation.AddRange(l.m_vegetation);
        ... environments / clutter / events ...
    }
    foreach (AltBiome a in AltBiomeList.m_altBiomes) {
        m_locations.AddRange(a.m_addLocations);
        foreach (var loc in a.m_addLocations) loc.AltBiomeParent = a.m_name;
        m_vegetation.AddRange(a.m_addVegetation);
        ...
    }
    foreach (ZoneLocation loc in m_locations)                        // build hash index
        if ((loc.m_enable || loc.m_prefab.IsValid) && Application.isPlaying) {
            loc.m_prefabName = loc.m_prefab.Name;
            int hash = loc.Hash;                                     // m_prefab.Name.GetStableHashCode()
            if (!m_locationsByHash.ContainsKey(hash)) m_locationsByHash.Add(hash, loc);
            else ZLog.LogError("Duplicate locations ... Ignoring ...");   // later entry dropped from the index only
        }
    ...
}
```

Final order of `m_locations`:

1. **`ZoneSystem.m_locations` as serialized on the ZoneSystem prefab** (`ZoneSystem` line 502:
   `public List<ZoneLocation> m_locations = new List<ZoneLocation>();`, under `[Header("Generation
   data")]`, no `[NonSerialized]`; `AddRange` appends to it).
2. Each `LocationList.m_locations`, lists ordered by `m_sortOrder` ascending.
3. **Every** `AltBiome.m_addLocations` in `AltBiomeList.m_altBiomes`, tagged with
   `AltBiomeParent = altBiome.m_name`.

**Corrected:** step 3 is *not* filtered by `m_enabled`. `ZoneSystem.SetupLocations` runs
`foreach (AltBiome altBiome in AltBiomeList.m_altBiomes) { m_locations.AddRange(altBiome.m_addLocations); ... }`
with no `m_enabled` test, and `AltBiomeList.Awake` does `m_altBiomes.AddRange(m_alts)` unconditionally.
(Contrast `AltBiomeList.GetValidAltBiomes`, which *does* test `altBiome.m_enabled` — that is the only
place the flag is used on this path.) Consequence: a **disabled** alt-biome still injects its
`m_addLocations` into `m_locations` with `AltBiomeParent` set, and those entries then burn their whole
attempt budget failing filter 10a, because no sector will ever carry a disabled alt-biome.

Also note `LocationList.GetAllLocationLists()` returns the static `m_allLocationLists` **itself**, not a
copy, so `SetupLocations` sorts that static list in place.

Observed on this machine (`BepInEx\LogOutput.log`, fresh world `testworldclaude`, 2026-09-22 20:37:16):
six `LocationList`s contributed **3, 2, 27, 4, 25, 25 = 86** locations, all from scene `main` (i.e. all
instantiated from `m_locationLists`, none from `m_locationScenes`). The run then processed **183**
location entries (`"locations: 183"` in the `Loading: Done.` line is `m_locationsRun`, which is
incremented exactly once per element of `ordered`, so it equals `ordered.Count` after filtering), so
`ZoneSystem.m_locations`'s own serialized part plus any `AltBiome.m_addLocations` contribute roughly 97
enabled entries with `m_quantity != 0`. Exact composition must come from the dumper.

**Caveat on the 183 figure:** the install that produced the log was **modded** (16 BepInEx plugins).
183 is *that install's* count; that it equals an unmodded 1.0.15's count is **Unverified**. The dumper
must record the plugin set alongside the table (see §10.1 and the versioning rule in §7).

**What this means for the tool:** do **not** reimplement `SetupLocations`. Dump the final `m_locations`
list, in order, after `SetupLocations` has run, and treat it as data. Reasons:

* `List<T>.Sort(Comparison<T>)` is documented unstable. With 6 elements both the .NET and the Mono
  implementations fall back to insertion sort (partitions < 16), which happens to be stable, so today the
  result equals `m_locationLists` order broken by `m_sortOrder` — but that is an implementation detail of
  the BCL, not a guarantee, and it changes if a mod adds lists.
* The duplicate-hash rule only affects `m_locationsByHash` (used by `GetLocation(hash)` on load and by
  console commands). A duplicate entry **stays in `m_locations`** and is still generated.
* Any mod that patches `SetupLocations` changes the list and hence every downstream result.

`ZoneSystem.ZoneLocation` fields and defaults (all of these are inputs to §5):

```
m_name; m_enable=true; m_prefabName; m_prefab (SoftReference<GameObject>);
m_biome (bitmask); m_biomeArea = Everything(3); m_quantity; m_prioritized; m_centerFirst; m_unique;
m_group=""; m_minDistanceFromSimilar; m_groupMax=""; m_maxDistanceFromSimilar;
m_iconAlways; m_iconPlaced; m_randomRotation=true; m_slopeRotation; m_snapToWater;
m_interiorRadius; m_exteriorRadius; m_clearArea;
m_minTerrainDelta=0; m_maxTerrainDelta=2;
m_minimumVegetation=0; m_maximumVegetation=1;
m_surroundCheckVegetation; m_surroundCheckDistance=20; m_surroundCheckLayers=2; m_surroundBetterThanAverage;
m_inForest; m_forestTresholdMin=0; m_forestTresholdMax=1;
m_minDistanceFromCenter; m_maxDistanceFromCenter;
m_minDistance; m_maxDistance; m_minAltitude=-1000; m_maxAltitude=1000;
Hash => m_prefab.Name.GetStableHashCode();  AltBiomeParent (string, null unless from an AltBiome)
```

`ZoneSystem.LocationInstance` is a **struct**: `{ ZoneLocation m_location; Vector3 m_position; bool m_placed; }`.
That it is a struct matters — see §6.2.

---

## 4. `GenerateLocationsTimeSliced()` — the outer loop

```csharp
ClearNonPlacedLocations();
List<ZoneLocation> ordered = m_locations.OrderByDescending(a => a.m_prioritized).ToList();
for (int n = ordered.Count - 1; n >= 0; n--)
    if (!ordered[n].m_enable || ordered[n].m_quantity == 0) ordered.RemoveAt(n);
for (int i = 0; i < ordered.Count; i++) {
    ... time-slice yield ...
    yield return GenerateLocationsTimeSliced(ordered[i], timeSliceStopwatch, iterationsPkg);
    ... progress bookkeeping ...
}
LocationsGenerated = true;
```

* `Enumerable.OrderByDescending` is a **stable** sort. Descending on a bool puts `m_prioritized == true`
  first and preserves `m_locations` order inside each group. Reproduce as: *stable partition into
  (prioritized, rest)*.
* The removal pass runs **after** the sort and iterates backwards, so relative order is preserved.
* `ClearNonPlacedLocations()`: rebuilds `m_locationInstances` keeping only `m_placed == true` entries, and
  calls `RemoveNonPlaced` on the three caches. **No-op on a fresh world.**
* Estimation numbers `200000 / 100000` in the progress math are **only** for the ETA display. They are
  not the attempt budget (which is 60000/12000, §5.1). Do not confuse them.
* The time-slice `yield` between location types does not touch RNG; the per-location routine saves and
  restores the ambient state around itself (§5.2).
* Nothing here reads world modifiers, global keys, player count, difficulty or the date.

---

## 5. `GenerateLocationsTimeSliced(ZoneLocation, Stopwatch, ZPackage)` — the per-location routine

### 5.1 Prologue

```csharp
int seed = WorldGenerator.instance.GetSeed() + location.m_prefab.Name.GetStableHashCode();  // unchecked int add
UnityEngine.Random.State state = UnityEngine.Random.state;   // save ambient
UnityEngine.Random.InitState(seed);                          // open THIS type's stream
float maxRadius = Mathf.Max(location.m_exteriorRadius, location.m_interiorRadius);
int attempts = location.m_prioritized ? 60000 : 12000;
int iterations = 0;
int placed = CountNrOfLocation(location);                    // 0 on a fresh world
float maxRange = 10000f;
s_tempVeg.Clear();                                           // INSTANCE List<float>, per-location baseline
if (!location.m_unique || placed <= 0) {                     // unique + already present -> skip entirely
    if (location.m_centerFirst) maxRange = location.m_minDistance;
    while (i < attempts && placed < location.m_quantity) { ...outer attempt... i++; }
}
UnityEngine.Random.state = state;                            // restore ambient
```

* `WorldGenerator.GetSeed()` returns `m_world.m_seed` — the int seed from the `.fwl2`.
* `GetStableHashCode` is the two-lane djb2-xor from 5381 combined `num + num2 * 1566083941`, unchecked
  32-bit (`StringExtensionMethods.GetStableHashCode`). The string hashed is
  **`m_prefab.Name`** — the soft-reference asset name (= the prefab name, e.g. `"StartTemple"`), *not*
  `ZoneLocation.m_name`.
* Both additions are **unchecked** C# int arithmetic and routinely overflow. Use `unchecked` in the port.
* **Corrected:** `s_tempVeg` is *not* static. `ZoneSystem` line 418 declares
  `private List<float> s_tempVeg = new List<float>();` — an **instance** field, despite the `s_` prefix.
  It is cleared here, i.e. **per location type**, and shared across all of that type's attempts (§5.4
  filter 11). It is also used by `ZoneSystem.PlaceVegetation` (same field, cleared at the top of each
  vegetation entry and appended to in its own surround check). During a fresh-world genloc the two
  cannot interleave, because `ZoneSystem.Update` returns before `CreateLocalZones` while
  `IsServer && !LocationsGenerated`. On a console `genloc` re-run they **can** — see §8(c).

### 5.2 Time slicing does not affect determinism

At each `yield return null` inside the loops:

```csharp
UnityEngine.Random.State insideState = UnityEngine.Random.state;
UnityEngine.Random.state = state;          // hand the ambient state back to the rest of the game
yield return null;
timeSliceStopwatch.Restart();
state = UnityEngine.Random.state;          // re-capture whatever the game left
UnityEngine.Random.state = insideState;    // resume this location's stream exactly
```

The location's stream is bit-identical regardless of how many frames the coroutine spans. A port models
one independent PRNG per location type and ignores time slicing entirely.

### 5.3 One outer attempt = one candidate zone

```csharp
Vector2s zoneID = location.m_centerFirst
    ? GetRandomZone(maxRange)
    : (!(location.m_minAltitude < 0f)
        ? GetZone(AltBiomeWorldData.MapSpaceToWorldSpace(ZNet.World.m_biomeData.GetRandomPointByBiomesAboveSeaLevel(location.m_biome)))
        : GetZone(AltBiomeWorldData.MapSpaceToWorldSpace(ZNet.World.m_biomeData.GetRandomPointByBiomes(location.m_biome))));
if (location.m_centerFirst) maxRange++;          // AFTER the draw

if (m_locationInstances.ContainsKey(zoneID))      { errorLocationInZone++; }          // reject
else if (!IsZoneGenerated(zoneID)) {
    Vector2s zoneCenter = GetZoneCenter(zoneID);                                      // (id.x*64, id.y*64) as shorts
    Heightmap.BiomeArea biomeArea = WorldGenerator.instance.GetBiomeArea(zoneCenter);
    if ((location.m_biomeArea & biomeArea) == 0) { errorBiomeArea++; }                // reject
    else { for (int j = 0; j < 6; j++) { ...point try... } }
}
// (if the zone IS generated: silently skipped, no counter, no draws)
i++;
```

Branch selection: `m_centerFirst` wins; otherwise `m_minAltitude >= 0` selects the above-sea-level list,
`m_minAltitude < 0` (the default `-1000`) selects the full list.

`ZoneSystem.GetRandomZone(float range)`:

```csharp
int num = (int)range / 64;                    // truncating cast, then integer divide
Vector2s v;
do { v = new Vector2s(UnityEngine.Random.Range(-num, num), UnityEngine.Random.Range(-num, num)); }
while (!(GetZonePos(v).magnitude < 10000f));  // repeat while >= 10000 m (or NaN)
return v;
```

* **2 int draws per do-while iteration**, repeated until accepted. `Random.Range(int,int)` has an
  **exclusive** upper bound, so the reachable zone range is `[-num, num-1]`.
* With `num == 0` the call is `Random.Range(0, 0)`. **Unverified: whether Unity's native
  `Random.Range(int,int)` advances the PRNG state when `min == max`.** This is not academic: a
  `m_centerFirst` location with a small `m_minDistance` spends its first ~64 attempts at `num == 0`. Must
  be measured (§10.2). The accept test passes immediately (`|(0,0)| = 0 < 10000`), so the loop terminates.
* `maxRange` grows by exactly **+1 m per outer attempt**, and is incremented *after* the draw. So attempt
  `k` (0-based) uses `range = m_minDistance + k`.
* Acceptance is on `Vector3(v.x*64, 0, v.y*64).magnitude < 10000f`, i.e. a disc of 156.25 zones. Once
  `num > 157` most draws are rejected; each rejection costs another 2 draws.
* `Vector2s` holds **shorts**; `new Vector2s(int,int)` truncates. No overflow occurs for legal ranges
  (|zone| ≤ 156 for accepted `centerFirst` zones, ≤ 192 for biome-point zones).

`ZoneSystem.GetZone` / `GetZonePos` / `GetZoneCenter`:

```csharp
public static Vector2s GetZone(Vector3 p) {
    int x = Utils.FloorToInt((float)(((double)p.x + 32.0) / 64.0));
    int y = Utils.FloorToInt((float)(((double)p.z + 32.0) / 64.0));   // note: p.z
    return new Vector2s(x, y); }
public static Vector3 GetZonePos(Vector2s id) => new Vector3(id.x * 64f, 0f, id.y * 64f);   // zone CENTRE
private static Vector2s GetZoneCenter(Vector2s id) => new Vector2s(id.x * 64, id.y * 64);
// Utils.FloorToInt(f) => (int)(f + 64000f) - 64000
```

The `(double)` promotion then narrowing to `float` before `FloorToInt` is observable at zone boundaries;
port it literally.

`WorldGenerator.GetBiomeArea(Vector2s point)` — this is the **Vector2s overload**, not the Vector3 one:

```csharp
if (s_cachedBiomeAreas.TryGetValue(point, out value)) return value;     // static memo, cleared in WorldGenerator..ctor
Heightmap.Biome b = GetBiome(point);                                    // GetBiome(point.x, point.y) with a memo
// 8 neighbours at point - offset, offsets = (-64,-64) (64,-64) (64,64) (-64,64) (-64,0) (64,0) (0,-64) (0,64)
value = (any neighbour biome != b) ? BiomeArea.Edge : BiomeArea.Median;
```

`point.x`/`point.y` are **world metres** here (the zone centre), and `GetBiome(Vector2s)` calls
`GetBiome((float)point.x, (float)point.y)` — the raw procedural biome, **not** the 12 m sector grid. The
static memo dictionaries are per-`WorldGenerator` instance in effect (cleared in the ctor); a
multi-seed search tool **must** key or clear them per seed.

`Heightmap.BiomeArea`: `Edge = 1, Median = 2, Everything = 3`. The default `m_biomeArea` is `Everything`,
so this filter only bites for entries that explicitly want `Edge` or `Median`.

### 5.4 One point try (up to 6 per accepted zone) — filters in exact order

```csharp
iterations++;
Vector3 p = GetRandomPointInZone(zoneID, maxRadius);      // 2 float draws
float magnitude = p.magnitude;                            // y is still 0 here -> horizontal distance from origin
```

`ZoneSystem.GetRandomPointInZone`:

```csharp
Vector3 zonePos = GetZonePos(zone);
float x = UnityEngine.Random.Range(-32f + locationRadius, 32f - locationRadius);
float z = UnityEngine.Random.Range(-32f + locationRadius, 32f - locationRadius);
return zonePos + new Vector3(x, 0f, z);
```

**Exactly 2 float draws, always, before any filter runs.** If `maxRadius > 32` then `min > max`.
**Unverified: what Unity's `Range(float,float)` does when `min > max`.** The managed side is
`[MethodImpl(InternalCall)] public static extern float Range(float minInclusive, float maxInclusive)`
(decompiled `UnityEngine.Random`) — the implementation is native, and the parameter is named
*maxInclusive*, not maxExclusive. The common assumption is that it lerps and therefore returns a value in
`[max, min]`, letting the point land **outside** the candidate zone (see §6.1 for the consequence), but
nothing in managed code establishes that, nor whether it consumes the same amount of state as the normal
case. Measure it (§10.2 experiment 4) before relying on either behaviour.

| # | Filter | Exact test | Counter | RNG? |
|---|---|---|---|---|
| 1 | min/max distance from origin | `m_minDistance != 0 && magnitude < m_minDistance` → reject; `m_maxDistance != 0 && magnitude > m_maxDistance` → reject | `errorCenterDistance` | no |
| 2 | biome | `b = WorldGenerator.GetBiome(p)` (= `GetBiome(p.x, p.z)`); reject if `(m_biome & b) == 0` | `errorBiome` | no |
| 3 | altitude | `p.y = WorldGenerator.GetHeight(p.x, p.z, out Color mask);` `float alt = (float)((double)p.y - 30.0);` reject if `alt < m_minAltitude \|\| alt > m_maxAltitude` | `errorAltitude` | no |
| 4 | forest | only if `m_inForest`: `f = WorldGenerator.GetForestFactor(p)`; reject if `f < m_forestTresholdMin \|\| f > m_forestTresholdMax` | `errorForest` | no |
| 5 | distance-from-centre (second, redundant pair) | only if `m_minDistanceFromCenter > 0 \|\| m_maxDistanceFromCenter > 0`: `d = Utils.LengthXZ(p)`; reject if `(min>0 && d<min) \|\| (max>0 && d>max)` | **none** | no |
| 6 | terrain delta | `WorldGenerator.GetTerrainDelta(p, m_exteriorRadius, out delta, out _)`; reject if `delta > m_maxTerrainDelta \|\| delta < m_minTerrainDelta` | `errorTerrainDelta` | **YES — 10 × `Random.insideUnitCircle`** |
| 7 | min distance from similar | only if `m_minDistanceFromSimilar > 0`: reject if `HaveLocationInRange(m_prefab.m_assetID, m_group, p, m_minDistanceFromSimilar)` | `errorSimilar` | no |
| 8 | max distance from similar | only if `m_maxDistanceFromSimilar > 0`: reject if `!HaveLocationInRange(m_prefab.m_assetID, m_groupMax, p, m_maxDistanceFromSimilar, maxGroup: true)` | `errorNotSimilar` | no |
| 9 | vegetation mask | `a = mask.a` (from filter 3); reject if `m_minimumVegetation > 0 && a <= m_minimumVegetation`; reject if `m_maximumVegetation < 1 && a >= m_maximumVegetation` | `errorVegetation` | no |
| 10a | alt-biome parent | `s = WorldGenerator.GetBiomeSector(p)`; reject if `AltBiomeParent != null && !s.AltBiomes.Any(x => x.m_name == AltBiomeParent)` | `errorAltBiomeMissing` | no |
| 10b | alt-biome block | reject if `s.AltBiomes.Any(x => x.m_blockLocationNames.Contains(location.m_name))` — matches **`m_name`**, not the prefab name | `errorAltBiomeBlock` | no |
| 11 | surround vegetation | only if `m_surroundCheckVegetation`; see below | none | no |

Filter 11 verbatim:

```csharp
float score = 0f;
for (int layer = 0; layer < m_surroundCheckLayers; layer++) {
    float r = (float)(layer + 1) / m_surroundCheckLayers * m_surroundCheckDistance;
    for (int k = 0; k < 6; k++) {
        float f = (float)k / 6f * MathF.PI * 2f;
        Vector3 v = p + new Vector3(Mathf.Sin(f) * r, 0f, Mathf.Cos(f) * r);
        WorldGenerator.instance.GetHeight(v.x, v.z, out Color mask2);
        float w = (m_surroundCheckDistance - r) / (m_surroundCheckDistance * 2f);
        score += mask2.a * w;
    }
}
s_tempVeg.Add(score);
if (s_tempVeg.Count < 10) continue;                     // first 9 qualifying points ALWAYS rejected
float cutoff = s_tempVeg.Average() + (s_tempVeg.Max() - s_tempVeg.Average()) * m_surroundBetterThanAverage;
if (score < cutoff) continue;
```

`s_tempVeg` is never trimmed and keeps growing for the whole location type, including the accepted
values. The outermost ring has weight `w = 0` (when `layer + 1 == m_surroundCheckLayers`, `r ==
m_surroundCheckDistance`), so the outermost layer contributes nothing.

**Critical, easily-missed fact about `mask.a` (filters 9 and 11):** `WorldGenerator.GetBiomeHeight` sets
`mask = Color.black` and only three biome branches overwrite it:
`GetMistlandsHeight` → `new Color(0,0,0,num5)`, `GetAshlandsHeight` → `new Color(0,0,0,num19)`, and
`GetDeepNorthHeight` → `new Color(0, num9, 0, 0)` — **green, not alpha**. Therefore
**`mask.a` is non-zero only in Mistlands and AshLands**; everywhere else (including Deep North) it is
exactly `0f`. So `m_minimumVegetation > 0` makes a location *unplaceable outside Mistlands/AshLands*, and
the surround check scores 0 for every sample outside them (then the `< 10` rule rejects the first 9 and
the `score >= cutoff` test is `0 >= 0`, which passes). Reproduce, do not "improve".

On success:

```csharp
RegisterLocation(location, p, generated: false);
placed++;
break;               // leaves the j-loop: at most ONE instance per candidate zone
```

### 5.5 What a rejected attempt has already consumed

This is the part a port gets wrong most easily. Per **outer attempt**:

| Path | Draws consumed, in order |
|---|---|
| `m_centerFirst` | `2 * (number of do-while iterations in GetRandomZone)` ints |
| multi-bit `m_biome`, not centerFirst | 1 int (`RandomBiomeFromBiomes`) + 1 int (list index) |
| single-bit `m_biome`, not centerFirst | 1 int (list index) |

Then, if the zone survives the `ContainsKey` / `IsZoneGenerated` / `m_biomeArea` gates, per **point try**
(up to 6):

| Stage reached | Draws consumed by that try |
|---|---|
| any try at all | 2 floats (`GetRandomPointInZone`) |
| survived filters 1–5 | + 10 × `Random.insideUnitCircle` (`GetTerrainDelta`) |
| rejected at filter 1, 2, 3, 4 or 5 | nothing beyond the 2 floats |
| rejected at filter 6..11 | the 2 floats **and** the 10 `insideUnitCircle` samples |

And, crucially: **a zone rejected at `ContainsKey` / `IsZoneGenerated` / `m_biomeArea` consumes the zone
draw and nothing else** — no point draws at all. The `i++` happens for every outer attempt regardless of
outcome; the loop terminates on `i >= attempts` or `placed >= m_quantity`.

`WorldGenerator.GetTerrainDelta` (the one used here — note `ZoneSystem` has a private same-named method
that uses physics; that one is only for slope rotation at placement time):

```csharp
int num = 10; float max = -999999f, min = 999999f; Vector3 hi = center, lo = center;
for (int i = 0; i < 10; i++) {
    Vector2 o = UnityEngine.Random.insideUnitCircle * radius;
    Vector3 s = center + new Vector3(o.x, 0f, o.y);
    float h = GetHeight(s.x, s.z);
    if (h < min) { min = h; lo = s; }
    if (h > max) { max = h; hi = s; }
}
delta = (float)((double)max - (double)min);
slopeDirection = Vector3.Normalize(lo - hi);
```

`radius` here is `location.m_exteriorRadius` (**not** `maxRadius`). With `m_exteriorRadius == 0` all ten
samples collapse to the centre and `delta == 0`, but the ten draws are still consumed.

### 5.6 Epilogue

The failure log line is `"Failed to place all {prefabName}, placed {placed} out of {quantity} with
{errors} tries in {elapsed}"`. In a **release** build `UnityEngine.Debug.isDebugBuild` is false, so the
error total prints as `0` and `m_IterationsRun` stays 0 — matching this machine's log
(`iterations: 0`, `with 0 tries`). The error counters themselves still increment; only the aggregation is
gated. No RNG effect. There is **no retry** after a shortfall.

---

## 6. Registration, counting, range tests, unique handling

### 6.1 `ZoneSystem.RegisterLocation`

```csharp
private void RegisterLocation(ZoneLocation location, Vector3 pos, bool generated) {
    LocationInstance v = { m_location = location, m_position = pos, m_placed = generated };
    Vector2s zone = GetZone(pos);
    if (m_locationInstances.ContainsKey(zone)) { ZLog.LogWarning("Location already exist in zone " + zone); return; }
    m_locationInstances.Add(zone, v);
    AddToCache(m_locationIDCache,       v.m_location.m_prefab.m_assetID, v);
    AddToCache(m_locationGroupCache,    v.m_location.m_group,            v);
    AddToCache(m_locationMaxGroupCache, v.m_location.m_groupMax,         v);
}
```

* The zone is **recomputed from the point**, not taken from the candidate `zoneID`. They agree whenever
  `maxRadius <= 32`. If `maxRadius > 32` the point can fall outside the candidate zone (§5.4), in which
  case (a) it may be registered in a zone the biome-area filter never checked, or (b) the target zone may
  already be occupied → the early `return` fires, **but the caller still does `placed++`**. A location
  type with `Max(m_exteriorRadius, m_interiorRadius) > 32` can therefore report fewer instances than
  `placed` claims. Handle this case explicitly; dump the radii to find out which types hit it.
* Group keys are added **even when empty** (`""`), so `m_locationGroupCache[""]` accumulates every
  instance whose `m_group` is empty. `HaveLocationInRange` never looks up an empty key, so this is inert —
  but it is a memory trap if you mirror the structure.
* `m_locationIDCache` is keyed by the prefab's `AssetID` (`SoftReferenceableAssets.AssetID`, four
  `uint` fields in declaration order `v3, v2, v1, v0` — confirmed by Mono.Cecil against
  `SoftReferenceableAssets.dll`). **Two different `ZoneLocation` entries that point at the same prefab
  share one bucket**, and would then also share a stream seed, because both derive from
  `m_prefab.Name` (`SoftReference<T>.Name` returns the cached `m_name`, else the file name of
  `Runtime.GetAssetPath(m_assetID)` — decompiled IL of `SoftReference\`1.get_Name`).
* **Corrected:** the `TarPit1` / `TarPit1_1` pair is *not* an example of this. Every one of those log
  lines is formatted with `location.m_prefab.Name` (`GenerateLocationsTimeSliced`:
  `$"Failed to place all {location.m_prefab.Name}, ..."`), so `TarPit1` and `TarPit1_1` are two
  **different** prefab names — different `AssetID`s, different buckets, different stream seeds.
  **Unverified: whether any two `ZoneLocation` entries in 1.0.15 actually share a prefab.** The dumper
  settles it by comparing `m_prefab.m_assetID` across the table; note that a shared prefab would also
  make `CountNrOfLocation` (§6.3) non-zero for the second entry.

### 6.2 The caches store struct **copies** — a real quirk

`LocationInstance` is a struct and `AddToCache` appends by value. A cached copy's `m_placed` is frozen at
whatever `RegisterLocation(location, pos, generated)` was given. `PlaceLocations` later sets
`m_placed = true` only on the dictionary entry (`m_locationInstances[zoneID] = value`), never on the
cached copy. Consequences — **corrected; the first draft overstated this**:

* Within a single fresh generation run: irrelevant (nothing reads `m_placed` from the caches during the
  run, and every registration is `generated: false` in both places anyway).
* **The caches are *not* uniformly stale after a world load.** `ZoneSystem.Load` replays every saved
  instance through `RegisterLocation(location, pos, generated)` with `generated` read from the `.db2`
  (`bool generated = zPackage.ReadBool();`), so a location that was already placed when the world was
  saved gets `m_placed == true` in the dictionary **and** in all three caches, and survives
  `RemoveNonPlaced`.
* The staleness is confined to instances that `PlaceLocations` promoted **during the current session**,
  i.e. locations that were generated in-world since the last load. For those, and only those, the cached
  copy still says `!m_placed`.
* On a **re-run** (`genloc`, or a `m_locationVersion` bump that forces regeneration after a load):
  `ClearNonPlacedLocations()` → `RemoveNonPlaced(cache.Values)` drops every cached entry whose copy says
  `!m_placed`. That correctly drops genuine candidates, and additionally drops the session-placed
  instances described above — which do survive in `m_locationInstances`. So the min/max
  distance-from-similar tests on the second run are blind to exactly that subset, not to every placed
  location.
* Same mechanism in `RemoveUnplacedLocations(location)` (§6.5), which passes the specific location so
  only that `ZoneLocation`'s entries are considered.

### 6.3 `CountNrOfLocation`

```csharp
int num = 0;
foreach (LocationInstance v in m_locationInstances.Values)
    if (v.m_location.m_prefab.Name == location.m_prefab.Name) num++;
return num;
```

Compares by **prefab name string**, over all instances regardless of `m_placed`. On a fresh world it is
always 0. If two `ZoneLocation` entries share a prefab name, the second entry starts with the first's
count already on the board and places fewer. (`ZoneSystem.CheckLocationDuplicates` exists to warn about
exactly this, but **it is dead code — nothing calls it** in 1.0.15: the only occurrence of the name in
`ZoneSystem.cs` is its own definition. So the absence of a duplicate warning in the log proves nothing.
The real load-time warning is the `"Duplicate locations found..."` `ZLog.LogError` in `SetupLocations`,
which fires on a duplicate **hash**, not a duplicate prefab, and it does not appear in this machine's
log.)

### 6.4 `HaveLocationInRange`

```csharp
private bool HaveLocationInRange(AssetID assetID, string group, Vector3 p, float radius, bool maxGroup = false) {
    if (m_locationIDCache.TryGetValue(assetID, out var a) && IsAnyWithinRadius(a)) return true;
    if (group.Length > 0 && !maxGroup && m_locationGroupCache.TryGetValue(group, out var b) && IsAnyWithinRadius(b)) return true;
    if (((group.Length > 0) & maxGroup) && m_locationMaxGroupCache.TryGetValue(group, out var c) && IsAnyWithinRadius(c)) return true;
    return false;
    bool IsAnyWithinRadius(List<LocationInstance> l) {
        for (int i = 0; i < l.Count; i++) if ((l[i].m_position - p).sqrMagnitude < radius * radius) return true;
        return false; }
}
```

* Distance is **3-D** (`Vector3.sqrMagnitude`, strictly `<`), and both positions carry
  `y = WorldGenerator.GetHeight(...)` (set by filter 3), so terrain height participates. Do not simplify
  to 2-D.
* The **AssetID bucket is always consulted**, for both the min and the max test. So
  `m_maxDistanceFromSimilar` is satisfied by any same-prefab instance *or* any instance sharing
  `m_groupMax`.
* Unplaced candidates count — during a fresh run, every instance is unplaced.

### 6.5 `m_unique`, `PlaceLocations`, `RemoveUnplacedLocations`

`GenerateLocationsTimeSliced` skips a type entirely when `location.m_unique && CountNrOfLocation(...) > 0`.
During a fresh run that never triggers, so **a unique type registers up to `m_quantity` candidates like
any other type**.

`ZoneSystem.PlaceLocations(zoneID, ...)` runs during zone generation, only when the zone holds an
**unplaced** instance:

```csharp
Vector3 p = value.m_position;
GetGroundData(ref p, out _, out _, out _, out _);        // terrain raycast, modifies the LOCAL p only
if (m_snapToWater) p.y = 30f;
if (m_clearArea) clearAreas.Add(new ClearArea(p, m_exteriorRadius));
Quaternion rot = Quaternion.identity;
if (m_slopeRotation) { GetTerrainDelta(p, m_exteriorRadius, out _, out dir); ... snapped to 22.5° ... }
else if (m_randomRotation) rot = Quaternion.Euler(0f, UnityEngine.Random.Range(0, 16) * 22.5f, 0f);
int seed = WorldGenerator.instance.GetSeed() + zoneID.x * 4271 + zoneID.y * 9187;
SpawnLocation(value.m_location, seed, p, rot, mode, spawnedObjects);
value.m_placed = true; m_locationInstances[zoneID] = value;
if (m_unique)     RemoveUnplacedLocations(value.m_location);
if (m_iconPlaced) SendLocationIcons(0L);
```

* **`value.m_position` is never updated** — the stored/saved position is exactly the position produced by
  §5.4. This is what makes the `.db2` a perfect oracle for validating the port (§12).
* The rotation draws come from the **ambient** `UnityEngine.Random` state, with no `InitState` anywhere on
  the `SpawnZone`/`PlaceLocations` path. They are therefore **not predictable** from the seed.
* `RemoveUnplacedLocations(location)` removes every `m_locationInstances` entry with
  `loc.m_location == location` (reference equality on the `ZoneLocation` object) and `!m_placed`, then
  strips the three caches via `RemoveNonPlaced(..., location)`.

So for `m_unique && m_quantity > 1`: all candidates exist in the world until a player (or a ghost zone
around a peer) causes one of the candidate zones to generate; that one wins and the rest are deleted.
**The winner is a function of exploration order, not of the seed.** For `m_unique && m_quantity == 1`
there is only ever one candidate, and it is fully determined.

**Unverified: which vanilla entries set `m_unique`, and with what `m_quantity`.** Community lore says the
trader (`Vendor_BlackForest`) is unique with several candidates; that is exactly the case the tool must
present as "N candidate positions, one of which will become real". Settle with the dumper.

### 6.6 What the tool can and cannot predict about a placed location

| Thing | Predictable offline? |
|---|---|
| Instance position `(x, y, z)` as registered and saved | **Yes** (fresh world) |
| Which zone it sits in | Yes (`GetZone(pos)`) |
| Final in-world y after `GetGroundData` | No — built-heightmap terrain, not `GetHeight` (the saved value stays the `GetHeight` one) |
| Y-rotation | **No** — ambient RNG |
| Dungeon interior layout | Only if the generator's local XZ offset is zero or `m_useCustomInteriorTransform` is set; otherwise the unseeded rotation feeds `DungeonGenerator.GetSeed` |
| Unique winner among N candidates | **No** |
| Whether a map icon shows | Needs `m_iconAlways` / `m_iconPlaced` (asset data) + `Minimap.m_locationIcons` sprite table |

---

## 7. The dependency chain between location types

Each type has an **independent RNG stream** (`seed + prefabNameHash`). Adding, removing or reordering a
type never changes another type's *draws*. It can change their *outcomes*, through exactly four channels:

1. **Zone occupancy.** `m_locationInstances.ContainsKey(zoneID)` rejects any zone already claimed by an
   earlier type. One location per zone, globally.
2. **`m_locationIDCache`** (filters 7 and 8). Cross-type only when two `ZoneLocation` entries share a
   prefab `AssetID`.
3. **`m_locationGroupCache` / `m_locationMaxGroupCache`** (filters 7 and 8). Cross-type whenever two
   types share a non-empty `m_group` / `m_groupMax` string. This is the main designed coupling (e.g. a
   whole family of huts keeping its distance from another family).
4. **`CountNrOfLocation`** (by prefab **name**), which seeds `placed` and, for unique types, can skip the
   type entirely.

Direction: **strictly forward in `ordered`.** A type is affected only by types that precede it. Nothing
that comes later can change it.

**Therefore: to reproduce target type `T` exactly, simulate every entry of `ordered` from index 0 up to
and including `T`, in full** — you need their registered positions, not just their count, because
positions determine zone occupancy and the group-distance tests.

Practical consequences for the user's three targets:

* **Bosses.** Boss altars are the most likely `m_prioritized` entries (together with `StartTemple`), which
  would put them at the very front of `ordered` and make them cheap to compute — potentially only a
  handful of predecessors. **Unverified: which entries set `m_prioritized`.** Dump it; if boss altars are
  prioritized, a boss-only search is orders of magnitude cheaper than a full run.
* **Traders.** If the trader is `m_unique` with `m_quantity > 1`, the tool can compute the full candidate
  set exactly but cannot name the winner. Report all candidates.
* **Dungeons / camps / ore.** These are ordinary, high-quantity, non-prioritized entries deep in the list
  (this machine's log shows `Crypt4` at 200, `GoblinCamp2` at 200, `NorthVillage` at 135, `TarPit*` at
  16–100). Getting any one of them right requires simulating essentially the whole prefix — in practice
  the whole list.

A cheap early exit exists only in one direction: because a type's own stream is independent, you can
**skip a type entirely** if it can neither occupy a zone you care about nor share a group with your target
— but "occupy a zone you care about" is global, so in practice the only safe skip is "types after the
target".

---

## 8. Determinism classification

### (a) Fully determined by the int seed (plus `m_worldGenVersion`)

* `GetBiome` / `GetHeight` / `GetForestFactor` everywhere → the entire 2048² `PointBiomes` /
  `PointHeights` grid, the `BiomeSector` decomposition, `AllPoints` / `AllPointsAboveSeaLevel` and their
  exact ordering, `EdgeCount`, `Center`, `HeightMin/Max/Avg`, `Neighbors`, `DistanceFromCenter`.
* `WorldGenerator.GetBiomeArea` for any zone.
* The per-type RNG seed `seed + prefabNameHash`, the per-type stream, and hence the entire sequence of
  candidate zones and points — **given** the location table.
* The registered position of every instance and its zone, for a fresh world.
* `GenerateAltBiomes` outcomes, given the alt-biome table.

Note: the world **UID** and the world **name** do not enter placement at all
(`AltBiomeWorldData.RemoveCache(world.m_name)` only touches a cache file). Neither do world modifiers /
global keys, player count, simulation distance, frame rate, or the save's `netTime`.

### (b) Also needs asset data (dump it; it is not in code)

* The full `ZoneSystem.m_locations` list **in final order**, with every `ZoneLocation` field (§3).
* `m_prefab.Name` and `m_prefab.m_assetID` per entry (the name drives the stream seed **and** the saved
  hash; the AssetID drives the similar-distance cache).
* `ZoneSystem.m_locationVersion` (code default 1; the prefab value decides whether an existing world
  re-rolls).
* `AltBiomeList.m_altBiomes` (all fields of every `AltBiome`, especially `m_enabled`, `m_biome`,
  `m_chance`, `m_min/maxAmountSpawned`, `m_min/maxEdgeSize`, `m_min/maxAvgHeight`,
  `m_minDistanceFromCenter`, `m_incompatibleAltBiomes`, `m_addLocations`, `m_blockLocationNames`).
* `Minimap.m_locationIcons` (name → sprite) if the tool wants to mirror the in-game icon set.
* For dungeon prediction: each location prefab's `DungeonGenerator` local offset and
  `m_useCustomInteriorTransform` flag.

### (c) Not predictable at all

* **Y-rotation** of every placed location (ambient `UnityEngine.Random`, no seeding) — and, through it,
  the dungeon seed for any location whose generator has a non-zero local XZ offset.
* **Which candidate of a `m_unique` type survives** when `m_quantity > 1` (first zone generated wins).
* Anything after a **re-generation** (`genloc`, `m_locationVersion` bump) on a played world: placed
  instances are kept, generated zones are excluded, session-placed instances vanish from the
  similar-distance caches through the struct-copy quirk (§6.2), and unplaced candidates move. A console
  `genloc` is additionally **non-deterministic**, not
  merely different: `GenerateLocations()` does not clear `LocationsGenerated`, so `ZoneSystem.Update`'s
  early return (`IsServer && !LocationsGenerated`) no longer fires and zone spawning continues between
  the coroutine's frames. `ZoneSystem.PlaceVegetation` then clears and refills the **same**
  `s_tempVeg` instance list that filter 11 is accumulating into (§5.1), and `PlaceLocations` mutates
  `m_locationInstances` mid-run. Both depend on frame timing and player position. A port must refuse to
  model a `genloc` re-run.
* `SpawnLocationMidGame` placements (`PersistentEventSystem`) and console `location` spawns
  (`Random.Range(0, 99999)` seed, saving disabled).
* The exact ground y a location ends up at in-world (built heightmap + terrain edits ≠ `GetHeight`).

---

## 9. Reference constants, one place

```
Zone size                     64 m           ZoneSystem.c_ZoneSize
Zone half                     32 m           ZoneSystem.c_ZoneSizeHalf
Water level                   30 m           ZoneSystem.c_WaterLevel
World radius (biome grid cut) 10500 m        WorldGenerator.waterEdgeSqr = 110250000f
Zone accept radius (centreFirst) 10000 m     ZoneSystem.GetRandomZone
Biome grid                    2048 x 2048    AltBiomeWorldData.c_textureSize
Biome grid spacing            12 m           AltBiomeWorldData.c_pixelSize
Biome grid origin offset      -1024, +6 m    AltBiomeWorldData.c_halfWidth / c_halfPixel
Attempts (prioritized)        60000          GenerateLocationsTimeSliced
Attempts (normal)             12000          GenerateLocationsTimeSliced
Point tries per zone          6              GenerateLocationsTimeSliced
Terrain-delta samples         10             WorldGenerator.GetTerrainDelta
maxRange growth (centreFirst) +1 m / attempt GenerateLocationsTimeSliced
Surround-check ring count     6 per layer    GenerateLocationsTimeSliced
Surround-check baseline       first 10 values s_tempVeg
AltBiome global seed offset   +920           AltBiomeWorldData.GenerateAltBiomes
Zone spawn seed               seed + zx*4271 + zy*9187   ZoneSystem.PlaceLocations
Biome flags  None 0, Meadows 1, Swamp 2, Mountain 4, BlackForest 8, Plains 16,
             AshLands 32, DeepNorth 64, Ocean 256, Mistlands 512, Land 639, All 895
BiomeArea    Edge 1, Median 2, Everything 3
```

---

## 10. What the BepInEx dumper must capture

Hook `ZoneSystem.SetupLocations` (postfix) and `AltBiomeWorldData.GenerateAltBiomes` (prefix), and write
JSON.

**10.1 Location table** — for each entry of `ZoneSystem.m_locations`, in list order, with its index:
every field listed in §3, plus `m_prefab.Name`, `m_prefab.m_assetID` (all four uints),
`m_prefab.Name.GetStableHashCode()`, and `AltBiomeParent`. Also dump `ZoneSystem.m_locationVersion` and
the `ordered` list actually produced by `m_locations.OrderByDescending(a => a.m_prioritized)` after the
`m_enable`/`m_quantity` filter, so the port never has to trust its own sort. Cross-check: the dumped
`ordered.Count` must be **183** on *this* install (`LogOutput.log`, world `testworldclaude`,
2026-09-22 20:37:57). Dump the loaded BepInEx plugin list (GUID + version) into the same file: 183 is a
modded-install figure, and the table is only valid for the build+plugin set that produced it.

**10.2 UnityEngine.Random semantics** — the highest-risk item. **Corrected:** `UnityEngine.Random.State`
is a struct of four **`private`** ints (`s0..s3`), each carrying `[SerializeField]` (decompiled
`UnityEngine.Random`). They are not directly readable. The plugin must get at them by one of:
`JsonUtility.ToJson(UnityEngine.Random.state)` (works precisely because of the `[SerializeField]`
attributes), reflection over `typeof(UnityEngine.Random.State).GetFields(BindingFlags.NonPublic |
BindingFlags.Instance)`, or an unsafe reinterpret of the 16-byte struct. Having done that, it can record
the state before and after each call:

1. `InitState(k)` for a spread of k (0, 1, -1, int.MinValue, int.MaxValue, and the real
   `seed + nameHash` values) → dump `state.s0..s3`. Determine the seeding function.
2. `Random.value` × N from a known state → dump state after each and the float bits
   (`BitConverter.SingleToInt32Bits`). Determine the generator and the float mapping.
3. `Random.Range(int,int)` for `(0,0)`, `(0,1)`, `(-3,3)`, `(-n,n)` with large n → **state deltas**, to
   answer whether `min == max` consumes state, and to recover the integer mapping (modulo vs.
   multiply-shift, and the sign convention for negative `min`).
4. `Random.Range(float,float)` including `min > max` → value + state delta.
5. `Random.insideUnitCircle` → **state delta (how many draws)** and the exact value, over many samples.
   Test the hypothesis `angle = value*2π; r = sqrt(value)` (2 draws) against rejection sampling.
6. Confirm `Random.state` set/get round-trips exactly.

**10.3 Alt-biome table** — every field of every `AltBiome` in `AltBiomeList.m_altBiomes`, plus
`m_enabled`. The list is known to be non-empty (§2.9), so "record that it is empty" is not an option;
what the dump must answer instead is (a) the full field set of `"Fortress Mountain"` — above all
`m_biome`, `m_requireNeighbor`, `m_notNeighbor`, `m_minEdgeSize`/`m_maxEdgeSize`,
`m_minAvgHeight`/`m_maxAvgHeight`, `m_minDistanceFromCenter`, `m_chance`, `m_addLocations`,
`m_blockLocationNames` — (b) whether any other `AltBiome` exists (ones that meet their minimum log only
at `ZLog.DevLog` level and are invisible in a release build), and (c) per world,
`AltBiomeList.m_altBiomes[i].Sectors.Count` after `GenerateAltBiomes`, so the port can check whether any
sector ever receives a modifier. Also dump, for each entry, the `ZoneLocation`s in `m_addLocations` —
they are appended to `m_locations` regardless of `m_enabled` (§3) and carry `AltBiomeParent`.

**10.4 Ground truth for validation** — with the dumper loaded, create a throwaway world with a known
seed, let genloc finish, then dump `m_locationInstances` as `(zone, prefabName, x, y, z, placed)`. Also
dump `AltBiomeWorldData.Biomes[b].AllPoints.Count` and `AllPointsAboveSeaLevel.Count` per biome, plus the
first and last 100 entries of each list — that alone will catch the flood-fill ordering and the
missing-seed-point quirk (§2.5) without needing a full comparison.

---

## 11. Cost model (this is what decides the tool's architecture)

Per seed, a full location run costs roughly:

* **4 194 304** `GetBiome` + `GetBiomeHeight` calls for the point grid (the single largest cost).
* One 2048² flood fill (~4.2 M stack operations) plus one 2046² edge pass.
* 183 location types × up to 12 000 (or 60 000) zone draws × up to 6 point tries, each point try costing
  1 `GetBiome` + 1 `GetHeight`, and each surviving try a further **10** `GetHeight` calls.

Reference wall-clock, re-measured from the log currently on disk (`BepInEx\LogOutput.log`, fresh world
`testworldclaude`, 2026-09-22, in-game, Unity, one core — the earlier `test1` log the first draft cited
has since been overwritten and cannot be checked):

```
20:37:16  Loading: ZNet Start
20:37:19  Loading: Placed 0/1-2 of 'Fortress Mountain' altbiome.   <- end of VerifyBiomeData
20:37:23  Generating new world minimap done [4265ms]
20:37:24  Loading: Generating locations
20:37:57  Loading: Done. ... Genloc duration: 33417.005 ms ... locations: 183, iterations: 0
20:37:57  There are 18 that take a long time to generate (over 0.5 sec).
          Total slow location time is 18.5 seconds ...
```

So: ~3 s for the 2048² biome grid + flood fill, ~8 s from `ZNet Start` to `Loading: Generating
locations` (grid + minimap), then **33.4 s** for genloc, with **18** location types individually over
0.5 s (corrected from "13.4 s" and "6 types"). The slowest single types in that run were `GoblinHut03`
2.61 s, `GoblinHut02` 2.43 s, `NorthVillage` 1.34 s, `TarPit3_1` 1.25 s, `TarPit1_1` 1.29 s.

Memory per world, if `PointSectors` is stored as an `int` sector id rather than a reference:
16.8 MB heights + 4.2 MB biomes + 16.8 MB sector ids + ~16.8 MB `AllPoints` + up to 16.8 MB
`AllPointsAboveSeaLevel` ≈ **70 MB**. Parallelise across seeds, not within one seed, and pool the arrays.

**Implication for the seed-search feature:** a full location run per seed is far too expensive for a broad
sweep. The search must be staged: cheap `GetBiome`/`GetHeight` criteria first (biome fractions, distance
from spawn to a biome, etc.), then the location run only on survivors. Also note that the seed *text*
space collapses: the world is a function of the **32-bit** `m_seed`, so there are at most 2^32 distinct
worlds no matter how many seed strings exist, and distinct strings can collide onto the same world.

---

## 12. Validation plan (do this before trusting any result)

1. Port `GetBiome`/`GetHeight` first (spec 01) and validate `PointHeights` / `PointBiomes` against a
   dumped grid.
2. Validate `AllPoints` counts and the first/last 100 entries per biome (catches §2.5).
3. Implement `UnityEngine.Random` from the §10.2 measurements; validate by replaying a recorded
   sequence of `(state_before, call, value, state_after)` triples.
4. Run the full placement for the user's world **`asdasdasd`** (seed text `MWd8eV6svz`, int seed
   **-1772362158**, `worldGenVersion` 2) and compare, instance by instance, against the location list in
   `<Steam>\userdata\<accountId>\892970\remote\worlds\asdasdasd\_main.2.db2`. The location gate does
   exactly this, on the copy in `groundtruth\`
   (`dotnet run -c Release --project tools\SeedLab.LocationLab -- gate`);
   `vseed world asdasdasd --locations` shows the stored counts per prefab. **Read-only —
   Steam Cloud syncs that folder.**
   * The saved `(x, y, z)` are exactly the §5.4 positions (§6.5), so this is an exact comparison, not a
     fuzzy one, and `y` doubles as a check on the height port.
   * Expect mismatches only where a `m_unique` type has had candidates removed by
     `RemoveUnplacedLocations` (§6.5) and where the world has been played (generated zones). Compare the
     saved `placed` flag and the saved generated-zone set to tell those apart.
5. Only after step 4 passes for every non-unique type should the tool report positions to a user.

---

## 13. Open items (**Unverified**), with what would settle each

| Item | Why it is open | What settles it |
|---|---|---|
| `UnityEngine.Random` algorithm, seeding, and per-call state consumption (`Range(int,int)` with `min==max`, `Range(float,float)` with `min>max`, `insideUnitCircle`) | The implementation is native; the only managed handle is `Random.State`, whose four ints are **private `[SerializeField]`** fields | The §10.2 experiments |
| The vanilla location table (183 entries **on this modded install**, all fields, final order) | Serialized Unity asset data, not code; and 183 is not established for an unmodded install | §10.1 dumper, plus the plugin list |
| Which entries are `m_prioritized`, `m_unique`, `m_centerFirst`, and their `m_quantity` | Asset data. Decides how short the prefix is for bosses and whether the trader has one candidate or many | §10.1 dumper |
| ~~Whether `AltBiomeList.m_altBiomes` has any enabled entries in 1.0.15~~ **Settled: 28 entries, all `m_enabled = true`, names known** | Bundle scan of `SoftRef\Bundles\d59cfac` + the `Fortress Mountain` log line (see §2.9) | — |
| The **field values** of those 28 `AltBiome`s — above all `m_biome`, `m_requireNeighbor`, `m_notNeighbor`, `m_minEdgeSize`/`m_maxEdgeSize`, `m_minAvgHeight`/`m_maxAvgHeight`, `m_minDistanceFromCenter`, `m_chance`, and which carry `m_addLocations` / `m_blockLocationNames` | Only the record count and names were recovered from the bundle; the values were not | §10.3 dumper |
| How many of the 28 actually place sectors on a given seed (`Fortress Mountain` placed 0 on `testworldclaude`; `CanAddModifier` returns false outright for any entry whose `m_requireNeighbor` is non-zero — §2.9) | Per-seed outcome, and the field values above are unknown | §10.3 dumper, then replay in the port |
| The prefab value of `ZoneSystem.m_locationVersion` (code default 1) | Asset data. Only matters for predicting re-rolls on existing worlds | §10.1 dumper |
| Whether any location has `Max(m_exteriorRadius, m_interiorRadius) > 32` (the out-of-zone registration path, §6.1) | Asset data | §10.1 dumper |
| Whether `Dictionary<Heightmap.Biome, BiomeTypeInfo>` enumeration order is guaranteed to equal insertion order on Unity's runtime | Documented as unspecified; in practice insertion order with no removals | Dump the observed `GenerateAltBiomes` iteration order; only matters if alt-biomes exist |
| Which vanilla dungeon prefabs have a non-zero `DungeonGenerator` local XZ offset (i.e. whose interiors depend on the unseeded rotation) | Asset data | Dump `DungeonGenerator.transform.localPosition` and `m_useCustomInteriorTransform` per location prefab |
| Whether the `sqrMagnitude > 110250000f` / `DUtils.Length > 10500f` mismatch (§2.4) changes any `BiomeSector.HeightMin`, and through it `HeightAvg` and `CanAddModifier` | Two differently-computed float tests on the same circle; the affected ring is thin but non-empty in principle | Compute both tests over the 2048² grid in the port and count disagreements, then compare `HeightMin`/`HeightAvg` per sector against a dumped set |
| Whether `"Fortress Mountain"` (or any shipped alt-biome) carries `m_addLocations`, and whether the `TarPit1_1` / `TarPit2_1` / `TarPit3_1` / `GoblinCamp2_1` / `StoneTowerRuins05_leet` / `StoneTowerRuins10_sunk` entries that placed **0** in this machine's log are those entries | Placing exactly 0 with a full attempt budget is the signature of filter 10a rejecting every point, but the log does not name the cause | §10.1 + §10.3 dumper: compare `AltBiomeParent` per entry against the alt-biome names |
| Whether two `ZoneLocation` entries share one `m_prefab.m_assetID` anywhere in the table (§6.1, §6.3) | `CheckLocationDuplicates` is dead code, so no runtime warning exists; the `SetupLocations` duplicate-**hash** error does not fire in this machine's log | §10.1 dumper: group the table by `m_assetID` and by `m_prefab.Name` |
| Whether `Enum.HasFlag` on `Heightmap.Biome` behaves as `(x & flag) == flag` on Unity's runtime (the §2.9 `HasFlag(None) == true` argument depends on it) | Standard BCL semantics, but Unity's Mono/IL2CPP build is the one that matters | Have the dumper log `((Heightmap.Biome)someMask).HasFlag(Heightmap.Biome.None)` once at startup |

---

## 14. Verification

Independent adversarial check of this document against `scratchpad/decomp/`, the live
`assembly_valheim.dll` / `assembly_utils.dll` / `SoftReferenceableAssets.dll` (via Mono.Cecil and
`.claude\skills\valheim-modding\scripts\decompile.ps1`), and
`E:\SteamLibrary\steamapps\common\Valheim\BepInEx\LogOutput.log`. Every quoted code excerpt in §§1–6 was
re-read against the real file rather than trusted.

**Confirmed correct, re-derived rather than trusted** (no change made):

* `ZNet.ServerLoadWorld`'s four lines and their order (re-decompiled verbatim).
* `AltBiomeWorldData` constants 2048 / 12f / 1024 / 6f; `MapSpaceToWorldSpace` / `WorldSpaceToMapSpace`;
  the `> 110250000f` cutoff to `Ocean` / `-1000f`; `world.m_biomeData` assigned after the loop.
* `VerifyBiomeData` deletes the cache and never reads it; `TryLoadCache` / `SaveCache` are off this path.
* The flood fill: the three global sectors in the order AshLands, DeepNorth, Ocean; the row-major
  (y-outer, x-inner) global pass; the `+x, -x, +y, -y` `tryFill` order with a LIFO stack; and the
  **seed-point-never-appended** quirk — only `tryFill` appends to `AllPoints` /
  `AllPointsAboveSeaLevel`, the outer scan does not. The `>= 30f` threshold is on the raw stored height.
* `MaxZone` computed from `Min`, `new Vector3(a, b)` leaving `z == 0`, `IsDiscovered` unreachable,
  `ZoneCount == 1`, `Min` / `Max` defaulting to `(0,0)`.
* The `Biomes` dictionary population from `Enum.GetValues(typeof(Heightmap.Biome))`, and the enum values
  `None 0, Meadows 1, Swamp 2, Mountain 4, BlackForest 8, Plains 0x10, AshLands 0x20, DeepNorth 0x40,
  Ocean 0x100, Mistlands 0x200, All 895, Land 0x27F` — ascending order does put Land(639) before
  All(895), as §2.6 says.
* `RandomBiomeFromBiomes` verbatim, including `Range(0, num - 1)`, the Plains→BlackForest return and the
  second `Meadows` test returning `Ocean`; and the derived claim that Ocean requires Meadows + Ocean +
  Mistlands all set.
* `GenerateAltBiomes`'s `InitState(seed + 920)`, the per-(biome, altbiome)
  `InitState((int)(key + nameHash + seed))`, `Utils.Shuffle<T>(IList<T>)` as end-to-start Fisher–Yates
  with `Count - 1` draws, and one `Range(0f, 1f)` per sector examined while below `m_maxAmountSpawned`.
* `GenerateLocationsTimeSliced` (both overloads) line by line: the `OrderByDescending(m_prioritized)`
  stable sort, the backwards `m_enable` / `m_quantity == 0` removal, the 200000 / 100000 ETA numbers
  being display-only, the 60000 / 12000 attempt budgets,
  `seed = GetSeed() + m_prefab.Name.GetStableHashCode()`, the save/restore of the ambient
  `Random.state` around the whole routine and around every `yield return null`, the branch selection on
  `m_centerFirst` / `m_minAltitude < 0f`, `maxRange++` *after* the draw, the six point tries, and the
  exact filter order 1→11 with their error counters (filter 5 has none) and their `continue`s.
* `GetRandomZone` (`(int)range / 64`, two draws per do-while iteration, accept on `< 10000f`),
  `GetRandomPointInZone` (exactly two float draws, always), `GetZone` with its `double`→`float`
  narrowing and `Utils.FloorToInt(f) = (int)(f + 64000f) - 64000`, `GetZonePos`, `GetZoneCenter`.
* `WorldGenerator.GetBiomeArea(Vector2s)` and its memo, and `s_biomeAreaOffsetsInt` =
  `(-64,-64) (64,-64) (64,64) (-64,64) (-64,0) (64,0) (0,-64) (0,64)` in that order, subtracted from the
  point. `WorldGenerator..ctor` clears `s_cachedBiomes` / `s_cachedBiomeAreas`.
* `GetTerrainDelta` with `num = 10`, `radius = m_exteriorRadius`, the `(double)max - (double)min`
  subtraction and `Normalize(lo - hi)`.
* The `mask` claim: `GetBiomeHeight` sets `mask = Color.black` and only `GetMistlandsHeight`
  (`new Color(0f, 0f, 0f, num5)`), `GetAshlandsHeight` (`new Color(0f, 0f, 0f, (float)num19)`) and
  `GetDeepNorthHeight` (`new Color(0f, num9, 0f, 0f)` — **green**) overwrite it. So `mask.a` is non-zero
  only in Mistlands and AshLands.
* `RegisterLocation`, `AddToCache`, `HaveLocationInRange` (3-D `sqrMagnitude`, strict `<`, AssetID bucket
  always consulted), `CountNrOfLocation` (by `m_prefab.Name`), `ClearNonPlacedLocations`,
  `RemoveNonPlaced`, `RemoveUnplacedLocations`, `PlaceLocations` (including `value.m_position` never
  being updated, the unseeded `Random.Range(0, 16) * 22.5f` rotation, and
  `seed + zoneID.x * 4271 + zoneID.y * 9187`).
* `ZoneSystem.Update` returns before any zone spawning while `IsServer && !LocationsGenerated`.
* Release-build gating: `m_IterationsRun += iterations` and the error aggregation sit inside
  `if (UnityEngine.Debug.isDebugBuild)`, matching `iterations: 0` and `with 0 tries` in the log.
* §9's constant table (`c_ZoneSize 64f`, `c_ZoneSizeHalf 32f`, `c_WaterLevel 30f`,
  `waterEdgeSqr 110250000f`, and the rest).
* `Vector2s` really is `{ Int16 x; Int16 y }` with an `(Int32, Int32)` ctor, and the ranges §5.3 quotes
  do not overflow a short (|zone| ≤ 192 gives |zone × 64| ≤ 12288 < 32767).
* §12's validation command line: `python scripts/valheim_saves.py locations "<world>"` exists, and the
  script opens files read-only. §12 now names the location gate; `vseed world <world> --locations`
  reads the same save read-only but only counts the instances.

**Corrected in place** (each edit carries its evidence):

1. §2.9 — `AltBiomeList.m_altBiomes` is **not** empty in 1.0.15: vanilla ships **28** `AltBiome`s and
   every one has `m_enabled = true` (already established in
   `.claude\skills\valheim-worldgen\references\zones-locations-vegetation.md` §3.4 by a scan of the
   shipped bundle `SoftRef\Bundles\d59cfac`; the spec's author had not consulted it). Independently
   corroborated by `LogOutput.log` 2026-09-22 20:37:19,
   `Loading: Placed 0/1-2 of 'Fortress Mountain' altbiome. (Valid, sectors: 0, combos: 2)`, from which
   `m_minAmountSpawned`, `m_maxAmountSpawned` and "single-bit `m_biome`" follow. Removed "if the list is
   empty the port can omit this".
1b. §2.9 — added the material omission that **`GenerateAltBiomes` never clears `AltBiome.Sectors`**, so a
   multi-seed sweep reusing one `AltBiome` set starves every run after the first and leaks each previous
   world's 2048² grid through stale `BiomeSector` references.
2. §2.9 — the `m_requireNeighbor` / `m_notNeighbor` description was wrong. The loop index is a
   `BiomeIndex`, the mask test uses `((BiomeIndex)i).ToBiome()` (`BiomeHelpers`), and the neighbour test
   uses `(Heightmap.Biome)i`. The `i == 0` iteration is never skipped (`HasFlag(None)` is always true)
   and demands a `Biome.None` neighbour, which cannot exist — so a non-zero `m_requireNeighbor` makes
   `CanAddModifier` return **false unconditionally**. Wrote out the exact per-index mapping for
   `m_notNeighbor` (the BlackForest bit blocks a *Mountain* neighbour; the Ocean bit blocks a
   *BlackForest* neighbour; the Mountain / Plains / AshLands / DeepNorth / Mistlands bits are inert).
3. §3 — `AltBiome.m_addLocations` are appended for **every** entry of `AltBiomeList.m_altBiomes`, not
   only the enabled ones (`SetupLocations` has no `m_enabled` test; `AltBiomeList.Awake` adds all).
4. §2.5 Step D — added the edge pass's real neighbour order (`-x, +x, -y, +y`, different from the flood
   fill's) and the `||` short-circuit that records **at most one** neighbour per edge point.
5. §2.4 — added `GetBiomeHeight`'s separate `DUtils.Length(wx, wy) > 10500f` → `-400f` early return, and
   strengthened the "no alt-biome changes height" claim (`heightMapChanges` is a `public const bool
   = false`; a Mono.Cecil scan finds the six height fields read nowhere in `assembly_valheim.dll`).
6. §5.1 / §5.4 — `s_tempVeg` is an **instance** field (`ZoneSystem` line 418), not static, and is shared
   with `PlaceVegetation`.
7. §6.1 — `TarPit1` / `TarPit1_1` is not an example of two entries sharing a prefab: the log line prints
   `m_prefab.Name`, so those are two different prefab names, with different `AssetID`s and different
   stream seeds. Added `SoftReference<T>.get_Name`'s actual behaviour and the `AssetID` field order.
8. §6.2 — the caches are **not** uniformly stale. `ZoneSystem.Load` replays the saved `m_placed` flag
   into `RegisterLocation`, so loaded-placed instances are correct in the caches. Only instances promoted
   by `PlaceLocations` during the current session are stale.
9. §6.3 — `ZoneSystem.CheckLocationDuplicates` is dead code (no callers); it emits no runtime warning, so
   its absence from the log proves nothing.
10. §10.2 — `UnityEngine.Random.State`'s `s0..s3` are `private [SerializeField]` ints, not public; the
    dumper needs `JsonUtility`, reflection, or an unsafe reinterpret.
11. §11 — the cited `test1` log has been overwritten and cannot be checked. Re-measured against the log
    now on disk (fresh world `testworldclaude`): genloc is **33.4 s**, not 13.4 s, with **18** slow
    types, not 6.
12. §3 / §10.1 — the 183 figure comes from a **modded** install (16 BepInEx plugins in the same log); it
    is not established for an unmodded 1.0.15.
13. §5.4 — "Unity's `Range(float,float)` does not validate `min > max` (it interpolates)" was stated as
    fact about native code; downgraded to **Unverified**, with the managed signature
    (`extern float Range(float minInclusive, float maxInclusive)`, `InternalCall`) as the only evidence
    that exists in managed code.
14. §1 — added that `GenerateLocationsIfNeeded` has exactly one caller (`ZNet.ServerLoadWorld`), that
    `GenerateLocations()` calls `SetupLocations()` only when `!Application.isPlaying`, and the verified
    `genloc` / `genloc alt` split read from `Terminal.<>c.<InitTerminal>b__7_17`'s IL. (The draft's
    "console `genloc` calls `GenerateLocations()` again" was correct.)
15. §2.2 — `GetBiomeSector(int, int, bool)`'s `clamp` parameter is never read (the body always clamps),
    and the three `BiomeSector.Empty*` statics it can return have empty `AltBiomes` lists.
16. Header — `scratchpad/decomp/Vector2s.cs` is a failed dump containing a PowerShell "type not found"
    error, not source; the type was verified with Mono.Cecil instead.
17. §8(c) — a console `genloc` is not merely "different", it is **non-deterministic**: it leaves
    `LocationsGenerated` true, so `ZoneSystem.Update` keeps spawning zones and `PlaceVegetation` clears
    and refills the same `s_tempVeg` list that filter 11 is accumulating into.

**Still unverified after this pass** — everything in §13, which now carries four further items at the end
of its table. The single largest risk is unchanged: **nothing in this document establishes the behaviour
of native `UnityEngine.Random`.** Until the §10.2 measurements exist and a replay test passes, every draw
count in §5.5 is an assumption about where the state advances, not a measured fact; a wrong answer on
`Range(0, 0)` or on `insideUnitCircle`'s consumption will desynchronise each per-location stream from its
first divergence while still producing plausible-looking output.

*checked by an independent reviewer*
