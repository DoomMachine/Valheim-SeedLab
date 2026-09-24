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

            // '--only 15,17' runs just those sections - for working on one of them; a full run is the
            // one whose count is reported.
            int only = Array.IndexOf(args, "--only");
            if (only >= 0 && only + 1 < args.Length)
            {
                foreach (string n in args[only + 1].Split(',', StringSplitOptions.RemoveEmptyEntries)) _only.Add(n.Trim());
            }

            Console.WriteLine("SeedLab.Search tests");
            Console.WriteLine(new string('=', 78));

            if (Section("1. Deferred pre-generation is the same world as eager pre-generation"))
            {
                PregenEquivalence.Run(Check, quick ? 6 : 24);
            }

            if (Section("2. The tiered evaluator agrees with an unfiltered evaluation"))
            {
                PrefilterParity.Run(Check, quick ? 150 : 600);
            }

            if (Section("3. The scan order is a bijection: no repeats, no gaps"))
            {
                PermutationChecks.Run(Check, quick);
            }

            if (Section("4. The criteria language round-trips and the presets all compile"))
            {
                QueryChecks.Run(Check);
            }

            if (Section("5. The two soundness bugs a verifier found stay fixed"))
            {
                SoundnessRegressions.Run(Check, quick);
            }

            if (Section("6. Location goals: the ordered prefix, the gate, the rings and the candidate sets"))
            {
                LocationGoals.Run(Check, quick);
            }

            if (Section("7. The funnel strategy's substrate: survivor lists and the stage plan"))
            {
                FunnelChecks.Run(Check, SeedLab.LocationOracle.DumpedLocationOracle.Create(out _));
            }

            if (Section("8. Display names, the grouped listing, and the curated world features"))
            {
                NameChecks.Run(Check);
            }

            if (Section("9. Names in, prefabs out: the target rewrite, the run hash and the contents caveat"))
            {
                QueryNameChecks.Run(Check);
            }

            if (Section("10. The plan: grid warnings only for goals the grid measures, each said once"))
            {
                PlanChecks.Run(Check);
            }

            if (Section("11. A block for every worker: the block-size rule, the resume point and the session"))
            {
                BlockChecks.Run(Check);
            }

            if (Section("12. What a run reports: the busy workers, the wall stop, 0 % coverage and the budget line"))
            {
                RunReportChecks.Run(Check);
            }

            if (Section("13. A stop, a gate and a plan line that say what the run did"))
            {
                StopAndGateChecks.Run(Check);
            }

            if (Section("14. Where funnel stage 2 checkpoints, serve's cache root, and an earlier build's stage 2"))
            {
                CheckpointPlaceChecks.Run(Check);
            }

            if (Section("15. A checkpoint another program holds: warnings, the last save, and the .top/.ckpt pair"))
            {
                AccessDeniedChecks.Run(Check);
            }

            if (Section("16. The data files this process verified, counted for the session log"))
            {
                IntegrityChecks.Run(Check);
            }

            if (Section("17. The terminal and the page when a file is in the way: start checks, warnings, the last save, the session log"))
            {
                HostChecks.Run(Check);
            }

            Console.WriteLine();
            Console.WriteLine(new string('=', 78));
            Console.WriteLine((_fail == 0 ? "PASS" : "FAIL") + "  " + _pass + " checks passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        private static readonly HashSet<string> _only = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Prints a section's title; false when '--only' leaves it out, and its checks are then not run.</summary>
        private static bool Section(string title)
        {
            bool skip = _only.Count > 0 && !_only.Contains(title.Substring(0, title.IndexOf('.')));
            Console.WriteLine();
            Console.WriteLine(title + (skip ? "   (skipped: --only)" : ""));
            Console.WriteLine(new string('-', title.Length));
            return !skip;
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
