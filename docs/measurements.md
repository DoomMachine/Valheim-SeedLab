# SeedLab measured performance

**One pass, 2026-09-23.** Every number in this file was produced by a single measurement pass on the shipped `vseed` CLI on the date and machine below - except the `axe-heads` preset row in section 6, which was added on 2026-09-24 by the same protocol and says so where it stands. Nothing here is copied from an earlier document or from a library-level benchmark.

> **This file is the single source.** The README, `docs\`, the CLI's own help and the web UI all cite it. Any throughput, file-size or cost figure elsewhere in the project that disagrees with this file is **superseded by this file**. Its machine-readable sibling is `docs\measurements.json` (schema `seedlab-measurements/1`).

Earlier sessions quoted 4.6 days, 28 days, 4.42 days and 2.3 days for the same kind of run. Those were measured while other work competed for the machine and they contradict each other. They are all retired by the tables below.

## The machine, the build, and what else was running

|  |  |
|---|---|
| **Measured** | 2026-09-23 11:26:25 UTC |
| **CPU** | AMD Ryzen 7 9800X3D 8-Core Processor - 8 cores / 16 logical, 4,700 MHz nominal |
| **RAM** | 61.6 GiB |
| **OS** | Microsoft Windows 10 Pro 10.0.19045 |
| **.NET SDK** | 10.0.401 |
| **AVX2** | default (DOTNET_EnableAVX2 not set) |
| **Results volume** | C: (598.6 GiB free) |
| **Project volume** | E: (3,098.2 GiB free) |
| **Source control** | none - E:\SteamLibrary\steamapps\common\Valheim is not a git repository, so there is no commit id. The build is identified by the SHA-256 of the binaries below. |

### Build identity

There is no commit id - the Valheim folder is not a git repository - so the build is identified by the SHA-256 of the binaries that produced every figure here.

| file | bytes | modified (UTC) | sha256 (first 16) |
|---|---:|---|---|
| `vseed.exe` | 162,304 | 2026-09-23 08:57:29 | `AD5139B5F093E2C5...` |
| `SeedLab.Contracts.dll` | 22,016 | 2026-09-23 08:57:27 | `44CD93F445290194...` |
| `SeedLab.Data.dll` | 459,264 | 2026-09-23 08:57:28 | `02FB681866197B98...` |
| `SeedLab.LocationOracle.dll` | 12,288 | 2026-09-23 08:57:29 | `F36572A66F8BE60A...` |
| `SeedLab.Locations.dll` | 54,272 | 2026-09-23 08:57:29 | `B5E4A695030C56CF...` |
| `SeedLab.Render.dll` | 45,056 | 2026-09-23 07:23:08 | `8BA285A80E794A97...` |
| `SeedLab.Runtime.dll` | 115,712 | 2026-09-23 06:34:47 | `20D18F4714192D49...` |
| `SeedLab.Saves.dll` | 62,464 | 2026-09-23 07:23:06 | `FBF47F0DA898DDA7...` |
| `SeedLab.Search.dll` | 339,456 | 2026-09-23 07:42:24 | `71FDBD6F325E526D...` |
| `SeedLab.Seeds.dll` | 20,992 | 2026-09-23 06:18:50 | `A589752CE462C6FA...` |
| `SeedLab.Web.dll` | 407,552 | 2026-09-23 07:52:05 | `EB0219DFDC1A6FDF...` |
| `SeedLab.WorldGen.dll` | 54,272 | 2026-09-23 06:18:50 | `5767F65908CB98B8...` |

Game data snapshot: `data\1.0.15-59f53fb5`, manifest SHA-256 `4DDF7F30213F91A2...`.

### Background load, stated rather than assumed

The machine was made as quiet as it could be made: `dotnet build-server shutdown`, the Roslyn compiler server killed, and **no other SeedLab process of this session's own**. What could not be removed was the ordinary desktop load.

Sampled over 30 s with no `vseed` on the machine, as a per-process `TotalProcessorTime` delta. Everything running on the desktop together came to **5.86 core-seconds**, i.e. **19.5 % of one core** - **1.22 % of the 16-thread box**. Ordinary desktop applications and this agent session were present throughout and are the reason the CPU column is an upper bound rather than an exact cost.

> A warning for anyone repeating this: **do not trust `\Processor(_Total)\% Processor Time` here.** During this pass it read a steady ~50 % while per-process deltas and `Win32_PerfFormattedData_PerfOS_Processor` idle time both said ~3 %. The 50 % was real work - a single `vseed` belonging to another agent, burning 6.5 cores - but the counter gave no way to see that. Summing per-process CPU deltas (excluding `Idle`) is what found it.

**The one thing that could not be removed:** another agent was running its own `vseed` commands on this machine for part of the pass. The harness therefore watched for any `vseed` process other than the one it had started, and **discarded and repeated every run during which one existed**. 410 vseed invocations were launched in total to produce the clean repetitions below.

## How each figure was taken

- **median of 3 clean repetitions** per cell; the individual repetitions are in the JSON.
- **seeds/s** is the run's own `--json` `seeds_per_second`: in-process scan time, excluding process start and JIT.
- **CPU ms/seed** is `Process.TotalProcessorTime` of the whole `vseed` process over seeds evaluated. It *includes* process start, JIT and the self-test stamp check, so it is an **upper bound**, and it is a *sum over workers* - at 16 threads it is about 10x the wall-clock ms/seed by construction.
- **peak working set** is `Process.PeakWorkingSet64`, polled at 120 ms and again after exit.
- **whole-space** is `4,294,967,296 / (median seeds/s)` as wall-clock time. It is a projection from a short run, not a run.
- every throughput cell ran with `--screen off` and **nice-to-have goals only**, so no must-have could trigger the grid auto-upgrade and the grid column is the grid measured. The harness asserted the reported `grid_m` and `threads` on every run and threw if either moved.
- one warm `--cache-dir` for the whole pass, so no measured run paid a cold self-test.
- **spread across the three repetitions**: worst cell is `locations/loc-bosses|16t` at **5.7 %** between its fastest and slowest repetition; 1 of 44 cells exceed 5 %. The taint rule only watched for a foreign `vseed`, not for a foreign `dotnet build`; this spread is the evidence that nothing else large ran across a measured cell.
- **`--block-size` was set so every worker gets at least 8 work blocks**, never coarser than the shipped default of 256. This matters more than it sounds: at the default, a 500-seed G12 run is *two* blocks, so 16 workers finish it in the time two would. The first attempt at this pass left the default in place and measured 1.04x scaling from 1 to 8 threads at G12; that was queue starvation, not the engine. The block size used is in the JSON for every cell.

### The fixed cost of one `vseed` process

A one-seed run, warm cache, measured end to end - this is what the CPU column carries on top of the work itself, and why a short run reports a lower rate than a long one.

| query | wall | CPU |
|---|---:|---:|
| `biome` | 0.41 s | 0.31 s |
| `height` | 0.69 s | 0.66 s |
| `loc-boss1` | 1.80 s | 1.75 s |

## 1. Biome / terrain criteria (T2) across the sampling ladder

Query: one nice-to-have `biome:Swamp area` goal, shuffled order, fixed Feistel key.

| grid | threads | seeds/run | seeds/s | wall ms/seed | CPU ms/seed | peak WS MiB | whole space |
|---|---:|---:|---:|---:|---:|---:|---:|
| G384 | 1 | 15,595 | 1,937.13 | 0.516 | 0.54 | 117.5 | 25.7 days |
| G384 | 8 | 124,757 | 13,416.89 | 0.075 | 0.59 | 135.2 | 3.7 days |
| G384 | 16 | 249,513 | 20,956.88 | 0.048 | 0.73 | 159.8 | 2.4 days |
| G256 | 1 | 8,677 | 1,086.47 | 0.920 | 0.97 | 111.9 | 45.8 days |
| G256 | 8 | 69,415 | 7,606.02 | 0.131 | 1.03 | 137.0 | 6.5 days |
| G256 | 16 | 138,829 | 12,273.31 | 0.081 | 1.24 | 158.7 | 4.1 days |
| G192 | 1 | 5,410 | 690.05 | 1.449 | 1.52 | 112.8 | 72.0 days |
| G192 | 8 | 43,273 | 4,800.35 | 0.208 | 1.61 | 140.1 | 10.4 days |
| G192 | 16 | 86,545 | 7,826.36 | 0.128 | 1.91 | 162.2 | 6.4 days |
| G96 | 1 | 1,667 | 209.02 | 4.784 | 5.01 | 107.0 | 237.8 days |
| G96 | 8 | 13,334 | 1,467.82 | 0.681 | 5.35 | 127.4 | 33.9 days |
| G96 | 16 | 26,667 | 2,366.08 | 0.423 | 6.35 | 146.4 | 21.0 days |
| G48 | 1 | 474 | 59.26 | 16.874 | 17.67 | 102.9 | 2.3 years |
| G48 | 8 | 3,786 | 412.70 | 2.423 | 19.05 | 114.9 | 120.5 days |
| G48 | 16 | 7,572 | 652.63 | 1.532 | 23.30 | 119.1 | 76.2 days |
| G24 | 1 | 127 | 15.87 | 63.022 | 65.70 | 101.1 | 8.6 years |
| G24 | 8 | 1,015 | 104.60 | 9.560 | 72.40 | 118.9 | 475.2 days |
| G24 | 16 | 2,030 | 165.08 | 6.058 | 89.26 | 131.2 | 301.1 days |
| G12 | 1 | 34 | 4.22 | 236.776 | 247.24 | 106.3 | 32.2 years |
| G12 | 8 | 270 | 26.85 | 37.243 | 281.02 | 141.5 | 5.1 years |
| G12 | 16 | 540 | 41.93 | 23.850 | 348.50 | 176.1 | 3.2 years |

**Thread scaling, measured** (the CLI's own estimator assumes 8.00x at 8 threads and 10.5x at 16):

| grid | 8 threads vs 1 | 16 threads vs 1 |
|---|---:|---:|
| G384 | 6.93x | 10.82x |
| G256 | 7.00x | 11.30x |
| G192 | 6.96x | 11.34x |
| G96 | 7.02x | 11.32x |
| G48 | 6.96x | 11.01x |
| G24 | 6.59x | 10.40x |
| G12 | 6.36x | 9.93x |

## 2. The three runtime modes

Same G24 biome query; `--mode` chosen and `--threads` left to the mode. On an otherwise quiet machine a mode is a **worker count**; its process priority only shows up under contention, which is exactly the case this pass removed.

| mode | workers | seeds/s | CPU ms/seed | peak WS MiB | whole space |
|---|---:|---:|---:|---:|---:|
| `--mode background` | 4 | 57.18 | 66.25 | 109.9 | 2.4 years |
| `--mode balanced` | 8 | 104.14 | 72.41 | 119.1 | 477.4 days |
| `--mode full` | 16 | 165.23 | 89.35 | 131.3 | 300.9 days |

## 3. Height (T3) and river (T4) criteria

Height: one nice `world:land_area` goal. Rivers: one nice `world:river_count` goal.

| criterion | grid | threads | seeds/run | seeds/s | wall ms/seed | CPU ms/seed | peak WS MiB | whole space |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| T3 `world:land_area` | G384 | 1 | 74 | 6.663 | 150.1 | 179.7 | 238.8 | 20.4 years |
| T3 `world:land_area` | G384 | 16 | 1,172 | 53.364 | 18.7 | 229.7 | 874.8 | 2.6 years |
| T3 `world:land_area` | G12 | 1 | 11 | 0.816 | 1,224.9 | 1,257.1 | 203.4 | 166.7 years |
| T3 `world:land_area` | G12 | 16 | 162 | 8.359 | 119.6 | 1,700.5 | 1,209.2 | 16.3 years |
| T4 `world:river_count` | G384 | 1 | 73 | 6.690 | 149.5 | 194.6 | 227.8 | 20.3 years |
| T4 `world:river_count` | G384 | 16 | 1,167 | 53.739 | 18.6 | 232.0 | 901.6 | 2.5 years |
| T4 `world:river_count` | G12 | 1 | 11 | 0.809 | 1,235.4 | 1,262.8 | 205.8 | 168.1 years |
| T4 `world:river_count` | G12 | 16 | 163 | 8.511 | 117.5 | 1,673.4 | 1,239.5 | 16.0 years |

**A river goal pays for a grid it never reads.** `river_count` comes out of the lake/river/stream pre-generation, not out of the sampling grid: over 32 seeds the per-seed values at G384 and at G12 are **identical**. The *run* is still priced by the grid - in the table above a rivers-only query is about 6x faster at G384 than at G12 for the same answer - so ask a rivers-only question at a coarse grid.

Height and rivers both sit behind the lake/river/stream pre-generation, which is why their 1-thread cost at G384 is two orders of magnitude above a biome query's: the pre-generation is a fixed ~150 ms per world that no grid makes cheaper.

## 4. Location criteria (T5)

All at G12. `prefix` is how many of the 183 ordered location entries the query forces a seed to place.

| query | prefix | threads | seeds/run | seeds/s | wall ms/seed | CPU ms/seed | peak WS MiB | whole space |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| one boss (`location:Eikthyrnir`) | 2 | 1 | 18 | 0.921 | 1,086 | 1,118 | 347.3 | 147.8 years |
| one boss (`location:Eikthyrnir`) | 2 | 16 | 284 | 9.474 | 106 | 1,535 | 1,988.6 | 14.4 years |
| one dungeon type (`group:sunken_crypts`) | 6 | 1 | 18 | 0.930 | 1,075 | 1,104 | 352.9 | 146.3 years |
| one dungeon type (`group:sunken_crypts`) | 6 | 16 | 283 | 9.376 | 107 | 1,550 | 2,226.9 | 14.5 years |
| all bosses (`group:bosses`) | 17 | 1 | 18 | 0.904 | 1,107 | 1,136 | 330.4 | 150.6 years |
| all bosses (`group:bosses`) | 17 | 16 | 273 | 8.903 | 112 | 1,605 | 2,506.5 | 15.3 years |
| bosses + traders | 22 | 1 | 17 | 0.847 | 1,180 | 1,218 | 352.9 | 160.6 years |
| bosses + traders | 22 | 16 | 271 | 8.615 | 116 | 1,654 | 2,401.1 | 15.8 years |
| all 183 entries (`group:tar_pits_with_alt`) | 183 | 1 | 5 | 0.206 | 4,843 | 4,922 | 314.8 | 659.2 years |
| all 183 entries (`group:tar_pits_with_alt`) | 183 | 16 | 65 | 1.917 | 522 | 7,028 | 2,659.0 | 71.0 years |

## 5. The region restriction

`biome:Swamp area_within radius 1000` at G12, which restricts measurement to a 1 km disc, against the same grid measured over the whole world.

| threads | seeds/s | wall ms/seed | CPU ms/seed | peak WS MiB | whole space | vs whole-world G12 biome area |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 350.06 | 2.86 | 2.98 | 123.6 | 142.0 days | 82.9x |
| 16 | 3,484.56 | 0.29 | 4.29 | 198.8 | 14.3 days | 83.1x |

**The controlled comparison** - the *same* goal, same grid, same 16 workers, once normally and once with `--no-prefilter`, which turns the region restriction off:

|  | seeds scanned | seeds/s | CPU ms/seed | peak WS MiB |
|---|---:|---:|---:|---:|
| with the 1 km region | 40,000 | 3,459.1 | 4.32 | 198.5 |
| `--no-prefilter` (no region) | 1,200 | 42.4 | 347.94 | 176.2 |

**81.5x.** That is the honest figure for this goal on this machine. Earlier documents quote 162x (a resolution study), 110x and 39.8x for "the region lever"; those are different goals at different radii and grids, and none of them is this measurement. The factor is a property of the goal and the radius, not a constant - quote it with both.

**Exactness, tested rather than argued.** The same goal over 2,000 seeds, with and without the region restriction, produced 2,000 records on each side and **identical per-seed values**. A metric bounded by a disc cannot be changed by a cell outside it, and the measurement agrees.

## 6. Every shipped preset, exactly as shipped

No `--grid`, no `--screen`, no `--threads`, no `--mode`: the grid policy, the screen-then-verify decision and the default `balanced` worker plan are all in play, and each preset was given a 20 s wall budget three times. **This is the number a user gets.**

| preset | grid the run used | workers | seeds/s | CPU ms/seed | peak WS MiB | hit rate | whole space |
|---|---|---:|---:|---:|---:|---:|---:|
| `all-traders` | G384 | 8 | 5.68 | 1,343.8 | 1,024.2 | 13.67 % | 23.9 years |
| `archipelago` | G12 | 8 | 5.65 | 1,402.2 | 985.9 | 10.55 % | 24.1 years |
| `axe-heads` (added 2026-09-24) | G12, unused - location goals only | 8 | 4.12 | not measured | not measured | 41.80 % | 33.1 years |
| `balanced` | G192 | 8 | 3.79 | 1,982.9 | 1,352.7 | 61.33 % | 35.9 years |
| `balanced-biomes` | G192 | 8 | 38.09 | 199.7 | 658.4 | 100.00 % | 3.6 years |
| `boss-rush` | G384 | 8 | 5.77 | 1,342.2 | 1,199.6 | 5.47 % | 23.6 years |
| `coastal-builder` | G24 screen -> G12 | 8 | 19.41 | 399.7 | 625.9 | 36.91 % | 7.0 years |
| `compact-progression` | G384 | 8 | 5.93 | 1,308.2 | 1,072.2 | 22.27 % | 22.9 years |
| `custom` | G12 | 8 | 2,404.54 | 3.3 | 159.8 | 100.00 % | 20.7 days |
| `dungeon-delver` | G384 | 8 | 3.67 | 2,106.6 | 1,450.9 | 12.89 % | 37.1 years |
| `gentle-start` | G24 screen -> G12 | 8 | 7.93 | 914.0 | 919.4 | 19.92 % | 17.2 years |
| `iron-rich` | G24 | 8 | 5.64 | 1,372.2 | 1,120.7 | 18.75 % | 24.1 years |
| `large-continents` | G12 | 8 | 5.60 | 1,414.1 | 942.2 | 0.00 % | 24.3 years |
| `mountain-home` | G24 screen -> G12 | 8 | 1,722.68 | 4.6 | 61.8 | 0.00 % | 28.9 days |

**The `axe-heads` row** was measured a day later than the rest, by the same protocol (`--budget 20s
--block-size 32`, three runs; each ran one block per worker, 256 seeds, in 63.1 / 62.2 / 60.7 s, and all
three found 107 matches); the rate is the median of the three, as in every other row, and
`docs\measurements.json` carries all three. The CLI does not print CPU time or peak working set,
so those two cells say so rather than borrow a neighbour's. Its grid is irrelevant: every goal is a
location goal, and the same 256-seed run at G384 took 60.1 s against 61.6 s at G12, with bit-identical
distances (see the preset's own comments).

**Read the hit rate beside the rate.** A preset is not slow or fast because of its grid alone: `mountain-home` and `custom` are three orders of magnitude faster than `dungeon-delver` at a *finer* grid, because their must-have goals reject almost every seed at the cheapest tier and the expensive tier is never reached. That early exit is the engine working as designed, and it is why a whole-space projection from one preset says nothing about another.

Peak working set for a preset at the shipped 8 workers runs to about 1 GiB; the T5 location tier at 16 workers reaches 2.5 GiB (section 4). Budget memory from those columns, not from the biome ladder.

**The one deviation from "as shipped": `--block-size 32`.** A wall budget cannot stop a run sooner than one block per worker, so at the shipped default of 256 with 8 workers the smallest possible run is 2,048 seeds - `--budget 20s` on `all-traders` actually took **355 s**. At 32 the same command takes 46 s. The control: the same 240,000-seed G384 scan on 16 workers ran at 20,408.6 seeds/s at `--block-size 32` against 20,956.9 at 256 - a **2.62 %** cost, measured at the fastest tier, where per-block overhead matters most. Every preset above is therefore at most that much slower than its shipped-default self.

Fastest shipped preset: `custom` at 2,404.54 seeds/s (20.7 days for the whole space). Slowest: `dungeon-delver` at 3.67 seeds/s (37.1 years). Hit rates are per-preset and are in the JSON under `presets[].hit_rate`.

## 7. What a run costs on disk

### Bytes per record, by output format

| format | goals in query | records | file bytes | bytes/record |
|---|---:|---:|---:|---:|
| `.jsonl` | 1 | 1,000 | 433,622 | 433.6 |
| `.json` | 1 | 1,000 | 436,625 | 436.6 |
| `.csv` | 1 | 1,000 | 85,796 | 85.8 |
| `.jsonl` | 4 | 1,000 | 1,779,417 | 1,779.4 |
| `.json` | 4 | 1,000 | 1,782,420 | 1,782.4 |
| `.csv` | 4 | 1,000 | 200,778 | 200.8 |

**Fitted on this build:** a JSONL record costs about **449 bytes per goal** (`-15 + 449 x goals`; the fixed part is inside the noise of a two-point fit and the useful number is the slope). That is far above the `54 + 150.5 x goals` law the web UI still projects with, because every goal now carries its presentation fields - `measured_at_grid_m`, `measured_median_rel_err_at_grid`, `error_source`, and `grid_note` or `censored` where they apply. A projected file size computed from the old law under-reads a real one by roughly 3x. CSV is unaffected: it carries value/pass/score/bounded per goal and none of the presentation fields, which is why it is about 5.1x smaller at one goal.

### A bounded run's file size against `--keep`

The same 20,000-seed scan every time, so the number of *matches* is constant and only the cap moves. This is the whole point of `--keep`: the file is `keep x record size`, not `matches x record size`.

| --keep | matches | records written | dropped | file bytes | bytes/record |
|---:|---:|---:|---:|---:|---:|
| 10 | 20,000 | 10 | 19,990 | 4,336 | 433.6 |
| 100 | 20,000 | 100 | 19,900 | 43,367 | 433.7 |
| 1,000 | 20,000 | 1,000 | 19,000 | 433,622 | 433.6 |
| 10,000 | 20,000 | 10,000 | 10,000 | 4,409,454 | 440.9 |
| 20,000 | 20,000 | 20,000 | 0 | 8,890,608 | 444.5 |

### Rotation and compression

`--keep all --rotate 200KB --compress gz` over 20,000 records produced **44 segments** plus a manifest:

|  |  |
|---|---|
| segments | 44 (`seg.0001.jsonl.gz` .. `seg.0044.jsonl.gz`) |
| closed segment, median | 19,097 B |
| closed segment, min / max | 18,572 B / 19,576 B |
| final, partial segment | 7,666 B |
| manifest | `seg.manifest.json`, 10,509 B |

Each closed segment is gzipped to about 19 KB from a 200 KB raw cut, which is where the compression ratio below comes from. The manifest lists every segment with its record count, seed range, sizes and SHA-256; the full per-segment list is in the JSON.

Same run with `--compress none`: 8,890,608 B of segments against 828,940 B gzipped - **10.73x**.

### Checkpoint

- `3d5d18a8f9808e9b.ckpt` - **903 B**, the resume point itself.
- `3d5d18a8f9808e9b.ckpt.top` - **785,718 B**, the kept-set snapshot beside it - `keep` records, so it scales with `--keep`, not with the scan.
- both live in the cache root, keyed by the query hash, never in the working directory, and a completed run deletes them.

Cost of writing one every second, on the same 240,000-seed G384 scan at `--block-size 64`: 20,672.7 seeds/s at `--checkpoint-every 1` against 20,851.9 seeds/s at `--checkpoint-every 3600` - **0.9 %**.

## 8. The three claims, re-verified end to end through the CLI

### 8.1 A whole-space run with the default bound writes a bounded file

`vseed search ... --all` with **no `--keep`** (so the default 1,000 applies) and a 45 s wall budget. It is a genuine `--all` range; the budget only decides how far it got, and the cap does not depend on that.

|  |  |
|---|---|
| seeds evaluated | 971,008 |
| fraction of 2^32 | 0.00022608041763305664 |
| seeds matched | 971,008 |
| records written | 1,000 |
| records dropped | 970,008 |
| file | 1,000 lines, 433,623 B |
| run's own words | `top 1,000 of 971,008 matches; 970,008 were not kept, from the point where the cut line was 0.6341 (it ended at 1)` |
| **verdict** | **BOUNDED** |

### 8.2 A hard kill resumes to a byte-identical result

`Stop-Process -Force` (TerminateProcess - no shutdown path runs, unlike Ctrl-C) partway through, then the same command with `--resume`. Compared against an uninterrupted run of the identical command by SHA-256 of the results file.

| case | bound | seeds | threads | killed at | bytes at kill | clean bytes | resumed bytes | SHA-256 |
|---|---|---:|---:|---:|---:|---:|---:|---|
| bounded | `--keep 1000` | 60,000 | 16 | 28.1 s | 431,605 | 432,568 | 432,568 | **identical** |
| streaming | `--keep all` | 60,000 | 16 | 28.1 s | 6,986,205 | 26,735,098 | 26,735,098 | **identical** |

- `bounded`: the resume found and used the checkpoint in the cache root.
- **`streaming`: the kill really did tear the file** and the resume really did repair it - the run's own words: *repaired a torn results tail: 598,016 B written past the checkpoint were discarded, because this leg produces them again*

The streaming case is the one worth having: 16 workers appending to one file, killed with a partial record on disk, and the resumed file still matches the uninterrupted one byte for byte.

- `bounded` clean `E87A27F21FA8231325F3B6D3B61CBFC9E10CD90813BEB4BE2EA06C6938933587`
- `bounded` resumed `E87A27F21FA8231325F3B6D3B61CBFC9E10CD90813BEB4BE2EA06C6938933587`
- `streaming` clean `331476C71962DB2DB31BECEC579DFF8C099AE8770FD200BEA750A4F87448CAB7`
- `streaming` resumed `331476C71962DB2DB31BECEC579DFF8C099AE8770FD200BEA750A4F87448CAB7`

### 8.3 How far `--dry-run` lands from reality

`--dry-run --calibrate 8 --threads 16` against a real 16-thread run of the same query, both medians of 3. The estimator times a tight single-thread loop over 8 seeds and divides by `min(threads, 10.5)`, so it charges none of the per-seed fixed cost a real run pays.

| tier | seeds | block size | estimated seeds/s | measured seeds/s | estimate / measured |
|---|---:|---:|---:|---:|---:|
| T2 biome, G384 | 240,000 | 256 | 10,526.2 | 20,990.4 | **0.50x** |
| T2 biome, G96 | 25,000 | 195 | 1,266.1 | 2,381.2 | **0.53x** |
| T2 biome, G12 | 500 | 3 | 45.8 | 42.6 | **1.07x** |
| T2 biome in a 1 km disc, G12 | 40,000 | 256 | 1,750.5 | 3,450.8 | **0.51x** |
| T3 height, G12 | 120 | 1 | 9.0 | 8.4 | **1.07x** |
| T4 rivers, G384 | 800 | 6 | 66.2 | 54.0 | **1.23x** |
| T5 one boss, G12 | 140 | 1 | 9.9 | 9.2 | **1.08x** |
| T5 all 183 entries, G12 | 32 | 1 | 2.2 | 2.1 | **1.06x** |

**Verdict: 8 of 8 tiers land inside 2x, and the worst is 1.23x** (*T4 rivers, G384*). On 5 of 8 tiers the estimate runs ahead of reality, never by more than 1.23x; on the rest it is **conservative** - as low as 0.50x on *T2 biome, G384*, where the calibration loop's own warm-up over 8 seeds costs more per seed than a long run pays.

**This corrects a claim the project has been repeating.** `vseed search`'s own `--dry-run` note (`SearchCommand.cs`, the paragraph beginning "Those last three are an ESTIMATE"), the README and the seedlab skill (`.claude\skills\seedlab\SKILL.md`) all say the estimate runs "about 35x ahead" on the cheapest biome-only query. **This pass could not reproduce 35x in any tier.** At G384 the estimate projects 10,526 seeds/s against a measured 20,990 - it **under**-projects by about 2x. The largest over-projection seen anywhere in this pass was 10.6x, and it came from an earlier attempt at this very table that left `--block-size` at its default: the measured side was then two work blocks across 16 workers. With enough blocks the same tier reads 1.07x. That is the most plausible cause of the 35x too, but this pass did not reproduce 35x from starvation either, so the honest statement is "not reproducible here" rather than "caused by X". Those three pieces of text should be updated to match this table.

**The independent verifier's figure is corrected here too.** That review reported *4 of 5 tiers inside 2x, worst 3.26x*. Measured over eight tiers with every worker fed: **8 of 8 inside 2x, worst 1.23x**. The 3.26x does not reproduce. Given what section 9.2 shows about short runs and block size, the most likely explanation is the same artefact, but that is an inference, not a measurement.

`--dry-run` is still not a promise - it is a single-thread loop over 8 seeds divided by `min(threads, 10.5)` - but on this build it is a reasonable first guess in both directions rather than a wild over-estimate. Confirm anything long with a short real run (`--seeds 20000`); a finished run's own `measured rate` is the real number.

## 9. What this pass found that is not a throughput number

These came out of running the shipped CLI rather than reading it, and each one is a statement about the build whose SHA-256 is at the top of this file.

1. **A wall budget cannot stop a run sooner than one block per worker.** `vseed search all-traders --budget 20s` with the shipped `--block-size 256` and 8 workers ran for **355 s** and evaluated 2,048 seeds - one full round - because the budget is checked at block boundaries. At `--block-size 32` the same command took 46 s. The budget is a *floor-plus-one-round*, not a ceiling, and nothing says so.
2. **Block size decides how many workers a short run actually uses.** Seeds per run divided by block size is the number of blocks; if that is below the worker count, the extra workers idle. At the shipped default a 540-seed G12 run is two blocks. This is correct behaviour for a long scan and a trap for anyone benchmarking with a short one - including this pass on its first attempt, where it turned a real 6.36x scaling at G12 into a measured 1.04x.
3. **A rivers-only query pays for the sampling grid it never reads** (section 3): identical per-seed `river_count` values at G384 and G12, about 6x the cost at G12.
4. **The web UI's projected-file-size law is about 3x low** for today's record shape (section 7): a JSONL record now carries per-goal presentation fields that did not exist when `54 + 150.5 x goals` was fitted.
5. **`--checkpoint-every 1` costs 0.9 %** and the checkpoint itself is 903 B; what is actually large beside it is the kept-set snapshot, which scales with `--keep`.

## 10. What this file supersedes

Nothing below is wrong-in-principle; it is measured on a busier machine, on a different build, or with a short run that could not fill its workers. Where a figure here and a figure there disagree, **this file wins**, and the other should be changed to cite it.

| where | what it says | replace with |
|---|---|---|
| `README.md`, "The measured table" | seven preset rows at `--mode balanced`, and "`--mode full` (16 workers) buys about 1.5x, not 2x" | section 6 here for the presets, and the mode table in section 2 (full is 1.59x balanced on the G24 biome query) |
| `README.md`, "Why the spread" | "7.90 ms/seed inside a 1 km disc against 314.40 ms/seed over the whole world - 39.8x" | the controlled 1 km comparison in section 5 |
| `README.md` and the seedlab skill (`.claude\skills\seedlab\SKILL.md`) | "`--dry-run` over-projects up to 35x on the cheapest query" | section 8.3 - it could not be reproduced in any tier |
| `SearchCommand.cs`, the `--dry-run` note | "by ~1.5x on a heavy T3 query and by ~35x on a cheap biome-only one" | section 8.3 |
| `SearchCommand.cs`, `Calibrate` **(confirmed, not superseded)** | "10.5x is the measured 16-thread speed-up" | keep it - the thread-scaling table in section 1 measures 10.8x at G384 down to 9.9x at G12, so 10.5x is a good assumption at every grid in the ladder |
| the criteria-language schema | "JSONL segments compress 10.0x" | **10.73x** measured here (section 7) |
| session notes | 4.6 days / 28 days / 4.42 days / 2.3 days for "the same kind of run" | whichever row of section 1 or section 6 names the same grid, region and worker count |

## How to cite this file

Quote the cell, the date and the machine together - a throughput number without its grid, thread count and machine is not a fact about SeedLab. For example:

> a biome-only query at G384 on 16 workers runs at **20,957 seeds/s** on the machine in `docs\measurements.md` (2026-09-23), which projects the whole 4,294,967,296-seed space at **2.4 days**.

A figure taken from here should name the section it came from, so that the next pass can replace it in place. If a later pass runs on a different machine or a different build, it **replaces this file** - it does not get added beside it.

`docs\measurements.json` carries the 44 cells with the three repetitions behind each median under `throughput`, and **every one of the 410 `vseed` invocations this pass launched** under `runs_raw` - including the pilots that sized it, the repetitions discarded for contamination, and the first, block-size-confounded attempt at the ladder. `runs_raw` is the audit trail; `throughput` is the answer. Paths under the measurement work folder are shown as `<work>\` in the recorded command lines.
