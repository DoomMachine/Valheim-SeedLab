# SeedLab search & resource design — the user's decisions (master spec)

Decided by DoomMachine on 2026-09-23 across several rounds of questions. These are decisions, not
suggestions. An earlier copy of this file lived in `scratchpad\diskaudit\` and was destroyed when an
agent cleaned up its scratch folder — keep this one at the scratchpad root.

The user's framing, in their words: *"we can have unrestricted functionality without the possibility of
crashing a file system or filling up a drive"*, *"this needs to work as intended"*, and *"any questions
or unclear matters should be asked as opposed to assuming"*.

---

## 0. The measured facts these decisions rest on

From the disk audit (all measured on this machine, not estimated):

- A result record is **1,709.6 B in JSONL** (law: ≈54 + 150.5 × goal-count), **383.0 B in CSV**
  (≈75 + 28 × goals), 1,712.6 B in JSON-array. Record size is driven by GOAL COUNT, not by the
  sampling grid.
- **`--keep N` does not cap the file today** (`SearchCommand.cs:58` says so explicitly): every passing
  seed is streamed to disk. Worst case measured: `balanced` and `custom` are shipped presets with ZERO
  must-have goals, so they match **100 %** of seeds; `vseed search custom --all --out results.json`
  would produce a single **7.36 TB** file.
- Projections: 200,000 seeds = 26–342 MB (not dangerous); 10 M = 1.3–17 GB; 4.29 e9 = 0.56–7.4 TB.
  JSONL crosses 1 GB at ~585,000 seeds.
- A **hard kill lost everything**: 300 s of 16-thread work left a 0-byte results file and no
  checkpoint, because nothing reaches disk until a whole 256-seed block completes AND the collector
  emits it in strict order. `--checkpoint-every 30` does not mean what it says.
- A hard kill can leave a **torn final record** (file ended mid-record, 94 % of bytes beyond the
  checkpoint's `results_length`) and an orphaned `<ckpt>.tmp`.
- Checkpoints are written **unconditionally** to the CWD (`vseed-search.ckpt`) even when the user never
  asked for resume, and are **never deleted** — two orphans already exist in the repo.
- `vseed map` with no `-o` writes `map-<seed>.png` (3.2 MB at the default size) into the CWD, with a
  seed-derived name, so renders accumulate.
- Verified clean at IL level: `SeedLab.WorldGen`, `Seeds`, `Locations`, `Contracts` and the tests
  reference **zero** `System.IO` members. `Saves`, `Data` and the web server are read-only. There is no
  per-seed disk garbage anywhere; the problem is entirely the output layer.

Performance baseline (16 threads, Ryzen 9800X3D): biome criteria at G384 = 10,717 seeds/s (whole space
4.6 days); at the game's native G12 = 18.2 seeds/s (7.5 years); heights/rivers at G384 = 28.6 seeds/s;
bosses+traders ≈ 1 s/seed/thread; all 183 location types ≈ 7 s/seed/thread.

---

## 1. Result bounding — HYBRID, both modes, bounded by default

| Mode | Flag | Behaviour |
|---|---|---|
| Bounded (**default**) | `--keep N`, default **1000** | A bounded best-N heap in memory. **Only those N are ever written.** Disk is O(N) and independent of seeds scanned: 1000 × 1,709 B ≈ 1.7 MB for a whole-space run. |
| Unbounded | `--keep all` | Every match streamed, with §5 rotation mandatory. |

- **This is the headline fix**: `--keep` must become a real cap on the file, not just an in-memory table.
- Bounded mode must still report the TRUE total match count, so the user sees "top 1000 of 131,076",
  never "1000 matches".
- Tie-break at the cut line: score, then seed ascending, so a re-run is byte-identical.

## 2. At the ceiling — STOP by default, EVICT as a confirmed opt-in

1. **`--on-limit stop` (DEFAULT)** — refuse to start when the pre-run estimate exceeds the budget or
   free space; during the run, stop cleanly at the ceiling, flush, checkpoint, and print the exact
   resume command.
2. **`--on-limit evict`** — keep running, dropping the lowest-scoring records. Requires an interactive
   y/N confirmation (or `--yes`), prints what it will do beforehand, and the final report must state
   how many records were dropped and the score at which dropping began.

Never implement "warn and keep writing until the disk fills".

## 3. Location searches — BOTH strategies, chosen per run

1. **`--strategy funnel`** — cheap terrain/biome goals over the whole range first (4.6 days for the
   whole space), then exact location placement on survivors only. Must measure and report the funnel
   ratio and the resulting stage-2 cost BEFORE stage 2 starts, and confirm if it exceeds the budget.
   Reference table the user has already seen: survivor ratio 1e-6 → 4.5 min; 1e-5 → 45 min;
   1e-4 → 7.5 h; 1e-3 → 3.1 days; 1e-2 → 31 days.
2. **`--strategy sample`** — N seeds drawn without repetition from the full space, always reporting
   "covered X.XXXX % of all 4,294,967,296 worlds". Never described as exhaustive.

Default when a query has location goals and no strategy: `funnel` if the query has cheap goals that can
filter (say so), otherwise `sample` with the reason stated.

## 4. Slices — first-class, with cleanup after each

- A **slice** is a contiguous run of blocks sized to ≈30–120 s of work (auto from the measured rate;
  `--slice N` overrides).
- After each slice, in order: merge hits into the bounded heap (or append+rotate in unbounded mode) →
  flush and fsync → write the checkpoint (temp+rename) → delete that slice's intermediates → print a
  one-line slice summary.
- **Nothing per-seed is ever written**, so "deleting data for discarded seeds" is satisfied by
  construction. Say that plainly rather than pretending a cleanup step is doing work it is not.

## 5. Segmented output for unbounded runs (the user's chunking proposal)

DECIDED: **1 GB segments, JSONL + gzip.**

- `--out results.jsonl --rotate 1GB` → `results.0001.jsonl.gz`, `results.0002.jsonl.gz`, … plus
  `results.manifest.json` (segment list, seed ranges, record counts, sizes, hashes).
- `--compress gz` is ON by default for rotated output (JSONL compresses ≈8–15×).
- `--reduce top:N` — the user's "analyse then discard" step: when a segment closes, reduce it, keep the
  reduced output, delete the raw segment, continue.
- **Refuse `--rotate` with `--format json`**: a split JSON array is not valid JSON. Line-oriented only.
- **Refuse per-segment reduction for non-mergeable criteria.** Top-N by score, counts, sums, min/max
  merge exactly; medians, percentiles and "most diverse N" do NOT. A wrong answer here would be silent.
- The manifest must make ~7,000 segments navigable.

## 6. Sampling — ship the whole ladder, auto-pick by default

Our grids share the game's extent (2048 × 12 m = 24,576 m), so they map onto the reference site exactly:

| Option | Spacing | Samples/seed | Relative time | gaming.tools equivalent |
|---|---|---|---|---|
| G384 | 384 m | 64×64 = 4,096 | 1× (10,717 seeds/s measured) | "Fast (64×64)" |
| G256 | 256 m | 96×96 = 9,216 | ~2.25× | "Balanced (96×96)" |
| G192 | 192 m | 128×128 = 16,384 | ~4× | "Precise (128×128)" — their finest |
| G96 | 96 m | 256×256 = 65,536 | ~16× | — |
| G48 | 48 m | 512×512 = 262,144 | ~64× | — |
| G24 | 24 m | 1024×1024 = 1,048,576 | ~256× | — |
| G12 | 12 m | 2048×2048 = 4,194,304 | 589× measured | the game's own grid |

- Sampling changes **time and memory**, **not disk**. Do not claim otherwise.
- All three selection behaviours ship; **auto-pick per query is the default**: choose the coarsest grid
  provably safe for the goals present (finer automatically for island / small-feature / tight-threshold
  goals), display the choice and its cost, allow immediate override. The other two: ask-every-time, and
  finest-affordable. All overrides present in BOTH CLI and GUI.
- The resolution study decides the safe margins per metric; a coarse pass must never produce a false
  negative (a coarse-rejected seed that would have qualified exactly).

## 7. Resource modes and hardware compatibility

The user: *"the software will run on other machines, which may not be as beefy as mine"*, and such a
tool *"is not meant for terribly heavy use while the game is running"*.

**Three modes, `--mode background|balanced|full`, DEFAULT BALANCED:**

| Mode | Workers | Priority | Intent |
|---|---|---|---|
| background | ~25 % of logical cores, min 1 | BelowNormal | user is playing or working; must never be felt |
| **balanced (DEFAULT)** | ~50 % of logical cores, min 1 | Normal | machine stays usable — the safe default anywhere |
| full | all logical cores, subject to the memory guard | Normal (never High/Realtime) | dedicated run |

**Auto-throttle: DECIDED YES.** If Valheim (or another recognised heavy app) is running when a search
starts, drop to background automatically and print ONE line saying so and how to override
(`--mode full --ignore-running-game`). Re-check periodically; announce any change.

**Hardware probe and guards at every start:** logical/physical cores, total and available RAM, free disk
on the output volume. Worker count = min(mode share, ⌊available RAM × 0.5 ÷ per-worker footprint at the
chosen grid and tier⌋), never below 1; if memory forces fewer workers than the mode asks, print both
numbers. Refuse to start, showing the arithmetic, when the estimate cannot fit in RAM or disk.

**Cross-architecture exactness (not a speed issue):** the port is bit-exact on x64. On arm64 or any
other target, `Math.Sin/Cos/Atan2/Pow` and float evaluation could differ in the last bit and change a
biome at a threshold. The startup self-test against the recorded goldens must run on first use on a new
machine and **fail closed** on divergence.

## 8. GPU — DECIDED: not now

CPU SIMD first: vectorising the Perlin inner loop with `Vector256` is **bit-exact** (IEEE per-lane, no
FMA contraction), needs no dependency, and helps every machine. Reassess GPU afterwards with measured
numbers. If it is ever revisited: the user's GPU is an **AMD Radeon RX 9070 XT** (no CUDA; OpenCL,
DirectML and D3D12 present), and a GPU path could only ever be an approximate SCREENING tier with every
survivor re-verified exactly on the CPU — GPUs fuse multiply-add and approximate transcendentals, and
consumer AMD FP64 runs at ~1/32–1/64 of FP32 while this port is double-heavy.

## 9. Goal model — parity with the reference site, plus the refusal rule

Per goal: target; "look for" (nearby / keep far away / number nearby) or requirement (at least / at
most); value; importance (must-have = filter, nice-to-have = scores); and in an expandable section a
**weight** (1 = normal) and a **"when matches are equally good"** preference (recommended for this goal
/ no extra preference / prefer closer / prefer farther / prefer smaller / prefer larger). World-shape
goals add **"measure world shape within (km)"** (default 10.5) and island count adds **minimum island
size (km²)**. Two output numbers, as they have: a **match percentage** and a **quality score** that
breaks ties.

**THE REFUSAL RULE (explicit user instruction).** When a query is all must-haves with no preferences
and no nice-to-haves, "top N" is meaningless and returning the first N found would waste the entire
search. The user: *"it is better to refuse with a message on how it can be corrected."* So: detect it,
refuse, and name the concrete fixes — add a nice-to-have goal, set a preference on an existing goal, or
pass an explicit flag to accept first-N-in-scan-order.

## 10. Estimates, metrics and cost transparency

- **Before every run** (not only `--dry-run`): seeds and % of 2^32; the tier answering each goal; the
  measured per-seed cost; estimated wall time; estimated peak memory; estimated output size WITH the
  assumption it rests on; free space; verdict (fits / tight / refused).
- **Warning + confirmation** when: est. time > 1 h, OR seeds > 100 M, OR est. output > 1 GB, OR est.
  output > 10 % of free space, OR `--all` (always).
- **During:** elapsed, rate, ETA, seeds done and % of 2^32, hits found and kept, bytes written,
  projected final size, free space remaining — refreshed at most twice a second, `--progress none` for
  scripts. Estimates re-projected after every slice as the hit rate sharpens; approximations are fine
  and must be labelled as approximations.
- **Per-goal cost class** shown in CLI and GUI (instant / cheap / expensive / very expensive) and, most
  importantly, that **one location goal drags the whole query into the seconds-per-seed tier**.
- **After:** the same numbers, the resume command if unfinished, and what was deleted versus kept.

## 11. Must-fix defects found by the audit

1. `--keep` must actually cap the file (§1).
2. Flush and checkpoint on a TIME basis, not only on ordered block completion — a hard kill must never
   lose 300 s of work.
3. On resume, truncate a torn results file back to the checkpoint's `results_length`.
4. Clean up `<ckpt>.tmp` orphans on the next run; delete the checkpoint after a run completes.
5. Do not write a checkpoint at all unless the run is resumable/long enough to warrant it, and never
   into the CWD by default — use the cache root.
6. `vseed map` must not default to writing into the CWD with an accumulating name.
7. A `vseed clean` command, plus a report of current disk use, plus telling the user the 51 MB dumper
   folder in their profile is redundant now that `data\` holds a verified copy.
8. Warn when a query matches ~100 % of seeds (zero must-have goals) BEFORE the run.
9. A regression test that fails if any command writes outside the allowed set — a tripwire that is not
   tested is a suggestion.

## 12. GUI parity (explicit user requirement)

*"everything must also be doable from the web-interface (GUI) as opposed to having CLI-only
operations."* Every flag, mode, sampling option, strategy, bound, rotation setting, estimate, warning
and confirmation above must be reachable and legible in the web UI, mapping onto the same query JSON as
the CLI so the two are the same thing.
