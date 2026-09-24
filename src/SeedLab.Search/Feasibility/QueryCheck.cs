using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Render;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;
using SeedLab.WorldGen;

namespace SeedLab.Search.Feasibility
{
    /// <summary>The verdict tiers, in increasing severity.</summary>
    public enum CheckVerdict
    {
        Ok,

        /// <summary>Legal, but the sample says few seeds pass. Never blocking on its own.</summary>
        WarnRare,

        /// <summary>The goal has silently become a PRESENCE filter: it excludes only worlds where the target failed to place.</summary>
        WarnDegenerate,

        /// <summary>True for every seed. The filter rejects nothing, so a "targeted" search is a full scan.</summary>
        WarnVacuous,

        /// <summary>No seed can satisfy it. Hard evidence only; there is no override.</summary>
        Refuse,
    }

    /// <summary>One goal's check. Build a refusal only through <see cref="Refuse"/>.</summary>
    public sealed class GoalCheck
    {
        private readonly List<CheckEvidence> _evidence = new List<CheckEvidence>();

        public GoalCheck(Goal goal, MetricDef def)
        {
            Goal = goal;
            Def = def;
        }

        public Goal Goal { get; }
        public MetricDef Def { get; }
        public CheckVerdict Verdict { get; private set; } = CheckVerdict.Ok;
        public Feasible F { get; set; } = Feasible.Unknown;

        /// <summary>The sentence the CLI, the web API and the results file all use.</summary>
        public string Message { get; private set; } = "";

        /// <summary>The edit that fixes it, as the user would type it. Null when there is none.</summary>
        public string? Repair { get; private set; }

        /// <summary>The field the repair touches ("value", "radius", "grid") and its new value.</summary>
        public string? RepairField { get; private set; }

        public double RepairValue { get; private set; } = double.NaN;

        /// <summary>The grid this goal was DECIDED at, metres. A screen-then-verify run decides different goals at different grids.</summary>
        public double DecidedAtGrid { get; set; }

        /// <summary>The A1 absence line, when the goal needs the target to be present. Never a proof from a sample.</summary>
        public string? AbsenceLine { get; set; }

        public IReadOnlyList<CheckEvidence> Evidence => _evidence;

        /// <summary>
        /// The ONLY way to produce a refusal. The signature takes <see cref="HardEvidence"/>, so a
        /// measurement cannot reach this verdict: the structural half of the safety property that
        /// says a sample may never refuse a legal query.
        /// </summary>
        public GoalCheck Refuse(string message, string? repair, string? repairField, double repairValue,
                                params HardEvidence[] evidence)
        {
            if (evidence == null || evidence.Length == 0)
            {
                throw new ArgumentException("a refusal must name the code or asset it rests on", nameof(evidence));
            }

            Verdict = CheckVerdict.Refuse;
            Message = message;
            Repair = repair;
            RepairField = repairField;
            RepairValue = repairValue;
            _evidence.AddRange(evidence);
            return this;
        }

        /// <summary>A vacuity or degeneracy claim. Also hard-evidence only: it is a statement about every seed.</summary>
        public GoalCheck Warn(CheckVerdict verdict, string message, string? repair,
                              params HardEvidence[] evidence)
        {
            if (verdict == CheckVerdict.Refuse) throw new ArgumentException("use Refuse", nameof(verdict));
            if (verdict > Verdict) Verdict = verdict;
            Message = message;
            Repair = repair;
            _evidence.AddRange(evidence);
            return this;
        }

        /// <summary>A rarity warning. This is the one tier a measurement may produce.</summary>
        public GoalCheck WarnFromSample(string message, params CheckEvidence[] evidence)
        {
            if (Verdict < CheckVerdict.WarnRare) Verdict = CheckVerdict.WarnRare;
            if (Message.Length == 0) Message = message;
            else Message = Message + " " + message;
            _evidence.AddRange(evidence);
            return this;
        }

        /// <summary>
        /// The stamp gate: turn a refusal into a warning that names the mismatch.
        ///
        /// <para>This is the ONE place a verdict is allowed to move DOWN, and it needs its own method
        /// rather than <see cref="Warn"/>, which only ever raises severity - a downgrade written as
        /// <c>Warn(WarnRare, ...)</c> silently leaves the verdict at <see cref="CheckVerdict.Refuse"/>
        /// and the gate does nothing at all. T9 is the test that caught exactly that.</para>
        /// </summary>
        public void DowngradeToWarning(string mismatch)
        {
            if (Verdict != CheckVerdict.Refuse) return;
            Verdict = CheckVerdict.WarnRare;
            Message = Message + " -- NOT REFUSED: " + mismatch;
            _evidence.Add(HardEvidence.Query("data-stamp",
                "the constraint atlas and the location table name different builds"));
        }

        /// <summary>Adds a note without changing the verdict.</summary>
        public void Note(string text)
        {
            Message = Message.Length == 0 ? text : Message + " " + text;
        }
    }

    /// <summary>
    /// Everything the checker decided about one query, in the shape the CLI prints and the web API
    /// serialises - so the terminal and the page can never word the same fact differently.
    /// </summary>
    public sealed class QueryCheckReport
    {
        public const int CheckerVersion = 1;

        public List<GoalCheck> Goals = new List<GoalCheck>();

        /// <summary>Contradictions between goals, and query-level statements. Query evidence only.</summary>
        public List<string> QueryMessages = new List<string>();

        /// <summary>Usage errors: the query is malformed or outside this build's contract. No override.</summary>
        public List<string> UsageErrors = new List<string>();

        public string AtlasStamp = "";
        public string DataBuildTag = "";
        public bool AtlasAvailable;

        /// <summary>
        /// False when the atlas and the live game data name different builds. Every would-be REFUSE
        /// is then a warning carrying the mismatch: a game update is exactly the case where a hard
        /// bound quietly stops being hard, so the tool becomes less helpful, never wrong.
        /// </summary>
        public bool RefusalsEnabled = true;

        public string? StampMismatch;

        public CheckVerdict Verdict
        {
            get
            {
                CheckVerdict worst = CheckVerdict.Ok;
                foreach (GoalCheck g in Goals)
                {
                    if (g.Goal.Importance != Importance.Must && g.Verdict == CheckVerdict.Refuse) continue;
                    if (g.Verdict > worst) worst = g.Verdict;
                }

                return worst;
            }
        }

        /// <summary>Refused must-goals, which is what stops a run outright.</summary>
        public IEnumerable<GoalCheck> Refused
        {
            get
            {
                foreach (GoalCheck g in Goals)
                {
                    if (g.Verdict == CheckVerdict.Refuse && g.Goal.Importance == Importance.Must) yield return g;
                }
            }
        }

        public IEnumerable<GoalCheck> NotDiscriminating
        {
            get
            {
                foreach (GoalCheck g in Goals)
                {
                    if (g.Goal.Importance != Importance.Must) continue;
                    if (g.Verdict == CheckVerdict.WarnVacuous || g.Verdict == CheckVerdict.WarnDegenerate)
                    {
                        yield return g;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The pre-flight feasibility, vacuity and rarity check. It runs on a query BEFORE a seed is
    /// touched and decides whether the run is worth starting.
    ///
    /// <para>It <b>wraps</b> <see cref="StaticAnalysis"/> and <see cref="LocationFeasibility"/>
    /// rather than replacing them: those two already produce the REFUSE tier correctly and are
    /// already wired into <see cref="CompiledQuery.Compile"/>. What this adds is the vacuity tier,
    /// the degeneracy tier, the count rules, the combination rules and one report object that the
    /// CLI and the web API both render.</para>
    ///
    /// <para><b>The one soundness principle.</b> For each goal the checker builds an
    /// OVER-approximation <c>F</c> of the values the metric can take over all 4,294,967,296 seeds. It
    /// REFUSES only when <c>F ∩ accept = ∅</c> and calls a goal VACUOUS only when
    /// <c>F ⊆ accept</c>. Widening <c>F</c> can only turn a refusal into an OK, never the other way,
    /// so when two sound bounds disagree the LOOSER one decides.</para>
    /// </summary>
    public static class QueryCheck
    {
        /// <summary>The permissive relief every hard comparison is made with, metres.</summary>
        public const double Slack = LocationFeasibility.Slack;

        /// <summary>One grid cell of relief on an area comparison, for the same reason.</summary>
        public static double AreaSlack(FieldGrid grid) => grid.CellArea;

        /// <summary>The world's own edge. No distance or radius above it can mean anything (R12).</summary>
        public const double WaterEdge = 10500.0;

        /// <summary>
        /// Above this grid spacing <c>shore_area_within</c> is identically 0 in every seed, because no
        /// two distinct cell centres are closer than the spacing and the band is 100 m wide. That is a
        /// proof, not a measurement, and it is why the usage contract below is drawn at 50 m while the
        /// PROVABLE statement is drawn at 100 m.
        /// </summary>
        public const double ShoreProvablyZeroAbove = 100.0;

        /// <summary>The coarsest grid this build defines <c>shore_area_within</c> on. A contract, not a verdict.</summary>
        public const double ShoreCoarsestGrid = 50.0;

        public static QueryCheckReport Run(Query q, CompiledQuery cq, ILocationOracle oracle,
                                           ConstraintAtlas? atlas = null)
        {
            atlas ??= ConstraintAtlas.Load();
            QueryCheckReport r = new QueryCheckReport
            {
                AtlasAvailable = atlas.Available,
                AtlasStamp = atlas.Stamp,
                DataBuildTag = BuildTag(oracle),
            };

            // ---- the stamp gate --------------------------------------------------------------------
            if (atlas.Available && oracle.Available && r.DataBuildTag.Length > 0
                && !string.Equals(atlas.BuildTag, r.DataBuildTag, StringComparison.Ordinal))
            {
                r.RefusalsEnabled = false;
                r.StampMismatch =
                    "the constraint atlas was built for game " + atlas.BuildTag + " and the location "
                    + "table in use is " + r.DataBuildTag + ". Bounds are ADVISORY until the dumper is "
                    + "re-run and the atlas rebuilt: nothing will be refused, and every refusal below "
                    + "is printed as a warning instead. (The one exception is a contradiction between "
                    + "two goals, which needs no game data at all.)";
                r.QueryMessages.Add(r.StampMismatch);
            }
            else if (!atlas.Available)
            {
                r.QueryMessages.Add(atlas.UnavailableReason);
            }

            // ---- per-field sanity, before any goal is looked at (design 3.1) ------------------------
            Sanity(q, cq, r);

            double grid = cq.Grid.Spacing;
            foreach (CompiledGoal cg in cq.Goals)
            {
                GoalCheck gc = new GoalCheck(cg.Goal, cg.Def) { DecidedAtGrid = grid };
                r.Goals.Add(gc);

                if (!cg.Available)
                {
                    gc.Note(cg.UnavailableReason ?? "this build cannot measure this goal");
                    continue;
                }

                gc.F = Build(cg, q, cq, atlas);
                Decide(gc, cg, q, cq, atlas, r);
                Absence(gc, cg, atlas);
            }

            Contradictions(cq, r);
            SharedZoneBudget(cq, atlas, r);
            Downgrade(r);
            return r;
        }

        private static string BuildTag(ILocationOracle oracle)
        {
            // DumpedLocationOracle.Provenance is "1.0.15 / 59f53fb5 (232 entries, 183 ordered)".
            string p = oracle.Available ? oracle.Provenance : "";
            int paren = p.IndexOf('(');
            return paren > 0 ? p.Substring(0, paren).Trim() : p.Trim();
        }

        // =========================================================================================
        // F, per metric family
        // =========================================================================================

        private static Feasible Build(CompiledGoal cg, Query q, CompiledQuery cq, ConstraintAtlas atlas)
        {
            int gen = q.World.GenVersion;
            MetricDef def = cg.Def;
            List<CheckEvidence> ev = new List<CheckEvidence>();

            if (def.NeedsLocations) return BuildLocation(cg, gen, atlas, ev);

            if (def.Kind == TargetKind.Biome)
            {
                if (!MetricCatalog.TryParseBiome(cg.Goal.Target.Name, out Biome b)) return Feasible.Unknown;
                return BuildBiome(cg, b, gen, cq.Grid, atlas, ev);
            }

            if (def.Kind == TargetKind.World) return BuildWorld(cg, cq.Grid, ev);
            return Feasible.Unknown;
        }

        private static Feasible BuildLocation(CompiledGoal cg, int gen, ConstraintAtlas atlas,
                                              List<CheckEvidence> ev)
        {
            double lo = double.PositiveInfinity, hi = 0.0, loAll = 0.0;
            int placeable = 0;
            long quantity = 0;
            bool hiUnbounded = false;

            foreach (LocationTypeInfo t in cg.Types)
            {
                AtlasType? at = atlas.TypeOf(t.Prefab);
                if (!t.Placeable || at?.Impossible != null) continue;
                placeable++;
                quantity += t.Quantity;

                // Widen-F: the atlas and the live computation are both sound, so the LOOSER of the
                // two decides. The atlas is tighter for the AshLands floor (7,907.681 against the
                // band's 7,900); 7,900 is what the verdict rests on, and the exact figure is shown
                // beside it as a labelled value.
                double tl = LocationFeasibility.LowerBound(t, gen);
                double th = LocationFeasibility.UpperBound(t, gen);
                if (at != null)
                {
                    tl = Math.Min(tl, at.RadialLo);
                    th = Math.Max(th, at.RadialHi);
                }

                lo = Math.Min(lo, tl);
                if (tl > loAll) loAll = tl;
                if (double.IsPositiveInfinity(th)) hiUnbounded = true;
                else hi = Math.Max(hi, th);
            }

            if (placeable == 0) return Feasible.Unknown;
            if (hiUnbounded) hi = double.PositiveInfinity;
            if (double.IsPositiveInfinity(lo)) lo = 0.0;

            ev.Add(HardEvidence.Asset("m_minDistance / m_maxDistance / m_min-maxDistanceFromCenter",
                "the ring every accepted point satisfies; ZoneSystem.PlaceLocations:1956 applies it"));
            ev.Add(HardEvidence.Code("WorldGenerator.GetBiome",
                "the all-seed distance band of each biome in m_biome"));

            switch (cg.Def.Name)
            {
                case "nearest_distance":
                case "all_candidates_distance":
                    // The metric is a min over the types PRESENT, so if only the widest-ringed type
                    // placed, the measured value is that type's: the sound upper bound is the LARGEST
                    // hi in the group, never the smallest. A prototype of this design used min and
                    // promptly declared boss-rush, balanced and compact-progression vacuous.
                    return new Feasible(lo, hi, true, double.PositiveInfinity, false, ev);

                case "all_types_distance":
                    // max over types of nearest(t), and each nearest(t) >= lo(t), so the max is at
                    // least the largest lo. This is the one metric whose lower bound TIGHTENS as the
                    // group grows, and it follows from the metric's own definition.
                    return new Feasible(placeable > 1 ? loAll : lo, hi, true, double.PositiveInfinity, false, ev);

                case "count":
                case "count_within":
                {
                    // CORRECTION to the design: the cap is the sum of m_quantity, NOT the sum of
                    // "maxCoexisting" (1 for an m_unique type). Candidates of a unique type are
                    // counted individually - MetricDef.UniqueSemantics says so, and a trader with
                    // m_quantity 10 really does contribute up to 10 to this metric.
                    double cap = quantity;
                    double radius = cg.Def.Name == "count_within" ? cg.Goal.Radius : WaterEdge;
                    int zones = atlas.Available ? atlas.ZonesIntersecting(radius) : int.MaxValue;
                    ev.Add(HardEvidence.Asset("m_quantity",
                        "the placement loop is 'while (i < attempts && placed < m_quantity)', so a world "
                        + "holds at most " + Q(cap) + " of this target"));
                    if (atlas.Available)
                    {
                        ev.Add(HardEvidence.Code("ZoneSystem.RegisterLocation:2604-2611",
                            "one location per 64 m zone for ALL types combined; "
                            + Q(zones) + " zones meet a disc of " + Q(radius) + " m"));
                        cap = Math.Min(cap, zones);
                    }

                    return new Feasible(0, cap, false, 0, false, ev);
                }

                case "types_within":
                    ev.Add(HardEvidence.Asset("group membership",
                        placeable + " of the target's prefabs are in the list the placement run walks"));
                    return new Feasible(0, placeable, false, 0, false, ev);
            }

            return Feasible.Unknown;
        }

        private static Feasible BuildBiome(CompiledGoal cg, Biome b, int gen, FieldGrid grid,
                                           ConstraintAtlas atlas, List<CheckEvidence> ev)
        {
            (double min, double max) = BiomeGeometry.Band(b, gen);
            ev.Add(HardEvidence.Code("WorldGenerator.GetBiome", BiomeGeometry.Evidence(b, gen)));

            // The one biome whose cell set is identical in every seed: GetBiome tests IsAshlands
            // BEFORE the ocean cut (WorldGeneratorPort.GetBiome), and IsAshlands is position-only -
            // no noise, no m_offset, no seed. DeepNorth is NOT constant: its test is equally
            // seed-free but is applied AFTER 'baseHeight <= oceanLevel -> Ocean'.
            if (b == Biome.AshLands && IsConstantMetric(cg.Def.Name))
            {
                double v = AshLandsConstant.Value(cg.Def.Name, grid, cg.Goal.Radius);
                if (!double.IsNaN(v))
                {
                    ev.Add(HardEvidence.Code("WorldGenerator.IsAshlands:751-755, tested at GetBiome:796",
                        "position only - no noise, no seed - and tested before the ocean cut, so the "
                        + "AshLands cell set is identical in every seed on any fixed grid"));
                    return Feasible.Constant(v, ev);
                }
            }

            switch (cg.Def.Name)
            {
                case "nearest_distance":
                    return new Feasible(min, max, true, double.PositiveInfinity, false, ev);

                case "present":
                    return new Feasible(0, 1, false, 0, false, ev);

                case "area_within":
                    return new Feasible(0, BiomeGeometry.MaxArea(grid, min, max, cg.Goal.Radius), false, 0, false, ev);

                case "area":
                case "largest_patch_area":
                case "land_area":
                case "area_above_height":
                    return new Feasible(0, BiomeGeometry.MaxArea(grid, min, max, double.PositiveInfinity),
                                        false, 0, false, ev);

                case "share":
                    return new Feasible(0, 1, false, 0, false, ev);
            }

            return Feasible.Unknown;
        }

        private static Feasible BuildWorld(CompiledGoal cg, FieldGrid grid, List<CheckEvidence> ev)
        {
            double disc = BiomeGeometry.MaxArea(grid, 0, WaterEdge, double.PositiveInfinity);
            switch (cg.Def.Name)
            {
                case "land_area":
                case "water_area":
                case "largest_island_area":
                case "spawn_island_area":
                case "area_above_height":
                    ev.Add(HardEvidence.Code("WorldGenerator.GetBaseHeight:917-926",
                        "height is lerped to -0.2 from 10,000 m and to -2 from 10,490 m in every seed, "
                        + "so the 10,000-10,500 m ring is never land"));
                    return new Feasible(0, disc, false, 0, false, ev);

                case "land_area_within":
                case "shore_area_within":
                    ev.Add(HardEvidence.Query("radius", "the goal's own disc bounds the value"));
                    return new Feasible(0, BiomeGeometry.MaxArea(grid, 0, WaterEdge, cg.Goal.Radius),
                                        false, 0, false, ev);

                case "land_share":
                case "water_share":
                case "ocean_share":
                    return new Feasible(0, 1, false, 0, false, ev);

                case "nearest_land_distance":
                    ev.Add(HardEvidence.Code("GetBiomeHeight:1032-1035", "the world ends at 10,500 m"));
                    return new Feasible(0, WaterEdge, true, double.PositiveInfinity, false, ev);

                default:
                    // highest_peak, island_count, river/lake/stream counts: the generator gives no
                    // code-level bound at all, and inventing one is exactly how a false refusal
                    // ships. Say nothing rather than guess.
                    return Feasible.Unknown;
            }
        }

        private static bool IsConstantMetric(string metric) => metric switch
        {
            "area" or "share" or "nearest_distance" or "largest_patch_area" or "area_within" or "present" => true,
            _ => false,
        };

        // =========================================================================================
        // The verdict
        // =========================================================================================

        private static void Decide(GoalCheck gc, CompiledGoal cg, Query q, CompiledQuery cq,
                                   ConstraintAtlas atlas, QueryCheckReport r)
        {
            Goal g = cg.Goal;
            MetricDef def = cg.Def;

            // The refusals StaticAnalysis and LocationFeasibility already produce are re-published
            // here with their evidence attached, rather than re-derived - they are correct today and
            // they are what SearchCommand already prints.
            if (cg.Unsatisfiable != null)
            {
                gc.Refuse(cg.Unsatisfiable, RepairFor(gc, def), "value", double.NaN,
                          HardEvidence.Code("WorldGenerator.GetBiome / ZoneSystem.PlaceLocations",
                              "the all-seed geometry T0 decided this on"),
                          HardEvidence.Asset("the dumped ZoneLocation row", "quantity, ring and biome mask"));
                return;
            }

            // R8: a type the game can never place, proved from a dumped field against a decompiled
            // branch. LocationTypeInfo has no m_biomeArea, so this one needs the atlas.
            if (def.NeedsLocations && WantsPresence(cg))
            {
                foreach (LocationTypeInfo t in cg.Types)
                {
                    AtlasType? at = atlas.TypeOf(t.Prefab);

                    // DERIVED, not read. GetBiomeArea returns only Edge = 1 or Median = 2, so
                    // m_biomeArea == 0 makes '(location.m_biomeArea & biomeArea) == 0' true for every
                    // zone in every seed. The live table is hash-checked against manifest.json; the
                    // atlas is not, and trusting its verdict let an edited file produce a false
                    // refusal - the one failure this checker exists to prevent.
                    bool liveSaysImpossible = t.BiomeArea == 0;
                    bool atlasSaysImpossible = at?.Impossible != null;

                    if (atlasSaysImpossible && t.BiomeArea > 0)
                    {
                        gc.Note("the constraint atlas claims '" + t.Prefab + "' can never be placed, but "
                                + "the dumped location table gives it m_biomeArea " + t.BiomeArea
                                + ", which GetBiomeArea can match. The atlas is derived and is not "
                                + "hash-checked, so the TABLE wins and this goal is not refused. If the "
                                + "atlas was not edited by hand, re-generate it.");
                        continue;
                    }

                    if (!liveSaysImpossible) continue;
                    bool sole = cg.Types.Count == 1;
                    if (!sole && def.Name != "all_types_distance") continue;
                    gc.Refuse(
                        "'" + t.Prefab + "' can never be placed in any seed: its m_biomeArea is 0, and "
                        + "ZoneSystem.PlaceLocations rejects a zone unless "
                        + "(location.m_biomeArea & GetBiomeArea(zone)) is non-zero - GetBiomeArea only "
                        + "ever returns Edge = 1 or Median = 2, so no zone in any seed can match.",
                        sole ? "drop this goal, or target a type the game does place" : null,
                        null, double.NaN,
                        HardEvidence.Asset(t.Prefab + ".m_biomeArea",
                            "m_biomeArea = 0 in the dumped location table, which manifest.json hashes"),
                        HardEvidence.Code("ZoneSystem.PlaceLocations:1934",
                            "(location.m_biomeArea & biomeArea) == 0 rejects every zone"));
                    return;
                }
            }

            // R14 - 'from: spawn'. No centre-relative ring applies to a separation between two placed
            // things, but the triangle inequality between two annuli about a common origin does:
            //     sep(X, StartTemple) >= max(0, lo(X) - hi(StartTemple)),  hi(StartTemple) = 5,100 m
            // It is a genuine bound for the far-out types and NOTHING at all for the rest, which is
            // said out loud rather than printed as a bare 0.
            // METRES ONLY. The bound below is a separation in metres, and every sentence it prints
            // is about metres - so applying it to a metric whose unit is a COUNT compares a number of
            // instances against a number of metres and refuses a query that every seed satisfies.
            // Measured 2026-09-23: 'location:DN_Bossroom count_within radius 500 from spawn at_most 0'
            // was refused as impossible with the repair "near 2,800 m", which is not even a count;
            // the same goal measures 0 instances in 8 of 8 seeds. 68 of the 183 running types have a
            // floor above the temple's 5,100 m ceiling, so every one of them was exposed.
            if (g.From == DistanceOrigin.Spawn && def.NeedsLocations && def.Unit == Unit.Metres)
            {
                SpawnSeparation(gc, cg, q, atlas);
                return;
            }

            if (gc.F.IsUnknown)
            {
                gc.Note("no code-level bound exists for this metric, so nothing about it is refused or "
                        + "called vacuous; it is measured and ranked.");
                return;
            }

            double slack = def.Unit == Unit.Metres ? Slack
                         : def.Unit == Unit.SquareMetres ? AreaSlack(cq.Grid)
                         : def.Unit == Unit.Count ? 0.0 : 0.0;
            Accepted acc = Accepted.For(g.Test, g.Value, g.Max, gc.F.AbsentValue, slack);

            // ---- REFUSE: F ∩ accept = ∅ ------------------------------------------------------------
            bool anyReachable = Overlaps(gc.F, acc);
            if (!anyReachable)
            {
                string what = def.Kind + ":" + g.Target.Name + "." + def.Name;
                string message = gc.F.Exact
                    ? what + " measures " + Feasible.Num(gc.F.Lo, def.Unit) + " in EVERY seed - it is a "
                      + "constant of the generator, not a property of the seed - and that value does not "
                      + "satisfy this goal."
                    : what + " can only ever measure " + gc.F.Describe(def.Unit)
                      + ", so no seed satisfies '" + Phrase(g, def) + "'.";

                List<HardEvidence> hard = new List<HardEvidence>();
                foreach (CheckEvidence e in gc.F.Evidence)
                {
                    if (e is HardEvidence h) hard.Add(h);
                }

                if (hard.Count == 0)
                {
                    hard.Add(HardEvidence.Code("WorldGenerator", "the generator's own branch conditions"));
                }

                gc.Refuse(message, RepairFor(gc, def), "value", NearestSatisfiable(gc.F, g), hard.ToArray());
                return;
            }

            // ---- WARN-VACUOUS: F ⊆ accept ------------------------------------------------------------
            bool everythingPasses = acc.Contains(gc.F.Lo) && acc.Contains(gc.F.Hi)
                                    && (!gc.F.IncludesAbsent || acc.AcceptsAbsent);
            if (everythingPasses)
            {
                string message = gc.F.Exact
                    ? def.Kind.ToString().ToLowerInvariant() + ":" + g.Target.Name + "." + def.Name
                      + " measures " + Feasible.Num(gc.F.Lo, def.Unit) + " in EVERY seed, so this goal "
                      + "passes all 4,294,967,296 of them and rejects none."
                    : "this goal is true for every seed: " + def.Name + " can only ever measure "
                      + gc.F.Describe(def.Unit) + ", and '" + Phrase(g, def) + "' accepts all of that. "
                      + "It filters nothing.";

                gc.Warn(CheckVerdict.WarnVacuous, message,
                        g.Importance == Importance.Must
                            ? "delete the goal, or move the threshold inside the range above so it means something"
                            : "rank on a metric that varies between seeds - this one scores the same for every seed",
                        HardFrom(gc.F));
                return;
            }

            // ---- WARN-DEGENERATE: it excludes only worlds where the target failed to place -----------
            // A distance metric reports +infinity on absence, so a 'near D' with D at or above the
            // hard upper bound is not a no-op: it has become a PRESENCE filter. Different verdict,
            // different fix - which is why this is its own tier and not a flavour of vacuity.
            if (gc.F.IncludesAbsent && !acc.AcceptsAbsent
                && acc.Contains(gc.F.Lo) && acc.Contains(gc.F.Hi))
            {
                gc.Warn(CheckVerdict.WarnDegenerate,
                    "every instance that exists is already inside " + Feasible.Num(gc.F.Hi, def.Unit)
                    + " of the centre, so this goal is no longer a distance filter - it is 'a seed in "
                    + "which this target was placed at all'.",
                    "to filter on distance, ask for something inside "
                    + Feasible.Num(gc.F.Lo, def.Unit) + " .. " + Feasible.Num(gc.F.Hi, def.Unit),
                    HardFrom(gc.F));
                CountRuleNote(gc, cg, atlas);
                return;
            }

            CountRules(gc, cg, atlas);
            TypesWithinRules(gc, cg, atlas);
        }

        /// <summary>
        /// R14 - the provable separation between a target and the spawn point, from the triangle
        /// inequality between two annuli that share the origin.
        ///
        /// <para><c>StartTemple</c>'s own ring is the Meadows band, whose ceiling is 5,100 m
        /// (<c>GetBiome:833-836</c>: the branch before <c>return Meadows</c> is
        /// <c>if (num &gt; 5000 + A) return BlackForest</c>, and A is the +/-100 m wobble). So any
        /// type whose own floor is above 5,100 m cannot be placed within
        /// <c>lo(X) - 5,100</c> of wherever the temple ended up.</para>
        ///
        /// <para>The bound is sound and <b>very weak</b> - the empirical spawn radius is 0-499 m over
        /// 240 seeds and 71-228 m over three real worlds - so it is labelled a limit and never a
        /// likelihood, and when it comes out at 0 the report says "no provable constraint" instead of
        /// printing a zero that reads like an answer.</para>
        /// </summary>
        private static void SpawnSeparation(GoalCheck gc, CompiledGoal cg, Query q, ConstraintAtlas atlas)
        {
            const double StartTempleCeiling = 5100.0;
            double loX = double.PositiveInfinity;
            string who = "";
            foreach (LocationTypeInfo t in cg.Types)
            {
                if (!t.Placeable || atlas.TypeOf(t.Prefab)?.Impossible != null) continue;
                double tl = LocationFeasibility.LowerBound(t, q.World.GenVersion);
                AtlasType? at = atlas.TypeOf(t.Prefab);
                if (at != null) tl = Math.Min(tl, at.RadialLo);
                if (tl < loX) { loX = tl; who = t.Prefab; }
            }

            if (double.IsPositiveInfinity(loX)) return;
            double floor = Math.Max(0.0, loX - StartTempleCeiling);

            if (floor <= 0)
            {
                gc.Note("from: spawn - no provable constraint here: this target's ring and the spawn "
                        + "point's own 0 .. 5,100 m Meadows band overlap, so the two can be arbitrarily "
                        + "close. Any threshold you set is measured, not bounded.");
                return;
            }

            Goal g = cg.Goal;
            bool nearish = g.Test == GoalTest.Near || g.Test == GoalTest.AtMost;
            if (nearish && g.Value + Slack < floor)
            {
                gc.Refuse(
                    "'" + who + "' can never be placed closer than " + Q(loX) + " m to the centre, and "
                    + "the spawn temple can never be further than 5,100 m from it, so the two are at "
                    + "least " + Q(floor) + " m apart in every seed - '" + Phrase(g, cg.Def)
                    + " from spawn' is impossible.",
                    "nearest satisfiable value: near " + Q(Math.Ceiling(floor)) + " m",
                    "value", Math.Ceiling(floor),
                    HardEvidence.Asset(who + " ring", "floor " + Q(loX) + " m"),
                    HardEvidence.Code("WorldGenerator.GetBiome:833-836",
                        "StartTemple is Meadows-only and Meadows requires |p| <= 5000 + A, A in "
                        + "[-100, +100], so its ceiling is 5,100 m"));
                return;
            }

            gc.Note("from: spawn - provably " + Q(floor) + " m or more in every seed ('" + who
                    + "'s floor of " + Q(loX) + " m against the spawn's 5,100 m Meadows ceiling). That "
                    + "is a LIMIT, not a likelihood: the observed spawn radius is 0-499 m over 240 "
                    + "sampled seeds, so the real separation is much larger.");
        }

        /// <summary>
        /// D3 and V5b from the count decisions. Both rest on the same hard cap: the placement loop is
        /// <c>while (i &lt; attempts &amp;&amp; placed &lt; m_quantity)</c> and instances are only ever
        /// appended, so a world holds at most <c>Q = sum of m_quantity</c> of the target.
        ///
        /// <para><b>There is no lower bound in the code.</b> After the loop there is no retry, no
        /// filter relaxation and no fallback - only
        /// <c>ZLog.LogWarning("Failed to place all ...")</c>. The game ships a code path for a
        /// shortfall, and it is taken: <c>DN_Bossroom</c> fell short in 26 of 5,000 seeds. So a goal at
        /// <c>N == Q</c> is not a count filter any more, it is "every candidate placed".</para>
        ///
        /// <para><b>D4</b> is the same defect one step below the cap: <c>at_least N</c> with
        /// <c>0 &lt; N &lt; Q</c>, where the calibration sample never measured a value below
        /// <c>N</c>. Its trigger is a measurement, not an asset field, so it rests on
        /// <see cref="CountSample"/> - which is why it was deferred until that sample shipped, and
        /// why it stays silent rather than guessing when the sample is missing.</para>
        /// </summary>
        private static void CountRules(GoalCheck gc, CompiledGoal cg, ConstraintAtlas atlas)
        {
            CountSample sample = CountSample.Load(atlas);
            if (cg.Def.Name != "count" && cg.Def.Name != "count_within") return;
            Goal g = cg.Goal;
            double cap = gc.F.Hi;
            if (double.IsPositiveInfinity(cap)) return;

            long quantity = 0;
            foreach (LocationTypeInfo t in cg.Types)
            {
                if (t.Placeable && atlas.TypeOf(t.Prefab)?.Impossible == null) quantity += t.Quantity;
            }

            bool atLeast = g.Test == GoalTest.AtLeast || g.Test == GoalTest.Far;
            bool atMost = g.Test == GoalTest.AtMost || g.Test == GoalTest.Near;

            // D3: world-wide count at N == Q.
            if (atLeast && cg.Def.Name == "count" && Math.Abs(g.Value - quantity) < 0.5 && quantity > 0)
            {
                gc.Warn(CheckVerdict.WarnDegenerate,
                    "Q = " + Q(quantity) + " is the sum of m_quantity over this target, which is a HARD "
                    + "cap - the placement loop is 'while (i < attempts && placed < m_quantity)' and "
                    + "instances are only ever appended. Asking for exactly Q is therefore not a count "
                    + "filter: it is 'every candidate of every type placed'. There is no lower bound in "
                    + "the code - after the loop there is no retry and no fallback, only "
                    + "ZLog.LogWarning(\"Failed to place all ...\") - so a shortfall is a real code path.",
                    "ask for " + Q(quantity - 1) + " or fewer to filter on the count, or use "
                    + "count_within with a radius where the number actually varies",
                    HardEvidence.Asset("m_quantity", "summed over the target: " + Q(quantity)),
                    HardEvidence.Code("ZoneSystem.PlaceLocations",
                        "'while (i < attempts && placed < m_quantity)'; no retry after the loop"));
                CountRuleNote(gc, cg, atlas);
                return;
            }

            // D4: a world-wide count BELOW the cap that the sample has never seen missed. Unlike
            // D3 this cannot be decided from the assets - "does any seed have fewer than N?" is a
            // question about the distribution - so the trigger is measured and the verdict is a
            // warning. A sample that HAS seen the goal fail is exactly the case where the goal is
            // doing its job, and then D4 must stay quiet.
            if (atLeast && cg.Def.Name == "count" && quantity > 0
                && g.Value > 0.5 && g.Value + 0.5 < quantity && sample.Available)
            {
                List<string> d4Prefabs = new List<string>();
                foreach (LocationTypeInfo t in cg.Types)
                {
                    if (t.Placeable && atlas.TypeOf(t.Prefab)?.Impossible == null) d4Prefabs.Add(t.Prefab);
                }

                int below = sample.SeedsBelow(d4Prefabs, g.Value);
                if (below == 0)
                {
                    long min = sample.MinSum(d4Prefabs);
                    gc.Warn(CheckVerdict.WarnDegenerate,
                        "Q = " + Q(quantity) + " is the sum of m_quantity over this target, so this "
                        + "goal asks for " + Q(g.Value) + " of the at most " + Q(quantity)
                        + " a world can hold - but no seed was measured with fewer. MEASURED - 0 of "
                        + Q(sample.N) + " sampled seeds placed fewer than " + Q(g.Value)
                        + " (the smallest count seen was " + Q(min) + "), so this goal excludes at "
                        + "most " + Pct(Rates.RuleOfThree(sample.N)) + " of seeds (95 %, rule of "
                        + "three). That is a bound from a sample, NOT a proof: the generator has a "
                        + "code path for a shortfall and logs it, and three location types were "
                        + "measured taking it.",
                        "use count_within with a radius where the number actually varies, or "
                        + "nearest_distance, if you meant to filter on WHERE they are",
                        HardEvidence.Asset("m_quantity", "summed over the target: " + Q(quantity)),
                        HardEvidence.Code("ZoneSystem.PlaceLocations",
                            "'while (i < attempts && placed < m_quantity)'; no retry after the loop"));
                    CountRuleNote(gc, cg, atlas);
                    return;
                }
            }

            // V5b: at_most N with N >= Q. Hard evidence, so this really is vacuity and not a guess.
            if (atMost && g.Value + 0.5 >= cap)
            {
                gc.Warn(CheckVerdict.WarnVacuous,
                    "a world can hold at most " + Q(cap) + " of this target in any seed"
                    + (cap < quantity
                        ? " (the one-per-64-m-zone rule binds before m_quantity does at this radius)"
                        : " (the sum of m_quantity)")
                    + ", so 'at most " + Q(g.Value) + "' is true for every one of the 4,294,967,296 "
                    + "seeds and rejects none of them.",
                    "ask for at most " + Q(Math.Max(0, cap - 1)) + " or fewer, or drop the goal",
                    HardEvidence.Asset("m_quantity", "summed over the target: " + Q(quantity)),
                    HardEvidence.Code("ZoneSystem.RegisterLocation:2604-2611", atlas.OnePerZoneEvidence));
            }

            CountRuleNote(gc, cg, atlas);
        }

        private static void CountRuleNote(GoalCheck gc, CompiledGoal cg, ConstraintAtlas atlas)
        {
            if (cg.Def.Name != "count" && cg.Def.Name != "count_within") return;
            CountSample sample = CountSample.Load(atlas);
            if (!sample.Available) gc.Note("(" + sample.UnavailableReason + ".)");
        }

        /// <summary>
        /// D5 - <c>types_within</c> asking for EVERY type in the group, from the centre, at a radius
        /// at or beyond the widest type's ring ceiling. Every instance of every type is already
        /// inside that radius in every seed, so the radius has stopped selecting and the goal has
        /// become "every type in the group placed at all".
        ///
        /// <para>It is not vacuous - a seed CAN be missing a type, and three types were measured
        /// doing it - which is why this warns rather than refuses, and why the rate comes from the
        /// sample. The rate is a JOINT question ("at least one type missing"), so it is counted over
        /// the sample's own rows; it cannot be assembled from the per-type absence lines.</para>
        /// </summary>
        private static void TypesWithinRules(GoalCheck gc, CompiledGoal cg, ConstraintAtlas atlas)
        {
            Goal g = cg.Goal;
            if (cg.Def.Name != "types_within") return;
            if (g.Test != GoalTest.AtLeast && g.Test != GoalTest.Far) return;
            if (g.From != DistanceOrigin.Center) return;

            List<string> prefabs = new List<string>();
            double widest = 0.0;
            foreach (LocationTypeInfo t in cg.Types)
            {
                AtlasType? at = atlas.TypeOf(t.Prefab);
                if (!t.Placeable || at?.Impossible != null) continue;
                prefabs.Add(t.Prefab);
                if (at != null && at.RadialHi > widest) widest = at.RadialHi;
            }

            if (prefabs.Count == 0 || widest <= 0.0) return;
            if (Math.Abs(g.Value - prefabs.Count) > 0.5) return;
            if (g.Radius + Slack < widest) return;

            CountSample sample = CountSample.Load(atlas);
            int k = sample.Available ? sample.SeedsMissingAny(prefabs) : -1;
            string measured = k < 0
                ? "The calibration sample cannot measure the rate for this target, so none is quoted."
                : k == 0
                    ? "MEASURED - every one of " + Q(sample.N) + " sampled seeds had all of them, so "
                      + "this goal excludes at most " + Pct(Rates.RuleOfThree(sample.N))
                      + " of seeds (95 %, rule of three). That is a bound from a sample, not a proof."
                    : Wilson(k, sample.N, "were missing at least one of them") + ".";

            gc.Warn(CheckVerdict.WarnDegenerate,
                "every instance of every type in this target is already inside " + Q(widest)
                + " m of the centre in every seed (the widest ring ceiling in the group), and this "
                + "goal's radius is " + Q(g.Radius) + " m - so the radius selects nothing. Asking "
                + "for all " + Q(prefabs.Count) + " types at that radius is no longer 'how many of "
                + "them are near the centre'; it is 'a seed in which every one of them placed at "
                + "all'. " + measured,
                "lower the radius below " + Q(widest) + " m so that it selects, or ask for fewer "
                + "than " + Q(prefabs.Count) + " types",
                HardEvidence.Asset("m_maxDistance / m_maxDistanceFromCenter",
                    "the widest ring ceiling over the target: " + Q(widest) + " m"),
                HardEvidence.Asset("group membership",
                    prefabs.Count + " placeable prefabs in this target"));
        }

        /// <summary>
        /// A1: one absence line per type whose presence the goal needs, in exactly one of three
        /// shapes - a PROOF for a provably unplaceable type, a MEASURED rate with its Wilson interval,
        /// or a MEASURED rule-of-three bound when the sample saw none. Never "PROOF" on a sample.
        /// </summary>
        private static void Absence(GoalCheck gc, CompiledGoal cg, ConstraintAtlas atlas)
        {
            if (!cg.Def.NeedsLocations || !WantsPresence(cg) || !atlas.Available) return;

            // Types that share a statement share a line: seven boss altars each repeating the same
            // rule-of-three sentence is seven times the words and none of the information.
            List<string> order = new List<string>();
            Dictionary<string, List<string>> byStatement =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (LocationTypeInfo t in cg.Types)
            {
                AtlasType? at = atlas.TypeOf(t.Prefab);
                string statement;
                if (at?.Impossible != null)
                {
                    statement = "PROOF - never placed in any seed (" + at.Impossible + ")";
                }
                else
                {
                    AtlasAbsence? ab = atlas.AbsenceOf(t.Prefab);
                    if (ab == null || ab.N <= 0) continue;
                    int k = ab.SeedsWithNone >= 0 ? ab.SeedsWithNone
                          : ab.SeedsShortOfQuantity >= 0 ? ab.SeedsShortOfQuantity : -1;
                    if (k < 0) continue;
                    string what = ab.SeedsWithNone >= 0 ? "placed none at all" : "placed fewer than m_quantity";

                    statement = k == 0
                        ? "MEASURED - 0 of " + Q(ab.N) + " sampled seeds " + what + ", so at most "
                          + Pct(Rates.RuleOfThree(ab.N)) + " of seeds do (95 %, rule of three); that is "
                          + "a bound from a sample, NOT a proof that a shortfall is impossible - the "
                          + "game ships a code path for one"
                        : Wilson(k, ab.N, what);
                }

                if (!byStatement.TryGetValue(statement, out List<string>? names))
                {
                    byStatement[statement] = names = new List<string>();
                    order.Add(statement);
                }

                names.Add(t.Prefab);
            }

            if (order.Count == 0) return;
            List<string> lines = new List<string>(order.Count);
            foreach (string statement in order)
            {
                lines.Add(string.Join(", ", byStatement[statement]) + ": " + statement);
            }

            gc.AbsenceLine = string.Join(" | ", lines);
        }

        // =========================================================================================
        // Query-level rules
        // =========================================================================================

        private static void Sanity(Query q, CompiledQuery cq, QueryCheckReport r)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            double grid = cq.Grid.Spacing;

            if (!(grid >= 1.0 && grid <= 512.0))
            {
                r.UsageErrors.Add("search.grid = " + grid.ToString("0.###", ci)
                                  + " m is outside the 1 .. 512 m this build samples on");
            }

            foreach (CompiledGoal cg in cq.Goals)
            {
                Goal g = cg.Goal;
                string id = "goal '" + g.Id + "'";

                if (g.Test == GoalTest.Between && g.Value > g.Max)
                {
                    r.UsageErrors.Add(id + ": 'between' has value " + g.Value.ToString("N0", ci)
                                      + " above max " + g.Max.ToString("N0", ci) + " - the range is empty");
                }

                if (cg.Def.Unit == Unit.SquareMetres || cg.Def.Unit == Unit.Count)
                {
                    if (g.Value < 0 || (g.Test == GoalTest.Between && g.Max < 0))
                    {
                        r.UsageErrors.Add(id + ": a negative " + cg.Def.Unit.ToString().ToLowerInvariant()
                                          + " threshold cannot be measured");
                    }
                }

                // R12, and it is the user's own instruction: the world has a fixed radius, so no range
                // value may exceed it. The line is 10,500 m and not 10,000: real instances exist at
                // 10,360 m (ShipWreck02_DN) and 10,275.8 m (Greydwarf_camp1), and refusing above
                // 10,000 m would refuse legal queries.
                if (cg.Def.Unit == Unit.Metres && cg.Def.Kind != TargetKind.World)
                {
                    Over(r, id, "value", g.Value);
                    if (g.Test == GoalTest.Between) Over(r, id, "max", g.Max);
                }

                if (cg.Def.NeedsRadius) Over(r, id, "radius", g.Radius);

                // shore_area_within's contract. This is deliberately a USAGE error and not a
                // feasibility REFUSE: at G96 the metric is not zero (measured 0.724x its G12 value),
                // so refusing the query there would be a false claim about the seed space. What IS
                // provable is the line at 100 m - no two cell centres are closer than the spacing, so
                // above 100 m no land cell has water within the band and the metric is 0 everywhere.
                if (cg.Def.Kind == TargetKind.World && cg.Def.Name == "shore_area_within")
                {
                    if (grid > ShoreCoarsestGrid)
                    {
                        r.UsageErrors.Add(id + ": shore_area_within is not defined at G"
                            + grid.ToString("0.###", ci) + ". The 100 m band is a fixed physical width, "
                            + "and measured over 512 seeds the value is 0.939x its own G12 value at G24, "
                            + "0.793x at G48 and 0.724x at G96"
                            + (grid > ShoreProvablyZeroAbove
                                ? "; above " + ShoreProvablyZeroAbove.ToString("0", ci)
                                  + " m it is provably 0 for every seed, because no two cell centres on "
                                  + "this grid are closer than the spacing"
                                : "")
                            + ". Set \"grid\": 12, or use land_area_within if you want a metric that is "
                            + "safe to screen coarsely");
                    }

                    if (g.Radius >= WaterEdge)
                    {
                        r.UsageErrors.Add(id + ": shore_area_within needs a radius BELOW the 10,500 m "
                            + "water edge. At world scale its coefficient of variation collapses to "
                            + "0.022 over 512 seeds - p90/p10 = 1.06 - which is the same "
                            + "non-discrimination that retired coastline_length. Inside 1 km its CV is "
                            + "0.166. Use a radius a player would walk: 600 .. 2,000 m");
                    }
                }
            }
        }

        private static void Over(QueryCheckReport r, string id, string field, double v)
        {
            if (v > WaterEdge + Slack)
            {
                r.UsageErrors.Add(id + ": " + field + " = "
                    + v.ToString("N0", CultureInfo.InvariantCulture)
                    + " m is beyond the edge of the world. GetBiomeHeight returns -400 m past 10,500 m "
                    + "in every seed and nothing is placed out there, so no measurement above that "
                    + "radius can differ from the measurement at it. Clamp it to 10,500 m");
            }
        }

        /// <summary>
        /// R13 - two must-goals on the same metric key whose accepted intervals do not intersect, and
        /// the monotone-radius version of it. This is the one rule that needs no game data at all: it
        /// is a contradiction in the query's own text, so it survives a stamp mismatch.
        /// </summary>
        private static void Contradictions(CompiledQuery cq, QueryCheckReport r)
        {
            Dictionary<string, List<CompiledGoal>> byKey =
                new Dictionary<string, List<CompiledGoal>>(StringComparer.Ordinal);

            foreach (CompiledGoal cg in cq.Goals)
            {
                if (!cg.Available || cg.Goal.Importance != Importance.Must) continue;
                Goal g = cg.Goal;
                string key = g.Target + "." + g.Metric + "|r=" + g.Radius + "|h=" + g.Height
                             + "|a=" + g.MinArea + "|f=" + g.From;
                if (!byKey.TryGetValue(key, out List<CompiledGoal>? list))
                {
                    byKey[key] = list = new List<CompiledGoal>();
                }

                list.Add(cg);
            }

            foreach (KeyValuePair<string, List<CompiledGoal>> kv in byKey)
            {
                if (kv.Value.Count < 2) continue;
                Accepted joint = new Accepted(double.NegativeInfinity, double.PositiveInfinity, true);
                foreach (CompiledGoal cg in kv.Value)
                {
                    joint = joint.Intersect(Accepted.For(cg.Goal.Test, cg.Goal.Value, cg.Goal.Max,
                                                         double.NaN, Slack));
                }

                if (!joint.IsEmpty) continue;
                List<string> ids = new List<string>();
                foreach (CompiledGoal cg in kv.Value) ids.Add("'" + cg.Goal.Id + "'");
                r.QueryMessages.Add("goals " + string.Join(" and ", ids)
                    + " are on the same measurement and accept no value in common, so no seed can "
                    + "satisfy both. This is a contradiction in the query itself - it needs no game "
                    + "data, and it stands even when the data stamp does not match. Change one of them.");
            }

            // Counts and areas are monotone non-decreasing in the radius, so a floor at a small radius
            // above a ceiling at a larger one is the same contradiction wearing two radii.
            foreach (CompiledGoal a in cq.Goals)
            {
                if (!a.Available || a.Goal.Importance != Importance.Must || !a.Def.NeedsRadius) continue;
                foreach (CompiledGoal b in cq.Goals)
                {
                    if (ReferenceEquals(a, b) || !b.Available || b.Goal.Importance != Importance.Must) continue;
                    if (b.Def.Name != a.Def.Name || b.Goal.Target.ToString() != a.Goal.Target.ToString()) continue;
                    if (!(a.Goal.Radius <= b.Goal.Radius)) continue;

                    double minAtSmall = (a.Goal.Test == GoalTest.AtLeast || a.Goal.Test == GoalTest.Far
                                         || a.Goal.Test == GoalTest.Between) ? a.Goal.Value : double.NegativeInfinity;
                    double maxAtLarge = (b.Goal.Test == GoalTest.AtMost || b.Goal.Test == GoalTest.Near)
                        ? b.Goal.Value
                        : b.Goal.Test == GoalTest.Between ? b.Goal.Max : double.PositiveInfinity;

                    if (minAtSmall > maxAtLarge + 0.5)
                    {
                        r.QueryMessages.Add("goal '" + a.Goal.Id + "' needs at least "
                            + Q(minAtSmall) + " inside " + Q(a.Goal.Radius) + " m while goal '"
                            + b.Goal.Id + "' allows at most " + Q(maxAtLarge) + " inside the larger "
                            + Q(b.Goal.Radius) + " m disc. " + a.Def.Name + " cannot fall as the radius "
                            + "grows, so no seed satisfies both.");
                    }
                }
            }
        }

        /// <summary>
        /// Several <c>count_within</c> must-goals on DIFFERENT targets still compete for the same
        /// zones, because the one-per-zone rule binds all types together. This is what kills
        /// "12 Fuling villages within 300 m" even where the ring allows them, and it composes across
        /// goals rather than being a per-goal bound.
        /// </summary>
        private static void SharedZoneBudget(CompiledQuery cq, ConstraintAtlas atlas, QueryCheckReport r)
        {
            if (!atlas.Available) return;

            List<(double Radius, double Need, string Id)> demands = new List<(double, double, string)>();
            foreach (CompiledGoal cg in cq.Goals)
            {
                if (!cg.Available || cg.Goal.Importance != Importance.Must) continue;
                if (cg.Def.Name != "count_within") continue;
                if (cg.Goal.Test != GoalTest.AtLeast && cg.Goal.Test != GoalTest.Far
                    && cg.Goal.Test != GoalTest.Between) continue;
                if (!(cg.Goal.Value > 0)) continue;
                demands.Add((cg.Goal.Radius, cg.Goal.Value, cg.Goal.Id));
            }

            if (demands.Count < 2) return;
            demands.Sort((a, b) => a.Radius.CompareTo(b.Radius));

            // The sound statement is a RUNNING sum, not a total charged against the smallest disc.
            // Every instance demanded by a goal whose radius is at most R lies inside the R disc, so
            // those goals share the R disc's zone budget - but a goal at 10,500 m does not compete for
            // the zones inside 300 m, and charging it there would refuse
            //   'one Fuling village within 300 m' + '100 burial chambers within 10,500 m',
            // which is trivially satisfiable. The design's own formula (sum over ALL goals against
            // min radius) does exactly that; this is the corrected version.
            double running = 0;
            List<string> ids = new List<string>();
            foreach ((double radius, double need, string id) in demands)
            {
                running += need;
                ids.Add("'" + id + "'");
                int zones = atlas.ZonesIntersecting(radius);
                if (running <= zones) continue;

                r.QueryMessages.Add("goals " + string.Join(", ", ids) + " together need " + Q(running)
                    + " instances inside " + Q(radius) + " m, but only " + Q(zones)
                    + " of the game's 64 m zones meet that disc and the game refuses a second location "
                    + "in a zone for ALL types combined (" + atlas.OnePerZoneEvidence + "). No seed can "
                    + "hold them all.");
                return;
            }
        }

        /// <summary>
        /// The stamp gate applied: with a mismatch, every refusal becomes a warning naming it. R13
        /// survives, because a contradiction in the query text does not depend on the game build.
        /// </summary>
        private static void Downgrade(QueryCheckReport r)
        {
            if (r.RefusalsEnabled) return;
            foreach (GoalCheck g in r.Goals) g.DowngradeToWarning(r.StampMismatch ?? "the data stamps disagree");
        }

        // =========================================================================================
        // helpers
        // =========================================================================================

        private static bool Overlaps(Feasible f, Accepted a)
        {
            if (f.IncludesAbsent && a.AcceptsAbsent) return true;
            double lo = Math.Max(f.Lo, a.Lo), hi = Math.Min(f.Hi, a.Hi);
            return lo <= hi;
        }

        private static HardEvidence[] HardFrom(Feasible f)
        {
            List<HardEvidence> hard = new List<HardEvidence>();
            foreach (CheckEvidence e in f.Evidence)
            {
                if (e is HardEvidence h) hard.Add(h);
            }

            if (hard.Count == 0)
            {
                hard.Add(HardEvidence.Code("WorldGenerator", "the generator's own branch conditions"));
            }

            return hard.ToArray();
        }

        private static bool WantsPresence(CompiledGoal cg)
        {
            Goal g = cg.Goal;
            switch (cg.Def.Name)
            {
                case "count":
                case "count_within":
                case "types_within":
                    return (g.Test == GoalTest.AtLeast || g.Test == GoalTest.Far || g.Test == GoalTest.Between)
                           && g.Value > 0;
                case "nearest_distance":
                case "all_candidates_distance":
                case "all_types_distance":
                    return g.Test == GoalTest.Near || g.Test == GoalTest.AtMost || g.Test == GoalTest.Between;
                default:
                    return false;
            }
        }

        private static double NearestSatisfiable(Feasible f, Goal g)
        {
            switch (g.Test)
            {
                case GoalTest.Near:
                case GoalTest.AtMost:
                    return Math.Ceiling(f.Lo);
                case GoalTest.Far:
                case GoalTest.AtLeast:
                    return double.IsPositiveInfinity(f.Hi) ? double.NaN : Math.Floor(f.Hi);
                default:
                    return double.NaN;
            }
        }

        private static string? RepairFor(GoalCheck gc, MetricDef def)
        {
            double v = NearestSatisfiable(gc.F, gc.Goal);
            if (double.IsNaN(v) || gc.F.IsUnknown) return null;
            string verb = gc.Goal.Test switch
            {
                GoalTest.Near => "near ",
                GoalTest.AtMost => "at most ",
                GoalTest.Far => "far ",
                GoalTest.AtLeast => "at least ",
                _ => "",
            };
            return verb.Length == 0 ? null : "nearest satisfiable value: " + verb + Feasible.Num(v, def.Unit);
        }

        private static string Phrase(Goal g, MetricDef def)
        {
            if (g.Test == GoalTest.Between)
            {
                return "between " + Feasible.Num(g.Value, def.Unit) + " and " + Feasible.Num(g.Max, def.Unit);
            }

            string t = g.Test switch
            {
                GoalTest.AtMost => "at most",
                GoalTest.AtLeast => "at least",
                GoalTest.Near => "near",
                GoalTest.Far => "far",
                _ => g.Test.ToString().ToLowerInvariant(),
            };
            return t + " " + Feasible.Num(g.Value, def.Unit);
        }

        private static string Q(double v)
            => v.ToString("N0", CultureInfo.InvariantCulture);

        private static string Pct(double p)
            => (p * 100).ToString("0.###", CultureInfo.InvariantCulture) + " %";

        private static string Wilson(int k, int n, string what)
        {
            (double lo, double hi) = Rates.Wilson95(k, n);
            return "MEASURED - " + Q(k) + " of " + Q(n) + " sampled seeds " + what + ", "
                   + Pct((double)k / n) + " (95 % CI " + Pct(lo) + " .. " + Pct(hi) + ")";
        }
    }

    /// <summary>
    /// The AshLands constants, computed on the query's own grid.
    ///
    /// <para><b>No cached number is ever quoted.</b> 43,031,376 m2 and 12.4236 % are the values on the
    /// 12 m grid and on no other, and a <c>grid: 96</c> query gets a different number for the same
    /// provable reason. So the value is computed per grid, once, and cached by grid spacing.</para>
    /// </summary>
    public static class AshLandsConstant
    {
        private static readonly Dictionary<double, double[]> Cache = new Dictionary<double, double[]>();
        private static readonly object Gate = new object();

        /// <summary>area, share, nearest_distance, largest_patch_area, present - by index.</summary>
        public static double Value(string metric, FieldGrid grid, double radius)
        {
            double[] v = For(grid);
            switch (metric)
            {
                case "area": return v[0];
                case "share": return v[1];
                case "nearest_distance": return v[2];
                case "largest_patch_area": return v[3];
                case "present": return v[0] > 0 ? 1 : 0;
                case "area_within":
                    // A disc inside the AshLands floor holds none of it in any seed; beyond that the
                    // exact figure needs its own pass, and an over-approximation is what F wants
                    // anyway, so only the provable zero is reported as a constant.
                    return radius > 0 && radius + 1.0 < v[2] ? 0.0 : double.NaN;
                default: return double.NaN;
            }
        }

        private static double[] For(FieldGrid grid)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(grid.Spacing, out double[]? got)) return got;
                double[] v = Compute(grid);
                Cache[grid.Spacing] = v;
                return v;
            }
        }

        private static double[] Compute(FieldGrid grid)
        {
            long cells = 0, inWorld = 0;
            double nearest2 = double.PositiveInfinity;
            int n = grid.Size;
            for (int row = 0; row < n; row++)
            {
                float wz = grid.WorldZ(row);
                for (int col = 0; col < n; col++)
                {
                    float wx = grid.WorldX(col);
                    if (DUtils.Length(wx, wz) > 10500f) continue;
                    inWorld++;
                    if (!WorldGeneratorPort.IsAshlands(wx, wz)) continue;
                    cells++;
                    double d2 = (double)wx * wx + (double)wz * wz;
                    if (d2 < nearest2) nearest2 = d2;
                }
            }

            double area = cells * grid.CellArea;
            return new[]
            {
                area,
                inWorld == 0 ? 0.0 : (double)cells / inWorld,
                double.IsPositiveInfinity(nearest2) ? double.PositiveInfinity : Math.Sqrt(nearest2),
                area,   // AshLands is one 4-connected ring on every grid, so the patch is the whole set
            };
        }
    }
}
