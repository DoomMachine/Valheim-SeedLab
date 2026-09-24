using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Search.Criteria;

namespace SeedLab.Search.Evaluation
{
    /// <summary>What a coarse grid does to a goal, and therefore what the engine is allowed to do with it.</summary>
    public enum GridSafety
    {
        /// <summary>The grid does not touch this goal at all. Location placement uses the game's own
        /// hard-coded 2048 x 2048 @ 12 m point grid, so a location goal is exact at any setting.</summary>
        GridIndependent,

        /// <summary>Counting metrics: a coarse pass plus a measured margin loses nothing, then every
        /// survivor is re-measured exactly. Zero false negatives on 2,560 seeds at G24 + 1 %.</summary>
        ScreenWithMargin,

        /// <summary>Connectivity or extremum metrics: no margin is both safe and selective at any
        /// grid, so the goal is measured at the game's own 12 m grid and nowhere else.</summary>
        FineOnly,
    }

    /// <summary>
    /// The per-goal grid policy, with the measured numbers it rests on.
    ///
    /// <para>Source: <c>docs\studies\resolution-study.md</c>, 2,560 seeds (islands: 512),
    /// measured 2026-09-23 with SeedLab's own measurement code. Two results decide everything here:</para>
    ///
    /// <list type="number">
    /// <item><b>Bulk counting metrics converge.</b> Biome area, share, land/ocean share, <c>area_within</c>:
    /// a <b>1 % margin at G24 or G48</b> lost zero of 2,560 seeds on all eleven goals tested, at 1.2x
    /// survivors to re-measure. G96 needs 5 %. G164 needs 10 %, G384 needs 30 % - at which point 90 %
    /// of seeds survive the screen and the filter has stopped filtering, so G96 is the coarsest grid
    /// this policy will ever choose for a must-have.</item>
    /// <item><b>Connectivity and extrema do not.</b> <c>largest_island_area</c> at G24 loses 32 of 643
    /// true matches at 0 % and still 14 at 10 %; zero needs 35 %, which passes 2,461 of 2,560.
    /// <c>spawn_island_area</c> never reaches zero at any margin tested (50 % at G24 still lost 8).
    /// <c>island_count</c> needs 20 %, which passes 2,480+, and it is
    /// non-monotone in the grid. <c>highest_peak >= 450 m</c> loses 31.7 %
    /// at G48 and 82.3 % at G96. <c>shore_area_within</c> is identically zero at G164 and coarser,
    /// where one cell is wider than the 100 m band it measures.</item>
    /// </list>
    ///
    /// <para><b>And the cost twist that makes the second row cheap to do properly.</b> Every metric in
    /// it needs <c>GetBiomeHeight</c>, which needs the lake/river/stream pre-generation: a fixed
    /// 570-617 ms per seed that no grid can reduce. Measured whole-world per-seed CPU: biome-only
    /// 1,324 ms at G12 against 1.14 ms at G384 (1,162x), but heights 4,210 ms against 623 ms - only
    /// <b>6.8x</b>. Coarsening an island or peak query saves 6.8x and is the only place it silently
    /// loses matches. So those goals run at G12, and the policy says why.</para>
    /// </summary>
    public sealed class GoalGridPolicy
    {
        public GoalGridPolicy(string metric, GridSafety safety, string why)
        {
            Metric = metric;
            Safety = safety;
            Why = why;
        }

        public string Metric { get; }

        public GridSafety Safety { get; }

        /// <summary>The measured sentence the CLI and the GUI print next to the goal.</summary>
        public string Why { get; }

        /// <summary>The coarsest grid this goal may be screened on, metres. 12 for a fine-only goal.</summary>
        public double CoarsestScreenGrid => Safety == GridSafety.ScreenWithMargin ? 96.0 : 12.0;
    }

    /// <summary>The grid decision for a whole query, and the sentences that explain it.</summary>
    public sealed class GridPlan
    {
        /// <summary>The grid every surviving seed is finally measured on. The definitional grid.</summary>
        public double VerifyGrid = 12.0;

        /// <summary>The coarse screening grid, or 0 when there is no screening pass.</summary>
        public double ScreenGrid;

        /// <summary>The relative margin applied to every screened threshold, e.g. 0.01 for 1 %.</summary>
        public double Margin;

        public bool ScreenThenVerify => ScreenGrid > 0;

        /// <summary>Why this plan, in the user's words, with the measurement behind it.</summary>
        public List<string> Notes = new List<string>();

        /// <summary>Goals that forced the fine grid, with the reason each one gives.</summary>
        public List<string> FineOnlyGoals = new List<string>();

        /// <summary>
        /// Must-have goals that a coarse grid measures wrongly. When auto-pick is on, the grid is
        /// raised to the game's own 12 m for them and <see cref="RaisedFrom"/> records what it was;
        /// with <c>screen: off</c> the query's grid is honoured and these become plain warnings.
        /// These are the defects the resolution study found in the shipped presets.
        /// </summary>
        public List<string> UnsafeMusts = new List<string>();

        /// <summary>The grid the query asked for, when auto-pick raised it to 12 m. 0 when it did not.</summary>
        public double RaisedFrom;

        public string Describe()
        {
            if (!ScreenThenVerify)
            {
                return "measure once at G" + G(VerifyGrid);
            }

            return "screen at G" + G(ScreenGrid) + " with a "
                   + (Margin * 100).ToString("0.##", CultureInfo.InvariantCulture)
                   + " % margin, then re-measure every survivor at G" + G(VerifyGrid);
        }

        private static string G(double g) => g.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The measured policy table and the auto-pick that reads it.
    ///
    /// <para>This is the resolution study encoded as code rather than as advice, so that a query
    /// cannot quietly be run at a grid the study showed loses half of its matches - which is what two
    /// of the shipped presets were doing.</para>
    /// </summary>
    public static class GridPolicy
    {
        private const string BulkWhy =
            "a counting metric: coarse + margin + exact re-measure lost zero of 2,560 seeds at G24 with "
            + "a 1 % margin (G96 needs 5 %); never screened coarser than G96, where 90 % of seeds "
            + "survive and the screen stops filtering";

        private const string IslandWhy =
            "connectivity, not counting: coarse grids merge islands across straits. largest_island >= 6 km2 "
            + "loses 32 of 643 true matches at G24 and needs a 35 % margin to lose none, which passes "
            + "2,461 of 2,560 seeds. Measured at G12 only - and that costs 6.8x, not 1,000x, because the "
            + "river pre-generation dominates a height query";

        private const string SpawnIslandWhy =
            "the worst metric in the tool at a coarse grid: at G96, 50 % of the seeds a 3 km2 must-have "
            + "reports are not matches at G12, and no margin reaches zero false negatives (50 % at G24 "
            + "still lost 8 of 802). G12 only";

        private const string PeakWhy =
            "an extremum: highest_peak >= 450 m loses 31.7 % of true matches at G48 and 82.3 % at G96. "
            + "G24 with a 3-5 % margin at the loosest, G12 for a must-have";

        private const string ShoreWhy =
            "the 100 m shore band is a fixed physical width, so a grid whose cells approach it stops "
            + "resolving it: measured over 512 seeds the value is 0.939x its own G12 value at G24, "
            + "0.793x at G48, 0.724x at G96 and exactly 0 at G164 and coarser. A zero-false-negative "
            + "screen needs 8.5-10.4 % at G24 and 23-27 % at G48, where the screen stops filtering, so "
            + "the goal itself is decided at the game's own 12 m grid";

        private const string NearestBiomeWhy =
            "a distance to the nearest cell of a biome: safe with a metric margin (<= 46 m at G24), "
            + "except for Mountain, whose margin is 927 m at G24 and 3,015 m at G384 - usually larger "
            + "than the goal itself";

        private const string LocationWhy =
            "location placement draws from the game's own hard-coded 2048 x 2048 @ 12 m point grid "
            + "(BiomeGrid.Size = 2048), so --grid does not touch this goal: it is exact at every setting";

        /// <summary>The policy for one compiled goal.</summary>
        public static GoalGridPolicy For(CompiledGoal g)
        {
            if (g.Def.NeedsLocations || g.Goal.Target.Kind == TargetKind.Location
                || g.Goal.Target.Kind == TargetKind.Group)
            {
                return new GoalGridPolicy(g.Goal.Metric, GridSafety.GridIndependent, LocationWhy);
            }

            switch (g.Goal.Metric)
            {
                case "largest_island_area":
                case "island_count":
                case "largest_patch_area":
                    return new GoalGridPolicy(g.Goal.Metric, GridSafety.FineOnly, IslandWhy);

                case "spawn_island_area":
                    return new GoalGridPolicy(g.Goal.Metric, GridSafety.FineOnly, SpawnIslandWhy);

                case "shore_area_within":
                    return new GoalGridPolicy(g.Goal.Metric, GridSafety.FineOnly, ShoreWhy);

                case "highest_peak":
                    return new GoalGridPolicy(g.Goal.Metric, GridSafety.FineOnly, PeakWhy);

                case "area_above_height":
                    // Not a bulk metric: it counts only the tops, so it behaves like highest_peak.
                    // Measured: 'area above 200 m' needs 3 % at G48 and 7 % at G96, against 0.5-1 %
                    // for a true area goal - which is why it gets its own row here.
                    return new GoalGridPolicy(g.Goal.Metric, GridSafety.FineOnly,
                        "counts only high ground, so it behaves like highest_peak rather than like an "
                        + "area: 'area above 200 m' needs a 3 % margin at G48 and 7 % at G96 where a true "
                        + "area goal needs 0.5-1 %");

                case "nearest_distance":
                case "nearest_land_distance":
                    return new GoalGridPolicy(g.Goal.Metric, GridSafety.FineOnly, NearestBiomeWhy);

                default:
                    return new GoalGridPolicy(g.Goal.Metric, GridSafety.ScreenWithMargin, BulkWhy);
            }
        }

        /// <summary>
        /// The margin that removed every false negative on the 2,560-seed sample, per screening grid.
        /// Above G96 there is no entry: the study showed the screen stops filtering there, so the
        /// policy refuses to screen that coarsely rather than offering a 30 % margin that passes
        /// everything.
        /// </summary>
        public static double MarginFor(double screenGrid) => screenGrid switch
        {
            <= 24.0 => 0.01,
            <= 48.0 => 0.01,
            <= 96.0 => 0.05,
            _ => double.NaN,
        };

        /// <summary>The coarsest grid a bulk must-have may be screened on. Decided, not guessed.</summary>
        public const double CoarsestScreen = 96.0;

        /// <summary>
        /// Chooses the grid plan for a compiled query.
        ///
        /// <para>The rule, straight from the study's section 8.1: if any must-have goal is
        /// connectivity or extremal, measure at G12 and say why; otherwise screen at G24 (1 %) and
        /// re-measure survivors at G12. A query of nothing but location goals is grid-independent and
        /// gets the cheapest grid that still answers the side metrics.</para>
        /// </summary>
        public static GridPlan AutoPick(CompiledQuery q, double screenGrid = 24.0)
        {
            // The query's own grid is the DEFINITIONAL grid, and auto-pick starts from it: a user who
            // wrote grid 96 asked for a different measurement, not for a slower one. Two things can
            // change it, and neither is silent. It puts a cheap coarse SCREEN in front of the query's
            // grid when the measured margin makes that lossless, which changes the cost and not the
            // answer. And it RAISES the grid to the game's own 12 m when a must-have goal is one no
            // coarse grid measures safely, which is a change to the answer and is therefore printed,
            // turned into a confirmation by the caller, and overridable with "screen": "off".
            GridPlan plan = new GridPlan { VerifyGrid = q.Query.Search.Grid };
            bool anyFine = false, anyBulk = false, anyGridded = false;

            foreach (CompiledGoal g in q.Goals)
            {
                if (!g.Available) continue;
                GoalGridPolicy p = For(g);
                if (p.Safety == GridSafety.GridIndependent) continue;
                anyGridded = true;

                if (p.Safety == GridSafety.FineOnly)
                {
                    anyFine = true;
                    plan.FineOnlyGoals.Add(g.Goal.Id + " (" + g.Goal.Metric + "): " + p.Why);
                    if (g.Goal.Importance == Importance.Must && q.Query.Search.Grid > 12.0)
                    {
                        plan.UnsafeMusts.Add(g.Goal.Id + " is a MUST-HAVE on " + g.Goal.Metric
                                             + " at G"
                                             + q.Query.Search.Grid.ToString("0.###", CultureInfo.InvariantCulture)
                                             + ", and " + p.Why);
                    }
                }
                else
                {
                    anyBulk = true;
                }
            }

            // Decision 6: auto-pick goes FINER automatically for island and small-feature goals.
            // A must-have that no coarse grid measures safely is measured at the game's own 12 m grid
            // even when the query asked for something coarser - the alternative is a filter that
            // silently both admits seeds that are not matches and throws away ones that are (measured:
            // half of what gentle-start reported at 96 m was not a match at 12 m). It is said out loud,
            // the caller turns it into a confirmation, and "screen": "off" keeps the query's own grid.
            if (plan.UnsafeMusts.Count > 0 && plan.VerifyGrid > 12.0)
            {
                plan.RaisedFrom = plan.VerifyGrid;
                plan.VerifyGrid = 12.0;
                plan.Notes.Add("grid raised from G"
                               + plan.RaisedFrom.ToString("0.###", CultureInfo.InvariantCulture)
                               + " to the game's own G12, because this query has a must-have goal that no "
                               + "coarse grid measures safely. For a height query that is 6.8x, not 1,162x - "
                               + "the river pre-generation dominates either way. Pass \"screen\": \"off\" to "
                               + "keep G"
                               + plan.RaisedFrom.ToString("0.###", CultureInfo.InvariantCulture)
                               + " and accept the measured loss");
            }

            if (!anyGridded)
            {
                plan.Notes.Add("every goal in this query is answered by location placement, which uses the "
                               + "game's own 2048 x 2048 @ 12 m point grid: the sampling grid changes nothing "
                               + "here except the side metrics on the record");
                return plan;
            }

            // A fine-only goal does not stop the SCREEN: it stops the screen from TESTING that goal.
            // The screen tests the query's counting must-haves, which is often the cheap half of the
            // query (a biome area inside a 1.2 km disc needs no pre-generation at all), and the exact
            // pass then decides the island or peak goal on the few survivors. Refusing to screen at
            // all because one goal cannot be screened was leaving the cheapest filter in the tool
            // unused on exactly the queries that need it most.
            if (anyFine && !anyBulk)
            {
                plan.Notes.Add(plan.VerifyGrid <= 12.0
                    ? "measuring at the game's own 12 m grid because this query has a goal that a coarse grid "
                      + "gets wrong, and for a height query that costs 6.8x rather than 1,162x - the river "
                      + "pre-generation dominates either way"
                    : "this query has a goal that no coarse grid measures safely, and it asked for G"
                      + plan.VerifyGrid.ToString("0.###", CultureInfo.InvariantCulture)
                      + ": the number it reports is that grid's number, not the game's");
                return plan;
            }

            if (anyBulk && screenGrid > plan.VerifyGrid)
            {
                if (anyFine)
                {
                    plan.Notes.Add("the screen tests only this query's counting must-haves; its island, peak or "
                                   + "shore goals have no safe coarse margin, so they are decided by the "
                                   + "exact pass alone - on the survivors, which is where they are affordable");
                }

                double margin = MarginFor(screenGrid);
                if (double.IsNaN(margin))
                {
                    plan.Notes.Add("G" + screenGrid.ToString("0.###", CultureInfo.InvariantCulture)
                                   + " is coarser than G96, where a safe margin passes 90 % of all seeds and the "
                                   + "screen stops filtering; screening at G96 with a 5 % margin instead");
                    screenGrid = CoarsestScreen;
                    margin = MarginFor(screenGrid);
                }

                plan.ScreenGrid = screenGrid;
                plan.Margin = margin;
                plan.Notes.Add("every must-have here is a counting metric, so a coarse screen with a measured "
                               + "margin loses nothing: zero false negatives on 2,560 seeds, 1.2x survivors to "
                               + "re-measure. Every survivor is measured again at 12 m before it is written, so "
                               + "the numbers in the results file are the game's own grid's numbers");
            }

            return plan;
        }

        /// <summary>
        /// The relaxed threshold a screening pass must use for a goal, so that no seed which would
        /// qualify exactly is rejected coarsely.
        ///
        /// <para>The direction matters: an "at least" goal relaxes DOWNWARDS and an "at most" goal
        /// relaxes UPWARDS. Getting that backwards would tighten the screen and lose matches, which is
        /// the one failure this whole mechanism exists to prevent.</para>
        /// </summary>
        public static double RelaxedThreshold(GoalTest test, double value, double margin)
        {
            double m = Math.Abs(margin);
            switch (test)
            {
                case GoalTest.AtLeast:
                case GoalTest.Far:
                    return value >= 0 ? value * (1.0 - m) : value * (1.0 + m);
                case GoalTest.AtMost:
                case GoalTest.Near:
                case GoalTest.Between:
                    return value >= 0 ? value * (1.0 + m) : value * (1.0 - m);
                default:
                    return value;
            }
        }
    }
}
