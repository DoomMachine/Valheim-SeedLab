using System;
using System.Collections.Generic;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// Permanent checks for the two soundness bugs an adversarial verifier found in the tiered
    /// evaluator, plus the general audit-equality check that would have caught either of them.
    ///
    /// <para><b>Both bugs were one-directional and silent</b>, which is why they need a test rather
    /// than a comment: each made the fast path disagree with the audit path on seeds a user would
    /// never re-check by hand.</para>
    ///
    /// <list type="number">
    /// <item><b>The T1 probe's 'present' comparison.</b> <c>present</c> is a 0/1 indicator - the exact
    /// reader is <c>m.BiomeCells[bi] &gt; 0 ? 1 : 0</c> - but the probe compared its raw sampled cell
    /// COUNT against the threshold. A query asking <c>present &gt;= 2</c> was then accepted by the
    /// probe as soon as two probe cells of the biome were found, while the exact pass can never return
    /// more than 1. Every such seed was a false positive, and the probe is accept-only so nothing
    /// downstream re-checked it. The query file is scratchpad\verify3\present2.json, reproduced
    /// verbatim below.</item>
    /// <item><b>T0 static analysis refusing a satisfiable 'between'.</b>
    /// <c>StaticAnalysis.TooMuchArea</c> compared the goal's <i>upper</i> end against the biome's
    /// maximum possible area, so "Meadows area between 1 km2 and 1,000,000 km2" - which every seed in
    /// the space satisfies - was declared impossible for every seed and the run refused to start. The
    /// achievable set is [0, cap], so only the LOW end can make a range unreachable.</item>
    /// </list>
    /// </summary>
    public static class SoundnessRegressions
    {
        /// <summary>scratchpad\verify3\present2.json, the query that exposed bug 1.</summary>
        private const string Present2 = @"{ ""version"":1,""defs"":1,""name"":""present>=2 probe soundness"",""world"":{""gen_version"":2},
  ""search"":{""order"":""shuffled"",""key"":""0xAAAABBBBCCCCDDDD"",""grid"":192,""keep"":10,""block_size"":256},
  ""goals"":[
   {""id"":""ocean"",""target"":""biome:Ocean"",""metric"":""present"",""test"":""at_least"",""value"":2,""importance"":""must""},
   {""id"":""mist"",""target"":""biome:Mistlands"",""metric"":""present"",""test"":""at_least"",""value"":2,""importance"":""must""}
  ]}";

        /// <summary>The probe-friendly sibling: the same query with the reachable threshold 1.</summary>
        private const string Present1 = @"{ ""version"":1,""defs"":1,""name"":""present>=1"",""world"":{""gen_version"":2},
  ""search"":{""order"":""shuffled"",""key"":""0xAAAABBBBCCCCDDDD"",""grid"":192,""keep"":10,""block_size"":256},
  ""goals"":[
   {""id"":""ocean"",""target"":""biome:Ocean"",""metric"":""present"",""test"":""at_least"",""value"":1,""importance"":""must""},
   {""id"":""mist"",""target"":""biome:Mistlands"",""metric"":""present"",""test"":""at_least"",""value"":1,""importance"":""must""}
  ]}";

        /// <summary>scratchpad\verify3\adv.json - a 'between' every seed satisfies. T0 must let it run.</summary>
        private const string BetweenSatisfiable = @"{ ""version"":1,""defs"":1,""name"":""adv"",""world"":{""gen_version"":2},
  ""search"":{""order"":""shuffled"",""key"":""0x11"",""grid"":192,""keep"":10,""block_size"":64},
  ""goals"":[{""id"":""g"",""target"":""biome:Meadows"",""metric"":""area"",""test"":""between"",""value"":1000000,""max"":1000000000000,""importance"":""must""}]}";

        /// <summary>scratchpad\verify3\adv2.json - the LOW end is above the cap, so T0 must still refuse it.</summary>
        private const string BetweenImpossible = @"{ ""version"":1,""defs"":1,""name"":""adv2"",""world"":{""gen_version"":2},
  ""search"":{""order"":""shuffled"",""key"":""0x11"",""grid"":192,""keep"":10,""block_size"":64},
  ""goals"":[{""id"":""g"",""target"":""biome:Meadows"",""metric"":""area"",""test"":""between"",""value"":1000000000000,""max"":2000000000000,""importance"":""must""}]}";

        /// <summary>scratchpad\verify3\adv3.json - the plain 'at_least' form of the same impossibility.</summary>
        private const string AtLeastImpossible = @"{ ""version"":1,""defs"":1,""name"":""adv3"",""world"":{""gen_version"":2},
  ""search"":{""order"":""shuffled"",""key"":""0x11"",""grid"":192,""keep"":10,""block_size"":64},
  ""goals"":[{""id"":""g"",""target"":""biome:Meadows"",""metric"":""area"",""test"":""at_least"",""value"":400000000,""importance"":""must""}]}";

        public static void Run(Action<bool, string, string> check, bool quick)
        {
            ProbePresentIsAnIndicator(check, quick ? 64 : 256);
            StaticAnalysisReadsTheLowEndOfARange(check);
            FixedRangeAuditEquality(check, quick ? 48 : 192);
        }

        // ---- bug 1 -------------------------------------------------------------------------------

        /// <summary>
        /// <c>present</c> is an indicator, so <c>present &gt;= 2</c> is unsatisfiable by definition and
        /// the accept-only probe must witness nothing. The companion <c>present &gt;= 1</c> query is
        /// run as the positive control: if the probe had simply stopped firing, the first assertion
        /// would pass for the wrong reason.
        /// </summary>
        private static void ProbePresentIsAnIndicator(Action<bool, string, string> check, int seeds)
        {
            ILocationOracle oracle = UnavailableLocationOracle.Instance;

            Query q2 = QueryReader.Parse(Present2, "present2");
            CompiledQuery fast2 = CompiledQuery.Compile(q2, oracle, noPrefilter: false);
            CompiledQuery slow2 = CompiledQuery.Compile(q2, oracle, noPrefilter: true);
            SeedEvaluator evFast2 = new SeedEvaluator(fast2, oracle);
            SeedEvaluator evSlow2 = new SeedEvaluator(slow2, oracle);

            int fastPasses = 0, slowPasses = 0, disagree = 0;
            int firstBad = 0;
            foreach (int seed in Range(1_000_000, seeds))
            {
                bool a = evFast2.Evaluate(seed).Pass;
                bool b = evSlow2.Evaluate(seed, full: true).Pass;
                if (a) fastPasses++;
                if (b) slowPasses++;
                if (a != b && disagree++ == 0) firstBad = seed;
            }

            check(fastPasses == 0 && slowPasses == 0 && disagree == 0,
                  "T1 probe: 'present >= 2' is unsatisfiable and the probe never accepts it",
                  seeds + " seeds: tiered " + fastPasses + " hits, audit " + slowPasses + " hits, "
                  + disagree + " disagreements"
                  + (disagree > 0 ? "  <<< first at seed " + firstBad : "")
                  + "; probe accepts " + evFast2.ProbeAccepts);

            check(evFast2.ProbeAccepts == 0,
                  "  the probe settled no seed on an indicator it cannot witness",
                  evFast2.ProbeAccepts + " probe accepts (must be 0: the exact reader returns at most 1)");

            // Positive control - the same shape at a threshold the probe CAN witness.
            Query q1 = QueryReader.Parse(Present1, "present1");
            CompiledQuery fast1 = CompiledQuery.Compile(q1, oracle, noPrefilter: false);
            CompiledQuery slow1 = CompiledQuery.Compile(q1, oracle, noPrefilter: true);
            SeedEvaluator evFast1 = new SeedEvaluator(fast1, oracle);
            SeedEvaluator evSlow1 = new SeedEvaluator(slow1, oracle);

            int hits = 0, diff = 0;
            foreach (int seed in Range(1_000_000, seeds))
            {
                bool a = evFast1.Evaluate(seed).Pass;
                bool b = evSlow1.Evaluate(seed, full: true).Pass;
                if (a) hits++;
                if (a != b) diff++;
            }

            check(diff == 0 && evFast1.ProbeAccepts > 0,
                  "  positive control: 'present >= 1' still fires the probe and still agrees with the audit",
                  hits + " hits of " + seeds + ", " + evFast1.ProbeAccepts + " probe accepts, " + diff + " disagreements");
        }

        // ---- bug 2 -------------------------------------------------------------------------------

        private static void StaticAnalysisReadsTheLowEndOfARange(Action<bool, string, string> check)
        {
            ILocationOracle oracle = UnavailableLocationOracle.Instance;

            CompiledQuery ok = CompiledQuery.Compile(QueryReader.Parse(BetweenSatisfiable, "adv"), oracle);
            check(ok.Unsatisfiable.Count == 0,
                  "T0: a 'between' whose LOW end is reachable is not refused",
                  ok.Unsatisfiable.Count == 0
                      ? "Meadows area between 1 km2 and 1,000,000 km2 compiles - every seed satisfies it"
                      : "WRONGLY REFUSED: " + ok.Unsatisfiable[0].Unsatisfiable);

            CompiledQuery no = CompiledQuery.Compile(QueryReader.Parse(BetweenImpossible, "adv2"), oracle);
            check(no.Unsatisfiable.Count == 1,
                  "  a 'between' whose LOW end is above the cap is still refused",
                  no.Unsatisfiable.Count == 1 ? no.Unsatisfiable[0].Unsatisfiable ?? "" : "NOT CAUGHT");

            CompiledQuery no2 = CompiledQuery.Compile(QueryReader.Parse(AtLeastImpossible, "adv3"), oracle);
            check(no2.Unsatisfiable.Count == 1,
                  "  the plain 'at_least' form of the same impossibility is still refused",
                  no2.Unsatisfiable.Count == 1 ? no2.Unsatisfiable[0].Unsatisfiable ?? "" : "NOT CAUGHT");

            // And the one that made the rule necessary: an unbounded upper end must never be read as
            // the thing that decides reachability.
            Query huge = QueryReader.Parse(@"{""version"":1,""goals"":[{""id"":""g"",""target"":""biome:Swamp"",
                ""metric"":""area_within"",""radius"":9000,""test"":""between"",""value"":1000,
                ""max"":999999999999,""importance"":""must""}]}", "wide-range");
            CompiledQuery wide = CompiledQuery.Compile(huge, oracle);
            check(wide.Unsatisfiable.Count == 0,
                  "  an area_within 'between' with an absurd upper end is not refused either",
                  wide.Unsatisfiable.Count == 0 ? "compiles" : "WRONGLY REFUSED: " + wide.Unsatisfiable[0].Unsatisfiable);
        }

        // ---- the general check that covers both --------------------------------------------------

        /// <summary>
        /// The audit contract, stated as a set equality over a <b>fixed contiguous range</b> rather
        /// than a scattered sample: for every query, the seeds a prefiltered run returns must be
        /// exactly the seeds a <c>--no-prefilter</c> run returns. Both of the bugs above are set
        /// differences, and a contiguous range is the form a user can reproduce from the command line
        /// with <c>--from</c>/<c>--to</c>.
        /// </summary>
        private static void FixedRangeAuditEquality(Action<bool, string, string> check, int seeds)
        {
            const int from = 5_000_000;
            foreach (KeyValuePair<string, string> q in AuditQueries())
            {
                ILocationOracle oracle = UnavailableLocationOracle.Instance;
                Query parsed = QueryReader.Parse(q.Value, q.Key);
                CompiledQuery fast = CompiledQuery.Compile(parsed, oracle, noPrefilter: false);
                CompiledQuery slow = CompiledQuery.Compile(QueryReader.Parse(q.Value, q.Key), oracle, noPrefilter: true);
                SeedEvaluator evFast = new SeedEvaluator(fast, oracle);
                SeedEvaluator evSlow = new SeedEvaluator(slow, oracle);

                List<int> hitsFast = new List<int>();
                List<int> hitsSlow = new List<int>();
                foreach (int seed in Range(from, seeds))
                {
                    if (evFast.Evaluate(seed).Pass) hitsFast.Add(seed);
                    if (evSlow.Evaluate(seed, full: true).Pass) hitsSlow.Add(seed);
                }

                string diff = SetDifference(hitsFast, hitsSlow);
                check(diff.Length == 0,
                      "audit equality over [" + from + ", " + (from + seeds - 1) + "]: " + q.Key,
                      hitsFast.Count + " hits in " + seeds + " seeds, identical sets"
                      + (diff.Length > 0 ? "  <<< " + diff : "")
                      + " (" + evFast.ProbeAccepts + " T1 accepts, " + evFast.EarlyExits + " early exits)");
            }
        }

        private static string SetDifference(List<int> a, List<int> b)
        {
            HashSet<int> sa = new HashSet<int>(a), sb = new HashSet<int>(b);
            List<string> parts = new List<string>();
            foreach (int s in a)
            {
                if (!sb.Contains(s)) { parts.Add("tiered-only " + s); break; }
            }

            foreach (int s in b)
            {
                if (!sa.Contains(s)) { parts.Add("audit-only " + s); break; }
            }

            return string.Join(", ", parts);
        }

        private static IEnumerable<int> Range(int from, int count)
        {
            for (int i = 0; i < count; i++) yield return from + i;
        }

        /// <summary>
        /// One query per prefilter the engine has, so the set equality above is not vacuous: an
        /// indicator goal (the T1 probe), a region-restricted nearest goal, a cheap must-goal in front
        /// of an expensive height goal (the T2 early exit), and a 'between' that T0 must leave alone.
        /// </summary>
        private static Dictionary<string, string> AuditQueries()
        {
            return new Dictionary<string, string>
            {
                ["indicator + T1 probe"] = Present1,
                ["T0 'between' that must run"] = BetweenSatisfiable,

                ["region-restricted nearest"] = @"{ ""version"":1, ""search"":{""grid"":256},
                  ""goals"":[
                    {""id"":""bf"",""target"":""biome:BlackForest"",""metric"":""nearest_distance"",""test"":""near"",""value"":700,""importance"":""must""},
                    {""id"":""swamp"",""target"":""biome:Swamp"",""metric"":""nearest_distance"",""test"":""near"",""value"":2600,""importance"":""must""}
                  ]}",

                ["T2 gate in front of T3"] = @"{ ""version"":1, ""search"":{""grid"":384},
                  ""goals"":[
                    {""id"":""mountain"",""target"":""biome:Mountain"",""metric"":""area"",""test"":""at_least"",""value"":13000000,""importance"":""must""},
                    {""id"":""peak"",""target"":""world:highest_peak"",""metric"":""highest_peak"",""test"":""at_least"",""value"":210,""importance"":""must""},
                    {""id"":""land"",""target"":""world:land_share"",""metric"":""land_share"",""test"":""at_least"",""value"":0.18,""importance"":""nice""}
                  ]}",
            };
        }
    }
}
