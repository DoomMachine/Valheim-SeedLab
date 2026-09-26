# Game data — `data\1.0.16-96cfc004\` (and `data\1.0.15-59f53fb5\`), `src\SeedLab.Data`

## What it does

Some of what SeedLab needs is not in Valheim's code at all. It is **serialized asset data**: values
that live in prefabs and are loaded at runtime. A code default is not the shipped value, and the gap
is not small:

| | in the IL | in the prefab |
|---|---|---|
| `Minimap.m_textureSize` | 256 | **2048** |
| `Minimap.m_pixelSize` | 64 | **12** |
| `ZoneSystem.m_locationVersion` | 1 | **32** |

Read the decompiled source and you get a map at a quarter of the game's resolution, sampled five
times too coarsely, with the wrong location version — wrong in exactly the way nobody notices.

So `data\` holds what the running game was actually holding, captured by the dumper
(`docs\dumper.md`), one folder per game build, named `<game version>-<first 8 hex of the
assembly_valheim.dll SHA-256>`:

- **`data\1.0.16-96cfc004\`** - Valheim 1.0.16, the build SeedLab is verified against. Dumper run 7,
  2026-09-26: one game session, all three modes, the asset dump taken in a fresh world, `Throwaway`
  (seed text `VRbvYNainE`, `BB9B7F96`).
- **`data\1.0.15-59f53fb5\`** - Valheim 1.0.15, kept. Runs 4 and 5 (2026-09-23) and run 6
  (2026-09-24).

Each holds 232 `ZoneLocation` entries in list order, 257 vegetation entries, 32 alt biomes, 186
location prefabs, what is inside each location and room prefab (`locationchildren.json`,
`roomchildren.json` - including every `Teleport` door and `Vegvisir`), the English localization
table, the prefab constants, the version constants, the seed-field limits, and the native goldens.
The 1.0.16 location, vegetation and alt-biome tables, prefab constants and prefab walk are identical
to 1.0.15's apart from the DATA-STAMP. `SeedLab.Data` loads a folder. `vseed data` reports it.

**Which folder is used.** `SEEDLAB_DATA_DIR` names one outright. Without it, SeedLab looks for `data\`
upwards from the working directory and from the binary, and when `data\` holds several folders it
uses the one whose name ends with the first 8 hex of the installed game's hash. If none matches, it
refuses and asks you to choose one with `SEEDLAB_DATA_DIR`.

Each folder also carries **`constraint-atlas.json`** (194,742 B, all 183 placed location types), the
versioned evidence the search engine refuses and warns from, and the count sample
(`count-sample.json` / `.bin`) that rules D4 and D5 warn from. They are **derived, not dumped**, and
no command in this repository rebuilds them. The 1.0.16 copies are the 1.0.15 files with their stamp
replaced, carried over only after the location tables were shown identical and the files had been
recomputed or compared on the 1.0.16 data (the folder's own README lists each check).

`ConstraintAtlas` finds the atlas from the data-dir environment variable first, then by walking up to
twelve levels from the working directory and from the binary (`ConstraintAtlas.cs`, `FindPath` and
`PickFrom`). Since 0.2.0a it prefers, among several folders, the one whose name ends with the
location table's build hash - the same rule as `SeedLab.Data`. Before that it took the first folder
that had an atlas, so with both folders in `data\` a 1.0.16 search ran with the 1.0.15 atlas, all its
bounds advisory. With no match it still takes the first folder with an atlas, and the stamp gate
reports the mismatch.

A stamp mismatch does not make the atlas wrong-but-usable: `QueryCheck.Downgrade` turns **every
feasibility refusal into a warning naming the mismatch**, because a refusal built on another build's
table would be a false claim about the seed space. The one refusal that survives is R13, a
contradiction between two goals in the query text, which does not depend on the game build at all.
**A missing atlas is different: it refuses every search**, terrain-only ones included, with a
message naming `constraint-atlas.json` (`SearchPreflight` turns the atlas's "not found" message into
a refusal).

The dumper last ran on 2026-09-26 (run 7, in Valheim 1.0.16) and **has been retired again since** —
`BepInEx\plugins\` holds no SeedLab plugin and F4 is free. See `docs\dumper.md` for the procedure and
for why a stamp's date is UTC while the files carry a local mtime.

The 1.0.16 folder's three runs wrote into one empty folder, so the dumper's own `manifest.json`
already lists every dumped file; one note was appended to it afterwards, saying where the derived
files came from. The 1.0.15 folder was built differently. Run 6 there was an assets-only run, so it
was imported file by file rather than copied over the folder: its ten asset tables and
`goldens/natives-hash.json` replaced the 2026-09-23 ones, and the four goldens of the world it was
taken in (`B83592B8`, seed text `8QHItAXH7v`) were added. A structural diff first showed that every
replaced asset file differs from its predecessor only in its stamp's date, apart from the three new
door fields per prefab in the two walks and, in the hash golden, the seed text of the world the dump
ran in. That folder's `manifest.json` is run 6's own manifest with `files[]` recomputed over the
merged folder - its last note says so - and `manifest-assets.json` is run 6's, unchanged.

Each folder's own `README.md` documents every file and is the authority; this page is the summary.

## The DATA-STAMP, and failing closed

Every dumped JSON file starts with a stamp naming the build it came from, so a file lifted out of the
folder is still self-identifying:

```
DATA-STAMP game-version=1.0.16 network=40 unity=6000.0.75f1
           assembly_valheim-sha256=96cfc004...5c6127
           dumped=2026-09-26 dumper=1.0.0 mode=assets schema=1
```

The files of one folder need not agree on `dumped=` or `mode=`: they are written by different runs.
The 1.0.15 folder holds stamps dated 2026-09-22, -23 and -24, from `assets`, `natives` and `worldgen`
runs; the 1.0.16 folder's three runs came from one session and differ only in `mode=`. What must
agree is the build: every file's two SHA-256s are checked against the manifest's
(`DataStamp.SameBuild`), and a file from another build is an error.

`SeedLab.Data` compares that SHA-256 against the installed `assembly_valheim.dll` on every run:

- **terrain answers** (`at`, `map`, `seed`, biome and height search) continue with a **warning** — they
  come from the seed and `worldGenVersion` alone and use nothing in this folder;
- **location answers** (`locations`, dungeons, traders, the landmark section) are **refused** — a table
  from another build produces coordinates that look right and are not.

That asymmetry is the whole design. `vseed data` prints the verdict in two lines you can read at a
glance.

## What proves it correct

- `manifest.json` carries the size and SHA-256 of every dumped file the tool reads, and `SeedLab.Data`
  verifies each one **before parsing**. In the 1.0.16 folder that is 25 of its 33 files; the rest are
  the three derived files, the four manifests and the folder's README. In the 1.0.15 folder it is 47
  of 67 since run 6; the rest are the derived files, the four manifests, the README and twelve
  2026-09-22 goldens nothing reads. An edit is indistinguishable from corruption and is treated as
  corruption. A file the manifest does not list is read UNCHECKED, which is why an import recomputes
  the list over the merged folder and never trims it.
- Every float member has a sibling `bits` object with the raw IEEE-754 pattern; the loader requires
  both and checks them against each other. The decimal is a convenience, the bits are the value.
- `vseed data --verify` re-checks the whole folder on demand: 8 of 8 on `data\1.0.16-96cfc004\`
  (2026-09-26). Two of its checks compare with the game's own log in `groundtruth\`, which is not in
  the repository, so on a clone they fail (`docs\game-data.md`, section 14).
- The tables are checked downstream by the location gate - for 1.0.16, 12,182 instances bit-identical
  in the `BB9B7F96` world (`Throwaway`) and every instance stored in the 1.0.16 saves of the three
  reference worlds; for 1.0.15, 12,228 in the `0480A34C` world and 12,216 in the `B83592B8` world run
  6 was taken in - and the goldens by the acceptance suite, the natives gate and GoldenCheck.

## Traps

- **Do not edit anything in `data\`.** To change it, re-run the dumper and write a **new folder beside
  the old one** named `<game version>-<first 8 hex of the assembly sha256>`.
- **Do not carry a derived file over to another build without proof.** The atlas and the count sample
  were reused for 1.0.16 only because the tables they come from were identical and the files were
  checked again on the new data. If an update changes a location table, they must be rebuilt, and the
  tools that built them are not in this repository.
- **Array order is semantic, member order is not.** `locations.json` is in `m_locations` order and that
  order decides every placement downstream, so it is an array and never an object keyed by name.
- **Enums are integers, never names**, and biomes are the `Heightmap.Biome` bitmask.
- **Nothing is omitted for being a default** — which is what makes a missing field an error rather
  than a shrug.
- **232 ≠ 186 ≠ 183 ≠ 200.** 232 rows exist; 186 have `m_enable`; 183 have `m_enable && m_quantity != 0`
  and are the list the placement run walks (and the number the game's own log reports); 200 carry a
  non-zero quantity whether enabled or not. Quoting the wrong one is an easy mistake.
- **The table order is one observation.** Six `LocationList`s share `m_sortOrder = 3` and `List.Sort`
  is unstable. The observed order reproduced the game's own `orderedPrefabNames` 183/183 in both
  dumps, but a future dump of the same build could differ, and neither would be corrupt.

## After a Valheim update

See the README's *After a Valheim update* section, which walks through the move to 1.0.16. Short
form: `tools\check-game-version.ps1` says the game changed (exit 2), location answers start refusing,
and the fix is a fresh dumper run into a new `data\<version>-<hash>\` folder beside the old one —
never an edit to the old folder. A new dump brings location answers back but does not show that
SeedLab still matches the new build; that takes the maintainer's ground truth, rebuilt from worlds the
new build generated:

- the three reference worlds re-created with the same names and seeds (`asdasdasd` / `MWd8eV6svz`,
  `testworldclaude` / `hnBd9gJf2G`, `ClaudeTestWold2` / `ClaudeTest`), and the fresh world the dump
  was taken in;
- the game's logs of the session that created them - `Player.log` and `Player-prev.log` in
  `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\`, and `BepInEx\LogOutput.log` - copied **before
  the game starts again**, because every start rotates or rewrites them.

The script that turned those into `groundtruth\` is not in this repository.
