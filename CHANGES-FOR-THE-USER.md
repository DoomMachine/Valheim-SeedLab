# SeedLab — what changed, and what to try first

Written 2026-09-23. If you are reading this three weeks later: this is the state of the tool the
last time anyone ran it end to end, and every number below came out of a command run that day on
your machine (8-core / 16-thread 9800X3D, Valheim not running, machine otherwise idle).

---

## The short version

The libraries were finished a while ago; **the shipped commands did not call them.** `--keep` capped
an in-memory table while every match still went to disk. The refusal rule existed and was never
invoked. Screen-then-verify produced zero records carrying the grid they were screened at.
Checkpoints and map PNGs piled up in whatever folder you were standing in. Some of that had been
reported to you as done. It was not.

**It is done now, and each item below names the command that proves it.** Nothing about the
generator changed: all five gates still pass with the same counts.

## Try these first

```powershell
cd E:\SteamLibrary\steamapps\common\Valheim\_ModSource\SeedLab
dotnet build src\SeedLab.Cli\SeedLab.Cli.csproj -c Release
Set-Alias vseed .\src\SeedLab.Cli\bin\Release\net10.0\vseed.exe

vseed presets list                       # 13 queries, all of them runnable, with their cost class
vseed clean                              # what SeedLab is holding on disk, and where
vseed search gentle-start --seeds 4000 --keep 20 --out hits.jsonl --yes
vseed explain <a seed from that run> gentle-start
vseed seed <that seed>                   # the whole world: land, biomes, islands, bosses, traders
vseed serve                              # the map and the same search engine, on 127.0.0.1:8731
```

Two flags worth knowing on day one: **`--mode full`** when you are not playing (about 1.5× faster
than the default), and **`--block-size 16`** on anything expensive when you want a finer resume
point: one worker computes a whole block, so a kill costs up to one block of that worker's time.
(It used to be needed for speed too, because a short run at the fixed 256 left most of your cores
idle; the block size is automatic now — see 2026-09-24 below.)

## What works today — with the command that proves it

| | proof |
|---|---|
| **`--keep` is a real cap on the results file** | `vseed search gentle-start --seeds 400 --keep 10 --out first.jsonl --yes` → file holds **10 records**, report says `top 10 of 82 matches; 72 were not kept`. |
| **Resource modes are real, and default to balanced** | every run prints `workers 8 (balanced mode = 50 % of 16 logical cores -> 8; memory allowed 840)`, the memory budget and the priority. `--mode full` measured 1.45–1.54× faster. |
| **Auto-throttle when Valheim is running** | drops to background (~25 % of cores, BelowNormal) and prints one line with the override. (Proved by the wiring pass with a process named `valheim` alive; not re-run today because the game was closed.) |
| **A run that cannot answer honestly is refused** | `search custom --all --keep all` → exit 1, *"it WILL write about 818 GB and the volume has 599 GB free"*. A query that is all must-haves → exit 1 naming four fixes. A goal that excludes nothing → exit 1. All three: nothing scanned, nothing written. |
| **A run that is expensive asks first** | `search gentle-start --all` → exit 1, *"This run needs confirming: - this is the WHOLE 4,294,967,296-seed space"*, and tells you to pass `--yes`. |
| **Screen-then-verify reaches disk** | `gentle-start` and `coastal-builder` runs: 20 of 20 records carry `"screened_at_grid_m":24` beside `"grid":12`. |
| **The grid is raised when a must-have needs it** | the plan says so, and the record's `grid` is the raised one. |
| **Unbounded output rotates and reduces** | `search custom --seeds 3000 --keep all --rotate 200KB --compress gz --reduce top:20` → 6 segments `seg.0001.top20.jsonl` … plus `seg.manifest.json` `{compressed:true, rotate_bytes:204800, reduce:"top:20", complete:true, records:3000}`. |
| **Checkpoints live in the cache root and are deleted on completion** | every run prints the path under `<cache>\checkpoints`; a completed run prints `checkpoint retired - the run completed, so ... was deleted`. Nothing lands in the working directory. |
| **`vseed map` no longer litters** | `vseed map 1457947068 --px 512` with no `-o` → written under `<cache>\maps`, and the working directory is unchanged. |
| **`vseed clean`** | reports per category with free space, `--yes` removes the caches, and it tells you the dumper's 48.77 MiB output folder is redundant (25 of 25 files byte-identical to `data\`) **without ever deleting it**. |
| **All 13 presets run** | `vseed presets list --json` → `runs_now: true` for every one; 6 of them need the location table. |
| **Two dead metrics give a named refusal** | `coastline_length` → the measurement that killed it (p90/p10 = 1.065 over 2,560 seeds) and a worked example of its replacement `world:shore_area_within`. Never "unknown metric". |
| **The feasibility checker runs on every search** | built from all 183 location types and validated against 36,829 real instances with zero violations; a DATA-STAMP mismatch disables refusals entirely rather than refusing on another build's data. |
| **The machine self-test fails closed** | on a cold cache, 271 numerics checks + 263,780 recorded native values, stamped in `<cache>\selftest`. One altered recorded value made `vseed seed 12345` exit 1 naming the case and both numbers. |
| **The web UI is the CLI** | `vseed serve --selftest` → **exit 0, every check PASS**, including `search panel vs the engine` (200 hits from the page vs 200 from a direct engine run, identical in order and score) and all four security checks. |

## The gates did not move

Re-run at the end of this pass (see the bottom of this file for the exact commands):
acceptance **32/32** with 0 biome mismatches and 4,194,304/4,194,304 height codes per world;
location gate **12,228/12,228** fresh + 12,314 + 12,287 played, 29/29 game-log counters;
natives **11/11** (262,780 Perlin samples, 276 Random traces, 93 libm, 429 hashes);
GoldenCheck **PASS** (8,469,000 floats, 0 differing); search tests **70/70** (the suite has grown
since: **244/244** just before the block-size change below, **321/321** after it).

## What it costs — measured, not projected

At the default `--mode balanced` (8 workers). The full table, its provenance and the per-seed cost
breakdown are in `docs\measurements.md`.

| | seeds/s | the whole 4.29-billion space |
|---|---|---|
| one biome goal inside a 1 km disc | 2,046 | **24 days** |
| `mountain-home` (screened at G24) | 1,736 | **29 days** |
| `balanced-biomes` (nine biomes, G192) | 38.7 | **3.5 years** |
| `coastal-builder` (shore, 1.1 km disc) | 19.5 | **7.0 years** |
| `gentle-start` (spawn island) | 7.3 | **18.6 years** |
| `all-traders` (a location goal) | 5.7 | **23.9 years** |
| `archipelago` (island count at 12 m) | 5.6 | **24.3 years** |

**A biome question inside a disc can sweep the whole space in weeks. Heights, islands, shore and
locations cannot — those are years.** The way to search for something expensive is to filter the
whole space with something cheap and re-check the survivors.

One preset is worth a warning: **`mountain-home` found 0 matches in 217,600 seeds** today. It is not
broken — its two must-haves (Mountain within 1,500 m *and* ≥ 3 km² of Mountain inside 2.5 km) are
genuinely rare, under 3 in 217,600 by the rule of three. Loosen it rather than wait.

## Since the first write-up (2026-09-23, later the same day)

- **`--strategy auto|funnel|sample` now exists**, in the terminal and in the web panel. `funnel`
  measures the cheap must-have goals over the whole range first and places locations only for the
  survivors, reporting the survivor count and the **measured** cost of the second stage before it
  starts. It is not faster than the ordinary run — the tier ladder already skips placement for a
  settled seed — it is that the cost stops being an extrapolation. On a probe query the gate said
  17.1 s and the run took 17.3 s. `docs\search.md` has the section, including the soundness bug this
  found in itself and the test that now holds it.
- **The D4 and D5 count rules shipped.** They needed a measured count distribution, and
  `data\1.0.15-59f53fb5\count-sample.bin` now ships it: the raw 5,000-seed x 183-type matrix, 1.8 MB.
  Both warn and never refuse, and both fall silent if that sample is missing or from another build.
- **The dumper ran a fourth time** and the dump now carries the whole dungeon surface: all 23 readable
  `DungeonGenerator` fields and 1,019 room connections. It had been carrying 10 of 24 generator fields
  and no connections at all — and almost none of the missing ones were at their code default, so an
  offline camp or crypt reproduction would have been quietly wrong.

## The map now calls places by their names (2026-09-24)

`vseed serve` used to print the game's internal prefab names everywhere: `GDKing` for The Elder,
`Vendor_BlackForest` for Haldor, `Mistlands_DvergrBossEntrance1` for The Queen. It now shows the
game's own name wherever the 1.0.15 dump has one — 14 of the 183 placed types: all 8 bosses, all 3
traders, and the three places the game itself labels on the map pin (Forge of Potential, Sealed Tower,
Charred Fortress) — with the prefab kept beside it, never instead of it.

- **The map labels, the hover readout and the marker list** read `The Elder`, with `GDKing` in the
  smaller mono face next to it. A row in the plain mono face is a place the dump has no name for,
  and there is no invented name anywhere: 169 of the 183 stand as their prefabs, as they did.
- **The type list is grouped and in order**: bosses A–Z, traders A–Z, then the rest by biome in the
  same order as the map legend, and "Several biomes" last. Bosses read *Bonemass, Eikthyr, Fader,
  Kall Fimbulbringer, Moder, The Elder, The Queen, Yagluth* — sorted on exactly the string you see,
  so "The Elder" is under T. A group with nothing in it is not shown at all.
- **The filter box takes a name, a prefab, a biome or a feature.** Typing `axe` now finds the two
  house types whose chests can hold an axe head (`WoodHouse2`, `WoodHouse6`), which is the one thing
  that could not be searched for before. Click either and the card states, in full, the two
  *different* things that are not known: whether WoodHouse2's chest exists at all is decided by the
  seed and SeedLab does not replay that draw yet, and what is inside any of these chests is not a
  function of the seed at all. Those two are never multiplied into one percentage.
- **The search panel's place box takes either spelling.** Type `The Elder`, `the elder` or `elder`
  and the goal that is built, run, hashed and written to CSV still says `location:GDKing`. Your
  saved query files are untouched and keep working exactly as they did — the prefab is still the
  identity, and the box is only more forgiving about how you reach it.
- **Click a marker** and the card now names where the name came from. One of them says
  *unverified* and means it: `BogWitch_Camp`'s trader carries no name in this build, so "The Bog
  Witch" comes from the `$npc_bogwitch` token by the convention the other two traders follow. You
  confirmed it from the game; the dump still does not join the two, and the card keeps saying so.
- **Markers you saved before this change keep their old text.** A saved marker is a note you wrote,
  stored under `seedlab.markers.<seed>` in the browser, and nothing rewrites it. New markers made
  from the "Mark" button on a selected place carry the name. If you want an old one renamed, delete
  it and mark it again.

The page is embedded in `vseed.exe`, so this needs `dotnet build src\SeedLab.Web` and then
`src\SeedLab.Cli` before `vseed serve` shows any of it — editing the files on disk does nothing on
its own.

## The toolbar says what is on without using colour (2026-09-24)

You said it plainly: *"I can't know if Mark being yellow means it is enabled or not."* You were right,
and it was worse than it looked. Measured on the shipped page: the on and off label colours differed by
a contrast ratio of **1.448:1** where 3:1 is the floor for a reader to see a difference at all; the
pressed fill was **1.05:1** against its own background, which is invisible; the only thing that cleared
3:1 was a 1px ring, and only over a dark part of the map. A pressed button also got **no hover
feedback whatsoever**, because the pressed rule beat the hover rule on every property — so the one
gesture that usually tells you a control is live did nothing.

State is now answered four independent ways, none of them hue:

- **Fill polarity.** On is a solid filled pill; off is outlined and dim.
- **Text polarity.** On is dark text on the fill; off is light text on nothing.
- **A glyph.** A filled shape when on, a hollow one when off — and the SHAPE tells you what kind of
  control it is before the state does: a **circle** for Pan / Ruler / Mark, which are one-of-three, and
  a **square** for the layer switches, which are independent of each other.
- **The word**, on the layer switches: literally `ON` or `OFF`.

In greyscale, Pan reads as a near-white pill with dark text and a filled circle; Ruler and Mark as
transparent with light text and a hollow circle; Grid as an outlined box with a hollow square and the
word OFF; Shade, Depth and Places as filled pills with a filled square and the word ON. Nothing on that
bar needs you to see a colour.

Two more things came out of the same pass. Pan / Ruler / Mark are now a real segmented control, so a
screen reader is told they are three choices of one thing rather than three unrelated buttons, and the
arrow keys move between them; `R`, `M` and `Esc` work exactly as before. And a bug: while the first
place computation was still running, the Places button would announce *"places shown"* while its own
pressed state still said off — the button and the message contradicting each other. It now says *"still
placing — the markers appear when it finishes"* and shows a busy state instead.

This one needs your eyes rather than a test. Nothing automated can tell you whether the bar reads right
to you, whether it wraps where you would want it to, or whether the filled state is too loud on your
monitor. Say so and it moves.

## An axe-head preset, measured before it was written (2026-09-24)

`vseed search axe-heads` finds seeds with a house that can hold an axe head — `WoodHouse2` or
`WoodHouse6` — within **150 m of spawn** (measured from the temple, not the world centre), and ranks
first the seeds whose `WoodHouse6` is close too, because that house's chest always exists (55.4 % for
the axe head) while `WoodHouse2`'s chest is itself a 50 % coin (27.7 % before the seed is consulted).
Every threshold in it came out of two independent 512-seed samples with no seed in common: the
nearest house from spawn has a median of 189 / 194 m, and 150 m keeps 37.5 % / 37.9 % of seeds. It
runs at about 4.1 seeds/s. It promises the house, never the axe head: what a chest holds is drawn when
you first open it, from a generator no seed controls.

## Dungeon names need one more dump (2026-09-24)

The name you see when you walk into a crypt — "Burial Chambers", "Sunken Crypts", "Frost Caves" — is
not in the game's code. It is a field on the dungeon's door (`Teleport.m_enterText`), stored in the
game's compressed asset bundles, and the proven way to read it is the running game (the Unity editor
you installed matches the game's build and could be a second route, but it is untried). So the map still shows
`Crypt2` where you would say "Burial Chambers", and it will keep doing that rather than guess from a
wiki (your wiki IDs are kept as the cross-check for when the real data arrives). The dumper has been
taught to read every dungeon door and every Vegvisir, reviewed adversarially, and **installed and
armed** for one run — see "Two things only you can do" below.

## A short run is cut so every worker can have a block, and a budget says what it really bounds (2026-09-24)

You found it: `vseed search <query> --seeds 512` on 8 workers was **2 blocks of 256**, and one worker
computes a whole block, so 6 of the 8 did nothing — and the rate the report then printed "on 8
threads" was two workers' rate. Measured on `axe-heads`: 64 seeds took 1.6 min as one block of 64 and
16.4 s as eight blocks of 8, with byte-identical results.

- **The block size is automatic.** Leave `--block-size` out and it is 256, or smaller when that would
  give a worker fewer than four blocks: `--seeds 512` on 8 workers is now 32 blocks of 16, and the
  plan says why — *"block size 16, sized automatically for this run: the default 256 would cut these
  512 seeds into 2 blocks, and one worker computes a whole block, so 6 of the 8 workers would have
  nothing to do"*.
- **A size you give is kept.** When it leaves workers idle you get a warning with the counts and what
  the automatic size would be, never a silent change.
- **A resumed run keeps its checkpoint's size**, on any thread count, because a resume point is a
  block number. A different `--block-size` on `--resume` is refused before the prompt, naming the
  size that works and the file to delete — for a funnel's stage 2 at its gate instead, once stage 1
  has run or its survivor list is reused, because stage 2's checkpoint is recognised by that list.
  A funnel resumed over a *sample* run's checkpoint of the same query no longer borrows its block
  size: it says the funnel does not continue that file, and when stage 2 would have to overwrite it
  (no `--cache-dir`, no `--checkpoint`) it refuses before anything runs instead of after stage 1.
- **None of this changes a result.** A completed run's results file is the same bytes at every block
  size — jsonl, json and csv, `--keep N` and `--keep all` — and a run stopped on 8 threads and
  finished on 3 at the automatic size ends with the uninterrupted run's SHA-256. `proof blocks`
  checks both.
- **The web page's Block size box is empty now, and empty means automatic** — the same rule as the
  terminal's. It used to send a fixed 64. The ceiling is 256 for both, your choice, so on a long
  location-tier run one block can be about 6 to 8 minutes of one worker's time (256 seeds at 1.3 to
  1.9 s each: the location presets in `docs\measurements.md`, and `axe-heads` measured with all 8
  workers busy), and that is when Stop and the first streamed results land. Type a number in the box
  for finer steps.
- **The rate says when not every worker had work**: `measured rate  145.7 seeds/s on 8 threads  (2 of
  8 workers had work)`, and `--json` carries `busy_workers`, `block_size` and `blocks`. It is counted
  when a worker takes its first block, so a run that lasts well under a second can say `7 of 8` even
  at the automatic size — one worker was still starting up when the others had taken every block —
  and that is the truth about that run. The web page no longer keeps such a rate as "measured here"
  or works out the whole space from it. The
  `--dry-run` estimate now times a short run by its busiest worker and prints the scale it used.
- **`--budget` is an overrun, not a floor.** It is checked only when a worker is about to take a
  block, and a block already taken is finished, so a run can end up to one block of work past the
  budget, plus the workers' start-up and the final write — the plan prints that bound with the run's
  own numbers, and a funnel's gate prints stage 2's. The old sentence, "a budget cannot
  stop a run sooner than one block per worker", was false: `--budget 0.001s` evaluates 0 seeds. That
  run also reported `coverage 100.00 % ... (the whole space)`; it reports 0 % now. And a run that
  finished is no longer reported as stopped by the budget as well.
- **A funnel gives each stage the whole budget, and a stop in stage 1 ends the run**, in the terminal
  and on the page alike — your call. The terminal's stage 1 used to ignore the budget, and the page's
  went on into stage 2 after a Stop or a budget stop, with a fresh budget. A Stop pressed after the
  last block was already taken stops nothing, and is no longer reported as a stop — so it no longer
  throws a finished stage 1 away; the terminal remembers it (and one pressed while the gate measures
  placement) and stops stage 2 at its first block boundary, with the survivor list and a checkpoint
  kept, as the page already did. And the page now ends a funnel whose stage 1 kept no seed, and a
  run that fails between stages, instead of waiting on it for good.

## Still not implemented — named so you do not go looking

- **GPU.** Still rejected after CPU SIMD delivered 6.03× bit-exactly. A GPU path could only ever be
  an approximate screening tier — fused multiply-add and approximate transcendentals are exactly
  what decides a biome at a threshold.
- **The write-path tripwire test.** "SeedLab writes nothing outside its cache root and the file you
  named" rests on an IL-level audit and hand-checked before/after listings, not on a test that fails
  when it stops being true. This is the one I would fix next.
- **A calibration sample for rarity** (`vseed calibrate` does not exist), so rate warnings use the
  atlas's labelled samples only.
- **Cross-architecture exactness.** x64 only; Linux/macOS on x64 expected to work but untested,
  arm64 untested and unproven. The self-test is what stands in for it, and it fails closed.
- **CSV does not carry the new per-goal fields** (`measured_at_grid_m`, `grid_comparable`,
  `grid_note`, `censored`). Use `.jsonl` when you care how a number was measured.
- **`vseed search --json` on a refused run** writes the refusal to stderr and exits 1 with an empty
  stdout.

## Two things only you can do

1. **Run the dumper once more — it is installed and ARMED again (2026-09-24, run 6).** It is in
   `BepInEx\plugins\DoomMachine-SeedLabDumper\`, armed for the asset dump only (`dumper.enable`
   says `assets`), SHA-256 checked against the build. Start Valheim **solo**, create a **fresh
   throwaway world**, stand in it, press **F4**, wait for `asset dump DONE`, quit. That is the whole
   job — no console commands this time. It is retired again as soon as the data is checked.
   (History: runs 4 and 5 on 2026-09-23 were retired to `_ModSource\_retired\` the same way; the
   tool needs no plugin to work, and apart from this run only a Valheim update brings it back.)
2. **Type `y` once.** The confirmation prompt has only ever been exercised with stdin redirected,
   which proves the refusal-and-name-`--yes` path but not the interactive one. Run
   `vseed search gentle-start --all` in a real terminal, answer `n`, then `y`, and you will have
   covered the branch no harness can reach.

## Where to read more

| | |
|---|---|
| `docs\finding-a-seed.md` | the whole path once, start to finish |
| `docs\measurements.md` | every cost number, with the machine and the load it was taken under |
| `docs\limits.md` | what is **not** true of the tool — grid-relative metrics, bounded rates, unpredictable things, one architecture |
| `docs\search.md` | the query language, refusals, output bounding, the grid policy |
| `README.md` | everything, in order |

The five gates, for when you want to check the tool rather than a seed:

```
dotnet run --project tests\SeedLab.Acceptance.Tests -c Release
dotnet run -c Release --project tools\SeedLab.LocationLab -- gate
dotnet run --project tests\SeedLab.Tests -c Release -- natives
dotnet run -c Release --project tools\SeedLab.GoldenCheck
dotnet run --project tests\SeedLab.Search.Tests -c Release
```
