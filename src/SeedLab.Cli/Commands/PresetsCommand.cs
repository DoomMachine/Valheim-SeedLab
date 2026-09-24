using System;
using System.Collections.Generic;
using SeedLab.Cli.Infra;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed presets</c> - the shipped queries, and their source, so that the criteria language is
    /// something the user edits rather than something they are given.
    /// </summary>
    public static class PresetsCommand
    {
        public const string Help = @"vseed presets list           the shipped queries, and which ones this build can run
vseed presets show <name>    print a preset's source - redirect it to a file and edit it

  A preset is an ordinary query file. 'vseed presets show gentle-start > mine.json' gives a
  working starting point; 'vseed search --schema' documents every field.";

        public static int Run(Args a, Out o)
        {
            string sub = a.Positional.Count > 0 ? a.Positional[0] : "list";
            a.RejectUnknown();

            switch (sub)
            {
                case "list":
                    return List(o);
                case "show":
                    if (a.Positional.Count < 2) throw new CliException("which preset?", ExitCodes.Usage, Help);
                    string? text = Presets.Read(a.Positional[1]);
                    if (text == null)
                    {
                        throw new CliException("no preset called '" + a.Positional[1] + "'.", ExitCodes.NotFound,
                            "presets: " + string.Join(", ", Presets.Names));
                    }

                    Console.Out.Write(text);
                    return ExitCodes.Ok;
                default:
                    throw new CliException("unknown sub-command '" + sub + "'.", ExitCodes.Usage, Help);
            }
        }

        private static int List(Out o)
        {
            // The oracle is opened once so the list can print what a preset actually COSTS: a query
            // that places locations runs a prefix of ZoneSystem's ordered list, and that prefix is the
            // difference between ~10,000 seeds/s and ~8.
            ILocationOracle oracle = SearchCommand.Oracle(out string? oracleProblem);

            List<string[]> rows = new List<string[]>();
            foreach (string name in Presets.Names)
            {
                (string desc, bool needs) = Presets.Describe(name);
                string status;
                if (!needs)
                {
                    status = "biomes only";
                }
                else if (!oracle.Available)
                {
                    status = "needs locations";
                }
                else
                {
                    int prefix = 0, total = 0;
                    try
                    {
                        CompiledQuery cq = CompiledQuery.Compile(Presets.Load(name), oracle);
                        prefix = cq.Locations?.PrefixLength ?? 0;
                        total = cq.Locations?.OrderedCount ?? 0;
                    }
                    catch (QueryException)
                    {
                        // Reported by the compile checks in the test suite; the listing stays useful.
                    }

                    status = total > 0 ? "locations " + prefix + "/" + total : "locations";
                }

                rows.Add(new[] { name, status, desc });
            }

            if (o.Json)
            {
                o.J.WriteStartArray();
                foreach (string[] r in rows)
                {
                    o.J.WriteStartObject();
                    o.J.WriteString("name", r[0]);
                    // This compared the status column against "runs now", a string this command has
                    // never produced - the three it writes are "biomes only", "needs locations" and
                    // "locations N/183" - so 'vseed presets list --json' reported runs_now: false for
                    // every preset, including the ones that run. The human table said one thing and
                    // the machine-readable output said another, which is the failure mode --json
                    // exists to avoid. A preset runs unless it is waiting for the location table.
                    o.J.WriteBoolean("runs_now", r[1] != "needs locations");
                    o.J.WriteBoolean("needs_locations", r[1] != "biomes only");
                    o.J.WriteString("status", r[1]);
                    o.J.WriteString("description", r[2]);
                    o.J.WriteEndObject();
                }

                o.J.WriteEndArray();
                o.Flush();
                return ExitCodes.Ok;
            }

            o.Header("Presets");
            o.Table(new[] { "name", "status", "what it looks for" }, rows);
            o.Note("");
            o.Note("\"biomes only\" runs off the sampling grid alone - thousands of seeds a second.");
            o.Note("\"locations N/183\" places N of ZoneSystem's ordered location entries on every seed,");
            o.Note("which is what a boss, trader, dungeon or village goal costs. The floor under any of");
            o.Note("them is the 2048^2 biome-and-height point grid the candidate-zone draw reads: about");
            o.Note("1.9 s per seed per thread, whatever N is.");
            if (oracleProblem != null)
            {
                o.Note("");
                o.Note("location goals are unavailable: " + oracleProblem);
            }

            o.Note("");
            o.Note("vseed presets show <name> > mine.json   then edit and:  vseed search mine.json");
            o.Flush();
            return ExitCodes.Ok;
        }
    }
}
