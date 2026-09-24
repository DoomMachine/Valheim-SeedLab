# SeedLab: the user's decisions about search, output and resources

**These are decisions, not suggestions.** DoomMachine made them on 2026-09-23 over several rounds of
questions, after a disk audit of the search engine. Treat this file as the record of what was agreed,
so nobody re-litigates it.

**Status, updated 2026-09-23 after the wiring pass:** sections 1, 2, 4, 5, 6, 7, 9, 10, 12 and eight
of the nine must-fix defects in section 11 are **built and reachable from shipped commands** - the
proving command for each is in the project's `CHANGES-FOR-THE-USER.md`, and the measured numbers that
replace section 0's are in `docs\measurements.md`. Still unbuilt: **section 3's named
`--strategy funnel|sample`** (what ships is `search.screen` plus the scan order, doing the same work
in the small, with no funnel-ratio report), **defect 9's write-path tripwire test**, and GPU
(section 8, rejected on purpose). Section 0's record-size law and the 7.36 TB worst case are the
*pre-fix* measurements: that command is now refused before it starts. (The original lived in a session scratchpad, which does not survive; it was copied
here on 2026-09-23. An earlier copy was lost once already when an agent cleaned its scratch folder.)

The user's framing, in their words: *"we can have unrestricted functionality without the possibility of
crashing a file system or filling up a drive"*, *"this needs to work as intended"*, and *"any questions
or unclear matters should be asked as opposed to assuming"*.

Contents: 0. The measured facts · 1. Result bounding · 2. At the ceiling · 3. Location searches ·
4. Slices · 5. Segmented output · 6. Sampling · 7. Resource modes · 8. GPU · 9. Goal model ·
10. Cost transparency · 11. Must-fix defects · 12. GUI parity

---

## 0. The measured facts these decisions rest on

From the disk audit, all measured on this machine:

- A result record is **1,709.6 B in JSONL** (law: about 54 + 150.5 x goal-count), **383.0 B in CSV**
  (about 75 + 28 x goals), 1,712.6 B in JSON-array. Record size is driven by **goal count**, not by the
  sampling grid.
- **`--keep N` does not cap the file today**: every passing seed is streamed to disk. Worst case
  measured - `balanced` and `custom` ship with **zero** must-have goals, so they match 100 % of seeds,
  and `vseed search custom --all --out results.json` would produce a single **7.36 TB** file.
- Projections: 200,000 seeds = 26-342 MB; 10 M = 1.3-17 GB; 4.29e9 = 0.56-7.4 TB. JSONL crosses 1 GB at
  about 585,000 seeds.
- A **hard kill lost everything**: 300 s of 16-thread work left a 0-byte results file and no
  checkpoint, because nothing reaches disk until a whole 256-seed block completes *and* the collector
  emits it in strict order. `--checkpoint-every 30` does not mean what it says.
- A hard kill can also leave a **torn final record** and an orphaned `<ckpt>.tmp`.
- Checkpoints are written **unconditionally** into the CWD (`vseed-search.ckpt`) even when resume was
  never asked for, and are never deleted.
- `vseed map` with no `-o` writes `map-<seed>.png` (3.2 MB) into the CWD, so renders accumulate.
- Verified clean at IL level: `SeedLab.WorldGen`, `Seeds`, `Locations`, `Contracts` and the tests
  reference **zero** `System.IO` members; `Saves`, `Data` and the web server are read-only. There is no
  per-seed disk garbage anywhere - the problem is entirely the output layer.

## 1. Result bounding - hybrid, bounded by default

| Mode | Flag | Behaviour |
| --- | --- | --- |
| Bounded (**default**) | `--keep N`, default **1000** | a bounded best-N heap in memory; **only those N are ever written**. Disk is O(N) and independent of seeds scanned |
| Unbounded | `--keep all` | every match streamed, with section 5 rotation mandatory |

`--keep` must become a real cap on the *file*, not just an in-memory table. Bounded mode must still
report the **true total match count** ("top 1000 of 131,076", never "1000 matches"). Tie-break at the
cut line: score, then seed ascending, so a re-run is byte-identical.

## 2. At the ceiling - stop by default, evict only on confirmation

1. **`--on-limit stop` (default)** - refuse to start when the pre-run estimate exceeds the budget or
   free space; during the run stop cleanly at the ceiling, flush, checkpoint, print the exact resume
   command.
2. **`--on-limit evict`** - keep running, dropping the lowest-scoring records. Needs an interactive
   y/N (or `--yes`), prints what it will do beforehand, and the final report must state how many
   records were dropped and the score at which dropping began.

Never implement "warn and keep writing until the disk fills".

## 3. Location searches - both strategies, chosen per run

1. **`--strategy funnel`** - cheap terrain/biome goals over the whole range first, then exact placement
   on survivors only. Must measure and report the funnel ratio and the resulting stage-2 cost **before**
   stage 2 starts, and confirm if it exceeds the budget. Reference table the user has seen: survivor
   ratio 1e-6 -> 4.5 min; 1e-5 -> 45 min; 1e-4 -> 7.5 h; 1e-3 -> 3.1 days; 1e-2 -> 31 days.
2. **`--strategy sample`** - N seeds drawn without repetition from the full space, always reporting
   "covered X.XXXX % of all 4,294,967,296 worlds". Never described as exhaustive.

Default for a query with location goals and no strategy: `funnel` if it has cheap goals that can filter
(say so), otherwise `sample`, with the reason stated.

## 4. Slices - first class, with cleanup after each

A **slice** is a contiguous run of blocks sized to about 30-120 s of work (auto from the measured rate,
`--slice N` overrides). After each slice, in order: merge hits into the bounded heap (or append and
rotate) -> flush and fsync -> write the checkpoint (temp + rename) -> delete that slice's intermediates
-> print a one-line summary. **Nothing per-seed is ever written**, so "delete the data for discarded
seeds" is satisfied by construction - say that plainly rather than pretending a cleanup step is doing
work it is not.

## 5. Segmented output for unbounded runs

Decided: **1 GB segments, JSONL + gzip.**

- `--out results.jsonl --rotate 1GB` -> `results.0001.jsonl.gz`, ... plus `results.manifest.json`
  (segment list, seed ranges, record counts, sizes, hashes). The manifest must make ~7,000 segments
  navigable.
- `--compress gz` on by default for rotated output (JSONL compresses 8-15x).
- `--reduce top:N` - reduce a segment when it closes, keep the reduced output, delete the raw segment.
- **Refuse `--rotate` with `--format json`**: a split JSON array is not valid JSON. Line-oriented only.
- **Refuse per-segment reduction for non-mergeable criteria.** Top-N by score, counts, sums, min/max
  merge exactly; medians, percentiles and "most diverse N" do not, and a wrong answer there is silent.

## 6. Sampling - ship the whole ladder, auto-pick by default

SeedLab's grids share the game's extent (2048 x 12 m = 24,576 m), so they line up with the reference
site's:

| Option | Spacing | Samples/seed | Relative time | gaming.tools equivalent |
| --- | --- | --- | --- | --- |
| G384 | 384 m | 64x64 = 4,096 | 1x | "Fast (64x64)" |
| G256 | 256 m | 96x96 = 9,216 | ~2.25x | "Balanced (96x96)" |
| G192 | 192 m | 128x128 = 16,384 | ~4x | "Precise (128x128)" - their finest |
| G96 | 96 m | 256x256 = 65,536 | ~16x | - |
| G48 | 48 m | 512x512 = 262,144 | ~64x | - |
| G24 | 24 m | 1024x1024 = 1,048,576 | ~256x | - |
| G12 | 12 m | 2048x2048 = 4,194,304 | 589x measured | the game's own grid |

Sampling changes **time and memory, not disk** - do not claim otherwise. All three selection behaviours
ship; **auto-pick per query is the default** (coarsest grid provably safe for the goals present, finer
automatically for island / small-feature / tight-threshold goals), displaying the choice and its cost
and allowing an immediate override. A coarse pass must never produce a false negative.

## 7. Resource modes and hardware compatibility

The user: *"the software will run on other machines, which may not be as beefy as mine"*, and such a
tool *"is not meant for terribly heavy use while the game is running"*.

| `--mode` | Workers | Priority | Intent |
| --- | --- | --- | --- |
| background | ~25 % of logical cores, min 1 | BelowNormal | the user is playing or working; must never be felt |
| **balanced (default)** | ~50 %, min 1 | Normal | machine stays usable - the safe default anywhere |
| full | all, subject to the memory guard | Normal (never High/Realtime) | a dedicated run |

**Auto-throttle: yes.** If Valheim (or another recognised heavy app) is running when a search starts,
drop to background automatically and print one line saying so and how to override
(`--mode full --ignore-running-game`). Re-check periodically; announce any change.

**Probe and guard at every start:** logical/physical cores, total and available RAM, free disk on the
output volume. Workers = min(mode share, floor(available RAM x 0.5 / per-worker footprint at the chosen
grid and tier)), never below 1; if memory forces fewer workers than the mode asks, print both numbers.
Refuse to start, showing the arithmetic, when the estimate cannot fit in RAM or disk.

**Cross-architecture exactness** (a correctness issue, not a speed one): the port is bit-exact on x64.
On arm64 or anything else, `Math.Sin/Cos/Atan2/Pow` and float evaluation could differ in the last bit
and move a biome at a threshold. The startup self-test against the recorded goldens must run on first
use on a new machine and **fail closed** on divergence.

## 8. GPU - not now

CPU SIMD first: vectorising the Perlin inner loop with `Vector256` is bit-exact (IEEE per lane, no FMA
contraction), needs no dependency and helps every machine. Reassess afterwards with measured numbers.
If it is ever revisited: the user's GPU is an **AMD Radeon RX 9070 XT** (no CUDA; OpenCL, DirectML and
D3D12 present), and a GPU path could only ever be an approximate **screening** tier with every survivor
re-verified exactly on the CPU - GPUs fuse multiply-add and approximate transcendentals, and consumer
AMD FP64 runs at about 1/32-1/64 of FP32 while this port is double-heavy.

## 9. Goal model, and the refusal rule

Per goal: target; "look for" (nearby / keep far away / number nearby) or requirement (at least / at
most); value; importance (must-have = filter, nice-to-have = scores); and, in an expandable section, a
**weight** (1 = normal) and a **"when matches are equally good"** preference (recommended for this goal
/ no extra preference / prefer closer / farther / smaller / larger). World-shape goals add "measure
world shape within (km)" (default 10.5); island count adds a minimum island size (km2). Two output
numbers: a **match percentage** and a **quality score** that breaks ties.

**The refusal rule (explicit user instruction).** When a query is all must-haves with no preferences and
no nice-to-haves, "top N" is meaningless and returning the first N found would waste the whole search.
The user: *"it is better to refuse with a message on how it can be corrected."* So detect it, refuse,
and name the concrete fixes - add a nice-to-have goal, set a preference on an existing goal, or pass an
explicit flag to accept first-N-in-scan-order.

## 10. Estimates, metrics and cost transparency

- **Before every run** (not only `--dry-run`): seeds and % of 2^32; the tier answering each goal; the
  measured per-seed cost; estimated wall time; estimated peak memory; estimated output size *with the
  assumption it rests on*; free space; verdict (fits / tight / refused).
- **Warn and confirm** when estimated time > 1 h, or seeds > 100 M, or estimated output > 1 GB, or
  output > 10 % of free space, or `--all` (always).
- **During:** elapsed, rate, ETA, seeds done and % of 2^32, hits found and kept, bytes written,
  projected final size, free space - at most twice a second, `--progress none` for scripts. Re-project
  after every slice as the hit rate sharpens; label approximations as approximations.
- **Per-goal cost class** in CLI and GUI (instant / cheap / expensive / very expensive), and in
  particular that **one location goal drags the whole query into the seconds-per-seed tier**.
- **After:** the same numbers, the resume command if unfinished, and what was deleted versus kept.

## 11. Must-fix defects the audit found

1. `--keep` must actually cap the file (section 1).
2. Flush and checkpoint on a **time** basis, not only on ordered block completion - a hard kill must
   never lose 300 s of work.
3. On resume, truncate a torn results file back to the checkpoint's `results_length`.
4. Clean up `<ckpt>.tmp` orphans on the next run; delete the checkpoint after a run completes.
5. Do not write a checkpoint unless the run warrants it, and never into the CWD by default - use the
   cache root.
6. `vseed map` must not default to writing into the CWD with an accumulating name.
7. A `vseed clean` command, a report of current disk use, and telling the user that the 51 MB dumper
   folder in their profile is redundant now that `data\` holds a verified copy.
8. Warn **before** the run when a query matches ~100 % of seeds (zero must-have goals).
9. A regression test that fails if any command writes outside the allowed set - a tripwire that is not
   tested is a suggestion.

## 12. GUI parity (explicit user requirement)

*"everything must also be doable from the web-interface (GUI) as opposed to having CLI-only
operations."* Every flag, mode, sampling option, strategy, bound, rotation setting, estimate, warning
and confirmation above must be reachable and legible in the web UI, mapping onto the **same query JSON**
as the CLI, so the two are the same thing.
