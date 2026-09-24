using System;
using SeedLab.Render;
using SeedLab.WorldGen;

namespace SeedLab.Search.Metrics
{
    /// <summary>
    /// One worker's reusable view of one world: the biome index and (when a goal needs it) the height
    /// of every grid cell inside the query's region of interest.
    ///
    /// <para><b>Allocated once per worker and reused for every seed.</b> At G12 the three buffers are
    /// 4 MB + 16 MB + 16 MB = 36 MB, so a 16-thread run at G12 holds 576 MB. That is the reason
    /// <c>--threads</c> exists and the reason the search prints its own memory figure before it
    /// starts.</para>
    ///
    /// <para>The grid is <see cref="FieldGrid"/> - the same type the renderer and <c>vseed seed</c>
    /// measure on - so "the grid" has exactly one definition in the whole tool
    /// (07-features.md section 2.1).</para>
    /// </summary>
    public sealed class SeedSampler
    {
        /// <summary>A cell that was not sampled: outside the water edge, or outside the query's region.</summary>
        public const byte Unsampled = 255;

        /// <summary>
        /// <c>GetBiomeHeight</c>'s first statement is
        /// <c>if (DUtils.Length(wx, wy) &gt; 10500f) return -2f * GetHeightMultiplier();</c>, so 10,500 m
        /// is the edge of the world in the only sense that matters here.
        /// </summary>
        public const double WaterEdge = 10500.0;

        /// <summary>The water level the game hard-codes in <c>Minimap.GetMaskColor</c> and <c>AltBiomeWorldData.tryFill</c>.</summary>
        public const float WaterLevel = MapPalette.WaterLevel;   // 30f

        private readonly int _size;

        public SeedSampler(FieldGrid grid)
        {
            Grid = grid;
            _size = grid.Size;
            Biome = new byte[grid.Count];
            Height = new float[grid.Count];
        }

        public FieldGrid Grid { get; }

        /// <summary>Per cell: <c>Heightmap.BiomeIndex</c> (None 0 .. Mistlands 9), or <see cref="Unsampled"/>.</summary>
        public byte[] Biome { get; }

        /// <summary>Per sampled cell: <c>GetBiomeHeight(biome, x, z)</c> in metres. Only valid after <see cref="SampleHeights"/>.</summary>
        public float[] Height { get; }

        /// <summary>The radius the last <see cref="SampleBiomes"/> covered, metres.</summary>
        public double SampledRadius { get; private set; }

        public bool HeightsSampled { get; private set; }

        /// <summary>Cells actually sampled by the last <see cref="SampleBiomes"/>.</summary>
        public long SampledCells { get; private set; }

        /// <summary>
        /// The half-open column range [lo, hi) of row <paramref name="row"/> that lies inside the disc of
        /// radius <paramref name="radius"/> centred on the origin. Returns false when the row misses the
        /// disc entirely.
        ///
        /// <para>This is the whole of the region restriction of 07-features.md section 3.3, and it is
        /// EXACT: a cell outside the disc cannot contribute to any metric whose definition is bounded by
        /// that disc. The saving is the area ratio, <c>(R/10500)^2</c>.</para>
        /// </summary>
        public bool RowSpan(int row, double radius, out int lo, out int hi)
        {
            double wz = Grid.WorldZ(row);
            double half = radius * radius - wz * wz;
            if (half <= 0) { lo = hi = 0; return false; }

            double dx = Math.Sqrt(half);
            // WorldX(col) = (col - Size/2) * s + s/2, so col = (x - s/2)/s + Size/2.
            double s = Grid.Spacing;
            double c0 = (-dx - s / 2.0) / s + _size / 2.0;
            double c1 = (dx - s / 2.0) / s + _size / 2.0;
            lo = Math.Max(0, (int)Math.Ceiling(c0));
            hi = Math.Min(_size, (int)Math.Floor(c1) + 1);
            return hi > lo;
        }

        /// <summary>
        /// Fills <see cref="Biome"/> inside the disc of <paramref name="radius"/> and marks everything
        /// else <see cref="Unsampled"/>. Cells beyond 10,500 m are never sampled whatever the radius:
        /// the generator returns -400 there and the game treats them as outside the world.
        /// </summary>
        public void SampleBiomes(WorldGeneratorPort gen, double radius)
        {
            double r = Math.Min(radius, WaterEdge);
            SampledRadius = r;
            HeightsSampled = false;
            long n = 0;

            Array.Fill(Biome, Unsampled);
            for (int row = 0; row < _size; row++)
            {
                if (!RowSpan(row, r, out int lo, out int hi)) continue;
                float wz = Grid.WorldZ(row);
                int b = row * _size;
                for (int col = lo; col < hi; col++)
                {
                    float wx = Grid.WorldX(col);
                    // The span test is done in double; the generator's own edge test is the float
                    // DUtils.Length, so re-apply it verbatim for the cells near 10,500 m.
                    if (DUtils.Length(wx, wz) > 10500f) continue;
                    Biome[b + col] = (byte)gen.GetBiome(wx, wz).ToGameIndex();
                    n++;
                }
            }

            SampledCells = n;
        }

        /// <summary>
        /// Fills <see cref="Height"/> for every cell <see cref="SampleBiomes"/> sampled, using the biome
        /// already stored - exactly <c>GetBiomeHeight(GetBiome(x,z), x, z)</c>, which is what
        /// <c>Minimap.GenerateWorldMap</c> stores and what the ground-truth height oracle contains.
        /// </summary>
        public void SampleHeights(WorldGeneratorPort gen)
        {
            for (int row = 0; row < _size; row++)
            {
                if (!RowSpan(row, SampledRadius, out int lo, out int hi)) continue;
                float wz = Grid.WorldZ(row);
                int b = row * _size;
                for (int col = lo; col < hi; col++)
                {
                    int k = b + col;
                    byte bi = Biome[k];
                    if (bi == Unsampled) continue;
                    Height[k] = gen.GetBiomeHeight(((BiomeIndex)bi).ToBiome(), Grid.WorldX(col), wz, out _);
                }
            }

            HeightsSampled = true;
        }

        public bool IsLand(int index) => Biome[index] != Unsampled && Height[index] >= WaterLevel;

        /// <summary>
        /// Fills the buffers from samples that came from somewhere other than the generator - the game's
        /// own minimap cache, for instance. That is what makes "does the search's land and island
        /// arithmetic agree with the world the game actually wrote?" a question that can be asked
        /// directly, instead of two numbers being compared across two implementations.
        ///
        /// <paramref name="biomeGameIndices"/> is <c>Heightmap.BiomeIndex</c> (None 0 .. Mistlands 9) or
        /// <see cref="Unsampled"/>; the 10,500 m edge is re-applied here so an oracle file and a
        /// generated field are cut to exactly the same cells.
        /// </summary>
        public void LoadFrom(byte[] biomeGameIndices, float[] height, double radius)
        {
            if (biomeGameIndices.Length != Biome.Length) throw new ArgumentException("biome array length does not match the grid.");
            if (height.Length != Height.Length) throw new ArgumentException("height array length does not match the grid.");

            Array.Copy(biomeGameIndices, Biome, Biome.Length);
            Array.Copy(height, Height, Height.Length);
            SampledRadius = Math.Min(radius, WaterEdge);
            HeightsSampled = true;

            long n = 0;
            for (int row = 0; row < _size; row++)
            {
                float wz = Grid.WorldZ(row);
                int b = row * _size;
                bool any = RowSpan(row, SampledRadius, out int lo, out int hi);
                for (int col = 0; col < _size; col++)
                {
                    bool inside = any && col >= lo && col < hi && DUtils.Length(Grid.WorldX(col), wz) <= 10500f;
                    if (!inside) Biome[b + col] = Unsampled;
                    else if (Biome[b + col] != Unsampled) n++;
                }
            }

            SampledCells = n;
        }
    }
}
