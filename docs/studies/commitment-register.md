# SeedLab — the commitment register

Every substantive thing discussed with DoomMachine in this session, with its status. Written by Claude
from the conversation itself, because no agent has the transcript. This is the checklist for the user's
item 1: *"Check the entire conversation for all discussions and ensure that everything that was talked
about (accepted, rejected, modified, etc) has been either implemented or rejected."*

Status vocabulary: **SHIPPED** (reachable from a shipped command and proven by a command I ran) ·
**BUILT-NOT-WIRED** (library code exists and is tested, but no shipped command calls it) ·
**IN FLIGHT** (an agent is working on it now) · **PENDING** (agreed, not started) ·
**REJECTED** (decided against, with the reason) · **DEFERRED** (with the reason and the trigger).

A claim may only be marked SHIPPED with the exact command that demonstrates it. Anything that cannot
be demonstrated is BUILT-NOT-WIRED or PENDING, however complete the code looks.

---

## 1. The generator and its proof

| # | Commitment | Status | Evidence |
|---|---|---|---|
| 1.1 | Bit-exact offline reimplementation of Valheim 1.0.15 world generation | SHIPPED | acceptance 32/32; 0 biome mismatches over 5,104,872 minimap pixels on two seeds; 4,194,304/4,194,304 height codes per world |
| 1.2 | Unity's `Mathf.PerlinNoise` and `UnityEngine.Random` reimplemented exactly | SHIPPED | natives gate 11/11; 262,780/262,780 real Perlin samples, 276/276 Random traces, 1,980/1,980 draws |
| 1.3 | Location placement reproduced exactly | SHIPPED | location gate: 12,228/12,228 fresh world, 12,314/12,314 and 12,287/12,287 played, 29/29 game-log counters |
| 1.4 | Generator internals (lakes, rivers, streams, river points) verified | SHIPPED | GoldenCheck: 8,469,000 floats across 3 seeds, 0 differing |
| 1.5 | Mono/.NET libm agreement | SHIPPED | 93/93 bit-identical; ±1 ULP perturbation of WorldAngle moves 0 of 4.19 M biome samples |
| 1.6 | AVX2 vectorisation without losing a bit | SHIPPED | 19.3 M three-way Perlin comparisons, 0 differing; all gates re-run with `DOTNET_EnableAVX2=0` |
| 1.7 | Fail-closed startup self-test on an unverified platform | BUILT-NOT-WIRED | `PerlinSelfTest` runs from a `[ModuleInitializer]`; the CLI's `ISelfTestSuite` registration is in the consolidation pass |
| 1.8 | Cross-architecture (ARM64 / Linux / macOS) exactness | PENDING | untested outside x64 — the user's very first message asked for Linux/macOS portability |

## 2. Seed-space mathematics

| # | Commitment | Status | Evidence |
|---|---|---|---|
| 2.1 | Confirm the user's 853,058,371,866,181,866 figure | SHIPPED | exact; sum of 62^k, k=1..10 |
| 2.2 | Establish the real cap: 2^32 = 4,294,967,296 distinct worlds | SHIPPED | `World..ctor` hashes the text to int32; `vseed space` |
| 2.3 | Every int32 reachable from a typeable seed text; 7 chars always suffice | SHIPPED | `vseed space`: ≤5 → 3.33 %, ≤6 → 77.08 %, ≤7 → 100 % |
| 2.4 | Inverse hash (int → typeable text) | SHIPPED | `vseed invert`; 200,000 verified round trips |
| 2.5 | The game's own field limit, measured not assumed | SHIPPED | dumper `seed-input.json`: characterLimit 10, characterValidation Alphanumeric |
| 2.6 | Search enumerates ints, never texts | SHIPPED | search engine scan order over int32 |

## 3. Disk, bounds and crash safety (the user's central concern)

| # | Commitment | Status | Evidence |
|---|---|---|---|
| 3.1 | `--keep` becomes a REAL cap on the file, bounded by default (1000) | BUILT-NOT-WIRED → IN FLIGHT | proven at library level (1,000 kept of 154,145 matches, 154× less disk); the CLI did not route through it — consolidation is wiring it |
| 3.2 | Report the TRUE match total, never "1000 matches" | IN FLIGHT | part of 3.1 |
| 3.3 | 1 GB gzipped JSONL rotation with a manifest | BUILT-NOT-WIRED → IN FLIGHT | `--rotate`/`--compress` exist in the library; CLI flags in flight |
| 3.4 | `--reduce top:N` (analyse-reduce-delete per segment) | BUILT-NOT-WIRED → IN FLIGHT | refuse for non-mergeable criteria (medians, percentiles) |
| 3.5 | Refuse `--rotate` with `--format json` (a split array is invalid JSON) | BUILT-NOT-WIRED | |
| 3.6 | Stop-at-ceiling by default; evict as a confirmed opt-in | BUILT-NOT-WIRED → IN FLIGHT | `--on-limit stop|evict`; evict needs y/N or `--yes` |
| 3.7 | Time-based flush and checkpoint (a hard kill lost 300 s of work) | SHIPPED (library) | 27 hard kills, every resume byte-identical |
| 3.8 | Truncate a torn results file back to `results_length` on resume | SHIPPED (library) | |
| 3.9 | Checkpoints out of the working directory, deleted on completion | IN FLIGHT | audit defect 5 — still writing `vseed-search.ckpt` to CWD as of the last verification |
| 3.10 | `vseed map` must stop writing `map-<seed>.png` into the CWD | IN FLIGHT | audit defect 6 |
| 3.11 | `vseed clean` + disk-use report + "the dumper folder is redundant" | IN FLIGHT | audit defect 7 |
| 3.12 | Warn before a run that matches ~100 % of seeds | IN FLIGHT | `balanced`/`custom` used to; presets now carry must-haves |
| 3.13 | A tripwire test that fails if a command writes outside the allowed set | PENDING | audit defect 9 — "a tripwire that is not tested is a suggestion" |
| 3.14 | Slices with per-slice flush/checkpoint/cleanup | SHIPPED (library) | nothing per-seed is ever written, so discarded seeds leave nothing to collect |

## 4. Estimates, metrics and honesty

| # | Commitment | Status | Evidence |
|---|---|---|---|
| 4.1 | Fix the `--dry-run` estimate (was wrong by up to 35×) | SHIPPED (library) | now 4 of 5 tiers within 2×, worst 3.26× — and it now states the direction it fails in |
| 4.2 | Live, re-projected time/disk/memory estimates during a run | BUILT-NOT-WIRED → IN FLIGHT | |
| 4.3 | Plan block before EVERY run, not only `--dry-run` | IN FLIGHT | |
| 4.4 | Warning + confirmation thresholds (1 h, 100 M seeds, 1 GB, 10 % of free space, `--all`) | IN FLIGHT | |
| 4.5 | Per-goal cost classes, and "one location goal drags the whole query into the seconds-per-seed tier" | IN FLIGHT | |
| 4.6 | One clean measurement pass as the single source of every quoted number | IN FLIGHT | `docs/measurements.md` |
| 4.7 | Remove `coastline_length` (CV 0.0245 — an 8 % span) and `deepest_point` | IN FLIGHT | |
| 4.8 | Add `shore_area_within` (land within 100 m of water) | IN FLIGHT | |
| 4.9 | Every number carries its resolution and uncertainty; grid-relative metrics say so on the record | IN FLIGHT | |
| 4.10 | Fix defective presets (`gentle-start`'s three vacuous goals and constant ranking; `balanced`/`archipelago` must-haves on coarse-unsafe metrics) | IN FLIGHT | |

## 5. Sampling and resolution

| # | Commitment | Status | Evidence |
|---|---|---|---|
| 5.1 | Ship the full sampling ladder (G384…G12) | IN FLIGHT | |
| 5.2 | Auto-pick per query as the default, plus ask-every-time and finest-affordable | IN FLIGHT | user asked for all three |
| 5.3 | Screen-then-verify: coarse + margin, then re-measure survivors at G12 | BUILT-NOT-WIRED → IN FLIGHT | produced 0 records carrying `screened_at_grid_m` at last check |
| 5.4 | Auto-upgrade island/peak/coastline goals to G12 (no safe margin exists) | IN FLIGHT | `archipelago` still ran a must-have `island_count` at G24 |
| 5.5 | Region restriction as a first-class control (162×, exact) | IN FLIGHT | |
| 5.6 | Answer "is the heavy full-resolution pass ever worth it" | SHIPPED (analysis) | yes for island/peak/coastline/nearest-Mountain (only 6.8× because river pre-generation dominates); no for bulk area (1,162× for accuracy a 1 % margin already gives) |

## 6. Resource modes and hardware

| # | Commitment | Status | Evidence |
|---|---|---|---|
| 6.1 | background / balanced / full, defaulting to balanced | BUILT-NOT-WIRED → IN FLIGHT | `SeedLab.Runtime`, 122/122 tests |
| 6.2 | Auto-throttle when Valheim is running, with one line and an override | BUILT-NOT-WIRED → IN FLIGHT | |
| 6.3 | Hardware probe + memory guard (never assume this machine) | BUILT-NOT-WIRED → IN FLIGHT | |
| 6.4 | Cache root per OS; per-process scratch reaped on next launch | BUILT-NOT-WIRED → IN FLIGHT | |
| 6.5 | CPU SIMD before GPU | SHIPPED | 6.03× on `GetBaseHeight`; 21,470 seeds/s at 16 threads |
| 6.6 | GPU acceleration | REJECTED for now | user's decision: reassess after SIMD. AMD RX 9070 XT (no CUDA); a GPU path could only ever be an approximate screening tier — FMA contraction and approximate transcendentals decide biome boundaries here, and consumer FP64 is ~1/32 of FP32 |

## 7. Feasibility, vacuity and constraints

| # | Commitment | Status | Evidence |
|---|---|---|---|
| 7.1 | Constraint atlas for all 183 types | SHIPPED (analysis) | 36,829 real instances, **0 violations** |
| 7.2 | Refuse provably impossible queries, citing the game constant | IN FLIGHT | 16 refuse rules |
| 7.3 | Warn on vacuous goals (they turn a targeted search into a full scan) | IN FLIGHT | 10 vacuity rules |
| 7.4 | Distinguish VACUOUS from DEGENERATE-TO-PRESENCE | IN FLIGHT | `nearest_distance` returns +∞ on absence |
| 7.5 | Rarity warnings with a rate and a confidence interval, never a refusal | IN FLIGHT | 7 rarity rules |
| 7.6 | Refusals only from code/asset evidence, never from a sample | IN FLIGHT | enforced structurally by an evidence record |
| 7.7 | DATA-STAMP mismatch disables all refusals | IN FLIGHT | |
| 7.8 | Pairwise (triangle-inequality) bounds | IN FLIGHT | only Haldor–Eikthyr ≥ 500 m is non-trivial |
| 7.9 | Count rules D3, V5b, A1 | IN FLIGHT | |
| 7.10 | Count rules D4/D5 | DEFERRED → now PENDING | need per-type count histograms; the user has asked for those to be built (item A) |
| 7.11 | The refusal rule for an all-must-have query with no scorer | BUILT-NOT-WIRED → IN FLIGHT | implemented at `SearchPreflight.cs:121-133`, never called by `vseed search` |
| 7.12 | T1 test: no real instance may be refused by the checker | IN FLIGHT | 36,829 instances |

## 8. Interfaces

| # | Commitment | Status | Evidence |
|---|---|---|---|
| 8.1 | CLI (`vseed`) | SHIPPED | seed, map, at, hash, invert, space, worlds, world, data, locations, search, explain, presets, selftest, bench, serve |
| 8.2 | Local web UI with a slippy map | SHIPPED | tiles 8,388,608/8,388,608 pixels identical to the game's own map |
| 8.3 | Location markers with honest candidate sets | SHIPPED | `/api/locations` matches `vseed locations` 60/60 |
| 8.4 | Full GUI parity for every CLI capability | IN FLIGHT | the user's explicit requirement |
| 8.5 | Goal editor with weights, preferences and per-goal parameters | IN FLIGHT | mirrors the reference site's model |
| 8.6 | Bounds expressed by the control itself, evidence on demand | IN FLIGHT | "visible without cluttering it" |
| 8.7 | Security: loopback only, Host check, no CORS, no external requests | SHIPPED | re-verified with real HTTP probes |

## 9. Game data and updates

| # | Commitment | Status | Evidence |
|---|---|---|---|
| 9.1 | Dumper plugin, inert until armed, safe in a live install | SHIPPED | preflight 301 checks; run successfully 2026-09-22 |
| 9.2 | Asset tables shipped with the tool, DATA-STAMP versioned | SHIPPED | `data/1.0.15-59f53fb5`, 21/21 files SHA-256 verified |
| 9.3 | Fail closed for location answers on a stamp mismatch | SHIPPED | |
| 9.4 | Dumper hardening after review | SHIPPED | mid-run safety re-checks, restore-before-compare, 18+20 API tripwire lists |
| 9.5 | The installed dumper is still ARMED | OPEN — user's action | they disarm it themselves |
| 9.6 | Prefab child data (Container drop tables, RandomSpawn) | PENDING | needed for axe-head variants and the Maypole; user agreed to one more run |
| 9.7 | Game-update resilience procedure | PENDING | user item 6 |

## 10. Outstanding user items (this message)

| # | Item | Status |
|---|---|---|
| A | D4/D5 count histograms | **DONE 2026-09-23** — the 5,000-seed study did already carry the data (`counts-*.bin`: per seed, per type, `placed`/`registered`/`attempts`). Shipped as `data\1.0.15-59f53fb5\count-sample.bin` (5,000 x 183 `uint16`, 1.8 MB) rather than per-type histograms, because a target is often a group and the distribution of a sum is not recoverable from marginals. D4 and D5 implemented in `QueryCheck`, both WARN-DEGENERATE; verified firing and verified silent on negative controls. Sample re-verified against the current build (549 cells, 0 differing). |
| B | Funnel and sample strategies | **DONE 2026-09-23, CLI and GUI.** The substrate is built and tested (18 checks): `SearchStrategy`, `FunnelPlan` (which goals stage 1 can filter on, and whether a funnel is worth running at all), `SurvivorSink`, `SurvivorList` (bounded, identity-checked, refuses a truncated or foreign list), `FunnelGate` (the measured report), `ScanPlan.OverSeeds` so stage 2 reuses the ordinary block/checkpoint machinery, and a `planOverride` on `SearchSession`. `--strategy auto\|funnel\|sample` is wired in the CLI and verified end to end: on a probe query it scanned 600 seeds cheaply, kept 15, and placed only those 15. The GUI is wired too and parity is verified against a running server: the same query returns the same strategy and the same one-sentence reason, is refused by the same rules, and its funnel run passed 22 of 600 while placing 23 worlds - the same 22 a `strategy: sample` run of that query passed. Two honesty bugs in the gate were found by running it and fixed: the per-seed cost was measured on seeds drawn from the range (which the ladder rejects for free, so it reported 0.00 s for work that took 16.8 s) and must be measured on SURVIVORS; and the wall estimate divided by the thread count when one worker takes a whole block, so 15 survivors at block size 256 is one worker, not eight. After both: estimate 16.5 s, actual 16.5 s. FINDING: the compute saving a funnel was specified for ALREADY EXISTS — `SeedEvaluator`'s ladder skips placement for any seed a cheap must-have has settled (`LocationSkips`). What the staged funnel adds is the measured survivor count before the expensive stage, which the ladder structurally cannot give. |
| 1 | This register, verified against the code | IN PROGRESS |
| 2 | Axe-head chest differentiation by cabin variant | PENDING — needs 9.6 |
| 3 | Maypole: on-demand + cache + background sample index | PENDING — needs 9.6 |
| 4 | QA pass on every safeguard | PENDING |
| 5 | Handoff documentation | PENDING (partly IN FLIGHT) |
| 6 | Game-update adaptability | PENDING |
| 7 | Lessons → memories, skills, agents | PENDING |
| 8 | Complete mathematical-constraint guards | PENDING (overlaps §7) |

## 11. Explicitly rejected or superseded

- **GPU acceleration** — deferred by the user's decision after CPU SIMD delivered 6×.
- **`coastline_length` and `deepest_point`** — removed; `coastline_length` ranks *better* than metrics
  we keep but spans only 8 % across all seeds, so it cannot discriminate.
- **"Boss altar counts are fixed"** — disproved: `DN_Bossroom` falls short in 0.520 % of seeds. The
  seven classic bosses are bounded at ≤0.060 %, which is not the same as fixed.
- **"Eikthyr within 1.2 km" as a filter** — degenerate to a presence test (`m_maxDistance` 1000).
- **Per-segment reduction for non-mergeable criteria** — refused by design (medians, percentiles).
- **Unbounded output as a default** — replaced by bounded-by-default with `--keep all` explicit.
- **Full precomputed Maypole index over all 2^32 seeds** — infeasible (~11 years compute), though
  storage would be only 512 MB. Replaced by on-demand + cache + a background sample index.
