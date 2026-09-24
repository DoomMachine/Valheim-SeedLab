# Locations — `src\SeedLab.Locations`, `tools\SeedLab.LocationLab`

## What it does

`LocationPlacementEngine` is a port of `ZoneSystem.GenerateLocationsTimeSliced`: the pass that
decides where every boss altar, trader, crypt, cave, village, tar pit and point of interest goes.
It walks the 183 enabled `ZoneLocation` entries **in the game's own order**, and for each one throws
candidate zones at the world until it has placed `m_quantity` of them or runs out of attempts.

It needs two things the decompiled code cannot give: the `ZoneLocation` table (serialized asset data,
see `docs\data.md`) and a 2048² biome-point grid plus the 32 alt-biome sector assignments, which
`WorldLocations.Build` computes from the generator.

`vseed locations <seed>` runs it. `vseed seed` runs a prefix of it for the boss/trader summary.

## What proves it correct

```
dotnet run -c Release --project tools\SeedLab.LocationLab -- gate
```

- **Fresh world**: 12,228/12,228 instances bit-identical to the game's own dump — same zone, same
  prefab, same x/y/z float32 bits. 178/178 prefabs reproduced exactly.
- **Two played worlds**, read out of their `.db2`: 12,314/12,314 and 12,287/12,287 bit-identical.
- The game's own log lines (`Crypt4 170 out of 200`) — all **29** per-type counters reproduced exactly.
- Alt biomes: 32/32 assigned to the same sectors, and the same under-minimum warning the game logged.
- The acceptance suite's T5 check compares `GetHeight` against the float32 `y` the save stores:
  0 differ, worst 0 float ULPs, including 1,462 and 1,526 instances sitting in a river field.

## Traps

- **`vseed locations` prints the generator's output, not what a player will find.** The game writes
  every candidate into the save and only some become buildings.
- **A `m_unique` type is a list of candidates, not a place.** Vendor_BlackForest has ten. The game
  keeps exactly one, and *which* one is not a function of the seed: the first candidate whose zone a
  player or a peer generates wins, and `RemoveUnplacedLocations` deletes the rest. SeedLab lists all
  ten and refuses to call any of them "the" trader. This is the single biggest difference from the
  seed sites, which usually print one.
- **Rotation is not predictable at all.** `Quaternion.Euler(0, Random.Range(0,16)*22.5, 0)` is drawn
  from the *ambient* `UnityEngine.Random` stream at zone-spawn time, which nothing seeds.
- **Therefore some dungeon interiors are not predictable either**: when a `DungeonGenerator` sits at a
  non-zero local XZ offset, the unseeded rotation moves it, and its position feeds its seed.
- **`gen y` is not the ground.** It is `WorldGenerator.GetHeight`, which is the value saved in the
  `.db2`. The ground a building finally rests on comes from the built heightmap and the location's own
  terrain edits.
- **A played world is not re-derivable.** Generated zones are excluded from a re-run, placed instances
  are kept, and the game's own `genloc` console re-run is not deterministic either.
- **The prefix rule.** Placement is global and one location per zone, so an earlier type can take a
  later type's zone. Asking for only the bosses still runs every type before them. `--type all` runs
  all 183: measured at 26.4 s for one seed here (4.7 s for the biome grid on 16 threads,
  21.7 s placement).
- **The order of the table is one observation.** Six `LocationList`s share `m_sortOrder = 3` and
  `List.Sort` is unstable, so a future dump of the same build could concatenate them differently. The
  observed order was checked against the game's own `orderedPrefabNames` and reproduced it 183/183.

## How many of a type a seed gets — bounded, not fixed

Measured over **5,000 uniformly drawn seeds** (2026-09-23), because "every seed has five boss
altars" is the kind of sentence that is easy to say and hard to support:

- The **seven boss altars never fell short** in the sample — Eikthyrnir 3, GDKing 4, Bonemass 5,
  Dragonqueen 3, GoblinKing 4, the Queen 5, Fader 3, all 5,000 of 5,000. That **bounds** the
  shortfall rate at **≤ 0.060 % (95 %, rule of three)** and does not make the count fixed.
  Dragonqueen has the thinnest headroom (2,117 attempts used of 60,000 at worst, a 28× margin
  against 123–811× for the others).
- **`DN_Bossroom` does fall short: 26 of 5,000 (0.520 %, CI 0.355–0.761 %)**, each having exhausted
  all 60,000 attempts — on the seed examined, the *altitude* filter rejected 335,886 of 359,829
  point tries. `Hildir_camp` 26/5,000 (0.520 %), `BogWitch_Camp` 1/5,000 (0.020 %).
- **Proved from the code:** `count <= m_quantity` always
  (`while (i < attempts && placed < m_quantity)`), and there is **no lower bound** — after the loop
  there is no retry, no filter relaxation, no fallback, only
  `ZLog.LogWarning("Failed to place all ...")`.
- **Proved absent:** `GoblinCamp2_1` exists in no seed — `m_biomeArea` is 0 while `GetBiomeArea`
  only returns Edge (1) or Median (2), so the filter always rejects. Confirmed 0 of 5,000.
- **Commonly absent** (alt-biome gated): TarPit2_1 92.4 %, TarPit1_1 73.5 %, TarPit3_1 51.4 %,
  StoneTowerRuins05_leet 48.1 %, GoblinHut03 34.0 %.

The search engine states the same distinction on every count goal: a code-provable absence is
labelled PROOF, a sampled one is labelled MEASURED with its interval, and the two are never mixed.

## What it cannot tell you

Creature spawns, loot, ore deposits inside a crypt, the layout of a rotated dungeon, which unique
candidate you will actually get, and anything about a world that has already been played.
`PlacementResult.NotPredictable` is the authoritative list and `vseed locations` prints it in full.
[`limits.md`](limits.md) collects it with everything else the tool cannot promise.
