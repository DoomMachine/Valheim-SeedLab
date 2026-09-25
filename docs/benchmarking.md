# Benchmarking SeedLab: where the time, the memory and the disk go

Three tools answer three questions:

| question | tool | how long it takes |
|---|---|---|
| Where does one seed's time go, and what does each step use - processor, memory, disk - with every core busy? | `vseed profile --saturate` | what you ask for, plus a short pilot per measurement |
| What does each stage of a real search cost, from start to finish? | `tests\bench-search.ps1` | 12 to 15 minutes (`-Quick`: about 2) |
| Did a change to the river code change any value at all? | `river-golden` in `tests\SeedLab.Tests` | about 20 s per 64 worlds |

Build first: `dotnet build src\SeedLab.Cli -c Release` (and `dotnet build tests\SeedLab.Tests -c Release`
for the golden).

**A timing from a busy machine is not a measurement.** Close the game, other SeedLab windows and any
build before you start. Both timing tools watch the machine and mark a run **TAINTED**, naming what they
saw (another `vseed`, Valheim, a `dotnet` build, or other programs using more than one core). A tainted
run still shows that the tools work; its numbers are not figures to quote.
[`measurements.md`](measurements.md) stays the one place for official cost figures.

---

## 1. One seed, every core: `vseed profile --saturate`

```
vseed profile --tier t4 --threads 1,8,max --saturate 30 --out before.json
```

- `--tier t4` is the lake, river and stream pre-generation alone. `t3` adds heights, `t2` is biomes
  only, `t5` places locations. Leave `--tier` out for the whole fixed battery.
- `--threads 1,8,max` measures with 1 worker, then 8, then every logical core (`max`).
- `--saturate 30` makes each measurement long enough to keep all its workers busy for about 30
  seconds. A short pilot, not counted, measures how fast seeds go first; the count is then at least 32
  seeds per worker. The plan and the estimated total time are printed **before** anything is measured.
- `--out before.json` keeps everything. Later, `vseed profile --plan before.json --out after.json`
  measures **exactly the same worlds** with the same worker counts, so a before/after comparison is
  fair. It refuses to run if the seed order has changed.
- The machine is watched for 30 seconds before measuring (`--quiet-baseline 0` skips that for a quick
  check).

### Reading one measurement

Under each measurement's phase table:

```
  cpu        22.9 s of processor time (0.7 s in the kernel) over 5.4 s = 4.2 of 16 cores busy (26.5 %)
  cpu split  workers 18.2 s, everything else 4.7 s (garbage collection threads, JIT, runtime);
             the workers were off a processor 3.3 s of their 21.4 s (waiting for a collection, ...)
  ram        working set peak 387.6 MiB (mean 294.4 MiB), private 385.8 MiB, GC heap 202.7 MiB,
             GC committed 340.1 MiB; 27 samples every 200 ms
  gc heap    allocated 6.14 GiB (49.1 MiB per seed); last collection #205 (gen2): heap 38.0 MiB, ...
  disk       read 0 B, wrote 0 B in the window
  steady     all 4 worker(s) busy for 5.29 s of 5.39 s: 23.88 seeds/s there, 23.76 over the whole
             section (start 0.00 s, tail 0.09 s, 0.5 % lost to them)
```

(From a tainted smoke run on 4 workers - the shape, not the numbers.)

- **cpu** - processor time the whole program used, and how many of the machine's cores that kept busy
  on average. 100 % means every core was working all the time.
- **cpu split** - the workers' own processor time, measured on each worker thread, against everything
  else. With vseed's garbage collector (the part of .NET that frees memory no longer in use), "everything
  else" is mostly the collector. "Off a processor" is time a worker wanted to work but could not: mostly
  waiting while the collector ran.
- **ram** - memory, sampled five times a second without disturbing the workers. *Working set* is the
  memory the program has in RAM; *private* is what belongs to it alone; *GC heap* is what the collector
  is holding; *GC committed* is what it has reserved from Windows.
- **gc heap** - how much was allocated (asked for and later thrown away), per seed, and what the last
  collection found. Allocation is what makes the collector run: less allocation, less time waiting.
- **disk** - bytes the program read and wrote while measuring. The profile writes nothing while it
  measures; `--out` is written once at the end.
- **steady** - the stretch in which **every** worker was busy, and its rate. The difference from the
  whole measurement's rate is what starting up and the last few seeds (the "tail") cost. A big
  difference means the measurement was too short: raise `--saturate`.

The same figures, and more (every worker's own time, each generation of the collector's heap, the
collector's own settings), are in the JSON file under each section's `cpu`, `memory`, `io`,
`steady_state` and `gc.last_gc`.

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

Everything goes into one new folder under your temporary folder (it prints where): the results files,
everything vseed printed with the time each line arrived, each run's session log, and the report as
`bench-search.txt` and `bench-search.json`. Nothing is written anywhere else. Delete the folder when you
have read the report.

### Reading a query's table

```
Q5  bench-funnel.json - a T3 must-have funnelling into a T5 must-have
    512 seeds, 16 threads, exit 0, 36.2 s, funnel
    stage                              wall s    CPU s   cores  util %   peak WS ...
    start-up                             0.10 0.1 (0.0)     0.9     5.6      72.5
    preflight                            0.38 0.4 (0.1)     1.1     6.8      72.5
    stage 1                              9.97 128.4 (1.6)    12.9    80.5     994.5
    survivor list + gate (1 thread)     11.94 12.3 (0.1)     1.0     6.4     994.5
    stage 2                             13.45 179.4 (1.9)    13.3    83.4    2907.4
    ...
```

(A tainted `-Quick` smoke run - the shape, not the numbers.)

- **wall s** - seconds on the clock. This is what you wait for.
- **CPU s (kernel)** - processor seconds, all cores together; the part in brackets was spent inside
  Windows (mostly handing memory to the program and taking it back).
- **cores busy** and **util %** - how many cores the stage kept busy on average, and that as a share of
  all of them. A stage at 1.0 core on a 16-core machine is running on one thread while 15 wait.
- **peak WS / peak priv** - the most memory the stage held (working set, private).
- **written / read** - bytes the program wrote and read in the stage: files, but also its own screen
  output. The files it left are listed below the table with their sizes.
- **faults** - page faults, in thousands: how often Windows had to hand the program memory. Many faults
  together with high kernel time point at memory churn.

Below the table: the **steady** part of each scan (the longest stretch at 85 % or more of its usual
number of busy cores) and its seed rate, the most disk the run used at once, the files it left, and
vseed's own `Result` lines. For Q5 and Q6 it also checks that both results files are identical, as they
must be.

**Stages.** *start-up* is the program starting and checking the machine; *preflight* reads the query and
the game data and plans the run; *plan* prints it; *open the run* creates the results file and starts the
workers; *scan* is the search itself; *finish writing* completes the results file; *report and exit*
prints the report and ends. A funnel has *stage 1* (the cheap goals over all seeds), *survivor list +
gate* (vseed saves the seeds that passed and times a few location placements, **on one thread**), and
*stage 2* (the expensive goals on the survivors only).

---

## 3. The river-points golden

Before changing anything in the lake, river or stream code, record what it produces; after the change,
check that nothing moved:

```
dotnet run -c Release --project tests\SeedLab.Tests -- river-golden --write %TEMP%\rivers.bin --seeds 64
... make the change, rebuild ...
dotnet run -c Release --project tests\SeedLab.Tests -- river-golden --check %TEMP%\rivers.bin
```

It records, for the first 64 seeds of the profile's seed order, every lake, river, stream and river point
bit for bit, **in the order the program lists them**, plus heights and river strengths at a few hundred
places asked five different ways (the ways a search, the web map and the location placement reach the
same data). About 2 MiB per seed. `--check` names the first difference it finds - the seed, the river
cell, the point and the field - and says so separately when only the order of the cells changed.
`river-golden --self-test` proves the golden itself works (it changes a copy in memory and checks the
change is found where it was made).
