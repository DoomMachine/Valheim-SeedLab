using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// The search engine's test suite. Deliberately dependency-free: it is a console program that
    /// returns 0 when every check passes and 1 when one does not, so it can be run from anywhere
    /// without a test runner.
    /// </summary>
    public static class Program
    {
        private static int _pass, _fail;

        public static int Main(string[] args)
        {
            bool quick = Array.IndexOf(args, "--quick") >= 0;

            Console.WriteLine("SeedLab.Search tests");
            Console.WriteLine(new string('=', 78));

            Section("1. Deferred pre-generation is the same world as eager pre-generation");
            PregenEquivalence.Run(Check, quick ? 6 : 24);

            Section("2. The tiered evaluator agrees with an unfiltered evaluation");
            PrefilterParity.Run(Check, quick ? 150 : 600);

            Section("3. The scan order is a bijection: no repeats, no gaps");
            PermutationChecks.Run(Check, quick);

            Section("4. The criteria language round-trips and the presets all compile");
            QueryChecks.Run(Check);

            Section("5. The two soundness bugs a verifier found stay fixed");
            SoundnessRegressions.Run(Check, quick);

            Section("6. Location goals: the ordered prefix, the gate, the rings and the candidate sets");
            LocationGoals.Run(Check, quick);

            Section("7. The funnel strategy's substrate: survivor lists and the stage plan");
            FunnelChecks.Run(Check, SeedLab.LocationOracle.DumpedLocationOracle.Create(out _));

            Section("8. Display names, the grouped listing, and the curated world features");
            NameChecks.Run(Check);

            Section("9. Names in, prefabs out: the target rewrite, the run hash and the contents caveat");
            QueryNameChecks.Run(Check);

            Section("10. The plan: grid warnings only for goals the grid measures, each said once");
            PlanChecks.Run(Check);

            Section("11. A block for every worker: the block-size rule, the resume point and the session");
            BlockChecks.Run(Check);

            Section("12. What a run reports: the busy workers, the wall stop, 0 % coverage and the budget line");
            RunReportChecks.Run(Check);

            Section("13. A stop, a gate and a plan line that say what the run did");
            StopAndGateChecks.Run(Check);

            Console.WriteLine();
            Console.WriteLine(new string('=', 78));
            Console.WriteLine((_fail == 0 ? "PASS" : "FAIL") + "  " + _pass + " checks passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine(title);
            Console.WriteLine(new string('-', title.Length));
        }

        /// <summary>The one assertion primitive: a named condition and a detail line shown either way.</summary>
        public static void Check(bool ok, string name, string detail)
        {
            if (ok) _pass++;
            else _fail++;
            Console.WriteLine("  [" + (ok ? "ok  " : "FAIL") + "] " + name + (detail.Length > 0 ? " - " + detail : ""));
        }
    }
}
