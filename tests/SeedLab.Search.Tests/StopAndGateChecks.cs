using System;
using System.Collections.Generic;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Execution;
using SeedLab.Search.Locations;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// A stop, a gate and a plan line that say what the run did - the defects the three reviews of
    /// the block-size change found on 2026-09-24, each pinned by a check that fails without its fix.
    ///
    /// <para><b>A Stop that stopped nothing.</b> <c>StoppedByUser</c> was set by any Stop, so one
    /// pressed after the last block had been claimed reported a run that FINISHED as stopped - and a
    /// funnel's stage one reported so was thrown away as "the survivors of the seeds stage 1 reached,
    /// not of the range asked for" on both front ends. The wall had the same flaw and got
    /// <c>&amp;&amp; !complete</c> the same day; the Stop now has it too.</para>
    ///
    /// <para><b>The budget's numbers.</b> The block the overrun sentence named was the plan's size even
    /// when the whole run was smaller ("256 seeds on ONE worker" beside "at most 1 block (3 seeds)"),
    /// and a funnel's stage two, whose blocks are sized over its survivors, had no bound of its own:
    /// the gate now prints one.</para>
    ///
    /// <para><b>The gate at zero survivors</b> said "0 survivors at a block size of 1 is 1 block over 1
    /// of 8 threads".</para>
    ///
    /// <para><b>The plan's grid line</b> was written before the session turned the screening pass off;
    /// the CLI patched its copy at print time and the web page printed the stale one.</para>
    ///
    /// <para><b>The strategy, decided before the session.</b> The CLI now decides funnel or sample
    /// ahead of <see cref="SearchSession.Create"/>, so a funnel's resume never adopts a sample run's
    /// block size; that is sound only because a grid raise inside Create cannot change the decision,
    /// which the last check pins with the dumped location table (skipped, said out loud, without it).</para>
    /// </summary>
    public static class StopAndGateChecks
    {
        private const string Engine = "tests";

        private const string Meadows =
            @"{""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":1000000,""importance"":""must""},
              {""id"":""swamp"",""target"":""biome:Swamp"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":100000,""importance"":""nice""}";

        public static void Run(Action<bool, string, string> check)
        {
            LateStop(check);
            BudgetBlock(check);
            Gate(check);
            GridLine(check);
            StrategyBeforeTheSession(check);
        }

        // =========================================================================================
        // A Stop after the last claim stops nothing.

        private static void LateStop(Action<bool, string, string> check)
        {
            // 16 seeds in 2 blocks of 8 on ONE worker, so the order of events is fixed: block 0, then
            // block 1, and the Stop is pressed while the worker evaluates a chosen seed.
            SearchSession s = Session(Query("stop", "\"grid\":384,\"screen\":\"off\",\"block_size\":8", Meadows), 16, 1);
            check(s.Plan.Blocks == 2 && s.Plan.Limit == 16, "16 seeds in 2 blocks of 8 (GUARD)",
                  s.Plan.Limit + " seeds in " + s.Plan.Blocks + " blocks");

            SearchOutcome late = RunStoppingAt(s, s.Plan.SeedAt(s.Plan.Limit - 1));
            check(late.Complete && !late.StoppedByUser && late.Evaluated == 16,
                  "a Stop pressed during the LAST block - after the last claim - stops nothing, and the run is not reported as stopped",
                  "complete " + late.Complete + ", stopped by user " + late.StoppedByUser + ", " + late.Evaluated + " of 16 seeds");

            SearchOutcome early = RunStoppingAt(s, s.Plan.SeedAt(0));
            check(!early.Complete && early.StoppedByUser && early.Evaluated == 8,
                  "a Stop pressed during the first block ends the run after that block, and it IS reported as stopped",
                  "complete " + early.Complete + ", stopped by user " + early.StoppedByUser + ", " + early.Evaluated + " of 16 seeds");
        }

        // =========================================================================================
        // The budget line names the largest block left, and says what comes on top.

        private static void BudgetBlock(Action<bool, string, string> check)
        {
            SearchSession tiny = Session(Query("tiny",
                "\"grid\":384,\"screen\":\"off\",\"block_size\":256,\"budget\":{\"wall\":\"20s\"}", Meadows), 3, 8);
            string? line = PlanLine(tiny, "budget       ");
            check(line != null
                  && line.Contains("up to one block of work past 20 s (3 seeds on ONE worker), with at most 1 block (3 seeds) in progress",
                                   StringComparison.Ordinal),
                  "3 seeds at a given 256: the block named is the 3-seed block the run has, not a 256-seed one",
                  line ?? "(no budget line)");
            check(line != null && line.Contains("plus the workers' start-up and the final write", StringComparison.Ordinal),
                  "and the start-up and the final write are said to come on top (0.001 s measured up to 0.063 s with no block claimed)",
                  line ?? "(no budget line)");

            BlockSizeDecision resumed = BlockSizing.Decide(null, 190, 8, resume: new ResumePoint { BlockSize = 64, NextBlock = 2 });
            string bound = SearchPreflightCheck.BudgetBound(TimeSpan.FromSeconds(20), resumed, "run");
            check(bound.Contains("(62 seeds on ONE worker), with at most 1 block (62 seeds) in progress", StringComparison.Ordinal),
                  "a resumed leg with only the short last block left names that block: 190 at 64 from block 2 is one block of 62",
                  bound);
        }

        // =========================================================================================
        // The funnel gate: nothing to spread at zero survivors; stage two's own budget bound.

        private static void Gate(Action<bool, string, string> check)
        {
            FunnelGate empty = new FunnelGate
            {
                Scanned = 400,
                Survivors = 0,
                Decision = BlockSizing.Decide(null, 0, 8),
                Wall = TimeSpan.FromSeconds(20),
                StageOneSeconds = 1.0,
                SecondsPerSurvivor = 0.0,
            };
            string emptyText = string.Join("\n", empty.Lines());
            check(!emptyText.Contains("survivors at a block size of", StringComparison.Ordinal)
                  && !emptyText.Contains("stage 2 budget", StringComparison.Ordinal),
                  "a gate with no survivor says nothing about spreading them, or about a budget for placing none",
                  emptyText);

            FunnelGate fresh = new FunnelGate
            {
                Scanned = 4_000,
                Survivors = 190,
                Decision = BlockSizing.Decide(null, 190, 8),
                Wall = TimeSpan.FromSeconds(20),
                StageOneSeconds = 1.0,
                SecondsPerSurvivor = 2.0,
            };
            string? freshBudget = Find(fresh.Lines(), "stage 2 budget       ");
            check(fresh.BlockSize == 5 && freshBudget != null
                  && freshBudget.Contains("so the stage can end up to one block of work past 20 s (5 seeds on ONE worker), with at most 8 blocks (40 seeds) in progress",
                                          StringComparison.Ordinal),
                  "190 survivors on 8 workers at the automatic 5: stage 2's own bound, with ITS blocks",
                  freshBudget ?? "(no stage 2 budget line)");

            FunnelGate noWall = new FunnelGate
            {
                Scanned = 4_000,
                Survivors = 190,
                Decision = BlockSizing.Decide(null, 190, 8),
                StageOneSeconds = 1.0,
                SecondsPerSurvivor = 2.0,
            };
            check(Find(noWall.Lines(), "stage 2 budget") == null, "no budget, no stage 2 budget line (GUARD)", "");

            FunnelGate adopted = new FunnelGate
            {
                Scanned = 4_000,
                Survivors = 190,
                Decision = BlockSizing.Decide(null, 190, 8, resume: new ResumePoint { BlockSize = 64, NextBlock = 1 }),
                Wall = TimeSpan.FromSeconds(20),
                StageOneSeconds = 1.0,
                SecondsPerSurvivor = 2.0,
            };
            string? adoptedBudget = Find(adopted.Lines(), "stage 2 budget       ");
            check(adoptedBudget != null
                  && adoptedBudget.Contains("(64 seeds on ONE worker), with at most 2 blocks (126 seeds) in progress", StringComparison.Ordinal),
                  "a resumed stage 2 keeps its checkpoint's 64, and its bound is 64-seed blocks - the plan's stage-1 figures would understate it",
                  adoptedBudget ?? "(no stage 2 budget line)");
        }

        // =========================================================================================
        // The plan's grid line is the grid the session settled on.

        private static void GridLine(Action<bool, string, string> check)
        {
            // large-continents is raised from G24 to G12, and at G12 its screening pass is turned off
            // (no must-have is one a coarse screen is safe for): the preflight wrote "screen at G24 ...
            // then re-measure every survivor at G12" before that, and the web page printed it.
            Query lc = Presets.Load("large-continents");
            SearchSession s = SearchSession.Create(lc, UnavailableLocationOracle.Instance, Engine, 300, 8, outPath: null,
                                                   acceptScanOrder: true, allowVacuous: true);
            string? line = PlanLine(s, "grid         ");
            check(line == "grid         " + s.Grid.Describe(),
                  "large-continents at 300 seeds: the plan's grid line is the session's final grid, which the web page prints as it stands",
                  (line ?? "(no grid line)") + "  |  session: " + s.Grid.Describe());

            // The same through --no-prefilter, which turns the screen off in the session itself.
            SearchSession audit = SearchSession.Create(Presets.Load("gentle-start"), UnavailableLocationOracle.Instance, Engine,
                                                       300, 8, outPath: null, noPrefilter: true,
                                                       acceptScanOrder: true, allowVacuous: true);
            string? auditLine = PlanLine(audit, "grid         ");
            check(!audit.Grid.ScreenThenVerify && auditLine == "grid         " + audit.Grid.Describe(),
                  "--no-prefilter turns the screen off, and the plan's grid line says so",
                  (auditLine ?? "(no grid line)") + "  |  session: " + audit.Grid.Describe());
        }

        // =========================================================================================
        // The CLI decides funnel or sample BEFORE the session: a grid raise must not change it.

        private static void StrategyBeforeTheSession(Action<bool, string, string> check)
        {
            ILocationOracle oracle = SeedLab.LocationOracle.DumpedLocationOracle.Create(out string? problem);
            check(oracle.Available, "the dumped location table is available for the strategy-under-a-raise check",
                  oracle.Available ? "" : (problem ?? "unavailable") + " - the check below is SKIPPED, not passed");
            if (!oracle.Available) return;

            // A must-have no coarse grid measures safely (raised from G96 to G12) and a location goal
            // (a funnel needs one): the funnel decision before the session and after it.
            Query q = QueryReader.Parse("{\"version\":1,\"defs\":1,\"name\":\"raisefunnel\",\"search\":{\"grid\":96},\"goals\":["
                + @"{""id"":""home"",""target"":""world:spawn_island_area"",""metric"":""spawn_island_area"",""test"":""at_least"",""value"":1000000,""importance"":""must""},
                    {""id"":""eik"",""target"":""location:Eikthyrnir"",""metric"":""nearest_distance"",""test"":""near"",""value"":2000,""importance"":""nice""}"
                + "]}", "raisefunnel");
            FunnelPlan before = FunnelPlan.For(q, CompiledQuery.Compile(q, oracle, false));
            SearchSession s = SearchSession.Create(q, oracle, Engine, 512, 8, outPath: null,
                                                   acceptScanOrder: true, allowVacuous: true);
            FunnelPlan after = FunnelPlan.For(s.Query, s.Compiled);
            check(s.Grid.RaisedFrom == 96.0 && before.Usable && after.Usable
                  && string.Join(",", before.StageOneGoals) == string.Join(",", after.StageOneGoals)
                  && string.Join(",", before.DeferredGoals) == string.Join(",", after.DeferredGoals),
                  "a query raised from G96 to G12 takes the funnel before the session and after it, with the same stages",
                  "raised from " + s.Grid.RaisedFrom + "; before: " + before.Usable + " [" + string.Join(",", before.StageOneGoals)
                  + " | " + string.Join(",", before.DeferredGoals) + "], after: " + after.Usable + " ["
                  + string.Join(",", after.StageOneGoals) + " | " + string.Join(",", after.DeferredGoals) + "]");
        }

        // =========================================================================================
        // helpers

        private static SearchOutcome RunStoppingAt(SearchSession s, int stopSeed)
        {
            SearchRun run = new SearchRun(s.Compiled, s.Plan, UnavailableLocationOracle.Instance, 1);
            run.EvaluatorFactory = () => new StopAt(new SeedEvaluator(s.Compiled, UnavailableLocationOracle.Instance), stopSeed, run);
            return run.Run(0, null, null, null, TimeSpan.FromHours(1), TimeSpan.Zero, null);
        }

        /// <summary>An evaluator that presses Stop while it evaluates one chosen seed.</summary>
        private sealed class StopAt : ISeedEvaluator
        {
            private readonly ISeedEvaluator _inner;
            private readonly int _seed;
            private readonly SearchRun _run;

            public StopAt(ISeedEvaluator inner, int seed, SearchRun run)
            {
                _inner = inner;
                _seed = seed;
                _run = run;
            }

            public SeedResult Evaluate(int seed, bool full = false)
            {
                if (seed == _seed) _run.RequestStop();
                return _inner.Evaluate(seed, full);
            }

            public double ConstructSeconds => _inner.ConstructSeconds;
            public double SampleSeconds => _inner.SampleSeconds;
            public double PregenSeconds => _inner.PregenSeconds;
            public double LocationSeconds => _inner.LocationSeconds;
            public long Pregenerated => _inner.Pregenerated;
            public long ProbeAccepts => _inner.ProbeAccepts;
            public long EarlyExits => _inner.EarlyExits;
            public long LocationAborts => _inner.LocationAborts;
            public long LocationSkips => _inner.LocationSkips;
            public long Placed => _inner.Placed;
        }

        private static Query Query(string name, string search, string goals)
            => QueryReader.Parse("{\"version\":1,\"defs\":1,\"name\":\"" + name + "\",\"search\":{" + search
                                 + "},\"goals\":[" + goals + "]}", name);

        private static SearchSession Session(Query q, long seeds, int threads)
            => SearchSession.Create(q, UnavailableLocationOracle.Instance, Engine, seeds, threads, outPath: null,
                                    acceptScanOrder: true, allowVacuous: true);

        private static string? PlanLine(SearchSession s, string needle)
        {
            foreach (string l in s.Preflight.Plan)
            {
                if (l.Contains(needle, StringComparison.Ordinal)) return l;
            }

            return null;
        }

        private static string? Find(IEnumerable<string> lines, string prefix)
        {
            foreach (string l in lines)
            {
                if (l.StartsWith(prefix, StringComparison.Ordinal)) return l;
            }

            return null;
        }
    }
}
