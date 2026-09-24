# The generator — `src\SeedLab.WorldGen`, `src\SeedLab.Render`

## What it does

`WorldGeneratorPort` is Valheim 1.0.15's `WorldGenerator` rewritten in C#, statement for statement.
Give it an int32 seed and a `worldGenVersion` and it answers the same three questions the game asks
of its own generator:

- `GetBiome(x, z)` — the `Heightmap.Biome` at a point;
- `GetBiomeHeight(biome, x, z, out mask)` — the terrain height in metres, and the Ashlands lava mask;
- `GetHeight(x, z)` — the height the game stores in a save for a location instance.

Everything else in SeedLab is built on those. `WorldField` (in `SeedLab.Render`) samples them over a
`FieldGrid`; `MapRenderer` paints a `WorldField`; `WorldSummary` and `IslandAnalysis` count it.

`FieldGrid.G12` is the grid that matters: 2048 × 2048 samples at 12 m, centre at the origin, sample
points `(j - 1024) * 12 + 6`. That is exactly what `Minimap.GenerateWorldMap` walks, which is why a
number measured on G12 can be compared with the game's own map cache and a number measured on any
other grid cannot.

The Unity functions the generator calls are ported too, because they are native code with no managed
source: `UnityPerlin` (`Mathf.PerlinNoise`), `UnityRandom` (`UnityEngine.Random`, xorshift128), and
`UnityMath` / `DUtils` (the two different length functions, the two different `LerpStep`s).

## What proves it correct

| evidence | what it shows |
|---|---|
| `dotnet run --project tests\SeedLab.Acceptance.Tests -c Release` | 32/32. Two worlds the game itself generated and wrote: biome 0 mismatches over ~5.1 M decodable minimap pixels, height 4,194,304/4,194,304 identical binary16 codes per world, 0 m worst difference. |
| `groundtruth\decoded\*.biome.u8`, `*.height.f32` | The oracles: the game's own minimap cache, decoded. Not a re-derivation — bytes the game wrote. |
| `dotnet run --project tests\SeedLab.Tests -c Release -- natives` | 11 checks against corpora captured from the running game: 262,780/262,780 `Mathf.PerlinNoise` samples, 276/276 `UnityEngine.Random` traces (1,980 draws — result bits and the state after each), `Mathf.FloatToHalf` resolved as ties **away** from zero (.NET's `(Half)f` gets 2 of the 4 midpoints wrong), 93/93 libm results identical between Mono and .NET 10, 429/429 hash vectors. |
| `dotnet run -c Release --project tools\SeedLab.GoldenCheck` | The generator's *private* state for three seeds, against what the game's own generator was holding: the five offsets, the two river seeds, the constructor's RNG draws, the lakes, rivers and streams in order, and the full rendered river-point grid. 8.5 M float32 comparisons, 0 differ. |
| `vseed selftest` | A fast subset of the above, runnable any time. |

One of the two worlds, `testworldclaude` (seed 319486907), is a **hold-out**: it was never used while
porting the biome and height code. It matched blind on biome and to 99.9998 % on height, and a last
one-ulp residual was then diagnosed on both worlds and closed. The fully independent check is the
third, fresh seed 75539276 (GoldenCheck). (The world's name is explained in the root README, under
*Where the evidence lives* and *Credits*.)

## Traps

- **Do not tidy an expression.** The shipped IL evaluates in `double` and truncates to `float` at the
  end of each source statement. Several sites differ from the obvious implementation by exactly one
  rounding — Meadows vs Plains, `DUtils.Length` vs `Vector2.magnitude`, the two `LerpStep`s,
  `MathfLikeSmoothStep`'s float-rounded return. Reordering any of them changes the world.
- **Game bugs are reproduced on purpose**, because the shipped world data depends on them:
  `GetBiomeArea`'s `Vector3` overload tests `(64,0,0)` twice, `FindRandomRiverEnd` calls
  `Range(0, count)`, the single-entry river cache goes stale, `PlaceStreams` builds and discards a
  second list. Fixing one is a regression.
- **One `WorldGeneratorPort` instance is not thread-safe** (that stale river cache). Use `Fork()` per
  worker; `WorldField.Sample` already does.
- **Every number carries its grid.** A figure from a 24 m grid is a *different measurement*, not an
  approximation of the 12 m one. The island component count moves ~45× over a 16× change of spacing.
  `vseed seed` prints the grid with every number for this reason.
- **The Ashlands and Deep North rings use different length functions in the game** — `DUtils.Length`
  (double-accumulated) for one, `Vector2.magnitude` (float) for the other. That asymmetry is real.
- Outside the 10,500 m water edge `GetBiomeHeight` returns the constant −400 m without evaluating
  terrain at all. `WorldField.Outside[]` marks those cells and every count excludes them.

## One architecture

Every gate above has only ever run on **x64**. On arm64 the risk is concrete rather than
theoretical: `Math.Sin/Cos/Atan2/Pow` and float evaluation may differ in the last bit, and a
last-bit difference at a biome threshold changes the world.

What ships instead of a promise: on a cold cache the first command that builds a world re-checks
this binary against the recorded corpora — `numerics` (271 checks) and `seedlab/natives`
(**263,780** values) — and **fails closed**. Demonstrated on 2026-09-23 by altering one recorded
hash by 1 in a copy of `groundtruth\natives`: `vseed seed 12345` exited 1 with
`seedlab/natives: 263779/263780 exact  FIRST FAILURE: GetStableHashCode("StoneCircle") ...` and the
three things to do about it. A pass stamps `<cache root>\selftest` and costs nothing again.
`--skip-self-test` turns it off, and `seed`, `at`, `map`, `locations` and `search` then warn that
bit-exactness is UNVERIFIED on that run; `--accept-unverified-platform` proceeds on an architecture
the gates have never covered.

The AVX2 path is bit-exact by construction (IEEE per lane, no FMA contraction) and was re-proved on
the scalar path with `DOTNET_EnableAVX2=0`: all five gates unmoved.

## Honest limits

Terrain is bit-exact. What the generator says is **all** it says: it does not know about creatures,
loot, ore under the ground, or anything a player does. See `docs\locations.md` for the things that
are placed but still not predictable, and [`limits.md`](limits.md) for the full list of what the
tool cannot promise — grid-relative metrics, bounded rates, and this untested-architecture gap.
