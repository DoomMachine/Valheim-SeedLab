using System;
using System.Collections.Generic;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// The criteria language: every shipped preset must parse and compile, a query must canonicalise
    /// and hash stably (the checkpoint's identity depends on it), and a goal that needs the dumped
    /// location table must be reported as unavailable rather than silently skipped.
    /// </summary>
    public static class QueryChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            ILocationOracle oracle = UnavailableLocationOracle.Instance;

            int ok = 0, needLocations = 0;
            List<string> broken = new List<string>();
            foreach (string name in Presets.Names)
            {
                try
                {
                    Query q = Presets.Load(name);
                    CompiledQuery c = CompiledQuery.Compile(q, oracle);
                    ok++;
                    if (c.Unavailable.Count > 0) needLocations++;

                    foreach (CompiledGoal g in c.Unsatisfiable)
                    {
                        if (g.Goal.Importance == Importance.Must)
                        {
                            broken.Add(name + " has an impossible must-goal: " + g.Goal.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    broken.Add(name + ": " + ex.Message);
                }
            }

            check(broken.Count == 0, "every shipped preset parses and compiles",
                  ok + " of " + Presets.Names.Count + " presets, " + needLocations + " need the location table"
                  + (broken.Count > 0 ? "  <<< " + string.Join(" | ", broken) : ""));

            // A location goal must be reported, not silently dropped.
            Query loc = QueryReader.Parse(@"{ ""version"": 1, ""goals"": [
                { ""id"": ""elder"", ""target"": ""location:Eikthyrnir"", ""metric"": ""nearest_distance"",
                  ""test"": ""at_most"", ""value"": 2000, ""importance"": ""must"" } ] }", "test");
            CompiledQuery cl = CompiledQuery.Compile(loc, oracle);
            check(cl.Unavailable.Count == 1 && cl.Unavailable[0].UnavailableReason != null,
                  "a location goal is reported unavailable, with a reason",
                  cl.Unavailable.Count == 1 ? cl.Unavailable[0].UnavailableReason ?? "" : "NOT REPORTED");

            // The hash is the checkpoint's identity: it must ignore formatting and follow the goals.
            Query a1 = QueryReader.Parse(@"{""version"":1,""goals"":[{""id"":""g"",""target"":""biome:Swamp"",
                ""metric"":""area"",""test"":""at_least"",""value"":1000,""importance"":""must""}]}", "a");
            Query a2 = QueryReader.Parse("{\n  \"version\": 1,\n  \"goals\": [ {\n  \"id\": \"g\",\n"
                + "  \"target\": \"biome:Swamp\", \"metric\": \"area\", \"test\": \"at_least\",\n"
                + "  \"value\": 1000, \"importance\": \"must\" } ]\n}", "b");
            Query a3 = QueryReader.Parse(@"{""version"":1,""goals"":[{""id"":""g"",""target"":""biome:Swamp"",
                ""metric"":""area"",""test"":""at_least"",""value"":1001,""importance"":""must""}]}", "c");
            string h1 = QueryReader.Hash(a1), h2 = QueryReader.Hash(a2), h3 = QueryReader.Hash(a3);
            check(h1 == h2 && h1 != h3, "the query hash ignores formatting and follows the thresholds",
                  h1.Substring(0, 12) + "... == " + h2.Substring(0, 12) + "..., != " + h3.Substring(0, 12) + "...");

            // A bad query must fail loudly at parse time, not produce an empty result set at runtime.
            int refused = 0;
            string[] bad =
            {
                @"{""version"":1,""goals"":[]}",
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""biome:Nowhere"",""metric"":""area"",""test"":""at_least"",""value"":1,""importance"":""must""}]}",
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""biome:Swamp"",""metric"":""not_a_metric"",""test"":""at_least"",""value"":1,""importance"":""must""}]}",
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""biome:Swamp"",""metric"":""area_within"",""test"":""at_least"",""value"":1,""importance"":""must""}]}",
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""biome:Swamp"",""metric"":""area"",""test"":""at_least"",""value"":1,""importance"":""maybe""}]}",
            };
            foreach (string b in bad)
            {
                try
                {
                    Query q = QueryReader.Parse(b, "bad");
                    CompiledQuery.Compile(q, oracle);
                }
                catch (QueryException)
                {
                    refused++;
                }
            }

            check(refused == bad.Length, "malformed queries are refused at compile time",
                  refused + " of " + bad.Length + " refused");

            // A goal no seed can satisfy must be caught statically, before 4.29 billion worlds are built.
            Query impossible = QueryReader.Parse(@"{""version"":1,""goals"":[{""id"":""g"",
                ""target"":""biome:Meadows"",""metric"":""area"",""test"":""at_least"",
                ""value"":400000000,""importance"":""must""}]}", "impossible");
            CompiledQuery ci = CompiledQuery.Compile(impossible, oracle);
            check(ci.Unsatisfiable.Count == 1, "a goal larger than the world is rejected statically (T0)",
                  ci.Unsatisfiable.Count == 1 ? ci.Unsatisfiable[0].Unsatisfiable ?? "" : "NOT CAUGHT");
        }
    }
}
