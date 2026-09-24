# Is a full-resolution (12 m) seed search ever worth it?

A resolution study over 2,560 seeds (islands: 512), measured 2026-09-23 with SeedLab's own
measurement code. Everything here is a measurement; where something is a guess it says so.

---

## 0. The answer, first

**The honest answer is "it depends on the goal, and the two cases are opposite".**

| goal family | is a coarse pass + margin + exact re-measure as good as a fine pass? | notes |
|---|---|---|
| **Bosses, traders, dungeons, Fuling villages, tar pits, geysers, Charred Fortresses** | **The question does not apply.** | Location placement runs on the game's own hard-coded 2048 x 2048 @ 12 m point grid (`BiomeGrid.Size = 2048`, `src\SeedLab.Locations\BiomeGrid.cs:33`). `--grid` does not touch it. These goals are *already* exact at any setting. |
| **Bulk area / share goals** - biome area, biome share, land/ocean share, area within R, area above H | **YES - zero false negatives on this sample.** A **1 % margin at G24** lost **zero** of 2,560 seeds on all eleven such goals tested, at 1.2x survivors to re-measure. G96 needs 5 %, G384 needs 30 % (and then it filters nothing). It is an empirical margin, not a proof - see 5 and 9.1. | A fine pass finds nothing the coarse+margin pass misses. |
| **Extremal / topological goals** - largest island, spawn island, island count, highest peak, largest biome patch, coastline length, "nearest Mountain" | **NO.** There is no margin that is both safe and selective. The margin needed to reach zero false negatives admits 90-100 % of all seeds, i.e. the coarse pass stops filtering at all. | This is the honest case for a fine pass. |

**And then the cost twist that decides it.** The 4.6 days / 7.5 years spread is a **biome-only**
number. Every goal in the third row needs `GetBiomeHeight`, which needs the lake/river/stream
pre-generation, which is a fixed **570-617 ms per seed** that the grid cannot reduce. Measured here:

| whole-world query | per-seed CPU at G12 | per-seed CPU at G384 | what coarsening buys |
|---|---|---|---|
| biome-only (deferred pre-generation) | 1,324 ms | 1.14 ms | **1,162x** |
| anything needing heights or islands | 617 + 3,593 = 4,210 ms | 617 + 5.6 = 623 ms | **6.8x** |

So the coarse grid is a huge lever exactly where it is safe (biome areas), and almost no lever at all
exactly where it is dangerous (islands, peaks, land). **Coarsening an island or height query is a bad
trade twice over: it saves 6.8x and it is the only place where it silently loses matches.**

### What that means for the toggle the user asked for

Ship it - but not as "coarse (fast) vs fine (slow)". Ship it as three named modes, because that is
what the data supports:

1. **Exact (default).** `grid = 12`. This is already SeedLab's default (`QueryModel.cs:160`).
   For a height/island query it costs only 6.8x more than the coarsest setting, so for those queries
   **there is no reason to offer anything else**.
2. **Screen then verify (recommended for biome-area goals).** Coarse pass with an automatic margin,
   then re-measure every survivor at G12. Zero false negatives on this sample at G24 + 1 %, and
   the whole-space biome scan drops from 17 years to days. This is strictly better than what the tool
   does today, because today a coarse run's answer is simply a *different* answer and nothing
   re-measures it.
3. **Coarse, reported as coarse.** What `--grid 384` does now. Fine for exploring, never for a
   "must have" goal. The GUI should say, per goal, what it costs in lost matches - the numbers are in
   section 4.

**Is there a case where the 7.5-year option finds something the 4.6-day option cannot?** Yes, and it
is not exotic: **largest island, spawn island, island count, highest peak and "nearest Mountain"**.
At G24 - the finest coarse grid anyone would use - "largest island >= 6 km2" already loses 32 of 643
true matches (5.0 %), and no margin below 35 % fixes it; a 35 % margin passes 2,461 of 2,560 seeds,
so the coarse stage has stopped being a filter. "Highest peak >= 450 m" loses 31.7 % at G48 and
82.3 % at G96. Those seeds are unreachable by the cheap route. That is the exact, named case.

**Is the reference site right to hedge?** Yes, and it under-hedges. valheim.gaming.tools's finest
setting is 128x128 (164.06 m). At that grid, on our sample: island count at A_min = 1 km2 is wrong
for 88 % of seeds, largest island is off by more than +10 % for 53 % of seeds, spawn island has a
median error of 44 %, and "Mountain within 800 m" loses 16 % of true matches. Their notes
("Island checks use their own finer sampling", "Sizes are estimates", "Spawn island follows the
starting stones") are the right instinct; "estimates" is doing a lot of work.

---

## 1. Method

### What was run

A standalone harness in this scratch directory, built against a **frozen snapshot** of SeedLab's own
measurement code (copied 2026-09-23 while another workflow was editing `src\SeedLab.*`, so the study
cannot be invalidated mid-run by an edit):

```
scratchpad\searchanalysis\study\src\WorldGen        <- src\SeedLab.WorldGen       (verbatim)
scratchpad\searchanalysis\study\src\Render          <- src\SeedLab.Render         (verbatim)
scratchpad\searchanalysis\study\src\SearchMetrics   <- src\SeedLab.Search\Metrics\*.cs
                                                        + Execution\Permutation.cs (verbatim)
scratchpad\searchanalysis\study\Program.cs          <- the harness (new)
```

Nothing under `E:\SteamLibrary\steamapps\common\Valheim\_ModSource\SeedLab` was written to. Build
output went to the scratch tree only.

**Caveat on the line citations.** They were read from the tree between 04:30 and 05:00 on
2026-09-23 while another workflow was actively editing `SeedLab.Search`, `SeedLab.Cli`,
`SeedLab.Web` and the presets. Files cited here by line number - `SearchGrids.cs`,
`QueryModel.cs`, `MeasurementPlan.cs`, `SeedSampler.cs`, `WorldMeasurement.cs`, `BiomeGrid.cs`,
`BiomeGeometry.cs`, `WorldGeneratorPort.cs` - had not changed as of 05:20, but
`CompiledQuery.cs`, `SeedEvaluator.cs`, `SearchCommand.cs` and several preset JSONs had.
Re-check any line number before quoting it downstream.

Every number below therefore comes from the **production** `SeedSampler`, `WorldMeasurement` and
`MeasurementPlan` - the same code `vseed search` runs - not from a re-implementation.

### Proof that the harness is the product

`vseed seed -1773433966 --min-island 10000` (the shipped Release CLI) against the harness's G12 row
for the same seed:

| figure | `vseed seed` | harness G12 |
|---|---|---|
| cells in world | 2,405,324 | 2,405,324 |
| land | 119.44 km2 | 119.439792 km2 |
| Meadows area | 10.23 km2 | 10.230624 km2 |
| Black Forest area | 48.04 km2 | 48.04488 km2 |
| components (all) | 9,349 | 9,349 |
| largest island | 4.21 km2 | 4.205664 km2 |
| nearest land | 110 m | 110.309 m |
| centre island | 3 cells | 432 m2 = 3 cells |

Identical.

### Sample

2,560 seeds drawn by `Permutation.Shuffle(index, 2^32, key = 0)` for `index = 0 .. 2559`, i.e. the
first 2,560 seeds of the search engine's own default shuffled scan order over the whole int32 space.
Reproducible: the same key and index range give the same seeds. (0.00006 % of the space - large
enough for the medians and p95s below, and stated as a sample everywhere, per the honesty rules in
`user-decisions.md` section 7.)

### Grids

`N = 2 * ceil(10500 / r)` samples per axis at `x_i = (i - N/2) * r + r/2`, `r = 12` special-cased to
`N = 2048` - `SearchGrids.ForSpacing`, `src\SeedLab.Search\Evaluation\SearchGrids.cs:19-41`.

| grid | N | cells | in-world cells | vs G12 |
|---|---|---|---|---|
| G12 (the game's own) | 2048 | 4,194,304 | 2,405,324 | 1x |
| G24 | 876 | 767,376 | 601,252 | 4.0x cheaper |
| G48 | 438 | 191,844 | 150,364 | 16x |
| G96 | 220 | 48,400 | 37,556 | 64x |
| **G164.0625** | 128 | 16,384 | 12,892 | 187x |
| G192 | 110 | 12,100 | 9,396 | 256x |
| **G218.75** | 96 | 9,216 | 7,232 | 333x |
| **G328.125** | 64 | 4,096 | 3,228 | 745x |
| G384 | 56 | 3,136 | 2,348 | 1024x |

The three starred grids reproduce valheim.gaming.tools's **Precise (128x128)**, **Balanced (96x96)**
and **Fast (64x64)** exactly: 10500 / 164.0625 = 64, / 218.75 = 48, / 328.125 = 32, all integers, so
`N` comes out at 128, 96 and 64. That is why they are in the table - so "how good is the reference
site's finest setting?" has a direct answer.

### What was measured, per seed, per grid

One `MeasurementPlan` requesting everything the search can ask for, over the whole world
(radius 10,500 m):

- per-biome area, share, nearest distance from (0,0), largest 4-connected patch - all 9 biomes;
- land area, water area, land share, coastline length, nearest land distance;
- highest peak, deepest point, area above 100 m and 200 m;
- island count at `A_min` = 1 cell / 0.1 km2 / 1 km2 / 10 km2, largest island area,
  spawn island area (definition rule 2: the component nearest the origin, absent beyond 500 m);
- land area within 500 m / 1 km / 2 km / 5 km;
- area within 2 km and 5 km, total and for Swamp / Mountain / Plains / Mistlands within 5 km.

Raw output: `scratchpad\searchanalysis\study.csv`, 2,560 x 9 = 23,040 rows, one per (seed, grid).

---

## 2. What a fine pass actually costs - the cost model, measured

### 2.1 Measurement conditions (say what else was running)

Another workflow was running a `vseed` benchmark on the same box throughout (a `vseed` process with
5,942 CPU-seconds accumulated was visible at the start and it kept running). **Every wall-clock
number here is therefore an upper bound and the single-thread figures are inflated by contention.**
Prefer the *ratios* and the *per-seed work counts*, which contention does not change.

- study run: 12 threads of 16, 2,560 seeds, 1,253 s wall, 2.04 seeds/s (nine grids per seed).
- `bench` run: 1 thread, 16 seeds, same box, same contention.

### 2.2 Per-seed CPU, from the study run (all nine grids, same seeds, same conditions)

`scratchpad\searchanalysis\study.log`:

| grid | cells | in-world cells | sample + all metrics, ms/seed CPU | vs G12 |
|---|---|---|---|---|
| G12 | 4,194,304 | 2,405,324 | 3,592.79 | 1.00x |
| G24 | 767,376 | 601,252 | 955.79 | 3.76x cheaper |
| G48 | 191,844 | 150,364 | 262.90 | 13.7x |
| G96 | 48,400 | 37,556 | 75.80 | 47x |
| G164.0625 | 16,384 | 12,892 | 28.87 | 124x |
| G192 | 12,100 | 9,396 | 20.99 | 171x |
| G218.75 | 9,216 | 7,232 | 16.97 | 212x |
| G328.125 | 4,096 | 3,228 | 8.08 | 445x |
| G384 | 3,136 | 2,348 | 5.64 | 637x |

plus a fixed **617.4 ms/seed** for `new WorldGeneratorPort(seed, 2)` with pre-generation.

### 2.3 The fixed cost that the grid cannot touch

`bench.csv`, single thread:

```
ctor_eager_ms_per_seed,570.064      <- WorldGeneratorPort with lake/river/stream pre-generation
ctor_deferred_ms_per_seed,0.000     <- deferPregeneration: true
```

`GetBiomeHeight` reaches `GetRiverWeight`, which forces pre-generation
(`WorldGeneratorPort.cs:886`, `if (m_pregenPending) EnsurePregenerated();`). So:

- a **biome-only** query pays 0 ms of construction and the grid is ~100 % of the cost;
- **any** height, land, island, peak, coastline or lava query pays 570-617 ms whatever the grid.

### 2.4 Region restriction is a bigger lever than the grid, and it is exact

`bench.csv`, `biome_only`, single thread, ms/seed:

| radius | G12 | G24 | G48 | G96 | G164* | G192 | G384 |
|---|---|---|---|---|---|---|---|
| 1,000 m | 8.16 | 2.43 | 0.57 | 0.16 | 0.066 | 0.051 | 0.014 |
| 2,500 m | 84.5 | 26.7 | 3.44 | 2.75 | 0.380 | 0.299 | 0.067 |
| 5,000 m | 250.0 | 206.2 | 14.6 | 6.08 | 1.55 | 1.19 | 0.291 |
| 10,500 m | 1,324.1 | 721.8 | 58.0 | 16.2 | 6.20 | 4.79 | 1.14 |

(The G24 row is noisy - 721.8 ms for 638,830 cells is 2.2x the per-cell cost of G12's row; that is
contention, not physics. The clean per-grid scaling is the study table in 2.2.)

The point: **`Swamp within 2 km` at the game's own 12 m grid costs 84 ms/seed. The same question over
the whole world costs 1,324 ms.** Region restriction is exact - it changes nothing about the answer
(`SeedSampler.RowSpan`, `src\SeedLab.Search\Metrics\SeedSampler.cs:64-88`) - while the grid changes
the answer. **Spend the restriction budget before the grid budget.**

### 2.5 Projected whole-space cost, and why the brief's two numbers are not comparable

Using the spec's own measured parallel factor of **10.5x on 16 logical threads**
(`docs\specs\07-features.md` section 3.4 - not 16x; SMT gives ~31 % on top of 8 cores for this
workload):

| query shape | per-seed CPU | whole space (4,294,967,296 seeds) |
|---|---|---|
| biome-only, whole world, **G384** | 1.14 ms | **5.4 days** |
| biome-only, whole world, **G12** | 1,324 ms | **17.2 years** |
| biome-only, 2.5 km disc, **G12** | 84.5 ms | 1.1 years |
| biome-only, 1 km disc, **G12** | 8.16 ms | **39 days** |
| heights/islands, whole world, **G384** | 623 ms | 8.1 years |
| heights/islands, whole world, **G12** | 4,210 ms | 54.6 years |

The brief's "4.6 days vs 7.5 years" is the first two rows measured on an idle box; mine are ~2.3x
slower because of the concurrent benchmark. The shape is the same and the shape is what matters:

- **biome-only: coarsening is a ~1,000x lever.**
- **heights/islands: coarsening is a 6.8x lever** - and it is the only place it costs accuracy.

A "full-resolution toggle" that applies to a height/island query is therefore buying 6.8x of cost for
correctness that the coarse grid could not deliver at any price. A "full-resolution toggle" on a
*biome-area* query is buying 1,000x of cost for accuracy a 2 % margin already delivers.

---

## 3. Error distributions - the short version

Full tables in section 10. The shape of the result:

### 3.1 Bulk area and share metrics are almost exactly right, and unbiased

Median |relative error| against G12, per-biome area:

| grid | Meadows | Swamp | Mountain | BlackForest | Plains | Mistlands | land area | land share |
|---|---|---|---|---|---|---|---|---|
| G24 | 0.103 % | 0.092 % | 0.067 % | 0.049 % | 0.047 % | 0.036 % | 0.033 % | 0.000115 |
| G48 | 0.212 % | 0.236 % | 0.193 % | 0.102 % | 0.102 % | 0.073 % | 0.084 % | 0.000300 |
| G96 | 0.583 % | 0.716 % | 0.645 % | 0.295 % | 0.329 % | 0.207 % | 0.199 % | 0.000676 |
| G164* | 1.33 % | 1.43 % | 1.15 % | 0.662 % | 0.783 % | 0.522 % | 0.353 % | 0.00119 |
| G192 | 1.72 % | 1.95 % | 1.50 % | 0.725 % | 0.775 % | 0.506 % | 0.445 % | 0.00151 |
| G384 | 4.15 % | 5.53 % | 4.66 % | 2.16 % | 2.12 % | 1.83 % | 0.987 % | 0.00334 |

The worst case over 2,560 seeds is 4-6x the median (Swamp area: median 0.092 % / max 0.438 % at G24;
median 5.53 % / max 33.5 % at G384). The **signed** median is within +-0.4 % of zero at G24-G96 and
within +-2.5 % at the coarsest grids, with no consistent direction at any grid: coarsening a bulk
area is an unbiased sample-mean estimate whose error falls like 1/sqrt(cells). That is exactly why a
small margin works, and why it does not work for anything in 3.2.

Three rows in the full tables are degenerate; the reasons are worth stating rather than leaving them
looking broken, and they double as a check on the harness:

- **Ashlands area is identical in every seed** (median = p95 = max error at every grid). Verified in
  the code: `GetBiome` tests `IsAshlands(wx, wy)` - purely geometric, `ashlandsMinDistance = 12000`,
  `ashlandsYOffset = -4000`, wobble `WorldAngle = sin(atan2(x,z)*20)`, no seed term - **before** the
  `baseHeight <= oceanLevel` cut (`WorldGeneratorPort.cs:1210-1215`). Deep North's test
  `IsDeepnorth` is equally geometric but is applied **after** that ocean cut
  (`WorldGeneratorPort.cs:1216-1222`), so Deep North area *does* vary with the seed. That asymmetry
  is a real, verified generator fact.
- **"any area within R"** is the count of in-world cells inside a disc: geometry only, no seed.
- **Mistlands area within 5 km is 0 for every seed at every grid** - the Mistlands band starts at
  5,900 m (`BiomeGeometry.Band`, `src\SeedLab.Search\Feasibility\BiomeGeometry.cs:40-42`).

### 3.2 Extremal and topological metrics are not

Median |relative error|, same sample:

| grid | largest island | spawn island | highest peak (m) | coastline | largest Plains patch |
|---|---|---|---|---|---|
| G24 | 0.260 % | 14.0 % | 2.31 m | 24.4 % | 0.285 % |
| G48 | 1.71 % | 33.6 % | 9.25 m | 48.1 % | 3.12 % |
| G96 | 4.92 % | 43.4 % | 27.7 m | 63.8 % | 11.9 % |
| G164* | 14.2 % | 47.0 % | 54.8 m | 70.6 % | 17.8 % |
| G192 | 23.4 % | 52.2 % | 62.1 m | 72.1 % | 19.6 % |
| G384 | 79.0 % | 70.8 % | 116 m | 78.1 % | 24.2 % |

and the **max** over 2,560 seeds: largest island 144 % at G24 and 552 % at G384; spawn island
**4,966,700 %** at G24 (a 12 m grid can see a 3-cell, 432 m2 islet at the origin that any coarser
grid replaces with the mainland - `vseed seed -1773433966` is exactly that world).

Two of these are *systematically* wrong rather than noisily wrong, and that matters:

- **coastline length** is a fractal measured in units of the grid: signed median error is -24 % at
  G24 and -78 % at G384, with p95 within 1.5 points of the median. It is not an estimate of the G12
  number, it is a different number. Section 5 shows that **no margin can rescue it**.
- **highest peak** is a maximum over samples, so coarsening can only lose it: the required margin in
  the ">= T" direction is 5.5 % at G24 and 51.6 % at G384, while in the "<= T" direction it is under
  1 % at every grid. One-sided, exactly as expected.

### 3.3 Nothing ever disappears at world scale; things disappear locally

Over 2,560 seeds and all nine grids, **no biome that exists at G12 is ever absent at a coarser
grid**, and no nearest-distance ever went to infinity. "Does this world contain any Mistlands"
is answered identically at G384 and G12 for all 2,560 seeds.

That is *not* the case inside a radius. "A nearby X", `nearest_distance <= T`, false-negative rate:

| biome | T | G24 | G48 | G96 | G164* | G192 | G384 |
|---|---|---|---|---|---|---|---|
| Mountain | 800 m | 0.6 % | 2.1 % | 8.0 % | 15.7 % | 25.8 % | **100 %** |
| Mountain | 1,000 m | 0.4 % | 1.2 % | 5.0 % | 9.8 % | 13.6 % | 49.8 % |
| Mountain | 1,500 m | 0.1 % | 0.2 % | 0.8 % | 2.2 % | 3.3 % | 20.6 % |
| BlackForest | 600 m | 0.0 % | 0.0 % | 0.1 % | 2.9 % | 1.4 % | **100 %** |
| Meadows / Swamp / Plains / Mistlands | any | 0.0 % | 0.0 % | 0.0 % | 0.0 % | 0.0 % | ~0 % |

Mountain is the hard one because a Mountain patch near the centre is small: the margin that removes
every false negative is **927 m at G24** and **3,015 m at G384** (section 5). "Black Forest within
600 m" is unanswerable at G328/G384 because the nearest sample point to the origin is 232/271 m out
and the patch can sit between samples.

---

## 4. Threshold flips - the number the user cannot otherwise see

Thresholds placed at the 10th/25th/50th/75th/90th percentile of the **G12** distribution, i.e.
deliberately where seeds cluster. A false negative is *coarse says no, G12 says yes*. Full tables in
section 10; the median-threshold row:

| metric (">= median") | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows area | 0.3 % | 1.2 % | 4.3 % | 7.8 % | 15.8 % | 11.2 % | 10.4 % | 17.4 % |
| Swamp area | 0.4 % | 1.4 % | 4.2 % | 8.4 % | 8.7 % | 16.6 % | 29.1 % | 20.0 % |
| Mountain area | 0.5 % | 0.7 % | 4.1 % | 8.2 % | 10.0 % | 13.0 % | 17.8 % | 21.9 % |
| Plains area | 0.2 % | 0.8 % | 3.6 % | 2.5 % | 7.0 % | 7.1 % | 18.4 % | 20.2 % |
| Mistlands area | 0.5 % | 2.3 % | 2.2 % | 11.6 % | 8.4 % | 7.8 % | 17.5 % | 9.3 % |
| land share | 0.2 % | 1.6 % | 3.2 % | 7.2 % | 8.3 % | 5.9 % | 15.5 % | 18.8 % |
| Swamp within 5 km | 0.5 % | 1.9 % | 4.9 % | 9.6 % | 7.3 % | 11.6 % | 28.0 % | 23.1 % |
| land within 2 km | 0.6 % | 0.6 % | 0.6 % | 3.8 % | 10.5 % | 11.2 % | 5.1 % | 7.1 % |
| largest island area | 4.6 % | 6.7 % | 8.4 % | 7.7 % | 5.5 % | 3.8 % | 0.7 % | 0.8 % |
| spawn island area | 1.0 % | 1.6 % | 1.7 % | 2.7 % | 3.8 % | 3.8 % | 9.2 % | 8.7 % |
| island count (>= 1 km2) | 6.9 % | 5.8 % | 4.4 % | 12.8 % | 23.7 % | 22.1 % | 74.0 % | 61.4 % |
| **highest peak** | **14.5 %** | **37.2 %** | **46.1 %** | **74.1 %** | **82.3 %** | **86.5 %** | **93.4 %** | **95.9 %** |
| **coastline length** | **100 %** | **100 %** | **100 %** | **100 %** | **100 %** | **100 %** | **100 %** | **100 %** |

Read the two numbers the user actually asked about:

- **A coarse run at 384 m on a biome-area "must have" silently drops about one match in five.**
  Not one in a thousand - one in five.
- **At the reference site's finest setting (164 m) it drops one in ten to one in eight.**
- **G24 drops 1 in 200 to 1 in 500** on bulk areas, and that residue is removable (section 5).

The `largest island` row looks better at G384 than at G24 only because coarsening *inflates* that
metric (it bridges straits), so a ">=" test over-accepts. The same coarsening is catastrophic for the
"<=" direction: `largest_island_area <= 3 km2` - the shipped `archipelago` preset's goal - loses
**4 of the 9** qualifying seeds at its own grid of 24 m (section 7).

Counts of false positives are in the tables too. They are harmless for correctness **only if the
tool re-measures the survivors at G12**, which today it does not: a `--grid 96` run reports the G96
numbers as the answer.

---

## 5. Margins - what makes a coarse pass lose nothing, and what it costs

The design question: relax the coarse threshold by a margin `m`, keep everything that passes, then
re-measure the survivors exactly. What `m` gives zero false negatives, and how many survivors?

Each cell below is `false negatives still lost / survivors to re-measure`, out of 2,560 seeds. Full
sweep for 15 goals in section 10.

### Meadows area >= 11 km2 (966 of 2,560 qualify at G12)

| margin | G24 | G48 | G96 | G164* | G192 | G384 |
|---|---|---|---|---|---|---|
| 0 % | 2 / 973 | 14 / 973 | 46 / 975 | 72 / 998 | 174 / 855 | 190 / 1131 |
| 1 % | **0 / 1116** | **0 / 1114** | 5 / 1111 | 47 / 1124 | 107 / 969 | 142 / 1272 |
| 2 % | 0 / 1252 | 0 / 1249 | **0 / 1247** | 20 / 1261 | 67 / 1102 | 142 / 1272 |
| 5 % | 0 / 1695 | 0 / 1688 | 0 / 1685 | 1 / 1687 | 4 / 1515 | 47 / 1697 |
| 10 % | 0 / 2203 | 0 / 2199 | 0 / 2193 | **0 / 2196** | **0 / 2082** | 8 / 2061 |
| 20 % | 0 / 2544 | 0 / 2546 | 0 / 2547 | 0 / 2545 | 0 / 2529 | **0 / 2508** |

### Mountain area >= 12 km2 (345 of 2,560 - a selective goal)

| margin | G24 | G48 | G96 | G164* | G192 | G384 |
|---|---|---|---|---|---|---|
| 0 % | 5 / 343 | 4 / 349 | 23 / 345 | 42 / 352 | 63 / 349 | 109 / 510 |
| 1 % | **0 / 420** | **0 / 413** | 4 / 417 | 19 / 413 | 33 / 427 | 97 / 594 |
| 2 % | 0 / 487 | 0 / 488 | **0 / 503** | 4 / 504 | 20 / 503 | 74 / 712 |
| 5 % | 0 / 826 | 0 / 831 | 0 / 824 | **0 / 811** | **0 / 857** | 38 / 942 |
| 20 % | 0 / 2472 | 0 / 2469 | 0 / 2466 | 0 / 2460 | 0 / 2450 | **0 / 2314** |

**The rule that falls out of the sweep.** The margin was searched over
{0.5, 1, 2, 3, 5, 7, 10, 15, 20, 30, 40, 50} % for each of ten bulk goals
(`b1/b2/b3/b5/b9_area`, `land_area >= 125 km2`, `land_share`, `Swamp within 5 km`,
`land within 2 km`, `area above 100 m`). Smallest margin giving **zero** false negatives:

| goal (true matches) | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows area (966) | 0.5 % | 1 % | 2 % | 7 % | 7 % | 10 % | 20 % | 15 % |
| Swamp area (742) | 0.5 % | 1 % | 3 % | 7 % | 7 % | 10 % | 20 % | 30 % |
| Mountain area (345) | 0.5 % | 0.5 % | 2 % | 5 % | 5 % | 5 % | 15 % | 15 % |
| Plains area (641) | 0.5 % | 0.5 % | 2 % | 2 % | 3 % | 3 % | 7 % | 10 % |
| Mistlands area (573) | 0.5 % | 0.5 % | 0.5 % | 2 % | 2 % | 2 % | 5 % | 5 % |
| land area >= 125 km2 (14) | 0.5 % | 0.5 % | 0.5 % | 0.5 % | 2 % | 1 % | 3 % | 2 % |
| land share (708) | 0.5 % | 0.5 % | 1 % | 2 % | 2 % | 2 % | 5 % | 5 % |
| Swamp within 5 km (889) | 0.5 % | 1 % | 5 % | 10 % | 10 % | 15 % | 30 % | 30 % |
| land within 2 km (681) | 0.5 % | 1 % | 2 % | 5 % | 10 % | 10 % | 10 % | 15 % |
| **worst of the ten** | **0.5 %** | **1 %** | **5 %** | **10 %** | **10 %** | **15 %** | **30 %** | **30 %** |
| *`area above 200 m` (942)* | *1 %* | *3 %* | *7 %* | *20 %* | *30 %* | *40 %* | *50 %* | *>50 %* |

`area above 200 m` is listed separately because it is not a bulk metric at all - it counts only high
mountain tops, so it behaves like `highest_peak` (section 3.2), and it is the reason the naive
"one margin for every area goal" rule needs a carve-out.

What the margin costs, for the most selective of the bulk goals (`Mountain area >= 12 km2`, 345 true
matches of 2,560):

| grid | margin used | survivors to re-measure | inflation | verdict |
|---|---|---|---|---|
| G24 | 1 % | 420 | **1.22x** | ideal |
| G48 | 1 % | 413 | **1.20x** | ideal |
| G96 | 2 % | 503 | **1.46x** | good |
| G164* | 5 % | 811 | 2.35x | workable |
| G192 | 5 % | 857 | 2.48x | workable |
| G384 | 20 % | 2,314 | **6.7x - 90 % of the sample survives; the filter is dead** | pointless |

A 1.2x survivor inflation is nothing: the stage-2 exact re-measure runs on 420 seeds instead of 345.
A 6.7x inflation at G384 means 2,314 of 2,560 seeds go to stage 2, so the coarse pass cost you a full
extra pass and filtered nothing.

**Where no useful margin exists.** These are the ones to be blunt about:

| goal | what the sweep says |
|---|---|
| `largest island >= 6 km2` (643 true) | G24 at 0 % loses 32; at 10 % still loses 14; zero FN needs **35 %**, which passes **2,461 of 2,560**. Dead at every grid. |
| `spawn island >= 3 km2` (802 true) | **never reaches zero.** Even a 50 % margin at G24 still loses 8, and passes 2,068 seeds. Unbounded. |
| `island count (>= 1 km2) >= 40` (553 true) | zero FN needs a margin of 8 counts (20 %) at G24-G164, which passes 2,480-2,532 seeds. Dead. |
| `highest peak >= 450 m` (706 true) | G24 happens to lose 0 at 0 % on this sample, but G48 needs 5 %, G96 needs >10 %, G164+ needs >50 %. Fragile, and one-sided. |
| `coastline >= 2,750 km` (718 true) | at G24 the margin must exceed **26 %** before a single seed passes at all; at G48 **50 %**; at G96 and coarser no margin under 50 % passes anything. Not an approximation of the G12 number at all. |

These five are exactly the metrics whose definition depends on *connectivity or extrema* rather than
on *counting cells*. Counting is averaging, and averages converge; connectivity and maxima do not.

**Honesty about what "zero false negatives" means here.** It means zero on 2,560 seeds, which is
0.00006 % of the space. It is an empirical margin, not a proof. There is no proof available: the
spec already records why (`07-features.md` section 3.2.7) - `Mathf.PerlinNoise` is native
`[FreeFunction]` code with an unverified gradient set and fade curve, so there is no Lipschitz
constant and no interval arithmetic, so no coarse rejection can be made *provably* sound. A margin
derived from a sample is the best available, and the tool must say so on the record it writes.

---

## 6. Islands - the topological case, measured

512 seeds, seven grids, `scratchpad\searchanalysis\islands.csv`. This run also answers a question the
spec left open: `07-features.md` section 2.2 carries a five-row table measured on **one** seed by
decimating G12 indices, with the caveat *"the table is 'G12 decimated by k', not G_r as defined
above... **Unverified:** the same table evaluated on the section 2.1 G_r points."* The study below is
that table, on the real G_r points, over 512 seeds.

### 6.1 The raw component count collapses, as the spec says - but by more than 45x

| grid | median components | vs G12 | min | max |
|---|---|---|---|---|
| G12 | 9,868 | 1.000x | - | - |
| G24 | 4,892 | 0.496x | 0.455x | 0.531x |
| G48 | 1,706 | 0.173x | 0.152x | 0.190x |
| G96 | 499 | 0.051x | 0.043x | 0.058x |
| G164* | 239 | 0.024x | 0.021x | 0.029x |
| G192 | 208 | 0.021x | 0.018x | 0.024x |
| G384 | 128 | 0.013x | 0.010x | 0.016x |

G12 -> G192 is **47x**, matching the spec's "45x over a 16x resolution change" almost exactly.
G12 -> G384 is **77x**. The spec's conclusion stands: the raw count is not a publishable metric.

### 6.2 But the 1-hectare count is NOT stable to +-5 %, and the spec says it is

`07-features.md` section 2.2: *"The >= 1 ha count varies by **+-5%**"* (one seed, decimated grid).
Over 512 seeds on the proper G_r points:

| grid | exact match | median signed error | p95 \|rel err\| | max \|rel err\| | worst signed |
|---|---|---|---|---|---|
| G24 | 2.1 % | -5 | **8.58 %** | 14.7 % | -37 .. +21 |
| G48 | 1.0 % | +20 | **18.7 %** | 30.3 % | -20 .. +61 |
| G96 | 1.4 % | +4 | 12.9 % | 22.5 % | -39 .. +41 |
| G164* | 0.6 % | +15 | 18.8 % | 37.9 % | -30 .. +78 |
| G192 | 2.0 % | -16 | 16.4 % | 25.7 % | -62 .. +26 |
| G384 | 0.0 % | -97 | **50.0 %** | 55.8 % | -138 .. -49 |

**This is a correction to the knowledge base.** +-5 % was one seed; the true spread at G24 is
+-8.6 % at p95 and +-14.7 % at worst, and it is *not* monotone in the grid (G96 is better than G48).
At A_min = 1 km2 the picture is the same: exact match 19.3 % at G24, 7.2 % at G384.

At A_min = 10 km2 the metric finally stabilises - 100 % exact at G24 and G48, 99.9 % at G96 - because
a 10 km2 component is 69,444 cells at G12 and still 68 cells at G384, far above the noise.

### 6.3 Merging across straits is real, directional and common

The component that sits **where G12's largest island is**, as a ratio to G12's largest island area:

| grid | median | p95 | max | fraction >= 1.10x | fraction >= 1.5x |
|---|---|---|---|---|---|
| G24 | 1.000x | 1.134x | 1.64x | 7.6 % | 0.6 % |
| G48 | 1.001x | 1.246x | 2.10x | 11.3 % | 1.2 % |
| G96 | 1.003x | 1.246x | 2.12x | 13.1 % | 1.4 % |
| G164* | 1.007x | 1.530x | 2.43x | 19.9 % | 5.7 % |
| G192 | 1.009x | 1.588x | 2.53x | 22.5 % | 6.2 % |
| G384 | 1.011x | 2.231x | 4.90x | **40.0 %** | **20.9 %** |

So: coarsening **merges** (the ratio is >= 1 almost everywhere, and the median largest-island error is
signed **+82.9 %** at G384), it does so by bridging channels narrower than a cell, and by 164 m one
world in five has a largest island at least 1.5x too big. The spec's rule *"use r <= 24 m for any
largest-island criterion and state +-10 %"* is directionally right but too weak: at G24,
**16.9 % of seeds are outside +-10 %** (14.8 % high, 2.1 % low) and the worst is +107 %.

### 6.4 Spawn island: the worst metric in the tool, and anchoring does not fix it

`spawn_island_area` under rule 2 (the land component nearest the origin,
`WorldMeasurement.MeasureIslands`, `src\SeedLab.Search\Metrics\WorldMeasurement.cs:246-290`).
This is the 512-seed subset, so the medians differ slightly from the 2,560-seed table in 3.2
(14.0 % vs 10.6 % at G24 - same story, smaller sample):


| grid | median \|rel\| | p95 | max | fraction off by >2x or <0.5x | anchored median \|rel\| | "no spawn island" wrongly reported |
|---|---|---|---|---|---|---|
| G24 | 10.6 % | 743 % | 4,517,500 % | 19.5 % | 9.96 % | 0 / 512 |
| G48 | 28.1 % | 1,889 % | 4,556,700 % | 30.5 % | 28.0 % | 0 / 512 |
| G96 | 39.3 % | 56,656 % | 4,505,500 % | 34.0 % | 39.3 % | 0 / 512 |
| G164* | 43.5 % | 77,691 % | 4,953,284 % | 37.3 % | 43.5 % | 0 / 512 |
| G192 | 42.4 % | 113,955 % | 5,631,900 % | 35.4 % | 41.7 % | 0 / 512 |
| G384 | 62.7 % | 86,765 % | 7,270,300 % | 42.0 % | 57.1 % | **19 / 512 (3.7 %)** |

**One in five worlds has a spawn island that is out by more than a factor of two at G24.** At G96 -
the grid the shipped `gentle-start` preset uses for a **must-have** goal - it is one in three.

I expected the cause to be the rule-2 definition picking a different component on a coarser grid, and
that **spec rule 1** (anchor on the `StartTemple` instance, which comes from location placement and
is therefore grid-independent) would fix it. **It does not.** The `anchored` column above re-measures
the component that physically contains G12's spawn-island point, and it is essentially as bad -
median 9.96 % vs 10.6 % at G24, identical maxima. The real cause is that the spawn island is very
often a *tiny* component at G12: `vseed seed -1773433966` reports `centre island 0.00 km2, 3 cells`,
432 m2 - and any grid coarser than 12 m either misses it or merges it into the mainland 100 m away.
Rule 1 would fix *which* island is named; it cannot fix *how big a 432 m2 island is* when the cell is
576 m2.

Anchor drift itself is modest (median 8.5 m at G24, 263 m at G384) and at G384 it is enough to push
19 of 512 worlds past the 500 m "spawn area is at sea" cutoff and report **no spawn island at all**
for a world that has one.

### 6.5 Is the reference site right to hedge?

Yes. Their help text says island area *excludes water*, that river crossings up to 64 m wide stay
within one island, that the spawn island *follows the starting stones*, and that **sizes are
estimates**. Every one of those hedges is doing real work:

- "river crossings up to 64 m wide stay within one island" is a deliberate *merge*, i.e. they have
  already decided that strait-bridging is acceptable - sensible, and it makes their number more
  stable than ours, but it is a different definition again.
- "spawn island follows the starting stones" is our rule 1 - the right anchor, and as 6.4 shows, not
  sufficient.
- "sizes are estimates" at 128x128 (164 m) means, on this sample: largest island wrong by >10 % for
  53 % of worlds, spawn island median error 44 %, island count at 1 km2 exactly right for 12 % of
  worlds. The hedge is correct and it is not strong enough to be read as a warning.

---

## 7. What this says about SeedLab's own shipped presets

Replaying the shipped presets' goals at their own `search.grid` (`presets.md`):

| preset | grid | goal replayed | truth at G12 | what the preset run would report |
|---|---|---|---|---|
| `gentle-start` | 96 | `spawn_island_area >= 3,000,000` (**must**) | 802 of 2,560 | 1,565 "matches": **20 real matches lost (2.5 %) and 783 of the 1,565 reported (50 %) are not matches at all** |
| `archipelago` | 24 | `largest_island_area <= 3,000,000` + `land_area` 80-130 km2 | 9 of 2,560 | 5: **4 of the 9 lost (44 %)** |
| `archipelago` | 24 | `island_count(A_min = 1 ha) >= 120` (**must**) | **512 of 512** | 512 - but the goal is a no-op: every world in the sample has >= 120 one-hectare islands at G12 (median 9,868 components, hundreds of them >= 1 ha). At G384 it would lose 17.2 %. |
| `balanced` / `balanced-biomes` | 192 | biome share goals | - | bulk shares at 192 m are fine (median error 0.0015 in share units); this preset's grid is a reasonable choice |
| `coastal-builder`, `iron-rich`, `large-continents` | 24 | area goals at 24 m | - | 1 % margin would make these lossless; today they have no margin |

Two concrete, actionable defects:

1. **`gentle-start` uses `spawn_island_area` as a `must` at grid 96.** Half of everything it reports
   is not a match. Either raise it to `grid: 12` (the goal is height-based, so the grid is only 6.8x
   of the cost - section 2), or re-measure survivors at G12 before writing them out.
2. **`archipelago`'s `island_count >= 120` at 1 ha never rejects anything**, so the preset is really
   just its two `nice` goals, one of which (`largest_island_area <= 3 km2`) is the single most
   grid-sensitive test in the tool. It should say so, or use a bigger `A_min`.

---

## 8. Recommendation

### 8.1 Per goal family

| goal family | measured verdict | what to do |
|---|---|---|
| bosses, traders, dungeons, world features, all 183 location types | grid-independent by construction | nothing - state in the GUI that the grid control does not affect these |
| biome area, biome share, land/ocean share, `area_within` | coarse + margin + exact re-measure is **as good as a fine pass**, zero FN on 2,560 seeds | **screen at G24 or G48 with a 1 % margin** (G96 needs 5 %), then re-measure every survivor at G12. Cost: 1.2-1.5x survivors. Do not go coarser than G96: G164 needs 10 % and G384 needs 30 %, at which point nothing is filtered. |
| `nearest_distance` to a large biome (Meadows, Black Forest, Plains, Swamp, Mistlands) | safe with a metric margin | screen coarse, relax the radius by the measured margin (<= 937 m at G384, <= 46 m at G24), re-measure |
| `nearest_distance` to **Mountain** | margin is 927 m at G24, 3,015 m at G384 - usually bigger than the goal itself | **run this one at G12**, with region restriction (a 1 km disc at G12 is 8 ms/seed) |
| `highest_peak`, `area_above_height` at high H | one-sided and large; `area above 200 m` needs 3 % at G48 and 7 % at G96, `highest_peak` needs >10 % at G96 | G24 with a 3-5 % margin, or G12; never 96 m or coarser |
| `largest_island_area`, `spawn_island_area`, `island_count`, `coastline_length`, `largest_patch_area` | **no safe, selective margin exists at any grid** | **G12 only.** It costs 6.8x, not 1,000x, because the pre-generation dominates. |

### 8.2 The toggle, as the user asked for it

Ship the full-resolution option - but the data says the default should be the *opposite* of what the
framing assumed. Concretely, in the GUI, per goal:

- a **badge** on every goal saying `exact at this grid` / `needs a 1 % margin` / `not safe below 12 m`,
  driven by the table in 8.1;
- when any goal in the query is in the bottom row, **default the grid to 12 and say why**: "island
  and peak goals are only 6.8x more expensive at full resolution because the river pre-generation
  dominates - coarsening them saves little and loses matches";
- when every goal is in the second row, **default to screen-then-verify** with the margin filled in,
  and show both numbers: seeds screened at the coarse grid, and survivors re-measured at 12 m;
- the "run the whole grid at 12 m" toggle keeps its warning, but the warning should be *specific*:
  for a biome-area query it should say "this is 1,000x the cost for accuracy a 1 % margin already
  gives you"; for an island query it should say "this is 6.8x the cost and it is the only way to get
  the right answer".

### 8.3 One thing that is worth more than the toggle

`SeedSampler.RowSpan` already restricts every goal to its own disc, exactly. A 1 km goal at the
game's own 12 m grid is **8.16 ms/seed** - 162x cheaper than the same goal over the whole world, with
no loss of any kind. Most of the reference site's goals ("a nearby X", "keep Y far away", "number of
Z within R") are radius-bounded. **Surfacing the radius as a first-class control, and showing the
estimate move when it changes, buys more than the grid control does and costs nothing in
correctness.**

---

## 9. What is not settled, and what would settle it

1. **The margins are empirical, not proofs.** 2,560 seeds is 0.00006 % of the space. A margin that
   held on every one of them can still fail. A proof needs a Lipschitz bound on `Mathf.PerlinNoise`,
   which needs its gradient set, fade curve and normalisation verified - `07-features.md` section
   3.2.7 records the permutation table as found and the rest as **Unverified**. *What would settle
   it:* verify the port bit-exact against dumped native samples, derive the bound, then a genuinely
   sound coarse rejection tier becomes possible.
2. **Sample size for the tails.** The p99/max columns for `spawn_island_area` and
   `largest_island_area` are driven by a handful of pathological worlds. A 50,000-seed run at G12 +
   G24 only (no other grids) would cost about 4 hours on this box and would pin those tails properly.
3. **`island_count` non-monotonicity.** G96 beats G48 on the 1-ha count, repeatedly. I have not
   explained why; it is probably the interaction of `A_min = 10,000 m2` with the cell area
   (1 ha is 1.08 cells at G96, 4.3 at G48, 17 at G24), which makes the threshold itself move relative
   to the grid. *What would settle it:* sweep `A_min` continuously at each grid on the existing
   `islands.csv` component data - but that needs the component-size histogram, which this run did not
   keep.
4. **Wall-clock absolutes.** Everything was measured with another benchmark running. The ratios and
   per-seed work counts are sound; the "17.2 years" style figures are ~2.3x pessimistic against the
   brief's idle-box numbers. *What would settle it:* re-run `bench` on an idle machine.
5. **Location goals were not re-measured here.** I verified from the code that `--grid` cannot reach
   them (`BiomeGrid.Size` is a `const`, and `MeasurementPlan.NeedBiomes` is false for a location-only
   query, `src\SeedLab.Search\Metrics\MeasurementPlan.cs:16-22`). I did not run a location search at
   two grids to confirm empirically. *What would settle it:* one `vseed search` with a boss goal at
   `grid: 12` and `grid: 384` over the same 1,000 seeds - the result sets must be identical.
6. **The `anchored` spawn-island experiment used a geometric anchor, not the real `StartTemple`.**
   It used the point where G12's spawn island lies, which is the right *test* of "does anchoring
   help", but not the real rule 1. *What would settle it:* run the partial S5 (`StartTemple` only,
   `m_centerFirst`, `m_quantity = 1`) and anchor on the actual instance.

---

## 10. Data tables

Raw data: `study.csv` (2,560 seeds x 9 grids), `islands.csv` (512 x 7), `bench.csv`.
Note: another workflow is writing into the same `searchanalysis\` directory (`goal-model.md`,
`perf-*.txt`, `patch_*.py`, `q-*.json`, `rec.*`); those are not part of this study.
Scripts: `study\Program.cs`, `bench\Program.cs`, `islands\Program.cs`, `analyze.py`, `render.py`,
`extra.py`, `sweep.py`, `islands_an.py`, `presets.py`.


### 10.1 Error distributions, all metrics, all grids

seeds = 2560


### |relative error| vs G12 - median

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows area | 0.103% | 0.212% | 0.583% | 1.33% | 1.72% | 1.84% | 3.55% | 4.15% |
| Swamp area | 0.092% | 0.236% | 0.716% | 1.43% | 1.95% | 2.36% | 4.81% | 5.53% |
| Mountain area | 0.067% | 0.193% | 0.645% | 1.15% | 1.50% | 1.95% | 3.79% | 4.66% |
| BlackForest area | 0.049% | 0.102% | 0.295% | 0.662% | 0.725% | 0.928% | 1.80% | 2.16% |
| Plains area | 0.047% | 0.102% | 0.329% | 0.783% | 0.775% | 0.873% | 1.73% | 2.12% |
| AshLands area | 0.024% | 0.097% | 0.368% | 0.644% | 0.197% | 0.252% | 0.832% | 0.625% |
| DeepNorth area | 0.030% | 0.080% | 0.252% | 0.458% | 0.609% | 0.822% | 1.44% | 2.00% |
| Ocean area | 0.035% | 0.055% | 0.179% | 0.379% | 0.364% | 0.480% | 0.996% | 1.20% |
| Mistlands area | 0.036% | 0.073% | 0.207% | 0.522% | 0.506% | 0.665% | 1.28% | 1.83% |
| land area | 0.033% | 0.084% | 0.199% | 0.353% | 0.445% | 0.521% | 0.901% | 0.987% |
| water area | 0.023% | 0.053% | 0.123% | 0.255% | 0.230% | 0.314% | 0.524% | 0.506% |
| largest island area | 0.260% | 1.71% | 4.92% | 14.2% | 23.4% | 33.1% | 68.0% | 79.0% |
| spawn island area | 14.0% | 33.6% | 43.4% | 47.0% | 52.2% | 50.1% | 70.4% | 70.8% |
| land area within 500 m | 0.684% | 2.53% | 3.55% | 9.76% | 13.0% | 6.00% | 44.1% | 23.8% |
| land area within 1 km | 0.197% | 0.561% | 1.66% | 2.55% | 3.16% | 3.74% | 7.18% | 8.70% |
| land area within 2 km | 0.141% | 0.293% | 0.823% | 1.52% | 2.42% | 2.60% | 4.02% | 4.58% |
| land area within 5 km | 0.086% | 0.184% | 0.419% | 0.781% | 0.931% | 1.23% | 1.78% | 2.41% |
| any area within 2 km | 0.110% | 0.055% | 0.605% | 0.228% | 2.62% | 2.53% | 2.80% | 3.24% |
| any area within 5 km | 0.004% | 0.065% | 0.076% | 0.206% | 0.122% | 0.403% | 0.342% | 1.38% |
| Swamp area within 5 km | 0.110% | 0.307% | 0.826% | 1.99% | 2.30% | 3.06% | 5.96% | 7.11% |
| Mountain area within 5 km | 0.138% | 0.362% | 1.11% | 2.23% | 2.84% | 3.23% | 6.73% | 8.25% |
| Plains area within 5 km | 0.062% | 0.159% | 0.453% | 0.900% | 1.15% | 1.47% | 2.59% | 3.52% |
| Mistlands area within 5 km | - | - | - | - | - | - | - | - |
| area above 100 m | 0.106% | 0.264% | 0.680% | 1.38% | 1.66% | 2.17% | 4.09% | 4.69% |
| area above 200 m | 0.338% | 0.890% | 2.43% | 5.09% | 6.03% | 8.88% | 15.7% | 18.7% |
| coastline length | 24.4% | 48.1% | 63.8% | 70.6% | 72.1% | 73.2% | 76.6% | 78.1% |
| Meadows largest patch | 0.173% | 0.426% | 2.64% | 5.52% | 9.66% | 9.73% | 14.5% | 17.8% |
| Swamp largest patch | 0.299% | 0.876% | 2.99% | 7.60% | 10.4% | 12.7% | 16.8% | 19.2% |
| Mountain largest patch | 0.265% | 0.762% | 1.94% | 8.20% | 13.7% | 15.5% | 16.0% | 17.5% |
| BlackForest largest patch | 0.222% | 0.894% | 6.13% | 16.3% | 19.1% | 21.3% | 25.3% | 28.2% |
| Plains largest patch | 0.285% | 3.12% | 11.9% | 17.8% | 19.6% | 20.1% | 22.7% | 24.2% |
| AshLands largest patch | 0.026% | 0.092% | 0.368% | 0.582% | 0.283% | 0.475% | 0.832% | 0.968% |
| DeepNorth largest patch | 0.068% | 0.181% | 0.627% | 53.4% | 82.8% | 110.5% | 169.5% | 178.8% |
| Ocean largest patch | 0.046% | 0.076% | 7.93% | 72.0% | 82.2% | 85.8% | 91.2% | 92.1% |
| Mistlands largest patch | 0.108% | 0.306% | 1.31% | 22.2% | 29.1% | 34.2% | 43.0% | 44.1% |

### |relative error| vs G12 - p95

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows area | 0.289% | 0.613% | 1.66% | 3.71% | 4.89% | 5.62% | 10.8% | 12.0% |
| Swamp area | 0.257% | 0.697% | 2.07% | 4.26% | 5.61% | 6.98% | 13.7% | 16.3% |
| Mountain area | 0.199% | 0.566% | 1.73% | 3.40% | 4.35% | 5.72% | 10.7% | 12.9% |
| BlackForest area | 0.135% | 0.296% | 0.881% | 1.92% | 2.07% | 2.71% | 5.30% | 6.16% |
| Plains area | 0.136% | 0.301% | 0.930% | 2.20% | 2.27% | 2.59% | 5.13% | 6.03% |
| AshLands area | 0.024% | 0.097% | 0.368% | 0.644% | 0.197% | 0.252% | 0.832% | 0.625% |
| DeepNorth area | 0.089% | 0.231% | 0.734% | 1.35% | 1.74% | 2.34% | 4.22% | 5.91% |
| Ocean area | 0.078% | 0.158% | 0.513% | 0.996% | 1.05% | 1.38% | 2.85% | 3.33% |
| Mistlands area | 0.112% | 0.224% | 0.665% | 1.48% | 1.48% | 1.89% | 3.65% | 5.07% |
| land area | 0.099% | 0.246% | 0.562% | 1.07% | 1.28% | 1.53% | 2.56% | 2.96% |
| water area | 0.064% | 0.147% | 0.347% | 0.694% | 0.656% | 0.893% | 1.41% | 1.53% |
| largest island area | 31.0% | 44.8% | 50.4% | 83.2% | 105.7% | 132.3% | 198.3% | 218.6% |
| spawn island area | 1,168% | 106,842% | 261,927% | 249,750% | 307,313% | 288,449% | 32,151% | 140,084% |
| land area within 500 m | 1.82% | 5.76% | 10.5% | 25.1% | 31.6% | 24.5% | 100.0% | 63.8% |
| land area within 1 km | 0.601% | 1.59% | 4.30% | 7.48% | 9.38% | 10.8% | 20.5% | 25.6% |
| land area within 2 km | 0.403% | 0.921% | 2.49% | 4.48% | 6.94% | 7.26% | 12.1% | 14.1% |
| land area within 5 km | 0.248% | 0.521% | 1.19% | 2.23% | 2.66% | 3.51% | 5.20% | 7.11% |
| any area within 2 km | 0.110% | 0.055% | 0.605% | 0.228% | 2.62% | 2.53% | 2.80% | 3.24% |
| any area within 5 km | 0.004% | 0.065% | 0.076% | 0.206% | 0.122% | 0.403% | 0.342% | 1.38% |
| Swamp area within 5 km | 0.336% | 0.895% | 2.51% | 5.69% | 6.70% | 8.66% | 17.2% | 20.3% |
| Mountain area within 5 km | 0.401% | 1.12% | 3.26% | 6.81% | 8.09% | 10.7% | 18.9% | 24.8% |
| Plains area within 5 km | 0.186% | 0.470% | 1.30% | 2.72% | 3.29% | 4.32% | 7.94% | 9.94% |
| Mistlands area within 5 km | - | - | - | - | - | - | - | - |
| area above 100 m | 0.308% | 0.778% | 1.91% | 4.11% | 4.95% | 6.26% | 11.6% | 13.9% |
| area above 200 m | 1.03% | 2.73% | 7.25% | 15.6% | 18.6% | 27.1% | 48.3% | 52.8% |
| coastline length | 25.2% | 49.0% | 64.7% | 71.6% | 73.1% | 74.1% | 77.6% | 79.1% |
| Meadows largest patch | 6.68% | 18.6% | 36.2% | 43.1% | 44.9% | 47.1% | 52.3% | 60.3% |
| Swamp largest patch | 2.81% | 19.6% | 30.9% | 41.3% | 41.5% | 43.3% | 51.4% | 66.2% |
| Mountain largest patch | 0.803% | 2.15% | 6.22% | 32.2% | 33.0% | 33.1% | 39.7% | 38.3% |
| BlackForest largest patch | 14.5% | 30.6% | 46.0% | 58.5% | 63.9% | 64.7% | 77.0% | 83.1% |
| Plains largest patch | 23.5% | 38.3% | 44.7% | 55.1% | 60.5% | 65.7% | 85.2% | 92.4% |
| AshLands largest patch | 0.026% | 0.092% | 0.368% | 0.582% | 0.283% | 0.475% | 0.832% | 0.968% |
| DeepNorth largest patch | 0.209% | 0.647% | 21.1% | 188.7% | 263.2% | 308.2% | 429.7% | 439.0% |
| Ocean largest patch | 0.414% | 0.776% | 30.9% | 84.4% | 90.5% | 92.1% | 94.7% | 95.2% |
| Mistlands largest patch | 5.12% | 15.6% | 31.8% | 89.4% | 111.6% | 122.7% | 138.2% | 144.4% |

### |relative error| vs G12 - p99

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows area | 0.385% | 0.824% | 2.18% | 5.04% | 6.23% | 7.45% | 14.3% | 16.2% |
| Swamp area | 0.331% | 0.914% | 2.74% | 5.70% | 7.34% | 9.15% | 18.1% | 20.5% |
| Mountain area | 0.252% | 0.745% | 2.21% | 4.49% | 5.72% | 7.55% | 13.5% | 16.3% |
| BlackForest area | 0.173% | 0.371% | 1.18% | 2.59% | 2.67% | 3.64% | 7.33% | 8.06% |
| Plains area | 0.176% | 0.383% | 1.25% | 2.90% | 2.96% | 3.46% | 6.52% | 8.19% |
| AshLands area | 0.024% | 0.097% | 0.368% | 0.644% | 0.197% | 0.252% | 0.832% | 0.625% |
| DeepNorth area | 0.114% | 0.292% | 0.956% | 1.83% | 2.36% | 3.17% | 5.68% | 7.72% |
| Ocean area | 0.096% | 0.205% | 0.652% | 1.28% | 1.40% | 1.81% | 3.65% | 4.29% |
| Mistlands area | 0.152% | 0.298% | 0.988% | 1.86% | 1.95% | 2.54% | 4.91% | 6.49% |
| land area | 0.131% | 0.312% | 0.754% | 1.42% | 1.72% | 1.99% | 3.30% | 3.96% |
| water area | 0.082% | 0.188% | 0.449% | 0.892% | 0.883% | 1.13% | 1.85% | 2.03% |
| largest island area | 64.1% | 76.1% | 80.2% | 126.2% | 155.0% | 196.4% | 292.2% | 312.9% |
| spawn island area | 2,274,440% | 2,827,180% | 3,076,316% | 2,849,685% | 3,133,596% | 3,144,811% | 2,827,628% | 3,113,884% |
| land area within 500 m | 2.75% | 8.60% | 16.5% | 39.3% | 46.8% | 38.7% | 100.0% | 100.0% |
| land area within 1 km | 0.875% | 2.18% | 5.60% | 10.2% | 12.8% | 14.7% | 28.4% | 34.0% |
| land area within 2 km | 0.549% | 1.25% | 3.27% | 6.15% | 8.90% | 9.57% | 16.2% | 18.3% |
| land area within 5 km | 0.314% | 0.691% | 1.55% | 2.99% | 3.61% | 4.53% | 6.62% | 9.35% |
| any area within 2 km | 0.110% | 0.055% | 0.605% | 0.228% | 2.62% | 2.53% | 2.80% | 3.24% |
| any area within 5 km | 0.004% | 0.065% | 0.076% | 0.206% | 0.122% | 0.403% | 0.342% | 1.38% |
| Swamp area within 5 km | 0.446% | 1.23% | 3.28% | 7.33% | 9.24% | 11.5% | 22.9% | 28.8% |
| Mountain area within 5 km | 0.540% | 1.43% | 4.28% | 8.84% | 11.0% | 14.6% | 25.0% | 34.0% |
| Plains area within 5 km | 0.235% | 0.606% | 1.66% | 3.52% | 4.37% | 5.53% | 10.5% | 12.7% |
| Mistlands area within 5 km | - | - | - | - | - | - | - | - |
| area above 100 m | 0.404% | 1.03% | 2.63% | 5.26% | 6.75% | 8.12% | 16.2% | 18.1% |
| area above 200 m | 1.41% | 3.97% | 9.63% | 21.8% | 25.2% | 37.7% | 66.4% | 71.2% |
| coastline length | 25.5% | 49.3% | 65.1% | 72.0% | 73.4% | 74.5% | 78.0% | 79.5% |
| Meadows largest patch | 33.2% | 40.1% | 52.1% | 67.2% | 62.1% | 79.5% | 80.9% | 94.4% |
| Swamp largest patch | 24.0% | 39.3% | 45.2% | 52.2% | 54.1% | 55.2% | 78.8% | 99.7% |
| Mountain largest patch | 1.08% | 2.72% | 18.2% | 36.1% | 38.2% | 39.6% | 51.4% | 48.5% |
| BlackForest largest patch | 35.8% | 43.9% | 56.9% | 83.7% | 100.3% | 105.1% | 131.3% | 141.0% |
| Plains largest patch | 41.6% | 48.0% | 53.2% | 80.4% | 91.7% | 108.8% | 142.1% | 136.3% |
| AshLands largest patch | 0.026% | 0.092% | 0.368% | 0.582% | 0.283% | 0.475% | 0.832% | 0.968% |
| DeepNorth largest patch | 0.338% | 11.0% | 71.3% | 257.0% | 332.0% | 480.0% | 534.4% | 551.1% |
| Ocean largest patch | 2.12% | 2.36% | 40.6% | 88.1% | 92.0% | 93.8% | 95.3% | 95.9% |
| Mistlands largest patch | 24.2% | 33.9% | 49.0% | 130.6% | 164.0% | 170.8% | 199.6% | 200.9% |

### |relative error| vs G12 - max

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows area | 0.553% | 1.15% | 3.40% | 6.87% | 8.41% | 9.82% | 21.1% | 24.0% |
| Swamp area | 0.438% | 1.29% | 4.23% | 8.81% | 10.6% | 12.7% | 26.7% | 33.5% |
| Mountain area | 0.389% | 0.982% | 3.72% | 7.87% | 10.5% | 9.78% | 17.2% | 25.6% |
| BlackForest area | 0.256% | 0.525% | 1.55% | 3.75% | 3.49% | 4.84% | 10.1% | 11.3% |
| Plains area | 0.269% | 0.503% | 1.92% | 4.48% | 4.40% | 4.60% | 8.68% | 10.6% |
| AshLands area | 0.024% | 0.097% | 0.368% | 0.644% | 0.197% | 0.252% | 0.832% | 0.625% |
| DeepNorth area | 0.150% | 0.403% | 1.21% | 2.60% | 3.37% | 4.14% | 8.04% | 10.4% |
| Ocean area | 0.133% | 0.296% | 0.969% | 1.71% | 1.98% | 2.66% | 4.92% | 5.92% |
| Mistlands area | 0.214% | 0.513% | 1.40% | 2.51% | 2.79% | 3.69% | 6.91% | 8.88% |
| land area | 0.183% | 0.409% | 1.17% | 1.96% | 2.43% | 2.90% | 6.18% | 5.75% |
| water area | 0.113% | 0.231% | 0.601% | 1.16% | 1.25% | 1.53% | 3.77% | 2.92% |
| largest island area | 144.3% | 141.0% | 152.3% | 236.0% | 249.4% | 500.9% | 470.6% | 551.6% |
| spawn island area | 4,966,700% | 6,428,700% | 6,521,500% | 6,523,414% | 6,399,900% | 7,177,634% | 7,850,547% | 9,727,900% |
| land area within 500 m | 166.7% | 100.0% | 2,033% | 100.0% | 102.9% | 100.0% | 100.0% | 167.4% |
| land area within 1 km | 1.30% | 3.06% | 9.24% | 17.3% | 22.5% | 24.0% | 50.4% | 63.1% |
| land area within 2 km | 0.789% | 1.87% | 6.09% | 10.4% | 12.1% | 14.0% | 31.5% | 29.4% |
| land area within 5 km | 0.518% | 0.922% | 2.26% | 4.61% | 4.75% | 7.01% | 10.0% | 15.3% |
| any area within 2 km | 0.110% | 0.055% | 0.605% | 0.228% | 2.62% | 2.53% | 2.80% | 3.24% |
| any area within 5 km | 0.004% | 0.065% | 0.076% | 0.206% | 0.122% | 0.403% | 0.342% | 1.38% |
| Swamp area within 5 km | 0.630% | 1.69% | 5.47% | 10.6% | 13.0% | 17.8% | 32.2% | 44.8% |
| Mountain area within 5 km | 0.783% | 2.42% | 6.81% | 12.5% | 15.3% | 18.1% | 39.4% | 49.8% |
| Plains area within 5 km | 0.341% | 0.812% | 2.55% | 4.87% | 6.21% | 7.92% | 16.7% | 17.0% |
| Mistlands area within 5 km | - | - | - | - | - | - | - | - |
| area above 100 m | 0.633% | 1.43% | 3.92% | 7.53% | 10.1% | 11.0% | 26.7% | 25.3% |
| area above 200 m | 1.88% | 6.00% | 15.1% | 36.1% | 36.1% | 55.4% | 93.4% | 99.4% |
| coastline length | 26.1% | 49.9% | 65.8% | 72.6% | 74.5% | 75.2% | 78.6% | 80.1% |
| Meadows largest patch | 90.3% | 93.1% | 153.8% | 138.4% | 132.8% | 227.2% | 232.0% | 216.7% |
| Swamp largest patch | 45.0% | 57.7% | 70.8% | 75.5% | 109.5% | 101.5% | 142.3% | 194.8% |
| Mountain largest patch | 15.6% | 14.7% | 25.3% | 42.1% | 43.4% | 46.5% | 73.2% | 77.5% |
| BlackForest largest patch | 79.3% | 62.7% | 99.0% | 183.2% | 213.5% | 181.5% | 285.2% | 265.8% |
| Plains largest patch | 79.4% | 61.1% | 80.4% | 170.0% | 240.8% | 222.4% | 236.2% | 443.3% |
| AshLands largest patch | 0.026% | 0.092% | 0.368% | 0.582% | 0.283% | 0.475% | 0.832% | 0.968% |
| DeepNorth largest patch | 41.4% | 47.3% | 100.1% | 345.5% | 589.7% | 615.9% | 670.6% | 704.8% |
| Ocean largest patch | 6.73% | 7.33% | 71.6% | 90.5% | 93.6% | 94.8% | 96.1% | 97.3% |
| Mistlands largest patch | 46.8% | 77.5% | 94.4% | 225.9% | 310.8% | 262.3% | 311.3% | 334.6% |

### signed median relative error (negative = coarse UNDER-reads)

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows area | +0.072% | +0.023% | +0.059% | +0.112% | -0.978% | -0.141% | +1.35% | +0.514% |
| Swamp area | +0.020% | -0.064% | -0.341% | -0.073% | +0.625% | -0.759% | -2.51% | +1.03% |
| Mountain area | 0 | -0.015% | +0.068% | -0.083% | +0.111% | -0.126% | +0.023% | +0.018% |
| BlackForest area | -0.004% | +0.003% | +0.153% | -0.147% | +0.082% | -0.467% | +0.831% | -1.00% |
| Plains area | -0.012% | +0.023% | -0.048% | +0.614% | +0.110% | +0.154% | -0.318% | -0.077% |
| AshLands area | -0.024% | +0.097% | -0.368% | +0.644% | -0.197% | -0.252% | +0.832% | -0.625% |
| DeepNorth area | +0.009% | +0.037% | -0.160% | -0.009% | +0.005% | +0.445% | -0.400% | -1.33% |
| Ocean area | -0.034% | +0.039% | -0.134% | +0.348% | +0.116% | -0.130% | +0.624% | +0.094% |
| Mistlands area | -0.010% | -0.040% | +0.068% | -0.291% | -0.065% | +0.096% | +0.145% | +1.35% |
| land area | +0.000% | -0.020% | -0.022% | +0.093% | +0.021% | +0.205% | +0.292% | -0.102% |
| water area | -0.020% | +0.042% | -0.098% | +0.234% | -0.007% | -0.238% | +0.365% | -0.008% |
| largest island area | +0.057% | +0.400% | +2.21% | +11.4% | +22.3% | +33.0% | +68.0% | +79.0% |
| spawn island area | +10.5% | +31.2% | +40.8% | +43.4% | +48.6% | +45.8% | +52.2% | +55.1% |
| land area within 500 m | +0.670% | -2.52% | +3.38% | +9.72% | +12.9% | -2.48% | -44.0% | -23.5% |
| land area within 1 km | +0.041% | +0.417% | -1.42% | +1.45% | +2.16% | +2.31% | +5.49% | +5.29% |
| land area within 2 km | -0.085% | -0.056% | +0.528% | +0.201% | -2.00% | -2.07% | +2.24% | +2.05% |
| land area within 5 km | +0.020% | +0.014% | -0.107% | +0.218% | -0.196% | +0.839% | +0.195% | +1.46% |
| any area within 2 km | -0.110% | -0.055% | +0.605% | +0.228% | -2.62% | -2.53% | +2.80% | +3.24% |
| any area within 5 km | +0.004% | +0.065% | -0.076% | -0.206% | -0.122% | +0.403% | +0.342% | +1.38% |
| Swamp area within 5 km | +0.029% | +0.081% | -0.251% | -0.532% | +0.613% | +0.550% | -2.01% | +1.42% |
| Mountain area within 5 km | +0.014% | +0.073% | -0.092% | -0.231% | +0.043% | +0.258% | +0.040% | +0.722% |
| Plains area within 5 km | -0.028% | +0.088% | -0.010% | +0.318% | -0.211% | +1.05% | +1.08% | +2.27% |
| Mistlands area within 5 km | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| area above 100 m | 0 | +0.010% | +0.044% | +0.073% | -0.058% | -0.008% | +0.140% | -0.118% |
| area above 200 m | +0.019% | +0.021% | +0.015% | +0.073% | -0.700% | -0.115% | -0.787% | -0.474% |
| coastline length | -24.4% | -48.1% | -63.8% | -70.6% | -72.1% | -73.2% | -76.6% | -78.1% |
| Meadows largest patch | +0.097% | -0.308% | -2.25% | -0.891% | -6.99% | -3.88% | -1.10% | -7.10% |
| Swamp largest patch | -0.023% | -0.292% | -1.68% | -5.02% | -6.61% | -9.01% | -4.20% | +4.63% |
| Mountain largest patch | -0.022% | -0.029% | +0.281% | -2.24% | -11.1% | -13.0% | -9.85% | -7.98% |
| BlackForest largest patch | -0.195% | -0.810% | -5.23% | -6.35% | -3.12% | -6.23% | -5.67% | -6.86% |
| Plains largest patch | -0.239% | -2.96% | -11.5% | -6.75% | -4.35% | -0.913% | +6.39% | +10.0% |
| AshLands largest patch | -0.026% | +0.092% | -0.368% | +0.582% | -0.283% | -0.475% | +0.832% | -0.968% |
| DeepNorth largest patch | +0.012% | +0.012% | -0.174% | +53.4% | +82.8% | +110.5% | +169.5% | +178.8% |
| Ocean largest patch | -0.042% | +0.006% | -7.91% | -72.0% | -82.2% | -85.8% | -91.2% | -92.1% |
| Mistlands largest patch | -0.025% | -0.163% | -0.693% | +16.5% | +27.1% | +33.7% | +42.7% | +44.0% |

### shares - absolute error in share units (1.0 = whole world)


med:

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows share | 3.44e-05 | 6.51e-05 | 0.000184 | 0.000409 | 0.000534 | 0.000576 | 0.00108 | 0.0013 |
| Swamp share | 2.45e-05 | 6.3e-05 | 0.000184 | 0.000375 | 0.000514 | 0.000618 | 0.00127 | 0.00145 |
| Mountain share | 2.15e-05 | 6.17e-05 | 0.00021 | 0.000369 | 0.000482 | 0.000623 | 0.00118 | 0.00148 |
| BlackForest share | 6.52e-05 | 0.000138 | 0.000428 | 0.000913 | 0.000975 | 0.00122 | 0.00235 | 0.00287 |
| Plains share | 5.86e-05 | 0.000129 | 0.00042 | 0.000891 | 0.000992 | 0.00113 | 0.00226 | 0.00273 |
| AshLands share | 1.4e-05 | 9.51e-05 | 0.000368 | 0.00057 | 0.000248 | 0.000204 | 0.000609 | 0.000727 |
| DeepNorth share | 2.35e-05 | 5.17e-05 | 0.000159 | 0.000327 | 0.000423 | 0.000588 | 0.00104 | 0.00138 |
| Ocean share | 7.03e-05 | 0.00014 | 0.000451 | 0.000819 | 0.00103 | 0.00137 | 0.00257 | 0.00343 |
| Mistlands share | 5.96e-05 | 0.00014 | 0.000384 | 0.001 | 0.000869 | 0.00116 | 0.00216 | 0.00317 |
| land share | 0.000115 | 0.0003 | 0.000676 | 0.00119 | 0.00151 | 0.00185 | 0.00292 | 0.00334 |

p95:

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows share | 9.26e-05 | 0.00019 | 0.000526 | 0.00115 | 0.00151 | 0.00173 | 0.00343 | 0.00376 |
| Swamp share | 6.87e-05 | 0.000186 | 0.000534 | 0.00114 | 0.00147 | 0.00181 | 0.00361 | 0.00425 |
| Mountain share | 6.34e-05 | 0.000179 | 0.000559 | 0.00109 | 0.0014 | 0.00176 | 0.00338 | 0.00409 |
| BlackForest share | 0.000182 | 0.000392 | 0.00124 | 0.00265 | 0.00274 | 0.00351 | 0.00679 | 0.00816 |
| Plains share | 0.000169 | 0.000379 | 0.00117 | 0.0026 | 0.00291 | 0.0034 | 0.00652 | 0.00765 |
| AshLands share | 1.4e-05 | 9.51e-05 | 0.000368 | 0.00057 | 0.000248 | 0.000204 | 0.000609 | 0.000727 |
| DeepNorth share | 6.63e-05 | 0.000153 | 0.000469 | 0.000977 | 0.00118 | 0.00167 | 0.00299 | 0.00401 |
| Ocean share | 0.000183 | 0.000404 | 0.00131 | 0.00236 | 0.00298 | 0.00385 | 0.00748 | 0.00944 |
| Mistlands share | 0.000187 | 0.000415 | 0.00124 | 0.00283 | 0.00251 | 0.00327 | 0.00632 | 0.00883 |
| land share | 0.00035 | 0.00087 | 0.00196 | 0.00368 | 0.00433 | 0.00546 | 0.00837 | 0.0101 |

mx:

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows share | 0.000182 | 0.000352 | 0.000976 | 0.00216 | 0.00256 | 0.0032 | 0.00714 | 0.00677 |
| Swamp share | 0.000126 | 0.000323 | 0.00109 | 0.00211 | 0.00261 | 0.00314 | 0.00693 | 0.00813 |
| Mountain share | 0.00012 | 0.00032 | 0.00108 | 0.00244 | 0.00259 | 0.00306 | 0.0059 | 0.00783 |
| BlackForest share | 0.000348 | 0.000733 | 0.00211 | 0.00496 | 0.0049 | 0.00613 | 0.0127 | 0.0149 |
| Plains share | 0.000336 | 0.000632 | 0.00223 | 0.00479 | 0.00493 | 0.00614 | 0.0118 | 0.0137 |
| AshLands share | 1.4e-05 | 9.51e-05 | 0.000368 | 0.00057 | 0.000248 | 0.000204 | 0.000609 | 0.000727 |
| DeepNorth share | 0.000113 | 0.000291 | 0.000753 | 0.00171 | 0.00207 | 0.0028 | 0.0054 | 0.00691 |
| Ocean share | 0.000337 | 0.000766 | 0.00247 | 0.0045 | 0.00571 | 0.00715 | 0.0136 | 0.0167 |
| Mistlands share | 0.000381 | 0.000888 | 0.00257 | 0.00474 | 0.00498 | 0.0063 | 0.0121 | 0.015 |
| land share | 0.000645 | 0.00141 | 0.0038 | 0.00647 | 0.00826 | 0.00974 | 0.0224 | 0.0195 |

### distances - absolute error, metres


med:

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows nearest dist | 8.49 m | 25.5 m | 59.4 m | 108 m | 127 m | 146 m | 224 m | 263 m |
| Swamp nearest dist | 0.558 m | 3.51 m | 5.66 m | 26 m | 18.3 m | 10.8 m | 139 m | 121 m |
| Mountain nearest dist | 3.93 m | 11.6 m | 32.2 m | 77.2 m | 57.3 m | 128 m | 196 m | 354 m |
| BlackForest nearest dist | 2.19 m | 9.75 m | 21.8 m | 71.7 m | 51.4 m | 49.4 m | 188 m | 98.8 m |
| Plains nearest dist | 0.906 m | 6.98 m | 13.9 m | 7.63 m | 63.3 m | 53.3 m | 114 m | 35.2 m |
| AshLands nearest dist | 0.742 m | 3.2 m | 22.9 m | 52 m | 64.3 m | 76.3 m | 132 m | 151 m |
| DeepNorth nearest dist | 0.742 m | 3.2 m | 22.9 m | 52 m | 64.3 m | 76.3 m | 132 m | 151 m |
| Ocean nearest dist | 5.56 m | 16.7 m | 42.9 m | 86.8 m | 108 m | 125 m | 210 m | 249 m |
| Mistlands nearest dist | 2.41 m | 1.12 m | 11.6 m | 25.1 m | 74.7 m | 20 m | 28.5 m | 54.7 m |
| nearest land dist | 8.49 m | 25.5 m | 59.4 m | 108 m | 127 m | 146 m | 224 m | 263 m |

p95:

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows nearest dist | 8.49 m | 25.5 m | 59.4 m | 108 m | 127 m | 146 m | 224 m | 263 m |
| Swamp nearest dist | 2.29 m | 6.95 m | 41.6 m | 91 m | 71.8 m | 104 m | 285 m | 320 m |
| Mountain nearest dist | 15.1 m | 38.1 m | 128 m | 454 m | 595 m | 690 m | 1.13e+03 m | 1.43e+03 m |
| BlackForest nearest dist | 2.19 m | 9.75 m | 21.8 m | 71.7 m | 51.4 m | 49.4 m | 328 m | 98.8 m |
| Plains nearest dist | 1.1 m | 10.1 m | 20.2 m | 7.63 m | 75.7 m | 53.3 m | 114 m | 134 m |
| AshLands nearest dist | 0.742 m | 3.2 m | 22.9 m | 52 m | 64.3 m | 76.3 m | 132 m | 151 m |
| DeepNorth nearest dist | 10.3 m | 16.2 m | 40.5 m | 68.9 m | 78.2 m | 82.2 m | 155 m | 260 m |
| Ocean nearest dist | 16.5 m | 39.3 m | 102 m | 290 m | 396 m | 485 m | 719 m | 777 m |
| Mistlands nearest dist | 2.7 m | 5.41 m | 13.1 m | 34.2 m | 74.7 m | 28 m | 46.7 m | 54.7 m |
| nearest land dist | 18.3 m | 43.8 m | 85.5 m | 145 m | 174 m | 190 m | 224 m | 263 m |

p99:

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows nearest dist | 10.6 m | 30.2 m | 66.5 m | 115 m | 127 m | 146 m | 224 m | 263 m |
| Swamp nearest dist | 4.3 m | 10.4 m | 70.2 m | 159 m | 162 m | 214 m | 466 m | 503 m |
| Mountain nearest dist | 18.7 m | 161 m | 649 m | 927 m | 1.05e+03 m | 1.18e+03 m | 1.56e+03 m | 2.02e+03 m |
| BlackForest nearest dist | 4.57 m | 18.6 m | 38.9 m | 116 m | 170 m | 129 m | 328 m | 306 m |
| Plains nearest dist | 1.65 m | 12.5 m | 20.2 m | 25.9 m | 75.7 m | 69.4 m | 150 m | 134 m |
| AshLands nearest dist | 0.742 m | 3.2 m | 22.9 m | 52 m | 64.3 m | 76.3 m | 132 m | 151 m |
| DeepNorth nearest dist | 18.5 m | 33.6 m | 64.2 m | 81.8 m | 94.7 m | 109 m | 212 m | 349 m |
| Ocean nearest dist | 18.8 m | 163 m | 458 m | 594 m | 660 m | 764 m | 948 m | 971 m |
| Mistlands nearest dist | 2.9 m | 7.75 m | 19.3 m | 38.8 m | 87.1 m | 52.2 m | 64.8 m | 79.4 m |
| nearest land dist | 35.2 m | 69.1 m | 128 m | 206 m | 244 m | 267 m | 391 m | 414 m |

mx:

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows nearest dist | 19 m | 45.3 m | 97.5 m | 181 m | 201 m | 220 m | 308 m | 396 m |
| Swamp nearest dist | 45.5 m | 94.5 m | 274 m | 334 m | 324 m | 499 m | 793 m | 937 m |
| Mountain nearest dist | 927 m | 926 m | 1.2e+03 m | 1.37e+03 m | 1.53e+03 m | 2.26e+03 m | 3.09e+03 m | 3.01e+03 m |
| BlackForest nearest dist | 18.3 m | 43.3 m | 118 m | 234 m | 220 m | 265 m | 328 m | 471 m |
| Plains nearest dist | 7.22 m | 17.3 m | 35.7 m | 80.5 m | 113 m | 101 m | 254 m | 134 m |
| AshLands nearest dist | 0.742 m | 3.2 m | 22.9 m | 52 m | 64.3 m | 76.3 m | 132 m | 151 m |
| DeepNorth nearest dist | 61.6 m | 65 m | 211 m | 169 m | 188 m | 195 m | 323 m | 367 m |
| Ocean nearest dist | 569 m | 582 m | 855 m | 927 m | 1.01e+03 m | 982 m | 1.24e+03 m | 1.28e+03 m |
| Mistlands nearest dist | 4.85 m | 16.7 m | 26.7 m | 56.9 m | 112 m | 76.3 m | 136 m | 128 m |
| nearest land dist | 98.1 m | 185 m | 297 m | 303 m | 295 m | 337 m | 510 m | 599 m |

### heights - absolute error, metres (signed min/max)

| metric | grid | med|e| | p95|e| | max|e| | min signed | max signed |
|---|---|---|---|---|---|---|
| highest peak | G24 | 2.31 | 6.92 | 23.9 | -23.9 | 4.51 |
| highest peak | G48 | 9.25 | 24.5 | 40.4 | -40.4 | 4.86 |
| highest peak | G96 | 27.7 | 65.3 | 97.3 | -97.3 | 4.45 |
| highest peak | G164* | 54.8 | 93.9 | 144 | -144 | 1.29 |
| highest peak | G192 | 62.1 | 103 | 148 | -148 | 2.13 |
| highest peak | G219* | 71.3 | 121 | 158 | -158 | 1.82 |
| highest peak | G328* | 99.7 | 174 | 217 | -217 | 2.09 |
| highest peak | G384 | 116 | 193 | 239 | -239 | 1.02 |
| deepest point | G24 | 3.55 | 3.55 | 3.55 | 3.55 | 3.55 |
| deepest point | G48 | 4.04 | 4.04 | 4.04 | 4.04 | 4.04 |
| deepest point | G96 | 69.3 | 69.3 | 69.3 | 69.2 | 69.3 |
| deepest point | G164* | 162 | 162 | 162 | 161 | 162 |
| deepest point | G192 | 13.9 | 13.9 | 13.9 | 13.9 | 13.9 |
| deepest point | G219* | 337 | 344 | 348 | 322 | 348 |
| deepest point | G328* | 277 | 277 | 278 | 277 | 278 |
| deepest point | G384 | 299 | 299 | 300 | 299 | 300 |

### island counts - difference from G12

| metric | grid | exact match | median signed | p95|d| | max|d| | min signed | max signed |
|---|---|---|---|---|---|---|---|
| island count (>=1 cell) | G24 | 0.0% | -4.98e+03 | 5.46e+03 | 6.05e+03 | -6.05e+03 | -3.91e+03 |
| island count (>=1 cell) | G48 | 0.0% | -8.17e+03 | 8.82e+03 | 9.51e+03 | -9.51e+03 | -6.69e+03 |
| island count (>=1 cell) | G96 | 0.0% | -9.37e+03 | 1.01e+04 | 1.09e+04 | -1.09e+04 | -7.83e+03 |
| island count (>=1 cell) | G164* | 0.0% | -9.64e+03 | 1.04e+04 | 1.12e+04 | -1.12e+04 | -8.08e+03 |
| island count (>=1 cell) | G192 | 0.0% | -9.66e+03 | 1.04e+04 | 1.13e+04 | -1.13e+04 | -8.11e+03 |
| island count (>=1 cell) | G219* | 0.0% | -9.68e+03 | 1.04e+04 | 1.13e+04 | -1.13e+04 | -8.12e+03 |
| island count (>=1 cell) | G328* | 0.0% | -9.73e+03 | 1.05e+04 | 1.13e+04 | -1.13e+04 | -8.16e+03 |
| island count (>=1 cell) | G384 | 0.0% | -9.74e+03 | 1.05e+04 | 1.13e+04 | -1.13e+04 | -8.18e+03 |
| island count (>=0.1 km2) | G24 | 0.2% | -11 | 18 | 29 | -29 | 6 |
| island count (>=0.1 km2) | G48 | 0.0% | -18 | 27 | 38 | -38 | 3 |
| island count (>=0.1 km2) | G96 | 0.1% | -17 | 28 | 38 | -38 | 1 |
| island count (>=0.1 km2) | G164* | 0.1% | -16 | 28 | 40 | -40 | 4 |
| island count (>=0.1 km2) | G192 | 0.2% | -16 | 29 | 44 | -44 | 8 |
| island count (>=0.1 km2) | G219* | 0.0% | -25 | 38 | 55 | -55 | 1 |
| island count (>=0.1 km2) | G328* | 3.5% | 5 | 22 | 33 | -28 | 33 |
| island count (>=0.1 km2) | G384 | 2.2% | -9 | 25 | 43 | -43 | 22 |
| island count (>=1 km2) | G24 | 20.0% | 1 | 4 | 7 | -5 | 7 |
| island count (>=1 km2) | G48 | 12.6% | 2 | 6 | 13 | -7 | 13 |
| island count (>=1 km2) | G96 | 10.0% | 3 | 7 | 12 | -7 | 12 |
| island count (>=1 km2) | G164* | 11.5% | 2 | 7 | 14 | -9 | 14 |
| island count (>=1 km2) | G192 | 11.6% | 0 | 7 | 12 | -10 | 12 |
| island count (>=1 km2) | G219* | 9.4% | 1 | 7 | 13 | -11 | 13 |
| island count (>=1 km2) | G328* | 5.9% | -4 | 10 | 17 | -17 | 11 |
| island count (>=1 km2) | G384 | 7.1% | -2 | 10 | 18 | -18 | 12 |
| island count (>=10 km2) | G24 | 100.0% | 0 | 0 | 0 | 0 | 0 |
| island count (>=10 km2) | G48 | 100.0% | 0 | 0 | 0 | 0 | 0 |
| island count (>=10 km2) | G96 | 99.9% | 0 | 0 | 1 | -1 | 1 |
| island count (>=10 km2) | G164* | 98.8% | 0 | 0 | 1 | -1 | 1 |
| island count (>=10 km2) | G192 | 94.7% | 0 | 1 | 2 | 0 | 2 |
| island count (>=10 km2) | G219* | 90.6% | 0 | 1 | 2 | 0 | 2 |
| island count (>=10 km2) | G328* | 67.7% | 0 | 1 | 3 | -1 | 3 |
| island count (>=10 km2) | G384 | 60.3% | 0 | 2 | 3 | -1 | 3 |

### biome disappearance: exact area > 0 but coarse area = 0

| biome | seeds with area>0 at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| Meadows | 2560 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Swamp | 2560 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Mountain | 2560 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| BlackForest | 2560 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Plains | 2560 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| AshLands | 2560 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| DeepNorth | 2560 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Ocean | 2560 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Mistlands | 2560 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

(also: nearest-distance became inf on a coarse grid while finite at G12)

| biome | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Swamp | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Mountain | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| BlackForest | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Plains | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| AshLands | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| DeepNorth | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Ocean | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Mistlands | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

### false-negative rate at realistic thresholds (">= T", T = quantile of the G12 distribution)

FN = coarse says no, G12 says yes, as a fraction of the seeds that truly qualify.


**Meadows area**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 9,743,069 | 0.0% | 0.2% | 0.7% | 1.9% | 5.0% | 4.5% | 5.6% | 9.6% |
| q25 | 10,233,648 | 0.3% | 0.7% | 2.0% | 5.6% | 10.3% | 7.0% | 10.6% | 14.1% |
| q50 | 10,754,496 | 0.3% | 1.2% | 4.3% | 7.8% | 15.8% | 11.2% | 10.4% | 17.4% |
| q75 | 11,309,652 | 0.8% | 1.2% | 6.2% | 12.7% | 19.8% | 17.3% | 19.4% | 24.2% |
| q90 | 11,888,280 | 0.4% | 1.2% | 5.1% | 10.5% | 15.2% | 15.2% | 12.5% | 20.7% |

**Swamp area**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 8,238,614 | 0.1% | 0.5% | 1.2% | 2.7% | 2.8% | 6.2% | 16.8% | 9.8% |
| q25 | 8,666,352 | 0.4% | 1.1% | 4.4% | 5.9% | 6.2% | 12.3% | 26.4% | 15.0% |
| q50 | 9,108,216 | 0.4% | 1.4% | 4.2% | 8.4% | 8.7% | 16.6% | 29.1% | 20.0% |
| q75 | 9,579,636 | 1.2% | 3.6% | 10.5% | 12.5% | 10.5% | 26.4% | 37.5% | 26.2% |
| q90 | 9,998,294 | 0.8% | 2.3% | 9.0% | 18.0% | 12.5% | 28.1% | 37.1% | 27.3% |

**Mountain area**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 10,101,715 | 0.1% | 0.6% | 1.1% | 2.6% | 3.2% | 4.9% | 9.4% | 12.7% |
| q25 | 10,579,644 | 0.5% | 0.7% | 1.9% | 4.6% | 5.0% | 9.4% | 16.1% | 18.8% |
| q50 | 11,045,232 | 0.5% | 0.7% | 4.1% | 8.2% | 10.0% | 13.0% | 17.8% | 21.9% |
| q75 | 11,574,432 | 0.5% | 1.9% | 4.7% | 12.7% | 10.2% | 14.1% | 26.7% | 25.6% |
| q90 | 12,200,126 | 1.6% | 2.7% | 8.2% | 16.0% | 18.4% | 18.0% | 26.2% | 33.2% |

**Plains area**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 41,872,680 | 0.0% | 0.1% | 0.8% | 0.6% | 1.5% | 2.0% | 6.0% | 6.5% |
| q25 | 43,216,308 | 0.1% | 0.6% | 1.7% | 1.2% | 3.9% | 4.9% | 12.4% | 13.7% |
| q50 | 44,605,584 | 0.2% | 0.8% | 3.6% | 2.5% | 7.0% | 7.1% | 18.4% | 20.2% |
| q75 | 46,004,004 | 1.9% | 1.6% | 6.2% | 3.8% | 9.7% | 13.0% | 24.4% | 26.4% |
| q90 | 47,211,322 | 1.2% | 1.2% | 5.1% | 2.3% | 13.7% | 12.1% | 23.0% | 34.8% |

**Mistlands area**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 57,234,384 | 0.3% | 0.5% | 0.7% | 3.6% | 2.3% | 3.0% | 6.1% | 4.0% |
| q25 | 58,365,684 | 0.2% | 0.8% | 1.4% | 4.8% | 4.9% | 5.0% | 11.4% | 5.5% |
| q50 | 59,658,840 | 0.5% | 2.3% | 2.2% | 11.6% | 8.4% | 7.8% | 17.5% | 9.3% |
| q75 | 60,864,480 | 0.9% | 2.5% | 3.3% | 16.7% | 14.1% | 13.3% | 23.9% | 9.7% |
| q90 | 62,031,485 | 0.4% | 3.1% | 2.3% | 20.7% | 10.2% | 13.3% | 22.7% | 13.3% |

**land share**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 0.331 | 0.1% | 0.8% | 1.3% | 3.4% | 4.0% | 2.9% | 8.0% | 10.0% |
| q25 | 0.335 | 0.3% | 1.7% | 1.7% | 5.3% | 5.7% | 3.8% | 11.6% | 14.0% |
| q50 | 0.34 | 0.2% | 1.6% | 3.2% | 7.2% | 8.3% | 5.9% | 15.5% | 18.8% |
| q75 | 0.346 | 0.5% | 4.7% | 5.8% | 15.8% | 13.3% | 9.7% | 22.5% | 28.1% |
| q90 | 0.35 | 0.4% | 5.5% | 5.9% | 16.0% | 16.8% | 11.3% | 28.1% | 28.5% |

**largest island area**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 3,999,614 | 0.8% | 0.8% | 0.6% | 0.7% | 0.3% | 0.0% | 0.0% | 0.0% |
| q25 | 4,507,884 | 2.1% | 2.4% | 1.9% | 2.3% | 1.5% | 0.6% | 0.2% | 0.1% |
| q50 | 5,243,040 | 4.6% | 6.7% | 8.4% | 7.7% | 5.5% | 3.8% | 0.7% | 0.8% |
| q75 | 6,000,984 | 4.8% | 8.8% | 14.5% | 17.7% | 13.1% | 12.5% | 3.8% | 3.9% |
| q90 | 6,595,330 | 7.0% | 14.1% | 21.9% | 28.9% | 22.7% | 22.7% | 10.2% | 7.8% |

**spawn island area**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 2.53e+05 | 0.8% | 0.7% | 0.1% | 0.4% | 0.3% | 0.3% | 3.5% | 2.5% |
| q25 | 9.53e+05 | 0.9% | 0.6% | 0.7% | 0.8% | 0.8% | 1.0% | 4.4% | 3.9% |
| q50 | 2,175,192 | 1.0% | 1.6% | 1.7% | 2.7% | 3.8% | 3.8% | 9.2% | 8.7% |
| q75 | 3,382,380 | 2.2% | 3.0% | 4.2% | 6.2% | 7.8% | 8.8% | 14.8% | 14.8% |
| q90 | 4,655,462 | 2.7% | 5.5% | 6.2% | 7.4% | 9.8% | 10.2% | 14.1% | 16.0% |

**island count (>=1 km2)**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 33 | 1.5% | 1.1% | 0.6% | 3.3% | 7.9% | 7.4% | 40.6% | 30.6% |
| q25 | 34 | 2.0% | 2.0% | 1.4% | 5.3% | 11.4% | 10.0% | 48.7% | 38.0% |
| q50 | 37 | 6.9% | 5.8% | 4.4% | 12.8% | 23.7% | 22.1% | 74.0% | 61.4% |
| q75 | 39 | 10.7% | 10.5% | 7.4% | 21.8% | 35.8% | 37.4% | 85.3% | 76.2% |
| q90 | 41 | 17.6% | 16.5% | 11.5% | 32.1% | 46.2% | 47.8% | 94.0% | 85.7% |

**highest peak**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 365 | 0.6% | 3.9% | 19.4% | 50.8% | 57.0% | 66.6% | 83.3% | 88.8% |
| q25 | 405 | 5.9% | 24.9% | 54.2% | 71.9% | 80.8% | 84.5% | 92.9% | 94.9% |
| q50 | 421 | 14.5% | 37.2% | 46.1% | 74.1% | 82.3% | 86.5% | 93.4% | 95.9% |
| q75 | 457 | 14.7% | 67.0% | 91.7% | 95.9% | 98.4% | 98.4% | 99.4% | 99.2% |
| q90 | 464 | 43.0% | 80.9% | 94.5% | 96.9% | 99.2% | 99.2% | 100.0% | 99.6% |

**land area within 2 km**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 3,928,219 | 0.2% | 0.2% | 0.1% | 1.0% | 3.2% | 3.3% | 1.7% | 2.8% |
| q25 | 4,511,844 | 0.4% | 0.5% | 0.3% | 1.7% | 4.9% | 5.7% | 2.3% | 3.5% |
| q50 | 5,250,096 | 0.6% | 0.6% | 0.6% | 3.8% | 10.5% | 11.2% | 5.1% | 7.1% |
| q75 | 5,837,148 | 0.8% | 1.4% | 2.7% | 4.1% | 20.2% | 18.0% | 9.8% | 9.7% |
| q90 | 6,325,906 | 1.2% | 1.2% | 1.6% | 5.1% | 23.4% | 23.0% | 9.4% | 7.8% |

**Swamp area within 5 km**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 4,943,491 | 0.0% | 0.0% | 0.9% | 1.8% | 1.6% | 2.0% | 9.8% | 8.7% |
| q25 | 5,385,636 | 0.1% | 0.7% | 2.9% | 7.1% | 5.6% | 6.2% | 22.7% | 15.8% |
| q50 | 5,786,640 | 0.5% | 1.9% | 4.9% | 9.6% | 7.3% | 11.6% | 28.0% | 23.1% |
| q75 | 6,153,048 | 0.6% | 1.7% | 6.1% | 14.2% | 11.6% | 17.7% | 35.2% | 23.3% |
| q90 | 6,533,525 | 1.2% | 2.3% | 10.9% | 14.8% | 17.6% | 19.1% | 29.7% | 25.8% |

**Swamp nearest dist**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 2e+03 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| q25 | 2e+03 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| q50 | 2e+03 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| q75 | 2e+03 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| q90 | 2e+03 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |

**Mountain nearest dist**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 623 | 0.2% | 0.1% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| q25 | 631 | 0.3% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| q50 | 654 | 0.2% | 1.1% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| q75 | 700 | 0.3% | 1.7% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| q90 | 809 | 1.2% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |

**Mistlands nearest dist**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 5.9e+03 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| q25 | 5.9e+03 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| q50 | 5.9e+03 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| q75 | 5.9e+03 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| q90 | 5.9e+03 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |

**coastline length**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 2,625,271 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q25 | 2,666,412 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q50 | 2,712,816 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q75 | 2,756,358 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q90 | 2,796,511 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |

### false-negative rate for "<= T" (keep it small / far away) goals


**Swamp nearest dist <= T**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 2e+03 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q25 | 2e+03 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q50 | 2e+03 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q75 | 2e+03 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q90 | 2e+03 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |

**Mountain nearest dist <= T**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 623 | 29.0% | 44.7% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q25 | 631 | 26.3% | 76.9% | 56.2% | 75.9% | 100.0% | 100.0% | 100.0% | 100.0% |
| q50 | 654 | 7.3% | 20.7% | 57.0% | 87.9% | 100.0% | 68.1% | 100.0% | 100.0% |
| q75 | 700 | 1.7% | 5.2% | 31.8% | 91.9% | 32.4% | 78.7% | 64.7% | 100.0% |
| q90 | 809 | 0.6% | 2.0% | 8.5% | 16.1% | 26.2% | 25.3% | 70.6% | 100.0% |

**Mistlands nearest dist <= T**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 5.9e+03 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q25 | 5.9e+03 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q50 | 5.9e+03 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q75 | 5.9e+03 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q90 | 5.9e+03 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |

**nearest land dist <= T**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 8.49 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q25 | 8.49 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q50 | 8.49 | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q75 | 30.6 | 4.9% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| q90 | 115 | 1.9% | 5.8% | 11.9% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |

**spawn island area <= T**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 2.53e+05 | 51.6% | 77.0% | 90.2% | 92.6% | 95.7% | 94.9% | 78.5% | 86.7% |
| q25 | 9.53e+05 | 56.7% | 74.7% | 82.8% | 82.5% | 85.9% | 86.2% | 77.8% | 79.7% |
| q50 | 2,175,192 | 34.7% | 52.3% | 57.9% | 60.9% | 62.4% | 62.0% | 57.0% | 57.3% |
| q75 | 3,382,380 | 20.3% | 34.6% | 40.3% | 40.0% | 42.2% | 41.6% | 40.9% | 43.5% |
| q90 | 4,655,462 | 11.2% | 20.2% | 24.1% | 25.3% | 26.4% | 26.6% | 30.5% | 30.7% |

**island count (>=1 km2) <= T**

| threshold | T | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| q10 | 33 | 53.1% | 71.5% | 79.6% | 69.0% | 59.5% | 60.0% | 24.1% | 36.6% |
| q25 | 34 | 48.8% | 66.2% | 74.5% | 63.1% | 55.2% | 58.2% | 19.3% | 31.8% |
| q50 | 37 | 28.4% | 44.3% | 56.9% | 42.7% | 33.1% | 39.8% | 9.6% | 16.3% |
| q75 | 39 | 16.0% | 29.2% | 39.8% | 28.8% | 19.1% | 24.9% | 3.6% | 8.2% |
| q90 | 41 | 8.2% | 16.1% | 23.1% | 15.8% | 9.9% | 11.7% | 1.4% | 3.3% |

### margin that removes every false negative on this sample, and what it costs

margin = max over the sample of (G12 - coarse)/G12, i.e. the worst under-read. inflation = survivors at the relaxed threshold / seeds that truly qualify, at the median threshold.


required margin for ">= T" goals (relax the coarse threshold DOWN by this):

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows area | 0.392% | 1.01% | 2.72% | 6.65% | 8.41% | 9.82% | 21.1% | 24.0% |
| Swamp area | 0.438% | 1.29% | 4.23% | 8.19% | 10.6% | 12.7% | 26.7% | 31.2% |
| Mountain area | 0.389% | 0.943% | 3.72% | 6.14% | 10.5% | 8.96% | 17.2% | 20.6% |
| Plains area | 0.269% | 0.477% | 1.92% | 2.90% | 4.40% | 4.50% | 8.66% | 10.6% |
| Mistlands area | 0.214% | 0.513% | 0.807% | 2.51% | 2.78% | 3.44% | 6.91% | 8.21% |
| land share | 0.170% | 0.430% | 1.10% | 1.94% | 2.43% | 2.29% | 6.50% | 5.71% |
| largest island area | 38.5% | 41.9% | 35.5% | 47.8% | 41.0% | 37.1% | 29.8% | 39.0% |
| spawn island area | 100.0% | 100.0% | 99.8% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| island count (>=1 km2) | 5 | 7 | 7 | 9 | 10 | 11 | 17 | 18 |
| highest peak | 5.46% | 9.42% | 22.5% | 33.0% | 31.3% | 33.5% | 48.7% | 51.6% |
| land area within 2 km | 0.789% | 1.75% | 4.25% | 10.4% | 12.1% | 14.0% | 17.5% | 25.0% |
| Swamp area within 5 km | 0.630% | 1.58% | 5.47% | 10.6% | 10.2% | 14.1% | 32.2% | 41.6% |
| Swamp nearest dist | 0.187% | 0.205% | 0 | 0 | 0 | 0 | 0 | 0 |
| Mountain nearest dist | 14.0% | 3.17% | 0.704% | 0.433% | 0.749% | 0.105% | 0 | 0.005% |
| Mistlands nearest dist | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| coastline length | 26.1% | 49.9% | 65.8% | 72.6% | 74.5% | 75.2% | 78.6% | 80.1% |
| land area | 0.183% | 0.409% | 1.17% | 1.76% | 2.43% | 2.38% | 6.18% | 5.75% |
| BlackForest area | 0.256% | 0.453% | 1.23% | 3.75% | 3.49% | 4.84% | 8.31% | 10.5% |
| Ocean area | 0.133% | 0.186% | 0.969% | 0.924% | 1.98% | 2.66% | 3.15% | 5.17% |
| area above 100 m | 0.600% | 1.41% | 3.38% | 6.88% | 10.1% | 10.4% | 26.7% | 25.3% |

required margin for "<= T" goals (relax the coarse threshold UP by this):

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows area | 0.553% | 1.15% | 3.40% | 6.87% | 7.49% | 9.26% | 17.2% | 21.9% |
| Swamp area | 0.400% | 1.12% | 2.73% | 8.81% | 8.81% | 10.5% | 23.9% | 33.5% |
| Mountain area | 0.315% | 0.982% | 2.77% | 7.87% | 6.71% | 9.78% | 17.1% | 25.6% |
| Plains area | 0.260% | 0.503% | 1.49% | 4.48% | 3.89% | 4.60% | 8.68% | 10.0% |
| Mistlands area | 0.213% | 0.320% | 1.40% | 2.04% | 2.79% | 3.69% | 6.02% | 8.88% |
| land share | 0.194% | 0.377% | 1.04% | 1.77% | 2.25% | 2.99% | 4.45% | 5.19% |
| largest island area | 144.3% | 141.0% | 152.3% | 236.0% | 249.4% | 500.9% | 470.6% | 551.6% |
| spawn island area | 4,966,700% | 6,428,700% | 6,521,500% | 6,523,414% | 6,399,900% | 7,177,634% | 7,850,547% | 9,727,900% |
| island count (>=1 km2) | 7 | 13 | 12 | 14 | 12 | 13 | 11 | 12 |
| highest peak | 0.957% | 1.11% | 0.998% | 0.346% | 0.463% | 0.514% | 0.497% | 0.234% |
| land area within 2 km | 0.645% | 1.87% | 6.09% | 10.4% | 8.87% | 7.89% | 31.5% | 29.4% |
| Swamp area within 5 km | 0.548% | 1.69% | 3.36% | 10.6% | 13.0% | 17.8% | 32.2% | 44.8% |
| Swamp nearest dist | 2.24% | 4.65% | 13.7% | 16.7% | 16.2% | 24.9% | 39.7% | 46.8% |
| Mountain nearest dist | 138.9% | 138.8% | 174.3% | 208.1% | 237.5% | 347.8% | 470.3% | 466.4% |
| Mistlands nearest dist | 0.082% | 0.283% | 0.453% | 0.964% | 1.89% | 1.29% | 2.31% | 2.18% |
| coastline length | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| land area | 0.181% | 0.398% | 0.962% | 1.96% | 2.26% | 2.90% | 4.81% | 5.15% |
| BlackForest area | 0.245% | 0.525% | 1.55% | 3.20% | 3.48% | 3.77% | 10.1% | 11.3% |
| Ocean area | 0.048% | 0.296% | 0.490% | 1.71% | 1.73% | 2.50% | 4.92% | 5.92% |
| area above 100 m | 0.633% | 1.43% | 3.92% | 7.53% | 7.92% | 11.0% | 21.9% | 21.0% |

survivor inflation at the median threshold:

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows area | 1.05x | 1.11x | 1.30x | 1.64x | 1.65x | 1.77x | 1.99x | 1.99x |
| Swamp area | 1.06x | 1.14x | 1.39x | 1.72x | 1.83x | 1.86x | 1.99x | 2.00x |
| Mountain area | 1.05x | 1.13x | 1.46x | 1.62x | 1.85x | 1.77x | 1.94x | 1.96x |
| Plains area | 1.05x | 1.08x | 1.31x | 1.55x | 1.66x | 1.67x | 1.86x | 1.89x |
| Mistlands area | 1.05x | 1.12x | 1.22x | 1.52x | 1.59x | 1.71x | 1.95x | 1.98x |
| land share | 1.06x | 1.14x | 1.39x | 1.61x | 1.74x | 1.76x | 1.99x | 1.97x |
| largest island area | 1.99x | 2.00x | 2.00x | 2.00x | 2.00x | 2.00x | 2.00x | 2.00x |
| spawn island area | 2.00x | 2.00x | 2.00x | 2.00x | 2.00x | 2.00x | 2.00x | 2.00x |
| island count (>=1 km2) | 1.82x | 1.86x | 1.86x | 1.87x | 1.86x | 1.87x | 1.87x | 1.87x |
| highest peak | 1.62x | 1.66x | 1.85x | 1.98x | 1.93x | 1.95x | 2.00x | 2.00x |
| land area within 2 km | 1.02x | 1.06x | 1.19x | 1.40x | 1.37x | 1.43x | 1.61x | 1.80x |
| Swamp area within 5 km | 1.05x | 1.12x | 1.38x | 1.63x | 1.63x | 1.80x | 1.93x | 1.98x |
| Swamp nearest dist | 1.78x | 1.78x | 1.78x | 1.78x | 1.78x | 1.78x | 1.78x | 1.78x |
| Mountain nearest dist | 1.99x | 1.88x | 1.56x | 1.87x | 1.99x | 1.67x | 1.99x | 1.99x |
| Mistlands nearest dist | 1.00x | 1.00x | 1.00x | 1.00x | 1.00x | 1.00x | 1.00x | 1.00x |
| coastline length | 1.66x | 1.91x | 2.00x | 2.00x | 2.00x | 2.00x | 2.00x | 2.00x |
| land area | 1.06x | 1.14x | 1.39x | 1.61x | 1.74x | 1.76x | 1.99x | 1.97x |
| BlackForest area | 1.05x | 1.10x | 1.24x | 1.59x | 1.59x | 1.68x | 1.94x | 1.92x |
| Ocean area | 1.03x | 1.07x | 1.26x | 1.38x | 1.61x | 1.71x | 1.86x | 1.94x |
| area above 100 m | 1.06x | 1.15x | 1.35x | 1.64x | 1.79x | 1.81x | 1.99x | 1.98x |

survivor inflation at the q90 (selective) threshold:

| metric | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| Meadows area | 1.09x | 1.18x | 1.74x | 3.47x | 3.79x | 5.01x | 9.34x | 9.60x |
| Swamp area | 1.09x | 1.26x | 2.36x | 4.55x | 6.24x | 6.65x | 9.55x | 9.88x |
| Mountain area | 1.07x | 1.19x | 2.03x | 2.95x | 5.69x | 4.57x | 8.43x | 9.04x |
| Plains area | 1.11x | 1.23x | 1.98x | 3.18x | 4.11x | 4.26x | 7.16x | 8.25x |
| Mistlands area | 1.10x | 1.25x | 1.61x | 2.98x | 3.57x | 4.74x | 8.07x | 9.27x |
| land share | 1.18x | 1.35x | 2.36x | 3.48x | 4.41x | 4.70x | 9.39x | 8.82x |
| largest island area | 9.37x | 9.82x | 9.39x | 10.00x | 9.96x | 9.95x | 9.97x | 10.00x |
| spawn island area | 10.00x | 10.00x | 10.00x | 10.00x | 10.00x | 10.00x | 10.00x | 10.00x |
| island count (>=1 km2) | 5.36x | 6.60x | 6.72x | 6.81x | 6.80x | 6.92x | 7.01x | 7.02x |
| highest peak | 2.78x | 3.25x | 7.72x | 9.09x | 8.54x | 8.37x | 9.91x | 9.85x |
| land area within 2 km | 1.10x | 1.27x | 1.78x | 3.25x | 3.27x | 3.74x | 5.43x | 6.88x |
| Swamp area within 5 km | 1.12x | 1.35x | 2.36x | 4.50x | 4.55x | 6.14x | 9.15x | 9.78x |
| Swamp nearest dist | 5.27x | 5.27x | 5.27x | 5.27x | 5.27x | 5.27x | 5.27x | 5.27x |
| Mountain nearest dist | 2.81x | 1.41x | 1.76x | 2.45x | 3.36x | 3.28x | 7.35x | 10.00x |
| Mistlands nearest dist | 6.83x | 6.83x | 6.83x | 6.83x | 6.83x | 6.83x | 6.83x | 6.83x |
| coastline length | 3.55x | 5.92x | 9.17x | 9.68x | 9.99x | 9.90x | 9.96x | 9.99x |
| land area | 1.18x | 1.35x | 2.36x | 3.48x | 4.41x | 4.70x | 9.39x | 8.82x |
| BlackForest area | 1.07x | 1.12x | 1.61x | 3.23x | 3.22x | 4.02x | 7.74x | 7.90x |
| Ocean area | 1.05x | 1.13x | 1.66x | 2.02x | 2.77x | 3.31x | 4.63x | 6.73x |
| area above 100 m | 1.14x | 1.38x | 2.06x | 3.67x | 5.32x | 5.54x | 9.72x | 9.61x |

### 10.2 Fixed-threshold false negatives ("a nearby X", spawn island, peak)

seeds = 2560

### "A nearby X": nearest_distance(biome) <= T, false-negative rate

FN = the coarse grid said the biome is further away than T when G12 says it is within T.


**Meadows**

| T (m) | true matches at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| 600 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.2% |
| 800 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 1000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 1500 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 2000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 3000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 4000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 6000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |

**Swamp**

| T (m) | true matches at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| 3000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 4000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 6000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |

**Mountain**

| T (m) | true matches at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| 800 | 2292 | 0.6% | 2.1% | 8.0% | 15.7% | 25.8% | 24.9% | 70.4% | 100.0% |
| 1000 | 2431 | 0.4% | 1.2% | 5.0% | 9.8% | 13.6% | 14.9% | 32.4% | 49.8% |
| 1500 | 2548 | 0.1% | 0.2% | 0.8% | 2.2% | 3.3% | 3.7% | 10.7% | 20.6% |
| 2000 | 2560 | 0.0% | 0.0% | 0.0% | 0.2% | 0.2% | 0.6% | 2.9% | 5.9% |
| 3000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.2% | 0.5% |
| 4000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 6000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |

**BlackForest**

| T (m) | true matches at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| 600 | 2560 | 0.0% | 0.0% | 0.1% | 2.9% | 1.4% | 2.3% | 100.0% | 100.0% |
| 800 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 8.4% | 2.3% |
| 1000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 1500 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 2000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 3000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 4000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 6000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |

**Plains**

| T (m) | true matches at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| 3000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 100.0% | 19.4% |
| 4000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 6000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |

**Mistlands**

| T (m) | true matches at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| 6000 | 2560 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.4% | 0.1% |

### "Keep it far away": nearest_distance(biome) >= T, false-negative rate


**Meadows**

| T (m) | true matches at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|

**Swamp**

| T (m) | true matches at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|

**Mountain**

| T (m) | true matches at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| 800 | 268 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 1000 | 129 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| 1500 | 12 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |

**BlackForest**

| T (m) | true matches at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|

**Plains**

| T (m) | true matches at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|

**Mistlands**

| T (m) | true matches at G12 | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|

### margin in METRES that removes every false negative on the sample, per biome

"nearby" goals need the coarse threshold relaxed UP by max(G_r - G12); "far away" goals need it relaxed DOWN by max(G12 - G_r).

| biome | direction | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|---|
| Meadows | nearby (relax up) | 19 m | 45 m | 97 m | 181 m | 201 m | 220 m | 308 m | 396 m |
| Meadows | far away (relax down) | 5 m | 4 m | 1 m | 0 m | 0 m | 0 m | 0 m | 0 m |
| Swamp | nearby (relax up) | 46 m | 95 m | 274 m | 334 m | 324 m | 499 m | 793 m | 937 m |
| Swamp | far away (relax down) | 4 m | 4 m | 0 m | 0 m | 0 m | 0 m | 0 m | 0 m |
| Mountain | nearby (relax up) | 927 m | 926 m | 1,199 m | 1,368 m | 1,532 m | 2,257 m | 3,091 m | 3,015 m |
| Mountain | far away (relax down) | 130 m | 23 m | 5 m | 5 m | 5 m | 2 m | 0 m | 0 m |
| BlackForest | nearby (relax up) | 18 m | 43 m | 118 m | 234 m | 220 m | 265 m | 328 m | 471 m |
| BlackForest | far away (relax down) | 0 m | 0 m | 0 m | 0 m | 0 m | 0 m | 0 m | 0 m |
| Plains | nearby (relax up) | 7 m | 17 m | 36 m | 81 m | 113 m | 101 m | 254 m | 134 m |
| Plains | far away (relax down) | 3 m | 0 m | 0 m | 0 m | 0 m | 0 m | 0 m | 0 m |
| Mistlands | nearby (relax up) | 5 m | 17 m | 27 m | 57 m | 112 m | 76 m | 136 m | 128 m |
| Mistlands | far away (relax down) | 0 m | 0 m | 0 m | 0 m | 0 m | 0 m | 0 m | 0 m |

### spawn island: the metric, at the thresholds the shipped preset uses


**spawn_island_area >= 1,000,000 m2** - 1898 of 2560 seeds qualify at G12

| grid | reported | false neg | FN rate | false pos | precision of the coarse answer |
|---|---|---|---|---|---|
| G24 | 2256 | 16 | 0.8% | 374 | 83.4% |
| G48 | 2376 | 12 | 0.6% | 490 | 79.4% |
| G96 | 2426 | 13 | 0.7% | 541 | 77.7% |
| G164* | 2422 | 18 | 0.9% | 542 | 77.6% |
| G192 | 2440 | 19 | 1.0% | 561 | 77.0% |
| G219* | 2442 | 19 | 1.0% | 563 | 76.9% |
| G328* | 2311 | 92 | 4.8% | 505 | 78.1% |
| G384 | 2356 | 71 | 3.7% | 529 | 77.5% |

**spawn_island_area >= 2,000,000 m2** - 1378 of 2560 seeds qualify at G12

| grid | reported | false neg | FN rate | false pos | precision of the coarse answer |
|---|---|---|---|---|---|
| G24 | 1825 | 11 | 0.8% | 458 | 74.9% |
| G48 | 2017 | 15 | 1.1% | 654 | 67.6% |
| G96 | 2089 | 19 | 1.4% | 730 | 65.1% |
| G164* | 2091 | 36 | 2.6% | 749 | 64.2% |
| G192 | 2106 | 43 | 3.1% | 771 | 63.4% |
| G219* | 2102 | 39 | 2.8% | 763 | 63.7% |
| G328* | 1982 | 109 | 7.9% | 713 | 64.0% |
| G384 | 1966 | 121 | 8.8% | 709 | 63.9% |

**spawn_island_area >= 3,000,000 m2** - 802 of 2560 seeds qualify at G12

| grid | reported | false neg | FN rate | false pos | precision of the coarse answer |
|---|---|---|---|---|---|
| G24 | 1191 | 19 | 2.4% | 408 | 65.7% |
| G48 | 1454 | 21 | 2.6% | 673 | 53.7% |
| G96 | 1565 | 20 | 2.5% | 783 | 50.0% |
| G164* | 1540 | 45 | 5.6% | 783 | 49.2% |
| G192 | 1565 | 52 | 6.5% | 815 | 47.9% |
| G219* | 1562 | 54 | 6.7% | 814 | 47.9% |
| G328* | 1527 | 99 | 12.3% | 824 | 46.0% |
| G384 | 1493 | 106 | 13.2% | 797 | 46.6% |

**spawn_island_area >= 5,000,000 m2** - 207 of 2560 seeds qualify at G12

| grid | reported | false neg | FN rate | false pos | precision of the coarse answer |
|---|---|---|---|---|---|
| G24 | 436 | 6 | 2.9% | 235 | 46.1% |
| G48 | 607 | 10 | 4.8% | 410 | 32.5% |
| G96 | 694 | 13 | 6.3% | 500 | 28.0% |
| G164* | 713 | 19 | 9.2% | 525 | 26.4% |
| G192 | 744 | 18 | 8.7% | 555 | 25.4% |
| G219* | 737 | 22 | 10.6% | 552 | 25.1% |
| G328* | 844 | 34 | 16.4% | 671 | 20.5% |
| G384 | 855 | 35 | 16.9% | 683 | 20.1% |

### highest peak, at round thresholds


**highest_peak >= 400 m** - 2084 of 2560

| grid | reported | false neg | FN rate |
|---|---|---|---|
| G24 | 2039 | 50 | 2.4% |
| G48 | 1678 | 406 | 19.5% |
| G96 | 957 | 1127 | 54.1% |
| G164* | 612 | 1472 | 70.6% |
| G192 | 425 | 1659 | 79.6% |
| G219* | 350 | 1734 | 83.2% |
| G328* | 164 | 1920 | 92.1% |
| G384 | 107 | 1977 | 94.9% |

**highest_peak >= 450 m** - 706 of 2560

| grid | reported | false neg | FN rate |
|---|---|---|---|
| G24 | 706 | 0 | 0.0% |
| G48 | 482 | 224 | 31.7% |
| G96 | 125 | 581 | 82.3% |
| G164* | 56 | 650 | 92.1% |
| G192 | 39 | 667 | 94.5% |
| G219* | 26 | 680 | 96.3% |
| G328* | 15 | 691 | 97.9% |
| G384 | 12 | 694 | 98.3% |

### 10.3 Margin sweep: false negatives / survivors, 15 goals

seeds = 2560

Each cell is `FN / survivors`: FN = true matches still lost at that margin,
survivors = seeds the relaxed coarse filter passes on to the exact re-measure.
`survivors` is the real cost: every one of them costs a full G12 evaluation.


### Meadows area >= 11 km2  -  966 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 2 / 973 | 14 / 973 | 46 / 975 | 72 / 998 | 174 / 855 | 133 / 1001 | 157 / 1163 | 190 / 1131 |
| 1% | 0 / 1116 | 0 / 1114 | 5 / 1111 | 47 / 1124 | 107 / 969 | 89 / 1107 | 118 / 1284 | 142 / 1272 |
| 2% | 0 / 1252 | 0 / 1249 | 0 / 1247 | 20 / 1261 | 67 / 1102 | 53 / 1224 | 88 / 1399 | 142 / 1272 |
| 5% | 0 / 1695 | 0 / 1688 | 0 / 1685 | 1 / 1687 | 4 / 1515 | 6 / 1622 | 28 / 1705 | 47 / 1697 |
| 10% | 0 / 2203 | 0 / 2199 | 0 / 2193 | 0 / 2196 | 0 / 2082 | 0 / 2141 | 3 / 2181 | 8 / 2061 |
| 20% | 0 / 2544 | 0 / 2546 | 0 / 2547 | 0 / 2545 | 0 / 2529 | 0 / 2536 | 0 / 2520 | 0 / 2508 |
| 35% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2559 | 0 / 2559 |
| 50% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |

### Swamp area >= 9.5 km2  -  742 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 6 / 743 | 13 / 742 | 64 / 709 | 87 / 767 | 77 / 854 | 182 / 708 | 306 / 643 | 213 / 980 |
| 1% | 0 / 875 | 0 / 861 | 8 / 852 | 54 / 854 | 51 / 957 | 135 / 818 | 260 / 732 | 170 / 1133 |
| 2% | 0 / 1010 | 0 / 1002 | 1 / 957 | 21 / 1013 | 27 / 1089 | 90 / 951 | 214 / 838 | 170 / 1133 |
| 5% | 0 / 1410 | 0 / 1407 | 0 / 1362 | 1 / 1382 | 2 / 1497 | 20 / 1357 | 108 / 1174 | 94 / 1442 |
| 10% | 0 / 2052 | 0 / 2039 | 0 / 1997 | 0 / 2029 | 0 / 2063 | 0 / 1957 | 33 / 1644 | 18 / 1952 |
| 20% | 0 / 2520 | 0 / 2519 | 0 / 2513 | 0 / 2506 | 0 / 2517 | 0 / 2504 | 0 / 2378 | 1 / 2420 |
| 35% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2559 | 0 / 2560 | 0 / 2556 | 0 / 2557 |
| 50% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |

### Mountain area >= 12 km2  -  345 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 5 / 343 | 4 / 349 | 23 / 345 | 42 / 352 | 63 / 349 | 56 / 370 | 95 / 473 | 109 / 510 |
| 1% | 0 / 420 | 0 / 413 | 4 / 417 | 19 / 413 | 33 / 427 | 32 / 423 | 79 / 539 | 97 / 594 |
| 2% | 0 / 487 | 0 / 488 | 0 / 503 | 4 / 504 | 20 / 503 | 9 / 538 | 64 / 621 | 74 / 712 |
| 5% | 0 / 826 | 0 / 831 | 0 / 824 | 0 / 811 | 0 / 857 | 0 / 858 | 16 / 1028 | 38 / 942 |
| 10% | 0 / 1658 | 0 / 1642 | 0 / 1654 | 0 / 1627 | 0 / 1664 | 0 / 1605 | 2 / 1558 | 7 / 1494 |
| 20% | 0 / 2472 | 0 / 2469 | 0 / 2466 | 0 / 2460 | 0 / 2450 | 0 / 2446 | 0 / 2370 | 0 / 2314 |
| 35% | 0 / 2559 | 0 / 2559 | 0 / 2560 | 0 / 2560 | 0 / 2557 | 0 / 2557 | 0 / 2559 | 0 / 2549 |
| 50% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |

### Plains area >= 46 km2  -  641 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 8 / 637 | 10 / 641 | 40 / 628 | 22 / 758 | 63 / 665 | 83 / 667 | 157 / 668 | 169 / 714 |
| 1% | 0 / 819 | 0 / 821 | 1 / 819 | 3 / 963 | 14 / 857 | 19 / 887 | 81 / 880 | 104 / 907 |
| 2% | 0 / 1038 | 0 / 1049 | 0 / 1038 | 0 / 1174 | 1 / 1085 | 3 / 1090 | 42 / 1056 | 72 / 1056 |
| 5% | 0 / 1727 | 0 / 1729 | 0 / 1717 | 0 / 1819 | 0 / 1752 | 0 / 1724 | 2 / 1626 | 9 / 1621 |
| 10% | 0 / 2375 | 0 / 2376 | 0 / 2368 | 0 / 2399 | 0 / 2373 | 0 / 2370 | 0 / 2292 | 0 / 2281 |
| 20% | 0 / 2517 | 0 / 2517 | 0 / 2516 | 0 / 2519 | 0 / 2519 | 0 / 2517 | 0 / 2516 | 0 / 2512 |
| 35% | 0 / 2559 | 0 / 2559 | 0 / 2559 | 0 / 2559 | 0 / 2559 | 0 / 2559 | 0 / 2558 | 0 / 2556 |
| 50% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |

### Mistlands area >= 61 km2  -  573 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 9 / 567 | 19 / 563 | 19 / 606 | 96 / 541 | 76 / 573 | 81 / 632 | 130 / 697 | 62 / 1059 |
| 1% | 0 / 872 | 0 / 860 | 0 / 907 | 9 / 814 | 7 / 856 | 14 / 933 | 57 / 972 | 29 / 1315 |
| 2% | 0 / 1211 | 0 / 1204 | 0 / 1246 | 0 / 1116 | 0 / 1189 | 0 / 1256 | 20 / 1205 | 12 / 1569 |
| 5% | 0 / 2077 | 0 / 2070 | 0 / 2092 | 0 / 2018 | 0 / 2055 | 0 / 2070 | 0 / 1998 | 0 / 2199 |
| 10% | 0 / 2543 | 0 / 2539 | 0 / 2542 | 0 / 2536 | 0 / 2536 | 0 / 2537 | 0 / 2525 | 0 / 2535 |
| 20% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |
| 35% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |
| 50% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |

### land area >= 125 km2  -  14 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 0 / 14 | 0 / 14 | 3 / 12 | 3 / 19 | 8 / 14 | 3 / 24 | 3 / 42 | 4 / 33 |
| 1% | 0 / 39 | 0 / 41 | 0 / 41 | 0 / 49 | 1 / 46 | 0 / 56 | 2 / 100 | 2 / 77 |
| 2% | 0 / 100 | 0 / 100 | 0 / 107 | 0 / 121 | 0 / 117 | 0 / 155 | 1 / 206 | 0 / 171 |
| 5% | 0 / 959 | 0 / 948 | 0 / 954 | 0 / 1011 | 0 / 980 | 0 / 1068 | 0 / 1134 | 0 / 969 |
| 10% | 0 / 2519 | 0 / 2519 | 0 / 2519 | 0 / 2516 | 0 / 2499 | 0 / 2517 | 0 / 2495 | 0 / 2467 |
| 20% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |
| 35% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |
| 50% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |

### land share >= 0.345  -  708 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 7 / 711 | 36 / 685 | 35 / 728 | 105 / 669 | 89 / 720 | 56 / 844 | 155 / 767 | 194 / 742 |
| 1% | 0 / 1132 | 0 / 1101 | 0 / 1131 | 5 / 1067 | 3 / 1116 | 3 / 1250 | 45 / 1134 | 72 / 1140 |
| 2% | 0 / 1560 | 0 / 1535 | 0 / 1580 | 0 / 1514 | 0 / 1564 | 0 / 1674 | 10 / 1535 | 28 / 1511 |
| 5% | 0 / 2475 | 0 / 2465 | 0 / 2470 | 0 / 2440 | 0 / 2445 | 0 / 2467 | 0 / 2392 | 0 / 2380 |
| 10% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2558 | 0 / 2559 |
| 20% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |
| 35% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |
| 50% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |

### Swamp within 5 km >= 6 km2  -  889 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 5 / 887 | 17 / 893 | 66 / 853 | 101 / 882 | 82 / 1000 | 137 / 948 | 255 / 889 | 200 / 1153 |
| 1% | 0 / 999 | 0 / 1002 | 22 / 964 | 76 / 960 | 57 / 1063 | 109 / 1035 | 255 / 889 | 200 / 1153 |
| 2% | 0 / 1107 | 0 / 1110 | 7 / 1058 | 46 / 1051 | 38 / 1164 | 67 / 1189 | 199 / 1020 | 140 / 1332 |
| 5% | 0 / 1429 | 0 / 1432 | 0 / 1412 | 3 / 1387 | 7 / 1455 | 25 / 1429 | 117 / 1297 | 101 / 1505 |
| 10% | 0 / 1903 | 0 / 1908 | 0 / 1878 | 0 / 1827 | 0 / 1885 | 1 / 1949 | 60 / 1581 | 45 / 1805 |
| 20% | 0 / 2368 | 0 / 2369 | 0 / 2356 | 0 / 2338 | 0 / 2356 | 0 / 2360 | 1 / 2175 | 2 / 2252 |
| 35% | 0 / 2507 | 0 / 2505 | 0 / 2502 | 0 / 2503 | 0 / 2514 | 0 / 2508 | 0 / 2468 | 0 / 2492 |
| 50% | 0 / 2558 | 0 / 2558 | 0 / 2556 | 0 / 2556 | 0 / 2558 | 0 / 2558 | 0 / 2553 | 0 / 2547 |

### land within 2 km >= 5.8 km2  -  681 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 7 / 677 | 17 / 673 | 23 / 706 | 41 / 694 | 131 / 566 | 151 / 546 | 47 / 860 | 76 / 798 |
| 1% | 0 / 740 | 0 / 743 | 5 / 774 | 27 / 741 | 90 / 635 | 90 / 646 | 47 / 860 | 36 / 952 |
| 2% | 0 / 800 | 0 / 805 | 0 / 845 | 8 / 804 | 70 / 672 | 65 / 699 | 23 / 986 | 36 / 952 |
| 5% | 0 / 992 | 0 / 992 | 0 / 1048 | 0 / 1009 | 7 / 874 | 18 / 838 | 11 / 1094 | 17 / 1094 |
| 10% | 0 / 1298 | 0 / 1296 | 0 / 1333 | 0 / 1319 | 0 / 1196 | 0 / 1167 | 0 / 1390 | 5 / 1376 |
| 20% | 0 / 1823 | 0 / 1823 | 0 / 1840 | 0 / 1826 | 0 / 1757 | 0 / 1751 | 0 / 1840 | 0 / 1861 |
| 35% | 0 / 2383 | 0 / 2387 | 0 / 2386 | 0 / 2377 | 0 / 2337 | 0 / 2344 | 0 / 2380 | 0 / 2356 |
| 50% | 0 / 2546 | 0 / 2547 | 0 / 2548 | 0 / 2547 | 0 / 2545 | 0 / 2543 | 0 / 2549 | 0 / 2540 |

### area above 200 m >= 1 km2  -  942 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 8 / 941 | 40 / 916 | 98 / 917 | 208 / 887 | 244 / 860 | 206 / 1071 | 346 / 937 | 284 / 1185 |
| 1% | 0 / 989 | 6 / 984 | 60 / 1003 | 136 / 1054 | 147 / 1065 | 206 / 1071 | 346 / 937 | 284 / 1185 |
| 2% | 0 / 1037 | 1 / 1044 | 43 / 1055 | 136 / 1054 | 147 / 1065 | 206 / 1071 | 346 / 937 | 284 / 1185 |
| 5% | 0 / 1269 | 0 / 1261 | 15 / 1243 | 86 / 1235 | 75 / 1276 | 136 / 1319 | 192 / 1349 | 284 / 1185 |
| 10% | 0 / 1621 | 0 / 1608 | 0 / 1595 | 15 / 1547 | 31 / 1480 | 67 / 1558 | 192 / 1349 | 284 / 1185 |
| 20% | 0 / 2015 | 0 / 2019 | 0 / 2017 | 0 / 1996 | 3 / 1978 | 17 / 1942 | 97 / 1713 | 140 / 1669 |
| 35% | 0 / 2364 | 0 / 2370 | 0 / 2362 | 0 / 2340 | 0 / 2344 | 0 / 2330 | 38 / 2042 | 38 / 2097 |
| 50% | 0 / 2546 | 0 / 2547 | 0 / 2545 | 0 / 2534 | 0 / 2532 | 0 / 2493 | 0 / 2436 | 6 / 2350 |

### largest island >= 6 km2  -  643 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 32 / 741 | 57 / 821 | 94 / 875 | 113 / 1324 | 86 / 1650 | 81 / 1881 | 24 / 2399 | 26 / 2454 |
| 1% | 24 / 783 | 51 / 858 | 80 / 924 | 103 / 1367 | 80 / 1680 | 72 / 1911 | 24 / 2399 | 26 / 2454 |
| 2% | 23 / 827 | 49 / 905 | 75 / 959 | 95 / 1419 | 72 / 1727 | 68 / 1964 | 18 / 2425 | 17 / 2480 |
| 5% | 17 / 973 | 34 / 1057 | 58 / 1133 | 70 / 1585 | 55 / 1856 | 50 / 2067 | 13 / 2465 | 13 / 2499 |
| 10% | 14 / 1241 | 24 / 1347 | 48 / 1429 | 42 / 1849 | 36 / 2041 | 26 / 2241 | 7 / 2497 | 5 / 2529 |
| 20% | 2 / 1888 | 9 / 2069 | 10 / 2149 | 19 / 2306 | 14 / 2375 | 12 / 2447 | 0 / 2543 | 0 / 2555 |
| 35% | 0 / 2461 | 1 / 2500 | 0 / 2520 | 1 / 2536 | 1 / 2551 | 0 / 2558 | 0 / 2560 | 0 / 2560 |
| 50% | 0 / 2555 | 0 / 2559 | 0 / 2558 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |

### spawn island >= 3 km2  -  802 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 19 / 1191 | 21 / 1454 | 20 / 1565 | 45 / 1540 | 52 / 1565 | 54 / 1562 | 99 / 1527 | 106 / 1493 |
| 1% | 17 / 1215 | 18 / 1475 | 18 / 1579 | 42 / 1558 | 45 / 1586 | 54 / 1562 | 99 / 1527 | 106 / 1493 |
| 2% | 17 / 1231 | 18 / 1491 | 16 / 1594 | 40 / 1577 | 43 / 1610 | 49 / 1583 | 99 / 1527 | 90 / 1548 |
| 5% | 16 / 1298 | 15 / 1544 | 15 / 1655 | 32 / 1648 | 40 / 1648 | 42 / 1629 | 90 / 1576 | 90 / 1548 |
| 10% | 15 / 1378 | 15 / 1614 | 12 / 1729 | 27 / 1716 | 29 / 1725 | 30 / 1718 | 80 / 1635 | 75 / 1606 |
| 20% | 12 / 1566 | 12 / 1790 | 9 / 1890 | 20 / 1885 | 21 / 1904 | 21 / 1882 | 69 / 1787 | 53 / 1767 |
| 35% | 8 / 1860 | 8 / 2034 | 9 / 2114 | 17 / 2115 | 17 / 2136 | 17 / 2124 | 51 / 1982 | 32 / 1966 |
| 50% | 8 / 2068 | 4 / 2235 | 4 / 2306 | 8 / 2298 | 9 / 2320 | 12 / 2302 | 34 / 2204 | 22 / 2171 |

### islands (>=1 km2) >= 40  -  553 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 87 / 788 | 74 / 1066 | 59 / 1292 | 145 / 987 | 229 / 707 | 246 / 807 | 486 / 140 | 454 / 263 |
| 1% | 87 / 788 | 74 / 1066 | 59 / 1292 | 145 / 987 | 229 / 707 | 246 / 807 | 486 / 140 | 454 / 263 |
| 2% | 87 / 788 | 74 / 1066 | 59 / 1292 | 145 / 987 | 229 / 707 | 246 / 807 | 486 / 140 | 454 / 263 |
| 5% | 18 / 1388 | 19 / 1641 | 14 / 1859 | 57 / 1518 | 116 / 1241 | 129 / 1342 | 410 / 367 | 351 / 573 |
| 10% | 1 / 1950 | 1 / 2119 | 1 / 2238 | 16 / 1992 | 40 / 1767 | 40 / 1825 | 288 / 744 | 228 / 1007 |
| 20% | 0 / 2490 | 0 / 2524 | 0 / 2532 | 0 / 2480 | 3 / 2394 | 1 / 2416 | 85 / 1679 | 52 / 1923 |
| 35% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 2 / 2513 | 1 / 2516 |
| 50% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 |

### highest peak >= 450 m  -  706 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 0 / 706 | 224 / 482 | 581 / 125 | 650 / 56 | 667 / 39 | 680 / 26 | 691 / 15 | 694 / 12 |
| 1% | 0 / 706 | 65 / 641 | 504 / 202 | 616 / 90 | 642 / 64 | 659 / 47 | 686 / 20 | 688 / 18 |
| 2% | 0 / 708 | 8 / 700 | 411 / 295 | 571 / 135 | 610 / 96 | 637 / 69 | 680 / 26 | 681 / 25 |
| 5% | 0 / 817 | 0 / 730 | 113 / 602 | 435 / 271 | 516 / 192 | 565 / 143 | 642 / 66 | 660 / 48 |
| 10% | 0 / 1820 | 0 / 1448 | 4 / 883 | 222 / 541 | 382 / 370 | 454 / 297 | 584 / 137 | 618 / 97 |
| 20% | 0 / 2318 | 0 / 2264 | 0 / 1965 | 9 / 1243 | 67 / 1084 | 166 / 868 | 423 / 428 | 513 / 286 |
| 35% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2491 | 0 / 2451 | 0 / 2386 | 114 / 1757 | 196 / 1400 |
| 50% | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2560 | 0 / 2555 | 1 / 2520 |

### coastline >= 2750 km  -  718 of 2560 seeds qualify at G12

| margin | G24 | G48 | G96 | G164* | G192 | G219* | G328* | G384 |
|---|---|---|---|---|---|---|---|---|
| 0% | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 |
| 1% | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 |
| 2% | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 |
| 5% | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 |
| 10% | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 |
| 20% | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 |
| 35% | 0 / 2560 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 |
| 50% | 0 / 2560 | 0 / 2226 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 | 718 / 0 |

### 10.4 Islands, 512 seeds

seeds = 512

### raw component count (no A_min) - the number the spec calls meaningless

| grid | median count | median vs G12 | min ratio | max ratio |
|---|---|---|---|---|
| G12 | 9,868 | 1.000x | 1.000x | 1.000x |
| G24 | 4,892 | 0.496x | 0.455x | 0.531x |
| G48 | 1,706 | 0.173x | 0.152x | 0.190x |
| G96 | 499 | 0.051x | 0.043x | 0.058x |
| G164.0625 | 239 | 0.024x | 0.021x | 0.029x |
| G192 | 208 | 0.021x | 0.018x | 0.024x |
| G384 | 128 | 0.013x | 0.010x | 0.016x |

### island count, A_min = 1 ha (the default)

| grid | exact match | median signed err | p95 |rel err| | max |rel err| | min signed | max signed |
|---|---|---|---|---|---|---|
| G24 | 2.1% | -5 | 8.58% | 14.7% | -37 | +21 |
| G48 | 1.0% | +20 | 18.7% | 30.3% | -20 | +61 |
| G96 | 1.4% | +4 | 12.9% | 22.5% | -39 | +41 |
| G164.0625 | 0.6% | +15 | 18.8% | 37.9% | -30 | +78 |
| G192 | 2.0% | -16 | 16.4% | 25.7% | -62 | +26 |
| G384 | 0.0% | -97 | 50.0% | 55.8% | -138 | -49 |

### island count, A_min = 1 km2

| grid | exact match | median signed err | p95 |rel err| | max |rel err| | min signed | max signed |
|---|---|---|---|---|---|---|
| G24 | 19.3% | +1 | 12.9% | 21.9% | -3 | +7 |
| G48 | 10.7% | +2 | 18.2% | 39.4% | -4 | +13 |
| G96 | 8.0% | +3 | 22.0% | 36.4% | -4 | +12 |
| G164.0625 | 12.3% | +2 | 20.6% | 36.4% | -8 | +12 |
| G192 | 11.3% | +1 | 18.8% | 32.3% | -9 | +10 |
| G384 | 7.2% | -2 | 25.6% | 38.6% | -17 | +12 |

### total land area (the sanity control - bulk area is not topological)

| grid | median |rel| | p95 | max |
|---|---|---|---|
| G24 | 0.035% | 0.101% | 0.173% |
| G48 | 0.087% | 0.239% | 0.409% |
| G96 | 0.200% | 0.600% | 0.858% |
| G164.0625 | 0.361% | 1.01% | 1.87% |
| G192 | 0.455% | 1.27% | 2.01% |
| G384 | 1.06% | 3.11% | 5.15% |

### spawn island, decomposed

`own` = the grid's own rule-2 answer (component nearest the origin - what the search computes today).
`anchored` = the component that contains the point where G12's spawn island is - i.e. what you would
get if the spawn island were anchored on a fixed world point (spec rule 1, the StartTemple anchor).

| grid | own: median |rel| | own p95 | own max | own >2x or <0.5x | anchored: median |rel| | anchored p95 | anchored max | anchor not found |
|---|---|---|---|---|---|---|---|---|
| G24 | 10.6% | 743.1% | 4,517,500% | 19.5% | 9.96% | 410.4% | 4,517,500% | 1.0% |
| G48 | 28.1% | 1,889% | 4,556,700% | 30.5% | 28.0% | 1,866% | 4,556,700% | 0.4% |
| G96 | 39.3% | 56,656% | 4,505,500% | 34.0% | 39.3% | 27,207% | 4,505,500% | 0.0% |
| G164.0625 | 43.5% | 77,691% | 4,953,284% | 37.3% | 43.5% | 77,691% | 4,953,284% | 0.0% |
| G192 | 42.4% | 113,955% | 5,631,900% | 35.4% | 41.7% | 113,955% | 5,631,900% | 0.0% |
| G384 | 62.7% | 86,765% | 7,270,300% | 42.0% | 57.1% | 86,765% | 6,553,500% | 0.0% |

### largest island

| grid | median |rel| | p95 | max | signed median | fraction > +10% | fraction < -10% |
|---|---|---|---|---|---|---|
| G24 | 0.226% | 34.1% | 107.2% | +0.063% | 14.8% | 2.1% |
| G48 | 1.68% | 47.0% | 117.5% | +0.402% | 24.6% | 4.5% |
| G96 | 6.10% | 50.7% | 119.5% | +3.19% | 35.2% | 5.7% |
| G164.0625 | 15.9% | 88.3% | 148.9% | +12.9% | 53.1% | 6.8% |
| G192 | 24.3% | 114.0% | 216.5% | +22.8% | 63.7% | 4.5% |
| G384 | 82.9% | 230.9% | 390.2% | +82.9% | 94.3% | 0.8% |

### merging: the component that sits where G12's largest island is

If coarsening bridged a strait, the component at that place is BIGGER than G12's largest island.

| grid | median ratio | p95 ratio | max ratio | fraction >= 1.10x | fraction >= 1.5x |
|---|---|---|---|---|---|
| G24 | 1.000x | 1.134x | 1.64x | 7.6% | 0.6% |
| G48 | 1.001x | 1.246x | 2.10x | 11.3% | 1.2% |
| G96 | 1.003x | 1.246x | 2.12x | 13.1% | 1.4% |
| G164.0625 | 1.007x | 1.530x | 2.43x | 19.9% | 5.7% |
| G192 | 1.009x | 1.588x | 2.53x | 22.5% | 6.2% |
| G384 | 1.011x | 2.231x | 4.90x | 40.0% | 20.9% |

### spawn anchor drift: how far the rule-2 anchor point moves from G12's

| grid | median drift m | p95 | max | origin_dist > 500 m at G12 | at this grid |
|---|---|---|---|---|---|
| G24 | 8.5 | 35.0 | 641.4 | 0 | 0 |
| G48 | 25.5 | 80.0 | 400.2 | 0 | 0 |
| G96 | 59.4 | 147.9 | 657.3 | 0 | 0 |
| G164.0625 | 107.5 | 228.6 | 602.6 | 0 | 0 |
| G192 | 127.3 | 259.7 | 527.5 | 0 | 0 |
| G384 | 263.0 | 328.6 | 893.5 | 0 | 19 |

### archipelago preset replay: island_count(A_min = 1 ha) >= 120, at each grid

true matches at G12: 512 of 512

| grid | reported | false negatives | FN rate | false positives |
|---|---|---|---|---|
| G24 | 512 | 0 | 0.0% | 0 |
| G48 | 512 | 0 | 0.0% | 0 |
| G96 | 512 | 0 | 0.0% | 0 |
| G164.0625 | 512 | 0 | 0.0% | 0 |
| G192 | 512 | 0 | 0.0% | 0 |
| G384 | 424 | 88 | 17.2% | 0 |

### 10.5 Shipped-preset replay


## gentle-start: spawn_island_area >= 3,000,000 (must)  (preset grid = 96 m)
   only the spawn-island must-goal; the area_within goals were not sampled at r=1000/1200.
   seeds                      2560
   pass at G12 (the truth)    802
   pass at G96            1565
   FALSE NEGATIVES (lost)     20   = 2.5% of true matches
   false positives (extra)    783   = 50.0% of what the run would report

## archipelago: largest_island_area <= 3,000,000 (nice) + land_area 80-130 km2  (preset grid = 24 m)
   island_count at A_min = 1 ha is in the islands study, not here.
   seeds                      2560
   pass at G12 (the truth)    9
   pass at G24            5
   FALSE NEGATIVES (lost)     4   = 44.4% of true matches
   false positives (extra)    0   = 0.0% of what the run would report

## large-continents-style: largest_island_area >= 50 km2  (preset grid = 24 m)
   
   seeds                      2560
   pass at G12 (the truth)    0
   pass at G24            0
   FALSE NEGATIVES (lost)     0   = 0.0% of true matches
   false positives (extra)    0   = 0.0% of what the run would report

## mountain-home-style: Mountain area >= 25 km2 and nearest Mountain <= 1500 m  (preset grid = 96 m)
   
   seeds                      2560
   pass at G12 (the truth)    0
   pass at G96            0
   FALSE NEGATIVES (lost)     0   = 0.0% of true matches
   false positives (extra)    0   = 0.0% of what the run would report

## balanced-style: every biome share within 0.5x-2x of its G12 median  (preset grid = 192 m)
   
   seeds                      2560
   pass at G12 (the truth)    2560
   pass at G192            2560
   FALSE NEGATIVES (lost)     0   = 0.0% of true matches
   false positives (extra)    0   = 0.0% of what the run would report

## spawn_island_area >= 3,000,000 at every grid
   true matches at G12: 802 of 2560
   grid | pass | false neg | FN rate | false pos
          24 | 1191 |        19 |    2.4% |       408
          48 | 1454 |        21 |    2.6% |       673
          96 | 1565 |        20 |    2.5% |       783
    164.0625 | 1540 |        45 |    5.6% |       783
         192 | 1565 |        52 |    6.5% |       815
      218.75 | 1562 |        54 |    6.7% |       814
     328.125 | 1527 |        99 |   12.3% |       824
         384 | 1493 |       106 |   13.2% |       797

## largest_island_area >= 20 km2 at every grid
   true matches at G12: 0 of 2560
          24 | fn    0 (  0.0%) | fp    0
          48 | fn    0 (  0.0%) | fp    0
          96 | fn    0 (  0.0%) | fp    0
    164.0625 | fn    0 (  0.0%) | fp    0
         192 | fn    0 (  0.0%) | fp    0
      218.75 | fn    0 (  0.0%) | fp    1
     328.125 | fn    0 (  0.0%) | fp    4
         384 | fn    0 (  0.0%) | fp   14
