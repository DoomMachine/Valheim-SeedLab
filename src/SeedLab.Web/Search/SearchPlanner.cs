using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Runtime;
using SeedLab.Runtime.Estimation;
using SeedLab.Runtime.Execution;
using SeedLab.Runtime.Hardware;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Execution;
using SeedLab.Search.Locations;
using SeedLab.Search.Output;

namespace SeedLab.Web.Search
{
    /// <summary>
    /// The page's half of "the browser and the terminal can never disagree".
    ///
    /// <para>It builds the run the same way <c>vseed search</c> does - <see cref="QueryTranslator"/>,
    /// <see cref="QueryReader"/>, <see cref="SearchSession.Create"/> - and stops one step short of
    /// touching a seed. What comes back is the whole pre-run truth: the refusals, the confirmations,
    /// the warnings, the plan block, the grid decision, the resolved output policy, the worker plan and
    /// the estimate. <b>Nothing here is a second implementation</b>: every sentence in the report is
    /// produced by <see cref="SearchPreflightCheck"/>, <see cref="GridPolicy"/>,
    /// <see cref="OutputPolicy"/>, <see cref="WorkerPlanner"/> or <see cref="Estimator"/>, which are
    /// the objects the CLI prints from.</para>
    ///
    /// <para>The same method answers <c>POST /api/search/preflight</c> (the live verdict beside the
    /// Find button) and gates <c>POST /api/search</c>. That matters: if only the page checked, a
    /// hand-written POST would skip the refusal rule and the confirmations, and the guarantee would be
    /// a convention rather than a property.</para>
    /// </summary>
    public static class SearchPlanner
    {
        public sealed class Planned
        {
            public SearchSession Session = null!;
            public QueryTranslator.Translation Translation = null!;
            public CompiledQuery Compiled = null!;
            public PreflightReport Report = null!;
            public WorkerPlan Workers = null!;

            /// <summary>Which of the two shapes this run takes, after <c>auto</c> has been resolved.</summary>
            public SearchStrategy Strategy;

            /// <summary>The two-stage plan, whether or not it is used - the page shows the reason either way.</summary>
            public FunnelPlan Funnel = null!;

            /// <summary>The engine version this plan was built with, so a second stage uses the same one.</summary>
            public string EngineVersion = "";

            /// <summary>The absolute results path the server would write, or null.</summary>
            public string? OutPath;
        }

        public static Planned Plan(SearchQuery q, ILocationOracle oracle, RuntimeContext runtime,
                                   string engineVersion, string? resultsDirectory)
        {
            if (q == null) throw new ArgumentNullException(nameof(q));

            string? outPath = null;
            string? outName = QueryTranslator.Clean(q.OutName);
            if (outName != null) outPath = QueryTranslator.ResolveOutPath(outName, resultsDirectory ?? "");

            // The goal id is a CSV column header, so it must name the prefab the run will actually
            // use rather than whatever the user typed. SearchSession canonicalises the TARGET later
            // and independently; this only keeps the label in step with it, and it is best-effort -
            // an oracle with no dumped table returns null and the typed string stands.
            QueryTranslator.Translation t = QueryTranslator.Translate(q, outPath, CanonicaliseForId(oracle));
            Query parsed = t.Query;

            // The mode the run would actually use: what the user picked, unless the auto-throttle has
            // something to say about it. Asking the throttle here - not at server start - is what makes
            // the estimate honest when the user launched the game after opening the page.
            ResourceModes.TryParse(q.Mode, out ResourceMode asked, out string modeError);
            ResourceMode effective = asked;
            bool throttled = false;
            string throttledBy = "";
            if (!q.IgnoreRunningGame && runtime.Throttle.IsThrottled)
            {
                effective = ResourceMode.Background;
                throttled = true;
                throttledBy = runtime.Throttle.ThrottledBy ?? "a running game";
            }

            CompiledQuery probe;
            try
            {
                probe = CompiledQuery.Compile(parsed, oracle);
            }
            catch (QueryException ex)
            {
                throw new ArgumentException(ex.Message + (string.IsNullOrEmpty(ex.Hint) ? "" : " - " + ex.Hint));
            }

            long gridCells = WorkerFootprints.CellsForSpacing(parsed.Search.Grid);
            WorkTier tier = TierOf(probe, oracle);
            WorkerPlan workers = WorkerPlanner.Plan(effective, WorkerFootprints.For(tier, gridCells),
                runtime.Hardware,
                new WorkerPlanOptions { RequestedWorkers = q.Threads > 0 ? q.Threads : (int?)null });

            SearchSession session;
            try
            {
                session = SearchSession.Create(parsed, oracle, engineVersion, parsed.Search.Seeds,
                                               workers.Workers, outPath, false, q.AcceptScanOrder);
            }
            catch (QueryException ex)
            {
                throw new ArgumentException(ex.Message + (string.IsNullOrEmpty(ex.Hint) ? "" : " - " + ex.Hint));
            }

            // The checkpoint lives in the runtime cache root, never in whatever directory the server
            // happened to be started from. Defect 5 of the audit, and the reason it is passed rather
            // than left to a default: a default is a thing that can quietly change back.
            session.CheckpointPath = System.IO.Path.Combine(runtime.Cache.Checkpoints,
                Safe16(session.QueryHash) + ".ckpt");

            PreflightReport r = Describe(q, parsed, session, probe, workers, runtime, t, outPath,
                                         asked, effective, throttled, throttledBy, modeError, tier, gridCells);

            // The strategy is decided HERE, on the server, by the same FunnelPlan the terminal uses -
            // so a hand-written POST cannot get a different answer than the page, and the page cannot
            // get a different answer than the terminal.
            FunnelPlan funnel = FunnelPlan.For(parsed, session.Compiled);
            SearchStrategy askedStrategy = ParseStrategy(q.Strategy);
            SearchStrategy chosen;
            string why;
            if (askedStrategy == SearchStrategy.Auto)
            {
                chosen = FunnelPlan.Decide(funnel, out why);
            }
            else if (askedStrategy == SearchStrategy.Funnel && !funnel.Usable)
            {
                chosen = SearchStrategy.Sample;
                why = "sample: funnel was asked for, but " + funnel.Reason;
            }
            else
            {
                chosen = askedStrategy;
                why = (chosen == SearchStrategy.Funnel ? "funnel: " : "sample: ") + funnel.Reason;
            }

            r.Strategy = chosen.ToString().ToLowerInvariant();
            r.StrategyAsked = askedStrategy.ToString().ToLowerInvariant();
            r.StrategyWhy = why;
            r.StrategyStageOne = funnel.StageOneGoals.ToArray();
            r.StrategyDeferred = funnel.DeferredGoals.ToArray();

            return new Planned
            {
                Session = session,
                Translation = t,
                Compiled = session.Compiled,
                Report = r,
                Workers = workers,
                OutPath = outPath,
                Strategy = chosen,
                Funnel = funnel,
                EngineVersion = engineVersion,
            };
        }

        private static SearchStrategy ParseStrategy(string? v)
        {
            if (string.IsNullOrEmpty(v)) return SearchStrategy.Auto;
            switch (v!.Trim().ToLowerInvariant())
            {
                case "funnel": return SearchStrategy.Funnel;
                case "sample": return SearchStrategy.Sample;
                case "auto": return SearchStrategy.Auto;
                default:
                    throw new ArgumentException(
                        "strategy '" + v + "' is not one of auto, funnel, sample.");
            }
        }

        private static string Safe16(string hash)
            => hash.Length >= 16 ? hash.Substring(0, 16) : (hash.Length > 0 ? hash : "query");

        // -----------------------------------------------------------------------------------------
        private static PreflightReport Describe(SearchQuery q, Query parsed, SearchSession s, CompiledQuery cq,
                                                WorkerPlan workers, RuntimeContext runtime,
                                                QueryTranslator.Translation t, string? outPath,
                                                ResourceMode asked, ResourceMode effective, bool throttled,
                                                string throttledBy, string modeError, WorkTier tier, long gridCells)
        {
            PreflightReport r = new PreflightReport
            {
                QueryJson = t.QueryJson,
                QueryHash = s.QueryHash,
                TranslationNotes = t.Notes,
                Refusals = s.Preflight.Refusals,
                Confirmations = s.Preflight.Confirmations,
                Warnings = s.Preflight.Warnings,
                Plan = s.Preflight.Plan,
                Ok = s.Preflight.Ok,
                Seeds = s.Plan.Limit,
                FractionOfSpace = s.Plan.FractionOfSpace,
                BlockSize = s.Plan.BlockSize,
                Blocks = s.Plan.Blocks,
                Key = "0x" + s.Plan.Key.ToString("X16", CultureInfo.InvariantCulture),
                Order = s.Plan.Order.ToString().ToLowerInvariant(),
                MaxTier = cq.MaxTier.ToString(),
                RegionRadiusM = cq.Plan.Radius,
                CheckpointPath = s.CheckpointPath,
                OutPath = outPath,
                ProjectedBytesIfAllMatch = s.Preflight.ProjectedBytesIfAllMatch,
                FreeBytes = s.Preflight.FreeBytes,
            };

            r.Grid = new GridReport
            {
                VerifyGrid = s.Grid.VerifyGrid,
                ScreenGrid = s.Grid.ScreenGrid,
                Margin = s.Grid.Margin,
                ScreenThenVerify = s.Grid.ScreenThenVerify,
                RaisedFrom = s.Grid.RaisedFrom,
                Describe = s.Grid.Describe(),
                Notes = s.Grid.Notes,
                FineOnlyGoals = s.Grid.FineOnlyGoals,
                UnsafeMusts = s.Grid.UnsafeMusts,
            };

            r.Output = new OutputReport
            {
                Path = outPath,
                Name = QueryTranslator.Clean(q.OutName),
                Format = s.Output.Format.ToString().ToLowerInvariant(),
                KeepAll = s.Output.KeepAll,
                Keep = s.Output.Keep,
                RotateBytes = s.Output.RotateBytes,
                Compress = s.Output.Compress,
                Reduce = s.Output.Reduce.Text,
                MaxBytes = s.Output.MaxBytes,
                OnLimit = s.Output.OnLimit.ToString().ToLowerInvariant(),
                IsRotating = s.Output.IsRotating,
                Warning = s.Output.Warning,
                BytesPerRecord = RecordFormatter.EstimatedBytesPerRecord(s.Output.Format, cq.Goals.Count),
            };

            r.Runtime = new RuntimeReport
            {
                Mode = ResourceModes.Name(effective),
                ModeAsked = ResourceModes.Name(asked),
                ModeDescription = ResourceModes.Describe(effective),
                ModeError = string.IsNullOrEmpty(modeError) ? null : modeError,
                Throttled = throttled,
                ThrottledBy = throttled ? throttledBy : null,
                Workers = workers.Workers,
                ModeWorkers = workers.ModeWorkers,
                MemoryWorkers = workers.MemoryWorkers,
                MemoryLimited = workers.MemoryLimited,
                InsufficientMemory = workers.InsufficientMemory,
                Lines = new List<string>(workers.Lines()),
                LogicalCores = runtime.Hardware.LogicalCores,
                PhysicalCores = runtime.Hardware.PhysicalCores ?? runtime.Hardware.LogicalCores,
                AvailableMemoryBytes = runtime.Hardware.AvailableMemoryBytes,
                TotalMemoryBytes = runtime.Hardware.TotalMemoryBytes,
                CacheRoot = runtime.Cache.Path,
                SelfTest = runtime.SelfTestOutcome?.Message,
            };

            // The estimate. Its inputs are the machine's half from the runtime context and the query's
            // half from what was just compiled - the same two halves the CLI feeds it.
            EstimateInputs inputs = new EstimateInputs
            {
                Seeds = s.Plan.Limit,
                Tier = tier,
                GridCells = gridCells,
                Format = FormatOf(s.Output),
                GoalCount = cq.Goals.Count,
                KeepN = s.Output.KeepAll ? (long?)null : s.Output.Keep,
                AllSeedsFlag = s.Plan.Limit >= EstimateInputs.WholeSeedSpace,
            };

            Estimate est = runtime.Estimator.Project(runtime.Inputs(inputs, workers, outPath ?? ""));
            r.Estimate = new EstimateReport
            {
                SecondsLow = est.Time.Low,
                SecondsHigh = est.Time.High,
                MemoryLowBytes = est.Memory.Low,
                MemoryHighBytes = est.Memory.High,
                DiskLowBytes = est.Disk.Low,
                DiskHighBytes = est.Disk.High,
                SeedsPerSecond = est.SeedsPerSecond,
                Calibrated = est.Calibrated,
                Verdict = est.Verdict.ToString().ToLowerInvariant(),
                Reasons = new List<string>(est.Reasons),
                Notes = new List<string>(est.Notes),
            };

            // Per-goal cost, from the measured cost model rather than a table of adjectives.
            double worst = 0;
            foreach (CompiledGoal g in cq.Goals)
            {
                GoalCostReport c = CostOf(g, parsed.Search.Grid, cq);
                if (c.MillisecondsPerSeed > worst) worst = c.MillisecondsPerSeed;
                r.Goals.Add(c);
            }

            r.WorstGoalMillisecondsPerSeed = worst;
            r.CostHeadline = Headline(cq, worst, r.Goals);
            r.GridOptions = Ladder(cq, s, workers.Workers, tier);
            return r;
        }

        /// <summary>
        /// Decision 6's sampling ladder with the cost of each rung ON THIS QUERY - not a table of
        /// relative factors copied out of the study, but <see cref="CostModel"/> evaluated at this
        /// query's tier and region, so the number beside "G96" is the number this run would pay.
        ///
        /// <para>Each rung also says whether it is SAFE for the must-have goals present. That is the
        /// resolution study's finding encoded rather than repeated: a counting metric survives a coarse
        /// grid with a margin, a connectivity or extremum metric does not survive one at all, and past
        /// G96 the margin needed to lose nothing passes 90 % of seeds - at which point the screen has
        /// stopped filtering.</para>
        /// </summary>
        private static List<GridOption> Ladder(CompiledQuery cq, SearchSession s, int workers, WorkTier tier)
        {
            double[] rungs = { 384, 256, 192, 96, 48, 24, 12 };
            bool fineOnly = s.Grid.FineOnlyGoals.Count > 0;

            // Whether the sampling grid decides anything here at all. Location placement runs on the
            // game's own hard-coded 2048 x 2048 @ 12 m point grid (GridSafety.GridIndependent), so a
            // query whose must-haves are all location goals is EXACT at every rung and the ladder is
            // flat - and calling G384 "not safe" for it would be a warning about nothing.
            bool gridDecides = false;
            foreach (CompiledGoal g in cq.Goals)
            {
                if (g.Goal.Importance == Importance.Must && g.Available && g.Tier <= Tier.T4) gridDecides = true;
            }
            double region = cq.Plan.Radius;
            double share = region > 0 && region < SeedLab.Search.Metrics.SeedSampler.WaterEdge
                ? Math.Min(1.0, region * region / (SeedLab.Search.Metrics.SeedSampler.WaterEdge * SeedLab.Search.Metrics.SeedSampler.WaterEdge))
                : 1.0;

            List<GridOption> list = new List<GridOption>();
            double baseline = 0;
            foreach (double g in rungs)
            {
                long cells = (long)Math.Max(1, WorkerFootprints.CellsForSpacing(g) * share);
                (double seconds, bool extrapolated, string how) = CostModel.SingleThreadSeconds(tier, cells);
                if (baseline <= 0) baseline = seconds;

                string? why = null;
                bool safe = true;
                if (!gridDecides)
                {
                    why = "no must-have goal here is measured on the sampling grid: location placement "
                          + "uses the game's own hard-coded 2048 x 2048 @ 12 m point grid, so this query "
                          + "is exact at every rung and costs the same at all of them";
                }
                else if (fineOnly && g > 12.0)
                {
                    safe = false;
                    why = "a goal in this query is measured by connectivity or an extremum, and a coarse grid "
                          + "does not approximate those - auto-pick raises the whole query to G12 for it";
                }
                else if (g > GridPolicy.CoarsestScreen)
                {
                    safe = false;
                    why = "coarser than G96, where the margin needed to lose no true match passes 90 % of all "
                          + "seeds: a must-have here is no longer a filter";
                }

                list.Add(new GridOption
                {
                    Grid = g,
                    Cells = WorkerFootprints.CellsForSpacing(g),
                    SampledCells = cells,
                    MillisecondsPerSeed = seconds * 1000.0,
                    SeedsPerSecond = seconds > 0 ? workers / seconds : 0,
                    RelativeToCoarsest = baseline > 0 ? seconds / baseline : 1,
                    WholeSpaceSeconds = seconds > 0 ? seconds * 4294967296.0 / Math.Max(1, workers) : double.NaN,
                    SafeForMusts = safe,
                    GridDecides = gridDecides,
                    Why = why,
                    Extrapolated = extrapolated,
                    Chosen = Math.Abs(g - s.Grid.VerifyGrid) < 1e-9,
                    IsGameGrid = Math.Abs(g - 12.0) < 1e-9,
                });
            }

            return list;
        }

        private static OutputFormat FormatOf(OutputPolicy o)
        {
            if (o.Path == null) return OutputFormat.None;
            if (o.Format == ResultFormat.Csv) return OutputFormat.Csv;
            if (o.Format == ResultFormat.Json) return OutputFormat.Json;
            return o.IsRotating && o.Compress ? OutputFormat.JsonlGz : OutputFormat.Jsonl;
        }

        /// <summary>
        /// The work tier a query's most expensive goal puts it in. The engine evaluates every seed to
        /// <c>max(tier)</c>, so this - not the sum of the goals - is what a run costs.
        /// </summary>
        /// <summary>
        /// Resolves a goal target for the purpose of NAMING it - <c>location:The Elder</c> to
        /// <c>location:GDKing</c> - so the generated goal id, which becomes a CSV column header, says
        /// what the run is actually about.
        ///
        /// <para>It deliberately mirrors <c>SearchSession.CanonicaliseLocationTargets</c> rather than
        /// replacing it: that one rewrites the target the run compiles and hashes, this one only
        /// labels it, and they must agree. Only a <c>location:</c> target is touched, and only when the
        /// oracle can answer - anything else is returned exactly as typed, because a label is never
        /// worth failing a search over.</para>
        /// </summary>
        private static Func<string, string>? CanonicaliseForId(ILocationOracle oracle)
        {
            if (oracle == null || !oracle.Available) return null;
            return target =>
            {
                if (target == null) return "";
                const string prefix = "location:";
                if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return target;

                string typed = target.Substring(prefix.Length).Trim();
                if (typed.Length == 0) return target;

                LocationNameMatch? match = oracle.ResolveLocationName(typed);
                return match == null ? target : prefix + match.Prefab;
            };
        }

        public static WorkTier TierOf(CompiledQuery cq, ILocationOracle oracle)
        {
            if (cq.MaxTier < Tier.T3) return WorkTier.BiomeGrid;
            if (cq.MaxTier < Tier.T5) return WorkTier.HeightsRivers;

            // T5: the whole 183-entry list, or the ordered prefix this query actually needs.
            try
            {
                double fraction = cq.Locations != null ? cq.Locations.PrefixFraction : 1.0;
                return fraction > 0.5 ? WorkTier.LocationsAll : WorkTier.LocationsCore;
            }
            catch (Exception)
            {
                return WorkTier.LocationsCore;
            }
        }

        /// <summary>
        /// One goal's cost class, from <see cref="CostModel"/> at the grid and region the goal is
        /// actually measured on. The boundaries are §4.1 of the goal model: instant (no seed touched),
        /// cheap &lt; 10 ms, moderate 10-100 ms, expensive 100 ms - 2 s, very expensive &gt; 2 s.
        /// </summary>
        public static GoalCostReport CostOf(CompiledGoal g, double grid, CompiledQuery cq)
        {
            GoalCostReport c = new GoalCostReport
            {
                Id = g.Goal.Id,
                Target = g.Goal.Target.ToString(),
                Metric = g.Goal.Metric,
                Tier = g.Tier.ToString(),
                Importance = g.Goal.Importance == Importance.Must ? "must" : "nice",
                RegionRadiusM = g.RegionRadius,
                Help = g.Def.Help,
                Available = g.Available,
                Unavailable = g.UnavailableReason,
                Unsatisfiable = g.Unsatisfiable,
            };

            if (g.Tier <= Tier.T0)
            {
                c.Class = "instant";
                c.MillisecondsPerSeed = 0;
                c.Why = "decided by static analysis of the query: no seed is touched at all.";
                return c;
            }

            long cells = WorkerFootprints.CellsForSpacing(grid);

            // A disc-bounded goal samples only the disc, and that is exact - a metric bounded by a disc
            // cannot be changed by a cell outside it. The cost falls with the AREA.
            double radius = g.RegionRadius > 0 && g.RegionRadius < SeedLab.Search.Metrics.SeedSampler.WaterEdge
                ? g.RegionRadius
                : SeedLab.Search.Metrics.SeedSampler.WaterEdge;
            double share = radius * radius / (SeedLab.Search.Metrics.SeedSampler.WaterEdge * SeedLab.Search.Metrics.SeedSampler.WaterEdge);
            long goalCells = (long)Math.Max(1, cells * Math.Min(1.0, share));

            WorkTier wt = g.Tier >= Tier.T5
                ? (cq.Locations != null && cq.Locations.PrefixFraction > 0.5 ? WorkTier.LocationsAll : WorkTier.LocationsCore)
                : g.Tier >= Tier.T3 ? WorkTier.HeightsRivers : WorkTier.BiomeGrid;

            (double seconds, bool extrapolated, string how) = CostModel.SingleThreadSeconds(wt, goalCells);
            c.MillisecondsPerSeed = seconds * 1000.0;
            c.Class = Classify(c.MillisecondsPerSeed);
            c.Extrapolated = extrapolated;
            c.Why = how;

            // A location goal is 'very expensive' whatever the arithmetic says. The cost model's
            // LocationsCore constant is 1.0 s/seed/thread - a FLOOR for the shortest ordered prefix -
            // which lands a single boss goal just under the 2 s boundary, while the measured group
            // figures behind the class table are 3,372-4,250 ms for bosses_classic and
            // 16,237-25,923 ms for tar_pits_with_alt. Rounding a boss goal down to 'expensive' on the
            // floor constant would be the one place this badge could mislead.
            if (g.Tier >= Tier.T5)
            {
                c.Class = "very expensive";
                c.Why = how + "; every location goal is in this class - the constant is the FLOOR for the "
                        + "shortest ordered prefix, and the measured whole-group figures are 3.4-4.3 s "
                        + "(bosses) to 16-26 s (tar pits with their alt biome)";
            }

            return c;
        }

        public static string Classify(double msPerSeed)
        {
            if (msPerSeed <= 0) return "instant";
            if (msPerSeed < 10) return "cheap";
            if (msPerSeed < 100) return "moderate";
            if (msPerSeed < 2000) return "expensive";
            return "very expensive";
        }

        /// <summary>
        /// The sentence decision 10 asks for above all the badges: that the query costs its most
        /// expensive goal on EVERY seed, and - when that goal is a location goal - the number.
        /// </summary>
        private static string Headline(CompiledQuery cq, double worstMs, List<GoalCostReport> goals)
        {
            string worstId = "";
            foreach (GoalCostReport g in goals)
            {
                if (Math.Abs(g.MillisecondsPerSeed - worstMs) < 1e-9) { worstId = g.Id; break; }
            }

            string perSeed = worstMs >= 1000
                ? (worstMs / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " s"
                : worstMs.ToString("0.##", CultureInfo.InvariantCulture) + " ms";

            if (cq.MaxTier >= Tier.T5)
            {
                return "ONE location goal ('" + worstId + "') drags the whole query into the seconds-per-seed "
                       + "tier: every seed now pays " + perSeed + " of one thread, because the engine "
                       + "evaluates each seed to the most expensive tier any goal needs. The cheap biome "
                       + "goals beside it are not what this run costs - this one is.";
            }

            if (cq.MaxTier >= Tier.T3)
            {
                return "'" + worstId + "' needs terrain heights, so every seed pays the river and lake "
                       + "pre-generation: " + perSeed + " of one thread per seed, whatever the cheaper "
                       + "goals beside it cost. A query costs its most expensive goal, not the sum.";
            }

            return "the whole query costs " + perSeed + " of one thread per seed - its most expensive goal, "
                   + "paid on every seed. Nothing here needs heights, rivers or location placement.";
        }
    }

    // =============================================================================================
    // The wire shapes. Flat on purpose: the page renders them, the self-test asserts on them, and a
    // shell can curl them and compare with what 'vseed search --dry-run' printed.
    // =============================================================================================

    public sealed class PreflightReport
    {
        public bool Ok { get; set; }

        public List<string> Refusals { get; set; } = new List<string>();

        public List<string> Confirmations { get; set; } = new List<string>();

        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>The plan block, the same lines and the same order the terminal prints.</summary>
        public List<string> Plan { get; set; } = new List<string>();

        /// <summary>What the translator did that the editor does not show, e.g. an added preference goal.</summary>
        public List<string> TranslationNotes { get; set; } = new List<string>();

        public string QueryJson { get; set; } = "";

        public string QueryHash { get; set; } = "";

        public long Seeds { get; set; }

        public double FractionOfSpace { get; set; }

        public int BlockSize { get; set; }

        public long Blocks { get; set; }

        public string Key { get; set; } = "";

        public string Order { get; set; } = "";

        /// <summary>The strategy this run will use, after <c>auto</c> has been resolved.</summary>
        public string Strategy { get; set; } = "sample";

        /// <summary>What the request asked for, so the page can show "(auto)" honestly.</summary>
        public string StrategyAsked { get; set; } = "auto";

        /// <summary>Why that strategy, in one sentence - shown whichever way it went.</summary>
        public string StrategyWhy { get; set; } = "";

        /// <summary>Goal ids a funnel's first stage would measure.</summary>
        public string[] StrategyStageOne { get; set; } = Array.Empty<string>();

        /// <summary>Goal ids only the second stage can measure.</summary>
        public string[] StrategyDeferred { get; set; } = Array.Empty<string>();

        public string MaxTier { get; set; } = "";

        public double RegionRadiusM { get; set; }

        public string CheckpointPath { get; set; } = "";

        public string? OutPath { get; set; }

        public long ProjectedBytesIfAllMatch { get; set; }

        public long FreeBytes { get; set; }

        public GridReport Grid { get; set; } = new GridReport();

        public OutputReport Output { get; set; } = new OutputReport();

        public RuntimeReport Runtime { get; set; } = new RuntimeReport();

        public EstimateReport Estimate { get; set; } = new EstimateReport();

        public List<GoalCostReport> Goals { get; set; } = new List<GoalCostReport>();

        public double WorstGoalMillisecondsPerSeed { get; set; }

        public string CostHeadline { get; set; } = "";

        /// <summary>The sampling ladder, with this query's measured cost at every rung.</summary>
        public List<GridOption> GridOptions { get; set; } = new List<GridOption>();
    }

    /// <summary>One rung of the sampling ladder, costed for the query in hand.</summary>
    public sealed class GridOption
    {
        public double Grid { get; set; }
        public long Cells { get; set; }

        /// <summary>Cells actually sampled once the region is applied - what the cost is taken from.</summary>
        public long SampledCells { get; set; }

        public double MillisecondsPerSeed { get; set; }
        public double SeedsPerSecond { get; set; }
        public double RelativeToCoarsest { get; set; }
        public double WholeSpaceSeconds { get; set; }
        public bool SafeForMusts { get; set; }

        /// <summary>False when no must-have goal is measured on the sampling grid at all.</summary>
        public bool GridDecides { get; set; }
        public string? Why { get; set; }
        public bool Extrapolated { get; set; }
        public bool Chosen { get; set; }
        public bool IsGameGrid { get; set; }
    }

    public sealed class GridReport
    {
        public double VerifyGrid { get; set; }
        public double ScreenGrid { get; set; }
        public double Margin { get; set; }
        public bool ScreenThenVerify { get; set; }
        public double RaisedFrom { get; set; }
        public string Describe { get; set; } = "";
        public List<string> Notes { get; set; } = new List<string>();
        public List<string> FineOnlyGoals { get; set; } = new List<string>();
        public List<string> UnsafeMusts { get; set; } = new List<string>();
    }

    public sealed class OutputReport
    {
        public string? Path { get; set; }
        public string? Name { get; set; }
        public string Format { get; set; } = "";
        public bool KeepAll { get; set; }
        public int Keep { get; set; }
        public long RotateBytes { get; set; }
        public bool Compress { get; set; }
        public string Reduce { get; set; } = "none";
        public long MaxBytes { get; set; }
        public string OnLimit { get; set; } = "stop";
        public bool IsRotating { get; set; }
        public string? Warning { get; set; }
        public double BytesPerRecord { get; set; }
    }

    public sealed class RuntimeReport
    {
        public string Mode { get; set; } = "";
        public string ModeAsked { get; set; } = "";
        public string ModeDescription { get; set; } = "";
        public string? ModeError { get; set; }
        public bool Throttled { get; set; }
        public string? ThrottledBy { get; set; }
        public int Workers { get; set; }
        public int ModeWorkers { get; set; }
        public int MemoryWorkers { get; set; }
        public bool MemoryLimited { get; set; }
        public bool InsufficientMemory { get; set; }
        public List<string> Lines { get; set; } = new List<string>();
        public int LogicalCores { get; set; }
        public int PhysicalCores { get; set; }
        public long AvailableMemoryBytes { get; set; }
        public long TotalMemoryBytes { get; set; }
        public string CacheRoot { get; set; } = "";
        public string? SelfTest { get; set; }
    }

    public sealed class EstimateReport
    {
        public double SecondsLow { get; set; }
        public double SecondsHigh { get; set; }
        public double MemoryLowBytes { get; set; }
        public double MemoryHighBytes { get; set; }
        public double DiskLowBytes { get; set; }
        public double DiskHighBytes { get; set; }
        public double SeedsPerSecond { get; set; }
        public bool Calibrated { get; set; }
        public string Verdict { get; set; } = "";
        public List<string> Reasons { get; set; } = new List<string>();
        public List<string> Notes { get; set; } = new List<string>();
    }

    public sealed class GoalCostReport
    {
        public string Id { get; set; } = "";
        public string Target { get; set; } = "";
        public string Metric { get; set; } = "";
        public string Tier { get; set; } = "";
        public string Importance { get; set; } = "";
        public string Class { get; set; } = "";
        public double MillisecondsPerSeed { get; set; }
        public double RegionRadiusM { get; set; }
        public bool Extrapolated { get; set; }
        public string Why { get; set; } = "";
        public string Help { get; set; } = "";
        public bool Available { get; set; }
        public string? Unavailable { get; set; }
        public string? Unsatisfiable { get; set; }
    }
}
