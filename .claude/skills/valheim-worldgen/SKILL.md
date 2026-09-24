---
name: valheim-worldgen
description: Verified knowledge of how Valheim builds and stores worlds - how the seed text becomes the integer seed, the world UID, biome placement and terrain height from WorldGenerator, rivers, zones, location placement (bosses, traders, dungeons), vegetation and dungeon seeds, world modifiers, and the on-disk formats of world (.fwl2/.db2/.chunk) and character (.fch) saves. Includes a read-only Python tool that hashes seeds and parses real saves. Use this whenever a task involves seeds, biomes, terrain height, where things spawn, world or character save files, map exploration data, world modifiers, or reproducing world generation outside the game - even if the user only asks "what's at these coordinates", "why is this biome here" or "where is my save".
---

# Valheim world generation and saves

Researched 2026-09-22 against **Valheim 1.0.15** by decompiling the shipped assemblies; every claim was
then checked by an independent refute-by-default verifier (442 claims checked across the five
research documents, 66 corrected in place). For how to read game code, the toolchain and pitfalls, use
the **valheim-modding** skill; run `valheim-modding/scripts/check-game-version.ps1` first —
world-generation details are exactly the kind of thing a game update changes.

## The essentials

- **Seed text → seed int:** `m_seed = seedText == "" ? 0 : seedText.GetStableHashCode()` (`World` ctor).
  `GetStableHashCode` is two interleaved djb2-xor lanes from 5381, combined `num + num2 * 1566083941`,
  unchecked 32-bit. No trimming or case folding. The same hash names prefabs (location hashes in saves).
  `python scripts/valheim_saves.py seed "<text>"` reproduces stored seeds exactly.
- **Generation reads only** the int seed, `m_worldGenVersion` (2 for new worlds) and the menu flag — never
  the seed text. Clients receive seed, seed text, UID and world-gen version from the server
  (`ZNet.RPC_PeerInfo`) and generate terrain locally; nothing about terrain shape is in the save.
- **World UID is random** (`name hash + Utils.GenerateUID()`): recreating a world with the same name and
  seed gives a new UID. Everything a character stores per world (exploration, pins, logout/home/death
  points) is keyed by it, in the character's `.fch`, never in the world save.
- **Biome = f(seed, world-gen version, x, z)**: distance bands from (0,0) plus low-frequency Perlin masks,
  tested in order menu → Ashlands → Ocean → Deep North → Mountain → Swamp → Mistlands → Plains →
  Black Forest (twice) → Meadows. Height = per-biome function × 200 m, sea level 30 m, rivers carved in.
  World radius 10 000 m, water edge 10 500 m.
- **"Ocean" is not "below sea level"**: `GetBiome` returns Ocean when the *base* height is ≤ 0.02 (about
  4 m before scaling), tested **after** Ashlands and **before** Deep North — so the Ashlands sea counts as
  Ashlands and the Deep North sea as Ocean, and low terrain elsewhere keeps its land biome. Exact table:
  `references/world-generator.md` §5.
- **In-game ground ≠ `WorldGenerator.GetHeight`**: the terrain builder blends the four corner biomes of
  each heightmap cell and applies terrain edits; the gameplay biome comes from a 12 m sector grid
  (`AltBiomeWorldData`). Use GetHeight for an estimate anywhere, the loaded heightmap for exact ground.
- **WorldGenerator runs off the main thread** (`HeightmapBuilder` thread) — a Harmony patch on
  GetBiome/GetBiomeHeight must be thread-safe. On a client `WorldGenerator.instance` is null from
  `ZNet.Awake` until `RPC_PeerInfo`; at the main menu it exists but is the menu world (seed 0).
- **Zones are 64 m squares** (`GetZone(p) = floor((p.x+32)/64), floor((p.z+32)/64)`). Only the server
  generates them, once, when a player first comes near; results are stored as ZDOs.
- **Locations** are placed once, on the server, at world load while the save's `locationsGenerated` is
  false; each type re-seeds `UnityEngine.Random` with `worldSeed + prefabName.GetStableHashCode()`. The
  result is **seed-deterministic but not seed-determined**: it also depends on the location list and its
  order (game version, mods) and on zones already generated; unique locations go to whichever candidate
  zone generates first. At most one location per zone. Instances are stored in the world `.db2`
  (server-side only; clients get map icons).
- **Vegetation** is seeded per zone and prefab (`worldSeed + zx*4271 + zy*9187 + prefab hash`), accepted by
  physics raycasts, stored as ZDOs. **Dungeon layouts** are seeded from world seed + position and saved
  to the generator's ZDO — never regenerated.
- **Reproducing generation outside the game is done.** It needs an exact port of the C# (mixed
  float/double maths, and Mono keeps some of it at double precision) plus re-implementations of Unity's
  native `Mathf.PerlinNoise` and `UnityEngine.Random` — both of which were recorded from the running
  game in 2026-09 and are now reproduced bit-for-bit. The tool is **SeedLab** (the repository these
  skills ship in, the **seedlab** skill): biomes, heights and location placement all match what the game wrote.
- **Straight, kilometre-long biome edges are vanilla**, not a port artifact: a degeneracy in Unity's
  Perlin gradient set on the masks' 1 km lattice. Predictable from the seed's offsets —
  `references/world-generator.md` 5.1.

## Saves

This build uses the **chunked world format** (world version 40+): each world is a folder
`<worldsRoot>\<World>\` with `_main.<N>.fwl2` (metadata: name, seed text, seed, UID, world-gen version,
world modifiers), `_main.<N>.db2` (net time, zones, location instances, events), `_main.<N>.chunks`
(index), `*.chunk` (ZDOs) and `_main.<N>.ok` (commit marker, written last). The game **deletes files it
does not expect** in a world folder. On this machine the user's saves are in **Steam Cloud**
(`<Steam>\userdata\<account>\892970\remote\worlds` and `\characters`); `LocalLow\IronGate\Valheim\worlds_local\<World>\`
holds only the minimap texture cache for a cloud world.

**Never write to save folders** from a script or mod experiment — Steam Cloud syncs them.

## Reference files

| File | Read it for |
| --- | --- |
| `references/seeds-and-world-files.md` | the seed hash, UID, version constants, world presets/modifiers, the exact byte layout of .fwl2/.db2/.chunks/.chunk/.fch, save/commit sequence, backups, save locations, caches |
| `references/world-generator.md` | initialization, biome logic with constants, height functions, rivers/lakes/streams, threading, determinism and what an external reimplementation needs |
| `references/zones-locations-vegetation.md` | zone maths and generation, the location placement algorithm and its filters, discovery and map icons, vegetation, dungeon seeds and room generation (which algorithm, room fit, connections), height helpers, global keys |

## Tools

**SeedLab** (the repository these skills ship in, skill **seedlab**) answers
any "what is at X in seed Y" question offline and exactly — biome, height, rivers, the map as a PNG,
every location instance, seed-text arithmetic, and a search over the whole 2^32 seed space. Its
`data\<version>-<hash>\` snapshot is also the authority for serialized prefab values this skill quotes
(location table, alt biomes, `m_locationVersion`, the minimap geometry). Use it before writing any new
offline world-generation code.

`scripts/valheim_saves.py` — read-only (opens files `rb` only); finds local and Steam Cloud saves itself.

```
python scripts/valheim_saves.py seed "MyWorldSeed"
python scripts/valheim_saves.py worlds
python scripts/valheim_saves.py characters
python scripts/valheim_saves.py locations "<world name>" [--name StartTemple] [--limit 25]
```

`locations` reveals where things are in a world — useful for development and testing, a spoiler for
play (the user built the Wayfinder mod precisely to avoid looking places up).

## Keeping this skill current

Record new verified findings about world generation or saves in the matching reference file with their
evidence. After a game update, re-verify
the version constants and save layouts first (they change with new biomes). Run
`python ../valheim-modding/scripts/validate-kb.py` after editing.
