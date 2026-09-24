using System;
using SeedLab.Cli.Infra;
using SeedLab.Render;

namespace SeedLab.Cli.Analysis
{
    /// <summary>
    /// How much of a world summary survives the precision the game itself keeps.
    ///
    /// <para><c>Minimap.GenerateWorldMap</c> evaluates the height in float32 and then stores it through
    /// <c>Utils.FloatsToCompressedHalfBuffer</c> -> <c>Mathf.FloatToHalf</c>, i.e. binary16. Near the
    /// 30 m water line binary16 steps by 2^-6 = 0.015625 m, so a cell whose float32 height is within
    /// half of that (0.0078125 m) of 30.0 can land on the other side of the line once it is stored.
    /// Anything counted by thresholding at 30 m therefore has a precision-dependent part, and the size
    /// of that part is a property of the seed and the grid - not a constant.</para>
    ///
    /// <para>So it is measured per run rather than quoted: the whole field is re-quantised through the
    /// same encoder the self-test compares against (<see cref="UnityHalf.Bits"/>), the island analysis
    /// is re-run on it, and <c>vseed seed</c> prints the difference for the seed in front of it.</para>
    /// </summary>
    public sealed class HalfPrecisionProbe
    {
        private HalfPrecisionProbe() { }

        /// <summary>In-world cells examined.</summary>
        public long ComparedCells { get; private set; }

        /// <summary>In-world cells that change side of the 30 m line when the height is stored as binary16.</summary>
        public long FlippedCells { get; private set; }

        public int ComponentsAll { get; private set; }

        public int ComponentsAtLeastMin { get; private set; }

        public double LandAreaM2 { get; private set; }

        /// <summary>binary16 spacing at 30 m: the smallest height difference the cache can represent there.</summary>
        public const double UlpAt30M = 0.015625;

        public static HalfPrecisionProbe Compute(WorldField field, double minAreaM2)
        {
            FieldGrid g = field.Grid;
            float[] rounded = new float[g.Count];
            HalfPrecisionProbe p = new HalfPrecisionProbe();

            for (int k = 0; k < g.Count; k++)
            {
                float h = field.Height[k];
                float q = (float)BitConverter.UInt16BitsToHalf(UnityHalf.Bits(h));
                rounded[k] = q;
                if (field.Outside[k]) continue;
                p.ComparedCells++;
                if ((h >= MapPalette.WaterLevel) != (q >= MapPalette.WaterLevel)) p.FlippedCells++;
            }

            WorldField asStored = WorldField.FromSamples(field.Seed, field.WorldGenVersion, g,
                                                         field.BiomeIndices, rounded, field.Outside);
            IslandAnalysis isl = IslandAnalysis.Compute(asStored, minAreaM2, top: 1);
            p.ComponentsAll = isl.ComponentsAll;
            p.ComponentsAtLeastMin = isl.ComponentsAtLeastMin;
            p.LandAreaM2 = isl.LandAreaM2;
            return p;
        }
    }
}
