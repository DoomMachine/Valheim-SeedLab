using System;
using System.Threading.Tasks;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Locations
{
    /// <summary>
    /// The 2048 x 2048 biome-and-height point grid that <c>AltBiomeWorldData.GenerateBiomePoints</c>
    /// builds on every server world load (decomp/AltBiomeWorldData.cs 96-125).
    ///
    /// <para><b>Geometry.</b> 12 m between points, origin at grid index 1024, half a pixel of offset:
    /// <c>world = (index - 1024) * 12 + 6</c>. That is exactly the minimap cache's geometry
    /// (<c>Minimap.GenerateWorldMap</c>), which is why <c>groundtruth\decoded\*.biome.u8</c> is a valid
    /// oracle for this array - see <c>tools\SeedLab.LocationLab</c> command <c>grid</c>.</para>
    ///
    /// <para><b>Index convention.</b> The game writes <c>PointBiomes[x, y]</c> where x is the world-X
    /// axis and y the world-Z axis. This port stores the same values in a flat array with
    /// <c>k = y * 2048 + x</c>, which is also the minimap cache's own linear index (row = z, col = x),
    /// so a comparison against the oracle is index-for-index with no transposition.</para>
    ///
    /// <para><b>Two different cutoffs, both reproduced.</b> <c>GenerateBiomePoints</c> rejects a point
    /// on <c>Vector2(wx, wz).sqrMagnitude &gt; 110250000f</c> and stores Ocean / -1000. Inside
    /// <c>WorldGenerator.GetBiomeHeight</c> there is a second, differently computed test,
    /// <c>DUtils.Length(wx, wz) &gt; 10500f</c>, which returns -400. The two disagree on a thin ring, and
    /// that ring stores -400: it is not dead code, it feeds <c>BiomeSector.HeightMin</c> and hence
    /// <c>HeightAvg</c> and <c>CanAddModifier</c>. <see cref="Build"/> counts the disagreeing points and
    /// reports them in <see cref="CutoffRingPoints"/>, settling one of spec 02's open items.</para>
    /// </summary>
    public sealed class BiomeGrid
    {
        /// <summary><c>AltBiomeWorldData.c_textureSize</c>.</summary>
        public const int Size = 2048;

        /// <summary><c>AltBiomeWorldData.c_pixelSize</c>.</summary>
        public const float PixelSize = 12f;

        /// <summary><c>AltBiomeWorldData.c_halfWidth</c>.</summary>
        public const int HalfWidth = 1024;

        /// <summary><c>AltBiomeWorldData.c_halfPixel</c>.</summary>
        public const float HalfPixel = 6f;

        /// <summary><c>WorldGenerator.waterEdgeSqr</c> = 10500^2, compared against Vector2.sqrMagnitude.</summary>
        public const float WaterEdgeSqr = 110250000f;

        /// <summary>The height stored for a point outside <see cref="WaterEdgeSqr"/>.</summary>
        public const float OutsideHeight = -1000f;

        public const int PointCount = Size * Size;

        private BiomeGrid(byte[] biomes, float[] heights, int seed, int worldGenVersion,
                          long cutoffRingPoints, double buildMilliseconds, int workers)
        {
            PointBiomes = biomes;
            PointHeights = heights;
            Seed = seed;
            WorldGenVersion = worldGenVersion;
            CutoffRingPoints = cutoffRingPoints;
            BuildMilliseconds = buildMilliseconds;
            Workers = workers;
        }

        /// <summary>
        /// <c>Heightmap.BiomeIndex</c> per point (None 0, Meadows 1 ... Mistlands 9) - the game's own
        /// numbering, which <c>AltBiomeWorldData.PointBiomes</c> also stores. NOT SeedLab's dense index.
        /// </summary>
        public byte[] PointBiomes { get; }

        /// <summary><c>AltBiomeWorldData.PointHeights</c> - raw procedural metres, sea level 30.</summary>
        public float[] PointHeights { get; }

        public int Seed { get; }
        public int WorldGenVersion { get; }

        /// <summary>
        /// Points that pass <c>sqrMagnitude &lt;= 110250000f</c> but fail
        /// <c>DUtils.Length &gt; 10500f</c> inside GetBiomeHeight, i.e. the ones that store -400 instead
        /// of a real biome height. Measured, not assumed.
        /// </summary>
        public long CutoffRingPoints { get; }

        public double BuildMilliseconds { get; }
        public int Workers { get; }

        /// <summary><c>k = y * 2048 + x</c>, x = world-X index, y = world-Z index.</summary>
        public static int Index(int x, int y) => y * Size + x;

        public static int XOf(int index) => index & (Size - 1);
        public static int YOf(int index) => index >> 11;

        /// <summary>
        /// <c>AltBiomeWorldData.MapSpaceToWorldSpace(float)</c>: <c>(v - 1024f) * 12f + 6f</c>.
        ///
        /// <para><b>Evaluated in double with ONE narrowing, at the return.</b> The IL loads the float32
        /// argument as the native <c>F</c> type and never narrows it again until the value is returned
        /// as float32, so the subtract, the multiply and the add all happen at R8. For an integer
        /// <c>v</c> that makes no difference - every intermediate is exact - which is why the grid
        /// lattice and <c>ZoneOfGridPoint</c> could not tell the two spellings apart. It IS observable
        /// for <c>BiomeSector.Center</c>, the one caller that passes a non-integer: measured on seed
        /// 75539276 against the game's own dumped sectors, the float-per-step spelling missed 57 of 938
        /// centres by 1 ULP and this one misses none (goldens/altbiomes-assignment-0480A34C.json,
        /// 2026-09-23).</para>
        /// </summary>
        public static float MapSpaceToWorldSpace(float v) => (float)(((double)v - 1024.0) * 12.0 + 6.0);

        /// <summary>
        /// <c>AltBiomeWorldData.WorldSpaceToMapSpace(float)</c>: <c>(int)((x - 6f) / 12f + 1024f)</c>.
        /// A C-style truncating cast, not a floor - it only goes negative below -12282 m, and callers
        /// clamp afterwards anyway (<c>WorldGenerator.GetBiomeSector</c>).
        /// </summary>
        public static int WorldSpaceToMapSpace(float x) => (int)((x - 6f) / 12f + 1024f);

        public BiomeIndex BiomeAt(int x, int y) => (BiomeIndex)PointBiomes[Index(x, y)];
        public float HeightAt(int x, int y) => PointHeights[Index(x, y)];

        /// <summary>
        /// <c>AltBiomeWorldData.GenerateBiomePoints</c>, row by row.
        ///
        /// <para>The game runs this on one thread; the loop body is pure, so it parallelises across rows
        /// with one <see cref="WorldGeneratorPort.Fork"/> per worker. Forking is mandatory, not an
        /// optimisation: a WorldGeneratorPort carries a mutable single-entry river cache and is
        /// explicitly documented as unsafe for concurrent use. All workers fork the same parent, so the
        /// whole grid is computed in one mode ("always forked"), which is the rule that port's
        /// documentation asks callers to follow.</para>
        /// </summary>
        /// <param name="gen">The world. Not used directly by the workers - each forks it.</param>
        /// <param name="maxDegreeOfParallelism">-1 for Environment.ProcessorCount; 1 for serial.</param>
        public static BiomeGrid Build(WorldGeneratorPort gen, int maxDegreeOfParallelism = -1)
            => Build(gen, maxDegreeOfParallelism, null, null);

        /// <summary>
        /// The same build into caller-owned arrays, so a loop over many seeds does not hand the
        /// collector 20 MB of large-object arrays per world.
        ///
        /// <para><b>Reuse is safe because every point is written.</b> <see cref="BuildRows"/> assigns
        /// both <c>biomes[k]</c> and <c>heights[k]</c> on every k of every row, on both sides of the
        /// water-edge branch, so nothing of the previous seed can survive into this one. The arrays
        /// must be exactly <see cref="PointCount"/> long and must not be read by anything else while
        /// the build runs - and, because <see cref="BiomeField"/> reads them only during its own
        /// build, a caller may reuse them as soon as that has returned.</para>
        /// </summary>
        public static BiomeGrid Build(WorldGeneratorPort gen, int maxDegreeOfParallelism,
                                      byte[]? biomeBuffer, float[]? heightBuffer)
        {
            if (gen == null) throw new ArgumentNullException(nameof(gen));
            if (biomeBuffer != null && biomeBuffer.Length != PointCount)
                throw new ArgumentException("the biome buffer must be exactly " + PointCount + " bytes.", nameof(biomeBuffer));
            if (heightBuffer != null && heightBuffer.Length != PointCount)
                throw new ArgumentException("the height buffer must be exactly " + PointCount + " floats.", nameof(heightBuffer));

            byte[] biomes = biomeBuffer ?? new byte[PointCount];
            float[] heights = heightBuffer ?? new float[PointCount];

            // The 2048 world coordinates are shared by both axes and are exact in float, so they are
            // computed once. (float)j is exact for j < 2^24, so this is the same value the game's
            // MapSpaceToWorldSpace(int) overload produces.
            float[] coord = new float[Size];
            for (int i = 0; i < Size; i++) coord[i] = MapSpaceToWorldSpace(i);

            int workers = maxDegreeOfParallelism > 0
                ? maxDegreeOfParallelism
                : Environment.ProcessorCount;

            long ringTotal = 0;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

            if (workers == 1)
            {
                ringTotal = BuildRows(gen.Fork(), coord, biomes, heights, 0, Size);
            }
            else
            {
                // One contiguous band of rows per worker: the fork is the expensive part, and bands keep
                // the river cache warm for neighbouring rows.
                int bands = workers;
                long[] ring = new long[bands];
                int rowsPerBand = (Size + bands - 1) / bands;
                Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = workers }, b =>
                {
                    int y0 = b * rowsPerBand;
                    int y1 = Math.Min(Size, y0 + rowsPerBand);
                    if (y0 >= y1) return;
                    ring[b] = BuildRows(gen.Fork(), coord, biomes, heights, y0, y1);
                });
                for (int b = 0; b < bands; b++) ringTotal += ring[b];
            }

            sw.Stop();
            return new BiomeGrid(biomes, heights, gen.GetSeed(), gen.WorldGenVersion,
                                 ringTotal, sw.Elapsed.TotalMilliseconds, workers);
        }

        private static long BuildRows(WorldGeneratorPort w, float[] coord, byte[] biomes, float[] heights,
                                      int y0, int y1)
        {
            long ring = 0;
            for (int y = y0; y < y1; y++)
            {
                float wz = coord[y];
                int rowBase = y * Size;
                for (int x = 0; x < Size; x++)
                {
                    float wx = coord[x];
                    int k = rowBase + x;
                    // Vector2.sqrMagnitude: one narrowing, at the store (UnityMath.Vec2).
                    float sqr = new Vec2(wx, wz).SqrMagnitudeSelf;
                    if (sqr > WaterEdgeSqr)
                    {
                        biomes[k] = (byte)BiomeIndex.Ocean;
                        heights[k] = OutsideHeight;
                    }
                    else
                    {
                        Biome b = w.GetBiome(wx, wz);
                        biomes[k] = (byte)b.ToBiomeIndex();
                        float h = w.GetBiomeHeight(b, wx, wz, out _);
                        heights[k] = h;
                        // The second cutoff inside GetBiomeHeight returns exactly -2 * 200 = -400.
                        if (DUtils.Length(wx, wz) > WorldGeneratorPort.WaterEdge) ring++;
                    }
                }
            }
            return ring;
        }
    }
}
