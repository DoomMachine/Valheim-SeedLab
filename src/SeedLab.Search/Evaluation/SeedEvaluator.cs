using System;
using System.Collections.Generic;
using System.Diagnostics;
using SeedLab.Search.Criteria;
using SeedLab.Search.Locations;
using SeedLab.Search.Metrics;
using SeedLab.WorldGen;

namespace SeedLab.Search.Evaluation
{
    /// <summary>
    /// Evaluates one seed against one compiled query. <b>One of these per worker thread</b>: it owns the
    /// grid buffers and the flood-fill scratch, so a steady-state scan allocates nothing per seed except
    /// the generator itself.
    ///
    /// <para><b>The tier ladder, and what each step is allowed to claim</b> (07-features.md section 3.1):</para>
    /// <list type="bullet">
    /// <item><b>T0</b> - <c>EXACT</c>. Runs once per query, not per seed. Rejects a goal only with a
    /// proof that covers every seed.</item>
    /// <item><b>T1</b> - <c>SOUND-BOUND, accept-only</c>. A decimated <i>subset of the query's own grid</i>,
    /// so a witness on the probe is a witness on the grid by construction. It may accept; it may never
    /// reject. (The spec's Vogel-spiral probe would <i>not</i> be sound here: its points are not grid
    /// points, so a biome found on the spiral is not evidence about any grid cell. Using grid points is
    /// the fix.)</item>
    /// <item><b>T2</b> - <c>EXACT</c>. <c>GetBiome</c> over the grid inside the query's region. Exact for
    /// every biome metric, by construction, because the metric is defined on that grid.</item>
    /// <item><b>T3</b> - <c>EXACT</c>. <c>GetBiomeHeight</c> over the same cells: land, islands, peaks.
    /// This is where a seed pays for the deferred lake/river/stream pre-generation, because heights
    /// read rivers - so it is roughly 350x the cost of T2 per seed, measured. The spec's rivers-off
    /// <c>SOUND-BOUND</c> variant is <b>not implemented</b>: it would need a second height path inside
    /// <c>SeedLab.WorldGen</c> that the port does not have. Running the exact height instead is slower,
    /// never wrong.</item>
    /// <item><b>T4</b> - <c>EXACT</c>. The river, lake and stream lists come straight out of
    /// pre-generation, so a T4 goal costs a seed exactly what a T3 goal costs it and nothing more; on
    /// a query that also measures heights it is free.</item>
    /// <item><b>T5</b> - <c>EXACT</c>. The full location placement, through
    /// <see cref="ILocationOracle"/>, over the shortest ordered prefix that makes the query's own
    /// types final. It is two orders of magnitude dearer than every other tier put together - about
    /// 2 s a seed on one thread, of which the 2048^2 biome-and-height point grid that
    /// <c>GetRandomPointByBiomes</c> draws from is most - so it runs last, only for seeds the cheap
    /// tiers have not already rejected, and it can stop part-way through the prefix the moment a
    /// must-goal's own last type has finished and failed. Without a usable dumped table the goal is
    /// refused with a reason, never silently passed.</item>
    /// </list>
    ///
    /// <para><b>Region restriction</b> (section 3.3) is <c>EXACT</c>: a metric bounded by a disc cannot be
    /// changed by a cell outside that disc. Where it leaves a <i>displayed</i> value short of the true
    /// one, the record says <c>bounded</c> and carries the radius - the verdict is never affected.</para>
    /// </summary>
    public sealed class SeedEvaluator : ISeedEvaluator
    {
        /// <summary>Stride of the T1 probe: every 16th grid cell on each axis, i.e. 1/256 of the cells.</summary>
        public const int ProbeStride = 16;

        private readonly CompiledQuery _q;
        private readonly ILocationOracle _oracle;
        private readonly SeedSampler _sampler;
        private readonly WorldMeasurement _measure;
        private readonly int _genVersion;

        private readonly LocationTypeGate? _gate;
        private readonly List<CompiledGoal> _gatedGoals = new List<CompiledGoal>();

        public SeedEvaluator(CompiledQuery q, ILocationOracle oracle)
        {
            _q = q;
            _oracle = oracle;
            _sampler = new SeedSampler(q.Grid);
            _measure = new WorldMeasurement(q.Grid);
            _genVersion = q.Query.World.GenVersion;
            _gate = BuildLocationGate();
        }

        /// <summary>Seeds a location gate stopped part-way through the placement prefix.</summary>
        public long LocationAborts { get; private set; }

        /// <summary>Seeds whose placement never started because a cheaper must-goal had already failed.</summary>
        public long LocationSkips { get; private set; }

        /// <summary>Seeds whose location placement ran at all.</summary>
        public long Placed { get; private set; }

        /// <summary>Seconds spent inside the location oracle.</summary>
        public double LocationSeconds { get; private set; }

        /// <summary>
        /// The early abort for location goals, and the proof that it is EXACT.
        ///
        /// <para>A location type's instances are appended only by the loop of its own ordered entry,
        /// and no later entry ever removes or moves one. The moment that entry's loop returns, the
        /// type's instance set for this seed is <b>final</b>. So a <c>must</c> goal every one of whose
        /// prefabs has finished is already decided; if it is false, no amount of further placement can
        /// make it true, and the seed can be rejected there. The gate therefore returns false only for
        /// a must-goal whose own last prefab has just finished - which is what the delegate's contract
        /// requires - and the <c>--no-prefilter</c> audit switches it off entirely, so the parity test
        /// compares a gated run against an ungated one over the same seeds.</para>
        ///
        /// <para>Goals measured <c>from: spawn</c> are deliberately not gated: their origin is itself a
        /// placed position, and keeping the gate to centre-relative goals keeps the proof above to one
        /// sentence. They are measured at the end, exactly, like every other goal.</para>
        /// </summary>
        private LocationTypeGate? BuildLocationGate()
        {
            if (_q.NoPrefilter) return null;

            foreach (CompiledGoal cg in _q.Goals)
            {
                if (!cg.Available || cg.Goal.Importance != Importance.Must) continue;
                if (!cg.Def.NeedsLocations || cg.Goal.From != DistanceOrigin.Center) continue;
                if (cg.LastPrefabInOrder == null) continue;
                _gatedGoals.Add(cg);
            }

            if (_gatedGoals.Count == 0) return null;

            return (prefab, hits) =>
            {
                foreach (CompiledGoal cg in _gatedGoals)
                {
                    if (!string.Equals(cg.LastPrefabInOrder, prefab, StringComparison.Ordinal)) continue;
                    if (!cg.Pass(cg.ReadLocations(hits, 0.0, 0.0))) return false;
                }

                return true;
            };
        }

        /// <summary>Seeds whose height pass was skipped because a T2 must-goal had already failed.</summary>
        public long EarlyExits { get; private set; }

        /// <summary>Seeds the T1 accept-only probe settled without the full biome pass.</summary>
        public long ProbeAccepts { get; private set; }

        public long Evaluated { get; private set; }

        /// <summary>Seconds spent constructing generators - the cost every tier pays whether it wants it or not.</summary>
        public double ConstructSeconds { get; private set; }

        public double SampleSeconds { get; private set; }

        /// <summary>
        /// Seconds spent on the deferred lake/river/stream pre-generation, i.e. the part of world
        /// construction that only the tiers which read rivers actually pay for.
        /// </summary>
        public double PregenSeconds { get; private set; }

        /// <summary>Seeds that reached a tier which needed the river data.</summary>
        public long Pregenerated { get; private set; }

        /// <summary>Runs the deferred pregeneration, timed separately from sampling.</summary>
        private void Pregenerate(WorldGeneratorPort gen)
        {
            if (!gen.PregenerationPending) return;
            long t = Stopwatch.GetTimestamp();
            gen.ForcePregeneration();
            PregenSeconds += (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency;
            Pregenerated++;
        }

        /// <summary>
        /// Evaluates one seed. <paramref name="full"/> forces every goal to be measured even after a
        /// must-goal has already failed - that is what <c>vseed explain</c> needs, and what the
        /// <c>--no-prefilter</c> audit uses.
        /// </summary>
        public SeedResult Evaluate(int seed, bool full = false)
        {
            long t0 = Stopwatch.GetTimestamp();
            SeedResult r = new SeedResult
            {
                Seed = seed,
                Pass = true,
                TierReached = Tier.T0,
                RegionRadiusM = _q.Plan.Radius,
            };

            // Nothing in the query can be answered without the generator. It is built with
            // deferPregeneration, so the lake/river/stream pre-generation - which is ~99.5 % of the cost
            // of a world and is read only by GetHeight and the river/lake/stream accessors - runs only
            // for the seeds and the tiers that actually reach it. A T2 biome-only query never pays it;
            // a T3 query that a T2 must-goal has already rejected never pays it either. The world is
            // identical either way: same four calls, same RNG state (WorldGeneratorPort.Pregenerate).
            long c0 = Stopwatch.GetTimestamp();
            WorldGeneratorPort gen = new WorldGeneratorPort(seed, _genVersion, menu: false,
                                                            deferPregeneration: true);
            ConstructSeconds += (Stopwatch.GetTimestamp() - c0) / (double)Stopwatch.Frequency;

            _measure.Reset(_q.Plan);
            bool structuresMeasured = false;
            bool biomesMeasured = false;
            bool heightsMeasured = false;

            if (_q.Plan.NeedStructures)
            {
                Pregenerate(gen);
                _measure.MeasureStructures(gen);
                structuresMeasured = true;
            }

            // ---- T1: accept-only -------------------------------------------------------------------
            bool settledByProbe = false;
            if (!full && !_q.NoPrefilter && CanProbeDecide())
            {
                long p0 = Stopwatch.GetTimestamp();
                settledByProbe = ProbeWitnessesEveryMust(gen);
                SampleSeconds += (Stopwatch.GetTimestamp() - p0) / (double)Stopwatch.Frequency;
                if (settledByProbe)
                {
                    ProbeAccepts++;
                    r.TierReached = Tier.T1;
                }
            }

            if (!settledByProbe && _q.Plan.NeedBiomes)
            {
                long s0 = Stopwatch.GetTimestamp();
                _sampler.SampleBiomes(gen, _q.Plan.Radius);
                _measure.MeasureBiomes(_sampler, _q.Plan);
                biomesMeasured = true;
                r.TierReached = Tier.T2;

                // ---- T2 gate: a failed must-goal here makes the height pass pointless ----------------
                // Counted only when there IS a height pass to skip, so "early exits" means work saved
                // rather than "must-goals that failed"; and switched off by --no-prefilter, because the
                // audit mode's documented contract is that no prefilter of any kind runs in it.
                if (!full && !_q.NoPrefilter && _q.Plan.NeedHeights
                    && AnyMustFails(biomes: true, heights: false, structures: structuresMeasured))
                {
                    EarlyExits++;
                }
                else if (_q.Plan.NeedHeights)
                {
                    // Heights are the first thing that reads rivers, so this is where a height query
                    // pays for pregeneration. Timed on its own so the cost report stays honest.
                    Pregenerate(gen);
                    _sampler.SampleHeights(gen);
                    _measure.MeasureHeights(_sampler, _q.Plan);
                    heightsMeasured = true;
                    r.TierReached = Tier.T3;
                }

                SampleSeconds += (Stopwatch.GetTimestamp() - s0) / (double)Stopwatch.Frequency;
                if (structuresMeasured) r.TierReached = Tier.T4;
            }

            // ---- T5: location placement --------------------------------------------------------------
            // The most expensive tier by two orders of magnitude, so it runs last and only when the
            // cheap tiers have not already settled the seed. The oracle runs the shortest correct
            // ordered prefix for this query's prefabs (LocationPlan.PrefixLength), and the gate above
            // can stop it part-way once a must-goal is decided.
            LocationWorld? world = null;
            bool locationsMeasured = false;
            if (_q.Locations != null)
            {
                // The most valuable early exit in the whole engine: a T2 or T3 must-goal that has
                // already failed saves the seed a ~2 s placement, not a ~5 ms measurement.
                bool settledCheaply = !full && !_q.NoPrefilter
                                      && AnyMustFails(biomesMeasured, heightsMeasured, structuresMeasured);
                if (settledCheaply)
                {
                    LocationSkips++;
                }
                else
                {
                    long l0 = Stopwatch.GetTimestamp();
                    world = _oracle.Run(_q.Locations, seed, _genVersion, full ? null : _gate);
                    LocationSeconds += (Stopwatch.GetTimestamp() - l0) / (double)Stopwatch.Frequency;
                    Placed++;
                    if (world.Aborted) LocationAborts++;
                    locationsMeasured = true;
                    r.TierReached = Tier.T5;
                    r.LocationPrefixAborted = world.Aborted ? world.AbortedAfter : null;
                }
            }

            double originX = 0.0, originZ = 0.0;
            bool haveOrigin = true;
            if (world != null && _q.Locations!.NeedSpawn)
            {
                haveOrigin = world.HasSpawn;
                originX = world.SpawnX;
                originZ = world.SpawnZ;
            }

            // ---- collect every goal's outcome -------------------------------------------------------
            double weighted = 0, totalWeight = 0;
            foreach (CompiledGoal cg in _q.Goals)
            {
                GoalOutcome o = new GoalOutcome
                {
                    Id = cg.Goal.Id,
                    Target = cg.Goal.Target.ToString(),
                    Metric = cg.Goal.Metric,
                    Test = cg.Goal.Test,
                    Importance = cg.Goal.Importance,
                    Threshold = cg.Goal.Value,
                    ThresholdMax = cg.Goal.Max,
                    Weight = cg.Goal.Weight,
                    Unit = cg.Def.Unit,
                    Tier = cg.Tier,
                    Unsatisfiable = cg.Unsatisfiable,
                    UniqueSemantics = cg.UniqueSemantics,
                    ContentsNote = cg.ContentsNote,

                    // Presentation, so a number cannot mislead whoever reads the file next month.
                    // GridIndependent metrics (location placement) carry the game's own 12 m point
                    // grid, not this query's sampling grid, and saying "decided at G192" about one of
                    // them would be false.
                    MeasuredAtGrid = cg.Def.NeedsLocations ? 12.0 : _q.Grid.Spacing,
                    GridComparable = cg.Def.GridComparable,
                    GridNote = cg.Def.GridComparable ? null : cg.Def.GridNote,
                };

                if (!cg.Def.NeedsLocations)
                {
                    double? err = MetricError.MedianRelErrorAt(cg.Def.Name, _q.Grid.Spacing, out string src);
                    if (err.HasValue && err.Value > 0)
                    {
                        o.MeasuredRelError = err.Value;
                        o.ErrorSource = src;
                    }
                }

                if (!cg.Available)
                {
                    o.Unavailable = cg.UnavailableReason;
                    o.Value = double.NaN;
                    o.Pass = false;
                    r.Goals.Add(o);
                    r.Pass = false;
                    r.FailedGoal ??= cg.Goal.Id;
                    continue;
                }

                if (settledByProbe)
                {
                    o.Bounded = true;
                    o.BoundRadius = _q.Plan.Radius;
                    o.Value = double.NaN;
                    o.Pass = true;
                    r.Goals.Add(o);
                    continue;
                }

                // A goal whose pass never ran (because an earlier must-goal failed) has no value.
                if (!Measured(cg, biomesMeasured, heightsMeasured, structuresMeasured, locationsMeasured))
                {
                    o.Value = double.NaN;
                    o.Pass = false;
                    r.Goals.Add(o);
                    continue;
                }

                double v;
                if (cg.Tier == Tier.T5)
                {
                    if (cg.Goal.From == DistanceOrigin.Spawn && !haveOrigin)
                    {
                        // StartTemple did not place. The game would put the player on the fallback
                        // spawn, which is not a function of the seed, so the goal is refused rather
                        // than measured against a guess.
                        o.Unavailable = "no StartTemple placed in this world, so 'from: spawn' has no origin";
                        o.Value = double.NaN;
                        o.Pass = false;
                        r.Goals.Add(o);
                        r.Pass = false;
                        r.FailedGoal ??= cg.Goal.Id;
                        continue;
                    }

                    // A gated run stops as soon as ONE must-goal is decided false. Every goal whose own
                    // last prefab had not finished by then is unmeasured, and says so, rather than
                    // reporting the truncated count as if it were the world's.
                    if (world!.Aborted && cg.LastOrderedIndex > world.LastOrderedIndexRun)
                    {
                        o.Value = double.NaN;
                        o.Pass = false;
                        r.Goals.Add(o);
                        continue;
                    }

                    v = cg.ReadLocations(world.Hits,
                                         cg.Goal.From == DistanceOrigin.Spawn ? originX : 0.0,
                                         cg.Goal.From == DistanceOrigin.Spawn ? originZ : 0.0);
                }
                else
                {
                    v = cg.Read(_measure);
                }

                o.Value = v;
                o.Bounded = cg.RegionLimited || (cg.MayBeCensored && !double.IsFinite(v));
                o.BoundRadius = cg.RegionLimited ? _q.Plan.Radius : cg.RegionRadius;

                // A distance at or below the grid's own resolution floor is not a measurement of the
                // world - it is the floor. It happens to 70 % of seeds on nearest_land_distance at
                // G12 and to 93 % on Meadows' nearest_distance, and a bare "8.485 m" reads as a
                // distance somebody measured. Print it as the bound it is.
                if (!cg.Def.NeedsLocations && cg.Def.Unit == Unit.Metres
                    && cg.Def.Name.Contains("distance") && double.IsFinite(v))
                {
                    double floor = MetricError.ResolutionFloor(_q.Grid.Spacing);
                    if (v <= floor + 1e-3)
                    {
                        o.Censored = "<= " + floor.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                                     + " m, one grid cell at G"
                                     + _q.Grid.Spacing.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                                     + ": the origin cell already matches, so this is the grid's floor "
                                     + "and not a measured distance";
                    }
                }
                o.Pass = cg.Pass(v);
                o.Margin = Margin(cg, v);

                if (cg.Goal.Importance == Importance.Must)
                {
                    if (!o.Pass)
                    {
                        r.Pass = false;
                        r.FailedGoal ??= cg.Goal.Id;
                    }
                }
                else
                {
                    o.Score = cg.Score(v);
                    weighted += o.Score * cg.Goal.Weight;
                    totalWeight += cg.Goal.Weight;
                }

                r.Goals.Add(o);
            }

            r.Score = totalWeight > 0 ? weighted / totalWeight : 1.0;

            if (biomesMeasured)
            {
                r.OceanShare = _measure.InWorldCells > 0 ? _measure.BiomeShare(8) : null;
            }

            if (heightsMeasured)
            {
                r.LandAreaM2 = _measure.LandArea;
                r.HighestPeak = _measure.PeakHeight;
                if (_q.Plan.NeedIslands)
                {
                    r.LargestIslandM2 = _measure.LargestIslandCells * _measure.CellArea;
                }
            }

            Evaluated++;
            r.Seconds = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
            return r;
        }

        private static double Margin(CompiledGoal cg, double v)
        {
            if (!double.IsFinite(v)) return double.NaN;
            return cg.Goal.Test switch
            {
                GoalTest.Near or GoalTest.AtMost => cg.Goal.Value - v,
                GoalTest.Far or GoalTest.AtLeast => v - cg.Goal.Value,
                GoalTest.Between => v < cg.Goal.Value ? v - cg.Goal.Value : cg.Goal.Max - v,
                _ => double.NaN,
            };
        }

        /// <summary>True when the pass this goal's metric comes from has run on this seed.</summary>
        private static bool Measured(CompiledGoal cg, bool biomes, bool heights, bool structures, bool locations)
            => cg.Tier switch
            {
                Tier.T2 => biomes,
                Tier.T3 => heights,
                Tier.T4 => structures,
                Tier.T5 => locations,
                _ => false,
            };

        /// <summary>
        /// Does any must-goal that HAS been measured already fail? Only passes that actually ran are
        /// consulted, so this never reads an accumulator the seed never filled.
        /// </summary>
        private bool AnyMustFails(bool biomes, bool heights, bool structures)
        {
            foreach (CompiledGoal cg in _q.Goals)
            {
                if (cg.Goal.Importance != Importance.Must || !cg.Available) continue;
                if (cg.Tier == Tier.T5) continue;                       // not measured yet by definition
                if (!Measured(cg, biomes, heights, structures, false)) continue;
                if (!cg.Pass(cg.Read(_measure))) return true;
            }

            return false;
        }

        // ---- T1 ---------------------------------------------------------------------------------------

        /// <summary>
        /// The probe can settle a seed only when every goal is a must-goal whose truth follows from a
        /// witness - "this biome exists", "at least X of it", "something of it within D". A nice-to-have
        /// needs its exact value for the score, and an upper-bound goal ("at most N") can never be
        /// established by finding more of something.
        /// </summary>
        private bool CanProbeDecide()
        {
            if (_q.HasNiceGoals || _q.Goals.Count == 0) return false;
            foreach (CompiledGoal cg in _q.Goals)
            {
                if (!cg.Available) return false;
                if (cg.Goal.Importance != Importance.Must) return false;
                if (cg.Def.Kind != TargetKind.Biome) return false;
                switch (cg.Def.Name)
                {
                    case "present":
                    case "area":
                    case "area_within":
                        if (cg.Goal.Test != GoalTest.AtLeast && cg.Goal.Test != GoalTest.Far) return false;
                        break;
                    case "nearest_distance":
                        if (cg.Goal.Test != GoalTest.Near && cg.Goal.Test != GoalTest.AtMost) return false;
                        break;
                    default:
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Evaluates <c>GetBiome</c> on every <see cref="ProbeStride"/>-th cell of the query's own grid
        /// and asks whether that subset already proves every must-goal.
        ///
        /// <para><b>Soundness.</b> The probe points are grid points, so: a cell of biome B found on the
        /// subset is a cell of biome B on the full grid (existence, and "nearest &lt;= D" when it lies
        /// inside D); and the number of subset cells of B is a lower bound on the number of full-grid
        /// cells of B, so <c>subsetArea &gt;= X</c> implies <c>fullArea &gt;= X</c>. Every inference is
        /// one-sided: if the probe fails to witness something, nothing at all is concluded and the full
        /// pass runs.</para>
        /// </summary>
        private bool ProbeWitnessesEveryMust(WorldGeneratorPort gen)
        {
            Span<long> cells = stackalloc long[10];
            Span<double> nearest2 = stackalloc double[10];
            for (int i = 0; i < 10; i++) nearest2[i] = double.PositiveInfinity;

            int n = _q.Grid.Size;
            int nw = _q.Plan.AreaWithin.Count;
            long[] within = nw > 0 ? new long[nw] : Array.Empty<long>();

            for (int row = ProbeStride / 2; row < n; row += ProbeStride)
            {
                if (!_sampler.RowSpan(row, _q.Plan.Radius, out int lo, out int hi)) continue;
                float wz = _q.Grid.WorldZ(row);
                int start = lo + ((ProbeStride - (lo % ProbeStride)) % ProbeStride);
                for (int col = start; col < hi; col += ProbeStride)
                {
                    float wx = _q.Grid.WorldX(col);
                    if (DUtils.Length(wx, wz) > 10500f) continue;
                    int bi = gen.GetBiome(wx, wz).ToGameIndex();
                    cells[bi]++;
                    double d2 = (double)wx * wx + (double)wz * wz;
                    if (d2 < nearest2[bi]) nearest2[bi] = d2;
                    for (int i = 0; i < nw; i++)
                    {
                        (int wb, double wr) = _q.Plan.AreaWithin[i];
                        if (d2 <= wr * wr && (wb < 0 || wb == bi)) within[i]++;
                    }
                }
            }

            double cellArea = _q.Grid.CellArea;
            foreach (CompiledGoal cg in _q.Goals)
            {
                int bi = cg.BiomeIndex;
                bool ok = cg.Def.Name switch
                {
                    // 'present' is a 0/1 INDICATOR, not a cell count: the exact reader is
                    // CompiledGoal.Read -> m.BiomeCells[bi] > 0 ? 1 : 0. Comparing the probe's raw cell
                    // count against the threshold would accept "present >= 2" whenever the probe found
                    // two cells, while the exact pass can never return more than 1 - a false positive.
                    // Witnessing one probe cell proves the full grid has one, and nothing more.
                    "present" => (cells[bi] > 0 ? 1 : 0) >= cg.Goal.Value,
                    "area" => cells[bi] * cellArea >= cg.Goal.Value,
                    "area_within" => within[cg.Accumulator] * cellArea >= cg.Goal.Value,
                    "nearest_distance" => Math.Sqrt(nearest2[bi]) <= cg.Goal.Value,
                    _ => false,
                };

                if (!ok) return false;
            }

            return true;
        }
    }
}
