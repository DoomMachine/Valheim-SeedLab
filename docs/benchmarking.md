# Benchmarking SeedLab: where the time, the memory and the disk go

Three tools answer three questions:

| question | tool | how long it takes |
|---|---|---|
| Where does one seed's time go, and what does each step use - processor, memory, disk - with every core busy? | `vseed profile --saturate` | about the seconds you ask for x the number of measurements, plus a pilot and a warm-up each (see below) |
| What does each stage of a real search cost, from start to finish? | `tests\bench-search.ps1` | about 20 minutes (`-Quick`: about 3), projected |
| Did a change to the river code change any value at all? | `river-golden` in `tests\SeedLab.Tests` | about half a minute per 64 worlds to write, the same to check |

Build first: `dotnet build tests\SeedLab.Tests -c Release` (for the golden), then
`dotnet build src\SeedLab.Cli -c Release` **last**: the test project builds its own copy of the search
library, and `tests\SeedLab.Search.Tests` refuses a `vseed` whose copy differs from its own.

**The search benchmark and the location sections need the game data** (`data\`, made by the dumper -
[`data.md`](data.md)). `vseed search` refuses every search without it, so on a copy of SeedLab without
`data\` each `bench-search.ps1` query is refused at its dry run and skipped. `vseed profile` refuses a run
that includes a location section (`t5`, part of the default battery of 7); `--tier t2`, `t3` and `t4` and
the river golden need only the seeds.

**A timing from a busy machine is not a measurement.** Close the game, other SeedLab windows and any
build before you start. Both timing tools watch the machine and mark a run **TAINTED**, naming what they
saw (another `vseed`, Valheim, a `dotnet` build, or other programs using more than one core). A tainted
run still shows that the tools work; its numbers are not figures to quote.
[`measurements.md`](measurements.md) stays the one place for official cost figures.

---

## 1. One seed, every core: `vseed profile --saturate`

A before/after comparison of a change takes two commands, one on each build:

```
vseed profile --tier t4 --threads 1,8,max --saturate 30 --out before.json      (the build before the change)
vseed profile --plan before.json --out after.json                               (the build after it)
```

- `--tier t4` is the lake, river and stream pre-generation alone. `t3` adds heights, `t2` is biomes
  only, `t5` places locations. Leave `--tier` out for the whole fixed battery of 7 sections.
- `--threads 1,8,max` measures with 1 worker, then 8, then every logical core (`max`).
- `--saturate 30` makes each measurement long enough to keep all its workers busy for about 30
  seconds. A pilot, not counted, first works for 10 % of that (between 0.5 and 3 seconds) and times the
  later half of its seeds; the count is then at least 32 seeds per worker.
- `--plan before.json` measures **exactly the same worlds** with the same worker counts, and first runs
  the same pilots again (not counted), so both profiles start measuring from the same point - the same
  memory already in use, the same code already optimised by the runtime. It refuses to run if the seed
  list would differ, if the plan was measured with the other `--counters` setting, or if its location
  sections (`t5`) used other game data. Anything else that differs - the build above all, which is the
  point, but also the garbage collector's settings or the machine - is printed before measuring and kept
  in `after.json` under `run.plan.differences`.
- The machine is watched for 30 seconds before measuring (`--quiet-baseline 0` skips that for a quick
  check).

**How long it takes.** A *measurement* is one section at one worker count, so the command above makes 3
measurements (one section, three worker counts) and the whole battery with `--threads 1,8,max` makes
21. The time is about `--saturate` x measurements - 21 x 30 s is 10.5 minutes - plus the 30 s watch and a
pilot and a warm-up per measurement. For slow sections the 32-seeds-per-worker minimum decides instead:
placing all 183 location entries (`t5 prefix 183`) at 16 workers takes about 5 minutes per measurement
whatever `--saturate` says. `vseed profile` prints a lower bound before the watch, the full plan (every
measurement's seed count and time) after the pilots, and each measurement's expected time as it starts.
A `--plan` replay prints how long the plan's measurements and pilots took when they were recorded.

### Reading one measurement

Under each measurement's phase table:

```
  cpu        28.9 s of processor time (0.8 s in the kernel) over 7.5 s = 3.8 of 16 cores busy (23.9 %)
  cpu split  workers 19.9 s, everything else 9.0 s (the garbage collector's threads, the JIT compiling code, the runtime)
  off cpu    the workers were off a processor 10.2 s of their 30.1 s (waiting for a collection, preempted, page faults)
  ram        working set peak 331.8 MiB (mean 270.4 MiB), private 329.7 MiB, GC heap 222.3 MiB,
             GC committed 278.6 MiB; 37 samples every 200 ms
  ram peak   working set 366.6 MiB, private 366.6 MiB (exact, from Windows' own peak counters; ...)
  gc heap    allocated 6.14 GiB (49.1 MiB per seed); last collection #195 (gen0): heap 129.2 MiB, ...;
             up to 16 collector heaps (the configured maximum; how many were in use is not measured)
  disk       read 0 B, wrote 0 B in the window
  steady     all 4 worker(s) busy for 7.49 s of 7.54 s: 17.02 seeds/s there, 16.97 over the whole
             section (start 0.00 s, tail 0.05 s, 0.3 % lost to them)
  sizing     pilot 8 seeds, 266.07 ms each (its later half) -> 15.0 seeds/s -> 128 seeds, estimated
             8.5 s (it took 7.5 s); the 32-per-worker minimum decided, not the 3 s asked
```

(From a tainted smoke run on 4 workers, with the game running - the shape, not the numbers.)

- **cpu** - processor time the whole program used, and how many of the machine's cores that kept busy
  on average. 100 % means every core was working all the time. *In the kernel* means inside Windows
  itself (mostly handing memory to the program and taking it back).
- **cpu split** - the workers' own processor time, measured on each worker thread, against everything
  else. With vseed's garbage collector (the part of .NET that frees memory no longer in use), "everything
  else" is mostly the collector. The *JIT* is the runtime compiling the program's code as it first runs.
- **off cpu** - time a worker wanted to work but could not: mostly waiting while the collector ran.
- **ram** - memory, sampled five times a second without disturbing the workers. *Working set* is the
  memory the program has in RAM; *private* is what belongs to it alone; *GC heap* is what the collector
  is holding; *GC committed* is the memory the collector has taken from Windows, as of its last collection.
- **ram peak** - the highest working set and private memory inside the measurement, exactly, from
  Windows' own counters (they only ever rise, so they are read at both ends). "Not above its earlier
  peak" means the measurement stayed below what an earlier part of the run (often a pilot) had reached.
- **gc heap** - how much was allocated (asked for and later thrown away), per seed, and what the last
  collection found. Allocation is what makes the collector run: less allocation, less time waiting.
  The collector sorts objects by age into *gen0*, *gen1* and *gen2* (young to old); *large objects* are
  those of 85,000 bytes or more, kept apart. The number of collector heaps is only the configured
  maximum: the collector adapts how many it uses, and that number is not measured.
- **disk** - bytes the program read and wrote while measuring. The profile writes nothing while it
  measures; `--out` is written once at the end.
- **steady** - the stretch in which **every** worker was busy, and its rate. The difference from the
  whole measurement's rate is what starting up and the last few seeds (the "tail") cost. A big
  difference means the measurement was too short: raise `--saturate`.
- **sizing** - how the seed count was chosen, what it was expected to take and what it took.

The same figures, and more (every worker's own time, each generation of the collector's heap, the
collector's own settings, the profiler's own per-seed table), are in the JSON file under each section's
`cpu`, `memory`, `io`, `steady_state` and `gc`. Comparisons between worker counts (hypothesis H10)
name the seed counts when a saturated run gave them different ones.

---

## 2. A real search, stage by stage: `tests\bench-search.ps1`

```
powershell -ExecutionPolicy Bypass -File tests\bench-search.ps1
powershell -ExecutionPolicy Bypass -File tests\bench-search.ps1 -Queries Q3,Q5 -Quick
```

It runs real `vseed search` commands at `--mode full` (every core) with fixed seed counts:

| | query | what it exercises | seeds (`-Quick`) |
|---|---|---|---|
| Q1 | preset `custom` | one biome goal, the cheapest tier; every seed matches | 409,600 (16,384) |
| Q2 | preset `gentle-start` | heights and a coarse screen | 1,280 (128) |
| Q3 | `bench-rivers` | rivers only: almost all pre-generation | 6,144 (384) |
| Q4 | preset `compact-progression` | five bosses placed | 1,024 (64) |
| Q5 | `bench-funnel` | a height goal that narrows the seeds before locations are placed (a *funnel*) | 4,096 (512) |
| Q6 | `bench-funnel`, `--strategy sample` | the same seeds without the funnel; its results must equal Q5's | 4,096 (512) |
| Q7 | `custom --keep all --rotate 32MB --compress gz` | every match written, split and compressed (not in the default set) | 409,600 (16,384) |

The whole default set takes about 20 minutes on an 8-core, 16-thread machine, Q6 alone about half of
that; `-Quick` about 3 minutes (projected from runs on a busy machine; not yet timed on a quiet one). Each query first has an untimed dry run (which also catches a query
vseed would refuse); after those the script prints vseed's own estimate for the timed runs - a lower
bound, as vseed's quick calibration runs ahead of a real run.

It will not start while Valheim is running (vseed would then use only about a quarter of the cores, so
every run would be about four times slower and measure nothing useful; `-AllowGame` overrides that, to
test the script itself), nor in a window running as administrator.

**To stop it**, press Ctrl+C: within half a second the script stops and ends the `vseed` it started.
Closing the window works too - Windows then ends that `vseed` as well. `-TimeoutMinutes` (default 60)
stops any one query that runs longer.

Everything goes into one new folder under your temporary folder (it prints where): the results files,
everything vseed printed with the time each line arrived, each run's session log, and the report as
`bench-search.txt` and `bench-search.json`. Nothing is written anywhere else. Delete the folder when you
have read the report.

### Reading a query's table

```
Q3  bench-rivers.json - rivers only: nearly all pre-generation (T4)
    384 seeds, 4 threads, exit 0, 19.7 s  TAINTED: ...; vseed throttled itself: ... [auto-throttled by valheim]
    stage                          wall s         CPU s   cores  util %   peak WS peak priv   written      read    faults
                                             (kernel s)    busy               MiB       MiB       KiB       KiB  thousand
    start-up                         0.12     0.2 (0.0)    1.3~    8.4~     61.3~     46.9~         6     1,228         8
    preflight                        0.08     0.1 (0.0)    1.4~    8.5~     61.3~     46.9~         4       874         5
    plan                             0.00     0.0 (0.0)    1.4~    8.5~     61.3~     46.9~         0        40         0
    open the run                     0.01     0.0 (0.0)    1.4~    8.5~     61.3~     46.9~         1       112         1
    scan                            19.44    77.5 (2.4)     4.0    24.9     370.9     369.0        39       460     1,398
    finish writing                   0.00     0.0 (0.0)    2.0~   12.7~    277.8~    273.2~         1         0         0
    report and exit                  0.02     0.0 (0.0)    2.0~   12.7~    277.8~    273.2~         5         0         1
    whole run                       19.67    77.8 (2.5)     4.0    24.7     370.9     369.0        56     2,714     1,413
```

(A tainted `-Quick` smoke run with the game running, so vseed used 4 of the 16 threads - the shape, not
the numbers.)

- **wall s** - seconds on the clock. This is what you wait for.
- **CPU s (kernel s)** - processor seconds, all cores together; the part in brackets was spent inside
  Windows (mostly handing memory to the program and taking it back).
- **cores busy** and **util %** - how many cores the stage kept busy on average, and that as a share of
  all of them. A stage at 1.0 core on a 16-core machine is running on one thread while 15 wait.
- **peak WS / peak priv** - the most memory the stage held (working set, private), in MiB.
- **written / read** - what the program wrote and read in the stage, in KiB: files, but also its own
  screen output. The files it left are listed below the table with their sizes.
- **faults** - page faults, in thousands: how often Windows had to hand the program memory. Many faults
  together with high kernel time point at memory churn.
- A value marked **~** belongs to a stage shorter than two samples (250 ms apart): it is estimated from
  the samples on either side, not measured inside the stage.

Below the table: the **steady** part of each scan (the longest stretch at 85 % or more of its usual
number of busy cores) with its seed rate - the slope of vseed's progress lines inside that stretch,
which is not thrown off by the blocks still being worked on - the most disk the run used at once, the
files it left, and vseed's own `Result` lines. For Q5 and Q6 it also checks that both results files are
identical, as they must be.

**Stages.** *start-up* is the program starting and checking the machine; *preflight* reads the query and
the game data and plans the run; *plan* prints it; *open the run* creates the results file and starts the
workers; *scan* is the search itself; *finish writing* completes the results file; *report and exit*
prints the report and ends. A funnel has *stage 1* (the cheap goals over all seeds), *survivor list +
gate* (vseed saves the seeds that passed and times a few location placements - its busy-cores column
shows how many threads that used), and *stage 2* (the expensive goals on the survivors only).

---

## 3. The river-points golden

Before changing anything in the lake, river or stream code, record what it produces; after the change,
check that nothing moved. In a Command Prompt:

```
dotnet run -c Release --project tests\SeedLab.Tests -- river-golden --write %TEMP%\rivers.bin --seeds 64
... make the change, rebuild ...
dotnet run -c Release --project tests\SeedLab.Tests -- river-golden --check %TEMP%\rivers.bin
```

In PowerShell, write `$env:TEMP\rivers.bin` instead of `%TEMP%\rivers.bin`.

It records, for the first 64 seeds of the profile's seed order, every lake, river, stream and river point
bit for bit, **in the order the program lists them**, plus heights and river strengths at a few hundred
places asked five different ways (the ways a search, the web map and the location placement reach the
same data). Then, after two more worlds of the same seed and one of another seed have been generated on
the same thread, it reads all of that again: a change that hands one world's memory to the next - the
kind of saving the river code is a candidate for - shows up there. About 2 MiB per seed. `--check` names
the first difference it finds - the seed, the river cell, the point and the field - and says so
separately when only the order of the cells changed and everything else agrees. A damaged or cut-short
golden is reported as such (exit 2); a golden written by an older format of this tool is refused with
the reason, and has to be written again with the code from before the change.
`river-golden --self-test` proves the golden itself works (it changes a copy in memory and checks the
change is found where it was made).
