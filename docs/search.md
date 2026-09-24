# The search engine — `src\SeedLab.Search`, `vseed search` / `explain` / `presets`

## What it does

You write what you want as a declarative query; the engine scans the int32 seed space and writes the
best matches to disk. There is no 20,000-seed cap: the only limits are the ones it prints before it
starts — and what it costs on disk is bounded by what you asked for rather than by what it finds.

```
vseed presets list                           what ships, and what each one costs
vseed presets show gentle-start > mine.json  a working query to edit
vseed search mine.json --seeds 200000 --keep 20 --out hits.jsonl --yes
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
- **A file another program holds does not end the run** (since 2026-09-24). A virus scanner, a
  sync tool or an editor that has the checkpoint open makes its save fail; the save is retried for
  about 1.6 s, and a save that still fails is a warning naming the file and the probable cause - the
  run goes on and the next checkpoint tries again. The last save of a run that stops early waits
  about 15 s, and if it still fails the run reports it instead of dying: an older checkpoint on disk
  is still a correct resume point, and the save can be tried again. (When no save of the run ever
  worked there is no older checkpoint, and the report says that resuming starts from the beginning.)
  A bounded run's snapshot and checkpoint change together or not at all: each save writes the
  snapshot under whichever of `<ckpt>.top` / `<ckpt>.top2` the checkpoint on disk does not name,
  then renames the checkpoint naming it, and a snapshot whose `next_block` or match count disagrees
  with its checkpoint is refused on resume. (Before, a failed or killed save left a newer snapshot
  beside an older checkpoint, and the resume duplicated 12 and lost 12 of 50 kept records.) Which
  generation the checkpoint on disk names is known, never guessed: a resumed run takes it from the
  checkpoint it loaded, and a fresh run reads the stale file it replaces - a held one that cannot be
  read fails that save before anything is written (review of 2026-09-24: guessing wrote over the
  named snapshot, and the next `--resume` was refused).
- **The block is the resume granularity, and ONE worker computes a whole block.** A block's wall
  time is `block size × per-seed cost`; the thread count does not divide it. At 12 m over the whole
  world a seed is about 4.2 s, so a 256-seed block is ~18 minutes that no checkpoint interval can
  shorten. For a finer resume point use `--block-size 16` at T3, or `4` when the query places
  locations.
- **The block size is automatic unless you give one** (since 2026-09-24): 256, or smaller when that
  would give a worker fewer than four blocks, so a short run no longer leaves workers idle —
  `--seeds 512` on 8 workers is 32 blocks of 16, where the old fixed 256 was 2 blocks and 6 idle
  workers. The plan's `block size` line says which rule applied and why. A size you give
  (`--block-size`, `search.block_size`, or the web page's Block size box) is kept, with a warning
  that counts the idle workers when it leaves any; a resumed run keeps its checkpoint's size on any
  thread count, and a different size on `--resume` is refused before the prompt — for a funnel's
  stage 2 at its gate, once stage 1 has run (or its survivor list is reused), because stage 2's
  checkpoint is recognised by that list. A completed
  run's results file is the same bytes at every block size (`proof blocks`), so none of this
  changes an answer.

## Before every run, not only `--dry-run`

Every run prints a plan: the grid and the reason for it, the region, the tier per goal, the coverage
as a fraction of 2³², the worker count with its arithmetic, the block size (and why, when it is not
the default 256), the memory budget, the **ceiling on the results file**, free space, the checkpoint
path, and with a `--budget` how far past it the run can go. Then one of three things happens.

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

(That gate was captured before 2026-09-24, when stage 2 still took the query's fixed block size.
Stage 2 is now sized by the same automatic rule as the main run, over the survivor count, and the
gate prints a `stage 2 block size` line saying so: those 16 survivors would be 16 blocks of 1, with
every one of the 8 workers busy. A size you give is kept for stage 2 too, and the gate warns when it
leaves workers idle. With a `--budget`, each stage gets the whole budget, and a stop in stage 1 -
Ctrl-C or the budget - ends the run, on the page as in the terminal.)

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

**Stage 2 checkpoints at the run's own checkpoint path** - the file the plan block names, beside the
survivor list - so `--checkpoint`, `--cache-dir` and `$SEEDLAB_CACHE_DIR` move both halves together,
in the terminal and on the page. Until 2026-09-24 stage 2 always used `%LOCALAPPDATA%\SeedLab\checkpoints`,
whatever those said. So a `--resume` that finds no checkpoint at the run's path also looks at that old
location: a file there that is this run's stage 2 (it passes the same identity check a resume makes)
is moved to the run's path with its kept-results snapshot, and the terminal prints both paths; any
other file there is left exactly as it is, and a warning says where it is and why it was not used.
(The page never resumes, so it never looks.)

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
`%LOCALAPPDATA%\SeedLab`): checkpoints, rendered maps, web tiles, run manifests, scratch, the
self-test stamp and the session log (`logs\vseed.log`). `vseed clean` reports it and empties it.

This is the headline change from earlier builds, where `--keep` capped an in-memory table while
every match was streamed to disk: `vseed search custom --all --out r.json` would have written a
measured **7.36 TB**. It cannot any more — the same command is now refused before it starts.

## A file another program holds (2026-09-24)

Windows refuses to replace a file while any other program has it open, whatever that program asked
for — a virus scanner, a sync tool, a search indexer, an editor or a viewer. A search used to die of
it an hour in, with `Access to the path is denied.` and no file named. What the terminal does now:

- **Before a seed is scanned** it checks what the run will use: the results file (which a spreadsheet
  may hold open) and its folder, the checkpoint's folder, and on `--resume` the checkpoint, its
  kept-results snapshot and a funnel's survivor list (plus the data's `manifest.json` when the query
  places locations). The checks create and truncate nothing. They run after the preflight's own
  refusals, so a query that cannot be answered is refused for that reason first. A check that fails:
  - in a terminal: the diagnosis and `[r]etry / [a]bort?` — `r` checks again, `a` stops (exit 3);
  - `--json` or stdin redirected: `REFUSED - SeedLab cannot use a file this run needs.`, the
    diagnosis, `Nothing was scanned and nothing was written.`, exit 3 — never a prompt a script
    would hang on;
  - `--dry-run`: a `warning:` per file, and the estimate goes on.

  The startup block's line `file access checked: N paths OK; integrity confirmed (...)` counts the
  cache root's folders and these, and adds what this process has verified: the self-test, the data
  files matched against `manifest.json`, and the DATA-STAMP. **A check that passed is a fact about
  that moment only** — it cannot stop a scanner opening a file later. A rotated run
  (`--keep all --rotate`) also checks its manifest, `<results stem>.manifest.json`.
- **What a later failure says about the start check.** "It passed SeedLab's access check at
  <time>, so something changed after that" is said only when the file ITSELF passed the check and
  the same check fails when the failure is diagnosed ("Its folder passed ..." when it was the folder
  that did and does not now). The write check opens a file sharing everything, so a program that
  has it open while letting others write passes it and still blocks the rename; a file that passed
  and passes again, and is in use, gets "but a program that has the file open while letting other
  programs write to it passes that check and still stops SeedLab replacing it - so that program may
  have had it open since before then". A file whose folder alone was checked - the checkpoint of a
  run without `--resume`, the snapshots - gets no note.
- **During the run** a checkpoint save that still fails after its ~1.6 s of retries is printed as
  `warning: the checkpoint could not be saved at <time>. SeedLab could not save <file>: <why>. ...`
  (the progress line is ended first), then every 10th failure in a row, then
  `warning: the checkpoint was saved again at <time>, after N failed saves`. The run goes on; the
  report adds `checkpoint saves  N failed along the way ...`.
- **The last save** of a run that stops early (Ctrl-C, the budget, the ceiling) waits ~15 s, and
  its first failed attempt prints `warning: saving the last checkpoint: <file> is busy - another
  program may have it open. SeedLab keeps trying for up to 14.6 s.` If it still fails:
  `error: the last checkpoint of this run could not be saved.` with the file, the cause, and what is
  on disk, one of three:
  - `The resume point on disk is from block N (..., saved hh:mm:ss); resuming starts there and
    repeats the K blocks after it.`
  - `The checkpoint already on disk (saved hh:mm:ss) could not be read just now either - the same
    program has it open - ...` - it is still the resume point (the report keeps the resume command),
    and `--resume` continues from it once that program lets go (tested: the same bytes);
  - `There is no checkpoint on disk that a resume could use, so resuming would start the run from the
    beginning.` - no save of this run worked, and nothing else is there.

  In a terminal, `[r]etry / [g]ive up?` repeats the identical save as often as asked; answer `g` to
  give up. Ctrl-C at that question does not end `vseed`: the results file is still finished and the
  report printed. The exit code is not changed: an older checkpoint is a correct resume point, and
  resuming from it produces the same bytes (tested).
- **A finished run whose checkpoint could not be deleted** says so, and that the file is safe to
  delete — never "kept: this run has not finished".
- **A rotated run whose manifest is held when it finishes.** Every record is on disk by then, so the
  report is printed; `error: the run finished and all N records it found are written, in K segment
  files beside <results> - only the manifest that lists them could not be saved ...` follows (after the
  ~15 s wait, which is said as it starts), a terminal offers `[r]etry / [g]ive up`, and the command
  exits **3**: the one file that indexes the segments is missing or describes an earlier moment. A
  manifest held at a rotation costs that rotation nothing (one attempt); the next flush writes it,
  even with no segment open.
- `--json` carries `checkpoint_retired`, `resumable_checkpoint` (null when nothing on disk can be
  resumed), `checkpoint_leftovers`, `checkpoint_error` (`path`, `checkpoint`, `problem` — `in_use`,
  `read_only`, `no_permission`, `folder_missing`, `disk_full`, `unknown` — `message`, `run_block`,
  `on_disk_block`, `on_disk_unreadable`, `attempts`), `results_error` (`path`, `problem`, `message`,
  or null), `failed_saves` and `warnings`. The page's `checkpointError.problem` uses the same values.
- A failure anywhere else in a command — a file held when the results file is opened, a map's
  output — ends it with exit 3 and the file named when the error carries its path (a path-less
  access denial says "a file or folder" and points at the session log). A full drive is said as one,
  exit 3. Any other input/output error still says "this is a bug", exit 4. Every line of it is also
  in the session log, `<cache root>\logs\vseed.log`, with the exception detail the terminal leaves
  out.

## The grid is part of the answer

`--grid <m>` changes the answer of every goal measured on the sampling grid, not just the speed, so the
grid is recorded per goal in the results file, and the plan names those goals in a warning. It does not
change a location or group goal: placement draws from the game's own 2048 × 2048 @ 12 m point grid
whatever `--grid` says, so those goals give the same numbers at every grid and get no grid warning. A
query of nothing but location goals gets a plan note instead, because the grid is still part of its
identity: it moves the run hash, and with it the checkpoint, the survivor list and - for a shuffled
run with no `search.key` over part of the range, or one a `budget.wall` can stop before it covers the
range - which seeds are visited.

Under `--screen auto` (the default) the engine:

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

Names come from the dumped localization table and from nothing else — 31 of the 183 placed types
have one (8 bosses, 3 traders, 3 map-pin labels, and 17 dungeon entrances named by the caption on
their door), and the other 152 answer only to their prefab because the game names them nothing. A
caption several prefabs share ("Burial Chambers", "Infested Mine", "Putrid Hole") names each of them
but picks none as a target: `location:Burial Chambers` is refused with the prefabs listed, and
`group:burial_chambers` asks for all of them. `vseed data --names` lists every spelling the engine accepts.

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
  hard kills and resumes, and (`proof blocks`) that a completed run's file is the same bytes at
  every block size and that a run resumed on another thread count keeps its checkpoint's size.
  `SeedLab.Search.Tests` does build sessions, run the preflight and the grid policy, and run a scan
  with no sink; only its sections 14, 15 and 17 write a results file and resume a checkpoint from
  disk, to prove where a funnel's stage 2 checkpoints, what a checkpoint another program holds does to
  a run (warnings, the last save, the `.top`/`.ckpt` pair), and - section 17, through the built
  `vseed` and `vseed serve` - what the terminal and the page say and do about a held file, and the
  session log. None of its checks hard-kills a run or exercises the
  bounds, rotation and ceilings, so this is a separate suite on purpose.
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
- **`--budget` bounds an overrun, not a floor.** It is checked only when a worker is about to take a
  block, and a block already taken is finished, so a run can end up to one block of one worker's
  time past the budget, with at most one block per busy worker in flight, plus the workers' start-up
  and the final write; the plan prints both numbers for the run in hand, and a funnel's gate prints
  stage 2's. It can also stop a run at 0 seeds (`--budget 0.001s` does), which
  then reports 0 % coverage.
- **A rate measured with idle workers is not the machine's.** One worker computes a whole block, so
  a run with fewer blocks than workers — a `--block-size` you gave, the tail of a resumed run, a
  budget that stopped it early — runs at its busy workers' rate. The report labels it `(2 of 8
  workers had work)` and `--json` carries `busy_workers`, `block_size` and `blocks`. Before the block
  size was automatic (2026-09-24), 400 seeds of `gentle-start` in 2 blocks of 256 measured 2.0
  seeds/s where the same preset does 7.3 with every worker fed.
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
