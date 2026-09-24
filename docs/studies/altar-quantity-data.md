# Data files written by the altar-quantity study

(The `coastline_length` study shares this folder; its files are `verdict.md` (published here as
`coastline-verdict.md`), `shore*`, `rank.py`, `dist.py`, `sweep.py`, `floors.py`, `join.py`,
`margin.py` and are not described here.)

All under `scratchpad\metrictruth\`. Nothing was written under `_ModSource\SeedLab`.

## How the sample was drawn

The tool's own scan order, so the sample is reproducible from seven numbers:

```
order      shuffled   (SeedLab.Search.Execution.Permutation, 4-round balanced Feistel, h = 16)
from       -2147483648
to          2147483647
count       4294967296   (a power of two, so no cycle walking ever happens)
key        0xA17A25EED10C5117
index      0 .. 4,999     (a contiguous prefix of the permutation; the radial sub-study used 0 .. 999)
seed(i)    ScanPlan.SeedAt(i) = (int)(from + Permutation.Shuffle(i, count, key))
```

The first eight seeds of the sample, as a check that a reimplementation agrees:

```
1544594208, -98710706, -1923311157, 1693137585, -454913440, 952884812, -880016200, 914782029
```

`seedlist.py` in this folder is a 12-line pure-Python reimplementation of that permutation; it was
written independently of the C# and produces the same eight.

The run is 10 chunks of 500 seeds, permutation indices 0..4,999: **5,000 seeds**.

## `out\run1\types.csv`

The 183 entries of the ordered placement list, in placement order, with the fields the study uses:
`idx,prefab,quantity,prioritized,unique,centerFirst,minDistance,maxDistance,minDistanceFromCenter,maxDistanceFromCenter,biome,minAltitude,group,minDistanceFromSimilar`.
`idx` is the index used by the binary below.

## `out\run1\counts-<from>-<to>.bin`

One file per 500-seed chunk. Little-endian throughout.

```
header   int32  magic      0x53565932  ("SVY2")
         int32  T          number of ordered types (183)
         int64  n          seeds this chunk was asked for
         uint64 key        the permutation key
         int64  start      the permutation index this chunk started at

record   int32  seed
         T x { uint16 placed; uint16 registered; uint16 attempts }   -- by types.csv idx
```

* `placed` is the game's own `placed` counter - the one the loop condition and the
  "Failed to place all ..." warning use (`ZoneSystem.GenerateLocationsTimeSliced`).
* `registered` is instances actually added to the world; it differs from `placed` only when
  `RegisterLocation` hit an occupied zone, which needs `maxRadius > 32`.
* `attempts` is the outer loop counter `i` when the loop exited, against a budget of 60,000 for a
  `m_prioritized` type and 12,000 otherwise. `attempts == budget` means the type ran out of tries.

## `out\run1\radii-<from>-<to>.csv`

Per chunk, per type: the minimum and maximum distance from the world origin over every instance the
chunk placed. Used for the annulus check in section 5.

## `out\within\within-*.csv`, `out\within\dists-*.csv`

The radial sub-study over the first 1,000 indices of the same permutation, for the eleven boss and
trader types: instance counts inside 300/500/1,000/1,500/2,000/3,000/4,000/5,000/6,000/8,000/10,500 m,
and every instance's distance, sorted.

## `out\packing.txt`

The geometric capacity table of section 1.4: 64 m zones intersecting each type's derived annulus, and
how many points fit on the annulus mid-radius circle at the type's `m_minDistanceFromSimilar`.

## `out\shortfall-diag.txt`

Full per-filter rejection breakdowns for the seeds where a boss or trader fell short.

## Tools

`driver\` (counts survey), `driver2\` (per-seed diagnostics), `driver3\` (radial study) are .NET 10
console projects that reference a **snapshot** of SeedLab's Release DLLs taken into `lib\` at
2026-09-23 07:00, so they are unaffected by the concurrent rebuilds of the repository. They were built
with `-p:BaseOutputPath=<scratch>\build{,2,3}\` and never wrote into the repository. `analyze.py`,
`report.py`, `named.py`, `shortseeds.py` read the binaries.
