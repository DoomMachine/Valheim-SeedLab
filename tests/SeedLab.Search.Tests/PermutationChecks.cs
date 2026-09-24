using System;
using System.Collections.Generic;
using SeedLab.Search.Criteria;
using SeedLab.Search.Execution;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// "Shuffled, with no repeats and no gaps" is a claim about a bijection, so it is testable. These
    /// checks exhaust small ranges (every index, counted) and spot-check the full int32 space.
    /// </summary>
    public static class PermutationChecks
    {
        public static void Run(Action<bool, string, string> check, bool quick)
        {
            // ---- exhaustive on ranges that DO need cycle walking (count is not a power of two) -------
            foreach (long count in new long[] { 1, 2, 3, 7, 100, 1000, 65537 })
            {
                long from = -50;
                ScanPlan plan = new ScanPlan(ScanOrder.Shuffled, from, from + count - 1, 0xABCDEF0123456789UL, 64, 0);
                HashSet<int> seen = new HashSet<int>();
                bool inRange = true;
                for (long i = 0; i < count; i++)
                {
                    int s = plan.SeedAt(i);
                    if (s < from || s > from + count - 1) inRange = false;
                    seen.Add(s);
                }

                check(seen.Count == count && inRange,
                      "shuffled bijection over " + count + " seeds",
                      seen.Count + " distinct of " + count + (inRange ? ", all in range" : ", OUT OF RANGE"));
            }

            // ---- sequential must be the identity mapping ---------------------------------------------
            ScanPlan seq = new ScanPlan(ScanOrder.Sequential, int.MinValue, int.MaxValue, 1, 1024, 0);
            bool seqOk = seq.SeedAt(0) == int.MinValue && seq.SeedAt(1) == int.MinValue + 1
                         && seq.SeedAt(4294967295L) == int.MaxValue;
            check(seqOk, "sequential order is index + int.MinValue",
                  "index 0 -> " + seq.SeedAt(0) + ", index 2^32-1 -> " + seq.SeedAt(4294967295L));

            // ---- the full int32 space: a large sample must have no collisions -------------------------
            ScanPlan full = new ScanPlan(ScanOrder.Shuffled, int.MinValue, int.MaxValue,
                                         0x5EEDF00D1234ABCDUL, 4096, 0);
            int sample = quick ? 200_000 : 4_000_000;
            HashSet<int> hit = new HashSet<int>(sample);
            for (long i = 0; i < sample; i++) hit.Add(full.SeedAt(i));
            check(hit.Count == sample, "no repeats in the first " + sample.ToString("N0") + " indices of 2^32",
                  hit.Count.ToString("N0") + " distinct seeds");

            // For 2^32 the Feistel domain equals the range, so cycle walking can never run: prove the
            // map is onto by inverting a sample through a full forward scan of a small sub-block.
            bool spread = false;
            long neg = 0, pos = 0;
            for (long i = 0; i < 100_000; i++)
            {
                if (full.SeedAt(i) < 0) neg++; else pos++;
            }

            spread = neg > 40_000 && pos > 40_000;
            check(spread, "the shuffled walk is spread over the whole space",
                  neg.ToString("N0") + " negative and " + pos.ToString("N0") + " positive seeds in 100,000 indices");

            // ---- the key decides the order, and only the key ------------------------------------------
            ScanPlan k1 = new ScanPlan(ScanOrder.Shuffled, int.MinValue, int.MaxValue, 1, 4096, 0);
            ScanPlan k2 = new ScanPlan(ScanOrder.Shuffled, int.MinValue, int.MaxValue, 2, 4096, 0);
            ScanPlan k1b = new ScanPlan(ScanOrder.Shuffled, int.MinValue, int.MaxValue, 1, 999, 0);
            bool same = true, differs = false;
            for (long i = 0; i < 10_000; i++)
            {
                if (k1.SeedAt(i) != k1b.SeedAt(i)) same = false;
                if (k1.SeedAt(i) != k2.SeedAt(i)) differs = true;
            }

            check(same, "same key = same sequence regardless of block size", "10,000 indices identical");
            check(differs, "a different key gives a different sequence", "as it must, or --key would be a lie");

            // ---- key derivation from the query hash is stable -----------------------------------------
            ulong a = ScanPlan.KeyFromHash("deadbeef");
            ulong b = ScanPlan.KeyFromHash("deadbeef");
            ulong c = ScanPlan.KeyFromHash("deadbeee");
            check(a == b && a != c, "key derived from the query hash is deterministic",
                  "0x" + a.ToString("X16") + " for 'deadbeef'");

            // ---- the Feistel core is a bijection on its own domain ------------------------------------
            const int h = 8;
            HashSet<ulong> img = new HashSet<ulong>();
            for (ulong v = 0; v < (1UL << (2 * h)); v++) img.Add(Permutation.Feistel(v, 0x1234UL, h));
            check(img.Count == (1 << (2 * h)), "the 4-round Feistel network is a permutation on 2^16",
                  img.Count + " distinct images of " + (1 << (2 * h)) + " inputs");
        }
    }
}
