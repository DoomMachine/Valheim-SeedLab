# How do I find a seed?

From nothing installed, to a search, to reading what came back, to standing on the map — and an
honest answer to "can I just scan all four billion?".

Nothing here goes into Valheim. SeedLab is a command-line program and a local web page; the game
never knows it ran.

---

## 0. Build it once (about two minutes)

You need the **.NET 10 SDK**. There are **no NuGet packages** — it builds offline.

```powershell
cd E:\SteamLibrary\steamapps\common\Valheim\_ModSource\SeedLab
dotnet build src\SeedLab.Cli\SeedLab.Cli.csproj -c Release
Set-Alias vseed E:\SteamLibrary\steamapps\common\Valheim\_ModSource\SeedLab\src\SeedLab.Cli\bin\Release\net10.0\vseed.exe
```

Run it **from the SeedLab folder** (or set `SEEDLAB_DATA_DIR` to `data\1.0.15-59f53fb5\`), otherwise
the location commands fail closed and say so.

Check it against the game before trusting a single number:

```
vseed selftest --quick        # about 4 s
vseed data                    # does the shipped game data match your install?
```

The first run on a new machine also checks this build's arithmetic against **263,780 values the game
itself produced**, and refuses to answer at all if one of them differs. That takes milliseconds and
then caches a stamp.

## 1. Pick a question

```
vseed presets list
```

Thirteen shipped queries, each with a status column. `biomes only` means it runs off the sampling
grid — thousands of seeds a second. `locations N/183` means the query needs the game's location
placement, which costs about **1.9 s per seed per core** whatever N is.

A preset is also a worked example of the query language:

```
vseed presets show gentle-start > mine.json
```

Open it. Every goal has a `target`, a `metric`, a `test`, a `value` and an `importance`
(`must` filters, `nice` ranks), and the shipped presets carry comments saying what was measured to
choose each threshold.

## 2. Run the first search

Start small. This is a real run from 2026-09-24:

```
vseed search gentle-start --seeds 400 --keep 10 --out first.jsonl --yes
```

Before it scans anything, it tells you what it is about to do — the grid, the region, the tier, the
coverage, the cap on the output file, how the seeds are cut into blocks, and where the checkpoint
will live:

```
  keep         the best 10 - a REAL cap on the RECORD COUNT: the file holds at most 10 records
               whatever the scan finds, and the true match count is reported beside it. ...
  grid         screen at G24 with a 1 % margin, then re-measure every survivor at G12
  coverage     9.313e-06 % of all 4,294,967,296 worlds
  threads      8
  block size   12, sized automatically for this run: the default 256 would cut these 400 seeds
               into 2 blocks, and one worker computes a whole block, so 6 of the 8 workers would
               have nothing to do; at 12 per block it is 34 blocks and the busiest worker
               computes 60 seeds
```

and afterwards:

```
Result
  seeds evaluated       400 of 400 planned
  matches               82  (20.500 %)
  wall clock            44.9 s
  measured rate         8.9 seeds/s on 8 threads
  results file          ...\first.jsonl  (10 records)
  what was kept         top 10 of 82 matches; 72 were not kept, from the point where the cut line
                        was 0.8231 (it ended at 1)
  checkpoint            retired - the run completed, so ...\53f7a88a9728fd2b.ckpt was deleted

Best 10 of 82
  seed         text     score  blackforest-close
  -1957974196  X5k576L  1.000           1.83 km2
  -1934870084  YMPr9A   1.000           1.96 km2
  ...
```

**Two things to take from that.**

*`--keep` is a cap on the file, not on the count.* Ten records were written; eighty-two seeds
matched, and the run says both. A whole-space run with the default `--keep 1000` still writes about
200 KB.

*Block size decides how many workers actually work — and it is sized for you.* One worker computes
a whole block. Until 2026-09-24 the block was a fixed 256, so this same run was **2 blocks of 256**
and only two of the eight workers had anything to do: it took 3.4 min at 2.0 seeds/s. The size is
automatic now — **34 blocks of 12** here, and the plan says why — so the same 400 seeds took 44.9 s
at 8.9 seeds/s and found the same 82 matches: the block size changes how long a run takes, never
what it finds. A rate from a run where not every worker had work says so, as
`(2 of 8 workers had work)`.

```
vseed search gentle-start --seeds 4000 --keep 20 --out hits.jsonl --yes
```

At 4,000 seeds the automatic size is 125, and the plan warns that ONE worker computes each block of
a T3 query — here about two minutes of its time (125 seeds at the ~0.9 s a seed each worker took
above), which is what a kill can cost however often the checkpoint is written. Add
`--block-size 16` (or `4` when the query places locations) if you want a finer resume point; a size
you give is kept.

## 3. Read what came back

`hits.jsonl` is one JSON object per line. Per seed you get the score, whether it passed, the grid it
was measured on, and then **per goal**: the measured value, the threshold, pass/fail, the grid *that
goal* was decided at, and — when the metric is one of the eight that cannot be compared across grids
— `grid_comparable: false` with a sentence saying why.

```json
{"seed":-1957974196,"text":"X5k576L","score":1,"pass":true,"grid":12,
 "goals":{"big-home":{"value":4731696,"unit":"m2","threshold":3000000,"pass":true,
                      "importance":"must","measured_at_grid_m":12,"grid_comparable":false,
                      "grid_note":"the worst-behaved metric in the tool at a coarse grid: ..."}}}
```

To see *why* one seed passed or failed, ask:

```
vseed explain -1957974196 gentle-start
```

which re-measures that seed with no prefilter and no region restriction and prints the goals one by
one, with the verdict, the margin and the score each contributed.

## 4. Look at the world

```
vseed seed X5k576L                 # land, biomes, islands, spawn, boss altars and traders
vseed locations X5k576L            # every instance, with the unpredictable ones flagged
vseed map X5k576L --px 4096 --rivers
vseed at X5k576L 120 -340          # the generator's answer at one exact point
```

`vseed map` with no `-o` writes into the cache root and prints the path — it does **not** drop a
3 MB PNG into whatever folder you were standing in.

Or click around:

```
vseed serve
```

A pan-and-zoom map on `http://127.0.0.1:8731` with the seed panel, a click-anywhere point panel, a
ruler, location markers and the same search engine behind a form. Bound to loopback only; the page
is four files embedded in `vseed.exe`; no CDN, no web font, no request ever leaves the machine.

## 5. Take it into the game

The **text** column is what you type into Valheim's seed box. The game hashes that text to an int32
and never looks at the text again — so any text that hashes to the same int gives the same world:

```
vseed invert -1957974196 --alphabet game --length 10 --count 3
```

Every text SeedLab prints has been re-hashed and checked before printing. The seed field allows 10
alphanumeric characters, and **7 characters are enough to reach every one of the 4,294,967,296
worlds**, so anything you find here can always be typed back in.

## 6. What it costs, honestly

Measured on 2026-09-23 on an 8-core / 16-thread Ryzen 9800X3D, at the shipped default
`--mode balanced` (8 workers). Full detail, provenance and the `--mode full` comparison are in
[`docs\measurements.md`](measurements.md).

| what you are asking for | seeds/s | seeds/hour | the whole 4.29-billion space |
|---|---|---|---|
| one biome question inside a 1 km disc | 2,046 | 7.37 M | **24 days** |
| `mountain-home` (biome + peak, screened at G24) | 1,736 | 6.25 M | **29 days** |
| `coastal-builder` (shore, 1.1 km disc) | 19.5 | 70 k | **7.0 years** |
| `balanced-biomes` (nine biomes, whole world, G192) | 38.7 | 139 k | **3.5 years** |
| `gentle-start` (spawn island, whole world) | 7.3 | 26 k | **18.6 years** |
| `all-traders` (any location goal) | 5.7 | 21 k | **23.9 years** |
| `archipelago` (island count at 12 m) | 5.6 | 20 k | **24.3 years** |

**The sentence that matters most: a biome question inside a disc can sweep the whole 4,294,967,296
worlds in weeks. A question about terrain height, islands, shore or a location cannot — those are
years.**

So the way to search for something expensive is not to wait. It is to **filter the whole space with
something cheap and then re-check the survivors with something expensive**:

```
vseed search cheap.json --all --keep 5000 --out survivors.jsonl --yes     # weeks, bounded output
vseed explain <seed> expensive.json                                       # per survivor
```

and the largest single lever on cost is **`search.region`** (or a `radius` on the goal): one biome
goal at the game's own 12 m grid costs 7.90 ms/seed inside a 1 km disc against 314.40 ms/seed over
the whole world — 39.8× — and the disc answers a disc-bounded goal *exactly*.

## 7. When it refuses — and what to do

A refusal always names the fix. These are real, from this build:

**"every goal in this query is a must-have"** — there is nothing to rank by, so the best-N file
would be an arbitrary sample wearing the name "top 1,000". Fix: make one goal `nice`, add a
nice-to-have to rank by, pass `--accept-scan-order`, or `--keep all`.

**"keep: all on a query with no must-have goal matches every seed, so it WILL write about 818 GB and
the volume has 599 GB free"** — exit 1, nothing scanned, nothing written. Fix: `--keep N`, add a
must-have, or point `--out` at a bigger volume.

**"goal 'eikthyr-close' … does not exclude any seed: every instance that exists is already inside
1,000 m of the centre"** — the goal is not a distance filter, it is a presence test. Fix: ask for
something inside the range that actually varies, or `--allow-vacuous` to scan anyway.

**"'coastline_length' was removed from the metric catalogue on 2026-09-23"** — with the measurement
that killed it and a worked example of `world:shore_area_within`, which replaced it.

And a run that is merely expensive or surprising **asks** instead of refusing:

```
This run needs confirming:
  - this is the WHOLE 4,294,967,296-seed space

  Nothing is reading the keyboard (stdin is redirected), so this run is not started.
  Pass --yes to accept the points above, or change the query.
  --dry-run answers "what would this cost" without starting it.
```

## 8. Housekeeping

Everything SeedLab writes by itself lives in **one place**: the cache root
(`%LOCALAPPDATA%\SeedLab`, or `--cache-dir`, or `$SEEDLAB_CACHE_DIR`) — checkpoints, rendered maps,
web tiles, run manifests, scratch and the self-test stamp. The only thing it writes outside that is
the results file you named.

```
vseed clean                       # a report: what is there, per category, and the free space
vseed clean --yes                 # remove the caches (not your checkpoints)
vseed clean --what checkpoints --yes
```

`vseed clean` also checks `%USERPROFILE%\AppData\valheim-dumper` — the dumper plugin's raw output —
against the copy in `data\`, and tells you when it is redundant. It never deletes that folder: it is
outside the cache root and it is the original the shipped copy came from.

Deleting the whole cache root at any moment loses nothing you asked to keep, except a search you
were part-way through resuming.

## 9. Stopping and continuing

Ctrl-C stops at the next block boundary, writes a checkpoint and exits 0. `--resume` continues from
it, and a completed run deletes its own checkpoint. A hard kill costs at most one block, and the
resumed file is byte-identical to an uninterrupted run's — checked by killing runs at three points,
bounded and streaming, and comparing SHA-256.

---

**Further reading:** [`search.md`](search.md) for the query language and the engine,
[`measurements.md`](measurements.md) for every cost number and where it came from,
[`limits.md`](limits.md) for what the tool cannot tell you and what it knows only within a bound.
