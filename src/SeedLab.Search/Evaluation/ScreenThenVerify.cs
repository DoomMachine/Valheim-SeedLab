using System;
using System.Collections.Generic;
using SeedLab.Search.Criteria;
using SeedLab.Search.Locations;

namespace SeedLab.Search.Evaluation
{
    /// <summary>
    /// What a <see cref="SearchRun"/> worker needs from an evaluator. <see cref="SeedEvaluator"/> is
    /// the one-pass implementation; <see cref="TwoStageEvaluator"/> is the screen-then-verify one.
    /// </summary>
    public interface ISeedEvaluator
    {
        SeedResult Evaluate(int seed, bool full = false);

        double ConstructSeconds { get; }
        double SampleSeconds { get; }
        double PregenSeconds { get; }
        double LocationSeconds { get; }
        long Pregenerated { get; }
        long ProbeAccepts { get; }
        long EarlyExits { get; }
        long LocationAborts { get; }
        long LocationSkips { get; }
        long Placed { get; }
    }

    /// <summary>
    /// The resolution study's main recommendation, implemented: a cheap coarse pass with a measured
    /// margin, then an exact re-measurement of every survivor at the game's own 12 m grid.
    ///
    /// <para><b>What the user gets.</b> Every record written was measured at G12, so its numbers are
    /// the game's own grid's numbers and are comparable with <c>vseed seed</c>, with the map, and with
    /// any other G12 result. The coarse pass never decides a record's contents; it decides only
    /// whether the expensive measurement is worth spending on that seed.</para>
    ///
    /// <para><b>What it costs and what it saves.</b> Measured on 2,560 seeds: screening the most
    /// selective bulk goal tested (Mountain area >= 12 km2, 345 true matches) at G24 with a 1 %
    /// margin passes 420 seeds - a 1.22x survivor inflation - and loses none of the 345. The G24 pass
    /// is ~256x the samples of G384 but still a small fraction of a G12 pass, and only the 420
    /// survivors pay for G12.</para>
    ///
    /// <para><b>What it does not claim.</b> The margin is empirical: zero false negatives on 2,560
    /// seeds, which is 0.00006 % of the space. There is no proof available -
    /// <c>Mathf.PerlinNoise</c> is native code with an unverified gradient set, so there is no
    /// Lipschitz constant and no interval arithmetic, and no coarse rejection can be made provably
    /// sound (07-features.md section 3.2.7). Every record says which grid screened it and which grid
    /// measured it, and the report says the margin is a sample-derived bound rather than a proof.</para>
    /// </summary>
    public sealed class TwoStageEvaluator : ISeedEvaluator
    {
        private readonly SeedEvaluator _screen;
        private readonly SeedEvaluator _verify;
        private readonly double _screenGrid;
        private readonly double _verifyGrid;

        public TwoStageEvaluator(CompiledQuery screen, CompiledQuery verify, ILocationOracle oracle)
        {
            _screen = new SeedEvaluator(screen, oracle);
            _verify = new SeedEvaluator(verify, oracle);
            _screenGrid = screen.Query.Search.Grid;
            _verifyGrid = verify.Query.Search.Grid;
        }

        /// <summary>Seeds the coarse pass rejected, so they never reached the exact measurement.</summary>
        public long Screened { get; private set; }

        /// <summary>Seeds the coarse pass passed on to the exact measurement.</summary>
        public long Survivors { get; private set; }

        /// <summary>Survivors the exact measurement then rejected - the margin's cost, measured live.</summary>
        public long FalseSurvivors { get; private set; }

        public SeedResult Evaluate(int seed, bool full = false)
        {
            SeedResult coarse = _screen.Evaluate(seed, full);
            Screened++;
            if (!coarse.Pass)
            {
                coarse.ScreenedAtGrid = _screenGrid;
                coarse.VerifiedAtGrid = 0;
                return coarse;
            }

            Survivors++;
            SeedResult exact = _verify.Evaluate(seed, full);
            exact.ScreenedAtGrid = _screenGrid;
            exact.VerifiedAtGrid = _verifyGrid;
            if (!exact.Pass) FalseSurvivors++;
            return exact;
        }

        public double ConstructSeconds => _screen.ConstructSeconds + _verify.ConstructSeconds;
        public double SampleSeconds => _screen.SampleSeconds + _verify.SampleSeconds;
        public double PregenSeconds => _screen.PregenSeconds + _verify.PregenSeconds;
        public double LocationSeconds => _screen.LocationSeconds + _verify.LocationSeconds;
        public long Pregenerated => _screen.Pregenerated + _verify.Pregenerated;
        public long ProbeAccepts => _screen.ProbeAccepts + _verify.ProbeAccepts;
        public long EarlyExits => _screen.EarlyExits + _verify.EarlyExits;
        public long LocationAborts => _screen.LocationAborts + _verify.LocationAborts;
        public long LocationSkips => _screen.LocationSkips + _verify.LocationSkips;
        public long Placed => _screen.Placed + _verify.Placed;
    }

    /// <summary>Builds the two queries a screen-then-verify run evaluates.</summary>
    public static class ScreenThenVerify
    {
        /// <summary>
        /// The screening query: the same goals at the coarse grid, with every MUST threshold relaxed
        /// by the margin and every NICE goal dropped.
        ///
        /// <para>Nice goals are dropped because they never reject anything, so measuring them coarsely
        /// would cost time and buy nothing - and because their scores must come from the exact pass or
        /// the ranking would be a coarse ranking wearing a fine grid's name.</para>
        ///
        /// <para>A goal whose policy is <see cref="GridSafety.FineOnly"/> is dropped from the screen
        /// as well: there is no margin that makes it safe, so the screen simply does not test it and
        /// the exact pass decides it alone.</para>
        /// </summary>
        public static Query ScreenQuery(Query original, GridPlan plan, CompiledQuery compiled)
        {
            Query q = Clone(original);
            q.Search.Grid = plan.ScreenGrid;
            q.Goals = new List<Goal>();

            Dictionary<string, CompiledGoal> byId = new Dictionary<string, CompiledGoal>(StringComparer.Ordinal);
            foreach (CompiledGoal cg in compiled.Goals) byId[cg.Goal.Id] = cg;

            foreach (Goal g in original.Goals)
            {
                if (g.Importance != Importance.Must) continue;
                if (!byId.TryGetValue(g.Id, out CompiledGoal? cg) || !cg.Available) continue;

                GoalGridPolicy p = GridPolicy.For(cg);
                if (p.Safety != GridSafety.ScreenWithMargin) continue;

                Goal s = CloneGoal(g);
                s.Value = GridPolicy.RelaxedThreshold(g.Test, g.Value, plan.Margin);
                if (g.Test == GoalTest.Between)
                {
                    s.Value = GridPolicy.RelaxedThreshold(GoalTest.AtLeast, g.Value, plan.Margin);
                    s.Max = GridPolicy.RelaxedThreshold(GoalTest.AtMost, g.Max, plan.Margin);
                }

                q.Goals.Add(s);
            }

            if (q.Goals.Count == 0)
            {
                // Nothing screenable: there is no cheap pass to run, so the caller measures once.
                return original;
            }

            q.CanonicalJson = QueryReader.Canonicalise(q);
            return q;
        }

        /// <summary>The exact query: the user's own goals at the verification grid (12 m).</summary>
        public static Query VerifyQuery(Query original, GridPlan plan)
        {
            Query q = Clone(original);
            q.Search.Grid = plan.VerifyGrid;
            q.Goals = new List<Goal>(original.Goals.Count);
            foreach (Goal g in original.Goals) q.Goals.Add(CloneGoal(g));
            q.CanonicalJson = QueryReader.Canonicalise(q);
            return q;
        }

        private static Query Clone(Query q) => new Query
        {
            Version = q.Version,
            Defs = q.Defs,
            Name = q.Name,
            Description = q.Description,
            World = new WorldSpec { GenVersion = q.World.GenVersion },
            Search = new SearchSpec
            {
                Order = q.Search.Order,
                Key = q.Search.Key,
                From = q.Search.From,
                To = q.Search.To,
                Seeds = q.Search.Seeds,
                Wall = q.Search.Wall,
                Keep = q.Search.Keep,
                KeepAll = q.Search.KeepAll,
                Grid = q.Search.Grid,
                Approx = q.Search.Approx,
                Threads = q.Search.Threads,
                BlockSize = q.Search.BlockSize,
                Region = q.Search.Region,
                Screen = q.Search.Screen,
                ScreenGrid = q.Search.ScreenGrid,
            },
            Output = new OutputSpec { Path = q.Output.Path, Explain = q.Output.Explain },
            Goals = new List<Goal>(q.Goals),
            CanonicalJson = q.CanonicalJson,
        };

        private static Goal CloneGoal(Goal g) => new Goal
        {
            Id = g.Id,
            Target = g.Target,
            Metric = g.Metric,
            Test = g.Test,
            Value = g.Value,
            Max = g.Max,
            Radius = g.Radius,
            Height = g.Height,
            MinArea = g.MinArea,
            From = g.From,
            Importance = g.Importance,
            Weight = g.Weight,
            Pad = g.Pad,
        };
    }
}
