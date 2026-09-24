using System;
using System.Collections.Generic;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// The prefilter correctness test the design asks for: <b>a prefilter that can reject a seed which
    /// would actually have matched is a correctness bug</b>, so the tiered evaluator and the
    /// <c>--no-prefilter</c> audit evaluator must return the same result set over a sample.
    ///
    /// <para>Normal mode uses the T1 accept-only probe, the T2 must-goal early exit and the region
    /// restriction. Audit mode uses none of them and measures every goal over the whole 10,500 m disc.
    /// Two things are compared:</para>
    /// <list type="number">
    ///   <item>the PASS/FAIL verdict, on every seed, for every query - this must be identical, always;</item>
    ///   <item>the measured value of every goal that neither mode reported as bounded - also identical,
    ///         because the only approximation the engine allows is the region restriction, and it marks
    ///         what it touched.</item>
    /// </list>
    /// </summary>
    public static class PrefilterParity
    {
        public static void Run(Action<bool, string, string> check, int seedsPerQuery)
        {
            foreach (KeyValuePair<string, string> q in Queries())
            {
                Compare(check, q.Key, q.Value, seedsPerQuery);
            }
        }

        private static void Compare(Action<bool, string, string> check, string name, string json, int n)
        {
            Query fast = QueryReader.Parse(json, name);
            Query slow = QueryReader.Parse(json, name);
            ILocationOracle oracle = UnavailableLocationOracle.Instance;
            CompiledQuery cqFast = CompiledQuery.Compile(fast, oracle, noPrefilter: false);
            CompiledQuery cqSlow = CompiledQuery.Compile(slow, oracle, noPrefilter: true);

            SeedEvaluator evFast = new SeedEvaluator(cqFast, oracle);
            SeedEvaluator evSlow = new SeedEvaluator(cqSlow, oracle);

            int verdictDiff = 0, valueDiff = 0, compared = 0, passes = 0, boundedSkips = 0;
            string firstDiff = "";

            ulong x = 0xD1B54A32D192ED03UL ^ (ulong)name.Length;
            for (int i = 0; i < n; i++)
            {
                x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                int seed = unchecked((int)x);

                SeedResult a = evFast.Evaluate(seed);
                SeedResult b = evSlow.Evaluate(seed);
                compared++;
                if (a.Pass) passes++;

                if (a.Pass != b.Pass)
                {
                    verdictDiff++;
                    if (firstDiff.Length == 0)
                    {
                        firstDiff = "seed " + seed + ": tiered=" + (a.Pass ? "pass" : "fail")
                                    + " audit=" + (b.Pass ? "pass" : "fail")
                                    + " (failed goal: tiered=" + (a.FailedGoal ?? "-")
                                    + ", audit=" + (b.FailedGoal ?? "-") + ")";
                    }

                    continue;
                }

                // The tiered run may not have measured every goal (that is the point of the early exit),
                // so only goals both runs actually measured are comparable.
                for (int g = 0; g < a.Goals.Count && g < b.Goals.Count; g++)
                {
                    GoalOutcome ga = a.Goals[g], gb = b.Goals[g];
                    if (ga.Bounded || gb.Bounded) { boundedSkips++; continue; }
                    if (double.IsNaN(ga.Value) || double.IsNaN(gb.Value)) continue;
                    if (!a.Pass && ga.Id != a.FailedGoal && !ga.Pass) continue;   // unmeasured after the exit
                    if (ga.Value != gb.Value)
                    {
                        valueDiff++;
                        if (firstDiff.Length == 0)
                        {
                            firstDiff = "seed " + seed + " goal " + ga.Id + ": tiered=" + ga.Value
                                        + " audit=" + gb.Value;
                        }
                    }
                }
            }

            check(verdictDiff == 0 && valueDiff == 0,
                  "parity: " + name,
                  compared + " seeds, " + passes + " hits, " + verdictDiff + " verdict differences, "
                  + valueDiff + " value differences"
                  + (boundedSkips > 0 ? ", " + boundedSkips + " bounded values not compared" : "")
                  + (firstDiff.Length > 0 ? "  <<< " + firstDiff : ""));

            if (evFast.ProbeAccepts + evFast.EarlyExits == 0)
            {
                check(true, "  (note) " + name + " exercised no prefilter",
                      "no T1 accepts and no T2 early exits, so this case only shows the region restriction");
            }
            else
            {
                check(true, "  " + name + " prefilters that fired",
                      evFast.ProbeAccepts + " T1 accepts, " + evFast.EarlyExits + " T2 early exits, "
                      + evFast.Pregenerated + " of " + compared + " seeds needed pre-generation");
            }
        }

        /// <summary>
        /// Queries chosen so that between them they fire every prefilter the engine has: a T1
        /// accept-only probe (a pure presence goal), a T2 early exit (a cheap must-goal in front of an
        /// expensive height goal), and the region restriction (a radius-bounded goal).
        /// </summary>
        private static Dictionary<string, string> Queries()
        {
            return new Dictionary<string, string>
            {
                ["presence (fires the T1 probe)"] = @"{
                  ""version"": 1, ""search"": { ""grid"": 384 },
                  ""goals"": [
                    { ""id"": ""swamp"", ""target"": ""biome:Swamp"", ""metric"": ""present"", ""test"": ""at_least"", ""value"": 1, ""importance"": ""must"" },
                    { ""id"": ""plains"", ""target"": ""biome:Plains"", ""metric"": ""present"", ""test"": ""at_least"", ""value"": 1, ""importance"": ""must"" }
                  ]}",

                ["biome areas (T2 only)"] = @"{
                  ""version"": 1, ""search"": { ""grid"": 384 },
                  ""goals"": [
                    { ""id"": ""mist"", ""target"": ""biome:Mistlands"", ""metric"": ""area"", ""test"": ""at_least"", ""value"": 63000000, ""importance"": ""must"" },
                    { ""id"": ""ocean"", ""target"": ""world:ocean_share"", ""metric"": ""ocean_share"", ""test"": ""at_most"", ""value"": 0.35, ""importance"": ""nice"" }
                  ]}",

                ["region restriction (radius goals)"] = @"{
                  ""version"": 1, ""search"": { ""grid"": 256 },
                  ""goals"": [
                    { ""id"": ""bf-near"", ""target"": ""biome:BlackForest"", ""metric"": ""area_within"", ""radius"": 1500, ""test"": ""at_least"", ""value"": 1500000, ""importance"": ""must"" },
                    { ""id"": ""swamp-far"", ""target"": ""biome:Swamp"", ""metric"": ""nearest_distance"", ""test"": ""at_least"", ""value"": 2150, ""importance"": ""must"" }
                  ]}",

                ["T2 gate in front of heights"] = @"{
                  ""version"": 1, ""search"": { ""grid"": 384 },
                  ""goals"": [
                    { ""id"": ""mountain"", ""target"": ""biome:Mountain"", ""metric"": ""area"", ""test"": ""at_least"", ""value"": 12000000, ""importance"": ""must"" },
                    { ""id"": ""peak"", ""target"": ""world:highest_peak"", ""metric"": ""highest_peak"", ""test"": ""at_least"", ""value"": 200, ""importance"": ""must"" },
                    { ""id"": ""land"", ""target"": ""world:land_share"", ""metric"": ""land_share"", ""test"": ""at_least"", ""value"": 0.2, ""importance"": ""nice"" }
                  ]}",

                ["rivers and lakes (T4)"] = @"{
                  ""version"": 1, ""search"": { ""grid"": 512 },
                  ""goals"": [
                    { ""id"": ""rivers"", ""target"": ""world:river_count"", ""metric"": ""river_count"", ""test"": ""at_least"", ""value"": 145, ""importance"": ""must"" },
                    { ""id"": ""lakes"", ""target"": ""world:lake_count"", ""metric"": ""lake_count"", ""test"": ""at_least"", ""value"": 115, ""importance"": ""nice"" }
                  ]}",
            };
        }
    }
}
