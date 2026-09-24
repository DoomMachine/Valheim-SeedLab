using System;
using System.Collections.Generic;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// The proof that <c>deferPregeneration: true</c> is a deferral and not a different world.
    ///
    /// <para>The search engine builds every generator with the flag set, because the lake/river/stream
    /// pre-generation is ~99.5 % of the cost of a world and is read only by heights and by the
    /// river/lake/stream accessors. If deferring it changed anything, every search result would be
    /// suspect - so this check compares, for a set of seeds, the whole pregenerated state and a dense
    /// grid of heights, <b>bit for bit</b>, between an eager handle and a deferred one.</para>
    ///
    /// <para>Bit-for-bit is the right standard here and not an overreach: the deferred path runs the
    /// identical four calls from the identical <c>UnityRandom</c> state, so any difference at all is a
    /// defect, not a rounding artefact.</para>
    /// </summary>
    public static class PregenEquivalence
    {
        public static void Run(Action<bool, string, string> check, int seedCount)
        {
            int[] seeds = Seeds(seedCount);

            int worstHeightSeed = 0;
            long heightsCompared = 0, heightMismatches = 0, biomesCompared = 0, biomeMismatches = 0;
            int riverMismatch = 0, streamMismatch = 0, pointMismatch = 0, lakeMismatch = 0;

            foreach (int seed in seeds)
            {
                WorldGeneratorPort eager = new WorldGeneratorPort(seed, 2, menu: false);
                WorldGeneratorPort lazy = new WorldGeneratorPort(seed, 2, menu: false, deferPregeneration: true);

                // Biomes and base heights first, BEFORE anything triggers pregeneration on the deferred
                // handle: this is the state a T2-only search actually runs in.
                for (int i = 0; i < 64; i++)
                {
                    for (int j = 0; j < 64; j++)
                    {
                        float wx = -10000f + i * (20000f / 63f);
                        float wy = -10000f + j * (20000f / 63f);
                        biomesCompared++;
                        if (eager.GetBiome(wx, wy) != lazy.GetBiome(wx, wy)) biomeMismatches++;
                        if (BitConverter.SingleToInt32Bits(eager.GetBaseHeightPublic(wx, wy))
                            != BitConverter.SingleToInt32Bits(lazy.GetBaseHeightPublic(wx, wy)))
                        {
                            biomeMismatches++;
                        }
                    }
                }

                bool wasPending = lazy.PregenerationPending;
                if (!wasPending) check(false, "seed " + seed + " deferred handle should still be pending", "");

                // Now force it and compare the pregenerated state itself.
                IReadOnlyList<WorldGeneratorPort.River> er = eager.GetRivers(), lr = lazy.GetRivers();
                if (!SameRivers(er, lr)) riverMismatch++;

                IReadOnlyList<WorldGeneratorPort.River> es = eager.GetStreams(), ls = lazy.GetStreams();
                if (!SameRivers(es, ls)) streamMismatch++;

                IReadOnlyList<Vec2>? el = eager.GetLakes(), ll = lazy.GetLakes();
                if (!SameLakes(el, ll)) lakeMismatch++;

                if (!SamePoints(eager.GetRiverPoints(), lazy.GetRiverPoints())) pointMismatch++;

                // And the thing that actually reads it: GetHeight, over a dense grid, bit-exact.
                long before = heightMismatches;
                for (int i = 0; i < 96; i++)
                {
                    for (int j = 0; j < 96; j++)
                    {
                        float wx = -10500f + i * (21000f / 95f);
                        float wy = -10500f + j * (21000f / 95f);
                        heightsCompared++;
                        if (BitConverter.SingleToInt32Bits(eager.GetHeight(wx, wy))
                            != BitConverter.SingleToInt32Bits(lazy.GetHeight(wx, wy)))
                        {
                            heightMismatches++;
                        }
                    }
                }

                if (heightMismatches > before) worstHeightSeed = seed;
            }

            check(biomeMismatches == 0,
                  "biome and base height before pre-generation",
                  biomesCompared.ToString("N0") + " points x " + seeds.Length + " seeds, "
                  + biomeMismatches + " mismatches");
            check(riverMismatch == 0, "river list identical", riverMismatch + " of " + seeds.Length + " seeds differ");
            check(streamMismatch == 0, "stream list identical", streamMismatch + " of " + seeds.Length + " seeds differ");
            check(lakeMismatch == 0, "lake list identical", lakeMismatch + " of " + seeds.Length + " seeds differ");
            check(pointMismatch == 0, "river-point grid identical", pointMismatch + " of " + seeds.Length + " seeds differ");
            check(heightMismatches == 0,
                  "GetHeight bit-exact after the deferred pre-generation",
                  heightsCompared.ToString("N0") + " samples, " + heightMismatches + " mismatches"
                  + (heightMismatches > 0 ? " (first bad seed " + worstHeightSeed + ")" : ""));

            // The flag must not leak: a forked handle is never left pending, because Fork() pregenerates.
            WorldGeneratorPort parent = new WorldGeneratorPort(12345, 2, menu: false, deferPregeneration: true);
            WorldGeneratorPort fork = parent.Fork();
            check(!fork.PregenerationPending && !parent.PregenerationPending,
                  "Fork() forces pre-generation on both handles", "so two threads cannot race to run it");

            // A menu world has nothing to pre-generate and must behave identically either way.
            WorldGeneratorPort menuEager = new WorldGeneratorPort(999, 2, menu: true);
            WorldGeneratorPort menuLazy = new WorldGeneratorPort(999, 2, menu: true, deferPregeneration: true);
            bool menuSame = menuEager.GetLakes() == null && menuLazy.GetLakes() == null
                            && menuEager.GetRivers().Count == 0 && menuLazy.GetRivers().Count == 0
                            && BitConverter.SingleToInt32Bits(menuEager.GetHeight(100f, 100f))
                               == BitConverter.SingleToInt32Bits(menuLazy.GetHeight(100f, 100f));
            check(menuSame, "menu world unaffected by the flag", "no lakes, no rivers, same height");
        }

        /// <summary>A spread of seeds: the two ground-truth worlds, the edges, and a fixed pseudo-random set.</summary>
        private static int[] Seeds(int n)
        {
            List<int> s = new List<int> { -1772362158, 319486907, 0, 1, -1, int.MinValue, int.MaxValue };
            ulong x = 0x9E3779B97F4A7C15UL;
            while (s.Count < n)
            {
                x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                s.Add(unchecked((int)x));
            }

            return s.ToArray();
        }

        private static bool SameRivers(IReadOnlyList<WorldGeneratorPort.River> a,
                                       IReadOnlyList<WorldGeneratorPort.River> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                WorldGeneratorPort.River x = a[i], y = b[i];
                if (!Same(x.p0, y.p0) || !Same(x.p1, y.p1) || !Same(x.center, y.center)) return false;
                if (!Bits(x.widthMin, y.widthMin) || !Bits(x.widthMax, y.widthMax)) return false;
                if (!Bits(x.curveWidth, y.curveWidth) || !Bits(x.curveWavelength, y.curveWavelength)) return false;
            }

            return true;
        }

        private static bool SameLakes(IReadOnlyList<Vec2>? a, IReadOnlyList<Vec2>? b)
        {
            if (a == null || b == null) return ReferenceEquals(a, b) || (a == null && b == null);
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!Same(a[i], b[i])) return false;
            }

            return true;
        }

        private static bool SamePoints(IReadOnlyDictionary<Vec2i, WorldGeneratorPort.RiverPoint[]> a,
                                       IReadOnlyDictionary<Vec2i, WorldGeneratorPort.RiverPoint[]> b)
        {
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<Vec2i, WorldGeneratorPort.RiverPoint[]> kv in a)
            {
                if (!b.TryGetValue(kv.Key, out WorldGeneratorPort.RiverPoint[]? other)) return false;
                if (other.Length != kv.Value.Length) return false;
                for (int i = 0; i < other.Length; i++)
                {
                    if (!Same(kv.Value[i].p, other[i].p)) return false;
                    if (!Bits(kv.Value[i].w, other[i].w) || !Bits(kv.Value[i].w2, other[i].w2)) return false;
                }
            }

            return true;
        }

        private static bool Same(Vec2 a, Vec2 b) => Bits(a.x, b.x) && Bits(a.y, b.y);

        private static bool Bits(float a, float b)
            => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    }
}
