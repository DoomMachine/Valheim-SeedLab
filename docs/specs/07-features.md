# 07 — Feature set, search engine, throughput, query language, rendering

> Target: Valheim **1.0.15** (Unity 6000.0.75), `worldGenVersion` 2. Every constant below is cited to a
> decompiled member (`Type.Member`) or to a file read on this machine. Anything not settled from
> evidence is marked **Unverified:**. A wrong constant here silently corrupts every result the tool
> ever produces — do not "fix" a value without re-deriving it.
>
> Working name used throughout: `vseed`.

---

## 0. Ground facts this document is built on

### 0.1 The seed space is 2^32, not 8.5×10^17

`World..ctor`: `m_seed = (m_seedName != "") ? m_seedName.GetStableHashCode() : 0`. The hash
(`StringExtensionMethods.GetStableHashCode`, assembly_utils) is two interleaved djb2-xor lanes from
5381, combined `num + num2 * 1566083941` with unchecked 32-bit overflow. Nothing else about the seed
text reaches generation (`WorldGenerator..ctor` reads only `m_seed` and `m_worldGenVersion`).

Verified by running the decompiled hash in Python against every sample in the knowledge base **and**
against the user's own world:

| seed text | hash | source of expectation |
|---|---|---|
| `a` | 372029373 | KB `world-generator.md` §2 |
| `abc` | 1099313834 | KB |
| `HHcLC5acQt` | 298112588 | KB |
| `""` | 371857150 (forced to 0 by the ctor) | KB |
| `MWd8eV6svz` | **-1772362158** | `_main.2.fwl2` of world `asdasdasd` on this machine |

The user's count of **853,058,371,866,181,866** is arithmetically correct for
`sum(62^k, k=1..10)` — computed independently:
`(62^11 − 62)/61 = 52036560683837093826/61 = 853058371866181866`. Exact match.
(59-char alphabet → 519,929,111,116,169,700; 36-char → 3,760,620,109,779,060.)

But that is a count of **strings**, and strings are not worlds:

```
distinct 1..10-char strings over [A-Za-z0-9] : 853,058,371,866,181,866
distinct int32 seeds                         :         4,294,967,296  (2^32)
average strings per world                    :           198,618,130  (1.99e8)
```
*(corrected: `853058371866181866 / 2**32 = 198618129.796`, recomputed here; the previous 198,617,432 was
off by 698.)*

**Design consequence, and the single most important one in this document:** `vseed` searches the
**int32 space**, never the string space. A complete sweep is 4.29×10^9 evaluations, not 8.5×10^17 —
a factor of 1.99×10^8 less work. `valheim.gaming.tools` searches seed *strings*, which is why its
"20,000 seeds per search" is both a cap and a waste.

### 0.2 Any int32 can be turned back into a typeable 10-character seed

The hash is invertible cheaply. For a 10-char string, lane A takes indices 0,2,4,6,8 and lane B takes
1,3,5,7,9 (`StringExtensionMethods.GetStableHashCode`: the loop steps `i += 2`, folds `str[i]` into
`num` and `str[i+1]` into `num2`). Fix lane B (any 5 chars) → `num2` is determined → the required
lane-A terminal value is `num = T − num2·1566083941 (mod 2^32)`. Per-character djb2-xor is invertible
(`h_prev = (h_next ^ c) · 33^-1 mod 2^32`; **`33^-1 mod 2^32 = 0x3E0F83E1`**), so lane A is solved by
meet-in-the-middle: 3 chars forward from 5381 against 2 chars unapplied backwards from the target
(59^2 = 3,481 probes).

> **Corrected (two constants and one probability).** `0xB3CB1B4D` is *not* the inverse of 33:
> `0xB3CB1B4D · 33 mod 2^32 = 758,023,405`. The inverse is `0x3E0F83E1`
> (`0x3E0F83E1 · 33 = 0x8_00000001 ≡ 1 mod 2^32`). The forward table is **not** 59^3 = 205,379 distinct
> states: the chain collides heavily, and the measured number of distinct 3-char lane values is
> **64,726**. Measured distinct lane values after *k* characters (run here, exhaustive):
> `k=1: 59 (1.000 of 59^k) · k=2: 2,048 (0.588) · k=3: 64,726 (0.315) · k=4: 2,016,367 (0.166)`.
> Consequently the per-lane-B-draw success rate is **1.54 %**, not 16.6 % — the 59^5/2^32 = 16.6 %
> figure counts *strings*, and the image of the 5-char lane chain is only ≈ 0.0154·2^32 ≈ 6.6×10^7
> values, about 9 % of 59^5. The mean number of lane-B draws per target is **64.8**, which is why the
> wall-clock figure below (≈ 18 ms/seed in Python) is nevertheless right.

Measured in `scratchpad/probe/seedmath.py`: **12/12 random int32 targets inverted in 0.21 s total**
(≈ 17.5 ms each), producing 10-char seeds in the game's own alphabet
(`World.GenerateSeed` alphabet: `abcdefghijklmnpqrstuvwxyzABCDEFGHIJKLMNPQRSTUVWXYZ023456789`,
59 chars, no `o`/`O`/`1`). Examples that verify (re-checked against the decompiled hash):
`1922895273 → "KYHSGIfTsC"`, `-1499591369 → "GCZ7nJMkiq"`, `2045500108 → "NeyBfxiEfb"`.
Independently re-run by the reviewer with the corrected `0x3E0F83E1`: **300/300 random int32 targets
inverted**, mean 64.8 lane-B draws, 19.8 ms/target (Python, unoptimised), every result re-hashed and
checked equal to its target.

**Unverified:** that *every* int32 is reachable. The counting argument is: the number of *distinct*
lane-B values is the image of the 5-char chain, ≈ 6.6×10^7 (not 59^5 = 7.1×10^8 — repeated draws
repeat `num2`), each independently ~1.54 % likely to land in the lane-A image, giving a failure
probability of order `(1−0.0154)^6.6e7`, i.e. zero for practical purposes; but no exhaustive proof was
run. `vseed` must therefore fall back to 11- and 12-character seeds if inversion ever fails, and must
always print the verification `GetStableHashCode(text) == seed` next to the text it emits.
**Corrected:** an 11- or 12-char text does **not** "strictly enlarge" the 10-char reachable set —
the lane chain is not monotone, so the image of a 6-char lane A is not a superset of the image of a
5-char lane A. What is true is that the *union* over lengths 10–12 is strictly larger than any one of
them, which is all the fallback needs.

### 0.3 The game's own sampling grid — use it as the canonical one

`AltBiomeWorldData` and `Minimap.GenerateWorldMap` both sample a **2048 × 2048 grid at 12 m**.

- `AltBiomeWorldData.MapSpaceToWorldSpace(i) = (i − 1024f) * 12f + 6f`; `AltBiomeWorldData.GenerateBiomePoints`
  builds a `new AltBiomeWorldData(2048)` and, for points with `vector.sqrMagnitude > 110250000f`
  (r = 10500), writes `Biome.Ocean` and `PointHeights = -1000f` instead of calling the generator.
  The grid is also fixed by named constants: `AltBiomeWorldData.c_textureSize = 2048`,
  `c_pixelSize = 12f`, `c_halfWidth = 1024`, `c_halfPixel = 6f`. Inverse:
  `WorldSpaceToMapSpace(x) = (int)((x − 6f) / 12f + 1024f)`.
- `Minimap.GenerateWorldMap`: `wx = (j − m_textureSize/2) * m_pixelSize + m_pixelSize/2`.

The code *defaults* are `m_textureSize = 256`, `m_pixelSize = 64`, but those are serialized prefab
fields; the KB marked the real values **Unverified**. **Now settled from data on this machine**:
`%LOCALAPPDATA%Low\IronGate\Valheim\worlds_local\asdasdasd\cacheMinimap*` (read-only) gunzips to

```
cacheMinimapBiome  : 16,777,216 bytes = 4,194,304 Color32  -> 2048 x 2048
cacheMinimapHeight :  8,388,608 bytes = 4,194,304 half     -> 2048 x 2048
cacheMinimapMask   : 16,777,216 bytes = 4,194,304 Color32  -> 2048 x 2048
cacheMinimapMeta   : 8 bytes = int seed (-1772362158) + int 1
```

and the row through the texture centre has non-`−400` height exactly for column indices 149..1898,
i.e. a radius of 875 px for the 10500 m cutoff → **`m_pixelSize = 12.0`, `m_textureSize = 2048`**, and
the pixel-centre formula is identical to `MapSpaceToWorldSpace`. Centre lands at index 1023.5.
*(Re-verified by the reviewer: gunzip sizes 16,777,216 / 8,388,608 / 16,777,216 and meta
`(-1772362158, 1)`; rows **and** columns 1023 and 1024 all have exactly 1750 non-`−400` samples
spanning indices 149..1898. `−400 = -2f * GetHeightMultiplier()` is the `DUtils.Length(wx,wy) > 10500f`
early return in `WorldGenerator.GetBiomeHeight` — it is **not** the `−1000` that
`AltBiomeWorldData.GenerateBiomePoints` writes; the two out-of-world sentinels differ.)*

So the minimap grid **is** the alt-biome grid. `vseed` adopts it as the canonical grid **G12** and
defines every world-shape and biome-area metric on it (§2). That is not an approximation of the game:
it is the same sampling the game itself uses to decide where locations may be drawn.

### 0.4 The minimap cache is a free, exact validation oracle

For any world the user has opened once, `cacheMinimapBiome` is `GetPixelColor(GetBiome(x,z))` and
`cacheMinimapHeight` is `GetBiomeHeight(biome,x,z)` as half-floats, on G12, for the seed named in
`cacheMinimapMeta`. That is 4.19 M ground-truth samples per world, sitting on disk.
**`vseed verify` must use it as the acceptance test for the port** (§5.6). Half precision gives
±0.015 m at 30 m and ±0.25 m at 400 m — compare with that tolerance, never for equality.

Never write to `worlds_local\` or the Steam Cloud `remote\worlds\` folders; read-only (`rb`) access
only. Steam Cloud syncs both, and the game deletes files it does not expect.

### 0.5 The real biome palette (prefab values, not the code defaults)

Derived from `cacheMinimapBiome` for seed −1772362158 by cross-referencing colour against the
distance bands and height signatures of `WorldGenerator.GetBiome`.

**Corrected — the two quoted bands were wrong.** Re-measured by the reviewer over all 4,194,304 cells
(distance of the cell centre from the origin, and the in-world height range), which is what actually
pins the assignment:

| Color32 | cells | d min | d max | h min | h max | `GetBiome` test that bounds it |
|---|---|---|---|---|---|---|
| (255,255,255) | 1,651,812 | 102 | 15,055 | −398.75 | 457.00 | Mountain ∪ DeepNorth ∪ Ocean (all white) |
| (123,32,32) | 1,361,539 | 7,909 | 17,369 | −94.75 | 85.50 | `IsAshlands` |
| (51,51,51) | 402,651 | **5,900** | **10,000** | −0.62 | 150.00 | `num > 6000 + A ∧ num < 10000`, A ∈ [−100,100] |
| (231,171,120) | 327,479 | **2,902** | **8,000** | 0.00 | 96.62 | `num > 3000 + A ∧ num < 8000` |
| (107,116,63) | 307,927 | 508 | 10,377 | 0.00 | 96.81 | `num > 600 + A ∧ num < 6000`, plus `num > 5000 + A` |
| (146,167,92) | 80,277 | 8 | 5,098 | 3.84 | 95.00 | fall-through, reached only when `num ≤ 5000 + A` |
| (163,114,88) | 62,619 | **2,000** | **6,000** | 24.31 | 33.94 | `num > 2000 ∧ num < maxMarshDistance(=6000)`, no `A` term |

The Swamp band is the decisive one: `GetMarshHeight` ignores `GetBaseHeight` entirely and returns
`0.137f + P·P·0.03f`, then `AddRivers`, then `+P·0.01f + P·0.003f` — i.e. `[0.137, 0.18] × 200`
before rivers carve it, which is the measured 24.31–33.94 m band (mean 29.54 m). *(The earlier text
said "27–31 m … 0.137 ± 0.03": the noise term is `+[0, 0.03]`, not `±0.03`, and the observed band is
24.3–33.9 m because `AddRivers` can only lower it.)* The Meadows band's `d min = 8` is the origin cell
itself (d = 8.49 m), so "the origin pixel is Meadows" is measured, not assumed.

| Biome | Color32 | hex | `Minimap` field | code default (differs) |
|---|---|---|---|---|
| Meadows | (146,167,92) | `#92A75C` | `m_meadowsColor` | (0.45,1,0.43) |
| BlackForest | (107,116,63) | `#6B743F` | `m_blackforestColor` | (0,0.7,0) |
| Swamp | (163,114,88) | `#A37258` | `m_swampColor` | (0.6,0.5,0.5) |
| Plains | (231,171,120) | `#E7AB78` | `m_heathColor` | (1,1,0.2) |
| Mistlands | (51,51,51) | `#333333` | `m_mistlandsColor` | (0.2,0.2,0.2) — **matches** |
| AshLands | (123,32,32) | `#7B2020` | `m_ashlandsColor` | (1,0.2,0.2) |
| Mountain, DeepNorth, Ocean | (255,255,255) | `#FFFFFF` | `m_mountainColor`, `m_deepnorthColor`, `Color.white` | all white |

`Minimap.GetPixelColor` returns `Color.white` for Ocean and for anything unmatched, so **the biome
texture cannot distinguish Mountain / DeepNorth / Ocean**. When validating against it, collapse those
three to one class, or disambiguate with `IsDeepnorth`, `baseHeight > 0.4` and the height texture.

Mask texture (`Minimap.GetMaskColor`), in the code's own order — the water test comes **first** and
overrides every biome rule:

1. `height < 30f` → `new Color(0, 0, Mathf.Clamp01(WorldGenerator.GetAshlandsOceanGradient(wx,wy)), 0)`
2. `Meadows` → `s_forestColor` if `WorldGenerator.InForest(new Vector3(wx,0,wy))`, else `s_noForestColor`
   (`InForest` = `GetForestFactor(pos) < 1.15f`)
3. `Plains` → `s_forestColor` if `GetForestFactor(...) < 0.8f`, else `s_noForestColor`
4. `BlackForest` → always `s_forestColor`
5. `Mistlands` → `new Color(0, 1 − Utils.SmoothStep(1.1f, 1.3f, forestFactor), 0, 0)`
6. `AshLands` → `new Color(0, 0, mask.a, 0)` from `GetAshlandsHeight(wx, wy, out mask, cheap: true)`
7. everything else (Swamp, Mountain, DeepNorth, Ocean above 30 m) → `s_noForestColor`

with `s_forestColor = new Color(1,0,0,0)` and `s_noForestColor = new Color(0,0,0,0)`. *(Added: the
per-biome forest thresholds — 1.15 for Meadows, 0.8 for Plains — were missing and V6 cannot be
implemented without them. Note `Utils.SmoothStep`, not `DUtils.SmoothStep`.)* Measured distinct mask
colours in the real cache: 512, dominated by `(0,0,0,0)` 2,475,151, `(0,0,255,0)` 1,201,819 (water),
`(255,0,0,0)` 186,552 (forest), `(0,255,0,0)` 142,747 (full mist) — all four re-counted by the
reviewer and identical.

### 0.6 The vanilla location list is now known by name

The newest `_main.<n>.db2` of world `asdasdasd` (**currently `_main.3.db2` — the save number
increments on every save, so pin the file by `newest(_main.*.db2)`, never by the literal `_main.2`**)
stores 12,314 location instances under 176 distinct prefab-name hashes, and `location version 32`
(**this settles the KB's Unverified `m_locationVersion`: it is 32, not the code default
`ZoneSystem.m_locationVersion = 1`**). All 176 hashes were matched to prefab names by hashing every
`*.prefab` basename in
`valheim_Data\StreamingAssets\SoftRef\manifest` + `manifest_extended` — **176/176 matched**, with
paths that confirm each one's biome folder (`Assets/world/Locations/<Biome>/<Name>.prefab`).
*(Re-verified by the reviewer against the live save: 12,314 instances, 176 distinct hashes, location
version 32, 112 generated zones, 44 instances with `placed = true` across 17 prefabs;
`probe/loc_names.json` holds 176 entries.)*

The `LogOutput.log` from the user's session on 2026-09-22 adds the real `m_quantity` values for the
types that failed to place, and the total cost of generation (§4.4). It carries two kinds of line:
25 × `Failed to place all <name>, placed A out of B with N tries` and 18 ×
`Location <name> took more than 0.5 seconds to place … (placed A out of B with N)`; their union is
**29 distinct prefabs with a known `m_quantity`** — re-counted by the reviewer, and it is the whole
of the asset-data recovery available from the log.

The run reported **183 location types** processed. That number is `ZoneSystem.m_locationsRun`, which
`GenerateLocationsTimeSliced()` increments once per entry of
`m_locations.OrderByDescending(a => a.m_prioritized)` **after** dropping every entry with
`!m_enable || m_quantity == 0`. So "183" is the count of *enabled, non-zero-quantity* location types,
and the iteration order is prioritized-first, then original list order (LINQ `OrderByDescending` is
stable). Both facts are load-bearing for T5: see §7.5.

**Unverified:** the claim that `ZoneSystem.SetupLocations` logged `3 + 2 + 27 + 4 + 25 + 25 = 86`
entries from six `LocationList` prefabs. The current `BepInEx\LogOutput.log` (42 KB, overwritten by
the 2026-09-22 20:37 session) contains no `Added N locations, … from <scene>` line at all, so the
breakdown cannot be re-checked; and 86 is not reconcilable with 183 without a count for
`AltBiomeList.m_altBiomes[*].m_addLocations`, which `SetupLocations` also appends to `m_locations`
(`ZoneSystem.SetupLocations`). Treat the per-list breakdown as unrecovered; only `183` is evidenced.

Mapping from the reference sites' vocabulary to prefab names (candidate counts `n` are from world
`asdasdasd`, seed −1772362158; they are the *registered candidates after generation*, i.e.
`min(m_quantity, what placement achieved)`):

| Site target | Prefab(s) | n | Notes |
|---|---|---|---|
| Spawn | `StartTemple` | 1 | placed at (70.5, −2.8), d = 71 m. `Game.FindSpawnPoint`: `GetLocationIcon(m_StartLocation, out pos)` then `point = pos + Vector3.up * 2f` — the +2 m is in **Y only**, so the spawn's XZ equals the `StartTemple` position exactly |
| Eikthyr | `Eikthyrnir` | 3 | Meadows |
| The Elder | `GDKing` | 4 | BlackForest |
| Bonemass | `Bonemass` | 5 | Swamp |
| Moder | `Dragonqueen` | 3 | Mountains |
| Yagluth | `GoblinKing` | 4 | Plains |
| The Queen | `Mistlands_DvergrBossEntrance1` | 5 | Mistlands |
| Fader | `FaderLocation` | 3 | Ashlands |
| (Deep North boss room) | `DN_Bossroom` | 3 | not in the site list; expose it anyway |
| Haldor | `Vendor_BlackForest` | 10 | |
| Hildir | `Hildir_camp` | 10 | |
| The Bog Witch | `BogWitch_Camp` | 10 | `Assets/world/Props/BogWitchHut/` |
| Hildir Trials | `Hildir_cave`, `Hildir_crypt`, `Hildir_plainsfortress` | 3 + 3 + 3 | |
| Burial Chambers | `Crypt2`, `Crypt3`, `Crypt4` | 200 + 200 + 175 | `m_quantity(Crypt4) = 200` (log) |
| Troll Caves | `TrollCave02` | 200 | |
| Sunken Crypts | `SunkenCrypt4` | 175 | |
| Frost Caves | `MountainCave02` | 120 | `m_quantity = 120` (log: `Failed to place all MountainCave02, placed 82 out of 120`) |
| Infested Mines | `Mistlands_DvergrTownEntrance1`, `…2` | 120 + 120 | |
| Fuling Villages | `GoblinCamp2` | 190 | `m_quantity = 200` (log) |
| Surtling Geysers | `FireHole` | 75 | `Assets/world/Locations/Misc/` |
| Tar Pits | `TarPit1`, `TarPit2`, `TarPit3` | 60 + 16 + 100 | `m_quantity` = 100 / **16** / 100 (log) |
| Charred Fortresses | `CharredFortress` | 20 | |
| "Places of Mystery" (Dyrnwyn) | `PlaceofMystery1/2/3` | 1 + 1 + 1 | all Ashlands, d ≈ 9.1–9.3 km |
| Runestones | `Runestone_*` (11 types) | 50–100 each | |
| Ore/resources/scenery/landmarks | the remaining ~140 types | | full list in `probe/loc_names.json` |

Two facts here change what the tool may honestly claim:

1. **Boss altars are not unique.** `Eikthyrnir` has 3 registered instances in the save and one of them
   is `placed=True`; if `m_unique` were set, `ZoneSystem.PlaceLocations → RemoveUnplacedLocations`
   would have deleted the other two when the placed one generated. So multiple altars of the same
   boss coexist, and "distance to Eikthyr" means "distance to the nearest of n altars".
2. **Trader placement is not fully seed-determined.** `Vendor_BlackForest`, `Hildir_camp` and
   `BogWitch_Camp` each register **10** candidates with none placed. For `m_unique` types the survivor
   is whichever candidate zone is generated first, which depends on exploration order
   (KB `zones-locations-vegetation.md` §3.5). **Unverified:** which of the 183 types carry `m_unique`
   — that is serialized asset data and must come from the dumper plugin
   (`ZoneSystem.m_locations[i].m_unique`).
   Until then `vseed` reports the **candidate set** and its min/median distance, labels the metric
   `candidate-min`, and never prints a single "the trader is at X".

### 0.7 Alt-biomes are live in vanilla

`LogOutput.log`: `Loading: Placed 0/1-2 of 'Fortress Mountain' altbiome. (Valid, sectors: 0, combos: 2)`.
This settles the KB's "Unverified: whether vanilla ships any enabled AltBiomes" — `AltBiomeList.m_altBiomes`
is non-empty in vanilla and contains at least `Fortress Mountain` with `m_minAmountSpawned = 1`,
`m_maxAmountSpawned = 2`. **Read the line precisely:** it is the `ZLog.LogWarning` branch of
`AltBiomeWorldData.GenerateAltBiomes`, taken when `altBiome2.Sectors.Count < m_minAmountSpawned`, and
it reports `ValidPlacementSectors = 0` — so in world `testworldclaude` that alt-biome was placed in
**zero** sectors. Alt-biomes that *do* place are logged with `ZLog.DevLog`, which is debug-build only,
so the log cannot tell us whether any other alt-biome placed.

`AltBiomeWorldData.GenerateAltBiomes` calls `Random.InitState(worldSeed + 920)` once, then per
(biome, valid-alt) pair `Random.InitState((int)(biome.Key + altName.GetStableHashCode() + worldSeed))`,
`biome.Value.Sectors.Shuffle()`, and for each sector draws `Random.Range(0f, 1f)` and adds the modifier
when `Sectors.Count < m_minAmountSpawned || m_chance >= r`, capped at `m_maxAmountSpawned`. Note the
`+ 920` state is overwritten by the first per-pair `InitState` and therefore never consumed; and
`GenerateAltBiomes` does **not** save/restore `Random.state`.

**Unverified:** that the `_1` / `_leet` location variants in the log (`SwampHut1_1`, `TarPit3_1`,
`GoblinCamp2_1`, `StoneTowerRuins05_leet`) are `AltBiome.m_addLocations` entries. It is consistent for
`TarPit1_1 / TarPit2_1 / TarPit3_1 / GoblinCamp2_1 / StoneTowerRuins05_leet`, which all logged
`placed 0 out of N` — the outcome `GenerateLocationsTimeSliced` produces when `location.AltBiomeParent != null`
and no `BiomeSector` carries that alt-biome. It is *not* consistent with `SwampHut1_1` (33/50),
`SwampHut2_1` (2/50) and `SwampHut3_1` (2/50), which did place; those either are not alt-biome
locations or belong to an alt-biome that did place. `m_addLocations` is serialized asset data —
dumper input.

**A location tier that ignores alt-biomes can disagree with the game** (`AltBiomeParent` gates one
filter and `m_blockLocationNames` another, both inside the per-candidate loop). It must therefore be
implemented, and it needs the flood-filled `BiomeSector` graph, i.e. the full G12 biome field.

---

## 1. Target-by-target: what we compute, which stage it needs, what it costs

### 1.1 Generation stages, in dependency order

| Stage | Produces | Needs | Cost (1 thread, measured/derived §4) |
|---|---|---|---|
| **S0 Offsets** | `m_offset0..4`, `m_riverSeed`, `m_streamSeed` | port of `UnityEngine.Random`: 7 draws after `InitState(m_world.m_seed)`, **in this order** — `offset0, offset1, offset2, offset3, riverSeed, streamSeed, offset4` (`WorldGenerator..ctor`; `offset4` is drawn *last*, after the two seeds, not fifth) | ~10 ns |
| **S1 Biome field** | `GetBiome(x,z)` anywhere | S0 (off0,1,2,4) + Perlin. **Does not need rivers** | 134 ns/point |
| **S2 Pregeneration** | lakes → rivers → streams (`m_riverPoints`) | S0 (riverSeed, streamSeed, off0..3) + an exact `UnityEngine.Random` | ~0.2 s/seed |
| **S3 Height field** | `GetHeight`/`GetBiomeHeight` anywhere | S1 + S2 | 235–370 ns/point |
| **S3′ Height, rivers off** | upper bound on S3 (§3.4) | S1 only | 235 ns/point |
| **S4 Biome-point grid** | G12 biome + height + `AllPoints[b]`, `AllPointsAboveSeaLevel[b]`, `BiomeSector` flood fill, alt-biome assignment | S1+S3 over 2048² | 1.12 s/seed |
| **S5 Location placement** | all 183 types' instance sets | S4 + per-type seeded RNG + all 11 filters | est. 3–10 s/seed (game: 33.4 s on Mono) |

**Added, and it changes what has to be ported.** `WorldGenerator..ctor` sets
`m_noiseGen.SetSeed(0)` **unconditionally**, after the `new FastNoise(m_world.m_seed)` that only runs
on the very first construction (`if (m_noiseGen == null)`), and `m_noiseGen` is `static`. So the
`FastNoise` **field itself is the same in every world** — seed 0, `NoiseType.Cellular`,
`CellularDistanceFunction.Euclidean`, `CellularReturnType.Distance`, `SetFractalOctaves(2)`. It is not
a per-seed generator and must not be ported as one. (The *sample coordinates* still move with the
seed: `GetAshlandsHeight` adds `100000f + m_offset3` to both axes before the `GetCellular` /
`GetSimplexFractal` calls, and its `GetBaseHeight` term carries `m_offset0` / `m_offset1`. So the
field can be built once for all seeds, but a value at a fixed *world* point still differs per seed.)

`GetBiome` never consults rivers **at its default arguments** (`GetBiome(float wx, float wy,
float oceanLevel = 0.02f, bool waterAlwaysOcean = false)` calls only `GetBaseHeight`, `IsAshlands`,
`IsDeepnorth`, `WorldAngle` and four mask Perlins) — this is what makes the cheap tiers possible.
**Corrected:** the claim is *conditional*. The first statement in the body is
`if (waterAlwaysOcean && GetHeight(wx, wy) <= oceanLevel) return Ocean;`, and `GetHeight` does run
`AddRivers`. Every path this document relies on passes the default `false` — `Minimap.GenerateWorldMap`
(`worldGenerator.GetBiome(wx, wy)`), `AltBiomeWorldData.GenerateBiomePoints`
(`WorldGenerator.instance.GetBiome(vector.x, vector.y)`) and the candidate test in
`ZoneSystem.GenerateLocationsTimeSliced` (`GetBiome(randomPointInZone)`) — so S1, T1, T2, T2b and the
V4 oracle are all river-free. A port must still implement the `waterAlwaysOcean` branch, or refuse to
expose it.

`GetBiomeHeight` calls `GetBiomeSector(wx,wy)` and **never uses the result** in this build (the local
`biomeSector` is assigned and not read again); the port omits it.

Two terms of `GetBiomeHeight` that the rest of this document depends on and did not state:
`float num2 = preGeneration ? GetHeightMultiplier() : (float)(GetHeightMultiplier() * CreateAshlandsGap(wx,wy) * CreateDeepNorthGap(wx,wy))`,
and every branch returns `biomeFn(...) * num2 + 0f`. `GetHeightMultiplier() = 200f`; both gaps are
`MathfLikeSmoothStep(0, 1, Clamp01(|d| / 400))` ∈ **[0, 1]**, so heights are pinched to 0 in a ±400 m
band around the Ashlands and Deep North rings — visible on the map as a water moat, and the reason
`num2 ≥ 0` (which §3.2.5's proof needs). Before the switch, `if (DUtils.Length(wx,wy) > 10500f)
return -2f * GetHeightMultiplier();` → exactly −400.

### 1.2 Mapping every goal type

Distances are from the **world centre (0,0)** by `DUtils.Length(x,z) = (float)sqrt(x²+z²)`, matching
`valheim.gaming.tools`. `vseed` additionally offers `from: spawn` (the `StartTemple` point, which is
where the player actually starts — `Game.FindSpawnPoint`), since for this user that is the number that
matters; the query declares which.

| Goal target | How `vseed` computes it | Stage | Marginal cost/seed |
|---|---|---|---|
| **Bosses** (Eikthyr, Elder, Bonemass, Moder, Yagluth, Queen, Fader) | the instance set of the matching prefab from S5; metric = min/median/max `|p|`, count within R | S5 | full S5 |
| — cheap bound | `dist ≥ min over G12 cells of the type's reachable biomes of (|p| − 90.5 m)` (§3.3) | S4-lite | 0.012–0.30 s |
| **Traders** (Haldor, Hildir, Bog Witch) | candidate set of `Vendor_BlackForest` / `Hildir_camp` / `BogWitch_Camp`; report `candidate-min`, `candidate-count-within-R`, and the caveat of §0.6.2 | S5 | full S5 |
| **Dungeons** (Burial Chambers, Troll Caves, Sunken Crypts, Frost Caves, Infested Mines, Hildir Trials) | union of the prefab sets in the §0.6 table; `count within R`, `nearest` | S5 | full S5 |
| **World features** (Fuling Villages, Surtling Geysers, Tar Pits, Charred Fortresses) | same, prefabs `GoblinCamp2` / `FireHole` / `TarPit1..3` / `CharredFortress` | S5 | full S5 |
| **Biome present / area** | cells of that biome on G_r (§2.1) × r² | S1 | see §4.3 |
| **Biome nearest to centre** | `min |p|` over that biome's cells on G_r (± r/√2) | S1 | see §4.3 |
| **Biome within radius R** | count of that biome's cells inside the disc of radius R | S1, region-restricted | (R/10500)² × full cost |
| **Island count** | 4-connected components of land on G_r with area ≥ `A_min` (§2.2) | S3 (S3′ for a bound) | S2 + S3 over G_r |
| **Spawn island size** | area of the component containing `StartTemple`; fallbacks in §2.3 | S3 + S5(StartTemple only) | S2+S3+partial S5 |
| **Largest island size** | max component area | S3 | S2+S3 |
| **Ore deposits / resources / hazards / scenery / landmarks** (bobmitch categories) | the remaining ~140 location prefabs, grouped by the folder in their asset path | S5 | full S5 |
| **"Highest peaks"** (bobmitch) | top-k local maxima of the S3 height field on G_r, non-max-suppressed at 500 m | S3 | S2+S3 |
| **Lava area** (Ashlands) | `mask.a > 0.6` from `GetAshlandsHeight(..., cheap: false)` — exactly `ZoneSystem.IsLavaPreHeightmap`, which is `GetBiome(...) == AshLands` **and** `GetBiomeHeight(AshLands, x, z, out mask)` then `mask.a > lavaValue` (default `0.6f`) | S3 (Ashlands only) | Ashlands cells only |
| **Rivers / lakes / streams** | `GetRivers()` / `GetLakes()` / `GetStreams()` equivalents straight out of S2 | S2 | free once S2 ran |
| **Forest cover** | `WorldGenerator.InForest`/`GetForestFactor` — **static, no seed input**, identical in every world; usable as a static mask, never as a seed discriminator | — | free |

Two traps in that table, both verified against the source:

- **`mask.a` is not the same function in the two places it appears.** `Minimap.GetMaskColor` asks for
  `GetAshlandsHeight(wx, wy, out mask, cheap: true)` — 2 cellular octaves and 2 lava octaves — while
  `ZoneSystem.IsLavaPreHeightmap` goes through `GetBiomeHeight`, i.e. `cheap: false` — 5 and 3 octaves.
  A lava-area metric computed from `cacheMinimapMask` is therefore **not** the game's lava test and
  must not be validated against V6, and vice versa. Pick `cheap: false` for the metric.
- **`GetStreams()` returns only half the streams.** `WorldGenerator.Pregenerate` runs
  `m_streams = PlaceStreams(isDN: false); PlaceStreams(isDN: true);` — the Deep North pass is rendered
  into `m_riverPoints` (so it *does* carve terrain) but its `List<River>` is discarded. A renderer that
  draws `GetStreams()` centre-lines will silently omit every Deep North stream; keep both lists.

Things the sites offer that we deliberately do **not** promise:

- **Dungeon interiors.** `DungeonGenerator.GetSeed` uses the generator's *world* position, which
  includes the **unseeded** location rotation (`Random.Range(0,16) * 22.5°` from the ambient global
  RNG) whenever the generator has a non-zero XZ offset. Layouts are then frozen in the ZDO. Interiors
  are not predictable offline. State this in the UI rather than guessing.
- **Vegetation / ore nodes.** Seeded per (zone, prefab) but accepted by physics raycasts against
  whatever colliders exist at that moment (`ZoneSystem.PlaceVegetation`). Seed-driven, not
  bit-reproducible. `vseed` may *estimate* copper/tin density from biome area, clearly labelled as an
  estimate, and must not print node coordinates.
- **Which unique-location candidate wins.** §0.6.2.

---

## 2. Our definitions (stable, stated once, never silently changed)

These are ours, so they are normative. The query file records the definition version
(`defs: 1`) and the grid; a results file from a different `defs` is not comparable.

### 2.1 Sampling grids

`G_r` for a spacing `r` metres: `N = 2 * ceil(10500/r)` samples per axis,
`x_i = (i − N/2) * r + r/2`, same for `z`. For `r = 12` this reproduces
`AltBiomeWorldData.MapSpaceToWorldSpace` exactly with `N = 2048` (because `2*ceil(10500/12) = 1750`,
so `vseed` special-cases `r = 12 → N = 2048`, keeping the game's own indexing and its
out-of-radius rule `x²+z² > 110250000 → Ocean / −1000`).

Default `r = 12` (**G12**) for everything definitional. Coarser grids exist only as *cost knobs*, and
using one changes the definition, so it is recorded in every result record. **A result produced on
G96 is not "approximately" a G12 result; it is a different, equally well-defined measurement.**

### 2.2 Land, islands, area

- **land cell**: `dist ≤ 10500` and `GetBiomeHeight(GetBiome(x,z), x, z) >= 30.0f`.
  30 is the water level — `ZoneSystem.m_waterLevel` code default 30, and both
  `Minimap.GetMaskColor` (`height < 30f` → water) and `AltBiomeWorldData.tryFill` (`>= 30f` → above
  sea level) hard-code it.
- **island**: a **4-connected** component of land cells (never 8-connected: diagonal contact bridges
  two islands across a one-cell channel).
- **island area** = `cellCount * r²` m².
- **island count** = number of components with area ≥ `A_min`, default `A_min = 10,000 m²` (1 ha).
- **largest island** = max component area.

**Why the threshold, with numbers.** Union-find over the real G12 height cache of seed −1772362158
(`scratchpad/probe/islands.py`), decimating the same field to coarser grids. **All five rows
reproduced exactly by the reviewer** on an independent re-implementation (4-connected union-find,
`h >= 30.0`, `A_min = 10,000 m²`, subsampling every k-th index from index 0).

**Caveat on what the table measures.** The decimation takes G12 indices `0, k, 2k, …`, whose world
coordinates are `≡ 6 (mod 12k)`, while §2.1 defines `G_r` at `(i − N/2)·r + r/2` — for `r = 24` that
is `…, −12, 12, 36, …`, a *different* point set. So the table is "G12 decimated by k", not "`G_r` as
defined above". It is the right evidence for the resolution-sensitivity conclusion (any half-cell
shift would show the same spread), but a `vseed` run at `grid: 24` will not reproduce these exact
numbers. **Unverified:** the same table evaluated on the §2.1 `G_r` points.

| grid | components | components ≥ 1 ha | total land | largest island |
|---|---|---|---|---|
| 12 m | 9,866 | **227** | 117.7 km² | 5.77 km² |
| 24 m | 4,927 | **229** | 117.7 km² | 6.23 km² |
| 48 m | 1,727 | **240** | 117.8 km² | 5.76 km² |
| 96 m | 502 | **234** | 117.7 km² | 5.70 km² |
| 192 m | 217 | **217** | 118.3 km² | 7.00 km² |

The raw component count varies by **45×** across a 16× resolution change and is therefore meaningless
as a published metric. The ≥ 1 ha count varies by **±5%** and total land area by **±0.5%**. Largest
island varies **−1% to +21%** — coarsening never destroys land in bulk but does *bridge* narrow
channels, so it is not monotone; 192 m merged two islands into a 7.00 km² one. **Rule: report island
count and area only with `A_min`; use `r ≤ 24 m` for any largest-island criterion and state ±10%.**

### 2.3 Spawn island

In order:
1. the component containing the **`StartTemple` instance** (requires S5 restricted to that one type —
   `StartTemple` is `m_centerFirst`, `m_quantity = 1`, so it is the cheapest possible partial S5);
2. if S5 has not run, or `StartTemple` falls on a water cell after snapping, the land component whose
   nearest cell centre is closest to (0,0);
3. if that nearest land cell is more than **500 m** from the origin, report
   `spawn_island: none (spawn area is at sea)` rather than a number.

Case 2/3 is not hypothetical: for seed −1772362158 the **origin cell itself is water** (G12 height at
(6,6) = 23.09 m) while `StartTemple` sits at (70.5, −2.8). A definition anchored naively at (0,0)
would have returned "no spawn island" for a perfectly ordinary world.

### 2.4 Other derived metrics

- **coastline length** ≈ (number of land/water 4-adjacency edges) × r. Resolution-dependent
  (a fractal); report with the grid and never compare across grids.
- **biome mix of an island**: per-biome cell counts inside the component.
- **progression radius** `P(q)`: the radius containing a fraction `q` of some target set — e.g. the
  radius containing the nearest altar of every boss (`compact progression`).
- **peak**: a G_r cell whose height is the max within a 500 m radius; reported with height and
  biome.

---

## 3. The search engine

### 3.1 Non-negotiable correctness rule

> **A prefilter may reject a seed only when it evaluates the criterion's own definition exactly, or
> when it computes a proven bound in the safe direction. Evaluating a G12-defined criterion on a
> coarser grid and rejecting on the result is a correctness bug, not an optimisation.**

Every prefilter below is classified `EXACT`, `SOUND-BOUND` (with the proof) or `HEURISTIC`.
`HEURISTIC` filters are off by default, are only reachable via `--approx`, and every result file
produced with one carries `approx: true` plus the measured false-negative rate from a calibration
sample. This is the whole reason to build the tool locally; do not trade it away for throughput.

### 3.2 Tiers

```
T0  offsets + static query analysis      ~10 ns            EXACT (see 3.2.1)
T1  sparse probe, witnesses only         ~5 us             ACCEPT-ONLY (never rejects)
T2  biome field on G_r, region-limited   0.8 ms .. 0.30 s  EXACT for biome metrics on G_r
T2b location-zone bound (reuses T2's G12) + ~0             SOUND-BOUND for location distances
T3  = T2 + heights with rivers OFF       +0.82 s at G12    SOUND-BOUND for land/island metrics
T4  = T3 + S2 + river lookups            +0.18 s over T3   EXACT   (1.30 s/seed total at G12)
T5  = S4 + full location placement       +3..10 s over T4  EXACT
```

#### 3.2.1 T0 — static query analysis (free, and catches real mistakes)

Before any seed is touched, check each goal against the hard geometry of
`WorldGenerator.GetBiome`. `A = WorldAngle(x,z)*100 ∈ [−100, 100]`, so:

| Biome | possible `dist` range | source |
|---|---|---|
| Mistlands | (5900, 10000) | test 7: `dist > 6000 + A`, `dist < 10000` |
| Plains | (2900, 8000) | test 8 |
| BlackForest | (500, 6000) ∪ (4900, ∞) | tests 9, 10 |
| Swamp | (2000, 6000) | test 6, `maxMarshDistance = 6000` on v2 |
| Meadows | [0, 5100) | test 11 is only reached when `dist ≤ 5000 + A` |
| Mountain | (600, …) | `GetBaseHeight` caps base at 0.38 below `m_minMountainDistance − 400 = 600 m` |
| AshLands | `Length(x, z−4000) > 12000 + A` → at x=0, z ≲ −7900 | `IsAshlands`, `ashlandsMinDistance = 12000`, `ashlandsYOffset = −4000` |
| DeepNorth | `\|(x, z+4000)\| > 12000 + A` → at x=0, z ≳ +7900 | `IsDeepnorth` |

So "a Mistlands within 4 km of centre", "Swamp beyond 6 km", "a Mountain within 600 m" are
**impossible for every seed**, and the tool says so instantly instead of burning a week.

**Every row of that table is `worldGenVersion`-dependent** and the query file lets the user pick
0 | 1 | 2, so T0 must read the version before it rejects anything (`WorldGenerator.VersionSetup`):
`version <= 0` sets `m_minMountainDistance = 1500f`, which moves the Mountain floor from 600 m to
**1100 m**; `version <= 1` sets `minDarklandNoise = 0.5f` (Mistlands rarer, same distance band) and
`maxMarshDistance = 8000f`, which moves the Swamp band from (2000, 6000) to **(2000, 8000)**.
Hard-coding the v2 numbers into T0 would make it reject satisfiable v0/v1 queries.
Same for location goals once the dumper has `m_minDistance / m_maxDistance / m_biome`: e.g. any goal
asking for Fader nearer than the Ashlands boundary is unsatisfiable.

T0 also emits, per goal, the minimum tier needed, and the estimated wall-clock for the requested seed
budget, before the run starts.

#### 3.2.2 T1 — probe (accept-only)

A fixed 256- or 1024-point Vogel spiral inside r = 10000 (`r_k = 10000·sqrt((k+0.5)/n)`,
`θ_k = k·2.39996`), evaluated with `GetBiome`. Measured **209,718 seeds/s on 16 threads** for 256
points. It may only:
- **accept** a "biome present" / "≥ N cells" goal early when the probe already contains a witness
  (existence is monotone: a witness on any subset is a witness on G12 — sound);
- feed the scheduler's ordering heuristic (which seeds to promote first within a block);
- never reject anything.

Its real job is to make a full-space *scan* possible at all: a T1 sweep of the entire 2^32 space takes
**5.7 hours**, and produces a bitmap of "seeds that already witnessed the hard existence goals", which
subsequent tiers use as their candidate list.

#### 3.2.3 T2 — the biome field (the workhorse)

Evaluate `GetBiome` on G_r, restricted to the union of the query's regions of interest. For a goal
bounded by radius R, only the disc of radius `R + 90.5 m` matters (§3.3), which costs
`((R+90.5)/10500)²` of the full map. `EXACT` for every biome-area / biome-distance metric defined on
G_r, by construction (§2.1).

Reuse: one G_r evaluation serves *all* biome goals and (at r = 12) all T2b location bounds in the
query. Cache it per seed in the worker's thread-local buffer.

#### 3.2.4 T2b — the location-zone bound (`SOUND-BOUND`, and the most valuable filter here)

**Claim (corrected — the original was stated unconditionally and is not).** For any location type `L`
that is **not** `m_centerFirst` **and whose `maxRadius = max(m_exteriorRadius, m_interiorRadius)
is ≤ 64 m**, every instance of `L` lies within **64 m in L∞** (≤ 90.51 m Euclidean) of a **G12 grid
point whose biome is in `L`'s reachable biome set** (defined below, and it is *not* `m_biome`).

**Proof, from `ZoneSystem.GenerateLocationsTimeSliced(ZoneLocation,…)`:**
1. The candidate zone is `ZoneSystem.GetZone(AltBiomeWorldData.MapSpaceToWorldSpace(p))` where `p` is
   drawn by `GetRandomPointByBiomes(m_biome)` (when `m_minAltitude < 0f`) or
   `GetRandomPointByBiomesAboveSeaLevel(m_biome)` (otherwise) — i.e. `p` **is** a G12 grid point of a
   biome returned by `RandomBiomeFromBiomes(m_biome)`.
2. `GetZone(v) = (FloorToInt((v.x+32)/64), FloorToInt((v.z+32)/64))`, and `GetZonePos(id) = id*64` is
   the zone **centre**, so `|p − zoneCentre|∞ ≤ 32`.
3. `GetRandomPointInZone(zone, r)` returns `GetZonePos(zone) + (Random.Range(−32f+r, 32f−r), 0,
   Random.Range(−32f+r, 32f−r))`. **Corrected:** the interval spanned by the two arguments is
   `[−|32−r|, +|32−r|]`, so per axis `|offset| ≤ |32 − r|`, which is ≤ 32 **only when `r ≤ 64`**.
   For a location with `maxRadius = 100` the arguments are `Range(68, −68)` and the offset can reach
   ±68. This step additionally assumes `UnityEngine.Random.Range(min, max)` with `min > max` returns a
   value inside `[min(a,b), max(a,b)]` — **Unverified**, native `extern`, dumper input (§7.2). If it
   returns something outside that span there is **no** bound at all.
4. Triangle inequality in L∞: `|point − p|∞ ≤ 32 + |32 − r|`, hence Euclidean
   `≤ √2·(32 + |32 − r|)`. For `r ≤ 64` that is `64` and `90.51 m`. ∎

**General form the implementation must use:** `Δ∞(L) = 32 + |32 − maxRadius(L)|`,
`Δ(L) = √2 · Δ∞(L)`. The 90.51 m constant below is `Δ(L)` **only** for `maxRadius(L) ≤ 64`; for any
type with a larger radius the bound must widen to `√2 · maxRadius(L)`, and if `maxRadius(L)` is
unknown (it is asset data — §7.3) the type gets **no bound**, exactly like a `m_centerFirst` type.

**Consequences, all sound:**
- `minDist(L) ≥ max(m_minDistance, m_minDistanceFromCenter, min over S_L of (|p| − 90.51))`
- `maxDist(L) ≤ min(m_maxDistance, m_maxDistanceFromCenter, max over S_L of (|p| + 90.51))`

where `S_L` is the set of G12 points whose biome is in `L`'s reachable biome set, and `90.51` is
`Δ(L)` from the corrected step 4. Using the **full** `AllPoints` list (ignoring the above-sea-level
restriction) yields a **superset** of the true `S_L`, which only loosens the bounds — still sound, and
it removes the dependency on S2/S3 entirely. So **T2b needs only the G12 biome field, not heights and
not rivers.**

Therefore:
- goal "`L` within R of centre" → **reject** when `minDist(L) > R`. Sound.
- goal "`L` at least D from centre" → **accept** when `minDist(L) ≥ D`. Sound.
- goal "at least k of `L` within R" → reject when the number of G12 cells of `L`'s reachable biomes
  inside radius `R + Δ(L)` is `< k`. Sound, because each instance needs a distinct zone (at most one
  location per zone: `ZoneSystem.RegisterLocation` returns early on
  `m_locationInstances.ContainsKey(zone)`, and the candidate loop skips a zone already in the
  dictionary), and distinct zones ≤ distinct cells.

  > **Corrected — the "tighter" form was unsound.** The document previously offered
  > `ceil(cells/29) < k` as "tighter but still sound". It is not sound: `ceil(cells/M)` is a **lower**
  > bound on the number of distinct zones the cells can occupy, and rejection needs an **upper**
  > bound. 100 cells could lie in 100 different zones; `ceil(100/29) = 4 < k = 5` would reject a seed
  > that can hold 5 instances. Use `cells < k` only. (The constant was wrong too: a 64 m zone spans
  > `floor(64/12) + 1 = 6` G12 points per axis, so **at most 36** points per zone, not
  > `(64/12)² ≈ 29` — 29 is the average, not the maximum.)

**It does not apply to `m_centerFirst` types.** For those the zone is `GetRandomZone(maxRange)`, which
draws `new Vector2s(Random.Range(-num, num), Random.Range(-num, num))` with `num = (int)range / 64`,
retrying until `GetZonePos(...).magnitude < 10000f` — it ignores the biome grid entirely. Note
`maxRange` starts at `location.m_minDistance` and is incremented by 1 on **every** attempt, so a
centerFirst type spirals outward from the origin; with `m_minDistance == 0` the first attempt is
forced to zone (0,0). `StartTemple` is certainly one.
**Unverified:** which other types set `m_centerFirst` — dumper input. Until known, treat every type as
centerFirst for safety *unless the dumper says otherwise*, i.e. T2b is enabled per type only when the
dumped flag is `false`.

> **Corrected — this is the error that would have made T2b reject matching seeds.**
>
> `AltBiomeWorldData.RandomBiomeFromBiomes` is buggy, and the previous paragraph drew the wrong
> conclusion from it: it claimed the reachable set is a *subset* of `m_biome`, so that using the full
> mask is conservative. **It is not a subset.** The function counts 9 bits into `num`, draws
> `num2 = Random.Range(0, num − 1)` (max-exclusive → `num2 ∈ [0, num−2]`), then walks an 8-entry
> chain, decrementing `num2` at each *matching* entry:
>
> ```
> Meadows→Meadows, Swamp→Swamp, Mountain→Mountain, BlackForest→BlackForest,
> Plains→BlackForest,  AshLands→AshLands, DeepNorth→DeepNorth, Meadows→Ocean
> ```
> with `return Heightmap.Biome.Mistlands;` as the fall-through. Mistlands is counted in `num` but has
> no chain entry; Meadows is tested twice; the fifth entry tests the **Plains** bit and returns
> **BlackForest**; the eighth tests the **Meadows** bit and returns **Ocean**.
>
> Exhaustive enumeration over all 511 masks (run here, `Heightmap.Biome` = Meadows 1, Swamp 2,
> Mountain 4, BlackForest 8, Plains 0x10, AshLands 0x20, DeepNorth 0x40, Ocean 0x100, Mistlands 0x200)
> gives the exact result:
>
> - **120 masks can return a biome that is not in `m_biome`, and in every one of those cases the
>   escapee is `BlackForest`, via the Plains entry.**
> - Ocean never escapes: the eighth entry is reachable only when Meadows **and** Ocean **and**
>   Mistlands are all set, so Ocean is in the mask whenever it can be returned.
> - Mistlands never escapes: the fall-through needs `chainMatches ≤ num − 2`, i.e. Ocean and Mistlands
>   both set and Meadows clear — so Mistlands is in the mask whenever it can be returned.
> - A single-bit mask returns itself (`if ((biome & (biome − 1)) == 0) return biome;`), so the bug
>   only bites multi-biome types.
>
> **Therefore the conservative superset the bound must be computed over is**
> `reach(L) = m_biome(L) ∪ ({BlackForest} if the Plains bit is set and m_biome has ≥ 2 bits)`.
> Computing `S_L` over `m_biome` alone omits the BlackForest points and can therefore report a
> `minDist(L)` that is **too large**, rejecting seeds that really do contain `L` within `R`. `vseed`
> uses `reach(L)` for the bound, and the exact per-draw biome only in T5.

#### 3.2.5 T3 — heights with rivers disabled (`SOUND-BOUND` for land)

**Claim.** `GetBiomeHeight` with rivers disabled is pointwise ≥ `GetBiomeHeight` with rivers.

**Proof.** `AddRivers(x,z,h)` returns `h` unchanged when `weight <= 0`; otherwise
`if (h > num) h = Lerp(h, num, weight)` with `num = Lerp(0.14f,0.12f,t) ∈ [0.12,0.14]`,
`weight ∈ (0,1]`, and the second clause is the same shape with `num2 ∈ [0.128,0.139]`. Both move `h`
toward a *smaller* value and only when `h` already exceeds it, so `AddRivers(h) ≤ h`. Every biome
function applies the identical post-river terms in both variants:
Meadows/Plains/BlackForest/Swamp/Mountain/DeepNorth add fixed noise terms (`+F`, and Mountain's
`+P(U·0.2,V·0.2)·2·tilt`), which are additive constants w.r.t. `h`; Mistlands applies
`Lerp(h + P·0.002, ceil(h·400)/400, k)` with `k = Clamp01(num3·7) ∈ [0,1]` independent of `h` — both
arguments are non-decreasing in `h` (`ceil` is non-decreasing), and a `DUtils.Lerp` with fixed `k` of
two non-decreasing functions is non-decreasing; AshLands does not call `AddRivers` at all outside
pre-generation (`GetAshlandsHeight` contains no `AddRivers` call; only `GetAshlandsHeightPregenerate`
ends with one).

**Two steps the proof was missing, both verified:** (a) every branch of `GetBiomeHeight` returns
`biomeFn(...) * num2 + 0f` with `num2 = 200f * CreateAshlandsGap(wx,wy) * CreateDeepNorthGap(wx,wy)`,
and both gaps are `MathfLikeSmoothStep(0, 1, Clamp01(|d| / 400))` ∈ `[0, 1]`, so `num2 ≥ 0` and the
multiplication preserves `≤`; (b) the *biome* is the same in both variants, because `GetBiome` at its
default arguments never consults rivers (§1.1) — so the same branch is taken and the two heights are
comparable at all. Hence the inequality propagates to the returned height. ∎

**Consequences:** `land_norivers ⊇ land_true`, so
- "largest island ≥ X" → reject when `largest(norivers) < X`. Sound.
- "spawn island ≥ X" → reject when `spawnComponent(norivers) < X`. Sound.
- "total land ≥ X" → reject when `land(norivers) < X`. Sound.
- "island count ≥ N" → **accept** when `count(norivers) ≥ N`, because cutting with rivers can only
  split components, never merge them, so `count_true ≥ count_norivers`. Sound.
- "island count ≤ N" → reject when `count(norivers) > N`. Sound.

Tightness: for seed −1772362158 the river network is ~77 rivers (60–100 m wide) plus ~4,800 stream
segments (20 m wide), i.e. of order 20 km² of carved area against 117.7 km² of land, much of which
lies over water already. Expect the bound to be loose by roughly 5–15% of land area; it is still a
strict filter and it skips the entire 0.2 s S2 stage.

#### 3.2.6 T4 / T5 — exact

T4 runs S2 then S3 on G_r: exact island, height, peak, lava and river metrics.
T5 runs S4 then the full 183-type placement loop: exact location sets. T5 is the only tier that can
answer a boss/trader/dungeon goal exactly, and it is ~4 orders of magnitude more expensive than T1.

#### 3.2.7 What admits no cheap conservative prefilter

Be blunt about these in the docs:

- **"Biome B exists anywhere"** when B's patch may be smaller than the grid cell. The G12 grid is the
  *game's own* sampling for location purposes, so for location goals it is definitional; but for a
  pure "does this world contain any Mistlands pixel at all" question, only an exhaustive fine
  evaluation answers it, and there is no sound coarse rejection — **because we cannot bound
  `Mathf.PerlinNoise`.** It is `[FreeFunction("PerlinNoise::NoiseNormalized")] extern` native code;
  the KB found Ken Perlin's permutation table (151,160,137,91,90,15,…) as 512 int32s at file offset
  `0x1BE0BC0` in `UnityPlayer.dll` — **re-verified by the reviewer: the 512 little-endian int32s at
  that offset are exactly `perm[0..255]` repeated twice, each half a permutation of 0..255, starting
  151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225** — but the gradient set, fade curve and
  normalisation are **Unverified**. Without them there is no Lipschitz constant, so there is no
  interval arithmetic.
  *If* the port is later verified bit-exact against dumped samples (§5.6), a Lipschitz bound becomes
  derivable and a genuinely conservative coarse-grid rejection tier (T2-L) can be added; it is
  specified as future work, gated on that verification, and must not ship before it.
- **Anything about location placement order** — `m_unique` winners, rotations, dungeon seeds.
- **"Exactly k of L"** as opposed to "≥ k": the upper bound from T2b is weak (many grid cells per
  zone), so an upper-bound rejection on counts is technically sound but almost never fires.

### 3.3 Region restriction

Every goal carries an implicit region:
`radius-bounded goal with bound R` → disc of radius `min(10500, R + 90.51)` (the +90.51 is exactly the
T2b bound; omitting it would be a correctness bug for location goals).
The seed's region of interest is the union over all goals; T2/T3/T4 evaluate only that region.
For a 2 km goal this is `(2090/10500)² = 4%` of the map — a 25× saving, sound.

### 3.4 Parallelism (16 threads on this machine)

Hardware here: **AMD Ryzen 7 9800X3D, 8 physical cores / 16 logical, 4.7 GHz**.
Measured parallel speed-up on the T2@192 m workload: `3591 / 341 = 10.5×` on 16 logical threads
(not 16×; SMT gives ~31% on top of 8 cores for this FP-heavy, cache-resident workload).
**Use 10.5× as the planning factor, not 16×.** Publish it in `--help` so estimates are honest.

Model:
- **One seed per thread**, never split a seed across threads. Every tier is embarrassingly parallel at
  seed granularity, and per-seed state (G_r buffers, river grid, union-find scratch) is large but
  reusable — allocate once per worker and reuse forever (no GC churn; the whole engine should run with
  zero steady-state allocation in the hot path).
- Per-worker buffers at r = 12: biome `byte[2048*2048]` = 4 MB, height `float[2048*2048]` = 16 MB,
  union-find `int[2048*2048]` = 16 MB → 36 MB × 16 threads = 576 MB. That exceeds the 9800X3D's
  96 MB L3, so **allocate the big buffers only in the T4/T5 pool and size that pool separately**
  (e.g. 16 workers for T1/T2, 8 for T4/T5). Make the pool sizes independent flags.
- Work distribution: **blocks of 2^16 consecutive scan indices**, claimed atomically from a shared
  counter. Big enough to amortise, small enough that a 16-way tail is under a second at T2.
- No shared mutable state except (a) the block counter, (b) a lock-free result queue drained by a
  single writer thread, (c) an atomic progress counter.

### 3.5 Deterministic, resumable, repeat-free scanning

Two modes, both covering the int32 space exactly once:

**`order: sequential`** — index `i ∈ [0, 2^32)` maps to seed `i + int.MinValue`. Simple, cache-friendly
for nothing in particular (there is no locality in seed space anyway), and easy to reason about.

**`order: shuffled` (default)** — index `i` maps to `seed = F_k(i) + int.MinValue`, where `F_k` is a
**4-round balanced Feistel permutation on 32 bits**:

```
L,R = i >> 16, i & 0xFFFF
for round in 0..3:  L,R = R, L ^ (H(k, round, R) & 0xFFFF)
F_k(i) = (L << 16) | R
```

`H` = xxHash64 (or SipHash-2-4) keyed by the run key `k`. This is a bijection on `[0,2^32)` by
construction, so **every seed is visited exactly once and none is repeated**, the order looks random
(so partial runs are an unbiased sample of the whole space, and early hits are spread over the map
rather than clustered in one arithmetic region), and the state is a single 64-bit index.

`k` is written into the run manifest; rerunning with the same `k` and index range reproduces the
identical seed sequence, therefore identical results.

**Checkpointing.** A `run.ckpt` (little-endian, versioned) holds:
`{engine version, game build fingerprint, query hash, defs version, order, key k, blockSize,
nextBlock, completedBlockBitmap, seedsEvaluated, tierCounters, bestResultsHeap}`.
Blocks are claimed monotonically but may finish out of order, so the bitmap (2^32/2^16 = 65,536 bits =
8 KB) is the authority. Flush every `--checkpoint-interval` (default 30 s) with write-to-temp +
`fsync` + atomic rename. On resume, re-run any claimed-but-unfinished block: tiers are pure functions
of the seed, so re-running is free of side effects and the result stream is deduplicated by seed.

**Determinism requirements.** No floating-point reduction order dependence in metrics (sum areas as
`long` cell counts, not floats); no `HashSet` iteration order in anything that reaches output; ties
broken by an explicit total order (score desc, then seed asc). A run must be byte-reproducible given
`{engine version, query, defs, k, range}`.

### 3.6 Ranking and scoring

Each goal is `{target, mode, value, importance, weight?}`.

- `importance: must` → hard filter. A seed failing any `must` goal is rejected and, if
  `--explain-failures` is on, emits a one-line reason (goal id, measured value, threshold, tier).
- `importance: nice` → contributes a sub-score `s ∈ [0,1]` with weight `w` (default 1).

Sub-score definitions (all clamped to [0,1], all monotone, all stated in the output):

| mode | sub-score |
|---|---|
| `near` (value `D`) | `s = clamp01(1 − d/D)` where `d` = the metric distance; `s = 0` beyond `D` |
| `far` (value `D`) | `s = clamp01(d/D)`; `s = 1` at or beyond `D` |
| `count_near` (value `k` within `R`) | `s = min(1, n/k)` where `n` = count within `R` |
| `area` (value `X`) | `s = min(1, a/X)` |
| `range` (value `[lo,hi]`) | `1` inside, falling linearly to 0 over a stated pad (default 25% of the span) |

Total: `score = Σ wᵢ·sᵢ / Σ wᵢ` ∈ [0,1]. Results are kept in a bounded max-heap of size
`--keep` (default 200), ordered by `(score desc, seed asc)`. `--keep` is the only cap in the tool and
it is a memory bound, not a search bound; `--out results.jsonl` streams **every** passing seed with no
cap at all.

Report `score` together with the **per-goal breakdown** — a single number without the breakdown is
useless for deciding between two seeds.

---

## 4. Throughput: what is actually searchable

### 4.1 The cost model, from the algorithm

Perlin evaluations per call, counted from the decompiled source:

| Function | `DUtils.PerlinNoise` calls | note |
|---|---|---|
| `GetBaseHeight` (non-menu) | **8** | 3 octave pairs (6) + 2 sea-channel |
| `GetBiome` | 8 + **0..4** masks | early-out on AshLands / Ocean / DeepNorth / Mountain |
| `GetMeadowsHeight` / `GetForestHeight` / `GetPlainsHeight` | 8 (its own `GetBaseHeight`) + 6 | note it **recomputes** base |
| `GetMarshHeight` | 4 | no base at all; no seed offset (`wx + 100000` only) |
| `GetMistlandsHeight` | 8 + 7 | |
| `GetDeepNorthHeight` | 8 + 6 + `Fbm(…,3)` = 3 | |
| `GetSnowMountainHeight` | 8 + `BaseHeightTilt` (4 × 8 = **32**) + 7 | by far the most expensive Perlin biome |
| `GetOceanHeight` | 8 | it *is* `GetBaseHeight` |
| `GetAshlandsHeight` | 8 (base) + **8 own Perlin** + 5 `GetCellular` + 3 `GetCellular` + 1 `GetSimplexFractal` | most expensive overall; both cellular loops drop to **2** octaves when `cheap: true` |
| `GetHeight` = `GetBiome` + `GetBiomeHeight` | Swamp 13, Meadows/BlackForest/Plains 26, Mistlands 27, **55 on Mountain** | |

*(Corrected: "48+ on Mountain" undercounts. `GetBiome` on a Mountain point costs 8 Perlin — it returns
at `baseHeight > 0.4f`, before any mask — and `GetSnowMountainHeight` costs 8 + 32 + 7 = 47, so
`GetHeight` is **55**, or 47 with the base memoisation below. Swamp is 8 + 1 mask + 4 = 13: the
Swamp test is the first mask, and `GetMarshHeight` calls no `GetBaseHeight`. Of the 8 own Perlin
calls in `GetAshlandsHeight`, **4 are dead** — `num11` is computed from
`P(0.01)·P(0.02)` and `P(0.05)·P(0.1)` and never read again. A port may drop them; they cost nothing
in correctness and ~4 Perlin per Ashlands point in speed.)*

**Exact optimisation, not an approximation:** `GetBaseHeight(x,z)` is a pure function and is evaluated
**twice** per `GetHeight` (once inside `GetBiome`, once inside the biome height function). Memoising
it for the duration of one `(x,z)` gives bit-identical output and removes 8 Perlin calls — ~30% off
S3. Do it. Do **not** memoise it across the `BaseHeightTilt` offsets (±1 m), which are different
points.

### 4.2 Measured numbers

Benchmark: `scratchpad/probe/bench/` (.NET 10.0.401, Release, `ServerGarbageCollection`), a
structurally faithful port of `GetBaseHeight` / `GetBiome` / a Meadows-style `GetHeight` with the same
float/double casts and the same Perlin call counts, driving an improved-Perlin surrogate. **The
*values* are not the game's** (Unity's native Perlin is unverified) — only the **shape and the cost**
are, and cost is all that is claimed here.

```
Perlin                                190.3 M/s 1T      (5.3 ns)
GetBaseHeight                           9.94 M/s        (101 ns, 8 Perlin)
GetBiome                                7.46 M/s        (134 ns)
GetBiome+GetHeight (no base reuse)      4.26 M/s        (235 ns for the WHOLE call)
2048x2048 G12 biome+height grid         1.12 s/seed 1T
Pregenerate (lakes+rivers+streams)      0.23 s/seed 1T  (surrogate; see 4.3)
biome map @384 m (2,129 pts)            0.82 ms/seed 1T
biome map @192 m (8,516 pts)            2.93 ms/seed 1T
biome map @ 96 m (34,093 pts)           8.73 ms/seed 1T
biome map @ 48 m (136,364 pts)         24.82 ms/seed 1T
biome map @ 24 m (545,436 pts)         85.95 ms/seed 1T
biome map @ 12 m (2,181,705 pts)      303.6  ms/seed 1T
T1 probe, 256 points, 16T             209,718 seeds/s
T2 @192 m, 16T measured                 3,591 seeds/s   (vs 5,459 "ideal 16×" → 10.5x scaling)
```

**Corrected annotations on that block.** `4.26 M/s` is `1 / 235 ns` for the *whole*
`GetBiome + GetBiomeHeight` call, not for "the height part"; with `GetBiome` at 134 ns the height part
is ≈ **101 ns**. And the decomposition used by the tier table mixes two different point sets: the
`biome map @ r` rows count only the disc `r ≤ 10000` (`π·10000²/r²`: 2,181,705 at 12 m, 8,516 at
192 m — all six rows check out), while `2048x2048 G12 biome+height grid` counts the full 4,194,304-cell
square. So "T3 = T2@12 + 0.82 s" is really "T3(full square, 1.12 s) − T2(disc, 0.304 s)". The **totals**
(T3 ≈ 1.12 s, T4 ≈ 1.30 s) are the numbers to trust; the deltas are not additive.

Sanity check against the game itself: `LogOutput.log` records
`Generating new world minimap done [4265ms]` against the .NET port's 1.12 s for the same 2048² grid.
**This is not the same work in each direction**, so the 3.8× ratio is an upper bound on the
Mono-vs-.NET-10 gap, not a measurement of it: `Minimap.GenerateWorldMap` also calls
`GetMaskColor(wx, wy, biomeHeight, biome)` per pixel, which runs `GetForestFactor` (3 Perlin via
`DUtils.Fbm`) on Meadows/Plains/Mistlands cells and a **whole extra `GetAshlandsHeight(cheap: true)`**
on every AshLands cell — 1,361,539 of the 4,194,304 cells in this world.

### 4.3 Pre-generation cost, corrected against real data

The surrogate ran `FindLakes` (157×157 grid, inside r = 10000, `GetBaseHeight < 0.05`),
`MergePoints(800)` and both `PlaceStreams` passes (3,000 attempts each, 100 start tries and 100 end
tries at 198.8 → 80 m in 1.2 m steps):

```
raw lake grid points   6,579      merged lakes  94      rivers  77
river-end scans       17,112      IsRiverAllowed calls  523
stream start tries   145,451      end tries   101,200      streams kept 4,827 (2 passes)
```

The stream-loop iteration count is set by the acceptance probabilities. Those were measured on the
**real** height field of seed −1772362158 (G12 cache, restricted to the ±10000 square that
`FindStreamStartPoint` samples):

```
P(26 < h < 31) = 0.0814   -> expected start tries 12.3   (surrogate: 0.0406 -> 24.6)
P(36 < h < 44) = 0.0555   -> expected end   tries ~18    (surrogate: 0.0685 -> 14.6)
```

*(Both probabilities re-measured by the reviewer over the 2,775,556 G12 cells with |x| ≤ 10000 and
|z| ≤ 10000 — the square `FindStreamStartPoint` actually samples with
`Random.Range(-10000f, 10000f)`: **0.081384** and **0.055463**. The loop constants also check out:
`PlaceStreams` runs `for (i = 0; i < 3000; i++)` with `FindStreamStartPoint(100, 26f, 31f, …)` and
`FindStreamEndPoint(100, 36f, 44f, p, 80f, 200f, …)`, and the end-point radius is
`num2 -= (200−80)/100` **before** each test, so it sweeps 198.8 → 80 m in 1.2 m steps. **Caveat, and
it is not small:** these two probabilities were taken from `cacheMinimapHeight`, which is
`GetBiomeHeight(...)` after full pre-generation, whereas both stream searches call
`GetPregenerationHeight(x, z, riverPreGen)` → `GetBiomeHeight(..., preGeneration: true, …)`, a
different function (Mistlands uses `GetForestHeight`, DeepNorth and AshLands use their `…Pregenerate`
variants, and `num2` is `200f` with no Ashlands/DeepNorth gap). The acceptance rates are an
approximation of the right ones, not the right ones. **Unverified** until the port logs them.)*

so the surrogate over-counts stream evaluations by roughly **1.29×** (real
`12.3 + 0.9998·18.0 = 30.3` evaluations per `PlaceStreams` iteration against the surrogate's
`24.6 + 0.984·14.6 = 39.0`; the previously quoted 1.35× came from summing the two ratios rather than
the two expectations). Corrected estimate:
**S2 ≈ 0.15–0.20 s/seed, 1 thread**, of which a large share is `MergePoints`' O(n²) nearest-neighbour
loop over ~6,600 points (the game's `MergePoints` has the same shape; a k-d tree or a 800 m uniform
grid reproduces the *same* merge sequence only if the "closest neighbour" tie-breaking matches — so
**keep the naive scan unless you can prove the acceleration picks the identical index**, because the
merge order feeds `m_lakes` order, which feeds `PlaceRivers`).

### 4.4 Location placement cost

Ground truth from the user's own session (`BepInEx\LogOutput.log`, 2026-09-22, world `testworldclaude`,
fresh world, Mono, time-sliced at 0.1 s/frame):

```
Loading: Done. ... Genloc duration: 33417.005 ms, (Total genloc time: 00:00:38.1089140,
                    locations: 183, iterations: 0)
There are 18 that take a long time to generate (over 0.5 sec).
Total slow location time is 18.5 seconds that could be saved on world gen!
```

**33.4 s wall / 38.1 s total for 183 types on Mono.** A .NET 10 port with the `GetBaseHeight`
memoisation should land at **3–10 s/seed, 1 thread**. *That range is an estimate*, not a measurement;
the implementer must measure it and update this section.

> **Corrected — the stated reason for the 18 slow types is a misreading of the source.**
> `ZoneSystem.HaveLocationInRange` does **not** scan `m_locationInstances`. It looks up three
> pre-built caches — `m_locationIDCache[assetID]`, `m_locationGroupCache[group]`,
> `m_locationMaxGroupCache[group]` (all filled by `AddToCache` inside `RegisterLocation`) — and
> linearly scans only the matching list, i.e. at most the instances of that one prefab or group (200
> for `Crypt2`, not 12,314). What *does* scan the whole dictionary is
> `ZoneSystem.CountNrOfLocation(location)`, called **once per type** at the top of
> `GenerateLocationsTimeSliced`, and `RemoveUnplacedLocations`, called once per placed unique. The
> real per-candidate cost is dominated by `GetTerrainDelta` (10 × `GetHeight` at
> `Random.insideUnitCircle * m_exteriorRadius`), `GetHeight` itself, and — for
> `m_surroundCheckVegetation` types — `m_surroundCheckLayers × 6` further `GetHeight` calls.
> A spatial index is therefore *not* the lever it was claimed to be; if one is added anyway it must
> return exactly the same accept/reject decisions (same squared-distance test on the full `Vector3`,
> same inclusion of unplaced candidates) or the placement diverges.
>
> Two further cost constants the section did not state:
> `int attempts = location.m_prioritized ? 60000 : 12000` outer attempts per type, each attempt
> granting an inner `for (j = 0; j < 6; j++)` retry loop over `GetRandomPointInZone`.

### 4.5 What that means for the 2^32 space

Using the measured 10.5× scaling on 16 logical threads:

| Tier | seeds/s 1T | seeds/s 16T | full 2^32 | seeds/hour 16T | fraction of 2^32 per hour |
|---|---|---|---|---|---|
| T1 probe (256 pts) | ~20,000 | **209,718** | **5.7 h** | 7.5×10^8 | **17.6%** |
| T2 @384 m | 1,213 | ~12,700 | 3.9 days | 4.6×10^7 | 1.06% |
| T2 @192 m | 341 | **3,591** | 13.8 days | 1.3×10^7 | 0.30% |
| T2 @96 m | 115 | ~1,210 | 41 days | 4.4×10^6 | 0.10% |
| T2 @12 m (G12 biome only) | 3.3 | ~35 | **3.9 years** | 1.2×10^5 | 0.0029% |
| T3 (G12 heights, rivers off) | 0.89 | ~9.4 | 14.5 years | 3.4×10^4 | 0.00079% |
| T4 (S2 + exact G12 heights) | 0.77 | ~8.1 | 16.8 years | 2.9×10^4 | 0.00068% |
| T5 (full locations, est.) | 0.09–0.23 | 1–2.4 | 57–146 years | 3.4×10^3–8.7×10^3 | ~0.0002% |

*(Corrected: the T5 row's 1T figure did not follow from its own input. "T4 + 3…10 s" is 4.3–11.3 s
per seed = **0.09–0.23** seeds/s, not 0.1–0.3; at the 10.5× factor that is 0.93–2.4 seeds/s on 16T
and 4.295e9/0.93 = 146 years to 4.295e9/2.4 = 57 years. Every other row's arithmetic was re-checked
and is correct.)*

**Say this plainly in the README and in `--help`:**

> A complete sweep of all 4,294,967,296 worlds is only possible at the cheapest tier. At 256 probe
> points it takes 5.7 hours. Adding exact biome geometry at 192 m takes two weeks. Anything that needs
> terrain heights, rivers or where the bosses actually are cannot be swept — those tiers run at
> 10^4 seeds per hour, so a 24-hour run sees about 0.006% of all worlds. The tool has **no result cap
> and no seed cap**; the limit is arithmetic, not policy.

Designing around it, in order of leverage:

1. **Put every hard existence goal in T1/T2 and use them to build a candidate list once.** A single
   overnight T2@192 m run over a shuffled 10^8-seed prefix costs ~7.7 h and yields a reusable
   candidate file; later queries re-filter that file instead of re-scanning.
2. **Region-restrict.** Most real goals are "within 2–4 km of centre". `(2090/10500)² = 4%` of the
   map → T2@12 m goes from 0.30 s to 0.012 s, i.e. ~900 seeds/s on 16T. This alone makes G12-exact
   trader/boss *bounds* practical at scale.
3. **Order goals by selectivity, measured.** The engine calibrates on the first 10,000 seeds of a run,
   records each goal's pass rate, and reorders the T2b/T3 checks cheapest-first ×
   most-selective-first. Reordering never changes results (all filters are conjunctive and pure).
4. **Persist everything.** A local SQLite/Parquet store keyed by (seed, defs, tier) means no seed is
   ever evaluated twice across runs. Over months this is the biggest single win.
5. **Be honest in the progress line**: `seeds/s`, `seeds done`, `% of 2^32`, `ETA for the requested
   range`, `ETA for the full space`, and the current tier mix.

For the comparison the user cares about: `valheim.gaming.tools` evaluates **20,000 seeds per search**
and stops. `vseed` does 20,000 seeds at T1 in **0.1 s**, at T2@192 m in **5.6 s**, at T2@12 m in
**9.5 min**, at T4 in **42 min**, and at T5 in **~2–6 h** — and it can keep going for a week.

---

## 5. Features

### 5.1 What both sites do, that we must match

- **Inspect a seed**: full world map with terrain and biomes, all ~12,300 location markers by
  category, coordinates and distances, "highest peaks" (bobmitch).
- **Search by goals**: presets and custom goals with `must` / `nice`, `near` / `far` / `count_near`,
  distance from centre, a world-version selector, a configurable seed budget, keep-best-N
  (gaming.tools).
- **Ruler / measure**, **base planner**, **save-file overlay** (fog, pins, buildings) — bobmitch's
  drag-a-`.fwl`/`.fch`/`.db` feature.

### 5.2 What only a local tool can do — and what fits this user

The user plays multiplayer with a Cartography Table, values immersion, tests every build live, and
already runs TomTom/Wayfinder.

1. **Int-seed search with seed-text inversion (§0.2).** Search the 4.29×10^9 real worlds, then emit a
   typeable 10-character seed. No other tool does this; it is a 1.99×10^8-fold efficiency gain.
   - **Vanity seeds**: `--seed-text-pattern "DOOM*"` — constrain the lane characters, then invert
     within that constraint (a prefix on even/odd positions fixes part of both lanes; the
     meet-in-the-middle still applies over the free tail).
2. **No caps, fully offline.** No network calls at any point, ever — a hard requirement, asserted by a
   test that fails the build if any socket is opened.
3. **Reproducible runs.** `run.manifest.json` carries engine version + git hash, game build
   fingerprint (`assembly_valheim.dll` SHA-256 + **`Version.c_WorldGenVersion`**, which is `2` —
   *corrected:* `Version.World.WorldGenVersion` is a member of the **save-format** enum and equals
   **26**, an entirely different number, so pinning it would pin the wrong thing), the query file
   hash, `defs` version, Feistel key, index range, thread count, and the dumper data hash. Re-running
   with the manifest reproduces byte-identical output, or fails loudly saying which input changed.
4. **"Why did this seed fail".** `vseed explain <seed> -q query.yaml` prints, per goal: the tier that
   decided it, the measured value, the threshold, the margin, and — for `SOUND-BOUND` rejections — the
   bound and its proof reference (`T2b: minDist(Vendor_BlackForest) ≥ 2,431 m > 2,000 m requested;
   bound = nearest G12 BlackForest/... cell at 2,521 m minus 90.51 m`). This is the feature that turns
   a search from a slot machine into a tool.
5. **Compare seeds.** `vseed compare A B [C…]` → a metric table with deltas, plus side-by-side maps
   and an optional difference map (biome-change mask). Useful when the top 5 candidates all score 0.93.
6. **Export into the user's own mods.** `vseed export <seed> --format wayfinder` writes a route/waypoint
   file for Wayfinder; `--format console` writes a script of `waypoint x z name` commands.
   **Every exported marker must be local-only** (`save:false`, ownerID 0) — the user shares a
   Cartography Table in multiplayer and does not want tool output leaking onto everyone's map. The
   exporter must refuse to emit anything that would become a saved/shared pin.
7. **Spoiler control, default-on.** `--spoiler none|terrain|landmarks|all` (default `terrain`). At
   `terrain` the map shows biomes, water, rivers and contours and *no* location markers; `landmarks`
   adds runestones/ruins/scenery; `all` adds bosses, traders, dungeons and Places of Mystery. The
   search engine can still *filter* on hidden targets while the report says only
   "Bonemass: satisfied (distance withheld)". This exists because the user built Wayfinder
   specifically to avoid looking things up — the default must respect that.
8. **Multiplayer/group metrics.** "All three traders within R of spawn", "a boss altar of each of the
   first three bosses within R", "a shared base site: ≥ X m² of land with slope < s, within 200 m of
   navigable water, inside biome B, within R of spawn". Expose the base-site finder as a *search
   criterion*, not just a manual planner (bobmitch has a planner, not a finder).
9. **Route planning.** Given a seed and a target list, compute a sailing route over water cells
   (A* on G_r with water/land costs, shoreline penalty) and a walking route over land with slope and
   biome-danger weights; output distance, leg list and an ETA at configurable speeds. Export as
   waypoints (see 6).
10. **Validate against your own worlds.** `vseed verify` (§5.6).
11. **Seed library.** Append-only local store of every evaluated seed with its metric vector, plus
    tags, notes and favourites. `vseed similar <seed>` finds the nearest metric vectors already in the
    store — "like this one, but with the Swamp closer".
12. **Save-file overlay**, read-only: parse `.fwl2`/`.db2`/`.fch` with the existing
    `valheim-worldgen/scripts/valheim_saves.py` logic (ported or shelled out; the world-save reading
    has since been ported: `vseed worlds`, `vseed world <name> --locations`) and draw explored fog,
    player pins and the real placed-location set on top of the generated map. This is also the best
    smoke test that the port agrees with a world the user has actually played.
13. **Long-run ergonomics.** Resumable, checkpointed, `--budget 8h`, `--nice`, a one-line status that
    is honest about the fraction of the space covered, and a `SIGINT` that checkpoints and exits 0.

### 5.3 Criteria language

YAML (a JSON subset, so `.json` works identically). One file = one reproducible search.

```yaml
version: 1
defs: 1                     # metric-definition version (section 2)
world:
  gen_version: 2            # 0 | 1 | 2  -> WorldGenerator.VersionSetup
  modifiers: {}             # reserved; nothing in generation reads world modifiers
search:
  order: shuffled           # shuffled | sequential
  key: 0x5EEDF00D1234ABCD   # Feistel key; omit -> derived from the query hash (still deterministic)
  range: [-2147483648, 2147483647]
  budget: { seeds: 50000000, wall: "8h" }   # whichever comes first; both optional
  keep: 200
  grid: 12                  # metres; the definitional grid for this query
  approx: false             # true enables HEURISTIC prefilters and taints every output record
  threads: { light: 16, heavy: 8 }
goals:
  - id: haldor-close
    target: { kind: location, name: Vendor_BlackForest }   # or kind: group, name: traders
    metric: candidate_min_distance         # distance | candidate_min_distance | count_within | area | ...
    from: center                           # center | spawn
    mode: near
    value: 2000                            # metres
    importance: must
  - id: swamp-early
    target: { kind: biome, name: Swamp }
    metric: nearest_distance
    mode: near
    value: 2500
    importance: nice
    weight: 2
  - id: big-home-island
    target: { kind: world_shape, name: spawn_island_area }
    mode: area
    value: 4000000                          # 4 km^2
    importance: nice
    weight: 3
  - id: not-too-many-crypts-far
    target: { kind: group, name: burial_chambers }   # Crypt2|Crypt3|Crypt4
    metric: count_within
    radius: 3000
    mode: count_near
    value: 8
    importance: must
output:
  jsonl: results.jsonl
  map_top: 10                # render maps for the best 10
  explain: true
report:
  spoiler: terrain
```

Rules the parser enforces:
- unknown keys are errors, not warnings (a typo must not silently drop a `must` goal);
- every goal's required tier is computed and printed;
- unsatisfiable goals (T0, §3.2.1) abort with the geometric reason;
- `grid` != 12 prints a warning that the metrics are defined on that grid and are not comparable with
  G12 results;
- `approx: true` prints a red banner and stamps `"approx": true` on every record.

**Preset queries** (shipped in `presets/`, each a plain query file the user can copy and edit),
mirroring the play styles on `valheim.gaming.tools`:

| Preset | Goals |
|---|---|
| `balanced` | all 9 biomes present (G12 area ≥ 1 km² each, `nice`); each of the first five bosses' nearest altar `nice near` its biome-typical radius; spawn island ≥ 2 km² `nice` |
| `compact-progression` | `must`: nearest altar of Eikthyr ≤ 1.5 km, Elder ≤ 3 km, Bonemass ≤ 4 km, Moder ≤ 5 km, Yagluth ≤ 6 km; `nice`: minimise the max of those |
| `all-traders` | `must`: candidate_min_distance ≤ 3 km for each of `Vendor_BlackForest`, `Hildir_camp`, `BogWitch_Camp` (with the §0.6.2 caveat printed on every result) |
| `boss-rush` | `must`: all seven boss prefabs have ≥ 1 candidate within 6 km; `nice`: minimise the sum of nearest distances |
| `gentle-start` | `must`: spawn island area ≥ 3 km², Meadows area within 1 km ≥ 0.5 km², **no** Swamp/Plains/Mistlands cell within 1.2 km; `nice`: BlackForest within 800 m |
| `iron-rich` | `must`: `SunkenCrypt4` count_within 3 km ≥ 6; `nice`: Swamp area within 4 km |
| `dungeon-delver` | `must`: ≥ 10 of (`Crypt2|3|4`) within 3 km; `nice`: `TrollCave02`, `MountainCave02`, `Mistlands_DvergrTownEntrance1|2` counts within 5 km |
| `archipelago` | `must`: island count (≥1 ha) ≥ 120; `nice`: largest island ≤ 3 km², total land 80–130 km² |
| `large-continents` | `must`: largest island ≥ 8 km²; `nice`: island count ≤ 150 |
| `coastal-builder` | `must`: ≥ 0.4 km² of land within 600 m of spawn with slope < 8° and ≥ 60% of its perimeter on water; `nice`: Meadows biome |
| `custom` | empty skeleton with every field commented |

The exact thresholds above are **starting points, not measurements** — they must be calibrated by
running each preset over a 10^6-seed shuffled sample and tuning so the `must` set passes for roughly
0.1–2% of seeds. Ship the calibration script and the resulting distribution table.

### 5.4 CLI surface

```
vseed hash   <text>                          -> int32 seed (+ the two lane values)
vseed name   <int32> [--len 10] [--alphabet game|alnum] [--pattern "DOOM*"]
vseed inspect <seed> [--tier auto|t2|t4|t5] [--spoiler …] [--json|--table]
vseed map    <seed> -o out.png [--res 2048|4096|8192] [--layers biome,water,shade,contour,grid,markers]
             [--marks bosses,traders,dungeons,…] [--zoom x,z,radius] [--spoiler …]
vseed search -q query.yaml [--out results.jsonl] [--resume run.ckpt] [--threads …] [--budget …]
             [--approx] [--progress json|tty]
vseed explain <seed> -q query.yaml
vseed compare <seedA> <seedB> [...] [--map]
vseed route  <seed> --targets a,b,c [--mode sail|walk|mixed] [--speed …]
vseed export <seed> --format wayfinder|console|csv|json [--targets …] [--out …]
vseed verify [--world <name>] [--cache <dir>] [--tolerance 0.05]
vseed presets list|show <name>
vseed store  import|query|tag|similar
vseed bench  [--tiers t1,t2,t4]              -> prints this machine's real numbers for §4
```

Cross-platform: a single .NET 10 project, `PublishAot` where possible, no Unity dependency, no
platform-specific APIs. Paths, save-file discovery and the minimap-cache location are the only
OS-specific parts; keep them behind one `IPlatform`. Windows is the primary target
(`win-x64` single-file); `linux-x64`, `osx-arm64` and `osx-x64` from the same source. Rendering uses
`ImageSharp` (pure managed) rather than `System.Drawing`, which is Windows-only.

### 5.5 Output formats

- **JSONL**, one record per passing seed — the primary format, streamable, no cap:
  ```json
  {"seed":-1772362158,"text":"MWd8eV6svz","score":0.871,"tier":"t5","defs":1,"grid":12,
   "goals":{"haldor-close":{"value":2516.4,"s":0.0,"pass":true},"swamp-early":{"value":2103.0,"s":0.16}},
   "metrics":{"land_km2":117.7,"largest_island_km2":5.77,"island_count_1ha":227,
              "spawn_island_km2":null,"spawn_island_note":"origin cell is water; StartTemple at (70.5,-2.8)"},
   "engine":"0.4.1","approx":false}
  ```
- **CSV** with a fixed, versioned column set (for spreadsheets);
- **human table** for the terminal, sorted by score, with the per-goal breakdown;
- **PNG / WebP** maps (§6);
- **self-contained HTML report** per seed: the map inline as a data URI, the metric table, the goal
  breakdown, the marker list with coordinates, and the seed text — one file the user can keep or send
  to the people they play with. Offline, no CDN references.
- **Wayfinder/TomTom export** (§5.2.6), local-only markers.

### 5.6 Verification — the acceptance gate

The port is not allowed to ship results until all of these pass; `vseed verify` runs them.

| # | Check | Oracle | Tolerance |
|---|---|---|---|
| V1 | `GetStableHashCode` | the 5 samples in §0.1 | exact |
| V2 | offsets `m_offset0..4`, `m_riverSeed`, `m_streamSeed` for ≥ 10^4 seeds | BepInEx dumper plugin (reflection on the private fields) | exact |
| V3 | `Mathf.PerlinNoise` | dumper: a dense sample sweep | exact bits |
| V4 | `GetBiome` over all 2048² G12 points | `cacheMinimapBiome` (collapsing Mountain/DeepNorth/Ocean to one class) | 100% of cells |
| V5 | `GetBiomeHeight` over all 2048² G12 points | `cacheMinimapHeight` (half-float) | ≤ 0.02 m at 30 m, ≤ 0.3 m at 400 m; **and** 0 cells crossing the 30 m land threshold |
| V6 | the mask channels | `cacheMinimapMask` | ±1/255 |
| V7 | location instance set | newest `_main.<n>.db2` of `asdasdasd` (today `_main.3.db2`): **176 prefab types, 12,314 instances, locationVersion 32, 112 generated zones**, plus the §0.6 position fixture | exact set and exact coordinates |
| V8 | determinism | same query + key + range twice | byte-identical output |

V4/V5 are the ones that matter: 4.19 million ground-truth samples per world, already on disk, free.
V7 is the only check that exercises S4+S5 end to end. Fixture (seed −1772362158, world `asdasdasd`,
distances from centre; `*` = placed):

```
StartTemple                    (70,-3) d=71*
Eikthyrnir       n=3           (110,-71) d=131* ; (-596,-174) d=621 ; (-779,428) d=889
GDKing           n=4           (188,-2751) d=2757 ; (-2817,1665) d=3273 ; (-4411,-2812) d=5231 ; (-6331,2947) d=6983
Bonemass         n=5           (1723,2561) d=3087 ; (3595,-1977) d=4103 ; (-4355,-636) d=4401 ; (-2684,3522) d=4428 ; (-4172,-4097) d=5847
Dragonqueen      n=3           (2625,1554) d=3050 ; (4972,-3268) d=5950 ; (5363,3318) d=6306
GoblinKing       n=4           (5120,1088) d=5234 ; (3584,3840) d=5253 ; (2752,-4544) d=5312 ; (-4928,4096) d=6408
Mistlands_DvergrBossEntrance1  n=5  (4288,-4672) d=6341 ; (-4032,-6848) d=7947 ; (-8192,-3840) d=9047 ; (9280,1280) d=9368 ; (-8896,3072) d=9411
FaderLocation    n=3           (-1152,-9024) d=9097 ; (2944,-9152) d=9614 ; (-5376,-8448) d=10013
DN_Bossroom      n=3           (-1344,9344) d=9440 ; (320,9536) d=9541 ; (-3456,9088) d=9723
Vendor_BlackForest n=10        nearest (1545,1986) d=2516 ; farthest (-9390,-2564) d=9734
Hildir_camp      n=10          nearest (-2950,-634) d=3017 ; farthest (1530,4610) d=4857
BogWitch_Camp    n=10          nearest (-1726,2618) d=3136 ; farthest (-1343,5689) d=5846
PlaceofMystery1/2/3            (3581,-8443) d=9171 ; (-2247,-8826) d=9108 ; (968,-9216) d=9267
```

Note that several coordinates are exact multiples of 64 (5120, 1088, 3584, −4032): those are
locations whose `max(m_exteriorRadius, m_interiorRadius) ≥ 32`, so `GetRandomPointInZone`'s
`Random.Range(-32+r, 32-r)` has a degenerate or inverted interval.

> **Corrected — the "~40%" figure is wrong by a factor of six.** Re-counted over all 12,314 instances
> in the save: **811 have both x and z an exact multiple of 64, i.e. 6.59 %** (816, or 6.63 %, have
> x alone). For `r == 32` exactly, `Range(0f, 0f)` is degenerate and the point *is* the zone centre
> regardless of the implementation; the open question therefore affects only types with `r > 32`, and
> it governs at most a few per cent of coordinates, not 40 %. It is still worth settling, because it
> also decides (a) whether a draw is consumed from the per-type RNG stream — which shifts *every*
> subsequent coordinate of that type — and (b) whether the T2b bound of §3.2.4 exists at all for
> types with `r > 64`.

**Unverified:** what `UnityEngine.Random.Range(float min, float max)` returns when `min > max`, and
whether it consumes a draw — native `extern`, dumper input.

*(Fixture provenance re-verified by the reviewer against the live save: every `n=` above matches, the
`StartTemple` and `Eikthyrnir` `placed` flags match, `Vendor_BlackForest` / `Hildir_camp` /
`BogWitch_Camp` each have 10 candidates and **0 placed**, and `Hildir_camp` has two candidates tied at
d = 3017 — (−639, −2949) and (−2950, −634) — so "nearest" needs the documented tie-break, not a
coordinate. Across the whole save, 44 instances are `placed` across 17 prefabs.)*

The two blocking unknowns for everything from T4 up remain `Mathf.PerlinNoise` and
`UnityEngine.Random` (both native, both `extern`). The dumper plugin's output is a hard dependency of
V2/V3; until V3 passes bit-exactly, `vseed` must run in a mode that refuses to emit T4/T5 results and
says why.

---

## 6. Map rendering

### 6.1 What an offline map must show to beat a screenshot

Layers, bottom to top, each independently toggleable:

1. **Water** — every cell with height < 30. Depth-shaded: `t = clamp01((30 − h)/120)`, lerp
   `#3E6E8C → #10203A`. Flat blue hides the whole shape of the sea floor and the river network.
2. **Biome fill** — the §0.5 palette, exactly the bytes the game uses, so the offline map and the
   in-game map read as the same world. Mountain and DeepNorth must be separated from Ocean here
   (`#FFFFFF` for Mountain, `#E8F0FF` for DeepNorth — our choice, since the game cannot tell them
   apart and a white-on-white map is useless). Record the deviation in the legend.
3. **Hillshade** — from the S3 height field, standard Lambert shading with a light at azimuth 315°,
   altitude 45°, gradient from the 4-neighbour differences, multiplied over the fill at ~35% strength.
   This is what makes mountains, valleys and river beds legible.
4. **Contours** — every 25 m up to 200 m, every 50 m above, thin and 40% alpha; a bolder line at 30 m
   (the shoreline). Marching squares on the height field.
5. **Rivers and streams** — already implicit in the heights (they carve to 24–28 m), but draw the
   `GetRivers()` / `GetStreams()` centre-lines at 20% alpha as an optional overlay: it shows
   navigability, which shading alone does not.
6. **Lava** — Ashlands cells with `mask.a > 0.6` (the `ZoneSystem.IsLavaPreHeightmap` test) in
   `#FF5A1E`, additive.
7. **Rings and grid** — circles at r = 2000/4000/6000/8000/10000 (thin) and at 10500 (the water edge),
   the Ashlands ring `|(x, z−4000)| = 12000` and the Deep North ring `|(x, z+4000)| = 12000` dashed,
   a 1 km grid with labels, the origin cross, and a scale bar. Coordinates in the game's (x, z)
   convention with north = +z up.
8. **Markers** — bobmitch's categories, driven by the prefab→category table derived from the asset
   paths (`Assets/world/Locations/<Biome>/…`) plus an explicit override list:
   Spawn, Bosses, Traders, Places of Mystery, Dungeons, Camps & Ruins, Ore/Resources & Hazards,
   Landmarks, Runestones, Scenery. Each marker: category icon, tooltip with prefab name, (x, z),
   distance from centre and from spawn, and — for `m_unique` types — a hollow outline plus the word
   "candidate".
9. **Highest peaks** — top 20 local maxima, labelled with height.
10. **Save overlay** (optional) — explored fog from the `.fch`, player pins, and placed structures.

Legend, seed text, int seed, `worldGenVersion`, grid spacing, engine version and the spoiler level all
rendered into the image footer, so a shared PNG is self-describing.

### 6.2 Resolutions and cost

The natural resolution is **2048 × 2048 at 12 m/px** — the game's own grid, exactly one PNG pixel per
`AltBiomeWorldData` point, covering ±12,288 m. Offer:

| Output | grid | px | covers | field cost 1T | field cost 16T | notes |
|---|---|---|---|---|---|---|
| thumbnail | 96 m | 256² | full world | 8.7 ms + heights | ~2 ms | for result grids |
| standard | 24 m | 1024² | full world | ~0.30 s | ~0.03 s | good screen map |
| **native** | **12 m** | **2048²** | full world | **1.12 s** | **0.11 s** | matches the game; default |
| detailed | 6 m | 4096² | full world | ~4.5 s | ~0.43 s | print/poster |
| zoom | 1–2 m | any | `--zoom x,z,r` | ∝ area | | base planning |

Field evaluation dominates; shading, contours and PNG encoding add roughly 0.2–0.6 s for 2048²
(ImageSharp, single-threaded encode) — so **a full native-resolution map is ~1.5 s end-to-end on this
machine**, against the game's own 4,265 ms for just the field. Rendering the top 10 results of a search
costs ~15 s. Tile the field evaluation by rows across threads; it is embarrassingly parallel and the
buffers are already per-worker.

Caching: key the rendered field by `(seed, genVersion, grid, engineVersion)` and keep the raw
`byte[] biome` + `float[] height` next to the PNG, so re-rendering with different layers or markers is
free.

---

## 7. Open risks

1. **`Mathf.PerlinNoise` and `UnityEngine.Random` are native and unverified.** Everything above T2b's
   *shape* depends on reproducing them bit-exactly. If the Perlin normalisation differs by one ULP at
   a threshold (`base <= 0.02f`, `base > 0.4f`, `noise > 0.4f`), single cells flip biome — usually
   harmless for area metrics, potentially fatal for a location that sat on exactly that cell.
   Mitigation: V3/V4/V5 and a "threshold proximity" report that counts how many G12 cells sit within
   1e-6 of a decision boundary (a per-seed fragility score, worth exposing to the user).
2. **`Random.Range(min, max)` with `min > max`** is unverified (§5.6). *Corrected:* it does **not**
   decide ~40% of location coordinates — 6.59 % of the 12,314 instances in the reference save sit
   exactly on a zone centre, and the `r == 32` majority of those is degenerate rather than inverted.
   What it does decide is whether a draw is consumed (shifting the whole per-type RNG stream) and
   whether T2b has any bound at all for types with `maxRadius > 64` (§3.2.4 step 3).
3. **`m_unique`, `m_centerFirst`, `m_quantity`, `m_biome`, `m_minDistance`, `m_exteriorRadius`,
   `m_interiorRadius`, `m_prioritized`, `m_minAltitude`, `m_minDistanceFromSimilar`, `m_group`… are
   asset data**, only partially recovered here (quantities for 29 types from the log — 25 ×
   `Failed to place all …` plus 18 × `took more than 0.5 seconds …`, union 29 — and candidate counts
   for 176 prefabs from a save). T2b's validity per type depends on **`m_centerFirst` *and*
   `max(m_exteriorRadius, m_interiorRadius) ≤ 64`** (§3.2.4); either flag wrong turns a `SOUND-BOUND`
   filter into a correctness bug. Gate T2b on dumped values, per type, and default to "no bound".
4. **T5 cost is an estimate** (3–10 s/seed) extrapolated from the game's 33.4 s on Mono. If the real
   figure is 30 s, every T5 number in §4.5 is 5× worse.
5. **Location placement is not a pure function of the seed** (already-generated zones, mod/version
   list order, unique winners, unseeded rotations). Results are exact only for a fresh, unmodded
   world at `locationVersion 32` with the shipped 183-type list in the shipped order — and "the
   shipped order" is specifically `m_locations.OrderByDescending(a => a.m_prioritized)` after dropping
   `!m_enable || m_quantity == 0`, a **stable** sort, so it is prioritized-first then original list
   order (`ZoneSystem.GenerateLocationsTimeSliced()`). Per type the RNG is re-seeded
   `Random.InitState(WorldGenerator.GetSeed() + location.m_prefab.Name.GetStableHashCode())` and the
   ambient state is saved and restored, so a type's *draw sequence* is order-independent; what depends
   on order is which zones are already occupied and what `HaveLocationInRange` finds.
6. **Alt-biomes** (`Fortress Mountain`, the `_1`/`_leet` location variants) are live and
   under-specified here; ignoring them makes T5 disagree with the game.
7. **Game updates invalidate everything.** Pin the build: `assembly_valheim.dll` SHA-256 plus
   `locationVersion` plus `worldGenVersion`, checked into every manifest, and refuse to merge stores
   across builds. Run `tools\check-game-version.ps1` on startup.
8. **The parallel factor is 10.5×, not 16×** on this CPU. Estimates built on 16× will be 50% optimistic.

---

## 8. Open questions

Raised by the independent review; each is a thing the document asserts, implies or needs, that the
evidence on this machine cannot settle.

1. **What `UnityEngine.Random.Range(float min, float max)` does when `min > max`, and whether it
   consumes a draw.** Blocks §3.2.4 step 3 for every type with `maxRadius > 64`, and shifts the whole
   per-type coordinate stream if the draw count differs. Dumper probe: call `Range(68f, -68f)` in a
   loop, log the values and the `Random.state` delta against `Range(-68f, 68f)`.
2. **`max(m_exteriorRadius, m_interiorRadius)` per location type.** T2b's constants `Δ∞ = 64`,
   `Δ = 90.51` are valid only where this is ≤ 64. Until it is dumped, T2b must be off for every type.
   Dumper: `ZoneSystem.m_locations[i].m_exteriorRadius / m_interiorRadius` after `SetupLocations`.
3. **`m_unique` and `m_centerFirst` per type** (carried over from the original list; still open).
4. **Whether the six `LocationList` prefabs really contribute 3/2/27/4/25/25 locations**, and how
   many of the 183 come from `AltBiome.m_addLocations` (§0.6). Not present in the surviving log.
5. **Whether `SwampHut1_1` / `SwampHut2_1` / `SwampHut3_1` are `AltBiome.m_addLocations` entries**
   (§0.7): they placed 33/2/2, which the alt-biome gate would normally prevent for an alt-biome that
   placed in zero sectors.
6. **The real per-point cost of the river lookup.** §3.2 charges T4 `+0.18 s over T3`, which is the
   whole of the S2 pre-generation estimate and leaves ~0 for 4.19 M `GetRiverWeight` calls (each a
   `Vector2i` dictionary lookup under a `ReaderWriterLockSlim` plus a scan of that cell's
   `RiverPoint[]`). Either the T4 figure is optimistic or the lookup is free; measure it.
7. **The stream acceptance probabilities on `GetPregenerationHeight`**, not on the post-generation
   `cacheMinimapHeight` used in §4.3. Different function; the 0.0814 / 0.0555 figures are a proxy.
8. **The §2.2 table on the §2.1 `G_r` point set** rather than on G12 decimated by k (the two point
   sets differ by half a coarse cell).
9. **`Mathf.PerlinNoise`'s gradient set, fade curve and normalisation** (carried over; the
   permutation table at `UnityPlayer.dll+0x1BE0BC0` is now confirmed, the rest is not).
10. **The real T5 cost in a .NET port** (carried over), now without the `HaveLocationInRange`
    speed-up the original section assumed — see §4.4.

---

## Verification

Independent adversarial check of this document against `scratchpad/decomp/*.cs` (ILSpy output for
this build), `BepInEx\LogOutput.log`, the read-only minimap cache of world `asdasdasd`, its
`_main.3.db2`, and `UnityPlayer.dll`. `check-game-version.ps1` reports **OK** — Valheim 1.0.15,
network 40, Steam build 25390630, `assembly_valheim.dll` SHA-256 `59f53fb5…33adb1`, matching the KB
stamp. No file outside the scratchpad was written; both save locations were opened read-only.

**Re-derived and confirmed unchanged:** the `GetStableHashCode` two-lane structure and all five hash
samples (recomputed: `a`→372029373, `abc`→1099313834, `HHcLC5acQt`→298112588, `""`→371857150,
`MWd8eV6svz`→−1772362158); `sum(62^k, k=1..10) = 853,058,371,866,181,866`;
`AltBiomeWorldData.MapSpaceToWorldSpace` and the `110250000` cutoff; the minimap cache sizes, the
2048 × 12 m grid (rows *and* columns 1023/1024 non-`−400` over indices 149..1898), the meta record,
all seven biome `Color32` values and all four dominant mask colours; the §2.2 island table (all five
rows reproduced exactly, independently); `P(26<h<31) = 0.081384`, `P(36<h<44) = 0.055463`; total land
117.69 km²; the origin cell height 23.09375 m; 12,314 instances / 176 prefabs / locationVersion 32 /
112 zones / 44 placed across 17 prefabs, and every `n=` in the §0.6 and §5.6 fixtures; the traders'
10-candidates-0-placed; `Genloc duration: 33417.005 ms … locations: 183`, the 18 slow-location lines
and the 25 `Failed to place all` lines (union: 29 known `m_quantity` values, as claimed); the T0
biome distance bands; the `AddRivers` monotonicity argument; `GetBiomeHeight` never reading its
`BiomeSector`; `GetZone` / `GetZonePos` / `GetRandomPointInZone` / `IsLavaPreHeightmap`;
one-location-per-zone; `DungeonGenerator.GetSeed` depending on `transform.position`; the Perlin
permutation table at `UnityPlayer.dll+0x1BE0BC0`; and every arithmetic cell of the §4.5 throughput
table except the T5 row.

**Corrected in place, with evidence, in rough order of how badly each would have hurt:**

1. §3.2.4 — `RandomBiomeFromBiomes`'s reachable set is **not** a subset of `m_biome`. Exhaustive
   enumeration over all 511 masks: 120 of them can return `BlackForest` via the Plains chain entry
   when BlackForest is not in the mask. Using `m_biome` alone as `S_L` was therefore *not*
   conservative and would have rejected matching seeds. The bound is now taken over
   `m_biome ∪ {BlackForest if the Plains bit is set}`.
2. §3.2.4 — step 3 of the T2b proof (`|point − zoneCentre|∞ ≤ 32`) holds only for
   `maxRadius ≤ 64`; general form `Δ∞ = 32 + |32 − maxRadius|` added, and the whole bound made
   conditional on an unverified `Random.Range` behaviour.
3. §3.2.4 — `ceil(cells/29) < k` was labelled "tighter but still sound"; it is **unsound** (it uses a
   lower bound on zone count where rejection needs an upper bound). Removed. The constant was also
   wrong: at most 36, not 29, G12 points fit in a 64 m zone.
4. §0.2 — `33^-1 mod 2^32` is `0x3E0F83E1`, not `0xB3CB1B4D` (whose product with 33 is 758,023,405).
   The 3-char forward table has **64,726** distinct keys, not 59³ = 205,379, and the per-lane-B-draw
   success rate is **1.54 %**, not 16.6 %; the failure-probability argument was restated over the
   ~6.6×10⁷ *distinct* lane-B values. Inverter re-run with the corrected constant: 300/300 targets
   inverted and re-hashed. The "11/12-char seeds strictly enlarge the reachable set" claim was also
   corrected — the lane chain is not monotone.
5. §5.6, §7.2 — "~40 % of instance coordinates are exact multiples of 64" is **6.59 %** (811/12,314).
6. §4.4 — `HaveLocationInRange` does **not** scan `m_locationInstances`; it scans the per-assetID /
   per-group caches built by `AddToCache`. The stated cause of the 18 slow types, and the proposed
   spatial index, were based on that misreading.
7. §0.5 — the Mistlands and Plains distance bands quoted as evidence for the palette were wrong
   (6199..9559 → **5900..10000**; 3278..7682 → **2902..8000**), and the Swamp band is
   `0.137 + [0, 0.03]`, measured 24.31..33.94 m, not "27–31 m … ± 0.03".
8. §5.2.3 — `Version.World.WorldGenVersion` is **26** (a save-format enum member); the constant to
   pin in the manifest is `Version.c_WorldGenVersion` = 2.
9. §1.1 — "`GetBiome` never consults rivers" is true only at the default `waterAlwaysOcean: false`.
10. §1.1 — S0's seven draws are in the order `offset0..3, riverSeed, streamSeed, offset4`; `offset4`
    is drawn last, not fifth.
11. §4.1 — `GetHeight` on Mountain is **55** Perlin calls, not "48+"; the Ashlands row is itemised
    (4 of its 8 own Perlin calls are dead code); Swamp is 13.
12. §4.2 — `4.26 M/s` is 235 ns for the *whole* call (height part ≈ 101 ns), and the 3.8× Mono/.NET
    comparison is not like-for-like (`GenerateWorldMap` also runs `GetMaskColor` per pixel, including
    a full extra `GetAshlandsHeight` on 1.36 M cells).
13. §4.3 — the surrogate over-counts stream evaluations by **1.29×**, not 1.35×.
14. §4.5 — the T5 row's 1T rate is 0.09–0.23 seeds/s (its own "T4 + 3…10 s" implies that, not
    0.1–0.3).
15. §0.1 — average strings per world is 198,618,130, not 198,617,432.
16. §0.6 — the save is the newest `_main.<n>.db2` (now `_main.3.db2`), not a literal `_main.2.db2`.

**Added because it was material and establishable:** `GetBiomeHeight`'s
`* (200f · CreateAshlandsGap · CreateDeepNorthGap)` factor and the `>10500 → −400` early return
(§1.1), without which §3.2.5's proof is incomplete and §2.2's land test is undefined; the static
seed-0 `FastNoise` (§1.1); the exact per-biome `GetMaskColor` rules including the 1.15 / 0.8 forest
thresholds (§0.5), without which V6 cannot be written; the `cheap:true` vs `cheap:false` split
between the minimap mask and `IsLavaPreHeightmap`, and the discarded Deep North stream list (§1.2);
the `worldGenVersion` dependence of the entire T0 table (§3.2.1); `GetRandomZone`'s outward spiral
(§3.2.4); the definition of "183 types" and the stable prioritized-first ordering (§0.6, §7.5); the
`attempts = m_prioritized ? 60000 : 12000` constant (§4.4).

**Marked Unverified:** the `3+2+27+4+25+25 = 86` `LocationList` breakdown (absent from the surviving
log and not reconcilable with 183); the attribution of the `_1`/`_leet` variants to
`AltBiome.m_addLocations`; the §2.2 table on the true `G_r` point set; the stream acceptance
probabilities as proxies for `GetPregenerationHeight`; `Random.Range` with `min > max`;
`Mathf.PerlinNoise`'s gradient set, fade curve and normalisation. All of §4.2's throughput numbers
remain the engineer's surrogate measurements and were checked only for internal arithmetic
consistency, not re-measured.

*checked by an independent reviewer*
