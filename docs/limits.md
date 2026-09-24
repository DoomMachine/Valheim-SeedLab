# What is **not** true of this tool

SeedLab's terrain is bit-exact against what Valheim itself wrote, and that claim is checked by five
gates. This page is the other half, in the same voice: everything the tool cannot do, cannot know,
or knows only within a bound. Nothing here is a bug report — it is the shape of the problem, and the
tool prints most of it at you while it runs.

Contents: [numbers that carry a grid](#numbers-that-carry-a-grid) ·
[answers that are bounded, not exact](#answers-that-are-bounded-not-exact) ·
[things the seed does not decide](#things-the-seed-does-not-decide) ·
[rates that are bounded, not proven](#rates-that-are-bounded-not-proven) ·
[one architecture](#one-architecture-and-what-the-self-test-does-about-it) ·
[not implemented](#not-implemented)

---

## Numbers that carry a grid

Anything counted or measured over an area comes off a **sampling grid**, and the grid is part of the
number. A figure at 24 m is a *different measurement* from the same figure at 12 m, not an
approximation of it: the raw island component count moves about **45×** over a 16× change of
spacing. `vseed` prints the grid beside every such figure, and every results record carries
`measured_at_grid_m` for each goal.

**Eight metrics are not comparable across grids at all.** The results record marks each of them
`"grid_comparable": false` and carries a one-sentence `grid_note` saying why:

`biome.nearest_distance` · `biome.largest_patch_area` · `world.island_count` ·
`world.largest_island_area` · `world.spawn_island_area` · `world.nearest_land_distance` ·
`world.shore_area_within` · `world.highest_peak`

Measured examples, from the resolution study:

- **`spawn_island_area` is the worst-behaved metric in the tool.** Median |relative error| against
  G12 is **14.0 % at G24, 43.4 % at G96, 70.8 % at G384**, and no margin at any coarse grid removes
  the false negatives (a 50 % margin at G24 still lost 8 of 802 true matches). Coarse grids merge
  islands across straits.
- **`highest_peak`** is an extremum: a `>= 450 m` goal loses **31.7 % of true matches at G48** and
  **82.3 % at G96**.
- **`nearest_distance` to Mountain** needs a margin of **927 m at G24 and 3,015 m at G384** — larger
  than the distances people actually ask for, so a coarse answer is not an approximation of the
  exact one. Mountain is the exception: for Meadows, Black Forest, Plains, Swamp and Mistlands the
  margin is **≤ 46 m**.
- **`shore_area_within`** is defined as land within a **100 m** band of water. Below about 100 m of
  grid spacing that band cannot be resolved, and above it the metric is 0 in every seed. Asking for
  it on a grid that cannot resolve it is a **usage error**, not a claim about seeds.

What the engine does about it: under `screen: auto` (the default) a query's coarse-unsafe goals are
measured at the game's own 12 m grid whatever `--grid` says, the survivors of a coarse screen are
**re-measured** before anything is written, and every record carries `screened_at_grid_m` beside the
fine-grid numbers. Verified on this build: 20 of 20 records from `gentle-start` and
`coastal-builder` carried `"screened_at_grid_m": 24` beside `"grid": 12` (2026-09-23).

**Two metrics were removed** because they were grid-relative *and* could not tell Valheim worlds
apart: `coastline_length` (p90/p10 = 1.065 over 2,560 seeds — the most coastal world in 2,560 has
8 % more coastline than the least) and `deepest_point`. Using either by name gives a refusal that
explains the measurement and names `world:shore_area_within` as the replacement, never "unknown
metric".

## Answers that are bounded, not exact

- **A distance to a biome is the centre of the nearest cell of that biome**, so it carries half a
  cell diagonal of slack — **8.485 m on the game's own 12 m grid**. When a distance is at or below
  that floor the record says so in a `censored` field rather than printing a number that looks
  measured. About 70 % of seeds hit this on `nearest_land_distance` at G12.
- **Where the resolution study sampled a (metric, grid) pair, the record carries
  `measured_median_rel_err_at_grid` and its `error_source`. Where it did not, the record says
  nothing** rather than interpolating. Today that means measured error rows exist for bulk areas,
  largest island, spawn island, largest patch and shore, and for nothing else.
- **The new per-goal fields are JSONL-only.** The CSV writer still emits value / pass / score /
  bounded per goal and does not carry `measured_at_grid_m`, `grid_comparable`, `grid_note`,
  `censored` or the measured error. Adding columns would change the shape of every CSV already
  written, so the gap is named here rather than papered over. **Use `.jsonl` if you care about how a
  number was measured.**
- **`--dry-run` is an upper bound, not a forecast** — see `docs\measurements.md`.
- **`--approx` allows heuristic prefilters and this build ships none**, so it currently only stamps
  `approx` on every record.

## Things the seed does not decide

These are not gaps in the port. Nothing could compute them from a seed, and any site that prints
them is printing a guess.

- **Which candidate of a `m_unique` location survives.** Haldor (`Vendor_BlackForest`), Hildir and
  the Bog Witch have **ten candidate positions each**. The game keeps exactly one, and which one
  depends on the first candidate zone a player *or a peer* generates, after which
  `ZoneSystem.RemoveUnplacedLocations` deletes the rest. SeedLab lists all ten and refuses to name
  one; the web map draws them as a **candidate set**, never as a position.
- **Any rotation.** `Quaternion.Euler(0, Random.Range(0, 16) * 22.5f, 0)` is drawn from the
  *ambient* `UnityEngine.Random` stream at zone-spawn time, and nothing seeds that stream.
- **Therefore the interior of a dungeon whose generator sits off its location's axis** (12 of the 21
  dungeon-bearing prefabs): the unseeded rotation moves the generator, and its position feeds its
  own seed.
- **The ground a building finally rests on.** `gen y` is `WorldGenerator.GetHeight`, which is the
  value the save stores; the real ground comes from the built heightmap and the location's own
  terrain edits.
- **Anything about a world that has already been played.** Generated zones are excluded from a
  re-run, already-placed instances are kept, and the game's own `genloc` re-run is not deterministic
  either.
- **Not modelled at all:** creatures and spawns, loot, ore under the ground, dungeon room contents,
  and world modifiers' effect on anything beyond the flags in the save.

`vseed locations` prints this list in full on every run. It is not a footnote.

## Rates that are bounded, not proven

Measured over **5,000 uniformly drawn seeds** (2026-09-23). These are the numbers behind "you will
always get five boss altars", which is the kind of sentence SeedLab will not write.

- **The seven boss altars never fell short in the sample.** Eikthyrnir 3, GDKing 4, Bonemass 5,
  Dragonqueen 3, GoblinKing 4, the Queen 5, Fader 3 — 5,000 of 5,000 each. By the rule of three that
  bounds the shortfall rate at **≤ 0.060 % (95 %)**. It does **not** prove the count is fixed, and
  the tool never says it is. Dragonqueen has the thinnest headroom (a maximum of 2,117 attempts used
  of 60,000, a 28× margin against 123–811× for the others), so it is the likeliest of the seven to
  have a tail.
- **`DN_Bossroom`, a boss altar in the same catalogue, does fall short: 26 of 5,000 seeds
  (0.520 %, CI 0.355–0.761 %)**, each having exhausted all 60,000 attempts. So "can a shortfall
  happen at all" is answered **yes, by counterexample**. `Hildir_camp` 26/5,000 (0.520 %),
  `BogWitch_Camp` 1/5,000 (0.020 %).
- **Proved from the code, not sampled:** `count <= m_quantity` always
  (`while (i < attempts && placed < m_quantity)`), and there is **no lower bound anywhere** — after
  the loop there is no retry, no filter relaxation and no fallback, only
  `ZLog.LogWarning("Failed to place all ...")`. The game ships a code path for shortfall.
- **Proved absent:** `GoblinCamp2_1` is in **no** seed — its `m_biomeArea` is 0 while `GetBiomeArea`
  only ever returns Edge (1) or Median (2), so the filter always rejects. Confirmed 0 of 5,000.
- **Commonly absent** (alt-biome-gated): TarPit2_1 92.4 %, TarPit1_1 73.5 %, TarPit3_1 51.4 %,
  StoneTowerRuins05_leet 48.1 %, GoblinHut03 34.0 %.

Two limits on the rarity machinery itself: **there is no shipped calibration sample** (`vseed
calibrate` does not exist), so rate warnings come from the atlas's labelled samples — 240-seed
distance percentiles and 5,000-seed absence rows — and the joint-rate estimator and the
"P(zero hits) = (1 − p)^B" budget line are implemented and tested but not fed. And the two
count rules **D4/D5 are deliberately deferred**: they need per-type *count* histograms, today's
sample holds distance percentiles only, and built against it they would never fire while their tests
passed for the wrong reason. The deferral and its reason are printed next to every count goal.

## One architecture, and what the self-test does about it

**SeedLab's bit-exactness has only ever been proven on x64.** The five gates have never run on
arm64, and the risk is real rather than theoretical: `Math.Sin/Cos/Atan2/Pow` and float evaluation
can differ in the last bit, and a last-bit difference at a biome threshold changes the world.

What ships instead of a promise:

- On a cold cache every command that builds a world runs a **machine self-test** against the corpora
  the game itself produced — `numerics` (271 checks) and `seedlab/natives` (**263,780** recorded
  values: Perlin samples, `Random` traces, libm results, hash vectors) — and writes a stamp into
  `<cache root>\selftest` recording platform, runtime, ISA and result.
- It **fails closed, demonstrated by counterexample rather than asserted.** Copy
  `groundtruth\natives` aside, change one recorded hash by 1, run `vseed` with that copy on the
  search path, and `vseed seed 12345` exits **1**:

  ```
  vseed seed: SELF-TEST FAILED on win-x64 / X64 / .NET 10.0.12.
  SeedLab reproduces Valheim's world generation bit for bit; on this machine it does not.
    seedlab/natives: 263779/263780 exact  FIRST FAILURE: GetStableHashCode("StoneCircle"):
    the game recorded -1587608450, this machine computes -1587608451
  Any seed, biome or location this build reports here could be wrong.
  ```

  with the three things to do next, the last of them being `--skip-self-test`.
- `--accept-unverified-platform` proceeds on an architecture the gates have never run on;
  `--skip-self-test` turns the check off and then says so: `warning: the machine self-test was turned
  off: SeedLab's bit-exactness is UNVERIFIED on this run`. Checked one command at a time on
  2026-09-23, that warning appears on **`seed`, `at`, `map`, `locations` and `search`**, and not on
  `hash`, `invert`, `space`, `data` or `worlds` — they do not build a world. `hash` and `invert` do
  rest on the 429 verified hash vectors, so their silence is a gap worth closing.

Linux and macOS on x64 are expected to work and are **untested**; arm64 is untested and unproven.

## Not implemented

Named here so nobody has to discover them by trying:

- **`--strategy funnel` and `--strategy sample`** as named strategies. The two knobs that exist are
  `search.screen` (auto/on/off, which is the funnel in the small) and the scan order
  (shuffled/sequential, which is the sample in the small). The funnel ratio and stage-2 cost report
  the design asks for do not exist.
- **Count rules D4/D5** — deferred, with the reason above.
- **GPU acceleration** — rejected for now, after CPU SIMD delivered 6.03× on `GetBaseHeight`
  bit-exactly. A GPU path could only ever be an approximate screening tier: GPUs fuse multiply-add
  and approximate transcendentals, which is exactly what decides a biome at a threshold, and
  consumer AMD FP64 runs at ~1/32–1/64 of FP32 while this port is double-heavy.
- **The write-path tripwire test.** "SeedLab writes nothing outside its cache root and the file you
  named" is currently held up by an IL-level audit and by hand-checked before/after listings, not by
  a test that fails when it stops being true.
- **A regression test for the interactive `y/N` confirmation.** The non-interactive branch (refuse,
  name `--yes`) is proven; the typed answer is not.
- **`vseed search --json` on a refused run** writes the refusal to stderr and exits 1 with an empty
  stdout, so a JSON consumer gets no machine-readable refusal document.
- **Per-query `mode: background` lowers the worker count but not the thread priority** in the web
  UI; `BelowNormal` comes from the server process's own mode at startup.
