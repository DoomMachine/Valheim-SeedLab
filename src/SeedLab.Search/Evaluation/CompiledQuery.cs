using System;
using System.Collections.Generic;
using SeedLab.Render;
using SeedLab.Search.Criteria;
using SeedLab.Search.Feasibility;
using SeedLab.Search.Locations;
using SeedLab.Search.Metrics;
using SeedLab.WorldGen;

namespace SeedLab.Search.Evaluation
{
    /// <summary>One goal, resolved against the metric catalogue and the data this build actually has.</summary>
    public sealed class CompiledGoal
    {
        public Goal Goal = null!;
        public MetricDef Def = null!;

        /// <summary><c>Heightmap.BiomeIndex</c> 0..9 for a biome goal, -1 otherwise.</summary>
        public int BiomeIndex = -1;

        /// <summary>Index into the matching accumulator list on the <see cref="MeasurementPlan"/>.</summary>
        public int Accumulator = -1;

        /// <summary>Location prefabs this goal covers, after group expansion. Empty for non-location goals.</summary>
        public IReadOnlyList<string> Prefabs = Array.Empty<string>();

        /// <summary>The same set, for the per-hit filter in <see cref="ReadLocations"/>.</summary>
        public HashSet<string> PrefabSet = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>What the oracle knows about each of <see cref="Prefabs"/>. Empty when unavailable.</summary>
        public IReadOnlyList<LocationTypeInfo> Types = Array.Empty<LocationTypeInfo>();

        /// <summary>
        /// The prefab of this goal that finishes LAST in the ordered placement list, or null when none
        /// of the goal's prefabs ever places. Once that entry's loop has returned, every instance this
        /// goal can ever see exists, so the goal is decided - which is what makes the early abort in
        /// <see cref="SeedEvaluator"/> exact rather than a guess.
        /// </summary>
        public string? LastPrefabInOrder;

        /// <summary>The ordered index of <see cref="LastPrefabInOrder"/>, or -1 when nothing places.</summary>
        public int LastOrderedIndex = -1;

        /// <summary>The m_unique prefabs in this goal's target, i.e. the ones whose instances are candidates.</summary>
        public IReadOnlyList<string> UniquePrefabs = Array.Empty<string>();

        /// <summary>
        /// The sentence <c>vseed explain</c> prints when the target contains an m_unique type, naming
        /// the semantics this metric used against the candidate set. Null when nothing is unique.
        /// </summary>
        public string? UniqueSemantics;

        /// <summary>
        /// What the seed does and does not decide about the CONTENTS of this goal's places, when one of
        /// its prefabs belongs to a curated <see cref="WorldFeature"/>. Null when none does.
        ///
        /// <para><b>Attached by prefab, never by group name.</b> The question behind it - "where are the
        /// houses with the axe heads" - is asked as <c>group:axe_head_houses</c> by one user and as
        /// <c>location:WoodHouse6</c> by the next, and a caveat that only the group carried would be
        /// missing from exactly the query that spells out the prefab. It is deliberately separate from
        /// <see cref="UniqueSemantics"/>: neither house is <c>m_unique</c>, so the unique channel never
        /// fires for them, which is how this caveat came to have no way of reaching the user at all.</para>
        /// </summary>
        public string? ContentsNote;

        public Tier Tier;

        /// <summary>False when this build cannot measure the goal at all. The reason is printed, never guessed around.</summary>
        public bool Available = true;

        public string? UnavailableReason;

        /// <summary>The disc this goal's measurement is contained by, metres.</summary>
        public double RegionRadius = SeedSampler.WaterEdge;

        /// <summary>
        /// True when the region restriction can leave the measured value unknown-but-beyond-the-region.
        /// Only ever set for a <c>must</c> goal whose verdict the restricted disc still decides exactly.
        /// </summary>
        public bool MayBeCensored;

        /// <summary>
        /// True when an explicit <c>search.region</c> made this goal's measurement disc SMALLER than
        /// the goal's own definition asks for. The number is then defined inside that disc - which is
        /// a legitimate thing to ask for ("how much Meadows within 2 km") and a dishonest thing to
        /// report as a whole-world figure, so every record it touches carries <c>bounded</c> and the
        /// radius, and the plan prints the change before the run starts.
        /// </summary>
        public bool RegionLimited;

        /// <summary>Set by T0 when no seed in the whole space can satisfy this goal.</summary>
        public string? Unsatisfiable;

        public override string ToString() => Goal.Id;

        /// <summary>Reads the measured value in the goal's own units.</summary>
        public double Read(WorldMeasurement m)
        {
            switch (Def.Kind)
            {
                case TargetKind.Biome:
                    switch (Def.Name)
                    {
                        case "area": return m.BiomeArea(BiomeIndex);
                        case "share": return m.BiomeShare(BiomeIndex);
                        case "nearest_distance": return m.NearestDistance(BiomeIndex);
                        case "largest_patch_area": return m.LargestPatchCells[BiomeIndex] * m.CellArea;
                        case "area_within": return m.AreaWithinCells[Accumulator] * m.CellArea;
                        case "present": return m.BiomeCells[BiomeIndex] > 0 ? 1 : 0;
                        case "land_area": return m.BiomeLandCells[BiomeIndex] * m.CellArea;
                        case "area_above_height": return m.AreaAboveCells[Accumulator] * m.CellArea;
                    }

                    break;

                case TargetKind.World:
                    switch (Def.Name)
                    {
                        case "ocean_share": return m.BiomeShare((int)BiomeIndex_Ocean);
                        case "land_area": return m.LandArea;
                        case "water_area": return m.WaterArea;
                        case "land_share": return m.LandShare;
                        case "water_share": return 1.0 - m.LandShare;
                        case "island_count": return m.IslandCounts[Accumulator];
                        case "largest_island_area": return m.LargestIslandCells * m.CellArea;
                        case "spawn_island_area": return m.SpawnIslandExists ? m.SpawnIslandCells * m.CellArea : 0.0;
                        case "land_area_within": return m.LandAreaWithinCells[Accumulator] * m.CellArea;
                        case "nearest_land_distance": return m.NearestLandDistance;
                        case "shore_area_within": return m.ShoreWithinCells[Accumulator] * m.CellArea;
                        case "highest_peak": return m.PeakHeight;
                        case "area_above_height": return m.AreaAboveCells[Accumulator] * m.CellArea;
                        case "river_count": return m.RiverCount;
                        case "lake_count": return m.LakeCount;
                        case "stream_count": return m.StreamCount;
                    }

                    break;
            }

            throw new InvalidOperationException("no reader for " + Def.Kind + "." + Def.Name);
        }

        /// <summary>
        /// Reads a location metric off a hit list. <paramref name="ox"/>/<paramref name="oz"/> is the
        /// origin: (0,0) for <c>from: center</c>, the <c>StartTemple</c> point for <c>from: spawn</c>.
        ///
        /// <para><b>An empty set measures +infinity, never 0.</b> "no Fuling village within 3 km" must
        /// pass on a world with no villages, and "a village within 3 km" must fail on it; both follow
        /// from infinity and neither follows from zero.</para>
        ///
        /// <para>For an <c>m_unique</c> prefab the hits are CANDIDATES. Which of the three distance
        /// metrics a goal uses is what decides what that means - see
        /// <c>MetricDef.UniqueSemantics</c>, which the report prints verbatim.</para>
        /// </summary>
        public double ReadLocations(IReadOnlyList<Locations.LocationHit> hits, double ox, double oz)
        {
            switch (Def.Name)
            {
                case "nearest_distance":
                {
                    double best = double.PositiveInfinity;
                    foreach (Locations.LocationHit h in hits)
                    {
                        if (!PrefabSet.Contains(h.Prefab)) continue;
                        double d = h.DistanceFrom(ox, oz);
                        if (d < best) best = d;
                    }

                    return best;
                }

                case "all_candidates_distance":
                {
                    double worst = double.NegativeInfinity;
                    foreach (Locations.LocationHit h in hits)
                    {
                        if (!PrefabSet.Contains(h.Prefab)) continue;
                        double d = h.DistanceFrom(ox, oz);
                        if (d > worst) worst = d;
                    }

                    // No instance at all: the goal is about instances, so it is unmet, not trivially met.
                    return double.IsNegativeInfinity(worst) ? double.PositiveInfinity : worst;
                }

                case "all_types_distance":
                {
                    double worst = 0.0;
                    foreach (string prefab in Prefabs)
                    {
                        double best = double.PositiveInfinity;
                        foreach (Locations.LocationHit h in hits)
                        {
                            if (!string.Equals(h.Prefab, prefab, StringComparison.Ordinal)) continue;
                            double d = h.DistanceFrom(ox, oz);
                            if (d < best) best = d;
                        }

                        if (double.IsPositiveInfinity(best)) return double.PositiveInfinity;
                        if (best > worst) worst = best;
                    }

                    return Prefabs.Count == 0 ? double.PositiveInfinity : worst;
                }

                case "count":
                {
                    long n = 0;
                    foreach (Locations.LocationHit h in hits)
                    {
                        if (PrefabSet.Contains(h.Prefab)) n++;
                    }

                    return n;
                }

                case "count_within":
                {
                    long n = 0;
                    foreach (Locations.LocationHit h in hits)
                    {
                        if (PrefabSet.Contains(h.Prefab) && h.DistanceFrom(ox, oz) <= Goal.Radius) n++;
                    }

                    return n;
                }

                case "types_within":
                {
                    HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (Locations.LocationHit h in hits)
                    {
                        if (PrefabSet.Contains(h.Prefab) && h.DistanceFrom(ox, oz) <= Goal.Radius) seen.Add(h.Prefab);
                    }

                    return seen.Count;
                }
            }

            throw new InvalidOperationException("no location reader for " + Def.Kind + "." + Def.Name);
        }

        private const int BiomeIndex_Ocean = 8;   // Heightmap.BiomeIndex.Ocean

        /// <summary>Does the measured value satisfy the goal?</summary>
        public bool Pass(double v) => Goal.Test switch
        {
            GoalTest.Near or GoalTest.AtMost => v <= Goal.Value,
            GoalTest.Far or GoalTest.AtLeast => v >= Goal.Value,
            GoalTest.Between => v >= Goal.Value && v <= Goal.Max,
            _ => false,
        };

        /// <summary>
        /// The nice-to-have sub-score, 07-features.md section 3.6. Clamped to [0,1] and monotone in the
        /// direction the goal asks for, so a score is always comparable between two seeds.
        /// </summary>
        public double Score(double v)
        {
            double a = Goal.Value;
            switch (Goal.Test)
            {
                case GoalTest.Near:
                    if (!double.IsFinite(v)) return 0.0;
                    return a <= 0 ? (v <= 0 ? 1 : 0) : Clamp01(1.0 - v / a);
                case GoalTest.Far:
                    if (!double.IsFinite(v)) return 1.0;
                    return a <= 0 ? 1 : Clamp01(v / a);
                case GoalTest.AtLeast:
                    if (double.IsPositiveInfinity(v)) return 1.0;
                    return a <= 0 ? 1 : Clamp01(v / a);
                case GoalTest.AtMost:
                    if (!double.IsFinite(v)) return 0.0;
                    return a <= 0 ? (v <= 0 ? 1 : 0) : Clamp01(1.0 - v / a);
                case GoalTest.Between:
                {
                    if (!double.IsFinite(v)) return 0.0;
                    if (v >= a && v <= Goal.Max) return 1.0;
                    double span = Goal.Max - a;
                    double pad = Goal.Pad * (span > 0 ? span : Math.Abs(a));
                    if (pad <= 0) return 0.0;
                    double d = v < a ? a - v : v - Goal.Max;
                    return Clamp01(1.0 - d / pad);
                }
            }

            return 0.0;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }

    /// <summary>A query turned into an evaluation plan: what to measure, how far out, and how far up the tiers.</summary>
    public sealed class CompiledQuery
    {
        public Query Query = null!;
        public FieldGrid Grid = null!;
        public MeasurementPlan Plan = new MeasurementPlan();
        public List<CompiledGoal> Goals = new List<CompiledGoal>();
        public List<string> Warnings = new List<string>();

        /// <summary>Goals T0 proved no seed can satisfy. A <c>must</c> goal here means the run is pointless.</summary>
        public List<CompiledGoal> Unsatisfiable = new List<CompiledGoal>();

        /// <summary>Goals this build cannot measure (they need the dumped location table).</summary>
        public List<CompiledGoal> Unavailable = new List<CompiledGoal>();

        public Tier MaxTier = Tier.T0;

        /// <summary>
        /// The location work this query needs, or null when it has no location goal. It carries the
        /// ordered prefix - the whole reason a boss query is not a 183-entry placement.
        /// </summary>
        public LocationPlan? Locations;

        /// <summary>Which game build the location table came from. Empty when no location goal ran.</summary>
        public string LocationProvenance = "";

        /// <summary>True when the audit mode is on: no region restriction, no T0 rejection, no early exit.</summary>
        public bool NoPrefilter;

        /// <summary>The explicit <c>search.region</c> that was applied, or 0 when the goals decided the disc.</summary>
        public double RegionRequested;

        public double TotalNiceWeight;

        public bool HasNiceGoals => TotalNiceWeight > 0;

        /// <summary>
        /// Resolves every goal, builds the shared measurement plan and runs the T0 static analysis.
        /// Nothing here touches a seed.
        /// </summary>
        public static CompiledQuery Compile(Query q, ILocationOracle oracle, bool noPrefilter = false)
        {
            CompiledQuery c = new CompiledQuery
            {
                Query = q,
                NoPrefilter = noPrefilter,
                Grid = SearchGrids.ForSpacing(q.Search.Grid),
            };

            if (q.Search.Grid != 12.0)
            {
                c.Warnings.Add("grid = " + q.Search.Grid + " m: every metric in this run is DEFINED on that grid and is "
                               + "not comparable with a G12 result (07-features.md section 2.1)");
            }

            if (q.Search.Approx)
            {
                c.Warnings.Add("approx = true, but this build ships no HEURISTIC prefilter, so the flag changes "
                               + "nothing except the 'approx' stamp on every record");
            }

            double region = 0.0;

            foreach (Goal g in q.Goals)
            {
                CompiledGoal cg = new CompiledGoal { Goal = g };
                MetricDef? def = MetricCatalog.Find(g.Target.Kind, g.Metric);
                if (def == null)
                {
                    // A metric this build DELIBERATELY removed gets its own refusal, naming the
                    // measurement that removed it and the edit that replaces it. "unknown metric"
                    // would read as a typo and send the user looking for a spelling that never
                    // existed, which is how a deliberate removal turns into a silent one.
                    RetiredMetric? retired = RetiredMetrics.Find(g.Target.Kind, g.Metric)
                                             ?? RetiredMetrics.FindAnyKind(g.Metric);
                    if (retired != null)
                    {
                        throw new QueryException("goals." + g.Id, retired.Message, retired.Instead);
                    }

                    throw new QueryException("goals." + g.Id,
                        "'" + g.Metric + "' is not a metric for a " + g.Target.Kind.ToString().ToLowerInvariant() + " target",
                        "available: " + string.Join(", ", MetricCatalog.NamesFor(g.Target.Kind)));
                }

                cg.Def = def;
                cg.Tier = def.Tier;

                if (def.NeedsRadius && !(g.Radius > 0))
                {
                    throw new QueryException("goals." + g.Id, "metric '" + g.Metric + "' needs a positive 'radius'");
                }

                if (def.NeedsHeight && !double.IsFinite(g.Height))
                {
                    throw new QueryException("goals." + g.Id, "metric '" + g.Metric + "' needs a 'height'");
                }

                Biome biome = Biome.None;
                if (g.Target.Kind == TargetKind.Biome)
                {
                    if (!MetricCatalog.TryParseBiome(g.Target.Name, out biome))
                    {
                        throw new QueryException("goals." + g.Id, "'" + g.Target.Name + "' is not a biome",
                            "one of: " + string.Join(", ", MetricCatalog.BiomeNames));
                    }

                    cg.BiomeIndex = biome.ToGameIndex();
                }

                // ---- location goals: expressible now, measurable when the dump exists -------------------
                if (def.NeedsLocations)
                {
                    cg.Prefabs = ResolvePrefabs(g, oracle);
                    cg.PrefabSet = new HashSet<string>(cg.Prefabs, StringComparer.Ordinal);
                    if (!oracle.Available)
                    {
                        cg.Available = false;
                        cg.UnavailableReason = oracle.UnavailableReason;
                    }
                    else
                    {
                        ResolveTypes(cg, g, oracle);
                    }
                }

                if (g.From == DistanceOrigin.Spawn)
                {
                    // A biome or world metric is measured on the sampling grid, and every distance on
                    // that grid is from the ORIGIN - WorldMeasurement has no other origin to offer. So
                    // 'from: spawn' on one of them would quietly return a centre-relative number under
                    // a spawn-relative name. Refuse it instead; the location metrics below do honour
                    // the origin, because they measure placed positions rather than grid cells.
                    if (def.Kind != TargetKind.Location && def.Kind != TargetKind.Group)
                    {
                        throw new QueryException("goals." + g.Id,
                            "'from: spawn' is only supported on a location: or group: metric",
                            def.Kind.ToString().ToLowerInvariant() + ":" + g.Target.Name + "." + g.Metric
                            + " is measured on the sampling grid, where every distance is from the world "
                            + "centre - there is no spawn-relative reading of it that this build can give "
                            + "exactly, so it is refused rather than answered from the wrong origin");
                    }

                    if (!oracle.Available)
                    {
                        cg.Available = false;
                        cg.UnavailableReason = "from: spawn measures from the StartTemple point, which "
                                               + oracle.UnavailableReason;
                    }
                }

                if (!cg.Available)
                {
                    c.Unavailable.Add(cg);
                    c.Goals.Add(cg);
                    continue;
                }

                // A metric whose number at one grid is not an approximation of its number at another
                // says so once per query, in the same sentence the record and the report use. The
                // failure this prevents is not a wrong verdict - it is somebody comparing a G96
                // island count with a G12 one in a month and believing the difference.
                if (!def.GridComparable && c.Grid.Spacing > 12.0)
                {
                    c.Warnings.Add("goal '" + g.Id + "' uses " + def.Name + ", which is NOT comparable "
                                   + "across grids: " + def.GridNote + ". This run measures it at G"
                                   + c.Grid.Spacing.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                                   + ", so its number is that grid's number and nothing else"
                                   + (g.Importance == Importance.Must
                                       ? " - and as a MUST-HAVE with a threshold taken from another grid it "
                                         + "will reject real matches"
                                       : ""));
                }

                // ---- T0: can any seed at all satisfy this? ----------------------------------------------
                string? impossible = def.NeedsLocations
                    ? LocationFeasibility.Check(cg, cg.Types, q.World.GenVersion)
                    : StaticAnalysis.Check(cg, biome, q.World.GenVersion, c.Grid);
                if (impossible != null)
                {
                    cg.Unsatisfiable = impossible;
                    c.Unsatisfiable.Add(cg);
                }

                // ---- region of interest ------------------------------------------------------------------
                // A location metric is measured off the placed instance list, not off the sampling grid,
                // so the grid's region restriction neither helps it nor applies to it. Leaving it at
                // the whole world also keeps a location-only query from shrinking the disc that the
                // record's side numbers are measured in.
                cg.RegionRadius = SeedSampler.WaterEdge;
                if (def.NeedsLocations)
                {
                    // nothing: see above
                }
                else if (def.Kind == TargetKind.World && def.Name == "shore_area_within")
                {
                    // The ONLY metric whose disc is wider than its own radius. It asks whether a land
                    // cell inside the radius has water within 100 m, and that water may lie outside
                    // the radius - so the sampled disc has to carry the band with it. Taking
                    // g.Radius here would silently drop shore on the rim of the disc, which is
                    // exactly where a builder's shoreline is.
                    cg.RegionRadius = Math.Min(SeedSampler.WaterEdge,
                                               g.Radius + WorldMeasurement.ShoreBandM);
                }
                else if (def.NeedsRadius)
                {
                    cg.RegionRadius = Math.Min(SeedSampler.WaterEdge, g.Radius);
                }
                else if (def.Kind == TargetKind.Biome && def.Name == "nearest_distance"
                         && g.Importance == Importance.Must
                         && (g.Test == GoalTest.Near || g.Test == GoalTest.AtMost) && !noPrefilter)
                {
                    // EXACT. "nearest <= D" is true iff a cell of the biome lies inside the disc of
                    // radius D, so the disc decides the verdict with no error at all. The reported
                    // number is then censored to "> D", which is recorded on the record; the verdict,
                    // which is the only thing a must-goal contributes, is exact.
                    cg.RegionRadius = Math.Min(SeedSampler.WaterEdge, g.Value);
                    cg.MayBeCensored = true;
                }

                if (noPrefilter) cg.RegionRadius = SeedSampler.WaterEdge;
                region = Math.Max(region, cg.RegionRadius);

                // ---- what has to be measured --------------------------------------------------------------
                switch (def.Kind)
                {
                    case TargetKind.Biome:
                        if (def.Name == "area_within") cg.Accumulator = c.Plan.AddAreaWithin(cg.BiomeIndex, g.Radius);
                        else if (def.Name == "area_above_height") cg.Accumulator = c.Plan.AddAreaAbove(cg.BiomeIndex, g.Height);
                        else if (def.Name == "largest_patch_area") c.Plan.NeedLargestPatch[cg.BiomeIndex] = true;
                        break;

                    case TargetKind.World:
                        switch (def.Name)
                        {
                            case "island_count": cg.Accumulator = c.Plan.AddIslandMinArea(g.MinArea); break;
                            case "largest_island_area":
                            case "spawn_island_area": c.Plan.NeedIslands = true; break;
                            case "shore_area_within": cg.Accumulator = c.Plan.AddShoreWithin(g.Radius); break;
                            case "area_above_height": cg.Accumulator = c.Plan.AddAreaAbove(-1, g.Height); break;
                            case "land_area_within": cg.Accumulator = c.Plan.AddLandAreaWithin(g.Radius); break;
                        }

                        break;
                }

                // T5 is NOT a height tier: a location goal reads the placement engine's own grid, not
                // this one, so a location-only query samples nothing here at all.
                if (def.Tier == Tier.T2) c.Plan.NeedBiomes = true;
                if (def.Tier == Tier.T3 || def.Tier == Tier.T4) { c.Plan.NeedHeights = true; c.Plan.NeedBiomes = true; }
                if (def.Tier == Tier.T4) c.Plan.NeedStructures = true;
                if (def.Tier > c.MaxTier) c.MaxTier = def.Tier;
                if (g.Importance == Importance.Nice) c.TotalNiceWeight += g.Weight;
                c.Goals.Add(cg);
            }

            c.Plan.Radius = region <= 0 ? SeedSampler.WaterEdge : region;

            // ---- search.region: exact, free, and the biggest lever in the tool ----------------------
            //
            // Measured on this machine: a 1 km goal at the game's own 12 m grid costs 8.16 ms/seed
            // against 1,324 ms for the same goal over the whole world - 162x - with no loss of any
            // kind, because a metric bounded by a disc cannot be changed by a cell outside it. Where
            // the disc is SMALLER than a goal's own definition the goal is still measured, but inside
            // the disc: that is a different number, so it is marked bounded on every record and named
            // in a warning here rather than quietly relabelled.
            double requested = q.Search.Region;
            if (requested > 0 && !noPrefilter && requested < c.Plan.Radius)
            {
                List<string> redefined = new List<string>();
                double floor = requested;
                foreach (CompiledGoal cg in c.Goals)
                {
                    if (!cg.Available || cg.Def.NeedsLocations) continue;
                    if (cg.RegionRadius > requested)
                    {
                        cg.RegionLimited = true;
                        cg.RegionRadius = requested;
                        redefined.Add(cg.Goal.Id);
                    }

                    // shore_area_within is the one metric the disc cannot simply be cut to: it needs
                    // the 100 m band around the land it counts, and a band that fell outside the
                    // sampled disc would silently report less shore rather than a bounded number. So
                    // the disc keeps the band, and the goal is still marked region-limited.
                    if (cg.Def.Kind == TargetKind.World && cg.Def.Name == "shore_area_within")
                    {
                        double want = Math.Min(SeedSampler.WaterEdge,
                                               cg.RegionRadius + WorldMeasurement.ShoreBandM);
                        cg.RegionRadius = want;
                        if (want > floor) floor = want;
                    }
                }

                c.Plan.Radius = floor;
                c.RegionRequested = requested;
                if (redefined.Count > 0)
                {
                    c.Warnings.Add("search.region = " + requested.ToString("0.###",
                                       System.Globalization.CultureInfo.InvariantCulture)
                                   + " m: " + string.Join(", ", redefined)
                                   + (redefined.Count == 1 ? " is" : " are")
                                   + " now measured inside that disc rather than over the whole 10,500 m world, "
                                   + "so the number is region-limited and is marked 'bounded' on every record. "
                                   + "The restriction is EXACT for what it measures and samples "
                                   + Execution.RunEstimator.RegionSpeedup(requested).ToString("0",
                                         System.Globalization.CultureInfo.InvariantCulture)
                                   + "x fewer cells than the whole world (the speed-up is smaller than "
                                   + "that: part of every seed is generator construction, which no disc "
                                   + "removes)");
                }
            }

            // A nice-to-have needs its real value for the score, so it never accepts a censored region.
            foreach (CompiledGoal cg in c.Goals)
            {
                if (cg.MayBeCensored && cg.RegionRadius < c.Plan.Radius) cg.MayBeCensored = false;
            }

            BuildLocationPlan(c, oracle);
            return c;
        }

        /// <summary>
        /// One shared plan for every location goal in the query: the union of their prefabs, and the
        /// prefix that makes ALL of them exact - the maximum of the per-type prefixes, because a type's
        /// instances are final only once its own ordered entry has run.
        /// </summary>
        private static void BuildLocationPlan(CompiledQuery c, ILocationOracle oracle)
        {
            List<string> prefabs = new List<string>();
            bool needSpawn = false;
            bool any = false;

            foreach (CompiledGoal cg in c.Goals)
            {
                if (!cg.Available) continue;
                if (cg.Goal.From == DistanceOrigin.Spawn) { needSpawn = true; any = true; }
                if (!cg.Def.NeedsLocations) continue;
                any = true;
                foreach (string p in cg.Prefabs)
                {
                    if (!prefabs.Contains(p)) prefabs.Add(p);
                }
            }

            if (!any || !oracle.Available) return;

            c.Locations = oracle.Plan(prefabs, needSpawn);
            c.LocationProvenance = oracle.Provenance;

            if (c.Locations.PrefixLength >= c.Locations.OrderedCount && c.Locations.OrderedCount > 0)
            {
                c.Warnings.Add("this query reaches ordered entry " + c.Locations.PrefixLength + " of "
                               + c.Locations.OrderedCount + ", so every seed pays for the FULL location "
                               // --metrics, not --groups: there is no --groups flag, and the group
                               // vocabulary with each group's prefix prints under --metrics. Sending
                               // a user to a flag that does not exist is a worse answer than none.
                               + "placement; a goal on an earlier type is far cheaper (vseed search --metrics "
                               + "prints each group's prefix)");
            }
        }

        /// <summary>
        /// Looks each of a location goal's prefabs up in the oracle, records what is unique, and works
        /// out which of them the ordered list finishes last.
        /// </summary>
        private static void ResolveTypes(CompiledGoal cg, Goal g, ILocationOracle oracle)
        {
            List<LocationTypeInfo> types = new List<LocationTypeInfo>(cg.Prefabs.Count);
            List<string> unique = new List<string>();
            int lastIndex = -1;

            foreach (string p in cg.Prefabs)
            {
                LocationTypeInfo? t = oracle.TypeOf(p);
                if (t == null)
                {
                    throw new QueryException("goals." + g.Id,
                        "'" + p + "' is not a location prefab in the dumped table"
                        + (g.Target.Kind == TargetKind.Group ? " (reached through group '" + g.Target.Name + "')" : ""),
                        Suggest(p, oracle));
                }

                types.Add(t);
                if (t.Unique) unique.Add(p);
                if (t.Placeable && t.OrderedIndex > lastIndex)
                {
                    lastIndex = t.OrderedIndex;
                    cg.LastPrefabInOrder = p;
                    cg.LastOrderedIndex = t.OrderedIndex;
                }
            }

            cg.Types = types;
            cg.UniquePrefabs = unique;
            if (unique.Count > 0 && cg.Def.UniqueSemantics.Length > 0)
            {
                cg.UniqueSemantics = "'" + string.Join("', '", unique) + "' "
                                     + (unique.Count == 1 ? "is m_unique" : "are m_unique") + ": "
                                     + cg.Def.UniqueSemantics;
            }

            // By prefab, off the goal's own expanded set, so a bare location: goal and the group: goal
            // that contains it carry the same sentence. One feature's Note is printed verbatim
            // everywhere - the CLI, the record, the web card - so the three cannot drift into three
            // different promises about the same chest.
            IReadOnlyList<WorldFeature> features = WorldFeatures.ForPrefabs(cg.Prefabs);
            if (features.Count > 0)
            {
                List<string> parts = new List<string>(features.Count);
                foreach (WorldFeature f in features) parts.Add(f.Name + ": " + f.Note);
                cg.ContentsNote = string.Join("  ", parts);
            }
        }

        /// <summary>
        /// The five closest known names, for an error the user can act on.
        ///
        /// <para>It scans <see cref="ILocationOracle.LocationNames"/> rather than
        /// <c>PrefabNames</c>, so a near miss on the name a player actually uses is offered as well as
        /// a near miss on the prefab. Scanning prefabs alone meant that someone who typed
        /// <c>"The Eldar"</c> was told "did you mean: " and nothing else, because no PREFAB contains
        /// it; now the display name is matched too and they are pointed at <c>GDKing</c>. Each hit is
        /// rendered <c>GDKing ("The Elder")</c> so the user sees both spellings and knows which one
        /// the tool will echo back.</para>
        ///
        /// <para><b>What it does NOT do</b>, because the doc here claimed otherwise until 2026-09-24:
        /// <see cref="Close"/> is a two-way substring test, not an edit distance. A typo INSIDE a word
        /// - <c>"Eldar"</c>, <c>"Elderr"</c> - is not a substring of anything and gets the generic
        /// fallback. Only a truncation or an extension matches (<c>"Elder"</c>, <c>"GDKingg"</c>).
        /// Making it tolerant of transpositions is a real improvement and a separate change; do not
        /// let this comment imply it already happened.</para>
        /// </summary>
        private static string Suggest(string wanted, ILocationOracle oracle)
        {
            List<string> near = new List<string>();
            foreach ((string prefab, string? display) in oracle.LocationNames)
            {
                if (!Close(prefab, wanted) && !(display != null && Close(display, wanted))) continue;

                // Bonemass's display name IS its prefab: printing it twice would read as two places.
                near.Add(display == null || string.Equals(display, prefab, StringComparison.Ordinal)
                    ? prefab
                    : prefab + " (\"" + display + "\")");
                if (near.Count == 5) break;
            }

            return near.Count > 0
                ? "did you mean: " + string.Join(", ", near)
                : "'vseed search --metrics' lists the groups, and 'vseed locations <seed> --type all' "
                  + "lists every prefab the dumped table holds";
        }

        private static bool Close(string known, string wanted)
            => known.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0
               || wanted.IndexOf(known, StringComparison.OrdinalIgnoreCase) >= 0;

        private static IReadOnlyList<string> ResolvePrefabs(Goal g, ILocationOracle oracle)
        {
            if (g.Target.Kind == TargetKind.Group)
            {
                IReadOnlyList<string>? prefabs = oracle.ExpandGroup(g.Target.Name)
                                                 ?? LocationGroups.Expand(g.Target.Name);
                if (prefabs == null)
                {
                    throw new QueryException("goals." + g.Id, "'" + g.Target.Name + "' is not a known group",
                        "groups: " + string.Join(", ", LocationGroups.Names));
                }

                return prefabs;
            }

            // A location target may be spelled as the prefab (GDKing) or as the name a player knows
            // (The Elder). Resolved HERE, where one target becomes one prefab, rather than in the
            // per-prefab loop below, so a group's members - which are always prefabs already - are not
            // put through a name resolver that could only ever answer with themselves.
            //
            // A null is deliberately NOT rewritten. From an unavailable oracle it means "cannot say",
            // and from an available one it means "no such name" - and in both cases leaving the raw
            // string is what makes the existing refusal (the dump is missing, or the prefab is
            // unknown and here are five near spellings) the message the user sees.
            LocationNameMatch? match = oracle.ResolveLocationName(g.Target.Name);
            return new[] { match != null ? match.Prefab : g.Target.Name };
        }
    }
}
