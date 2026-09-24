# SeedLab search: where the time actually goes, and how to get it back

Measured 2026-09-23 on DoomMachine's machine: **AMD Ryzen 7 9800X3D**, 8 physical cores / 16 logical,
96 MiB L3, 64 GB, Windows 10 19045, .NET 10.0.12. Everything below is a number I watched being
produced; where I could not measure something I say so.

**This is an analysis. Nothing under `_ModSource\SeedLab` was modified.** The port was copied to
`…\scratchpad\searchanalysis\work*\src\` and instrumented there; four trees were built:

| tree | what it is |
|---|---|
| `work\` | the port as shipped + a profiler (`Prof`) — clean timings |
| `workc\` | the same with `-p:DefineConstants=COUNTERS` — exact call counts |
| `worko\` | the port + six candidate optimisations behind runtime switches |
| `workoc\` | `worko` with COUNTERS — exact "work removed" counts |

Raw output is beside this file as `perf-*.txt`; the patch scripts that produced the trees are
`patch_opt.py`, `patch_simd.py`, `patch_o6.py`, `patch_optcounts.py`, `patch_simdbench.py`,
`patch_final.py`, `patch_scale.py`.

## Measurement conditions — read this before trusting a wall-clock number

Two other agents were benchmarking on this machine for the first half of the session: three `vseed`
processes and ~19 `dotnet` processes, `\Processor(_Total)\% Processor Time` pinned at **100 %** until
**05:29**, then ~14 % afterwards. Every table says which regime it came from. I used three defences:

1. **Exact operation counts** (Perlin calls, `Atan2` calls, `sqrt` calls, river points scanned).
   A call count does not move when another process is using the machine. These are the evidence;
   wall-clock is the corroboration.
2. **Paired A/B inside one process** — baseline and optimised run back-to-back on the same seed, and
   the statistic is the median of per-pair ratios, so a load spike hits both halves of the pair.
3. **Burst sampling** — many short measurements, keep the minimum, which is the sample least likely to
   have been interrupted.

Cross-check that the model is right: the shipped `vseed bench` (run at 05:29) reports
`GetBiome 1.91 M/s`, `GetBiomeHeight 1.12 M/s`, `construct + pregenerate 340.7 ms/world`; my
independent harness got 361.7 ns (= 2.77 M/s under a different loop shape), 597.4 ns and 273.6 ms.
And `vseed search` end-to-end on the quiet machine gives **11,236 seeds/s** at the 384 m biome tier
against the 10,717 seeds/s in the brief — the baselines in the brief reproduce.

---

# 1. Per-stage cost model

## 1.1 The whole ladder, one thread, quiet machine

`Prof grid --spacings 384,164,109.375,48,12 --reps 5` → `perf-grid-quiet.txt`

| grid | N | cells sampled | ctor (deferred) | biome pass | **pre-generation** | height pass | metrics (incl. islands) |
|---|---|---|---|---|---|---|---|
| 384 m | 56 | 2,348 | 0.0001 ms | 1.07 ms | **273.6 ms** | 1.79 ms | 0.07 ms |
| 164 m | 130 | 12,892 | 0.0001 ms | 4.52 ms | **270.8 ms** | 8.61 ms | 0.22 ms |
| 109.4 m | 192 | 28,968 | 0.0001 ms | 8.65 ms | **267.3 ms** | 18.22 ms | 0.39 ms |
| 48 m | 438 | 150,364 | 0.0001 ms | 29.84 ms | **269.0 ms** | 78.71 ms | 1.48 ms |
| 12 m (G12) | 2048 | 2,405,324 | 0.0001 ms | 366.5 ms | **270.4 ms** | 993.3 ms | 20.52 ms |

The constructor is **98.8 ns** — the seven RNG draws and three empty collections. It is not a cost.
It is 0.00004 % of a height-tier seed. Any plan block that lists "construct" as a line item is
listing noise.

**Share of each tier** (one thread, quiet):

| tier | what it runs | per-seed | pre-generation's share |
|---|---|---|---|
| T2 biome @384 m | ctor + biome pass + metrics | 1.14 ms | 0 % (skipped) |
| T2 biome @G12 | ctor + biome pass + metrics | 387.0 ms | 0 % (skipped) |
| T3 height @384 m | + pregen + height pass | 276.5 ms | **99.0 %** |
| T3 height @G12 | + pregen + height pass | 1,650.7 ms | **16.4 %** |
| T5 location | + 2048² grid + sectors + placement | 2.4–9.0 s | 3–11 % |

This is the single most important number in the report and it is **not** what the tool currently
prints (see §7, defect D1): at the 384 m grid a height-tier seed is *99 % lake/river/stream
pre-generation*. The 28.6 seeds/s figure in the brief is, to within 1 %, the cost of pre-generation
alone. The grid resolution is almost free at that tier; the world construction is everything.

At G12 the balance flips — sampling 2.4 M cells costs 1.36 s and pre-generation is only 16 %.

## 1.2 Inside pre-generation

Timing: `Prof stages --reps 7` → `perf-stages.txt` (taken during the 100 %-load window, so treat the
absolute milliseconds as ~1.5× inflated and read the **shares**).
Work attribution: `Prof stagework` → `perf-stagework.txt` (exact, load-free).

| stage | Perlin calls / seed | share of Perlin | min ms (contended) | share of time |
|---|---|---|---|---|
| `FindLakes` scan (157×157 `GetBaseHeight`) | 153,400 | 2.7 % | 5.9 | 1.5 % |
| `MergePoints` (O(n²) `FindClosest`) | **0** | 0 % | **74.6** | **19.2 %** |
| `PlaceRivers` incl. `RenderRivers` | 37,544 – 53,976 | 0.7–0.9 % | 10.7 (render 9.0) | 2.8 % |
| `PlaceStreams` pass 1 (not Deep North) | 2,413,724 – 2,757,663 | **43.6 %** | 149.3 | **38.4 %** |
| `PlaceStreams` pass 2 (Deep North) | 2,878,472 – 3,218,526 | **53.1 %** | 148.1 | **38.1 %** |
| river/stream point rendering | **0** | 0 % | 23.3 (of the above) | 6 % |
| **total** | **5.49 M – 6.17 M** | | 428.8 | |

Two facts that change what you would optimise:

* **The two `PlaceStreams` passes are 96.7 % of all the Perlin work in a world and ~77 % of its time.**
  Each pass runs 3,000 iterations of (up to 100 start probes + up to 100 end probes), every probe a
  full `GetPregenerationHeight` = `GetBiome` then `GetBiomeHeight`. Measured: **267,198 `GetBiome`
  calls and 267,198 `GetBiomeHeight` calls per seed**, against 2,348 for a whole 384 m biome pass.
  Pre-generation costs **259× the Perlin work of a 384 m biome pass** (5,726,248 vs 22,126).
* **`MergePoints` is 19 % of the time and does no noise work at all.** It is the classic
  quadratic merge: `FindClosest` scans the whole remaining candidate list, once per merge, and there
  are a few thousand lake candidates. 74.6 ms of pure `Vector2.Distance`.

Both are addressable (§3, O2/O4/O5/O6).

## 1.3 The location tier

`Prof locgrid`, `Prof locmem`, and `vseed locations` (the prebuilt 04:37 binary in `searchanalysis\bin\`).

| stage | 1 thread | 16 threads |
|---|---|---|
| ctor + pre-generation | 273 ms | — (serial per seed) |
| `BiomeGrid.Build` 2048² (4,194,304 points) | 1,503–2,157 ms | 370 ms (5.8× — intra-seed `Parallel.For` over rows) |
| `BiomeField.Build` (sector flood fill, 938–1,017 sectors) | 122 ms | serial |
| `AltBiomeAssignment` + placement, boss+trader prefix | ~0.4 s | serial |
| `AltBiomeAssignment` + placement, all 183 types | ~6.5 s | serial |

End to end through the shipped CLI, one seed, seed 75539276:

```
vseed locations 75539276 --type boss        --threads 1   2.46 s      --threads 16   1.17 s
vseed locations 75539276 --type boss,trader --threads 1   2.71 s
vseed locations 75539276 --type dungeon     --threads 1   7.47 s
vseed locations 75539276 --type all         --threads 1   8.96 s      --threads 16   7.84 s
```

(includes ~0.25 s of process start and data load). That confirms the brief's provisional
~1 s/seed/thread for the boss+trader prefix and ~7 s for all types.

**Memory per location-tier worker, measured** (`Prof locmem` → `perf-locmem.txt`):

```
generator + river points          12,555,520 B =  12.0 MiB
BiomeGrid 2048^2 (byte + float)   20,975,856 B =  20.0 MiB
BiomeField sectors + point lists  49,404,344 B =  47.1 MiB
TOTAL                             82,935,720 B =  79.1 MiB     (L3 on this CPU is 96 MiB)
```

So the brief's "~60–70 MB" is an under-estimate: it is **79 MiB live per world**, and `BiomeField`'s
point lists — not the grid — are the biggest part. **One worker alone nearly fills the 96 MiB L3.**
Two workers cannot both fit. See §4.

## 1.4 Per-seed allocation — the hidden cost of the height and location tiers

`Prof alloc` → `perf-alloc.txt`, `GC.GetAllocatedBytesForCurrentThread`:

| stage | bytes allocated per seed |
|---|---|
| deferred constructor | **456 B** |
| pre-generation | **49.8 – 60.4 MB** |
| biome pass + height pass + metrics at 384 m | **0 B** (the worker's buffers are reused — this part is already right) |

The 50–60 MB is the river-point store: 21,827–26,218 `Dictionary<Vec2i, RiverPoint[]>` entries holding
644,031–764,577 points (28.4–29.5 points per cell), built through per-cell `List<RiverPoint>` and then
`ToArray()`, with `RenderRivers` re-allocating a *new* array for every cell that a later pass touches.
Only 12.0 MiB of it survives; the rest is garbage.

At 42 seeds/s (16 threads, optimised) that is **2.4 GB/s of allocation**, and it is the reason the
height tier scales worse than the biome tier (§4).

---

# 2. Instruction-level: what a `GetBiome` and a `GetHeight` are made of

Exact call counts, `Prof counts` on the COUNTERS build → `perf-counts.txt`.
Averaged over the 2,348 cells of the 384 m grid inside the 10,500 m disc, seed −1772362158.

## 2.1 `GetBiome` — 9.42 Perlin, 2.60 Atan2+Sin, 3.00 sqrt

| resulting biome | n | Perlin | WorldAngle (Atan2+Sin) | `DUtils.Length` |
|---|---|---|---|---|
| **ALL** | 2,348 | **9.423** | **2.599** | **3.000** |
| Meadows | 74 | 12 | 3 | 3 |
| BlackForest | 311 | 12 | 3 | 3 |
| Plains | 305 | 11 | 3 | 3 |
| Mistlands | 410 | 10 | 3 | 3 |
| Swamp | 67 | 9 | 3 | 3 |
| Mountain | 81 | 8 | 3 | 3 |
| AshLands | 290 | 8 | 2 | 3 |
| DeepNorth | 158 | 8 | 3 | 3 |
| Ocean | 652 | 8 | 2 | 3 |

`GetBaseHeight` is **exactly 8 Perlin and 1 `Length`**, always. Everything above 8 is the 1–4 mask
noises of the Swamp/Mistlands/Plains/BlackForest tests, short-circuited in that order.

**The 2.6 `WorldAngle` calls are all the same call.** `GetBiome` evaluates `WorldAngle(wx, wy)` for
`a`, `IsAshlands` evaluates `WorldAngle(x, y)` again, and `IsDeepnorth` a third time — identical
arguments to a pure static function (`WorldGeneratorPort.cs:997`, `:1009`, `:1022`). That is 1.6
wasted `Atan2`+`Sin` pairs per cell, and `WorldAngle` costs **31.05 ns** (Atan2 11.57 + Sin 15.04).

## 2.2 `GetBiomeHeight` — 13.32 Perlin, and Mountain is 6× the rest

| biome | n | Perlin | Atan2 | sqrt | FastNoise cellular | simplex | river points scanned |
|---|---|---|---|---|---|---|---|
| **ALL** | 2,348 | **13.316** | **2.124** | **4.109** | **0.988** | **0.124** | **5.60** |
| Mountain | 81 | **47** | 2 | 8 | 0 | 0 | 0.52 |
| DeepNorth | 158 | 17 | 2 | 4 | 0 | 0 | 2.72 |
| Mistlands | 410 | 15 | 2 | 4 | 0 | 0 | 9.35 |
| Meadows / BlackForest / Plains | 690 | 14 | 2 | 4 | 0 | 0 | 6.0 / 10.8 / 11.5 |
| AshLands | 290 | 12 | 3 | 4 | **8** | **1** | 0 |
| Ocean | 652 | 8 | 2 | 4 | 0 | 0 | 0 |
| Swamp | 67 | 4 | 2 | 3 | 0 | 0 | 23.12 |

Mountain's 47 Perlin is `BaseHeightTilt`'s four extra `GetBaseHeight` evaluations (4 × 8 = 32) plus
the base 8 plus 7 detail octaves — `WorldGeneratorPort.cs:1161-1169`. Measured cost by biome
(`Prof micro2`, quiet, min of 600 bursts):

```
Swamp        126.76 ns      DeepNorth   244.63 ns
Ocean        185.94 ns      Mistlands   321.97 ns
Meadows      214.45 ns      AshLands    446.88 ns   (8 cellular + 1 simplex, 18.1/18.4 ns each)
BlackForest  241.31 ns      Mountain    595.61 ns
Plains       241.50 ns
```

The `Atan2` count of 2.124 is `CreateAshlandsGap` and `CreateDeepNorthGap` — **again the same
`WorldAngle(wx, wy)` twice** (`:1035`, `:1047`).

The single-entry river cache works: 2,348 height samples produce only **0.60 river-weight lookups per
call** and scan **5.60 river points** each. It is not a hot spot at a coarse grid. At G12, consecutive
samples share a 64 m river cell almost always.

## 2.3 The primitives (1 thread, quiet, min of 600 short bursts) — `perf-micro2-quiet.txt`

| primitive | ns |
|---|---|
| `Mathf.PerlinNoise` (standalone loop) | 45.12 |
| `Mathf.PerlinNoise` (in situ inside `GetBaseHeight`: 272.75 / 8, minus the sqrt and the float maths) | **≈ 32–34** |
| `DUtils.Length` | 2.08 |
| `Math.Sqrt` | 1.90 |
| `Math.Atan2` | 11.57 |
| `Math.Sin` | 15.04 |
| `WorldGeneratorPort.WorldAngle` | 31.05 |
| `IsAshlands` | 21.29 |
| `FastNoise.GetCellular` | 18.07 |
| `FastNoise.GetSimplexFractal` | 18.36 |
| `GetBaseHeight` | **272.75** |
| `GetBiome` (384 m mix) | **361.72** |
| `GetBiomeHeight` (384 m mix) | **597.36** |

## 2.4 The answer to "where does the time really go"

Rather than divide estimated costs, I measured it: making **only** the eight `GetBaseHeight` Perlin
samples 5.7× cheaper (§3, O5) speeds the whole 384 m biome tier by **2.93×**. Solving
`T/(P/5.71 + (T−P)) = 2.93` gives **P/T = 79 %**.

> **79 % of a 384 m biome-tier seed is the eight `Mathf.PerlinNoise` calls inside `GetBaseHeight`.**
> Roughly 9 % is the redundant `WorldAngle` evaluations. The distance maths, the FastNoise Ashlands
> path, the grid bookkeeping and the metric accumulation together are the rest.

For the height/pre-generation tier the same eight calls dominate through `GetPregenerationHeight`:
5.7 M Perlin per seed at ~32 ns in situ is ~183 ms of the 273 ms pre-generation, i.e. ~67 %; the
quadratic `MergePoints` is ~19 %; everything else is ~14 %.

Memory traffic is **not** the limit at one thread (the Perlin permutation table is 2 KiB, the 384 m
grid buffers are 30 KiB). It becomes the limit at high thread counts and on the big grids — see §4.

---

# 3. Optimisations

Every one of these was **implemented and run**, and bit-exactness was **proved, not argued**, before
any speed was measured. The exactness harness (`Prof verify`, `Prof simd`, `Prof pairedloc`,
`Prof deepnorth`) rebuilds whole worlds with and without each switch and compares, as IEEE-754 bit
patterns: the lake list, every river and stream field, every entry of the river-point dictionary
(keys, lengths, and each point's `p.x`, `p.y`, `w`, `w2`), and a 192×192 grid of `GetBiome` and
`GetBiomeHeight`.

```
perf-verify.txt
  O3 scalar byte-table Perlin : 200000 / 200000 bit-identical
  O5 AVX2 8-wide Perlin       : 200000 / 200000 bit-identical
  O1 WorldAngle once      17,913,710 values compared, 0 differ  OK
  O2 GetBaseHeight memo   17,913,710 values compared, 0 differ  OK
  O3 fast Perlin          17,913,710 values compared, 0 differ  OK
  O4 grid MergePoints     17,913,710 values compared, 0 differ  OK
  O1+O2+O3                17,913,710 values compared, 0 differ  OK
  O1+O2+O3+O4 (all)       17,913,710 values compared, 0 differ  OK
perf-simd.txt
  GetBaseHeight vs GetBaseHeightSimd : 720000 / 720000 bit-identical  OK
  O1+O2+O3+O4+O5 whole worlds        : 12,099,093 values, 0 differ  OK
perf-pairedloc.txt
  2048^2 grid under all flags: 25,165,824 values, 0 differ  OK
```

## 3.1 Ranked

Rank = (measured gain × confidence) / exactness risk. "Safe by construction" means the change cannot
produce a different float: it removes a duplicate evaluation of a pure function, changes only load
widths and control flow, or evaluates the same IEEE-754 single-precision operations in the same order
on a wider register.

| # | optimisation | measured gain (1 thread) | exactness | risk |
|---|---|---|---|---|
| **1** | **O5 — batch `GetBaseHeight`'s eight Perlin samples 8-wide (AVX2)** | `GetBaseHeight` **5.71–6.29×**; 384 m biome tier **2.85–2.94×**; G12 biome tier **1.66×**; 2048² location grid contribution 1.27× | 720,000/720,000 + 12.1 M whole-world values | **safe by construction**, but the largest new code — re-run the acceptance suite |
| **2** | **O4 — spatial-grid `MergePoints`** | pre-generation **1.247×** | 17.9 M values, 0 differ | *needs re-validating*: reproduces "min by (distance, then lowest live index)" over the same candidate set; the tie-break is load-bearing because lake candidates sit on a 128 m lattice |
| **3** | **O2 — one-entry memo on `GetBaseHeight`** | pre-generation **1.098×**; 2048² location grid **1.229×**; exact work removed: **Perlin −28.9 %**, `GetBaseHeight` calls −40.4 % (509,474 → 303,406), 206,068 memo hits/seed | 17.9 M values, 0 differ | **safe by construction** — pure function, per-handle state exactly like the existing river cache |
| **4** | **O6 — skip the second (Deep North) stream pass for a bounded query** | pre-generation **1.558×** | inside 7,648 m: 76,780 heights, **0 differ**; whole world: 44 of 144,840 differ (so the test is sensitive) | *sound only under a proven radius bound* — must be gated on the query's region, never global |
| **5** | **O7 — radial-band early reject for single-biome goals** | Swamp-only pass **4.20×** (426.15 → 101.58 ns/cell), same 67 cells found | exact; the bands already exist and are already trusted, in `Feasibility/BiomeGeometry.Band` | **safe by construction** — reuses shipped, version-aware code |
| **6** | **O3 — bounds-check-free byte-table Perlin** | raw Perlin 44.38 → 36.38 ns (**1.22×**); 384 m biome tier +12 % over O1 | 200,000/200,000 + 17.9 M | **safe by construction** — identical arithmetic, `byte[512]` instead of `int[512]`, no bounds checks. Subsumed by O5 on the paths O5 covers |
| **7** | **O1 — evaluate `WorldAngle` once per `GetBiome`** | pre-generation 1.012×; 384 m biome tier **1.041–1.068×**; exact work removed: **`Atan2` −62.2 %** in pre-generation, −61.4 % in the biome pass | 17.9 M values, 0 differ | **safe by construction** |
| **8** | O1b — the same for `GetBiomeHeight`'s two gap functions | not implemented; bounded at ≤ 5 % of the height pass (4,986 `Atan2` × 31 ns = 155 µs of a 1.79 ms pass, halved) | same argument as O1 | safe by construction |
| **9** | flatten the river-point store (one `RiverPoint[]` + offsets instead of 24,000 small arrays) | not implemented; bounded by the 50–60 MB/seed allocation it removes, which is what caps the height tier at 16 threads (§4) | order-preserving, so `GetWeight`'s float sum is unchanged — but that *is* the risk | *needs re-validating* |
| **10** | don't allocate `SeedSampler.Height` for a biome-only query | not implemented; 16 MiB per worker at G12, 256 MiB at 16 threads | trivially exact | safe by construction |

Combined, measured, single thread (`perf-simd.txt`, `perf-simd-g12.txt`, `perf-pairedloc.txt`):

| tier | baseline | O1+O2+O3+O4+O5 | ratio |
|---|---|---|---|
| biome @384 m | 1.09 ms | 0.37 ms | **2.93×** |
| pre-gen + height @384 m | 287.4 ms | 139.7 ms | **2.08×** |
| biome @G12 | 398.7 ms | 228.3 ms | **1.75×** |
| pre-gen + height @G12 | 1,700.4 ms | 1,182.6 ms | **1.44×** |
| `BiomeGrid.Build` 2048² (location tier) | 1,531.9 ms | 971.4 ms | **1.57×** |
| pre-generation with O1+O2+O4 only | 287.5 ms | 199.0 ms | **1.43–1.48×** |

## 3.2 The honest caveat — the gain shrinks at 16 threads

This is the finding I would most want the user to see, because it is the one an optimistic report
would omit. Running the same optimised code across workers:

`Prof scale --spacing 384 --seeds 4800 --reps 3 --threads 8,16 [--opt]`

| tier | threads | baseline seeds/s | optimised seeds/s | ratio | CPU ms/seed base → opt |
|---|---|---|---|---|---|
| biome @384 m | 8 | 6,438 | **13,196** | **2.05×** | 1.21 → 0.57 |
| biome @384 m | 16 | 11,459 | **15,254** | **1.33×** | 1.34 → 1.02 |
| height @384 m | 16 | 31.6 | **42.0** | **1.33×** | 440 → 314 |

Once the arithmetic is ~3× cheaper the workload stops being arithmetic-bound. At 8 threads (one per
physical core) the full 2.05× lands. At 16 the two SMT siblings on a core are competing for the same
AVX2 issue ports and gather units, and the optimised code goes from 0.57 to 1.02 CPU-ms per seed —
SMT stops paying. The end-to-end win at full thread count is **1.33×**, not 2.93×.

That is still worth having, and it tells you the next thing to fix is memory and allocation (rank 9
and 10 above), not more arithmetic.

## 3.3 Two things I checked and would *not* do

* **SIMD across whole grid rows.** Tempting, but `GetBiome` short-circuits: the Swamp/Mistlands/Plains/
  BlackForest mask noises are only evaluated when the earlier tests fall through. Evaluating all four
  for every lane costs 4 samples where the scalar path averages 1.4. Measured on the counts: 9,392
  eager mask samples at the 8-wide rate ≈ 162 µs vs 3,344 lazy ones at the scalar rate ≈ 148 µs — no
  gain. Batch `GetBaseHeight` (always exactly 8, always unconditional); leave the masks scalar.
* **Caching `GetBiome` across seeds.** Nothing is shared between seeds: the seven offsets change
  everything. The only reusable object is the `int[512]`/`byte[512]` permutation table, which is
  already static.

---

# 4. Threading and system load

All four curves below are from the quiet machine (~14 % background). `seeds/s` is wall clock;
`CPU ms/seed` is `Process.TotalProcessorTime` divided by seeds, which **does not fall when the OS
deschedules us**, so it separates "we lost CPU share" from "each seed genuinely got more expensive".

## 4.1 Biome tier @384 m — working set ~30 KiB, compute-bound

`perf-scale-biome384-quiet.txt`, `perf-scale-hi.txt`

Sweep, 1,200 seeds per point, 5 interleaved reps (`perf-scale-biome384-quiet.txt`):

| threads | 1 | 2 | 3 | 4 | 6 | 8 | 10 | 12 | 14 | 16 |
|---|---|---|---|---|---|---|---|---|---|---|
| seeds/s | 901 | 1,764 | 2,597 | 3,390 | 4,941 | 6,236 | 7,710 | 9,034 | 9,957 | 7,315 |
| efficiency vs 1 thread | 100 % | 98 % | 96 % | 94 % | 91 % | 87 % | 86 % | 84 % | 79 % | 51 % |
| CPU ms/seed | 1.11 | 1.12 | 1.16 | 1.13 | 1.18 | 1.24 | 1.16 | 1.20 | 1.27 | 1.25 |

The 16-thread figure there is an **artefact of too little work per measurement** (1,200 seeds ÷ 16 =
75 seeds per thread, so thread start-up is visible). Re-run with 4,800 seeds per point
(`perf-scale-hi.txt`):

| threads | 8 | 12 | 14 | **16** |
|---|---|---|---|---|
| seeds/s | 6,817 | 9,690 | 11,158 | **12,088** |
| efficiency vs 1 thread (901 seeds/s) | 95 % | 90 % | 88 % | **84 %** |
| CPU ms/seed | 1.14 | 1.21 | 1.23 | **1.26** |

`vseed search` itself agrees that 16 beats 14: 11,236 vs 10,439 seeds/s on the same query.

No knee. CPU-per-seed rises only 14 % from 1 to 16 threads: SMT is nearly free here. **16 is the
throughput optimum for this tier.**

## 4.2 Biome tier @G12 — 24 MiB of buffers per worker, memory-bound

`perf-scale-g12.txt`

| threads | seeds/s | efficiency | CPU ms/seed |
|---|---|---|---|
| 1 | 2.6 | 100 % | 385 |
| 4 | 9.5 | 92 % | 412 |
| 8 | 16.3 | **78 %** | 475 |
| 12 | 20.2 | 65 % | 581 |
| 16 | 22.4 | 54 % | **661 (+72 %)** |

## 4.3 Height / pre-generation tier @384 m — 50–60 MB allocated per seed

`perf-scale-height384.txt`

| threads | seeds/s | efficiency | CPU ms/seed |
|---|---|---|---|
| 1 | 3.5 | 100 % | 304 |
| 4 | 12.4 | 88 % | 328 |
| 8 | 21.4 | **76 %** | 352 |
| 12 | 27.4 | 65 % | 395 |
| 16 | 32.0 | 57 % | **418 (+37 %)** |

## 4.4 Location tier — 79.1 MiB **live** per worker against a 96 MiB L3

`perf-locscale-quiet.txt` (one whole 2048² grid + `BiomeField` per seed per worker)

| workers | seeds/s | speedup | efficiency | CPU s/seed | live footprint |
|---|---|---|---|---|---|
| 1 | 0.582 | 1.00 | 100 % | 1.73 | 79 MiB |
| 2 | 1.083 | 1.86 | 93 % | 1.80 | 158 MiB |
| 4 | 2.127 | 3.66 | 91 % | 1.84 | 316 MiB |
| 6 | 3.002 | 5.16 | 86 % | 1.93 | 475 MiB |
| 8 | 3.646 | 6.27 | **78 %** | 2.09 | 633 MiB |
| 12 | 4.831 | 8.31 | 69 % | 2.30 | 949 MiB |
| 14 | 5.360 | 9.21 | 66 % | 2.46 | 1.08 GiB |
| 16 | **5.374** | 9.24 | 58 % | **2.49 (+44 %)** | 1.24 GiB |

**Direct answer to the brief's question — "does it fall out of cache, and does a lower worker count
actually run faster?"** It falls out of cache immediately: one worker's 79 MiB already nearly fills
the 96 MiB L3, so even a single location-tier worker is streaming from DRAM, and CPU-work per seed
rises 44 % from 1 to 16 workers. But **no, a lower worker count does not run faster** — throughput
rises monotonically to 14 and then flattens (5.360 → 5.374, +0.3 % for two more threads). Lower counts
are more *efficient*, not faster. **14 is the right cap for this tier; 16 buys nothing.**

## 4.5 Recommended defaults

| situation | recommendation | why |
|---|---|---|
| **(a) dedicated run, biome/coast tiers** | `Environment.ProcessorCount` (**16**) | 84 % efficiency, CPU/seed +14 %, measured optimum |
| **(a) dedicated run, height / G12 / location tiers** | `ProcessorCount − 2` (**14**) | 16 gives +0.3 % on the location tier and +11 % on G12 for +33 % threads and +14 % CPU work; 14 leaves the machine responsive |
| **(b) while playing Valheim** | **4** workers, and offer `--nice` | Valheim wants 4–6 threads for its own job system; 4 SeedLab workers still deliver 3,390 seeds/s on the biome tier (28 % of the 16-thread rate) at 94 % efficiency, and never contend for more than half the physical cores |
| **(b) using the machine for other things** | `ProcessorCount − 2` for cheap tiers, **6** for memory-heavy ones | 6 workers on the location tier is 86 % efficient and leaves >60 GiB and ~10 logical cores free |

**A concrete `--threads` policy:**

```
--threads auto        (default) = per-tier table above, from the compiled query's top tier
--threads N           exact
--threads -N          ProcessorCount - N
--threads N%          percentage of ProcessorCount, rounded down, min 1
--nice                auto/2, plus BELOW_NORMAL process priority and a 50 ms sleep
                      every block; costs ~5 % of the rate of the same worker count
```

The web UI already uses `ProcessorCount − 2` with a hand-written justification
(`EngineSearchEngine.cs:45-51`: *"about 12 % of the rate on this 16-thread machine"*). That is now
measured and correct for the tiers it can run: 14 threads is 92 % of 16 threads' rate on the biome
tier (11,158 / 12,088), and on the location tier it is 100 %.

**Yes, ship a background/nice mode.** The user tests builds live and plays multiplayer; a whole-space
scan is days. A mode that is explicitly slower and explicitly polite is the difference between "I can
leave this running" and "I have to remember to stop it".

**Memory to put in the plan block** — `per_worker`, measured:

| tier | live per worker | churn per seed |
|---|---|---|
| biome @384 m | 30 KiB | 0 B |
| biome @G12 | 24 MiB (and 16 MiB of it is the never-written height array, D5) | 0 B |
| height @384 m | 30 KiB + 12 MiB of river points | **50–60 MB** |
| location | **79.1 MiB** | 50–60 MB |

`SearchGrids.BytesPerWorker` (9 bytes/cell) is right for the grid tiers and silently omits the
location tier's 79 MiB, which is the only one that could actually hurt. I did not measure the fixed
process overhead (runtime + loaded data tables) separately, so the plan block should measure it at
start-up rather than take a constant from here.

## 4.6 One number in the estimator that is wrong

`SearchCommand.cs:526` — `double par = per / Math.Min(threads, 10.5);` — projects every tier's
parallel rate with a hard-coded ceiling of 10.5 effective threads. Measured speedups at 16 threads:

| tier | measured speedup at 16 threads | the constant says |
|---|---|---|
| biome @384 m | **13.4** | 10.5 |
| biome @G12 | **8.6** | 10.5 |
| height @384 m | **9.1** | 10.5 |
| location | **9.2** | 10.5 |

So the estimate is 22 % pessimistic on the cheap tier and 16–22 % optimistic on the three expensive
ones — in the direction that matters, it under-states how long a multi-day run takes. It should be a
per-tier measured constant (or calibrated by the `--calibrate` pass, which already exists).

---

# 5. Asynchrony — where it helps, where it is only complexity

The per-seed compute is CPU-bound and embarrassingly parallel at seed granularity. `async` adds
nothing there and would cost: a `Task` per seed is a scheduling record per 1.1 ms of work at the
biome tier (≈ 15,000 allocations/s of pure overhead), and `async` state machines would break the
tight loops that O5 depends on. **Keep `SearchRun.Worker` as dedicated threads running synchronous
loops.** That decision is already right.

Asynchrony earns its place in exactly four places.

## 5.1 Streaming results off the compute path — the one real bottleneck today

`SearchRun.Run` (`Execution/SearchRun.cs:171-179`) does this on the single collector thread, per hit:

```csharp
r.SeedText = SeedText.Invert(r.Seed, SeedAlphabet.Alnum62);
writer?.Write(r);
OnResult?.Invoke(r);
Keep(top, r, _q.Query.Search.Keep);
```

`vseed bench` measures `SeedText.Invert` at **3,750 seeds/s on one thread — 267 µs per call.** It is
an exhaustive walk of a lane level-set (`SeedText.cs:80-95`), not a formatting call.

That makes the collector thread a hard serial ceiling of **3,750 hits/s**. At today's 11,236 seeds/s
the biome tier saturates it at a 33 % hit rate; at the optimised 15,254 seeds/s, at 25 %; and the
inversion runs even for hits the bounded top-N heap is about to discard. This is Amdahl's law with a
267 µs serial section and nobody has noticed because the demo queries have low hit rates.

**Design.**

```
workers ──▶ BoundedChannel<Block>(capacity = threads × 2, FullMode = Wait)
                   │
                   ├─ collector task: order blocks, apply Keep(), decide what survives
                   │
                   └─ writer task:  serialise + FileStream.WriteAsync + periodic FlushAsync
```

* `SeedText.Invert` moves **into the worker**, or better, is applied only to records that are actually
  emitted (in bounded `--keep N` mode, to the final N). It is a pure function of the seed, so moving
  it changes nothing about determinism. 16× headroom, or ~4,000× if it is deferred to the survivors.
* Ordering and therefore byte-reproducibility are preserved because the **collector**, not the worker,
  decides emission order — exactly as today.
* **Back-pressure, which does not exist today.** `Worker` does `done[block] = hits;` and immediately
  claims the next block; nothing ever blocks it. If the collector stalls (slow disk, a paused UI
  client), the `ConcurrentDictionary` grows without bound — an unbounded queue in a process that is
  meant to run for days. A `BoundedChannel` with `FullMode.Wait` and capacity `threads × 2` fixes it
  with the right semantics: workers idle instead of consuming RAM, and the run slows to the speed of
  the slowest consumer rather than falling over.
* Disk is not the constraint: records are **306.9 B** (jsonl) and **89.9 B** (csv) measured on a real
  results file. Even `--keep all` at a 1 % hit rate and 15,000 seeds/s is 46 KB/s. One async writer
  task with a 1 MiB buffer and an `fsync` per slice is ample.

## 5.2 Slice bookkeeping overlapped with the next slice's compute

The user's slice contract is: merge → fsync → checkpoint (temp+rename) → delete intermediates → print.
`fsync` on this drive is 1–10 ms and the checkpoint is a few KB; a slice is 30–120 s of work. Doing it
synchronously costs < 0.01 %. **Do it synchronously.** Overlapping it would mean a checkpoint could be
written while the next slice is already mutating the heap — exactly the way a resume stops being
byte-identical. The correct async here is: workers keep running while the collector finishes slice N's
bookkeeping (which they already do, once §5.1's channel exists), and the *slice boundary itself* stays
a strict barrier on the collector.

## 5.3 The web UI: tile generation and cancellation

This is genuinely I/O- and latency-shaped and belongs on `async`:

* Map tiles are pure functions of (seed, rect, zoom). Serve them from an async endpoint backed by a
  bounded worker pool **separate from the search pool**, with a `CancellationToken` wired to the HTTP
  request — a user panning the map generates and abandons tiles constantly, and an abandoned tile
  should stop computing at the next row.
* Two pools, not one: the search pool at the §4.5 default, the tile pool at 2 threads. Otherwise the
  search starves the UI (the reason `ProcessorCount − 2` exists) and, worse, the UI's bursts steal from
  a multi-day scan.
* Progress to the browser is a server-sent-event stream from the same `OnResult` seam that the writer
  uses, coalesced to ≤ 2 updates/s (the CLI's existing 500 ms rule). Never one event per hit.
* Cancellation must be *cooperative at a block boundary*, which `RequestStop()` already is. Keep it.

## 5.4 Calibration and the live estimate

The user wants time and disk estimates "dynamically shown and updated". That is a small async loop,
not a thread: a timer on the collector side that recomputes `time_s = remaining / rate_measured`,
`bytes_out = min(hits, keep) × 306.9 B` and the hit-rate interval after every slice, and pushes them
through the same SSE stream. Keep it off the compute threads entirely, and keep the measured
constants — rate, bytes/record, per-worker memory — in one place that both the CLI and the web UI read,
so the two never quote different numbers.

---

# 6. Projected rates if the safe optimisations land

Baselines are `vseed search` on the quiet machine at 16 threads with an adequate block size.
Projections apply the **16-thread measured** ratio (§3.2), not the single-thread one.

| tier | today (16 threads) | whole 2^32 | with O1+O2+O3+O4+O5 | whole 2^32 |
|---|---|---|---|---|
| biome/coast @384 m | **11,236 seeds/s** (measured) | 4.42 d | **15,254 seeds/s** (measured, 1.33×) | **3.26 d** |
| biome/coast @384 m, 8 threads | **6,438 seeds/s** (measured) | 7.72 d | **13,196 seeds/s** (measured, 2.05×) | **3.77 d** |
| height/river @384 m | **27.7 seeds/s** (measured) | 4.91 y | **42.0 seeds/s** (measured, 1.33×) | **3.24 y** |
| height/river @384 m, bounded < 7.65 km (+O6) | 27.7 seeds/s | 4.91 y | *55–65 seeds/s* (projected; O6 is 1.56× on pre-generation alone and overlaps O2/O4) | *2.1–2.5 y* |
| biome @G12 | **19.2 seeds/s** (measured) | 7.09 y | *25–34 seeds/s* (projected; 1.75× at 1 thread, less at 16 because the tier is memory-bound) | *3.9–5.4 y* |
| location, boss+trader prefix | *≈ 3.8 seeds/s* (9.24× speedup ÷ 2.46 s/seed) | *36 y* | *≈ 5.3 seeds/s* (grid part 1.57×, placement unchanged) | *26 y* |
| location, all 183 types | *≈ 1.06 seeds/s* (9.24× ÷ 8.7 s/seed) | *128 y* | *≈ 1.16 seeds/s* | *117 y* |
| single-biome goal @384 m (+O7) | 11,236 seeds/s | 4.42 d | **4.20× on the pass, measured at 1 thread** for a Swamp goal; end-to-end at 16 threads not measured, and it overlaps O5 | — |

**Bold = measured. Italic = projected, with the arithmetic shown.**

Read this honestly: **the biome tier goes from "a long weekend" to "three days"**, and O7 should take
a single-biome question well below that again. **The height tier stays in years** — 3.2 instead of
4.9 — so
the funnel strategy in the user's decisions document is still the only way to reach location goals,
and these optimisations make its *stage 1* 1.33× faster and its *stage 2* 1.33–2.0× faster, which is
exactly where they are worth the most.

Three separate measurements say the same thing about the ceiling: after O5 the biome tier is at
0.57 CPU-ms/seed on 8 threads and 1.02 on 16; the height tier is allocating 2.4 GB/s; the location
tier's single worker already exceeds L3. **The next round of work is memory, not arithmetic.**

---

# 7. Defects found while profiling

These are in the code another workflow is editing right now, so they are reported, not fixed.

**D1 — the cost split the tool prints is wrong, and it is wrong about the very thing that matters.**
`Evaluation/SeedEvaluator.cs:208-238`: `SampleSeconds` is accumulated from a timestamp taken *before*
the deferred `Pregenerate(gen)` call, so it includes `PregenSeconds`. The report then prints both as
shares of their sum:

```
vseed search …/h384.json --threads 16
  thread time split   construct 0.00 %, pre-generation 49.75 %, sample 50.25 %
```

The true split at that grid is pre-generation **99.0 %**, sampling 1.0 % (§1.1). Fix: take `s0` after
`Pregenerate`, or subtract `PregenSeconds` when reporting.

**D2 — `SeedText.Invert` on the collector thread.** 267 µs per hit, single-threaded, run for every
passing seed including those the bounded heap discards. See §5.1. A hard ceiling of 3,750 hits/s.

**D3 — the block size is a fixed 256 seeds** (`Criteria/QueryModel.cs:169`) regardless of tier, so one
block is 0.28 s at the 384 m biome tier, 77 s at the height tier, 94 s at G12 and **11.5 minutes** at
the location tier. Threads starve and the tail is long. Measured, same query, same 2,000 seeds,
16 threads:

```
--block-size 256 (default)   18.8 seeds/s
--block-size 16              27.3 seeds/s      +45 %
```

Auto-size the block from the calibrated per-seed cost to ~1–5 s of work, with at least `threads × 8`
blocks in the run (and note this is *below* the slice, which the user's decisions put at 30–120 s: a
slice should be many blocks).

**D4 — no back-pressure between workers and the collector.** `Execution/SearchRun.cs:262`
(`done[block] = hits;`) never blocks. See §5.1.

**D5 — `SeedSampler` always allocates the height array.** `Metrics/SeedSampler.cs:47-48` allocates
`Height = new float[grid.Count]` in the constructor even when the plan has `NeedHeights == false`:
16 MiB per worker at G12, 256 MiB across 16 threads, never written.

**D6 — the parallel-scaling constant.** `SearchCommand.cs:526`, `Math.Min(threads, 10.5)`. See §4.6.

---

# 8. What I could not settle, and what would settle it

* **The location tier's placement cost, decomposed.** I timed `vseed locations` end to end (2.46 s for
  the boss prefix, 8.96 s for all 183 types, one thread) and the grid, field and pre-generation stages
  inside my own harness, but the placement loop itself and `AltBiomeAssignment` I could only get by
  subtraction, because they need the dumped `ZoneLocation` table and my scratch tree does not carry
  `data\`. **Settled by:** a `--stage-timings` flag on `vseed locations`, or running my `Prof` against
  a `SeedLab.LocationOracle` built with the real `data\` directory.
* **Whether O5's 2.93× survives once the masks and the per-biome detail noise are also batched.**
  I batched only `GetBaseHeight`'s eight samples. `GetForestHeight`, `GetMistlandsHeight` and the rest
  each have 4–7 more independent Perlin calls that could join a batch. **Settled by:** extending
  `GetBaseHeightSimd`'s technique to one biome-height function and re-running `Prof simd`.
* **The exact soundness radius for O6.** I proved 7,648 m empirically (0 of 76,780 heights differ) and
  derived it as 7,900 − 252 m. The 252 m is `maxLength (200) + widthMax (20) + 32`; I did not verify
  that `FindStreamEndPoint` can never return a point further than `maxLength` from the start (it steps
  *down* from 198.8 m, so it cannot, but that is reading, not measuring). **Settled by:** an assertion
  over every stream in a few thousand worlds that `Distance(p0, p1) <= 200`.
* **How much of the 16-thread shortfall in §3.2 is SMT and how much is DRAM.** CPU-ms/seed rising
  0.57 → 1.02 says SMT; the location tier's 79 MiB says DRAM. **Settled by:** hardware counters
  (`perf`/`uProf`) for `L3_MISS` and `EX_RET_OPS` per seed — not available to me here.
* **A clean 1–16 curve for every tier on a truly idle machine.** The first half of the session was at
  100 % CPU from other agents. The curves in §4 are from the quiet window and are self-consistent, but
  they still carry ~14 % background. **Settled by:** re-running `Prof scale` and `Prof locscale` when
  nothing else is on the box.
