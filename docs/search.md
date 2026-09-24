# The search engine — `src\SeedLab.Search`, `vseed search` / `explain` / `presets`

## What it does

You write what you want as a declarative query; the engine scans the int32 seed space and writes the
best matches to disk. There is no 20,000-seed cap: the only limits are the ones it prints before it
starts — and what it costs on disk is bounded by what you asked for rather than by what it finds.

```
vseed presets list                           what ships, and what each one costs
vseed presets show gentle-start > mine.json  a working query to edit
vseed search mine.json --seeds 200000 --block-size 16 --keep 20 --out hits.jsonl --yes
vseed explain -1772362158 mine.json          why that one seed passed or failed
vseed search --schema                        the query language
vseed search --metrics                       every target/metric, its tier and its status
```

New to it: [`finding-a-seed.md`](finding-a-seed.md). What the answers do *not* mean:
[`limits.md`](limits.md). What anything costs: [`measurements.md`](measurements.md).

Four things make it usable rather than a slot machine:

- **Tiers.** A goal is answered at the cheapest level that can decide it. T0 is static (no seed
  touched), T1/T2 sample a coarse biome field, T3 needs heights, T4 rivers, T5 the location
  placement. The plan prints the tier each goal reached, and `--no-prefilter` re-runs everything at
  full cost as an audit — it must produce the same result set, and that is the test.
- **Determinism.** Order (`shuffled` by default) is a Feistel permutation of the range under a key
  derived from the query hash. Same query + same key + same range = the same seed sequence, however
  many threads ran, and the results file is byte-identical across thread counts.
- **Resumability and crash safety.** Work is done in blocks; the flush and the checkpoint are on a
  clock, not on "a block happened to be emitted just now". Ctrl-C stops at the next block boundary,
  writes the checkpoint and exits 0; `--resume` continues; on resume the results file is truncated
  back to the byte count the checkpoint recorded as complete, which repairs the torn last record a
  hard kill leaves, and a bounded run restores its kept set from the snapshot beside the checkpoint.
  Verified by killing runs at three points, bounded and streaming, and comparing SHA-256: every
  resumed file was byte-identical to the uninterrupted one. The checkpoint lives in the **cache
  root**, keyed by the query hash, and a completed run deletes it.
- **The block is the resume granularity, and ONE worker computes a whole block.** A block's wall
  time is `block size × per-seed cost`; the thread count does not divide it. At 12 m over the whole
  world a seed is about 4.2 s, so a 256-seed block is ~18 minutes that no checkpoint interval can
  shorten — and on a short run it also means most of your workers have nothing to do. Use
  `--block-size 16` at T3, or `4` when the query places locations.

## Before every run, not only `--dry-run`

Every run prints a plan: the grid and the reason for it, the region, the tier per goal, the coverage
as a fraction of 2³², the worker count with its arithmetic, the memory budget, the **ceiling on the
results file**, free space, and the checkpoint path. Then one of three things happens.

**It runs.**

**It asks** — the whole space (always), over an hour, over 100 M seeds, over 1 GB of output or 10 %
of the volume's free space, `--on-limit evict`, or a grid the policy had to raise. With stdin
redirected nothing starts:

```
This run needs confirming:
  - this is the WHOLE 4,294,967,296-seed space

  Nothing is reading the keyboard (stdin is redirected), so this run is not started.
  Pass --yes to accept the points above, or change the query.
  --dry-run answers "what would this cost" without starting it.
```

`--dry-run` is deliberately **not** gated by that prompt: it prints what the run would have to
confirm, then the measured cost. A refused query still stops a dry run.

**It refuses**, exit 1, nothing scanned and nothing written, with the fix named. The four you are
most likely to meet, all captured from this build on 2026-09-23:

| refusal | what it says | the named fix |
|---|---|---|
| every goal is a must-have | there is nothing to rank by, so a bounded run would return "whichever 1,000 matches the scan reached first — an arbitrary sample wearing the name 'top 1,000'" | add a nice-to-have, change one goal to `nice`, `--accept-scan-order`, or `--keep all` |
| unbounded output that cannot fit | *"keep: all on a query with no must-have goal matches every seed, so it WILL write about 818 GB and the volume has 599 GB free"* | `--keep N`, add a must-have goal, or a bigger volume |
| a goal that excludes nothing | *"every instance that exists is already inside 1,000 m of the centre, so this goal is no longer a distance filter — it is 'a seed in which this target was placed at all'"* | ask inside the range that varies, or `--allow-vacuous` |
| a retired metric | the measurement that retired it (`coastline_length`: p90/p10 = 1.065 over 2,560 seeds) | `world:shore_area_within` with a radius, with a worked example |

And a goal no seed can satisfy is refused before the scan, citing the game constant:

```
vseed search: goal 'swamp-near' can never be satisfied by any seed.
  Swamp can never be closer than 2,000 m to the centre ... maxMarshDistance = 6000
  Refusing to scan 4.29 billion worlds for something the generator cannot make.
```

A query that matches ~100 % of seeds is not refused but **warned about before it starts**: *"this
query has no must-have goal, so EVERY seed matches it: 4,000 of 4,000. It ranks, it does not filter
— the bounded file keeps the best 50, so it is 9.99 KB rather than 799 KB."* Two shipped presets
(`custom`, `balanced-biomes`) are in that class on purpose.

## Two strategies, chosen per run

`--strategy auto|funnel|sample` (`strategy` in the web panel). `auto` is the default, picks per query
and **always says why** - the reason is printed whichever way it goes, because a default nobody can
see is a default nobody can check.

**`sample`** evaluates the seeds straight through, in the shuffled order, without repetition. It is
never described as exhaustive and the plan block always states the coverage: 600 seeds is
`1.397e-05 %` of the 4,294,967,296 worlds.

**`funnel`** measures the cheap must-have goals over the whole range FIRST, then places locations only
for the seeds that survived.

**What the funnel is actually for, stated plainly.** It is not faster than the ordinary run. The tier
ladder already skips location placement for any seed a cheap must-have has settled - that is the
`never started` line in every run's report, and it is where the saving comes from. What a staged
funnel adds is the thing the ladder structurally cannot give: a **measured** survivor count for the
whole range before a single expensive placement is paid for.

```
Funnel gate - measured, before stage 2 places anything
    stage 1 scanned      600 seeds in 0.6 s
    stage 1 kept         16  (2.667 % of them)
    survivor list        224 B
    stage 2 will place   16 worlds at 1.07 s each, measured
    stage 2 estimate     17.1 s on 1 worker
                         (8 threads are available, but 16 survivors at a block size of 256 is
                          1 block, and one worker takes a whole block - so the rest have nothing
                          to take)
```

That run then took 17.3 s. The alternative - extrapolating from a handful of calibration seeds - has
been wrong by 35x on this tool's own record, which is the whole argument for measuring instead.

**When it will not funnel, and says so.** A funnel needs something cheap to filter on AND something
expensive to defer. It refuses to pretend otherwise:

- no must-have below the location tier - *"a first stage would admit every seed and the second would
  do all the work twice"*;
- nothing that needs placement - *"the run is already as cheap as it gets"*;
- a cheap goal that is only a nice-to-have - it ranks, it never rejects, so it cannot filter;
- and when stage 1 rejected less than 90 % of what it saw, the gate says the funnel is saving very
  little here and names `--strategy sample` as the honest alternative.

**The survivor list** is the stage boundary, four bytes a seed, written beside the checkpoint. It
carries the query hash, the game build's stamp and the scanned count, and all three are checked when
it is read: a list from another query, another build, or one that was truncated is refused rather
than used. It is bounded like every other output - at a ceiling of 2 GiB, a stage 1 that admits half
the space is refused with the reason instead of writing 8.6 GB. Because it is on disk, `--resume`
skips stage 1 entirely and goes straight to the gate.

**Soundness.** Stage 1 may only drop a seed stage 2 would also have dropped, so it keeps exactly the
must-have goals below the location tier and every survivor is re-measured in full. The contract is the
one that already existed for the tiered evaluator: *a funnel run's result set must equal the same
query's `--no-prefilter` result set.* Verified over 600 seeds - funnel, sample and audit mode all
returned the same 16 seeds, with the funnel placing 16 worlds instead of 600.

**One trap, found the hard way.** Stage 1 runs a *derived* query - the original minus its expensive
goals - and `ScanPlan` derives its permutation key from the query hash, so a derived query walks a
DIFFERENT sample of the space. Before this was fixed the funnel returned 14 matches where the ordinary
run returned 16, and the two sets shared not one seed: every one of those 14 was a real match to a
question nobody had asked. Stage 1 is now given the full run's plan, and a test asserts both halves -
that an unconstrained derived plan differs, and that the constrained one agrees index for index.

## Output: bounded by default

| flag | what it does |
|---|---|
| `--keep <n>` (default 1000) | **a real cap on the file.** Only the best n records are ever written; the run still reports the true match count — `top 10 of 82 matches; 72 were not kept`. Tie-break is score, then seed ascending, so a re-run is byte-identical. |
| `--keep all` | stream every match. Wants `--rotate` on a large range. |
| `--rotate <size>` | close a segment every size (e.g. `1GB`, `200KB`) and write a `*.manifest.json`. Line-oriented formats only — a split JSON array is not valid JSON and is refused. |
| `--compress gz\|none` | gzip each closed segment (default `gz` when rotating; measured 10.0×). |
| `--reduce top:N\|count` | reduce a segment when it closes and delete the raw one. Only reductions that merge exactly across segments are accepted — medians and percentiles are refused, because a wrong answer there would be silent. |
| `--max-bytes <size>` | a hard ceiling on total result bytes. |
| `--on-limit stop\|evict` | at the ceiling: stop cleanly and print the resume command (default), or keep going and drop the lowest-scoring records (bounded runs only, and it must be confirmed). |

Everything else SeedLab writes goes to the **cache root** (`--cache-dir`, `$SEEDLAB_CACHE_DIR`, or
`%LOCALAPPDATA%\SeedLab`): checkpoints, rendered maps, web tiles, run manifests, scratch and the
self-test stamp. `vseed clean` reports it and empties it.

This is the headline change from earlier builds, where `--keep` capped an in-memory table while
every match was streamed to disk: `vseed search custom --all --out r.json` would have written a
measured **7.36 TB**. It cannot any more — the same command is now refused before it starts.

## The grid is part of the answer

`--grid <m>` changes the answer, not just the speed, so the grid is recorded per goal in the results
file. Under `--screen auto` (the default) the engine:

- **screens coarsely with a measured margin** where the metric allows one, then **re-measures every
  survivor** at the definitional grid before writing it. Every record then carries
  `screened_at_grid_m` (the coarse grid it was *found* on) beside numbers that are the fine grid's.
  Measured on this build: 20 of 20 records from `gentle-start` and from `coastal-builder` carried
  `"screened_at_grid_m": 24` beside `"grid": 12`.
- **raises the grid** when a must-have goal is one no coarse grid can decide (island counts, spawn
  island, peak, nearest-Mountain, shore), and says so in the plan — and the raised grid is what the
  run is actually compiled and measured at, not merely described as.
- `--screen off` measures once at the query's grid; `--screen-grid <m>` picks the coarse grid by
  hand; `--region <m>` restricts the measurement to a disc, which is **exact** for a disc-bounded
  goal and the largest cost lever in the tool (39.8× measured at G12 for a 1 km disc).

Eight metrics are not comparable across grids at all and say so on the record
(`grid_comparable: false` plus a `grid_note`): see [`limits.md`](limits.md).

## Metrics

`vseed search --metrics` prints the catalogue with each metric's tier and status. Two were
**retired** on 2026-09-23 and give a named refusal rather than "unknown metric":

- `coastline_length` — measured at G12 over 2,560 seeds, p10/p50/p90 = 2,625,271 / 2,712,816 /
  2,796,511 m, so the most coastal world in 2,560 has **8 % more coastline than the least**
  (CV 0.0245). A threshold on an 8 % span is not a search criterion. (It was also grid-relative in
  the strongest sense, but that was only a ranking offset; the 8 % span is what killed it.)
- `deepest_point` — same class of defect, with a stronger proof.

Their replacement is **`world:shore_area_within`** (radius required): the area of land (height
≥ 30 m) whose cell centre lies within **100 m** of a water cell centre, inside a disc of `radius`
around the centre. The 100 m band is part of the *definition* and is never a user parameter. At
radius 1,000 m it has 7× the spread of coastline (CV 0.166, p90/p10 1.53), ranks better at every
grid (Spearman 0.988 / 0.949 / 0.862 at G24/G48/G96) and is statistically independent of
`land_area_within` at the same radius. It ships in the `coastal-builder` preset.

Asking for it at a grid that cannot resolve a 100 m band, or with no radius / a radius ≥ 10,500 m,
is a **usage error** rather than a refusal — a refusal would be a claim about the seed space, and at
G96 the metric is measurably not zero (0.724× its G12 value over 512 seeds). The one provable line
is at 100 m spacing: above it no land cell can have water inside the band, so the metric is 0 in
every seed.

## Naming a place: either spelling (2026-09-24)

A `location:` target takes **the prefab or the name a player uses**. `location:The Elder`,
`location:the elder` and `location:GDKing` all compile to the same goal; matching folds case,
spaces, hyphens and apostrophes, and tolerates a leading "the".

**The prefab is still the identity.** The target is rewritten to the prefab before the query is
compiled and hashed, so the run hash, the checkpoint, the CSV column header and every record say
`GDKing` whatever was typed, and a query file written before this change hashes to exactly the same
value it always did. The substitution is announced once on stderr rather than done silently. A name
is an input convenience; it never becomes the identity.

Names come from the dumped localization table and from nothing else — 14 of the 183 placed types
have one (8 bosses, 3 traders, 3 map-pin labels), and the other 169 answer only to their prefab
because the game names them nothing. `vseed data --names` lists every spelling the engine accepts.

Two group targets come out of the same work. `group:axe_head_houses` is the two house types whose
chests can hold an axe head, and its `nearest_distance` answers the same question bobmitch's
`axeChestNearest` does. The matching `axeChestsAfoot` is **refused by name**: it needs a walkable
flood fill from the start temple, which SeedLab does not have, and answering it with a Euclidean
disc would count houses across an ocean.

## Feasibility, vacuity and rarity

Before a seed is touched, every goal is checked against a **constraint atlas** built from the game's
own asset data — 183 location types, validated against 36,829 real instances with zero violations —
which ships as `data\1.0.15-59f53fb5\constraint-atlas.json` and is stamped like every other data
file. Refusals only ever come from code or asset evidence, never from a sample; a **DATA-STAMP
mismatch disables refusals entirely** and downgrades them to warnings, because a refusal built on
another build's table would be a false statement about seeds.

The count rules that ship are D3 (a world-wide count at the cap → warn, with the proof that
`count ≤ m_quantity`), V5b (`at_most N` with N ≥ the cap → vacuous) and A1 (an absence line per
type, in exactly one of three shapes: PROOF for a code-provable absence, or a MEASURED Wilson
interval, or a MEASURED rule-of-three bound — never PROOF on sample evidence). **D4 and D5 are
deferred**, with the reason printed next to every count goal: they need per-type count histograms,
today's calibration sample holds distance percentiles only, and built against it they would never
fire while their tests passed for the wrong reason.

## What proves it correct

- `tests\SeedLab.Search.Tests` — the query language, the tiers, and **prefilter parity**, which is
  the important one: for a sample of seeds the tiered run and the `--no-prefilter` run must agree
  exactly. A prefilter that rejects a seed the full evaluation would have accepted is a silent wrong
  answer, and this is what catches it.
- `tests\SeedLab.Search.Safety.Tests` — the output layer: bounds, rotation, reduction, ceilings,
  hard kills and resumes. None of the checks in `SeedLab.Search.Tests` reaches a sink, a checkpoint
  or the grid policy, so this is a separate suite on purpose.
- The metrics it measures are the same `WorldField` / `WorldSummary` code the acceptance suite
  proves against the game's own output, so a passing seed's *numbers* are the verified ones.
- `vseed serve --selftest` includes `search panel vs the engine`: the page's own query file run
  through a direct `SearchRun`, identical in order and score.

## Traps

- **A run that did not cover the whole space did not cover the whole space.** The plan prints the
  fraction before it starts and the summary prints it again. `--all` is how you ask for the full
  4,294,967,296, deliberately out loud; `--seeds 0` is refused for the same reason.
- **`--budget` makes a run irreproducible on its own.** Its checkpoint records the `--seeds` value
  that makes it reproducible.
- **A small run under-uses the machine.** 400 seeds in blocks of 256 is two blocks, so two workers
  work: measured 2.0 seeds/s where the same preset does 7.3 with `--block-size 16`.
- **A leftover `vseed search` or `vseed serve` locks `bin\Release\`** and the next build fails with
  MSB3027 / MSB3021.
- **`--approx` allows heuristic prefilters. This build ships none**, so it currently only stamps
  `approx` on every record.
- **`--json` on a refused or unconfirmed run** writes the refusal to stderr and exits 1 with nothing
  on stdout. A JSON consumer gets an empty document, not a machine-readable refusal.
- **The new per-goal fields are JSONL-only.** CSV still carries value/pass/score/bounded per goal
  and none of `measured_at_grid_m`, `grid_comparable`, `grid_note`, `censored` or the measured
  error. Use `.jsonl` when you care how a number was measured.
- **Check the query hash in a results file** before trusting it: it is what ties those seeds to the
  query you think produced them.
