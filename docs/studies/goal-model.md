# SeedLab — the complete goal model, its costs, and its two front ends

Analysis written 2026-09-23. **Read-only:** nothing under
`E:\SteamLibrary\steamapps\common\Valheim\_ModSource\SeedLab` was edited. Every binary run came from a
private copy of the Release output at
`...\scratchpad\searchanalysis\bin\vseed.exe`
so that no build of the live tree was blocked (`docs\web.md`: a running `vseed` locks `bin\Release\`).

## 0. Measurement conditions — read this before trusting any millisecond here

Every timing below was taken while **the machine was saturated by other agents**: `LoadPercentage`
reported 96 % and then 100 %, with 17 `dotnet` processes and two *other* `vseed` processes resident
(`Get-CimInstance Win32_Processor`, `Get-Process`). So:

- **Absolute wall-clock numbers here are inflated and must not be quoted as the machine's speed.**
  Measured drift: the identical one-goal calibration (`location:StartTemple`, `--threads 1
  --calibrate 2`) returned **6177.5 ms**, then **3862.2 ms**, then **3163.2 ms** per seed within
  about ten minutes — a 2.0x spread from contention alone.
- **Ratios taken inside one back-to-back batch are stable and are what this document argues from.**
  The location-prefix ratio prefix-183 / prefix-1 came out **25922.5 / 6177.5 = 4.20x** in the first
  batch and **16237.3 / 3862.2 = 4.20x** in a second batch taken under different load. Two independent
  contention regimes, same ratio to three significant figures.
- Per-seed *work counts* (grid cells, ordered prefix length) are contention-free and are given
  wherever they exist.

Commands are shown with their output throughout. Re-measure on an idle machine before publishing any
absolute figure: `vseed search <query> --dry-run --calibrate N` is exactly the tool for it, and it
already prints the constants it used.

Source of truth for the reference site's goal model: the capture supplied with this task
(valheim.gaming.tools/seeds, same day). I did not re-fetch the page; where this document quotes their
wording it is quoting that capture.

---

## 1. The goal catalogue

### 1.0 How to read the table

| column | meaning |
|---|---|
| **tier** | the cheapest tier that answers the goal *exactly* (`src\SeedLab.Search\Criteria\MetricCatalog.cs` lines 20-42). The **query's** tier is the maximum over its goals — this is the single most important cost fact in the tool. |
| **cost class** | `instant` / `cheap` / `moderate` / `expensive` / `very expensive`, defined in §4.1 by measured ms per seed per thread. |
| **exactness** | `exact@grid` = the metric *is defined* on the sampling grid, so the answer is exact for that grid and the grid is part of the definition; `exact` = independent of the grid; `resolution-critical` = coarsening does not merely add error, it can change the answer by an order of magnitude (§1.6 has the measurements). |
| **status** | `now` = works in this build today; `after-integration` = the engine can compute it but the metric is not wired into `MetricCatalog`; `after-dump` = needs `tools\SeedLab.Dumper` data (present in this tree at `data\1.0.15-59f53fb5\`, so `now` in practice once `SEEDLAB_DATA_DIR` is set); `impossible` = with a stated reason. |

The tiers, with what each forces the engine to pay (`Evaluation\SeedEvaluator.cs` lines 168-260):

| tier | what runs | pays lake/river/stream pre-generation? |
|---|---|---|
| T0 | static analysis of the query against fixed generator geometry | no — no seed is touched |
| T1 | sparse accept-only probe | no |
| T2 | `GetBiome` over the query grid, restricted to the region of interest | **no** — `GetBiome` reads `GetBaseHeight`, never `GetHeight` |
| T3 | `GetBiomeHeight` over the grid | **yes** |
| T4 | the pre-generated river/lake/stream structures themselves | yes (it *is* the structures) |
| T5 | ordered location placement | pays the 2048² biome-point grid `GetRandomPointByBiomes` draws from |

**The T2 → T3 step is the big one and it is not about the grid.** Measured, contended:

```
$ vseed search q-biome-area384.json --dry-run --calibrate 3 --threads 2   # T2, G384, whole world
  per seed              12.7 ms
    of which pre-gen    0.0 ms  (0.00 %) on 0 of 3 seeds
    of which sampling   12.7 ms  (99.90 %)

$ vseed search q-islandcount384.json --dry-run --calibrate 3 --threads 2  # T3, G384, whole world
  per seed              615.9 ms
    of which pre-gen    599.0 ms  (97.26 %) on 3 of 3 seeds
```

Adding *one* height goal to a G384 query multiplied the per-seed cost by **48x**, and **97 %** of the
new cost is the pre-generation, not the extra sampling. Corollary the UI must state: once a query has
*any* height, river, lake or stream goal, **further height goals are nearly free** — the expensive
thing has already been bought.

### 1.1 Biome goals — the nine biomes (T2, the workhorse)

Targets: `biome:Meadows | BlackForest | Swamp | Mountain | Plains | Mistlands | AshLands | DeepNorth |
Ocean` (`MetricCatalog.cs` lines 259-270).

| metric | definition | unit | tier | cost | exactness | params | default | status |
|---|---|---|---|---|---|---|---|---|
| `present` | 1 when >= 1 grid cell of the biome exists inside the 10,500 m water edge, else 0 | count | T2 | cheap | exact@grid | — | — | now |
| `nearest_distance` | Euclidean distance from the origin (0,0) to the nearest cell **centre** of the biome; `+inf` when absent | m | T2 | cheap | exact@grid, floored at `grid/sqrt(2)` | `from` | `center` | now |
| `area` | (cells of the biome) x grid² inside the water edge | m² | T2 | moderate | exact@grid | — | — | now |
| `share` | biome cells / in-world cells | fraction | T2 | moderate | exact@grid | — | — | now |
| `area_within` | biome area inside a disc of `radius` around the origin | m² | T2 | cheap–moderate | exact@grid | `radius` | 2,000 m | now |
| `largest_patch_area` | largest 4-connected component of the biome x grid² | m² | T2 | moderate | **resolution-critical** | — | — | now |
| `land_area` | biome area with `GetBiomeHeight >= 30 m` | m² | **T3** | expensive | exact@grid | — | — | now |
| `area_above_height` | biome area above `height` m | m² | **T3** | expensive | exact@grid | `height` | 300 m | now |

Reference-site parity: their "biomes (all nine)" goal is `present` / `nearest_distance` / `share`.
We have all three, plus `area`, `area_within`, `land_area`, `area_above_height` and
`largest_patch_area`, which they do not offer.

T0 can reject a biome goal for the whole space before any seed runs, using the generator's fixed
radial bands (`Feasibility\StaticAnalysis.cs` lines 26-60, bands in `Feasibility\BiomeGeometry.cs`).
What that catches: "AshLands within 4 km of centre" is impossible for every one of the 4,294,967,296
seeds, and the tool says so in a microsecond instead of after a week.

### 1.2 World-shape goals

| metric | definition | unit | tier | cost | exactness | params | default | status |
|---|---|---|---|---|---|---|---|---|
| `ocean_share` | Ocean cells / in-world cells | fraction | T2 | moderate | exact@grid | — | — | now |
| `land_area` | cells with height >= 30 m, x grid², inside r <= 10,500 | m² | T3 | expensive | exact@grid | — | — | now |
| `water_area` | in-world cells below 30 m | m² | T3 | expensive | exact@grid | — | — | now |
| `land_share` / `water_share` | the above / in-world cells | fraction | T3 | expensive | exact@grid | — | — | now |
| `land_area_within` | land area inside a disc of `radius` | m² | T3 | expensive | exact@grid | `radius` | 2,000 m | now |
| `nearest_land_distance` | origin to nearest land cell centre | m | T3 | expensive | exact@grid, floored at `grid/sqrt(2)` | — | — | now |
| `highest_peak` | max sampled height | m | T3 | expensive | exact@grid | — | — | now |
| `deepest_point` | min sampled in-world height | m | T3 | expensive | exact@grid | — | — | now |
| `area_above_height` | any-biome area above `height` | m² | T3 | expensive | exact@grid | `height` | 300 m | now |
| `island_count` | 4-connected land components of area >= `min_area` | count | T3 | expensive | **resolution-critical** | `min_area` | 10,000 m² (1 ha) | now |
| `largest_island_area` | largest 4-connected land component | m² | T3 | expensive | **resolution-critical** | — | — | now |
| `spawn_island_area` | the land component containing `StartTemple`; failing that the component whose nearest cell centre is closest to (0,0); 0 when that is > 500 m away (spec 07 §2.3) | m² | T3 (T5 for the `StartTemple` anchor) | expensive → very expensive | **resolution-critical, worst case in the tool** | — | — | now (rule 2); rule 1 after-integration |
| `coastline_length` | land/water 4-adjacency edges x grid | m | T3 | expensive | **grid-defined fractal — never comparable across grids** | — | — | now |
| `river_count` | rivers from `WorldGenerator.PlaceRivers` | count | T4 | expensive | **exact** (not a grid measurement) | — | — | now |
| `lake_count` | merged lake points from `FindLakes`/`MergePoints` | count | T4 | expensive | **exact** | — | — | now |
| `stream_count` | streams from `PlaceStreams` pass 1 (the Deep North pass is discarded by the game) | count | T4 | expensive | **exact** | — | — | now |

The three T4 metrics are worth calling out: they are the only *shape* goals in the tool that are
**exact independent of the grid**, because they read the generator's own structures rather than a
resampling of them. They are nearly free once any height goal has been paid for:

```
$ vseed search q-rivers.json --dry-run --calibrate 3 --threads 2
  highest tier needed   T4 rivers
  per seed              631.0 ms
    of which pre-gen    623.8 ms  (98.86 %)
    of which sampling   7.1 ms  (1.13 %)
```

Reference-site parity: they offer "Island count", "Spawn island size", "Largest island size" with a
"Measure world shape within (km)" radius and a "Minimum island size (km²)". We map those onto
`island_count` (+`min_area`), `spawn_island_area`, `largest_island_area`, and their radius onto a new
`measure_within` parameter (§1.7). They have no river, lake, stream, peak, coastline or land-share
goal at all.

### 1.3 Location goals — bosses, traders, dungeons, world features (T5)

Target forms: `location:<PrefabName>` for one prefab, `group:<name>` for a curated set
(`Locations\LocationGroups.cs`). Six metrics, each available for both forms
(`MetricCatalog.cs` lines 212-239):

| metric | definition | unit | exactness | params |
|---|---|---|---|---|
| `nearest_distance` | origin -> nearest instance of any prefab in the target; `+inf` when none, so a "far" goal passes on absence | m | **exact** | `from` |
| `all_candidates_distance` | origin -> the **furthest** instance; "near D" therefore means every one is within D | m | **exact** | `from` |
| `all_types_distance` | the largest of the per-prefab nearest distances — how far out you must go to have reached **every** type in the group; `+inf` when one is missing | m | **exact** | `from` |
| `count` | instances of the target in the whole world | count | **exact** | — |
| `count_within` | instances inside `radius` | count | **exact** | `radius` |
| `types_within` | how many **distinct** prefabs of the target have >= 1 instance inside `radius` | count | **exact** | `radius` |

**Location goals are exact, full stop — the sampling grid does not enter them.** That is the largest
capability gap against the reference site, whose finest biome sampling is 128x128 ~ 164 m per sample
and who therefore cannot run placement at all; they can only estimate.

**`m_unique` semantics are part of the definition and must be shown.** Haldor (`Vendor_BlackForest`,
`quantity 10`), Hildir (`Hildir_camp`, `quantity 10`), the Bog Witch (`BogWitch_Camp`, `quantity 10`)
and the three Places of Mystery are `unique: true` (verified directly in
`data\1.0.15-59f53fb5\locations.json`). Their instances are **candidates**, not placements: the game
keeps exactly one, decided by which zone a player generates first, which is not a function of the
seed. `MetricCatalog.cs` lines 202-210 defines four different honest readings and stamps the right one
on every record. The UI must print it verbatim next to any trader goal.
`all_candidates_distance` is the only metric that **guarantees** a distance for a trader.

#### 1.3.1 Ordered index and prefix cost — what actually sets a location query's price

A location query must run the ordered placement list from entry 0 up to the **latest** entry it needs
(`Locations\ILocationOracle.cs`, `LocationPlan.PrefixLength`). The tool's own plan text gives the
reason: "Each type's RNG stream is seeded from the world seed plus its own prefab-name hash, so
nothing AFTER a type can change it — but zone occupancy is global and the AssetID / group /
CountNrOfLocation buckets are shared, so nothing BEFORE it can be skipped."

Ordered indices read from `data\1.0.15-59f53fb5\locations.json` (232 entries, 183 enabled and ordered):

| group / prefab | ordered index of latest member | prefix | reference-site equivalent |
|---|---|---|---|
| `location:StartTemple` | 0 | 1 | — (they have no spawn anchor) |
| `group:bosses_classic` (Eikthyr, Elder, Bonemass, Moder, Yagluth) | 7 `Dragonqueen` | **8** | 5 of their 7 bosses |
| `group:sunken_crypts` | 5 | 6 | Sunken Crypts |
| `group:infested_mines` | 10 | 11 | Infested Mines |
| `group:bosses` (all seven, incl. the Queen and Fader) | 16 `FaderLocation` | **17** | bosses |
| `group:hildir_quests` | 15 | 16 | Hildir Trials |
| `group:places_of_mystery` | 19 | 20 | — |
| `group:charred_fortresses` | 20 | 21 | Charred Fortresses |
| `group:traders` (Haldor, Hildir, Bog Witch) | 21 `BogWitch_Camp` | **22** | traders |
| `group:boss_rooms` (+ `DN_Bossroom`) | 22 | 23 | — |
| `group:surtling_geysers` (`FireHole`) | 29 | 30 | Surtling Geysers |
| `group:fuling_villages` (`GoblinCamp2`) | 37 | 38 | Fuling Villages |
| `group:troll_caves` | 61 | 62 | Troll Caves |
| `group:burial_chambers` (Crypt2/3/4) | 66 | 67 | Burial Chambers |
| `group:tar_pits` (TarPit1/2/3) | 105 | 106 | Tar Pits |
| `group:frost_caves` (`MountainCave02`) | 106 | **107** | Frost Caves |
| `group:dungeons` (all five kinds) | 106 | **107** | — |
| `group:fuling_villages_with_alt` | 176 | **177** | — |
| `group:tar_pits_with_alt` | 182 `TarPit3_1` | **183 (everything)** | — |

Measured cost against that prefix, one thread, `--calibrate 2`, all contended. The two batches are
separated because contention differed; **compare within a batch, not across**:

| prefix | query | batch A ms/seed | batch B ms/seed |
|---|---|---|---|
| 1 | `location:StartTemple` | 6177.5 | 3862.2, then 3163.2 (same query, twice) |
| 8 | `group:bosses_classic` | 4250.0 | 3372.0 |
| 22 | `group:traders` | 3969.5 and 4778.3 | — |
| 30 | `group:surtling_geysers` | 5951.5 | — |
| 32 | `location:Crypt2` | 6208.2 | — |
| 38 | `group:fuling_villages` | 6730.4 | — |
| 107 | `group:frost_caves` | 9803.2 | — |
| 107 | `group:dungeons` | 10230.3 | — |
| 183 | `group:tar_pits_with_alt` | 25922.5 | 16237.3 (`location:TarPit3_1`) |

Three conclusions the UI must carry, all robust to the contention:

1. **A location query has a large fixed cost the prefix does not change.** Prefix 1 costs the same as
   prefix 32 inside a batch. The tool already names the cause: "the 2048² biome/height point grid that
   `GetRandomPointByBiomes` draws from is most of that and is not optional; the ordered prefix only
   shortens the rest."
2. **The marginal cost of the prefix appears only past about entry 40**, and the tail from 107 to 183
   is where it explodes.
3. **prefix-183 / prefix-1 = 4.20x, measured twice under different load.** So asking for tar pits
   *with* the alt-biome variants costs **4.2x** what asking for bosses costs. `tar_pits_with_alt` and
   `fuling_villages_with_alt` must carry that in the picker, and the engine already emits it:
   "this query reaches ordered entry 183 of 183, so every seed pays for the FULL location placement".

### 1.4 Goals only an exact engine can offer — proposed, with status

These are worth building *because* SeedLab reproduces the generator bit-for-bit and the reference site
only samples a 128x128 grid.

| proposed goal | definition | tier | cost | exactness | status |
|---|---|---|---|---|---|
| `world:spawn_island_area` anchored on **`StartTemple`** (rule 1, spec 07 §2.3) | the land component containing the actual spawn stone, not the component nearest (0,0) | T3 + T5 prefix 1 | very expensive | resolution-critical for the component, exact for the anchor | **after-integration** — `DistanceOrigin.Spawn` exists (`QueryModel.cs` line 110), the metric does not use it |
| `from: spawn` on **every** distance metric | measure from the spawn stone instead of (0,0) — what a player actually walks | promotes the goal to T5 prefix 1 | very expensive | exact | **now** for location metrics; **after-integration** for biome/world metrics |
| `world:progression_radius` | the radius containing the nearest altar of every boss in a set — one number for "how compact is this playthrough" (spec 07 §2.4) | T5 prefix 8 | very expensive | exact | after-integration |
| `world:island_biome_mix` | per-biome cell counts inside the spawn (or largest) island — "a starting island with Meadows *and* Black Forest *and* no Plains" | T3 | expensive | resolution-critical | after-integration |
| walkability qualifier: `same_island_as: spawn` on any location goal | the target is reachable without a boat | T3 + T5 | very expensive | resolution-critical (it is a connectivity question) | after-integration |
| `world:river_length_total`, `world:longest_river` | from the `PlaceRivers` point list already in memory at T4 | T4 | free once T3/T4 paid | **exact** | after-integration |
| `world:lake_area`, `world:largest_lake` | from `FindLakes`/`MergePoints` output | T4 | free once T4 paid | **exact** | after-integration |
| `biome:<B>.area_on_spawn_island` | biome area restricted to the spawn component | T3 | expensive | resolution-critical | after-integration |
| `world:massif_count` | components of `height >= h` of area >= `min_area` — "how many silver-bearing massifs" | T3 | expensive | resolution-critical | after-integration |
| `group:*.count_between(r0, r1)` | instances in an annulus — "Fuling villages far enough to be safe, near enough to raid" | T5 | very expensive | exact | after-integration |
| `location:<X>.nth_nearest_distance(n)` | distance to the n-th nearest instance — "three burial chambers within 1 km", strictly stronger than `count_within` | T5 | very expensive | exact | after-integration |
| `world:vegetation_*` | `data\1.0.15-59f53fb5\vegetation.json` is dumped but no tier consumes it | new tier below T5 | **unmeasured** | exact | after-integration — see §7 |
| `world:altbiome_*` | `altbiomes.json` is dumped and there is a golden (`goldens\altbiomes-assignment-0480A34C.json`) | T2-ish | **unmeasured** | exact | after-integration |

### 1.5 Impossible, and exactly why

| asked for | why no seed-space search can answer it |
|---|---|
| "Haldor **is** at (x,z)" | `Vendor_BlackForest` is `unique: true` with `quantity: 10`. The seed fixes ten candidate positions; which one survives is decided by **which zone a player generates first**, which is session history, not seed. Only the four candidate-set readings in §1.3 are honest. |
| the Deep North boss as a placed boss | `DN_Bossroom` (ordered 22) places, but the boss itself is not a `ZoneLocation` outcome the generator decides. |
| "this world has an ore vein at (x,z)" | ore is vegetation and destructible spawning at chunk-generation time. `vegetation.json` covers the *rules*; no placement of it has been validated by this engine. |
| the second `PlaceStreams` pass | the game discards it (`MetricCatalog.cs` line 187). A goal on it would describe something no player can see. |
| "how good is this world" as one absolute number | there is no such thing. That is what §2's *two* numbers exist to avoid pretending. |
| anything about mods, events or world modifiers | not generator output. |

### 1.6 The resolution question, measured — "is the heavy stuff worth it?"

This is the user's open question ("I do not know if there are any benefits to doing this heavy review
over the approximation ... there may be something I am missing"). **There is, and it is confined to one
family of goals.** One seed measured at six grids, everything else identical:

```
$ for g in 384 192 96 48 24 12; do
    vseed seed -1772362158 --grid $g --no-landmarks --json --threads 4 > grid-$g.json
  done
```

| metric \ grid (m) | 384 | 192 | 96 | 48 | 24 | **12 (the game's own)** |
|---|---|---|---|---|---|---|
| `land_area` m² | 1.184e8 | 1.177e8 | 1.173e8 | 1.174e8 | 1.175e8 | **1.176e8** |
| `land_share` | 0.34199 | 0.33972 | 0.33888 | 0.33898 | 0.33939 | **0.33954** |
| `Swamp.share` | 0.02853 | 0.02522 | 0.02585 | 0.02603 | 0.02610 | **0.02603** |
| `Plains.share` | 0.12990 | 0.13697 | 0.13628 | 0.13619 | 0.13600 | **0.13615** |
| `Ocean.share` | 0.27768 | 0.28459 | 0.28339 | 0.28286 | 0.28291 | **0.28287** |
| `Plains.nearest_distance` m | 2937.0 | 2965.1 | 2915.8 | 2908.9 | 2901.0 | **2901.9** |
| `Meadows.nearest_distance` m | 271.5 | 135.8 | 67.9 | 33.9 | 17.0 | **8.5** |
| `island_count` (>=1 ha) | 132 | 217 | 234 | 231 | 226 | **227** |
| `largest_island_area` m² | 6.193e6 | 5.567e6 | 5.806e6 | **7.182e6** | 5.758e6 | **5.764e6** |
| `spawn_island_area` m² | 4.571e6 | 5.235e6 | 3.843e6 | **7.182e6** | 3.661e6 | **4.892e6** |

Two more seeds, same procedure:

| seed | metric \ grid | 192 | 96 | 48 | 24 | **12** |
|---|---|---|---|---|---|---|
| 75539276 | `land_share` | 0.34440 | 0.34546 | 0.34633 | 0.34621 | **0.34584** |
| 75539276 | `largest_island_area` | 6.894e6 | 6.774e6 | 5.279e6 | 5.219e6 | **4.885e6** |
| 75539276 | `spawn_island_area` | 6.894e6 | 6.774e6 | 5.279e6 | 5.219e6 | **3.563e6** |
| -2028901234 | `land_share` | 0.35047 | 0.35137 | 0.35106 | 0.35097 | **0.35096** |
| -2028901234 | `largest_island_area` | 6.488e6 | 6.221e6 | 6.629e6 | 6.243e6 | **6.240e6** |
| -2028901234 | `spawn_island_area` | 6.488e6 | 4.756e6 | 6.629e6 | 6.243e6 | **5.118e5** |

**What that says, goal family by goal family.**

- **Area, share and land goals converge fast and are safe to approximate.** `land_share` is within
  **0.3 %** of the G12 answer at 192 m on all three seeds; biome shares are within ~3 % at 192 m and
  within ~1 % at 96 m. For these goals the coarse grid is a legitimate screen and the fine grid buys
  almost nothing. **Use 96–192 m.**
- **Distance goals have a hard floor of `grid/sqrt(2)` and are otherwise fine.**
  `Meadows.nearest_distance` in the first table is exactly `grid x 0.7071` at every row — the true
  nearest Meadows cell is essentially under the origin and the grid is simply quantising it. A *far*
  target converges normally: `Plains.nearest_distance` is within **1.2 %** even at 384 m.
  **Rule: a distance goal is sound when its threshold is comfortably larger than `grid/sqrt(2)`; the
  UI should warn when `value < 3 x grid/sqrt(2)`.**
- **Connectivity goals are not approximations at all, and this is the finding.** A coarse grid cannot
  see a water channel narrower than its spacing, so it **bridges** islands that are genuinely
  separate. `largest_island_area` is non-monotone (48 m gave 7.18e6 against G12's 5.76e6, **+25 %**),
  and `spawn_island_area` is worse — on seed **-2028901234** it reads **6.24e6 m² at 24 m and
  5.12e5 m² at 12 m, a factor of 12**. A player asking for "a big starting island" would have been
  handed a world whose spawn island is half a square kilometre. `island_count` at 384 m reported 132
  against the true 227, **42 % low**.

  **So: `spawn_island_area`, `largest_island_area`, `island_count`, `largest_patch_area` and every
  other 4-connected measurement must be computed at grid 12, or the number is not a bound, an estimate
  or an approximation — it is sometimes simply a different world's answer.**

This settles an item the project's own spec left open. `docs\specs\07-features.md` §2.2 carries a
resolution table built by *decimating* a G12 cache (indices 0, k, 2k, ...) and flags, verbatim,
"**Unverified:** the same table evaluated on the §2.1 `G_r` points". The tables above are exactly that
— real `G_r` resampling through `vseed seed --grid`, on three seeds — and they reproduce the spec's
qualitative conclusion for `largest_island` while showing that `spawn_island_area`, which the spec
does not tabulate at all, is far more fragile than `largest_island`.

**The honest recommendation for the fine-grid toggle.** Offer it, but label it by *which goals it
changes*:

> **Fine grid (12 m — the grid the game itself samples).** For a whole-world biome query it costs
> **86x** a G384 run of the same query (measured: 1088.5 ms vs 12.7 ms per seed) and 1,024x the cells.
> It changes the answer for **island count, largest island and spawn island** — where a coarse grid
> bridges narrow channels and can be wrong by more than 10x — and for a distance goal whose threshold
> is near the grid spacing. It changes almost nothing for area, share and land-fraction goals
> (< 1 % at 96 m). **If your query has no island or connectivity goal, the fine grid is buying you
> precision you cannot use.**

And the funnel that follows, which should be the default offer: **screen at 96–192 m on the area and
share goals, then re-verify the survivors at 12 m on the connectivity goals.** That is what the
reference site does in spirit ("Possible matches appear quickly; verified results improve as the
search continues") — except their verification tops out at 128x128 ~ 164 m, so their "Spawn island
size" is never verified at all. Their own help text concedes it: "Sizes are estimates."

### 1.7 Parameters that need adding for reference-site parity

| new parameter | applies to | meaning | default |
|---|---|---|---|
| `measure_within` (m) | every world-shape and biome-area goal | their "Measure world shape within (km)". Caps the disc the metric is measured in. | **10,500 m**, with their explanation reproduced: "10,500 m covers the playable world. Smaller radii clip islands at the edge." |
| `bridge_width` (m) | `island_count`, `largest_island_area`, `spawn_island_area`, `largest_patch_area` | their "river crossings up to 64 m wide stay within one island": land components separated by <= `bridge_width` of water count as one island. Implement as a morphological closing of the land mask by `ceil(bridge_width / grid)` cells before the 4-connected labelling. | **0 m (strict)** for SeedLab, with `64` offered as "match valheim.gaming.tools". It must be a *parameter*, never a hidden constant, because it moves the answer as much as the grid does. |
| `min_area` (m²) | `island_count` | already exists (`QueryModel.cs` line 108) | 10,000 m² (1 ha) |
| `from` | biome and world distance metrics | already exists for location metrics only | `center` |

`measure_within` and `bridge_width` are **not implemented today**. Note that
`SeedResult.RegionRadiusM` (`Evaluation\SeedResult.cs` lines 86-93) is a *different* thing: the disc
the engine restricted itself to for efficiency, chosen so the verdicts stay exact. A user-chosen
measurement radius is a change to the metric's definition and needs its own field.

---

## 2. The scoring model — two numbers, specified to the bit

### 2.1 What the reference site does, and what we are matching

Their model, from the capture: every goal has **How important? (Must have / Nice to have)**, a
**Weight (1 = normal)** and a **When matches are equally good** preference (*Recommended for this
goal* / *no extra preference* / *prefer closer* / *prefer farther away* / *prefer smaller values* /
*prefer larger values*). Their help text: "Preferences shape the quality score, which orders worlds
with equal match percentages. Weight changes this goal's influence on both numbers; 1 is normal. A
must-have goal always has to pass."

Three things follow and they drive the whole design:

1. **Match percentage must be binary per goal.** If it were continuous, two worlds would almost never
   have "equal match percentages" and a tie-break would be pointless. Match counts goals met, not how
   well they were met.
2. **Quality is the continuous number**, and it is what a preference shapes.
3. **Weight feeds both.**

### 2.2 What SeedLab does today, and why it is not enough

Today there is exactly one number: `SeedResult.Score` = the weighted mean of the nice-to-have
sub-scores, or **1.0 when the query has no nice goal** (`Evaluation\SeedEvaluator.cs` line 383:
`r.Score = totalWeight > 0 ? weighted / totalWeight : 1.0;`). The sub-scores are clamped linear ramps
(`Evaluation\CompiledQuery.cs` lines 237-265), so every seed that beats its threshold scores exactly
1.0 and ties with every other.

Measured consequence:

```
$ vseed search q-biome384.json --seeds 3000 --threads 2 --out rec.jsonl --progress none
$ python -c "import json; d=[json.loads(l) for l in open('rec.jsonl')]; \
             print(len(d), sorted(set(round(r['score'],6) for r in d)))"
2901 [1]
```

**2,901 hits out of 3,000 seeds and exactly one distinct score.** The heap comparator is
`(score desc, seed asc)` (`Execution\SearchRun.cs` lines 346-350), so with all scores equal the
"best of" table degenerates to *the numerically smallest matching seeds* — which is what the terminal
printed (`-2134485992, -2134329576, -2130794540, ...` in ascending order). That is precisely the
pointless outcome the user described, and §3 is the fix.

### 2.3 The goal record, extended

```jsonc
{ "id": "swamp_near", "target": "biome:Swamp", "metric": "nearest_distance",
  "test": "near", "value": 2500,
  "importance": "must",          // must | nice
  "weight": 1.0,                 // > 0, applies to BOTH numbers. UI range 0.1 .. 10.
  "prefer": "recommended"        // recommended | none | closer | farther | smaller | larger | centred
}
```

`prefer` is new. `pad` (the `between` ramp width) is **superseded** by the `centred` preference and
should be kept only for reading old query files; see §2.6.

### 2.4 Match percentage `M`

Let `Nice` be the goals with `importance: nice`, `w_i > 0` their weights, and `pass_i ∈ {0,1}` the
goal's own verdict (`CompiledGoal.Pass(v)`, `CompiledQuery.cs` lines 224-231 — unchanged).

```
M = ( sum over i in Nice of  w_i * pass_i ) / ( sum over i in Nice of w_i )
```

- Must-have goals do **not** enter `M`. Every surviving seed passes all of them, so including them
  would only compress the scale and make two different queries incomparable.
- `Nice` empty: `M` is **undefined**. Report it as `n/a`, never as `100 %`, and order as if `M = 1`.
  The UI writes "no nice-to-have goals — match cannot tell these worlds apart".
- A goal whose value could not be measured is excluded from both sums and the record says so. An
  unmeasurable **must** goal already fails the run (`SeedEvaluator.cs` lines 283-292); an
  unmeasurable **nice** goal is dropped only under `--skip-unavailable`.
- Displayed to one decimal place (`87.5 %`); **ordered on the exact binary64 value**.

### 2.5 Quality score `Q`

Let `P` be the goals with `prefer != none` **and** a measured value. Every goal is eligible —
must-haves included, which is the whole point: it is how "Eikthyr within 2,000 m" gets ordered by how
close Eikthyr actually is.

```
Q = ( sum over i in P of  w_i * q_i ) / ( sum over i in P of w_i )
```

with `q_i` in `[0,1]` from the measured value `v_i` and an anchor `a_i > 0`:

| direction | formula | q at v=0 | q at v=a | q as v -> inf |
|---|---|---|---|---|
| smaller is better | `q = a / (a + v)` | 1 | **0.5** | 0 |
| larger is better | `q = v / (a + v)` | 0 | **0.5** | 1 |
| centred (a range) | `q = s / (s + abs(v - m))`, `m = (lo+hi)/2`, `s = (hi-lo)/2` | — | — | 0 |

**Why this shape and not the clamped ramp in use today.**

- **It never saturates**, so it can always break a tie. The current `clamp01(1 - v/D)` gives 1.0 to
  every seed at or inside `D` — which is exactly the 2,901-way tie measured above.
- **`q = 0.5` exactly at the threshold.** A quality score is then readable without a legend: above
  0.5 on a goal means "better than you asked for".
- **Scale-free.** Switching a metric from metres to kilometres does not move `Q`. A clamped ramp is
  scale-free too, but a fixed normalisation constant (say "divide by 10,500 m") is not, and it also
  crushes area metrics into the bottom 3 % of the range.
- **Total and defined at the edges.** `v = 0` and `v = +inf` need no special case: smaller-is-better
  gives 1 and 0, larger-is-better gives 0 and 1. That matters because location metrics legitimately
  return `+inf` for "the world has none" (`MetricCatalog.cs` line 216).
- **One division, so two implementations agree.** Compute in IEEE-754 binary64, literally as
  `a / (a + v)` or `v / (a + v)`; do not reassociate, do not pre-divide. Given the same `a` and `v`
  the result is bit-identical on any conforming runtime.

**The anchor `a_i`, precisely.**

1. `a = goal.value` when the test is `near`/`far`/`at_least`/`at_most` and `goal.value > 0`.
2. `a = s = (max - value) / 2` for `between`, with `m = (value + max) / 2`.
3. otherwise (threshold 0, negative, or non-finite) `a` = the metric's **default anchor**, a constant
   that must live in `MetricCatalog` so both front ends read the same table:

   | unit | default anchor |
   |---|---|
   | metres (distance) | 2,000 m |
   | square metres | 1,000,000 m² (1 km²) |
   | count | 1 |
   | fraction | 0.1 |

   `deepest_point` and `highest_peak` are signed metres, not distances: anchor 100 m, and the value is
   shifted by the water level (`v' = v - 30`) before the formula so that `q` is defined on a
   non-negative quantity. Any other metric that can go negative must declare the same shift.

**Non-finite and unmeasured values.**

| case | `q` |
|---|---|
| `v = +inf`, smaller-better | 0 |
| `v = +inf`, larger-better | 1 |
| `v = NaN` (never measured — an earlier must-goal short-circuited the seed) | the goal leaves both `M` and `Q`; the record records `"measured": false` |
| `Bounded = true` (the region restriction proved the verdict but not the figure, `SeedResult.cs` lines 24-31) | the goal leaves `Q` and the record says `"quality_excluded": "bounded"`. A bound must never be scored as if it were a measurement. |

### 2.6 "Recommended for this goal", per target type

`recommended` resolves at compile time to one of the four directions, and the resolved direction is
**printed in the plan and written into the results file**, so nobody has to guess later.

| metric | recommended direction | note |
|---|---|---|
| any `*_distance` with test `near` / `at_most` | **closer** | |
| any `*_distance` with test `far` / `at_least` | **farther** | |
| `nearest_land_distance` | **closer** | |
| `area`, `share`, `area_within`, `land_area`, `land_area_within`, `area_above_height`, `largest_patch_area`, `largest_island_area`, `spawn_island_area`, `water_area`, `land_share`, `water_share`, `ocean_share`, `coastline_length` | **follows the test**: `at_least` -> larger, `at_most` -> smaller | |
| `island_count`, `count`, `count_within`, `types_within`, `river_count`, `lake_count`, `stream_count` | **follows the test** | |
| `highest_peak` | **larger** | |
| `deepest_point` | **smaller** | named exception: "deepest" reads backwards, so state it in the tooltip — "prefers a deeper trench" |
| any test `between` | **centred** | |
| `present` | **none** | named exception: it is 0-or-1, so no preference can order it. The UI greys the preference control out and says "this goal is yes-or-no; it shapes match, not quality." |

### 2.7 Normalisation must be absolute — the constraint that rules out the obvious alternatives

The results heap is bounded (`--keep N`, default 1000 per the user's decisions) and hits are streamed
to disk as they are found. A new seed is therefore compared against seeds that **have already been
evicted from memory and written out**. Any normalisation computed over the observed population — a
z-score, a percentile, min-max over what has been seen, "best so far" — would make the result order a
function of scan order and of `--keep`, so the same query would rank differently on 8 threads than on
16, and a resumed run would disagree with an uninterrupted one.

**Rule: `q_i` depends only on `(v_i, a_i, direction)`.** Nothing about any other seed may enter it.
This is what keeps the existing guarantee in `docs\search.md` — "the results file is byte-identical
across thread counts" — true once ranking gets richer.

### 2.8 Ordering, including ties

```
compare(A, B):
    if A.M != B.M:  higher M first          (binary64, exact)
    if A.Q != B.Q:  higher Q first          (binary64, exact)
    otherwise:      lower seed first        (int32, so -2147483648 sorts first)
```

Total order, because seeds are unique. Reproducible across thread counts, block sizes, resumes and
`--keep` values, because `M` and `Q` are pure functions of `(seed, query)`. This extends the existing
`(score desc, seed asc)` rule in `SearchRun.cs` line 346 by one level and keeps its tie-break.

Display rounding (`87.5 %`, `0.732`) is presentation only; **never sort the rounded values** or two
seeds that differ in the fourth decimal will swap between renders.

### 2.9 What a result record carries

```jsonc
{
  "seed": 3677642, "text": "Gi1713",
  "match": 0.875,                 // M, or null when the query has no nice goal
  "quality": 0.7318,              // Q, or null when no goal carries a preference
  "ranked": true,                 // false under --first (see §3)
  "defs": 2, "grid": 96, "engine": "0.1.0", "tier": "t3",
  "goals": {
    "swamp_near": { "value": 2120.7, "unit": "m", "test": "near", "threshold": 2500,
                    "pass": true, "importance": "must",
                    "prefer": "closer", "prefer_resolved_from": "recommended",
                    "weight": 1.0, "q": 0.5410, "measured": true }
  }
}
```

`defs` must be bumped to **2** the moment `prefer`/`M`/`Q` ship: a results file written under the old
single-score model is not comparable and the field is what says so (`QueryModel.cs` lines 189-194).

---

## 3. The refusal rule

### 3.1 What is detected, exactly

At compile time, in `CompiledQuery.Compile` — before one seed is touched:

```
nice_weight = sum of w_i over goals with importance == nice
pref_weight = sum of w_i over goals with prefer != none

REFUSE  iff  nice_weight == 0
        and  pref_weight == 0
        and  the run keeps a bounded best-of list (keep != "all")
        and  --first was not passed
```

All four conditions matter:

- `nice_weight == 0` — `M` cannot discriminate.
- `pref_weight == 0` — `Q` cannot discriminate either. **A single preference on a single must-have
  goal is enough to make the query rankable**, which is why the message leads with that fix.
- `keep != all` — with `--keep all` nothing is being ranked; every match is written and the user gets
  the complete answer. No refusal.
- not `--first` — the explicit opt-in below.

Note the interaction with defaults. If `prefer` defaults to `recommended`, this state is only ever
reached deliberately, by setting every goal to `prefer: none`. **Until `prefer` ships, the detector
reduces to `nice_weight == 0` and bounded keep**, which is exactly the 2,901-identical-scores run in
§2.2 and is the condition that should ship first.

### 3.2 The message

```
vseed search: this query cannot rank its results, so "the best 1000" would be a lie.

  Every goal is a must-have and none of them carries a preference, so every world that
  passes scores exactly the same. The best 1000 would really be "whichever 1000 the scan
  reached first" - and the other 4,294,966,296 worlds would be evaluated to produce a
  list that nothing actually chose. That is days of compute for an arbitrary answer.

  Three ways to make the ranking mean something. Any one of them is enough.

  1  Add a nice-to-have goal - something you would like but do not require.
       "importance": "nice"          e.g. { "id": "meadows", "target": "biome:Meadows",
                                            "metric": "share", "test": "at_least",
                                            "value": 0.08, "importance": "nice" }
       The match percentage then says how many of your wishes each world granted.

  2  Put a preference on a goal you already have. This is usually the one you want.
       --prefer swamp_near=closer          (or "prefer": "closer" in the goal)
       Your goals, and the preference each one would take:
         swamp_near   biome:Swamp.nearest_distance   closer | farther
         isles        world:island_count             larger | smaller
       The quality score then orders worlds that all passed, by how well they passed.

  3  Ask for every match instead of the best N.
       --keep all        streams every hit to the results file. Estimated output for
                         this query: 131,000 - 2,100,000 records, 39 MB - 640 MB at
                         307 bytes/record (jsonl). Free on E: 3,098 GB.

  If you really do want whichever N the scan reaches first:
       --first 1000      first-N in scan order, no ranking claimed. Every record is
                         stamped "ranked": false and the summary reads
                         "1000 of N matches, unranked, in scan order".

  Nothing was scanned. Exit 2.
```

Notes on the wording, which are requirements not decoration:

- It names **the user's own goal ids** and the two directions each would accept. A refusal that makes
  the user go read the schema is a worse refusal.
- Fix 3 quotes a **measured** bytes-per-record (see §4.4) and the **actual free space**, so `--keep
  all` is a choice with a number on it rather than a dare.
- The record count is a range, because the hit rate is the one thing not knowable in advance
  (user-decisions §6). It comes from the same calibration slice the plan uses.
- "Nothing was scanned" is there so nobody wonders whether a partial file exists.

### 3.3 The opt-out flag

| surface | spelling |
|---|---|
| CLI | `--first <n>` (mutually exclusive with `--keep <n>`; `--first` implies `--keep n` internally but suppresses ranking) |
| query file | `"output": { "ranking": "none", "first": 1000 }` |
| GUI | in the **Matches to keep** control, a third radio: `best N` / `all` / `first N (unranked)`, with the same explanation inline |

Contract for `--first`:

- Results are emitted in **scan order** (the Feistel-shuffled order, so an unbiased sample), not
  sorted. The file order is the scan order and the summary says so.
- Every record carries `"ranked": false` and `"match": null, "quality": null` unless the query
  actually produced them.
- The summary line must never contain the words "best" or "top": `1,000 of 131,076 matches, unranked,
  in scan order (covered 3.0518 % of all 4,294,967,296 worlds)`.
- The run still reports the **true total hit count** (user-decisions §1).

### 3.4 A second, softer case worth warning about (not refusing)

When the query *has* nice goals but they are **all** `present`-style binary goals and no goal carries
a preference, `M` discriminates but `Q` does not, so large ties are still likely. This is a warning,
not a refusal:

```
note: your nice-to-have goals are all yes/no, so worlds will tie on match percentage
      and nothing will order them within a tie. Add a preference (e.g. --prefer
      swamp_near=closer) if you want the best of each tie rather than an arbitrary one.
```

---

## 4. Per-goal cost transparency — telling the user before they press go

### 4.1 Cost classes

A class is assigned from the **measured** ms per seed per thread for that goal alone, not from a
hard-coded table, so it stays true when the machine or the game changes. `--calibrate` already
produces the number.

| class | measured ms/seed/thread | what it means in practice | example goals |
|---|---|---|---|
| **instant** | 0 — no seed is touched | answered by static analysis of the query | any biome goal T0 can decide; `world:*` bounds |
| **cheap** | < 10 ms | region-restricted or coarse biome sampling | `biome:Swamp.nearest_distance near 2500` at G384 — **0.2 ms measured** |
| **moderate** | 10 – 100 ms | whole-world biome sampling at a usable grid | `biome:Swamp.area` at G384 — **12.7 ms measured**; the same at G96 ~ 200 ms |
| **expensive** | 100 ms – 2 s | anything that needs heights, rivers, lakes or streams | `world:island_count` at G384 — **615.9 ms**; `world:river_count` — **631.0 ms**; any biome goal at G12 — **1,088.5 ms** |
| **very expensive** | > 2 s | any location goal, and heights at G12 | `group:traders` — **3,969–4,778 ms**; `world:island_count` at G12 — **3,970 ms**; `group:tar_pits_with_alt` — **16,237–25,923 ms** |

(All figures from §0's contended machine. The classes are wide enough that contention does not move a
goal between them, which is the point of choosing decade-wide boundaries.)

### 4.2 The one thing that matters most: a goal drags the whole query up

The engine evaluates every seed to `max(tier over goals)`
(`CompiledQuery.MaxTier`, consumed by `SeedEvaluator.Evaluate`). So the cost of a query is **not** the
sum of its goals' costs — it is the cost of its most expensive goal, paid on every seed. The interface
has to say this at the moment the user adds the expensive goal, not in a footnote.

Measured illustration, same grid, same region, one goal each:

| query | tier | ms/seed (1T, contended) | multiple of the cheap query |
|---|---|---|---|
| `biome:Swamp.nearest_distance near 2500` @ G384 | T2 | 0.2 | 1x |
| `biome:Swamp.area at_least 20 km²` @ G384 | T2 | 12.7 | 64x |
| `world:island_count at_least 200` @ G384 | T3 | 615.9 | **3,080x** |
| `world:river_count at_least 60` @ G384 | T4 | 631.0 | 3,155x |
| `biome:Swamp.area` @ G12 | T2 | 1,088.5 | 5,443x |
| `world:island_count` @ G12 | T3 | 3,970.2 | 19,851x |
| `group:bosses_classic` @ G384 | T5 | 3,372 – 4,250 | ~19,000x |
| `group:tar_pits_with_alt` @ G384 | T5 | 16,237 – 25,923 | ~100,000x |

### 4.3 The exact wording, for the common cases

These strings belong in one resource table read by both the CLI and the GUI, so the terminal and the
page cannot disagree. `{}` are substituted from the live calibration.

**(a) Adding the first height/river goal to a biome-only query — the 48x cliff.**

> **This goal changes the whole search, not just itself.**
> `world:island_count` needs terrain heights, and heights need the lake/river/stream pre-generation —
> about 99.5 % of the cost of building a world. Your query was **{0.2} ms per seed**; with this goal
> it is **{615.9} ms per seed**, a **{3,080}x** increase, and **{97} %** of the new cost is the
> pre-generation.
> Whole space: **{4.3 days}** becomes **{42 years}**.
> The good news: any *further* height, river, lake or stream goal is nearly free — you have already
> bought the expensive part.

*(Both whole-space figures in that template are the tool's own output for the same thread count —
2 threads in the runs quoted here. Never mix a rate measured at one thread count with a projection at
another; the plan must print the thread count next to every projection, as it already does.)*

**(b) Adding the first location goal — the seconds-per-seed cliff.**

> **This goal moves every seed into the placement tier.**
> `group:traders` runs Valheim's own location placement for ordered entries 0–21 of 183. There is no
> cheaper way to know where a trader's candidates are; the 2048² biome-point grid that
> `GetRandomPointByBiomes` draws from is most of the cost and is not optional.
> Per seed: **{0.2} ms** becomes **{4,000} ms**, about **{20,000}x**.
> Whole space: **{4.3 days}** becomes **{~13 years}** at the measured 16-thread rate.
> **Do not scan the whole space with this goal.** Use the funnel: scan the cheap goals over the whole
> space first (**{4.3 days}**), then run placement on the survivors only. `--strategy funnel` does
> this, and it will tell you the survivor count and the stage-2 cost before stage 2 starts.

**(c) A late-ordered location group — the 4.2x surcharge.**

> **`tar_pits_with_alt` reaches ordered entry 183 of 183, so every seed pays for the FULL placement.**
> Measured, this costs **4.2x** what a boss or trader goal costs on the same machine (prefix 183 vs
> prefix 1, measured twice under different load, 4.20x both times).
> `tar_pits` alone reaches entry 105 and is about **2.5x** cheaper. The alt-biome variants
> (`TarPit1_1/2_1/3_1`, `GoblinCamp2_1`) place in a minority of worlds, a few instances, far out.
> Drop them unless you specifically want them.

**(d) Switching the grid to 12 m.**

> **12 m is the grid the game itself samples, and it is 1,024x the cells of 384 m.**
> Measured on a whole-world biome query: **12.7 ms -> 1,088.5 ms per seed (86x)**.
> It changes the *answer* for island count, largest island and spawn island — a coarse grid bridges
> water channels narrower than its spacing and can be wrong by more than 10x (measured: one seed's
> spawn island reads 6.24 km² at 24 m and 0.51 km² at 12 m).
> It changes almost nothing for area, share and land-fraction goals (< 1 % at 96 m).
> **Your current goals: {2 of 3 are unaffected; `world:spawn_island_area` is affected}.**

**(e) A distance threshold near the grid spacing.**

> **`biome:Meadows.nearest_distance near 200 m` at grid 384 m cannot mean what you want.**
> The nearest a grid can ever report is half a cell diagonal, **{271.5} m at this grid**, so this goal
> can never pass. Either lower the grid to **{96 m or finer}** or raise the threshold above
> **{815 m}** (3x the floor).

**(f) The whole-space banner.**

> **You are asking for all 4,294,967,296 worlds.** At the measured **{11,679} seeds/s** on
> {2} threads this run is **{4.3 days}**. It is resumable: Ctrl-C stops at the next block boundary and
> `--resume` continues. With `--keep 1000` the output is ~{307 KB} regardless of how many worlds match,
> and the true total hit count is reported separately.

### 4.4 The estimator, with its measured constants

Implementing user-decisions §6. Every constant below was measured here; none is a guess.

```
rate(tier, grid, threads) = 1000 / ms_per_seed_1T  x  scaling(threads)
    ms_per_seed_1T          from --calibrate on this query (the only correct source)
    scaling(16 threads)     10.5x, NOT 16x - the project's measured figure for this
                            FP-heavy work on an 8-core/16-thread part, printed by the tool itself

time_s        = seeds / rate
hits          = seeds x hit_rate        hit_rate unknown before the run; calibrate on slice 1,
                                        re-project after every slice, always show as an interval
bytes_out     = header + min(hits, keep) x bytes_per_record       [bounded]
              = header + hits x bytes_per_record                  [--keep all]
checkpoint    = a few KB, rewritten once per block
scratch       = 0 per seed
memory        = fixed + threads x per_worker(tier, grid)
```

**`bytes_per_record`, measured, not assumed.** Same 3,000-seed run, two formats:

```
$ vseed search q-biome384.json --seeds 3000 --threads 2 --out rec.jsonl --progress none
$ wc -l rec.jsonl ; wc -c rec.jsonl      ->  2901 lines, 890,418 bytes
$ vseed search q-biome384.json --seeds 3000 --threads 2 --out rec.csv   --progress none
$ wc -l rec.csv   ; wc -c rec.csv        ->  2902 lines (1 header), 260,850 bytes
```

| format | bytes/record (1 goal) | note |
|---|---|---|
| `.jsonl` | **307** | grows by roughly 90–130 B per extra goal (each goal is an object with value/unit/test/threshold/pass/importance) |
| `.csv` | **90** | grows by roughly 45–60 B per extra goal (4 columns per goal: value, pass, score, bounded) |
| `.json` | ~ jsonl + 2 B/record | array punctuation |

So the default safe run — whole space, `--keep 1000`, one goal, jsonl — writes about **307 KB**,
independent of the 4.29 billion seeds scanned. That is the property the user asked to be true by
construction, and the estimator should say it in those words.

**`per_worker` memory**, from the tool's own plan line: 55 KiB of grid buffers per run at G384 with
2 threads; `schema.md` states **36 MB per worker at grid 12**. A 16-thread G12 run is therefore
~576 MB of grid buffers, and the plan must show it before the user starts.

### 4.5 Where the time actually goes, for the optimisation review

Ranked by measured share of per-seed cost, so effort goes where the time is:

| cost centre | share | already exploited? | what is left |
|---|---|---|---|
| lake/river/stream pre-generation | **97–99 %** of any T3/T4 seed (599 of 615.9 ms; 623.8 of 631.0 ms) | **Yes** — deferred, so T2-only queries and T3 queries whose T2 must-goal already failed never pay it (`SeedEvaluator.cs` lines 168-232) | `MergePoints` is O(n²) over ~6,600 points; spec 07 §4.3 warns any acceleration must reproduce the identical merge order, so this is a correctness-risky optimisation, not a free one |
| the 2048² biome-point grid for `GetRandomPointByBiomes` | **most** of any T5 seed (prefix 1 costs as much as prefix 32) | partially — the ordered prefix shortens only the rest | this is the single biggest remaining lever for location queries, and it is shared across all types in one seed; measuring exactly how much of the 3.2–6.2 s it is would be the first thing to do |
| grid sampling | 99.9 % of a T2 seed, 1.1 % of a T4 seed | region restriction (measured: a 2,500 m disc is 5.67 % of the map and drops 12.7 ms to 0.2 ms, a **64x** win) | grid choice is the user's lever; §1.6 says which goals can afford a coarse one |
| generator construction | **0.0–0.3 %** | n/a | nothing to win |
| result writing | not measurable at these rates | bounded heap, O(keep) | nothing to win |

**The largest available win is not in the code — it is in the query.** Region restriction gave 64x and
grid choice gives up to 1,024x, both for free, and both are already implemented. The funnel gives the
rest. That should be said plainly rather than promising engine speed-ups that do not exist: as
`docs\search.md` puts it, "cost per seed is how much of the world the goal forces you to evaluate, and
nothing else."

---

## 5. The GUI — the goal editor and everything around it

The user's requirement is blunt: "everything must also be doable from the web-interface (GUI) as
opposed to having CLI-only operations." §5.6 is the honest list of where that is not true today.

The architectural rule that makes parity checkable already exists and must be kept: the page speaks
**only** the criteria language, and the server turns the panel's object straight into the query text
`vseed search` reads (`src\SeedLab.Web\Search\SearchModel.cs` lines 6-16 says exactly this, and
`Search\QueryTranslator.cs` is the translation). So every control below is specified as *the JSON key
it writes*. If a control does not name a key, it does not belong on the page.

### 5.1 The goal list

One card, `#goalList`, one row per goal, in query order. Each row is collapsed by default and shows
the sentence a player would say:

```
[ Swamp ▾ ] [ nearest ▾ ] [ within ▾ ] [ 2500 ] m      ( Must have ▾ )   [ ▸ ] [ × ]
   ^target      ^metric       ^test      ^value           ^importance      expand  remove
```

- **Target** is two controls that swap on the kind: `biome | world | group | prefab`
  (`app.js` `kindSel`) then the name. Groups show their help line as the option title
  (`LocationGroups.Help`), and late-ordered groups get a cost badge (§5.2).
- **Metric**, **test** and the unit suffix come from `/api/meta`'s `MetricInfo`
  (`SearchModel.cs` lines 252-291), which already carries `unitLabel`, `scale`, `tests`,
  `defaultValue`, `needsRadius`, `needsHeight`, `tier` and `needsLocations`. Nothing is hard-coded in
  the page.
- **Importance** is a two-state control labelled *Must have* / *Nice to have*, with the reference
  site's own explanation on hover: "A must-have goal always has to pass."
- **`×`** removes; **`▸`** expands.

**Add / remove flow.** `+ Add goal` appends a row pre-filled from the last row's kind (not a fixed
`biome:Swamp`, which is what `addGoalRow` does today) and focuses the target control. Removing the
last goal is allowed; the run button then says "add at least one goal". A **Duplicate** action on
each row is worth having: the common edit is "same goal, different biome".

**Presets** stay where they are (`#presetSelect` -> `/api/search/presets`), plus two new actions:
**Save as query file** (downloads the canonical JSON, the GUI equivalent of `vseed presets show`) and
**Load query file** (drag or pick a `.json`, validated by the server's own reader so the page and the
CLI reject the same files).

### 5.2 The expanded per-goal panel

Everything the reference site puts behind its own expander, plus what only we have:

| control | JSON key | default | notes |
|---|---|---|---|
| **Weight** (1 = normal) | `weight` | `1` | number, 0.1–10, step 0.1. Help, quoting them: "Weight changes this goal's influence on both numbers; 1 is normal." |
| **When matches are equally good** | `prefer` | `recommended` | select: *Recommended for this goal (closer)* / *No extra preference* / *Prefer closer* / *Prefer farther away* / *Prefer smaller values* / *Prefer larger values*. The recommended option shows the **resolved** direction in its own label (§2.6). Disabled with an explanation for `present`. |
| **Upper value** | `max` | — | shown only when test = `between`. **Missing today and it silently breaks `between` goals** (§5.6). |
| **Radius** | `radius` | 2,000 m | shown when `needsRadius` |
| **Height** | `height` | 300 m | shown when `needsHeight` |
| **Minimum island size** | `min_area` | 10,000 m² (1 ha), entered in km² | `island_count` only. Their equivalent control. |
| **Measure world shape within** | `measure_within` | 10,500 m, entered in km | world-shape and biome-area goals. Reproduce their help verbatim: "10.5 km covers the playable world. Smaller radii clip islands at the edge." **New — see §1.7.** |
| **Count river crossings up to … as one island** | `bridge_width` | 0 m (`64` to match valheim.gaming.tools) | connectivity goals only. **New — see §1.7.** |
| **Measure from** | `from` | `center` | `Centre of the world (0,0)` / `The spawn stone`. The second says "runs one location placement, which makes this goal cost seconds per seed". |
| **Goal id** | `id` | derived | read-only unless the user edits it; it names the CSV columns. |

Under the controls, three read-only lines that are the goal's honesty panel:

1. the metric's `help` text and its **tier** (already present as `#g-help`);
2. the **cost class and what it does to the query** (§4.1, §4.3) — this is the new, important one;
3. for any target containing an `m_unique` type, the `UniqueSemantics` string **verbatim**
   (`MetricCatalog.cs` lines 202-210). Never paraphrased: it is the difference between "Haldor is
   here" and "one of Haldor's ten candidates is here".

### 5.3 The search-options panel

| control | JSON key | default | notes |
|---|---|---|---|
| **Seeds to screen** | `budget.seeds` | 20,000 | plus a **Whole space (4,294,967,296)** toggle = `--all`. Today the number box silently accepts 4294967296 with no ceremony. |
| **Matches to keep** | `keep` | **1000** (user-decisions §1) | three radios: `best N` / `all` (with the live size estimate beside it) / `first N (unranked)` (§3.3). Today this is hard-coded to 200 in `app.js searchQuery()`. |
| **Biome sampling / grid** | `grid` | 96 m | the existing select, relabelled with what each costs and what it changes — 384 m "fastest, area goals only", 96 m "balanced", 12 m "the game's own grid; required for island and spawn-island goals". |
| **World version** | `world.gen_version` | 2 | their "World version". Exposed by the model, not by the page. |
| **Candidate source / order** | `search.order`, `search.key` | `shuffled`, derived | their "Candidate source (Random)". Ours is better and should say so: a Feistel permutation, so every seed exactly once, no repeats, and a partial run is an unbiased sample. Show the key and allow pasting one — that is how a run is reproduced. |
| **Range** | `search.range` | whole int32 | the existing From/To boxes. |
| **Strategy** | `search.strategy` | auto | `funnel` / `sample` / `direct` (user-decisions §3). With location goals and no choice, default `funnel` and say so in the plan. |
| **At the ceiling** | `on_limit` | `stop` | `stop` / `evict`; `evict` needs the confirm dialog described in user-decisions §2. |
| **Slice size** | `slice` | auto (30–120 s of work) | user-decisions §4. |
| **Threads** | `search.threads` | server default | plus a plain warning that the server also renders the map. |
| **Stop after** | `budget.wall` | 120 s | with the existing caveat that a wall-limited run is not reproducible on its own. |
| **Audit mode** | `--no-prefilter` | off | "re-measures everything at full cost; the result set must be identical. Slow on purpose." |
| **Output file** | `output.path` | none | **entirely missing from the GUI today** (§5.6). |

### 5.4 The live metric panel — before, during, after

This is where the user's "dynamically shown and updated so as to avoid surprises" requirement lands,
and it needs **three** states, not the one the page has now.

**(a) Before — a live plan that updates as the user edits, with no run started.**

The single most valuable change to the page. Today `#planCard` is populated from the SSE `started`
event, i.e. *after* the run begins. It must instead be driven by a new **`POST /api/search/plan`**
endpoint — the GUI equivalent of `--dry-run --calibrate` — debounced ~400 ms after any edit:

```
Plan                                                  [ ● recalculating ]
  goals              3   (highest tier: T3 height — one goal forces it: isles)
  grid               G96  ·  region evaluated: the whole world (10,500 m)
  seeds to screen    4,294,967,296  (100 % of the space)
  measured cost      616 ms/seed/thread   (calibrated on 8 seeds just now)
  estimated rate     6,464 seeds/s on 16 threads  (measured 10.5x scaling, not 16x)
  estimated time     7.7 days              ▸ how this was calculated
  peak memory        16 threads x 9 MB = 144 MB
  output             keep 1000 x 307 B = 307 KB        free on E: 3,098 GB
  hit rate           unknown until the first slice; the size above is the bounded ceiling
  verdict            FITS — but see the warning below
```

Every row is a measured constant or arithmetic on one, and **"▸ how this was calculated" expands to
the formulas in §4.4 with the numbers substituted** — that is the "exact calculations" the user asked
for, in the GUI.

**(b) During — the live line, refreshed at most twice a second.**

`SearchProgress` (`SearchModel.cs` lines 170-201) has `scanned, passed, limit, elapsedS,
seedsPerSecond, etaSeconds, fractionOfSpace, fractionCovered, probeAccepts, earlyExits, status`.
It needs these added, all of which the user asked for by name:

| new field | why |
|---|---|
| `hitsKept` | so "1,000 kept of 131,076 found" can be shown — user-decisions §1 and §7 |
| `bytesWritten` | measured, not projected |
| `projectedBytes` | re-projected after every slice as the hit rate sharpens |
| `freeBytes` | on the output volume, live |
| `sliceIndex`, `sliceCount`, `lastSliceSeconds` | the slice boundary is the resume point (user-decisions §4) |
| `evicted`, `evictionThreshold` | only under `on_limit: evict`, and the final report must state both |
| `hitRateLow`, `hitRateHigh` | the projection interval |
| `etaWholeSpace` | already computed in the page; move it server-side so both front ends agree |

Rendered as two lines plus the bar that already exists:

```
1,204,224 of 4,294,967,296 seeds · 3,318 passed · 6,441 seeds/s · 3 m 07 s · 7.7 days left
covered 0.0280 % of the 4,294,967,296-world space · kept 1,000 of 3,318 found
307 KB written · projected 307 KB (bounded by keep) · 3,098 GB free · slice 14 of 41,943
```

**(c) After — the same numbers frozen**, plus the resume command, the true total hit count, what was
deleted and what was kept, and a **Copy resume command** button so the GUI run can be continued from
the terminal (and vice versa).

### 5.5 The warning-and-confirm dialog

Thresholds from user-decisions §5, all configurable, these are the defaults. The dialog fires when
**any** of: estimated wall time > 1 h; seeds > 100,000,000; estimated output > 1 GB; estimated output
> 10 % of free space; or the whole-space toggle is on (always).

```
+--------------------------------------------------------------------------+
|  This is a big search. Read the numbers before you start.                 |
|                                                                           |
|  Seeds          4,294,967,296  — every world Valheim can make             |
|  Tier           T3 height — forced by one goal: "isles" (world:island_count)|
|  Measured cost  616 ms per seed per thread, calibrated on 8 seeds         |
|  Time           7.7 days at 6,464 seeds/s on 16 threads                   |
|  Memory         144 MB                                                    |
|  Output         307 KB  (keep 1000; the file size does not grow with the  |
|                 number of seeds scanned)                                  |
|  Free space     3,098 GB on E:                                            |
|                                                                           |
|  It is resumable. It stops at a slice boundary and writes a checkpoint,    |
|  so a reboot costs at most one slice (about 60 s of work).                |
|                                                                           |
|  [ type RUN to confirm: ______ ]              [ Cancel ]   [ Start ]      |
+--------------------------------------------------------------------------+
```

Two rules for this dialog:

- **The honest sentence for the tier is mandatory** and names the goal responsible. "T3 height —
  forced by one goal: isles" is what lets the user delete one goal and get their 4.3-day search back.
- **A location-goal run must show the funnel offer instead of a plain Start.** Per §4.3(b): the whole
  space at seconds per seed is years, so the primary button becomes `Run the funnel` and `Start
  anyway` is secondary and greyed until the typed confirmation.

The typed `RUN` confirmation replaces `--yes`; `on_limit: evict` gets a second, separate confirmation
naming what it will throw away (user-decisions §2).

### 5.6 Results view

Table columns, replacing today's `score / seed / int32 / land / largest`:

| column | source |
|---|---|
| **Match** | `M` as a percentage, or `n/a` with the "no nice-to-have goals" tooltip |
| **Quality** | `Q` to three decimals, or `n/a` |
| **Seed** | `text`, with `int32` on hover |
| one column **per goal** | the measured value in the metric's unit, green/red for pass/fail, `> n` when `bounded` (the existing `fmtUnit` + `bounded` handling in `goalSummary` already does this correctly — it just needs to be columns instead of a `title` attribute) |
| **Map** | an explicit button. Clicking the row already calls `openSeed` (`app.js renderResults`), but nothing on screen says so. |

Under the table: `1,000 of 131,076 matches shown, ranked by match then quality then seed` — never
"1,000 matches" (user-decisions §7). And an **Export** button writing `.jsonl` / `.json` / `.csv`
through the same `ResultWriter`, so a GUI run's file is byte-identical to a CLI run's.

Each row expands to the full per-goal breakdown: measured value, threshold, pass, weight, resolved
preference, sub-score `q`, contribution to `Q`, the tier that produced it, and the
`UniqueSemantics` line where it applies. This is the GUI equivalent of `vseed explain`, and
`docs\specs\07-features.md` §3.6 is explicit that it is not optional: "a single number without the
breakdown is useless for deciding between two seeds."

### 5.7 Control-to-JSON map (the parity contract)

| GUI control | query JSON | CLI equivalent |
|---|---|---|
| goal target selects | `goals[].target` | in the query file |
| metric / test / value | `goals[].metric` / `.test` / `.value` | " |
| upper value | `goals[].max` | " |
| radius / height | `goals[].radius` / `.height` | " |
| min island size | `goals[].min_area` | " |
| measure within | `goals[].measure_within` *(new)* | " |
| bridge width | `goals[].bridge_width` *(new)* | " |
| measure from | `goals[].from` | " |
| importance | `goals[].importance` | " |
| weight | `goals[].weight` | `--weight <id>=<w>` *(new)* |
| preference | `goals[].prefer` *(new)* | `--prefer <id>=<dir>` *(new)* |
| seeds to screen | `search.budget.seeds` | `--seeds` |
| whole space toggle | `search.budget.seeds` omitted | `--all` |
| matches to keep | `search.keep` | `--keep N` / `--keep all` |
| first-N (unranked) | `output.ranking: "none"`, `output.first` *(new)* | `--first N` *(new)* |
| grid | `search.grid` | `--grid` |
| world version | `world.gen_version` | in the query file |
| order / key | `search.order`, `search.key` | `--order`, `--key` |
| range | `search.range` | `--from`, `--to` |
| strategy | `search.strategy` *(new)* | `--strategy` *(new)* |
| at the ceiling | `search.on_limit` *(new)* | `--on-limit` *(new)* |
| slice size | `search.slice` *(new)* | `--slice` *(new)* |
| threads | `search.threads` | `--threads` |
| stop after | `search.budget.wall` | `--budget` |
| audit mode | `search.no_prefilter` *(new key)* | `--no-prefilter` |
| output file | `output.path` | `--out` |
| Plan card | — | `--dry-run --calibrate` |
| Export | `output.path` + format from the extension | `--out` |
| Resume | — | `--resume`, `--checkpoint` |

### 5.8 What the CLI can do that the GUI cannot today — the parity gap list

Split by how much work each is, because they are not the same size of problem.

**(A) The web model already carries it; only the page does not send it.** These are edits to
`wwwroot\index.html` and `wwwroot\app.js` alone — `SearchModel.SearchGoal` and `QueryTranslator`
handle all of them already (`QueryTranslator.cs` lines 119-141).

| missing from the page | evidence |
|---|---|
| `weight` | `app.js collectGoals()` writes `weight: 1` literally, so the per-goal weight is unreachable |
| `max` (the upper end of `between`) | never collected. The test dropdown *offers* `between` whenever the metric lists it, so **a `between` goal built in the GUI is sent with `max: 0` and cannot behave correctly.** This is the one item on this list that is a live defect, not just a missing feature. |
| `min_area` | `SearchGoal.MinArea` exists and defaults to 10,000; no control writes it |
| `from` (`center` / `spawn`) | `SearchGoal.From` exists; no control |
| `pad` | `SearchGoal.Pad` exists; no control (and §2.6 supersedes it anyway) |
| `id` | auto-derived; not editable, so CSV column names cannot be chosen |
| `keep` | hard-coded to `200` in `app.js searchQuery()`; the model allows 1–5,000 |
| `key` | `SearchQuery.Key` exists; no control, so a run cannot be reproduced from the page |
| `genVersion` | exists; no control |
| `blockSize` | exists; no control |
| `skipUnavailable` | exists; no control |

**(B) Missing from the web API as well — new endpoints or new fields are needed.**

| missing | what it needs |
|---|---|
| **plan before running** (`--dry-run`, `--calibrate`) | `POST /api/search/plan`. **The biggest gap**, because the user's stated requirement is estimates shown *before* the run. Today `#planCard` only appears after the run has started. |
| **results to a file** (`--out`) | the GUI holds results in memory only; nothing is persisted, so `--keep all`, the size estimate and the slice/cleanup story have nowhere to land |
| **resume** (`--resume`, `--checkpoint`, `--checkpoint-every`) | a GUI run cannot survive a reboot, which makes a multi-day run GUI-impossible by construction |
| **`--all`** | no explicit whole-space affordance and no warning/confirm dialog |
| **`--no-prefilter`** (audit mode) | no field on `SearchQuery` |
| **`--approx`** | no field (harmless today — this build ships no heuristic prefilter) |
| **`vseed explain <seed> <query>`** | no endpoint. The per-goal breakdown exists only as a `title` tooltip on a table row. |
| **`vseed search --schema`** | `/api/meta` serves the metric list but not the schema text |
| **`vseed presets show > mine.json`** | the GUI can load a preset but cannot export an editable query file; "Copy query" only works *after* a run has started (`btnCopyQuery` is enabled by the SSE `started` event) |
| **disk and slice metrics** | `SearchProgress` has no `bytesWritten`, `projectedBytes`, `freeBytes`, `hitsKept`, `evicted`, `sliceIndex` — see §5.4(b) |
| **strategy / on-limit / slice** | new in user-decisions; absent from *both* surfaces, so they must be designed into both at once |

**(C) Things the GUI has that the CLI does not**, listed so parity is understood as two-way: the
slippy map, the point probe (`/api/at`), the location-marker overlay and the ruler. A CLI user gets
`vseed map` and `vseed at`, which cover the first two. No action needed, but the README should say so
rather than implying the GUI is a subset.

---

## 6. What this changes in the existing code (for whoever implements it)

Read-only analysis, so nothing was edited. The touch points, with why:

| file | change |
|---|---|
| `src\SeedLab.Search\Criteria\QueryModel.cs` | add `Goal.Prefer`, `Goal.MeasureWithin`, `Goal.BridgeWidth`; `SearchSpec.Strategy`, `.OnLimit`, `.Slice`; `OutputSpec.Ranking`, `.First`; default `Keep` 200 -> 1000 |
| `Criteria\MetricCatalog.cs` | add `RecommendedDirection` and `DefaultAnchor` per metric (§2.5, §2.6); add a `CostClass` hint |
| `Criteria\QueryReader.cs`, `Criteria\schema.md` | read and document the new keys; unknown keys must stay an error |
| `Evaluation\CompiledQuery.cs` | resolve `recommended` -> a concrete direction at compile time; replace `Score(v)` with the two-number model of §2.5; add the §3.1 refusal check |
| `Evaluation\SeedEvaluator.cs` line 383 | `r.Score = ...` becomes `r.Match` and `r.Quality` |
| `Evaluation\SeedResult.cs` | `Score` -> `Match` + `Quality` + `Ranked`; per-goal `Q` and resolved preference on `GoalOutcome` |
| `Execution\SearchRun.cs` lines 346-350 | the comparator gains the `M` level |
| `Output\ResultWriter.cs` | new columns/fields; bump `defs` to 2 |
| `src\SeedLab.Web\Search\SearchModel.cs` | `SearchGoal.Prefer` etc.; the `SearchProgress` fields of §5.4(b) |
| `src\SeedLab.Web\WebServer.cs` | `POST /api/search/plan`, `GET /api/search/{id}/export`, `POST /api/explain` |
| `wwwroot\index.html`, `wwwroot\app.js` | the whole of §5.1–§5.6 |
| `docs\search.md`, `docs\web.md`, `README.md` | the new flags, the refusal, the two numbers |

---

## 7. What I could not settle, and what would settle it

1. **Absolute per-seed costs.** Everything here was measured on a machine at 96–100 % load from other
   agents, and the same calibration varied 2.0x across ten minutes. *Settled by:* re-running
   `vseed search <q> --dry-run --calibrate 32` for each of the queries in
   `...\scratchpad\searchanalysis\q-*.json` on an idle machine. The query files are there and the
   ratios in this document should reproduce.
2. **How much of a T5 seed is the 2048² biome-point grid.** Prefix 1 costing the same as prefix 32
   proves the fixed cost dominates, but not what fraction it is. *Settled by:* a stopwatch around the
   point-grid build inside the oracle, reported like `pregen` already is in the cost block. This is
   worth doing: it is the largest unexplored lever for location queries.
3. **`vegetation.json` and `altbiomes.json` goal costs.** Both files are dumped and `altbiomes` has a
   golden, but no tier consumes them, so the §1.4 rows are marked `unmeasured` rather than estimated.
   *Settled by:* a prototype metric and a `--dry-run`.
4. **The right default `bridge_width`.** valheim.gaming.tools uses 64 m. Whether that matches what a
   Valheim player calls "one island" (can you walk it? swim it? is a 60 m channel a river or a
   strait?) is a judgement call for DoomMachine, not a fact I can measure. *Settled by:* asking the author,
   and by rendering one seed's island labelling at 0 m and 64 m side by side.
5. **Whether `M` should count must-have goals when a seed is shown as a near-miss.** The reference
   site filters must-haves out entirely, so the question only arises if SeedLab ever offers "show me
   worlds that missed by one goal". Out of scope here; flagged so it is not decided by accident.
6. **`bytes_per_record` for multi-goal queries.** Measured for a one-goal query (307 B jsonl, 90 B
   csv). The per-extra-goal figures in §4.4 are arithmetic on the record shape, not measurements.
   *Settled by:* the same `wc -c` on a five-goal query.
7. **Whether the reference site's capture is still current.** I did not re-fetch
   valheim.gaming.tools/seeds; §2.1 quotes the capture supplied with the task. *Settled by:* a fresh
   capture before shipping parity claims.
