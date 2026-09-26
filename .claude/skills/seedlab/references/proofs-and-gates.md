# SeedLab: the oracles, the gates and what they really prove

Contents: 1. The four oracles · 2. The gates (2.0 on 1.0.16, and what a missing file does) ·
3. What each oracle cannot discriminate · 4. Measured cost · 5. Open ends

Every figure here was produced by running something on this machine between 2026-09-22 and 2026-09-23,
against Valheim 1.0.15 (network 40, Steam build 25390630, `assembly_valheim.dll` sha256 `59f53fb5...`),
except section 2.0: **the gates were re-run on 2026-09-26 against Valheim 1.0.16** (Steam build 25527674,
`96cfc004...`) and its own worlds. Numbers are quoted with the world they came from, because a number without
its world is not evidence.

---

## 1. The four oracles

An oracle is something **the game itself wrote**. SeedLab is checked against four of them, in
increasing order of how hard they are to fool.

| Oracle | What it is | Precision | Covers |
| --- | --- | --- | --- |
| Minimap cache (`cacheMinimapBiome` / `cacheMinimapHeight`) | the 2048x2048 12 m map the game builds on first load | biome exact; height **binary16** | `GetBaseHeight`, `GetBiome`, `WorldAngle`, all four mask noises, `GetBiomeHeight`, the world edge |
| `.db2` location instances | `GenerateLocationsTimeSliced` stores `y = WorldGenerator.GetHeight(x, z)` unchanged | **float32** | the same, at full precision, including the river pass; 12,314 and 12,287 free samples out to 10,300 m |
| The dumper's golden captures (`data\...\goldens\`) | the generator's own private state, read by reflection in the running game | bit patterns | offsets, river seeds, lakes, rivers, streams, the whole `m_riverPoints` grid, the native functions |
| The game's own log | `placed N out of M` per location type, alt-biome under-min warnings | counts | the placement run end to end |

The float32 oracle is the one that matters most: **a perfect score against the half-precision minimap
does not mean the port is right.** Before the Mono-R8 correction SeedLab was 99.9998 % exact against the
cache and 99.43 % / 99.51 % against the `.db2` floats, with errors up to 47 float ulps
(valheim-modding `pitfalls.md` section 9).

## 2. The gates

### 2.0 On Valheim 1.0.16 (2026-09-26)

Run from the SeedLab root on binaries built at `ded6c94` (every assembly `0.1.0+ded6c943...`), with the
`SEEDLAB_*` variables unset and fresh cache roots; the ground truth is the rebuilt `groundtruth\` (four worlds
1.0.16 created: `asdasdasd`, `testworldclaude`, `ClaudeTestWold2` re-created from their seeds, and
`Throwaway`, the fresh world dumper run 7 ran in) and the data is `data\1.0.16-96cfc004`. Evidence: the fix round's report (section 3) and its
evidence folder, with the author's working notes.

| Gate | Result |
| --- | --- |
| `vseed data --verify` | **all 8 checks passed**, MATCH: 25/25 files (155,494,018 B), 707 rows, 35/35 names and hashes, 35 quantities, 521 table rows against 429 recorded hashes, constants, 186/186 prefabs walked, order 183/183. The 1.0.15 folder (`SEEDLAB_DATA_DIR` = it, `SEEDLAB_VALHEIM_DIR` = an empty folder): 8/8, UNVERIFIED as expected, 47/47 files |
| `vseed selftest` (full; also `--strict`) | PASS, **14 ok**, G1 "same build (1.0.16)": T2 biomes 2,542,492/2,542,492 and 2,562,380/2,562,380 (white 1,651,812 / 1,631,924), T3 heights 4,194,304 x 2, T3a 1,788,980 edge pixels, 0 disagree; `vseed.log`: `264069 recorded values` from `groundtruth\natives` |
| `vseed selftest --report` | PASS: natives 263,780/263,780; 24 world digests bit for bit; L4-L5 "computed but not compared" (the reference file is stamped 1.0.15; all 40 digests of the 8 report seeds equal it, checked by the review) |
| Acceptance | **32 passed, 0 failed**: S2/S3 0 differ on both worlds; S4 -0.0 pixels 1 and 2; S7 12,314 / 12,287; T3 TIER A both; T5 location heights 12,314/12,314 and 12,287/12,287 bit-exact |
| `LocationLab gate` | **GATE: PASS.** Fresh `Throwaway` (`BB9B7F96`) **12,182/12,182** exact, 974 sectors, 32/32 assignments (100/100 slots), 178/178 prefabs, order 183/183. Played: 12,314/12,314, 12,287/12,287, 12,228/12,228, 0 engine-only. Alt biomes: 32 assigned to 92 sector slots, 1 under-min warning in the game's log and 1 in the engine, all counters equal. Game log: 27 types, 27 reproduced exactly. With the 1.0.15 data instead: PASS, fresh `0480A34C` 12,228/12,228 and the three 1.0.16 saves exact |
| `SeedLab.Tests -- natives` | **11/11**: Perlin 262,780/262,780; 268 `InitState`; 276 traces / 1,980 draws; 1,634 halves; 93 libm; 49 `WorldAngle`; 429 hashes |
| `GoldenCheck -- data\1.0.16-96cfc004` | **PASS**, 3 captures bit-identical: rivers 161 / 140 / 170; river points 677,094 + 675,579 + 729,925, 0 differing; `GetHeight` 12,182/12,182 (37 placed, 1,769 river-weighted) |
| `SeedLab.Runtime.Tests` | 326 passed, 0 failed |
| `SeedLab.Search.Tests` (~10 min) | **543 passed, 0 failed**; section 8: the door-named types place exactly as the game did in `BB9B7F96` (1,549 instances); section 19: the atlas used is the same build's, refusals enabled (not ADVISORY) |
| `proof.exe refuse / policy / bounded / rotate / blocks / screen` | all exit 0 (`tests\SeedLab.Search.Safety.Tests` is `proof.exe <subcommand>`; a bare run prints usage and exits 2) |
| `killtest.ps1` | 3 kills, each resumed in 1 leg: IDENTICAL x3. It exits 0 even on DIFFERENT, so read the `IDENTICAL` lines |
| `vseed search ... --dry-run` (both folders in `data\`) | exit 0, `location table 1.0.16 / 96cfc004`, 0 `ADVISORY` / `REFUSED` lines |

**What fails, and what does not, when a ground-truth file is missing** (mutation tests on a copy of
`groundtruth\`, the fix round's report, section 4):
- `LocationLab played` / `gate` **fail** since `ded6c94` when `groundtruth\worldgen-testworldclaude.log` is
  absent: `alt-biomes MISSING <path>`, `game log MISSING <path>`, `GATE: FAIL`, exit 1. (Before it, `GATE: PASS`
  with the 27 counters and the alt-biome check silently dropped.)
- **`vseed selftest` passed silently** up to `ded6c94`. The fix `f8a8f4d` (21:28 on 2026-09-26: a new row N1 and
  failing `V1c` / natives rows on an incomplete ground truth; the test `SeedLab.Tests groundtruth-completeness`, 32
  checks, fails 21 of them against `ded6c94`) is on `main` since the alpha 0.2.0a, **merged without its review and
  fix phases** (history.md, "0.2.0a"). Up to `ded6c94`, without a
  world's `.fwl2` it prints 13 rows, no `V1c/<world>` (`SelfTestCommand.cs:209`); without any of the four
  natives files the natives suite is not registered (`NativesSuite.cs:55-93`, `CliRuntime.cs:160-172`),
  `vseed.log` shows `289 recorded values` instead of `264069`, and `selftest --report` wrongly says "not beside
  this build" (`MachineReport.cs:269-272`).
- Loud: `SeedLab.Tests natives` (exit 2 without `natives-perlin.bin`, an unhandled exception without another
  file); acceptance (crashes at S5 without a `.fwl2`); `vseed data --verify` (fails 2 of 8 without
  `groundtruth\location-names.csv`; `vseed selftest` exits 3 without `groundtruth\decoded`).
- So read a PASS for its rows: `V1c/<world>` twice, `Passed with the generator goldens from
  ...\groundtruth\natives: 264069 recorded values`, `game log 27 types`, `alt-biomes ... all counters equal`
  (valheim-modding `pitfalls.md` section 10).

The subsections below are the 1.0.15 record (2026-09-22/23). Their counts on the two played worlds and in the
natives are the same on 1.0.16. The 1.0.15 world copies and game log they cite were lost on 2026-09-26; the
fresh-world golden `0480A34C` survives in `data\1.0.15-59f53fb5\goldens\`.

### Acceptance suite - terrain

```
dotnet run --project tests\SeedLab.Acceptance.Tests -c Release
```

32 checks, 2-3.5 minutes. Regenerates both ground-truth worlds cell by cell.

- Biome: **0 mismatches** over 2,542,492 (`asdasdasd`) and 2,562,380 (`testworldclaude`) unambiguous
  pixels; every white pixel resolved to Ocean / DeepNorth / Mountain with the counts adding up exactly.
- Height: **4,194,304 / 4,194,304 binary16 codes identical per world**, worst difference 0 m, using the
  away-from-zero tie-break that `Mathf.FloatToHalf` was measured to use.
- The world-edge constant: 1,788,980 pixels hold -400 m, and that set equals
  `DUtils.Length(wx, wy) > 10500f` with 0 disagreements, on both worlds.
- Location heights from the `.db2`: **12,314 / 12,314** and **12,287 / 12,287** bit-exact as float32.

### Location gate

```
dotnet run -c Release --project tools\SeedLab.LocationLab -- gate
```

- Fresh world `ClaudeTestWold2` (seed 75539276, dumped straight after genloc, nothing explored):
  **12,228 / 12,228** instances with zone, prefab and x/y/z bit-identical; 0 missing, 0 extra;
  178/178 prefabs; every type's `placed` counter equal. A fresh world is the better oracle - nothing
  has been pruned by exploration, so the unplaced candidates are there too.
- The same world's sector decomposition: **938 / 938** sectors agreeing on biome, EdgeCount, Center,
  Min/Max, MinZone/MaxZone, HeightMin/Max/Avg, DistanceFromCenter and neighbours, floats as bit
  patterns; **32 / 32** alt biomes with identical sector lists *in `AddModifier` order*.
- The two played worlds from their `.db2`: 12,314 / 12,314 and 12,287 / 12,287, with 0 engine instances
  the save no longer holds.
- The game's own log of `testworldclaude`'s creation: all **29** types that logged
  `placed N out of M` reproduced exactly, plus the single alt-biome under-min warning with the same
  counters.

### Natives

```
dotnet run --project tests\SeedLab.Tests -c Release -- natives
```

11 checks against corpora captured inside the running game (`groundtruth\natives\`, copied into
`data\...\goldens\`):

- `Mathf.PerlinNoise`: **262,780 / 262,780** float32 bit patterns over four blocks - 24 hand-picked
  probes (the abs fold, exact lattice points, the 256 period, 2^24), the same 24 through
  `PerlinNoise1D`, a dense 512x512 grid spanning negatives, and 588 of the exact arguments `GetBiome`
  and `GetBaseHeight` feed it. Every rival normalisation was re-measured and fails.
- `UnityEngine.Random`: **268 / 268** `InitState` seeds and **276 / 276** traces (1,980 draws),
  checking the result bits *and* the four state words after every draw.
- `Mathf.FloatToHalf`: 1,634 finite values, **1,634 / 1,634** match `(Half)f` with midpoints redirected
  away from zero; plain ties-to-even matches only 1,632.
- Mono libm vs .NET 10: **93 / 93** bit-identical (`Sin`, `Cos`, `Atan2`, `Pow`), max |ULP| 0.
- `GetStableHashCode`: **429 / 429** vectors.

### Generator internals

```
dotnet run -c Release --project tools\SeedLab.GoldenCheck
```

Three seeds (-1772362158 menu capture, 319486907 menu capture, 75539276 in-world), every float compared
as an IEEE bit pattern: the five offsets, both RNG seeds, the seven constructor draws in order (offset4
**last**), lakes in count/order/coordinates, rivers and streams in order with every endpoint, width and
curve parameter, and `m_riverPoints` cell for cell and point for point in order - 2,117,250 points over
72,721 cells, 8,469,000 floats, **0 differing**. It is negative-controlled: a planted 1-ULP change is
reported.

### Self-tests you can run any time

`vseed selftest` (`--quick` = 1 cell in 16, ~4 s), `vseed serve --selftest` (tiles against the game's
own texture, the tile mosaic against a single render, markers against the placement engine, the search
panel against a direct engine run, plus loopback / `Host` / CORS / CSP / path traversal),
`vseed data --verify` (every file in `data\` re-hashed against `manifest.json`), `vseed space` (the lane
tables and inverse round trips recomputed before they are printed).

### The Perlin self-test - always on, cannot be skipped

Since the 2026-09-23 optimisation pass `SeedLab.WorldGen` carries three spellings of
`Mathf.PerlinNoise`: the literal transcription `UnityPerlin.Noise` (the reference), the byte-table
scalar `PerlinFast.NoiseScalar` (what `UnityPerlin.PerlinNoise` now calls) and the AVX2 8-wide
`PerlinFast.Noise8`. `PerlinSelfTest` compares all three on a fixed hostile vector set and **throws**
if any pair disagrees by one bit. It runs from a `[ModuleInitializer]`, so it fires before any tool can
use the assembly and no caller can forget it - the fail-closed machine check decisions.md section 7
asks for. Cost ~10-13 ms once per process; `PerlinSelfTest.Report()` returns the line to print.

Typical line: `Perlin self-test PASS: 7,320 byte-table scalar samples and 58,560 AVX2 8-wide samples
bit-identical to UnityPerlin.Noise; 8-wide path ENABLED (AVX2); 9.7 ms`. On a machine without AVX2 the
8-wide path is simply disabled and the line says so.

It is not a substitute for the gates - it proves the three spellings agree with each other, while
`tests\SeedLab.Tests -- natives` is what proves the one they agree on is the game's.

## 3. What each oracle cannot discriminate

This is the part that is easy to lose. A gate that passes says what it can see, no more.

- **The minimap cache is binary16.** It cannot see an error below half a ulp - 0.015 m at sea level,
  0.25 m on a 400 m peak. It also should not be used for a land/water mask: 597 and 637 of 4,194,304
  cells classify differently from the exact float32 at the 30 m line, which moves island *connectivity*
  (raw component counts 9,866 -> 9,935 and 10,302 -> 10,383) while the totals barely move.
- **A defect census is how you measure an oracle's power.** Planting each ported detail's plausible
  "fix" one at a time and re-running the fresh-world location gate caught **9 of 12**; the three misses
  were explained by the data (no running entry has `m_exteriorRadius == 0`, none has a radius over 32,
  no alt-biome sets `m_requireNeighbor`), so those three claims rest on IL reading and are labelled that
  way rather than as "validated". The full table is in valheim-worldgen
  `zones-locations-vegetation.md` section 3.7.
- **`insideUnitCircle`'s trig flavour is confirmed, not discriminated.** `(float)Math.Cos((double)a)`
  and `MathF.Cos(a)` differ on 0.2586 % of the angles `Range(0f, 2*pi)` can produce, always by 1 ULP.
  The 8 recorded pairs agree with both. The fresh-world gate now passes with the double form and
  exercises ten of these draws per candidate point, which is a strong confirmation - but nobody has
  re-run the gate with the `MathF` spelling to watch it fail, and until someone does, it stays
  **Unverified**.
- **`Mathf.FloatToHalf`'s algorithm** is not read, only its tie-break measured (640 real midpoints
  across two seeds, plus an adversarial set). Its NaN payload differs from .NET's.

## 4. Measured cost (AMD Ryzen 7 9800X3D, 8 cores / 16 threads, 2026-09-23)

**The single source for every throughput number is the project's `docs\measurements.md`**, with its
machine-readable sibling `docs\measurements.json` (schema `seedlab-measurements/1`). Every figure in
it comes from **one** measurement pass on the shipped CLI, 2026-09-23 14:26, on a machine with
`vseed.exe` `AD5139B5F093E2C5...`; the pass also carries the per-run audit trail (410 invocations)
under `runs_raw`. Quote that file. The lines below are only enough to know whether you need it, and
if one of them ever disagrees with it, **it is wrong and the file is right**.

- **Biome-only (T2), whole world, 16 workers**: 20,957 seeds/s at G384 (2.4 days for 2^32) falling to
  41.9 seeds/s at G12 (3.2 years). Thread scaling is 6.4-7.0x at 8 workers and 9.9-11.3x at 16, so
  `Calibrate`'s 10.5x assumption is sound at every grid in the ladder.
- **Height (T3) and rivers (T4)**: 53 seeds/s at G384 and 8.4 at G12 on 16 workers - both sit behind
  the lake/river/stream pre-generation, a fixed ~150 ms per world that no grid makes cheaper.
  `river_count` does not read the grid at all (identical values at G384 and G12) but a rivers-only
  run is still priced by it, ~6x.
- **Locations (T5), 16 workers**: 9.5 seeds/s for one boss, 8.9 for all seven, 8.6 for bosses and
  traders, **1.9 for the full 183-entry placement**. At one worker that is 1.09 s per seed for one
  boss and 4.84 s for all 183, so **~1.1 s per seed per core is the floor for any location
  question** on this build.
- **Region**: the largest lever there is. The same `area_within radius 1000` goal at G12 on 16
  workers runs **81.5x** faster with the region restriction than with `--no-prefilter`, and produces
  **identical per-seed values** over 2,000 seeds - a disc-bounded metric cannot be changed by a cell
  outside the disc.
- **Shipped presets at the default `--mode balanced`** span `custom` at 2,405 seeds/s (20.7 days)
  down to `dungeon-delver` at 3.67 (37.1 years). Read a preset's *hit rate* beside its rate:
  `mountain-home` is three orders of magnitude faster than `dungeon-delver` at a **finer** grid
  because its must-goals reject almost every seed at the cheapest tier.
- `--mode full` (16 workers) measured **1.59x** the balanced rate on the G24 biome query, not 2x.
- **Peak working set** is not small: ~1 GiB for a preset at the shipped 8 workers, **2.0-2.7 GiB**
  for the T5 location tier at 16.

**Corrected here, 2026-09-23:** this section used to say `--dry-run` "over-projects by 1.5x on a
heavy query and 35x on the cheapest". Measured over eight tiers with every worker fed, **8 of 8 land
inside 2x and the worst is 1.23x**; on the cheap G384 biome query it *under*-projects by 2x. The
independent verifier's "4 of 5 inside 2x, worst 3.26x" does not reproduce either. The most plausible
cause of both is the trap in the next paragraph. It also quoted "39.8x" for the region lever, from a
different goal and radius; use the 81.5x above, and quote the goal with it.

**The trap that produced those numbers, and nearly produced this pass's:** one worker computes a
whole `--block-size` block, so a *short* run whose seed count divided by the block size is below the
worker count leaves most workers idle. At the shipped default of 256 a 540-seed G12 run is two
blocks; measuring it reported **1.04x** scaling from 1 to 8 workers where the real figure is
**6.36x**. Any benchmark shorter than a few thousand seeds must set `--block-size` so every worker
gets several blocks. **Fixed in the tool on 2026-09-24:** with no size given, `vseed search` now
sizes blocks so every worker gets at least 4, and says so in the plan; an explicit size is kept and
warned about. **Corrected the same day: a `--budget` is not a floor.** It is checked only when a
worker is about to take a block, and a taken block is always finished, so the bound is an OVERRUN of
up to one block (plus worker start-up and the final write) - `all-traders --budget 20s` at 256 x 8
ran **355 s** for that reason - but a run can stop at **0 seeds** (measured with `--budget 0.001s`).

**Unverified:** the per-stage breakdown this section used to carry (pre-generation 0.31-0.44 s, the
2048x2048 point grid 1.44-1.54 s, placement 6.5-6.8 s for all 183 types) has **not** been
re-measured since the AVX2 vectorisation, and it is inconsistent with the end-to-end figures above -
a whole one-boss seed now costs 1.09 s on one core, less than the point grid alone was said to cost.
Re-measure the stages with `vseed bench` before quoting any of them.

One preset is rare enough to plan around: `mountain-home` matched **0 of 217,600 seeds** on
2026-09-23 (rule of three: < 3 in 217,600, <= 0.0014 % at 95 %), and 0 of 256 again in the
measurement pass.

## 5. Open ends

- The `insideUnitCircle` negative control above.
- **Corrected 2026-09-23:** `--keep N` **is** now a real cap on the results file through the CLI and
  the web UI, and the bounding, rotation, resource-mode, refusal and GUI-parity work in
  `references/decisions.md` is built and reachable from shipped commands. The proving commands are in
  the project's `CHANGES-FOR-THE-USER.md`. `src\SeedLab.LocationOracle` is now in the README layout
  table too.
- **Still open, and named in the project's `docs\limits.md`:** the write-path tripwire test (item 9 of
  the decisions - the "SeedLab writes nothing outside its cache root" invariant rests on an IL audit
  and hand-checked listings, not on a failing test); `--strategy funnel|sample` as named strategies;
  a shipped calibration sample for rarity
  (`vseed calibrate` does not exist); CSV missing the per-goal grid/censoring fields;
  `vseed search --json` writing nothing to stdout on a refusal; and the interactive `y/N`
  confirmation branch, which no harness can reach because stdin is always redirected.
- **Closed 2026-09-23 — the D4/D5 count rules are no longer deferred.** They ship against
  `data\1.0.15-59f53fb5\count-sample.bin`, the raw 5 000-seed x 183-type `placed` matrix; both
  WARN-DEGENERATE only, and both silent when the sample is missing or its build tag disagrees with the
  atlas's. Fired and negative-controlled live; see `references/history.md`, top entry. The sample's
  provenance re-check against the current build is a **spot check** — 3 seeds x 183 types = 549 cells,
  0 differing — not a re-run of the 5 000. **On 1.0.16** (2026-09-26) the 1.0.15 sample and atlas were
  re-stamped, not rebuilt (no tool can rebuild them): 251 rows / 45,933 cells re-computed on 1.0.16 by the
  builder and 100 rows / 18,300 cells by the review, 0 differing (history.md, "the 1.0.16 ground truth").
- Cross-architecture exactness is untested: the port is bit-exact on x64, and `Math.Sin/Cos/Atan2/Pow`
  plus float evaluation could differ on arm64. The machine self-test **is** now wired and fails closed
  there (271 numerics checks + 263,780 recorded native values, stamped in `<cache>\selftest`,
  demonstrated by altering one recorded hash and watching `vseed seed` exit 1).
- **Open since 2026-09-26** (history.md has each):
  - **Fixed in the alpha 0.2.0a, each still awaiting its review and fix phases** (the review has not run yet;
    history.md, "0.2.0a"):
    - `vseed selftest` passed silently on an incomplete `groundtruth\` (section 2.0): `f8a8f4d` makes it fail.
    - The rounding defect: `Utils.FloorToInt`, `RoundToInt` and `WorldSpaceToMapSpace` were ported with a float
      rounding the game does not do, so a point a hair from a zone edge or a 12 m sector line could get the
      neighbouring zone, a map pixel one off, or - rarely, never seen - a different location answer. `0b31717`
      computes them as the game's IL does, proved by `tools\SeedLab.MonoProbe` and 196 `rounding` checks (46 fail
      against the old code). No gate discriminates it: none of the ground-truth worlds hits the band.
    - `WorldFingerprintReference.json` was stamped 1.0.15, so L4/L5 were "computed but not compared" on 1.0.16:
      `3412934` re-recorded it on 1.0.16 (only `recorded_utc`, `data` and a new `note` changed; all 320 digests
      equal the 1.0.15 recording), and `selftest --report` now compares all 40 digests of its 8 seeds.
  - CheckerSelfTest T7 (`Vendor_BlackForest count at_least 2` expects Ok, gets WarnDegenerate) is a stale
    expectation, 18/19 on both builds, and nothing runs it.
  - `CountSample._cached` is process-wide, not keyed by the atlas's build (harmless today).
  - The atlas's `absence` block is not derived from `count-sample.bin` (4 of 15 rows differ by 1-2 seeds);
    warn-only.
  - `vseed serve --selftest` was not re-run on the 1.0.16 data.
