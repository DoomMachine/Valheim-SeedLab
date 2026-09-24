using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLabAcceptanceTests
{
    /// <summary>
    /// Regenerates every one of the 4 194 304 minimap pixels of a ground-truth world and compares the
    /// biome and the height against what the game itself stored.
    ///
    /// Minimap.GenerateWorldMap (decomp/Minimap.cs, quoted in 05-validation.md section 1.4):
    ///     int num = m_textureSize / 2;  float num2 = m_pixelSize / 2f;
    ///     wy = (float)(i - num) * m_pixelSize + num2;      // row 0 = SOUTH
    ///     wx = (float)(j - num) * m_pixelSize + num2;      // col 0 = WEST
    ///     biome  = worldGenerator.GetBiome(wx, wy);                        // oceanLevel .02, waterAlwaysOcean false
    ///     height = worldGenerator.GetBiomeHeight(biome, wx, wy, out _);
    /// with m_textureSize = 2048 and m_pixelSize = 12 (both measured; section 1.5).
    /// </summary>
    public static class WorldSweep
    {
        public const int TextureSize = 2048;
        public const float PixelSize = 12.0f;

        private const byte White = 255;           // Ocean | Mountain | DeepNorth all render pure white
        private const byte UnknownColour = 254;

        public sealed class Worst
        {
            public double Metres;
            public int Ulps;
            public int I, J;
            public ushort OracleBits, OurBits;
            public float OurFloat;
            public Biome B;
            public float RiverWeight, RiverWidth;
        }

        public sealed class Stats
        {
            // --- biome ---
            public long BiomeCompared, BiomeMismatches, UnknownColourPixels;
            public readonly long[,] Confusion = new long[9, 10];
            public long WhiteCount, WhiteOutsideSet;
            public readonly long[] WhiteGot = new long[10];

            // --- height ---
            public long HeightCompared, ExactUnity, ExactNet;
            public long Ties, TiesOracleAwayFromZero, TiesOracleToEven, TiesOracleNeither;
            public long SignOfZeroDifferences;
            public const int UlpSpan = 10;                       // histogram covers -10..+10 half-ULPs
            public readonly long[] UlpHist = new long[2 * UlpSpan + 1];
            public long UlpOutOfRange;
            public readonly long[] MetreHist = new long[6];      // <=1e-3, <=1e-2, <=0.0625, <=0.25, <=1, >1
            public double MaxMetres;
            public readonly long[] DiffByBiome = new long[10];
            public readonly List<Worst> Worsts = new List<Worst>();
            private const int KeepWorst = 12;

            // --- T3a: the world-edge constant ---
            public long Minus400Oracle, Minus400Predicate, Minus400Disagree;

            // --- rivers ---
            public long InWorld;                 // DUtils.Length(wx,wy) <= 10500, i.e. not the -400 early-out
            public long RiverTouched, RiverTouchedExact, RiverTouchedDiff;
            public long RiverStrong, RiverStrongExact;      // weight > 0.5
            public long RiverVeryStrong, RiverVeryStrongLand, RiverVeryStrongLandInFloorBand;   // weight > 0.9
            public long DryLand, DryLandInFloorBand;

            public void AccountWorst(Worst w)
            {
                if (Worsts.Count < KeepWorst || w.Metres > Worsts[Worsts.Count - 1].Metres)
                {
                    Worsts.Add(w);
                    Worsts.Sort((a, b) => b.Metres.CompareTo(a.Metres));
                    if (Worsts.Count > KeepWorst) Worsts.RemoveAt(Worsts.Count - 1);
                }
            }

            public void Merge(Stats o)
            {
                BiomeCompared += o.BiomeCompared; BiomeMismatches += o.BiomeMismatches;
                UnknownColourPixels += o.UnknownColourPixels;
                for (int a = 0; a < 9; a++) for (int b = 0; b < 10; b++) Confusion[a, b] += o.Confusion[a, b];
                WhiteCount += o.WhiteCount; WhiteOutsideSet += o.WhiteOutsideSet;
                for (int a = 0; a < 10; a++) { WhiteGot[a] += o.WhiteGot[a]; DiffByBiome[a] += o.DiffByBiome[a]; }

                HeightCompared += o.HeightCompared; ExactUnity += o.ExactUnity; ExactNet += o.ExactNet;
                Ties += o.Ties; TiesOracleAwayFromZero += o.TiesOracleAwayFromZero;
                TiesOracleToEven += o.TiesOracleToEven; TiesOracleNeither += o.TiesOracleNeither;
                SignOfZeroDifferences += o.SignOfZeroDifferences;
                for (int a = 0; a < UlpHist.Length; a++) UlpHist[a] += o.UlpHist[a];
                UlpOutOfRange += o.UlpOutOfRange;
                for (int a = 0; a < MetreHist.Length; a++) MetreHist[a] += o.MetreHist[a];
                if (o.MaxMetres > MaxMetres) MaxMetres = o.MaxMetres;
                foreach (Worst w in o.Worsts) AccountWorst(w);

                Minus400Oracle += o.Minus400Oracle; Minus400Predicate += o.Minus400Predicate;
                Minus400Disagree += o.Minus400Disagree;

                InWorld += o.InWorld;
                RiverTouched += o.RiverTouched; RiverTouchedExact += o.RiverTouchedExact;
                RiverTouchedDiff += o.RiverTouchedDiff;
                RiverStrong += o.RiverStrong; RiverStrongExact += o.RiverStrongExact;
                RiverVeryStrong += o.RiverVeryStrong; RiverVeryStrongLand += o.RiverVeryStrongLand;
                RiverVeryStrongLandInFloorBand += o.RiverVeryStrongLandInFloorBand;
                DryLand += o.DryLand; DryLandInFloorBand += o.DryLandInFloorBand;
            }
        }

        public sealed class SweepResult
        {
            public Stats S = new Stats();
            public double Seconds;
            public int Workers;
            public double ConstructSeconds;
            public int Rivers, Streams, RiverGridCells, RiverPoints, Lakes;
            public float[] Offsets = new float[5];
            public int RiverSeed, StreamSeed;
        }

        public static float WorldX(int j) => (float)(j - TextureSize / 2) * PixelSize + PixelSize / 2f;
        public static float WorldY(int i) => (float)(i - TextureSize / 2) * PixelSize + PixelSize / 2f;

        /// <param name="oracleBiome">dense Heightmap.BiomeIndex per pixel, 255 = white, 254 = unknown colour</param>
        /// <param name="oracleHalf">the raw 16-bit codes out of cacheMinimapHeight</param>
        public static SweepResult Run(WorldFixture world, byte[] oracleBiome, ushort[] oracleHalf, bool serial)
        {
            SweepResult res = new SweepResult();
            Stopwatch sw = Stopwatch.StartNew();
            WorldGeneratorPort gen = new WorldGeneratorPort(world.Seed, 2, menu: false);
            sw.Stop();
            res.ConstructSeconds = sw.Elapsed.TotalSeconds;
            res.Rivers = gen.GetRivers().Count;
            res.Streams = gen.GetStreams().Count;
            res.Lakes = gen.GetLakes()?.Count ?? -1;
            res.RiverGridCells = gen.GetRiverPoints().Count;
            long pts = 0;
            foreach (KeyValuePair<Vec2i, WorldGeneratorPort.RiverPoint[]> kv in gen.GetRiverPoints()) pts += kv.Value.Length;
            res.RiverPoints = (int)pts;
            res.Offsets = new[] { gen.Offset0, gen.Offset1, gen.Offset2, gen.Offset3, gen.Offset4 };
            res.RiverSeed = gen.RiverSeed;
            res.StreamSeed = gen.StreamSeed;

            ushort minus400Bits = BitConverter.HalfToUInt16Bits((Half)(-400f));

            int workers = serial ? 1 : Math.Max(1, Environment.ProcessorCount);
            res.Workers = workers;
            const int rowsPerChunk = 16;
            int chunks = (TextureSize + rowsPerChunk - 1) / rowsPerChunk;
            Stats[] partial = new Stats[chunks];

            sw.Restart();
            Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = workers }, c =>
            {
                // One handle for the heights and a SEPARATE handle for the river probe: a handle owns a
                // single-entry river cache, and asking it for a river weight between two height queries
                // would perturb the very cache the height path reads. Two forks cannot interfere -
                // m_riverPoints is immutable once Pregenerate has finished.
                WorldGeneratorPort w = serial ? gen : gen.Fork();
                WorldGeneratorPort probe = gen.Fork();
                Stats st = new Stats();
                int i0 = c * rowsPerChunk;
                int i1 = Math.Min(TextureSize, i0 + rowsPerChunk);
                for (int i = i0; i < i1; i++)
                {
                    float wy = WorldY(i);
                    int rowBase = i * TextureSize;
                    for (int j = 0; j < TextureSize; j++)
                    {
                        float wx = WorldX(j);
                        int k = rowBase + j;

                        Biome b = w.GetBiome(wx, wy);
                        float h = w.GetBiomeHeight(b, wx, wy, out _);

                        Account(st, i, j, wx, wy, oracleBiome[k], b, oracleHalf[k], h, minus400Bits, probe);
                    }
                }
                partial[c] = st;
            });
            sw.Stop();
            res.Seconds = sw.Elapsed.TotalSeconds;
            for (int c = 0; c < chunks; c++) res.S.Merge(partial[c]);
            return res;
        }

        private static void Account(Stats st, int i, int j, float wx, float wy,
                                    byte oracleB, Biome got, ushort oracleBits, float ourH,
                                    ushort minus400Bits, WorldGeneratorPort probe)
        {
            int gi = got.ToIndex();
            int gidx = (gi >= 0 && gi <= 8) ? gi : 9;

            // ---- biome --------------------------------------------------------------------------
            if (oracleB == White)
            {
                st.WhiteCount++;
                st.WhiteGot[gidx]++;
                // Minimap.GetPixelColor maps Ocean -> Color.white, and the prefab's m_mountainColor and
                // m_deepnorthColor are both (255,255,255) (05-validation.md section 2.1), so a white
                // pixel is decodable only up to that three-way set.
                if (got != Biome.Ocean && got != Biome.Mountain && got != Biome.DeepNorth) st.WhiteOutsideSet++;
            }
            else if (oracleB <= 8)
            {
                st.BiomeCompared++;
                if (gidx != oracleB)
                {
                    st.BiomeMismatches++;
                    st.Confusion[oracleB, gidx]++;
                }
            }
            else
            {
                st.UnknownColourPixels++;          // 254: a colour the decoder did not recognise
            }

            // ---- height -------------------------------------------------------------------------
            ushort netBits = HalfCodec.NetBits(ourH);
            ushort uniBits = HalfCodec.UnityBits(ourH, out bool wasTie);
            st.HeightCompared++;
            if (netBits == oracleBits) st.ExactNet++;
            bool exact = uniBits == oracleBits;
            if (exact) st.ExactUnity++;

            if (wasTie)
            {
                st.Ties++;
                if (oracleBits == uniBits) st.TiesOracleAwayFromZero++;
                else if (oracleBits == netBits) st.TiesOracleToEven++;
                else st.TiesOracleNeither++;
            }

            if (!exact && (oracleBits & 0x7FFF) == 0 && (uniBits & 0x7FFF) == 0) st.SignOfZeroDifferences++;

            // T3a: GetBiomeHeight's first statement is
            //   if (DUtils.Length(wx, wy) > 10500f) return -2f * GetHeightMultiplier();   // = -400
            // (decomp/WorldGenerator.cs 1032-1035), so the -400 pixels are pure geometry and must agree
            // with the predicate exactly - for any seed.
            bool oracleIsEdge = oracleBits == minus400Bits;
            bool predicate = DUtils.Length(wx, wy) > 10500f;
            if (oracleIsEdge) st.Minus400Oracle++;
            if (predicate) st.Minus400Predicate++;
            if (oracleIsEdge != predicate) st.Minus400Disagree++;

            if (!exact)
            {
                st.DiffByBiome[gidx]++;
                double metres = Math.Abs((double)HalfCodec.Decode(uniBits) - (double)HalfCodec.Decode(oracleBits));
                if (metres > st.MaxMetres) st.MaxMetres = metres;
                int mh = metres <= 1e-3 ? 0 : metres <= 1e-2 ? 1 : metres <= 0.0625 ? 2
                       : metres <= 0.25 ? 3 : metres <= 1.0 ? 4 : 5;
                st.MetreHist[mh]++;
                int ulps = HalfCodec.UlpDistance(uniBits, oracleBits);
                if (ulps == int.MinValue || ulps < -Stats.UlpSpan || ulps > Stats.UlpSpan) st.UlpOutOfRange++;
                else st.UlpHist[ulps + Stats.UlpSpan]++;

                probe.GetRiverWeightPublic(wx, wy, out float rwW, out float rwWid);
                st.AccountWorst(new Worst
                {
                    Metres = metres,
                    Ulps = ulps,
                    I = i,
                    J = j,
                    OracleBits = oracleBits,
                    OurBits = uniBits,
                    OurFloat = ourH,
                    B = got,
                    RiverWeight = rwW,
                    RiverWidth = rwWid,
                });
            }

            // ---- river coverage ------------------------------------------------------------------
            // Only inside the world disc: outside it GetBiomeHeight returns the -400 constant before it
            // ever reaches a per-biome height function, so AddRivers is not on that path at all.
            if (!predicate)
            {
                st.InWorld++;
                probe.GetRiverWeightPublic(wx, wy, out float weight, out float width);
                float oracleH = HalfCodec.Decode(oracleBits);
                // AddRivers (decomp/WorldGenerator.cs 937-957) pulls the NORMALISED height down toward
                //   num = DUtils.Lerp(0.14f, 0.12f, LerpStep(20f, 60f, width))
                // which is 24..28 m once GetBiomeHeight multiplies by GetHeightMultiplier() = 200.
                // A pixel whose weight is ~1 therefore sits in that floor band in the GAME'S OWN data
                // if and only if the game put a river there too. That is the direct evidence that this
                // height comparison is testing the river pass and not just the base terrain.
                bool inFloorBand = oracleH >= 23f && oracleH <= 30f;
                if (weight > 0f)
                {
                    st.RiverTouched++;
                    if (exact) st.RiverTouchedExact++; else st.RiverTouchedDiff++;
                    if (weight > 0.5f) { st.RiverStrong++; if (exact) st.RiverStrongExact++; }
                    if (weight > 0.9f)
                    {
                        st.RiverVeryStrong++;
                        if (got != Biome.Ocean)
                        {
                            st.RiverVeryStrongLand++;
                            if (inFloorBand) st.RiverVeryStrongLandInFloorBand++;
                        }
                    }
                }
                else if (got != Biome.Ocean)
                {
                    st.DryLand++;
                    if (inFloorBand) st.DryLandInFloorBand++;
                }
            }
        }

        public static string BiomeName(int idx) => idx switch
        {
            0 => "Meadows", 1 => "Swamp", 2 => "Mountain", 3 => "BlackForest", 4 => "Plains",
            5 => "AshLands", 6 => "DeepNorth", 7 => "Ocean", 8 => "Mistlands", _ => "other",
        };
    }
}
