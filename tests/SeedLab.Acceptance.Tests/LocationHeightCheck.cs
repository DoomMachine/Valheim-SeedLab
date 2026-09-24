using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SeedLab.Saves;
using SeedLab.WorldGen;

namespace SeedLabAcceptanceTests
{
    /// <summary>
    /// T5 of 05-validation.md: the location instances in _main.&lt;N&gt;.db2 are a FLOAT32 oracle for
    /// WorldGenerator.GetHeight, and the only one there is - the minimap cache is quantised to half.
    ///
    /// ZoneSystem.GenerateLocationsTimeSliced (decomp lines 1954-2073):
    ///     Vector3 randomPointInZone = GetRandomPointInZone(zoneID, maxRadius);     // y = 0
    ///     randomPointInZone.y = WorldGenerator.instance.GetHeight(randomPointInZone.x, randomPointInZone.z, out var mask);
    ///     RegisterLocation(location, randomPointInZone, generated: false);
    /// RegisterLocation stores the Vector3 unchanged, and PlaceLocations only ever copies it into a
    /// local before snapping - it never writes a new position back (decomp lines 2198-2229). So every
    /// stored y is exactly GetHeight(x, z) as float32, including the river and stream contribution,
    /// and it needs no reproduction of the placement RNG.
    ///
    /// GetHeight(wx, wy) is GetBiomeHeight(GetBiome(wx, wy), wx, wy, out mask) (decomp lines 998-1009),
    /// so this is the same function the minimap sweep tests, sampled at full precision instead of 11 bits.
    /// </summary>
    public static class LocationHeightCheck
    {
        /// <summary>What the summary block needs from this test.</summary>
        public sealed class Result
        {
            public int Total, Exact, RiverTouched, RiverTouchedBad, NoRiverBad;
            public double MaxMetres;
            public int MaxUlps;
        }

        private sealed class Worst
        {
            public double Metres;
            public int Ulps;
            public float X, Z, Expected, Got;
            public Biome B;
            public float RiverWeight, RiverWidth;
            public bool Placed;
        }

        /// <summary>Monotone ordinal over float32 bit patterns, so a difference is a count of float ULPs.</summary>
        private static long Ordinal(float f)
        {
            int b = BitConverter.SingleToInt32Bits(f);
            return b >= 0 ? (long)b + 0x80000000L : 0x80000000L - (b & 0x7FFFFFFF);
        }

        public static Result Run(WorldFixture world, Report rep)
        {
            IReadOnlyList<LocationInstance> list = SaveChecks.Locations(world);
            WorldGeneratorPort gen = new WorldGeneratorPort(world.Seed, 2, menu: false);

            int n = list.Count;
            long[] exact = new long[1];
            object gate = new object();
            long[] ulpHist = new long[9];          // 0, 1, 2, 3-4, 5-8, 9-16, 17-64, 65-1024, more
            long[] byBiome = new long[10];
            double maxMetres = 0;
            List<Worst> worsts = new List<Worst>();
            List<Worst> dryWorsts = new List<Worst>();
            long riverTouched = 0, riverTouchedBad = 0;
            long belowSeaLevel = 0, belowSeaLevelBad = 0;
            long placedTotal = 0, placedBad = 0;
            List<(float x, float z)> differing = new List<(float, float)>();

            int workers = Math.Max(1, Environment.ProcessorCount);
            int chunk = Math.Max(1, (n + workers - 1) / workers);
            Parallel.For(0, (n + chunk - 1) / chunk, c =>
            {
                WorldGeneratorPort w = gen.Fork();
                WorldGeneratorPort probe = gen.Fork();
                long lExact = 0, lRiver = 0, lRiverBad = 0, lShallow = 0, lShallowBad = 0;
                long lPlaced = 0, lPlacedBad = 0;
                long[] lUlp = new long[9];
                long[] lBiome = new long[10];
                double lMax = 0;
                List<Worst> lWorst = new List<Worst>();
                List<Worst> lDry = new List<Worst>();
                List<(float, float)> lDiff = new List<(float, float)>();

                int i0 = c * chunk, i1 = Math.Min(n, i0 + chunk);
                for (int i = i0; i < i1; i++)
                {
                    LocationInstance li = list[i];
                    Biome b = w.GetBiome(li.X, li.Z);
                    float h = w.GetBiomeHeight(b, li.X, li.Z, out _);

                    probe.GetRiverWeightPublic(li.X, li.Z, out float rw, out float rwid);
                    bool touched = rw > 0f;
                    if (touched) lRiver++;
                    if (li.Y < 30f) lShallow++;
                    if (li.Placed) lPlaced++;

                    bool ok = BitConverter.SingleToInt32Bits(h) == BitConverter.SingleToInt32Bits(li.Y);
                    if (ok) { lExact++; lUlp[0]++; continue; }

                    if (touched) lRiverBad++;
                    if (li.Y < 30f) lShallowBad++;
                    // An instance with m_placed = true was written to the save when a player first
                    // visited its zone, possibly on an older game build; an unplaced one was produced by
                    // the current build's generator. If the differences clustered on placed instances
                    // the oracle would be stale rather than the port wrong.
                    if (li.Placed) lPlacedBad++;
                    lDiff.Add((li.X, li.Z));
                    int gi = b.ToIndex();
                    lBiome[(gi >= 0 && gi <= 8) ? gi : 9]++;

                    long d = Math.Abs(Ordinal(h) - Ordinal(li.Y));
                    int bucket = d == 1 ? 1 : d == 2 ? 2 : d <= 4 ? 3 : d <= 8 ? 4 : d <= 16 ? 5
                               : d <= 64 ? 6 : d <= 1024 ? 7 : 8;
                    lUlp[bucket]++;
                    double metres = Math.Abs((double)h - (double)li.Y);
                    if (metres > lMax) lMax = metres;
                    Worst worst = new Worst
                    {
                        Metres = metres,
                        Ulps = d > int.MaxValue ? int.MaxValue : (int)d,
                        X = li.X, Z = li.Z, Expected = li.Y, Got = h, B = b,
                        RiverWeight = rw, RiverWidth = rwid, Placed = li.Placed,
                    };
                    lWorst.Add(worst);
                    lWorst.Sort((p, q) => q.Metres.CompareTo(p.Metres));
                    if (lWorst.Count > 10) lWorst.RemoveAt(lWorst.Count - 1);
                    if (!touched)
                    {
                        lDry.Add(worst);
                        lDry.Sort((p, q) => q.Metres.CompareTo(p.Metres));
                        if (lDry.Count > 6) lDry.RemoveAt(lDry.Count - 1);
                    }
                }

                lock (gate)
                {
                    exact[0] += lExact;
                    riverTouched += lRiver; riverTouchedBad += lRiverBad;
                    belowSeaLevel += lShallow; belowSeaLevelBad += lShallowBad;
                    placedTotal += lPlaced; placedBad += lPlacedBad;
                    differing.AddRange(lDiff);
                    for (int a = 0; a < 9; a++) ulpHist[a] += lUlp[a];
                    for (int a = 0; a < 10; a++) byBiome[a] += lBiome[a];
                    if (lMax > maxMetres) maxMetres = lMax;
                    worsts.AddRange(lWorst);
                    worsts.Sort((p, q) => q.Metres.CompareTo(p.Metres));
                    while (worsts.Count > 10) worsts.RemoveAt(worsts.Count - 1);
                    dryWorsts.AddRange(lDry);
                    dryWorsts.Sort((p, q) => q.Metres.CompareTo(p.Metres));
                    while (dryWorsts.Count > 6) dryWorsts.RemoveAt(dryWorsts.Count - 1);
                }
            });

            long bad = n - exact[0];
            Console.WriteLine("  " + world.Name + ": " + n.ToString("N0") + " instances from the .db2, "
                              + exact[0].ToString("N0") + " bit-exact, " + bad + " differing"
                              + "   (river-touched " + riverTouched.ToString("N0") + ", of which " + riverTouchedBad
                              + " differ; below 30 m " + belowSeaLevel.ToString("N0") + ", of which " + belowSeaLevelBad + " differ)");
            if (bad > 0)
            {
                string[] names = { "exact", "1 ulp", "2 ulps", "3-4", "5-8", "9-16", "17-64", "65-1024", ">1024" };
                StringBuilder sb = new StringBuilder("    float-ULP histogram: ");
                for (int a = 0; a < 9; a++) if (ulpHist[a] > 0) sb.Append(names[a]).Append('=').Append(ulpHist[a]).Append("  ");
                Console.WriteLine(sb.ToString());
                Console.WriteLine("    max |diff| " + maxMetres.ToString("G6") + " m");
                foreach (Worst wst in worsts)
                    Console.WriteLine("      (" + wst.X + ", " + wst.Z + ")  biome " + wst.B
                                      + "  save " + wst.Expected + "  ours " + wst.Got
                                      + "  " + wst.Ulps + " ulps  " + wst.Metres.ToString("G6") + " m"
                                      + "  riverWeight " + wst.RiverWeight.ToString("G6")
                                      + " width " + wst.RiverWidth.ToString("G6"));
                StringBuilder bb = new StringBuilder("    differing by biome: ");
                for (int a = 0; a < 10; a++) if (byBiome[a] > 0) bb.Append(WorldSweep.BiomeName(a)).Append('=').Append(byBiome[a]).Append("  ");
                Console.WriteLine(bb.ToString());
                Console.WriteLine("    m_placed = true on " + placedBad + " of the " + bad + " differing instances ("
                                  + placedTotal + " of all " + n.ToString("N0") + " are placed)");
                RiverDiagnostic.Describe(gen, differing, "dominating river point of the differing samples");
                // The differing samples that no river touches at all: these cannot be explained by the
                // river pass, so they bound how much of the residual is base/biome terrain arithmetic.
                Console.WriteLine("    differing samples with ZERO river weight: " + (bad - riverTouchedBad)
                                  + " of " + bad + "  (worst of them:)");
                foreach (Worst wst in dryWorsts)
                    Console.WriteLine("      (" + wst.X + ", " + wst.Z + ")  biome " + wst.B
                                      + "  save " + wst.Expected + "  ours " + wst.Got
                                      + "  " + wst.Ulps + " ulps  " + wst.Metres.ToString("G6") + " m"
                                      + (wst.Placed ? "  [placed]" : ""));
            }

            int maxUlps = 0;
            foreach (Worst wst in worsts) if (wst.Ulps > maxUlps) maxUlps = wst.Ulps;
            rep.Add(world.Name + "/T5", "GetHeight vs the .db2's float32 y", bad == 0,
                    exact[0].ToString("N0") + " / " + n.ToString("N0") + " bit-exact"
                    + (bad == 0 ? "" : ", max |diff| " + maxMetres.ToString("G6") + " m")
                    + "; " + riverTouched.ToString("N0") + " of them sit in a river/stream field");

            return new Result
            {
                Total = n,
                Exact = (int)exact[0],
                RiverTouched = (int)riverTouched,
                RiverTouchedBad = (int)riverTouchedBad,
                NoRiverBad = (int)(bad - riverTouchedBad),
                MaxMetres = maxMetres,
                MaxUlps = maxUlps,
            };
        }
    }
}
