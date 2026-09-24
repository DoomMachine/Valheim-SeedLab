# Third-party seed tools: reconciliation, and what is worth borrowing

Date: 2026-09-23. SeedLab data build `1.0.15-59f53fb5`.

Two public tools answer the same questions SeedLab does. The user asked (a) whether their
numbers agree with ours, (b) whether a different sampling grid could explain the gaps, and
(c) what their feature set suggests SeedLab should have. This records all three.

The tools:

| tool | build it claims | how it was read |
|---|---|---|
| **valheim.tools** `/seeds`, `/bosses`, `/traders` | "the 1.0 build" (not pinned) | page text pasted by the user; the CDN serves a bot challenge to automated fetches, which was not bypassed |
| **bobmitch.com/valheim** | `1.0.7 (current)`, selectable against `0.221.12 and earlier` | direct browser session; its own IndexedDB cache (`valheim-map` -> `worlds`) holds the generated site list, which was read out and compared instance by instance |
| **SeedLab** | `1.0.15` (`assembly_valheim` sha `59f53fb5...`) | `vseed locations <seed> --type all --top 0 --json` |

---

## 1. Verdict

**SeedLab and bobmitch agree. valheim.tools is the outlier.**

### 1.1 bobmitch vs SeedLab: every location instance, two seeds

bobmitch caches a whole generated world as `sites: { prefabName: [x, y, z, x, y, z, ...] }`.
That is directly comparable with `vseed locations`.

| | seed `bmbp74` (-825705586) | seed `138fmg` (509686548) |
|---|---|---|
| prefab types, bobmitch / SeedLab | 183 / 183 | 183 / 183 |
| prefab types present in one but not the other | **0** | **0** |
| location instances, bobmitch / SeedLab | **12,286 / 12,286** | **12,278 / 12,278** |
| per-type counts differing | **0 of 183** | **0 of 183** |
| per-type coordinate sets identical at 0.1 m | 155 of 183 | - |
| aggregate `sum(x)`, `sum(y)`, `sum(z)` over all instances | within 0.004 m | within 0.010 m |

The 28 types whose 0.1 m-rounded hashes differed on `bmbp74` are **rounding ties, not different
worlds**: inspected point by point they differ in one or two entries by exactly one unit in the
last printed decimal (`6068.6` vs `6068.5`, `2626.8` vs `2626.9`), and the per-type coordinate
sums agree to 0.004 m over as many as 500 points.

**Do not call this bit-exact.** A tie at one decimal requires a value of exactly k/20, which is
never a dyadic float, so identical inputs would have produced identical text. The ties therefore
mean the underlying floats really do differ, by something on the order of 1e-4 m. The likely
cause is arithmetic width: bobmitch runs its generator in JavaScript, where every number is
float64, while SeedLab reproduces the game's float32 arithmetic and is proven bit-exact against
two real worlds (see `proofs-and-gates.md`). SeedLab is the one with the bit-level proof; the
honest claim here is **"two independent implementations agree on every instance to better than
a decimetre"**.

### 1.2 valheim.tools vs both

Across the 9 headline seeds and 24 "best of the rest" seeds the page lists, **188 of 216 claimed
distances (87 %) reproduce on 1.0.15** within the tolerance the page's own 2-significant-figure
rounding implies. The residue is not a build gap. On `138fmg`, a disputed seed:

| altar | SeedLab 1.0.15 | bobmitch 1.0.7 | valheim.tools |
|---|---|---|---|
| Eikthyrnir | 50 | 50 | 50 |
| GDKing (the Elder) | 641 | 641 | 641 |
| Bonemass | 2,995 | 2,995 | 3,000 |
| **Dragonqueen (Moder)** | **3,344** | **3,344** | **2,000** |
| **GoblinKing (Yagluth)** | **4,190** | **4,190** | **5,400** |
| FaderLocation | 8,908 | 8,908 | 8,900 |
| DN_Bossroom | 9,068 | 9,068 | 9,100 |
| Vendor_BlackForest (Haldor) | 2,246 | 2,246 | 2,200 |

Both independent implementations agree to the metre, including on the two rows valheim.tools
disputes, and they agree on the spawn point the distances are measured from (`-324.1, -325.3`).
The 13 % residue belongs to valheim.tools, whose build and method are both unstated.

Haldor's disagreements remain a separate, structural matter: `Vendor_BlackForest` is `m_unique`
with `m_quantity 10`, so ten candidate sites are written and the game keeps whichever zone is
generated first. Distance to Haldor is **not seed-determined**. bobmitch corroborates this
independently - it draws all ten and greys them, captioned "candidate sites for a location the
game keeps only one of", and its own tooltip calls the distance a lower bound.

### 1.3 The sampling question, answered

The user asked whether a different sampling grid (128x128, or some other multiple) on the
third party's side could produce these gaps. **It cannot, for location distances.**

Location placement does not sample. `ZoneSystem.GetRandomPointByBiomes` walks the game's
hard-coded 2048x2048 point grid at 12 m spacing; there is no resolution to choose, which is why
`vseed locations` has no `--grid` option. A sampling grid is a SeedLab concept that applies to
*terrain* questions - biome share, land fraction, river counts - where a continuous field is
being integrated. It has no bearing on where an altar is.

The bobmitch comparison closes the question empirically: a wholly independent implementation,
whatever it samples, lands on the same 12,286 coordinates.

### 1.4 Does the 1.0.7 -> 1.0.15 build gap move locations?

**On the evidence here, no.** bobmitch is explicitly set to 1.0.7 and SeedLab's data is 1.0.15,
and across two seeds the two agree on all 183 prefab types, all 24,564 instances and their
heights. That does not prove the builds are identical - it is two seeds, and bobmitch's own note
says 1.0.7 and 0.221.12 *do* draw different worlds - but it removes "different build" as the
explanation for the valheim.tools residue.

---

## 2. What bobmitch has

Captured from the live page on 2026-09-23. Its menu: Locations, Base planner, Measure, Your save,
Live session, Replay, Seed finder, World, Support.

### 2.1 Locations panel - 10 groups, ~90 categories, 12,286 sites

`Essentials` (Spawn, Bosses, Forge of Potential) - `Traders & Quests` (Traders, Hildir) -
`Places of Mystery` - `Dungeons` (Sunken Crypts, Burial Chambers, Troll Caves, Frost Caves,
Infested Mines, Fire Holes, Morgen Holes, Bear Caves, Mork Borg, The Hole) - `Camps & Ruins`
(Fuling Villages, Greydwarf Camps, Draugr Villages, Swamp Huts, Forest Ruins, Plains Ruins,
Stone Tower Ruins, Abandoned Cabins, Fortresses, Charred Ruins, Charred Towers, Dvergr Guard
Towers, Dvergr Structures, Deep North Villages, Deep North Huts) - `Ore Deposits` (Copper, Tin,
Obsidian, Silver) - `Resources & Hazards` (Queen Bees, **Axe-Head Chests**, Turnip Seeds,
Leviathans, Tar Pits, Drake Nests, Volture Nests, Sulfur Arches, Lava Leviathans, Charred
Spawners, Dvergr Excavations, Rock Spires, Infested Trees, Giant Remains, Troll Remains, Ice
Ponds) - `Landmarks` (Stone Circles, Stone Henges, Ship Settings, Shipwrecks, Wells, Mountain
Graves, Swamp Graves, Waymarkers, Drake Lorestones, Combat Ruins, Big Rock Clearings, Giant
Swords, **Maypoles**, Frozen Ships, Memorial Places) - `Runestones` (per biome) - `Scenery`
(Abandoned Houses/Farms/Villages, Dolmens, Dvergr Road Posts, Dvergr Statues).

It also derives **sub-location** markers - individual dungeon and camp rooms - which its cache
stores separately as `camps` (`{host, room, x, y, z}`) and `campMaypoles`. On `bmbp74` it lists
14 camps and exactly 1 maypole. SeedLab has the same raw material in `roomchildren.json` but does
not place rooms in the world.

### 2.2 Seed finder - 21 criteria, 7 presets, over the same 2^32

Presets: `islandStart`, `walkableStart`, `theHook`, `hookAtSpawn`, `earlyAxes`, `ironRush`,
`custom`.

Criteria: `hookIntact`, `hookDistance`, `hookAfoot`, `landShare`, `meadowsShare`,
`blackForestShare`, `swampShare`, `mountainShare`, `plainsShare`, `spawnLandmass`, `timeHaldor`,
`dEikthyr`, `dElder`, `dBonemass`, `dModer`, `dYagluth`, `dHaldor`, `cryptCount`, `cryptNearest`,
`axeChestsAfoot`, `axeChestNearest`.

The interaction model is the interesting part, and it differs from SeedLab's:

- Every criterion carries a **weight** on 0..3 in steps of 0.25, used to **rank** the survivors.
- A criterion may *additionally* carry a **hard threshold** ("at least 2 bosses reachable on
  foot", "at most 2h 0m to the first three bosses", a tick for "only worlds with the trader
  reachable without a boat"), which filters.
- The status line is `0 of 4,294,967,296 worlds searched - 0 matched`: the same seed space
  SeedLab scans, scanned incrementally with results streaming in.
- Its disclaimer is worth matching in spirit: results are "ordered by the weights you set. That
  is not 'the best seed' - nothing in Valheim scores a world, and every weight here is a
  judgement."

### 2.3 Base planner - 30 criteria over sites within one world

Weighted 0..3 the same way, each with an optional hard requirement:
`pad` (flat ground), `treesOnPad`, `room`, `elevation`, `harbour` (beachable shore), `safeCoast`,
`riverMouth`, `shelter`, `defence` (few land approaches), `buffer` (distance from other biomes),
`dangerAdj`, `hostileLoc` (neighbours), `tinShore`, `copperArea`, `oreNear`, `silverNear`, `iron`
(crypt cluster), `cryptCount`, `silver` (mountain access), `farmland`, `dualFarm`, `plains`,
`ocean`, `ashlands`, `earlyBosses`, `lateBosses`, `moder`, `yagluth`, `trader`, `spawnWalk`,
`centrality`.

Shape parameters: `Spread sites apart` 100-1600 m, `Buffer from other biomes` 0-400 m,
`Distance to open water` -120..1040 m, `Pad tolerance` -4..16 m, `Merge within` 4-24 m,
`Base needs N pieces` 5-200.

Travel model: a vehicle radio (`foot`, `raft`, `karve`, `longship`, `drakkar`) plus `Reach` 1-60
minutes, and a "shade what is in reach" overlay. This is a real routing model, not Euclidean
distance - the same machinery behind `timeHaldor` and "reachable on foot" in the seed finder.

### 2.4 Everything else

`Measure` (ruler), `Your save` (drop a `.fwl`, `.fch`, `.db` or `.vmrec` to overlay revealed area,
map pins, ticked-off pins, build areas, terrain edits), `Live session` and `Replay` (a companion
mod streams position), a `View` layer stack (terrain / biome only / no map, relief shading, tree
cover, topographic contours at a chosen interval, biome variants split into dangerous / calm /
foraging / scenery, a GPU relief renderer with sun direction and height, cast shadows, sky
shading), `highest peaks`, a finer 16 m grid, and the `World` build selector.

---

## 3. What SeedLab should take from this

Ranked by what it buys, with the honest note that **only the feature design transfers** - their
numbers are 1.0.7 and 1.0.15 is what SeedLab answers for.

1. **Ranking, not just filtering.** This is the real gap. SeedLab's search is a predicate: a seed
   passes or it does not, and survivors come out unordered. bobmitch's weight-plus-threshold
   model lets a user say "I *need* two altars afoot, and among those I *prefer* a short walk to
   Haldor". An optional score - weights over the same goal values the checker already computes,
   applied to survivors only - costs nothing in the scan and changes how usable the output is.
   The funnel already materialises a survivor list, which is exactly where a ranking pass belongs.
2. **A walkable-landmass model.** `spawnLandmass`, `hookAfoot`, `axeChestsAfoot`, "bosses
   reachable on foot" and "time to Haldor" all rest on one primitive SeedLab does not have: a
   flood fill over walkable ground from the start temple. It is the highest-leverage addition,
   because six of their criteria fall out of it, and "can I get there without a boat" is the
   question players actually ask.
3. **Travel time over distance.** Given the flood fill, a coarse speed model (on foot, and per
   boat class) turns distance into minutes and makes "2 hours to the first three bosses" a query.
   Lower priority than the fill itself.
4. **Presets.** Seven named starting points beat an empty criterion list. SeedLab's query files
   can carry the same idea at no engineering cost - ship a handful of named example queries.
5. **Base planner.** The user called this a nice-to-have and it is. Note the structural difference
   already recorded in `docs/specs/07-features.md`: bobmitch has a planner, not a finder - it
   scores sites *within* one world. SeedLab could go further and make base quality a *search*
   criterion across seeds, but that is a large piece of work and belongs after the items above.
6. **Sub-location markers.** They place individual rooms (camps, the maypole). SeedLab dumps
   `roomchildren.json` but never places rooms. Worth doing only if a user asks for it.

**What not to take:**

- **Their taxonomy is not the naming authority.** The user's decision stands: the localization
  dump (`localization.json`, 6,258 entries) is authoritative for player-facing names, and the
  grouping is bosses A-Z, traders A-Z, then the remainder by biome. bobmitch's category names are
  a useful *cross-check* on the localization-derived names and a source of ideas for grouping the
  long tail - nothing more.
- **Their "Axe-Head Chests" label.** See below.

---

## 4. The axe-head houses (the user's missing "World Feature") - and the catch

**The data was always there; the query was not.** Exactly two location prefabs can yield an axe
head, and `locationchildren.json` names both:

| location prefab | item | localized name |
|---|---|---|
| `WoodHouse6` | `AxeHead1` | Curious Axe Head |
| `WoodHouse2` | `AxeHead2` | Mysterious Axe Head |

Nothing in `roomchildren.json` holds an axe head, so dungeon rooms are not a second source. On
`bmbp74` each prefab places 20 instances, and bobmitch agrees on all 40 coordinates.

**But the axe head is not seed-determined.** The chest's drop table is:

```
dropMin 2, dropMax 3, dropChance 1, oneOfEach true, 7 entries, totalWeight 8
  Feathers w1, Coins w1, Amber w1, ArrowFlint w1, Torch w1, Flint w1, AxeHead1 w2
```

and `DropTable.GetDropListItems` picks with `UnityEngine.Random.Range` - the global, unseeded
Unity RNG, rolled when the container spawns, not when the world is generated. The world seed
fixes **where the houses are**; it does not fix **which ones contain an axe head**.

Arithmetic on that table (draws without replacement, 2 or 3 draws with equal probability) puts
the chance that a chest yields its axe head at **31/56 = 55.4 %**. The two houses are not alike,
though: `WoodHouse6`'s chest is a plain child and is always there, while `WoodHouse2`'s is
`RandomSpawn` entry 50 with `m_chanceToSpawn 50`, so that house is **31/112 = 27.7 %**. *Derived
from the dumped table and the decompiled draw loop; not measured in game.*

The two uncertainties are of different kinds, which matters more than the numbers. The `RandomSpawn`
draw comes out of the zone-seeded stream `SpawnLocation` opens, so whether that chest exists **is**
seed-determined and SeedLab could compute it. What a chest contains never is. Collapsing both into
one percentage hides exactly that distinction.

So the feature SeedLab should ship is **"candidate axe-head houses"**: 40 sites per world, 20 of
them a little better than even money and 20 of them rather worse, and "the nearest one" is a lower
bound on the walk, in the same sense as Haldor. bobmitch's `axeChestsAfoot` / `axeChestNearest` and its "Axe-Head Chests" marker
category count the same candidate houses but label them as chests, which overclaims. SeedLab
should say candidate.

This is the same class of caveat as `m_unique` and deserves the same treatment in the UI: draw
them, count them, and say plainly what is and is not determined by the seed.

---

## 5. Evidence

- Instance-level comparison: bobmitch's IndexedDB `valheim-map` -> `worlds` ->
  `-825705586|2|1.0.7|9be6da8efba0` and `509686548|2|1.0.7|9be6da8efba0`, against
  `vseed locations <seed> --text --type all --top 0 --json`.
- Drop semantics: `DropTable.GetDropListItems` (decompiled 2026-09-23);
  `data/1.0.15-59f53fb5/locationchildren.json` -> `WoodHouse2`, `WoodHouse6`.
- Placement grid: `ZoneSystem.GetRandomPointByBiomes`, 2048x2048 points at 12 m.
- Haldor: `Vendor_BlackForest` `m_unique` with `m_quantity 10`;
  `ZoneSystem.RemoveUnplacedLocations`.
