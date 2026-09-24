# Does a boss always get its full set of altars?

Settling DoomMachine's hypothesis — *"I am unsure if it is even theoretically possible for any given
boss to have fewer than the listed number of altars ... these are fixed, so for example Eikthyr always
has 3 altars all within the min/max range"* — from the decompiled generator first, then from a
uniformly drawn sample of the seed space.

Nothing under `_ModSource\SeedLab` was written by this work. Everything here lives in this scratchpad
folder; section 6 is the patch to apply afterwards.

**The answer in four lines.**

1. A shortfall is **possible**, and not only in principle: over 5,000 uniformly drawn seeds
   `DN_Bossroom` placed fewer than its 3 altars in 26, `Hildir_camp` fewer than its 10 candidates in
   26, and `BogWitch_Camp` in 1. The generator has a code path for it and logs it.
2. For the seven altars of the `group:bosses` set and for Haldor, **no counterexample was found** in
   5,000 seeds - which bounds the shortfall rate at 0.06 %, not at zero, and is not a proof.
3. "All within the min/max range" is **PROVED** from the filters, and 5,000 seeds of instances held
   every bound with 0 violations.
4. So a world-wide count goal on a boss is **not vacuous** (the checker may not claim that) but it is
   **degenerate**: it has become "a seed where the type placed at all". The user's proposal -
   `count_within(R)` - is right, with one trap: `R` must be strictly below the type's ring ceiling, or
   it is the same goal again.

Build under test: `DATA-STAMP game-version=1.0.15 assembly_valheim-sha256=59f53fb5... dumped=2026-09-22`,
`data\1.0.15-59f53fb5\locations.json` (232 entries, 183 with a quantity, in placement order).

---

## 1. The theoretical side

### 1.1 The loop, stated exactly

`ZoneSystem.GenerateLocationsTimeSliced(ZoneLocation, Stopwatch, ZPackage)` —
`scratchpad\decomp\ZoneSystem.cs:1877`. The whole of the per-type run is:

```csharp
int seed = WorldGenerator.instance.GetSeed() + location.m_prefab.Name.GetStableHashCode();  // 1880
UnityEngine.Random.InitState(seed);                                                         // 1882
float maxRadius = Mathf.Max(location.m_exteriorRadius, location.m_interiorRadius);          // 1895
int attempts   = (location.m_prioritized ? 60000 : 12000);                                  // 1896
int placed     = CountNrOfLocation(location);                                               // 1898
if (!location.m_unique || placed <= 0) {                                                    // 1901
    int i = 0;
    while (i < attempts && placed < location.m_quantity) { ... i++; }                        // 1908
}
```

and after the loop, nothing but logging:

```csharp
if (placed < location.m_quantity)                                                           // 2098
    ZLog.LogWarning($"Failed to place all {location.m_prefab.Name}, placed {placed} out of ...");  // 2099
```

**The first thing to say plainly: the game has a code path for a shortfall, and a message for it.**
There is no retry, no relaxation of any filter, no second pass, and no fallback position. The loop has
exactly two exits — the quantity was reached, or the attempt budget ran out.

Two consequences follow immediately and are **PROVED**:

* `count(type) <= m_quantity` in every world. Instances are only appended; the loop stops at the cap.
  (This is the existing rule R4 / `LocationFeasibility` line 121, and it is sound.)
* `count(type) >= 0` is the only lower bound the code gives. Nothing in the generator makes
  `count = m_quantity` an invariant.

### 1.2 What a candidate zone is, and why the budget is not the scarce resource

Line 1919, for a type with `m_centerFirst == false`:

```csharp
Vector2s zoneID = (location.m_minAltitude < 0f)
    ? GetZone(AltBiomeWorldData.MapSpaceToWorldSpace(ZNet.World.m_biomeData.GetRandomPointByBiomes(location.m_biome)))
    : GetZone(AltBiomeWorldData.MapSpaceToWorldSpace(ZNet.World.m_biomeData.GetRandomPointByBiomesAboveSeaLevel(location.m_biome)));
```

All eight boss altars and all three trader camps have `centerFirst = false` and `minAltitude >= 0`
(`locations.json`), so every one of them draws its candidate zone **uniformly from the above-sea-level
points of its own biome, anywhere in the world** — the 2048x2048 grid of 12 m samples that
`AltBiomeWorldData.GenerateSectors` / `tryFill` fills wherever `PointHeights[x,y] >= 30f`
(`AltBiomeWorldData.cs:142` and `:170`).

Three things fall out of that:

1. **The draw is not restricted to the type's own ring.** Eikthyrnir's candidate zone is a random
   Meadows land point out of the *whole* world; the `m_maxDistance = 1000` test at line 1962 then
   throws away every draw outside 1 km. So the effective budget is `60,000 x p`, where `p` is the
   share of that biome's land that lies inside the ring.
2. **An empty biome degrades, then crashes.** `GetRandomPointByBiomeAboveSeaLevel`
   (`AltBiomeWorldData.cs:360-367`) falls back to `GetRandomPointByBiome` when the above-sea list is
   empty — i.e. it starts drawing underwater points, which the altitude filter at line 1977 rejects
   every single time, a guaranteed `0 / m_quantity`. If `AllPoints` is empty too, the game does
   `Random.Range(0, 0)` on an empty list and throws. SeedLab reproduces that as an explicit exception
   (`src\SeedLab.Locations\BiomeField.cs:439-442`), so a seed that would crash the game shows up in a
   sweep as a failed seed rather than a silent zero.
3. **Six point tries, sometimes one.** Line 1941 runs `for (int j = 0; j < 6; j++)` per candidate zone,
   and the point is `GetRandomPointInZone` (`:2166-2172`), `Range(-32 + maxRadius, 32 - maxRadius)` on
   each axis. For `maxRadius == 32` that is `Range(0, 0)`: **the point is exactly the zone centre and
   the six tries are six evaluations of the same point.** That is the case for `GoblinKing`,
   `FaderLocation`, `Mistlands_DvergrBossEntrance1` and `DN_Bossroom` (all `m_exteriorRadius = 32`).
   Confirmed empirically: every instance of those four in this study sits on a multiple of 64 m.
   No boss or trader has `maxRadius > 32`, so none of them can escape its candidate zone and none can
   hit the `RegisterLocation` one-per-zone collision at `:2604-2611` — `Placed == Registered` for all
   eleven, in every seed measured.

### 1.3 Every filter that can reject, in order

| # | line | counter | test |
|---|---|---|---|
| Z1 | 1925 | `errorLocationInZone` | the zone already holds a location |
| Z2 | 1930 | — | `IsZoneGenerated` — dead on a fresh world (spec 02 §1.1) |
| Z3 | 1934 | `errorBiomeArea` | `(m_biomeArea & GetBiomeArea(zoneCenter)) == 0` |
| P1 | 1956/1962 | `errorCenterDistance` | `m_minDistance` / `m_maxDistance` against the point's XZ magnitude |
| P2 | 1969 | `errorBiome` | `(m_biome & GetBiome(p)) == 0` |
| P3 | 1977 | `errorAltitude` | `GetHeight(p) - 30` outside `m_min/maxAltitude` |
| P4 | 1983 | `errorForest` | `m_inForest` band |
| P5 | 1993 | — | `m_min/maxDistanceFromCenter` (unused by every boss and trader) |
| P6 | 2002 | `errorTerrainDelta` | `GetTerrainDelta(p, m_exteriorRadius)` outside `m_min/maxTerrainDelta` |
| P7 | 2008 | `errorSimilar` | `m_minDistanceFromSimilar` — **3D** distance, by asset id and `m_group` |
| P8 | 2014 | `errorNotSimilar` | `m_maxDistanceFromSimilar` (unused here) |
| P9 | 2021/2027 | `errorVegetation` | the height mask's `a` channel against `m_min/maximumVegetation` |
| P10 | 2034/2039 | `errorAltBiomeMissing` / `Block` | alt-biome parent present, alt-biome block list |
| P11 | 2044 | — | `m_surroundCheckVegetation` (unused here) |

### 1.4 Can the geometry alone force a shortfall? No — computed, not assumed

If `m_quantity` instances could not physically coexist inside the type's derived annulus at its
`m_minDistanceFromSimilar` spacing, a shortfall would be **PROVED** for that type. It cannot, for any
of the eleven. Recomputed for this document (`out\packing.txt`), "zones" counts 64 m zones whose square
intersects the annulus and "max on ring" is how many points fit on the single circle at the annulus
mid-radius at the required spacing:

```
prefab                              q  space  radialLo  radialHi      zones  max on ring
Eikthyrnir                          3      0       0.0    1000.0        837    unbounded
GDKing                              4   3000    1000.0    7000.0      37284            8
Bonemass                            5   3000    2000.0    6000.0      25052            8
Dragonqueen                         3   3000     600.0    8000.0      49340            8
GoblinKing                          4   3000    2900.0    8000.0      43312           11
Mistlands_DvergrBossEntrance1       5   2048    5900.0   10000.0      50940           24
FaderLocation                       3   2048    7907.7   10500.0      37796           28
DN_Bossroom                         3   1024    7907.7   10500.0      37796           56
Vendor_BlackForest                 10    512    1500.0   10500.0      83608           73
Hildir_camp                        10   1000    3000.0    5100.0      13536           25
BogWitch_Camp                      10   1000    3000.0    6000.0      21272           28
```

Every "max on ring" exceeds the type's `m_quantity`, on one circle, before any of the annulus's area is
used. So packing is never the binding constraint, and the spacing bound is in any case only a heuristic
(P7 compares a 3D distance — the atlas's own note).

**Therefore: if a shortfall happens at all, it is terrain scarcity, not geometry and not the budget.**

### 1.5 Is a shortfall possible IN PRINCIPLE, per boss?

The honest answer, type by type, is the same answer: **yes in principle, and the code cannot settle it
either way.** What would have to be true of a seed:

| boss | what would starve it |
|---|---|
| `Eikthyrnir` (3, Meadows, r<=1000, alt 1..1000, delta<=3) | almost no Meadows land inside 1 km of the origin. Within ~500 m of the centre the biome is decided by `GetBaseHeight` alone — `IsAshlands`/`IsDeepnorth` are seed-free and false there, Swamp needs `num > 2000` (`GetBiome:812`), Plains `num > 3000 + A` (`:820`), Mistlands `num > 6000 + A` (`:816`), BlackForest `num > 600 + A` (`:824`, `:828`) — so it is Ocean (`baseHeight <= 0.02`), Mountain (`> 0.4`), or Meadows. A centre that is open ocean, or a single steep peak, starves it. |
| `GDKing` (4, BlackForest, 1000..7000, spacing 3000) | four BlackForest patches 3 km apart inside 7 km. The most forgiving of the eight. |
| `Bonemass` (5, Swamp, 2000..6000, alt 0..2, spacing 3000) | Swamp is noise-gated (`GetBiome:812`, `PerlinNoise(offset0) > 0.6`) **and** the altitude window is only 0..2 m above sea level. Five such spots 3 km apart is the tightest ask of the five classic bosses. |
| `Dragonqueen` (3, Mountain, 600..8000, alt **150..500**, delta<=4) | mountains that reach 150 m but stay under 500 m, flat to within 4 over a 12 m radius, three of them 3 km apart. |
| `GoblinKing` (4, Plains, 2900..8000, spacing 3000) | four Plains **zone centres** (§1.2 item 3) 3 km apart. |
| `Mistlands_DvergrBossEntrance1` (5, Mistlands, 5900..10000, alt 1..20, spacing 2048) | Mistlands land between 1 and 20 m above sea level, five of them 2 km apart. |
| `FaderLocation` (3, AshLands, spacing 2048, `m_maximumVegetation 0.1`) | the AshLands region is seed-free, but its *land* is not: it needs 3 low-vegetation zone centres above water. |
| `DN_Bossroom` (3, DeepNorth, alt **80..5000**, spacing 1024) | DeepNorth land above 80 m. |
| `Vendor_BlackForest` (10 candidates, BlackForest, r>=1500, spacing 512) | ten BlackForest spots at `maxTerrainDelta 2` — the flattest requirement of the eleven. |
| `Hildir_camp` (10 candidates, Meadows, 3000..5100, spacing 1000, delta<=2) | Meadows survives only out to `5000 + A` (`GetBiome:828`), so this type lives in a 2.1 km-wide annulus that BlackForest noise dominates. Ten flat Meadows spots 1 km apart in that ring is the hardest ask in the table. |
| `BogWitch_Camp` (10 candidates, Swamp, 3000..6000, spacing 1000, delta<=2) | ten flat Swamp spots 1 km apart. |

**Is it provable either way from the code?** No, and here is exactly how far a proof gets before it
stops:

* The *upper* bound is provable and already proved: `count <= m_quantity`.
* The *lower* bound is not. Proving "Eikthyr always gets 3" means proving a property of a Perlin-noise
  field over all 4,294,967,296 seeds. The seed enters `GetBaseHeight` only through the five integer
  offsets `m_offset0..4`, each `UnityEngine.Random.Range(-10000, 10000)` (`WorldGenerator..ctor:223-229`),
  so the base-height field is one of at most `20,000^2 = 4x10^8` translations of one fixed field — a
  real reduction, and enough in principle to settle the *biome* map inside 500 m exhaustively. It is
  not enough for the whole question: the altitude filter P3 uses `GetHeight`, which adds rivers
  (`m_riverSeed`, a full int32) and the vegetation mask, and the ring runs out to 1 km where the
  BlackForest noise (`m_offset2`) also matters. So an exhaustive proof is not reachable, and
  **the lower bound is MEASURABLE ONLY.**
* Disproving it — showing one seed with fewer than `m_quantity` — needs only one counterexample, and
  SeedLab can check any candidate bit-exactly. **Section 2 supplies counterexamples for three of the
  eleven** — `DN_Bossroom`, `Hildir_camp` and `BogWitch_Camp` — so for those three "possible in
  principle" is now "observed in fact", and the whole question is settled in the negative: a boss
  altar type CAN come up short. For the seven types of the `group:bosses` set and for
  `Vendor_BlackForest` no counterexample was found, and no proof that none exists is available.

---

---

## 2. The measurement

**Sample: 5,000 seeds**, drawn with the tool's own shuffled scan order over the whole int32 range
(`SeedLab.Search.Execution.Permutation`, a 4-round balanced Feistel with `h = 16`, so no cycle
walking), key **`0xA17A25EED10C5117`**, permutation indices `0 .. 4,999`. The seven numbers that reproduce it
exactly, and the first eight seeds as a cross-check, are in `ALTAR-QUANTITY-DATA.md`.

Each seed was generated in full: the 2048x2048 biome point grid, the sectors, the alt biomes, and
all 183 ordered location types - the same code path the location gate proves bit-exact
(12,228/12,228 instances of a freshly dumped world). Per type the study records the game's own
`placed` counter, the instances actually registered, and the outer attempts consumed.

The machine was running the other workflow's builds and search work throughout, so wall-clock
timings are not quotable. The per-seed *work* is fixed and is: 4,194,304 `GetBiome` +
`GetBiomeHeight` evaluations for the grid, then the placement run. Measured throughput was
0.60-1.02 s per seed at 12 worker threads on a 16-thread machine.

### 2.1 Bosses and traders - counts

`short` counts seeds where the game's `placed` ended below `m_quantity` - i.e. where the game
itself would log "Failed to place all ...".

| type | m_quantity | count distribution | seeds short | shortfall rate, 95 % |
|---|---|---|---|---|
| `Eikthyrnir` | 3 | 3 x 5,000 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `GDKing` | 4 | 4 x 5,000 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `Bonemass` | 5 | 5 x 5,000 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `Dragonqueen` | 3 | 3 x 5,000 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `GoblinKing` | 4 | 4 x 5,000 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `Mistlands_DvergrBossEntrance1` | 5 | 5 x 5,000 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `FaderLocation` | 3 | 3 x 5,000 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `DN_Bossroom` | 3 | 1 x 2, 2 x 24, 3 x 4,974 | 26 | 26 / 5,000 = 0.520 %  (95 % CI 0.355 .. 0.761 %) |
| `Vendor_BlackForest` | 10 | 10 x 5,000 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `Hildir_camp` | 10 | 7 x 2, 8 x 6, 9 x 18, 10 x 4,974 | 26 | 26 / 5,000 = 0.520 %  (95 % CI 0.355 .. 0.761 %) |
| `BogWitch_Camp` | 10 | 8 x 1, 10 x 4,999 | 1 | 1 / 5,000 = 0.020 %  (95 % CI 0.004 .. 0.113 %) |

### 2.2 Bosses and traders - how much of the attempt budget was used

This is the number that settles whether the 60,000-attempt budget is the binding constraint. All
eleven types are `m_prioritized`, so all have 60,000.

| type | median | p95 | p99 | max | max as % of 60,000 | exhausted | margin factor |
|---|---|---|---|---|---|---|---|
| `Eikthyrnir` | 22 | 64 | 96 | 313 | 0.52 % | 0 | 192 x |
| `GDKing` | 58 | 137 | 192 | 352 | 0.59 % | 0 | 170 x |
| `Bonemass` | 44 | 111 | 168 | 489 | 0.81 % | 0 | 123 x |
| `Dragonqueen` | 231 | 612 | 888 | 2,117 | 3.53 % | 0 | 28 x |
| `GoblinKing` | 41 | 89 | 124 | 263 | 0.44 % | 0 | 228 x |
| `Mistlands_DvergrBossEntrance1` | 21 | 40 | 50 | 74 | 0.12 % | 0 | 811 x |
| `FaderLocation` | 8 | 21 | 30 | 76 | 0.13 % | 0 | 789 x |
| `DN_Bossroom` | 568 | 2,825 | 13,250 | 60,000 | 100.00 % | 26 | - |
| `Vendor_BlackForest` | 93 | 151 | 182 | 262 | 0.44 % | 0 | 229 x |
| `Hildir_camp` | 1,864 | 9,810 | 33,371 | 60,000 | 100.00 % | 26 | - |
| `BogWitch_Camp` | 1,125 | 2,684 | 4,807 | 60,000 | 100.00 % | 1 | - |

Read that table with section 1.4: the budget is not scarce for the seven `group:bosses` types -
the worst seed of 5,000 used a few per cent of it - and it is the whole story for the three types
that ever fall short, every one of which exhausted all 60,000 attempts in the seeds where it did.

**The margin factor**, and what it does and does not say. Within one seed the outer attempts are
independent draws from that world's own candidate pool, so `p_hat = m_quantity / attempts` is the
maximum-likelihood per-attempt success rate for that seed, and the budget can only bind when
`p` falls to about `m_quantity / 60,000`. The margin factor is `60,000 / (worst attempts seen)` -
how many times **harder** than the hardest sampled world a seed would have to be before the budget
started to bite. `p` is not terrain alone: it is the share of the biome's land that lies in the
ring, times the pass rate of the point filters inside a candidate zone, so a thin margin can come
from either. It is a scale, not a probability - nothing here bounds how thin the tail of `p` is
over the 4,294,967,296 seeds, and the three types with no margin factor are the proof that the
tail reaches all the way down for some types.

### 2.3 Every placed instance against its derived annulus

| type | observed min | observed max | radialLo | radialHi | inside |
|---|---|---|---|---|---|
| `Eikthyrnir` | 2.3 m | 1000.0 m | 0.0 m | 1000.0 m | yes |
| `GDKing` | 1000.2 m | 6999.9 m | 1000.0 m | 7000.0 m | yes |
| `Bonemass` | 2062.4 m | 5934.5 m | 2000.0 m | 6000.0 m | yes |
| `Dragonqueen` | 771.3 m | 7999.9 m | 600.0 m | 8000.0 m | yes |
| `GoblinKing` | 2999.1 m | 7922.8 m | 2900.0 m | 8000.0 m | yes |
| `Mistlands_DvergrBossEntrance1` | 5974.0 m | 9927.4 m | 5900.0 m | 10000.0 m | yes |
| `FaderLocation` | 8260.0 m | 10151.6 m | 7907.7 m | 10500.0 m | yes |
| `DN_Bossroom` | 8201.0 m | 10261.0 m | 7907.7 m | 10500.0 m | yes |
| `Vendor_BlackForest` | 1500.0 m | 10296.3 m | 1500.0 m | 10500.0 m | yes |
| `Hildir_camp` | 3000.0 m | 5095.6 m | 3000.0 m | 5100.0 m | yes |
| `BogWitch_Camp` | 3000.3 m | 5931.5 m | 3000.0 m | 6000.0 m | yes |

0 violations.

### 2.4 All 183 types - shortfall and absence

| type | m_quantity | seeds short | seeds with ZERO | absence rate, 95 % |
|---|---|---|---|---|
| `GoblinCamp2_1` | 5 | 5,000 | 5,000 | 5,000 / 5,000 = 100.000 %  (95 % CI 99.923 .. 100.000 %) |
| `TarPit2_1` | 20 | 5,000 | 4,622 | 4,622 / 5,000 = 92.440 %  (95 % CI 91.674 .. 93.141 %) |
| `TarPit1_1` | 50 | 5,000 | 3,674 | 3,674 / 5,000 = 73.480 %  (95 % CI 72.239 .. 74.685 %) |
| `TarPit3_1` | 100 | 5,000 | 2,568 | 2,568 / 5,000 = 51.360 %  (95 % CI 49.974 .. 52.744 %) |
| `StoneTowerRuins05_leet` | 10 | 3,493 | 2,405 | 2,405 / 5,000 = 48.100 %  (95 % CI 46.717 .. 49.486 %) |
| `GoblinHut03` | 20 | 4,957 | 1,699 | 1,699 / 5,000 = 33.980 %  (95 % CI 32.680 .. 35.305 %) |
| `SwampHut3_1` | 50 | 5,000 | 1,268 | 1,268 / 5,000 = 25.360 %  (95 % CI 24.173 .. 26.585 %) |
| `WoodVillage2` | 15 | 5,000 | 583 | 583 / 5,000 = 11.660 %  (95 % CI 10.800 .. 12.579 %) |
| `StoneTowerRuins10_sunk` | 10 | 4,122 | 514 | 514 / 5,000 = 10.280 %  (95 % CI 9.468 .. 11.153 %) |
| `SwampRuin2` | 30 | 3,732 | 314 | 314 / 5,000 = 6.280 %  (95 % CI 5.641 .. 6.987 %) |
| `SwampHut2_1` | 50 | 5,000 | 278 | 278 / 5,000 = 5.560 %  (95 % CI 4.958 .. 6.230 %) |
| `GoblinHut02` | 30 | 4,602 | 211 | 211 / 5,000 = 4.220 %  (95 % CI 3.697 .. 4.813 %) |
| `StoneTowerRuins09_sunk` | 10 | 3,215 | 196 | 196 / 5,000 = 3.920 %  (95 % CI 3.416 .. 4.494 %) |
| `GoblinHut01` | 30 | 2,474 | 133 | 133 / 5,000 = 2.660 %  (95 % CI 2.249 .. 3.144 %) |
| `StoneTowerRuins08_sunk` | 10 | 1,961 | 34 | 34 / 5,000 = 0.680 %  (95 % CI 0.487 .. 0.949 %) |
| `StoneTowerRuins07_sunk` | 10 | 676 | 6 | 6 / 5,000 = 0.120 %  (95 % CI 0.055 .. 0.262 %) |
| `CombatRuin01` | 5 | 533 | 4 | 4 / 5,000 = 0.080 %  (95 % CI 0.031 .. 0.206 %) |
| `SwampHut1_1` | 50 | 4,930 | 1 | 1 / 5,000 = 0.020 %  (95 % CI 0.004 .. 0.113 %) |
| `TarPit2` | 16 | 4,605 | 1 | 1 / 5,000 = 0.020 %  (95 % CI 0.004 .. 0.113 %) |
| `MorgenHole3` | 40 | 2,207 | 1 | 1 / 5,000 = 0.020 %  (95 % CI 0.004 .. 0.113 %) |
| `NorthVillage` | 135 | 5,000 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `TarPit1` | 100 | 4,947 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `Runestone_Mountains` | 100 | 4,739 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `MountainWell1` | 25 | 4,500 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `Crypt4` | 200 | 4,204 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `Mistlands_Giant1` | 250 | 3,612 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `MountainCave02` | 120 | 2,715 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `GoblinCamp2` | 200 | 2,662 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `FireHole` | 75 | 2,171 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `TarPit3` | 100 | 2,107 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `StoneTowerRuins05` | 50 | 1,597 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `AbandonedLogCabin04` | 50 | 1,465 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `TrollCave02` | 200 | 1,440 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `SwampHut3` | 50 | 1,128 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `SwampRuin1` | 30 | 1,042 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `Grave1` | 200 | 721 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `ShipSetting01` | 100 | 607 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `CharredFortress` | 20 | 349 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `ShipWreck01_DN` | 170 | 325 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `BigRockClearing` | 10 | 277 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `IcePond1` | 40 | 184 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `ShipWreck04` | 25 | 128 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `VoltureNest` | 350 | 125 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `StoneTower3` | 50 | 84 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `CharredTowerRuins3` | 30 | 43 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `MorgenHole2` | 40 | 30 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `Hildir_camp` | 10 | 26 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `DN_Bossroom` | 3 | 26 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `Mistlands_Giant2` | 85 | 23 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `MorkBorg` | 40 | 22 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `ShipWreck02_DN` | 120 | 12 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `StoneTowerRuins10` | 80 | 3 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `AbandonedLogCabin03` | 33 | 3 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `Runestone_Boars` | 50 | 2 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `BogWitch_Camp` | 10 | 1 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `Mistlands_Excavation2` | 40 | 1 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |
| `MorgenHole1` | 40 | 1 | 0 | 0 / 5,000  (<= 0.0600 %, rule of three 3/5,000) |

**163 of 183 types were never absent** in the sample - which bounds their absence rate at
0.0600 % (rule of three), not at zero.

**126 of 183 types were at full `m_quantity` in every sampled seed:**

`AbandonedLogCabin02`, `AncientUpgradeStation`, `AshlandRuins`, `BearCave`, `Bonemass`, `CharredRuins1`, `CharredRuins2`, `CharredRuins3`, `CharredRuins4`, `CharredStone_Spawner`, `CharredTowerRuins1`, `CharredTowerRuins1_dvergr`, `CharredTowerRuins2`, `Crypt2`, `Crypt3`, `DN_gammeltrollFrac01`, `DN_gammeltrollFrac02`, `DN_hut01`, `Dolmen01`, `Dolmen02`, `Dolmen03`, `Dragonqueen`, `DrakeLorestone`, `DrakeNest01`, `Eikthyrnir`, `FaderLocation`, `FortressRuins`, `FrozenShip01_DN`, `FrozenShip02_DN`, `FrozenShip03_DN`, `GDKing`, `GoblinKing`, `Greydwarf_camp1`, `Hildir_cave`, `Hildir_crypt`, `Hildir_plainsfortress`, `InfestedTree01`, `LeviathanLava`, `LumberCamp`, `Mistlands_DvergrBossEntrance1`, `Mistlands_DvergrTownEntrance1`, `Mistlands_DvergrTownEntrance2`, `Mistlands_Excavation1`, `Mistlands_Excavation3`, `Mistlands_GuardTower1_new`, `Mistlands_GuardTower1_ruined_new`, `Mistlands_GuardTower1_ruined_new2`, `Mistlands_GuardTower2_new`, `Mistlands_GuardTower3_new`, `Mistlands_GuardTower3_ruined_new`, `Mistlands_Harbour1`, `Mistlands_Lighthouse1_new`, `Mistlands_RoadPost1`, `Mistlands_RockSpire1`, `Mistlands_Statue1`, `Mistlands_Statue2`, `Mistlands_StatueGroup1`, `Mistlands_Swords1`, `Mistlands_Swords2`, `Mistlands_Swords3`, `Mistlands_Viaduct1`, `Mistlands_Viaduct2`, `MountainGrave01`, `NorthMemorialPlace`, `PlaceofMystery1`, `PlaceofMystery2`, `PlaceofMystery3`, `Ruin1`, `Ruin2`, `Ruin3`, `Runestone_Ashlands`, `Runestone_BlackForest`, `Runestone_DeepNorth`, `Runestone_Draugr`, `Runestone_Greydwarfs`, `Runestone_Meadows`, `Runestone_Mistlands`, `Runestone_Plains`, `Runestone_Swamps`, `ShipSetting02`, `ShipSetting03`, `ShipWreck01`, `ShipWreck02`, `ShipWreck03`, `StartTemple`, `StoneCircle`, `StoneHenge1`, `StoneHenge2`, `StoneHenge3`, `StoneHenge4`, `StoneHenge5`, `StoneHenge6`, `StoneHouse3`, `StoneHouse4`, `StoneTower1`, `StoneTowerRuins03`, `StoneTowerRuins04`, `StoneTowerRuins07`, `StoneTowerRuins08`, `StoneTowerRuins09`, `SulfurArch`, `SunkenCrypt4`, `SwampHut1`, `SwampHut2`, `SwampHut4`, `SwampHut5`, `SwampWell1`, `TheHole01`, `Vendor_BlackForest`, `Waymarker01`, `Waymarker02`, `WoodFarm1`, `WoodHouse1`, `WoodHouse10`, `WoodHouse11`, `WoodHouse12`, `WoodHouse13`, `WoodHouse2`, `WoodHouse3`, `WoodHouse4`, `WoodHouse5`, `WoodHouse6`, `WoodHouse7`, `WoodHouse8`, `WoodHouse9`, `WoodVillage1`

Types where the game's `placed` counter ever exceeded the instances actually registered
(`RegisterLocation` zone collisions, only possible when `maxRadius > 32`): **none**.

**Which of the two these tables report.** The `short` and `zero` columns above use the game's own
`placed` counter - the one the loop condition and the "Failed to place all ..." warning use. The
checker's `count` metric instead counts oracle hits, i.e. instances actually *registered*
(`CompiledQuery.cs:194-203` over `PlacementResult.Instances`). The two can differ only when
`RegisterLocation` (`:2604-2611`) refuses a second location in a zone while the caller still
increments `placed`, which needs `maxRadius > 32`. None of the eleven boss and trader types can
reach that (all have `maxRadius <= 32`), and no type of the 183 showed a collision in this sample at all, so every number in 2.1 and 2.4 is simultaneously a `placed` count and a registered count.

### 2.5 What radius actually discriminates - the replacement for a world-wide count

A sub-study over the first 1,000 indices of the same permutation: how many instances of each type lie
inside each radius. This is the data a D3/D4 message needs in order to offer a radius that means
something. `radialHi` is the type's derived ceiling; at or above it, `count_within` is `count`.

| type | radialHi | largest sampled radius BELOW radialHi where the count still varies | the count there (p5 / median / p95 / max) |
|---|---|---|---|
| `Eikthyrnir` | 1000 m | 500 m | at 500 m: 0 / 2 / 3 / 3 |
| `GDKing` | 7000 m | 6,000 m | at 6,000 m: 2 / 3 / 4 / 4 |
| `Bonemass` | 6000 m | 5,000 m | at 5,000 m: 1 / 3 / 5 / 5 |
| `Dragonqueen` | 8000 m | 6,000 m | at 6,000 m: 0 / 2 / 3 / 3 |
| `GoblinKing` | 8000 m | 6,000 m | at 6,000 m: 1 / 3 / 4 / 4 |
| `Mistlands_DvergrBossEntrance1` | 10000 m | 8,000 m | at 8,000 m: 1 / 3 / 5 / 5 |
| `FaderLocation` | 10500 m | **none of the sampled radii** | - none of 300..10,500 m varied; see the note below |
| `DN_Bossroom` | 10500 m | **none of the sampled radii** | - none of 300..10,500 m varied; see the note below |
| `Vendor_BlackForest` | 10500 m | 8,000 m | at 8,000 m: 4 / 7 / 9 / 10 |
| `Hildir_camp` | 5100 m | 5,000 m | at 5,000 m: 9 / 10 / 10 / 10 |
| `BogWitch_Camp` | 6000 m | 5,000 m | at 5,000 m: 3 / 6 / 8 / 10 |

Three readings worth stating out loud:

* **`Eikthyrnir count_within radius 1000` is exactly `count`.** `m_maxDistance` is 1,000, so every
  altar that exists is already inside it: all 1,000 sampled seeds measured 3. The radii that
  discriminate are 300 m (median 1, max 3) and 500 m (median 2, max 3). This is why a D3 message
  must check its own suggestion (section 4.1).
* The same trap sits at each type's `radialHi`: `GoblinKing` at 8,000 m, `Bonemass` at 6,000 m,
  `Vendor_BlackForest` / `Hildir_camp` / `BogWitch_Camp` at their ceilings, all measure the full
  `m_quantity` in every sampled seed.
* `FaderLocation` and `DN_Bossroom` have no sampled radius that discriminates, because both live
  in the 7,907.7..10,500 m AshLands/DeepNorth band and the study's radius grid jumps straight
  from 8,000 m (where both measure 0 in every seed) to 10,500 m (where both measure their full
  set, bar the DN_Bossroom shortfalls). Their useful radii lie between about 8,200 m and
  10,150 m and were not measured - an open end, not a claim that none exists.

---

## 3. Which types can genuinely be ABSENT from a world

"This world has none" is the single most useful thing a search can tell a player, and the easiest thing
to get wrong. There are exactly three evidence classes, and the tool must never use one where another
belongs. Over the 5,000-seed sample, **20 of the 183 types** were seen with zero instances at least
once.

### 3.1 PROVED absent from every seed - one type

`GoblinCamp2_1` has **`m_biomeArea = 0`** in the dumped table. Line 1934 is
`if ((location.m_biomeArea & biomeArea) == 0) { reject }`, and `GetBiomeArea` returns only
`Edge = 1` or `Median = 2` (`WorldGenerator.cs:705-723`, `:736-744`), so `0 & anything == 0` rejects
every candidate zone in every seed. It placed 0 in all 5,000 sampled seeds, and that is not a
measurement - it is a proof, and it is the existing rule R8.

While reading that field: the dumped table's `m_biomeArea` values are `-1, 0, 1, 2, 3, 6, 7`, but
`GetBiomeArea` only ever produces 1 or 2. So `2` and `6` both mean **Median only** (`GDKing`,
`Bonemass`, `Grave1`, `SwampRuin1`, `SwampRuin2` carry 6), `1` means **Edge only**
(`Mistlands_Harbour1`, the only one), `3`, `7` and `-1` all mean **Everything** (75, 24 and 26 types -
Unity serialises an "Everything" flags dropdown as -1), and `0` means **never**. The placement engine
is correct - `LocationPlacementEngine.cs:370` does the bitwise AND - but anything that *compares* the
field instead of masking it is wrong for 55 of the 183 types. One such comparison is live:
`LocationProbe.cs:62` decides "does this filter apply" with `e.BiomeArea != BiomeArea.Everything`,
which is true for 6, 7 and -1, so `vseed explain` reports the biome-area filter as active on 50 types
where it constrains nothing and does not label the five value-6 types as Median-only. Cosmetic, not a
placement bug, but it should be a mask test.

### 3.2 MEASURED absent - all 16 alt-biome children, plus four ordinary types

**Every one of the 16 types that carries an `altBiomeParent` was absent in at least one sampled seed.**
That is structural, not a fluke: such a type can only place inside an alt-biome region, and whether
that region exists at all is decided per seed by `AltBiomeWorldData.GenerateAltBiomes`. Rates run from
92.4 % (`TarPit2_1`) down to 0.02 % (`SwampHut1_1`); the full table is section 2.4. Note the asymmetry
in what that establishes: **absence being possible** is an existence claim and one seed settles it,
but the **rate** for the thin end of the list rests on a single observation (`SwampHut1_1` 1/5,000,
and likewise `TarPit2` and `MorgenHole3` below). Treat the possibility as settled and the rate as
uncertain until a larger sample lands.

Four types with **no** `altBiomeParent` also went absent, and these are the ones a checker is most
likely to get wrong, because nothing in their fields marks them as fragile:

| type | fields that make it fragile | absent |
|---|---|---|
| `SwampRuin2` | Swamp, `m_biomeArea 6` (Median only), `m_minAltitude -0.5`, spacing 256, 12,000 attempts | 314 / 5,000 = 6.28 % |
| `CombatRuin01` | Meadows, `m_inForest` with `forestTresholdMin 0.5`, `maxTerrainDelta 1.5`, `m_minDistance 1500`, spacing 2,000, `m_quantity 5` | 4 / 5,000 = 0.08 % |
| `TarPit2` | Plains, Median only, altitude window 5..60 m, `maxTerrainDelta 1.5`, group `tarpit` | 1 / 5,000 = 0.02 % |
| `MorgenHole3` | AshLands, Median only, `m_maximumVegetation 0.3`, spacing 200, group `MorgenHole` | 1 / 5,000 = 0.02 % |

`TarPit2` matters: it is a member of the shipped `tar_pits` group, so a `location:TarPit2` goal can be
empty even though the group as a whole never was.

### 3.3 NEVER SEEN absent - which is not the same as never absent

The other 163 types, including every boss altar, every trader, and almost everything the shipped
presets and the reference sites name:

| what the user asks for | prefabs | ever absent in 5,000 seeds |
|---|---|---|
| burial chambers | `Crypt2`, `Crypt3`, `Crypt4` | no |
| sunken crypts | `SunkenCrypt4` | no |
| troll caves | `TrollCave02` | no |
| frost caves | `MountainCave02` | no |
| infested mines | `Mistlands_DvergrTownEntrance1/2` | no |
| Fuling villages | `GoblinCamp2` | no |
| Fuling villages (alt) | `GoblinCamp2_1` | **every seed - PROVED absent** |
| tar pits | `TarPit1`, `TarPit3` | no |
| tar pits | `TarPit2` | **yes - 1 / 5,000** |
| tar pits (alt) | `TarPit1_1`, `TarPit2_1`, `TarPit3_1` | **yes - 73.5 %, 92.4 %, 51.4 %** |
| surtling geysers | `FireHole` | no |
| charred fortresses | `CharredFortress` | no |
| Hildir's quests | `Hildir_cave`, `Hildir_crypt`, `Hildir_plainsfortress` | no (and all three are at full `m_quantity` 3 in every seed) |
| places of mystery | `PlaceofMystery1/2/3` | no (all `m_unique`, `m_quantity` 1, so the single candidate is the position) |
| the spawn temple | `StartTemple` | no |
| all 8 boss altars, all 3 traders | - | no |

For every one of those, the strongest honest statement is the rule of three: **absent in at most
0.06 % of seeds at 95 % confidence** (`3/5,000`). That is a bound, not a zero.

Two group-level readings follow:

* `tar_pits_with_alt` is the group to watch. Its `_1` members are absent from between half and nine
  tenths of worlds, so a goal written against it behaves nothing like the same goal against
  `tar_pits`. The group's help line already warns about its cost; it should warn about this too.
* A `group:` goal is far more robust than a `location:` goal: the group is empty only when every
  member is, and for `burial_chambers`, `dungeons` and `bosses` that never happened.

---

## 4. The consequence for the tool

### 4.0 The soundness argument, before any rule

The checker's own principle (`checker-design.md` section 0) is that `F` is an **over-approximation** of
the achievable set, REFUSE needs `F n accept = {}` and VACUOUS needs `F subset-of accept`, both from
HARD evidence only. For a count metric the design's own table (section 1.2) sets `Lo = 0`. That is the
right bound, and section 1 above is why: the generator gives no lower bound but 0.

Two things follow, and they are the whole answer to "is a world-wide boss count goal vacuous":

* **`count at_least N` (1 <= N <= Q) can never be VACUOUS**, because `0` is in `F` and 0 fails the test.
  Declaring it vacuous would need a proof that the type always places, section 1.5 says no such proof
  exists, and section 2 supplies counterexamples for three of the eleven types.
* **It can never be REFUSED either**, unless `N > Q` - which is exactly the existing R4.

So the answer to the user's question, *in the checker's vocabulary*, is neither VACUOUS nor REFUSABLE.
It is **WARN-DEGENERATE** - the tier the design already invented for "Eikthyr within 1.2 km"
(section 2.3), whose definition is *"the goal excludes nothing except worlds in which the target failed
to place at all"*. That is exactly what a world-wide boss count goal does, and the fix the tier
prescribes - say what the goal has actually become, and how often that bites, from the sample - is
exactly the user's own proposal: use `count_within(R)` instead.

Throughout, `Q = sum over the goal's placeable types of m_quantity`.

### 4.1 The rules to add

Write `worldWide(g)` for "this goal's disc contains the whole of every type in its target". It has two
halves and they do **not** have the same precondition:

```
worldWide(g)  =  g.Metric == "count"                                  // any From: a world count is a
                                                                      // world count wherever you stand
              || (g.Metric == "count_within"
                  && g.From == DistanceOrigin.Center                  // REQUIRED - see below
                  && g.Radius + Slack >= max over t in T of radialHi(t))
```

The `From == Center` guard is not optional. `radialHi` is measured **from the world origin**, so a disc
of radius `R` drawn around the *spawn point* does not contain the type's ring at `R = radialHi`; it
needs `R >= radialHi + hi(StartTemple)`, and the sound value of `hi(StartTemple)` is the Meadows
ceiling, 5,100 m (`checker-design.md` section 1.6; `LocationFeasibility.Check` already carries the same
`fromCentre` guard for its ring tests). Without the guard, D3 would call
`location:Eikthyrnir count_within radius 1000 from: spawn at_least 3` degenerate when it genuinely
discriminates. A checker that wants the spawn case may use `radialHi + 5,100` instead of `radialHi`;
that bound is sound but very weak, so "do not fire for `from: spawn`" is the safer default. The bare
`count` half needs no guard at all - a world count is a world count wherever the user stands.

The centre-relative half also matters on its own account and is easy to miss:
`location:Eikthyrnir count_within radius 1000` is exactly `count`, because `m_maxDistance` is 1,000 -
so the obvious "fix" for D3 can itself be degenerate. Section 2.5 measures this for all eleven.

| # | tier | trigger | evidence class |
|---|---|---|---|
| **D3** | WARN-DEGENERATE | `worldWide(g)`, `at_least`/`between` with `value == Q` | HARD for the cap, SAMPLE for the rate |
| **D4** | WARN-DEGENERATE | `worldWide(g)`, `at_least N` with `0 < N < Q`, and the calibration sample never measured a value below `N` for this target | HARD for the cap, SAMPLE for the trigger and the rate |
| **D5** | WARN-DEGENERATE | `types_within`, `at_least N == the number of types in T`, `From == Center`, and `g.Radius + Slack >= max over t of radialHi(t)` - the goal has become "every type in the group placed at all" | HARD for the rings, SAMPLE for the rate |
| **V5b** | WARN-VACUOUS | metric `count` or `count_within`, `at_most N` with `N >= Q` | HARD only - a genuine vacuity, and `From` does not matter |
| **A1** | informational line, never a verdict | any goal that needs presence, for each type in its target | SAMPLE only, except the R8 types |

**D3, D4 and D5 are WARN, never REFUSE, never VACUOUS.** Their rate comes from the sample and a sample
may only warn (`checker-design.md` section 2.1). A type with no observed shortfall reports a
rule-of-three bound; a type that has been seen short reports the measured rate with a Wilson interval.

**The suggested replacement must be checked against the same predicate.** A D3/D4 stanza offers
`count_within radius R`; the checker must pick `R` strictly below `min over t of radialHi(t)` and
verify from the calibration sample that the value at that `R` actually varies between seeds. If no
such `R` exists in the sample's radius grid, say so and offer `nearest_distance` or
`all_candidates_distance` instead. Section 2.5 is that table for the eleven.

**Ordering: D3 and V5b can ship immediately; D4 and D5 cannot.** D3's cap and V5b's cap come from
`m_quantity` in the dumped table, which is already shipped, so both work the day they are added - D3
still needs a rate for its message, so print the stanza without the "Measured:" line until the sample
carries counts, rather than printing a made-up number. D4's trigger and D5's rate are *defined* by the
calibration sample, and today's sample carries distance percentiles only (`checker-design.md`
section 5.2). Wiring D4 before section 6 step 5 lands gives a rule that silently never fires.

### 4.2 The exact user-facing text

Voice per `checker-design.md` section 4: one line per fact, numbers with units and thousands
separators, the evidence named as the user would name it, the fix stated as an edit, no apology, no
exclamation marks.

**Every number in these stanzas is a substitution, not a literal.** `{n}` is the calibration sample
size, `{k}` the number of sampled seeds that failed the goal, `{pct}` / `{lo}` / `{hi}` the Wilson
point estimate and its interval, `{ruleOfThree}` is `300/{n}` per cent, and `{p5}`/`{median}`/`{p95}`
come from the suggested radius's own column. Section 4.6 shows the same stanzas filled from this
study's sample, which is what the shipped sample should reproduce.

**D3 - a world-wide count at the cap.** (`vseed search`, exit 1 unless `--allow-vacuous`.)

```
vseed search: 1 must-have goal does not exclude any seed it was meant to.

  goal 'eikthyr-altars'  location:Eikthyrnir count at_least 3
    Eikthyrnir has m_quantity 3, and ZoneSystem's loop is
    'while (i < attempts && placed < m_quantity)' - so no world holds more than 3, and
    this goal asks for every altar the generator is allowed to make. It is not a count
    filter any more; it is 'a seed in which Eikthyrnir placed its full set'.
    Measured: {n-k} of {n} sampled seeds placed all 3, so this goal excludes at most
    {ruleOfThree} % of seeds (95 % rule of three, 3/{n}). That is a measurement, not a
    proof - the generator has a code path for a shortfall and logs it
    (ZoneSystem.GenerateLocationsTimeSliced:2099), and three other location types were
    measured taking it.
    count_within will not help here either: m_maxDistance is 1,000, so any radius of
    1,000 m or more asks the same question again. To filter on where the altars are, use
    count_within radius 500 (measured {p5} / {median} / {p95}) or nearest_distance
    near 300 m.
```

**D4 - a world-wide count below the cap.** Same stanza, middle sentence replaced by:

```
    Eikthyrnir has m_quantity 3, so this goal asks for 2 of the at most 3 altars a world
    can hold. Measured: no sampled seed of {n} placed fewer than 3, so this goal excludes
    at most {ruleOfThree} % of seeds (95 % rule of three).
```

**D3/D4 for a type the sample HAS seen fall short** - the number is measured, not a bound:

```
  goal 'hildir'  location:Hildir_camp count at_least 10
    Hildir_camp is m_unique with m_quantity 10: a world is written with up to TEN
    candidates and the game keeps whichever one a player generates first
    (ZoneSystem.RemoveUnplacedLocations:2243), so this goal counts candidates, not camps,
    and asking for 10 asks for a full candidate set.
    Measured: {k} of {n} sampled seeds fell short, so this goal excludes about {pct} %
    of seeds (95 % CI {lo} .. {hi} %).
    A count of candidates is not a count of anything a player can visit. To put Hildir
    near home, use location:Hildir_camp all_candidates_distance near 4,500 m, which holds
    for whichever candidate the game finally keeps.
```

**V5b - a count ceiling at or above the cap.** (Genuine vacuity, HARD evidence only, no sample needed.)

```
  goal 'not-too-many'  group:bosses count at_most 27
    The seven boss types have m_quantity 3, 4, 4, 5, 3, 5 and 3 - 27 in total - and the
    placement loop stops at m_quantity for each, so no world can exceed 27. This goal
    measures at most 27 in every seed and rejects none of them.
    To filter on crowding, use count_within radius 5000 at_most N.
```

**A1 - the absence line.** Printed under any goal that needs presence, in the pre-flight block. Three
shapes, and the tool must never use one where another belongs:

```
  absence  GoblinCamp2_1 is absent from EVERY seed: m_biomeArea is 0, so
           GenerateLocationsTimeSliced:1934 rejects every candidate zone. PROOF. (R8)
  absence  TarPit1_1 was absent in {k} of {n} sampled seeds ({pct} %,
           95 % CI {lo} .. {hi} %). MEASURED.
  absence  Crypt4 was never absent in {n} sampled seeds, so it is absent in at most
           {ruleOfThree} % of seeds (95 % rule of three). MEASURED - "never in the
           sample" is not "never".
```

### 4.3 One correction to the existing checker design

`checker-design.md` R4 and V5 both say the count cap is **`sum of maxCoexisting`**, and V5's worked
example is *"at most 3 traders in the world - all three are m_unique, so the sum of maxCoexisting is
3"*. That is wrong for the metric as implemented, and it would produce both a false refusal and a false
vacuity claim:

* `MetricCatalog.cs:207` (`uniqCount`) states the semantics: *"candidates of an m_unique type are
  counted individually: a trader with m_quantity 10 contributes up to 10, of which the game keeps
  exactly one"*, and `CompiledQuery.cs:194-203` implements exactly that - it counts every hit in the
  prefab set with no unique collapsing.
* `LocationFeasibility.cs:121` already uses `quantity += t.Quantity`, i.e. the sum of `m_quantity`.
  **The shipped code is right and the document is wrong.**
* Concretely: `group:traders count` ranges 0..30, not 0..3. With the document's cap, `at_least 5` would
  be REFUSED (it is satisfied by nearly every seed - all three traders reach 10 candidates in almost
  all of them) and `at_most 3` would be called VACUOUS when it is in fact almost always FALSE.

Fix: every occurrence of `sum of maxCoexisting` in R4, V5 and the section 1.2 `F` table becomes `Q`
(`sum of m_quantity`), and V5's example becomes the `group:bosses count at_most 27` stanza above.
`maxCoexisting` stays the right number for "how many of this type stand in the world at once after
exploration" - which no metric currently asks.

### 4.4 What changes in the shipped presets and the GUI

* **No shipped preset needs a change.** Nothing in `src\SeedLab.Search\Presets\` uses a world-wide
  `count`: `boss-rush`'s `altars-near` is already `count_within radius 5000`, and `dungeon-delver`,
  `balanced` and `iron-rich` all use `count_within`. The preset comments already explain why.
* **The GUI is where this bites, and it bites today.** `src\SeedLab.Web\wwwroot\app.js` (the metric
  `select` built at 1515 and filled at 1534-1541) populates the dropdown from `Search.byKind[kind]`
  with no filtering, so `location:Eikthyrnir  count  at_least 3` is three clicks away in the web UI
  and runs a full scan that rejects nothing. Bind the count metric for a `location:` / `group:` target
  to `count_within` by default, with the radius slider bounded below by the target's `radialLo` and
  above by its `radialHi` (the atlas has both), and reach the bare `count` only through an explicit
  "whole world" switch that carries the D3 text inline.
* **Reference-site parity.** The sites plot "Distance from center (km)" per instance, not a world
  count - so `count_within(R)` is also the metric that matches what the user is comparing against.

### 4.5 Test additions (in the numbering of `checker-design.md` section 7)

* **T10 - D3/D4 never fire as REFUSE or VACUOUS.** Property test: for every one of the 183 types and
  every `N` in `1..m_quantity`, `count at_least N` must come back WARN-DEGENERATE or OK, never REFUSE,
  never VACUOUS. The single allowed exception is an R8 type (`GoblinCamp2_1`).
* **T11 - the cap is `m_quantity`, not `maxCoexisting`.** `group:traders count at_least 11` must NOT be
  refused; `group:traders count at_least 31` must be. This is the regression test for section 4.3.
* **T12 - V5b.** `location:Eikthyrnir count at_most 3` is VACUOUS; `at_most 2` is not.
* **T13 - the absence line picks the right shape.** `GoblinCamp2_1` gets the PROOF line;
  `TarPit1_1` gets a measured rate; `Crypt4` gets a rule-of-three bound. No type may get the PROOF
  line on sample evidence.
* **T14 - `from: spawn` is not degenerate.** `location:Eikthyrnir count_within radius 1000 from: spawn
  at_least 3` must come back OK, and the same goal with `from: center` must come back
  WARN-DEGENERATE. This is the regression test for the guard in section 4.1.

### 4.6 The same stanzas, filled from this study's sample

Substitutions taken from section 2, `n = 5,000`. A shipped calibration sample of a different size
gives different numbers; these are what the tool should print today.

```
  goal 'eikthyr-altars'  location:Eikthyrnir count at_least 3
    Eikthyrnir has m_quantity 3, and ZoneSystem's loop is
    'while (i < attempts && placed < m_quantity)' - so no world holds more than 3, and
    this goal asks for every altar the generator is allowed to make. It is not a count
    filter any more; it is 'a seed in which Eikthyrnir placed its full set'.
    Measured: 5,000 of 5,000 sampled seeds placed all 3, so this goal excludes at most
    0.0600 % of seeds (95 % rule of three, 3/5,000). That is a measurement, not a
    proof - the generator has a code path for a shortfall and logs it
    (ZoneSystem.GenerateLocationsTimeSliced:2099), and three other location types were
    measured taking it.
    count_within will not help here either: m_maxDistance is 1,000, so any radius of
    1,000 m or more asks the same question again. To filter on where the altars are, use
    count_within radius 500 (measured 0 / 2 / 3 at the 5th / 50th / 95th percentile)
    or nearest_distance near 300 m.

  goal 'hildir'  location:Hildir_camp count at_least 10
    Hildir_camp is m_unique with m_quantity 10: a world is written with up to TEN
    candidates and the game keeps whichever one a player generates first
    (ZoneSystem.RemoveUnplacedLocations:2243), so this goal counts candidates, not camps,
    and asking for 10 asks for a full candidate set.
    Measured: 26 of 5,000 sampled seeds fell short, so this goal excludes about 0.520 %
    of seeds (95 % CI 0.355 .. 0.761 %).
    A count of candidates is not a count of anything a player can visit. To put Hildir
    near home, use location:Hildir_camp all_candidates_distance near 4,500 m, which holds
    for whichever candidate the game finally keeps.

  absence  GoblinCamp2_1 is absent from EVERY seed: m_biomeArea is 0, so
           GenerateLocationsTimeSliced:1934 rejects every candidate zone. PROOF. (R8)
  absence  TarPit1_1 was absent in 3,674 of 5,000 sampled seeds (73.5 %,
           95 % CI 72.2 .. 74.7 %). MEASURED.
  absence  Crypt4 was never absent in 5,000 sampled seeds, so it is absent in at most
           0.0600 % of seeds (95 % rule of three). MEASURED - "never in the
           sample" is not "never".
```

---

## 5. "All within the min/max range" - the second half of the hypothesis

This half of the user's sentence is **PROVED**, not merely measured, and the measurement is a check on
SeedLab rather than on the game.

The radial test is applied to the accepted point itself, not to the candidate zone:

```csharp
Vector3 randomPointInZone = GetRandomPointInZone(zoneID, maxRadius);          // 1954
float magnitude = randomPointInZone.magnitude;                                 // 1955, y == 0
if (location.m_minDistance != 0f && magnitude < location.m_minDistance) continue;   // 1956
if (location.m_maxDistance != 0f && magnitude > location.m_maxDistance) continue;   // 1962
...
Heightmap.Biome biome = WorldGenerator.instance.GetBiome(randomPointInZone);   // 1968
if ((location.m_biome & biome) == 0) continue;                                 // 1969
...
RegisterLocation(location, randomPointInZone, generated: false);               // 2073
```

`GetRandomPointInZone` builds the point with `y = 0` (`:2166-2172`), so `magnitude` **is** the XZ
distance from the origin, and the point that is registered is the same point that was tested. The
biome test then adds the all-seed band of the accepted biome from `GetBiome`'s branch conditions. The
intersection of the two is the "derived annulus" the atlas stores as `radialLo` / `radialHi`.

A type with `m_minDistance == m_maxDistance == 0` (GoblinKing, FaderLocation, DN_Bossroom, the Queen's
entrance) has **no radial field at all**: its ring comes entirely from the biome band, which is why
GoblinKing's floor is 2,900 m (`GetBiome:820`, `num > 3000 + A`, `A` in `[-100, +100]`) rather than
3,000 m, and why FaderLocation's is the AshLands constant 7,907.68 m.

Measured over every instance of every sampled seed: **no violation**. Section 2.3 has the table of
observed extremes against the bounds. The interesting entries are the ones that sit hard against a
bound - `Vendor_BlackForest` at exactly 1,500.0 m (its `m_minDistance`), `Hildir_camp` at 3,000.1 m,
`Eikthyrnir` at 999.8 m - which is what a correct floor/ceiling looks like from inside.

One nuance worth keeping in the tool's language: the *derived* annulus is tighter than the raw field
for several types, and the tighter number is the one a user should see. `Bonemass` carries
`m_maxDistance 10000` but Swamp itself stops at 6,000 m (`GetBiome:812`, `num < maxMarshDistance`), so
its real ceiling is 6,000 m. `Hildir_camp` carries `m_maxDistance 8000` but Meadows stops at
`5000 + A` (`GetBiome:828`), so its real ceiling is 5,100 m. Quoting `m_maxDistance` at a user would
be quoting a bound that nothing ever approaches.

---

## 6. How to apply this afterwards

Nothing here was applied; the repository was left untouched. In order:

1. **`docs\studies\checker-design.md`, section 1.2 table, R4 and V5** - replace
   `sum of maxCoexisting` with `Q = sum of m_quantity` and swap V5's trader example for the
   `group:bosses count at_most 27` stanza (section 4.3 above). This is a correction, not an addition:
   the shipped `LocationFeasibility.cs:121` already uses `m_quantity`, so the document is what is out
   of step.
2. **`checker-design.md`, section 2.3** - add D3, D4, D5 and the `count_within(R >= radialHi)`
   extension to the WARN-DEGENERATE catalogue, with the section 4.2 text and the `From == Center`
   guard from section 4.1.
3. **`checker-design.md`, section 2.2** - add V5b.
4. **`checker-design.md`, section 4** - add the A1 absence line and its three shapes.
5. **`checker-design.md`, section 5.2** - the shipped calibration sample must carry, per type, the
   count histogram and the absence count, not only distance percentiles. That is the data D3, D4 and A1
   read. The files written by this study (`out\run1\counts-*.bin` + `out\run1\types.csv`) are already
   in that shape; the format is documented in `ALTAR-QUANTITY-DATA.md` beside them.
6. **`checker-design.md`, section 7** - add T10..T14 (section 4.5 above).

7. **`src\SeedLab.Search\Locations\LocationGroups.cs`** - the `tar_pits` group's help line says what
   the group contains and `tar_pits_with_alt`'s says what it costs. Add to both what section 3 found:
   `TarPit2` was absent in 1 of 5,000 seeds, and the three `_1` members of `tar_pits_with_alt` are
   absent from 51 %, 73 % and 92 % of worlds. This is the one finding in this pass that changes what
   a shipped group tells a player, and it has to reach the help text, not only this document.
8. **The GUI count control** (section 4.4) - default a `location:` / `group:` count goal to
   `count_within`, bound the radius slider to the target's derived annulus, and put the bare `count`
   behind an explicit "whole world" switch.
9. **The knowledge base.** The facts in section 1 that are new go to
   `.claude\skills\valheim-worldgen\references\` (the placement-budget and candidate-draw facts, the
   `maxRadius == 32` zone-centre consequence, the empty-biome fallback and crash) with their
   `Type.Member:line` evidence and a line in the knowledge base's changelog (not published here); the
   measured rates go to the seedlab skill's `references/`, marked as a sample with its size and key.

**Order matters between 2 and 5.** D3 and V5b decide on `m_quantity`, which is already shipped, so
they can be implemented and tested the day step 2 lands - D3 simply omits its "Measured:" line until
the sample exists. D4's *trigger* and D5's rate are defined by the calibration sample, so implementing
them before step 5 produces rules that never fire and a test suite that passes for the wrong reason.
Do step 5 first, or land D4/D5 behind it.

### What must NOT be done

* **Do not turn "no shortfall in the sample" into a VACUOUS verdict or a refusal.** It is a measurement
  of at most `n` seeds out of 4,294,967,296. The rule-of-three bound is the strongest honest statement.
* **Do not declare a type "never absent".** `A1`'s third shape exists for exactly this reason.
* **Do not quote `m_maxDistance` when the biome band is tighter** (section 5).
* **Do not collapse an `m_unique` type's candidates to 1 for a count metric** - that is the
  section 4.3 bug in reverse.

---
