using System;
using SeedLab.Render;
using SeedLab.WorldGen;

namespace SeedLab.Cli.Analysis
{
    /// <summary>
    /// Every world-shape number <c>vseed seed</c> prints, measured on one stated grid.
    /// Nothing here is an estimate: each figure is a count over the cells of
    /// <see cref="WorldField"/>, and the grid it was counted on travels with it.
    /// </summary>
    public sealed class WorldSummary
    {
        public WorldField Field { get; private set; } = null!;
        public FieldGrid Grid => Field.Grid;

        /// <summary>Cells with <c>DUtils.Length(x,z) &lt;= 10500</c> - the ones the generator evaluates terrain for.</summary>
        public long InWorldCells { get; private set; }

        public long OutsideCells { get; private set; }

        public double InWorldAreaM2 => InWorldCells * Grid.CellArea;

        /// <summary>Per <c>Heightmap.BiomeIndex</c> (0 = None ... 9 = Mistlands), in-world cells only.</summary>
        public long[] BiomeCells { get; } = new long[10];

        /// <summary>Per biome, cells that are also land (height >= 30 m).</summary>
        public long[] BiomeLandCells { get; } = new long[10];

        /// <summary>Distance from (0,0) to the nearest in-world cell centre of each biome, metres.</summary>
        public double[] NearestBiomeM { get; } = new double[10];

        /// <summary>Same, restricted to land cells.</summary>
        public double[] NearestBiomeLandM { get; } = new double[10];

        public long LandCells { get; private set; }
        public long WaterCells { get; private set; }

        public double LandAreaM2 => LandCells * Grid.CellArea;
        public double WaterAreaM2 => WaterCells * Grid.CellArea;

        public float PeakHeight { get; private set; } = float.NegativeInfinity;
        public float PeakX { get; private set; }
        public float PeakZ { get; private set; }
        public Biome PeakBiome { get; private set; }

        public float DeepestHeight { get; private set; } = float.PositiveInfinity;
        public float DeepestX { get; private set; }
        public float DeepestZ { get; private set; }

        public IslandAnalysis Islands { get; private set; } = null!;

        /// <summary>The same island analysis re-run on binary16 heights - see <see cref="HalfPrecisionProbe"/>.</summary>
        public HalfPrecisionProbe HalfPrecision { get; private set; } = null!;

        // ---- the exact origin, evaluated off-grid --------------------------------------------------
        /// <summary>
        /// The generator's answer at exactly (0, 0). The grid never samples the origin - G12's nearest
        /// cell centre is (6, 6) - so this is asked of the generator directly.
        /// </summary>
        public Biome OriginBiome { get; private set; }

        public float OriginHeight { get; private set; }
        public float OriginForestFactor { get; private set; }

        public static WorldSummary Compute(WorldField field, double minIslandAreaM2 = IslandAnalysis.DefaultMinAreaM2)
        {
            WorldSummary s = new WorldSummary { Field = field };
            FieldGrid g = field.Grid;
            for (int i = 0; i < 10; i++)
            {
                s.NearestBiomeM[i] = double.PositiveInfinity;
                s.NearestBiomeLandM[i] = double.PositiveInfinity;
            }

            for (int row = 0; row < g.Size; row++)
            {
                float wz = g.WorldZ(row);
                int b = row * g.Size;
                for (int col = 0; col < g.Size; col++)
                {
                    int k = b + col;
                    if (field.Outside[k]) { s.OutsideCells++; continue; }

                    s.InWorldCells++;
                    float wx = g.WorldX(col);
                    float h = field.Height[k];
                    byte bi = field.BiomeIndices[k];
                    s.BiomeCells[bi]++;

                    bool land = h >= MapPalette.WaterLevel;
                    if (land) { s.LandCells++; s.BiomeLandCells[bi]++; } else s.WaterCells++;

                    double d = Math.Sqrt((double)wx * wx + (double)wz * wz);
                    if (d < s.NearestBiomeM[bi]) s.NearestBiomeM[bi] = d;
                    if (land && d < s.NearestBiomeLandM[bi]) s.NearestBiomeLandM[bi] = d;

                    if (h > s.PeakHeight)
                    {
                        s.PeakHeight = h; s.PeakX = wx; s.PeakZ = wz;
                        s.PeakBiome = ((BiomeIndex)bi).ToBiome();
                    }

                    if (h < s.DeepestHeight) { s.DeepestHeight = h; s.DeepestX = wx; s.DeepestZ = wz; }
                }
            }

            s.Islands = IslandAnalysis.Compute(field, minIslandAreaM2);
            s.HalfPrecision = HalfPrecisionProbe.Compute(field, minIslandAreaM2);

            WorldGeneratorPort gen = new WorldGeneratorPort(field.Seed, field.WorldGenVersion, menu: false);
            s.OriginBiome = gen.GetBiome(0f, 0f);
            s.OriginHeight = gen.GetBiomeHeight(s.OriginBiome, 0f, 0f, out _);
            s.OriginForestFactor = WorldGeneratorPort.GetForestFactor(0f, 0f, 0f);
            return s;
        }

        /// <summary>
        /// Compass bearing from the origin to (x, z), degrees clockwise from north (+z), which is the
        /// convention the in-game map and the wayfinder mods use.
        /// </summary>
        public static double BearingFromOrigin(double x, double z)
        {
            double deg = Math.Atan2(x, z) * 180.0 / Math.PI;
            return (deg % 360 + 360) % 360;
        }

        /// <summary>
        /// The worst-case error in a "nearest cell centre" distance: a target anywhere in a cell can be
        /// up to half a diagonal from that cell's centre.
        /// </summary>
        public double GridDistanceUncertaintyM => Grid.Spacing * Math.Sqrt(2.0) / 2.0;
    }
}
