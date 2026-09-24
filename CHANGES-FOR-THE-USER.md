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

## Dungeon names arrived (2026-09-24)

The name you see when you walk into a crypt — "Burial Chambers", "Sunken Crypts", "Frost Caves" — is
not in the game's code. It is a field on the dungeon's door (`Teleport.m_enterText`), stored in the
game's compressed asset bundles. Your dumper run of 2026-09-24 read every door and every Vegvisir, and
the map now calls `Crypt2` "Burial Chambers", `SunkenCrypt4` "Sunken Crypts" and so on, straight from
the door's caption: **31 of the 183 placed types have a name now** (8 bosses, 3 traders, 3 map-pin
labels, 17 dungeon entrances), up from 14, and 152 stand as their prefabs. Placement did not move: four
seeds, every instance bit-identical before and after, and the world the dump ran in is a new hold-out
the port had never seen (12,216 of 12,216 instances identical).

- **A name several places share picks none of them.** Crypt2, Crypt3 and Crypt4 all read "Burial
  Chambers" (so do the two Dvergr town entrances, "Infested Mine", and the three Putrid Holes), so
  typing that name lists the prefabs instead of choosing one; `group:burial_chambers` asks for all
  three.
- **The wiki agrees wherever it has a page.** Bear Cave, Winding tunnels and Mörkhalla have no page to
  check against, so they are the game's own captions and nothing more. DN_Bossroom carries a door
  captioned "The Prison", but that door is switched off in the prefab, so it names nothing.
- The dumper is retired again and **F4 is free**. Making the same data from your own copy of the game,
  step by step: `docs\game-data.md`.

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
  size: it says the funnel does not continue that file, and because stage 2 keeps its checkpoint at
  that same path it refuses before anything runs instead of after stage 1.
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

## A funnel's stage 2 checkpoints where you told it to, and `vseed serve` uses `--cache-dir` (2026-09-24)

- **Stage 2 used to ignore `--cache-dir`, `$SEEDLAB_CACHE_DIR` and `--checkpoint`**, in the terminal
  and on the page: its checkpoint always went to `%LOCALAPPDATA%\SeedLab\checkpoints`, while stage 1's
  survivor list went where you said. Now both go to the path the plan block prints.
- **A stage 2 you interrupted before this is not lost.** `--resume` finds nothing at the new path, so
  it looks at the old one; if the file there is this run's stage 2 it is moved over, with its
  kept-results snapshot, and you are told both paths. A file there that is anything else is left
  alone, and you are told where it is and why it was not used. With no `--cache-dir`, no
  `$SEEDLAB_CACHE_DIR` and no `--checkpoint` on Windows, the two paths are the same file and nothing
  moves.
- **`vseed serve --cache-dir <dir>` works.** The server used to start a second runtime of its own
  that had never seen the option, so the tile cache and every search checkpoint from the page went to
  the default cache root. It now shares the command's runtime: its startup block's `cache` line is your
  folder, and the process runs one throttle and one self-test instead of two.
- Proved by `SeedLab.Search.Tests` section 14: a terminal funnel and a page funnel, each stopped by
  its budget during stage 2 with `--cache-dir`, leave stage 2's checkpoint in that folder and nothing
  in the cache root `$SEEDLAB_CACHE_DIR` names; the terminal's `--resume` continues from it; and the
  move adopts a real stopped stage 2 (resuming to the uninterrupted run's bytes) while leaving another
  survivor list's, a sample run's and an unreadable file exactly as they were.

## "Access to the path is denied." — now it says which file, why, and the run goes on (2026-09-24)

A search died of that one sentence, with no file named. The cause, measured: Windows refuses to
replace a file while **any** other program has it open — a virus scanner, OneDrive or Dropbox, a
search indexer, a spreadsheet or an image viewer — and the same sentence also means "read-only" and
"not allowed", with nothing to tell the three apart.

- **Every command keeps a session log**: `%LOCALAPPDATA%\SeedLab\logs\vseed.log` (or under
  `--cache-dir`). It is **rewritten at the start of every command**, like the game's
  `BepInEx\LogOutput.log` — copy it before running another `vseed` if you want to keep it. (Changed
  later the same day: the last session's log is now kept as `vseed-prev.log` - see "Starting and
  stopping the web page" below.) It holds
  when and how the command was started, the machine, an access check of every SeedLab folder, the
  self-test, the warnings and errors that were printed (a question answered "no" shows only as the
  exit code), the retries behind a busy file, the full detail of an unexpected error, an integrity
  line, and the exit code. It holds your folder paths (`C:\Users\<your account name>\...` on
  Windows) and the command as typed: read it before posting it anywhere public. A second `vseed`
  running at the same time (a `vseed serve` left open) writes `vseed.log.1` (up to `.4`); a numbered
  log nobody uses is deleted by the next command. `hash`, `invert`, `space`, `presets`, `data`,
  `worlds` and `world` keep none.
- **Checked before it starts.** `vseed search` checks its results file and folders (and on `--resume`
  the checkpoint, its snapshot and the survivor list); `vseed map` checks `-o`. A problem is named in
  plain words with what to do, and in a terminal you get `[r]etry / [a]bort`. With `--json` or no
  keyboard the command stops (exit 3) and writes nothing; `--dry-run` says so, says which of the two
  a real run would do, and goes on. The startup block includes the line
  `file access checked: 11 paths OK; integrity confirmed (...)`.
- **That check cannot see the future.** A scanner can open a file an hour later; that is what the
  retries are for. A checkpoint that cannot be saved is now a **warning** (the file and the likely
  cause), the run goes on, and the next save tries again. "It passed SeedLab's access check at
  <time>, so something changed after that" is added only when the file itself passed that check and
  fails it now; a program that has the file open while letting others write to it passes the check
  and still blocks the save, and is named as one the check cannot see.
- **The last save** of a run that stops early waits ~15 s — and says so when the wait begins — then
  says what is on disk and what `--resume` will do, and offers `[r]etry / [g]ive up` in a terminal
  (answer `g`; Ctrl-C there no longer ends `vseed` before the results file is finished). A
  checkpoint that is there but held so that it cannot even be read is still named as the resume
  point, not as "no checkpoint". Giving up costs time, not results — at worst, when no save of the
  run ever worked, the whole run, because there is then nothing to resume from.
- **A save never guesses which kept-results snapshot is in use.** A resumed run whose checkpoint was
  held so that it could not be read used to write over the snapshot that checkpoint named, and the
  next `--resume` was refused ("delete everything and start again"). It now writes neither.
- **`vseed map`** waits ~15 s for a viewer that opened the output after the check, says so, and in a
  terminal asks `[r]etry / [g]ive up` with the finished image kept. **A rotated `--keep all` run**
  that finishes while its manifest is held prints its report, says every record is written and only
  the manifest is not, and exits 3; the page publishes it as done, with the manifest as a warning. A
  held manifest no longer stalls every rotation for 1.6 s.
- **On the web page:** a search whose file is in the way is refused by name ("press Find seeds again
  once the file is free"), warnings are listed under Find seeds (each added, none replacing another,
  none listed twice when the stream reconnects), and a last save that failed gets a **Retry saving**
  button. Checked in a real browser: "Not saved", then "still not saved" while the file was held, then
  "Saved" once it was let go. A page reloaded afterwards shows the save as it stands, not the error
  it was sent at the end; a run's warnings and its end are replayed however long it ran (they used to
  be dropped after 4,000 events), and a page whose server no longer knows the run says so and stops
  reconnecting.
- **Another web page can no longer press your buttons.** Any site you had open could send Stop to
  `127.0.0.1`; the server now refuses a `POST` that a browser marks as coming from another page.
- **`vseed clean`** counts only what it really deleted, lists what it could not (a running `vseed`'s
  log, a file a viewer has open) with the reason, and keeps its own log; its table says `removed`
  only for a category whose every file went. `vseed clean --json --yes` now removes what it reports;
  before, it returned before the removal and deleted nothing.
- **What a file problem that ends a command says.** A file that is in use, read-only or not allowed
  is named, with the probable cause and exit 3, whenever the error carries its path (every save
  SeedLab retries does); an access denial whose message names no path says "a file or folder" and
  points at the session log, also exit 3. A full drive is said as one, with exit 3, naming the file
  when the error does. Any other input/output error still prints "this is a bug" with exit 4.
  Exit code 3 now reads "not found - or a file or folder SeedLab needs is in use, read-only, not
  allowed or on a full drive; also a finished rotated search whose manifest could not be saved".
- Proved by `SeedLab.Search.Tests` section 17 (44 checks, through the built `vseed` and `vseed serve`:
  a held results file refused, a held checkpoint warned about and the run completed with the same
  bytes, a held last save announced, reported and then resumed to the same bytes, a map and a
  rotated run held after the check, the log's lifecycle, the clean and its table, the cross-site
  refusal, Retry saving and the save state a reloaded page reads), section 15 (42 checks: the search
  engine underneath, including a resumed run whose checkpoint could not even be read - run against
  the guessing save, that test fails twice: the snapshot changed, and the resume refused),
  `SeedLab.Runtime.Tests` section 7, and three new rows in `vseed serve --selftest`. On 2026-09-24,
  after the review's fixes:
  Search.Tests 443/443 (571 s), Runtime.Tests 173/173, `vseed serve --selftest` 15 PASS rows and 3
  MEASURED, `vseed selftest` 13 checks PASS, the proofs `refuse`, `policy` and `blocks` exit 0, and the
  kill tests 3/3 IDENTICAL both bounded and `keep all`.

## Starting and stopping the web page, two session logs, and files that do it for you (2026-09-24)

You asked for three things: that the web server is never left running without anyone knowing, with
scripts and commands for starting and stopping it; exactly two session logs; and double-click files
for people who are not power users. All three are built. The README's new
[How to use it](README.md#how-to-use-it) section is the walk-through; this is what changed.

- **The one-click files (Windows).** `SeedLab 1 - Install or update`, `2 - Open web page`, `3 - Stop
  web page`, `4 - Command window`, `5 - Uninstall (keeps the build)` and `6 - Remove the build`, and
  `SeedLab.bat`, which has all of them as a menu plus Status and Help. They ask before they install,
  stop or remove anything, refuse to run as administrator, and say that installing the .NET SDK is
  the one step that needs administrator rights (Windows' own prompt). On macOS and Linux the same
  actions are `sh seedlab.sh` (and `SeedLab.command` on a Mac) - **not tested on macOS at all, not
  yet on a real Linux**. [`docs\scripts.md`](docs/scripts.md) explains every step and question.
- **The web server is a window you can see.** `vseed serve` titles its window **SeedLab web
  server** and says first what it is and how to stop it. It never stops by itself and never starts by
  itself. `SeedLab 2` starts it in a window of its own with nothing else in it, and a second start
  opens the page of the one already running.
- **Five ways to stop it.** Stop SeedLab on the page (with a second
  warning if a search is running), `SeedLab 3 - Stop web page`, `vseed serve --stop` (asks when a
  search is running; `--yes`, `--force`), Ctrl+C twice within 10 seconds in its window (the first
  press stops nothing and says what is running), or closing the window. Every way stops a running
  search at once with its checkpoint saved, and says the command that continues it - except a search
  still in a funnel's first stage, which has no resume point yet and loses that stage's work (the
  warnings say so; see the review section below).
- **The 60-minute reminder.** After 60 minutes with nobody using the page, the page and the window
  suggest stopping it, and again every 60 minutes. It never stops SeedLab. `--idle-reminder <minutes>`
  changes it; `0` turns it off.
- **`vseed serve --status`** says whether it is running, where, since when, and whether a search is
  running in it. `--status` and `--stop` create nothing - not even the cache folder - so an
  uninstall can ask them after removing it.
- **Two session logs**: `logs\vseed.log` (this session) and `logs\vseed-prev.log` (the one before),
  in the cache folder. A log past 4 MiB keeps only warnings and errors; past 16 MiB it stops.
- **Uninstall** stops the web server through `vseed serve --stop`, asks separately when a search is
  running (its checkpoint would go to the Recycle Bin with the cache folder), and removes the cache
  folder with both session logs and the server's `serve\` file in it. A `vseed` started with
  `--cache-dir` naming another folder is left out of it.
- **`seedlab-results\`** (the page's results folder inside the SeedLab folder) is now in
  `.gitignore`.

**Checked** - the script test matrix, run on 2026-09-24/25 in a copy of the working tree (built
there) with every script test redirection set: a scratch cache folder, a test registry key standing
in for your user Path, and no browser. Windows, through `cmd /c SeedLab.bat` and the numbered files:

| Row | Result |
| --- | --- |
| help; an unknown action; test mode without its safety settings; "Run as administrator" (simulated) | help exit 0; exit 2; refused, exit 2; refused, exit 3 |
| .NET SDK missing (hidden): the W/B/C choice, answered C and with no answer | cancelled, exit 1, nothing changed |
| install: build, Path entry added (still `REG_EXPAND_SZ`, `%USERPROFILE%` unexpanded), a planted entry for a deleted folder removed; install again | exit 0; second run "already registered", no duplicate; the cache folder was not created |
| web (`--no-pause`, port 0): only `vseed`'s banner, no second one from the script; web again | server up with its cache in scratch; second run printed `vseed`'s "SeedLab is already running at ... (since 23:57)", exit 0 |
| web in a real new window (no `--no-pause`) | a console window titled "SeedLab web server" with `vseed.exe` in it; `stop` closed it, and the window went away |
| status with a server, with a search running, and with a second server of the same build started by hand with another `--cache-dir` | the address, pid and search progress; the other server listed as one no cache folder knows about |
| stop with no search; stop with nothing running | stopped, exit 0; "not running - nothing to stop", exit 0 |
| stop with a search running and nobody to answer | `vseed` refused, exit 1, the server kept running |
| `stop --yes`; `stop --cache-dir <other folder>` | stopped; the search's checkpoint and the resume command printed; the other server stopped |
| install while the server runs a search: n, then y | n: nothing built, exit 1; y: the server stopped through `vseed` with its checkpoint saved, then the build |
| uninstall: n; y then n to "Stop it anyway?"; `stop --yes` and y | nothing changed; server left running, cache folder left in place, Path entry removed; cache folder to the Recycle Bin |
| uninstall with everything: a search running, a `SEEDLAB_CACHE_DIR` folder holding a file of yours, two `SEEDLAB_...` settings, a fake dumper plugin and config, a dumper output folder (all y) | 6 items to the Recycle Bin (the cache folder; only `logs` and `serve` of the chosen folder, your file stayed; the plugin, its config, the output folder); the test Path back to exactly its two original entries; the settings removed |
| after uninstall: uninstall again, status, stop, and a bare `vseed serve --status` / `--stop` | "Nothing to uninstall"; the cache folder stayed absent |
| the removed cache folders, restored afterwards to look inside | each held `logs\vseed.log` (and `vseed-prev.log` where two sessions had run), and an empty `serve\` |
| shell; menu s, h, q; the six numbered files; a changed source (n) | all ran; "The SeedLab source has changed ... Using the existing build." |
| remove-build with the uninstall pending (y, y, y), and a decoy `docs\bin` | uninstall ran first, 24 `bin`/`obj` folders deleted (16.0 MB), the other 422 files and the decoy untouched |

macOS/Linux script, under `dash` (Git for Windows) with the OS forced to linux, a scratch home and
the Windows build of `vseed`: help, unknown action, the test-mode guard, the non-Linux refusal, root
refusal (exit 3), missing SDK (C and no answer), install (launcher and three lines in `.bashrc`),
web with `vseed`'s banner only, status, second web ("already running"), stop refused with a search
running, uninstall y then n (server and cache kept), `stop --yes` (checkpoint and resume command),
uninstall (cache deleted for good - Git Bash has no Trash - and `.bashrc` byte-identical to before),
nothing recreated afterwards, unregistered servers from a fake process list (one of this build sent
SIGTERM, one with another `--cache-dir` left out), remove-build with the uninstall pending
(delete declined), the menu, a stale source, and `SeedLab.command`. `sh -n` / `dash -n` pass.

**Nothing else regressed** - re-run in the real tree after the scripts and the documentation were
done (a fresh Release build, 0 errors): `SeedLab.Search.Tests` **521/521** (616 s, including section
18, the lifecycle: real Ctrl+C and Ctrl+Break sent to a server's own console), `SeedLab.Runtime.Tests`
**206/206**, and `vseed serve --selftest` **17 PASS rows and 3 `MEASURED`**, exit 0 (its stop with a
search running answered in 6 ms). The lifecycle work's own numbers, measured just before: the same
521/521 and 206/206, the self-test twice at 17 PASS, `vseed selftest` 13 checks PASS, the proofs
`refuse`, `policy` and `blocks` exit 0, and the kill tests 3/3 IDENTICAL both bounded and `keep all`.

**Not tested:** anything on a real Mac or a real Linux (SIGTERM, SIGHUP, `open`/`xdg-open`, the
terminal title, `gio`/`trash-put`, the zsh startup file, the ARM notice, `ps` output); closing the
server's window for real; a Windows shutdown or logoff; typing an answer at `vseed`'s own `Stop
anyway? [y/N]` (only the refusal with nobody to answer, and `--yes`, ran); `--force`; the 60-minute
default itself (tests used seconds); Windows' "delete permanently?" warning for a folder too big for
the Recycle Bin; installing the SDK for real (winget, the browser, `dotnet-install.sh`); the web
action from the menu; 32-bit PowerShell; the SmartScreen and "file came from the internet" wording.
Rows of the scripts' first test run (earlier on 2026-09-24) that were not run again this time: winget
missing from the PC (W shown as not available); a double-clicked window pausing until Enter (the web
action's new window was tested instead); the command window opening as a real new window (only its
test-mode run in the same window); on the macOS/Linux side, the generated `~/.local/bin/vseed`
running `vseed --version` and its exit-3 message with the build missing, remove-build actually
deleting (declined this time; it deleted for real on Windows), and stopping a live process by
SIGTERM (a fake process list stood in).

**Found on the way, not fixed here:** **without `data\`, every search is refused** - even one that
asks only about terrain - because the search's checker needs `data\...\constraint-atlas.json`
(`vseed search archipelago --dry-run` in a copy without `data\`: `REFUSED ... the constraint atlas
(constraint-atlas.json) was not found`, exit 1; the page's Search panel answers 400 with the same
refusal). The README, `docs\game-data.md` and `docs\finding-a-seed.md` said terrain searches work
without the game data; they now say what happens. Whether a missing atlas should refuse or only warn
(its own message says "every such refusal is downgraded to a warning") is yours to decide.

## After the review of the start-and-stop work (2026-09-25)

Three reviews read the start-and-stop work, the scripts and the documents, and ran them against
scratch folders. What they found, and what changed:

- **The resume command now works as printed.** It used to say `vseed search <this query file>
  --resume ...`, and after a stop from the server's window, a script or a closed window nothing could
  give you that file any more. A page search now keeps its query file beside its checkpoint
  (`<cache folder>\checkpoints\<hash>.ckpt.query.json`), and every resume command names it. The file
  goes with the checkpoint (a finished search deletes both; `vseed clean` counts it under
  checkpoints). The server's window now stays open until you press Enter (at most 10 minutes) when a
  search it stopped left a checkpoint, so the command can be read or copied; the page's "stopped"
  overlay lists that search even when the stop came from outside the page.
- **A resume point is never replaced without asking.** Pressing **Find seeds** again on a search that
  was stopped started it from the first seed and overwrote the checkpoint every stop had just promised
  to keep. Now the page says a resume point exists, shows the command that continues it, and asks:
  **Start again from the first seed** or **Cancel**. (`vseed search` in a terminal without `--resume`
  still starts over without asking - that is older behaviour, not changed here.)
- **Signing out of Windows or shutting it down now stops the server properly.** Windows sends a
  console program no sign-out or shutdown event once it has loaded a certain Windows library, and
  `vseed serve` has - so a shutdown ended it abruptly, losing up to 30 seconds of a search. The server
  now keeps a hidden window that hears the session end and runs the same stop as every other way.
- **A left-over "server is running" file no longer locks you out.** If the web server was ended
  without cleaning up (Task Manager, a power cut), its small file in `serve\` could later name a
  system process, and SeedLab then believed a server was running: `SeedLab 2` opened nothing, `SeedLab
  3` could not stop it and the uninstall refused. Such a file is now recognised as left over, named as
  safe to delete, and removed by the next server. The address in that file is also checked: only
  `http://127.0.0.1:<port>` is ever opened or asked.
- **The warnings say the truth about a funnel search in its first stage.** They promised "the finished
  part is saved" and then said the work would be lost. Each running search now gets its own sentence:
  saved, with its command - or, in a funnel's first stage, lost.
- **Ctrl+C pressed again while SeedLab is stopping** says "SeedLab is already stopping; wait a moment"
  instead of "nothing has been stopped yet".
- **Uninstall** offers, from a folder you chose with `SEEDLAB_CACHE_DIR`, only folders with SeedLab's
  names that hold nothing but SeedLab's kind of files (it used to offer any folder called `logs` or
  `maps`, and any `*.log` file), and it says it did not finish - exit 1, and `SeedLab 6` stops - when
  something was left running. On macOS and Linux it no longer rewrites a startup file whose SeedLab
  block has lost its end line (it would have removed everything after the block); it leaves the file
  as it is and says which lines to remove by hand.
- **Install** does not ask to stop the web server when the build is already up to date; it says
  "nothing to build" and goes on.
- **Stop and Status** refuse "Run as administrator" (or root) like the other actions.
- **Wording:** the ASP.NET Core runtime is needed by every `vseed` command, not only the web page; the
  server's token keeps other web pages out, not other programs on the same computer; the README's
  command table marks `vseed explain` as needing the game data; Updating from a ZIP now says to
  unpack into a new folder; the Mac file's "no permission" fix is in the README.

**Checked:** a clean Release build, 0 errors; `SeedLab.Search.Tests` **536/536** (614 s; section 18
has the new checks: left-over registry files, the query file and a resume from it, the "same query
again" refusal, a funnel stopped in its first stage, the session-end window's two messages sent to it,
and a third Ctrl+C while it stops); `SeedLab.Runtime.Tests` **211/211** (a registry file naming a
system process is stale, seven wrong addresses refused, a live log recognised by its last writer line);
`vseed serve --selftest` **17 PASS rows and 3 `MEASURED`**, exit 0; `vseed selftest` 13 checks PASS;
the proofs `refuse`, `policy` and `blocks` exit 0; the kill tests 3/3 IDENTICAL, bounded and `keep
all`. In a browser against a scratch server: a search stopped with `vseed serve --stop --yes` was
listed on the tab's overlay with its command; pressing Find seeds again on it showed "This search was
stopped before, and can be continued" (Cancel kept it, Start again started over); the page's own Stop
SeedLab showed the per-search text and no Save button. The script matrix again, in a fresh copy of the
folder, test mode, scratch everything: Windows - install, install again with nothing to build (with and
without a server running: no question), web, status, web again, stop refused with a search running
(the command names the query file), uninstall y/n with a search running ("did NOT finish", exit 1),
remove-build then refusing to go on (exit 1), `stop --yes`, stop and status as administrator
(simulated, exit 3), a server started with another cache folder ("But a vseed web server IS
running"), a left-over file naming pid 4 (not running; nothing to stop; not in the uninstall plan), a
`SEEDLAB_CACHE_DIR` folder holding the user's own `notes.txt`, `build.log`, `logs\...` and `maps\...`
(only `checkpoints` and `serve` offered; the user's files untouched), uninstall with only the Path
entry (finished, exit 0), remove-build (24 build folders deleted, 16.4 MB); macOS/Linux script under
`dash` - install, status and stop as root (exit 3), uninstall with an edited end line (`.bashrc`
byte-identical, "did NOT finish", exit 1), the same with the end line put back (only SeedLab's lines
removed, a conda block after it kept), and the chosen-folder case as on Windows. Status and Stop, on
both, now also name a left-over "server is running" file as safe to delete (a planted one naming pid
4 on Windows, pid 1 under `dash`). Rows of the first matrix that were **not** run again this time,
although the scripts changed: the .NET SDK missing (W/B/C), the web page in a real new window, the six
numbered files and the menu, the command window, the "source has changed" prompt, uninstall with a
dumper plugin and `SEEDLAB_...` settings, "Run as administrator" for install, web and the command
window, and on the macOS/Linux side the fake-process-list rows (servers of this and another folder)
and remove-build.

**Not tested:** clicking the server window's close button (a review sent the window its close message once, by
hand: the search's checkpoint was saved and nothing was left behind - no automated test does that); a
REAL sign-out or shutdown (the test sends the hidden window the two messages Windows sends; that
Windows delivers them at a real sign-out is Microsoft's documented behaviour, not watched here); the
server window waiting for Enter after a stop that left a checkpoint (it only waits in a window of its
own with a keyboard, which a test cannot give it); typing an answer at `Stop anyway? [y/N]`; `--force`;
two web servers in one cache folder at the same time; a stop during the short measurement a funnel
makes between its two stages; the 60-minute default itself; and everything on a real Mac or Linux
(SIGTERM, SIGHUP, file modes, `open`/`xdg-open`, `gio`/`trash-put`). After a stop from outside the
page, a tab takes up to about 20 seconds to notice - known, not changed.

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
- **`vseed search --json` on a refused run** writes the refusal to stderr and exits 1 (3 when a file
  it needs is in the way) with an empty stdout.
- **The `[r]etry / [a]bort` and `[r]etry / [g]ive up` questions have not been run yet** - nor
  Ctrl-C at them, which is now caught so that the results file is finished and the report printed
  (whether Windows then ends the question as a give-up, or leaves it waiting for `g`, is not
  verified). Every automated test runs with nothing reading the keyboard, which takes the refusing
  path, and that path is the one tested. The first time a file is in the way in a real terminal is
  their first run.
- **A full drive is not waited out.** It is named now instead of being called a bug, but a checkpoint
  save or a results write that meets one still ends the run. Saves write a temporary file first, so
  the checkpoint on disk stays the last one that was saved - by construction; no test fills a drive.

## One thing only you can do

1. Done: **run 6 of the dumper** ran on 2026-09-24 and the plugin is retired again to
   `_ModSource\_retired\DoomMachine-SeedLabDumper-20260924-run6`; F4 is free.
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
