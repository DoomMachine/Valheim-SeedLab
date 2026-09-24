using System;
using System.Collections.Generic;
using SeedLab.Search.Criteria;

namespace SeedLab.Search.Evaluation
{
    /// <summary>What one goal measured on one seed, and why that made the seed pass or fail.</summary>
    public struct GoalOutcome
    {
        public string Id;
        public string Target;
        public string Metric;
        public GoalTest Test;
        public Importance Importance;
        public double Threshold;
        public double ThresholdMax;
        public double Weight;

        /// <summary>The measured value in the metric's own unit.</summary>
        public double Value;

        public Unit Unit;

        /// <summary>
        /// True when the region restriction stopped short of the true value. The verdict is still exact
        /// (that is the only restriction the engine allows); the number is a bound, not a measurement.
        /// </summary>
        public bool Bounded;

        /// <summary>The radius the measurement covered when <see cref="Bounded"/> is set.</summary>
        public double BoundRadius;

        public bool Pass;

        /// <summary>The nice-to-have sub-score in [0,1]. Zero and unused for a must goal.</summary>
        public double Score;

        /// <summary>The tier that produced the value.</summary>
        public Tier Tier;

        /// <summary>How much the goal missed (or beat) its threshold by, in the metric's unit.</summary>
        public double Margin;

        /// <summary>Set when the goal could not be measured at all in this build.</summary>
        public string? Unavailable;

        /// <summary>Set when no seed in the space can satisfy this goal (T0).</summary>
        public string? Unsatisfiable;

        /// <summary>
        /// The grid THIS goal was decided at, metres. A screen-then-verify run decides different
        /// goals at different grids, and one whole-record <c>grid_m</c> hides that - so the number
        /// travels with the goal, not only with the record.
        /// </summary>
        public double MeasuredAtGrid;

        /// <summary>
        /// False when this metric's number at one grid is not an approximation of its number at
        /// another (<see cref="MetricDef.GridComparable"/>). The record then also carries
        /// <see cref="GridNote"/>, because the failure mode is somebody comparing a G96 island count
        /// with a G12 one in a month and believing the difference.
        /// </summary>
        public bool GridComparable;

        /// <summary>One sentence saying why, from <see cref="MetricDef.GridNote"/>. Null when comparable.</summary>
        public string? GridNote;

        /// <summary>
        /// Set when the value is at or below the grid's own resolution floor (half a cell diagonal:
        /// 8.485 m at G12). The number is then a BOUND, not a measurement - 70 % of seeds hit this on
        /// <c>nearest_land_distance</c> at G12 - and the record says so instead of printing a figure
        /// that reads like a distance somebody measured.
        /// </summary>
        public string? Censored;

        /// <summary>
        /// The measured median |relative error| of this metric at the grid it was decided on, against
        /// the game's own 12 m grid. Left at 0 - and then never printed - when the resolution study
        /// did not sample that pair, so the record says nothing rather than interpolating a number.
        /// </summary>
        public double MeasuredRelError;

        /// <summary>The sample <see cref="MeasuredRelError"/> came from. Never printed without it.</summary>
        public string? ErrorSource;

        /// <summary>
        /// Set when the goal's target contains an <c>m_unique</c> location type: exactly what this
        /// metric's number means about the candidate set, since none of those types has a position the
        /// seed decides. Printed verbatim by <c>vseed explain</c>.
        /// </summary>
        public string? UniqueSemantics;

        /// <summary>
        /// Set when the goal's target is part of a curated world feature whose CONTENTS the seed does
        /// not decide - the axe-head houses being the one in this build. Separate from
        /// <see cref="UniqueSemantics"/> because neither house is <c>m_unique</c>: a results file that
        /// listed twenty WoodHouse6 coordinates with no such line would be read as twenty axe heads.
        /// </summary>
        public string? ContentsNote;
    }

    /// <summary>One seed's verdict: the score, every goal's measurement, and a few standard world numbers.</summary>
    public sealed class SeedResult
    {
        public int Seed;

        /// <summary>A typeable seed text that hashes back to <see cref="Seed"/>, filled in by the writer.</summary>
        public string? SeedText;

        public bool Pass;

        /// <summary>Weighted mean of the nice-to-have sub-scores, or 1.0 when the query has none.</summary>
        public double Score;

        public Tier TierReached;

        public List<GoalOutcome> Goals = new List<GoalOutcome>();

        /// <summary>The goal that rejected the seed, for the "why not" report. Null when it passed.</summary>
        public string? FailedGoal;

        // ---- a small, fixed set of world numbers carried with every hit so a results file is useful
        //      without re-running anything. Null when the run never measured them.
        public double? LandAreaM2;
        public double? LargestIslandM2;
        public int? IslandCount1Ha;
        public double? OceanShare;
        public double? HighestPeak;

        /// <summary>
        /// The disc these side numbers were measured inside, metres. When the query's goals allowed a
        /// region restriction this is smaller than 10,500 and the numbers above are region-limited, not
        /// whole-world - so the radius is written on the record rather than left to be guessed. The
        /// goal verdicts are unaffected: the restriction is only ever taken where it decides them
        /// exactly (CompiledQuery.Compile).
        /// </summary>
        public double RegionRadiusM = 10500.0;

        /// <summary>
        /// The prefab after which the location gate stopped this seed's placement, or null when the
        /// whole planned prefix ran. A record carrying this one is a REJECTION whose later location
        /// goals were never measured - which is why they report no value rather than a truncated one.
        /// </summary>
        public string? LocationPrefixAborted;

        /// <summary>Wall-clock spent on this seed, seconds. Used only for the measured throughput report.</summary>
        public double Seconds;

        /// <summary>
        /// Screen-then-verify: the coarse grid this seed was screened on, metres, or 0 when it was
        /// measured once at the query's own grid. A record that carries it was re-measured exactly at
        /// <see cref="VerifiedAtGrid"/>, so its numbers are that grid's numbers and nothing else -
        /// the coarse pass only decided whether to spend the exact measurement on this seed.
        /// </summary>
        public double ScreenedAtGrid;

        /// <summary>The grid the values on this record were finally measured on, metres.</summary>
        public double VerifiedAtGrid;
    }
}
