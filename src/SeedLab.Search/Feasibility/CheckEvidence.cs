using System;
using System.Collections.Generic;

namespace SeedLab.Search.Feasibility
{
    /// <summary>Where a statement in a <see cref="QueryCheckReport"/> came from.</summary>
    public enum EvidenceKind
    {
        /// <summary>A decompiled branch condition, named with its member and line.</summary>
        Code,

        /// <summary>A field of the dumped <c>ZoneLocation</c> table, named with its prefab and field.</summary>
        Asset,

        /// <summary>The query's own text - a contradiction that needs no game data at all.</summary>
        Query,

        /// <summary>A measurement over some number of seeds. <b>Never sufficient for a refusal.</b></summary>
        Sample,
    }

    /// <summary>
    /// One reason the checker gives for a verdict.
    ///
    /// <para><b>The safety property is structural, not asserted.</b> A REFUSE verdict is built by
    /// <see cref="GoalCheck.Refuse"/>, whose signature takes <see cref="HardEvidence"/> and nothing
    /// else. There is no code path that puts a <see cref="SampleEvidence"/> behind a refusal, because
    /// the type system will not let one be written - which is a stronger guarantee than a test, and
    /// the test (T2) then only has to confirm that the types were not worked around.</para>
    ///
    /// <para>The rule this enforces is the design's: a sample may never produce a REFUSE. Not from
    /// 240 seeds, not from 5,000, not from the 36,829 real instances the atlas was validated
    /// against. A measurement can say a query looks hopeless; only the generator's own code or the
    /// game's own asset data can say no seed satisfies it.</para>
    /// </summary>
    public abstract class CheckEvidence
    {
        private protected CheckEvidence(EvidenceKind kind, string reference, string detail)
        {
            Kind = kind;
            Reference = reference;
            Detail = detail;
        }

        public EvidenceKind Kind { get; }

        /// <summary>What to look at: <c>ZoneSystem.PlaceLocations:1956</c>, <c>Eikthyrnir.m_maxDistance</c>.</summary>
        public string Reference { get; }

        /// <summary>The value or the condition, in one clause.</summary>
        public string Detail { get; }

        public override string ToString()
            => Kind.ToString().ToLowerInvariant() + " " + Reference + (Detail.Length > 0 ? ": " + Detail : "");
    }

    /// <summary>
    /// Evidence that holds for every one of the 4,294,967,296 seeds. Its constructor accepts only
    /// <see cref="EvidenceKind.Code"/>, <see cref="EvidenceKind.Asset"/> and
    /// <see cref="EvidenceKind.Query"/>; passing anything else throws, so a refusal cannot be built
    /// out of a measurement even by accident.
    /// </summary>
    public sealed class HardEvidence : CheckEvidence
    {
        public HardEvidence(EvidenceKind kind, string reference, string detail = "")
            : base(Validate(kind), reference, detail)
        {
        }

        public static HardEvidence Code(string reference, string detail = "")
            => new HardEvidence(EvidenceKind.Code, reference, detail);

        public static HardEvidence Asset(string reference, string detail = "")
            => new HardEvidence(EvidenceKind.Asset, reference, detail);

        public static HardEvidence Query(string reference, string detail = "")
            => new HardEvidence(EvidenceKind.Query, reference, detail);

        private static EvidenceKind Validate(EvidenceKind kind)
        {
            if (kind == EvidenceKind.Sample)
            {
                throw new ArgumentException(
                    "a sample is not hard evidence: it can support a warning and never a refusal or a "
                    + "vacuity claim. Use SampleEvidence.", nameof(kind));
            }

            return kind;
        }
    }

    /// <summary>
    /// A measurement, with the sample it came from. It may support a WARN and nothing stronger, and
    /// every number it carries is printed with its size and its interval.
    /// </summary>
    public sealed class SampleEvidence : CheckEvidence
    {
        public SampleEvidence(string reference, string detail, int n, int k = -1)
            : base(EvidenceKind.Sample, reference, detail)
        {
            N = n;
            K = k;
        }

        /// <summary>The sample size.</summary>
        public int N { get; }

        /// <summary>The hit count, or -1 when the row is not a rate.</summary>
        public int K { get; }
    }

    /// <summary>Wilson intervals and the rule of three - the two things a rate is never printed without.</summary>
    public static class Rates
    {
        /// <summary>
        /// The Wilson score interval at 95 % for <paramref name="k"/> of <paramref name="n"/>. It is
        /// used instead of the normal approximation because the rates that matter here are small and
        /// often zero, where the normal interval runs below 0 and reads as certainty.
        /// </summary>
        public static (double Lo, double Hi) Wilson95(int k, int n)
        {
            if (n <= 0) return (0.0, 1.0);
            const double z = 1.959963984540054;
            double p = (double)k / n;
            double d = 1.0 + z * z / n;
            double centre = p + z * z / (2.0 * n);
            double spread = z * Math.Sqrt(p * (1.0 - p) / n + z * z / (4.0 * (double)n * n));
            return (Math.Max(0.0, (centre - spread) / d), Math.Min(1.0, (centre + spread) / d));
        }

        /// <summary>
        /// The one honest sentence for <c>k = 0</c>: the rule of three, <c>p &lt; 3/n</c> at 95 %.
        /// Never "0 %", which claims a proof a sample cannot give.
        /// </summary>
        public static double RuleOfThree(int n) => n <= 0 ? 1.0 : 3.0 / n;

        /// <summary>The provenance clause every rate in a report carries, verbatim.</summary>
        public static string Provenance(string source, int n, int k)
            => "from " + source + ", " + n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
               + " seeds, " + k.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " hits";

        /// <summary>"1 in 745 (0.134 %)" - a rate is stated both ways or not at all.</summary>
        public static string Describe(double p)
        {
            System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
            if (p <= 0) return "0";
            if (p >= 1) return "every seed";
            return "1 in " + (1.0 / p).ToString("N0", ci) + " (" + (p * 100).ToString("0.###", ci) + " %)";
        }
    }

    /// <summary>A small helper so a list of evidence prints the same way everywhere.</summary>
    public static class EvidenceText
    {
        public static string Join(IReadOnlyList<CheckEvidence> items)
        {
            List<string> parts = new List<string>(items.Count);
            foreach (CheckEvidence e in items) parts.Add(e.ToString());
            return string.Join("; ", parts);
        }
    }
}
