# SeedLab.GoldenCheck — the port against the game's own internals

`tests\SeedLab.Acceptance.Tests` validates SeedLab against world **output**: minimap biome colours and
binary16 height codes the game wrote into a save. This tool validates it against the generator's
**internal state**, captured by `tools\SeedLab.Dumper` from the running game
(`goldens\worldgen-<seedHex>-<source>.json` + `-riverpoints.bin`, spec 04 §3.6.1).

That is a sharper test. The lake, river and stream lists are the product of thousands of chained
`UnityEngine.Random` draws, so an exact match proves the whole pre-generation pipeline — `FindLakes`,
`MergePoints`' averaging order, `PlaceRivers`' link search, both `PlaceStreams` passes — and not merely
that the end result looks the same.

```
dotnet run -c Release --project tools\SeedLab.GoldenCheck [-- <dumpRoot>]
```

With no argument it takes the newest `%USERPROFILE%\AppData\valheim-dumper\<gameVersion>-<hash>`.
Exit 0 only when every comparison is exact. It is **read-only**: it never writes to the dump.

## What it compares

| | |
|---|---|
| `[1]` | `m_offset0..4`, `m_riverSeed`, `m_streamSeed`, and the three `VersionSetup` constants |
| `[1b]` | the dumper's independent 7-draw replay, against our own `UnityRandom` |
| `[2a]` | `m_lakes` — count, order, and both coordinates of every entry |
| `[2b]` | `m_rivers` — `p0`, `p1`, `center`, `widthMin/Max`, `curveWidth`, `curveWavelength`, in order |
| `[2c]` | `m_streams` (pass 1 only; `Pregenerate` discards the DeepNorth pass's return value) |
| `[3]` | `m_riverPoints` — every cell, every point's `p.x`, `p.y`, `w`, `w2`, **including the order within the cell**, which `GetWeight`'s float accumulation depends on |
| `[4]` | `worldgrid-*.bin` float32 `GetBiome`/`GetHeight` grids, when the dumper was run with `grid=` |
| `[5]` | `locationinstances-<seedHex>.json` — every instance `y` is `GetHeight(x, z)` as float32 (T5 of spec 05), on a fresh world where nothing was pruned |

Every float is read from the golden's sibling `"bits"` object and compared as its IEEE-754 pattern.
The printed decimal is never used; it is lossy.

Order inside a river-grid cell is reported separately from content: a cell that holds the same points
in a different order is flagged as such, because that is a different defect from a wrong point.

## Result — 2026-09-23, dump `1.0.15-59f53fb5`

Three seeds, all exact, nothing differing:

| seed | source | lakes | rivers | streams | river cells | river points | floats compared |
|---|---|---|---|---|---|---|---|
| `75539276` (the fresh world) | in-world | 126 | 183 | 2,162 | 26,079 | 764,577 | 3,058,308 |
| `319486907` "hnBd9gJf2G" | menu | 119 | 161 | 2,059 | 23,262 | 677,094 | 2,708,376 |
| `-1772362158` "MWd8eV6svz" | menu | 111 | 140 | 2,135 | 23,380 | 675,579 | 2,702,316 |

Plus 12,228 / 12,228 float32 `GetHeight` samples bit-exact on seed `75539276`.

`75539276` is a **third independent seed** — the port had only ever been checked on the other two.

## Notes on the dump

- The `-menu` / `-world` token in a golden's file name is the **capture source**, not `World.m_menu`.
  All three captures carry `"menu": false`: a menu-mode capture builds a normal (non-menu) generator
  from the seed text. A reader must take the `menu` field, never the file name.
- This dump has no `worldgrid-*.bin`, so check `[4]` reports "does not exist" rather than passing. To
  produce one, run the dumper with `grid=`.
- The tool is deliberately not wired into the acceptance suite: the suite must pass from the repo
  alone, and this dump lives outside it in the user's profile.

Credits: see the root README - created and tested by DoomMachine; code, tests and docs written by Claude (Anthropic) in Claude Code.
