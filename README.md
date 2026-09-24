# SeedLab

**Valheim 1.0.15 world generation, offline and exact.** Type a seed and see the world without
launching the game; or scan all 4,294,967,296 of them for one that suits you, with no 20,000-seed cap.

Two things make it worth having rather than another seed viewer:

- **The terrain is not an approximation.** On two worlds the game itself generated and wrote to disk,
  every biome pixel matches and every height matches *as a binary16 bit pattern* — 4,194,304 of
  4,194,304 codes per world, 0 differing, worst difference 0 m. The location placement reproduces
  12,228 of 12,228 instances of a fresh world bit-identically, including the `x/y/z` float bits.
- **It never rounds an answer into a claim.** Every figure carries the grid it was measured on, a
  thing that cannot be predicted is labelled as unpredictable instead of being printed as a
  coordinate, and a search that could not answer a goal refuses rather than returning seeds it never
  tested.

Everything runs locally. Nothing is uploaded, and nothing in your save folders is ever written.

---

**In a hurry?** [`docs\finding-a-seed.md`](docs/finding-a-seed.md) is the one page to read: from
nothing installed, through a first search, to reading the result and opening it on the map.

## Contents

- [Cloned from GitHub? Two folders are not here](#cloned-from-github-two-folders-are-not-here)
- [What it can and cannot tell you](#what-it-can-and-cannot-tell-you)
- [Build it](#build-it)
- [The commands](#the-commands)
- [The web UI](#the-web-ui)
- [Searching, and what it really costs](#searching-and-what-it-really-costs)
- [Seeds: 853 quadrillion texts, 4.29 billion worlds](#seeds-853-quadrillion-texts-429-billion-worlds)
- [The game data, and what to do after a Valheim update](#the-game-data-and-what-to-do-after-a-valheim-update)
- [Where the evidence lives](#where-the-evidence-lives)
- [Layout](#layout)
- [Credits](#credits)

---

## Cloned from GitHub? Two folders are not here

This repository holds the source, the tests, the tools and the documentation. Two folders the rest of
this README mentions are kept on the author's machine on purpose:

- **`data\`** - the game data snapshot (`data\1.0.15-59f53fb5\`). It is read out of the running game
  by `tools\SeedLab.Dumper`, so it is Iron Gate's content - location tables, prefab constants, the
  game's own English text - and it is not redistributed. **Without it, terrain answers work** (biome,
  height, rivers, maps, seed arithmetic, terrain searches: they need only the seed), and **location
  answers refuse** with exit 3 and a message saying so, exactly as they do after a game update. To
  get it, run the dumper against your own copy of Valheim - `tools\SeedLab.Dumper\README.md`, "The
  short version" - and copy its output folder into `data\`.
- **`groundtruth\`** - two worlds the game generated on the author's machine, their map caches, a game
  log and the recorded native-function values. It is the evidence the gates compare against, so
  `vseed selftest`, `tests\SeedLab.Acceptance.Tests`, the location gate and the tile check in
  `vseed serve --selftest` say that it is missing rather than pass. It is **not** needed to run
  `vseed`: on x64 the built-in machine self-test is self-contained, and only another CPU architecture
  would need `groundtruth\natives` to prove itself.

Paths in the documentation such as `E:\SteamLibrary\steamapps\common\Valheim\_ModSource\SeedLab` are
where the project lives on the author's machine; SeedLab itself does not depend on them.

---

## What it can and cannot tell you

### Exact — bit-for-bit against what the game wrote

| | |
|---|---|
| Biome at any point | `GetBiome`, verified over ~5.1 M minimap pixels on two worlds, 0 mismatches |
| Terrain height at any point | verified as the stored binary16 code, 4,194,304/4,194,304 per world |
| Rivers, streams and lakes | the generator's own point lists, 2.1 M rendered river points in order, bit-exact on three seeds |
| The biome map | `vseed map --plain --palette game` is pixel-identical to the game's own texture |
| Where every location instance is | 12,228/12,228 on a fresh world; 12,314/12,314 and 12,287/12,287 against two played worlds' `.db2` |
| How many of each type got placed | the game's own 29 `placed N out of M` counters, all reproduced exactly |
| Seed text ↔ int32, both directions | every returned text is re-hashed before it is printed |

### True, but with a resolution attached

Anything counted over an area — biome share, land area, island counts, "nearest Swamp" — is counted
on a **sampling grid**, and the grid is part of the number. `vseed` prints it with every figure. A
count at 24 m is a *different measurement* from the same count at 12 m, not an approximation of it:
the raw island component count moves about 45× over a 16× change of spacing. Do not compare figures
across grids.

Distances to a biome are "the centre of the nearest cell of that biome", so they carry half a cell
diagonal of slack — 8.485 m on the game's own 12 m grid — and the output says so. When a distance is
at or below that floor the record says `censored` rather than printing a number that looks measured.

Eight metrics cannot be compared across grids at all; each carries `grid_comparable: false` and a
sentence saying why. The worst is `spawn_island_area`: median |relative error| against the game's
own grid is 14.0 % at 24 m and 70.8 % at 384 m, because coarse grids merge islands across straits.

### True, but bounded rather than proven

Some answers are statements about *all* seeds that only a sample supports, and SeedLab says which
kind it is holding. The seven boss altars never fell short in 5,000 uniformly drawn seeds, which
**bounds** the shortfall rate at ≤ 0.060 % (95 %, rule of three) and does **not** make the count
fixed — and `DN_Bossroom`, a boss altar in the same catalogue, *does* fall short, in 26 of 5,000
seeds (0.520 %, CI 0.355–0.761 %). Full list and the proofs behind it:
[`docs\limits.md`](docs/limits.md).

### Not predictable at all — by anyone, from the seed

- **Which candidate of a unique location survives.** Vendor_BlackForest has ten candidate positions.
  The game keeps exactly **one**, and which one depends on the first zone a player or a peer
  generates, not on the seed. SeedLab lists all ten and refuses to name one. The seed sites that show
  you "the" trader are showing you a candidate.
- **The rotation of anything.** `Quaternion.Euler(0, Random.Range(0,16)*22.5, 0)` is drawn from the
  ambient `UnityEngine.Random` stream at spawn time, which nothing seeds.
- **The interior of a dungeon whose generator sits off its location's axis**, because the unseeded
  rotation moves the generator and its position feeds its seed.
- **The ground a building finally rests on.** `gen y` is `WorldGenerator.GetHeight`, which is what the
  save stores; the real ground comes from the built heightmap and the location's own terrain edits.
- **Anything about a world that has already been played.** Generated zones are excluded from a re-run,
  placed instances are kept, and the game's own `genloc` is not deterministic either.

### Simply not modelled

Creatures and spawns, loot, ore under the ground, dungeon room contents, world modifiers' effect on
anything but the flags in the save, and anything a player did.

`vseed locations` prints the unpredictable list in full every time it runs. It is not a footnote.

**[`docs\limits.md`](docs/limits.md) is the long version of this section** — every grid-relative
metric with its measured error, the answers that are bounded rather than exact, the shortfall rates,
what a single untested architecture means and what the startup self-test does about it, and what is
simply not implemented (`--strategy funnel|sample`, the D4/D5 count rules, GPU).

---

## Build it

You need the **.NET 10 SDK**. `vseed serve` also needs the ASP.NET Core 10 shared runtime, which
ships in the same install. **There are no NuGet packages** — the whole thing builds offline.

```
cd E:\SteamLibrary\steamapps\common\Valheim\_ModSource\SeedLab
dotnet build src\SeedLab.Cli\SeedLab.Cli.csproj -c Release
```

The tool is `src\SeedLab.Cli\bin\Release\net10.0\vseed.exe`. Put it on your PATH or alias it:

```powershell
Set-Alias vseed E:\SteamLibrary\steamapps\common\Valheim\_ModSource\SeedLab\src\SeedLab.Cli\bin\Release\net10.0\vseed.exe
```

Run it from inside the SeedLab folder, or set `SEEDLAB_DATA_DIR` to `data\1.0.15-59f53fb5\` — that is
where the location table lives, and without it the location commands exit 3 and say so.

Check the build against the game before trusting it:

```
vseed selftest                    # full: every cell of both ground-truth worlds
vseed selftest --quick            # 1 cell in 16, about 4 s
```

**If a build fails with MSB3027 or MSB3021**, a `vseed serve` or `vseed search` is still running and
holding `bin\Release\`. Stop it and build again.

---

## The commands

```
vseed --help                 the command list
vseed <command> --help       one command's options
```

`--json` gives every data command a machine-readable form on stdout, with warnings on stderr, so it
pipes into `jq` cleanly. Exit codes: `0` ok, `1` a check failed, `2` bad command line, `3` not found,
`4` internal fault. Global options work on either side of the command name:

| | |
|---|---|
| `--mode background\|balanced\|full` | how much of the machine to use. **Default `balanced`** — about 50 % of the logical cores at Normal priority. `background` is ~25 % at BelowNormal, for while you play; `full` is every core, still capped by the memory guard. Every run prints the arithmetic: `workers 8 (balanced mode = 50 % of 16 logical cores -> 8; memory allowed 840)`. |
| `--threads <n>` | override the worker count (1..64). Still capped by free memory, and the cap is printed. |
| `--cache-dir <dir>` | where checkpoints, rendered maps, web tiles and the self-test stamp live. Default `%LOCALAPPDATA%\SeedLab`, or `$SEEDLAB_CACHE_DIR`. |
| `--ignore-running-game` | do not drop to `background` when Valheim is running. |
| `--skip-self-test` | do not check this machine against the recorded goldens. `seed`, `at`, `map`, `locations` and `search` then say `warning: the machine self-test was turned off: SeedLab's bit-exactness is UNVERIFIED on this run` (commands that do not build a world stay quiet). |
| `--accept-unverified-platform` | proceed on an architecture the gates have never run on (see [`docs\limits.md`](docs/limits.md)). |

**Auto-throttle.** If Valheim is running when a command starts, SeedLab drops to background mode by
itself and prints one line saying so and how to override it:

```
vseed: valheim is running - dropping to background mode (~25 % of cores, BelowNormal).
       Override with --mode full --ignore-running-game.
```

**The machine self-test.** On a cold cache, the first command that builds a world re-checks this
build's arithmetic against the corpora the game itself produced — 271 numerics checks and 263,780
recorded native values — and **fails closed**: one divergent value and the command exits 1, naming
the platform, the suite, the first failing case with both numbers, and what to do. Demonstrated by
altering one recorded hash by 1 in a copy of `groundtruth\natives`, which made `vseed seed 12345`
exit 1 with `seedlab/natives: 263779/263780 exact`. A pass writes a stamp in `<cache root>\selftest`
and costs nothing again.

**`vseed clean`** reports what SeedLab is holding on disk, per category, with the volume's free
space, and removes the caches with `--yes` (`--what checkpoints,maps,tiles,scratch,runs,selftest,all`).
It also compares the dumper's raw output folder against `data\` and tells you when it is redundant —
without ever deleting it.

### `vseed seed` — everything about one world

```
$ vseed seed MWd8eV6svz

Seed
----------------
  as typed              "MWd8eV6svz"  (seed text)
  int32                 -1772362158
  shortest text         Hi9L9a  (6 chars, alphanumeric)
  game-style text       5R3inNYZse  (10 chars, the 59 the game's own generator uses - ONE of the many texts for this seed)
  worldGenVersion       2

  The text is hashed to the int by World..ctor and never looked at again, so the int IS the world.

Measurement
-----------------------
  grid                  G12 (2048 x 2048 @ 12 m, the grid the game itself samples)
  cells in world        2,405,324 of 4,194,304   (DUtils.Length(x,z) <= 10500 m)
  area sampled          346.37 km2   (cell 144 m2)
  time                  5.48 s field, 1.98 s analysis, 16 threads

Land and water  (land = height >= 30.0 m, the game's water level)
-----------------------------------------------------------------------------
  land                  117.60 km2   33.95 %
  water                 228.76 km2   66.05 %

Biomes  (share of the sampled in-world area)
--------------------------------------------------------
  biome         area km2      %  land km2  nearest m  nearest land m
  ------------  --------  -----  --------  ---------  --------------
  Meadows          11.56   3.34      5.72          8              19
  Black Forest     44.34  12.80     19.38        508             508
  Swamp             9.02   2.60      3.04       2000            2000
  Mountain         11.39   3.29     11.18        628             628
  Plains           47.16  13.61     21.09       2902            2902
  Mistlands        57.98  16.74     32.02       5900            5900
  Ashlands         43.03  12.42      6.98       7909            8443
  Deep North       23.91   6.90     18.19       7915            8078
  Ocean            97.98  28.29      0.00        102               -

  'nearest' is the centre of the closest cell of that biome to (0,0), so it is within 8.5 m
  (half a cell diagonal) of the true nearest point.
```

It goes on to islands (with a per-seed measurement of how much of the count survives the binary16
precision the game's own map cache stores), the spawn area, the extremes, and:

```
Landmarks  (the game's own location placement, run for this seed)
-----------------------------------------------------------------------------
  game data             1.0.15, dumped 2026-09-23
  types run             23 of 183   (0.58 s biome grid, 0.09 s placement)
  spawn (StartTemple)   (71, -3)   this is Game.FindSpawnPoint's anchor, not (0, 0)
  name                prefab                         kind        count  nearest m  dir      at              biome         one position?
  ------------------  -----------------------------  ----------  -----  ---------  -------  --------------  ------------  -------------
  Eikthyr             Eikthyrnir                     boss altar      3        131  ESE 123  (110, -71)      Meadows       yes
  The Elder           GDKing                         boss altar      4       2757  S 176    (188, -2751)    Black Forest  yes
  Moder               Dragonqueen                    boss altar      3       3050  ENE 59   (2625, 1554)    Mountain      yes
  Bonemass            Bonemass                       boss altar      5       3087  NE 34    (1723, 2561)    Swamp         yes
  Yagluth             GoblinKing                     boss altar      4       5234  ENE 78   (5120, 1088)    Plains        yes
  The Queen           Mistlands_DvergrBossEntrance1  boss altar      5       6341  SE 137   (4288, -4672)   Mistlands     yes
  Fader               FaderLocation                  boss altar      3       9097  S 187    (-1152, -9024)  Ashlands      yes
  Kall Fimbulbringer  DN_Bossroom                    boss altar      3       9440  N 352    (-1344, 9344)   Deep North    yes
  Haldor              Vendor_BlackForest             trader         10       2516  NE 38    (1545, 1986)    Black Forest  1 of 10
  Hildir              Hildir_camp                    trader         10       3017  SSW 192  (-639, -2949)   Meadows       1 of 10
  The Bog Witch       BogWitch_Camp                  trader         10       3136  NNW 327  (-1726, 2618)   Swamp         1 of 10
```

Two columns carry the honesty. `name` is the game's own string for the place, from the dumped
localization table - a dash there means the dump names it nothing and the prefab is what it is
called, which is the case for 169 of the 183 placed types. `one position?` answers `1 of 10` for a
`m_unique` type, because the game keeps exactly one of those candidates and which one is not a
function of the seed.

Useful options: `--grid <m>`, `--islands <n>`, `--no-landmarks` (much faster), `--dungeons` (runs all
183 location types), `--json`.

### `vseed at` — one point, exactly

No grid, no snapping — the coordinates go straight into the generator.

```
$ vseed at MWd8eV6svz 0 0

Point  (0.00, 0.00)  in seed -1772362158
----------------------------------------------------
  biome                 Meadows   (Heightmap.BiomeIndex 1)
  height                23.619 m
  vs sea level          6.381 m below the 30 m water line (underwater)
  base height           0.08344   (normalised; x200 before biome shaping)
  river / stream        no  (weight 0)
  forest factor         1.0189   in forest (< 1.15)
  zone                  (0, 0)   64 m zone, centre (0, 0)
  from the centre       0.0 m, bearing 0.0 deg N
  geometry              inside the 10500 m water edge
```

`--ascii` draws a terrain sketch around the point.

### `vseed locations` — where things are

```
$ vseed locations MWd8eV6svz

Locations
---------------------
  as typed              "MWd8eV6svz"  (seed text)
  int32                 -1772362158
  game data             1.0.15, dumped 2026-09-23 (59f53fb55d99)
  selection             --type boss,trader  ->  11 of 183 location types
  names                 English, from the 1.0.15 dump
  placement run         23 of 183 types (0.74 s world, 0.12 s placement)
  
  Everything before the selection has to run too: one location per zone, globally, so an
  earlier type can take a later one's zone. The prefix is never shortened past that.

  Eikthyr  (Eikthyrnir)   [boss altar]
    3 shown of 3 placed, m_quantity 3, biome Meadows
     x     z  dist m  bearing        biome    gen y  zone
  ----  ----  ------  -------------  -------  -----  --------
   110   -71     131  122.7 deg ESE  Meadows   43.1  (2, -1)
  -597  -174     621  253.8 deg WSW  Meadows   33.1  (-9, -3)
  -779   428     889  298.8 deg WNW  Meadows   51.5  (-12, 7)

  Haldor  (Vendor_BlackForest)   [trader, m_unique]
    10 shown of 10 placed, m_quantity 10, biome Black Forest
    m_unique: the game keeps exactly ONE of these 10 candidates. Which one is NOT a
    function of the seed - the first candidate whose zone a player (or a peer)
    generates wins, and ZoneSystem.RemoveUnplacedLocations deletes the others.
    All of them are listed; none of them is 'the' position.
  ...
```

`--type boss|trader|dungeon|unique|all`, `--name <prefab>`, `--top <n>` (`0` for every one),
`--max-distance <m>`, `--json`. A full `--type all` run took **26.4 s** for one seed here (4.7 s world
grid on 16 threads, 21.7 s placement).

### `vseed map` — a PNG

```
vseed map MWd8eV6svz                                  # 2048 x 2048, the game's own grid
vseed map MWd8eV6svz --px 4096 --rivers --grid
vseed map MWd8eV6svz --zoom 0,0,2000 --px 1024        # 2 km around spawn
vseed map MWd8eV6svz --plain --palette game -o exact.png   # pixel-identical to the game's texture
```

The footer records the seed, the `worldGenVersion`, the grid, the area covered, the engine version
and the biome shares, so a PNG that has been sitting in a folder for a month still says what it is.
It lays itself out to the image width, down to `--px 256`.

**With no `-o` the render goes to the cache root** (`<cache>\maps`) and the path is printed — it is
not dropped into whatever directory you happened to be standing in, and `vseed clean` empties it.

### `vseed hash`, `vseed invert`, `vseed space` — the seed arithmetic

```
$ vseed hash MWd8eV6svz
  text                  "MWd8eV6svz"
  int32 seed            -1772362158
  even lane             210405865
  odd lane              223983285
  combiner              1566083941   seed = even + combiner * odd (checked)

$ vseed invert -1772362158 --alphabet game --length 10 --count 3
  text                  B2JpDNUCMg   (10 chars, re-hashed and checked)
  text                  9dEJ2Gsby3   (10 chars, re-hashed and checked)
  text                  pQscvHcbVt   (10 chars, re-hashed and checked)
```

Every text is re-hashed before it is printed. An unverified preimage is never returned.

### `vseed worlds`, `vseed world` — your saves, read-only

```
$ vseed worlds
Worlds  (2)
  world                seed text    int32 seed  gen  map cache  note
  -------------------  ----------  -----------  ---  ---------  ---------------
  asdasdasd            MWd8eV6svz  -1772362158    2  yes
  testworldclaude      hnBd9gJf2G    319486907    2  yes
```

`vseed world <name>` reads one save's seed, world-gen version, modifiers and contents;
`--locations` counts the location instances stored in the `.db2`. **Nothing is ever written to a save
folder or to Steam Cloud.**

### `vseed data`, `vseed selftest`, `vseed bench`

`data` reports the shipped game data and whether it matches your install. `selftest` re-checks this
build against the ground truth. `bench` measures each stage on your machine, so any throughput
estimate is anchored to a number you watched being produced:

```
$ vseed bench --no-map
Bench  (16 logical cores, .NET 10.0.12)
  stage                              measured                       rate
  ---------------------------------  -----------------------------  ----------------------
  generator construct + pregenerate  949.2 ms per world (8 worlds)  1.1 worlds/s, 1 thread
  GetBiome                           2,000,000 samples in 3.479 s   0.57 M/s, 1 thread
  GetBiomeHeight                     2,000,000 samples in 3.955 s   0.51 M/s, 1 thread
  GetStableHashCode (10 chars)       1,000,000 in 0.074 s           13.57 M/s, 1 thread
  shortest-text inverse              2,000 in 1.612 s               1241 seeds/s, 1 thread
```

---

## The web UI

```
vseed serve
```

A pan-and-zoom map of any seed on `http://127.0.0.1:8731`, with the seed panel, a click-anywhere
point panel, a ruler, location markers and a search panel. It is bound to loopback only, refuses any
`Host` header that is not `127.0.0.1`/`localhost`, and serves four files embedded in `vseed.exe`
itself — no CDN, no web font, no external request of any kind.

**The search panel is the CLI, not a subset of it.** The page's goals become the same query file
`vseed search` reads, planned by the same `SearchSession` and gated by the same preflight, so
`keep` is a real cap on the file there too, and the refusal rule and the confirmations are enforced
**on the server**: a `POST` from curl that skips them is refused with `kind: "refused"` or
`kind: "confirm"` and the whole report. Export downloads exactly that query file; running it in a
terminal reproduces the run.

**What it writes** (it used to say "nothing", which stopped being true when every search flag became
reachable from the page): the results file you name on the Search panel, inside one results
directory the server owns; the checkpoint and its kept-set snapshot, in the cache root; and the tile
cache's disk tier, also in the cache root. Your saves and Steam Cloud folders are still never
touched. `docs\web.md` has the exact rules.

`vseed serve --selftest` checks the server against the ground truth and prints every result. Run on
2026-09-23 on this build: **13 checks, all PASS**, exit 0 — tiles against the game's own texture
(262,144 of 262,144 pixels on both worlds), the mosaic, the markers (60 of 60 bit-identical), the
search panel against a direct engine run (200 hits identical in order and score), and loopback,
`Host`, CORS, CSP and path-traversal behaviour. See `docs\web.md`.

---

## Searching, and what it really costs

You write what you want as a query; the engine scans the seed space and writes the best matches to
disk. It is deterministic (same query + key + range = the same seed sequence, however many threads
ran), resumable (Ctrl-C writes a checkpoint, `--resume` continues) and it tells you what fraction of
the space it is going to cover **before it starts**.

```
vseed presets list                             what ships, and what each one costs
vseed presets show gentle-start > mine.json    a working query to edit
vseed search mine.json --dry-run               estimate the cost (an upper bound - see below)
vseed search mine.json --seeds 200000 --block-size 16 --keep 20 --out hits.jsonl --yes
vseed explain -1772362158 mine.json            why that one seed passed or failed
```

New to it? [`docs\finding-a-seed.md`](docs/finding-a-seed.md) walks the whole path once, with real
output.

### Before every run, not only `--dry-run`

Every `vseed search` prints a plan first: the grid and why, the region, the tier each goal is
answered at, the coverage as a fraction of 2³², the worker count with its arithmetic, the memory
budget, the **ceiling on the output file**, the free space, and the checkpoint path. Then one of
three things happens.

- **It runs.**
- **It asks**, when the run is expensive or surprising — the whole space, over an hour, over 100 M
  seeds, over 1 GB of output or 10 % of the volume, an evicting ceiling, or a grid the engine had to
  raise. With stdin redirected it does not start: it prints what needs confirming and tells you to
  pass `--yes` or use `--dry-run`.
- **It refuses**, when the run could not answer the question honestly, and names the fix. Nothing is
  scanned and nothing is written. Real examples from this build: a query whose goals are *all*
  must-haves (nothing to rank by, so "top 1,000" would be an arbitrary sample — fix it with a
  nice-to-have goal or `--accept-scan-order`); `--keep all` on a query that matches every seed
  (*"it WILL write about 818 GB and the volume has 599 GB free"*); a goal no seed can satisfy; a
  goal that excludes nothing (`Eikthyrnir` is always within 1,000 m, so "within 1,200 m" is a
  presence test, not a distance filter); and a metric that was retired, with the measurement that
  retired it and its replacement.

### Disk is bounded by what you asked for, not by what it finds

`keep` (default 1000) is **a real cap on the results file**: only those records are ever written, so
a whole-space run writes about 200 KB, and the report says `top 10 of 82 matches; 72 were not kept`
rather than `10 matches`. This is the headline change from earlier builds, where `--keep` capped an
in-memory table while every match was streamed to disk — `vseed search custom --all` would have
written a measured **7.36 TB**.

Ask for everything with `--keep all` and the output rotates into gzipped 1 GB segments (measured
10.0× compression) beside a `results.manifest.json`, with an optional `--reduce top:N` that reduces
each segment as it closes and deletes the raw one. `--max-bytes` sets a hard ceiling;
`--on-limit stop` (the default) stops cleanly at it and prints the resume command, `--on-limit evict`
keeps going and drops the lowest-scoring records, and has to be confirmed.

Checkpoints live in the cache root, keyed by the query hash — never in the directory you are
standing in — and a completed run deletes its own. A hard kill costs at most one block, and the
resumed file is **byte-identical** to an uninterrupted run's, verified by killing runs at three
points, bounded and streaming, and comparing SHA-256.

### The grid is part of the answer, and the engine will raise it

A figure measured at 24 m is a *different measurement* from the same figure at 12 m. Eight metrics
cannot be compared across grids at all, and for island, spawn-island, peak and shore goals no coarse
grid has a margin that is both safe and selective. So under `--screen auto` (the default) the engine
**screens coarsely with a measured margin and re-measures every survivor at the definitional grid**
before writing it, raises the grid for a must-have goal no coarse grid can decide (and says that the
run "really is compiled and measured at G12, not only described as it"), and stamps every record
with `screened_at_grid_m` beside the fine-grid numbers. `--screen off` measures once;
`--screen-grid <m>` picks the coarse grid by hand.

### The measured table

Measured on 2026-09-23 on an 8-core / 16-thread Ryzen 9800X3D at the shipped default `--mode
balanced` (8 workers). Every row is a real run's own `measured rate`. Provenance, machine load, the
`--mode full` comparison, the per-seed cost breakdown and the preset hit rates are in
[`docs\measurements.md`](docs/measurements.md), which is the single source for every cost number in
this repository.

| query | grid the run used | region | top tier | seeds/s | whole space |
|---|---|---|---|---|---|
| one biome goal (`custom`) | G12 | 1.0 km disc | T2 biome | 2,046 | **24.3 days** |
| `mountain-home` | G24 screen → G12 verify | whole world | T3 height | 1,736 | **28.6 days** |
| `coastal-builder` | G24 screen → G12 verify | 1.1 km disc | T3 shore | 19.5 | **7.0 years** |
| `balanced-biomes` | G192 | whole world | T3 height | 38.7 | **3.5 years** |
| `gentle-start` | G24 screen → G12 verify | whole world | T3 height | 7.3 | **18.6 years** |
| `all-traders` | G384 | whole world | T5 locations | 5.7 | **23.9 years** |
| `archipelago` | G12 | whole world | T3 height | 5.6 | **24.3 years** |

**Days for a biome question inside a disc; years for anything that needs heights, islands, shore or
a location.** `--mode full` (16 workers) buys about 1.5×, not 2×.

`--dry-run` will project a better number than any of these: it times a tight single-thread loop and
scales it, running about 1.5× ahead of a real run on a heavy T3 query and about **35× ahead** on the
cheapest biome-only one. It says so in its own output. Treat it as an upper bound and confirm with
`--seeds 20000`.

### Why the spread

**Cost per seed is how much of the world the goal forces you to evaluate, plus a fixed cost for
every new seed.** A biome question over a small disc is a few thousand cheap `GetBiome` calls on top
of that fixed cost. A height question adds `GetBiomeHeight` to every sample, and heights need the
lake/river/stream pre-generation — about 99.5 % of the cost of building a world — which is why the
engine defers it until a tier actually reaches it. A location question needs the 2048 × 2048
biome-and-height point grid that `GetRandomPointByBiomes` draws from, about **1.9 s per seed per
core** before a single candidate zone is tried, plus the ordered placement prefix.

The largest honest lever is the **region**: one biome goal at the game's own 12 m grid costs
7.90 ms/seed inside a 1 km disc against 314.40 ms/seed over the whole world — 39.8× — and the disc
decides a disc-bounded goal *exactly*.

None of that is fixable by writing faster code; it is the generator's own cost multiplied by 4.29
billion. So the realistic shape of a session is: **scan the whole space on something cheap, then
re-check the survivors with something expensive.**

A goal no seed can satisfy is refused before a single seed is touched:

```
$ vseed search q.json --dry-run
vseed search: goal 'swamp-near' can never be satisfied by any seed.
  Swamp can never be closer than 2,000 m to the centre, so '1,500 m or nearer' is impossible
  for every seed. GetBiome test 'dist > 2000 && dist < m_maxMarshDistance',
  maxMarshDistance = 6000 (worldGenVersion 2)

  Refusing to scan 4.29 billion worlds for something the generator cannot make.
```

More in [`docs\search.md`](docs/search.md); what the numbers do *not* mean is in
[`docs\limits.md`](docs/limits.md).

---

## Seeds: 853 quadrillion texts, 4.29 billion worlds

The game's new-world box takes a **text**. `World..ctor` runs `string.GetStableHashCode` on it and
keeps only the resulting **int32**; generation never sees the text again. So the int *is* the world,
and the whole space of worlds is exactly 2³² = **4,294,967,296**.

The seed field allows **10 characters** and validates them as **Alphanumeric** — measured from the
live UI component (`FejdStartup.m_newWorldSeed`: `characterLimit` 10, `Alphanumeric`), not guessed
from the code, because nothing in the game's code enforces a seed length at all. That makes

> 62¹ + 62² + … + 62¹⁰ = **853,058,371,866,181,866** typeable seed texts

collapsing onto 4,294,967,296 worlds — about **198.6 million texts per world** on average. Two
different-looking seeds giving the same world is not a bug; it is arithmetic.

Going the other way is exact, and `vseed space` re-verifies the whole thing before it prints it:

```
$ vseed space
  seed texts            853,058,371,866,181,866
  distinct worlds       4,294,967,296   = 2^32, because World..ctor keeps only the int
  texts per world       198,618,130   (a mean, not a guarantee)

  reachable at <= 5     142,962,629   3.33 %
  reachable at <= 6     3,310,424,872   77.08 %
  reachable at <= 7     4,294,967,296   100.00 %  (complete)
  unreachable at 6      984,542,424   includes seed 0 itself

  shortest is <= 5      3.33 %
  shortest is 6         73.75 %
  shortest is 7         22.92 %

Re-verified now  (0.23 s)
  |E_1| computed from the lane tables = 62 vs published 62  ok
  |E_2| computed from the lane tables = 2,097 vs published 2,097  ok
  |E_3| computed from the lane tables = 66,014 vs published 66,014  ok
  |E_4| computed from the lane tables = 2,058,466 vs published 2,058,466  ok
  inverse round trips: 200/200 texts re-hashed to their seed  ok
  seeds published as unreachable at 6 characters: 12/12 have no 6-char text and do have a 7-char one  ok

  all checks passed
```

**Seven characters reach every one of the 4,294,967,296 worlds.** Six reach 77.08 % of them; the
remaining 984,542,424 — including seed 0 itself — have no six-character alphanumeric text. Since the
field allows ten, any world SeedLab finds can always be typed back into the game.

One consequence worth knowing: **an empty seed box is not random.** `World..ctor` maps it straight to
0, which is one specific world — the same one the main menu background uses.

A token on the command line that parses as an int32 is read as the **int**, because that is what a
script emits; the seed-text reading is always reported as well, and `--text` / `--int` force either
one.

---

## The game data, and what to do after a Valheim update

Some of what SeedLab needs is not in Valheim's code — it is serialized asset data, and the code
defaults are wrong: `Minimap.m_textureSize` is 256 in the IL and **2048** in the prefab,
`m_pixelSize` is 64 and **12**, `ZoneSystem.m_locationVersion` is 1 and **32**.

So `data\1.0.15-59f53fb5\` holds what the running game was actually holding, read out of its loaded
objects by `tools\SeedLab.Dumper`, a BepInEx plugin: 232 `ZoneLocation` entries in list order, 257
vegetation entries, 32 alt biomes, 186 location prefabs, the prefab and version constants, the
seed-field limits, the constraint atlas and the native goldens. Every file carries a `DATA-STAMP`
naming the game build and the SHA-256 of its `assembly_valheim.dll`, and every file's own SHA-256 is
in `manifest.json` and is verified before it is parsed.

**The dumper is installed only for a named capture and retired after each.** The current data came out
of runs 4 and 5 on 2026-09-23: run 4 captured the whole dungeon surface after a field-coverage audit
found the earlier dumps had been carrying 10 of `DungeonGenerator`'s 24 public instance fields and no
`RoomConnection` data at all, and run 5 the localization table that lets the map say "The Elder"
rather than `GDKing`. Both copies were retired to `_ModSource\_retired\` afterwards. As of 2026-09-24
it is **installed again for run 6** - dungeon names (`Teleport.m_enterText`) and Vegvisir pins - and
holds **F4** until it is retired once more.

**SeedLab itself needs no plugin and no console command.** The dumper is a capture tool, not a runtime
dependency: terrain answers (biome, height, rivers, maps, seed arithmetic) are computed from the seed
and `worldGenVersion` alone and need nothing from the dump, and location answers read the shipped
tables. The one thing that brings the dumper back is a **game update**, which makes location answers
refuse rather than quietly produce coordinates from a stale table. `docs\dumper.md` has the procedure.

`vseed data` prints the stamp and the verdict:

```
Installed game
--------------------------
  verdict               MATCH - this data describes the installed game
  install               E:\SteamLibrary\steamapps\common\Valheim
  assembly_valheim      59f53fb55d99d22a33e8ed094eec8d21e9f133543bce92bc3d80dce44033adb1
  the installed game is the build this data was dumped from (Valheim 1.0.15, assembly_valheim 59f53fb5).

  terrain answers (at, map, seed, search on terrain): allowed
  location answers (locations, dungeons, traders, resources): allowed
```

### After a Valheim update

The stamp stops matching, and SeedLab **fails closed in the direction that matters**:

- **terrain answers keep working with a warning** — they come from the seed and `worldGenVersion`
  alone and use nothing in `data\`;
- **location answers are refused** — a location table from another build produces coordinates that
  look right and are not.

What to do, in order. **If you cloned this repository**, the first two steps are yours:

1. `powershell -ExecutionPolicy Bypass -File tools\check-game-version.ps1` — exit 0 means the
   installed game is the build SeedLab was verified on, exit 2 means the game moved, exit 3 means the
   game was not found (name it with `-ValheimDir <folder>` or `SEEDLAB_VALHEIM_DIR`).
2. Re-run the dumper (`docs\dumper.md`, and the full manual in `tools\SeedLab.Dumper\README.md`) and
   copy its output into a **new** folder `data\<game version>-<first 8 hex of the assembly sha256>\`.
   **Never edit the old folder** — an edit is indistinguishable from corruption and is treated as
   corruption.

The rest needs `groundtruth\`, which is not in the repository (see
[Cloned from GitHub?](#cloned-from-github-two-folders-are-not-here)), so it is **the maintainer's**:

3. `vseed selftest`. If the terrain checks still pass against the old ground truth, the generator
   itself did not change; if they fail, the port needs re-verifying against the new build before any
   number from it is trusted.
4. Capture fresh ground truth: a world generated by the new build, its `.fwl2`/`.db2`/map cache, into
   `groundtruth\`.
5. Re-run the two gates below. They are the definition of "SeedLab still matches the game".

---

## Where the evidence lives

Nothing in this README is a claim you have to take on trust. These are the things that check it:

```
dotnet run --project tests\SeedLab.Acceptance.Tests -c Release
```
32 checks, two to three and a half minutes. Regenerates both ground-truth worlds cell by cell and compares them
against what the game itself wrote — biome, height as binary16 codes, the world-edge constant, the
river pass, and the float32 `y` of every location instance in the saves. Last run: **32 passed, 0
failed**, 4,194,304/4,194,304 height codes exact per world, 0 biome mismatches, 12,314/12,314 and
12,287/12,287 location heights bit-exact.

```
dotnet run -c Release --project tools\SeedLab.LocationLab -- gate
```
The location gate. Fresh world: 12,228/12,228 instances bit-identical in zone, prefab and x/y/z, 178
of 178 prefabs exact, every per-type count equal. Played worlds: 12,314/12,314 and 12,287/12,287
reproduced from the `.db2`. Alt biomes 32/32, and all 29 of the game's own `placed N out of M` log
lines reproduced exactly. Last run: **GATE: PASS**.

```
dotnet run --project tests\SeedLab.Tests -c Release -- natives
```
The three Unity functions the port had to re-implement, against corpora captured from the running
game. 11 checks: **262,780/262,780** `Mathf.PerlinNoise` samples bit-exact, 268/268 `InitState`
seeds, **276/276** `UnityEngine.Random` traces (1,980 draws, both the result bits and the state after
each), `Mathf.FloatToHalf` resolved as ties-**away**-from-zero (.NET's `(Half)f` gets 2 of the 4
midpoints wrong), 93/93 libm results identical between Mono and .NET 10, 429/429 `GetStableHashCode`
vectors. Last run: **ALL 11 NATIVE CHECKS PASSED**.

```
dotnet run -c Release --project tools\SeedLab.GoldenCheck
```
The generator's *private* state against what the game's own generator was holding, for three seeds:
the five offsets, the two river seeds, the constructor's RNG draws, the lakes / rivers / streams in
order, the full rendered river-point grid (2.1 M points, 8.5 M float32 comparisons), and
`GetHeight` as float32 for 12,228 location instances. Last run: **VERDICT: PASS - every internal
field is bit-identical**.

```
vseed selftest          # the fast subset, any time
vseed serve --selftest  # the web server's tiles and its security properties
vseed space             # the seed-space arithmetic, recomputed before it is printed
```

The raw material:

| | |
|---|---|
| `groundtruth\decoded\*.biome.u8`, `*.height.f32` | the game's own minimap cache, decoded — bytes the game wrote |
| `groundtruth\worlds\` | the `.fwl2` / `.db2` of two worlds the game generated |
| `groundtruth\*-locations.csv`, `LogOutput-*.log` | the game's own location dump and its log lines |
| `groundtruth\natives\` | 262,780 real `Mathf.PerlinNoise` samples, 268 `Random` states + 276 traces, `Mathf.FloatToHalf` over an adversarial float set, Mono's libm, 429 hash vectors |
| `data\1.0.15-59f53fb5\goldens\` | the same natives corpora, plus the generator's private state per seed (with the full river-point grids) and 12,228 `LocationInstance`s of a fresh world |

One of the two ground-truth worlds, `testworldclaude` (seed 319486907), is a **hold-out**: it was
never used while porting the biome and height code. It matched blind on biome and to 99.9998 % on
height, and a last one-ulp residual was then diagnosed on both worlds and closed. The fully
independent check is the third, fresh seed 75539276 (GoldenCheck).

The name is the one DoomMachine gave that world in game, as a test world for Claude (see
[Credits](#credits)). It is stored inside the game's own save, which SeedLab never edits, and the
gates key on it, so it stays.

---

## Layout

```
src\SeedLab.WorldGen     the ported WorldGenerator + the Unity natives       docs\generator.md
src\SeedLab.Render       sampling grids, the map renderer, the PNG encoder   docs\generator.md
src\SeedLab.Locations    the ported ZoneSystem location placement            docs\locations.md
src\SeedLab.Search       the query language and the scan engine              docs\search.md
src\SeedLab.Web          the local web UI                                    docs\web.md
src\SeedLab.Data         the dumped game data, and the DATA-STAMP policy     docs\data.md
src\SeedLab.Seeds        GetStableHashCode and its inverse
src\SeedLab.Saves        read-only .fwl2 / .db2 / map-cache readers
src\SeedLab.Runtime      hardware probe, modes, memory guard, cache root, machine self-test
src\SeedLab.LocationOracle  the feasibility oracle the search checker refuses with
src\SeedLab.Cli          vseed itself
tools\SeedLab.Dumper     the BepInEx plugin that captured data\              docs\dumper.md
tools\SeedLab.LocationLab   the location gate
tools\SeedLab.GoldenCheck   the generator's private state against the game's
tools\check-game-version.ps1  is the installed game the build SeedLab was verified on?
tools\decompile.ps1           one game type as C#, for checking a spec's citation (needs ILSpy or ilspycmd)
tests\SeedLab.Acceptance.Tests   the 32-check gate against the game's own output
tests\SeedLab.Search.Tests       the query language, the tiers and prefilter parity
tests\SeedLab.Search.Safety.Tests  the output layer: bounds, rotation, kills and resumes
tests\SeedLab.Runtime.Tests      the runtime layer (122 checks)
tests\SeedLab.Tests              the library-level checks, incl. the natives gate
data\1.0.15-59f53fb5\    the captured game data (its own README is the authority)
groundtruth\             what the game itself wrote
docs\                    one short page per subsystem, plus docs\specs\
.claude\                 Claude Code skills and a research agent for Valheim modding and SeedLab
                         (.claude\README.md)
```

`docs\` in reading order:
[`finding-a-seed.md`](docs/finding-a-seed.md) (start here) ·
[`search.md`](docs/search.md) · [`measurements.md`](docs/measurements.md) (every cost number) ·
[`limits.md`](docs/limits.md) (what is not true of it) · [`generator.md`](docs/generator.md) ·
[`locations.md`](docs/locations.md) · [`data.md`](docs/data.md) · [`dumper.md`](docs/dumper.md) ·
[`web.md`](docs/web.md).

`docs\specs\` holds the eight design documents the source cites by name and section
(`07-features.md section 2.2` and the like). They lived in a session scratchpad that does not
survive; they were copied in on 2026-09-23 so those citations still resolve. Where a spec and the
code disagree, the code and the goldens are the evidence.

House rules, in case you come back to this and wonder: **no NuGet packages**; port rather than
improve, and cite the decompiled member for anything ported; keep the numerics discipline (the game
evaluates in `double` and truncates to `float` per statement — do not tidy an expression); never write
to a save folder or to Steam Cloud; and every number printed either is exact by construction or
carries its resolution.

---

## Credits

SeedLab was conceived, directed and tested by DoomMachine, who also captured - in their own copy
of Valheim - the worlds, game logs and game-data dumps it is verified against.

The code, tests and documentation were written by Claude, Anthropic's AI model, working in Claude
Code under DoomMachine's direction. Where docs\ mention "sessions" or "agents", they mean that work.
Commits are authored by DoomMachine; Claude is credited here rather than as a co-author.

Valheim is a trademark of Iron Gate AB. SeedLab is an independent project, not affiliated with or
endorsed by Iron Gate or Coffee Stain. Third-party code: THIRD-PARTY-NOTICES.md (FastNoise, MIT).
