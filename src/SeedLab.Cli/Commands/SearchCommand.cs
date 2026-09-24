using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using SeedLab.Cli.Infra;
using SeedLab.Runtime.Execution;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Execution;
using SeedLab.Search.Locations;
using SeedLab.Search.Output;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed search</c> - the half of the tool the websites cap and this one does not.
    ///
    /// <para>Its contract with the user is that every number it prints is measured on this machine and
    /// every rejection it makes is provable. It prints the fraction of the 4,294,967,296-world space
    /// the run will actually cover <b>before it starts</b>, the measured rate while it runs, and what
    /// the tiers cost when it finishes - because a search tool that implies it has seen the whole space
    /// is worse than no search tool.</para>
    /// </summary>
    public static class SearchCommand
    {
        public const string Help = @"vseed search <query.json | preset-name> [options]

  Scans the int32 seed space against a declarative query and writes the best matches to disk.

query
  <file.json>            a query file (see --schema)
  <preset-name>          one of the shipped presets (see 'vseed presets list')

range and budget
  --seeds <n>            evaluate n seeds (default: the query's budget, else 10000)
  --all                  evaluate the whole range - say 4294967296 seeds out loud before you do
  --from <int32>         first seed of the range (default -2147483648)
  --to <int32>           last seed of the range (default 2147483647)
  --budget <dur>         stop after 90s / 45m / 8h / 2d; a wall-limited run is not reproducible
                         on its own - its checkpoint records what to pass to --seeds so it is

how
  --grid <m>             override the query's sampling grid, metres (12 is the game's own)
  --strategy auto|funnel|sample
                         auto (default) picks per query and says why.
                         funnel   measure the cheap must-have goals over the whole range FIRST,
                                  then place locations only for the seeds that survived - and
                                  report the survivor count and the cost of that second stage
                                  before it starts.
                                  (The per-seed ladder already skips placement for a seed a
                                  cheap goal has settled; what this adds is the MEASURED count
                                  before the expensive work, not extra speed.)
                         sample   evaluate n seeds drawn without repetition. Never exhaustive,
                                  and the coverage of the whole space is always reported.
  --order shuffled|sequential
  --key <0x...>          the Feistel key; same key + same range = the same seed sequence
  --threads <n>          workers (default: every logical core)
  --block-size <n>       seeds per work block; checkpoints land on block boundaries
  --no-prefilter         audit mode: no T0 rejection, no region restriction, no early exit.
                         Must produce the same result set as a normal run - that is the test.
  --approx               allow HEURISTIC prefilters. This build ships none.
  --skip-unavailable     drop nice-to-have goals that need the dumped location table
                         (a must-have that needs it is always a hard error)

  --region <m>           measure only inside this disc from the world centre. EXACT for a
                         goal bounded by it, and the largest cost lever there is.
  --screen auto|on|off   screen-then-verify: measure coarsely, then re-measure every
                         survivor at the definitional grid (default auto)
  --screen-grid <m>      the coarse screening grid (default: chosen from the goals)

output
  --out <file>           .jsonl (default), .json or .csv
  --keep <n|all>         A REAL CAP ON THE FILE: only the best n records are ever written
                         (default 1000, or the query's search.keep). The run still reports
                         the TRUE match count - 'top 1000 of 154,145'. '--keep all' streams
                         every match instead, and wants --rotate on a large range.
  --rotate <size>        with --keep all: close a segment every <size> (e.g. 1GB) and write
                         a manifest. Line-oriented formats only - a split JSON array is not
                         valid JSON and is refused.
  --compress gz|none     gzip each closed segment (default gz when rotating)
  --reduce top:N|count   reduce a segment when it closes and DELETE the raw one. Only
                         reductions that merge exactly across segments are accepted.
  --max-bytes <size>     a hard ceiling on total result bytes
  --on-limit stop|evict  at the ceiling: stop cleanly (default) or keep going and drop the
                         lowest-scoring records (bounded runs only, and it is confirmed)
  --resume               continue from the checkpoint
  --checkpoint <file>    checkpoint path (default: one per query hash, in the cache root)
  --checkpoint-every <s> seconds between checkpoints (default 30)
  --progress tty|none    live progress on stderr (default tty when stderr is a console)

other
  --dry-run              plan, static analysis and a MEASURED per-seed cost, then stop
  --calibrate <n>        seeds to time for --dry-run (default 8)
  --yes                  answer yes to every confirmation the plan raises
  --accept-scan-order    accept an arbitrary first-N when a query has nothing to rank by
  --schema               print the criteria-language schema
  --metrics              print every target/metric with the tier that answers it
  --json                 machine-readable summary on stdout

Before EVERY run - not only --dry-run - the plan block says what will be scanned, what it
will cost, where the results go and how big they can get. A run that cannot be answered
honestly is REFUSED with the fix named; one that is expensive or surprising asks first.

Ctrl-C stops at the next block boundary, writes the checkpoint and exits 0.";

        public static int Run(Args a, Out o, CliRuntime rt)
        {
            if (a.Flag("schema"))
            {
                a.RejectUnknown();
                Console.Out.Write(Presets.Schema());
                return ExitCodes.Ok;
            }

            if (a.Flag("metrics"))
            {
                a.RejectUnknown();
                PrintMetrics(o);
                o.Flush();
                return ExitCodes.Ok;
            }

            if (a.Positional.Count < 1)
            {
                throw new CliException("a query file or preset name is required.", ExitCodes.Usage,
                    "try 'vseed presets list', or 'vseed search --schema' for the query language");
            }

            Query q = LoadQuery(a.Positional[0]);
            ApplyOverrides(q, a);

            ILocationOracle oracle = Oracle(out string? oracleProblem);
            bool skipUnavailable = a.Flag("skip-unavailable");
            bool noPrefilter = a.Flag("no-prefilter");
            bool dryRun = a.Flag("dry-run");
            int calibrate = a.Int("calibrate", 8);
            string? outPath = a.Get("out") ?? q.Output.Path;
            bool resume = a.Flag("resume");
            string? ckptPath = a.Get("checkpoint");
            double ckptEvery = a.Double("checkpoint-every", 30.0);
            string progressMode = a.Get("progress") ?? (Console.IsErrorRedirected ? "none" : "tty");
            SearchStrategy strategy = ParseStrategy(a.Get("strategy"));
            bool all = a.Flag("all");
            if (all && a.Has("seeds"))
            {
                // Reading one and ignoring the other would silently run a different search than the
                // one that was asked for, and the leftover-option check would blame '--seeds'.
                throw new CliException("--all and --seeds ask for different runs.", ExitCodes.Usage,
                    "--all means the whole 4,294,967,296-seed range; drop one of the two");
            }

            long seedBudget = all ? 0 : a.Int("seeds", (int)Math.Min(int.MaxValue, DefaultBudget(q)));
            if (!all && a.Has("seeds") && seedBudget <= 0)
            {
                // ScanPlan reads "budget <= 0" as "the whole range", so '--seeds 0' would silently start
                // the same 4,294,967,296-seed run that --all makes the user ask for out loud. Ask.
                throw new CliException("--seeds must be at least 1.", ExitCodes.Usage,
                    "--all is how you ask for the whole 4,294,967,296-seed range");
            }

            TimeSpan wall = a.Get("budget") != null
                ? QueryReader.Duration(a.Require("budget"), "--budget")
                : q.Search.Wall;
            bool yes = a.Flag("yes");
            bool acceptScanOrder = a.Flag("accept-scan-order");

            // Everything the command reads has been read by now, so anything left over is a typo - and
            // a silently dropped option is how a run ends up being a different run than the user asked
            // for.
            a.RejectUnknown();

            CompiledQuery cq;
            try
            {
                cq = CompiledQuery.Compile(q, oracle, noPrefilter);
            }
            catch (QueryException ex)
            {
                throw new CliException(ex.Message, ExitCodes.Usage, ex.Hint);
            }

            // ---- refuse to run a query whose answer would be a lie -------------------------------
            if (cq.Unavailable.Count > 0)
            {
                List<CompiledGoal> blocking = new List<CompiledGoal>();
                foreach (CompiledGoal g in cq.Unavailable)
                {
                    if (g.Goal.Importance == Importance.Must || !skipUnavailable) blocking.Add(g);
                }

                if (blocking.Count > 0)
                {
                    Console.Error.WriteLine("vseed search: this query cannot be answered by this build.");
                    foreach (CompiledGoal g in blocking)
                    {
                        Console.Error.WriteLine("  " + g.Goal.Id + " (" + g.Goal.Target + "." + g.Goal.Metric + ")");
                        Console.Error.WriteLine("      " + g.UnavailableReason);
                    }

                    Console.Error.WriteLine();
                    if (oracleProblem != null)
                    {
                        Console.Error.WriteLine("  The location table was found but could not be used:");
                        Console.Error.WriteLine("  " + oracleProblem.Replace("\n", "\n  "));
                        Console.Error.WriteLine();
                    }

                    Console.Error.WriteLine("  Nothing is returned rather than seeds that were never tested against these goals.");
                    Console.Error.WriteLine("  Run tools\\SeedLab.Dumper once in game to capture the table, or drop the goals.");
                    if (skipUnavailable)
                    {
                        Console.Error.WriteLine("  (--skip-unavailable drops nice-to-have goals only; these are must-haves.)");
                    }

                    return ExitCodes.CheckFailed;
                }

                // Only nice-to-haves were unavailable and the user asked for them to be dropped.
                List<Goal> kept = new List<Goal>();
                foreach (Goal g in q.Goals)
                {
                    bool drop = false;
                    foreach (CompiledGoal u in cq.Unavailable)
                    {
                        if (ReferenceEquals(u.Goal, g)) drop = true;
                    }

                    if (drop) Out.Warn("dropping nice-to-have goal '" + g.Id + "': " + oracle.UnavailableReason);
                    else kept.Add(g);
                }

                q.Goals = kept;
                if (q.Goals.Count == 0) throw new CliException("every goal needed the dumped location table.", ExitCodes.CheckFailed);
                q.CanonicalJson = QueryReader.Canonicalise(q);
                cq = CompiledQuery.Compile(q, oracle, noPrefilter);
            }

            foreach (CompiledGoal g in cq.Unsatisfiable)
            {
                if (g.Goal.Importance == Importance.Must)
                {
                    Console.Error.WriteLine("vseed search: goal '" + g.Goal.Id + "' can never be satisfied by any seed.");
                    Console.Error.WriteLine("  " + g.Unsatisfiable);
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("  Refusing to scan 4.29 billion worlds for something the generator cannot make.");
                    return ExitCodes.CheckFailed;
                }

                Out.Warn("goal '" + g.Goal.Id + "' can never be satisfied: " + g.Unsatisfiable
                         + " - it is a nice-to-have, so it will always score 0");
            }

            // ---- the stamp, on a run that is going ahead -------------------------------------------
            //
            // DataPolicy has two halves and only the refusing one was ever wired up: WarningForTerrain
            // had no callers anywhere in the repository. Locations are refused above; terrain is
            // generated from the seed and the generator alone, reads no dumped asset, and is therefore
            // ANSWERED - with the mismatch stated once. This is the once. It sits after the refusal
            // path has returned, so a refused query does not say it twice, and it goes to stderr so
            // --json's stdout stays machine-readable. Console.Error rather than Out.Warn because the
            // text already begins with "warning: " and Out.Warn would prepend a second one.
            string? stampWarning = SeedLab.Data.DataPolicy.WarningForTerrain();
            if (stampWarning != null) Console.Error.WriteLine(stampWarning);

            // ---- the session: the grid decision, the output policy and the preflight -------------
            //
            // EVERYTHING below this line used to be assembled here, by hand, differently from the way
            // the web UI assembled it - which is how --keep came to cap a display list while the file
            // took every hit, how the refusal rule came to be implemented and never called, and how
            // screen-then-verify came to exist with no front end able to reach it. SearchSession is
            // the one assembly of those parts; the CLI's job is now to print what it decided, ask
            // where it says to ask, and stop where it says to stop.
            //
            // The worker count is planned twice on purpose. The first plan sizes against the grid the
            // QUERY asked for, because the session needs a thread count to build its plan with; the
            // session may then RAISE the grid (a must-have goal a coarse grid measures wrongly is
            // moved to the game's own 12 m), and a 12 m worker holds far more than a 384 m one. So the
            // plan is redone against the grid that will actually run, and that is the number printed
            // and used.
            WorkTier tier = TierOf(cq);
            WorkerPlan plan0 = rt.Plan(tier, q.Search.Grid);
            int threads = plan0.Workers;

            SearchSession session;
            try
            {
                session = SearchSession.Create(q, oracle, Verified.EngineVersion, seedBudget, threads,
                                               outPath, noPrefilter, acceptScanOrder);
            }
            catch (QueryException ex)
            {
                throw new CliException(ex.Message, ExitCodes.Usage, ex.Hint);
            }

            // The raise now happens inside SearchSession.Create, so that the web UI gets it too -
            // it went through the same method and never did, and shipped G96 numbers under a G12
            // label. All that is left here is to take the raised query and its compilation BACK from
            // the session: this method's own locals are the pre-raise ones, and the plan block, the
            // goal table and the funnel planner all read them.
            q = session.Query;
            cq = session.Compiled;

            WorkerPlan workers = rt.Plan(tier, session.Grid.VerifyGrid);
            session.Threads = workers.Workers;

            // The checkpoint goes in the cache root, one file per query hash - not 'vseed-search.ckpt'
            // in whatever directory the user happened to be standing in (audit defect 5). Two searches
            // running side by side therefore cannot overwrite each other's resume point, and --cache-dir
            // moves all of it together.
            string checkpointPath = ckptPath
                ?? Path.Combine(rt.Cache.Checkpoints, Short(session.QueryHash) + ".ckpt");
            session.CheckpointPath = checkpointPath;

            ScanPlan plan = session.Plan;

            // ---- the plan block, before EVERY run (decision 10) -----------------------------------
            if (!o.Json)
            {
                rt.PrintStartup("Machine");
                foreach (string line in workers.Lines()) Console.Error.WriteLine("  " + line);
                Console.Error.WriteLine("  checkpoint  " + checkpointPath);
            }

            PrintPlan(o, q, cq, plan, session.Threads, outPath, noPrefilter);
            PrintPreflight(o, session, checkpointPath);

            // ---- refusals: the run must not start --------------------------------------------------
            if (!session.Preflight.Ok)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("vseed search: REFUSED - this run would not answer the question honestly.");
                foreach (string r in session.Preflight.Refusals)
                {
                    Console.Error.WriteLine("  " + Wrap(r, "      "));
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine("  Nothing was scanned and nothing was written.");
                return ExitCodes.CheckFailed;
            }

            // ---- warnings: it runs, but the user has to know ----------------------------------------
            foreach (string w in session.Preflight.Warnings) Out.Warn(Wrap(w, "         "));

            // ---- --dry-run: the cost, and what the real run WOULD ask ----------------------------
            //
            // Deliberately ahead of the confirmation gate. --dry-run exists to find out what a run
            // would cost before committing to it, and a script that asks that question has stdin
            // redirected - so gating it behind a y/N would have made "vseed search <preset> --all
            // --dry-run" exit 1 with "pass --yes" instead of answering. The confirmations are still
            // PRINTED here, as the list the real run will stop on. Refusals are not treated this way
            // and still stop a dry run: they say the run cannot be answered honestly at all, and
            // timing it would be timing something that is never going to happen.
            if (dryRun)
            {
                if (session.Preflight.Confirmations.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Without --dry-run, this run would stop and ask about:");
                    foreach (string c in session.Preflight.Confirmations)
                    {
                        Console.Error.WriteLine("  - " + Wrap(c, "    "));
                    }

                    Console.Error.WriteLine("  (--yes accepts them all.)");
                }

                Calibrate(o, cq, oracle, plan, session.Threads, calibrate);
                o.Flush();
                return ExitCodes.Ok;
            }

            // ---- confirmations: it runs after a yes --------------------------------------------------
            if (session.Preflight.Confirmations.Count > 0 && !yes)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("This run needs confirming:");
                foreach (string c in session.Preflight.Confirmations)
                {
                    Console.Error.WriteLine("  - " + Wrap(c, "    "));
                }

                // A prompt nobody can answer is a hang, so a non-interactive run (a script, a pipe,
                // --json) is REFUSED here rather than asked. The refusal names the flag that means
                // "I have read the list above": that is a decision the user makes, not one a default
                // makes for them.
                if (o.Json || Console.IsInputRedirected)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("  Nothing is reading the keyboard"
                                            + (o.Json ? " (--json)" : " (stdin is redirected)")
                                            + ", so this run is not started.");
                    Console.Error.WriteLine("  Pass --yes to accept the points above, or change the query.");
                    Console.Error.WriteLine("  --dry-run answers \"what would this cost\" without starting it.");
                    return ExitCodes.CheckFailed;
                }

                Console.Error.Write("continue? [y/N] ");
                string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (answer != "y" && answer != "yes")
                {
                    Console.Error.WriteLine("not started.");
                    return ExitCodes.CheckFailed;
                }
            }

            // ---- Ctrl-C, installed BEFORE anything long-running ---------------------------------------
            //
            // It used to be installed after the strategy block, which meant a funnel's FIRST STAGE ran
            // with no handler: Ctrl-C killed the process outright (STATUS_CONTROL_C_EXIT), the
            // checkpoint the plan block had just named was never written, and every second of stage one
            // was lost. Measured 2026-09-23 on a 6,000-seed funnel: 19.7 CPU-seconds gone, an empty
            // checkpoint directory, and no resume point of any kind. The handler now covers every phase
            // that can run long, and `running` is re-pointed at whichever SearchRun is live.
            SearchRun? running = null;
            ConsoleCancelEventHandler cancel = (_, e) =>
            {
                e.Cancel = true;
                Out.Info("");
                Out.Info("stopping at the next block boundary and writing the checkpoint...");
                running?.RequestStop();
            };
            Console.CancelKeyPress += cancel;

            // ---- strategy: which of the two shapes this run takes, and why ---------------------------
            FunnelPlan funnel = FunnelPlan.For(q, cq);
            SearchStrategy chosen = strategy;
            string strategyWhy;
            if (strategy == SearchStrategy.Auto)
            {
                chosen = FunnelPlan.Decide(funnel, out strategyWhy);
            }
            else if (strategy == SearchStrategy.Funnel && !funnel.Usable)
            {
                // Asked for, but it cannot apply. Saying so and running the ordinary way is better
                // than either silently ignoring the flag or refusing a run that is perfectly valid.
                chosen = SearchStrategy.Sample;
                strategyWhy = "sample: --strategy funnel was asked for, but " + funnel.Reason;
            }
            else
            {
                strategyWhy = (chosen == SearchStrategy.Funnel ? "funnel: " : "sample: ") + funnel.Reason;
            }

            o.Header("Strategy");
            o.Field("chosen", chosen.ToString().ToLowerInvariant()
                              + (strategy == SearchStrategy.Auto ? "  (auto)" : "  (asked for)"));
            o.Note("  " + Wrap(strategyWhy, "  "));
            if (chosen == SearchStrategy.Sample)
            {
                o.Note("  This is a SAMPLE of the seed space, never a proof about it: "
                       + CoverageLine(plan));
            }

            o.Flush();

            if (chosen == SearchStrategy.Funnel)
            {
                string survivorPath = checkpointPath.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase)
                    ? checkpointPath.Substring(0, checkpointPath.Length - 5) + ".survivors"
                    : checkpointPath + ".survivors";

                int[]? survivors = RunStageOne(o, q, funnel, oracle, plan, session, seedBudget,
                                               survivorPath, resume, yes, progressMode,
                                               run => running = run, out int stageExit);
                if (survivors == null) return stageExit;

                // Stage two is the SAME session code over a different bijection - the survivor list -
                // so every guarantee the ordinary path has (checkpoint, resume, bounded sink, torn-tail
                // repair) applies to it without a second implementation.
                try
                {
                    session = SearchSession.Create(q, oracle, Verified.EngineVersion, seedBudget,
                                                   session.Threads, outPath, noPrefilter, acceptScanOrder,
                                                   false, ScanPlan.OverSeeds(survivors, q.Search.BlockSize));
                }
                catch (QueryException ex)
                {
                    throw new CliException(ex.Message, ExitCodes.Usage, ex.Hint);
                }

                plan = session.Plan;
                checkpointPath = session.CheckpointPath;
            }

            // ---- open the run: orphan cleanup, resume, torn-tail repair, the sink -------------------
            bool hadCheckpoint = File.Exists(checkpointPath);
            IResultSink? sink;
            try
            {
                sink = session.Start(resume, checkpointPath);
            }
            catch (InvalidOperationException ex)
            {
                throw new CliException(ex.Message, ExitCodes.CheckFailed,
                    "delete " + checkpointPath + " to start this query from the beginning");
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException
                                       or FormatException)
            {
                throw new CliException("the checkpoint at " + checkpointPath + " could not be read ("
                                       + ex.Message + ").", ExitCodes.CheckFailed,
                    "delete it to start this query from the beginning");
            }

            if (resume && !hadCheckpoint)
            {
                Out.Warn("no checkpoint at " + checkpointPath + "; starting from the beginning");
            }

            if (resume && hadCheckpoint)
            {
                Out.Info("resuming from " + checkpointPath
                         + ": the seeds an earlier leg finished are not evaluated again, and this leg's "
                         + "totals include them");
            }

            foreach (string orphan in session.CleanedOrphans)
            {
                Out.Info("removed the orphaned temp file " + orphan + " (a kill during a checkpoint save)");
            }

            if (session.RepairedBytes > 0)
            {
                Out.Info("repaired a torn results tail: " + Out.N(session.RepairedBytes)
                         + " B written past the checkpoint were discarded, because this leg produces them again");
            }

            long seedsMeasured = 0;
            int slicesSeen = 0;
            Action<Progress>? onProgress = progressMode == "none"
                ? null
                : p =>
                {
                    PrintProgress(p);
                    // The estimate is re-projected from what THIS run has measured, once per slice, so
                    // the number the user is watching sharpens as the hit rate becomes known instead of
                    // staying the guess the plan opened with.
                    if (session.Estimator.SeedsMeasured > seedsMeasured)
                    {
                        seedsMeasured = session.Estimator.SeedsMeasured;
                        RunEstimate e = session.Estimator.Project(
                            Math.Max(0, plan.Limit - p.Evaluated), p.Evaluated,
                            session.Output, cq.Goals.Count, outPath);
                        if (e.Slices > slicesSeen)
                        {
                            slicesSeen = e.Slices;
                            Console.Error.WriteLine();
                            Console.Error.WriteLine("  slice " + slicesSeen + ": " + e.WallText + " left, "
                                                    + e.MatchesText + " matches projected, " + e.BytesText
                                                    + " of output"
                                                    + (e.FitsOnDisk ? "" : "  - THAT DOES NOT FIT")
                                                    + "   [" + e.Basis + "]");
                        }
                    }
                };

            SearchOutcome outcome;
            try
            {
                outcome = session.Run(sink, onProgress, wall, TimeSpan.FromSeconds(ckptEvery),
                                      run => running = run);
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
                if (sink != null)
                {
                    sink.Finish();
                    sink.Dispose();
                }
            }

            if (progressMode != "none") Console.Error.WriteLine();
            Report(o, q, cq, plan, outcome, checkpointPath, session);
            o.Flush();
            return ExitCodes.Ok;
        }

        private static SearchStrategy ParseStrategy(string? v)
        {
            if (string.IsNullOrEmpty(v)) return SearchStrategy.Auto;
            switch (v!.Trim().ToLowerInvariant())
            {
                case "auto": return SearchStrategy.Auto;
                case "funnel": return SearchStrategy.Funnel;
                case "sample": return SearchStrategy.Sample;
                default:
                    throw new CliException("--strategy '" + v + "' is not one of auto, funnel, sample.",
                                           ExitCodes.Usage, "--strategy auto|funnel|sample");
            }
        }

        /// <summary>
        /// A funnel's first stage: the cheap must-have goals over the whole range, keeping only the
        /// seeds that survived.
        ///
        /// <para>Returns the survivors, or null when the run should stop - in which case
        /// <paramref name="exitCode"/> says with what. A survivor list from an earlier identical stage
        /// one is reused rather than recomputed, because recomputing it is the expensive half of the
        /// point of resuming.</para>
        /// </summary>
        private static int[]? RunStageOne(Out o, Query q, FunnelPlan funnel, ILocationOracle oracle,
                                          ScanPlan plan, SearchSession full, long seedBudget,
                                          string survivorPath, bool resume, bool yes,
                                          string progressMode, Action<SearchRun> publishRun,
                                          out int exitCode)
        {
            exitCode = ExitCodes.Ok;
            string stamp = oracle.Available ? oracle.Provenance : "";
            string qhash = full.QueryHash;

            int[]? survivors = null;
            SurvivorList.Header? head = null;
            double stageOneSeconds = -1;      // -1 = not run in this process; see the gate's wording
            if (resume && File.Exists(survivorPath))
            {
                try
                {
                    survivors = SurvivorList.Read(survivorPath, qhash, stamp,
                                                  q.Search.From, q.Search.To, plan.Limit,
                                                  out SurvivorList.Header h);
                    head = h;
                    Out.Info("stage 1 was already finished: reusing " + survivorPath + " ("
                             + h.Survivors.ToString("N0", CultureInfo.InvariantCulture) + " survivors of "
                             + h.Scanned.ToString("N0", CultureInfo.InvariantCulture) + " scanned)");
                }
                catch (Exception ex)
                {
                    // A survivor list that does not match is not a reason to guess - it is a reason to
                    // say which of the three identities disagreed and run stage one again.
                    Out.Warn(ex.Message);
                    survivors = null;
                }
            }

            if (survivors == null)
            {
                SearchSession one;
                try
                {
                    // planOverride is the FULL run's plan, and it is not an optimisation - it is the
                    // correctness condition. ScanPlan derives its Feistel key from the query hash when
                    // search.key is null, and the stage-one query is a different query with a
                    // different hash, so a stage one left to build its own plan walks a DIFFERENT
                    // sample of the space than the run it stands in for. Measured before this line
                    // existed: a 600-seed funnel and a 600-seed ordinary run of the same query shared
                    // not one seed, and the funnel's 14 matches were 14 real matches to a question
                    // nobody had asked. (Mutating one.Search.Key instead would edit the user's own
                    // query object, which q and the stage-one query share by reference.)
                    one = SearchSession.Create(funnel.StageOneQuery!, oracle, Verified.EngineVersion,
                                               seedBudget, full.Threads, null, false, true, true, plan);
                }
                catch (QueryException ex)
                {
                    throw new CliException(ex.Message, ExitCodes.Usage, ex.Hint);
                }

                o.Header("Funnel stage 1 - the cheap goals, over the whole range");
                o.Field("measuring", string.Join(", ", funnel.StageOneGoals));
                o.Field("deferring", string.Join(", ", funnel.DeferredGoals) + "  (until stage 2)");
                o.Field("seeds", one.Plan.Limit.ToString("N0", CultureInfo.InvariantCulture));
                o.Flush();

                SurvivorSink sink = new SurvivorSink();
                Action<Progress>? onProgress = progressMode == "none" ? null : p =>
                {
                    Console.Error.Write("\r  stage 1: "
                                        + p.Evaluated.ToString("N0", CultureInfo.InvariantCulture) + " / "
                                        + p.Limit.ToString("N0", CultureInfo.InvariantCulture)
                                        + " seeds | " + sink.Count.ToString("N0", CultureInfo.InvariantCulture)
                                        + " kept        ");
                };

                Stopwatch sw = Stopwatch.StartNew();
                SearchOutcome res;
                try
                {
                    // The handler installed before this block holds `running`; without publishing the
                    // stage-one run into it, Ctrl-C here would have nothing to ask to stop.
                    res = one.Run(sink, onProgress, TimeSpan.Zero, TimeSpan.FromSeconds(30),
                                  run => publishRun(run));
                }
                catch (InvalidOperationException ex)
                {
                    throw new CliException(ex.Message, ExitCodes.CheckFailed,
                                           "--strategy sample runs this query without a survivor list");
                }

                sw.Stop();
                stageOneSeconds = sw.Elapsed.TotalSeconds;
                if (progressMode != "none") Console.Error.WriteLine();

                // A stage one that was stopped early did not see the whole range, so its survivors are
                // the survivors of a PREFIX. Running stage two on them would place real worlds and
                // report them as the answer to a question that was never fully asked, and writing the
                // list would bank a partial result that a later --resume can only refuse (its Scanned
                // is below the budget). So the run stops here, cleanly, and says what it did and did
                // not do rather than quietly doing half of it.
                if (res.StoppedByUser || res.StoppedByWall)
                {
                    string why = res.StoppedByUser ? "you stopped it" : "the wall-clock budget ran out";
                    o.Header("Stopped during stage 1");
                    o.Field("scanned", Out.N(res.Evaluated) + " of "
                                       + Out.N(one.Plan.Limit) + " seeds before " + why);
                    o.Field("kept so far", Out.N(sink.Count) + " survivors");
                    o.Note("  Stage 2 has NOT run: these are the survivors of the seeds stage 1 reached,");
                    o.Note("  not of the range you asked for, and placing them would answer a narrower");
                    o.Note("  question than the one on the command line.");
                    o.Note("");
                    o.Note("  Nothing was written. Stage 1 has no resume point of its own in this build,");
                    o.Note("  so re-running repeats it - run a smaller --seeds if you want it to finish.");
                    o.Flush();
                    exitCode = ExitCodes.Ok;
                    return null;
                }

                survivors = new int[sink.Seeds.Count];
                for (int i = 0; i < survivors.Length; i++) survivors[i] = sink.Seeds[i];
                head = new SurvivorList.Header
                {
                    QueryHash = qhash,
                    Stamp = stamp,
                    From = q.Search.From,
                    To = q.Search.To,
                    Scanned = res.Evaluated,
                };

                try
                {
                    SurvivorList.Write(survivorPath, head, survivors, SurvivorList.DefaultMaxBytes);
                }
                catch (InvalidOperationException ex)
                {
                    throw new CliException(ex.Message, ExitCodes.CheckFailed,
                                           "--strategy sample runs this query without a survivor list");
                }
            }

            // ---- the measured gate ----------------------------------------------------------------
            FunnelGate gate = new FunnelGate
            {
                Scanned = head!.Scanned,
                Survivors = survivors.Length,
                Threads = full.Threads,
                BlockSize = q.Search.BlockSize,
                StageOneSeconds = stageOneSeconds,
                SecondsPerSurvivor = MeasurePlacement(full, oracle, survivors),
            };

            o.Header("Funnel gate - measured, before stage 2 places anything");
            foreach (string line in gate.Lines()) o.Note("  " + Wrap(line, "  "));
            o.Note("  (the per-seed figure is measured on a small sample of this query and scaled; "
                   + "everything above it is a count, not an estimate)");
            o.Flush();

            if (survivors.Length == 0)
            {
                o.Note("");
                o.Note("  Stage 1 kept no seeds at all, so there is nothing for stage 2 to place.");
                o.Flush();
                exitCode = ExitCodes.Ok;
                return null;
            }

            if (!yes && gate.StageTwoSeconds > 600)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Stage 2 is the expensive half and it has not started yet.");
                if (o.Json || Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("  Nothing is reading the keyboard, so it is not started.");
                    Console.Error.WriteLine("  Pass --yes to accept the cost above. The survivor list is "
                                            + "kept at " + survivorPath + ", so --resume skips stage 1.");
                    exitCode = ExitCodes.CheckFailed;
                    return null;
                }

                Console.Error.Write("run stage 2? [y/N] ");
                string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (answer != "y" && answer != "yes")
                {
                    Console.Error.WriteLine("not started. The survivor list is kept at " + survivorPath
                                            + "; --resume picks up from there without redoing stage 1.");
                    exitCode = ExitCodes.CheckFailed;
                    return null;
                }
            }

            return survivors;
        }

        /// <summary>
        /// Seconds the FULL query costs per seed, measured on SURVIVORS.
        ///
        /// <para><b>It must be survivors and nothing else.</b> A first version of this timed seeds
        /// drawn from the range, and reported <c>0.00 s each</c> for a stage two that then took 16.8 s:
        /// a seed from the range is overwhelmingly one the cheap must-have rejects, and the tier ladder
        /// rejects it without ever reaching placement. Timing that population measures the early exit,
        /// which is precisely the cost stage two does NOT pay. Survivors are by construction the seeds
        /// that reach the expensive tier, so they are the only honest sample.</para>
        ///
        /// <para>Single-threaded, and divided by the thread count at the point of use - the same way
        /// the rest of the tool reports a rate.</para>
        /// </summary>
        private static double MeasurePlacement(SearchSession full, ILocationOracle oracle, int[] survivors)
        {
            if (survivors.Length == 0) return 0.0;

            SeedEvaluator warm = new SeedEvaluator(full.Compiled, oracle);
            warm.Evaluate(survivors[0]);

            int n = Math.Min(4, survivors.Length);
            SeedEvaluator timed = new SeedEvaluator(full.Compiled, oracle);
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) timed.Evaluate(survivors[i]);
            sw.Stop();
            return sw.Elapsed.TotalSeconds / n;
        }

        /// <summary>
        /// The work tier a query puts a seed through, for the memory guard. It is a coarser question
        /// than <see cref="Tier"/>: what matters to a worker's footprint is whether it holds a grid
        /// (kilobytes to megabytes), the river pre-generation (50-60 MB) or the location placement's
        /// own 2048^2 point grid (~65 MB).
        /// </summary>
        private static WorkTier TierOf(CompiledQuery cq)
        {
            if (cq.Locations != null || cq.MaxTier >= Tier.T5)
            {
                return cq.Locations != null && cq.Locations.PrefixLength >= cq.Locations.OrderedCount
                    ? WorkTier.LocationsAll
                    : WorkTier.LocationsCore;
            }

            return cq.MaxTier >= Tier.T3 ? WorkTier.HeightsRivers : WorkTier.BiomeGrid;
        }

        private static string Short(string hash) => hash.Length >= 16 ? hash.Substring(0, 16) : hash;

        // ApplyGridUpgrade lived here. It was the CLI's local workaround for a grid raise that was
        // announced but never applied, and its own doc comment said "the proper fix belongs in
        // SearchSession.Create, which the web UI also goes through". On 2026-09-23 QA found that the web
        // UI did indeed ship G96 numbers under a G12 label, so the fix moved there and this went away
        // rather than being left as a second implementation of the same rule.

        /// <summary>
        /// Re-flows one long preflight sentence to the terminal, indenting its continuation lines.
        /// The refusals and confirmations name the fix in full sentences; printed as one line they
        /// wrapped at the terminal edge and the fix ended up in the middle of a word.
        /// </summary>
        internal static string Wrap(string text, string indent, int width = 94)
        {
            List<string> lines = new List<string>();
            string current = "";
            foreach (string word in text.Split(' '))
            {
                if (current.Length == 0) { current = word; continue; }
                if (current.Length + 1 + word.Length > width) { lines.Add(current); current = word; }
                else current += " " + word;
            }

            if (current.Length > 0) lines.Add(current);
            return string.Join(Environment.NewLine + indent, lines);
        }

        /// <summary>
        /// Corrects the plan's grid line to what the session FINALLY decided.
        ///
        /// <para>The preflight writes "grid  &lt;GridPlan.Describe()&gt;" while it is still holding the
        /// grid policy's first answer; <c>SearchSession.Create</c> then runs on and can turn the
        /// screening pass off after the fact - when no must-have goal is one a coarse screen is safe
        /// for, <c>ScreenThenVerify.ScreenQuery</c> hands back the original query and the session sets
        /// <c>ScreenGrid = 0</c>. The plan line written earlier still said "screen at G24 with a 1 %
        /// margin, then re-measure every survivor at G12", and measured: on a query with one fine-only
        /// must-have, the JSON report of the same run said screen_then_verify false. One of the two was
        /// wrong and it was the sentence the user reads. Re-rendering it from the session's own grid at
        /// the moment of printing makes them the same statement by construction.</para>
        /// </summary>
        private static string GridLine(string line, SearchSession session)
        {
            if (line.StartsWith("grid  ", StringComparison.Ordinal))
            {
                return "grid         " + session.Grid.Describe();
            }

            // Same problem, same cause: the preflight was given the thread count planned against the
            // grid the QUERY asked for, and the run uses the one planned against the grid the session
            // settled on. On a machine with room to spare those are the same number; on one where the
            // memory guard bites at 12 m and not at 384 m they are not, and the plan block would then
            // have printed two different thread counts three lines apart.
            if (line.StartsWith("threads  ", StringComparison.Ordinal))
            {
                return "threads      " + session.Threads.ToString(CultureInfo.InvariantCulture);
            }

            return line;
        }

        /// <summary>
        /// The preflight's own plan lines - the output bound, the record size, the grid and why, the
        /// region, the coverage and the checkpoint granularity. They are the SAME lines the web UI
        /// shows, produced by the same code, so the two front ends cannot describe a run differently.
        /// </summary>
        private static void PrintPreflight(Out o, SearchSession session, string checkpointPath)
        {
            if (o.Json) return;
            o.Header("Before anything is scanned");
            foreach (string line in session.Preflight.Plan) o.Note(GridLine(line, session));
            o.Note("checkpoint   " + checkpointPath);
            if (session.Grid.ScreenThenVerify)
            {
                o.Note("");
                o.Note("Every record written carries screened_at_grid_m: the coarse grid it was FOUND on,");
                o.Note("beside the numbers, which are the fine grid's. A survivor is always re-measured.");
            }
        }

        private static long DefaultBudget(Query q) => q.Search.Seeds > 0 ? q.Search.Seeds : 10_000;

        internal static Query LoadQuery(string token)
        {
            try
            {
                if (File.Exists(token)) return QueryReader.Parse(File.ReadAllText(token), token);
                if (Presets.Exists(token)) return Presets.Load(token);
            }
            catch (QueryException ex)
            {
                throw new CliException(ex.Message, ExitCodes.Usage, ex.Hint);
            }

            throw new CliException("'" + token + "' is neither a file nor a preset.", ExitCodes.NotFound,
                "presets: " + string.Join(", ", Presets.Names));
        }

        private static void ApplyOverrides(Query q, Args a)
        {
            if (a.Has("grid")) q.Search.Grid = a.Double("grid", q.Search.Grid);
            if (a.Has("block-size")) q.Search.BlockSize = a.Int("block-size", q.Search.BlockSize);

            // --keep takes a number OR the word "all", because the two are different modes and not
            // two spellings of one: a number is a REAL CAP on the file (the best N records and
            // nothing else), "all" streams every match and wants rotation. Reading it as an int
            // first would have turned "--keep all" into a parse error naming the wrong thing.
            if (a.Has("keep"))
            {
                string keep = a.Require("keep").Trim();
                if (string.Equals(keep, "all", StringComparison.OrdinalIgnoreCase))
                {
                    q.Search.KeepAll = true;
                }
                else if (int.TryParse(keep, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                {
                    q.Search.KeepAll = false;
                    q.Search.Keep = n;
                }
                else
                {
                    throw new CliException("--keep takes a whole number or 'all', not '" + keep + "'.",
                        ExitCodes.Usage,
                        "--keep 1000 writes only the best 1000; --keep all streams every match "
                        + "(pair it with --rotate 1GB on a large range)");
                }
            }

            if (a.Has("region")) q.Search.Region = a.Double("region", q.Search.Region);
            if (a.Has("screen-grid")) q.Search.ScreenGrid = a.Double("screen-grid", q.Search.ScreenGrid);
            if (a.Has("screen"))
            {
                string v = a.Require("screen").Trim().ToLowerInvariant();
                q.Search.Screen = v switch
                {
                    "auto" => ScreenMode.Auto,
                    "on" or "yes" or "true" => ScreenMode.On,
                    "off" or "no" or "false" => ScreenMode.Off,
                    _ => throw new CliException("--screen takes auto, on or off, not '" + v + "'.", ExitCodes.Usage,
                        "auto (the default) lets the measured grid policy decide and says what it chose"),
                };
            }

            if (a.Has("rotate")) q.Output.RotateBytes = SizeArg(a.Require("rotate"), "--rotate");
            if (a.Has("max-bytes")) q.Output.MaxBytes = SizeArg(a.Require("max-bytes"), "--max-bytes");
            if (a.Has("compress"))
            {
                string v = a.Require("compress").Trim().ToLowerInvariant();
                q.Output.Compress = v switch
                {
                    "gz" or "gzip" or "on" or "yes" or "true" => true,
                    "none" or "off" or "no" or "false" => false,
                    _ => throw new CliException("--compress takes 'gz' or 'none', not '" + v + "'."),
                };
            }

            if (a.Has("reduce")) q.Output.Reduce = a.Require("reduce");
            if (a.Has("on-limit"))
            {
                string v = a.Require("on-limit").Trim().ToLowerInvariant();
                if (v != "stop" && v != "evict")
                {
                    throw new CliException("--on-limit takes 'stop' or 'evict', not '" + v + "'.", ExitCodes.Usage,
                        "stop (the default) flushes, checkpoints and prints the resume command; "
                        + "evict keeps going and drops the lowest-scoring records");
                }

                q.Output.OnLimit = v;
            }

            if (a.Has("approx")) q.Search.Approx = a.Flag("approx");
            if (a.Has("from")) q.Search.From = a.Int("from", (int)q.Search.From);
            if (a.Has("to")) q.Search.To = a.Int("to", (int)q.Search.To);
            if (a.Has("order"))
            {
                string v = a.Require("order");
                q.Search.Order = v switch
                {
                    "shuffled" => ScanOrder.Shuffled,
                    "sequential" => ScanOrder.Sequential,
                    _ => throw new CliException("--order takes 'shuffled' or 'sequential', not '" + v + "'."),
                };
            }

            if (a.Has("key"))
            {
                string v = a.Require("key").Trim();
                bool hex = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                if (!(hex
                        ? ulong.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong k)
                        : ulong.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out k)))
                {
                    throw new CliException("--key takes a 64-bit number, e.g. 0x5EEDF00D1234ABCD.");
                }

                q.Search.Key = k;
            }

            if (q.Search.From > q.Search.To) throw new CliException("--from is above --to.");
            if (!q.Search.KeepAll && q.Search.Keep < 1) throw new CliException("--keep must be at least 1, or 'all'.");
            q.CanonicalJson = QueryReader.Canonicalise(q);
        }

        /// <summary>
        /// A size on the command line - <c>1GB</c>, <c>512MB</c>, a byte count - parsed by the SAME
        /// code that reads <c>output.rotate</c> out of a query file, so <c>--rotate 1GB</c> and
        /// <c>"rotate": "1GB"</c> cannot come to mean different numbers. It is reached through a
        /// one-element JSON document rather than copied, because a second implementation of a parser
        /// is a second set of edge cases.
        /// </summary>
        private static long SizeArg(string text, string option)
        {
            try
            {
                using System.Text.Json.JsonDocument d =
                    System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(text));
                return QueryReader.Size(d.RootElement, option);
            }
            catch (QueryException ex)
            {
                throw new CliException(option + ": " + ex.Message, ExitCodes.Usage, ex.Hint);
            }
        }

        /// <summary>
        /// The location oracle, or the refusing one with the reason. Built once per command: opening
        /// it parses <c>locations.json</c> and checks the DATA-STAMP against the installed game, and a
        /// mismatch refuses location goals rather than answering them from another build's table.
        /// </summary>
        internal static ILocationOracle Oracle(out string? problem)
            => SeedLab.LocationOracle.DumpedLocationOracle.Create(out problem);

        // ---- reporting -------------------------------------------------------------------------------

        private static void PrintPlan(Out o, Query q, CompiledQuery cq, ScanPlan plan, int threads,
                                      string? outPath, bool noPrefilter)
        {
            if (o.Json) return;
            o.Header("Search plan" + (q.Name != null ? " - " + q.Name : ""));
            if (q.Description != null) o.Note(q.Description);
            o.Field("grid", SearchGrids.Describe(cq.Grid));
            o.Field("region evaluated", cq.Plan.Radius >= 10500
                ? "the whole world (10,500 m)"
                : Out.Metres(cq.Plan.Radius) + " disc - "
                  + Out.Pct(cq.Plan.Radius * cq.Plan.Radius / (10500.0 * 10500.0)) + " of the map");
            o.Field("highest tier needed", TierName(cq.MaxTier));
            o.Field("heights sampled", cq.Plan.NeedHeights ? "yes" : "no (biome field only)");
            o.Field("order", plan.Order.ToString().ToLowerInvariant()
                             + " (key 0x" + plan.Key.ToString("X16") + ")");
            o.Field("range", Out.N(plan.From) + " .. " + Out.N(plan.To)
                             + "  (" + plan.Count.ToString("N0", CultureInfo.InvariantCulture) + " seeds)");
            o.Field("this run", Out.N(plan.Limit) + " seeds in " + Out.N(plan.Blocks) + " blocks of " + plan.BlockSize);
            o.Field("coverage", CoverageLine(plan));
            o.Field("threads", threads.ToString(CultureInfo.InvariantCulture)
                               + ", " + Bytes(SearchGrids.BytesPerWorker(cq.Grid) * threads) + " of grid buffers");
            o.Field("results", outPath ?? "(none - pass --out to keep them)");

            if (cq.Locations != null)
            {
                o.Field("location table", cq.LocationProvenance);
                o.Field("placement prefix",
                    cq.Locations.PrefixLength + " of " + cq.Locations.OrderedCount + " ordered entries ("
                    + Out.Pct(cq.Locations.PrefixFraction) + ") for "
                    + string.Join(", ", cq.Locations.Prefabs));
                o.Note("Each type's RNG stream is seeded from the world seed plus its own prefab-name hash,");
                o.Note("so nothing AFTER a type can change it - but zone occupancy is global and the");
                o.Note("AssetID / group / CountNrOfLocation buckets are shared, so nothing BEFORE it can be");
                o.Note("skipped. The prefix above is therefore the shortest correct one, not an estimate.");
            }

            if (noPrefilter) o.Note("--no-prefilter: audit mode, every goal measured over the whole world");
            if (q.Search.Approx) o.Note("approx = true is stamped on every record");

            o.Header("Goals");
            List<string[]> rows = new List<string[]>();
            foreach (CompiledGoal g in cq.Goals)
            {
                rows.Add(new[]
                {
                    g.Goal.Id,
                    g.Goal.Target.ToString() + "." + g.Goal.Metric,
                    g.Goal.Test.ToString().ToLowerInvariant() + " " + Value(g.Goal.Value, g.Def.Unit)
                        + (g.Goal.Test == GoalTest.Between ? " .. " + Value(g.Goal.Max, g.Def.Unit) : ""),
                    g.Goal.Importance == Importance.Must ? "must" : "nice x" + Out.F(g.Goal.Weight, 1),
                    TierName(g.Tier),
                });
            }

            o.Table(new[] { "id", "measures", "test", "importance", "tier" }, rows);
        }

        internal static string CoverageLine(ScanPlan plan)
        {
            double f = plan.Limit / 4294967296.0;
            string pct = f >= 0.01 ? Out.F(f * 100, 2) + " %"
                       : f >= 1e-6 ? Out.F(f * 100, 6) + " %"
                       : (f * 100).ToString("0.###e+00", CultureInfo.InvariantCulture) + " %";
            return pct + " of all 4,294,967,296 worlds"
                   + (plan.Limit >= 4294967296L ? " (the whole space)" : "");
        }

        private static void Calibrate(Out o, CompiledQuery cq, ILocationOracle oracle, ScanPlan plan,
                                      int threads, int n)
        {
            if (n < 1) n = 1;
            o.Header("Measured cost on this machine");
            SeedEvaluator ev = new SeedEvaluator(cq, oracle);
            // One seed first, so the JIT and the buffers are warm before anything is timed.
            ev.Evaluate(plan.SeedAt(0));

            SeedEvaluator timed = new SeedEvaluator(cq, oracle);
            Stopwatch sw = Stopwatch.StartNew();
            long hits = 0;
            for (int i = 0; i < n; i++)
            {
                if (timed.Evaluate(plan.SeedAt(i + 1)).Pass) hits++;
            }

            sw.Stop();
            double per = sw.Elapsed.TotalSeconds / n;
            double construct = timed.ConstructSeconds / n;
            double sample = timed.SampleSeconds / n;
            double pregen = timed.PregenSeconds / n;

            o.Field("seeds timed", n.ToString(CultureInfo.InvariantCulture) + " on 1 thread");
            o.Field("per seed", Out.F(per * 1000, 1) + " ms");
            o.Field("  of which construct", Out.F(construct * 1000, 1) + " ms  ("
                                            + Out.Pct(construct / per) + ")");
            o.Field("  of which pre-gen", Out.F(pregen * 1000, 1) + " ms  ("
                                        + Out.Pct(pregen / per) + ") on "
                                        + Out.N(timed.Pregenerated) + " of " + n + " seeds");
            o.Field("  of which sampling", Out.F(sample * 1000, 1) + " ms  (" + Out.Pct(sample / per) + ")");
            if (cq.Locations != null)
            {
                double loc = timed.LocationSeconds / n;
                o.Field("  of which locations", Out.F(loc * 1000, 1) + " ms  (" + Out.Pct(loc / per) + ") on "
                                                + Out.N(timed.Placed) + " of " + n + " seeds, "
                                                + Out.N(timed.LocationAborts) + " stopped early by the gate");
                o.Note("  (the 2048^2 biome/height point grid that GetRandomPointByBiomes draws from is");
                o.Note("   most of that and is not optional; the ordered prefix only shortens the rest.)");
            }

            o.Field("1 thread", Out.F(1.0 / per, 1) + " seeds/s");
            o.Note("");
            o.Note("The lake/river/stream pre-generation is ~99.5 % of the cost of a world and is read");
            o.Note("only by heights and by the river/lake/stream metrics: GetBiome takes GetBaseHeight,");
            o.Note("never GetHeight. It is therefore DEFERRED - it runs for the seeds and tiers that");
            o.Note("actually reach it, so a T2 biome-only query, and a T3 query whose T2 must-goal has");
            o.Note("already failed, never pay it. Same four calls, same RNG state, same world.");
            o.Note("");
            o.Field("pass rate on this sample", hits + " / " + n);

            double par = per / Math.Min(threads, 10.5);
            o.Header("What that means for this run - measured 1T, scaled by 10.5x");
            o.Note("(10.5x is the measured 16-thread speed-up on the reference 8-core/16-thread machine for this");
            o.Note(" kind of FP-heavy work, not the 16x the core count would suggest.)");
            o.Field("estimated rate", Out.F(1.0 / par, 1) + " seeds/s on " + threads + " threads");
            o.Field("this run", Duration(plan.Limit * par) + " for " + Out.N(plan.Limit) + " seeds");
            o.Field("coverage", CoverageLine(plan));
            o.Field("the whole space", Duration(4294967296.0 * par) + " for all 2^32 worlds");
            o.Note("");
            o.Note("Those last three are an ESTIMATE, not a measurement: a tight single-thread loop over a");
            o.Note("handful of seeds, scaled. On 2026-09-23 it ran ahead of the rate a real run then");
            o.Note("measured - by ~1.5x on a heavy T3 query and by ~35x on a cheap biome-only one, where");
            o.Note("per-seed fixed cost the calibration loop does not pay dominates. Treat it as an upper");
            o.Note("bound on throughput and confirm with a short real run ('--seeds 20000') before");
            o.Note("planning anything long on it. A finished run's 'measured rate' is the real number.");
        }


        private static void PrintProgress(Progress p)
        {
            string line = "  " + Out.N(p.Evaluated) + " / " + Out.N(p.Limit) + " seeds"
                          + " | " + Out.F(p.SeedsPerSecond, 1) + "/s"
                          + " | " + Out.N(p.Passed) + " hits"
                          + " | " + Out.F(100.0 * p.Evaluated / Math.Max(1, p.Limit), 1) + "% of the run"
                          + " | " + Sci(p.FractionCovered * 100) + "% of 2^32"
                          + " | eta " + (double.IsNaN(p.EtaSeconds) ? "?" : Duration(p.EtaSeconds));
            Console.Error.Write("\r" + line.PadRight(Math.Min(150, Math.Max(80, line.Length))));
        }

        private static void Report(Out o, Query q, CompiledQuery cq, ScanPlan plan, SearchOutcome r,
                                   string checkpointPath, SearchSession session)
        {
            if (o.Json)
            {
                o.J.WriteStartObject();
                o.J.WriteString("query", q.Name ?? "(unnamed)");
                o.J.WriteString("query_hash", QueryReader.Hash(q));
                o.J.WriteNumber("defs", q.Defs);
                o.J.WriteNumber("grid_m", q.Search.Grid);
                o.J.WriteString("order", plan.Order.ToString().ToLowerInvariant());
                o.J.WriteString("key", "0x" + plan.Key.ToString("X16"));
                o.J.WriteNumber("seeds_evaluated", r.Evaluated);
                o.J.WriteNumber("seeds_passed", r.Passed);
                o.J.WriteNumber("seconds", r.Seconds);
                o.J.WriteNumber("seeds_per_second", r.SeedsPerSecond);
                o.J.WriteNumber("threads", r.Threads);
                o.J.WriteNumber("fraction_of_seed_space", r.Evaluated / 4294967296.0);
                o.J.WriteBoolean("complete", r.Complete);
                o.J.WriteBoolean("stopped_by_wall", r.StoppedByWall);
                o.J.WriteBoolean("stopped_by_user", r.StoppedByUser);
                o.J.WriteNumber("probe_accepts", r.ProbeAccepts);
                o.J.WriteNumber("early_exits", r.EarlyExits);
                if (cq.Locations != null)
                {
                    o.J.WriteNumber("location_prefix", cq.Locations.PrefixLength);
                    o.J.WriteNumber("location_ordered_total", cq.Locations.OrderedCount);
                    o.J.WriteNumber("locations_placed", r.LocationsPlaced);
                    o.J.WriteNumber("location_aborts", r.LocationAborts);
                    o.J.WriteNumber("location_skips", r.LocationSkips);
                    o.J.WriteNumber("location_seconds", r.LocationSeconds);
                    o.J.WriteString("location_table", cq.LocationProvenance);
                }
                o.J.WriteNumber("construct_seconds_total", r.ConstructSeconds);
                o.J.WriteNumber("pregen_seconds_total", r.PregenSeconds);
                o.J.WriteNumber("pregenerated_seeds", r.Pregenerated);
                o.J.WriteNumber("sample_seconds_total", r.SampleSeconds);
                if (r.ResultsPath != null) o.J.WriteString("results", r.ResultsPath);
                o.J.WriteNumber("results_written", r.ResultsWritten);
                o.J.WriteNumber("results_dropped", r.ResultsDropped);
                o.J.WriteBoolean("bounded", r.Bounded);
                o.J.WriteString("result_line", session.ResultLine(r));
                o.J.WriteNumber("verify_grid_m", session.Grid.VerifyGrid);
                o.J.WriteNumber("screen_grid_m", session.Grid.ScreenGrid);
                o.J.WriteBoolean("screen_then_verify", session.Grid.ScreenThenVerify);
                o.J.WriteString("checkpoint", checkpointPath);
                o.J.WriteBoolean("checkpoint_retired", !File.Exists(checkpointPath));
                o.J.WriteStartArray("top");
                foreach (SeedResult s in r.Top)
                {
                    o.J.WriteStartObject();
                    o.J.WriteNumber("seed", s.Seed);
                    if (s.SeedText != null) o.J.WriteString("text", s.SeedText);
                    o.J.WriteNumber("score", s.Score);
                    o.J.WriteEndObject();
                }

                o.J.WriteEndArray();
                o.J.WriteEndObject();
                return;
            }

            o.Header("Result");
            o.Field("seeds evaluated", Out.N(r.Evaluated) + " of " + Out.N(plan.Limit) + " planned");
            o.Field("matches", Out.N(r.Passed)
                               + (r.Evaluated > 0 ? "  (" + Rate(r.Passed, r.Evaluated) + ")" : ""));
            o.Field("wall clock", Duration(r.Seconds));
            o.Field("measured rate", Out.F(r.SeedsPerSecond, 1) + " seeds/s on " + r.Threads + " threads");
            if (r.ConstructSeconds > 0)
            {
                double tot = r.ConstructSeconds + r.PregenSeconds + r.SampleSeconds;
                o.Field("thread time split", "construct " + Out.Pct(r.ConstructSeconds / tot)
                                             + ", pre-generation " + Out.Pct(r.PregenSeconds / tot)
                                             + ", sample " + Out.Pct(r.SampleSeconds / tot));
                o.Field("pre-generation", Out.N(r.Pregenerated) + " of " + Out.N(r.Evaluated)
                                          + " seeds needed river data");
            }

            if (r.ProbeAccepts > 0) o.Field("T1 probe accepts", Out.N(r.ProbeAccepts) + " seeds settled without the full biome pass");
            if (r.EarlyExits > 0) o.Field("T2 early exits", Out.N(r.EarlyExits) + " seeds rejected before the height pass");
            if (cq.Locations != null)
            {
                o.Field("T5 placements", Out.N(r.LocationsPlaced) + " seeds placed "
                                         + cq.Locations.PrefixLength + " of " + cq.Locations.OrderedCount
                                         + " ordered entries, " + Out.F(r.LocationSeconds, 1) + " s of worker time");
                o.Field("  stopped by the gate", Out.N(r.LocationAborts)
                                                 + " seeds rejected part-way through the prefix");
                o.Field("  never started", Out.N(r.LocationSkips)
                                           + " seeds a cheaper must-goal had already rejected");
            }
            o.Field("coverage", CoverageLine(new ScanPlan(plan.Order, plan.From, plan.To, plan.Key,
                                                          plan.BlockSize, r.Evaluated)));
            if (!r.Complete)
            {
                o.Note(r.StoppedByUser ? "stopped on request." : r.StoppedByWall ? "stopped on the wall budget." : "stopped early.");
                o.Note("resume with:  vseed search ... --resume --checkpoint " + checkpointPath);
            }

            if (r.ResultsPath != null)
            {
                // Never "N matches" when N was capped. ResultLine says "top 1,000 of 154,145", names
                // how many were dropped and the score the cut line started and ended at - the audit's
                // headline defect was a file that silently held everything while the report named a
                // number that had nothing to do with it.
                o.Field("results file", r.ResultsPath + "  (" + Out.N(r.ResultsWritten) + " records)");
                o.Field("what was kept", session.ResultLine(r));
                if (r.StoppedByLimit)
                {
                    o.Note("the run stopped at its output ceiling, cleanly, with the checkpoint written.");
                }
            }

            o.Field("checkpoint", File.Exists(checkpointPath)
                ? checkpointPath + "  (kept: this run has not finished)"
                : "retired - the run completed, so " + checkpointPath + " was deleted");

            if (r.Top.Count == 0)
            {
                o.Header("No seed matched");
                o.Note("At " + Out.F(r.SeedsPerSecond, 1) + " seeds/s this run saw "
                       + CoverageLine(new ScanPlan(plan.Order, plan.From, plan.To, plan.Key, plan.BlockSize, r.Evaluated)) + ".");
                o.Note("That is not evidence that no such world exists - only that none was in this sample.");
                o.Note("Loosen a must-have, or run longer with --resume.");
                return;
            }

            o.Header("Best " + Math.Min(r.Top.Count, 20) + " of " + Out.N(r.Passed));
            List<string[]> rows = new List<string[]>();
            List<CompiledGoal> nice = new List<CompiledGoal>();
            foreach (CompiledGoal g in cq.Goals)
            {
                if (g.Goal.Importance == Importance.Nice) nice.Add(g);
            }

            List<string> headers = new List<string> { "seed", "text", "score" };
            foreach (CompiledGoal g in nice) headers.Add(g.Goal.Id);
            bool[] right = new bool[headers.Count];
            for (int i = 2; i < right.Length; i++) right[i] = true;

            for (int i = 0; i < Math.Min(20, r.Top.Count); i++)
            {
                SeedResult s = r.Top[i];
                List<string> row = new List<string>
                {
                    s.Seed.ToString(CultureInfo.InvariantCulture),
                    s.SeedText ?? "",
                    Out.F(s.Score, 3),
                };

                foreach (CompiledGoal g in nice)
                {
                    string cell = "-";
                    foreach (GoalOutcome go in s.Goals)
                    {
                        if (go.Id == g.Goal.Id) cell = Value(go.Value, go.Unit);
                    }

                    row.Add(cell);
                }

                rows.Add(row.ToArray());
            }

            o.Table(headers, rows, right);
            o.Note("");
            o.Note("Inspect one with:  vseed seed <text>       Explain one with:  vseed explain <seed> <query>");
        }

        private static void PrintMetrics(Out o)
        {
            o.Header("Targets and metrics");
            List<string[]> rows = new List<string[]>();
            foreach (MetricDef m in MetricCatalog.All)
            {
                rows.Add(new[]
                {
                    m.Kind.ToString().ToLowerInvariant(),
                    m.Name,
                    ResultWriter.UnitName(m.Unit),
                    TierName(m.Tier),
                    m.NeedsLocations ? "needs the dumped location table" : "available",
                    m.Help,
                });
            }

            o.Table(new[] { "target", "metric", "unit", "tier", "status", "what it measures" }, rows);
            o.Note("");
            o.Note("biome names: " + string.Join(", ", MetricCatalog.BiomeNames));

            o.Header("Location groups");
            List<string[]> grows = new List<string[]>();
            ILocationOracle probe = Oracle(out _);
            foreach (LocationGroup g in LocationGroups.All)
            {
                int prefix = 0;
                bool known = probe.Available;
                foreach (string p in g.Prefabs)
                {
                    LocationTypeInfo? t = probe.TypeOf(p);
                    if (t == null) { known = false; continue; }
                    if (t.Placeable && t.PrefixLength > prefix) prefix = t.PrefixLength;
                }

                grows.Add(new[]
                {
                    g.Name,
                    g.Prefabs.Count.ToString(CultureInfo.InvariantCulture),
                    known ? prefix.ToString(CultureInfo.InvariantCulture) : "?",
                    g.Help,
                });
            }

            o.Table(new[] { "group", "prefabs", "prefix", "what it is" }, grows,
                    new[] { false, true, true, false });
            o.Note("");
            o.Note("'prefix' is how many of the 183 ordered location entries a seed has to place before");
            o.Note("the group's own instances are final - the ordered-prefix property. A group with a");
            o.Note("small prefix is cheap; one near 183 costs a seed the full placement.");

            o.Header("What a distance means for an m_unique type");
            o.Note("Haldor (Vendor_BlackForest), Hildir_camp and BogWitch_Camp are m_unique with");
            o.Note("m_quantity 10: the game writes TEN candidates into the world and keeps whichever one");
            o.Note("a player generates the zone of first, deleting the rest. That is exploration order,");
            o.Note("not seed, so there is no single position to measure. The three distance metrics say");
            o.Note("which question is being asked:");
            o.Note("  nearest_distance         at least ONE candidate is within D");
            o.Note("  all_types_distance       every named type has a candidate within D");
            o.Note("  all_candidates_distance  EVERY candidate is within D, so the survivor is too");
            o.Note("'vseed explain' prints the one a goal used, with the type names, on every result.");
        }

        internal static string TierName(Tier t) => t switch
        {
            Tier.T0 => "T0 static",
            Tier.T1 => "T1 probe",
            Tier.T2 => "T2 biome",
            Tier.T2b => "T2b bound",
            Tier.T3 => "T3 height",
            Tier.T4 => "T4 rivers",
            _ => "T5 locations",
        };

        internal static string Value(double v, Unit u)
        {
            if (double.IsNaN(v)) return "-";
            if (double.IsPositiveInfinity(v)) return "none";
            return u switch
            {
                Unit.Metres => Out.F(v, 1) + " m",
                Unit.SquareMetres => v >= 1e6 ? Out.F(v / 1e6, 2) + " km2" : Out.F(v / 1e4, 2) + " ha",
                Unit.Fraction => Out.Pct(v),
                _ => Out.F(v, 0),
            };
        }

        internal static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "?";
            if (seconds < 90) return Out.F(seconds, 1) + " s";
            if (seconds < 5400) return Out.F(seconds / 60, 1) + " min";
            if (seconds < 172800) return Out.F(seconds / 3600, 1) + " h";
            if (seconds < 63113852) return Out.F(seconds / 86400, 1) + " days";
            return Out.F(seconds / 31556952, 1) + " years";
        }

        private static string Rate(long hits, long of)
        {
            if (of <= 0) return "";
            double f = (double)hits / of;
            return f >= 0.001 ? Out.F(f * 100, 3) + " %" : Sci(f * 100) + " %";
        }

        private static string Sci(double v)
            => v == 0 ? "0" : v.ToString("0.###e+00", CultureInfo.InvariantCulture);

        private static string Bytes(long b)
            => b >= 1L << 30 ? Out.F(b / (double)(1L << 30), 1) + " GiB"
             : b >= 1L << 20 ? Out.F(b / (double)(1L << 20), 0) + " MiB"
             : Out.F(b / 1024.0, 0) + " KiB";
    }
}
