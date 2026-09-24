using System;
using System.Diagnostics;
using System.Threading.Tasks;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Render
{
    /// <summary>
    /// The sampling grid a field or a map is measured on.
    ///
    /// <para><b>G12, the canonical grid.</b> With <c>Size = 2048</c>, <c>Spacing = 12</c> and the
    /// centre at the origin, <see cref="WorldX"/>/<see cref="WorldZ"/> reproduce
    /// <c>Minimap.GenerateWorldMap</c>'s <c>(j - m_textureSize/2) * m_pixelSize + m_pixelSize/2</c>
    /// and <c>AltBiomeWorldData.MapSpaceToWorldSpace</c> exactly - the same points the game itself
    /// samples (07-features.md section 0.3, measured in 05-validation.md section 1.5). Anything
    /// measured on a different grid is a different measurement, not an approximation of this one, so
    /// every number SeedLab prints carries the grid it came from.</para>
    ///
    /// <para>Row 0 is the SOUTH row and column 0 the WEST column, as in the cache; the renderer flips
    /// rows so that north is up in the image.</para>
    /// </summary>
    public sealed class FieldGrid
    {
        public FieldGrid(int size, float spacing, float centerX = 0f, float centerZ = 0f)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size), size, "Grid must have at least one sample.");
            if (!(spacing > 0f)) throw new ArgumentOutOfRangeException(nameof(spacing), spacing, "Spacing must be positive.");
            Size = size;
            Spacing = spacing;
            CenterX = centerX;
            CenterZ = centerZ;
        }

        /// <summary>The canonical 2048 x 2048 at 12 m grid the game itself uses.</summary>
        public static FieldGrid G12 => new FieldGrid(2048, 12f);

        public int Size { get; }
        public float Spacing { get; }
        public float CenterX { get; }
        public float CenterZ { get; }

        public int Count => Size * Size;

        /// <summary>Half the side of the square the grid covers, in metres.</summary>
        public double HalfSpan => Size * (double)Spacing / 2.0;

        /// <summary>Area of one cell, in m^2.</summary>
        public double CellArea => (double)Spacing * Spacing;

        public float WorldX(int col) => (float)((double)CenterX + ((double)col - Size / 2.0) * Spacing + Spacing / 2.0);
        public float WorldZ(int row) => (float)((double)CenterZ + ((double)row - Size / 2.0) * Spacing + Spacing / 2.0);

        /// <summary>
        /// The exact G12 formula, float for float, used when the grid IS G12 so that the sample points
        /// are bit-identical to the game's: <c>(float)(j - 1024) * 12f + 6f</c>.
        /// </summary>
        public bool IsGameGrid => Size == 2048 && Spacing == 12f && CenterX == 0f && CenterZ == 0f;

        public override string ToString() =>
            Size + "x" + Size + " @ " + Spacing.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " m, centre (" + CenterX + ", " + CenterZ + ")";
    }

    /// <summary>
    /// A sampled biome + height field over a <see cref="FieldGrid"/>. Everything downstream - the map,
    /// the world summary, the island analysis - reads this, so there is exactly one place where the
    /// generator is called and exactly one definition of what a cell means.
    /// </summary>
    public sealed class WorldField
    {
        /// <summary>
        /// <c>WorldGenerator.GetBiomeHeight</c>'s first statement is
        /// <c>if (DUtils.Length(wx, wy) &gt; 10500f) return -2f * GetHeightMultiplier();</c>, so every
        /// sample outside the water edge carries this constant and no terrain was evaluated for it.
        /// </summary>
        public const float OutsideHeight = -400f;

        public const float WaterEdgeRadius = 10500f;

        private WorldField(int seed, int worldGenVersion, FieldGrid grid,
                           byte[] biome, float[] height, byte[]? lava, bool[] outside,
                           double sampleSeconds, int workers)
        {
            Seed = seed;
            WorldGenVersion = worldGenVersion;
            Grid = grid;
            BiomeIndices = biome;
            Height = height;
            LavaAlpha = lava;
            Outside = outside;
            SampleSeconds = sampleSeconds;
            Workers = workers;
        }

        public int Seed { get; }
        public int WorldGenVersion { get; }
        public FieldGrid Grid { get; }

        /// <summary>Per cell: <c>Heightmap.BiomeIndex</c> (None = 0, Meadows = 1 ... Mistlands = 9).</summary>
        public byte[] BiomeIndices { get; }

        /// <summary>Per cell: <c>GetBiomeHeight(biome, x, z)</c> in metres, -400 outside the water edge.</summary>
        public float[] Height { get; }

        /// <summary>Per Ashlands cell: <c>GetAshlandsHeight(..., cheap: true)</c>'s mask alpha * 255; null when not sampled.</summary>
        public byte[]? LavaAlpha { get; }

        /// <summary>Per cell: <c>DUtils.Length(x, z) &gt; 10500f</c>, i.e. the -400 early return fired.</summary>
        public bool[] Outside { get; }

        public double SampleSeconds { get; }
        public int Workers { get; }

        /// <summary>Construction + full river/lake pregeneration time for the generator handle.</summary>
        public double ConstructSeconds { get; private set; }

        public Biome BiomeAt(int index) => ((BiomeIndex)BiomeIndices[index]).ToBiome();

        public bool IsLand(int index) => !Outside[index] && Height[index] >= MapPalette.WaterLevel;

        /// <summary>
        /// Wraps arrays that came from somewhere other than the generator - the game's own minimap
        /// cache, for instance - so the same analysis and the same renderer can run over them. That is
        /// what makes "does our island code agree with the game's stored heights?" a question that can
        /// be asked directly, instead of two numbers being compared across two implementations.
        /// </summary>
        public static WorldField FromSamples(int seed, int worldGenVersion, FieldGrid grid,
                                             byte[] biomeIndices, float[] height, bool[]? outside = null,
                                             byte[]? lavaAlpha = null)
        {
            if (biomeIndices.Length != grid.Count) throw new ArgumentException("biome array length does not match the grid.", nameof(biomeIndices));
            if (height.Length != grid.Count) throw new ArgumentException("height array length does not match the grid.", nameof(height));

            bool[] outs = outside ?? new bool[grid.Count];
            if (outside == null)
            {
                for (int row = 0; row < grid.Size; row++)
                {
                    float wz = grid.WorldZ(row);
                    for (int col = 0; col < grid.Size; col++)
                    {
                        outs[row * grid.Size + col] = DUtils.Length(grid.WorldX(col), wz) > WaterEdgeRadius;
                    }
                }
            }

            return new WorldField(seed, worldGenVersion, grid, biomeIndices, height, lavaAlpha, outs, 0.0, 1);
        }

        /// <summary>Samples the whole grid. <paramref name="threads"/> &lt;= 0 means every logical core.</summary>
        public static WorldField Sample(int seed, FieldGrid grid, bool sampleLava, int threads = 0,
                                        int worldGenVersion = 2)
        {
            Stopwatch sw = Stopwatch.StartNew();
            WorldGeneratorPort gen = new WorldGeneratorPort(seed, worldGenVersion, menu: false);
            sw.Stop();
            double construct = sw.Elapsed.TotalSeconds;

            int n = grid.Count;
            byte[] biome = new byte[n];
            float[] height = new float[n];
            byte[]? lava = sampleLava ? new byte[n] : null;
            bool[] outside = new bool[n];

            int workers = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount);
            const int rowsPerChunk = 8;
            int chunks = (grid.Size + rowsPerChunk - 1) / rowsPerChunk;

            sw.Restart();
            Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = workers }, c =>
            {
                // Fork(): a handle owns a single-entry river cache, so every worker needs its own.
                // m_riverPoints is immutable once pregeneration has finished, so the forks share it.
                WorldGeneratorPort w = workers == 1 ? gen : gen.Fork();
                int r0 = c * rowsPerChunk;
                int r1 = Math.Min(grid.Size, r0 + rowsPerChunk);
                for (int row = r0; row < r1; row++)
                {
                    float wz = grid.WorldZ(row);
                    int baseIdx = row * grid.Size;
                    for (int col = 0; col < grid.Size; col++)
                    {
                        float wx = grid.WorldX(col);
                        int k = baseIdx + col;

                        Biome b = w.GetBiome(wx, wz);
                        float h = w.GetBiomeHeight(b, wx, wz, out _);
                        biome[k] = (byte)b.ToGameIndex();
                        height[k] = h;
                        outside[k] = DUtils.Length(wx, wz) > WaterEdgeRadius;

                        if (lava != null && b == Biome.AshLands && !outside[k])
                        {
                            // ZoneSystem.IsLavaPreHeightmap reads the same cheap Ashlands mask alpha.
                            w.GetAshlandsHeight(wx, wz, out ColorRGBA mask, cheap: true);
                            float a = Math.Clamp(mask.a, 0f, 1f);
                            lava[k] = (byte)Math.Round(a * 255f);
                        }
                    }
                }
            });
            sw.Stop();

            return new WorldField(seed, worldGenVersion, grid, biome, height, lava, outside,
                                  sw.Elapsed.TotalSeconds, workers)
            {
                ConstructSeconds = construct,
            };
        }
    }
}
