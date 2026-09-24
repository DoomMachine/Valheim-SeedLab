# SeedLab constraint atlas - what is provably possible, per location type and per metric

Game build: `game-version=1.0.15 network=40 unity=6000.0.75f1 assembly_valheim-sha256=59f53fb55d99d22a33e8ed094eec8d21e9f133543bce92bc3d80dce44033adb1 unityplayer-sha256=4d161e15d8ccdb32eb73262e7a3e0a66f8c175b50a38e22ae5b0e8fb9aea98f3`

Two kinds of statement live in this document and they must never be mixed:

* **HARD** - follows from the decompiled code or from a field in the dumped `ZoneLocation` table.
  A query outside a hard bound has no solution anywhere in the 4,294,967,296-seed space, so the tool
  may **refuse** it.
* **EMPIRICAL** - measured on 3 real worlds plus 240 sampled seeds. These say what is *rare*.
  The tool may **warn** on them; it must never refuse on them.

## 0. Validation first

| | |
|---|---|
| Location types covered (enabled, quantity > 0) | 183 |
| Real instances checked against their derived bound | 36,829 |
| Violations | **0** |

Sources: `groundtruth/asdasdasd-locations.csv` (12,314), `groundtruth/testworldclaude-locations.csv`
(12,287) and `data/1.0.15-59f53fb5/goldens/locationinstances-0480A34C.json` (12,228). The two CSVs
carry the prefab name on only some rows, so every row is resolved by its stable hash against
`locations.json`; all 36,829 rows resolved to one of the 183 running types.

## 1. The engine-level bounds that apply to everything

| Bound | Value | Evidence |
|---|---|---|
| World radius (terrain) | 10,000 m | `WorldGenerator.worldSize` |
| Water edge | 10,500 m | `WorldGenerator.waterEdge`; `GetBiomeHeight` 1032-1035 returns `-2f * GetHeightMultiplier()` = -400 m beyond it, i.e. altitude -430 m |
| Measured disc area | 346,360,590 m2 (346.36 km2) | pi * 10500^2; `SeedSampler.SampleBiomes` skips every cell with `DUtils.Length(wx,wz) > 10500f` |
| Outer 500 m ring is always ocean | 32,201,325 m2 | `GetBaseHeight` 917-926 lerps height to -0.2 from 10,000 m and to -2 from 10,490 m, for every seed |
| Zone grid | 64 m | `ZoneSystem.m_zoneSize`; `GetZonePos(id) = id * 64` |
| **At most one location instance per zone, all types combined** | see table | `PlaceLocations` 1924-1928 rejects a zone already in `m_locationInstances`; `RegisterLocation` 2604-2611 keys the dictionary by `GetZone(pos)` and refuses a second entry |
| Candidate points are y = 0 when the radial filter runs | - | `GetRandomPointInZone` 2166-2172 returns `GetZonePos(zone) + (x, 0, z)`, so `randomPointInZone.magnitude` at line 1956 **is** the XZ distance from the origin |
| Attempts per type | 60,000 prioritized / 12,000 otherwise | `PlaceLocations` 1896 |
| Total quantity requested by the 183 running types | 12939 | sum of `m_quantity` |

**Hard cap on "N locations within R metres of the centre"** - one per 64 m zone:

| R (m) | max instances of ANY kind |
|---|---|
| 300 | 89 |
| 500 | 225 |
| 1,000 | 837 |
| 2,000 | 3,197 |
| 3,000 | 7,105 |
| 5,000 | 19,501 |
| 10,500 | 85,233 |

A query such as "12 Fuling villages within 300 m" is refusable on this alone (at most 89 zones intersect that disc).

## 2. Biome radial bands (`WorldGenerator.GetBiome`, decomp lines 779-838)

`A = WorldAngle(x, z) * 100` where `WorldAngle = sin(atan2(x, z) * 20)` - a 20-lobed +/-100 m wobble.
It is a function of bearing only: it contains no noise, no `m_offset`, no seed. Where a bound carries
it the hard interval below is the worst case over the wobble; the nominal column is the literal
constant in the branch.

| Biome | hard nearest | hard farthest | nominal | wobble on | closest real instance | slack | farthest real instance | slack |
|---|---|---|---|---|---|---|---|---|
| Meadows | 0.0 | 5100.0 | 0 .. 5000 | upper | 48.6 (WoodHouse4) | 48.6 | 5068.0 (ShipSetting01) | 32.0 |
| Swamp | 2000.0 | 6000.0 | 2000 .. 6000 | neither | 2003.7 (InfestedTree01) | 3.7 | 5999.3 (InfestedTree01) | 0.7 |
| Mountain | 600.0 | 10500.0 | 600 .. 10500 | neither | 790.8 (Runestone_Mountains) | 190.8 | 10078.5 (Runestone_Mountains) | 421.5 |
| BlackForest | 500.0 | 10500.0 | 600 .. 10500 | lower | 508.8 (StoneTowerRuins09) | 8.8 | 10275.8 (Greydwarf_camp1) | 224.2 |
| Plains | 2900.0 | 8000.0 | 3000 .. 8000 | lower | 2902.0 (GoblinHut01) | 2.0 | 7994.8 (GoblinHut01) | 5.2 |
| AshLands | 7907.7 | 10500.0 | 7908 .. 10500 | neither | 8347.1 (CharredRuins4) | 439.4 | 10136.0 (CharredRuins3) | 364.0 |
| DeepNorth | 7907.7 | 10500.0 | 7908 .. 10500 | neither | 8120.8 (DN_gammeltrollFrac01) | 213.1 | 10360.0 (ShipWreck02_DN) | 140.0 |
| Mistlands | 5900.0 | 10000.0 | 6000 .. 10000 | lower | 5912.0 (Mistlands_RoadPost1) | 12.0 | 9998.6 (Mistlands_StatueGroup1) | 1.4 |
| Ocean | 0.0 | 10500.0 | 0 .. 10500 | neither | - | - | - | - |

The slack columns are the whole argument for trusting these numbers. Swamp's 6,000 m upper bound
carries no `A` term and a real InfestedTree01 sits at 5,999.3 m - 0.7 m of slack. Mistlands' lower
bound does carry `A`, and a real Mistlands_RoadPost1 sits at 5,912.0 m, which is *below* the nominal
6,000 m and 12 m above the wobbled 5,900 m. Plains: 2,902.0 m against a wobbled 2,900 m, and
7,994.8 m against an unwobbled 8,000 m. These are the bounds working, not being guessed at.

Two bounds are **weak** and must be labelled as such in the tool:

* **Mountain, nearest 600 m.** Hard, but never approached: the closest real Mountain-only instance
  is at 790.8 m. `GetBaseHeight` 929-933 does not switch the cap off at 600 m, it *blends* it back in
  over 600 -> 1,000 m, so 600 m is the point where the cap is fully on, not where mountains become
  common. Refusing "Mountain within 500 m" is correct; refusing "within 700 m" is not.
* **AshLands, nearest 7907.7 m.** Hard and exact, but the nearest real Ashlands *location* was at
  8,347 m - locations also need land, altitude and terrain-delta.

### 2.1 AshLands is the same in every seed

`GetBiome` tests `IsAshlands` at line 796, **before** the ocean test at 800. `IsAshlands(x, y)` is
`Length(x, y - 4000) > 12000 + 100 * WorldAngle(x, y)` - distance from the fixed point (0, +4000),
compared with a fixed function of bearing. No noise, no offset, no seed.

| AshLands metric | value | seed-dependent? |
|---|---|---|
| Nearest possible point to the centre | **7907.7 m** | no |
| Area inside the 10,500 m disc | **43.03 km2** | no |
| Share of the measured disc | **12.423 %** | no |

Verified empirically: along the bearing 0.975*pi (where `sin(20*theta) = -1`) the biome flips to
AshLands between 7,908 m and 7,910 m in three unrelated seeds (12345, -998877, 2000000000), while
the biome on the near side of that line differs in all three (Black Forest / Ocean / Plains).

**Therefore every AshLands *area* and *nearest-distance* criterion is a constant, and a search on it
is either a full scan or an empty scan.** That is the second vacuous goal found in this project,
after "Eikthyr within 1.2 km".

DeepNorth is *not* the same: `IsDeepnorth` is equally seed-free, but it is tested at line 804,
**after** `baseHeight <= 0.02 -> Ocean`. The realised DeepNorth biome is the region minus a
seed-dependent ocean part, so DeepNorth area <= 43.03 km2 and nearest >= 7907.7 m are one-sided hard
bounds, not constants.

Land/ocean share has **no** code-level bound beyond 0..1 - `GetBaseHeight` is pure Perlin noise
offset by `m_offset0`/`m_offset1`, which are drawn from the seed (`WorldGenerator` 223-229). The only
hard statements are the disc area, the fixed AshLands share, and the always-ocean outer ring.

## 2.2 Which biome-level metrics actually move with the seed

Measured on 40 uniformly drawn seeds with `vseed seed --json` (the 2048x2048, 12 m game grid; the
tool reports its own distance uncertainty as 8.49 m, one cell diagonal).

| Biome | distinct "nearest from centre" values in 40 seeds | range | spread | biome share range |
|---|---|---|---|---|
| Meadows | 4 | 8.5 .. 119.1 m | 110.6 m | 0.0253 .. 0.0390 |
| Black Forest | 1 | 508.3 .. 508.3 m | 0.0 m | 0.1181 .. 0.1740 |
| Swamp | 4 | 2000.0 .. 2000.5 m | 0.5 m | 0.0228 .. 0.0317 |
| Mountain | 37 | 619.4 .. 1187.5 m | 568.1 m | 0.0241 .. 0.0367 |
| Plains | 3 | 2901.9 .. 2902.6 m | 0.7 m | 0.0933 .. 0.1422 |
| Mistlands | 3 | 5900.4 .. 5900.9 m | 0.5 m | 0.1615 .. 0.1828 |
| Ashlands | 1 | 7908.9 .. 7908.9 m | 0.0 m | 0.1242 .. 0.1242 |
| Deep North | 10 | 7908.9 .. 7971.7 m | 62.9 m | 0.0609 .. 0.0767 |
| Ocean | 38 | 8.5 .. 846.0 m | 837.5 m | 0.2675 .. 0.2947 |

**This is the single most useful result for refusing pointless searches.** "Nearest Black Forest to
the centre" took exactly ONE value in 40 seeds (508.3 m). Plains varied by 0.7 m, Mistlands by 0.5 m,
Swamp by 0.5 m, Ashlands by 0 m. Only **Mountain** (619 - 1,188 m), **Ocean** (9 - 846 m) and, weakly,
**Meadows** (9 - 119 m) move at all.

The mechanism is geometric, so it will hold for the whole seed space and not just this sample: each
band's inner edge is a circle of circumference 2*pi*r - 37.7 km for Mistlands - and the biome test
there is a Perlin threshold (`> 0.4`, `> 0.6`) that a large fraction of that ring passes. With 12 m
cells there are thousands of samples on the ring, so at least one passes in practically every seed,
and the measured "nearest" is pinned to the band edge. Mountain is the exception because its test is
`baseHeight > 0.4` under a cap that is blended off between 600 m and 1,000 m, so *how close* a
mountain gets really is a property of the noise.

A "nearest biome X within R km" criterion should therefore be treated as:

* **AshLands** - constant, provably. Refuse.
* **Black Forest, Swamp, Plains, Mistlands, Deep North** - constant to within a metre or two across
  40 seeds. Refuse if R is on the far side of the band edge; warn loudly otherwise that the criterion
  does not discriminate. Mark this as empirical, because it is.
* **Mountain, Ocean, Meadows** - genuinely seed-dependent. Allow.

Biome *area* is a different story and does vary usefully - Black Forest 11.8-17.4 % of the disc,
Plains 9.3-14.2 %, Mistlands 16.2-18.3 %, Meadows 2.5-3.9 % - **except AshLands, which was
43,031,376 m2 (298,829 cells, 12.4236 %) in every single seed measured, matching the 43.03 km2
derived analytically above to within 0.003 %.**

Land, ocean and island metrics over the same 40 seeds (all EMPIRICAL - the code bounds them only by
0 and the disc):

| Metric | observed range | hard bound |
|---|---|---|
| Land share of the 10,500 m disc | 0.3309 .. 0.3608 | 0 .. 1 (the outer 500 m ring is always water) |
| Islands of at least 10,000 m2 | 199 .. 261 | <= disc area / 10,000 m2 = 34,636 |
| Largest island | 3.79 .. 7.19 km2 | <= land area <= 314.16 km2 (disc of 10,000 m) |
| Island containing the origin | 144 m2 .. 7.19 km2 | 0 (the origin can be open water) .. same |
| Nearest land to the origin | 8.5 .. 251.3 m | >= 0 |

Note the spawn island: its measured minimum was a single 144 m2 cell. Nothing in the generator
guarantees the world centre is on land, so "large island at spawn" is a legitimate, non-vacuous
criterion - unlike most of the nearest-biome ones.

## 3. The radial filters on the location table

`PlaceLocations` applies **two** radial filters and they are numerically the same quantity:

```
1956  if (location.m_minDistance != 0f && magnitude < location.m_minDistance)   // magnitude is XZ, y=0
1962  if (location.m_maxDistance != 0f && magnitude > location.m_maxDistance)
1993  if (location.m_minDistanceFromCenter > 0f || location.m_maxDistanceFromCenter > 0f)
1996      ... Utils.LengthXZ(randomPointInZone) ...
```

So **0 really does mean unconstrained** for all four fields - confirmed in the filter code, not
assumed. Of the 183 running types, 21 carry at least one of them:

| Type | minDistance | maxDistance | minDistFromCenter | maxDistFromCenter | derived annulus | observed min | observed max |
|---|---|---|---|---|---|---|---|
| StartTemple | 0 | 10000 | 0 | 0 | 0 .. 5100 | 71 | 228 |
| Eikthyrnir | 0 | 1000 | 0 | 0 | 0 .. 1000 | 131 | 889 |
| Runestone_Greydwarfs | 0 | 2000 | 0 | 0 | 500 .. 2000 | 531 | 1996 |
| GDKing | 1000 | 7000 | 0 | 0 | 1000 .. 7000 | 1548 | 6998 |
| Bonemass | 2000 | 10000 | 0 | 0 | 2000 .. 6000 | 2762 | 5904 |
| WoodFarm1 | 500 | 2000 | 0 | 0 | 500 .. 2000 | 528 | 1937 |
| WoodVillage1 | 2000 | 10000 | 0 | 0 | 2000 .. 5100 | 2041 | 5007 |
| Vendor_BlackForest | 1500 | 0 | 0 | 0 | 1500 .. 10500 | 2108 | 10056 |
| Dragonqueen | 0 | 8000 | 0 | 0 | 600 .. 8000 | 1256 | 7970 |
| DrakeNest01 | 0 | 10000 | 0 | 0 | 600 .. 10000 | 798 | 9976 |
| CombatRuin01 | 1500 | 10000 | 1500 | 0 | 1500 .. 5100 | 1879 | 4784 |
| BigRockClearing | 1000 | 10000 | 1000 | 0 | 1000 .. 10000 | 1451 | 9902 |
| AncientUpgradeStation | 200 | 0 | 500 | 0 | 600 .. 10500 | 1528 | 9533 |
| Hildir_cave | 1000 | 8000 | 0 | 0 | 1000 .. 8000 | 3009 | 7930 |
| Hildir_camp | 3000 | 8000 | 0 | 0 | 3000 .. 5100 | 3017 | 4956 |
| Hildir_crypt | 3000 | 8000 | 0 | 0 | 3000 .. 8000 | 3938 | 7933 |
| BogWitch_Camp | 3000 | 8000 | 0 | 0 | 3000 .. 6000 | 3073 | 5848 |
| FrozenShip01_DN | 0 | 0 | 8000 | 9750 | 8000 .. 9750 | 8026 | 9734 |
| FrozenShip02_DN | 0 | 0 | 8000 | 9750 | 8000 .. 9750 | 8034 | 9739 |
| FrozenShip03_DN | 0 | 0 | 8000 | 9750 | 8000 .. 9750 | 8040 | 9749 |
| WoodVillage2 | 2000 | 10000 | 0 | 0 | 2000 .. 5100 | 2127 | 4985 |

## 4. Vacuous goals - as harmful as impossible ones

A filter whose bound is looser than the hard bound cannot reject anything. It silently converts a
targeted search into a full scan of 4.29 billion seeds.

| Goal of the form ... | is vacuous for any R at or above | because |
|---|---|---|
| "Eikthyrnir within R of the centre" | **1000 m** | m_maxDistance=1000 |
| "GDKing within R of the centre" | **7000 m** | m_maxDistance=7000 |
| "Bonemass within R of the centre" | **6000 m** | m_maxDistance=10000, biome band Swamp |
| "Dragonqueen within R of the centre" | **8000 m** | m_maxDistance=8000 |
| "GoblinKing within R of the centre" | **8000 m** | biome band Plains |
| "Hildir_camp within R of the centre" | **5100 m** | m_maxDistance=8000, biome band Meadows |
| "Hildir_crypt within R of the centre" | **8000 m** | m_maxDistance=8000 |
| "BogWitch_Camp within R of the centre" | **6000 m** | m_maxDistance=8000, biome band Swamp |
| "StartTemple within R of the centre" | **5100 m** | m_maxDistance=10000, biome band Meadows |
| "Runestone_Greydwarfs within R of the centre" | **2000 m** | m_maxDistance=2000 |
| "WoodFarm1 within R of the centre" | **2000 m** | m_maxDistance=2000 |

And the mirror image, lower bounds - "at least R from the centre" is vacuous below these:

| Goal | vacuous for any R at or below | because |
|---|---|---|
| "Bonemass at least R from the centre" | **2000 m** | m_minDistance=2000 |
| "GDKing at least R from the centre" | **1000 m** | m_minDistance=1000 |
| "GoblinKing at least R from the centre" | **2900 m** | biome band Plains |
| "Vendor_BlackForest at least R from the centre" | **1500 m** | m_minDistance=1500 |
| "Hildir_camp at least R from the centre" | **3000 m** | m_minDistance=3000 |
| "BogWitch_Camp at least R from the centre" | **3000 m** | m_minDistance=3000 |
| "FaderLocation at least R from the centre" | **7908 m** | biome band AshLands |
| "Mistlands_DvergrBossEntrance1 at least R from the centre" | **5900 m** | biome band Mistlands |

The flagship case: **"Eikthyr within 1.2 km of the centre" is vacuous.** `Eikthyrnir` carries
`m_maxDistance = 1000`, so every Eikthyr altar that exists at all is inside 1 km. Stated precisely,
because this matters for how the tool should word the refusal: the filter is satisfied by every seed
in which at least one Eikthyrnir was placed, and can only ever reject a seed in which all three
failed to place - which is a completely different (and far rarer) condition than the player means.
Observed maximum over the three real worlds: 889 m; over 240 sampled seeds: 992 m.

## 5. Structurally impossible types

* **GoblinCamp2_1** (`m_quantity` 5, enabled) - m_biomeArea=0 masks to 0 against {Edge=1, Median=2} (Heightmap.BiomeArea, decomp/Heightmap.cs 47-53, and WorldGenerator.GetBiomeArea 705-723 only ever RETURNS Edge or Median). ZoneSystem.PlaceLocations 1934 `(location.m_biomeArea & biomeArea) == 0` is therefore true for every zone in every seed: this type can never be placed.

Confirmed in the real data: `GoblinCamp2_1` has 0 instances in all three worlds, and the world-gen
log in `groundtruth/LogOutput-20260922-worldgen.log` records `Failed to place all GoblinCamp2_1,
placed 0 out of 5`. **Any query that requires a GoblinCamp2_1 is refusable outright.**

## 6. Altitude windows and what they mean geometrically

Altitude is `GetHeight(x, z) - 30` (`PlaceLocations` 1973-1976); `GetHeightMultiplier()` is 200 and
the water line is y = 30 m, so altitude 0 is exactly sea level.

| Type | min alt | max alt | geometric meaning | observed alt range (golden world) |
|---|---|---|---|---|
| StartTemple | 3 | 1000 | - | 55.4 .. 55.4 |
| Eikthyrnir | 1 | 1000 | - | 10.6 .. 37.9 |
| GDKing | 1 | 1000 | - | 9.8 .. 35.8 |
| Bonemass | 0 | 2 | straddles the water line (altitude 0 == y 30 m): shoreline / shallows only; window is only 2 m tall - a very thin altitude band | 0.0 .. 0.9 |
| Dragonqueen | 150 | 500 | needs y >= 180 m, which only Mountain terrain reaches: high peaks | 162.7 .. 220.9 |
| GoblinKing | 1 | 1000 | - | 3.5 .. 15.7 |
| Vendor_BlackForest | 1 | 1000 | - | 1.2 .. 32.3 |
| Hildir_camp | 1 | 1000 | - | 1.1 .. 48.8 |
| FaderLocation | 1 | 300 | - | 3.6 .. 27.1 |
| Mistlands_DvergrBossEntrance1 | 1 | 20 | - | 2.5 .. 19.5 |

## 7. Spacing, groups and how many can coexist

* `m_quantity` is the hard ceiling on instances of a type; `m_unique` caps it at 1 regardless
  (`PlaceLocations` 1901: `if (!location.m_unique || placed <= 0)`). 8 types are unique.
* `m_minDistanceFromSimilar` rejects a candidate that is within that distance of an already-placed
  instance **of the same asset OR of the same `m_group`** (`HaveLocationInRange` 2626-2650).
* That comparison is `(l[i].m_position - p).sqrMagnitude`, and `p.y` has already been set from
  `GetHeight` at line 1973, so **the spacing is a 3D distance**. Two instances on a slope can be
  closer than `m_minDistanceFromSimilar` *horizontally*. A circle-packing bound computed from the
  raw spacing is therefore a heuristic, not a proof; the one-per-zone bound in section 1 is the
  proof.

Groups that share a spacing budget (every member excludes every other):

* `Runestones` (12): Runestone_Greydwarfs, Runestone_Draugr, DrakeLorestone, Runestone_Meadows, Runestone_Boars, Runestone_Swamps, Runestone_Mountains, Runestone_BlackForest, Runestone_Plains, Runestone_Mistlands, Runestone_Ashlands, Runestone_DeepNorth
* `Dvergr` (7): Mistlands_GuardTower1_new, Mistlands_GuardTower1_ruined_new, Mistlands_GuardTower1_ruined_new2, Mistlands_GuardTower2_new, Mistlands_GuardTower3_new, Mistlands_GuardTower3_ruined_new, Mistlands_Lighthouse1_new
* `Stonehenge` (6): StoneHenge1, StoneHenge2, StoneHenge3, StoneHenge4, StoneHenge5, StoneHenge6
* `Swamphut` (5): SwampHut1, SwampHut2, SwampHut3, SwampHut4, SwampHut5
* `Stonetowerruins` (5): StoneTowerRuins03, StoneTowerRuins07, StoneTowerRuins08, StoneTowerRuins09, StoneTowerRuins10
* `Shipwreck` (4): ShipWreck01, ShipWreck02, ShipWreck03, ShipWreck04
* `Stonetowerruins_sunk` (4): StoneTowerRuins07_sunk, StoneTowerRuins08_sunk, StoneTowerRuins09_sunk, StoneTowerRuins10_sunk
* `Goblintower` (3): Ruin3, StoneTower1, StoneTower3
* `woodvillage` (3): WoodFarm1, WoodVillage1, WoodVillage2
* `Abandonedcabin` (3): AbandonedLogCabin02, AbandonedLogCabin03, AbandonedLogCabin04
* `tarpit` (3): TarPit1, TarPit2, TarPit3
* `Excavation` (3): Mistlands_Excavation1, Mistlands_Excavation2, Mistlands_Excavation3
* `Harbour` (3): Mistlands_Harbour1, Mistlands_Viaduct1, Mistlands_Viaduct2
* `GiantArmor` (3): Mistlands_Swords1, Mistlands_Swords2, Mistlands_Swords3
* `PlaceofMystery` (3): PlaceofMystery1, PlaceofMystery2, PlaceofMystery3
* `MorgenHole` (3): MorgenHole1, MorgenHole2, MorgenHole3
* `FrozenShip` (3): FrozenShip01_DN, FrozenShip02_DN, FrozenShip03_DN
* `SwampRuin` (2): SwampRuin1, SwampRuin2
* `Mountainruin` (2): StoneTowerRuins04, StoneTowerRuins05
* `Giant` (2): Mistlands_Giant1, Mistlands_Giant2
* `DvergrDungeon` (2): Mistlands_DvergrTownEntrance1, Mistlands_DvergrTownEntrance2
* `FaderBoss` (2): FaderLocation, CharredFortress
* `towerruins` (2): CharredTowerRuins1, CharredTowerRuins1_dvergr
* `shipsetting` (2): ShipSetting02, ShipSetting03

## 8. Pairwise geometry - bounds on the separation of two location types

Both annuli are centred on the world origin, so the triangle inequality gives:
`|d1 - d2| <= separation <= d1 + d2`, minimised/maximised over the two annuli. A lower bound of 0
means the annuli overlap and the bound is **trivially satisfied** - the tool must say so rather than
pretend it has constrained anything.

| Pair | annulus A | annulus B | provable min separation | provable max separation | trivial? | observed (3 real worlds, all pairs) |
|---|---|---|---|---|---|---|
| GDKing - Bonemass | 1000..7000 | 2000..6000 | **0 m** | **13000 m** | lower | 642 .. 12282 m (n=60) |
| Eikthyrnir - GDKing | 0..1000 | 1000..7000 | **0 m** | **8000 m** | lower | 1437 .. 7500 m (n=36) |
| Vendor_BlackForest - StartTemple | 1500..10500 | 0..5100 | **0 m** | **15600 m** | lower | 1883 .. 9997 m (n=30) |
| Dragonqueen - GoblinKing | 600..8000 | 2900..8000 | **0 m** | **16000 m** | lower | 589 .. 13784 m (n=36) |
| Eikthyrnir - StartTemple | 0..1000 | 0..5100 | **0 m** | **6100 m** | lower | 79 .. 952 m (n=9) |
| Bonemass - Dragonqueen | 2000..6000 | 600..8000 | **0 m** | **14000 m** | lower | 1352 .. 13558 m (n=45) |
| GoblinKing - Hildir_camp | 2900..8000 | 3000..5100 | **0 m** | **13100 m** | lower | 942 .. 11529 m (n=120) |
| StartTemple - Hildir_camp | 0..5100 | 3000..5100 | **0 m** | **10200 m** | lower | 3030 .. 5090 m (n=30) |
| Vendor_BlackForest - Eikthyrnir | 1500..10500 | 0..1000 | **500 m** | **11500 m** | no | 2003 .. 10325 m (n=90) |
| Dragonqueen - StartTemple | 600..8000 | 0..5100 | **0 m** | **13100 m** | lower | 1055 .. 8111 m (n=9) |

Worked readings of the pairs a player actually asks for:

* **Elder - Bonemass.** Elder is pinned to 1,000-7,000 m by `m_minDistance`/`m_maxDistance`;
  Bonemass to 2,000-6,000 m (its own 2,000-10,000 m, tightened by the Swamp band's 6,000 m ceiling).
  The annuli overlap, so **no positive lower bound exists**: "Elder and Bonemass within 200 m of each
  other" is not refusable. The upper bound is 13000 m, so "Elder and Bonemass at least 14 km apart" IS
  refusable. Observed across the three worlds: 642 - 12282 m.
* **Eikthyr - Elder.** Eikthyr 0-1,000 m, Elder 1,000-7,000 m. The annuli touch at exactly 1,000 m,
  so the lower bound is 0 and again not refusable - but the **upper** bound is only 8000 m, which is
  a genuinely useful constraint: "Eikthyr and Elder more than 8 km apart" is impossible.
* **Haldor - spawn.** Haldor is >= 1,500 m from the *centre*, and the spawn (StartTemple) is only
  bounded by the Meadows band, 0-5,100 m. Lower bound on their separation: **0 m** - trivial,
  because the spawn can itself be far out. In practice the spawn is never far out: over the three
  real worlds StartTemple sits at 71-228 m from the centre, so Haldor is *empirically* almost never
  closer than ~1,300 m to it. That is a WARN, not a refusal.
* **Haldor - Eikthyr: the one pair with a real lower bound.** Haldor (`Vendor_BlackForest`) is
  >= 1,500 m from the centre and Eikthyr is <= 1,000 m from it, so the annuli are disjoint and
  **their separation is provably at least 500 m in every seed**. "Haldor within 400 m of the Eikthyr
  altar" is refusable, citing `Vendor_BlackForest.m_minDistance = 1500` against
  `Eikthyrnir.m_maxDistance = 1000`. Observed over the three worlds: 2,003 - 10,325 m, so 500 m is a
  sound but weak bound - the tool should refuse below 500 m and warn below ~2 km.
* **Moder - Yagluth.** Moder 600-8,000 m (Mountain lower bound, own `m_maxDistance` 8,000); Yagluth
  2,900-8,000 m (Plains band). Overlapping, so lower bound 0; upper bound 16000 m. Observed
  589 - 13784 m.

The honest summary of pairwise geometry: **almost every lower bound is 0.** Radial annuli centred on
the same origin overlap for nearly every interesting pair, so the triangle inequality gives the tool
an upper bound worth checking and a lower bound that is nearly always trivial. The tool should offer
the feature, and should say "no constraint" out loud when the lower bound is 0, rather than printing
a 0 that looks like an answer.

## 9. What is only RARE - warn, never refuse

### 9.1 Types that failed to reach `m_quantity` in real worlds

33 of the 183 types fell short of their `m_quantity` in at least one of the three worlds, and 9 were
**absent entirely** from at least one. Absence is normal, not a bug, and a query that requires one of
these must warn.

| Type | quantity | asdasdasd | testworldclaude | fresh world (75539276) | alt-biome parent | parent guaranteed? |
|---|---|---|---|---|---|---|
| GoblinCamp2_1 | 5 | 0 | 0 | 0 | Goblin Plains | **no** |
| GoblinHut03 | 20 | 1 | 2 | 0 | Goblin Plains | **no** |
| StoneTowerRuins05_leet | 10 | 0 | 0 | 10 | Fortress Mountain | yes |
| StoneTowerRuins10_sunk | 10 | 6 | 0 | 3 | Ruin Black Forest | yes |
| SwampHut2_1 | 50 | 0 | 2 | 1 | Hut Swamp | yes |
| SwampHut3_1 | 50 | 0 | 2 | 1 | Hut Swamp | yes |
| TarPit1_1 | 50 | 0 | 0 | 0 | Death Plains | **no** |
| TarPit2_1 | 20 | 0 | 0 | 0 | Death Plains | **no** |
| TarPit3_1 | 100 | 0 | 0 | 0 | Death Plains | **no** |

The `alt-biome parent` column is the mechanism. `PlaceLocations` 2032-2041 rejects a candidate whose
biome sector does not contain the named alt-biome. Three alt-biomes ship with
`m_minAmountSpawned = 0` - **Lox Plains, Goblin Plains, Death Plains** - so they can be absent from a
world entirely, and with them every location that names them as a parent. `TarPit1_1`, `TarPit2_1`,
`TarPit3_1`, `GoblinCamp2_1` and `GoblinHut01..03` are in that class - and the three TarPit_1
variants plus GoblinCamp2_1 were absent from all three real worlds. The other parents
(`Dark Meadows` minN 2, `Ruin Black Forest` 1, `Hut Swamp` 1, `Fortress Mountain` 1) are guaranteed
to be *placed*, which is still not a guarantee that any location inside them succeeds.

### 9.2 Boss and trader distance distributions

Sampled with the shipped `vseed locations --type boss,trader` over **240** uniformly drawn int32
seeds. Every number in this table is empirical except the two bound columns.

| Type | HARD annulus | nearest-per-seed min | p5 | median | p95 | max | seeds short of quantity |
|---|---|---|---|---|---|---|---|
| StartTemple | 0 .. 5100 | 0 | 3 | 68 | 365 | 499 | 0 / 240 |
| Eikthyrnir | 0 .. 1000 | 60 | 101 | 290 | 543 | 826 | 0 / 240 |
| GDKing | 1000 .. 7000 | 1000 | 1092 | 1849 | 3134 | 5266 | 0 / 240 |
| Bonemass | 2000 .. 6000 | 2092 | 2162 | 2926 | 4209 | 5039 | 0 / 240 |
| Dragonqueen | 600 .. 8000 | 963 | 1401 | 3661 | 5796 | 7555 | 0 / 240 |
| GoblinKing | 2900 .. 8000 | 3042 | 3139 | 3964 | 5141 | 5950 | 0 / 240 |
| Vendor_BlackForest | 1500 .. 10500 | 1508 | 1567 | 1944 | 3085 | 5166 | 0 / 240 |
| Hildir_camp | 3000 .. 5100 | 3000 | 3004 | 3061 | 3465 | 3917 | 1 / 240 |
| BogWitch_Camp | 3000 .. 6000 | 3000 | 3014 | 3248 | 3869 | 4290 | 0 / 240 |
| FaderLocation | 7908 .. 10500 | 8326 | 8554 | 8934 | 9461 | 9677 | 0 / 240 |

Read this table as: *the left two columns may drive a refusal; nothing to the right of them may.*
A request for "Haldor within 1,800 m" is legal (the hard lower bound is 1,500 m) but the sample says
it is worth warning about; a request for "Haldor within 1,400 m" is **impossible** and must be
refused, citing `Vendor_BlackForest.m_minDistance = 1500`.

## 10. Per-type table

The machine-readable form is `constraint-atlas.json`. Columns: `qty`/`uniq` = `m_quantity` and
`m_unique`; `annulus` = derived hard radial interval; `wob` = which endpoint carries the +/-100 m
WorldAngle wobble; `alt` = `m_minAltitude .. m_maxAltitude` in metres above the water line;
`sim` = `m_minDistanceFromSimilar` (3D); `n` = real instances checked; `slack` = distance between
the derived endpoint and the nearest real instance to it.

| # | prefab | biomes | area | qty | uniq | annulus | wob | alt | sim | group | n | obs min | obs max | slackLo | slackHi |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | StartTemple | Meadows | M | 1 |  | 0 .. 5100 | upper | 3 .. 1000 | 0 | - | 3 | 71 | 228 | 71 | 4872 |
| 1 | Eikthyrnir | Meadows | M | 3 |  | 0 .. 1000 | - | 1 .. 1000 | 0 | - | 9 | 131 | 889 | 131 | 111 |
| 2 | GoblinKing | Plains | M | 4 |  | 2900 .. 8000 | lower | 1 .. 1000 | 3000 | - | 12 | 3747 | 7720 | 847 | 280 |
| 3 | GDKing | BlackForest | M | 4 |  | 1000 .. 7000 | - | 1 .. 1000 | 3000 | - | 12 | 1548 | 6998 | 548 | 2 |
| 4 | Bonemass | Swamp | M | 5 |  | 2000 .. 6000 | - | 0 .. 2 | 3000 | - | 15 | 2762 | 5904 | 762 | 96 |
| 5 | SunkenCrypt4 | Swamp | M | 175 |  | 2000 .. 6000 | - | 0 .. 2 | 64 | SunkenCrypt | 525 | 2248 | 5925 | 248 | 75 |
| 6 | Vendor_BlackForest | BlackForest | M | 10 | Y | 1500 .. 10500 | - | 1 .. 1000 | 512 | - | 30 | 2108 | 10056 | 608 | 444 |
| 7 | Dragonqueen | Mountain | M | 3 |  | 600 .. 8000 | - | 150 .. 500 | 3000 | - | 9 | 1256 | 7970 | 656 | 30 |
| 8 | AncientUpgradeStation | Mountain | M | 10 | Y | 600 .. 10500 | - | 0 .. 5000 | 400 | AncientUpgradeStation | 30 | 1528 | 9533 | 928 | 967 |
| 9 | Mistlands_DvergrTownEntrance1 | Mistlands | M | 120 |  | 5900 .. 10000 | lower | 12 .. 1000 | 256 | DvergrDungeon | 360 | 6026 | 9892 | 126 | 108 |
| 10 | Mistlands_DvergrTownEntrance2 | Mistlands | M | 120 |  | 5900 .. 10000 | lower | 1 .. 20 | 256 | DvergrDungeon | 360 | 6023 | 9903 | 123 | 97 |
| 11 | Mistlands_DvergrBossEntrance1 | Mistlands | M | 5 |  | 5900 .. 10000 | lower | 1 .. 20 | 2048 | DvergrBoss | 15 | 6341 | 9533 | 441 | 467 |
| 12 | Hildir_cave | Mountain | M | 3 |  | 1000 .. 8000 | - | 200 .. 5000 | 2000 | - | 9 | 3009 | 7930 | 2009 | 70 |
| 13 | Hildir_camp | Meadows | EM | 10 | Y | 3000 .. 5100 | upper | 1 .. 1000 | 1000 | - | 30 | 3017 | 4956 | 17 | 144 |
| 14 | Hildir_crypt | BlackForest | M | 3 |  | 3000 .. 8000 | - | 1 .. 1000 | 3000 | - | 9 | 3938 | 7933 | 938 | 67 |
| 15 | Hildir_plainsfortress | Plains | M | 3 |  | 2900 .. 8000 | lower | 8 .. 1000 | 3000 | - | 9 | 3394 | 6520 | 494 | 1480 |
| 16 | FaderLocation | AshLands | M | 3 |  | 7908 .. 10500 | - | 1 .. 300 | 2048 | FaderBoss | 9 | 9097 | 10013 | 1190 | 487 |
| 17 | PlaceofMystery1 | AshLands | M | 1 | Y | 7908 .. 10500 | - | 0 .. 1000 | 2048 | PlaceofMystery | 3 | 9028 | 9171 | 1120 | 1329 |
| 18 | PlaceofMystery2 | AshLands | M | 1 | Y | 7908 .. 10500 | - | 0 .. 1000 | 2048 | PlaceofMystery | 3 | 9108 | 9681 | 1200 | 819 |
| 19 | PlaceofMystery3 | AshLands | M | 1 | Y | 7908 .. 10500 | - | 0 .. 1000 | 2048 | PlaceofMystery | 3 | 9267 | 9537 | 1359 | 963 |
| 20 | CharredFortress | AshLands | EM | 20 |  | 7908 .. 10500 | - | 20 .. 1000 | 256 | FaderBoss | 60 | 8582 | 9728 | 674 | 772 |
| 21 | BogWitch_Camp | Swamp | M | 10 | Y | 3000 .. 6000 | - | 1 .. 1000 | 1000 | - | 30 | 3073 | 5848 | 73 | 152 |
| 22 | DN_Bossroom | DeepNorth | EM | 3 |  | 7908 .. 10500 | - | 80 .. 5000 | 1024 | dn_boss | 9 | 8585 | 9971 | 677 | 529 |
| 23 | StoneCircle | Meadows | EM | 25 |  | 0 .. 5100 | upper | 1 .. 1000 | 200 | - | 75 | 279 | 4984 | 279 | 116 |
| 24 | Greydwarf_camp1 | BlackForest | M | 300 |  | 500 .. 10500 | lower | 1 .. 1000 | 128 | - | 900 | 729 | 10276 | 229 | 224 |
| 25 | Runestone_Greydwarfs | BlackForest | EM | 25 |  | 500 .. 2000 | lower | 1 .. 1000 | 128 | Runestones | 75 | 531 | 1996 | 31 | 4 |
| 26 | Grave1 | Swamp | M | 200 |  | 2000 .. 6000 | - | 0.5 .. 1000 | 0 | - | 600 | 2072 | 5941 | 72 | 59 |
| 27 | SwampRuin1 | Swamp | M | 30 |  | 2000 .. 6000 | - | -0.5 .. 1000 | 256 | SwampRuin | 90 | 2175 | 5873 | 175 | 127 |
| 28 | SwampRuin2 | Swamp | M | 30 |  | 2000 .. 6000 | - | -0.5 .. 1000 | 256 | SwampRuin | 55 | 2181 | 5842 | 181 | 158 |
| 29 | FireHole | Swamp | M | 75 |  | 2000 .. 6000 | - | -1 .. 1000 | 16 | FireHole | 176 | 2057 | 5989 | 57 | 11 |
| 30 | Runestone_Draugr | Swamp | EM | 50 |  | 2000 .. 6000 | - | 0.5 .. 1000 | 128 | Runestones | 150 | 2016 | 5992 | 16 | 8 |
| 31 | Crypt2 | BlackForest | EM | 200 |  | 500 .. 10500 | lower | 1 .. 1000 | 128 | - | 600 | 537 | 10204 | 37 | 296 |
| 32 | Ruin1 | BlackForest | EM | 200 |  | 500 .. 10500 | lower | 1 .. 1000 | 0 | - | 600 | 643 | 10183 | 143 | 317 |
| 33 | Ruin2 | BlackForest | EM | 200 |  | 500 .. 10500 | lower | 1 .. 1000 | 0 | - | 600 | 592 | 10214 | 92 | 286 |
| 34 | StoneHouse3 | BlackForest | EM | 200 |  | 500 .. 10500 | lower | 1 .. 1000 | 0 | - | 600 | 536 | 10273 | 36 | 227 |
| 35 | StoneHouse4 | BlackForest | EM | 200 |  | 500 .. 10500 | lower | 1 .. 1000 | 0 | - | 600 | 533 | 10250 | 33 | 250 |
| 36 | Ruin3 | Plains | EM | 50 |  | 2900 .. 8000 | lower | 1 .. 1000 | 512 | Goblintower | 150 | 2960 | 7960 | 60 | 40 |
| 37 | GoblinCamp2 | Plains | EM | 200 |  | 2900 .. 8000 | lower | 2 .. 1000 | 250 | - | 590 | 2997 | 7968 | 97 | 32 |
| 38 | StoneTower1 | Plains | EM | 50 |  | 2900 .. 8000 | lower | 1 .. 1000 | 512 | Goblintower | 150 | 3050 | 7988 | 150 | 12 |
| 39 | StoneTower3 | Plains | EM | 50 |  | 2900 .. 8000 | lower | 1 .. 1000 | 512 | Goblintower | 150 | 2971 | 7942 | 71 | 58 |
| 40 | StoneHenge1 | Plains | EM | 5 |  | 2900 .. 8000 | lower | 5 .. 1000 | 1000 | Stonehenge | 15 | 2950 | 7701 | 50 | 299 |
| 41 | StoneHenge2 | Plains | EM | 5 |  | 2900 .. 8000 | lower | 5 .. 1000 | 1000 | Stonehenge | 15 | 2950 | 7899 | 50 | 101 |
| 42 | StoneHenge3 | Plains | EM | 5 |  | 2900 .. 8000 | lower | 5 .. 1000 | 1000 | Stonehenge | 15 | 3326 | 7598 | 426 | 402 |
| 43 | StoneHenge4 | Plains | EM | 5 |  | 2900 .. 8000 | lower | 5 .. 1000 | 1000 | Stonehenge | 15 | 3180 | 7115 | 280 | 885 |
| 44 | StoneHenge5 | Plains | EM | 20 |  | 2900 .. 8000 | lower | 2 .. 1000 | 500 | Stonehenge | 60 | 3155 | 7973 | 255 | 27 |
| 45 | StoneHenge6 | Plains | EM | 20 |  | 2900 .. 8000 | lower | 2 .. 1000 | 500 | Stonehenge | 60 | 3310 | 7867 | 410 | 133 |
| 46 | WoodHouse1 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 409 | 4930 | 409 | 170 |
| 47 | WoodHouse2 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 276 | 4744 | 276 | 356 |
| 48 | WoodHouse3 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 241 | 4904 | 241 | 196 |
| 49 | WoodHouse4 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 49 | 4976 | 49 | 124 |
| 50 | WoodHouse5 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 90 | 4804 | 90 | 296 |
| 51 | WoodHouse6 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 331 | 4969 | 331 | 131 |
| 52 | WoodHouse7 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 175 | 4863 | 175 | 237 |
| 53 | WoodHouse8 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 323 | 5049 | 323 | 51 |
| 54 | WoodHouse9 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 365 | 4914 | 365 | 186 |
| 55 | WoodHouse10 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 260 | 4932 | 260 | 168 |
| 56 | WoodHouse11 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 352 | 4804 | 352 | 296 |
| 57 | WoodHouse12 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 207 | 4883 | 207 | 217 |
| 58 | WoodHouse13 | Meadows | EM | 20 |  | 0 .. 5100 | upper | 1 .. 1000 | 0 | - | 60 | 250 | 4977 | 250 | 123 |
| 59 | WoodFarm1 | Meadows | EM | 10 |  | 500 .. 2000 | - | 1 .. 1000 | 128 | woodvillage | 30 | 528 | 1937 | 28 | 63 |
| 60 | WoodVillage1 | Meadows | EM | 15 |  | 2000 .. 5100 | upper | 1 .. 1000 | 256 | woodvillage | 45 | 2041 | 5007 | 41 | 93 |
| 61 | TrollCave02 | BlackForest | M | 200 |  | 500 .. 10500 | lower | 3 .. 1000 | 256 | - | 600 | 775 | 10259 | 275 | 241 |
| 62 | Dolmen01 | Meadows\|BlackForest | EM | 100 |  | 0 .. 10500 | - | 1 .. 1000 | 0 | - | 300 | 132 | 5088 | 132 | 5412 |
| 63 | Dolmen02 | Meadows\|BlackForest | EM | 100 |  | 0 .. 10500 | - | 1 .. 1000 | 0 | - | 300 | 87 | 4987 | 87 | 5513 |
| 64 | Dolmen03 | Meadows\|BlackForest | EM | 50 |  | 0 .. 10500 | - | 1 .. 1000 | 0 | - | 150 | 121 | 5027 | 121 | 5473 |
| 65 | Crypt3 | BlackForest | EM | 200 |  | 500 .. 10500 | lower | 3 .. 1000 | 128 | - | 600 | 606 | 10211 | 106 | 289 |
| 66 | Crypt4 | BlackForest | EM | 200 |  | 500 .. 10500 | lower | 1 .. 1000 | 128 | - | 515 | 587 | 10187 | 87 | 313 |
| 67 | InfestedTree01 | Swamp | EM | 700 |  | 2000 .. 6000 | - | -1 .. 1000 | 0 | - | 2100 | 2004 | 5999 | 4 | 1 |
| 68 | SwampHut1 | Swamp | EM | 50 |  | 2000 .. 6000 | - | -2 .. 1000 | 128 | Swamphut | 150 | 2013 | 5983 | 13 | 17 |
| 69 | SwampHut2 | Swamp | EM | 50 |  | 2000 .. 6000 | - | 2 .. 1000 | 128 | Swamphut | 150 | 2272 | 5986 | 272 | 14 |
| 70 | SwampHut3 | Swamp | EM | 50 |  | 2000 .. 6000 | - | 2 .. 1000 | 128 | Swamphut | 136 | 2141 | 5984 | 141 | 16 |
| 71 | SwampHut4 | Swamp | EM | 50 |  | 2000 .. 6000 | - | -1 .. 1000 | 128 | Swamphut | 150 | 2022 | 5999 | 22 | 1 |
| 72 | SwampHut5 | Swamp | EM | 25 |  | 2000 .. 6000 | - | -1 .. 1000 | 128 | Swamphut | 75 | 2008 | 5975 | 8 | 25 |
| 73 | SwampWell1 | Swamp | EM | 25 |  | 2000 .. 6000 | - | -1 .. 1000 | 1024 | - | 75 | 2125 | 5991 | 125 | 9 |
| 74 | StoneTowerRuins04 | Mountain | EM | 50 |  | 600 .. 10500 | - | 150 .. 1000 | 128 | Mountainruin | 150 | 1432 | 9838 | 832 | 662 |
| 75 | StoneTowerRuins05 | Mountain | EM | 50 |  | 600 .. 10500 | - | 150 .. 1000 | 128 | Mountainruin | 128 | 1096 | 9736 | 496 | 764 |
| 76 | StoneTowerRuins03 | BlackForest | EM | 80 |  | 500 .. 10500 | lower | 2 .. 1000 | 200 | Stonetowerruins | 240 | 648 | 10108 | 148 | 392 |
| 77 | StoneTowerRuins07 | BlackForest | EM | 80 |  | 500 .. 10500 | lower | 2 .. 1000 | 200 | Stonetowerruins | 240 | 633 | 10199 | 133 | 301 |
| 78 | StoneTowerRuins08 | BlackForest | EM | 80 |  | 500 .. 10500 | lower | 2 .. 1000 | 200 | Stonetowerruins | 240 | 590 | 10245 | 90 | 255 |
| 79 | StoneTowerRuins09 | BlackForest | EM | 80 |  | 500 .. 10500 | lower | 2 .. 1000 | 200 | Stonetowerruins | 240 | 509 | 10232 | 9 | 268 |
| 80 | StoneTowerRuins10 | BlackForest | EM | 80 |  | 500 .. 10500 | lower | 2 .. 1000 | 200 | Stonetowerruins | 240 | 549 | 10122 | 49 | 378 |
| 81 | ShipSetting01 | Meadows | EM | 100 |  | 0 .. 5100 | upper | 1 .. 1000 | 128 | - | 300 | 127 | 5068 | 127 | 32 |
| 82 | DrakeNest01 | Mountain | EM | 200 |  | 600 .. 10000 | - | 100 .. 2000 | 100 | - | 600 | 798 | 9976 | 198 | 24 |
| 83 | Waymarker01 | Mountain | EM | 50 |  | 600 .. 10500 | - | 100 .. 1000 | 0 | - | 150 | 1369 | 9856 | 769 | 644 |
| 84 | Waymarker02 | Mountain | EM | 50 |  | 600 .. 10500 | - | 100 .. 1000 | 0 | - | 150 | 1258 | 9972 | 658 | 528 |
| 85 | AbandonedLogCabin02 | Mountain | EM | 33 |  | 600 .. 10500 | - | 100 .. 1000 | 128 | Abandonedcabin | 99 | 1079 | 9566 | 479 | 934 |
| 86 | AbandonedLogCabin03 | Mountain | EM | 33 |  | 600 .. 10500 | - | 100 .. 1000 | 128 | Abandonedcabin | 99 | 1199 | 9997 | 599 | 503 |
| 87 | AbandonedLogCabin04 | Mountain | EM | 50 |  | 600 .. 10500 | - | 100 .. 1000 | 128 | Abandonedcabin | 132 | 823 | 9917 | 223 | 583 |
| 88 | MountainGrave01 | Mountain | EM | 100 |  | 600 .. 10500 | - | 100 .. 1000 | 50 | - | 300 | 1005 | 10027 | 405 | 473 |
| 89 | DrakeLorestone | Mountain | EM | 50 |  | 600 .. 10500 | - | 100 .. 1000 | 128 | Runestones | 150 | 1044 | 9913 | 444 | 587 |
| 90 | MountainWell1 | Mountain | EM | 25 |  | 600 .. 10500 | - | 100 .. 1000 | 256 | - | 37 | 1350 | 9250 | 750 | 1250 |
| 91 | ShipWreck01 | Swamp\|BlackForest\|Plains\|Ocean | EM | 25 |  | 0 .. 10500 | - | -1 .. 1 | 1024 | Shipwreck | 75 | 862 | 9911 | 862 | 589 |
| 92 | ShipWreck02 | Swamp\|BlackForest\|Plains\|Ocean | EM | 25 |  | 0 .. 10500 | - | -1 .. 1 | 1024 | Shipwreck | 75 | 799 | 10192 | 799 | 308 |
| 93 | ShipWreck03 | Swamp\|BlackForest\|Plains\|Ocean | EM | 25 |  | 0 .. 10500 | - | -1 .. 1 | 1024 | Shipwreck | 75 | 849 | 10166 | 849 | 334 |
| 94 | ShipWreck04 | Swamp\|BlackForest\|Plains\|Ocean | EM | 25 |  | 0 .. 10500 | - | -1 .. 1 | 1024 | Shipwreck | 75 | 617 | 10293 | 617 | 207 |
| 95 | Runestone_Meadows | Meadows | EM | 100 |  | 0 .. 5100 | upper | 1 .. 1000 | 128 | Runestones | 300 | 54 | 5033 | 54 | 67 |
| 96 | Runestone_Boars | Meadows | EM | 50 |  | 0 .. 5100 | upper | 1 .. 1000 | 128 | Runestones | 150 | 143 | 5014 | 143 | 86 |
| 97 | Runestone_Swamps | Swamp | EM | 100 |  | 2000 .. 6000 | - | 0 .. 1000 | 128 | Runestones | 300 | 2022 | 5992 | 22 | 8 |
| 98 | Runestone_Mountains | Mountain | EM | 100 |  | 600 .. 10500 | - | 100 .. 1000 | 128 | Runestones | 180 | 791 | 10079 | 191 | 421 |
| 99 | Runestone_BlackForest | BlackForest | EM | 50 |  | 500 .. 10500 | lower | 1 .. 1000 | 128 | Runestones | 150 | 1071 | 10229 | 571 | 271 |
| 100 | Runestone_Plains | Plains | EM | 100 |  | 2900 .. 8000 | lower | 1 .. 1000 | 128 | Runestones | 300 | 2923 | 7987 | 23 | 13 |
| 101 | CombatRuin01 | Meadows | EM | 5 |  | 1500 .. 5100 | upper | 5 .. 1000 | 2000 | - | 15 | 1879 | 4784 | 379 | 316 |
| 102 | BigRockClearing | BlackForest | M | 10 | Y | 1000 .. 10000 | - | 5 .. 1000 | 2000 | - | 30 | 1451 | 9902 | 451 | 98 |
| 103 | TarPit1 | Plains | M | 100 |  | 2900 .. 8000 | lower | 5 .. 60 | 128 | tarpit | 225 | 3085 | 7847 | 185 | 153 |
| 104 | TarPit2 | Plains | M | 16 |  | 2900 .. 8000 | lower | 5 .. 60 | 128 | tarpit | 41 | 3183 | 7567 | 283 | 433 |
| 105 | TarPit3 | Plains | M | 100 |  | 2900 .. 8000 | lower | 5 .. 60 | 128 | tarpit | 292 | 3017 | 7875 | 117 | 125 |
| 106 | MountainCave02 | Mountain | EM | 120 |  | 600 .. 10500 | - | 100 .. 5000 | 200 | mountaincaves | 322 | 977 | 9965 | 377 | 535 |
| 107 | Mistlands_GuardTower1_new | Mistlands | M | 75 |  | 5900 .. 10000 | lower | 2 .. 1000 | 128 | Dvergr | 225 | 6093 | 9827 | 193 | 173 |
| 108 | Mistlands_GuardTower1_ruined_new | Mistlands | M | 80 |  | 5900 .. 10000 | lower | 2 .. 1000 | 128 | Dvergr | 240 | 6044 | 9926 | 144 | 74 |
| 109 | Mistlands_GuardTower1_ruined_new2 | Mistlands | M | 20 |  | 5900 .. 10000 | lower | 2 .. 1000 | 128 | Dvergr | 60 | 6098 | 9546 | 198 | 454 |
| 110 | Mistlands_GuardTower2_new | Mistlands | M | 75 |  | 5900 .. 10000 | lower | 4 .. 1000 | 128 | Dvergr | 225 | 5985 | 9932 | 85 | 68 |
| 111 | Mistlands_GuardTower3_new | Mistlands | M | 50 |  | 5900 .. 10000 | lower | 12 .. 1000 | 128 | Dvergr | 150 | 6030 | 9877 | 130 | 123 |
| 112 | Mistlands_GuardTower3_ruined_new | Mistlands | M | 50 |  | 5900 .. 10000 | lower | 11 .. 1000 | 128 | Dvergr | 150 | 6019 | 9826 | 119 | 174 |
| 113 | Mistlands_Lighthouse1_new | Mistlands | M | 100 |  | 5900 .. 10000 | lower | 1 .. 20 | 128 | Dvergr | 300 | 6001 | 9844 | 101 | 156 |
| 114 | Mistlands_Excavation1 | Mistlands | M | 40 |  | 5900 .. 10000 | lower | 4 .. 100 | 128 | Excavation | 120 | 6061 | 9889 | 161 | 111 |
| 115 | Mistlands_Excavation2 | Mistlands | M | 40 |  | 5900 .. 10000 | lower | 4 .. 100 | 128 | Excavation | 120 | 6015 | 9916 | 115 | 84 |
| 116 | Mistlands_Excavation3 | Mistlands | M | 40 |  | 5900 .. 10000 | lower | 4 .. 100 | 96 | Excavation | 120 | 6011 | 9876 | 111 | 124 |
| 117 | Mistlands_Harbour1 | Mistlands | E | 100 |  | 5900 .. 10000 | lower | -1 .. -0.25 | 64 | Harbour | 300 | 5935 | 9987 | 35 | 13 |
| 118 | Mistlands_Viaduct1 | Mistlands | M | 100 |  | 5900 .. 10000 | lower | -1 .. 15 | 128 | Harbour | 300 | 6062 | 9867 | 162 | 133 |
| 119 | Mistlands_Viaduct2 | Mistlands | M | 150 |  | 5900 .. 10000 | lower | -1 .. 25 | 64 | Harbour | 450 | 5974 | 9925 | 74 | 75 |
| 120 | Mistlands_RockSpire1 | Mistlands | M | 200 |  | 5900 .. 10000 | lower | -10 .. 1000 | 60 | - | 600 | 6031 | 9911 | 131 | 89 |
| 121 | Mistlands_Giant1 | Mistlands | M | 250 |  | 5900 .. 10000 | lower | -1 .. 1000 | 180 | Giant | 674 | 5959 | 9908 | 59 | 92 |
| 122 | Mistlands_Giant2 | Mistlands | M | 85 |  | 5900 .. 10000 | lower | -1 .. 1000 | 256 | Giant | 253 | 6031 | 9884 | 131 | 116 |
| 123 | Mistlands_RoadPost1 | Mistlands | EM | 500 |  | 5900 .. 10000 | lower | 1 .. 1000 | 0 | - | 1500 | 5912 | 9998 | 12 | 2 |
| 124 | Mistlands_StatueGroup1 | Mistlands | EM | 200 |  | 5900 .. 10000 | lower | 2 .. 1000 | 32 | - | 600 | 5934 | 9999 | 34 | 1 |
| 125 | Mistlands_Statue1 | Mistlands | EM | 200 |  | 5900 .. 10000 | lower | 2 .. 1000 | 0 | - | 600 | 5920 | 9963 | 20 | 37 |
| 126 | Mistlands_Statue2 | Mistlands | EM | 200 |  | 5900 .. 10000 | lower | 2 .. 1000 | 0 | - | 600 | 5944 | 9960 | 44 | 40 |
| 127 | Mistlands_Swords1 | Mistlands | M | 33 |  | 5900 .. 10000 | lower | -1 .. 1000 | 256 | GiantArmor | 99 | 6077 | 9891 | 177 | 109 |
| 128 | Mistlands_Swords2 | Mistlands | M | 33 |  | 5900 .. 10000 | lower | -1 .. 1000 | 256 | GiantArmor | 99 | 6026 | 9919 | 126 | 81 |
| 129 | Mistlands_Swords3 | Mistlands | M | 33 |  | 5900 .. 10000 | lower | -1 .. 1000 | 256 | GiantArmor | 99 | 5992 | 9904 | 92 | 96 |
| 130 | Runestone_Mistlands | Mistlands | EM | 50 |  | 5900 .. 10000 | lower | -1 .. 1000 | 128 | Runestones | 150 | 5931 | 9912 | 31 | 88 |
| 131 | FortressRuins | AshLands | EM | 100 |  | 7908 .. 10500 | - | 0 .. 1000 | 0 | - | 300 | 8518 | 9856 | 610 | 644 |
| 132 | CharredRuins1 | AshLands | M | 75 |  | 7908 .. 10500 | - | -5 .. 1000 | 256 | zigg | 225 | 8396 | 10103 | 488 | 397 |
| 133 | Runestone_Ashlands | AshLands | EM | 70 |  | 7908 .. 10500 | - | 0 .. 1000 | 128 | Runestones | 210 | 8518 | 10027 | 610 | 473 |
| 134 | LeviathanLava | AshLands | EM | 100 |  | 7908 .. 10500 | - | 10 .. 1000 | 0 | - | 300 | 8478 | 9770 | 570 | 730 |
| 135 | SulfurArch | AshLands | M | 100 |  | 7908 .. 10500 | - | 10 .. 1000 | 40 | - | 300 | 8453 | 9870 | 546 | 630 |
| 136 | CharredStone_Spawner | AshLands | EM | 300 |  | 7908 .. 10500 | - | 0 .. 1000 | 100 | - | 900 | 8404 | 10056 | 496 | 444 |
| 137 | VoltureNest | AshLands | EM | 350 |  | 7908 .. 10500 | - | 0 .. 1000 | 100 | - | 1050 | 8446 | 10046 | 538 | 454 |
| 138 | CharredTowerRuins1 | AshLands | EM | 30 |  | 7908 .. 10500 | - | 0 .. 1000 | 100 | towerruins | 90 | 8610 | 9909 | 702 | 591 |
| 139 | CharredTowerRuins1_dvergr | AshLands | EM | 30 |  | 7908 .. 10500 | - | 0 .. 1000 | 100 | towerruins | 90 | 8523 | 10013 | 615 | 487 |
| 140 | CharredTowerRuins2 | AshLands | EM | 40 |  | 7908 .. 10500 | - | 0 .. 1000 | 0 | - | 120 | 8459 | 9999 | 552 | 501 |
| 141 | CharredTowerRuins3 | AshLands | EM | 30 |  | 7908 .. 10500 | - | 0 .. 1000 | 500 | - | 90 | 8492 | 10005 | 584 | 495 |
| 142 | AshlandRuins | AshLands | M | 100 |  | 7908 .. 10500 | - | 0 .. 1000 | 16 | - | 300 | 8518 | 9991 | 610 | 509 |
| 143 | MorgenHole1 | AshLands | M | 40 |  | 7908 .. 10500 | - | 0 .. 1000 | 300 | MorgenHole | 120 | 8577 | 9969 | 669 | 531 |
| 144 | MorgenHole2 | AshLands | M | 40 |  | 7908 .. 10500 | - | 0 .. 1000 | 200 | MorgenHole | 120 | 8509 | 10064 | 601 | 436 |
| 145 | MorgenHole3 | AshLands | M | 40 |  | 7908 .. 10500 | - | 0 .. 1000 | 200 | MorgenHole | 119 | 8392 | 9999 | 485 | 501 |
| 146 | CharredRuins2 | AshLands | M | 100 |  | 7908 .. 10500 | - | -5 .. 1000 | 128 | - | 300 | 8348 | 10130 | 440 | 370 |
| 147 | CharredRuins3 | AshLands | M | 100 |  | 7908 .. 10500 | - | -5 .. 1000 | 128 | - | 300 | 8415 | 10136 | 507 | 364 |
| 148 | CharredRuins4 | AshLands | M | 100 |  | 7908 .. 10500 | - | -5 .. 1000 | 128 | - | 300 | 8347 | 10121 | 439 | 379 |
| 149 | TheHole01 | DeepNorth | M | 40 |  | 7908 .. 10500 | - | 2 .. 1000 | 256 | - | 120 | 8262 | 10166 | 355 | 334 |
| 150 | NorthVillage | DeepNorth | M | 135 |  | 7908 .. 10500 | - | 2 .. 1000 | 70 | - | 158 | 8320 | 10107 | 412 | 393 |
| 151 | MorkBorg | DeepNorth | EM | 40 |  | 7908 .. 10500 | - | 30 .. 10000 | 275 | morkborg | 120 | 8416 | 10218 | 508 | 282 |
| 152 | NorthMemorialPlace | DeepNorth | M | 15 |  | 7908 .. 10500 | - | 2 .. 1000 | 400 | memorialplace | 45 | 8326 | 10063 | 419 | 437 |
| 153 | FrozenShip01_DN | DeepNorth | EM | 50 |  | 8000 .. 9750 | - | -15 .. -5 | 64 | FrozenShip | 150 | 8026 | 9734 | 26 | 16 |
| 154 | FrozenShip02_DN | DeepNorth | EM | 50 |  | 8000 .. 9750 | - | -15 .. -5 | 64 | FrozenShip | 150 | 8034 | 9739 | 34 | 11 |
| 155 | FrozenShip03_DN | DeepNorth | EM | 50 |  | 8000 .. 9750 | - | -15 .. -5 | 64 | FrozenShip | 150 | 8040 | 9749 | 40 | 1 |
| 156 | IcePond1 | DeepNorth | M | 40 |  | 7908 .. 10500 | - | 10 .. 200 | 128 | icepond | 120 | 8313 | 10203 | 405 | 297 |
| 157 | DN_hut01 | DeepNorth | M | 40 |  | 7908 .. 10500 | - | 40 .. 1000 | 100 | northvillage | 120 | 8451 | 10116 | 544 | 384 |
| 158 | ShipSetting02 | DeepNorth | EM | 100 |  | 7908 .. 10500 | - | 2 .. 1000 | 64 | shipsetting | 300 | 8199 | 10266 | 291 | 234 |
| 159 | ShipSetting03 | DeepNorth | EM | 50 |  | 7908 .. 10500 | - | 2 .. 1000 | 64 | shipsetting | 150 | 8288 | 10178 | 380 | 322 |
| 160 | LumberCamp | DeepNorth | M | 50 |  | 7908 .. 10500 | - | 4 .. 1000 | 100 | thehole | 150 | 8260 | 10235 | 352 | 265 |
| 161 | ShipWreck01_DN | DeepNorth | EM | 170 |  | 7908 .. 10500 | - | -0.5 .. 0.5 | 0 | - | 503 | 8162 | 10272 | 254 | 228 |
| 162 | ShipWreck02_DN | DeepNorth | EM | 120 |  | 7908 .. 10500 | - | -0.5 .. 1 | 0 | - | 360 | 8182 | 10360 | 274 | 140 |
| 163 | Runestone_DeepNorth | DeepNorth | EM | 70 |  | 7908 .. 10500 | - | 0 .. 1000 | 128 | Runestones | 210 | 8259 | 10293 | 351 | 207 |
| 164 | DN_gammeltrollFrac01 | DeepNorth | EM | 30 |  | 7908 .. 10500 | - | 5 .. 1000 | 0 | - | 90 | 8121 | 10251 | 213 | 249 |
| 165 | DN_gammeltrollFrac02 | DeepNorth | EM | 30 |  | 7908 .. 10500 | - | 10 .. 1000 | 0 | - | 90 | 8395 | 10072 | 488 | 428 |
| 166 | BearCave | BlackForest | M | 50 |  | 500 .. 10500 | lower | 5 .. 1000 | 256 | - | 150 | 742 | 10224 | 242 | 276 |
| 167 | WoodVillage2 | Meadows | EM | 15 |  | 2000 .. 5100 | upper | 1 .. 1000 | 256 | woodvillage | 11 | 2127 | 4985 | 127 | 115 |
| 168 | StoneTowerRuins07_sunk | BlackForest | EM | 10 |  | 500 .. 10500 | lower | -8 .. -2 | 50 | Stonetowerruins_sunk | 30 | 3235 | 8515 | 2735 | 1985 |
| 169 | StoneTowerRuins08_sunk | BlackForest | EM | 10 |  | 500 .. 10500 | lower | -8 .. -2 | 50 | Stonetowerruins_sunk | 24 | 3326 | 8529 | 2826 | 1971 |
| 170 | StoneTowerRuins09_sunk | BlackForest | EM | 10 |  | 500 .. 10500 | lower | -8 .. -2 | 50 | Stonetowerruins_sunk | 18 | 3398 | 7707 | 2898 | 2793 |
| 171 | StoneTowerRuins10_sunk | BlackForest | EM | 10 |  | 500 .. 10500 | lower | -8 .. -2 | 50 | Stonetowerruins_sunk | 9 | 3285 | 7628 | 2785 | 2872 |
| 172 | SwampHut1_1 | Swamp | EM | 50 |  | 2000 .. 6000 | - | -2 .. 1000 | 0 | - | 63 | 2051 | 5993 | 51 | 7 |
| 173 | SwampHut2_1 | Swamp | EM | 50 |  | 2000 .. 6000 | - | -2 .. 1000 | 0 | - | 3 | 2542 | 5488 | 542 | 512 |
| 174 | SwampHut3_1 | Swamp | EM | 50 |  | 2000 .. 6000 | - | -2 .. 1000 | 0 | - | 3 | 2565 | 5792 | 565 | 208 |
| 175 | StoneTowerRuins05_leet | Mountain | EM | 10 |  | 600 .. 10500 | - | 150 .. 1000 | 32 | - | 10 | 2954 | 3296 | 2354 | 7204 |
| 176 | GoblinCamp2_1 | Plains | **0** | 5 |  | 2900 .. 8000 | lower | 2 .. 1000 | 0 | - | 0 | - | - | - | - |
| 177 | GoblinHut01 | Plains | EM | 30 |  | 2900 .. 8000 | lower | 1 .. 1000 | 0 | - | 71 | 2902 | 7995 | 2 | 5 |
| 178 | GoblinHut02 | Plains | EM | 30 |  | 2900 .. 8000 | lower | 1 .. 1000 | 0 | - | 29 | 3259 | 7992 | 359 | 8 |
| 179 | GoblinHut03 | Plains | EM | 20 |  | 2900 .. 8000 | lower | 1 .. 1000 | 0 | - | 3 | 7702 | 7892 | 4802 | 108 |
| 180 | TarPit1_1 | Plains | M | 50 |  | 2900 .. 8000 | lower | 3 .. 1000 | 0 | - | 0 | - | - | - | - |
| 181 | TarPit2_1 | Plains | M | 20 |  | 2900 .. 8000 | lower | 3 .. 1000 | 0 | - | 0 | - | - | - | - |
| 182 | TarPit3_1 | Plains | M | 100 |  | 2900 .. 8000 | lower | 3 .. 1000 | 0 | - | 0 | - | - | - | - |

## 11. What the tool should do with this

1. **Refuse** when a requested radius falls outside the derived hard annulus for the type, when a
   requested count exceeds `m_quantity` (or 1 for a unique type, or the one-per-zone cap for the
   radius), when the type is `GoblinCamp2_1`, or when any range exceeds the 10,500 m water edge.
   Cite the field or the decompiled line in the message.
2. **Refuse as vacuous** when the requested radius is looser than the hard bound, because such a
   query scans all 4.29 billion seeds and returns the first one. Say which field makes it vacuous.
3. **Refuse as constant** for any AshLands area or AshLands nearest-distance criterion: the answer is
   the same 43.03 km2 / 7907.7 m in every seed.
4. **Warn** on everything in section 9, and on a lower pairwise bound of 0 - and label these as
   observations from a small sample, not as limits.
