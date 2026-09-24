using System;
using SeedLab.Search.Criteria;

namespace SeedLab.Search.Execution
{
    /// <summary>
    /// The index -> seed mapping for one run, plus how the work is cut into blocks.
    ///
    /// <para>A run is identified by <c>{order, key, from, to, blockSize, limit}</c> together with the
    /// query hash. Those seven values reproduce the seed sequence exactly, which is what makes a
    /// resumed run and an uninterrupted run the same run.</para>
    /// </summary>
    public sealed class ScanPlan
    {
        /// <summary>
        /// A plan over an EXPLICIT list of seeds - the survivors of a funnel's first stage.
        ///
        /// <para>Everything downstream (blocks, the collector, the checkpoint, resume) works on
        /// <c>index -&gt; seed</c> and nothing else, so a second stage needs no new execution path: it
        /// is the same run over a different bijection. The identity that a checkpoint is matched on
        /// carries <see cref="Key"/>, and here that key is a hash OF THE SEED LIST - so resuming stage
        /// two against a different survivor set is a mismatch rather than a silent wrong answer.</para>
        /// </summary>
        public static ScanPlan OverSeeds(int[] seeds, int blockSize)
        {
            if (seeds == null) throw new ArgumentNullException(nameof(seeds));
            ScanPlan p = new ScanPlan(ScanOrder.Sequential, 0, Math.Max(0, seeds.Length - 1),
                                      HashOf(seeds), blockSize, seeds.Length);
            p._explicit = seeds;
            return p;
        }

        private int[]? _explicit;

        /// <summary>True when this plan walks a fixed list rather than a range.</summary>
        public bool IsExplicit => _explicit != null;

        private static ulong HashOf(int[] seeds)
        {
            ulong k = 0xCBF29CE484222325UL;
            foreach (int s in seeds)
            {
                k ^= (uint)s;
                k *= 0x100000001B3UL;
            }

            return k;
        }

        public ScanPlan(ScanOrder order, long from, long to, ulong key, int blockSize, long limit)
        {
            if (from > to) throw new ArgumentException("the scan range runs backwards");
            Order = order;
            From = from;
            To = to;
            Key = key;
            BlockSize = Math.Max(1, blockSize);
            Count = (ulong)(to - from + 1);
            Limit = limit <= 0 || (ulong)limit > Count ? (long)Count : limit;
            Blocks = (Limit + BlockSize - 1) / BlockSize;
        }

        public ScanOrder Order { get; }
        public long From { get; }
        public long To { get; }
        public ulong Key { get; }
        public int BlockSize { get; }

        /// <summary>Seeds in the range.</summary>
        public ulong Count { get; }

        /// <summary>Seeds this run will actually visit (the seed budget, capped by the range).</summary>
        public long Limit { get; }

        public long Blocks { get; }

        /// <summary>
        /// The fraction of the whole 4,294,967,296-world space this run covers.
        ///
        /// <para>For an explicit plan this is the fraction the SURVIVORS are, which is not the fraction
        /// the search covered - stage one covered more and rejected most of it. A caller reporting
        /// coverage for a funnel must quote stage one's, and <c>FunnelReport</c> does.</para>
        /// </summary>
        public double FractionOfSpace => Limit / 4294967296.0;

        /// <summary>Index -> seed. A bijection over <c>[0, Count)</c> in both orders.</summary>
        public int SeedAt(long index)
        {
            if (_explicit != null) return _explicit[index];
            ulong off = Order == ScanOrder.Sequential
                ? (ulong)index
                : Permutation.Shuffle((ulong)index, Count, Key);
            return (int)(From + (long)off);
        }

        public long BlockStart(long block) => block * BlockSize;

        public long BlockEnd(long block) => Math.Min(Limit, (block + 1) * (long)BlockSize);

        /// <summary>
        /// A key derived from the query hash, so that leaving <c>search.key</c> out of a query file is
        /// still fully deterministic: the same query always walks the space in the same order.
        /// </summary>
        public static ulong KeyFromHash(string hexHash)
        {
            ulong k = 0xCBF29CE484222325UL;
            foreach (char c in hexHash)
            {
                k ^= c;
                k *= 0x100000001B3UL;
            }

            return k;
        }
    }
}
