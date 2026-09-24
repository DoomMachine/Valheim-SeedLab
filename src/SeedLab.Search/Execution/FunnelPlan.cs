using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;

namespace SeedLab.Search.Execution
{
    /// <summary>Which execution strategy a run uses. Chosen per run, by the user or by <see cref="Auto"/>.</summary>
    public enum SearchStrategy
    {
        /// <summary>Pick per query, and say why. <see cref="FunnelPlan.Decide"/> is that choice.</summary>
        Auto = 0,

        /// <summary>Cheap must-have goals over the whole range first, exact placement on survivors only.</summary>
        Funnel = 1,

        /// <summary>N seeds drawn without repetition from the space, never described as exhaustive.</summary>
        Sample = 2,
    }

    /// <summary>
    /// The two-stage plan for <see cref="SearchStrategy.Funnel"/>, and the decision of whether a
    /// funnel is worth running at all.
    ///
    /// <para><b>What the funnel really adds, stated honestly.</b> The per-seed ladder in
    /// <see cref="SeedEvaluator"/> ALREADY skips location placement for any seed a cheap must-have has
    /// settled - that is <c>LocationSkips</c>, and it is where the compute saving comes from. So a
    /// staged funnel does not make a query faster than the ladder already makes it. What it adds is
    /// the thing the ladder structurally cannot give: a <b>measured</b> survivor count for the whole
    /// range BEFORE a single expensive placement is paid for, so the cost of stage two is a number
    /// rather than an extrapolation from a handful of calibration seeds. On this project's own record
    /// that extrapolation has been wrong by 35x, which is the argument for measuring instead.</para>
    ///
    /// <para><b>Soundness.</b> Stage one may only drop a seed that stage two would also have dropped.
    /// It therefore keeps exactly the MUST-have goals whose tier is below T5 and drops everything
    /// else: a nice-to-have never rejects anything (it only ranks), and a must-have at T5 is a
    /// location goal, which is what stage two exists to measure. Every survivor is re-measured in full
    /// at stage two, so stage one can only ever over-admit - and the audit contract is the existing
    /// one: a funnel run's result set must equal the same query's <c>--no-prefilter</c> result set.</para>
    /// </summary>
    public sealed class FunnelPlan
    {
        private FunnelPlan()
        {
        }

        /// <summary>True when a funnel can run - there is something cheap to filter on, and something
        /// expensive to defer.</summary>
        public bool Usable { get; private set; }

        /// <summary>Why it is or is not usable, in one sentence, always set.</summary>
        public string Reason { get; private set; } = "";

        /// <summary>The stage-one query: the cheap must-haves only. Null when not <see cref="Usable"/>.</summary>
        public Query? StageOneQuery { get; private set; }

        /// <summary>The ids of the goals stage one measures.</summary>
        public List<string> StageOneGoals { get; } = new List<string>();

        /// <summary>The ids of the goals only stage two can measure.</summary>
        public List<string> DeferredGoals { get; } = new List<string>();

        /// <summary>
        /// Decides the strategy for a query, and always states the reason - including when it picks
        /// the one the user would have picked anyway. A default nobody can see is a default nobody can
        /// check.
        /// </summary>
        public static SearchStrategy Decide(FunnelPlan plan, out string why)
        {
            if (plan.Usable)
            {
                why = "funnel: " + plan.Reason;
                return SearchStrategy.Funnel;
            }

            why = "sample: " + plan.Reason;
            return SearchStrategy.Sample;
        }

        /// <summary>
        /// Builds the plan for a query. Never throws and never runs a seed: it reads the compiled
        /// tiers only.
        /// </summary>
        public static FunnelPlan For(Query q, CompiledQuery compiled)
        {
            FunnelPlan p = new FunnelPlan();
            if (q == null || compiled == null)
            {
                p.Reason = "there is no compiled query to plan for";
                return p;
            }

            foreach (CompiledGoal cg in compiled.Goals)
            {
                string id = cg.Goal.Id;
                bool must = cg.Goal.Importance == Importance.Must;
                bool cheap = cg.Tier < Tier.T5;

                // A goal this build cannot measure filters nothing and must not be counted as a
                // reason to funnel - it would produce a stage one that admits everything.
                if (!cg.Available) continue;

                if (must && cheap) p.StageOneGoals.Add(id);
                else if (cg.Tier >= Tier.T5) p.DeferredGoals.Add(id);
            }

            if (p.StageOneGoals.Count == 0)
            {
                p.Reason = "no must-have goal can be measured below the location tier, so a first "
                           + "stage would admit every seed and the second would do all the work twice";
                return p;
            }

            if (p.DeferredGoals.Count == 0)
            {
                p.Reason = "nothing in this query needs location placement, so there is no expensive "
                           + "stage to defer - the run is already as cheap as it gets";
                return p;
            }

            Query one = StageOne(q, p.StageOneGoals);
            p.StageOneQuery = one;
            p.Usable = true;
            p.Reason = Plural(p.StageOneGoals.Count, "must-have goal") + " can be measured without "
                       + "placing a single location (" + string.Join(", ", p.StageOneGoals) + "), and "
                       + Plural(p.DeferredGoals.Count, "goal")
                       + (p.DeferredGoals.Count == 1 ? " needs" : " need") + " placement ("
                       + string.Join(", ", p.DeferredGoals) + "), which costs about two seconds a seed";
            return p;
        }

        /// <summary>
        /// The stage-one query: the same query with only the named goals, no output, and the ranking
        /// stripped. It keeps the world spec, the grid and the scan order, because stage one must walk
        /// the same seeds in the same order as the run it is standing in for.
        /// </summary>
        private static Query StageOne(Query q, List<string> keep)
        {
            HashSet<string> wanted = new HashSet<string>(keep, StringComparer.Ordinal);
            Query one = new Query
            {
                Version = q.Version,
                Defs = q.Defs,
                Name = (q.Name ?? "query") + " (funnel stage 1)",
                Description = "the cheap must-have goals of '" + (q.Name ?? "query")
                              + "', measured over the whole range to find which seeds are worth placing",
                World = q.World,
                Search = q.Search,
                Output = new OutputSpec(),
            };

            foreach (Goal g in q.Goals)
            {
                if (wanted.Contains(g.Id)) one.Goals.Add(g);
            }

            return one;
        }

        private static string Plural(int n, string noun)
            => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
    }
}
