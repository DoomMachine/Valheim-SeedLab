using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using SeedLab.Runtime;
using SeedLab.Runtime.Execution;
using SeedLab.Runtime.Hardware;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Execution;
using SeedLab.Search.Locations;
using SeedLab.Search.Output;
using SeedLab.Seeds;

namespace SeedLab.Web.Search
{
    /// <summary>
    /// The real engine, behind the page's seam: <c>SeedLab.Search</c> itself.
    ///
    /// <para><b>The same run the terminal makes.</b> The page's goals become a query file
    /// (<see cref="QueryTranslator"/>), the file is read by <see cref="QueryReader"/>, compiled by
    /// <see cref="CompiledQuery"/> and scanned by <see cref="SearchRun"/> - the four steps
    /// <c>vseed search</c> takes, in the same order, with the same defaults. The permutation key is
    /// derived from the same query hash, so the page and the terminal walk the seed space in the same
    /// order and return the same seeds for the same query and range.</para>
    ///
    /// <para><b>Streamed, not collected.</b> <c>SearchRun</c> hands every passing seed to its collector
    /// thread in block order; that is where the results file is written, and
    /// <see cref="SearchRun.OnResult"/> is the same emit point. This engine subscribes there, so a hit
    /// found in the first second is on screen in the first second and a run that is cancelled after an
    /// hour keeps everything it found.</para>
    ///
    /// <para><b>The whole safety layer, not a copy of it.</b> The run is built by
    /// <see cref="SearchSession"/> - the bounded sink, the grid decision, the screen-then-verify pass,
    /// the checkpoint, the torn-file repair and the resume - and gated by
    /// <see cref="SearchPreflightCheck"/>, so <c>keep</c> is a real cap on the file here exactly as it
    /// is in the terminal, the refusal rule fires here exactly as it does there, and a run started from
    /// a browser lands its checkpoint in the runtime cache root rather than in whatever directory the
    /// server was launched from.</para>
    ///
    /// <para><b>It can now write a results file</b>, because decision 12 says every flag must be
    /// reachable from the page and rotation, compression, the ceiling and <c>on_limit</c> are flags
    /// about a file. What is still enforced is WHERE: the browser names a bare file name and the server
    /// resolves it inside one results directory it owns (<see cref="QueryTranslator.Clean"/>). With no
    /// name, nothing reaches the disk but the checkpoint - which is tool state in the cache root, not
    /// the user's data.</para>
    ///
    /// <para><b>It refuses rather than guesses.</b> A goal this build cannot measure - anything that
    /// needs the dumped <c>ZoneLocation</c> table, or <c>from: spawn</c> - stops the run with the
    /// reason, exactly as the terminal does. An empty answer would be seeds that were never tested
    /// against the goal the user actually asked for.</para>
    /// </summary>
    public sealed class EngineSearchEngine : ISeedSearchEngine
    {
        private readonly ILocationOracle _oracle;
        private readonly string _engineVersion;
        private readonly RuntimeContext _runtime;
        private readonly string? _resultsDirectory;
        private readonly int _threadOverride;
        private double? _measured;

        public EngineSearchEngine(ILocationOracle oracle, string engineVersion, RuntimeContext runtime,
                                  string? resultsDirectory = null, int defaultThreads = 0)
        {
            _oracle = oracle ?? UnavailableLocationOracle.Instance;
            _engineVersion = engineVersion ?? "";
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _resultsDirectory = resultsDirectory;
            _threadOverride = defaultThreads;
        }

        /// <summary>
        /// The worker count a query that asks for 0 threads would get RIGHT NOW.
        ///
        /// <para>It used to be <c>ProcessorCount - 2</c>: a guess that took no account of the mode, of
        /// how much memory is free, or of whether the user is playing the game. It is now the runtime's
        /// <see cref="WorkerPlan"/> - balanced mode's half of the logical cores, capped by half of
        /// available RAM at this tier and grid, dropped to background while Valheim is running - which
        /// is the identical rule the CLI applies. Recomputed on every call, because the auto-throttle
        /// can change the answer between two searches.</para>
        /// </summary>
        public int DefaultThreads => PlanFor(WorkTier.BiomeGrid, WorkerFootprints.CellsForSpacing(96)).Workers;

        /// <summary>
        /// The plan for a tier and grid at the mode in force right now.
        ///
        /// <para><c>WebServerOptions.SearchThreads</c> is applied as a <b>ceiling</b>, not as a fixed
        /// count. A host that passes it (<c>vseed serve</c> passes its own plan's worker count) is
        /// saying "do not take more of this machine than this" - it is not saying "run at exactly this
        /// many however the mode changes". Pinning it would make the Engine card report a fixed 8 while
        /// the auto-throttle had already dropped the run to 4, which is precisely the kind of
        /// disagreement this whole pass exists to remove.</para>
        /// </summary>
        public WorkerPlan PlanFor(WorkTier tier, long gridCells)
            => WorkerPlanner.Plan(_runtime.EffectiveMode, WorkerFootprints.For(tier, gridCells), _runtime.Hardware,
                new WorkerPlanOptions { MaxWorkers = _threadOverride > 0 ? _threadOverride : 1024 });

        /// <summary>The bounds every goal control limits itself with. Built once; it is seed-independent.</summary>
        public BoundsCatalog Bounds(int genVersion) => _bounds ??= GoalBounds.Build(_oracle, genVersion);

        private BoundsCatalog? _bounds;

        /// <summary>
        /// Results files a live run owns. A browser makes it easy to press Run twice, and two bounded
        /// sinks on one path would each rewrite the file from their own heap - so the second start is
        /// refused with the name of the run that has it.
        /// </summary>
        private static readonly Dictionary<string, string> InUse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static bool ClaimOutput(string path, string runId)
        {
            lock (InUse)
            {
                if (InUse.ContainsKey(path)) return false;
                InUse[path] = runId;
                return true;
            }
        }

        internal static void ReleaseOutput(string? path)
        {
            if (path == null) return;
            lock (InUse) InUse.Remove(path);
        }

        internal static string? OwnerOf(string path)
        {
            lock (InUse) return InUse.TryGetValue(path, out string? id) ? id : null;
        }

        /// <summary>
        /// The pre-run truth for a query, without touching a seed: the refusals, the confirmations, the
        /// warnings, the plan block, the grid, the resolved output policy, the workers and the estimate.
        /// </summary>
        public SearchPlanner.Planned Preflight(SearchQuery query)
        {
            // skip_unavailable: drop the nice-to-have goals this build cannot measure, before anything
            // is planned. It is done on the PAGE's goals rather than on the compiled query because the
            // session, the plan, the query hash and the exported file all have to describe the run that
            // is actually made - re-compiling a mutated Query behind the session's back would leave the
            // preflight, the checkpoint key and the export describing a query nobody ran. It is done
            // HERE rather than in Start so that the live verdict shows the same query the run will use.
            // A MUST goal it cannot measure still refuses, whatever this flag says.
            if (query.SkipUnavailable && !_oracle.Available) query = WithoutUnmeasurableNiceGoals(query);
            return SearchPlanner.Plan(query, _oracle, _runtime, _engineVersion, _resultsDirectory);
        }

        public SearchEngineInfo Describe()
        {
            SearchEngineInfo info = new SearchEngineInfo
            {
                Name = "SeedLab.Search " + _engineVersion,
                Description =
                    "The tiered engine - the same one 'vseed search' runs. A goal declares the cheapest "
                    + "tier that answers it exactly, and a seed climbs the ladder only as far as the "
                    + "query needs: a biome question never pays for heights, and a must-goal that fails "
                    + "stops the seed there. Every rejection is exact or a proven bound; this build "
                    + "ships no heuristic filter at all.",
                IsRealEngine = true,
                MeasuredSeedsPerSecond = _measured,
                DefaultThreads = DefaultThreads,
                ProcessorCount = _runtime.Hardware.LogicalCores,
                LocationsUnavailable = _oracle.Available ? null : _oracle.UnavailableReason,
                ResultsDirectory = _resultsDirectory,
                Bounds = Bounds(2),
            };

            foreach (MetricDef m in MetricCatalog.All) info.Metrics.Add(Describe(m));
            foreach (string b in MetricCatalog.BiomeNames) info.Biomes.Add(b);
            foreach (LocationGroup g in LocationGroups.All)
            {
                info.Groups.Add(new GroupInfo { Name = g.Name, Help = g.Help, Prefabs = g.Prefabs.Count });
            }

            foreach (string name in Presets.Names)
            {
                PresetInfo? p = TryDescribePreset(name);
                if (p != null) info.Presets.Add(p);
            }

            return info;
        }

        public ISearchRun Start(SearchQuery query)
        {
            // Everything the CLI decides before a seed is touched, decided here too - and decided on the
            // SERVER. A page that checked this itself would be a convention; a hand-written POST would
            // walk straight past the refusal rule and the confirmations, and "the browser and the
            // terminal can never disagree" would stop being true the moment anyone used curl.
            SearchPlanner.Planned p = Preflight(query);
            PreflightReport pf = p.Report;

            if (pf.Refusals.Count > 0)
            {
                throw new SearchRefusedException(pf, "refused", string.Join("  ", pf.Refusals));
            }

            if (pf.Confirmations.Count > 0 && !query.Confirmed)
            {
                throw new SearchRefusedException(pf, "confirm",
                    "this run needs a confirmation before it starts: " + string.Join("  ", pf.Confirmations));
            }

            if (pf.Estimate.Verdict == "refuse")
            {
                throw new SearchRefusedException(pf, "refused",
                    "the estimate refuses this run: " + string.Join("  ", pf.Estimate.Reasons));
            }

            if (pf.Estimate.Verdict == "confirm" && !query.Confirmed)
            {
                throw new SearchRefusedException(pf, "confirm",
                    "this run needs a confirmation before it starts: " + string.Join("  ", pf.Estimate.Reasons));
            }

            if (p.Report.Runtime.InsufficientMemory)
            {
                throw new SearchRefusedException(pf, "refused",
                    "one worker needs more memory than the whole budget on this machine - this run should not "
                    + "start. Use a coarser grid, or a smaller region.");
            }

            _runtime.RequireVerifiedMachine();

            Query q = p.Translation.Query;
            CompiledQuery cq = p.Compiled;
            QueryTranslator.Translation t = p.Translation;

            // ---- refuse a query whose answer would be a lie -----------------------------------------
            if (cq.Unavailable.Count > 0)
            {
                List<string> names = new List<string>();
                foreach (CompiledGoal g in cq.Unavailable) names.Add("'" + g.Goal.Id + "' (" + g.Goal.Target + "." + g.Goal.Metric + ")");
                throw new ArgumentException(
                    "this build cannot measure " + string.Join(", ", names) + ": "
                    + cq.Unavailable[0].UnavailableReason
                    + ". Nothing is returned rather than seeds that were never tested against "
                    + (cq.Unavailable.Count == 1 ? "that goal" : "those goals") + ".");
            }

            foreach (CompiledGoal g in cq.Unsatisfiable)
            {
                if (g.Goal.Importance == Importance.Must)
                {
                    throw new ArgumentException(
                        "goal '" + g.Goal.Id + "' can never be satisfied by any seed: " + g.Unsatisfiable
                        + " Refusing to scan for something the generator cannot make.");
                }
            }

            EngineRun run = new EngineRun(p, _oracle, t.QueryJson, cq.Warnings, r => _measured = r);
            if (p.OutPath != null && !ClaimOutput(p.OutPath, run.Id))
            {
                throw new ArgumentException(
                    "a search that is still running is already writing " + p.OutPath
                    + " (run " + OwnerOf(p.OutPath) + "). Two runs on one results file would each rewrite it "
                    + "from their own kept set. Stop that run, or give this one a different file name.");
            }

            run.Begin();
            return run;
        }

        /// <summary>
        /// A copy of the query without the nice-to-have goals this build cannot measure. Returns the
        /// original when there are none to drop; throws when nothing would be left.
        /// </summary>
        private SearchQuery WithoutUnmeasurableNiceGoals(SearchQuery q)
        {
            List<SearchGoal> kept = new List<SearchGoal>();
            List<string> dropped = new List<string>();
            foreach (SearchGoal g in q.Goals)
            {
                bool nice = string.Equals(g.Importance, "nice", StringComparison.Ordinal);
                bool needsTable = (g.Target ?? "").StartsWith("location:", StringComparison.Ordinal)
                                  || (g.Target ?? "").StartsWith("group:", StringComparison.Ordinal)
                                  || string.Equals(g.From, "spawn", StringComparison.Ordinal);
                if (nice && needsTable) { dropped.Add(g.Target + "." + g.Metric); continue; }
                kept.Add(g);
            }

            if (dropped.Count == 0) return q;
            if (kept.Count == 0)
            {
                throw new ArgumentException("every goal in this query needs the dumped location table, so "
                                            + "skip_unavailable would leave nothing to search for.");
            }

            SearchQuery copy = (SearchQuery)q.MemberwiseCloneShim();
            copy.Goals = kept;
            return copy;
        }

        // -----------------------------------------------------------------------------------------
        private static PresetInfo? TryDescribePreset(string name)
        {
            Query q;
            try
            {
                q = Presets.Load(name);
            }
            catch (QueryException)
            {
                // A preset that no longer parses is a build problem, not a reason to break the panel.
                return null;
            }

            PresetInfo p = new PresetInfo
            {
                Name = name,
                Description = q.Description ?? q.Name ?? "",
                GridSpacingM = q.Search.Grid,
            };

            foreach (Goal g in q.Goals)
            {
                if (g.Target.Kind == TargetKind.Location || g.Target.Kind == TargetKind.Group) p.NeedsLocations = true;
                if (g.From == DistanceOrigin.Spawn) p.NeedsLocations = true;
                p.Goals.Add(new SearchGoal
                {
                    Id = g.Id,
                    Target = g.Target.ToString(),
                    Metric = g.Metric,
                    Test = Lower(g.Test),
                    Value = g.Value,
                    Max = g.Max,
                    Radius = g.Radius,
                    Height = g.Height,
                    MinArea = g.MinArea,
                    From = Lower(g.From),
                    Importance = Lower(g.Importance),
                    Weight = g.Weight,
                    Pad = g.Pad,
                });
            }

            return p;
        }

        private static MetricInfo Describe(MetricDef m)
        {
            MetricInfo i = new MetricInfo
            {
                Kind = m.Kind.ToString().ToLowerInvariant(),
                Name = m.Name,
                Help = m.Help,
                Tier = m.Tier.ToString(),
                Unit = m.Unit.ToString().ToLowerInvariant(),
                NeedsRadius = m.NeedsRadius,
                NeedsHeight = m.NeedsHeight,
                NeedsLocations = m.NeedsLocations,
            };

            switch (m.Unit)
            {
                case Unit.Metres:
                    i.UnitLabel = "m";
                    i.Scale = 1.0;
                    i.DefaultValue = m.Name.Contains("distance") ? 2500 : 300;
                    break;
                case Unit.SquareMetres:
                    // Typed in km², stored in m². Nobody writes 9000000 for nine square kilometres.
                    i.UnitLabel = "km²";
                    i.Scale = 1e6;
                    i.DefaultValue = m.Name.Contains("island") ? 9 : 40;
                    break;
                case Unit.Fraction:
                    i.UnitLabel = "%";
                    i.Scale = 0.01;
                    i.DefaultValue = 25;
                    break;
                default:
                    i.UnitLabel = "count";
                    i.Scale = 1.0;
                    i.DefaultValue = m.Name == "present" ? 1 : 200;
                    break;
            }

            // near/far are the distance-flavoured spellings of at_most/at_least: identical verdicts,
            // differently shaped nice-to-have scores. Offer them first where they read naturally.
            if (m.Unit == Unit.Metres && m.Name.Contains("distance"))
            {
                i.Tests.AddRange(new[] { "near", "far", "between" });
            }
            else
            {
                i.Tests.AddRange(new[] { "at_least", "at_most", "between" });
            }

            return i;
        }

        private static string Lower(object v) => v.ToString()!.ToLowerInvariant() switch
        {
            "atleast" => "at_least",
            "atmost" => "at_most",
            string s => s,
        };

        // =========================================================================================
        private sealed class EngineRun : ISearchRun
        {
            /// <summary>Live result lines per second on the wire. The best-of table is authoritative.</summary>
            private const int ResultsPerSecond = 25;

            private readonly Query _q;
            private CompiledQuery _cq;

            /// <summary>Not readonly: a funnel replaces it with the survivor plan between its stages.</summary>
            private ScanPlan _plan;
            private readonly ILocationOracle _oracle;
            private readonly int _threads;
            private readonly string _hash;
            private readonly string _queryJson;
            private readonly IReadOnlyList<string> _warnings;
            private readonly Action<double> _reportRate;
            private readonly SearchEventHub _hub = new SearchEventHub();
            private readonly object _lock = new object();
            private readonly List<SeedResult> _top = new List<SeedResult>();
            private readonly SearchProgress _progress = new SearchProgress();

            private SearchRun? _run;
            private Thread? _thread;
            private volatile bool _cancelRequested;
            private long _lastResultTicks;
            private long _resultAllowance;
            private DateTime _lastTop = DateTime.MinValue;
            private bool _topDirty;
            private long _streamedResults;
            private long _suppressedResults;
            private readonly SearchPlanner.Planned _planned;

            /// <summary>Not readonly, for the same reason as <see cref="_plan"/>.</summary>
            private SearchSession _session;
            private IResultSink? _sink;
            private long _freeBytes = -1;
            private long _freeCheckedTicks;
            private readonly double _bytesPerRecord;

            public EngineRun(SearchPlanner.Planned planned, ILocationOracle oracle, string queryJson,
                             IReadOnlyList<string> warnings, Action<double> reportRate)
            {
                _planned = planned;
                _session = planned.Session;
                _q = _session.Query;
                _cq = _session.Compiled;
                _plan = _session.Plan;
                _oracle = oracle;
                _threads = _session.Threads;
                _hash = _session.QueryHash;
                _queryJson = queryJson;
                _warnings = warnings;
                _reportRate = reportRate;
                _bytesPerRecord = planned.Report.Output.BytesPerRecord;
                Id = "s" + DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture);
                _progress.Limit = _plan.Limit;
                _progress.FractionOfSpace = _plan.FractionOfSpace;
            }

            public string Id { get; }

            public SearchProgress Progress
            {
                get { lock (_lock) { return _progress; } }
            }

            public void Begin()
            {
                _hub.Publish(new SearchEvent("started", new
                {
                    id = Id,
                    queryHash = _hash,
                    queryJson = _queryJson,
                    key = "0x" + _plan.Key.ToString("X16", CultureInfo.InvariantCulture),
                    order = _plan.Order.ToString().ToLowerInvariant(),
                    from = _plan.From,
                    to = _plan.To,
                    rangeSeeds = _plan.Count,
                    limit = _plan.Limit,
                    blocks = _plan.Blocks,
                    blockSize = _plan.BlockSize,
                    fractionOfSpace = _plan.FractionOfSpace,
                    grid = _q.Search.Grid,
                    gridIsGameGrid = Math.Abs(_q.Search.Grid - 12.0) < 1e-9,
                    regionRadiusM = _cq.Plan.Radius,
                    maxTier = _cq.MaxTier.ToString(),
                    threads = _threads,
                    keep = _q.Search.Keep,
                    keepAll = _q.Search.KeepAll,
                    wallSeconds = _q.Search.Wall.TotalSeconds,
                    warnings = _warnings,
                    unsatisfiable = Unsatisfiable(),
                    goals = GoalPlan(),

                    // The whole pre-run report travels with the run, so the panel's plan block, its
                    // estimate and its cost badges are the SAME objects the refusal gate just used -
                    // not a second rendering of the same idea that could drift from it.
                    preflight = _planned.Report,
                    outPath = _planned.OutPath,
                    checkpoint = _session.CheckpointPath,
                    cleanedOrphans = _session.CleanedOrphans,
                }));

                _thread = new Thread(Run) { IsBackground = true, Name = "vseed-web-search" };
                _thread.Start();
            }

            /// <summary>
            /// Stop at the next block boundary. Safe before the worker thread has built its
            /// <see cref="SearchRun"/>: a cancel that arrives in that window is remembered and applied
            /// the moment the run exists, rather than being dropped on a null.
            /// </summary>
            public void Cancel()
            {
                _cancelRequested = true;
                _run?.RequestStop();
            }

            public System.Collections.Generic.IAsyncEnumerable<SearchEvent> ReadEvents(CancellationToken ct)
                => _hub.Read(ct);

            /// <summary>
            /// A funnel's first stage, on the worker thread so its progress reaches the page.
            ///
            /// <para>Returns false when the run should stop here - stage one found nothing, or the
            /// measured cost of stage two needs a confirmation this request did not carry. The survivor
            /// list is on disk either way, so confirming and re-submitting does not repeat stage one.</para>
            ///
            /// <para><b>Stage one is given the FULL run's plan.</b> Not an optimisation - a correctness
            /// condition. <c>ScanPlan</c> derives its permutation key from the query hash, and the
            /// stage-one query is a different query with a different hash, so a stage one left to build
            /// its own plan walks a different sample of the space than the run it stands in for. The
            /// terminal carries the same comment for the same reason.</para>
            /// </summary>
            private bool RunStageOne()
            {
                FunnelPlan funnel = _planned.Funnel;
                string survivorPath = _session.CheckpointPath.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase)
                    ? _session.CheckpointPath.Substring(0, _session.CheckpointPath.Length - 5) + ".survivors"
                    : _session.CheckpointPath + ".survivors";

                string stamp = _oracle.Available ? _oracle.Provenance : "";
                int[]? survivors = null;
                SurvivorList.Header? head = null;
                double stageOneSeconds = -1;

                if (System.IO.File.Exists(survivorPath))
                {
                    try
                    {
                        survivors = SurvivorList.Read(survivorPath, _hash, stamp,
                                                      _q.Search.From, _q.Search.To, _plan.Limit,
                                                      out SurvivorList.Header h);
                        head = h;
                    }
                    catch (Exception)
                    {
                        // A survivor list that does not match this query, this build or its own declared
                        // length is not a shortcut - it is a different search. Redo stage one.
                        survivors = null;
                    }
                }

                if (survivors == null)
                {
                    SearchSession one = SearchSession.Create(
                        funnel.StageOneQuery!, _oracle, _planned.EngineVersion,
                        _plan.Limit, _threads, null, false, true, true, _plan,
                        overrideDecision: _session.BlockDecision);

                    _hub.Publish(new SearchEvent("stage", new
                    {
                        stage = 1,
                        of = 2,
                        measuring = funnel.StageOneGoals,
                        deferring = funnel.DeferredGoals,
                        seeds = one.Plan.Limit,
                    }));

                    SurvivorSink sink = new SurvivorSink();
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    SearchOutcome res = one.Run(sink, p =>
                    {
                        lock (_lock)
                        {
                            _progress.Status = "running";
                            _progress.Message = "stage 1 of 2: " + p.Evaluated.ToString("N0", CultureInfo.InvariantCulture)
                                                + " of " + p.Limit.ToString("N0", CultureInfo.InvariantCulture)
                                                + " seeds measured, " + sink.Count.ToString("N0", CultureInfo.InvariantCulture)
                                                + " kept";
                            _progress.Scanned = p.Evaluated;
                        }
                    }, _q.Search.Wall, TimeSpan.FromSeconds(30), run =>
                    {
                        _run = run;
                        if (_cancelRequested) run.RequestStop();
                    });

                    sw.Stop();
                    stageOneSeconds = sw.Elapsed.TotalSeconds;

                    // A stage one that was stopped - the Stop button or the time budget - saw a PREFIX of
                    // the range, so its survivors are not the answer's candidates. This page used to
                    // write them as the survivor list anyway and go on to stage two with a fresh, whole
                    // budget (so a run could take about twice its budget), where the terminal stops. It
                    // stops here too now, writes nothing, and says why - the user's decision of
                    // 2026-09-24, which is what lets one budget sentence in the plan be true on both.
                    if (res.StoppedByUser || res.StoppedByWall)
                    {
                        StoppedInStageOne(res, one.Plan.Limit, sink.Count);
                        return false;
                    }

                    survivors = new int[sink.Seeds.Count];
                    for (int i = 0; i < survivors.Length; i++) survivors[i] = sink.Seeds[i];
                    head = new SurvivorList.Header
                    {
                        QueryHash = _hash,
                        Stamp = stamp,
                        From = _q.Search.From,
                        To = _q.Search.To,
                        Scanned = res.Evaluated,
                    };

                    try
                    {
                        SurvivorList.Write(survivorPath, head, survivors, SurvivorList.DefaultMaxBytes);
                    }
                    catch (InvalidOperationException ex)
                    {
                        Fail(ex.Message);
                        return false;
                    }
                }

                // Stage two's block size, decided ONCE for the gate and for stage two's plan by the rule
                // the terminal uses, over the survivor count - few survivors is exactly where a fixed
                // size left workers idle. The page never resumes, so there is no checkpoint to adopt.
                BlockSizeDecision stageTwo = BlockSizing.Decide(_q.Search.BlockSize, survivors.Length, _threads);

                FunnelGate gate = new FunnelGate
                {
                    Scanned = head!.Scanned,
                    Survivors = survivors.Length,
                    Decision = stageTwo,
                    Wall = _q.Search.Wall,
                    StageOneSeconds = stageOneSeconds,
                    SecondsPerSurvivor = MeasurePlacement(survivors),
                };

                List<string> lines = new List<string>(gate.Lines());
                _hub.Publish(new SearchEvent("funnelGate", new
                {
                    scanned = gate.Scanned,
                    survivors = gate.Survivors,
                    ratio = gate.Ratio,
                    survivorBytes = gate.SurvivorBytes,
                    secondsPerSurvivor = gate.SecondsPerSurvivor,
                    stageTwoSeconds = gate.StageTwoSeconds,
                    effectiveWorkers = gate.EffectiveWorkers,
                    pointless = gate.Pointless,
                    lines,
                }));

                if (survivors.Length == 0)
                {
                    const string none = "stage 1 kept no seeds, so there is nothing for stage 2 to place - no seed in "
                                        + "this run matches";
                    lock (_lock)
                    {
                        _progress.Status = "done";
                        _progress.Message = none;
                        _progress.Scanned = gate.Scanned;
                        _progress.Passed = 0;
                        _progress.Limit = _plan.Limit;
                    }

                    PublishEnded("done", none, complete: true);
                    return false;
                }

                try
                {
                    _session = SearchSession.Create(_q, _oracle, _planned.EngineVersion, _plan.Limit,
                                                    _threads, _planned.OutPath, false, true, false,
                                                    ScanPlan.OverSeeds(survivors, stageTwo.Size),
                                                    overrideDecision: stageTwo);
                }
                catch (Exception ex)
                {
                    Fail(ex.Message);
                    return false;
                }

                _plan = _session.Plan;
                _cq = _session.Compiled;
                lock (_lock)
                {
                    _progress.Limit = _plan.Limit;
                    _progress.Message = "stage 2 of 2: placing " + _plan.Limit.ToString("N0", CultureInfo.InvariantCulture)
                                        + " survivors";
                }

                return true;
            }

            /// <summary>
            /// Seconds the full query costs per seed, measured on SURVIVORS - the only seeds stage two
            /// runs. Measuring on seeds from the range instead times the tier ladder's early exit, which
            /// is the cost stage two does not pay; that version reported 0.00 s for work that took 16.8 s.
            /// </summary>
            private double MeasurePlacement(int[] survivors)
            {
                if (survivors.Length == 0) return 0.0;
                SeedEvaluator warm = new SeedEvaluator(_session.Compiled, _oracle);
                warm.Evaluate(survivors[0]);

                int n = Math.Min(4, survivors.Length);
                SeedEvaluator timed = new SeedEvaluator(_session.Compiled, _oracle);
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < n; i++) timed.Evaluate(survivors[i]);
                sw.Stop();
                return sw.Elapsed.TotalSeconds / n;
            }

            /// <summary>
            /// Ends the run after a stage one the Stop button or the time budget cut short - the same
            /// stop and the same words as the terminal's "Stopped during stage 1". Published as
            /// <c>done</c>, the event the page ends a run on. The rate is stage one's, of the cheap
            /// goals only, so it is marked as not the machine's rate for this query.
            /// </summary>
            private void StoppedInStageOne(SearchOutcome res, long stageOneLimit, long kept)
            {
                string why = res.StoppedByUser ? "you stopped it" : "the wall-clock budget ran out";
                string message = "stopped during stage 1 - " + why + " after "
                                 + res.Evaluated.ToString("N0", CultureInfo.InvariantCulture) + " of "
                                 + stageOneLimit.ToString("N0", CultureInfo.InvariantCulture) + " seeds, with "
                                 + kept.ToString("N0", CultureInfo.InvariantCulture) + " survivors so far. Stage 2 "
                                 + "has NOT run: these are the survivors of the seeds stage 1 reached, not of the "
                                 + "range asked for, and placing them would answer a narrower question. Nothing was "
                                 + "written, and stage 1 has no resume point of its own, so re-running repeats it"
                                 + (res.StoppedByWall ? " - a smaller seed count or a longer budget lets it finish" : "");
                string status = res.StoppedByUser ? "cancelled" : "done";

                lock (_lock)
                {
                    _progress.Status = status;
                    _progress.Message = message;
                    _progress.Scanned = res.Evaluated;
                    _progress.Passed = 0;
                    _progress.Limit = stageOneLimit;
                    _progress.ElapsedS = res.Seconds;
                    _progress.SeedsPerSecond = res.SeedsPerSecond;
                    _progress.FractionCovered = res.Evaluated / 4294967296.0;
                    _progress.EtaSeconds = 0;
                }

                _hub.Publish(new SearchEvent("done", new
                {
                    status,
                    message,
                    stage = 1,
                    scanned = res.Evaluated,
                    passed = 0,
                    limit = stageOneLimit,
                    elapsedS = res.Seconds,
                    seedsPerSecond = res.SeedsPerSecond,
                    threads = res.Threads,
                    busyWorkers = res.BusyWorkers,
                    rateNote = res.RateNote,
                    rateIsMachine = false,
                    blocks = res.Blocks,
                    blocksDone = res.BlocksDone,

                    // Stage one runs the full plan, so its block size is the plan's - carried like on
                    // every other done event (it was missing here, review of 2026-09-24).
                    blockSize = _plan.BlockSize,
                    complete = false,
                    stoppedByWall = res.StoppedByWall,
                    fractionOfSpace = _plan.FractionOfSpace,
                    fractionCovered = res.Evaluated / 4294967296.0,
                    kept = 0,
                    resultsWritten = 0,
                    resultBytes = -1,
                }));
            }

            private void Fail(string message)
            {
                lock (_lock)
                {
                    _progress.Status = "failed";
                    _progress.Message = message;
                }

                PublishEnded("failed", message, complete: false);
            }

            /// <summary>
            /// Ends a run that stops before it has a <see cref="SearchOutcome"/> to report - a failure
            /// before or between the stages, or a funnel whose stage one kept no seed - with the same
            /// <c>done</c> event every other end publishes, carrying the fields the page draws its final
            /// progress line from.
            ///
            /// <para>These two used to publish <c>finished</c>, which the page has no listener for. The
            /// server closed the stream, the browser's EventSource reconnected about every 3 s and was
            /// replayed the run, and Run stayed disabled and Stop enabled for good (review of
            /// 2026-09-24: a funnel on Meadows area plus a Haldor distance at G192, 64 seeds, gave 5
            /// streams and 5 <c>finished</c> events in 15 s and no <c>done</c>). The page's
            /// <c>done</c> handler already shows a failed run's message.</para>
            /// </summary>
            private void PublishEnded(string status, string message, bool complete)
            {
                long scanned, passed, limit;
                double elapsed, rate;
                lock (_lock)
                {
                    scanned = _progress.Scanned;
                    passed = _progress.Passed;
                    limit = _progress.Limit;
                    elapsed = _progress.ElapsedS;
                    rate = _progress.SeedsPerSecond;
                }

                _hub.Publish(new SearchEvent("done", new
                {
                    status,
                    message,
                    scanned,
                    passed,
                    limit,
                    elapsedS = elapsed,
                    seedsPerSecond = rate,
                    threads = _threads,
                    blockSize = _plan.BlockSize,
                    rateIsMachine = false,
                    complete,
                    stoppedByWall = false,
                    fractionOfSpace = _plan.FractionOfSpace,
                    fractionCovered = scanned / 4294967296.0,
                    kept = 0,
                    resultsWritten = 0,
                    resultBytes = -1,
                }));
            }

            // -------------------------------------------------------------------------------------
            private void Run()
            {
                SearchOutcome outcome;
                try
                {
                    // The session opens the run: it cleans orphaned .ckpt.tmp files, creates the
                    // checkpoint, repairs a torn results tail on resume and builds the sink that makes
                    // 'keep' a REAL cap on the file. It is the identical call the terminal makes.
                    if (_planned.OutPath != null)
                    {
                        string? dir = System.IO.Path.GetDirectoryName(_planned.OutPath);
                        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    }

                    if (_planned.Strategy == SearchStrategy.Funnel && !RunStageOne()) return;

                    _sink = _session.Start(resume: false, checkpointPath: _session.CheckpointPath);

                    outcome = _session.Run(_sink, OnProgress, _q.Search.Wall, TimeSpan.FromSeconds(30),
                        run =>
                        {
                            _run = run;
                            run.OnResult = OnResult;
                            if (_cancelRequested) run.RequestStop();
                        });

                    _sink?.Finish();
                    FlushTop(force: true);

                    // Kept as the machine's rate ("measured here ... the whole space would take") only
                    // when every worker had work. A run in fewer blocks than workers - a Block size too
                    // large for the seed count, a budget stop before every worker claimed - measured the
                    // busy workers, and the page used to store that starved number as this machine's and
                    // extrapolate the whole space from it (2026-09-24).
                    bool machineRate = outcome.BusyWorkers >= outcome.Threads;
                    if (machineRate && outcome.Seconds > 0 && outcome.Evaluated > 0) _reportRate(outcome.SeedsPerSecond);

                    string status = outcome.StoppedByUser ? "cancelled" : "done";
                    string? message = outcome.StoppedByUser
                        ? "stopped at a block boundary; every seed below the count above was evaluated"
                        : outcome.StoppedByWall
                            ? "the wall-clock budget ran out"
                            : outcome.Complete ? null : "the run ended before the range was exhausted";

                    lock (_lock)
                    {
                        _progress.Status = status;
                        _progress.Message = message;
                        _progress.Scanned = outcome.Evaluated;
                        _progress.Passed = outcome.Passed;
                        _progress.ElapsedS = outcome.Seconds;
                        _progress.SeedsPerSecond = outcome.SeedsPerSecond;
                        _progress.FractionCovered = outcome.Evaluated / 4294967296.0;
                        _progress.EtaSeconds = 0;
                        _progress.Kept = outcome.ResultsWritten;
                        _progress.ResultBytes = _sink != null ? _sink.FileBytes : -1;
                        _progress.FreeBytes = FreeBytes(true);
                    }

                    _hub.Publish(new SearchEvent("done", new
                    {
                        status,
                        message,
                        scanned = outcome.Evaluated,
                        passed = outcome.Passed,
                        // The limit travels on the final event too: the page draws the same progress
                        // line from 'progress' and from 'done', and a missing limit there printed
                        // "600 of 0 seeds" at the end of every run.
                        limit = _plan.Limit,
                        elapsedS = outcome.Seconds,
                        seedsPerSecond = outcome.SeedsPerSecond,
                        blocks = outcome.Blocks,
                        blocksDone = outcome.BlocksDone,
                        blockSize = _plan.BlockSize,
                        complete = outcome.Complete,
                        stoppedByWall = outcome.StoppedByWall,
                        fractionOfSpace = _plan.FractionOfSpace,
                        fractionCovered = outcome.Evaluated / 4294967296.0,
                        threads = outcome.Threads,

                        // The workers that had blocks, the label the rate needs when that is fewer
                        // than the threads - the same words the terminal prints - and whether the rate
                        // may be kept as the machine's.
                        busyWorkers = outcome.BusyWorkers,
                        rateNote = outcome.RateNote,
                        rateIsMachine = machineRate,
                        probeAccepts = outcome.ProbeAccepts,
                        earlyExits = outcome.EarlyExits,
                        pregenerated = outcome.Pregenerated,
                        constructSeconds = outcome.ConstructSeconds,
                        sampleSeconds = outcome.SampleSeconds,
                        pregenSeconds = outcome.PregenSeconds,
                        streamedResults = Interlocked.Read(ref _streamedResults),
                        suppressedResults = Interlocked.Read(ref _suppressedResults),
                        wholeSpaceSeconds = machineRate && outcome.SeedsPerSecond > 0
                            ? 4294967296.0 / outcome.SeedsPerSecond
                            : (double?)null,

                        // What actually reached the disk, and the line that never says "N matches"
                        // when N was capped.
                        resultsPath = outcome.ResultsPath,
                        resultsWritten = outcome.ResultsWritten,
                        kept = outcome.ResultsWritten,
                        resultBytes = _sink != null ? _sink.FileBytes : -1,
                        resultsDropped = outcome.ResultsDropped,
                        bounded = outcome.Bounded,
                        cutScore = double.IsNaN(outcome.CutScore) ? (double?)null : outcome.CutScore,
                        firstDropScore = double.IsNaN(outcome.FirstDropScore) ? (double?)null : outcome.FirstDropScore,
                        resultLine = _session.ResultLine(outcome),
                        stoppedByLimit = outcome.StoppedByLimit,
                        checkpointPath = outcome.CheckpointPath,
                        resumeCommand = outcome.CheckpointPath == null
                            ? null
                            : "vseed search <this query file> --resume --checkpoint \"" + outcome.CheckpointPath + "\"",
                        repairedBytes = _session.RepairedBytes,
                        freeBytes = FreeBytes(true),
                    }));
                }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        _progress.Status = "failed";
                        _progress.Message = ex.GetType().Name + ": " + ex.Message;
                    }

                    _hub.Publish(new SearchEvent("done", new
                    {
                        status = "failed",
                        message = ex.GetType().Name + ": " + ex.Message,
                        scanned = _progress.Scanned,
                        passed = _progress.Passed,
                        limit = _plan.Limit,
                    }));
                }
                finally
                {
                    try { _sink?.Dispose(); } catch (Exception) { }
                    ReleaseOutput(_planned.OutPath);
                    _hub.Close();
                }
            }

            /// <summary>
            /// Called on the collector thread for every passing seed, in block order - the same call
            /// site that writes the results file in the terminal.
            /// </summary>
            private void OnResult(SeedResult r)
            {
                bool send;
                lock (_lock)
                {
                    Insert(r);
                    _topDirty = true;

                    // A generous query can pass thousands of seeds a second. Everything is kept in the
                    // best-of list; only the "latest finds" ticker is rate-limited, because 5,000 DOM
                    // rows a second is how a browser tab stops responding. The counter below says how
                    // many lines were not sent, so the feed never pretends to be the whole set.
                    long now = Environment.TickCount64;
                    long elapsed = now - _lastResultTicks;
                    _lastResultTicks = now;
                    _resultAllowance = Math.Min(ResultsPerSecond, _resultAllowance + elapsed * ResultsPerSecond / 1000);
                    send = _resultAllowance >= 1;
                    if (send) _resultAllowance--;
                }

                if (send)
                {
                    Interlocked.Increment(ref _streamedResults);
                    _hub.Publish(new SearchEvent("result", Hit(r)));
                }
                else
                {
                    Interlocked.Increment(ref _suppressedResults);
                }

                FlushTop(force: false);
            }

            /// <summary>
            /// Free space on the volume the results are going to, refreshed at most every two seconds.
            /// -1 when this run keeps nothing, because then there is no volume it is filling.
            /// </summary>
            private long FreeBytes(bool force)
            {
                if (_planned.OutPath == null) return -1;
                long now = Environment.TickCount64;
                if (!force && _freeBytes >= 0 && now - _freeCheckedTicks < 2000) return _freeBytes;
                _freeCheckedTicks = now;
                try { _freeBytes = VolumeInfo.For(_planned.OutPath).FreeBytes; }
                catch (Exception) { _freeBytes = -1; }
                return _freeBytes;
            }

            private void OnProgress(SeedLab.Search.Execution.Progress p)
            {
                lock (_lock)
                {
                    _progress.Scanned = p.Evaluated;
                    _progress.Passed = p.Passed;
                    _progress.Limit = p.Limit;
                    _progress.ElapsedS = p.Seconds;
                    _progress.SeedsPerSecond = p.SeedsPerSecond;
                    _progress.EtaSeconds = p.EtaSeconds;
                    _progress.FractionOfSpace = p.FractionOfSpace;
                    _progress.FractionCovered = p.FractionCovered;
                    _progress.ProbeAccepts = p.ProbeAccepts;
                    _progress.EarlyExits = p.EarlyExits;
                }

                // Decision 10's live line, in full: elapsed, rate, ETA, seeds and % of 2^32, hits found
                // and kept, bytes written, the projected final size and the free space left. The
                // projection is an approximation and is labelled one: it is the measured bytes-per-record
                // so far times the hit rate so far, extended over the rest of the range.
                IResultSink? sink = _sink;
                long bytes = sink != null ? sink.FileBytes : p.ResultBytes;
                long kept = sink != null ? sink.Kept : p.Kept;

                // The projection, and the reason it cannot simply be bytes/records: a BOUNDED sink
                // rewrites its whole file from the heap at each flush, so FileBytes is legitimately 0
                // between flushes and dividing by it would print "-" for most of a run. The measured
                // per-record law (54 + 150.5 x goals for JSONL, from the disk audit) is what the
                // preflight already quoted, so the same number is used here and the projection exists
                // from the first block. It is an approximation and says so on the wire.
                double perRecord = _bytesPerRecord > 0
                    ? _bytesPerRecord
                    : (bytes > 0 && kept > 0 ? (double)bytes / kept : 0);
                double? projected = null;
                if (perRecord > 0 && p.Evaluated > 0 && p.Limit > 0 && _planned.OutPath != null)
                {
                    double hitRate = (double)p.Passed / p.Evaluated;
                    double expected = _q.Search.KeepAll ? hitRate * p.Limit : Math.Min(_q.Search.Keep, hitRate * p.Limit);
                    projected = perRecord * expected;
                }

                _hub.Publish(new SearchEvent("progress", new
                {
                    scanned = p.Evaluated,
                    passed = p.Passed,
                    limit = p.Limit,
                    elapsedS = p.Seconds,
                    seedsPerSecond = p.SeedsPerSecond,
                    etaSeconds = double.IsFinite(p.EtaSeconds) ? p.EtaSeconds : (double?)null,
                    fractionOfSpace = p.FractionOfSpace,
                    fractionCovered = p.FractionCovered,
                    probeAccepts = p.ProbeAccepts,
                    earlyExits = p.EarlyExits,
                    suppressedResults = Interlocked.Read(ref _suppressedResults),
                    kept,
                    resultBytes = bytes,
                    bytesPerRecord = perRecord,
                    boundedRewrites = sink != null && sink.IsBounded,
                    projectedBytes = projected,
                    projectedIsApproximate = true,
                    freeBytes = FreeBytes(false),
                    pendingBlocks = p.PendingBlocks,
                }));

                FlushTop(force: false);
            }

            /// <summary>
            /// The authoritative results table: the same <c>(score desc, seed asc)</c> total order
            /// <see cref="SearchRun.CompareResults"/> applies to the terminal's "best of" list, over the
            /// same emitted records. This is what the page renders, so the two cannot drift apart.
            /// </summary>
            private void FlushTop(bool force)
            {
                List<object> rows;
                lock (_lock)
                {
                    if (!_topDirty) return;
                    if (!force && (DateTime.UtcNow - _lastTop).TotalMilliseconds < 700) return;
                    _lastTop = DateTime.UtcNow;
                    _topDirty = false;
                    _top.Sort(SearchRun.CompareResults);
                    int keep = Math.Min(_q.Search.Keep, _top.Count);
                    rows = new List<object>(keep);
                    for (int i = 0; i < keep; i++) rows.Add(Hit(_top[i]));
                }

                _hub.Publish(new SearchEvent("top", new { results = rows }));
            }

            private void Insert(SeedResult r)
            {
                _top.Add(r);
                int keep = _q.Search.Keep;
                if (_top.Count <= keep * 2L) return;
                _top.Sort(SearchRun.CompareResults);
                _top.RemoveRange(keep, _top.Count - keep);
            }

            private object Hit(SeedResult r)
            {
                List<GoalResult> goals = new List<GoalResult>(r.Goals.Count);
                foreach (GoalOutcome g in r.Goals)
                {
                    goals.Add(new GoalResult
                    {
                        Id = g.Id,
                        Target = g.Target,
                        Metric = g.Metric,
                        Test = Lower(g.Test),
                        Importance = Lower(g.Importance),
                        Threshold = g.Threshold,
                        Value = double.IsFinite(g.Value) ? g.Value : (double?)null,
                        Unit = g.Unit.ToString().ToLowerInvariant(),
                        Pass = g.Pass,
                        Score = g.Score,
                        Bounded = g.Bounded,
                        BoundRadius = g.BoundRadius,
                        Tier = g.Tier.ToString(),
                        Unavailable = g.Unavailable,
                    });
                }

                return new SearchHit
                {
                    Seed = r.Seed,
                    Text = r.SeedText ?? SeedText.Invert(r.Seed, SeedAlphabet.Alnum62)
                           ?? r.Seed.ToString(CultureInfo.InvariantCulture),
                    Score = r.Score,
                    Tier = r.TierReached.ToString(),
                    RegionRadiusM = r.RegionRadiusM,
                    Goals = goals,
                    LandKm2 = r.LandAreaM2.HasValue ? r.LandAreaM2.Value / 1e6 : (double?)null,
                    LargestIslandKm2 = r.LargestIslandM2.HasValue ? r.LargestIslandM2.Value / 1e6 : (double?)null,
                    IslandCount1Ha = r.IslandCount1Ha,
                    OceanShare = r.OceanShare,
                    PeakM = r.HighestPeak,
                };
            }

            private List<object> GoalPlan()
            {
                List<object> rows = new List<object>();
                foreach (CompiledGoal g in _cq.Goals)
                {
                    rows.Add(new
                    {
                        id = g.Goal.Id,
                        target = g.Goal.Target.ToString(),
                        metric = g.Goal.Metric,
                        test = Lower(g.Goal.Test),
                        importance = Lower(g.Goal.Importance),
                        value = g.Goal.Value,
                        unit = g.Def.Unit.ToString().ToLowerInvariant(),
                        tier = g.Tier.ToString(),
                        regionRadiusM = g.RegionRadius,
                        mayBeCensored = g.MayBeCensored,
                        available = g.Available,
                        unavailable = g.UnavailableReason,
                        unsatisfiable = g.Unsatisfiable,
                        help = g.Def.Help,
                    });
                }

                return rows;
            }

            private List<object> Unsatisfiable()
            {
                List<object> rows = new List<object>();
                foreach (CompiledGoal g in _cq.Unsatisfiable)
                {
                    rows.Add(new { id = g.Goal.Id, reason = g.Unsatisfiable, importance = Lower(g.Goal.Importance) });
                }

                return rows;
            }
        }
    }
}
