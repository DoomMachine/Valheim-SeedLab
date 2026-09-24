# SeedLab dumper

A BepInEx plugin that captures the Valheim data SeedLab cannot get any other way: the location and
vegetation tables with every placement parameter, the alt-biome list, a handful of prefab constants,
and a reference corpus for `Mathf.PerlinNoise`, `UnityEngine.Random` and `Mathf.FloatToHalf`.

**It is meant to be installed, run once or twice, and removed again.** It is not a mod to play with.

**Never used a mod before?** [`docs\game-data.md`](../../docs/game-data.md) is the step-by-step guide:
installing BepInEx by hand or through a mod manager, building the dumper, the safety check, the run,
copying the data into SeedLab, checking it, and removing the dumper again, with every command
written out. This README is the full technical manual behind it.

Credits: see the root README - created and tested by DoomMachine; code, tests and docs written by Claude (Anthropic) in Claude Code.

---

## The short version, for when people are waiting

1. Build it and run the preflight (below). The preflight must say `PASS`.
2. Copy `SeedLab.Dumper.dll` and `SeedLab.Contracts.dll` into
   `BepInEx\plugins\DoomMachine-SeedLabDumper\`, and put a file called `dumper.enable` next to them
   containing the single word `all`.
3. Start Valheim **solo**. At the main menu press **F5** for the console and type `seedlab_natives`,
   then `seedlab_worldgen MWd8eV6svz hnBd9gJf2G`. The menu terrain behind the buttons will flicker
   while the second one runs — that is the world generator being swapped, and it is put back at the
   end whether the dump succeeds or fails.
4. Start a **freshly created throwaway single-player world** — not a saved one, and do not go
   exploring first. Once you are standing in it, press **F4** and wait for the top-left message
   `asset dump DONE. Files are in …`. Do not quit before it says DONE. (Why a fresh world:
   `RandomSpawn.Reset()` mutates the shared prefab assets as locations spawn, so a dump taken after
   exploring can see a SHORTER enabled array than the authored prefab has. See
   *"Run it in a freshly created world"* below.)
5. Read the last lines it prints. They are the `search.json` verdicts — one per name in
   `SoughtPrefabNames`, each saying FOUND or NOT FOUND in as many words.
6. Quit. Delete `dumper.enable`. The plugin is inert on the next launch; move its folder out of
   `BepInEx\plugins\` when you want it gone entirely (the author keeps retired copies in
   `_ModSource\_retired\`).

Nothing here touches your worlds or characters, and none of it can run while anyone is connected.

**How long it takes, measured.** It has been run: `BepInEx\LogOutput.log` from 2026-09-22 has the
whole session, and the game's own timestamped lines around the dumper's give these numbers on the
author's machine — `seedlab_natives` a few seconds (262,780 Perlin samples); `seedlab_worldgen`
about 2-3 seconds per seed without a grid (two seeds took 5 seconds, the restore included); the
asset dump about 8 seconds end to end, of which roughly 7 are the 186-prefab walk. A `grid=full12` is 4.2 million samples per seed and is the one thing here that
takes minutes.


### `seed-input.json` (main menu only)

`seedlab_natives` also records what the new-world UI will actually accept as a seed text:
`FejdStartup.m_newWorldSeed`'s `characterLimit`, `characterValidation` and `contentType`, read by
reflection off the live component (it is a `GUIFramework.GuiInputField`, i.e. a `TMP_InputField`).
Nothing in the game's code enforces a seed length - `FejdStartup.OnNewWorldDone` hands the text
straight to `new World(name, text)` - so this prefab value is the only thing that decides whether a
seed text SeedLab hands back is typeable. Seven characters reach every one of the 2^32 worlds, so any
limit of 7 or more costs nothing. **Measured on 1.0.15: `characterLimit` 10, `Alphanumeric`** (the
world-name field alongside it allows 20), so every seed SeedLab can name can be typed in.

It is only written when `FejdStartup.instance` exists, i.e. when you run `seedlab_natives` at the
**main menu**. Running it in-world skips the file and says so in the log rather than writing an empty
one.

---

## 2026-09-23: it zeroed `UnityEngine.Random`, and what was done about it

**Read this before installing it again.** Versions of this plugin up to and including
`SeedLab.Dumper.dll` sha256 `015bc685fab4157b4b3f7e0163d0d30873ba948b22cd426f4c51ab2354609978` had a
bug that silently destroyed the game's random number generator for the rest of the session.

**The symptom.** After installing the dumper, Valheim's new-world dialog offered the seed
`"aaaaaaaaaa"` every time. Moving the plugin out of `BepInEx\plugins` and restarting gave a normal
seed (`WlwDZTBFlF`), which settled where it came from.

**Why ten `a`s is a proof, not a coincidence.** `World.GenerateSeed()` builds ten characters of
`"abcdefghijklmnpqrstuvwxyzABCDEFGHIJKLMNPQRSTUVWXYZ023456789"[Random.Range(0, 59)]`, and
`UnityEngine.Random.Range(int,int)` is `min + (Next() % (uint)(max - min))`. Unity's `Next()` is
xorshift128 with shifts 11/8/19, and `{0,0,0,0}` is a **fixed point** of it: with all four state words
zero, `Next()` returns 0 forever. Ten `a`s in a row therefore means the global state was all zeros.
And because it is a fixed point, nothing recovers - every random draw in the session is dead: seeds,
effects, piece rotations, plant growth times, rune-stone text, weather. Nothing crashes, so it looks
like nothing is wrong.

**The cause.** `RandomGuard` was `internal readonly struct RandomGuard : IDisposable` with exactly one
constructor, `RandomGuard(int? initState = null)`, and every argumentless site was written
`using (new RandomGuard())`. For a **struct** the implicit parameterless constructor is always a
member, and C# overload resolution prefers a candidate that needs no default-argument substitution -
so `new RandomGuard()` bound to the *implicit* constructor and Roslyn emitted `initobj`. The
constructor body never ran, the saved state stayed `default(Random.State)` = `{0,0,0,0}`, and
`Dispose()` wrote those four zeros into the generator. Six sites were affected; the nine that passed a
seed compiled to a real `call .ctor` and were correct. That asymmetry is why reading the C# by eye,
twice, found nothing - only the IL showed it, in one Mono.Cecil pass. `ModeAssets`'s prefab walk and
`RoomWalk` are the two that ran in the author's session, which is exactly the asset dump they ran.

**What was fixed (2026-09-23).**

1. **`RandomGuard` is now a `sealed class` with private constructors** and two factories,
   `RandomGuard.Capture(site)` and `RandomGuard.Seeded(seed, site)`. Static factories on a *struct*
   would not have been enough - `new S()` compiles for a struct whatever constructors it declares.
   Only a reference type makes it a compile error, and it does:
   `error CS1729: 'RandomGuard' does not contain a constructor that takes 0 arguments`, proved by
   compiling a scratch copy with the old form put back.
2. **One writer, and it refuses zeros.** `RandomStateSafe.Restore(state, site)` is the only place in
   the whole assembly that assigns `UnityEngine.Random.state` (the preflight proves the DLL contains
   exactly one `set_state` instruction). If the state it is handed is four zeros it does **not** write
   it: it re-seeds from a non-deterministic source and logs an ERROR naming the call site. The zero
   test is `state.Equals(default(Random.State))`, which matters because a check that throws must never
   be able to skip a restore. `UnityEngine.Random.State` is a value type with exactly four
   `System.Int32` fields and declares no `Equals` of its own (checked with Mono.Cecil against the
   shipped `UnityEngine.CoreModule.dll`), so `ValueType.Equals` takes the fieldwise path for an
   all-primitive struct; run against the real type it gives `True` for two default states and `False`
   the moment one word differs.
   `RandomGuard.Dispose`, `NoDrawCheck.Around` and both `StateRoundTrip` restores all go through it.
3. **The capture is checked too.** If `Random.state` already reads as zeros when a guard is taken,
   something else has already killed the generator: it is reported as an ERROR and re-seeded, and each
   of `seedlab_dump`, `seedlab_natives` and `seedlab_worldgen` **refuses to start at all** on a zero
   state rather than recording garbage as ground truth. Any dump in which a zero was seen carries a
   note in its own manifest saying it is UNVERIFIED.
4. **It says what the state is.** The four state words are logged at Info level at dump start, at
   every section boundary and at dump end, with the section name (`SeedLab.Dumper: Random.state @
   assets: locations = [...]`). Reading the state draws nothing, so this is free. If this ever happens
   again the log says which section did it.
5. **The rule the plugin claims is now enforced by the build.** `preflight.ps1` has an 11-gate
   `== the RandomGuard contract ==` section, including a mechanical audit of *"a `RandomGuard` must
   never span a `yield`"*. That audit is subtler than it looks: Roslyn hoists **every** local of an
   iterator into the state-machine type, so a `<>s__N` field of the guard's type is not by itself a
   violation. What is: the guard being released from outside `MoveNext` - the iterator's own
   `Dispose` or a `<>m__FinallyN`, which Roslyn only emits when the `try` contains a `yield`. A second
   check (a `stfld <>2__current` inside the `try` whose `finally` releases the guard) is kept as a
   backstop but is unproven, because Roslyn lifts such a finally out of `MoveNext` and the first check
   fires instead.

**Proof that the gates work.** A scratch copy of the project was built with five real violations
planted in it - `RandomGuard` back to a struct, `new RandomGuard()` back at the prefab-walk site,
`RoomWalk`'s `using` made to span its `yield`, a raw `Random.state = ...` inside `ModeNatives.Run`, and
that mode's zero check disabled. The preflight named all five and exited `FAIL` with 6 failures. The
real build is `PASS, 422 checks / 436 with -SelfTest, 0 failures`. Nothing was planted in the real
tree.

**The build that fixed it** (2026-09-23): `SeedLab.Dumper.dll` sha256
`ad9386ea78e15bcbd5d135a63897dfdb7b0d4e3342825a7ee3b8fe205ff6b89d`, `SeedLab.Contracts.dll` sha256
`3c2c1f2460a400e8a3f6af0bdcba6a4c930210e8d74d2ef68bfc684e1ac39e74`; the affected copy is in
`_ModSource\_retired\DoomMachine-SeedLabDumper-20260923`.

**Current build** (2026-09-24, run 6 - adds the `teleports[]` / `vegvisirs[]` walk and
`waymarksCaptured`): `SeedLab.Dumper.dll` sha256
`85F54A8566E3AC50CCB549521C332E7079D37AD712E5DC2625F64705B6103C7A`, `SeedLab.Contracts.dll` sha256
`81056CC679D78575F29BEB13006D7BDDD34A42CEDF8F1112C3C351297D4FAF6F`, preflight -SelfTest PASS 484/0.
It is **installed and armed (`assets`) for run 6**, and is retired again once that data is verified.

**If you ever see `"aaaaaaaaaa"` offered as a new-world seed again** - from this plugin or any other -
that is an all-zero `UnityEngine.Random`. Quit and restart the game; the state does not recover on its
own, and any world created in that session got a seed that was not random.

---

## Is it safe to leave installed?

It is designed to be, but don't. Four things are true:

* **Without a `dumper.enable` file next to the DLL it does nothing at all.** `Awake` logs one line and
  returns *before* applying any Harmony patch, so there is nothing to interact with anything.
* **With the file, it still does nothing until you press a key or type a command**, and it refuses
  outright if this is not a solo session you host with zero connected peers. If you are a client, a
  dedicated server, or anyone is connected, it says so loudly and disables itself for the session.
  It also keeps asking about once a second **while a dump runs** — at every section boundary, inside
  the prefab walk, inside a grid job, and before every seed of the world-generator dump. A dump is
  seconds to minutes of frames and the plugin's coroutine survives a scene change (BepInEx's manager
  object is `DontDestroyOnLoad`), so "solo when it started" is not the same claim as "solo now". If
  the answer changes mid-run, the asset and native dumps stop where they are, say `STOPPED, not
  finished`, and leave a folder with no manifest in it — treat that folder as unusable. The
  world-generator dump stops before it starts the next seed (or on the spot, inside a long grid), puts
  the menu's generator back, and writes a manifest whose `notes` say how far it got and why it stopped.
* **Its dumps never go in the game folder or the save folders.** Output goes to
  `%USERPROFILE%\AppData\valheim-dumper\`. Writes to `worlds`, `worlds_local`, `characters`, the
  biome-data `cache`, any Steam Cloud `remote` folder, or the game install are refused by a guard that
  every single write path in the plugin runs through.
* **Two files do appear inside the game folder, and neither is written by the dump:**
  `dumper.enable`, which *you* create in `BepInEx\plugins\DoomMachine-SeedLabDumper\` to arm it (the
  plugin only ever reads it), and `BepInEx\config\DoomMachine.SeedLabDumper.cfg`, which BepInEx writes
  itself the first time the plugin launches **armed**, because that is when the plugin binds its
  settings. An unarmed launch writes neither. Both are yours to delete.

Still, remove it when you're done. It is one more thing that can go wrong in a session you care about.

---

## Install

1. Build it (from the repository root):

   ```
   dotnet build tools\SeedLab.Dumper\SeedLab.Dumper.csproj -c Release -p:DeployToGame=false -p:ValheimDir="<game folder>" -v quiet --nologo
   powershell -ExecutionPolicy Bypass -File tools\SeedLab.Dumper\preflight.ps1
   ```

   `<game folder>` is the Valheim install, the folder that holds `valheim_Data`. Without
   `-p:ValheimDir` the project looks for the game where it is installed on the author's machine.

   If BepInEx is not in the game folder (a mod manager keeps it in a profile folder), add
   `-p:BepInExCore="<profile folder>\BepInEx\core"` to the build, and give the preflight a stand-in
   folder: [`docs\game-data.md`](../../docs/game-data.md), sections 6 and 7.

   The preflight must print `PASS`. If it prints `FAIL`, **stop**: either a Valheim update renamed
   something the dumper reads — in which case the dump would be silently wrong — or the plugin has
   grown code it is not allowed to have.

   **What the preflight actually proves.** It reads the built DLL and the game's assemblies with
   Mono.Cecil. It never launches the game, so everything it says is about metadata and IL:

   * the plugin's GUID is `DoomMachine.SeedLabDumper` and it is gated to `valheim.exe`;
   * every `[HarmonyPatch]` target still exists in this build of the game;
   * every game field, property and method the dumper names — including the ones it reaches by
     reflection, where a rename would otherwise produce zeros instead of an error — still exists;
   * **the seeded stream still has the shape the offline replay assumes.** Names surviving an update
     does not keep the arithmetic true, so the preflight also counts calls in the game's own IL:
     `RandomSpawn.Randomize` makes exactly one `Random.Range` call; `RandomObject.Randomize` makes
     none directly and exactly one call to `GetWeightedObject`, which itself makes one;
     `ZoneSystem.SpawnLocation` reseeds three times and builds three component arrays; and
     `ZoneSystem.PlaceLocations` takes exactly one ambient draw (the location's rotation). A second
     draw added anywhere in there would shift every later entry and silently move which house has its
     maypole — this is the check that turns that into a `FAIL`;
   * **the same for a ROOM's stream** (added 2026-09-23): `DungeonGenerator.PlaceRoom` builds the same
     three arrays, reseeds three times, restores the ambient state exactly once, and runs two
     `RandomSpawn.Randomize` loops. `PlaceRoom` is overloaded, so those checks name the 5-argument one
     explicitly — the 3-argument overload touches no RNG at all, and counting it would make every one
     of those assertions true about the wrong method;
   * **the name-normalisation rule still has the shape the dump documents**: `Utils.GetPrefabName`
     (the `string` overload, selected by parameter *type*, because both overloads take one argument)
     makes exactly one `IndexOfAny` and one `Remove` call, and the `GameObject` overload is still a
     single forward to it. A version that looked names up in a dictionary would still be called
     `GetPrefabName` and would still compile, and every "not found" in `search.json` would quietly
     become untrustworthy;
   * **`Utils.GetEnabledComponentsInChildren` still excludes a component on the root transform**
     (one `Object::op_Equality`) as well as a disabled one (one `IsEnabledInheirarcy`). The
     enabled-vs-present warning in `locationchildren.json` compares against a count that applies the
     same root exclusion, so this is the clause that keeps it from crying wolf;
   * **no method anywhere in the assembly** calls any of **18 destructive file APIs**: `File.Delete`,
     `Move`, `Replace`, `Copy`, `Encrypt`, `Decrypt`, `SetAttributes`; `Directory.Delete`, `Move`;
     `FileInfo.Delete`, `MoveTo`, `Replace`, `CopyTo`, `Encrypt`, `Decrypt`; `DirectoryInfo.Delete`,
     `MoveTo`; `FileSystemInfo.Delete`. "Anywhere" is meant literally: the scan descends into nested
     types, which is where the compiler puts every iterator body and every lambda — 34 of this
     assembly's 61 types as built on 2026-09-23, and the preflight prints both counts every run, so
     the numbers in this file are never the ones you have to trust. A scan that skipped nested types
     (this one did, until 2026-09-23) would have been blind to most of the plugin's code;
   * every call to any of **20 file-writing APIs** is inside `DumpWriter`: `File.WriteAllText`,
     `WriteAllBytes`, `WriteAllLines`, `AppendAllText`, `AppendAllLines`, `AppendText`, `Create`,
     `CreateText`, `Open`, `OpenWrite`; `Directory.CreateDirectory`; `DirectoryInfo.Create`,
     `CreateSubdirectory`; `FileInfo.Create`, `CreateText`, `Open`, `OpenWrite`, `AppendText`; and the
     `FileStream` and `StreamWriter` constructors. Each of `WriteJson`, `WriteText`, `WriteBinary` and
     `WriteBinaryStreaming` reaches `PathPolicy.Reject` through `Resolve` → `AssertWritable`.
     (Both lists were widened on 2026-09-23. The old six-and-six lists let `File.Copy` and
     `Directory.CreateDirectory` through while this file claimed the scan covered every write — a
     tripwire nobody could step on. Read-only calls are deliberately absent: the plugin reads its
     trigger file and hashes the game's assemblies, and neither can damage anything.);
   * the forbidden-path list compiled into the DLL is exactly `worlds, worlds_local, characters,
     remote, cache`, and the policy, executed against a table of real save, Steam Cloud and game
     install paths, refuses every one of them while still allowing the dump folder;
   * the world-generator dump's restore of `WorldGenerator.instance` is reached from a `finally`, and
     `NoDrawCheck` puts `UnityEngine.Random.state` back inside a `finally`;
   * every assembly the plugin references resolves from the game folder.

   **What it does not prove**: that any dump is *correct*, that the plugin behaves at runtime, or that
   a reflection lookup the script does not list still resolves. It is a gate against renames and
   against unsafe file code, and nothing more.

   **Prove the tripwires before you trust a `PASS`.** `-SelfTest` adds three proofs: the member and
   call-count checks fire on **eleven** deliberately wrong probes (names that do not exist, renamed
   prefab-child and room-walk fields, a wrong `Random`-draw count, a wrong count asked of a *specific*
   overload, and a request for an overload that does not exist at all — which must fail rather than
   silently resolve to one that does); the IL scanner finds calls it is told to find in both top-level
   and nested types; and the destructive/write signature strings are run over the game's own
   `assembly_valheim.dll`, which does delete, move, create and write files — if our strings were
   misspelled they would match nothing there either, and "no bad calls" would mean "no working scan".

   As of 2026-09-23 (third pass, after the RandomGuard contract gates were added) a clean run is
   **422 checks, 0 failures**, and **436 with `-SelfTest`**. Those numbers move with every member the dumper starts reading, so read the line the
   script prints rather than this sentence.

   The scan's own tripwire has been proved twice, the only way that settles it — by building a
   **scratch copy** of the project with real violations planted in it and running this preflight
   against that DLL:

   * 2026-09-23, first pass: five planted calls (`File.Copy`, `File.Delete`,
     `Directory.CreateDirectory`, a `StreamWriter` ctor and a `FileStream` ctor, all outside
     `DumpWriter`). It named all five and exited `FAIL`. Two of them — `File.Copy` and
     `Directory.CreateDirectory` — were invisible to the lists as they stood before that day.
   * 2026-09-23, second pass: a `File.Delete` inside `RoomWalk.Run` (an **iterator**, so the call
     lands in the compiler-generated `RoomWalk/<Run>d__0.MoveNext`) and a `File.WriteAllText` inside a
     **lambda** in `ModeAssets.Run` (`ModeAssets/<>c__DisplayClass0_0.<Run>b__7`). The preflight named
     both by their nested-type names and exited `FAIL`, which is what proves the recursion into
     compiler-generated types is still live on the new code.
   * 2026-09-23, third pass, for the `RandomGuard` contract: five violations at once — `RandomGuard`
     reverted to a `struct`, `new RandomGuard()` put back at the prefab-walk site, `RoomWalk`'s
     `using` made to span its `yield`, a raw `UnityEngine.Random.state = ...` added inside
     `ModeNatives.Run`, and that mode's zero check disabled. The preflight exited `FAIL` with 6
     failures naming all five, including
     `SeedLab.Dumper.RoomWalk/<Run>d__0::<>m__Finally1 disposes the guard field` — the
     "never span a yield" gate firing on real Roslyn output.

   Nothing was ever deleted from the real tree to do this; the planted builds live in a scratch copy.

2. Copy **both** DLLs from `tools\SeedLab.Dumper\build\` into a folder of their own:

   ```
   BepInEx\plugins\DoomMachine-SeedLabDumper\
       SeedLab.Dumper.dll
       SeedLab.Contracts.dll
   ```

   (`SeedLab.Contracts.dll` holds the data shapes; the plugin will not load without it.)

3. **Arm it.** Create a file called `dumper.enable` in the same folder, containing exactly one word:

   | word | what it enables |
   |---|---|
   | `assets` | the asset tables + this world's generator state (the main one) |
   | `natives` | the PerlinNoise / Random / FloatToHalf evidence only |
   | `worldgen` | the main-menu, multi-seed world-generator dump |
   | `all` | all three (each still needs its own key or command) |
   | `groundtruth` | an accepted alias for `all` — the word spec 04 uses for the ground-truth run |

   The word is trimmed and lower-cased, so `All` and a trailing newline are fine. Anything else — or
   no file — and the plugin stays idle and says which word it found in `BepInEx\LogOutput.log`.

---

## Running it

### The asset dump — `assets`

1. Start or load a **single-player** world you don't mind being in. A throwaway one is ideal; nothing
   is written to it, but a fresh world is the cleanest data.
2. Wait for the world to finish loading (you are standing in it, not on the loading screen).
3. Press **F4**, or open the console with F5 and type `seedlab_dump`.

Optionally add a sample grid for this world: `seedlab_dump grid=coarse128` (also `findlakes`, `edges`,
or `full12` — 4.2 million samples, on the order of 100 MB). This only *reads* the world generator;
it does not replace it, so it is safe in-world. Default is no grid.

Configurable in `BepInEx\config\DoomMachine.SeedLabDumper.cfg` — `DumpKey` (F4 was the one function
key neither vanilla nor any mod in the author's install used), `OutputDir`, `WalkLocationPrefabs` /
`WalkPrefabChildren` / `PrefabsPerFrame` for the location prefab walk, `WalkRoomPrefabs` for the
dungeon/camp room walk, and `SoughtPrefabNames` for the names `search.json` resolves.

**`SoughtPrefabNames` has to be set before the run.** The walk `Release`s every prefab the moment it
has read it, so a name you think of afterwards cannot be looked for without dumping again. The default
is `piece_maypole,TreasureChest_meadows_01,TreasureChest_meadows_02` (the Maypole, and the two Meadows
chests that separate the Curious from the Mysterious axe head); it takes a comma-separated list.

**What you will see.** A top-left message *"SeedLab.Dumper: asset dump started. The game will hitch; do
not quit until it says DONE."* Then the game stutters for a second or two while it hashes
`assembly_valheim.dll` and `UnityPlayer.dll` and writes the tables. Then it counts through the prefab
walk (`prefab walk: 42/186`), one prefab per frame — the frame rate dips, the game stays playable —
then through the room walk (`room walk: 120/…`) the same way. Finally the search verdicts, one line per
sought name, and *"asset dump DONE. Files are in …"*. The whole thing took about 8 seconds in the 2026-09-22
run — **expect longer now**: the prefab walk gained the child walk on 2026-09-23 and runs one prefab
per frame instead of two, so 186 frames is its floor and the per-prefab work went up. It has not been
timed live yet; the progress line keeps counting and the solo/no-peers rule is re-checked between every
two prefabs. Progress lines also go to `BepInEx\LogOutput.log`, and to the console if you started it from
there.

**If it refuses**, the message says why. "not now - no world is loaded" just means try again once you
are in the world; the plugin stays armed. Anything mentioning peers or a server disables the plugin
for the session on purpose — restart the game to try again. The same is true of a refusal *during* a
dump, including one caused by leaving the world part way through: the conditions were checked and
true when the dump started, so a change of them is treated as the real event it is.

### The native-function evidence — `natives`

Console: `seedlab_natives`. Works at the **main menu** as well as in a world (in a world it also picks
up that world's real offsets and every location's stream seed, which is better data). This is the part
that settles how Unity's random number generator and Perlin noise actually behave on this machine, so
it is worth running once at the menu and once in a world.

### The multi-seed world-generator dump — `worldgen`

**Main menu only.** From the console:

```
seedlab_worldgen MWd8eV6svz hnBd9gJf2G
seedlab_worldgen MWd8eV6svz grid=coarse128
```

Up to 32 seeds at a time. `grid=` is optional: `none` (default), `coarse128`, `findlakes`, `edges`, or
`full12`. `full12` is 4.2 million samples **per seed** — only ask for it deliberately.

**What you will see.** The menu backdrop terrain visibly flickers and changes between seeds: that is
the world generator being swapped, and it is expected. Each seed prints a line with its lake, river
and stream counts — about 2-3 seconds per seed without a grid. This refuses to run with a world
loaded, because swapping the generator while terrain is loaded corrupts it.

**It re-asks that question before every seed**, not only at the start (fixed 2026-09-23). The
plugin's coroutine is not tied to the menu scene, so pressing "Start World" while a multi-seed run is
still going used to leave it swapping `WorldGenerator.instance` inside a loading world. Now it stops
before it starts the next seed — or on the spot, inside a long grid — writes its manifest with a note
saying how far it got and why it stopped, and restores. And the restore only ever touches the
generator while the one it installed is still the live one, because
`ZNet.Awake` installs the real world's generator when a world loads and writing over *that* would be
the very corruption being avoided.

**The backdrop is restored from a `finally`**, so it is put back whether the dump finishes, fails on
one seed, or dies half way through writing a file. If the restore itself were ever to fail, the log
says so in as many words and the run's `notes` record it — in that one case, restart the game before
starting a real world. (Before 2026-09-23 the restore only ran on the success path: one failed write
left the menu generating its backdrop from the dumper's throwaway world for the rest of the session.)

---

## What it writes

Everything lands in `%USERPROFILE%\AppData\valheim-dumper\<game version>-<first 8 of the
assembly_valheim hash>\`, so a game update can never overwrite an older dump.

```
manifest.json               what build this came from, file hashes, counts, and any warnings
manifest-assets.json        a per-run copy, so one mode's run cannot erase another's details
manifest-natives.json
manifest-worldgen.json
locations.json              every ZoneLocation, in list order: 47 keys each (every field the game
                            serializes, plus index, hashes, asset id and exact bit patterns)
vegetation.json             every ZoneVegetation, same
altbiomes.json              every AltBiome, with its added locations and vegetation
prefab-constants.json       ZoneSystem, Minimap and zone-Heightmap values that only exist in prefabs
version-constants.json      world / network / minimap-cache version numbers
seed-input.json             what the new-world seed field accepts (main-menu natives run only)
locationprefabs.json        per-prefab Location radii and every DungeonGenerator field (the prefab
                            walk). Since 2026-09-23 that is all 23 of the generator's readable
                            fields, not the 10 some feature happened to ask for: m_maxTilt,
                            m_minAltitude, m_perimeterBuffer, m_perimeterSections, m_zoneCenter,
                            m_zoneSize, m_doorTypes, m_doorChance, m_alternativeFunctionality,
                            m_tileWidth, m_gridSize, m_spawnChance and m_originalPosition were all
                            missing, and every one of them is read by GenerateRooms. fullFieldsCaptured
                            is false on a file written before that. parentLocalPosition is the
                            generator's PARENT-relative localPosition, which is what SpawnLocation
                            copies into m_originalPosition for the 18 custom-interior locations - it
                            is not the same quantity as localPosition.
locationchildren.json       what is INSIDE each location prefab: the ordered RandomSpawn and
                            RandomObject arrays that spend the location's seeded RNG stream, every
                            Container with its default drop table, and an index of the distinct
                            child names. Same walk, same load. The biggest file in the dump.
roomchildren.json           the same treatment for every dungeon/camp ROOM prefab in DungeonDB --
                            the crypt, cave, village and fort pieces a location prefab never
                            contains, plus each room's Room fields, theme, room list and enabled
                            flag, so a reader can tell which dungeon can contain it.
search.json                 for each name in SoughtPrefabNames: FOUND (with every host, path and
                            gate) or NOT FOUND, in as many words, plus what was searched and what
                            was not. The one file whose job is to make an absence explicit.
goldens/
  natives-random.json       Random: seeded states, the seven world-generator draws, and the traces
  natives-perlin.bin/.json  raw float32 PerlinNoise samples, plus an index of what is in the .bin
  natives-libm.json         Sin / Cos / Atan2 / Pow at the arguments world generation uses
  natives-half.json         Mathf.FloatToHalf over an adversarial float set
  natives-hash.json         every prefab name with its GetStableHashCode
  worldgen-<seed>-world.json          the LOADED world's generator (written by the asset dump)
  worldgen-<seed>-world-riverpoints.bin
  worldgen-<seed>-menu.json           a generator built at the MENU from the seed text
  worldgen-<seed>-menu-riverpoints.bin
  worldgrid-<seed>-<grid>-world.bin   and -menu.bin
  locationinstances-<seed>.json     the game's own placement output for this world
  altbiomes-assignment-<seed>.json  which alt biomes landed on which biome sectors
```

The `-world` / `-menu` token is not decoration: both dumps can produce a generator file for the same
seed, and without it whichever ran second would silently replace the first.

Every number that matters is written twice: as a readable decimal **and** as its exact bit pattern in a
`"bits"` object beside it. The tool reads the bits; the decimals are for you.

`manifest.json` ends with a `notes` list. **Read it.** That is where the dumper reports anything it is
unsure about — an empty location-instance list, a contaminated alt-biome assignment, a prefab it could
not load, a generator it could not restore.

### `locationchildren.json` — the inside of each location prefab

Added 2026-09-23. `ZoneSystem.SpawnLocation` opens a seeded RNG stream per location instance
(`Random.InitState(worldSeed + zone.x*4271 + zone.y*9187)`) and spends it on the prefab's own children:
one `Random.Range(0f, 100f)` per `RandomSpawn`, in array order, then one
`Random.Range(0f, totalWeight)` per `RandomObject`. **Both draws are unconditional** — a gated-off
entry still spends its draw, because `RandomObject.Randomize` calls `GetWeightedObject()` before it
looks at any gate. So the array order *is* the data: entry N consumes draw N, and with the arrays
captured a question like "does this particular house have its maypole?" becomes arithmetic SeedLab can
do offline, without the game.

The arrays are written in the order the game's own `Utils.GetEnabledComponentsInChildren<T>(asset)`
returned them — the dumper calls that very method rather than reimplementing its filter, so the
order is identical by construction rather than by assumption. Each entry carries its chance, its biome
/ elevation / lava / dungeon-theme gates, the position the game actually reads, and **what it
toggles**: the `m_OffObject` it switches on when it does *not* spawn, and the names of the `ZNetView`s
it switches on when it does. That last list is what lets a question about `piece_maypole` be answered
by name.

Also captured: every `Container` anywhere in the children, with its `m_defaultItems` drop table in
full — every entry's item **prefab name** and stable hash, stack range, weight and `m_dontScale` —
plus the index of the `RandomSpawn` that gates it, if any. And a flat index of every distinct child
GameObject name in the prefab, inactive included, so "which locations contain X" never needs another
dump.

**A drop table means CAN contain, never DOES contain.** It is a probability: `m_dropChance` decides
whether anything is rolled at all, `m_dropMin..m_dropMax` how many times, and each spin picks one entry
by weight. Nothing in this file — or anywhere in this dump — predicts what is in a *particular*
chest, because the contents are rolled at spawn time from the game's ambient, unseeded stream, not from
the location's seeded one. Every answer built on this data has to be phrased "the chest in WoodHouse7
can contain the Curious axe head", and never "contains".

Two things to read before trusting a draw index. Each prefab has a `warnings` array, and the manifest
`notes` say how many prefabs have a non-empty one. The common warning is an enabled-vs-present
component count mismatch: `SpawnLocation` ends by calling `Reset()` on every `RandomSpawn`, which
leaves that spawn's `m_OffObject` **inactive** on the shared prefab asset and never puts it back, so a
prefab whose location has already spawned this session can legitimately report a shorter array than the
authored prefab has. The other is `Location.m_biome`, which the same `Randomize` calls cache on the
shared asset the first time a biome-gated entry runs and never clear — on the 2026-09-22 dump 181 of
186 prefabs read `None`, and the five that do not are Mountain/DeepNorth locations that could not have
spawned near that dump's Meadows spawn point, which is the evidence that non-zero there means authored.
A dump taken from a freshly created world is the clean one.

(That first warning used to fire on healthy prefabs. `Utils.GetEnabledComponentsInChildren` drops a
component mounted on the **root transform** as well as any disabled one, and the "present" count did
not — so every clean prefab with a root `RandomSpawn` was reported as session-drifted and the reader
was told to distrust a good dump. Fixed 2026-09-23: both counts now exclude the root one, so the
warning means exactly one thing, which is that something was switched off.)

**Names are normalised the game's way.** Unity appends ` (1)` to duplicated siblings, and Valheim's
authored prefabs are full of them — so a child that is, to the game, `piece_maypole` can be called
`piece_maypole (1)` in the hierarchy. Everything in the game that asks "what prefab is this" runs the
name through `Utils.GetPrefabName`, which truncates at the first `(` or space
(`extraCharacters = { '(', ' ' }`, read from the shipped assembly). So every index and every search in
this dump matches on that normalised form, and keeps the raw spelling beside it: each entry carries
`name` **and** `normalizedName`, and each prefab carries both a `childNames` index (raw) and a
`prefabNames` index (normalised, listing the raw spellings it merged). **Ask identity questions of
`prefabNames`.** An exact-match query against raw names is a false negative for every duplicated child,
and a false "not found" is the worst answer this dump can give.

**Nine prefabs move their own children before the draw** — `MountainCave02`, `Mistlands_DvergrTownEntrance1`, `Mistlands_DvergrTownEntrance2`, `Mistlands_DvergrBossEntrance1`, `Hildir_cave`, `Hildir_crypt`, `Hildir_plainsfortress`, `TheHole01` and `MorkBorg`, the nine that set
`Location.m_useCustomInteriorTransform` in the 2026-09-22 dump (and the same nine set it on their
`DungeonGenerator`). When a location's root `Location` has
`m_useCustomInteriorTransform` **and** both `m_interiorTransform` and `m_generator`, `SpawnLocation`
mutates the shared asset between `Random.InitState(seed)` and the Randomize loop: the generator's
transform goes to `Vector3.zero`, and the interior transform is moved *and* rotated from the zone
centre, the instance position and the instance rotation. Anything underneath either one is therefore
evaluated at a position this dump cannot state — instance-dependent, for the interior one. Those
entries are flagged individually (`underGeneratorTransform`, `underInteriorTransform`,
`prefabPositionIsInstanceDependent`), the prefab carries `customInteriorTransformActive` and a count of
them, and a warning names it. **Draw order and draw count are unaffected**; only the elevation and lava
gates of those particular entries are unknowable offline.

Controlled by `WalkPrefabChildren` in `BepInEx\config\DoomMachine.SeedLabDumper.cfg` (default on). It
is part of the prefab walk, so `WalkLocationPrefabs` has to be on too, and turning it on forces **one
prefab per frame** regardless of `PrefabsPerFrame`: it visits every transform of every prefab, and a
frame between each one is what keeps the solo/no-peers re-check firing and the game responsive. The
dump prints the file's measured size as it writes it.

**Names, in this file and in `roomchildren.json`.** Each entry also carries who and what is inside -
`characters`, `traders`, `offeringBowls`, `runeStones` under `occupantsCaptured` (since 2026-09-23) -
and, from run 6 (2026-09-24), every `Teleport` and `Vegvisir` under their own `waymarksCaptured`. A
Teleport's `enterTextToken` is the dungeon's player-facing name (`$location_forestcrypt` = "Burial
Chambers"), for most dungeons the only place the game keeps it (Hildir's two are also pinned by her
map table). A Vegvisir's `locations[]` name the places it REVEALS,
never its host. Each flag is false, with empty arrays, when that walk did not run or threw - an empty
array under a true flag means "none here", under a false one "not known".

### `roomchildren.json` — the inside of each dungeon and camp room

Added 2026-09-23, and it exists because `locationchildren.json` on its own answers the wrong question.
A location that has an interior — a crypt, a cave, a Fuling camp, a Meadows village — contains a
`DungeonGenerator` and nothing else. The actual contents are **room prefabs**, which live in
`DungeonDB` and are instantiated at generation time; nothing in a location prefab's child tree names
them. So "which prefab contains `piece_maypole`" could come back empty from a complete, correct
location walk — and empty reads as *"this game has no maypole"*, which is exactly the wrong answer we
are trying not to give.

The rooms are reached through `DungeonDB`: `GetRooms()` for the list, `RoomList.GetAllRoomLists()` for
the grouping, `m_roomByHash` (reflected) for the hash dictionary's size, and `m_roomLists` /
`m_roomScenes` for the authored inputs. All four are written out, so you can see which of them this
build actually used rather than taking anyone's word for which field is populated.

Each room gets the same treatment a location prefab gets — the ordered `RandomSpawn` and `RandomObject`
arrays in the game's own order, every `Container` with its full drop table, the raw and normalised name
indices — plus the `Room` component's own 13 fields (`m_size`, `m_theme`, `m_enabled`, `m_entrance`,
`m_endCap`, `m_divider`, `m_endCapPrio`, `m_minPlaceOrder`, `m_weight`, `m_faceCenter`, `m_perimeter`,
`m_placeOrder`, `m_seed`) and the metadata that says **which dungeon or camp can contain it**: its room
list, its `RoomData.m_theme` and its `RoomData.m_enabled`.

**And, since 2026-09-23, its `RoomConnection`s** — `connections[]`, in
`GetComponentsInChildren<RoomConnection>(false)` order, which is the order and the array the game's own
`Room.GetConnections()` produces. Each carries the **parent-relative** `localPosition`/`localRotation`
that `CalculateRoomPosRot` actually reads, the same pair measured from the `Room` anchor for when the
two differ, `directChildOfRoom`, and `m_type` / `m_entrance` / `m_allowDoor` /
`m_doorOnlyIfOtherAlsoAllowsDoor`. Without them nothing can reproduce the Dungeon algorithm — every
crypt, cave, Dvergr town, Hildir dungeon, the Hole and MorkBorg reach every room through a connection.

`connectionsCaptured` says whether the array is a reading. It is **false** on a file written before
that date, on a room whose walk threw, on a room with no `Room` component, and on a room where the
game handed back a null connection — because an empty array and "not captured" are different facts,
and a room with genuinely no connections cannot be placed by the Dungeon algorithm at all. A reader
that needs connections must test the flag, not the array's length.

`Room.GetConnections()` is deliberately **not** called: it caches into the component's private
`m_roomConnections` on a shared asset the game keeps for the rest of the session. The walk makes the
same call `GetConnections()` would make, and leaves nothing behind.

Three things about a room's stream that a location's does not have:

* **It is filtered on `RoomData`, not on the `Room` component.**
  `DungeonGenerator.SetupAvailableRooms` keeps a room when
  `(roomData.m_theme & generator.m_themes) != None && roomData.m_enabled`. The component's own
  `m_theme` / `m_enabled` are written too, and a disagreement between the two is reported as a warning
  rather than smoothed over.
* **The seed comes from where the room landed.** `PlaceRoom` uses
  `(int)v.x*4271 + (int)v.y*9187 + (int)v.z*2134` over the room's placement position — note the
  `+2134`, where `DungeonGenerator.GetSeed` uses `-2134`; they are different constants — plus the
  generator's own `GetSeed()` when `m_addBaseSeedToRandomSpawn` is set. That position comes out of the
  dungeon layout, which is rolled once from `GetSeed()` and then **saved to the generator's ZDO**, so a
  layout is never re-rolled. This file therefore says what a room **can** contain and under which gate;
  it does not predict a particular dungeon.
* **The gates swap over.** `PlaceRoom` calls `Randomize(pos, null, this)` and `SpawnLocation` calls
  `Randomize(pos, location)`. In `RandomSpawn.Randomize` the biome gate is `loc != null && ...` and the
  theme gate is `dg != null && ...`, so `m_requireBiome` is **inert inside a room** and
  `m_dungeonRequireTheme` is **inert inside a location**. Both are recorded either way; the docs on
  each field say which one is live where.

Positions are anchored on the **`Room` component's** transform, because that is what `PlaceRoom`
measures from (`Inverse(room.rotation) * (child.position - room.position)`). `roomComponentOnRoot` says
whether that is the asset root, which it normally is.

Controlled by `WalkRoomPrefabs` (default on). One room per frame, same as the location child walk and
for the same reason. If `DungeonDB.instance` is null — it only exists in the `main` scene, after its
`Start()` has run — the file is written with a `skipped` reason instead of silently coming out empty,
and every "not found" in `search.json` is marked inconclusive.

### `search.json` — the file that refuses to be silent

Every other file in the dump is a table, and a table cannot tell you which of three things a missing
name means: that it is not in the game, that it is somewhere the walk never went, or that it was in a
prefab that failed to load. After spending a game launch on a dump, being left to guess between those
three is the failure this file exists to prevent.

For each name in `SoughtPrefabNames` it writes:

* `found` — **true or false, always present**, never an omission;
* every hit: which location or room hosts it, the hierarchy path, the raw name that matched, whether a
  `RandomSpawn` gates it **and at which index**, and that spawn's chance and every gate
  (`requireBiome`, `minElevation`, `maxElevation`, `notInLava`, `dungeonRequireTheme`). A
  `gatedByRandomSpawnIndex` of `-1` means nothing gates it: it is unconditional in that prefab;
* `coverage` — what was searched and, separately, what was **not**: a walk that was switched off, a
  prefab that failed to load, a subtree that threw;
* `inconclusive` — set when `found` is false **and** coverage had a real gap. A NOT FOUND with this
  flag set is not evidence of absence. `standingLimitations` is kept apart from `notSearched` on
  purpose: what no dump can ever reach is not a defect of this run, and counting it as one would make
  every verdict inconclusive forever;
* `existsInZNetScene` — whether `ZNetScene.GetPrefab(name)` finds a registered prefab by that name.
  This separates *"the name is wrong"* from *"the piece exists in this build but nothing in world
  generation places it"*, which is how a player-built piece behaves;
* `verdict` — one sentence saying all of the above in English. The same sentences are printed to the
  console and copied into `manifest.json`'s `notes`, because you will be sitting in front of a running
  game and will read the console line first.

It searches location prefabs and their children, room prefabs and their children, the weighted options
of every `RandomObject` (which are prefab *references*, not children, so they never appear in a name
index), every `Container` drop table, and the location and vegetation tables. Matching is on the
normalised name, including for the names you configure, so `piece_maypole` finds `piece_maypole (1)`
and a config entry typed as `piece_maypole (1)` still searches for the right thing.

### Run it in a freshly created world

Not a saved one, and do not go exploring first. `ZoneSystem.SpawnLocation` and
`DungeonGenerator.PlaceRoom` both end by calling `Reset()` on every `RandomSpawn` of the **shared
prefab asset**, which is `SetSpawned(true)`, which leaves that spawn's `m_OffObject` inactive and never
puts it back. Anything under a switched-off off-object then drops out of
`Utils.GetEnabledComponentsInChildren`, so a dump taken after a session of exploring can see a
**shorter enabled array than the authored prefab has** — and that array is the draw budget the whole
offline replay is built on.

It is minimal, not zero: even a brand-new world has already run `SpawnLocation` for the zones around
the spawn point — StartTemple and whatever Meadows locations landed nearby — and `WoodVillage` /
`WoodFarm` are `DungeonGenerator` camps, so a few MeadowsVillage and MeadowsFarm rooms may have been
through `PlaceRoom` as well. The per-entry `warnings` say which prefabs were touched; read them before
replaying a stream from the file.

---

## After a Valheim update — the one time you will need this again

This is why the plugin still exists. Everything in `data\<version>-<hash>\` was read out of one build
of the game, and the update that changes `assembly_valheim.dll` is the update that can add a location,
raise a quantity or reorder a `LocationList`.

**How it surfaces.** SeedLab does not guess. `src\SeedLab.Data` compares the shipped DATA-STAMP with
the SHA-256 of the installed `valheim_Data\Managed\assembly_valheim.dll`, and:

* `vseed data` says `MISMATCH - the installed game is a different build`;
* anything that names a location, a dungeon or a resource **refuses**, with a message that explains
  that "a location table from another build gives coordinates that look right and are not";
* terrain answers — `vseed at`, `vseed map`, `vseed seed`, the heights, the biomes — still work and
  print one warning line, because they come from the ported `WorldGenerator` and the seed alone, and
  no dumped asset takes part.

**The fix is to run this plugin again.** In order:

1. From the repository root, `powershell -ExecutionPolicy Bypass -File tools\check-game-version.ps1`
   (add `-ValheimDir <game folder>` if it does not find the game). Exit 2 means the game changed and
   exit 3 that it found no game; it prints the installed `assembly_valheim.dll` hash, and the game
   version and Steam build id when it can find them.
2. Rebuild and re-preflight the dumper:
   `dotnet build tools\SeedLab.Dumper\SeedLab.Dumper.csproj -c Release -p:DeployToGame=false -p:ValheimDir="<game folder>" -v quiet --nologo`
   then `powershell -ExecutionPolicy Bypass -File tools\SeedLab.Dumper\preflight.ps1` (*Install*
   above says what `-p:ValheimDir` is for).
   **A `FAIL` here is the point of the exercise**: it names the game member that was renamed, and the
   dumper must be fixed before it runs, or the new dump is silently wrong.
3. Copy `SeedLab.Dumper.dll` and `SeedLab.Contracts.dll` into
   `BepInEx\plugins\DoomMachine-SeedLabDumper\`, and create `dumper.enable` containing `all`.
4. Start Valheim solo. At the **main menu**: `seedlab_natives`, then
   `seedlab_worldgen MWd8eV6svz hnBd9gJf2G` (same two seeds as last time, so the new dump can be
   diffed against the old one).
5. Create a **throwaway single-player world**, stand in it, press **F4**, wait for `DONE`.
6. Quit, **delete `dumper.enable`**, and move the plugin folder back out of `BepInEx\plugins\`.
7. Copy the new `%USERPROFILE%\AppData\valheim-dumper\<version>-<hash8>\` folder into SeedLab's
   `data\`. Leave the old folder where it is: when `data\` holds several dumps, SeedLab picks the one
   whose hash matches the installed game, and keeping the old one is what makes the two diffable.
8. `vseed data` — it must now say `MATCH`. Then re-run the gates:
   `dotnet run --project tests\SeedLab.Acceptance.Tests -c Release` and
   `dotnet run -c Release --project tools\SeedLab.LocationLab -- gate`. A location count that moved
   is a real change in the game, not a bug in the port; the `.db2` oracles in `groundtruth\` were
   written by the old build and need re-taking too.
9. Update the notes. The maintainer's working notes are published in this repository under
   `.claude\skills\` (see `.claude\README.md`): the new build's facts go in
   `valheim-modding\references\` - its stamp is the `KB-STAMP` line in `environment.md` - and what
   changed in SeedLab goes in `seedlab\references\history.md`.

If the game did **not** update and `vseed data` still says `MISMATCH`, the data folder is the wrong
one rather than the game being new — check `SEEDLAB_DATA_DIR` before re-dumping anything.

---

## Remove it

1. Delete `dumper.enable` — the plugin is inert immediately on the next launch, and applies no
   patches at all.
2. Move `BepInEx\plugins\DoomMachine-SeedLabDumper\` out of `BepInEx\plugins\` - move, don't delete
   (the author keeps retired copies in `_ModSource\_retired\`).
3. `BepInEx\config\DoomMachine.SeedLabDumper.cfg` can stay or go; it does nothing on its own.

Your dumps in `%USERPROFILE%\AppData\valheim-dumper\` are untouched by any of this. So are your worlds
and characters — the plugin has never been able to write there.

---

## If something goes wrong

Nothing in this plugin is allowed to take your session with it: every hook and every step is wrapped,
and a failed dump prints `FAILED — see BepInEx\LogOutput.log` and stops. If a dump fails partway, treat
the whole folder as unusable and run it again — a partial dump is not a smaller dump.

`seedlab_status` in the console prints the arming mode, the output folder, whether each dump is allowed
right now and why not, and the two DLL hashes.
