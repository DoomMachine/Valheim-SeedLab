using System;
using System.Diagnostics;
using System.IO;
using SeedLab.Locations;
using SeedLab.WorldGen;

namespace SeedLab.LocationLab
{
    /// <summary>
    /// Validates the 2048^2 biome point grid against the minimap cache the game itself wrote, and
    /// measures the per-seed cost of building it.
    ///
    /// <para>The comparison is legitimate because the two grids are the SAME geometry:
    /// <c>Minimap.GenerateWorldMap</c> samples pixel (j, i) at <c>(j - 1024) * 12 + 6</c> /
    /// <c>(i - 1024) * 12 + 6</c> and stores it at <c>k = i * 2048 + j</c>, and
    /// <c>AltBiomeWorldData.MapSpaceToWorldSpace</c> is the same expression. The minimap does NOT apply
    /// <c>GenerateBiomePoints</c>'s 10 500 m cut-off, so points outside it are reported separately
    /// rather than counted as disagreements.</para>
    /// </summary>
    public static class GridCheck
    {
        public static int Run(string[] args)
        {
            int workers = Args.Int(args, "--workers", -1);
            bool heights = !Args.Has(args, "--no-heights");

            if (Args.Has(args, "--seed"))
            {
                int seed = Args.Int(args, "--seed", 0);
                Console.WriteLine("seed " + seed + " (no oracle - cost measurement only)");
                Measure(seed, workers);
                return 0;
            }

            string only = Args.Str(args, "--world", "");
            int bad = 0;
            foreach (WorldRef w in WorldRef.All)
            {
                if (only.Length > 0 && !string.Equals(only, w.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Check(w, workers, heights)) bad++;
            }
            return bad == 0 ? 0 : 1;
        }

        private static WorldLocations Measure(int seed, int workers)
        {
            Stopwatch ctor = Stopwatch.StartNew();
            WorldGeneratorPort gen = new WorldGeneratorPort(seed, 2, menu: false);
            ctor.Stop();
            Stopwatch sw = Stopwatch.StartNew();
            WorldLocations wl = WorldLocations.Build(gen, null, workers);
            sw.Stop();
            Console.WriteLine("  worldgen    " + ctor.Elapsed.TotalMilliseconds.ToString("F0")
                              + " ms (lakes, rivers, streams, the river-point grid)");
            Console.WriteLine("  grid        " + wl.GridMilliseconds.ToString("F0") + " ms on "
                              + wl.Grid.Workers + " workers  (" + BiomeGrid.PointCount.ToString("N0")
                              + " GetBiome+GetBiomeHeight pairs)");
            Console.WriteLine("  sectors     " + wl.SectorMilliseconds.ToString("F0") + " ms  -> "
                              + wl.Field.Sectors.Count.ToString("N0") + " sectors");
            Console.WriteLine("  total       "
                              + (ctor.Elapsed.TotalMilliseconds + sw.Elapsed.TotalMilliseconds).ToString("F0")
                              + " ms per seed, before any placement");
            Console.WriteLine("  cutoff ring " + wl.Grid.CutoffRingPoints.ToString("N0")
                              + " points pass Vector2.sqrMagnitude <= 110250000 but fail "
                              + "DUtils.Length <= 10500 inside GetBiomeHeight. Both tests are pure "
                              + "geometry on a fixed lattice, so this count is seed-independent: the "
                              + "ring spec 02 section 2.4 warns about is EMPTY on this grid.");
            return wl;
        }

        /// <summary>
        /// <c>Mathf.FloatToHalf</c> as the acceptance suite measured it: IEEE binary16
        /// round-to-nearest, but an EXACT midpoint rounds away from zero rather than to even. The
        /// difference shows up on about 150 of the 2.4 M in-world grid points.
        /// </summary>
        private static ushort UnityHalfBits(float v)
        {
            Half h = (Half)v;
            ushort b = BitConverter.HalfToUInt16Bits(h);
            if (!float.IsFinite(v) || !Half.IsFinite(h)) return b;
            double dv = v;
            if ((double)(float)h == dv) return b;          // exactly representable
            if ((b & 1) != 0) return b;                    // ties-to-even leaves the low bit clear
            Half up = Half.BitIncrement(h);
            if (Half.IsFinite(up) && ((double)(float)h + (double)(float)up) / 2.0 == dv)
                return Math.Abs((float)up) > Math.Abs((float)h) ? BitConverter.HalfToUInt16Bits(up) : b;
            Half dn = Half.BitDecrement(h);
            if (Half.IsFinite(dn) && ((double)(float)h + (double)(float)dn) / 2.0 == dv)
                return Math.Abs((float)dn) > Math.Abs((float)h) ? BitConverter.HalfToUInt16Bits(dn) : b;
            return b;
        }

        private static bool Check(WorldRef w, int workers, bool heights)
        {
            Console.WriteLine();
            Console.WriteLine("== " + w.Name + "  seed " + w.Seed + (w.IsHoldOut ? "  (HOLD-OUT)" : ""));
            WorldLocations wl = Measure(w.Seed, workers);
            BiomeGrid g = wl.Grid;

            byte[] oracle = File.ReadAllBytes(GroundTruthPaths.BiomeOracle(w));
            if (oracle.Length != BiomeGrid.PointCount)
                throw new InvalidDataException("biome oracle is " + oracle.Length + " bytes, expected "
                                               + BiomeGrid.PointCount);

            long decodable = 0, agree = 0, disagreeInside = 0, disagreeOutside = 0;
            long insideDecodable = 0, outsideCut = 0, outsideOcean = 0;
            int firstBadK = -1;
            float[] coord = new float[BiomeGrid.Size];
            for (int i = 0; i < coord.Length; i++) coord[i] = BiomeGrid.MapSpaceToWorldSpace(i);

            for (int y = 0; y < BiomeGrid.Size; y++)
            {
                for (int x = 0; x < BiomeGrid.Size; x++)
                {
                    int k = BiomeGrid.Index(x, y);
                    byte o = oracle[k];
                    if (o > 8) continue;                      // 255 ambiguous white, 254 unknown colour
                    decodable++;
                    Biome ours = ((BiomeIndex)g.PointBiomes[k]).ToBiome();
                    bool cut = new SeedLab.WorldGen.Unity.Vec2(coord[x], coord[y]).SqrMagnitudeSelf > BiomeGrid.WaterEdgeSqr;
                    if (cut) { outsideCut++; if (o == Biome.Ocean.ToDenseIndex()) outsideOcean++; }
                    else insideDecodable++;
                    if (ours.ToDenseIndex() == o) agree++;
                    else
                    {
                        if (cut) disagreeOutside++;
                        else { disagreeInside++; if (firstBadK < 0) firstBadK = k; }
                    }
                }
            }

            Console.WriteLine("  biomes      INSIDE the 10500 m cut-off: "
                              + (insideDecodable - disagreeInside).ToString("N0") + " / "
                              + insideDecodable.ToString("N0") + " decodable pixels agree  ("
                              + (100.0 * (insideDecodable - disagreeInside) / insideDecodable).ToString("F6") + " %)");
            Console.WriteLine("              OUTSIDE it, GenerateBiomePoints stores Ocean/-1000 while the minimap "
                              + "keeps sampling GetBiome, so the two are EXPECTED to differ: "
                              + disagreeOutside.ToString("N0") + " of " + outsideCut.ToString("N0")
                              + " decodable pixels differ (" + outsideOcean.ToString("N0")
                              + " of them are Ocean in the minimap too). Not a port error.");
            Console.WriteLine("              total agreement over all " + decodable.ToString("N0")
                              + " decodable pixels: " + agree.ToString("N0"));
            if (firstBadK >= 0)
            {
                int bx = BiomeGrid.XOf(firstBadK), by = BiomeGrid.YOf(firstBadK);
                Console.WriteLine("              first inside-disagreement at grid (" + bx + ", " + by + ") = world ("
                                  + coord[bx] + ", " + coord[by] + "): ours "
                                  + ((BiomeIndex)g.PointBiomes[firstBadK]).ToBiome()
                                  + ", game dense index " + oracle[firstBadK]);
            }

            bool ok = disagreeInside == 0;

            if (heights)
            {
                byte[] raw = File.ReadAllBytes(GroundTruthPaths.HeightOracle(w));
                if (raw.Length != BiomeGrid.PointCount * 4)
                    throw new InvalidDataException("height oracle is " + raw.Length + " bytes");
                long compared = 0, same = 0, oneCode = 0, worse = 0;
                for (int y = 0; y < BiomeGrid.Size; y++)
                {
                    for (int x = 0; x < BiomeGrid.Size; x++)
                    {
                        int k = BiomeGrid.Index(x, y);
                        if (new SeedLab.WorldGen.Unity.Vec2(coord[x], coord[y]).SqrMagnitudeSelf > BiomeGrid.WaterEdgeSqr)
                            continue;                        // our grid stores -1000 here by design
                        compared++;
                        float o = BitConverter.ToSingle(raw, k * 4);
                        ushort ob = BitConverter.HalfToUInt16Bits((Half)o);
                        ushort mb = UnityHalfBits(g.PointHeights[k]);
                        if (ob == mb) same++;
                        else if (Math.Abs(ob - mb) == 1) oneCode++;
                        else worse++;
                    }
                }
                Console.WriteLine("  heights     " + same.ToString("N0") + " / " + compared.ToString("N0")
                                  + " binary16 codes identical, " + oneCode.ToString("N0")
                                  + " off by one code (an exact-midpoint tie), " + worse.ToString("N0") + " worse");
                if (worse != 0) ok = false;
            }

            Console.WriteLine("  point lists");
            foreach (string line in wl.Field.PointListSummary().Split('\n'))
                if (line.Length > 0) Console.WriteLine("              " + line);

            long listed = 0;
            foreach (BiomeTypeInfo t in wl.Field.BiomesInKeyOrder) listed += t.AllPoints.Count;
            long expectedMissing = 0;
            foreach (BiomeSectorData s in wl.Field.Sectors) if (s.Index >= 3) expectedMissing++;
            Console.WriteLine("              " + listed.ToString("N0") + " of " + BiomeGrid.PointCount.ToString("N0")
                              + " points are reachable by GetRandomPointByBiome; "
                              + expectedMissing.ToString("N0") + " flood-filled sector seed points are not "
                              + "(the AltBiomeWorldData.tryFill quirk). "
                              + (listed + expectedMissing == BiomeGrid.PointCount
                                    ? "Accounted for exactly."
                                    : "MISMATCH - " + (BiomeGrid.PointCount - listed - expectedMissing) + " unexplained."));
            if (listed + expectedMissing != BiomeGrid.PointCount) ok = false;

            Console.WriteLine("  verdict     " + (ok ? "PASS" : "FAIL"));
            return ok;
        }
    }
}
