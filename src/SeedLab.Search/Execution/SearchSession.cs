using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;
using SeedLab.Search.Output;

namespace SeedLab.Search.Execution
{
    /// <summary>
    /// One search, wired up: the grid decision, the screening pass, the bounded sink, the checkpoint,
    /// the estimator and the resume, behind one object.
    ///
    /// <para><b>Why this exists.</b> The safety of a run is a property of how those pieces are put
    /// together, not of any one of them: a bounded sink with no snapshot loses its set on resume, a
    /// checkpoint with no torn-file repair resumes into a broken file, a screening pass whose
    /// survivors are not re-measured writes coarse numbers under a fine grid's name. Assembling them
    /// in one place means the CLI and the web UI get the same guarantees, and a front end cannot
    /// accidentally build a half-safe run.</para>
    /// </summary>
    public sealed class SearchSession
    {
        private SearchSession()
        {
        }

        public Query Query = null!;
        public CompiledQuery Compiled = null!;

        /// <summary>The coarse screening query, or null when this run measures once.</summary>
        public CompiledQuery? Screen;

        public ScanPlan Plan = null!;
        public OutputPolicy Output = null!;
        public SearchPreflight Preflight = null!;
        public GridPlan Grid = null!;
        public RunEstimator Estimator = new RunEstimator();
        public string QueryHash = "";
        public string CheckpointPath = "";
        public int Threads;

        /// <summary>Orphaned <c>.ckpt.tmp</c> files this session cleaned up. Reported, not hidden.</summary>
        public List<string> CleanedOrphans = new List<string>();

        /// <summary>
        /// Every location target this session rewrote from a typed name to a prefab, one sentence each,
        /// plus any caveat the match carried. Also copied into <see cref="SearchPreflight.Warnings"/>,
        /// so a front end that prints only the preflight still shows them; kept separately so a front
        /// end that wants to render them differently can tell them apart from a grid warning.
        /// </summary>
        public List<string> NameNotes = new List<string>();

        /// <summary>Bytes of a torn results tail the resume discarded, or 0.</summary>
        public long RepairedBytes;

        private ILocationOracle _oracle = null!;
        private string _engine = "";
        private Checkpoint _checkpoint = null!;
        private long _startBlock;
        private long _resumedSeeds, _resumedPassed;
        private double _resumedSeconds;
        private BoundedResultSet? _restored;

        /// <summary>
        /// Builds a session: compiles the query, decides the grid, resolves the output policy and runs
        /// the preflight. Nothing is created on disk and no seed is touched until <see cref="Start"/>.
        /// </summary>
        /// <param name="planOverride">
        /// A scan plan to use instead of the one the query's range implies - the survivor list of a
        /// funnel's first stage. It is applied BEFORE the preflight, so the estimate, the confirmations
        /// and the plan block all describe the run that will actually happen rather than the range it
        /// was derived from.
        /// </param>
        public static SearchSession Create(Query q, ILocationOracle oracle, string engineVersion,
                                           long seedBudget, int threads, string? outPath = null,
                                           bool noPrefilter = false, bool acceptScanOrder = false,
                                           bool allowVacuous = false, ScanPlan? planOverride = null,
                                           bool alreadyRaised = false)
        {
            SearchSession s = new SearchSession
            {
                Query = q,
                _oracle = oracle,
                _engine = engineVersion,
                Threads = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount),
            };

            // ---- names in, prefabs out: done HERE, before anything derives an identity from the query --
            //
            // A hand-written query file may say location:"The Elder". Everything downstream - the
            // compiled goal, the canonical JSON, the run hash, the checkpoint's MustMatch, the goal id
            // in every record and in every CSV column header - has to speak ONE identity, and the
            // identity SeedLab has always used is the prefab. So the typed spelling is turned into the
            // prefab once, at the top, rather than being tolerated separately by each of those.
            //
            // Order matters and is the whole point of doing it here and not later: Compile() below
            // resolves prefabs and Hash() reads the canonical JSON, so a rewrite that happened after
            // either of them would give the SAME query two different run hashes depending on how it
            // was spelled - and a resume, a checkpoint or a results file from the prefab spelling
            // would no longer match the one from the display-name spelling.
            List<string> nameNotes = CanonicaliseLocationTargets(q, oracle);

            s.Compiled = CompiledQuery.Compile(q, oracle, noPrefilter);
            s.QueryHash = QueryReader.Hash(q);
            ulong key = q.Search.Key ?? ScanPlan.KeyFromHash(s.QueryHash);
            s.Plan = planOverride
                     ?? new ScanPlan(q.Search.Order, q.Search.From, q.Search.To, key, q.Search.BlockSize, seedBudget);

            string? path = outPath ?? q.Output.Path;
            s.Output = new OutputPolicy
            {
                Path = path,
                Format = path != null ? ResultWriter.FormatFor(path) : ResultFormat.Jsonl,
                KeepAll = q.Search.KeepAll,
                Keep = q.Search.Keep,
                RotateBytes = q.Output.RotateBytes,
                Compress = q.Output.Compress,
                Reduce = ReduceSpec.Parse(q.Output.Reduce),
                MaxBytes = q.Output.MaxBytes,
                OnLimit = q.Output.OnLimit == "evict" ? OnLimit.Evict : OnLimit.Stop,
            };

            // keep: all with a big range and no rotation is the one configuration that can fill a
            // disk. Rotation is the decided answer, so it is turned on by default rather than left
            // for the user to remember - and the plan says so.
            if (s.Output.KeepAll && s.Output.RotateBytes <= 0 && s.Output.Path != null
                && s.Output.Format != ResultFormat.Json && s.Plan.Limit > 1_000_000)
            {
                s.Output.RotateBytes = OutputPolicy.DefaultRotateBytes;
            }

            // An unbounded run with no ceiling of its own gets one: 90 % of the free space on the
            // output volume. "Warn and keep writing until the disk fills" was never an option
            // (decision 2) - at the ceiling the run stops cleanly, flushes, checkpoints and prints the
            // command that continues it somewhere with more room.
            if (s.Output.KeepAll && s.Output.MaxBytes <= 0 && s.Output.Path != null)
            {
                long free = CheckpointStore.FreeBytes(s.Output.Path);
                if (free > 0) s.Output.MaxBytes = (long)(free * 0.9);
            }

            // The oracle goes through so the feasibility checker can see the dumped ZoneLocation
            // rows: without it every location goal is 'unavailable' and the whole REFUSE tier for
            // bosses, traders and dungeons is silently unreachable - which is the defect this wiring
            // exists to close.
            s.Preflight = SearchPreflightCheck.Check(q, s.Compiled, s.Plan, s.Output, s.Threads,
                                                     acceptScanOrder, oracle, allowVacuous);
            s.Grid = s.Preflight.Grid;
            foreach (string u in s.Grid.UnsafeMusts) s.Preflight.Warnings.Add(u);
            foreach (string w in s.Compiled.Warnings) s.Preflight.Warnings.Add(w);

            // Said out loud, never silently: a user who wrote "the elder" and got a run about GDKing is
            // entitled to see the substitution and the rule that made it, because the results file, the
            // hash and every column header will say GDKing and nothing else ever will say "the elder".
            s.NameNotes = nameNotes;
            foreach (string n in nameNotes) s.Preflight.Warnings.Add(n);

            // Audit mode measures every goal over the whole world with no shortcut of any kind, and a
            // screening pass is a shortcut - a sound one, but an empirical one. It is exactly what an
            // audit run exists to be compared against, so it is off here.
            if (noPrefilter && s.Grid.ScreenThenVerify)
            {
                s.Grid.ScreenGrid = 0;
                s.Grid.Notes.Add("--no-prefilter: the coarse screen is off, every seed is measured exactly");
            }

            if (s.Grid.ScreenThenVerify)
            {
                Query screenQuery = ScreenThenVerify.ScreenQuery(q, s.Grid, s.Compiled);
                if (!ReferenceEquals(screenQuery, q))
                {
                    s.Screen = CompiledQuery.Compile(screenQuery, oracle, noPrefilter);
                }
                else
                {
                    s.Grid.ScreenGrid = 0;
                }
            }

            // ---- make the grid raise REAL, not only announced -----------------------------------
            //
            // GridPolicy raises the grid to the game's own 12 m when a must-have goal is one no coarse
            // grid measures safely, and the preflight turns that into a confirmation the user accepts.
            // But the query was COMPILED above, before the grid decision existed, and nothing recompiled
            // it - so the run announced G12, made the user confirm G12, and then measured at G96.
            //
            // That is worse than not raising at all, because the user was told otherwise. It was fixed
            // once in the CLI as a local workaround whose own comment said the fix belonged here; the
            // web UI went through this method and never got it, and shipped G96 numbers under a G12
            // label (measured 2026-09-23: a seed streamed as a 6.41 km2 match that is 5.87 km2 at G12,
            // and another overstated by 28 %). Doing it HERE is what makes the two front ends agree.
            //
            // One level of recursion only: the rebuilt session is already at the verify grid, so its
            // own policy has nothing left to raise and `alreadyRaised` is belt and braces.
            if (!alreadyRaised && s.Grid.RaisedFrom > 0 && planOverride == null)
            {
                double from = s.Grid.RaisedFrom;
                var notes = new List<string>(s.Grid.Notes);
                var unsafeMusts = new List<string>(s.Grid.UnsafeMusts);
                var confirmations = new List<string>(s.Preflight.Confirmations);

                q.Search.Grid = s.Grid.VerifyGrid;
                q.CanonicalJson = QueryReader.Canonicalise(q);

                SearchSession raised = Create(q, oracle, engineVersion, seedBudget, threads, outPath,
                                              noPrefilter, acceptScanOrder, allowVacuous, null, true);
                raised.Grid.RaisedFrom = from;

                // A carried note goes into the PLAN too, directly under the grid line. The raised
                // session's own Check turned its Grid.Notes into plan lines before these were added,
                // so until 2026-09-24 the note that explains the raise ("grid raised from G96 to the
                // game's own G12, because ...") reached Grid.Notes and nothing else: neither the CLI's
                // plan block nor the web page, which both print Preflight.Plan, ever showed it.
                //
                // And at the FRONT of Grid.Notes, in the same order: explain prints Grid.Notes as they
                // stand (and the web's preflight report carries the list), so appending them put "grid
                // raised from G96 ..." after the raised session's own note in explain and before it in
                // the plan block.
                int at = raised.Preflight.Plan.FindIndex(l => l.StartsWith("grid         ", StringComparison.Ordinal));
                at = at < 0 ? raised.Preflight.Plan.Count : at + 1;
                int front = 0;
                foreach (string n in notes)
                {
                    if (raised.Grid.Notes.Contains(n)) continue;
                    raised.Grid.Notes.Insert(front++, n);
                    raised.Preflight.Plan.Insert(at++, "             " + n);
                }

                foreach (string u in unsafeMusts)
                {
                    if (!raised.Grid.UnsafeMusts.Contains(u)) raised.Grid.UnsafeMusts.Add(u);
                }

                // Anything the first preflight wanted confirmed that the second no longer raises is by
                // construction about the raise itself. Re-added rather than re-worded, so the sentence
                // the user accepts is the one the policy wrote.
                foreach (string c in confirmations)
                {
                    if (!raised.Preflight.Confirmations.Contains(c)) raised.Preflight.Confirmations.Add(c);
                }

                // The name rewrite is IDEMPOTENT, which is exactly why its notes have to be carried
                // across rather than recomputed: the recursive Create above is handed a query whose
                // targets are already prefabs, so it rewrites nothing and says nothing - and the
                // substitution the user needs to see would vanish on any query that raised its grid.
                foreach (string n in nameNotes)
                {
                    if (raised.NameNotes.Contains(n)) continue;
                    raised.NameNotes.Add(n);
                    raised.Preflight.Warnings.Add(n);
                }

                raised.Preflight.Plan.Add("             the grid above is the RAISED one: this run really "
                                          + "is compiled and measured at G"
                                          + raised.Grid.VerifyGrid.ToString("0.###", CultureInfo.InvariantCulture)
                                          + ", not only described as it");
                return raised;
            }

            s.CheckpointPath = CheckpointStore.DefaultPath(s.QueryHash);
            return s;
        }

        /// <summary>
        /// Turns every <c>location:</c> target the user spelled as a player-facing name into the
        /// prefab it means, in place, and returns one sentence per substitution.
        ///
        /// <para><b>Idempotent, by construction.</b> An exact prefab spelling resolves to itself, so
        /// the <c>!=</c> guard below leaves the goal untouched, nothing is re-canonicalised and no note
        /// is produced. Running this twice therefore changes neither the query nor the hash, which is
        /// what lets <see cref="Create"/> re-enter itself for the grid raise and what keeps a results
        /// file written before this feature existed comparable with one written after it.</para>
        ///
        /// <para><b>Prefabs only, and only when the oracle can say.</b> A <c>group:</c> name is its own
        /// vocabulary and is never a location name, so it is left alone. When the oracle is
        /// unavailable its null means "cannot say" rather than "no such place"
        /// (<see cref="ILocationOracle.ResolveLocationName"/>), and a rewrite on that answer would
        /// replace the build's honest "needs the dumped location table" refusal with a false "no such
        /// place" - so the typed string is kept and the existing refusal fires. A null from an
        /// AVAILABLE oracle is kept for the same reason from the other side: the unknown-prefab error
        /// in <c>CompiledQuery.ResolveTypes</c> is the message that names it and suggests spellings.</para>
        /// </summary>
        private static List<string> CanonicaliseLocationTargets(Query q, ILocationOracle oracle)
        {
            List<string> notes = new List<string>();
            if (!oracle.Available) return notes;

            bool changed = false;
            foreach (Goal g in q.Goals)
            {
                if (g.Target.Kind != TargetKind.Location) continue;

                LocationNameMatch? match = oracle.ResolveLocationName(g.Target.Name);
                if (match == null) continue;
                if (string.Equals(match.Prefab, g.Target.Name, StringComparison.Ordinal)) continue;

                g.Target = new GoalTarget(TargetKind.Location, match.Prefab);
                changed = true;
                notes.Add("goal '" + g.Id + "': '" + match.AsTyped + "' is the " + match.How
                          + " of " + match.Prefab + ", so this run is about " + match.Prefab
                          + " - which is the spelling the run hash, the results file and every column "
                          + "header will use"
                          + (match.Note != null ? ". " + match.Note : ""));
            }

            // The canonical JSON is what Hash() reads when it is already filled in, and the parser
            // fills it in - so a rewrite that did not refresh it would hash the string the user typed
            // while running the prefab it means. Same call, and the same reason, as the grid raise
            // above makes after it changes q.Search.Grid.
            if (changed) q.CanonicalJson = QueryReader.Canonicalise(q);
            return notes;
        }

        /// <summary>
        /// Opens the run: cleans orphaned temp files, loads or creates the checkpoint, repairs a torn
        /// results tail, restores a bounded run's kept set, and builds the sink.
        /// </summary>
        public IResultSink? Start(bool resume, string? checkpointPath = null)
        {
            if (checkpointPath != null) CheckpointPath = checkpointPath;
            CleanedOrphans = CheckpointStore.CleanOrphans(CheckpointPath);

            long resumeLength = -1;
            _checkpoint = new Checkpoint
            {
                Engine = _engine,
                QueryHash = QueryHash,
                Defs = Query.Defs,
                Grid = Query.Search.Grid,
                Order = Plan.Order.ToString().ToLowerInvariant(),
                Key = Plan.Key,
                From = Plan.From,
                To = Plan.To,
                BlockSize = Plan.BlockSize,
                Limit = Plan.Limit,
            };

            if (resume && File.Exists(CheckpointPath))
            {
                Checkpoint c = Checkpoint.Load(CheckpointPath);
                c.MustMatch(Query, Plan, QueryHash);
                _checkpoint = c;
                _startBlock = c.NextBlock;
                _resumedSeeds = c.SeedsEvaluated;
                _resumedPassed = c.SeedsPassed;
                _resumedSeconds = c.ElapsedSeconds;
                resumeLength = c.ResultsLength;

                // The torn tail. A hard kill writes complete records after the checkpoint and then
                // usually half of one more; every byte beyond results_length is produced again by
                // this leg, so keeping them would duplicate them and keeping the half would leave a
                // line no reader can parse.
                // Only for the single-file sinks: a rotated run's base path is a name, not a file
                // (the records live in results.0001.jsonl.gz and so on), so there is nothing here to
                // truncate - and ResultSinks.Create refuses that resume outright.
                if (Output.Path != null && resumeLength > 0 && !Output.IsRotating)
                {
                    RepairedBytes = CheckpointStore.TruncateTorn(Output.Path, resumeLength);
                }

                if (!Output.KeepAll && c.KeptSnapshot != null && File.Exists(c.KeptSnapshot))
                {
                    _restored = BoundedResultSet.LoadSnapshot(c.KeptSnapshot, Output.Keep);
                }
                else if (!Output.KeepAll && Output.Path != null && c.SeedsPassed > 0)
                {
                    throw new InvalidOperationException(
                        "cannot resume this bounded run: the checkpoint at " + CheckpointPath
                        + " has no kept-results snapshot beside it, so the best records found before the "
                        + "interruption are not recoverable and this leg would report a top-N built only "
                        + "from the tail of the scan. Delete the checkpoint to run the query again from "
                        + "the beginning.");
                }
            }

            if (Output.Path == null) return null;

            IResultSink sink = ResultSinks.Create(Output, Query, Compiled, _engine, resumeLength);
            if (sink is BoundedResultSink b)
            {
                b.SnapshotPath = CheckpointStore.SnapshotPathFor(CheckpointPath);
                if (_restored != null) b.Restore(_restored);
            }

            return sink;
        }

        /// <summary>Runs the scan with everything this session decided.</summary>
        public SearchOutcome Run(IResultSink? sink, Action<Progress>? onProgress, TimeSpan wall,
                                 TimeSpan checkpointEvery, Action<SearchRun>? configure = null)
        {
            SearchRun run = new SearchRun(Compiled, Plan, _oracle, Threads);
            if (Screen != null)
            {
                CompiledQuery screen = Screen;
                CompiledQuery verify = Compiled;
                ILocationOracle oracle = _oracle;
                run.EvaluatorFactory = () => new TwoStageEvaluator(screen, verify, oracle);
            }

            configure?.Invoke(run);

            return run.Run(_startBlock, new RunOptions
            {
                Sink = sink,
                Checkpoint = _checkpoint,
                Checkpoints = new CheckpointPolicy
                {
                    Path = CheckpointPath,
                    Interval = checkpointEvery,
                    MinRunTime = TimeSpan.Zero,
                    SnapshotPath = Output.KeepAll ? null : CheckpointStore.SnapshotPathFor(CheckpointPath),
                    DeleteOnComplete = true,
                },
                Wall = wall,
                OnProgress = onProgress,
                Estimator = Estimator,
                MaxResultBytes = Output.MaxBytes,
                OnLimit = Output.OnLimit,
                ResumedEvaluated = _resumedSeeds,
                ResumedPassed = _resumedPassed,
                ResumedSeconds = _resumedSeconds,
            });
        }

        /// <summary>The line a report must print about the results, never "N matches" when N was capped.</summary>
        public string ResultLine(SearchOutcome outcome)
        {
            if (!outcome.Bounded)
            {
                return outcome.ResultsWritten.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                       + " matches written";
            }

            string kept = outcome.ResultsWritten.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            string total = outcome.Passed.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            if (outcome.ResultsDropped <= 0) return kept + " matches (all of them; the cap was not reached)";
            return "top " + kept + " of " + total + " matches"
                   + (double.IsNaN(outcome.FirstDropScore)
                       ? ""
                       : "; " + outcome.ResultsDropped.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                         + " were not kept, from the point where the cut line was "
                         + outcome.FirstDropScore.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                         + " (it ended at "
                         + outcome.CutScore.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ")");
        }
    }
}
