# The vseed criteria language

One file describes one search. It is **JSON** (`//` and `/* */` comments and trailing commas are
allowed), and **an unknown key is an error, never a warning** - a typo in `"importance"` must not
silently turn a must-have into nothing at all.

`vseed search --schema` prints this file. `vseed presets show <name>` prints a working example.

```json
{
  "version": 1,
  "defs": 1,
  "name": "my search",
  "description": "free text, carried into the report",

  "world":  { "gen_version": 2 },

  "search": {
    "order": "shuffled",
    "key": "0x5EEDF00D1234ABCD",
    "range": [-2147483648, 2147483647],
    "budget": { "seeds": 100000, "wall": "8h" },
    "keep": 1000,
    "grid": 12,
    "screen": "auto",
    "screen_grid": 24,
    "region": 2000,
    "approx": false,
    "threads": 16,
    "block_size": 256
  },

  "output": {
    "path": "results.jsonl",
    "explain": true,
    "rotate": "1GB",
    "compress": "gz",
    "reduce": "top:1000",
    "on_limit": "stop",
    "max_bytes": "10GB"
  },

  "goals": [
    { "id": "swamp-near", "target": "biome:Swamp", "metric": "nearest_distance",
      "test": "near", "value": 2500, "importance": "nice", "weight": 2 }
  ]
}
```

## Top level

| key | meaning |
|---|---|
| `version` | query-file format. This build reads **1**. |
| `defs` | metric-definition version (07-features.md section 2). This build implements **1**. A results file from a different `defs` is not comparable, so it is stamped on every record. |
| `name`, `description` | free text. |
| `world` | which generator to run. |
| `search` | how to walk the seed space. |
| `output` | where the hits go. |
| `goals` | at least one goal. |

## `world`

| key | meaning |
|---|---|
| `gen_version` | `0 \| 1 \| 2`, i.e. `World.m_worldGenVersion` -> `WorldGenerator.VersionSetup`. Every world created by Valheim 1.0.15 is **2**. It changes real geometry: v0 pushes the Mountain floor from 600 m to 1,100 m, v0/v1 extend the Swamp band from 6 km to 8 km. The static analysis reads it before it rejects anything. |

## `search`

| key | default | meaning |
|---|---|---|
| `order` | `shuffled` | `shuffled` walks the range through a 4-round Feistel permutation - every seed exactly once, no repeats, no gaps, and a partial run is an unbiased sample of the whole range. `sequential` is `from, from+1, ...`. |
| `key` | derived from the query hash | the Feistel key, as a number or a `"0x..."` string. Same key + same range = the same seed sequence = the same results. |
| `range` | the whole int32 space | `[from, to]`, inclusive. |
| `budget.seeds` | the whole range | stop after this many seeds. |
| `budget.wall` | none | stop after this long: `90s`, `45m`, `8h`, `2d`. **A wall-limited run is not reproducible on its own** - how many seeds fit depends on the machine - but its checkpoint records exactly how many blocks finished, so re-running with that `budget.seeds` is. |
| `keep` | 1000 | **a real cap on the results file.** Only the best `keep` records are ever written, so the disk cost is `keep x record size` however many seeds are scanned, and the run reports the true match count beside them ("top 1000 of 131,076"). Ties break on score then seed ascending, so a re-run is byte-identical. `"all"` streams every match instead - pair it with `output.rotate`, because a query with no must-have goal matches **100 %** of seeds and a whole-space run of one was measured at **7.36 TB**. |
| `grid` | 12 | the definitional sampling grid, metres. `12` is the game's own 2048x2048 grid. Anything coarser is a different measurement, not an approximation - it is printed with every number and stamped on every record. |
| `screen` | `auto` | `auto` \| `on` \| `off`. Screen-then-verify: a cheap coarse pass with a measured margin, then an exact re-measurement of every survivor at `grid`. Measured on 2,560 seeds: a **1 % margin at 24 m loses zero true matches** on every bulk-area goal tested, at 1.2x survivors. `auto` turns it on only for the goals it is safe for - a counting metric (biome area, share, `area_within`, land share). It never screens an island, spawn-island, island-count, shore, peak, `area_above_height` or nearest-distance goal, because **no margin at any grid is both safe and selective** for those. Every record written is measured at `grid`, and says which grid screened it. |
| `screen_grid` | 24 | the coarse grid for that screen. Never coarser than 96: at 164 m the margin that loses nothing is 10 %, at 384 m it is 30 %, and at that point 90 % of all seeds survive the screen and it has stopped filtering. |
| `region` | from the goals | measure inside this disc only, metres. **Exact**: a metric bounded by a disc cannot be changed by a cell outside it. Measured at the game's own 12 m grid, one biome goal costs **7.90 ms/seed** inside a 1 km disc against **314.40 ms/seed** over the whole world. A goal whose own definition is wider is still measured - inside the disc - and every record it touches is marked `bounded` with the radius, because that is a different number and must not be read as a whole-world one. |
| `approx` | false | would enable HEURISTIC prefilters. **This build ships none**, so it only stamps `approx` on the output. |
| `threads` | every logical core | workers. At `grid: 12` each worker holds 36 MB of buffers. |
| `block_size` | 256 | seeds per work block. Checkpoints land on block boundaries, so a smaller block means a finer resume point and slightly more overhead. |

## `output`

| key | meaning |
|---|---|
| `path` | `.jsonl` (one JSON object per line, the primary format), `.json` (a JSON array, valid at every flush), or `.csv` (a fixed column set). |
| `explain` | include the per-goal breakdown in the terminal report. |
| `rotate` | for `keep: "all"`: cut the stream into segments of this size (`"1GB"`, `"512MB"`, a byte count), named `results.0001.jsonl.gz`, `results.0002.jsonl.gz`, ... beside a `results.manifest.json` that lists every segment with its record count, seed range, sizes and SHA-256. **Refused with `.json`**: a split JSON array is not valid JSON. |
| `compress` | `"gz"` (the default when rotating) or `"none"`. Measured on this build: JSONL segments compress **10.0x**. |
| `reduce` | reduce a segment when it closes, keep the reduction and **delete the raw segment**: `"top:1000"`, `"count"`. Only reductions that merge exactly across segments are accepted, because the raw records are gone afterwards - a top-N of the union is the top-N of the per-segment top-Ns, and counts, sums, minima and maxima merge too. A median, a percentile or "most diverse N" is **refused by name**: it cannot be rebuilt from per-segment answers, and a wrong answer there would be silent. |
| `on_limit` | `"stop"` (the default: stop cleanly at the ceiling, flush, checkpoint and print the resume command) or `"evict"` (keep running and drop the lowest-scoring records; needs an explicit confirmation, and the final report says how many were dropped and the score at which dropping began). |
| `max_bytes` | the ceiling `on_limit` refers to. |

### What is on disk while a run is going

- The results file, capped by `keep`.
- One checkpoint, in the cache root (`%LOCALAPPDATA%\SeedLab\checkpoints\<query hash>.ckpt`) and **not** in the working directory, keyed by the query so two runs cannot overwrite each other's resume point. It is **deleted when the run completes**, along with the kept-set snapshot beside it.
- Nothing per seed, ever. A seed that fails is not written anywhere, so there is no per-seed cleanup to do and the tool does not pretend there is.

A killed run costs at most one checkpoint interval: the flush and the checkpoint are on a clock, not on "a block happened to be emitted just now". On resume the results file is truncated back to the byte count the checkpoint recorded as complete, which repairs the torn last record a hard kill leaves (measured: 978,944 bytes beyond the checkpoint, ending mid-token). A resumed run's file is then **byte-identical** to an uninterrupted one.

Both can be overridden from the command line (`--out`, `--seeds`, `--threads`, ...).

## A goal

```json
{ "id": "...", "target": "...", "metric": "...", "test": "...", "value": 0,
  "max": 0, "radius": 0, "height": 0, "min_area": 10000,
  "from": "center", "importance": "must", "weight": 1, "pad": 0.25 }
```

| key | meaning |
|---|---|
| `id` | required and unique. The report, the JSON and the CSV columns are named after it. |
| `target` | `"biome:<Name>"`, `"world:<anything>"`, `"location:<PrefabName>"` or `"group:<name>"`. The long form `{"kind":"biome","name":"Swamp"}` is identical. |
| `metric` | see the table below, or run `vseed search --metrics`. |
| `test` | `near` (<= value), `far` (>= value), `at_least` (>= value), `at_most` (<= value), `between` (value..max). `near`/`far` differ from `at_most`/`at_least` only in the shape of the nice-to-have score. |
| `value`, `max` | the threshold, in the metric's own unit: metres, m2, a fraction or a count. |
| `radius` | required by every `*_within` metric. |
| `height` | required by `area_above_height`. |
| `min_area` | the smallest component `island_count` counts, m2. Default 10,000 (1 ha). |
| `from` | `center` (the world origin, what both reference sites use) or `spawn` (the `StartTemple` point a location run places - **needs the dumped location table**, and is accepted only on a `location:` or `group:` metric; a biome or world metric is measured on the sampling grid, where every distance is from the origin, so `from: spawn` on one is refused rather than answered from the wrong point). |
| `importance` | `must` is a hard filter. `nice` contributes a weighted sub-score and never rejects. |
| `weight` | a `nice` goal's weight in the total score. Default 1. |
| `pad` | for `between`: how far outside the range the score falls to zero, as a fraction of the span. Default 0.25. |

## Scoring

Each `nice` goal produces a sub-score in [0,1]:

| test | sub-score |
|---|---|
| `near` (D) | `clamp01(1 - v/D)` |
| `far` (D) | `clamp01(v/D)` |
| `at_least` (X) | `clamp01(v/X)` |
| `at_most` (X) | `clamp01(1 - v/X)` |
| `between` (lo, hi) | 1 inside, falling linearly to 0 over `pad * (hi - lo)` |

`score = sum(w_i * s_i) / sum(w_i)`. A query with no `nice` goal scores every passing seed 1.0.
Results are ordered by **score descending, then seed ascending** - an explicit total order, so ties
never wobble between runs.

## Metrics available today

Everything in this table is measured from the generator alone and works now.

| target | metric | unit | tier |
|---|---|---|---|
| `biome:<B>` | `present` | count (0/1) | T2 |
| `biome:<B>` | `area` | m2 | T2 |
| `biome:<B>` | `share` | fraction | T2 |
| `biome:<B>` | `nearest_distance` | m | T2 |
| `biome:<B>` | `largest_patch_area` | m2 | T2 |
| `biome:<B>` | `area_within` (needs `radius`) | m2 | T2 |
| `biome:<B>` | `land_area` | m2 | T3 |
| `biome:<B>` | `area_above_height` (needs `height`) | m2 | T3 |
| `world:` | `ocean_share` | fraction | T2 |
| `world:` | `land_area`, `water_area`, `land_share`, `water_share` | m2 / fraction | T3 |
| `world:` | `land_area_within` (needs `radius`) | m2 | T3 |
| `world:` | `island_count` (uses `min_area`) | count | T3 |
| `world:` | `largest_island_area`, `spawn_island_area` | m2 | T3 |
| `world:` | `nearest_land_distance` | m | T3 |
| `world:` | `shore_area_within` (needs `radius`) | m2 | T3 |
| `world:` | `highest_peak` | m | T3 |
| `world:` | `area_above_height` (needs `height`) | m2 | T3 |
| `world:` | `river_count`, `lake_count`, `stream_count` | count | T4 |

`<B>` is one of `Meadows`, `Swamp`, `Mountain`, `BlackForest`, `Plains`, `AshLands`, `DeepNorth`,
`Ocean`, `Mistlands`.

## Location metrics (bosses, traders, dungeons, world features)

These place locations exactly as `ZoneSystem.GenerateLocationsTimeSliced` does, from the dumped
`ZoneSystem.m_locations` table. With no usable dump the search refuses to start with
`needs the dumped location table` rather than returning seeds it never tested.

| target | metric | unit | what it is |
|---|---|---|---|
| `location:<Prefab>` / `group:<name>` | `nearest_distance` | m | the nearest instance. `+infinity` when there is none, so a `far` goal passes on absence and a `near` goal fails |
| `location:<Prefab>` / `group:<name>` | `all_candidates_distance` | m | the FURTHEST instance, so `near D` means every one of them is inside D |
| `location:<Prefab>` / `group:<name>` | `all_types_distance` | m | the largest of the per-prefab nearest distances - how far out you must go to have reached EVERY named type |
| `location:<Prefab>` / `group:<name>` | `count_within` (needs `radius`) | count | instances inside the radius |
| `location:<Prefab>` / `group:<name>` | `count` | count | instances in the whole world |
| `location:<Prefab>` / `group:<name>` | `types_within` (needs `radius`) | count | how many DISTINCT prefabs of the target have an instance inside the radius |

`vseed search --metrics` prints the same table with the groups and their costs.

### m_unique types have no single position

`Vendor_BlackForest` (Haldor), `Hildir_camp` and `BogWitch_Camp` are `m_unique` with `m_quantity` 10.
The world is written with **ten candidates** for each, and the game keeps whichever one a player
generates the zone of first, deleting the rest (`ZoneSystem.RemoveUnplacedLocations`). That is
exploration order, not the seed, so **no offline tool can say where the trader is**. The metric name
is what picks the semantics, and `vseed explain` prints the one a goal used:

| metric | what it asserts about an m_unique type |
|---|---|
| `nearest_distance near D` | at least ONE candidate is within D. The survivor may be further. |
| `all_types_distance near D` | every named type has at least one candidate within D. |
| `all_candidates_distance near D` | EVERY candidate is within D - the only form that guarantees the survivor is. |
| `count`, `count_within` | candidates are counted individually (a trader contributes up to 10). |
| `types_within` | a type counts as present when any of its candidates is inside the radius. |

### What a location query costs

Each location type draws from its own RNG stream, seeded `worldSeed + m_prefab.Name.GetStableHashCode()`,
so nothing AFTER a type can change it. But zone occupancy is global (one location per zone) and the
AssetID, group and `CountNrOfLocation` buckets are shared, so nothing BEFORE it can be skipped. A
query therefore runs exactly `max(orderedIndex) + 1` of the 183 ordered entries - the `prefix` column
of `vseed search --metrics`. One boss is 2 entries; all seven are 17; bosses and traders are 22;
burial chambers are 67; the alt-biome tar pits are all 183.

The prefix only shortens the placement. The floor under every location query is the 2048^2
biome-and-height point grid that `GetRandomPointByBiomes` draws candidate zones from and that filters
10a/10b read sectors of: about 1.9 s of a seed on one thread, whatever the query asks for.

### Groups

`bosses`, `bosses_classic`, `boss_rooms`, `traders`, `burial_chambers`, `sunken_crypts`,
`troll_caves`, `frost_caves`, `infested_mines`, `dungeons`, `fuling_villages`,
`fuling_villages_with_alt`, `tar_pits`, `tar_pits_with_alt`, `surtling_geysers`,
`charred_fortresses`, `places_of_mystery`, `hildir_quests`, `runestones`.

### T0 knows the rings

Before any seed is built, the static analysis reads each type's `m_minDistance` / `m_maxDistance`,
`m_min/maxDistanceFromCenter`, `m_quantity` and biome mask and refuses a goal no seed can meet:
"all seven bosses within 6 km" (Fader is AshLands-only, which `IsAshlands` cannot place inside
7,900 m), "all three traders within 3 km" (Hildir and the Bog Witch both carry `m_minDistance` 3000),
"601 burial chambers" (three types of `m_quantity` 200).

## Definitions that travel with the numbers

- **land cell**: inside 10,500 m and `GetBiomeHeight(GetBiome(x,z), x, z) >= 30.0f`. 30 is the water
  level `Minimap.GetMaskColor` and `AltBiomeWorldData.tryFill` hard-code.
- **island**: a **4-connected** component of land cells. Never 8-connected - a diagonal contact would
  bridge two islands across a one-cell channel.
- **island area** = `cells * grid^2`; **island count** = components of at least `min_area`.
- **spawn island**: the land component whose nearest cell centre is closest to (0,0), reported as
  0 when the nearest land is more than 500 m away. This is a grid metric and stays centre-based on
  purpose: the real rule, "the component holding `StartTemple`", would drag the whole T3 height pass
  behind a T5 placement. Measured over 384 seeds, `StartTemple` sits a median of 65 m from the
  origin and at most 538 m, so on this grid the two rules pick the same component in all but a
  handful of worlds - use `from: spawn` on a location goal when you need the temple itself.
- **shore area within R** = the area of land (height >= 30 m) whose cell centre lies within
  **100 m** of the centre of a water cell, inside a disc of `radius` around the world centre. The
  100 m band is part of the definition and is **never a parameter**; the printed name always says
  so ("land within 100 m of water, inside 1,000 m of the centre, measured at G12"). `radius` is
  required, and must be below 10,500 m: measured over 512 seeds the whole-world value has a CV of
  0.022, the same non-discrimination that retired `coastline_length`, while inside 1 km its CV is
  0.166 and it is statistically independent of `land_area_within` at the same radius
  (Spearman -0.034). Computed by an exact squared-Euclidean distance transform over the height field
  the T3 pass already builds: +3.5 % of a G12 height query.
- **RETIRED metrics.** `world:coastline_length` and `world:deepest_point` were removed on
  2026-09-23. A query that names one is refused *by name*, with the measurement that removed it and
  the replacement - never with "unknown metric". `coastline_length` spanned only 8 % across 2,560
  seeds (p90/p10 = 1.065), so a threshold on it was not a criterion; use `shore_area_within` with a
  radius. `deepest_point` was -399.859 m for **all** 2,560 seeds at G12 and -100.87 m at G384: it
  reported the world-edge height of the outermost sampled cell, which is fixed by the grid, so
  there is nothing to replace.
- All distances are from the world centre by `sqrt(x^2 + z^2)`, matching valheim.gaming.tools.
