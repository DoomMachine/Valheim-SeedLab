# SeedLab.Runtime - the runtime and resource layer

Everything that has to know about **this machine** rather than about Valheim's world generation:
the hardware probe, the three resource modes and the worker/memory guard, the auto-throttle that gets
out of the game's way, the cache root and its lifecycle, the session log and the file-access checks,
the cost estimator with live re-calibration, and the startup self-test that fails closed on a machine
SeedLab has not verified.

It exists because of one decision in `docs\studies\decisions-1.md` section 7 - *"the software will
run on other machines, which may not be as beefy as mine"*, and such a tool *"is not meant for terribly
heavy use while the game is running"*. Nothing in here may assume this machine.

**Dependencies: none.** No NuGet, and no reference to any other SeedLab project, so both
`SeedLab.Cli` and `SeedLab.Web` can take it without dragging `WorldGen`, `Render` or `Search` into a web
request, and so the self-test can run before a single generator type is loaded. Everything is BCL:
`Environment.ProcessorCount`, `GC.GetGCMemoryInfo`, `DriveInfo`, `RuntimeInformation`, `Process` and
`System.Runtime.Intrinsics.X86`. No WMI, no registry, no P/Invoke.

Tests: `dotnet run --project tests\SeedLab.Runtime.Tests -c Release` (173 checks, exit 0/1).
This machine: `dotnet run --project tests\SeedLab.Runtime.Tests -c Release -- --probe`.

---

## The one call a host makes

```csharp
using RuntimeContext ctx = RuntimeContext.Start(new RuntimeOptions
{
    Mode = ResourceMode.Balanced,     // --mode background|balanced|full   (balanced is the default)
    Threads = null,                   // --threads N, still memory-capped
    CacheDirectory = null,            // --cache-dir, else SEEDLAB_CACHE_DIR, else the per-OS location
    AutoThrottle = true,              // --ignore-running-game turns it off
    SelfTest = true,                  // --skip-self-test turns it off (and says so on every result)
    Log = Console.Error.WriteLine     // where the layer's one-line announcements go
});

foreach (string line in ctx.StartupLines()) Console.WriteLine("  " + line);
ctx.RequireVerifiedMachine();         // fails closed: throws SelfTestFailedException

WorkerPlan plan = ctx.PlanWorkers(WorkTier.BiomeGrid, gridCells);
foreach (string line in plan.Lines()) Console.WriteLine("  " + line);

Estimate estimate = ctx.Estimator.Project(ctx.Inputs(query, plan, outPath));
Console.Write(estimate.Format());
if (estimate.Refused) return 1;
if (estimate.NeedsConfirmation && !yes) { /* ask */ }
```

`RuntimeContext.Start` probes the machine, opens the cache root, reaps what a killed process left
behind, looks for a running game, runs the self-test and applies the process priority. Disposing it
deletes the scratch directory, stops the throttle timer and restores the priority. Measured cost of the
whole thing on this machine: single-digit milliseconds, of which the self-test is ~3 ms and only on the
first run after anything about the machine, the runtime or the vectors changes.

---

## 1. The hardware probe - `SeedLab.Runtime.Hardware`

```csharp
HardwareInfo hw = HardwareProbe.Probe(options);   // never throws
VolumeInfo v  = VolumeInfo.For(@"D:\out\results.jsonl");   // also never throws
```

| Member | Notes |
| --- | --- |
| `LogicalCores` | `Environment.ProcessorCount`: honours affinity and container limits |
| `PhysicalCores`, `PhysicalCoreSource` | `null` when the BCL cannot say. Read from `/sys/devices/system/cpu/*/topology` on Linux; on Windows and macOS there is no BCL route that is not WMI, sysctl or P/Invoke, so it reports `unavailable` rather than guessing a SMT factor. `SEEDLAB_PHYSICAL_CORES` or `PhysicalCoresOverride` supply it |
| `TotalMemoryBytes` / `AvailableMemoryBytes` / `MemorySource` | `GCMemoryInfo.TotalAvailableMemoryBytes - MemoryLoadBytes`, or `MemAvailable` from `/proc/meminfo` where it exists (the kernel's better answer). `MemoryIsProcessLimit` flags a container/job limit |
| `ProcessArchitecture`, `OSArchitecture`, `RuntimeIdentifier`, `OSDescription`, `FrameworkDescription` | |
| `Features` | `Sse2/Avx/Avx2/Avx512F/Fma/AdvSimd`, `Vector<byte>.Count`. **`Fma` is recorded and never used** - a fused multiply-add rounds once where the game rounds twice |
| `PlatformKey`, `Lines()`, `Format()` | identity for the self-test stamp; the printable block |
| `VolumeInfo` | `AvailableFreeSpace` (what a quota actually allows), `TotalBytes`, `Format`, resolved by longest-prefix match so a Unix mount point is not confused with `/` |

**Trap, measured 2026-09-23 on win-x64 / .NET 10.0.12:** `GCMemoryInfo.MemoryLoadBytes` is **0 until the
first GC of the process**, which reads as "the whole machine is free". The probe forces one gen0
collection (0.4 ms) when it sees a zero. Without that the memory guard would size every run as if 61.6
GiB were free when 20.9 GiB is in use.

## 2. Modes and the worker plan - `SeedLab.Runtime.Execution`

```csharp
WorkerPlan plan = WorkerPlanner.Plan(mode, WorkerFootprints.For(tier, gridCells), hw, options);
using PriorityScope p = PriorityScope.Apply(plan.Priority);
```

| Mode | Workers | Priority |
| --- | --- | --- |
| `Background` | ~25 % of logical cores, min 1 | `BelowNormal` |
| **`Balanced` (default)** | ~50 %, min 1 | `Normal` |
| `Full` | all logical cores | `Normal` - **never above it**, and a caller asking for `High`/`RealTime` is clamped |

`Workers = min(mode share, floor(available RAM x 0.5 / per-worker footprint))`, never below 1. When
memory is what decided, `MemoryLimited` is true and `Lines()` prints **both** numbers
("16 asked for, 8 will run"). `InsufficientMemory` says even one worker does not fit - the flag a caller
should refuse on. `RequestedWorkers` (`--threads`) replaces the mode's share and is still memory-capped;
`ReservedBytes` takes the bounded heap and output buffers out of the budget first.

Per-worker footprints (`WorkerFootprints`, measured 2026-09-23, and a **parameter** everywhere - the
engine may hand the planner a better number at any time):

| Tier | Footprint |
| --- | --- |
| `BiomeGrid` | `5.997 B x cells + 11.9 KiB` - the line through the measured G384 (~30 KiB) and G12 (~24 MiB) anchors |
| `HeightsRivers` | grid buffers + the measured **50-60 MB** per seed, as a range |
| `LocationsCore` / `LocationsAll` | ~65 MB, independent of the query's grid: placement runs on the game's own 2048^2 grid |

## 3. Auto-throttle - `SeedLab.Runtime.Throttle`

```csharp
AutoThrottle t = new AutoThrottle(requested, watchOptions, announce, lister);
t.Start();                 // one check now, then a timer (15 s by default)
t.Poll();                  // or call it between slices - 2.0 ms measured for 315 processes
ResourceMode m = t.EffectiveMode;
```

A detected game drops the run to `Background` and prints **one** line naming the process and the
override (`--mode full --ignore-running-game`); re-checks do not repeat it; the return to the requested
mode when the game exits is announced too, because no speed change is ever silent. `GameWatchOptions`
carries the process-name list (defaults cover `valheim`, `valheim.x86_64`, `valheim_server`,
`valheim_server.x86_64`, case-insensitively and with `.exe` stripped), the poll interval, and
`Enabled = false` for the whole feature. `IProcessLister` makes all of it testable without a game.

## 4. The cache root and lifecycle - `SeedLab.Runtime.Storage`

```csharp
CacheRoot cache = CacheRoot.Open(new CacheRootOptions { Override = cliFlag });
ReapReport reaped = cache.ReapAbandoned();                 // call at every launch
using ScratchDirectory scratch = cache.CreateScratch("search");
DurableWrite.Text(Path.Combine(cache.Checkpoints, name), text);
DiskUsageReport usage = cache.MeasureUsage(outputPath);
```

One wipeable place per OS - `%LOCALAPPDATA%\SeedLab`, `$XDG_CACHE_HOME/seedlab` (or `~/.cache/seedlab`),
`~/Library/Caches/SeedLab` - overridden by `SEEDLAB_CACHE_DIR` or `--cache-dir`, with
`checkpoints/ runs/ maps/ tiles/ scratch/ selftest/ logs/` inside it. Deleting the whole root at any moment
must never lose anything the user asked to keep.

* **Scratch** is `scratch/pid-<pid>-<process start, UTC>` plus an `owner.txt` recording both. It is
  deleted on dispose and, because a killed process cannot clean up after itself, **reaped by the next
  launch**: a directory is kept only when its pid is alive *and* started at the recorded time, so a
  reused pid can never cost a live run its scratch.
* **`DurableWrite`** is temp+rename for every durable write: a sibling temp file in the same directory
  (so the rename cannot cross a volume), `Flush(true)` to the device, then `File.Move(overwrite: true)`.
  A writer that throws leaves the previous file untouched and removes its temp file. Temp names carry
  the pid, and `CleanOrphans` reaps the ones whose process is gone.
* **`DiskUsageReport`** is "what am I using on disk", by category, with the volume's free space - the
  report behind `vseed clean` and the web UI's storage panel. It walks only SeedLab's own directories
  plus paths the caller names.

### When another program has a file open (2026-09-24)

A run died of `Access to the path is denied.` - no file named, no cause. Measured: ANY open handle on
the target makes the rename of a temp-and-rename fail, whatever it shares (a reader that shares read,
write and delete included), and a read-only file or a folder permission throws the very same exception
with the very same HResult. Retrying only the rename 10 ms later fixed 9 of 9 failures against a reader
that opened the file every 50 ms. So:

* **`FileRetry`** retries an operation that failed for a transient reason (an access denial, a sharing
  or lock violation) on a **`RetrySchedule`** - `Quick` (~1.6 s, for saves a run repeats every interval)
  or `Patient` (~15 s, for saves nothing repeats) - and then throws a **`FileAccessException`** (an
  `IOException`) whose message comes from **`FileRetry.Diagnose`**: the file by full path, what
  probably happened (another program has it open / marked read-only / the folder's security settings /
  the folder is gone) and what to do. Never an HResult or a type name in that sentence; the session log
  gets those. `DurableWrite.Replace(temp, target)` is the rename for every temp-and-rename site in
  SeedLab, each keeping its own temp name; `DurableWrite.Stream/Text/Bytes` use it.
  **`FileRetry.DiagnoseEscaped(ex)`** is for a failure that reached a host WITHOUT the retry: a
  `FileAccessException` gives its own diagnosis, a raw sharing violation or access denial whose
  message names the file (`'...'`) is diagnosed from that file, and anything else - the rename's
  path-less "Access to the path is denied." included - gives null, so the host says what it can and
  points at the session log instead of guessing a file.
* **`AccessCheck`** - `Directory` (creates it, probes with a temp file that deletes itself on close;
  with `create: false` a missing folder is not created and the nearest folder above it is probed -
  for a check that must leave nothing behind, a `--dry-run` or a refused run),
  `FileForWrite` (opens without truncating, sharing everything, so it trips on a holder that denies
  writing - a spreadsheet), `FileForRead` - records every result with its time, and
  `StartCheckFor(path)` tells a later failure what the check said: when it passed, whether it was the
  file itself (`Examined`) or only its folder, and whether the same check made again NOW fails. Only
  then does the diagnosis say "It passed SeedLab's access check at 14:03:12, so something changed after
  that"; a file that passes again and is in use is held by a program that lets others write, which the
  check cannot see, and is said to be; a folder-only check says nothing about the file (review of
  2026-09-24: it used to claim a change in all three cases). A start check cannot predict a scanner
  that opens a file an hour later; the retry is the fix, the check is the diagnosis.
* **`FileProblem.DiskFull`** - `FileRetry.IsDiskFull(ex)` (HResult 0x80070070 or 0x80070027) and
  `DiagnoseEscaped` name a full drive as one, from the path the message carries; never retried.
  `FileProblems.Name(p)` is the one JSON spelling of a problem (`in_use`, `disk_full`, ...).
* **`DurableWrite.WriteTemp`** is the first half of `Stream` - the complete, flushed temp file, not
  yet renamed - for a caller that asks before giving up on the rename (`vseed map` keeps the rendered
  image while it asks). `Stream`, `Checkpoint.Save`, `BoundedResultSet.SaveSnapshot` and a rotated
  run's manifest take an `onAttempt` hook, so a front end can say "waiting" at the first failure.
* **`SessionLog`** - `<cache root>\logs\vseed.log`, emptied at the start of every session, the way
  BepInEx writes `LogOutput.log` (`FileMode.Create`, write access, sharing read only): a second live
  session writes `vseed.log.1` .. `.4`, then none; unlike BepInEx it deletes numbered logs no session is
  using, flushes every line, and never throws. Lines are
  `yyyy-MM-dd HH:mm:ss.fff zzz  LEVEL  text`. `RuntimeContext.Start` opens it straight after the cache
  root and writes the session's header (time with its UTC offset, program and version, the command
  line, pid, OS and .NET, the cache root), the access check of the root and every category, the reap,
  the self-test and every `RuntimeOptions.Log` line; `Dispose` writes the end with `ExitCode`.
  `SessionLog.Current` is the log for code too deep to be handed one. Read it sharing read AND write.
* **`SharedRead`** opens the files a run writes (checkpoints, snapshots, survivor lists) sharing read,
  write and delete, so a reader never stops a writer's delete or open.

## 5. The estimator - `SeedLab.Runtime.Estimation`

```csharp
Estimate e = estimator.Project(inputs);                          // before the run
calibration.ObserveSlice(seeds, elapsed, matches, bytesWritten); // after every slice
Estimate now = estimator.Reproject(inputs, calibration, seedsDone);
```

`EstimateInputs` = seeds, tier, grid cells, threads, format, goal count, `KeepN` (null = `--keep all`),
optional expected hit rate, free space, available memory, footprint, `AllSeedsFlag`.
`Estimate` = `Time`, `Memory`, `Disk`, `Matches` as `EstimateRange` (low/high/geometric mid),
`SeedsPerSecond`, `Calibrated`, `Constants` (each `ConstantUse` says **measured** or **ASSUMED** and
where it came from), `Verdict` (`Ok` / `Confirm` / `Refuse`), `Reasons`, `Notes`, and `Format()` for the
block printed before **every** run.

Cost constants (`CostModel`, measured on a Ryzen 9800X3D at 16 threads, 2026-09-23, and reproduced to
within 1 % by the model's own unit tests): biome G384 10,717 seeds/s, biome G12 18.2, heights+rivers
G384 28.6, bosses+traders ~1 s/seed/thread, all 183 location types ~7 s/seed/thread; parallel efficiency
84 % (compute-bound) and 56 % (memory-bound) at 16 threads; record sizes `54 + 150.5 x goals` (JSONL) and
`75 + 28 x goals` (CSV); gzip 8-15x. Cost across grids is the two-point power law `cells^0.8297` - the
fine grids get more out of each cell than the coarse ones, which is why G12 is 589x G384 and not 1024x.
Everything outside the measured anchors is labelled `EXTRAPOLATED`.

Verdicts, from the decisions file: **confirm** above 1 h, above 100 M seeds, above 1 GB of output, above
10 % of free space, or on `--all`; **refuse** when the output cannot fit (1 GiB held back) or the peak
memory cannot fit. A query whose hit rate is at or above 50 % is flagged as one that is not filtering.

Live re-calibration is the point: before the first slice the range is an **assumed** +/-25 % band
around this build's measurements; after two slices it is the **observed** spread of the real slices, and
the rate, the hit rate and the real record size all come from the run itself. `Reproject(..., seedsDone)`
projects the remainder only.

## 6. The startup self-test - `SeedLab.Runtime.SelfTest`

```csharp
MachineSelfTest st = new MachineSelfTest(cache);
st.Register(generatorSuite);                 // the host supplies the generator goldens
SelfTestOutcome outcome = st.Verify(hw);
outcome.EnsureUsable();                      // fails closed
```

The built-in suite is 271 recorded values embedded in the assembly: `Math.Sin/Cos/Tan/Atan/Atan2/Pow/
Sqrt/Exp/Log/Floor/Ceiling/Round` at the arguments world generation actually uses, plus float and double
evaluation cases - double-to-float rounding at a tie, `1/3f`, and three folded chains of thousands of
operations whose single result cannot hide a divergent bit. Two cases are built so that a **contracted
FMA** produces a different value, because that is the divergence that would silently move a biome
boundary. The cases live in code (`NumericCases`); only their results are recorded, so a vector file can
never drift from what is computed, and a file that stops covering a case is itself a failure.

`Verify` returns `Passed`, `PassedCached`, `Unproven`, `Failed` or `Skipped`, and `EnsureUsable()` throws
on anything but a pass or an explicit skip. A pass is stamped in `selftest/` under a fingerprint of
platform + architecture + runtime + ISA + vector hash + registered suites + assembly version, so it runs
once per machine and re-runs by construction when any of that changes.

**The unproven case matters most.** SeedLab's gates have only ever been run on x64. On any other
architecture a numeric pass proves libm and float evaluation and *nothing about the generator*, so with
no generator-level suite registered the outcome is `Unproven` and fails closed, naming the three ways
out: run the full gates here and report, run the query on x64, or pass `--accept-unverified-platform` in
the knowledge that the bit-exact claim is untested on this machine.

Re-recording the vectors (only ever on a machine that has just passed the full gates):

```
dotnet run --project tests\SeedLab.Runtime.Tests -c Release -- --emit-vectors --out src\SeedLab.Runtime\SelfTest\selftest-vectors.txt
```

---

## What the CLI and the web UI have to call

This project deliberately does not touch either of them. The list of calls they must make is in the
handover section of the task report; in short: `RuntimeContext.Start` once per command,
`RequireVerifiedMachine()` before any answer a user would act on, `PlanWorkers` instead of
`Environment.ProcessorCount` (11 sites), `Estimator.Project` before every run with
`Calibration.ObserveSlice` after every slice, and `CacheRoot` for checkpoints, maps and tiles instead of
the current working directory.

---

Credits: see the root README - created and tested by DoomMachine; code, tests and docs written by Claude (Anthropic) in Claude Code.
