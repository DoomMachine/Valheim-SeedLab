using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Search.Criteria;

namespace SeedLab.Search.Feasibility
{
    /// <summary>
    /// An OVER-approximation of the set of values one metric can take over all 4,294,967,296 seeds.
    ///
    /// <para>Everything the checker decides follows from one rule: it REFUSES only when
    /// <c>F ∩ accept(goal) = ∅</c> and calls a goal VACUOUS only when <c>F ⊆ accept(goal)</c>. Both
    /// tests get <i>safer</i> as F gets wider, which is why over-approximation is the right
    /// direction and why a merely plausible bound must never be narrowed into F.</para>
    ///
    /// <para><b>Widen-F.</b> When two sound bounds disagree, the LOOSER one decides. Concretely
    /// <see cref="BiomeGeometry.Band"/> returns 7,900 m for the AshLands floor while the atlas
    /// computes 7,907.681 m; both are sound, 7,900 is looser, so 7,900 decides and 7,907.7 is shown
    /// only as the labelled exact value.</para>
    ///
    /// <para><b><see cref="IncludesAbsent"/> is the field that gets vacuity wrong when it is
    /// forgotten.</b> A distance metric reports +infinity on a world that holds none of the target,
    /// so a "near D" goal can never be vacuous however large D is - it has become a PRESENCE filter
    /// instead, which is a different verdict with a different fix.</para>
    /// </summary>
    public readonly struct Feasible
    {
        public Feasible(double lo, double hi, bool includesAbsent, double absentValue, bool exact,
                        IReadOnlyList<CheckEvidence> evidence)
        {
            Lo = lo;
            Hi = hi;
            IncludesAbsent = includesAbsent;
            AbsentValue = absentValue;
            Exact = exact;
            Evidence = evidence;
        }

        /// <summary>The widest sound lower bound on the measured value, in the metric's own unit.</summary>
        public double Lo { get; }

        /// <summary>The widest sound upper bound; <c>+infinity</c> when nothing bounds it.</summary>
        public double Hi { get; }

        /// <summary>True when the metric can also report its "nothing there" sentinel.</summary>
        public bool IncludesAbsent { get; }

        /// <summary>+infinity for a distance metric; 0 for a count or an area (already inside [Lo, Hi]).</summary>
        public double AbsentValue { get; }

        /// <summary>True when F is the EXACT achievable set - a constant metric, where Lo == Hi.</summary>
        public bool Exact { get; }

        public IReadOnlyList<CheckEvidence> Evidence { get; }

        /// <summary>Nothing is known: the checker may neither refuse nor call anything vacuous.</summary>
        public static Feasible Unknown =>
            new Feasible(double.NegativeInfinity, double.PositiveInfinity, false, double.NaN, false,
                         Array.Empty<CheckEvidence>());

        public bool IsUnknown => double.IsNegativeInfinity(Lo) && double.IsPositiveInfinity(Hi);

        /// <summary>A constant metric: one value, in every seed, provably.</summary>
        public static Feasible Constant(double value, IReadOnlyList<CheckEvidence> evidence)
            => new Feasible(value, value, false, double.NaN, true, evidence);

        public Feasible WithAbsence(double absentValue)
            => new Feasible(Lo, Hi, true, absentValue, false, Evidence);

        /// <summary>"1,500 m .. 10,500 m, and +infinity when none placed".</summary>
        public string Describe(Unit unit)
        {
            string lo = Num(Lo, unit), hi = double.IsPositiveInfinity(Hi) ? "no upper bound" : Num(Hi, unit);
            if (Exact) return "exactly " + lo + " in every seed";
            return lo + " .. " + hi
                   + (IncludesAbsent
                       ? ", and " + (double.IsPositiveInfinity(AbsentValue) ? "+infinity" : Num(AbsentValue, unit))
                         + " when the world holds none of it"
                       : "");
        }

        public static string Num(double v, Unit unit)
        {
            if (double.IsPositiveInfinity(v)) return "+infinity";
            if (double.IsNegativeInfinity(v)) return "-infinity";
            switch (unit)
            {
                case Unit.Metres:
                    return v.ToString("N0", CultureInfo.InvariantCulture) + " m";
                case Unit.SquareMetres:
                    return v >= 1e6
                        ? (v / 1e6).ToString("N2", CultureInfo.InvariantCulture) + " km2"
                        : v.ToString("N0", CultureInfo.InvariantCulture) + " m2";
                case Unit.Fraction:
                    return (v * 100).ToString("0.###", CultureInfo.InvariantCulture) + " %";
                default:
                    return v.ToString("N0", CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>The interval a goal's test accepts, on the real line, plus whether it accepts absence.</summary>
    public readonly struct Accepted
    {
        public Accepted(double lo, double hi, bool acceptsAbsent)
        {
            Lo = lo;
            Hi = hi;
            AcceptsAbsent = acceptsAbsent;
        }

        public double Lo { get; }
        public double Hi { get; }

        /// <summary>
        /// True when a world holding none of the target satisfies the goal. For a distance metric
        /// absence measures +infinity, so only <c>far</c>/<c>at_least</c> accepts it; for a count or
        /// an area absence is 0 and is already inside the interval.
        /// </summary>
        public bool AcceptsAbsent { get; }

        /// <summary>
        /// The accepted interval for a goal. <paramref name="slack"/> is applied in the PERMISSIVE
        /// direction only, so a rounding difference can widen what the checker will accept and can
        /// never narrow it into a false refusal.
        /// </summary>
        public static Accepted For(GoalTest test, double value, double max, double absentValue, double slack)
        {
            bool absentIsHigh = double.IsPositiveInfinity(absentValue);
            switch (test)
            {
                case GoalTest.Near:
                case GoalTest.AtMost:
                    return new Accepted(double.NegativeInfinity, value + slack, !absentIsHigh);
                case GoalTest.Far:
                case GoalTest.AtLeast:
                    return new Accepted(value - slack, double.PositiveInfinity, true);
                case GoalTest.Between:
                    return new Accepted(value - slack, max + slack, !absentIsHigh);
                default:
                    return new Accepted(double.NegativeInfinity, double.PositiveInfinity, true);
            }
        }

        public bool Contains(double v) => v >= Lo && v <= Hi;

        /// <summary>The intersection, for the self-contradiction rule (R13).</summary>
        public Accepted Intersect(Accepted other)
            => new Accepted(Math.Max(Lo, other.Lo), Math.Min(Hi, other.Hi),
                            AcceptsAbsent && other.AcceptsAbsent);

        public bool IsEmpty => Lo > Hi;
    }
}
