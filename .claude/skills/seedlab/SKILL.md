---
name: seedlab
description: SeedLab and its vseed CLI - DoomMachine's offline, bit-exact reimplementation of Valheim world generation (verified against 1.0.16, unchanged from 1.0.15) - the repository these skills ship in. Use it whenever a task needs the biome, terrain height, rivers, map image, island or land statistics, or location placement (bosses, traders, dungeons, altars) of a seed without launching the game; whenever a seed text must be hashed, inverted or searched for; whenever someone asks "find me a seed with X"; and whenever work touches SeedLab's source, its data\ snapshot of game data, its ground truth, its gates, the dumper plugin, the local web map on 127.0.0.1 and its start/stop scripts, the per-seed performance profiler, or which CPUs and vector paths SeedLab is proved on.
---

# SeedLab

**Valheim world generation, offline and exact** - verified against **1.0.16** (Steam build 25527674,
`assembly_valheim` `96cfc004...`) since 2026-09-26 (`Verified.cs`, commit `5d94b63`); the port was built and
first proven on 1.0.15, whose generation 1.0.16 did not change. A .NET 10 command-line tool (`vseed`) and a
local web map that answer "what is in this seed" without launching the game, and can scan the whole
2^32 seed space. Built by DoomMachine's sessions 2026-09-22/23; it is the repository this skill ships
in (on the author's machine it lives at `<Valheim>\_ModSource\SeedLab`, inside the game folder), and the
repository's root `README.md` is the user-facing manual (kept current by the project itself). Paths
below are relative to the repository root.

For the *game facts* behind it, use the **valheim-worldgen** skill; for the toolchain and pitfalls, the
**valheim-modding** skill. This skill is about the tool: what it proves, how to run it, what it must
never do, and how to bring it forward after a game update.

| | |
| --- | --- |
| Source | the repository root (11 `src\` projects, 3 `tools\`, 3 `tests\`); a git repository since 2026-09-24, published **public** at https://github.com/DoomMachine/Valheim-SeedLab (MIT). `data\` and `groundtruth\` are git-ignored on the user's decision and exist only locally. Commits: author DoomMachine with the noreply address, **never a `Co-Authored-By` line** (a local `commit-msg` hook refuses one) |
| Binary | `src\SeedLab.Cli\bin\Release\net10.0\vseed.exe` (`dotnet build src\SeedLab.Cli\SeedLab.Cli.csproj -c Release`) |
| Needs | .NET 10 SDK; the ASP.NET Core 10 shared runtime for **every** `vseed` command, not only `serve` (`vseed.runtimeconfig.json` lists `Microsoft.AspNetCore.App`). **No NuGet packages** - it builds offline |
| Game data | `data\<version>-<hash>\` - read out of the running game by the dumper, stamped with the build's `assembly_valheim.dll` SHA-256. As of 2026-09-26: **`1.0.16-96cfc004\`** (33 files: dumper run 7's 29, a README, and the three derived files re-stamped from 1.0.15) and `1.0.15-59f53fb5\` (rebuilt after that day's deletion: every data file byte-identical to a surviving source, `manifest.json` regenerated). With both present, vseed picks the one matching the installed game |
| Ground truth | `groundtruth\` - **private, never published**: four worlds Valheim 1.0.16 created on 2026-09-26 (saves, map caches and their decoded rasters), the game's logs of those two sessions, the natives run 7 recorded, the location lists; `MANIFEST.sha256` checks it (74 lines) and its `README.md` says where each file came from. Rebuilt 2026-09-26 from a second copy kept outside the repository |
| In the game | only `tools\SeedLab.Dumper`, a BepInEx plugin. **It is NOT installed** (checked 2026-09-26 evening): run 7 ran on 2026-09-26 (asset dump at 16:33) and the plugin was retired to `_ModSource\_retired\DoomMachine-SeedLabDumper-20260926-run7` (earlier runs beside it) - see "the dumper" below. Nothing else in SeedLab runs inside Valheim |

## The invariants - never break these

1. **Nothing in a save folder or Steam Cloud is ever written.** Save, map-cache and `.db2` readers open
   files read-only; `SeedLab.WorldGen`, `Seeds`, `Locations` and `Contracts` reference **zero**
   `System.IO` members, and `Saves`, `Data` and the web server are read-only - all checked at IL level
   in the 2026-09-23 audit. **The regression test that would keep it true is not written yet** (it is
   item 9 on the must-fix list in `references/decisions.md`), so this invariant currently rests on a
   one-off audit, not on a tripwire.
2. **Location answers fail closed on a DATA-STAMP mismatch.** Every file in `data\` carries a
   `DATA-STAMP` naming the game build and its `assembly_valheim.dll` hash, and every file's SHA-256 is
   checked against `manifest.json` before it is parsed. If the installed game is not that build:
   *terrain* answers continue with a warning (they need only the seed and `worldGenVersion`), *location*
   answers are **refused** - a location table from another build yields coordinates that look right and
   are not.
3. **The tool enumerates int seeds, never texts.** `World..ctor` hashes the text and generation never
   sees it again, so there are exactly 2^32 worlds and 853,058,371,866,181,866 texts. A search over
   texts would do 198.6 million times the work for the same answer.
4. **Port, do not improve.** Every ported member cites the decompiled `Type.Member`, and the numerics
   discipline is the game's: double interiors, narrow where the IL narrows, never tidy an expression.
   Mono keeps float expressions at double precision on the evaluation stack - that is a real rule here,
   not a style (valheim-modding `pitfalls.md` section 9).
5. **Every number carries its resolution, and an unpredictable thing is never printed as a coordinate.**
   Area/island/nearest figures name the sampling grid; `m_unique` types list all candidates and refuse
   to name a winner; a goal no seed can satisfy is refused before the scan starts.
6. **Never edit `data\<version>-<hash>\` by hand.** An edit is indistinguishable from corruption and is
   treated as corruption. A new build gets a **new** folder.
7. **A search's disk cost is bounded by what was asked for, not by what it finds** - and since
   2026-09-23 this is true *through the shipped commands*, not only in the library. `keep` (default
   1000) is a real cap on the results file (verified: `--keep 10` over 400 seeds wrote 10 records and
   reported `top 10 of 82 matches`), the true match count is always reported beside it, and
   `keep: "all"` has to be written out loud and rotates into gzipped segments with a manifest
   (verified: 6 segments + `seg.manifest.json` `{complete:true, records:3000}`). A killed run costs at
   most one block and its resumed file is **byte-identical** to an uninterrupted one. Checkpoints and
   rendered maps live in the **cache root** (`--cache-dir`, `$SEEDLAB_CACHE_DIR`,
   `%LOCALAPPDATA%\SeedLab`), never in the working directory, and a completed run deletes its own
   checkpoint. Nothing per seed is ever written, so there is no per-seed cleanup and the tool does not
   pretend there is.
8. **A run that cannot answer the question honestly is refused, before a seed is touched** - an
   all-must-have query (nothing to rank by), unbounded output that cannot fit the volume, a goal that
   excludes nothing, an impossible goal, a retired metric. Every refusal names the fix and says
   "Nothing was scanned and nothing was written". A run that is merely expensive (the whole space,
   >1 h, >100 M seeds, >1 GB, an evicting ceiling, a raised grid) **asks**, and with stdin redirected
   it does not start. `--yes` accepts; `--dry-run` is never gated by the prompt.
9. **One RuntimeContext per command.** `--mode background|balanced|full` (**default balanced** =
   ~50 % of logical cores), the memory guard, the auto-throttle when Valheim is running, the cache
   root and the machine self-test all come from `SeedLab.Runtime`, and every sizing decision prints
   its arithmetic. The self-test checks this binary against 271 numerics checks and 263,780 recorded
   native values on a cold cache and **fails closed** (exit 1, naming the first failing case and both
   numbers); `--skip-self-test` then warns on every world-building command that bit-exactness is
   UNVERIFIED.

## What it proves, and which gate proves it

Detail, exact counts and how to read a failure: `references/proofs-and-gates.md`.

(The five gates below answer "is the answer right". What the output layer does to the disk, to
memory and to a hard kill is mostly a separate suite, `tests\SeedLab.Search.Safety.Tests`.
`tests\SeedLab.Search.Tests` reaches the grid policy (sections 10-13) and, since 2026-09-24,
checkpoints and the built `vseed.exe` end to end: section 14 runs a CLI funnel and a `vseed serve`
search under `--cache-dir` and needs a Release CLI built from the same source plus the dumped
location table.)

| Gate | Command | Result on 1.0.16 (2026-09-26, binaries built at `ded6c94`; on 2026-09-27 the acceptance, location gate, natives, generator-internals and self-test rows passed again on a fresh clone of `048ccd1`, the alpha 0.2.0a, in its release audit, and `serve --selftest` and `space` on a fresh clone of `fc1807a` in its claims check) |
| --- | --- | --- |
| Acceptance (terrain, 32 checks) | `dotnet run --project tests\SeedLab.Acceptance.Tests -c Release` | 32/32; biomes 0 mismatches and **4,194,304/4,194,304** binary16 heights exact per world, on both re-created worlds; location heights 12,314/12,314 and 12,287/12,287 bit-exact |
| Location gate | `dotnet run -c Release --project tools\SeedLab.LocationLab -- gate` | fresh world `Throwaway` **12,182/12,182** bit-identical, 974 sectors, 32/32 alt biomes; the 1.0.16 saves of the three reference worlds 12,314 / 12,287 / 12,228; all 27 of the game's logged counters. **Fails** (`MISSING`) when its log is absent, since `ded6c94` |
| Natives (11 checks) | `dotnet run --project tests\SeedLab.Tests -c Release -- natives` | **262,780/262,780** `Mathf.PerlinNoise`; 268 `InitState` seeds and 276/276 `Random` traces (1,980 draws); `FloatToHalf` ties away from zero; 93/93 libm; 429/429 hash vectors |
| Generator internals | `dotnet run -c Release --project tools\SeedLab.GoldenCheck -- data\1.0.16-96cfc004` | run 7's 3 captures, every private field bit-identical: 677,094 + 675,579 + 729,925 river points, 0 differing; `GetHeight` 12,182/12,182 |
| Fast subset, any time | `vseed selftest` (`--quick`, ~4 s) | 15 ok on 0.2.0a (2026-09-27, 2.9 s): the new row N1 is the machine self-test with the generator goldens (263,780/263,780). Since `f8a8f4d` (in the alpha 0.2.0a, **not yet reviewed**) it **fails** when `groundtruth\` is there but a file of it is missing (before it, that passed silently); with no `groundtruth\` at all, as in a clone of the public repository, it exits 3 "ground truth not found", as before (proofs-and-gates.md) |
| Web server + its security | `vseed serve --selftest` | tiles vs the game's texture, markers, search parity, loopback/Host/CORS/CSP/traversal, POSTs from other pages refused, the stop endpoint. **All checks passed on the 1.0.16 data** on 2026-09-27 (a fresh clone of `fc1807a`, 8 s); with stale data it had failed its marker check, because the page hangs instead of refusing (history.md, the web diagnoses) |
| Seed arithmetic | `vseed space` | recomputes the lane tables and round-trips before printing |

The ground-truth worlds: `asdasdasd` (`MWd8eV6svz`, the development world) and `testworldclaude`
(`hnBd9gJf2G`, 319486907, a **hold-out** never used while porting the biome and height code: it matched blind
on biome and to 99.9998 % on height, and a last one-ulp residual was then diagnosed and closed, valheim-worldgen
`world-generator.md`), `ClaudeTestWold2` (`ClaudeTest`, 75539276, the first fresh-world check), all three
re-created in 1.0.16 on 2026-09-26; and `Throwaway` (`VRbvYNainE`, -1147437162), the fresh world dumper run 7
ran in, which is the independent check on 1.0.16 (GoldenCheck and the location gate).

## Running it

```
vseed seed <seed>            everything about one world: land, biomes, islands, spawn, landmarks
vseed at <seed> <x> <z>      one point, exactly - biome, height, base height, river weight, forest
vseed locations <seed>       where things are (--type boss|trader|dungeon|unique|all, --name, --top)
vseed map <seed>             a PNG (--px, --zoom, --rivers, --plain --palette game = the game's texture)
vseed hash|invert|space      seed text <-> int32, and the size of the space
vseed worlds|world <name>    your own saves, read-only
vseed data|selftest|bench    what data is loaded, is it still right, how fast is this machine
vseed profile                where one seed's time goes, phase by phase (marks a busy machine TAINTED)
vseed presets|search|explain search the seed space; explain why one seed passed or failed
vseed serve                  the local map on http://127.0.0.1:8731 (loopback only, no external request);
                             --stop / --status find and stop a running one
vseed clean                  what SeedLab holds in its cache root; --yes removes it
```

- Global flags, either side of the command name: `--mode background|balanced|full` (default
  **balanced**), `--threads`, `--cache-dir`, `--ignore-running-game`, `--skip-self-test`,
  `--accept-unverified-platform`, `--simd auto|scalar|avx2|avx512` (or `SEEDLAB_SIMD`; a ceiling on
  the vector path, never a change of result), `--json`, `--debug`.
- **For non-technical users** the repository root has one-click scripts (since 2026-09-25): `SeedLab.bat`
  (a menu) and `SeedLab 1 - Install or update.bat` to `SeedLab 6 - Remove the build.bat` on Windows,
  `seedlab.sh` / `SeedLab.command` on macOS and Linux (`docs\scripts.md`). Every session writes
  `<cache root>\logs\vseed.log`; the previous one is `vseed-prev.log`.
- **Other CPUs**: every vector path gives the same bits and the self-test fails closed if one does not.
  `vseed selftest --report` is the machine report to run and send from an untested CPU (needs neither
  `groundtruth\` nor `data\`); `docs\cpu-compatibility.md` says which path each CPU family gets and what
  is proved where. A standalone package for testers without Valheim or .NET:
  https://github.com/DoomMachine/Valheim-SeedLab-IntelTest (release v1.0.0). First result, an Intel i7-12700K: all 93 checks pass (history.md).
- `--json` on any data command; exit codes `0` ok, `1` a check failed, `2` bad command line, `3` not
  found, `4` internal fault.
- **The block size is automatic since 2026-09-24** (CLI and web alike; ceiling 256, the user's
  choice). One worker computes a whole block, so a short run used to leave workers idle (400 seeds in
  blocks of 256 = two busy workers: 2.0 seeds/s against 7.3). Now, when no size is given, a run too
  short to give every worker 4 blocks is cut finer and the plan says why; an explicit `--block-size` /
  `search.block_size` / web box value is kept and WARNS with the idle count; a `--resume` adopts the
  checkpoint's size (a resume point is a block number). Pass a size only to pin the resume
  granularity (the T3+ warning still suggests 16, or 4 with location goals).
- **Run it with the working directory at the SeedLab root**, or set `SEEDLAB_DATA_DIR` to a
  `data\<version>-<hash>\` folder; otherwise every location answer fails closed (invariant 2). To use an
  older build's data while a newer game is installed, set `SEEDLAB_DATA_DIR` to it and `SEEDLAB_VALHEIM_DIR` to
  an **empty** folder: the verdict becomes "no Valheim install found" and location answers stay allowed,
  unverified (unless `SEEDLAB_REQUIRE_GAME_MATCH` is set) - how the 1.0.15 snapshot was checked on 2026-09-26.
- A token that parses as an int32 is read as the **int**; `--text` / `--int` force either reading.
- Searching costs what the generator costs: **days for a biome question inside a disc, years for
  anything needing heights, islands, shore or locations**, and `--dry-run` over-projects (up to 35x on
  the cheapest query). **The single source for every cost number is the project's
  `docs\measurements.md`** (measured 2026-09-23 at the default balanced mode, with the machine load
  recorded); do not quote a throughput figure from anywhere else, including older entries in this
  skill.

## What it cannot tell you

Not a limitation of the port - these are not functions of the seed at all:

- **Which candidate of an `m_unique` location survives** (Haldor, Hildir, the Bog Witch have 10
  candidates each). The winner is the first candidate zone a player or peer generates.
- **Any rotation**, and therefore the interior of a dungeon whose generator sits off its location's axis
  (12 of the 21 dungeon-bearing prefabs).
- **The ground a building finally rests on.** `gen y` is `WorldGenerator.GetHeight`, which is what the
  save stores; real ground comes from the built heightmap and the location's terrain edits.
- **Anything about a world that has already been played** - and the reproduction is only valid because
  genloc runs once, at creation, with no zone yet generated.
- Creatures, spawns, loot, ore, dungeon room contents: not modelled at all.

And two things that are known only *within a bound*, which the tool states as bounds and never as
facts: **how many instances of a type a seed gets** (the seven boss altars were 5,000/5,000 over
5,000 seeds, which bounds shortfall at <= 0.060 % and does not make it fixed - while `DN_Bossroom`
falls short in 0.520 % of seeds, CI 0.355-0.761 %), and **bit-exactness off x64** (never tested; the
fail-closed machine self-test is what stands in for it). The project's `docs\limits.md` is the full
list, with the measured grid errors for the eight metrics that are not comparable across grids.

## The dumper, and its current state

`tools\SeedLab.Dumper` (GUID `DoomMachine.SeedLabDumper`) is the BepInEx plugin that reads the game's
own loaded objects - the location table, alt biomes, prefab constants, the seed field, the generator's
private state, and the native-function corpora - and writes `data\<build>\`. It is **inert unless armed**
by a `dumper.enable` file beside its DLL, runs only in a solo session you host (main menu for the
generator and native modes), refuses to write anywhere near the game install or a save folder (output
goes to `%USERPROFILE%\AppData\valheim-dumper`), and restores every game static it borrows in a
`finally`.

**State as of 2026-09-26, evening** (checked on disk): **not installed.** Run 7 - the whole 1.0.16 dump,
mode `all` - ran on 2026-09-26: natives and the generator captures of `MWd8eV6svz` and `hnBd9gJf2G` at the
main menu, then F4 in the fresh world `Throwaway` (`asset dump DONE` at 16:33; prefab walk 186 loaded and room
walk 358 loaded, 0 failed) into `%USERPROFILE%\AppData\valheim-dumper\1.0.16-96cfc004`, copied whole into
`data\1.0.16-96cfc004\`. The plugin (`SeedLab.Dumper.dll` `FAEEE8D7...F6F3F7`, `SeedLab.Contracts.dll`
`15504007...0B2F`, `dumper.enable` = `all`; hashes re-checked 2026-09-26) is in
`_ModSource\_retired\DoomMachine-SeedLabDumper-20260926-run7`, with runs 4-6 and the build that zeroed
`UnityEngine.Random` beside it (history.md). Its config, `BepInEx\config\DoomMachine.SeedLabDumper.cfg`, was
left behind and still says `DumpKey = F4`; that binds nothing while the plugin is absent, but the user keeps
**F4 reserved** for the dumper (decision 2026-09-24) so it can be installed again without a key conflict.
**F4 is ignored while the console, chat, a map text box or any text field is open** (`Plugin.IsTypingElsewhere`),
so close the console before pressing it. `Random.state` is identical at start and end of the menu modes; during
the asset dump it changes (run 7, and run 6 too by its session log - corrected 2026-09-26: this said run 6's was
identical), because the game draws from `Random` itself between frames in a live world (**Unverified**
explanation; no state was all-zero, so it is not the 2026-09-23 defect). The dumper records whatever plugins are
loaded: runs 6 and 7 and every ground-truth world were made with the same 16 plugins (TomTom's version aside;
run 7's log: `17 plugins to load` with the dumper), and an IL scan found none that touches world generation or
the tables. Agreement with a mod-free game is inferred, never observed.

While it is armed it holds **F4** (asset dump) and registers `seedlab_status`, `seedlab_dump`,
`seedlab_natives`, `seedlab_worldgen`, so F4 is not a free hotkey for another mod, and a stray F4 in a
solo session starts a dump that hitches the game. **Deleting `dumper.enable` makes it inert again**
without uninstalling anything; moving the folder to `_ModSource\_retired\` removes it entirely. Either
is the user's call, not an agent's - it is recorded here so the next session knows.

## After a Valheim update

As it worked for 1.0.16 on 2026-09-26 (history.md has the detail):

1. `tools\check-game-version.ps1` - exit 2 means the game moved (exit 3: game not found); the
   valheim-modding skill's `.claude\skills\valheim-modding\scripts\check-game-version.ps1` answers the same
   against its KB-STAMP.
   **Before the user starts the game again, and after every game session below,** copy `Player.log`,
   `Player-prev.log` (`%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\`) and `BepInEx\LogOutput.log` to a
   durable, private place outside Temp: `Player.log` is gone two starts later.
2. **Audit the code, not the self-test.** `vseed selftest` compares SeedLab with the OLD build's recordings,
   so it passes whether or not the generator changed. Decompile the new build and compare every ported member
   with the port, op by op (biomes and heights, rivers, placement and alt biomes, seeds and saves), and hash the
   engine (`UnityPlayer.dll`, CoreModule, the Mono runtime). No copy of the old assemblies is kept, so it is a
   port-vs-new-code comparison, not a diff.
3. **Dump** (`docs\dumper.md`; `tools\SeedLab.Dumper\README.md`). Build with `-p:DeployToGame=false`, run
   `preflight.ps1` (run 7: 464 checks, 0 failures), and install only with the user's OK, after they quit
   Valheim to the desktop; a **new** `dumper.enable` reading `all`; check the installed hashes. The user, at the
   main menu: `seedlab_status`, `seedlab_natives`, `seedlab_worldgen MWd8eV6svz hnBd9gJf2G`; then a new world
   with a throwaway name and the offered seed, Start server unticked; standing in it, console closed, F4 once;
   wait for `asset dump DONE`; quit to the desktop. Move the plugin to `_ModSource\_retired\` at once.
4. **Fresh ground truth.** The user re-creates the three reference worlds from their seeds - `asdasdasd`
   (`MWd8eV6svz`), `testworldclaude` (`hnBd9gJf2G`), `ClaudeTestWold2` (`ClaudeTest`) - enters each once and
   logs out, nothing explored. Copy their saves (`_main.N.*`, Steam Cloud), map caches (`worlds_local`) and the
   logs of both sessions read-only into a second copy outside the repository, then build
   `groundtruth\` from it with a manifest. **Never give a worktree a junction** to `data\` or `groundtruth\`
   (valheim-modding `pitfalls.md` section 2: `git worktree remove` deleted both through junctions).
5. **New data folder:** copy the dumper's output folder whole into `data\<version>-<first 8 hex>\` and change
   nothing in it. The dumper does not make `constraint-atlas.json` or `count-sample.*`, and no tool rebuilds
   them: without them every `vseed search` is refused. On 2026-09-26 the 1.0.15 ones were re-stamped, after
   proving the tables identical (history.md). `vseed data --verify` must pass 8/8. **The same holds for a person who
   clones the public repository and makes their own dump**: location answers work, search does not (the atlas's
   "not found" is turned into a refusal by `SearchPreflight.AppendCheck`) - a product gap until the atlas and count
   sample builders ship in `tools\` (the repository's docs now say so, 2026-09-26).
6. **Point the gates at the new worlds**: `git grep` the old fresh-world seed hex in `tests` and `tools` (two
   gates hard-coded 1.0.15's). Rebuild every project after each commit, the CLI last. Then acceptance, the
   location gate, natives, GoldenCheck, `selftest`, Runtime.Tests, Search.Tests and the safety proofs - they
   are the definition of "SeedLab still matches the game". Read each PASS for its rows (proofs-and-gates.md).
7. Only then commit `Verified.cs` (version, Steam build, hash) as its own commit, record it in
   `references/history.md`, and re-stamp valheim-modding's KB-STAMP (in its `environment.md`).

## Reference files

| File | Read it for |
| --- | --- |
| `references/proofs-and-gates.md` | every oracle and gate in detail, the measured numbers, what each one can and cannot discriminate, and the known open ends |
| `references/decisions.md` | the user's own decisions about searching, output bounding, resource use and GUI parity - the master spec for work that is not built yet |
| `references/history.md` | what was built and corrected, in order, with evidence |

The project's own `docs\` has one short page per subsystem (`generator`, `locations`, `search`, `web`,
`data`, `dumper`, `scripts`) plus pages that are the authority for a whole question:

| Project page | It is the authority for |
| --- | --- |
| `docs\measurements.md` | **every cost/throughput number**, with the machine, the date and the load |
| `docs\limits.md` | what is NOT true of the tool: grid-relative metrics and their measured errors, bounded rates, unpredictable things, one untested architecture, what is not implemented |
| `docs\finding-a-seed.md` | the user-facing path: build, search, read the result, open the map |
| `docs\cpu-compatibility.md` | which vector path each CPU gets, the per-level proofs, the machine reports received |
| `docs\game-data.md` | making `data\` from the reader's own game, step by step (the dumper) |
| `CHANGES-FOR-THE-USER.md` | what changed for the user, from the 2026-09-23 wiring pass on, with the proving command per item |

`docs\specs\` holds the eight design documents the source cites by name and section. Where a spec and
the code disagree, **the code and the goldens are the evidence**.

The agents' reports, raw measurement runs and evidence copies that these files cite as "the author's
working notes" stay on the author's machine; they are not in this repository.

## Keeping this skill current

A change to SeedLab (feature, fix, gate result, measured number) goes in `references/history.md` with
its evidence. A new fact about *the game*
that SeedLab discovers belongs in the valheim-worldgen or valheim-modding references, not here - this
skill cites them rather than repeating them. Run
`python .claude\skills\valheim-modding\scripts\validate-kb.py` after editing.
