# SeedLab: the user's decisions about search, output and resources

**These are decisions, not suggestions.** DoomMachine made them on 2026-09-23 over several rounds of
questions, after a disk audit of the search engine. Treat this file as the record of what was agreed,
so nobody re-litigates it.

**Status, updated 2026-09-23 after the wiring pass:** sections 1, 2, 4, 5, 6, 7, 9, 10, 12 and eight
of the nine must-fix defects in section 11 are **built and reachable from shipped commands** - the
proving command for each is in the project's `CHANGES-FOR-THE-USER.md`, and the measured numbers that
replace section 0's are in `docs\measurements.md`. Section 3's named `--strategy auto|funnel|sample`
shipped later the same day, with its stage-1 ratio report (`FunnelRun`; history, "the funnel strategy
shipped"). Still unbuilt (checked 2026-09-25): **defect 9's write-path tripwire test** (listed in the
project's `docs\limits.md`), and GPU (section 8, rejected on purpose). Section 0's record-size law and the 7.36 TB worst case are the
*pre-fix* measurements: that command is now refused before it starts. (The original lived in a session scratchpad, which does not survive; it was copied
here on 2026-09-23. An earlier copy was lost once already when an agent cleaned its scratch folder.)

The user's framing, in their words: *"we can have unrestricted functionality without the possibility of
crashing a file system or filling up a drive"*, *"this needs to work as intended"*, and *"any questions
or unclear matters should be asked as opposed to assuming"*.

Contents: 0. The measured facts · 1. Result bounding · 2. At the ceiling · 3. Location searches ·
4. Slices · 5. Segmented output · 6. Sampling · 7. Resource modes · 8. GPU · 9. Goal model ·
10. Cost transparency · 11. Must-fix defects · 12. GUI parity · 13. Server lifecycle ·
14. Two session logs · 15. Performance direction · 16. Future work

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

## 13. The web server's lifecycle (decided 2026-09-24)

The user: *"I don't find it prudent to have something permanently running"*, then, after the exact meaning
of Ctrl+C was explained to them, **"On the server-stop design - confirming the proposal"**:
- `vseed serve` runs in its own visible console window titled "SeedLab web server" (never hidden, never a
  service, never started at login), which prints what it is and how to stop it. The Windows one-click file
  opens it in a new window of its own, so Ctrl+C and closing reach vseed alone.
- **Stop SeedLab on the page**: a first dialog says what stopping does; if a search is running, a SECOND
  warning names it and says what stopping costs (the finished part is saved; the resume command is shown).
  **Exception, a funnel in its first stage**: it has no checkpoint yet, so stopping it loses its work so far.
  Every stop text - this dialog, the first Ctrl+C's message and both `--stop` question paths - says that for
  such a search, and offers no resume command (per search, via `ServeConsole.StopCosts`; a review on
  2026-09-25 found one sentence promising a saved part for every search).
- **Idle reminder, never an automatic stop**: after 60 minutes with no user activity (the page's own polling
  does not count; a running search does) the page, and the server window, suggest stopping it, with the
  option to do so; ignored or "Keep running" starts another 60 minutes, and so on.
- **Scripts and commands**: `vseed serve --stop` (asks when a search is running; `--yes`; `--force` after an
  unanswered request) and `vseed serve --status`, behind the Windows and macOS/Linux scripts.
- **Ctrl+C only counts in the server's own console window**, and only twice within 10 seconds; the first
  press stops nothing and says what is running. Closing the window (which cannot be refused) saves a running
  search's checkpoint before the process ends.
- **A Windows sign-out or shutdown takes the same graceful stop** (added 2026-09-25, after a review found it
  never did: a console handler gets no sign-out or shutdown event in a process that has loaded user32.dll,
  valheim-modding `pitfalls.md` section 10). A hidden top-level window on its own message loop
  (`src\SeedLab.Cli\Infra\SessionEndWindow.cs`) answers `WM_QUERYENDSESSION` "yes" at once - SeedLab never
  holds up a shutdown, and nothing stops yet because another program may still cancel it - and on
  `WM_ENDSESSION` runs the same stop as closing the window: searches saved, the registry file removed. Tested
  by sending the window both messages; a real sign-out or shutdown has not been tested.
- A second `vseed serve` opens the browser at the running one instead of failing.

**Built 2026-09-25** (`25f2a9f`; history.md, "the web server starts and stops safely").

## 14. The session log: exactly two files (decided 2026-09-24)

*"This should have a total of two logs - a current log, and a last session log. That way, we can retain
information, but not a lot of information, and there can be redundancy in case of issues."* At the start of
every session that starts a runtime, `vseed.log` becomes `vseed-prev.log` (replacing the older one) and a
fresh `vseed.log` is written, BepInEx-style (the user's model: `BepInEx\LogOutput.log`, rewritten each game
start). A vseed started while another runs writes `vseed.log.1` (to `.4`), removed at a later start.
**Built 2026-09-25** (`25f2a9f`), replacing `95bba24`'s single log; the size cap and the append when
`vseed-prev.log` is held are the implementer's choices, recorded in history.md.

**Amended by the user on 2026-09-26** (after reporting "There is no log being saved for an operation"; the
session log existed but held one line for a whole web search): the two session logs stay exactly as above,
**and every search also records itself in them** (asked for, refused, started with its settings and the
workers really used, progress at a bounded rate, a stop and who asked, how it ended, the results file, the
checkpoint and the resume command) - web and CLI alike - **and keeps its own small log file beside its
checkpoint** (`<checkpoint>.log`, the same lines). The page shows where both logs are and reports log
problems itself, not only in the server window. Asked as a choice; the user picked the per-search file over
"session log only". **What happens to that file was settled the same day (D3, section 17):** a resume appends
to it; when the search finishes it moves beside its results file, or is removed if there is none; it is not
kept indefinitely. (Corrected 2026-09-26: this said it is "kept as long as the checkpoint and removed with it",
which would delete it the moment a search finishes.) **Not built yet** (paused, unmerged work in progress).

## 15. Performance direction (discussed 2026-09-24)

Measured, not assumed (`docs\measurements.md`): the wall-clock limit is the per-seed work the game's own
algorithms demand x 4.29 billion seeds, under bit-exactness; hardware sets the multiplier (16 threads = ~10.8x
one thread on the 9800X3D). **The language stays C#**: the hot code is scalar IEEE double arithmetic that
must round exactly as the game does, so .NET emits what C++/Rust would; C# SIMD already gave 6.03x bit-exactly.
Agreed next steps, in order: (1) profile the fixed per-seed costs (river pre-generation ~150-180 ms; the
location world build ~1.1 s, nearly independent of how many types are placed); (2) SIMD where the profile says
it matters, AVX-512 first, each path proven bit-exact; (3) a **seed atlas** - precompute cheap per-seed features
once, store them losslessly (grid measurements are integers; the seed is the row), columnar, chunked and
compressed with built-in .NET codecs, with per-chunk min/max so a query skips chunks, and query it on demand;
(4) a **20,000-seed pilot** first, to check atlas answers against live search, measure bytes per seed and the
compression ratio, and profile. GPU stays an approximate screening idea only (section 8).

**The pilot, decided by the user (2026-09-24)** from the merged design (section 16 of the author's working
notes, which are not in this repository): atlases live in **a folder the user names** - never the cache root, which `vseed clean` may empty -
and the pilot's in a staging folder on the author's machine; the pilot stores **all 183** location types (not the 67 the
presets use); it builds in **`--mode full`**; and it runs the **required tier and the overnight tier**, on a quiet machine, on a night the user chooses.
**Size: 5,000 seeds, not 20,000** (the user, 2026-09-25: the 20,000 was "a number that I pulled out of thin air";
asked, they set the atlas pilot to 5,000). The first 5,000 seeds of the pilot order are the count study's sample, so
`count-sample.bin` cross-checks every stored count for free; the build is ~1 h at full with all 183 types instead
of ~3.5-4 h, the overnight comparison ~3 h instead of ~12, and an atlas grows later without recomputing a row.
The profile keeps its own fixed battery (~1,300 seed-runs), and the whole-range size projection keeps its two
cheap sets (65,536 consecutive seeds; 1,000,000 seeds at G384).

**Atlas == live, the test baseline (the user, 2026-09-25: "Let's be rigorous and err on the side of caution - set it
to 9.5 million").** A bug is a fixed defect with an unknown trigger rate, so the baseline is a stated confidence:
for a defect that fires once in N seeds, testing n seeds sees it with probability 1 - e^(-n/N). The user's line:
anything rarer than 1 in 1,000,000 is not worth contemplating now. So:
- **Whole-space families - biomes at G384, G192 and G96: every stored value of 9,500,000 seeds** (indices
  0..9,499,999 of the pilot order) compared with a fresh live computation, 0 differences required. It sees a
  1-in-1,000,000 defect with 99.99 % probability (1 - e^-9.5) and a 1-in-500,000 one with 99.9999994 %; a clean run
  bounds any remaining rate below ~1 in 3.2 million at 95 % (rule of three). Cost on the 9800X3D at full, both
  sides: ~15 min (G384) + ~41 min (G192) + ~2.2 h (G96) = **~3.1 h**, on the pilot night; the rows are the start
  of a whole-space atlas, so none of it is thrown away.
- **Expensive families (heights, rivers, locations)** cannot reach such counts (locations ~1.9 seeds/s: 9.5 M
  seeds would be ~58 days): every value of the 5,000 pilot rows is checked (a clean run bounds the rate below 1 in
  1,667 at 95 %), and their safety rests on the design - the atlas never writes a result; every seed it passes is
  re-evaluated live and every value compared, so the only silent failure is a missed match.
- **Boundary defects are not sampled**: exhaustive tests enumerate every exact-boundary cell for every grid and
  edge, and the rare stale-river-cache state is flagged, routed live and tested with a planted condition.
- **Pass rule:** zero differences anywhere; any difference stops the pilot with the evidence.
The design's recommendations stand for the rest: `--atlas` never changes which seeds a command visits (explicit
key/seeds), atlas builds are CLI-only in v1, screen false negatives are kept and named, no provenance field on
records, non-FMA3 CPUs stay fail-closed, and the profile decides whether scalar speed-ups or AVX-512 come first.

## 16. Future work the user has asked to be planned (2026-09-24)

- **Seed pickers, GUI and CLI**: a *truly random* picker when a range is chosen (toggleable), and a
  *sequential from 0* picker (toggleable); **the default stays the current keyed shuffle** (a 4-round Feistel
  permutation keyed from the query's hash - reproducible, no repeats, `--seeds 1000` extends `--seeds 500`).
  Open, for the user: the query's `name` is part of that hash, so renaming a query changes its sample.
- **CPU compatibility**: Intel and AMD x64 consumer and workstation CPUs released **2015-2026+**. Every SIMD
  path dispatches at run time (AVX-512 where the runtime accelerates it, else AVX2, else scalar - CPUs of that
  era without AVX2 exist, such as low-end Pentium/Celeron/Atom parts) and is proven bit-exact on each level on
  this machine by switching the upper levels off (`DOTNET_EnableAVX512=0`, `DOTNET_EnableAVX2=0`; corrected
  2026-09-24: `DOTNET_EnableAVX512F` does not exist in the installed .NET 10.0.12 runtime - a test must assert a
  switch's EFFECT, e.g. `Avx512F.IsSupported == false` in the child, never trust its name). Not
  testable here: whether the C runtime's `sin/cos/pow` pick CPU-specific code on other processors (the
  per-machine self-test fails closed if they do), hybrid P/E-core Intel parts (12th gen+) where equal-size
  blocks meet unequal cores, and arm64. Evidence from other real CPUs is still needed.
  **First evidence, 2026-09-25:** a tester ran the Intel test package (machine report 1.0.0, SeedLab `11aeb8f`) on an
  Intel Core i7-12700K (Alder Lake, 8P+4E, AVX2, AVX-512 fused off) under Windows 11 25H2 (`ucrtbase.dll`
  10.0.26100.9444): **93/93 checks PASS at four levels** - numerics 271/271, natives 263,778/263,778, 7 world
  fingerprints equal to the 9800X3D's. So SeedLab is bit-identical across Intel/AMD and Windows 10/11 C runtimes for
  everything the report compares. Still open: CPUs without FMA3, `libm-dense` on Windows 11 (the package predates
  it), hybrid per-core-type timing. **Performance lead, resolved 2026-09-25 (quiet-machine profile):** the package's
  pre-generation stops at 21.3 seeds/s on 20 threads and 22.8 on the 9800X3D's 16 because the package runs .NET's
  default *workstation* GC; `vseed` runs the server GC and reaches 55.6 seeds/s at 16 workers (7.8x), and forcing
  workstation GC on `vseed` reproduces the 22.2 stall (GC 51 % of wall). What remains is allocation: 48.8 MiB per
  seed (river render 21.9, stream render 25.9), GC 36 % of wall at 16 workers under server GC - reusing those
  buffers per worker is the lever (history.md, "the quiet-machine CPU comparison"). The report is kept with the author's working notes;
  `docs\cpu-compatibility.md` records it (`ab45aa2`, merged into main as `81e3f97` on 2026-09-25). The package
  itself: https://github.com/DoomMachine/Valheim-SeedLab-IntelTest, release v1.0.0 (history.md).
- **Intel SDE (Software Development Emulator) - APPROVED by the user, deferred** (*"Log the SDE for Intel as a
  future task - it is approved, just not now"*, 2026-09-24): run SeedLab's self-test and fingerprints on this
  machine as if on older/newer Intel CPUs (Haswell, Skylake, Ice Lake, Sapphire Rapids, ...) to exercise the
  instruction-set dispatch and any CPU-specific C-runtime math paths. The download is approved; timings under
  emulation mean nothing.
- **A machine report for testers without Valheim** (asked 2026-09-24): a downloadable, self-contained package
  another person can run on an Intel Windows PC with no game and no .NET installed: CPU facts .NET sees, the
  machine self-test, world fingerprints at each instruction-set level compared with this PC's, a short timing
  run - hardware and OS version only, nothing personal - and no game data in it (Iron Gate's content).
  **Built and released 2026-09-25** as `Valheim-SeedLab-IntelTest` v1.0.0 (history.md), a separate program built
  from SeedLab `11aeb8f`; since `e4b9079`, `vseed selftest --report` gives a comparable report from SeedLab itself.
- **Boss spawns per world - a claim to test, noted by the user 2026-09-25 ("just a note, not a prompt to start"):**
  the number of places each boss can spawn in one world, possibly the maximum:

  | boss | claimed spawns |
  |---|---|
  | Eikthyr, Moder | 3 |
  | The Elder, Yagluth | 4 |
  | Bonemass, The Queen, Fader | 5 |

  **Checked for 1.0.15 on 2026-09-26 (in passing, by the web-page diagnosis):** SeedLab's 5,000-seed count
  sample (`data\1.0.15-59f53fb5\count-sample.bin`, placement proven bit-exact against the game) places exactly
  Eikthyr 3, the Elder 4, Bonemass 5, Moder 3, Yagluth 4, the Queen 5 and **Fader 3** in every one of the
  5,000 worlds (min = max, no world short), matching each location's `m_quantity` in the dump
  (`FaderLocation` quantity 3) - 27 altars per world. So the claim holds for six bosses and is **wrong for
  Fader (3, not 5)**. Not yet checked on Valheim 1.0.16 (installed 2026-09-25), whose data needs a new dump.
  Why it matters: it bounds which boss goals are legal. A goal asking for more of a boss than a
  world can hold must be refused before a scan, like every impossible goal (section 9's refusal rule and the
  feasibility checker). When it is taken up, check: each boss location's quantity in the `data\` snapshot (the
  game's placement tries to place that many and may place fewer, so the number is a ceiling and a world can
  have less); the counts SeedLab places across a large sample of seeds (the smallest and largest seen, how often
  a world falls short); the three reference worlds against the game's own placement; which location prefab each
  boss's altar or summoning spot is; and whether the goals and presets already cap these counts.

## 17. No administrator rights, and limits checked locally (decided 2026-09-26)

The user, after choosing 20 threads in the web page on a 16-thread machine and seeing nothing check it:
*"a check must be made locally whether the number of threads requested is possible. This check must be made
gracefully, as much as possible, without requiring explicit admin access. This should be a priority for the
entire application - while I am the admin user of the machine and OS, some users might not be, or may be
using restricted environments for additional security."*

- **Everything works without administrator rights** and in restricted environments: limited accounts, CPU
  affinity masks, job objects, containers, group policy, redirected or read-only folders. Nothing asks for or
  relies on elevation (the one-click scripts already refuse to run elevated; only installing the .NET SDK
  asks, through Windows itself).
- **A limit the user can ask past is checked before the work starts** - a thread count above what this
  process may use, a folder that cannot be written - and handled gracefully: a plain-English note saying
  what is used instead and why, the same in the page, the CLI and query files; never a crash, never silent
  oversubscription.
- **Read only what a normal user may read**: `Environment.ProcessorCount`, the process affinity mask, job or
  cgroup limits where readable; when something cannot be read, say so and fall back safely.
- Same day, the user on the web page's goal options: a goal on a group must say unmistakably whether it means
  any ONE member, EVERY type (one place each) or EVERY candidate place; the feasibility check refuses only
  what is impossible for that meaning, and every option in the page's drop-downs gets a plain-English
  explanation (section 9's refusal rule; the fixes are in history.md once built).
- **The user's answers of 2026-09-26** (asked after the web diagnoses; they settle the web fix):
  1. **"Every single place" (`all_candidates_distance`) on a group:** a member that can place but has no place
     in a world makes the goal fail (as "one of each kind" already does), so "every boss altar within 2,500 m"
     is provably impossible and refused, naming Yagluth (2,900 m), the Queen (5,900 m) and Fader (7,900 m). A
     deliberate definition change: the value differs only in worlds where a member places zero. Members that can
     never place (e.g. `GoblinCamp2_1`) are left out of the group for this metric.
  2. **Threads above what this process may use are clamped**, with a plain note - page, CLI and query files alike.
  3. **Must-haves that can only mean "is it there at all" stay refused**, on the page too, with a plain reason
     and no command-line flag the page cannot pass.
  4. **The time estimate respects "Stop after"**: the budget plus its bounded overrun, and how much of the asked
     seeds fits; a yes is asked only when that capped time is long.
  5. (A proved false refusal, fixed without asking under the standing rule) distances from SPAWN may reach
     10,500 + 5,100 = 15,600 m; 10,530 m was measured in a real world.
  6. **When the installed game is another build, the refusal stays, in plain words**: what happened (Valheim
     was updated), what still works (biomes, heights, rivers, maps), what does not (bosses, traders, dungeons
     and other places), and what to do (the game-data step in `docs\game-data.md`), with no hash in the first
     sentence. The check itself is never weakened. The web page must fail such a request at once, not spin.
  7. The per-search log of decision 14 (as amended the same day).
- **The user's answers to the design's three questions (2026-09-26, after the design)** - they override its
  defaults:
  - **D1, the automatic block size for slow searches: about one minute per group.** Automatic =
    `min(256, today's count rule, max(1, floor(60 s / s_high)))`, `s_high` being the estimate's per-seed,
    per-worker time; fast searches keep up to 256, the plan says which rule applied, a resume keeps the
    checkpoint's size, and 256 stays the ceiling (the 2026-09-24 decision).
  - **D2, "Stop after" (the time limit): a short grace period.** At the limit, a group that can finish within
    the grace is finished and saved; every other unfinished group is thrown away and checked again if the
    search is continued. Grace = `max(30 s, 25 % of the limit)` from the limit; after it, every worker stops
    after the seed it is on. A limit is still not a floor. The overrun is bounded by the grace plus one seed
    plus the final save, and the plan says so. The page and the CLI plan warn before the start when the
    limit is too short to save anything.
  - **D3, a search's own log** (the user: *"any such logs should not be completely persistent ... A resume
    option should utilize the log, but if a brand new search starts there should be a warning about an
    operation that can be resumed"*): beside the checkpoint while resumable, a resume appends to it; at the
    end it moves beside the results file (`<results file>.log`) or is removed if there is none; it is
    replaced or removed with that results file, removed by `vseed clean` and the uninstall, and search logs
    in SeedLab's folders get a retention cap (for example the newest 20, or 64 MB in all, oldest first, at a
    session start; stated in `docs\limits.md` and the plan), each still capped at 1 MiB of progress lines.
    **Starting a new search while a resumable one exists warns** and offers: continue that search (its
    name, progress and last run), start the new one anyway - which deletes the earlier search's saved
    progress and log, said plainly before the yes - or cancel; the CLI asks, and with no one to answer
    refuses and names `--resume` / `--discard-previous`. The same-query "checkpoint exists" dialog becomes
    one case of this.
- **Status, 2026-09-26 evening: none of 1-7 or D1-D3 is on `main` or reviewed.** The implementer was stopped
  at 16:34 for the data rebuild; its unfinished, untested work (stop, thread-count and estimate code, 30 files)
  is preserved as a work-in-progress commit on a local branch (after a commit that pins every preset's query
  hash), "not for merging as is". history.md, "the web page
  diagnosed", has what was proved and the plan.
