# SeedLab decisions, part 2 — metrics, counts, and what ships (2026-09-23)

Companion to `decisions-1.md`. Same status: these are decisions, not suggestions.
An advisor review of the whole session produced the scoping rule at the end; obey it.

---

## 1. `coastline_length` — DELETE

The user asked to remove it and asked whether there was a consideration they were missing. There is
one, and it does not save the metric — but the reason to delete is NOT the one first given.

- The fractal/false-negative story is real but is a pure **offset**, not a ranking failure: the G24
  value is 0.756× the G12 value with p5..p95 within one point of that ratio, implied box dimension
  1.44–1.49. As a *ranking* it is the best of all the fine-only metrics (Spearman vs G12: 0.966 at
  G24, 0.894 at G48).
- **What actually kills it: it does not distinguish Valheim worlds.** At G12 over 2,560 seeds,
  p10/p50/p90 = 2,625,271 / 2,712,816 / 2,796,511 m, so p90/p10 = 1.065 and CV = 0.0245. The most
  coastal world in 2,560 has **8 % more coastline than the least**. A perfectly consistent ranking of
  an 8 % span is not a search criterion.
- Renaming it to an honest grid-relative shape index (option b) buys nothing measurable: land-cells-
  adjacent-to-water ranks the same (ρ 0.965/0.872/0.696) with the same CV (0.0221) — honest and
  equally useless.
- **The follow-up that must be answered in the same breath**, or the deletion looks arbitrary:
  `largest_island_area` (ρ 0.851) and `island_count` (0.831) rank *worse* than coastline at every grid
  and are KEPT. The distinction is **discrimination, not ranking fidelity** — those two have real
  spread between seeds; coastline has 8 %.
- `deepest_point` goes too, same class of defect with a stronger proof.

**Replacement — `world:shore_area_within` (radius required), unit m², tier T3:** the area of land
(height ≥ 30 m) whose cell centre lies within **100 m** of a water cell centre, inside a disc of
`radius` around the world centre. The 100 m band is **definitional, never a user parameter**, and the
printed name always says so: *"land within 100 m of water, inside 1,000 m of the centre, measured at
G12"*. Computed by an exact squared-Euclidean distance transform over the height field the T3 pass
already builds: +3.5 % cost (116.4 ms/seed at G12 against 2,818.6 ms for the pass). Ranks better than
coastline at every grid (0.988/0.949/0.862 at G24/G48/G96 for radius 1 km) **with 7× the spread**, and
is not a restatement of land area at the radius that matters (cross-correlation −0.034 at 1 km).
Hard refusals: grid > 50 m (the 100 m band cannot be resolved; at G164 and coarser it is 0 for every
seed), and no radius / radius ≥ 10,500 (at world scale its CV collapses to 0.022 — coastline's defect).

A retired-metric table gives a NAMED refusal for `coastline_length` and `deepest_point` explaining why
and what to use instead — never a generic "unknown metric".

## 2. Boss altar counts — the user's hypothesis, answered in two tiers

Measured over **5,000 uniformly drawn seeds**:

- **The seven `group:bosses` types never fell short**: Eikthyrnir 3, GDKing 4, Bonemass 5, Dragonqueen
  3, GoblinKing 4, the Queen 5, Fader 3 — 5,000/5,000 each. That bounds the shortfall rate at
  **≤ 0.060 % (95 %, rule of three)**. It is NOT the "fixed" the hypothesis proposed, and the tool must
  never state it as one. Dragonqueen has the thinnest headroom (max 2,117 attempts of 60,000, margin
  28× against 123–811× for the others), so it is the likeliest of the seven to have a tail.
- **`DN_Bossroom` — a boss altar in our own catalogue — falls short in 26 of 5,000 (0.520 %, CI
  0.355–0.761 %)**, every one having exhausted all 60,000 attempts, with the *altitude* filter
  rejecting 335,886 of 359,829 point tries on the examined seed. So "is a shortfall even theoretically
  possible" is answered **YES by counterexample**, not by argument. `Hildir_camp` 26/5,000 (0.520 %),
  `BogWitch_Camp` 1/5,000 (0.020 %).
- **PROVED upper bound**: `count ≤ m_quantity` always — `while (i < attempts && placed < m_quantity)`.
  **No lower bound exists in code**: after the loop there is no retry, no filter relaxation, no
  fallback, only `ZLog.LogWarning("Failed to place all ...")`. The game ships a code path for shortfall.
- **PROVED absence**: `GoblinCamp2_1` is absent from **every** seed — `m_biomeArea` is 0 while
  `GetBiomeArea` only ever returns Edge=1 or Median=2, so the filter always rejects. Confirmed 0/5,000.
- Alt-biome-gated types are commonly absent: TarPit2_1 92.4 %, TarPit1_1 73.5 %, TarPit3_1 51.4 %,
  StoneTowerRuins05_leet 48.1 %, GoblinHut03 34.0 %.

**Checker rules to SHIP:** D3 (world-wide count at N == Q → WARN-DEGENERATE, hard evidence for the cap,
sample for the rate), V5b (count/count_within `at_most N` with N ≥ Q → WARN-VACUOUS, hard evidence),
A1 (an absence line per type in one of exactly three shapes: PROOF only for an R8 type, MEASURED with a
Wilson interval, or MEASURED rule-of-three bound — never PROOF on sample evidence).
**CORRECTION to the checker design:** the count cap is `Q = sum of m_quantity`, not "sum of
maxCoexisting" — candidates of a unique type are counted individually.
**DEFER D4/D5** with the reason recorded: they need per-type count histograms in the calibration
sample; built against today's distance-percentile-only sample they would never fire and the suite would
pass for the wrong reason.
**Suggestion guard:** when a degeneracy stanza offers `count_within R` instead, it must pick R strictly
below min(radialHi) AND verify from the calibration sample that the value at that R actually varies —
at radialHi the count is constant for all eleven types.

## 3. Scoping rule — specification is outpacing implementation

As of this file: unimplemented are the feasibility checker (16 refuse + 10 vacuity + 7 rarity + 11
combination rules), the coastline removal and its replacement (13-step patch plan), the count rules,
three preset rewrites, the sampling ladder, resource-mode wiring, GUI parity, and three live defects
(the 35× estimate error, the `--all` memory blow-up, zero thread scaling on cheap queries).

**No further analysis workflows.** Order from here:
1. impl-1 finishes → re-run ALL FIVE gates (acceptance, location, natives, GoldenCheck, search tests).
2. ONE consolidation workflow: checker (D3/V5b/A1 + the atlas rules) + the metric patch with every
   anchor **re-grepped** (impl-1 edited those exact files; the patch plan's line numbers are stale) +
   preset rewrites + CLI and GUI parity.
3. ONE measurement pass on a QUIET machine — every throughput figure quoted so far was taken under
   contention and they disagree with each other.
4. ONE documentation update from that single source of numbers.
5. Stop and show the user a working tool.

**Honesty debt to close in step 4** — these were stated to the user as facts but are currently plans:
"the 7.34 TB case is structurally impossible" and "`--keep` becomes a real cap" (both are impl-1's
unreported work), and the throughput figures 4.6 days / 28 days / 4.42 days (different contention).

**The original requirement that is slipping:** the user's very first message asked for Linux/macOS
portability. Cross-architecture exactness is still untested (x64 only), and impl-1 is adding AVX2 —
which raises the stakes on the scalar/vector equivalence check rather than lowering them. The startup
self-test's fail-closed behaviour must be part of impl-1's verification, not a later milestone.

**Re-run the location gate before any of the 5,000-seed counts reach the user:** that study used a
07:00 snapshot of the Release DLLs while impl-1 was rebuilding `SeedLab.WorldGen`.
