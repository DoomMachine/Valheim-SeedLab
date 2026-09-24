using System;
using SeedLab.Search.Criteria;
using SeedLab.Search.Execution;
using SeedLab.Search.Locations;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// What a run says about itself when it ends, and what the plan says about its budget.
    ///
    /// <para><b>Why these checks exist.</b> Four sentences were false on 2026-09-24, each measured:
    /// a run the budget stopped before any worker claimed a block (<c>--budget 0.001s --seeds 512</c>,
    /// 0 seeds) reported "coverage 100.00 % ... (the whole space)", because the report built a
    /// <see cref="ScanPlan"/> from the seed count and a plan reads 0 as the whole range; a rate
    /// measured on 2 busy workers of 8 was reported "on 8 threads" and, on the web page, kept as the
    /// machine's; a run that finished could report <c>stopped_by_wall</c> as well as
    /// <c>complete</c>, because a worker that sees the wall after the last block was claimed still
    /// set the flag; and the documented "a budget cannot stop a run sooner than one block per
    /// worker" was a floor that does not exist - the true bound is an overrun, which the plan's
    /// budget line now states instead.</para>
    ///
    /// <para>The runs here go through <see cref="SearchRun"/>'s legacy entry point with no sink and
    /// no checkpoint, so nothing touches the disk; the queries are biome queries on the unavailable
    /// oracle, except the funnel clause, which needs the dumped location table to have a funnel at
    /// all and is skipped - said out loud - without it.</para>
    /// </summary>
    public static class RunReportChecks
    {
        private const string Engine = "tests";

        private const string Meadows =
            @"{""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":1000000,""importance"":""must""},
              {""id"":""swamp"",""target"":""biome:Swamp"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":100000,""importance"":""nice""}";

        public static void Run(Action<bool, string, string> check)
        {
            Coverage(check);
            BusyWorkers(check);
            WallStops(check);
            BudgetLine(check);
        }

        // =========================================================================================
        // Coverage is a count, never a plan built from one.

        private static void Coverage(Action<bool, string, string> check)
        {
            ScanPlan trap = new ScanPlan(ScanOrder.Shuffled, int.MinValue, int.MaxValue, 1, 16, 0);
            check(trap.Limit == 4294967296L,
                  "a plan reads a limit of 0 as the whole range - why a seed count must not build one (GUARD)",
                  "limit " + trap.Limit);

            string none = ScanPlan.CoverageLine(0);
            check(none == "0 % of all 4,294,967,296 worlds",
                  "a run that evaluated no seed covered 0 %, not the whole space", none);

            string all = ScanPlan.CoverageLine(4294967296L);
            check(all == "100.00 % of all 4,294,967,296 worlds (the whole space)",
                  "the whole space is still the whole space", all);

            string few = ScanPlan.CoverageLine(512);
            check(few == "1.192e-05 % of all 4,294,967,296 worlds",
                  "and a small run keeps its scientific notation", few);
        }

        // =========================================================================================
        // The workers that had work - counted at the claim - and the label the rate needs.

        private static void BusyWorkers(Action<bool, string, string> check)
        {
            SearchSession given = Session(Query("given", "\"grid\":384,\"screen\":\"off\",\"block_size\":256", Meadows), 512, 8);
            SearchOutcome two = RunIt(given, 8, TimeSpan.Zero);

            // At most 2: there are 2 blocks. Possibly 1: a worker that finishes its block before
            // another has started takes the second as well - which is exactly why it is counted at the
            // claim rather than derived from the plan.
            check(two.Complete && two.Threads == 8 && two.BusyWorkers >= 1 && two.BusyWorkers <= 2
                  && two.RateNote == "(" + two.BusyWorkers + " of 8 workers had work)",
                  "512 seeds in 2 blocks of 256 on 8 threads: at most 2 workers had work, and the rate says so",
                  "busy " + two.BusyWorkers + " of " + two.Threads + ", note " + (two.RateNote ?? "(none)")
                  + ", " + two.SeedsPerSecond.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " seeds/s");

            SearchOutcome one = RunIt(given, 1, TimeSpan.Zero);
            check(one.Complete && one.BusyWorkers == 1 && one.RateNote == null,
                  "one thread that had work needs no label", "busy " + one.BusyWorkers + " of " + one.Threads);
        }

        // =========================================================================================
        // The wall: a stop before any claim reports 0 seeds; a run that finished is not "stopped".

        private static void WallStops(Action<bool, string, string> check)
        {
            // A wall of one tick has passed before any worker has built its evaluator and looked, so no
            // block is ever claimed: this is the 0-seed stop --budget 0.001s produced from the CLI.
            SearchSession auto = Session(Query("wall", "\"grid\":384,\"screen\":\"off\"", Meadows), 512, 8);
            SearchOutcome zero = RunIt(auto, 8, TimeSpan.FromTicks(1));
            check(zero.StoppedByWall && !zero.Complete && zero.Evaluated == 0 && zero.BusyWorkers == 0,
                  "a wall that has passed before any worker looks stops the run at 0 seeds - there is no floor",
                  "evaluated " + zero.Evaluated + ", busy " + zero.BusyWorkers + ", complete " + zero.Complete
                  + ", stopped by wall " + zero.StoppedByWall + "; coverage " + ScanPlan.CoverageLine(zero.Evaluated));
            check(ScanPlan.CoverageLine(zero.Evaluated).StartsWith("0 %", StringComparison.Ordinal),
                  "and its coverage is 0 %", ScanPlan.CoverageLine(zero.Evaluated));

            // Two slow blocks on two workers, a wall shorter than one of them: both blocks are claimed
            // before the wall, both workers finish after it and look at it on their way out, so the flag
            // is set - on a run that FINISHED. It used to be reported as stopped by the wall anyway. The
            // check is the invariant, so a machine too loaded to claim both blocks inside the wall (the
            // run is then really stopped) cannot make it fail.
            SearchSession slow = Session(Query("slow",
                "\"grid\":24,\"screen\":\"off\",\"block_size\":8",
                @"{""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":10000,""test"":""at_least"",""value"":1000000,""importance"":""nice""}"),
                16, 2);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            SearchOutcome finished = RunIt(slow, 2, TimeSpan.FromMilliseconds(150));
            sw.Stop();
            check(finished.StoppedByWall == !finished.Complete,
                  "a run that finished is never reported as stopped by the wall, and one the wall cut short always is",
                  "complete " + finished.Complete + ", stopped by wall " + finished.StoppedByWall + ", "
                  + finished.Evaluated + " of 16 seeds in "
                  + sw.Elapsed.TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                  + " ms against a 150 ms wall" + (finished.Complete ? " (the case the fix is about)" : " (a loaded machine: really stopped)"));
        }

        // =========================================================================================
        // The plan's budget line: an overrun with its counts, never a floor.

        private static void BudgetLine(Action<bool, string, string> check)
        {
            SearchSession none = Session(Query("nobudget", "\"grid\":384,\"screen\":\"off\"", Meadows), 512, 8);
            check(PlanLine(none, "budget       ") == null, "no budget, no budget line (GUARD)", "");

            SearchSession auto = Session(Query("budget",
                "\"grid\":384,\"screen\":\"off\",\"budget\":{\"wall\":\"20s\"}", Meadows), 512, 8);
            string? line = PlanLine(auto, "budget       ");
            check(line != null
                  && line.StartsWith("budget       20 s, checked only when a worker is about to take a block", StringComparison.Ordinal)
                  && line.Contains("up to one block of work past 20 s (16 seeds on ONE worker)", StringComparison.Ordinal)
                  && line.Contains("with at most 8 blocks (128 seeds) in progress", StringComparison.Ordinal)
                  && line.Contains("Each resumed leg gets the whole budget again", StringComparison.Ordinal)
                  && !line.Contains("funnel", StringComparison.Ordinal),
                  "a 20 s budget on 512 seeds in blocks of 16: the overrun, with the counts the run has",
                  line ?? "(no budget line)");
            check(line != null && !line.Contains("cannot stop", StringComparison.Ordinal)
                  && !line.Contains("floor", StringComparison.Ordinal)
                  && !line.Contains("no earlier", StringComparison.Ordinal),
                  "and it never promises a floor, which does not exist (0 seeds at 0.001 s, measured)", "");

            SearchSession given = Session(Query("budget256",
                "\"grid\":384,\"screen\":\"off\",\"block_size\":256,\"budget\":{\"wall\":\"20s\"}", Meadows), 512, 8);
            string? line256 = PlanLine(given, "budget       ");
            check(line256 != null && line256.Contains("(256 seeds on ONE worker), with at most 2 blocks (512 seeds) in progress", StringComparison.Ordinal),
                  "a given 256 on the same run: 2 blocks in flight, not 8 - the busy workers', not the threads'",
                  line256 ?? "(no budget line)");

            ILocationOracle oracle = SeedLab.LocationOracle.DumpedLocationOracle.Create(out string? problem);
            check(oracle.Available, "the dumped location table is available for the funnel budget clause",
                  oracle.Available ? "" : (problem ?? "unavailable") + " - the funnel clause check below is SKIPPED, not passed");
            if (!oracle.Available) return;

            Query funnel = Query("budgetfunnel", "\"grid\":384,\"screen\":\"off\",\"budget\":{\"wall\":\"20s\"}",
                @"{""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":1000000,""importance"":""must""},
                  {""id"":""eik"",""target"":""location:Eikthyrnir"",""metric"":""nearest_distance"",""test"":""near"",""value"":2000,""importance"":""nice""}");
            SearchSession f = SearchSession.Create(funnel, oracle, Engine, 512, 8, outPath: null,
                                                   acceptScanOrder: true, allowVacuous: true);
            string? fline = PlanLine(f, "budget       ");
            check(FunnelPlan.For(f.Query, f.Compiled).Usable && fline != null
                  && fline.Contains("If this run takes the funnel, each stage gets the whole budget, and a stop in stage 1 ends the run",
                                    StringComparison.Ordinal),
                  "a query that can take the funnel is told each stage gets the whole budget and a stage-1 stop ends it",
                  fline ?? "(no budget line)");
        }

        // =========================================================================================
        // helpers

        private static Query Query(string name, string search, string goals)
            => QueryReader.Parse("{\"version\":1,\"defs\":1,\"name\":\"" + name + "\",\"search\":{" + search
                                 + "},\"goals\":[" + goals + "]}", name);

        private static SearchSession Session(Query q, long seeds, int threads)
            => SearchSession.Create(q, UnavailableLocationOracle.Instance, Engine, seeds, threads, outPath: null,
                                    acceptScanOrder: true, allowVacuous: true);

        /// <summary>The session's plan, run on <paramref name="threads"/> with no sink and no checkpoint.</summary>
        private static SearchOutcome RunIt(SearchSession s, int threads, TimeSpan wall)
            => new SearchRun(s.Compiled, s.Plan, UnavailableLocationOracle.Instance, threads)
                .Run(0, null, null, null, TimeSpan.FromHours(1), wall, null);

        private static string? PlanLine(SearchSession s, string needle)
        {
            foreach (string l in s.Preflight.Plan)
            {
                if (l.Contains(needle, StringComparison.Ordinal)) return l;
            }

            return null;
        }
    }
}
