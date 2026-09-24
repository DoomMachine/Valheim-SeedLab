using System;
using SeedLab.Search.Criteria;
using SeedLab.Search.Execution;

namespace SeedLab.LocationOracle
{
    /// <summary>
    /// The one fixed seed order the profiler, the world fingerprints and the seed-atlas pilot share: the
    /// tool's own shuffled scan order over the whole int32 range under key <see cref="Key"/>, read from
    /// permutation index 0.
    ///
    /// <para><b>Why this key.</b> It is the count study's documented order (<c>CountSample</c>,
    /// <c>docs\studies\altar-quantity-data.md</c>), with its first eight seeds written down, so every
    /// machine and every run walks the same seeds - profile rows and atlas rows join on the seed, and a
    /// row of either can be checked against the count sample for free. <see cref="Verify"/> recomputes
    /// the documented eight before anything relies on the order, so a change to the permutation cannot
    /// quietly move every seed a profile names.</para>
    /// </summary>
    public static class PilotSeedOrder
    {
        /// <summary>The Feistel key of the order.</summary>
        public const ulong Key = 0xA17A25EED10C5117UL;

        /// <summary>The key as it is written on a command line: "0xA17A25EED10C5117".</summary>
        public const string KeyText = "0xA17A25EED10C5117";

        /// <summary>The first eight seeds of the order, as the count study recorded them.</summary>
        public static readonly int[] DocumentedFirstEight =
        {
            1544594208, -98710706, -1923311157, 1693137585, -454913440, 952884812, -880016200, 914782029,
        };

        /// <summary>The whole-range plan the order is read from, for any key.</summary>
        public static ScanPlan Plan(ulong key = Key)
            => new ScanPlan(ScanOrder.Shuffled, int.MinValue, int.MaxValue, key, 1, 0);

        /// <summary>The seed at permutation index <paramref name="index"/> of the order under <paramref name="key"/>.</summary>
        public static int At(long index, ulong key = Key) => Plan(key).SeedAt(index);

        /// <summary>
        /// Recomputes the documented first eight seeds and throws when any differs. Cheap (eight Feistel
        /// evaluations); call it before an answer depends on the order.
        /// </summary>
        public static void Verify()
        {
            ScanPlan plan = Plan();
            for (int i = 0; i < DocumentedFirstEight.Length; i++)
            {
                int s = plan.SeedAt(i);
                if (s != DocumentedFirstEight[i])
                {
                    throw new InvalidOperationException(
                        "the pilot seed order does not reproduce its documented seeds: index " + i + " gives "
                        + s + ", the count study recorded " + DocumentedFirstEight[i]
                        + ". The permutation has changed; nothing that names seeds by this order can be trusted.");
                }
            }
        }
    }
}
