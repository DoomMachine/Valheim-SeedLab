# The dumper — `tools\SeedLab.Dumper`

## What it does

A BepInEx plugin that reads Valheim's own loaded objects and writes them out as JSON. It is the only
way to get the things SeedLab cannot derive: the `ZoneLocation` / `ZoneVegetation` / `AltBiome`
tables, the prefab constants, the seed field's limits, and reference corpora for the three native
functions the port had to re-implement — `Mathf.PerlinNoise`, `UnityEngine.Random`,
`Mathf.FloatToHalf` — plus `WorldGenerator`'s private state per seed.

**It is meant to be installed, run, and then disarmed or removed.** It is not a mod to play with.

**To run it yourself**, follow [`game-data.md`](game-data.md): the step-by-step guide for readers who
have never installed a mod, from BepInEx to a checked `data\` folder and the dumper removed again.

## Its state on this machine, right now

It **has run seven times and is not installed.** Run 7 (2026-09-26, the first in Valheim 1.0.16: all
three modes in one session - `seedlab_natives` and `seedlab_worldgen` at the main menu, the asset dump
in the fresh world `Throwaway`) became `data\1.0.16-96cfc004\`, and the plugin was retired (moved out of
`BepInEx\plugins\` into the author's archive). Run 6 (2026-09-24, Valheim 1.0.15,
assets only: the dungeon doors' captions and the Vegvisir pins) was the last run for
`data\1.0.15-59f53fb5\`. **F4** is free.

```
retired 2026-09-26 (run 7)  SeedLab.Dumper.dll     FAEEE8D755746141B20DB99B515E6A2B74A31610BE3694DF9614A7C945F6F3F7
                            SeedLab.Contracts.dll  15504007B7D28F65885BEC41A799646FD92DA999048563CD726A2E66544E0B2F
                            dumper.enable          "all"
retired 2026-09-24 (run 6)  SeedLab.Dumper.dll     85F54A8566E3AC50CCB549521C332E7079D37AD712E5DC2625F64705B6103C7A
                            SeedLab.Contracts.dll  81056CC679D78575F29BEB13006D7BDDD34A42CEDF8F1112C3C351297D4FAF6F
                            dumper.enable          "assets"
```

Runs 6 and 7 are also the live check on the 2026-09-23 RNG fix: `Random.state` was identical at the
start and the end of run 6's asset dump, and of run 7's natives and worldgen dumps. (The defect: a
`RandomGuard` struct whose constructor never ran zeroed Unity's global generator and made every new
world's suggested seed `aaaaaaaaaa` for the rest of the session. `RandomGuard` is now a reference type with private constructors, a single
`RandomStateSafe.Restore` is the assembly's only writer of `Random.state` and it refuses to write an
all-zero state, and preflight gates hold the contract. The affected build is kept in
the author's archive.)

Making the data from your own copy of the game, step by step: [`game-data.md`](game-data.md).

The 1.0.16 data came out of run 7, stamped `dumped=2026-09-26`. The oldest 1.0.15 files came out of a
run stamped `dumped=2026-09-22` — that is the **UTC** date the plugin writes (`DateTime.UtcNow` in
`GameInfo.Stamp`); the files in a `data\` folder carry the local date they were written on, which can
differ from it by a day. `BepInEx\LogOutput.log` holds the session, until the game's next start
rewrites it; run 7's said:

```
[Warning:SeedLab.Dumper] SeedLab.Dumper 1.0.0 is ARMED in mode 'all'. Remove dumper.enable to make it inert again.
[Warning:SeedLab.Dumper] SeedLab.Dumper: native-function dump DONE -> ...\valheim-dumper\1.0.16-96cfc004
[Warning:SeedLab.Dumper] SeedLab.Dumper: world-generator dump DONE -> ...\valheim-dumper\1.0.16-96cfc004
[Warning:SeedLab.Dumper] SeedLab.Dumper: asset dump DONE -> ...\valheim-dumper\1.0.16-96cfc004
```

A statement that it was "run once on 2026-09-22 and then removed" was wrong on the second half at the
time it was written; it became true on 2026-09-23, by a different route - the RNG defect above.

**What being armed costs you** (when it is installed): it holds **F4** for the asset dump and registers `seedlab_status`,
`seedlab_dump`, `seedlab_natives` and `seedlab_worldgen`, so F4 is not free for another mod and a
stray press in a solo session starts a dump that hitches the game.

**Deleting `dumper.enable` makes it inert** without uninstalling anything (`Awake` logs one line and
returns before applying a patch). Moving the plugin folder out of `BepInEx\plugins\` removes it
entirely (the author keeps retired copies in an archive folder).
Either is the user's call — no SeedLab command does it for you, and `vseed clean` deliberately never
touches the game folder or the dumper's own output.

`tools\SeedLab.Dumper\README.md` is the operating manual and is the authority. The short version:

1. `dotnet build tools\SeedLab.Dumper\SeedLab.Dumper.csproj -c Release -p:DeployToGame=false -p:ValheimDir="<game folder>"`
   then `powershell -ExecutionPolicy Bypass -File tools\SeedLab.Dumper\preflight.ps1` — it must say `PASS`.
   Without `-p:ValheimDir` the project looks for the game where it is installed on the author's machine.
2. Copy `SeedLab.Dumper.dll` + `SeedLab.Contracts.dll` into
   `BepInEx\plugins\DoomMachine-SeedLabDumper\`, with a file `dumper.enable` containing `all`.
3. Start Valheim **solo**. At the **main menu**, F5 → `seedlab_natives`, then
   `seedlab_worldgen MWd8eV6svz hnBd9gJf2G`.
4. Enter a **throwaway single-player world**, press **F4**, wait for `asset dump DONE`.
5. Quit, delete `dumper.enable`, and move the plugin folder out of `BepInEx\plugins\` (the author
   keeps retired copies in an archive folder).

Output goes to `%USERPROFILE%\AppData\valheim-dumper\`. Copy it into
`data\<game version>-<first 8 hex of the assembly sha256>\`.

## What proves it correct

The dumper is not checked by tests — it is checked by **everything downstream agreeing with the game**:

- the 262,780 Perlin samples and 276 `UnityEngine.Random` traces it captured are what the ported
  natives are compared against, bit for bit;
- the location table it captured drives the placement engine, which reproduces every instance of the
  game's own fresh-world dump (12,182/12,182 in 1.0.16's, 12,228/12,228 in 1.0.15's) and every
  `placed N out of M` counter in the game's log of a world's creation (27 in 1.0.16's, 29 in 1.0.15's);
- the `seed-input.json` it captured (`characterLimit` 10, `Alphanumeric`) is what bounds the seed
  inverter, and `vseed space` re-verifies the arithmetic that rests on it;
- the preflight (`preflight.ps1`) checks the built plugin before it is ever put in the game folder.

If a dumped value were wrong, one of those comparisons would stop being exact.

## Traps

- **It refuses to run in multiplayer.** If you are a client, a dedicated server, or anyone is
  connected, it says so and disables itself for the session. That is deliberate.
- **Without `dumper.enable` next to the DLL it does nothing at all** — `Awake` logs one line and
  returns before applying any Harmony patch.
- **It never writes to a save folder.** Every write path goes through a guard that refuses `worlds`,
  `worlds_local`, `characters`, the biome-data `cache`, any Steam Cloud `remote` folder and the game
  install.
- **`seedlab_natives` must be run at the main menu**, or `seed-input.json` is skipped (it reads
  `FejdStartup.m_newWorldSeed` off the live component).
- **`seedlab_worldgen` swaps the menu's world generator**, so the menu terrain flickers while it runs.
  It is restored whether the dump succeeds or fails.
- **Use a throwaway world for the F4 asset dump**, not one you care about — not because it writes
  anything, but because the rule "never point a tool at a save you would miss" is cheap to keep.
- **Two files do appear inside the game folder and neither is written by the dump**: `dumper.enable`,
  which you create, and `BepInEx\config\DoomMachine.SeedLabDumper.cfg`, which BepInEx writes the first
  time the plugin launches armed. Both are yours to delete.
- **Timing figures in the plugin README were written before the first real run** and are estimates.
  Treat the `manifest*.json` files in each `data\` folder as the record of what the runs actually did.
- **The `dumped=` date in a DATA-STAMP is UTC**, taken from `DateTime.UtcNow`. Depending on the
  machine's time zone, the local timestamps of the same dump can fall on a different day — which is
  why the 1.0.15 data's `dumped=2026-09-22` need not match the local date on its files.
  The two SHA-256s, not the date, are the authority on which build the data describes.
- **`vseed clean` reports the dumper's output folder but never deletes it.** It compares every file
  in `%USERPROFILE%\AppData\valheim-dumper` against the copy in `data\` by SHA-256 and tells you
  when the folder is redundant (measured: 25 of 25 byte-identical, 48.77 MiB). Deleting it is a
  manual choice: it is outside SeedLab's cache root and it is the original every shipped copy came
  from.
