using System;
using SeedLab.Render;
using SeedLab.Search.Criteria;

namespace SeedLab.Search.Evaluation
{
    /// <summary>
    /// The sampling grid a query is measured on, to the one definition the whole tool shares
    /// (07-features.md section 2.1): <c>N = 2 * ceil(10500 / r)</c> samples per axis at
    /// <c>x_i = (i - N/2) * r + r/2</c>, with <c>r = 12</c> special-cased to <c>N = 2048</c> so that it
    /// reproduces the game's own <c>Minimap</c> / <c>AltBiomeWorldData</c> indexing point for point.
    ///
    /// <para>This is the same construction <c>SeedLab.Cli.Infra.Grids</c> uses for <c>vseed map</c> and
    /// <c>vseed seed</c>, so a hit from a search re-measures identically when the user inspects it. A
    /// coarser grid is a cost knob and a <i>different measurement</i>, never an approximation of G12.</para>
    /// </summary>
    public static class SearchGrids
    {
        public const double WorldRadiusM = 10500.0;

        public static FieldGrid ForSpacing(double spacing)
        {
            if (!(spacing > 0) || !double.IsFinite(spacing))
            {
                throw new QueryException("search.grid", "takes a positive spacing in metres, not '" + spacing + "'");
            }

            if (spacing == 12.0) return FieldGrid.G12;

            int n = 2 * (int)Math.Ceiling(WorldRadiusM / spacing);
            if (n < 2) n = 2;
            long cells = (long)n * n;
            if (cells > 400_000_000L)
            {
                throw new QueryException("search.grid",
                    spacing + " m would need " + n + "x" + n + " = " + cells.ToString("N0") + " samples per seed",
                    "use a coarser spacing");
            }

            return new FieldGrid(n, (float)spacing);
        }

        /// <summary>"G12 (2048 x 2048 @ 12 m, the grid the game itself samples)".</summary>
        public static string Describe(FieldGrid g)
        {
            string tag = "G" + g.Spacing.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string note = g.IsGameGrid ? ", the grid the game itself samples" : "";
            return tag + " (" + g.Size + " x " + g.Size + " @ "
                   + g.Spacing.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " m" + note + ")";
        }

        /// <summary>Bytes of per-worker buffer at this grid: biome + height + the flood-fill label array.</summary>
        public static long BytesPerWorker(FieldGrid g) => (long)g.Count * (1 + 4 + 4);
    }
}
