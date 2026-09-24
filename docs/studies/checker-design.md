# SeedLab query checker - feasibility, vacuity and rarity

Design for the pre-flight check that runs on a query **before** a seed is touched, decides whether the
run is worth starting, and says so in one screen the user can act on.

Build this is written against: `DATA-STAMP game-version=1.0.15 network=40 unity=6000.0.75f1
assembly_valheim-sha256=59f53fb5... dumped=2026-09-22 dumper=1.0.0 mode=assets schema=1`

Inputs:

| input | path | what it gives |
|---|---|---|
| constraint atlas | `constraints/constraint-atlas.json` | per-type hard radial annulus, quantity, unique, biome bands, one-per-zone caps, pairwise annuli, the AshLands constant |
| location table | `data/1.0.15-59f53fb5/locations.json` | the fields the atlas was derived from; the atlas is a cache, this is the source |
| calibration sample | new, shipped: `data/1.0.15-59f53fb5/calibration.{json,bin}` | per-seed measured values for a fixed seed sample - the rarity numbers |
| existing T0 | `src\SeedLab.Search\Feasibility\{StaticAnalysis,LocationFeasibility,BiomeGeometry}.cs` | the refusal half already exists and is sound |

> **Naming.** This document calls the component `QueryCheck`. It is a new file set under
> `src\SeedLab.Search\Feasibility\` and it *wraps* `StaticAnalysis` / `LocationFeasibility` rather than
> replacing them: those two already produce the REFUSE tier correctly and are already wired into
> `CompiledQuery.Compile` (line 416-424). What is missing is (a) the vacuity tier, (b) the rarity tier,
> (c) the combination rules, (d) one report object that the CLI and the web API both render.

---

## 0. The one soundness principle

Everything below follows from a single rule, and nothing in this document may contradict it.

> **For each goal the checker builds an OVER-approximation `F` of the set of values that metric can
> take over all 4,294,967,296 seeds. It REFUSES only when `F ∩ accept(goal) = ∅`, and calls a goal
> VACUOUS only when `F ⊆ accept(goal)`.**

Both tests get *safer* as `F` gets wider, which is why over-approximation is the right direction and
why a merely plausible bound must never be narrowed into `F`:

* widening `F` can only turn a REFUSE into an OK - it can never invent a refusal;
* widening `F` can only turn a VACUOUS into an OK - it can never invent a vacuity claim.

So when two sound bounds disagree, **take the looser one**. Concretely: `BiomeGeometry.Band` returns
7,900 m for the AshLands/DeepNorth floor while the atlas computes 7,907.68 m. Both are sound; 7,900 is
looser; the checker decides on 7,900 and displays 7,907.7 only as the labelled exact value
(section 8, F1).

`F` always carries an explicit flag for whether **absence** is in it (section 1.3). Forgetting the
absent case is the single most likely way to ship a wrong vacuity verdict; section 7 has the test that
catches it.

---

## 1. The feasible set `F` for one goal

### 1.1 Shape

```csharp
public readonly struct Feasible
{
    public double Lo;              // widest sound lower bound on the measured value
    public double Hi;              // widest sound upper bound; +inf when unbounded
    public bool   IncludesAbsent;  // the metric can also report the "nothing there" sentinel
    public double AbsentValue;     // +inf for a distance metric, 0 for a count/area metric
    public bool   Exact;           // F is the EXACT achievable set (constant metrics only)
    public EvidenceItem[] Evidence;// one per bound, each naming a decompiled member or an asset field
}
```

`Lo`/`Hi` are in the metric's own unit (`MetricDef.Unit`), computed **on the query's own grid** for
every area and count metric. An area cap quoted for a 12 m grid is wrong at 96 m, so the checker
computes caps per query with `BiomeGeometry.MaxArea(c.Grid, ...)` exactly as `StaticAnalysis` already
does, and never quotes a cached km2 figure.

### 1.2 How `F` is built, per metric family

Write `lo(t)`, `hi(t)` for a location type's hard radial annulus (`locations[].radialLo/radialHi` in
the atlas, recomputed at runtime by `LocationFeasibility.LowerBound/UpperBound` so the two can be
cross-checked - test T4). `T` is the goal's prefab set after group expansion, restricted to types that
are `Placeable` and not `impossible` (`GoblinCamp2_1`). `band(b)` is
`BiomeGeometry.Band(b, genVersion)`.

| target.metric | `Lo` | `Hi` | `IncludesAbsent` |
|---|---|---|---|
| `location/group.nearest_distance` | `min_t lo(t)` | `max_t hi(t)` | yes, `+inf` |
| `location/group.all_candidates_distance` | `min_t lo(t)` | `max_t hi(t)` | yes, `+inf` |
| `location/group.all_types_distance`, \|T\|>1 | `max_t lo(t)` | `max_t hi(t)` | yes, `+inf` |
| `location/group.count`, `count_within(R)` | 0 | `min(Σ_t maxCoexisting(t), zonesIntersecting(R))` | no (0 is in range) |
| `location/group.types_within` | 0 | \|T\| | no |
| `biome.nearest_distance` | `band(b).Min` | `band(b).Max` | yes, `+inf` |
| `biome.area`, `land_area`, `largest_patch_area`, `area_above_height` | 0 | `MaxArea(grid, band, ∞)` | no |
| `biome.area_within(R)` | 0 | `MaxArea(grid, band, R)` | no |
| `biome.present` | 0 | 1 | no |
| `world.*` | see 1.5 | see 1.5 | - |

Three of these deserve their derivation spelled out, because getting them backwards is exactly how a
checker refuses a legal query.

**`nearest_distance` over a group takes `max_t hi(t)`, not `min_t hi(t)`.** The metric is
`min over types present`. If only the type with the widest ring happens to be placed, the measured
value is that type's, so the sound upper bound is the largest `hi` in the group. A prototype of this
design used `min` and promptly declared three shipped presets vacuous - `boss-rush`'s `all-seven`
"within 8.6 km" was reported vacuous against Eikthyr's 1,000 m ring. That is the failure mode
section 7 exists to prevent.

**`all_types_distance` takes `max_t lo(t)` for `Lo`.** It is `max over types of nearest(t)` and each
`nearest(t) >= lo(t)`, so the max is at least the largest `lo`. This is the one metric whose lower
bound *tightens* as the group grows, and it is sound because it follows from the metric's own
definition, not from any assumption about placement succeeding.

**`count_within(R)` is capped twice, independently.** By `Σ_t maxCoexisting(t)` (`m_quantity`, or 1 for
an `m_unique` type - `PlaceLocations` 1901 `if (!location.m_unique || placed <= 0)`), and by the number
of 64 m zones that intersect the disc of radius `R`, because `RegisterLocation` 2604-2611 keys
`m_locationInstances` by `GetZone(pos)` and refuses a second entry for a zone. The zone cap binds
**all types combined**, so it also bounds a conjunction of count goals (section 3.5). Reproduced
independently for this document: R=300 -> 89, 500 -> 225, 1,000 -> 837, 2,000 -> 3,197, 3,000 -> 7,105,
5,000 -> 19,501, 10,500 -> 85,233, counting a zone when its 64 m square intersects the disc. That is
the over-approximating choice on purpose: a zone-*centre* test gives 767 at R=1,000 and would refuse
legal queries.

### 1.3 Absence, and why no location `near` goal is ever vacuous

`nearest_distance` is documented as `+infinity when the world has none` (`MetricCatalog`: "so a 'far'
goal passes on absence"). Therefore:

* `near D` / `at_most D` **rejects** an absent world. `F ⊆ accept` can never hold because `+inf ∈ F`,
  so such a goal is never VACUOUS. When `D >= Hi` it is **DEGENERATE**: it has stopped being a distance
  filter and become a *presence* filter - "a seed in which this type placed at all". Different verdict,
  different fix (section 2.3).
* `far D` / `at_least D` **accepts** an absent world, so it is never refusable, and it is VACUOUS
  exactly when `D <= Lo`.
* `between a..b` rejects absence, so it can be refused (both `LocationFeasibility` cases already do)
  and can never be vacuous while `IncludesAbsent`.

For an area or count metric the absent value is 0 and already lies inside `[Lo, Hi]`, so no special
case is needed - which is why `biome:Swamp area_within(1200) at_most 0` genuinely *is* vacuous
(section 2.2).

### 1.4 Constant metrics

A metric is **constant** when the generator's decision for it contains no seed-dependent term. Exactly
one biome qualifies: `GetBiome` tests `IsAshlands` at `WorldGenerator.cs:796`, *before* the ocean test
at 800, and `IsAshlands(x,z)` is `Length(x, z-4000) > 12000 + 100*WorldAngle(x,z)`
(`WorldGenerator.cs:751-755`) - position only, no noise, no `m_offset`, no seed. So the AshLands cell
set is identical in every seed **on any fixed grid**.

For `biome:AshLands` with `area`, `share`, `nearest_distance`, `largest_patch_area`, `area_within` or
`present`, the checker computes the single value on the query's grid (one pass, cached per grid) and
sets `F = {v}`, `Exact = true`. The verdict then falls out of section 2 with no new machinery: the goal
either accepts `v` (VACUOUS - a constant that passes) or it does not (REFUSE - a constant that fails).
**No cached km2 number is quoted**: 43,031,376 m2 / 12.4236 % is the value on the 12 m grid only, and a
`grid: 96` query gets a different number for the same provable reason.

`land_area` / `area_above_height` for AshLands are **not** constant - they depend on height, which is
seeded. `DeepNorth` is **not** constant: `IsDeepnorth` is equally seed-free but is tested at line 804,
*after* `baseHeight <= 0.02 -> Ocean`, so the realised region is the fixed region minus a seeded ocean
part. DeepNorth therefore yields one-sided hard bounds only - `area <= regionArea(grid)` and
`nearest >= 7,900 m` - and nothing else.

### 1.5 World metrics

`world:*` metrics have almost no code-level bounds and the checker must say so rather than invent them.
`GetBaseHeight` is Perlin noise offset by `m_offset0`/`m_offset1` drawn from the seed
(`WorldGenerator` 223-229), so land share, island count, largest island and peak height are bounded
only by 0 and the disc.

| world metric | `Lo` | `Hi` | evidence |
|---|---|---|---|
| `land_area`, `water_area`, `land_area_within(R)` | 0 | disc area on grid (or disc(R)); the 10,000-10,500 m ring never counts as land | `GetBaseHeight` 917-926 lerps height to -0.2 from 10,000 m and to -2 from 10,490 m, for every seed |
| `land_share`, `water_share`, `ocean_share` | 0 | 1 | - |
| `island_count`, `largest_island_area`, `spawn_island_area` | 0 | the `land_area` cap | - |
| `highest_peak`, `deepest_point` | unbounded | unbounded | do not guess |
| `nearest_land_distance` | 0 | 10,500 | water edge |
| `coastline_length` | 0 | +inf | a fractal, grid-dependent; no bound |
| `river_count`, `lake_count`, `stream_count` | 0 | +inf | no bound |

Every one of these is **rarity-only**: the checker may warn from the calibration sample and may never
refuse. The single real refusal available here is `radius > 10,500 m` (R12).

### 1.6 `from: spawn`

A goal with `From = Spawn` measures the separation of two *placed* things, so no centre-relative ring
applies to it directly - `LocationFeasibility.Check` correctly bails out on its `fromCentre` guard.
What does apply is the triangle inequality between two annuli that share the origin:

```
sep(X, StartTemple) >= max(0, lo(X) - hi(StartTemple))      hi(StartTemple) = 5,100 m  (Meadows band)
sep(X, StartTemple) <= hi(X) + hi(StartTemple)
```

which gives a genuine REFUSE for the far-out types and nothing at all for the rest:

| target | `lo(X)` | provable min separation from spawn |
|---|---|---|
| `FaderLocation`, `DN_Bossroom` | 7,907.7 | **2,807.7 m** (2,800 m on the looser 7,900 floor) |
| `FrozenShip01/02/03_DN` | 8,000 | **2,900 m** |
| `Mistlands_DvergrBossEntrance1` and the other Mistlands types | 5,900 | **800 m** |
| everything else (`lo <= 5,100`) | - | 0 m - **no constraint; say so out loud** |

The empirical spawn radius is 0-499 m over 240 seeds and 71-228 m over three real worlds, so this bound
is *sound but very weak*. It must be labelled a limit, not a likelihood, and the WARN threshold for the
same pair comes from the calibration sample instead.

---

## 2. The three verdict tiers

```csharp
public enum Verdict { Ok, WarnRare, WarnDegenerate, WarnVacuous, Refuse }   // increasing severity
```

A goal gets exactly one verdict; the query's verdict is the worst of its `must` goals' verdicts plus
whatever the combination rules (section 3) add.

### 2.1 REFUSE - no seed can satisfy it

**Rule.** `F ∩ accept(goal) = ∅`, where `F` came only from HARD evidence: a decompiled branch condition
or a field of the dumped `ZoneLocation` table. **A sample may never produce a REFUSE** - not from 240
seeds, not from the 36,829 real instances, not from anything.

**Slack.** Every comparison is made with `LocationFeasibility.Slack = 1.0 m` of relief in the
permissive direction (`asked + Slack < bound`), three orders of magnitude more than the
float32-versus-double gap of ~1.2 mm at 10 km. Area comparisons use one grid cell of slack for the same
reason.

**Stamp gate.** If `atlas.stamp != manifest.stamp`, or either is absent, **no REFUSE may be issued**:
every would-be refusal is downgraded to a WARN carrying the stamp mismatch. A game update is precisely
the case where a hard bound quietly stops being hard. This is `check-game-version.ps1`'s exit-2
discipline applied to the search tool: the tool becomes less helpful, never wrong.

The refusal catalogue. Each row names the evidence it must cite and the repair it must offer:

| # | trigger | cite | offer |
|---|---|---|---|
| R1 | location distance `near D`, `D + 1 < Lo` | `radialLoSources` (the field) and/or the biome band | `near ceil(Lo)` |
| R2 | location distance `between a..b`, `b + 1 < Lo` | same | `between Lo..b` |
| R3 | location distance `between a..b`, `a > Hi + 1` | `radialHiSources` | `between floor(Hi)..b` |
| R4 | `count`/`count_within at_least N`, `N > Σ maxCoexisting` | `m_quantity` per type, `m_unique` where it caps to 1 | `at_least Σ` |
| R5 | `count_within(R) at_least N > 0`, `R + 1 < Lo` | the ring | `radius ceil(Lo)` |
| R6 | `count_within(R) at_least N`, `N > zonesIntersecting(R)` | `RegisterLocation` 2604-2611 | the radius at which `N` zones fit |
| R7 | `types_within at_least N > \|T\|` | group membership | `at_least \|T\|` |
| R8 | the target is wholly unplaceable (`GoblinCamp2_1`, or `m_enable` false / `m_quantity` 0) and the goal needs presence | `m_biomeArea = 0` against `PlaceLocations` 1934 | drop the goal, or use `GoblinCamp2` |
| R9 | biome `nearest_distance near D`, `D + 1 < band.Min` | the `GetBiome` line | `near ceil(band.Min)` |
| R10 | biome `area*` `at_least A > MaxArea(grid, band, R)` | band + grid | `at_least floor(cap)` |
| R11 | a constant metric whose single value fails the test | `IsAshlands` 751-755, tested at 796 | the value, and the test that would pass |
| R12 | any distance or radius field `> 10,500 m` | `GetBiomeHeight` 1032-1035: altitude is -430 m beyond the water edge and no running type has `m_minAltitude <= -430` | clamp to 10,500 m |
| R13 | two `must` goals on the same metric key with disjoint accepted intervals | the two goal ids | which one to change |
| R14 | `from: spawn`, `near D`, `D + 1 < lo(X) - 5,100` | `X`'s ring against the Meadows band on `StartTemple` | `near ceil(lo(X) - 5100)` |

R12 is the user's own words - *"the entire world has a fixed radius, as such no range value must ever
exceed this radius"* - and the radius to refuse above is **10,500 m, not 10,000 m**. Real instances
exist at 10,360 m (`ShipWreck02_DN`) and 10,275.8 m (`Greydwarf_camp1`), so refusing above 10,000 m
would refuse legal queries.

R13 is the only refusal that needs no game data at all: it is a contradiction in the query text. It
therefore survives a stamp mismatch.

### 2.2 WARN-VACUOUS - true for every seed

**Rule.** `F ⊆ accept(goal)`, from HARD evidence only.

A vacuous `must` goal is not an error - the run completes and returns real seeds. It is a *lie about
what the run is doing*: the filter rejects nothing, so a "targeted" search becomes a full scan that
returns whichever seed the permutation visited first. That is why this is a WARN and not a REFUSE, and
also why it blocks by default (section 4.4).

| # | trigger | example |
|---|---|---|
| V1 | location distance `far`/`at_least D`, `D <= Lo` | "Bonemass at least 2 km from centre" - `m_minDistance = 2000` |
| V2 | biome `nearest_distance far`/`at_least D`, `D <= band.Min` | "AshLands at least 5 km out" - band floor 7,900 m |
| V3 | biome `area_within(R) at_most A`, `R <= band.Min`, `A >= 0` | **`gentle-start`'s three "no X within 1.2 km" must-haves** |
| V4 | biome `area*` `at_most A`, `A >= MaxArea(grid, band, R)` | "Mistlands at most 200 km2" |
| V5 | `count*` `at_most N`, `N >= Σ maxCoexisting` or `N >= zonesIntersecting(R)` | "at most 3 traders in the world" - all three are `m_unique`, so `Σ maxCoexisting = 3` |
| V6 | a constant metric whose single value passes the test | any AshLands `area`/`nearest_distance` goal that is not R11 |
| V7 | `types_within at_most N`, `N >= \|T\|` | - |
| V8 | `between a..b` with `a <= Lo`, `b >= Hi`, `!IncludesAbsent` | - |

**V3 is live in the shipped presets.** `gentle-start` carries three `must` goals - `no-swamp`,
`no-plains`, `no-mistlands` - each `biome:X area_within radius 1200 test at_most value 0`. Swamp cannot
exist inside 2,000 m (`GetBiome` 812, `num > 2000 && num < 6000`, neither endpoint carrying the
WorldAngle wobble), Plains inside 2,900 m (`GetBiome` 820, `num > 3000 + A`), Mistlands inside 5,900 m
(`GetBiome` 816). All three measure exactly 0 in every one of the 4,294,967,296 seeds. The preset's
real must-set is two goals, not five, and its description ("no Swamp/Plains/Mistlands within 1.2 km")
describes a property of the generator rather than of the seeds it found. This is the flagship
demonstration that the vacuity tier earns its place, and it is test T7.

**A vacuity warning must offer a threshold that would discriminate**, taken from the calibration
sample: the value that would pass ~50 % of seeds and the value that would pass ~5 %. When the sample
shows there is no such value, say so instead of inventing one.

> **Rule.** If the sample's p5 and p95 for that metric differ by less than the sample's own distance
> uncertainty (one grid-cell diagonal - 8.49 m at G12), print *"no threshold on this metric
> discriminates between seeds; the value is effectively fixed"* rather than a suggested threshold.
> This fires for the nearest-distance of Swamp (0.5 m spread over 40 seeds), Plains (0.7 m), Mistlands
> (0.5 m) and Black Forest (one distinct value, 508.3 m).

### 2.3 WARN-DEGENERATE - the goal has silently become a different goal

**Rule.** `IncludesAbsent` and `accept(goal) ⊇ F \ {absent}`: the goal excludes nothing except worlds in
which the target failed to place at all.

This is the tier that gets "Eikthyr within 1.2 km of the centre" right, and it earns its own enum value
because the *fix* differs from V1's. The goal is not vacuous - it does reject some seeds - but it
rejects them for a reason the user never asked about. The message must say what the goal has actually
become, and how often that bites, from the sample.

`Eikthyrnir` carries `m_maxDistance = 1000`. `PlaceLocations` 1956-1962 compares that field against
`randomPointInZone.magnitude`, and `GetRandomPointInZone` 2166-2172 builds that point as
`GetZonePos(zone) + (x, 0, z)` with `y = 0`, so the magnitude **is** the XZ distance from the origin.
Over 240 sampled seeds `Eikthyrnir` was never short of its `m_quantity` of 3, so the degenerate goal
"Eikthyr within 1.2 km" passed 240 of 240: its exclusion rate is bounded above by the rule of three at
`3/240 = 1.3 %`.

Reporting rule: a degenerate goal whose type showed **zero** absences in `n` sampled seeds is reported
as "excludes at most `3/n` of seeds"; one whose type did fail (`Hildir_camp`, 1 of 240) is reported
with the measured absence rate and its interval.

### 2.4 WARN-RARE - legal but unlikely

**Rule.** The goal is neither refusable nor vacuous, and either the estimated pass rate is low or the
user's budget makes the expected hit count small. Two independent triggers, both reported:

1. **Rate:** `p̂ < 0.01` warns; `p̂ < 0.001` warns louder.
2. **Budget:** with budget `B` seeds, `P(zero hits) = (1 - p̂)^B`. Warn when `P(zero) > 0.10`.

Every estimate travels with its provenance and its uncertainty (section 5.4). Never refuse on rarity,
however small `p̂` is: `p̂ = 0` from 4,096 seeds means `p < 3/4096 = 0.073 %` at 95 %, which is a warning
with a number attached, not a proof of impossibility.

---

## 3. Combination rules

A query is a conjunction of its `must` goals. `nice` goals never reject; they are checked only so their
verdicts can be shown (a `nice` goal that is impossible always scores 0, which the existing code
already warns about).

### 3.1 Per-field sanity, before any goal is looked at

Run these first - they produce the clearest message and they are the user's literal request:

* any `value`, `max` or `radius` on a `Unit.Metres` metric that exceeds **10,500 m** -> R12;
* `radius <= 0` on a `*_within` metric -> usage error, not a verdict;
* `between` with `value > max` -> usage error;
* negative area or count thresholds -> usage error;
* `grid` outside 1..512 m -> usage error.

### 3.2 Any impossible must-have refuses the whole query

`Refuse(query) = ∃ g ∈ must : Verdict(g) = Refuse`. Report **every** refused goal, not the first: the
user fixes them in one edit, and stopping at the first turns one round trip into three. The existing
`SearchCommand` loop over `cq.Unsatisfiable` already has this shape; keep it.

### 3.3 Self-contradiction on one metric

Group `must` goals by the key `(target, metric, radius, height, minArea, from)`. Within a group,
intersect the accepted intervals:

```
near D / at_most D  ->  (-inf, D]
far D  / at_least D ->  [D, +inf)
between a..b        ->  [a, b]
```

An empty intersection is R13, citing both goal ids and no game data at all ("`crypts-close near
2,100 m` and `crypts-far far 3,000 m` cannot both hold"). A non-empty intersection that is tighter than
`F` is fine, and is where part of the joint rate comes from.

Also intersect across **nested radii on the same target**. `count_within(1000) at_least 5` together
with `count_within(2000) at_most 3` is a contradiction, because the count is monotone non-decreasing in
`R`. Rule: for two count goals on the same target with `R1 <= R2`, refuse when
`min_accept(R1) > max_accept(R2)`. The same monotonicity holds for `area_within` and `types_within`.

### 3.4 The joint rate - and the honest statement of what it is

Three estimators, in preference order.

1. **Sample-joint (primary).** Evaluate the whole conjunction on the shipped calibration sample
   (section 5.2). This is the only estimator that handles correlation, and correlation here is large:
   two Black Forest goals move together; `all_types_distance` over a group is a max of correlated
   distances; `land_share` and `island_count` are near-deterministic functions of one another; biome
   shares of one disc are anti-correlated by construction. Report `k/n` with a Wilson 95 % interval.
2. **Live calibration.** Run the real compiled query over `n` seeds drawn from the *actual* scan plan.
   `SearchCommand --dry-run --calibrate n` already builds the plan and times seeds, so this is a
   counter added to an existing loop. Default `n = 512`. Used automatically whenever the sample cannot
   cover the query (an unsampled metric, or a grid with no matching column).
3. **Product of marginals (fallback, always labelled).** `p̂_joint <= Π p̂_i` is **not** an estimate; it
   is an upper bound. Print it as `at most 1 in X`, never `about 1 in X`, and always offer live
   calibration alongside it.

This distinction is not pedantry. For `balanced.json`'s eight biome-area goals the product of marginals
is around 1e-6 while the sample-joint rate is a few per cent, because those goals are thresholds on
shares of a single disc.

### 3.5 Cross-goal geometric bounds

* **Pairwise annuli (triangle inequality).** For two location targets with rings `[loX, hiX]` and
  `[loY, hiY]`, `max(0, max(loX - hiY, loY - hiX)) <= sep <= hiX + hiY`. Today the only query shape
  that *asks* about a separation is `from: spawn` (section 1.6): `MetricCatalog` has no
  location-to-location metric, and the reference site the user is comparing against reports
  **"Distance from center (km)"**, not inter-boss distance. So this rule is implemented, exercised by
  `from: spawn`, and shown in the GUI as an informational line - **not** as a refusal path for goals
  that never asked about a separation.
* **When the lower bound is 0, say "no constraint" rather than printing a 0 that looks like an answer.**
  Nine of the ten pairs in the atlas have a lower bound of exactly 0, because radial annuli about a
  common origin overlap for nearly every interesting pair. The one exception is Haldor-Eikthyr:
  `Vendor_BlackForest.m_minDistance = 1500` against `Eikthyrnir.m_maxDistance = 1000` gives a provable
  500 m, against an observed 2,003-10,325 m. Refuse below 500 m; warn below ~2 km from the sample.
* **Shared zone budget.** Several `count_within` must-goals on *different* targets still compete for the
  same zones: `Σ_g N_g > zonesIntersecting(min_g R_g)` is refusable, citing the one-per-zone rule. This
  is the rule that kills "12 Fuling villages within 300 m" (89 zones exist there) and it composes
  across goals.
* **Shared `m_group` spacing is a heuristic, never a refusal.** `HaveLocationInRange` 2626-2650 compares
  `(l[i].m_position - p).sqrMagnitude` **after** `p.y` was set from `GetHeight` at line 1973, so
  `m_minDistanceFromSimilar` is a **3D** distance: two instances on a slope can be closer than it
  horizontally. A circle-packing bound built from it is unsound. The one-per-zone rule is the proof; the
  packing bound may only feed a WARN, and that WARN must say why it is not a proof.

### 3.6 No must-have at all

If the query has zero `must` goals - or every `must` goal is VACUOUS, or DEGENERATE with a near-100 %
pass rate - the run is a full scan that will report the first `keep` seeds the permutation visits,
ranked by the `nice` score. That is a legitimate thing to want (it is how you build a ranked sample of
the space), so it is a WARN and not a REFUSE; but it must be stated in those words, with the number of
seeds the budget will actually examine.

### 3.7 Verdict of the whole query

```
if  any must goal is Refuse                             -> Refuse            (no override)
elif any must goal is WarnVacuous or WarnDegenerate     -> Warn, blocking    (--allow-vacuous)
elif no must goal discriminates (3.6)                   -> Warn, blocking    (--allow-vacuous)
elif the joint rate or budget triggers 2.4              -> Warn, non-blocking with --yes
else                                                    -> Ok
```

---

## 4. User-facing text

Voice, from the existing code: one line per fact; numbers with units and thousands separators; the
evidence named as the user would name it (prefab and field, or class and member with its line); the fix
stated as an edit. No apology, no scolding, no exclamation marks. Distances are metres below 10,000 and
"N.NN km" above, matching the GUI slider.

### 4.1 REFUSE, CLI

```
vseed search: this query cannot be satisfied by any of the 4,294,967,296 seeds.

  goal 'haldor-close'  location:Vendor_BlackForest nearest_distance near 900 m
    Vendor_BlackForest has m_minDistance 1500, so no world can place Haldor within
    1,500 m of the centre - ZoneSystem.PlaceLocations:1956 compares that field against
    the candidate point's distance from the origin.
    Nearest satisfiable value: near 1,500 m.
    (Observed over 240 seeds: 1,508 m at best, median 1,944 m - so 2,000 m is a realistic ask.)

  goal 'fuling-cluster'  group:fuling_villages count_within radius 300 at_least 12
    GoblinCamp2 is Plains-only and Plains cannot exist within 2,900 m of the centre
    (WorldGenerator.GetBiome:820, 'num > 3000 + A' with A in [-100, +100]), so a 300 m
    disc holds none of it in any seed. Independently: a 300 m disc intersects only 89
    zones of the 64 m grid, and the game refuses a second location in a zone
    (ZoneSystem.RegisterLocation:2604-2611) - 12 instances need at least 96 m of radius
    even where they are legal. (89 zones is not the binding bound here; the ring is.)
    Nearest satisfiable value: radius 2,900 m or more.

Nothing was scanned. Fix the two goals above and run again.
exit 1
```

Rules for this block: one stanza per refused goal; the stanza names the goal id, restates the goal in
its own language, gives the evidence with member and line, then exactly **one** "Nearest satisfiable
value" line. The parenthetical from the sample is optional and appears only when the sample covers that
type.

### 4.2 WARN-VACUOUS and WARN-DEGENERATE, CLI

```
vseed search: 3 must-have goals do not exclude any seed.

  goal 'no-swamp'  biome:Swamp area_within radius 1200 at_most 0 m2
    Swamp can never exist within 2,000 m of the centre in any seed
    (WorldGenerator.GetBiome:812, 'num > 2000 && num < 6000'; neither endpoint carries
    the WorldAngle wobble). This goal measures 0 in every seed and rejects none of them.
    No threshold on this metric discriminates between seeds: the nearest Swamp varied by
    0.5 m over 40 sampled seeds. Delete the goal, or raise the radius above 2,000 m to
    make it mean something.

  goal 'no-plains'  biome:Plains area_within radius 1200 at_most 0 m2
    Plains can never exist within 2,900 m of the centre (GetBiome:820, 'num > 3000 + A',
    A = WorldAngle * 100 in [-100, +100]). Same reading as above.

  goal 'eikthyr-close'  location:Eikthyrnir nearest_distance near 1200 m
    Eikthyrnir has m_maxDistance 1000, so every altar that exists is already within
    1,000 m of the centre. This goal is not a distance filter any more - it is
    'a seed in which an Eikthyr altar was placed at all', which excluded 0 of 240
    sampled seeds (so at most 1.3 % of seeds, 95 % rule of three).
    To filter on distance, ask for 300 m: measured median 290 m, 5th percentile 101 m.

With these goals as written the run is a full scan: it will return the first 200 seeds the
permutation visits, ranked by your nice-to-have score.

Re-run with --allow-vacuous to scan anyway.
exit 1
```

### 4.3 WARN-RARE, CLI

```
vseed search: this query is legal but rare.

  estimated pass rate  1 in 745  (0.134 %, 95 % CI 1 in 416 .. 1 in 1,333)
  source               shipped calibration sample, 8,192 seeds, 11 hits, DATA-STAMP 1.0.15/59f53fb5
  your budget          500 seeds -> 0.7 expected hits; 51 % chance of finding none
  to find one at 90 %  about 1,714 seeds (~3 m 26 s at the measured 8.3 seeds/s)

  the binding goal     'all-seven' group:bosses all_types_distance near 8,600 m
                       passes 6.1 % on its own; the rest of the query costs the other 45x

Re-run with --yes to start, or --calibrate 4096 to measure the real rate first.
```

Every number in that block is derived, not quoted: `p̂ = k/n`, Wilson 95 % on `(k, n)`, expected hits
`= B p̂`, `P(zero) = (1 - p̂)^B`, seeds for 90 % confidence `= ln(0.1) / ln(1 - p̂)`, and the time from
the run's own measured seeds/s. The "costs the other 45x" figure is `0.061 / p̂`.

Wording rules: the rate is stated as "1 in N" **and** as a percentage; the CI is always present; the
source names the sample size and the DATA-STAMP; the budget line converts the rate into the user's own
units (their `--seeds`, the measured rate of this machine); the "binding goal" line names the single
goal with the lowest marginal rate, because that is the one to relax.

When the estimator is the product of marginals, the first two lines are replaced by:

```
  estimated pass rate  at most 1 in 745 - this is an UPPER BOUND, not an estimate:
                       it multiplies the goals' individual rates as if they were
                       independent, and they are not (two Black Forest goals move
                       together). The true rate is higher, possibly much higher.
  source               Run --calibrate 1024 for a measured number.
```

### 4.4 Overrides - what happens when the user insists

| verdict | override | why |
|---|---|---|
| REFUSE | **none exists.** No `--allow-impossible`, no `--force`, no GUI checkbox. | A provable impossibility cannot be overridden into existence, and a flag that pretends otherwise burns a week of CPU on a proof the tool already holds. The escape hatches are fixing the goal, or `--no-prefilter`, which is an *audit* mode documented to produce the same result set. |
| REFUSE under a stamp mismatch | it was never a REFUSE (2.1) | the bound may have moved with the game; the tool warns and scans. |
| WARN-VACUOUS / WARN-DEGENERATE | `--allow-vacuous`; "Scan anyway" in the GUI | the run is legal and returns seeds; a ranked sample of the whole space is a thing people want. |
| WARN-RARE | `--yes` (implied by a non-interactive stdin plus an explicit `--seeds`); "Start anyway" | rarity is an estimate, and the user's time is theirs to spend. |
| usage errors (3.1) | none | fix the query. |

`--no-prefilter` must **not** silence the checker. It disables T0 rejection *during the scan*; the
pre-flight report still prints, because the two exist for different reasons and conflating them is how
an audit run becomes an accidental week-long scan.

### 4.5 The machine-readable form

`vseed search --json` and `POST /api/search/check` return the same object:

```json
{
  "checkerVersion": 1,
  "dataStamp": "DATA-STAMP game-version=1.0.15 ...",
  "atlasStamp": "DATA-STAMP game-version=1.0.15 ...",
  "stampMatch": true,
  "verdict": "refuse",
  "blocking": true,
  "override": null,
  "goals": [
    {
      "id": "haldor-close",
      "target": "location:Vendor_BlackForest",
      "metric": "nearest_distance",
      "test": "near", "value": 900.0,
      "importance": "must",
      "verdict": "refuse",
      "feasible": { "lo": 1500.0, "hi": 10500.0, "includesAbsent": true,
                    "absentValue": null, "exact": false },
      "evidence": [
        { "kind": "asset", "ref": "Vendor_BlackForest.m_minDistance", "value": 1500 },
        { "kind": "code",  "ref": "ZoneSystem.PlaceLocations:1956" }
      ],
      "repair": { "field": "value", "value": 1500.0, "text": "near 1,500 m" },
      "sample": { "n": 240, "min": 1508, "p5": 1567, "median": 1944, "p95": 3085,
                  "max": 5166, "absent": 0 },
      "message": "Vendor_BlackForest has m_minDistance 1500, so no world can place Haldor within 1,500 m of the centre."
    }
  ],
  "query": {
    "mustGoals": 2,
    "discriminatingMustGoals": 0,
    "rate": { "estimator": "sample-joint", "n": 8192, "k": 11, "p": 0.0013428,
              "ci95": [0.0007500, 0.0024030], "source": "shipped", "stamp": "..." },
    "budget": { "seeds": 500, "expectedHits": 0.671, "pZero": 0.511 }
  },
  "messages": []
}
```

`evidence[].kind` is `code` | `asset` | `query` | `sample`. **A `sample` item may never appear on a
`refuse` goal.** That invariant is asserted in the checker and tested (T2).

---

## 5. Where the numbers come from

Three sources, never mixed in one sentence.

### 5.1 The atlas (HARD, shipped, stamped)

`constraint-atlas.json` ships beside the location table under `data/<build>/` and carries the same
`stamp`. It is *derived* data: every field is recomputable from `locations.json` plus the decompiled
bands. So the checker does two things at start-up:

1. compares `atlas.stamp` with `manifest.json`'s `stamp`; a mismatch disables REFUSE entirely (2.1);
2. **recomputes** `radialLo`/`radialHi` for every type from the live `LocationTypeInfo` and asserts
   equality with the atlas (test T4). The atlas is a cache and a document; the live computation is the
   authority. If they disagree, the live value wins and the mismatch is a loud warning - a stale atlas
   is precisely how a false refusal would ship.

A game update changes the DATA-STAMP, which invalidates the atlas, which downgrades every REFUSE to a
WARN until the dumper is re-run and the atlas rebuilt.

### 5.2 The shipped calibration sample (EMPIRICAL, stamped)

Not a table of quantiles - a **matrix of per-seed values**, so the checker can evaluate any threshold
and any conjunction on it rather than multiplying marginals.

**Contents.** `data/<build>/calibration.json` (manifest) + `calibration.bin` (values):

* `seeds` - `N` int32 seeds drawn by the same Feistel permutation the search uses, with a published
  fixed key, so the sample is reproducible and is not a contiguous block of the space;
* `columns` - one per `(target, metric, parameter, grid)` tuple, `M` of them;
* `values` - `N x M` little-endian `float32`; `+inf` encodes absence;
* per column: `absentCount`, `min`, `max` and the 101 percentiles, for display only;
* `grid` per column. **A column measured at G12 may not estimate a rate for a query at G96.** The
  checker either finds a matching-grid column or falls back to live calibration.

**Which columns, and how many seeds.** Per-seed cost is dominated by the ordered prefix of the location
table, so three strata:

| stratum | columns | `N` | cost per seed |
|---|---|---|---|
| A: cheap prefix (ordered index <= 22: all bosses, all traders, `StartTemple`) | nearest / all_candidates / all_types distance per type and per shipped group; `count_within` at 1,000 / 2,000 / 5,000 / 10,500 m | **8,192** | 22 of 183 entries |
| B: biome and world metrics at the shipped grids (12, 96, 384 m) | every `biome.*` and `world.*` metric in `MetricCatalog` | **8,192** at G96/G384, **2,048** at G12 | biome/height field only |
| C: full placement (dungeons, runestones, tar pits, anything past ordered 22) | nearest / `count_within` for every group in `LocationGroups` | **2,048** | all 183 entries |

**How large a sample has to be to justify a rate.** The Wilson interval's relative width is governed by
the *hit count* `k`, not by `N`: the relative standard error of `k/N` is about `1/sqrt(k)`. Therefore:

* `k >= 10` - quote the rate with its Wilson 95 % interval. At `k = 10` that interval spans a factor of
  3.4 end to end (computed: `k=10, n=4096` gives 1 in 223 .. 1 in 754); say so by printing the
  interval, not by rounding it away.
* `1 <= k < 10` - quote it as "about 1 in `N/k`, from only `k` hits - treat as an order of magnitude".
* `k = 0` - quote **only** the rule-of-three upper bound `p < 3/N`, and offer live calibration. Never
  print "0 %".

With `N = 8,192`, a goal at the 1 % warn threshold gives `k ~ 82` (plenty), and the smallest rate the
sample can *estimate* rather than bound is `10/8192 = 0.12 %`. Rates below that are the live
calibrator's job. `N = 8,192` at the measured ~8 seeds/s for a prefix query is about 17 minutes of
one-off generation per stratum, which is why it is shipped rather than computed on demand.

**How it is generated.** A new `vseed calibrate --out data/<build>/calibration.{json,bin}` that reuses
`SearchRun` with a query holding *every* column as a `nice` goal - so nothing rejects and every seed
contributes a full row - writes the matrix and stamps it. It is regenerated whenever the DATA-STAMP
changes, and `vseed selftest` fails when `calibration.stamp != manifest.stamp`.

### 5.3 Live calibration (EMPIRICAL, this query, this run)

`--calibrate n` evaluates the *actual compiled query* on the first `n` seeds of the actual scan plan and
reports `k/n` with its Wilson interval. This is the only estimator correct by construction for any
query, and it is what the checker recommends whenever the sample cannot cover the query. Because it
uses the same plan, the seeds are not wasted: a later `--resume` continues past them.

### 5.4 The provenance sentence

Every rate printed anywhere carries one of exactly these three clauses, verbatim:

* `from the shipped calibration sample, N seeds, k hits, DATA-STAMP <stamp>`
* `measured live on n seeds of this run`
* `an UPPER BOUND from multiplying independent-looking goal rates - not an estimate`

---

## 6. The GUI surface

The web app (`src\SeedLab.Web\wwwroot\app.js` goal editor, `SearchModel`) gains three things, all driven
by the same `/api/search/check` response the CLI uses, so page and terminal can never disagree - the
same discipline `SearchModel` already applies to the criteria vocabulary.

### 6.1 The bounded slider

When a goal's target and metric are chosen the server returns that goal's `feasible` block, and the
editor renders the threshold control as a slider over **`[Lo, min(Hi, 10500)]`**, not over
`[0, 10500]`:

* the track spans the feasible interval; outside it is drawn but not selectable, hatched, with the two
  end labels naming their evidence - `1,500 m - m_minDistance`, `10,500 m - water edge`;
* the numeric box still accepts any number, because typing is how a user discovers a bound. A value
  outside the track turns the box red and shows the REFUSE message inline beneath it, with the
  "Nearest satisfiable value" as a clickable chip that sets the field;
* for a **constant** metric (AshLands area / nearest distance) the slider is replaced by the single
  value and the words "this is the same in every seed";
* for a metric with no hard bounds (`world:*`) the track spans the calibration sample's `min..max`, with
  p5 / p50 / p95 ticked, labelled "observed over N seeds" - a bound the code does not have must never
  look like one that it does.

### 6.2 The vacuity mark, crossed live

The track carries a second, lighter zone: the **vacuous** region, on whichever side the test points -
for `near`, everything at or above `Hi`; for `far`, everything at or below `Lo`. Crossing into it while
dragging:

* the handle changes colour and the track beyond the crossing point greys out;
* a one-line inline note appears at once, e.g. *"1,000 m and above: every Eikthyr altar is already
  inside this - the goal becomes 'an altar exists'"*;
* the sample's p50 / p95 ticks stay visible so the user can see where a discriminating threshold is, and
  clicking a tick sets the value.

The note is the same sentence the CLI prints, from the same field. There is no second wording.

### 6.3 The running verdict, before Find is pressed

A persistent strip directly above the Find button, updated on every edit (debounced 250 ms, one
`/api/search/check` call, no seeds touched - the check is a few hundred microseconds plus one grid pass
per new grid size):

```
  [ ! ]  2 goals exclude no seeds              about 1 in 745 seeds pass
         no-swamp   eikthyr-close              500-seed budget -> 0.7 expected hits
         -----------------------------------------------------------------------------
         [ Fix them ]   [ Scan anyway ]                        [ Find ]  (disabled)
```

States:

* **Ok** - green strip, rate and expected-hit count, Find enabled.
* **WarnRare** - amber strip, rate + CI + expected hits, Find enabled, wording as 4.3.
* **WarnVacuous / WarnDegenerate** - amber strip, the goal ids as chips that scroll to the goal,
  **Find disabled** until the goals are fixed or "Scan anyway" is pressed (which sets `allowVacuous` on
  the query and re-enables Find, the strip staying amber).
* **Refuse** - red strip, **Find disabled with no override**, each refused goal a chip that scrolls to
  it and shows the repair chip. The strip says "this query cannot be satisfied by any of the 4.29
  billion seeds" and never offers a way past it.
* **Stamp mismatch** - blue strip above everything: "the constraint atlas was built for game
  1.0.15/59f53fb5; this data is `<other>`. Bounds are advisory until the dumper is re-run - nothing will
  be refused."

The strip also states the full-scan case from 3.6 in its own words: *"no must-have goal excludes
anything - this will return the first 200 seeds it visits, ranked by score."*

### 6.4 Where the pairwise line lives

A `from: spawn` goal gets one extra line under its slider: the provable separation window and the
observed one - *"provably 2,808 m or more from spawn (FaderLocation's AshLands floor against the
spawn's 5,100 m Meadows ceiling); observed 8,326-9,677 m over 240 seeds"*. When the provable lower bound
is 0 the line reads *"no provable constraint - the two rings overlap"* and gives only the observed
range. It never prints a bare 0.

---

## 7. Test plan

The dangerous failure is a checker that refuses a **legal** query: it makes a seed unfindable and tells
the user a falsehood with a citation attached. Every test below aims at that; the first four are the
ones that matter.

### T1 - every REFUSE is contradicted by no real instance (36,829 rows)

For each of the 183 running types, build the checker's `F` and assert every real instance's measured
value lies inside it. Sources: `groundtruth/asdasdasd-locations.csv` (12,314),
`groundtruth/testworldclaude-locations.csv` (12,287),
`data/1.0.15-59f53fb5/goldens/locationinstances-0480A34C.json` (12,228). Rows resolve by stable hash,
not by the partially-present prefab-name column. Expected violations: **0** - the atlas already reports
0, and the point of re-running it here is that it goes through the *checker's* code path rather than the
atlas builder's.

Then, mechanically: for every real instance at distance `d` of type `X`, assert the checker does **not**
refuse `location:X nearest_distance near ceil(d)`, nor `far floor(d)`, nor `between floor(d)..ceil(d)`.
That is 36,829 x 3 = 110,487 goals that must all come back not-refused. One refusal here is a shipped
bug.

### T2 - no REFUSE ever rests on a sample

Assert on the report object: `goal.verdict == "refuse"` implies every `evidence[].kind` is in
`{code, asset, query}`. Run it over the corpora of T1 and T3. One line, and it is the structural
guarantee behind the whole design.

### T3 - property test: a refused goal is satisfied by no seed

Generate random goals over the 183 types and the 9 biomes - random metric, test and threshold drawn from
a distribution that straddles each bound (`Lo ± δ`, `Hi ± δ` for `δ ∈ {0.5, 1, 5, 100}` m, plus uniform
draws). For every goal the checker **refuses**, evaluate it exactly on `n = 2,048` seeds from the
Feistel permutation and assert **zero** passes. For every goal the checker calls **vacuous**, evaluate on
the same 2,048 and assert **2,048** passes. For every **degenerate** goal, assert each pass/fail is
explained by presence/absence of the target rather than by the threshold.

Failure of the vacuous half is less dangerous than failure of the refuse half but is still a correctness
bug; keep the assertions separate so the report says which. This is the slow test (a few hours at
~8 seeds/s for a full sweep): the full sweep runs in the release gate, a 128-seed version in the normal
test pass.

### T4 - the atlas agrees with the live computation

For all 232 types (not only the 183 running ones), assert
`atlas.locations[].radialLo == LocationFeasibility.LowerBound(liveType, 2)` and likewise for `radialHi`,
within 1e-6, and that `atlas.stamp == manifest.stamp`. Assert the atlas's biome bands match
`BiomeGeometry.Band` for `genVersion ∈ {0, 1, 2}`, **allowing the atlas to be tighter** (7,907.68 vs
7,900) but failing if the atlas is ever *looser* than the code-derived band.

### T5 - the shipped presets are all legal

Run the checker over all 13 embedded presets at their own grids. Assert **zero REFUSE**. This was run
against the atlas while writing this document and came back clean for all 55 location- and
biome-targeted goals across the 13 presets - including `boss-rush`'s `all-seven near 8,600 m`, whose
hard floor is `max_t lo(t) = 7,907.7 m` (FaderLocation), and `all-traders`' `all-three near 3,150 m`,
whose hard floor is 3,000 m (Hildir and the Bog Witch both carry `m_minDistance 3000`).

### T6 - worldGenVersion is honoured

Every bound is recomputed from `q.World.GenVersion`. Assert `biome:Swamp nearest_distance near 7,000 m`
is **refused** at `gen_version: 2` (band ends at 6,000 m) and **not refused** at `gen_version: 1`
(`VersionSetup` 253-256 raises `maxMarshDistance` to 8,000). Same for Mountain at `gen_version: 0`
(floor 1,100 m rather than 600 m). Hard-coding v2 would be a false refusal, which is the failure this
whole plan is about.

### T7 - the known-vacuous corpus is detected

A fixed list, each with its expected verdict:

| goal | expected |
|---|---|
| `gentle-start`'s `no-swamp`, `no-plains`, `no-mistlands` as shipped | Vacuous x3 |
| `location:Eikthyrnir nearest_distance near 1200` | Degenerate |
| `biome:AshLands nearest_distance far 5000` | Vacuous (constant) |
| `biome:AshLands area at_least 40e6` at G12 | Vacuous (constant, value 43,031,376 m2) |
| `biome:AshLands area at_least 44e6` at G12 | **Refuse** (constant, R11) |
| `location:Bonemass nearest_distance far 2000` | Vacuous (`m_minDistance 2000`) |
| `group:traders count at_most 3` | Vacuous (three `m_unique` types, cap 1 each) |
| `group:fuling_villages count at_least 201` | **Refuse** (`GoblinCamp2.m_quantity = 200`) |
| `location:Vendor_BlackForest count at_least 2` | **Refuse** (`m_unique`, cap 1) |
| `location:GoblinCamp2_1 nearest_distance near 5000` | **Refuse** (`m_biomeArea = 0`) |
| `location:FaderLocation nearest_distance near 2500 from spawn` | **Refuse** (R14, floor 2,800 m) |

and a matching must-be-**Ok** list drawn from the observed extremes: Haldor at 1,508 m, InfestedTree01
at 5,999.3 m, GoblinHut01 at 2,902.0 m and 7,994.8 m, Greydwarf_camp1 at 10,275.8 m, ShipWreck02_DN at
10,360 m, and `Mistlands_RoadPost1` at 5,912.0 m - the last one is below the *nominal* 6,000 m Mistlands
bound and is the test that the wobble is applied to the right endpoint.

### T8 - rate reporting

* `k = 0` never prints "0 %"; it prints `< 3/N`.
* `k < 10` prints the order-of-magnitude wording.
* the product-of-marginals estimator always prints "UPPER BOUND".
* a query whose columns are all in the calibration sample never falls back to the product estimator.
* a query at a grid with no matching column never uses the sample.

### T9 - the override surface

`--allow-impossible` and `--force` do not exist: assert the argument parser rejects them with a message
pointing at the goal to fix. `--no-prefilter` still prints the pre-flight report. `--allow-vacuous`
proceeds and stamps `allowVacuous: true` into the results manifest, so a result file records that it
came from a full scan.

---

## 8. Findings while writing this, and the open items

* **F1 - `BiomeGeometry.Band` returns 7,900 m for the AshLands/DeepNorth floor; the exact value is
  7,907.68 m.** Both are sound and 7,900 is looser, so this is not a bug - but the GUI should display
  the exact figure. Reproduced independently here by minimising `|p|` subject to
  `|p - (0, 4000)| > 12000 + 100 sin(20 atan2(x, z))`: **7,907.681 m at bearing 0.9766 pi**, matching the
  atlas to 0.001 m.
* **F2 - the `gentle-start` preset ships three vacuous `must` goals** (2.2, V3). Its effective must-set
  is two goals, and its description states a property of the generator rather than of the seeds it
  found. Worth fixing in the preset when the checker lands, so the checker's first real output is not a
  complaint about the tool's own shipped content.
* **F3 - a naive group upper bound (`min_t hi(t)`) declares `boss-rush`, `balanced` and
  `compact-progression` vacuous.** The prototype written for this document did exactly that before the
  rule in 1.2 was fixed. T5 is the regression test for it.
* **F4 - `LocationFeasibility`'s existing `hi` computation already uses `Math.Max` across the types and
  is correct today.** The new code must not "tidy" it into a `Math.Min`.
* **F5 - the one-per-zone caps in the atlas count zones whose 64 m square *intersects* the disc**, which
  is the over-approximating choice and therefore the right one. Verified: 89 / 225 / 837 / 3,197 /
  7,105 / 19,501 / 85,233 at R = 300 / 500 / 1,000 / 2,000 / 3,000 / 5,000 / 10,500 m.
* **Open - the calibration sample does not exist yet.** Until `vseed calibrate` ships, every rarity
  number must come from live calibration or be labelled an upper bound. The checker is useful without
  it: the REFUSE and VACUOUS tiers need no sample at all. WARN-RARE is the part that waits.
* **Open - `MetricCatalog` has no location-to-location distance metric**, so the pairwise machinery in
  3.5 has exactly one consumer today: `from: spawn`. The reference site the user compares against
  reports "Distance from center (km)", so that is the right priority - but if an inter-location metric
  is ever added, the triangle-inequality bounds are already specified here.
