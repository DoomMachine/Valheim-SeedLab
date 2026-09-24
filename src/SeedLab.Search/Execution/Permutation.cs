using System;

namespace SeedLab.Search.Execution
{
    /// <summary>
    /// The scan order: index -> seed, as a bijection, so a scan visits every seed in its range
    /// <b>exactly once, with no repeats and no gaps</b>, and can be stopped and resumed from a single
    /// 64-bit index (07-features.md section 3.5).
    ///
    /// <para><b>The permutation, stated exactly, because "shuffled" is meaningless otherwise.</b>
    /// Let <c>M = to - from + 1</c> be the number of seeds in the range and
    /// <c>h = ceil(log2(M) / 2)</c>, so the domain <c>D = 2^(2h) &gt;= M</c>. Define a
    /// <b>4-round balanced Feistel network</b> on <c>2h</c> bits:</para>
    /// <code>
    /// L = x >> h ; R = x &amp; (2^h - 1)
    /// for round = 0..3:   (L, R) = (R, L xor (H(key, round, R) &amp; (2^h - 1)))
    /// F(x) = (L &lt;&lt; h) | R
    /// </code>
    /// <para><c>H</c> is the SplitMix64 finaliser over <c>key</c>, <c>round</c> and <c>R</c> (below).
    /// A Feistel network is a bijection for any round function whatsoever, which is what makes the
    /// no-repeats/no-gaps claim a fact rather than a hope. Where <c>D &gt; M</c> the map is reduced to
    /// <c>[0, M)</c> by <b>cycle walking</b> - re-apply <c>F</c> until the value lands in range - which
    /// is again a bijection on <c>[0, M)</c> because <c>F</c> permutes the finite set <c>[0, D)</c> and
    /// the walk follows its cycles. For the full int32 space <c>M = 2^32</c> is already a power of two,
    /// so <c>h = 16</c>, <c>D = M</c>, and no walking ever happens.</para>
    ///
    /// <para>The key goes in the run manifest. Same key + same index range = the same seed sequence =
    /// the same results, which is what makes a run reproducible.</para>
    /// </summary>
    public static class Permutation
    {
        /// <summary>Half-width h for a range of <paramref name="count"/> values.</summary>
        public static int HalfBits(ulong count)
        {
            int bits = 1;
            while (bits < 64 && (1UL << bits) < count) bits++;
            return (bits + 1) / 2;
        }

        /// <summary>The Feistel round function: the SplitMix64 finaliser, masked to the half width.</summary>
        public static ulong Round(ulong key, int round, ulong r, ulong mask)
        {
            ulong z = key ^ ((ulong)(round + 1) * 0x9E3779B97F4A7C15UL) ^ (r * 0xD6E8FEB86659FD93UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return z & mask;
        }

        /// <summary>One application of the 4-round Feistel permutation on 2h bits.</summary>
        public static ulong Feistel(ulong x, ulong key, int h)
        {
            ulong mask = h == 64 ? ulong.MaxValue : (1UL << h) - 1UL;
            ulong l = (x >> h) & mask;
            ulong r = x & mask;
            for (int round = 0; round < 4; round++)
            {
                ulong nl = r;
                ulong nr = l ^ Round(key, round, r, mask);
                l = nl;
                r = nr;
            }

            return (l << h) | r;
        }

        /// <summary>
        /// Index -> offset within the range, a bijection on <c>[0, count)</c>. Cycle-walks when the
        /// Feistel domain is larger than the range.
        /// </summary>
        public static ulong Shuffle(ulong index, ulong count, ulong key)
        {
            int h = HalfBits(count);
            ulong domain = h >= 32 ? 0UL : 1UL << (2 * h);   // 0 means 2^64, which never happens for int32
            ulong y = Feistel(index, key, h);
            // Bounded by the cycle length; for count = 2^32 the loop body never runs.
            while (domain != 0 && y >= count) y = Feistel(y, key, h);
            return y;
        }
    }
}
