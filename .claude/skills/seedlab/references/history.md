# SeedLab history: what was built, what was corrected, what it cost

Newest first. Each entry says what changed, why, and how it was verified. Game facts SeedLab
discovered are recorded in the valheim-worldgen / valheim-modding references and only pointed at from
here.

## 2026-09-25 (latest) - the README says the session log records the process ID

The pre-push release audit (its note 6) found that the
README's list of what a session log holds left out the `process  pid <n>; <OS> (<RID>); <framework>` line
that `RuntimeContext` writes at the start of every session. The user's standard counts process IDs among the
things to take out before posting a log, so the list now names it, beside the other things to remove.
README only. Committed as `831f55a`, on top of the merge below.

## 2026-09-25 - `vseed profile` and the CPU safety net, merged into main

Steps (1) and (2) of decisions.md section 15 and the CPU-compatibility item of section 16 (the why is there,
not repeated here). Built on the branch `perf-track` in a worktree of its own
from the merged design (the author's working notes, not in this repository); merged as `81e3f97` "Merge the profiler
and the CPU safety net", after which the branch and the worktree were removed. Every piece had to change no
value the generator computes, so each carries its own proof of that.

What changed, commit by commit:
- `ed0695b` - the profiler's fixed interface: `Phase` (the permanent phase map; the ids are column ids of a
  stored profile, appended and never renumbered, 7 left as a hole), `PhaseSink` (one thread's ticks, entries
  and allocated bytes per phase, reached through a thread-static that is null unless a profiler set it) and
  `PhaseClock` (per-point counters behind `SEEDLAB_PROFILE_COUNTERS`, a static readonly flag read once, so
  the JIT removes every counter site from a normal run - and the switch only takes effect in a new process).
  Read-only views of the river cache (`RiverCacheIsStale`, `RiverCacheCell`, `CopyRiverCachePoints`).
- `95e542c` - **`vseed profile`**: where one seed's time goes, phase by phase (the constructor,
  pre-generation and its nine steps, the biome and height passes per grid, the structure counts, the
  location oracle's six steps), with median, p10, p90, mean, share and allocation per phase, JIT and GC in
  the measured window, `--threads` as a list, `--counters`, `--overhead`, `--out` (schema
  `seedlab-profile/1`) and `--per-seed`. Seeds come from one fixed order (`PilotSeedOrder`, the count
  study's key `0xA17A25EED10C5117`), so profiles from different machines cover the same worlds.
  **`QuietMachineProbe`** watches the machine before and during the work and marks a run TAINTED, naming
  what it saw, instead of trusting a busy machine. **`WorldFingerprint`**: five SHA-256 layers per world,
  with a 64-seed reference recorded before any phase boundary was wired in. Found on the way:
  `RiverCacheIsStale` is true on 2 of the 64 reference worlds, not about 1e-5 as the design assumed (the
  1e-5 was the effect on heights, not how often the state occurs).
- `e4b9079` - **the CPU safety net**. `SimdDispatch` chooses the vector path once per process from the
  runtime's `IsSupported` flags (never a CPU name), its view of 512-bit speed, a `--simd` /
  `SEEDLAB_SIMD` ceiling (`auto|scalar|avx2|avx512`) and the widest kernel built. That is AVX2: AVX-512
  is detected and named but runs the AVX2 path, and says so. `SEEDLAB_SIMD_EXPECT` lets a proof run state
  what a runtime switch must have done, and the WorldGen module initialiser refuses to load where it did
  not. The self-test stamp is filed under the ISA flags, the processor from CPUID, `ucrtbase.dll`'s
  version and the path, and two built-in suites are new (`libm-dense`, `denormals`). **`vseed selftest
  --report`** is the machine report for testers on other CPUs: it needs neither `groundtruth\` nor
  `data\` and prints no machine name, user name or path. Also `--simd-all` (prove every path the hardware
  has), `--isa-json`, `docs\cpu-compatibility.md` and `tests\level-matrix.ps1`.
- `d905502` - thirteen review findings, all real (two reviews). The quiet probe could not see about a third of the processes (see valheim-modding
  `pitfalls.md` section 3); an unreadable process now keeps its name, and a second figure, the whole
  machine's busy time (`GetSystemTimes`, a P/Invoke kept in the CLI as `MachineCpu`; `/proc/stat` on
  Linux), is judged too. A tainted baseline no longer raises the limit; `SeedLab.*` processes are watched;
  generator code may not read the profiler's state at all (`BeginCurrent`/`EndCurrent`); the `patch` phase
  had timed a whole second `MeasureBiomes` (`WorldMeasurement.MeasurePatches` is split out); a leftover
  `SEEDLAB_PROFILE_COUNTERS=1` now warns; a vector-path divergence names `vseed --simd scalar selftest
  --report` as the way out; every harness refuses to start while a `DOTNET_` or `COMPlus_` code-generation
  switch is set and proves its default level against CPUID; the levels K10 (`DOTNET_EnableAVX=0`) and K3c
  (`COMPlus_EnableAVX512=0`) were added; AVX10.1/10.2 are recorded; the report prints an exception's type
  and file name only, never its message (which can hold a path).
- `7d433d1` - `d905502` could take an ended timing leg's CPU time out of the machine figure twice, so a
  busy interval could read as quiet in `--overhead`. Each reading now carries every excluded child's
  cumulative CPU time, and a window uses the difference between its two readings.
- `ab45aa2` - the first machine report from an Intel CPU (entry below).

Verified on the branch (a machine busy with other work, so no timing is a measurement): 64 seeds x 5
fingerprint layers identical to the pre-profiler recording with the profiler off, phases on, and phases plus
counters on, and the phase tree adds up on every seed; 7 of 7 planted profiler reads flagged. At each of K0,
K3, K6, K7, K8 and K10, every process asserting its own level: fingerprints identical and 30 of 30 gate runs
pass (Acceptance 32/32, location gate 12,228/12,228, natives 11/11, GoldenCheck, `vseed selftest`); knob
matrix 31/31 with eight states giving eight stamp keys; numerics tripwire 13 of 13 planted uses flagged.
Search results byte-identical before and after: 11 presets (2,360 seeds) between the `11aeb8f` and
`95e542c` builds, 14 searches (3,080 seeds, 1,130 records) between `e4b9079` and `d905502`. On the merged
tree, re-run by the main session: Runtime.Tests 266/266, Search.Tests 536/536, Acceptance 32/32, natives
11/11, proofs refuse/policy/blocks exit 0, killtest bare 3/3 IDENTICAL and `-KeepAll` 0 DIFFER, `vseed
selftest` PASS, `vseed selftest --report` PASS with 40/40 digests on AVX2, `vseed serve --selftest` PASS. The
merge kept both sets of runtime checks (the server registry, the quiet probe, the processor).

Still open:
- Not tested: the Linux `/proc/stat` reader; AVX10.2 code generation and APX (no such hardware, and .NET
  10.0.12 has no public way to ask about APX); the CPUID grouping rule on a CPU that has only part of a
  feature group, and the OS register-state setting, which managed code cannot read.
- A probe that runs under 1 s has no earlier sample to merge its short last one into, so a false taint is
  still possible there; the short-tail test relies on timing.
- Not built, on purpose: timed calibration between paths (with one vector path there is nothing to time;
  `--simd scalar` is the manual override), the denormal probe in the search workers, the study of the C
  runtime's non-FMA3 path (a CPU without FMA3 still fails closed), any AVX-512 kernel.
- The profiler's cost when switched off (at most 1 %, PT4) is not established; it needs the quiet machine.
  The profile itself and the atlas pilot (decisions.md section 15) have not run.
- A false `SEEDLAB_SIMD_EXPECT` stops the gate executables with a `TypeInitializationException` rather than
  a sentence (only `vseed` prints one); the gates count it as a failure, so the proofs hold.
- For the user: `vseed profile --out` refuses the game's install folders except a folder named
  `_ModSource`, hard-coded so the pilot can write to a staging folder under it.

## 2026-09-25 - the first machine report from another CPU: an Intel i7-12700K, bit-identical

A tester ran the machine report package (the `Valheim-SeedLab-IntelTest` entry below; SeedLab `11aeb8f`,
bundled .NET 10.0.12) on an Intel Core i7-12700K (Alder Lake, 8 performance + 4 efficiency cores, AVX2 and
FMA, AVX-512 fused off) under Windows 11 Pro 25H2 with `ucrtbase.dll` 10.0.26100.9444. **All 93 checks
passed at four levels** (as found = AVX2, AVX-512 off, AVX2 off, scalar): numerics 271/271, natives
263,778/263,778, the 11 natives checks, and all 7 world fingerprints equal to the reference made on the
Ryzen 7 9800X3D. (SeedLab's own natives count is 263,780: the package's `natives-hash.json` leaves out the one
seed-text entry, and each hash sample is two checks, the hash and its lane split - `NativesSuite.cs`.) The report is kept
with the author's working notes; the project's
`docs\cpu-compatibility.md` records it ("Machine reports received", committed as `ab45aa2` on `perf-track`
and merged in `81e3f97`). What it settles, what is still open and the performance lead it gave are in
decisions.md section 16. **Unverified:** that lead - pre-generation stopping near 21 seeds/s on both this CPU
and the 9800X3D - points to a limit in the code (allocation, garbage collection or a shared resource) rather
than the hardware; nothing has profiled it yet.

## 2026-09-25 - the web server starts and stops safely; two session logs; one-click scripts

**Why.** `vseed serve` ran in the console it was started from, and the first Ctrl+C there ended it at once:
a running search died mid-block and lost whatever its last checkpoint did not hold, and nothing but that
console could stop it. SeedLab's users are not necessarily power users. The user's decisions are
decisions.md sections 13 (the lifecycle) and 14 (two logs); this entry is how they were built. Committed as
`25f2a9f`.

What changed:
- **One graceful stop for every trigger** (`WebServerControl.Stop`): Stop SeedLab on the page, the idle
  reminder's Stop, `vseed serve --stop`, Ctrl+C twice within 10 s in the server's own window, Ctrl+Break,
  closing the window, SIGTERM/SIGHUP, and a Windows sign-out or shutdown. Each worker leaves its block
  between two seeds; the last checkpoint is saved on the quick retry schedule (about 1.6 s), because closing
  a console window leaves the process about 5 s; the server waits at most 3.5 s for searches; each search's
  stream gets a final `done` that says why it stopped. ASP.NET Core's own console lifetime is replaced by one
  that does nothing, because its Ctrl+C handling ended the process under a running search.
- **Sign-out and shutdown**: a hidden window (`SessionEndWindow.cs`), because a console handler never sees
  them in this process (valheim-modding `pitfalls.md` section 10; decisions.md section 13).
- **Finding the server**: a registry file `<cache root>\serve\server-<pid>.json` with the pid, the process
  start time, the URL and a token. A record counts only through the strict liveness check
  (`ProcessLiveness.IsSameProcessStrict`) and only when its URL is exactly `http://127.0.0.1:<its port>`;
  `/api/server/state` must answer with the same pid. The token keeps other web pages out, not other programs
  on the same PC (owner-only on Unix). `--stop` and `--status` run before anything else starts, so they
  create nothing; a second `vseed serve` opens the running one and exits 0; a vseed server this cache folder
  does not know about (started with another `--cache-dir`) is listed but never stopped (command lines are
  read with `NtQueryInformationProcess`, because WMI would need a NuGet package). `serve\` is not a `vseed
  clean` category.
- **A resume command that works after any stop**: a page search writes its query to
  `<checkpoint>.query.json` beside its checkpoint (retired with it), and every resume command names that
  file. Starting a page search whose checkpoint already exists asks first ("Start again from the first seed"
  or Cancel, sent as a new `replaceCheckpoint` field) instead of silently overwriting the resume point.
- **Two session logs**: `logs\vseed.log` is renamed `vseed-prev.log` at every session start;
  `vseed.log.1`-`.4` are used only while another live session holds the log (recognised from the log's last
  "written by process" line: pid and start time, which also works where file locks are only advisory). A
  held `vseed-prev.log` is kept and the new session appends to `vseed.log` (if under 4 MiB) rather than
  delete the last session's log. A cap the user did not ask for: past 4 MiB only warnings and errors are
  kept, past 16 MiB nothing, so the two logs stay under about 40 MiB. This replaces `95bba24`'s single log
  that was emptied at every session start.
- **One-click scripts**: `SeedLab.bat` (a menu, or the action as an argument) and six numbered files
  (`SeedLab 1 - Install or update.bat` to `SeedLab 6 - Remove the build.bat`) for Windows; `seedlab.sh` and
  `SeedLab.command` for macOS and Linux (the executable bit is set in git). Install or update (only
  installing the .NET SDK asks for administrator rights; the scripts refuse to run elevated and turn the
  SDK's telemetry off for their builds), open and stop the web page, a command window, uninstall (keeps the
  build) and remove the build. The README gained "How to use it"; `docs\scripts.md`, `docs\web.md`;
  `.gitignore` gains `/seedlab-results/`.
- **Review fixes** (three adversarial reviews; all 22 findings
  from the three reviews were real). The blocker: a leftover registry file whose pid now belonged to a process
  vseed cannot inspect counted as a live server, and locked the user out of starting, stopping, uninstalling
  and removing the build (valheim-modding `pitfalls.md` section 10). Also: sign-out and shutdown never
  reached the graceful stop; a funnel in its first stage was promised a saved part it did not have
  (decisions.md section 13); `seedlab.sh`'s `remove_block` could delete everything after a SeedLab block
  whose end marker had been edited (such a file is now left untouched and the user told which lines to
  remove); uninstall matched the user's own folders by name; a third Ctrl+C during the stop said "nothing
  has been stopped yet".

Verified: `SeedLab.Search.Tests` **536/536** (section 18, the lifecycle, is new), `SeedLab.Runtime.Tests`
**211/211**, `vseed serve --selftest` 17 PASS and 3 MEASURED, `vseed selftest` PASS, proofs
refuse/policy/blocks exit 0, killtest bare and `-KeepAll` 3/3 IDENTICAL, all on scratch cache roots. A
script matrix in fresh copies (31 Windows rows and a dash set). A real Ctrl+C sent to the server's own
console: one press stops nothing, a second within 10 s stops it (exit 0 in 17 ms), one Ctrl+Break stops it
(exit 0 in 24 ms). The sign-out path, by sending the hidden window both messages: the search saved, the
registry file removed, exit 0. A reviewer posted `WM_CLOSE` to a real server's console window while a search
ran: the log shows "stopping: its window was closed" and the checkpoint saved, no registry file was left,
and the process then exited with 0xC000013A (expected once a close handler returns). In a browser: both stop
dialogs, the stopped overlay, the idle dialog and the "start again?" dialog.

Never tested: a real sign-out or shutdown; clicking the window's X (the
reviewer's `WM_CLOSE` is the nearest); the Enter pause in a window of its own; typing an answer at `Stop
anyway? [y/N]`; `--force`; two servers in one cache root; a stop during the funnel's measurement between its
stages; anything on macOS or Linux (SIGTERM, SIGHUP, advisory locks, owner-only file modes). Earlier
script-matrix rows were not re-run after the last fixes (no SDK installed, a real new window, the numbered
files and the menu, the command window, the "source changed" prompt, administrator refusal for install, web
and shell). Still open: `vseed search` without `--resume` still overwrites an existing checkpoint (older
behaviour, kept); the page takes up to about 20 s to notice a stop begun elsewhere (its 15 s poll).

## 2026-09-25 - a machine report testers can run without Valheim: `Valheim-SeedLab-IntelTest` v1.0.0

The decisions.md section 16 item "a machine report for testers without Valheim", built as a separate public
repository: https://github.com/DoomMachine/Valheim-SeedLab-IntelTest, `main` = `89537c1`, tag and release **v1.0.0** "SeedLab machine report
1.0.0", published 2026-09-24 21:50 UTC (00:50 local on the 25th). Release asset
`SeedLab-MachineReport-1.0.0-win-x64.zip`: **37,764,988 bytes, SHA-256
`aaab4747479700b532c14d7bef63f7242da1287d86f09347826381e060aa3756`** (the release API's digest is the same),
with `SHA256SUMS.txt` beside it. Checked on GitHub 2026-09-25.

What is in it: `tools\SeedLab.MachineReport` (the program); `vendor\`, 52 files byte-identical to SeedLab
`11aeb8f` (WorldGen, Seeds, Runtime, the natives suite, `Half16.cs`; `VENDORED.md` lists every blob);
`natives\`, SeedLab's `groundtruth\natives` scrubbed (the one seed-text entry removed from
`natives-hash.json`, `manifest-natives.json` rewritten to the 7 files shipped); `reference\fingerprints.json`;
and an app-local .NET 10.0.12 runtime in `dotnet\` - the apphost looks only in `..\dotnet`, so no .NET needs
to be installed, and the runtime is redistributed under Microsoft's .NET Library licence, which the user
accepted. It runs 93 checks at four instruction-set levels in about 3 minutes (the user had been told 5 to
15), records only hardware and Windows facts, and writes nothing outside its own folder.

Verified before release: 93/93 on this machine at AVX-512, AVX2, AVX and no SIMD; nothing written outside
the folder (a run with TEMP, TMP, LOCALAPPDATA, APPDATA and USERPROFILE pointed at empty folders left all of
them empty); a fresh clone of `89537c1` built in a path containing the account name gave a byte-identical
zip, which proves both that the build reproduces and that no build path leaks; privacy scans of every commit
and every zip member. One audit; its fixes are
`5b9b8c1` and `89537c1`: a credit line in `README-FIRST.txt`, "no game files or assets" instead of the false
"nothing from the game is in this package" (the port and `natives\` are listed in `THIRD-PARTY-NOTICES.md`),
"and tested" dropped from the credit (nothing showed the user had run it), `<Copyright>` in
`Directory.Build.props`, and a path filter that stopped at whitespace. SeedLab was pushed first, because
`VENDORED.md` cites `11aeb8f`. The first report from a tester is the entry above.

Open: the vendored comments that name `testworldclaude` (`UnityRandom.cs:109`, `Half16.cs:13`) and a
`scratchpad` path (`UnityMath.cs:8`) are public upstream too; a fix goes into SeedLab first, then the files
are vendored again. The package predates `libm-dense`, so Windows 11's C runtime is only partly verified.

## 2026-09-25 - pushed: `8eee037..e5a8b90`

`git push origin main` - `main` only; the unaudited `perf-track` branch stayed local - sent `f48e682`,
`83d2f16`, `95bba24`, `3499936`, `86d75bb`, `11aeb8f` and `e5a8b90` to
github.com/DoomMachine/Valheim-SeedLab (`git ls-remote`: `main` = `e5a8b90`, checked 2026-09-25).

`e5a8b90` "Refresh the published skills from the knowledge base" is the refresh this history's 2026-09-24
follow-up asked for: 11 files of the repo's `.claude\` copies brought up to this knowledge base as of
2026-09-25 and scrubbed to the first publication's standard. Two first-pass scrubs were reversed on the
user's decision (the test-world names `ClaudeTestWold2` and `ClaudeTest` stay, and so do time zones);
valheim-modding `publishing.md` and the release-auditor agent are left out and listed in the repo's
`.claude\README.md`. It was committed once and amended before the push: the message was reworded (it
claimed no local paths, and a few passages still name folders on the author's machine; audit note 5) and the
two published history lines the push would make false were closed (note 4). The pre-push audit
found no blockers: 557 blobs across all commits scanned as
UTF-8 and UTF-16LE, every author and committer DoomMachine, no `Co-Authored-By`. Its note 6 is `831f55a`
(entry above); its publishing lessons went into the author's publishing procedure (left out, as above).

Left for the user (public since `3c4b214`; the push added nothing new): the character name in
`docs\specs\05-validation.md`, `src\SeedLab.Saves\CharacterProfile.cs` and `CharacterReader.cs`; about 80
lines outside `.claude` citing `scratchpad\` or the knowledge base; the install path in about 18 files.

## 2026-09-24 - dungeons are called by the caption on their door (dumper run 6 imported)

The plan in "the `axe-heads` preset, and dumper run 6 prepared" (below) was carried out: run 6 ran on
2026-09-24 and was imported the same evening. The game facts it settled (38 `Teleport`s in 19 location
prefabs, which token each carries, the Vegvisir pins, DN_Bossroom's inactive door) are in valheim-modding
`vanilla-behaviour.md` section 12; the new hold-out world is in valheim-worldgen
`zones-locations-vegetation.md`. Commits: `3499936` "Dungeons are called by the caption on their door" (branch
`run6-dungeon-names`), merged as `86d75bb`; then `11aeb8f`.

**The import.** The staged snapshot was the current one plus run 6's ten tables, `goldens\natives-hash.json`,
its four `B83592B8` goldens and `manifest-assets.json`, copied byte for byte; `manifest.json` is run 6's own
with only `files[]` recomputed (the union of both runs' paths, 47 files, each with its true size and
SHA-256) and one note saying so. Every table run 6 rewrote matched the previous snapshot except the stamp's
date; the two walks gained only `teleports`, `vegvisirs` and `waymarksCaptured`. **Order mattered**: this code
needs the run-6 snapshot (the strict loader demands the new fields, and an older dump fails naming by name),
so the old `data\1.0.15-59f53fb5` was moved to `_ModSource\_retired\data-1.0.15-59f53fb5-before-run6-20260924`
before the staged one replaced it and the branch was merged. `DumpSchemas` was regenerated; the three new DTOs are referenced through
`LocationOccupantsDef`, because the generator does not transcribe the non-sealed `InteriorDef` base class.

**The naming rule** (`DisplayNameSource.TeleportEnterText`, `src\SeedLab.Data\LocationDisplayNames.cs`): a
door's `m_enterText`, looked up in the dumped localization table. Only a door the game can use counts: active
in the prefab, with a target and a caption. Precedence: boss altar, trader, the Bog Witch convention, discover
label, door, prefab. The boss must win - The Queen's entrance door says "Infested Citadel", and the boss group
is derived from the name's source - and placed last the rule can only name a place that had no name, so no
existing name changed. Whatever names a place, its door caption also becomes an alias. **Shared captions
name nothing**: "Burial Chambers" (Crypt2/3/4), "Infested Mine" and "Putrid Hole" caption each prefab and
resolve to none of them; `vseed locations --name "Burial Chambers"` lists the prefabs, a query's `location:`
target is refused with them in its hint, and the web vocabulary drops the spelling without a note. **31 of
the 183 placed types now have a name** (8 bosses, 3 traders, 3 map-pin labels, 17 dungeon entrances), up
from 14. The wiki contradicts none of them; Bear Cave, Winding tunnels, Mörkhalla and The Prison have no
page to check against.

**The guide** (`11aeb8f`): `docs\game-data.md` walks a reader who has never installed a mod through making
the location data - BepInEx by hand or through a mod manager, building and arming the dumper, a throwaway
solo world, F4 and the log lines, copying the output into `data\`, `vseed data --verify`, removing the
dumper. Tested on Windows only; the in-game steps and some mod-manager details are marked Unverified. README,
CHANGES, `docs\search.md`, `docs\dumper.md` and the dumper manual caught up with run 6 (31 names, the dumper
retired again, F4 free). With the dumper retired, the comments that named knowledge-base scripts now point at
`tools\check-game-version.ps1` and `tools\decompile.ps1` - comments only, proved by building the dumper and
contracts DLLs byte-identical with and without the edits (`-p:DebugType=none`).

Verified: `vseed data --verify` 8/8 on the staged snapshot (47/47 manifest files, 186/186 prefabs walked,
183/183 placement order); placement identical, every instance's x/y/z bits, zone and biome, three ways (old
code on old data, old code on new data, new code on new data) on `hnBd9gJf2G` (12,287 instances),
`MWd8eV6svz` (12,314), `75539276` (12,228) and `8QHItAXH7v` (12,216); `LocationLab fresh --seed-hex B83592B8`
12,216/12,216 bit-identical, 909 sectors, 32/32 alt biomes, 178/178 prefabs; the door-named types place bit
for bit against the goldens of `0480A34C` and `B83592B8`; the dropdown golden changed in exactly the 17
door-named rows. On the merged tree: Runtime.Tests 173/173, Search.Tests 478/478, Acceptance 32/32, proofs
refuse/policy/blocks pass, `vseed selftest` and `vseed serve --selftest` PASS.

## 2026-09-24 - a file another program holds open no longer kills a run; a session log

**The defect** (the "Access to the path is denied" follow-up of the block-size entry below; the user asked
for it fixed and for a session log). A checkpoint save failed with a bare "Access to the path is denied."
whenever another program had the checkpoint open, and the whole run died with it (a CLI run exited 3 with no
path; in `vseed serve` the run's worker threads stayed parked for the life of the server). Why, and the retry
that recovers it: valheim-modding `pitfalls.md` section 2. A kill or a failure between the kept-set
snapshot's rename and the checkpoint's also corrupted a bounded run's resume (measured: 12 duplicated and 12
missing records out of 50).

What changed (commit `95bba24`; the design is in the author's working notes):
- Every temp-then-rename goes through `DurableWrite.Replace`: short retries (`RetrySchedule.Quick`, about
  1.6 s), then a diagnosis in plain words that names the file (in use, read-only, no permission, disk full).
  A periodic save that still fails is a warning and the run goes on; the last save of an unfinished run
  retries longer (`Patient`, about 14.6 s, announced on screen) and, if it still fails, is an error with
  the option to retry (`[r]etry / [g]ive up` in a terminal, a Retry saving button on the page). Workers are
  always stopped and joined, whatever throws. `SurvivorList` and map PNGs are written atomically too.
- **The snapshot and the checkpoint are one commit**: the snapshot is written under the generation
  (`<ckpt>.top` or `<ckpt>.top2`) the checkpoint on disk does not name, and the checkpoint's rename commits
  it; a snapshot whose `next_block` differs from its checkpoint's is refused.
- **A session log** modelled on BepInEx's `LogOutput.log` (valheim-modding `environment.md`): `<cache
  root>\logs\vseed.log`, emptied at every session start, `vseed.log.1`-`.4` while another session holds it,
  with the start time, what was checked, the integrity outcome, every retry and warning, and how the session
  ended. **Replaced the next day** by the two-log rotation (entry above; decisions.md section 14).
- Start-of-session access checks for the files a command will use, explained on failure, with `[r]etry /
  [a]bort` in a terminal; CLI warnings as they happen, `--json` fields, no "this is a bug" for a file
  problem; `vseed clean` counts only what it removed and lists what it could not.
- Web: a warnings list, a Retry saving button, event replay that never drops a warning or the end, and
  **every POST from another web page refused** (`Sec-Fetch-Site` not same-origin, or a foreign `Origin`;
  `curl` sends neither and still works) - before this, any page the user visited could POST to
  `/api/search/{id}/cancel`.
- Three adversarial reviews; all 24 findings from the three
  reviews were real (21 distinct). The blocker: a checkpoint that could not be read at a run's first save made the
  run write its new snapshot over the one the on-disk checkpoint named, so the next `--resume` was refused;
  a resumed run now takes the snapshot name from the checkpoint it loaded. Also: an unreadable checkpoint
  was reported as missing ("resuming would start from the beginning"); "it passed the access check, so
  something changed" was claimed when only the folder had been checked; event replay dropped warnings and
  `done` after 4,000 events.

Verified: `SeedLab.Runtime.Tests` **173/173**, `SeedLab.Search.Tests` **443/443** (sections 15-17 new), proofs refuse/policy/blocks pass, killtest 3/3 IDENTICAL bounded and keep-all, `vseed serve
--selftest` 15 PASS and 3 MEASURED, `vseed selftest` PASS. A live search whose checkpoint another process held
for 5 s warned, kept running and saved again when the file was released; in a real browser, the page went
"Not saved" (with the diagnosis), "still not saved", then "Saved" once the holder exited.

Still open: the interactive `[r]etry / [a]bort` and `[r]etry / [g]ive up`
questions and a Ctrl-C at them are untested (whether Windows ends the read as a give-up is unverified); the
CLI's disk-full exit and the page's handling of a lost stream or a 404 on Retry saving were checked by reading
only; a rotated run's periodic warning says "the checkpoint could not be saved" when it is the manifest that
is held (the file it names is right).

## 2026-09-24 - funnel stage 2 checkpoints where it was told, and `World..ctor` explained

**The defect (a follow-up of the block-size change, the user asked for it fixed).** Both front ends
built funnel stage 2 with a bare `SearchSession.Create`, which named its checkpoint from
`CheckpointStore.Root` - a second, hard-coded `%LOCALAPPDATA%\SeedLab\checkpoints` that ignored
`--cache-dir`, `SEEDLAB_CACHE_DIR` and (in the CLI) `--checkpoint`, while stage 1's survivor list,
named from the run's own path, honoured them. `vseed serve` never passed its runtime to the server, so
the server started a second `RuntimeContext` with none of the global options: `--cache-dir` reached
neither the tile cache nor any web checkpoint, and the process ran two throttles, two reaps and two
self-tests.

What changed:
- `SearchSession.Create` takes `checkpointDirectory` (both front ends pass their runtime cache root's),
  carried through the grid-raise recursion; `SearchSession.ForSurvivors` builds stage 2 and KEEPS the
  session's checkpoint path, so it cannot be lost again on either front end. `CheckpointStore.Root`
  resolves through `CacheRoot` (`Create = false`) and is only a library fallback; `DefaultPath` is gone
  (`PathIn(dir, hash)`); `CleanOrphans` tidies the run's own directory.
- `vseed serve` passes `Runtime = rt.Context` (also for `--selftest`'s live server): one runtime per
  process, disposed once by `CliRuntime`. Its help no longer says "It writes nothing".
- **Migration** (`CheckpointStore.AdoptLegacyStageTwo`): on a funnel `--resume`, when the run's own path
  has no checkpoint and differs from the old fixed one, a file at the old location that passes the same
  `Load` + `MustMatch` a file at the new path must pass is moved (snapshot copied, checkpoint written at
  the new path naming the copy, then the legacy pair deleted) with a notice; anything else is left
  untouched and said. The legacy path is a parameter, so the tests never touch the real
  `%LOCALAPPDATA%`.
- The sample-vs-funnel rule: stage 2 now always checkpoints at the run's path, so a SAMPLE run's
  checkpoint of the same plan there is always refused before anything runs (it used to be a warning and
  "left as it is" outside the default layout).
- **`World..ctor`, explained for non-programmers (the user's request).** The README explains it where a
  reader first meets it (after the `vseed seed` sample): the real name of the game's `World` constructor
  - `.ctor` is .NET's name for every constructor, hence the two dots - not a typo or a truncation. Every
  `vseed` message that shows the name now reads "the game's World constructor (World..ctor)"
  (`vseed seed`, `vseed hash`, `vseed space`, the int32 warning, the selftest row V1b). Developer text
  (code comments, `docs\specs`) keeps the bare name.

Verified: `SeedLab.Search.Tests` **354/354** (section 14, 33 new checks: a real CLI funnel stopped in
stage 2 under `--cache-dir` and resumed from there, a web funnel through `vseed serve --cache-dir`,
serve's cache line, and the move - adopted and resumed byte-identical to an uninterrupted run; another
survivor list's file, a sample's file and an unreadable file left byte-identical), `SeedLab.Runtime.Tests`
**125/125**, Acceptance **32/32**, `vseed selftest` 13 ok; proofs refuse/policy/blocks exit 0;
`vseed serve --selftest --cache-dir <scratch>` all PASS. Every run used scratch cache roots.

Committed as `f48e682` (stage 2 / serve) and `83d2f16` (`World..ctor`); pushed on 2026-09-25 with the rest of that
day's work.

## 2026-09-24 - a block for every worker, and what a budget really bounds

**The defect (reported by the user).** `vseed search <query> --seeds N` with N below workers x block
size left workers idle: one worker computes a whole block and the size was a fixed 256, so `--seeds
512` on 8 workers was 2 blocks and 6 workers did nothing - and the report printed that 2-worker rate
"on 8 threads". Measured: `axe-heads` at 64 seeds took 1.6 min as one block of 64 and 16.4 s as eight
blocks of 8. `docs\measurements.md` also said a `--budget` "cannot stop a run sooner than one block per
worker"; that floor does not exist (`--budget 0.001s --seeds 512` evaluates 0 seeds, and reported
"coverage 100.00 % ... (the whole space)"). The premise that made the size free to choose was measured
first: a completed run's results file is byte-identical across block sizes (jsonl, json and csv, keep N
and keep all, evict limits, screen-then-verify); compressed rotated output is not byte-stable even at a
fixed size (gzip boundaries follow the flush clock), so it is never compared.

**The user's two decisions.** (1) One automatic ceiling of **256 for both front ends**, chosen over the
recommended 64: the web page's hard-coded 64 is gone and an empty Block size box means automatic (a
location-tier block can then be about 6 to 8 minutes of one worker, which is when Stop and the first
streamed results land; the box gives finer steps). (2) **`--budget` covers funnel stage 1 too**: the
CLI's stage 1 obeys the wall (it ran with none), and the web page stops after a stage-1 wall stop or
Stop press as the CLI does (it went on into stage 2 with a fresh budget), so one budget sentence is true
on both.

What changed (`BlockSizing` is new; `SearchSession`, `SearchPreflight`, `Checkpoint`, `FunnelRun`,
`SearchRun`, `ScanPlan`, `QueryModel`/`QueryReader`, the CLI's `SearchCommand`, the web engine, planner,
model, translator and page, the estimator):
- **The rule, shared** (`BlockSizing.Decide`): automatic is 256, or `floor(limit / (4 x workers))`
  (never below 1) when 256 would give a worker fewer than four blocks; one worker keeps 256. Four per
  worker because blocks are not equal in time. A size that is given (`--block-size`,
  `search.block_size`, the Block size box) is kept and warned about with the idle count and what
  automatic would pick. `--block-size 0` is a usage error (it used to run as blocks of 1), and so is a 0
  in the box. The plan says which rule applied and why, in counts, never "Nx as long".
- **Resume**: a resumed run keeps its checkpoint's size on any thread count (a resume point is a block
  number), adopted only from a checkpoint that matches the run on everything else
  (`Checkpoint.MatchesExceptBlockSize`), so a funnel stage-2 file or another `--seeds` never feeds the
  main plan; `MustMatch` checks the seed budget before the block size. A different size asked for is
  refused, naming the size that works and the file: before the prompt for the main run, at the gate for
  funnel stage 2 (stage 2's checkpoint is recognised by the survivor list stage 1 makes).
- **The session plans its workers** at the grid it runs at (`plannerAtGrid`, after any raise) and keeps
  that `WorkerPlan`, which both front ends print instead of planning twice (`SeedLab.Search` now
  references `SeedLab.Runtime`). The decided size is never written back into the query.
- **Funnel stage 2** is sized by the same rule over its survivors, decided once for the gate and the
  plan; the gate prints its size line, its warning and, with a budget, stage 2's own bound.
- **Rates**: `SearchOutcome.BusyWorkers` (counted at the claim) labels a rate "(N of M workers had
  work)" on the CLI, in `--json` (`busy_workers`, `block_size`, `blocks`) and on the page, which keeps
  a rate as the machine's only when every worker had work. The dry run times "this run" by its busiest
  worker and prints the scale it used.
- **Budget**: `--budget` sets `q.Search.Wall`. The plan's budget line states the overrun: up to one
  block of one worker's time past the budget (the largest block left), at most one block per busy
  worker in flight, plus the workers' start-up and the final write. `StoppedByWall` and `StoppedByUser`
  are false on a complete run. A 0-seed stop reports 0 % coverage (`ScanPlan.CoverageLine(long)`).
- **Review fixes** (three adversarial reviews of the implementation): a funnel `--resume` over a SAMPLE
  run's checkpoint no longer adopts its block size - the CLI decides the strategy before the session
  (sound because a grid raise cannot change it, pinned by a check) - and says the funnel does not
  continue that file, refusing before anything runs when stage 2's checkpoint is that same file; resume
  refusals have their own header and keep the checkpoint path on one line (it can hold a space); a Stop
  after the last claim no longer throws a finished stage 1 away, and the CLI remembers a Ctrl-C and hands
  it to the next run, so one pressed then - or while the gate measures placement, when no run was live -
  stops stage 2 at its first block boundary (a real Ctrl-C sent during the gate: 0 of 400 placed,
  survivor list and checkpoint kept; before, by reading the code, stage 2 ran in full); the page now
  ends a funnel whose stage 1 kept no seed and a run that fails between stages (they published an event
  it had no listener for,
  and the browser replayed the run every few seconds); the page's plan block prints the session's final
  grid (`SearchSession.Create` re-renders the line); no spreading sentence at zero survivors; singular
  "1 block" / "1 seed"; a stale `axe-heads` comment; `--threads` help. The `proof refuse` fixture's
  must-have filtered nothing and was refused as vacuous; it is 5,000,000 m² now and the proof exits 0.
- Docs: README, CHANGES-FOR-THE-USER, `docs\search.md`, `docs\measurements.md` (the false floor corrected
  in place, dated, every measurement kept), `docs\finding-a-seed.md` (re-captured: 400 seeds of
  gentle-start in 34 blocks of 12, 44.9 s at 8.9 seeds/s, where 2 blocks of 256 took 3.4 min at 2.0;
  the same 82 matches and top 10), `schema.md`.

Verified: `SeedLab.Search.Tests` **321/321** (244 before; sections 11-13 are new, 77 checks),
`SeedLab.Runtime.Tests` **125/125**; `proof refuse`, `proof policy` and `proof blocks` exit 0 - `blocks`
finds jsonl, json and csv at keep 50 and keep all identical at the automatic 62 on 8 threads, 256 on 8
and 7 on 3, and a run stopped on 8 threads at the automatic 187 and finished on 3 (adopting 187) equal
to the uninterrupted `--block-size 7` run, bounded and keep-all. `killtest.ps1` 3/3 IDENTICAL bare, and
3/3 each with `-AutoBlock -ResumeThreads 3 -Seeds 6000 -Grid 24 -Radius 5000`, bounded and `-KeepAll`.
`vseed serve --selftest` all PASS. A real CLI run hard-killed on 8 threads at the automatic 187 and
resumed on 3 threads ends with the uninterrupted `--block-size 7` run's SHA-256, and a funnel whose
stage 2 the budget stopped on 8 threads (automatic 12) and that resumed on 3 (adopting 12) equals a
fresh `--block-size 7` funnel. `proof estimate` reports 1 estimator violation, as the build before the
change does.

Follow-ups, not in this change:
- **Fixed the same day** (entry above): funnel stage 2 checkpointed in the default cache root whatever
  `--cache-dir` or `SEEDLAB_CACHE_DIR` said, and `vseed serve` ignored `--cache-dir`; stage 2 now keeps the
  run's path, serve shares the command's runtime, and old stage-2 files are moved on `--resume`.
- A resumed leg's report puts cumulative "seeds evaluated" beside this leg's rate; the web planner
  ignores the `SearchThreads` ceiling; the page keeps stage 2's placement rate as the machine's.
- The example in `schema.md` is refused by `vseed explain` (it sets `reduce` with no segments).
- **Fixed 2026-09-24 (`95bba24`, entry "a file another program holds open no longer kills a run")**:
  observed while testing, present before this change: a checkpoint save failed with "Access to the path
  is denied" while another process held the checkpoint file open (a harness polling it every 50 ms
  triggered it); a virus scanner or file viewer could do the same to a real run.
- **Done (2026-09-25): the published knowledge-base copies were behind (2026-09-24).** `valheim-modding\references\pitfalls.md`
  has changed since the repo copy was last committed (`8eee037`, 1371 lines vs 1385 at 16:10 and 1424 after
  the `2a63e30` lessons). Today's publishing lessons came first, then TomTom 1.1.1's in sections 1 and 8, then TomTom
  `2a63e30`'s test lessons in section 1 (a new entry, "A test can pass for the wrong reason", which corrects
  the held-handle advice). Refresh the repo's
  `.claude\` copies and re-run the scrub checks before the next push ("republished clean" entry below). Refreshed and
  re-scrubbed before the 2026-09-25 push; the rule stands for every later push.

## 2026-09-24 - grid warnings only for goals the grid measures

**The defect (reported by the user).** A query whose goals were all location or group goals was told
two false things at a coarse grid, by `vseed search`, `vseed explain` and the web page alike: "grid =
384 m is coarser than G96 ... a must-have at this grid is not a filter" (the check counted EVERY
must-have) and "every metric in this run is DEFINED on that grid and is not comparable with a G12
result" (written before any goal was looked at). Placement never reads the query's grid: it builds its
own 2048 x 2048 @ 12 m biome-point grid, and `explain` at G384 and at G12 gave bit-identical values.
boss-rush and dungeon-delver (both G384) got both warnings; all-traders (three exact trader goals and
one nice Black Forest distance, G384) got the second, which read as covering its trader goals.

What changed (`GridPolicy`, `CompiledQuery`, `SearchPreflight`, `SearchSession`, the new
`GridLadder`, `ExplainCommand`, the web planner and page):
- **One predicate** for "the grid measures this goal": `GridPolicy.SamplesGrid` (available, T2/T3, not
  a location goal), with `IsBulkGridMust`, `IsFineOnlyGridMust` and `OnlyPlacement` built on it. The
  CLI, explain and the web ladder all read these.
- **The G96 warning** counts only grid-measured must-haves in the counting row, is keyed on the verify
  grid (a `screen_grid` coarser than the grid no longer hides it), names its goals, and drops the false
  "not an approximation" clause. It is no longer silenced by a NICE fine-only goal - that had hidden a
  true warning.
- **The run-level grid warning** names only the grid-measured goals the per-goal "NOT comparable"
  warning does not already name, and when heights are sampled adds the records' side metrics
  (`land_km2`, `ocean_share`, `highest_peak_m`, plus `largest_island_km2` when islands are measured),
  of which the peak and the island are not comparable. With no goal left to name, the side metrics get
  their own line (a river-count query; a lone spawn-island must-have under `screen: off`). Nothing is
  printed for a placement-only query, or for one that samples biomes alone and whose grid goals already
  have their own warning - all-traders' only grid warning is now the per-goal one about
  `blackforest-near`.
- **The per-goal "NOT comparable" warning** fires on any grid that is not the game's own, so G10 is
  covered.
- **Under `screen: off`** a fine-only must-have that no per-goal warning covers (`area_above_height`) is
  warned about with wording that says nothing is raised. The raise itself still uses the full set, so
  large-continents, island and spawn-island must-haves are still raised to G12.
- **The placement plan note** moved from auto-pick into the preflight (every screen mode, only for a
  placement-only query). It says the grid changes no value but is part of the run hash (checkpoint,
  survivor list), and adds "another grid visits different seeds" only when that is true: a shuffled
  run with no `search.key` that covers part of the range or has a `budget.wall`. The false "except the
  side metrics" clause is gone. The coarse-grid plan note names its goals.
- **explain** printed every compiled warning twice and, after a grid raise, rebuilt the session and
  lost the name notes; both are fixed (`SearchPreflight.WarningsForOneSeed`). A raise's notes now reach
  the plan block, and they lead `Grid.Notes` in the same order.
- **The web ladder** comes from the shared `GridLadder`: river/lake/stream must-haves are not called
  "no longer a filter", the flat table only for placement-only queries, a variant for nice goals
  whose ranking depends on the grid, a reason for every rung sent by the server (the page's own
  fallback was false under `screen: off`), no "raises" under `screen: off`, and "each seed gets the
  same must-have verdict" rather than "the same seeds".
- Docs: `07-features.md` sections 2.1 and the parser rules, `schema.md`'s `grid` row, `docs\search.md`,
  and the comments in the balanced, axe-heads and custom presets (outside the canonical hash).

Verified: `SeedLab.Search.Tests` **244/244** (168 before; section 10 is new, 76 checks through
`SearchSession.Create` with the real location table). Against the library before the change, 40 of the
59 section-10 checks that compile failed and the other 19 are guards plus the data check. An adversarial
review of the first cut found a lost true warning (the side metrics when every grid goal already had its
own warning) and six wording and doc defects. Eleven checks were added for them, and ten failed against
that cut; the eleventh is a guard. CLI and web build with 0 warnings; `vseed serve --selftest` passes;
`proof policy` output is identical to the previous build. `proof refuse` exits 1, with output identical
to the previous build (see the follow-ups). 41 `search --dry-run` / `explain` probes were compared before
and after. 20 live web preflights were read, and for the cases that also have a CLI probe the page's
warnings match the CLI's word for word.

Follow-ups, not in this change:
- A river, lake or stream must-have at G12 under `screen: auto` still gets a pointless G24 screen pass.
  `GridPolicy.For`'s default branch treats T4 as a counting metric, so the plan says "screen at G24 ...
  every must-have here is a counting metric" while the ladder says no must-have is measured on the grid.
- `proof refuse`: the "with --accept-scan-order" case uses an `area_within >= 1,000 m²` goal that the
  vacuity check refuses first.
- large-continents: the plan's "measure once at G12" sits beside a "counting metric" note.
- Screening notes still hard-code "12 m" after the grid is dropped.
- Grids are printed with "0.###", so `grid: 12.0004` reads as G12.
- After a raise, the per-goal unsafe-must-have sentences are only counted in the confirmation; they
  never reach the warnings.
- The CLI's `--budget` does not set `search.budget.wall`, so it does not trigger the seed clause.

Published as commit `c53c58b` on github.com/DoomMachine/Valheim-SeedLab.

## 2026-09-24 - republished clean: the first push leaked a Steam ID

**What went wrong with the first push (entry below).** Its scan looked for the user's name, email and
secret patterns, not for game and platform IDs: `docs\specs\05-validation.md` quoted a save's
player-history entry with the user's full SteamID64 and PlayFab id, three spec paths carried the Steam
account ID inside `...\steam\userdata\<id>\...`, and 1,193 agent-session scratch paths (with the session
UUID) were in `docs\measurements.json`. A publication audit (4 auditors + critic) found them. The user
chose to delete and recreate the repository rather than add a fix commit; GitHub then answered "No
commit found" / HTTP 422 for the old commit. The recreated repo's own "Initial commit" (`3ce22f6`, LICENSE
only) was kept, and the scrubbed project pushed on it as `3c4b214` (fast-forward, no force). Contributors:
DoomMachine only.

**The user's standard:** credit Claude properly in the
READMEs (never as a co-author); no instruction may point outside the repo (scripts ship standalone, no
reference to Claude); keys and IDs removed completely; machine details other than hardware and the OS
version scrubbed; time zones are NOT sensitive; "IP" means IP address (decompiled excerpts may stay).

**What the pass shipped:** Credits section + a credit line in every README; `THIRD-PARTY-NOTICES.md`
(FastNoise MIT, text fetched from github.com/Auburn/FastNoise_CSharp; Valheim non-affiliation);
`tools\check-game-version.ps1` (exit 0/1/2/3) and `tools\decompile.ps1` + `tools\decompiler\` (the
installed ILSpy's engine first, ilspycmd second; `Teleport` output identical to this skill set's own
decompile); scrubbed copies of the seedlab, valheim-worldgen and core valheim-modding skills and the
api-investigator agent under the repo's `.claude\`. **Those copies are a snapshot of this knowledge
base**: when these files change, refresh the repo copies and re-run the scrub checks (the standard
above) before the next push. Hold-out claim corrected in README/docs/tests; the
publication agents also fixed stale scratchpad citations to the published `docs\studies\` copies,
`gen_schema.py`'s hard-coded root, `preflight.ps1`'s hard-coded game path, and "this 8-core machine"
in a runtime string. Deferred until the dumper is retired: comments in `tools\SeedLab.Dumper\src` and
`src\SeedLab.Contracts` naming knowledge-base scripts (all resolve inside the repo now).

**A trap worth remembering:** the first review agents were derailed by a user message that arrived
mid-workflow (the `World..ctor` question): five of six declined their task as "not the user's
request". Workflow prompts now open with an explicit MANDATE paragraph quoting the user's request.

## 2026-09-24 - SeedLab is a git repository, public on GitHub

https://github.com/DoomMachine/Valheim-SeedLab, created by the user. The user's decisions: **public**,
**code only**, **name scrubbed**, **MIT like TomTom**.
- `.gitignore` keeps out `bin/`, `obj/`, the dumper's `build/`, and **`/data/` and `/groundtruth/`**:
  the snapshot is the game's own content (location tables, its English text) and ground truth is the
  user's worlds and a game log. The README's new first section, "Cloned from GitHub? Two folders are not
  here", says what a clone can do: terrain answers work; location answers refuse with exit 3 until a
  dump from the reader's own game is copied into `data\`; `vseed selftest`, the acceptance tests, the
  location gate and the serve tile check report the ground truth missing. The machine self-test does
  NOT need it on x64 (`NativesGoldenSuite` registers only when `groundtruth\natives` exists).
- The Windows user folder path (the user's real name, long and 8.3 forms, plain and JSON-escaped) was
  replaced by `%USERPROFILE%` - 1,195 occurrences in `docs\measurements.json` and three specs.
- Found while checking what would be published, and fixed: three characters earlier inline scripts had
  mangled (`\1` became U+0001 in two `data\1.0.15-...` paths, `\v` a vertical tab in a
  `MetricCatalog.cs` comment); four CRLF files normalised to LF; `gen_schema.py` wrote CRLF on purpose
  and now writes LF. `.gitattributes` is `* text=auto eol=lf`.
- Verified before and after the push: 0 `Co-Authored-By` lines in the whole history, every author
  `DoomMachine <58111381+DoomMachine@users.noreply.github.com>`, 0 occurrences of the user's name or
  email or any secret pattern in any pushed tree (that scan did not cover game or platform IDs; the later
  publication pass, "republished clean" above, did); GitHub's contributor list shows DoomMachine only.

## 2026-09-24 - the `axe-heads` preset, and dumper run 6 prepared for dungeon names

**1. `axe-heads` preset** (`src\SeedLab.Search\Presets\axe-heads.json`). Must: `group:axe_head_houses`
`nearest_distance` **from spawn** near 150 m. Nice: `location:WoodHouse6` nearest from spawn near
400 m (weight 2, the house whose chest always exists) and houses within 500 m of spawn at least 8.
Calibrated BEFORE writing on two independent 512-seed samples (numbers in valheim-worldgen
`zones-locations-vegetation.md` 5.3): 150 m keeps 37.5 % / 37.9 %. Two things the calibration taught:
- **A plain `--seeds 512` uses 2 of 8 workers.** 512 seeds is 2 blocks of 256 and one worker computes a
  whole block, so both samples ran at 1.3 seeds/s; with `--block-size 16` the same query runs at
  4.2. Nothing in the run output says the other six workers were idle. Size blocks to the sample.
- **`grid` is irrelevant to a location-only query, and setting it coarse is harmful.** 256 seeds at
  G384 took 60.1 s against 61.6 s at G12, three seeds explained at G384 gave distances bit-identical
  to the G12 sample, and the G384 run printed two warnings that are false for location goals ("a
  must-have at this grid is not a filter"). The preset therefore omits `grid`; the warning logic was
  flagged for a separate fix (it counts every must-have, not only grid-measured ones).
Measured by the `docs\measurements.md` section-6 protocol: 4.12 seeds/s median, 41.8 % hit rate,
33.1 years for the whole space (row and JSON entry marked as added a day after that pass).
`SeedLab.Search.Tests` 168/168 with 14 of 14 presets compiling against the real location table. The
CLI and web projects were rebuilt on the changed Contracts: the embedded preset is byte-identical to the
file, `vseed presets list` shows it, `vseed data --verify` 8/8, `vseed serve --selftest` all PASS.

**2. Dungeon names need the dumper once more.** The name a player sees on entering a dungeon is
`Teleport.m_enterText` (decompiled: `Interact` hands it to `MessageHud.ShowBiomeFoundMsg` after a
successful teleport; no code assigns `m_targetPoint`, so doors are linked in the prefab). The 16
`location_*` tokens (14 place names plus Enter/Exit) are in the localization table but no dumped file
says which prefab carries which - so SeedLab keeps showing `Crypt2` rather than guess, and the user's
wiki IDs (Burial Chambers = `DG_ForestCrypt`, Sunken Crypts = `SunkenCrypt4`, Infested Mine =
`Mistlands_DvergrTownEntrance1/2`) are kept as the cross-check for the real data. The dumper gained
`ChildWalk.ReadWaymarks` (every `Teleport` and `Vegvisir` in every location and room prefab, own
try/catch and own flag `waymarksCaptured`, so a throw cannot unset `occupantsCaptured`), DTOs
`TeleportDef` / `VegvisirDef` / `VegvisirLocationDef`, and preflight `Check-Field`s plus the three types
in the field-coverage gate (two `m_hoverOffset` waivers). Adversarial review found one real defect
before install: widening `Blank()` to empty the name arrays also made `Discard()` - the post-throw wipe
- empty arrays already captured while their flag stayed true ("captured, and this altar has no boss").
Fixed with `BlankNames()`, which resets flag and arrays together and is called only where nothing has
been captured yet; the re-review compared every method against the run-5 build (only the intended
methods differ). Preflight -SelfTest PASS 484/0. Installed, armed `assets` only, SHA-256
`SeedLab.Dumper.dll` 85F54A85...6103C7A and `SeedLab.Contracts.dll` 81056CC6...DD34A42 matching the
build. **Waiting on the user's F4.** After it, in order: diff the new dump
(`%USERPROFILE%\AppData\valheim-dumper\1.0.15-59f53fb5\`) against `data\1.0.15-59f53fb5` - every
table the change does not touch should be identical apart from the manifests' own stamps and hashes,
which is itself a determinism check; **copy the current snapshot to `_ModSource\_retired\` before
importing over it** (move, don't delete, applies to data too); import; `vseed data --verify`;
regenerate `DumpSchemas` (dump first, regenerate second); add the naming rule and its tests; retire the
dumper. Deferred on purpose until the dumper is retired, because any source edit changes the installed,
reviewed DLL's hash: `ChildWalk.ReadWaymarks`'s doc ("the only way to read it") and `TeleportDef`'s
("needs the game's own loader") overstate it - the matching Unity editor is an untried second route.

**3. The Unity editor route was considered and deferred.** Unity 6000.0.75f1, the editor that
exactly matches the game (valheim-modding `environment.md`), could read the bundles without a game
session, but only after settling whether the bundles carry type trees, whether `assembly_valheim`
loads as an editor plugin, and how the SoftRef `manifest` maps prefab to bundle - hours of new tooling
against a 30-line addition to a proven walk plus one F4. It is the option for a future question that
needs repeated bundle reads.

**4. The wiki's World seed page, checked against SeedLab 1.0.15.** It lists community seeds with
claims but no game version, and marks two of its own entries "outdated". `feret2` and `wVJCZahxX8`
agree with SeedLab in direction (an Eikthyr 384 m / 788 m from the centre; for `feret2` an Elder and
Haldor 130 m apart due south; for `wVJCZahxX8` the nearest Elder 2 km south). `Swamp` does not: its
tightest set of Elder + Bonemass + Moder + Yagluth spans 3.7 km to the WSW, not "a minute's run east
of spawn". Read as a stale claim, not as evidence against SeedLab, whose placement is proved
bit-exact against this build's own `.db2`. The user-supplied "Point of interest" page was checked the
same way: biome agrees on all 113 rows that name a dumped prefab, quantity on 104, and the 9 that differ
are all higher on the wiki (valheim-worldgen `zones-locations-vegetation.md` 2.3 lists them).

## 2026-09-24 (later) - a note now follows the place it is about

`vseed locations <seed> --type boss` printed the Bog Witch's `Unverified:` note, on every run, about a
trader that selection does not contain. The note is correct and the user's decision requires its
wording kept; printing it where it does not apply was the defect. It shares `Out.Warn` with the
`m_unique` caveats and the seed-ambiguity note, and **a caveat repeated where it does not apply is how
a reader learns to skip the one channel where this tool says what it cannot know.**

**Every note now carries the place it is about.** `LocationDisplayNames` gained a public
`LocationDisplayNote { string? Prefab; string Text; }`; `Prefab` is null when the note is about the
TABLE (an unreadable localization file, a rule that matched nothing, a boss count that no longer
matches the build) and set when it is about one place. Internally the notes are collected through a
small `NoteList` whose plain `Add` means table-wide and whose `AddFor(prefab, ...)` means one place -
so a note site that says nothing about ownership keeps the meaning that is always safe to print, and
the seven per-prefab sites say so in one token instead of balancing a second pair of brackets. Wording
is unchanged at all 16 sites.

**Why the prefab is carried rather than parsed back out.** Most of these notes begin with the prefab
and a colon, so a caller CAN recover it with string surgery - and will get it wrong on the notes that
mention a prefab mid-sentence, and on any note a later edit rewords. Carrying the place is cheaper
than recovering it and cannot drift from the wording.

**`LocationsCommand` prints in two passes.** Table-wide notes go out immediately, BEFORE anything can
fail, because "the localization table could not be read" has to reach a user whose `--name` then does
not resolve or the failure reads as arbitrary. Per-prefab notes wait until the selection is settled and
are filtered to it (`NotesFor(selected, includeTableWide: false)`).

**Nothing is suppressed and no other consumer changed.** `LocationCatalog.Notes` and
`LocationDisplayNames.Notes` still return every note in the same order, so `vseed seed`, `vseed serve`
and `vseed data --names` - which all show the whole table - are byte-identical. Measured after:
`--type boss` and `--name GDKing` print nothing; `--type trader`, `--type all`, `--name BogWitch_Camp`
and `--name "The Bog Witch"` print it; an unresolvable name still errors cleanly. New guard,
`NameChecks.CheckNoteOwnership`: the flat and structured lists agree sentence for sentence and in
order, every owned note names a prefab the table has, `NotesFor(every prefab) == Notes` (so filtering
defers rather than loses), and the Bog Witch's note is absent from a selection without it and present
in one with it - the last written so that it passes vacuously if a future dump gives that `Trader` a
real `m_name` and the note disappears.

Tests: SeedLab.Search.Tests 168/168 (163 before, plus the five new ownership checks), vseed serve --selftest all PASS, vseed data --verify 8/8.

## 2026-09-24 - the map calls places by their names, and the toolbar says what is on

The user's five complaints about the GUI, closed and each demonstrated by running something. Design
and evidence: `docs\studies\third-party-tools.md` for the naming sources,
`CHANGES-FOR-THE-USER.md` for what a player sees.

**Names are derived from the dump, never curated.** `src\SeedLab.Data\LocationDisplayNames.cs` runs four
rules, first match wins: an `OfferingBowl` whose `m_bossPrefab` is a `Character` with `m_boss` (8
altars), a `Trader` whose `m_name` resolves (Haldor, Hildir), a resolved `Location.m_discoverLabel`
(Forge of Potential, Sealed Tower, Charred Fortress), then the prefab. **14 of the 183 placed types get
a name; the other 169 keep their prefab**, and `DisplayName` is null rather than a copy of the prefab
so "unnamed" and "named the same as its prefab" (Bonemass) stay distinguishable. The derived boss and
trader sets are asserted against the curated `LocationCatalog` arrays and any difference is a Note; the
arrays stay for one release.

**The one human join.** `BogWitch_Camp`'s `Trader.m_name` is the EMPTY STRING in this build, so rule 2
finds nothing. The user confirmed from the game that the trader is the Bog Witch, so the name comes
from `$npc_bogwitch` by the convention Haldor and Hildir follow, under its own
`DisplayNameSource.TraderNpcTokenConvention` and with an `Unverified:` note that prints. It is pinned
to that one prefab and that one token deliberately: a general "$npc_ + lowercased prefab" rule would
silently name a future trader.

**Both spellings, everywhere, with the prefab still the identity.** `location:The Elder`,
`location:the elder` and `location:GDKing` compile to the same goal; the target is rewritten before
`Compile`/`Hash`, so the run hash, the checkpoint, the CSV header and every record say `GDKing`.
Verified byte-identical: a query file written on 2026-09-23 still canonicalises to the hash its
checkpoint recorded. `--json`, CSV and the canonical query form speak prefab only; display names are
additive fields.

**Ordering** is bosses A-Z, traders A-Z, then the remaining 172 by biome in map-legend order with
"Several biomes" last (only 7 of 183 name more than one biome). Sorted on the exact visible string by
the user's decision, so "The Elder" is under T; empty groups are omitted rather than hard-coded.

**Complaint 5, and the honesty it forced.** `group:axe_head_houses` is `WoodHouse2` + `WoodHouse6`, and
the Places filter matches a feature name so typing "axe" finds them. The two houses are NOT alike and
the UI says so: `WoodHouse6`'s chest is unconditional (31/56), `WoodHouse2`'s sits behind `RandomSpawn`
50 at `m_chanceToSpawn 50` (31/112), and **those are different kinds of uncertainty** - the
`RandomSpawn` draw comes out of the zone-seeded stream and IS computable offline (SeedLab does not
replay it yet), while the chest's contents are an unseeded `UnityEngine.Random` draw and never will be.
They are never multiplied into one forecast. `axeChestNearest` is answered; **`axeChestsAfoot` is
refused by name** for want of a walkable flood fill, rather than silently answered with a Euclidean
disc that counts houses across an ocean.

**The toolbar** (complaint 4) now carries state four ways, none of them hue: fill polarity, text
polarity, a glyph whose SHAPE also says whether the control is one-of-three or an independent switch,
and the literal word ON/OFF on the layer toggles. The measured defects it replaces: on/off label
contrast **1.448:1** where 3:1 is the floor, pressed fill **~1.05:1**, only a 1px ring clearing 3:1 and
only over a dark map, and **no hover feedback at all on a pressed control** because the pressed rule
beat the hover rule on every property. Pan/Ruler/Mark became a real radiogroup with arrow keys; `R`,
`M` and `Esc` are unchanged.

**Two defects found by the adversarial pass and fixed after it.** The web goal id was built from the
RAW target, so typing a display name produced the CSV column header `location-The Elder-nearest-distance`
- a header with a space in it - while the run itself used `GDKing`; `SearchPlanner` now passes an
oracle-backed canonicaliser into `QueryTranslator`, and `DefaultId` also sanitises, because resolution
is best-effort by design. And "World features" named two different sets in one panel: the residue
category (about 150 rows) and the one curated feature that closes complaint 5. The residue is now
"Other places", because the user's own phrase "World Features may also be missing" meant the curated
sense.

**Measured.** Cold `vseed locations <seed> --type boss` went from 933-1014 ms to 1252-1356 ms: a
once-per-process name table (localization 4 ms + the occupant slice ~172 ms + 3 ms derivation). The
per-seed path is untouched. The 71 MB `roomchildren.json` room join measured 290-390 ms cold, over the
250 ms line the plan set, so the shipped `Build` takes the plan's own fallback and does not read it;
`BuildWithRoomJoin` has exactly one caller, a test that re-derives The Queen from the room file and
diffs the result row for row. No unhashed cache file was added.

**Tests:** `SeedLab.Search.Tests` 163/163, `SeedLab.Runtime.Tests` 122/122, `SeedLab.Acceptance.Tests`
PASS, `vseed data --verify` 8/8, `vseed serve --selftest` 14/14 including a new row asserting every
named row has a source and no name echoes its own prefab (Bonemass exempt, stated out loud).

## 2026-09-23 - reconciled against both public tools, and the axe-head answer

Full write-up: `docs\studies\third-party-tools.md`.

**SeedLab and bobmitch.com agree; valheim.tools is the outlier.** bobmitch caches a whole generated
world in its own IndexedDB (`valheim-map` -> `worlds`, keyed `<int32>|<worldGen>|<build>|<hash>`) as
`sites: {prefab: [x,y,z,...]}`, so it can be compared instance by instance rather than headline by
headline. On `bmbp74` and `138fmg`: **183/183 prefab types, 0 types present in one and not the other,
12 286/12 286 and 12 278/12 278 instances, 0 of 183 per-type counts differing**, and aggregate
sum(x)/sum(y)/sum(z) within 0.01 m. 155 of 183 per-type coordinate sets on `bmbp74` hashed identically
at 0.1 m; the other 28 are **rounding ties, not different worlds** - one or two points differing by one
unit in the last printed decimal, with per-type sums agreeing to 0.004 m over up to 500 points.

**Do not call that bit-exact.** A tie at one decimal needs a value of exactly k/20, which is never a
dyadic float, so identical inputs would have printed identically; the ties mean the floats really
differ by ~1e-4 m. bobmitch is JavaScript (float64); SeedLab reproduces the game's float32 and is the
one with the bit-level proof against real worlds. The claim is "two independent implementations agree
on every instance to better than a decimetre".

**Two corrections to what was said in conversation before this check:**
- The earlier reconciliation guessed the valheim.tools residue (188/216 = 87 %) was **explained by the
  1.0.7 vs 1.0.15 build gap**. It is not. bobmitch is explicitly 1.0.7 and matches SeedLab 1.0.15 on
  every location, so that gap explains nothing at the location layer. On `138fmg` both implementations
  give Moder 3 344 m and Yagluth 4 190 m where valheim.tools claims 2 000 m and 5 400 m, and they agree
  on the spawn point too (`-324.1, -325.3`). The residue is valheim.tools', whose build and method are
  both unstated.
- **Sampling cannot explain it either.** The user asked whether a 128x128 or other grid on their side
  would. Location placement does not sample - `GetRandomPointByBiomes` walks the game's hard-coded
  2048x2048 grid at 12 m - which is why `vseed locations` has no `--grid`. Sampling is a *terrain*
  concept (biome share, land fraction, rivers).

**The axe heads - the user's missing "World Feature" - are answerable but only as candidates.** Exactly
two location prefabs can yield one (`WoodHouse6` -> AxeHead1 "Curious Axe Head", `WoodHouse2` ->
AxeHead2 "Mysterious Axe Head", 20 instances each on `bmbp74`, all 40 agreeing with bobmitch); nothing
in `roomchildren.json` holds one. **But the drop is a `UnityEngine.Random` roll at container spawn, not
a world-seed roll** - 31/56 = 55.4 % for `WoodHouse6`, whose chest is always there, and 31/112 = 27.7 %
for `WoodHouse2`, whose chest is `RandomSpawn` 50 at `m_chanceToSpawn 50`. That second roll IS
seed-determined - it comes out of the zone-seeded stream - so the two uncertainties are of different
kinds and must not be multiplied into one figure. So SeedLab must ship "candidate axe-head houses" with the
same honesty it gives `m_unique`, and bobmitch's "Axe-Head Chests" label overclaims by this reading.
Recorded in the worldgen reference 5.3 and vanilla-behaviour 11.

**Feature inventory taken off bobmitch**, for the backlog, with the note that only the design transfers
(their numbers are 1.0.7): the biggest gap is **ranking** - their seed finder puts a 0..3 weight *and*
an optional hard threshold on each of 21 criteria and orders survivors, where SeedLab's search is a
bare predicate with unordered output; the funnel's survivor list is exactly where a ranking pass
belongs. Second is a **walkable-landmass flood fill from the start temple**, which six of their criteria
(`spawnLandmass`, `hookAfoot`, `axeChestsAfoot`, bosses-afoot, `timeHaldor`) all reduce to. Then travel
time, named presets, and the **base planner** (30 weighted criteria over sites within one world, plus a
vehicle/reach model) that the user called a nice-to-have. Their 10-group category taxonomy is a
**cross-check** on the localization-derived names, not a replacement for them - the naming decision
(localization authoritative; bosses A-Z, traders A-Z, rest by biome) stands.

## 2026-09-23 (later) - the dump ran, the schema caught up, and the funnel strategy shipped

**1. The fourth dump run happened and everything above landed.** `locationprefabs.json` now carries all
23 readable `DungeonGenerator` fields on all 21 generators (`fullFieldsCaptured` true on every one), and
`roomchildren.json` carries **1,019 connections** across 358 rooms with `connectionsCaptured` true on
every one, zero nulls. The audit was worth the launch: almost none of the missing fields were at their
C# default. WoodFarm1 - the Maypole's host - has `m_maxTilt` **25** (default 10), `m_perimeterBuffer`
**0** (default 2), `m_perimeterSections` **10** (default 0, i.e. the wall pass runs), `m_spawnChance`
**0.5** and `m_zoneCenter` **(0, 50, 0)**. Assuming defaults would have reproduced a generator the game
never runs. Across all 21: `maxTilt` in {10, 25}, `perimeterBuffer` in {0, 2, 5}, `doorChance` in
{0, 0.2, 0.3, 0.5, 0.9, 1}. The connection walk also caught its one edge case honestly -
`halfBurried_forestcrypt_entrance_small` has a connection that is not a direct child of its `Room`.

Data snapshot: 54 files, 195 MB, `vseed data --verify` 8/8. The manifest covers 34 of the 54 - four are
derived, four are the manifests themselves, and twelve are goldens for three older worlds the emptied
output folder could not be rescanned for. That gap is written up in the snapshot's own README rather
than left for someone to discover.

**2. The port was re-proved against the FRESHLY dumped natives corpus**, which is new evidence and not a
replay: it grew to 263,025 samples and **263,025/263,025** matched bit for bit, all 11 native checks.

**3. `DumpSchemas` was regenerated, in the right order.** The rule is dump first, regenerate second -
regenerating first would have made the strict loader demand `fullFieldsCaptured` of a shipped file that
predates it and failed every location answer closed. Proved by planting the defect: the manifest hash
check fires first, and with the manifest re-stamped the loader names
`$.prefabs[13].generators[0].fullFieldsCaptured`. The warning now lives in `gen_schema.py`'s own
template (the generator rewrites the whole file and had eaten the first copy of it), and the generator
was moved out of session scratch into `src\SeedLab.Data\`.

**4. The axe-head chests differ in a way worth surfacing.** `WoodHouse6` holds
`TreasureChest_meadows_01` with **AxeHead1** (Curious) and the chest is **ungated**; `WoodHouse2` holds
`TreasureChest_meadows_02` with **AxeHead2** (Mysterious) behind `RandomSpawn[50]` at **50 %**. Both
draw 2-3 items, one of each, from a table of total weight 8 in which the axe head is weight 2.

**5. `--strategy auto|funnel|sample` shipped, CLI and GUI.** Files:
`Execution\FunnelPlan.cs` (which goals stage 1 can filter on, and whether a funnel is worth running),
`Execution\FunnelRun.cs` (`SurvivorSink`, `FunnelGate`), `Execution\SurvivorList.cs`,
`ScanPlan.OverSeeds`, a `planOverride` on `SearchSession.Create`, and the web side in
`SearchModel`/`SearchPlanner`/`EngineSearchEngine` plus the page.

**What the funnel actually adds, and it is not speed.** The tier ladder ALREADY skips placement for any
seed a cheap must-have has settled (`LocationSkips`). What a staged funnel adds is a **measured**
survivor count for the whole range before a single expensive placement is paid for - which the ladder
structurally cannot give, and which matters because extrapolating from a calibration prefix has been
wrong by 35x here. Verified: gate said 17.1 s, the run took 17.3 s.

**Three defects were found by RUNNING it, none by reading it** - worth remembering for how this engine
is tested:
- the per-seed cost was measured on seeds drawn from the range, which the ladder rejects for free, so
  it reported `0.00 s each` for work that took 16.8 s. It must be measured on SURVIVORS;
- the wall estimate divided by the thread count when one worker takes a whole block, so 15 survivors at
  block size 256 is one worker, not eight;
- **the soundness bug**: stage 1 walked a DIFFERENT sample of the space, because `ScanPlan` derives its
  permutation key from the query hash and the stage-one query is a derived query. The funnel returned 14
  matches where the ordinary run returned 16 and the two sets shared **not one seed**. Fixed by handing
  stage 1 the full run's plan; recorded as a pitfall ("a derived query is a different query"). After the
  fix: funnel == sample == `--no-prefilter`, the same 16 seeds, placing 16 worlds instead of 600.

GUI parity verified against a running server: same strategy, same one-sentence reason, same refusals,
and its funnel run passed 22 of 600 while placing 23 - the same 22 a `sample` run of that query passed.

Search suite: **90 checks, 0 failed** (70 before this work, 20 added for the funnel).

## 2026-09-23 - dungeon fields in the dumper, the D4/D5 count rules shipped, the studies preserved

**1. The dumper now captures the whole dungeon surface — but it has NOT been run.** Nothing in
`data\1.0.15-59f53fb5\` holds these fields yet; until the user spends a launch on it, the shipped
answer for them is "not dumped", not "zero". `DungeonGeneratorDef` gained `fullFieldsCaptured`,
`maxTilt`, `tileWidth`, `gridSize`, `spawnChance`, `minAltitude`, `perimeterSections`,
`perimeterBuffer`, `alternativeFunctionality`, `doorChance`, `doorTypes[]`, `zoneCenter`, `zoneSize`
and `originalPosition`. `RoomChildrenDef` gained `connectionsCaptured` and `connections[]`
(`RoomConnectionDef`: the parent-relative **and** room-anchor-relative transforms, `type`, `entrance`,
`allowDoor`, `doorOnlyIfOtherAlsoAllowsDoor`, `directChildOfRoom`, `activeSelf` — both frames because
`CalculateRoomPosRot` reads the parent-relative one, see
`valheim-worldgen\references\zones-locations-vegetation.md` 7.6).

The connection walk deliberately does **not** call `Room.GetConnections()`: that method caches into a
private field on the shared prefab asset, so calling it would fix the answer for the rest of the
session. It walks the children itself and **warns when the active and the inactive-inclusive counts
disagree**, so an empty `connections[]` can never be misread as "this room has no connections".

`DumpFormat.Schema` stays **1**. The change is purely additive, and the per-record `*Captured` flags
are the narrower and more honest statement than a version bump: they say which record was written by
which dumper. `SoughtPrefabNames` now defaults to
`piece_maypole,TreasureChest_meadows_01,TreasureChest_meadows_02`.

The reason all this was missing, and the rule that now prevents it, is in
`valheim-modding\references\pitfalls.md`: **field coverage is a diff against the type's serialized
fields, never against what a feature happened to need.** `tools\SeedLab.Dumper\preflight.ps1` enforces
it with a Cecil scan (its "field coverage" section) over `DungeonGenerator`, `DoorDef`, `Room`,
`RoomConnection`, `RandomSpawn`, `RandomObject` and `ObjectEntry`, with a waiver table and a
stale-waiver check, proved to fire on two planted violations.

**2. Rules D4 and D5 shipped — they were DEFERRED for want of a measured count distribution.**
`data\1.0.15-59f53fb5\count-sample.bin` (+ `.json`) now ships the raw **5 000-seed x 183-type `placed`
matrix**, 1.8 MB, read by `SeedLab.Search.Feasibility.CountSample`. It ships as the **raw matrix rather
than per-type histograms** because a target is often a group, and the distribution of a sum is not
recoverable from its marginals.

- **D4** = a world-wide `count at_least N` with `0 < N < Q` that the sample never saw missed.
- **D5** = `types_within at_least (all types)` from the centre at a radius at or beyond the widest ring
  ceiling.

Both **WARN-DEGENERATE, never REFUSE**, and both fall silent when the sample is absent or its build tag
disagrees with the atlas's. Verified live: D4 fires on `location:Eikthyrnir count at_least 2`
("0 of 5,000 sampled seeds placed fewer than 2") and D5 on
`group:bosses_classic types_within at_least 5 radius 10500` ("already inside 8,000 m ... radius selects
nothing"). Negative controls stay silent: `DN_Bossroom count at_least 2` (a type the sample **has** seen
fall short) and the same D5 goal at radius 4 000.

**3. The count sample's provenance was re-verified against the current build — as a spot check.**
3 sampled seeds x 183 types = **549 cells** compared against `vseed locations --type all --json`, **0
differing**. The other 4 997 seeds were not re-run.

The study behind the sample also settled a game fact: over all 915 000 type-seed cells the game's
`placed` counter equalled the number of instances actually registered
(`zones-locations-vegetation.md` 3.7).

**4. The study documents are preserved.** 14 documents plus a README index moved into
`docs\studies\` — they had existed only in a session scratch folder, one `rm` from
gone.

## 2026-09-23 - ROOT CAUSE: the dumper zeroed UnityEngine.Random; the plugin is retired

**Symptom.** After installing `DoomMachine-SeedLabDumper`, Valheim's new-world dialog offered the seed
`"aaaaaaaaaa"` every time. Moving the plugin out and restarting gave a normal seed (`WlwDZTBFlF`), so
the plugin was the cause. It is retired to `_ModSource\_retired\DoomMachine-SeedLabDumper-20260923`
and must stay out until this is fixed. No data was lost: the asset dump had already run successfully
three times.

**Cause.** `RandomGuard` is a struct with one constructor, `RandomGuard(int? initState = null)`.
`new RandomGuard()` does **not** call it - C# overload resolution prefers the struct's implicit
parameterless constructor, and Roslyn emits `initobj`. The guard's `_saved` field is therefore
`default(UnityEngine.Random.State)` = `{0,0,0,0}`, and `Dispose()` writes those zeros into the global
generator. Zero is a fixed point of Unity's xorshift128, so the state stays zero for the rest of the
session and `World.GenerateSeed()` returns ten `a`s. Proven from the shipped DLL with Mono.Cecil
(`sha256 015bc685...9978`, byte-identical to `build\SeedLab.Dumper.dll`); the general lesson is in
valheim-modding `pitfalls.md`.

**The six argumentless sites** (all `initobj`, all broken): `src\ModeAssets.cs:800`
(`<WalkPrefabs>d__11::MoveNext` IL_014d - fires once per frame-batch of the asset dump),
`src\RoomWalk.cs:147` (`<Run>d__0::MoveNext` IL_037e - once per room, 358 rooms),
`src\ModeWorldGen.cs:130` and `:327`, `src\ModeNatives.cs:174` and `:213`. The nine sites that pass a
seed (`ModeNatives` D5-D10b, `WorldGenReader.cs:83`) emit a real `call .ctor` and are correct. Only the
first two ran in the user's session - which is exactly the asset dump they ran three times.

**What was NOT the cause** (checked, so it is not re-checked): the documented rule "a `RandomGuard` must
never span a `yield`" is upheld everywhere - the guard is a plain local (`V_6`), not a hoisted state
machine field, and the iterator `Dispose` path is not involved. Interleaving with `EnvMan`'s per-frame
save/restore cannot produce zeros, only a rewind; it merely explains why the zeros persist. The two
refused main-menu F4 presses are harmless: `ModeAssets.Run` fails `Safety.SoloHostWithWorld` and
`yield break`s before any guard is constructed. `NoDrawCheck.Around` is sound (its `saved` is a local
assigned before the `try`) - but it is blind to this, because the zeroing happens outside every
`Around` body, and once the state is zero `before == after`.

**FIXED, same day - and the first draft of the fix was wrong.** "Remove the optional parameter so an
argumentless `new` cannot compile" does not work on a **struct**: `new S()` compiles whatever
constructors a struct declares, because the implicit parameterless one is always a member. Only a
reference type makes it a compile error. So `RandomGuard` is now a **`sealed class`** with **private**
constructors and `RandomGuard.Capture(site)` / `RandomGuard.Seeded(seed, site)` factories; all 15 call
sites were rewritten. Proved by compiling a scratch copy with `new RandomGuard()` put back at
`RoomWalk.cs:150`: `error CS1729: 'RandomGuard' does not contain a constructor that takes 0 arguments`.
The allocation (a few hundred small objects per dump) is the price of a bug that cannot be written.

**Defence in depth, independent of that cause.** A new `RandomStateSafe` is the assembly's ONLY writer
of `UnityEngine.Random.state` (the preflight proves there is exactly one `set_state` instruction in the
whole DLL, in `RandomStateSafe::Restore`). It refuses to write an all-zero state: it logs an ERROR
naming the call site and re-seeds from `TickCount ^ UtcNow.Ticks ^ Guid` instead. The zero test is
`state.Equals(default(Random.State))` - because the reflective word read (`RandomStateUtil`) can
throw, and a check that throws must never be able to skip a restore (the same lesson as
`NoDrawCheck`'s 2026-09-23 rework). Verified, not assumed: Cecil over
`valheim_Data\Managed\UnityEngine.CoreModule.dll` shows `UnityEngine.Random/State` is a value type
with exactly four `System.Int32` instance fields (`s0..s3`) and **no** `Equals`, `GetHashCode` or
`op_Equality` of its own, so `ValueType.Equals` - the fieldwise path for an all-primitive struct - is
what runs; and executing it on the real type gives `default.Equals(default) = True`,
`default.Equals(nonzero) = False`, `nonzero.Equals(default) = False`. (That execution was on .NET
Framework; Mono takes the same fieldwise path for an all-primitive struct and neither throws.)
`RandomGuard.Dispose`, `NoDrawCheck.Around` and both `StateRoundTrip` restores all go through it, and
`Dispose` is idempotent so a double release cannot rewind the stream.

**Capture is checked too.** `RandomGuard`'s constructor asks `RandomStateSafe.EnsureLiveAtCapture`: an
already-zero generator at capture time is reported as an ERROR and re-seeded before the capture, and
`ModeAssets.Run` / `ModeNatives.Run` / `ModeWorldGen.Run` each **refuse to start** on a zero state
(`Plugin.Refuse`). If a zero is seen at any point the dump's own manifest carries a note saying the
dump is UNVERIFIED.

**Instrumentation.** `RandomStateSafe.Trace(section)` logs the four state words at Info level at dump
start, at every `// ---- section ----` boundary and at dump end (about 20 lines per dump, not per
prefab). Reading `Random.state` draws nothing, so this is free.

**The rule is now mechanical.** `preflight.ps1` gained an 11-gate `== the RandomGuard contract ==`
section: the guard is a reference type (G1) with only private constructors, no `initobj RandomGuard`
appears anywhere (G2), it is built only in `Capture`/`Seeded` (G3), its constructor really calls
`get_state` (G4) and the capture check (G5), `Dispose` restores through `RandomStateSafe` (G6), that is
the single `set_state` in the assembly (G7) and it tests for zero and re-seeds (G8),
`NoDrawCheck.Around` restores through it inside a `finally` (G9), **no `RandomGuard` spans a yield**
(G10) and every dump entry point refuses a dead generator (G11). G10 is the audit of the rule the
plugin claims: note that Roslyn hoists EVERY iterator local into the state-machine type, so a
`<>s__N` field of the guard's type is *not* by itself a violation (the old struct guard stayed a plain
local `V_6`, which is why this only became visible once it was a class). The real signature is (a)
the guard being disposed from outside `MoveNext` - the iterator's own `Dispose` or a `<>m__FinallyN`,
which Roslyn only emits when the `try` contains a `yield`. A second check (b), a `stfld <>2__current`
inside the `try` whose `finally` releases the guard, is present as a backstop but is
**Unverified:** the planted build did not exercise it, because Roslyn lifts a yield-containing
`using`'s finally out of `MoveNext` and so (a) fires first. It is kept in case a future compiler
leaves the finally in place.

**All of it proved against a planted-violation scratch build** (in scratch, never in the real tree):
`RandomGuard` reverted to a struct, `new RandomGuard()` put back at the prefab-walk site, `RoomWalk`'s
`using` made to span its `yield`, a raw `URandom.state = ...` added inside `ModeNatives.Run`, and
`ModeNatives.Run`'s zero check disabled. The preflight named all five and exited `FAIL` with 6
failures, including `SeedLab.Dumper.RoomWalk/<Run>d__0::<>m__Finally1 disposes the guard field` - the
span-a-yield gate firing on real Roslyn output. The real build: **PASS, 422 checks / 436 with
`-SelfTest`, 0 failures.** `SeedLab.Dumper.dll`
sha256 `ad9386ea78e15bcbd5d135a63897dfdb7b0d4e3342825a7ee3b8fe205ff6b89d`,
`SeedLab.Contracts.dll` sha256 `3c2c1f2460a400e8a3f6af0bdcba6a4c930210e8d74d2ef68bfc684e1ac39e74`.
**Not installed** - the plugin stays in `_ModSource\_retired\DoomMachine-SeedLabDumper-20260923` until
the user decides to run it again.

## 2026-09-23 - the dumper learned to walk each location prefab's children

`tools\SeedLab.Dumper` gained a second output from the prefab walk it already ran:
**`locationchildren.json`**, which is what is INSIDE each of the 186 location prefabs. Built and
preflighted, **not installed** - the user runs it, and the copy in `BepInEx\plugins` is still the
2026-09-22 build.

**Why.** `ZoneSystem.SpawnLocation` spends a seeded RNG stream
(`InitState(worldSeed + zx*4271 + zy*9187)`) on the prefab's own children: one `Random.Range(0f,100f)`
per `RandomSpawn` in array order, then one `Random.Range(0f, totalWeight)` per `RandomObject`, both
unconditional. Capture the arrays in order and the decorative content of a location becomes arithmetic
SeedLab can do offline - which is what makes the Maypole (`piece_maypole`, a Meadows-only furniture
piece that appears in no dumped table because it is a child of some location prefab) and the two
axe-head chests in `WoodHouse1..13` answerable. The mechanism itself is recorded in
`valheim-worldgen\references\zones-locations-vegetation.md` section 4.1, not here.

**What it captures, per prefab.** The `RandomSpawn` and `RandomObject` arrays in the game's own order,
from the game's own `Utils.GetEnabledComponentsInChildren<T>(asset)` call rather than a reimplemented
filter, so the order is identical by construction and entry N *is* draw N - each with its chance, its
biome / elevation / lava / theme gates, the position the game actually reads, its `m_OffObject`, and
the names of the `ZNetView`s it switches on. Every `Container` with its full `m_defaultItems` drop
table (item prefab name + stable hash, stack range, weight, `m_dontScale`) and the index of the
`RandomSpawn` that gates it. And a flat index of every distinct child name in the prefab, inactive
included, so "which locations contain X" never needs another dump. Floats carry their bit patterns, as
everywhere else in the dump - `m_chanceToSpawn` is compared with `<=` against the draw, so its last bit
can decide a maypole.

**Three decisions worth remembering.**

- **Nothing mutates the shared asset.** `Prepare()` is reproduced read-only rather than called, and the
  position the game reads is computed as `Inverse(rootRot) * (childWorld - rootPos)` rather than by
  zeroing the root the way `SpawnLocation` does. Deliberately not `InverseTransformPoint`, which
  divides by the root scale the game keeps. `rootAtIdentity` on each entry says when the computed value
  is bit-exact.
- **Session drift is exposed, not resolved.** The asset the dumper reads has already been through
  `SpawnLocation` for any location that spawned this session, which leaves `m_OffObject` inactive and
  `Location.m_biome` cached. Both counts (filtered and `includeInactive: true`) are written, disagreements
  become a per-prefab `warnings` entry and a manifest note. See `pitfalls.md`.
- **A drop table means CAN contain, never DOES.** Container contents are rolled at spawn time from the
  ambient unseeded stream, so nothing in this dump predicts a particular chest. The README, the DTO
  documentation and the reading notes all say it in those words.

**Cost and safety.** The walk now runs **one prefab per frame** instead of two, so a frame and a
solo/no-peers re-check sit between every two prefabs - the finest granularity available, since a
`RandomGuard` may not span a yield. The file's size is measured and printed when it is written; it has
not been run live yet, so the estimate (single-digit MB, low tens at worst) is reasoning, not
measurement. The per-entry subtree name lists are capped at 64 with the true count beside them, which
is the only bounded part of the file; the whole-prefab index and the activated-ZNetView names are
complete.

**Preflight: PASS, 354 checks / 0 failures; `-SelfTest` PASS, 362 / 0.** It gained the field and method
names of `RandomSpawn`, `RandomObject` (+ `ObjectEntry`), `Container` and `DropTable` (+ `DropData`),
the two `Utils` helpers, and - the part that matters most - **call counts in the game's own IL**:
`RandomSpawn.Randomize` makes exactly one `Random.Range` call, `RandomObject.Randomize` none directly
and exactly one `GetWeightedObject`, which itself makes one, `SpawnLocation` reseeds three times and
builds three arrays, and `PlaceLocations` takes exactly one ambient draw. Member names surviving a game
update would not keep that arithmetic true; a second draw added anywhere would silently move which
house has its maypole. The self test proves the new tripwires fire (two renamed fields and a wrong draw
count, alongside the three existing wrong names).

**Still open**: the reader side. `src\SeedLab.Data` (owned by another workstream) needs
`LocationChildrenFile` added to `DumpJsonContext`, a `DumpSchemas` entry, and a strict loader in
`GameData` - a missing field must be an error, never a silent default. Until then the file is written
and unread.

## 2026-09-23 - GUI parity: the web UI now runs the same SearchSession as the CLI

The independent verifier's finding was that "the new output-safety, grid-policy and runtime layers are
correct but NOT WIRED INTO ANY SHIPPED COMMAND". For `SeedLab.Web` that is fixed. What changed, and
what each change is worth:

**The page runs `SearchSession`, not a bare `SearchRun`.** `EngineSearchEngine.Start` now goes through
`SearchPlanner` -> `SearchSession.Create` -> `SearchSession.Start` -> `SearchSession.Run`, the same
four steps `vseed search` takes. Consequences, all measured through the HTTP API:

- **`--keep` is a real cap through the GUI.** A 40,000-seed run with 2,802 matches and `keep 200`
  wrote **200 records / 93,155 B**, and the panel says *"top 200 of 2,802 matches; 2,602 were not
  kept"*. Before this the page kept an in-memory display list and wrote nothing at all.
- **The refusal rule fires.** `SearchPreflight`'s all-must-haves refusal (decision 9) was implemented
  and never called from either front end. `POST /api/search` with a single must-have goal now returns
  400 with `kind: "refused"` and the four named fixes.
- **Confirmations are enforced ON THE SERVER.** A whole-space run POSTed without `"confirmed": true`
  is refused with `kind: "confirm"` and the whole preflight report. The gate is not in the page, so
  curl cannot walk past it. It matches `SearchCommand.cs:318`, which prompts on *every*
  `Preflight.Confirmations` entry - so the two front ends confirm on the same list.
- **Checkpoints land in the runtime cache root**, passed explicitly
  (`<cache>\checkpoints\<hash16>.ckpt`), never in the working directory, and are retired on a
  completed run.
- **Screen-then-verify, region, rotation, compression, reduce, on-limit and max-bytes are reachable**
  from the page and written into the query file.

**Parity is proved byte for byte.** A query built in the panel, exported with the Export button, run
again by `vseed search <that file> --out ...`: both result files **SHA-256
`79e01afa50074a7ba43392c75059d61d014927538d65132f5e19cbae5692e8ec`**, 71,588 B, 154 records, seeds
identical in order. Script: scratchpad `gui\parity.py`.

**No shipped path in the web project sizes itself from `Environment.ProcessorCount` any more.** The
search engine's `ProcessorCount - 2` is deleted; `TileRenderer`'s `min(4, ProcessorCount)` survives
only as the constructor default for a host that builds a renderer without a count, which no shipped
caller does; `WebServer`'s render semaphore and the renderer's degree both come from the runtime
`WorkerPlan` now, so the mode, the memory guard and the auto-throttle govern the
whole process. Observed live: with Valheim running the panel dropped to **4 of 16** workers and showed
the banner; with it closed, 8 of 16 in balanced mode.

**New endpoints.** `POST /api/search/preflight` returns the whole pre-run report (refusals,
confirmations, warnings, the plan block, the grid decision, the resolved output policy, the worker
plan, the estimate, per-goal cost classes and the costed sampling ladder) without touching a seed -
it is the live verdict beside the Find button *and* the gate the POST uses. `GET /api/runtime`
returns the mode, the throttle's change log, the worker lines and the cache root.

**Bounds in the control.** `GoalBounds` ships a 232-prefab / 19-group / 9-biome table built from
`LocationFeasibility.LowerBound/UpperBound` and `BiomeGeometry.Band` - the same functions the engine
refuses with - so the value input carries `min`/`max` and an impossible number cannot be typed. The
hint is one line ("Eikthyrnir is always within 1 km of the centre (m_maxDistance 1000)") and the
evidence (m_quantity 3, the biome mask, the placement-loop proof) is behind an affordance that starts
closed.

**The server now writes, and every claim that said otherwise is corrected in place** - the
`WebServer` class doc, its startup log, `EngineSearchEngine`'s "three nulls" comment,
`QueryTranslator`'s "no output block on purpose", `TileCache`'s "nothing here touches the disk", the
marker note in `index.html` and `docs\web.md`. Three places, all bounded: the results file (a bare
NAME resolved inside one results directory, default `<cwd>\seedlab-results`), the checkpoint, and the
tile cache's new disk tier under `<cache>\tiles`.

**Security re-probed against a real socket after the changes: 45 checks, 0 failed**
(scratchpad `gui\probes.py`) - loopback only (the LAN address is refused by the socket), four
rebinding Host names -> 421, no CORS header on a cross-origin GET or an OPTIONS preflight, the CSP on
every reply, nothing outside the content root, and 11 attempts to escape the results directory all
refused.

### Two defects this pass found

1. **`CON.jsonl` started a run.** Win32 device names are reserved with *any* extension, and
   `Path.GetInvalidFileNameChars` says nothing about them, so a browser could name the console device
   as a results file. Found by the probe, not by reading. `QueryTranslator.Clean` now refuses CON,
   PRN, AUX, NUL, COM1-9, LPT1-9 and any name ending in a space or a dot.
2. **`applyPreset` was defined twice in `app.js`.** Function declarations hoist and the *last* one
   wins, so the preset dropdown was calling a dead copy written for the placeholder engine's
   vocabulary (`kind: 'spawn_land'`, `biome_nearest`) - every preset load built nothing. Deleted.
   A third, smaller one: `MetricCatalog`'s `Unit.SquareMetres` serialises as `"squaremetres"`, and
   `fmtUnit` tested for `"square_metres"`, so every area figure on the page was printed as a raw m2
   integer.

### Still open

- `vseed serve --selftest`'s "search panel vs the engine" check now FAILS, correctly: its fixture
  (`ServeCommand.cs:1175-1186`) POSTs a G192 query with a `nearest_distance` must-have, which the grid
  policy raises to G12 - a confirmation both front ends now require. The fixture needs
  `"confirmed": true`. `ServeCommand.cs` is the CLI's file, so it was not edited here. The other 10
  checks pass.
- `vseed serve` does not pass its own `RuntimeContext` to `WebServerOptions.Runtime`, so the server
  starts a second one: two probes, two throttle timers and two `PriorityScope` applications in one
  process. Harmless today, and one line to fix.

## 2026-09-23 - independent verification of the optimisation / output-safety / runtime pass

A fourth agent re-ran every gate from a deleted-bin/obj rebuild and re-tested the three agents' claims
against the shipped `vseed.exe` rather than against their reports. Full evidence in that session's
scratchpad `verify\`.

**Exactness: all of it holds.** From a clean Release rebuild of all 19 projects (0 errors; Saves 1,
Tests 1, Acceptance 2 pre-existing warnings, not the `0 warnings` the search agent's report claimed):
acceptance 32/32 with 0 biome mismatches over 2,542,492 + 2,562,380 pixels and 4,194,304/4,194,304
heights per world; LocationLab 12,228/12,228 fresh, 12,314/12,314 and 12,287/12,287 played, 29/29 log
counters; natives 11/11 with 262,780/262,780 Perlin, 276/276 Random traces, 93/93 libm, 49/49
WorldAngle, 429/429 hashes; GoldenCheck PASS over three seeds (3,058,308 + 2,708,376 + 2,702,316 =
8,469,000 floats, 0 differing); Search.Tests 70/70; Runtime.Tests 122/122.

**O3/O5 verified independently of the authors' own self-test.** A separate harness against
`SeedLab.WorldGen.dll` compared `UnityPerlin.PerlinNoiseReference` vs `PerlinFast.PerlinNoise`
(byte table) vs `PerlinFast.Noise8` (AVX2), each case rotated through all eight lanes:
117,824 hostile comparisons (exact lattice points, the 256 period and its ULP neighbours, +-0f, NaN,
both infinities, 2^20 +- 1 ULP, 1e5 and float.MaxValue) and 19,200,000 random comparisons - **0
differing**. `GetBaseHeightPathPublic` scalar vs SIMD over 10 seeds, 1,771,000 comparisons spanning
past the world edge and across the 1e6 guard - **0 differing**.

**The runtime selection cannot change a result - proven through the real runtime path.** Setting
`DOTNET_EnableAVX2=0` (not a source edit, which is how the worldgen agent tested it) makes
`Avx2.IsSupported=False` and `PerlinFast.Use8Wide=False`. Five whole-world fingerprints
(biome + base height over 1024^2 at 24 m for three seeds, and pregenerated `GetHeight` over 512^2 for
two) are **byte-identical** with AVX2 on and off, and the acceptance, natives and GoldenCheck gates all
pass unchanged on the no-AVX2 path. `natives` also passes in a Debug build.

**The O2 memo race is documented and no consumer triggers it.** Every `new WorldGeneratorPort(` and
`.Fork()` site was checked: `WebServer.cs:416` forks per request, `BiomeGrid.cs:184` forks per
`Parallel.For` band, `WorldField.cs:175` forks per worker, `SeedEvaluator.cs:183` constructs per
seed. Latent, pre-existing and unreached: `EnsurePregenerated` (line 318) is unsynchronised, so
concurrent `Fork()` on a *deferred* parent would race; no live caller passes one.

**`--keep` is a real cap - measured.** 200,000 seeds, same Feistel key, one run at `keep 1000` and
one at `keep "all"`: 154,145 matches both times; 1,000 records / 459,931 B against 154,145 records /
70,854,200 B (154.1x). The kept 1,000 are **positionally identical** to the top 1,000 of the unbounded
file by (score desc, seed asc), 0 mismatches, same cut score 0.39168, and the report says
"Best 20 of 154,145".

**Crash safety - measured, not taken on report.** Against an uninterrupted baseline
(SHA 51A90D2B92CFC27A..., 5,000 records, 119,842 matches): a mid-block kill, a kill caught *inside*
`ResultWriter.Rewrite` (results file observed shrinking 2,296,974 -> 2,296,932 B) and a kill within
milliseconds of a checkpoint save all resumed to a **byte-identical** file. 8 further randomised
double-kill trials (15 hard kills) plus 5 in `keep:all` streaming mode (9 kills, torn tails up to
46,441,624 B of a 55,048,005 B file) were **all byte-identical**. The one `.ckpt.top.tmp` orphan a
mid-flush kill leaves is reaped by the next resume. `ResultWriter.Rewrite` truncates **in place**
(`SetLength(0)`), so a bounded file's recovery rests entirely on the `.ckpt.top` snapshot - which
`SaveCheckpoint` (SearchRun.cs:594-601) re-saves immediately before writing the checkpoint.
**One window is reasoned about but not closed**: a kill between `SaveSnapshot` (L597) and
`c.Save` (L601) would leave a newer snapshot beside an older checkpoint, so a resume would re-offer
the blocks in between, and `BoundedResultSet.Offer` does not dedupe by seed - duplicate records and
an inflated total. The window is microseconds wide and 27 hard kills never hit it. Unverified.

**Estimator: the 35x error is gone.** Shipped `--dry-run` estimate against a real 16-thread run -
T5 locations 9.4 vs 9.4 seeds/s (1.00x); T3 height 70.6 vs 56.7 (1.25x over); T2 biome region-bounded
69,238 vs 43,233 (1.60x over); whole-world biome G384 full-sample 10,816 vs 21,470 (1.99x **under**);
same query with a strict must-have that early-exits, 10,816 vs 35,231 (3.26x under). Worst case 3.26x,
and the `--dry-run` text now warns that it is an upper bound on throughput. The live-slice
`RunEstimator` the search agent's report quotes is library-only: `SeedLab.Cli` never calls it.

**Performance reproduces and is conservative.** Quiet machine (1-8 % load, only idle desktop
apps), whole-world biome G384, 200,000 seeds, median of 3: **21,470 seeds/s at 16 threads** and
**14,193 at 8**; `--no-prefilter` audit mode 16,190 and 12,272. The pass's claimed post-optimisation
figures were 15,254 @16 and 13,196 @8, and the pre-optimisation baseline in decisions.md is 10,717
@16. Read apples-to-apples, the comparable full-work numbers are the `--no-prefilter` ones, because
the profile's figures are full-grid sampling with no early exit: **the 16-thread figure reproduces
(16,190 vs 15,254, +6 %) and the 8-thread figure comes in 7 % UNDER the claim (12,272 vs 13,196)**.
The *ratio* cannot be reproduced independently at all: there is no git here and no pre-change tree
survives, so 1.36x/2.05x is unverifiable either way.

**Record values are exact at the grid they claim.** A `gentle-start` record at G12 cross-checked
against `vseed seed -2139313257 --grid 12 --json`: spawn island 6,776,784 m2, land 112,403,952 m2,
highest peak 429.90628 m, ocean share 0.2943275001621403, nearest Black Forest 508.3384699194032 m -
all exact.

### What is NOT reachable from the shipped commands

The single biggest finding. `SeedLab.Cli` and `SeedLab.Web` reference neither `SeedLab.Runtime`
nor `SearchSession` / `SearchPreflight` / `ResultSinks` / `OutputPolicy` / `GridPolicy`.
`vseed search` still calls the legacy entry point, so the bounded sink and the checkpoint/resume
fixes are live, and everything else is library-only:

- `output.max_bytes` and `output.on_limit` are parsed and **silently ignored**. Measured: a 5 MB
  ceiling with `on_limit: stop` wrote 55,048,005 B, 10.5x over, flat and uncompressed.
- `output.rotate` / `compress` / `reduce` likewise; the auto-1 GB rotation and auto-90 %-of-free
  ceiling that `SearchSession` applies to `keep:all` never fire.
- `search.screen` is inert: a `screen: auto` run wrote 0 records carrying `screened_at_grid_m` or
  `verified_at_grid_m`. `GridPolicy.AutoPick` is called only by `SearchSession` and by
  `SeedLab.Search.Safety.Tests`.
- No auto-upgrade: the `archipelago` preset still runs a must-have `island_count` at **G24** with no
  raise to G12, no measured reason and no confirmation - the metric the resolution study says has no
  safe margin at any coarse grid.
- Without screening, `gentle-start` measures **0.8 seeds/s on 16 threads** (76/250 = 30.4 % pass,
  matching the reported 29.10 %), not the 4.2 seeds/s the search agent's report quotes.
- Audit defect 11.5 is **unfixed in the CLI**: with no `--out`, `vseed search` still writes
  `vseed-search.ckpt` into the CWD and a hard kill leaves it there. With `--out` the checkpoint is
  `<out>.ckpt` plus `<out>.ckpt.top`, never the cache root `CheckpointStore.Root` resolves.
- Decisions section 9's **refusal rule** is implemented (`SearchPreflight.cs:121-133`) and proven by
  `proof refuse`, but `vseed search` never calls it: a feasible all-must-have query with no
  nice-to-have ran to completion, 339 matches, **every record scoring exactly 1**, and printed
  "Best 20 of 339" in arbitrary scan order. The message it would have printed names
  `--accept-scan-order`, a flag the CLI does not have. (`vseed explain` does say "no nice-to-have
  goals, so every match scores 1"; `vseed search` says nothing.) Separately, the feasibility analysis
  IS live and good: asking for 6 km2 of Meadows inside a 1 km disc is refused with
  "Refusing to scan 4.29 billion worlds for something the generator cannot make".
- The library side is sound, which is what makes this a wiring gap rather than a design failure: the
  new `tests\SeedLab.Search.Safety.Tests` binary (`proof.exe`) passes `refuse`, `policy`,
  `region`, `estimate`, `bounded` and `rotate`, all exit 0, and `proof policy` correctly
  reports that `archipelago`-style `largest_island_area` at G24 loses 32 of 643 true matches.- All of `SeedLab.Runtime` is unreferenced: no `--mode`, no auto-throttle, no memory guard, no
  `vseed clean`, no disk-use report. Decisions sections 2, 5, 7, 10, 11.5-11.7 and 12 (GUI parity)
  are therefore designed, tested and **not shipped**.

### One preset defect

`gentle-start`'s only nice-to-have, `blackforest-close` (`biome:BlackForest.nearest_distance` from
the centre), is **constant across the seed space**. Measured by brute force on a 4 m lattice: the
nearest Black Forest to the origin is 500.78 m for all 14 seeds tried, matched or not, always on the
same ring - a property of the game, not a SeedLab error. At G12 the metric reports 508.3384699194032 m
for 75 of 76 matches, so every kept record scores 0.364576912600746 and "top 200" degenerates to the
first 200 in scan order. Decisions section 9's refusal rule does not catch it, because it tests for the
*presence* of a nice-to-have, not for whether that goal carries any information.

### Fixed in this verification pass

`SeedLab.Cli\Commands\SearchCommand.cs` - the `--keep` help said "size of the in-memory best-of
table (not a cap on the file)" and the summary line said the command "streams every matching seed to
disk". Both were the pre-fix behaviour and are now false. Corrected to state the real cap, the default,
the true-count reporting and `search.keep: "all"`. Rebuilt; the bounded 200,000-seed run is
byte-identical to the pre-change one (SHA 02DC69C16DC3B868...), Search.Tests still 70/70.
## 2026-09-23 - the session that finished it

Built, in order: the `WorldGenerator` port and the Unity natives, the read-only save and map-cache
readers, the location placement engine, the seed arithmetic and its inverse, the `vseed` CLI and map
renderer, the search engine, the local web UI, and the BepInEx dumper that captured the game data all
of it rests on. Every one of those was reviewed adversarially, and the reviews are the reason most of
the corrections below exist.

**The two native Unity functions were solved and verified against the running game**, which closed the
last open section of valheim-worldgen `world-generator.md`:

- `Mathf.PerlinNoise` - improved Perlin, `abs()` fold on both inputs, quintic fade, the classic
  256-entry permutation table doubled, normalised `(raw + 0.69f) / 1.483f` with a **real divide**;
  262,780/262,780 recorded samples bit-exact. The reciprocal-multiply variant is 1 ULP off on 37.5 % of
  samples - the earlier "2 in 217,959" figure was what survived the minimap's half-precision, not the
  real rate.
- `UnityEngine.Random` - xorshift128, `InitState` seeding `s*1812433253 + 1`, shifts 11/8/19,
  `Range(float,float) = (1f-f)*max + f*min`, the int overload's `Range(a,a)` consuming **no** draw while
  the float overload does, `insideUnitCircle` two draws with `x = cos`, `y = sin`,
  `r = sqrt(second draw)`; 276/276 traces, 1,980 draws, 268/268 `InitState` seeds.

**Corrections that cost real time and are worth remembering:**

- **Mono keeps float expressions at double precision on the evaluation stack.** `GetDeepNorthHeight`'s
  `base + 0.1f` and `UnityEngine.Vector2.magnitude / Distance / SqrMagnitude` accumulate in double
  before narrowing. Writing them as C# float chains cost ~7,800 wrong float32 heights per world and was
  **invisible** to the half-precision minimap cache. Fixing both took the port from 12,244/12,314 and
  12,227/12,287 to 12,314/12,314 and 12,287/12,287 bit-exact `.db2` heights, and the minimap sweep from
  5 and 9 differing binary16 codes to 0 and 0. Lake, river, stream and river-point counts did not move:
  a precision fix, not a geometry change.
- **`Mathf.FloatToHalf` breaks ties away from zero**, not to even like .NET's `(Half)` cast. 158 of the
  first 163 height "mismatches" were exact midpoints and nothing else, which nearly sent the
  investigation into the height formulas.
- **A code default is not the shipped value.** The dump corrected `Minimap.m_textureSize` 256 -> 2048,
  `m_pixelSize` 64 -> 12, `m_removeRadius` 128 -> 300, `ZoneSystem.m_locationVersion` 1 -> 32,
  `m_zoneTTL` 4 -> 10, `m_zoneTTS` 4 -> 5, and the alt-biome count 28 -> **32** (a contiguous-record
  bundle scan had missed `Mushroom`, `Lantern`, `Bones` and `Menhir`).
- **`Color.black` is `(0,0,0,1)`.** The vegetation-mask filter excludes *Deep North*, not
  "everything outside Mistlands/AshLands" - the opposite of what both the spec and the KB said.
- **`Random.Range(float,float)` with `min > max` narrows the interval**, so a location escapes its zone
  only when `maxRadius > 64`, not `> 32`.
- **The game's log number is a filtered count.** 232 `ZoneLocation` entries, 186 enabled, 183 enabled
  *and* with a quantity (the number the log prints), 200 with a quantity whether enabled or not.

**Measured, not assumed:** pre-generation is ~99.5 % of the cost of building a world, and nothing but
height reads it - deferring it made biome-only seed queries about 200x cheaper (299.3 ms -> 1.5 ms per
seed), proven bit-exact on rivers, streams, lakes and 64,512 `GetHeight` samples.

**Straight biome edges explained.** A separate investigation traced the dead-straight, kilometre-long
biome boundaries in vanilla maps to a degeneracy in Unity's Perlin gradient set riding on the masks'
1 km lattice, and predicted them from the seed's offsets alone on 25 of 25 random seeds. Recorded in
valheim-worldgen `world-generator.md` section 5.1, with the attribution trap in valheim-modding
`pitfalls.md` section 9.

**Tooling traps this session paid for** (all in valheim-modding `pitfalls.md`): leftover `vseed serve` /
`vseed search` processes locking a Release build output (`MSB3027`/`MSB3021` - build to a private
`-p:OutputPath` instead of killing another agent's process); `double.TryParse` / `float.TryParse`
accepting `NaN` and `Infinity` on .NET Core, so non-finite CLI arguments passed every range check and
printed nonsense with exit 0; and a *quoted* bash heredoc still collapsing doubled backslashes and
writing a literal `0x01` byte into a file.

**Audit findings the user then turned into decisions** (`references/decisions.md`): `--keep` does not
cap the results file; a hard kill loses a whole block and can leave a torn record; checkpoints are
written into the CWD unconditionally and never deleted; `vseed map` accumulates PNGs in the CWD. None
of that is fixed yet.

## 2026-09-22 - the port reaches bit-exactness

The `WorldGenerator` port was measured against the user's world `asdasdasd` over all 4,194,304 minimap
pixel centres, then against the hold-out `testworldclaude` that nothing had been tuned on: 0 biome
mismatches on both, and heights bit-identical as binary16 on all but 5 and 9 pixels, one half-ulp each
(closed on 2026-09-23 by the Mono-R8 correction; valheim-worldgen `world-generator.md`). Three knowledge-base facts were corrected in
the process, including the claim that Unity's managed `Vector2` members compute in single precision.

The BepInEx dumper (`tools\SeedLab.Dumper`) was written, reviewed twice and run in the live game on
2026-09-22 - and again on 2026-09-23 (01:47-01:50, the natives and world-generator dumps).
**Correction, 2026-09-23:** it was **not** removed afterwards, as the project README says; it is still
installed in `BepInEx\plugins\DoomMachine-SeedLabDumper` and still armed by a `dumper.enable` file
(SKILL.md, "The dumper, and its current state"). It is main-menu-only, refuses to run with peers connected, restores every
game static it borrows (in a `finally`, reachable from `MoveNext`'s fault handler), and its preflight
scans 18 destructive + 20 write file APIs recursively through nested types - after a review found that
a top-level-only scan was blind to 33 of the build's 59 types and that the old 6+6 API list was
narrower than the claim it backed.

## Still open

- The `insideUnitCircle` trig flavour: confirmed, not discriminated (`references/proofs-and-gates.md`).
- ~~Everything in `references/decisions.md` is specification, not implementation.~~ **Corrected
  2026-09-23:** almost all of it is now built *and wired into shipped commands* - see the 2026-09-23
  entries below and `references/decisions.md`'s own status banner. What remains unbuilt is
  `--strategy funnel|sample` as named strategies, the write-path tripwire test (decision 11 item 9),
  the D4/D5 count rules (deferred with reason) and GPU (rejected).
- Cross-architecture exactness is untested (x64 only so far); the fail-closed machine self-test is
  wired and is what stands in for it.

## 2026-09-23 - the runtime and resource layer

`src\SeedLab.Runtime` was added: a dependency-free (no NuGet, no other SeedLab project) BCL-only layer
holding the hardware probe, the three resource modes with the worker/memory guard, the auto-throttle
that drops to background while Valheim is running, the cache root with per-process scratch and
temp+rename writes, the cost estimator with live re-calibration, and the machine self-test that fails
closed. Its own README is the API reference; `tests\SeedLab.Runtime.Tests` is 122 checks, exit 0/1.
(**Corrected 2026-09-23:** this entry originally ended "nothing in `SeedLab.Cli` or `SeedLab.Web`
calls it yet". Both now do - one `RuntimeContext` per command via `src\SeedLab.Cli\Infra\CliRuntime.cs`
and per server start in the web UI, with every `Environment.ProcessorCount` sizing site, the
working-directory checkpoint and map defaults and the pre-run estimate block replaced.)
The self-test ships 271 recorded libm and float-evaluation vectors embedded in the assembly, including
cases a contracted FMA would change, and treats a non-x64 machine with no generator-level suite
registered as `Unproven` rather than verified.

## 2026-09-23 - the generator core gets 2-3x faster, with every gate still bit-exact

`src\SeedLab.WorldGen` took the five optimisations the performance profile ranked, in the order it
ranked them, re-running all five gates after each. Nothing else in the tree changed.

- **O1** - `GetBiome` evaluated `WorldAngle(wx, wy)` three times at identical arguments (once itself,
  once inside `IsAshlands`, once inside `IsDeepnorth`). It now computes it once and passes it to the
  new `IsAshlands(x, y, worldAngle)` / `IsDeepnorth(x, y, worldAngle)` overloads; the two-argument
  forms call those, so there is one copy of each body. Safe by construction: `WorldAngle` is static and
  pure.
- **O2** - a one-entry memo on `GetBaseHeight`, keyed on the raw **bit patterns** of both coordinates,
  so a hit can only occur for arguments identical in every bit (no `-0` / NaN case analysis needed).
  `GetBiome` and the per-biome height function that follows it always query the same point.
- **O3** - `UnityPerlin.PerlinNoise` now forwards to `PerlinFast.NoiseScalar`: the same arithmetic with
  the permutation table as `byte[512]` behind a pointer (no bounds checks, 512 B instead of 2 KiB of
  L1). `UnityPerlin.Noise` is untouched and is now the *reference* the self-test compares against;
  `PerlinNoiseReference` exposes it. The 262,780 recorded game samples in `tests\SeedLab.Tests --
  natives` go through the new path, so that gate re-proves O3 against the game on every run.
- **O5** - `GetBaseHeightSimd` issues the eight `Mathf.PerlinNoise` calls in `GetBaseHeight` as one
  AVX2 8-wide batch (`PerlinFast.Noise8`). All eight arguments depend only on (wx, wy) and the
  immutable offsets and are evaluated unconditionally on that path already, so batching changes when,
  not what. Packed SSE/AVX single-precision ops equal their scalar forms per lane, nothing is
  cross-lane, and **no FMA is used anywhere** (C# does not contract, and `Fma.*` is never referenced).
  The one non-IEEE operation is float-to-int: .NET saturates, `CVTTPS2DQ` gives 0x80000000, so the
  8-wide entry point refuses arguments that are NaN or >= 2^20 and falls back per call.
  `GetBaseHeightSimd` additionally requires `|wx|, |wy| < 1e6`. On a machine without AVX2
  (`PerlinFast.Use8Wide == false`) every caller runs the scalar path.
- **O4** - `MergePoints` gained a bucket grid of cell side `range` in front of `FindClosest`, cutting
  it from a scan of the whole live list (24,649) to a 3x3 neighbourhood (~350). This one is **not**
  safe by construction: it has to reproduce "minimum by distance, then lowest live index" over the same
  candidate set, and the game's `RemoveAt(0)` plus swap-remove ordering. The literal O(n^2) version is
  kept as `MergePointsLegacy` behind `WorldGenTuning.LegacyMergePoints` so the two can be compared.
- Three allocation/codegen cleanups: `RenderRivers` merges a cell with one `RiverPoint[]` allocation
  and two block copies instead of `new List(existing)` + `AddRange` + `ToArray`; `FindLakes` presizes
  its candidate list; and `GetBaseHeightSimd` carries `[SkipLocalsInit]`, because its three
  `stackalloc float[8]` buffers are 96 bytes the JIT would otherwise zero on every call and all 24
  floats are written before anything reads them. The first two were a wash at 1 thread and ~3-4 % less
  CPU per seed at 16 in a paired A/B (at the edge of this machine's noise); `[SkipLocalsInit]` is worth
  a repeatable ~7 % of `GetBaseHeight` (80.9 / 91.1 / 79.5 ns without against 71.5 / 76.6 / 75.5 ns
  with, interleaved).

**New: `PerlinSelfTest` runs from a `[ModuleInitializer]`**, so every tool that loads the assembly
proves the three Perlin spellings agree before anything can use them, and **throws** if they do not
(decisions.md section 7 asks for a startup self-test that fails closed). 7,320 scalar + 58,560 8-wide
samples over a fixed hostile vector set - exact lattice points, their ULP neighbours, both zeros,
negatives, the real `GetBaseHeight` and `GetBiome` argument shapes, 2^20 and 2^24 magnitudes, NaN and
both infinities - in ~10-13 ms, once per process. `PerlinSelfTest.Report()` returns the line for a
`selftest` command to print.

**The fail-closed path was fired, not assumed.** Sabotaging one entry of the byte table and,
separately, dropping the `h == 14` case from `Grad8`, each kills the process at load with the message
legible as the FIRST line of output - `SeedLab: the O3 byte-table scalar Perlin path ...` and
`SeedLab: the O5 AVX2 8-wide (lane 6) Perlin path ...`, both quoting the arguments and both bit
patterns. A throw from a `[ModuleInitializer]` does not bury the text. The scalar-fallback path was
also gate-verified: with `PerlinFast.Use8Wide` forced false, the acceptance suite (32/32), the golden
check and the natives suite all still pass, so a machine with no AVX2 generates the same world.

**A thread-safety contract changed, and the class doc now says so.** Before O2, `GetBaseHeight` and
`GetBiome` at their default arguments wrote no instance state, so sharing one handle between threads
for biome-only work was accidentally safe. The memo makes them writers: two threads can interleave the
four memo writes and pair one thread's key with the other's value, returning a wrong height rather than
throwing. Every parallel consumer in the repo was checked and all of them already fork or construct per
worker - `Locations\BiomeGrid.Build`, `Render\WorldField`, `Web\Tiles\TileRenderer`,
`Cli\SelfTestCommand`, `Search\SearchRun.Worker` (own evaluator, own generator per seed),
`Acceptance\WorldSweep`, `Acceptance\LocationHeightCheck`, `Tests\Program` - and `Render\MapRenderer`
parallelises over already-sampled arrays, not a generator. The `Threading` paragraph on
`WorldGeneratorPort` now lists the memo with the river cache and calls the change out by name.

### O6 was tried and REJECTED - the radius bound is not enough

Skipping the second (Deep North) `PlaceStreams` pass for a radius-bounded query is **not sound**, and
the reason is not the radius. The radius part holds: a Deep North stream's start point is more than
7,900 m from the centre (the biome boundary's true minimum is 7,907.7 m), its rendered points reach at
most ~201 m from it (length <= 198.8 m, curve offset <= length/15), and a river point only influences a
height within its own 20 m width - so nothing inside ~7,679 m can see one. **Measured: 1,777,728
heights inside 7,600 m over 12 seeds, 0 differ; 1,439 of 2,542,272 outside it do differ, so the test is
sensitive.**

What breaks it is the **single-entry river cache the port deliberately does not invalidate after
`RenderRivers`** (the game bug documented on `Fork`). The cell it is left pointing at when
pregeneration ends is wherever the *last* stream probe landed - and that probe is in the pass being
skipped. Skip it and the cache ends up on a different cell holding a different array, so the first
river query a fresh handle makes in that cell answers differently. **Measured with
`Fork(inheritRiverCache: true)`: 2,536,350 first-queries over 30 seeds, 1,589 divergences, 11 of them
inside 7,600 m - as far in as 2,715 m, with weights like 0.889 against 0, not last-bit noise, and every
one of the 30 seeds showing at least one.** No radius precondition can cover that, because the cell is
wherever a random draw put it. The experiment was removed from the tree; only the note in
`Pregenerate` remains.

### O7 needs nothing from SeedLab.WorldGen

The radial-band early reject for single-biome goals is entirely a `SeedLab.Search` change:
`SeedLab.Search.Feasibility.BiomeGeometry.Band(biome, genVersion)` already returns the wobble-aware
`(Min, Max)` radius interval, so the sampling loop can skip a cell on `dist` before it ever calls
`GetBiome`. No new API is required on the port.

### Measured (Ryzen 9800X3D, 16 logical cores, other agents' builds and tests running - see the numbers' spread)

| measurement | baseline | O1+O2+O3+O4+O5 | ratio |
| --- | --- | --- | --- |
| `GetBaseHeight`, 1 thread, 2 M non-repeating samples | 372.25 ns | 61.72 ns | **6.03x** |
| biome grid @384 m, 1 / 8 / 16 threads (seeds/s) | 735.9 / 5,155 / 6,306 | 2,482 / 16,461 / 21,930 | **3.37x / 3.19x / 3.48x** |
| biome grid @12 m (the game's own), 1 / 8 / 16 (seeds/s) | 1.53 / 9.06 / 12.19 | 2.68 / 17.69 / 25.83 | **1.75x / 1.95x / 2.12x** |
| pre-generation, 1 / 8 / 16 threads (seeds/s) | 3.22 / 15.27 / 17.78 | 6.77 / 21.57 / 21.51 | **2.10x / 1.41x / 1.21x** |

Exactness evidence beyond the gates: **37,748,736** `GetBaseHeight` values compared scalar vs AVX2 over
64 seeds and a 768x768 grid spanning past the world edge, 0 differ; `MergePointsLegacy` vs the bucket
grid over 300 seeds, whole worlds - 71,554 lake coordinates, 2,832,325 river and stream fields and
842,270,804 river-point `p.x` / `p.y` / `w` / `w2` values, **845,174,683 floats in all** - 0 differ.

**Open handoff:** that O4 comparison ran from a session scratchpad harness, so it is not
reproducible from the repo. `WorldGenTuning.LegacyMergePoints` ships but nothing under `tests`
exercises it yet. The test is short: set the flag true, `new WorldGeneratorPort(seed, 2, false,
false)`, set it false, build a second handle from the same seed, then bit-compare `GetLakes()`,
`GetRivers()`, `GetStreams()` and `GetRiverPoints()` element by element and in order. Seeds used:
`1000003 * s + 17` for s in 0..299; ~2.8 M float values per seed.

## 2026-09-23 - the output layer: bounded, crash-safe and honest (`src\SeedLab.Search`)

The disk audit's finding was that every real risk lives in the output layer, not in the generator.
All of it is now fixed inside `SeedLab.Search`, with the measurements that prove it.

**`keep` is a real cap on the file.** `BoundedResultSet` (a best-N heap ordered score desc then seed
asc) plus `BoundedResultSink` (the file IS the kept set, rewritten from the heap at every flush).
Measured: 20,000 matching seeds, `keep: 50` wrote **15,976 B** where `keep: "all"` wrote
**6,374,507 B** - 399x - and the kept 50 are exactly the top 50 of the unbounded file, same order,
seed for seed. The run reports `top 50 of 20,000`, never `50 matches`. Default `keep` is now 1000,
and it is applied at `SearchRun`'s legacy entry point too, so `vseed search --out` is bounded without
any CLI change. `keep: "all"` must be written out loud in the query.

**The memory blow-up was the collector, not the planner.** The audit guessed "something is allocated
per block up front". Measured: nothing is. Blocks are claimed and emitted lazily; the 67,108,864-block
whole-space plan costs nothing to enumerate. What grew was the queue of finished-but-unwritten blocks:
one collector thread did `SeedText.Invert` and all the formatting for 16 workers, so when most seeds
pass it falls behind and nothing stopped the queue. Two fixes - `SeedText.Invert` moved to the worker
(it is a pure function of the seed) and a hard bound on pending blocks (`RunOptions.MaxPendingBlocks`,
default `max(8, 2 x threads)`). Measured on the identical command
(`vseed search custom --all --block-size 64 --threads 16`), 120 s:

| | before | after |
|---|---|---|
| working set | 577 MB -> **7,599 MB**, still climbing ~60 MB/s | **112 MB, flat** |
| records written in 120 s | 101 MB | **1,477 MB** (14.6x) |

**Crash safety, proven by hard kills.** Flush and checkpoint are now on a clock rather than inside the
"a block was just emitted" branch; the results file is truncated back to the checkpoint's
`results_length` on resume; `<ckpt>.tmp` orphans are cleaned up; a completed run retires its own
checkpoint and kept-set snapshot; and the default checkpoint is `<cache root>\checkpoints\<query
hash>.ckpt`, never the CWD. Kills at 3 s, 6 s and 10 s of a 400,000-seed run, resumed to completion:

- bounded output - every resumed file SHA-256 `D03B3DE47701A73B`, identical to the uninterrupted run.
- `keep: all` streaming - every resumed file SHA-256 `C37A54901C601D28`, 127,490,329 B, 400,000
  records, identical to the uninterrupted run; the 6 s kill left **282,624 B** and the 10 s kill
  **978,944 B** beyond the checkpoint, ending mid-token, and both were truncated away on resume.

**Rotation for unbounded runs.** 1 GB (configurable) segments, `results.NNNN.jsonl.gz`, a
`results.manifest.json` with per-segment record counts, seed ranges, sizes and SHA-256, and a
`--reduce top:N` that reduces a closed segment, keeps the reduction and deletes the raw segment.
Measured: gzip **10.0x** (1,914,681 B of records -> 191,050 B on disk over 10 segments);
`--reduce top:10` left **16,012 B of 957,281 B** (59.8x). Every closed `.gz` decompresses to exactly
its manifest record count, because each checkpoint closes the gzip MEMBER and starts a new one, so
the file is valid at every point a resume could truncate to. `--rotate` with `--format json` is
refused (a split array is not valid JSON), and a non-mergeable reduction (`median:`, `p95:`,
`percentile:`, `diverse:`) is refused by name, because the raw records are deleted and a median
cannot be rebuilt from per-segment medians.

**Estimates calibrated on real work.** `RunEstimator` is fed one slice of real work at every
checkpoint and re-projects after each, always as a range with the evidence attached, and widens
itself when a slice falls outside what it previously claimed. Measured against a 300,000-seed run
that took 27.5 s: at 55,296 seeds it said "15.3 s .. 47.8 s left, 312 KB on disk"; the truth was
~22 s and 319,805 B. Bytes per record are MEASURED from the live file (the fitted law
`54 + 150.5 x goals` is an extrapolation from an 11-goal query and under-reads at small goal counts:
205 B predicted against 320 B measured for one goal), so it is used only before the first record
exists.

**Screen-then-verify.** `GridPolicy` encodes the resolution study per metric, with the measured loss
rates in the code; `TwoStageEvaluator` screens at a coarse grid with the measured margin and
re-measures every survivor at the query's own grid, so every record written carries the fine
measurement and says which grid screened it. Measured on a selective query (28 matches of 3,000
seeds): exact G12 11.6 s, screen G24 (1 %) then verify G12 **3.5 s - 3.32x** - with **0 false
negatives, 0 false positives and 0 differing records**. The screen is skipped for island,
spawn-island, island-count, coastline, peak, `area_above_height` and nearest-distance goals, for
which no margin at any grid is both safe and selective, and it is off entirely under
`--no-prefilter`.

**Preset defects.** `gentle-start` and `mountain-home` moved to grid 12 (both are disc-bounded, so
they stay cheap, and both had a must-have the study showed a coarse grid gets wrong - 50 % of what
`gentle-start` reported at G96 was not a match at G12, and Mountain's nearest-distance margin at G24
is 927 m against a 1,500 m goal). `balanced` and `balanced-biomes` moved `spawn_island_area` from
must to nice with the reason on the goal. `archipelago` (`island_count` must at G24) and
`large-continents` (`largest_island_area` must at G24) are **still defective**: the honest fix is
grid 12, which costs 6.8x, and that is a product decision rather than a silent change - the engine
now prints the measured defect before the run either way. `coastline_length` stays, documented as
grid-relative in the catalogue, in the schema, in the preset and in a warning the engine prints
whenever a query uses it.

**Refusals added:** an all-must-have query with a bounded output (a "top N" with nothing to rank by -
`--accept-scan-order` accepts it), a rotation that cannot be valid JSON, a non-mergeable per-segment
reduction, a `keep: all` run projected to be bigger than the free space, and a resume of a bounded
run whose kept-set snapshot is missing. **Warned, not refused:** a query with no must-have goal
(it matches 100 % of seeds), and a must-have on a metric this grid measures wrongly.

**Overlap to retire:** `SeedLab.Runtime` (added the same day) owns the hardware probe, the resource
modes, the auto-throttle and a cache root that resolves to the same `checkpoints` directory, plus a
`Calibration` with the same slice design. `SeedLab.Search` should take a project reference to it and
delete `CheckpointStore.Root` and the duplicated half of `RunEstimator`; nothing in either is wired
into the CLI yet.

**The proofs are a project now: `tests\SeedLab.Search.Safety.Tests`.** Everything above was
measured with it - `proof bounded|memory|run|rotate|refuse|policy|region|estimate|screen`, plus
`killtest.ps1`, which hard-kills a run at three points, resumes it and compares SHA-256 with an
uninterrupted one (run it bare and with `-KeepAll`). It is a SEPARATE project from
`tests\SeedLab.Search.Tests` on purpose: verified by inspection, all 70 of those checks construct
`SeedEvaluator`, `CompiledQuery`, `ScanPlan`, `Presets` or the feasibility analysis directly and
reach no sink, no checkpoint, no preflight and no grid policy - they would still pass with every fix
in this entry reverted. It is not yet wired into a single "run everything" command, and
`proof memory`, `proof region` and `proof estimate` report measurements rather than pass/fail.

It is **not** decisions item 9, which is still open: item 9 asks for a tripwire that fails if any
command writes outside the allowed set, and this suite checks what the search's output layer does,
not where the rest of the tool puts its files. The IL-level "zero System.IO members" finding is still
a one-off audit.

**One thing a resume still cannot do: a ROTATED run.** Segments are numbered from 0001 and opened
with `FileMode.Create`, so a resumed leg would overwrite the first segment and rewrite the manifest
without the others - silently, and totally. It is refused rather than attempted
(`ResultSinks.Create`), and `proof refuse` checks the refusal. The fix, when someone writes it: read
the manifest, continue at `segments.Count + 1`, and truncate the open segment back to the gzip-member
boundary the checkpoint recorded. Bounded and unrotated `keep: all` runs both resume exactly.

## 2026-09-23 - the safety layers reach the CLI (`src\SeedLab.Cli`)

An independent verifier found the output-safety, grid-policy and runtime layers **correct but not
wired into any shipped command**: `SeedLab.Cli` referenced neither `SeedLab.Runtime` nor
`SearchSession`, so `--keep` was a cap in the library and not through the CLI, `SearchPreflight`'s
refusal rule was never called, no record ever carried `screened_at_grid_m`, checkpoints landed in the
working directory and map PNGs accumulated there. All of that is now reached from `vseed`.

**What runs now** (each proved by a command, on a machine also running two other agents' builds
and desktop apps - so every TIME below is under contention and only the COUNTS are clean):

- `RuntimeContext` is started once per command for the ten commands that do real work; the
  arithmetic-only ones (`hash`, `invert`, `space`, `presets`, `data`, `worlds`, `world`) skip it and
  only validate the global options. New globals: `--mode background|balanced|full` (default
  **balanced**), `--threads`, `--cache-dir`, `--ignore-running-game`, `--skip-self-test`,
  `--accept-unverified-platform`. Every `Environment.ProcessorCount` sizing in the CLI is gone;
  `vseed search custom --seeds 4000` prints `workers 8 (balanced mode = 50 % of 16 logical cores ->
  8; memory allowed 787)`.
- **Auto-throttle fires.** With a process named `valheim` alive:
  `vseed: valheim is running - dropping to background mode (~25 % of cores, BelowNormal). Override
  with --mode full --ignore-running-game.` -> 4 workers at BelowNormal;
  `--mode full --ignore-running-game` -> 16 workers at Normal.
- **`--keep N` is a real cap through the CLI.** `vseed search custom --seeds 4000 --keep 50` wrote
  exactly **50 lines** and reported `top 50 of 4,000 matches; 3,950 were not kept`.
- **Refusals reach the user.** `--all --keep all` on a query with no must-have: *"it WILL write about
  818 GB and the volume has 599 GB free"*, exit 1, nothing written. An all-must query gets the
  decision-9 refusal, naming `--accept-scan-order`, which then runs.
- **Vacuity warning:** `this query has no must-have goal, so EVERY seed matches it: 4,000 of 4,000`.
- **Confirmations gate the run.** Non-interactive (`--json`, or stdin redirected) is REFUSED with
  *"Pass --yes to accept the points above"* rather than hung on a prompt nobody can answer.
- **Screen-then-verify reaches disk:** a three-must-have query wrote 30 of 30 records carrying
  `"screened_at_grid_m":24` beside `"grid":12`.
- Checkpoints go to `<cache>\checkpoints\<query-hash-16>.ckpt`, are kept while a run is unfinished
  and **deleted when it completes** (the report says which). `--resume` continued 4,416 -> 8,576
  seeds. `vseed map` with no `-o` writes into `<cache>\maps` and prints the path; nothing lands in
  the working directory. `vseed clean` reports every category and, verified by SHA-256 on all 25
  files, names `%USERPROFILE%\AppData\valheim-dumper` (48.77 MiB) as redundant - it reports it and
  never deletes it, because it is outside the cache root and is the raw dump `data\` came from.
- `--rotate 200KB --compress gz --reduce top:20` produced 5 reduced segments and a manifest;
  `--on-limit evict` with `keep: all` is refused by name.
- The CLI registers `NativesGoldenSuite : ISelfTestSuite` over `groundtruth\natives` (Perlin, Mono
  libm, `WorldAngle`, `GetStableHashCode` + the lane split): **263,780 checks**, stamped in the cache
  root, so a non-x64 machine is no longer stuck `Unproven`. It is registered only when the goldens
  are on disk - a suite that threw "file not found" would count as a FAILURE and fail-close a good
  x64 install. `RuntimeOptions` has no hook to register a suite BEFORE `RuntimeContext.Start` runs
  `Verify`, so the CLI starts the context, registers, and verifies again; that second outcome is the
  one `RequireVerified` enforces. A registration hook on `RuntimeOptions` would remove the double.

**Three defects found while wiring, two fixed here, one measured and left for `SeedLab.Search`:**

1. **The grid auto-upgrade was announced and not applied.** `GridPolicy.AutoPick` raises the grid to
   G12 for a must-have no coarse grid measures safely, `SearchPreflight` turns that into a
   confirmation - and `SearchSession.Create` compiles the query BEFORE the grid decision and never
   recompiles. `ScreenThenVerify.VerifyQuery`, whose whole job is to produce the query at the raised
   grid, **is called from nowhere in the repository**. So a run announced G12, was confirmed at G12
   and measured at G24. `SearchCommand.ApplyGridUpgrade` (and the same step in `ExplainCommand`) now
   re-canonicalises at the verify grid and rebuilds the session, carrying the first preflight's note
   and confirmation across. Proved: a query written at `"grid": 24` with a must-have `island_count`
   now writes records with `"grid":12`. **The proper fix belongs in `SearchSession.Create`, which the
   web UI also goes through.**
2. **The plan's grid line could contradict the run.** The preflight writes
   `grid <GridPlan.Describe()>` while `Create` may still turn screening off afterwards (`ScreenQuery`
   returns the original when no must-have is screenable, and the session sets `ScreenGrid = 0`).
   Measured: the same run printed *"screen at G24 with a 1 % margin, then re-measure every survivor
   at G12"* and reported `screen_then_verify: false` in `--json`. The CLI now re-renders that one
   line from the session's own grid at the moment of printing.
3. **`--max-bytes` is a stop-soon, not a hard ceiling.** `SearchRun` checks
   `sink.FileBytes >= MaxResultBytes` at a block boundary, and `FileBytes` only moves when the sink
   flushes. Measured with `--max-bytes 30KB --block-size 32` over a 200,000-seed budget: the file
   reached **132,334 B at `--checkpoint-every 30`** and **122,149 B at `--checkpoint-every 1`** -
   4.0-4.3x over, so the overshoot is not mostly the flush interval. Not fixed here (it is
   `SeedLab.Search`); the run does stop cleanly, flush and checkpoint, so the behaviour is safe -
   just not the number promised.

**Verified-not-a-defect, from a CLI polish list an earlier review carried:** `--json`/`--threads` are
already accepted on both sides of the command name (`vseed --json --threads 4 hash abc` works);
`--threads 0`, `-5` and `99999` are already refused with the fix named; `vseed seed 2147483648` and
`vseed seed -2147483649` are already handled symmetrically, both announcing the seed-TEXT reading;
and `presets list` no longer says anything about refusing to run. The map footer at small `--px` does
**not** collide - decoding the PNG puts the last ink row at y=596 of 606 at `--px 512`, and at
`--px 256` the legend degrades to one column and still fits (the footer is then nearly as tall as the
map, which is ugly but not a collision). Two things on that list WERE wrong and are fixed:
`presets list --json` compared its status column against the string `"runs now"`, which this command
has never produced, so `runs_now` was **false for every preset** while the human table said otherwise
(it now also carries `needs_locations` and `status`); and the islands caveat quoted the development
fixture's "~45x over a 16x change of grid" as though it had been measured on whatever seed was being
printed - it now names seed -1772362158 and says this seed's own factor was not measured.

`ServeCommand`'s selftest passed `TimeSpan.FromSeconds(30)` as the CHECKPOINT interval (on a run with
no checkpoint path - a no-op) and `TimeSpan.Zero` as the wall budget, which `SearchRun` reads as "no
time limit at all". The arguments were swapped; a selftest check that cannot time out is not a check.


### Caught in review of the same change, same day

- **`--dry-run` was briefly gated by the confirmation prompt.** The confirmation block sat above the
  `dryRun` branch, so `vseed search <preset> --all --dry-run` from a script exited 1 with
  *"pass --yes"* instead of printing the measured cost - the one thing `--dry-run` exists for. The
  dry-run branch now comes FIRST and prints the confirmations as *"Without --dry-run, this run would
  stop and ask about: …"*. Refusals still stop a dry run: timing a run that can never be answered
  honestly is timing nothing.
- **The plan's `threads` line had the grid line's bug.** The preflight is handed the count planned
  against the grid the QUERY asked for; the run uses the count planned against the grid the session
  settled on. Equal on this machine (memory is abundant), not equal where the guard bites at 12 m and
  not at 384 m - the plan block would then have printed two thread counts three lines apart. Both
  lines are now re-rendered from the session at print time.
- **`--skip-self-test` said "which says so on every result" and said it on none of them.** The
  Skipped outcome only appears in `StartupLines`, which `vseed seed` never prints. It is now a warning
  from `CliRuntime.Start`, so every command carries it.
- **`Args.Threads(fallback)` is deleted.** All six call sites moved to `RuntimeContext.PlanWorkers`,
  and the last `ProcessorCount` sizing fallback (`SelfTestCommand.SweepWorld`) went with it, so
  "no sizing in the CLI reads ProcessorCount" is now literally true. What is left of it is the
  `--threads` validation bound and two lines that REPORT the core count.
- **`vseed serve --selftest` failed, and it was the web layer's new confirm gate.** The server now
  runs the whole preflight and returns `400 {"kind":"confirm"}` for a query carrying confirmations -
  correct, and it means a hand-written POST cannot walk past the dialog. The selftest's own query
  posts as the page does, so it now sends `confirmed`/`acceptScanOrder`, and `"screen": "off"`
  besides: this check compares the page's seed list against a direct `SearchRun` at the grid the
  query names, and with the policy free to RAISE that grid the two sides would differ for a reason
  the check does not exist to catch. It passes again - 384 seeds, 200 hits from the page against 200
  from the terminal, identical in order and score.
- Fail-closed proved by counterexample: one recorded hash in a COPY of `groundtruth\natives` changed
  by 1, and `vseed seed 12345` from that directory exits 1 with
  `seedlab/natives: 263779/263780 exact  FIRST FAILURE: GetStableHashCode("StoneCircle"): the game
  recorded -1587608452, this machine computes -1587608451`. `--skip-self-test` then runs it and says
  the run is unverified.
- The live per-slice re-projection prints and SHARPENS: over one 12 s run, slice 1 projected
  `1,515 .. 5,256 matches`, slice 5 `3,997 .. 5,761`, each line carrying the basis it was measured on.

**All five gates re-run after the change and unmoved**: acceptance 32/32 with 0 biome mismatches over
5,104,872 pixels and 4,194,304/4,194,304 height codes per world; location gate 12,228 + 12,314 +
12,287 exact and 29/29 game-log counters; natives 11/11 with 262,780/262,780 Perlin samples;
GoldenCheck PASS, 0 differing; search tests 70/70; runtime tests 122/122. **None of the five
references `SeedLab.Cli`**, which is why this change could not have moved them - said out loud so
the counts are not read as evidence that they exercised it. The CLI's own behaviour is covered by the
commands above and by nothing automated yet; decisions item 9 (a tripwire that fails if any command
writes outside the allowed set) is still open and would now have a CLI to point at.

## 2026-09-23 - the feasibility checker, and two metrics deleted

### The metric catalogue lost two entries and gained one

- **`world:coastline_length` is RETIRED.** Not for the fractal reason first given - that is a pure
  ranking offset (the G24 value is 0.756x the G12 value with p5..p95 within one point of that ratio,
  Spearman 0.966 at G24). What kills it is that **it does not distinguish Valheim worlds**: at G12
  over 2,560 seeds p10/p50/p90 are 2,625,271 / 2,712,816 / 2,796,511 m, so the most coastal world in
  2,560 has **8 % more coastline than the least** (p90/p10 = 1.065, CV = 0.0245). A threshold on an
  8 % span is not a search criterion. `largest_island_area` (Spearman 0.851) and `island_count`
  (0.831) rank *worse* and are KEPT, because the distinction is **discrimination, not ranking
  fidelity** - those two have real spread between seeds.
- **`world:deepest_point` is RETIRED.** It is not a property of the seed at all: at G12 it is
  **-399.859 m for all 2,560 sampled seeds** (one distinct value), -396.308 at G24, -100.87 at G384.
  It reported the world-edge height of the outermost sampled cell, which is fixed by where the grid
  puts a cell centre near 10,500 m, and it moved 4x across grids while never varying by seed.
- Both get a **named refusal** from `RetiredMetrics` (`Criteria\MetricPresentation.cs`), consulted
  *before* "unknown metric" is ever considered - a removed metric that comes back as a typo message
  hides a decision.
- **`world:shore_area_within` (needs `radius`) replaces coastline.** The area of land (height >= 30 m)
  whose cell centre lies within **100 m** of a water cell centre, inside a disc of `radius`. The 100 m
  band is DEFINITIONAL and never a parameter. Computed by an exact Felzenszwalb-Huttenlocher
  squared-Euclidean distance transform over the height field the T3 pass already builds.

### Verified: the shore metric's numbers, measured twice by two implementations

Measured here at G12 on this machine, 48 seeds, against the metric-truth study's
(`docs\studies\coastline-verdict.md`) independent
implementation over 512 seeds - the medians agree to 1.7 %, 0.5 % and 1.2 %:

| metric | mine p10/p50/p90 (48 seeds) | metrictruth p10/p50/p90 (512 seeds) |
|---|---|---|
| `shore_area_within(1000)` | 695,261 / 912,528 / 1,144,310 m2 | 751,120 / 927,650 / 1,150,500 m2 |
| `shore_area_within(2000)` | 2,254,046 / 2,864,448 / 3,336,034 | 2,269,600 / 2,877,600 / 3,360,500 |
| `shore_area_within(5000)` | 15,884,006 / 17,251,344 / 18,425,059 | 15,462,000 / 17,042,000 / 18,555,000 |

Cost: the whole G12 measure pass (biomes + heights + the transform) is a median of **104 ms/seed**
over the whole world, against metrictruth's 116.4 ms for the transform alone - so the transform is
around +3 % of a G12 height query, as predicted.

`CompiledQuery` gives this goal a sampled disc of **`radius + 100 m`**, because the water a land cell
is near may lie outside the radius; a disc cut to `radius` would silently lose the shore on the rim,
which is exactly where a builder's shoreline is. Unsampled cells are treated as water only when they
are past the 10,500 m edge, which is the generator's own fact (`GetBiomeHeight` returns -400 there).

### Verified: island_count percentiles at G12, and a correction

`vseed`'s own measurement, 256 uniformly drawn seeds at G12 (`scratchpad\safe\calib`):

| metric | p10 | p50 | p90 |
|---|---|---|---|
| `island_count` min_area **10,000 m2 (1 ha)** | 209 | **225** | 244 |
| `island_count` min_area **100,000 m2 (10 ha)** | 128 | **138** | 148 |
| `largest_island_area` | 4,081,104 | 5,267,592 | 6,585,336 m2 |
| `spawn_island_area` | 193,176 | 2,258,064 | 5,471,136 m2 |
| `land_area` | 114,725,880 | 118,066,392 | 121,204,008 m2 |
| `biome:BlackForest area_within(1200)` (512 seeds) | 1,236,960 | 1,617,336 | 1,999,584 m2 |
| `biome:Meadows area_within(1000)` (512 seeds) | 1,399,853 | 1,695,672 | 1,993,032 m2 |

**CORRECTION, made in place**: `docs\studies\coastline-verdict.md` reports "median count at min_area
1 ha: 137/126/119/..." across G12..G384. That series is the **10 ha** count, not the 1 ha one - the
1 ha median at G12 is 225. `GridNotes.IslandCount` now says `min_area 100,000 m2` and carries both
medians, because a threshold on this metric is meaningless without the `min_area` it was measured
with. The consequence was live: `archipelago` shipped `island_count >= 120` at `min_area: 10000`,
which **256 of 256 measured seeds passed** - the minimum is 189.

### Three presets rewritten, each against a measurement

- **`gentle-start`** lost three of its five must-goals. `no-swamp`, `no-plains` and `no-mistlands`
  ("no X within 1.2 km") reject **nothing in any of the 4,294,967,296 seeds**, provably: Swamp needs
  `|p| > 2000` (`GetBiome:812`, no wobble on either endpoint), Plains `|p| > 3000 + A` (`:820`),
  Mistlands `|p| > 6000 + A` (`:816`). Its nice-to-have ranked on `biome:BlackForest
  nearest_distance`, which takes ONE distinct value - 508.3 m - in 2,560 of 2,560 seeds, so every
  seed scored the same 0.365; it now ranks on Black Forest AREA within 1.2 km. And `meadows-close`
  asked for 500,000 m2 within 1 km where the measured MINIMUM over 512 seeds is 1,028,448 m2, so it
  too filtered nothing; it is now 1,600,000 m2, just under the median.
- **`archipelago`** moved to `grid: 12` (`island_count` is non-monotone in the grid and no coarse
  grid measures it safely) and its must-have to the measured p90, 244. Both its ranking goals were
  inert: `largest_island_area at_most 3,000,000` scored 0 for 255 of 256 seeds (the measured minimum
  largest island is 3,143,808 m2), and `land_area between 80e6 and 130e6` scored 1.0 for all 256
  (land area's whole measured range sits inside that band, CV 0.0215). The land goal is deleted.
- **`coastal-builder`** moved to `grid: 12` and its `shoreline` goal to `shore_area_within(1000) >=
  1,000,000 m2` as a MUST. The old goal was `coastline_length >= 400,000` as a nice-to-have, and
  400 km is **4.75x below the minimum of 2,560 sampled seeds**, so its sub-score was exactly 1.0 for
  every seed ever scored.

### The feasibility checker (`src\SeedLab.Search\Feasibility\`)

`QueryCheck` runs on a query before a seed is touched and produces one `QueryCheckReport` that the
CLI and the web API both render. It **wraps** `StaticAnalysis` and `LocationFeasibility` rather than
replacing them. Five properties are worth recording because they are what makes it safe:

1. **Refusal is structurally hard-evidence-only.** `GoalCheck.Refuse` takes `params HardEvidence[]`,
   and `HardEvidence`'s constructor throws on `EvidenceKind.Sample`. A measurement cannot reach a
   refusal even by accident - a stronger guarantee than a test, and the test (T2) then only confirms
   the types were not worked around.
2. **A DATA-STAMP mismatch disables refusals entirely.** When the atlas and the live location table
   name different builds, every would-be refusal is printed as a warning naming the mismatch. The
   one exception is a contradiction between two goals, which needs no game data.
3. **Widen-F.** When two sound bounds disagree the LOOSER decides. The atlas computes the exact
   AshLands floor, 7,907.681 m; `BiomeGeometry.Band` returns 7,900 m; 7,900 decides.
4. **The group bound for `nearest_distance` is `max` over types, not `min`.** The metric is a min
   over the types PRESENT, so if only the widest-ringed type placed, that is the measured value. The
   design's own prototype used `min` and declared `boss-rush`, `balanced` and `compact-progression`
   vacuous.
5. **VACUOUS and DEGENERATE are different verdicts.** `nearest_distance` reports +infinity on
   absence, so a "near D" goal with D above the hard upper bound is never a no-op - it has become a
   PRESENCE filter ("a seed in which this placed at all"), which has a different fix.

**The count cap is `Q = sum of m_quantity`**, corrected from the design's "sum of maxCoexisting":
candidates of an `m_unique` type are counted individually, which `MetricDef.UniqueSemantics` already
said. Two rows of the design's own expected-verdict table were wrong because of it, and both are now
asserted the other way: `group:traders count at_most 3` is **not** vacuous (Q = 30, three types at
`m_quantity` 10 each) and `location:Vendor_BlackForest count at_least 2` is **not** refused. Shipped:
**D3** (world-wide `count` at N == Q -> WARN-DEGENERATE), **V5b** (`count at_most N`, N >= Q ->
WARN-VACUOUS), **A1** (an absence line per type in one of exactly three shapes: PROOF only for a
provably unplaceable type, MEASURED with a Wilson interval, or MEASURED rule-of-three). **D4 and D5
are DEFERRED** with the reason in the code: they need per-type count histograms in the calibration
sample, and today's sample holds distance percentiles only - built against it they would never fire
and their tests would pass for the wrong reason.
**Superseded 2026-09-23 (top entry): both shipped**, against the raw 5 000-seed x 183-type `placed`
matrix in `data\1.0.15-59f53fb5\count-sample.bin` — the missing measurement, kept raw rather than as
histograms.

**One deliberate deviation from the decision text**, because the decisions contradict each other:
decisions-2 asks for a hard REFUSAL on `shore_area_within` at `grid > 50 m`, but at G96 the metric is
measurably NOT zero (0.724x its G12 value), so refusing there would be a false claim about the seed
space - the very failure T1 exists to prevent. It ships as a **usage error** instead ("this build does
not define the metric at G > 50"), which blocks just as effectively without asserting anything about
seeds, and the separate PROVABLE line is stated at 100 m: no two cell centres on a grid are closer
than its spacing, so above 100 m no land cell has water inside the band and the metric is 0 in every
seed. The same reasoning applies to `radius >= 10,500`.

### The constraint atlas ships as versioned data

`data\1.0.15-59f53fb5\constraint-atlas.json` (190 KB), built from the constraint study's 597 KB
analysis atlas (the study is published as `docs\studies\constraint-atlas.md`). It is **derived**, not dumped, so it is deliberately NOT in `manifest.json`'s
`files[]`; `vseed data` iterates that list and does not mind extra files in the folder. It carries
the same DATA-STAMP and its own `soundness` field naming which half of it may refuse and which half
may only warn. `ConstraintAtlas` finds it by walking up from the working directory and from the
binary looking for `data\<version>-<hash>\`, exactly as `GameData.FindDumpDirectory` does - so
`SeedLab.Search` still references neither `SeedLab.Data` nor `SeedLab.Locations`, and a build with no
dumped data still compiles and simply issues no atlas-backed refusal.

### T1, the test that matters, measured

`CheckerSelfTest` ships in the library so the CLI, the web API and the test project all run the code
that ships. Measured on this machine, full pass, 1.0 s:

- **T1a** 36,829 real instances over 179 types (`asdasdasd` 12,314 + `testworldclaude` 12,287 +
  `locationinstances-0480A34C` 12,228), **0 outside the checker's own feasible set**. Rows resolve by
  **stable hash**, not by the prefab-name column - that column is blank on 10,293 of one CSV's 12,314
  rows, and a test that skipped those would report the whole corpus while testing a sixth of it.
- **T1b** 101,934 goals (`near ceil(d)`, `far floor(d)`, `between floor(d)..ceil(d)` per distinct
  (type, distance) pair), **0 refused**.
- **T2** 6 refusals examined, 0 sample-backed. **T4** 183 types, 0 looser than the live computation,
  38 tighter (max 7.7 m - the exact AshLands floor). **T5** 13 of 13 presets, 0 refusals, 0 usage
  errors, 0 vacuous or degenerate must-goals. **T6** gen_version honoured in both directions for
  Swamp (6,000 vs 8,000 m) and Mountain (600 vs 1,100 m). **T7** 15 corpus cases all as expected,
  plus 7 observed extremes including `Mistlands_RoadPost1` at 5,912 m - below the NOMINAL 6,000 m
  bound, which is the check that the wobble is applied to the right endpoint.

### Presentation, so a number cannot mislead later

Every goal on a results record now carries `measured_at_grid_m` (the grid THAT GOAL was decided at -
a location goal says 12, the game's own point grid, whatever `--grid` says), `grid_comparable: false`
plus a one-sentence `grid_note` for the eight metrics that are not comparable across grids,
`censored` when a distance is at or below the grid's resolution floor (`spacing * sqrt(2) / 2`;
70 % of seeds hit this on `nearest_land_distance` at G12), and
`measured_median_rel_err_at_grid` + `error_source` when the resolution study sampled that
(metric, grid) pair - and nothing at all when it did not.

**All five gates re-run after the change and unmoved**: acceptance 32/32 with 0 biome mismatches and
4,194,304/4,194,304 height codes per world; location gate 12,228 fresh + 12,314 + 12,287 played all
bit-exact and 29/29 game-log counters; natives 11/11 (429/429 hashes, 93/93 libm); GoldenCheck PASS
with 8,469,000 floats and 0 differing; search tests 70/70.

### Two defects the first pass shipped, and the tests that now hold them down

- **The shared zone budget refused a legal query.** The design's rule is
  `Sum(N_g) > zonesIntersecting(min_g R_g)`, and taken literally it charges every goal's instances
  against the SMALLEST disc. "one Fuling village within 300 m" plus "100 burial chambers within
  10,500 m" then needs 101 instances in the 89 zones that meet a 300 m disc, and is refused - though
  every seed satisfies it. The sound statement is a RUNNING sum by ascending radius:
  `Sum over goals with R_g <= R_k of N_g <= zonesIntersecting(R_k)` for every k, because only the
  goals at or inside R_k compete for that disc's zones. **T8** asserts both directions, and no
  shipped preset has two `count_within` must-goals, so nothing but a deliberate test would have
  caught it.
- **The stamp gate did nothing.** `Downgrade` called `GoalCheck.Warn(WarnRare, ...)`, and `Warn`
  only ever RAISES severity (`if (verdict > Verdict)`), so a refusal stayed a refusal and the
  non-negotiable "a DATA-STAMP mismatch disables refusals ENTIRELY" was silently false. A downgrade
  needs its own method - `DowngradeToWarning` - and **T9** is the test: it writes a copy of the atlas
  with ONE hex digit of the assembly hash changed, loads it with `ConstraintAtlas.LoadFrom`, and
  asserts that all four normally-refusable goals come back unrefused, that each message names both
  builds, and that the goal-to-goal contradiction (R13, which needs no game data) still stands.
  Every ordinary run has matching stamps, so this property had zero coverage until it was doctored.

Self-test at this revision: **19 checks, all passing, 1.0-2.8 s**, with T1b now 135,912 goals over
33,978 distinct (type, distance) pairs - the fourth goal shape, `count_within radius=ceil(d)
at_least 1`, exercises the R5 ring test and the atlas-backed one-per-zone cap against real instances
for free.

---

## 2026-09-23 - the dumper's second pass: rooms, normalised names, and a file that says NOT FOUND

Built and preflighted only; **not installed**. The copy armed in the live game is still the
2026-09-22 build (`2EFE0CA1...`). New build output
`tools\SeedLab.Dumper\build\SeedLab.Dumper.dll` SHA-256
`4351BFFE990EBAA191F1E2556848B6930C979D7C6419288ED38589D8F334BB4E`.

The first pass's child walk was reviewed as safe and as capturing what it claimed, and it still could
not answer the question it was built for. Five things changed.

**1. It walks room prefabs now (`roomchildren.json`).** A Valheim location with an interior contains a
`DungeonGenerator` and nothing else; the contents are ROOM prefabs out of `DungeonDB`, and nothing in a
location prefab's child tree names them. A complete walk of location prefabs therefore answers "which
prefab contains `piece_maypole`" with silence for any interior piece - and silence reads as "this game
has no maypole". `RoomWalk` reaches them through `DungeonDB.instance` (checked first: the public static
`GetRooms()` dereferences `m_instance` unguarded), records the room list, `RoomData.m_theme` and
`RoomData.m_enabled` - the pair `SetupAvailableRooms` actually filters on, not the `Room` component's -
all 13 `Room` fields, and the same interior walk location prefabs get. It never touches
`RoomData.RoomInPrefab`, which caches into private state on a shared object.

**2. `search.json` states an absence.** For each name in the new `SoughtPrefabNames` config (default
`piece_maypole`; **widened 2026-09-23** to add `TreasureChest_meadows_01/02`, top entry) it writes
`found` true or false, every hit with its host, path and gating RandomSpawn
index / chance / gates, a `coverage` block separating this run's gaps from what no dump can reach, an
`inconclusive` flag when a NOT FOUND had a real gap, and `existsInZNetScene` to separate "the name is
wrong" from "the piece exists but world generation never places it". The verdict sentences are printed
to the console and copied into the manifest notes.

**3. Names are normalised the game's way.** Everything now matches on `Utils.GetPrefabName` - truncate
at the first `(` or space - while keeping the raw spelling beside it, because a child authored as
`piece_maypole (1)` is `piece_maypole` to the game and would have been a false negative.
`prefabNames` is the new normalised index; `childNames` keeps the raw one.

**4. Three review defects fixed.** The nine prefabs with an active `m_useCustomInteriorTransform` now
flag each affected entry (`underGeneratorTransform` / `underInteriorTransform` /
`prefabPositionIsInstanceDependent`) instead of emitting a position the game will not read; the
enabled-vs-present comparison excludes root-mounted components on both sides, so a clean prefab is no
longer reported as session-drifted; and `ChildWalk`'s error handling matches its header - the whole
walk is wrapped, `loaded` means "the asset loaded" and `error` means "the walk failed", so
`locationprefabs.json` and `locationchildren.json` can no longer disagree about a prefab.

**5. Preflight.** 410 checks, 423 with `-SelfTest`, 0 failures. New coverage for `DungeonDB`,
`DungeonDB/RoomData`, `RoomList`, all 13 `Room` fields, the new `DungeonGenerator` fields,
`Location.m_interiorTransform` / `m_generator`, `Utils.GetPrefabName` and `extraCharacters`, and
`ZNetScene.GetPrefab`. New call-count checks pin `PlaceRoom`'s three arrays / three `InitState` / one
`set_state` / two `Randomize` loops, `GetPrefabName`'s one `IndexOfAny` + one `Remove`, and
`GetEnabledComponentsInChildren`'s root-transform exclusion - the clause the count fix depends on.
`Count-Calls` gained argument-count **and first-parameter-type** disambiguation, because
`PlaceRoom` has two overloads and `GetPrefabName` has two one-argument ones; the self test probes a
non-existent overload and requires the check to fail rather than silently resolve to another.

`SeedLab.Contracts.dll` SHA-256 `35728FA96086A6BEA0D7087999D1C64D3043BE9576C2CA3448CA5D3018A1903E`.
The build is deterministic - two clean rebuilds produced the same hash.

**`DumpFormat.Schema` stays at 1**, although four fields changed meaning (`activatedNetViewNames`,
`subtreeChildNames`, `offObjectChildNames` and `RandomObjectDef.subtreeChildNames` now hold normalised
names). The reason is written into `DumpFormat.cs` next to the constant rather than left to be
re-derived: the child walk has never produced a file, so no data with the old meaning exists anywhere
for a bump to protect.

**Desktop smoke test.** `Json.cs` has no Unity or game dependency, so the reflection JSON walker was
exercised against the new DTO shapes in a throwaway net10 console before the user spends a launch on
them - the `InteriorDef` base class (does `GetFields(Public|Instance)` return inherited public
fields?), `Vec3IntDef`, the whole `SearchFile` graph, an empty-terms document and a failed entry. It
passes.

Tripwires re-proved by planting real violations in a **scratch copy** of the tree: a `File.Delete`
inside `RoomWalk.Run` (an iterator, so the call lands in the compiler-generated
`RoomWalk/<Run>d__0.MoveNext`) and a `File.WriteAllText` inside a lambda in `ModeAssets.Run`
(`ModeAssets/<>c__DisplayClass0_0.<Run>b__7`). The preflight named both and exited 1.

**Run it in a freshly CREATED world, before exploring.** `SpawnLocation` and `PlaceRoom` both call
`Reset()` on every RandomSpawn of the shared prefab asset, which leaves its `m_OffObject` inactive
forever, so a later dump can see a shorter enabled array than the authored prefab has - and that array
is the draw budget. Minimal, not zero: the spawn-point zones have already spawned, and `WoodVillage` /
`WoodFarm` are camps, so a few MeadowsVillage / MeadowsFarm rooms may have been placed too.

## 2026-09-23 - the documentation pass: the record matches the tool

Step 4 of the scoping rule in `seedlab-decisions-2.md` (published as `docs/studies/decisions-2.md`). The libraries had been finished and then
wired into `vseed` and the web UI by two parallel agents; the documentation still described the
pre-wiring tool, and several things had been reported to the user as done that were true only of the
libraries. This pass re-measured the cost table on a quiet machine, re-ran every user-visible claim as
a command, and rewrote the docs against what it saw.

### The measurement pass (all of it in the project's `docs\measurements.md`)

Machine idle (CPU 1.0-3.7 %, Valheim not running, ten idle `dotnet` build servers resident), binary
built 10:52, each row one real run's own `measured rate`, at the shipped default `--mode balanced`
(8 of 16 logical cores): one biome goal in a 1 km disc **2,046 seeds/s** (whole space 24.3 d);
`mountain-home` **1,736** (28.6 d); `balanced-biomes` **38.7** (3.5 y); `coastal-builder` **19.5**
(7.0 y); `gentle-start` **7.3** (18.6 y); `all-traders` **5.7** (23.9 y); `archipelago` **5.6**
(24.3 y). `--mode full` measured **1.45-1.54x**, not 2x, on an 8-core/16-thread part.

**Every earlier throughput figure is superseded**, not averaged: the presets were rewritten and the
grid policy now screens, so the old table's rows (1,749 / 2,344 / 391 / 13.5 / 8.2 / 5.2 / 2.6 at 16
threads) describe queries that no longer exist. `proofs-and-gates.md` section 4 was corrected in place
and now points at `docs\measurements.md` as the single source.

> **Superseded in turn.** The single measurement pass recorded at the end of this file wrote
> `docs\measurements.md` and `docs\measurements.json` and is now what that file contains. The seven
> rows above agree with it to within 0.3-17 % (the largest gap is the one-biome-goal row, 2,046 here
> against 2,405 there), which is the right order for two runs taken while different other work was on
> the machine. **Quote the file, not this paragraph.**

Two findings that came out of measuring rather than reading:

- **`mountain-home` matched 0 of 217,600 seeds** (53,760 balanced + 163,840 full). Not a defect - its
  must-haves are Mountain within 1,500 m *and* >= 3 km2 of Mountain inside 2.5 km - but rule of three
  puts it under 3 in 217,600 (<= 0.0014 %, 95 %), which is worth saying out loud in a preset list.
- **`--block-size` decides how many workers actually work.** `gentle-start --seeds 400` is two blocks
  of 256, so two of eight workers ran: **2.0 seeds/s** against 7.3 for the same preset with
  `--block-size 16`. Now documented in three places, because it looks like a performance bug.

### Claims re-verified as commands, not recalled

`--keep 10` over 400 seeds wrote 10 records and reported `top 10 of 82 matches; 72 were not kept`;
`--all --keep all` on a no-must-have query was **refused** (exit 1, *"about 818 GB and the volume has
599 GB free"*, nothing scanned); an all-must-have query was refused naming four fixes; a vacuous
`Eikthyrnir near 1200` goal was refused; `coastline_length` gave its named retirement refusal with the
p90/p10 = 1.065 measurement and the `shore_area_within` example; `gentle-start --all` stopped for
confirmation and named `--yes`; 20 of 20 records from `gentle-start` and `coastal-builder` carried
`"screened_at_grid_m":24` beside `"grid":12`; `--keep all --rotate 200KB --compress gz --reduce
top:20` produced 6 segments and `seg.manifest.json {complete:true, records:3000}`; `vseed map` with no
`-o` wrote under `<cache>\maps` and left the working directory untouched; `vseed clean` reported per
category and named the dumper's 48.77 MiB folder as redundant (25 of 25 byte-identical) without
deleting it; `presets list --json` reported `runs_now: true` for **all 13** presets; and
**`vseed serve --selftest` exited 0 with every check PASS**, including `search panel vs the engine` -
the failure a mid-day run reported (its fixture tripping the new grid-raise confirmation) is gone on
this build.

The fail-closed self-test was re-demonstrated here rather than quoted: a copy of
`groundtruth\natives` with one recorded hash changed by 1, placed on the walk-up path
(`NativesGoldenSuite.FindDirectory` tries `Verified.FindGroundTruth()` - which needs a `decoded`
folder - then walks up to 12 levels from the cwd and from the binary), made `vseed seed 12345` exit
**1** with `seedlab/natives: 263779/263780 exact  FIRST FAILURE: GetStableHashCode("StoneCircle")`
and both numbers. The real ground truth was never touched.

One claim did **not** reproduce: `--skip-self-test` prints its `bit-exactness is UNVERIFIED` warning
on `seed`, `at`, `map`, `locations` and `search`, but **not** on `hash`, `invert`, `space`, `data` or
`worlds` (checked one command at a time). Those do not build a world, so it is defensible - except
that `hash` and `invert` rest on the 429 verified hash vectors. Recorded as a gap rather than a fix,
since this pass owned only the documentation.

### The dumper's real state, with its evidence

The README said the dumper was "run once on 2026-09-22 and then removed". Half of that is wrong and
the other half is a timezone: `BepInEx\plugins\DoomMachine-SeedLabDumper\` still holds both DLLs and a
`dumper.enable` containing `all`, and `BepInEx\LogOutput.log` records the session
(`SeedLab.Dumper 1.0.0 is ARMED in mode 'all'` -> two `DONE` lines). The shipped files in
`data\1.0.15-59f53fb5\` carry local mtimes of **2026-09-23 01:48-01:50** while their DATA-STAMP says
`dumped=2026-09-22`, because `GameInfo.Stamp` writes `DateTime.UtcNow` and this machine is UTC+3. Both
dates describe one run. The two SHA-256s, not the date, are what identify the build.

### What was written

New in the project: `docs\measurements.md` (the single source for every cost number),
`docs\limits.md` (what is **not** true of the tool - the eight grid-incomparable metrics with their
measured errors, censored distances, the bounded shortfall rates, the unpredictable things, one
untested architecture and what the self-test does about it, and the unimplemented list),
`docs\finding-a-seed.md` (the user-facing path from nothing installed to standing on the map), and
`CHANGES-FOR-THE-USER.md` at the repo root. Rewritten: the README's command, web, searching, game-data
and layout sections; `docs\search.md`; and the dumper, data, locations, generator and web pages.

## 2026-09-23 — the dumper's three paths to a confident false NOT FOUND, closed

An adversarial review of `tools\SeedLab.Dumper` found three ways `search.json` could report a NOT FOUND
without qualifying it. All three fixed; nothing else in the plugin changed.

1. **Zero coverage read as full coverage.** `SearchIndex.Coverage()` decided each source from a boolean
   ("did the walk run"). `RoomWalk.Run` sets `RoomWalkRan = true` after its loop, and an empty
   `DungeonDB.GetRooms()` falls straight through it, so a run that walked no rooms reported complete
   coverage and `inconclusive` stayed false. Coverage is now decided by **counts**: a source that
   contributed zero items is listed in `notSearched` whatever its flag says, for location prefabs, room
   prefabs, both tables and the RandomObject option scan. The verdict line now quotes *loaded of walked*
   rather than walked. The flags are still written to the file, as report only.
   *(The reviewer attributed this to a null `DungeonDB.instance`; that path `yield break`s before the
   flag is set. The live route is an empty room list — and an empty location candidate list.)*
2. **A prefab's own name was never searchable.** `ChildWalk.ChildNames` skips the root, so a sought name
   that IS a room or location prefab could not match. Root name now recorded on every entry
   (`rootName` / `rootNormalizedName` / `rootNormalizedHash`, new on `InteriorDef`) and matched, with the
   hit recorded as the **host itself**: null `path`, gate `-1`. The `SoftReference` spelling is offered
   too when it normalises differently from the loaded asset's GameObject name.
3. **A chest behind a RandomObject option had no drop table.** Resolved rather than merely reported:
   `RandomObject.ObjectEntry.m_object` is a plain `GameObject` field, **not** a `SoftReference`, so the
   option prefab is already resident and reading it is a pure component query — no `Load()`, no
   `Release()`, no reference count to unbalance. Each option entry now carries `containers`
   (full `m_defaultItems` drop tables), `containerCount`, `scanError` and `nestedRandomObjectCount`, and
   the names inside the option are matched too. The one boundary left is deliberate: options are
   resolved exactly ONE level deep. RandomObjects nested inside an option (counted only when they have
   a non-null option of their own) go to `standingLimitations`, **not** `notSearched` - a village whose
   house options carry their own RandomObjects makes that count non-zero in essentially every run, and
   a limitation present every time would mark every verdict in every dump inconclusive and leave the
   word meaning nothing.

Build: `dotnet build -c Release -t:Rebuild -p:DeployToGame=false`, reproducible across two consecutive
runs - `SeedLab.Dumper.dll` `015BC685FAB4157B4B3F7E0163D0D30873BA948B22CD426F4C51AB2354609978`,
`SeedLab.Contracts.dll` `930FF2745ACB12ECAA7DC3C3FF4D63869256145261E79FA073BD5E30A00A872A`.
`preflight.ps1` PASS 410/0, `-SelfTest` PASS 423/0. Not deployed - the user installs it.
An earlier agent's reported `4351BFFE99...` does not reproduce from this tree; the pre-change tree
builds `D6218D940CF39BAE43CC8D992B287DEC3F365A251AEADE559588C7A9850CCE7C` every time.
## One clean measurement pass, and what it corrected (2026-09-23)

Every throughput figure the project had quoted was taken while other work competed for the machine,
and they contradicted each other - 4.6 days, 28 days, 4.42 days and 2.3 days for "the same kind of
run". This pass replaces all of them with one set of numbers, taken in a single session on the
shipped CLI, and writes them to **`docs\measurements.md`** plus **`docs\measurements.json`**
(schema `seedlab-measurements/1`). That file is now the single source; the README, `docs\`, the
CLI's own help and the web UI are meant to cite it rather than carry their own figures.

**What it covers.** 44 throughput cells, each the median of three clean repetitions: the biome ladder
(G384/256/192/96/48/24/12 x 1/8/16 workers), the three runtime modes, height and rivers at G384 and
G12, five location shapes from one boss to the full 183-entry placement, and the region-restricted
case; all 13 shipped presets exactly as shipped; bytes per record per format, file size against
`--keep`, rotation and gzip, and checkpoint size and cost; and the three claims the user had been
told. 410 `vseed` invocations in total, every one kept in the JSON under `runs_raw`.

**Machine and provenance.** Ryzen 7 9800X3D (8C/16T), 61.6 GiB, Windows 10 Pro, .NET SDK 10.0.401,
`vseed.exe` SHA-256 `AD5139B5F093E2C5...`. There is no commit id - the folder is not a git repository
- so the build is identified by the binary hashes, which were re-hashed after the pass and were
unchanged. Background load, measured as per-process CPU deltas with no `vseed` running, was 1.22 % of
the 16-thread box.

**The three claims, all confirmed end to end through the CLI:**

1. A genuine `--all` run with **no** `--keep` (so the default 1,000 applies) scanned 971,008 seeds,
   matched all of them, and wrote **1,000 lines / 433,623 B**. Bounded.
2. A hard kill (`Stop-Process -Force`, not Ctrl-C) partway through, then `--resume`, gives a file
   **byte-identical** to an uninterrupted run - proved twice, once bounded (`--keep 1000`) and once
   streaming (`--keep all`, 26,735,098 B, 16 workers). The streaming case really did tear the file:
   the resume reported *repaired a torn results tail: 598,016 B written past the checkpoint were
   discarded*, and the SHA-256 still matched.
3. `--dry-run` lands **8 of 8 tiers inside 2x, worst 1.23x**.

**Corrections this pass makes:**

- **The "35x over-projection" claim is wrong on this build and could not be reproduced in any tier.**
  On the cheapest biome-only query `--dry-run` *under*-projects by 2x (10,526 seeds/s estimated
  against 20,990 measured). The independent verifier's "4 of 5 inside 2x, worst 3.26x" does not
  reproduce either. The text in `SearchCommand.cs`'s `--dry-run` note, the README and this skill all
  still say 35x.
- **The region lever is 81.5x**, not 39.8x and not 162x, for `biome:Swamp area_within radius 1000` at
  G12 on 16 workers - measured against the same goal with `--no-prefilter`. The factor belongs to the
  goal and the radius; quote it with both. The same comparison proved the restriction **exact**:
  identical per-seed values over 2,000 seeds.
- **JSONL segments compress 10.73x**, not the 10.0x the criteria-language schema states.
- **A JSONL record now costs about 449 bytes per goal** (434 B at one goal, 1,779 B at four), because
  every goal carries `measured_at_grid_m`, `measured_median_rel_err_at_grid`, `error_source` and
  where they apply `grid_note` / `censored`. The web UI still projects file size with the fitted
  `54 + 150.5 x goals` law, which under-reads a real file by about 3x. CSV is unaffected (85.8 B at
  one goal) because it carries none of the presentation fields.
- **`Calibrate`'s 10.5x 16-thread assumption is confirmed**, not superseded: measured 10.8x at G384
  down to 9.9x at G12.
- **`--mode full` buys 1.59x over balanced** on the G24 biome query.

**Two behaviours of the shipped build that nothing documents:**

- **A wall budget cannot stop a run sooner than one block per worker.** `vseed search all-traders
  --budget 20s` at the shipped `--block-size 256` with 8 workers ran for **355 s** and evaluated
  2,048 seeds - one full round - because the budget is checked at block boundaries. At
  `--block-size 32` the same command takes 46 s.
- **A rivers-only query pays for a sampling grid it never reads.** `river_count` comes out of the
  pre-generation: the per-seed values are identical at G384 and G12, but the run is about 6x more
  expensive at G12.

**The methodological trap this pass fell into first, and the reason it is worth recording:** the
first attempt left `--block-size` at its shipped default. One worker computes a whole block, so a
540-seed G12 run is two blocks and 16 workers finish it in the time two would - it measured **1.04x**
scaling from 1 to 8 workers where the truth is **6.36x**, and it made `--dry-run` look 7-11x
over-optimistic on the fine-grid tiers. Every cell was re-run with at least 8 blocks per worker. This
is very probably what produced the 35x and the 3.26x as well, though that is an inference. Spread
across the three repetitions of a cell is now 5.7 % at worst and under 5 % in 43 of 44 cells.

Contention that could not be removed, stated because it bounds everything above: another agent ran
its own `vseed` commands on this machine for part of the pass, and one of this pass's own preset
scripts was orphaned by a parent kill and ran for about 50 minutes beside the live one. The harness
watched for any `vseed` process other than the one it had started and **discarded and repeated every
run during which one existed**, so no accepted measurement overlapped another; the cost was waiting,
not accuracy.
