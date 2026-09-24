# The fate of `coastline_length`, and a sweep of every other metric for the same defect

Measurement and design only. Nothing under `E:\SteamLibrary\steamapps\common\Valheim\_ModSource\SeedLab`
was written to. All harness output is in this directory.

## Sources

| what | where | n |
|---|---|---|
| existing resolution study | `scratchpad\searchanalysis\study.csv` | 2,560 seeds x 9 grids |
| new shore-metric harness | `scratchpad\metrictruth\shore.csv` (harness `shore\Program.cs`, SeedLab's own `SeedSampler`/`WorldMeasurement` copied verbatim) | 512 seeds x 9 grids |
| analysis scripts | `rank.py` `dist.py` `sweep.py` `floors.py` `shore_an.py` `join.py` `margin.py` | - |
| raw outputs | `sweep.txt` `floors.txt` `margin.txt` | - |

Both samples are `Permutation.Shuffle(index, 2^32, key=0)`, index 0.. - the search engine's own
shuffled scan order - so the 512-seed shore sample is a prefix subset of the 2,560-seed study sample
and the two join by seed.

Harness cross-check: seed -1773433966, G12, land area 119,439,792 m2 - identical to the figure
`vseed seed -1773433966` prints (119.44 km2) and to the study's G12 row.

Machine conditions for any timing below: other desktop applications, a second agent session and the
concurrent SeedLab workflow's `dotnet` (1,126 CPU-s accumulated) were all running. Prefer the work
counts and the ratios.

## 1. coastline_length - the consideration, and the verdict

### The consideration that exists

A fractal length on a fixed grid is still a *consistent ranking at that grid*. Measured, Spearman
against the G12 answer, 2,560 seeds (`rank.py`):

| | G24 | G48 | G96 | G164 | G192 | G219 | G328 | G384 |
|---|---|---|---|---|---|---|---|---|
| coastline rho vs G12 | **0.966** | 0.894 | 0.757 | 0.624 | 0.553 | 0.512 | 0.407 | 0.390 |
| largest_island_area rho vs G12 | 0.851 | 0.734 | 0.661 | 0.365 | 0.269 | 0.171 | 0.061 | 0.051 |
| island_count(1 km2) rho vs G12 | 0.831 | 0.731 | 0.677 | 0.580 | 0.531 | 0.471 | 0.350 | 0.312 |

So coastline at G24 ranks seeds *better* than two metrics the tool keeps. The 100 % false-negative
rate is a pure offset artefact - the value is 0.756x the G12 value - not a ranking failure. That is
the honest consideration.

It is also a very clean fractal: ratio to G12 has p5..p95 inside +-1 point of the median at every
grid, implying a box dimension of 1.44-1.49 (`dist.py`). There is no convergence: the value simply
grows without limit as the grid refines, and G12 is not "the truth" either, it is just Valheim's
own map grid.

### The consideration that kills it anyway

**coastline_length does not distinguish Valheim worlds.** At G12 over 2,560 seeds:

| | p10 | p50 | p90 | p90/p10 | CV |
|---|---|---|---|---|---|
| coastline_length | 2,625,271 m | 2,712,816 m | 2,796,511 m | **1.065** | **0.0245** |
| land_area_within(500 m) | 306,850 m2 | 535,968 m2 | 724,176 m2 | 2.360 | 0.298 |
| spawn_island_area | 253,000 m2 | 2,175,000 m2 | 4,655,000 m2 | 18.40 | 0.735 |

Full range over the whole sample is 2,484 km to 2,924 km: the most "coastal" seed in 2,560 has 8 %
more coastline than the least. Coastline is the third least variable number in the whole catalogue,
after `water_area` and `land_area`, because every Valheim world has about the same land and about the
same fractal roughness. A ranking that is 100 % reliable but spans 8 % is not a search criterion.

### The shipped goal is provably inert

`src\SeedLab.Search\Presets\coastal-builder.json:19` -
`{"id":"shoreline","target":"world:coastline_length","metric":"coastline_length","test":"at_least","value":400000,"importance":"nice"}`
at `"grid": 24`.

- `CompiledGoal.Score` for `AtLeast` is `Clamp01(v / a)` (`CompiledQuery.cs:257-259`). With
  `a = 400,000` the score is exactly 1.0 for any seed with `v >= 400,000 m`. **Proof, from code.**
- Minimum coastline at G24 over 2,560 seeds: **1,899,888 m** = 4.75x the threshold. Minimum at G12:
  2,483,568 m. Zero seeds below it. **Measurement.**
- Total score is `sum(score_i * w_i) / sum(w_i)` (`SeedEvaluator.cs:387-395`). A goal that scores
  1.0 for every seed maps the remaining score `x` to `(xW + 1)/(W + 1)`, which is strictly increasing
  in `x`. **Deleting this goal cannot change the order of any result set.** Proof, not a sample.

So the goal has never filtered or ranked anything. Removing it is a no-op on output.

### Verdict: DELETE (option a). Not (b), not (c).

Option (b) - keep the computation, rename it to a grid-relative shape index - buys nothing. Measured
(`join.py`): the count of land cells adjacent to water (`coast_cells`) ranks seeds at
rho = 0.965/0.872/0.696 at G24/G48/G96 against its own G12 answer, i.e. **the same ranking as
coastline_length** (rho 0.967/0.897/0.752) and with the same CV of 0.0221. A rename would preserve a
metric that discriminates 8 % across the entire seed space and would still need "never comparable
across grids" stamped on it. It would be honest and useless.

Option (c) - replace - is the one that survives, and it is a *different shape* of metric (see 2).

## 2. The replacement: `shore_area_within`

### Definition

> **`world:shore_area_within` (needs `radius`), unit m2, tier T3.**
> The area of land (height >= 30 m) whose cell centre lies within **100 m** of the centre of a water
> cell, inside a disc of `radius` around the world centre. 100 m is part of the metric's definition,
> not a parameter.

Measured by an exact squared-Euclidean distance transform (Felzenszwalb-Huttenlocher, 2 linear passes)
over the same height field the other T3 metrics already use.

This is buildable shore in the sense a player means it: land you can put a dock, a longhouse and a
karve on. It is an **area**, not a length, so it is a proper measure of a set with an intrinsic
length scale and it converges as the grid refines, instead of diverging.

### It measures something the tool does not already have

Spearman at G12, 512 seeds (`join.py`):

| radius | `shore_area_within` vs `land_area_within` (same radius) |
|---|---|
| 1,000 m | **-0.034** (statistically independent) |
| 2,000 m | 0.749 |
| 5,000 m | 0.850 |

At the radius a builder cares about it is orthogonal to "how much land is near spawn". The count of
coast cells within 1 km is *negatively* correlated with land within 1 km (rho = -0.355): a solid
landmass has little edge. This is exactly the intent `coastal-builder` was reaching for, and
`coastline_length` could never express it because it was a whole-world number.

### It discriminates

G12, 512 seeds:

| metric | p10 | p50 | p90 | p90/p10 | CV |
|---|---|---|---|---|---|
| `shore_area_within(1000)` | 751,120 m2 | 927,650 m2 | 1,150,500 m2 | **1.53** | **0.166** |
| `shore_area_within(2000)` | 2,269,600 m2 | 2,877,600 m2 | 3,360,500 m2 | 1.48 | 0.147 |
| `shore_area_within(5000)` | 15,462,000 m2 | 17,042,000 m2 | 18,555,000 m2 | 1.20 | 0.069 |
| whole-world shore band (radius 10,500) | 69,145,000 m2 | 71,301,000 m2 | 73,265,000 m2 | 1.06 | 0.022 |

The last row is the warning: **at world scale the shore band is as undiscriminating as
coastline_length was** (CV 0.022). The intent only becomes measurable inside a disc. The metric must
therefore *require* `radius`, and the tool should refuse `radius >= 10500`.

### Its grid behaviour, measured

Ratio to the G12 value, and rho against the G12 answer, 512 seeds (`shore_an.py`, `join.py`):

| grid | ratio | rho | margin for ZERO false negatives at the q50/q75/q90 threshold | survivors |
|---|---|---|---|---|
| G12 | 1.000 | 1.000 | exact by definition | - |
| G24 | 0.939 | **0.995** | 7.4 % / 10.4 % / 6.6 % | 1.06x-1.53x of true |
| G48 | 0.793 | 0.978 | 24.4 % / 27.3 % / 23.1 % | 1.2x-2.0x |
| G96 | 0.724 | 0.953 | 33.4 % / 36.7 % / 36.3 % | 1.4x-4.1x |
| G164 and coarser | **0.000** | n/a | the grid is wider than the band; the metric is identically 0 | - |

(radius 2,000 m rows; radius 1,000 m is in `margin.txt` and needs 8.5-9.3 % at G24.)

So the policy writes itself:

- **G12: exact.** The definitional grid.
- **G24: screen only, margin 12 %** (the worst zero-FN margin measured was 10.4 %; 12 % gives headroom
  and still leaves 1.5x survivors at worst). Then re-measure at G12. Same shape as the bulk metrics.
- **G48 and coarser: refuse for a must-have.** The margin has to reach 25-37 % and the screen stops
  filtering.
- **grid > 50 m: hard refuse, always.** At G164 the band is narrower than one cell and the answer is
  structurally zero for every seed - a silent "no seed qualifies".

### Cost

Measured single-threaded, 8 seeds, on the busy machine described above:

```
per-seed ms: ctor(+pregen) 527.0   sample+measure G12 2818.6   EDT G12 116.4
```

The distance transform is **+3.5 % of a G12 height query** and 3.5 % of the 3,346 ms a seed already
costs. Work count: three linear passes over 4,194,304 cells (one classification, two O(1)-amortised
parabola sweeps), against 2,405,324 `GetBiomeHeight` calls the query already makes. It is free by
comparison; it needs nothing the T3 pass has not already computed.

### Rewritten preset and its predicted pass rate

```jsonc
{
  // Coastal builder - a buildable shoreline right where you spawn.
  //
  // MEASURED, 512 seeds (scratchpad\metrictruth\shore.csv, 2026-09-23): shore_area_within is the
  // area of land within 100 m of water inside the disc. At radius 1,000 m it is statistically
  // INDEPENDENT of land_area_within at the same radius (Spearman -0.034), so the two goals below
  // ask different questions. The old 'shoreline' goal used coastline_length, a fractal whose
  // whole-world value spans 8 % across the entire seed space and whose threshold of 400 km was
  // 4.75x below the minimum of 2,560 sampled seeds: it scored 1.0 for every seed and ranked nothing.
  "version": 1,
  "defs": 1,
  "name": "coastal-builder",
  "description": "Buildable land close to spawn with a lot of usable shore. Slope is NOT measured.",
  "world": { "gen_version": 2 },
  "search": { "order": "shuffled", "grid": 12, "screen": "auto", "screen_grid": 24, "keep": 100 },
  "goals": [
    { "id": "land-at-spawn", "target": "world:land_area_within", "metric": "land_area_within",
      "radius": 600, "test": "at_least", "value": 400000, "importance": "must" },
    { "id": "shoreline", "target": "world:shore_area_within", "metric": "shore_area_within",
      "radius": 1000, "test": "at_least", "value": 1000000, "importance": "must" },
    { "id": "meadows-at-spawn", "target": "biome:Meadows", "metric": "area_within",
      "radius": 600, "test": "at_least", "value": 300000, "importance": "nice", "weight": 2 }
  ]
}
```

Predicted pass rate, from the 512-seed sample at G12 (`join.py`, `margin.py`):

- `shore_area_within(1000) >= 1,000,000 m2` alone: **21.1 %** of seeds (measured at G24; at G12 the
  q90 threshold is 1,155,890 m2, so 1.0e6 sits near the 75th percentile - about 25 % at G12).
- `land_area_within(600) >= 400,000 m2` alone: 74 % (measured at radius 500, the nearest sampled
  radius; radius 600 will be a little looser).
- Because the two are independent at this radius, the joint must-have rate is approximately the
  product: **~16-19 % of seeds**. Against the old preset, whose two effective goals passed 74 % and
  100 %, this is the first version of `coastal-builder` that actually selects for shore.
- Unverified until run: the joint rate is a product-of-independents estimate, not a measurement of
  the compiled query. Run `vseed search --preset coastal-builder --estimate` before shipping the
  number in the docs.

## 3. The sweep - every metric in the catalogue

`EXACT` = the metric evaluates its own definition on the query grid and the number is that grid's
answer, with no claim about any other grid. `RELIABLE(m)` = a coarse screen plus margin `m` then an
exact re-measure loses nothing on the sample. `RANKING ONLY` = the value is grid-relative; only the
order survives. `VACUOUS` = the value barely varies across seeds, so a goal on it filters ~nothing.
`REMOVE/REDEFINE` = the number as presented is not the quantity its name claims.

| metric | verdict | evidence |
|---|---|---|
| `biome:present` | EXACT at any grid, but **VACUOUS** | all 9 biomes present in all 2,560 seeds at all 9 grids (`floors.py`). A goal on it never rejects. |
| `biome:area`, `biome:share` | RELIABLE(1 % at G24, 5 % at G96) | median rel. err vs G12 at G24: 0.02-0.11 %; rho >= 0.9997 (`sweep.txt`) |
| `biome:nearest_distance` | **REDEFINE / warn**. Genuine only for Mountain and Ocean | at G12 the value has 5 distinct values for BlackForest, 1 for AshLands, 2 for Plains/Mistlands; 595 for Mountain and 881 for Ocean (`floors.txt`). Meadows is the grid floor (8.485 m) in 93.2 % of seeds. For 7 of 9 biomes the number is the band boundary, not a seed property. |
| `biome:largest_patch_area` | RANKING ONLY below G24; FineOnly | median rel. err 0.17-0.30 % at G24 but 11.9-24.2 % at G96/G384; rho 0.98 -> 0.17-0.51 at G384 |
| `biome:area_within` | RELIABLE(1 % at G24). Two vacuous cases | "any biome within R" is pure geometry (identical for all 2,560 seeds); **Mistlands within 5 km is 0 for every seed** - proved from `BiomeGeometry.Band` (Mistlands starts at 5,900 m) |
| `biome:land_area` (T3) | presumed RELIABLE, **UNMEASURED** | not in the study's columns; it is a bulk count of two bulk conditions. Say "unmeasured" until it is sampled. |
| `biome:area_above_height` | RELIABLE(3 % at G48, 7 % at G96) - already FineOnly in `GridPolicy` | 0.34 % at G24 for "above 200 m", 25.3 % at G384 |
| `world:ocean_share`, `land_share`, `water_share`, `land_area`, `water_area` | RELIABLE(1 % at G24) | rel. err 0.02-0.03 % at G24; rho 1.000 |
| `world:land_area_within` | RELIABLE(1 % at G24) - **the best-behaved discriminating metric in the tool** | rho 1.000/1.000/0.998 at G24/G48/G96, CV 0.18 at R = 2 km. Caveat: needs `radius >= 8 x grid` - at G328 `land_area_within(500)` takes only 4 distinct values and its maximum equals its p90 (`dist.py`). |
| `world:island_count` | RANKING ONLY, and **NON-MONOTONE in the grid** | median count at min_area 1 ha: 137/126/119/119/121/120/112/**141**/128 across G12..G384 - it falls then rises. With `min_area: 0` it moves 77x (9,870 -> 128). rho 0.83 at G24. Already FineOnly. |
| `world:largest_island_area` | RANKING ONLY; FineOnly | rho 0.851 at G24, 0.051 at G384; "<= T" needs a 144 % margin at G24 |
| `world:spawn_island_area` | RANKING ONLY, weakest in the tool; FineOnly | rho 0.710 at G24, median rel. err 14.0 %; "<= T" margin 4,966,700 % at G24 |
| `world:nearest_land_distance` | **REDEFINE**: it is a quantised grid distance, not a metre distance | the reported value equals the grid's own floor (`spacing*sqrt(2)/2`) in **70.0 % of seeds at G12**, 96.6 % at G384, and takes only 2 distinct values at G384 (`floors.txt`). A bare "8.485 m" reads as a measurement; it means "the origin cell is land". |
| `world:coastline_length` | **REMOVE** | section 1 |
| `world:highest_peak` | RANKING ONLY in the ">=" direction; FineOnly | ">= median" loses 14.5 % at G24 and 95.9 % at G384; margin 5.5 % at G24, 51.6 % at G384; "<= T" under 1 % everywhere |
| `world:deepest_point` | **REMOVE** - it is not a seed property at all | at G12 it is **-399.859 m for all 2,560 seeds** (one distinct value); at G24 -396.308 for all; at G384 -100.87. It reports the world-edge height of the outermost sampled cell, which is fixed by the grid, and it moves by 4x across grids while never varying by seed. |
| `world:river_count`, `lake_count`, `stream_count` | **EXACT and GRID-INDEPENDENT** - proved from code | `WorldMeasurement.MeasureStructures` reads `gen.GetRivers()/GetStreams()/GetLakes()` (`WorldMeasurement.cs:336-341`); the sampling grid is not touched. Unmeasured across seeds, but the grid cannot affect them. |
| every `location:` / `group:` metric (`nearest_distance`, `all_candidates_distance`, `all_types_distance`, `count_within`, `count`, `types_within`) | **EXACT at every `--grid` setting** - proved from code | placement draws from the game's own hard-coded 2048 x 2048 @ 12 m point grid, `BiomeGrid.Size = 2048` / `PixelSize = 12f` (`src\SeedLab.Locations\BiomeGrid.cs:33,36`). `--grid` never reaches it. Already `GridSafety.GridIndependent` in `GridPolicy.For`. Their real uncertainty is not resolution but `m_unique` semantics, which `MetricDef.UniqueSemantics` already carries. |

### The defect class, named

Two distinct defects were found, and they need different fixes:

1. **Grid-relative quantity presented as a physical one.** `coastline_length` (a length that is a
   function of the ruler), `deepest_point` (the edge height of the grid), `nearest_land_distance` and
   `biome:nearest_distance` below ~1.5 cells (a quantised distance whose floor is the grid).
2. **Vacuous metric** - reliable but constant across seeds. `biome:present`; `biome:nearest_distance`
   for 7 of 9 biomes; all AshLands biome metrics (area, share, nearest, largest_patch are *identical*
   for all 2,560 seeds, because `IsAshlands` is applied before the ocean cut); `area_within` with no
   biome; `Mistlands area_within(5 km)`; and `coastline_length`, which is both.

The checker may **refuse** only on the ones proved from code or asset data - the AshLands constants,
Mistlands-within-5 km, `deepest_point`, the location metrics' grid-independence, the `Clamp01`
inertness of a threshold below the metric's floor. Everything sample-based may only **warn**.

## 4. Presentation rules

The standing requirement is that every number carries its resolution and uncertainty. Concretely:

1. **Every metric value in a results file carries its unit and the grid it was measured on.** The
   record already has `grid_m`, `screened_at_grid_m`, `verified_at_grid_m`
   (`RecordFormatter.cs:92,124,183-184`). Add, per goal, `"unit"` from `MetricDef.Unit` via
   `RecordFormatter.UnitName`, and `"measured_at_grid_m"` for that goal - a screen-then-verify run has
   goals decided at different grids and one whole-record `grid_m` hides that.
2. **A metric that is not comparable across grids says so on the record.** Add
   `MetricDef.GridComparable` (bool) and emit `"grid_comparable": false` plus a one-sentence
   `"grid_note"` for `island_count`, `largest_island_area`, `spawn_island_area`,
   `largest_patch_area`, `highest_peak`, `nearest_land_distance`, `biome:nearest_distance`. Text is
   the existing `GoalGridPolicy.Why` - it is already written and already measured.
3. **A number at or below the grid's resolution floor is printed as a bound, never as a value.**
   `nearest_land_distance` and `biome:nearest_distance`: when the value is within 1e-3 of
   `spacing*sqrt(2)/2`, emit `"value": 8.485, "censored": "<= 8.5 m (one grid cell at G12)"`. 70 % of
   seeds hit this at G12.
4. **Every value carries the measured error at the grid in use.** Add `MetricDef.MeasuredError` as a
   table of (grid -> median |relative error| vs G12, from `sweep.txt`) and emit
   `"measured_median_rel_err_at_grid"` whenever the goal was decided at a grid coarser than 12. It
   must say the sample it came from: `"error_source": "resolution-study 2026-09-23, 2,560 seeds"`.
5. **A vacuous goal is reported as vacuous, before the run.** `SearchPreflight` should refuse to start
   silently on a goal whose threshold is outside the metric's measured range - "this goal passed
   2,560 of 2,560 sampled seeds; it will not filter or rank anything". This is what would have caught
   the `coastline_length >= 400,000` goal on the day it was written. Refuse only where the inertness
   is provable (a `Clamp01` that saturates for every value the metric can take); warn otherwise.
6. **`shore_area_within` states its band in its own name text**, always: "land within 100 m of water,
   inside 1,000 m of the centre, measured at G12". The 100 m is not a parameter and must never look
   like one.
7. **The results file is read by someone who did not run it.** Every one of the above goes into the
   JSONL record, not only into the console, because the console scrolls away.

## 5. The patch plan

See the `patchPlan` list in the returned structured output; it is the same list, ordered so it can be
applied top to bottom without re-deciding anything.

## 6. Out of scope, but the user asked

The user's second question - whether a boss can ever have fewer than its `m_quantity` altars - is not
this task's, but the evidence already in `scratchpad\constraints\sample.jsonl` bears on it. Over 240
seeds, placed == m_quantity in **every** seed for Eikthyrnir (3), GDKing (4), Bonemass (5),
Dragonqueen (3), GoblinKing (4), FaderLocation (3), Vendor_BlackForest (10) and BogWitch_Camp (10);
`Hildir_camp` fell short in **1 of 240**. That is a measurement over 240 seeds, not a proof, and it
does not license a `count == m_quantity` shortcut in the checker: one counter-example already exists
in the sample.
