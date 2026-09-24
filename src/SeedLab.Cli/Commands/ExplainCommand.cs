using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Cli.Infra;
using SeedLab.Search.Criteria;
using SeedLab.Search.Execution;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;
using SeedLab.Search.Output;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed explain</c> - why this seed did or did not match.
    ///
    /// <para>This is the feature that turns a search from a slot machine into a tool. It measures every
    /// goal on the named seed - <b>all of them, with no early exit and no region restriction</b>, so
    /// the numbers are the real ones and not bounds - and prints, per goal, the measured value, the
    /// threshold, the margin, and the tier that decided it.</para>
    /// </summary>
    public static class ExplainCommand
    {
        public const string Help = @"vseed explain <seed> <query.json | preset-name> [options]

  Measures every goal in the query against one seed and prints why it passed or failed.
  Nothing is skipped and nothing is region-restricted, so every number is the exact one.

  --text / --int         read the seed token as a seed text / as an int32
  --grid <m>             override the query's sampling grid
  --json                 machine-readable output

  The grid is decided the same way 'vseed search' decides it, by the same code: if the
  query has a must-have goal no coarse grid measures safely, the explanation is measured
  at the game's own 12 m and says so - otherwise this command would explain a match the
  search would not have found, or fail to explain one it did.

  A seed token that parses as an int32 is read as the INT. Pass --text to mean the text.";

        public static int Run(Args a, Out o, CliRuntime rt)
        {
            if (a.Positional.Count < 2)
            {
                throw new CliException("a seed and a query are required.", ExitCodes.Usage,
                    "vseed explain <seed> <query.json|preset>");
            }

            SeedRef seed = SeedArg.Resolve(a, a.Positional[0]);
            Query q = SearchCommand.LoadQuery(a.Positional[1]);
            if (a.Has("grid")) q.Search.Grid = a.Double("grid", q.Search.Grid);
            a.RejectUnknown();

            if (seed.AmbiguityNote != null) Out.Warn(seed.AmbiguityNote);

            ILocationOracle oracle = SearchCommand.Oracle(out string? oracleProblem);
            if (oracleProblem != null) Out.Warn("location goals are unavailable: " + oracleProblem);

            // The SAME assembly 'vseed search' runs through, so the grid this explanation measures on
            // is the grid the search measured on. A budget of one seed and acceptScanOrder, because
            // neither the scan plan nor the ranking rule means anything for a single named seed - what
            // is wanted from the session is its GRID DECISION and its warnings, not its scan.
            // Start() is never called: explain opens no file, writes no checkpoint and keeps nothing.
            SearchSession session;
            try
            {
                session = SearchSession.Create(q, oracle, Verified.EngineVersion, 1, 1,
                                               outPath: null, noPrefilter: true, acceptScanOrder: true);
            }
            catch (QueryException ex)
            {
                throw new CliException(ex.Message, ExitCodes.Usage, ex.Hint);
            }

            // A grid raise is already REAL here: SearchSession.Create recompiles the query at the raised
            // grid and returns that session, with RaisedFrom set (the fix that retired the CLI's own
            // ApplyGridUpgrade). This command used to rebuild the session a second time as if it had
            // not, and the rebuild was worse than redundant: handed a query whose location targets were
            // already prefabs, it produced no name notes, so "'The Elder' is the display name of
            // GDKing" vanished from explain on every query that raised its grid. Only the notes that
            // explain the raise are printed here, as before.
            if (session.Grid.RaisedFrom > 0)
            {
                foreach (string n in session.Grid.Notes) Out.Warn(n);
            }

            if (!session.Preflight.Ok)
            {
                // A goal that cannot be answered is refused here for the same reason the search
                // refuses it: a number measured against a goal the build cannot evaluate is not an
                // explanation.
                Console.Error.WriteLine("vseed explain: this query cannot be answered by this build.");
                foreach (string refusal in session.Preflight.Refusals)
                {
                    Console.Error.WriteLine("  " + SearchCommand.Wrap(refusal, "      "));
                }

                return ExitCodes.CheckFailed;
            }

            CompiledQuery cq = session.Compiled;

            // The preflight's warnings are written for a SCAN; SearchPreflight.WarningsForOneSeed
            // keeps the ones about goals and grids and drops the ones about block size, checkpoints
            // and the results file. That list already holds every compiled-query warning (the session
            // copies them in), so it is the ONLY loop: a second one over cq.Warnings used to print each
            // grid, region and prefix warning twice.
            foreach (string w in session.Preflight.WarningsForOneSeed())
            {
                Out.Warn(SearchCommand.Wrap(w, "         "));
            }

            SeedEvaluator ev = new SeedEvaluator(cq, oracle);
            SeedResult r = ev.Evaluate(seed.Seed, full: true);
            r.SeedText = seed.Text ?? SeedLab.Seeds.SeedText.Invert(seed.Seed, SeedLab.Seeds.SeedAlphabet.Alnum62);

            if (o.Json)
            {
                WriteJson(o, q, cq, r);
                o.Flush();
                return r.Pass ? ExitCodes.Ok : ExitCodes.CheckFailed;
            }

            o.Header("Seed " + r.Seed + (r.SeedText != null ? "  (type \"" + r.SeedText + "\")" : ""));
            o.Field("query", (q.Name ?? "(unnamed)") + (q.Description != null ? " - " + q.Description : ""));
            o.Field("grid", SearchGrids.Describe(cq.Grid)
                            + (session.Grid.RaisedFrom > 0
                                ? "  <- raised from G" + session.Grid.RaisedFrom.ToString(
                                      "0.###", CultureInfo.InvariantCulture)
                                  + ", the grid the query asked for, because a must-have goal is one no "
                                  + "coarse grid measures safely"
                                : ""));
            o.Field("verdict", r.Pass ? "MATCHES every must-have" : "REJECTED by '" + r.FailedGoal + "'");
            o.Field("score", Out.F(r.Score, 3) + (cq.HasNiceGoals ? "" : "  (no nice-to-have goals, so every match scores 1)"));

            o.Header("Goal by goal");
            List<string[]> rows = new List<string[]>();
            foreach (GoalOutcome g in r.Goals)
            {
                string verdict = g.Unavailable != null ? "no data"
                               : g.Pass ? "pass" : "FAIL";
                rows.Add(new[]
                {
                    g.Id,
                    g.Metric,
                    g.Importance == Importance.Must ? "must" : "nice x" + Out.F(g.Weight, 1),
                    Test(g),
                    SearchCommand.Value(g.Value, g.Unit),
                    verdict,
                    MarginText(g),
                    g.Importance == Importance.Nice ? Out.F(g.Score, 3) : "",
                    SearchCommand.TierName(g.Tier),
                });
            }

            o.Table(new[] { "id", "metric", "importance", "wanted", "measured", "verdict", "by", "s", "tier" },
                    rows, new[] { false, false, false, false, true, false, true, true, false });

            foreach (GoalOutcome g in r.Goals)
            {
                if (g.Unavailable != null)
                {
                    o.Note("");
                    o.Note(g.Id + ": " + g.Unavailable);
                }

                if (g.Unsatisfiable != null)
                {
                    o.Note("");
                    o.Note(g.Id + ": no seed can satisfy this. " + g.Unsatisfiable);
                }
            }

            // The m_unique semantics, spelled out per goal. A trader has no position the seed decides,
            // so a report that printed a bare distance for one would be claiming something it cannot
            // know - see PlacementResult.NotPredictable.
            bool anyUnique = false;
            foreach (GoalOutcome g in r.Goals)
            {
                if (g.UniqueSemantics == null) continue;
                if (!anyUnique)
                {
                    o.Header("What these numbers mean for an m_unique type");
                    anyUnique = true;
                }

                o.Note(g.Id + " (" + g.Metric + "): " + g.UniqueSemantics);
            }

            if (anyUnique)
            {
                o.Note("");
                o.Note("Which candidate survives is decided by the first zone a player generates and then");
                o.Note("RemoveUnplacedLocations deletes the rest. That is exploration order, not the seed,");
                o.Note("so no offline tool can name it. Use all_candidates_distance for a guarantee.");
            }

            // The other half of "this coordinate is not a promise", and a separate header on purpose:
            // an m_unique caveat is about WHICH of these positions is real, and this one is about what
            // is inside a position that certainly is. The axe-head houses are not m_unique, so nothing
            // above would have said a word about them.
            bool anyContents = false;
            foreach (GoalOutcome g in r.Goals)
            {
                if (g.ContentsNote == null) continue;
                if (!anyContents)
                {
                    o.Header("What the seed does NOT decide about what is inside");
                    anyContents = true;
                }

                o.Note(g.Id + " (" + g.Metric + "): " + g.ContentsNote);
            }

            if (cq.Locations != null)
            {
                o.Header("Location placement");
                o.Field("table", cq.LocationProvenance);
                o.Field("prefix run", cq.Locations.PrefixLength + " of " + cq.Locations.OrderedCount
                                      + " ordered entries (explain always runs the whole prefix)");
                o.Field("prefabs", string.Join(", ", cq.Locations.Prefabs));
            }

            if (!r.Pass)
            {
                o.Header("Why not");
                foreach (GoalOutcome g in r.Goals)
                {
                    if (g.Importance != Importance.Must || g.Pass) continue;
                    if (g.Unavailable != null)
                    {
                        o.Note(g.Id + ": could not be measured - " + g.Unavailable);
                        continue;
                    }

                    o.Note(g.Id + ": wanted " + Test(g) + ", measured " + SearchCommand.Value(g.Value, g.Unit)
                           + " - short by " + SearchCommand.Value(Math.Abs(g.Margin), g.Unit) + ".");
                }
            }

            o.Note("");
            o.Note("Measured with no prefilter and no region restriction: every value above is exact on this grid.");
            o.Flush();
            return r.Pass ? ExitCodes.Ok : ExitCodes.CheckFailed;
        }

        private static string Test(GoalOutcome g) => g.Test switch
        {
            GoalTest.Near => "<= " + SearchCommand.Value(g.Threshold, g.Unit),
            GoalTest.AtMost => "<= " + SearchCommand.Value(g.Threshold, g.Unit),
            GoalTest.Far => ">= " + SearchCommand.Value(g.Threshold, g.Unit),
            GoalTest.AtLeast => ">= " + SearchCommand.Value(g.Threshold, g.Unit),
            _ => SearchCommand.Value(g.Threshold, g.Unit) + " .. " + SearchCommand.Value(g.ThresholdMax, g.Unit),
        };

        private static string MarginText(GoalOutcome g)
        {
            if (double.IsNaN(g.Margin)) return "";
            string sign = g.Margin >= 0 ? "+" : "-";
            return sign + SearchCommand.Value(Math.Abs(g.Margin), g.Unit);
        }

        private static void WriteJson(Out o, Query q, CompiledQuery cq, SeedResult r)
        {
            o.J.WriteStartObject();
            o.J.WriteNumber("seed", r.Seed);
            if (r.SeedText != null) o.J.WriteString("text", r.SeedText);
            o.J.WriteString("query", q.Name ?? "(unnamed)");
            o.J.WriteString("query_hash", QueryReader.Hash(q));
            o.J.WriteNumber("defs", q.Defs);
            o.J.WriteNumber("grid_m", q.Search.Grid);
            o.J.WriteBoolean("pass", r.Pass);
            o.J.WriteNumber("score", r.Score);
            if (r.FailedGoal != null) o.J.WriteString("failed_goal", r.FailedGoal);
            o.J.WriteStartArray("goals");
            foreach (GoalOutcome g in r.Goals)
            {
                o.J.WriteStartObject();
                o.J.WriteString("id", g.Id);
                o.J.WriteString("target", g.Target);
                o.J.WriteString("metric", g.Metric);
                o.J.WriteString("test", g.Test.ToString().ToLowerInvariant());
                o.J.WriteString("importance", g.Importance.ToString().ToLowerInvariant());
                o.J.WriteNumber("threshold", g.Threshold);
                if (g.Test == GoalTest.Between) o.J.WriteNumber("threshold_max", g.ThresholdMax);
                if (double.IsFinite(g.Value)) o.J.WriteNumber("value", g.Value);
                else o.J.WriteNull("value");
                o.J.WriteString("unit", ResultWriter.UnitName(g.Unit));
                o.J.WriteBoolean("pass", g.Pass);
                if (double.IsFinite(g.Margin)) o.J.WriteNumber("margin", g.Margin);
                if (g.Importance == Importance.Nice)
                {
                    o.J.WriteNumber("s", g.Score);
                    o.J.WriteNumber("weight", g.Weight);
                }

                o.J.WriteString("tier", g.Tier.ToString().ToLowerInvariant());
                if (g.Unavailable != null) o.J.WriteString("unavailable", g.Unavailable);
                if (g.Unsatisfiable != null) o.J.WriteString("unsatisfiable", g.Unsatisfiable);
                if (g.UniqueSemantics != null) o.J.WriteString("unique_semantics", g.UniqueSemantics);
                if (g.ContentsNote != null) o.J.WriteString("contents_note", g.ContentsNote);
                o.J.WriteEndObject();
            }

            o.J.WriteEndArray();
            o.J.WriteEndObject();
        }
    }
}
