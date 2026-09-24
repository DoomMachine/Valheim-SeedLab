using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLabTests
{
    /// <summary>
    /// Validation harness for the WorldGenerator port.
    ///
    /// It regenerates the biome and the height of every pixel centre of a minimap cache the GAME wrote
    /// and compares them. The .biome.u8 / .height.f32 files under groundtruth\decoded were decoded from
    /// the world's own cacheMinimapBiome / cacheMinimapHeight, so they are an oracle, not a reference
    /// implementation: a non-zero biome mismatch count is a bug in the port, never a tolerance to widen.
    ///
    ///   Minimap.GenerateWorldMap (decomp 1950-1980) samples pixel centres
    ///       wy = (i - textureSize/2) * pixelSize + pixelSize/2      (row 0 = SOUTH)
    ///       wx = (j - textureSize/2) * pixelSize + pixelSize/2      (col 0 = WEST)
    ///   and stores GetBiomeHeight(GetBiome(wx, wy), wx, wy, out _) through
    ///   Utils.FloatsToCompressedHalfBuffer, i.e. quantised to IEEE half.
    ///
    /// Usage:  dotnet run --project tests\SeedLab.Tests [-- &lt;worldName&gt;]
    /// Default world: asdasdasd (seed -1772362158, the development seed).
    /// The hold-out world testworldclaude (seed 319486907) is deliberately NOT the default.
    /// </summary>
    public static class Program
    {
        private const int TextureSize = 2048;
        private const float PixelSize = 12.0f;

        public static int Main(string[] args)
        {
            // The native-function goldens (goldens the GAME wrote through the dumper plugin) are a
            // separate, sub-second suite: dotnet run --project tests\SeedLab.Tests -- natives [--verbose]
            if (args.Length > 0 && args[0] == "natives") return NativesGoldens.Run(args);

            // PT1: generator code writes the profiler and never reads it - an IL-level check, seconds:
            // dotnet run --project tests\SeedLab.Tests -c Release -- profile-tripwire
            if (args.Length > 0 && args[0] == "profile-tripwire") return ProfileTripwire.Run(args);

            string world = (args.Length > 0 && !args[0].StartsWith("-")) ? args[0] : "asdasdasd";
            int seed = world switch
            {
                "asdasdasd" => -1772362158,        // seed text "MWd8eV6svz"
                "testworldclaude" => 319486907,    // seed text "hnBd9gJf2G" - HOLD OUT
                _ => 0,
            };
            if (seed == 0)
            {
                Console.Error.WriteLine("Unknown world '" + world + "'. Known: asdasdasd, testworldclaude.");
                return 2;
            }

            string? gt = FindGroundTruth();
            if (gt == null)
            {
                Console.Error.WriteLine("Could not locate the groundtruth folder (looked upwards from "
                                        + Directory.GetCurrentDirectory() + " and " + AppContext.BaseDirectory + ").");
                return 2;
            }

            string biomePath = Path.Combine(gt, "decoded", world + ".biome.u8");
            string heightPath = Path.Combine(gt, "decoded", world + ".height.f32");
            if (!File.Exists(biomePath) || !File.Exists(heightPath))
            {
                Console.Error.WriteLine("Missing oracle files: " + biomePath + " / " + heightPath);
                return 2;
            }

            Console.WriteLine("SeedLab WorldGen validation");
            Console.WriteLine("  world            " + world);
            Console.WriteLine("  seed             " + seed);
            Console.WriteLine("  worldGenVersion  2");
            Console.WriteLine("  oracle           " + gt);
            Console.WriteLine();

            byte[] oracleBiome = File.ReadAllBytes(biomePath);
            byte[] heightBytes = File.ReadAllBytes(heightPath);
            int n = TextureSize * TextureSize;
            if (oracleBiome.Length != n || heightBytes.Length != n * 4)
            {
                Console.Error.WriteLine("Oracle size mismatch: biome " + oracleBiome.Length + " bytes, height "
                                        + heightBytes.Length + " bytes; expected " + n + " and " + (n * 4) + ".");
                return 2;
            }
            float[] oracleHeight = new float[n];
            Buffer.BlockCopy(heightBytes, 0, oracleHeight, 0, n * 4);

            // --- construct ------------------------------------------------------------------------
            Stopwatch sw = Stopwatch.StartNew();
            WorldGeneratorPort gen = new WorldGeneratorPort(seed, 2, menu: false);
            sw.Stop();
            Console.WriteLine("Construction + pregeneration: " + sw.ElapsedMilliseconds + " ms");
            Console.WriteLine("  offsets   " + gen.Offset0 + ", " + gen.Offset1 + ", " + gen.Offset2 + ", "
                              + gen.Offset3 + ", " + gen.Offset4);
            Console.WriteLine("  riverSeed " + gen.RiverSeed + "   streamSeed " + gen.StreamSeed);
            Console.WriteLine("  lakes " + (gen.GetLakes()?.Count ?? -1)
                              + "   rivers " + gen.GetRivers().Count
                              + "   streams(pass1 list) " + gen.GetStreams().Count
                              + "   river grid cells " + gen.GetRiverPoints().Count);
            Console.WriteLine();

            // --- smoke test of the API paths the sweep never touches -------------------------------
            // None of these has an oracle yet; they are printed so a later agent can diff them against
            // dumped in-game values instead of re-deriving them.
            {
                WorldGeneratorPort probe = gen.Fork();
                probe.GetAshlandsHeight(0f, -9000f, out ColorRGBA am, cheap: false);
                float cheapH = probe.GetAshlandsHeight(0f, -9000f, out ColorRGBA acm, cheap: true);
                float dnH = probe.GetBiomeHeight(Biome.DeepNorth, 0f, 9000f, out ColorRGBA dnm);
                float miH = probe.GetBiomeHeight(Biome.Mistlands, 7000f, 2000f, out ColorRGBA mim);
                (float nx, float ny, float nz) = probe.GetNormal(1234f, -567f);
                Console.WriteLine("API smoke test (no oracle - recorded for a later dumper comparison)");
                Console.WriteLine("  GetAshlandsHeight(0,-9000)        " + probe.GetAshlandsHeight(0f, -9000f, out _)
                                  + "   lava mask a=" + am.a + "   cheap=" + cheapH + " a=" + acm.a);
                Console.WriteLine("  DeepNorth height (0,9000)         " + dnH + "   mask g=" + dnm.g);
                Console.WriteLine("  Mistlands height (7000,2000)      " + miH + "   mask a=" + mim.a);
                Console.WriteLine("  GetNormal(1234,-567)              (" + nx + ", " + ny + ", " + nz + ")");
                Console.WriteLine("  GetForestFactor(1234,0,-567)      " + WorldGeneratorPort.GetForestFactor(1234f, 0f, -567f)
                                  + "   InForest=" + WorldGeneratorPort.InForest(1234f, 0f, -567f));
                Console.WriteLine("  GetBiomeArea((0,0)) " + probe.GetBiomeArea(new Vec2s(0, 0))
                                  + "   ((2048,0)) " + probe.GetBiomeArea(new Vec2s(2048, 0)));
                Console.WriteLine("  WorldAngle(3000,4000)             " + WorldGeneratorPort.WorldAngle(3000f, 4000f));
                Console.WriteLine("  CreateAshlandsGap(0,-8000)        " + WorldGeneratorPort.CreateAshlandsGap(0f, -8000f));
                Console.WriteLine("  CreateDeepNorthGap(0,8000)        " + WorldGeneratorPort.CreateDeepNorthGap(0f, 8000f));
                Console.WriteLine("  DeepNorthWaveFade(0,8000)         " + WorldGeneratorPort.DeepNorthWaveFade(0f, 8000f));
                Console.WriteLine("  GetAshlandsOceanGradient(0,-8000) " + WorldGeneratorPort.GetAshlandsOceanGradient(0f, -8000f));

                // Random.insideUnitCircle is still unverified, so this only proves the path runs.
                SeedLab.WorldGen.Unity.UnityRandom rnd = new SeedLab.WorldGen.Unity.UnityRandom();
                rnd.InitState(12345);
                probe.GetTerrainDelta(rnd, 500f, 0f, 500f, 10f, out float td, out (float x, float y, float z) slope);
                Console.WriteLine("  GetTerrainDelta((500,500), r=10)  delta=" + td
                                  + " slope=(" + slope.x + ", " + slope.y + ", " + slope.z + ")");

                // The menu world: pregeneration is skipped, GetBiome answers only Mountain/BlackForest.
                WorldGeneratorPort menu = new WorldGeneratorPort(0, 2, menu: true);
                Console.WriteLine("  menu world  biome(0,0)=" + menu.GetBiome(0f, 0f)
                                  + "  height(0,0)=" + menu.GetHeight(0f, 0f)
                                  + "  lakes=" + (menu.GetLakes() == null ? "null" : "??"));

                // Older world-gen versions only change three constants; check they are applied.
                WorldGeneratorPort v0 = new WorldGeneratorPort(seed, 0, menu: true);
                WorldGeneratorPort v1 = new WorldGeneratorPort(seed, 1, menu: true);
                Console.WriteLine("  VersionSetup  v0 " + v0.MinMountainDistance + "/" + v0.MinDarklandNoise + "/" + v0.MaxMarshDistance
                                  + "   v1 " + v1.MinMountainDistance + "/" + v1.MinDarklandNoise + "/" + v1.MaxMarshDistance
                                  + "   v2 " + gen.MinMountainDistance + "/" + gen.MinDarklandNoise + "/" + gen.MaxMarshDistance);
                Console.WriteLine();
            }

            // --- sweep ----------------------------------------------------------------------------
            // --serial runs the whole sweep on one handle, which is what proves that Fork() and the
            // parallel partitioning change nothing: both modes must print identical counts.
            bool serial = Array.IndexOf(args, "--serial") >= 0;
            int workers = serial ? 1 : Math.Max(1, Environment.ProcessorCount);
            int rowsPerChunk = 16;
            int chunks = (TextureSize + rowsPerChunk - 1) / rowsPerChunk;
            Stats[] partial = new Stats[chunks];

            sw.Restart();
            Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = workers }, c =>
            {
                // Fork gives this worker its own river cache over the shared, immutable world data.
                WorldGeneratorPort w = serial ? gen : gen.Fork();
                Stats st = new Stats();
                int i0 = c * rowsPerChunk;
                int i1 = Math.Min(TextureSize, i0 + rowsPerChunk);
                for (int i = i0; i < i1; i++)
                {
                    float wy = (float)(i - TextureSize / 2) * PixelSize + PixelSize / 2f;
                    int rowBase = i * TextureSize;
                    for (int j = 0; j < TextureSize; j++)
                    {
                        float wx = (float)(j - TextureSize / 2) * PixelSize + PixelSize / 2f;
                        int k = rowBase + j;

                        Biome b = w.GetBiome(wx, wy);
                        float h = w.GetBiomeHeight(b, wx, wy, out _);

                        st.Account(k, i, j, oracleBiome[k], b, oracleHeight[k], h);
                    }
                }
                partial[c] = st;
            });
            sw.Stop();
            double sweepSeconds = sw.Elapsed.TotalSeconds;

            Stats total = new Stats();
            for (int c = 0; c < chunks; c++) total.Merge(partial[c]);

            total.Probe = gen.Fork();
            total.Report(n, sweepSeconds, workers);
            return total.BiomeMismatches == 0 ? 0 : 1;
        }

        /// <summary>Walks up from the working directory and the binary directory looking for groundtruth\decoded.</summary>
        private static string? FindGroundTruth()
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                DirectoryInfo? d = new DirectoryInfo(start);
                for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                {
                    string cand = Path.Combine(d.FullName, "groundtruth");
                    if (Directory.Exists(Path.Combine(cand, "decoded"))) return cand;
                }
            }
            return null;
        }

        // -------------------------------------------------------------------------------------------
        // Statistics
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Oracle byte 255 means "white pixel": Ocean, Mountain and DeepNorth all render white on the
        /// minimap, so the colour cannot tell them apart. Those pixels are excluded from the biome
        /// comparison and checked only for membership in that set.
        /// </summary>
        private const byte White = 255;
        private const byte UnknownColour = 254;

        private sealed class Worst
        {
            public double Diff;
            public int I, J;
            public float Expected, Got;
            public Biome B;
            public double BoundaryUlps;
        }

        private sealed class Stats
        {
            public long BiomeCompared;
            public long BiomeMismatches;
            // [expected 0..8, got 0..9] - got index 9 is "not one of the nine biomes"
            public readonly long[,] Confusion = new long[9, 10];

            public long WhiteCount;
            public long WhiteBad;
            public readonly long[] WhiteGot = new long[10];

            public long HeightCompared;
            public long HeightExact;
            public long HeightExactNetRounding;
            public double MaxAbsDiff;
            public readonly List<Worst> Worsts = new List<Worst>();
            public readonly List<Worst> Hard = new List<Worst>();

            // How close our float landed to the half-rounding boundary, measured in FLOAT ulps.
            // KnifeEdge[k] counts pixels whose value sits within 2^k float ulps of a boundary;
            // DiffKnife[k] counts the differing ones. If every differing pixel is a knife edge, the port
            // and the game agree to within a float ulp and the residual is last-bit noise, not a formula
            // error - which is a very different diagnosis from a systematic offset.
            public readonly long[] KnifeEdge = new long[4];
            public readonly long[] DiffKnife = new long[4];
            public readonly long[] DiffByBiome = new long[10];

            private const int KeepWorst = 8;

            /// <summary>Set before Report so the hard cases can be re-queried for river influence.</summary>
            public WorldGeneratorPort? Probe;

            /// <summary>
            /// The game stored heights with UnityEngine.Mathf.FloatToHalf, a native extern
            /// (decomp/UnityEngine.Mathf.cs 47-49, reached from Utils.FloatsToCompressedHalfBuffer,
            /// decomp/Utils.cs 1420-1428). .NET's (Half) cast rounds ties to EVEN; measuring against the
            /// oracle shows Unity breaks them AWAY FROM ZERO instead, which is what a
            /// "mantissa + 0x1000, then shift" implementation does. Only exact midpoints are affected,
            /// so this routine differs from (Half)v on nothing else.
            /// </summary>
            public static float ToUnityHalf(float v)
            {
                Half m = (Half)v;
                float f = (float)m;
                if (!float.IsFinite(v) || !Half.IsFinite(m) || f == v) return f;
                Half up = Half.BitIncrement(m);
                Half dn = Half.BitDecrement(m);
                if (Half.IsFinite(up))
                {
                    double b = ((double)f + (double)(float)up) / 2.0;
                    if ((double)v == b) return (Math.Abs((float)up) > Math.Abs(f)) ? (float)up : f;
                }
                if (Half.IsFinite(dn))
                {
                    double b = ((double)f + (double)(float)dn) / 2.0;
                    if ((double)v == b) return (Math.Abs((float)dn) > Math.Abs(f)) ? (float)dn : f;
                }
                return f;
            }

            /// <summary>Distance from h to the nearest IEEE-half rounding boundary, in float ulps.</summary>
            private static double BoundaryUlps(float h)
            {
                if (!float.IsFinite(h) || h == 0f) return double.PositiveInfinity;
                Half m = (Half)h;
                if (!Half.IsFinite(m)) return double.PositiveInfinity;
                double mid = (double)h;
                double b1 = ((double)(float)m + (double)(float)Half.BitIncrement(m)) / 2.0;
                double b2 = ((double)(float)m + (double)(float)Half.BitDecrement(m)) / 2.0;
                double d = Math.Min(Math.Abs(mid - b1), Math.Abs(mid - b2));
                float ah = Math.Abs(h);
                double ulp = (double)MathF.BitIncrement(ah) - (double)ah;
                return ulp > 0.0 ? d / ulp : double.PositiveInfinity;
            }

            public void Account(int k, int i, int j, byte oracleB, Biome got, float oracleH, float gotH)
            {
                int gi = got.ToIndex();
                int gidx = (gi >= 0 && gi <= 8) ? gi : 9;

                if (oracleB == White)
                {
                    WhiteCount++;
                    WhiteGot[gidx]++;
                    // White is Ocean | Mountain | DeepNorth.
                    if (got != Biome.Ocean && got != Biome.Mountain && got != Biome.DeepNorth) WhiteBad++;
                }
                else if (oracleB <= 8)
                {
                    BiomeCompared++;
                    if (gidx != oracleB)
                    {
                        BiomeMismatches++;
                        Confusion[oracleB, gidx]++;
                    }
                }
                // oracleB == 254 (unknown colour) is simply skipped.

                // Height: the game stored GetBiomeHeight through IEEE half, so round ours the same way
                // before comparing. Anything other than an exact match after that rounding is a real
                // arithmetic difference, not a storage artefact.
                float mine = ToUnityHalf(gotH);
                if (((float)(Half)gotH).Equals(oracleH)) HeightExactNetRounding++;
                HeightCompared++;

                double bu = BoundaryUlps(gotH);
                int bucket = bu < 1.0 ? 0 : bu < 2.0 ? 1 : bu < 4.0 ? 2 : 3;
                KnifeEdge[bucket]++;

                if (mine.Equals(oracleH))
                {
                    HeightExact++;
                }
                else
                {
                    DiffKnife[bucket]++;
                    DiffByBiome[gidx]++;
                    double diff = Math.Abs((double)mine - (double)oracleH);
                    if (diff > MaxAbsDiff) MaxAbsDiff = diff;
                    if (Worsts.Count < KeepWorst || diff > Worsts[Worsts.Count - 1].Diff)
                    {
                        Worsts.Add(new Worst { Diff = diff, I = i, J = j, Expected = oracleH, Got = mine, B = got, BoundaryUlps = bu });
                        Worsts.Sort((a, b) => b.Diff.CompareTo(a.Diff));
                        if (Worsts.Count > KeepWorst) Worsts.RemoveAt(Worsts.Count - 1);
                    }
                    // Every difference at a knife edge (< 1 float ulp from the boundary) is last-bit
                    // noise. The ones further out are the only genuinely interesting cases, so keep
                    // them all.
                    if (bucket > 0 && Hard.Count < 64)
                    {
                        Hard.Add(new Worst { Diff = diff, I = i, J = j, Expected = oracleH, Got = mine, B = got, BoundaryUlps = bu });
                    }
                }
            }

            public void Merge(Stats o)
            {
                BiomeCompared += o.BiomeCompared;
                BiomeMismatches += o.BiomeMismatches;
                for (int a = 0; a < 9; a++)
                    for (int b = 0; b < 10; b++)
                        Confusion[a, b] += o.Confusion[a, b];
                WhiteCount += o.WhiteCount;
                WhiteBad += o.WhiteBad;
                for (int a = 0; a < 10; a++) WhiteGot[a] += o.WhiteGot[a];
                HeightCompared += o.HeightCompared;
                HeightExact += o.HeightExact;
                HeightExactNetRounding += o.HeightExactNetRounding;
                for (int a = 0; a < 4; a++) { KnifeEdge[a] += o.KnifeEdge[a]; DiffKnife[a] += o.DiffKnife[a]; }
                for (int a = 0; a < 10; a++) DiffByBiome[a] += o.DiffByBiome[a];
                if (o.MaxAbsDiff > MaxAbsDiff) MaxAbsDiff = o.MaxAbsDiff;
                Hard.AddRange(o.Hard);
                Worsts.AddRange(o.Worsts);
                Worsts.Sort((a, b) => b.Diff.CompareTo(a.Diff));
                while (Worsts.Count > KeepWorst) Worsts.RemoveAt(Worsts.Count - 1);
            }

            private static string Name(int idx) => idx switch
            {
                0 => "Meadows",
                1 => "Swamp",
                2 => "Mountain",
                3 => "BlackForest",
                4 => "Plains",
                5 => "AshLands",
                6 => "DeepNorth",
                7 => "Ocean",
                8 => "Mistlands",
                _ => "other",
            };

            private static string Pos(int i, int j)
            {
                float wy = (float)(i - TextureSize / 2) * PixelSize + PixelSize / 2f;
                float wx = (float)(j - TextureSize / 2) * PixelSize + PixelSize / 2f;
                return "(" + wx + ", " + wy + ")";
            }

            public void Report(int n, double sweepSeconds, int workers)
            {
                Console.WriteLine("Sweep: " + n.ToString("N0") + " pixel centres in "
                                  + sweepSeconds.ToString("F2") + " s on " + workers + " workers ("
                                  + (n / sweepSeconds / 1e6).ToString("F2") + " M px/s)");
                Console.WriteLine();

                Console.WriteLine("BIOME (excluding white pixels)");
                Console.WriteLine("  compared   " + BiomeCompared.ToString("N0"));
                Console.WriteLine("  mismatches " + BiomeMismatches.ToString("N0")
                                  + "   rate " + (BiomeCompared == 0 ? 0 : (double)BiomeMismatches / BiomeCompared).ToString("G6"));
                if (BiomeMismatches > 0)
                {
                    Console.WriteLine("  breakdown (expected -> got : count)");
                    for (int a = 0; a < 9; a++)
                        for (int b = 0; b < 10; b++)
                            if (Confusion[a, b] > 0)
                                Console.WriteLine("    " + Name(a).PadRight(12) + " -> " + Name(b).PadRight(12)
                                                  + " : " + Confusion[a, b].ToString("N0"));
                }
                Console.WriteLine();

                Console.WriteLine("WHITE pixels (oracle 255 = Ocean | Mountain | DeepNorth)");
                Console.WriteLine("  count      " + WhiteCount.ToString("N0"));
            Console.WriteLine("  our Ocean+Mountain+DeepNorth total " + (WhiteGot[7] + WhiteGot[2] + WhiteGot[6]).ToString("N0")
                                  + " (must equal the white count exactly - no non-white pixel may be one of those three,"
                                  + " which the zero biome mismatch count above already guarantees)");
                Console.WriteLine("  outside the set " + WhiteBad.ToString("N0")
                                  + "   rate " + (WhiteCount == 0 ? 0 : (double)WhiteBad / WhiteCount).ToString("G6"));
                Console.WriteLine("  our biomes there:");
                for (int a = 0; a < 10; a++)
                    if (WhiteGot[a] > 0)
                        Console.WriteLine("    " + Name(a).PadRight(12) + " : " + WhiteGot[a].ToString("N0"));
                Console.WriteLine();

                Console.WriteLine("HEIGHT (ours rounded to IEEE half the way Unity does, compared bit-for-bit)");
                Console.WriteLine("  compared   " + HeightCompared.ToString("N0"));
                Console.WriteLine("  exact      " + HeightExact.ToString("N0")
                                  + "   rate " + ((double)HeightExact / HeightCompared).ToString("G9"));
                Console.WriteLine("  differing  " + (HeightCompared - HeightExact).ToString("N0"));
                Console.WriteLine("  for comparison, with .NET's round-ties-to-EVEN (Half) cast: exact "
                                  + HeightExactNetRounding.ToString("N0") + ", differing "
                                  + (HeightCompared - HeightExactNetRounding).ToString("N0"));
                Console.WriteLine("  max |diff| " + MaxAbsDiff.ToString("G9") + " m");
                string[] band = { "< 1 ulp", "1-2 ulps", "2-4 ulps", ">= 4 ulps" };
                Console.WriteLine("  distance of OUR float from the half-rounding boundary (float ulps):");
                for (int a = 0; a < 4; a++)
                    Console.WriteLine("    " + band[a].PadRight(10) + " : " + KnifeEdge[a].ToString("N0").PadLeft(12)
                                      + " pixels, of which " + DiffKnife[a].ToString("N0") + " differ");
                Console.WriteLine("  differing pixels by biome:");
                for (int a = 0; a < 10; a++)
                    if (DiffByBiome[a] > 0)
                        Console.WriteLine("    " + Name(a).PadRight(12) + " : " + DiffByBiome[a].ToString("N0"));
                if (Worsts.Count > 0)
                {
                    Console.WriteLine("  worst offenders:");
                    foreach (Worst w in Worsts)
                        Console.WriteLine("    " + Pos(w.I, w.J).PadRight(22) + " px(" + w.I + "," + w.J + ")"
                                          + "  biome " + w.B
                                          + "  oracle " + w.Expected + "  ours " + w.Got
                                          + "  diff " + w.Diff.ToString("G6")
                                          + "  boundary " + w.BoundaryUlps.ToString("G4") + " ulps");
                }
                if (Hard.Count > 0)
                {
                    Hard.Sort((a, b) => b.BoundaryUlps.CompareTo(a.BoundaryUlps));
                    Console.WriteLine("  differences NOT at a knife edge (>= 1 float ulp from the boundary):");
                    foreach (Worst w in Hard)
                    {
                        float rw = 0f, rwid = 0f;
                        if (Probe != null)
                        {
                            float wy = (float)(w.I - TextureSize / 2) * PixelSize + PixelSize / 2f;
                            float wx = (float)(w.J - TextureSize / 2) * PixelSize + PixelSize / 2f;
                            Probe.GetRiverWeightPublic(wx, wy, out rw, out rwid);
                        }
                        Console.WriteLine("    " + Pos(w.I, w.J).PadRight(22) + " px(" + w.I + "," + w.J + ")"
                                          + "  biome " + w.B
                                          + "  oracle " + w.Expected + "  ours " + w.Got
                                          + "  boundary " + w.BoundaryUlps.ToString("G4") + " ulps"
                                          + "  riverWeight " + rw.ToString("G6") + " width " + rwid.ToString("G6"));
                    }
                }
                Console.WriteLine();
                Console.WriteLine(BiomeMismatches == 0 ? "BIOME: PASS (zero mismatches)" : "BIOME: FAIL");
            }
        }
    }
}
