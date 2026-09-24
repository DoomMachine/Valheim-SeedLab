using System;
using System.Collections.Generic;
using SeedLab.Search.Criteria;

namespace SeedLab.Search.Evaluation
{
    /// <summary>One rung of the sampling ladder: may a must-have be decided there, and why.</summary>
    public sealed class GridRungVerdict
    {
        public GridRungVerdict(bool safe, string why)
        {
            Safe = safe;
            Why = why;
        }

        /// <summary>False when a must-have goal stops being a (correct) filter at this rung.</summary>
        public bool Safe { get; }

        /// <summary>
        /// The sentence behind the verdict - never null. The page used to supply its own when this was
        /// null ("a counting metric survives this grid with a margin, and every survivor is re-measured
        /// exactly"), and that sentence was false under <c>screen: off</c> (nothing is screened or
        /// re-measured), at G96, G48 and G24 under auto-pick (the plan measures once there, because the
        /// default screen grid is not coarser than the rung) and for a fine-only must-have at G12 (not
        /// a counting metric). Every rung's reason is now written here.
        /// </summary>
        public string Why { get; }
    }

    /// <summary>
    /// What the web page's sampling ladder says about each grid rung for one query, and the note under
    /// the table - decided HERE, in SeedLab.Search, from the same predicates the CLI's warnings use
    /// (<see cref="GridPolicy.SamplesGrid"/>, <see cref="GridPolicy.IsBulkGridMust"/>,
    /// <see cref="GridPolicy.IsFineOnlyGridMust"/>, <see cref="GridPolicy.OnlyPlacement"/>).
    ///
    /// <para><b>Why it moved out of the web project.</b> Until 2026-09-24 the ladder had its own test
    /// for "the grid decides something" (any available must-have of tier T4 or below) and the page had
    /// its own hard-coded note. So a river-count must-have was marked "no longer a filter" at G384
    /// while the counts come from the generator's own lists; a query whose only grid goals were
    /// nice-to-haves got the flat table's "exact at every rung and costs the same at all of them" over a
    /// cost column that varied by rung; and a nice nearest-biome goal made every rung above G12 say
    /// "auto-pick raises the whole query to G12" when nothing would be raised. The CLI and the page are
    /// required to say the same thing (decision 12), and the only way to make that a property rather
    /// than a convention is one implementation - which also makes it testable, since no test project
    /// references the web assembly.</para>
    ///
    /// <para><b>What it must not claim</b> (review of 2026-09-24): that another rung "decides the same
    /// seeds" - the grid is in the run hash, and a shuffled sample with no <c>search.key</c> takes its
    /// permutation key from that hash, so another rung visits different seeds; the ladder says only
    /// that each seed gets the same must-have verdict. That the query has must-haves or location goals
    /// when it has none. And why a particular fine-only goal is fine-only: the policy's reasons differ
    /// (connectivity, an extremum, high ground that behaves like a peak, a fixed-width shore band, a
    /// distance to the nearest cell), so a rung names the goal and says only what the plan's own coarse
    /// note says - it is one no coarse grid measures safely.</para>
    /// </summary>
    public sealed class GridLadder
    {
        private GridLadder()
        {
        }

        /// <summary>Every value comes from location placement: the table is flat.</summary>
        public bool OnlyPlacement { get; private set; }

        /// <summary>True when at least one must-have goal is measured on the sampling grid.</summary>
        public bool GridDecides { get; private set; }

        public bool ScreenOff { get; private set; }

        /// <summary>True when the query has at least one available must-have goal, of any kind.</summary>
        public bool HasMusts { get; private set; }

        /// <summary>True when the query has at least one available location or group goal.</summary>
        public bool HasLocations { get; private set; }

        /// <summary>Grid-measured must-haves in the bulk counting row (the "past G96" argument).</summary>
        public List<string> BulkMusts { get; } = new List<string>();

        /// <summary>Grid-measured must-haves no coarse grid measures safely.</summary>
        public List<string> FineMusts { get; } = new List<string>();

        /// <summary>Grid-measured nice-to-haves: the must-haves may be exact at a rung while these still rank differently.</summary>
        public List<string> NiceGridGoals { get; } = new List<string>();

        /// <summary>River, lake and stream goals: exact at every rung, but they make the query sample heights.</summary>
        public List<string> StructureGoals { get; } = new List<string>();

        /// <summary>The side metrics the records carry off the grid (<see cref="GridPolicy.RecordSideMetrics"/>).</summary>
        public List<string> SideMetrics { get; private set; } = new List<string>();

        /// <summary>The note printed under the ladder table.</summary>
        public string TableNote { get; private set; } = "";

        public static GridLadder For(CompiledQuery cq)
        {
            GridLadder l = new GridLadder
            {
                OnlyPlacement = GridPolicy.OnlyPlacement(cq),
                ScreenOff = cq.Query.Search.Screen == ScreenMode.Off,
                SideMetrics = GridPolicy.RecordSideMetrics(cq.Plan),
            };

            foreach (CompiledGoal g in cq.Goals)
            {
                if (!g.Available) continue;
                if (g.Goal.Importance == Importance.Must) l.HasMusts = true;
                if (g.Def.NeedsLocations) l.HasLocations = true;

                if (GridPolicy.IsBulkGridMust(g)) l.BulkMusts.Add(g.Goal.Id);
                else if (GridPolicy.IsFineOnlyGridMust(g)) l.FineMusts.Add(g.Goal.Id);
                else if (g.Goal.Importance == Importance.Nice && GridPolicy.SamplesGrid(g)) l.NiceGridGoals.Add(g.Goal.Id);

                if (g.Def.Tier == Tier.T4) l.StructureGoals.Add(g.Goal.Id);
            }

            l.GridDecides = l.BulkMusts.Count > 0 || l.FineMusts.Count > 0;
            l.TableNote = l.Note();
            return l;
        }

        /// <summary>The verdict for one rung of the ladder, in metres.</summary>
        public GridRungVerdict Rung(double grid)
        {
            if (OnlyPlacement)
            {
                return new GridRungVerdict(true,
                    "no goal here is measured on the sampling grid: location placement uses the game's own "
                    + "hard-coded 2048 x 2048 @ 12 m point grid, so every rung gives the same values and costs "
                    + "the same");
            }

            if (FineMusts.Count > 0 && grid > 12.0)
            {
                // Named in the plan's own words ("goal 'x' is one no coarse grid measures safely"), not
                // with a reason: the fine-only metrics are fine-only for different reasons, and a
                // sentence written for islands and peaks was being printed over area_above_height and
                // shore goals. "Raises" only when something will be raised: under screen: off
                // auto-pick never runs, and the rung the user picks is the rung the goal is measured at.
                bool one = FineMusts.Count == 1;
                return new GridRungVerdict(false,
                    Goals("must-have", FineMusts) + (one ? " one" : " ones") + " no coarse grid measures safely - "
                    + (ScreenOff
                        ? "screen is off, so nothing is raised and this rung is where "
                          + (one ? "it is measured: as a must-have it can" : "they are measured: as must-haves they can")
                          + " admit seeds that are not matches and reject ones that are"
                        : "auto-pick raises the whole query to G12 for " + (one ? "it" : "them")));
            }

            if (BulkMusts.Count > 0 && grid > GridPolicy.CoarsestScreen)
            {
                return new GridRungVerdict(false,
                    "coarser than G96, where the margin needed to lose no true match passes 90 % of all "
                    + "seeds: " + Goals("must-have", BulkMusts) + " no longer a filter here");
            }

            if (!GridDecides)
            {
                // "Each seed gets the same verdict", never "every rung decides the same seeds": the grid
                // is in the run hash, and a shuffled sample with no search.key visits different seeds
                // at another rung (the plan's placement note says when).
                string why = HasMusts
                    ? "no must-have here is measured on the sampling grid, so every rung gives each seed the "
                      + "same must-have verdict"
                    : "this query has no must-have goal, so no rung filters anything";
                if (NiceGridGoals.Count > 0)
                {
                    why += "; but the nice goals' ranking depends on the grid: " + Goals("nice", NiceGridGoals)
                           + " measured on it";
                }

                if (StructureGoals.Count > 0)
                {
                    why += "; the cost varies with the grid, the counts do not: river, lake and stream "
                           + "counts come from the generator's own lists, but the heights behind the "
                           + "records' side metrics are sampled on it";
                }

                return new GridRungVerdict(true, why);
            }

            // A grid-measured must-have that this rung still measures soundly. Said without "screened"
            // or "re-measured": whether this rung is screened depends on the screen mode and on the
            // screen grid, and the plan block is where that is said.
            if (Math.Abs(grid - 12.0) < 1e-9)
            {
                return new GridRungVerdict(true,
                    "the game's own grid: every goal measured on it gives the game's own number");
            }

            if (grid < 12.0)
            {
                return new GridRungVerdict(true,
                    "finer than the game's own grid: every goal measured on it gives this grid's number, "
                    + "not the game's");
            }

            // 12 < grid <= 96 here, and every grid-measured must-have is a counting one: a fine-only one
            // would have returned "no" above, so BulkMusts is not empty (GridDecides). Guarded anyway,
            // so a future branch cannot print "must-have '' is".
            if (BulkMusts.Count == 0)
            {
                return new GridRungVerdict(true, "every must-have measured on this grid is still a filter here");
            }

            bool single = BulkMusts.Count == 1;
            return new GridRungVerdict(true,
                Goals("must-have", BulkMusts) + (single ? " a counting metric" : " counting metrics")
                + ", and this rung is not past G96, where the margin needed to lose no true match passes "
                + "90 % of all seeds: " + (single ? "it is" : "they are") + " still a filter here, and "
                + (single ? "its number is this grid's number" : "their numbers are this grid's numbers"));
        }

        private string Note()
        {
            if (OnlyPlacement)
            {
                return "This query's cost is the same at every rung, and every rung gives the same values: "
                       + "location placement runs on the game's own hard-coded 2048 x 2048 @ 12 m point grid, "
                       + "so no goal here is measured on the sampling grid. The grid is still part of the "
                       + "query's identity: another rung is another run hash, and the plan says what that "
                       + "changes for this run. Add a biome, height or island goal and this table starts to "
                       + "matter.";
            }

            if (!GridDecides && NiceGridGoals.Count > 0)
            {
                return (HasMusts
                           ? "The must-haves are exact at every rung - none of them is measured on the sampling "
                             + "grid - but the nice goals' ranking depends on the grid: "
                           : "This query has no must-have goal, so no rung filters anything, but its ranking "
                             + "depends on the grid: ")
                       + Goals("nice", NiceGridGoals) + " measured on it, so which seeds rank best can "
                       + "change from rung to rung. The cost column is this query's own, measured at its "
                       + "tier and region.";
            }

            if (!GridDecides && StructureGoals.Count > 0)
            {
                return "Every rung gives the same goal values: river, lake and stream counts come from the "
                       + "generator's own lists"
                       + (HasLocations ? ", and location placement from its own 12 m grid" : "")
                       + ". The cost does vary: this query samples heights on the grid for the records' side "
                       + "metrics (" + string.Join(", ", SideMetrics) + "), which are that grid's numbers.";
            }

            return "Sampling changes time and memory, not disk. The cost column is this query's own, measured "
                   + "at its tier and region - not a relative factor from a table. A rung marked \"no\" is one "
                   + "where a must-have stops being a sound filter: past G96 for a counting metric, where the "
                   + "margin needed to lose no true match passes 90 % of all seeds, and anywhere coarser than "
                   + "G12 for a goal no coarse grid measures safely - an island, largest-patch, spawn-island, "
                   + "peak, area_above_height, shore or nearest-distance goal.";
        }

        /// <summary>"must-have 'a' is" / "must-haves 'a', 'b' are" / "nice goal 'c' is" / "nice goals ... are".</summary>
        private static string Goals(string kind, List<string> ids)
        {
            bool one = ids.Count == 1;
            string noun = kind == "nice"
                ? (one ? "nice goal" : "nice goals")
                : (one ? "must-have" : "must-haves");
            return noun + " '" + string.Join("', '", ids) + (one ? "' is" : "' are");
        }
    }
}
