# Game data — `data\1.0.15-59f53fb5\`, `src\SeedLab.Data`

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

So `data\1.0.15-59f53fb5\` holds what the running game was actually holding, captured by the dumper
(`docs\dumper.md`): 232 `ZoneLocation` entries in list order, 257 vegetation entries, 32 alt biomes,
186 location prefabs, what is inside each location and room prefab (`locationchildren.json`,
`roomchildren.json` - since 2026-09-24 including every `Teleport` door and `Vegvisir`), the English
localization table, the prefab constants, the version constants, the seed-field limits, and the
native goldens. `SeedLab.Data` loads it. `vseed data` reports it.

Since 2026-09-23 the folder also carries **`constraint-atlas.json`** (194,742 B, all 183 placed
location types), the versioned evidence the search engine refuses and warns from. It is stamped and
hashed like every other file, and `ConstraintAtlas` finds it from the data-dir environment variable
first, then by walking up to twelve levels from the working directory and from the binary
(`ConstraintAtlas.cs`, `FileName` / the walk at lines 372–399). A stamp mismatch does not make it
wrong-but-usable: `QueryCheck.Downgrade` turns **every feasibility refusal into a warning naming the
mismatch**, because a refusal built on another build's table would be a false claim about the seed
space. The one refusal that survives is R13, a contradiction between two goals in the query text,
which does not depend on the game build at all.

The dumper last ran on 2026-09-24 (run 6: the captions on the dungeon doors, `Teleport.m_enterText`,
and the Vegvisir pins) and **has been retired again since** — `BepInEx\plugins\` holds no SeedLab
plugin and F4 is free. See `docs\dumper.md` for the procedure and for why a stamp's date is UTC while
the files carry a local mtime.

Run 6 was an assets-only run, so it was imported file by file rather than copied over the folder:
its ten asset tables and `goldens/natives-hash.json` replaced the 2026-09-23 ones, and the four
goldens of the world it was taken in (`B83592B8`, seed text `8QHItAXH7v`) were added. A structural
diff first showed that every replaced asset file differs from its predecessor only in its stamp's date,
apart from the three new door fields per prefab in the two walks and, in the hash golden, the seed
text of the world the dump ran in. `manifest.json` is run 6's own manifest with `files[]`
recomputed over the merged folder - its last note says so - and `manifest-assets.json` is run 6's,
unchanged. The new world is a fresh hold-out: the location gate reproduces all 12,216 of its
instances bit for bit.

The folder's own `README.md` documents every file and is the authority; this page is the summary.

## The DATA-STAMP, and failing closed

Every file starts with a stamp naming the build it came from, so a file lifted out of the folder is
still self-identifying:

```
DATA-STAMP game-version=1.0.15 network=40 unity=6000.0.75f1
           assembly_valheim-sha256=59f53fb5...33adb1
           dumped=2026-09-22 dumper=1.0.0 mode=assets schema=1
```

The files of one folder need not agree on `dumped=` or `mode=`: they are written by different runs
(this folder holds stamps dated 2026-09-22, -23 and -24, from `assets`, `natives` and `worldgen`
runs). What must agree is the build: every file's two SHA-256s are checked against the manifest's
(`DataStamp.SameBuild`), and a file from another build is an error.

`SeedLab.Data` compares that SHA-256 against the installed `assembly_valheim.dll` on every run:

- **terrain answers** (`at`, `map`, `seed`, biome and height search) continue with a **warning** — they
  come from the seed and `worldGenVersion` alone and use nothing in this folder;
- **location answers** (`locations`, dungeons, traders, the landmark section) are **refused** — a table
  from another build produces coordinates that look right and are not.

That asymmetry is the whole design. `vseed data` prints the verdict in two lines you can read at a
glance.

## What proves it correct

- `manifest.json` carries the size and SHA-256 of every dumped file the tool reads - 47 of the
  folder's 67 since run 6; the rest are the derived atlas and count sample, the four manifests, the
  folder's README and twelve 2026-09-22 goldens nothing reads - and `SeedLab.Data` verifies each one
  **before parsing**. An edit is indistinguishable from corruption and is treated as corruption. A
  file the manifest does not list is read UNCHECKED, which is why an import recomputes the list
  over the merged folder and never trims it.
- Every float member has a sibling `bits` object with the raw IEEE-754 pattern; the loader requires
  both and checks them against each other. The decimal is a convenience, the bits are the value.
- `vseed data --verify` re-checks the whole folder on demand.
- The tables are checked downstream by the location gate (12,228 instances bit-identical in the
  `0480A34C` world, 12,216 in the `B83592B8` world run 6 was taken in) and the goldens by the
  acceptance suite.

## Traps

- **Do not edit anything in `data\`.** To change it, re-run the dumper and write a **new folder beside
  the old one** named `<game version>-<first 8 hex of the assembly sha256>`.
- **Array order is semantic, member order is not.** `locations.json` is in `m_locations` order and that
  order decides every placement downstream, so it is an array and never an object keyed by name.
- **Enums are integers, never names**, and biomes are the `Heightmap.Biome` bitmask.
- **Nothing is omitted for being a default** — which is what makes a missing field an error rather
  than a shrug.
- **232 ≠ 186 ≠ 183 ≠ 200.** 232 rows exist; 186 have `m_enable`; 183 have `m_enable && m_quantity != 0`
  and are the list the placement run walks (and the number the game's own log reports); 200 carry a
  non-zero quantity whether enabled or not. Quoting the wrong one is an easy mistake.
- **The table order is one observation.** Six `LocationList`s share `m_sortOrder = 3` and `List.Sort`
  is unstable. The observed order reproduced the game's own `orderedPrefabNames` 183/183, but a future
  dump of the same build could differ, and neither would be corrupt.

## After a Valheim update

See the README's *After a Valheim update* section. Short form: `tools\check-game-version.ps1` will say
the stamp no longer matches (exit 2), location answers start refusing, and the fix is a fresh dumper
run into a new `data\<version>-<hash>\` folder plus fresh ground truth — never an edit to the old
folder.
